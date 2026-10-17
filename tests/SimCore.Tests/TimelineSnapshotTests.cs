using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>快照 → 還原 → 續跑 的事件序列與雜湊 = 連續跑(含待執行 delay_s、進行中漸變、holdFor 中的項目);Version 1 快照可還原。</summary>
public class TimelineSnapshotTests
{
    private const string Extra = """
        route:
          xtdLimit_m: 100
          waypoints: [ { name: A, x: 0, y: 0 }, { name: B, x: 0, y: 20000 } ]
        timeline:
          - id: windUp
            when: { type: time, atLeast: 20 }
            actions: [ { setEnvironment: { wind: { trueSpeed: 20, trueDir: 270 }, ramp_s: 60 } } ]
          - id: delayed
            delay_s: 30
            when: { type: time, atLeast: 25 }
            actions: [ { injectFault: steeringGear }, { message: { to: instructor, text: jam } } ]
          - id: held
            holdFor_s: 40
            when: { type: fault, name: steeringGear, active: true }
            actions: [ { phase: emergency } ]
          - id: coach
            repeat: always
            cooldown_s: 15
            when: { type: time, atLeast: 10 }
            actions: [ { message: { to: instructor, text: tick } } ]
        assessment:
          hardFail: [ { id: g, rule: grounding } ]
        """;

    [Fact]
    public void RestoreMidTimelineMatchesContinuousRun()
    {
        var reference = TestData.MinimalEngine(Extra, seed: 11);
        var refEvents = TestData.Capture(reference);
        TestData.RunTo(reference, 140);

        var a = TestData.MinimalEngine(Extra, seed: 11);
        var aEvents = TestData.Capture(a);
        TestData.RunTo(a, 47); // 漸變中、delay 待執行中、holdFor 尚未成立
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Single(snap.Timeline!.PendingActions.Select(p => p.ItemId).Distinct());
        Assert.Equal(2, snap.Timeline.Ramps.Count);
        Assert.NotNull(snap.Assessment);
        Assert.Equal(a.EventSeq, snap.Timeline.Seq);
        var seqAtSnap = a.EventSeq;

        // 弄亂後還原
        TestData.RunTo(a, 100);
        a.Enqueue(SimCommand.InjectFault("x")); a.ProcessPendingCommands();
        a.Restore(snap);
        Assert.Equal(seqAtSnap, a.EventSeq);
        Assert.Equal(snap.StateHash, a.StateHash);
        TestData.RunTo(a, 140);
        Assert.Equal(reference.StateHash, a.StateHash);

        // 全新引擎還原
        var b = TestData.MinimalEngine(Extra, seed: 11);
        var bEvents = TestData.Capture(b);
        b.Restore(snap);
        TestData.RunTo(b, 140);
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal("emergency", b.Phase);
        Assert.Equal(reference.Phase, b.Phase);
        var refAfter = refEvents.Where(x => x.Seq >= seqAtSnap).Select(x => (x.Seq, x.Kind, x.Tick, x.ItemId)).ToList();
        Assert.Equal(refAfter, bEvents.Select(x => (x.Seq, x.Kind, x.Tick, x.ItemId)).ToList());
        Assert.Single(bEvents, x => x.Kind == SimEventKinds.FaultInjected); // 延遲動作不重複觸發
        Assert.Equal(ContractJson.Serialize(reference.LatestSummary), ContractJson.Serialize(b.LatestSummary));
        Assert.Equal(ContractJson.Serialize(reference.BuildState()), ContractJson.Serialize(b.BuildState()));
    }

    [Fact]
    public void RestoringSameSnapshotTwiceDoesNotDuplicateEvents()
    {
        var e = TestData.MinimalEngine(Extra, seed: 11);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 30);
        var snap = e.CreateSnapshot();
        TestData.RunTo(e, 70); // 延遲動作於 55 s 執行
        Assert.Single(events, x => x.Kind == SimEventKinds.FaultInjected);
        e.Restore(snap);
        events.Clear();
        TestData.RunTo(e, 70);
        Assert.Single(events, x => x.Kind == SimEventKinds.FaultInjected);
        Assert.Equal(55.0, events.Single(x => x.Kind == SimEventKinds.FaultInjected).T);
    }

    [Fact]
    public void Version1SnapshotWithoutTimelineRestores()
    {
        var e = TestData.MinimalEngine(Extra, seed: 5);
        TestData.RunTo(e, 15);
        var snap = e.CreateSnapshot() with { Version = 1, Timeline = null, Assessment = null, Phase = null, VisibilityNm = null, ParameterSetVersion = null };
        var json = snap.ToJson();
        Assert.DoesNotContain("\"timeline\"", json);
        var b = TestData.MinimalEngine(Extra, seed: 5);
        b.Restore(EngineSnapshot.FromJson(json));
        Assert.Equal(750, b.Tick);
        Assert.Equal("normal", b.Phase);
        TestData.RunTo(b, 30);
        Assert.Equal(1500, b.Tick);
    }
}
