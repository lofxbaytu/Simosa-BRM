using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Sync;

/// <summary>
/// 船端直連同步服務(<c>simosa-pkg serve</c>)。一次只服務一個連線;握手:
/// hello(教官 nonce)→ challenge(船端 nonce + 裝置簽章)→ auth(教官金鑰簽章船端 nonce;須在信任清單)→ welcome。
/// 之後處理 status / pushUpdate / listRecords / getRecord / backupReport / bye。驗證失敗回 error 並斷線、記入稽核。
/// 驗證前的防護:hello/auth 訊息上限 4 KiB、握手逾時;稽核只記錄截斷/格式合法的對方欄位;未驗證的失敗在
/// <see cref="AuthFailureWindow"/> 內超過 <see cref="AuthFailureAuditLimit"/> 次後只計數(sync.auth.throttled)並延遲斷線,
/// 避免任何能連到埠的人灌爆稽核紀錄與磁碟。
/// </summary>
public sealed class SyncServer : IAsyncDisposable
{
    private readonly ShipNode _ship;
    private readonly TcpListener _listener;
    private readonly TextWriter? _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SyncServer(ShipNode ship, IPAddress address, int port, TextWriter? log = null)
    {
        _ship = ship;
        _listener = new TcpListener(address, port);
        _log = log;
    }

    /// <summary>單一訊息的讀取逾時(握手與閒置)</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public long MaxFileBytes { get; init; } = SyncProtocol.DefaultMaxFileBytes;
    /// <summary>未驗證失敗超過上限後,每次失敗結束連線前的延遲(限制嘗試速率;測試可設 0)</summary>
    public TimeSpan AuthFailureBackoff { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>握手(hello → auth)每則訊息的讀取逾時(教官端簽 nonce 時硬體金鑰可能要求輸入 PIN,不宜太短)</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>時間窗內逐筆記入稽核的未驗證失敗次數上限</summary>
    public int AuthFailureAuditLimit { get; init; } = 10;
    public TimeSpan AuthFailureWindow { get; init; } = TimeSpan.FromMinutes(1);

    private readonly object _failGate = new();
    private readonly Queue<DateTime> _failures = new();
    private int _suppressed;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start(1);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public async Task RunUntilCancelledAsync(CancellationToken ct)
    {
        Start();
        try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await DisposeAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            using (client)
            {
                try { await HandleAsync(client, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // 單一連線的任何錯誤都不終止服務(下一位教官仍可連線)
                    _log?.WriteLine($"[serve] 連線結束:{ex.Message}");
                    if (ex is not (IOException or OperationCanceledException or EndOfStreamException or SocketException))
                        _ship.Layout.Audit(_ship.Clock).Write(AuditEvents.SyncSessionError, new { error = ex.GetType().Name, message = Clip(ex.Message) });
                }
            }
        }
    }

    private async Task<JsonObject> ReadWithTimeout(Stream s, CancellationToken ct, int maxBytes = SyncProtocol.MaxMessageBytes, TimeSpan? timeout = null)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(timeout ?? IdleTimeout);
        return await SyncProtocol.ReadAsync(s, t.Token, maxBytes).ConfigureAwait(false);
    }

    private Task<JsonObject> ReadHandshake(Stream s, CancellationToken ct) => ReadWithTimeout(s, ct, SyncProtocol.HandshakeMaxMessageBytes, HandshakeTimeout);

    /// <summary>對方提供、未驗證的字串:去除控制字元並截斷(稽核用)。</summary>
    internal static string? Clip(string? text, int max = 128)
    {
        if (text is null) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).Take(max + 1).ToArray());
        return clean.Length > max ? clean[..max] + "…" : clean;
    }

    /// <summary>對方提供的金鑰 ID:只有 64 位 hex 才原樣記錄。</summary>
    internal static string SafeKeyId(string? keyId)
        => keyId is { Length: 64 } && keyId.All(char.IsAsciiHexDigit) ? keyId.ToLowerInvariant() : "(invalid)";

    /// <summary>記錄一次未驗證的失敗(速率上限內逐筆記錄,超過只計數);回傳 true 表示已超過上限(呼叫端應延遲斷線)。</summary>
    private bool RecordAuthFailure(AuditLog audit, string? peer, string reason, string? keyId, string? claimedSigner, string? signer = null, string? error = null)
    {
        lock (_failGate)
        {
            var now = _ship.Clock.GetUtcNow().UtcDateTime;
            while (_failures.Count > 0 && now - _failures.Peek() > AuthFailureWindow) _failures.Dequeue();
            if (_failures.Count == 0) FlushThrottled(audit);
            _failures.Enqueue(now);
            if (_failures.Count <= AuthFailureAuditLimit)
            {
                audit.Write(AuditEvents.SyncAuthFailed, new { peer, keyId = keyId is null ? null : SafeKeyId(keyId), claimedSigner = Clip(claimedSigner), signer, reason, error = Clip(error, 256) });
                return false;
            }
            if (_suppressed++ == 0)
                audit.Write(AuditEvents.SyncAuthThrottled, new { phase = "start", peer, limit = AuthFailureAuditLimit, windowS = AuthFailureWindow.TotalSeconds });
            return true;
        }
    }

    /// <summary>結束一段只計數的期間:寫一筆彙總(被略過的次數)。須在 _failGate 內呼叫。</summary>
    private void FlushThrottled(AuditLog audit)
    {
        if (_suppressed == 0) return;
        audit.Write(AuditEvents.SyncAuthThrottled, new { phase = "end", suppressed = _suppressed });
        _suppressed = 0;
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var peer = client.Client.RemoteEndPoint?.ToString();
        var audit = _ship.Layout.Audit(_ship.Clock);
        await using var stream = client.GetStream();
        (string Desc, string Role, string DeviceId)? authed;
        try
        {
            authed = await HandshakeAsync(stream, peer, audit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            // 格式錯誤、訊息過大等(驗證前):計入失敗速率,不把對方內容寫入稽核
            if (RecordAuthFailure(audit, peer, "protocol", null, null, error: ex.Message)) await Backoff(ct).ConfigureAwait(false);
            try { await Fail(stream, SyncErrors.Protocol, "握手訊息不合法", ct).ConfigureAwait(false); }
            catch (Exception wex) when (wex is IOException or SocketException or ObjectDisposedException) { }
            return;
        }
        if (authed is not { } a) return;
        lock (_failGate) FlushThrottled(audit);
        var (signerDesc, signerRole, deviceId) = a;
        await SessionAsync(stream, peer, audit, signerDesc, signerRole, deviceId, ct).ConfigureAwait(false);
    }

    private async Task Backoff(CancellationToken ct)
    {
        if (AuthFailureBackoff > TimeSpan.Zero) await Task.Delay(AuthFailureBackoff, ct).ConfigureAwait(false);
    }

    /// <summary>握手;驗證失敗時已回 error 並記錄,回傳 null。</summary>
    private async Task<(string Desc, string Role, string DeviceId)?> HandshakeAsync(Stream stream, string? peer, AuditLog audit, CancellationToken ct)
    {
        // 1. hello
        var hello = await ReadHandshake(stream, ct).ConfigureAwait(false);
        if (SyncProtocol.TypeOf(hello) != SyncTypes.Hello)
        {
            await Fail(stream, SyncErrors.Protocol, "須先送 hello", ct).ConfigureAwait(false);
            return null;
        }
        var h = SyncProtocol.As<HelloMessage>(hello);
        if (h.Protocol != SyncProtocol.Version || h.Role != "instructor" || !SyncProtocol.IsValidNonce(h.ClientNonce))
        {
            await Fail(stream, SyncErrors.Protocol, $"不支援的協定版本或角色(protocol {h.Protocol}, role {Clip(h.Role, 32)})", ct).ConfigureAwait(false);
            return null;
        }
        // 2. challenge(船端 nonce + 裝置簽章教官 nonce)
        var (info, deviceKey) = DeviceKeyStore.Load(_ship.Layout);
        string nonce;
        using (deviceKey)
        {
            nonce = SyncProtocol.NewNonce();
            var shipId = _ship.ShipId;
            await SyncProtocol.WriteAsync(stream, new ChallengeMessage
            {
                ShipId = shipId, DeviceId = info.DeviceId, Nonce = nonce, DeviceKeyId = deviceKey.KeyId, DevicePublicKey = deviceKey.PublicKeyPem,
                DeviceSignature = Convert.ToBase64String(deviceKey.Sign(SyncProtocol.DeviceAuthPayload(shipId, info.DeviceId, h.ClientNonce, nonce))),
            }, ct).ConfigureAwait(false);
        }
        // 3. auth
        var authObj = await ReadHandshake(stream, ct).ConfigureAwait(false);
        if (SyncProtocol.TypeOf(authObj) != SyncTypes.Auth)
        {
            await Fail(stream, SyncErrors.Protocol, "須送 auth", ct).ConfigureAwait(false);
            return null;
        }
        var auth = SyncProtocol.As<AuthMessage>(authObj);
        using (var trust = TrustStore.LoadDirectory(_ship.Layout.TrustedKeysDir))
        {
            var key = auth.KeyId is { Length: 64 } ? trust.Find(auth.KeyId) : null;
            if (key is null || !KeyRoles.IsSigner(key.Role))
            {
                if (RecordAuthFailure(audit, peer, "untrusted-key", auth.KeyId ?? "", auth.Signer)) await Backoff(ct).ConfigureAwait(false);
                await Fail(stream, SyncErrors.UntrustedKey, $"金鑰 {KeyFiles.Short(SafeKeyId(auth.KeyId))} 不在船上信任清單", ct).ConfigureAwait(false);
                return null;
            }
            var env = new SignatureEnvelope { KeyId = auth.KeyId, Signature = auth.Signature ?? "" };
            if (!env.VerifyWith(key.Key, SyncProtocol.InstructorAuthPayload(_ship.ShipId, info.DeviceId, nonce, h.ClientNonce)))
            {
                if (RecordAuthFailure(audit, peer, "bad-signature", auth.KeyId, null, signer: key.Describe())) await Backoff(ct).ConfigureAwait(false);
                await Fail(stream, SyncErrors.AuthFailed, "nonce 簽章驗證失敗", ct).ConfigureAwait(false);
                return null;
            }
            return (key.Describe(), key.Role, info.DeviceId);
        }
    }

    private async Task SessionAsync(Stream stream, string? peer, AuditLog audit, string signerDesc, string signerRole, string deviceId, CancellationToken ct)
    {
        var visit = _ship.Records.BeginVisit("tcp", peer);
        audit.Write(AuditEvents.SyncSessionStart, new { peer, signer = signerDesc, visit });
        _log?.WriteLine($"[serve] {peer} 已驗證:{signerDesc}(訪船 #{visit})");
        await SyncProtocol.WriteAsync(stream, new WelcomeMessage { ShipId = _ship.ShipId, DeviceId = deviceId, Signer = signerDesc, SignerRole = signerRole, VisitSeq = visit }, ct).ConfigureAwait(false);

        // 4. 指令迴圈
        var stats = new { updates = new List<object>(), pulled = new List<string>(), cleanup = new List<string>() };
        while (true)
        {
            var msg = await ReadWithTimeout(stream, ct).ConfigureAwait(false);
            var type = SyncProtocol.TypeOf(msg);
            switch (type)
            {
                case SyncTypes.Status:
                    await SyncProtocol.WriteAsync(stream, _ship.Status(), ct).ConfigureAwait(false);
                    break;
                case SyncTypes.PushUpdate:
                {
                    var hdr = SyncProtocol.As<FileHeader>(msg);
                    Directory.CreateDirectory(_ship.Layout.IncomingDir);
                    var tmp = Path.Combine(_ship.Layout.IncomingDir, $"push-{Guid.NewGuid():N}.sbupd");
                    try
                    {
                        var sha = await SyncProtocol.ReadFileBytesAsync(stream, hdr.Size, tmp, MaxFileBytes, ct).ConfigureAwait(false);
                        UpdateApplyResult result;
                        if (!Sha256Hex.Equal(sha, hdr.Sha256))
                            result = new UpdateApplyResult { Errors = { "傳輸後雜湊不符" }, Message = "傳輸錯誤,未套用" };
                        else
                            result = _ship.Updates.Apply(tmp, new UpdateApplyOptions { Via = "tcp", Operator = signerDesc });
                        stats.updates.Add(new { result.PackageId, result.Version, result.Ok });
                        _log?.WriteLine($"[serve] 更新包 {hdr.Name}:{result.Message}");
                        await SyncProtocol.WriteAsync(stream, new UpdateResultMessage { Result = result }, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    break;
                }
                case SyncTypes.ListRecords:
                {
                    var warnings = new List<string>();
                    try { _ship.Records.PackPending(); }
                    catch (IOException ex) { warnings.Add(ex.Message); }
                    // 保留上限警告另列:教官端若接著送已備份回報,改用清理後(cleanupResult)的警告,避免把即將清理的紀錄包也列為超限
                    await SyncProtocol.WriteAsync(stream, new RecordListMessage { Packages = _ship.Records.Outstanding(), Warnings = warnings, RetentionWarnings = _ship.Records.RetentionWarnings() }, ct).ConfigureAwait(false);
                    break;
                }
                case SyncTypes.GetRecord:
                {
                    var req = SyncProtocol.As<GetRecordMessage>(msg);
                    var path = _ship.Records.PackagePath(req.PackageId);
                    if (path is null)
                    {
                        await SyncProtocol.WriteAsync(stream, new ErrorMessage { Code = SyncErrors.NotFound, Message = $"找不到紀錄包 {req.PackageId}" }, ct).ConfigureAwait(false);
                        break;
                    }
                    await SyncProtocol.WriteAsync(stream, new FileHeader { Name = Path.GetFileName(path), PackageId = req.PackageId, Size = new FileInfo(path).Length, Sha256 = Sha256Hex.OfFile(path) }, ct).ConfigureAwait(false);
                    await SyncProtocol.WriteFileBytesAsync(stream, path, ct).ConfigureAwait(false);
                    _ship.Records.MarkPulled(req.PackageId, peer);
                    stats.pulled.Add(req.PackageId);
                    break;
                }
                case SyncTypes.BackupReport:
                {
                    var hdr = SyncProtocol.As<FileHeader>(msg);
                    if (hdr.Size > 4 << 20) throw new InvalidDataException("回報過大");
                    var tmp = Path.Combine(_ship.Layout.IncomingDir, $"report-{Guid.NewGuid():N}.sbbak");
                    try
                    {
                        var sha = await SyncProtocol.ReadFileBytesAsync(stream, hdr.Size, tmp, 4 << 20, ct).ConfigureAwait(false);
                        var result = Sha256Hex.Equal(sha, hdr.Sha256)
                            ? _ship.Records.AcceptBackupReport(await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false))
                            : new CleanupResult { Errors = { "傳輸後雜湊不符" } };
                        stats.cleanup.AddRange(result.Deleted);
                        await SyncProtocol.WriteAsync(stream, new CleanupResultMessage { Result = result }, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    break;
                }
                case SyncTypes.Bye:
                    await SyncProtocol.WriteAsync(stream, new { type = SyncTypes.Bye }, ct).ConfigureAwait(false);
                    audit.Write(AuditEvents.SyncSessionEnd, new { peer, signer = signerDesc, visit, stats.updates, stats.pulled, deleted = stats.cleanup });
                    return;
                default:
                    await SyncProtocol.WriteAsync(stream, new ErrorMessage { Code = SyncErrors.UnknownType, Message = $"未知的訊息 {type}" }, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task Fail(Stream s, string code, string message, CancellationToken ct)
        => await SyncProtocol.WriteAsync(s, new ErrorMessage { Code = code, Message = message }, ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        try
        {
            lock (_failGate) FlushThrottled(_ship.Layout.Audit(_ship.Clock));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
    }
}
