using System.Globalization;
using System.Net;
using SimosaBRM.Gateway.Nmea;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Host;
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

// ---------------------------------------------------------------- 重播模式
if (options.Replay is { } replayPath)
{
    var full = Path.IsPathRooted(replayPath) ? replayPath : Path.Combine(root, replayPath);
    Console.WriteLine($"重播:{full}");
    // 依紀錄標頭的模型名稱選擇動力學(紀錄用暫代模型時不會因預設 MMG 而雜湊不一致)
    var recordedDynamics = RecordReader.Read(full).Header.Dynamics;
    var replayFactory = DynamicsFactory(recordedDynamics.StartsWith("mmg", StringComparison.OrdinalIgnoreCase) ? "mmg" : "placeholder");
    var result = Replayer.Replay(full, LoadShip, replayFactory);
    Console.WriteLine($"  完整性雜湊:{(result.IntegrityOk ? "一致" : "不一致或缺少 footer")}");
    Console.WriteLine($"  動力學模型:紀錄 {result.DynamicsRecorded} / 目前 {result.DynamicsUsed}");
    Console.WriteLine($"  重播指令數:{result.InputsReplayed},最終 tick {result.FinalTick}");
    Console.WriteLine($"  紀錄雜湊:{result.ExpectedHash}");
    Console.WriteLine($"  重播雜湊:{result.ActualHash}");
    if (result.Warning is not null) Console.WriteLine($"  警告:{result.Warning}");
    Console.WriteLine(result.HashMatches ? "結果:狀態雜湊一致" : "結果:狀態雜湊不一致");
    return result.HashMatches && result.IntegrityOk ? 0 : 1;
}

// ---------------------------------------------------------------- 即時模式
var scenarioPath = Path.IsPathRooted(options.Scenario) ? options.Scenario : Path.Combine(root, options.Scenario);
var scenario = ScenarioLoader.Load(scenarioPath);
if (options.Ship is { } shipOverride) scenario.Ship.Id = shipOverride.ToUpperInvariant();
if (options.Loading is { } loadingOverride) scenario.Ship.Loading = loadingOverride;
if (options.TimeScale is { } ts) scenario.TimeScale = ts;
scenario.Validate();

var ship = LoadShip(scenario.Ship.Id);
SimulationEngine engine;
try { engine = new SimulationEngine(ship, scenario, DynamicsFactory(options.Dynamics)); }
catch (FileNotFoundException ex) when (options.Dynamics == "mmg")
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("(或以 --dynamics placeholder 使用暫代模型)");
    return 3;
}
engine.ScenarioLoadRequested += (e, path) =>
{
    try
    {
        var p = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        var sc = ScenarioLoader.Load(p);
        e.LoadScenario(sc, string.Equals(sc.Ship.Id, e.Ship.Id, StringComparison.OrdinalIgnoreCase) ? null : LoadShip(sc.Ship.Id));
        Console.WriteLine($"已載入情境 {sc.Id}");
    }
    catch (Exception ex) { Console.Error.WriteLine($"載入情境失敗:{ex.Message}"); }
};
engine.Grounded += e => Console.WriteLine($"[t={e.Time:F1}] 擱淺:UKC ≤ 0");
engine.TrafficEventRaised += (e, ev) => Console.WriteLine($"[t={ev.T:F1}] 交通事件 {ev.Kind}{(ev.TargetId is null ? "" : $" {ev.TargetId}")}:{ev.Message}");

var startUtc = scenario.StartTimeUtc is { } st
    ? DateTime.Parse(st, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
    : DateTime.UtcNow;

Console.WriteLine($"Simosa BRM SimCore Host — {ship.Name} ({ship.Id}),裝載 {scenario.Ship.Loading},情境 {scenario.Id}");
Console.WriteLine($"  dt = {engine.Dt} s(50 Hz)、廣播 25 Hz、快照每 {engine.Options.AutoSnapshotIntervalTicks * engine.Dt:F0} s、動力學 {engine.Dynamics.ModelName}、種子 {scenario.Seed}");
if (scenario.Targets.Count > 0)
    Console.WriteLine($"  目標船 {scenario.Targets.Count} 艘:{string.Join("、", scenario.Targets.Select(t => $"{t.Id}({t.Behaviour.Mode}{(t.Trigger is { Type: not SimosaBRM.SimCore.Scenario.TargetTriggerType.None } ? ",觸發" : "")})"))};最小 CPA 門檻 {scenario.Assessment.MinCpaNm} nm");

RecordWriter? recorder = null;
if (options.Record)
{
    var recPath = RecordWriter.DefaultPath(RepositoryPaths.RecordsDirectory(root), scenario.Id);
    recorder = new RecordWriter(recPath, engine);
    Console.WriteLine($"  紀錄:{recPath}");
}

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
    Console.WriteLine($"  NMEA 0183:udp://127.0.0.1:{options.NmeaPort} 與 {options.NmeaMulticastGroup}:{options.NmeaMulticastPort}(HDT/ROT/RSA 10 Hz,GGA/RMC/VTG/VBW/DPT/MWV 1 Hz,目標 TTM 1 Hz、AIS VDM 位置 10 s/靜態 6 min,起始 UTC {startUtc:O})");
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
        $"t={s.T,8:F1}s  HDG {s.Heading,6:F1}  COG {s.Cog,6:F1}  SOG {s.Sog,5:F1}kn  STW {s.Stw,5:F1}  ROT {s.Rot,6:F1}  舵 {s.Rudder,5:F1}/{s.RudderOrder,5:F1}  rpm {s.Rpm,6:F1}  {s.Telegraph}  UKC {s.DepthBelowKeel,5:F1}m  ×{engine.TimeScale:F1}{(engine.Frozen ? " 凍結" : "")}  steps/s {rate}  ws {ws?.ClientCount ?? 0}{(runner.DroppedSteps > 0 ? $"  掉拍 {runner.DroppedSteps}" : "")}{(s.Targets is { Count: > 0 } tg ? $"  目標 {tg.Count}(最近 {tg.MinBy(x => x.RangeNm)!.Id} {tg.Min(x => x.RangeNm):F2} nm,CPA {tg.MinBy(x => x.CpaNm)!.CpaNm:F2} nm)" : "")}{(s.Flags?.Collision == true ? "  碰撞" : "")}");
}

thread.Join(TimeSpan.FromSeconds(2));
nmea?.Dispose();
bus?.Dispose();
if (ws is not null) await ws.DisposeAsync();
if (recorder is not null)
{
    var footer = recorder.Close();
    Console.WriteLine($"紀錄已關閉:{recorder.Path}(tick {footer.FinalTick},狀態雜湊 {footer.StateHash[..16]}…,檔案 SHA-256 {footer.Sha256[..16]}…)");
}
Console.WriteLine("結束。");
return 0;
