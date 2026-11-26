using System.Net;
using System.Text.RegularExpressions;
using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Sync;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 狀態檔的跨行程鎖 — 審查後修正的部分(docs/design/packaging-sync.md 第 5.2 節;共用的鎖與原有三把鎖見 <see cref="StateLockTests"/>):
/// 直連同步送出已備份回報之前先拿到帳冊的鎖、trust add / remove 取船上的鎖、稽核紀錄附加寫入的鎖、備份紀錄庫時的兩船版次清單與稽核紀錄、
/// 已在紀錄庫的紀錄包不取鎖、鎖定檔建立不了時報真正的原因、同步服務把等鎖的那一句寫進自己的紀錄。
/// 「另一個行程佔著鎖」同樣以測試自己獨占開啟鎖定檔代表。
/// </summary>
public sealed class StateLockReviewTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

    private static FileStream OtherProcessHolds(string lockPath) => StateLockTests.OtherProcessHolds(lockPath);

    private static bool CanOpenExclusively(string lockPath) => StateLockTests.CanOpenExclusively(lockPath);

    /// <summary>船上打包一個紀錄包,回傳 .sbrec 的路徑</summary>
    private string PackOne(InstallLayout ship, string recordName)
    {
        TestEnv.WriteRecord(ship.RecordsDir, recordName + ".jsonl", "E01_baseline", Start, 600);
        var p = new ShipRecordManager(ship).PackPending();
        Assert.NotNull(p);
        return Path.Combine(ship.OutboxDir, p!.FileName);
    }

    private static List<string?> AuditEventsIn(string auditPath)
        => File.ReadAllLines(auditPath).Where(l => l.Length > 0).Select(l => (string?)System.Text.Json.Nodes.JsonNode.Parse(l)!["event"]).ToList();

    // ── R1:直連同步的已備份回報 ──────────────────────────

    [Fact]
    public async Task Sync_LedgerBusyWhenTheBackupReportIsDue_NothingIsSentOrDeleted_AndTheNextSyncCompletesIt()
    {
        // 原本:船上回覆「已刪除」之後才取帳冊的鎖做標記。另一個行程佔著帳冊超過上限(備份紀錄庫複製到很慢的外接碟)時,船上已經刪了、
        // 教官端沒有記到,同步以錯誤結束 — 下一次同步船上的待回傳清單已經沒有這些紀錄包,帳冊永遠停在「船上 保留」。
        // 現在先拿到鎖才送:拿不到就不送(船上什麼都不刪),列為這次同步失敗的項目,其餘步驟照常;下一次同步補上
        var ship = _env.NewShip();
        PackOne(ship, "r1");
        var server = new SyncServer(new ShipNode(ship), IPAddress.Loopback, 0, TextWriter.Null) { IdleTimeout = TimeSpan.FromSeconds(30) };
        server.Start();
        await using var _server = server;
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var clientLog = new StringWriter();
        SyncOptions Options() => new() { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store, EnrollDevice = true, Log = clientLog };

        var first = await SyncClient.SyncAsync(Options());
        Assert.True(first.Ok);
        var packageId = Assert.Single(first.Pulled).PackageId;
        Assert.Null(first.Cleanup);                                                              // 還沒有完成岸上備份:沒有回報可送

        // 沒有可以回報的紀錄包時,這一步不碰帳冊的鎖:備份紀錄庫進行中的同步不必等它
        store.LedgerLockTimeout = TimeSpan.FromMilliseconds(300);
        using (OtherProcessHolds(store.LedgerLockPath))
        {
            var idle = await SyncClient.SyncAsync(Options());
            Assert.True(idle.Ok);
            Assert.Null(idle.Cleanup);
        }

        store.MarkBackedUp(null, "外接碟");
        SyncReport busy;
        using (OtherProcessHolds(store.LedgerLockPath))
            busy = await SyncClient.SyncAsync(Options());
        Assert.False(busy.Ok);                                                                   // 指令列:「同步完成,但有項目失敗」,結束碼 1
        Assert.NotNull(busy.Cleanup);
        Assert.False(busy.Cleanup!.Ok);
        Assert.StartsWith("這次沒有送出(船上的紀錄包都還在,下一次同步會再送)— 另一個 simosa-pkg 正在使用這個紀錄庫的帳冊", Assert.Single(busy.Cleanup.Errors));
        Assert.Contains("  已備份回報:這次沒有送出(船上的紀錄包都還在,下一次同步會再送)— 另一個 simosa-pkg 正在使用這個紀錄庫的帳冊", clientLog.ToString());
        Assert.True(busy.FleetSaved);                                                            // 同步的其餘步驟照常(兩船版次清單有更新)
        Assert.Equal(packageId, Assert.Single(new ShipRecordManager(ship).Outstanding()).PackageId);   // 船上沒有刪
        Assert.Null(store.LoadLedger().Packages.Single().ShipDeletedUtc);
        Assert.DoesNotContain(AuditEvents.StoreReportIssued, AuditEventsIn(store.Audit.Path));  // 沒有產生回報
        Assert.DoesNotContain(AuditEvents.BackupReportAccepted, AuditEventsIn(ship.AuditPath));

        // 另一個行程結束之後再同步一次:回報送出、船上刪除、教官端記到「船上已刪除」
        store.LedgerLockTimeout = TimeSpan.FromSeconds(30);
        var done = await SyncClient.SyncAsync(Options());
        Assert.True(done.Ok, string.Join(";", done.Cleanup?.Errors ?? new()));
        Assert.Equal(new[] { packageId }, done.Cleanup!.Deleted);
        Assert.Empty(new ShipRecordManager(ship).Outstanding());
        var entry = store.LoadLedger().Packages.Single();
        Assert.NotNull(entry.ShipDeletedUtc);
        Assert.Equal(done.Cleanup.ReportId, entry.ReportedId);
        Assert.True(CanOpenExclusively(store.LedgerLockPath));                                  // 同步結束後放開了
    }

    /// <summary>代表船上:教官端送出回報之後才讀得到回覆;第一次讀的時候(船上正在處理)看一下教官端有沒有拿著帳冊的鎖</summary>
    private sealed class ShipReplyStream : Stream
    {
        private readonly MemoryStream _reply;
        private readonly Action _whileShipWorks;
        private bool _probed;

        public ShipReplyStream(byte[] reply, Action whileShipWorks)
        {
            _reply = new MemoryStream(reply);
            _whileShipWorks = whileShipWorks;
        }

        public long Written { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_probed)
            {
                _probed = true;
                _whileShipWorks();
            }
            return _reply.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SendBackupReport_HoldsTheLedgerLockUntilTheShipDeletionIsRecorded()
    {
        // 「產生回報 → 送出 → 船上刪除 → 標記船上已刪除」整段拿著帳冊的鎖:船上處理回報的期間別的行程拿不到,所以標記那一步不會等不到
        var ship = _env.NewShip();
        var store = new RecordStore(_env.PathIn("store"));
        var imported = store.Import(PackOne(ship, "r1"), enrollUnknownDevice: true);
        Assert.True(imported.Ok);
        store.MarkBackedUp(null, "外接碟");
        using var key = _env.Primary();
        var reply = new MemoryStream();
        await SyncProtocol.WriteAsync(reply, new CleanupResultMessage { Result = new CleanupResult { Ok = true, ReportId = "bak-test", Deleted = { imported.PackageId! } } }, CancellationToken.None);
        bool? heldWhileShipWorks = null;
        var stream = new ShipReplyStream(reply.ToArray(), () => heldWhileShipWorks = !CanOpenExclusively(store.LedgerLockPath));
        var options = new SyncOptions { Host = "unused", InstructorKey = key, Store = store };

        var (reportId, result) = SyncClient.SendBackupReport(options, stream, "FSB1", new List<string> { imported.PackageId! }, CancellationToken.None);

        Assert.True(heldWhileShipWorks, "船上處理已備份回報的期間,教官端應該拿著帳冊的鎖");
        Assert.True(stream.Written > 0);
        Assert.True(result!.Ok);
        var entry = store.LoadLedger().Packages.Single();
        Assert.NotNull(entry.ShipDeletedUtc);
        Assert.Equal(reportId, entry.ReportedId);
        Assert.True(CanOpenExclusively(store.LedgerLockPath));                                  // 做完之後放開了
        // 進了鎖才發現沒有可以回報的(剛才那一包已經標記船上已刪除):什麼都不送
        var nothing = new ShipReplyStream(Array.Empty<byte>(), () => { });
        Assert.Equal((null, null), SyncClient.SendBackupReport(options, nothing, "FSB1", new List<string> { imported.PackageId! }, CancellationToken.None));
        Assert.Equal(0, nothing.Written);
        Assert.False(store.HasBackupReportFor("FSB1"));
    }

    // ── R2:trust add / trust remove ───────────────────────

    [Fact]
    public async Task Cli_TrustAddAndRemove_TakeTheShipLock_SoATrustUpdateBeingAppliedCannotSwallowThem()
    {
        // 套用信任清單更新包是「複製現行的 config/ → 整個換上去」(在船上的狀態鎖裡)。trust add / remove 原本不取鎖:落在複製與替換之間時
        // 寫進(或刪到)被換下來的那一份 — 指令印「已加入 / 已移除」而信任清單沒有變(真實行程實測 80 次有 3 至 9 次)。現在取同一把鎖
        var ship = _env.NewShip();
        var (_, thirdPub, thirdKeyId) = KeyFiles.Generate(new Dictionary<string, string?> { ["role"] = KeyRoles.Backup, ["name"] = "第三把(測試)" });
        var (_, fourthPub, _) = KeyFiles.Generate(new Dictionary<string, string?> { ["role"] = KeyRoles.Backup, ["name"] = "第四把(測試)" });
        var thirdFile = _env.PathIn("third.pub.pem");
        var fourthFile = _env.PathIn("fourth.pub.pem");
        File.WriteAllText(thirdFile, thirdPub);
        File.WriteAllText(fourthFile, fourthPub);
        var trustedThird = Path.Combine(ship.TrustedKeysDir, "third.pub.pem");
        static (int Code, string Out, string Err) Cli(TimeSpan? lockTimeout, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            return (new CliApp(o, e) { ShipLockTimeout = lockTimeout }.Run(args), o.ToString(), e.ToString());
        }

        // 鎖被佔著(另一個行程正在套用信任清單更新包):trust add 等它做完才寫
        Task<(int Code, string Out, string Err)> add;
        using (OtherProcessHolds(ship.LockPath))
        {
            add = StateLockTests.OnOwnThread(() => Cli(null, "trust", "add", "--root", ship.Root, "--file", thirdFile, "--admin", "t"));
            await Task.Delay(600);
            Assert.False(add.IsCompleted, "船上的狀態鎖被佔用時 trust add 應該等待");
            Assert.False(File.Exists(trustedThird));
        }
        var added = await add.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((0, ""), (added.Code, added.Err));
        Assert.Contains("已加入信任清單:第三把(測試)", added.Out);
        Assert.True(File.Exists(trustedThird));

        // 等不到:原本的錯誤處理(「錯誤:…」、結束碼 1),信任清單與稽核紀錄都沒有改;trust list 只讀,不被擋住
        var auditBefore = File.ReadAllText(ship.AuditPath);
        using (OtherProcessHolds(ship.LockPath))
        {
            foreach (var args in new[]
                     {
                         new[] { "trust", "remove", "--root", ship.Root, "--key-id", thirdKeyId[..16], "--admin", "t" },
                         new[] { "trust", "add", "--root", ship.Root, "--file", fourthFile, "--admin", "t" },
                     })
            {
                var busy = Cli(TimeSpan.FromMilliseconds(300), args);
                Assert.Equal((1, ""), (busy.Code, busy.Out));
                Assert.StartsWith("錯誤:另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態", busy.Err);
                Assert.Contains("這一步沒有執行", busy.Err);
            }
            Assert.True(File.Exists(trustedThird));
            Assert.False(File.Exists(Path.Combine(ship.TrustedKeysDir, "fourth.pub.pem")));
            Assert.Equal(auditBefore, File.ReadAllText(ship.AuditPath));
            var list = Cli(TimeSpan.FromMilliseconds(300), "trust", "list", "--root", ship.Root);
            Assert.Equal(0, list.Code);
            Assert.Contains("third.pub.pem", list.Out);
        }

        // 鎖被佔著時 trust remove 也是等它做完才刪
        Task<(int Code, string Out, string Err)> remove;
        using (OtherProcessHolds(ship.LockPath))
        {
            remove = StateLockTests.OnOwnThread(() => Cli(null, "trust", "remove", "--root", ship.Root, "--key-id", thirdKeyId[..16], "--admin", "t"));
            await Task.Delay(600);
            Assert.False(remove.IsCompleted, "船上的狀態鎖被佔用時 trust remove 應該等待");
            Assert.True(File.Exists(trustedThird));
        }
        var removed = await remove.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((0, ""), (removed.Code, removed.Err));
        Assert.Contains("已自信任清單移除:第三把(測試)", removed.Out);
        Assert.False(File.Exists(trustedThird));
        Assert.Equal(new[] { "add", "remove" }, ship.Audit().ReadAll().Where(e => (string?)e["event"] == AuditEvents.TrustManualChange).Select(e => (string?)e["details"]!["action"]));
    }

    // ── R3:稽核紀錄的附加寫入 ─────────────────────────────

    [Fact]
    public async Task AuditLog_Write_WaitsForAnotherProcessAppending_AndStillWritesWhenTheLockCannotBeTaken()
    {
        // .NET 的附加寫入是「開檔時記下檔尾,再從那裡寫」:兩個行程在同一瞬間寫會寫在同一個位置(真實行程各寫 300 行,每回合少 7 至 144 行、偶爾留下半行)。
        // 現在每一行的寫入在鎖定檔 audit.jsonl.lock 裡
        var path = _env.PathIn("audit", "audit.jsonl");
        var audit = new AuditLog(path);
        audit.Write("first");
        Assert.Equal(path + ".lock", audit.LockPath);
        Assert.True(File.Exists(audit.LockPath));                                                // 鎖定檔留著(0 位元組)
        Assert.Equal(0, new FileInfo(audit.LockPath).Length);

        Task writing;
        using (OtherProcessHolds(audit.LockPath))
        {
            writing = StateLockTests.OnOwnThread(() => audit.Write("waited"));
            await Task.Delay(500);
            Assert.False(writing.IsCompleted, "另一個行程正在附加時應該等它寫完");
            File.AppendAllText(path, "{\"event\":\"other\"}\n");                                  // 另一個行程(持有鎖)寫了一行
            Assert.Equal(new[] { "first", "other" }, AuditEventsIn(path));
            Assert.Single(audit.ReadAll(), e => (string?)e["event"] == "other");                // 讀取不取鎖,也不被等待中的寫入擋住
        }
        await writing.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "first", "other", "waited" }, AuditEventsIn(path));                 // 接在另一個行程那一行的後面,沒有蓋掉它

        // 稽核紀錄不能因為鎖而寫不進去:一直被佔著(超過上限)、或鎖定檔建立不了(位置被一個資料夾占住)時照原本的作法直接附加
        var impatient = new AuditLog(path) { LockTimeout = TimeSpan.FromMilliseconds(300) };
        using (OtherProcessHolds(audit.LockPath)) impatient.Write("lock-busy");
        File.Delete(audit.LockPath);
        Directory.CreateDirectory(audit.LockPath);
        audit.Write("lock-unavailable");
        Assert.Equal(new[] { "first", "other", "waited", "lock-busy", "lock-unavailable" }, AuditEventsIn(path));
    }

    [Fact]
    public async Task AuditLog_ManyWritersToTheSameFile_EveryLineIsThereAndComplete()
    {
        // 多個寫入者(各自的 AuditLog 物件,像不同的指令)同時寫同一個檔:行數對、每一行都是完整的 JSON。
        // 兩個真實行程的對照(修正前少行)見 docs/design/packaging-sync.md 第 5.2 節的清查表
        var path = _env.PathIn("audit-many", "audit.jsonl");
        const int writers = 8, lines = 60;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => StateLockTests.OnOwnThread(() =>
        {
            var audit = new AuditLog(path);
            for (var i = 0; i < lines; i++) audit.Write("test.line", new { writer = w, line = i, padding = new string('x', (w * 13 + i * 7) % 200) });
        })));
        var all = new AuditLog(path).ReadAll();
        Assert.Equal(writers * lines, File.ReadAllLines(path).Length);
        Assert.Equal(writers * lines, all.Count);
        Assert.Equal(writers * lines, all.Select(e => $"{e["details"]!["writer"]}-{e["details"]!["line"]}").Distinct().Count());
    }

    // ── R4:備份紀錄庫時的兩船版次清單、稽核紀錄與暫存檔 ────

    private static List<string> FilesIn(string dir)
        => Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Backup_SkipsHalfWrittenStateFiles_AndCopiesTheFleetListAndAuditLogWhileHoldingTheirLocks()
    {
        var ship = _env.NewShip();
        var store = new RecordStore(_env.PathIn("store"));
        Assert.True(store.Import(PackOne(ship, "r1"), enrollUnknownDevice: true).Ok);
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "1" });

        // 另一個行程寫到一半的帳冊 / 版次清單暫存檔(新的檔名與舊版固定的檔名):不是資料,而且它改名之後就不在了
        // (真實行程實測:備份以「Could not find file '…fleet-versions.json.<8 碼>.tmp'」失敗)。records/ 裡名字像暫存檔的是資料,照常備份
        File.WriteAllText(Path.Combine(store.Root, "fleet-versions.json.1a2b3c4d.tmp"), "{");
        File.WriteAllText(Path.Combine(store.Root, "store-ledger.json.tmp"), "{");
        File.WriteAllText(Path.Combine(store.RecordsDir, "note.tmp"), "data");
        // 直連同步正在下載的紀錄包(incoming/;下載的行程開著它):同樣不是資料 — 原本備份會以「…because it is being used by another process」失敗,
        // 或把這個還沒有驗證的暫存檔備份進去(真實行程:同步取回 170 MB 的紀錄包的期間備份,4 回合都發生)
        Directory.CreateDirectory(store.IncomingDir);
        string target;
        using (new FileStream(Path.Combine(store.IncomingDir, "0123456789abcdef.sbrec"), FileMode.Create, FileAccess.Write, FileShare.None))
            (target, _) = store.Backup(_env.PathIn("backup"));
        var copied = FilesIn(target);
        Assert.DoesNotContain(copied, f => f.StartsWith("incoming/", StringComparison.Ordinal));
        Assert.DoesNotContain("fleet-versions.json.1a2b3c4d.tmp", copied);
        Assert.DoesNotContain("store-ledger.json.tmp", copied);
        Assert.Contains("records/note.tmp", copied);
        Assert.Contains("fleet-versions.json", copied);
        Assert.Contains("audit.jsonl", copied);
        Assert.DoesNotContain(copied, f => f.EndsWith(".lock", StringComparison.Ordinal));     // 三個鎖定檔(帳冊、版次清單、稽核紀錄)都不備份
        Assert.True(File.Exists(store.Audit.LockPath) && File.Exists(store.FleetLockPath) && File.Exists(store.LedgerLockPath));

        // 另一個行程正在更新兩船版次清單(同步的最後一步;不經過帳冊的鎖):備份等它寫完再複製那個檔 — 原本複製到一半被換掉就是「備份後雜湊不符」
        store.FleetLockTimeout = TimeSpan.FromSeconds(20);
        Task<(string Target, List<string> Marked)> backup;
        using (OtherProcessHolds(store.FleetLockPath))
        {
            backup = StateLockTests.OnOwnThread(() => store.Backup(_env.PathIn("backup2")));
            await Task.Delay(700);
            Assert.False(backup.IsCompleted, "另一個行程持有兩船版次清單的鎖時,備份應該等它寫完");
            var other = store.LoadFleet();
            other.Ships["FSB2"] = new ShipVersionEntry { ShipId = "FSB2", Version = "other" };
            PackageJson.SaveAtomic(store.FleetPath, other);                                      // 它寫完了
        }
        var second = await backup.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("other", PackageJson.Load<FleetVersions>(Path.Combine(second.Target, "fleet-versions.json"), "備份的版次清單").Ships["FSB2"].Version);

        // 另一個行程正在附加稽核紀錄:同樣等它寫完再複製
        using (OtherProcessHolds(store.Audit.LockPath))
        {
            backup = StateLockTests.OnOwnThread(() => store.Backup(_env.PathIn("backup3")));
            await Task.Delay(700);
            Assert.False(backup.IsCompleted, "另一個行程持有稽核紀錄的鎖時,備份應該等它寫完");
            File.AppendAllText(store.Audit.Path, "{\"event\":\"other.process\"}\n");
        }
        var third = await backup.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("other.process", AuditEventsIn(Path.Combine(third.Target, "audit.jsonl")));
        Assert.True(CanOpenExclusively(store.FleetLockPath) && CanOpenExclusively(store.Audit.LockPath) && CanOpenExclusively(store.LedgerLockPath));
    }

    [Fact]
    public void Backup_FleetListOrAuditLogChangedWhileBeingCopied_IsCopiedAgain_ButARecordFileStillFailsTheBackup()
    {
        // 取不到版次清單 / 稽核紀錄的鎖(舊版的 simosa-pkg 不認得、或等到上限)時,另一個行程仍可能在複製的那一瞬間改了這兩個檔:
        // 比對不符就再複製一次(最多三次),不讓整個備份以「備份後雜湊不符」失敗。紀錄包與解開的紀錄不會被別的行程改(帳冊的鎖):照舊一次不符就失敗
        var ship = _env.NewShip();
        var changes = new Dictionary<string, int>(StringComparer.Ordinal);
        string? tamperRecord = null;
        RecordStore? store = null;
        void OtherProcessWrites(string rel)
        {
            rel = rel.Replace('\\', '/');
            changes[rel] = changes.GetValueOrDefault(rel) + 1;
            if (changes[rel] > 1) return;                                                         // 只在第一次複製之後改一次
            if (rel == "audit.jsonl") File.AppendAllText(store!.Audit.Path, "{\"event\":\"other.process\"}\n");
            else if (rel == "fleet-versions.json")
            {
                var fleet = store!.LoadFleet();
                fleet.Ships["FSB2"] = new ShipVersionEntry { ShipId = "FSB2", Version = "other" };
                PackageJson.SaveAtomic(store.FleetPath, fleet);
            }
            else if (rel == tamperRecord) File.AppendAllText(Path.Combine(store!.Root, rel), "x");
        }
        store = new RecordStore(_env.PathIn("store")) { AfterBackupCopy = OtherProcessWrites };
        Assert.True(store.Import(PackOne(ship, "r1"), enrollUnknownDevice: true).Ok);
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "1" });

        var (target, marked) = store.Backup(_env.PathIn("backup"));
        Assert.Single(marked);
        Assert.Equal((2, 2), (changes["audit.jsonl"], changes["fleet-versions.json"]));          // 各複製了兩次
        Assert.Contains("other.process", AuditEventsIn(Path.Combine(target, "audit.jsonl")));     // 備份裡是改過之後的那一份(與來源相同)
        Assert.Equal("other", PackageJson.Load<FleetVersions>(Path.Combine(target, "fleet-versions.json"), "備份的版次清單").Ships["FSB2"].Version);

        // 解開的紀錄在複製的那一瞬間被改:這不該發生(帳冊的鎖擋住匯入),照舊讓備份失敗
        tamperRecord = FilesIn(store.Root).First(f => f.StartsWith("records/", StringComparison.Ordinal) && f.EndsWith(".jsonl", StringComparison.Ordinal));
        changes.Clear();
        var ex = Assert.Throws<IOException>(() => store.Backup(_env.PathIn("backup2")));
        Assert.Equal($"備份後雜湊不符:{tamperRecord.Replace('/', Path.DirectorySeparatorChar)}", ex.Message);
        Assert.True(CanOpenExclusively(store.LedgerLockPath));                                   // 失敗之後鎖也放開了
    }

    // ── R5:已在紀錄庫的紀錄包 ─────────────────────────────

    [Fact]
    public void Import_PackageAlreadyInTheStore_NeedsNoLock_SoABusyOrReadOnlyStoreStillAnswers()
    {
        // 隨身碟上留著以前的紀錄包是常態。已在紀錄庫的再匯入一次不寫任何東西:帳冊被另一個行程佔著(備份紀錄庫)時不必每一個各等到逾時再列為錯誤,
        // 唯讀的紀錄庫(鎖定檔建立不了)也照常回報 — 加上帳冊的鎖之前就是這樣
        var ship = _env.NewShip();
        var sbrec = PackOne(ship, "r1");
        var store = new RecordStore(_env.PathIn("store")) { LedgerLockTimeout = TimeSpan.FromMilliseconds(300) };
        var first = store.Import(sbrec, enrollUnknownDevice: true);
        Assert.True(first.Ok && !first.AlreadyImported);
        var ledgerBefore = File.ReadAllText(store.LedgerPath);
        var auditBefore = File.ReadAllText(store.Audit.Path);
        using (OtherProcessHolds(store.LedgerLockPath))
        {
            var again = store.Import(sbrec);
            Assert.True(again.Ok, string.Join(";", again.Errors));
            Assert.True(again.AlreadyImported);
            Assert.Equal(first.Directory, again.Directory);
        }
        // 鎖定檔的位置被一個資料夾占住:取鎖會是「存取被拒」(唯讀的紀錄庫也是)— 已在庫的紀錄包不走到那裡
        File.Delete(store.LedgerLockPath);
        Directory.CreateDirectory(store.LedgerLockPath);
        var readOnly = store.Import(sbrec);
        Assert.True(readOnly.Ok && readOnly.AlreadyImported);
        Assert.Equal(ledgerBefore, File.ReadAllText(store.LedgerPath));                          // 什麼都沒有寫
        Assert.Equal(auditBefore, File.ReadAllText(store.Audit.Path));
        Directory.Delete(store.LedgerLockPath);

        // 同一個紀錄包編號、內容不同:照舊報錯(同樣不必等鎖)
        var ledger = store.LoadLedger();
        ledger.Packages.Single().Sha256 = new string('0', 64);
        PackageJson.SaveAtomic(store.LedgerPath, ledger);
        using (OtherProcessHolds(store.LedgerLockPath))
        {
            var different = store.Import(sbrec);
            Assert.False(different.Ok);
            Assert.EndsWith("已存在但內容不同", Assert.Single(different.Errors));
        }

        // 還沒有在紀錄庫的紀錄包仍然要取鎖(等不到列為這個紀錄包的錯誤)
        var another = PackOne(ship, "r2");
        using (OtherProcessHolds(store.LedgerLockPath))
            Assert.StartsWith("另一個 simosa-pkg 正在使用這個紀錄庫的帳冊", Assert.Single(store.Import(another).Errors));
    }

    // ── R6:鎖定檔建立不了 ─────────────────────────────────

    [Fact]
    public void Acquire_OnlyAnExclusivelyOpenedFileCountsAsBusy()
    {
        // 「被佔用」只指開得到、鎖不到(Windows:共用違規;Linux / macOS:flock 回 EWOULDBLOCK)。這項在 Windows 執行器上也跑:
        // 分辨錯了的話,等待超過 OtherErrorGrace(2 秒)的指令會把共用違規當成別的錯誤丟出去
        var path = _env.PathIn("locks", "classify.lock");
        using (OtherProcessHolds(path))
        {
            var violation = Assert.Throws<IOException>(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            Assert.True(StateLock.IsSharingViolation(violation), $"HResult 0x{violation.HResult:X8}:{violation.Message}");
            // 真的被佔用到逾時:訊息就是設定的那一句(不附別的),原本的例外放在 InnerException
            var busy = Assert.Throws<IOException>(() => StateLock.Acquire(new StateLockSpec { Path = path, Timeout = TimeSpan.FromMilliseconds(300), BusyMessage = _ => "忙" }));
            Assert.Equal("忙", busy.Message);
            Assert.True(StateLock.IsBusy(busy));
            Assert.True(StateLock.IsSharingViolation(busy.InnerException!));
        }
        Assert.False(StateLock.IsSharingViolation(new IOException("No space left on device", 28)));
        Assert.False(StateLock.IsSharingViolation(new FileNotFoundException("x")));
        Assert.False(StateLock.IsSharingViolation(new UnauthorizedAccessException("x")));
    }

    [Fact]
    public void Acquire_WhenTheLockFileCannotBeCreated_ReportsTheRealErrorSoon_InsteadOfBusyAfterTheFullTimeout()
    {
        // 真實行程實測(inode 用完的檔案系統):原本等滿 60 秒後報「另一個 simosa-pkg 正在使用這個紀錄庫的帳冊…」,真正的原因(No space left on device)看不到。
        // 這裡以「鎖定檔是一個指向自己的符號連結」做出同一類錯誤(開檔得到 Too many levels of symbolic links:一般的 IOException、檔案不在、資料夾在)。
        // 只在非 Windows 跑:Windows 上鎖定檔不存在時的 IOException 本來就不重試,而且建立符號連結需要權限
        if (OperatingSystem.IsWindows()) return;
        var path = _env.PathIn("locks", "loop.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, path);
        var notices = new List<string>();
        StateLockSpec Spec(int timeoutMs, int graceMs) => new()
        {
            Path = path, Timeout = TimeSpan.FromMilliseconds(timeoutMs), OtherErrorGrace = TimeSpan.FromMilliseconds(graceMs), BusyMessage = _ => "忙",
            WaitingMessage = "等待中", Notice = notices.Add, NoticeAfter = TimeSpan.Zero,
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<IOException>(() => StateLock.Acquire(Spec(timeoutMs: 4000, graceMs: 300)));
        Assert.False(StateLock.IsBusy(ex), ex.Message);                                           // 不是「另一個 simosa-pkg 佔著」
        Assert.NotEqual("忙", ex.Message);
        Assert.Contains("loop.lock", ex.Message);                                                 // 原本的例外(.NET 的訊息,帶著路徑)
        Assert.InRange(clock.ElapsedMilliseconds, 250, 3000);                                     // 短暫重試後就報,不等到上限
        Assert.Empty(notices);                                                                    // 也不印「另一個 simosa-pkg 正在使用…,等它結束」

        // 等待上限比寬限還短時:仍是逾時的例外,但訊息尾附上真正的原因
        var busy = Assert.Throws<IOException>(() => StateLock.Acquire(Spec(timeoutMs: 200, graceMs: 5000)));
        Assert.True(StateLock.IsBusy(busy));
        Assert.StartsWith("忙(開啟鎖定檔時最後一次的錯誤:", busy.Message);
        Assert.Contains("loop.lock", busy.Message);
        Assert.IsType<IOException>(busy.InnerException);

        // 失敗之後沒有留下行程內的鎖:原因排除後可以取得
        File.Delete(path);
        using (StateLock.Acquire(Spec(timeoutMs: 4000, graceMs: 300))) Assert.False(CanOpenExclusively(path));
    }

    // ── R8:同步服務等鎖時寫進自己的紀錄 ───────────────────

    [Fact]
    public async Task Cli_Serve_WritesTheLockWaitNoticeToItsOwnLog()
    {
        // 指令列的 serve(啟動腳本把它的輸出存成 build/logs/sync.log):等船上的狀態鎖超過 3 秒時,同一句「另一個 simosa-pkg 正在使用…」以 [serve] 開頭寫進紀錄
        var ship = _env.NewShip();
        using var stop = new CancellationTokenSource();
        var buffer = new StringWriter();
        var log = TextWriter.Synchronized(buffer);
        string Logged()
        {
            lock (log) return buffer.ToString();
        }
        var serve = StateLockTests.OnOwnThread(() => new CliApp(log, log) { ServeStop = stop.Token }.Run(new[] { "serve", "--root", ship.Root, "--listen", "127.0.0.1", "--port", "0" }));
        try
        {
            var port = 0;
            for (var i = 0; i < 300 && port == 0; i++)
            {
                if (Regex.Match(Logged(), @"監聽 127\.0\.0\.1:(\d+)") is { Success: true } m) port = int.Parse(m.Groups[1].Value);
                else if (serve.IsCompleted) break;
                else await Task.Delay(50);
            }
            Assert.True(port > 0, $"serve 沒有啟動:{Logged()}");
            using var key = _env.Primary();
            var store = new RecordStore(_env.PathIn("store"));
            const string notice = "[serve] 另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態(同步服務正在處理教官筆電的同步,或另一個視窗的指令還沒有結束),等它結束(最多 120 秒)…";
            bool Noticed() => Logged().Split('\n').Select(l => l.TrimEnd('\r')).Contains(notice);
            Task<SyncReport> syncing;
            using (OtherProcessHolds(ship.LockPath))
            {
                syncing = SyncClient.SyncAsync(new SyncOptions { Host = "127.0.0.1", Port = port, InstructorKey = key, Store = store, EnrollDevice = true });
                // 佔住鎖到那一行出現為止(等超過 3 秒才寫;不靠固定的秒數,機器忙的時候握手比較慢)。沒有寫的話同步服務最多等 120 秒,這裡 20 秒就放棄
                for (var i = 0; i < 400 && !Noticed() && !syncing.IsCompleted; i++) await Task.Delay(50);
                Assert.False(syncing.IsCompleted, "船上的狀態鎖被佔住時同步應該還在等");
                Assert.True(Noticed(), $"同步服務等鎖超過 3 秒時,紀錄裡應該有一行以 [serve] 開頭的等待訊息:{Logged()}");
            }
            Assert.True((await syncing.WaitAsync(TimeSpan.FromSeconds(30))).Ok);
        }
        finally
        {
            stop.Cancel();
        }
        Assert.Equal(0, await serve.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
