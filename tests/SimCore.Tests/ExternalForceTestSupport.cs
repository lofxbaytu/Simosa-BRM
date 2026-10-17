using System.Globalization;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 剛體質點模型(測試用):無水動力,u̇ = X/m + v r、v̇ = Y/m − u r、ṙ = N/I;外力的解析比對(質量–彈簧碰墊、纜繩靜平衡)用。
/// </summary>
internal sealed class PointMassDynamics : IShipDynamics
{
    public double Mass { get; }
    public double Inertia { get; }
    public PointMassDynamics(double mass, double inertia) { Mass = mass; Inertia = inertia; }
    public string ModelName => "pointmass/1";
    public bool SupportsExternalForces => true;
    public StateVector Derivative(double t, in StateVector s, in EnvironmentSample env, in ControlInput c) => Derivative(t, s, env, c, BodyForce.Zero);
    public StateVector Derivative(double t, in StateVector s, in EnvironmentSample env, in ControlInput c, in BodyForce f)
    {
        var (dx, dy, dpsi) = IShipDynamics.Kinematics(s, env);
        return new StateVector(f.X / Mass + s.V * s.R, f.Y / Mass - s.U * s.R, f.N / Inertia, dx, dy, dpsi);
    }
}

/// <summary>不支援外力的動力學(包住暫代模型;明確不支援的路徑測試用)。</summary>
internal sealed class NoExternalDynamics : IShipDynamics
{
    private readonly IShipDynamics _inner;
    public NoExternalDynamics(IShipDynamics inner) => _inner = inner;
    public string ModelName => "no-external/1";
    public StateVector Derivative(double t, in StateVector s, in EnvironmentSample env, in ControlInput c) => _inner.Derivative(t, s, env, c);
}

internal static class ExternalForceTestSupport
{
    public const double PointMassKg = 1.0e7;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>FSB1 情境(本地座標、原點 23.80N 120.15E);<paramref name="extra"/> 為附加 YAML(tugs、anchors、berth…)。</summary>
    public static Scenario.Scenario Sc(string extra = "", double x = 0, double y = 0, double headingDeg = 0, double speedKn = 0,
        string telegraph = "STOP", double depth = 20, double currentSet = 0, double currentDriftKn = 0, string loading = "full", int seed = 3, string? seabed = null)
        => ScenarioLoader.FromYaml($$"""
            id: T_ext
            seed: {{seed}}
            ship: { id: FSB1, loading: {{loading}} }
            origin: { lat: 23.80, lon: 120.15 }
            initial: { position: { x: {{F(x)}}, y: {{F(y)}} }, heading: {{F(headingDeg)}}, speed: {{F(speedKn)}}, telegraph: {{telegraph}} }
            environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: {{F(currentSet)}}, drift: {{F(currentDriftKn)}} }, waterDepth: {{F(depth)}}{{(seabed is null ? "" : $", seabed: {seabed}")}} }
            {{extra}}
            """, baseDirectory: TestData.Root).Validate();

    /// <summary>MMG(FSB1 滿載係數,dt 0.02,無自動快照)。</summary>
    public static SimulationEngine Mmg(Scenario.Scenario sc) => MmgTestSupport.MmgEngine(sc);

    /// <summary>剛體質點(m = 1e7 kg,I = m·(25 m)²)。</summary>
    public static SimulationEngine PointMass(Scenario.Scenario sc, double mass = PointMassKg)
        => new(TestData.Fsb1(), sc, (_, _) => new PointMassDynamics(mass, mass * 625.0), new EngineOptions { AutoSnapshotIntervalTicks = 0 });

    /// <summary>
    /// 東西向碼頭(y = 30 m,水域在南)、兩個線性碰墊(x = ±20 m,H 1.0 m、額定 1000 kN @ 72 %);自船航向 090、左舷朝碼頭,
    /// 船舯 y = 19.5 → 左舷距碰墊面 0.5 m。<paramref name="fenders"/> = false 時無碰墊(硬接觸測試)。
    /// </summary>
    public static string LinearBerthYaml(bool fenders = true, string lines = "", double damping = 0.0) => $$"""
        berth:
          definition:
            id: TB
            quay: [ { x: -200, y: 30 }, { x: 200, y: 30 } ]
            waterSide: right
            fenderTypes:
              lin: { height_m: 1.0, width_m: 2.5, ratedReaction_kN: 1000, ratedDeflection_pct: 72, curve: [[0, 0], [72, 100]], friction: 0, damping_kNs_per_m: {{F(damping)}} }
            fenders: {{(fenders ? "[ { id: F1, type: lin, chainage_m: 180 }, { id: F2, type: lin, chainage_m: 220 } ]" : "[]")}}
            bollards: [ { id: B1, chainage_m: 200, setback_m: 3 }, { id: B2, chainage_m: 150, setback_m: 3 } ]
          lines: [ {{lines}} ]
        """;

    /// <summary>推進到 <paramref name="seconds"/>(模擬時間)。</summary>
    public static void RunTo(SimulationEngine e, double seconds) => TestData.RunTo(e, seconds);

    public static void Apply(SimulationEngine e, Contracts.SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }
}
