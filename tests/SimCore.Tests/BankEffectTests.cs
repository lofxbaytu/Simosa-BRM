using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 岸壁效應(設計文件 docs/design/physics-bank-effect.md 第 7 節):趨勢(吸向岸、艏離岸、∝ u²、距離、遠距為 0、h/T)、左右對稱、
/// 正負號與船體座標慣例、解析值、斜坡/淹沒岸、岸壁起訖的連續性、引擎接線(無 banks 雜湊不變)、快照/重播一致、F03 航道示意岸壁不發散。
/// </summary>
public class BankEffectTests
{
    private const double Depth = 12.5;
    private static readonly LocalTangentPlane Proj = new(23.80, 120.15);

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>南北向直線岸壁 x = <paramref name="east"/>(長 ±<paramref name="half"/> m);east &gt; 0 時在航向北的自船右舷。</summary>
    private static BankDefinition Wall(double east, double half = 3000, string id = "W", string type = "vertical", double? slope = null, double top = 0, double? channel = null)
        => new()
        {
            Id = id, Type = type, Slope = slope, TopDepthM = top, ChannelDepthM = channel,
            Line = new List<ScenarioPosition> { new() { X = east, Y = -half }, new() { X = east, Y = half } },
            // 沿折線(向北)看:岸在東(x > 0)時水域在西 = 左
            WaterSide = east > 0 ? "left" : "right",
        };

    private static BankEffectModel Model(params BankDefinition[] lines) => Model(1.0, lines);

    private static BankEffectModel Model(double scale, params BankDefinition[] lines)
        => new(TestData.Fsb1(), LoadingCondition.Full, new ScenarioBanks { Lines = lines.ToList(), Scale = scale }, Proj, supported: true);

    /// <summary>航向北(ψ = 0)、對水 u、位於 (x, y) 的狀態。</summary>
    private static StateVector Ship(double u, double x = 0, double y = 0, double psiDeg = 0) => new(u, 0, 0, x, y, Units.DegToRad(psiDeg));

    // ------------------------------------------------------------------ 趨勢與正負號

    [Fact]
    public void StarboardBankSucksTowardBankAndPushesBowAway()
    {
        var m = Model(Wall(30));
        var r = m.Compute(Ship(3.0), Depth, 0, detail: true);
        Assert.True(r.Y > 0, $"右舷岸:Y 向右舷(正),實得 {r.Y}");
        Assert.True(r.N < 0, $"艏離岸:N 艏向左(負),實得 {r.N}");
        Assert.True(r.D > 0);
        Assert.Equal(30.0, r.StarboardDistanceM!.Value, 9);
        Assert.Null(r.PortDistanceM);
        // 數量級:FSB1 滿載 6 kn、船側距岸 21 m、h/T 2.2 → 數十 kN(同量級的舵力約 5–10° 舵角)
        Assert.InRange(r.Y / 1000.0, 20.0, 150.0);
        var st = m.State();
        Assert.Equal("none", st.Side); // Last 尚未更新(預設)
        m.UpdateLast(Ship(3.0), Depth, 0);
        st = m.State();
        Assert.Equal("starboard", st.Side);
        Assert.Equal("starboard", st.NearestSide);
        Assert.Equal(30.0, st.DistanceM!.Value, 9);
        Assert.Equal(30.0 - 9.0, st.ClearanceM!.Value, 9);
        Assert.Equal(r.Y / 1000.0, st.YKN, 12);
        Assert.Equal(Depth / 5.701, st.DepthRatio!.Value, 9);
    }

    [Fact]
    public void PortAndStarboardAreMirrorSymmetric()
    {
        foreach (var dist in new[] { 15.0, 25.0, 45.0, 80.0 })
        {
            var s = Model(Wall(dist)).Compute(Ship(3.0), Depth, 90, detail: true);
            var p = Model(Wall(-dist)).Compute(Ship(3.0), Depth, 90, detail: true);
            Assert.Equal(s.Y, -p.Y, 6);
            Assert.Equal(s.N, -p.N, 4);
            Assert.Equal(s.StarboardDistanceM, p.PortDistanceM);
        }
        // 運河中線(兩岸等距):合力為 0;偏向一岸時吸向較近的岸
        var canal = Model(Wall(40, id: "E"), Wall(-40, id: "W"));
        var mid = canal.Compute(Ship(3.0), Depth, 90, detail: true);
        Assert.Equal(0.0, mid.Y, 6);
        Assert.Equal(0.0, mid.N, 4);
        var off = canal.Compute(Ship(3.0, x: 10), Depth, 90, detail: true);
        Assert.True(off.Y > 0 && off.N < 0);
        Assert.Equal(30.0, off.StarboardDistanceM!.Value, 9);
        Assert.Equal(50.0, off.PortDistanceM!.Value, 9);
    }

    [Fact]
    public void ScalesWithSpeedSquaredAtZeroRpm()
    {
        // 深水(200 m):影響寬度不隨 Fnh 變化,Y、N 嚴格 ∝ u²
        var m = Model(Wall(30));
        var r1 = m.Compute(Ship(2.0), 200, 0, detail: false);
        var r2 = m.Compute(Ship(4.0), 200, 0, detail: false);
        Assert.Equal(4.0, r2.Y / r1.Y, 6);
        Assert.Equal(4.0, r2.N / r1.N, 6);
        Assert.Equal(0.0, m.Compute(Ship(0.0), 200, 0, detail: false).Y);
        // 倒退:吸力仍向岸(u²),力矩反號(u|u|:倒退時艉為前導端,艉離岸)
        var back = m.Compute(Ship(-2.0), 200, 0, detail: false);
        Assert.Equal(r1.Y, back.Y, 9);
        Assert.Equal(-r1.N, back.N, 6);
    }

    [Fact]
    public void IncreasesAsDistanceDecreasesAndVanishesBeyondInfluenceWidth()
    {
        var prev = double.PositiveInfinity;
        foreach (var dist in new[] { 12.0, 16.0, 20.0, 30.0, 45.0, 60.0, 80.0 })
        {
            var y = Model(Wall(dist)).Compute(Ship(3.0), Depth, 0, detail: false).Y;
            Assert.True(y > 0 && y < prev, $"距離 {dist} m:Y {y} 應小於較近者 {prev}");
            prev = y;
        }
        // 影響寬度 y_inf = 5B(1 + 16 Fnh⁸) ≈ 90 m:之外完全為 0(N 亦然)
        var far = Model(Wall(95)).Compute(Ship(3.0), Depth, 120, detail: true);
        Assert.True(far.InfluenceWidthM < 95);
        Assert.Equal(0.0, far.Y);
        Assert.Equal(0.0, far.N);
        Assert.Null(far.StarboardDistanceM);
        // 船側 0.1 B 以內以下限計(外推限制):貼岸與 0.6 B 相同
        var atMin = Model(Wall(0.6 * 18)).Compute(Ship(3.0), Depth, 0, detail: false).Y;
        var inside = Model(Wall(9.5)).Compute(Ship(3.0), Depth, 0, detail: false).Y;
        Assert.Equal(atMin, inside, 9);
    }

    [Fact]
    public void StrongerInShallowerWater()
    {
        var m = Model(Wall(30));
        var prev = 0.0;
        foreach (var h in new[] { 60.0, 20.0, 12.5, 8.0, 6.5 }) // h/T 10.5 → 1.14
        {
            // 同一 Fnh 以下的速度,只比較 h/T 的影響
            var r = m.Compute(Ship(1.0), h, 0, detail: false);
            Assert.True(r.Y > prev, $"h = {h}:Y {r.Y} 應大於較深者 {prev}");
            Assert.True(r.N < 0);
            prev = r.Y;
        }
        // h/T 下限 1.05(外推限制):更淺時不再增加
        var lim = m.Compute(Ship(1.0), 1.05 * 5.701, 0, detail: false).Y;
        Assert.Equal(1.0, m.Compute(Ship(1.0), 5.8, 0, detail: false).Y / lim, 6); // 只剩 Fnh⁸ 對影響寬度的微小差異
    }

    [Fact]
    public void MatchesAnalyticValueForLongStraightWall()
    {
        var ship = TestData.Fsb1();
        double L = ship.Hull.LengthBetweenPerpendiculars_m, B = ship.Hull.Breadth_m, T = 5.701, D = 3.7;
        var c = BankEffectCoefficients.Default;
        const double u = 3.0, y = 30.0, rpm = 100.0;
        var r = Model(Wall(y, half: 20000)).Compute(Ship(u), Depth, rpm, detail: false);
        var fnh = u / Math.Sqrt(9.80665 * Depth);
        var yInf = 5.0 * B * (1 + 16 * Math.Pow(fnh, 8));
        var d = 0.5 * B * (1.0 / y - 1.0 / yInf);
        var tau = 1.0 / (Depth / T - 1.0);
        var nD2 = Math.Pow(rpm / 60.0 * D, 2);
        var q = 0.5 * 1025.0 * L * T;
        var yExp = (1 + c.AY * tau) * d * q * (u * u * (c.CYLead + c.CYTrail) + nD2 * c.CYP);
        var nExp = (1 + c.AN * tau) * d * q * L * (u * u * (c.LeverLead * c.CYLead + c.LeverTrail * c.CYTrail) + nD2 * c.LeverP * c.CYP);
        Assert.Equal(0.040, c.CYLead + c.CYTrail, 12);
        Assert.InRange(-(c.LeverLead * c.CYLead + c.LeverTrail * c.CYTrail) / (c.CYLead + c.CYTrail), 0.2, 0.3); // 合力作用點在船舯後約 0.26 L
        Assert.Equal(d, r.D, 9);
        Assert.Equal(r.DFore, r.DAft, 9);
        Assert.Equal(yExp, r.Y, 4);
        Assert.Equal(nExp, r.N, 2);
    }

    [Fact]
    public void PropellerLoadingIncreasesEffectAheadOnly()
    {
        var m = Model(Wall(30));
        var r0 = m.Compute(Ship(3.0), Depth, 0, detail: false);
        var r1 = m.Compute(Ship(3.0), Depth, 120, detail: false);
        var ra = m.Compute(Ship(3.0), Depth, -120, detail: false);
        Assert.True(r1.Y > r0.Y && r1.N < r0.N, "正車載荷增加吸力與艏離岸力矩");
        Assert.Equal(r0.Y, ra.Y); // 倒車不計螺槳項(模型未涵蓋)
        // 停船中正車(踢車)仍有艉部吸力
        var kick = m.Compute(Ship(0.0), Depth, 80, detail: false);
        Assert.True(kick.Y > 0 && kick.N < 0);
    }

    [Fact]
    public void SlopedAndSubmergedBanksUseEquivalentWall()
    {
        // 斜坡 1:4、岸頂露出:等效岸壁在坡腳向陸側 4·h/2 = 25 m
        var sloped = Model(Wall(20, type: "sloped", slope: 4)).Compute(Ship(3.0), Depth, 0, detail: true);
        var wall = Model(Wall(45)).Compute(Ship(3.0), Depth, 0, detail: true);
        Assert.Equal(wall.Y, sloped.Y, 6);
        Assert.Equal(45.0, sloped.StarboardDistanceM!.Value, 9);
        // 淹沒岸(岸頂水深 5 m,航道 12.5 m):強度 × (12.5 − 5)/12.5,等效位置 20 + 4·7.5/2 = 35 m
        var sub = Model(Wall(20, type: "sloped", slope: 4, top: 5, channel: 12.5)).Compute(Ship(3.0), Depth, 0, detail: false);
        var wall35 = Model(Wall(35)).Compute(Ship(3.0), Depth, 0, detail: false);
        Assert.Equal(wall35.Y * 7.5 / 12.5, sub.Y, 6);
        // 岸頂比自船所在水深還深(淹沒於航道底以下):無效應
        Assert.Equal(0.0, Model(Wall(20, top: 13)).Compute(Ship(3.0), Depth, 0, detail: false).Y);
    }

    [Fact]
    public void BankEndsFadeContinuouslyAndObliqueApproachKeepsSigns()
    {
        // 岸壁只到 y = 0(船舯):只有船艉半受吸力(無艏部壓力墊);船完全通過端點(船舯在端點北方 > 1 L)後為 0
        var c = BankEffectCoefficients.Default;
        var full = Model(Wall(30)).Compute(Ship(3.0), Depth, 0, detail: false);
        var endLine = new BankDefinition
        {
            Id = "E", WaterSide = "left",
            Line = new List<ScenarioPosition> { new() { X = 30, Y = -3000 }, new() { X = 30, Y = 0 } },
        };
        var m = Model(endLine);
        var half = m.Compute(Ship(3.0), Depth, 0, detail: false);
        Assert.True(half.DFore == 0 && half.DAft > 0, "岸壁只在後半:前半加權距離為 0");
        Assert.Equal(full.DAft, half.DAft, 9);
        Assert.Equal(full.Y * c.CYTrail / (c.CYLead + c.CYTrail), half.Y, 4);
        Assert.True(half.N < 0, "船艉吸向岸 → 艏離岸");
        // 岸壁自船舯開始(船艏先進入):艏部壓力墊把船艏推離岸(Y 為離岸方向、N 仍艏離岸)
        var start = new BankDefinition { Id = "S", WaterSide = "left", Line = new List<ScenarioPosition> { new() { X = 30, Y = 0 }, new() { X = 30, Y = 3000 } } };
        var entering = Model(start).Compute(Ship(3.0), Depth, 0, detail: false);
        Assert.True(entering.DAft == 0 && entering.DFore > 0);
        Assert.True(entering.Y < 0 && entering.N < 0);
        Assert.Equal(0.0, m.Compute(Ship(3.0, y: 120), Depth, 0, detail: false).Y);
        // 連續性:船沿岸移動 0.1 m,力的變化極小(無階躍)
        var prev = m.Compute(Ship(3.0, y: -150), Depth, 0, detail: false).Y;
        for (var yy = -149.9; yy < 150; yy += 0.1)
        {
            var cur = m.Compute(Ship(3.0, y: yy), Depth, 0, detail: false).Y;
            Assert.True(Math.Abs(cur - prev) < 0.01 * full.Y, $"y = {yy}:{prev} → {cur}");
            prev = cur;
        }
        // 斜向接近(船艏偏向右舷岸 5°):D_fore > D_aft,仍吸向岸、艏離岸
        var oblique = Model(Wall(30)).Compute(Ship(3.0, psiDeg: 5), Depth, 0, detail: false);
        Assert.True(oblique.DFore > oblique.DAft);
        Assert.True(oblique.Y > 0 && oblique.N < 0);
        // 與船垂直的岸(正前方):沿船長投影長度 0,無橫向力
        var ahead = new BankDefinition { Id = "A", WaterSide = "right", Line = new List<ScenarioPosition> { new() { X = -500, Y = 60 }, new() { X = 500, Y = 60 } } };
        Assert.Equal(0.0, Model(ahead).Compute(Ship(3.0), Depth, 0, detail: false).Y, 9);
    }

    [Fact]
    public void ScaleMultipliesAndZeroDisablesForce()
    {
        var one = Model(1.0, Wall(30)).Compute(Ship(3.0), Depth, 60, detail: false);
        var two = Model(2.0, Wall(30)).Compute(Ship(3.0), Depth, 60, detail: false);
        var zero = Model(0.0, Wall(30)).Compute(Ship(3.0), Depth, 60, detail: true);
        Assert.Equal(2.0 * one.Y, two.Y, 9);
        Assert.Equal(2.0 * one.N, two.N, 6);
        Assert.Equal(0.0, zero.Y);
        Assert.Equal(30.0, zero.StarboardDistanceM); // 仍顯示距離
    }

    // ------------------------------------------------------------------ 情境

    private const string WallYaml = """
        banks:
          lines:
            - { id: E1, type: vertical, waterSide: left, line: [ { x: 32, y: -5000 }, { x: 32, y: 8000 } ] }
        """;

    [Fact]
    public void ScenarioLoadsFileAndValidates()
    {
        var sc = Sc("banks: { file: data/areas/mailiao-channel-banks.json, ids: [MLC-N, MLC-S], scale: 1.2 }");
        Assert.Equal(2, sc.Banks!.Definitions!.Count);
        Assert.Equal("sloped", sc.Banks.Definitions[0].Type);
        Assert.Equal(5.0, sc.Banks.Definitions[0].Slope);
        Assert.Equal(12.5, sc.Banks.Definitions[0].ChannelDepthM);
        Assert.Contains("示意", sc.Banks.Definitions[0].Source);
        Assert.Equal(1.2, sc.Banks.Scale);
        var all = Sc("banks: { file: data/areas/mailiao-channel-banks.json }");
        Assert.Equal(3, all.Banks!.Definitions!.Count);
        // JSON 往返(紀錄 header):definitions 內嵌,不再讀檔
        var back = Scenario.Scenario.FromJson(sc.ToJson());
        Assert.Equal(2, back.Banks!.Definitions!.Count);
        Assert.Equal(sc.Banks.Definitions[1].Line.Count, back.Banks.Definitions[1].Line.Count);

        Assert.Throws<InvalidDataException>(() => Sc("banks: { file: data/areas/none.json }"));
        Assert.Throws<InvalidDataException>(() => Sc("banks: { file: data/areas/mailiao-channel-banks.json, ids: [NOPE] }"));
        foreach (var bad in new[]
                 {
                     "banks: { lines: [] }",
                     "banks: { lines: [ { id: A, type: sloped, line: [ { x: 0, y: 0 }, { x: 0, y: 10 } ] } ] }",
                     "banks: { lines: [ { id: A, type: cliff, line: [ { x: 0, y: 0 }, { x: 0, y: 10 } ] } ] }",
                     "banks: { lines: [ { id: A, waterSide: up, line: [ { x: 0, y: 0 }, { x: 0, y: 10 } ] } ] }",
                     "banks: { lines: [ { id: A, line: [ { x: 0, y: 0 } ] } ] }",
                     "banks: { lines: [ { id: A, line: [ { x: 0, y: 0 }, { x: 0, y: 10 } ] }, { id: A, line: [ { x: 5, y: 0 }, { x: 5, y: 10 } ] } ] }",
                     "banks: { scale: -1, lines: [ { id: A, line: [ { x: 0, y: 0 }, { x: 0, y: 10 } ] } ] }",
                     "banks: { lines: [ { id: A, topDepth_m: 6, channelDepth_m: 5, line: [ { x: 0, y: 0 }, { x: 0, y: 10 } ] } ] }",
                 })
            Assert.Throws<InvalidDataException>(() => Sc(bad));
    }

    // ------------------------------------------------------------------ 引擎

    /// <summary>舊版雜湊(與 HashCompatibilityTests 相同的獨立實作)。</summary>
    private static byte[] LegacyNext(byte[] prev, SimulationEngine e)
    {
        var doubles = new List<double>
        {
            e.Motion.U, e.Motion.V, e.Motion.R, e.Motion.X, e.Motion.Y, e.Motion.Psi,
            e.Actuators.RudderDeg, e.Actuators.RudderOrderDeg, e.Actuators.Rpm, e.Actuators.RpmOrder, e.Actuators.ThrusterActual, e.Environment.GustFactor,
            (double)e.Actuators.EngineMode, e.Actuators.EngineTimerSec, e.Actuators.RpmTarget, e.Actuators.RpmTau,
        };
        var buf = new byte[32 + 8 + doubles.Count * 8];
        prev.CopyTo(buf, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32), e.Tick);
        for (var i = 0; i < doubles.Count; i++)
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40 + i * 8), BitConverter.DoubleToInt64Bits(doubles[i]));
        return SHA256.HashData(buf);
    }

    [Fact]
    public void WithoutBanksHashAndStateAreUnchanged()
    {
        foreach (var mmg in new[] { true, false })
        {
            var sc = Sc("", speedKn: 6, telegraph: "SAH", depth: Depth);
            var e = mmg ? Mmg(sc) : new SimulationEngine(TestData.Fsb1(), sc);
            Assert.Null(e.Bank);
            var legacy = new byte[32];
            e.Stepped += eng => legacy = LegacyNext(legacy, eng);
            e.Enqueue(SimCommand.Rudder(5));
            e.Run(400);
            Assert.Equal(Convert.ToHexString(legacy), e.StateHash);
            Assert.Null(e.BuildState().Bank);
            Assert.DoesNotContain("\"bank\"", ContractJson.Serialize(e.BuildState()));
        }
        // 動力學不接受外力:banks 不啟用,雜湊亦不變
        var nx = new SimulationEngine(TestData.Fsb1(), Sc(WallYaml, speedKn: 6, telegraph: "SAH", depth: Depth), (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        Assert.False(nx.Bank!.Active);
        var leg = new byte[32];
        nx.Stepped += eng => leg = LegacyNext(leg, eng);
        nx.Run(300);
        Assert.Equal(Convert.ToHexString(leg), nx.StateHash);
        Assert.Null(nx.BuildState().Bank);
    }

    [Fact]
    public void FarBankLeavesMotionUnchangedButHashesItsBlock()
    {
        var far = WallYaml.Replace("x: 32", "x: 900");
        var a = Mmg(Sc("", speedKn: 6, telegraph: "SAH", depth: Depth));
        var b = Mmg(Sc(far, speedKn: 6, telegraph: "SAH", depth: Depth));
        a.Run(500);
        b.Run(500);
        Assert.True(b.Bank!.Active);
        Assert.Equal(a.Motion, b.Motion); // 影響寬度外:力恰為 0,運動位元相同
        Assert.NotEqual(a.StateHash, b.StateHash); // 啟用時追加 BANKEFCT 區塊
        var st = b.BuildState().Bank!;
        Assert.Equal(0.0, st.YKN);
        Assert.Null(st.DistanceM);
    }

    [Fact]
    public void MmgShipIsDrawnTowardBankAndBowSheersAway()
    {
        // 右舷 32 m 的垂直岸壁(船側距岸 23 m),6 kn、舵正中:向右舷橫移、艏向左偏(艏離岸)
        var e = Mmg(Sc(WallYaml, speedKn: 6, telegraph: "SAH", depth: Depth));
        var free = Mmg(Sc("", speedKn: 6, telegraph: "SAH", depth: Depth));
        var y0 = e.Bank!.Last;
        Assert.True(y0.Y > 0 && y0.N < 0);
        RunTo(e, 30);
        RunTo(free, 30);
        Assert.True(e.Motion.R < free.Motion.R - 1e-4, $"艏離岸(左轉):r {e.Motion.R} vs 無岸 {free.Motion.R}");
        Assert.True(Units.WrapRadPi(e.Motion.Psi) < Units.WrapRadPi(free.Motion.Psi));
        var st = e.BuildState().Bank!;
        Assert.Equal("starboard", st.NearestSide);
        Assert.True(st.YKN > 0 && st.NKNm < 0);
        Assert.InRange(st.DistanceM!.Value, 10, 60);
        // 船身受吸力而右移(與無岸相比,x 較大),但艏向左偏使之後離岸;全程數值有限
        RunTo(e, 120);
        Assert.True(double.IsFinite(e.Motion.X) && double.IsFinite(e.Motion.R));
        using var doc = JsonDocument.Parse(ContractJson.Serialize(e.BuildState()));
        var bank = doc.RootElement.GetProperty("bank");
        foreach (var k in new[] { "Y_kN", "N_kNm", "side", "parameter", "influence_m", "depthRatio" }) Assert.True(bank.TryGetProperty(k, out _), k);
    }

    private const string MixedBanks = """
        tugs:
          - { id: TG1, bollardPull_t: 30, attach: aft, riseTime_s: 20, initial: { mode: standby, direction_deg: 270 } }
        banks:
          scale: 1.0
          lines:
            - { id: E1, type: vertical, waterSide: left, line: [ { x: 35, y: -5000 }, { x: 35, y: 8000 } ] }
            - { id: W1, type: sloped, slope: 3, topDepth_m: 2, waterSide: right, line: [ { x: -80, y: -5000 }, { x: -60, y: 8000 } ] }
        """;

    private static (long Tick, SimCommand Cmd)[] MixedScript() => new[]
    {
        (100L, SimCommand.Rudder(-5)),
        (600L, SimCommand.TugControl("TG1", "push", 270, 40)),
        (1200L, SimCommand.Rudder(3)),
        (1800L, SimCommand.TugControl("TG1", "standby", null, null)),
        (2400L, SimCommand.Rudder(0)),
    };

    private static SimulationEngine MixedEngine() => Mmg(Sc(MixedBanks, speedKn: 5, telegraph: "SAH", depth: Depth));

    [Fact]
    public void SnapshotRestoreContinuesBitIdentical()
    {
        var a = MixedEngine();
        EngineDeterminismTests.RunScript(a, 3000, MixedScript());
        Assert.True(a.Bank!.Active && a.External.Active);

        var b = MixedEngine();
        EngineDeterminismTests.RunScript(b, 1400, MixedScript());
        var snap = EngineSnapshot.FromJson(b.CreateSnapshot().ToJson());
        var c = MixedEngine();
        c.Restore(snap);
        Assert.Equal(b.StateHash, c.StateHash);
        Assert.Equal(ContractJson.Serialize(b.BuildState().Bank), ContractJson.Serialize(c.BuildState().Bank));
        var rest = MixedScript().Where(x => x.Tick >= 1400).ToArray();
        for (var i = 0; i < 3000 - 1400; i++)
        {
            foreach (var (tick, cmd) in rest) if (tick == c.Tick) c.Enqueue(cmd);
            c.Step();
        }
        Assert.Equal(a.StateHash, c.StateHash);
        Assert.Equal(a.Motion, c.Motion);
        Assert.Equal(ContractJson.Serialize(a.BuildState().Bank), ContractJson.Serialize(c.BuildState().Bank));

        // 只有岸壁(無拖船):同樣一致
        var sOnly = Sc(WallYaml, speedKn: 6, telegraph: "SAH", depth: Depth);
        var p = Mmg(sOnly);
        p.Run(2000);
        var q = Mmg(sOnly);
        q.Run(900);
        var snap2 = EngineSnapshot.FromJson(q.CreateSnapshot().ToJson());
        Assert.Null(snap2.External);
        var r = Mmg(sOnly);
        r.Restore(snap2);
        r.Run(1100);
        Assert.Equal(p.StateHash, r.StateHash);
    }

    [Fact]
    public void RecordReplayReproducesHashWithBanksFromFile()
    {
        // F03 航道的示意岸壁檔:header 內嵌 definitions,重播不讀檔
        var sc = F03WithBanks();
        var path = Path.Combine(TestData.TestRecordsDirectory, $"banks-{Guid.NewGuid():N}.jsonl");
        var engine = MmgTestSupport.MmgEngine(sc);
        string hash;
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 2500, new[] { (200L, SimCommand.Rudder(-3)), (1500L, SimCommand.Rudder(2)) });
            hash = writer.Close().StateHash;
        }
        var rec = RecordReader.Read(path);
        Assert.Equal(3, rec.Header.Scenario.Banks!.Definitions!.Count);
        var result = Replayer.Replay(path, TestData.Ship, (s, l) => new MmgDynamics(s, l, MmgTestSupport.Fsb1Full));
        Assert.True(result.IntegrityOk);
        Assert.Equal(hash, result.ActualHash);
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
    }

    /// <summary>
    /// F03(麥寮航道,FSB1 滿載)+ 示意岸壁檔:自船置於航段 0(WP1→WP2,051.7°)中心線北側 95 m(距北側坡腳 30 m)、7.5 kn,
    /// 不用 F03 時間表(示範動作會在航道內下滿舵),以自動舵保持航段航向。
    /// </summary>
    private static Scenario.Scenario F03WithBanks()
    {
        var sc = ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", "F03_FSB1_channel.yaml"));
        sc.Timeline.Clear();
        sc.Assessment = new ScenarioAssessment(); // 規則引用時間表項目
        sc.Banks = new ScenarioBanks { File = "data/areas/mailiao-channel-banks.json" };
        ScenarioBankLoader.ResolveBanks(sc, TestData.Root);
        var proj = new LocalTangentPlane(sc.Origin!.Lat, sc.Origin.Lon);
        var (bx, by) = proj.ToLocal(23.7960, 120.1220); // WP2(WP1 = 原點)
        var len = Math.Sqrt(bx * bx + by * by);
        var (te, tn) = (bx / len, by / len);
        var (le, ln) = (-tn, te); // 航向左側(北側)
        var (x0, y0) = (600 * te + 95 * le, 600 * tn + 95 * ln);
        var (lat, lon) = proj.ToGeodetic(x0, y0);
        sc.Initial.Position = new ScenarioPosition { Lat = lat, Lon = lon };
        sc.Initial.Heading = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(te, tn)));
        return sc.Validate();
    }

    [Fact]
    public void F03ChannelWithIllustrativeBanksStaysBoundedUnderAutopilot()
    {
        var sc = F03WithBanks();
        var e = MmgTestSupport.MmgEngine(sc);
        var free = MmgTestSupport.MmgEngine(ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", "F03_FSB1_channel.yaml")) is var f && Reposition(f, sc) ? f : f);
        Assert.True(e.Bank!.Active);
        var st0 = e.BuildState().Bank!;
        Assert.Equal("port", st0.NearestSide); // 北側坡岸在航向左舷
        Assert.Equal("MLC-N", st0.BankId);
        Assert.True(st0.YKN < 0 && st0.NKNm > 0, "吸向左舷岸、艏向右(離岸)");
        Assert.InRange(st0.DistanceM!.Value, 30 + 18.75 - 1, 30 + 18.75 + 1); // 坡腳 30 m + 等效偏移 5·(12.5−5)/2

        var heading = sc.Initial.Heading;
        Apply(e, SimCommand.Autopilot(true, heading, 20));
        Apply(free, SimCommand.Autopilot(true, heading, 20));
        var maxRudder = 0.0;
        var maxY = 0.0;
        for (var t = 1; t <= 300; t++)
        {
            RunTo(e, t);
            RunTo(free, t);
            var m = e.Motion;
            Assert.True(double.IsFinite(m.U) && double.IsFinite(m.V) && double.IsFinite(m.R) && double.IsFinite(m.X) && double.IsFinite(m.Y), $"t = {t}");
            maxRudder = Math.Max(maxRudder, Math.Abs(e.Actuators.RudderDeg));
            maxY = Math.Max(maxY, Math.Abs(e.Bank.Last.Y));
            Assert.InRange(Units.RadPerSecToDegPerMin(m.R), -30.0, 30.0);
        }
        // 自動舵以舵抵銷艏離岸力矩(需要的舵角大於無岸時),航向保持;力有界
        Assert.True(Math.Abs(e.Actuators.RudderDeg) > Math.Abs(free.Actuators.RudderDeg) + 0.2, $"抵銷岸壁力矩的舵角 {e.Actuators.RudderDeg} vs 無岸 {free.Actuators.RudderDeg}");
        Assert.InRange(maxRudder, 0.2, 20.0);
        Assert.InRange(maxY / 1000.0, 1.0, 400.0);
        Assert.InRange(Units.WrapDeg180(Units.RadToDeg(e.Motion.Psi) - heading), -3.0, 3.0);
        Assert.InRange(e.BuildState().Stw, 6.0, 8.5);
    }

    private static bool Reposition(Scenario.Scenario target, Scenario.Scenario like)
    {
        target.Timeline.Clear();
        target.Assessment = new ScenarioAssessment();
        target.Initial.Position = like.Initial.Position;
        target.Initial.Heading = like.Initial.Heading;
        target.Validate();
        return true;
    }

    // ------------------------------------------------------------------ 契約

    [Fact]
    public void ContractsDescribeBanksAndBankState()
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "state.schema.json")));
        var props = state.RootElement.GetProperty("properties");
        Assert.True(props.TryGetProperty("bank", out var bank));
        Assert.DoesNotContain("bank", state.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var bp = bank.GetProperty("properties");
        foreach (var k in new[] { "Y_kN", "N_kNm", "side", "parameter", "distance_m", "clearance_m", "nearestSide", "bankId", "port_m", "starboard_m", "depthRatio", "influence_m" })
            Assert.True(bp.TryGetProperty(k, out _), k);

        using var scen = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json")));
        var sp = scen.RootElement.GetProperty("properties");
        Assert.True(sp.TryGetProperty("banks", out var banks));
        foreach (var k in new[] { "file", "ids", "lines", "definitions", "scale" }) Assert.True(banks.GetProperty("properties").TryGetProperty(k, out _), k);
        var defs = scen.RootElement.GetProperty("$defs").GetProperty("bankDefinition").GetProperty("properties");
        foreach (var k in new[] { "id", "type", "line", "waterSide", "slope", "topDepth_m", "channelDepth_m" }) Assert.True(defs.TryGetProperty(k, out _), k);

        // state JSON 往返(含 bank)
        var e = Mmg(Sc(WallYaml, speedKn: 6, telegraph: "SAH", depth: Depth));
        e.Run(50);
        var json = ContractJson.Serialize(e.BuildState());
        Assert.Contains("\"bank\":{\"Y_kN\":", json);
        Assert.Equal(json, ContractJson.Serialize(ContractJson.Deserialize<OwnShipState>(json)));
    }
}
