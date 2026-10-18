using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Scenario;

// 情境的岸壁/航道邊坡區段 banks(選填;src/Contracts/scenario.schema.json;設計文件 docs/design/physics-bank-effect.md;
// 規劃書第 6.2 節下標 B、R2「受限水域含岸壁與運河效應」)。只有情境有此區段時才計算岸壁效應,未使用者雜湊與舊版逐位元相同。
// Python 參考實作忽略此區段。

/// <summary>
/// 情境岸壁區段:<c>file</c>(data/areas/*.json)+ <c>ids</c> 於載入時解析並內嵌到 <c>definitions</c>(紀錄 header 含完整幾何,重播不讀檔);
/// <c>lines</c> 為情境內直接定義的岸壁(與檔案合併,先 lines 後 definitions)。
/// </summary>
public sealed class ScenarioBanks
{
    /// <summary>岸壁檔(相對專案根目錄或情境檔目錄),例如 data/areas/mailiao-channel-banks.json</summary>
    public string? File { get; set; }
    /// <summary>岸壁檔 banks[].id(省略 = 全部)</summary>
    public List<string>? Ids { get; set; }
    /// <summary>情境內直接定義的岸壁</summary>
    public List<BankDefinition> Lines { get; set; } = new();
    /// <summary>載入時由 file 內嵌的岸壁定義(已有時不再讀檔)</summary>
    public List<BankDefinition>? Definitions { get; set; }
    /// <summary>岸壁效應整體倍率(預設 1;0 = 只顯示距離不施力;教官調整或日後以實船經驗校正)</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>計算用的全部岸壁(lines 在前、definitions 在後)。</summary>
    [JsonIgnore] public IEnumerable<BankDefinition> All => Lines.Concat(Definitions ?? Enumerable.Empty<BankDefinition>());
}

/// <summary>
/// 一段岸壁(折線):垂直岸壁(碼頭/護岸,折線為岸壁面)或斜坡岸(浚深航道邊坡/自然坡岸,折線為坡腳 = 航道底邊線)。
/// 沿折線方向看,水域在 <see cref="WaterSide"/> 側。
/// </summary>
public sealed class BankDefinition
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Source { get; set; }
    /// <summary>vertical(預設)| sloped</summary>
    public string Type { get; set; } = "vertical";
    /// <summary>岸壁折線({lat, lon} 或 {x, y})</summary>
    public List<ScenarioPosition> Line { get; set; } = new();
    /// <summary>沿折線方向看,水域在 right(預設)或 left</summary>
    public string WaterSide { get; set; } = "right";
    /// <summary>斜坡岸坡度 1:n 的 n(水平/垂直;sloped 必填,例如 5 表示 1:5)</summary>
    public double? Slope { get; set; }
    /// <summary>岸頂(淺灘)水深(m;0 = 岸頂露出水面,> 0 = 淹沒岸/淺灘);預設 0</summary>
    [JsonPropertyName("topDepth_m")] public double TopDepthM { get; set; }
    /// <summary>岸腳(航道)水深(m;適用水深;省略 = 自船所在水深)</summary>
    [JsonPropertyName("channelDepth_m")] public double? ChannelDepthM { get; set; }

    [JsonIgnore] public bool IsSloped => string.Equals(Type, "sloped", StringComparison.OrdinalIgnoreCase);
}

/// <summary>岸壁檔讀取(ScenarioLoader 呼叫)與結構檢查(Scenario.StructuralIssues 呼叫)。</summary>
public static class ScenarioBankLoader
{
    public static readonly string[] BankTypes = { "vertical", "sloped" };

    /// <summary>
    /// 解析 banks.file(+ ids)為內嵌 definitions(相對路徑依序找專案根目錄、情境檔目錄);已有 definitions 時不讀檔。
    /// </summary>
    public static void ResolveBanks(Scenario scenario, string? baseDirectory)
    {
        if (scenario.Banks is not { } b || b.Definitions is not null || string.IsNullOrWhiteSpace(b.File)) return;
        var candidates = new List<string>();
        if (Path.IsPathRooted(b.File)) candidates.Add(b.File);
        else
        {
            if (RepositoryPaths.TryFindRoot() is { } root) candidates.Add(Path.Combine(root, b.File));
            if (baseDirectory is not null) candidates.Add(Path.Combine(baseDirectory, b.File));
            candidates.Add(b.File);
        }
        var path = candidates.FirstOrDefault(System.IO.File.Exists) ?? throw new InvalidDataException($"banks.file 找不到:{b.File}");
        b.Definitions = LoadBanks(path, b.Ids);
    }

    /// <summary>讀取岸壁檔中指定 id(省略 = 全部)的岸壁;檔案頂層 source 補到未標來源的岸壁。</summary>
    public static List<BankDefinition> LoadBanks(string path, IReadOnlyList<string>? ids)
    {
        var root = JsonNode.Parse(System.IO.File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException($"岸壁檔格式錯誤:{path}");
        if (root["banks"] is not JsonArray banks || banks.Count == 0) throw new InvalidDataException($"岸壁檔沒有 banks[]:{path}");
        var all = banks.Select(n => ContractJson.Deserialize<BankDefinition>(n!.ToJsonString()) ?? throw new InvalidDataException($"無法解析岸壁:{path}")).ToList();
        var src = root["source"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
        foreach (var d in all) d.Source ??= src;
        if (ids is null || ids.Count == 0) return all;
        var picked = new List<BankDefinition>();
        foreach (var id in ids)
            picked.Add(all.FirstOrDefault(d => d.Id == id) ?? throw new InvalidDataException($"岸壁檔 {Path.GetFileName(path)} 沒有岸壁 {id}"));
        return picked;
    }

    /// <summary>banks 區段的結構檢查。</summary>
    public static void CheckStructure(Scenario sc, Action<string, string> error)
    {
        if (sc.Banks is not { } b) return;
        if (!(b.Scale >= 0) || double.IsInfinity(b.Scale)) error("banks.scale", "scale 必須 ≥ 0");
        if (b.Lines.Count == 0 && b.Definitions is null)
        {
            error("banks", string.IsNullOrWhiteSpace(b.File) ? "banks 需要 lines,或 file(+ ids)由載入時解析" : "banks.file 尚未解析(請以 ScenarioLoader 載入)");
            return;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var d in b.All)
        {
            var path = i < b.Lines.Count ? $"banks.lines[{i}]" : $"banks.definitions[{i - b.Lines.Count}]";
            i++;
            if (string.IsNullOrWhiteSpace(d.Id)) error(path, "岸壁缺少 id");
            else if (!ids.Add(d.Id)) error(path, $"岸壁 id 重複:{d.Id}");
            if (d.Line.Count < 2) error($"{path}.line", "岸壁折線至少需要 2 點");
            foreach (var p in d.Line)
                if (!((p.Lat is not null && p.Lon is not null) || (p.X is not null && p.Y is not null))) { error($"{path}.line", "岸壁頂點需為 {lat, lon} 或 {x, y}"); break; }
            if (d.WaterSide is not ("left" or "right")) error($"{path}.waterSide", "waterSide 須為 left | right");
            if (!BankTypes.Contains(d.Type)) error($"{path}.type", $"type 須為 {string.Join(" | ", BankTypes)}:{d.Type}");
            if (d.IsSloped && d.Slope is not > 0) error($"{path}.slope", "斜坡岸需要 slope(1:n 的 n,> 0)");
            if (d.Slope is <= 0) error($"{path}.slope", "slope 必須為正");
            if (!(d.TopDepthM >= 0)) error($"{path}.topDepth_m", "topDepth_m 不可為負");
            if (d.ChannelDepthM is { } cd && !(cd > d.TopDepthM)) error($"{path}.channelDepth_m", "channelDepth_m 必須為正且大於 topDepth_m");
        }
    }
}
