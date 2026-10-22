using System.Net;
using System.Net.WebSockets;
using System.Text;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Net;
using SimosaBRM.Visual.Shared.Quality;
using SimosaBRM.Visual.Shared.Smoke;
using SimosaBRM.Visual.Shared.State;
using SimosaBRM.Visual.Shared.View;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void ParsesAllDocumentedArguments()
    {
        var o = LaunchOptions.Parse(new[] { "--view=stbdWing", "--monitor", "1", "--ws=192.168.10.2:8765", "--quality=high", "--data-dir=D:/Simosa BRM",
            "--area=kaohsiung-approach,kaohsiung-e20-passage", "--ship=FSB2", "--loading=ballast", "--daynight=night", "--start-utc=2026-05-05T22:30:00Z", "--fullscreen", "--no-hud", "--smoke=50", "--smoke-out=build/visual/x.json", "--res", "1" });
        Assert.Equal(ViewKind.StbdWing, o.View);
        Assert.Equal(1, o.Monitor);
        Assert.Equal("192.168.10.2:8765", o.WebSocketUrl);
        Assert.Equal(QualityTier.High, o.Quality);
        Assert.Equal("D:/Simosa BRM", o.DataDir);
        Assert.Equal(2, o.Areas.Count);
        Assert.Equal("FSB2", o.ShipId);
        Assert.Equal("ballast", o.Loading);
        Assert.Equal(DayNightMode.Night, o.DayNight);
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 22, 30, 0, TimeSpan.Zero), o.StartUtc);
        Assert.True(o.Fullscreen);
        Assert.False(o.Hud);
        Assert.True(o.Smoke);
        Assert.Equal(50, o.SmokeFrames);
        Assert.Equal("build/visual/x.json", o.SmokeOutput);
        Assert.Empty(o.Warnings);
    }

    [Fact]
    public void DefaultsAndInvalidValuesProduceWarnings()
    {
        var o = LaunchOptions.Parse(new[] { "--view=sideways", "--monitor=abc", "--quality=ultra" });
        Assert.Equal(ViewKind.Bridge, o.View);
        Assert.Equal(0, o.Monitor);
        Assert.Null(o.Quality);
        Assert.Equal("ws://127.0.0.1:8765", o.WebSocketUrl);
        Assert.Equal(3, o.Warnings.Count);
        Assert.Equal(new Uri("ws://127.0.0.1:8765"), StateClient.ParseUrl(o.WebSocketUrl));
        Assert.Equal(new Uri("ws://10.0.0.5:8765/"), StateClient.ParseUrl("10.0.0.5:8765/"));
        Assert.Throws<ArgumentException>(() => StateClient.ParseUrl("http://x"));
    }
}

public class EnvironmentMapperTests
{
    [Fact]
    public void VisibilityControlsFogDensity()
    {
        var s = SmokeSequence.Frame(0) with { VisibilityNm = 2.0 };
        var look = EnvironmentMapper.Build(s, null, DayNightMode.Day, null);
        Assert.InRange(look.VisibilityM, 3703.9, 3704.1);
        // 在能見度距離處透射率 5 %
        Assert.InRange(Math.Exp(-look.FogDensity * look.VisibilityM), 0.049, 0.051);
        var clear = EnvironmentMapper.Build(s with { VisibilityNm = null }, null, DayNightMode.Day, null);
        Assert.True(clear.FogDensity < look.FogDensity);
    }

    [Fact]
    public void RainOnlyInsideRainAreas()
    {
        var s = SmokeSequence.Frame(0, withRadarEnvironment: true);
        var radar = s.RadarEnvironment!;
        Assert.Equal(0.0, EnvironmentMapper.RainAt(radar, s.Pos.X, s.Pos.Y)); // 自船在雨區外
        Assert.Equal(0.6, EnvironmentMapper.RainAt(radar, -2000, -2000));
        var poly = new RadarEnvironmentState
        {
            RainAreas = new[] { new RadarRainAreaState { Id = "p", Intensity = 0.3, RateMmH = 4.8, Polygon = new[] { G(0, 0), G(100, 0), G(100, 100), G(0, 100) } } },
        };
        Assert.Equal(0.3, EnvironmentMapper.RainAt(poly, 50, 50));
        Assert.Equal(0.0, EnvironmentMapper.RainAt(poly, 150, 50));
        Assert.Equal(0.0, EnvironmentMapper.RainAt(null, 0, 0));
        static GeoPosition G(double x, double y) => new() { Lat = 0, Lon = 0, X = x, Y = y };
    }

    [Fact]
    public void SeaStateFromWindAndRadarOverride()
    {
        var s = SmokeSequence.Frame(0); // 10 kn
        var look = EnvironmentMapper.Build(s, null, DayNightMode.Day, null);
        Assert.Equal(2.5, look.SeaState);
        Assert.True(look.WaveHeightM > 0);
        Assert.Equal(45, look.WaveDirectionDeg); // 風自 225 → 浪向 045
        var over = EnvironmentMapper.Build(s, new RadarEnvironmentState { RainAreas = Array.Empty<RadarRainAreaState>(), SeaState = 5 }, DayNightMode.Day, null);
        Assert.Equal(5, over.SeaState);
        Assert.Equal(0.0, EnvironmentMapper.SeaStateFromWind(0));
        Assert.Equal(8.0, EnvironmentMapper.SeaStateFromWind(50));
    }

    [Fact]
    public void ManualDayNightModes()
    {
        var s = SmokeSequence.Frame(0);
        Assert.True(EnvironmentMapper.Build(s, null, DayNightMode.Day, null).Daylight > 0.99);
        Assert.False(EnvironmentMapper.Build(s, null, DayNightMode.Day, null).NavigationLightsOn);
        var night = EnvironmentMapper.Build(s, null, DayNightMode.Night, null);
        Assert.Equal(0.0, night.Daylight);
        Assert.True(night.NavigationLightsOn);
        var dusk = EnvironmentMapper.Build(s, null, DayNightMode.Dusk, null);
        Assert.InRange(dusk.Daylight, 0.1, 0.5);
        Assert.True(dusk.NavigationLightsOn);
    }

    [Fact]
    public void SolarPositionMatchesKaohsiungSunriseAndNight()
    {
        // 2026-05-05 22:30Z = 06:30 當地(UTC+8),高雄日出約 05:20 → 太陽已升起、高度角低
        var (el, az) = EnvironmentMapper.SolarPosition(new DateTimeOffset(2026, 5, 5, 22, 30, 0, TimeSpan.Zero), 22.53, 120.24);
        Assert.InRange(el, 5, 25);
        Assert.InRange(az, 60, 100); // 東北東
        var (night, _) = EnvironmentMapper.SolarPosition(new DateTimeOffset(2026, 5, 5, 14, 0, 0, TimeSpan.Zero), 22.53, 120.24); // 22:00 當地
        Assert.True(night < -20);
        var (noon, _) = EnvironmentMapper.SolarPosition(new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero), 22.53, 120.24); // 12:00 當地
        Assert.InRange(noon, 75, 90);
        // auto 模式以 --start-utc + 模擬秒數
        var s = SmokeSequence.Frame(0) with { T = 3600 * 8 };
        var look = EnvironmentMapper.Build(s, null, DayNightMode.Auto, new DateTimeOffset(2026, 5, 5, 22, 30, 0, TimeSpan.Zero)); // 14:30 當地
        Assert.True(look.SunElevationDeg > 50);
        Assert.False(look.NavigationLightsOn);
    }
}

public class SmokeReportTests
{
    [Fact]
    public void ReportIsOkWhenShipMovesAndNodesExist()
    {
        var frames = Enumerable.Range(0, 10).Select(i => new SmokeFrame { Frame = i, SimTime = i * 0.04, FrameTimeMs = 2.0 + i, Nodes = 20, TargetsDrawn = 4, HarbourMeshes = 3, OwnEast = i * 1.0, OwnNorth = 0, Heading = 45 }).ToList();
        var r = SmokeReport.Build(frames, "low", "bridge", new[] { "kaohsiung-approach" }, headless: true, notes: new[] { "x" });
        Assert.True(r.Ok);
        Assert.Equal(11, r.MaxFrameTimeMs);
        Assert.InRange(r.MeanFrameTimeMs, 6.49, 6.51);
        var dir = TestPaths.Scratch("smoke");
        var path = Path.Combine(dir, "smoke.json");
        r.Save(path);
        Assert.Contains("\"ok\": true", File.ReadAllText(path));
        Directory.Delete(dir, true);
        var bad = SmokeReport.Build(Array.Empty<SmokeFrame>(), "low", "bridge", Array.Empty<string>(), true, Array.Empty<string>());
        Assert.False(bad.Ok);
    }
}

public class StateClientTests
{
    [Fact]
    public void HandleTextRoutesFramesEventsAndGarbage()
    {
        var clock = 0.0;
        var interp = new StateInterpolator();
        var client = new StateClient(new Uri("ws://127.0.0.1:1"), interp, () => clock);
        client.HandleText(ContractJson.Serialize(SmokeSequence.Frame(0)));
        clock = 0.04;
        client.HandleText(ContractJson.Serialize(SmokeSequence.Frame(1)));
        client.HandleText("{\"type\":\"event\",\"event\":{\"kind\":\"cpaAlarm\"}}");
        client.HandleText("{\"type\":\"assessment\"}");
        client.HandleText("garbage");
        client.HandleText("{\"t\":1,\"pos\":5}");
        Assert.Equal(2, client.FramesReceived);
        Assert.Equal("cpaAlarm", client.LastEventKind);
        Assert.Equal(2, client.BadFrames);
        Assert.NotNull(client.Sample(0.04));
    }

    [Fact]
    public async Task ConnectsReceivesFramesAndReconnectsAfterServerClose()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var served = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            for (var conn = 0; conn < 2; conn++)
            {
                var ctx = await listener.GetContextAsync();
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                if (conn == 1) secondConnection.TrySetResult(true);
                for (var i = 0; i < 3; i++)
                {
                    var frame = Encoding.UTF8.GetBytes(ContractJson.Serialize(SmokeSequence.Frame(i + conn * 100)));
                    await ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                if (conn == 0) served.TrySetResult(3);
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"event\",\"event\":{\"kind\":\"phase\"}}"), WebSocketMessageType.Text, true, CancellationToken.None);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(2000).Token); }
                catch (Exception) { /* 客戶端可能先關 */ }
                ws.Dispose();
            }
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var interp = new StateInterpolator();
        await using var client = new StateClient(new Uri($"ws://localhost:{port}/"), interp, () => sw.Elapsed.TotalSeconds) { InitialBackoffSeconds = 0.1, MaxBackoffSeconds = 0.2 };
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        client.Log += m => logs.Enqueue(m);
        client.Start();
        try
        {
            await served.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await secondConnection.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}; connection={client.Connection}; frames={client.FramesReceived}; logs: {string.Join(" | ", logs)}; serverTask={serverTask.Status}", ex);
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.FramesReceived < 6 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(6, client.FramesReceived);
        Assert.True(client.Reconnects >= 1);
        Assert.Equal("phase", client.LastEventKind);
        Assert.NotNull(client.Sample(sw.Elapsed.TotalSeconds));
        listener.Stop();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
