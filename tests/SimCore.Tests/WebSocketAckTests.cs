using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Host;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 第十六輪審查已知限制 2:主控程序層指令(sessionInfo / shutdown)的 ack 與 getAssessment 回覆走每客戶端獨立、無界、不丟棄的回覆通道
/// (送出順序:回覆 → 事件 → 狀態幀)。事件通道(容量 256、丟最舊)被爆量事件塞滿、持續灌入時,客戶端仍一定收到 ack;
/// shutdown 的後續動作在 ack 真的送出後才執行,ack 送不出去(送出迴圈被塞住、客戶端離線)時記 log 並仍執行。
/// 以程序內的 WebSocketStateServer + ClientWebSocket(loopback)驗證。
/// </summary>
public class WebSocketAckTests
{
    private static readonly byte[] FloodFrame = ContractJson.SerializeToUtf8Bytes(new { type = "event", @event = new { kind = "flood", seq = -1 } });

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly SimulationEngine Engine = TestData.Engine();
        public readonly ShutdownCoordinator Shutdown = new();
        public readonly HostCommandRouter Router;
        public readonly WebSocketStateServer Server;
        public readonly ClientWebSocket Client = new();
        public readonly List<string> Log = new();

        private Fixture()
        {
            Router = new HostCommandRouter(Engine, Shutdown);
            Server = new WebSocketStateServer(Engine, FreePort()) { HostCommandHandler = Router.Handle };
            Server.Log += msg => { lock (Log) Log.Add(msg); };
        }

        public static async Task<Fixture> StartAsync()
        {
            var f = new Fixture();
            f.Server.Start();
            await f.Client.ConnectAsync(new Uri($"ws://127.0.0.1:{f.Server.Port}/"), CancellationToken.None);
            for (var i = 0; i < 200 && f.Server.ClientCount == 0; i++) await Task.Delay(10);
            Assert.Equal(1, f.Server.ClientCount);
            return f;
        }

        public Task SendAsync(object command)
            => Client.SendAsync(ContractJson.SerializeToUtf8Bytes(command), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>讀框直到 {"type":"ack"}(事件框計數後略過);回傳 ack 與略過的框數。</summary>
        public async Task<(JsonElement Ack, long Skipped)> ReceiveAckAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[256 * 1024];
            long skipped = 0;
            while (true)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await Client.ReceiveAsync(buffer, cts.Token);
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                var bytes = ms.ToArray();
                // 事件/狀態框數量極多:先看前綴再解析(ContractJson 把 type 放第一個屬性)
                if (!Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 16)).StartsWith("{\"type\":\"ack\"", StringComparison.Ordinal)) { skipped++; continue; }
                using var doc = JsonDocument.Parse(bytes);
                return (doc.RootElement.Clone(), skipped);
            }
        }

        /// <summary>以緊迴圈持續把事件框灌進每客戶端事件通道(遠快於送出迴圈 → 通道長期塞滿、丟最舊),直到取消。</summary>
        public Task<long> FloodAsync(CancellationToken token) => Task.Run(() =>
        {
            long n = 0;
            while (!token.IsCancellationRequested)
            {
                Server.PublishEvent(FloodFrame);
                n++;
            }
            return n;
        });

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Client.Dispose();
            Shutdown.Dispose();
        }
    }

    private static object SessionInfoCommand() => new
    {
        type = "sessionInfo",
        args = new
        {
            participants = new object[] { new { name = "王大副", role = "oow" } },
            restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = true },
        },
    };

    [Fact]
    public async Task SessionInfoAck_DeliveredWhileEventChannelIsFloodedAndOverflowing()
    {
        await using var f = await Fixture.StartAsync();
        using var floodCts = new CancellationTokenSource();
        var flood = f.FloodAsync(floodCts.Token);
        await Task.Delay(100); // 事件通道已塞滿並持續丟最舊
        await f.SendAsync(SessionInfoCommand());
        var (ack, skipped) = await f.ReceiveAckAsync(TimeSpan.FromSeconds(15));
        floodCts.Cancel();
        var published = await flood;
        Assert.Equal("sessionInfo", ack.GetProperty("command").GetString());
        Assert.True(ack.GetProperty("ok").GetBoolean(), ack.GetProperty("detail").GetString());
        Assert.True(published > 256 * 8, $"灌入 {published} 框不足以塞滿事件通道");
        Assert.True(skipped < published, $"送達 {skipped} / 灌入 {published}:事件通道應有丟棄(ack 不在其中)");
        Assert.NotNull(f.Router.Session);
        Assert.Equal(0, Interlocked.Read(ref f.Server.RepliesNotSent));
    }

    [Fact]
    public async Task ShutdownAck_DeliveredWhileFlooded_ThenShutdownRequested()
    {
        await using var f = await Fixture.StartAsync();
        using var floodCts = new CancellationTokenSource();
        var flood = f.FloodAsync(floodCts.Token);
        await Task.Delay(100);
        await f.SendAsync(new { type = "shutdown", args = new { reason = "test-flood" } });
        var (ack, _) = await f.ReceiveAckAsync(TimeSpan.FromSeconds(15));
        floodCts.Cancel();
        await flood;
        Assert.Equal("shutdown", ack.GetProperty("command").GetString());
        Assert.True(ack.GetProperty("ok").GetBoolean(), ack.GetProperty("detail").GetString());
        // AfterReply 在 ack 送出後執行:停止已請求、要求結束練習;ack 已送出所以沒有「未能送出」的 log
        for (var i = 0; i < 200 && !f.Shutdown.IsRequested; i++) await Task.Delay(10);
        Assert.True(f.Shutdown.IsRequested);
        Assert.Equal("shutdown", f.Shutdown.Reason);
        Assert.True(f.Shutdown.EndExerciseRequested);
        Assert.Equal(0, Interlocked.Read(ref f.Server.RepliesNotSent));
        lock (f.Log) Assert.DoesNotContain(f.Log, m => m.Contains("ack 未能送出"));
    }

    /// <summary>getAssessment 的回覆同樣走回覆通道,事件爆量時不會被丟。</summary>
    [Fact]
    public async Task AssessmentReply_DeliveredWhileFlooded()
    {
        await using var f = await Fixture.StartAsync();
        using var floodCts = new CancellationTokenSource();
        var flood = f.FloodAsync(floodCts.Token);
        await Task.Delay(100);
        await f.SendAsync(new { type = "getAssessment" });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[256 * 1024];
        JsonElement? reply = null;
        while (reply is null)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await f.Client.ReceiveAsync(buffer, cts.Token);
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            var bytes = ms.ToArray();
            if (Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 24)).StartsWith("{\"type\":\"assessment\"", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(bytes);
                reply = doc.RootElement.Clone();
            }
        }
        floodCts.Cancel();
        await flood;
        Assert.Equal("assessment", reply.Value.GetProperty("type").GetString());
        Assert.False(reply.Value.GetProperty("final").GetBoolean());
    }

    /// <summary>
    /// 送出迴圈被塞住(客戶端不讀、事件框夠大使 socket 傳送緩衝區填滿)時 shutdown 的 ack 送不出去:等 ReplyGrace 後記 log、
    /// RepliesNotSent 計數,後續動作(停止)仍執行;ack 留在回覆通道不丟(只是客戶端此時收不到)。
    /// </summary>
    [Fact]
    public async Task ShutdownWhenSendLoopIsStuck_LogsUnsentAck_StillShutsDown()
    {
        await using var f = await Fixture.StartAsync();
        f.Server.ReplyGrace = TimeSpan.FromMilliseconds(300);
        // 256 個 512 KB 事件框(同一個陣列,通道只存參考)= 128 MB,遠超 loopback 的傳送/接收緩衝區;客戶端不讀 → SendAsync 阻塞
        var big = new byte[512 * 1024];
        Array.Fill(big, (byte)' ');
        var head = Encoding.UTF8.GetBytes("{\"type\":\"event\",\"pad\":\"");
        head.CopyTo(big, 0);
        big[^2] = (byte)'"';
        big[^1] = (byte)'}';
        for (var i = 0; i < 256; i++) f.Server.PublishEvent(big);
        await Task.Delay(300); // 讓送出迴圈卡在某個大框上
        await f.SendAsync(new { type = "shutdown", args = new { reason = "stuck" } });
        for (var i = 0; i < 500 && !f.Shutdown.IsRequested; i++) await Task.Delay(10);
        Assert.True(f.Shutdown.IsRequested, "ack 送不出去時仍應請求停止");
        Assert.Equal("shutdown", f.Shutdown.Reason);
        Assert.Equal(1, Interlocked.Read(ref f.Server.RepliesNotSent));
        lock (f.Log) Assert.Contains(f.Log, m => m.Contains("ack 未能送出") && m.Contains("shutdown"));
    }
}
