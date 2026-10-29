using System.Net;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 參與者與休息時間確認(需求追溯表 R9/R13;規劃書第 5.5 節):sessionInfo 指令由主控程序層(HostCommandRouter)處理 —
/// 紀錄已開啟時追加 session 行、未開啟時寫進下一份紀錄的 header;不進引擎、狀態雜湊與重播不受影響;格式不符整筆拒絕。
/// 另含 shutdown 指令的來源政策與觸發、停止檔輪詢(StopFileWatcher)與 ShutdownCoordinator 的基本行為。
/// </summary>
public class SessionInfoRecordTests
{
    private static object ValidArgs(string firstName = "王大副") => new
    {
        participants = new object[]
        {
            new { name = firstName, role = "OOW", rank = "大副", certificateNo = "C-123" },
            new { name = "李舵工", role = "helmsman" },
        },
        restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T09:30:00+08:00", compliant = true, note = "已核對休息時間紀錄" },
        course = new { id = "BRM-2026-03", title = "BRM 複訓", instructor = "陳教官" },
    };

    private static JsonElement AckOf(HostCommandResult r) => JsonSerializer.SerializeToElement(r.Reply, ContractJson.Options);
    private static readonly IPEndPoint Loopback = new(IPAddress.Loopback, 50000);

    [Fact]
    public void TryParse_NormalizesAndValidates()
    {
        var info = RecordSessionInfo.TryParse(SimCommand.SessionInfo(ValidArgs()).Args, out var error);
        Assert.NotNull(info);
        Assert.Equal("", error);
        Assert.Equal(new[] { "oow", "helmsman" }, info!.Participants.Select(p => p.Role)); // role 不分大小寫、存小寫
        Assert.Equal("大副", info.Participants[0].Rank);
        Assert.Equal("C-123", info.Participants[0].CertificateNo);
        Assert.Null(info.Participants[1].Rank);
        Assert.Equal("2026-10-08T01:30:00.000Z", info.RestHoursConfirmation!.ConfirmedAtUtc); // 正規化為 UTC 毫秒 Z
        Assert.True(info.RestHoursConfirmation.Compliant);
        Assert.Equal("BRM-2026-03", info.Course!.Id);

        Assert.Null(RecordSessionInfo.TryParse(null, out error));
        Assert.Contains("args", error);
        Assert.Null(RecordSessionInfo.TryParse(SimCommand.Obj(new { participants = new object[] { new { name = "甲", role = "captain" } }, restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = true } }), out error));
        Assert.Contains("role", error);
        Assert.Null(RecordSessionInfo.TryParse(SimCommand.Obj(new { participants = Array.Empty<object>(), restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = true } }), out error));
        Assert.Null(RecordSessionInfo.TryParse(SimCommand.Obj(new { participants = new object[] { new { name = "甲", role = "master" } } }), out error));
        Assert.Contains("restHoursConfirmation", error);
        Assert.Null(RecordSessionInfo.TryParse(SimCommand.Obj(new { participants = new object[] { new { name = "甲", role = "master" } }, restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "昨天", compliant = true } }), out error));
        Assert.Contains("confirmedAtUtc", error);
        Assert.Null(RecordSessionInfo.TryParse(SimCommand.Obj(new { participants = new object[] { new { name = "甲", role = "master" } }, restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = "yes" } }), out error));
        Assert.Contains("compliant", error);
        // 控制字元清理、course 全空則省略
        var cleaned = RecordSessionInfo.TryParse(SimCommand.Obj(new { participants = new object[] { new { name = " 王\u0001大副 ", role = "Master" } }, restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = false }, course = new { id = "  " } }), out _);
        Assert.Equal("王 大副", cleaned!.Participants[0].Name);
        Assert.Null(cleaned.Course);
    }

    [Fact]
    public void SessionInfoAfterRecordOpened_WritesSessionLine_ReplayAndHashUnaffected()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"session-line-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        using var shutdown = new ShutdownCoordinator();
        var router = new HostCommandRouter(engine, shutdown);
        var writer = router.SwapRecorder(_ => new RecordWriter(path, engine));
        string? acceptedWhere = null;
        router.SessionInfoAccepted += (_, where) => acceptedWhere = where;

        engine.Enqueue(SimCommand.Rudder(10));
        engine.ProcessPendingCommands();
        TestData.RunTo(engine, 2.0);
        var hashBefore = engine.StateHash;
        var result = router.Handle(SimCommand.SessionInfo(ValidArgs()), Loopback);
        Assert.NotNull(result);
        var ack = AckOf(result!);
        Assert.Equal("ack", ack.GetProperty("type").GetString());
        Assert.Equal("sessionInfo", ack.GetProperty("command").GetString());
        Assert.True(ack.GetProperty("ok").GetBoolean());
        Assert.Equal(engine.Tick, ack.GetProperty("tick").GetInt64());
        Assert.Contains("session 行", ack.GetProperty("detail").GetString());
        Assert.Equal("session 行", acceptedWhere);
        Assert.Null(result.AfterReply);
        Assert.Equal(hashBefore, engine.StateHash); // 引擎狀態與雜湊不變
        Assert.Equal(1, writer.SessionLines);
        TestData.RunTo(engine, 4.0);
        engine.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
        engine.ProcessPendingCommands();
        TestData.RunTo(engine, 6.0);
        var footer = writer.Close();

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.NotNull(rec.Footer!.ClosedUtc);
        Assert.Null(rec.Header.Participants); // 開檔時尚未收到 → header 沒有
        var line = Assert.Single(rec.Sessions);
        Assert.Equal(100, line.Tick);
        Assert.Equal(2.0, line.T, 9);
        Assert.Equal("王大副", line.Participants[0].Name);
        Assert.Equal("oow", line.Participants[0].Role);
        Assert.Equal("session", rec.SessionInfoSource);
        Assert.Equal("船長", rec.SessionInfo!.RestHoursConfirmation!.ConfirmedBy);
        Assert.Equal("BRM-2026-03", rec.SessionInfo.Course!.Id);
        Assert.Equal(2, rec.Inputs.Count); // session 不是 input 行
        Assert.DoesNotContain(rec.Inputs, i => i.Command.Type == SimCommandType.SessionInfo);
        Assert.Contains("\"kind\":\"session\"", File.ReadAllText(path));

        // 重播:狀態雜湊與事件序列一致(session 行被略過)
        var replay = Replayer.Replay(path, TestData.Ship);
        Assert.True(replay.IntegrityOk);
        Assert.Null(replay.Warning);
        Assert.True(replay.HashMatches, $"{replay.ExpectedHash} vs {replay.ActualHash}");
        Assert.True(replay.EventSequenceMatches, replay.FirstEventMismatch);
        Assert.Equal(footer.FinalTick, replay.FinalTick);

        // 與沒有 sessionInfo 的對照引擎相同雜湊
        var reference = TestData.Engine();
        reference.Enqueue(SimCommand.Rudder(10));
        reference.ProcessPendingCommands();
        TestData.RunTo(reference, 4.0);
        reference.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
        reference.ProcessPendingCommands();
        TestData.RunTo(reference, 6.0);
        Assert.Equal(reference.StateHash, footer.StateHash);
        File.Delete(path);
    }

    [Fact]
    public void SessionInfoBeforeRecordOpened_GoesToHeader_AndHeaderWinsOverLaterSessionLine()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"session-header-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        using var shutdown = new ShutdownCoordinator();
        var router = new HostCommandRouter(engine, shutdown); // Recorder 預設 null:紀錄尚未開啟
        Assert.Null(router.Recorder);
        var first = router.Handle(SimCommand.SessionInfo(ValidArgs()), Loopback)!;
        Assert.True(AckOf(first).GetProperty("ok").GetBoolean());
        Assert.Contains("header", AckOf(first).GetProperty("detail").GetString());
        Assert.NotNull(router.Session);

        using var writer = router.SwapRecorder(session => new RecordWriter(path, engine, session: session));
        TestData.RunTo(engine, 1.0);
        // 之後再送一次(例如修正職務):已開啟 → session 行;讀取規則 header 優先
        var second = router.Handle(SimCommand.SessionInfo(ValidArgs("張二副")), Loopback)!;
        Assert.True(AckOf(second).GetProperty("ok").GetBoolean());
        TestData.RunTo(engine, 2.0);
        writer.Close();

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal(2, rec.Header.Participants!.Count);
        Assert.Equal("王大副", rec.Header.Participants[0].Name);
        Assert.True(rec.Header.RestHoursConfirmation!.Compliant);
        Assert.Equal("陳教官", rec.Header.Course!.Instructor);
        Assert.Equal("header", rec.SessionInfoSource);
        Assert.Equal("王大副", rec.SessionInfo!.Participants[0].Name);
        Assert.Equal("張二副", Assert.Single(rec.Sessions).Participants[0].Name);
        var replay = Replayer.Replay(path, TestData.Ship);
        Assert.True(replay.HashMatches && replay.EventSequenceMatches, replay.FirstEventMismatch);
        File.Delete(path);
    }

    [Fact]
    public void InvalidSessionInfo_Rejected_NothingWritten_PreviousKept()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"session-bad-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        using var shutdown = new ShutdownCoordinator();
        var router = new HostCommandRouter(engine, shutdown);
        using var writer = router.SwapRecorder(_ => new RecordWriter(path, engine));
        var bad = router.Handle(new SimCommand { Type = SimCommandType.SessionInfo, Args = SimCommand.Obj(new { participants = new object[] { new { name = "甲", role = "captain" } } }) }, Loopback)!;
        Assert.False(AckOf(bad).GetProperty("ok").GetBoolean());
        Assert.Contains("role", AckOf(bad).GetProperty("detail").GetString());
        Assert.Equal(0, writer.SessionLines);
        Assert.Null(router.Session);
        Assert.True(AckOf(router.Handle(SimCommand.SessionInfo(ValidArgs()), Loopback)!).GetProperty("ok").GetBoolean());
        Assert.False(AckOf(router.Handle(new SimCommand { Type = SimCommandType.SessionInfo }, Loopback)!).GetProperty("ok").GetBoolean());
        Assert.Equal("王大副", router.Session!.Participants[0].Name); // 先前接受的保留
        Assert.Equal(1, writer.SessionLines);
        // 重播模式(唯讀)不接受
        Assert.False(AckOf(router.Handle(SimCommand.SessionInfo(ValidArgs()), Loopback, readOnly: true)!).GetProperty("ok").GetBoolean());
        // 非主控程序層指令回 null(交給引擎)
        Assert.Null(router.Handle(SimCommand.Rudder(5), Loopback));
        writer.Close();
        File.Delete(path);
    }

    /// <summary>情境切換另開新檔:舊紀錄已關閉時 sessionInfo 保留到新紀錄的 header(主控程序 ApplyLoadedScenario 的流程)。</summary>
    [Fact]
    public void ScenarioSwitch_NewRecordHeaderCarriesSessionInfo()
    {
        var dir = TestData.TestRecordsDirectory;
        var path1 = Path.Combine(dir, $"session-sw1-{Guid.NewGuid():N}.jsonl");
        var path2 = Path.Combine(dir, $"session-sw2-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        using var shutdown = new ShutdownCoordinator();
        var router = new HostCommandRouter(engine, shutdown);
        router.SwapRecorder(_ => new RecordWriter(path1, engine));
        engine.ScenarioLoadRequested += (e, _) =>
        {
            e.LoadScenario(TestData.E07());
            router.SwapRecorder(session => new RecordWriter(path2, e, session: session));
        };
        TestData.RunTo(engine, 1.0);
        Assert.True(AckOf(router.Handle(SimCommand.SessionInfo(ValidArgs()), Loopback)!).GetProperty("ok").GetBoolean());
        engine.Enqueue(SimCommand.LoadScenario("E07"));
        engine.ProcessPendingCommands();
        Assert.Equal("E07_steering_failure", engine.Scenario.Id);
        TestData.RunTo(engine, 1.0);
        router.Recorder!.Close();

        var rec1 = RecordReader.Read(path1);
        Assert.Equal("session", rec1.SessionInfoSource);
        Assert.True(rec1.IntegrityOk);
        var rec2 = RecordReader.Read(path2);
        Assert.Equal("header", rec2.SessionInfoSource);
        Assert.Equal("王大副", rec2.Header.Participants![0].Name);
        Assert.Empty(rec2.Sessions);
        Assert.True(rec2.IntegrityOk);
        var r2 = Replayer.Replay(path2, TestData.Ship);
        Assert.True(r2.HashMatches && r2.EventSequenceMatches, r2.FirstEventMismatch);
        File.Delete(path1);
        File.Delete(path2);
    }

    [Fact]
    public void ShutdownCommand_PolicyAndTrigger()
    {
        var engine = TestData.Engine();
        using var shutdown = new ShutdownCoordinator();
        var router = new HostCommandRouter(engine, shutdown);
        // 公網來源拒絕、重播模式拒絕
        var denied = router.Handle(SimCommand.Shutdown("test"), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 1))!;
        Assert.False(AckOf(denied).GetProperty("ok").GetBoolean());
        Assert.Null(denied.AfterReply);
        Assert.False(shutdown.IsRequested);
        Assert.False(AckOf(router.Handle(SimCommand.Shutdown(), Loopback, readOnly: true)!).GetProperty("ok").GetBoolean());
        Assert.True(HostCommandRouter.IsAllowedShutdownClient(null));
        Assert.True(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Parse("192.168.77.21"), 1)));
        Assert.True(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Parse("::ffff:10.1.2.3"), 1)));
        Assert.True(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.IPv6Loopback, 1)));
        Assert.False(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Parse("2606:4700::1111"), 1)));
        // 直連教官筆電:接受,AfterReply(ack 送出後)才觸發停止並要求結束練習
        var ok = router.Handle(SimCommand.Shutdown("stop-all"), new IPEndPoint(IPAddress.Parse("192.168.77.21"), 1))!;
        var ack = AckOf(ok);
        Assert.True(ack.GetProperty("ok").GetBoolean());
        Assert.Equal("shutdown", ack.GetProperty("command").GetString());
        Assert.NotNull(ok.AfterReply);
        Assert.False(shutdown.IsRequested);
        ok.AfterReply!();
        Assert.True(shutdown.IsRequested);
        Assert.Equal("shutdown", shutdown.Reason);
        Assert.True(shutdown.EndExerciseRequested);
        Assert.True(shutdown.Token.IsCancellationRequested);
    }

    [Fact]
    public void ShutdownCoordinator_FirstReasonWins_AndFinishedWait()
    {
        using var c = new ShutdownCoordinator();
        var reasons = new List<string>();
        c.Requested += reasons.Add;
        Assert.False(c.WaitFinished(TimeSpan.Zero));
        c.Request("stopFile");
        c.Request("SIGTERM", endExercise: true);
        Assert.Equal("stopFile", c.Reason);
        Assert.True(c.EndExerciseRequested); // 後到的結束練習要求仍記錄
        Assert.Equal(new[] { "stopFile" }, reasons);
        c.MarkFinished();
        Assert.True(c.WaitFinished(TimeSpan.Zero));
    }

    [Fact]
    public async Task StopFileWatcher_ResolvesInsideRoot_RemovesStale_AndTriggers()
    {
        var root = Path.Combine(TestData.TestRecordsDirectory, $"stoproot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Assert.Throws<ArgumentException>(() => StopFileWatcher.Resolve(root, Path.Combine("..", "outside.stop")));
        Assert.Throws<ArgumentException>(() => StopFileWatcher.Resolve(root, Path.GetTempPath()));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "build", "run", "simcore.stop")), StopFileWatcher.Resolve(root));
        // 殘留檔:建立時刪除,不視為停止請求
        var stale = Path.Combine(root, "run", "simcore.stop");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "");
        var w = StopFileWatcher.Create(root, "run/simcore.stop");
        Assert.True(w.RemovedStale);
        Assert.False(File.Exists(stale));
        Assert.False(w.Check());
        // 輪詢:寫入即觸發並刪除
        var stopped = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = w.RunAsync(TimeSpan.FromMilliseconds(20), () => stopped.TrySetResult(), cts.Token);
        File.WriteAllText(w.Path, "");
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(w.Path));
        // 取消時安靜結束
        using var cts2 = new CancellationTokenSource();
        var run2 = w.RunAsync(TimeSpan.FromMilliseconds(20), () => throw new InvalidOperationException("不應觸發"), cts2.Token);
        cts2.Cancel();
        await run2.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.Delete(root, recursive: true);
    }
}
