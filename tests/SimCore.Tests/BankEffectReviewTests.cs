using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 岸壁效應審查發現的回歸測試(設計文件 docs/design/physics-bank-effect.md 第 3.3、5 節):
/// (1) 船橫越淹沒岸/斜坡岸的等效岸壁線到淺灘上方時,力連續且依船寬在水域側的比例淡出(不再以 y_min 給最大吸力、越過 −B 時階躍為 0);
/// (2) 薄堤兩面皆為岸壁時,背面不因 −B 門檻被當成反向的最近岸計入;
/// (3) state.bank.side 為岸壁橫向力 Y 的方向(不是加權距離參數 D 的正負)。
/// </summary>
public class BankEffectReviewTests
{
    private const double Depth = 12.5;
    private const double Beam = 18.0; // FSB1 型寬
    private static readonly LocalTangentPlane Proj = new(23.80, 120.15);

    /// <summary>南北向直線岸壁 x = <paramref name="east"/>;<paramref name="waterSide"/> 為沿折線(向北)看的水域側。</summary>
    private static BankDefinition Wall(double east, string waterSide, string id = "W", string type = "vertical", double? slope = null,
        double top = 0, double? channel = null, double south = -3000, double north = 3000)
        => new()
        {
            Id = id, Type = type, Slope = slope, TopDepthM = top, ChannelDepthM = channel, WaterSide = waterSide,
            Line = new List<ScenarioPosition> { new() { X = east, Y = south }, new() { X = east, Y = north } },
        };

    private static BankEffectModel Model(double scale, params BankDefinition[] lines)
        => new(TestData.Fsb1(), LoadingCondition.Full, new ScenarioBanks { Lines = lines.ToList(), Scale = scale }, Proj, supported: true);

    private static BankEffectModel Model(params BankDefinition[] lines) => Model(1.0, lines);

    private static StateVector Ship(double u, double x = 0, double y = 0, double psiDeg = 0) => new(u, 0, 0, x, y, Units.DegToRad(psiDeg));

    // ------------------------------------------------------------------ 發現 1:橫越岸線

    /// <summary>
    /// 岸線 x = 0、水域在西(岸在航向北的右舷);船舯自等效岸壁水域側 40 m 向東橫移到陸側 40 m(淺灘上方),每 0.01 m 取樣。
    /// <paramref name="offset"/> 為等效岸壁相對折線的陸側偏移(垂直岸 0、斜坡 1:5 top 8 → 5·4.5/2 = 11.25 m)。
    /// </summary>
    private static void AssertCrossingContinuous(BankEffectModel m, double offset)
    {
        // 參考:長直平行岸、船側距等效岸壁 0.1 B 以內(y_min 下限)的最大值
        var yMax = m.Compute(Ship(3.0, x: offset - 0.6 * Beam), Depth, 0, detail: false).Y;
        Assert.True(yMax > 0, "右舷岸:吸向右舷(Y 正)");
        double? prevY = null, prevN = null;
        var maxDy = 0.0;
        var maxDn = 0.0;
        var nMax = Math.Abs(m.Compute(Ship(3.0, x: offset - 0.6 * Beam), Depth, 0, detail: false).N);
        var prevAbs = double.PositiveInfinity;
        for (var i = 4000; i >= -4000; i--)
        {
            var ys = i * 0.01; // 船舯至等效岸壁(水域側為正)
            var x = offset - ys;
            var r = m.Compute(Ship(3.0, x: x), Depth, 0, detail: true);
            if (prevY is { } py) maxDy = Math.Max(maxDy, Math.Abs(r.Y - py));
            if (prevN is { } pn) maxDn = Math.Max(maxDn, Math.Abs(r.N - pn));
            prevY = r.Y;
            prevN = r.N;
            Assert.True(r.Y >= 0.0, $"x = {x}:不應反向({r.Y})");
            Assert.True(r.Y <= yMax * (1 + 1e-9), $"x = {x}:不應超過 y_min 上限 {yMax}({r.Y})");
            if (ys <= 0.5 * Beam)
            {
                // 船寬跨越等效岸壁線後:隨船往陸側移動單調遞減
                Assert.True(r.Y <= prevAbs + 1e-9, $"x = {x}:跨越岸線時 Y 應遞減({prevAbs} → {r.Y})");
                prevAbs = r.Y;
            }
            if (ys < -0.5 * Beam - 1e-6)
            {
                // 整個船寬都在陸側(淺灘上方):不再計此岸壁
                Assert.Equal(0.0, r.Y);
                Assert.Equal(0.0, r.N);
                Assert.Null(r.StarboardDistanceM);
            }
        }
        // 0.01 m 步進:力與力矩的單步變化遠小於最大值(無階躍)
        Assert.True(maxDy < 0.002 * yMax, $"最大單步 |ΔY| {maxDy} N(最大 {yMax} N)");
        Assert.True(maxDn < 0.002 * nMax, $"最大單步 |ΔN| {maxDn} N·m(最大 {nMax} N·m)");
        // 船舯恰在等效岸壁線上:一半船寬在水域側 → 最大值的一半
        var onLine = m.Compute(Ship(3.0, x: offset), Depth, 0, detail: true);
        Assert.Equal(0.5, onLine.Y / yMax, 6);
        Assert.Equal(0.0, onLine.StarboardDistanceM!.Value, 9);
    }

    [Fact]
    public void CrossingSubmergedVerticalBankOntoShoalIsContinuousAndFadesOut()
    {
        // 淹沒岸 topDepth 8 m > T 5.70 m:船可浮在淺灘上方
        AssertCrossingContinuous(Model(Wall(0, "left", id: "SUB", top: 8, channel: 12.5)), 0.0);
    }

    [Fact]
    public void CrossingSlopedBankIsContinuousAndFadesOut()
    {
        AssertCrossingContinuous(Model(Wall(0, "left", id: "SL", type: "sloped", slope: 5, top: 8, channel: 12.5)), 5.0 * (12.5 - 8.0) / 2.0);
    }

    [Fact]
    public void ShipSideInsideMinimumClearanceKeepsSaturatedValue()
    {
        // 外推限制不變:船側距等效岸壁 0–0.1 B(船舯 B/2–0.6 B)以 y_min 計
        var atMin = Model(Wall(0.6 * Beam, "left")).Compute(Ship(3.0), Depth, 0, detail: false).Y;
        foreach (var d in new[] { 9.0, 9.5, 10.0, 10.5 })
            Assert.Equal(atMin, Model(Wall(d, "left")).Compute(Ship(3.0), Depth, 0, detail: false).Y, 9);
    }

    [Fact]
    public void EngineDriftingAcrossSubmergedBankHasNoForceJumps()
    {
        // 淹沒岸 x = +20(水域在西)、東流 1.5 kn 把船推過岸線到淺灘上方;每步讀步末 state.bank
        const string yaml = """
            banks:
              lines:
                - { id: SUB, type: vertical, topDepth_m: 8, channelDepth_m: 12.5, waterSide: left, line: [ { x: 20, y: -5000 }, { x: 20, y: 20000 } ] }
            """;
        var sc = Sc(yaml, speedKn: 6, telegraph: "HAH", depth: Depth, currentSet: 90, currentDriftKn: 1.5);
        foreach (var e in new[] { new SimulationEngine(TestData.Fsb1(), sc), Mmg(sc) })
        {
            Assert.True(e.Bank!.Active);
            Apply(e, SimCommand.Autopilot(true, 0, 20)); // 保持航向北(艏離岸力矩由自動舵抵銷)
            double? prev = null;
            var maxStep = 0.0;
            var maxAbs = 0.0;
            for (var i = 0; i < 50 * 600 && e.Motion.X < 50; i++)
            {
                e.Step();
                var b = e.BuildState().Bank!;
                if (prev is { } p) maxStep = Math.Max(maxStep, Math.Abs(b.YKN - p));
                maxAbs = Math.Max(maxAbs, Math.Abs(b.YKN));
                prev = b.YKN;
            }
            Assert.True(e.Motion.X >= 50, $"船應被推過岸線(x = {e.Motion.X})");
            Assert.True(maxAbs > 5.0, $"應經過岸壁影響範圍(最大 |Y| {maxAbs} kN)");
            Assert.True(maxStep < 0.5, $"單步 |ΔY| {maxStep} kN 應遠小於 5 kN");
            // 整個船寬在淺灘上方(船舯越過岸線 > B/2 + 半船長的橫向投影):岸壁力為 0
            Assert.Equal(0.0, e.BuildState().Bank!.YKN);
        }
    }

    // ------------------------------------------------------------------ 發現 2:薄堤兩面

    [Theory]
    [InlineData(5.0)]
    [InlineData(1.0)]
    [InlineData(9.0)]
    public void ThinPierBackFaceDoesNotReverseNearFaceSuction(double thickness)
    {
        // 近面 x = +20(水域在西)、遠面 x = 20 + t(水域在東);船在西側向北航行,船側不越過近面(船舯距近面 ≥ B/2)
        var pier = Model(Wall(20, "left", id: "NEAR"), Wall(20 + thickness, "right", id: "FAR"));
        var nearOnly = Model(Wall(20, "left", id: "NEAR"));
        for (var x = 0.0; x <= 20 - 0.5 * Beam + 1e-9; x += 0.1)
        {
            var a = pier.Compute(Ship(3.0, x: x), Depth, 0, detail: true);
            var b = nearOnly.Compute(Ship(3.0, x: x), Depth, 0, detail: true);
            Assert.True(a.Y > 0, $"x = {x:F1}:吸向近面(右舷),實得 {a.Y}");
            Assert.Equal(b.Y, a.Y, 9);
            Assert.Equal(b.N, a.N, 6);
            Assert.Equal(b.D, a.D, 12);
            Assert.Null(a.PortDistanceM); // 背面不顯示為左舷岸
            Assert.Equal("NEAR", a.StarboardBankId);
        }
        // 油輪在堤另一側(東側)同理:只受遠面吸力(吸向西 = 左舷)
        var east = pier.Compute(Ship(3.0, x: 20 + thickness + 15), Depth, 0, detail: true);
        var farOnly = Model(Wall(20 + thickness, "right", id: "FAR")).Compute(Ship(3.0, x: 20 + thickness + 15), Depth, 0, detail: true);
        Assert.True(east.Y < 0);
        Assert.Equal(farOnly.Y, east.Y, 9);
        Assert.Null(east.StarboardDistanceM);
    }

    [Fact]
    public void CanalBetweenTwoWallsStillSumsBothSides()
    {
        // 正常的兩岸(各自的水域都朝向船):仍兩岸相加(運河中線 0、偏向一岸時吸向較近者)
        var canal = Model(Wall(40, "left", id: "E"), Wall(-40, "right", id: "W"));
        Assert.Equal(0.0, canal.Compute(Ship(3.0), Depth, 0, detail: false).Y, 6);
        var off = canal.Compute(Ship(3.0, x: 10), Depth, 0, detail: true);
        var eOnly = Model(Wall(40, "left", id: "E")).Compute(Ship(3.0, x: 10), Depth, 0, detail: false);
        var wOnly = Model(Wall(-40, "right", id: "W")).Compute(Ship(3.0, x: 10), Depth, 0, detail: false);
        Assert.Equal(eOnly.Y + wOnly.Y, off.Y, 6);
        Assert.Equal(50.0, off.PortDistanceM!.Value, 9);
    }

    // ------------------------------------------------------------------ 發現 3:state.bank.side

    [Fact]
    public void StateSideFollowsLateralForceDirection()
    {
        // 右舷 25 m 岸壁只從船舯往前(船艏先進入):D > 0(右舷岸較近),但艏部壓力墊使 Y < 0(推向左舷)
        var fore = Model(Wall(25, "left", id: "FWD", south: 0, north: 3000));
        foreach (var rpm in new[] { 0.0, 40.0 })
        {
            fore.UpdateLast(Ship(3.0), Depth, rpm);
            var st = fore.State();
            Assert.True(st.YKN < 0, $"rpm {rpm}:Y {st.YKN}");
            Assert.True(st.Parameter > 0);
            Assert.Equal("port", st.Side); // 力朝左舷
            Assert.Equal("starboard", st.NearestSide); // 岸在右舷
        }
        // 長直右舷岸:吸向右舷
        var full = Model(Wall(25, "left"));
        full.UpdateLast(Ship(3.0), Depth, 0);
        Assert.Equal("starboard", full.State().Side);
        // scale 0:不施力 → none(仍顯示距離與 D)
        var zero = Model(0.0, Wall(25, "left"));
        zero.UpdateLast(Ship(3.0), Depth, 0);
        var z = zero.State();
        Assert.Equal("none", z.Side);
        Assert.True(z.Parameter > 0);
        Assert.Equal("starboard", z.NearestSide);
    }
}
