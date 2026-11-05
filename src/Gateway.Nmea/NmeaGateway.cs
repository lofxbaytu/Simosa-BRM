using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 掛在引擎上,依句型速率產生 NMEA(HDT/THS/ROT/RSA 10 Hz,其餘 1 Hz;規劃書第 5.3 節),時間 = 情境起始 UTC + 模擬時間。
/// 位置/航向/水深/STW 取 state 的指示值(感測器故障時與真值不同或無效,見 NmeaEncoder);警報 ALR 與 ALF 每個 state.alerts[] 的警報 1 Hz,
/// ALC 週期清單每 <see cref="AlcIntervalS"/> 秒(第 1 秒起)與清單/修訂改變的那一秒(IEC 62923-1:接收端據此重新同步;無警報時為 0 項)。
/// 自船 AIS !AIVDO:MMSI 取情境 ship.mmsi,無則 particulars.mmsi;皆無則不送並以 <see cref="Log"/> 記一次(每個情境);類型 1 位置每 10 s、類型 5 靜態於第 1 秒後每 6 分。
/// 目標船:每目標 TTM 1 Hz;AIS !AIVDM 類型 1 位置報告每 10 s(固定,不依航速縮短)、類型 5 靜態資料於出現後數秒先送一次再每 6 分,
/// 各目標依其在 targets[] 的序號錯開秒數;AIS 關閉或不發送(錯誤注入 silent)的目標只有 TTM。在引擎執行緒上執行;輸出端應避免阻塞。
/// 起始 UTC:建構時給定則固定(測試);未給(主控程序)則每步取引擎的 <see cref="SimulationEngine.StartUtc"/>,
/// loadScenario 切換情境後 NMEA 時間與 state.utc 同步改為新情境的起點(<c>nmea_check</c> 的位置/時間一致性檢查依此);警報追蹤與 ALC 序號同時重設。
/// </summary>
public sealed class NmeaGateway : IDisposable
{
    private readonly SimulationEngine _engine;
    private readonly INmeaSink _sink;
    private readonly int _fastDivider;
    private readonly int _slowDivider;
    private readonly DateTime? _fixedStartUtc;
    private readonly DateTime _fallbackStartUtc;

    /// <summary>ALF 用的每個警報追蹤:上次狀態、最近一次狀態改變的模擬秒、修訂計數(1–99)。</summary>
    private sealed class AlertTrack
    {
        public AlertState State;
        public double ChangeS;
        public int Revision = 1;
    }

    private readonly Dictionary<string, AlertTrack> _alerts = new(StringComparer.Ordinal);
    private object? _scenarioRef;
    private int _alfSeq;
    private int _alcSeq;
    private string? _alcSignature;
    private bool _loggedNoMmsi;

    /// <summary>練習開始的 UTC 時刻(固定值,或目前引擎情境的起點;引擎無起點時為建構時的實際時鐘)</summary>
    public DateTime StartUtc => _fixedStartUtc ?? _engine.StartUtc ?? _fallbackStartUtc;
    public long SentencesSent { get; private set; }
    /// <summary>AIS 位置報告間隔(秒;目標與自船)</summary>
    public int AisPositionIntervalS { get; init; } = 10;
    /// <summary>AIS 靜態資料間隔(秒;目標與自船)</summary>
    public int AisStaticIntervalS { get; init; } = 360;
    /// <summary>ALC 週期清單間隔(秒;第 1 秒先送一次,之後每隔此秒數;清單改變時另送)</summary>
    public int AlcIntervalS { get; init; } = 30;
    /// <summary>自船 AIS 目前使用的 MMSI(情境 ship.mmsi ?? particulars.mmsi;null = 不送 VDO)</summary>
    public int? OwnMmsi => _engine.Scenario.Ship.Mmsi ?? _engine.Ship.Mmsi;
    /// <summary>非句型的訊息(例如自船 MMSI 未設定、VDO 不送);在引擎執行緒上發出</summary>
    public event Action<string>? Log;

    /// <param name="startUtc">固定的起始 UTC;null = 跟隨引擎(情境 startTimeUtc,切換情境時更新)</param>
    public NmeaGateway(SimulationEngine engine, INmeaSink sink, DateTime? startUtc = null, double fastHz = 10.0, double slowHz = 1.0)
    {
        _engine = engine;
        _sink = sink;
        _fixedStartUtc = startUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null;
        _fallbackStartUtc = DateTime.UtcNow;
        _fastDivider = Math.Max(1, (int)Math.Round(1.0 / (engine.Dt * fastHz)));
        _slowDivider = Math.Max(1, (int)Math.Round(1.0 / (engine.Dt * slowHz)));
        engine.Stepped += OnStepped;
    }

    private void OnStepped(SimulationEngine e)
    {
        var fast = e.Tick % _fastDivider == 0;
        var slow = e.Tick % _slowDivider == 0;
        if (!fast && !slow) return;
        var state = e.BuildState();
        var utc = StartUtc.AddSeconds(e.Time);
        var batch = new List<string>(12);
        if (fast) batch.AddRange(NmeaEncoder.FastSentences(state));
        if (slow)
        {
            if (!ReferenceEquals(_scenarioRef, e.Scenario)) ResetForScenario(e.Scenario);
            var sec = e.Tick / _slowDivider; // 整數秒(避免浮點時間的取整誤差)
            batch.AddRange(NmeaEncoder.SlowSentences(state, utc));
            batch.AddRange(NmeaEncoder.AlrSentences(state, utc)); // 警報狀態 1 Hz(BAM;每個非 normal 的警報一句)
            AppendAlertSentences(state, e.Time, sec, batch);
            AppendOwnShipAis(state, e, utc, sec, batch);
            if (state.Targets is { Count: > 0 } targets)
            {
                batch.AddRange(NmeaEncoder.TtmSentences(state, utc));
                for (var i = 0; i < targets.Count; i++)
                {
                    var ais = targets[i].Ais;
                    if (ais is null) continue;
                    // 位置:第 i 艘在 sec ≡ i (mod 10);靜態:第 2+i 秒先送一次(讓 ECDIS 儘早取得船名),之後每 6 分
                    if ((sec - i) % AisPositionIntervalS == 0) batch.AddRange(AisEncoder.PositionReport(ais, utc, i % 2 == 0 ? 'A' : 'B'));
                    var staticAge = sec - 2 - i;
                    if (staticAge >= 0 && staticAge % AisStaticIntervalS == 0) batch.AddRange(AisEncoder.StaticAndVoyage(ais, i % 9 + 1, i % 2 == 0 ? 'A' : 'B'));
                }
            }
        }
        SentencesSent += batch.Count;
        _sink.Send(batch);
    }

    /// <summary>切換情境:清掉警報追蹤、序號與 MMSI 提示(新情境可能有不同的 ship.mmsi)。</summary>
    private void ResetForScenario(object scenario)
    {
        _scenarioRef = scenario;
        _alerts.Clear();
        _alfSeq = 0;
        _alcSeq = 0;
        _alcSignature = null;
        _loggedNoMmsi = false;
    }

    /// <summary>
    /// ALF(每個 state.alerts[] 的警報一句;狀態改變時修訂計數 +1、時刻改為改變時刻)與 ALC(第 1 秒、每 AlcIntervalS 秒、清單或修訂改變時)。
    /// 改變時刻:首次出現用 raisedAt_s,確認用 acknowledgedAt_s(核心提供精確值),其他轉換(靜音、解除)用偵測到的整秒。
    /// </summary>
    private void AppendAlertSentences(OwnShipState state, double timeS, long sec, List<string> batch)
    {
        var alerts = state.Alerts ?? Array.Empty<AlertStatus>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<(int Number, int Revision)>(alerts.Count);
        foreach (var a in alerts)
        {
            seen.Add(a.Id);
            if (!_alerts.TryGetValue(a.Id, out var tr))
            {
                tr = new AlertTrack { State = a.State, ChangeS = a.RaisedAtS };
                _alerts[a.Id] = tr;
            }
            else if (tr.State != a.State)
            {
                tr.State = a.State;
                tr.ChangeS = a.State == AlertState.ActiveAcknowledged && a.AcknowledgedAtS is { } ack ? ack : timeS;
                tr.Revision = tr.Revision >= 99 ? 1 : tr.Revision + 1;
            }
            batch.Add(NmeaEncoder.Alf(a, StartUtc.AddSeconds(tr.ChangeS), _alfSeq, tr.Revision));
            _alfSeq = (_alfSeq + 1) % 10;
            entries.Add((a.Number, tr.Revision));
        }
        foreach (var gone in _alerts.Keys.Where(k => !seen.Contains(k)).ToArray()) _alerts.Remove(gone);
        var signature = string.Join(";", entries.Select(x => $"{x.Number}:{x.Revision}"));
        var periodic = (sec - 1) % AlcIntervalS == 0;
        if (periodic || signature != _alcSignature)
        {
            batch.AddRange(NmeaEncoder.Alc(entries, _alcSeq));
            _alcSeq = (_alcSeq + 1) % 100;
            _alcSignature = signature;
        }
    }

    /// <summary>自船 AIS VDO:類型 1 位置在 sec ≡ 0 (mod 10),類型 5 靜態於第 1 秒與其後每 6 分;無 MMSI 時每個情境記一次 log。</summary>
    private void AppendOwnShipAis(OwnShipState state, SimulationEngine e, DateTime utc, long sec, List<string> batch)
    {
        if (OwnMmsi is not { } mmsi)
        {
            if (!_loggedNoMmsi)
            {
                _loggedNoMmsi = true;
                Log?.Invoke($"自船 AIS !AIVDO 不送:情境 {e.Scenario.Id} 無 ship.mmsi,且 data/ships/{e.Ship.Id}/particulars.json 無 mmsi");
            }
            return;
        }
        if (sec % AisPositionIntervalS == 0) batch.AddRange(NmeaEncoder.OwnPositionReport(state, e.Ship, mmsi, utc));
        if ((sec - 1) % AisStaticIntervalS == 0) batch.AddRange(NmeaEncoder.OwnStaticAndVoyage(state, e.Ship, mmsi, 9));
    }

    public void Dispose()
    {
        _engine.Stepped -= OnStepped;
        _sink.Dispose();
    }
}
