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
    /// <summary>
    /// 船上回報的發行版狀態(發行版本、降版提醒;沒有回報、讀不懂也在這裡分清楚)。<see cref="Status"/> 的 ReleaseVersion / ReleaseDowngrade
    /// 是同一份通過檢查的值。降版只是提醒,不影響 <see cref="Ok"/>(結束碼不變)。
    /// </summary>
    public ShipReleaseReport? Release { get; set; }
    /// <summary>
    /// 船上的狀態有沒有寫進兩船版次清單。最後一步失敗(狀態的其餘欄位讀不進來、fleet-versions.json 讀不進來或寫不進去)時是 false,
    /// 而 <see cref="Release"/> 可能已經有內容 — 呼叫端傳入自己的 <see cref="SyncReport"/> 就能在例外之後照樣印出這次收到的降版提醒。
    /// </summary>
    public bool FleetSaved { get; set; }
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
/// <para>
/// 船端送來的文字(握手時自稱的船舶 / 裝置識別、簽章者描述、更新包結果、警告、錯誤訊息)寫到 <see cref="SyncOptions.Log"/> 或放進例外訊息之前
/// 都先經 <see cref="UntrustedText.Clean"/>(去控制 / 格式字元並截斷;合法的內容不變):握手之後的訊息沒有各自的簽章,
/// 一則帶 ANSI 跳脫序列的警告可以把終端機上剛印出的降版提醒清掉。<see cref="SyncReport.Warnings"/> 保留原文,由顯示的一方清理。
/// </para>
/// </summary>
public static class SyncClient
{
    /// <param name="report">
    /// 要填寫的結果(預設新建)。呼叫端自己建立並傳入時,同步中途丟例外也看得到已經完成的部分 —
    /// CLI 靠它在最後一步失敗時仍印出船上回報的發行版與降版提醒(<see cref="SyncReport.FleetSaved"/>)。
    /// </param>
    public static async Task<SyncReport> SyncAsync(SyncOptions o, CancellationToken ct = default, SyncReport? report = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(o.Timeout);
        ct = timeout.Token;
        report ??= new SyncReport();
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
        o.Log?.WriteLine($"已連線 {Shown(ch.ShipId)}/{Shown(ch.DeviceId)};船上確認簽章者:{Shown(welcome.Signer, 200)}(訪船 #{welcome.VisitSeq})");

        // 2. 推送更新包(依版本由小到大)
        var ordered = o.PushPackages.Select(p => (Path: p, Manifest: UpdatePackageReader.PeekManifest(p)))
            .OrderBy(p => PackageVersion.Parse(p.Manifest.Version)).ToList();
        foreach (var (path, m) in ordered)
        {
            await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.PushUpdate, Name = Path.GetFileName(path), PackageId = m.PackageId, Size = new FileInfo(path).Length, Sha256 = Sha256Hex.OfFile(path) }, ct).ConfigureAwait(false);
            await SyncProtocol.WriteFileBytesAsync(s, path, ct).ConfigureAwait(false);
            var res = SyncProtocol.As<UpdateResultMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.UpdateResult)).Result;
            report.Updates.Add(res);
            o.Log?.WriteLine($"  更新包 {m.Version}({m.Kind}):{Shown(res.Message, 400)}" + (res.Errors.Count > 0 ? " — " + Shown(string.Join(";", res.Errors), 800) : ""));
        }

        // 3. 紀錄包
        var outstanding = new List<OutstandingPackage>();
        var retentionWarnings = new List<string>();
        if (o.PullRecords || o.SendBackupReport)
        {
            await SyncProtocol.WriteAsync(s, new { type = SyncTypes.ListRecords }, ct).ConfigureAwait(false);
            var list = SyncProtocol.As<RecordListMessage>(Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.RecordList));
            outstanding = list.Packages;
            report.Warnings.AddRange(list.Warnings);
            retentionWarnings = list.RetentionWarnings;
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
                    o.Log?.WriteLine($"  紀錄包 {Shown(p.PackageId, 128)}:" + (imp.Ok ? $"已匯入 {imp.Directory}({pulled.Exercises} 個練習,{pulled.TrainingHours} 小時)" : Shown(string.Join(";", imp.Errors), 800)));
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
                if (res.Ok)
                {
                    o.Store.MarkShipDeleted(c.Report.ReportId, res.Deleted, ch.ShipId);
                    retentionWarnings = res.Warnings; // 清理後的保留上限警告(及無法刪除的檔案)取代清理前的
                }
                else report.Warnings.AddRange(res.Warnings);
                o.Log?.WriteLine($"  已備份回報 {c.Report.ReportId}:" + (res.Ok ? $"船上刪除 {res.Deleted.Count} 個紀錄包,保留 {res.Retained.Count} 個" : Shown(string.Join(";", res.Errors), 800)));
            }
        }

        report.Warnings.AddRange(retentionWarnings);

        // 5. 狀態 → 兩船版次清單
        await SyncProtocol.WriteAsync(s, new { type = SyncTypes.Status }, ct).ConfigureAwait(false);
        JsonObject statusMessage;
        try
        {
            statusMessage = Expect(await SyncProtocol.ReadAsync(s, ct).ConfigureAwait(false), SyncTypes.StatusResult);
        }
        // 頂層有重複的鍵等(JsonNode 到存取時才檢查)。整則不是有效的 JSON 是 InvalidDataException、連線中斷是 IOException / ObjectDisposedException:照舊
        catch (Exception ex) when (ex is not ObjectDisposedException && ShipReleaseReport.IsJsonAccessError(ex))
        {
            throw StatusUnreadable(ex);
        }
        // 發行版本與降版資訊:statusResult 沒有自己的簽章(來源只由握手時的裝置金鑰簽章與同一條連線保證),內容當成不可信的輸入。
        // 這兩欄先寬鬆讀取(型別、格式不對只記為「讀不懂」)再從訊息拿掉,其餘欄位照舊解析 — 它們的型別不對不會讓同步在最後一步失敗
        var release = ShipReleaseReport.Read(statusMessage);
        // 先交給呼叫端,再做可能失敗的事(其餘欄位的解析、寫兩船版次清單):失敗時這次收到的降版提醒仍印得出來,不跟著存檔一起不見
        report.Release = release;
        ShipReleaseReport.RemoveFields(statusMessage);
        ShipStatus status;
        try
        {
            status = SyncProtocol.As<ShipStatus>(statusMessage) with { ReleaseVersion = release.ReleaseVersion, ReleaseDowngrade = release.Downgrade };
        }
        catch (Exception ex) when (ex is InvalidDataException || ShipReleaseReport.IsJsonAccessError(ex))
        {
            throw StatusUnreadable(ex);
        }
        report.Status = status;
        var entry = new ShipVersionEntry
        {
            ShipId = ch.ShipId, DeviceId = ch.DeviceId, Version = status.Version, PackageId = status.PackageId, Components = status.Components,
            ParameterSet = status.ParameterSet, Environment = status.Environment, Via = "tcp",
        };
        release.ApplyTo(entry);
        o.Store.UpdateFleet(entry);   // 直連拿到的是當下的狀態:一律取代(隨身碟匯入才比新舊)
        report.FleetSaved = true;
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
            throw new SyncException(e.Code, $"船端拒絕:{Shown(e.Message, 400)}");
        }
        if (t != type) throw new SyncException(SyncErrors.Protocol, $"預期 {type},收到 {Shown(t, 32)}");
        return msg;
    }

    /// <summary>船端送來的文字寫到紀錄或例外訊息之前:去掉控制 / 格式字元並截斷(合法的內容不變)。</summary>
    private static string Shown(string? text, int max = 64) => UntrustedText.Clean(text, max);

    /// <summary>
    /// statusResult 這兩欄以外的內容讀不進來(型別不對、重複的鍵、不合法的 UTF-8):與原本一樣以錯誤結束(CLI 結束碼 1,兩船版次清單沒有更新),
    /// 但錯誤訊息是固定的中文說明加上清理過的細節 — .NET 的例外訊息會帶出對方寫的鍵名(可以夾 ESC、換行、雙向控制字元)。
    /// </summary>
    private static SyncException StatusUnreadable(Exception ex)
        => new(SyncErrors.Protocol, $"船上回報的狀態(statusResult)有教官筆電讀不懂的內容,兩船版次清單沒有更新:{Shown(ex.Message, 300)}");
}
