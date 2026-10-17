using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>錨與錨鏈(設計文件第 3 節):懸鏈線解析值、抓地/走錨門檻、起錨速率、剎車滑出、絞盤停轉、鏈端、引擎流程。</summary>
public class AnchorTests
{
    private const double Dt = 0.02;

    // ------------------------------------------------------------------ 懸鏈線

    [Theory]
    [InlineData(100.0, 20.0, 330.0, 5.0)]
    [InlineData(100.0, 20.0, 330.0, 200.0)]
    [InlineData(247.5, 26.0, 640.0, 50.0)]
    [InlineData(137.5, 25.0, 329.0, 1500.0)]
    public void TouchdownCatenaryMatchesAnalyticGeometry(double length, double h, double w, double a)
    {
        // 給定 a = H/w 由解析式得水平距離,求解應還原 H;並以懸鏈線 y = a(cosh(x/a) − 1) 檢查幾何(獨立於求解式)
        var aUse = Math.Min(a, Catenary.MaxTouchdownA(length, h) * 0.999);
        var x = Catenary.TouchdownDistance(aUse, length, h);
        var r = Catenary.Solve(x, length, h, w, 1e12);
        Assert.Equal(CatenaryRegime.Touchdown, r.Regime);
        Assert.Equal(w * aUse, r.H, w * aUse * 1e-8);
        var s = length - r.OnBottom;
        var xs = x - r.OnBottom;
        Assert.Equal(h, aUse * (Math.Cosh(xs / aUse) - 1.0), h * 1e-6);   // 懸空段終點高度 = h
        Assert.Equal(s, aUse * Math.Sinh(xs / aUse), s * 1e-6);           // 懸空弧長 = s
        Assert.Equal(r.H + w * h, r.TopTension, r.TopTension * 1e-9);     // T_top = H + w·h
        Assert.Equal(0.0, r.UpliftRad);
    }

    [Fact]
    public void TensionGrowsMonotonicallyWithDistanceAndRegimesAreContinuous()
    {
        const double length = 137.5, h = 25.0, w = 329.0;
        var prev = -1.0;
        CatenaryRegime? last = null;
        var regimes = new HashSet<CatenaryRegime>();
        var chord = Math.Sqrt(length * length - h * h);
        for (var x = 100.0; x < chord - 0.01; x += 0.05)
        {
            var r = Catenary.Solve(x, length, h, w, 1e12);
            Assert.True(r.H >= prev - 1e-6, $"x = {x}:H 應隨距離遞增");
            if (last is { } lr && lr != r.Regime && prev > 0) Assert.InRange(r.H / prev, 0.98, 1.05); // 區域交界連續
            prev = r.H;
            last = r.Regime;
            regimes.Add(r.Regime);
        }
        Assert.Contains(CatenaryRegime.Slack, regimes);
        Assert.Contains(CatenaryRegime.Touchdown, regimes);
        Assert.Contains(CatenaryRegime.Suspended, regimes);
        // 全懸空:2a·sinh(X/2a) = √(L² − h²)
        var xs = chord - 0.3;
        var sus = Catenary.Solve(xs, length, h, w, 1e12);
        Assert.Equal(CatenaryRegime.Suspended, sus.Regime);
        var aS = sus.H / w;
        Assert.Equal(chord, 2.0 * aS * Math.Sinh(xs / (2.0 * aS)), 1e-6);
        Assert.True(sus.UpliftRad > 0.0);
        // 鬆弛與拉直
        Assert.Equal(0.0, Catenary.Solve(length - h - 1.0, length, h, w, 1e12).H);
        Assert.Equal(5e5, Catenary.Solve(chord + 1.0, length, h, w, 5e5).H);
        Assert.Equal(CatenaryRegime.Hanging, Catenary.Solve(10.0, 20.0, h, w, 1e12).Regime);
    }

    // ------------------------------------------------------------------ 單錨模型

    private static AnchorParticulars Fsb1Spec => AnchorParticulars.FromShip(TestData.Fsb1())!;

    private static (AnchorUnit Unit, StateVector Ship) AnchoredAtTension(double hNewtons, double length = 137.5, double depth = 20.0)
    {
        var u = new AnchorUnit("starboard", Fsb1Spec, 5.7);
        var ship = new StateVector(0, 0, 0, 0, 0, 0);
        u.Length = length;
        u.Mode = AnchorMode.Braked;
        u.OnBottom = true;
        var hv = u.VerticalSpan(depth);
        var w = u.Spec.ChainSubmergedWeightPerM;
        var x = Catenary.TouchdownDistance(hNewtons / w, length, hv);
        var (he, hn) = BodyFrame.ToEnu(ship, u.HawseXb, u.HawseYb);
        (u.AnchorX, u.AnchorY) = (he, hn + x);
        return (u, ship);
    }

    [Fact]
    public void ParticularsAndEstimatesAreLoaded()
    {
        var a = Fsb1Spec;
        Assert.Equal("AC-14", a.Type);
        Assert.Equal(2140.0, a.AnchorMassKg);
        Assert.Equal(9 * 27.5, a.PortLengthM);
        Assert.Equal(21.7 / 60.0, a.HeavingRateMps, 12);
        Assert.Equal("hhp", a.HoldingClass);
        Assert.Equal(46.5, a.HawseX);
        Assert.DoesNotContain("mass_kg", a.EstimatedFields);
        var b = AnchorParticulars.FromShip(TestData.Fsb2())!;
        Assert.Equal(3300.0, b.AnchorMassKg);          // 估計(particulars mass_kg = null)
        Assert.Contains("mass_kg", b.EstimatedFields);
        Assert.Equal(10 * 27.5, b.StarboardLengthM);
        Assert.Equal("stockless", b.HoldingClass);
        Assert.Equal(1405e3, a.ChainBreakingLoadN);
        Assert.Equal(632e3, a.BrakeHoldingN); // particulars 估計值(≈ 0.45 × 1405 kN)
    }

    [Fact]
    public void HoldingAndDraggingThreshold()
    {
        var spec = Fsb1Spec;
        // 抓地力 = 錨重 × g × 係數(sand, HHP = 7)+ 0.75 × w × 躺底長
        var (u, ship) = AnchoredAtTension(100e3);
        var r = u.Solve(ship, 20.0, out _, out _);
        var expected = 2140 * 9.80665 * 7.0 + 0.75 * spec.ChainSubmergedWeightPerM * r.OnBottom;
        Assert.Equal(expected, u.HoldingPower(r, Seabed.Sand, 1.0), 1e-6);
        Assert.Equal(100e3, r.H, 1.0);

        var events = new List<ExternalEvent>();
        u.PostStep(ship, 20.0, Dt, Seabed.Sand, 1.0, 0.5, 0.0, events);
        Assert.False(u.Dragging);
        Assert.Empty(events);
        var ax = u.AnchorY;

        // 同張力在 mud(係數 5)與 holdingFactor 0.5 → 超過抓地力 → 走錨,錨朝船移動
        u.PostStep(ship, 20.0, Dt, Seabed.Mud, 0.5, 0.5, 0.0, events);
        Assert.True(u.Dragging);
        var drag = Assert.Single(events);
        Assert.Equal("anchorDragging", drag.Kind);
        Assert.True(u.AnchorY < ax, "走錨:錨朝錨鏈孔方向被拖動");
        Assert.Equal("dragging", u.Status);
        // 拖動速率有上限(dragSpeed 0.5 m/s)
        Assert.True(ax - u.AnchorY <= 0.5 * Dt + 1e-12);

        // 回到 sand:張力 ≤ 0.95 × 抓地力 → anchorHolding
        u.PostStep(ship, 20.0, Dt, Seabed.Sand, 1.0, 0.5, 0.0, events);
        Assert.False(u.Dragging);
        Assert.Equal("anchorHolding", events.Last().Kind);
        Assert.Equal("holding", u.Status);
    }

    [Fact]
    public void HeavingRateMatchesParticularsAndWindlassStalls()
    {
        var (u, ship) = AnchoredAtTension(5e3);
        Assert.Null(u.Command("heave", null, ship, 20.0));
        var l0 = u.Length;
        var events = new List<ExternalEvent>();
        // 船隨錨鏈縮短保持鬆弛:每步把錨移近,使張力維持低值(只測絞盤速率)
        for (var i = 0; i < 3000; i++)
        {
            u.PostStep(ship, 20.0, Dt, Seabed.Sand, 1.0, 0.5, 0.0, events);
            var (he, hn) = BodyFrame.ToEnu(ship, u.HawseXb, u.HawseYb);
            u.AnchorY = hn + Math.Max(0.0, u.Length - u.VerticalSpan(20.0) - 1.0);
            u.AnchorX = he;
        }
        Assert.Equal(21.7, l0 - u.Length, 1e-6); // 60 s × 21.7 m/min
        Assert.Equal("heaving", u.Status);

        // 超過絞盤過載拉力 → 停轉(鏈長不變)、事件一次
        var (s2, ship2) = AnchoredAtTension(1.6 * Fsb1Spec.WindlassPullN);
        s2.Command("heave", null, ship2, 20.0);
        var before = s2.Length;
        var ev2 = new List<ExternalEvent>();
        for (var i = 0; i < 50; i++) s2.PostStep(ship2, 20.0, Dt, Seabed.Sand, 100.0, 0.5, 0.0, ev2);
        Assert.Equal(before, s2.Length);
        Assert.True(s2.Stalled);
        Assert.Single(ev2, x => x.Kind == "anchorWindlassStalled");
    }

    [Fact]
    public void BrakeSlipsWhenTensionExceedsHolding()
    {
        var spec = Fsb1Spec;
        // 錨鏈孔張力 > 剎車握持力(632 kN):鏈被拉出,每步 ≤ SlipRate·dt,張力回到剎車握持力
        var (u, ship) = AnchoredAtTension(1000.0);
        var hv = u.VerticalSpan(20.0);
        var chord = Math.Sqrt(u.Length * u.Length - hv * hv);
        var (he, hn) = BodyFrame.ToEnu(ship, u.HawseXb, u.HawseYb);
        u.AnchorY = hn + chord - 0.01; // 幾乎拉直
        var r0 = u.Solve(ship, 20.0, out _, out _);
        Assert.True(r0.TopTension > spec.BrakeHoldingN);
        var events = new List<ExternalEvent>();
        var l0 = u.Length;
        u.PostStep(ship, 20.0, Dt, Seabed.Sand, 100.0, 0.5, 0.0, events); // holdingFactor 100:錨不走,剎車先滑
        Assert.True(u.Length > l0);
        Assert.True(u.Length - l0 <= AnchorUnit.SlipRateMps * Dt + 1e-9);
        Assert.Contains(events, x => x.Kind == "anchorBrakeSlipping");
        for (var i = 0; i < 500; i++) u.PostStep(ship, 20.0, Dt, Seabed.Sand, 100.0, 0.5, 0.0, events);
        var r1 = u.Solve(ship, 20.0, out _, out _);
        Assert.InRange(r1.TopTension, 0.95 * spec.BrakeHoldingN, 1.0001 * spec.BrakeHoldingN);
        Assert.Single(events, x => x.Kind == "anchorBrakeSlipping");
    }

    [Fact]
    public void LetGoWithoutTargetRunsToBitterEnd()
    {
        var u = new AnchorUnit("port", Fsb1Spec, 5.7);
        var ship = new StateVector(0, 0, 0, 0, 0, 0);
        Assert.Null(u.Command("letGo", null, ship, 20.0));
        var events = new List<ExternalEvent>();
        for (var i = 0; i < 20000 && !u.FullyOut; i++) u.PostStep(ship, 20.0, Dt, Seabed.Sand, 1.0, 0.5, 0.0, events);
        Assert.True(u.FullyOut);
        Assert.Equal(9 * 27.5, u.Length);
        var full = Assert.Single(events, x => x.Kind == "anchorChainFullyOut");
        Assert.Equal("port", full.ItemId);
        Assert.Equal("chainFullyOut", u.Command("veer", null, ship, 20.0));
        Assert.Equal("anchorAlreadyOut", u.Command("letGo", null, ship, 20.0));
        Assert.Equal("invalidTarget", u.Command("heave", 300.0, ship, 20.0));
    }

    // ------------------------------------------------------------------ 引擎流程(MMG)

    [Fact]
    public void LetGoInCurrentBringsUpThenHeaveToAweigh()
    {
        // 船艏向北、1.5 kn 南向流:拋右錨 5 節 → 船後退、鏈受力、帶上;起錨 → 離底 → 收妥
        var e = Mmg(Sc(depth: 20, currentSet: 180, currentDriftKn: 1.5));
        Assert.False(e.External.Active); // 錨未用、無拖船/泊位:舊積分路徑
        var events = TestData.Capture(e);
        var st0 = e.BuildState();
        Assert.Equal(new[] { "port", "starboard" }, st0.Anchors!.Select(a => a.Side));
        Assert.All(st0.Anchors!, a => Assert.Equal("stowed", a.Status));
        Apply(e, SimCommand.Anchor("starboard", "letGo", targetShackles: 5));
        Assert.True(e.External.Active);
        Assert.Single(events, x => x.Kind == SimEventKinds.AnchorLetGo);
        RunTo(e, 900);
        var bu = Assert.Single(events, x => x.Kind == SimEventKinds.AnchorBroughtUp);
        Assert.Equal("starboard", bu.ItemId);
        var a = e.BuildState().Anchors!.Single(x => x.Side == "starboard");
        Assert.Equal("holding", a.Status);
        // 鏈突然受力時動態張力可能短暫超過剎車握持力而滑出少許
        Assert.InRange(a.LengthM, 5 * 27.5, 5 * 27.5 + 5.0);
        Assert.InRange(a.LengthShackles!.Value, 5.0, 5.2);
        Assert.True(a.TensionT > 0.0 && a.OnBottomM > 0.0);
        Assert.NotNull(a.Position);
        var sog = e.BuildState().Truth!.Sog;
        Assert.True(sog < 0.3, $"錨泊後對地速度應很小,實際 {sog} kn");
        // 以 1.5 kn 後退中鏈突然受力(snub)時錨可能短暫被拖動,但每次走錨都須再抓住
        Assert.Equal(events.Count(x => x.Kind == SimEventKinds.AnchorDragging), events.Count(x => x.Kind == SimEventKinds.AnchorHolding));

        // 起錨:速率 21.7 m/min(張力低於絞盤拉力時)
        Apply(e, SimCommand.Anchor("starboard", "heave"));
        var l0 = e.External.Anchor("starboard")!.Length;
        RunTo(e, e.Time + 30);
        Assert.InRange(l0 - e.External.Anchor("starboard")!.Length, 0.0, 21.7 / 2 + 1e-6);
        RunTo(e, e.Time + 900);
        Assert.Single(events, x => x.Kind == SimEventKinds.AnchorAweigh);
        Assert.Single(events, x => x.Kind == SimEventKinds.AnchorHoused);
        Assert.Equal("stowed", e.BuildState().Anchors!.Single(x => x.Side == "starboard").Status);
    }

    [Fact]
    public void StrongPullDragsAnchorOnMud()
    {
        var e = Mmg(Sc("""
            tugs: [ { id: TG2, bollardPull_t: 40, attach: aft, riseTime_s: 20 } ]
            anchors: { holdingFactor: 0.5, starboard: { length_shackles: 4 } }
            """, depth: 20, seabed: "mud"));
        var events = TestData.Capture(e);
        var a0 = e.BuildState().Anchors!.Single(x => x.Side == "starboard");
        Assert.Equal("holding", a0.Status); // 初始已錨泊
        Apply(e, SimCommand.TugControl("TG2", "pull", 180, 100)); // 向後拉 40 t
        RunTo(e, 300);
        var drag = Assert.Single(events, x => x.Kind == SimEventKinds.AnchorDragging);
        Assert.Equal("starboard", drag.ItemId);
        Assert.True(drag.DataNumber("tension_t") > drag.DataNumber("holding_t"));
        var a = e.BuildState().Anchors!.Single(x => x.Side == "starboard");
        Assert.Equal("dragging", a.Status);
        Assert.True(Math.Abs(a.Position!.Y - a0.Position!.Y) > 5.0, "錨位移動");
        // 停止拉 → 錨重新抓住
        Apply(e, SimCommand.TugControl("TG2", "standby"));
        RunTo(e, 900);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AnchorHolding);
    }

    [Fact]
    public void AnchorCommandRejections()
    {
        var e = Mmg(Sc());
        var events = TestData.Capture(e);
        Apply(e, SimCommand.Anchor("centre", "letGo"));
        Apply(e, SimCommand.Anchor("port", "heave"));
        Apply(e, SimCommand.Anchor("port", "drop"));
        Assert.Equal(new[] { CommandRejectReasons.UnknownAnchor, CommandRejectReasons.AnchorStowed, CommandRejectReasons.UnknownAction },
            events.Where(x => x.Kind == SimEventKinds.CommandRejected).Select(x => x.DataString("reason")));
        Assert.False(e.External.Active); // 被拒的指令不算「使用」
    }
}
