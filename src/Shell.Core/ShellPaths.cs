// 外殼的路徑規則:安裝根目錄(含 data/ 的上層;發行版 app/shell/../..、儲存庫 build/bin/Shell.WebView2/<cfg>/<tfm>/../../../..)、
// WebView2 使用者資料夾(預設 <根目錄>/build/webview2/<profile>;CLAUDE.md:不寫 C:\ 使用者目錄)、log 檔(<根目錄>/build/logs/shell-<profile>.log)。
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Shell.Core;

public static class ShellPaths
{
    /// <summary>自 <paramref name="startDirectory"/> 往上找含 data/ 的目錄(最多 <paramref name="maxLevels"/> 層);找不到時回傳 null。</summary>
    public static string? TryFindInstallRoot(string startDirectory, Func<string, bool> directoryExists, int maxLevels = 8)
    {
        var dir = Path.GetFullPath(startDirectory);
        for (var level = 0; level <= maxLevels; level++)
        {
            if (directoryExists(Path.Combine(dir, "data"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    public static string? TryFindInstallRoot(string startDirectory) => TryFindInstallRoot(startDirectory, Directory.Exists);

    /// <summary>同 <see cref="TryFindInstallRoot(string, Func{string, bool}, int)"/>,找不到時回傳 startDirectory(log、下載資料夾、預設使用者資料夾的基準)。</summary>
    public static string FindInstallRoot(string startDirectory, Func<string, bool> directoryExists, int maxLevels = 8)
        => TryFindInstallRoot(startDirectory, directoryExists, maxLevels) ?? Path.GetFullPath(startDirectory);

    public static string FindInstallRoot(string startDirectory) => FindInstallRoot(startDirectory, Directory.Exists);

    /// <summary>
    /// WebView2 使用者資料夾:--profile 給了就用它(相對路徑以 <paramref name="relativeBase"/> 為準,預設 <paramref name="installRoot"/> — 不以目前目錄為準),
    /// 否則 &lt;根目錄&gt;/build/webview2/&lt;profileName&gt;。<paramref name="env"/> 只影響路徑的組合方式(測試注入 Windows 語意;預設目前程序)。
    /// </summary>
    public static string ResolveProfileDirectory(string? explicitProfile, string installRoot, string profileName, string? relativeBase = null, LocalDataEnvironment? env = null)
    {
        env ??= LocalDataEnvironment.FromProcess();
        if (!string.IsNullOrWhiteSpace(explicitProfile))
            return LocalDataGuard.FullPath(explicitProfile.Trim(), relativeBase ?? installRoot, env);
        return LocalDataGuard.FullPath(Path.Combine("build", "webview2", profileName), installRoot, env);
    }

    /// <summary>
    /// 外殼實際使用的使用者資料夾與本機資料守則的判定(Program 啟動時呼叫;被拒絕時不開視窗,以結束碼 2 結束):
    /// <paramref name="foundRoot"/> 為找到的安裝根目錄(<see cref="TryFindInstallRoot(string)"/>;null = 找不到,
    /// 相對路徑改以 <paramref name="currentDirectory"/>(預設目前目錄)為準、所有位置都算根目錄外);<paramref name="installRoot"/> 為預設資料夾的基準
    /// (找不到時是程式所在資料夾)。位置在根目錄外而且位於 Windows 系統磁碟時 <see cref="LocalDataTarget.Allowed"/> 為 false
    /// (經同意設 SIMOSA_ALLOW_SYSTEM_DRIVE=1 則為 <see cref="LocalDataVerdict.AllowedBySwitch"/>)。
    /// </summary>
    public static LocalDataTarget ProfileDirectory(string? explicitProfile, string? foundRoot, string installRoot, string profileName, LocalDataEnvironment env, string? currentDirectory = null)
    {
        var dir = ResolveProfileDirectory(explicitProfile, installRoot, profileName, foundRoot ?? currentDirectory ?? Directory.GetCurrentDirectory(), env);
        return LocalDataGuard.Check(dir, new[] { foundRoot }, env);
    }

    /// <summary>外殼 log 檔:&lt;根目錄&gt;/build/logs/shell-&lt;profileName&gt;.log(與啟動腳本的 build/logs 同資料夾)。</summary>
    public static string LogFile(string installRoot, string profileName) =>
        Path.GetFullPath(Path.Combine(installRoot, "build", "logs", $"shell-{profileName}.log"));

    /// <summary>
    /// 下載資料夾:&lt;根目錄&gt;/build/downloads(網頁的「下載 YAML / 紀錄 JSON / 報告」存到這裡;WebView2 預設是 Windows 使用者的「下載」資料夾,
    /// 即 C:\Users\…\Downloads — 守則:本機資料放 D: 或 G:,不寫 C:)。
    /// </summary>
    public static string DownloadsDirectory(string installRoot) =>
        Path.GetFullPath(Path.Combine(installRoot, "build", "downloads"));

    /// <summary>Evergreen 離線安裝檔的完整路徑。</summary>
    public static string InstallerPath(string installRoot) =>
        Path.GetFullPath(Path.Combine(installRoot, RuntimeCheck.InstallerRelativePath.Replace('/', Path.DirectorySeparatorChar)));
}
