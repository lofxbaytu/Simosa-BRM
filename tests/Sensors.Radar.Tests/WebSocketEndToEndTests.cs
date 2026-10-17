using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.Sensors.Radar.Net;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>WebSocket 端對端(本機迴路):狀態訂閱 → 雷達服務 → /radar/x 影像幀 + JSON(status/tracks/ack)+ UDP NMEA。</summary>
public class WebSocketEndToEndTests
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
    public async Task RadarServerStreamsFramesTracksAndAcceptsCommands()
    {
        var port = FreeTcpPort();
        await using var server = new RadarWebSocketServer(port, new[] { "x", "s" }, allInterfaces: false);
        server.Start();
        using var udpRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var nmeaPort = ((IPEndPoint)udpRx.Client.LocalEndPoint!).Port;
        var nmea = new NmeaOutputOptions { Host = "127.0.0.1" };
        nmea.Ports["x"] = nmeaPort;
        nmea.Ports["s"] = FreeTcpPort();
        using var service = new RadarService(new[] { RadarFactory.X(), RadarFactory.S() }, server, nmea);
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("T", 2000, 4000, 250, 10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/radar/x"), cts.Token);
        // 第一個 JSON 應為 status
        JsonDocument? status = null;
        var gotFrame = false;
        while (status is null || !gotFrame)
        {
            var (type, data) = await ReceiveAsync(ws, cts.Token);
            if (type == WebSocketMessageType.Binary)
            {
                var f = SpokeFrame.Parse(data);
                Assert.Equal('X', f.RadarId);
                gotFrame = true;
            }
            else
            {
                var doc = JsonDocument.Parse(data);
                if (doc.RootElement.GetProperty("type").GetString() == "status") status = doc;
            }
        }
        Assert.Equal("x", status.RootElement.GetProperty("radar").GetString());
        Assert.Equal(6.0, status.RootElement.GetProperty("range_nm").GetDouble());

        // 指令:量程 3 nm(reqId 回傳)與手動擷取
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"range\",\"value\":3,\"reqId\":7}"), WebSocketMessageType.Text, true, cts.Token);
        var g = scene.Truth("T", t);
        await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3},\"reqId\":\"acq\"}}"), WebSocketMessageType.Text, true, cts.Token);
        bool rangeAck = false, acqAck = false, rangeStatus = false, tracksWithTarget = false, frame3nm = false;
        while (!(rangeAck && acqAck && rangeStatus && tracksWithTarget && frame3nm))
        {
            var (type, data) = await ReceiveAsync(ws, cts.Token);
            if (type == WebSocketMessageType.Binary)
            {
                if (Math.Abs(SpokeFrame.Parse(data).RangeM - 3 * 1852) < 1) frame3nm = true;
                continue;
            }
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ack":
                    Assert.True(root.GetProperty("ok").GetBoolean(), data.Length > 0 ? Encoding.UTF8.GetString(data) : "");
                    if (root.GetProperty("cmd").GetString() == "range") { rangeAck = true; Assert.Equal(7, root.GetProperty("reqId").GetInt32()); }
                    if (root.GetProperty("cmd").GetString() == "acquire") { acqAck = true; Assert.Equal(1, root.GetProperty("id").GetInt32()); }
                    break;
                case "status":
                    if (root.GetProperty("range_nm").GetDouble() == 3.0) rangeStatus = true;
                    break;
                case "tracks":
                    if (root.GetProperty("targets").GetArrayLength() > 0) tracksWithTarget = true;
                    break;
            }
        }

        // NMEA TTM 由 UDP 送到 X 的埠
        var udpTask = udpRx.ReceiveAsync(cts.Token).AsTask();
        var res = await udpTask;
        var text = Encoding.ASCII.GetString(res.Buffer);
        Assert.Contains("$RATTM,01,", text);
        Assert.Contains("$RATLL,01,", text);

        cts.Cancel();
        await feeding;
        Assert.True(service.StatesProcessed > 0);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => { });
    }

    [Fact]
    public async Task SetUnitsBroadcastsFreshStatus()
    {
        // 情境切換(SetUnits)後新雷達的試操船與 CPA/TCPA 限值回到預設;必須廣播新的 status,
        // 否則客戶端沿用舊 status 的 trial 與 cpaLimit(審查發現:網頁仍顯示「TRIAL 090.0°…」與 0.5 nm)
        var port = FreeTcpPort();
        await using var server = new RadarWebSocketServer(port, new[] { "x" }, allInterfaces: false);
        server.Start();
        using var service = new RadarService(new[] { RadarFactory.X() }, server, null);
        var sceneA = new LinearScene { OwnSpeedKn = 8, ScenarioId = "A" };
        var sceneB = new LinearScene { OwnSpeedKn = 8, ScenarioId = "B" };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/radar/x"), cts.Token);
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var receiving = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var (type, data) = await ReceiveAsync(ws, cts.Token);
                    if (type != WebSocketMessageType.Text) continue;
                    var txt = Encoding.UTF8.GetString(data);
                    using var doc = JsonDocument.Parse(txt);
                    if (doc.RootElement.GetProperty("type").GetString() == "status") statuses.Enqueue(txt);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        });
        // 狀態幀在測試執行緒餵入(與 Host 相同:SetUnits 與 OnState 同一處理執行緒)
        var t = 0.0;
        async Task FeedUntil(LinearScene sc, Func<bool> done, int maxStates = 1500)
        {
            for (var i = 0; !done() && i < maxStates; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                service.OnState(sc.StateAt(t));
                t += 0.04;
                await Task.Delay(2);
            }
        }
        static bool IsArmed(string s) => s.Contains("\"trial\":{") && s.Contains("\"cpaLimit_nm\":0.5");
        await FeedUntil(sceneA, () => !statuses.IsEmpty);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"trialManoeuvre\",\"course\":90,\"speed\":8,\"delay\":1}"), WebSocketMessageType.Text, true, cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"cpaLimits\",\"cpa_nm\":0.5,\"tcpa_min\":6}"), WebSocketMessageType.Text, true, cts.Token);
        await FeedUntil(sceneA, () => statuses.Any(IsArmed));
        Assert.Contains(statuses, IsArmed);
        var before = statuses.Count;

        service.SetUnits(new[] { RadarFactory.X(seed: 7) });
        t = 0;
        await FeedUntil(sceneB, () => statuses.Count > before, maxStates: 250); // 10 s 模擬時間內必須收到
        Assert.True(statuses.Count > before, "SetUnits 後未廣播 status");
        cts.Cancel();
        await receiving;

        var last = statuses.Last();
        Assert.False(IsArmed(last), last);
        using var lastDoc = JsonDocument.Parse(last);
        var root = lastDoc.RootElement;
        Assert.True(!root.TryGetProperty("trial", out var trial) || trial.ValueKind == JsonValueKind.Null, last);
        Assert.Equal(service.Units[0].Arpa.CpaLimitNm, root.GetProperty("cpaLimit_nm").GetDouble());
        Assert.Equal(service.Units[0].Arpa.TcpaLimitMin, root.GetProperty("tcpaLimit_min").GetDouble());
    }

    [Fact]
    public async Task UnknownPathIsRejected()
    {
        var port = FreeTcpPort();
        await using var server = new RadarWebSocketServer(port, new[] { "x" }, allInterfaces: false);
        server.Start();
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"ws://localhost:{port}/radar/q"), CancellationToken.None));
    }

    [Fact]
    public async Task StateClientParsesStatesAndSkipsEvents()
    {
        // 假的 SimCore 狀態伺服器:先送一個事件框,再送狀態框
        var port = FreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var scene = new LinearScene { OwnSpeedKn = 5 };
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var wsc = await ctx.AcceptWebSocketAsync(null);
            await wsc.WebSocket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"event\",\"event\":{\"kind\":\"x\"}}"), WebSocketMessageType.Text, true, CancellationToken.None);
            for (var i = 0; i < 3; i++)
                await wsc.WebSocket.SendAsync(ContractJson.SerializeToUtf8Bytes(scene.StateAt(i * 0.04)), WebSocketMessageType.Text, true, CancellationToken.None);
            await Task.Delay(500);
            try { await wsc.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch (Exception) { }
        });
        var client = new SimStateClient(new Uri($"ws://localhost:{port}/"));
        var states = new List<OwnShipState>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = client.RunAsync(s => { lock (states) { states.Add(s); if (states.Count == 3) cts.Cancel(); } }, cts.Token);
        await run;
        await serverTask;
        Assert.Equal(3, states.Count);
        Assert.Equal(0.08, states[2].T, 6);
        Assert.Equal("FSB1", states[0].ShipId);
        Assert.Null(SimStateClient.ParseState(Encoding.UTF8.GetBytes("{\"type\":\"assessment\"}")));
    }
}
