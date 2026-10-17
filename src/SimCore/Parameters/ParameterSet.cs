using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Parameters;

/// <summary>
/// 公司程序參數集(data/parameters/company-procedures.yaml;schema src/Contracts/parameter-set.schema.json;
/// 規劃書第 8.3、9.1 節 R14、情境引擎設計第 2 節)。鍵名 <c>段.群.名_單位後綴</c>,後綴須與 unit 一致。
/// </summary>
public sealed class ParameterSet
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string? EffectiveFrom { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ApprovedOn { get; set; }
    public string? Notes { get; set; }
    public Dictionary<string, ParameterSource> Sources { get; set; } = new();
    public Dictionary<string, ParameterEntry> Parameters { get; set; } = new();
    /// <summary>保留:依船舶 ID 覆寫(第一版不解析)</summary>
    public JsonElement? Overrides { get; set; }

    /// <summary>載入來源路徑(由 Loader 填入;專案內的檔案為相對專案根目錄的路徑,例如 data/parameters/company-procedures.yaml)</summary>
    [JsonIgnore] public string? Path { get; set; }
    /// <summary>檔案位元組的 SHA-256(hex;由 Loader 填入)</summary>
    [JsonIgnore] public string Sha256 { get; set; } = "";

    public bool TryGet(string key, out ParameterEntry entry) => Parameters.TryGetValue(key, out entry!);

    /// <summary>參數引用的摘要(紀錄標頭 header.parameterSet / summary.parameterSet)。</summary>
    public ParameterSetInfo ToInfo(IReadOnlyList<ParameterRef>? resolved = null) => new()
    {
        Id = Id, Version = Version, Path = Path, Sha256 = Sha256, Resolved = resolved ?? Array.Empty<ParameterRef>(),
    };

    public static readonly Regex VersionPattern = new(@"^\d{4}\.\d{1,2}\.\d+$", RegexOptions.Compiled);
    public static readonly string[] Units = { "nm", "m", "s", "min", "kn", "mps", "deg", "degPerMin", "fractionOfDraft", "score", "count", "bool", "text" };

    /// <summary>鍵名/欄位名後綴 → 單位(後綴比對不分大小寫)。</summary>
    public static readonly (string Suffix, string Unit)[] SuffixUnits =
    {
        ("_degPerMin", "degPerMin"), ("_fraction", "fractionOfDraft"), ("_mps", "mps"), ("_min", "min"),
        ("_nm", "nm"), ("_kn", "kn"), ("_deg", "deg"), ("_m", "m"), ("_s", "s"),
    };

    /// <summary>名稱的單位後綴所對應的單位;無後綴時 null。</summary>
    public static string? UnitOfSuffix(string name)
    {
        foreach (var (suffix, unit) in SuffixUnits)
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return unit;
        return null;
    }

    /// <summary>結構檢查(版次格式、單位列舉、來源存在、後綴一致、min/max);錯誤以 InvalidDataException 回報。</summary>
    public ParameterSet Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("參數集缺少 id");
        if (!VersionPattern.IsMatch(Version)) throw new InvalidDataException($"參數集 version 格式須為 YYYY.M.n:{Version}");
        foreach (var (key, src) in Sources)
        {
            if (string.IsNullOrWhiteSpace(src.Title)) throw new InvalidDataException($"sources.{key} 缺少 title");
            if (src.Revision is null) throw new InvalidDataException($"sources.{key} 缺少 revision");
        }
        foreach (var (key, p) in Parameters)
        {
            if (p.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"parameters.{key}.value 須為數值、布林或字串");
            if (!Units.Contains(p.Unit)) throw new InvalidDataException($"parameters.{key}.unit 未知:{p.Unit}");
            if (string.IsNullOrWhiteSpace(p.Source) || !Sources.ContainsKey(p.Source))
                throw new InvalidDataException($"parameters.{key}.source 未在 sources 中定義:{p.Source}");
            var suffixUnit = UnitOfSuffix(key);
            if (suffixUnit is not null && suffixUnit != p.Unit)
                throw new InvalidDataException($"parameters.{key} 的單位後綴({suffixUnit})與 unit({p.Unit})不符");
            if (suffixUnit is null && p.Unit is not ("score" or "count" or "bool" or "text"))
                throw new InvalidDataException($"parameters.{key} 缺少單位後綴(unit {p.Unit} 需以 _{p.Unit} 結尾)");
            if (p.Value.ValueKind == JsonValueKind.Number)
            {
                var v = p.Value.GetDouble();
                if (p.Min is { } min && v < min) throw new InvalidDataException($"parameters.{key}.value {v} 低於 min {min}");
                if (p.Max is { } max && v > max) throw new InvalidDataException($"parameters.{key}.value {v} 高於 max {max}");
            }
        }
        return this;
    }
}

public sealed class ParameterSource
{
    public string Title { get; set; } = "";
    public string? Revision { get; set; }
    public string? ApprovedBy { get; set; }
    public string? Date { get; set; }
    public bool External { get; set; }
}

public sealed class ParameterEntry
{
    public JsonElement Value { get; set; }
    public string Unit { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Clause { get; set; }
    public string? Description { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>數值(非數值時 null)。</summary>
    public double? NumberValue => Value.ValueKind == JsonValueKind.Number ? Value.GetDouble() : null;
}

/// <summary>一次參數引用的解析結果(紀錄標頭 header.parameterSet.resolved[])。</summary>
public sealed record ParameterRef
{
    /// <summary>情境欄位路徑(例如 assessment.bands[0].warn)</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("value")] public required JsonElement Value { get; init; }
    [JsonPropertyName("unit")] public required string Unit { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("clause")] public string? Clause { get; init; }
}

/// <summary>參數集摘要(紀錄標頭 header.parameterSet;重播與講評不需原始參數集檔案)。</summary>
public sealed record ParameterSetInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("resolved")] public IReadOnlyList<ParameterRef> Resolved { get; init; } = Array.Empty<ParameterRef>();

    /// <summary>某情境欄位路徑所引用的參數(無則 null)。</summary>
    public ParameterRef? RefAt(string path) => Resolved.FirstOrDefault(r => r.Path == path);
}

/// <summary>參數集 YAML 載入(YAML → JSON → System.Text.Json),含 sha256 與結構檢查。</summary>
public static class ParameterSetLoader
{
    public static ParameterSet Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到參數集:{path}", path);
        var bytes = File.ReadAllBytes(path);
        var set = FromYaml(System.Text.Encoding.UTF8.GetString(bytes));
        set.Path = Ship.RepositoryPaths.ToProjectRelative(path); // 紀錄標頭 header.parameterSet.path 為相對專案根目錄(情境引擎設計 D34)
        set.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        return set;
    }

    public static ParameterSet FromYaml(string yaml)
    {
        var node = YamlJson.ToJsonNode(yaml) ?? throw new InvalidDataException("參數集 YAML 為空");
        var set = ContractJson.Deserialize<ParameterSet>(node.ToJsonString()) ?? throw new InvalidDataException("無法解析參數集");
        set.Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml)));
        return set.Validate();
    }
}
