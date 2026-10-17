using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Parameters;

/// <summary>
/// 情境節點樹中的 <c>${params:&lt;key&gt;}</c> 引用解析(情境引擎設計第 2.3 節):整個字串必須恰為引用(不支援內嵌/運算);
/// 未知鍵、單位後綴不符、引用於不允許的位置(id、name、seed…)皆為 <see cref="InvalidDataException"/>。
/// </summary>
public static class ParameterResolver
{
    public static readonly Regex RefPattern = new(@"^\$\{params:([A-Za-z0-9_.\-]+)\}$", RegexOptions.Compiled);

    /// <summary>不得引用參數的欄位名(路徑最後一段)。</summary>
    private static readonly HashSet<string> ForbiddenLeaves = new(StringComparer.Ordinal)
    {
        "id", "name", "seed", "loading", "kind", "type", "rule", "metric", "mode", "shipType", "role", "channel", "parameterSet",
    };

    /// <summary>欄位名 → 參數 unit 的額外對應(無單位後綴的慣用欄位)。</summary>
    private static readonly Dictionary<string, string> LeafUnits = new(StringComparer.Ordinal)
    {
        ["fractionOfDraft"] = "fractionOfDraft", ["metres"] = "m", ["passMark"] = "score",
    };

    /// <summary>節點樹是否含任何參數引用。</summary>
    public static bool HasReferences(JsonNode? node) => Walk(node, "").Any();

    /// <summary>
    /// 就地替換所有引用;回傳解析清單。<paramref name="set"/> 為 null 且有引用時丟出例外(提示 --parameters)。
    /// </summary>
    public static List<ParameterRef> Resolve(JsonNode root, ParameterSet? set)
    {
        var refs = Walk(root, "").ToList();
        var resolved = new List<ParameterRef>(refs.Count);
        if (refs.Count == 0) return resolved;
        if (set is null)
            throw new InvalidDataException($"情境引用參數 {refs[0].Key}({refs[0].Path})但未提供參數集;請以 --parameters <path> 指定或在 assessment.parameterSet 設定");
        foreach (var (path, key) in refs)
        {
            var leaf = LeafName(path);
            if (ForbiddenLeaves.Contains(leaf)) throw new InvalidDataException($"欄位 {path} 不允許引用參數");
            if (!set.TryGet(key, out var entry)) throw new InvalidDataException($"參數集 {set.Id} {set.Version} 無鍵 {key}(引用於 {path})");
            var expected = ParameterSet.UnitOfSuffix(leaf) ?? (LeafUnits.TryGetValue(leaf, out var u) ? u : null);
            if (expected is not null && expected != entry.Unit)
                throw new InvalidDataException($"欄位 {path} 的單位後綴({expected})與參數 {key} 的 unit({entry.Unit})不符");
            if (!set.Sources.TryGetValue(entry.Source, out var src))
                throw new InvalidDataException($"參數 {key} 的來源文件 {entry.Source} 未在 sources 定義");
            YamlJson.SetByPath(root, path, JsonNode.Parse(entry.Value.GetRawText()));
            resolved.Add(new ParameterRef
            {
                Path = path, Key = key, Value = entry.Value, Unit = entry.Unit, Source = entry.Source,
                Revision = src.Revision, Clause = entry.Clause,
            });
        }
        return resolved;
    }

    /// <summary>
    /// 以新參數集重新代入既有解析清單的欄位(講評站「重新評分」:物理不受影響,只重算評估;情境引擎設計第 7.4 節)。
    /// 回傳新的解析清單;缺鍵為錯誤。
    /// </summary>
    public static List<ParameterRef> Rescore(JsonNode scenarioRoot, IReadOnlyList<ParameterRef> previous, ParameterSet set)
    {
        var resolved = new List<ParameterRef>(previous.Count);
        foreach (var r in previous)
        {
            if (!set.TryGet(r.Key, out var entry)) throw new InvalidDataException($"參數集 {set.Id} {set.Version} 無鍵 {r.Key}(引用於 {r.Path})");
            if (entry.Unit != r.Unit) throw new InvalidDataException($"參數 {r.Key} 的單位由 {r.Unit} 變為 {entry.Unit},無法重新評分");
            if (!YamlJson.SetByPath(scenarioRoot, r.Path, JsonNode.Parse(entry.Value.GetRawText())))
                throw new InvalidDataException($"情境中找不到欄位 {r.Path}");
            set.Sources.TryGetValue(entry.Source, out var src);
            resolved.Add(r with { Value = entry.Value, Source = entry.Source, Revision = src?.Revision, Clause = entry.Clause });
        }
        return resolved;
    }

    private static string LeafName(string path)
    {
        var i = path.LastIndexOf('.');
        var leaf = i < 0 ? path : path[(i + 1)..];
        var b = leaf.IndexOf('[');
        return b < 0 ? leaf : leaf[..b];
    }

    private static IEnumerable<(string Path, string Key)> Walk(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    foreach (var r in Walk(v, path.Length == 0 ? k : $"{path}.{k}")) yield return r;
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    foreach (var r in Walk(a[i], $"{path}[{i}]")) yield return r;
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && RefPattern.Match(s) is { Success: true } m:
                yield return (path, m.Groups[1].Value);
                break;
        }
    }
}
