using System.Net;
using System.Net.Sockets;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Transfer;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>TCP 直連同步本機迴路端對端(含錯誤 nonce 被拒)與隨身碟備援。</summary>
public sealed class SyncTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc);

    private async Task<SyncServer> StartServer(InstallLayout ship, TimeProvider? clock = null)
    {
        var server = new SyncServer(new ShipNode(ship, clock), IPAddress.Loopback, 0) { IdleTimeout = TimeSpan.FromSeconds(20) };
        server.Start();
        await Task.Yield();
        return server;
    }

    [Fact]
    public async Task EndToEnd_PushPullReportCleanup()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 3600, new object[] { "王大副" });
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero));
        await using var server = await StartServer(ship, clock);
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var pkg = _env.SoftwarePackage("2026.10.1", key);

        // 第一次訪船:推送更新包、取回紀錄(首次登錄裝置)
        var rep1 = await SyncClient.SyncAsync(new SyncOptions
        {
            Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store, PushPackages = { pkg }, EnrollDevice = true,
        });
        Assert.True(rep1.Ok);
        Assert.True(rep1.DeviceEnrolled);
        Assert.Contains("訪船船長(測試)", rep1.AuthenticatedAs);
        var upd = Assert.Single(rep1.Updates);
        Assert.True(upd.Ok, string.Join(";", upd.Errors));
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        var pulled = Assert.Single(rep1.Pulled);
        Assert.True(pulled.Ok, string.Join(";", pulled.Errors));
        Assert.Equal(1.0, pulled.TrainingHours, 3);
        Assert.Null(rep1.Cleanup); // 尚未備份
        Assert.Equal("2026.10.1", store.LoadFleet().Ships["FSB1"].Version);
        Assert.True(File.Exists(Path.Combine(ship.RecordsDir, "r1.jsonl")));

        // 岸上備份後,第二次訪船:送出已備份回報 → 船上刪除副本
        store.MarkBackedUp(null, "加密外接碟");
        clock.Advance(TimeSpan.FromDays(14));
        var rep2 = await SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store });
        Assert.True(rep2.Ok);
        Assert.Empty(rep2.Pulled);
        Assert.NotNull(rep2.Cleanup);
        Assert.Equal(pulled.PackageId, Assert.Single(rep2.Cleanup!.Deleted));
        Assert.False(File.Exists(Path.Combine(ship.RecordsDir, "r1.jsonl")));
        Assert.NotNull(store.LoadLedger().Packages.Single().ShipDeletedUtc);

        var audit = ship.Audit().ReadAll().Select(l => l["event"]!.GetValue<string>()).ToList();
        Assert.Contains(AuditEvents.SyncSessionStart, audit);
        Assert.Contains(AuditEvents.UpdateApplied, audit);
        Assert.Contains(AuditEvents.RecordsDeleted, audit);
        Assert.Equal(2, new ShipRecordManager(ship).LoadLedger().CurrentVisit);
    }

    [Fact]
    public async Task WrongNonceSignature_Rejected()
    {
        var ship = _env.NewShip();
        await using var server = await StartServer(ship);
        using var key = _env.Primary();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var s = tcp.GetStream();
        var clientNonce = SyncProtocol.NewNonce();
        await SyncProtocol.WriteAsync(s, new HelloMessage { ClientNonce = clientNonce }, default);
        var ch = SyncProtocol.As<ChallengeMessage>(await SyncProtocol.ReadAsync(s, default));
        // 簽一個不是船端提供的 nonce(例如重播先前錄下的握手)
        var wrong = SyncProtocol.NewNonce();
        var sig = key.Sign(SyncProtocol.InstructorAuthPayload(ch.ShipId, ch.DeviceId, wrong, clientNonce));
        await SyncProtocol.WriteAsync(s, new AuthMessage { KeyId = key.KeyId, Signature = Convert.ToBase64String(sig) }, default);
        var reply = await SyncProtocol.ReadAsync(s, default);
        Assert.Equal(SyncTypes.Error, SyncProtocol.TypeOf(reply));
        Assert.Equal(SyncErrors.AuthFailed, SyncProtocol.As<ErrorMessage>(reply).Code);
        await Task.Delay(100);
        var failed = Assert.Single(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.SyncAuthFailed);
        Assert.Equal("bad-signature", failed["details"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task UntrustedInstructorKey_Rejected()
    {
        var ship = _env.NewShip();
        await using var server = await StartServer(ship);
        var (priv, _) = TestEnv.NewKey(KeyRoles.Primary, "陌生人");
        using var stranger = new PemKeyProvider(priv);
        var ex = await Assert.ThrowsAsync<SyncException>(() => SyncClient.SyncAsync(new SyncOptions
        {
            Host = "127.0.0.1", Port = server.Port, InstructorKey = stranger, Store = new RecordStore(_env.PathIn("store")), EnrollDevice = true,
        }));
        Assert.Equal(SyncErrors.UntrustedKey, ex.Code);
    }

    [Fact]
    public async Task UnregisteredShipDevice_RejectedByInstructor()
    {
        var ship = _env.NewShip();
        await using var server = await StartServer(ship);
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var (_, otherPub) = TestEnv.NewKey(KeyRoles.Primary, "x");
        store.RegisterDevice("FSB1", "FSB1-LT01", otherPub).Dispose(); // 登錄的是別台筆電
        var ex = await Assert.ThrowsAsync<SyncException>(() => SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store }));
        Assert.Equal(SyncErrors.AuthFailed, ex.Code);
        var ex2 = await Assert.ThrowsAsync<SyncException>(() => SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = new RecordStore(_env.PathIn("store2")) }));
        Assert.Equal(SyncErrors.UntrustedKey, ex2.Code);
    }

    [Fact]
    public async Task RejectedUpdate_ReportedButSessionContinues()
    {
        var ship = _env.NewShip();
        await using var server = await StartServer(ship);
        using var key = _env.Primary();
        var good = _env.SoftwarePackage("2026.10.2", key, "app 2.0\n");
        var (priv, _) = TestEnv.NewKey(KeyRoles.Primary, "陌生人");
        using var stranger = new PemKeyProvider(priv);
        var bad = _env.SoftwarePackage("2026.10.3", stranger, "evil\n", "bad.sbupd");
        var rep = await SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = new RecordStore(_env.PathIn("store")), EnrollDevice = true, PushPackages = { bad, good } });
        Assert.Equal(2, rep.Updates.Count);
        Assert.True(rep.Updates[0].Ok);  // 依版本排序:2026.10.2 先
        Assert.False(rep.Updates[1].Ok);
        Assert.False(rep.Ok);
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
    }

    [Fact]
    public void UsbFallback_RoundTrip()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 1200);
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var usb = _env.PathIn("usb");
        var node = new ShipNode(ship);

        UsbTransfer.InstructorExport(store, usb, new[] { _env.SoftwarePackage("2026.10.1", key) }, key);
        var shipSide = UsbTransfer.ShipImport(node, usb);
        Assert.True(Assert.Single(shipSide.Updates).Ok);
        Assert.Contains(shipSide.Written, w => w.EndsWith(".sbrec"));

        var inst = UsbTransfer.InstructorImport(store, usb, enrollDevice: true);
        Assert.Empty(inst.Errors);
        Assert.True(Assert.Single(inst.Imported).Ok);
        Assert.Equal("2026.10.1", store.LoadFleet().Ships["FSB1"].Version);

        // 下次:備份後以隨身碟送回報 → 船上清理;已套用的舊更新包略過
        store.MarkBackedUp(null, "外接碟");
        UsbTransfer.InstructorExport(store, usb, Array.Empty<string>(), key);
        var second = UsbTransfer.ShipImport(node, usb);
        Assert.Empty(second.Updates);
        Assert.Single(second.Cleanup!.Deleted);
        Assert.False(File.Exists(Path.Combine(ship.RecordsDir, "r1.jsonl")));
    }
}
