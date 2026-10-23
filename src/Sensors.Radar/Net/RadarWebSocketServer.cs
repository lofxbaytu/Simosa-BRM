using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace SimosaBRM.Sensors.Radar.Net;

/// <summary>
/// 收到的控制指令(由處理執行緒取出並交給對應的 <see cref="RadarUnit"/>,維持單執行緒確定性)。
/// <paramref name="Disconnected"/> 為客戶端離線通知(同一佇列,處理執行緒據以清理該客戶端的量程設定)。
/// </summary>
/// <para><paramref name="Replayed"/>:主控程序重播紀錄時經 /radar/replay 重放的學員操作(radar-arpa.md 第 5.2 節):套用到雷達但不轉送核心(避免重播中再寫入 input 行),
/// 量程同時套到該雷達所有連線中的顯示客戶端(讓 PPI 跟著學員當時的量程)。</para>
public readonly record struct RadarCommandRequest(string RadarId, Guid ClientId, string Json, bool Disconnected = false, bool Replayed = false);

/// <summary>
/// 雷達 WebSocket 伺服器(System.Net.HttpListener,無額外套件):路徑 /radar/x 與 /radar/s。
/// 送出:二進位影像幀(每客戶端容量 8、慢速客戶端丟最舊幀;依客戶端量程分送,<see cref="SendFrame"/>)與 JSON 文字框(status/tracks/event/ack,容量 256)。
/// 接收:JSON 控制指令(放入 <see cref="Commands"/> 佇列;離線時放入離線通知)。先嘗試所有介面,失敗時退回 localhost(與 SimCore.Host 相同作法)。
/// </summary>
public sealed class RadarWebSocketServer : IAsyncDisposable
{
    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public required string RadarId { get; init; }
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        public Channel<byte[]> Json { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _radars;
    private readonly bool _allInterfaces;
    private Task? _acceptLoop;

    public int Port { get; }
    public string? Prefix { get; private set; }
    public ConcurrentQueue<RadarCommandRequest> Commands { get; } = new();
    public event Action<string>? Log;
    /// <summary>客戶端連線(radarId, clientId);主控程式據以送出 status。</summary>
    public event Action<string, Guid>? ClientConnected;

    public RadarWebSocketServer(int port, IEnumerable<string> radarIds, bool allInterfaces = true)
    {
        Port = port;
        _radars = new HashSet<string>(radarIds, StringComparer.OrdinalIgnoreCase);
        _allInterfaces = allInterfaces;
    }

    public int ClientCount(string radarId) => _clients.Values.Count(c => string.Equals(c.RadarId, radarId, StringComparison.OrdinalIgnoreCase));

    /// <summary>重播接收端路徑(/radar/replay;主控程序 --replay 連線後送 {"type":"radarControlReplay", …})</summary>
    public const string ReplayId = "replay";
    /// <summary>經重播接收端收到並排入佇列的指令數</summary>
    public long ReplayedCommands;

    /// <summary>
    /// 重播訊息 → 指令佇列:{"type":"radarControlReplay","radar":"x","client":"<32 hex>","t":123.4,"request":{"cmd":"range","value":3}}。
    /// client 為紀錄的雷達客戶端識別(Guid "N" 格式;無法解析時以字串雜湊產生固定 Guid),request 為原指令物件(radarControl.args.request 加回 cmd)。
    /// 回傳 false = 格式不符(略過)。
    /// </summary>
    public bool EnqueueReplay(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "radarControlReplay") return false;
            if (!root.TryGetProperty("radar", out var radarEl) || radarEl.GetString() is not { } radar || !_radars.Contains(radar.ToLowerInvariant())) return false;
            if (!root.TryGetProperty("request", out var req) || req.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!req.TryGetProperty("cmd", out _)) return false;
            var clientText = root.TryGetProperty("client", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() ?? "" : "";
            Commands.Enqueue(new RadarCommandRequest(radar.ToLowerInvariant(), ReplayClientId(clientText), req.GetRawText(), Replayed: true));
            Interlocked.Increment(ref ReplayedCommands);
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>紀錄的 client 字串 → Guid(radarControl.args.client 為 Guid "N" 格式;其他字串以 FNV-1a 產生固定 Guid)。</summary>
    public static Guid ReplayClientId(string client)
    {
        if (Guid.TryParseExact(client, "N", out var g)) return g;
        var h1 = 14695981039346656037UL;
        var h2 = 1099511628211UL;
        foreach (var ch in client) { h1 ^= ch; h1 *= 1099511628211UL; h2 ^= ch; h2 *= 14695981039346656037UL; }
        var bytes = new byte[16];
        BitConverter.GetBytes(h1).CopyTo(bytes, 0);
        BitConverter.GetBytes(h2).CopyTo(bytes, 8);
        return new Guid(bytes);
    }

    /// <summary>某台雷達目前連線的客戶端(依連線識別排序,順序確定)。</summary>
    public List<Guid> ClientIds(string radarId)
        => _clients.Where(kv => string.Equals(kv.Value.RadarId, radarId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).OrderBy(g => g).ToList();

    public void Start()
    {
        var wildcard = $"http://*:{Port}/radar/";
        var local = $"http://localhost:{Port}/radar/";
        if (_allInterfaces)
        {
            try
            {
                _listener.Prefixes.Add(wildcard);
                _listener.Start();
                Prefix = wildcard;
            }
            catch (HttpListenerException ex)
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(local);
                _listener.Start();
                Prefix = local;
                Log?.Invoke($"無法監聽 {wildcard}({ex.Message});改為只監聽 {local}。遠端站別需設定 URL ACL:netsh http add urlacl url={wildcard} user=Everyone");
            }
        }
        else
        {
            _listener.Prefixes.Add(local);
            _listener.Start();
            Prefix = local;
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void PublishFrame(string radarId, byte[] frame)
    {
        foreach (var c in _clients.Values)
            if (string.Equals(c.RadarId, radarId, StringComparison.OrdinalIgnoreCase)) c.Frames.Writer.TryWrite(frame);
    }

    public void PublishJson(string radarId, byte[] json)
    {
        foreach (var c in _clients.Values)
            if (string.Equals(c.RadarId, radarId, StringComparison.OrdinalIgnoreCase)) c.Json.Writer.TryWrite(json);
    }

    public void SendJson(Guid clientId, byte[] json)
    {
        if (_clients.TryGetValue(clientId, out var c)) c.Json.Writer.TryWrite(json);
    }

    /// <summary>送影像幀給單一客戶端(每客戶端量程不同時由主控程式分送)。</summary>
    public void SendFrame(Guid clientId, byte[] frame)
    {
        if (_clients.TryGetValue(clientId, out var c)) c.Frames.Writer.TryWrite(frame);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            catch (HttpListenerException) { continue; }
            catch (ObjectDisposedException) { break; }
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var id = path.StartsWith("/radar/", StringComparison.OrdinalIgnoreCase) ? path["/radar/".Length..].ToLowerInvariant() : "";
            var known = _radars.Contains(id) || id == ReplayId;
            if (!known || !ctx.Request.IsWebSocketRequest)
            {
                var body = Encoding.UTF8.GetBytes($"Simosa BRM radar endpoint. WebSocket paths: {string.Join(", ", _radars.Select(r => "/radar/" + r))}, /radar/{ReplayId}\n");
                ctx.Response.StatusCode = known ? 200 : 404;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                try { await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false); } catch (Exception) { }
                ctx.Response.Close();
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(ctx, id));
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, string radarId)
    {
        WebSocket socket;
        try { socket = (await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false)).WebSocket; }
        catch (Exception ex)
        {
            Log?.Invoke($"WebSocket 握手失敗:{ex.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }
        var id = Guid.NewGuid();
        var client = new Client { Socket = socket, RadarId = radarId };
        var replay = radarId == ReplayId;
        _clients[id] = client;
        Log?.Invoke(replay ? $"重播接收端連線 {ctx.Request.RemoteEndPoint}(主控程序 --replay)" : $"雷達 {radarId.ToUpperInvariant()} 客戶端連線 {ctx.Request.RemoteEndPoint}(共 {ClientCount(radarId)})");
        if (!replay) ClientConnected?.Invoke(radarId, id);
        var sender = SendLoopAsync(client);
        var receiver = ReceiveLoopAsync(client, id);
        await Task.WhenAny(sender, receiver).ConfigureAwait(false);
        client.Frames.Writer.TryComplete();
        client.Json.Writer.TryComplete();
        _clients.TryRemove(id, out _);
        if (!replay) Commands.Enqueue(new RadarCommandRequest(radarId, id, "", Disconnected: true));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
        socket.Dispose();
        Log?.Invoke(replay ? "重播接收端離線" : $"雷達 {radarId.ToUpperInvariant()} 客戶端離線(共 {ClientCount(radarId)})");
    }

    private async Task SendLoopAsync(Client c)
    {
        try
        {
            while (c.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                while (c.Json.Reader.TryRead(out var js))
                    await c.Socket.SendAsync(js, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                if (c.Frames.Reader.TryRead(out var frame))
                {
                    await c.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                var a = c.Frames.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var b = c.Json.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var done = await Task.WhenAny(a, b).ConfigureAwait(false);
                if (!await done.ConfigureAwait(false) && c.Frames.Reader.Completion.IsCompleted && c.Json.Reader.Completion.IsCompleted) break;
            }
        }
        catch (Exception) { }
    }

    private async Task ReceiveLoopAsync(Client c, Guid id)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (c.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await c.Socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text && message.Length < 1 << 20)
                {
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    if (c.RadarId == ReplayId)
                    {
                        // 重播接收端:每則訊息回 {"type":"replayAck","ok":…}(主控程序只計數,不等待)
                        var ok = EnqueueReplay(text);
                        c.Json.Writer.TryWrite(Encoding.UTF8.GetBytes(ok ? "{\"type\":\"replayAck\",\"ok\":true}" : "{\"type\":\"replayAck\",\"ok\":false,\"error\":\"格式不符或未知雷達\"}"));
                    }
                    else Commands.Enqueue(new RadarCommandRequest(c.RadarId, id, text));
                }
                message.SetLength(0);
            }
        }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        foreach (var c in _clients.Values)
        {
            try { c.Socket.Abort(); } catch (Exception) { }
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        _listener.Close();
        _cts.Dispose();
    }
}
