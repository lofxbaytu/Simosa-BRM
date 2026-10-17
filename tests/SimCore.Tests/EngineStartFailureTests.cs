using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Recording;
using Xunit;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// engineStartFailure 故障(fault-catalogue modelled;規劃書第 8.1 節 E14「主機起動延遲」):由 STOP 轉進/倒車時起動延遲(args.delay_s)、
/// 先失敗 N 次(args.attempts)或持續失敗(無 args,連續 3 次封鎖);每次失敗消耗起動空氣;換向後反向點火亦受影響;
/// 暫代模型同樣適用;快照/雜湊/紀錄重播一致;未使用時雜湊不變。
/// </summary>
public class EngineStartFailureTests
{
    private const double StartDelay = 5.0; // data/ships/FSB1/coefficients.full.json engine.startDelay_s

    private static SimulationEngine Stopped() => MmgEngine(MmgScenario(0.0, telegraph: "STOP"));

    private static void Apply(SimulationEngine e, SimCommand c)
    {
        e.Enqueue(c);
        e.ProcessPendingCommands();
    }

    [Fact]
    public void AttemptsFailThenStartAndConsumeStartingAir()
    {
        var e = Stopped();
        var events = TestData.Capture(e);
        var starts0 = e.BuildState().Engine!.StartsRemaining!.Value;
        Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure, new { attempts = 2 }));
        TestData.RunTo(e, 1);
        Apply(e, SimCommand.Telegraph(TelegraphOrder.DSAH));
        TestData.RunTo(e, 1 + 2 * StartDelay + 0.5);
        Assert.Equal(0.0, e.Actuators.Rpm, 9);                           // 兩次失敗:未點火
        var failed = events.Where(x => x.Kind == SimEventKinds.EngineStartFailed).ToList();
        Assert.Equal(2, failed.Count);
        Assert.Equal(1 + StartDelay, failed[0].T, 6);                    // 第 1 次嘗試於起動時間到達時失敗
        Assert.Equal(1 + 2 * StartDelay, failed[1].T, 6);                // 自動再試(再一個起動時間)
        Assert.All(failed, f => { Assert.Equal(SimEventSources.Engine, f.Source); Assert.Equal(FaultNames.EngineStartFailure, f.ItemId); Assert.True(f.Visible); });
        Assert.Equal(EngineRunState.Starting, e.BuildState().Engine!.State);
        TestData.RunTo(e, 1 + 3 * StartDelay + 1);
        var started = Assert.Single(events, x => x.Kind == SimEventKinds.EngineStarted);
        Assert.Equal(1 + 3 * StartDelay, started.T, 6);
        Assert.Equal(3 * StartDelay, started.DataNumber("after_s")!.Value, 1); // 自下令起算的起動計時(含點火步)
        Assert.Equal(2, started.DataNumber("failedAttempts"));
        Assert.True(e.Actuators.Rpm > 0);
        Assert.Equal(starts0 - 3, e.BuildState().Engine!.StartsRemaining);   // 下令 1 次 + 自動再試 2 次
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.EngineStartBlocked);
    }

    [Fact]
    public void DelayPostponesEveryStartWhileActive()
    {
        var e = Stopped();
        var events = TestData.Capture(e);
        Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure, new { delay_s = 20 }));
        Apply(e, SimCommand.Telegraph(TelegraphOrder.DSAH));
        TestData.RunTo(e, StartDelay + 19);
        Assert.Equal(0.0, e.Actuators.Rpm, 9);
        TestData.RunTo(e, StartDelay + 21);
        var started = Assert.Single(events, x => x.Kind == SimEventKinds.EngineStarted);
        Assert.Equal(StartDelay + 20, started.T, 1);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.EngineStartFailed);
        // 清除後恢復正常起動時間
        Apply(e, SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 200);
        Apply(e, SimCommand.ClearFault(FaultNames.EngineStartFailure));
        var t0 = e.Time;
        Apply(e, SimCommand.Telegraph(TelegraphOrder.DSAH));
        TestData.RunTo(e, t0 + StartDelay + 1);
        Assert.True(e.Actuators.Rpm > 0);
        Assert.Single(events, x => x.Kind == SimEventKinds.EngineStarted); // 故障清除後不再發 engineStarted
    }

    [Fact]
    public void PersistentFailureBlocksAfterThreeAttemptsUntilStopAndClear()
    {
        var e = Stopped();
        var events = TestData.Capture(e);
        Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure));
        Apply(e, SimCommand.Telegraph(TelegraphOrder.SAH));
        TestData.RunTo(e, 60);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.EngineStartFailed));
        var blocked = Assert.Single(events, x => x.Kind == SimEventKinds.EngineStartBlocked);
        Assert.Equal(3 * StartDelay, blocked.T, 6);
        Assert.True(e.Actuators.StartBlocked);
        Assert.Equal(EngineRunState.Failed, e.BuildState().Engine!.State);
        Assert.Equal(0.0, e.Actuators.Rpm, 9);
        // 車令回 STOP 解除封鎖;故障仍在 → 再下令又失敗並封鎖
        Apply(e, SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 61);
        Assert.False(e.Actuators.StartBlocked);
        Apply(e, SimCommand.Telegraph(TelegraphOrder.SAH));
        TestData.RunTo(e, 100);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.EngineStartBlocked));
        // 清除故障:封鎖解除,下一個起動時間點火
        Apply(e, SimCommand.ClearFault(FaultNames.EngineStartFailure));
        Assert.False(e.Actuators.StartBlocked);
        TestData.RunTo(e, 100 + StartDelay + 1);
        Assert.True(e.Actuators.Rpm > 0);
        Assert.Equal(EngineRunState.Running, e.BuildState().Engine!.State);
    }

    [Fact]
    public void ReversalIgnitionIsAStartAttempt()
    {
        var e = MmgEngine(MmgScenario(6.0, telegraph: "SAH"));
        var events = TestData.Capture(e);
        TestData.RunTo(e, 10);
        Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure, new { attempts = 1 }));
        Apply(e, SimCommand.Telegraph(TelegraphOrder.HAS));
        TestData.RunTo(e, 400);
        var failed = Assert.Single(events, x => x.Kind == SimEventKinds.EngineStartFailed);
        var started = Assert.Single(events, x => x.Kind == SimEventKinds.EngineStarted);
        Assert.Equal(StartDelay, started.T - failed.T, 6);              // 失敗後一個起動時間再點火
        Assert.True(failed.T - 10 >= 110.0 - 1e-6);                      // 換向延遲(reversalDelay_s 110)之後才嘗試反向點火
        Assert.True(e.Actuators.Rpm < 0);
        // 已運轉中的主機不受影響(改變同向車令不需起動)
        Apply(e, SimCommand.Telegraph(TelegraphOrder.FAS));
        TestData.RunTo(e, 420);
        Assert.Single(events, x => x.Kind == SimEventKinds.EngineStartFailed);
    }

    [Fact]
    public void PlaceholderDynamicsAlsoGatesStart()
    {
        var sc = TestData.Minimal(speedKn: 0.0);
        sc.Initial.Telegraph = "STOP";
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        var events = TestData.Capture(e);
        Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure, new { attempts = 1 }));
        Apply(e, SimCommand.Telegraph(TelegraphOrder.HAH));
        TestData.RunTo(e, 0.1);
        Assert.Single(events, x => x.Kind == SimEventKinds.EngineStartFailed);   // 暫代模型無起動時間:第 1 次嘗試立即失敗
        Assert.Equal(0.0, e.Actuators.Rpm, 9);
        TestData.RunTo(e, e.Actuators.Parameters.StartDelaySec + 1);
        Assert.Single(events, x => x.Kind == SimEventKinds.EngineStarted);
        Assert.True(e.Actuators.Rpm > 0);
    }

    /// <summary>
    /// 暫代模型:情境開始即進車(狀態機初值 Run、目標 +100 rpm),故障未成立時停俥、軸停下後才注入 engineStartFailure(無 args = 每次失敗)
    /// → 再進車須經起動閘(失敗 2 次後封鎖),不得沿用故障未成立期間未更新的「運轉中」狀態;清除後運轉、再停俥並重新注入亦同。
    /// </summary>
    [Fact]
    public void PlaceholderInjectAfterStopWithoutFaultStillGatesStart()
    {
        var e = new SimulationEngine(TestData.Fsb1(), TestData.Minimal(speedKn: 8.0)); // HAH
        var events = TestData.Capture(e);
        Assert.Equal(EngineMode.Run, e.Actuators.EngineMode);
        Apply(e, SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 120);
        Assert.True(Math.Abs(e.Actuators.Rpm) < e.Actuators.Parameters.ShaftStillRpm);
        for (var round = 0; round < 2; round++)
        {
            var t0 = e.Time;
            Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure));
            Assert.Equal(EngineMode.Stopped, e.Actuators.EngineMode);
            Apply(e, SimCommand.Telegraph(TelegraphOrder.HAH));
            TestData.RunTo(e, t0 + 40);
            Assert.True(Math.Abs(e.Actuators.Rpm) < 1e-3, $"第 {round + 1} 輪:封鎖中不得起動(rpm {e.Actuators.Rpm})");
            Assert.True(e.Actuators.StartBlocked);
            Assert.Equal(2 * (round + 1), events.Count(x => x.Kind == SimEventKinds.EngineStartFailed));
            Assert.Equal(round + 1, events.Count(x => x.Kind == SimEventKinds.EngineStartBlocked));
            Assert.Equal(EngineRunState.Failed, e.BuildState().Engine!.State);
            // 清除故障 → 起動運轉;故障未成立時停俥到軸靜止,下一輪再注入
            Apply(e, SimCommand.ClearFault(FaultNames.EngineStartFailure));
            TestData.RunTo(e, t0 + 80);
            Assert.True(e.Actuators.Rpm > 50.0);
            Apply(e, SimCommand.Telegraph(TelegraphOrder.STOP));
            TestData.RunTo(e, t0 + 220);
        }
    }

    /// <summary>
    /// 起動封鎖後注入並清除 mainEngine(車令一直維持 SAH、未回 STOP):封鎖不得解除、不得自動再試或再消耗起動空氣
    /// (契約:連續 3 次失敗起動封鎖,須車令回 STOP 再下令)。MMG 與暫代模型皆同。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MainEngineFaultDoesNotLiftStartBlock(bool mmg)
    {
        SimulationEngine e;
        if (mmg) e = Stopped();
        else
        {
            var sc = TestData.Minimal(speedKn: 0.0);
            sc.Initial.Telegraph = "STOP";
            e = new SimulationEngine(TestData.Fsb1(), sc);
        }
        var events = TestData.Capture(e);
        Apply(e, SimCommand.InjectFault(FaultNames.EngineStartFailure));
        Apply(e, SimCommand.Telegraph(TelegraphOrder.SAH));
        TestData.RunTo(e, 30);
        Assert.True(e.Actuators.StartBlocked);
        var starts = e.BuildState().Engine!.StartsRemaining;
        Assert.Single(events, x => x.Kind == SimEventKinds.EngineStartBlocked);
        var failed = events.Count(x => x.Kind == SimEventKinds.EngineStartFailed);
        Apply(e, SimCommand.InjectFault(FaultNames.MainEngine));
        TestData.RunTo(e, 31);
        Assert.True(e.Actuators.StartBlocked);
        Apply(e, SimCommand.ClearFault(FaultNames.MainEngine));
        TestData.RunTo(e, 70);
        Assert.True(e.Actuators.StartBlocked);
        Assert.Equal(starts, e.BuildState().Engine!.StartsRemaining);
        Assert.Single(events, x => x.Kind == SimEventKinds.EngineStartBlocked);
        Assert.Equal(failed, events.Count(x => x.Kind == SimEventKinds.EngineStartFailed));
        Assert.Equal(0.0, e.Actuators.Rpm, 6);
        Assert.Equal(EngineRunState.Failed, e.BuildState().Engine!.State);
        // 車令回 STOP 才解除封鎖
        Apply(e, SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 71);
        Assert.False(e.Actuators.StartBlocked);
    }

    [Fact]
    public void SnapshotMidAttemptContinuesBitIdenticalAndRecordReplays()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"startfail-{Guid.NewGuid():N}.jsonl");
        var a = Stopped();
        EngineSnapshot snap;
        using (var w = new RecordWriter(path, a))
        {
            Apply(a, SimCommand.InjectFault(FaultNames.EngineStartFailure, new { attempts = 2, delay_s = 3 }));
            Apply(a, SimCommand.Telegraph(TelegraphOrder.DSAH));
            TestData.RunTo(a, StartDelay + 4);                          // 第 1 次失敗之後、第 2 次之前
            Assert.Equal(1, a.Actuators.StartFailedAttempts);
            snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
            TestData.RunTo(a, 60);
            w.Close();
        }
        Assert.Equal(3.0, snap.Actuators.StartFailureDelayS);
        Assert.Equal(2, snap.Actuators.StartFailureAttempts);
        Assert.Equal(1, snap.Actuators.StartFailedAttempts);
        var b = Stopped();
        b.Restore(snap);
        TestData.RunTo(b, 60);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(a.Actuators.StartFailedAttempts, b.Actuators.StartFailedAttempts);

        var r = Replayer.Replay(path, TestData.Ship, (s, l) => new Physics.Mmg.MmgDynamics(s, l, Fsb1Full));
        Assert.True(r.HashMatches);
        Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
    }

    [Fact]
    public void UnusedFaultKeepsSnapshotJsonAndHashLayout()
    {
        // 未注入:致動器快照 JSON 不含起動故障欄位(與舊版相同),狀態無非預設值;注入其他故障不影響
        var e = Stopped();
        Apply(e, SimCommand.Telegraph(TelegraphOrder.DSAH));
        Apply(e, SimCommand.InjectFault(FaultNames.BowThruster));
        TestData.RunTo(e, 30);
        Assert.False(e.Actuators.HasStartFailureState);
        Assert.DoesNotContain("startFailure", e.CreateSnapshot().ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartBlocked", e.CreateSnapshot().ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.True(FaultNames.Modelled.Contains(FaultNames.EngineStartFailure));
    }
}
