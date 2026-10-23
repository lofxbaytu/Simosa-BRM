using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// 重播時把紀錄的 <c>radarControl</c> input 行重放到雷達服務(規劃書第 8.3 節第 7 類;docs/design/radar-arpa.md 第 5.2 節):
/// 連線雷達服務的重播接收端 <c>ws://…:8766/radar/replay</c>,在 <see cref="SimulationEngine.CommandApplied"/> 的相同 tick 送出
/// <c>{"type":"radarControlReplay","radar","client","t","request":{cmd,…}}</c>(request = 紀錄的 args.request 加回 cmd)。
/// 雷達服務套用但不轉送核心;無連線時主控程序略過並記 note。送出為非阻塞(佇列),回覆只計數。
/// </summary>
public sealed class RadarReplayClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly System.Threading.Channels.Channel<byte[]> _outbox =
        System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _sendLoop;
    private Task? _recvLoop;

    public Uri Uri { get; }
    public bool Connected => _ws.State == WebSocketState.Open;
    /// <summary>已排入佇列的訊息數(Unbounded channel 不支援 Count,自行計數)</summary>
    public long Queued => Interlocked.Read(ref _queued);
    private long _queued;
    public long Sent { get; private set; }
    public long AcksOk { get; private set; }
    public long AcksFailed { get; private set; }

    /// <param name="baseUrl">雷達服務根 URL(例如 ws://127.0.0.1:8766/);接收端路徑固定為 radar/replay</param>
    public RadarReplayClient(string baseUrl)
    {
        var b = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        Uri = new Uri(new Uri(b), "radar/replay");
    }

    /// <summary>嘗試連線(逾時或拒絕時回傳 false,不丟例外)。</summary>
    public async Task<bool> TryConnectAsync(TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(timeout);
        try
        {
            await _ws.ConnectAsync(Uri, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
        _sendLoop = Task.Run(SendLoopAsync);
        _recvLoop = Task.Run(RecvLoopAsync);
        return true;
    }

    /// <summary>紀錄的 radarControl 指令 → 重播訊息(不是 radarControl 或缺 radar/request 時回傳 false)。任何執行緒可呼叫,非阻塞。</summary>
    public bool Send(SimCommand cmd, double t)
    {
        if (cmd.Type != SimCommandType.RadarControl || !Connected) return false;
        var radar = cmd.ArgAsString("radar");
        var name = cmd.ArgAsString("cmd");
        if (radar is null || name is null) return false;
        var request = new Dictionary<string, object?> { ["cmd"] = name };
        if (cmd.Arg("request") is { ValueKind: JsonValueKind.Object } req)
            foreach (var p in req.EnumerateObject()) request[p.Name] = p.Value.Clone();
        var msg = new { type = "radarControlReplay", radar, client = cmd.ArgAsString("client") ?? "", t, request };
        if (_outbox.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(msg, ContractJson.Options))) Interlocked.Increment(ref _queued);
        return true;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                while (_outbox.Reader.TryRead(out var json))
                {
                    await _ws.SendAsync(json, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                    Sent++;
                }
        }
        catch (Exception) { /* 取消或連線中斷 */ }
    }

    private async Task RecvLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                ms.SetLength(0);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) AcksOk++; else AcksFailed++;
                }
                catch (JsonException) { AcksFailed++; }
            }
        }
        catch (Exception) { }
    }

    /// <summary>等待佇列送完(最多 <paramref name="timeout"/>)。</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Sent < Queued && DateTime.UtcNow < deadline && Connected) await Task.Delay(20).ConfigureAwait(false);
        var ackDeadline = DateTime.UtcNow + timeout;
        while (AcksOk + AcksFailed < Sent && DateTime.UtcNow < ackDeadline && Connected) await Task.Delay(20).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        try
        {
            if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
        _cts.Cancel();
        if (_sendLoop is not null) { try { await _sendLoop.ConfigureAwait(false); } catch (Exception) { } }
        if (_recvLoop is not null) { try { await _recvLoop.ConfigureAwait(false); } catch (Exception) { } }
        _ws.Dispose();
        _cts.Dispose();
    }
}
