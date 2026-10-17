using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Scenario;

// 情境的拖船、錨、泊位(碰墊/繫船柱/繫纜)區段(全部選填;src/Contracts/scenario.schema.json;
// 設計文件 docs/design/physics-tugs-anchors-mooring.md;Python 參考實作忽略)。

/// <summary>
/// 船體座標點:具名(fwd|aft|bow|stern|fwdPort|fwdStbd|midPort|midStbd|aftPort|aftStbd)或 {x, y}(m,x 向艏、y 向右舷、原點船舯)。
/// YAML/JSON 可寫字串或物件。
/// </summary>
[JsonConverter(typeof(ScenarioBodyPointConverter))]
public sealed class ScenarioBodyPoint
{
    public string? Named { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }

    public static readonly string[] Names = { "fwd", "aft", "bow", "stern", "fwdPort", "fwdStbd", "midPort", "midStbd", "aftPort", "aftStbd" };

    public bool IsValid => Named is { } n ? Names.Contains(n, StringComparer.OrdinalIgnoreCase) : X is not null && Y is not null;

    /// <summary>
    /// 換算為船體座標(m)。具名點以 L = 垂標間長、B = 型寬估計:fwd/bow = (+0.45 L, 0)、aft/stern = (−0.45 L, 0)、
    /// fwdPort/fwdStbd = (+0.40 L, ∓0.45 B)、midPort/midStbd = (0, ∓0.5 B)、aftPort/aftStbd = (−0.40 L, ∓0.45 B)(port 為負 y)。
    /// </summary>
    public (double X, double Y) Resolve(ShipParticulars ship)
    {
        var l = ship.Hull.LengthBetweenPerpendiculars_m;
        var b = ship.Hull.Breadth_m;
        if (Named is null) return (X ?? 0.0, Y ?? 0.0);
        return Named.ToLowerInvariant() switch
        {
            "fwd" or "bow" => (0.45 * l, 0.0),
            "aft" or "stern" => (-0.45 * l, 0.0),
            "fwdport" => (0.40 * l, -0.45 * b),
            "fwdstbd" => (0.40 * l, 0.45 * b),
            "midport" => (0.0, -0.5 * b),
            "midstbd" => (0.0, 0.5 * b),
            "aftport" => (-0.40 * l, -0.45 * b),
            "aftstbd" => (-0.40 * l, 0.45 * b),
            _ => (0.0, 0.0),
        };
    }
}

public sealed class ScenarioBodyPointConverter : JsonConverter<ScenarioBodyPoint>
{
    public override ScenarioBodyPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return new ScenarioBodyPoint { Named = reader.GetString() };
        using var doc = JsonDocument.ParseValue(ref reader);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException("船體座標點須為字串或 {x, y}");
        double? Get(string n) => e.TryGetProperty(n, out var v) ? SimCommand.AsDouble(v) : null;
        return new ScenarioBodyPoint { X = Get("x"), Y = Get("y") };
    }

    public override void Write(Utf8JsonWriter writer, ScenarioBodyPoint value, JsonSerializerOptions options)
    {
        if (value.Named is { } n) { writer.WriteStringValue(n); return; }
        writer.WriteStartObject();
        if (value.X is { } x) writer.WriteNumber("x", x);
        if (value.Y is { } y) writer.WriteNumber("y", y);
        writer.WriteEndObject();
    }
}

/// <summary>情境拖船(規劃書第 6.2 節 T 項:作用點固定在船上繫纜點的推/拉力向量)。</summary>
public sealed class ScenarioTug
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>繫柱拖力(t)</summary>
    [JsonPropertyName("bollardPull_t")] public double BollardPullT { get; set; } = 40.0;
    /// <summary>連接點(fwd | aft | 具名點 | {x, y});預設 fwd</summary>
    public ScenarioBodyPoint? Attach { get; set; }
    /// <summary>拖纜長(m;繞行速率推估與顯示用)</summary>
    [JsonPropertyName("lineLength_m")] public double LineLengthM { get; set; } = 40.0;
    /// <summary>可用模式(push、pull;預設兩者)</summary>
    public List<string>? Modes { get; set; }
    /// <summary>出力一階上升時間常數(s;20–40 s)</summary>
    [JsonPropertyName("riseTime_s")] public double RiseTimeS { get; set; } = 30.0;
    /// <summary>方向變更速率(度/秒);未給時 = repositionSpeed / lineLength</summary>
    [JsonPropertyName("turnRate_degPerSec")] public double? TurnRateDegPerSec { get; set; }
    /// <summary>拖船繞行速度(kn;推估方向變更速率)</summary>
    [JsonPropertyName("repositionSpeed_kn")] public double RepositionSpeedKn { get; set; } = 5.0;
    /// <summary>推頂:自船對水速度 ≤ 此值時效能 100 %(kn)</summary>
    [JsonPropertyName("pushFullUpTo_kn")] public double PushFullUpToKn { get; set; } = 3.0;
    /// <summary>推頂:效能降到 50 % 的自船速度(kn)</summary>
    [JsonPropertyName("pushHalfAt_kn")] public double PushHalfAtKn { get; set; } = 5.0;
    /// <summary>拖拉(拖纜):效能 100 % 的速度上限(kn)</summary>
    [JsonPropertyName("pullFullUpTo_kn")] public double PullFullUpToKn { get; set; } = 4.0;
    /// <summary>拖拉:效能 50 % 的速度(kn)</summary>
    [JsonPropertyName("pullHalfAt_kn")] public double PullHalfAtKn { get; set; } = 7.0;
    /// <summary>推 ↔ 拉換位時間(s;期間無出力)</summary>
    [JsonPropertyName("modeChange_s")] public double ModeChangeS { get; set; } = 60.0;
    /// <summary>free(已解纜)→ push/pull 的帶纜/靠上時間(s)</summary>
    [JsonPropertyName("connect_s")] public double ConnectS { get; set; } = 90.0;
    public ScenarioTugInitial? Initial { get; set; }
}

public sealed class ScenarioTugInitial
{
    /// <summary>standby(預設,已帶纜/靠妥待命)| free | push | pull</summary>
    public string? Mode { get; set; }
    /// <summary>力方向(相對船艏,度;0 = 向前、90 = 向右舷)</summary>
    [JsonPropertyName("direction_deg")] public double? DirectionDeg { get; set; }
    /// <summary>出力(0–100 %)</summary>
    [JsonPropertyName("power_pct")] public double? PowerPct { get; set; }
}

/// <summary>情境錨設定(初始已錨泊、抓著係數倍率、走錨速率)。</summary>
public sealed class ScenarioAnchors
{
    public ScenarioAnchorInitial? Port { get; set; }
    public ScenarioAnchorInitial? Starboard { get; set; }
    /// <summary>抓地力倍率(教官調整底質品質;預設 1)</summary>
    public double HoldingFactor { get; set; } = 1.0;
    /// <summary>走錨速率上限(m/s;預設 0.5)</summary>
    [JsonPropertyName("dragSpeed_mps")] public double DragSpeedMps { get; set; } = 0.5;
}

public sealed class ScenarioAnchorInitial
{
    /// <summary>初始已送出鏈長(m;> 0 表示開始時已錨泊、剎車握持)</summary>
    [JsonPropertyName("length_m")] public double? LengthM { get; set; }
    /// <summary>同上,以節計(一節 = particulars shackleLength_m,預設 27.5 m)</summary>
    [JsonPropertyName("length_shackles")] public double? LengthShackles { get; set; }
    /// <summary>錨相對錨鏈孔的真方位(度;預設初始航向,即錨在船艏前方)</summary>
    [JsonPropertyName("bearing_deg")] public double? BearingDeg { get; set; }
}

/// <summary>
/// 泊位參照與繫纜(情境 berth):<c>file</c>(data/areas/*.json)+ <c>id</c>(泊位 id)於載入時解析並內嵌到 <c>definition</c>
/// (紀錄 header 含完整定義,重播不需讀檔);或直接內嵌 <c>definition</c>。
/// </summary>
public sealed class ScenarioBerth
{
    public string? File { get; set; }
    public string? Id { get; set; }
    public BerthDefinition? Definition { get; set; }
    /// <summary>自船繫纜(導纜孔、預設繫船柱、MBL、初始狀態)</summary>
    public List<ScenarioMooringLine> Lines { get; set; } = new();
}

/// <summary>泊位定義(data/areas/*.json berths[] 的元素;單位 SI,反力 kN、能量 kN·m)。</summary>
public sealed class BerthDefinition
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Source { get; set; }
    /// <summary>碼頭面折線({lat,lon} 或 {x,y});水域在 waterSide 側</summary>
    public List<ScenarioPosition> Quay { get; set; } = new();
    /// <summary>沿折線方向看,水域在 right(預設)或 left</summary>
    public string WaterSide { get; set; } = "right";
    /// <summary>船體–碼頭硬接觸的懲罰剛度(kN/m;預設 20000)</summary>
    [JsonPropertyName("quayStiffness_kNpm")] public double? QuayStiffnessKNpm { get; set; }
    public Dictionary<string, FenderTypeDefinition> FenderTypes { get; set; } = new(StringComparer.Ordinal);
    public List<FenderDefinition> Fenders { get; set; } = new();
    public List<BollardDefinition> Bollards { get; set; } = new();
}

/// <summary>碰墊型式:非線性壓縮–反力曲線(壓縮 % 對 反力 %,以額定反力與高度縮放)。</summary>
public sealed class FenderTypeDefinition
{
    /// <summary>碰墊高度(未壓縮突出量,m)</summary>
    [JsonPropertyName("height_m")] public double HeightM { get; set; } = 1.0;
    /// <summary>沿碼頭方向的接觸寬度(m;面板寬)</summary>
    [JsonPropertyName("width_m")] public double WidthM { get; set; } = 2.0;
    /// <summary>額定反力(kN)</summary>
    [JsonPropertyName("ratedReaction_kN")] public double RatedReactionKN { get; set; } = 1000.0;
    /// <summary>額定壓縮(% 高度)</summary>
    [JsonPropertyName("ratedDeflection_pct")] public double RatedDeflectionPct { get; set; } = 72.0;
    /// <summary>額定吸收能量(kN·m;未給時由曲線積分到額定壓縮)</summary>
    [JsonPropertyName("ratedEnergy_kNm")] public double? RatedEnergyKNm { get; set; }
    /// <summary>[壓縮 %, 反力 %] 點列(0,0 起,遞增);未給時為線性 0 → 100 %</summary>
    public List<double[]>? Curve { get; set; }
    /// <summary>黏性阻尼(kN·s/m;預設 0,能量以靜態曲線計)</summary>
    [JsonPropertyName("damping_kNs_per_m")] public double DampingKNsPerM { get; set; }
    /// <summary>面板摩擦係數(預設 0.2)</summary>
    public double Friction { get; set; } = 0.2;
}

public sealed class FenderDefinition
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>沿碼頭折線自起點的距離(m)</summary>
    [JsonPropertyName("chainage_m")] public double ChainageM { get; set; }
}

public sealed class BollardDefinition
{
    public string Id { get; set; } = "";
    [JsonPropertyName("chainage_m")] public double ChainageM { get; set; }
    /// <summary>碼頭面向陸側的退縮距離(m)</summary>
    [JsonPropertyName("setback_m")] public double SetbackM { get; set; } = 2.0;
    /// <summary>安全工作負荷(t;僅顯示)</summary>
    [JsonPropertyName("swl_t")] public double? SwlT { get; set; }
}

/// <summary>自船繫纜(情境 berth.lines[])。</summary>
public sealed class ScenarioMooringLine
{
    public string Id { get; set; } = "";
    /// <summary>導纜孔(具名點或 {x, y})</summary>
    public ScenarioBodyPoint? Fairlead { get; set; }
    /// <summary>預設繫船柱 id</summary>
    public string? Bollard { get; set; }
    /// <summary>最小斷裂負荷(t)</summary>
    [JsonPropertyName("mbl_t")] public double MblT { get; set; } = 50.0;
    /// <summary>斷裂時應變(預設 0.18:聚酯/聚丙烯混紡)</summary>
    public double BreakStrain { get; set; } = 0.18;
    /// <summary>張力–應變指數 T = MBL·(ε/ε_b)^n(預設 1.5)</summary>
    public double Exponent { get; set; } = 1.5;
    /// <summary>可用纜長(m)</summary>
    [JsonPropertyName("length_m")] public double LengthM { get; set; } = 150.0;
    /// <summary>絞纜速率(m/min)</summary>
    [JsonPropertyName("heaveRate_mPerMin")] public double HeaveRateMPerMin { get; set; } = 15.0;
    /// <summary>絞纜機拉力上限(t)</summary>
    [JsonPropertyName("winchPull_t")] public double WinchPullT { get; set; } = 10.0;
    /// <summary>makeFast 預張力(t)</summary>
    [JsonPropertyName("pretension_t")] public double PretensionT { get; set; } = 2.0;
    /// <summary>初始狀態:stowed(預設)| fast</summary>
    public string? Initial { get; set; }
}

/// <summary>泊位檔讀取(ScenarioLoader 呼叫)與結構檢查。</summary>
public static class ScenarioExternalForces
{
    public static readonly string[] TugModes = { "push", "pull" };
    public static readonly string[] TugControlModes = { "push", "pull", "standby", "free" };
    public static readonly string[] SeabedTypes = { "mud", "sand", "clay", "rock" };

    /// <summary>
    /// 解析 berth.file + id 為內嵌 definition(相對路徑依序找專案根目錄、情境檔目錄);檔案頂層 fenderTypes 與泊位 fenderTypes 合併(泊位優先)。
    /// 已有 definition 時不讀檔。
    /// </summary>
    public static void ResolveBerth(Scenario scenario, string? baseDirectory)
    {
        if (scenario.Berth is not { } b || b.Definition is not null || string.IsNullOrWhiteSpace(b.File)) return;
        var candidates = new List<string>();
        if (Path.IsPathRooted(b.File)) candidates.Add(b.File);
        else
        {
            if (RepositoryPaths.TryFindRoot() is { } root) candidates.Add(Path.Combine(root, b.File));
            if (baseDirectory is not null) candidates.Add(Path.Combine(baseDirectory, b.File));
            candidates.Add(b.File);
        }
        var path = candidates.FirstOrDefault(File.Exists) ?? throw new InvalidDataException($"berth.file 找不到:{b.File}");
        b.Definition = LoadBerth(path, b.Id);
    }

    /// <summary>讀取泊位檔中指定 id(省略 = 第一個)的泊位定義。</summary>
    public static BerthDefinition LoadBerth(string path, string? id)
    {
        var root = JsonNode.Parse(System.IO.File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException($"泊位檔格式錯誤:{path}");
        if (root["berths"] is not JsonArray berths || berths.Count == 0) throw new InvalidDataException($"泊位檔沒有 berths[]:{path}");
        var node = id is null ? berths[0] : berths.FirstOrDefault(x => x?["id"]?.GetValue<string>() == id)
                   ?? throw new InvalidDataException($"泊位檔 {Path.GetFileName(path)} 沒有泊位 {id}");
        var def = ContractJson.Deserialize<BerthDefinition>(node!.ToJsonString()) ?? throw new InvalidDataException($"無法解析泊位 {id}");
        if (root["fenderTypes"] is JsonObject shared)
        {
            var types = ContractJson.Deserialize<Dictionary<string, FenderTypeDefinition>>(shared.ToJsonString()) ?? new();
            foreach (var (k, v) in types) def.FenderTypes.TryAdd(k, v);
        }
        if (def.Source is null && root["source"] is JsonValue src && src.TryGetValue<string>(out var s)) def.Source = s;
        return def;
    }

    /// <summary>拖船/錨/泊位區段的結構檢查(Scenario.StructuralIssues 呼叫)。</summary>
    public static void CheckStructure(Scenario sc, Action<string, string> error)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sc.Tugs is { } tugs)
            for (var i = 0; i < tugs.Count; i++)
            {
                var t = tugs[i];
                var path = $"tugs[{i}]";
                if (string.IsNullOrWhiteSpace(t.Id)) error(path, "拖船缺少 id");
                else if (!ids.Add(t.Id)) error(path, $"拖船 id 重複:{t.Id}");
                if (!(t.BollardPullT > 0)) error(path, "bollardPull_t 必須為正");
                if (!(t.RiseTimeS > 0)) error(path, "riseTime_s 必須為正");
                if (!(t.LineLengthM > 0)) error(path, "lineLength_m 必須為正");
                if (t.TurnRateDegPerSec is <= 0) error(path, "turnRate_degPerSec 必須為正");
                if (t.Attach is { IsValid: false }) error($"{path}.attach", $"連接點須為 {string.Join("|", ScenarioBodyPoint.Names)} 或 {{x, y}}");
                foreach (var m in t.Modes ?? new List<string>())
                    if (!TugModes.Contains(m)) error($"{path}.modes", $"未知的拖船模式:{m}");
                if (t.Initial?.Mode is { } im && !TugControlModes.Contains(im)) error($"{path}.initial.mode", $"未知的拖船模式:{im}");
                // 初始 push/pull 須在 modes 允許清單內(modes 省略 = 兩者皆可),與 tugControl 的 tugModeUnavailable 一致
                else if (t.Initial?.Mode is "push" or "pull" && t.Modes is { Count: > 0 } allowed && !allowed.Contains(t.Initial.Mode))
                    error($"{path}.initial.mode", $"初始模式 {t.Initial.Mode} 不在 modes [{string.Join(", ", allowed)}] 內");
                if (t.Initial?.PowerPct is < 0 or > 100) error($"{path}.initial.power_pct", "power_pct 須在 0–100");
                if (!(t.PushHalfAtKn > t.PushFullUpToKn) || !(t.PullHalfAtKn > t.PullFullUpToKn)) error(path, "效能遞減速度須 halfAt_kn > fullUpTo_kn");
            }
        if (sc.Environment.Seabed is { } bed && !SeabedTypes.Contains(bed)) error("environment.seabed", $"seabed 須為 {string.Join("|", SeabedTypes)}:{bed}");
        if (sc.Anchors is { } an)
        {
            foreach (var (side, a) in new[] { ("port", an.Port), ("starboard", an.Starboard) })
                if (a is not null && (a.LengthM is < 0 || a.LengthShackles is < 0)) error($"anchors.{side}", "鏈長不可為負");
            if (!(an.HoldingFactor > 0)) error("anchors.holdingFactor", "holdingFactor 必須為正");
            if (!(an.DragSpeedMps > 0)) error("anchors.dragSpeed_mps", "dragSpeed_mps 必須為正");
        }
        if (sc.Berth is not { } b) return;
        if (b.Definition is not { } d)
        {
            error("berth", "berth 需要 definition,或 file(+ id)由載入時解析");
            return;
        }
        if (d.Quay.Count < 2) error("berth.definition.quay", "碼頭線至少需要 2 點");
        foreach (var p in d.Quay)
            if (!((p.Lat is not null && p.Lon is not null) || (p.X is not null && p.Y is not null))) { error("berth.definition.quay", "碼頭線頂點需為 {lat, lon} 或 {x, y}"); break; }
        if (d.WaterSide is not ("left" or "right")) error("berth.definition.waterSide", "waterSide 須為 left | right");
        foreach (var (k, ft) in d.FenderTypes)
        {
            if (!(ft.HeightM > 0) || !(ft.RatedReactionKN > 0) || !(ft.WidthM > 0) || !(ft.RatedDeflectionPct > 0)) error($"berth.definition.fenderTypes.{k}", "碰墊 height_m/width_m/ratedReaction_kN/ratedDeflection_pct 必須為正");
            if (ft.Curve is { } c)
            {
                if (c.Count < 2 || c.Any(pt => pt.Length != 2)) error($"berth.definition.fenderTypes.{k}.curve", "曲線須為至少 2 個 [壓縮 %, 反力 %] 點");
                else
                {
                    if (c[0][0] != 0 || c[0][1] != 0) error($"berth.definition.fenderTypes.{k}.curve", "曲線須自 [0, 0] 起");
                    for (var i = 1; i < c.Count; i++)
                        if (!(c[i][0] > c[i - 1][0]) || c[i][1] < 0) { error($"berth.definition.fenderTypes.{k}.curve", "壓縮 % 須遞增、反力 % 不可為負"); break; }
                }
            }
        }
        var fids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in d.Fenders)
        {
            if (!fids.Add(f.Id)) error("berth.definition.fenders", $"碰墊 id 重複:{f.Id}");
            if (!d.FenderTypes.ContainsKey(f.Type)) error("berth.definition.fenders", $"碰墊 {f.Id} 的型式不存在:{f.Type}");
        }
        var bids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bo in d.Bollards)
            if (!bids.Add(bo.Id)) error("berth.definition.bollards", $"繫船柱 id 重複:{bo.Id}");
        var lids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < b.Lines.Count; i++)
        {
            var l = b.Lines[i];
            var path = $"berth.lines[{i}]";
            if (string.IsNullOrWhiteSpace(l.Id)) error(path, "繫纜缺少 id");
            else if (!lids.Add(l.Id)) error(path, $"繫纜 id 重複:{l.Id}");
            if (l.Fairlead is not { IsValid: true }) error($"{path}.fairlead", "繫纜需要導纜孔(具名點或 {x, y})");
            if (l.Bollard is { } bid && !bids.Contains(bid)) error($"{path}.bollard", $"繫船柱不存在:{bid}");
            if (!(l.MblT > 0) || !(l.BreakStrain > 0) || !(l.Exponent > 0) || !(l.LengthM > 0)) error(path, "mbl_t/breakStrain/exponent/length_m 必須為正");
            if (l.Initial is { } init && init is not ("stowed" or "fast")) error($"{path}.initial", "initial 須為 stowed | fast");
            if (l.Initial == "fast" && l.Bollard is null) error($"{path}.initial", "initial fast 需要 bollard");
        }
    }
}
