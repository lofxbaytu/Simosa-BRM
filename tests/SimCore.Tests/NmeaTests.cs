using System.Text.RegularExpressions;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
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

    /// <summary>IEC 61162-1 的欄位數(不含位址欄;與 Python nmea_check.SPECS 相同的表):GGA 14、RMC 12(NMEA 2.3)、VTG 9、VBW 10、DPT 3、MWV 5、HDT 2、ROT 2、RSA 4、TTM 15、ALR 5、VDM 6。</summary>
    private static readonly IReadOnlyDictionary<string, int> Iec61162FieldCounts = new Dictionary<string, int>
    {
        ["GGA"] = 14, ["RMC"] = 12, ["VTG"] = 9, ["VBW"] = 10, ["DPT"] = 3, ["MWV"] = 5, ["HDT"] = 2, ["ROT"] = 2, ["RSA"] = 4, ["TTM"] = 15, ["ALR"] = 5, ["VDM"] = 6,
    };

    private static (string Talker, string Formatter, string[] Fields) Split(string sentence)
    {
        var body = sentence[1..sentence.LastIndexOf('*')];
        var parts = body.Split(',');
        return (parts[0][..2], parts[0][2..], parts[1..]);
    }

    [Fact]
    public void AllGatewaySentencesHaveIecFieldCountsAndTalkers()
    {
        // 正常 + GNSS/電羅經/測深儀/計程儀失效(欄位空白但欄位數不變)+ 目標 TTM/AIS + 警報 ALR
        var utc = new DateTime(2026, 1, 1, 2, 0, 12, 340, DateTimeKind.Utc);
        var all = new List<string>();
        var nominal = TestData.MinimalEngine();
        nominal.Run(60);
        var e = TestData.Engine(); // E01:T1(AIS)、T2
        e.Run(60);
        foreach (var f in new[] { "gpsFailure", "gyroFailure", "echoSounderFailure", "logFailure" }) e.Enqueue(SimCommand.InjectFault(f));
        e.Run(10);
        foreach (var st in new[] { nominal.BuildState(), e.BuildState() })
        {
            all.AddRange(NmeaEncoder.FastSentences(st));
            all.AddRange(NmeaEncoder.SlowSentences(st, utc));
            all.AddRange(NmeaEncoder.AlrSentences(st, utc));
            all.AddRange(NmeaEncoder.TtmSentences(st, utc));
            foreach (var t in st.Targets ?? Array.Empty<TargetState>())
                if (t.Ais is { } ais)
                {
                    all.AddRange(AisEncoder.PositionReport(ais, utc));
                    all.AddRange(AisEncoder.StaticAndVoyage(ais, 1));
                }
        }
        Assert.Contains(all, x => x.StartsWith("$IIALR"));
        Assert.Contains(all, x => x.StartsWith("!AIVDM,2,"));
        Assert.Contains(all, x => x.StartsWith("$GPGGA") && x.Contains(",0,00,"));
        var expectedTalker = new Dictionary<string, string> { ["GGA"] = "GP", ["RMC"] = "GP", ["VTG"] = "GP", ["HDT"] = "HE", ["ROT"] = "TI", ["VBW"] = "VD", ["DPT"] = "SD", ["MWV"] = "WI", ["RSA"] = "ER", ["TTM"] = "RA", ["ALR"] = "II", ["VDM"] = "AI" };
        foreach (var s in all)
        {
            Assert.True(NmeaSentence.Verify(s), s);
            Assert.True(s.Length <= 82, s);
            var (talker, formatter, fields) = Split(s);
            Assert.True(Iec61162FieldCounts.TryGetValue(formatter, out var n), $"未列入欄位數表的句型 {formatter}:{s}");
            Assert.True(fields.Length == n, $"{formatter} 欄位數 {fields.Length} ≠ IEC 61162-1 的 {n}:{s}");
            Assert.Equal(expectedTalker[formatter], talker);
            Assert.All(fields, f => Assert.DoesNotContain(f, c => c is '$' or '!' or '*' or ',' or '\\' or '^' or '~' || c < 0x20 || c > 0x7E));
        }
    }

    [Fact]
    public void GatewayRatesForWholeSentenceSetOverTenSeconds()
    {
        // 10 s:HDT/ROT/RSA 各 100、GGA/RMC/VTG/VBW/DPT/MWV(R)/MWV(T) 各 10;電羅經故障後 HDT 停送、ALR 每警報 1 Hz(nmea_check 的 ±20% 以此為基準)
        var engine = TestData.MinimalEngine();
        var sink = new CollectingNmeaSink();
        using var gw = new NmeaGateway(engine, sink, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        engine.Run(500);
        int Count(string prefix) => sink.Sentences.Count(x => x.StartsWith(prefix));
        Assert.Equal(100, Count("$HEHDT"));
        Assert.Equal(100, Count("$TIROT"));
        Assert.Equal(100, Count("$ERRSA"));
        foreach (var p in new[] { "$GPGGA", "$GPRMC", "$GPVTG", "$VDVBW", "$SDDPT" }) Assert.Equal(10, Count(p));
        Assert.Equal(10, sink.Sentences.Count(x => x.StartsWith("$WIMWV") && x.Contains(",R,")));
        Assert.Equal(10, sink.Sentences.Count(x => x.StartsWith("$WIMWV") && x.Contains(",T,")));
        Assert.Equal(0, Count("$IIALR"));
        Assert.Equal(370, sink.Sentences.Count); // 3 × 100 + 7 × 10
        // 第 10 秒的批次:GGA 時刻 02:00:10.00;同批 RMC 時刻相同(nmea_check 的時間一致性檢查)
        var gga = sink.Sentences.Last(x => x.StartsWith("$GPGGA"));
        var rmc = sink.Sentences.Last(x => x.StartsWith("$GPRMC"));
        Assert.Equal("020010.00", Split(gga).Fields[0]);
        Assert.Equal("020010.00", Split(rmc).Fields[0]);

        sink.Sentences.Clear();
        engine.Enqueue(SimCommand.InjectFault("gyroFailure"));
        engine.Run(500);
        Assert.Equal(0, Count("$HEHDT"));
        Assert.Equal(100, Count("$TIROT"));
        Assert.Equal(10, Count("$IIALR"));
        Assert.All(sink.Sentences.Where(x => x.StartsWith("$IIALR")), x => Assert.Equal("001", Split(x).Fields[1]));
        Assert.All(sink.Sentences, x => Assert.True(NmeaSentence.Verify(x), x));
    }

    [Fact]
    public void GatewayFollowsEngineStartUtcAfterScenarioLoad()
    {
        // 主控程序不給固定起點:NMEA 時刻 = 引擎 StartUtc + t;loadScenario 後改為新情境的 startTimeUtc(與 state.utc 同源)
        var engine = TestData.Engine(); // E01:2026-01-01T02:00:00Z
        var sink = new CollectingNmeaSink();
        using var gw = new NmeaGateway(engine, sink);
        Assert.Equal(new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc), gw.StartUtc);
        engine.Run(50);
        Assert.Contains(sink.Sentences, x => x.StartsWith("$GPGGA,020001.00,"));
        sink.Sentences.Clear();
        var e05 = Path.Combine(TestData.Root, "data", "scenarios", "E05_pilot_traffic.yaml");
        Assert.True(engine.TryApplyScenarioLoad(ScenarioLoader.TryLoad(e05), TestData.Ship, "file"));
        engine.Run(50);
        Assert.Equal(new DateTime(2026, 5, 5, 22, 30, 0, DateTimeKind.Utc), gw.StartUtc);
        var gga = sink.Sentences.Last(x => x.StartsWith("$GPGGA"));
        Assert.StartsWith("$GPGGA,223001.00,", gga);
        Assert.Equal(engine.UtcAt(engine.Time)!.Substring(11, 8).Replace(":", "") + ".00", Split(gga).Fields[0]);
        // 固定起點時不跟隨
        using var fixedGw = new NmeaGateway(engine, new CollectingNmeaSink(), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2030, fixedGw.StartUtc.Year);
    }

    [Fact]
    public void FileSinkWritesSentencesWithCrLfAndCompositeFansOut()
    {
        var dir = Path.Combine(TestData.Root, "build", "test-tmp", "nmea");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"sink-{Guid.NewGuid():N}.nmea");
        try
        {
            var collecting = new CollectingNmeaSink();
            var file = new FileNmeaSink(path);
            using (var composite = new CompositeNmeaSink(new INmeaSink[] { file, collecting }))
            {
                var engine = TestData.MinimalEngine();
                using var gw = new NmeaGateway(engine, composite, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
                engine.Run(100);
            }
            Assert.Equal(collecting.Sentences.Count, file.SentencesWritten);
            var bytes = File.ReadAllBytes(path);
            Assert.All(bytes, b => Assert.InRange(b, (byte)0x0A, (byte)0x7E));
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.EndsWith("\r\n", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
            var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(collecting.Sentences, lines);
            Assert.All(lines, l => Assert.True(NmeaSentence.Verify(l), l));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileSinkPathMustBeInsideProject()
    {
        var root = TestData.Root;
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "build", "nmea", "E05.nmea")), FileNmeaSink.Resolve(root, "build/nmea/E05.nmea"));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "build", "x.nmea")), FileNmeaSink.Resolve(root, Path.Combine(root, "build", "x.nmea")));
        Assert.Throws<ArgumentException>(() => FileNmeaSink.Resolve(root, Path.Combine(Path.GetTempPath(), "outside.nmea")));
        Assert.Throws<ArgumentException>(() => FileNmeaSink.Resolve(root, "../outside.nmea"));
        Assert.Throws<ArgumentException>(() => FileNmeaSink.Resolve(root, " "));
    }

    [Fact]
    public void HostOptionsParseNmeaTargetBindAndFile()
    {
        var o = SimosaBRM.SimCore.Host.HostOptions.Parse(new[] { "--nmea-target", "192.168.77.21:10110", "--nmea-target", "192.168.77.21", "--nmea-bind", "192.168.77.11", "--nmea-file", "build/nmea/E05.nmea", "--no-nmea" });
        Assert.Equal(2, o.NmeaTargets.Count);
        Assert.Equal("192.168.77.21:10110", o.NmeaTargets[0].ToString());
        Assert.Equal(10110, o.NmeaTargets[1].Port); // 未給埠 → 與 --nmea-port 相同
        Assert.Equal(System.Net.IPAddress.Parse("192.168.77.11"), o.NmeaBind);
        Assert.Equal("build/nmea/E05.nmea", o.NmeaFile);
        Assert.False(o.EnableNmea);
        Assert.Null(SimosaBRM.SimCore.Host.HostOptions.Parse(Array.Empty<string>()).NmeaFile);
        Assert.Empty(SimosaBRM.SimCore.Host.HostOptions.Parse(Array.Empty<string>()).NmeaTargets);
        Assert.Throws<ArgumentException>(() => SimosaBRM.SimCore.Host.HostOptions.Parse(new[] { "--nmea-target", "opencpn-laptop:10110" }));
        Assert.Throws<ArgumentException>(() => SimosaBRM.SimCore.Host.HostOptions.Parse(new[] { "--nmea-bind", "eth0" }));
        Assert.Contains("--nmea-file", SimosaBRM.SimCore.Host.HostOptions.Usage);
    }
}
