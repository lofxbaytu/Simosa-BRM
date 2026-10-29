// ShellOptions.Parse 與 BuildUrl:參數 → 網址組合(?station=、?ws=、?mode=、?radar=)、--url 優先與頁面推斷、錯誤訊息、視窗標題、使用者資料夾名稱
using SimosaBRM.Shell.Core;
using Xunit;

namespace SimosaBRM.Shell.Core.Tests;

public class ShellOptionsTests
{
    private static ShellOptions Parse(params string[] args)
    {
        var r = ShellOptions.Parse(args);
        Assert.True(r.Ok, r.Error);
        return r.Options!;
    }

    [Fact]
    public void Defaults_ConningBridge_OnLocalWebServer()
    {
        var o = Parse();
        Assert.Equal(ShellPage.Conning, o.Page);
        Assert.Equal(ShellStation.Bridge, o.Station);
        Assert.False(o.Kiosk);
        Assert.False(o.AllowDevTools);
        Assert.Null(o.Profile);
        Assert.Equal("http://127.0.0.1:8080/index.html?station=bridge", o.BuildUrl());
        Assert.Equal("conning-bridge", o.ProfileName);
        Assert.Equal("Simosa BRM 操船台 — 駕駛台 bridge", o.WindowTitle);
    }

    [Fact]
    public void FlagsAcceptSeparateBooleanTokenAndNoCloseConfirm()
    {
        // 審查(第二十輪):Usage 寫「--key value 或 --key=value」,旗標也要接受 --kiosk true;--no-close-confirm 供 stop-all.ps1 的 CloseMainWindow
        var o = Parse("--kiosk", "true", "--page", "radar", "--no-close-confirm");
        Assert.True(o.Kiosk);
        Assert.True(o.NoCloseConfirm);
        var off = Parse("--kiosk", "false", "--allow-devtools", "no", "--no-close-confirm", "0");
        Assert.False(off.Kiosk);
        Assert.False(off.AllowDevTools);
        Assert.False(off.NoCloseConfirm);
        var bare = Parse("--kiosk", "--page", "radar");
        Assert.True(bare.Kiosk);
        Assert.False(bare.NoCloseConfirm);
    }

    [Theory]
    [InlineData("bridge", "bridge", "駕駛台")]
    [InlineData("portWing", "portWing", "左翼")]
    [InlineData("STBDWING", "stbdWing", "右翼")]
    [InlineData("ecr", "ecr", "機控室")]
    public void Station_IsWrittenLikeTheWebPage(string arg, string expectedQuery, string label)
    {
        var o = Parse("--page", "conning", "--station", arg, "--ws", "ws://192.168.77.11:8765");
        Assert.Equal($"http://127.0.0.1:8080/index.html?station={expectedQuery}&ws=ws%3A%2F%2F192.168.77.11%3A8765", o.BuildUrl());
        Assert.Contains(label, o.WindowTitle);
        Assert.Equal($"conning-{expectedQuery.ToLowerInvariant()}", o.ProfileName);
    }

    [Fact]
    public void Instructor_DefaultsToSimpleMode_OnShip()
    {
        var o = Parse("--page=instructor");
        Assert.Equal("http://127.0.0.1:8080/instructor.html?mode=simple", o.BuildUrl());
        Assert.Equal("Simosa BRM 教官站 — simple", o.WindowTitle);
        Assert.Equal("instructor", o.ProfileName);
        var full = Parse("--page", "instructor", "--mode", "FULL", "--ws", "ws://192.168.77.11:8765");
        Assert.Equal("http://127.0.0.1:8080/instructor.html?mode=full&ws=ws%3A%2F%2F192.168.77.11%3A8765", full.BuildUrl());
        var replay = Parse("--page", "instructor", "--mode", "replay");
        Assert.Equal("http://127.0.0.1:8080/instructor.html?mode=replay", replay.BuildUrl());
    }

    [Fact]
    public void Radar_SelectsBand_AndPassesServiceBase()
    {
        var o = Parse("--page", "radar", "--radar", "s", "--ws", "ws://127.0.0.1:8766");
        Assert.Equal("http://127.0.0.1:8080/radar.html?radar=s&ws=ws%3A%2F%2F127.0.0.1%3A8766", o.BuildUrl());
        Assert.Equal("Simosa BRM 雷達 — S 波段", o.WindowTitle);
        Assert.Equal("radar-s", o.ProfileName);
        Assert.Equal("http://127.0.0.1:8080/radar.html?radar=x", Parse("--page", "radar").BuildUrl());
    }

    [Fact]
    public void Report_HasNoQuery_EvenWithWs()
    {
        var o = Parse("--page", "report", "--ws", "ws://127.0.0.1:8765");
        Assert.Equal("http://127.0.0.1:8080/report.html", o.BuildUrl());
        Assert.Equal("Simosa BRM 報告站", o.WindowTitle);
    }

    [Fact]
    public void Base_OverridesWebServer_TrailingSlashTrimmed()
    {
        var o = Parse("--base", "http://192.168.77.11:8080/", "--station", "portWing");
        Assert.Equal("http://192.168.77.11:8080/index.html?station=portWing", o.BuildUrl());
    }

    [Fact]
    public void Url_TakesPrecedence_AndInfersPageStationMode()
    {
        var o = Parse("--url", "http://127.0.0.1:8080/instructor.html?mode=simple&ws=ws://127.0.0.1:8765", "--page", "radar");
        Assert.Equal("http://127.0.0.1:8080/instructor.html?mode=simple&ws=ws://127.0.0.1:8765", o.BuildUrl());
        Assert.Equal(ShellPage.Instructor, o.Page);
        Assert.Equal("simple", o.EffectiveMode);
        Assert.Equal("Simosa BRM 教官站 — simple", o.WindowTitle);

        var c = Parse("--url=http://127.0.0.1:8080/index.html?station=stbdWing");
        Assert.Equal(ShellPage.Conning, c.Page);
        Assert.Equal(ShellStation.StbdWing, c.Station);
        Assert.Equal("conning-stbdwing", c.ProfileName);

        var root = Parse("--url", "http://127.0.0.1:8080/");
        Assert.Equal(ShellPage.Conning, root.Page);

        var unknown = Parse("--url", "http://127.0.0.1:8080/other.html", "--page", "report");
        Assert.Equal(ShellPage.Report, unknown.Page);   // 推斷不出時沿用 --page
    }

    [Fact]
    public void Flags_KioskProfileDevtoolsMonitor()
    {
        var o = Parse("--kiosk", "--profile", "build/webview2/custom", "--allow-devtools", "--monitor", "1");
        Assert.True(o.Kiosk);
        Assert.True(o.AllowDevTools);
        Assert.Equal("build/webview2/custom", o.Profile);
        Assert.Equal(1, o.Monitor);
        Assert.False(Parse("--kiosk=false").Kiosk);
        Assert.True(Parse("--kiosk=1").Kiosk);
        Assert.True(Parse("--help").ShowHelp);
        Assert.True(Parse("-h").ShowHelp);
    }

    [Theory]
    [InlineData(new[] { "--page", "ecdis" }, "--page")]
    [InlineData(new[] { "--station", "wing" }, "--station")]
    [InlineData(new[] { "--mode", "demo" }, "--mode")]
    [InlineData(new[] { "--radar", "k" }, "--radar")]
    [InlineData(new[] { "--ws", "http://127.0.0.1:8765" }, "--ws")]
    [InlineData(new[] { "--url", "127.0.0.1:8080/index.html" }, "--url")]
    [InlineData(new[] { "--base", "ftp://x" }, "--base")]
    [InlineData(new[] { "--monitor", "-1" }, "--monitor")]
    [InlineData(new[] { "--kiosk=maybe" }, "--kiosk")]
    [InlineData(new[] { "--no-close-confirm=maybe" }, "--no-close-confirm")]
    [InlineData(new[] { "--profile" }, "--profile")]
    [InlineData(new[] { "--bogus" }, "--bogus")]
    public void InvalidArguments_ReturnChineseError_NamingTheParameter(string[] args, string expectedInMessage)
    {
        var r = ShellOptions.Parse(args);
        Assert.False(r.Ok);
        Assert.Null(r.Options);
        Assert.Contains(expectedInMessage, r.Error);
    }

    [Fact]
    public void Usage_MentionsEveryParameter()
    {
        foreach (var k in new[] { "--url", "--page", "--station", "--ws", "--mode", "--radar", "--kiosk", "--monitor", "--profile", "--allow-devtools" })
            Assert.Contains(k, ShellOptions.Usage);
        Assert.Contains("F11", ShellOptions.Usage);
        Assert.Contains("Alt+F4", ShellOptions.Usage);
        Assert.Contains("docs/ops/安裝與啟動.md", ShellOptions.Usage);
    }

    [Fact]
    public void InferPage_And_ParseQuery()
    {
        Assert.Equal(ShellPage.Radar, ShellOptions.InferPage(new Uri("http://h/radar.html?radar=x")));
        Assert.Equal(ShellPage.Report, ShellOptions.InferPage(new Uri("http://h/sub/REPORT.html")));
        Assert.Null(ShellOptions.InferPage(new Uri("http://h/x.html")));
        var q = ShellOptions.ParseQuery("?station=portWing&ws=ws%3A%2F%2Fa%3A1&x");
        Assert.Equal("portWing", q["station"]);
        Assert.Equal("ws://a:1", q["ws"]);
        Assert.Equal("", q["x"]);
    }
}
