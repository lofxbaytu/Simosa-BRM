using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.ConsoleSerial.Bridge;

/// <summary>核心連結(可替換:測試用假核心)。</summary>
public interface ICoreLink : IAsyncDisposable
{
    bool Connected { get; }
    event Action<bool>? ConnectionChanged;
    /// <summary>收到的文字框(狀態幀、事件、ack;由 <see cref="SimosaBRM.ConsoleSerial.Core.ConsoleStateView.TryParse"/> 分辨)</summary>
    event Action<string>? FrameReceived;
    /// <summary>送指令(斷線時暫存,最多 256 筆、滿時丟最舊)</summary>
    void Send(SimCommand command);
    void Start();
}

/// <summary>
/// SimCore.Host 狀態/指令 WebSocket 客戶端(console-serial.md 第 5 節):接收狀態幀、送 SimCommand JSON;
/// 斷線時指令暫存(有界 256、丟最舊)、每秒重連;連線狀態以 <see cref="ConnectionChanged"/> 通知(LED,LINK)。不走代理。
/// </summary>
public sealed class CoreLink : ICoreLink
{
    public const int QueueCapacity = 256;
    private readonly Uri _uri;
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public CoreLink(Uri uri)
    {
        _uri = uri;
    }

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public bool Connected { get; private set; }
    public long FramesReceived { get; private set; }
    public long CommandsSent { get; private set; }
    public int Reconnects { get; private set; }
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? FrameReceived;
    public event Action<string>? Log;

    public void Start() => _loop ??= Task.Run(RunAsync);

    public void Send(SimCommand command) => _outbox.Writer.TryWrite(ContractJson.SerializeToUtf8Bytes(command));

    private async Task RunAsync()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.Proxy = null; // 船上離線、教官筆電直連:不走系統/環境變數代理
            try
            {
                await ws.ConnectAsync(_uri, _cts.Token).ConfigureAwait(false);
                SetConnected(true);
                if (!first) Reconnects++;
                first = false;
                Log?.Invoke($"已連線核心 {_uri}");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var send = SendLoopAsync(ws, linked.Token);
                var recv = ReceiveLoopAsync(ws, linked.Token);
                await Task.WhenAny(send, recv).ConfigureAwait(false);
                linked.Cancel();
                try { await Task.WhenAll(send, recv).ConfigureAwait(false); } catch (Exception) { }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (first || Connected) Log?.Invoke($"核心連線失敗 {_uri}:{ex.Message}(每 {ReconnectDelay.TotalSeconds:0.#} s 重試)");
                first = false;
            }
            SetConnected(false);
            if (_cts.IsCancellationRequested) break;
            try { await Task.Delay(ReconnectDelay, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        SetConnected(false);
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        ConnectionChanged?.Invoke(value);
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reader = _outbox.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryPeek(out var frame))
            {
                await ws.SendAsync(frame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                reader.TryRead(out _);   // 送出成功才出列(送失敗時保留,重連後再送)
                CommandsSent++;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                Log?.Invoke("核心關閉連線");
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false); } catch (Exception) { }
                return;
            }
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            FramesReceived++;
            FrameReceived?.Invoke(text);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (Exception) { } }
        _cts.Dispose();
    }
}
