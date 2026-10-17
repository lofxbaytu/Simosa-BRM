using System.IO.Compression;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Records;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 紀錄包的完整性與清理(審查發現的回歸測試):zip 內重複 manifest 被拒且紀錄庫只保存已驗證的 manifest、
/// 訓練時數以牆鐘起訖計算(倍速/凍結)、已備份回報清理時單一檔案刪除失敗不中斷且帳冊一致。
/// </summary>
public sealed class RecordIntegrityTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void DuplicateManifestEntry_InRecordPackage_Rejected()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "a.jsonl", "E01_baseline", Start, 3600, new object[] { "王大副" });
        var mgr = new ShipRecordManager(ship);
        var p = mgr.PackPending()!;
        var copy = _env.PathIn("dup.sbrec");
        File.Copy(mgr.PackagePath(p.PackageId)!, copy);
        TestEnv.AddZipEntry(copy, "manifest.json", "{\"format\":\"simosa-records/1\",\"note\":\"FORGED\"}"u8.ToArray());

        var store = new RecordStore(_env.PathIn("store"));
        var r = store.Import(copy, enrollUnknownDevice: true);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("重複"));
        Assert.Empty(store.LoadLedger().Packages);
    }

    [Fact]
    public void ImportedManifest_IsTheVerifiedBytes()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "a.jsonl", "E01_baseline", Start, 3600);
        var mgr = new ShipRecordManager(ship);
        var p = mgr.PackPending()!;
        var path = mgr.PackagePath(p.PackageId)!;
        byte[] signed;
        using (var zip = ZipFile.OpenRead(path))
            signed = Updates.UpdatePackageReader.ReadSigned(zip).ManifestBytes;
        var store = new RecordStore(_env.PathIn("store"));
        var r = store.Import(path, enrollUnknownDevice: true);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.Equal(signed, File.ReadAllBytes(Path.Combine(store.Root, r.Directory!, "manifest.json")));
    }

    // ── 訓練時數:牆鐘 ──────────────────────────────────────

    [Fact]
    public void TrainingHours_UseWallClock_WhenTimeScaled()
    {
        // 以 10 倍速錄 120 秒模擬時間:牆鐘只過 12 秒(紀錄檔最後修改時間)
        var dir = _env.PathIn("rec");
        var path = TestEnv.WriteRecord(dir, "fast.jsonl", "E01_baseline", Start, 120, new object[] { "王大副" }, wallS: 12);
        var s = RecordInspector.Inspect(path, "records/fast.jsonl");
        Assert.Equal(12, s.DurationS!.Value, 3);
        Assert.Equal(120, s.SimDurationS!.Value, 3);
        Assert.Equal(Start.AddSeconds(12), s.EndUtc);
        Assert.True(s.EndEstimated);
        var hours = RecordInspector.Summarize(new[] { s });
        Assert.Equal(12, hours.TotalS, 3);
        Assert.Equal(Start.AddSeconds(12), Assert.Single(Assert.Single(hours.Participants).Sessions).EndUtc);
    }

    [Fact]
    public void TrainingHours_UseWallClock_WhenFrozen()
    {
        // 教官凍結講解:模擬 600 秒、牆鐘 1500 秒;footer 寫有 closedUtc 時以它為準
        var dir = _env.PathIn("rec");
        var path = TestEnv.WriteRecord(dir, "frozen.jsonl", "E01_baseline", Start, 600, closedUtc: Start.AddSeconds(1500), wallS: 1600);
        var s = RecordInspector.Inspect(path, "records/frozen.jsonl");
        Assert.Equal(1500, s.DurationS!.Value, 3);
        Assert.Equal(600, s.SimDurationS!.Value, 3);
        Assert.Equal(Start.AddSeconds(1500), s.EndUtc);
        Assert.False(s.EndEstimated);
    }

    // ── 已備份回報清理:單檔刪除失敗 ─────────────────────────

    [Fact]
    public void BackupReport_FileDeleteFailure_KeepsPackageForRetry()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "a.jsonl", "E01_baseline", Start, 600);
        TestEnv.WriteRecord(ship.RecordsDir, "b.jsonl", "E01_baseline", Start.AddHours(1), 600);
        var locked = Path.Combine(ship.RecordsDir, "b.jsonl");
        var mgr = new ShipRecordManager(ship)
        {
            FileDeleter = f =>
            {
                if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(locked), StringComparison.Ordinal))
                    throw new IOException("The process cannot access the file because it is being used by another process.");
                File.Delete(f);
            },
        };
        var p = mgr.PackPending()!;
        var store = new RecordStore(_env.PathIn("store"));
        Assert.True(store.Import(mgr.PackagePath(p.PackageId)!, true).Ok);
        store.MarkBackedUp(null, "外接碟");
        using var key = _env.Primary();

        var res = mgr.AcceptBackupReport(store.CreateBackupReport("FSB1", key)!.Value.Document.ToBytes());
        Assert.True(res.Ok, string.Join(";", res.Errors));
        Assert.Empty(res.Deleted);
        Assert.Contains("a.jsonl", res.DeletedFiles);
        Assert.Contains(res.Failed, f => f.Contains("b.jsonl"));
        Assert.NotEmpty(res.Warnings);
        Assert.False(File.Exists(Path.Combine(ship.RecordsDir, "a.jsonl")));
        Assert.True(File.Exists(locked));
        // 紀錄包副本保留、帳冊未標刪除,仍列為待清理(下次回報重試)
        Assert.NotNull(mgr.PackagePath(p.PackageId));
        Assert.Null(mgr.LoadLedger().Packages.Single().DeletedUtc);
        Assert.Single(mgr.Outstanding());

        // 檔案解除鎖定後重試:清理完成
        var retry = new ShipRecordManager(ship).AcceptBackupReport(store.CreateBackupReport("FSB1", key)!.Value.Document.ToBytes());
        Assert.True(retry.Ok);
        Assert.Equal(p.PackageId, Assert.Single(retry.Deleted));
        Assert.False(File.Exists(locked));
        Assert.Null(mgr.PackagePath(p.PackageId));
        Assert.NotNull(mgr.LoadLedger().Packages.Single().DeletedUtc);
    }
}
