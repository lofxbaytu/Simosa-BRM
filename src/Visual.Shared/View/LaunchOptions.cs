using SimosaBRM.Visual.Shared.Quality;

namespace SimosaBRM.Visual.Shared.View;

/// <summary>日夜模式(state 無日期時間,故由啟動參數決定;auto = 依 --start-utc 與自船位置算太陽高度)。</summary>
public enum DayNightMode
{
    Auto,
    Day,
    Dusk,
    Night,
}

/// <summary>
/// 視景啟動參數(docs/design/visual-client.md 第 6 節;多螢幕原則:每顯示器一個程式訂閱同一狀態流):
/// --view=bridge|portWing|stbdWing|aft|overview、--monitor=N、--ws=url、--quality=low|mid|high|auto、--data-dir=路徑、
/// --area=區域 id 或檔案(可多個,逗號分隔)、--ship=FSB1、--loading=full|ballast、--daynight=auto|day|dusk|night、--start-utc=ISO8601、
/// --fullscreen、--no-hud、--smoke[=frames](無頭煙霧測試)、--smoke-out=路徑。
/// 格式 --key=value 或 --key value;未知參數忽略(Godot 自身參數會混在前面)。
/// </summary>
public sealed record LaunchOptions
{
    public ViewKind View { get; init; } = ViewKind.Bridge;
    public int Monitor { get; init; } = 0;
    public string WebSocketUrl { get; init; } = "ws://127.0.0.1:8765";
    /// <summary>null = auto</summary>
    public QualityTier? Quality { get; init; }
    public string? DataDir { get; init; }
    public IReadOnlyList<string> Areas { get; init; } = Array.Empty<string>();
    public string ShipId { get; init; } = "FSB1";
    public string Loading { get; init; } = "full";
    public DayNightMode DayNight { get; init; } = DayNightMode.Auto;
    public DateTimeOffset? StartUtc { get; init; }
    public bool Fullscreen { get; init; }
    public bool Hud { get; init; } = true;
    public bool Smoke { get; init; }
    public int SmokeFrames { get; init; } = 200;
    public string? SmokeOutput { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var o = new LaunchOptions();
        var warnings = new List<string>();
        var areas = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            string key, value;
            var eq = a.IndexOf('=');
            if (eq >= 0) { key = a[2..eq]; value = a[(eq + 1)..]; }
            else
            {
                key = a[2..];
                value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
            }
            try
            {
                switch (key.ToLowerInvariant())
                {
                    case "view": o = o with { View = ViewDefinition.ParseKind(value) }; break;
                    case "monitor": o = o with { Monitor = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }; break;
                    case "ws": o = o with { WebSocketUrl = value }; break;
                    case "quality": o = o with { Quality = QualityPersistence.ParseOverride(value) }; if (o.Quality is null && !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) warnings.Add($"--quality={value} 無法辨識,改用 auto"); break;
                    case "data-dir": o = o with { DataDir = value }; break;
                    case "area": areas.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "ship": o = o with { ShipId = value }; break;
                    case "loading": o = o with { Loading = value }; break;
                    case "daynight": o = o with { DayNight = Enum.Parse<DayNightMode>(value, ignoreCase: true) }; break;
                    case "start-utc": o = o with { StartUtc = DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }; break;
                    case "fullscreen": o = o with { Fullscreen = value is "" or "1" or "true" }; break;
                    case "no-hud": o = o with { Hud = false }; break;
                    case "smoke":
                        o = o with { Smoke = true };
                        if (value.Length > 0) o = o with { SmokeFrames = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
                        break;
                    case "smoke-frames": o = o with { SmokeFrames = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }; break;
                    case "smoke-out": o = o with { SmokeOutput = value }; break;
                    default: break; // Godot 自身或未知參數
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                warnings.Add($"參數 --{key}={value} 無效:{ex.Message}");
            }
        }
        return o with { Areas = areas, Warnings = warnings };
    }
}
