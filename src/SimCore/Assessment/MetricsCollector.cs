using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.SimCore.Assessment;

public sealed class MetricsState
{
    public long Samples { get; set; }
    public double DistanceM { get; set; }
    public double SumSogKn { get; set; }
    public double MaxSogKn { get; set; }
    public int Freezes { get; set; }
    public double[] RudderBins { get; set; } = new double[5];
    public int HardOverCount { get; set; }
    public double HardOverTimeS { get; set; }
    public bool InHardOver { get; set; }
    public int RudderOrders { get; set; }
    public double? LastRudderOrderDeg { get; set; }
    public long LastRudderOrderTick { get; set; } = -1;
    public double MaxRotDegPerMin { get; set; }
    public long MaxRotTick { get; set; } = -1;
    public double TimeRotOverLimitS { get; set; }
    public int RotExcursions { get; set; }
    public bool RotOver { get; set; }
    public int TelegraphChanges { get; set; }
    public List<TelegraphSpanState> TelegraphSpans { get; set; } = new();
    public int RpmOrders { get; set; }
    public int ThrusterOrders { get; set; }
    public double ThrusterTimeS { get; set; }
    public double LastThrusterOrder { get; set; }
    public double TimeOverSpeedLimitS { get; set; }
    public double MaxOverSpeedKn { get; set; }
    public bool SpeedOver { get; set; }
    public string? SpeedLimitSource { get; set; }
    public double AutopilotTimeS { get; set; }
    public int AutopilotSwitches { get; set; }
    public bool? LastAutopilotEnabled { get; set; }
    // UKC
    public double? MinUkcM { get; set; }
    public long MinUkcTick { get; set; } = -1;
    public double MinUkcSquatM { get; set; }
    public double TimeBelowUkcLimitS { get; set; }
    public int UkcExcursions { get; set; }
    public bool UkcBelow { get; set; }
    public int Groundings { get; set; }
    public double? UkcLimitM { get; set; }
    public string? UkcLimitSource { get; set; }
    // 警報與 BNWAS(規劃書第 8.3 節第 5、7 類:警報確認時間;BNWAS 第二/三階段次數)
    public List<AlertMetricRecord> Alerts { get; set; } = new();
    public int AlertEscalations { get; set; }
    public int AlertSilences { get; set; }
    public int BnwasStage1 { get; set; }
    public int BnwasStage2 { get; set; }
    public int BnwasStage3 { get; set; }
    public int BnwasResets { get; set; }
    // 雷達操作(規劃書第 8.3 節第 7 類;radarControl 事件:成功的控制指令數、手動擷取數、量程切換數)
    public int RadarControls { get; set; }
    public int RadarAcquisitions { get; set; }
    public int RadarRangeChanges { get; set; }
    /// <summary>依雷達 id(x、s)分列的雷達操作計數(summary.radar.byRadar;進快照)</summary>
    public Dictionary<string, RadarCountsState> RadarByRadar { get; set; } = new();
}

/// <summary>單一雷達的操作計數(MetricsState.RadarByRadar;可序列化進快照)。</summary>
public sealed class RadarCountsState
{
    public int Controls { get; set; }
    public int Acquisitions { get; set; }
    public int RangeChanges { get; set; }
}

/// <summary>每次警報成立的確認紀錄(summary.alerts.records[])。</summary>
public sealed class AlertMetricRecord
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("priority")] public string Priority { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("raisedAt_s")] public double RaisedAtS { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("acknowledgedAt_s")] public double? AcknowledgedAtS { get; set; }
    /// <summary>確認時間(確認 − 成立,秒;未確認 null)</summary>
    [System.Text.Json.Serialization.JsonPropertyName("ackTime_s")] public double? AckTimeS { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("rectifiedAt_s")] public double? RectifiedAtS { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("escalated")] public bool Escalated { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("silences")] public int Silences { get; set; }
}

public sealed class TelegraphSpanState
{
    public string Order { get; set; } = "";
    public long FromTick { get; set; }
}

/// <summary>
/// 操縱控制、一般與 UKC 指標的每秒累計器(第 4.3、4.5 節);舵角分箱 0-5/5-10/10-20/20-35/&gt;35,滿舵 |δ| ≥ hardOver − 0.5°,
/// 速限/UKC 超限事件含滯後(0.2 kn / +0.2 m)。
/// </summary>
public sealed class MetricsCollector
{
    private static readonly string[] BinNames = { "0-5", "5-10", "10-20", "20-35", ">35" };
    private readonly double _hardOverDeg;
    private readonly double? _rotLimit;
    private readonly double _dt;

    public MetricsState State { get; private set; } = new();

    public MetricsCollector(double hardOverDeg, double? rotLimitDegPerMin, double dt, TelegraphOrder initialTelegraph)
    {
        _hardOverDeg = hardOverDeg;
        _rotLimit = rotLimitDegPerMin;
        _dt = dt;
        State.TelegraphSpans.Add(new TelegraphSpanState { Order = initialTelegraph.ToString(), FromTick = 0 });
    }

    public void OnFreeze() => State.Freezes++;

    /// <summary>
    /// 警報/BNWAS 事件(引擎 source = engine):alertRaised 開一筆紀錄(每次成立一筆)、alertAcknowledged 填確認時間、
    /// alertEscalated/alertSilenced 計數、alertRectified 記解除時刻;bnwasStage 計各階段次數、bnwasReset 計重設(階段 ≥ 1 時;並以重設時刻填入該筆 bnwas 紀錄的確認時間)。
    /// </summary>
    public void OnAlertEvent(SimEvent e)
    {
        var st = State;
        AlertMetricRecord? Open(string? id) => st.Alerts.LastOrDefault(r => r.Id == id && r.AcknowledgedAtS is null);
        switch (e.Kind)
        {
            case SimEventKinds.AlertRaised:
                st.Alerts.Add(new AlertMetricRecord { Id = e.ItemId ?? "", Priority = e.DataString("priority") ?? "", RaisedAtS = e.T });
                break;
            case SimEventKinds.AlertAcknowledged:
                if (Open(e.ItemId) is { } rec)
                {
                    rec.AcknowledgedAtS = e.T;
                    rec.AckTimeS = e.T - rec.RaisedAtS;
                }
                break;
            case SimEventKinds.AlertSilenced:
                st.AlertSilences++;
                if (Open(e.ItemId) is { } sr) sr.Silences++;
                break;
            case SimEventKinds.AlertEscalated:
                st.AlertEscalations++;
                if (Open(e.ItemId) is { } er) { er.Escalated = true; er.Priority = e.DataString("to") ?? er.Priority; }
                break;
            case SimEventKinds.AlertRectified:
                if (st.Alerts.LastOrDefault(r => r.Id == e.ItemId && r.RectifiedAtS is null) is { } rr) rr.RectifiedAtS = e.T;
                break;
            case SimEventKinds.BnwasStage:
                switch ((int)(e.DataNumber("stage") ?? 0))
                {
                    case 1: st.BnwasStage1++; break;
                    case 2: st.BnwasStage2++; break;
                    case 3: st.BnwasStage3++; break;
                }
                break;
            case SimEventKinds.BnwasReset:
                if (e.DataNumber("fromStage") is > 0)
                {
                    st.BnwasResets++;
                    // BNWAS 重設即解除指示(sensors-alerts.md 第 3.2 節,不需再確認):重設時刻視為該筆 bnwas 警報的確認時刻
                    if (Open(AlertIds.Bnwas) is { } br)
                    {
                        br.AcknowledgedAtS = e.T;
                        br.AckTimeS = e.T - br.RaisedAtS;
                    }
                }
                break;
        }
    }

    /// <summary>summary.alerts(BNWAS 次數併入 bnwas 子區塊)。</summary>
    public SummaryAlerts AlertSummary()
    {
        var st = State;
        var acked = st.Alerts.Where(r => r.AckTimeS is not null).Select(r => r.AckTimeS!.Value).ToList();
        return new SummaryAlerts
        {
            Raised = st.Alerts.Count,
            Acknowledged = acked.Count,
            Unacknowledged = st.Alerts.Count - acked.Count,
            Escalations = st.AlertEscalations,
            Silences = st.AlertSilences,
            MeanAckTimeS = acked.Count > 0 ? acked.Average() : null,
            MaxAckTimeS = acked.Count > 0 ? acked.Max() : null,
            Records = st.Alerts.Select(r => new AlertMetricRecord
            {
                Id = r.Id, Priority = r.Priority, RaisedAtS = r.RaisedAtS, AcknowledgedAtS = r.AcknowledgedAtS, AckTimeS = r.AckTimeS,
                RectifiedAtS = r.RectifiedAtS, Escalated = r.Escalated, Silences = r.Silences,
            }).ToArray(),
            Bnwas = new SummaryBnwas { Stage1 = st.BnwasStage1, Stage2 = st.BnwasStage2, Stage3 = st.BnwasStage3, Resets = st.BnwasResets },
        };
    }
    public void OnGrounding() => State.Groundings++;

    /// <summary>
    /// 雷達操作事件(radarControl,source = instructor;data {radar, client, cmd, ok, …}):只計成功(ok)的指令;
    /// acquire 計入 radar.acquisitions、range 計入 radar.rangeChanges;getStatus/getTracks 為查詢不計。
    /// </summary>
    public void OnRadarControl(SimEvent e)
    {
        if (e.Kind != SimEventKinds.RadarControl || e.DataBool("ok") == false) return;
        var cmd = e.DataString("cmd");
        if (cmd is null or "getStatus" or "getTracks") return;
        State.RadarControls++;
        if (cmd == "acquire") State.RadarAcquisitions++;
        else if (cmd == "range") State.RadarRangeChanges++;
        var radar = (e.DataString("radar") ?? e.ItemId ?? "?").ToLowerInvariant();
        if (!State.RadarByRadar.TryGetValue(radar, out var per)) State.RadarByRadar[radar] = per = new RadarCountsState();
        per.Controls++;
        if (cmd == "acquire") per.Acquisitions++;
        else if (cmd == "range") per.RangeChanges++;
    }

    /// <summary>summary.radar(有雷達操作時;否則 null,舊紀錄 summary 不變)。byRadar 依雷達 id 排序(確定性)。</summary>
    public SummaryRadar? RadarSummary()
    {
        if (State.RadarControls == 0 && State.RadarByRadar.Count == 0) return null;
        return new SummaryRadar
        {
            ControlCount = State.RadarControls,
            Acquisitions = State.RadarAcquisitions,
            RangeChanges = State.RadarRangeChanges,
            ByRadar = State.RadarByRadar.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => new SummaryRadarCounts { ControlCount = kv.Value.Controls, Acquisitions = kv.Value.Acquisitions, RangeChanges = kv.Value.RangeChanges }),
        };
    }

    /// <summary>radar.* 指標值(<see cref="MetricNames"/>)。</summary>
    public double? RadarMetric(string name) => name switch
    {
        "radar.controlCount" => State.RadarControls,
        "radar.acquisitions" => State.RadarAcquisitions,
        "radar.rangeChanges" => State.RadarRangeChanges,
        _ => null,
    };

    /// <summary>學員指令觀察(引擎於套用指令時呼叫)。</summary>
    public void OnAction(ObservedAction a)
    {
        var st = State;
        switch (a.Kind)
        {
            case ActionKinds.RudderOrder:
                // 1 s 視窗內合併:視窗自計入的那道舵令起算,不隨後續舵令延長(連續拖曳舵輪每 1 s 最多計 1 次)
                if (st.LastRudderOrderTick < 0 || a.Tick - st.LastRudderOrderTick >= Math.Round(1.0 / _dt))
                {
                    st.RudderOrders++;
                    st.LastRudderOrderTick = a.Tick;
                }
                break;
            case ActionKinds.TelegraphChange:
                st.TelegraphChanges++;
                break;
            case ActionKinds.RpmOrder: st.RpmOrders++; break;
            case ActionKinds.ThrusterOrder: st.ThrusterOrders++; break;
            case ActionKinds.AutopilotOn or ActionKinds.AutopilotOff: st.AutopilotSwitches++; break;
        }
    }

    /// <summary>車鐘變更(含時間表 ownShip 覆寫)紀錄段落。</summary>
    public void OnTelegraph(TelegraphOrder order, long tick)
    {
        var last = State.TelegraphSpans[^1];
        if (last.Order == order.ToString()) return;
        State.TelegraphSpans.Add(new TelegraphSpanState { Order = order.ToString(), FromTick = tick });
    }

    public void Sample(Situation sit, (double Limit, string Source)? speedLimit, (double LimitM, string Source)? ukcLimit, Action<string, string?, object?> raise)
    {
        var st = State;
        st.Samples++;
        st.DistanceM += Units.KnToMps(sit.SogKn);
        st.SumSogKn += sit.SogKn;
        if (sit.SogKn > st.MaxSogKn) st.MaxSogKn = sit.SogKn;

        var r = Math.Abs(sit.RudderDeg);
        st.RudderBins[r < 5 ? 0 : r < 10 ? 1 : r < 20 ? 2 : r <= 35 ? 3 : 4] += 1.0;
        var hard = r >= _hardOverDeg - 0.5;
        if (hard) { st.HardOverTimeS += 1.0; if (!st.InHardOver) st.HardOverCount++; }
        st.InHardOver = hard;

        var rot = Math.Abs(sit.RotDegPerMin);
        if (rot > st.MaxRotDegPerMin || st.MaxRotTick < 0) { st.MaxRotDegPerMin = rot; st.MaxRotTick = sit.Tick; }
        if (_rotLimit is { } lim)
        {
            var over = rot > lim;
            if (over) { st.TimeRotOverLimitS += 1.0; if (!st.RotOver) st.RotExcursions++; }
            st.RotOver = over;
        }

        if (Math.Abs(sit.ThrusterOrder) >= 0.05) st.ThrusterTimeS += 1.0;

        if (sit.AutopilotEnabled) st.AutopilotTimeS += 1.0;
        st.LastAutopilotEnabled = sit.AutopilotEnabled;

        if (speedLimit is { } sl)
        {
            var over = sit.SogKn - sl.Limit;
            if (over > 0)
            {
                st.TimeOverSpeedLimitS += 1.0;
                if (over > st.MaxOverSpeedKn) { st.MaxOverSpeedKn = over; st.SpeedLimitSource = sl.Source; }
                if (!st.SpeedOver)
                {
                    st.SpeedOver = true;
                    raise(SimEventKinds.SpeedLimitExceeded, null, new { sog_kn = sit.SogKn, limit_kn = sl.Limit, source = sl.Source.Split(':')[0], id = sl.Source.Contains(':') ? sl.Source.Split(':')[1] : sl.Source });
                }
            }
            else if (st.SpeedOver && over <= -0.2)
            {
                st.SpeedOver = false;
                raise(SimEventKinds.SpeedLimitRestored, null, new { sog_kn = sit.SogKn, limit_kn = sl.Limit, source = sl.Source.Split(':')[0], id = sl.Source.Contains(':') ? sl.Source.Split(':')[1] : sl.Source });
            }
        }
        else if (st.SpeedOver)
        {
            st.SpeedOver = false;
            raise(SimEventKinds.SpeedLimitRestored, null, new { sog_kn = sit.SogKn });
        }

        if (st.MinUkcM is not { } mu || sit.UkcM < mu) { st.MinUkcM = sit.UkcM; st.MinUkcTick = sit.Tick; st.MinUkcSquatM = sit.SquatM; }
        if (ukcLimit is { } ul)
        {
            st.UkcLimitM = ul.LimitM;
            st.UkcLimitSource = ul.Source;
            if (sit.UkcM < ul.LimitM)
            {
                st.TimeBelowUkcLimitS += 1.0;
                if (!st.UkcBelow)
                {
                    st.UkcBelow = true;
                    st.UkcExcursions++;
                    raise(SimEventKinds.UkcAlarm, null, new { ukc_m = sit.UkcM, limit_m = ul.LimitM, source = ul.Source });
                }
            }
            else if (st.UkcBelow && sit.UkcM >= ul.LimitM + 0.2)
            {
                st.UkcBelow = false;
                raise(SimEventKinds.UkcClear, null, new { ukc_m = sit.UkcM, limit_m = ul.LimitM, source = ul.Source });
            }
        }
    }

    public SummaryGeneral General(double time) => new()
    {
        DurationS = time,
        Freezes = State.Freezes,
        DistanceNm = State.DistanceM / Units.MetresPerNauticalMile,
        AvgSogKn = State.Samples > 0 ? State.SumSogKn / State.Samples : 0.0,
        MaxSogKn = State.MaxSogKn,
    };

    public SummaryUkc Ukc() => new()
    {
        MinM = State.MinUkcM,
        MinAtS = State.MinUkcTick >= 0 ? State.MinUkcTick * _dt : null,
        MinSquatM = State.MinUkcM is null ? null : State.MinUkcSquatM,
        TimeBelowLimitS = State.TimeBelowUkcLimitS,
        Excursions = State.UkcExcursions,
        Groundings = State.Groundings,
        LimitM = State.UkcLimitM,
        LimitSource = State.UkcLimitSource,
    };

    public SummaryManoeuvring Manoeuvring(long tick)
    {
        var st = State;
        var spans = new List<TelegraphSpan>();
        for (var i = 0; i < st.TelegraphSpans.Count; i++)
        {
            var from = st.TelegraphSpans[i].FromTick * _dt;
            var to = (i + 1 < st.TelegraphSpans.Count ? st.TelegraphSpans[i + 1].FromTick : tick) * _dt;
            spans.Add(new TelegraphSpan { Order = st.TelegraphSpans[i].Order, FromS = from, ToS = to, DurationS = to - from });
        }
        var bins = new Dictionary<string, double>();
        for (var i = 0; i < BinNames.Length; i++) bins[BinNames[i]] = st.RudderBins[i];
        return new SummaryManoeuvring
        {
            RudderDistributionS = bins,
            HardOverCount = st.HardOverCount,
            HardOverTimeS = st.HardOverTimeS,
            RudderOrders = st.RudderOrders,
            MaxRotDegPerMin = st.MaxRotDegPerMin,
            MaxRotAtS = st.MaxRotTick >= 0 ? st.MaxRotTick * _dt : null,
            TimeRotOverLimitS = st.TimeRotOverLimitS,
            RotExcursions = st.RotExcursions,
            TelegraphChanges = st.TelegraphChanges,
            TelegraphOrders = spans,
            RpmOrders = st.RpmOrders,
            ThrusterOrders = st.ThrusterOrders,
            ThrusterTimeS = st.ThrusterTimeS,
            TimeOverSpeedLimitS = st.TimeOverSpeedLimitS,
            MaxOverSpeedKn = st.MaxOverSpeedKn,
            SpeedLimitSource = st.SpeedLimitSource,
            AutopilotTimeS = st.AutopilotTimeS,
            AutopilotSwitches = st.AutopilotSwitches,
        };
    }

    public void Restore(MetricsState s) => State = s;
}
