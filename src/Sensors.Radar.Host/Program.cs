using System.Diagnostics;
using System.Globalization;
using SimosaBRM.Sensors.Radar;
using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.Sensors.Radar.Host;
using SimosaBRM.Sensors.Radar.Net;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

// 雷達/ARPA 感測器主控程式(規劃書第 5.2 節 Radar、第 7.2 節 MSC.192(79)):
// 訂閱 SimCore.Host 狀態 → X/S 兩台雷達影像與 ARPA → ws://0.0.0.0:8766/radar/{x|s} + NMEA TTM/TLL(UDP 10111/10112)。
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
RadarHostOptions options;
try { options = RadarHostOptions.Parse(args); }
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
if (options.Help)
{
    Console.WriteLine(RadarHostOptions.Usage);
    return 0;
}

var root = RepositoryPaths.TryFindRoot() ?? Directory.GetCurrentDirectory();
string? Resolve(string? p) => p is null ? null : Path.IsPathRooted(p) ? p : Path.Combine(root, p);

// 情境種子與起始 UTC(讀 data/scenarios/<id>.yaml;讀不到時以代號雜湊)
(ulong Seed, DateTime StartUtc) ScenarioInfo(string? scenarioId)
{
    var start = DateTime.UtcNow.Date;
    ulong seed = options.Seed ?? Fnv(scenarioId ?? "none");
    if (scenarioId is not null)
    {
        var path = Path.Combine(root, "data", "scenarios", scenarioId + ".yaml");
        if (File.Exists(path))
        {
            try
            {
                var sc = ScenarioLoader.Load(path);
                if (options.Seed is null) seed = (ulong)(uint)sc.Seed;
                if (sc.StartTimeUtc is { } st && DateTime.TryParse(st, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) start = dt;
            }
            catch (Exception ex) { Console.WriteLine($"  無法讀取情境 {scenarioId}:{ex.Message}(種子改用代號雜湊)"); }
        }
    }
    return (seed, start);
}

static ulong Fnv(string s)
{
    var h = 14695981039346656037UL;
    foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
    return h;
}

RadarEnvironment LoadEnvironment()
{
    var env = RadarEnvironment.Load(Resolve(options.Areas));
    if (Resolve(options.Environment) is { } ef)
    {
        if (!File.Exists(ef)) throw new FileNotFoundException($"找不到雷達環境檔:{ef}");
        env.AddFile(File.ReadAllText(ef), Path.GetFileName(ef));
    }
    return env;
}

List<RadarUnit> BuildUnits(OwnShipState s, ulong seed)
{
    ShipParticulars? ship = null;
    try { ship = ShipParticulars.LoadFromDataRoot(root, s.ShipId); }
    catch (Exception ex) { Console.WriteLine($"  無法讀取船舶資料 {s.ShipId}:{ex.Message}(天線高度用預設值)"); }
    var draft = s.Draft is { } d ? (d.Fore + d.Aft) / 2.0 : (double?)null;
    var list = new List<RadarUnit>();
    foreach (var id in options.Radars)
    {
        var cfg = id == "x" ? RadarConfig.XBand() : RadarConfig.SBand();
        if (ship is not null) cfg.ApplyParticulars(ship, s.Loading, draft);
        cfg.Rpm = options.Rpm;
        cfg.DisplayCells = options.Cells;
        var unit = new RadarUnit(cfg, seed, LoadEnvironment());
        if (options.Guard is { } g) unit.Controls.Guard = new GuardZone(true, g.Inner, g.Outer);
        list.Add(unit);
        Console.WriteLine($"  雷達 {unit.RadarLetter}:{cfg.FrequencyGHz} GHz、波束 {cfg.BeamwidthDeg}°、{cfg.Rpm} rpm、{cfg.Spokes} 線 × {cfg.DisplayCells} 格、" +
                          $"天線水線上 {cfg.AntennaHeightM:F1} m(船舯 {cfg.AntennaFwdM:F1} m)、陰影扇區 {string.Join(",", cfg.ShadowSectors.Select(x => $"{x.FromRelDeg:F0}–{x.ToRelDeg:F0}°"))}、" +
                          $"陸地 {unit.Environment.LandPolygonCount} 筆、種子 {seed}");
    }
    return list;
}

var server = new RadarWebSocketServer(options.Port, options.Radars, allInterfaces: !options.LocalhostOnly);
server.Log += m => Console.WriteLine(m);
server.Start();
Console.WriteLine($"雷達 WebSocket:{server.Prefix}{{{string.Join("|", options.Radars)}}}");
NmeaOutputOptions? nmea = null;
if (options.Nmea)
{
    nmea = new NmeaOutputOptions { Host = options.NmeaHost, Talker = options.Talker };
    nmea.Ports["x"] = options.NmeaPortX;
    nmea.Ports["s"] = options.NmeaPortS;
    Console.WriteLine($"NMEA ARPA:${options.Talker}TTM/${options.Talker}TLL 1 Hz → udp://{options.NmeaHost}:{options.NmeaPortX}(X)、:{options.NmeaPortS}(S)(NMEA 閘道真值 $RATTM 在 10110)");
}

RadarService? service = null;
string? scenarioId = null;
var startUtc = DateTime.UtcNow.Date;
var nextReport = 0.0;
var cpaErrors = new List<(double T, string Radar, int Id, string Truth, double Err, bool Stable)>();
var trackCounts = new List<(double T, string Radar, int Tracking, int Total)>();
var wall = Stopwatch.StartNew();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (options.Duration > 0) cts.CancelAfter(TimeSpan.FromSeconds(options.Duration));
var simStartT = double.NaN;
var lastT = 0.0;
var client = new SimStateClient(options.Sim);
client.Log += m => Console.WriteLine(m);

void OnState(OwnShipState s)
{
    var id = s.Scenario?.Id;
    if (service is null || !string.Equals(id, scenarioId, StringComparison.Ordinal))
    {
        scenarioId = id;
        var (seed, st) = ScenarioInfo(id);
        startUtc = st;
        Console.WriteLine($"情境 {id ?? "(無)"}、船 {s.ShipId} {ContractJson.EnumName(s.Loading)}");
        var units = BuildUnits(s, seed);
        if (service is null)
        {
            service = new RadarService(units, server, nmea, t => startUtc.AddSeconds(t)) { ForceDisplay = options.ForceDisplay };
            // 學員控制指令 → 核心 radarControl(同一條 WebSocket 送回;規劃書第 8.3 節第 7 類、radar-arpa.md 第 5.1 節)
            if (options.ForwardControl) service.ForwardControl = cmd => client.Send(ContractJson.SerializeToUtf8Bytes(cmd));
            service.EventRaised += (u, e) => Console.WriteLine($"[{u.RadarLetter} {e.T,7:F1}s] {e.Kind}:{e.Message}");
            service.TracksPublished += OnTracks;
        }
        else service.SetUnits(units);
        nextReport = s.T;
    }
    if (double.IsNaN(simStartT)) simStartT = s.T;
    lastT = s.T;
    service.OnState(s);
}

void OnTracks(RadarUnit unit, RadarScene scene)
{
    var tracking = unit.Arpa.Tracks.Count(t => t.State == TrackState.Tracking);
    trackCounts.Add((scene.T, unit.Config.Id, tracking, unit.Arpa.Tracks.Count));
    var matches = ArpaTruthComparison.Compare(unit, scene);
    foreach (var m in matches.Where(m => m.State == "tracking"))
        cpaErrors.Add((scene.T, unit.Config.Id, m.TrackId, m.TruthId, m.CpaErrorNm, m.Stable));
    if (options.TruthReport <= 0 || unit != service!.Units[0] || scene.T + 1e-6 < nextReport) return;
    nextReport = scene.T + options.TruthReport;
    Console.WriteLine($"--- t={scene.T:F0}s 追蹤 {tracking}/{unit.Arpa.Tracks.Count}({unit.RadarLetter});真值目標 {scene.TruthTargets?.Count ?? 0}");
    foreach (var m in matches)
        Console.WriteLine($"    {unit.RadarLetter}{m.TrackId:00} ↔ {m.TruthId,-6} {m.State,-9} 追蹤 {m.AgeS,5:F0}s 位置誤差 {m.PositionErrorM,5:F0} m  " +
                          $"CPA {m.ArpaCpaNm:F3}/{m.TruthCpaNm:F3} nm(誤差 {m.CpaErrorNm:F3})  TCPA {m.ArpaTcpaMin:F1}/{m.TruthTcpaMin:F1} 分  航向誤差 {m.CourseErrorDeg:F1}° 速度誤差 {m.SpeedErrorKn:F2} kn");
}

Console.WriteLine($"連線 SimCore:{options.Sim}{(options.ForwardControl ? "(學員雷達操作轉送為 radarControl 指令)" : "")}");
try { await client.RunAsync(OnState, cts.Token); }
catch (OperationCanceledException) { }

// 結束摘要(效能與 ARPA 對真值)
var wallS = wall.Elapsed.TotalSeconds;
Console.WriteLine($"結束:牆鐘 {wallS:F0} s、狀態幀 {client.FramesReceived}、模擬時間 {(double.IsNaN(simStartT) ? 0 : lastT - simStartT):F0} s、轉送控制指令 {client.CommandsSent}");
if (service is not null)
{
    foreach (var u in service.Units)
    {
        var cpu = service.CpuSeconds(u.Config.Id);
        Console.WriteLine($"  雷達 {u.RadarLetter}:方位線 {u.TotalSpokes}、處理 {cpu:F1} s CPU(平均 {cpu / Math.Max(wallS, 1e-9) * 100:F1}% 單核)、追蹤 {u.Arpa.Tracks.Count(t => t.State == TrackState.Tracking)}/{u.Arpa.Tracks.Count}");
        var errs = cpaErrors.Where(e => e.Radar == u.Config.Id).ToList();
        var stable = errs.Where(e => e.Stable).ToList();
        if (errs.Count > 0)
            Console.WriteLine($"    CPA 誤差(追蹤中,全部 {errs.Count} 筆):平均 {errs.Average(e => e.Err):F4} nm、最大 {errs.Max(e => e.Err):F4} nm");
        if (stable.Count > 0)
            Console.WriteLine($"    CPA 誤差(穩定追蹤 ≥ 3 分,{stable.Count} 筆):平均 {stable.Average(e => e.Err):F4} nm、最大 {stable.Max(e => e.Err):F4} nm");
        var lastCount = trackCounts.LastOrDefault(c => c.Radar == u.Config.Id);
        if (lastCount != default) Console.WriteLine($"    最後追蹤數 {lastCount.Tracking}(t={lastCount.T:F0}s)");
        foreach (var g in errs.GroupBy(e => (e.Id, e.Truth)).OrderBy(g => g.Key.Id))
        {
            var last = g.Last();
            Console.WriteLine($"    {u.RadarLetter}{g.Key.Id:00} ↔ {g.Key.Truth}:最後 CPA 誤差 {last.Err:F4} nm(t={last.T:F0}s,{(last.Stable ? "穩定" : "趨勢")})");
        }
    }
    service.Dispose();
}
await server.DisposeAsync();
return 0;
