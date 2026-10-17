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

    /// <summary>最近一次 Load/FromYaml 的參數解析清單(無引用時為空)。</summary>
    public static IReadOnlyList<ParameterRef> LastResolvedParameters { get; private set; } = Array.Empty<ParameterRef>();

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
        var set = parameters;
        if (set is null && ParameterResolver.HasReferences(root))
            set = TryAutoLoadParameterSet(root, baseDirectory);
        var resolved = ParameterResolver.Resolve(root, set);
        LastResolvedParameters = resolved;
        var scenario = ContractJson.Deserialize<Scenario>(root.ToJsonString()) ?? throw new InvalidDataException("無法解析情境");
        if (set is not null && resolved.Count > 0) scenario.ParameterSetInfo = set.ToInfo(resolved);
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
