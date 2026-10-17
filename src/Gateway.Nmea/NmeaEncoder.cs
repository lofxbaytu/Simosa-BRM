using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 由 OwnShipState 產生 NMEA 0183 句型(規劃書第 5.2 節 NMEA Gateway;第 7.2 節儀器)。
/// 發話者代碼:GP(GNSS)、HE(電羅經)、TI(迴轉率指示器)、VD(都卜勒計程儀)、SD(測深儀)、WI(風儀)、ER(舵角,機艙/舵機監視)、
/// II(整合儀器;警報 ALR)。所有句型取 state 的「指示值」;感測器無效時(state.sensors)依 IEC 61162-1:無狀態欄位的句型(HDT、DPT)停送,
/// 有狀態/模式欄位的句型(GGA 品質 0、RMC 狀態 V/模式 N、VTG 模式 N、VBW 狀態 V)輸出空欄位與無效旗標(設計文件 sensors-alerts.md 第 6 節)。
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
    /// <summary>雷達/ARPA(TTM)</summary>
    public const string TalkerRadar = "RA";
    /// <summary>整合儀器/警報管理(ALR)</summary>
    public const string TalkerAlert = "II";

    /// <summary>10 Hz 句型:HDT、ROT、RSA。</summary>
    public static IReadOnlyList<string> FastSentences(OwnShipState s)
        => HeadingValid(s) ? new[] { Hdt(s), Rot(s), Rsa(s) } : new[] { Rot(s), Rsa(s) };

    /// <summary>1 Hz 句型:GGA、RMC、VTG、VBW、DPT、MWV(相對)、MWV(真風)。</summary>
    public static IReadOnlyList<string> SlowSentences(OwnShipState s, DateTime utc)
    {
        var list = new List<string>(8) { Gga(s, utc), Rmc(s, utc), Vtg(s), Vbw(s) };
        if (DepthValid(s)) list.Add(Dpt(s));
        list.Add(MwvRelative(s));
        list.Add(MwvTrue(s));
        return list;
    }

    private static bool HeadingValid(OwnShipState s) => s.Sensors?.HeadingValid ?? true;
    private static bool PositionValid(OwnShipState s) => s.Sensors?.PositionValid ?? true;
    private static bool DepthValid(OwnShipState s) => s.Sensors?.DepthValid ?? true;
    private static bool StwValid(OwnShipState s) => s.Sensors?.StwValid ?? true;

    /// <summary>GGA:定位、時間、品質 1(GPS)、10 顆衛星、HDOP 0.9、天線高 0 m。</summary>
    public static string Gga(OwnShipState s, DateTime utc)
    {
        // GNSS 失效:定位品質 0(無定位)、位置/衛星/HDOP 空白
        if (!PositionValid(s))
            return NmeaSentence.Build(TalkerGnss, "GGA", NmeaSentence.FormatTime(utc), "", "", "", "", "0", "00", "", "", "M", "", "M", "", "");
        var (lat, ns) = NmeaSentence.FormatLatitude(s.Pos.Lat);
        var (lon, ew) = NmeaSentence.FormatLongitude(s.Pos.Lon);
        return NmeaSentence.Build(TalkerGnss, "GGA", NmeaSentence.FormatTime(utc), lat, ns, lon, ew,
            "1", "10", "0.9", "0.0", "M", "0.0", "M", "", "");
    }

    /// <summary>RMC:時間、狀態 A、位置、SOG(kn)、COG(真)、日期、磁差空白、模式 A(NMEA 2.3)。</summary>
    public static string Rmc(OwnShipState s, DateTime utc)
    {
        // GNSS 失效:狀態 V(無效)、模式 N(資料無效)、位置/SOG/COG 空白
        if (!PositionValid(s))
            return NmeaSentence.Build(TalkerGnss, "RMC", NmeaSentence.FormatTime(utc), "V", "", "", "", "", "", "", NmeaSentence.FormatDate(utc), "", "", "N");
        var (lat, ns) = NmeaSentence.FormatLatitude(s.Pos.Lat);
        var (lon, ew) = NmeaSentence.FormatLongitude(s.Pos.Lon);
        return NmeaSentence.Build(TalkerGnss, "RMC", NmeaSentence.FormatTime(utc), "A", lat, ns, lon, ew,
            NmeaSentence.F1(s.Sog), NmeaSentence.F1(s.Cog), NmeaSentence.FormatDate(utc), "", "", "A");
    }

    /// <summary>VTG:COG 真/磁(磁空白)、SOG kn 與 km/h、模式 A。</summary>
    public static string Vtg(OwnShipState s)
        => PositionValid(s)
            ? NmeaSentence.Build(TalkerGnss, "VTG", NmeaSentence.F1(s.Cog), "T", "", "M",
                NmeaSentence.F1(s.Sog), "N", NmeaSentence.F1(s.Sog * 1.852), "K", "A")
            : NmeaSentence.Build(TalkerGnss, "VTG", "", "T", "", "M", "", "N", "", "K", "N");

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
        // 計程儀無效:對水速度欄位空白、狀態 V;GNSS 或電羅經無效:對地分量無法投影到船體座標,空白、狀態 V
        var water = StwValid(s);
        var ground = PositionValid(s) && HeadingValid(s);
        return NmeaSentence.Build(TalkerLog, "VBW",
            water ? NmeaSentence.F1(s.Stw) : "", water ? NmeaSentence.F1(Units.MpsToKn(s.V)) : "", water ? "A" : "V",
            ground ? NmeaSentence.F1(groundLon) : "", ground ? NmeaSentence.F1(groundTrans) : "", ground ? "A" : "V",
            "", "V", "", "V");
    }

    /// <summary>DPT:龍骨下水深(換能器視為位於龍骨,偏移 0.0),最大量程空白。</summary>
    public static string Dpt(OwnShipState s)
        => NmeaSentence.Build(TalkerSounder, "DPT", NmeaSentence.F1(Math.Max(0.0, s.DepthBelowKeel)), "0.0", "");

    /// <summary>MWV(R):相對風角(相對艏向 0–360)與相對風速 kn。</summary>
    public static string MwvRelative(OwnShipState s)
        => NmeaSentence.Build(TalkerWind, "MWV", NmeaSentence.F1(s.Wind.RelDir ?? 0.0), "R", NmeaSentence.F1(s.Wind.RelSpeed ?? 0.0), "N", "A");

    /// <summary>
    /// MWV(T):真風角(真風來向相對艏向)與真風速 kn(NMEA 定義的 T 為相對船艏的真風)。
    /// 相對船艏的真風由相對風與船速在船體座標推得,不經電羅經:以真航向(state.truth.heading)計算,
    /// 電羅經漂移/凍結/失效不影響此句(頂層 heading 為電羅經指示值,不可使用;無 truth 區塊時才退回)。
    /// </summary>
    public static string MwvTrue(OwnShipState s)
        => NmeaSentence.Build(TalkerWind, "MWV", NmeaSentence.F1(Units.NormalizeHeadingDeg(s.Wind.TrueDir - (s.Truth?.Heading ?? s.Heading))), "T",
            NmeaSentence.F1(s.Wind.TrueSpeed), "N", "A");

    /// <summary>RSA:右舵(單舵)舵角,右正,狀態 A;左舵欄位無效。</summary>
    public static string Rsa(OwnShipState s) => NmeaSentence.Build(TalkerRudder, "RSA", NmeaSentence.F1(s.Rudder), "A", "", "V");

    /// <summary>
    /// TTM(追蹤目標訊息,供 OpenCPN/ARPA 顯示):目標編號 01–99、距離(nm)與真方位、對地速度(kn)與對地航向(真)、
    /// CPA(nm)與 TCPA(分,負 = 已通過)、單位 N、船名(≤ 12 字)、狀態 T(追蹤中)、無參考目標、UTC 時間、取得方式 A(自動)。
    /// </summary>
    public static string Ttm(TargetState t, int number, DateTime utc)
    {
        var name = new string(t.Name.Where(c => c != ',' && c != '*' && c != '$' && c != '!' && c != '\\' && c != '^' && c != '~' && c < 127).Take(12).ToArray());
        return NmeaSentence.Build(TalkerRadar, "TTM",
            Math.Clamp(number, 0, 99).ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            NmeaSentence.F2(t.RangeNm), NmeaSentence.F1(t.BearingDeg), "T",
            NmeaSentence.F1(t.Sog), NmeaSentence.F1(t.Cog), "T",
            NmeaSentence.F2(t.CpaNm), NmeaSentence.F1(t.TcpaMin), "N",
            name, "T", "", NmeaSentence.FormatTime(utc), "A");
    }

    /// <summary>
    /// ALR(IEC 61162-1 警報狀態):時刻、警報編號(3 位)、條件 A = 成立 / V = 已解除、確認 A = 已確認 / V = 未確認、英文說明(ASCII)。
    /// 每個非 normal 的警報一句;英文文字取警報目錄的 NmeaText(中文顯示文字不得進 NMEA)。
    /// </summary>
    public static string Alr(AlertStatus a, DateTime utc)
    {
        var def = SimosaBRM.SimCore.Alerts.AlertCatalogue.Find(a.Id);
        var text = new string((def?.NmeaText ?? a.Id.ToUpperInvariant()).Where(c => c >= 0x20 && c < 0x7F && c != ',' && c != '*' && c != '$' && c != '!' && c != '\\' && c != '^' && c != '~').ToArray());
        var condition = a.State is SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged or SimosaBRM.SimCore.Alerts.AlertState.ActiveSilenced
            or SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged ? "A" : "V";
        var acked = a.State == SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged ? "A" : "V";
        return NmeaSentence.Build(TalkerAlert, "ALR", NmeaSentence.FormatTime(utc),
            Math.Clamp(a.Number, 0, 999).ToString("000", System.Globalization.CultureInfo.InvariantCulture), condition, acked, text);
    }

    /// <summary>所有目前警報的 ALR(state.alerts[] 順序)。</summary>
    public static IReadOnlyList<string> AlrSentences(OwnShipState s, DateTime utc)
        => s.Alerts is { Count: > 0 } alerts ? alerts.Select(a => Alr(a, utc)).ToArray() : Array.Empty<string>();

    /// <summary>所有目標的 TTM(編號依 targets[] 順序自 01 起)。</summary>
    public static IReadOnlyList<string> TtmSentences(OwnShipState s, DateTime utc)
    {
        if (s.Targets is not { Count: > 0 } targets) return Array.Empty<string>();
        var list = new List<string>(targets.Count);
        for (var i = 0; i < targets.Count; i++) list.Add(Ttm(targets[i], i + 1, utc));
        return list;
    }
}
