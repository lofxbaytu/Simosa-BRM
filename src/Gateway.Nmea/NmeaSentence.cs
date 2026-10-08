using System.Globalization;
using System.Text;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>NMEA 0183 句型組裝、校驗和與欄位格式(IEC 61162-1)。</summary>
public static class NmeaSentence
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>校驗和:'$' 與 '*' 之間所有字元的 XOR,兩位大寫十六進位。</summary>
    public static string Checksum(string payload)
    {
        byte x = 0;
        foreach (var ch in payload) x ^= (byte)ch;
        return x.ToString("X2", Inv);
    }

    /// <summary>組成 "$TTSSS,f1,f2,...*hh"(不含 CR LF)。</summary>
    public static string Build(string talker, string type, params string[] fields) => Build('$', talker, type, fields);

    /// <summary>組成以 <paramref name="start"/>('$' 參數句、'!' 封裝句如 AIVDM)開頭的句型。</summary>
    public static string Build(char start, string talker, string type, params string[] fields)
    {
        var sb = new StringBuilder(96);
        sb.Append(talker).Append(type);
        foreach (var f in fields) sb.Append(',').Append(f);
        var payload = sb.ToString();
        return start + payload + "*" + Checksum(payload);
    }

    /// <summary>驗證一句的校驗和(接受 '$' 與 '!' 開頭、有無 CR LF)。</summary>
    public static bool Verify(string sentence)
    {
        var s = sentence.TrimEnd('\r', '\n');
        if (s.Length < 4 || (s[0] != '$' && s[0] != '!')) return false;
        var star = s.LastIndexOf('*');
        if (star < 0 || star + 3 != s.Length) return false;
        return string.Equals(Checksum(s[1..star]), s[(star + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>緯度 "ddmm.mmmm" 與半球。</summary>
    public static (string Value, string Hemisphere) FormatLatitude(double lat)
    {
        var (deg, min) = SplitDegMin(Math.Abs(lat));
        return (deg.ToString("00", Inv) + min.ToString("00.0000", Inv), lat < 0 ? "S" : "N");
    }

    /// <summary>經度 "dddmm.mmmm" 與半球。</summary>
    public static (string Value, string Hemisphere) FormatLongitude(double lon)
    {
        var (deg, min) = SplitDegMin(Math.Abs(lon));
        return (deg.ToString("000", Inv) + min.ToString("00.0000", Inv), lon < 0 ? "W" : "E");
    }

    private static (int Deg, double Min) SplitDegMin(double absDegrees)
    {
        // 先把總分數四捨五入到 4 位小數,避免 59.99995 → "60.0000"
        var totalMin = Math.Round(absDegrees * 60.0, 4);
        var deg = (int)Math.Floor(totalMin / 60.0);
        return (deg, totalMin - deg * 60.0);
    }

    /// <summary>UTC 時刻 "hhmmss.ss"。</summary>
    public static string FormatTime(DateTime utc) => utc.ToString("HHmmss.ff", Inv);

    /// <summary>UTC 日期 "ddmmyy"。</summary>
    public static string FormatDate(DateTime utc) => utc.ToString("ddMMyy", Inv);

    public static string F1(double v) => v.ToString("0.0", Inv);
    public static string F2(double v) => v.ToString("0.00", Inv);
}
