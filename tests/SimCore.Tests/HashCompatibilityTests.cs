using System.Buffers.Binary;
using System.Security.Cryptography;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>無時間表/航線/區域/規則的情境,狀態鏈雜湊的位元組配置與舊版相同(以獨立實作的舊版雜湊逐步對照)。</summary>
public class HashCompatibilityTests
{
    /// <summary>舊版雜湊:h_n = SHA-256(h_{n−1} ‖ tick ‖ 16 個 double ‖ 各目標 x,y,ψ,V,r)。</summary>
    private static byte[] LegacyNext(byte[] prev, SimulationEngine e)
    {
        var doubles = new List<double>
        {
            e.Motion.U, e.Motion.V, e.Motion.R, e.Motion.X, e.Motion.Y, e.Motion.Psi,
            e.Actuators.RudderDeg, e.Actuators.RudderOrderDeg, e.Actuators.Rpm, e.Actuators.RpmOrder, e.Actuators.ThrusterActual, e.Environment.GustFactor,
            (double)e.Actuators.EngineMode, e.Actuators.EngineTimerSec, e.Actuators.RpmTarget, e.Actuators.RpmTau,
        };
        foreach (var t in e.Traffic.Targets) doubles.AddRange(new[] { t.X, t.Y, t.Psi, t.Speed, t.R });
        var buf = new byte[32 + 8 + doubles.Count * 8];
        prev.CopyTo(buf, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32), e.Tick);
        for (var i = 0; i < doubles.Count; i++)
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40 + i * 8), BitConverter.DoubleToInt64Bits(doubles[i]));
        return SHA256.HashData(buf);
    }

    [Fact]
    public void ScenarioWithoutScenarioEngineKeepsLegacyHashLayout()
    {
        var sc = TestData.Minimal("""
            targets:
              - id: T1
                initial: { position: { x: 500, y: 3000 }, heading: 200, speed: 6 }
            """, seed: 21);
        sc.Environment.Wind.Gustiness = 0.2;
        Assert.False(sc.HasScenarioEngine);
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        var legacy = new byte[32];
        e.Stepped += eng => legacy = LegacyNext(legacy, eng);
        EngineDeterminismTests.RunScript(e, 600);
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 12, rampS: 5)); // 漸變不進無時間表情境的雜湊
        e.ProcessPendingCommands();
        e.Run(400);
        Assert.Equal(Convert.ToHexString(legacy), e.StateHash);
    }

    [Fact]
    public void ScenarioEngineBytesChangeHashOnlyWhenPresent()
    {
        var plain = TestData.MinimalEngine(seed: 4);
        var withRule = TestData.MinimalEngine("assessment:\n  hardFail: [ { id: c, rule: collision } ]", seed: 4);
        Assert.True(withRule.Scenario.HasScenarioEngine);
        plain.Run(100);
        withRule.Run(100);
        Assert.Equal(plain.Motion, withRule.Motion);
        Assert.NotEqual(plain.StateHash, withRule.StateHash);
        // 同樣有規則的兩個引擎雜湊一致
        var again = TestData.MinimalEngine("assessment:\n  hardFail: [ { id: c, rule: collision } ]", seed: 4);
        again.Run(100);
        Assert.Equal(withRule.StateHash, again.StateHash);
        // 教官指令事件(seq)進入雜湊
        var ack = TestData.MinimalEngine("assessment:\n  hardFail: [ { id: c, rule: collision } ]", seed: 4);
        ack.Run(50);
        ack.Enqueue(SimCommand.Acknowledge("verbal"));
        ack.Run(50);
        Assert.Equal(withRule.Motion, ack.Motion);
        Assert.NotEqual(withRule.StateHash, ack.StateHash);
    }

    [Fact]
    public void E05WithoutTimelineIsUnaffectedByEngineUpgrade()
    {
        var sc = Scenario.ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", "E05_pilot_traffic.yaml"));
        Assert.False(sc.HasScenarioEngine);
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        var legacy = new byte[32];
        e.Stepped += eng => legacy = LegacyNext(legacy, eng);
        e.Run(300);
        Assert.Equal(Convert.ToHexString(legacy), e.StateHash);
    }
}
