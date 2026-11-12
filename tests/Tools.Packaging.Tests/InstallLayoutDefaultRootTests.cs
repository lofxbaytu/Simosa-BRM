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
        string? repo = null, bool windows = true, string cwd = "/cwd", string? systemDrive = "C:")
    {
        var set = new HashSet<string>(existing ?? Array.Empty<string>(), StringComparer.Ordinal);
        return InstallLayout.DefaultRoot(env, baseDir, set.Contains, () => repo, windows, cwd, systemDrive);
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
    public void NearestRootWins_WhenAnAncestorOfTheRepositoryLooksInstalled()
    {
        // 審查(第二十八輪):專案的上層(例如磁碟根目錄)剛好同時有 data/ 與 app/ 時,儲存庫內的 simosa-pkg 不可把那個上層當安裝根目錄
        var top = Path.GetFullPath(Path.Combine("/media", "d"));
        var repo = Path.Combine(top, "dev", "Simosa BRM");
        var baseDir = Path.Combine(repo, "build", "bin", "Tools.Packaging.Cli", "Release", "net8.0");
        var existing = new[] { Path.Combine(top, "data"), Path.Combine(top, "app") };
        Assert.Equal(repo, Resolve(baseDir: baseDir, existing: existing, repo: repo));
        // 專案根目錄不在那個上層之下(例如 SIMOSA_BRM_ROOT 指向別處)→ 仍以執行檔所在的安裝根目錄為準
        Assert.Equal(top, Resolve(baseDir: Path.Combine(top, "app", "pkg"), existing: existing, repo: "/somewhere/else"));
        // 安裝根目錄本身就是(或在)專案內:較近的安裝根目錄優先
        var inner = Path.Combine(repo, "build", "release", "2026.11.28");
        Assert.Equal(inner, Resolve(baseDir: Path.Combine(inner, "app", "pkg"), existing: new[] { Path.Combine(inner, "data"), Path.Combine(inner, "app") }, repo: repo));
    }

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
        // Windows 本身裝在 D: 的機器:清單中的 D: 略過 → G:(即使 G: 不存在也不落到系統磁碟)
        Assert.Equal(G, Resolve(existing: new[] { @"D:\", D, @"G:\" }, systemDrive: "D:"));
        Assert.Equal(G, Resolve(existing: new[] { @"D:\", D }, systemDrive: @"D:\"));
        // 未提供系統磁碟(非 Windows 的呼叫端)→ 不排除
        Assert.Equal(D, Resolve(existing: new[] { @"D:\" }, systemDrive: null));
    }

    [Fact]
    public void NonWindowsFallsBackToCurrentDirectory()
        => Assert.Equal("/cwd", Resolve(windows: false, existing: new[] { D, G }));

    [Fact]
    public void LocalConfigFileWithUtf8BomIsAccepted_ButSignedDocumentsAreNot()
    {
        // 審查(第二十八輪):Windows PowerShell 5.1 的 Set-Content -Encoding UTF8、或記事本「UTF-8 with BOM」存的 config/packaging.json
        // 開頭有 BOM;本機設定檔(PackageJson.Load)要能讀,簽章文件與更新包內容(PackageJson.FromBytes)不放寬
        var root = SimosaBRM.SimCore.Ship.RepositoryPaths.FindRoot();
        var dir = Path.Combine(root, "build", "test-tmp", "packaging", "bom-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "packaging.json");
            var json = "{ \"role\": \"ship\", \"shipId\": \"FSB1\", \"deviceId\": \"FSB1-BOM\" }";
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(json)).ToArray());
            var cfg = PackagingConfig.LoadOrDefault(path);
            Assert.Equal("FSB1", cfg.ShipId);
            Assert.Equal("FSB1-BOM", cfg.DeviceId);
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
            Assert.Equal("FSB1-BOM", PackagingConfig.LoadOrDefault(path).DeviceId);
            var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(json)).ToArray();
            Assert.Throws<InvalidDataException>(() => PackageJson.FromBytes<PackagingConfig>(withBom, "簽章文件"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ParameterlessOverloadResolvesToThisRepositoryInTests()
    {
        // 測試程序沒有 SIMOSA_INSTALL_ROOT 時:執行檔在專案的 build/bin/… 之下 → 專案根目錄(不論專案放在哪個磁碟)
        // SIMOSA_BRM_ROOT(RepositoryPaths 優先採用)指向另一份專案時也略過:那是環境設定,不是本函式的行為
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMOSA_INSTALL_ROOT"))) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMOSA_BRM_ROOT"))) return;
        var root = InstallLayout.DefaultRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "data")), $"預設根目錄 {root} 應含 data/");
        Assert.True(AppContext.BaseDirectory.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), $"預設根目錄 {root} 應為執行檔 {AppContext.BaseDirectory} 的上層");
    }
}
