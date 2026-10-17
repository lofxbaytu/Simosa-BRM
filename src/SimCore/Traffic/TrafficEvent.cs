namespace SimosaBRM.SimCore.Traffic;

/// <summary>交通事件種類(紀錄檔 <c>event</c> 行;規劃書第 5.3 節衍生事件、第 8.3 節避碰指標)。</summary>
public static class TrafficEventKinds
{
    /// <summary>自船與目標外形相交</summary>
    public const string Collision = "collision";
    /// <summary>與目標的 CPA 低於情境門檻(assessment.minCpa_nm)且 TCPA &gt; 0</summary>
    public const string CpaAlarm = "cpaAlarm";
    /// <summary>CPA 警報解除(已通過或 CPA 回到門檻以上)</summary>
    public const string CpaClear = "cpaClear";
    /// <summary>目標出現(觸發條件成立或情境開始)</summary>
    public const string Activated = "targetActivated";
    /// <summary>到達航點</summary>
    public const string Waypoint = "waypointReached";
    /// <summary>腳本步執行</summary>
    public const string Script = "scriptStep";
    /// <summary>COLREG 判定/行動變化</summary>
    public const string Colreg = "colreg";
    /// <summary>聲號</summary>
    public const string Sound = "soundSignal";
    /// <summary>目標新增/刪除/教官覆寫等控制</summary>
    public const string Control = "targetControl";
}

/// <summary>交通事件:<see cref="Engine.SimEvent"/> 的語意標記,Source 固定為 traffic;JSON 欄位與舊版完全相同(新欄位選填)。</summary>
public sealed record TrafficEvent : Engine.SimEvent
{
    public TrafficEvent() => Source = Engine.SimEventSources.Traffic;
}
