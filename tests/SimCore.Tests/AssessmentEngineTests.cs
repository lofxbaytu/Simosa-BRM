using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
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
    public void ActionAfterDeadlineButBeforeNextWholeSecondIsLateNotMet()
    {
        // 審查發現:after 事件落在非整秒 tick(教官 injectFault)時,期限與下一整秒評估之間送達的動作被判 met(late=false),
        // 與 FaultResponseTracker 的 withinFirstAction = dt ≤ within_s 矛盾;修正後以 dt > within_s 判 late,並先補發 requiredEventMissed
        var e = TestData.MinimalEngine("""
            assessment:
              required:
                - id: first
                  after: { event: faultInjected }
                  expect: { any: [ telegraphChange, steeringModeChange ] }
                  within_s: 60
                  penalty: 20
                - id: notify
                  after: { event: faultInjected }
                  expect: { acknowledge: { kind: notifyEcr } }
                  within_s: 60
                  penalty: 10
                  latePenaltyPer_s: 0.5
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 100.5);
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear)); e.ProcessPendingCommands();
        Assert.Equal(160.5, events.Single(x => x.Kind == SimEventKinds.RequiredEventPending && x.ItemId == "first").DataNumber("deadline_t"));
        TestData.RunTo(e, 160.8);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.RequiredEventMissed); // 整秒評估(160.0)尚未逾時
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Emergency)); e.ProcessPendingCommands(); // dt 60.3 > 60
        TestData.RunTo(e, 162.5);
        e.Enqueue(SimCommand.Acknowledge("notifyEcr")); e.ProcessPendingCommands();              // dt 62 → late 1 分
        TestData.RunTo(e, 170);

        var missedFirst = events.Single(x => x.Kind == SimEventKinds.RequiredEventMissed && x.ItemId == "first");
        var metFirst = events.Single(x => x.Kind == SimEventKinds.RequiredEventMet && x.ItemId == "first");
        Assert.True(missedFirst.Seq < metFirst.Seq); // 先 missed 再 met(late),與整秒路徑的事件序列相同
        Assert.Equal(metFirst.Tick, missedFirst.Tick);
        Assert.True(metFirst.DataBool("late"));
        Assert.Equal(60.3, metFirst.DataNumber("dt_s")!.Value, 9);
        Assert.Equal(20.0, metFirst.DataNumber("penalty"));
        var r = e.LatestSummary!.Assessment.Required;
        var first = r.Single(x => x.Id == "first");
        Assert.Equal("late", first.State);
        Assert.Equal(20.0, first.Penalty);
        Assert.Equal(ActionKinds.SteeringModeChange, first.By);
        var fault = e.Assessment!.Faults.State.Records.Single();
        Assert.False(fault.WithinFirstAction); // 與 faultResponses 一致
        Assert.Equal(60.3, fault.FirstAction!.DtS, 9);

        // notify:整秒評估 161 已判 missed(既有路徑),之後確認 → late,逾 2 s × 0.5 = 1 分
        var notify = r.Single(x => x.Id == "notify");
        Assert.Equal("late", notify.State);
        Assert.Equal(1.0, notify.Penalty, 9);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.RequiredEventMissed));
        Assert.Equal(100 - 20 - 1, e.LatestSummary.Assessment.Score, 9);

        // 剛好在期限送達(dt = within_s)仍為 met
        var g = TestData.MinimalEngine("""
            assessment:
              required:
                - { id: first, after: { event: faultInjected }, expect: telegraphChange, within_s: 60, penalty: 20 }
            """);
        var gEvents = TestData.Capture(g);
        TestData.RunTo(g, 100.5);
        g.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear)); g.ProcessPendingCommands();
        TestData.RunTo(g, 160.5);
        g.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); g.ProcessPendingCommands();
        TestData.RunTo(g, 165);
        Assert.Equal("met", g.LatestSummary!.Assessment.Required.Single().State);
        Assert.False(gEvents.Single(x => x.Kind == SimEventKinds.RequiredEventMet).DataBool("late"));
        Assert.DoesNotContain(gEvents, x => x.Kind == SimEventKinds.RequiredEventMissed);
        Assert.True(g.Assessment!.Faults.State.Records.Single().WithinFirstAction);
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

    /// <summary>
    /// 黃金案例:E07 600 s 合成腳本(固定舵令/車令/確認序列)→ summary JSON 與 Golden/assessment-E07-600s.json 相等(數值公差 1e-9)。更新黃金檔需說明。
    /// <para>
    /// 比對時忽略 <c>$.parameterSet.sha256</c>:它是整個參數集檔案的雜湊,其他情境(E12–E14 等)新增參數鍵就會改變,
    /// 但 E07 的評分只取決於它實際引用的參數。E07 引用的參數仍逐一比對 —— <c>assessment.parameters[]</c>(解析清單:鍵、值、單位、來源、版次、條款)、
    /// bands/required 的 paramKey/source/revision 與門檻值,以及 <c>parameterSet.id/version</c>(版次改變代表訓練負責人核定了新版,黃金檔須重新確認)。
    /// 因此新增未被 E07 引用的鍵不會讓本測試失敗,改動 E07 引用的任何數值、單位或來源則會失敗。
    /// </para>
    /// </summary>
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
        // 參數集身分:id 與 version 仍須相同(sha256 見上方說明;以目前檔案確認 sha256 確實為參數集檔案雜湊)
        Assert.Equal(expected.RootElement.GetProperty("parameterSet").GetProperty("id").GetString(), summary.ParameterSet!.Id);
        Assert.Equal(expected.RootElement.GetProperty("parameterSet").GetProperty("version").GetString(), summary.ParameterSet.Version);
        Assert.Equal(64, summary.ParameterSet.Sha256.Length);
    }

    /// <summary>黃金比對忽略的路徑(理由見 <see cref="GoldenE07SummaryMatches"/>)。</summary>
    private static readonly HashSet<string> GoldenIgnoredPaths = new(StringComparer.Ordinal) { "$.parameterSet.sha256" };

    private static void AssertJsonEqual(JsonElement a, JsonElement b, string path)
    {
        if (GoldenIgnoredPaths.Contains(path)) return;
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
