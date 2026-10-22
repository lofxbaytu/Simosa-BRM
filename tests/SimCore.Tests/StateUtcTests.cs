using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// state.utc(state.schema.json 選填欄位;docs/design/visual-client.md 第 4 節):= 練習開始 UTC + t。
/// 開始時刻:情境 startTimeUtc;未給時引擎以 EngineOptions.Clock(主控程序傳 TimeProvider.System)的現在時刻代入並寫回情境
/// (紀錄標頭因此帶有它,重播得到相同 utc,主控程序的 NMEA 閘道在引擎建立後讀情境 startTimeUtc 也得到同一起點);無時鐘(單元測試、重播)則省略 utc。
/// 不進快照與雜湊(HashCompatibilityTests 另行確認舊情境雜湊不變)。
/// </summary>
public class StateUtcTests
{
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void UtcIsScenarioStartPlusSimulationTime()
    {
        var e = TestData.Engine(); // E01_baseline:startTimeUtc 2026-01-01T02:00:00Z
        Assert.Equal(new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc), e.StartUtc);
        Assert.Equal(DateTimeKind.Utc, e.StartUtc!.Value.Kind);
        Assert.Equal("2026-01-01T02:00:00.000Z", e.BuildState().Utc);
        e.Run(252); // 5.04 s
        Assert.Equal("2026-01-01T02:00:05.040Z", e.BuildState().Utc);
        // JSON 欄位名與格式(ISO 8601、毫秒、Z)
        using var doc = JsonDocument.Parse(ContractJson.Serialize(e.BuildState()));
        Assert.Equal("2026-01-01T02:00:05.040Z", doc.RootElement.GetProperty("utc").GetString());
        Assert.Equal(e.StartUtc!.Value.AddSeconds(5.04), DateTime.Parse(doc.RootElement.GetProperty("utc").GetString()!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    [Fact]
    public void MissingStartTimeUsesClockAndWritesBackToScenario()
    {
        var sc = TestData.Minimal();
        Assert.Null(sc.StartTimeUtc);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 22, 3, 4, 5, 678, TimeSpan.Zero).AddTicks(9999));
        var e = new SimulationEngine(TestData.Fsb1(), sc, options: new EngineOptions { Clock = clock });
        Assert.Equal(new DateTime(2026, 10, 22, 3, 4, 5, 678, DateTimeKind.Utc), e.StartUtc); // 截到毫秒
        Assert.Equal("2026-10-22T03:04:05.678Z", sc.StartTimeUtc);                           // 寫回情境
        Assert.Equal("2026-10-22T03:04:05.678Z", e.BuildState().Utc);
        e.Run(100);
        Assert.Equal("2026-10-22T03:04:07.678Z", e.BuildState().Utc);
        // Reset 不改開始時刻(reset 為可重播的輸入)
        e.Enqueue(SimCommand.Reset());
        e.ProcessPendingCommands();
        Assert.Equal("2026-10-22T03:04:05.678Z", e.BuildState().Utc);
        // 同一情境再建引擎(例如主控程序之後建立 NMEA 閘道讀 startTimeUtc)得到相同起點
        var e2 = new SimulationEngine(TestData.Fsb1(), sc, options: new EngineOptions { Clock = new FixedClock(DateTimeOffset.UtcNow) });
        Assert.Equal(e.StartUtc, e2.StartUtc);
    }

    [Fact]
    public void WithoutClockAndStartTimeUtcIsOmitted()
    {
        // 單元測試與重播的預設(Clock = null):情境無 startTimeUtc → 不以實際時鐘代入、state 省略 utc、情境不被改寫;
        // 同一情境分別建立的引擎其狀態 JSON 因此逐位元相同(SensorFaultTests/TimelineSnapshotTests 的快照還原比較)
        var sc = TestData.Minimal();
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        Assert.Null(e.StartUtc);
        Assert.Null(sc.StartTimeUtc);
        e.Run(10);
        Assert.Null(e.BuildState().Utc);
        Assert.DoesNotContain("\"utc\"", ContractJson.Serialize(e.BuildState()));
        var other = new SimulationEngine(TestData.Fsb1(), TestData.Minimal());
        other.Run(10);
        Assert.Equal(ContractJson.Serialize(e.BuildState()), ContractJson.Serialize(other.BuildState()));
    }

    [Fact]
    public void InvalidStartTimeFallsBackToClock()
    {
        var sc = TestData.Minimal("startTimeUtc: not-a-time");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));
        var e = new SimulationEngine(TestData.Fsb1(), sc, options: new EngineOptions { Clock = clock });
        Assert.Equal("2026-10-22T00:00:00.000Z", e.BuildState().Utc);
        Assert.Equal("2026-10-22T00:00:00.000Z", sc.StartTimeUtc);
    }

    [Fact]
    public void UtcDoesNotEnterHashOrSnapshot()
    {
        // 相同情境、不同開始時刻 → 狀態雜湊相同;快照不含開始時刻(還原後 utc 仍依引擎的 StartUtc)
        var a = new SimulationEngine(TestData.Fsb1(), TestData.Minimal(), options: new EngineOptions { Clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) });
        var b = new SimulationEngine(TestData.Fsb1(), TestData.Minimal(), options: new EngineOptions { Clock = new FixedClock(new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero)) });
        EngineDeterminismTests.RunScript(a, 300);
        EngineDeterminismTests.RunScript(b, 300);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.NotEqual(a.BuildState().Utc, b.BuildState().Utc);
        var snap = a.TakeSnapshot();
        Assert.DoesNotContain("utc", ContractJson.Serialize(snap), StringComparison.OrdinalIgnoreCase);
        a.Run(100);
        a.Restore(snap);
        Assert.Equal("2026-01-01T00:00:06.000Z", a.BuildState().Utc);
    }

    [Fact]
    public void ReplayReproducesUtcFromRecordHeaderNotWallClock()
    {
        var sc = TestData.Minimal(seed: 11);
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 3, 3, 3, 3, TimeSpan.Zero));
        var engine = new SimulationEngine(TestData.Fsb1(), sc, options: new EngineOptions { Clock = clock });
        var path = Path.Combine(TestData.TestRecordsDirectory, $"utc-{Guid.NewGuid():N}.jsonl");
        using (var writer = new RecordWriter(path, engine))
        {
            engine.Run(50);
            engine.Enqueue(SimCommand.Rudder(10));
            engine.ProcessPendingCommands();
            engine.Run(200);
            writer.Close();
        }
        var recorded = RecordReader.ReadStates(path).ToList();
        Assert.NotEmpty(recorded);
        Assert.All(recorded, s => Assert.NotNull(s.Utc));
        Assert.Equal("2026-03-03T03:03:03.040Z", recorded[0].Utc); // 第一個廣播幀在 tick 2(t = 0.04 s)
        Assert.Equal("2026-03-03T03:03:03.000Z", RecordReader.Read(path).Header.Scenario.StartTimeUtc);

        // 重播引擎以紀錄標頭的情境重建(真實時鐘),utc 序列與紀錄逐幀相同
        var replayed = new List<string?>();
        var result = Replayer.Replay(path, TestData.Ship, configure: e => e.Broadcast += (_, s) => replayed.Add(s.Utc));
        Assert.True(result.HashMatches);
        Assert.Equal(recorded.Select(s => s.Utc), replayed);
        File.Delete(path);
    }

    [Fact]
    public void TryParseUtcAcceptsIsoVariants()
    {
        Assert.True(SimulationEngine.TryParseUtc("2026-01-01T10:00:00+08:00", out var a));
        Assert.Equal(new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc), a);
        Assert.True(SimulationEngine.TryParseUtc("2026-01-01T02:00:00", out var b)); // 無時區視為 UTC
        Assert.Equal(a, b);
        Assert.False(SimulationEngine.TryParseUtc(null, out _));
        Assert.False(SimulationEngine.TryParseUtc("", out _));
        Assert.False(SimulationEngine.TryParseUtc("abc", out _));
    }
}
