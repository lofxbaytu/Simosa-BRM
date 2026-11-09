using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 船上筆電的執行環境版次(規劃書第 5.5 節「兩船版次清單」;R13):作業系統、.NET 執行階段、WebView2 Runtime。
/// 隨 <c>simosa-pkg status --json</c> / 直連 <c>statusResult</c> / 隨身碟 <c>status.json</c> 一起送到教官端,
/// 寫進 <c>fleet-versions.json</c> 的每船項目(<c>environment</c>),讓訪船時不必再手動登錄 Windows/WebView2 版次
/// (docs/design/packaging-sync.md 第 7 節)。純資訊欄位,不簽章、不影響更新包驗證。
/// </summary>
public sealed record EnvironmentInfo
{
    /// <summary>作業系統描述(RuntimeInformation.OSDescription,例如「Microsoft Windows 10.0.26100」)</summary>
    public string? Os { get; init; }
    /// <summary>.NET 執行階段描述(RuntimeInformation.FrameworkDescription,例如「.NET 8.0.11」)</summary>
    public string? Dotnet { get; init; }
    /// <summary>
    /// WebView2 Runtime(Evergreen)版本字串,自 Windows 登錄檔讀取;非 Windows、未安裝或只有固定版本(Fixed Version)散布時為 null。
    /// JSON 名稱固定為 <c>webview2</c>(不是 camelCase 的 webView2),與設計文件一致。
    /// </summary>
    [JsonPropertyName("webview2")]
    public string? WebView2 { get; init; }
    /// <summary>收集時刻(UTC;由呼叫端的 TimeProvider 給,測試可固定)</summary>
    public DateTime CollectedUtc { get; init; }

    /// <summary>WebView2 Runtime 的 Edge Update 用戶端 GUID(Microsoft 文件「Detect if a WebView2 Runtime is already installed」)</summary>
    public const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>登錄檔鍵(依序嘗試;64 位元 Windows 的全機安裝在 WOW6432Node 下,32 位元 Windows 無 WOW6432Node,每使用者安裝在 HKCU)</summary>
    internal static readonly (string Hive, string Path)[] WebView2RegistryKeys =
    {
        ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId),
        ("HKLM", @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId),
        ("HKCU", @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId),
    };

    /// <summary>收集目前程序所在機器的環境版次。</summary>
    public static EnvironmentInfo Collect(TimeProvider? clock = null) => new()
    {
        Os = RuntimeInformation.OSDescription.Trim(),
        Dotnet = RuntimeInformation.FrameworkDescription.Trim(),
        WebView2 = DetectWebView2Version(),
        CollectedUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime,
    };

    /// <summary>
    /// 讀取 Windows 登錄檔的 WebView2 Runtime 版本(<c>pv</c> 值);非 Windows 一律 null(不呼叫登錄檔 API)。
    /// <c>pv</c> 缺少、空白或為 0.0.0.0(安裝程式留下的佔位值)視為未安裝。讀取失敗(權限、登錄檔損毀)也回 null,status 不因此失敗。
    /// </summary>
    public static string? DetectWebView2Version()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ReadWebView2VersionFromRegistry();
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWebView2VersionFromRegistry()
    {
        foreach (var (hive, path) in WebView2RegistryKeys)
        {
            try
            {
                using var root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = root.OpenSubKey(path, writable: false);
                if (NormalizeVersion(key?.GetValue("pv") as string) is { } version) return version;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 讀不到就試下一個鍵;全部失敗視為未安裝
            }
        }
        return null;
    }

    /// <summary>pv 值正規化:去空白;空字串或 0.0.0.0 → null(未安裝)。</summary>
    internal static string? NormalizeVersion(string? pv)
    {
        var v = pv?.Trim();
        return string.IsNullOrEmpty(v) || v == "0.0.0.0" ? null : v;
    }

    /// <summary>單行摘要(simosa-pkg status 文字輸出、sync 報告)。</summary>
    public string Describe() => $"{Os ?? "?"};{Dotnet ?? "?"};WebView2 {WebView2 ?? "未安裝/未偵測"}";
}
