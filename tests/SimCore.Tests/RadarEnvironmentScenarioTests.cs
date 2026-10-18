using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 情境雷達環境 environment.radar(雨區多邊形/圓、強度 0–1、海況;docs/design/radar-arpa.md 第 2.1 節)→ state.radarEnvironment:
/// 換算本地 ENU、降雨率 16 × intensity、半徑 nm → m;結構檢查;不進雜湊(與無此區段者雜湊逐步相同)、未給時 state 無此欄位;
/// 紀錄 header 含定義、重播雜湊一致;S 頻故障名稱在目錄中且只記錄。
/// </summary>
public class RadarEnvironmentScenarioTests
{
    private readonly ITestOutputHelper _out;
    public RadarEnvironmentScenarioTests(ITestOutputHelper output) => _out = output;

    private const string RadarYaml = """
          radar:
            seaState: 4
            rainAreas:
              - { id: SQ1, intensity: 0.75, polygon: [ { lat: 23.82, lon: 120.04 }, { lat: 23.82, lon: 120.06 }, { lat: 23.84, lon: 120.06 }, { lat: 23.84, lon: 120.04 } ] }
              - { intensity: 0.25, center: { x: 3000, y: -2000 }, radius_nm: 1.5 }
        """;

    private static Scenario.Scenario Sc(string radar = RadarYaml) => ScenarioLoader.FromYaml($$"""
        id: T_radar_env
        seed: 7
        ship: { id: FSB1, loading: ballast }
        origin: { lat: 23.80, lon: 120.05 }
        initial: { position: { x: 0, y: 0 }, heading: 30, speed: 8, telegraph: HAH }
        environment:
          wind: { trueSpeed: 12, trueDir: 40, gustiness: 0.2 }
          current: { set: 0, drift: 0 }
          waterDepth: 30
        {{radar}}
        """).Validate();

    [Fact]
    public void StateCarriesRainAreasInLocalEnu()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Sc());
        e.Step();
        var re = e.BuildState().RadarEnvironment!;
        Assert.Equal(4.0, re.SeaState);
        Assert.Equal(2, re.RainAreas.Count);
        var sq = re.RainAreas[0];
        Assert.Equal("SQ1", sq.Id);
        Assert.Equal(0.75, sq.Intensity);
        Assert.Equal(12.0, sq.RateMmH, 12);
        var ltp = new LocalTangentPlane(23.80, 120.05);
        var (x0, y0) = ltp.ToLocal(23.82, 120.04);
        Assert.Equal(x0, sq.Polygon![0].X, 9);
        Assert.Equal(y0, sq.Polygon[0].Y, 9);
        Assert.Equal(23.82, sq.Polygon[0].Lat);
        Assert.Null(sq.Center);
        var c = re.RainAreas[1];
        Assert.Equal("rain2", c.Id);                   // 未給 id 時依序號命名
        Assert.Equal(4.0, c.RateMmH, 12);
        Assert.Equal(1.5 * 1852, c.RadiusM!.Value, 9);
        Assert.Equal((3000.0, -2000.0), (c.Center!.X, c.Center.Y));
        var (lat, lon) = ltp.ToGeodetic(3000, -2000);
        Assert.Equal(lat, c.Center.Lat, 9);
        Assert.Equal(lon, c.Center.Lon, 9);
        Assert.Null(c.Polygon);
        // 狀態 JSON 往返(雷達服務以 OwnShipState 反序列化)
        var json = ContractJson.Serialize(e.BuildState());
        _out.WriteLine(JsonSerializer.Serialize(re, ContractJson.Options));
        Assert.Contains("\"radarEnvironment\":{\"rainAreas\":[{\"id\":\"SQ1\",\"intensity\":0.75,\"rate_mmh\":12,\"polygon\":[", json);
        var back = ContractJson.Deserialize<OwnShipState>(json)!.RadarEnvironment!;
        Assert.Equal(re.RainAreas[1].RadiusM, back.RainAreas[1].RadiusM);
        Assert.Equal(4.0, back.SeaState);
    }

    [Fact]
    public void RadarEnvironmentDoesNotAffectHashOrMotionAndIsOmittedWhenAbsent()
    {
        var with = new SimulationEngine(TestData.Fsb1(), Sc());
        var without = new SimulationEngine(TestData.Fsb1(), Sc(radar: ""));
        for (var i = 0; i < 500; i++)
        {
            with.Step();
            without.Step();
            Assert.Equal(without.StateHash, with.StateHash);
        }
        Assert.Equal(without.Motion, with.Motion);
        Assert.Null(without.BuildState().RadarEnvironment);
        Assert.DoesNotContain("radarEnvironment", ContractJson.Serialize(without.BuildState()));
        Assert.Null(without.Scenario.Environment.Radar);
        Assert.DoesNotContain("\"radar\"", JsonSerializer.Serialize(without.Scenario, ContractJson.Options));
        // 既有情境(未使用者)state 無此欄位
        var e01 = new SimulationEngine(TestData.Fsb1(), TestData.Baseline());
        e01.Step();
        Assert.Null(e01.BuildState().RadarEnvironment);
    }

    [Theory]
    [InlineData("{ intensity: 1.5, center: { x: 0, y: 0 }, radius_nm: 1 }", "intensity")]
    [InlineData("{ intensity: 0.5, center: { x: 0, y: 0 }, radius_nm: 1, polygon: [ { x: 0, y: 0 }, { x: 1, y: 0 }, { x: 1, y: 1 } ] }", "二擇一")]
    [InlineData("{ intensity: 0.5 }", "二擇一")]
    [InlineData("{ intensity: 0.5, polygon: [ { x: 0, y: 0 }, { x: 1, y: 0 } ] }", "3 點")]
    [InlineData("{ intensity: 0.5, center: { x: 0, y: 0 }, radius_nm: 0 }", "radius_nm")]
    [InlineData("{ intensity: 0.5, radius_nm: 1 }", "center")]
    public void StructuralErrorsAreReported(string area, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Sc($"""
              radar:
                rainAreas:
                  - {area}
            """));
        Assert.Contains("environment.radar.rainAreas[0]", ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void SeaStateAndDuplicateIdsAreChecked()
    {
        Assert.Contains("seaState", Assert.Throws<InvalidDataException>(() => Sc("""
              radar: { seaState: 10, rainAreas: [] }
            """)).Message);
        Assert.Contains("id 重複", Assert.Throws<InvalidDataException>(() => Sc("""
              radar:
                rainAreas:
                  - { id: A, intensity: 0.5, center: { x: 0, y: 0 }, radius_nm: 1 }
                  - { id: A, intensity: 0.5, center: { x: 9000, y: 0 }, radius_nm: 1 }
            """)).Message);
    }

    [Fact]
    public void RecordHeaderCarriesRadarEnvironmentAndReplayMatches()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"radar-env-{Guid.NewGuid():N}.jsonl");
        var e = new SimulationEngine(TestData.Fsb1(), Sc());
        using (var w = new RecordWriter(path, e))
        {
            TestData.RunTo(e, 20);
            w.Close();
        }
        try
        {
            using var header = JsonDocument.Parse(File.ReadLines(path).First());
            var radar = header.RootElement.GetProperty("scenario").GetProperty("environment").GetProperty("radar");
            Assert.Equal(2, radar.GetProperty("rainAreas").GetArrayLength());
            Assert.Equal(1.5, radar.GetProperty("rainAreas")[1].GetProperty("radius_nm").GetDouble());
            Assert.Contains(File.ReadLines(path), l => l.Contains("\"kind\":\"state\"") && l.Contains("\"radarEnvironment\""));
            var r = Replayer.Replay(path, TestData.Ship);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// state.radarEnvironment 內容只來自情境、練習中不變,廣播(與紀錄)只在關鍵幀帶出:載入/重設/倒帶後第一幀,
    /// 之後每秒(50 步)一次;其他幀省略(雷達服務視為不變)。直接 BuildState() 仍帶完整內容;無 environment.radar 時永不出現。
    /// </summary>
    [Fact]
    public void BroadcastCarriesRadarEnvironmentOnlyOnKeyframes()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Sc());
        var frames = new List<(long Tick, bool Has)>();
        e.Broadcast += (_, s) => frames.Add((s.Tick, s.RadarEnvironment is not null));
        TestData.RunTo(e, 60);
        Assert.Equal(1500, frames.Count);
        Assert.True(frames[0].Has);
        var keys = frames.Where(f => f.Has).Select(f => f.Tick).ToList();
        Assert.InRange(keys.Count, 60, 61);
        Assert.All(keys.Zip(keys.Skip(1)), p => Assert.InRange(p.Second - p.First, 1, 50));
        Assert.True(frames[^1].Tick - keys[^1] < 50);
        Assert.NotNull(e.BuildState().RadarEnvironment);

        // 倒帶後第一幀帶出
        frames.Clear();
        Assert.True(e.RestoreFromHistory(1000));
        e.Step(); e.Step();
        Assert.True(frames[0].Has);
        // 重設後第一幀帶出
        frames.Clear();
        e.Reset();
        e.Step(); e.Step();
        Assert.True(frames[0].Has);

        // 無 environment.radar 的情境:任何廣播幀都沒有此欄位
        var without = new SimulationEngine(TestData.Fsb1(), Sc(radar: ""));
        var any = false;
        without.Broadcast += (_, s) => any |= s.RadarEnvironment is not null;
        TestData.RunTo(without, 5);
        Assert.False(any);
    }

    /// <summary>紀錄狀態行不再每幀重複雨區定義:有/無 environment.radar 的 60 s 紀錄,每行平均大小相差 &lt; 5 %。</summary>
    [Fact]
    public void RecordStateLinesDoNotRepeatRadarEnvironmentEveryFrame()
    {
        double AvgStateBytes(string radar)
        {
            var path = Path.Combine(TestData.TestRecordsDirectory, $"radar-env-size-{Guid.NewGuid():N}.jsonl");
            var e = new SimulationEngine(TestData.Fsb1(), Sc(radar));
            using (var w = new RecordWriter(path, e))
            {
                TestData.RunTo(e, 60);
                w.Close();
            }
            try
            {
                var lines = File.ReadLines(path).Where(l => l.Contains("\"kind\":\"state\"")).ToList();
                Assert.Equal(1500, lines.Count);
                return lines.Average(l => (double)System.Text.Encoding.UTF8.GetByteCount(l));
            }
            finally { File.Delete(path); }
        }
        var with = AvgStateBytes(RadarYaml);
        var without = AvgStateBytes("");
        _out.WriteLine($"每行平均 {with:F0} B(有雨區) vs {without:F0} B(無)");
        Assert.True(with < without * 1.05, $"{with:F0} vs {without:F0}");
    }

    [Fact]
    public void SBandFaultsAreCataloguedLogOnlyAndSchemaDescribesRadarEnvironment()
    {
        Assert.Contains("radarFailureS", ScenarioValidator.FaultCatalogue);
        Assert.Contains("radarDegradedS", ScenarioValidator.FaultCatalogue);
        Assert.DoesNotContain("radarFailureS", FaultNames.Modelled);
        Assert.DoesNotContain("radarDegradedS", FaultNames.Modelled);
        var e = TestData.MinimalEngine();
        e.Enqueue(SimCommand.InjectFault("radarFailureS"));
        e.Step();
        Assert.Contains("radarFailureS", e.BuildState().Faults!);
        using var sc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json")));
        var radar = sc.RootElement.GetProperty("properties").GetProperty("environment").GetProperty("properties").GetProperty("radar");
        foreach (var k in new[] { "polygon", "center", "radius_nm", "intensity" })
            Assert.True(radar.GetProperty("properties").GetProperty("rainAreas").GetProperty("items").GetProperty("properties").TryGetProperty(k, out _), k);
        Assert.True(radar.GetProperty("properties").TryGetProperty("seaState", out _));
        using var st = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "state.schema.json")));
        Assert.True(st.RootElement.GetProperty("properties").TryGetProperty("radarEnvironment", out _));
    }
}
