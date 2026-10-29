// RuntimeCheck.Evaluate:版本字串/例外 → 狀態與中文說明(離線安裝檔路徑、文件章節)
using SimosaBRM.Shell.Core;
using Xunit;

namespace SimosaBRM.Shell.Core.Tests;

public class RuntimeCheckTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "simosa-shell-root");

    [Fact]
    public void Available_WhenVersionReported()
    {
        var r = RuntimeCheck.Evaluate("120.0.2210.91", null, Root);
        Assert.True(r.Available);
        Assert.Equal(RuntimeStatus.Available, r.Status);
        Assert.Equal("120.0.2210.91", r.Version);
        Assert.Contains("120.0.2210.91", r.Message);
    }

    private sealed class WebView2RuntimeNotFoundException(string m) : Exception(m);

    [Fact]
    public void NotInstalled_ByExceptionTypeName_PointsToOfflineInstallerAndDocs()
    {
        var r = RuntimeCheck.Evaluate(null, new WebView2RuntimeNotFoundException("Couldn't find a compatible Webview2 Runtime installation"), Root);
        Assert.False(r.Available);
        Assert.Equal(RuntimeStatus.NotInstalled, r.Status);
        Assert.Contains("WebView2 Runtime", r.Message);
        Assert.Contains("MicrosoftEdgeWebView2RuntimeInstallerX64.exe", r.Message);
        Assert.Contains(Path.Combine(Root, "tools", "webview2"), r.Message);
        Assert.Contains("docs/ops/安裝與啟動.md 第 4.5 節", r.Message);
        Assert.Contains("/silent /install", r.Message);
        Assert.Contains("Couldn't find", r.Message);   // 原始錯誤附在後面
    }

    [Fact]
    public void NotInstalled_WhenVersionEmpty_WithoutException()
    {
        var r = RuntimeCheck.Evaluate("", null, Root);
        Assert.Equal(RuntimeStatus.NotInstalled, r.Status);
        Assert.DoesNotContain("錯誤:", r.Message);
    }

    [Fact]
    public void LoaderMissing_WhenDllNotFound()
    {
        var r = RuntimeCheck.Evaluate(null, new DllNotFoundException("WebView2Loader.dll"), Root);
        Assert.Equal(RuntimeStatus.LoaderMissing, r.Status);
        Assert.Contains("WebView2Loader.dll", r.Message);
        Assert.Contains("release verify", r.Message);
    }

    [Fact]
    public void Failed_ForOtherErrors_EvenWithVersion()
    {
        var r = RuntimeCheck.Evaluate("120.0", new UnauthorizedAccessException("denied"), Root);
        Assert.Equal(RuntimeStatus.Failed, r.Status);
        Assert.Contains("UnauthorizedAccessException", r.Message);
        Assert.Contains("--profile", r.Message);
        Assert.Contains("denied", r.Message);
    }
}
