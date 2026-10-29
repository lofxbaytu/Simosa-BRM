using System.Net;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 第十六輪審查已知限制 1(需求追溯表 R9/R13):情境切換時紀錄器交換與 sessionInfo 寫入由 HostCommandRouter 在同一把鎖內完成
/// (<see cref="HostCommandRouter.SwapRecorder"/>)。不論 sessionInfo 落在切換前(舊檔仍開)、切換中(舊檔已由 ScenarioLoading 關閉、
/// 新檔尚未開)或切換後,資訊都恰好進入新檔一次(header 或 session 行;切換前到達者另在舊檔留一行 session),不會兩份紀錄都沒有。
/// 決定性三案例 + 多執行緒交錯壓力(WebSocket 執行緒在切換窗口附近以不同延遲送 sessionInfo)。
/// </summary>
public class HostRecorderSwapTests
{
    private static readonly IPEndPoint Loopback = new(IPAddress.Loopback, 50000);

    private static SimCommand SessionInfoCmd(string name = "王大副") => SimCommand.SessionInfo(new
    {
        participants = new object[] { new { name, role = "oow", rank = "大副" } },
        restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = true },
    });

    private static JsonElement AckOf(HostCommandResult r) => JsonSerializer.SerializeToElement(r.Reply, ContractJson.Options);

    /// <summary>模擬主控程序(Program.cs)的紀錄流程:router 擁有紀錄器;loadScenario 時 RecordWriter 於 ScenarioLoading 自動關閉舊檔,ScenarioLoadRequested 切換後由 router 在鎖內開新檔。</summary>
    private sealed class Harness : IDisposable
    {
        public readonly SimulationEngine Engine = TestData.Engine();
        public readonly ShutdownCoordinator Shutdown = new();
        public readonly HostCommandRouter Router;
        public readonly string OldPath;
        public readonly string NewPath;
        /// <summary>切換窗口:舊檔已關閉、新檔尚未開(引擎執行緒,ScenarioLoading 之後、SwapRecorder 之前)</summary>
        public Action? InSwitchWindow;

        public Harness(string tag)
        {
            var dir = TestData.TestRecordsDirectory;
            OldPath = Path.Combine(dir, $"swap-{tag}-old-{Guid.NewGuid():N}.jsonl");
            NewPath = Path.Combine(dir, $"swap-{tag}-new-{Guid.NewGuid():N}.jsonl");
            Router = new HostCommandRouter(Engine, Shutdown);
            Router.SwapRecorder(_ => new RecordWriter(OldPath, Engine));
            // RecordWriter 先訂閱 ScenarioLoading(關檔),這裡的處理常式在其後執行 → 窗口內舊檔已關
            Engine.ScenarioLoading += (_, _) => InSwitchWindow?.Invoke();
            Engine.ScenarioLoadRequested += (e, _) =>
            {
                e.LoadScenario(TestData.E07());
                Router.SwapRecorder(session => new RecordWriter(NewPath, e, session: session));
            };
            TestData.RunTo(Engine, 0.5);
        }

        public string SendSessionInfo(string name = "王大副")
        {
            var r = Router.Handle(SessionInfoCmd(name), Loopback)!;
            var ack = AckOf(r);
            Assert.True(ack.GetProperty("ok").GetBoolean(), ack.GetProperty("detail").GetString());
            return ack.GetProperty("detail").GetString()!.Contains("session 行") ? "session" : "header";
        }

        public void Switch()
        {
            Engine.Enqueue(SimCommand.LoadScenario("E07"));
            Engine.ProcessPendingCommands();
            Assert.Equal("E07_steering_failure", Engine.Scenario.Id);
        }

        /// <summary>再跑幾拍、關閉新檔,讀回兩份紀錄並刪檔。</summary>
        public (RecordFile Old, RecordFile New) Finish()
        {
            for (var i = 0; i < 10; i++) Engine.Step();
            var current = Router.Recorder!;
            Assert.Equal(NewPath, current.Path);
            current.Close();
            var oldRec = RecordReader.Read(OldPath);
            var newRec = RecordReader.Read(NewPath);
            File.Delete(OldPath);
            File.Delete(NewPath);
            return (oldRec, newRec);
        }

        public void Dispose() => Shutdown.Dispose();
    }

    /// <summary>
    /// 不變量:新檔恰有一份(header 或 session 行,二者擇一);舊檔 header 無(開檔時尚未收到)、至多一行 session;
    /// ack 說 header → 新檔 header 有、兩檔都沒有 session 行;ack 說 session 行 → 兩檔合計恰一行 session。兩檔完整性皆通過。
    /// </summary>
    private static void AssertExactlyOnce(RecordFile oldRec, RecordFile newRec, string where, string name = "王大副")
    {
        Assert.True(oldRec.IntegrityOk);
        Assert.True(newRec.IntegrityOk);
        Assert.Null(oldRec.Header.Participants);
        Assert.True(oldRec.Sessions.Count <= 1, $"舊檔 session 行 {oldRec.Sessions.Count}");
        var inNewHeader = newRec.Header.Participants is not null ? 1 : 0;
        Assert.Equal(1, inNewHeader + newRec.Sessions.Count);
        Assert.NotNull(newRec.SessionInfo);
        Assert.Equal(name, newRec.SessionInfo!.Participants[0].Name);
        if (where == "header")
        {
            Assert.Equal(1, inNewHeader);
            Assert.Empty(oldRec.Sessions);
            Assert.Empty(newRec.Sessions);
        }
        else
        {
            Assert.Equal(1, oldRec.Sessions.Count + newRec.Sessions.Count);
        }
    }

    [Fact]
    public void SessionInfoBeforeSwitch_OldSessionLine_NewHeaderCarriesIt()
    {
        using var h = new Harness("before");
        var where = h.SendSessionInfo();
        Assert.Equal("session", where);
        h.Switch();
        var (oldRec, newRec) = h.Finish();
        AssertExactlyOnce(oldRec, newRec, where);
        Assert.Single(oldRec.Sessions);
        Assert.Equal("session", oldRec.SessionInfoSource);
        Assert.Equal("header", newRec.SessionInfoSource); // 同一批人沿用到新情境
    }

    [Fact]
    public void SessionInfoInSwitchWindow_GoesToNewHeader_NotLost()
    {
        using var h = new Harness("window");
        string? where = null;
        h.InSwitchWindow = () =>
        {
            Assert.True(h.Router.Recorder!.Closed); // 舊檔已關、新檔尚未開
            where = h.SendSessionInfo();
        };
        h.Switch();
        Assert.Equal("header", where);
        var (oldRec, newRec) = h.Finish();
        AssertExactlyOnce(oldRec, newRec, where!);
        Assert.Empty(oldRec.Sessions);
        Assert.Equal("header", newRec.SessionInfoSource);
    }

    [Fact]
    public void SessionInfoAfterSwitch_NewSessionLine_NoHeader()
    {
        using var h = new Harness("after");
        h.Switch();
        var where = h.SendSessionInfo();
        Assert.Equal("session", where);
        var (oldRec, newRec) = h.Finish();
        AssertExactlyOnce(oldRec, newRec, where);
        Assert.Empty(oldRec.Sessions);
        Assert.Null(newRec.Header.Participants);
        Assert.Equal("session", newRec.SessionInfoSource);
    }

    /// <summary>
    /// 多執行緒交錯:引擎執行緒做情境切換,另一執行緒在切換窗口開啟後延遲 0 ~ 數萬次自旋再送 sessionInfo(落點涵蓋舊檔關閉前後、
    /// 新檔開檔中與開檔後);每一回合都必須滿足「新檔恰一份、不遺失」。舊設計(交換與 sessionInfo 不同鎖)在新檔開檔中那段會兩邊都沒有。
    /// </summary>
    [Fact]
    public void ConcurrentSessionInfoDuringSwitch_NeverLost_ExactlyOnceInNewRecord()
    {
        const int rounds = 40;
        var outcomes = new Dictionary<string, int>();
        for (var i = 0; i < rounds; i++)
        {
            using var h = new Harness($"race{i}");
            using var windowOpened = new ManualResetEventSlim(false);
            var spins = (i * 7919) % 60000; // 0 … 59 999 次自旋(涵蓋 RecordWriter 開檔寫 header 的時間尺度)
            var sendBeforeSwitch = i % 5 == 4; // 每 5 回合一次在切換前就送(舊檔仍開)
            h.InSwitchWindow = () =>
            {
                windowOpened.Set();
                Thread.SpinWait(spins / 2); // 讓對方有機會在開檔前後交錯
            };
            string? where = null;
            Exception? senderError = null;
            var sender = new Thread(() =>
            {
                try
                {
                    if (!sendBeforeSwitch)
                    {
                        windowOpened.Wait();
                        Thread.SpinWait(spins);
                    }
                    where = h.SendSessionInfo();
                }
                catch (Exception ex) { senderError = ex; }
            }) { IsBackground = true, Name = "ws-sessionInfo" };
            sender.Start();
            if (sendBeforeSwitch) sender.Join();
            h.Switch();
            Assert.True(sender.Join(TimeSpan.FromSeconds(10)), "sessionInfo 執行緒未結束");
            Assert.Null(senderError);
            Assert.NotNull(where);
            var (oldRec, newRec) = h.Finish();
            AssertExactlyOnce(oldRec, newRec, where!);
            var key = $"{where}/{(oldRec.Sessions.Count > 0 ? "old-line" : newRec.Header.Participants is not null ? "new-header" : "new-line")}";
            outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
        }
        // 至少要觀察到兩種落點(切換前就送的回合保證 session/old-line;其餘落在窗口或新檔開啟後)
        Assert.True(outcomes.Count >= 2, string.Join(", ", outcomes.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.True(outcomes.GetValueOrDefault("session/old-line") >= rounds / 5, string.Join(", ", outcomes.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    /// <summary>SwapRecorder 的 factory 擲例外(例如開檔失敗)時紀錄器維持原樣;Recorder 只由 SwapRecorder 換上。</summary>
    [Fact]
    public void SwapRecorder_FactoryThrows_KeepsCurrentRecorder()
    {
        var engine = TestData.Engine();
        using var shutdown = new ShutdownCoordinator();
        var router = new HostCommandRouter(engine, shutdown);
        Assert.Null(router.Recorder);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"swap-throw-{Guid.NewGuid():N}.jsonl");
        var writer = router.SwapRecorder(_ => new RecordWriter(path, engine));
        Assert.Same(writer, router.Recorder);
        Assert.Throws<IOException>(() => router.SwapRecorder(_ => throw new IOException("磁碟已滿")));
        Assert.Same(writer, router.Recorder);
        // factory 收到的 Session = 最近接受的 sessionInfo
        Assert.True(AckOf(router.Handle(SessionInfoCmd("張二副"), Loopback)!).GetProperty("ok").GetBoolean());
        RecordSessionInfo? seen = null;
        writer.Close();
        var path2 = Path.Combine(TestData.TestRecordsDirectory, $"swap-throw2-{Guid.NewGuid():N}.jsonl");
        using var second = router.SwapRecorder(s => new RecordWriter(path2, engine, session: seen = s));
        Assert.Equal("張二副", seen!.Participants[0].Name);
        second.Close();
        Assert.Equal("張二副", RecordReader.Read(path2).Header.Participants![0].Name);
        File.Delete(path);
        File.Delete(path2);
    }
}
