using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 載入時的靜態檢查(規劃書第 9.1 節「自動檢查」、情境引擎設計第 2.4 節):結構錯誤(同 <see cref="Scenario.StructuralIssues"/>)、
/// 初始狀態已成立的硬性失敗(error)、初始即成立的時間表條件/過晚的 time 觸發/無 cooldown 的 always/缺音檔/
/// 只記錄的故障(injectFault 物件形式 recordOnly: true 明示預期者不警告)、
/// 航段短於到達半徑、CPA 門檻大於航道走廊寬度(warning)。
/// 主控程序啟動與 loadScenario 時印出;<c>--validate</c> 只檢查不執行。
/// </summary>
public static class ScenarioValidator
{
    /// <summary>引擎會改變行為的故障名稱;其餘只記錄(情境引擎設計第 1.1.4 節)。</summary>
    public static readonly string[] ModelledFaults = Engine.FaultNames.Modelled;

    /// <summary>故障目錄(與 src/Contracts/fault-catalogue.json、教官站 DEFAULT_FAULTS 一致)。</summary>
    public static readonly string[] FaultCatalogue =
    {
        "steeringGear", "steeringFuOnly", "steeringPumpFailure", "mainEngine", "engineStartFailure", "bowThruster",
        "gyroDrift", "gyroFreeze", "gyroFailure", "autopilotFailure", "gpsJump", "gpsOffset", "gpsFailure",
        "radarFailure", "radarDegraded", "radarFailureS", "radarDegradedS", "aisFailure", "ecdisFreeze", "ecdisAlarmFlood", "echoSounderFailure", "logFailure",
        "vhfFailure", "ecrPhoneFailure", "blackout", "fireAlarmEngineRoom", "cargoHeatingFailure", "tugLineBreak",
    };

    public const double DefaultDurationS = 7200.0;

    /// <summary><paramref name="ship"/> 供 UKC 門檻換算;<paramref name="scenarioDirectory"/> 供音檔/附件存在檢查(情境包目錄 data/scenarios/&lt;id&gt;/)。</summary>
    public static IReadOnlyList<ScenarioIssue> Check(Scenario scenario, ShipParticulars? ship = null, string? scenarioDirectory = null)
    {
        var issues = scenario.StructuralIssues().ToList();
        void Error(string path, string msg) => issues.Add(new ScenarioIssue(ScenarioIssueSeverity.Error, path, msg));
        void Warn(string path, string msg) => issues.Add(new ScenarioIssue(ScenarioIssueSeverity.Warning, path, msg));
        if (issues.Any(i => i.Severity == ScenarioIssueSeverity.Error)) return issues;
        // expect 的 includePrior 只對指定 targetId 的 encounterFirstAction 有效(AssessmentEngine.PriorFirstAction);其餘位置不作用
        for (var i = 0; i < scenario.Assessment.Required.Count; i++)
            foreach (var leaf in scenario.Assessment.Required[i].Expect.Leaves())
                if (leaf.Event is { IncludePrior: true } ev && (ev.Kind != Engine.SimEventKinds.EncounterFirstAction || ev.TargetId is null))
                    Warn($"assessment.required[{i}].expect", $"includePrior 只對指定 targetId 的 encounterFirstAction 有效,此處({ev.Kind}{(ev.TargetId is null ? ",未指定 targetId" : "")})不作用");
        if (scenario.Origin is null || scenario.Initial.Position is null) return issues;

        var proj = new LocalTangentPlane(scenario.Origin.Lat, scenario.Origin.Lon);
        var (x0, y0) = Scenario.Resolve(proj, scenario.Initial.Position);
        var maxDraft = ship?.GetLoading(scenario.Ship.Loading).MaxDraft_m;
        var depth0 = scenario.Environment.WaterDepth;
        foreach (var z in scenario.Zones)
        {
            var poly = z.Polygon.Select(p => Scenario.Resolve(proj, p)).ToArray();
            var inside = Polygon2D.Contains(poly, x0, y0);
            if (inside && z.WaterDepthM is { } zd) depth0 = Math.Min(depth0, zd);
            if (inside && z.Type == ZoneType.NoGo && scenario.Assessment.HardFail.Any(r => r.Rule == "zoneEntered" && r.ZoneId == z.Id))
                Error($"zones[{scenario.Zones.IndexOf(z)}]", $"初始位置已在禁區 {z.Id} 內(硬性失敗在初始狀態已成立)");
        }
        // 潮汐:初始有效水深 = 基底(含區域覆寫)+ 潮高(t = 0)(與 SimulationEngine.Initialize 相同);utc 表點而無 startTimeUtc 時結構檢查已回報,此處略過潮高
        if (scenario.Environment.Tide is { } tideSpec)
        {
            var hasStart = Engine.SimulationEngine.TryParseUtc(scenario.StartTimeUtc, out var startUtc);
            try
            {
                if (Environment.TideModel.FromScenario(tideSpec, hasStart ? startUtc : null) is { } tide) depth0 += tide.HeightAt(0.0);
            }
            catch (InvalidDataException) { }
        }
        if (maxDraft is { } draft)
        {
            var ukc0 = depth0 - draft;
            if (ukc0 <= 0) Error("initial.position", $"初始 UKC {ukc0:F2} m ≤ 0(初始即擱淺)");
            foreach (var r in scenario.Assessment.HardFail.Where(r => r.Rule == "ukc"))
            {
                var limit = r.LessThanUkc?.ToMetres(draft) ?? 0;
                if (ukc0 < limit) Error($"assessment.hardFail[{scenario.Assessment.HardFail.IndexOf(r)}]", $"初始 UKC {ukc0:F2} m 已低於硬性門檻 {limit:F2} m");
            }
        }

        if (scenario.Route is { } route)
        {
            // 航段長度 ≤ 到達半徑:RouteTracker 以 s ≥ 0 且 |B−P| ≤ 半徑判定通過,過短的航段會在剛進入時就被視為通過
            var loa = ship is null ? 0.0 : ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : ship.Hull.LengthBetweenPerpendiculars_m;
            var defaultRadius = Math.Max(2.0 * loa, 150.0);
            var wps = route.Waypoints.Select(w => Scenario.Resolve(proj, w)).ToArray();
            for (var i = 0; i + 1 < wps.Length; i++)
            {
                var len = Math.Sqrt((wps[i + 1].X - wps[i].X) * (wps[i + 1].X - wps[i].X) + (wps[i + 1].Y - wps[i].Y) * (wps[i + 1].Y - wps[i].Y));
                var radius = route.Waypoints[i + 1].ArrivalRadiusM ?? defaultRadius;
                if (len <= radius) Warn($"route.waypoints[{i + 1}]", $"航段 {route.Waypoints[i].Name}→{route.Waypoints[i + 1].Name} 長 {len:F0} m 不大於到達半徑 {radius:F0} m,進入航段即視為通過");
            }
            // 走廊半寬 w 的航道內,正常交會的通過距離約 ≤ 2w:CPA 容許帶/硬性失敗門檻大於 2w 時任何航道內交會都會判定失敗
            var halfWidths = route.Waypoints.Select(w => w.XtdLimitM).Append(route.XtdLimitM).Where(x => x is > 0).Select(x => x!.Value).ToList();
            if (halfWidths.Count > 0)
            {
                var maxPassNm = 2.0 * halfWidths.Max() / Units.MetresPerNauticalMile;
                foreach (var b in scenario.Assessment.Bands.Where(b => b.Metric == "collisionAvoidance.minCpa_nm"))
                {
                    var threshold = Math.Max(b.Warn ?? 0, b.FailValue ?? 0);
                    if (threshold > maxPassNm)
                        Warn($"assessment.bands[{scenario.Assessment.Bands.IndexOf(b)}]", $"minCpa 容許帶門檻 {threshold:F2} nm 大於走廊寬度 {maxPassNm:F2} nm(2 × 半寬),航道內任何交會都會超出容許帶");
                }
                foreach (var r in scenario.Assessment.HardFail.Where(r => r.Rule == "cpa"))
                {
                    if ((r.LessThanNumber ?? 0) > maxPassNm)
                        Warn($"assessment.hardFail[{scenario.Assessment.HardFail.IndexOf(r)}]", $"硬性失敗 cpa 門檻 {r.LessThanNumber:F2} nm 大於走廊寬度 {maxPassNm:F2} nm(2 × 半寬)");
                }
            }
        }

        var duration = scenario.Briefing?.DurationS ?? DefaultDurationS;
        for (var i = 0; i < scenario.Timeline.Count; i++)
        {
            var item = scenario.Timeline[i];
            var path = $"timeline[{i}]";
            if (item.Repeat.Mode == RepeatMode.Always && item.CooldownS <= 0) Warn(path, "repeat: always 且無 cooldown_s,條件持續成立時每秒觸發");
            if (item.When is { } when && !item.IsManual)
            {
                foreach (var leaf in when.Leaves())
                {
                    if (leaf.Type == "time" && leaf.Since is null && leaf.AtLeast is { } at && at > duration)
                        Warn($"{path}.when", $"time 觸發 {at:F0} s 晚於情境預估長度 {duration:F0} s");
                }
                if (when.Leaves().All(l => l.Type != "time") && InitiallyTrue(when, scenario, proj, x0, y0))
                    Warn($"{path}.when", "條件在初始狀態(t = 0)已成立,將於第一個評估步觸發");
            }
            foreach (var a in item.Actions)
            {
                if (a.Kind == "injectFault" && a.AsString() is { } fault)
                {
                    if (!FaultCatalogue.Contains(fault)) Warn(path, $"故障 {fault} 不在故障目錄中");
                    // recordOnly: true = 作者明示「只記錄,由儀器/腳本反應」,不警告;標在核心有模型的故障上則警告(標記與行為不符)
                    else if (!ModelledFaults.Contains(fault) && !a.RecordOnly) Warn(path, $"故障 {fault} 只記錄與廣播,儀器端需自行反應(預期如此時加 recordOnly: true)");
                    else if (ModelledFaults.Contains(fault) && a.RecordOnly) Warn(path, $"故障 {fault} 由核心模擬(會改變行為),recordOnly: true 不適用");
                }
                if (a.Kind == "voiceScript" && a.AsVoiceScript()?.File is { } file)
                {
                    var dir = scenarioDirectory ?? (RepositoryPaths.TryFindRoot() is { } root ? Path.Combine(root, "data", "scenarios", scenario.Id) : null);
                    if (dir is null || !File.Exists(Path.Combine(dir, file))) Warn(path, $"語音檔 {file} 不存在於情境包目錄,教官站將退回 TTS");
                }
            }
        }
        return issues;
    }

    /// <summary>以初始狀態判斷條件是否已成立(只評估可由情境靜態推得的 leaf:ownDistance/ownHeading/ownSpeed/zone/phase/telegraph/autopilot/fault/routeLeg)。</summary>
    private static bool InitiallyTrue(TriggerCondition c, Scenario sc, LocalTangentPlane proj, double x0, double y0)
    {
        if (c.All is not null) return c.All.Count > 0 && c.All.All(k => InitiallyTrue(k, sc, proj, x0, y0));
        if (c.Any is not null) return c.Any.Any(k => InitiallyTrue(k, sc, proj, x0, y0));
        if (c.Not is not null) return !InitiallyTrue(c.Not, sc, proj, x0, y0);
        switch (c.Type)
        {
            case "ownDistance":
            {
                double px, py;
                if (c.Point is not null) (px, py) = Scenario.Resolve(proj, c.Point);
                else if (c.Waypoint is { } wn && sc.Route?.Waypoints.FirstOrDefault(w => w.Name == wn) is { } wp) (px, py) = Scenario.Resolve(proj, wp);
                else if (c.Target is { } tid && sc.Targets.FirstOrDefault(t => string.Equals(t.Id, tid, StringComparison.OrdinalIgnoreCase))?.Initial.Position is { } tp) (px, py) = Scenario.Resolve(proj, tp);
                else return false;
                var d = Math.Sqrt((x0 - px) * (x0 - px) + (y0 - py) * (y0 - py)) / Units.MetresPerNauticalMile;
                return (c.LessThan is not { } lt || d < lt) && (c.GreaterThan is not { } gt || d > gt);
            }
            case "ownHeading":
            {
                var h = Units.NormalizeHeadingDeg(sc.Initial.Heading);
                if (c.GreaterThan is { } from && c.LessThan is { } to) return Units.NormalizeHeadingDeg(h - from) <= Units.NormalizeHeadingDeg(to - from);
                if (c.GreaterThan is { } g) return h > g;
                if (c.LessThan is { } l) return h < l;
                return true;
            }
            case "ownSpeed":
                return (c.LessThan is not { } slt || sc.Initial.Speed < slt) && (c.GreaterThan is not { } sgt || sc.Initial.Speed > sgt);
            case "zone":
            {
                var z = sc.Zones.FirstOrDefault(z => z.Id == c.Id);
                if (z is null) return false;
                var inside = Polygon2D.Contains(z.Polygon.Select(p => Scenario.Resolve(proj, p)).ToArray(), x0, y0);
                return inside == (c.Inside ?? true);
            }
            case "phase": return string.Equals(c.EqualsString, "normal", StringComparison.Ordinal);
            case "telegraph":
            {
                var tg = sc.Initial.Telegraph ?? "";
                if (c.In is { } inList) return inList.Any(t => string.Equals(t, tg, StringComparison.OrdinalIgnoreCase));
                if (c.NotIn is { } notIn) return !notIn.Any(t => string.Equals(t, tg, StringComparison.OrdinalIgnoreCase));
                return false;
            }
            case "autopilot": return c.Enabled == false;
            case "fault": return c.Active == false;
            case "routeLeg": return c.AtLeast is { } al ? (sc.Route?.StartLeg ?? 0) >= al : c.EqualsNumber is { } eq && (sc.Route?.StartLeg ?? 0) == (int)eq;
            default: return false;
        }
    }
}
