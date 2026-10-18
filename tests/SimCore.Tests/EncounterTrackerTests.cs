using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>會遇追蹤(情境引擎設計第 4.4 節):偵測、首次行動、航向改變幅度、escort 排除、會遇結束。</summary>
public class EncounterTrackerTests
{
    private const string HeadOn = """
        targets:
          - id: T1
            initial: { position: { x: 0, y: 7408 }, heading: 180, speed: 6 }
          - id: PB
            shipType: pilot
            loa: 18
            beam: 5
            initial: { position: { x: 100, y: 100 }, heading: 0, speed: 8 }
            behaviour: { mode: follow, follow: { leader: own, bearing: 120, range: 70 } }
        assessment:
          minCpa_nm: 0.5
          encounter: { detectionRange_nm: 3, substantialCourseChange_deg: 30 }
        """;

    [Fact]
    public void HeadOnTargetIsDetectedFirstActionTimedAndCourseChangeMeasured()
    {
        var e = TestData.MinimalEngine(HeadOn, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 250); // 4 nm 距離、閉合 14 kn → 約 1 nm/4.3 min;3 nm 約 257 s... 以事件判定
        TestData.RunTo(e, 400);
        var started = events.Single(x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "T1");
        Assert.InRange(started.T, 200, 300);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "PB");
        e.Enqueue(SimCommand.Rudder(20)); // 首次行動
        e.ProcessPendingCommands();
        var actionT = e.Time;
        Assert.Single(events, x => x.Kind == SimEventKinds.EncounterFirstAction && x.TargetId == "T1");
        TestData.RunTo(e, 520);
        var rec = e.Assessment!.Encounters.State.Records.Single(r => r.TargetId == "T1");
        Assert.Equal(started.T, rec.DetectedAtS);
        Assert.Equal(actionT, rec.FirstActionAtS);
        Assert.Equal(actionT - started.T, rec.TimeToActionS!.Value, 9);
        Assert.Equal("rudder", rec.FirstAction);
        Assert.True(rec.CourseChangeDeg >= 30, $"course change {rec.CourseChangeDeg}");
        Assert.True(rec.Substantial);
        Assert.NotNull(rec.MinCpaNm);
        Assert.NotNull(rec.MinRangeNm);
        Assert.NotNull(rec.TcpaAtDetectionMin);
        var summary = e.Assessment.Encounters.Summary();
        Assert.Equal(1, summary.Encounters); // escort 不計
        Assert.DoesNotContain(summary.Targets, t => t.TargetId == "PB"); // escort 不建立會遇紀錄
        Assert.Equal(rec.MinRangeNm, summary.MinCpaNm);
    }

    [Fact]
    public void EncounterClosesAfterPassingAndOnRemoval()
    {
        var e = TestData.MinimalEngine("""
            targets:
              - id: T1
                initial: { position: { x: 300, y: 2500 }, heading: 180, speed: 10 }
              - id: T2
                initial: { position: { x: 300, y: 3000 }, heading: 180, speed: 10 }
            assessment: { minCpa_nm: 0.5, encounter: { detectionRange_nm: 3 } }
            """, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 5);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.EncounterStarted));
        e.Enqueue(SimCommand.TargetRemove("T2"));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 400); // 18 kn 閉合:約 270 s 通過,再 30 s 距離增加
        var closed = events.Where(x => x.Kind == SimEventKinds.EncounterClosed).ToList();
        Assert.Equal(2, closed.Count);
        Assert.Equal("removed", closed.Single(x => x.TargetId == "T2").DataString("reason"));
        Assert.Equal("passed", closed.Single(x => x.TargetId == "T1").DataString("reason"));
        Assert.All(e.Assessment!.Encounters.State.Records, r => Assert.NotNull(r.ClosedAtS));
    }


    /// <summary>會遇關閉後同一目標再接近:再次成立開始條件即新開一筆並追蹤 minRange;minCpa_nm 與硬性失敗 cpa 反映真實最小通過距離。</summary>
    [Fact]
    public void ReapproachAfterClosedEncounterIsTrackedAndTriggersCpaHardFail()
    {
        var e = TestData.MinimalEngine("""
            targets:
              - id: A1
                initial: { position: { x: 1100, y: 1500 }, heading: 40 }
                behaviour: { mode: anchored }
            assessment:
              minCpa_nm: 0.5
              encounter: { detectionRange_nm: 3 }
              hardFail:
                - { id: cpaHard, rule: cpa, lessThan: 0.2 }
            """, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 500);
        var closed = events.Single(x => x.Kind == SimEventKinds.EncounterClosed && x.TargetId == "A1");
        var rec0 = e.Assessment!.Encounters.State.Records.Single();
        Assert.True(rec0.Closed);
        Assert.InRange(rec0.MinRangeNm!.Value, 0.55, 0.65);
        Assert.False(e.Assessment.Rules.HardFailed);

        // 把目標移到自船航跡旁 60 m、前方 1500 m(TCPA > 0、CPA 0.03 nm → 重新成立開始條件)
        e.Enqueue(SimCommand.TargetControl(new { id = "A1", position = new { x = 60.0, y = e.Motion.Y + 1500.0 } }));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 900);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "A1"));
        Assert.True(events.IndexOf(events.Last(x => x.Kind == SimEventKinds.EncounterStarted)) > events.IndexOf(closed));
        var records = e.Assessment.Encounters.State.Records;
        Assert.Equal(2, records.Count);
        Assert.InRange(records[1].MinRangeNm!.Value, 0.0, 0.05);
        var summary = e.LatestSummary!;
        Assert.InRange(summary.CollisionAvoidance.MinCpaNm!.Value, 0.0, 0.05);
        Assert.Equal(2, summary.CollisionAvoidance.Encounters);
        Assert.True(summary.Assessment.HardFailed);
        Assert.Contains(summary.Assessment.HardFails, h => h.Id == "cpaHard");
    }

    /// <summary>關閉後再接近但未重新成立開始條件(例如橫向漂近,TCPA ≤ 0):仍把更小的距離記到該目標最後一筆紀錄。</summary>
    [Fact]
    public void ClosedEncounterStillTracksSmallerRangeWithoutReopening()
    {
        var e = TestData.MinimalEngine("""
            targets:
              - id: A1
                initial: { position: { x: 1100, y: 1500 }, heading: 40 }
                behaviour: { mode: anchored }
            assessment:
              minCpa_nm: 0.5
              encounter: { detectionRange_nm: 3 }
            """, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 500);
        var rec = e.Assessment!.Encounters.State.Records.Single();
        Assert.True(rec.Closed);
        var before = rec.MinRangeNm!.Value;
        // 目標移到正橫 60 m(TCPA ≈ 0、距離增加)→ 不新開會遇
        e.Enqueue(SimCommand.TargetControl(new { id = "A1", position = new { x = 60.0, y = e.Motion.Y } }));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 505);
        Assert.Single(e.Assessment.Encounters.State.Records);
        Assert.Equal(1, events.Count(x => x.Kind == SimEventKinds.EncounterStarted));
        Assert.True(rec.MinRangeNm < before);
        Assert.InRange(rec.MinRangeNm!.Value, 0.0, 0.05);
        Assert.InRange(e.LatestSummary!.CollisionAvoidance.MinCpaNm!.Value, 0.0, 0.05);
    }

    /// <summary>
    /// expect 事件 encounterFirstAction 的 <c>includePrior: true</c>(情境引擎設計第 4.4 節;E15 審查發現):規則的 after 晚於會遇開始、
    /// 首次行動又發生在 after 之前時,該會遇不會再發 encounterFirstAction —— 會遇仍進行中則於開始計時當下以 dt = 0 判 met(data.prior_t = 首次行動時刻);
    /// 未加 includePrior 的規則語意不變(等不到、逾時 missed)。
    /// </summary>
    [Fact]
    public void IncludePriorCountsFirstActionBeforeRuleStart()
    {
        var e = TestData.MinimalEngine("""
            targets:
              - id: T1
                initial: { position: { x: 0, y: 7408 }, heading: 180, speed: 6 }
            timeline:
              - { id: go, when: { type: time, atLeast: 300 }, actions: [ { message: { to: all, text: go } } ] }
            assessment:
              minCpa_nm: 0.5
              encounter: { detectionRange_nm: 3 }
              required:
                - { id: withPrior, after: { event: timelineTriggered, itemId: go }, expect: { event: { kind: encounterFirstAction, targetId: T1, includePrior: true } }, within_s: 60, penalty: 10 }
                - { id: anyWithPrior, after: { event: timelineTriggered, itemId: go }, expect: { any: [ { acknowledge: { kind: report } }, { event: { kind: encounterFirstAction, targetId: T1, includePrior: true } } ] }, within_s: 60, penalty: 10 }
                - { id: plain, after: { event: timelineTriggered, itemId: go }, expect: { event: { kind: encounterFirstAction, targetId: T1 } }, within_s: 60, penalty: 10 }
            """, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 270);
        var started = events.Single(x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "T1");
        Assert.True(started.T < 270);
        e.Enqueue(SimCommand.Rudder(20));
        e.ProcessPendingCommands();
        var actionT = e.Time;
        TestData.RunTo(e, 400);
        Assert.Single(events, x => x.Kind == SimEventKinds.EncounterFirstAction);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        foreach (var id in new[] { "withPrior", "anyWithPrior" })
        {
            var r = s.Assessment.Required.Single(q => q.Id == id);
            Assert.Equal(("met", 0.0, "event:encounterFirstAction", 0.0), (r.State, r.DtS, r.By, r.Penalty));
            var met = events.Single(x => x.Kind == SimEventKinds.RequiredEventMet && x.ItemId == id);
            Assert.Equal((300.0, actionT), (met.T, met.DataNumber("prior_t")));
        }
        Assert.Equal("missed", s.Assessment.Required.Single(q => q.Id == "plain").State);
        // 一般路徑(未在 after 之前行動)不帶 prior_t
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.RequiredEventMet && x.ItemId == "plain");
    }

    /// <summary>includePrior 不計入已結束的會遇(首次行動屬於前一次會遇)。</summary>
    [Fact]
    public void IncludePriorIgnoresClosedEncounter()
    {
        var e = TestData.MinimalEngine("""
            targets:
              - id: A1
                initial: { position: { x: 300, y: 1500 }, heading: 40 }
                behaviour: { mode: anchored }
            timeline:
              - { id: go, when: { type: time, atLeast: 500 }, actions: [ { message: { to: all, text: go } } ] }
            assessment:
              minCpa_nm: 0.5
              encounter: { detectionRange_nm: 3 }
              required:
                - { id: withPrior, after: { event: timelineTriggered, itemId: go }, expect: { event: { kind: encounterFirstAction, targetId: A1, includePrior: true } }, within_s: 30, penalty: 10 }
            """, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 20);
        Assert.Single(events, x => x.Kind == SimEventKinds.EncounterStarted);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
        e.ProcessPendingCommands();
        Assert.Single(events, x => x.Kind == SimEventKinds.EncounterFirstAction);
        TestData.RunTo(e, 560);
        Assert.True(e.Assessment!.Encounters.State.Records.Single().Closed);
        Assert.Equal("missed", e.Assessment.BuildSummary(e.Tick, final: true).Assessment.Required.Single().State);
    }
}
