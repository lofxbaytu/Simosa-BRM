using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 外力場的效能快取(第十八輪已知限制 1;純配置省略,數值不變):
/// <see cref="ShipInteractionModel.TargetProfile"/> 以 (L, B, T, Cb) 快取目標船剖面積曲線(同尺寸回傳同一實例、結果與每次重建逐位元相同),
/// <see cref="SimulationEngine.CombinedForceField"/> 依啟用組合快取 Advance 的合成外力場(連續步同一實例;setEnvironment waves 重建浪模型與快照還原後重組;
/// 無外力情境為 null)。數值不變由既有 WaveDriftTests / ShipInteractionTests / 黃金夾具保證,此處另比對有無快取路徑與重設前後的雜湊。
/// </summary>
public class ForceFieldCacheTests
{
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    private static StateVector At(double psiDeg, double u) => new(u, 0.0, 0.0, 0.0, 0.0, Units.DegToRad(psiDeg));

    private static InteractionTarget Target(in StateVector own, double dx, double dy, double l = 130.0, double b = 22.0, double t = 7.5, double cb = 0.75, string id = "T")
    {
        var (e, n) = BodyFrame.ToEnu(own, dx, dy);
        return new InteractionTarget(id, e, n, own.Psi, Units.KnToMps(12.0), l, b, t, cb);
    }

    private static ShipInteractionModel Model()
        => new(TestData.Ship("FSB1"), LoadingCondition.Full, new ScenarioShipInteraction { Enabled = true }, supported: true);

    /// <summary>內嵌情境:FSB1 滿載 8 kn,選填的浪/船間交互作用/拖船區段。</summary>
    private static Scenario.Scenario Yaml(string waves = "", string interaction = "", string targets = "", string extra = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_field_cache",
            "seed: 7",
            "ship: { id: FSB1, loading: full }",
            "origin: { lat: 23.80, lon: 120.15 }",
            "initial: { position: { x: 0, y: 0 }, heading: 0, speed: 8, telegraph: HAH }",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current: { set: 0, drift: 0 }",
            "  waterDepth: 30",
            waves,
            interaction,
            targets,
            extra,
        }) + "\n");

    private const string BeamWaves = "  waves: { significantHeight_m: 1.5, peakPeriod_s: 7, direction_deg: 90 }";
    private const string Interaction = "  shipInteraction: { enabled: true }";
    private const string Overtaker = """
        targets:
          - id: OVT
            shipType: cargo
            loa: 130
            beam: 22
            draft: 7.5
            initial: { position: { x: -50, y: -200 }, heading: 0, speed: 12 }
            behaviour: { mode: hold }
        """;

    [Fact]
    public void TargetHullProfileIsCachedPerDimensionsAndBitIdentical()
    {
        var m = Model();
        var own = At(0.0, Units.KnToMps(8.0));
        var tgt = Target(own, 0.0, -50.0);
        Assert.Equal(0, m.CachedTargetProfiles);
        var r1 = m.Compute(own, 20.0, new[] { tgt });
        Assert.Equal(1, m.CachedTargetProfiles);
        var p1 = m.TargetProfile(tgt);
        for (var i = 0; i < 50; i++) m.Compute(own, 20.0, new[] { tgt });
        Assert.Equal(1, m.CachedTargetProfiles);                  // 同尺寸不再重建
        Assert.Same(p1, m.TargetProfile(tgt));                    // 同一實例
        // 另一尺寸的目標 → 第二筆;原目標仍為同一實例
        var other = Target(own, 100.0, 60.0, l: 90.0, b: 15.0, t: 5.0, cb: 0.62, id: "U");
        m.Compute(own, 20.0, new[] { tgt, other });
        Assert.Equal(2, m.CachedTargetProfiles);
        Assert.Same(p1, m.TargetProfile(tgt));
        Assert.NotSame(p1, m.TargetProfile(other));
        // 快取實例與直接重建逐位元相同;結果與另一個全新模型(第一次計算)逐位元相同
        var fresh = HullProfile.Build(tgt.LengthM, tgt.BeamM, tgt.DraftM, tgt.Cb, m.Coefficients);
        Assert.Equal(fresh.X, p1.X);
        Assert.Equal(fresh.A, p1.A);
        Assert.Equal(fresh.S, p1.S);
        var r2 = Model().Compute(own, 20.0, new[] { tgt });
        Assert.Equal(r1.X, r2.X);
        Assert.Equal(r1.Y, r2.Y);
        Assert.Equal(r1.N, r2.N);
        Assert.Equal(r1, m.Compute(own, 20.0, new[] { tgt }));
    }

    [Fact]
    public void TargetHullProfileCacheIsBounded()
    {
        var m = Model();
        var own = At(0.0, 4.0);
        for (var i = 0; i < ShipInteractionModel.MaxCachedTargetProfiles + 5; i++)
            m.TargetProfile(Target(own, 0.0, -50.0, l: 100.0 + i));
        Assert.InRange(m.CachedTargetProfiles, 1, ShipInteractionModel.MaxCachedTargetProfiles);
    }

    [Fact]
    public void CombinedForceFieldIsReusedAcrossStepsAndRebuiltAfterWavesChangeAndRestore()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(BeamWaves, Interaction, Overtaker), Mmg);
        Assert.Null(e.CombinedForceField);
        e.Step();
        var f1 = e.CombinedForceField;
        Assert.NotNull(f1);
        Assert.IsType<SumForceField>(f1);                         // 浪 + 船間交互作用
        TestData.RunTo(e, 2);
        Assert.Same(f1, e.CombinedForceField);                    // 啟用組合不變 → 不再配置
        var snap = e.CreateSnapshot();
        // setEnvironment waves 重建浪模型 → 參考不同 → 重組
        e.Enqueue(SimCommand.SetWaves(1.0, 7.0, 90.0));
        e.ProcessPendingCommands();
        e.Step();
        var f2 = e.CombinedForceField;
        Assert.NotNull(f2);
        Assert.NotSame(f1, f2);
        e.Step();
        Assert.Same(f2, e.CombinedForceField);
        // 快照還原:模組重建 → 快取失效,下一步重組
        e.Restore(snap);
        Assert.Null(e.CombinedForceField);
        e.Step();
        Assert.NotSame(f2, e.CombinedForceField);
        Assert.NotSame(f1, e.CombinedForceField);
    }

    [Fact]
    public void CombinedFieldReturnsStableModuleInstancesWhenNoCompositionIsNeeded()
    {
        // 只有船間交互作用 → 場就是模組本身(與舊版相同,無 SumForceField)
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(interaction: Interaction, targets: Overtaker), Mmg);
        e.Step();
        Assert.Same(e.ShipInteraction, e.CombinedForceField);
        // 只有浪 → 浪模型本身
        var w = new SimulationEngine(TestData.Fsb1(), Yaml(BeamWaves), Mmg);
        w.Step();
        Assert.Same(w.Waves, w.CombinedForceField);
        // 無外力 → null(走舊積分多載)
        var plain = new SimulationEngine(TestData.Fsb1(), Yaml(), Mmg);
        TestData.RunTo(plain, 1);
        Assert.Null(plain.CombinedForceField);
    }

    [Fact]
    public void CachedCompositionDoesNotChangeHash()
    {
        // 同一情境兩具引擎:一具中途還原快照(快取失效重組)、一具連續推進 → 同 tick 雜湊相同;浪與交互作用的步末結果也相同
        var a = new SimulationEngine(TestData.Fsb1(), Yaml(BeamWaves, Interaction, Overtaker), Mmg);
        var b = new SimulationEngine(TestData.Fsb1(), Yaml(BeamWaves, Interaction, Overtaker), Mmg);
        TestData.RunTo(a, 10);
        TestData.RunTo(b, 5);
        var snap = b.CreateSnapshot();
        TestData.RunTo(b, 8);
        b.Restore(snap);
        TestData.RunTo(b, 10);
        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(a.BuildState().Waves), ContractJson.Serialize(b.BuildState().Waves));
        Assert.Equal(ContractJson.Serialize(a.BuildState().ShipInteraction), ContractJson.Serialize(b.BuildState().ShipInteraction));
        Assert.Equal(1, a.ShipInteraction!.CachedTargetProfiles);
        Assert.Equal(1, b.ShipInteraction!.CachedTargetProfiles);
    }
}
