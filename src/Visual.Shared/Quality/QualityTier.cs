namespace SimosaBRM.Visual.Shared.Quality;

/// <summary>視景品質等級(規劃書第 7.3 節 D6:低/中/高)。</summary>
public enum QualityTier
{
    Low = 0,
    Mid = 1,
    High = 2,
}

/// <summary>渲染器選擇(Godot 4:Compatibility = OpenGL/行動級;Forward+ = Vulkan 完整效果)。</summary>
public enum RendererKind
{
    Compatibility,
    ForwardPlus,
}

/// <summary>
/// 每個等級的渲染參數(規劃書第 7.3 節數值):三級共用同一組幾何與狀態,只差渲染設定。
/// 繪製距離:低 3–4 nm、中 6 nm、高 10 nm+;低品質用 Compatibility 渲染器 + 解析度縮放。
/// </summary>
public sealed record QualitySettings
{
    public required QualityTier Tier { get; init; }
    public required RendererKind Renderer { get; init; }
    /// <summary>3D 解析度縮放(1.0 = 原生)</summary>
    public required double RenderScale { get; init; }
    /// <summary>繪製距離(m)</summary>
    public required double DrawDistanceM { get; init; }
    /// <summary>海面網格每邊格數(低品質平面 + 法線貼圖;中/高 Gerstner 頂點位移)</summary>
    public required int OceanGridCells { get; init; }
    /// <summary>海面網格邊長(m)</summary>
    public required double OceanGridSizeM { get; init; }
    public required bool GerstnerWaves { get; init; }
    public required bool Foam { get; init; }
    public required bool Shadows { get; init; }
    /// <summary>月光 DirectionalLight3D 投影(低品質關閉;月光本身三級都有)</summary>
    public required bool MoonShadows { get; init; }
    public required bool PostProcessing { get; init; }
    public required int Msaa { get; init; }
    public required bool RainParticles { get; init; }
    /// <summary>目標船切換到低面數模型的距離(m)</summary>
    public required double TargetLodDistanceM { get; init; }
    /// <summary>目標船在此距離內才畫出細節(燈、上層建築)</summary>
    public required double TargetDetailDistanceM { get; init; }

    public const double NauticalMile = 1852.0;

    public static QualitySettings For(QualityTier tier) => tier switch
    {
        QualityTier.Low => new()
        {
            Tier = QualityTier.Low,
            Renderer = RendererKind.Compatibility,
            RenderScale = 0.75,
            DrawDistanceM = 3.5 * NauticalMile,
            OceanGridCells = 64,
            OceanGridSizeM = 2 * 3.5 * NauticalMile,
            GerstnerWaves = false,
            Foam = false,
            Shadows = false,
            MoonShadows = false,
            PostProcessing = false,
            Msaa = 0,
            RainParticles = false,
            TargetLodDistanceM = 1.0 * NauticalMile,
            TargetDetailDistanceM = 0.5 * NauticalMile,
        },
        QualityTier.Mid => new()
        {
            Tier = QualityTier.Mid,
            Renderer = RendererKind.ForwardPlus,
            RenderScale = 1.0,
            DrawDistanceM = 6.0 * NauticalMile,
            OceanGridCells = 128,
            OceanGridSizeM = 2 * 6.0 * NauticalMile,
            GerstnerWaves = true,
            Foam = true,
            Shadows = true,
            MoonShadows = true,
            PostProcessing = true,
            Msaa = 2,
            RainParticles = true,
            TargetLodDistanceM = 2.0 * NauticalMile,
            TargetDetailDistanceM = 1.0 * NauticalMile,
        },
        _ => new()
        {
            Tier = QualityTier.High,
            Renderer = RendererKind.ForwardPlus,
            RenderScale = 1.0,
            DrawDistanceM = 10.0 * NauticalMile,
            OceanGridCells = 256,
            OceanGridSizeM = 2 * 10.0 * NauticalMile,
            GerstnerWaves = true,
            Foam = true,
            Shadows = true,
            MoonShadows = true,
            PostProcessing = true,
            Msaa = 4,
            RainParticles = true,
            TargetLodDistanceM = 4.0 * NauticalMile,
            TargetDetailDistanceM = 2.0 * NauticalMile,
        },
    };

    public static QualityTier ParseTier(string s) => s.Trim().ToLowerInvariant() switch
    {
        "low" or "l" or "低" => QualityTier.Low,
        "mid" or "medium" or "m" or "中" => QualityTier.Mid,
        "high" or "h" or "高" => QualityTier.High,
        _ => throw new ArgumentException($"未知的品質等級:{s}(low|mid|high)"),
    };

    public static string TierName(QualityTier t) => t switch
    {
        QualityTier.Low => "low",
        QualityTier.Mid => "mid",
        _ => "high",
    };
}
