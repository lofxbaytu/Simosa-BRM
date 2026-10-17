using System.Net.Sockets;
using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Sync;

public sealed class SyncOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = PackagingConfig.DefaultPort;
    /// <summary>教官金鑰(硬體金鑰或開發用 PEM;須在船上信任清單)</summary>
    public required IKeyProvider InstructorKey { get; init; }
    public required RecordStore Store { get; init; }
    /// <summary>要推送的更新包(依版本排序後推送)</summary>
    public List<string> PushPackages { get; init; } = new();
    /// <summary>未登錄的船上筆電裝置金鑰:首次登錄(TOFU,記入稽核)</summary>
    public bool EnrollDevice { get; init; }
    public bool PullRecords { get; init; } = true;
    public bool SendBackupReport { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public TextWriter? Log { get; init; }
}

/// <summary>同步結果(CLI 顯示與教官站的同步畫面)。</summary>
public sealed class SyncReport
{
    public string? ShipId { get; set; }
    public string? DeviceId { get; set; }
    public string? AuthenticatedAs { get; set; }
    public bool DeviceEnrolled { get; set; }
    public List<UpdateApplyResult> Updates { get; set; } = new();
    public List<PulledPackage> Pulled { get; set; } = new();
    public CleanupResult? Cleanup { get; set; }
    public ShipStatus? Status { get; set; }
    public List<string> Warnings { get; set; } = new();
    public bool Ok => Updates.All(u => u.Ok) && Pulled.All(p => p.Ok) && (Cleanup is null || Cleanup.Ok);
}

public sealed class PulledPackage
{
    public string PackageId { get; set; } = "";
    public bool Ok { get; set; }
    public bool AlreadyImported { get; set; }
    public string? Directory { get; set; }
    public int Exercises { get; set; }
    public double TrainingHours { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class SyncException : Exception
{
    public SyncException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

/// <summary>
/// 教官端直連同步(<c>simosa-pkg sync --ship &lt;ip&gt;</c>):握手(雙向驗證)→ 推送更新包(船端驗證套用並回報)→
/// 列出待回傳紀錄包 → 拉取並驗證匯入紀錄庫 → 送出已備份回報(只含已完成岸上備份者)→ 船端清理 → 更新兩船版次清單。
/// </summary>
public static class SyncClient
{
    public static async Task<SyncReport> SyncAsync(SyncOptions o, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(o.Timeout);
        ct = timeout.Token;
        var report = new SyncReport();
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(o.Host, o.Port, ct).ConfigureAwait(false);
        await using var s = tcp.GetStream();

        // 1. 握手
        var clientNonce = SyncProtocol.NewNonce();
        await SyncProtocol.WriteAsync(s, new HelloMessage { ClientNonce = clientNonce, Client = System.Environment.MachineName }, ct).ConfigureAwait(false);
        var ch = SyncProtocol.As<ChallengeMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.Challenge));
        if (!SyncProtocol.IsValidNonce(ch.Nonce)) throw new SyncException(SyncErrors.Protocol, "船端 nonce 不合法");
        report.ShipId = ch.ShipId;
        report.DeviceId = ch.DeviceId;
        var registered = o.Store.FindDevice(ch.ShipId, ch.DeviceId);
        TrustedKey device;
        if (registered is null)
        {
            if (!o.EnrollDevice) throw new SyncException(SyncErrors.UntrustedKey, $"船上筆電 {ch.ShipId}/{ch.DeviceId} 的裝置金鑰 {KeyFiles.Short(ch.DeviceKeyId)} 未登錄於紀錄庫(核對後以 --enroll-device 登錄)");
            device = TrustedKey.Parse(ch.DevicePublicKey, null, KeyRoles.Device);
        }
        else device = registered;
        using (device)
        {
            var env = new SignatureEnvelope { KeyId = ch.DeviceKeyId, Signature = ch.DeviceSignature };
            if (!env.VerifyWith(device.Key, SyncProtocol.DeviceAuthPayload(ch.ShipId, ch.DeviceId, clientNonce, ch.Nonce)))
                throw new SyncException(SyncErrors.AuthFailed, $"船上筆電 {ch.ShipId}/{ch.DeviceId} 的裝置簽章驗證失敗(不是登錄的筆電)");
            if (registered is null)
            {
                o.Store.RegisterDevice(ch.ShipId, ch.DeviceId, ch.DevicePublicKey).Dispose();
                o.Store.Audit.Write(AuditEvents.StoreDeviceEnrolled, new { ch.ShipId, ch.DeviceId, keyId = device.KeyId, via = "tcp", host = o.Host });
                report.DeviceEnrolled = true;
            }
        }
        var sig = o.InstructorKey.Sign(SyncProtocol.InstructorAuthPayload(ch.ShipId, ch.DeviceId, ch.Nonce, clientNonce));
        await SyncProtocol.WriteAsync(s, new AuthMessage { KeyId = o.InstructorKey.KeyId, Signer = o.InstructorKey.DisplayName, Signature = Convert.ToBase64String(sig) }, ct).ConfigureAwait(false);
        var welcome = SyncProtocol.As<WelcomeMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.Welcome));
        report.AuthenticatedAs = welcome.Signer;
        o.Log?.WriteLine($"已連線 {ch.ShipId}/{ch.DeviceId};船上確認簽章者:{welcome.Signer}(訪船 #{welcome.VisitSeq})");

        // 2. 推送更新包(依版本由小到大)
        var ordered = o.PushPackages.Select(p => (Path: p, Manifest: UpdatePackageReader.PeekManifest(p)))
            .OrderBy(p => PackageVersion.Parse(p.Manifest.Version)).ToList();
        foreach (var (path, m) in ordered)
        {
            await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.PushUpdate, Name = Path.GetFileName(path), PackageId = m.PackageId, Size = new FileInfo(path).Length, Sha256 = Sha256Hex.OfFile(path) }, ct).ConfigureAwait(false);
            await SyncProtocol.WriteFileBytesAsync(s, path, ct).ConfigureAwait(false);
            var res = SyncProtocol.As<UpdateResultMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.UpdateResult)).Result;
            report.Updates.Add(res);
            o.Log?.WriteLine($"  更新包 {m.Version}({m.Kind}):{res.Message}" + (res.Errors.Count > 0 ? " — " + string.Join(";", res.Errors) : ""));
        }

        // 3. 紀錄包
        var outstanding = new List<OutstandingPackage>();
        if (o.PullRecords || o.SendBackupReport)
        {
            await SyncProtocol.WriteAsync(s, new { type = SyncTypes.ListRecords }, ct).ConfigureAwait(false);
            var list = SyncProtocol.As<RecordListMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.RecordList));
            outstanding = list.Packages;
            report.Warnings.AddRange(list.Warnings);
        }
        if (o.PullRecords)
        {
            var incoming = Path.Combine(o.Store.Root, "incoming");
            foreach (var p in outstanding.Where(p => !o.Store.Has(p.PackageId, p.Sha256)))
            {
                await SyncProtocol.WriteAsync(s, new GetRecordMessage { PackageId = p.PackageId }, ct).ConfigureAwait(false);
                var hdrObj = await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false);
                var pulled = new PulledPackage { PackageId = p.PackageId };
                report.Pulled.Add(pulled);
                if (SyncProtocol.TypeOf(hdrObj) == SyncTypes.Error)
                {
                    pulled.Errors.Add(SyncProtocol.As<ErrorMessage>(hdrObj).Message);
                    continue;
                }
                var hdr = SyncProtocol.As<FileHeader>(Expect(hdrObj, SyncTypes.File));
                var tmp = Path.Combine(incoming, $"{Guid.NewGuid():N}.sbrec");
                try
                {
                    var sha = await SyncProtocol.ReadFileBytesAsync(s, hdr.Size, tmp, SyncProtocol.DefaultMaxFileBytes, ct).ConfigureAwait(false);
                    if (!Sha256Hex.Equal(sha, hdr.Sha256) || !Sha256Hex.Equal(sha, p.Sha256))
                    {
                        pulled.Errors.Add("傳輸後雜湊不符");
                        continue;
                    }
                    var imp = o.Store.Import(tmp, enrollUnknownDevice: false);
                    pulled.Ok = imp.Ok;
                    pulled.AlreadyImported = imp.AlreadyImported;
                    pulled.Directory = imp.Directory;
                    pulled.Exercises = imp.Manifest?.Exercises.Count ?? 0;
                    pulled.TrainingHours = imp.Manifest?.TrainingHours.TotalHours ?? 0;
                    pulled.Errors.AddRange(imp.Errors);
                    o.Log?.WriteLine($"  紀錄包 {p.PackageId}:" + (imp.Ok ? $"已匯入 {imp.Directory}({pulled.Exercises} 個練習,{pulled.TrainingHours} 小時)" : string.Join(";", imp.Errors)));
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            }
        }

        // 4. 已備份回報 → 船端清理
        if (o.SendBackupReport && outstanding.Count > 0)
        {
            var created = o.Store.CreateBackupReport(ch.ShipId, o.InstructorKey, outstanding.Select(p => p.PackageId));
            if (created is { } c)
            {
                var bytes = c.Document.ToBytes();
                await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.BackupReport, Name = c.Report.ReportId + ".sbbak", Size = bytes.Length, Sha256 = Sha256Hex.Of(bytes) }, ct).ConfigureAwait(false);
                await s.WriteAsync(bytes, ct).ConfigureAwait(false);
                await s.FlushAsync(ct).ConfigureAwait(false);
                var res = SyncProtocol.As<CleanupResultMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.CleanupResult)).Result;
                report.Cleanup = res;
                if (res.Ok) o.Store.MarkShipDeleted(c.Report.ReportId, res.Deleted);
                report.Warnings.AddRange(res.Warnings);
                o.Log?.WriteLine($"  已備份回報 {c.Report.ReportId}:" + (res.Ok ? $"船上刪除 {res.Deleted.Count} 個紀錄包,保留 {res.Retained.Count} 個" : string.Join(";", res.Errors)));
            }
        }

        // 5. 狀態 → 兩船版次清單
        await SyncProtocol.WriteAsync(s, new { type = SyncTypes.Status }, ct).ConfigureAwait(false);
        var status = SyncProtocol.As<ShipStatus>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.StatusResult));
        report.Status = status;
        o.Store.UpdateFleet(new ShipVersionEntry
        {
            ShipId = ch.ShipId, DeviceId = ch.DeviceId, Version = status.Version, PackageId = status.PackageId, Components = status.Components,
            ParameterSet = status.ParameterSet, Via = "tcp",
        });
        await SyncProtocol.WriteAsync(s, new { type = SyncTypes.Bye }, ct).ConfigureAwait(false);
        try { await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or EndOfStreamException) { }
        return report;
    }

    private static JsonObject Expect(JsonObject msg, string type)
    {
        var t = SyncProtocol.TypeOf(msg);
        if (t == SyncTypes.Error)
        {
            var e = SyncProtocol.As<ErrorMessage>(msg);
            throw new SyncException(e.Code, $"船端拒絕:{e.Message}");
        }
        if (t != type) throw new SyncException(SyncErrors.Protocol, $"預期 {type},收到 {t}");
        return msg;
    }
}
