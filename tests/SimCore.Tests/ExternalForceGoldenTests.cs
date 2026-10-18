using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 外力項 C# 對 Python 參考實作的數值一致性(規劃書第 14 章「C# 核心 vs Python 參考實作(相同係數)軌跡差異 &lt;1%」;
/// 設計文件 physics-tugs-anchors-mooring.md 與 physics-bank-effect.md 的「Python 參考實作」章節)。
/// 黃金檔 Golden/ext_*.csv 與情境 Golden/ext_*.yaml 由 Golden/export_external_reference.py 產生(本測試只讀):
/// 以同一份情境 YAML 建立 MMG 引擎(FSB1 滿載已識別係數、dt 0.02 s),依標頭的 <c># cmd: tick=… {JSON}</c> 在相同 tick 下指令,
/// 每 1 s 比較位置(位置差 / 航程 &lt; 1%)、航向、速度、外力合計 X/Y/N 與各力項(拖船出力/方向、鏈長/水平張力/錨位、碰墊反力、纜張力、
/// 岸壁 Y/N/D)的時間序列,並要求外力事件(tick、kind、itemId)序列相同。
/// </summary>
public class ExternalForceGoldenTests
{
    private readonly ITestOutputHelper _out;
    public ExternalForceGoldenTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] ExternalEventKinds =
    {
        "tugCommand", "tugLineParted", "anchorLetGo", "anchorCommand", "anchorDragging", "anchorHolding", "anchorBrakeSlipping",
        "anchorBroughtUp", "anchorAweigh", "anchorHoused", "anchorChainFullyOut", "anchorWindlassStalled", "fenderContact", "quayContact",
        "mooringCommand", "mooringLineParted",
    };

    private sealed record Golden(string Title, string ScenarioFile, List<(long Tick, SimCommand Command)> Commands,
        List<(long Tick, string Kind, string ItemId)> Events, string[] Columns, List<double[]> Rows);

    private static Golden Load(string file)
    {
        var inv = CultureInfo.InvariantCulture;
        var title = "";
        string? scenario = null;
        var commands = new List<(long, SimCommand)>();
        var events = new List<(long, string, string)>();
        string[]? cols = null;
        var rows = new List<double[]>();
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, file)))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var body = line.TrimStart('#').Trim();
                if (title == "") title = body;
                if (body.StartsWith("scenario:", StringComparison.Ordinal)) scenario = body["scenario:".Length..].Trim();
                else if (body.StartsWith("cmd: tick=", StringComparison.Ordinal))
                {
                    var rest = body["cmd: tick=".Length..];
                    var sp = rest.IndexOf(' ');
                    var cmd = ContractJson.Deserialize<SimCommand>(rest[(sp + 1)..]) ?? throw new InvalidDataException(line);
                    commands.Add((long.Parse(rest[..sp], inv), cmd));
                }
                else if (body.StartsWith("event: tick=", StringComparison.Ordinal))
                {
                    var parts = body["event: tick=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    events.Add((long.Parse(parts[0], inv), parts[1], parts[2]));
                }
                continue;
            }
            if (cols is null) { cols = line.Split(','); continue; }
            rows.Add(line.Split(',').Select(v => double.Parse(v, inv)).ToArray());
        }
        return new Golden(title, scenario ?? throw new InvalidDataException($"{file} 缺少 # scenario:"), commands, events,
            cols ?? throw new InvalidDataException($"{file} 缺少欄名"), rows);
    }

    /// <summary>C# 端對應欄位的值(外力合計以取樣時刻狀態與步首相同的環境取樣計算,與 Python 匯出端相同)。</summary>
    private static double Value(SimulationEngine e, string col)
    {
        var m = e.Motion;
        var ext = e.External;
        switch (col)
        {
            case "tick": return e.Tick;
            case "t": return e.Time;
            case "x": return m.X;
            case "y": return m.Y;
            case "psi_deg": return Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi));
            case "u": return m.U;
            case "v": return m.V;
            case "r": return m.R;
            case "rpm": return e.Actuators.Rpm;
            case "ext_X" or "ext_Y" or "ext_N":
            {
                var env = e.Environment.SampleAt(m.X, m.Y);
                var f = ext.Active ? ext.Evaluate(m, env) : BodyForce.Zero;
                if (e.Bank is { Active: true } b) f += b.Evaluate(m, env);
                return (col == "ext_X" ? f.X : col == "ext_Y" ? f.Y : f.N) / 1000.0;
            }
        }
        var p = col.Split(':');
        switch (p[0])
        {
            case "tug":
            {
                var t = ext.Tug(p[1]) ?? throw new InvalidDataException(col);
                return p[2] == "force_t" ? t.ForceN / TugUnit.TonneForce : Units.NormalizeHeadingDeg(Units.RadToDeg(t.DirectionRad));
            }
            case "anchor":
            {
                var a = ext.Anchor(p[1]) ?? throw new InvalidDataException(col);
                return p[2] switch
                {
                    "length_m" => a.Length,
                    "H_t" => a.Last.H / TugUnit.TonneForce,
                    "anchorX" => a.AnchorX,
                    "anchorY" => a.AnchorY,
                    _ => throw new InvalidDataException(col),
                };
            }
            case "fender": return ext.Berth!.Fenders.First(f => f.Id == p[1]).LastReactionN / 1000.0;
            case "line": return ext.Berth!.Lines.First(l => l.Id == p[1]).LastTensionN / MooringLine.TonneForce;
            case "bank":
            {
                var r = e.Bank!.Last;
                return p[1] switch { "Y_kN" => r.Y / 1000.0, "N_kNm" => r.N / 1000.0, "parameter" => r.D, _ => throw new InvalidDataException(col) };
            }
        }
        throw new InvalidDataException($"未知欄位 {col}");
    }

    private static bool IsAngle(string col) => col == "psi_deg" || col.EndsWith(":direction_deg", StringComparison.Ordinal);

    [Theory]
    [InlineData("ext_tug_push_turn.csv")]
    [InlineData("ext_anchor_wind_drag.csv")]
    [InlineData("ext_berth_fender_lines.csv")]
    [InlineData("ext_bank_suction.csv")]
    public void PythonConsistency_ExternalForcesWithinOnePercent(string file)
    {
        var g = Load(file);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, g.ScenarioFile));
        var e = MmgEngine(sc);
        Assert.Equal(Dt, e.Dt);
        var events = TestData.Capture(e);

        var cols = g.Columns;
        var iTick = Array.IndexOf(cols, "tick");
        // 各欄的尺度(Python 序列的最大絕對值;角度欄以 360°),相對差 = 最大絕對差 / 尺度
        var scale = cols.Select((c, j) => IsAngle(c) ? 360.0 : g.Rows.Max(r => Math.Abs(r[j]))).ToArray();
        var maxDiff = new double[cols.Length];
        var worstTick = new long[cols.Length];
        var cmds = g.Commands.OrderBy(c => c.Tick).ToList();
        var ci = 0;
        var lastTick = (long)g.Rows[^1][iTick];
        var track = 0.0;
        var (px, py) = (e.Motion.X, e.Motion.Y);
        var maxPos = 0.0;
        var maxPosPct = 0.0;
        long posTick = 0;
        var ri = 0;
        for (long tick = 0; tick <= lastTick; tick++)
        {
            if (ri < g.Rows.Count && (long)g.Rows[ri][iTick] == tick)
            {
                var row = g.Rows[ri++];
                for (var j = 0; j < cols.Length; j++)
                {
                    var cs = Value(e, cols[j]);
                    var d = IsAngle(cols[j]) ? Math.Abs(Units.WrapDeg180(cs - row[j])) : Math.Abs(cs - row[j]);
                    if (d > maxDiff[j]) { maxDiff[j] = d; worstTick[j] = tick; }
                }
                var dPos = Math.Sqrt((e.Motion.X - row[Array.IndexOf(cols, "x")]) * (e.Motion.X - row[Array.IndexOf(cols, "x")])
                                     + (e.Motion.Y - row[Array.IndexOf(cols, "y")]) * (e.Motion.Y - row[Array.IndexOf(cols, "y")]));
                maxPos = Math.Max(maxPos, dPos);
                var pct = track > 1.0 ? 100.0 * dPos / track : 0.0;
                if (pct > maxPosPct) { maxPosPct = pct; posTick = tick; }
            }
            if (tick == lastTick) break;
            while (ci < cmds.Count && cmds[ci].Tick == tick) e.Enqueue(cmds[ci++].Command);
            e.Step();
            track += Math.Sqrt((e.Motion.X - px) * (e.Motion.X - px) + (e.Motion.Y - py) * (e.Motion.Y - py));
            (px, py) = (e.Motion.X, e.Motion.Y);
        }
        Assert.Equal(g.Rows.Count, ri);
        Assert.Equal(cmds.Count, ci);
        Assert.DoesNotContain(events, ev => ev.Kind == "commandRejected");

        _out.WriteLine(g.Title);
        _out.WriteLine($"  航程 {track:F1} m;最大位置差 {maxPos:E3} m({maxPosPct:E3}% of track @tick {posTick})");
        _out.WriteLine("  | 欄位 | 最大絕對差 | 尺度 | 相對差 % | @tick |");
        for (var j = 0; j < cols.Length; j++)
        {
            if (cols[j] is "tick" or "t") continue;
            var rel = scale[j] > 0 ? 100.0 * maxDiff[j] / scale[j] : 0.0;
            _out.WriteLine($"  | {cols[j]} | {maxDiff[j]:E3} | {scale[j]:G6} | {rel:E3} | {worstTick[j]} |");
        }

        // 軌跡(規劃書第 14 章):位置差 / 航程 < 1%;航向差 < 1% × 360°
        Assert.True(maxPosPct < 1.0, $"位置差 {maxPosPct:F4}% ≥ 1%");
        // 各時間序列(速度、外力合計、各力項):最大差 < 1% × 該序列最大絕對值
        for (var j = 0; j < cols.Length; j++)
        {
            if (cols[j] is "tick" or "t" or "x" or "y") continue;
            var rel = scale[j] > 1e-9 ? maxDiff[j] / scale[j] : maxDiff[j];
            Assert.True(rel < 0.01, $"{cols[j]}:最大差 {maxDiff[j]:E3}(尺度 {scale[j]:G6})≥ 1%,@tick {worstTick[j]}");
        }

        // 外力事件序列(tick、kind、itemId)相同
        var csEvents = events.Where(ev => ExternalEventKinds.Contains(ev.Kind)).Select(ev => (ev.Tick, ev.Kind, ev.ItemId ?? "")).ToList();
        Assert.Equal(g.Events, csEvents);
    }

    [Fact]
    public void GoldenScenariosUseExternalForcesAndAreDeterministic()
    {
        // 黃金情境本身:外力模組或岸壁啟用(雜湊含外力區塊),且同一輸入重跑雜湊相同
        foreach (var file in new[] { "ext_tug_push_turn.yaml", "ext_anchor_wind_drag.yaml", "ext_berth_fender_lines.yaml", "ext_bank_suction.yaml" })
        {
            var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, file));
            string Run()
            {
                var e = MmgEngine(sc);
                Assert.True(e.External.Active || e.Bank is { Active: true }, file);
                e.Run(500);
                return e.StateHash;
            }
            Assert.Equal(Run(), Run());
        }
    }
}
