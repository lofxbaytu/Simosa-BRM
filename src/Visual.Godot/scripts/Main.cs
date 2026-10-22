using System.Diagnostics;
using Godot;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Net;
using SimosaBRM.Visual.Shared.Quality;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.Smoke;
using SimosaBRM.Visual.Shared.State;
using SimosaBRM.Visual.Shared.View;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 主場景(docs/design/visual-client.md 第 2 節):
/// 啟動參數 → 專案根目錄/資料 → 硬體偵測與品質等級 → 建立海面/天候/港口/自船/目標船/拖船錨/相機/HUD → 訂閱狀態流。
/// 每顯示幀:自 StateClient 取插值樣本 → 更新各節點 → 自動降級檢查 → HUD。無頭煙霧模式(--smoke)以假狀態序列餵入並輸出 build/visual/smoke.json
/// (煙霧時鐘固定每顯示幀 1/60 s,N 幀 = N/60 s 模擬時間,與主機幀距無關)。
/// 品質持久化(build/visual/quality.json):啟動偵測後、每次等級改變(暖機再偵測、自動降級、手動覆寫)、Esc 與視窗關閉時寫入;
/// 無頭/煙霧模式不寫(避免 CI 的 headless=low 影響實機的啟動渲染器)。
/// 視景不做物理、不送指令(規劃書第 10.1 節)。
/// </summary>
public partial class Main : Node3D
{
    private LaunchOptions _options = new();
    private string _root = "";
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _manualClock; // 煙霧模式用的確定性時鐘(固定步長)
    private const double SmokeStepSeconds = 1.0 / 60.0;
    private StateClient? _client;
    private StateInterpolator _interp = new();
    private AdaptiveQualityController _quality = new(QualityTier.Low);
    private QualityDecision _decision = new(QualityTier.Low, "尚未偵測");
    private HardwareInfo _hardware = new() { GpuName = "" }; // 啟動時偵測到的硬體;暖機再偵測沿用(只補實測幀率)
    private QualitySettings _settings = QualitySettings.For(QualityTier.Low);
    private string _persistPath = "";
    private PersistedQuality _persisted = new();

    private HullParameters _ownHull = null!;
    private ShipParticulars? _particulars;
    private OceanNode _ocean = null!;
    private WeatherNode _weather = null!;
    private HarbourNode _harbour = null!;
    private ShipNode _ownShip = null!;
    private TargetShipsNode _targets = null!;
    private TugsAndAnchorsNode _tugs = null!;
    private CameraRig _camera = null!;
    private HudLayer? _hud;
    private readonly List<string> _loadedAreas = new();
    private LocalTangentPlane? _origin;
    private string? _scenarioId;
    private bool _harbourBuilt;
    private bool _warmupDone;
    private double _warmupSeconds;
    private int _warmupFrames;
    private double _runSeconds;
    private readonly List<string> _notes = new();
    private readonly List<string> _qualityLog = new();

    // 煙霧模式
    private int _smokeFed;
    private readonly List<SmokeFrame> _smokeFrames = new();
    private int _smokeRendered;

    public bool Headless => DisplayServer.GetName() == "headless";

    public override void _Ready()
    {
        _options = LaunchOptions.Parse(OS.GetCmdlineUserArgs());
        foreach (var w in _options.Warnings) { GD.PushWarning(w); _notes.Add(w); }
        _root = ResolveRoot();
        GD.Print($"[visual] 專案根目錄:{_root}");
        _persistPath = QualityPersistence.DefaultPath(_root);
        _persisted = QualityPersistence.Load(_persistPath);

        // 品質等級:啟動參數 > 持久化的手動覆寫 > 自動偵測
        _decision = DetectHardware();
        var initial = _options.Quality ?? QualityPersistence.ParseOverride(_persisted.ManualOverride) ?? _decision.Tier;
        _quality = new AdaptiveQualityController(initial);
        if (_options.Quality is { } q) _quality.SetOverride(q, 0);
        else if (QualityPersistence.ParseOverride(_persisted.ManualOverride) is { } p) _quality.SetOverride(p, 0);
        _quality.Changed += OnQualityChanged;
        _settings = QualitySettings.For(_quality.Current);
        GD.Print($"[visual] 品質:{QualitySettings.TierName(_quality.Current)}(偵測:{_decision.Reason};渲染器 {RenderingServer.GetCurrentRenderingMethod()})");
        if (RendererMismatch) GD.Print("[visual] 低品質應使用 Compatibility 渲染器:本次已記錄到 build/visual/quality.json,下次以 tools/visual-run.* -Quality auto 啟動即套用(--rendering-method gl_compatibility)");

        SetupWindow();
        LoadShip();
        BuildScene();
        ApplyQuality(_settings);
        Persist(); // 啟動偵測結果立即記錄(視窗關閉鈕離開也有 quality.json)

        if (_options.Smoke)
        {
            GD.Print($"[visual] 煙霧模式:{_options.SmokeFrames} 幀,假狀態序列(E05 示意),區域 {string.Join(",", _options.Areas.Count > 0 ? _options.Areas : new[] { "kaohsiung-approach", "kaohsiung-south-approach" })}");
            _client = new StateClient(new Uri("ws://127.0.0.1:1"), _interp, () => _manualClock);
            EnsureHarbour(SmokeSequence.Frame(0));
        }
        else
        {
            _client = new StateClient(StateClient.ParseUrl(_options.WebSocketUrl), _interp, () => _clock.Elapsed.TotalSeconds);
            _client.Log += m => GD.Print("[ws] " + m);
            _client.Start();
        }
    }

    private string ResolveRoot()
    {
        if (!string.IsNullOrEmpty(_options.DataDir) && Directory.Exists(_options.DataDir)) return Path.GetFullPath(_options.DataDir);
        var projectDir = ProjectSettings.GlobalizePath("res://");
        var root = RepositoryPaths.TryFindRoot(projectDir) ?? RepositoryPaths.TryFindRoot(OS.GetExecutablePath().GetBaseDir());
        if (root is null)
        {
            _notes.Add("找不到專案根目錄(需含 Directory.Build.props 與 data/);以 --data-dir 指定");
            return projectDir;
        }
        return root;
    }

    private QualityDecision DetectHardware()
    {
        var name = RenderingServer.GetVideoAdapterName();
        var type = RenderingServer.GetVideoAdapterType() switch
        {
            RenderingDevice.DeviceType.DiscreteGpu => "discrete",
            RenderingDevice.DeviceType.IntegratedGpu => "integrated",
            RenderingDevice.DeviceType.Cpu => "cpu",
            _ => null,
        };
        long? mem = null;
        try
        {
            var info = OS.GetMemoryInfo();
            if (info.ContainsKey("physical")) mem = (long)info["physical"] / (1024 * 1024);
        }
        catch (Exception) { }
        _hardware = new HardwareInfo { GpuName = Headless ? "headless" : name, DeviceType = type, SystemMemoryMB = mem, ScreenWidth = DisplayServer.ScreenGetSize().X, ScreenHeight = DisplayServer.ScreenGetSize().Y };
        return QualityDetector.Detect(_hardware);
    }

    /// <summary>目前等級要求 Compatibility 渲染器但本程序以 Forward+ 啟動(渲染器只能在啟動時決定;下次啟動由腳本依 quality.json 套用)。</summary>
    private bool RendererMismatch => _settings.Renderer == RendererKind.Compatibility && RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";

    private void SetupWindow()
    {
        if (Headless) return;
        var screens = DisplayServer.GetScreenCount();
        var monitor = Math.Clamp(_options.Monitor, 0, Math.Max(0, screens - 1));
        if (monitor != _options.Monitor) _notes.Add($"--monitor={_options.Monitor} 超出範圍(共 {screens} 個螢幕),改用 {monitor}");
        DisplayServer.WindowSetCurrentScreen(monitor);
        if (_options.Fullscreen) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        DisplayServer.WindowSetTitle($"Simosa BRM Visual — {ViewDefinition.KindName(_options.View)}(訓練模擬器,非航行設備)");
    }

    private void LoadShip()
    {
        try
        {
            _particulars = ShipParticulars.LoadFromDataRoot(_root, _options.ShipId);
            var loading = _options.Loading.ToLowerInvariant() == "ballast" ? LoadingCondition.Ballast : LoadingCondition.Full;
            _ownHull = HullParameters.FromParticulars(_particulars, loading);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"無法載入船舶資料 {_options.ShipId}:{ex.Message};改用預設尺寸");
            _notes.Add($"船舶資料載入失敗:{ex.Message}");
            _ownHull = HullParameters.ForTarget(_options.ShipId, _options.ShipId, "tanker", 105.5, 18.0, 5.7);
        }
    }

    private void BuildScene()
    {
        _weather = new WeatherNode { Name = "weather" };
        AddChild(_weather);
        _weather.Build(_settings);
        _ocean = new OceanNode();
        _ocean.Build(_settings);
        AddChild(_ocean);
        _harbour = new HarbourNode { Name = "harbour" };
        AddChild(_harbour);
        _ownShip = new ShipNode { Name = "ownShip" };
        _ownShip.Build(_ownHull, new Color(0.12f, 0.12f, 0.14f), "powerDriven", withLowPoly: false);
        AddChild(_ownShip);
        _targets = new TargetShipsNode { Name = "targets" };
        AddChild(_targets);
        _tugs = new TugsAndAnchorsNode { Name = "tugsAnchors" };
        AddChild(_tugs);
        _camera = new CameraRig { Name = "cameraRig" };
        AddChild(_camera);
        _camera.Configure(_ownHull, _options.View, _persisted.FovDeg);
        if (_options.Hud && !Headless)
        {
            _hud = new HudLayer { Name = "hud" };
            AddChild(_hud);
            _hud.SetDetection(_decision.Reason);
            _hud.SetOverrideSelection(_quality.ManualOverride);
            _hud.QualityOverrideChanged += t =>
            {
                _quality.SetOverride(t, _runSeconds);
                ApplyQuality(QualitySettings.For(_quality.Current));
                Persist();
            };
            _hud.Visible = _persisted.HudVisible;
        }
    }

    /// <summary>第一幀(或情境改變)時依狀態反推 ENU 原點並載入區域幾何。</summary>
    private void EnsureHarbour(OwnShipState state)
    {
        var scenario = state.Scenario?.Id;
        if (_harbourBuilt && string.Equals(scenario, _scenarioId, StringComparison.Ordinal)) return;
        if (_harbourBuilt)
        {
            foreach (var c in _harbour.GetChildren()) c.QueueFree();
            _harbour = new HarbourNode { Name = "harbour" };
            AddChild(_harbour);
            _loadedAreas.Clear();
        }
        _scenarioId = scenario;
        _origin = OriginSolver.FromPosition(state.Pos);
        var catalogue = AreaCatalogue.Load(_root);
        IReadOnlyList<string> ids = _options.Areas.Count > 0 ? _options.Areas
            : _options.Smoke ? new[] { "kaohsiung-approach", "kaohsiung-south-approach" }
            : catalogue.AreasForScenario(scenario);
        var geoms = new List<AreaGeometry>();
        foreach (var id in ids)
        {
            try
            {
                var g = AreaLoader.Load(catalogue.ResolvePath(id), _origin);
                geoms.Add(g);
                _loadedAreas.Add(g.Id);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"區域 {id} 載入失敗:{ex.Message}");
                _notes.Add($"區域 {id} 載入失敗:{ex.Message}");
            }
        }
        _harbour.Build(geoms);
        _harbourBuilt = true;
        GD.Print($"[visual] 港口幾何:{geoms.Count} 個區域、{_harbour.MeshCount} 個網格、{_harbour.Triangles} 三角形(原點 {_origin.OriginLat:F4}, {_origin.OriginLon:F4};情境 {scenario ?? "未知"})");
    }

    private void ApplyQuality(QualitySettings s)
    {
        _settings = s;
        var vp = GetViewport();
        vp.Scaling3DScale = (float)s.RenderScale;
        vp.Msaa3D = s.Msaa switch { >= 8 => Viewport.Msaa.Msaa8X, >= 4 => Viewport.Msaa.Msaa4X, >= 2 => Viewport.Msaa.Msaa2X, _ => Viewport.Msaa.Disabled };
        _ocean.Apply(s);
        _weather.Apply(s);
        _camera.SetDrawDistance(s.DrawDistanceM);
    }

    private void OnQualityChanged(QualityChange c)
    {
        var line = $"{c.AtSeconds:F1}s {QualitySettings.TierName(c.From)} → {QualitySettings.TierName(c.To)}:{c.Reason}";
        _qualityLog.Add(line);
        GD.Print("[quality] " + line);
        CallDeferred(nameof(ApplyCurrentQuality));
        Persist(); // 自動降級/暖機再偵測/手動覆寫都記錄 lastAutoTier,啟動腳本據此選渲染器
        try
        {
            var log = Path.Combine(_root, "build", "visual", "quality.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.UtcNow:O} {line}{System.Environment.NewLine}");
        }
        catch (Exception) { }
    }

    private void ApplyCurrentQuality()
    {
        ApplyQuality(QualitySettings.For(_quality.Current));
        _hud?.SetOverrideSelection(_quality.ManualOverride);
    }

    private void Persist()
    {
        if (Headless || _options.Smoke) return; // 無頭/煙霧不寫 quality.json
        try
        {
            // lastAutoTier:無手動覆寫時目前生效的自動等級(偵測 + 暖機 + 自動降級);有覆寫時為偵測結果
            var autoTier = _quality.ManualOverride is null ? _quality.Current : _decision.Tier;
            QualityPersistence.Save(_persistPath, _persisted with
            {
                ManualOverride = _quality.ManualOverride is { } t ? QualitySettings.TierName(t) : null,
                LastAutoTier = QualitySettings.TierName(autoTier),
                LastGpu = RenderingServer.GetVideoAdapterName(),
                HudVisible = _hud?.Visible ?? true,
                FovDeg = _camera.FovDeg,
            });
        }
        catch (Exception ex) { GD.PushWarning($"無法寫入 {_persistPath}:{ex.Message}"); }
    }

    public override void _Process(double delta)
    {
        _runSeconds += delta;
        if (_options.Smoke) FeedSmoke(delta);
        var now = _options.Smoke ? _manualClock : _clock.Elapsed.TotalSeconds;
        var sample = _client?.Sample(now);
        var frameStart = Time.GetTicksUsec();

        if (sample is not null)
        {
            EnsureHarbour(sample.Latest);
            var look = EnvironmentMapper.Build(sample.Latest, sample.RadarEnvironment, _options.DayNight, _options.StartUtc);
            _ownShip.SetPose(sample.Own.East, sample.Own.North, sample.Own.HeadingDeg);
            _ownShip.ApplyLod(0, double.MaxValue, double.MaxValue, look.NavigationLightsOn);
            _camera.Update(sample.Own);
            _harbour.SetOverviewMode(_camera.Kind == ViewKind.Overview);
            _targets.Update(sample, _settings, look.NavigationLightsOn);
            _tugs.Update(sample, _ownHull, look.NavigationLightsOn, _settings.DrawDistanceM);
            var cam = _camera.Camera.GlobalPosition;
            _ocean.Update(delta, cam, look);
            _weather.Update(look, cam);
            _hud?.UpdateText(delta, sb => FillHud(sb, sample, look));
        }
        else
        {
            _hud?.UpdateText(delta, sb => sb.Append("等待狀態流 ").Append(_options.WebSocketUrl).Append(' ').Append(_client?.Connection).Append("  fps ").Append(Engine.GetFramesPerSecond()));
        }

        // 啟動暖機:前 3 秒的實測幀率作為偵測輸入(規劃書第 7.3 節「實測幀率」)
        if (!_warmupDone && !Headless)
        {
            _warmupSeconds += delta;
            _warmupFrames++;
            if (_warmupSeconds >= 3.0)
            {
                _warmupDone = true;
                var fps = _warmupFrames / _warmupSeconds;
                // 沿用啟動時的硬體資訊只補實測幀率(審查發現:記憶體/類型設 null 會把所有內顯機降為 low);只允許降級
                var refined = QualityDetector.Refine(_hardware, _decision, fps);
                GD.Print($"[visual] 暖機 {_warmupSeconds:F1} s 實測 {fps:F0} fps → {QualitySettings.TierName(refined.Tier)}");
                if (_quality.ManualOverride is null && refined.Tier < _decision.Tier)
                {
                    _decision = refined;
                    _quality.SetDetected(refined.Tier, _runSeconds, refined.Reason);
                    _hud?.SetDetection(refined.Reason);
                }
            }
        }
        else if (!Headless)
        {
            _quality.Update(delta, _runSeconds);
        }

        if (_options.Smoke) RecordSmoke(frameStart, sample);
    }

    private System.Text.StringBuilder FillHud(System.Text.StringBuilder sb, VisualSample s, EnvironmentLook look)
    {
        var st = s.Latest;
        sb.Append("HDG ").Append(s.Own.HeadingDeg.ToString("000")).Append("°  SOG ").Append(st.Sog.ToString("0.0")).Append(" kn  ROT ").Append(st.Rot.ToString("+0;-0")).Append(" °/min\n");
        sb.Append("舵 ").Append(st.Rudder.ToString("+0;-0")).Append("°  RPM ").Append(st.Rpm.ToString("0")).Append("  ").Append(st.Telegraph).Append('\n');
        sb.Append("能見度 ").Append((look.VisibilityM / 1852.0).ToString("0.0")).Append(" nm  風 ").Append(st.Wind.TrueDir.ToString("000")).Append('/').Append(st.Wind.TrueSpeed.ToString("0")).Append(" kn  海況 ").Append(look.SeaState.ToString("0")).Append(look.Rain > 0.05 ? "  雨" : "").Append('\n');
        sb.Append("視角 ").Append(_camera.View?.Label).Append("  FOV ").Append(_camera.FovDeg.ToString("0")).Append("°  品質 ").Append(QualitySettings.TierName(_quality.Current)).Append(_quality.ManualOverride is null ? "(自動)" : "(手動)").Append("  ").Append(RenderingServer.GetCurrentRenderingMethod());
        if (RendererMismatch) sb.Append("(低品質建議 Compatibility 渲染器:已記錄,重啟後生效)");
        sb.Append('\n');
        sb.Append("fps ").Append(Engine.GetFramesPerSecond()).Append("  連線 ").Append(_client?.Connection).Append("  t ").Append(s.SimTime.ToString("0.0")).Append(" s  ×").Append(_client?.Rate.ToString("0.0"));
        if (s.Stalled) sb.Append("  [狀態停滯]");
        if (st.Flags?.Frozen == true) sb.Append("  [凍結]");
        if (st.Flags?.Aground == true) sb.Append("  [擱淺]");
        if (st.Flags?.Collision == true) sb.Append("  [碰撞]");
        sb.Append("\n目標 ").Append(_targets.DrawnCount).Append('/').Append(_targets.NodeCount).Append("  港口網格 ").Append(_harbour.MeshCount);
        if (_client?.LastEventKind is { } ev) sb.Append("  最近事件 ").Append(ev);
        sb.Append("\n鍵:1–5 視角、滑鼠拖曳環視、滾輪視野、H HUD、F11 全螢幕、Esc 離開");
        return sb;
    }

    private void FeedSmoke(double delta)
    {
        _manualClock += SmokeStepSeconds; // 固定步長(與主機實際幀距無關;delta 只用於幀時間量測)
        // 25 Hz:牆鐘走到 k/25 時餵第 k 幀(第 0 幀帶雷達環境作為關鍵幀;之後每 25 幀一次)
        while (_smokeFed <= (int)(_manualClock * SmokeSequence.Hz))
        {
            _client!.Feed(SmokeSequence.Frame(_smokeFed, withRadarEnvironment: _smokeFed % 25 == 0));
            _smokeFed++;
        }
    }

    private void RecordSmoke(ulong frameStart, VisualSample? sample)
    {
        var elapsedMs = (Time.GetTicksUsec() - frameStart) / 1000.0;
        if (sample is not null)
        {
            _smokeFrames.Add(new SmokeFrame
            {
                Frame = _smokeRendered,
                SimTime = sample.SimTime,
                FrameTimeMs = elapsedMs,
                Nodes = GetTree().GetNodeCount(),
                TargetsDrawn = _targets.DrawnCount,
                HarbourMeshes = _harbour.MeshCount,
                OwnEast = sample.Own.East,
                OwnNorth = sample.Own.North,
                Heading = sample.Own.HeadingDeg,
                SogKn = sample.Own.SogKn,
                Extrapolating = sample.Extrapolating,
                Stalled = sample.Stalled,
            });
        }
        _smokeRendered++;
        if (_smokeRendered < _options.SmokeFrames) return;
        FinishSmoke();
    }

    private void FinishSmoke()
    {
        var version = Engine.GetVersionInfo();
        var report = SmokeReport.Build(
            _smokeFrames,
            QualitySettings.TierName(_quality.Current),
            ViewDefinition.KindName(_camera.Kind),
            _loadedAreas,
            Headless,
            _notes,
            godotVersion: version.ContainsKey("string") ? (string)version["string"] : null,
            renderer: RenderingServer.GetCurrentRenderingMethod(),
            qualityReason: _decision.Reason,
            harbourTriangles: _harbour.Triangles,
            ownShipTriangles: _ownShip.Triangles,
            qualityChanges: _qualityLog);
        var path = _options.SmokeOutput ?? Path.Combine(_root, "build", "visual", "smoke.json");
        if (!Path.IsPathRooted(path)) path = Path.Combine(_root, path);
        report.Save(path);
        GD.Print($"[visual] 煙霧測試完成:{_smokeFrames.Count} 幀,平均 {report.MeanFrameTimeMs:F2} ms,最大 {report.MaxFrameTimeMs:F2} ms,節點 {(_smokeFrames.Count > 0 ? _smokeFrames[^1].Nodes : 0)},ok={report.Ok} → {path}");
        GetTree().Quit(report.Ok ? 0 : 1);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        switch (k.Keycode)
        {
            case Key.H:
                if (_hud is not null) { _hud.Visible = !_hud.Visible; Persist(); }
                break;
            case Key.F11:
                DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
                break;
            case Key.Escape:
                Persist();
                GetTree().Quit();
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Persist(); // 視窗關閉鈕離開也記錄 quality.json
    }

    public override void _ExitTree()
    {
        if (_client is not null) _ = _client.DisposeAsync();
    }
}
