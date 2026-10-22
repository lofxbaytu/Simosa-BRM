using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 等速直線目標(ENU 起點 m、航向度、速度 kn)。<paramref name="AisOn"/> 時狀態含 AIS 報告(MMSI = <paramref name="Mmsi"/>、船名 = Id),
/// 報告位置 = 真位置 + (<paramref name="AisErrEast"/>, <paramref name="AisErrNorth"/>)(模擬 aisError 注入)。
/// </summary>
internal sealed record LinearTarget(string Id, double X0, double Y0, double CourseDeg, double SpeedKn, double Loa = 120, double Beam = 20, string Type = "cargo", double Draft = 7,
    bool AisOn = false, int Mmsi = 0, double AisErrEast = 0, double AisErrNorth = 0)
{
    public (double X, double Y) At(double t)
    {
        var v = Units.KnToMps(SpeedKn);
        var c = Units.DegToRad(CourseDeg);
        return (X0 + v * Math.Sin(c) * t, Y0 + v * Math.Cos(c) * t);
    }

    public (double Ve, double Vn) Vel => (Units.KnToMps(SpeedKn) * Math.Sin(Units.DegToRad(CourseDeg)), Units.KnToMps(SpeedKn) * Math.Cos(Units.DegToRad(CourseDeg)));
}

/// <summary>
/// 測試場景:自船與目標皆等速直線,依時間產生 SimCore 契約的 <see cref="OwnShipState"/>(含 targets[] 真值 CPA/TCPA,
/// 與 SimCore <see cref="Encounter"/> 同一定義)。不需網路,直接餵給 <see cref="RadarUnit"/>。
/// </summary>
internal sealed class LinearScene
{
    private static readonly LocalTangentPlane Ltp = new(22.53, 120.24);
    public double OwnX0 { get; init; }
    public double OwnY0 { get; init; }
    public double OwnCourseDeg { get; init; }
    public double OwnSpeedKn { get; init; }
    public double WindKn { get; init; }
    public double WindFromDeg { get; init; }
    public List<LinearTarget> Targets { get; } = new();
    public List<string> Faults { get; } = new();
    public string ScenarioId { get; init; } = "T_radar";
    /// <summary>電羅經指示誤差(度;指示 = 真 + 誤差)</summary>
    public double HeadingErrorDeg { get; set; }
    /// <summary>情境雷達環境(state.radarEnvironment;null = 情境未給)</summary>
    public SimosaBRM.SimCore.Scenario.RadarEnvironmentState? RadarEnvironment { get; set; }

    public (double X, double Y) OwnAt(double t)
    {
        var v = Units.KnToMps(OwnSpeedKn);
        var c = Units.DegToRad(OwnCourseDeg);
        return (OwnX0 + v * Math.Sin(c) * t, OwnY0 + v * Math.Cos(c) * t);
    }

    public (double Ve, double Vn) OwnVel => (Units.KnToMps(OwnSpeedKn) * Math.Sin(Units.DegToRad(OwnCourseDeg)), Units.KnToMps(OwnSpeedKn) * Math.Cos(Units.DegToRad(OwnCourseDeg)));

    /// <summary>目標 id 於時間 t 的真值會遇幾何。</summary>
    public EncounterGeometry Truth(string id, double t)
    {
        var tg = Targets.Single(x => x.Id == id);
        var (ox, oy) = OwnAt(t);
        var (tx, ty) = tg.At(t);
        var (ove, ovn) = OwnVel;
        var (tve, tvn) = tg.Vel;
        return Encounter.Compute(ox, oy, ove, ovn, Units.DegToRad(OwnCourseDeg), tx, ty, tve, tvn);
    }

    public OwnShipState StateAt(double t)
    {
        var (ox, oy) = OwnAt(t);
        var (lat, lon) = Ltp.ToGeodetic(ox, oy);
        var pos = new GeoPosition { Lat = lat, Lon = lon, X = ox, Y = oy };
        var targets = new List<TargetState>();
        foreach (var tg in Targets)
        {
            var (tx, ty) = tg.At(t);
            var (tl, tn) = Ltp.ToGeodetic(tx, ty);
            var g = Truth(tg.Id, t);
            AisReport? ais = null;
            if (tg.AisOn)
            {
                var (al, an) = Ltp.ToGeodetic(tx + tg.AisErrEast, ty + tg.AisErrNorth);
                ais = new AisReport
                {
                    Mmsi = tg.Mmsi, Name = tg.Id, ShipType = 70, NavStatus = 0, Lat = al, Lon = an, Cog = tg.CourseDeg, Sog = tg.SpeedKn, Heading = tg.CourseDeg, Rot = 0,
                    DimToBow = tg.Loa / 2, DimToStern = tg.Loa / 2, DimToPort = tg.Beam / 2, DimToStarboard = tg.Beam / 2, Draught = tg.Draft,
                };
            }
            targets.Add(new TargetState
            {
                Id = tg.Id, Name = tg.Id, Mmsi = tg.Mmsi, ShipType = tg.Type, Loa = tg.Loa, Beam = tg.Beam, Draft = tg.Draft,
                Pos = new GeoPosition { Lat = tl, Lon = tn, X = tx, Y = ty },
                Heading = tg.CourseDeg, Cog = tg.CourseDeg, Sog = tg.SpeedKn, Rot = 0,
                RangeNm = g.RangeNm, BearingDeg = Units.RadToDeg(g.BearingRad), CpaNm = g.CpaNm, TcpaMin = g.TcpaMin,
                BcrNm = g.BcrNm, BctMin = g.BctMin, AisOn = tg.AisOn, Lights = "powerDriven", Behaviour = "hold", Ais = ais,
            });
        }
        var indicated = Units.NormalizeHeadingDeg(OwnCourseDeg + HeadingErrorDeg);
        return new OwnShipState
        {
            T = t,
            Tick = (long)Math.Round(t * 50),
            ShipId = "FSB1",
            Pos = pos,
            Heading = indicated,
            Cog = OwnCourseDeg,
            Sog = OwnSpeedKn,
            Stw = OwnSpeedKn,
            Rot = 0, U = Units.KnToMps(OwnSpeedKn), V = 0, R = 0,
            Rudder = 0, RudderOrder = 0, Rpm = 100, RpmOrder = 100,
            Telegraph = TelegraphOrder.HAH,
            DepthBelowKeel = 20,
            Wind = new WindState { TrueSpeed = WindKn, TrueDir = WindFromDeg },
            Current = new CurrentState { Set = 0, Drift = 0 },
            Loading = LoadingCondition.Full,
            Faults = Faults.ToArray(),
            Targets = targets,
            Scenario = new ScenarioStatus { Id = ScenarioId, Phase = "run" },
            Truth = new TruthState
            {
                Heading = OwnCourseDeg, Pos = pos, Cog = OwnCourseDeg, Sog = OwnSpeedKn, Stw = OwnSpeedKn, DepthBelowKeel = 20,
                HeadingErrorDeg = HeadingErrorDeg, PositionErrorM = 0,
            },
            Sensors = new SensorStatus { HeadingValid = true, PositionValid = true, DepthValid = true, StwValid = true, MagneticHeading = OwnCourseDeg },
            RadarEnvironment = RadarEnvironment,
        };
    }

    /// <summary>以 25 Hz 狀態推進雷達(t0 → t1),回傳所有步驟結果。</summary>
    public List<RadarStepResult> Run(RadarUnit unit, double t0, double t1, double dt = 0.04, Action<double, RadarStepResult>? each = null)
    {
        var list = new List<RadarStepResult>();
        var n = (int)Math.Round((t1 - t0) / dt);
        for (var i = 0; i <= n; i++)
        {
            var t = t0 + i * dt;
            var r = unit.Process(StateAt(t));
            each?.Invoke(t, r);
            list.Add(r);
        }
        return list;
    }
}

internal static class RadarFactory
{
    /// <summary>測試用 X 頻雷達:天線高 20 m、位於參考點(無偏移),無陰影扇區(除非指定)。</summary>
    public static RadarUnit X(ulong seed = 42, bool shadow = false, RadarEnvironment? env = null, Action<RadarConfig>? configure = null)
    {
        var cfg = RadarConfig.XBand(20.0);
        if (!shadow) cfg.ShadowSectors.Clear();
        configure?.Invoke(cfg);
        return new RadarUnit(cfg, seed, env);
    }

    public static RadarUnit S(ulong seed = 42, bool shadow = false)
    {
        var cfg = RadarConfig.SBand(18.0);
        if (!shadow) cfg.ShadowSectors.Clear();
        return new RadarUnit(cfg, seed);
    }

    /// <summary>收集一整圈(以相對方位索引排列)的顯示影像:回傳 [spoke][cell]。</summary>
    public static byte[][] CollectScan(IEnumerable<RadarStepResult> results, int spokes)
    {
        var img = new byte[spokes][];
        foreach (var r in results)
            foreach (var f in r.Frames)
            {
                var fr = SpokeFrame.Parse(f);
                for (var j = 0; j < fr.SpokeCount; j++) img[(fr.FirstSpoke + j) % spokes] = fr.Spoke(j).ToArray();
            }
        return img;
    }
}
