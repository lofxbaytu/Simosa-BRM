// WebView2 外殼的命令列參數與網址組合(規劃書第 5.2 節「WebView2 殼」、第 11.2 節 Instruments.Shell;需求 R1、R13)。
// 網址規則與網頁一致:index.html ?station=bridge|portWing|stbdWing|ecr(src/Instruments.Web/src/lib/alerts.ts parseStation)、
// instructor.html ?mode=simple|full|replay(instructor-app.ts readMode)、radar.html ?radar=x|s、各頁 ?ws=(狀態或雷達服務位址)。
namespace SimosaBRM.Shell.Core;

/// <summary>外殼要開的頁面(--page)。</summary>
public enum ShellPage
{
    /// <summary>操船台/儀器頁 index.html</summary>
    Conning,
    /// <summary>教官站 instructor.html(船上預設 ?mode=simple)</summary>
    Instructor,
    /// <summary>雷達 PPI radar.html</summary>
    Radar,
    /// <summary>報告站 report.html</summary>
    Report,
}

/// <summary>工作站(--station;警報面板的確認/轉移依此;docs/design/sensors-alerts.md 第 3.6 節)。</summary>
public enum ShellStation
{
    Bridge,
    PortWing,
    StbdWing,
    Ecr,
}

/// <summary>命令列解析結果:成功時 <see cref="Options"/> 非 null,否則 <see cref="Error"/> 為中文錯誤訊息。</summary>
public sealed record ShellParseResult(ShellOptions? Options, string? Error)
{
    public bool Ok => Options is not null && Error is null;
}

/// <summary>
/// WebView2 外殼的啟動選項。--url 給完整網址時優先(頁面與站別由網址推斷,用於視窗標題與使用者資料夾名稱);
/// 否則由 --base + --page + --station/--ws/--mode/--radar 組合。
/// </summary>
public sealed record ShellOptions
{
    public const string DefaultBaseUrl = "http://127.0.0.1:8080";

    /// <summary>--url:完整網址(優先於 --page 等組合)。</summary>
    public string? Url { get; init; }
    /// <summary>--base:網頁伺服基底(預設 http://127.0.0.1:8080,即船上 simosa-web)。</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    /// <summary>--page:conning|instructor|radar|report(預設 conning)。</summary>
    public ShellPage Page { get; init; } = ShellPage.Conning;
    /// <summary>--station:bridge|portWing|stbdWing|ecr(預設 bridge;只有操船台頁才放進網址)。</summary>
    public ShellStation Station { get; init; } = ShellStation.Bridge;
    /// <summary>--ws:狀態服務(ws://host:8765)或雷達服務(ws://host:8766)位址;未給時網頁自行以頁面主機推定。</summary>
    public string? Ws { get; init; }
    /// <summary>--mode:教官站模式 simple|full|replay(教官站未給時船上預設 simple)。</summary>
    public string? Mode { get; init; }
    /// <summary>--radar:雷達頁選台 x|s。</summary>
    public string? Radar { get; init; }
    /// <summary>--kiosk:無邊框全螢幕、最上層、關閉需確認。</summary>
    public bool Kiosk { get; init; }
    /// <summary>--profile:WebView2 使用者資料夾(未給時 &lt;安裝根目錄&gt;/build/webview2/&lt;page&gt;;不寫 C:\ 使用者目錄)。</summary>
    public string? Profile { get; init; }
    /// <summary>--allow-devtools:允許 DevTools 與右鍵選單(預設關閉)。</summary>
    public bool AllowDevTools { get; init; }

    /// <summary>--no-close-confirm:關閉視窗不詢問(啟動腳本以 stop-all.ps1 的 CloseMainWindow 結束外殼時使用;預設關閉 = 使用者關閉需確認)。</summary>
    public bool NoCloseConfirm { get; init; }
    /// <summary>--monitor:顯示在第幾個螢幕(0 起算;未給時依系統預設/主螢幕)。</summary>
    public int? Monitor { get; init; }
    /// <summary>--help</summary>
    public bool ShowHelp { get; init; }

    public static readonly IReadOnlyList<string> ModeValues = new[] { "simple", "full", "replay" };
    public static readonly IReadOnlyList<string> RadarValues = new[] { "x", "s" };

    public const string Usage =
        "用法:SimosaBRM.Shell [--url <完整網址>] [--page conning|instructor|radar|report] [--station bridge|portWing|stbdWing|ecr]\n" +
        "                      [--ws ws://主機:8765] [--mode simple|full|replay] [--radar x|s] [--base http://127.0.0.1:8080]\n" +
        "                      [--kiosk] [--monitor <n>] [--profile <資料夾>] [--allow-devtools] [--no-close-confirm] [--help]\n" +
        "  --url            直接開啟此網址(優先;頁面與站別由網址推斷)\n" +
        "  --page           conning 操船台 index.html(預設)、instructor 教官站、radar 雷達、report 報告站\n" +
        "  --station        操船台的工作站(?station=;預設 bridge)\n" +
        "  --ws             狀態服務或雷達服務位址(?ws=;未給時網頁依頁面主機推定)\n" +
        "  --mode           教官站模式(?mode=;教官站未給時為 simple)\n" +
        "  --radar          雷達頁選台(?radar=;預設 x)\n" +
        "  --kiosk          無邊框全螢幕、最上層;F11 切換全螢幕、Esc 不離開、Alt+F4 需確認\n" +
        "  --monitor        顯示在第 n 個螢幕(0 起算;規劃書 D6 兩螢幕配置:視景在 1、操船台在 0)\n" +
        "  --profile        WebView2 使用者資料夾(預設 <安裝根目錄>\\build\\webview2\\<page>;不寫 C:\\)\n" +
        "  --allow-devtools 允許 DevTools 與右鍵選單(預設關閉)\n" +
        "  --no-close-confirm 關閉視窗不詢問(啟動腳本/stop-all.ps1 結束外殼用)\n" +
        "  參數可寫成 --key value 或 --key=value(旗標亦接受 --kiosk true|false);WebView2 Runtime 未安裝時顯示離線安裝說明(docs/ops/安裝與啟動.md 第 4.5 節)";

    /// <summary>解析命令列;錯誤時回傳中文訊息(呼叫端顯示訊息與 <see cref="Usage"/> 後以結束碼 2 離開)。</summary>
    public static ShellParseResult Parse(IReadOnlyList<string> args)
    {
        var o = new ShellOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var raw = args[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string key, value = "";
            var hasValue = false;
            var eq = raw.IndexOf('=');
            if (raw.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                key = raw[..eq];
                value = raw[(eq + 1)..];
                hasValue = true;
            }
            else key = raw;
            key = key.ToLowerInvariant();

            // 旗標:--key、--key=true|false,或 --key true|false(下一個參數為布林字樣時消耗它)
            string? FlagValue()
            {
                if (hasValue) return value;
                if (i + 1 < args.Count && IsBoolToken(args[i + 1])) return args[++i];
                return null;
            }

            // 取下一個參數作為值(--key value 形式)
            string Next(string name)
            {
                if (hasValue) return value;
                if (i + 1 >= args.Count) throw new ShellArgumentException($"{name} 需要一個值");
                return args[++i];
            }

            try
            {
                switch (key)
                {
                    case "--url":
                        o = o with { Url = RequireNonEmpty(Next("--url"), "--url") };
                        break;
                    case "--base":
                        o = o with { BaseUrl = RequireNonEmpty(Next("--base"), "--base").TrimEnd('/') };
                        break;
                    case "--page":
                        {
                            var p = Next("--page");
                            o = o with { Page = ParsePage(p) ?? throw new ShellArgumentException($"--page 須為 conning|instructor|radar|report,實際:{p}") };
                            break;
                        }
                    case "--station":
                        {
                            var s = Next("--station");
                            o = o with { Station = ParseStation(s) ?? throw new ShellArgumentException($"--station 須為 bridge|portWing|stbdWing|ecr,實際:{s}") };
                            break;
                        }
                    case "--ws":
                        o = o with { Ws = RequireNonEmpty(Next("--ws"), "--ws") };
                        break;
                    case "--mode":
                        {
                            var m = Next("--mode").Trim().ToLowerInvariant();
                            if (!ModeValues.Contains(m)) throw new ShellArgumentException($"--mode 須為 simple|full|replay,實際:{m}");
                            o = o with { Mode = m };
                            break;
                        }
                    case "--radar":
                        {
                            var r = Next("--radar").Trim().ToLowerInvariant();
                            if (!RadarValues.Contains(r)) throw new ShellArgumentException($"--radar 須為 x|s,實際:{r}");
                            o = o with { Radar = r };
                            break;
                        }
                    case "--profile":
                        o = o with { Profile = RequireNonEmpty(Next("--profile"), "--profile") };
                        break;
                    case "--monitor":
                        {
                            var m = Next("--monitor").Trim();
                            if (!int.TryParse(m, out var idx) || idx < 0) throw new ShellArgumentException($"--monitor 須為 0 起算的整數,實際:{m}");
                            o = o with { Monitor = idx };
                            break;
                        }
                    case "--kiosk":
                        o = o with { Kiosk = ParseFlag(FlagValue(), "--kiosk") };
                        break;
                    case "--allow-devtools":
                        o = o with { AllowDevTools = ParseFlag(FlagValue(), "--allow-devtools") };
                        break;
                    case "--no-close-confirm":
                        o = o with { NoCloseConfirm = ParseFlag(FlagValue(), "--no-close-confirm") };
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        o = o with { ShowHelp = true };
                        break;
                    default:
                        throw new ShellArgumentException($"未知參數:{raw}");
                }
            }
            catch (ShellArgumentException ex)
            {
                return new ShellParseResult(null, ex.Message);
            }
        }

        if (o.Url is not null)
        {
            if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeFile))
                return new ShellParseResult(null, $"--url 須為 http/https/file 絕對網址,實際:{o.Url}");
            // 由網址推斷頁面/站別(視窗標題、預設使用者資料夾名稱用);網址本身不改
            var inferredPage = InferPage(u) ?? o.Page;
            var q = ParseQuery(u.Query);
            var inferredStation = q.TryGetValue("station", out var st) ? ParseStation(st) ?? o.Station : o.Station;
            var inferredMode = q.TryGetValue("mode", out var md) && ModeValues.Contains(md.ToLowerInvariant()) ? md.ToLowerInvariant() : o.Mode;
            o = o with { Page = inferredPage, Station = inferredStation, Mode = inferredMode };
        }
        else if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var b) || (b.Scheme != Uri.UriSchemeHttp && b.Scheme != Uri.UriSchemeHttps))
        {
            return new ShellParseResult(null, $"--base 須為 http/https 絕對網址,實際:{o.BaseUrl}");
        }
        if (o.Ws is not null && !(o.Ws.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || o.Ws.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)))
            return new ShellParseResult(null, $"--ws 須以 ws:// 或 wss:// 開頭,實際:{o.Ws}");
        return new ShellParseResult(o, null);
    }

    /// <summary>頁面檔名(相對網頁根目錄;與 app/web 一致)。</summary>
    public static string PageFile(ShellPage page) => page switch
    {
        ShellPage.Conning => "index.html",
        ShellPage.Instructor => "instructor.html",
        ShellPage.Radar => "radar.html",
        ShellPage.Report => "report.html",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>--page 的寫法(小寫英文;使用者資料夾名稱亦用此)。</summary>
    public static string PageName(ShellPage page) => page switch
    {
        ShellPage.Conning => "conning",
        ShellPage.Instructor => "instructor",
        ShellPage.Radar => "radar",
        ShellPage.Report => "report",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>?station= 的寫法(與網頁 parseStation 相同的大小寫)。</summary>
    public static string StationName(ShellStation station) => station switch
    {
        ShellStation.Bridge => "bridge",
        ShellStation.PortWing => "portWing",
        ShellStation.StbdWing => "stbdWing",
        ShellStation.Ecr => "ecr",
        _ => throw new ArgumentOutOfRangeException(nameof(station)),
    };

    public static string PageLabel(ShellPage page) => page switch
    {
        ShellPage.Conning => "操船台",
        ShellPage.Instructor => "教官站",
        ShellPage.Radar => "雷達",
        ShellPage.Report => "報告站",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>站別中文(與網頁 ALERT_STATION_LABEL 相同)。</summary>
    public static string StationLabel(ShellStation station) => station switch
    {
        ShellStation.Bridge => "駕駛台",
        ShellStation.PortWing => "左翼",
        ShellStation.StbdWing => "右翼",
        ShellStation.Ecr => "機控室",
        _ => throw new ArgumentOutOfRangeException(nameof(station)),
    };

    public static ShellPage? ParsePage(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "conning" or "index" or "bridge-display" => ShellPage.Conning,
        "instructor" => ShellPage.Instructor,
        "radar" => ShellPage.Radar,
        "report" => ShellPage.Report,
        _ => null,
    };

    public static ShellStation? ParseStation(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "bridge" => ShellStation.Bridge,
        "portwing" => ShellStation.PortWing,
        "stbdwing" => ShellStation.StbdWing,
        "ecr" => ShellStation.Ecr,
        _ => null,
    };

    /// <summary>教官站實際使用的模式:未給時船上預設 simple(規劃書第 9.1 節簡化教官模式)。</summary>
    public string EffectiveMode => Mode ?? "simple";

    /// <summary>
    /// 要導向的網址:--url 原樣;否則 base/頁面檔 + 查詢字串(操船台 ?station=、教官站 ?mode=、雷達 ?radar=、各頁 ?ws=)。
    /// 查詢值以 Uri.EscapeDataString 編碼。
    /// </summary>
    public string BuildUrl()
    {
        if (Url is not null) return Url;
        var query = new List<KeyValuePair<string, string>>();
        switch (Page)
        {
            case ShellPage.Conning:
                query.Add(new("station", StationName(Station)));
                break;
            case ShellPage.Instructor:
                query.Add(new("mode", EffectiveMode));
                break;
            case ShellPage.Radar:
                query.Add(new("radar", Radar ?? "x"));
                break;
            case ShellPage.Report:
                break;
        }
        if (Ws is not null && Page != ShellPage.Report) query.Add(new("ws", Ws));
        var sb = new System.Text.StringBuilder(BaseUrl.TrimEnd('/')).Append('/').Append(PageFile(Page));
        for (var i = 0; i < query.Count; i++)
            sb.Append(i == 0 ? '?' : '&').Append(Uri.EscapeDataString(query[i].Key)).Append('=').Append(Uri.EscapeDataString(query[i].Value));
        return sb.ToString();
    }

    /// <summary>視窗標題:頁面與站別(操船台)/模式(教官站),例如「Simosa BRM 操船台 — 駕駛台 bridge」。</summary>
    public string WindowTitle => Page switch
    {
        ShellPage.Conning => $"Simosa BRM {PageLabel(Page)} — {StationLabel(Station)} {StationName(Station)}",
        ShellPage.Instructor => $"Simosa BRM {PageLabel(Page)} — {EffectiveMode}",
        ShellPage.Radar => $"Simosa BRM {PageLabel(Page)} — {(Radar ?? "x").ToUpperInvariant()} 波段",
        _ => $"Simosa BRM {PageLabel(Page)}",
    };

    /// <summary>預設使用者資料夾名稱(同一台筆電同時開多個外殼時各用自己的資料夾):conning-bridge、instructor、radar-x…。</summary>
    public string ProfileName => Page switch
    {
        ShellPage.Conning => $"{PageName(Page)}-{StationName(Station).ToLowerInvariant()}",
        ShellPage.Radar => $"{PageName(Page)}-{Radar ?? "x"}",
        _ => PageName(Page),
    };

    /// <summary>由網址路徑最後一段推斷頁面(index.html → conning…;其他 → null)。</summary>
    public static ShellPage? InferPage(Uri url)
    {
        var last = url.AbsolutePath.TrimEnd('/');
        var slash = last.LastIndexOf('/');
        var file = (slash >= 0 ? last[(slash + 1)..] : last).ToLowerInvariant();
        return file switch
        {
            "" or "index.html" or "index.htm" => ShellPage.Conning,
            "instructor.html" => ShellPage.Instructor,
            "radar.html" => ShellPage.Radar,
            "report.html" => ShellPage.Report,
            _ => null,
        };
    }

    /// <summary>簡單的查詢字串解析(鍵小寫;重複鍵取第一個)。</summary>
    public static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            d.TryAdd(k, v);
        }
        return d;
    }

    private static string RequireNonEmpty(string v, string name)
    {
        if (string.IsNullOrWhiteSpace(v)) throw new ShellArgumentException($"{name} 的值不可為空");
        return v.Trim();
    }

    /// <summary>true|false|1|0|yes|no|on|off(不分大小寫)視為旗標值。</summary>
    private static bool IsBoolToken(string t)
        => t.ToLowerInvariant() is "true" or "false" or "1" or "0" or "yes" or "no" or "on" or "off";

    private static bool ParseFlag(string? v, string name)
    {
        if (v is null) return true;
        return v.Trim().ToLowerInvariant() switch
        {
            "" or "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ShellArgumentException($"{name} 的值須為 true|false,實際:{v}"),
        };
    }

    private sealed class ShellArgumentException(string message) : Exception(message);
}
