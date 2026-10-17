using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>環境漸變(情境引擎設計第 3.5 節):線性、最短弧、覆蓋、完成事件、快照中途還原。</summary>
public class EnvironmentRampTests
{
    [Fact]
    public void LinearMidpointAndCompletion()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 10, waterDepth: 20, rampS: 10));
        e.ProcessPendingCommands();
        Assert.Equal(2, e.Ramps.Count);
        TestData.RunTo(e, 5);
        e.Step(); // 漸變於每步推進前以當時 tick 插值:tick 250 的值在第 251 步套用
        Assert.Equal(Units.KnToMps(5), e.Environment.WindTrueSpeedMps, 9);
        Assert.Equal(25.0, e.Environment.Depth.DepthAt(0, 0), 9);
        TestData.RunTo(e, 11);
        Assert.Equal(Units.KnToMps(10), e.Environment.WindTrueSpeedMps, 12);
        Assert.Equal(20.0, e.Environment.Depth.DepthAt(0, 0), 12);
        Assert.Empty(e.Ramps);
        var done = events.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).ToList();
        Assert.Equal(2, done.Count);
        Assert.All(done, x => Assert.Equal(500, x.Tick));
        Assert.Equal(10.0, events.Single(x => x.Kind == SimEventKinds.EnvironmentChange).DataNumber("ramp_s"));
    }

    [Fact]
    public void AngleTakesShortestArcThrough360()
    {
        var e = TestData.MinimalEngine();
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 10, windTrueDirDeg: 350));
        e.ProcessPendingCommands();
        e.Enqueue(SimCommand.SetEnvironment(windTrueDirDeg: 10, rampS: 20));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 10);
        e.Step();
        Assert.Equal(0.0, Units.NormalizeHeadingDeg(Units.RadToDeg(e.Environment.WindTrueDirFromRad)), 6);
        TestData.RunTo(e, 15);
        e.Step();
        Assert.Equal(5.0, Units.NormalizeHeadingDeg(Units.RadToDeg(e.Environment.WindTrueDirFromRad)), 6);
        TestData.RunTo(e, 21);
        Assert.Equal(10.0, Units.NormalizeHeadingDeg(Units.RadToDeg(e.Environment.WindTrueDirFromRad)), 9);
    }

    [Fact]
    public void NewRampOverridesOldFromCurrentValueAndZeroRampIsImmediate()
    {
        var e = TestData.MinimalEngine();
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 10, rampS: 10));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 5);
        e.Step(); // 5 kn(tick 251)
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 25, rampS: 10));
        e.ProcessPendingCommands();
        var r = e.Ramps.Single();
        Assert.Equal(Units.KnToMps(5), r.From, 9);
        Assert.Equal(Units.KnToMps(25), r.To, 9);
        Assert.Equal(251 + 500, r.EndTick);
        TestData.RunTo(e, 10.02);
        e.Step(); // tick 501 的值 = From + (To − From)·(501 − 251)/500 = 15 kn
        Assert.Equal(Units.KnToMps(15), e.Environment.WindTrueSpeedMps, 9);
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 3));
        e.ProcessPendingCommands();
        Assert.Empty(e.Ramps);
        Assert.Equal(Units.KnToMps(3), e.Environment.WindTrueSpeedMps, 9);
    }

    [Fact]
    public void SnapshotMidRampRestoresAndFinishesIdentically()
    {
        var reference = TestData.MinimalEngine(seed: 3);
        reference.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 20, windTrueDirDeg: 300, currentSetDeg: 90, currentDriftKn: 1, rampS: 30));
        reference.ProcessPendingCommands();
        TestData.RunTo(reference, 60);

        var a = TestData.MinimalEngine(seed: 3);
        a.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 20, windTrueDirDeg: 300, currentSetDeg: 90, currentDriftKn: 1, rampS: 30));
        a.ProcessPendingCommands();
        TestData.RunTo(a, 12.3);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(2, snap.Version);
        Assert.Equal(4, snap.Timeline!.Ramps.Count);
        var b = TestData.MinimalEngine(seed: 3);
        b.Restore(snap);
        Assert.Equal(4, b.Ramps.Count);
        TestData.RunTo(b, 60);
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(reference.Environment.WindTrueSpeedMps, b.Environment.WindTrueSpeedMps);
        Assert.Equal(reference.Environment.CurrentSetRad, b.Environment.CurrentSetRad);
        Assert.Empty(b.Ramps);
    }


    /// <summary>情境未設能見度(無限制)時,帶 ramp_s 的 setEnvironment 直接設為目標值,不從 0 nm 起算;事件 from 為 null。</summary>
    [Fact]
    public void VisibilityRampFromUnlimitedIsImmediate()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        Assert.Null(e.VisibilityNm);
        TestData.RunTo(e, 10);
        e.Enqueue(SimCommand.SetEnvironment(visibilityNm: 2, rampS: 60)); e.ProcessPendingCommands();
        Assert.Equal(2.0, e.VisibilityNm);
        Assert.Empty(e.Ramps);
        e.Step();
        Assert.Equal(2.0, e.VisibilityNm);
        TestData.RunTo(e, 40);
        Assert.Equal(2.0, e.VisibilityNm);
        var change = events.Single(x => x.Kind == SimEventKinds.EnvironmentChange);
        var vis = change.Data!.Value.GetProperty("changes").GetProperty("visibility");
        Assert.True(!vis.TryGetProperty("from", out var from) || from.ValueKind == System.Text.Json.JsonValueKind.Null); // from = null(序列化時省略)
        Assert.Equal(2.0, vis.GetProperty("to").GetDouble());
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.EnvironmentRampDone);
        // 已有有限能見度時照常漸變
        e.Enqueue(SimCommand.SetEnvironment(visibilityNm: 4, rampS: 10)); e.ProcessPendingCommands();
        Assert.Single(e.Ramps);
        TestData.RunTo(e, 51);
        Assert.Equal(4.0, e.VisibilityNm!.Value, 9);
    }
}
