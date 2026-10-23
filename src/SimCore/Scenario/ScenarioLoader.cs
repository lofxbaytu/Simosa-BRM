using System.Text.Json.Nodes;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// YAML 情境讀取:YAML → 通用節點樹 → <c>${params:…}</c> 替換(情境引擎設計第 2.3 節)→ JSON → <see cref="Scenario"/>(System.Text.Json,
/// 未知鍵忽略以便日後擴充)。有參數引用而未提供參數集時,依 <c>assessment.parameterSet</c>(相對於專案根目錄或情境檔目錄)自動載入;
/// 仍無則丟出例外並提示 --parameters。
/// </summary>
public static class ScenarioLoader
{
    public const string DefaultParameterSetPath = "data/parameters/company-procedures.yaml";

    /// <summary>
    /// 本執行緒最近一次 Load/FromYaml 的參數解析清單(無引用時為空)。以執行緒區域儲存:平行執行的測試(或多執行緒載入)不會讀到別人的結果。
    /// </summary>
    public static IReadOnlyList<ParameterRef> LastResolvedParameters
    {
        get => _lastResolved ?? Array.Empty<ParameterRef>();
        private set => _lastResolved = value;
    }

    [ThreadStatic] private static IReadOnlyList<ParameterRef>? _lastResolved;

    public static Scenario Load(string path, ParameterSet? parameters = null)
    {
        var yaml = File.ReadAllText(path);
        var scenario = FromYaml(yaml, parameters, Path.GetDirectoryName(Path.GetFullPath(path)));
        if (string.IsNullOrWhiteSpace(scenario.Id)) scenario.Id = Path.GetFileNameWithoutExtension(path);
        return scenario.Validate();
    }

    /// <summary>自 YAML 文字載入(未呼叫 Validate);<paramref name="baseDirectory"/> 供自動尋找 assessment.parameterSet。</summary>
    public static Scenario FromYaml(string yaml, ParameterSet? parameters = null, string? baseDirectory = null)
    {
        var node = YamlJson.ToJsonNode(yaml) ?? throw new InvalidDataException("情境 YAML 為空");
        if (node is not JsonObject root) throw new InvalidDataException("情境 YAML 須為物件");
        return FromNode(root, parameters, baseDirectory);
    }

    /// <summary>
    /// 內嵌情境物件(<c>loadScenario</c> 指令的 <c>args.scenario</c>,結構與 YAML 相同;規劃書第 9.1 節靠離泊預演自動組題):
    /// 走與 YAML 相同的「節點樹 → 參數替換 → JSON → Scenario」路徑(參數引用 <c>"${params:…}"</c>、泊位/岸壁檔參照照常解析),
    /// id 空白時以 <paramref name="defaultId"/> 代入(紀錄用名稱),最後 Validate。<paramref name="baseDirectory"/> 預設為專案的 data/scenarios
    /// (泊位檔 <c>data/areas/…</c> 相對專案根目錄,與檔案載入相同)。
    /// </summary>
    public static Scenario FromJsonElement(System.Text.Json.JsonElement element, ParameterSet? parameters = null, string? baseDirectory = null, string? defaultId = null)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("內嵌情境須為物件");
        if (JsonNode.Parse(element.GetRawText()) is not JsonObject root) throw new InvalidDataException("內嵌情境須為物件");
        baseDirectory ??= RepositoryPaths.TryFindRoot() is { } rootDir ? Path.Combine(rootDir, "data", "scenarios") : null;
        var scenario = FromNode(root, parameters, baseDirectory);
        if (string.IsNullOrWhiteSpace(scenario.Id)) scenario.Id = string.IsNullOrWhiteSpace(defaultId) ? "inline" : defaultId!.Trim();
        return scenario.Validate();
    }

    /// <summary>通用節點樹(已是 JSON 物件)→ 參數替換 → <see cref="Scenario"/>(未呼叫 Validate);YAML 與內嵌物件共用。</summary>
    public static Scenario FromNode(JsonObject root, ParameterSet? parameters = null, string? baseDirectory = null)
    {
        var set = parameters;
        if (set is null && ParameterResolver.HasReferences(root))
            set = TryAutoLoadParameterSet(root, baseDirectory);
        var resolved = ParameterResolver.Resolve(root, set);
        LastResolvedParameters = resolved;
        var scenario = ContractJson.Deserialize<Scenario>(root.ToJsonString()) ?? throw new InvalidDataException("無法解析情境");
        if (set is not null && resolved.Count > 0) scenario.ParameterSetInfo = set.ToInfo(resolved);
        // 泊位參照(berth.file + id)內嵌為 definition,紀錄 header 因此含完整泊位定義(設計文件 physics-tugs-anchors-mooring.md 第 4.1 節)
        ScenarioExternalForces.ResolveBerth(scenario, baseDirectory);
        // 岸壁參照(banks.file + ids)同樣內嵌為 definitions(設計文件 physics-bank-effect.md 第 2 節)
        ScenarioBankLoader.ResolveBanks(scenario, baseDirectory);
        return scenario;
    }

    private static ParameterSet? TryAutoLoadParameterSet(JsonObject root, string? baseDirectory)
    {
        var declared = root["assessment"] is JsonObject a && a["parameterSet"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : DefaultParameterSetPath;
        var candidates = new List<string>();
        if (Path.IsPathRooted(declared)) candidates.Add(declared);
        else
        {
            if (RepositoryPaths.TryFindRoot() is { } rootDir) candidates.Add(Path.Combine(rootDir, declared));
            if (baseDirectory is not null) candidates.Add(Path.Combine(baseDirectory, declared));
            candidates.Add(declared);
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return ParameterSetLoader.Load(c);
        return null;
    }

    /// <summary>情境 → YAML(經 JSON;鍵名與 schema 相同,供紀錄/匯出)。</summary>
    public static string ToYaml(Scenario scenario) => YamlJson.JsonToYaml(scenario.ToJson());
}
