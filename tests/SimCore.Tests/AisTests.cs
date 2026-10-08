using System.Text.RegularExpressions;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// AIS !AIVDM(ITU-R M.1371 類型 1/5)編碼:以自寫的簡易解碼器驗證已知範例與編碼往返、6 位元填充、多句分段;
/// $RATTM 格式;NMEA 閘道的目標句型速率。
/// </summary>
public class AisTests
{
    /// <summary>簡易解碼器:去裝甲 → 位元串 → 欄位(測試專用,與編碼器獨立撰寫)。</summary>
    private sealed class AisDecoder
    {
        private readonly string _bits;
        public int Length => _bits.Length;

        public AisDecoder(IEnumerable<string> sentences)
        {
            var sb = new System.Text.StringBuilder();
            var fill = 0;
            foreach (var s in sentences)
            {
                Assert.True(NmeaSentence.Verify(s), s);
                var body = s[1..s.LastIndexOf('*')];
                var f = body.Split(',');
                Assert.Equal("AIVDM", f[0]);
                foreach (var c in f[5])
                {
                    var v = c - 48;
                    if (v > 40) v -= 8;
                    sb.Append(Convert.ToString(v, 2).PadLeft(6, '0'));
                }
                fill = int.Parse(f[6]);
            }
            _bits = fill > 0 ? sb.ToString()[..^fill] : sb.ToString();
        }

        public long U(int start, int len) => Convert.ToInt64(_bits.Substring(start, len), 2);

        public long S(int start, int len)
        {
            var v = U(start, len);
            return v >= (1L << (len - 1)) ? v - (1L << len) : v;
        }

        public string Text(int start, int chars)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < chars; i++) sb.Append(AisEncoder.SixBitTable[(int)U(start + 6 * i, 6)]);
            return sb.ToString().TrimEnd('@', ' ');
        }
    }

    private static AisReport Sample() => new()
    {
        Mmsi = 416123456, Name = "HAI XING 8", CallSign = "BXHX8", Imo = 9692428, ShipType = 70, NavStatus = 0,
        Lat = 23.8833, Lon = 120.0517, Cog = 183.4, Sog = 9.2, Heading = 181.0, Rot = -12.0,
        DimToBow = 60, DimToStern = 60, DimToPort = 9.5, DimToStarboard = 9.5, Draught = 6.5, Destination = "KAOHSIUNG",
    };

    [Fact]
    public void DecodesKnownPositionReportExample()
    {
        // 公開文件常見範例(gpsd AIVDM 說明):MMSI 477553000、繫泊、SOG 0、西雅圖附近、COG 51°、HDG 181°
        const string known = "!AIVDM,1,1,,B,177KQJ5000G?tO`K>RA1wUbN0TKH,0*5C";
        Assert.True(NmeaSentence.Verify(known));
        var d = new AisDecoder(new[] { known });
        Assert.Equal(168, d.Length);
        Assert.Equal(1, d.U(0, 6));
        Assert.Equal(477553000, d.U(8, 30));
        Assert.Equal(5, d.U(38, 4));
        Assert.Equal(0, d.S(42, 8));
        Assert.Equal(0.0, d.U(50, 10) / 10.0);
        Assert.Equal(-122.3458, d.S(61, 28) / 600000.0, 4);
        Assert.Equal(47.5828, d.S(89, 27) / 600000.0, 4);
        Assert.Equal(51.0, d.U(116, 12) / 10.0);
        Assert.Equal(181, d.U(128, 9));
        Assert.Equal(15, d.U(137, 6));
        // 裝甲字元表往返
        for (var v = 0; v < 64; v++) Assert.Equal(v, AisEncoder.DearmorChar(AisEncoder.ArmorChar(v)));
    }

    [Fact]
    public void PositionReportRoundTrips()
    {
        var a = Sample();
        var utc = new DateTime(2026, 1, 1, 2, 0, 37, DateTimeKind.Utc);
        var sentences = AisEncoder.PositionReport(a, utc, 'B');
        var s = Assert.Single(sentences);
        Assert.Matches(new Regex(@"^!AIVDM,1,1,,B,[0-9:;<=>?@A-W`a-w]{28},0\*[0-9A-F]{2}$"), s);
        Assert.True(s.Length <= 82);
        var d = new AisDecoder(sentences);
        Assert.Equal(168, d.Length);
        Assert.Equal(1, d.U(0, 6));
        Assert.Equal(0, d.U(6, 2));
        Assert.Equal(a.Mmsi, d.U(8, 30));
        Assert.Equal(0, d.U(38, 4));
        Assert.Equal(-16, d.S(42, 8));                       // −4.733·√12 = −16.4 → −16
        Assert.Equal(9.2, d.U(50, 10) / 10.0, 9);
        Assert.Equal(1, d.U(60, 1));
        Assert.Equal(a.Lon, d.S(61, 28) / 600000.0, 5);
        Assert.Equal(a.Lat, d.S(89, 27) / 600000.0, 5);
        Assert.Equal(183.4, d.U(116, 12) / 10.0, 9);
        Assert.Equal(181, d.U(128, 9));
        Assert.Equal(37, d.U(137, 6));
        Assert.Equal(0, d.U(143, 2));
        Assert.Equal(0, d.U(147, 19));

        // 南緯西經(負值二補數)
        var sw = a with { Lat = -33.8688, Lon = -70.6693 };
        var d2 = new AisDecoder(AisEncoder.PositionReport(sw, utc));
        Assert.Equal(-70.6693, d2.S(61, 28) / 600000.0, 5);
        Assert.Equal(-33.8688, d2.S(89, 27) / 600000.0, 5);
    }

    [Fact]
    public void StaticAndVoyageSplitsIntoTwoSentencesWithFillBits()
    {
        var a = Sample();
        var sentences = AisEncoder.StaticAndVoyage(a, seqId: 3, channel: 'A');
        Assert.Equal(2, sentences.Count);
        Assert.StartsWith("!AIVDM,2,1,3,A,", sentences[0]);
        Assert.StartsWith("!AIVDM,2,2,3,A,", sentences[1]);
        Assert.EndsWith(",0*" + sentences[0][^2..], sentences[0]);
        Assert.Contains(",2*", sentences[1]);                 // 424 位元 = 70 字 + 4 位元 → 71 字,填充 2
        Assert.All(sentences, s => Assert.True(s.Length <= 82 && NmeaSentence.Verify(s), s));
        Assert.Equal(60, sentences[0].Split(',')[5].Length);
        Assert.Equal(11, sentences[1].Split(',')[5].Length);

        var d = new AisDecoder(sentences);
        Assert.Equal(424, d.Length);
        Assert.Equal(5, d.U(0, 6));
        Assert.Equal(a.Mmsi, d.U(8, 30));
        Assert.Equal(1, d.U(38, 2));
        Assert.Equal(9692428, d.U(40, 30));
        Assert.Equal("BXHX8", d.Text(70, 7));
        Assert.Equal("HAI XING 8", d.Text(112, 20));
        Assert.Equal(70, d.U(232, 8));
        Assert.Equal(60, d.U(240, 9));
        Assert.Equal(60, d.U(249, 9));
        Assert.Equal(10, d.U(258, 6));                       // 9.5 → 四捨五入(銀行家)→ 10
        Assert.Equal(10, d.U(264, 6));
        Assert.Equal(1, d.U(270, 4));
        Assert.Equal(0, d.U(274, 4));
        Assert.Equal(0, d.U(278, 5));
        Assert.Equal(24, d.U(283, 5));
        Assert.Equal(60, d.U(288, 6));
        Assert.Equal(65, d.U(294, 8));
        Assert.Equal("KAOHSIUNG", d.Text(302, 20));
        Assert.Equal(0, d.U(422, 1));
    }

    [Fact]
    public void SixBitTextHandlesPaddingTruncationAndLowercase()
    {
        var w = new AisEncoder.BitWriter();
        w.Text("pilot 3", 7);
        var d = new AisDecoder(AisEncoder.Vdm(w));
        Assert.Equal("PILOT 3", d.Text(0, 7));
        var w2 = new AisEncoder.BitWriter();
        w2.Text("A VERY LONG SHIP NAME EXCEEDING", 20);
        Assert.Equal(120, w2.Length);
        Assert.Equal("A VERY LONG SHIP NAM", new AisDecoder(AisEncoder.Vdm(w2)).Text(0, 20));
        var w3 = new AisEncoder.BitWriter();
        w3.Text("AB", 4);                                     // 補 '@'
        Assert.Equal(0, w3.Armor().FillBits);
        Assert.Equal("AB", new AisDecoder(AisEncoder.Vdm(w3)).Text(0, 4));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(10.0, 15)]
    [InlineData(-10.0, -15)]
    [InlineData(720.0, 127)]
    [InlineData(-720.0, -127)]
    [InlineData(700.0, 125)]
    public void RotEncodingFollowsSpec(double rot, int expected) => Assert.Equal(expected, AisEncoder.EncodeRot(rot));

    [Fact]
    public void TtmFormatAndContents()
    {
        var t = new TargetState
        {
            Id = "T1", Name = "HAI XING 8, LTD", Mmsi = 416123456, ShipType = "cargo", Loa = 120, Beam = 19,
            Pos = new GeoPosition { Lat = 23.88, Lon = 120.05, X = -100, Y = 9260 },
            Heading = 180, Cog = 181.2, Sog = 9.0, Rot = 0,
            RangeNm = 4.98, BearingDeg = 359.4, CpaNm = 0.05, TcpaMin = 17.8, AisOn = true, Lights = "powerDriven", Behaviour = "colreg",
        };
        var utc = new DateTime(2026, 1, 1, 2, 0, 12, 340, DateTimeKind.Utc);
        var s = NmeaEncoder.Ttm(t, 1, utc);
        Assert.Equal("$RATTM,01,4.98,359.4,T,9.0,181.2,T,0.05,17.8,N,HAI XING 8 L,T,,020012.34,A*" + s[^2..], s);
        Assert.True(NmeaSentence.Verify(s));
        Assert.True(s.Length <= 82);
        Assert.Matches(new Regex(@"^\$RATTM,\d{2},\d+\.\d{2},\d+\.\d,T,\d+\.\d,\d+\.\d,T,\d+\.\d{2},-?\d+\.\d,N,[^,]*,T,,\d{6}\.\d{2},A\*[0-9A-F]{2}$"), s);
    }

    [Fact]
    public void GatewayEmitsTtmEverySecondAndAisAtReportIntervals()
    {
        var engine = TestData.Engine(); // E01:T1(AIS 開)與 T2(錨泊)
        Assert.Equal(2, engine.Scenario.Targets.Count);
        var sink = new CollectingNmeaSink();
        using var gw = new NmeaGateway(engine, sink, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        engine.Run(50 * 30); // 30 s
        var ttm = sink.Sentences.Where(x => x.StartsWith("$RATTM")).ToList();
        Assert.Equal(60, ttm.Count);                                // 2 目標 × 30 s
        Assert.Equal(30, ttm.Count(x => x.StartsWith("$RATTM,01,")));
        Assert.Equal(30, ttm.Count(x => x.StartsWith("$RATTM,02,")));
        var vdm = sink.Sentences.Where(x => x.StartsWith("!AIVDM")).ToList();
        // 位置報告每 10 s:目標 0 在 10、20、30 s,目標 1 在 1、11、21 s → 各 3 句
        Assert.Equal(6, vdm.Count(x => x.Contains(",1,1,,")));
        Assert.All(sink.Sentences, x => Assert.True(NmeaSentence.Verify(x), x));
        Assert.All(sink.Sentences, x => Assert.True(x.Length <= 82, x));
        var first = vdm.First(x => x.Contains(",1,1,,A,"));
        var d = new AisDecoder(new[] { first });
        Assert.Equal(416123456, d.U(8, 30));                      // T1 的 MMSI
        // 靜態資料:第 2、3 秒各送一次(2 句),之後每 6 分
        Assert.Equal(4, vdm.Count(x => x.StartsWith("!AIVDM,2,")));
        var staticT1 = vdm.Where(x => x.StartsWith("!AIVDM,2,") && x.Contains(",1,A,")).ToList();
        Assert.Equal("HAI XING 8", new AisDecoder(staticT1).Text(112, 20));
        engine.Run(50 * 340); // t = 370 s:第二次靜態報告在 362、363 s
        Assert.Equal(8, sink.Sentences.Count(x => x.StartsWith("!AIVDM,2,")));

        // AIS 關閉後只剩 TTM
        sink.Sentences.Clear();
        engine.Enqueue(SimCommand.TargetAis("T1", false));
        engine.Enqueue(SimCommand.TargetAis("T2", false));
        engine.Run(50 * 20);
        Assert.DoesNotContain(sink.Sentences, x => x.StartsWith("!AIVDM"));
        Assert.Equal(40, sink.Sentences.Count(x => x.StartsWith("$RATTM")));
    }
}
