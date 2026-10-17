using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class SnapshotRestoreTests
{
    [Fact]
    public void RestoreThenContinueMatchesUninterruptedRun()
    {
        // 不中斷:0 → 2000
        var reference = TestData.Engine();
        EngineDeterminismTests.RunScript(reference, 2000);

        // 中斷:0 → 1000,快照(經 JSON 往返),接續 → 2000
        var a = TestData.Engine();
        EngineDeterminismTests.RunScript(a, 1000);
        var snapJson = a.CreateSnapshot().ToJson();
        var snap = EngineSnapshot.FromJson(snapJson);
        Assert.Equal(1000, snap.Tick);
        Assert.Equal(a.StateHash, snap.StateHash);

        // 還原到同一引擎(先把它弄亂)
        a.Run(300);
        a.Enqueue(SimCommand.Rudder(-30));
        a.Run(10);
        a.Restore(snap);
        Assert.Equal(1000, a.Tick);
        Assert.Equal(snap.Motion.U, a.Motion.U); // 位元一致由 JSON 最短往返格式保證
        Assert.Equal(snap.StateHash, a.StateHash);
        EngineDeterminismTests.RunScript(a, 1000, EngineDeterminismTests.Script.Where(s => s.Tick >= 1000));
        Assert.Equal(2000, a.Tick);
        Assert.Equal(reference.StateHash, a.StateHash);
        Assert.Equal(reference.Motion, a.Motion);

        // 還原到全新引擎
        var b = TestData.Engine();
        b.Restore(snap);
        Assert.Equal(1000, b.Tick);
        Assert.Equal(snap.RandomCount, b.Random.Count);
        EngineDeterminismTests.RunScript(b, 1000, EngineDeterminismTests.Script.Where(s => s.Tick >= 1000));
        Assert.Equal(reference.StateHash, b.StateHash);
    }

    [Fact]
    public void SnapshotPreservesRandomStateWithGusts()
    {
        var reference = TestData.Engine(seed: 7);
        reference.Run(1500);

        var a = TestData.Engine(seed: 7);
        a.Run(777); // 非整秒,確保亂數計數正確
        var snap = a.CreateSnapshot();
        Assert.Equal(16, snap.RandomCount); // ticks 0,50,…,750 各抽一次
        var b = TestData.Engine(seed: 7);
        b.Restore(snap);
        b.Run(1500 - 777);
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(reference.Environment.GustFactor, b.Environment.GustFactor);
    }

    [Fact]
    public void AutoSnapshotsAndRestoreCommandRewind()
    {
        var e = TestData.Engine();
        var taken = new List<long>();
        e.SnapshotTaken += (_, s) => taken.Add(s.Tick);
        e.Run(1250);
        Assert.Equal(new long[] { 500, 1000 }, taken);
        Assert.Equal(2, e.Snapshots.Count);

        var hashAt1000 = e.Snapshots[1].StateHash;
        e.Enqueue(SimCommand.Restore());
        e.ProcessPendingCommands();
        Assert.Equal(1000, e.Tick);
        Assert.Equal(hashAt1000, e.StateHash);
        Assert.Equal(0, e.Snapshots.Count(s => s.Tick > 1000));
    }

    [Fact]
    public void RestoreRejectsMismatchedShip()
    {
        var e = TestData.Engine();
        var snap = e.CreateSnapshot() with { ShipId = "FSB2" };
        Assert.Throws<InvalidOperationException>(() => e.Restore(snap));
    }


    /// <summary>教官指令還原保留目前的凍結狀態與時間倍率;練習結束後不能倒帶。</summary>
    [Fact]
    public void RestoreCommandKeepsFrozenAndTimeScaleAndIsRefusedAfterEnd()
    {
        var e = TestData.Engine();
        var events = TestData.Capture(e);
        e.Run(1250); // 自動快照 500、1000(未凍結、倍率 1)
        e.Enqueue(SimCommand.TimeScale(4)); e.Enqueue(SimCommand.Freeze()); e.ProcessPendingCommands();
        Assert.True(e.Frozen);
        e.Enqueue(SimCommand.Restore()); e.ProcessPendingCommands();
        Assert.Equal(1000, e.Tick);
        Assert.True(e.Frozen, "凍結中倒帶不得自行恢復執行");
        Assert.Equal(4.0, e.TimeScale);
        e.Step();
        Assert.Equal(1000, e.Tick);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.Resumed);

        // 直接呼叫 Restore(快照) 仍完全依快照(測試/工具路徑)
        e.Restore(e.Snapshots[^1]);
        Assert.False(e.Frozen);
        Assert.Equal(1.0, e.TimeScale);

        e.Run(300);
        e.Enqueue(SimCommand.EndExercise("done")); e.ProcessPendingCommands();
        Assert.True(e.Ended);
        var hash = e.StateHash;
        Assert.False(e.RestoreFromHistory());
        e.Enqueue(SimCommand.Restore()); e.ProcessPendingCommands();
        Assert.Equal(1300, e.Tick);
        Assert.Equal(hash, e.StateHash);
        Assert.True(e.Ended);
    }

    /// <summary>快照時能見度為無限制(null)而之後設為有限值:還原後回到 null(Version 2 快照直接取快照值)。</summary>
    [Fact]
    public void RestoreBringsBackUnlimitedVisibility()
    {
        var e = TestData.MinimalEngine();
        Assert.Null(e.VisibilityNm);
        TestData.RunTo(e, 5);
        var snap = e.TakeSnapshot();
        Assert.Null(snap.VisibilityNm);
        e.Enqueue(SimCommand.SetEnvironment(visibilityNm: 1)); e.ProcessPendingCommands();
        Assert.Equal(1.0, e.VisibilityNm);
        e.Restore(snap);
        Assert.Null(e.VisibilityNm);
        // Version 1 快照沒有此欄位:沿用目前值
        e.Enqueue(SimCommand.SetEnvironment(visibilityNm: 2)); e.ProcessPendingCommands();
        e.Restore(snap with { Version = 1 });
        Assert.Equal(2.0, e.VisibilityNm);
    }
}
