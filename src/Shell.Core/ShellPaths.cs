// 外殼的路徑規則:安裝根目錄(含 data/ 的上層;發行版 app/shell/../..、儲存庫 build/bin/Shell.WebView2/<cfg>/<tfm>/../../../..)、
// WebView2 使用者資料夾(預設 <根目錄>/build/webview2/<profile>;CLAUDE.md:不寫 C:\ 使用者目錄)、log 檔(<根目錄>/build/logs/shell-<profile>.log)。
namespace SimosaBRM.Shell.Core;

public static class ShellPaths
{
    /// <summary>自 <paramref name="startDirectory"/> 往上找含 data/ 的目錄(最多 <paramref name="maxLevels"/> 層);找不到時回傳 startDirectory。</summary>
    public static string FindInstallRoot(string startDirectory, Func<string, bool> directoryExists, int maxLevels = 8)
    {
        var dir = Path.GetFullPath(startDirectory);
        for (var level = 0; level <= maxLevels; level++)
        {
            if (directoryExists(Path.Combine(dir, "data"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return Path.GetFullPath(startDirectory);
    }

    public static string FindInstallRoot(string startDirectory) => FindInstallRoot(startDirectory, Directory.Exists);

    /// <summary>
    /// WebView2 使用者資料夾:--profile 給了就用它(相對路徑以 <paramref name="currentDirectory"/> 為準),否則 &lt;根目錄&gt;/build/webview2/&lt;profileName&gt;。
    /// </summary>
    public static string ResolveProfileDirectory(string? explicitProfile, string installRoot, string profileName, string? currentDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitProfile))
        {
            var p = explicitProfile.Trim();
            if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
            return Path.GetFullPath(Path.Combine(currentDirectory ?? Directory.GetCurrentDirectory(), p));
        }
        return Path.GetFullPath(Path.Combine(installRoot, "build", "webview2", profileName));
    }

    /// <summary>外殼 log 檔:&lt;根目錄&gt;/build/logs/shell-&lt;profileName&gt;.log(與啟動腳本的 build/logs 同資料夾)。</summary>
    public static string LogFile(string installRoot, string profileName) =>
        Path.GetFullPath(Path.Combine(installRoot, "build", "logs", $"shell-{profileName}.log"));

    /// <summary>Evergreen 離線安裝檔的完整路徑。</summary>
    public static string InstallerPath(string installRoot) =>
        Path.GetFullPath(Path.Combine(installRoot, RuntimeCheck.InstallerRelativePath.Replace('/', Path.DirectorySeparatorChar)));
}
