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
}

/// <summary>
/// 專用隨身碟備援(網路線直連不可用時;規劃書第 5.5 節)。隨身碟上只放已簽章的檔案:
/// <code>
/// &lt;dir&gt;/simosa-transfer/to-ship/*.sbupd                 教官 → 船:更新包
/// &lt;dir&gt;/simosa-transfer/to-ship/&lt;ship&gt;/*.sbbak         教官 → 船:已備份回報
/// &lt;dir&gt;/simosa-transfer/from-ship/&lt;ship&gt;/*.sbrec       船 → 教官:紀錄包
/// &lt;dir&gt;/simosa-transfer/from-ship/&lt;ship&gt;/status.json   船 → 教官:版次(資訊用,未簽章)
/// </code>
/// 船端套用與教官端匯入時都完整驗證簽章,與直連相同。
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
                    if (c.Ok) File.Delete(f);
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
            var statusPath = Path.Combine(shipDir, "status.json");
            if (File.Exists(statusPath))
            {
                try
                {
                    var st = PackageJson.Load<ShipStatus>(statusPath, "status.json");
                    if (st.ShipId is { } sid)
                        store.UpdateFleet(new ShipVersionEntry { ShipId = sid, DeviceId = st.DeviceId, Version = st.Version, PackageId = st.PackageId, Components = st.Components, ParameterSet = st.ParameterSet, Via = "usb" });
                }
                catch (InvalidDataException ex) { rep.Warnings.Add(ex.Message); }
            }
        }
        return rep;
    }
}
