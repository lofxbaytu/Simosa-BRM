using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Sensors.Radar.Net;

/// <summary>NMEA 輸出設定(TTM/TLL,UDP)。</summary>
public sealed class NmeaOutputOptions
{
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>每台雷達的 UDP 埠(預設 x → 10111、s → 10112;NMEA 閘道真值 TTM 在 10110)</summary>
    public Dictionary<string, int> Ports { get; } = new(StringComparer.OrdinalIgnoreCase) { ["x"] = 10111, ["s"] = 10112 };
    public string Talker { get; set; } = "RA";
}

/// <summary>
/// 雷達服務:把 SimCore 狀態幀交給各台 <see cref="RadarUnit"/>(X、S 平行處理;各台內部單執行緒),
/// 執行佇列中的控制指令、發佈影像幀/追蹤清單/事件/回應到 WebSocket,並以 UDP 送出 TTM/TLL。
/// 指令在狀態幀之間套用(處理執行緒),因此同一指令序列得到相同結果。
/// </summary>
public sealed class RadarService : IDisposable
{
    private List<RadarUnit> _units;
    private readonly RadarWebSocketServer? _server;
    private readonly NmeaOutputOptions? _nmea;
    private readonly UdpClient? _udp;
    private readonly Func<double, DateTime> _utc;
    private readonly Dictionary<string, Stopwatch> _cpu = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>下一次 OnState 必須廣播 status 的雷達(情境切換後;處理執行緒專用)</summary>
    private readonly HashSet<string> _statusPending = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RadarUnit> Units => _units;
    public long StatesProcessed { get; private set; }
    public long NmeaSentencesSent { get; private set; }
    /// <summary>強制產生顯示影像(即使沒有客戶端;測試與效能量測用)</summary>
    public bool ForceDisplay { get; set; }
    /// <summary>每台雷達的事件(主控程式記錄用)</summary>
    public event Action<RadarUnit, Arpa.ArpaEvent>? EventRaised;
    /// <summary>每次輸出追蹤清單後(主控程式比對真值用)</summary>
    public event Action<RadarUnit, RadarScene>? TracksPublished;

    public RadarService(IEnumerable<RadarUnit> units, RadarWebSocketServer? server, NmeaOutputOptions? nmea, Func<double, DateTime>? utc = null)
    {
        _units = units.ToList();
        _server = server;
        _nmea = nmea;
        _udp = nmea is null ? null : new UdpClient();
        _utc = utc ?? (t => DateTime.UnixEpoch.AddSeconds(t));
        foreach (var u in _units) _cpu[u.Config.Id] = new Stopwatch();
        if (_server is not null)
            _server.ClientConnected += (radar, client) => _server.Commands.Enqueue(new RadarCommandRequest(radar, client, "{\"cmd\":\"getStatus\"}"));
    }

    /// <summary>
    /// 換成新的雷達組(情境切換:新種子/新船);沿用舊雷達的操作員設定(量程、增益、雜波、調諧、待機、保護區)。
    /// 試操船、CPA/TCPA 限值回到預設,天線位置可能隨船改變,所以下一次 <see cref="OnState"/> 處理完後
    /// 對每台新雷達廣播 status(客戶端才不會沿用舊 status 的 trial/限值/天線位置)。
    /// 必須在處理執行緒呼叫(OnState 之間)。
    /// </summary>
    public void SetUnits(IEnumerable<RadarUnit> units)
    {
        var next = units.ToList();
        foreach (var u in next)
        {
            if (Find(u.Config.Id) is { } old)
            {
                var json = $"{{\"cmd\":\"range\",\"value\":{old.Controls.RangeNm.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
                u.Apply(json);
                u.Controls.Gain = old.Controls.Gain;
                u.Controls.Sea = old.Controls.Sea;
                u.Controls.Rain = old.Controls.Rain;
                u.Controls.Tune = old.Controls.Tune;
                u.Controls.AutoTune = old.Controls.AutoTune;
                u.Controls.Transmit = old.Controls.Transmit;
                u.Controls.Guard = old.Controls.Guard;
            }
            if (!_cpu.ContainsKey(u.Config.Id)) _cpu[u.Config.Id] = new Stopwatch();
            _statusPending.Add(u.Config.Id);
        }
        _units = next;
    }

    /// <summary>累計處理時間(秒;單台雷達)。</summary>
    public double CpuSeconds(string radarId) => _cpu.TryGetValue(radarId, out var sw) ? sw.Elapsed.TotalSeconds : 0.0;

    public RadarUnit? Find(string id) => _units.FirstOrDefault(u => string.Equals(u.Config.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>處理一個狀態幀(由狀態客戶端的接收執行緒呼叫)。</summary>
    public void OnState(OwnShipState state)
    {
        DrainCommands();
        var scenes = new RadarScene[_units.Count];
        var results = new RadarStepResult[_units.Count];
        for (var i = 0; i < _units.Count; i++)
        {
            scenes[i] = RadarScene.FromState(state, _units[i].Config.Band);
            _units[i].DisplayEnabled = ForceDisplay || (_server?.ClientCount(_units[i].Config.Id) ?? 0) > 0;
        }
        Parallel.For(0, _units.Count, i =>
        {
            var sw = _cpu[_units[i].Config.Id];
            sw.Start();
            results[i] = _units[i].Process(scenes[i]);
            sw.Stop();
        });
        StatesProcessed++;
        for (var i = 0; i < _units.Count; i++) Publish(_units[i], scenes[i], results[i]);
    }

    private void Publish(RadarUnit unit, RadarScene scene, RadarStepResult r)
    {
        var id = unit.Config.Id;
        if (_server is not null)
            foreach (var f in r.Frames) _server.PublishFrame(id, f);
        foreach (var e in r.Events)
        {
            EventRaised?.Invoke(unit, e);
            _server?.PublishJson(id, RadarJson.Utf8(unit.EventMessage(e)));
        }
        if (_statusPending.Remove(id) | r.StatusChanged) _server?.PublishJson(id, RadarJson.Utf8(unit.Status()));
        if (!r.TracksDue) return;
        _server?.PublishJson(id, RadarJson.Utf8(unit.Tracks()));
        TracksPublished?.Invoke(unit, scene);
        if (_udp is null || _nmea is null || !_nmea.Ports.TryGetValue(id, out var port)) return;
        var sentences = ArpaNmea.Sentences(unit, _nmea.Talker, _utc(scene.T));
        if (sentences.Count == 0) return;
        var payload = Encoding.ASCII.GetBytes(string.Concat(sentences.Select(s => s + "\r\n")));
        try
        {
            _udp.Send(payload, payload.Length, _nmea.Host, port);
            NmeaSentencesSent += sentences.Count;
        }
        catch (SocketException) { /* 無接收端時忽略 */ }
    }

    private void DrainCommands()
    {
        if (_server is null) return;
        while (_server.Commands.TryDequeue(out var req))
        {
            var unit = Find(req.RadarId);
            if (unit is null) continue;
            var ack = unit.Apply(req.Json);
            if (ack.Cmd == "getStatus")
            {
                _server.SendJson(req.ClientId, RadarJson.Utf8(unit.Status()));
                continue;
            }
            _server.SendJson(req.ClientId, RadarJson.Utf8(ack));
            if (ack.Cmd == "getTracks") _server.SendJson(req.ClientId, RadarJson.Utf8(unit.Tracks()));
            else if (ack.Ok) _server.PublishJson(unit.Config.Id, RadarJson.Utf8(unit.Status()));
        }
    }

    public void Dispose() => _udp?.Dispose();
}

/// <summary>
/// SimCore.Host 狀態訂閱者(ClientWebSocket,斷線自動重連):每個含 tick 的文字框解析為 <see cref="OwnShipState"/>;
/// 事件框({"type":"event"})與評估框略過。
/// </summary>
public sealed class SimStateClient
{
    private readonly Uri _uri;
    public event Action<string>? Log;
    public long FramesReceived { get; private set; }
    public bool Connected { get; private set; }

    public SimStateClient(Uri uri) => _uri = uri;

    /// <summary>解析狀態框;不是狀態(事件/評估/其他)時回傳 null。</summary>
    public static OwnShipState? ParseState(ReadOnlySpan<byte> utf8)
    {
        // 判斷:第一層有 "tick" 欄位才是狀態幀
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("tick", out _)) return null;
            return doc.RootElement.Deserialize<OwnShipState>(ContractJson.Options);
        }
        catch (JsonException) { return null; }
    }

    public async Task RunAsync(Action<OwnShipState> onState, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        while (!ct.IsCancellationRequested)
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            try
            {
                await ws.ConnectAsync(_uri, ct).ConfigureAwait(false);
                Connected = true;
                Log?.Invoke($"已連線 SimCore 狀態 {_uri}");
                var ms = new MemoryStream();
                while (ws.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, res.Count);
                    if (!res.EndOfMessage) continue;
                    var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Text && ParseState(span) is { } st)
                    {
                        FramesReceived++;
                        try { onState(st); }
                        catch (Exception ex) { Log?.Invoke($"狀態處理錯誤:{ex}"); }
                    }
                    ms.SetLength(0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or HttpRequestException or InvalidOperationException)
            {
                if (Connected) Log?.Invoke($"SimCore 連線中斷:{ex.Message}");
            }
            Connected = false;
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
