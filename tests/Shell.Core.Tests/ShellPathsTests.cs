// ShellPaths:安裝根目錄(含 data/ 的上層)、使用者資料夾(預設 build/webview2/<profile>,不寫 C:\)、log 檔、安裝檔路徑
using SimosaBRM.Shell.Core;
using SimosaBRM.SimCore.Ship;
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
    public void ResolveProfileDirectory_ExplicitRelative_IsAgainstInstallRoot_AbsoluteAsIs()
    {
        var root = P(Path.GetTempPath(), "simosa-root");
        var cwd = P(Path.GetTempPath(), "cwd");
        // 相對路徑以安裝根目錄為準(不以目前目錄為準;本機資料守則)
        Assert.Equal(Path.Combine(root, "build", "webview2", "custom"), ShellPaths.ResolveProfileDirectory("build/webview2/custom", root, "x"));
        // 基準可另外指定(Program:找不到安裝根目錄時傳目前目錄)
        Assert.Equal(Path.Combine(cwd, "build", "webview2", "custom"), ShellPaths.ResolveProfileDirectory("build/webview2/custom", root, "x", cwd));
        var abs = P(Path.GetTempPath(), "elsewhere", "profile");
        Assert.Equal(abs, ShellPaths.ResolveProfileDirectory(abs, root, "x", cwd));
        Assert.Equal(Path.Combine(root, "build", "webview2", "x"), ShellPaths.ResolveProfileDirectory("  ", root, "x", cwd));
    }

    [Fact]
    public void TryFindInstallRoot_ReturnsNull_WhenNoData()
    {
        var start = P(Path.GetTempPath(), "nowhere", "app", "shell");
        Assert.Null(ShellPaths.TryFindInstallRoot(start, _ => false));
        var root = P(Path.GetTempPath(), "simosa-root");
        Assert.Equal(root, ShellPaths.TryFindInstallRoot(Path.Combine(root, "app", "shell"), d => d == Path.Combine(root, "data")));
    }

    // ---- 本機資料守則:--profile 在安裝根目錄外而且位於 Windows 系統磁碟時拒絕(注入 Windows + C:,任何作業系統可測)----

    private static readonly LocalDataEnvironment Win = new(true, "C:", false);
    private const string RootD = @"D:\Simosa BRM";

    private static LocalDataTarget Profile(string? profile, string? foundRoot, LocalDataEnvironment env, string? cwd = null)
        => ShellPaths.ProfileDirectory(profile, foundRoot, foundRoot ?? @"C:\Tools\shell", "conning-bridge", env, cwd);

    [Theory]
    [InlineData(@"C:\Users\trainee\AppData\Local\wv2")]
    [InlineData(@"c:/temp/wv2")]
    [InlineData(@"\\?\C:\temp\wv2")]
    [InlineData(@"..\Users\wv2")]                       // 相對路徑以安裝根目錄為準;根目錄在 C: 時 .. 走出根目錄
    public void Profile_OutsideRoot_OnSystemDrive_IsRejected(string profile)
    {
        var t = Profile(profile, @"C:\Simosa BRM", Win);
        Assert.False(t.Allowed);
        var message = LocalDataGuard.RejectionMessage("--profile", t);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", message);
        Assert.Contains("--profile", message);
        Assert.False(Profile(profile.StartsWith("..") ? @"C:\Users\wv2" : profile, RootD, Win).Allowed);
    }

    [Fact]
    public void Profile_AllowedLocations()
    {
        Assert.Equal(@"D:\Simosa BRM\build\webview2\conning-bridge", Profile(null, RootD, Win).Path);   // 預設
        Assert.Equal(LocalDataVerdict.InsideRoot, Profile(null, @"C:\Simosa BRM", Win).Verdict);            // 根目錄在 C:,預設資料夾在根目錄內
        var rel = Profile(@"build\webview2\custom", RootD, Win);
        Assert.Equal(@"D:\Simosa BRM\build\webview2\custom", rel.Path);                                 // 相對 → 安裝根目錄
        Assert.Equal(LocalDataVerdict.InsideRoot, rel.Verdict);
        Assert.Equal(LocalDataVerdict.NotSystemDrive, Profile(@"E:\wv2", RootD, Win).Verdict);             // 隨身碟
        Assert.Equal(LocalDataVerdict.NotSystemDrive, Profile(@"\\server\share\wv2", RootD, Win).Verdict); // UNC
        Assert.Equal(LocalDataVerdict.InsideRoot, Profile(@"C:\Simosa BRM\build\webview2\x", @"C:\Simosa BRM", Win).Verdict);
        Assert.Equal(LocalDataVerdict.AllowedBySwitch, Profile(@"C:\Users\trainee\wv2", RootD, Win with { AllowSystemDrive = true }).Verdict);
        Assert.True(Profile(@"C:\Users\trainee\wv2", RootD, new LocalDataEnvironment(false, null, false)).Allowed);   // 非 Windows 不檢查
    }

    [Fact]
    public void Profile_RootNotFound_RelativeIsAgainstCurrentDirectory()
    {
        // 找不到安裝根目錄:相對路徑以目前目錄為準(維持原行為);所有位置都算根目錄外 → 在系統磁碟時拒絕
        var onC = Profile("wv2", null, Win, @"C:\Users\trainee");
        Assert.Equal(@"C:\Users\trainee\wv2", onC.Path);
        Assert.False(onC.Allowed);
        var onE = Profile("wv2", null, Win, @"E:\work");
        Assert.Equal(@"E:\work\wv2", onE.Path);
        Assert.True(onE.Allowed);
    }

    [Fact]
    public void LogAndInstallerPaths_InsideInstallRoot()
    {
        var root = P(Path.GetTempPath(), "simosa-root");
        Assert.Equal(Path.Combine(root, "build", "logs", "shell-instructor.log"), ShellPaths.LogFile(root, "instructor"));
        // 下載資料夾在安裝根目錄內(不用 Windows 使用者的「下載」資料夾)
        Assert.Equal(Path.Combine(root, "build", "downloads"), ShellPaths.DownloadsDirectory(root));
        Assert.Equal(Path.Combine(root, "tools", "webview2", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"), ShellPaths.InstallerPath(root));
    }
}
