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

    private static void RunScriptSequential(SimulationEngine engine, int stepsAfterLast)
    {
        // 依腳本順序執行(含倒帶,所以不能用 tick 排序)
        foreach (var (tick, cmd) in Script)
        {
            while (engine.Tick < tick) engine.Step();
            engine.Enqueue(cmd);
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
