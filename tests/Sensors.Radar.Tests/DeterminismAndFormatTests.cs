using System.Security.Cryptography;
using System.Text.Json;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.SimCore.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>確定性(同種子同影像/同追蹤)、幀格式、JSON 契約(radar.schema.json)、NMEA TTM/TLL、環境檔、效能。</summary>
public class DeterminismAndFormatTests
{
    private readonly ITestOutputHelper _out;
    public DeterminismAndFormatTests(ITestOutputHelper output) => _out = output;

    private static LinearScene BusyScene()
    {
        var s = new LinearScene { OwnSpeedKn = 9, OwnCourseDeg = 20, WindKn = 18, WindFromDeg = 250 };
        s.Targets.Add(new LinearTarget("A", 3000, 6000, 200, 11));
        s.Targets.Add(new LinearTarget("B", -2500, 2500, 100, 7, 30, 7, "fishing"));
        s.Targets.Add(new LinearTarget("C", 500, -1500, 20, 14, 180, 30, "tanker", 10));
        return s;
    }

    private static (string FramesHash, string TracksJson) RunHash(ulong seed)
    {
        var env = RadarEnvironment.Empty();
        env.AddRain(new RainCell(-4000, 6000, 1500, 8));
        env.AddLand(new LandPolygon { Name = "island", HeightM = 40, Points = new[] { (6000.0, 8000.0), (8000.0, 8000.0), (8000.0, 9500.0), (6000.0, 9500.0) } });
        var radar = RadarFactory.X(seed, shadow: true, env: env);
        radar.Apply("{\"cmd\":\"guardZone\",\"enabled\":true,\"inner_nm\":0.5,\"outer_nm\":6}");
        using var sha = SHA256.Create();
        var scene = BusyScene();
        foreach (var r in scene.Run(radar, 0, 40))
            foreach (var f in r.Frames) sha.TransformBlock(f, 0, f.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (Convert.ToHexString(sha.Hash!), RadarJson.Text(radar.Tracks()));
    }

    [Fact]
    public void SameSeedGivesIdenticalImagesAndTracks()
    {
        var a = RunHash(20260505);
        var b = RunHash(20260505);
        var c = RunHash(7);
        Assert.Equal(a.FramesHash, b.FramesHash);
        Assert.Equal(a.TracksJson, b.TracksJson);
        Assert.NotEqual(a.FramesHash, c.FramesHash);
        _out.WriteLine($"影像雜湊 {a.FramesHash[..16]}…;追蹤 {a.TracksJson.Length} 字元");
    }

    [Fact]
    public void FrameHeaderRoundTrips()
    {
        var radar = RadarFactory.X();
        var results = new LinearScene { OwnCourseDeg = 33 }.Run(radar, 0, 0.5);
        var frames = results.SelectMany(r => r.Frames).Select(f => SpokeFrame.Parse(f)).ToList();
        Assert.NotEmpty(frames);
        var f0 = frames[0];
        Assert.Equal('X', f0.RadarId);
        Assert.Equal(2048, f0.SpokesPerRev);
        Assert.Equal(512, f0.Cells);
        Assert.InRange(f0.SpokeCount, 1, radar.Config.BatchSpokes);
        Assert.Equal(6 * 1852f, f0.RangeM, 1);
        Assert.Equal(33f, f0.HeadingDeg, 3);
        Assert.True(f0.Flags.HasFlag(SpokeFrame.FrameFlags.Transmitting));
        Assert.True(f0.Flags.HasFlag(SpokeFrame.FrameFlags.HeadingValid));
        // 方位線連續(相鄰幀首尾相接)
        for (var i = 1; i < frames.Count; i++)
            Assert.Equal((frames[i - 1].FirstSpoke + frames[i - 1].SpokeCount) % 2048, frames[i].FirstSpoke);
        Assert.InRange(frames.Sum(f => f.SpokeCount), 385, 400); // 24 rpm:0–0.48 s 掃過 0.48 × 819.2 ≈ 393 線(第一幀只定起點)
        Assert.Throws<InvalidDataException>(() => SpokeFrame.Parse(new byte[10]));
    }

    [Fact]
    public void JsonMessagesMatchRadarSchema()
    {
        var root = SimosaBRM.SimCore.Ship.RepositoryPaths.FindRoot();
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Contracts", "radar.schema.json")));
        var defs = schema.RootElement.GetProperty("$defs");
        var scene = new LinearScene { OwnSpeedKn = 10 };
        scene.Targets.Add(new LinearTarget("T", 3000, 5000, 250, 10));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        var g = scene.Truth("T", 2);
        radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3}}}");
        radar.Apply("{\"cmd\":\"trialManoeuvre\",\"course\":90,\"speed\":10,\"delay\":1}");
        var events = scene.Run(radar, 2.04, 80).SelectMany(r => r.Events).ToList();

        void Check(string def, object message)
        {
            using var doc = JsonDocument.Parse(RadarJson.Text(message));
            var d = defs.GetProperty(def);
            foreach (var req in d.GetProperty("required").EnumerateArray())
                Assert.True(doc.RootElement.TryGetProperty(req.GetString()!, out _), $"{def} 缺少必要欄位 {req.GetString()}");
            var props = d.GetProperty("properties");
            foreach (var p in doc.RootElement.EnumerateObject())
                Assert.True(props.TryGetProperty(p.Name, out _), $"{def} 輸出了 schema 未定義的欄位 {p.Name}");
        }

        var tracks = radar.Tracks();
        Check("tracksMessage", tracks);
        Check("statusMessage", radar.Status());
        Check("ackMessage", radar.Apply("{\"cmd\":\"gain\",\"value\":60,\"reqId\":1}"));
        Check("eventMessage", radar.EventMessage(events.First()));
        var target = tracks.Targets.Single();
        using (var doc = JsonDocument.Parse(RadarJson.Text(target)))
        {
            var d = defs.GetProperty("track");
            foreach (var req in d.GetProperty("required").EnumerateArray())
                Assert.True(doc.RootElement.TryGetProperty(req.GetString()!, out _), $"track 缺少 {req.GetString()}");
            foreach (var p in doc.RootElement.EnumerateObject())
                Assert.True(d.GetProperty("properties").TryGetProperty(p.Name, out _), $"track 未定義欄位 {p.Name}");
        }
        Assert.Equal("tracking", target.State);
        Assert.NotNull(target.TrialCpaNm);
        // 指令列舉涵蓋所有實作的指令
        var cmds = defs.GetProperty("command").GetProperty("properties").GetProperty("cmd").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var c in new[] { "range", "gain", "sea", "rain", "tune", "standby", "transmit", "acquire", "cancel", "guardZone", "trialManoeuvre", "cpaLimits", "environment", "getStatus", "getTracks" })
            Assert.Contains(c, cmds);
        // 幀標頭長度與 magic 一致
        Assert.Equal(SpokeFrame.HeaderBytes, defs.GetProperty("spokeFrame").GetProperty("properties").GetProperty("headerBytes").GetProperty("const").GetInt32());
    }

    [Fact]
    public void NmeaTtmAndTllAreValid()
    {
        var scene = new LinearScene { OwnSpeedKn = 10 };
        scene.Targets.Add(new LinearTarget("T", 3000, 5000, 250, 10));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        var g = scene.Truth("T", 2);
        radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3}}}");
        scene.Run(radar, 2.04, 5);
        var utc = new DateTime(2026, 5, 5, 22, 30, 0, DateTimeKind.Utc);
        var acquiring = ArpaNmea.Sentences(radar, "RA", utc);
        Assert.Equal(2, acquiring.Count);
        Assert.StartsWith("$RATTM,01,", acquiring[0]);
        Assert.Contains(",Q,", acquiring[0]); // 擷取中
        scene.Run(radar, 5.04, 80);
        var s = ArpaNmea.Sentences(radar, "AR", utc.AddSeconds(80));
        Assert.All(s, x => Assert.True(NmeaSentence.Verify(x), x));
        var ttm = s[0].Split('*')[0].Split(',');
        Assert.Equal("$ARTTM", ttm[0]);
        Assert.Equal("01", ttm[1]);
        Assert.Equal("T", ttm[4]);
        Assert.Equal("N", ttm[10]);
        Assert.Equal("T", ttm[12]); // 追蹤中
        Assert.Equal("M", ttm[15]); // 手動擷取
        var tr = radar.Arpa.Solve(radar.Arpa.Tracks[0], 80, null);
        Assert.Equal(tr.CpaNm, double.Parse(ttm[8], System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.StartsWith("$ARTLL,01,22", s[1]);
        Assert.Contains(",N,120", s[1]);
    }

    [Fact]
    public void AreaFileLoadsLatLonPolygons()
    {
        var root = SimosaBRM.SimCore.Ship.RepositoryPaths.FindRoot();
        var env = RadarEnvironment.Load(Path.Combine(root, "data", "areas"));
        Assert.True(env.LandPolygonCount >= 1);
        env.Anchor(22.53, 120.24, 0, 0);
        Assert.NotEmpty(env.Land);
        // 高雄海岸示意在原點(Charlie 引水站)東方 4–10 km
        var coast = env.Land[0];
        Assert.Contains(coast.Points, p => p.X > 4000 && p.X < 10000 && Math.Abs(p.Y) < 4000);
        Assert.Empty(RadarEnvironment.Load(Path.Combine(root, "data", "no-such-dir")).Land);
        var custom = RadarEnvironment.Empty();
        custom.AddFile("{\"rainCells\":[{\"lat\":22.55,\"lon\":120.24,\"radius_m\":2000,\"rate_mmh\":6}],\"seaState\":3}");
        custom.Anchor(22.53, 120.24, 0, 0);
        var rc = Assert.Single(custom.RainCells);
        Assert.InRange(rc.Y, 2200, 2240);
        Assert.Equal(3.0, custom.SeaStateOverride);
    }

    [Fact]
    public void SingleRadarUsesLessThanOneCore()
    {
        // 一圈(2.5 s 模擬時間、25 Hz 狀態)含顯示影像 512 格 + ARPA 2048 格、3 目標、雨區、陸地、海雜波:處理時間須小於 2.5 s(< 1 核心)
        var env = RadarEnvironment.Empty();
        env.AddRain(new RainCell(-4000, 6000, 2500, 8));
        env.AddLand(new LandPolygon { Name = "coast", HeightM = 30, Points = new[] { (9000.0, -20000.0), (30000.0, -20000.0), (30000.0, 20000.0), (9000.0, 20000.0) } });
        var radar = RadarFactory.X(env: env);
        radar.Apply("{\"cmd\":\"rain\",\"value\":40}");
        var scene = BusyScene();
        scene.Run(radar, 0, 1); // 暖機(JIT)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        scene.Run(radar, 1.04, 1.0 + 3 * radar.Config.ScanPeriodS);
        sw.Stop();
        var perScan = sw.Elapsed.TotalSeconds / 3;
        _out.WriteLine($"每圈處理 {perScan * 1000:F0} ms(掃描週期 {radar.Config.ScanPeriodS * 1000:F0} ms)→ 約 {perScan / radar.Config.ScanPeriodS * 100:F1}% 單核");
        Assert.True(perScan < radar.Config.ScanPeriodS, $"每圈 {perScan:F2} s ≥ 掃描週期");
    }
}
