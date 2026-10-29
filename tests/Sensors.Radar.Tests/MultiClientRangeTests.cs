using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.Sensors.Radar.Net;
using SimosaBRM.SimCore.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 每客戶端獨立顯示量程(radar-arpa.md 第 4.1 節):相異量程各一份影像(6/12 nm 由 ARPA 影像重取樣、其餘自行繪製)、
/// 幀依客戶端量程分送、status.range_nm 為各自量程、離線清理、情境切換沿用、效能(3 個量程 &lt; 1 核心)、
/// 學員控制指令轉送核心(radarControl)。
/// </summary>
public class MultiClientRangeTests
{
    private readonly ITestOutputHelper _out;
    public MultiClientRangeTests(ITestOutputHelper output) => _out = output;

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
        }
    }

    [Fact]
    public void ResampleLineMapsCellsAndScale()
    {
        // 來源 8 格 × 10 m;目的 4 格 × 20 m:目的格 i 取來源格 (i+0.5)×2;強度 × 增益並飽和
        var src = new byte[] { 10, 20, 30, 40, 50, 60, 70, 200 };
        var dst = new byte[4];
        RadarUnit.ResampleLine(src, 10, dst, 20, 2.0f);
        Assert.Equal(new byte[] { 40, 80, 120, 255 }, dst);
        // 目的超出來源 → 0
        var dst2 = new byte[6];
        RadarUnit.ResampleLine(src, 10, dst2, 20, 1.0f);
        Assert.Equal(0, dst2[5]);
        var x = RadarFactory.X();
        Assert.True(x.ResamplesFromArpa(6) && x.ResamplesFromArpa(12));
        Assert.False(x.ResamplesFromArpa(3) || x.ResamplesFromArpa(24) || x.ResamplesFromArpa(0.75));
    }

    /// <summary>目標回波的徑向連續格數(強度 > 100)換成公尺:靜止 30 m 漁船在 (1500, 2500),本船靜止、量程 <paramref name="rangeNm"/>。</summary>
    private static double EchoExtentM(RadarUnit radar, double rangeNm)
    {
        var scene = new LinearScene { OwnSpeedKn = 0 };
        scene.Targets.Add(new LinearTarget("T", 1500, 2500, 250, 0, 30, 8, "fishing", 2));
        radar.DisplayRanges = new[] { rangeNm };
        var results = scene.Run(radar, 0, 3);
        var byRange = results.Select(r => { var x = new RadarStepResult(); x.Frames.AddRange(r.Frames.Where(f => Math.Abs(SpokeFrame.Parse(f).RangeM - rangeNm * 1852) < 1)); return x; }).ToList();
        var img = RadarFactory.CollectScan(byRange, radar.Config.Spokes);
        var cell = rangeNm * 1852 / radar.Config.DisplayCells;
        var expectedM = Math.Sqrt(1500.0 * 1500 + 2500.0 * 2500);
        var k = (int)Math.Round(Math.Atan2(1500, 2500) * 180 / Math.PI / 360 * radar.Config.Spokes);
        var best = 0;
        for (var i = k - 8; i <= k + 8; i++)
        {
            var s = img[(i + radar.Config.Spokes) % radar.Config.Spokes];
            if (s is null) continue;
            var run = 0;
            for (var j = (int)((expectedM - 200) / cell); j <= (int)((expectedM + 200) / cell) && j < s.Length; j++)
            {
                run = s[j] > 100 ? run + 1 : 0;
                best = Math.Max(best, run);
            }
        }
        return best * cell;
    }

    /// <summary>
    /// status.pulse / pulseLength_m 與影像實際使用的脈衝一致(radar-arpa.md 第 4.1 節):由 ARPA 影像重取樣的量程(6、12 nm)回報 ARPA 處理脈衝(45 m,medium),
    /// 自行繪製的量程回報量程脈衝(3 nm 45 m、24 nm 180 m);回波徑向長度 ≈ 脈衝 + 目標長度(重取樣 6 nm 約 87 m,若以 75 m 脈衝繪製會是約 130 m)。
    /// </summary>
    [Fact]
    public void StatusPulseMatchesPulseUsedForImage()
    {
        var radar = RadarFactory.X();
        var c = Guid.NewGuid();
        foreach (var (nm, pulse, pulseM) in new[] { (3.0, "medium", 45.0), (6.0, "medium", radar.Config.ArpaPulseM), (12.0, "medium", radar.Config.ArpaPulseM), (24.0, "long", 180.0) })
        {
            radar.Apply($"{{\"cmd\":\"range\",\"value\":{nm}}}", c);
            var st = radar.Status(c);
            Assert.Equal(pulse, st.Pulse);
            Assert.Equal(pulseM, st.PulseLengthM);
        }
        Assert.True(radar.ResamplesFromArpa(6) && radar.ResamplesFromArpa(12) && !radar.ResamplesFromArpa(3) && !radar.ResamplesFromArpa(24));
        // 重取樣的 6 nm(預設量程、無客戶端):回波長度在 [脈衝, 脈衝 + 目標長度 + 2 格] 內
        var resampled = RadarFactory.X();
        var cell6 = 6 * 1852.0 / resampled.Config.DisplayCells;
        var stR = resampled.Status();
        var extentR = EchoExtentM(resampled, 6);
        _out.WriteLine($"重取樣 6 nm:status 脈衝 {stR.PulseLengthM} m,回波 {extentR:F0} m");
        Assert.Equal(resampled.Config.ArpaPulseM, stR.PulseLengthM);
        Assert.InRange(extentR, stR.PulseLengthM, stR.PulseLengthM + 30 + 2 * cell6);
        // ARPA 量程縮到 3 nm 時 6 nm 改為自行繪製:status 回報 75 m,回波相應變長
        var native = RadarFactory.X(configure: cfg => cfg.ArpaRangeNm = 3);
        Assert.False(native.ResamplesFromArpa(6));
        var stN = native.Status();
        var extentN = EchoExtentM(native, 6);
        _out.WriteLine($"繪製 6 nm:status 脈衝 {stN.PulseLengthM} m,回波 {extentN:F0} m");
        Assert.Equal(75.0, stN.PulseLengthM);
        Assert.InRange(extentN, stN.PulseLengthM, stN.PulseLengthM + 30 + 2 * cell6);
        Assert.True(extentR < extentN);
    }

    [Fact]
    public void UnitProducesOneImagePerDistinctClientRange()
    {
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("T", 1500, 2500, 250, 10));
        var radar = RadarFactory.X();
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        var c3 = Guid.NewGuid();
        Assert.Equal(3.0, radar.Apply("{\"cmd\":\"range\",\"value\":3}", c1).Value);
        Assert.Equal(12.0, radar.Apply("{\"cmd\":\"range\",\"value\":12}", c2).Value);
        // c3 未設定 → 預設 6 nm;預設量程不受客戶端指令影響
        Assert.Equal(6.0, radar.Controls.RangeNm);
        Assert.Equal(3.0, radar.RangeFor(c1));
        Assert.Equal(6.0, radar.RangeFor(c3));
        Assert.Equal(6.0, radar.Status(c3).RangeNm);
        Assert.Equal(3.0, radar.Status(c1).RangeNm);
        Assert.Equal("medium", radar.Status(c1).Pulse);
        radar.DisplayRanges = new[] { radar.RangeFor(c1), radar.RangeFor(c2), radar.RangeFor(c3), radar.RangeFor(c2) };
        var results = scene.Run(radar, 0, 3);
        Assert.Equal(3, radar.ActiveDisplayCount);
        var ranges = results.SelectMany(r => r.Frames).Select(f => SpokeFrame.Parse(f).RangeM).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { 3 * 1852f, 6 * 1852f, 12 * 1852f }, ranges);
        // 每個量程都看得到目標(距離 ≈ 2916 m,相對方位 ≈ 31°):重取樣(6、12 nm)與自行繪製(3 nm)皆有回波
        var expectedM = Math.Sqrt(1500.0 * 1500 + 2500.0 * 2500);
        foreach (var rangeNm in new[] { 3.0, 6.0, 12.0 })
        {
            var byRange = results.Select(r => { var x = new RadarStepResult(); x.Frames.AddRange(r.Frames.Where(f => Math.Abs(SpokeFrame.Parse(f).RangeM - rangeNm * 1852) < 1)); return x; }).ToList();
            var img = RadarFactory.CollectScan(byRange, radar.Config.Spokes);
            var cell = rangeNm * 1852 / radar.Config.DisplayCells;
            var k = (int)Math.Round(31.0 / 360 * radar.Config.Spokes);
            var hit = Enumerable.Range(k - 8, 17).Select(i => img[(i + radar.Config.Spokes) % radar.Config.Spokes]).Where(s => s is not null)
                .Any(s => Enumerable.Range((int)((expectedM - 120) / cell), (int)(240 / cell) + 1).Any(i => i >= 0 && i < s.Length && s[i] > 100));
            Assert.True(hit, $"{rangeNm} nm 影像應含目標回波");
        }
        // 離線清理:移除 c1 後只剩兩個量程
        radar.RemoveClient(c1);
        Assert.Equal(6.0, radar.RangeFor(c1));
        radar.DisplayRanges = new[] { radar.RangeFor(c2), radar.RangeFor(c3) };
        scene.Run(radar, 3.04, 4);
        Assert.Equal(2, radar.ActiveDisplayCount);
        // 無客戶端(DisplayRanges null)→ 預設量程一份
        radar.DisplayRanges = null;
        scene.Run(radar, 4.04, 5);
        Assert.Equal(1, radar.ActiveDisplayCount);
    }

    [Fact]
    public void ImagesDoNotDependOnWhichClientsAreConnected()
    {
        // 同種子、同狀態序列:只開 3 nm 的雷達與開 3+6+24 nm 的雷達,3 nm 的影像位元相同(確定性與客戶端無關)
        var scene = new LinearScene { OwnSpeedKn = 8, WindKn = 15 };
        scene.Targets.Add(new LinearTarget("T", 1500, 2500, 250, 10));
        var solo = RadarFactory.X();
        solo.Apply("{\"cmd\":\"range\",\"value\":3}");
        var multi = RadarFactory.X();
        multi.DisplayRanges = new[] { 3.0, 6.0, 24.0 };
        var a = scene.Run(solo, 0, 3).SelectMany(r => r.Frames).ToList();
        var b = scene.Run(multi, 0, 3).SelectMany(r => r.Frames).Where(f => Math.Abs(SpokeFrame.Parse(f).RangeM - 3 * 1852) < 1).ToList();
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void ThreeClientRangesStayUnderOneCore()
    {
        // 效能守則(規劃書第 5.2 節):顯示 0.75 + 6(重取樣)+ 24 nm 三份影像 + ARPA 2048 格、3 目標、雨區、陸地、海雜波,每圈處理 < 掃描週期
        var env = RadarEnvironment.Empty();
        env.AddRain(new RainCell(-4000, 6000, 2500, 8));
        env.AddLand(new LandPolygon { Name = "coast", HeightM = 30, Points = new[] { (9000.0, -20000.0), (30000.0, -20000.0), (30000.0, 20000.0), (9000.0, 20000.0) } });
        var radar = RadarFactory.X(env: env);
        radar.Apply("{\"cmd\":\"rain\",\"value\":40}");
        radar.DisplayRanges = new[] { 0.75, 6.0, 24.0 };
        var scene = new LinearScene { OwnSpeedKn = 9, OwnCourseDeg = 20, WindKn = 18, WindFromDeg = 250 };
        scene.Targets.Add(new LinearTarget("A", 3000, 6000, 200, 11));
        scene.Targets.Add(new LinearTarget("B", -2500, 2500, 100, 7, 30, 7, "fishing"));
        scene.Targets.Add(new LinearTarget("C", 500, -1500, 20, 14, 180, 30, "tanker", 10));
        scene.Run(radar, 0, 1); // 暖機(JIT)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        scene.Run(radar, 1.04, 1.0 + 3 * radar.Config.ScanPeriodS);
        sw.Stop();
        var perScan = sw.Elapsed.TotalSeconds / 3;
        _out.WriteLine($"三量程每圈處理 {perScan * 1000:F0} ms(掃描週期 {radar.Config.ScanPeriodS * 1000:F0} ms)→ 約 {perScan / radar.Config.ScanPeriodS * 100:F1}% 單核");
        Assert.Equal(3, radar.ActiveDisplayCount);
        Assert.True(perScan < radar.Config.ScanPeriodS, $"每圈 {perScan:F2} s ≥ 掃描週期");
    }

    [Fact]
    public async Task TwoClientsGetTheirOwnRangeAndControlsAreForwarded()
    {
        var port = FreeTcpPort();
        await using var server = new RadarWebSocketServer(port, new[] { "x" }, allInterfaces: false);
        server.Start();
        using var service = new RadarService(new[] { RadarFactory.X() }, server, null);
        var forwarded = new System.Collections.Concurrent.ConcurrentQueue<SimCommand>();
        service.ForwardControl = forwarded.Enqueue;
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("T", 2000, 4000, 250, 10));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var t = 0.0;
        var feeding = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                service.OnState(scene.StateAt(t));
                t += 0.04;
                await Task.Delay(2, cts.Token).ContinueWith(_ => { });
            }
        });

        using var a = new ClientWebSocket();
        using var b = new ClientWebSocket();
        await a.ConnectAsync(new Uri($"ws://localhost:{port}/radar/x"), cts.Token);
        await b.ConnectAsync(new Uri($"ws://localhost:{port}/radar/x"), cts.Token);
        // b 改 3 nm;a 不動(預設 6 nm)
        await b.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"range\",\"value\":3,\"reqId\":1}"), WebSocketMessageType.Text, true, cts.Token);
        await b.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"gain\",\"value\":55}"), WebSocketMessageType.Text, true, cts.Token);
        await b.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"getTracks\"}"), WebSocketMessageType.Text, true, cts.Token);

        async Task<(HashSet<float> Ranges, List<double> StatusRanges, double? Gain)> Collect(ClientWebSocket ws, int frames)
        {
            var ranges = new HashSet<float>();
            var statuses = new List<double>();
            double? gain = null;
            var n = 0;
            while (n < frames)
            {
                var (type, data) = await ReceiveAsync(ws, cts.Token);
                if (type == WebSocketMessageType.Binary)
                {
                    ranges.Add(SpokeFrame.Parse(data).RangeM);
                    n++;
                    continue;
                }
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.GetProperty("type").GetString() == "status")
                {
                    statuses.Add(doc.RootElement.GetProperty("range_nm").GetDouble());
                    gain = doc.RootElement.GetProperty("gain").GetDouble();
                }
            }
            return (ranges, statuses, gain);
        }
        // 先讓 b 的指令生效(每個客戶端各自收;a 的幀在 b 指令前可能已有 6 nm,之後也仍是 6 nm)
        var rb = await Collect(b, 60);
        var ra = await Collect(a, 60);
        // 四個測試專案併行時 a 可能在 60 幀內尚未收到含新增益的 status:再等最多 200 幀直到 gain = 55(只看增益;量程斷言仍用首批)
        for (var extra = 0; ra.Gain != 55.0 && extra < 10; extra++)
        {
            var more = await Collect(a, 20);
            Assert.All(more.Ranges, r => Assert.Equal(6 * 1852f, r));
            ra = (ra.Ranges, ra.StatusRanges, more.Gain ?? ra.Gain);
        }
        Assert.Contains(3 * 1852f, rb.Ranges);
        Assert.Contains(3.0, rb.StatusRanges);
        Assert.All(ra.Ranges, r => Assert.Equal(6 * 1852f, r));
        Assert.All(ra.StatusRanges, r => Assert.Equal(6.0, r));
        // gain 影響全部客戶端:a 也收到含新增益的 status(各自量程)
        Assert.Equal(55.0, ra.Gain);
        Assert.Equal(55.0, rb.Gain);
        // 收尾幀:b 之後只收 3 nm
        var rb2 = await Collect(b, 20);
        Assert.All(rb2.Ranges, r => Assert.Equal(3 * 1852f, r));

        // 轉送核心:range 與 gain 轉送、getTracks 查詢不轉送
        SpinWait.SpinUntil(() => forwarded.Count >= 2, 5000);
        var cmds = forwarded.ToList();
        Assert.Equal(2, cmds.Count);
        Assert.All(cmds, c => Assert.Equal(SimCommandType.RadarControl, c.Type));
        Assert.Equal("range", cmds[0].ArgAsString("cmd"));
        Assert.Equal("x", cmds[0].ArgAsString("radar"));
        Assert.Equal(3.0, cmds[0].ArgAsDouble("value"));
        Assert.True(cmds[0].ArgAsBool("ok"));
        Assert.Equal(32, cmds[0].ArgAsString("client")!.Length);
        Assert.Equal("gain", cmds[1].ArgAsString("cmd"));
        var req = cmds[1].Arg("request")!.Value;
        Assert.Equal(55.0, req.GetProperty("value").GetDouble());
        Assert.False(req.TryGetProperty("cmd", out _));
        Assert.False(cmds[0].Arg("request")!.Value.TryGetProperty("reqId", out _));
        Assert.DoesNotContain(cmds, c => c.ArgAsString("cmd") == "getTracks");
        Assert.Equal(2, service.ControlsForwarded);

        // b 離線 → 處理執行緒清理其量程;a 仍收 6 nm(關閉握手若被伺服器先行中止亦不影響本測試的斷言)
        try { await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token); }
        catch (WebSocketException) { }
        SpinWait.SpinUntil(() => service.Units[0].ClientRanges.Count == 0, 5000);
        Assert.Empty(service.Units[0].ClientRanges);
        var ra2 = await Collect(a, 10);
        Assert.All(ra2.Ranges, r => Assert.Equal(6 * 1852f, r));
        // 情境切換沿用每客戶端量程(a 無獨立設定,預設量程由舊雷達帶過)
        cts.Cancel();
        await feeding;
        await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => { });
    }

    [Fact]
    public void SetUnitsKeepsClientRanges()
    {
        var old = RadarFactory.X();
        var client = Guid.NewGuid();
        old.Apply("{\"cmd\":\"range\",\"value\":12}", client);
        old.Apply("{\"cmd\":\"range\",\"value\":1.5}");
        using var service = new RadarService(new[] { old }, null, null);
        var next = RadarFactory.X(seed: 9);
        service.SetUnits(new[] { next });
        Assert.Equal(1.5, next.Controls.RangeNm);
        Assert.Equal(12.0, next.RangeFor(client));
    }
}
