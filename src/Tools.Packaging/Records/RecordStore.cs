using System.IO.Compression;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Records;

/// <summary>教官筆電紀錄庫帳冊 &lt;store&gt;/store-ledger.json。</summary>
public sealed class StoreLedger
{
    public const string FormatId = "simosa-store-ledger/1";

    public string Format { get; set; } = FormatId;
    public List<StoredPackage> Packages { get; set; } = new();
}

public sealed class StoredPackage
{
    public string PackageId { get; set; } = "";
    public string ShipId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ImportedUtc { get; set; }
    /// <summary>相對紀錄庫的目錄(records/&lt;ship&gt;/&lt;yyyy&gt;/&lt;packageId&gt;)</summary>
    public string Directory { get; set; } = "";
    public int Exercises { get; set; }
    public double TrainingS { get; set; }
    public DateTime? BackedUpUtc { get; set; }
    public string? BackupTarget { get; set; }
    /// <summary>已送出的回報 ID(船上已確認刪除時記錄)</summary>
    public string? ReportedId { get; set; }
    public DateTime? ShipDeletedUtc { get; set; }
}

/// <summary>教官端兩船版次清單 &lt;store&gt;/fleet-versions.json(規劃書第 5.5 節)。</summary>
public sealed class FleetVersions
{
    public Dictionary<string, ShipVersionEntry> Ships { get; set; } = new();
}

public sealed class ShipVersionEntry
{
    public string ShipId { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? Version { get; set; }
    public string? PackageId { get; set; }
    public Dictionary<string, ComponentVersion> Components { get; set; } = new();
    public ParameterSetRef? ParameterSet { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? Via { get; set; }
}

public sealed class ImportResult
{
    public bool Ok { get; set; }
    public bool AlreadyImported { get; set; }
    public string? PackageId { get; set; }
    public string? ShipId { get; set; }
    public string? DeviceId { get; set; }
    public string? Directory { get; set; }
    public bool DeviceEnrolled { get; set; }
    public RecordPackageManifest? Manifest { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 教官筆電紀錄庫(規劃書第 5.5 節:教官筆電即為訓練紀錄庫主本)。目錄:
/// <code>
/// &lt;store&gt;/records/&lt;ship&gt;/&lt;yyyy&gt;/&lt;packageId&gt;.sbrec   原始簽章紀錄包(證據保存)
/// &lt;store&gt;/records/&lt;ship&gt;/&lt;yyyy&gt;/&lt;packageId&gt;/…       解開的紀錄與報告
/// &lt;store&gt;/devices/&lt;ship&gt;/&lt;deviceId&gt;.pem             已登錄的船上筆電裝置公鑰
/// &lt;store&gt;/store-ledger.json  &lt;store&gt;/fleet-versions.json  &lt;store&gt;/audit.jsonl
/// </code>
/// 匯入時驗證裝置簽章與每檔雜湊;未登錄的裝置預設拒絕(--enroll-device 首次登錄並記入稽核)。
/// </summary>
public sealed class RecordStore
{
    private readonly TimeProvider _clock;

    public RecordStore(string root, TimeProvider? clock = null)
    {
        Root = Path.GetFullPath(root);
        _clock = clock ?? TimeProvider.System;
        System.IO.Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string RecordsDir => Path.Combine(Root, "records");
    public string DevicesDir => Path.Combine(Root, "devices");
    public string LedgerPath => Path.Combine(Root, "store-ledger.json");
    public string FleetPath => Path.Combine(Root, "fleet-versions.json");
    public AuditLog Audit => new(Path.Combine(Root, "audit.jsonl"), _clock);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private static readonly object Gate = new();

    public StoreLedger LoadLedger() => File.Exists(LedgerPath) ? PackageJson.Load<StoreLedger>(LedgerPath, "store-ledger.json") : new StoreLedger();
    private void SaveLedger(StoreLedger l) => PackageJson.SaveAtomic(LedgerPath, l);

    public FleetVersions LoadFleet() => File.Exists(FleetPath) ? PackageJson.Load<FleetVersions>(FleetPath, "fleet-versions.json") : new FleetVersions();

    public void UpdateFleet(ShipVersionEntry entry)
    {
        lock (Gate)
        {
            var f = LoadFleet();
            entry.UpdatedUtc = Now;
            f.Ships[entry.ShipId] = entry;
            PackageJson.SaveAtomic(FleetPath, f);
        }
    }

    private static string Safe(string s)
    {
        var cleaned = new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) throw new InvalidDataException($"不合法的識別字:{s}");
        return cleaned;
    }

    /// <summary>登錄船上筆電裝置公鑰(安裝時由船上 device show 匯出)。</summary>
    public TrustedKey RegisterDevice(string shipId, string deviceId, string publicPem)
    {
        var key = TrustedKey.Parse(publicPem, null, KeyRoles.Device);
        var dir = Path.Combine(DevicesDir, Safe(shipId));
        System.IO.Directory.CreateDirectory(dir);
        var info = new DeviceInfo { ShipId = shipId, DeviceId = deviceId, KeyId = key.KeyId };
        File.WriteAllText(Path.Combine(dir, Safe(deviceId) + ".pem"), DeviceKeyStore.PublicPemWithMeta(info, key.PublicKeyPem));
        return key;
    }

    public TrustedKey? FindDevice(string shipId, string deviceId)
    {
        var path = Path.Combine(DevicesDir, Safe(shipId), Safe(deviceId) + ".pem");
        if (!File.Exists(path)) return null;
        var k = TrustedKey.Parse(File.ReadAllText(path), path, KeyRoles.Device);
        if (k.Role != KeyRoles.Device) return null;
        return k;
    }

    public bool Has(string packageId, string sha256) => LoadLedger().Packages.Any(p => p.PackageId == packageId && Sha256Hex.Equal(p.Sha256, sha256));

    /// <summary>驗證並匯入紀錄包。</summary>
    public ImportResult Import(string sbrecPath, bool enrollUnknownDevice = false)
    {
        var r = new ImportResult();
        try
        {
            var pkgSha = Sha256Hex.OfFile(sbrecPath);
            using var zip = ZipFile.OpenRead(sbrecPath);
            // 重複項目(尤其是第二個 manifest.json)會讓解開的內容與驗證的簽章不一致:一律拒絕
            if (UpdatePackageReader.FindDuplicateEntry(zip) is { } dup) throw new InvalidDataException($"紀錄包內有重複的項目:{dup}");
            var (mb, sig) = UpdatePackageReader.ReadSigned(zip);
            if (sig.FormatError() is { } fe) throw new InvalidDataException(fe);
            var m = PackageJson.FromBytes<RecordPackageManifest>(mb, "紀錄包 manifest.json");
            if (m.Format != RecordPackageManifest.FormatId) throw new InvalidDataException($"紀錄包格式不支援:{m.Format}");
            r.PackageId = m.PackageId;
            r.ShipId = m.ShipId;
            r.DeviceId = m.DeviceId;
            Safe(m.PackageId);
            Safe(m.ShipId);
            Safe(m.DeviceId);

            using var registered = FindDevice(m.ShipId, m.DeviceId);
            TrustedKey? enrolled = null;
            var key = registered;
            if (key is null)
            {
                if (!enrollUnknownDevice) throw new InvalidDataException($"裝置 {m.ShipId}/{m.DeviceId} 未登錄於紀錄庫(首次請以 --enroll-device 登錄,並核對金鑰 {KeyFiles.Short(sig.KeyId)})");
                if (sig.PublicKey is null) throw new InvalidDataException("紀錄包未附裝置公鑰,無法登錄");
                enrolled = TrustedKey.Parse(sig.PublicKey, null, KeyRoles.Device);
                key = enrolled;
            }
            using var _e = enrolled;
            if (!string.Equals(key.KeyId, sig.KeyId, StringComparison.OrdinalIgnoreCase) || !string.Equals(m.Device.KeyId, sig.KeyId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"紀錄包簽章金鑰 {KeyFiles.Short(sig.KeyId)} 與登錄的裝置金鑰 {KeyFiles.Short(key.KeyId)} 不符");
            if (!sig.VerifyWith(key.Key, mb)) throw new InvalidDataException("紀錄包裝置簽章驗證失敗(manifest 遭竄改)");

            // 檔案清單 ↔ zip 內容與雜湊
            var listed = new Dictionary<string, RecordFileEntry>(StringComparer.Ordinal);
            foreach (var f in m.Files)
            {
                var p = PackagePaths.Normalize(f.Path);
                if (p != f.Path || !p.StartsWith(RecordPackageManifest.FilePrefix, StringComparison.Ordinal)) throw new InvalidDataException($"紀錄包路徑不合法:{f.Path}");
                if (!listed.TryAdd(p, f)) throw new InvalidDataException($"紀錄包檔案重複:{p}");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (e.FullName is "manifest.json" or "manifest.sig" || e.FullName.EndsWith('/')) continue;
                if (!listed.TryGetValue(e.FullName, out var f)) throw new InvalidDataException($"紀錄包含未列於 manifest 的檔案:{e.FullName}");
                if (!seen.Add(e.FullName)) throw new InvalidDataException($"紀錄包重複檔案:{e.FullName}");
                using var s = e.Open();
                if (!Sha256Hex.Equal(Sha256Hex.OfStream(s), f.Sha256) || e.Length != f.Size) throw new InvalidDataException($"紀錄檔雜湊不符(遭竄改):{e.FullName}");
            }
            var missing = listed.Keys.Where(k => !seen.Contains(k)).ToList();
            if (missing.Count > 0) throw new InvalidDataException($"紀錄包缺少檔案:{string.Join(", ", missing)}");

            lock (Gate)
            {
                var ledger = LoadLedger();
                var existing = ledger.Packages.FirstOrDefault(p => p.PackageId == m.PackageId);
                if (existing is not null)
                {
                    if (!Sha256Hex.Equal(existing.Sha256, pkgSha)) throw new InvalidDataException($"紀錄包 {m.PackageId} 已存在但內容不同");
                    r.Ok = true;
                    r.AlreadyImported = true;
                    r.Directory = existing.Directory;
                    r.Manifest = m;
                    return r;
                }
                if (enrolled is not null)
                {
                    using var reg = RegisterDevice(m.ShipId, m.DeviceId, enrolled.PublicKeyPem);
                    r.DeviceEnrolled = true;
                    Audit.Write(AuditEvents.StoreDeviceEnrolled, new { m.ShipId, m.DeviceId, keyId = enrolled.KeyId, via = "import" });
                }
                var year = m.CreatedUtc.Year.ToString("D4");
                var relDir = $"records/{Safe(m.ShipId)}/{year}/{Safe(m.PackageId)}";
                var dest = Path.Combine(Root, relDir.Replace('/', Path.DirectorySeparatorChar));
                var tmp = dest + ".importing";
                PackagePaths.DeleteDirectory(tmp);
                System.IO.Directory.CreateDirectory(tmp);
                // manifest.json/manifest.sig 寫入「已驗證的位元組」,不從 zip 另行解出;其餘只解出已核對雜湊的紀錄檔,不覆寫
                File.WriteAllBytes(Path.Combine(tmp, "manifest.json"), mb);
                File.WriteAllBytes(Path.Combine(tmp, "manifest.sig"), UpdatePackageReader.ReadEntry(zip.GetEntry("manifest.sig")!, 64 << 10));
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/') || e.FullName is "manifest.json" or "manifest.sig") continue;
                    var target = PackagePaths.Combine(tmp, PackagePaths.Normalize(e.FullName[RecordPackageManifest.FilePrefix.Length..]));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: false);
                    if (!Sha256Hex.Equal(Sha256Hex.OfFile(target), listed[e.FullName].Sha256)) throw new InvalidDataException($"解開後紀錄檔雜湊不符:{e.FullName}");
                }
                PackagePaths.DeleteDirectory(dest);
                System.IO.Directory.Move(tmp, dest);
                File.Copy(sbrecPath, dest + ".sbrec", overwrite: true);
                ledger.Packages.Add(new StoredPackage
                {
                    PackageId = m.PackageId, ShipId = m.ShipId, DeviceId = m.DeviceId, Sha256 = pkgSha, Size = new FileInfo(sbrecPath).Length,
                    CreatedUtc = m.CreatedUtc, ImportedUtc = Now, Directory = relDir, Exercises = m.Exercises.Count, TrainingS = m.TrainingHours.TotalS,
                });
                SaveLedger(ledger);
                r.Ok = true;
                r.Directory = relDir;
                r.Manifest = m;
                Audit.Write(AuditEvents.StoreImported, new { m.PackageId, m.ShipId, m.DeviceId, sha256 = pkgSha, files = m.Files.Count, exercises = m.Exercises.Count, trainingHours = m.TrainingHours.TotalHours });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Security.Cryptography.CryptographicException)
        {
            r.Errors.Add(ex.Message);
            Audit.Write(AuditEvents.StoreImportRejected, new { file = Path.GetFileName(sbrecPath), r.PackageId, r.ShipId, r.DeviceId, error = ex.Message });
        }
        return r;
    }

    /// <summary>
    /// 標記已完成岸上備份(加密外接碟或公司檔案共享)。<paramref name="packageIds"/> 為 null 時標記全部已匯入者。
    /// 之後的同步才會把這些紀錄包列入已備份回報。
    /// </summary>
    public List<string> MarkBackedUp(IEnumerable<string>? packageIds, string? target)
    {
        lock (Gate)
        {
            var l = LoadLedger();
            var ids = packageIds?.ToHashSet(StringComparer.Ordinal);
            var marked = new List<string>();
            foreach (var p in l.Packages.Where(p => p.BackedUpUtc is null && (ids is null || ids.Contains(p.PackageId))))
            {
                p.BackedUpUtc = Now;
                p.BackupTarget = target;
                marked.Add(p.PackageId);
            }
            SaveLedger(l);
            if (marked.Count > 0) Audit.Write(AuditEvents.StoreBackedUp, new { packages = marked, target });
            return marked;
        }
    }

    /// <summary>
    /// 備份整個紀錄庫到目的地(加密外接碟掛載點或檔案共享;目的地加密由作業系統 BitLocker To Go 等負責),
    /// 複製後逐檔比對雜湊,成功才標記已備份。
    /// </summary>
    public (string Target, List<string> Marked) Backup(string targetDir)
    {
        var targetFull = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (targetFull.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("備份目的地不可位於紀錄庫之內");
        var stamp = Now.ToString("yyyyMMdd'T'HHmmss");
        var dest = Path.Combine(targetDir, $"simosa-store-backup-{stamp}");
        var importedBefore = LoadLedger().Packages.Select(p => p.PackageId).ToList();
        foreach (var file in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root, file);
            var to = Path.Combine(dest, rel);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            if (!Sha256Hex.Equal(Sha256Hex.OfFile(file), Sha256Hex.OfFile(to))) throw new IOException($"備份後雜湊不符:{rel}");
        }
        return (dest, MarkBackedUp(importedBefore, dest));
    }

    /// <summary>產生指定船舶的已備份回報(列出已備份、且船上仍保留的紀錄包;<paramref name="onlyIds"/> 為船上回報的清單)。無可回報者回傳 null。</summary>
    public (SignedDocument Document, BackupReport Report)? CreateBackupReport(string shipId, IKeyProvider key, IEnumerable<string>? onlyIds = null)
    {
        var only = onlyIds?.ToHashSet(StringComparer.Ordinal);
        var l = LoadLedger();
        var pkgs = l.Packages.Where(p => string.Equals(p.ShipId, shipId, StringComparison.OrdinalIgnoreCase) && p.BackedUpUtc is not null && p.ShipDeletedUtc is null
                                         && (only is null || only.Contains(p.PackageId))).ToList();
        if (pkgs.Count == 0) return null;
        var now = Now;
        var report = new BackupReport
        {
            ReportId = $"bak-{shipId}-{now:yyyyMMdd'T'HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            ShipId = shipId, IssuedUtc = now, Store = System.Environment.MachineName,
            Packages = pkgs.Select(p => new BackedUpPackage { PackageId = p.PackageId, Sha256 = p.Sha256, ImportedUtc = p.ImportedUtc, BackedUpUtc = p.BackedUpUtc, BackupTarget = p.BackupTarget }).ToList(),
            SignerKeyId = key.KeyId, SignerName = key.DisplayName,
        };
        var doc = SignedDocument.Create(report, key, now);
        Audit.Write(AuditEvents.StoreReportIssued, new { report.ReportId, shipId, packages = report.Packages.Select(p => p.PackageId).ToList(), signer = key.DisplayName });
        return (doc, report);
    }

    /// <summary>船上回報已刪除(直連 cleanupResult,或隨身碟上的裝置簽章清理通知)後記錄;<paramref name="shipId"/> 不為 null 時只標記該船的紀錄包。</summary>
    public void MarkShipDeleted(string reportId, IEnumerable<string> packageIds, string? shipId = null)
    {
        lock (Gate)
        {
            var l = LoadLedger();
            var ids = packageIds.ToHashSet(StringComparer.Ordinal);
            foreach (var p in l.Packages.Where(p => ids.Contains(p.PackageId) && (shipId is null || string.Equals(p.ShipId, shipId, StringComparison.OrdinalIgnoreCase))))
            {
                p.ShipDeletedUtc ??= Now;
                p.ReportedId = reportId;
            }
            SaveLedger(l);
        }
    }
}
