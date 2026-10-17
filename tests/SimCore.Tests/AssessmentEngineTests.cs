using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>評分(情境引擎設計第 4.7、4.8 節):硬性失敗、容許帶 2x/below、必要事件 met/missed/late/notApplicable、passed、final、paramKey、黃金摘要。</summary>
public class AssessmentEngineTests
{
    [Fact]
    public void HardFailRulesCpaNeedsActualApproachAndEscortsAreExcluded()
    {
        var e = TestData.MinimalEngine("""
            targets:
              - id: T1
                initial: { position: { x: 0, y: 3000 }, heading: 180, speed: 10 }
              - id: PB
                shipType: pilot
                loa: 18
                beam: 5
                initial: { position: { x: 50, y: 50 }, heading: 0, speed: 8 }
                behaviour: { mode: follow, follow: { leader: own, bearing: 120, range: 60 } }
            assessment:
              minCpa_nm: 0.5
              hardFail:
                - { id: cpaHard, rule: cpa, lessThan: 0.3, excludeEscorts: true }
                - { id: ev, rule: event, kind: acknowledge }
            """, speedKn: 8);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 10);
        Assert.False(e.Assessment!.Rules.HardFailed); // 預測 CPA 低但尚未實際接近
        TestData.RunTo(e, 320); // 18 kn 閉合 1.6 nm(3000 m)→ 約 320 s 時距離 < 0.3 nm
        var hf = events.Single(x => x.Kind == SimEventKinds.HardFail);
        Assert.Equal("cpaHard", hf.ItemId);
        Assert.True(hf.DataNumber("value") < 0.3);
        Assert.True(e.Assessment.Rules.HardFailed);
        Assert.False(e.LatestSummary!.Assessment.Passed);
        Assert.Single(e.LatestSummary.Assessment.HardFails);
        e.Enqueue(SimCommand.Acknowledge("verbal")); e.ProcessPendingCommands();
        TestData.RunTo(e, 322);
        Assert.Equal(2, e.LatestSummary!.Assessment.HardFails.Count);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.HardFail));
        Assert.True(e.BuildState().Scenario!.HardFailed);
    }

    [Fact]
    public void GroundingUkcAndZoneHardFailsTrigger()
    {
        var e = TestData.MinimalEngine("""
            zones:
              - id: box
                type: noGo
                polygon: [ { x: -200, y: 300 }, { x: 200, y: 300 }, { x: 200, y: 900 }, { x: -200, y: 900 } ]
            assessment:
              hardFail:
                - { id: noGo, rule: zoneEntered, zoneId: box }
                - { id: ukc, rule: ukc, lessThan: { metres: 1.0 } }
                - { id: g, rule: grounding }
            """, speedKn: 6, waterDepth: 30);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 200);
        Assert.Contains(events, x => x.Kind == SimEventKinds.HardFail && x.ItemId == "noGo");
        e.Enqueue(SimCommand.SetEnvironment(waterDepth: TestData.Fsb1().GetLoading(LoadingCondition.Ballast).MaxDraft_m + 0.5));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 202);
        Assert.Contains(events, x => x.Kind == SimEventKinds.HardFail && x.ItemId == "ukc");
        e.Enqueue(SimCommand.SetEnvironment(waterDepth: 1.0));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 204);
        Assert.True(e.Aground);
        Assert.Contains(events, x => x.Kind == SimEventKinds.Grounding);
        Assert.Contains(events, x => x.Kind == SimEventKinds.HardFail && x.ItemId == "g");
        Assert.Equal(1, e.LatestSummary!.Ukc.Groundings);
    }

    [Fact]
    public void BandsSupportMultiplierAndBelowAndPenaltiesAccumulateOncePerRule()
    {
        var e = TestData.MinimalEngine("""
            route:
              xtdLimit_m: 20
              waypoints: [ { name: A, x: 0, y: -100 }, { name: B, x: 0, y: 30000 } ]
            assessment:
              bands:
                - { id: xtdMax, metric: track.maxXtd_m, warn: 20, fail: 2x, penalty: 10, failPenalty: 25 }
                - { id: sog, metric: general.maxSog_kn, direction: below, warn: 9, fail: 5, penalty: 3, failPenalty: 7 }
                - { id: dist, metric: general.distance_nm, warn: 0.01, fail: 0.02, penalty: 1, failPenalty: 2 }
              scoring: { start: 100, passMark: 80 }
            """, speedKn: 7);
        var events = TestData.Capture(e);
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 90, currentDriftKn: 2.0));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 100);
        var a = e.LatestSummary!.Assessment;
        var xtd = a.Bands.Single(b => b.Id == "xtdMax");
        Assert.Equal(40.0, xtd.Fail);
        Assert.Equal("fail", xtd.State);
        Assert.Equal(25.0, xtd.Penalty);
        var sog = a.Bands.Single(b => b.Id == "sog");
        Assert.Equal("warn", sog.State); // SOG 約 7.3 kn:< 9 warn、≥ 5 非 fail
        Assert.Equal(3.0, sog.Penalty);
        Assert.Equal("fail", a.Bands.Single(b => b.Id == "dist").State);
        Assert.Equal(100 - 25 - 3 - 2, a.Score);
        Assert.False(a.Passed);
        Assert.Equal(1, events.Count(x => x.Kind == SimEventKinds.BandWarn && x.ItemId == "xtdMax"));
        Assert.Equal(1, events.Count(x => x.Kind == SimEventKinds.BandFail && x.ItemId == "xtdMax"));
        Assert.Equal(70.0, e.BuildState().Scenario!.Score);
    }

    [Fact]
    public void RequiredEventsMetMissedLateAndNotApplicable()
    {
        var e = TestData.MinimalEngine("""
            timeline:
              - id: jam
                when: { type: time, atLeast: 10 }
                actions: [ { injectFault: steeringGear } ]
            assessment:
              required:
                - { id: quick, after: { event: faultInjected, itemId: jam }, expect: telegraphChange, within_s: 20, penalty: 20 }
                - { id: slow, after: { event: faultInjected, itemId: jam }, expect: { acknowledge: { kind: notifyEcr } }, within_s: 30, penalty: 10, latePenaltyPer_s: 0.5 }
                - { id: never, after: { event: faultInjected, itemId: jam }, expect: thrusterOrder, within_s: 15, penalty: 5 }
                - { id: na, after: { event: grounding }, expect: rudderOrder, within_s: 60, penalty: 50 }
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 20);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); e.ProcessPendingCommands(); // dt 10 → met
        TestData.RunTo(e, 50);
        e.Enqueue(SimCommand.Acknowledge("notifyEcr")); e.ProcessPendingCommands();        // dt 40 → late(逾 10 s → 5 分)
        TestData.RunTo(e, 60);
        var r = e.LatestSummary!.Assessment.Required;
        Assert.Equal("met", r.Single(x => x.Id == "quick").State);
        Assert.Equal(10.0, r.Single(x => x.Id == "quick").DtS!.Value, 9);
        Assert.Equal(0.0, r.Single(x => x.Id == "quick").Penalty);
        Assert.Equal("late", r.Single(x => x.Id == "slow").State);
        Assert.Equal(5.0, r.Single(x => x.Id == "slow").Penalty, 9);
        Assert.Equal("missed", r.Single(x => x.Id == "never").State);
        Assert.Equal(5.0, r.Single(x => x.Id == "never").Penalty);
        Assert.Equal("notApplicable", r.Single(x => x.Id == "na").State);
        Assert.Equal(90.0, e.LatestSummary.Assessment.Score);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.RequiredEventMissed));
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.RequiredEventMet));
        Assert.True(events.Single(x => x.Kind == SimEventKinds.RequiredEventMet && x.ItemId == "slow").DataBool("late"));
    }

    [Fact]
    public void FinalSummaryFreezesAndCarriesParameterProvenance()
    {
        var e = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        TestData.RunTo(e, 30);
        var s = e.LatestSummary!;
        Assert.False(s.Final);
        Assert.True(s.Advisory);
        Assert.Equal("simosa-company-procedures", s.ParameterSet!.Id);
        var band = s.Assessment.Bands.Single(b => b.Id == "xtdMax");
        Assert.Equal("navigation.xtd.channel_m", band.ParamKey);
        Assert.Equal("SMS-NAV-012", band.Source);
        Assert.Equal("2", band.Revision);
        Assert.Equal("4.1", band.Clause);
        Assert.Equal(50.0, band.Warn);
        Assert.Equal(100.0, band.Fail);
        var req = s.Assessment.Required.Single(r => r.Id == "steeringFirstAction");
        Assert.Equal("emergency.steeringFailure.firstAction_s", req.ParamKey);
        Assert.Equal("SMS-EMG-004", req.Source);
        Assert.Contains(s.Assessment.Parameters, p => p.Key == "navigation.ukc.channel_fraction" && p.Unit == "fractionOfDraft");
        Assert.Equal("navigation.ukc.minimum_m", s.Ukc.LimitSource);
        Assert.Equal(70.0, s.Assessment.PassMark);
        e.Enqueue(SimCommand.EndExercise("stop")); e.ProcessPendingCommands();
        var final1 = e.LatestSummary!;
        Assert.True(final1.Final);
        e.Enqueue(SimCommand.Acknowledge("notifyEcr")); e.ProcessPendingCommands();
        Assert.Same(final1, e.LatestSummary);
    }

    /// <summary>黃金案例:E07 600 s 合成腳本(固定舵令/車令/確認序列)→ summary JSON 與 Golden/assessment-E07-600s.json 相等(數值公差 1e-9)。更新黃金檔需說明。</summary>
    [Fact]
    public void GoldenE07SummaryMatches()
    {
        var e = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        EngineDeterminismTests.RunScript(e, 50 * 600, TimelineRecordReplayTests.Script);
        var summary = e.Assessment!.BuildSummary(e.Tick, final: true);
        var json = JsonSerializer.Serialize(summary, ContractJson.Indented);
        var goldenPath = Path.Combine(TestData.Root, "tests", "SimCore.Tests", "Golden", "assessment-E07-600s.json");
        if (System.Environment.GetEnvironmentVariable("SIMOSA_UPDATE_GOLDEN") == "1") File.WriteAllText(goldenPath, json + "\n");
        Assert.True(File.Exists(goldenPath), $"缺少黃金檔 {goldenPath}(以 SIMOSA_UPDATE_GOLDEN=1 產生)");
        using var expected = JsonDocument.Parse(File.ReadAllText(goldenPath));
        using var actual = JsonDocument.Parse(json);
        AssertJsonEqual(expected.RootElement, actual.RootElement, "$");
    }

    private static void AssertJsonEqual(JsonElement a, JsonElement b, string path)
    {
        Assert.True(a.ValueKind == b.ValueKind, $"{path}:型別 {a.ValueKind} vs {b.ValueKind}");
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var ak = a.EnumerateObject().Select(p => p.Name).ToList();
                var bk = b.EnumerateObject().Select(p => p.Name).ToList();
                Assert.True(ak.SequenceEqual(bk), $"{path}:鍵不同 [{string.Join(",", ak)}] vs [{string.Join(",", bk)}]");
                foreach (var p in a.EnumerateObject()) AssertJsonEqual(p.Value, b.GetProperty(p.Name), $"{path}.{p.Name}");
                break;
            case JsonValueKind.Array:
                Assert.True(a.GetArrayLength() == b.GetArrayLength(), $"{path}:長度 {a.GetArrayLength()} vs {b.GetArrayLength()}");
                var i = 0;
                foreach (var (x, y) in a.EnumerateArray().Zip(b.EnumerateArray())) AssertJsonEqual(x, y, $"{path}[{i++}]");
                break;
            case JsonValueKind.Number:
                var da = a.GetDouble();
                var db = b.GetDouble();
                Assert.True(Math.Abs(da - db) <= 1e-9 * Math.Max(1.0, Math.Abs(da)), $"{path}:{da} vs {db}");
                break;
            default:
                Assert.True(a.GetRawText() == b.GetRawText(), $"{path}:{a.GetRawText()} vs {b.GetRawText()}");
                break;
        }
    }
}
