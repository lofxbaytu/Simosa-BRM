using System.Text.Json;
using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Transfer;

public sealed class UsbReport
{
    public List<string> Written { get; set; } = new();
    public List<UpdateApplyResult> Updates { get; set; } = new();
    public List<ImportResult> Imported { get; set; } = new();
    public CleanupResult? Cleanup { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    /// <summary>教官端匯入:隨身碟上各船 status.json 的發行版狀態(已寫進兩船版次清單的船;CLI 逐船印一行摘要與降版提醒)</summary>
    public List<UsbShipRelease> ShipReleases { get; set; } = new();
}

/// <summary>
/// 隨身碟上一艘船的 status.json 所回報的發行版狀態(船舶與裝置識別是 status.json 自己寫的,沒有簽章;船舶識別已與所在的資料夾名稱核對)。
/// </summary>
public sealed record UsbShipRelease(string ShipId, string? DeviceId, ShipReleaseReport Release)
{
    /// <summary>船上產生這份 status.json 的時間(船上的時鐘;<see cref="RecordStore.StatusUtcOf"/>);沒有或不合理時 null</summary>
    public DateTime? StatusUtc { get; init; }
    /// <summary>
    /// 寫進兩船版次清單的結果:<see cref="FleetUpdate.Saved"/> false = 比教官筆電已有的舊,沒有採用(CLI 只印一行說明,不印它的提醒);
    /// null = 沒有存成(fleet-versions.json 讀不進來或寫不進去,原因在 <see cref="UsbReport.Warnings"/> / <see cref="UsbReport.Errors"/>)—
    /// 這時 status.json 裡的發行版與降版提醒仍照常印出,不跟著存檔一起不見。
    /// </summary>
    public FleetUpdate? Fleet { get; set; }
}

/// <summary>
/// 專用隨身碟備援(網路線直連不可用時;規劃書第 5.5 節)。隨身碟上只放已簽章的檔案:
/// <code>
/// &lt;dir&gt;/simosa-transfer/to-ship/*.sbupd                 教官 → 船:更新包
/// &lt;dir&gt;/simosa-transfer/to-ship/&lt;ship&gt;/*.sbbak         教官 → 船:已備份回報
/// &lt;dir&gt;/simosa-transfer/from-ship/&lt;ship&gt;/*.sbrec       船 → 教官:紀錄包
/// &lt;dir&gt;/simosa-transfer/from-ship/&lt;ship&gt;/*.sbcln       船 → 教官:清理通知(處理已備份回報後;裝置金鑰簽章)
/// &lt;dir&gt;/simosa-transfer/from-ship/&lt;ship&gt;/status.json   船 → 教官:版次、發行版本與降版資訊(資訊用,未簽章)
/// </code>
/// 船端套用與教官端匯入時都完整驗證簽章,與直連相同;清理通知讓教官紀錄庫標記「船上已刪除」,不再重複產生回報。
/// </summary>
public static class UsbTransfer
{
    public const string RootName = "simosa-transfer";

    private static string ToShip(string dir) => Path.Combine(dir, RootName, "to-ship");
    private static string FromShip(string dir) => Path.Combine(dir, RootName, "from-ship");

    /// <summary>教官端:把更新包與各船的已備份回報寫到隨身碟。</summary>
    public static UsbReport InstructorExport(RecordStore store, string toDir, IEnumerable<string> updatePackages, IKeyProvider? reportKey, IEnumerable<string>? ships = null)
    {
        var rep = new UsbReport();
        var dest = ToShip(toDir);
        Directory.CreateDirectory(dest);
        foreach (var pkg in updatePackages)
        {
            var target = Path.Combine(dest, Path.GetFileName(pkg));
            File.Copy(pkg, target, overwrite: true);
            rep.Written.Add(target);
        }
        if (reportKey is not null)
        {
            var shipIds = ships?.ToList() ?? store.LoadLedger().Packages.Select(p => p.ShipId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var ship in shipIds)
            {
                if (store.CreateBackupReport(ship, reportKey) is not { } c) continue;
                var dir = Path.Combine(dest, ship);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, c.Report.ReportId + ".sbbak");
                File.WriteAllBytes(path, c.Document.ToBytes());
                rep.Written.Add(path);
            }
        }
        return rep;
    }

    /// <summary>船端:套用隨身碟上的更新包(依版本)、處理已備份回報,並把待回傳紀錄包寫回隨身碟。</summary>
    public static UsbReport ShipImport(ShipNode ship, string fromDir, bool alsoExport = true)
    {
        var rep = new UsbReport();
        var src = ToShip(fromDir);
        ship.Records.BeginVisit("usb", Path.GetFullPath(fromDir));
        if (Directory.Exists(src))
        {
            var pkgs = new List<(string Path, PackageVersion Version)>();
            foreach (var f in Directory.EnumerateFiles(src, "*.sbupd"))
            {
                try { pkgs.Add((f, PackageVersion.Parse(UpdatePackageReader.PeekManifest(f).Version))); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { rep.Errors.Add($"{Path.GetFileName(f)}:{ex.Message}"); }
            }
            var installed = InstalledState.LoadOrNew(ship.Layout.InstalledStatePath);
            var highest = installed.HighestVersion ?? installed.Version;
            foreach (var (f, v) in pkgs.OrderBy(p => p.Version))
            {
                // 已套用過的舊包留在隨身碟上是常態,略過而不記為拒絕
                if (highest is not null && v.CompareTo(PackageVersion.Parse(highest)) <= 0)
                {
                    rep.Warnings.Add($"{Path.GetFileName(f)}(版本 {v})不大於已套用的 {highest},略過");
                    continue;
                }
                var r = ship.Updates.Apply(f, new UpdateApplyOptions { Via = "usb" });
                rep.Updates.Add(r);
                if (r.Ok) highest = r.Version;
            }
            var reportDir = Path.Combine(src, ship.ShipId);
            if (Directory.Exists(reportDir))
            {
                foreach (var f in Directory.EnumerateFiles(reportDir, "*.sbbak").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var c = ship.Records.AcceptBackupReport(File.ReadAllBytes(f));
                    rep.Cleanup = c;
                    rep.Errors.AddRange(c.Errors);
                    rep.Warnings.AddRange(c.Warnings.Where(w => !rep.Warnings.Contains(w)));
                    if (!c.Ok) continue;
                    if (c.Deleted.Count > 0) rep.Written.Add(WriteCleanupNotice(ship, fromDir, c));
                    File.Delete(f);
                }
            }
        }
        if (alsoExport)
        {
            var exp = ShipExport(ship, fromDir);
            rep.Written.AddRange(exp.Written);
            rep.Warnings.AddRange(exp.Warnings);
        }
        return rep;
    }

    /// <summary>船端:把清理結果(以裝置金鑰簽章)寫到 from-ship/&lt;ship&gt;/&lt;reportId&gt;.sbcln。</summary>
    private static string WriteCleanupNotice(ShipNode ship, string toDir, CleanupResult c)
    {
        var (info, key) = DeviceKeyStore.Load(ship.Layout);
        using (key)
        {
            var now = ship.Clock.GetUtcNow().UtcDateTime;
            var notice = new CleanupNotice { ShipId = ship.ShipId, DeviceId = info.DeviceId, ReportId = c.ReportId ?? "", Deleted = c.Deleted.ToList(), CreatedUtc = now };
            var dest = Path.Combine(FromShip(toDir), ship.ShipId);
            Directory.CreateDirectory(dest);
            var name = new string((c.ReportId ?? $"cleanup-{now:yyyyMMdd'T'HHmmss}").Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
            var path = Path.Combine(dest, name + CleanupNotice.Extension);
            File.WriteAllBytes(path, SignedDocument.Create(notice, key, now).ToBytes());
            return path;
        }
    }

    /// <summary>教官端:驗證清理通知(紀錄庫登錄的裝置公鑰)並標記船上已刪除;回傳錯誤訊息或 null。</summary>
    private static string? AcceptCleanupNotice(RecordStore store, string shipDirName, string path)
    {
        try
        {
            var doc = SignedDocument.Parse(File.ReadAllBytes(path), "清理通知");
            var n = doc.Read<CleanupNotice>("清理通知");
            if (n.Format != CleanupNotice.FormatId) return $"清理通知格式不支援:{n.Format}";
            if (!string.Equals(n.ShipId, shipDirName, StringComparison.OrdinalIgnoreCase)) return $"清理通知的船舶 {n.ShipId} 與目錄 {shipDirName} 不符";
            using var device = store.FindDevice(n.ShipId, n.DeviceId);
            if (device is null) return $"裝置 {n.ShipId}/{n.DeviceId} 未登錄於紀錄庫,無法驗證清理通知";
            if (!string.Equals(device.KeyId, doc.Signature.KeyId, StringComparison.OrdinalIgnoreCase) || !doc.Signature.VerifyWith(device.Key, doc.PayloadBytes))
                return $"清理通知簽章驗證失敗(不是登錄的裝置 {n.ShipId}/{n.DeviceId} 所簽)";
            store.MarkShipDeleted(n.ReportId, n.Deleted, n.ShipId);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            return ex.Message;
        }
    }

    /// <summary>船端:打包新紀錄,並把所有待回傳紀錄包與版次寫到隨身碟。</summary>
    public static UsbReport ShipExport(ShipNode ship, string toDir)
    {
        var rep = new UsbReport();
        ship.Records.PackPending();
        var dest = Path.Combine(FromShip(toDir), ship.ShipId);
        Directory.CreateDirectory(dest);
        foreach (var p in ship.Records.Outstanding())
        {
            var path = ship.Records.PackagePath(p.PackageId);
            if (path is null) continue;
            var target = Path.Combine(dest, Path.GetFileName(path));
            File.Copy(path, target, overwrite: true);
            if (!Sha256Hex.Equal(Sha256Hex.OfFile(target), p.Sha256)) throw new IOException($"寫入隨身碟後雜湊不符:{target}");
            rep.Written.Add(target);
        }
        File.WriteAllBytes(Path.Combine(dest, "status.json"), PackageJson.ToBytes(ship.Status()));
        rep.Warnings.AddRange(ship.Records.RetentionWarnings());
        return rep;
    }

    /// <summary>教官端:匯入隨身碟上的紀錄包(驗證裝置簽章與雜湊)並更新兩船版次清單。</summary>
    public static UsbReport InstructorImport(RecordStore store, string fromDir, bool enrollDevice = false)
    {
        var rep = new UsbReport();
        var src = FromShip(fromDir);
        if (!Directory.Exists(src))
        {
            rep.Errors.Add($"隨身碟上沒有 {RootName}/from-ship/");
            return rep;
        }
        foreach (var shipDir in Directory.EnumerateDirectories(src))
        {
            foreach (var f in Directory.EnumerateFiles(shipDir, "*.sbrec").OrderBy(f => f, StringComparer.Ordinal))
            {
                var r = store.Import(f, enrollDevice);
                rep.Imported.Add(r);
                rep.Errors.AddRange(r.Errors.Select(e => $"{Path.GetFileName(f)}:{e}"));
            }
            foreach (var f in Directory.EnumerateFiles(shipDir, "*" + CleanupNotice.Extension).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (AcceptCleanupNotice(store, Path.GetFileName(shipDir), f) is { } err) rep.Errors.Add($"{Path.GetFileName(f)}:{err}");
                else File.Delete(f);
            }
            var statusPath = Path.Combine(shipDir, "status.json");
            if (File.Exists(statusPath)) ImportShipStatus(store, Path.GetFileName(shipDir), statusPath, rep);
        }
        return rep;
    }

    /// <summary>
    /// 教官端:讀一艘船的 status.json(沒有簽章),印出用的發行版狀態放進報告,並更新兩船版次清單。
    /// <list type="bullet">
    /// <item>船舶識別要與所在的資料夾名稱相同(不分大小寫;船上匯出時兩者是同一個值),而且不含控制字元、不超過 64 個字 — 否則不採用、列一則警告
    /// (與清理通知的檢查相同):FSB1 資料夾裡改過的檔不能抹掉或種下 FSB2 保存的降版提醒。</item>
    /// <item>比教官筆電已有的舊就不採用(<see cref="RecordStore.UpdateFleet"/> 的 skipIfOlder):隨身碟上會留著以前匯出的 status.json。</item>
    /// <item>先把讀到的發行版狀態放進報告再存檔:fleet-versions.json 讀不進來(警告,與原本相同)或寫不進去(錯誤,結束碼與原本相同)時,
    /// 這次讀到的降版提醒仍會印出來。</item>
    /// </list>
    /// </summary>
    private static void ImportShipStatus(RecordStore store, string shipDirName, string statusPath, UsbReport rep)
    {
        ShipStatus st;
        ShipReleaseReport release;
        try
        {
            (st, release) = ReadShipStatusFile(statusPath);
        }
        // status.json 沒有簽章,內容(含解析失敗時例外訊息裡的鍵名片段)可能被改過:印出之前去掉控制字元並截斷
        catch (InvalidDataException ex)
        {
            rep.Warnings.Add(UntrustedText.Clean(ex.Message, 400));
            return;
        }
        var folder = UntrustedText.Clean(shipDirName, 64);
        const string unsigned = "這個檔沒有簽章;船上匯出時寫的船舶識別一定與資料夾名稱相同。版次與降版提醒請以船上的 simosa-pkg status 為準";
        if (string.IsNullOrEmpty(st.ShipId))
        {
            rep.Warnings.Add($"隨身碟上 from-ship/{folder}/status.json 沒有寫船舶識別,沒有採用({unsigned})");
            return;
        }
        if (!string.Equals(st.ShipId, shipDirName, StringComparison.OrdinalIgnoreCase))
        {
            rep.Warnings.Add($"隨身碟上 from-ship/{folder}/status.json 寫的船舶是「{UntrustedText.Clean(st.ShipId, 64)}」,與所在的資料夾「{folder}」不符,沒有採用({unsigned})");
            return;
        }
        if (UntrustedText.Clean(st.ShipId, 64) != st.ShipId)
        {
            rep.Warnings.Add($"隨身碟上 from-ship/{folder}/status.json 的船舶識別含有控制字元或超過 64 個字,沒有採用({unsigned})");
            return;
        }
        var entry = new ShipVersionEntry { ShipId = st.ShipId, DeviceId = st.DeviceId, Version = st.Version, PackageId = st.PackageId, Components = st.Components, ParameterSet = st.ParameterSet, Environment = st.Environment, Via = "usb" };
        release.ApplyTo(entry);
        var item = new UsbShipRelease(st.ShipId, st.DeviceId, release) { StatusUtc = store.StatusUtcOf(st.Environment) };
        rep.ShipReleases.Add(item);
        try
        {
            item.Fleet = store.UpdateFleet(entry, skipIfOlder: true);
        }
        catch (InvalidDataException ex)
        {
            rep.Warnings.Add(UntrustedText.Clean(ex.Message, 400));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rep.Errors.Add($"兩船版次清單 fleet-versions.json 寫不進去:{ex.Message}");
        }
    }

    /// <summary>隨身碟 status.json 的大小上限:與直連 statusResult 的訊息上限相同(實際約 1–3 KiB)</summary>
    public const int MaxStatusFileBytes = SyncProtocol.MaxMessageBytes;

    /// <summary>
    /// 讀隨身碟上的 status.json(船上 <see cref="ShipExport"/> 寫的 <c>simosa-pkg status --json</c>;沒有簽章)。發行版本與降版資訊由
    /// <see cref="ShipReleaseReport.Read"/> 寬鬆讀取(型別、格式不對只記為讀不懂),其餘欄位照舊解析;檔案太大、不是 JSON 物件或其餘欄位型別不對時
    /// 丟 <see cref="InvalidDataException"/>(呼叫端列為警告,不影響紀錄包的匯入與結束碼)。開頭的 UTF-8 BOM 略過(與 <see cref="PackageJson.Load{T}"/> 相同)。
    /// </summary>
    internal static (ShipStatus Status, ShipReleaseReport Release) ReadShipStatusFile(string path)
    {
        var what = $"status.json({path})";
        var size = new FileInfo(path).Length;
        if (size > MaxStatusFileBytes) throw new InvalidDataException($"{what} 有 {size} 位元組,超過上限 {MaxStatusFileBytes},略過");
        ReadOnlySpan<byte> bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject obj) throw new InvalidDataException($"{what} 不是 JSON 物件");
            var release = ShipReleaseReport.Read(obj);
            ShipReleaseReport.RemoveFields(obj);
            var status = obj.Deserialize<ShipStatus>(PackageJson.Options) ?? throw new InvalidDataException($"{what} 為空");
            return (status with { ReleaseVersion = release.ReleaseVersion, ReleaseDowngrade = release.Downgrade }, release);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{what} 不是有效的 JSON:{ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not InvalidDataException && ShipReleaseReport.IsJsonAccessError(ex))
        {
            // 重複的鍵、字串含不合法的 UTF-8 等(System.Text.Json 的 JsonNode 在存取時才檢查)
            throw new InvalidDataException($"{what} 的內容無法解析({ex.GetType().Name})", ex);
        }
    }
}
