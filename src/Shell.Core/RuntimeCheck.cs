// WebView2 Runtime 偵測結果 → 中文說明(規劃書第 5.5 節:船上筆電完全離線,WebView2 採固定版本隨應用程式散布;docs/ops/安裝與啟動.md 第 4.5 節)。
// 實際偵測(CoreWebView2Environment.GetAvailableBrowserVersionString)在 Shell.WebView2;本類別只把版本字串/例外對應成狀態與訊息,Linux 可測。
namespace SimosaBRM.Shell.Core;

public enum RuntimeStatus
{
    /// <summary>已安裝(Evergreen Runtime 或固定版本),可建立 WebView2 環境</summary>
    Available,
    /// <summary>未安裝 WebView2 Runtime(WebView2RuntimeNotFoundException 或版本字串為空)</summary>
    NotInstalled,
    /// <summary>外殼本身缺 WebView2Loader.dll(發行版不完整;DllNotFoundException)</summary>
    LoaderMissing,
    /// <summary>其他錯誤(使用者資料夾不可寫、Runtime 損毀…)</summary>
    Failed,
}

public sealed record RuntimeCheckResult(RuntimeStatus Status, string? Version, string Message)
{
    public bool Available => Status == RuntimeStatus.Available;
}

public static class RuntimeCheck
{
    /// <summary>Evergreen 離線安裝檔在安裝根目錄內的相對路徑(岸上自 Microsoft 下載 Evergreen Standalone Installer x64 後放入發行版或隨身碟)。</summary>
    public const string InstallerRelativePath = "tools/webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    public const string DocsReference = "docs/ops/安裝與啟動.md 第 4.5 節";

    /// <summary>
    /// 把偵測結果對應成狀態與訊息。<paramref name="version"/> 為 GetAvailableBrowserVersionString 的回傳(成功時非空);
    /// <paramref name="error"/> 為偵測或建立環境時的例外(依型別名稱判斷,避免 Core 依賴 WebView2 套件)。
    /// </summary>
    public static RuntimeCheckResult Evaluate(string? version, Exception? error, string installRoot)
    {
        var installer = Path.Combine(installRoot, InstallerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (error is null && !string.IsNullOrWhiteSpace(version))
            return new RuntimeCheckResult(RuntimeStatus.Available, version.Trim(), $"WebView2 Runtime {version.Trim()}");

        if (error is DllNotFoundException || error?.GetType().Name.Contains("DllNotFound", StringComparison.Ordinal) == true)
        {
            return new RuntimeCheckResult(RuntimeStatus.LoaderMissing, null,
                "外殼程式缺少 WebView2Loader.dll(發行版 app\\shell\\ 不完整)。\n" +
                "請以 manifest.json 重新核對發行版(simosa-pkg release verify --dir <安裝根目錄>)或重新複製 app\\shell\\;" +
                $"說明見 {DocsReference}。\n\n錯誤:{error!.Message}");
        }

        if (error is null || error.GetType().Name.Contains("RuntimeNotFound", StringComparison.Ordinal))
        {
            return new RuntimeCheckResult(RuntimeStatus.NotInstalled, null,
                "這台電腦尚未安裝 Microsoft Edge WebView2 Runtime,操船台外殼無法顯示網頁。\n\n" +
                "船上筆電不連網際網路,請用岸上準備的離線安裝檔(Evergreen Standalone Installer x64):\n" +
                $"  {installer}\n" +
                "以系統管理員身分執行(或在命令列加 /silent /install),安裝完成後按「重試」。\n" +
                "若發行版或隨身碟沒有這個檔案,請訪船船長自岸上下載後隨更新包帶上船;" +
                $"步驟與版次登錄見 {DocsReference}。\n" +
                "(在未改用外殼前,仍可用 start-ship-laptop.ps1 的預設瀏覽器開啟同一網頁。)" +
                (error is null ? "" : $"\n\n錯誤:{error.Message}"));
        }

        return new RuntimeCheckResult(RuntimeStatus.Failed, null,
            "WebView2 無法啟動(Runtime 已安裝但建立環境失敗)。\n" +
            "請確認使用者資料夾可寫入(預設 <安裝根目錄>\\build\\webview2\\;可用 --profile 指定)、磁碟未滿,並查看 build\\logs\\shell-*.log;" +
            $"說明見 {DocsReference}。\n\n錯誤:{error.GetType().Name}:{error.Message}");
    }
}
