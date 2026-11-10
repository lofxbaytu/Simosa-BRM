using System.Text.Json;
using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Sync;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 環境版次(作業系統 / .NET / WebView2 Runtime)納入 status 與教官端兩船版次清單(規劃書第 5.5 節;R13;packaging-sync.md 第 7 節):
/// 非 Windows 下 webview2 為 null、os/dotnet 非空、JSON 名稱與往返、fleet-versions.json 保留 environment(舊版船端無此欄位時為 null)。
/// </summary>
public sealed class EnvironmentInfoTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void Collect_OsAndDotnetNonEmpty_CollectedUtcFromClock_WebView2NullOutsideWindows()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 1, 2, 3, TimeSpan.Zero));
        var info = EnvironmentInfo.Collect(clock);
        Assert.False(string.IsNullOrWhiteSpace(info.Os));
        Assert.False(string.IsNullOrWhiteSpace(info.Dotnet));
        Assert.Contains(".NET", info.Dotnet);
        Assert.Equal(new DateTime(2026, 11, 9, 1, 2, 3, DateTimeKind.Utc), info.CollectedUtc);
        if (OperatingSystem.IsWindows())
        {
            // Windows CI/實機:可能已安裝 Evergreen Runtime(登錄檔 pv 可解析為版本)或未安裝(null);兩者都合法
            if (info.WebView2 is { } v) Assert.True(Version.TryParse(v, out _), $"WebView2 版本字串應可解析:{v}");
        }
        else
        {
            Assert.Null(info.WebView2);
            Assert.Null(EnvironmentInfo.DetectWebView2Version());
        }
        Assert.Contains("WebView2", info.Describe());
    }

    [Fact]
    public void NormalizeVersion_PlaceholderAndBlankMeanNotInstalled_RegistryKeysTargetWebView2Client()
    {
        Assert.Null(EnvironmentInfo.NormalizeVersion(null));
        Assert.Null(EnvironmentInfo.NormalizeVersion(""));
        Assert.Null(EnvironmentInfo.NormalizeVersion("  "));
        Assert.Null(EnvironmentInfo.NormalizeVersion("0.0.0.0"));
        Assert.Equal("130.0.2849.56", EnvironmentInfo.NormalizeVersion(" 130.0.2849.56 "));
        Assert.Equal(3, EnvironmentInfo.WebView2RegistryKeys.Length);
        Assert.All(EnvironmentInfo.WebView2RegistryKeys, k => Assert.EndsWith(EnvironmentInfo.WebView2ClientId, k.Path));
        Assert.Contains(EnvironmentInfo.WebView2RegistryKeys, k => k.Hive == "HKLM" && k.Path.Contains("WOW6432Node", StringComparison.Ordinal));
        Assert.Contains(EnvironmentInfo.WebView2RegistryKeys, k => k.Hive == "HKCU");
    }

    [Fact]
    public void ShipStatus_IncludesEnvironment_JsonNamesAndRoundTrip()
    {
        var ship = _env.NewShip();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero));
        var st = new ShipNode(ship, clock).Status();
        Assert.NotNull(st.Environment);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, st.Environment!.CollectedUtc);
        Assert.False(string.IsNullOrWhiteSpace(st.Environment.Os));
        Assert.False(string.IsNullOrWhiteSpace(st.Environment.Dotnet));

        var json = PackageJson.ToText(st);
        using var doc = JsonDocument.Parse(json);
        var env = doc.RootElement.GetProperty("environment");
        Assert.Equal(JsonValueKind.String, env.GetProperty("os").ValueKind);
        Assert.Equal(JsonValueKind.String, env.GetProperty("dotnet").ValueKind);
        Assert.Equal(JsonValueKind.String, env.GetProperty("collectedUtc").ValueKind);
        Assert.False(env.TryGetProperty("webView2", out _)); // 名稱固定為 webview2,不是 camelCase 的 webView2
        if (st.Environment.WebView2 is { } v) Assert.Equal(v, env.GetProperty("webview2").GetString());
        else Assert.False(env.TryGetProperty("webview2", out _)); // null 略過(PackageJson 的 WhenWritingNull)

        var back = PackageJson.FromText<ShipStatus>(json, "status");
        Assert.Equal(st.Environment, back.Environment);
    }

    [Fact]
    public void ShipStatus_JsonWithWebview2_ReadsBack_AndStatusWithoutEnvironmentIsNull()
    {
        var json = """{"type":"statusResult","shipId":"FSB1","environment":{"os":"Microsoft Windows 10.0.26100","dotnet":".NET 8.0.11","webview2":"130.0.2849.56","collectedUtc":"2026-11-09T01:00:00Z"}}""";
        var st = PackageJson.FromText<ShipStatus>(json, "status");
        Assert.Equal("130.0.2849.56", st.Environment!.WebView2);
        Assert.Equal("Microsoft Windows 10.0.26100", st.Environment.Os);
        Assert.Equal(".NET 8.0.11", st.Environment.Dotnet);
        Assert.Equal(new DateTime(2026, 11, 9, 1, 0, 0, DateTimeKind.Utc), st.Environment.CollectedUtc);
        Assert.Equal("Microsoft Windows 10.0.26100;.NET 8.0.11;WebView2 130.0.2849.56", st.Environment.Describe());
        // 舊版船端(第二十五輪以前)的 statusResult / status.json 沒有 environment:仍可讀,欄位為 null
        var old = PackageJson.FromText<ShipStatus>("""{"type":"statusResult","shipId":"FSB1","version":"2026.10.1"}""", "status");
        Assert.Null(old.Environment);
    }

    [Fact]
    public void FleetVersions_UpdateKeepsEnvironmentPerShip_OldEntriesStayNull()
    {
        var store = new RecordStore(_env.PathIn("store"), new ManualClock(new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero)));
        var env = new EnvironmentInfo { Os = "Microsoft Windows 10.0.26100", Dotnet = ".NET 8.0.11", WebView2 = "130.0.2849.56", CollectedUtc = new DateTime(2026, 11, 9, 2, 59, 0, DateTimeKind.Utc) };
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", DeviceId = "FSB1-LT01", Version = "2026.10.1", Environment = env, Via = "tcp" });
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB2", DeviceId = "FSB2-LT01", Version = "2026.10.1", Via = "usb" }); // 舊版船端:無 environment

        var fleet = store.LoadFleet();
        Assert.Equal(env, fleet.Ships["FSB1"].Environment);
        Assert.Null(fleet.Ships["FSB2"].Environment);
        var text = File.ReadAllText(store.FleetPath);
        Assert.Contains("\"environment\"", text);
        Assert.Contains("\"webview2\": \"130.0.2849.56\"", text);

        // 下次同步 FSB1(WebView2 已更新)取代該船項目;FSB2 的項目不受影響
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", DeviceId = "FSB1-LT01", Version = "2026.10.2", Environment = env with { WebView2 = "131.0.2903.70" }, Via = "tcp" });
        fleet = store.LoadFleet();
        Assert.Equal("131.0.2903.70", fleet.Ships["FSB1"].Environment!.WebView2);
        Assert.Equal("2026.10.2", fleet.Ships["FSB1"].Version);
        Assert.Null(fleet.Ships["FSB2"].Environment);
        Assert.Equal("2026.10.1", fleet.Ships["FSB2"].Version);

        // 船端未回報 environment(軟體回復到舊版、隨身碟上是舊格式 status.json):沿用上次已知值與它原本的 collectedUtc,其餘欄位照常取代(審查第二十六輪)
        store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", DeviceId = "FSB1-LT01", Version = "2026.10.9", Via = "usb" });
        fleet = store.LoadFleet();
        Assert.Equal("2026.10.9", fleet.Ships["FSB1"].Version);
        Assert.Equal("usb", fleet.Ships["FSB1"].Via);
        Assert.Equal("131.0.2903.70", fleet.Ships["FSB1"].Environment!.WebView2);
        Assert.Equal(env.CollectedUtc, fleet.Ships["FSB1"].Environment!.CollectedUtc);

        // 教官端 store list 的「版次」行同列環境摘要;從未回報的船顯示「未回報」
        var o = new StringWriter();
        Assert.Equal(0, new CliApp(o, new StringWriter()).Run(new[] { "store", "list", "--store", _env.PathIn("store") }));
        var listed = o.ToString();
        Assert.Contains("版次 FSB1:2026.10.9", listed);
        Assert.Contains("WebView2 131.0.2903.70(收集於 2026-11-09)", listed);
        Assert.Contains("版次 FSB2:2026.10.1", listed);
        Assert.Contains("環境 未回報", listed);
    }
}
