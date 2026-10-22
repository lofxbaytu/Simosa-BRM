using System.Text.RegularExpressions;

namespace SimosaBRM.Visual.Shared.Quality;

/// <summary>啟動時偵測到的硬體(Godot RenderingServer.GetVideoAdapterName、OS.GetMemoryInfo 等;單元測試直接給值)。</summary>
public sealed record HardwareInfo
{
    public required string GpuName { get; init; }
    /// <summary>顯示記憶體(MB;未知為 null;內顯通常回報共用記憶體)</summary>
    public long? VramMB { get; init; }
    /// <summary>系統記憶體(MB)</summary>
    public long? SystemMemoryMB { get; init; }
    /// <summary>Godot 回報的裝置類型(discrete / integrated / 其他;未知 null)</summary>
    public string? DeviceType { get; init; }
    /// <summary>實測幀率(啟動暖機 N 秒的平均;無則 null)</summary>
    public double? MeasuredFps { get; init; }
    public int ScreenWidth { get; init; } = 1920;
    public int ScreenHeight { get; init; } = 1080;
}

/// <summary>偵測結果與理由(HUD 與紀錄顯示)。</summary>
public sealed record QualityDecision(QualityTier Tier, string Reason);

/// <summary>
/// 品質分級自動偵測(規劃書第 7.3 節):依顯示晶片、記憶體與實測幀率。
/// 規則(由上而下第一個成立者):
/// 1. 獨顯且屬高階型號(RTX 20 以上、RX 6600 以上、Arc A5/A7、Radeon Pro/Quadro)→ 高;其他獨顯 → 中。
/// 2. 內顯(Intel Iris Xe/UHD/Arc Graphics、AMD Radeon Graphics/7xxM/8xxM、Apple M)+ 系統記憶體 ≥ 16 GB → 中;否則 → 低。
/// 3. 未知 GPU(軟體渲染、llvmpipe、headless)→ 低。
/// 之後若有實測幀率:低於 30 fps 降一級(規劃書:內顯基準 ≥30 fps),高於 90 fps 且目前為中則升到高。
/// </summary>
public static class QualityDetector
{
    private static readonly Regex HighEndDiscrete = new(@"(RTX\s*[2-5]0\d{2}|RTX\s*[A-Z]\d{3,4}|Radeon\s*RX\s*(6[6-9]\d{2}|7\d{3}|8\d{3}|9\d{3})|Arc\s*(A|B)[5-7]\d{2}|Quadro|Radeon\s*Pro\s*W)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Discrete = new(@"(GeForce|GTX|RTX|Radeon\s*RX|Arc\s*(A|B)\d{3}|Quadro|Tesla|Radeon\s*Pro)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Integrated = new(@"(Iris|UHD\s*Graphics|HD\s*Graphics|Arc\(TM\)\s*Graphics|Arc\s*Graphics|Radeon\(TM\)\s*Graphics|Radeon\s*Graphics|Radeon\s*[78]\d0M|Vega\s*\d|Apple\s*M\d|Adreno|Mali)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Software = new(@"(llvmpipe|SwiftShader|Software|Microsoft Basic Render|dummy|headless)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static QualityDecision Detect(HardwareInfo hw)
    {
        var name = hw.GpuName ?? string.Empty;
        var memGB = (hw.SystemMemoryMB ?? 0) / 1024.0;
        QualityTier tier;
        string reason;

        if (string.IsNullOrWhiteSpace(name) || Software.IsMatch(name))
        {
            tier = QualityTier.Low;
            reason = $"GPU 未知或軟體渲染({name})→ 低";
        }
        else if (string.Equals(hw.DeviceType, "discrete", StringComparison.OrdinalIgnoreCase) || (Discrete.IsMatch(name) && !Integrated.IsMatch(name)))
        {
            if (HighEndDiscrete.IsMatch(name) || (hw.VramMB is { } v && v >= 6000 && !Integrated.IsMatch(name)))
            {
                tier = QualityTier.High;
                reason = $"獨立顯示晶片 {name}(VRAM {hw.VramMB?.ToString() ?? "?"} MB)→ 高";
            }
            else
            {
                tier = QualityTier.Mid;
                reason = $"入門獨立顯示晶片 {name} → 中";
            }
        }
        else if (Integrated.IsMatch(name) || string.Equals(hw.DeviceType, "integrated", StringComparison.OrdinalIgnoreCase))
        {
            if (memGB >= 15.5)
            {
                tier = QualityTier.Mid;
                reason = $"內建顯示晶片 {name},系統記憶體 {memGB:F0} GB → 中";
            }
            else
            {
                tier = QualityTier.Low;
                reason = $"內建顯示晶片 {name},系統記憶體 {memGB:F0} GB(<16 GB)→ 低";
            }
        }
        else
        {
            tier = QualityTier.Low;
            reason = $"無法分類的顯示晶片 {name} → 低(可手動覆寫)";
        }

        if (hw.MeasuredFps is { } fps)
        {
            if (fps < 30.0 && tier > QualityTier.Low)
            {
                tier -= 1;
                reason += $";實測 {fps:F0} fps < 30 → 降為 {QualitySettings.TierName(tier)}";
            }
            else if (fps > 90.0 && tier == QualityTier.Mid)
            {
                tier = QualityTier.High;
                reason += $";實測 {fps:F0} fps > 90 → 升為 high";
            }
        }
        return new QualityDecision(tier, reason);
    }
}
