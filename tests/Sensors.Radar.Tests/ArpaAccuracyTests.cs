using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.SimCore.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 規劃書第 14 章「ARPA CPA/TCPA 計算 vs 解析值,CPA 誤差 &lt; 0.02 nm」:由雷達影像偵測 + 追蹤得到的 CPA/TCPA,
/// 在穩定追蹤 3 分鐘後與等速直線解析值比較(對遇、橫越、追越、相反舷);另驗證 MSC.192(79) 3 分鐘精度(航向 3°、速度 0.5 kn、TCPA 0.5 分)。
/// </summary>
public class ArpaAccuracyTests
{
    private readonly ITestOutputHelper _out;
    public ArpaAccuracyTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Geometries() => new[]
    {
        // 名稱、自船航向/速度、目標起點 (x, y)、目標航向/速度
        new object[] { "headOn", 0.0, 12.0, 900.0, 11000.0, 180.0, 10.0 },
        new object[] { "crossingStbd", 0.0, 10.0, 7000.0, 7000.0, 270.0, 12.0 },
        new object[] { "crossingPort", 45.0, 8.0, -5500.0, 4500.0, 120.0, 9.0 },
        new object[] { "overtaking", 0.0, 14.0, -400.0, 2600.0, 0.0, 6.0 },
        new object[] { "overtaken", 90.0, 6.0, -3200.0, -700.0, 90.0, 15.0 },
    };

    [Theory]
    [MemberData(nameof(Geometries))]
    public void TrackedCpaMatchesAnalyticAfterThreeMinutes(string name, double ownCourse, double ownSpeed, double tx, double ty, double tCourse, double tSpeed)
    {
        var scene = new LinearScene { OwnCourseDeg = ownCourse, OwnSpeedKn = ownSpeed, WindKn = 8, WindFromDeg = 200 };
        scene.Targets.Add(new LinearTarget("T", tx, ty, tCourse, tSpeed));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0.0, 2.0);
        var g0 = scene.Truth("T", 2.0);
        var ack = radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g0.BearingRad):F2},\"range\":{g0.RangeNm:F3}}}");
        Assert.True(ack.Ok, ack.Error);
        var id = ack.Id!.Value;

        // 擷取 → 1 分鐘趨勢 → 再穩定追蹤 3 分鐘
        scene.Run(radar, 2.04, 245.0);
        var tr = radar.Arpa.Tracks.Single(t => t.Id == id);
        Assert.Equal(TrackState.Tracking, tr.State);
        var s = radar.Arpa.Solve(tr, 245.0, null);
        var truth = scene.Truth("T", 245.0);
        var cpaErr = Math.Abs(s.CpaNm - truth.CpaNm);
        var tcpaErr = Math.Abs(s.TcpaMin - truth.TcpaMin);
        var courseErr = Math.Abs(Units.WrapDeg180(s.CourseDeg - tCourse));
        var speedErr = Math.Abs(s.SpeedKn - tSpeed);
        _out.WriteLine($"{name}: CPA ARPA {s.CpaNm:F4} / 真值 {truth.CpaNm:F4} nm(誤差 {cpaErr:F4});TCPA {s.TcpaMin:F2} / {truth.TcpaMin:F2} 分;航向誤差 {courseErr:F2}°、速度誤差 {speedErr:F3} kn;距離 {truth.RangeNm:F2} nm;命中 {tr.Hits}");
        Assert.True(cpaErr < 0.02, $"{name} CPA 誤差 {cpaErr:F4} nm ≥ 0.02 nm");
        Assert.True(tcpaErr < 0.5, $"{name} TCPA 誤差 {tcpaErr:F2} 分");
        Assert.True(courseErr < 3.0, $"{name} 航向誤差 {courseErr:F2}°");
        Assert.True(speedErr < 0.5, $"{name} 速度誤差 {speedErr:F3} kn");
    }
}
