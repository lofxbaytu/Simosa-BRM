using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Parameters;
using AssessmentNs = SimosaBRM.SimCore.Assessment;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 情境(data/scenarios/*.yaml;JSON Schema 見 src/Contracts/scenario.schema.json,與 Python 側共用)。
/// 最小內容:自船 ID 與裝載、初始位置/航向/速度、環境、亂數種子。
/// 屬性同時供 YamlDotNet(camelCase 命名)與 System.Text.Json(紀錄檔標頭)序列化。
/// </summary>
public sealed class Scenario
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>亂數種子(所有亂數由 Random(seed) 產生,CLAUDE.md 確定性要求)</summary>
    public int Seed { get; set; } = 1;
    /// <summary>練習開始的 UTC 時刻(ISO 8601),供 NMEA 時間欄位;未指定時主控程序以實際時鐘代入</summary>
    public string? StartTimeUtc { get; set; }
    /// <summary>初始時間倍率(1 = 即時)</summary>
    public double TimeScale { get; set; } = 1.0;
    public ScenarioShip Ship { get; set; } = new();
    /// <summary>本地 ENU 原點;未指定時以初始位置的經緯度為原點</summary>
    public GeoPoint? Origin { get; set; }
    public ScenarioInitial Initial { get; set; } = new();
    public ScenarioEnvironment Environment { get; set; } = new();
    /// <summary>目標船(交通;規劃書第 9.1 節);Python 參考實作無目標船,忽略此欄位</summary>
    public List<ScenarioTarget> Targets { get; set; } = new();
    /// <summary>評估參數與規則(最小 CPA 門檻、硬性失敗、容許帶、必要事件、評分;規劃書第 8.3 節)</summary>
    public ScenarioAssessment Assessment { get; set; } = new();
    /// <summary>事件時間表(情境引擎設計第 1.1 節;Python 參考實作忽略)</summary>
    public List<TimelineItem> Timeline { get; set; } = new();
    /// <summary>航線(航點、走廊、速限、檢查點;第 1.2 節)</summary>
    public ScenarioRoute? Route { get; set; }
    /// <summary>區域(禁區、速限、UKC 政策、報告;第 1.2 節)</summary>
    public List<ScenarioZone> Zones { get; set; } = new();
    /// <summary>簡報(字串或物件;第 1.4 節)</summary>
    public ScenarioBriefing? Briefing { get; set; }
    /// <summary>感測器常態參數(磁羅經誤差;設計文件 docs/design/sensors-alerts.md;Python 參考實作忽略)</summary>
    public ScenarioSensors Sensors { get; set; } = new();
    /// <summary>警報管理(BAM,MSC.302(87))設定:升級/靜音時間、偏航/航向監視/淺水門檻(未設定即關閉)</summary>
    public ScenarioAlerts Alerts { get; set; } = new();
    /// <summary>BNWAS(MSC.128(75))設定:模式、休眠期、第二/三階段延遲(預設 off)</summary>
    public ScenarioBnwas Bnwas { get; set; } = new();
    /// <summary>拖船(規劃書第 6.2 節 T 項;設計文件 physics-tugs-anchors-mooring.md;未給 = 無拖船,雜湊與舊版相同)</summary>
    public List<ScenarioTug>? Tugs { get; set; }
    /// <summary>錨的初始狀態與抓地力調整(錨/鏈規格在 particulars anchors;未給 = 兩錨收存)</summary>
    public ScenarioAnchors? Anchors { get; set; }
    /// <summary>泊位(碼頭線、碰墊、繫船柱;data/areas/*.json 參照)與自船繫纜</summary>
    public ScenarioBerth? Berth { get; set; }
    /// <summary>岸壁/航道邊坡(規劃書第 6.2 節下標 B 岸壁效應;設計文件 physics-bank-effect.md;未給 = 不計算,雜湊與舊版相同)</summary>
    public ScenarioBanks? Banks { get; set; }

    /// <summary>載入時解析的參數集摘要(無引用時 null;不序列化,紀錄標頭另有 parameterSet 欄位)</summary>
    [JsonIgnore] public ParameterSetInfo? ParameterSetInfo { get; set; }

    /// <summary>是否有時間表/航線/區域/評估規則(決定雜湊鏈是否追加情境引擎位元組;無者雜湊與舊版一致)。</summary>
    [JsonIgnore] public bool HasScenarioEngine => Timeline.Count > 0 || Route is not null || Zones.Count > 0 || Assessment.HasRules;

    /// <summary>檢查必要欄位並補齊原點,再以 <see cref="StructuralIssues"/> 的第一個 error 項丟出例外;回傳自身以便串接。</summary>
    public Scenario Validate()
    {
        ValidateRequired();
        var firstError = StructuralIssues().FirstOrDefault(i => i.Severity == ScenarioIssueSeverity.Error);
        if (firstError is not null) throw new InvalidDataException($"{firstError.Path}:{firstError.Message}");
        return this;
    }

    /// <summary>
    /// 載入前檢查並列出全部問題(主控程序 loadScenario 拒絕時回報教官站用;<see cref="ScenarioLoader.TryLoad"/>):
    /// 必要欄位(<see cref="Validate"/> 的前半段)任一缺失即只回該項(path 為 "scenario"),通過後列出 <see cref="StructuralIssues"/> 的全部 error 項。
    /// 空清單 = 可載入(且已補齊原點,與 Validate 相同);不丟例外。
    /// </summary>
    public IReadOnlyList<ScenarioIssue> LoadIssues()
    {
        try { ValidateRequired(); }
        catch (InvalidDataException ex) { return new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "scenario", ex.Message) }; }
        return StructuralIssues().Where(i => i.Severity == ScenarioIssueSeverity.Error).ToList();
    }

    /// <summary>必要欄位檢查並補齊原點(id、ship.id、initial.position/origin、水深、能見度、目標船定義);缺失即丟 <see cref="InvalidDataException"/>。</summary>
    private void ValidateRequired()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("情境缺少 id");
        if (string.IsNullOrWhiteSpace(Ship.Id)) throw new InvalidDataException("情境缺少 ship.id");
        var pos = Initial.Position ?? throw new InvalidDataException("情境缺少 initial.position");
        var hasGeo = pos.Lat is not null && pos.Lon is not null;
        var hasLocal = pos.X is not null && pos.Y is not null;
        if (!hasGeo && !hasLocal) throw new InvalidDataException("initial.position 需為 {lat, lon} 或 {x, y}");
        if (Origin is null)
        {
            if (!hasGeo) throw new InvalidDataException("initial.position 以 {x, y} 指定時必須提供 origin {lat, lon}");
            Origin = new GeoPoint { Lat = pos.Lat!.Value, Lon = pos.Lon!.Value };
        }
        if (Environment.WaterDepth <= 0) throw new InvalidDataException("environment.waterDepth 必須為正");
        if (Environment.VisibilityNm is { } vis && vis <= 0) throw new InvalidDataException("environment.visibility_nm 必須為正");
        ValidateTargets();
    }

    /// <summary>
    /// 時間表/航線/區域/評估規則的結構檢查(不依賴初始狀態;情境引擎設計第 2.4 節 error 項):id 重複、參照不存在、
    /// 動作名稱不在允許清單、航點不足或重合、多邊形點數/自交、bands 指標名稱、required.after 事件。
    /// </summary>
    public IEnumerable<ScenarioIssue> StructuralIssues()
    {
        var issues = new List<ScenarioIssue>();
        void Error(string path, string msg) => issues.Add(new ScenarioIssue(ScenarioIssueSeverity.Error, path, msg));
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var zoneIds = new HashSet<string>(Zones.Select(z => z.Id), StringComparer.Ordinal);
        var wpNames = new HashSet<string>(Route?.Waypoints.Select(w => w.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var targetIds = new HashSet<string>(Targets.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var allItemIds = new HashSet<string>(Timeline.Select(t => t.Id), StringComparer.Ordinal);

        for (var i = 0; i < Timeline.Count; i++)
        {
            var item = Timeline[i];
            var path = $"timeline[{i}]";
            if (string.IsNullOrWhiteSpace(item.Id)) Error(path, "時間表項目缺少 id");
            else if (!itemIds.Add(item.Id)) Error(path, $"時間表 id 重複:{item.Id}");
            if (item.When is not null) CheckCondition(item.When, $"{path}.when", Error, zoneIds, wpNames, targetIds);
            if (item.Repeat.Mode == RepeatMode.Every && item.Repeat.EveryS <= 0) Error($"{path}.repeat", "every_s 必須為正");
            for (var a = 0; a < item.Actions.Count; a++)
            {
                var act = item.Actions[a];
                var ap = $"{path}.actions[{a}]";
                if (TimelineAction.ForbiddenKinds.Contains(act.Kind)) Error(ap, $"不允許的動作:{act.Kind}");
                else if (!TimelineAction.AllowedKinds.Contains(act.Kind)) Error(ap, $"未知的動作:{act.Kind}");
                else switch (act.Kind)
                {
                    case "arm" or "disarm":
                        if (act.AsString() is not { } rid || !allItemIds.Contains(rid)) Error(ap, $"{act.Kind} 引用不存在的時間表項目:{act.AsString()}");
                        break;
                    case "injectFault" or "clearFault":
                        if (string.IsNullOrWhiteSpace(act.AsString())) Error(ap, $"{act.Kind} 缺少故障名稱");
                        if (act.Kind == "injectFault" && act.Payload.ValueKind == JsonValueKind.Object && act.Payload.TryGetProperty("recordOnly", out var ro)
                            && ro.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            Error(ap, "injectFault.recordOnly 須為布林值");
                        break;
                    case "tugControl":
                        CheckTugControlAction(act, ap, Error);
                        break;
                    case "setEnvironment":
                        CheckSetEnvironmentAction(act, ap, Error);
                        break;
                    case "phase":
                        if (string.IsNullOrWhiteSpace(act.AsString())) Error(ap, "phase 缺少階段名稱");
                        break;
                    case "timeScale":
                        if (act.AsNumber() is not > 0) Error(ap, "timeScale 必須為正數");
                        break;
                    case "voiceScript":
                        if (act.AsVoiceScript() is not { } vs || (string.IsNullOrWhiteSpace(vs.Text) && string.IsNullOrWhiteSpace(vs.File))) Error(ap, "voiceScript 需要 text 或 file");
                        break;
                    case "message":
                        if (act.AsMessage() is not { } ms || string.IsNullOrWhiteSpace(ms.Text)) Error(ap, "message 需要 text");
                        break;
                }
            }
        }

        if (Route is { } route)
        {
            if (route.Waypoints.Count < 2) Error("route.waypoints", "航線至少需要 2 個航點");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < route.Waypoints.Count; i++)
            {
                var w = route.Waypoints[i];
                if (string.IsNullOrWhiteSpace(w.Name)) Error($"route.waypoints[{i}]", "航點缺少 name");
                else if (!names.Add(w.Name)) Error($"route.waypoints[{i}]", $"航點名稱重複:{w.Name}");
                if (!w.HasPosition) Error($"route.waypoints[{i}]", "航點需為 {lat, lon} 或 {x, y}");
            }
            if (route.StartLeg < 0 || (route.Waypoints.Count >= 2 && route.StartLeg >= route.Waypoints.Count - 1)) Error("route.startLeg", "startLeg 超出航段範圍");
            if (Origin is { } o)
            {
                var proj = new Geo.LocalTangentPlane(o.Lat, o.Lon);
                for (var i = 0; i + 1 < route.Waypoints.Count; i++)
                {
                    if (!route.Waypoints[i].HasPosition || !route.Waypoints[i + 1].HasPosition) continue;
                    var (ax, ay) = Resolve(proj, route.Waypoints[i]);
                    var (bx, by) = Resolve(proj, route.Waypoints[i + 1]);
                    if (Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) < 1.0)
                        Error($"route.waypoints[{i + 1}]", $"航點 {route.Waypoints[i + 1].Name} 與前一航點重合(航段 < 1 m)");
                }
            }
        }

        var zids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Zones.Count; i++)
        {
            var z = Zones[i];
            var path = $"zones[{i}]";
            if (string.IsNullOrWhiteSpace(z.Id)) Error(path, "區域缺少 id");
            else if (!zids.Add(z.Id)) Error(path, $"區域 id 重複:{z.Id}");
            if (z.Polygon.Count < 3) Error($"{path}.polygon", "多邊形至少需要 3 點");
            if (z.Polygon.Count > 64) Error($"{path}.polygon", "多邊形點數上限 64");
            foreach (var p in z.Polygon)
                if (!((p.Lat is not null && p.Lon is not null) || (p.X is not null && p.Y is not null))) { Error($"{path}.polygon", "頂點需為 {lat, lon} 或 {x, y}"); break; }
            if (z.WaterDepthM is <= 0) Error($"{path}.waterDepth_m", "區域水深必須為正");
            if (Origin is { } o && z.Polygon.Count >= 3 && z.Polygon.All(p => (p.Lat is not null && p.Lon is not null) || (p.X is not null && p.Y is not null)))
            {
                var proj = new Geo.LocalTangentPlane(o.Lat, o.Lon);
                var pts = z.Polygon.Select(p => Resolve(proj, p)).ToArray();
                if (Geo.Polygon2D.SelfIntersects(pts)) Error($"{path}.polygon", "多邊形自交");
            }
        }

        // 警報/BNWAS 設定(設計文件 sensors-alerts.md 第 3、4 節)
        if (Alerts.EscalateS <= 0) Error("alerts.escalate_s", "escalate_s 必須為正");
        if (Alerts.SilenceS <= 0) Error("alerts.silence_s", "silence_s 必須為正");
        if (Alerts.OffHeadingDeg is <= 0) Error("alerts.offHeading_deg", "offHeading_deg 必須為正");
        if (Alerts.OffHeadingDelayS < 0) Error("alerts.offHeadingDelay_s", "offHeadingDelay_s 不可為負");
        if (Alerts.HeadingMonitorDeg is <= 0) Error("alerts.headingMonitor_deg", "headingMonitor_deg 必須為正");
        if (Alerts.ShallowWaterM is <= 0) Error("alerts.shallowWater_m", "shallowWater_m 必須為正");
        if (Bnwas.DormantS < ScenarioBnwas.MinDormantS || Bnwas.DormantS > ScenarioBnwas.MaxDormantS)
            Error("bnwas.dormant_s", $"dormant_s 須在 {ScenarioBnwas.MinDormantS}–{ScenarioBnwas.MaxDormantS} s(MSC.128(75):3–12 分鐘)");
        if (Bnwas.Stage2S <= 0) Error("bnwas.stage2_s", "stage2_s 必須為正");
        if (Bnwas.Stage3S <= 0) Error("bnwas.stage3_s", "stage3_s 必須為正");

        // 拖船、錨、泊位(設計文件 physics-tugs-anchors-mooring.md)
        ScenarioExternalForces.CheckStructure(this, Error);
        // 岸壁(設計文件 physics-bank-effect.md)
        ScenarioBankLoader.CheckStructure(this, Error);
        // 雷達環境(radar-arpa.md 第 2.1 節)
        ScenarioRadarEnvironmentBuilder.CheckStructure(this, Error);

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Assessment.HardFail.Count; i++)
        {
            var r = Assessment.HardFail[i];
            var path = $"assessment.hardFail[{i}]";
            if (!ruleIds.Add(r.Id)) Error(path, $"規則 id 重複:{r.Id}");
            if (!HardFailRule.Rules.Contains(r.Rule)) Error(path, $"未知的 rule:{r.Rule}");
            if (r.Rule == "cpa" && r.LessThanNumber is null) Error(path, "cpa 規則需要 lessThan(nm)");
            // lessThan 須為物件且至少含 metres 或 fractionOfDraft 其一(與 Python structural_issues 相同;純數字或空物件皆不可)
            if (r.Rule == "ukc" && (r.LessThanUkc is not { } u || u.Metres is null && u.FractionOfDraft is null))
                Error(path, "ukc 規則需要 lessThan {metres|fractionOfDraft}");
            if (r.Rule == "zoneEntered" && (r.ZoneId is null || !zoneIds.Contains(r.ZoneId))) Error(path, $"zoneEntered 引用不存在的區域:{r.ZoneId}");
            if (r.Rule == "event" && string.IsNullOrWhiteSpace(r.Kind)) Error(path, "event 規則需要 kind");
        }
        for (var i = 0; i < Assessment.Bands.Count; i++)
        {
            var b = Assessment.Bands[i];
            var path = $"assessment.bands[{i}]";
            if (!ruleIds.Add(b.Id)) Error(path, $"規則 id 重複:{b.Id}");
            if (!AssessmentNs.MetricNames.IsKnown(b.Metric)) Error(path, $"未知的指標:{b.Metric}");
            if (b.Warn is null && b.Fail is null) Error(path, "容許帶需要 warn 或 fail");
            if (b.Fail is { Multiplier: not null } && b.Warn is null) Error(path, "fail 為倍數時需要 warn");
            if (b.Direction is not ("above" or "below")) Error(path, $"direction 須為 above | below:{b.Direction}");
        }
        for (var i = 0; i < Assessment.Required.Count; i++)
        {
            var r = Assessment.Required[i];
            var path = $"assessment.required[{i}]";
            if (!ruleIds.Add(r.Id)) Error(path, $"規則 id 重複:{r.Id}");
            if (string.IsNullOrWhiteSpace(r.After.Event)) Error($"{path}.after", "after 需要 event");
            if (r.After.ItemId is { } aid && ItemIdRefersToTimeline(r.After.Event) && !allItemIds.Contains(aid)) Error($"{path}.after", $"after 引用不存在的時間表項目:{aid}");
            if (r.After.Waypoint is { } awp && !wpNames.Contains(awp)) Error($"{path}.after", $"after 引用不存在的航點:{awp}");
            if (r.After.ZoneId is { } azid && !zoneIds.Contains(azid)) Error($"{path}.after", $"after 引用不存在的區域:{azid}");
            if (r.WithinS <= 0) Error(path, "within_s 必須為正");
            if (r.Expect.IsEmpty) Error($"{path}.expect", "必要事件需要 expect");
            foreach (var leaf in r.Expect.Leaves())
            {
                if (leaf.Name is { } n && !ExpectSpec.SimpleNames.Contains(n)) Error($"{path}.expect", $"未知的 expect:{n}");
                if (leaf.Event is { } ev && ev.ItemId is { } eid && ItemIdRefersToTimeline(ev.Kind) && !allItemIds.Contains(eid)) Error($"{path}.expect", $"expect 引用不存在的時間表項目:{eid}");
            }
        }
        // 潮汐(environment.tide;情境引擎設計第 1.5.1 節):table 至少 2 點、t 嚴格遞增、utc 需情境 startTimeUtc;harmonic 週期為正。與 Python structural_issues 一致
        if (Environment.Tide is { } tide)
        {
            const string tp = "environment.tide";
            var hasTable = tide.Table is not null;
            var hasHarmonic = tide.Harmonic is not null;
            if (hasTable == hasHarmonic) Error(tp, "tide 需為 table 或 harmonic 擇一");
            if (tide.Table is { } table)
            {
                if (table.Count < 2) Error($"{tp}.table", "潮高表至少需要 2 點");
                var startOk = Engine.SimulationEngine.TryParseUtc(StartTimeUtc, out var start);
                double? prev = null;
                for (var i = 0; i < table.Count; i++)
                {
                    var p = table[i];
                    var pp = $"{tp}.table[{i}]";
                    double? t = null;
                    if (p.T is not null && p.Utc is not null) Error(pp, "表點的 t 與 utc 只能擇一");
                    else if (p.T is { } given) t = given;
                    else if (p.Utc is { } utcText)
                    {
                        if (!Engine.SimulationEngine.TryParseUtc(utcText, out var utc)) Error(pp, $"utc 無法解析:{utcText}");
                        else if (!startOk) Error(pp, "表點以 utc 指定時情境必須有可解析的 startTimeUtc");
                        else t = (utc - start).TotalSeconds;
                    }
                    else Error(pp, "表點需要 t 或 utc");
                    if (!double.IsFinite(p.HeightM)) Error(pp, "height_m 必須為有限數值");
                    if (t is { } tv)
                    {
                        if (!double.IsFinite(tv)) Error(pp, "t 必須為有限數值");
                        else if (prev is { } pv && tv <= pv) Error(pp, "潮高表的 t 必須嚴格遞增");
                        prev = tv;
                    }
                }
            }
            if (tide.Harmonic is { } h)
            {
                if (!double.IsFinite(h.MeanLevelM)) Error($"{tp}.harmonic.meanLevel_m", "meanLevel_m 必須為有限數值");
                if (h.Constituents.Count == 0) Error($"{tp}.harmonic.constituents", "調和潮至少需要 1 個分潮");
                for (var i = 0; i < h.Constituents.Count; i++)
                {
                    var c = h.Constituents[i];
                    var cp = $"{tp}.harmonic.constituents[{i}]";
                    if (!(c.PeriodS > 0) || !double.IsFinite(c.PeriodS)) Error(cp, "period_s 必須為正");
                    if (!(c.AmplitudeM >= 0) || !double.IsFinite(c.AmplitudeM)) Error(cp, "amplitude_m 不可為負");
                    if (!double.IsFinite(c.PhaseDeg)) Error(cp, "phase_deg 必須為有限數值");
                }
            }
        }
        // 時變流場(environment.current.table | tidal;情境引擎設計第 1.5.2 節):擇一;表 ≥ 2 點、t/utc 擇一且嚴格遞增、速度非負;
        // tidal 需 environment.tide、flood/ebb 皆要有;空間變化流場 field 可與其一並存(見下)。與 Python current_issues 一致(路徑與訊息相同)
        {
            const string cp = "environment.current";
            var cur = Environment.Current;
            if (cur.Table is not null && cur.Tidal is not null) Error(cp, "current 的 table 與 tidal 只能擇一");
            if (cur.Table is { } table)
            {
                if (table.Count < 2) Error($"{cp}.table", "流場表至少需要 2 點");
                var startOk = Engine.SimulationEngine.TryParseUtc(StartTimeUtc, out var start);
                double? prev = null;
                for (var i = 0; i < table.Count; i++)
                {
                    var p = table[i];
                    var pp = $"{cp}.table[{i}]";
                    double? t = null;
                    if (p.T is not null && p.Utc is not null) Error(pp, "表點的 t 與 utc 只能擇一");
                    else if (p.T is { } given) t = given;
                    else if (p.Utc is { } utcText)
                    {
                        if (!Engine.SimulationEngine.TryParseUtc(utcText, out var utc)) Error(pp, $"utc 無法解析:{utcText}");
                        else if (!startOk) Error(pp, "表點以 utc 指定時情境必須有可解析的 startTimeUtc");
                        else t = (utc - start).TotalSeconds;
                    }
                    else Error(pp, "表點需要 t 或 utc");
                    if (!(p.SpeedKn >= 0) || !double.IsFinite(p.SpeedKn)) Error(pp, "speed_kn 不可為負");
                    if (!double.IsFinite(p.DirectionDeg)) Error(pp, "direction_deg 必須為有限數值");
                    if (t is { } tv)
                    {
                        if (!double.IsFinite(tv)) Error(pp, "t 必須為有限數值");
                        else if (prev is { } pv && tv <= pv) Error(pp, "流場表的 t 必須嚴格遞增");
                        prev = tv;
                    }
                }
            }
            if (cur.Tidal is { } tidal)
            {
                var tp2 = $"{cp}.tidal";
                if (Environment.Tide is null) Error(tp2, "tidal 需要 environment.tide");
                void CheckPhase(ScenarioCurrentPhase? phase, string name)
                {
                    if (phase is null) { Error($"{tp2}.{name}", $"tidal 需要 {name}"); return; }
                    if (!(phase.SpeedKn >= 0) || !double.IsFinite(phase.SpeedKn)) Error($"{tp2}.{name}", "speed_kn 不可為負");
                    if (!double.IsFinite(phase.DirectionDeg)) Error($"{tp2}.{name}", "direction_deg 必須為有限數值");
                }
                CheckPhase(tidal.Flood, "flood");
                CheckPhase(tidal.Ebb, "ebb");
                if (tidal.SlackWindowMin is { } w && (!(w >= 0) || !double.IsFinite(w))) Error($"{tp2}.slackWindow_min", "slackWindow_min 不可為負");
            }
            // 空間變化流場(environment.current.field):grid 必填且 dx/dy > 0、nx/ny ≥ 2;u_kn/v_kn 為 ny 列 × nx 行的有限數值;
            // outside ∈ edge|zero;timeScale ∈ none|tidal|table 且對應模型存在。與 Python current_issues 一致(路徑與訊息相同)
            if (cur.Field is { } field)
            {
                var fp = $"{cp}.field";
                int nx = 0, ny = 0;
                if (field.Grid is not { } grid) Error($"{fp}.grid", "field 需要 grid");
                else
                {
                    if (!double.IsFinite(grid.X0) || !double.IsFinite(grid.Y0)) Error($"{fp}.grid", "x0/y0 必須為有限數值");
                    if (!(grid.Dx > 0) || !double.IsFinite(grid.Dx)) Error($"{fp}.grid", "dx 必須為正");
                    if (!(grid.Dy > 0) || !double.IsFinite(grid.Dy)) Error($"{fp}.grid", "dy 必須為正");
                    if (grid.Nx < 2) Error($"{fp}.grid", "nx 必須為 ≥ 2 的整數");
                    if (grid.Ny < 2) Error($"{fp}.grid", "ny 必須為 ≥ 2 的整數");
                    nx = grid.Nx;
                    ny = grid.Ny;
                }
                void CheckArray(List<List<double>>? rows, string name)
                {
                    if (rows is null) { Error($"{fp}.{name}", $"field 需要 {name}"); return; }
                    if (nx < 2 || ny < 2) return;   // 格數錯誤時不再逐列檢查(訊息已足夠)
                    if (rows.Count != ny) { Error($"{fp}.{name}", $"{name} 須有 ny = {ny} 列(得到 {rows.Count})"); return; }
                    for (var j = 0; j < rows.Count; j++)
                    {
                        var row = rows[j];
                        if (row is null || row.Count != nx) { Error($"{fp}.{name}[{j}]", $"{name} 每列須有 nx = {nx} 個值(得到 {row?.Count ?? 0})"); continue; }
                        for (var i = 0; i < row.Count; i++)
                            if (!double.IsFinite(row[i])) Error($"{fp}.{name}[{j}][{i}]", $"{name} 的值必須為有限數值");
                    }
                }
                CheckArray(field.UKn, "u_kn");
                CheckArray(field.VKn, "v_kn");
                if (field.Outside is { } outside && outside.ToLowerInvariant() is not ("edge" or "zero")) Error($"{fp}.outside", $"outside 須為 edge | zero:{outside}");
                if (field.TimeScale is { } ts)
                {
                    var tsl = ts.ToLowerInvariant();
                    if (tsl is not ("none" or "tidal" or "table")) Error($"{fp}.timeScale", $"timeScale 須為 none | tidal | table:{ts}");
                    else if (tsl == "tidal" && cur.Tidal is null) Error($"{fp}.timeScale", "timeScale tidal 需要 current.tidal");
                    else if (tsl == "table" && cur.Table is null) Error($"{fp}.timeScale", "timeScale table 需要 current.table");
                }
            }
        }
        // 浪(environment.waves;設計文件 physics-waves.md 第 2 節):Hs ≥ 0、Tp > 0、來向 0–360、spectrum pm|jonswap、gamma ≥ 1。與 Python waves_issues 一致
        if (Environment.Waves is { } wv)
        {
            const string wp = "environment.waves";
            if (wv.SignificantHeightM is not { } hs || !double.IsFinite(hs) || hs < 0) Error($"{wp}.significantHeight_m", "significantHeight_m 必須為 ≥ 0 的有限數值");
            if (wv.PeakPeriodS is not { } tpk || !double.IsFinite(tpk) || tpk <= 0) Error($"{wp}.peakPeriod_s", "peakPeriod_s 必須為正");
            if (wv.DirectionDeg is not { } dir || !double.IsFinite(dir) || dir < 0 || dir > 360) Error($"{wp}.direction_deg", "direction_deg 須在 0–360(來向)");
            if (wv.Spectrum is { } spec && spec.ToLowerInvariant() is not ("pm" or "jonswap")) Error($"{wp}.spectrum", $"spectrum 須為 pm | jonswap:{spec}");
            if (wv.Gamma is { } g && (!double.IsFinite(g) || g < 1)) Error($"{wp}.gamma", "gamma 必須 ≥ 1");
        }
        // 船間交互作用(environment.shipInteraction;設計文件 physics-ship-interaction.md 第 2 節):coefficientScale ≥ 0、eventThreshold_t ≥ 0(皆有限)。與 Python ship_interaction_issues 一致
        if (Environment.ShipInteraction is { } si)
        {
            const string sp = "environment.shipInteraction";
            if (si.CoefficientScale is { } scale && (!double.IsFinite(scale) || scale < 0)) Error($"{sp}.coefficientScale", "coefficientScale 必須為 ≥ 0 的有限數值");
            if (si.EventThresholdT is { } thr && (!double.IsFinite(thr) || thr < 0)) Error($"{sp}.eventThreshold_t", "eventThreshold_t 必須為 ≥ 0 的有限數值");
        }
        // 橫搖(ship.roll;設計文件 physics-roll.md 第 2 節):naturalPeriod_s > 0、0 < dampingRatio < 1、各 gain ≥ 0(皆有限;enabled 與否都檢查)。與 Python roll_issues 一致
        if (Ship.Roll is { } roll)
        {
            const string rp = "ship.roll";
            if (roll.NaturalPeriodS is { } tn && (!double.IsFinite(tn) || tn <= 0)) Error($"{rp}.naturalPeriod_s", "naturalPeriod_s 必須為正");
            if (roll.DampingRatio is { } zeta && (!double.IsFinite(zeta) || zeta <= 0 || zeta >= 1)) Error($"{rp}.dampingRatio", "dampingRatio 須在 0–1 之間(不含)");
            if (roll.WindHeelGain is { } wg && (!double.IsFinite(wg) || wg < 0)) Error($"{rp}.windHeelGain", "windHeelGain 必須為 ≥ 0 的有限數值");
            if (roll.TurnHeelGain is { } tg && (!double.IsFinite(tg) || tg < 0)) Error($"{rp}.turnHeelGain", "turnHeelGain 必須為 ≥ 0 的有限數值");
            if (roll.WaveGain is { } vg && (!double.IsFinite(vg) || vg < 0)) Error($"{rp}.waveGain", "waveGain 必須為 ≥ 0 的有限數值");
        }
        return issues;
    }

    /// <summary>
    /// 事件的 itemId 是否指時間表項目。警報與 BNWAS 事件(alert*、bnwas*)的 itemId 是警報 id
    /// (例如 gyroFailure、bnwas;docs/design/sensors-alerts.md),不比對時間表項目。
    /// </summary>
    internal static bool ItemIdRefersToTimeline(string? eventKind)
        => eventKind is null || !(eventKind.StartsWith("alert", StringComparison.Ordinal) || eventKind.StartsWith("bnwas", StringComparison.Ordinal));

    /// <summary>
    /// 時間表 setEnvironment 動作(與指令相同 args;設計文件 physics-waves.md「練習中變更」):須為物件;waves 為 null(關閉)或物件,欄位皆選填,
    /// 給了就檢查(Hs ≥ 0 有限、Tp &gt; 0、來向 0–360、spectrum pm|jonswap、gamma ≥ 1;訊息與 environment.waves 相同)。與 Python set_environment_action_issues 一致。
    /// </summary>
    private static void CheckSetEnvironmentAction(TimelineAction act, string path, Action<string, string> error)
    {
        if (act.Payload.ValueKind != JsonValueKind.Object)
        {
            error(path, "setEnvironment 須為物件");
            return;
        }
        if (!act.Payload.TryGetProperty("waves", out var wv) || wv.ValueKind == JsonValueKind.Null) return;
        var wp = $"{path}.setEnvironment.waves";
        if (wv.ValueKind != JsonValueKind.Object)
        {
            error(wp, "setEnvironment.waves 須為物件或 null");
            return;
        }
        if (wv.TryGetProperty("significantHeight_m", out var hs) && (SimCommand.AsDouble(hs) is not { } h || !double.IsFinite(h) || h < 0)) error($"{wp}.significantHeight_m", "significantHeight_m 必須為 ≥ 0 的有限數值");
        if (wv.TryGetProperty("peakPeriod_s", out var tp) && (SimCommand.AsDouble(tp) is not { } t || !double.IsFinite(t) || t <= 0)) error($"{wp}.peakPeriod_s", "peakPeriod_s 必須為正");
        if (wv.TryGetProperty("direction_deg", out var dir) && (SimCommand.AsDouble(dir) is not { } d || !double.IsFinite(d) || d < 0 || d > 360)) error($"{wp}.direction_deg", "direction_deg 須在 0–360(來向)");
        if (wv.TryGetProperty("spectrum", out var sp) && (sp.ValueKind != JsonValueKind.String || sp.GetString()!.ToLowerInvariant() is not ("pm" or "jonswap"))) error($"{wp}.spectrum", $"spectrum 須為 pm | jonswap:{sp}");
        if (wv.TryGetProperty("gamma", out var g) && (SimCommand.AsDouble(g) is not { } gv || !double.IsFinite(gv) || gv < 1)) error($"{wp}.gamma", "gamma 必須 ≥ 1");
    }

    /// <summary>時間表 tugControl 動作(與指令相同 args):id 須為情境 tugs[].id,mode/power_pct/direction_deg 須合法。</summary>
    private void CheckTugControlAction(TimelineAction act, string path, Action<string, string> error)
    {
        if (act.Payload.ValueKind != JsonValueKind.Object) { error(path, "tugControl 須為物件 {id, mode?, direction_deg?, power_pct?}"); return; }
        var id = act.Field("id");
        if (string.IsNullOrWhiteSpace(id)) error(path, "tugControl 缺少 id");
        else if (Tugs?.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) != true) error(path, $"tugControl 引用不存在的拖船:{id}(須為 tugs[].id)");
        if (act.Payload.TryGetProperty("mode", out var m)
            && (m.ValueKind != JsonValueKind.String || m.GetString()?.ToLowerInvariant() is not ("push" or "pull" or "standby" or "free" or "unavailable")))
            error(path, $"tugControl.mode 須為 push|pull|standby|free|unavailable:{m}");
        if (act.Payload.TryGetProperty("power_pct", out _) && act.NumberField("power_pct") is not (>= 0 and <= 100)) error(path, "tugControl.power_pct 須為 0–100");
        if (act.Payload.TryGetProperty("direction_deg", out _) && act.NumberField("direction_deg") is null) error(path, "tugControl.direction_deg 須為數值");
    }

    private static void CheckCondition(TriggerCondition c, string path, Action<string, string> error,
        HashSet<string> zoneIds, HashSet<string> wpNames, HashSet<string> targetIds)
    {
        if (c.IsComposite)
        {
            if (c.Type is not null) error(path, "組合條件不可同時有 type");
            if (c.All is not null) for (var i = 0; i < c.All.Count; i++) CheckCondition(c.All[i], $"{path}.all[{i}]", error, zoneIds, wpNames, targetIds);
            if (c.Any is not null) for (var i = 0; i < c.Any.Count; i++) CheckCondition(c.Any[i], $"{path}.any[{i}]", error, zoneIds, wpNames, targetIds);
            if (c.Not is not null) CheckCondition(c.Not, $"{path}.not", error, zoneIds, wpNames, targetIds);
            return;
        }
        if (c.Type is null || !TriggerCondition.LeafTypes.Contains(c.Type)) { error(path, $"未知的條件型別:{c.Type}"); return; }
        switch (c.Type)
        {
            case "time" when c.AtLeast is null: error(path, "time 條件需要 atLeast"); break;
            case "ownDistance":
                if (c.Point is null && c.Waypoint is null && c.Target is null) error(path, "ownDistance 需要 point | waypoint | target");
                if (c.LessThan is null && c.GreaterThan is null) error(path, "ownDistance 需要 lessThan / greaterThan");
                if (c.Waypoint is { } w && !wpNames.Contains(w)) error(path, $"ownDistance 引用不存在的航點:{w}");
                if (c.Target is { } t && !targetIds.Contains(t)) error(path, $"ownDistance 引用不存在的目標:{t}");
                break;
            case "ownHeading" or "ownSpeed" when c.LessThan is null && c.GreaterThan is null: error(path, $"{c.Type} 需要 lessThan / greaterThan"); break;
            case "ukc" when c.LessThan is null && c.GreaterThan is null && c.LessThanFractionOfDraft is null: error(path, "ukc 需要 lessThan / greaterThan / lessThanFractionOfDraft"); break;
            case "xtd" when c.GreaterThan is null: error(path, "xtd 需要 greaterThan"); break;
            case "routeLeg":
                if (c.AtLeast is null && c.EqualsNumber is null && c.PassedWaypoint is null) error(path, "routeLeg 需要 atLeast | equals | passedWaypoint");
                if (c.PassedWaypoint is { } pw && !wpNames.Contains(pw)) error(path, $"routeLeg 引用不存在的航點:{pw}");
                break;
            case "zone":
                if (c.Id is null || !zoneIds.Contains(c.Id)) error(path, $"zone 引用不存在的區域:{c.Id}");
                break;
            case "phase" when string.IsNullOrWhiteSpace(c.EqualsString): error(path, "phase 需要 equals"); break;
            case "event" when string.IsNullOrWhiteSpace(c.Kind): error(path, "event 需要 kind"); break;
            case "fault" when string.IsNullOrWhiteSpace(c.Name): error(path, "fault 需要 name"); break;
            case "telegraph" when c.In is null && c.NotIn is null: error(path, "telegraph 需要 in / notIn"); break;
            case "autopilot" when c.Enabled is null: error(path, "autopilot 需要 enabled"); break;
        }
        if (c.Since is { } s && string.IsNullOrWhiteSpace(s.Event)) error(path, "since 需要 event");
    }

    internal static (double X, double Y) Resolve(Geo.LocalTangentPlane proj, RouteWaypoint w)
        => w.X is not null && w.Y is not null ? (w.X.Value, w.Y.Value) : proj.ToLocal(w.Lat!.Value, w.Lon!.Value);

    internal static (double X, double Y) Resolve(Geo.LocalTangentPlane proj, ScenarioPosition p)
        => p.X is not null && p.Y is not null ? (p.X.Value, p.Y.Value) : proj.ToLocal(p.Lat!.Value, p.Lon!.Value);

    /// <summary>目標船定義檢查:id 唯一、位置可解析、航點/跟隨/腳本的必要欄位。</summary>
    private void ValidateTargets()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) throw new InvalidDataException("targets[] 缺少 id");
            if (!ids.Add(t.Id)) throw new InvalidDataException($"目標船 id 重複:{t.Id}");
            ValidateTarget(t);
        }
    }

    /// <summary>單一目標船定義檢查(情境載入與 targetControl add 共用)。</summary>
    public static void ValidateTarget(ScenarioTarget t)
    {
        if (string.IsNullOrWhiteSpace(t.Id)) throw new InvalidDataException("目標船缺少 id");
        if (t.Loa <= 0 || t.Beam <= 0) throw new InvalidDataException($"目標船 {t.Id} 的 loa/beam 必須為正");
        var pos = t.Initial.Position ?? throw new InvalidDataException($"目標船 {t.Id} 缺少 initial.position");
        if (!((pos.Lat is not null && pos.Lon is not null) || (pos.X is not null && pos.Y is not null)))
            throw new InvalidDataException($"目標船 {t.Id} 的 initial.position 需為 {{lat, lon}} 或 {{x, y}}");
        var b = t.Behaviour;
        if (b.Mode == TargetBehaviourMode.Waypoints && (b.Waypoints is null || b.Waypoints.Count == 0))
            throw new InvalidDataException($"目標船 {t.Id} 為 waypoints 模式但無航點");
        foreach (var w in b.Waypoints ?? new List<TargetWaypoint>())
            if (!((w.Lat is not null && w.Lon is not null) || (w.X is not null && w.Y is not null)))
                throw new InvalidDataException($"目標船 {t.Id} 的航點需為 {{lat, lon}} 或 {{x, y}}");
        if (b.Mode == TargetBehaviourMode.Scripted && (b.Script is null || b.Script.Count == 0))
            throw new InvalidDataException($"目標船 {t.Id} 為 scripted 模式但無腳本");
        if (t.Trigger is { } tr)
        {
            switch (tr.Type)
            {
                case TargetTriggerType.Time when tr.Time is null:
                    throw new InvalidDataException($"目標船 {t.Id} 的 time 觸發缺少 time");
                case TargetTriggerType.OwnDistance when tr.Point is null || (tr.LessThan is null && tr.GreaterThan is null):
                    throw new InvalidDataException($"目標船 {t.Id} 的 ownDistance 觸發需要 point 與 lessThan/greaterThan");
                case TargetTriggerType.OwnHeading when tr.LessThan is null && tr.GreaterThan is null:
                    throw new InvalidDataException($"目標船 {t.Id} 的 ownHeading 觸發需要 lessThan/greaterThan");
            }
        }
    }

    public string ToJson() => ContractJson.Serialize(this);
    public static Scenario FromJson(string json)
        => (ContractJson.Deserialize<Scenario>(json) ?? throw new InvalidDataException("無法解析情境 JSON")).Validate();
}

public sealed class ScenarioShip
{
    /// <summary>FSB1 / FSB2</summary>
    public string Id { get; set; } = "FSB1";
    public LoadingCondition Loading { get; set; } = LoadingCondition.Ballast;
    /// <summary>
    /// 解耦 1 自由度橫搖(選填、預設關閉;設計文件 docs/design/physics-roll.md):enabled 時核心以線性橫搖方程式計算迴轉/風傾靜傾與浪激勵橫搖,
    /// 輸出 state.roll、雜湊追加 ROLLDOF1 區塊;不回饋到 3 自由度運動。未給或 enabled = false 時與舊版逐位元相同。
    /// </summary>
    public ScenarioRoll? Roll { get; set; }
}

/// <summary>情境橫搖設定(<see cref="Scenario.StructuralIssues"/> 檢查:naturalPeriod_s &gt; 0、0 &lt; dampingRatio &lt; 1、各 gain ≥ 0;Python roll_issues 同路徑與訊息)。</summary>
public sealed class ScenarioRoll
{
    /// <summary>啟用(預設 false:有區段但未啟用與無區段相同)</summary>
    public bool Enabled { get; set; }
    /// <summary>自然橫搖週期(s,&gt; 0;未給時依 GM 與船寬估計 T = 2·0.4·B/√GM,GM 取 particulars 或預設 1.2 m)</summary>
    [JsonPropertyName("naturalPeriod_s")] public double? NaturalPeriodS { get; set; }
    /// <summary>阻尼比 ζ(0–1 之間,不含;預設 0.08)</summary>
    public double? DampingRatio { get; set; }
    /// <summary>風傾倍率(≥ 0;預設 1)</summary>
    public double? WindHeelGain { get; set; }
    /// <summary>迴轉傾倍率(≥ 0;預設 1)</summary>
    public double? TurnHeelGain { get; set; }
    /// <summary>浪激勵倍率(≥ 0;預設 1)</summary>
    public double? WaveGain { get; set; }
}

public sealed class GeoPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
}

/// <summary>初始位置:經緯度(度)或本地 ENU(m),二擇一。</summary>
public sealed class ScenarioPosition
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
}

public sealed class ScenarioInitial
{
    public ScenarioPosition? Position { get; set; }
    /// <summary>航向(度,0–360)</summary>
    public double Heading { get; set; }
    /// <summary>對水速度(kn)</summary>
    public double Speed { get; set; }
    /// <summary>
    /// 初始車鐘代號(EFAS…NAVF);未指定時:動力學模型能解直航穩態者(MMG)取該航速的平衡轉速並以最接近的車令標示,
    /// 否則取航速最接近的前進車令(0 kn → STOP)
    /// </summary>
    public string? Telegraph { get; set; }
    /// <summary>初始軸轉速(rpm,倒車負);指定時優先於車鐘/航速推得的轉速(轉速令同值)</summary>
    public double? Rpm { get; set; }
    /// <summary>初始舵角(度)</summary>
    public double Rudder { get; set; }
}

public sealed class ScenarioEnvironment
{
    public ScenarioWind Wind { get; set; } = new();
    public ScenarioCurrent Current { get; set; } = new();
    /// <summary>水深(m,常數;日後可改為水深網格參照)</summary>
    public double WaterDepth { get; set; } = 50.0;
    /// <summary>能見度(nm;未給 = 無限制;無物理效果)</summary>
    [JsonPropertyName("visibility_nm")] public double? VisibilityNm { get; set; }
    /// <summary>海床底質(mud | sand | clay | rock;錨抓著係數與鏈摩擦;未給 = sand)</summary>
    public string? Seabed { get; set; }
    /// <summary>雷達環境(雨區、海況;無物理效果,以 state.radarEnvironment 廣播給雷達服務;ScenarioRadarEnvironment.cs)</summary>
    public ScenarioRadarEnvironment? Radar { get; set; }
    /// <summary>
    /// 潮汐(選填;情境引擎設計第 1.5.1 節):有效水深 = waterDepth(海圖/基底水深,含 zones 覆寫與 setEnvironment 漸變)+ 潮高(t)。
    /// table(線性內插,範圍外取端點)或 harmonic(mean + Σ A·cos(2πt/T + φ))擇一;未給時潮高恆為 0,數值與舊版相同。
    /// </summary>
    public ScenarioTide? Tide { get; set; }
    /// <summary>
    /// 浪(選填;設計文件 physics-waves.md):有義波高、譜峰週期、來向(氣象慣例)、浪譜(pm | jonswap)與 γ。
    /// 有此區段時核心計算平均二階波浪漂移力(surge/sway/yaw)並輸出 state.waves;未給時零力、輸出與雜湊與舊版相同。
    /// </summary>
    public ScenarioWaves? Waves { get; set; }
    /// <summary>
    /// 船間交互作用(選填;設計文件 physics-ship-interaction.md):enabled 時核心計算目標船通過/追越時的流體交互作用力(surge/sway/yaw;
    /// Tuck–Newman 型細長體線源模型,單向)並輸出 state.shipInteraction、超過門檻時發 shipInteraction 事件;未給或 enabled = false 時零力、輸出與雜湊與舊版相同。
    /// </summary>
    public ScenarioShipInteraction? ShipInteraction { get; set; }
}

/// <summary>情境船間交互作用設定(<see cref="Scenario.StructuralIssues"/> 檢查:coefficientScale ≥ 0、eventThreshold_t ≥ 0)。</summary>
public sealed class ScenarioShipInteraction
{
    /// <summary>事件門檻預設(t):等效側向力 max(|Y|, 4|N|/L) ≥ 門檻時發 shipInteraction 進入事件,降到一半以下發離開事件</summary>
    public const double DefaultEventThresholdT = 3.0;
    /// <summary>啟用(預設 false:有區段但未啟用與無區段相同)</summary>
    public bool Enabled { get; set; }
    /// <summary>整體倍率(≥ 0;預設 1;0 = 只顯示不施力)</summary>
    public double? CoefficientScale { get; set; }
    /// <summary>事件門檻(t,≥ 0;預設 3;0 = 不發事件)</summary>
    [JsonPropertyName("eventThreshold_t")] public double? EventThresholdT { get; set; }
}

/// <summary>情境浪設定(<see cref="Scenario.StructuralIssues"/> 檢查:Hs ≥ 0、Tp &gt; 0、來向 0–360、spectrum pm|jonswap、gamma ≥ 1)。</summary>
public sealed class ScenarioWaves
{
    /// <summary>有義波高 Hs(m,≥ 0)</summary>
    [JsonPropertyName("significantHeight_m")] public double? SignificantHeightM { get; set; }
    /// <summary>譜峰週期 Tp(s,&gt; 0)</summary>
    [JsonPropertyName("peakPeriod_s")] public double? PeakPeriodS { get; set; }
    /// <summary>浪來向(度,0–360;氣象慣例,與風相同)</summary>
    [JsonPropertyName("direction_deg")] public double? DirectionDeg { get; set; }
    /// <summary>浪譜 pm(Pierson–Moskowitz,預設)| jonswap</summary>
    public string? Spectrum { get; set; }
    /// <summary>JONSWAP 峰度係數 γ(預設 3.3;pm 時忽略)</summary>
    public double? Gamma { get; set; }
}

/// <summary>情境潮汐設定:<see cref="Table"/> 與 <see cref="Harmonic"/> 擇一(<see cref="Scenario.StructuralIssues"/> 檢查)。</summary>
public sealed class ScenarioTide
{
    /// <summary>潮高表(≥ 2 點、t 嚴格遞增)</summary>
    public List<ScenarioTidePoint>? Table { get; set; }
    /// <summary>調和潮</summary>
    public ScenarioHarmonicTide? Harmonic { get; set; }
}

/// <summary>潮高表的一點:t(秒,相對練習開始)或 utc(ISO 8601,換算為相對情境 startTimeUtc 的秒數)擇一,height_m 為潮高(m,相對海圖基準)。</summary>
public sealed class ScenarioTidePoint
{
    public double? T { get; set; }
    public string? Utc { get; set; }
    [JsonPropertyName("height_m")] public double HeightM { get; set; }
}

/// <summary>調和潮:高度 = meanLevel_m + Σ amplitude_m·cos(2π·t/period_s + phase_deg)。</summary>
public sealed class ScenarioHarmonicTide
{
    [JsonPropertyName("meanLevel_m")] public double MeanLevelM { get; set; }
    public List<ScenarioTideConstituent> Constituents { get; set; } = new();
}

/// <summary>分潮(name 只作標示,例如 M2、S2、K1、O1)。</summary>
public sealed class ScenarioTideConstituent
{
    public string? Name { get; set; }
    [JsonPropertyName("amplitude_m")] public double AmplitudeM { get; set; }
    [JsonPropertyName("period_s")] public double PeriodS { get; set; }
    [JsonPropertyName("phase_deg")] public double PhaseDeg { get; set; }
}

public enum ScenarioIssueSeverity
{
    Error,
    Warning,
}

/// <summary>情境靜態檢查結果(規劃書第 9.1 節自動檢查)。</summary>
public sealed record ScenarioIssue(ScenarioIssueSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"[{(Severity == ScenarioIssueSeverity.Error ? "error" : "warning")}] {Path}:{Message}";
}

public sealed class ScenarioWind
{
    /// <summary>真風速(kn)</summary>
    public double TrueSpeed { get; set; }
    /// <summary>真風來向(度)</summary>
    public double TrueDir { get; set; }
    /// <summary>陣風強度(0–1,每秒以種子亂數更新 ±gustiness 的係數)</summary>
    public double Gustiness { get; set; }
}

public sealed class ScenarioCurrent
{
    /// <summary>流向(去向,度);有 <see cref="Table"/>/<see cref="Tidal"/> 時為載入瞬間的常數值,t = 0 即被模型覆寫(保留供摘要/編輯器顯示)</summary>
    public double Set { get; set; }
    /// <summary>流速(kn);同上</summary>
    public double Drift { get; set; }
    /// <summary>
    /// 流場表(選填;情境引擎設計第 1.5.2 節):[{t | utc, speed_kn, direction_deg}],以東/北分量線性內插(範圍外取端點);≥ 2 點、t 嚴格遞增。
    /// 與 <see cref="Tidal"/> 擇一;皆未給 = 常數流(數值與舊版相同)。
    /// </summary>
    public List<ScenarioCurrentPoint>? Table { get; set; }
    /// <summary>潮流(選填):由 environment.tide 的 dH/dt 符號決定漲/退、|dH/dt| 相對最大值縮放流速;需 environment.tide。</summary>
    public ScenarioTidalCurrent? Tidal { get; set; }
    /// <summary>
    /// 空間變化流場(選填;情境引擎設計第 1.5.2 節):規則網格上的東/北分量(kn)以雙線性內插,可與 <see cref="Table"/>/<see cref="Tidal"/> 並存
    /// (有 field 時流由場決定,table/tidal 只作為 timeScale 的流速比例來源)。未給時行為與數值與舊版相同。
    /// </summary>
    public ScenarioCurrentField? Field { get; set; }
}

/// <summary>
/// 空間變化流場:grid(本地 ENU 公尺,原點與情境座標語意相同)、u_kn/v_kn(東向/北向分量,kn;外層列為 y 索引 j、內層行為 x 索引 i,
/// 即 u_kn[j][i] 對應點 (x0 + i·dx, y0 + j·dy))、outside(格外:edge 取最近邊緣值 | zero 取 0;預設 edge)、
/// timeScale(none | tidal | table;預設 none;tidal/table 時整個場乘以該模型的流速比例,方向取場的向量)。
/// </summary>
public sealed class ScenarioCurrentField
{
    public const string DefaultOutside = "edge";
    public const string DefaultTimeScale = "none";
    public ScenarioCurrentGrid? Grid { get; set; }
    [JsonPropertyName("u_kn")] public List<List<double>>? UKn { get; set; }
    [JsonPropertyName("v_kn")] public List<List<double>>? VKn { get; set; }
    /// <summary>格外取值:edge | zero(未給 = edge)</summary>
    public string? Outside { get; set; }
    /// <summary>時間縮放:none | tidal | table(未給 = none;tidal 需 current.tidal、table 需 current.table)</summary>
    public string? TimeScale { get; set; }
}

/// <summary>流場網格:原點 (x0, y0)、格距 dx/dy(m,> 0)、格數 nx/ny(≥ 2);節點 (x0 + i·dx, y0 + j·dy),i ∈ [0, nx)、j ∈ [0, ny)。</summary>
public sealed class ScenarioCurrentGrid
{
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double Dx { get; set; }
    public double Dy { get; set; }
    public int Nx { get; set; }
    public int Ny { get; set; }
}

/// <summary>流場表的一點:t(秒,相對練習開始)或 utc(ISO 8601,換算為相對情境 startTimeUtc 的秒數)擇一;speed_kn 流速、direction_deg 流向(去向)。</summary>
public sealed class ScenarioCurrentPoint
{
    public double? T { get; set; }
    public string? Utc { get; set; }
    [JsonPropertyName("speed_kn")] public double SpeedKn { get; set; }
    [JsonPropertyName("direction_deg")] public double DirectionDeg { get; set; }
}

/// <summary>潮流設定:漲潮/退潮的流向(去向)與最大流速,平潮窗口(分;預設 20)。</summary>
public sealed class ScenarioTidalCurrent
{
    public const double DefaultSlackWindowMin = 20.0;
    public ScenarioCurrentPhase? Flood { get; set; }
    public ScenarioCurrentPhase? Ebb { get; set; }
    /// <summary>平潮窗口(分;轉流前後各半個窗口視為平潮;未給 = 20)</summary>
    [JsonPropertyName("slackWindow_min")] public double? SlackWindowMin { get; set; }
}

/// <summary>潮流一相(漲或退)的流速(kn,該相的最大值)與流向(去向,度)。</summary>
public sealed class ScenarioCurrentPhase
{
    [JsonPropertyName("speed_kn")] public double SpeedKn { get; set; }
    [JsonPropertyName("direction_deg")] public double DirectionDeg { get; set; }
}
