using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;

namespace SimosaBRM.Packaging.Tests;

/// <summary>紀錄包往返、裝置簽章、已備份回報後清理與保留上限(規劃書第 5.5 節「紀錄回傳」)。</summary>
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
}
