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
    [InlineData("THS", @"^\$HETHS,\d{1,3}\.\d,A\*[0-9A-F]{2}$")]
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
            "THS" => NmeaEncoder.Ths(s),
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
        Assert.Equal("$HETHS,274.1,A*" + NmeaSentence.Checksum("HETHS,274.1,A"), NmeaEncoder.Ths(s));
    }

    private static AlertStatus SampleAlert(SimosaBRM.SimCore.Alerts.AlertState state, string id = "gpsFailure", bool escalated = false, string? transferredTo = null) => new()
    {
        Id = id, Number = SimosaBRM.SimCore.Alerts.AlertCatalogue.Find(id)!.Number, Priority = SimosaBRM.SimCore.Alerts.AlertPriority.Alarm,
        Category = SimosaBRM.SimCore.Alerts.AlertCategory.B, Source = "gnss", State = state, Text = "GNSS 定位失效", RaisedAtS = 5.0,
        AcknowledgedAtS = state == SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged ? 7.5 : null, Escalated = escalated, Count = 1, Audible = false,
        TransferredTo = transferredTo,
    };

    [Fact]
    public void AlfFieldsFollowIec61162Ed5()
    {
        // 句數 1、句號 1、序號、改變時刻、類別 B、優先序 A、狀態 V、SIM、識別碼 002(= ALR 編號)、實例 1、修訂 1、升級 0、英文文字
        var t = new DateTime(2026, 5, 5, 22, 30, 5, DateTimeKind.Utc);
        var alf = NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged), t, 3, 1);
        Assert.Equal("$IIALF,1,1,3,223005.00,B,A,V,SIM,002,1,1,0,GNSS POSITION LOST*" + NmeaSentence.Checksum("IIALF,1,1,3,223005.00,B,A,V,SIM,002,1,1,0,GNSS POSITION LOST"), alf);
        Assert.Equal(13, Split(alf).Fields.Length);
        Assert.Equal("S", Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveSilenced), t, 0, 2)).Fields[6]);
        Assert.Equal("A", Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged), t, 0, 2)).Fields[6]);
        Assert.Equal("O", Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveAcknowledged, transferredTo: "portWing"), t, 0, 2)).Fields[6]);
        Assert.Equal("U", Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.RectifiedUnacknowledged), t, 0, 2)).Fields[6]);
        Assert.Equal("N", Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.Normal), t, 0, 2)).Fields[6]);
        var esc = Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged, escalated: true), t, 9, 99)).Fields;
        Assert.Equal(("9", "99", "1"), (esc[2], esc[10], esc[11]));
        // 序號/修訂超出範圍時夾限(0–9、1–99)
        var clamped = Split(NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged), t, 12, 150)).Fields;
        Assert.Equal(("9", "99"), (clamped[2], clamped[10]));
        Assert.Equal("W", NmeaEncoder.AlfPriority(SimosaBRM.SimCore.Alerts.AlertPriority.Warning));
        Assert.Equal("E", NmeaEncoder.AlfPriority(SimosaBRM.SimCore.Alerts.AlertPriority.EmergencyAlarm));
        Assert.Equal("C", NmeaEncoder.AlfPriority(SimosaBRM.SimCore.Alerts.AlertPriority.Caution));
        // 警報目錄每一筆的 ALF 都 ≤ 82 字且校驗和正確
        foreach (var d in SimosaBRM.SimCore.Alerts.AlertCatalogue.All)
        {
            var line = NmeaEncoder.Alf(SampleAlert(SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged, d.Id), t, 0, 1);
            Assert.True(line.Length <= 82, line);
            Assert.True(NmeaSentence.Verify(line), line);
            Assert.Equal(d.NmeaText, Split(line).Fields[12]);
        }
    }

    [Fact]
    public void AlcSplitsEntriesIntoSentencesWithinLengthLimit()
    {
        // 無警報:一句 0 項(4 欄);14 個警報(整個目錄)→ 每句 ≤ 4 項 → 4 句(01/04…04/04),每句 ≤ 82 字、欄位數 4 + 4n
        var empty = NmeaEncoder.Alc(Array.Empty<(int, int)>(), 0);
        Assert.Single(empty);
        Assert.Equal("$IIALC,01,01,00,0*" + NmeaSentence.Checksum("IIALC,01,01,00,0"), empty[0]);
        var all = SimosaBRM.SimCore.Alerts.AlertCatalogue.All.Select(d => (d.Number, 99)).ToArray();
        var alc = NmeaEncoder.Alc(all, 7);
        Assert.Equal(4, alc.Count);
        var totalEntries = 0;
        for (var i = 0; i < alc.Count; i++)
        {
            Assert.True(alc[i].Length <= 82, alc[i]);
            Assert.True(NmeaSentence.Verify(alc[i]), alc[i]);
            var f = Split(alc[i]).Fields;
            Assert.Equal(("04", (i + 1).ToString("00"), "07"), (f[0], f[1], f[2]));
            var n = int.Parse(f[3]);
            Assert.Equal(4 + 4 * n, f.Length);
            for (var k = 0; k < n; k++) Assert.Equal(("SIM", "1", "99"), (f[4 + 4 * k], f[6 + 4 * k], f[7 + 4 * k]));
            totalEntries += n;
        }
        Assert.Equal(14, totalEntries);
        Assert.Equal(new[] { "001", "002", "003", "004" }, Enumerable.Range(0, 4).Select(k => Split(alc[0]).Fields[5 + 4 * k]));
        Assert.Equal(new[] { "013", "014" }, Enumerable.Range(0, 2).Select(k => Split(alc[3]).Fields[5 + 4 * k]));
        Assert.Equal("$IIALC,01,01,05,1,SIM,011,1,2*" + NmeaSentence.Checksum("IIALC,01,01,05,1,SIM,011,1,2"), NmeaEncoder.Alc(new[] { (11, 2) }, 5)[0]);
    }

    [Fact]
    public void OwnShipVdoEncodesIndicatedPositionAndFlagsInvalidSensors()
    {
        var s = Sample();
        var utc = new DateTime(2026, 1, 1, 2, 0, 12, 340, DateTimeKind.Utc);
        var ship = TestData.Fsb1();
        var ais = NmeaEncoder.OwnShipAis(s, ship, 416100001);
        Assert.Equal((416100001, "FS BITUMEN NO.1", "BIAX", 9692428, 80, 0), (ais.Mmsi, ais.Name, ais.CallSign, ais.Imo, ais.ShipType, ais.NavStatus));
        Assert.Equal((52.75, 52.75, 9.0, 9.0), (ais.DimToBow, ais.DimToStern, ais.DimToPort, ais.DimToStarboard)); // LOA 105.5、B 18
        Assert.Equal(3.72, ais.Draught, 3); // state 無 draft → particulars 壓載平均吃水
        var vdo = NmeaEncoder.OwnPositionReport(s, ship, 416100001, utc);
        Assert.Single(vdo);
        Assert.StartsWith("!AIVDO,1,1,,A,", vdo[0]);
        Assert.True(NmeaSentence.Verify(vdo[0]), vdo[0]);
        var d = new AisTests.AisDecoder(vdo);
        Assert.Equal((1, 416100001), (d.U(0, 6), d.U(8, 30)));
        Assert.Equal(23.8, d.S(89, 27) / 600000.0, 5);
        Assert.Equal(120.05, d.S(61, 28) / 600000.0, 5);
        Assert.Equal((2705, 274, 12), (d.U(116, 12), d.U(128, 9), d.U(137, 6)));
        var stat = NmeaEncoder.OwnStaticAndVoyage(s, ship, 416100001, 9);
        Assert.Equal(2, stat.Count);
        Assert.All(stat, x => Assert.StartsWith("!AIVDO,2,", x));
        Assert.Equal("FS BITUMEN NO.1", new AisTests.AisDecoder(stat).Text(112, 20));
        Assert.Equal("BIAX", new AisTests.AisDecoder(stat).Text(70, 7));
        // GNSS 與電羅經失效:位置 91/181、COG 3600、SOG 1023、定位精度 0、航向 511(VDM 目標船不受影響)
        var bad = s with { Sensors = new SensorStatus { HeadingValid = false, PositionValid = false, DepthValid = true, StwValid = true, MagneticHeading = 0 } };
        var db = new AisTests.AisDecoder(NmeaEncoder.OwnPositionReport(bad, ship, 416100001, utc));
        Assert.Equal((91L * 600000, 181L * 600000, 3600, 1023, 0, 511), (db.S(89, 27), db.S(61, 28), db.U(116, 12), db.U(50, 10), db.U(60, 1), db.U(128, 9)));
        Assert.Equal(AisEncoder.PositionReport(ais, utc), AisEncoder.Vdm(AisEncoder.PositionReportBits(ais, utc.Second)));
    }

    [Fact]
    public void GatewaySendsOwnShipVdoWhenMmsiConfiguredAndLogsOnceOtherwise()
    {
        // 情境 ship.mmsi:第 1 秒類型 5(兩句)、第 10/20 秒類型 1
        var sc = TestData.Minimal();
        sc.Ship.Mmsi = 416100001;
        var engine = new SimosaBRM.SimCore.Engine.SimulationEngine(TestData.Fsb1(), sc);
        var sink = new CollectingNmeaSink();
        var logs = new List<string>();
        using var gw = new NmeaGateway(engine, sink, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        gw.Log += logs.Add;
        Assert.Equal(416100001, gw.OwnMmsi);
        engine.Run(1000); // 20 s
        var vdo = sink.Sentences.Where(x => x.StartsWith("!AIVDO")).ToList();
        Assert.Equal(4, vdo.Count); // 類型 5 兩句 + 類型 1 兩句
        Assert.Equal(2, vdo.Count(x => x.StartsWith("!AIVDO,2,")));
        Assert.Equal(2, vdo.Count(x => x.StartsWith("!AIVDO,1,1,,A,")));
        Assert.Empty(logs);
        Assert.All(vdo, x => Assert.True(NmeaSentence.Verify(x), x));
        var pos = new AisTests.AisDecoder(new[] { vdo.First(x => x.StartsWith("!AIVDO,1,1")) });
        Assert.Equal(416100001, pos.U(8, 30));
        Assert.Equal(10, pos.U(137, 6)); // 02:00:10 的 UTC 秒
        // 無 MMSI(particulars 亦無):不送 VDO,log 一次
        var engine2 = TestData.MinimalEngine();
        var sink2 = new CollectingNmeaSink();
        var logs2 = new List<string>();
        using var gw2 = new NmeaGateway(engine2, sink2, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        gw2.Log += logs2.Add;
        Assert.Null(gw2.OwnMmsi);
        engine2.Run(1000);
        Assert.DoesNotContain(sink2.Sentences, x => x.StartsWith("!AIVDO"));
        Assert.Single(logs2);
        Assert.Contains("ship.mmsi", logs2[0]);
    }

    [Fact]
    public void GatewayAlfAlcTrackRevisionsAndListChanges()
    {
        // ALC:第 1 秒 0 項;gyroFailure 於第 2 秒注入 → 第 3 秒清單改變(1 項、修訂 1)→ ALF 每秒、修訂 1;確認後修訂 2、狀態 A、時刻 = acknowledgedAt;
        // 第 31 秒週期 ALC;清除故障(警報解除 → normal 不在 state.alerts)→ ALC 0 項
        var engine = TestData.MinimalEngine();
        var sink = new CollectingNmeaSink();
        using var gw = new NmeaGateway(engine, sink, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        engine.Run(100); // 2 s
        Assert.Equal(new[] { "$IIALC,01,01,00,0*" + NmeaSentence.Checksum("IIALC,01,01,00,0") }, sink.Sentences.Where(x => x.StartsWith("$IIALC")));
        Assert.DoesNotContain(sink.Sentences, x => x.StartsWith("$IIALF"));
        engine.Enqueue(SimCommand.InjectFault("gyroFailure"));
        sink.Sentences.Clear();
        engine.Run(100); // 3–4 s
        var alf = sink.Sentences.Where(x => x.StartsWith("$IIALF")).Select(Split).ToList();
        Assert.Equal(2, alf.Count);
        Assert.All(alf, a => Assert.Equal(("B", "A", "V", "SIM", "001", "1", "1", "0", "GYRO FAILURE"), (a.Fields[4], a.Fields[5], a.Fields[6], a.Fields[7], a.Fields[8], a.Fields[9], a.Fields[10], a.Fields[11], a.Fields[12])));
        Assert.Equal("020002.02", alf[0].Fields[3]); // 成立時刻 raisedAt_s(第 101 步 = 2.02 s)
        Assert.Equal(new[] { "0", "1" }, alf.Select(a => a.Fields[2])); // 序號遞增
        var alc = sink.Sentences.Where(x => x.StartsWith("$IIALC")).Select(Split).ToList();
        Assert.Single(alc); // 清單改變時送一次,之後不重複
        Assert.Equal(new[] { "01", "01", "01", "1", "SIM", "001", "1", "1" }, alc[0].Fields);
        engine.Enqueue(SimCommand.AlertAcknowledge("gyroFailure"));
        sink.Sentences.Clear();
        engine.Run(100); // 5–6 s
        alf = sink.Sentences.Where(x => x.StartsWith("$IIALF")).Select(Split).ToList();
        Assert.All(alf, a => Assert.Equal(("A", "2"), (a.Fields[6], a.Fields[10])));
        var ackAt = engine.BuildState().Alerts!.Single().AcknowledgedAtS!.Value;
        Assert.Equal(NmeaSentence.FormatTime(gw.StartUtc.AddSeconds(ackAt)), alf[0].Fields[3]); // 確認時刻 = 核心的 acknowledgedAt_s
        alc = sink.Sentences.Where(x => x.StartsWith("$IIALC")).Select(Split).ToList();
        Assert.Single(alc);
        Assert.Equal(("02", "2"), (alc[0].Fields[2], alc[0].Fields[7])); // 序號 02、修訂 2
        sink.Sentences.Clear();
        engine.Run(1250); // 7–31 s:第 31 秒的週期 ALC(AlcIntervalS 30)
        alc = sink.Sentences.Where(x => x.StartsWith("$IIALC")).Select(Split).ToList();
        Assert.Single(alc);
        Assert.Equal(25, sink.Sentences.Count(x => x.StartsWith("$IIALF")));
        engine.Enqueue(SimCommand.ClearFault("gyroFailure"));
        sink.Sentences.Clear();
        engine.Run(100);
        Assert.DoesNotContain(sink.Sentences, x => x.StartsWith("$IIALF"));
        // 序號:00(第 1 秒)、01(清單改變)、02(確認)、03(第 31 秒週期)、04(清除後 0 項)
        Assert.Contains("$IIALC,01,01,04,0*" + NmeaSentence.Checksum("IIALC,01,01,04,0"), sink.Sentences);
        Assert.All(sink.Sentences, x => Assert.True(NmeaSentence.Verify(x), x));
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
        Assert.Equal(20, sink.Sentences.Count(x => x.StartsWith("$HETHS")));
        Assert.Equal(20, rot);
        Assert.Equal(20, rsa);
        Assert.Equal(2, gga);
        Assert.Equal(2, rmc);
        Assert.Equal(4, mwv);
        Assert.All(sink.Sentences, x => Assert.True(NmeaSentence.Verify(x), x));
        // 時間欄位 = 起始 UTC + 模擬時間(第 2 秒 → 02:00:02.00)
        Assert.Contains(sink.Sentences, x => x.StartsWith("$GPGGA,020002.00,"));
    }

    /// <summary>
    /// IEC 61162-1 的欄位數(不含位址欄;與 Python nmea_check.SPECS 相同的表):GGA 14、RMC 12(NMEA 2.3)、VTG 9、VBW 10、DPT 3、MWV 5、HDT 2、THS 2、ROT 2、RSA 4、
    /// TTM 15、ALR 5、ALF 13(ed.5)、VDM 6、VDO 6;ALC 為 4 + 4 × 項目數(另檢查)。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> Iec61162FieldCounts = new Dictionary<string, int>
    {
        ["GGA"] = 14, ["RMC"] = 12, ["VTG"] = 9, ["VBW"] = 10, ["DPT"] = 3, ["MWV"] = 5, ["HDT"] = 2, ["THS"] = 2, ["ROT"] = 2, ["RSA"] = 4, ["TTM"] = 15, ["ALR"] = 5, ["ALF"] = 13, ["VDM"] = 6, ["VDO"] = 6,
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
            var rev = 0;
            foreach (var a in st.Alerts ?? Array.Empty<AlertStatus>()) all.Add(NmeaEncoder.Alf(a, utc, rev, ++rev));
            all.AddRange(NmeaEncoder.Alc((st.Alerts ?? Array.Empty<AlertStatus>()).Select((a, i) => (a.Number, i + 1)).ToArray(), 1));
            all.AddRange(NmeaEncoder.OwnPositionReport(st, TestData.Fsb1(), 416100001, utc));
            all.AddRange(NmeaEncoder.OwnStaticAndVoyage(st, TestData.Fsb1(), 416100001, 9));
            all.AddRange(NmeaEncoder.TtmSentences(st, utc));
            foreach (var t in st.Targets ?? Array.Empty<TargetState>())
                if (t.Ais is { } ais)
                {
                    all.AddRange(AisEncoder.PositionReport(ais, utc));
                    all.AddRange(AisEncoder.StaticAndVoyage(ais, 1));
                }
        }
        Assert.Contains(all, x => x.StartsWith("$IIALR"));
        Assert.Contains(all, x => x.StartsWith("$IIALF"));
        Assert.Contains(all, x => x.StartsWith("$IIALC") && !x.StartsWith("$IIALC,01,01,01,0*"));
        Assert.Contains(all, x => x.StartsWith("!AIVDM,2,"));
        Assert.Contains(all, x => x.StartsWith("!AIVDO,2,"));
        Assert.Contains(all, x => x.StartsWith("$HETHS,,V"));
        Assert.Contains(all, x => x.StartsWith("$GPGGA") && x.Contains(",0,00,"));
        var expectedTalker = new Dictionary<string, string> { ["GGA"] = "GP", ["RMC"] = "GP", ["VTG"] = "GP", ["HDT"] = "HE", ["THS"] = "HE", ["ROT"] = "TI", ["VBW"] = "VD", ["DPT"] = "SD", ["MWV"] = "WI", ["RSA"] = "ER", ["TTM"] = "RA", ["ALR"] = "II", ["ALF"] = "II", ["ALC"] = "II", ["VDM"] = "AI", ["VDO"] = "AI" };
        foreach (var s in all)
        {
            Assert.True(NmeaSentence.Verify(s), s);
            Assert.True(s.Length <= 82, s);
            var (talker, formatter, fields) = Split(s);
            int n;
            if (formatter == "ALC") n = 4 + 4 * int.Parse(fields[3]);
            else Assert.True(Iec61162FieldCounts.TryGetValue(formatter, out n), $"未列入欄位數表的句型 {formatter}:{s}");
            Assert.True(fields.Length == n, $"{formatter} 欄位數 {fields.Length} ≠ IEC 61162-1 的 {n}:{s}");
            Assert.Equal(expectedTalker[formatter], talker);
            Assert.All(fields, f => Assert.DoesNotContain(f, c => c is '$' or '!' or '*' or ',' or '\\' or '^' or '~' || c < 0x20 || c > 0x7E));
        }
    }

    [Fact]
    public void GatewayRatesForWholeSentenceSetOverTenSeconds()
    {
        // 10 s:HDT/THS/ROT/RSA 各 100、GGA/RMC/VTG/VBW/DPT/MWV(R)/MWV(T) 各 10、ALC 第 1 秒 1 句(0 項);無 MMSI → 無 VDO;
        // 電羅經故障後 HDT 停送、THS 改模式 V、ALR 與 ALF 每警報 1 Hz、ALC 於清單改變時 1 句(nmea_check 的 ±20% 以此為基準)
        var engine = TestData.MinimalEngine();
        var sink = new CollectingNmeaSink();
        using var gw = new NmeaGateway(engine, sink, new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));
        engine.Run(500);
        int Count(string prefix) => sink.Sentences.Count(x => x.StartsWith(prefix));
        Assert.Equal(100, Count("$HEHDT"));
        Assert.Equal(100, Count("$HETHS"));
        Assert.Equal(100, sink.Sentences.Count(x => x.StartsWith("$HETHS") && x.Contains(",A*")));
        Assert.Equal(100, Count("$TIROT"));
        Assert.Equal(100, Count("$ERRSA"));
        foreach (var p in new[] { "$GPGGA", "$GPRMC", "$GPVTG", "$VDVBW", "$SDDPT" }) Assert.Equal(10, Count(p));
        Assert.Equal(10, sink.Sentences.Count(x => x.StartsWith("$WIMWV") && x.Contains(",R,")));
        Assert.Equal(10, sink.Sentences.Count(x => x.StartsWith("$WIMWV") && x.Contains(",T,")));
        Assert.Equal(0, Count("$IIALR"));
        Assert.Equal(0, Count("$IIALF"));
        Assert.Equal(1, Count("$IIALC"));
        Assert.Equal(0, Count("!AIVDO"));
        Assert.Equal(471, sink.Sentences.Count); // 4 × 100 + 7 × 10 + 1
        // THS 與同批 HDT 航向相同
        Assert.Equal(Split(sink.Sentences.First(x => x.StartsWith("$HEHDT"))).Fields[0], Split(sink.Sentences.First(x => x.StartsWith("$HETHS"))).Fields[0]);
        // 第 10 秒的批次:GGA 時刻 02:00:10.00;同批 RMC 時刻相同(nmea_check 的時間一致性檢查)
        var gga = sink.Sentences.Last(x => x.StartsWith("$GPGGA"));
        var rmc = sink.Sentences.Last(x => x.StartsWith("$GPRMC"));
        Assert.Equal("020010.00", Split(gga).Fields[0]);
        Assert.Equal("020010.00", Split(rmc).Fields[0]);

        sink.Sentences.Clear();
        engine.Enqueue(SimCommand.InjectFault("gyroFailure"));
        engine.Run(500);
        Assert.Equal(0, Count("$HEHDT"));
        Assert.Equal(100, Count("$HETHS"));
        Assert.All(sink.Sentences.Where(x => x.StartsWith("$HETHS")), x => Assert.Equal(new[] { "", "V" }, Split(x).Fields));
        Assert.Equal(100, Count("$TIROT"));
        Assert.Equal(10, Count("$IIALR"));
        Assert.Equal(10, Count("$IIALF"));
        Assert.Equal(1, Count("$IIALC")); // 清單改變(第 11 秒);下一次週期在第 31 秒
        Assert.All(sink.Sentences.Where(x => x.StartsWith("$IIALR")), x => Assert.Equal("001", Split(x).Fields[1]));
        Assert.All(sink.Sentences.Where(x => x.StartsWith("$IIALF")), x => Assert.Equal(("001", "V", "1"), (Split(x).Fields[8], Split(x).Fields[6], Split(x).Fields[10])));
        Assert.Equal(new[] { "01", "01", "01", "1", "SIM", "001", "1", "1" }, Split(sink.Sentences.Single(x => x.StartsWith("$IIALC"))).Fields);
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
