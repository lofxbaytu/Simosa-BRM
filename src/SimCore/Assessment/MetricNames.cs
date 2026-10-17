namespace SimosaBRM.SimCore.Assessment;

/// <summary>容許帶可引用的指標名稱(summary 的 <c>段.欄位</c>;情境引擎設計第 4.2–4.5 節)。</summary>
public static class MetricNames
{
    public static readonly string[] All =
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
    };

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string name) => Set.Contains(name);
}
