using System.Text.Json;

namespace SimosaBRM.Visual.Shared.Area;

/// <summary>
/// 情境 → 區域檔對照(data/visual/scenario-areas.json):state.scenario.id 的前綴(例如 E05)對應要載入的 data/areas/*.json 清單,
/// 視景啟動時若未給 --area 便依此載入;同時提供 --area=id 時由 id 找檔案。
/// 泊位後備(審查發現 utc-mooring):state.berth 不帶泊位檔路徑,對照表漏列/錯列時繫纜找不到繫船柱;
/// <see cref="AreasForScenario(string?, string?)"/> 會依 state.berth.id 掃描 data/areas/*.json 的 berths[].id 補上泊位所在的區域檔。
/// </summary>
public sealed class AreaCatalogue
{
    private readonly Dictionary<string, List<string>> _byScenario = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _projectRoot;
    private List<(string Path, string AreaId, HashSet<string> BerthIds)>? _berthIndex;

    public AreaCatalogue(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public static AreaCatalogue Load(string projectRoot)
    {
        var cat = new AreaCatalogue(projectRoot);
        var path = Path.Combine(projectRoot, "data", "visual", "scenario-areas.json");
        if (!File.Exists(path)) return cat;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.TryGetProperty("scenarios", out var sc) && sc.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in sc.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                var list = prop.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                cat._byScenario[prop.Name] = list;
            }
        }
        return cat;
    }

    public void Add(string scenarioPrefix, params string[] areaIds) => _byScenario[scenarioPrefix] = areaIds.ToList();

    /// <summary>依情境 id(例如 E05_pilot_traffic)找區域 id 清單:取最長相符的前綴鍵。</summary>
    public IReadOnlyList<string> AreasForScenario(string? scenarioId)
    {
        if (string.IsNullOrEmpty(scenarioId)) return Array.Empty<string>();
        string? bestKey = null;
        foreach (var k in _byScenario.Keys)
            if (scenarioId.StartsWith(k, StringComparison.OrdinalIgnoreCase) && (bestKey is null || k.Length > bestKey.Length)) bestKey = k;
        return bestKey is null ? Array.Empty<string>() : _byScenario[bestKey];
    }

    /// <summary>
    /// 依情境 id 與 state.berth.id 找要載入的區域:對照表清單,若其中沒有任何區域檔含該泊位,再補上 <see cref="AreaIdsWithBerth"/> 找到的區域
    /// (情境 berth.file 指向的區域檔);berthId 為 null 或 data/areas 無此泊位(例如煙霧示意泊位)時與 <see cref="AreasForScenario(string?)"/> 相同。
    /// </summary>
    public IReadOnlyList<string> AreasForScenario(string? scenarioId, string? berthId) => WithBerthArea(AreasForScenario(scenarioId), berthId);

    /// <summary>給定區域清單(id 或路徑;例如 --area),若無任何一個含泊位 berthId,附加泊位所在的區域 id;否則原樣。</summary>
    public IReadOnlyList<string> WithBerthArea(IReadOnlyList<string> areaIds, string? berthId)
    {
        if (string.IsNullOrEmpty(berthId)) return areaIds;
        var index = BerthIndex();
        var listedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in areaIds)
        {
            try { listedPaths.Add(Path.GetFullPath(ResolvePath(id))); }
            catch (FileNotFoundException) { }
        }
        foreach (var entry in index)
            if (listedPaths.Contains(entry.Path) && entry.BerthIds.Contains(berthId)) return areaIds;
        var extra = index.Where(e => e.BerthIds.Contains(berthId) && !listedPaths.Contains(e.Path)).Select(e => e.AreaId).ToList();
        if (extra.Count == 0) return areaIds;
        return areaIds.Concat(extra).ToList();
    }

    /// <summary>掃描 data/areas/*.json,回傳 berths[].id 含 berthId(不分大小寫)的區域 id(檔名順序;通常 0 或 1 個)。</summary>
    public IReadOnlyList<string> AreaIdsWithBerth(string? berthId)
    {
        if (string.IsNullOrEmpty(berthId)) return Array.Empty<string>();
        return BerthIndex().Where(e => e.BerthIds.Contains(berthId)).Select(e => e.AreaId).ToList();
    }

    /// <summary>區域檔 → 泊位 id 索引(只讀 id/berths[].id,不投影幾何;第一次用到時建立,壞檔略過)。</summary>
    private List<(string Path, string AreaId, HashSet<string> BerthIds)> BerthIndex()
    {
        if (_berthIndex is not null) return _berthIndex;
        var list = new List<(string, string, HashSet<string>)>();
        var dir = Path.Combine(_projectRoot, "data", "areas");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("berths", out var berths) || berths.ValueKind != JsonValueKind.Array) continue;
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in berths.EnumerateArray())
                        if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s)
                            ids.Add(s);
                    if (ids.Count == 0) continue;
                    var areaId = root.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.String && aid.GetString() is { Length: > 0 } a ? a : Path.GetFileNameWithoutExtension(file);
                    list.Add((Path.GetFullPath(file), areaId, ids));
                }
                catch (Exception) { /* 壞檔不影響其他區域 */ }
            }
        }
        _berthIndex = list;
        return list;
    }

    /// <summary>區域 id 或路徑 → 實際檔案路徑(data/areas/&lt;id&gt;.json;已是存在的路徑則原樣)。</summary>
    public string ResolvePath(string idOrPath)
    {
        if (File.Exists(idOrPath)) return Path.GetFullPath(idOrPath);
        var rel = Path.Combine(_projectRoot, idOrPath);
        if (File.Exists(rel)) return rel;
        var byId = Path.Combine(_projectRoot, "data", "areas", idOrPath + ".json");
        if (File.Exists(byId)) return byId;
        throw new FileNotFoundException($"找不到區域資料:{idOrPath}(data/areas/<id>.json)");
    }
}
