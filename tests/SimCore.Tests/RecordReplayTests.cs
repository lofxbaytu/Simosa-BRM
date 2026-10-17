using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class RecordReplayTests
{
    private static readonly (long Tick, SimCommand Cmd)[] Script =
    {
        (0, SimCommand.Rudder(10)),
        (100, SimCommand.Telegraph(TelegraphOrder.NAVF)),
        (300, SimCommand.Thruster(-0.8)),
        (520, SimCommand.Freeze()),
        (520, SimCommand.Rudder(-5)),
        (520, SimCommand.Resume()),
        (700, SimCommand.Snapshot()),
        (900, SimCommand.InjectFault(FaultNames.SteeringGear)),
        (1100, SimCommand.ClearFault(FaultNames.SteeringGear)),
        (1300, SimCommand.Restore()),          // 倒帶到 tick 1000 的自動快照(tick 往回)
        (1050, SimCommand.Rudder(20)),         // 倒帶後的新指令
        (1400, SimCommand.Autopilot(true, heading: 350)),
    };

    private static void RunScriptSequential(SimulationEngine engine, int stepsAfterLast, IEnumerable<(long Tick, SimCommand Cmd)>? script = null)
    {
        // 依腳本順序執行(含倒帶,所以不能用 tick 排序);與主控程序相同:指令在該 tick 立即套用(Step 開頭 / 凍結中直接套用),
        // 倒帶指令因此立即生效,之後的指令各自套在自己的 tick(RecordReplayTests 原本「不 Step 就 Enqueue」與重播缺陷互相抵消,改掉)
        foreach (var (tick, cmd) in script ?? Script)
        {
            while (engine.Tick < tick) engine.Step();
            engine.Enqueue(cmd);
            engine.ProcessPendingCommands();
        }
        engine.Run(stepsAfterLast);
    }

    [Fact]
    public void ReplayReproducesRecordedHash()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"replay-{Guid.NewGuid():N}.jsonl");
        string recordedHash;
        long finalTick;
        var engine = TestData.Engine();
        using (var writer = new RecordWriter(path, engine))
        {
            RunScriptSequential(engine, 400);
            var footer = writer.Close();
            recordedHash = footer.StateHash;
            finalTick = footer.FinalTick;
            Assert.Equal(Script.Length, footer.Inputs);
        }
        Assert.Equal(engine.StateHash, recordedHash);
        Assert.Equal(1800, finalTick);

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal(Script.Length, rec.Inputs.Count);
        Assert.True(rec.StateLines > 0);
        Assert.Equal("E01_baseline", rec.Header.Scenario.Id);
        Assert.Contains(rec.Snapshots, s => s.Tick == 700);

        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.IntegrityOk);
        Assert.Null(result.Warning);
        Assert.Equal(finalTick, result.FinalTick);
        Assert.Equal(Script.Length, result.InputsReplayed);
        Assert.Equal(recordedHash, result.ActualHash);
        Assert.True(result.HashMatches);
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);

        File.Delete(path);
    }

    /// <summary>倒帶後、在快照 tick 與倒帶 tick 之間的新指令必須套在紀錄的 tick(不是快照 tick);reset 同理。</summary>
    [Fact]
    public void CommandsAfterRewindReplayAtRecordedTicks()
    {
        var script = new (long, SimCommand)[]
        {
            (100, SimCommand.Rudder(10)),
            (1300, SimCommand.Restore()),          // 回到 tick 1000 的自動快照
            (1050, SimCommand.Rudder(-20)),        // 快照 tick 與倒帶 tick 之間
            (1400, SimCommand.Telegraph(TelegraphOrder.SAH)),
            (1600, SimCommand.Reset()),            // tick 回 0
            (120, SimCommand.Rudder(15)),
        };
        var path = Path.Combine(TestData.TestRecordsDirectory, $"rewind-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        RecordFooter footer;
        using (var writer = new RecordWriter(path, engine))
        {
            RunScriptSequential(engine, 500, script);
            footer = writer.Close();
        }
        Assert.Equal(620, footer.FinalTick);
        var rec = RecordReader.Read(path);
        Assert.Equal(new long[] { 100, 1300, 1050, 1400, 1600, 120 }, rec.Inputs.Select(i => i.Tick));

        // 重播引擎實際套用指令的 tick 必須與紀錄相同
        var applied = new List<(SimCommandType, long)>();
        var result = Replayer.Replay(path, TestData.Ship, configure: e => e.CommandApplied += (_, c, t) => applied.Add((c.Type, t)));
        Assert.Equal(rec.Inputs.Select(i => (i.Command.Type, i.Tick)), applied);
        Assert.Null(result.Warning);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.Equal(620, result.FinalTick);
        File.Delete(path);
    }

    /// <summary>情境切換:紀錄器在引擎重設前自動關閉(footer 為舊情境),loadScenario 不寫 input 行,新情境另開的紀錄可獨立重播。</summary>
    [Fact]
    public void ScenarioLoadClosesRecordBeforeResetAndNewRecordReplays()
    {
        var dir = TestData.TestRecordsDirectory;
        var path1 = Path.Combine(dir, $"load1-{Guid.NewGuid():N}.jsonl");
        var path2 = Path.Combine(dir, $"load2-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        RecordWriter? writer2 = null;
        engine.ScenarioLoadRequested += (e, _) =>
        {
            e.LoadScenario(TestData.E07());
            writer2 = new RecordWriter(path2, e);
        };
        var writer1 = new RecordWriter(path1, engine);
        engine.Enqueue(SimCommand.Rudder(5));
        engine.ProcessPendingCommands();
        TestData.RunTo(engine, 6);
        var hashBefore = engine.StateHash;
        engine.Enqueue(SimCommand.LoadScenario("E07"));
        engine.ProcessPendingCommands();
        Assert.True(writer1.Closed);
        Assert.Equal(300, writer1.Footer!.FinalTick);
        Assert.Equal(hashBefore, writer1.Footer.StateHash);
        Assert.Equal(0, engine.Tick);
        Assert.Equal("E07_steering_failure", engine.Scenario.Id);
        engine.Enqueue(SimCommand.Rudder(-3));
        engine.ProcessPendingCommands();
        TestData.RunTo(engine, 4);
        var footer2 = writer2!.Close();

        var rec1 = RecordReader.Read(path1);
        Assert.True(rec1.IntegrityOk);
        Assert.DoesNotContain(rec1.Inputs, i => i.Command.Type == SimCommandType.LoadScenario);
        var r1 = Replayer.Replay(path1, TestData.Ship);
        Assert.True(r1.HashMatches && r1.EventSequenceMatches, r1.FirstEventMismatch);
        Assert.Null(r1.Warning);

        var rec2 = RecordReader.Read(path2);
        Assert.Equal("E07_steering_failure", rec2.Header.Scenario.Id);
        Assert.DoesNotContain(rec2.Inputs, i => i.Command.Type == SimCommandType.LoadScenario);
        Assert.Equal(new long[] { 0 }, rec2.Inputs.Select(i => i.Tick));
        var r2 = Replayer.Replay(path2, TestData.Ship);
        Assert.Equal(footer2.StateHash, r2.ActualHash);
        Assert.True(r2.HashMatches && r2.EventSequenceMatches, r2.FirstEventMismatch);
        File.Delete(path1);
        File.Delete(path2);
    }

    /// <summary>舊版紀錄含 loadScenario input 行:重播在該行中止並以 Warning 說明(不是默默回報雜湊不一致)。</summary>
    [Fact]
    public void LegacyLoadScenarioInputAbortsReplayWithWarning()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"legacy-load-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        using (var writer = new RecordWriter(path, engine))
        {
            engine.Enqueue(SimCommand.Rudder(5));
            engine.Run(200);
            engine.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            engine.Run(200);
        }
        var lines = File.ReadAllLines(path).ToList();
        var idx = lines.FindIndex(l => l.Contains("\"type\":\"telegraph\""));
        lines.Insert(idx, "{\"kind\":\"input\",\"tick\":300,\"command\":{\"type\":\"loadScenario\",\"value\":\"E07\"}}");
        File.WriteAllLines(path, lines);
        var result = Replayer.Replay(path, TestData.Ship);
        Assert.NotNull(result.Warning);
        Assert.Contains("loadScenario", result.Warning);
        Assert.Equal(1, result.InputsReplayed);
        Assert.Equal(300, result.FinalTick);
        File.Delete(path);
    }

    [Fact]
    public void TamperedRecordFailsIntegrity()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"tamper-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        using (var writer = new RecordWriter(path, engine))
        {
            engine.Enqueue(SimCommand.Rudder(5));
            engine.Run(200);
        }
        var lines = File.ReadAllLines(path);
        var idx = Array.FindIndex(lines, l => l.Contains("\"kind\":\"input\""));
        lines[idx] = lines[idx].Replace("\"value\":5", "\"value\":6");
        File.WriteAllLines(path, lines);

        var rec = RecordReader.Read(path);
        Assert.False(rec.IntegrityOk);
        var result = Replayer.Replay(path, TestData.Ship);
        Assert.False(result.IntegrityOk);
        Assert.False(result.HashMatches);
        File.Delete(path);
    }

    [Fact]
    public void DefaultPathFollowsNamingConvention()
    {
        var p = RecordWriter.DefaultPath("/x/records", "E01_baseline", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.EndsWith("20260102-030405-E01_baseline.jsonl", p);
    }
}
