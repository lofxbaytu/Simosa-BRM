namespace SimosaBRM.SimCore.Assessment;

/// <summary>容許帶可引用的指標名稱(summary 的 <c>段.欄位</c>;情境引擎設計第 4.2–4.5 節)。</summary>
public static class MetricNames
{
    // 靜態欄位依宣告順序初始化:BaseNames 須在 All 之前
    private static readonly string[] BaseNames =
    {
        "track.meanAbsXtd_m", "track.maxXtd_m", "track.timeOutsideCorridor_s", "track.corridorExcursions", "track.fractionOutsideCorridor",
        "track.minDistanceToCorridorEdge_m", "track.minDistanceToNoGo_m", "track.meanHeadingDeviation_deg",
        "ukc.min_m", "ukc.timeBelowLimit_s", "ukc.excursions", "ukc.groundings",
        "collisionAvoidance.minCpa_nm", "collisionAvoidance.encounters", "collisionAvoidance.collisions",
        "manoeuvring.hardOverCount", "manoeuvring.hardOverTime_s", "manoeuvring.rudderOrders", "manoeuvring.maxRot_degPerMin",
        "manoeuvring.timeRotOverLimit_s", "manoeuvring.rotExcursions", "manoeuvring.telegraphChanges", "manoeuvring.rpmOrders",
        "manoeuvring.thrusterOrders", "manoeuvring.thrusterTime_s", "manoeuvring.timeOverSpeedLimit_s", "manoeuvring.maxOverSpeed_kn",
        "manoeuvring.autopilotTime_s", "manoeuvring.autopilotSwitches",
        "general.duration_s", "general.distance_nm", "general.avgSog_kn", "general.maxSog_kn",
        // 警報與 BNWAS(sensors-alerts.md 第 5 節;Python scenario.py METRIC_NAMES 尚未加入,情境 bands 暫勿引用)
        "alerts.meanAckTime_s", "alerts.maxAckTime_s", "alerts.unacknowledged", "alerts.escalations", "bnwas.stage2Count", "bnwas.stage3Count",
        // 雷達操作(規劃書第 8.3 節第 7 類;radarControl 事件,由雷達服務轉送;Python scenario.py METRIC_NAMES 同步)
        "radar.controlCount", "radar.acquisitions", "radar.rangeChanges",
    };

    // 操縱量測 manoeuvres.*(ManoeuvreTracker.MetricNameList;ship-familiarisation.md 第 2.6 節):同 id 最近一次 measured 的量測值;
    // Python scenario.py METRIC_NAMES 尚未加入,情境 bands 暫勿引用(Python 結構檢查會判為未知指標)
    // 靠泊/錨泊 berthing.* / anchoring.*(BerthingTracker.MetricNameList;physics-tugs-anchors-mooring.md 第 7 節):與 Python scenario.py METRIC_NAMES
    // 的靠泊/錨泊名稱一致(含 berthing.maxReContactSpeed_mps:已離開泊位後再碰回,E14 reContactSpeed 容許帶)
    public static readonly string[] All = BaseNames.Concat(ManoeuvreTracker.MetricNameList()).Concat(BerthingTracker.MetricNameList).ToArray();

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string name) => Set.Contains(name);
}
