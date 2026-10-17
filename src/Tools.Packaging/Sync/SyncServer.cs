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
                        _ship.Layout.Audit(_ship.Clock).Write("sync.session.error", new { error = ex.GetType().Name, message = ex.Message });
                }
            }
        }
    }

    private async Task<JsonObject> ReadWithTimeout(Stream s, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(IdleTimeout);
        return await SyncProtocol.ReadAsync(s, t.Token).ConfigureAwait(false);
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var peer = client.Client.RemoteEndPoint?.ToString();
        var audit = _ship.Layout.Audit(_ship.Clock);
        await using var stream = client.GetStream();
        // 1. hello
        var hello = await ReadWithTimeout(stream, ct).ConfigureAwait(false);
        if (SyncProtocol.TypeOf(hello) != SyncTypes.Hello)
        {
            await Fail(stream, SyncErrors.Protocol, "須先送 hello", ct).ConfigureAwait(false);
            return;
        }
        var h = SyncProtocol.As<HelloMessage>(hello);
        if (h.Protocol != SyncProtocol.Version || h.Role != "instructor" || !SyncProtocol.IsValidNonce(h.ClientNonce))
        {
            await Fail(stream, SyncErrors.Protocol, $"不支援的協定版本或角色(protocol {h.Protocol}, role {h.Role})", ct).ConfigureAwait(false);
            return;
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
        var authObj = await ReadWithTimeout(stream, ct).ConfigureAwait(false);
        if (SyncProtocol.TypeOf(authObj) != SyncTypes.Auth)
        {
            await Fail(stream, SyncErrors.Protocol, "須送 auth", ct).ConfigureAwait(false);
            return;
        }
        var auth = SyncProtocol.As<AuthMessage>(authObj);
        string signerDesc, signerRole;
        using (var trust = TrustStore.LoadDirectory(_ship.Layout.TrustedKeysDir))
        {
            var key = trust.Find(auth.KeyId);
            if (key is null || !KeyRoles.IsSigner(key.Role))
            {
                audit.Write(AuditEvents.SyncAuthFailed, new { peer, keyId = auth.KeyId, claimedSigner = auth.Signer, reason = "untrusted-key" });
                await Fail(stream, SyncErrors.UntrustedKey, $"金鑰 {KeyFiles.Short(auth.KeyId)} 不在船上信任清單", ct).ConfigureAwait(false);
                return;
            }
            var env = new SignatureEnvelope { KeyId = auth.KeyId, Signature = auth.Signature };
            if (!env.VerifyWith(key.Key, SyncProtocol.InstructorAuthPayload(_ship.ShipId, info.DeviceId, nonce, h.ClientNonce)))
            {
                audit.Write(AuditEvents.SyncAuthFailed, new { peer, keyId = auth.KeyId, signer = key.Describe(), reason = "bad-signature" });
                await Fail(stream, SyncErrors.AuthFailed, "nonce 簽章驗證失敗", ct).ConfigureAwait(false);
                return;
            }
            signerDesc = key.Describe();
            signerRole = key.Role;
        }
        var visit = _ship.Records.BeginVisit("tcp", peer);
        audit.Write(AuditEvents.SyncSessionStart, new { peer, signer = signerDesc, visit });
        _log?.WriteLine($"[serve] {peer} 已驗證:{signerDesc}(訪船 #{visit})");
        await SyncProtocol.WriteAsync(stream, new WelcomeMessage { ShipId = _ship.ShipId, DeviceId = info.DeviceId, Signer = signerDesc, SignerRole = signerRole, VisitSeq = visit }, ct).ConfigureAwait(false);

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
                    warnings.AddRange(_ship.Records.RetentionWarnings());
                    await SyncProtocol.WriteAsync(stream, new RecordListMessage { Packages = _ship.Records.Outstanding(), Warnings = warnings }, ct).ConfigureAwait(false);
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
