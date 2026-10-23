using System.Globalization;
using System.Net;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

// 主控程序(規劃書第 5.2 節):啟動引擎、載入情境、WebSocket(ws://0.0.0.0:8765)、UDP 多播狀態匯流排、NMEA 閘道、紀錄/重播。
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
HostOptions options;
try { options = HostOptions.Parse(args); }
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
if (options.Help)
{
    Console.WriteLine(HostOptions.Usage);
    return 0;
}

var root = RepositoryPaths.FindRoot();
ShipParticulars LoadShip(string id) => ShipParticulars.LoadFromDataRoot(root, id);

// 動力學工廠(規劃書第 6.2 節):預設 MMG 完整模型(係數檔由 src/Tools.Calibration 產生);--dynamics placeholder 切回 Nomoto 暫代模型
Func<ShipParticulars, LoadingCondition, IShipDynamics> DynamicsFactory(string name) => name switch
{
    "placeholder" => (s, l) => new PlaceholderDynamics(s, l),
    _ => (s, l) => MmgDynamics.Load(root, s, l),
};

// 參數集(情境引擎設計第 2 節):--parameters 指定時先載入;未指定時由 ScenarioLoader 依情境 assessment.parameterSet 自動載入
ParameterSet? parameterSet = null;
if (options.Parameters is { } paramPath)
{
    var full = Path.IsPathRooted(paramPath) ? paramPath : Path.Combine(root, paramPath);
    try { parameterSet = ParameterSetLoader.Load(full); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"參數集載入失敗:{ex.Message}");
        return 2;
    }
}

// ---------------------------------------------------------------- 重播模式
if (options.Replay is { } replayPath)
{
    var full = Path.IsPathRooted(replayPath) ? replayPath : Path.Combine(root, replayPath);
    Console.WriteLine($"重播:{full}");
    // 依紀錄標頭的模型名稱選擇動力學(紀錄用暫代模型時不會因預設 MMG 而雜湊不一致)
    var recordedDynamics = RecordReader.Read(full).Header.Dynamics;
    var replayFactory = DynamicsFactory(recordedDynamics.StartsWith("mmg", StringComparison.OrdinalIgnoreCase) ? "mmg" : "placeholder");
    // 雷達重播(radar-arpa.md 第 5.2 節):雷達服務已連線時,紀錄的 radarControl 行在相同 tick 重放到雷達服務的重播接收端,
    // 並以狀態 WebSocket(唯讀)推送重播中的狀態幀、依 --timescale 即時步調,雷達畫面因此重現學員當時的量程/擷取;無連線則略過並記 note
    RadarReplayClient? radarReplay = null;
    var replayNotes = new List<string>();
    if (options.RadarReplayUrl is { } radarUrl)
    {
        var rc = new RadarReplayClient(radarUrl);
        if (await rc.TryConnectAsync(TimeSpan.FromSeconds(1.5)))
        {
            radarReplay = rc;
            Console.WriteLine($"  雷達服務已連線:{rc.Uri}(radarControl 行將重放;狀態推送 ws://0.0.0.0:{options.Port},步調 ×{options.TimeScale ?? 1:F1})");
        }
        else
        {
            await rc.DisposeAsync();
            replayNotes.Add($"雷達服務未連線({rc.Uri}):radarControl 行未重放(以 --no-radar 關閉嘗試)");
        }
    }
    WebSocketStateServer? replayWs = null;
    var pace = options.TimeScale ?? 1.0;
    var wallStart = System.Diagnostics.Stopwatch.StartNew();
    Action<SimulationEngine>? onStep = null;
    if (radarReplay is not null)
    {
        onStep = e =>
        {
            // 即時步調:牆鐘 = 模擬時間 / 倍率(雷達服務依狀態幀時間轉動天線)
            var ahead = e.Time / Math.Max(0.1, pace) - wallStart.Elapsed.TotalSeconds;
            if (ahead > 0.002) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(ahead, 0.5)));
        };
    }
    void ConfigureReplay(SimulationEngine e)
    {
        if (radarReplay is null) return;
        if (options.EnableWebSocket)
        {
            replayWs = new WebSocketStateServer(e, options.Port) { ReadOnly = true };
            replayWs.Log += msg => Console.WriteLine($"  [ws] {msg}");
            replayWs.Start();
            e.Broadcast += (_, st) => replayWs.Publish(ContractJson.SerializeToUtf8Bytes(st));
        }
        e.CommandApplied += (eng, cmd, _) => { if (cmd.Type == SimCommandType.RadarControl) radarReplay.Send(cmd, eng.Time); };
    }
    // --rescore:影子評估以目前的試俥目標檔(data/ships/<ID>/trial_targets.json)重新比較;另給 --parameters 時同時以新參數集重新評分
    // (未給 --parameters 時沿用紀錄標頭的參數值,只換目標值)
    var result = Replayer.Replay(full, LoadShip, replayFactory, configure: ConfigureReplay, onStep: onStep, rescore: options.Rescore ? parameterSet : null, rescoreTargets: options.Rescore);
    if (radarReplay is not null)
    {
        await radarReplay.FlushAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine($"  雷達重播:送出 {radarReplay.Sent} 則 radarControl(雷達服務接受 {radarReplay.AcksOk}、拒絕 {radarReplay.AcksFailed}){(replayWs is not null ? $";狀態客戶端 {replayWs.ClientCount}" : "")}");
        await radarReplay.DisposeAsync();
    }
    if (replayWs is not null) await replayWs.DisposeAsync();
    Console.WriteLine($"  完整性雜湊:{(result.IntegrityOk ? "一致" : "不一致或缺少 footer")}");
    Console.WriteLine($"  動力學模型:紀錄 {result.DynamicsRecorded} / 目前 {result.DynamicsUsed}");
    Console.WriteLine($"  重播指令數:{result.InputsReplayed},最終 tick {result.FinalTick}");
    Console.WriteLine($"  紀錄雜湊:{result.ExpectedHash}");
    Console.WriteLine($"  重播雜湊:{result.ActualHash}");
    Console.WriteLine($"  事件序列:紀錄 {result.EventsRecorded} / 重播 {result.EventsReplayed},{(result.EventSequenceMatches ? "一致" : "不一致:" + result.FirstEventMismatch)}");
    if (result.SummaryMatches is { } sm) Console.WriteLine($"  評估摘要:{(sm ? "與紀錄一致" : "與紀錄不一致")}");
    if (result.ManoeuvreTargetsSource is { } mts)
        Console.WriteLine($"  試俥目標值:{(mts == "header" ? "紀錄標頭" : mts == "file" ? "目前檔案" : "無")}{(options.Rescore ? "(重新評分改用目前檔案)" : "")}");
    foreach (var note in result.Notes.Concat(replayNotes)) Console.WriteLine($"  警告:{note}");
    if (options.Rescore && result.Summary is { } rs)
    {
        // 重新評分:物理、交通、時間表與雜湊仍依紀錄的原情境重播(雜湊與事件序列必須一致),只有評估模組以新參數集重算
        Console.WriteLine($"  重新評分(參數集 {rs.ParameterSet?.Id} {rs.ParameterSet?.Version}{(parameterSet is null ? ",沿用紀錄標頭參數值" : "")}):分數 {rs.Assessment.Score:F0}/{rs.Assessment.PassMark:F0},{(rs.Assessment.Passed ? "通過" : "未通過")}");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rs, ContractJson.Indented));
    }
    if (result.Warning is not null) Console.WriteLine($"  警告:{result.Warning}");
    var ok = result.HashMatches && result.IntegrityOk && result.EventSequenceMatches;
    Console.WriteLine(ok ? "結果:狀態雜湊、完整性與事件序列一致" : result.HashMatches ? "結果:狀態雜湊一致,但完整性或事件序列不一致" : "結果:狀態雜湊不一致");
    return ok ? 0 : 1;
}

// ---------------------------------------------------------------- 即時模式
var scenarioPath = Path.IsPathRooted(options.Scenario) ? options.Scenario : Path.Combine(root, options.Scenario);
if (!File.Exists(scenarioPath) && !Path.HasExtension(options.Scenario))
    scenarioPath = Path.Combine(root, "data", "scenarios", options.Scenario + ".yaml"); // 允許以情境代號指定
Scenario scenario;
try { scenario = ScenarioLoader.Load(scenarioPath, parameterSet); }
catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"情境載入失敗:{ex.Message}");
    return 2;
}
if (options.Ship is { } shipOverride) scenario.Ship.Id = shipOverride.ToUpperInvariant();
if (options.Loading is { } loadingOverride) scenario.Ship.Loading = loadingOverride;
if (options.TimeScale is { } ts) scenario.TimeScale = ts;
scenario.Validate();

var ship = LoadShip(scenario.Ship.Id);

// 靜態檢查(規劃書第 9.1 節自動檢查;--validate 只檢查不執行)
void PrintIssues(Scenario sc, ShipParticulars sh)
{
    var issues = ScenarioValidator.Check(sc, sh, Path.Combine(root, "data", "scenarios", sc.Id));
    foreach (var issue in issues) Console.WriteLine($"  情境檢查 {issue}");
    if (sc.ParameterSetInfo is { } ps) Console.WriteLine($"  參數集 {ps.Id} {ps.Version}(sha256 {ps.Sha256[..16]}…),解析 {ps.Resolved.Count} 個引用");
}
PrintIssues(scenario, ship);
if (options.Validate)
{
    var errors = ScenarioValidator.Check(scenario, ship).Count(i => i.Severity == ScenarioIssueSeverity.Error);
    Console.WriteLine(errors == 0 ? "情境檢查:無錯誤" : $"情境檢查:{errors} 個錯誤");
    return errors == 0 ? 0 : 1;
}
SimulationEngine engine;
// 情境未給 startTimeUtc 時以實際時鐘代入並寫回情境(state.utc、紀錄標頭、NMEA 共用同一起點;ScenarioLoadRequested 的 LoadScenario 沿用 Options.Clock)
try { engine = new SimulationEngine(ship, scenario, DynamicsFactory(options.Dynamics), new EngineOptions { Clock = TimeProvider.System }); }
catch (FileNotFoundException ex) when (options.Dynamics == "mmg")
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("(或以 --dynamics placeholder 使用暫代模型)");
    return 3;
}
// 優雅停止(交接紀錄待辦第 1 項):Ctrl+C / SIGINT / SIGTERM / SIGHUP / ProcessExit、停止檔、WebSocket shutdown 指令與 --duration 都經同一個協調器,
// 主迴圈結束後關閉紀錄(寫 footer)再結束程序
using var shutdown = new ShutdownCoordinator();
shutdown.RegisterProcessSignals();
StopFileWatcher? stopFile = null;
if (options.EnableStopFile)
{
    try { stopFile = StopFileWatcher.Create(root, options.StopFile); }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}
// 主控程序層指令(sessionInfo / shutdown;不進引擎、不進雜湊):sessionInfo 寫進目前紀錄(session 行)或帶進下一份紀錄的 header
var router = new HostCommandRouter(engine, shutdown);
router.Log += msg => Console.WriteLine($"  [host] {msg}");
// 紀錄(規劃書第 5.3 節):每份紀錄一個 header 情境;loadScenario 時紀錄器在引擎重設前自動關閉,這裡再為新情境開新檔
RecordWriter? recorder = null;
router.Recorder = () => recorder;
string RecordPathFor(string scenarioId) => options.RecordPath is { } rp && recorder is null
    ? (Path.IsPathRooted(rp) ? rp : Path.Combine(root, rp))
    : RecordWriter.DefaultPath(RepositoryPaths.RecordsDirectory(root), scenarioId);
if (options.Record)
{
    recorder = new RecordWriter(RecordPathFor(scenario.Id), engine, session: router.Session);
    Console.WriteLine($"  紀錄:{recorder.Path}");
}
// 情境切換(檔案路徑或內嵌情境物件共用;交接紀錄待辦第 1 項):載入結果 → TryApplyScenarioLoad(驗證錯誤/船舶/動力學失敗 → 拒絕,
// 引擎狀態不變、紀錄不切換,引擎發 scenarioLoadRejected 由下方訂閱者印出並轉給教官站)→ 成功才列印檢查、紀錄另開新檔
void ApplyLoadedScenario(SimulationEngine e, ScenarioLoadResult result, string origin)
{
    if (!e.TryApplyScenarioLoad(result, LoadShip, origin)) return;
    var sc = e.Scenario;
    Console.WriteLine($"已載入情境 {sc.Id}({(origin == "file" ? "檔案" : "內嵌")})");
    PrintIssues(sc, e.Ship);
    if (recorder is { Closed: true } closed)
    {
        var footer = closed.Footer!;
        Console.WriteLine($"紀錄已關閉(情境切換):{closed.Path}(tick {footer.FinalTick},狀態雜湊 {footer.StateHash[..16]}…)");
        // 參與者與休息時間確認沿用到新情境的紀錄 header(同一批人繼續練習;教官站可再送 sessionInfo 覆寫為 session 行)
        recorder = new RecordWriter(RecordPathFor(sc.Id), e, session: router.Session);
        Console.WriteLine($"  紀錄(新情境):{recorder.Path}{(router.Session is { } si ? $"(header 含 {si.Describe()})" : "")}");
    }
}
engine.ScenarioLoadRequested += (e, path) =>
{
    var p = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
    if (!File.Exists(p) && !Path.HasExtension(path)) p = Path.Combine(root, "data", "scenarios", path + ".yaml"); // 允許以情境代號指定(與 --scenario 相同)
    ApplyLoadedScenario(e, ScenarioLoader.TryLoad(p, parameterSet), "file");
};
// 內嵌情境(loadScenario args.scenario;規劃書第 9.1 節靠離泊預演自動組題):與 YAML 相同的載入路徑,紀錄標頭保存完整情境,可獨立重播
engine.InlineScenarioLoadRequested += (e, element, id) =>
    ApplyLoadedScenario(e, ScenarioLoader.TryFromJsonElement(element, parameterSet, Path.Combine(root, "data", "scenarios"), id), "inline");
engine.ScenarioLoadRejected += (e, ev) =>
{
    Console.Error.WriteLine($"[t={ev.T:F1}] {ev.Message}(引擎狀態不變,情境仍為 {e.Scenario.Id})");
    if (ev.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } d && d.TryGetProperty("issues", out var issues))
        foreach (var issue in issues.EnumerateArray())
            Console.Error.WriteLine($"  [{issue.GetProperty("severity").GetString()}] {issue.GetProperty("path").GetString()}:{issue.GetProperty("message").GetString()}");
};
engine.EventRaised += (e, ev) => Console.WriteLine($"[t={ev.T:F1}] 事件 {ev.Source}/{ev.Kind}{(ev.ItemId is null ? "" : $" {ev.ItemId}")}{(ev.TargetId is null ? "" : $" {ev.TargetId}")}{(ev.Message is null ? "" : $":{ev.Message}")}");

// NMEA 起始 UTC = 引擎解析(或寫回)的開始時刻,與 state.utc 同源;Clock 已給所以不會為 null,保險起見仍保留 fallback
var startUtc = engine.StartUtc ?? DateTime.UtcNow;

Console.WriteLine($"Simosa BRM SimCore Host — {ship.Name} ({ship.Id}),裝載 {scenario.Ship.Loading},情境 {scenario.Id}");
Console.WriteLine($"  dt = {engine.Dt} s(50 Hz)、廣播 25 Hz、快照每 {engine.Options.AutoSnapshotIntervalTicks * engine.Dt:F0} s、動力學 {engine.Dynamics.ModelName}、種子 {scenario.Seed}");
if (scenario.Targets.Count > 0)
    Console.WriteLine($"  目標船 {scenario.Targets.Count} 艘:{string.Join("、", scenario.Targets.Select(t => $"{t.Id}({t.Behaviour.Mode}{(t.Trigger is { Type: not SimosaBRM.SimCore.Scenario.TargetTriggerType.None } ? ",觸發" : "")})"))};最小 CPA 門檻 {scenario.Assessment.MinCpaNm} nm");

if (scenario.Bnwas.Mode != BnwasMode.Off || scenario.Alerts.OffHeadingDeg is not null || scenario.Alerts.HeadingMonitorDeg is not null || scenario.Alerts.ShallowWaterM is not null)
    Console.WriteLine($"  BNWAS {ContractJson.EnumName(scenario.Bnwas.Mode)}(休眠 {scenario.Bnwas.DormantS:F0} s);警報門檻:偏航 {scenario.Alerts.OffHeadingDeg?.ToString("F0") ?? "關"}°、航向監視 {scenario.Alerts.HeadingMonitorDeg?.ToString("F0") ?? "關"}°、淺水 {scenario.Alerts.ShallowWaterM?.ToString("F1") ?? "關"} m;warning 升級 {scenario.Alerts.EscalateS:F0} s、靜音 {scenario.Alerts.SilenceS:F0} s");
// 外力(拖船/錨/泊位;docs/design/physics-tugs-anchors-mooring.md)
if (scenario.Tugs is { Count: > 0 } tugList)
    Console.WriteLine($"  拖船 {tugList.Count} 艘:{string.Join("、", tugList.Select(t => $"{t.Id}({t.BollardPullT:F0} t,{t.Attach?.Named ?? "點"})"))}");
if (scenario.Berth?.Definition is { } berthDef)
    Console.WriteLine($"  泊位 {berthDef.Id}{(berthDef.Name is null ? "" : $" {berthDef.Name}")}:碰墊 {berthDef.Fenders.Count}、繫船柱 {berthDef.Bollards.Count}、繫纜 {scenario.Berth.Lines.Count}{(berthDef.Source is { } bs && bs.Contains("示意") ? "(示意值)" : "")}");
if (engine.External.AnchorSpec is { } anchorSpec)
    Console.WriteLine($"  錨 {anchorSpec.Type} {anchorSpec.AnchorMassKg:F0} kg × 2,鏈 Φ{anchorSpec.ChainDiameterMm:F0} mm 左 {anchorSpec.PortLengthM / anchorSpec.ShackleLengthM:F0}/右 {anchorSpec.StarboardLengthM / anchorSpec.ShackleLengthM:F0} 節;底質 {scenario.Environment.Seabed ?? "sand"}{(anchorSpec.EstimatedFields.Count > 0 ? $";估計欄位 {string.Join(",", anchorSpec.EstimatedFields)}" : "")}{(engine.Dynamics.SupportsExternalForces ? "" : ";動力學不支援外力,拖船/錨/繫纜指令會被拒")}");
if (scenario.Timeline.Count > 0 || scenario.Route is not null || scenario.Zones.Count > 0)
    Console.WriteLine($"  情境引擎:時間表 {scenario.Timeline.Count} 項、航線 {scenario.Route?.Waypoints.Count ?? 0} 航點、區域 {scenario.Zones.Count}、硬性失敗 {scenario.Assessment.HardFail.Count}/容許帶 {scenario.Assessment.Bands.Count}/必要事件 {scenario.Assessment.Required.Count}");

WebSocketStateServer? ws = null;
if (options.EnableWebSocket)
{
    ws = new WebSocketStateServer(engine, options.Port) { HostCommandHandler = router.Handle };
    ws.Log += msg => Console.WriteLine($"  [ws] {msg}");
    ws.Start();
    Console.WriteLine($"  WebSocket:{ws.Prefix!.Replace("http://", "ws://").Replace("*", "0.0.0.0")}(推送 OwnShipState JSON 每 40 ms;接收 SimCommand JSON;sessionInfo / shutdown 由主控程序回 ack)");
}

UdpStateBus? bus = null;
if (options.EnableUdpBus)
{
    bus = new UdpStateBus(options.MulticastGroup, options.MulticastPort);
    Console.WriteLine($"  UDP 狀態匯流排:{options.MulticastGroup}:{options.MulticastPort}(JSON,25 Hz)");
}

NmeaGateway? nmea = null;
if (options.EnableNmea)
{
    var sink = new UdpNmeaSender(new[]
    {
        new IPEndPoint(IPAddress.Loopback, options.NmeaPort),
        new IPEndPoint(IPAddress.Parse(options.NmeaMulticastGroup), options.NmeaMulticastPort),
    });
    nmea = new NmeaGateway(engine, sink, startUtc);
    Console.WriteLine($"  NMEA 0183:udp://127.0.0.1:{options.NmeaPort} 與 {options.NmeaMulticastGroup}:{options.NmeaMulticastPort}(HDT/ROT/RSA 10 Hz,GGA/RMC/VTG/VBW/DPT/MWV/ALR 1 Hz,指示值;目標 TTM 1 Hz、AIS VDM 位置 10 s/靜態 6 min,起始 UTC {startUtc:O})");
}

OwnShipState? latest = null;
engine.Broadcast += (e, state) =>
{
    latest = state;
    if (ws is null && bus is null) return;
    var frame = ContractJson.SerializeToUtf8Bytes(state);
    ws?.Publish(frame);
    bus?.Publish(frame);
};
// 事件框與結束時的評估框(情境引擎設計第 5.2 節)
engine.EventRaised += (e, ev) =>
{
    if (ws is null) return;
    ws.PublishEvent(ContractJson.SerializeToUtf8Bytes(new { type = "event", @event = ev }));
    if (ev.Kind == SimEventKinds.ExerciseEnded && e.LatestSummary is { } summary)
        ws.PublishEvent(ContractJson.SerializeToUtf8Bytes(new { type = "assessment", final = summary.Final, summary }));
};
// 情境載入被拒(不進紀錄與雜湊的帶外事件):以同樣的事件框送教官站,教官站據此把載入標為失敗並顯示問題清單
engine.ScenarioLoadRejected += (_, ev) => ws?.PublishEvent(ContractJson.SerializeToUtf8Bytes(new { type = "event", @event = ev }));

if (options.Duration > 0)
    engine.Stepped += e => { if (e.Time >= options.Duration) shutdown.Request("duration"); };
if (stopFile is not null)
{
    if (stopFile.RemovedStale) Console.WriteLine($"  已刪除殘留的停止檔:{stopFile.Path}");
    Console.WriteLine($"  停止檔:{stopFile.Path}(每秒檢查,存在即正常結束並寫完紀錄 footer;stop-all 腳本寫入)");
    _ = stopFile.RunAsync(TimeSpan.FromSeconds(1), () => shutdown.Request("stopFile"), shutdown.Token);
}
shutdown.Requested += reason => Console.WriteLine($"收到停止請求({reason}),結束中…");

var runner = new RealtimeRunner(engine);
var thread = new Thread(() => runner.Run(shutdown.Token)) { Name = "SimCore", IsBackground = true };
thread.Start();
Console.WriteLine("執行中(Ctrl+C / SIGTERM / 停止檔 / shutdown 指令結束)…");

var lastTick = 0L;
while (!shutdown.IsRequested)
{
    try { await Task.Delay(1000, shutdown.Token); } catch (OperationCanceledException) { break; }
    var s = latest;
    if (s is null) continue;
    var rate = s.Tick - lastTick;
    lastTick = s.Tick;
    Console.WriteLine(
        $"t={s.T,8:F1}s  HDG {s.Heading,6:F1}  COG {s.Cog,6:F1}  SOG {s.Sog,5:F1}kn  STW {s.Stw,5:F1}  ROT {s.Rot,6:F1}  舵 {s.Rudder,5:F1}/{s.RudderOrder,5:F1}  rpm {s.Rpm,6:F1}  {s.Telegraph}  UKC {s.DepthBelowKeel,5:F1}m  ×{engine.TimeScale:F1}{(engine.Frozen ? " 凍結" : "")}  steps/s {rate}  ws {ws?.ClientCount ?? 0}{(runner.DroppedSteps > 0 ? $"  掉拍 {runner.DroppedSteps}" : "")}{(s.Targets is { Count: > 0 } tg ? $"  目標 {tg.Count}(最近 {tg.MinBy(x => x.RangeNm)!.Id} {tg.Min(x => x.RangeNm):F2} nm,CPA {tg.MinBy(x => x.CpaNm)!.CpaNm:F2} nm)" : "")}{(s.Flags?.Collision == true ? "  碰撞" : "")}{(s.Alerts is { Count: > 0 } al ? $"  警報 {al.Count}(未確認 {al.Count(a => a.State is SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged or SimosaBRM.SimCore.Alerts.AlertState.ActiveSilenced or SimosaBRM.SimCore.Alerts.AlertState.RectifiedUnacknowledged)}{(al.Any(a => a.Audible) ? ",聲響" : "")})" : "")}{(s.Bnwas is { Active: true } bn ? $"  BNWAS {bn.Stage}/{bn.RemainingS:F0}s" : "")}{(s.Tugs is { Count: > 0 } tgs ? $"  拖船 {string.Join(" ", tgs.Select(t => $"{t.Id}:{t.Status}/{t.ForceT:F0}t@{t.DirectionDeg:F0}°"))}" : "")}{(s.Anchors?.Where(a => a.Status != "stowed").ToList() is { Count: > 0 } ans ? $"  錨 {string.Join(" ", ans.Select(a => $"{a.Side}:{a.Status}/{a.LengthShackles:F1}節/{a.TensionT:F1}t"))}" : "")}{(s.Berth is { } bst && (bst.Contacts.Count > 0 || bst.QuayContact) ? $"  碰墊 {bst.Contacts.Count}{(bst.QuayContact ? " 硬接觸" : "")}" : "")}{(s.Truth is { } tr && (Math.Abs(tr.HeadingErrorDeg) > 0.05 || tr.PositionErrorM > 1) ? $"  [真值 HDG {tr.Heading:F1} 誤差 {tr.HeadingErrorDeg:+0.0;-0.0}° / {tr.PositionErrorM:F0} m]" : "")}");
}

var joined = thread.Join(TimeSpan.FromSeconds(2));
// shutdown 指令的語意:先結束練習(定稿 summary、發 exerciseEnded 並寫入紀錄)再寫 footer;引擎執行緒已停,主執行緒可安全套用
if (joined && shutdown.EndExerciseRequested && !engine.Ended)
{
    engine.Enqueue(SimCommand.EndExercise(shutdown.Reason ?? "shutdown"));
    engine.ProcessPendingCommands();
}
nmea?.Dispose();
bus?.Dispose();
if (ws is not null) await ws.DisposeAsync();
if (recorder is not null)
{
    var footer = recorder.Closed ? recorder.Footer! : recorder.Close();
    Console.WriteLine($"紀錄已關閉:{recorder.Path}(tick {footer.FinalTick},狀態雜湊 {footer.StateHash[..16]}…,檔案 SHA-256 {footer.Sha256[..16]}…,summary 行 {footer.SummaryLines})");
}
shutdown.MarkFinished();
if (engine.LatestSummary is { } finalSummary)
    Console.WriteLine($"評估摘要:分數 {finalSummary.Assessment.Score:F0}/{finalSummary.Assessment.PassMark:F0},{(finalSummary.Assessment.Passed ? "通過" : "未通過")}{(finalSummary.Assessment.HardFailed ? "(硬性失敗)" : "")};事件 {finalSummary.Events.Total}");
Console.WriteLine($"結束(原因:{shutdown.Reason ?? "未知"})。");
return 0;
