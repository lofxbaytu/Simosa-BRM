using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>載入時靜態檢查(情境引擎設計第 2.4 節):每條 error/warning 一案例;E01、E05、E07 零 error。</summary>
public class ScenarioValidatorTests
{
    private static IReadOnlyList<ScenarioIssue> Check(string extra, string loading = "ballast", double waterDepth = 30)
    {
        var sc = ScenarioLoader.FromYaml($$"""
            id: T
            ship: { id: FSB1, loading: {{loading}} }
            origin: { lat: 23.80, lon: 120.05 }
            initial: { position: { x: 0, y: 0 }, heading: 0, speed: 5, telegraph: HAH }
            environment: { waterDepth: {{waterDepth}} }
            {{extra}}
            """);
        return ScenarioValidator.Check(sc, TestData.Fsb1());
    }

    private static void HasError(string extra, string fragment, string loading = "ballast", double waterDepth = 30)
    {
        var issues = Check(extra, loading, waterDepth);
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Message.Contains(fragment));
    }

    private static void HasWarning(string extra, string fragment)
    {
        var issues = Check(extra);
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Warning && i.Message.Contains(fragment));
    }

    [Fact]
    public void StructuralErrors()
    {
        HasError("timeline:\n  - { id: a, when: { type: time, atLeast: 1 }, actions: [ { phase: x } ] }\n  - { id: a, when: { type: time, atLeast: 1 }, actions: [ { phase: y } ] }", "重複");
        HasError("timeline:\n  - { id: a, when: { type: zone, id: nope }, actions: [ { phase: x } ] }", "區域");
        HasError("route:\n  waypoints: [ { name: A, x: 0, y: 0 }, { name: B, x: 0, y: 1000 } ]\ntimeline:\n  - { id: a, when: { type: ownDistance, waypoint: Z, lessThan: 1 }, actions: [ { phase: x } ] }", "航點");
        HasError("timeline:\n  - { id: a, when: { type: ownDistance, target: Q, lessThan: 1 }, actions: [ { phase: x } ] }", "目標");
        HasError("timeline:\n  - { id: a, when: { type: time, atLeast: 1 }, actions: [ { snapshot: true } ] }", "不允許");
        HasError("route:\n  waypoints: [ { name: A, x: 0, y: 0 } ]", "至少");
        HasError("route:\n  waypoints: [ { name: A, x: 0, y: 0 }, { name: B, x: 0, y: 0.5 } ]", "重合");
        HasError("zones:\n  - { id: z, type: noGo, polygon: [ { x: 0, y: 0 }, { x: 1, y: 1 } ] }", "3 點");
        HasError("zones:\n  - { id: z, type: noGo, polygon: [ { x: 0, y: 1000 }, { x: 1000, y: 2000 }, { x: 1000, y: 1000 }, { x: 0, y: 2000 } ] }", "自交");
        HasError("assessment:\n  bands: [ { id: b, metric: track.nope, warn: 1 } ]", "指標");
        HasError("assessment:\n  required: [ { id: r, after: { event: faultInjected, itemId: nope }, expect: rudderOrder, within_s: 10 } ]", "時間表項目");
        HasError("assessment:\n  hardFail: [ { id: h, rule: zoneEntered, zoneId: nope } ]", "區域");
    }

    /// <summary>與 Python simosa_brm.scenario.structural_issues 對齊(原本只有 Python 檢查):required[].expect 必填、ukc 規則 lessThan 須含 metres|fractionOfDraft。</summary>
    [Fact]
    public void RequiredExpectAndUkcUnitAlignedWithPython()
    {
        HasError("assessment:\n  required: [ { id: r, after: { event: faultInjected }, within_s: 10 } ]", "必要事件需要 expect");
        HasError("assessment:\n  hardFail: [ { id: u, rule: ukc, lessThan: 2 } ]", "ukc 規則需要 lessThan {metres|fractionOfDraft}");
        HasError("assessment:\n  hardFail: [ { id: u, rule: ukc, lessThan: { feet: 2 } } ]", "ukc 規則需要 lessThan {metres|fractionOfDraft}");
        HasError("assessment:\n  hardFail: [ { id: u, rule: ukc } ]", "ukc 規則需要 lessThan {metres|fractionOfDraft}");
        // 正確寫法不報錯(水深 30 m,初始 UKC 充足)
        foreach (var ok in new[] {
            "assessment:\n  required: [ { id: r, after: { event: faultInjected }, expect: rudderOrder, within_s: 10 } ]",
            "assessment:\n  required: [ { id: r, after: { event: faultInjected }, expect: { any: [ rpmOrder, { acknowledge: { kind: notifyEcr } } ] }, within_s: 10 } ]",
            "assessment:\n  hardFail: [ { id: u, rule: ukc, lessThan: { metres: 1 } } ]",
            "assessment:\n  hardFail: [ { id: u, rule: ukc, lessThan: { fractionOfDraft: 0.1 } } ]",
        })
            Assert.DoesNotContain(Check(ok), i => i.Severity == ScenarioIssueSeverity.Error);
    }

    [Fact]
    public void AlertEventItemIdIsAlertIdNotTimelineItem()
    {
        // 警報/BNWAS 事件的 itemId 是警報 id(E08 alarmAck 限定 gyroFailure),不比對時間表項目
        foreach (var ok in new[] {
            "assessment:\n  required: [ { id: r, after: { event: alertRaised, itemId: gyroFailure }, expect: { event: { kind: alertAcknowledged, itemId: gyroFailure } }, within_s: 30 } ]",
            "assessment:\n  required: [ { id: r, after: { event: bnwasStage, itemId: bnwas }, expect: rudderOrder, within_s: 30 } ]",
        })
            Assert.DoesNotContain(Check(ok), i => i.Severity == ScenarioIssueSeverity.Error);
        // 時間表類事件仍檢查
        HasError("assessment:\n  required: [ { id: r, after: { event: faultInjected, itemId: nope }, expect: rudderOrder, within_s: 10 } ]", "after 引用不存在的時間表項目");
        HasError("assessment:\n  required: [ { id: r, after: { event: faultInjected }, expect: { event: { kind: timelineTriggered, itemId: nope } }, within_s: 10 } ]", "expect 引用不存在的時間表項目");
    }

    [Fact]
    public void InitialStateHardFailsAreErrors()
    {
        HasError("zones:\n  - { id: z, type: noGo, polygon: [ { x: -10, y: -10 }, { x: 10, y: -10 }, { x: 10, y: 10 }, { x: -10, y: 10 } ] }\nassessment:\n  hardFail: [ { id: n, rule: zoneEntered, zoneId: z } ]", "禁區");
        HasError("assessment:\n  hardFail: [ { id: u, rule: ukc, lessThan: { metres: 2.0 } } ]", "UKC", waterDepth: TestData.Fsb1().GetLoading(Contracts.LoadingCondition.Ballast).MaxDraft_m + 1.0);
    }

    [Fact]
    public void Warnings()
    {
        HasWarning("timeline:\n  - { id: a, when: { type: ownSpeed, lessThan: 10 }, actions: [ { phase: x } ] }", "初始狀態");
        HasWarning("timeline:\n  - { id: a, when: { type: time, atLeast: 99999 }, actions: [ { phase: x } ] }", "晚於");
        HasWarning("timeline:\n  - { id: a, repeat: always, when: { type: time, atLeast: 10 }, actions: [ { phase: x } ] }", "cooldown");
        HasWarning("timeline:\n  - { id: a, when: { type: time, atLeast: 10 }, actions: [ { voiceScript: { role: ecr, channel: phone, text: hi, file: missing.ogg } } ] }", "語音檔");
        HasWarning("timeline:\n  - { id: a, when: { type: time, atLeast: 10 }, actions: [ { injectFault: radarFailure } ] }", "只記錄"); // gyroDrift 已 modelled(感測器層),改以仍只記錄的 radarFailure 檢查
        HasWarning("timeline:\n  - { id: a, when: { type: time, atLeast: 10 }, actions: [ { injectFault: unknownThing } ] }", "故障目錄");
    }

    [Fact]
    public void RepositoryScenariosHaveNoErrors()
    {
        foreach (var name in new[] { "E01_baseline", "E05_pilot_traffic", "E07_steering_failure" })
        {
            var sc = ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", name + ".yaml"));
            var issues = ScenarioValidator.Check(sc, TestData.Ship(sc.Ship.Id));
            Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
        }
    }


    [Fact]
    public void WarnsOnLegShorterThanArrivalRadiusAndCpaThresholdWiderThanCorridor()
    {
        HasWarning("""
            route:
              xtdLimit_m: 50
              waypoints:
                - { name: A, x: 0, y: 100 }
                - { name: B, x: 0, y: 250 }
                - { name: C, x: 0, y: 5000 }
            """, "不大於到達半徑");
        HasWarning("""
            route:
              xtdLimit_m: 50
              waypoints:
                - { name: A, x: 0, y: 100 }
                - { name: B, x: 0, y: 5000 }
            assessment:
              bands:
                - { id: minCpa, metric: collisionAvoidance.minCpa_nm, direction: below, warn: 0.5, fail: 0.2, penalty: 10 }
            """, "大於走廊寬度");
        HasWarning("""
            route:
              waypoints:
                - { name: A, x: 0, y: 100, xtdLimit_m: 40 }
                - { name: B, x: 0, y: 5000, xtdLimit_m: 40 }
            assessment:
              hardFail:
                - { id: cpa, rule: cpa, lessThan: 0.2 }
            """, "大於走廊寬度");
        var ok = Check("""
            route:
              xtdLimit_m: 50
              waypoints:
                - { name: A, x: 0, y: 100 }
                - { name: B, x: 0, y: 5000 }
            assessment:
              bands:
                - { id: minCpa, metric: collisionAvoidance.minCpa_nm, direction: below, warn: 0.05, fail: 0.025, penalty: 10 }
            """);
        Assert.DoesNotContain(ok, i => i.Message.Contains("大於走廊寬度") || i.Message.Contains("到達半徑"));
        // E07(航道門檻 navigation.cpa.channel_nm)不再有 CPA 門檻警告
        var e07 = ScenarioValidator.Check(TestData.E07(), TestData.Fsb1());
        Assert.DoesNotContain(e07, i => i.Message.Contains("大於走廊寬度") || i.Message.Contains("到達半徑"));
    }
}
