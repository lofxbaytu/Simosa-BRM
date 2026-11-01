using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.SimCore.Recording;

namespace SimosaBRM.Packaging.Tests;

/// <summary>紀錄包往返、裝置簽章、已備份回報後清理與保留上限(規劃書第 5.5 節「紀錄回傳」);練習錄影(media/*.webm + 旁檔)納入、關聯與上限(docs/design/screen-recording.md 第 4 節)。</summary>
public sealed class RecordPackageTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc);

    private (InstallLayout Ship, ShipRecordManager Mgr) ShipWithRecords()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "20261012-013000-E01_baseline.jsonl", "E01_baseline", Start, 3600,
            new object[] { new { name = "王大副", role = "OOW" }, "李舵工" });
        TestEnv.WriteRecord(ship.RecordsDir, "20261013-020000-E07_steering_failure.jsonl", "E07_steering_failure", Start.AddDays(1), 1800,
            new object[] { new { name = "王大副", role = "OOW" } });
        Directory.CreateDirectory(Path.Combine(ship.RecordsDir, "reports"));
        File.WriteAllText(Path.Combine(ship.RecordsDir, "reports", "E01-debrief.html"), "<html>講評</html>");
        return (ship, new ShipRecordManager(ship));
    }

    [Fact]
    public void PackAndImport_RoundTrip_WithTrainingHours()
    {
        var (ship, mgr) = ShipWithRecords();
        var p = mgr.PackPending();
        Assert.NotNull(p);
        Assert.Equal(3, p!.Files.Count);
        Assert.Equal(2, p.Exercises);
        Assert.Null(mgr.PackPending()); // 已打包者不重複

        var store = new RecordStore(_env.PathIn("store"));
        var path = mgr.PackagePath(p.PackageId)!;
        var unknown = store.Import(path);
        Assert.False(unknown.Ok);
        Assert.Contains(unknown.Errors, e => e.Contains("未登錄"));

        var r = store.Import(path, enrollUnknownDevice: true);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.True(r.DeviceEnrolled);
        Assert.Equal($"records/FSB1/{DateTime.UtcNow.Year:D4}/{p.PackageId}", r.Directory);
        var dir = Path.Combine(store.Root, r.Directory!);
        Assert.True(File.Exists(Path.Combine(dir, "20261012-013000-E01_baseline.jsonl")));
        Assert.True(File.Exists(Path.Combine(dir, "reports", "E01-debrief.html")));
        Assert.True(File.Exists(dir + ".sbrec"));

        var m = r.Manifest!;
        Assert.Equal("FSB1", m.ShipId);
        Assert.Equal("FSB1-LT01", m.DeviceId);
        Assert.Equal(5400, m.TrainingHours.TotalS, 3);
        var wang = Assert.Single(m.TrainingHours.Participants, x => x.Name == "王大副");
        Assert.Equal(2, wang.Exercises);
        Assert.Equal(1.5, wang.TotalHours, 3);
        Assert.Equal("OOW", wang.Role);
        var e01 = Assert.Single(m.Exercises, e => e.ScenarioId == "E01_baseline");
        Assert.True(e01.IntegrityOk);
        Assert.Equal(Start, e01.StartUtc);
        Assert.Equal(Start.AddHours(1), e01.EndUtc);

        // 再次匯入相同包:冪等
        Assert.True(store.Import(path).AlreadyImported);
    }

    [Fact]
    public void TamperedRecordPackage_Rejected()
    {
        var (_, mgr) = ShipWithRecords();
        var p = mgr.PackPending()!;
        var copy = _env.PathIn("tampered.sbrec");
        File.Copy(mgr.PackagePath(p.PackageId)!, copy);
        TestEnv.RewriteZip(copy, "records/20261012-013000-E01_baseline.jsonl", b => b.Concat("{\"kind\":\"event\"}\n"u8.ToArray()).ToArray());
        var store = new RecordStore(_env.PathIn("store"));
        var r = store.Import(copy, enrollUnknownDevice: true);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("雜湊不符"));
        Assert.Empty(store.LoadLedger().Packages);

        var copy2 = _env.PathIn("tampered2.sbrec");
        File.Copy(mgr.PackagePath(p.PackageId)!, copy2);
        TestEnv.RewriteZip(copy2, "manifest.json", b => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(b).Replace("王大副", "張三")));
        var r2 = store.Import(copy2, enrollUnknownDevice: true);
        Assert.False(r2.Ok);
        Assert.Contains(r2.Errors, e => e.Contains("簽章驗證失敗"));
    }

    [Fact]
    public void PackageSignedByOtherDevice_Rejected()
    {
        var (ship, mgr) = ShipWithRecords();
        var store = new RecordStore(_env.PathIn("store"));
        // 教官端登錄的是另一把裝置金鑰(例如筆電被調包)
        var (_, otherPub) = TestEnv.NewKey(KeyRoles.Primary, "x");
        store.RegisterDevice("FSB1", "FSB1-LT01", otherPub).Dispose();
        var p = mgr.PackPending()!;
        var r = store.Import(mgr.PackagePath(p.PackageId)!, enrollUnknownDevice: true);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("不符"));
    }

    [Fact]
    public void InProgressRecord_NotPacked()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "running.jsonl", "E01_baseline", DateTime.UtcNow, 60, footer: false);
        var mgr = new ShipRecordManager(ship);
        Assert.Null(mgr.PackPending());
        var later = new ShipRecordManager(ship) { InProgressGrace = TimeSpan.Zero };
        var p = later.PackPending();
        Assert.NotNull(p);
    }

    private static byte[] Report(RecordStore store, string ship, PemKeyProvider key) => store.CreateBackupReport(ship, key)!.Value.Document.ToBytes();

    [Fact]
    public void BackupReport_DeletesOnlyConfirmedCopies()
    {
        var (ship, mgr) = ShipWithRecords();
        var p1 = mgr.PackPending()!;
        var store = new RecordStore(_env.PathIn("store"));
        Assert.True(store.Import(mgr.PackagePath(p1.PackageId)!, true).Ok);
        // 第二批紀錄(尚未取回)
        TestEnv.WriteRecord(ship.RecordsDir, "20261020-010000-E09_blackout.jsonl", "E09_blackout", Start.AddDays(8), 900);
        var p2 = mgr.PackPending()!;

        using var key = _env.Primary();
        Assert.Null(store.CreateBackupReport("FSB1", key)); // 尚未備份,不產生回報
        store.MarkBackedUp(null, "加密外接碟 #1");
        var res = mgr.AcceptBackupReport(Report(store, "FSB1", key));
        Assert.True(res.Ok, string.Join(";", res.Errors));
        Assert.Equal(new[] { p1.PackageId }, res.Deleted);
        Assert.Equal(3, res.DeletedFiles.Count);
        Assert.Contains(p2.PackageId, res.Retained);
        Assert.False(File.Exists(Path.Combine(ship.RecordsDir, "20261012-013000-E01_baseline.jsonl")));
        Assert.True(File.Exists(Path.Combine(ship.RecordsDir, "20261020-010000-E09_blackout.jsonl")));
        Assert.Null(mgr.PackagePath(p1.PackageId));
        Assert.NotNull(mgr.PackagePath(p2.PackageId));
        Assert.Contains(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.RecordsDeleted);
    }

    [Fact]
    public void BackupReport_UntrustedOrWrongShip_Rejected()
    {
        var (ship, mgr) = ShipWithRecords();
        var p = mgr.PackPending()!;
        var store = new RecordStore(_env.PathIn("store"));
        store.Import(mgr.PackagePath(p.PackageId)!, true);
        store.MarkBackedUp(null, null);

        var (priv, _) = TestEnv.NewKey(KeyRoles.Primary, "陌生人");
        using var stranger = new PemKeyProvider(priv);
        var bad = mgr.AcceptBackupReport(Report(store, "FSB1", stranger));
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Contains("不在信任清單"));

        using var key = _env.Primary();
        var doc = store.CreateBackupReport("FSB1", key)!.Value.Document;
        var forged = doc with { Payload = doc.Payload.Replace("\"FSB1\"", "\"FSB2\"") };
        var r = mgr.AcceptBackupReport(forged.ToBytes());
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("簽章驗證失敗"));

        Assert.True(File.Exists(Path.Combine(ship.RecordsDir, "20261012-013000-E01_baseline.jsonl")));
        Assert.NotNull(mgr.PackagePath(p.PackageId));
    }

    [Fact]
    public void RetentionLimit_WarnsButNeverDeletesUnconfirmed()
    {
        var ship = _env.NewShip(maxVisitIntervals: 2);
        TestEnv.WriteRecord(ship.RecordsDir, "a.jsonl", "E01_baseline", Start, 600);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var mgr = new ShipRecordManager(ship, clock);
        mgr.BeginVisit("tcp");             // 訪船 #1:打包取回
        var p = mgr.PackPending()!;
        Assert.Equal(1, p.VisitSeq);
        clock.Advance(TimeSpan.FromDays(14));
        mgr.BeginVisit("tcp");             // 訪船 #2:教官筆電未回報
        Assert.Empty(mgr.RetentionWarnings());
        clock.Advance(TimeSpan.FromDays(14));
        mgr.BeginVisit("usb");             // 訪船 #3:已累積兩個間隔
        var w = Assert.Single(mgr.RetentionWarnings());
        Assert.Contains(p.PackageId, w);
        Assert.True(File.Exists(Path.Combine(ship.RecordsDir, "a.jsonl")));
        Assert.NotNull(mgr.PackagePath(p.PackageId));
    }

    // ------------------------------------------------------------ 練習錄影(docs/design/screen-recording.md 第 4 節)
    /// <summary>合成一段錄影:media/&lt;mediaId&gt;.webm(偽內容)與 SimCore.Host MediaUploadServer 格式的旁檔。</summary>
    private static (string Webm, string Json, MediaSidecar Sidecar) WriteMedia(string recordsDir, string mediaId, int sizeBytes, double durationS, MediaRecordRef? record, bool truncated = false, DateTime? started = null)
    {
        var dir = Path.Combine(recordsDir, "media");
        Directory.CreateDirectory(dir);
        var bytes = new byte[sizeBytes];
        new Random(mediaId.GetHashCode()).NextBytes(bytes);
        var webm = Path.Combine(dir, mediaId + ".webm");
        File.WriteAllBytes(webm, bytes);
        var startUtc = started ?? Start.AddSeconds(5);
        var sc = new MediaSidecar
        {
            MediaId = mediaId, File = mediaId + ".webm", Station = "instructor", Mime = "video/webm;codecs=vp9,opus", ExerciseId = "x",
            StartedUtc = startUtc, StoppedUtc = startUtc.AddSeconds(durationS), DurationS = durationS, SizeBytes = sizeBytes,
            Chunks = 3, Sha256 = Sha256Hex.OfFile(webm), Truncated = truncated, MaxBytes = 2048L * 1024 * 1024, StopReason = truncated ? "limit" : "client", Record = record,
        };
        var json = Path.Combine(dir, mediaId + ".json");
        sc.Save(json);
        return (webm, json, sc);
    }

    [Fact]
    public void Media_PackedWithSidecar_LinkedToExercise_RoundTrip_AndDeletedAfterReport()
    {
        var (ship, mgr) = ShipWithRecords();
        // 錄影 A:旁檔以紀錄檔名對應 E01;錄影 B:檔名不符(教官站給錯)但 header.createdUtc 相同 → 對應 E07;錄影 C:無對應(已截斷)
        var (webmA, _, scA) = WriteMedia(ship.RecordsDir, "20261012-013005-instructor-E01-aaaaaa", 4096, 3600.5, new MediaRecordRef { File = "20261012-013000-E01_baseline.jsonl", CreatedUtc = Start, ScenarioId = "E01_baseline" });
        WriteMedia(ship.RecordsDir, "20261013-020005-instructor-E07-bbbbbb", 2048, 1700, new MediaRecordRef { File = "wrong.jsonl", CreatedUtc = Start.AddDays(1) }, started: Start.AddDays(1).AddSeconds(5));
        WriteMedia(ship.RecordsDir, "20261014-000000-instructor-x-cccccc", 512, 20, null, truncated: true, started: Start.AddDays(2));
        // 進行中的錄影(.part、沒有旁檔的 .webm)不打包
        File.WriteAllBytes(Path.Combine(ship.RecordsDir, "media", "running.webm.part"), new byte[10]);
        File.WriteAllBytes(Path.Combine(ship.RecordsDir, "media", "nosidecar.webm"), new byte[10]);

        var p = mgr.PackPending();
        Assert.NotNull(p);
        Assert.Equal(3 + 6, p!.Files.Count);
        Assert.Equal(3, p.Files.Count(f => f.Kind == "media"));
        Assert.Equal(3, p.Files.Count(f => f.Kind == "media-meta"));
        Assert.DoesNotContain(p.Files, f => f.Path.Contains("running") || f.Path.Contains("nosidecar"));
        Assert.Null(mgr.PackPending());

        var store = new RecordStore(_env.PathIn("store"));
        var r = store.Import(mgr.PackagePath(p.PackageId)!, enrollUnknownDevice: true);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        var m = r.Manifest!;
        Assert.Equal(3, m.Media.Count);
        var e01 = Assert.Single(m.Exercises, e => e.ScenarioId == "E01_baseline");
        var a = Assert.Single(e01.Media);
        Assert.Equal("records/media/20261012-013005-instructor-E01-aaaaaa.webm", a.File);
        Assert.Equal("records/media/20261012-013005-instructor-E01-aaaaaa.json", a.Sidecar);
        Assert.Equal(scA.Sha256, a.Sha256);
        Assert.Equal(4096, a.SizeBytes);
        Assert.Equal(3600.5, a.DurationS);
        Assert.False(a.DurationEstimated);
        Assert.False(a.Truncated);
        Assert.False(a.Omitted);
        Assert.Equal("instructor", a.Station);
        Assert.Equal("20261012-013000-E01_baseline.jsonl", a.Record);
        Assert.Equal(Start, a.RecordCreatedUtc);
        Assert.Equal(Start.AddSeconds(5), a.StartUtc);
        var e07 = Assert.Single(m.Exercises, e => e.ScenarioId == "E07_steering_failure");
        var b = Assert.Single(e07.Media);
        Assert.Equal("20261013-020005-instructor-E07-bbbbbb", b.MediaId);
        var c = Assert.Single(m.Media, x => x.MediaId == "20261014-000000-instructor-x-cccccc");
        Assert.True(c.Truncated);
        Assert.Null(c.Record);
        Assert.DoesNotContain(m.Exercises, e => e.Media.Any(x => x.MediaId == c.MediaId));
        // 錄影檔與 manifest 的雜湊一致;匯入解開到 records/media/
        Assert.Equal(a.Sha256, Assert.Single(m.Files, f => f.Path == a.File).Sha256);
        var dir = Path.Combine(store.Root, r.Directory!);
        Assert.True(File.Exists(Path.Combine(dir, "media", "20261012-013005-instructor-E01-aaaaaa.webm")));
        Assert.True(File.Exists(Path.Combine(dir, "media", "20261012-013005-instructor-E01-aaaaaa.json")));
        Assert.Equal(File.ReadAllBytes(webmA), File.ReadAllBytes(Path.Combine(dir, "media", "20261012-013005-instructor-E01-aaaaaa.webm")));

        // simosa-pkg inspect 顯示錄影
        var o = new StringWriter();
        var code = new CliApp(o, new StringWriter()).Run(new[] { "inspect", mgr.PackagePath(p.PackageId)! });
        Assert.Equal(0, code);
        var text = o.ToString();
        Assert.Contains("錄影 3 段(包內 3、只有旁檔 0)", text);
        Assert.Contains("練習 E01_baseline", text);
        Assert.Contains("1 h 00 min", text);
        Assert.Contains("已截斷(上傳超過上限)", text);

        // 已備份回報後連錄影一起刪除
        using var key = _env.Primary();
        store.MarkBackedUp(null, "加密外接碟 #1");
        var res = mgr.AcceptBackupReport(Report(store, "FSB1", key));
        Assert.True(res.Ok, string.Join(";", res.Errors));
        Assert.Equal(9, res.DeletedFiles.Count);
        Assert.False(File.Exists(webmA));
        Assert.False(File.Exists(Path.Combine(ship.RecordsDir, "media", "20261012-013005-instructor-E01-aaaaaa.json")));
        Assert.True(File.Exists(Path.Combine(ship.RecordsDir, "media", "nosidecar.webm")));
    }

    [Fact]
    public void Media_OverPackageLimit_OnlySidecarPacked_FlaggedOmitted_StaysOnShip()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "20261012-013000-E01_baseline.jsonl", "E01_baseline", Start, 600);
        var (webm, json, _) = WriteMedia(ship.RecordsDir, "20261012-013005-instructor-E01-dddddd", 3 * 1024 * 1024, 600, new MediaRecordRef { File = "20261012-013000-E01_baseline.jsonl", CreatedUtc = Start });
        var mgr = new ShipRecordManager(ship) { MediaMaxBytes = 1024 * 1024 };
        var over = Assert.Single(mgr.MediaOverLimit());
        Assert.Equal("media/20261012-013005-instructor-E01-dddddd.webm", over.Path);

        var p = mgr.PackPending()!;
        Assert.Equal(2, p.Files.Count); // 紀錄 + 旁檔
        Assert.DoesNotContain(p.Files, f => f.Kind == "media");
        Assert.Contains(p.Files, f => f.Kind == "media-meta");
        var store = new RecordStore(_env.PathIn("store"));
        var r = store.Import(mgr.PackagePath(p.PackageId)!, enrollUnknownDevice: true);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        var x = Assert.Single(r.Manifest!.Media);
        Assert.True(x.Omitted);
        Assert.Contains("超過紀錄包上限 1 MB", x.OmitReason);
        Assert.Equal(3 * 1024 * 1024, x.SizeBytes); // 旁檔記的大小與雜湊仍在,方便日後以外接碟搬移後核對
        Assert.NotNull(x.Sha256);
        var e01 = Assert.Single(r.Manifest.Exercises);
        Assert.True(Assert.Single(e01.Media).Omitted);
        Assert.Contains(mgr.RetentionWarnings(), w => w.Contains("1 段錄影超過紀錄包上限"));
        Assert.Null(mgr.PackPending()); // 只剩超限錄影:不再產生空包

        // 回報後:紀錄刪除;超限錄影留在船上,旁檔隨之保留(待錄影檔打包後一併刪除)
        using var key = _env.Primary();
        store.MarkBackedUp(null, null);
        var res = mgr.AcceptBackupReport(Report(store, "FSB1", key));
        Assert.True(res.Ok);
        Assert.Equal(new[] { "20261012-013000-E01_baseline.jsonl" }, res.DeletedFiles);
        Assert.Equal(new[] { "media/20261012-013005-instructor-E01-dddddd.json" }, res.RetainedFiles);
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(webm));

        // 放寬上限(--media-max-mb)後補打包錄影檔:摘要仍自船上旁檔讀出並標已於先前打包的旁檔
        var later = new ShipRecordManager(ship) { MediaMaxBytes = 4L * 1024 * 1024 };
        var p2 = later.PackPending();
        Assert.NotNull(p2);
        Assert.Equal("media", Assert.Single(p2!.Files).Kind);
        var r2 = store.Import(later.PackagePath(p2.PackageId)!);
        Assert.True(r2.Ok, string.Join(";", r2.Errors));
        var y = Assert.Single(r2.Manifest!.Media);
        Assert.False(y.Omitted);
        Assert.Equal(600, y.DurationS);
        Assert.True(File.Exists(Path.Combine(store.Root, r2.Directory!, "media", "20261012-013005-instructor-E01-dddddd.webm")));
        // 第二包回報後錄影檔與旁檔一起刪除
        store.MarkBackedUp(null, null);
        var res2 = later.AcceptBackupReport(Report(store, "FSB1", key));
        Assert.True(res2.Ok, string.Join(";", res2.Errors));
        Assert.False(File.Exists(webm));
        Assert.False(File.Exists(json));
        Assert.Empty(Directory.GetFiles(Path.Combine(ship.RecordsDir, "media")));

        // CLI:records pack --media-max-mb 與 inspect 的未納入說明
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(0, new CliApp(o, e).Run(new[] { "inspect", mgr.PackagePath(p.PackageId) ?? Path.Combine(store.Root, r.Directory!) + ".sbrec" }));
        Assert.Contains("只有旁檔 1", o.ToString());
        Assert.Contains("未納入:", o.ToString());
        Assert.Equal(0, new CliApp(o, e).Run(new[] { "records", "list", "--root", ship.Root, "--media-max-mb", "1" }));
        Assert.Equal(2, new CliApp(o, e).Run(new[] { "records", "pack", "--root", ship.Root, "--media-max-mb", "x" })); // 用法錯誤
    }
}
