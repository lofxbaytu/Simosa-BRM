using System.Net.WebSockets;
using System.Text;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.Visual.Shared.State;

namespace SimosaBRM.Visual.Shared.Net;

/// <summary>連線狀態(HUD 顯示用)。</summary>
public enum ClientConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// 狀態客戶端:訂閱 Host 的 WebSocket(預設 ws://127.0.0.1:8765),把狀態幀交給 <see cref="StateInterpolator"/>,
/// 斷線後以 0.5 s → 5 s 的退避自動重連;只讀不寫(視景不送指令)。事件框只回報 kind 給 HUD。
/// 執行緒:接收迴圈在背景工作;Push 在鎖內,渲染執行緒以 <see cref="Sample"/> 取樣(同一把鎖)。
/// </summary>
public sealed class StateClient : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly StateInterpolator _interp;
    private readonly Func<double> _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ClientConnectionState Connection { get; private set; } = ClientConnectionState.Disconnected;
    public long FramesReceived { get; private set; }
    public long BadFrames { get; private set; }
    public int Reconnects { get; private set; }
    public string? LastEventKind { get; private set; }
    public string? LastError { get; private set; }
    public double InitialBackoffSeconds { get; init; } = 0.5;
    public double MaxBackoffSeconds { get; init; } = 5.0;
    public event Action<string>? Log;
    public event Action<OwnShipState>? StateReceived;

    /// <param name="clock">牆鐘秒(例如 Stopwatch);與渲染端 Sample 用同一個時鐘</param>
    public StateClient(Uri uri, StateInterpolator interpolator, Func<double> clock)
    {
        _uri = uri;
        _interp = interpolator;
        _clock = clock;
    }

    public static Uri ParseUrl(string url)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) url = "ws://" + url;
        var u = new Uri(url);
        if (u.Scheme is not ("ws" or "wss")) throw new ArgumentException($"WebSocket 位址須為 ws:// 或 wss://:{url}");
        return u;
    }

    public void Start()
    {
        _loop ??= Task.Run(RunAsync);
    }

    /// <summary>渲染執行緒取樣(與接收端共用鎖)。</summary>
    public VisualSample? Sample(double wall)
    {
        lock (_gate) return _interp.Sample(wall);
    }

    public double Rate { get { lock (_gate) return _interp.Rate; } }

    /// <summary>直接餵入一幀(無頭煙霧測試或重播檔餵入;不經網路)。</summary>
    public void Feed(OwnShipState state)
    {
        lock (_gate)
        {
            _interp.Push(state, _clock());
            FramesReceived++;
        }
        StateReceived?.Invoke(state);
    }

    private async Task RunAsync()
    {
        var backoff = InitialBackoffSeconds;
        while (!_cts.IsCancellationRequested)
        {
            Connection = ClientConnectionState.Connecting;
            using var ws = new ClientWebSocket();
            ws.Options.Proxy = null; // 船上離線、教官筆電直連:一律不走系統/環境變數代理
            try
            {
                await ws.ConnectAsync(_uri, _cts.Token).ConfigureAwait(false);
                Connection = ClientConnectionState.Connected;
                backoff = InitialBackoffSeconds;
                Log?.Invoke($"已連線 {_uri}");
                await ReceiveLoopAsync(ws).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log?.Invoke($"連線失敗 {_uri}:{ex.Message}");
            }
            Connection = ClientConnectionState.Disconnected;
            if (_cts.IsCancellationRequested) break;
            Reconnects++;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(MaxBackoffSeconds, backoff * 2);
        }
        Connection = ClientConnectionState.Disconnected;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws)
    {
        var buffer = new byte[256 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Log?.Invoke("伺服器關閉連線");
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", _cts.Token).ConfigureAwait(false); }
                catch (Exception) { /* 對方已走 */ }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            HandleText(text);
        }
    }

    /// <summary>處理一個文字框(公開供測試與重播檔餵入)。</summary>
    public void HandleText(string text)
    {
        switch (StateFrameParser.Classify(text))
        {
            case FrameKind.State:
                var s = StateFrameParser.TryParseState(text);
                if (s is null) { BadFrames++; return; }
                Feed(s);
                break;
            case FrameKind.Event:
                LastEventKind = StateFrameParser.TryGetEventKind(text);
                break;
            case FrameKind.Assessment:
                break;
            default:
                BadFrames++;
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (Exception) { }
        }
        _cts.Dispose();
    }
}
