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

    /// <summary>
    /// 與 <see cref="Load"/> 相同的檔案載入,但不丟例外、把全部問題收成清單(主控程序 loadScenario 指令用;規劃書第 9.1 節自動檢查):
    /// 讀檔/YAML/參數集/泊位參照的例外成為單一 error 項(path "scenario"),解析成功後以 <see cref="Scenario.LoadIssues"/> 列出全部結構 error。
    /// </summary>
    public static ScenarioLoadResult TryLoad(string path, ParameterSet? parameters = null)
        => Gate(() =>
        {
            var yaml = File.ReadAllText(path);
            var scenario = FromYaml(yaml, parameters, Path.GetDirectoryName(Path.GetFullPath(path)));
            if (string.IsNullOrWhiteSpace(scenario.Id)) scenario.Id = Path.GetFileNameWithoutExtension(path);
            return scenario;
        }, Path.GetFileNameWithoutExtension(path), path);

    /// <summary>與 <see cref="FromJsonElement"/> 相同的內嵌情境載入,但不丟例外、把全部問題收成清單(語意同 <see cref="TryLoad"/>)。</summary>
    public static ScenarioLoadResult TryFromJsonElement(System.Text.Json.JsonElement element, ParameterSet? parameters = null, string? baseDirectory = null, string? defaultId = null)
        => Gate(() =>
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("內嵌情境須為物件");
            if (JsonNode.Parse(element.GetRawText()) is not JsonObject root) throw new InvalidDataException("內嵌情境須為物件");
            baseDirectory ??= RepositoryPaths.TryFindRoot() is { } rootDir ? Path.Combine(rootDir, "data", "scenarios") : null;
            var scenario = FromNode(root, parameters, baseDirectory);
            if (string.IsNullOrWhiteSpace(scenario.Id)) scenario.Id = string.IsNullOrWhiteSpace(defaultId) ? "inline" : defaultId!.Trim();
            return scenario;
        }, string.IsNullOrWhiteSpace(defaultId) ? InlineIdOf(element) ?? "inline" : defaultId!.Trim(), null);

    /// <summary>內嵌情境物件自帶的 id(非物件或無 id 時 null;拒絕回報用)。</summary>
    private static string? InlineIdOf(System.Text.Json.JsonElement element)
        => element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(id.GetString()) ? id.GetString() : null;

    /// <summary>
    /// 載入閘門:解析(未 Validate)→ <see cref="Scenario.LoadIssues"/>;任何例外(檔案不存在、YAML/JSON 格式、參數集、泊位/岸壁參照、船舶 id 不明)
    /// 都成為單一 error 項而不外拋。成功時情境已通過 Validate 的全部檢查(含補齊原點)。
    /// </summary>
    private static ScenarioLoadResult Gate(Func<Scenario> parse, string? fallbackId, string? path)
    {
        Scenario scenario;
        try { scenario = parse(); }
        catch (Exception ex)
        {
            return ScenarioLoadResult.Failure(fallbackId, path, new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "scenario", ex.Message) });
        }
        var issues = scenario.LoadIssues();
        return issues.Count == 0 ? ScenarioLoadResult.Success(scenario, path) : ScenarioLoadResult.Failure(scenario.Id, path, issues);
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

/// <summary>
/// 情境載入結果(<see cref="ScenarioLoader.TryLoad"/> / <see cref="ScenarioLoader.TryFromJsonElement"/>):成功時 <see cref="Scenario"/> 非 null 且已 Validate;
/// 失敗時 <see cref="Issues"/> 為載入例外或結構檢查的全部 error 項,<see cref="ScenarioId"/> 為能辨認的情境 id(檔名、內嵌物件的 id 或 args.id;皆無時 null)。
/// 主控程序以 <see cref="Engine.SimulationEngine.TryApplyScenarioLoad"/> 套用:失敗時引擎狀態不變並發 <c>scenarioLoadRejected</c> 事件回報教官站。
/// </summary>
public sealed class ScenarioLoadResult
{
    private ScenarioLoadResult(Scenario? scenario, string? scenarioId, string? path, IReadOnlyList<ScenarioIssue> issues)
    {
        Scenario = scenario;
        ScenarioId = scenarioId;
        Path = path;
        Issues = issues;
    }

    public Scenario? Scenario { get; }
    public string? ScenarioId { get; }
    /// <summary>檔案載入時的路徑(內嵌情境為 null;拒絕訊息用)</summary>
    public string? Path { get; }
    public IReadOnlyList<ScenarioIssue> Issues { get; }
    public bool Ok => Scenario is not null && Issues.Count == 0;

    public static ScenarioLoadResult Success(Scenario scenario, string? path = null) => new(scenario, scenario.Id, path, Array.Empty<ScenarioIssue>());
    public static ScenarioLoadResult Failure(string? scenarioId, string? path, IEnumerable<ScenarioIssue> issues)
    {
        var list = issues.ToList();
        if (list.Count == 0) list.Add(new ScenarioIssue(ScenarioIssueSeverity.Error, "scenario", "情境載入失敗(未說明原因)"));
        return new ScenarioLoadResult(null, scenarioId, path, list);
    }
}
