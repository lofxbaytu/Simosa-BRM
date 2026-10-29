using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 練習中變更浪(setEnvironment args.waves;設計文件 docs/design/physics-waves.md「練習中變更」):無浪情境以指令加浪(與情境自帶浪逐位元相同)、
/// 部分更新、Hs 漸變(線性、完成事件、漸變到 0 後關閉)、null / Hs = 0 關閉、不合法 → commandRejected invalidWaves(其他欄位照常)、
/// 快照(變更後才寫 Waves;漸變中途還原續跑位元相同;倒帶到變更前恢復情境浪)、reset 恢復情境浪、紀錄/重播、時間表動作與結構檢查。
/// </summary>
public class WaveEnvironmentCommandTests
{
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    /// <summary>內嵌情境:<paramref name="waves"/> 為 environment 之下的 waves 一行(兩格縮排);<paramref name="extra"/> 為頂層附加區段。</summary>
    private static Scenario.Scenario Yaml(string waves = "", double speedKn = 6.0, string telegraph = "HAH", string extra = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_waves_cmd",
            "seed: 7",
            "ship: { id: FSB1, loading: full }",
            "origin: { lat: 23.80, lon: 120.15 }",
            $"initial: {{ position: {{ x: 0, y: 0 }}, heading: 0, speed: {speedKn.ToString(CultureInfo.InvariantCulture)}, telegraph: {telegraph} }}",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current: { set: 0, drift: 0 }",
            "  waterDepth: 30",
            waves,
            extra,
        }) + "\n").Validate();

    private const string BeamWaves = "  waves: { significantHeight_m: 2.0, peakPeriod_s: 7.0, direction_deg: 90 }";

    private static void Apply(SimulationEngine e, SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }

    [Fact]
    public void CommandAddsWavesToScenarioWithoutWavesBitIdenticalToScenarioWaves()
    {
        // 無浪情境 + 指令在 tick 0 設定與情境相同的浪 → 運動與雜湊逐位元相同(模型以同一設定建立)
        var byScenario = MmgEngine(Yaml(BeamWaves));
        var byCommand = MmgEngine(Yaml());
        Assert.Null(byCommand.Waves);
        var events = TestData.Capture(byCommand);
        Apply(byCommand, SimCommand.SetWaves(2.0, 7.0, 90.0));
        Assert.True(byCommand.Waves is { Active: true });
        Assert.NotNull(byCommand.WaveSettings);
        var change = Assert.Single(events, x => x.Kind == SimEventKinds.EnvironmentChange);
        var waves = change.Data!.Value.GetProperty("changes").GetProperty("waves");
        Assert.Equal(JsonValueKindOf(waves, "from"), System.Text.Json.JsonValueKind.Null);
        Assert.Equal(2.0, waves.GetProperty("to").GetProperty("significantHeight_m").GetDouble());
        Assert.Equal("pm", waves.GetProperty("to").GetProperty("spectrum").GetString());
        for (var i = 0; i < 500; i++) { byScenario.Step(); byCommand.Step(); }
        Assert.Equal(byScenario.Motion, byCommand.Motion);
        Assert.Equal(byScenario.StateHash, byCommand.StateHash);
        var s = byCommand.BuildState();
        Assert.NotNull(s.Waves);
        Assert.Equal(2.0, s.Waves!.SignificantHeightM);
        Assert.Equal(90.0, s.Waves.DirectionDeg);
        Assert.True(s.Waves.YKN < -50.0, $"Y {s.Waves.YKN} kN");
    }

    private static System.Text.Json.JsonValueKind JsonValueKindOf(System.Text.Json.JsonElement o, string name)
        => o.TryGetProperty(name, out var e) ? e.ValueKind : System.Text.Json.JsonValueKind.Null;

    [Fact]
    public void PartialUpdateKeepsOtherFieldsAndNullOrZeroTurnsWavesOff()
    {
        var e = MmgEngine(Yaml(BeamWaves));
        var events = TestData.Capture(e);
        // 只改來向:Hs/Tp 沿用
        Apply(e, SimCommand.SetWaves(directionDeg: 270.0));
        Assert.Equal(2.0, e.Waves!.SignificantHeightM);
        Assert.Equal(7.0, e.Waves.PeakPeriodS);
        Assert.Equal(270.0, e.Waves.DirectionDeg);
        // 浪譜與 γ(即時;大小寫不拘)
        Apply(e, SimCommand.SetWaves(spectrum: "JONSWAP", gamma: 2.0));
        Assert.Equal("jonswap", e.Waves!.Spectrum);
        Assert.Equal(2.0, e.Waves.Gamma);
        Assert.Equal(270.0, e.Waves.DirectionDeg);
        TestData.RunTo(e, 2);
        Assert.NotNull(e.BuildState().Waves);
        var hashWithWaves = e.StateHash;
        // Hs = 0 → 關閉:state.waves 省略、雜湊區塊消失;再設定需 Tp 與來向
        Apply(e, SimCommand.SetWaves(significantHeightM: 0.0));
        Assert.Null(e.Waves);
        Assert.Null(e.WaveSettings);
        Assert.Null(e.BuildState().Waves);
        Assert.DoesNotContain("\"waves\"", ContractJson.Serialize(e.BuildState()));
        e.Step();
        Assert.NotEqual(hashWithWaves, e.StateHash);
        var off = events.Last(x => x.Kind == SimEventKinds.EnvironmentChange);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, JsonValueKindOf(off.Data!.Value.GetProperty("changes").GetProperty("waves"), "to"));
        Assert.Equal(2.0, off.Data!.Value.GetProperty("changes").GetProperty("waves").GetProperty("from").GetProperty("significantHeight_m").GetDouble());
        // 無浪時只給 Hs → 不合法(缺 Tp/來向)→ commandRejected invalidWaves,狀態不變
        var before = events.Count;
        Apply(e, SimCommand.SetWaves(significantHeightM: 1.5));
        Assert.Null(e.Waves);
        var rejected = Assert.Single(events.Skip(before), x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(CommandRejectReasons.InvalidWaves, rejected.DataString("reason"));
        Assert.DoesNotContain(events.Skip(before), x => x.Kind == SimEventKinds.EnvironmentChange);
        // 完整設定再以 waves: null 關閉
        Apply(e, SimCommand.SetWaves(1.5, 6.0, 45.0));
        Assert.True(e.Waves is { Active: true });
        Apply(e, SimCommand.ClearWaves());
        Assert.Null(e.Waves);
    }

    [Theory]
    [InlineData("{\"waves\":{\"significantHeight_m\":-1,\"peakPeriod_s\":7,\"direction_deg\":90}}")]
    [InlineData("{\"waves\":{\"significantHeight_m\":1,\"peakPeriod_s\":0,\"direction_deg\":90}}")]
    [InlineData("{\"waves\":{\"significantHeight_m\":1,\"peakPeriod_s\":7,\"direction_deg\":90,\"spectrum\":\"bretschneider\"}}")]
    [InlineData("{\"waves\":{\"significantHeight_m\":1,\"peakPeriod_s\":7,\"direction_deg\":90,\"gamma\":0.5}}")]
    [InlineData("{\"waves\":{\"significantHeight_m\":1,\"peakPeriod_s\":7,\"direction_deg\":90,\"spectrum\":3}}")]
    [InlineData("{\"waves\":\"rough\"}")]
    [InlineData("{\"waves\":[1,7,90]}")]
    public void InvalidWavesAreRejectedButOtherFieldsStillApply(string argsJson)
    {
        var e = MmgEngine(Yaml());
        var events = TestData.Capture(e);
        var args = SimCommand.RawJson(argsJson.Insert(1, "\"wind\":{\"trueSpeed\":12,\"trueDir\":45},"));
        Apply(e, new SimCommand { Type = SimCommandType.SetEnvironment, Args = args });
        Assert.Null(e.Waves);
        Assert.Equal(Units.KnToMps(12), e.Environment.WindTrueSpeedMps, 9);
        Assert.Single(events, x => x.Kind == SimEventKinds.CommandRejected && x.DataString("reason") == CommandRejectReasons.InvalidWaves);
        var change = Assert.Single(events, x => x.Kind == SimEventKinds.EnvironmentChange);
        Assert.False(change.Data!.Value.GetProperty("changes").TryGetProperty("waves", out _));
    }

    [Fact]
    public void HeightRampIsLinearAndRampToZeroTurnsWavesOff()
    {
        var e = MmgEngine(Yaml(BeamWaves));
        var events = TestData.Capture(e);
        // Hs 2 → 4,來向立即改 180(迎浪),10 s 漸變
        Apply(e, SimCommand.SetWaves(significantHeightM: 4.0, directionDeg: 180.0, rampS: 10.0));
        Assert.Equal(180.0, e.Waves!.DirectionDeg);
        Assert.Equal(2.0, e.Waves.SignificantHeightM);
        var ramp = Assert.Single(e.Ramps);
        Assert.Equal(RampChannel.WaveHeight, ramp.Channel);
        Assert.Equal(2.0, ramp.From);
        Assert.Equal(4.0, ramp.To);
        TestData.RunTo(e, 5);
        e.Step(); // 漸變於每步推進前以當時 tick 插值:tick 250 的值在第 251 步套用
        Assert.Equal(3.0, e.Waves.SignificantHeightM, 9);
        Assert.Equal(3.0, e.BuildState().Waves!.SignificantHeightM, 9);
        Assert.Equal(3.0, e.Environment.WaveHeightM, 9);
        TestData.RunTo(e, 11);
        Assert.Equal(4.0, e.Waves.SignificantHeightM, 12);
        Assert.Empty(e.Ramps);
        var done = Assert.Single(events, x => x.Kind == SimEventKinds.EnvironmentRampDone);
        Assert.Equal("waveHeight", done.DataString("channel"));
        Assert.Equal(4.0, done.DataNumber("to"));
        Assert.Equal(10.0, events.Single(x => x.Kind == SimEventKinds.EnvironmentChange).DataNumber("ramp_s"));
        // 漸變到 0:途中 state.waves 仍輸出、完成後關閉
        Apply(e, SimCommand.ClearWaves(rampS: 4.0));
        Assert.NotNull(e.Waves);
        TestData.RunTo(e, 13);
        e.Step();
        Assert.Equal(2.0, e.Waves!.SignificantHeightM, 9);
        Assert.NotNull(e.BuildState().Waves);
        TestData.RunTo(e, 15.1);
        Assert.Null(e.Waves);
        Assert.Null(e.BuildState().Waves);
        Assert.Empty(e.Ramps);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.EnvironmentRampDone));
        // 無浪時以漸變加浪:自 0 起算
        Apply(e, SimCommand.SetWaves(1.0, 5.0, 0.0, rampS: 2.0));
        Assert.Equal(0.0, e.Waves!.SignificantHeightM);
        Assert.NotNull(e.BuildState().Waves);
        TestData.RunTo(e, 18);
        Assert.Equal(1.0, e.Waves!.SignificantHeightM, 12);
    }

    [Fact]
    public void SnapshotMidRampRestoresBitIdenticalAndRewindBeforeChangeRestoresScenarioWaves()
    {
        var sc = Yaml(BeamWaves);
        var e = MmgEngine(sc);
        TestData.RunTo(e, 5);
        var snapBefore = e.TakeSnapshot();
        Assert.Null(snapBefore.Waves);
        Apply(e, SimCommand.SetWaves(significantHeightM: 3.5, peakPeriodS: 8.0, directionDeg: 135.0, spectrum: "jonswap", rampS: 20.0));
        TestData.RunTo(e, 15);
        var snapMid = e.TakeSnapshot();
        Assert.NotNull(snapMid.Waves);
        Assert.True(snapMid.Waves!.Enabled);
        Assert.InRange(snapMid.Waves.SignificantHeightM, 2.5, 3.0);
        Assert.Equal("jonswap", snapMid.Waves.Spectrum);
        Assert.Contains(snapMid.Timeline!.Ramps, r => r.Channel == RampChannel.WaveHeight);
        TestData.RunTo(e, 40);
        var (hash, last, motion) = (e.StateHash, e.Waves!.Last, e.Motion);
        // 快照 JSON 往返後還原、續跑到 40 s:雜湊、步末結果與運動逐位元相同
        e.Restore(EngineSnapshot.FromJson(snapMid.ToJson()));
        Assert.Equal(snapMid.Tick, e.Tick);
        Assert.Equal(snapMid.Waves.SignificantHeightM, e.Waves!.SignificantHeightM);
        Assert.Equal(8.0, e.Waves.PeakPeriodS);
        Assert.Equal(e.Waves.Compute(e.Motion, e.Environment.DepthAt(e.Motion.X, e.Motion.Y)), e.Waves.Last);
        TestData.RunTo(e, 40);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(last, e.Waves.Last);
        Assert.Equal(motion, e.Motion);
        Assert.Equal(3.5, e.Waves.SignificantHeightM);
        // 倒帶到變更前:快照無 Waves → 恢復情境浪(Hs 2、來向 090、pm),漸變清除
        e.Restore(snapBefore);
        Assert.Equal(2.0, e.Waves!.SignificantHeightM);
        Assert.Equal(90.0, e.Waves.DirectionDeg);
        Assert.Equal("pm", e.Waves.Spectrum);
        Assert.Empty(e.Ramps);
        Assert.Null(e.TakeSnapshot().Waves);
        // 關閉後的快照:Enabled = false;還原後無浪
        Apply(e, SimCommand.ClearWaves());
        var snapOff = e.TakeSnapshot();
        Assert.False(snapOff.Waves!.Enabled);
        Apply(e, SimCommand.SetWaves(1.0, 6.0, 0.0));
        e.Restore(EngineSnapshot.FromJson(snapOff.ToJson()));
        Assert.Null(e.Waves);
        Assert.Null(e.BuildState().Waves);
        // reset 回到情境浪
        e.Reset();
        Assert.Equal(2.0, e.Waves!.SignificantHeightM);
        Assert.Equal(90.0, e.Waves.DirectionDeg);
        Assert.Equal(2.0, e.Environment.WaveHeightM);
    }

    [Fact]
    public void RecordReplayReproducesHashWithWaveCommands()
    {
        var sc = Yaml();
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"waves-cmd-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        try
        {
            TestData.RunTo(e, 5);
            e.Enqueue(SimCommand.SetWaves(2.5, 7.5, 60.0, rampS: 15.0));
            TestData.RunTo(e, 25);
            e.Enqueue(SimCommand.SetWaves(directionDeg: 300.0, spectrum: "jonswap"));
            TestData.RunTo(e, 35);
            e.Enqueue(SimCommand.ClearWaves(rampS: 5.0));
            TestData.RunTo(e, 45);
            e.Enqueue(SimCommand.SetWaves(1.0, 5.0, 180.0));
            TestData.RunTo(e, 50);
            w.Close();
            Assert.NotNull(e.BuildState().Waves);
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TimelineSetEnvironmentWavesActionAppliesWithRamp()
    {
        var e = MmgEngine(Yaml(extra: """
            timeline:
              - id: swell
                when: { type: time, atLeast: 1 }
                actions:
                  - setEnvironment: { waves: { significantHeight_m: 1.5, peakPeriod_s: 8, direction_deg: 45, spectrum: jonswap }, ramp_s: 4 }
              - id: calm
                when: { type: time, atLeast: 10 }
                actions:
                  - setEnvironment: { waves: null }
            """));
        Assert.Empty(e.Scenario.StructuralIssues());
        var events = TestData.Capture(e);
        Assert.Null(e.Waves);
        TestData.RunTo(e, 3);
        Assert.NotNull(e.Waves);
        Assert.Equal("jonswap", e.Waves!.Spectrum);
        Assert.Equal(45.0, e.Waves.DirectionDeg);
        Assert.InRange(e.Waves.SignificantHeightM, 0.5, 1.0);
        TestData.RunTo(e, 6);
        Assert.Equal(1.5, e.Waves.SignificantHeightM, 12);
        var change = Assert.Single(events, x => x.Kind == SimEventKinds.EnvironmentChange && x.ItemId == "swell");
        Assert.Equal(SimEventSources.Timeline, change.Source);
        TestData.RunTo(e, 11);
        Assert.Null(e.Waves);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.EnvironmentChange));
    }

    [Theory]
    [InlineData("{ waves: { significantHeight_m: -1 } }", "timeline[0].actions[0].setEnvironment.waves.significantHeight_m", "≥ 0")]
    [InlineData("{ waves: { peakPeriod_s: 0 } }", "timeline[0].actions[0].setEnvironment.waves.peakPeriod_s", "必須為正")]
    [InlineData("{ waves: { direction_deg: 361 } }", "timeline[0].actions[0].setEnvironment.waves.direction_deg", "0–360")]
    [InlineData("{ waves: { spectrum: bretschneider } }", "timeline[0].actions[0].setEnvironment.waves.spectrum", "pm | jonswap")]
    [InlineData("{ waves: { gamma: 0.5 } }", "timeline[0].actions[0].setEnvironment.waves.gamma", "≥ 1")]
    [InlineData("{ waves: rough }", "timeline[0].actions[0].setEnvironment.waves", "物件或 null")]
    [InlineData("rough", "timeline[0].actions[0]", "setEnvironment 須為物件")]
    public void StructuralIssuesRejectBadTimelineWaves(string payload, string path, string message)
    {
        var sc = ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_waves_tl", "seed: 7", "ship: { id: FSB1, loading: full }", "origin: { lat: 23.80, lon: 120.15 }",
            "initial: { position: { x: 0, y: 0 }, heading: 0, speed: 6, telegraph: HAH }",
            "environment: { wind: { trueSpeed: 0, trueDir: 0 }, current: { set: 0, drift: 0 }, waterDepth: 30 }",
            "timeline:", "  - id: it", "    when: { type: time, atLeast: 1 }", "    actions:", $"      - setEnvironment: {payload}",
        }) + "\n");
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path == path && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
        // 合法形狀(欄位皆選填、null 關閉)無錯誤
        Assert.Empty(ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_waves_tl", "seed: 7", "ship: { id: FSB1, loading: full }", "origin: { lat: 23.80, lon: 120.15 }",
            "initial: { position: { x: 0, y: 0 }, heading: 0, speed: 6, telegraph: HAH }",
            "environment: { wind: { trueSpeed: 0, trueDir: 0 }, current: { set: 0, drift: 0 }, waterDepth: 30 }",
            "timeline:", "  - id: it", "    when: { type: time, atLeast: 1 }", "    actions:",
            "      - setEnvironment: { waves: { significantHeight_m: 0, peakPeriod_s: 5, direction_deg: 360, spectrum: JONSWAP, gamma: 1 }, ramp_s: 10 }",
            "      - setEnvironment: { waves: null }", "      - setEnvironment: { waves: { direction_deg: 90 } }",
        }) + "\n").StructuralIssues());
    }

    [Fact]
    public void UnsupportedDynamicsIgnoreWaveCommandsWithoutChangingOutputs()
    {
        // 不接受外力的動力學:指令照常記錄設定(快照/事件)但不施力、不輸出 state.waves、雜湊不追加
        var sc = TestData.Minimal();
        var e = new SimulationEngine(TestData.Fsb1(), sc, (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        Assert.False(e.Dynamics.SupportsExternalForces);
        var plain = new SimulationEngine(TestData.Fsb1(), sc, (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        Apply(e, SimCommand.SetWaves(2.0, 7.0, 90.0));
        Assert.NotNull(e.WaveSettings);
        Assert.False(e.Waves!.Active);
        for (var i = 0; i < 200; i++) { e.Step(); plain.Step(); }
        Assert.Equal(plain.StateHash, e.StateHash);
        Assert.Null(e.BuildState().Waves);
    }
}
