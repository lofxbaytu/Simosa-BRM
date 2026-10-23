using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.Sensors.Radar.Net;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Traffic;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 純 AIS 目標(tracks.aisTargets[]:有 AIS 報告、未被追蹤中的 ARPA 目標關聯者;radar-arpa.md 第 3.2 節)與
/// 重播接收端(/radar/replay:主控程序 --replay 把紀錄的 radarControl 行重放到雷達,不轉送核心;第 5.2 節)。
/// </summary>
public class AisOnlyAndReplayTests
{
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
    public void AisTargetsListUnassociatedReportsOnly()
    {
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("ALFA", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416001001));
        scene.Targets.Add(new LinearTarget("BRAVO", -3000, 2000, 90, 12, AisOn: true, Mmsi: 416001002));
        scene.Targets.Add(new LinearTarget("NOAIS", 1000, -3000, 0, 6));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        // 尚未擷取:兩個 AIS 報告都是純 AIS 目標;無 AIS 的目標不在清單
        var before = radar.Tracks();
        Assert.NotNull(before.AisTargets);
        Assert.Equal(new[] { 416001001, 416001002 }, before.AisTargets!.Select(a => a.Mmsi).ToArray());
        var alfa = before.AisTargets.Single(a => a.Mmsi == 416001001);
        var truth = scene.Truth("ALFA", 2);
        Assert.Equal("ALFA", alfa.Name);
        Assert.InRange(alfa.RangeNm, truth.RangeNm - 0.02, truth.RangeNm + 0.02);
        Assert.InRange(alfa.BearingDeg, Units.RadToDeg(truth.BearingRad) - 1, Units.RadToDeg(truth.BearingRad) + 1);
        Assert.InRange(alfa.CpaNm, truth.CpaNm - 0.02, truth.CpaNm + 0.02);
        Assert.Equal(250.0, alfa.CogDeg);
        Assert.Equal(10.0, alfa.SogKn);
        Assert.Null(alfa.CandidateTrackId);
        var json = RadarJson.Text(before);
        Assert.Contains("\"aisTargets\":[{\"mmsi\":416001001", json);

        // 擷取 ALFA 並等到關聯:ALFA 離開純 AIS 清單,BRAVO 仍在
        var g = scene.Truth("ALFA", 2);
        Assert.True(radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3}}}").Ok);
        scene.Run(radar, 2.04, 120);
        var tr = radar.Arpa.Tracks.Single();
        Assert.True(tr.AisAssociated);
        var after = radar.Tracks();
        Assert.Equal(new[] { 416001002 }, after.AisTargets!.Select(a => a.Mmsi).ToArray());
        Assert.Equal(416001001, after.Targets.Single().Ais!.Mmsi);
    }

    [Fact]
    public void AisTargetsOmittedWithoutReports()
    {
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("NOAIS", 1000, -3000, 0, 6));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 3);
        Assert.Null(radar.Tracks().AisTargets);
        Assert.DoesNotContain("aisTargets", RadarJson.Text(radar.Tracks()));
    }

    [Fact]
    public void ReplayClientIdParsesGuidOrHashesDeterministically()
    {
        var g = Guid.NewGuid();
        Assert.Equal(g, RadarWebSocketServer.ReplayClientId(g.ToString("N")));
        Assert.Equal(RadarWebSocketServer.ReplayClientId("c1"), RadarWebSocketServer.ReplayClientId("c1"));
        Assert.NotEqual(RadarWebSocketServer.ReplayClientId("c1"), RadarWebSocketServer.ReplayClientId("c2"));
        Assert.NotEqual(Guid.Empty, RadarWebSocketServer.ReplayClientId(""));
    }

    [Fact]
    public async Task ReplayEndpointAppliesControlsWithoutForwarding()
    {
        var port = FreeTcpPort();
        await using var server = new RadarWebSocketServer(port, new[] { "x", "s" }, allInterfaces: false);
        server.Start();
        using var service = new RadarService(new[] { RadarFactory.X(), RadarFactory.S() }, server, null);
        var forwarded = new List<SimCommand>();
        service.ForwardControl = cmd => { lock (forwarded) forwarded.Add(cmd); };
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("T", 2000, 4000, 250, 10));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // 場景推進由測試執行緒受控:餵送執行緒在 feedLock 內推進一幀(t 為下一幀的時間);測試執行緒取得鎖即凍結場景,
        // 以 paused 暫停餵送時送出擷取,擷取方位/距離用的 t 就是恢復後第一幀(DrainCommands 套用擷取的那一幀)的時間,
        // 不隨牆鐘(CPU 負載、WebSocket 往返)漂移,搜尋位置一定有回波(原寫法在機器忙碌時擷取套用過晚、追蹤被丟棄而不穩定)
        var feedLock = new object();
        var paused = false;
        var t = 0.0;
        var feeding = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                lock (feedLock)
                {
                    if (!paused)
                    {
                        service.OnState(scene.StateAt(t));
                        t += 0.04;
                    }
                }
                await Task.Delay(2, cts.Token).ContinueWith(_ => { });
            }
        });

        // 顯示客戶端(PPI)與重播接收端
        using var display = new ClientWebSocket();
        await display.ConnectAsync(new Uri($"ws://localhost:{port}/radar/x"), cts.Token);
        using var replay = new ClientWebSocket();
        await replay.ConnectAsync(new Uri($"ws://localhost:{port}/radar/replay"), cts.Token);
        Assert.Equal(1, server.ClientCount("x"));
        Assert.Equal(1, server.ClientCount("replay")); // 重播接收端不算雷達顯示客戶端(ClientIds("x") 不含它)

        // 紀錄的 radarControl 行 → 重播訊息(主控程序 RadarReplayClient 的格式):量程 3 nm、手動擷取
        // 暫停餵送並以「下一幀」的 t 算目標真值;replayAck 在指令進佇列之後才回,收齊後恢復餵送,第一幀 DrainCommands 即以同一 t 套用擷取
        EncounterGeometry g;
        lock (feedLock)
        {
            paused = true;
            g = scene.Truth("T", t);
        }
        var client = Guid.NewGuid().ToString("N");
        await replay.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"radarControlReplay\",\"radar\":\"x\",\"client\":\"{client}\",\"t\":12.5,\"request\":{{\"cmd\":\"range\",\"value\":3}}}}"), WebSocketMessageType.Text, true, cts.Token);
        await replay.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"radarControlReplay\",\"radar\":\"x\",\"client\":\"{client}\",\"t\":13,\"request\":{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3}}}}}"), WebSocketMessageType.Text, true, cts.Token);
        await replay.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"radarControlReplay\",\"radar\":\"q\",\"request\":{\"cmd\":\"range\",\"value\":3}}"), WebSocketMessageType.Text, true, cts.Token);
        await replay.SendAsync(Encoding.UTF8.GetBytes("not json"), WebSocketMessageType.Text, true, cts.Token);

        // 重播接收端的回覆:2 個 ok、2 個失敗
        int okAcks = 0, badAcks = 0;
        while (okAcks + badAcks < 4)
        {
            var (type, data) = await ReceiveAsync(replay, cts.Token);
            Assert.Equal(WebSocketMessageType.Text, type);
            using var doc = JsonDocument.Parse(data);
            Assert.Equal("replayAck", doc.RootElement.GetProperty("type").GetString());
            if (doc.RootElement.GetProperty("ok").GetBoolean()) okAcks++; else badAcks++;
        }
        Assert.Equal(2, okAcks);
        Assert.Equal(2, badAcks);
        lock (feedLock) paused = false;

        // 顯示客戶端:status 量程跟著重播變成 3 nm、影像幀 3 nm、tracks 出現目標;重播的指令不轉送核心
        bool rangeStatus = false, frame3nm = false, tracksWithTarget = false;
        while (!(rangeStatus && frame3nm && tracksWithTarget))
        {
            var (type, data) = await ReceiveAsync(display, cts.Token);
            if (type == WebSocketMessageType.Binary)
            {
                if (Math.Abs(SpokeFrame.Parse(data).RangeM - 3 * 1852) < 1) frame3nm = true;
                continue;
            }
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "status": if (root.GetProperty("range_nm").GetDouble() == 3.0) rangeStatus = true; break;
                case "tracks": if (root.GetProperty("targets").GetArrayLength() > 0) tracksWithTarget = true; break;
                case "ack": Assert.Fail("重播的指令不應回 ack 給顯示客戶端"); break;
            }
        }
        Assert.Equal(2, server.ReplayedCommands);
        Assert.Equal(2, service.ControlsReplayed);
        lock (forwarded) Assert.Empty(forwarded);
        Assert.Equal(0, service.ControlsForwarded);
        lock (feedLock) Assert.Single(service.Units[0].Arpa.Tracks); // 鎖內讀取:不與餵送執行緒的 OnState 並行

        // 顯示客戶端自己的指令仍會轉送(重播與即時操作可並存)
        await display.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"gain\",\"value\":70}"), WebSocketMessageType.Text, true, cts.Token);
        while (true)
        {
            var (type, data) = await ReceiveAsync(display, cts.Token);
            if (type != WebSocketMessageType.Text) continue;
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.GetProperty("type").GetString() == "ack") break;
        }
        // ack 由送出佇列非同步送達客戶端,ForwardControl 在處理執行緒於 ack 排入之後才呼叫(RadarService.DrainCommands):
        // 收到 ack 不代表已轉送,須等到轉送發生(受 cts 60 s 上限)再斷言;原寫法在機器忙碌時 ack 先到、轉送尚未執行而不穩定
        while (true)
        {
            lock (forwarded) if (forwarded.Count > 0) break;
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, cts.Token);
        }
        lock (forwarded) Assert.Single(forwarded);
        Assert.Equal(1, service.ControlsForwarded);

        cts.Cancel();
        await feeding;
        await replay.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => { });
        await display.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => { });
    }
}
