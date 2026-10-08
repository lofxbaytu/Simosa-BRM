using System.Globalization;
using System.Text;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// AIS 訊息的位元組裝與 NMEA 封裝(ITU-R M.1371 類型 1 位置報告、類型 5 靜態與航程資料;IEC 61162-1 !AIVDM)。
/// 位元由高位到低位依序寫入;6 位元 ASCII 裝甲:值 0–39 → '0'+值、40–63 → '0'+值+8;最後一句的填充位元數寫在 fill 欄位。
/// </summary>
public static class AisEncoder
{
    /// <summary>AIS 6 位元 ASCII 字元表(索引 = 6 位元值)</summary>
    public const string SixBitTable = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_ !\"#$%&'()*+,-./0123456789:;<=>?";
    /// <summary>每句的最大酬載字元數(!AIVDM,2,1,1,A, 15 字 + 60 + ,0*hh 5 字 = 80 ≤ 82)</summary>
    public const int MaxPayloadCharsPerSentence = 60;

    /// <summary>位元寫入器(MSB first)。</summary>
    public sealed class BitWriter
    {
        private readonly List<bool> _bits = new(448);
        public int Length => _bits.Count;

        /// <summary>無號整數,寫入 <paramref name="bits"/> 位元(超出範圍時截到可表示的最大值)。</summary>
        public BitWriter Unsigned(ulong value, int bits)
        {
            var max = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
            if (value > max) value = max;
            for (var i = bits - 1; i >= 0; i--) _bits.Add(((value >> i) & 1UL) == 1UL);
            return this;
        }

        /// <summary>二補數有號整數。</summary>
        public BitWriter Signed(long value, int bits)
        {
            var min = -(1L << (bits - 1));
            var max = (1L << (bits - 1)) - 1;
            if (value < min) value = min;
            if (value > max) value = max;
            var u = value < 0 ? (ulong)(value + (1L << bits)) : (ulong)value;
            return Unsigned(u, bits);
        }

        /// <summary>6 位元文字,固定 <paramref name="chars"/> 字,不足以 '@' 補齊,超長截斷;小寫轉大寫,表外字元以 '?' 代替。</summary>
        public BitWriter Text(string? s, int chars)
        {
            s = (s ?? "").ToUpperInvariant();
            for (var i = 0; i < chars; i++)
            {
                var c = i < s.Length ? s[i] : '@';
                var idx = SixBitTable.IndexOf(c);
                if (idx < 0) idx = SixBitTable.IndexOf('?');
                Unsigned((ulong)idx, 6);
            }
            return this;
        }

        public bool this[int i] => _bits[i];

        /// <summary>裝甲成酬載字串,回傳 (酬載, 填充位元數)。</summary>
        public (string Payload, int FillBits) Armor()
        {
            var fill = (6 - _bits.Count % 6) % 6;
            var sb = new StringBuilder((_bits.Count + fill) / 6);
            for (var i = 0; i < _bits.Count; i += 6)
            {
                var v = 0;
                for (var j = 0; j < 6; j++)
                {
                    v <<= 1;
                    var idx = i + j;
                    if (idx < _bits.Count && _bits[idx]) v |= 1;
                }
                sb.Append(ArmorChar(v));
            }
            return (sb.ToString(), fill);
        }
    }

    public static char ArmorChar(int v) => (char)(v < 40 ? v + 48 : v + 56);

    /// <summary>裝甲字元 → 6 位元值(解碼用)。</summary>
    public static int DearmorChar(char c)
    {
        var v = c - 48;
        if (v > 40) v -= 8;
        if (v < 0 || v > 63) throw new FormatException($"非 AIS 裝甲字元:{c}");
        return v;
    }

    /// <summary>ROT 感測值(度/分)→ AIS ROT 欄位:±4.733·√|ROT|,±127 表示 &gt; 708 度/分,0 不轉,−128 無資料。</summary>
    public static int EncodeRot(double rotDegPerMin)
    {
        if (double.IsNaN(rotDegPerMin)) return -128;
        var a = Math.Abs(rotDegPerMin);
        if (a < 1e-6) return 0;
        if (a > 708.0) return rotDegPerMin > 0 ? 127 : -127;
        var v = (int)Math.Round(4.733 * Math.Sqrt(a));
        if (v > 126) v = 126;
        return rotDegPerMin > 0 ? v : -v;
    }

    /// <summary>類型 1 位置報告的 168 位元。</summary>
    public static BitWriter PositionReportBits(AisReport a, int utcSecond, int repeat = 0)
    {
        var w = new BitWriter();
        w.Unsigned(1, 6);                                  // 訊息類型
        w.Unsigned((ulong)repeat, 2);
        w.Unsigned((ulong)a.Mmsi, 30);
        w.Unsigned((ulong)Math.Clamp(a.NavStatus, 0, 15), 4);
        w.Signed(EncodeRot(a.Rot), 8);
        w.Unsigned((ulong)Math.Clamp((long)Math.Round(a.Sog * 10.0), 0, 1022), 10);
        w.Unsigned(1, 1);                                  // 定位精度:高(DGPS 等級,模擬器)
        w.Signed((long)Math.Round(a.Lon * 600000.0), 28);
        w.Signed((long)Math.Round(a.Lat * 600000.0), 27);
        w.Unsigned((ulong)(Math.Clamp((long)Math.Round(a.Cog * 10.0), 0, 3600) % 3600), 12);
        w.Unsigned((ulong)(Math.Clamp((long)Math.Round(a.Heading), 0, 360) % 360), 9);
        w.Unsigned((ulong)Math.Clamp(utcSecond, 0, 63), 6);
        w.Unsigned(0, 2);                                  // 操船指示:無
        w.Unsigned(0, 3);                                  // 備用
        w.Unsigned(0, 1);                                  // RAIM
        w.Unsigned(0, 19);                                 // 無線電狀態(模擬器不模擬 SOTDMA)
        return w;
    }

    /// <summary>類型 5 靜態與航程資料的 424 位元(ETA 未知 → 月 0 日 0 時 24 分 60)。</summary>
    public static BitWriter StaticAndVoyageBits(AisReport a, int repeat = 0)
    {
        var w = new BitWriter();
        w.Unsigned(5, 6);
        w.Unsigned((ulong)repeat, 2);
        w.Unsigned((ulong)a.Mmsi, 30);
        w.Unsigned(1, 2);                                  // AIS 版本:ITU-R M.1371-1 之後
        w.Unsigned((ulong)Math.Max(0, a.Imo ?? 0), 30);
        w.Text(a.CallSign, 7);
        w.Text(a.Name, 20);
        w.Unsigned((ulong)Math.Clamp(a.ShipType, 0, 255), 8);
        w.Unsigned((ulong)Math.Clamp((long)Math.Round(a.DimToBow), 0, 511), 9);
        w.Unsigned((ulong)Math.Clamp((long)Math.Round(a.DimToStern), 0, 511), 9);
        w.Unsigned((ulong)Math.Clamp((long)Math.Round(a.DimToPort), 0, 63), 6);
        w.Unsigned((ulong)Math.Clamp((long)Math.Round(a.DimToStarboard), 0, 63), 6);
        w.Unsigned(1, 4);                                  // EPFD:GPS
        w.Unsigned(0, 4);                                  // ETA 月(0 = 無)
        w.Unsigned(0, 5);                                  // 日
        w.Unsigned(24, 5);                                 // 時(24 = 無)
        w.Unsigned(60, 6);                                 // 分(60 = 無)
        w.Unsigned((ulong)Math.Clamp((long)Math.Round(a.Draught * 10.0), 0, 255), 8);
        w.Text(a.Destination, 20);
        w.Unsigned(0, 1);                                  // DTE:資料終端就緒
        w.Unsigned(0, 1);                                  // 備用
        return w;
    }

    /// <summary>
    /// 封裝成 !AIVDM 句(多句時 sequential id 為 <paramref name="seqId"/> 1–9、通道 A/B);
    /// 每句最多 60 個酬載字元,填充位元只在最後一句。
    /// </summary>
    public static IReadOnlyList<string> Vdm(BitWriter bits, char channel = 'A', int seqId = 1)
    {
        var (payload, fill) = bits.Armor();
        var count = Math.Max(1, (payload.Length + MaxPayloadCharsPerSentence - 1) / MaxPayloadCharsPerSentence);
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = payload.Substring(i * MaxPayloadCharsPerSentence, Math.Min(MaxPayloadCharsPerSentence, payload.Length - i * MaxPayloadCharsPerSentence));
            var last = i == count - 1;
            list.Add(NmeaSentence.Build('!', "AI", "VDM",
                count.ToString(CultureInfo.InvariantCulture),
                (i + 1).ToString(CultureInfo.InvariantCulture),
                count > 1 ? seqId.ToString(CultureInfo.InvariantCulture) : "",
                channel.ToString(),
                chunk,
                (last ? fill : 0).ToString(CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>類型 1 位置報告 !AIVDM(單句)。</summary>
    public static IReadOnlyList<string> PositionReport(AisReport a, DateTime utc, char channel = 'A')
        => Vdm(PositionReportBits(a, utc.Second), channel);

    /// <summary>類型 5 靜態與航程資料 !AIVDM(兩句)。</summary>
    public static IReadOnlyList<string> StaticAndVoyage(AisReport a, int seqId, char channel = 'A')
        => Vdm(StaticAndVoyageBits(a), channel, seqId);
}
