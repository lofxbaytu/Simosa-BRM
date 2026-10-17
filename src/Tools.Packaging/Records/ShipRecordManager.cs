using System.IO.Compression;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Records;

/// <summary>船上紀錄帳冊 state/records-ledger.json。</summary>
public sealed class RecordLedger
{
    public const string FormatId = "simosa-records-ledger/1";

    public string Format { get; set; } = FormatId;
    public List<VisitEntry> Visits { get; set; } = new();
    public List<LedgerPackage> Packages { get; set; } = new();

    public int CurrentVisit => Visits.Count == 0 ? 0 : Visits[^1].Seq;
}

public sealed class VisitEntry
{
    public int Seq { get; set; }
    public DateTime Utc { get; set; }
    /// <summary>tcp(直連)或 usb(專用隨身碟)</summary>
    public string Via { get; set; } = "";
    public string? Peer { get; set; }
}

public sealed class LedgerPackage
{
    public string PackageId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>打包時的訪船序號(0:第一次訪船前)</summary>
    public int VisitSeq { get; set; }
    public List<RecordFileEntry> Files { get; set; } = new();
    public int Exercises { get; set; }
    public double TrainingS { get; set; }
    public DateTime? PulledUtc { get; set; }
    public DateTime? BackedUpUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public string? ReportId { get; set; }
}

/// <summary>同步訊息 recordList 的一列。</summary>
public sealed class OutstandingPackage
{
    public string PackageId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int VisitSeq { get; set; }
    public int Files { get; set; }
    public int Exercises { get; set; }
    public double TrainingS { get; set; }
    public DateTime? PulledUtc { get; set; }
}

/// <summary>
/// 船上紀錄管理(規劃書第 5.5 節「紀錄回傳」):把 build/records 的新紀錄(*.jsonl 與 reports/ 下的報告)打包成以裝置金鑰簽章的
/// 紀錄包(state/outbox/*.sbrec);原始檔保留到收到教官筆電的已備份回報才刪除。未取得回報的紀錄不自動清除;
/// 累積超過 <see cref="PackagingConfig.MaxVisitIntervals"/> 個訪船間隔時於 status 與同步回報警告。
/// </summary>
public sealed class ShipRecordManager
{
    private readonly InstallLayout _layout;
    private readonly TimeProvider _clock;

    public ShipRecordManager(InstallLayout layout, TimeProvider? clock = null)
    {
        _layout = layout;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>未寫 footer 且最近仍在修改的紀錄視為練習進行中,不打包。</summary>
    public TimeSpan InProgressGrace { get; init; } = TimeSpan.FromMinutes(2);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public RecordLedger LoadLedger() => File.Exists(_layout.LedgerPath) ? PackageJson.Load<RecordLedger>(_layout.LedgerPath, "records-ledger.json") : new RecordLedger();

    private void SaveLedger(RecordLedger l) => PackageJson.SaveAtomic(_layout.LedgerPath, l);

    /// <summary>開始一次訪船(直連 session 或隨身碟匯入);回傳訪船序號。</summary>
    public int BeginVisit(string via, string? peer = null)
    {
        using var _ = _layout.AcquireLock();
        var l = LoadLedger();
        var seq = l.CurrentVisit + 1;
        l.Visits.Add(new VisitEntry { Seq = seq, Utc = Now, Via = via, Peer = peer });
        SaveLedger(l);
        return seq;
    }

    /// <summary>候選檔案:紀錄目錄第一層 *.jsonl(record)與 reports/ 下所有檔案(report)。</summary>
    private IEnumerable<(string Full, string Rel, string Kind)> Candidates()
    {
        var dir = _layout.RecordsDir;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
            yield return (f, PackagePaths.Relative(dir, f), "record");
        var reports = Path.Combine(dir, "reports");
        if (Directory.Exists(reports))
            foreach (var f in Directory.EnumerateFiles(reports, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                yield return (f, PackagePaths.Relative(dir, f), "report");
    }

    /// <summary>把尚未打包(或內容已變)的紀錄打包成一個紀錄包;沒有新紀錄時回傳 null。</summary>
    public LedgerPackage? PackPending()
    {
        var (info, key) = DeviceKeyStore.Load(_layout);
        using (key)
        using (_layout.AcquireLock())
        {
            var ledger = LoadLedger();
            var known = ledger.Packages.SelectMany(p => p.Files).Select(f => f.Path + "\u0001" + f.Sha256.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var picked = new List<(string Full, RecordFileEntry Entry)>();
            foreach (var (full, rel, kind) in Candidates())
            {
                if (kind == "record" && !RecordInspector.HasFooter(full) && Now - File.GetLastWriteTimeUtc(full) < InProgressGrace) continue;
                var sha = Sha256Hex.OfFile(full);
                var path = RecordPackageManifest.FilePrefix + rel;
                if (known.Contains(path + "\u0001" + sha)) continue;
                picked.Add((full, new RecordFileEntry { Path = path, Sha256 = sha, Size = new FileInfo(full).Length, Kind = kind }));
            }
            if (picked.Count == 0) return null;

            var installed = InstalledState.LoadOrNew(_layout.InstalledStatePath);
            var exercises = picked.Where(p => p.Entry.Kind == "record").Select(p => RecordInspector.Inspect(p.Full, p.Entry.Path)).ToList();
            var now = Now;
            var manifest = new RecordPackageManifest
            {
                PackageId = $"rec-{info.ShipId}-{now:yyyyMMdd'T'HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                ShipId = info.ShipId, DeviceId = info.DeviceId, CreatedUtc = now, VisitSeq = ledger.CurrentVisit,
                Files = picked.Select(p => p.Entry).ToList(), Exercises = exercises, TrainingHours = RecordInspector.Summarize(exercises),
                InstalledVersion = installed.Version, ParameterSetVersion = installed.ParameterSet?.Version,
                Device = new DeviceKeyRef { KeyId = key.KeyId, PublicKey = key.PublicKeyPem },
            };
            var mb = PackageJson.ToBytes(manifest);
            var sig = SignatureEnvelope.Create(key, mb, now);
            Directory.CreateDirectory(_layout.OutboxDir);
            var fileName = manifest.PackageId + ".sbrec";
            var outPath = Path.Combine(_layout.OutboxDir, fileName);
            var tmp = outPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                UpdatePackageBuilder.Write(zip, "manifest.json", mb);
                UpdatePackageBuilder.Write(zip, "manifest.sig", PackageJson.ToBytes(sig));
                foreach (var (full, entry) in picked)
                {
                    var e = zip.CreateEntry(entry.Path, CompressionLevel.Optimal);
                    using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = e.Open();
                    src.CopyTo(dst);
                }
            }
            File.Move(tmp, outPath, overwrite: true);
            // 打包期間檔案若被改寫,雜湊會與 manifest 不符;重新核對,不符則放棄本次打包
            foreach (var (full, entry) in picked)
            {
                if (!Sha256Hex.Equal(Sha256Hex.OfFile(full), entry.Sha256))
                {
                    File.Delete(outPath);
                    throw new IOException($"打包期間紀錄檔被修改:{entry.Path},請稍後重試");
                }
            }
            var lp = new LedgerPackage
            {
                PackageId = manifest.PackageId, FileName = fileName, Sha256 = Sha256Hex.OfFile(outPath), Size = new FileInfo(outPath).Length,
                CreatedUtc = now, VisitSeq = manifest.VisitSeq, Files = manifest.Files, Exercises = exercises.Count, TrainingS = manifest.TrainingHours.TotalS,
            };
            ledger.Packages.Add(lp);
            SaveLedger(ledger);
            _layout.Audit(_clock).Write(AuditEvents.RecordsPacked, new { lp.PackageId, lp.Sha256, files = lp.Files.Count, lp.Exercises, trainingHours = manifest.TrainingHours.TotalHours });
            return lp;
        }
    }

    /// <summary>尚未刪除的紀錄包(待回傳或待備份回報)。</summary>
    public List<OutstandingPackage> Outstanding()
        => LoadLedger().Packages.Where(p => p.DeletedUtc is null && File.Exists(Path.Combine(_layout.OutboxDir, p.FileName)))
            .Select(p => new OutstandingPackage
            {
                PackageId = p.PackageId, Sha256 = p.Sha256, Size = p.Size, CreatedUtc = p.CreatedUtc, VisitSeq = p.VisitSeq,
                Files = p.Files.Count, Exercises = p.Exercises, TrainingS = p.TrainingS, PulledUtc = p.PulledUtc,
            }).ToList();

    public string? PackagePath(string packageId)
    {
        var p = LoadLedger().Packages.FirstOrDefault(x => x.PackageId == packageId && x.DeletedUtc is null);
        if (p is null) return null;
        var path = Path.Combine(_layout.OutboxDir, p.FileName);
        return File.Exists(path) ? path : null;
    }

    public void MarkPulled(string packageId, string? peer = null)
    {
        using var _ = _layout.AcquireLock();
        var l = LoadLedger();
        var p = l.Packages.FirstOrDefault(x => x.PackageId == packageId);
        if (p is null) return;
        p.PulledUtc ??= Now;
        SaveLedger(l);
        _layout.Audit(_clock).Write(AuditEvents.RecordsPulled, new { packageId, p.Sha256, peer });
    }

    /// <summary>
    /// 保留上限警告:未取得備份回報的紀錄包,其打包時的訪船序號 ≤ 目前序號 − 上限,表示已累積超過上限個訪船間隔
    /// (正常流程:第 k 次訪船取回 → 教官備份 → 第 k+1 次訪船回報後刪除)。只警告,不刪除。
    /// </summary>
    public List<string> RetentionWarnings()
    {
        var l = LoadLedger();
        var max = Math.Max(1, _layout.Config.MaxVisitIntervals);
        var warnings = new List<string>();
        var stale = l.Packages.Where(p => p.DeletedUtc is null && l.CurrentVisit - p.VisitSeq >= max).ToList();
        if (stale.Count > 0)
            warnings.Add($"{stale.Count} 個紀錄包已累積超過 {max} 個訪船間隔仍未取得教官筆電的已備份回報(不自動刪除;請取回並完成岸上備份):{string.Join(", ", stale.Select(s => s.PackageId))}");
        return warnings;
    }

    /// <summary>驗證已備份回報(信任清單主/備援金鑰簽章、船舶 ID)並刪除所列紀錄包與原始紀錄檔(雜湊相符者)。</summary>
    public CleanupResult AcceptBackupReport(byte[] reportBytes)
    {
        var result = new CleanupResult();
        var audit = _layout.Audit(_clock);
        using var trust = TrustStore.LoadDirectory(_layout.TrustedKeysDir);
        BackupReport report;
        try
        {
            var doc = SignedDocument.Parse(reportBytes, "已備份回報");
            var key = trust.Find(doc.Signature.KeyId);
            if (key is null || !KeyRoles.IsSigner(key.Role)) throw new InvalidDataException($"回報簽章金鑰 {KeyFiles.Short(doc.Signature.KeyId)}(自稱「{doc.Signature.Signer}」)不在信任清單");
            if (!doc.Signature.VerifyWith(key.Key, doc.PayloadBytes)) throw new InvalidDataException("回報簽章驗證失敗(遭竄改)");
            report = doc.Read<BackupReport>("已備份回報");
            if (report.Format != BackupReport.FormatId) throw new InvalidDataException($"回報格式不支援:{report.Format}");
            var ship = _layout.Config.ShipId ?? (DeviceKeyStore.Exists(_layout) ? DeviceKeyStore.LoadInfo(_layout).ShipId : null);
            if (!string.Equals(report.ShipId, ship, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"回報對象為 {report.ShipId},本機為 {ship}");
            result.Signer = key.Describe();
            result.ReportId = report.ReportId;
        }
        catch (InvalidDataException ex)
        {
            result.Errors.Add(ex.Message);
            audit.Write(AuditEvents.BackupReportRejected, new { error = ex.Message });
            return result;
        }

        using (_layout.AcquireLock())
        {
            var l = LoadLedger();
            var now = Now;
            foreach (var bp in report.Packages)
            {
                var p = l.Packages.FirstOrDefault(x => x.PackageId == bp.PackageId);
                if (p is null)
                {
                    result.Unknown.Add(bp.PackageId);
                    continue;
                }
                if (!Sha256Hex.Equal(p.Sha256, bp.Sha256))
                {
                    result.Mismatched.Add(bp.PackageId);
                    continue;
                }
                if (p.DeletedUtc is not null) continue;
                var outbox = Path.Combine(_layout.OutboxDir, p.FileName);
                if (File.Exists(outbox)) File.Delete(outbox);
                foreach (var f in p.Files)
                {
                    var rel = f.Path[RecordPackageManifest.FilePrefix.Length..];
                    var full = PackagePaths.Combine(_layout.RecordsDir, rel);
                    // 只刪除內容與打包時相同的檔案(之後被改寫的檔案會在下一次打包時另行處理)
                    if (File.Exists(full) && Sha256Hex.Equal(Sha256Hex.OfFile(full), f.Sha256))
                    {
                        File.Delete(full);
                        result.DeletedFiles.Add(rel);
                    }
                }
                p.BackedUpUtc = bp.BackedUpUtc ?? now;
                p.DeletedUtc = now;
                p.ReportId = report.ReportId;
                result.Deleted.Add(p.PackageId);
            }
            result.Retained = l.Packages.Where(p => p.DeletedUtc is null).Select(p => p.PackageId).ToList();
            SaveLedger(l);
        }
        result.Ok = true;
        result.Warnings = RetentionWarnings();
        audit.Write(AuditEvents.BackupReportAccepted, new { result.ReportId, signer = result.Signer, deleted = result.Deleted, files = result.DeletedFiles.Count, result.Retained, result.Mismatched, result.Unknown });
        if (result.Deleted.Count > 0) audit.Write(AuditEvents.RecordsDeleted, new { result.ReportId, packages = result.Deleted, files = result.DeletedFiles });
        return result;
    }
}
