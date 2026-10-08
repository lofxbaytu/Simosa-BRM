using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// WebSocket 伺服器(System.Net.HttpListener,無額外套件;與 Python 參考實作相同契約):
/// 每個廣播週期(40 ms)推送 OwnShipState JSON 文字框,接收 SimCommand JSON 文字框送入引擎佇列。
/// 慢速客戶端只會拿到最新一幀(每客戶端容量 1 的通道,舊幀丟棄),不會拖慢引擎。
/// </summary>
public sealed class WebSocketStateServer : IAsyncDisposable
{
    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    private readonly HttpListener _listener = new();
    private readonly SimulationEngine _engine;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public int ClientCount => _clients.Count;
    public long CommandsReceived;
    public event Action<string>? Log;

    public WebSocketStateServer(SimulationEngine engine, int port)
    {
        _engine = engine;
        Port = port;
    }

    /// <summary>實際監聽的前綴(所有介面,或僅 localhost)。</summary>
    public string? Prefix { get; private set; }

    /// <summary>
    /// 先嘗試監聽所有介面(0.0.0.0);Windows 上非管理員且未設定 URL ACL 時 HttpListener 會拒絕萬用前綴,
    /// 此時退回只監聽 localhost 並提示(教官筆電直連需執行
    /// <c>netsh http add urlacl url=http://*:8765/ user=Everyone</c> 或以管理員身分執行一次)。
    /// </summary>
    public void Start()
    {
        var wildcard = $"http://*:{Port}/";
        try
        {
            _listener.Prefixes.Add(wildcard);
            _listener.Start();
            Prefix = wildcard;
        }
        catch (HttpListenerException ex)
        {
            _listener.Prefixes.Clear();
            var local = $"http://localhost:{Port}/";
            _listener.Prefixes.Add(local);
            _listener.Start();
            Prefix = local;
            Log?.Invoke($"無法監聽 {wildcard}({ex.Message});改為只監聽 {local}。遠端站別需設定 URL ACL:netsh http add urlacl url={wildcard} user=Everyone");
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>廣播一幀(引擎執行緒呼叫;非阻塞)。</summary>
    public void Publish(byte[] frame)
    {
        foreach (var c in _clients.Values) c.Outbox.Writer.TryWrite(frame);
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

            if (!ctx.Request.IsWebSocketRequest)
            {
                // 簡單健康檢查
                var body = Encoding.UTF8.GetBytes("Simosa BRM SimCore WebSocket endpoint. Connect with a WebSocket client.\n");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                ctx.Response.Close();
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(ctx));
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        WebSocket socket;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            socket = wsCtx.WebSocket;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WebSocket 握手失敗:{ex.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }

        var id = Guid.NewGuid();
        var client = new Client { Socket = socket };
        _clients[id] = client;
        Log?.Invoke($"WebSocket 客戶端連線 {ctx.Request.RemoteEndPoint}(共 {_clients.Count})");

        var sender = SendLoopAsync(client);
        var receiver = ReceiveLoopAsync(client);
        await Task.WhenAny(sender, receiver).ConfigureAwait(false);
        client.Outbox.Writer.TryComplete();
        _clients.TryRemove(id, out _);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* 對方已斷線 */ }
        socket.Dispose();
        Log?.Invoke($"WebSocket 客戶端離線(共 {_clients.Count})");
    }

    private async Task SendLoopAsync(Client c)
    {
        try
        {
            await foreach (var frame in c.Outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (c.Socket.State != WebSocketState.Open) break;
                await c.Socket.SendAsync(frame, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* 連線中斷或取消 */ }
    }

    private async Task ReceiveLoopAsync(Client c)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (c.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await c.Socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (result.MessageType != WebSocketMessageType.Text) continue;
                try
                {
                    var cmd = SimCommand.Parse(text);
                    if (cmd is not null)
                    {
                        _engine.Enqueue(cmd);
                        Interlocked.Increment(ref CommandsReceived);
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"無法解析指令:{ex.Message}:{text}");
                }
            }
        }
        catch (Exception) { /* 連線中斷或取消 */ }
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
