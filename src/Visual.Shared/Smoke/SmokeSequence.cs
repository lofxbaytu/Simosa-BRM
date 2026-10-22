using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Visual.Shared.Area;

namespace SimosaBRM.Visual.Shared.Smoke;

/// <summary>
/// 無頭煙霧測試用的假狀態序列(不連 Host):仿 E05(高雄港外,原點 22.53N 120.24E,自船 FSB1 航向 045、8 kn,
/// 三艘目標船與一艘跟隨的引水船、一艘拖船、右錨示意、一個雨區)。25 Hz,直線運動,ROT 固定;純粹供視景功能驗證,
/// 非物理(規劃書第 10.1 節:視景不做物理)。
/// 自第 <see cref="BerthSegmentStartFrame"/> 幀(t = 2 s)起另帶示意泊位 <see cref="SmokeBerthId"/> 與四條繫纜
/// (正常/黃/紅/斷纜各一;泊位幾何由 <see cref="SmokeBerthArea"/> 給出、沿自船航跡左舷 22 m,船仍直線前進 — 只驗證繫纜節點,非物理)。
/// 每幀亦帶 utc(2026-05-05T22:30:00Z + t,高雄當地 06:30,日出前後)供日夜自動模式驗證。
/// </summary>
public static class SmokeSequence
{
    public const double OriginLat = 22.53;
    public const double OriginLon = 120.24;
    public const double Hz = 25.0;
    /// <summary>自此幀起狀態帶 berth(t = 2 s)</summary>
    public const int BerthSegmentStartFrame = 50;
    public const string SmokeBerthId = "SMOKE-BERTH";
    public const string SmokeAreaId = "smoke-berth";
    public static readonly DateTimeOffset StartUtc = new(2026, 5, 5, 22, 30, 0, TimeSpan.Zero);

    public static LocalTangentPlane Origin => new(OriginLat, OriginLon);

    public static OwnShipState Frame(int index, bool withRadarEnvironment = false)
    {
        var t = index / Hz;
        var plane = Origin;
        var ownPose = Advance(-3600.0, -3900.0, 45.0, 8.0, 0.0, t);
        var (lat, lon) = plane.ToGeodetic(ownPose.X, ownPose.Y);
        var targets = new List<TargetState>
        {
            Target("T1", "EVER ORIENT", 416300101, "cargo", 200, 32, 11, Advance(1540.0, 2220.0, 225.0, 10.0, 0.0, t), ownPose, true, "powerDriven", "colreg", plane),
            Target("T2", "GOLDEN WAVE", 416300102, "tanker", 150, 24, 9, Advance(5000.0, -500.0, 270.0, 8.0, 0.0, t), ownPose, true, "powerDriven", "colreg", plane),
            Target("T3", "CHIN FU 66", 416300103, "fishing", 25, 6, 2.5, Advance(-2800.0, 2000.0, 135.0, 7.0, 2.0, t), ownPose, false, "fishing", "colreg", plane),
            Target("PILOT", "KAOHSIUNG PILOT 3", 416300104, "pilot", 18, 5, 1.8, Advance(-3600.0 + 60, -3900.0 - 60, 45.0, 8.0, 0.0, t), ownPose, true, "pilot", "follow", plane),
        };
        var tugPose = Advance(-3600.0 - 40, -3900.0 + 10, 45.0, 8.0, 0.0, t);
        var (tLat, tLon) = plane.ToGeodetic(tugPose.X, tugPose.Y);
        return new OwnShipState
        {
            T = t,
            Tick = index * 2,
            Utc = StartUtc.AddSeconds(t).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ShipId = "FSB1",
            Pos = new GeoPosition { Lat = lat, Lon = lon, X = ownPose.X, Y = ownPose.Y },
            Heading = 45.0,
            Cog = 46.0,
            Sog = 8.0,
            Stw = 7.8,
            Rot = 0.0,
            U = 4.0,
            V = 0.1,
            R = 0.0,
            Rudder = 2.0,
            RudderOrder = 2.0,
            Rpm = 100,
            RpmOrder = 100,
            Telegraph = TelegraphOrder.HAH,
            DepthBelowKeel = 19.3,
            WaterDepth = 25,
            Wind = new WindState { TrueSpeed = 10, TrueDir = 225, RelSpeed = 4, RelDir = 180 },
            Current = new CurrentState { Set = 315, Drift = 1.2 },
            Loading = LoadingCondition.Full,
            Flags = new StateFlags(),
            Targets = targets,
            VisibilityNm = 6.0,
            Tugs = new[]
            {
                new TugStatusState { Id = "TG1", Name = "高港21號", Mode = "push", Status = "working", ForceT = 20, DirectionDeg = 90, Pos = new GeoPosition { Lat = tLat, Lon = tLon, X = tugPose.X, Y = tugPose.Y } },
            },
            Anchors = new[]
            {
                new AnchorStatusState { Side = "port", Status = "stowed", LengthM = 0, TensionT = 0, OnBottomM = 0 },
                new AnchorStatusState { Side = "starboard", Status = "stowed", LengthM = 0, TensionT = 0, OnBottomM = 0 },
            },
            Berth = index >= BerthSegmentStartFrame ? SmokeBerth() : null,
            RadarEnvironment = withRadarEnvironment ? new RadarEnvironmentState
            {
                RainAreas = new[]
                {
                    new RadarRainAreaState { Id = "rain1", Intensity = 0.6, RateMmH = 9.6, Center = new GeoPosition { Lat = lat, Lon = lon, X = -2000, Y = -2000 }, RadiusM = 1500 },
                },
                SeaState = 3,
            } : null,
        };
    }

    /// <summary>
    /// 示意泊位的繫纜狀態(自 BerthSegmentStartFrame 起每幀相同):HEAD 12/50 t(正常)、FWDSPRING 收纜 32/50 t(黃)、
    /// AFTSPRING 44/50 t(紅)、STERN 斷纜(不畫、閃爍)。
    /// </summary>
    public static BerthStatusState SmokeBerth() => new()
    {
        Id = SmokeBerthId,
        Name = "煙霧示意泊位",
        Contacts = Array.Empty<FenderContactState>(),
        QuayContact = false,
        Lines = new[]
        {
            new MooringLineState { Id = "HEAD", Status = "fast", TensionT = 12, MblT = 50, BollardId = "P4", LengthM = 60 },
            new MooringLineState { Id = "FWDSPRING", Status = "heaving", TensionT = 32, MblT = 50, BollardId = "P3", LengthM = 40 },
            new MooringLineState { Id = "AFTSPRING", Status = "fast", TensionT = 44, MblT = 50, BollardId = "P2", LengthM = 40 },
            new MooringLineState { Id = "STERN", Status = "parted", TensionT = 0, MblT = 50, BollardId = "P1" },
        },
    };

    /// <summary>
    /// 示意泊位幾何(ENU):碼頭線沿航向 045、在自船 t = 2 s 位置的左舷 22 m(半船寬 9 m + 碰墊 + 餘裕),長 160 m、水側為右;
    /// 繫船柱 P1–P4 在 chainage 10/50/110/150 m、退縮 3 m;無碰墊。供 Main 於煙霧模式加入港口幾何,使繫纜找得到繫船柱。
    /// </summary>
    public static AreaGeometry SmokeBerthArea()
    {
        var (x, y, h) = Advance(-3600.0, -3900.0, 45.0, 8.0, 0.0, BerthSegmentStartFrame / Hz);
        var hr = Units.DegToRad(h);
        var (fe, fn) = (Math.Sin(hr), Math.Cos(hr));       // 艏向單位向量
        var (pe, pn) = (-Math.Cos(hr), Math.Sin(hr));      // 左舷單位向量
        const double offset = 22.0, half = 80.0;
        var a = new EnuPoint(x + pe * offset - fe * half, y + pn * offset - fn * half);
        var b = new EnuPoint(x + pe * offset + fe * half, y + pn * offset + fn * half);
        var quay = new[] { a, b };
        var bollards = new List<(string Id, EnuPoint Pos)>();
        var chainages = new[] { 10.0, 50.0, 110.0, 150.0 };
        for (var i = 0; i < chainages.Length; i++) bollards.Add(("P" + (i + 1), AreaLoader.AlongQuay(quay, "right", chainages[i], -3.0)));
        return new AreaGeometry
        {
            Id = SmokeAreaId,
            SourceFile = "(smoke)",
            Berths = new[] { new BerthGeometry(SmokeBerthId, "煙霧示意泊位", quay, "right", Array.Empty<(string, EnuPoint)>(), bollards) },
        };
    }

    /// <summary>直線運動 + 固定 ROT(度/分)的近似位置(視景驗證用)。</summary>
    public static (double X, double Y, double Heading) Advance(double x0, double y0, double heading0, double sogKn, double rotDegPerMin, double t)
    {
        var v = Units.KnToMps(sogKn);
        var heading = Units.NormalizeHeadingDeg(heading0 + rotDegPerMin / 60.0 * t);
        var h = Units.DegToRad(heading0 + rotDegPerMin / 60.0 * t / 2); // 平均航向
        return (x0 + v * Math.Sin(h) * t, y0 + v * Math.Cos(h) * t, heading);
    }

    private static TargetState Target(string id, string name, int mmsi, string type, double loa, double beam, double draft,
        (double X, double Y, double Heading) pose, (double X, double Y, double Heading) own, bool ais, string lights, string behaviour, LocalTangentPlane plane)
    {
        var (lat, lon) = plane.ToGeodetic(pose.X, pose.Y);
        var dx = pose.X - own.X;
        var dy = pose.Y - own.Y;
        var range = Math.Sqrt(dx * dx + dy * dy) / Units.MetresPerNauticalMile;
        var bearing = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(dx, dy)));
        return new TargetState
        {
            Id = id,
            Name = name,
            Mmsi = mmsi,
            ShipType = type,
            Loa = loa,
            Beam = beam,
            Draft = draft,
            Pos = new GeoPosition { Lat = lat, Lon = lon, X = pose.X, Y = pose.Y },
            Heading = pose.Heading,
            Cog = pose.Heading,
            Sog = id == "PILOT" ? 8.0 : id == "T3" ? 7.0 : id == "T2" ? 8.0 : 10.0,
            Rot = id == "T3" ? 2.0 : 0.0,
            RangeNm = range,
            BearingDeg = bearing,
            RelBearingDeg = Units.NormalizeHeadingDeg(bearing - own.Heading),
            CpaNm = 0.5,
            TcpaMin = 10,
            AisOn = ais,
            Lights = lights,
            Behaviour = behaviour,
        };
    }
}
