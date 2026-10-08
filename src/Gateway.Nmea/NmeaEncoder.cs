using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 由 OwnShipState 產生 NMEA 0183 句型(規劃書第 5.2 節 NMEA Gateway;第 7.2 節儀器)。
/// 發話者代碼:GP(GNSS)、HE(電羅經)、TI(迴轉率指示器)、VD(都卜勒計程儀)、SD(測深儀)、WI(風儀)、ER(舵角,機艙/舵機監視)。
/// </summary>
public static class NmeaEncoder
{
    public const string TalkerGnss = "GP";
    public const string TalkerGyro = "HE";
    public const string TalkerRot = "TI";
    public const string TalkerLog = "VD";
    public const string TalkerSounder = "SD";
    public const string TalkerWind = "WI";
    public const string TalkerRudder = "ER";

    /// <summary>10 Hz 句型:HDT、ROT、RSA。</summary>
    public static IReadOnlyList<string> FastSentences(OwnShipState s) => new[] { Hdt(s), Rot(s), Rsa(s) };

    /// <summary>1 Hz 句型:GGA、RMC、VTG、VBW、DPT、MWV(相對)、MWV(真風)。</summary>
    public static IReadOnlyList<string> SlowSentences(OwnShipState s, DateTime utc)
        => new[] { Gga(s, utc), Rmc(s, utc), Vtg(s), Vbw(s), Dpt(s), MwvRelative(s), MwvTrue(s) };

    /// <summary>GGA:定位、時間、品質 1(GPS)、10 顆衛星、HDOP 0.9、天線高 0 m。</summary>
    public static string Gga(OwnShipState s, DateTime utc)
    {
        var (lat, ns) = NmeaSentence.FormatLatitude(s.Pos.Lat);
        var (lon, ew) = NmeaSentence.FormatLongitude(s.Pos.Lon);
        return NmeaSentence.Build(TalkerGnss, "GGA", NmeaSentence.FormatTime(utc), lat, ns, lon, ew,
            "1", "10", "0.9", "0.0", "M", "0.0", "M", "", "");
    }

    /// <summary>RMC:時間、狀態 A、位置、SOG(kn)、COG(真)、日期、磁差空白、模式 A(NMEA 2.3)。</summary>
    public static string Rmc(OwnShipState s, DateTime utc)
    {
        var (lat, ns) = NmeaSentence.FormatLatitude(s.Pos.Lat);
        var (lon, ew) = NmeaSentence.FormatLongitude(s.Pos.Lon);
        return NmeaSentence.Build(TalkerGnss, "RMC", NmeaSentence.FormatTime(utc), "A", lat, ns, lon, ew,
            NmeaSentence.F1(s.Sog), NmeaSentence.F1(s.Cog), NmeaSentence.FormatDate(utc), "", "", "A");
    }

    /// <summary>VTG:COG 真/磁(磁空白)、SOG kn 與 km/h、模式 A。</summary>
    public static string Vtg(OwnShipState s)
        => NmeaSentence.Build(TalkerGnss, "VTG", NmeaSentence.F1(s.Cog), "T", "", "M",
            NmeaSentence.F1(s.Sog), "N", NmeaSentence.F1(s.Sog * 1.852), "K", "A");

    /// <summary>HDT:真航向(電羅經)。</summary>
    public static string Hdt(OwnShipState s) => NmeaSentence.Build(TalkerGyro, "HDT", NmeaSentence.F1(s.Heading), "T");

    /// <summary>ROT:迴轉率 度/分,右轉正,狀態 A。</summary>
    public static string Rot(OwnShipState s) => NmeaSentence.Build(TalkerRot, "ROT", NmeaSentence.F1(s.Rot), "A");

    /// <summary>
    /// VBW:縱向/橫向對水速度、縱向/橫向對地速度(kn,橫向右正),艉部欄位無效(V)。
    /// 對地橫向分量由 SOG/COG 投影到船體座標求得。
    /// </summary>
    public static string Vbw(OwnShipState s)
    {
        var rel = Units.DegToRad(s.Cog - s.Heading);
        var groundLon = s.Sog * Math.Cos(rel);
        var groundTrans = s.Sog * Math.Sin(rel);
        return NmeaSentence.Build(TalkerLog, "VBW",
            NmeaSentence.F1(s.Stw), NmeaSentence.F1(Units.MpsToKn(s.V)), "A",
            NmeaSentence.F1(groundLon), NmeaSentence.F1(groundTrans), "A",
            "", "V", "", "V");
    }

    /// <summary>DPT:龍骨下水深(換能器視為位於龍骨,偏移 0.0),最大量程空白。</summary>
    public static string Dpt(OwnShipState s)
        => NmeaSentence.Build(TalkerSounder, "DPT", NmeaSentence.F1(Math.Max(0.0, s.DepthBelowKeel)), "0.0", "");

    /// <summary>MWV(R):相對風角(相對艏向 0–360)與相對風速 kn。</summary>
    public static string MwvRelative(OwnShipState s)
        => NmeaSentence.Build(TalkerWind, "MWV", NmeaSentence.F1(s.Wind.RelDir ?? 0.0), "R", NmeaSentence.F1(s.Wind.RelSpeed ?? 0.0), "N", "A");

    /// <summary>MWV(T):真風角(真風來向相對艏向)與真風速 kn(NMEA 定義的 T 為相對船艏的真風)。</summary>
    public static string MwvTrue(OwnShipState s)
        => NmeaSentence.Build(TalkerWind, "MWV", NmeaSentence.F1(Units.NormalizeHeadingDeg(s.Wind.TrueDir - s.Heading)), "T",
            NmeaSentence.F1(s.Wind.TrueSpeed), "N", "A");

    /// <summary>RSA:右舵(單舵)舵角,右正,狀態 A;左舵欄位無效。</summary>
    public static string Rsa(OwnShipState s) => NmeaSentence.Build(TalkerRudder, "RSA", NmeaSentence.F1(s.Rudder), "A", "", "V");
}
