using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.SimCore.Host;

/// <summary>VHF 角色扮演頻道的紀錄項(通話、文字、DSC;<see cref="VhfRelayServer.Logged"/>),主控程序轉成紀錄 vhf 行與教官站事件框。</summary>
public sealed record VhfLogEntry
{
    /// <summary>vhfCall | vhfText | vhfDsc</summary>
    public required string Kind { get; init; }
    /// <summary>伺服器指派的連線 id</summary>
    public required string Id { get; init; }
    /// <summary>站名(join 的 station;bridge、instructor、VTS…)</summary>
    public required string Station { get; init; }
    /// <summary>頻道(16、12、…;DSC 固定 70)</summary>
    public required string Channel { get; init; }
    /// <summary>通話持續秒數(vhfCall)</summary>
    public double? DurationS { get; init; }
    /// <summary>文字訊息 / DSC 附帶文字</summary>
    public string? Text { get; init; }
    /// <summary>DSC 類別 distress | urgency | safety | routine</summary>
    public string? Category { get; init; }
    /// <summary>通話結束原因(pttEnd | disconnected | timeout;vhfCall)</summary>
    /// <summary>pttEnd | disconnected | timeout | channelChanged</summary>
    public string? EndReason { get; init; }
    public required DateTime Utc { get; init; }

    /// <summary>轉成 SimEvent 形狀(source "vhf"、seq −1、itemId = 頻道、學員站可見);tick/t 為轉送當下的引擎時刻。</summary>
    public SimEvent ToEvent(long tick, double t)
    {
        var message = Kind switch
        {
            VhfRelayServer.KindCall => $"VHF {Channel} {Station} 通話 {DurationS:F1} s",
            VhfRelayServer.KindText => $"VHF {Channel} {Station}:{Text}",
            _ => $"DSC {Category?.ToUpperInvariant()} {Station}{(string.IsNullOrEmpty(Text) ? "" : $":{Text}")}",
        };
        return new SimEvent
        {
            Kind = Kind, Tick = tick, T = t, Source = VhfRelayServer.EventSource, ItemId = Channel, Message = message, Visible = true, Seq = -1,
            Data = SimEvent.DataOf(new { id = Id, station = Station, channel = Channel, duration_s = DurationS, text = Text, category = Category, endReason = EndReason, utc = Utc }),
        };
    }
}

/// <summary>
/// 內建 VHF 角色扮演頻道(需求追溯表 R1「VHF/DSC」、R6「角色扮演」;docs/design/vhf-roleplay.md、src/Contracts/vhf-protocol.md):
/// 獨立的 HttpListener WebSocket(預設 ws://0.0.0.0:8767/),不經核心、不進引擎佇列與狀態雜湊。
/// <list type="bullet">
/// <item>文字框:JSON <c>{type:"vhf", kind:…}</c>。客戶端 → 伺服器:join(station、channel、watch[])、channel、pttStart、pttEnd、text、dsc;
/// 伺服器 → 客戶端:joined(指派 id)、roster、pttStart/pttEnd(含 duration_s)、busy(頻道佔用)、text、dsc、error。</item>
/// <item>二進位框:<c>[uint32 LE id 長度][id UTF-8][audio/webm;codecs=opus 片段]</c>,只在發話者 pttStart 被接受後轉送給同頻道(含雙守聽)的其他站。</item>
/// <item>同頻道同時發話:先到者佔用頻道,後到者收到 busy、音訊不轉送(互相壓制的簡化)。斷線或超過 <see cref="MaxTransmitSeconds"/> 自動結束通話。</item>
/// <item>DSC(distress/urgency/safety/routine)簡化為文字警示,送給所有站(不分頻道,如 DSC 70 守聽)。</item>
/// <item>每次通話結束、文字、DSC 以 <see cref="Logged"/> 通知主控程序寫紀錄 vhf 行(帶外、不進雜湊)與教官站事件框。</item>
/// </list>
/// 慢速客戶端:每站出站通道容量 256、丟最舊(音訊片段 100 ms 一框);不拖慢其他站。
/// </summary>
public sealed class VhfRelayServer : IAsyncDisposable
{
    public const string EventSource = "vhf";
    public const string KindCall = "vhfCall";
    public const string KindText = "vhfText";
    public const string KindDsc = "vhfDsc";
    public const string DscChannel = "70";
    /// <summary>允許的頻道(規劃書第 7.2 節 VHF;頻道旋鈕清單)</summary>
    public static readonly IReadOnlyList<string> Channels = new[] { "16", "12", "13", "14", "06", "08", "10", "67", "68", "69", "71", "72", "73", "77" };
    private static readonly HashSet<string> ChannelSet = new(Channels, StringComparer.Ordinal);
    private static readonly HashSet<string> DscCategories = new(new[] { "distress", "urgency", "safety", "routine" }, StringComparer.Ordinal);

    private sealed class Station
    {
        public required string Id { get; init; }
        public required WebSocket Socket { get; init; }
        public IPEndPoint? Remote { get; init; }
        public string Name = "";
        public bool Joined;
        public string Channel = "16";
        /// <summary>雙守聽的額外頻道(教官站 16 + 工作頻道)</summary>
        public readonly HashSet<string> Watch = new(StringComparer.Ordinal);
        /// <summary>發話中的頻道(null = 未發話)</summary>
        public string? TxChannel;
        public DateTime TxStartedUtc;
        public Channel<(byte[] Data, WebSocketMessageType Type)> Outbox { get; } =
            System.Threading.Channels.Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        public bool Listens(string channel) => Channel == channel || Watch.Contains(channel);
    }

    private sealed class Incoming
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("station")] public string? StationName { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("watch")] public List<string>? Watch { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
    }

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Station> _stations = new();
    // 頻道佔用表與站的頻道/發話狀態:接收迴圈(多執行緒)與逾時檢查共用
    private readonly object _sync = new();
    private readonly Dictionary<string, Station> _occupant = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Task? _watchdog;
    private int _nextId;

    public int Port { get; }
    public string? Prefix { get; private set; }
    public int StationCount => _stations.Count;
    /// <summary>單次通話上限(秒);超過自動結束(卡住的 PTT 不會永久佔用頻道)</summary>
    public double MaxTransmitSeconds { get; set; } = 180;
    /// <summary>轉送的音訊框數(診斷)</summary>
    public long AudioFramesRelayed;
    /// <summary>因未發話/標頭 id 不符而丟棄的音訊框數(診斷)</summary>
    public long AudioFramesDropped;
    public event Action<string>? Log;
    /// <summary>通話結束、文字、DSC(主控程序寫紀錄 vhf 行與教官站事件框)</summary>
    public event Action<VhfLogEntry>? Logged;

    public VhfRelayServer(int port)
    {
        Port = port;
    }

    /// <summary>與 WebSocketStateServer 相同:先試所有介面,Windows 無 URL ACL 時退回 localhost。</summary>
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
        _watchdog = Task.Run(WatchdogAsync);
    }

    // ---------------------------------------------------------------- 連線
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
                var body = Encoding.UTF8.GetBytes("Simosa BRM VHF relay endpoint. Connect with a WebSocket client (see src/Contracts/vhf-protocol.md).\n");
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

    /// <summary>來源過濾(預設 <see cref="HostCommandRouter.IsAllowedShutdownClient"/>:本機或直連私有網段;與 MediaUploadServer 同一信任模型),其餘 403。</summary>
    public Func<IPEndPoint?, bool> ClientFilter { get; set; } = HostCommandRouter.IsAllowedShutdownClient;

    /// <summary>單一 WebSocket 訊息(文字或音訊框)大小上限;超過即以 MessageTooBig 關閉(音訊框正常數 KB、文字 ≤ 500 字)。</summary>
    public const int MaxMessageBytes = 1 << 20;

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        if (!ClientFilter(ctx.Request.RemoteEndPoint))
        {
            Log?.Invoke($"VHF 拒絕來源 {ctx.Request.RemoteEndPoint}(只接受本機或直連網段)");
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }
        WebSocket socket;
        try { socket = (await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false)).WebSocket; }
        catch (Exception ex)
        {
            Log?.Invoke($"VHF 握手失敗:{ex.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }
        var id = $"s{Interlocked.Increment(ref _nextId)}";
        var st = new Station { Id = id, Socket = socket, Remote = ctx.Request.RemoteEndPoint };
        _stations[id] = st;
        Log?.Invoke($"VHF 客戶端連線 {ctx.Request.RemoteEndPoint}(id {id},共 {_stations.Count})");
        var sender = SendLoopAsync(st);
        var receiver = ReceiveLoopAsync(st);
        await Task.WhenAny(sender, receiver).ConfigureAwait(false);
        _stations.TryRemove(id, out _);
        EndTransmit(st, "disconnected");
        st.Outbox.Writer.TryComplete();
        try { await sender.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
        socket.Dispose();
        if (st.Joined) BroadcastRoster();
        Log?.Invoke($"VHF 客戶端離線 {st.Name}(id {id},共 {_stations.Count})");
    }

    private async Task SendLoopAsync(Station st)
    {
        try
        {
            while (await st.Outbox.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (st.Outbox.Reader.TryRead(out var item))
                {
                    if (st.Socket.State != WebSocketState.Open) return;
                    await st.Socket.SendAsync(item.Data, item.Type, true, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) { /* 連線中斷或取消 */ }
    }

    private async Task ReceiveLoopAsync(Station st)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (st.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await st.Socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (message.Length + result.Count > MaxMessageBytes)
                {
                    Log?.Invoke($"VHF {st.Name} 訊息超過 {MaxMessageBytes} bytes,關閉連線");
                    try { await st.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var bytes = message.ToArray();
                message.SetLength(0);
                if (result.MessageType == WebSocketMessageType.Binary) OnAudio(st, bytes);
                else OnText(st, Encoding.UTF8.GetString(bytes));
            }
        }
        catch (Exception) { /* 連線中斷或取消 */ }
    }

    // ---------------------------------------------------------------- 訊息
    private void OnText(Station st, string text)
    {
        Incoming? msg;
        try { msg = JsonSerializer.Deserialize<Incoming>(text, ContractJson.Options); }
        catch (JsonException ex)
        {
            Send(st, new { type = "vhf", kind = "error", message = $"無法解析:{ex.Message}" });
            return;
        }
        if (msg is null || msg.Type != "vhf" || string.IsNullOrEmpty(msg.Kind))
        {
            Send(st, new { type = "vhf", kind = "error", message = "需要 {type:\"vhf\", kind:…}" });
            return;
        }
        if (msg.Kind == "join") { OnJoin(st, msg); return; }
        if (!st.Joined)
        {
            Send(st, new { type = "vhf", kind = "error", message = "尚未 join" });
            return;
        }
        switch (msg.Kind)
        {
            case "channel": OnChannel(st, msg); break;
            case "pttStart": OnPttStart(st, msg.Channel); break;
            case "pttEnd": EndTransmit(st, "pttEnd"); break;
            case "text": OnTextMessage(st, msg); break;
            case "dsc": OnDsc(st, msg); break;
            default: Send(st, new { type = "vhf", kind = "error", message = $"未知的 kind:{msg.Kind}" }); break;
        }
    }

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s.Trim())
        {
            if (sb.Length >= max) break;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString();
    }

    private static string? ValidChannel(string? c) => c is not null && ChannelSet.Contains(c) ? c : null;

    private void OnJoin(Station st, Incoming msg)
    {
        var name = Clean(msg.StationName, 40);
        if (name.Length == 0) name = st.Id;
        lock (_sync)
        {
            st.Name = name;
            st.Joined = true;
            st.Channel = ValidChannel(msg.Channel) ?? "16";
            ApplyWatch(st, msg.Watch);
        }
        Send(st, new { type = "vhf", kind = "joined", id = st.Id, station = st.Name, channel = st.Channel, watch = st.Watch.ToArray(), channels = Channels, stations = Roster() });
        Log?.Invoke($"VHF {st.Name}(id {st.Id})加入,頻道 {st.Channel}{(st.Watch.Count > 0 ? $" + 守聽 {string.Join(",", st.Watch)}" : "")}");
        BroadcastRoster();
    }

    private void ApplyWatch(Station st, List<string>? watch)
    {
        st.Watch.Clear();
        if (watch is null) return;
        foreach (var w in watch)
        {
            var c = ValidChannel(w);
            if (c is not null && c != st.Channel) st.Watch.Add(c);
        }
    }

    private void OnChannel(Station st, Incoming msg)
    {
        var c = ValidChannel(msg.Channel);
        if (c is null && msg.Channel is not null)
        {
            Send(st, new { type = "vhf", kind = "error", message = $"不支援的頻道:{msg.Channel}" });
            return;
        }
        // 換頻道時結束自己的發話(EndTransmit 自己取鎖)
        if (st.TxChannel is not null && c is not null && c != st.TxChannel) EndTransmit(st, "channelChanged");
        lock (_sync)
        {
            if (c is not null) st.Channel = c;
            if (msg.Watch is not null) ApplyWatch(st, msg.Watch);
            else st.Watch.Remove(st.Channel);
        }
        Send(st, new { type = "vhf", kind = "channel", id = st.Id, station = st.Name, channel = st.Channel, watch = st.Watch.ToArray() });
        BroadcastRoster();
    }

    private void OnPttStart(Station st, string? requested)
    {
        string channel;
        Station? occupant;
        lock (_sync)
        {
            channel = ValidChannel(requested) ?? st.Channel;
            if (st.TxChannel is not null) return; // 已在發話(重複的 pttStart 忽略)
            _occupant.TryGetValue(channel, out occupant);
            if (occupant is null || occupant == st)
            {
                _occupant[channel] = st;
                st.TxChannel = channel;
                st.TxStartedUtc = DateTime.UtcNow;
                occupant = null;
            }
        }
        if (occupant is not null)
        {
            // 後發者收到「頻道佔用」:不轉送、不記錄為通話(簡化的互相壓制)
            Send(st, new { type = "vhf", kind = "busy", channel, by = occupant.Id, byStation = occupant.Name });
            Log?.Invoke($"VHF {st.Name} 於頻道 {channel} 發話被拒:{occupant.Name} 佔用中");
            return;
        }
        Relay(channel, st, new { type = "vhf", kind = "pttStart", id = st.Id, station = st.Name, channel });
        BroadcastRoster(); // 不守聽該頻道的站也看得到佔用(頻道鈕 occupied 標示)
    }

    /// <summary>結束發話(pttEnd、斷線、換頻道、逾時):釋放頻道、轉送 pttEnd、記錄通話。未在發話時不動作。</summary>
    private void EndTransmit(Station st, string reason)
    {
        string? channel;
        double duration;
        lock (_sync)
        {
            channel = st.TxChannel;
            if (channel is null) return;
            st.TxChannel = null;
            if (_occupant.TryGetValue(channel, out var occ) && occ == st) _occupant.Remove(channel);
            duration = Math.Max(0, (DateTime.UtcNow - st.TxStartedUtc).TotalSeconds);
        }
        var rounded = Math.Round(duration, 2);
        // 先記錄再轉送:紀錄行的順序與聽者看到的順序一致(聽者收到 pttEnd 後的回覆不會排在這筆通話之前)
        Logged?.Invoke(new VhfLogEntry { Kind = KindCall, Id = st.Id, Station = st.Name, Channel = channel, DurationS = rounded, EndReason = reason, Utc = DateTime.UtcNow });
        Relay(channel, st, new { type = "vhf", kind = "pttEnd", id = st.Id, station = st.Name, channel, duration_s = rounded, reason });
        BroadcastRoster(); // 釋放佔用
    }

    private void OnTextMessage(Station st, Incoming msg)
    {
        var text = Clean(msg.Text, 500);
        if (text.Length == 0) return;
        string channel;
        lock (_sync) channel = ValidChannel(msg.Channel) ?? st.Channel;
        var utc = DateTime.UtcNow;
        var frame = new { type = "vhf", kind = "text", id = st.Id, station = st.Name, channel, text, utc };
        Logged?.Invoke(new VhfLogEntry { Kind = KindText, Id = st.Id, Station = st.Name, Channel = channel, Text = text, Utc = utc });
        Relay(channel, st, frame);
        Send(st, frame); // 回送給發話者(本站訊息列與他站一致,含伺服器時刻)
    }

    private void OnDsc(Station st, Incoming msg)
    {
        var category = (msg.Category ?? "").Trim().ToLowerInvariant();
        if (!DscCategories.Contains(category))
        {
            Send(st, new { type = "vhf", kind = "error", message = $"DSC category 須為 distress|urgency|safety|routine:{msg.Category}" });
            return;
        }
        var text = Clean(msg.Text, 500);
        var utc = DateTime.UtcNow;
        var frame = new { type = "vhf", kind = "dsc", id = st.Id, station = st.Name, channel = DscChannel, category, text, utc };
        Logged?.Invoke(new VhfLogEntry { Kind = KindDsc, Id = st.Id, Station = st.Name, Channel = DscChannel, Category = category, Text = text.Length > 0 ? text : null, Utc = utc });
        // DSC 送給所有已加入的站(含發話者;如 DSC 70 全船守聽),不分頻道
        foreach (var other in _stations.Values) if (other.Joined) Send(other, frame);
        Log?.Invoke($"DSC {category.ToUpperInvariant()} 由 {st.Name}{(text.Length > 0 ? $":{text}" : "")}");
    }

    private void OnAudio(Station st, byte[] bytes)
    {
        string? channel;
        lock (_sync) channel = st.TxChannel;
        if (channel is null || !HeaderMatches(bytes, st.Id))
        {
            Interlocked.Increment(ref AudioFramesDropped);
            return;
        }
        Interlocked.Increment(ref AudioFramesRelayed);
        foreach (var other in _stations.Values)
        {
            if (other == st || !other.Joined) continue;
            bool listens;
            lock (_sync) listens = other.Listens(channel);
            if (listens) other.Outbox.Writer.TryWrite((bytes, WebSocketMessageType.Binary));
        }
    }

    /// <summary>二進位框標頭:<c>[uint32 LE n][id n bytes]</c>;id 須為發話者自己的 id(其他連線的 id 不轉送)。</summary>
    public static bool HeaderMatches(ReadOnlySpan<byte> frame, string id)
    {
        if (frame.Length < 4) return false;
        var n = BinaryPrimitives.ReadUInt32LittleEndian(frame[..4]); // 協定明定 little-endian(vhf-protocol.md 第 1 節)
        if (n == 0 || n > 64 || frame.Length < 4 + n) return false;
        return Encoding.UTF8.GetString(frame.Slice(4, (int)n)) == id;
    }

    /// <summary>組二進位框(客戶端/測試用):<c>[uint32 LE id 長度][id][payload]</c>。</summary>
    public static byte[] EncodeAudioFrame(string id, ReadOnlySpan<byte> payload)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var frame = new byte[4 + idBytes.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)idBytes.Length);
        idBytes.CopyTo(frame, 4);
        payload.CopyTo(frame.AsSpan(4 + idBytes.Length));
        return frame;
    }

    // ---------------------------------------------------------------- 轉送
    private void Relay(string channel, Station from, object frame)
    {
        var bytes = ContractJson.SerializeToUtf8Bytes(frame);
        foreach (var other in _stations.Values)
        {
            if (other == from || !other.Joined) continue;
            bool listens;
            lock (_sync) listens = other.Listens(channel);
            if (listens) other.Outbox.Writer.TryWrite((bytes, WebSocketMessageType.Text));
        }
    }

    private static void Send(Station st, object frame) => st.Outbox.Writer.TryWrite((ContractJson.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text));

    private object[] Roster()
    {
        lock (_sync)
            return _stations.Values.Where(s => s.Joined).OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => (object)new { id = s.Id, station = s.Name, channel = s.Channel, watch = s.Watch.ToArray(), transmitting = s.TxChannel }).ToArray();
    }

    private void BroadcastRoster()
    {
        var bytes = ContractJson.SerializeToUtf8Bytes(new { type = "vhf", kind = "roster", stations = Roster() });
        foreach (var s in _stations.Values) if (s.Joined) s.Outbox.Writer.TryWrite((bytes, WebSocketMessageType.Text));
    }

    /// <summary>每秒檢查:超過上限的發話自動結束(PTT 卡住或客戶端未送 pttEnd 就斷線前)。</summary>
    private async Task WatchdogAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            List<Station> stale;
            lock (_sync)
                stale = _stations.Values.Where(s => s.TxChannel is not null && (DateTime.UtcNow - s.TxStartedUtc).TotalSeconds > MaxTransmitSeconds).ToList();
            foreach (var s in stale)
            {
                Log?.Invoke($"VHF {s.Name} 發話超過 {MaxTransmitSeconds:F0} s,自動結束");
                EndTransmit(s, "timeout");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        foreach (var s in _stations.Values)
        {
            try { s.Socket.Abort(); } catch (Exception) { }
        }
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { } }
        if (_watchdog is not null) { try { await _watchdog.ConfigureAwait(false); } catch (Exception) { } }
        _listener.Close();
        _cts.Dispose();
    }
}
