using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class EngineDeterminismTests
{
    /// <summary>固定的指令腳本:(tick, 指令)。</summary>
    internal static readonly (long Tick, SimCommand Cmd)[] Script =
    {
        (0, SimCommand.Rudder(15)),
        (200, SimCommand.Telegraph(TelegraphOrder.FAH)),
        (450, SimCommand.Thruster(0.5)),
        (800, SimCommand.Rudder(-10)),
        (900, SimCommand.SetEnvironment(windTrueSpeedKn: 25, windTrueDirDeg: 300, currentSetDeg: 90, currentDriftKn: 1.5)),
        (1200, SimCommand.Autopilot(true, heading: 45, rotLimit: 12)),
        (1500, SimCommand.InjectFault(FaultNames.BowThruster)),
    };

    internal static void RunScript(SimulationEngine engine, int totalSteps, IEnumerable<(long Tick, SimCommand Cmd)>? script = null)
    {
        var list = (script ?? Script).OrderBy(x => x.Tick).ToList();
        var idx = 0;
        for (var i = 0; i < totalSteps; i++)
        {
            while (idx < list.Count && list[idx].Tick == engine.Tick) engine.Enqueue(list[idx++].Cmd);
            engine.Step();
        }
    }

    [Fact]
    public void SameInputsGiveSameHash()
    {
        var a = TestData.Engine();
        var b = TestData.Engine();
        RunScript(a, 2000);
        RunScript(b, 2000);
        Assert.Equal(2000, a.Tick);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(a.Motion, b.Motion);
        Assert.Equal(ContractJson.Serialize(a.BuildState()), ContractJson.Serialize(b.BuildState()));
        Assert.NotEqual(new string('0', 64), a.StateHash);
    }

    [Fact]
    public void DifferentSeedGivesDifferentHashWhenGustsActive()
    {
        var a = TestData.Engine(seed: 1);
        var b = TestData.Engine(seed: 2);
        Assert.True(a.Environment.Gustiness > 0, "E01 需有陣風才能驗證亂數");
        RunScript(a, 600);
        RunScript(b, 600);
        Assert.NotEqual(a.StateHash, b.StateHash);
    }

    [Fact]
    public void DifferentInputsGiveDifferentHash()
    {
        var a = TestData.Engine();
        var b = TestData.Engine();
        RunScript(a, 500);
        RunScript(b, 500, new[] { (0L, SimCommand.Rudder(-15)) });
        Assert.NotEqual(a.StateHash, b.StateHash);
    }

    [Fact]
    public void FreezeStopsTimeAndResumeContinues()
    {
        var e = TestData.Engine();
        e.Run(100);
        e.Enqueue(SimCommand.Freeze());
        e.Run(50);
        Assert.Equal(100, e.Tick);
        Assert.True(e.Frozen);
        Assert.True(e.BuildState().Flags!.Frozen);
        e.Enqueue(SimCommand.Resume());
        e.Run(50);
        Assert.Equal(150, e.Tick);
        Assert.Equal(3.0, e.Time, 9);
    }

    [Fact]
    public void TimeScaleCommandClampsAndZeroFreezes()
    {
        var e = TestData.Engine();
        e.Enqueue(SimCommand.TimeScale(4));
        e.ProcessPendingCommands();
        Assert.Equal(4.0, e.TimeScale);
        e.Enqueue(SimCommand.TimeScale(99));
        e.ProcessPendingCommands();
        Assert.Equal(10.0, e.TimeScale);
        e.Enqueue(SimCommand.TimeScale(0));
        e.ProcessPendingCommands();
        Assert.True(e.Frozen);
    }

    [Fact]
    public void ResetReturnsToInitialState()
    {
        var e = TestData.Engine();
        var initialHash = e.StateHash;
        var initialMotion = e.Motion;
        RunScript(e, 1000);
        e.Enqueue(SimCommand.Reset());
        e.ProcessPendingCommands();
        Assert.Equal(0, e.Tick);
        Assert.Equal(initialHash, e.StateHash);
        Assert.Equal(initialMotion, e.Motion);
        Assert.Equal(0, e.Random.Count);
    }

    [Fact]
    public async Task CommandQueueIsThreadSafe()
    {
        var e = TestData.Engine();
        var applied = 0;
        e.CommandApplied += (_, _, _) => applied++;
        var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++) e.Enqueue(SimCommand.Rudder(i % 35));
        })).ToArray();
        await Task.WhenAll(producers);
        e.ProcessPendingCommands();
        Assert.Equal(1000, applied);
        Assert.Equal(0, e.PendingCommandCount);
    }
}
