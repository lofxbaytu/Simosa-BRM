using System.Globalization;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.Sensors.Radar;

/// <summary>
/// ARPA 追蹤結果的 NMEA 0183 句型(IEC 61162-1 TTM/TLL;句型組裝與校驗和沿用 Gateway.Nmea 的 <see cref="NmeaSentence"/>)。
/// 與 NMEA 閘道的真值 $RATTM(UDP 10110)以「埠」區分:X 頻預設 UDP 10111、S 頻 10112;talker 預設 RA,可設定(例如 "AR")。
/// 狀態:T 追蹤中、Q 擷取中、L 失追;取得方式:A 自動(保護區)、M 手動。擷取中與失追時速度/航向/CPA/TCPA 留空。
/// </summary>
public static class ArpaNmea
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Ttm(string talker, ArpaTrack tr, TrackSolution s, DateTime utc)
    {
        var tracking = tr.State == TrackState.Tracking;
        return NmeaSentence.Build(talker, "TTM",
            Math.Clamp(tr.Id, 0, 99).ToString("00", Inv),
            NmeaSentence.F2(s.RangeNm), NmeaSentence.F1(s.BearingDeg), "T",
            tracking ? NmeaSentence.F1(s.SpeedKn) : "", tracking ? NmeaSentence.F1(s.CourseDeg) : "", "T",
            tracking ? NmeaSentence.F2(s.CpaNm) : "", tracking ? NmeaSentence.F1(s.TcpaMin) : "", "N",
            "", StatusLetter(tr.State), "", NmeaSentence.FormatTime(utc), tr.Mode == AcquisitionMode.Auto ? "A" : "M");
    }

    public static string Tll(string talker, ArpaTrack tr, double lat, double lon, DateTime utc)
    {
        var (la, ns) = NmeaSentence.FormatLatitude(lat);
        var (lo, ew) = NmeaSentence.FormatLongitude(lon);
        return NmeaSentence.Build(talker, "TLL", Math.Clamp(tr.Id, 0, 99).ToString("00", Inv), la, ns, lo, ew, "",
            NmeaSentence.FormatTime(utc), StatusLetter(tr.State), "");
    }

    public static string StatusLetter(TrackState s) => s switch
    {
        TrackState.Tracking => "T",
        TrackState.Acquiring => "Q",
        _ => "L",
    };

    /// <summary>一台雷達目前所有追蹤目標的 TTM + TLL(依目標編號建立順序)。</summary>
    public static IReadOnlyList<string> Sentences(RadarUnit unit, string talker, DateTime utc)
    {
        var list = new List<string>();
        var t = unit.LastScene?.T ?? 0.0;
        var own = unit.Arpa.Own;
        var ltp = new LocalTangentPlane(own.Lat, own.Lon);
        foreach (var tr in unit.Arpa.Tracks)
        {
            if (!tr.HasEstimate) continue;
            var s = unit.Arpa.Solve(tr, t, null);
            list.Add(Ttm(talker, tr, s, utc));
            var (lat, lon) = ltp.ToGeodetic(s.X - own.X, s.Y - own.Y);
            list.Add(Tll(talker, tr, lat, lon, utc));
        }
        return list;
    }
}
