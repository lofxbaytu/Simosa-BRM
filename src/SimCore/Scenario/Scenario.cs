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

    /// <summary>載入時解析的參數集摘要(無引用時 null;不序列化,紀錄標頭另有 parameterSet 欄位)</summary>
    [JsonIgnore] public ParameterSetInfo? ParameterSetInfo { get; set; }

    /// <summary>是否有時間表/航線/區域/評估規則(決定雜湊鏈是否追加情境引擎位元組;無者雜湊與舊版一致)。</summary>
    [JsonIgnore] public bool HasScenarioEngine => Timeline.Count > 0 || Route is not null || Zones.Count > 0 || Assessment.HasRules;

    /// <summary>檢查必要欄位並補齊原點;回傳自身以便串接。</summary>
    public Scenario Validate()
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
        var firstError = StructuralIssues().FirstOrDefault(i => i.Severity == ScenarioIssueSeverity.Error);
        if (firstError is not null) throw new InvalidDataException($"{firstError.Path}:{firstError.Message}");
        return this;
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
        return issues;
    }

    /// <summary>
    /// 事件的 itemId 是否指時間表項目。警報與 BNWAS 事件(alert*、bnwas*)的 itemId 是警報 id
    /// (例如 gyroFailure、bnwas;docs/design/sensors-alerts.md),不比對時間表項目。
    /// </summary>
    internal static bool ItemIdRefersToTimeline(string? eventKind)
        => eventKind is null || !(eventKind.StartsWith("alert", StringComparison.Ordinal) || eventKind.StartsWith("bnwas", StringComparison.Ordinal));

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
    /// <summary>流向(去向,度)</summary>
    public double Set { get; set; }
    /// <summary>流速(kn)</summary>
    public double Drift { get; set; }
}
