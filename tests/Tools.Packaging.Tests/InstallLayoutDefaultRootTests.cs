using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 預設安裝根目錄(<see cref="InstallLayout.DefaultRoot()"/>;CLAUDE.md「本機資料放 D: 或 G:,不放 C:」):
/// 環境變數優先 → 程式所在的安裝根目錄(data/ + app/)→ 專案根目錄 → Windows 上 D:\Simosa BRM、G:\Simosa BRM(已存在者優先,其次磁碟存在者)
/// → 兩個磁碟都沒有時仍回傳 D:\Simosa BRM(不存在的磁碟,後續明確失敗),**不會自動落到 C:**。
/// </summary>
public sealed class InstallLayoutDefaultRootTests
{
    private const string D = @"D:\Simosa BRM";
    private const string G = @"G:\Simosa BRM";

    private static string Resolve(string? env = null, string baseDir = "/opt/elsewhere/bin", IEnumerable<string>? existing = null,
        string? repo = null, bool windows = true, string cwd = "/cwd")
    {
        var set = new HashSet<string>(existing ?? Array.Empty<string>(), StringComparer.Ordinal);
        return InstallLayout.DefaultRoot(env, baseDir, set.Contains, () => repo, windows, cwd);
    }

    [Fact]
    public void EnvironmentVariableWinsOverEverything()
    {
        var root = Resolve(env: "/custom/root", existing: new[] { D, G, @"D:\", @"G:\" }, repo: "/repo");
        Assert.Equal("/custom/root", root);
    }

    [Fact]
    public void InstalledLayoutIsFoundFromExecutableLocation_OnAnyDrive()
    {
        // 發行版:<root>/app/pkg/simosa-pkg.dll → <root>(同時含 data/ 與 app/);程式放在 G: 就用 G:,不寫死 D:
        var root = Path.GetFullPath(Path.Combine("/media", "g", "Simosa BRM"));
        var baseDir = Path.Combine(root, "app", "pkg");
        var existing = new[] { Path.Combine(root, "data"), Path.Combine(root, "app"), D, @"D:\" };
        Assert.Equal(root, Resolve(baseDir: baseDir, existing: existing, repo: "/repo"));
        // 尾端分隔字元不影響
        Assert.Equal(root, Resolve(baseDir: baseDir + Path.DirectorySeparatorChar, existing: existing));
        // 只有 data/ 沒有 app/ 的上層不算安裝根目錄
        Assert.Null(InstallLayout.FindInstalledRoot(baseDir, new HashSet<string> { Path.Combine(root, "data") }.Contains));
    }

    [Fact]
    public void RepositoryRootIsUsedWhenNotInstalled()
        => Assert.Equal("/repo", Resolve(existing: new[] { D, G }, repo: "/repo"));

    [Fact]
    public void WindowsFallback_PrefersExistingFolder_ThenExistingDrive_DThenG()
    {
        Assert.Equal(D, Resolve(existing: new[] { D, G }));
        Assert.Equal(G, Resolve(existing: new[] { G, @"D:\" }));          // G: 上已有安裝資料夾 → 用它,而不是在 D: 新建
        Assert.Equal(D, Resolve(existing: new[] { @"D:\", @"G:\" }));     // 都還沒有資料夾 → D: 優先
        Assert.Equal(G, Resolve(existing: new[] { @"G:\" }));             // 沒有 D: → G:
    }

    [Fact]
    public void WindowsFallback_NeverReturnsSystemDrive()
    {
        // 只有 C:(沒有 D:、G:):回傳 D:\Simosa BRM(磁碟不存在,後續建立目錄明確失敗),不落到 C: 或目前目錄
        var root = Resolve(existing: new[] { @"C:\", @"C:\Simosa BRM" }, cwd: @"C:\Users\someone");
        Assert.Equal(D, root);
        Assert.DoesNotContain(InstallLayout.WindowsDefaultRoots, r => r.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonWindowsFallsBackToCurrentDirectory()
        => Assert.Equal("/cwd", Resolve(windows: false, existing: new[] { D, G }));

    [Fact]
    public void ParameterlessOverloadResolvesToThisRepositoryInTests()
    {
        // 測試程序沒有 SIMOSA_INSTALL_ROOT 時:執行檔在專案的 build/bin/… 之下 → 專案根目錄(不論專案放在哪個磁碟)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMOSA_INSTALL_ROOT"))) return;
        var root = InstallLayout.DefaultRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "data")), $"預設根目錄 {root} 應含 data/");
        Assert.True(AppContext.BaseDirectory.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), $"預設根目錄 {root} 應為執行檔 {AppContext.BaseDirectory} 的上層");
    }
}
