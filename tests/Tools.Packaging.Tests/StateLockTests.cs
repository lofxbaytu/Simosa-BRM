using System.IO.Compression;
using System.Net;
using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Transfer;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 狀態檔的跨行程鎖(<see cref="StateLock"/>;docs/design/packaging-sync.md 第 5.2 節)與用到它的三把鎖:
/// 紀錄庫的帳冊 store-ledger.json.lock、兩船版次清單 fleet-versions.json.lock、船上的 state/.lock。
/// 「另一個行程佔著鎖」以測試自己獨占開啟鎖定檔代表(獨占開啟在同一個行程內也互斥 — Windows 是共用違規、Linux 是 flock;
/// 真實的兩個行程見 tools/launch/tests/launch-tests.sh 的 test_state_locks)。
/// </summary>
public sealed class StateLockTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>代表另一個行程:獨占開啟鎖定檔(與 StateLock 自己的開法相同)</summary>
    private static FileStream OtherProcessHolds(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool CanOpenExclusively(string lockPath)
    {
        try
        {
            using var s = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private StateLockSpec Spec(string path, int timeoutMs = 300, Action<string>? notice = null, int noticeAfterMs = 100) => new()
    {
        Path = path, Timeout = TimeSpan.FromMilliseconds(timeoutMs), BusyMessage = t => $"測試:被佔用超過 {t.TotalMilliseconds:0} 毫秒",
        WaitingMessage = "測試:等待中", Notice = notice, NoticeAfter = TimeSpan.FromMilliseconds(noticeAfterMs),
    };

    // ── 共用的鎖 ──────────────────────────────────────────

    [Fact]
    public async Task Acquire_ExcludesAnotherHolder_WaitsUntilReleased_AndTimesOutWithTheConfiguredMessage()
    {
        var path = _env.PathIn("locks", "a.lock");
        Task<StateLock> waiting;
        using (OtherProcessHolds(path))
        {
            waiting = Task.Run(() => StateLock.Acquire(Spec(path, timeoutMs: 10_000)));
            await Task.Delay(300);
            Assert.False(waiting.IsCompleted, "鎖定檔被佔用時應該等待");
        }
        // 放開之後照常取得
        using (await waiting.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.False(CanOpenExclusively(path));   // 取得之後,別人開不了
        }
        Assert.True(CanOpenExclusively(path));        // 放開之後,別人開得了
        Assert.True(File.Exists(path));               // 鎖定檔留著不刪
        Assert.Equal(0, new FileInfo(path).Length);

        // 一直被佔著:等到上限後丟 IOException(型別就是 IOException,指令列原有的處理與結束碼不變),訊息是設定的那一句
        using (OtherProcessHolds(path))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.Throws<IOException>(() => StateLock.Acquire(Spec(path, timeoutMs: 300)));
            Assert.Equal("測試:被佔用超過 300 毫秒", ex.Message);
            Assert.True(StateLock.IsBusy(ex));
            Assert.InRange(clock.ElapsedMilliseconds, 250, 5000);
        }
        Assert.False(StateLock.IsBusy(new IOException("磁碟滿了")));
        Assert.False(StateLock.IsBusy(null));
    }

    [Fact]
    public void Acquire_SameThread_IsReentrant_AndReleasesOnlyAtTheOutermostDispose()
    {
        // 獨占開啟在同一個行程內也互斥:同一條執行緒第二次取得若又去開檔,會等到逾時(把自己卡死)
        var path = _env.PathIn("locks", "reentrant.lock");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outer = StateLock.Acquire(Spec(path, timeoutMs: 5000));
        var inner = StateLock.Acquire(Spec(path, timeoutMs: 5000));
        var innermost = StateLock.Acquire(Spec(path, timeoutMs: 5000));
        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
        innermost.Dispose();
        inner.Dispose();
        inner.Dispose();                               // 重複 Dispose 不多減一次
        Assert.False(CanOpenExclusively(path));        // 最外層還沒有放開:別的行程仍然進不來
        outer.Dispose();
        Assert.True(CanOpenExclusively(path));
        // 放開之後可以再取得
        using (StateLock.Acquire(Spec(path))) Assert.False(CanOpenExclusively(path));
    }

    [Fact]
    public async Task Acquire_AnotherThreadInTheSameProcess_WaitsAndTimesOutLikeAnotherProcess()
    {
        var path = _env.PathIn("locks", "threads.lock");
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var l = StateLock.Acquire(Spec(path));
            held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
        var notices = new List<string>();
        var ex = Assert.Throws<IOException>(() => StateLock.Acquire(Spec(path, timeoutMs: 400, notice: notices.Add, noticeAfterMs: 100)));
        Assert.True(StateLock.IsBusy(ex));
        Assert.Equal(new[] { "測試:等待中" }, notices);   // 等超過 NoticeAfter:通知一次(只有一次)
        var waiter = Task.Run(() =>
        {
            using var l = StateLock.Acquire(Spec(path, timeoutMs: 10_000));
            return true;
        });
        await Task.Delay(200);
        Assert.False(waiter.IsCompleted);
        release.Set();
        Assert.True(await waiter.WaitAsync(TimeSpan.FromSeconds(10)));
        await holder;
    }

    [Fact]
    public void Acquire_NoticeOnlyWhenWaiting_AndAStaleLockFileDoesNotBlock()
    {
        // 行程被強制結束之後鎖定檔還在,但沒有人獨占開啟:下一個行程立刻取得、不通知(鎖是「獨占開啟」,不是檔案在不在)
        var path = _env.PathIn("locks", "stale.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        var notices = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (StateLock.Acquire(Spec(path, timeoutMs: 5000, notice: notices.Add, noticeAfterMs: 0))) { }
        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
        Assert.Empty(notices);

        // 被另一個行程佔用、等超過 NoticeAfter:通知一次
        using (OtherProcessHolds(path))
            Assert.Throws<IOException>(() => StateLock.Acquire(Spec(path, timeoutMs: 400, notice: notices.Add, noticeAfterMs: 100)));
        Assert.Equal(new[] { "測試:等待中" }, notices);
    }

    [Fact]
    public void Acquire_LockFileCannotBeOpened_UsesTheAccessDeniedMessage_AfterTheGracePeriod()
    {
        // 鎖定檔的位置被一個資料夾占住:兩個平台都是「存取被拒」(不必靠檔案權限,root 也一樣)
        var path = _env.PathIn("locks", "denied.lock");
        Directory.CreateDirectory(path);
        var plain = Assert.Throws<UnauthorizedAccessException>(() => StateLock.Acquire(Spec(path)));
        Assert.Contains("denied.lock", plain.Message);                                           // 沒有設定訊息:原本的例外(訊息是 .NET 的,帶著路徑)
        var spec = new StateLockSpec
        {
            Path = path, Timeout = TimeSpan.FromSeconds(5), BusyMessage = _ => "忙", AccessDeniedMessage = detail => $"資料夾寫不進去:{detail}",
            AccessDeniedGrace = TimeSpan.FromMilliseconds(300),
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var mapped = Assert.Throws<UnauthorizedAccessException>(() => StateLock.Acquire(spec));
        Assert.StartsWith("資料夾寫不進去:", mapped.Message);
        Assert.IsType<UnauthorizedAccessException>(mapped.InnerException);
        Assert.False(StateLock.IsBusy(mapped));
        Assert.InRange(clock.ElapsedMilliseconds, 250, 4000);                                     // 先重試到寬限時間(舊版放開鎖定檔的瞬間),不是等到逾時
        // 失敗之後沒有留下行程內的鎖:路徑恢復正常後可以取得
        Directory.Delete(path);
        using (StateLock.Acquire(Spec(path))) { }
    }

    [Fact]
    public async Task Acquire_InteroperatesWithTheOldDeleteOnCloseLock()
    {
        // 舊版的 simosa-pkg(InstallLayout.AcquireLock 原本的寫法)以「關閉時刪除」獨占開啟同一個 state/.lock:新舊版仍然互相排除
        var path = _env.PathIn("locks", "old", ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        static FileStream OldStyle(string p) => new(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        Task<StateLock> waiting;
        using (OldStyle(path))
        {
            waiting = Task.Run(() => StateLock.Acquire(Spec(path, timeoutMs: 10_000)));
            await Task.Delay(300);
            Assert.False(waiting.IsCompleted, "舊版持有時新版應該等待");
        }
        using (await waiting.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.True(File.Exists(path));                                    // 舊版放開時刪掉了檔;新版持有的是路徑上的那一個
            Assert.Throws<IOException>(() => OldStyle(path));                  // 新版持有時舊版開不了(它會重試)
            Assert.True(File.Exists(path));                                    // 舊版開不了時不會把新版的鎖定檔刪掉
        }
        using (OldStyle(path)) { }                                             // 新版放開後舊版可以取得
    }

    // ── PackageJson.SaveAtomic:暫存檔名 ───────────────────

    [Fact]
    public async Task SaveAtomic_ConcurrentWritersOfTheSameFile_NeverFailAndNeverLeaveABrokenFile()
    {
        // 原本固定的暫存檔名 <檔>.tmp:兩個寫入者寫進同一個暫存檔 — 真實的兩個行程實測其中一個以「Could not find file '….tmp'」失敗,
        // 也出現過兩份內容交錯、讀不進來的檔。現在暫存檔名每次不同:都成功,檔案隨時是某一份完整的內容
        var dir = _env.PathIn("atomic");
        var path = Path.Combine(dir, "state.json");
        PackageJson.SaveAtomic(path, new StoreLedger());
        using var stop = new CancellationTokenSource();
        // 同時有一個不取鎖的讀取端(status、store list 的讀法):讀到寫到一半的檔會丟 InvalidDataException。
        // 只在非 Windows 跑:Windows 上讀取端開著檔的瞬間寫入端的取代要靠重試(PackageJson.MoveOver),那一段沒有在實機觀察過,不放進必過的測試
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested && !OperatingSystem.IsWindows())
            {
                PackageJson.Load<StoreLedger>(path, "state.json");
                reads++;
            }
            return reads;
        });
        var writers = Enumerable.Range(0, 6).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 150; i++)
            {
                var ledger = new StoreLedger();
                // 每一份的長度不同:內容交錯時看得出來
                for (var k = 0; k < 1 + (w * 7 + i) % 40; k++) ledger.Packages.Add(new StoredPackage { PackageId = $"w{w}-{i}-{k}", ShipId = "FSB1", Sha256 = new string('a', 64) });
                PackageJson.SaveAtomic(path, ledger);
            }
        })).ToArray();
        await Task.WhenAll(writers);
        stop.Cancel();
        Assert.True(await reader > 0 || OperatingSystem.IsWindows());
        var last = PackageJson.Load<StoreLedger>(path, "state.json");
        Assert.All(last.Packages, p => Assert.StartsWith(last.Packages[0].PackageId[..last.Packages[0].PackageId.LastIndexOf('-')], p.PackageId));   // 整份來自同一個寫入者的同一次
        Assert.Equal(new[] { "state.json" }, Directory.GetFiles(dir).Select(Path.GetFileName));                                                    // 沒有留下暫存檔
    }

    [Fact]
    public void SaveAtomic_WhenTheFileCannotBeReplaced_LeavesNoTemporaryFileBehind()
    {
        var dir = _env.PathIn("atomic-fail");
        var path = Path.Combine(dir, "state.json");
        Directory.CreateDirectory(Path.Combine(path, "occupied"));   // 目的地被一個資料夾占住:寫不進去(不必靠檔案權限)
        var ex = Record.Exception(() => PackageJson.SaveAtomic(path, new StoreLedger()));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex?.ToString());
        Assert.Empty(Directory.GetFiles(dir));
    }

    // ── 紀錄庫:帳冊 store-ledger.json ─────────────────────

    /// <summary>船上打包一個紀錄包,回傳 .sbrec 的路徑</summary>
    private string PackOne(InstallLayout ship, string recordName)
    {
        TestEnv.WriteRecord(ship.RecordsDir, recordName + ".jsonl", "E01_baseline", Start, 600);
        var p = new ShipRecordManager(ship).PackPending();
        Assert.NotNull(p);
        return Path.Combine(ship.OutboxDir, p!.FileName);
    }

    [Fact]
    public async Task Import_WaitsForAnotherProcessHoldingTheLedgerLock_AndKeepsItsEntry()
    {
        // 兩個行程同時匯入(兩支隨身碟,或一邊直連同步):原本各自讀到同一份帳冊,後寫的把先寫的那一筆蓋掉(真實行程實測 20 次有 8 至 12 次)
        var ship = _env.NewShip();
        var sbrec = PackOne(ship, "r1");
        var store = new RecordStore(_env.PathIn("store"));
        Task<ImportResult> waiting;
        using (OtherProcessHolds(store.LedgerLockPath))
        {
            waiting = Task.Run(() => store.Import(sbrec, enrollUnknownDevice: true));
            await Task.Delay(400);
            Assert.False(waiting.IsCompleted, "帳冊的鎖定檔被佔用時 Import 應該等待");
            Assert.False(Directory.Exists(store.RecordsDir));                                    // 還沒有開始解開
            // 另一個行程(持有鎖)此時記了一筆
            var other = store.LoadLedger();
            other.Packages.Add(new StoredPackage { PackageId = "rec-FSB2-other", ShipId = "FSB2", Sha256 = new string('b', 64), Directory = "records/FSB2/2026/rec-FSB2-other" });
            PackageJson.SaveAtomic(store.LedgerPath, other);
        }
        var r = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(r.Ok, string.Join(";", r.Errors));
        var ids = store.LoadLedger().Packages.Select(p => p.PackageId).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains("rec-FSB2-other", ids);                                                  // 另一個行程記的那一筆還在
        Assert.Contains(r.PackageId, ids);
        Assert.True(File.Exists(store.LedgerLockPath));                                          // 鎖定檔留著(0 位元組)
        Assert.Equal(0, new FileInfo(store.LedgerLockPath).Length);
    }

    [Fact]
    public void LedgerLockHeldTooLong_ImportReportsItForThatPackage_AndTheMarkCommandsFailWithTheSameMessage()
    {
        var ship = _env.NewShip();
        var sbrec = PackOne(ship, "r1");
        var store = new RecordStore(_env.PathIn("store")) { LedgerLockTimeout = TimeSpan.FromMilliseconds(300) };
        var notices = new List<string>();
        using (OtherProcessHolds(store.LedgerLockPath))
        {
            // 匯入:列為這個紀錄包的錯誤(與其他匯入失敗相同:Ok = false → 指令列結束碼 1),沒有解開任何東西
            var r = store.Import(sbrec, enrollUnknownDevice: true);
            Assert.False(r.Ok);
            var error = Assert.Single(r.Errors);
            Assert.StartsWith("另一個 simosa-pkg 正在使用這個紀錄庫的帳冊(正在匯入紀錄包、標記備份或備份紀錄庫;", error);
            Assert.Contains("store-ledger.json.lock 被佔用超過 0 秒),這次沒有寫入;請等它結束後再執行一次", error);
            Assert.False(Directory.Exists(store.RecordsDir));
            Assert.False(File.Exists(store.LedgerPath));

            // 標記備份、標記船上已刪除、備份紀錄庫:IOException(指令列「錯誤:…」、結束碼 1)
            foreach (var act in new Action[]
                     {
                         () => store.MarkBackedUp(null, "T"), () => store.MarkShipDeleted("bak-1", new[] { "x" }),
                         () => store.Backup(_env.PathIn("backup-busy")),
                     })
            {
                var ex = Assert.Throws<IOException>(act);
                Assert.True(StateLock.IsBusy(ex));
                Assert.StartsWith("另一個 simosa-pkg 正在使用這個紀錄庫的帳冊", ex.Message);
            }
            Assert.False(Directory.Exists(_env.PathIn("backup-busy")));

            // 只讀的方法不取鎖、不被擋住
            Assert.Empty(store.LoadLedger().Packages);
            Assert.Empty(store.LoadFleet().Ships);
            Assert.False(store.Has("x", new string('a', 64)));
            // 兩船版次清單是另一把鎖:帳冊被佔著時照常更新
            Assert.True(store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "1" }).Saved);

            // 等超過幾秒時的通知(指令列印一行「注意:…」)
            store.LockNotice = notices.Add;
            store.LedgerLockTimeout = TimeSpan.FromSeconds(4);
            Assert.Throws<IOException>(() => store.MarkBackedUp(null, "T"));
        }
        Assert.Equal(new[] { "另一個 simosa-pkg 正在使用這個紀錄庫的帳冊(匯入紀錄包、標記備份或備份紀錄庫),等它結束(最多 4 秒)…" }, notices);
        // 放開之後照常匯入
        store.LedgerLockTimeout = TimeSpan.FromSeconds(30);
        Assert.True(store.Import(sbrec, enrollUnknownDevice: true).Ok);
    }

    [Fact]
    public async Task Backup_HoldsTheLedgerLockForTheWholeCopy_ReentersItToMark_AndNeverCopiesLockFiles()
    {
        var ship = _env.NewShip();
        var store = new RecordStore(_env.PathIn("store"));
        Assert.True(store.Import(PackOne(ship, "r1"), enrollUnknownDevice: true).Ok);
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "1" });
        Assert.True(File.Exists(store.LedgerLockPath) && File.Exists(store.FleetLockPath));

        // 兩船版次清單的鎖被另一個行程佔著也不影響備份(不複製它);備份持有帳冊的鎖,結束時在同一條執行緒重入做標記
        using (OtherProcessHolds(store.FleetLockPath))
        {
            var (target, marked) = store.Backup(_env.PathIn("backup"));
            Assert.Single(marked);                                                                  // 重入沒有把自己卡死(否則等到逾時丟例外)
            var copied = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(target, f).Replace('\\', '/')).ToList();
            Assert.Contains("store-ledger.json", copied);
            Assert.Contains("fleet-versions.json", copied);
            Assert.Contains("audit.jsonl", copied);
            Assert.DoesNotContain(copied, f => f.EndsWith(".lock", StringComparison.Ordinal));     // 鎖定檔不被備份
            Assert.DoesNotContain(copied, f => f.EndsWith(".tmp", StringComparison.Ordinal));
        }
        Assert.NotNull(store.LoadLedger().Packages.Single().BackedUpUtc);
        Assert.True(CanOpenExclusively(store.LedgerLockPath));                                          // 備份結束後放開了

        // 另一個行程正在匯入(持有帳冊的鎖)時,備份等它做完再開始:不會複製到解開到一半的資料夾
        store.LedgerLockTimeout = TimeSpan.FromSeconds(20);
        var half = Path.Combine(store.RecordsDir, "FSB2", "2026", "rec-FSB2-x.importing");
        Task<(string Target, List<string> Marked)> backup;
        using (OtherProcessHolds(store.LedgerLockPath))
        {
            Directory.CreateDirectory(half);
            File.WriteAllText(Path.Combine(half, "manifest.json"), "{");                                // 另一個行程解開到一半
            backup = Task.Run(() => store.Backup(_env.PathIn("backup2")));
            await Task.Delay(400);
            Assert.False(backup.IsCompleted, "另一個行程持有帳冊的鎖時備份應該等待");
            Assert.False(Directory.Exists(_env.PathIn("backup2")));
            Directory.Move(half, half[..^".importing".Length]);                                        // 它做完了(改名成正式的資料夾)
        }
        var second = await backup.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(Directory.GetDirectories(second.Target, "*.importing", SearchOption.AllDirectories));
    }

    // ── 船上:state/.lock ──────────────────────────────────

    [Fact]
    public void ShipLockHeldTooLong_EveryStateChangingStepFailsWithAClearMessage_AndChangesNothing()
    {
        // 原本等 30 秒後只有一句英文的「The process cannot access the file '…/state/.lock' because it is being used by another process.」
        var ship = _env.NewShip();
        ship.LockTimeout = TimeSpan.FromMilliseconds(300);
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 600);
        var applier = new UpdateApplier(ship);
        var records = new ShipRecordManager(ship);
        var auditBefore = File.ReadAllText(ship.AuditPath);
        using (OtherProcessHolds(ship.LockPath))
        {
            foreach (var act in new Action[]
                     {
                         () => applier.Apply(pkg), () => applier.Rollback(), () => records.PackPending(), () => records.BeginVisit("usb"),
                         () => records.MarkPulled("x"), () => UsbTransfer.ShipImport(new ShipNode(ship), _env.PathIn("usb")),
                         () => DeviceKeyStore.Initialize(ship, "FSB1", "FSB1-LT01", overwrite: true, forcePem: true),
                     })
            {
                var ex = Assert.Throws<IOException>(act);
                Assert.True(StateLock.IsBusy(ex));
                Assert.StartsWith("另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態(同步服務正在處理教官筆電的同步,或另一個視窗正在套用、回復更新包、打包紀錄或匯入隨身碟),等了 0 秒還沒有結束,這一步沒有執行。", ex.Message);
                Assert.Contains("請等它結束(教官筆電顯示「同步完成」,或另一個視窗的指令結束)後再執行一次", ex.Message);
            }
            // 什麼都沒有改
            Assert.False(File.Exists(ship.InstalledStatePath));
            Assert.False(File.Exists(ship.LedgerPath));
            Assert.Equal("app 1.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
            Assert.Equal(auditBefore, File.ReadAllText(ship.AuditPath));

            // 只讀的不取鎖:status、紀錄清單、可否回復、驗證更新包
            var status = new ShipNode(ship).Status();
            Assert.Equal("FSB1", status.ShipId);
            Assert.Empty(records.Outstanding());
            Assert.False(applier.CanRollback(out _));
            Assert.True(applier.Verify(pkg).Ok);
        }
        // 放開之後照常完成
        ship.LockTimeout = TimeSpan.FromSeconds(30);
        Assert.True(applier.Apply(pkg).Ok);
        Assert.NotNull(records.PackPending());
    }

    [Fact]
    public async Task ShipLockHeldBriefly_ApplyWaitsThenCompletes_AndTheWaitIsAnnouncedOnce()
    {
        var ship = _env.NewShip();
        var notices = new List<string>();
        ship.LockNotice = m => { lock (notices) notices.Add(m); };
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        Task<UpdateApplyResult> waiting;
        using (OtherProcessHolds(ship.LockPath))
        {
            waiting = Task.Run(() => new UpdateApplier(ship).Apply(pkg));
            await Task.Delay(3600);                                                              // 超過通知的門檻(3 秒)
            Assert.False(waiting.IsCompleted);
            Assert.False(File.Exists(ship.InstalledStatePath));
        }
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(30))).Ok);
        Assert.Equal(new[] { "另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態(同步服務正在處理教官筆電的同步,或另一個視窗的指令還沒有結束),等它結束(最多 120 秒)…" }, notices);
    }

    [Fact]
    public async Task DeviceInit_TwoAtTheSameTime_OnlyOneCreatesTheKey()
    {
        // 真實的兩個行程實測:20 次有 18 次兩邊都印「已產生裝置金鑰」、4 次留下私鑰與 device.json 對不上的金鑰。
        // 現在「檢查有沒有 → 產生 → 寫入」在船上的狀態鎖裡:後到的那一個看到「已存在」
        var root = _env.PathIn("fresh-ship");
        Directory.CreateDirectory(root);
        var layout = new InstallLayout(root);
        var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            try
            {
                DeviceKeyStore.Initialize(new InstallLayout(root), "FSB1", "FSB1-LT01", forcePem: true);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("裝置金鑰已存在", StringComparison.Ordinal))
            {
                return false;
            }
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(ok => ok));
        var (info, keyProvider) = DeviceKeyStore.Load(layout);                                   // 私鑰與 device.json 對得上(對不上會丟 CryptographicException)
        using (keyProvider) Assert.Equal(info.KeyId, keyProvider.KeyId);
        Assert.Single(layout.Audit().ReadAll(), e => (string?)e["event"] == AuditEvents.DeviceInitialized);
    }

    [Fact]
    public async Task Serve_WhenTheShipLockIsBusy_TellsTheInstructorInsteadOfDroppingTheConnection()
    {
        // 船上另一個視窗的指令佔著狀態鎖太久:教官端原本只看到連線中斷(「Unable to read beyond the end of the stream」)
        var ship = _env.NewShip();
        ship.LockTimeout = TimeSpan.FromMilliseconds(300);
        var log = new StringWriter();
        var server = new SyncServer(new ShipNode(ship), IPAddress.Loopback, 0, log) { IdleTimeout = TimeSpan.FromSeconds(20) };
        server.Start();
        await using var _server = server;
        using var key = _env.Primary();
        var store = new RecordStore(_env.PathIn("store"));
        var options = new SyncOptions { Host = "127.0.0.1", Port = server.Port, InstructorKey = key, Store = store, EnrollDevice = true };
        using (OtherProcessHolds(ship.LockPath))
        {
            var ex = await Assert.ThrowsAsync<SyncException>(() => SyncClient.SyncAsync(options));
            Assert.Equal("船端拒絕:船上筆電另一個 simosa-pkg 的指令(套用或回復更新包、打包紀錄、匯入隨身碟)還沒有結束,船上這一步沒有執行;請等船上的指令結束後再同步一次", ex.Message);
        }
        Assert.Contains("的同步沒有開始:另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態", log.ToString());
        Assert.False(File.Exists(ship.LedgerPath));                                              // 沒有記成一次訪船
        // 放開之後同一個服務照常同步(沒有因為上一個連線而卡住)
        var rep = await SyncClient.SyncAsync(options);
        Assert.True(rep.Ok);
        Assert.Equal(1, rep.Status!.VisitSeq);
    }

    [Fact]
    public async Task Serve_WhenTheShipLockBecomesBusyDuringASession_EachStepReportsItAndTheConnectionContinues()
    {
        // 連線已經開始之後,船上另一個視窗的指令才佔住狀態鎖:推送的更新包照「未套用」回報、打包不了列為警告、已備份回報照「沒有處理」回報,
        // 取回紀錄包照常送出 — 連線不中斷(原本任何一步等不到鎖都是直接斷線)。這裡以照協定握手的客戶端逐步送訊息
        var ship = _env.NewShip();
        ship.LockTimeout = TimeSpan.FromMilliseconds(300);
        var packed = PackOne(ship, "r1");
        var packageId = Path.GetFileNameWithoutExtension(packed);
        TestEnv.WriteRecord(ship.RecordsDir, "r2.jsonl", "E01_baseline", Start.AddHours(2), 600);   // 還沒有打包的新紀錄
        var log = new StringWriter();
        var server = new SyncServer(new ShipNode(ship), IPAddress.Loopback, 0, log) { IdleTimeout = TimeSpan.FromSeconds(20) };
        server.Start();
        await using var _server = server;
        using var key = _env.Primary();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        using var tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", server.Port, ct);
        await using var s = tcp.GetStream();
        var nonce = SyncProtocol.NewNonce();
        await SyncProtocol.WriteAsync(s, new HelloMessage { ClientNonce = nonce, Client = "test" }, ct);
        var ch = SyncProtocol.As<ChallengeMessage>(await SyncProtocol.ReadAsync(s, ct));
        var sig = key.Sign(SyncProtocol.InstructorAuthPayload(ch.ShipId, ch.DeviceId, ch.Nonce, nonce));
        await SyncProtocol.WriteAsync(s, new AuthMessage { KeyId = key.KeyId, Signer = key.DisplayName, Signature = Convert.ToBase64String(sig) }, ct);
        Assert.Equal(SyncTypes.Welcome, SyncProtocol.TypeOf(await SyncProtocol.ReadAsync(s, ct)));

        const string busy = "船上筆電另一個 simosa-pkg 的指令(套用或回復更新包、打包紀錄、匯入隨身碟)還沒有結束,船上這一步沒有執行;請等船上的指令結束後再同步一次";
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        var report = Crypto.SignedDocument.Create(new BackupReport
        {
            ReportId = "bak-FSB1-test", ShipId = "FSB1", IssuedUtc = DateTime.UtcNow, SignerKeyId = key.KeyId, SignerName = key.DisplayName,
            Packages = { new BackedUpPackage { PackageId = packageId, Sha256 = Sha256Hex.OfFile(packed), BackedUpUtc = DateTime.UtcNow } },
        }, key, DateTime.UtcNow).ToBytes();
        using (OtherProcessHolds(ship.LockPath))
        {
            // 推送更新包:未套用(Ok = false → 教官端結束碼 1),說明是船上在忙
            await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.PushUpdate, Name = "sw.sbupd", PackageId = "p", Size = new FileInfo(pkg).Length, Sha256 = Sha256Hex.OfFile(pkg) }, ct);
            await SyncProtocol.WriteFileBytesAsync(s, pkg, ct);
            var update = SyncProtocol.As<UpdateResultMessage>(await SyncProtocol.ReadAsync(s, ct)).Result;
            Assert.False(update.Ok);
            Assert.Equal("船上正在執行別的指令,未套用", update.Message);
            Assert.Equal(new[] { busy }, update.Errors);
            Assert.False(File.Exists(ship.InstalledStatePath));

            // 列出紀錄包:新的紀錄這次打包不了(警告),已經打包的照常列出
            await SyncProtocol.WriteAsync(s, new { type = SyncTypes.ListRecords }, ct);
            var list = SyncProtocol.As<RecordListMessage>(await SyncProtocol.ReadAsync(s, ct));
            Assert.Equal(new[] { "新的紀錄這次沒有打包:" + busy }, list.Warnings);
            Assert.Equal(packageId, Assert.Single(list.Packages).PackageId);

            // 取回紀錄包:照常送出;只是船上沒有記到「已取回」
            await SyncProtocol.WriteAsync(s, new GetRecordMessage { PackageId = packageId }, ct);
            var header = SyncProtocol.As<FileHeader>(await SyncProtocol.ReadAsync(s, ct));
            var received = _env.PathIn("received.sbrec");
            Assert.Equal(header.Sha256, await SyncProtocol.ReadFileBytesAsync(s, header.Size, received, SyncProtocol.DefaultMaxFileBytes, ct));

            // 已備份回報:沒有處理(Ok = false),船上的紀錄都還在
            await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.BackupReport, Name = "r.sbbak", Size = report.Length, Sha256 = Sha256Hex.Of(report) }, ct);
            await s.WriteAsync(report, ct);
            var cleanup = SyncProtocol.As<CleanupResultMessage>(await SyncProtocol.ReadAsync(s, ct)).Result;
            Assert.False(cleanup.Ok);
            Assert.Equal(new[] { busy }, cleanup.Errors);
            Assert.True(File.Exists(packed));
        }
        Assert.Null(new ShipRecordManager(ship).LoadLedger().Packages.Single().PulledUtc);
        Assert.Contains($"紀錄包 {packageId} 已送出,但沒有記為已取回", log.ToString());

        // 放開之後,同一個連線上再送一次都照常完成
        await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.PushUpdate, Name = "sw.sbupd", PackageId = "p", Size = new FileInfo(pkg).Length, Sha256 = Sha256Hex.OfFile(pkg) }, ct);
        await SyncProtocol.WriteFileBytesAsync(s, pkg, ct);
        Assert.True(SyncProtocol.As<UpdateResultMessage>(await SyncProtocol.ReadAsync(s, ct)).Result.Ok);
        await SyncProtocol.WriteAsync(s, new FileHeader { Type = SyncTypes.BackupReport, Name = "r.sbbak", Size = report.Length, Sha256 = Sha256Hex.Of(report) }, ct);
        await s.WriteAsync(report, ct);
        var done = SyncProtocol.As<CleanupResultMessage>(await SyncProtocol.ReadAsync(s, ct)).Result;
        Assert.True(done.Ok, string.Join(";", done.Errors));
        Assert.Equal(new[] { packageId }, done.Deleted);
        await SyncProtocol.WriteAsync(s, new { type = SyncTypes.Bye }, ct);
    }

    // ── 鎖定檔不是資料:不被備份、打包,也不算安裝資料夾裡多出的檔 ──

    [Fact]
    public void LockFiles_AreNotPackaged_NotExported_AndNotReportedByVerifyInstall()
    {
        var ship = _env.NewShip();
        // 讓它是一個「安裝版」:發行清單只列發行版的檔(安裝後才有的位置不在清單內)
        var manifest = ReleaseManifestBuilder.Build(ship.Root, "2026.10.0", "0123456789abcdef", "main", "linux-x64");
        manifest.Files.RemoveAll(f => f.Path.StartsWith("state/", StringComparison.Ordinal) || f.Path.StartsWith("config/", StringComparison.Ordinal));
        ReleaseManifestBuilder.Save(manifest, ship.Root);
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(_env.SoftwarePackage("2026.10.1", key)).Ok);
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", Start, 600);
        var usb = _env.PathIn("usb");
        var exported = UsbTransfer.ShipExport(new ShipNode(ship), usb);

        // 船上:state/.lock 留著(0 位元組)
        Assert.True(File.Exists(ship.LockPath));
        Assert.Equal(0, new FileInfo(ship.LockPath).Length);
        // 紀錄包與隨身碟上沒有鎖定檔
        using (var zip = ZipFile.OpenRead(exported.Written.Single(w => w.EndsWith(".sbrec", StringComparison.Ordinal))))
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(".lock", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.GetFiles(usb, "*", SearchOption.AllDirectories), f => f.Contains(".lock", StringComparison.Ordinal));
        // 覆蓋清單(更新包寫過的路徑)沒有鎖定檔;回復更新包照常(鎖定檔在 state/,不是元件)
        Assert.DoesNotContain(".lock", File.ReadAllText(ship.InstalledFilesPath));
        Assert.True(applier.Rollback().Ok);
        Assert.True(File.Exists(ship.LockPath));

        // 教官端紀錄庫放在安裝根目錄的 store/(預設位置):匯入、更新版次清單之後兩個鎖定檔都在
        var store = new RecordStore(Path.Combine(ship.Root, "store"));
        Assert.True(UsbTransfer.InstructorImport(store, usb, enrollDevice: true).Imported.Single().Ok);
        Assert.True(File.Exists(store.LedgerLockPath) && File.Exists(store.FleetLockPath));
        // 教官端匯出到隨身碟(更新包、已備份回報)也不帶鎖定檔
        store.MarkBackedUp(null, "T");
        var usb2 = _env.PathIn("usb2");
        UsbTransfer.InstructorExport(store, usb2, new[] { _env.SoftwarePackage("2026.10.2", key) }, key);
        Assert.DoesNotContain(Directory.GetFiles(usb2, "*", SearchOption.AllDirectories), f => f.Contains(".lock", StringComparison.Ordinal));

        // 安裝模式核對(verify-install):三個鎖定檔都在執行期資料的位置,不算多出的檔、沒有提醒
        var report = InstalledVerifier.Verify(ship.Root);
        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(e => $"[{e.Category}] {e.Path} {e.Detail}")));
        Assert.DoesNotContain(report.Errors.Concat(report.Notices), e => (e.Path ?? "").Contains(".lock", StringComparison.Ordinal) || (e.Detail ?? "").Contains(".lock", StringComparison.Ordinal));
        var cliOut = new StringWriter();
        var cliErr = new StringWriter();
        Assert.Equal(0, new CliApp(cliOut, cliErr).Run(new[] { "verify-install", "--root", ship.Root }));
        Assert.DoesNotContain(".lock", cliOut.ToString() + cliErr);
        // 一般模式的發行清單(release manifest)在使用中的安裝根目錄重建時也不會把鎖定檔列進去的位置:state/、store/ 都是執行期資料
        foreach (var lockFile in new[] { ship.LockPath, store.LedgerLockPath, store.FleetLockPath })
        {
            var rel = Path.GetRelativePath(ship.Root, lockFile).Replace('\\', '/');
            Assert.NotNull(InstalledPaths.FindRuntimeData(rel, InstalledPaths.DefaultComparison));
        }
    }
}
