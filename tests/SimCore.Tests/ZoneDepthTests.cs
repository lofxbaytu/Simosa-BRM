using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 區域水深覆寫(情境 zones[].waterDepth_m;情境引擎設計第 4.3 節)的 C#↔Python 跨語言夾具:Golden/zone_depth.csv(export_zone_depth_reference.py 產生;
/// 位置 → 有效水深 = 區域基底(在內取最淺,否則 environment.waterDepth)+ 潮高(0))與 C# <see cref="EnvironmentConditions.DepthAt"/> 一致(1e-9)。
/// 取樣點涵蓋重疊區、邊上/頂點、1e-9 門檻、凹多邊形、比基底深的區域(不覆寫)、無 waterDepth_m 區域、非 ukcPolicy 型別、經緯度頂點區域。
/// 其餘(ZoneTracker 的多邊形內外、潮高與區域相加、setEnvironment 只改基底)見 ZoneTrackerTests / TideTests。
/// </summary>
public class ZoneDepthTests
{
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static string GoldenDirectory => Path.Combine(TestData.Root, "tests", "SimCore.Tests", "Golden");

    [Fact]
    public void GoldenZoneDepthMatchesPythonReference()
    {
        var rows = File.ReadLines(Path.Combine(GoldenDirectory, "zone_depth.csv")).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("x,", StringComparison.Ordinal))
            .Select(l => l.Split(',')).Select(c => (X: double.Parse(c[0], CultureInfo.InvariantCulture), Y: double.Parse(c[1], CultureInfo.InvariantCulture),
                Depth: double.Parse(c[2], CultureInfo.InvariantCulture))).ToList();
        Assert.True(rows.Count >= 20);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, "zone_depth.yaml"));
        Assert.Equal(7, sc.Zones.Count);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var zd = Assert.IsType<ZoneDepth>(e.Environment.Depth);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "G" }, zd.Zones.Select(z => z.Id));   // 任何 type 皆參與,F 無 waterDepth_m 不參與
        Assert.Equal(25.0, zd.Base.Depth);
        Assert.Equal(0.7, e.Environment.TideHeightM, 12);
        foreach (var r in rows)
        {
            var d = e.Environment.DepthAt(r.X, r.Y);
            Assert.True(Math.Abs(r.Depth - d) <= 1e-9, $"({r.X:R}, {r.Y:R}): depth Python {r.Depth:R} vs C# {d:R}");
        }
        // 初始位置在區外:tick 0 的狀態輸出為基底 + 潮高;直航平衡轉速以基底計算(與 Python 同)
        var s0 = e.BuildState();
        Assert.Equal(25.7, s0.WaterDepth!.Value, 9);
    }
}
