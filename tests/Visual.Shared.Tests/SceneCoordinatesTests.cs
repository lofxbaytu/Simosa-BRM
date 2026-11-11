using SimosaBRM.Visual.Shared.Scene;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

public class SceneCoordinatesTests
{
    [Fact]
    public void EnuEastNorthUpMapsToGodotXYNegZ()
    {
        var p = SceneCoordinates.FromEnu(100, 200, 5);
        Assert.Equal(new ScenePoint(100, 5, -200), p);
        var (e, n, u) = SceneCoordinates.ToEnu(p);
        Assert.Equal((100.0, 200.0, 5.0), (e, n, u));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, -Math.PI / 2)]
    [InlineData(180, -Math.PI)]
    [InlineData(270, -3 * Math.PI / 2)]
    public void HeadingToYawIsNegativeRadians(double heading, double yaw)
    {
        Assert.InRange(SceneCoordinates.HeadingToYawRad(heading), yaw - 1e-12, yaw + 1e-12);
        Assert.InRange(SceneCoordinates.YawRadToHeading(yaw), heading - 1e-9, heading + 1e-9);
    }

    [Fact]
    public void HeadingEastRotatesNodeForwardToPlusX()
    {
        // 節點前方 −z 經 yaw 旋轉後應指向東(+x):rotation about y by θ: (x,z) → (x cos θ + z sin θ, −x sin θ + z cos θ)
        var yaw = SceneCoordinates.HeadingToYawRad(90);
        var fx = 0 * Math.Cos(yaw) + (-1) * Math.Sin(yaw);
        var fz = -0 * Math.Sin(yaw) + (-1) * Math.Cos(yaw);
        Assert.InRange(fx, 0.999, 1.001);
        Assert.InRange(fz, -0.001, 0.001);
    }

    [Fact]
    public void BodyFrameMapsBowToMinusZAndStarboardToPlusX()
    {
        Assert.Equal(new ScenePoint(0, 0, -50), SceneCoordinates.FromBody(50, 0, 0));
        Assert.Equal(new ScenePoint(9, 0, 0), SceneCoordinates.FromBody(0, 9, 0));
        Assert.Equal(new ScenePoint(0, 12, 0), SceneCoordinates.FromBody(0, 0, 12));
    }

    [Fact]
    public void BodyOffsetRotatedByHeadingGivesWorldOffset()
    {
        // 航向 090,艏前 10 m → 東 10 m;右舷 5 m → 南 5 m(場景 z = +5)
        var bow = SceneCoordinates.BodyToWorldOffset(10, 0, 0, 90);
        Assert.InRange(bow.X, 9.999, 10.001);
        Assert.InRange(bow.Z, -0.001, 0.001);
        var stbd = SceneCoordinates.BodyToWorldOffset(0, 5, 0, 90);
        Assert.InRange(stbd.X, -0.001, 0.001);
        Assert.InRange(stbd.Z, 4.999, 5.001);
    }

    [Fact]
    public void BearingAndRangeToEnu()
    {
        var (e, n) = SceneCoordinates.FromBearing(45, 1414.2135);
        Assert.InRange(e, 999.9, 1000.1);
        Assert.InRange(n, 999.9, 1000.1);
    }
}

/// <summary>橫搖的場景幾何(docs/design/visual-client.md 第 4 節「橫搖、縱搖與升沉視角」):右傾正、右舷下沉、高處往右舷移;0 時與無橫搖相同。</summary>
public class SceneRollTests
{
    [Fact]
    public void HeelOffsetMovesHighPointsToStarboardAndLowersStarboardSide()
    {
        // 桅頂(中心線、高 20 m)右傾 10°:往右舷 20·sin10° ≈ 3.47 m、高度 20·cos10° ≈ 19.70 m
        var (s, u) = SceneCoordinates.HeelOffset(0, 20, 10);
        Assert.InRange(s, 3.47, 3.48);
        Assert.InRange(u, 19.69, 19.70);
        // 右舷舷側(stbd 9 m、水線高 0)右傾 10°:下沉 9·sin10° ≈ 1.56 m
        var (s2, u2) = SceneCoordinates.HeelOffset(9, 0, 10);
        Assert.InRange(s2, 8.86, 8.87);
        Assert.InRange(u2, -1.57, -1.56);
        // 左傾(負)對稱
        var (s3, u3) = SceneCoordinates.HeelOffset(0, 20, -10);
        Assert.InRange(s3, -3.48, -3.47);
        Assert.Equal(u, u3, 9);
    }

    [Fact]
    public void ZeroRollKeepsBodyOffsetBitIdentical()
    {
        var a = SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137);
        var b = SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137, 0.0);
        Assert.Equal(a, b);
        Assert.Equal((-9.0, 18.0), SceneCoordinates.HeelOffset(-9, 18, 0));
    }

    [Fact]
    public void BodyOffsetWithRollRotatesEyeTowardsStarboardInWorldFrame()
    {
        // 航向 000:右舷 = 東;駕駛台眼位(中心線、高 18 m)右傾 5° → 東向 +18·sin5°、高度 18·cos5°
        var p = SceneCoordinates.BodyToWorldOffset(0, 0, 18, 0, 5);
        var (east, north, up) = SceneCoordinates.ToEnu(p);
        Assert.InRange(east, 1.568, 1.570);
        Assert.InRange(north, -1e-9, 1e-9);
        Assert.InRange(up, 17.93, 17.94);
        // 航向 090:右舷 = 南 → 北向為負
        var q = SceneCoordinates.BodyToWorldOffset(0, 0, 18, 90, 5);
        var (e2, n2, _) = SceneCoordinates.ToEnu(q);
        Assert.InRange(n2, -1.570, -1.568);
        Assert.InRange(e2, -1e-9, 1e-9);
    }

    [Fact]
    public void ObserverHeelComponentsFlipForAftViewAndBecomePitchAbeam()
    {
        var (r0, p0) = SceneCoordinates.ObserverHeelComponents(6, 0);     // 駕駛台前視:全為橫滾
        Assert.Equal(6.0, r0, 9);
        Assert.Equal(0.0, p0, 9);
        var (r180, p180) = SceneCoordinates.ObserverHeelComponents(6, 180); // 艉視:橫滾反號
        Assert.Equal(-6.0, r180, 9);
        Assert.InRange(p180, -1e-9, 1e-9);
        var (r90, p90) = SceneCoordinates.ObserverHeelComponents(6, 90);   // 正右舷看:右傾全化為下俯
        Assert.InRange(r90, -1e-9, 1e-9);
        Assert.Equal(-6.0, p90, 9);
        var (r60, p60) = SceneCoordinates.ObserverHeelComponents(6, 60);   // 右翼橋 60°:橫滾 3°、俯仰 −5.2°
        Assert.InRange(r60, 2.99, 3.01);
        Assert.InRange(p60, -5.20, -5.19);
    }
}

/// <summary>
/// 浪一階運動(state.waveMotion:升沉向上為正、縱搖艏仰為正;需求 R2)的場景幾何與觀察者轉換
/// (docs/design/visual-client.md 第 4 節「橫搖、縱搖與升沉」):合成順序固定為 Euler ZYX(艏搖 → 縱搖 → 橫搖),
/// 世界 = 升沉 · Ry(−艏向) · Rx(縱搖) · Rz(−橫搖) · 船體;這裡釘住順序與正負號(艏仰為正時駕駛台前視的地平線下移)。
/// </summary>
public class SceneWaveMotionTests
{
    private const double Deg = Math.PI / 180.0;

    private static void AssertPoint(ScenePoint expected, ScenePoint actual, double tol = 1e-9)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.Z, expected.Z - tol, expected.Z + tol);
    }

    [Fact]
    public void PitchOffsetRaisesBowLowersSternAndMovesHighPointsAft()
    {
        // 艏端(船舯前 50 m、水線)艏仰 2°:上升 50·sin2° ≈ 1.745 m
        var (f, u) = SceneCoordinates.PitchOffset(50, 0, 2);
        Assert.InRange(u, 1.744, 1.746);
        Assert.InRange(f, 49.96, 49.98);
        // 艉端下沉同量
        var (_, uAft) = SceneCoordinates.PitchOffset(-50, 0, 2);
        Assert.Equal(-u, uAft, 12);
        // 桅頂(船舯、高 20 m)艏仰 2°:往艉移 20·sin2° ≈ 0.698 m
        var (f2, u2) = SceneCoordinates.PitchOffset(0, 20, 2);
        Assert.InRange(f2, -0.699, -0.697);
        Assert.InRange(u2, 19.98, 19.99);
        // 艏俯(負)對稱:艏端下沉
        var (_, uDown) = SceneCoordinates.PitchOffset(50, 0, -2);
        Assert.Equal(-u, uDown, 12);
        // 0 時原樣
        Assert.Equal((30.0, 18.0), SceneCoordinates.PitchOffset(30, 18, 0));
    }

    [Fact]
    public void ZeroPitchAndHeaveKeepBodyOffsetBitIdentical()
    {
        // 無 waveMotion(縱搖 0、升沉 0):與只有橫搖/只有艏向的換算逐位元相同
        Assert.Equal(SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137, 4.0), SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137, 4.0, 0.0, 0.0));
        Assert.Equal(SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137), SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137, 0.0, 0.0, 0.0));
        // 橫搖路徑的數值未因重構而改變(航向 000、眼位高 18 m、右傾 5°)
        var (east, north, up) = SceneCoordinates.ToEnu(SceneCoordinates.BodyToWorldOffset(0, 0, 18, 0, 5));
        Assert.Equal(18 * Math.Sin(5 * Deg), east, 12);
        Assert.Equal(0.0, north, 12);
        Assert.Equal(18 * Math.Cos(5 * Deg), up, 12);
    }

    [Fact]
    public void HeaveLiftsEveryPointByTheSameAmountWithoutChangingHorizontalOffset()
    {
        foreach (var (f, s, u) in new[] { (30.0, -9.0, 18.0), (-50.0, 0.0, 0.0), (0.0, 9.0, 4.0) })
        {
            var a = SceneCoordinates.BodyToWorldOffset(f, s, u, 137, 4.0, -1.5, 0.0);
            var b = SceneCoordinates.BodyToWorldOffset(f, s, u, 137, 4.0, -1.5, 0.8);
            var c = SceneCoordinates.BodyToWorldOffset(f, s, u, 137, 4.0, -1.5, -0.8);
            Assert.Equal(a.X, b.X);
            Assert.Equal(a.Z, b.Z);
            Assert.Equal(0.8, b.Y - a.Y, 12);   // 向上為正
            Assert.Equal(-0.8, c.Y - a.Y, 12);
        }
    }

    [Fact]
    public void BowUpPitchRaisesBowInWorldFrameForAnyHeading()
    {
        // 航向 000:艏前 40 m 的點艏仰 3° → 北 40·cos3°、高 +40·sin3°
        var (e, n, up) = SceneCoordinates.ToEnu(SceneCoordinates.BodyToWorldOffset(40, 0, 0, 0, 0, 3, 0));
        Assert.Equal(0.0, e, 12);
        Assert.Equal(40 * Math.Cos(3 * Deg), n, 12);
        Assert.Equal(40 * Math.Sin(3 * Deg), up, 12);
        // 航向 090:同一點在東方,高度相同
        var (e2, n2, up2) = SceneCoordinates.ToEnu(SceneCoordinates.BodyToWorldOffset(40, 0, 0, 90, 0, 3, 0));
        Assert.Equal(40 * Math.Cos(3 * Deg), e2, 12);
        Assert.Equal(0.0, n2, 9);
        Assert.Equal(up, up2, 12);
        // 駕駛台眼位在船舯之後(−35 m、高 16 m):艏仰使眼位下沉並往艉移
        var (_, nEye, upEye) = SceneCoordinates.ToEnu(SceneCoordinates.BodyToWorldOffset(-35, 0, 16, 0, 0, 3, 0));
        Assert.True(upEye < 16.0);
        Assert.Equal(-35 * Math.Sin(3 * Deg) + 16 * Math.Cos(3 * Deg), upEye, 12);
        Assert.True(nEye < -35.0);
    }

    [Fact]
    public void CompositionOrderIsYawThenPitchThenRoll()
    {
        const double roll = 20, pitch = 10; // 刻意取大角度,讓兩種順序的差異(sin θ·sin φ ≈ 0.059)遠大於容差
        // 艏艉線只受縱搖與艏向影響:艏向單位向量的水平方位仍是艏向(東向分量為 0),仰角 = 縱搖
        var (bowE, bowN, bowU) = SceneCoordinates.ToEnu(SceneCoordinates.BodyToWorldOffset(1, 0, 0, 0, roll, pitch, 0));
        Assert.Equal(0.0, bowE, 12);
        Assert.Equal(Math.Cos(pitch * Deg), bowN, 12);
        Assert.Equal(Math.Sin(pitch * Deg), bowU, 12);
        // 右舷單位向量先橫搖(下沉 sin φ)再縱搖:下沉的右舷被艏仰帶往艏方 sin φ·sin θ(另一順序此分量為 0)
        var (stbE, stbN, stbU) = SceneCoordinates.ToEnu(SceneCoordinates.BodyToWorldOffset(0, 1, 0, 0, roll, pitch, 0));
        Assert.Equal(Math.Cos(roll * Deg), stbE, 12);
        Assert.Equal(Math.Sin(roll * Deg) * Math.Sin(pitch * Deg), stbN, 12);
        Assert.Equal(-Math.Sin(roll * Deg) * Math.Cos(pitch * Deg), stbU, 12);
        Assert.InRange(stbN, 0.0593, 0.0595);
        // 旋轉保長
        Assert.Equal(1.0, SceneCoordinates.BodyToWorldOffset(0, 1, 0, 213, roll, pitch, 0).Length, 12);
        Assert.Equal(Math.Sqrt(30 * 30 + 9 * 9 + 18 * 18), SceneCoordinates.BodyToWorldOffset(30, -9, 18, 213, roll, pitch, 0).Length, 9);
    }

    [Fact]
    public void ObserverBasisIsOrthonormalRightHandedAndLevelWithoutMotion()
    {
        // 無船體運動、航向 000、視角偏航/俯仰 0:右 = 東(+x)、上 = +y、前 = 北(−z)
        var level = SceneCoordinates.ObserverBasis(0, 0, 0, 0, 0);
        AssertPoint(new ScenePoint(1, 0, 0), level.Right);
        AssertPoint(new ScenePoint(0, 1, 0), level.Up);
        AssertPoint(new ScenePoint(0, 0, -1), level.Forward);
        // 航向 090:視線向東;視角偏航 +90(向右舷看)再疊加 → 向南(+z)
        AssertPoint(new ScenePoint(1, 0, 0), SceneCoordinates.ObserverBasis(90, 0, 0, 0, 0).Forward);
        AssertPoint(new ScenePoint(0, 0, 1), SceneCoordinates.ObserverBasis(90, 0, 0, 90, 0).Forward);
        // 視角俯仰上正
        Assert.Equal(Math.Sin(10 * Deg), SceneCoordinates.ObserverBasis(45, 0, 0, 30, 10).Forward.Y, 12);
        // 任意姿態:單位長、互相垂直、右手系(右 × 上 = −前,即 Godot 相機的 +z)
        foreach (var (h, roll, pitch, yaw, vp) in new[] { (137.0, 6.0, -2.5, 60.0, -8.0), (310.0, -12.0, 4.0, 180.0, -6.0), (0.0, 20.0, 10.0, -60.0, 15.0) })
        {
            var a = SceneCoordinates.ObserverBasis(h, roll, pitch, yaw, vp);
            Assert.Equal(1.0, a.Right.Length, 12);
            Assert.Equal(1.0, a.Up.Length, 12);
            Assert.Equal(1.0, a.Forward.Length, 12);
            Assert.Equal(0.0, Dot(a.Right, a.Up), 12);
            Assert.Equal(0.0, Dot(a.Right, a.Forward), 12);
            Assert.Equal(0.0, Dot(a.Up, a.Forward), 12);
            AssertPoint(new ScenePoint(-a.Forward.X, -a.Forward.Y, -a.Forward.Z), Cross(a.Right, a.Up), 1e-12);
        }
    }

    [Fact]
    public void BowUpPitchLowersHorizonInBridgeViewAndRaisesItInAftView()
    {
        // 駕駛台前視(偏航 0、俯仰 −2°):無船體運動時相機俯視 2° → 地平線在畫面中心上方 2°
        var (elev0, tilt0) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(45, 0, 0, 0, -2));
        Assert.Equal(2.0, elev0, 9);
        Assert.Equal(0.0, tilt0, 9);
        // 艏仰 +3°(pitch_deg 為正):視線上抬 3° → 地平線下移 3°(到畫面中心下方 1°),不傾斜
        var (elevUp, tiltUp) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(45, 0, 3, 0, -2));
        Assert.Equal(-1.0, elevUp, 9);
        Assert.True(elevUp < elev0);
        Assert.Equal(0.0, tiltUp, 9);
        // 艏俯 −3°:地平線上移 3°
        var (elevDown, _) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(45, 0, -3, 0, -2));
        Assert.Equal(5.0, elevDown, 9);
        // 艉視(偏航 180°、俯仰 −6°):艏仰時艉部下沉 → 視線下俯 → 地平線上移 3°
        var (aft0, _) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(45, 0, 0, 180, -6));
        var (aftUp, aftTilt) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(45, 0, 3, 180, -6));
        Assert.Equal(6.0, aft0, 9);
        Assert.Equal(9.0, aftUp, 9);
        Assert.Equal(0.0, aftTilt, 9);
    }

    [Fact]
    public void RollTiltsHorizonAndCombinesWithPitchInFixedOrder()
    {
        // 右傾 +5°、駕駛台前視:相機順時針橫滾 → 地平線右端升高 5°(畫面向左傾斜);艉視反號
        var (_, tilt) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(0, 5, 0, 0, 0));
        Assert.Equal(5.0, tilt, 9);
        var (_, tiltAft) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(0, 5, 0, 180, 0));
        Assert.Equal(-5.0, tiltAft, 9);
        // 橫搖 + 縱搖同時(駕駛台前視、視角俯仰 0):視線即艏艉線 → 仰角只由縱搖決定(順序:先橫搖再縱搖,艏艉線不受橫搖影響)
        var both = SceneCoordinates.ObserverBasis(0, 20, 10, 0, 0);
        var (elevBoth, tiltBoth) = SceneCoordinates.HorizonInView(both);
        Assert.Equal(-10.0, elevBoth, 9);
        Assert.Equal(0.0, both.Forward.X, 12); // 視線水平方位仍在艏向上
        Assert.Equal(20.0, tiltBoth, 9);       // 繞視線的橫滾即橫搖角
    }

    [Fact]
    public void ObserverMotionComponentsMixRollAndPitchByViewYaw()
    {
        // 縱搖為 0 時與既有的 ObserverHeelComponents 相同
        foreach (var yaw in new[] { 0.0, 60.0, -60.0, 90.0, 180.0 })
        {
            var (r, p) = SceneCoordinates.ObserverMotionComponents(6, 0, yaw);
            var (r0, p0) = SceneCoordinates.ObserverHeelComponents(6, yaw);
            Assert.Equal(r0, r, 12);
            Assert.Equal(p0, p, 12);
        }
        var (rb, pb) = SceneCoordinates.ObserverMotionComponents(0, 3, 0);     // 駕駛台前視:艏仰全為上仰
        Assert.Equal(0.0, rb, 12);
        Assert.Equal(3.0, pb, 12);
        var (ra, pa) = SceneCoordinates.ObserverMotionComponents(0, 3, 180);   // 艉視:下俯
        Assert.InRange(ra, -1e-9, 1e-9);
        Assert.Equal(-3.0, pa, 9);
        var (rs, ps) = SceneCoordinates.ObserverMotionComponents(0, 3, 90);    // 正右舷看:艏仰全化為順時針橫滾(畫面右側為艉方,下沉)
        Assert.Equal(3.0, rs, 9);
        Assert.InRange(ps, -1e-9, 1e-9);
        var (rw, pw) = SceneCoordinates.ObserverMotionComponents(0, 3, 60);    // 右翼橋 60°:橫滾 2.6°、上仰 1.5°
        Assert.InRange(rw, 2.59, 2.61);
        Assert.InRange(pw, 1.49, 1.51);
        // 小角度近似與精確合成(ObserverBasis → HorizonInView)一致:右翼橋、右傾 2°、艏仰 1.5°
        var (rollApprox, pitchApprox) = SceneCoordinates.ObserverMotionComponents(2, 1.5, 60);
        var (elev, tilt) = SceneCoordinates.HorizonInView(SceneCoordinates.ObserverBasis(0, 2, 1.5, 60, 0));
        Assert.InRange(tilt, rollApprox - 0.02, rollApprox + 0.02);
        Assert.InRange(-elev, pitchApprox - 0.02, pitchApprox + 0.02);
    }

    private static double Dot(ScenePoint a, ScenePoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static ScenePoint Cross(ScenePoint a, ScenePoint b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
