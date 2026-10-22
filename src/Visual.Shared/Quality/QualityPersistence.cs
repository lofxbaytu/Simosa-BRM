using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.Visual.Shared.Quality;

/// <summary>持久化的品質設定(專案內 build/visual/quality.json;CLAUDE.md:不寫到 C:\)。</summary>
public sealed record PersistedQuality
{
    [JsonPropertyName("manualOverride")] public string? ManualOverride { get; init; }
    [JsonPropertyName("lastAutoTier")] public string? LastAutoTier { get; init; }
    [JsonPropertyName("lastGpu")] public string? LastGpu { get; init; }
    [JsonPropertyName("hudVisible")] public bool HudVisible { get; init; } = true;
    [JsonPropertyName("fovDeg")] public double? FovDeg { get; init; }
    [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; init; }
}

public static class QualityPersistence
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultPath(string projectRoot) => Path.Combine(projectRoot, "build", "visual", "quality.json");

    public static PersistedQuality Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PersistedQuality();
            return JsonSerializer.Deserialize<PersistedQuality>(File.ReadAllText(path), Options) ?? new PersistedQuality();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PersistedQuality();
        }
    }

    public static void Save(string path, PersistedQuality q)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(q with { UpdatedUtc = DateTime.UtcNow.ToString("O") }, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static QualityTier? ParseOverride(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return null;
        try { return QualitySettings.ParseTier(s); } catch (ArgumentException) { return null; }
    }
}
