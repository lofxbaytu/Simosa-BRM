using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 由 OwnShipState 產生 NMEA 0183 句型(規劃書第 5.2 節 NMEA Gateway;第 7.2 節儀器)。
/// 發話者代碼:GP(GNSS)、HE(電羅經)、TI(迴轉率指示器)、VD(都卜勒計程儀)、SD(測深儀)、WI(風儀)、ER(舵角,機艙/舵機監視)、
/// II(整合儀器;警報 ALR/ALF/ALC)、AI(自船 AIS VDO)。所有句型取 state 的「指示值」;感測器無效時(state.sensors)依 IEC 61162-1:無狀態欄位的句型(HDT、DPT)停送,
/// 有狀態/模式欄位的句型(GGA 品質 0、RMC 狀態 V/模式 N、VTG 模式 N、VBW 狀態 V、THS 模式 V)輸出空欄位與無效旗標(設計文件 sensors-alerts.md 第 6 節)。
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

    /// <summary>10 Hz 句型:HDT、ROT、RSA、THS(電羅經無效時 HDT 停送、THS 改送模式 V)。</summary>
    public static IReadOnlyList<string> FastSentences(OwnShipState s)
        => HeadingValid(s) ? new[] { Hdt(s), Rot(s), Rsa(s), Ths(s) } : new[] { Rot(s), Rsa(s), Ths(s) };

    /// <summary>自船 AIS 的船型碼(ITU-R M.1371 表 53:80 = 油輪/液貨船;FSB1/FSB2 為瀝青船)。</summary>
    public const int OwnShipAisType = 80;

    /// <summary>ALF/ALC 的製造商代碼(IEC 61162-1:非 IEC 62923-2 標準警報識別碼時填 3 字代碼;本模擬器 SIM)。</summary>
    public const string AlertManufacturerCode = "SIM";

    /// <summary>每句 ALC 最多的警報項目數(4 項時 `$IIALC,01,01,00,4,SIM,014,1,99,…*hh` 為 72 字元 ≤ 82;5 項會到 85)。</summary>
    public const int AlcEntriesPerSentence = 4;

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

    /// <summary>
    /// THS(真艏向與模式,IEC 61162-1 ed.5;與 HDT 同批 10 Hz):模式 A = 自主(電羅經正常指示值,含漂移/凍結等仍「有效」的故障)、
    /// V = 資料無效(電羅經失效:航向欄空白)。E(估算)、M(手動輸入)、S(模擬器模式)本版不用:模擬器對外扮演真實電羅經,不以 S 標示。
    /// </summary>
    public static string Ths(OwnShipState s)
        => HeadingValid(s) ? NmeaSentence.Build(TalkerGyro, "THS", NmeaSentence.F1(s.Heading), "A") : NmeaSentence.Build(TalkerGyro, "THS", "", "V");

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
        var text = AsciiField(def?.NmeaText ?? a.Id.ToUpperInvariant());
        var condition = a.State is SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged or SimosaBRM.SimCore.Alerts.AlertState.ActiveSilenced
            or SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged ? "A" : "V";
        var acked = a.State == SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged ? "A" : "V";
        return NmeaSentence.Build(TalkerAlert, "ALR", NmeaSentence.FormatTime(utc),
            Math.Clamp(a.Number, 0, 999).ToString("000", System.Globalization.CultureInfo.InvariantCulture), condition, acked, text);
    }

    /// <summary>所有目前警報的 ALR(state.alerts[] 順序)。</summary>
    public static IReadOnlyList<string> AlrSentences(OwnShipState s, DateTime utc)
        => s.Alerts is { Count: > 0 } alerts ? alerts.Select(a => Alr(a, utc)).ToArray() : Array.Empty<string>();

    /// <summary>ALF 的警報狀態字元(IEC 61162-1 ed.5):V 成立未確認、S 已靜音、A 已確認、O 責任已轉移、U 已解除未確認、N 正常。</summary>
    public static string AlfState(AlertStatus a) => a.State switch
    {
        SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged => "V",
        SimosaBRM.SimCore.Alerts.AlertState.ActiveSilenced => "S",
        SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged => a.TransferredTo is not null ? "O" : "A",
        SimosaBRM.SimCore.Alerts.AlertState.RectifiedUnacknowledged => "U",
        _ => "N",
    };

    /// <summary>ALF 的優先序字元:E 緊急警報、A 警報、W 警告、C 注意。</summary>
    public static string AlfPriority(SimosaBRM.SimCore.Alerts.AlertPriority p) => p switch
    {
        SimosaBRM.SimCore.Alerts.AlertPriority.EmergencyAlarm => "E",
        SimosaBRM.SimCore.Alerts.AlertPriority.Alarm => "A",
        SimosaBRM.SimCore.Alerts.AlertPriority.Warning => "W",
        _ => "C",
    };

    /// <summary>ALF/ALC 的警報識別碼:與 ALR 相同的 3 位編號(警報目錄 Number)。</summary>
    public static string AlertIdentifier(int number) => Math.Clamp(number, 0, 999).ToString("000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// ALF(IEC 61162-1 ed.5 警報句;IEC 62923-1 BAM):句數 1、句號 1、序號(0–9)、最近一次狀態改變時刻、類別 A/B/C、優先序 E/A/W/C、
    /// 狀態 V/S/A/O/U/N、製造商代碼 SIM、警報識別碼(3 位,與 ALR 同)、實例 1、修訂計數(1–99,每次狀態改變 +1)、升級計數(0/1)、英文文字。
    /// 本版文字皆 ≤ 26 字,一句即可(標準允許第 2 句接續長文字)。
    /// </summary>
    public static string Alf(AlertStatus a, DateTime lastChangeUtc, int seqId, int revision)
    {
        var def = SimosaBRM.SimCore.Alerts.AlertCatalogue.Find(a.Id);
        var text = AsciiField(def?.NmeaText ?? a.Id.ToUpperInvariant());
        return NmeaSentence.Build(TalkerAlert, "ALF", "1", "1", Math.Clamp(seqId, 0, 9).ToString(System.Globalization.CultureInfo.InvariantCulture),
            NmeaSentence.FormatTime(lastChangeUtc), a.Category.ToString(), AlfPriority(a.Priority), AlfState(a), AlertManufacturerCode,
            AlertIdentifier(a.Number), "1", Math.Clamp(revision, 1, 99).ToString(System.Globalization.CultureInfo.InvariantCulture),
            a.Escalated ? "1" : "0", text);
    }

    /// <summary>
    /// ALC(IEC 61162-1 ed.5 週期警報清單):句數 01–99、句號、序號 00–99、本句項目數,之後每項 4 欄(製造商代碼、識別碼、實例、修訂計數)。
    /// 每句最多 <see cref="AlcEntriesPerSentence"/> 項以維持 ≤ 82 字;無警報時仍送一句 0 項(接收端據此知道清單為空)。
    /// </summary>
    public static IReadOnlyList<string> Alc(IReadOnlyList<(int Number, int Revision)> entries, int seqId)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var total = Math.Max(1, (entries.Count + AlcEntriesPerSentence - 1) / AlcEntriesPerSentence);
        var list = new List<string>(total);
        for (var i = 0; i < total; i++)
        {
            var chunk = entries.Skip(i * AlcEntriesPerSentence).Take(AlcEntriesPerSentence).ToArray();
            var fields = new List<string>(4 + chunk.Length * 4)
            {
                Math.Clamp(total, 1, 99).ToString("00", inv), (i + 1).ToString("00", inv), Math.Clamp(seqId, 0, 99).ToString("00", inv),
                chunk.Length.ToString(inv),
            };
            foreach (var (number, revision) in chunk)
            {
                fields.Add(AlertManufacturerCode);
                fields.Add(AlertIdentifier(number));
                fields.Add("1");
                fields.Add(Math.Clamp(revision, 1, 99).ToString(inv));
            }
            list.Add(NmeaSentence.Build(TalkerAlert, "ALC", fields.ToArray()));
        }
        return list;
    }

    /// <summary>欄位用 ASCII 文字(去掉 IEC 61162-1 保留字元與非可列印字元)。</summary>
    private static string AsciiField(string text)
        => new(text.Where(c => c >= 0x20 && c < 0x7F && c != ',' && c != '*' && c != '$' && c != '!' && c != '\\' && c != '^' && c != '~').ToArray());

    /// <summary>
    /// 自船 AIS 報告(VDO 用):MMSI 由呼叫端決定(情境 ship.mmsi 或 particulars.mmsi),船名/呼號/IMO 取 particulars,船型 <see cref="OwnShipAisType"/>,
    /// 航行狀態 0(機動航行中),位置/COG/SOG/航向/ROT 取指示值(無效時由 <see cref="AisEncoder.PositionReportBits"/> 以 positionAvailable/headingAvailable 標示),
    /// 天線視為在船舯(艏/艉 = LOA/2、左/右 = B/2),吃水取 state.draft 的最大者(無則 particulars 該裝載的平均吃水),目的港空白。
    /// </summary>
    public static AisReport OwnShipAis(OwnShipState s, SimosaBRM.SimCore.Ship.ShipParticulars ship, int mmsi)
    {
        var loa = ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : ship.Hull.LengthBetweenPerpendiculars_m;
        var beam = Math.Max(0.0, ship.Hull.Breadth_m);
        double draught;
        if (s.Draft is { } d) draught = Math.Max(d.Fore, d.Aft);
        else
        {
            // particulars 的裝載鍵為小寫 full/ballast;JSON 反序列化後的字典不保證忽略大小寫,逐鍵比對
            var key = s.Loading.ToString();
            draught = ship.LoadingConditions.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value?.DraftMean_m ?? 0.0;
        }
        int? imo = int.TryParse(ship.Imo, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null;
        return new AisReport
        {
            Mmsi = mmsi, Name = ship.Name, CallSign = ship.CallSign, Imo = imo, ShipType = OwnShipAisType, NavStatus = 0,
            Lat = s.Pos.Lat, Lon = s.Pos.Lon, Cog = s.Cog, Sog = Math.Max(0.0, s.Sog), Heading = s.Heading, Rot = s.Rot,
            DimToBow = loa / 2.0, DimToStern = loa / 2.0, DimToPort = beam / 2.0, DimToStarboard = beam / 2.0,
            Draught = Math.Max(0.0, draught), Destination = null,
        };
    }

    /// <summary>自船 AIS 類型 1 位置報告 !AIVDO(GNSS 失效 → 位置/COG/SOG 無資料值;電羅經失效 → 航向 511)。</summary>
    public static IReadOnlyList<string> OwnPositionReport(OwnShipState s, SimosaBRM.SimCore.Ship.ShipParticulars ship, int mmsi, DateTime utc)
        => AisEncoder.OwnPositionReport(OwnShipAis(s, ship, mmsi), utc, PositionValid(s), HeadingValid(s));

    /// <summary>自船 AIS 類型 5 靜態與航程資料 !AIVDO(兩句)。</summary>
    public static IReadOnlyList<string> OwnStaticAndVoyage(OwnShipState s, SimosaBRM.SimCore.Ship.ShipParticulars ship, int mmsi, int seqId)
        => AisEncoder.OwnStaticAndVoyage(OwnShipAis(s, ship, mmsi), seqId);

    /// <summary>所有目標的 TTM(編號依 targets[] 順序自 01 起)。</summary>
    public static IReadOnlyList<string> TtmSentences(OwnShipState s, DateTime utc)
    {
        if (s.Targets is not { Count: > 0 } targets) return Array.Empty<string>();
        var list = new List<string>(targets.Count);
        for (var i = 0; i < targets.Count; i++) list.Add(Ttm(targets[i], i + 1, utc));
        return list;
    }
}
