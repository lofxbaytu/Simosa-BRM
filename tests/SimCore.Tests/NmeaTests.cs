using System.Text.RegularExpressions;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class NmeaTests
{
    private static OwnShipState Sample() => new()
    {
        T = 12.34, Tick = 617, ShipId = "FSB1",
        Pos = new GeoPosition { Lat = 23.8, Lon = 120.05, X = 0, Y = 0 },
        Heading = 274.07, Cog = 270.5, Sog = 7.9, Stw = 7.8, Rot = -3.2,
        U = 4.0, V = -0.1, R = -0.00093, Drift = 1.4,
        Rudder = -12.5, RudderOrder = -15, Rpm = 100, RpmOrder = 100, Telegraph = TelegraphOrder.HAH,
        Thruster = new ThrusterState { Order = 0, Actual = 0 },
        DepthBelowKeel = 24.5, WaterDepth = 30, Squat = 0.1,
        Wind = new WindState { TrueSpeed = 15, TrueDir = 45, RelSpeed = 20.3, RelDir = 112.4 },
        Current = new CurrentState { Set = 200, Drift = 0.5 },
        Loading = LoadingCondition.Ballast,
    };

    [Fact]
    public void ChecksumMatchesKnownExample()
    {
        // 常見的參考範例
        Assert.Equal("47", NmeaSentence.Checksum("GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,"));
        Assert.True(NmeaSentence.Verify("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n"));
        Assert.False(NmeaSentence.Verify("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*48"));
        Assert.Equal("$HEHDT,274.1,T*2F", NmeaEncoder.Hdt(Sample()));
        Assert.True(NmeaSentence.Verify(NmeaEncoder.Hdt(Sample())));
    }

    [Fact]
    public void LatLonFormatting()
    {
        Assert.Equal(("2348.0000", "N"), NmeaSentence.FormatLatitude(23.8));
        Assert.Equal(("12003.0000", "E"), NmeaSentence.FormatLongitude(120.05));
        Assert.Equal(("3330.0000", "S"), NmeaSentence.FormatLatitude(-33.5));
        Assert.Equal(("07000.0000", "W"), NmeaSentence.FormatLongitude(-70.0));
        Assert.Equal(("4807.0380", "N"), NmeaSentence.FormatLatitude(48 + 7.038 / 60));
        // 分數進位不會出現 60.0000
        Assert.Equal(("2400.0000", "N"), NmeaSentence.FormatLatitude(23.9999999));
    }

    [Theory]
    [InlineData("GGA", @"^\$GPGGA,\d{6}\.\d{2},\d{4}\.\d{4},[NS],\d{5}\.\d{4},[EW],1,\d{2},\d+\.\d,-?\d+\.\d,M,-?\d+\.\d,M,,\*[0-9A-F]{2}$")]
    [InlineData("RMC", @"^\$GPRMC,\d{6}\.\d{2},A,\d{4}\.\d{4},[NS],\d{5}\.\d{4},[EW],\d+\.\d,\d+\.\d,\d{6},,,A\*[0-9A-F]{2}$")]
    [InlineData("VTG", @"^\$GPVTG,\d+\.\d,T,,M,\d+\.\d,N,\d+\.\d,K,A\*[0-9A-F]{2}$")]
    [InlineData("HDT", @"^\$HEHDT,\d{1,3}\.\d,T\*[0-9A-F]{2}$")]
    [InlineData("ROT", @"^\$TIROT,-?\d+\.\d,A\*[0-9A-F]{2}$")]
    [InlineData("VBW", @"^\$VDVBW,-?\d+\.\d,-?\d+\.\d,A,-?\d+\.\d,-?\d+\.\d,A,,V,,V\*[0-9A-F]{2}$")]
    [InlineData("DPT", @"^\$SDDPT,\d+\.\d,0\.0,\*[0-9A-F]{2}$")]
    [InlineData("MWVR", @"^\$WIMWV,\d+\.\d,R,\d+\.\d,N,A\*[0-9A-F]{2}$")]
    [InlineData("MWVT", @"^\$WIMWV,\d+\.\d,T,\d+\.\d,N,A\*[0-9A-F]{2}$")]
    [InlineData("RSA", @"^\$ERRSA,-?\d+\.\d,A,,V\*[0-9A-F]{2}$")]
    public void SentencesMatchFormatAndChecksum(string type, string pattern)
    {
        var s = Sample();
        var utc = new DateTime(2026, 1, 1, 2, 0, 12, 340, DateTimeKind.Utc);
        var sentence = type switch
        {
            "GGA" => NmeaEncoder.Gga(s, utc),
            "RMC" => NmeaEncoder.Rmc(s, utc),
            "VTG" => NmeaEncoder.Vtg(s),
            "HDT" => NmeaEncoder.Hdt(s),
            "ROT" => NmeaEncoder.Rot(s),
            "VBW" => NmeaEncoder.Vbw(s),
            "DPT" => NmeaEncoder.Dpt(s),
            "MWVR" => NmeaEncoder.MwvRelative(s),
            "MWVT" => NmeaEncoder.MwvTrue(s),
            "RSA" => NmeaEncoder.Rsa(s),
            _ => throw new ArgumentException(type),
        };
        Assert.Matches(new Regex(pattern), sentence);
        Assert.True(NmeaSentence.Verify(sentence), sentence);
        Assert.True(sentence.Length <= 82, "NMEA 0183 句長上限 82 字元");
    }

    [Fact]
    public void SentenceContentsReflectState()
    {
        var s = Sample();
        var utc = new DateTime(2026, 1, 1, 2, 0, 12, 340, DateTimeKind.Utc);
        Assert.Equal("$GPGGA,020012.34,2348.0000,N,12003.0000,E,1,10,0.9,0.0,M,0.0,M,,*5F", NmeaEncoder.Gga(s, utc));
        Assert.StartsWith("$GPRMC,020012.34,A,2348.0000,N,12003.0000,E,7.9,270.5,010126,,,A*", NmeaEncoder.Rmc(s, utc));
        Assert.StartsWith("$TIROT,-3.2,A*", NmeaEncoder.Rot(s));
        Assert.StartsWith("$ERRSA,-12.5,A,,V*", NmeaEncoder.Rsa(s));
        Assert.StartsWith("$WIMWV,112.4,R,20.3,N,A*", NmeaEncoder.MwvRelative(s));
        Assert.StartsWith("$WIMWV,130.9,T,15.0,N,A*", NmeaEncoder.MwvTrue(s)); // 45 − 274.07 → 130.93
        Assert.StartsWith("$SDDPT,24.5,0.0,*", NmeaEncoder.Dpt(s));
        Assert.StartsWith("$GPVTG,270.5,T,,M,7.9,N,14.6,K,A*", NmeaEncoder.Vtg(s));
    }

    [Fact]
    public void GatewayRatesAreTenAndOneHertz()
    {
        var engine = TestData.Engine();
        var sink = new CollectingNmeaSink();
        using var gw = new NmeaGateway(engine, sink, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        engine.Run(100); // 2 秒
        var hdt = sink.Sentences.Count(x => x.StartsWith("$HEHDT"));
        var rot = sink.Sentences.Count(x => x.StartsWith("$TIROT"));
        var rsa = sink.Sentences.Count(x => x.StartsWith("$ERRSA"));
        var gga = sink.Sentences.Count(x => x.StartsWith("$GPGGA"));
        var rmc = sink.Sentences.Count(x => x.StartsWith("$GPRMC"));
        var mwv = sink.Sentences.Count(x => x.StartsWith("$WIMWV"));
        Assert.Equal(20, hdt);
        Assert.Equal(20, rot);
        Assert.Equal(20, rsa);
        Assert.Equal(2, gga);
        Assert.Equal(2, rmc);
        Assert.Equal(4, mwv);
        Assert.All(sink.Sentences, x => Assert.True(NmeaSentence.Verify(x), x));
        // 時間欄位 = 起始 UTC + 模擬時間(第 2 秒 → 02:00:02.00)
        Assert.Contains(sink.Sentences, x => x.StartsWith("$GPGGA,020002.00,"));
    }
}
