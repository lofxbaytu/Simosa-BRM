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
}
