using System.Buffers.Binary;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Alerts;

/// <summary>警報管理的可變狀態(進快照與雜湊)。</summary>
public sealed class AlertManagerState
{
    /// <summary>目前非 normal 的警報(依目錄順序)</summary>
    public List<AlertRecord> Alerts { get; set; } = new();
    /// <summary>偏航條件開始成立的 tick(−1 = 未成立;持續 offHeadingDelay_s 才發警報)</summary>
    public long OffHeadingSinceTick { get; set; } = -1;
    /// <summary>航向監視條件(滯後 1°)</summary>
    public bool HeadingMonitorActive { get; set; }
    /// <summary>淺水條件(滯後 +0.2 m)</summary>
    public bool ShallowActive { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsEmpty => Alerts.Count == 0 && OffHeadingSinceTick < 0 && !HeadingMonitorActive && !ShallowActive;
}

/// <summary>警報條件的輸入(引擎每步提供;皆為指示值)。</summary>
public readonly record struct AlertInputs(
    IReadOnlyList<string> Faults,
    bool LogInvalid,
    bool AutopilotEnabled,
    double AutopilotSetHeadingDeg,
    double HeadingDeg,
    bool HeadingValid,
    double MagneticHeadingDeg,
    double DepthBelowKeelM,
    bool DepthValid,
    int BnwasStage);

/// <summary>
/// 駕駛台警報管理(BAM,MSC.302(87);設計文件 docs/design/sensors-alerts.md 第 3 節)。每步依 <see cref="AlertInputs"/> 判定各警報條件,
/// 執行狀態機:成立 → activeUnacknowledged;silence → activeSilenced(<c>alerts.silence_s</c> 後重響);acknowledge → activeAcknowledged
/// (rectifiedUnacknowledged → normal);條件解除 → normal 或 rectifiedUnacknowledged;warning 未確認 <c>alerts.escalate_s</c> 升級為 alarm。
/// 事件以 (kind, record, message, data) 回呼引擎發出(source = engine,itemId = 警報 id)。
/// </summary>
public sealed class AlertManager
{
    private readonly ScenarioAlerts _config;
    private readonly int _stepsPerSecond;
    private readonly double _dt;
    private readonly Action<string, AlertRecord, string?, object> _raise;

    public AlertManagerState State { get; private set; } = new();

    public AlertManager(ScenarioAlerts config, double dt, int stepsPerSecond, Action<string, AlertRecord, string?, object> raise)
    {
        _config = config;
        _dt = dt;
        _stepsPerSecond = stepsPerSecond;
        _raise = raise;
    }

    private long Ticks(double seconds) => (long)Math.Round(seconds * _stepsPerSecond);

    public AlertRecord? Find(string id) => State.Alerts.FirstOrDefault(a => a.Id == id);

    /// <summary>目前有聲響的警報(駕駛台蜂鳴器)。</summary>
    public bool Audible => State.Alerts.Any(a => a.Audible);

    // ------------------------------------------------------------------ 條件

    /// <summary>各警報條件是否成立,以及優先序覆寫(BNWAS 依階段)。會更新滯後/持續狀態。</summary>
    private (bool Active, AlertPriority? Priority) Condition(AlertDefinition d, in AlertInputs x, long tick)
    {
        switch (d.Id)
        {
            case FaultNames.LogFailure: return (x.LogInvalid, null);
            case AlertIds.ShallowWater:
            {
                if (_config.ShallowWaterM is not { } lim || !x.DepthValid) { State.ShallowActive = false; return (false, null); }
                State.ShallowActive = State.ShallowActive ? x.DepthBelowKeelM < lim + 0.2 : x.DepthBelowKeelM < lim;
                return (State.ShallowActive, null);
            }
            case AlertIds.OffHeading:
            {
                if (_config.OffHeadingDeg is not { } lim || !x.AutopilotEnabled || !x.HeadingValid) { State.OffHeadingSinceTick = -1; return (false, null); }
                var err = Math.Abs(Units.WrapDeg180(x.AutopilotSetHeadingDeg - x.HeadingDeg));
                if (err <= lim) { State.OffHeadingSinceTick = -1; return (false, null); }
                if (State.OffHeadingSinceTick < 0) State.OffHeadingSinceTick = tick;
                return (tick - State.OffHeadingSinceTick >= Ticks(_config.OffHeadingDelayS), null);
            }
            case AlertIds.HeadingMonitor:
            {
                if (_config.HeadingMonitorDeg is not { } lim || !x.HeadingValid) { State.HeadingMonitorActive = false; return (false, null); }
                var diff = Math.Abs(Units.WrapDeg180(x.HeadingDeg - x.MagneticHeadingDeg));
                State.HeadingMonitorActive = State.HeadingMonitorActive ? diff > lim - 1.0 : diff > lim;
                return (State.HeadingMonitorActive, null);
            }
            case AlertIds.Bnwas:
                return x.BnwasStage switch
                {
                    <= 0 => (false, null),
                    1 => (true, AlertPriority.Caution),
                    _ => (true, AlertPriority.Alarm),
                };
            default:
                return (x.Faults.Contains(d.Id), null);
        }
    }

    // ------------------------------------------------------------------ 每步

    public void Update(long tick, in AlertInputs inputs)
    {
        foreach (var d in AlertCatalogue.All)
        {
            var (active, overridePriority) = Condition(d, inputs, tick);
            var rec = Find(d.Id);
            if (active)
            {
                if (rec is null)
                {
                    rec = new AlertRecord { Id = d.Id, Priority = overridePriority ?? d.Priority, RaisedTick = tick, EscalateFromTick = tick };
                    Insert(rec);
                    RaiseEvent(SimEventKinds.AlertRaised, rec, tick, ("repeat", false));
                }
                else if (rec.State == AlertState.RectifiedUnacknowledged)
                {
                    rec.State = AlertState.ActiveUnacknowledged;
                    rec.RaisedTick = tick;
                    rec.EscalateFromTick = tick;
                    rec.SilencedUntilTick = -1;
                    rec.AckTick = -1;
                    rec.Count++;
                    RaiseEvent(SimEventKinds.AlertRaised, rec, tick, ("repeat", true));
                }
                if (overridePriority is { } p && p < rec.Priority)
                {
                    var from = rec.Priority;
                    rec.Priority = p;
                    rec.Escalated = true;
                    rec.State = AlertState.ActiveUnacknowledged;
                    rec.SilencedUntilTick = -1;
                    rec.AckTick = -1;
                    rec.EscalateFromTick = tick;
                    RaiseEvent(SimEventKinds.AlertEscalated, rec, tick, ("from", ContractJson.EnumName(from)), ("to", ContractJson.EnumName(p)), ("reason", "source"));
                }
                // 靜音期滿 → 重響
                if (rec.State == AlertState.ActiveSilenced && tick >= rec.SilencedUntilTick)
                {
                    rec.State = AlertState.ActiveUnacknowledged;
                    rec.SilencedUntilTick = -1;
                    RaiseEvent(SimEventKinds.AlertReannunciated, rec, tick);
                }
                // warning 未確認逾時 → alarm(MSC.302 第 9.1.2 節:可升級)
                if (rec.Priority == AlertPriority.Warning && rec.State is AlertState.ActiveUnacknowledged or AlertState.ActiveSilenced
                    && tick - rec.EscalateFromTick >= Ticks(_config.EscalateS))
                {
                    rec.Priority = AlertPriority.Alarm;
                    rec.Escalated = true;
                    rec.State = AlertState.ActiveUnacknowledged;
                    rec.SilencedUntilTick = -1;
                    RaiseEvent(SimEventKinds.AlertEscalated, rec, tick, ("from", "warning"), ("to", "alarm"), ("reason", "unacknowledged"), ("after_s", (tick - rec.EscalateFromTick) * _dt));
                }
            }
            else if (rec is not null && rec.Active)
            {
                // BNWAS 的重設本身即解除指示(MSC.128:重設取消視覺與聲響),不需再確認
                if (rec.State == AlertState.ActiveAcknowledged || d.Id == AlertIds.Bnwas)
                {
                    State.Alerts.Remove(rec);
                    rec.State = AlertState.Normal;
                }
                else
                {
                    rec.State = AlertState.RectifiedUnacknowledged;
                    rec.SilencedUntilTick = -1;
                }
                RaiseEvent(SimEventKinds.AlertRectified, rec, tick);
            }
        }
    }

    /// <summary>依目錄順序插入(state.alerts[] 再依優先序排序輸出)。</summary>
    private void Insert(AlertRecord rec)
    {
        var idx = AlertCatalogue.IndexOf(rec.Id);
        var pos = State.Alerts.FindIndex(a => AlertCatalogue.IndexOf(a.Id) > idx);
        if (pos < 0) State.Alerts.Add(rec); else State.Alerts.Insert(pos, rec);
    }

    /// <summary>發事件:data {id, priority, category, source, state(事件後), count, raisedAt_s, ackTime_s?, silencedUntil_s?, …附加欄位}。</summary>
    private void RaiseEvent(string kind, AlertRecord rec, long tick, params (string Key, object? Value)[] extra)
    {
        var d = rec.Definition;
        var data = new Dictionary<string, object?>
        {
            ["id"] = rec.Id, ["priority"] = ContractJson.EnumName(rec.Priority), ["category"] = d.Category.ToString(), ["source"] = d.Source,
            ["state"] = ContractJson.EnumName(rec.State), ["count"] = rec.Count, ["raisedAt_s"] = rec.RaisedTick * _dt,
        };
        if (kind == SimEventKinds.AlertAcknowledged) data["ackTime_s"] = (tick - rec.RaisedTick) * _dt;
        if (rec.SilencedUntilTick >= 0) data["silencedUntil_s"] = rec.SilencedUntilTick * _dt;
        foreach (var (k, v) in extra) data[k] = v;
        _raise(kind, rec, d.Text, data);
    }

    // ------------------------------------------------------------------ 操作

    /// <summary>確認(id = null 或 "all":全部可確認者)。回傳確認的警報數;未知 id 回傳 −1。</summary>
    public int Acknowledge(long tick, string? id)
    {
        var all = id is null || string.Equals(id, "all", StringComparison.OrdinalIgnoreCase);
        if (!all && AlertCatalogue.Find(id!) is null) return -1;
        var n = 0;
        foreach (var rec in State.Alerts.ToArray())
        {
            if (!all && rec.Id != id) continue;
            switch (rec.State)
            {
                case AlertState.ActiveUnacknowledged or AlertState.ActiveSilenced:
                    rec.State = AlertState.ActiveAcknowledged;
                    rec.AckTick = tick;
                    rec.SilencedUntilTick = -1;
                    RaiseEvent(SimEventKinds.AlertAcknowledged, rec, tick);
                    n++;
                    break;
                case AlertState.RectifiedUnacknowledged:
                    rec.AckTick = tick;
                    State.Alerts.Remove(rec);
                    rec.State = AlertState.Normal;
                    RaiseEvent(SimEventKinds.AlertAcknowledged, rec, tick);
                    n++;
                    break;
            }
        }
        return n;
    }

    /// <summary>靜音(id = null 或 "all"):有聲響且未確認者暫停聲響 silence_s。回傳靜音數;未知 id 回傳 −1。</summary>
    public int Silence(long tick, string? id)
    {
        var all = id is null || string.Equals(id, "all", StringComparison.OrdinalIgnoreCase);
        if (!all && AlertCatalogue.Find(id!) is null) return -1;
        var n = 0;
        foreach (var rec in State.Alerts)
        {
            if (!all && rec.Id != id) continue;
            if (rec.State != AlertState.ActiveUnacknowledged || !AlertCatalogue.IsAudible(rec.Priority)) continue;
            rec.State = AlertState.ActiveSilenced;
            rec.SilencedUntilTick = tick + Ticks(_config.SilenceS);
            RaiseEvent(SimEventKinds.AlertSilenced, rec, tick);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------ 狀態輸出 / 快照 / 雜湊

    /// <summary>state.alerts[](依優先序、成立時間;無警報時 null)。</summary>
    public IReadOnlyList<AlertStatus>? BuildStates()
    {
        if (State.Alerts.Count == 0) return null;
        return State.Alerts
            .OrderBy(a => (int)a.Priority).ThenBy(a => a.RaisedTick).ThenBy(a => AlertCatalogue.IndexOf(a.Id))
            .Select(a =>
            {
                var d = a.Definition;
                return new AlertStatus
                {
                    Id = a.Id, Number = d.Number, Priority = a.Priority, Category = d.Category, Source = d.Source, State = a.State, Text = d.Text,
                    RaisedAtS = a.RaisedTick * _dt, AcknowledgedAtS = a.AckTick >= 0 ? a.AckTick * _dt : null,
                    SilencedUntilS = a.SilencedUntilTick >= 0 ? a.SilencedUntilTick * _dt : null,
                    Escalated = a.Escalated, Count = a.Count, Audible = a.Audible,
                };
            }).ToArray();
    }

    public const int HashLongsHeader = 4;
    public const int HashLongsPerAlert = 7;

    public int HashSize => (HashLongsHeader + State.Alerts.Count * HashLongsPerAlert) * 8;

    public int WriteHash(Span<byte> buf)
    {
        var v = new List<long>(HashLongsHeader + State.Alerts.Count * HashLongsPerAlert)
        {
            State.Alerts.Count, State.OffHeadingSinceTick, State.HeadingMonitorActive ? 1 : 0, State.ShallowActive ? 1 : 0,
        };
        foreach (var a in State.Alerts)
            v.AddRange(new long[] { AlertCatalogue.IndexOf(a.Id), (long)a.Priority, (long)a.State, a.RaisedTick, a.AckTick, a.SilencedUntilTick, a.Count });
        for (var i = 0; i < v.Count; i++) BinaryPrimitives.WriteInt64LittleEndian(buf[(i * 8)..], v[i]);
        return v.Count * 8;
    }

    public AlertManagerState CreateSnapshot() => ContractJson.Deserialize<AlertManagerState>(ContractJson.Serialize(State))!;

    public void Restore(AlertManagerState? s) => State = s is null ? new AlertManagerState() : ContractJson.Deserialize<AlertManagerState>(ContractJson.Serialize(s))!;
}
