using System.Text.Json;

namespace SimosaBRM.Visual.Shared.Area;

/// <summary>
/// 情境 → 區域檔對照(data/visual/scenario-areas.json):state.scenario.id 的前綴(例如 E05)對應要載入的 data/areas/*.json 清單,
/// 視景啟動時若未給 --area 便依此載入;同時提供 --area=id 時由 id 找檔案。
/// </summary>
public sealed class AreaCatalogue
{
    private readonly Dictionary<string, List<string>> _byScenario = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _projectRoot;

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
