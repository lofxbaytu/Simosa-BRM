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
    if (options.Rescore && parameterSet is null)
    {
        Console.Error.WriteLine("--rescore 需要 --parameters <path>");
        return 2;
    }
    var result = Replayer.Replay(full, LoadShip, replayFactory, rescore: options.Rescore ? parameterSet : null);
    Console.WriteLine($"  完整性雜湊:{(result.IntegrityOk ? "一致" : "不一致或缺少 footer")}");
    Console.WriteLine($"  動力學模型:紀錄 {result.DynamicsRecorded} / 目前 {result.DynamicsUsed}");
    Console.WriteLine($"  重播指令數:{result.InputsReplayed},最終 tick {result.FinalTick}");
    Console.WriteLine($"  紀錄雜湊:{result.ExpectedHash}");
    Console.WriteLine($"  重播雜湊:{result.ActualHash}");
    Console.WriteLine($"  事件序列:紀錄 {result.EventsRecorded} / 重播 {result.EventsReplayed},{(result.EventSequenceMatches ? "一致" : "不一致:" + result.FirstEventMismatch)}");
    if (result.SummaryMatches is { } sm) Console.WriteLine($"  評估摘要:{(sm ? "與紀錄一致" : "與紀錄不一致")}");
    if (options.Rescore && result.Summary is { } rs)
    {
        // 重新評分:物理、交通、時間表與雜湊仍依紀錄的原情境重播(雜湊與事件序列必須一致),只有評估模組以新參數集重算
        Console.WriteLine($"  重新評分(參數集 {rs.ParameterSet?.Id} {rs.ParameterSet?.Version}):分數 {rs.Assessment.Score:F0}/{rs.Assessment.PassMark:F0},{(rs.Assessment.Passed ? "通過" : "未通過")}");
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
void PrintIssues(Scenario sc)
{
    var issues = ScenarioValidator.Check(sc, ship, Path.Combine(root, "data", "scenarios", sc.Id));
    foreach (var issue in issues) Console.WriteLine($"  情境檢查 {issue}");
    if (sc.ParameterSetInfo is { } ps) Console.WriteLine($"  參數集 {ps.Id} {ps.Version}(sha256 {ps.Sha256[..16]}…),解析 {ps.Resolved.Count} 個引用");
}
PrintIssues(scenario);
if (options.Validate)
{
    var errors = ScenarioValidator.Check(scenario, ship).Count(i => i.Severity == ScenarioIssueSeverity.Error);
    Console.WriteLine(errors == 0 ? "情境檢查:無錯誤" : $"情境檢查:{errors} 個錯誤");
    return errors == 0 ? 0 : 1;
}
SimulationEngine engine;
try { engine = new SimulationEngine(ship, scenario, DynamicsFactory(options.Dynamics)); }
catch (FileNotFoundException ex) when (options.Dynamics == "mmg")
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("(或以 --dynamics placeholder 使用暫代模型)");
    return 3;
}
// 紀錄(規劃書第 5.3 節):每份紀錄一個 header 情境;loadScenario 時紀錄器在引擎重設前自動關閉,這裡再為新情境開新檔
RecordWriter? recorder = null;
string RecordPathFor(string scenarioId) => options.RecordPath is { } rp && recorder is null
    ? (Path.IsPathRooted(rp) ? rp : Path.Combine(root, rp))
    : RecordWriter.DefaultPath(RepositoryPaths.RecordsDirectory(root), scenarioId);
if (options.Record)
{
    recorder = new RecordWriter(RecordPathFor(scenario.Id), engine);
    Console.WriteLine($"  紀錄:{recorder.Path}");
}
engine.ScenarioLoadRequested += (e, path) =>
{
    try
    {
        var p = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        var sc = ScenarioLoader.Load(p, parameterSet);
        e.LoadScenario(sc, string.Equals(sc.Ship.Id, e.Ship.Id, StringComparison.OrdinalIgnoreCase) ? null : LoadShip(sc.Ship.Id));
        Console.WriteLine($"已載入情境 {sc.Id}");
        PrintIssues(sc);
        if (recorder is { Closed: true } closed)
        {
            var footer = closed.Footer!;
            Console.WriteLine($"紀錄已關閉(情境切換):{closed.Path}(tick {footer.FinalTick},狀態雜湊 {footer.StateHash[..16]}…)");
            recorder = new RecordWriter(RecordPathFor(sc.Id), e);
            Console.WriteLine($"  紀錄(新情境):{recorder.Path}");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"載入情境失敗:{ex.Message}"); }
};
engine.EventRaised += (e, ev) => Console.WriteLine($"[t={ev.T:F1}] 事件 {ev.Source}/{ev.Kind}{(ev.ItemId is null ? "" : $" {ev.ItemId}")}{(ev.TargetId is null ? "" : $" {ev.TargetId}")}{(ev.Message is null ? "" : $":{ev.Message}")}");

var startUtc = scenario.StartTimeUtc is { } st
    ? DateTime.Parse(st, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
    : DateTime.UtcNow;

Console.WriteLine($"Simosa BRM SimCore Host — {ship.Name} ({ship.Id}),裝載 {scenario.Ship.Loading},情境 {scenario.Id}");
Console.WriteLine($"  dt = {engine.Dt} s(50 Hz)、廣播 25 Hz、快照每 {engine.Options.AutoSnapshotIntervalTicks * engine.Dt:F0} s、動力學 {engine.Dynamics.ModelName}、種子 {scenario.Seed}");
if (scenario.Targets.Count > 0)
    Console.WriteLine($"  目標船 {scenario.Targets.Count} 艘:{string.Join("、", scenario.Targets.Select(t => $"{t.Id}({t.Behaviour.Mode}{(t.Trigger is { Type: not SimosaBRM.SimCore.Scenario.TargetTriggerType.None } ? ",觸發" : "")})"))};最小 CPA 門檻 {scenario.Assessment.MinCpaNm} nm");

if (scenario.Bnwas.Mode != BnwasMode.Off || scenario.Alerts.OffHeadingDeg is not null || scenario.Alerts.HeadingMonitorDeg is not null || scenario.Alerts.ShallowWaterM is not null)
    Console.WriteLine($"  BNWAS {ContractJson.EnumName(scenario.Bnwas.Mode)}(休眠 {scenario.Bnwas.DormantS:F0} s);警報門檻:偏航 {scenario.Alerts.OffHeadingDeg?.ToString("F0") ?? "關"}°、航向監視 {scenario.Alerts.HeadingMonitorDeg?.ToString("F0") ?? "關"}°、淺水 {scenario.Alerts.ShallowWaterM?.ToString("F1") ?? "關"} m;warning 升級 {scenario.Alerts.EscalateS:F0} s、靜音 {scenario.Alerts.SilenceS:F0} s");
if (scenario.Timeline.Count > 0 || scenario.Route is not null || scenario.Zones.Count > 0)
    Console.WriteLine($"  情境引擎:時間表 {scenario.Timeline.Count} 項、航線 {scenario.Route?.Waypoints.Count ?? 0} 航點、區域 {scenario.Zones.Count}、硬性失敗 {scenario.Assessment.HardFail.Count}/容許帶 {scenario.Assessment.Bands.Count}/必要事件 {scenario.Assessment.Required.Count}");

WebSocketStateServer? ws = null;
if (options.EnableWebSocket)
{
    ws = new WebSocketStateServer(engine, options.Port);
    ws.Log += msg => Console.WriteLine($"  [ws] {msg}");
    ws.Start();
    Console.WriteLine($"  WebSocket:{ws.Prefix!.Replace("http://", "ws://").Replace("*", "0.0.0.0")}(推送 OwnShipState JSON 每 40 ms;接收 SimCommand JSON)");
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

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (options.Duration > 0)
    engine.Stepped += e => { if (e.Time >= options.Duration) cts.Cancel(); };

var runner = new RealtimeRunner(engine);
var thread = new Thread(() => runner.Run(cts.Token)) { Name = "SimCore", IsBackground = true };
thread.Start();
Console.WriteLine("執行中(Ctrl+C 結束)…");

var lastTick = 0L;
while (!cts.IsCancellationRequested)
{
    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
    var s = latest;
    if (s is null) continue;
    var rate = s.Tick - lastTick;
    lastTick = s.Tick;
    Console.WriteLine(
        $"t={s.T,8:F1}s  HDG {s.Heading,6:F1}  COG {s.Cog,6:F1}  SOG {s.Sog,5:F1}kn  STW {s.Stw,5:F1}  ROT {s.Rot,6:F1}  舵 {s.Rudder,5:F1}/{s.RudderOrder,5:F1}  rpm {s.Rpm,6:F1}  {s.Telegraph}  UKC {s.DepthBelowKeel,5:F1}m  ×{engine.TimeScale:F1}{(engine.Frozen ? " 凍結" : "")}  steps/s {rate}  ws {ws?.ClientCount ?? 0}{(runner.DroppedSteps > 0 ? $"  掉拍 {runner.DroppedSteps}" : "")}{(s.Targets is { Count: > 0 } tg ? $"  目標 {tg.Count}(最近 {tg.MinBy(x => x.RangeNm)!.Id} {tg.Min(x => x.RangeNm):F2} nm,CPA {tg.MinBy(x => x.CpaNm)!.CpaNm:F2} nm)" : "")}{(s.Flags?.Collision == true ? "  碰撞" : "")}{(s.Alerts is { Count: > 0 } al ? $"  警報 {al.Count}(未確認 {al.Count(a => a.State is SimosaBRM.SimCore.Alerts.AlertState.ActiveUnacknowledged or SimosaBRM.SimCore.Alerts.AlertState.ActiveSilenced or SimosaBRM.SimCore.Alerts.AlertState.RectifiedUnacknowledged)}{(al.Any(a => a.Audible) ? ",聲響" : "")})" : "")}{(s.Bnwas is { Active: true } bn ? $"  BNWAS {bn.Stage}/{bn.RemainingS:F0}s" : "")}{(s.Truth is { } tr && (Math.Abs(tr.HeadingErrorDeg) > 0.05 || tr.PositionErrorM > 1) ? $"  [真值 HDG {tr.Heading:F1} 誤差 {tr.HeadingErrorDeg:+0.0;-0.0}° / {tr.PositionErrorM:F0} m]" : "")}");
}

thread.Join(TimeSpan.FromSeconds(2));
nmea?.Dispose();
bus?.Dispose();
if (ws is not null) await ws.DisposeAsync();
if (recorder is not null)
{
    var footer = recorder.Closed ? recorder.Footer! : recorder.Close();
    Console.WriteLine($"紀錄已關閉:{recorder.Path}(tick {footer.FinalTick},狀態雜湊 {footer.StateHash[..16]}…,檔案 SHA-256 {footer.Sha256[..16]}…,summary 行 {footer.SummaryLines})");
}
if (engine.LatestSummary is { } finalSummary)
    Console.WriteLine($"評估摘要:分數 {finalSummary.Assessment.Score:F0}/{finalSummary.Assessment.PassMark:F0},{(finalSummary.Assessment.Passed ? "通過" : "未通過")}{(finalSummary.Assessment.HardFailed ? "(硬性失敗)" : "")};事件 {finalSummary.Events.Total}");
Console.WriteLine("結束。");
return 0;
