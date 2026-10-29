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
/// 事件框({"type":"event",…})走另一條容量 256 的通道(事件爆量時丟最舊),不被狀態幀丟棄(情境引擎設計第 5.2 節)。
/// 該客戶端自己指令的回覆(主控程序層指令的 ack、getAssessment 的 {"type":"assessment",…})走第三條**無界、不丟棄**的回覆通道,
/// 送出順序為回覆 → 事件 → 狀態幀:事件爆量時 shutdown / sessionInfo 的 ack 仍一定送達(第十六輪審查已知限制 2);
/// 回覆只因客戶端自己送指令而產生,數量受其自身速率限制。
/// </summary>
public sealed class WebSocketStateServer : IAsyncDisposable
{
    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public IPEndPoint? Remote { get; init; }
        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        public Channel<byte[]> Events { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        /// <summary>該客戶端指令的回覆(ack / assessment):無界、不丟棄,送出迴圈最優先</summary>
        public Channel<byte[]> Replies { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        /// <summary>送出迴圈實際送出的回覆框數(主控程序層指令回覆後確認 ack 已送出用)</summary>
        public long RepliesSent;
        /// <summary>已入列的回覆數(入列序號;同一客戶端的回覆依序送出,RepliesSent ≥ 序號即該回覆已送出)</summary>
        public long RepliesQueued;
    }

    private readonly HttpListener _listener = new();
    private readonly SimulationEngine _engine;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public int ClientCount => _clients.Count;
    public long CommandsReceived;
    /// <summary>唯讀(重播模式):客戶端送來的指令不進引擎(只記錄並忽略;getAssessment 仍回覆),避免雷達服務/教官站的指令改變重播結果</summary>
    public bool ReadOnly { get; set; }
    public long CommandsIgnored;
    public event Action<string>? Log;
    /// <summary>
    /// 主控程序層指令(sessionInfo、shutdown;HostCommandRouter.Handle):回傳非 null 時把 Reply 當 ack 框送回該客戶端(不進引擎;
    /// 走不丟棄的回覆通道),並在 ack 送出後(最多等 <see cref="ReplyGrace"/>)執行 AfterReply。參數為指令、客戶端端點與是否唯讀(重播)。
    /// </summary>
    public Func<SimCommand, IPEndPoint?, bool, HostCommandResult?>? HostCommandHandler { get; set; }
    /// <summary>AfterReply(shutdown)前等 ack 真的送出的上限;逾時仍執行 AfterReply 並記 log</summary>
    public TimeSpan ReplyGrace { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>逾時未能送出 ack 就執行 AfterReply 的次數(診斷用)</summary>
    public long RepliesNotSent;

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

    /// <summary>推送事件/評估框給所有客戶端(不與狀態幀競爭)。</summary>
    public void PublishEvent(byte[] frame)
    {
        foreach (var c in _clients.Values) c.Events.Writer.TryWrite(frame);
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
        var client = new Client { Socket = socket, Remote = ctx.Request.RemoteEndPoint };
        _clients[id] = client;
        Log?.Invoke($"WebSocket 客戶端連線 {ctx.Request.RemoteEndPoint}(共 {_clients.Count})");

        var sender = SendLoopAsync(client);
        var receiver = ReceiveLoopAsync(client);
        await Task.WhenAny(sender, receiver).ConfigureAwait(false);
        client.Outbox.Writer.TryComplete();
        client.Events.Writer.TryComplete();
        client.Replies.Writer.TryComplete();
        _clients.TryRemove(id, out _);
        // 先等傳送迴圈結束(通道已關閉,送完在途訊息即退出):Windows 原生 WebSocket 不允許 CloseAsync 與 SendAsync 並行
        try { await sender.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { }
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
            while (c.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                // 回覆最優先(不丟、無界),再送事件(不被狀態幀丟棄),最後最新狀態幀;三者皆空時等待任一通道
                if (c.Replies.Reader.TryRead(out var reply))
                {
                    await c.Socket.SendAsync(reply, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref c.RepliesSent);
                    continue;
                }
                if (c.Events.Reader.TryRead(out var ev))
                {
                    await c.Socket.SendAsync(ev, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                if (c.Outbox.Reader.TryRead(out var frame))
                {
                    await c.Socket.SendAsync(frame, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                var waitReply = c.Replies.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var waitState = c.Outbox.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var waitEvent = c.Events.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var done = await Task.WhenAny(waitReply, waitState, waitEvent).ConfigureAwait(false);
                if (!await done.ConfigureAwait(false) && c.Replies.Reader.Completion.IsCompleted && c.Outbox.Reader.Completion.IsCompleted && c.Events.Reader.Completion.IsCompleted) break;
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
                    if (cmd is null) continue;
                    Interlocked.Increment(ref CommandsReceived);
                    if (cmd.Type == SimCommandType.GetAssessment)
                    {
                        // 查詢:直接以 LatestSummary 回覆該客戶端(不進引擎佇列、不寫 input 行;回覆通道不被事件爆量丟掉)
                        var summary = _engine.LatestSummary;
                        c.Replies.Writer.TryWrite(ContractJson.SerializeToUtf8Bytes(new { type = "assessment", final = summary?.Final ?? false, summary }));
                        continue;
                    }
                    if (HostCommandHandler?.Invoke(cmd, c.Remote, ReadOnly) is { } hostResult)
                    {
                        // 主控程序層指令(sessionInfo / shutdown):回 ack 框;shutdown 要等 ack 真的送出再觸發停止(之後 socket 會被 Abort)
                        await ReplyAsync(c, hostResult).ConfigureAwait(false);
                        continue;
                    }
                    if (cmd.Type is SimCommandType.SessionInfo or SimCommandType.Shutdown)
                    {
                        Log?.Invoke($"主控程序未註冊 {cmd.Type} 處理器,忽略");
                        continue;
                    }
                    if (ReadOnly)
                    {
                        if (Interlocked.Increment(ref CommandsIgnored) == 1) Log?.Invoke($"重播模式:忽略客戶端指令 {cmd.Type}(之後不再提示)");
                        continue;
                    }
                    _engine.Enqueue(cmd);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"無法解析指令:{ex.Message}:{text}");
                }
            }
        }
        catch (Exception) { /* 連線中斷或取消 */ }
    }

    /// <summary>
    /// 把回覆放進該客戶端的回覆通道(無界、不丟棄;送出迴圈最優先),有 AfterReply(shutdown)時等送出迴圈實際送出
    /// (最多 <see cref="ReplyGrace"/>)再執行;逾時或客戶端已斷線則記 log 後仍執行(停止不因 ack 送不出去而取消)。
    /// </summary>
    private async Task ReplyAsync(Client c, HostCommandResult result)
    {
        long seq;
        bool queued;
        lock (c.Replies) // 序號與入列同一原子區:之前的回覆(例如 shutdown 前一筆 sessionInfo ack)送出不會被誤判為本回覆已送出
        {
            seq = Interlocked.Increment(ref c.RepliesQueued);
            queued = c.Replies.Writer.TryWrite(ContractJson.SerializeToUtf8Bytes(result.Reply)); // 只在客戶端已離線(通道已完成)時為 false
        }
        if (result.AfterReply is null) return;
        var deadline = DateTime.UtcNow + ReplyGrace;
        while (queued && Interlocked.Read(ref c.RepliesSent) < seq && DateTime.UtcNow < deadline && c.Socket.State == WebSocketState.Open)
            await Task.Delay(10).ConfigureAwait(false);
        if (Interlocked.Read(ref c.RepliesSent) < seq)
        {
            Interlocked.Increment(ref RepliesNotSent);
            Log?.Invoke($"ack 未能送出給 {c.Remote?.ToString() ?? "客戶端"}({(!queued || c.Socket.State != WebSocketState.Open ? "客戶端已離線" : $"{ReplyGrace.TotalMilliseconds:F0} ms 內未送出")}),仍執行後續動作(shutdown)");
        }
        result.AfterReply();
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
