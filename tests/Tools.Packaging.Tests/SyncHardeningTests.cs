using System.Net;
using System.Net.Sockets;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Transfer;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 直連同步與隨身碟途徑的強化(審查發現的回歸測試):未驗證連線不能灌爆稽核紀錄、訪船與 session 分開計算、
/// 隨身碟途徑回寫船上已刪除狀態。
/// </summary>
public sealed class SyncHardeningTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc);

    private static async Task<SyncServer> StartServer(ShipNode node)
    {
        var server = new SyncServer(node, IPAddress.Loopback, 0) { IdleTimeout = TimeSpan.FromSeconds(20), AuthFailureBackoff = TimeSpan.Zero };
        server.Start();
        await Task.Yield();
        return server;
    }

    /// <summary>未驗證者送巨大或格式錯誤的 auth:稽核紀錄大小有上限,對方提供的欄位被截斷。</summary>
    [Fact]
    public async Task UnauthenticatedFlood_DoesNotGrowAuditUnbounded()
    {
        var ship = _env.NewShip();
        await using var server = await StartServer(new ShipNode(ship));
        var big = new string('A', 960_000);
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
                await using var s = tcp.GetStream();
                await SyncProtocol.WriteAsync(s, new HelloMessage { ClientNonce = SyncProtocol.NewNonce() }, default);
                await SyncProtocol.ReadAsync(s, default);
                var signer = i % 4 == 0 ? big : new string('A', 1500);
                await SyncProtocol.WriteAsync(s, new AuthMessage { KeyId = new string('B', 2000), Signer = signer, Signature = "x" }, default);
                try { await SyncProtocol.ReadAsync(s, default); }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException) { }
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException) { }
        }
        await Task.Delay(200);
        var size = new FileInfo(ship.AuditPath).Length;
        Assert.True(size < 64 * 1024, $"audit.jsonl 大小 {size}");
        foreach (var l in ship.Audit().ReadAll().Where(l => l["event"]!.GetValue<string>() == AuditEvents.SyncAuthFailed))
        {
            var d = l["details"]!;
            if (d["keyId"] is { } k) Assert.Equal("(invalid)", k.GetValue<string>());
            if (d["claimedSigner"] is { } cs) Assert.True(cs.GetValue<string>().Length <= 130);
        }

        // 合法的教官仍可連線
        using var key = _env.Primary();
        var rep = await SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = new RecordStore(_env.PathIn("store")), EnrollDevice = true });
        Assert.True(rep.Ok);
    }

    /// <summary>同一次訪船重連(補推更新包)不另計訪船;正常流程(下次訪船即回報清理)不發超限警告。</summary>
    [Fact]
    public async Task ReconnectWithinVisit_CountsAsOneVisit_NoFalseRetentionWarning()
    {
        var ship = _env.NewShip(maxVisitIntervals: 2);
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 600);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero));
        await using var server = await StartServer(new ShipNode(ship, clock));
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));

        // 第 1 次訪船:session 1 取回紀錄;20 分鐘後 session 2 只推送更新包
        var s1 = await SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store, EnrollDevice = true });
        Assert.True(s1.Ok);
        Assert.Single(s1.Pulled);
        clock.Advance(TimeSpan.FromMinutes(20));
        var s2 = await SyncClient.SyncAsync(new SyncOptions
        {
            Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store, PushPackages = { _env.SoftwarePackage("2026.10.1", key) }, PullRecords = false,
        });
        Assert.True(s2.Ok);
        Assert.Empty(s2.Warnings);

        // 岸上備份;下次訪船(兩週後)回報 → 船上清理,不應警告超限
        store.MarkBackedUp(null, "外接碟");
        clock.Advance(TimeSpan.FromDays(14));
        var v2 = await SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store });
        Assert.True(v2.Ok);
        Assert.Single(v2.Cleanup!.Deleted);
        Assert.DoesNotContain(v2.Warnings, w => w.Contains("訪船間隔"));
        var ledger = new ShipRecordManager(ship).LoadLedger();
        Assert.Equal(2, ledger.CurrentVisit);
    }

    /// <summary>真的累積超過上限個訪船間隔時仍要警告(訪船以時間窗合併後)。</summary>
    [Fact]
    public void RetentionWarning_StillRaisedAcrossRealVisits()
    {
        var ship = _env.NewShip(maxVisitIntervals: 2);
        TestEnv.WriteRecord(ship.RecordsDir, "a.jsonl", "E01_baseline", Start, 600);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var mgr = new ShipRecordManager(ship, clock);
        Assert.Equal(1, mgr.BeginVisit("tcp"));
        Assert.Equal(1, mgr.BeginVisit("tcp")); // 同一次訪船重連
        var p = mgr.PackPending()!;
        clock.Advance(TimeSpan.FromDays(14));
        Assert.Equal(2, mgr.BeginVisit("usb"));
        Assert.Empty(mgr.RetentionWarnings());
        clock.Advance(TimeSpan.FromDays(14));
        Assert.Equal(3, mgr.BeginVisit("tcp"));
        Assert.Contains(p.PackageId, Assert.Single(mgr.RetentionWarnings()));
    }

    /// <summary>隨身碟途徑:船上清理結果(裝置簽章)回寫隨身碟,教官匯入後標記船上已刪除,不再重複回報。</summary>
    [Fact]
    public void Usb_ShipCleanup_IsReportedBackToInstructor()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 1200);
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var usb = _env.PathIn("usb");
        var node = new ShipNode(ship);

        UsbTransfer.ShipImport(node, usb);
        Assert.Empty(UsbTransfer.InstructorImport(store, usb, enrollDevice: true).Errors);
        store.MarkBackedUp(null, "外接碟");
        UsbTransfer.InstructorExport(store, usb, Array.Empty<string>(), key);
        var second = UsbTransfer.ShipImport(node, usb);
        Assert.Single(second.Cleanup!.Deleted);
        Assert.Empty(new ShipRecordManager(ship).Outstanding());

        var back = UsbTransfer.InstructorImport(store, usb);
        Assert.Empty(back.Errors);
        Assert.NotNull(store.LoadLedger().Packages.Single().ShipDeletedUtc);
        Assert.Null(store.CreateBackupReport("FSB1", key));
    }

    /// <summary>隨身碟上偽造的清理回報(非登錄裝置簽章)不得標記船上已刪除。</summary>
    [Fact]
    public void Usb_ForgedCleanupNotice_Ignored()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 1200);
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var usb = _env.PathIn("usb");
        UsbTransfer.ShipImport(new ShipNode(ship), usb);
        Assert.Empty(UsbTransfer.InstructorImport(store, usb, enrollDevice: true).Errors);
        store.MarkBackedUp(null, "外接碟");
        var pkgId = store.LoadLedger().Packages.Single().PackageId;

        var (priv, _) = TestEnv.NewKey(Crypto.KeyRoles.Primary, "冒充的筆電");
        using var fake = new Crypto.PemKeyProvider(priv);
        var notice = new CleanupNotice { ShipId = "FSB1", DeviceId = "FSB1-LT01", ReportId = "bak-x", Deleted = { pkgId }, CreatedUtc = DateTime.UtcNow };
        var dir = Path.Combine(usb, UsbTransfer.RootName, "from-ship", "FSB1");
        File.WriteAllBytes(Path.Combine(dir, "bak-x" + CleanupNotice.Extension), Crypto.SignedDocument.Create(notice, fake, DateTime.UtcNow).ToBytes());

        var r = UsbTransfer.InstructorImport(store, usb);
        Assert.NotEmpty(r.Errors);
        Assert.Null(store.LoadLedger().Packages.Single().ShipDeletedUtc);
    }
}
