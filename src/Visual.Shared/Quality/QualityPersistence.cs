using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.Visual.Shared.Quality;

/// <summary>
/// 持久化的品質設定(專案內 build/visual/quality.json;CLAUDE.md:不寫到 C:\)。
/// lastAutoTier = 無手動覆寫時目前生效的自動等級(偵測 + 暖機 + 執行中自動降級),供啟動腳本決定渲染器;
/// 視景在啟動偵測後、每次等級改變、Esc/視窗關閉時寫入(無頭/煙霧模式不寫,以免 CI 的 headless=low 影響實機)。
/// </summary>
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

    /// <summary>
    /// 啟動時應使用的渲染器(Godot 的渲染器只能以 --rendering-method 在啟動時決定,執行中不能切換):
    /// 啟動參數要求的等級 > 持久化的手動覆寫 > 上次自動分級(lastAutoTier;由視景在偵測、自動降級與離開時寫入);
    /// 其中任一為 low → Compatibility,否則 Forward+。tools/visual-run.ps1/.sh 在 -Quality auto 時以同一規則讀 build/visual/quality.json。
    /// </summary>
    public static RendererKind StartupRenderer(PersistedQuality persisted, QualityTier? requested)
    {
        var tier = requested ?? ParseOverride(persisted.ManualOverride) ?? ParseOverride(persisted.LastAutoTier);
        return tier is { } t ? QualitySettings.For(t).Renderer : RendererKind.ForwardPlus;
    }

    public static QualityTier? ParseOverride(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return null;
        try { return QualitySettings.ParseTier(s); } catch (ArgumentException) { return null; }
    }
}
