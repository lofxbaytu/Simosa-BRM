// ShellPaths:安裝根目錄(含 data/ 的上層)、使用者資料夾(預設 build/webview2/<profile>,不寫 C:\)、log 檔、安裝檔路徑
using SimosaBRM.Shell.Core;
using Xunit;

namespace SimosaBRM.Shell.Core.Tests;

public class ShellPathsTests
{
    private static string P(params string[] parts) => Path.GetFullPath(Path.Combine(parts));

    [Fact]
    public void FindInstallRoot_WalksUpToDirectoryWithData()
    {
        var root = P(Path.GetTempPath(), "simosa-root");
        var exe = Path.Combine(root, "app", "shell");
        bool Exists(string d) => string.Equals(d, Path.Combine(root, "data"), StringComparison.Ordinal);
        Assert.Equal(root, ShellPaths.FindInstallRoot(exe, Exists));
        // 儲存庫開發建置:build/bin/Shell.WebView2/Release/net8.0-windows → 往上 5 層
        Assert.Equal(root, ShellPaths.FindInstallRoot(Path.Combine(root, "build", "bin", "Shell.WebView2", "Release", "net8.0-windows"), Exists));
        Assert.Equal(root, ShellPaths.FindInstallRoot(root, Exists));
    }

    [Fact]
    public void FindInstallRoot_FallsBackToStartDirectory_WhenNoData()
    {
        var start = P(Path.GetTempPath(), "nowhere", "app", "shell");
        Assert.Equal(start, ShellPaths.FindInstallRoot(start, _ => false));
        // 超過層數上限也不會丟例外
        Assert.Equal(start, ShellPaths.FindInstallRoot(start, _ => false, maxLevels: 1));
    }

    [Fact]
    public void ResolveProfileDirectory_DefaultUnderBuildWebview2_InsideInstallRoot()
    {
        var root = P(Path.GetTempPath(), "simosa-root");
        var dir = ShellPaths.ResolveProfileDirectory(null, root, "conning-bridge");
        Assert.Equal(Path.Combine(root, "build", "webview2", "conning-bridge"), dir);
        Assert.StartsWith(root, dir);
    }

    [Fact]
    public void ResolveProfileDirectory_ExplicitRelative_IsAgainstCurrentDirectory_AbsoluteAsIs()
    {
        var root = P(Path.GetTempPath(), "simosa-root");
        var cwd = P(Path.GetTempPath(), "cwd");
        Assert.Equal(Path.Combine(cwd, "build", "webview2", "custom"), ShellPaths.ResolveProfileDirectory("build/webview2/custom", root, "x", cwd));
        var abs = P(Path.GetTempPath(), "elsewhere", "profile");
        Assert.Equal(abs, ShellPaths.ResolveProfileDirectory(abs, root, "x", cwd));
        Assert.Equal(Path.Combine(root, "build", "webview2", "x"), ShellPaths.ResolveProfileDirectory("  ", root, "x", cwd));
    }

    [Fact]
    public void LogAndInstallerPaths_InsideInstallRoot()
    {
        var root = P(Path.GetTempPath(), "simosa-root");
        Assert.Equal(Path.Combine(root, "build", "logs", "shell-instructor.log"), ShellPaths.LogFile(root, "instructor"));
        Assert.Equal(Path.Combine(root, "tools", "webview2", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"), ShellPaths.InstallerPath(root));
    }
}
