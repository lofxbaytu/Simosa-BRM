using System.Text.Json;
using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.Sensors.Radar.Video;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.Sensors.Radar;

/// <summary>一次 <see cref="RadarUnit.Process(RadarScene)"/> 的輸出。</summary>
public sealed class RadarStepResult
{
    /// <summary>影像幀(<see cref="SpokeFrame"/> 二進位格式;顯示關閉或未發射時為空)</summary>
    public List<byte[]> Frames { get; } = new();
    public List<ArpaEvent> Events { get; } = new();
    /// <summary>本幀到了追蹤清單輸出時刻(1 Hz 模擬時間)</summary>
    public bool TracksDue { get; set; }
    /// <summary>狀態改變(故障/待機/航向有效性),應重送 status</summary>
    public bool StatusChanged { get; set; }
    public int SpokesGenerated { get; set; }
}

/// <summary>
/// 一台雷達(X 或 S):天線依模擬時間旋轉(24 rpm、每圈 2048 方位線),每個狀態幀補齊應掃過的方位線,
/// 每條線產生 ARPA 處理影像(固定 16 nm)與各顯示量程的影像,ARPA 影像經叢集偵測送入追蹤器。
/// 顯示量程為每客戶端獨立(<see cref="SetClientRange"/>;radar-arpa.md 第 4.1 節):每個相異量程只產生一份影像,
/// 顯示格不細於 ARPA 格的量程(6、12 nm)由 ARPA 影像重取樣,其餘(短量程需短脈衝、長量程超出 ARPA 量程)各自繪製。
/// 天線方位由模擬時間決定、亂數由種子決定 → 同種子同狀態序列得到相同影像與追蹤結果(確定性;與連線的客戶端無關)。
/// 非執行緒安全:主控程式以單一執行緒(或鎖)呼叫 Process 與 Apply。
/// </summary>
public sealed class RadarUnit
{
    /// <summary>一個顯示量程的影像狀態(格表與累積中的幀)</summary>
    private sealed class DisplayView
    {
        public required double RangeNm { get; init; }
        public required VideoGrid Grid { get; init; }
        /// <summary>由 ARPA 影像重取樣(不另繪製)</summary>
        public required bool Resample { get; init; }
        public byte[]? Frame;
        public int FrameSpokes, FrameFirst;
    }

    private readonly ulong _seed;
    private readonly NoiseTables _noise;
    private readonly SceneGeometry _geo;
    private readonly SpokeGeometry _sg = new();
    private readonly SpokeRenderer _renderer = new();
    private readonly VideoGrid _arpaGrid;
    private readonly BlobDetector _detector;
    private readonly byte[] _arpaLine;
    private readonly List<Detection> _detections = new();
    /// <summary>各量程的顯示影像狀態(鍵 = 量程 nm;未用的量程保留,換回時不必重建格表)</summary>
    private readonly Dictionary<double, DisplayView> _displays = new();
    private readonly List<DisplayView> _activeViews = new();
    /// <summary>每客戶端的顯示量程(nm;未設定的客戶端用 <see cref="RadarControls.RangeNm"/>)</summary>
    private readonly Dictionary<Guid, double> _clientRanges = new();
    private long _spokeCount = -1;
    private double _lastT = double.NaN;
    private double _nextTracksT;
    private string? _scenarioId;
    private bool _lastFailed, _lastDegraded, _lastHeadingValid = true;

    public RadarConfig Config { get; }
    public RadarControls Controls { get; } = new();
    public RadarEnvironment Environment { get; }
    public ArpaTracker Arpa { get; }
    /// <summary>是否產生顯示影像(無客戶端時可關閉以節省 CPU;ARPA 不受影響)</summary>
    public bool DisplayEnabled { get; set; } = true;
    /// <summary>
    /// 本幀要產生影像的顯示量程(nm;主控程式依連線客戶端設定,null = 只用預設量程 <see cref="RadarControls.RangeNm"/>)。
    /// 相異量程各一份影像;幀標頭 range_m 標示量程,由主控程式依客戶端量程分送。
    /// </summary>
    public IReadOnlyCollection<double>? DisplayRanges { get; set; }
    public RadarScene? LastScene { get; private set; }
    public bool Failed { get; private set; }
    public bool Degraded { get; private set; }
    public bool Transmitting => Controls.Transmit && !Failed;
    public long SpokeCount => _spokeCount;
    public long Scan => _spokeCount < 0 ? 0 : _spokeCount / Config.Spokes;
    /// <summary>預設量程的顯示格表(測試用)</summary>
    public VideoGrid DisplayGrid => ViewFor(Controls.RangeNm).Grid;
    public VideoGrid ArpaGrid => _arpaGrid;
    public double SeaState { get; private set; }
    /// <summary>每個 ARPA 偵測(測試與診斷用)</summary>
    public event Action<Detection>? DetectionObserved;
    /// <summary>累計產生方位線數與耗時(效能量測)</summary>
    public long TotalSpokes { get; private set; }
    /// <summary>最近一幀實際產生影像的量程數(效能診斷)</summary>
    public int ActiveDisplayCount => _activeViews.Count;
    /// <summary>目前有獨立量程設定的客戶端</summary>
    public IReadOnlyDictionary<Guid, double> ClientRanges => _clientRanges;

    public RadarUnit(RadarConfig config, ulong seed, RadarEnvironment? environment = null)
    {
        Config = config;
        _seed = Hash.Mix(seed ^ (config.Band == RadarBand.X ? 0x5852UL : 0x5352UL) * Hash.Golden);
        _noise = new NoiseTables(_seed);
        Environment = environment ?? RadarEnvironment.Empty();
        _geo = new SceneGeometry(config);
        _arpaGrid = new VideoGrid(config, config.ArpaRangeNm * Units.MetresPerNauticalMile, config.ArpaCells, config.ArpaPulseM);
        _detector = new BlobDetector(config, _arpaGrid);
        _arpaLine = new byte[config.ArpaCells];
        Arpa = new ArpaTracker(config, _arpaGrid.CellM);
    }

    public char RadarLetter => Config.Band == RadarBand.X ? 'X' : 'S';

    /// <summary>
    /// 量程是否由 ARPA 影像重取樣:量程不超過 ARPA 量程且顯示格不細於 ARPA 格(預設 512 格 vs 16 nm/2048 格 → 6、12 nm)。
    /// 更短的量程需要短脈衝解析度、更長的量程超出 ARPA 影像,各自繪製。
    /// </summary>
    public bool ResamplesFromArpa(double rangeNm)
        => rangeNm <= Config.ArpaRangeNm + 1e-9 && rangeNm * Units.MetresPerNauticalMile / Config.DisplayCells >= _arpaGrid.CellM - 1e-9;

    /// <summary>
    /// 顯示量程實際使用的脈衝:由 ARPA 影像重取樣的量程(<see cref="ResamplesFromArpa"/>,預設 6、12 nm)回波以 ARPA 處理脈衝
    /// (<see cref="RadarConfig.ArpaPulseM"/>,中脈衝 45 m)繪製,<see cref="Status"/> 回報同一值使 pulseLength_m 與影像的回波長度一致;其餘量程依 <see cref="RadarControls.PulseFor"/>。
    /// </summary>
    public (PulseLength Pulse, double PulseM) DisplayPulseFor(double rangeNm)
        => ResamplesFromArpa(rangeNm) ? (PulseLength.Medium, Config.ArpaPulseM) : RadarControls.PulseFor(rangeNm);

    private DisplayView ViewFor(double rangeNm)
    {
        if (_displays.TryGetValue(rangeNm, out var v)) return v;
        v = new DisplayView
        {
            RangeNm = rangeNm,
            Grid = new VideoGrid(Config, rangeNm * Units.MetresPerNauticalMile, Config.DisplayCells, DisplayPulseFor(rangeNm).PulseM),
            Resample = ResamplesFromArpa(rangeNm),
        };
        _displays[rangeNm] = v;
        return v;
    }

    /// <summary>設定某客戶端的顯示量程(nm;已換成標準量程)。</summary>
    public void SetClientRange(Guid client, double rangeNm) => _clientRanges[client] = rangeNm;

    /// <summary>客戶端離線:移除其量程設定。</summary>
    public void RemoveClient(Guid client) => _clientRanges.Remove(client);

    /// <summary>客戶端的顯示量程(未設定或無客戶端時為預設量程)。</summary>
    public double RangeFor(Guid? client) => client is { } c && _clientRanges.TryGetValue(c, out var r) ? r : Controls.RangeNm;

    /// <summary>情境切換時沿用舊雷達的每客戶端量程。</summary>
    public void CopyClientRangesFrom(RadarUnit old)
    {
        _clientRanges.Clear();
        foreach (var (k, v) in old._clientRanges) _clientRanges[k] = v;
    }

    public RadarStepResult Process(OwnShipState state) => Process(RadarScene.FromState(state, Config.Band));

    public RadarStepResult Process(RadarScene scene)
    {
        var result = new RadarStepResult();
        var rate = Config.Rpm / 60.0 * Config.Spokes;
        var target = (long)Math.Floor(scene.T * rate + 1e-9);
        var reset = _spokeCount < 0 || scene.T < _lastT - 1e-6 || !string.Equals(scene.ScenarioId, _scenarioId, StringComparison.Ordinal);
        if (reset)
        {
            if (_spokeCount >= 0) Arpa.CancelAll(scene.T, "模擬時間倒退或情境切換");
            _detector.Reset();
            Environment.Unanchor();
            _spokeCount = target;
            _nextTracksT = scene.T;
            _scenarioId = scene.ScenarioId;
        }
        _lastT = scene.T;
        LastScene = scene;
        Environment.Anchor(scene.IndLat, scene.IndLon, scene.IndX, scene.IndY);
        // 核心只在關鍵幀(載入/重設/倒帶後第一幀與每秒一次)帶出 state.radarEnvironment:省略 = 不變;
        // 時間倒退或情境切換(reset)時以該幀內容重設(無則清除,待下一個關鍵幀重新套用)
        if (reset || scene.RadarEnvironment is not null) Environment.ApplyScenario(scene.RadarEnvironment);

        Failed = Config.FailureFaults.Any(f => scene.Faults.Contains(f));
        Degraded = Config.DegradedFaults.Any(f => scene.Faults.Contains(f));
        if (Failed != _lastFailed || Degraded != _lastDegraded || scene.HeadingValid != _lastHeadingValid)
        {
            result.StatusChanged = true;
            if (Failed && !_lastFailed) result.Events.Add(new ArpaEvent("radarFailure", scene.T, null, $"{RadarLetter} 頻雷達失效(停止發射)"));
            if (!Failed && _lastFailed) result.Events.Add(new ArpaEvent("radarRestored", scene.T, null, $"{RadarLetter} 頻雷達恢復發射"));
            if (Degraded && !_lastDegraded) result.Events.Add(new ArpaEvent("radarDegraded", scene.T, null, $"{RadarLetter} 頻雷達性能劣化(靈敏度 −{Config.DegradedLossDb:F0} dB)"));
            if (!scene.HeadingValid && _lastHeadingValid) result.Events.Add(new ArpaEvent("headingLost", scene.T, null, "航向輸入無效:ARPA 無法穩定化"));
            _lastFailed = Failed;
            _lastDegraded = Degraded;
            _lastHeadingValid = scene.HeadingValid;
        }

        var hInd = Units.DegToRad(scene.IndHeadingDeg);
        Arpa.SetOwn(new OwnShipFix(scene.T, scene.IndX, scene.IndY, scene.IndLat, scene.IndLon, scene.IndHeadingDeg, scene.IndCogDeg, scene.IndSogKn));

        if (target - _spokeCount > Config.Spokes) _spokeCount = target - Config.Spokes; // 時間跳躍(高倍率)最多補一圈
        if (!Transmitting)
        {
            _spokeCount = Math.Max(_spokeCount, target);
            _detector.Reset();
        }
        else if (target > _spokeCount)
        {
            GenerateSpokes(scene, target, hInd, result);
        }

        Arpa.Maintain(scene.T, Controls.Trial);
        if (scene.T + 1e-9 >= _nextTracksT)
        {
            result.TracksDue = true;
            _nextTracksT = Math.Max(_nextTracksT + Config.TrackIntervalS, scene.T + 0.5 * Config.TrackIntervalS);
            // AIS 融合與追蹤清單同週期(1 Hz);報告順序同 state.targets[](確定性)
            Arpa.AssociateAis(scene.T, scene.AisReports);
        }
        result.Events.AddRange(Arpa.DrainEvents());
        return result;
    }

    /// <summary>本幀要產生影像的量程清單(相異、依量程排序;顯示關閉時空)。</summary>
    private void SelectViews()
    {
        _activeViews.Clear();
        if (!DisplayEnabled) return;
        if (DisplayRanges is { Count: > 0 } ranges)
        {
            foreach (var r in ranges.Distinct().OrderBy(x => x)) _activeViews.Add(ViewFor(r));
        }
        else _activeViews.Add(ViewFor(Controls.RangeNm));
    }

    private void GenerateSpokes(RadarScene scene, long target, double hInd, RadarStepResult result)
    {
        var seaState = Environment.EffectiveSeaState ?? VideoGrid.SeaStateFromWind(scene.WindSpeedKn);
        SeaState = seaState;
        _arpaGrid.UpdateClutter(seaState);
        _arpaGrid.UpdateStc(Controls.Sea);
        SelectViews();
        var maxRangeM = _arpaGrid.RangeM;
        foreach (var v in _activeViews)
        {
            v.Frame = null;
            if (v.Resample) continue;
            v.Grid.UpdateClutter(seaState);
            v.Grid.UpdateStc(Controls.Sea);
            if (v.Grid.RangeM > maxRangeM) maxRangeM = v.Grid.RangeM;
        }
        var lossDb = Controls.TuneLossDb + (Degraded ? Config.DegradedLossDb : 0.0);
        var rp = new ReceiverParams(Math.Pow(10.0, -lossDb / 10.0), Controls.GainThresholdDb, Math.Clamp(Controls.Rain, 0, 100) / 100.0 * 0.9);
        var rpArpa = rp with { Scale = (float)(255.0 / Config.Arpa.DynamicRangeDb) };
        // 重取樣的強度換算:ARPA 影像 0–DynamicRange dB → 顯示 0–30 dB(ReceiverParams.Scale 預設 255/30)
        var resampleGain = (float)(Config.Arpa.DynamicRangeDb / 30.0);
        _geo.Prepare(scene, Environment, maxRangeM);

        // ARPA 穩定化:以指示位置與指示航向推得天線位置(CCRP = 船舶參考點)
        var sinI = Math.Sin(hInd);
        var cosI = Math.Cos(hInd);
        var antX = scene.IndX + Config.AntennaFwdM * sinI + Config.AntennaStbdM * cosI;
        var antY = scene.IndY + Config.AntennaFwdM * cosI - Config.AntennaStbdM * sinI;
        var arpaActive = scene.HeadingValid;
        if (!arpaActive) _detector.Reset();

        var count = (int)(target - _spokeCount);
        var cells = Config.DisplayCells;
        var flags = SpokeFrame.FrameFlags.Transmitting | (scene.HeadingValid ? SpokeFrame.FrameFlags.HeadingValid : 0) | (Degraded ? SpokeFrame.FrameFlags.Degraded : 0);
        for (var c = _spokeCount; c < target; c++)
        {
            var k = (int)(c % Config.Spokes);
            _geo.Compute(k, _sg);
            var spokeBase = Hash.Mix(_seed ^ ((ulong)c * 0xD1B54A32D192ED03UL));
            _renderer.Render(_arpaGrid, _sg, rpArpa, spokeBase, _noise, _arpaLine);
            if (arpaActive)
            {
                _detections.Clear();
                _detector.Process(c, k, _arpaLine, scene.T, antX, antY, hInd, _detections);
                foreach (var d in _detections)
                {
                    DetectionObserved?.Invoke(d);
                    Arpa.OnDetection(d, Controls);
                }
            }
            foreach (var v in _activeViews)
            {
                if (v.Frame is null)
                {
                    var n = Math.Min(Config.BatchSpokes, (int)(target - c));
                    n = Math.Min(n, Config.Spokes - k); // 幀不跨 2047→0
                    v.Frame = new byte[SpokeFrame.HeaderBytes + n * cells];
                    v.FrameSpokes = n;
                    v.FrameFirst = k;
                    SpokeFrame.WriteHeader(v.Frame, RadarLetter, flags, (uint)(c / Config.Spokes), k, n, Config.Spokes, cells,
                        (float)v.Grid.RangeM, (float)scene.IndHeadingDeg, (float)scene.IndCogDeg, scene.T);
                }
                var j = k - v.FrameFirst;
                var line = v.Frame.AsSpan(SpokeFrame.HeaderBytes + j * cells, cells);
                if (v.Resample) ResampleLine(_arpaLine, _arpaGrid.CellM, line, v.Grid.CellM, resampleGain);
                else _renderer.Render(v.Grid, _sg, rp, spokeBase ^ 0xA5A5A5A5A5A5A5A5UL, _noise, line);
                if (j + 1 == v.FrameSpokes)
                {
                    result.Frames.Add(v.Frame);
                    v.Frame = null;
                }
            }
        }
        result.SpokesGenerated = count;
        TotalSpokes += count;
        _spokeCount = target;
    }

    /// <summary>
    /// 把 ARPA 影像線重取樣到顯示量程(最近格;顯示格 ≥ ARPA 格,不會漏掉回波),強度乘 <paramref name="gain"/> 換成顯示刻度後飽和於 255。
    /// </summary>
    internal static void ResampleLine(ReadOnlySpan<byte> src, double srcCellM, Span<byte> dst, double dstCellM, float gain)
    {
        var ratio = dstCellM / srcCellM;
        for (var i = 0; i < dst.Length; i++)
        {
            var j = (int)((i + 0.5) * ratio);
            if (j >= src.Length) { dst[i] = 0; continue; }
            var v = src[j] * gain;
            dst[i] = v >= 255f ? (byte)255 : (byte)(v + 0.5f);
        }
    }

    // ------------------------------------------------------------------ 控制指令

    /// <summary>
    /// 執行一個 JSON 控制指令(radar.schema.json <c>$defs/command</c>):
    /// range、gain、sea、rain、tune、standby、transmit、acquire、cancel、guardZone、trialManoeuvre、cpaLimits、environment、getStatus、getTracks。
    /// </summary>
    /// <param name="client">發出指令的客戶端:range 只改該客戶端的顯示量程;null(無客戶端:測試、主控程式預設)改預設量程</param>
    public AckMessage Apply(string json, Guid? client = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return Ack("?", false, $"JSON 解析失敗:{ex.Message}"); }
        using (doc) return Apply(doc.RootElement, client);
    }

    public AckMessage Apply(JsonElement root, Guid? client = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return Ack("?", false, "指令須為 JSON 物件");
        var cmd = Str(root, "cmd") ?? Str(root, "type") ?? "";
        JsonElement? reqId = root.TryGetProperty("reqId", out var rid) ? rid.Clone() : null;
        var t = LastScene?.T ?? 0.0;
        try
        {
            switch (cmd)
            {
                case "range":
                {
                    var v = Num(root, "value") ?? throw new ArgumentException("range 需要 value(nm)");
                    if (v <= 0 || v > 96) throw new ArgumentException("量程須在 0–96 nm");
                    var snapped = RadarControls.StandardRanges.OrderBy(r => Math.Abs(Math.Log(r / v))).First();
                    if (client is { } cl) _clientRanges[cl] = snapped;
                    else Controls.RangeNm = snapped;
                    return Ack(cmd, true, null, reqId, value: snapped);
                }
                case "gain": Controls.Gain = Pct(root); return Ack(cmd, true, null, reqId, value: Controls.Gain);
                case "sea": Controls.Sea = Pct(root); return Ack(cmd, true, null, reqId, value: Controls.Sea);
                case "rain": Controls.Rain = Pct(root); return Ack(cmd, true, null, reqId, value: Controls.Rain);
                case "tune":
                    if (Bool(root, "auto") is { } auto) Controls.AutoTune = auto;
                    if (Num(root, "value") is { } tv) { Controls.Tune = Math.Clamp(tv, 0, 100); Controls.AutoTune = Bool(root, "auto") ?? false; }
                    return Ack(cmd, true, null, reqId, value: Controls.Tune);
                case "standby":
                    Controls.Transmit = false;
                    Arpa.CancelAll(t, "待機");
                    _detector.Reset();
                    return Ack(cmd, true, null, reqId);
                case "transmit":
                    Controls.Transmit = true;
                    return Ack(cmd, true, null, reqId);
                case "acquire":
                {
                    if (!Transmitting) throw new InvalidOperationException("雷達未發射");
                    var b = Num(root, "bearing") ?? throw new ArgumentException("acquire 需要 bearing(度)");
                    var r = Num(root, "range") ?? throw new ArgumentException("acquire 需要 range(nm)");
                    if (Bool(root, "relative") == true) b += LastScene?.IndHeadingDeg ?? 0.0;
                    var (id, err) = Arpa.AcquireManual(t, Units.NormalizeHeadingDeg(b), r);
                    return Ack(cmd, id is not null, err, reqId, id: id);
                }
                case "cancel":
                {
                    if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() == "all")
                    {
                        Arpa.CancelAll(t, "操作員");
                        return Ack(cmd, true, null, reqId);
                    }
                    var id = (int)(Num(root, "id") ?? throw new ArgumentException("cancel 需要 id(目標編號或 \"all\")"));
                    var ok = Arpa.Cancel(id, t);
                    return Ack(cmd, ok, ok ? null : $"無目標 {id}", reqId, id: id);
                }
                case "guardZone":
                {
                    var g = Controls.Guard;
                    var inner = Num(root, "inner_nm") ?? g.InnerNm;
                    var outer = Num(root, "outer_nm") ?? g.OuterNm;
                    if (inner < 0 || outer <= inner) throw new ArgumentException("guardZone 需 0 ≤ inner_nm < outer_nm");
                    Controls.Guard = new GuardZone(Bool(root, "enabled") ?? true, inner, outer,
                        Num(root, "from") ?? g.FromDeg, Num(root, "to") ?? g.ToDeg, Bool(root, "relative") ?? g.Relative);
                    return Ack(cmd, true, null, reqId);
                }
                case "trialManoeuvre":
                {
                    if (Bool(root, "active") == false) { Controls.Trial = null; return Ack(cmd, true, null, reqId); }
                    var course = Num(root, "course") ?? throw new ArgumentException("trialManoeuvre 需要 course(度)");
                    var speed = Num(root, "speed") ?? throw new ArgumentException("trialManoeuvre 需要 speed(kn)");
                    var delay = Num(root, "delay") ?? 0.0;
                    if (speed < 0 || delay < 0) throw new ArgumentException("speed/delay 不可為負");
                    Controls.Trial = new TrialManoeuvre(Units.NormalizeHeadingDeg(course), speed, delay);
                    return Ack(cmd, true, null, reqId);
                }
                case "cpaLimits":
                    Arpa.CpaLimitNm = Num(root, "cpa_nm") ?? Arpa.CpaLimitNm;
                    Arpa.TcpaLimitMin = Num(root, "tcpa_min") ?? Arpa.TcpaLimitMin;
                    return Ack(cmd, true, null, reqId);
                case "environment":
                {
                    if (root.TryGetProperty("seaState", out var ss))
                        Environment.SetSeaState(ss.ValueKind == JsonValueKind.Number ? Math.Clamp(ss.GetDouble(), 0, 9) : null);
                    if (root.TryGetProperty("rainCells", out var rc) && rc.ValueKind == JsonValueKind.Array)
                        Environment.SetRainCells(rc.Deserialize<List<RainCellDto>>(RadarEnvironment.ReadOptions) ?? new());
                    return Ack(cmd, true, null, reqId);
                }
                case "getStatus":
                case "getTracks":
                    return Ack(cmd, true, null, reqId);
                default:
                    return Ack(cmd, false, $"未知指令:{cmd}", reqId);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException)
        {
            return Ack(cmd, false, ex.Message, reqId);
        }
    }

    private AckMessage Ack(string cmd, bool ok, string? error, JsonElement? reqId = null, int? id = null, double? value = null)
        => new() { Radar = Config.Id, Cmd = cmd, Ok = ok, Error = error, ReqId = reqId, Id = id, Value = value };

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static double Pct(JsonElement e) => Math.Clamp(Num(e, "value") ?? throw new ArgumentException("需要 value(0–100)"), 0, 100);

    // ------------------------------------------------------------------ 訊息

    /// <param name="client">range_nm 與 pulse 為該客戶端的顯示量程(null = 預設量程);pulse/pulseLength_m 為影像實際使用的脈衝(<see cref="DisplayPulseFor"/>)</param>
    public StatusMessage Status(Guid? client = null)
    {
        var rangeNm = RangeFor(client);
        var (pulse, pulseM) = DisplayPulseFor(rangeNm);
        var g = Controls.Guard;
        return new StatusMessage
        {
            Radar = Config.Id,
            Band = Config.Band.ToString(),
            FrequencyGHz = Config.FrequencyGHz,
            Transmitting = Transmitting,
            Standby = !Controls.Transmit,
            Failed = Failed,
            Degraded = Degraded,
            HeadingValid = LastScene?.HeadingValid ?? true,
            RangeNm = rangeNm,
            RangeScales = RadarControls.StandardRanges,
            Pulse = ContractJson.EnumName(pulse),
            PulseLengthM = pulseM,
            Gain = Controls.Gain,
            Sea = Controls.Sea,
            Rain = Controls.Rain,
            Tune = Controls.Tune,
            AutoTune = Controls.AutoTune,
            Rpm = Config.Rpm,
            Spokes = Config.Spokes,
            Cells = Config.DisplayCells,
            BeamwidthDeg = Config.BeamwidthDeg,
            AntennaHeightM = RadarJson.R(Config.AntennaHeightM, 2),
            AntennaFwdM = RadarJson.R(Config.AntennaFwdM, 2),
            AntennaStbdM = RadarJson.R(Config.AntennaStbdM, 2),
            ShadowSectors = Config.ShadowSectors.Select(s => new ShadowSectorReport(s.FromRelDeg, s.ToRelDeg, s.AttenuationDb)).ToArray(),
            GuardZone = new GuardZoneReport(g.Enabled, g.InnerNm, g.OuterNm, g.FromDeg, g.ToDeg, g.Relative),
            Trial = Controls.Trial is { } tr ? new TrialReport(tr.CourseDeg, tr.SpeedKn, tr.DelayMin) : null,
            CpaLimitNm = Arpa.CpaLimitNm,
            TcpaLimitMin = Arpa.TcpaLimitMin,
            ArpaRangeNm = Config.ArpaRangeNm,
            MaxTracks = Config.Arpa.MaxTracks,
            SeaState = SeaState,
            RainCells = Environment.RainCells.Count + Environment.ScenarioRainCells.Count + Environment.ScenarioRainPolygons.Count,
            LandPolygons = Environment.Land.Count,
            Frame = new FrameFormatReport("SRAD", SpokeFrame.Version, SpokeFrame.HeaderBytes),
        };
    }

    public TracksMessage Tracks()
    {
        var t = LastScene?.T ?? 0.0;
        var own = Arpa.Own;
        var ltp = new LocalTangentPlane(own.Lat, own.Lon);
        (double Lat, double Lon) Geo(double x, double y) => ltp.ToGeodetic(x - own.X, y - own.Y);
        var stable = Config.Arpa.StableTimeS;
        var list = new List<TrackReport>(Arpa.Tracks.Count);
        foreach (var tr in Arpa.Tracks)
        {
            var s = Arpa.Solve(tr, t, Controls.Trial);
            var (lat, lon) = Geo(s.X, s.Y);
            var tracking = tr.State == TrackState.Tracking;
            list.Add(new TrackReport
            {
                Id = tr.Id,
                Label = tr.Id.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                State = ContractJson.EnumName(tr.State),
                Acquisition = ContractJson.EnumName(tr.Mode),
                AgeS = RadarJson.R(tr.AgeS(t), 1),
                Stable = tracking && tr.AgeS(t) >= stable,
                Manoeuvring = tr.Manoeuvring,
                Dangerous = tr.Dangerous,
                SwapRisk = tr.SwapRisk,
                Misses = tr.Misses,
                X = RadarJson.R(s.X, 1),
                Y = RadarJson.R(s.Y, 1),
                Lat = RadarJson.R(lat, 6),
                Lon = RadarJson.R(lon, 6),
                RangeNm = RadarJson.R(s.RangeNm, 3),
                BearingDeg = RadarJson.R(s.BearingDeg, 1),
                RelBearingDeg = RadarJson.R(s.RelBearingDeg, 1),
                CourseDeg = tracking ? RadarJson.R(s.CourseDeg, 1) : null,
                SpeedKn = tracking ? RadarJson.R(s.SpeedKn, 2) : null,
                RelCourseDeg = tracking ? RadarJson.R(s.RelCourseDeg, 1) : null,
                RelSpeedKn = tracking ? RadarJson.R(s.RelSpeedKn, 2) : null,
                CpaNm = tracking ? RadarJson.R(s.CpaNm, 3) : null,
                TcpaMin = tracking ? RadarJson.R(s.TcpaMin, 2) : null,
                BcrNm = tracking ? RadarJson.R(s.BcrNm, 3) : null,
                BctMin = tracking ? RadarJson.R(s.BctMin, 2) : null,
                TrialCpaNm = tracking ? RadarJson.R(s.TrialCpaNm, 3) : null,
                TrialTcpaMin = tracking ? RadarJson.R(s.TrialTcpaMin, 2) : null,
                Past = tr.Past.Count > 0
                    ? tr.Past.Select(p => { var (pl, po) = Geo(p.X, p.Y); return new PastPosition(RadarJson.R(p.T, 1), RadarJson.R(p.X, 1), RadarJson.R(p.Y, 1), RadarJson.R(pl, 6), RadarJson.R(po, 6)); }).ToArray()
                    : null,
                Ais = tr.AisMmsi != 0 ? new AisTrackReport(tr.AisMmsi, tr.AisName, tr.AisAssociated, RadarJson.R(tr.AisOffsetM, 0)) : null,
            });
        }
        return new TracksMessage
        {
            Radar = Config.Id,
            T = RadarJson.R(t, 2),
            Scan = Scan,
            Transmitting = Transmitting,
            OwnShip = new OwnShipReport(RadarJson.R(own.X, 1), RadarJson.R(own.Y, 1), RadarJson.R(own.Lat, 6), RadarJson.R(own.Lon, 6),
                RadarJson.R(own.HeadingDeg, 1), RadarJson.R(own.CogDeg, 1), RadarJson.R(own.SogKn, 2)),
            CpaLimitNm = Arpa.CpaLimitNm,
            TcpaLimitMin = Arpa.TcpaLimitMin,
            Trial = Controls.Trial is { } trial ? new TrialReport(trial.CourseDeg, trial.SpeedKn, trial.DelayMin) : null,
            Targets = list,
        };
    }

    public EventMessage EventMessage(ArpaEvent e) => new() { Radar = Config.Id, Event = new EventBody(e.Kind, RadarJson.R(e.T, 2), e.TargetId, e.Message) };
}
