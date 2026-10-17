using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.SimCore.Alerts;

/// <summary>警報優先序(MSC.302(87) 第 5 節;JSON:emergencyAlarm | alarm | warning | caution)。數值越小越優先。</summary>
public enum AlertPriority
{
    EmergencyAlarm,
    Alarm,
    Warning,
    Caution,
}

/// <summary>
/// 警報類別(MSC.302(87) 第 6 節):A = 需在相關工作站有圖形資訊輔助判斷(例如淺水、偏航)、B = 不需額外資訊、
/// C = 駕駛台無法確認(只能在機艙確認;本版未使用)。JSON 為 "A" | "B" | "C"。
/// </summary>
public enum AlertCategory
{
    A,
    B,
    C,
}

/// <summary>
/// 警報狀態(MSC.302(87) 第 8 節與 IEC 62923-1 狀態圖;responsibility transferred 本版略):
/// activeUnacknowledged(閃爍 + 聲響)→ activeSilenced(靜音 silence_s 後重響)→ activeAcknowledged(恆亮)
/// → 條件解除 → normal;未確認即解除者為 rectifiedUnacknowledged(閃爍、無聲響),確認後 normal。
/// </summary>
public enum AlertState
{
    ActiveUnacknowledged,
    ActiveSilenced,
    ActiveAcknowledged,
    RectifiedUnacknowledged,
    Normal,
}

/// <summary>警報定義:id(故障類警報與故障名稱相同)、NMEA ALR 編號(000–999)、預設優先序與類別、來源設備、中文顯示文字、NMEA 英文文字(ASCII)。</summary>
public sealed record AlertDefinition(string Id, int Number, AlertPriority Priority, AlertCategory Category, string Source, string Text, string NmeaText);

/// <summary>門檻類與系統類警報 id(故障類警報 id = 故障名稱,見 <see cref="FaultNames"/>)。</summary>
public static class AlertIds
{
    /// <summary>測深儀淺水警報(指示龍骨下水深 &lt; alerts.shallowWater_m)</summary>
    public const string ShallowWater = "shallowWater";
    /// <summary>自動舵偏航警報(|設定航向 − 指示航向| &gt; alerts.offHeading_deg 持續 offHeadingDelay_s)</summary>
    public const string OffHeading = "offHeading";
    /// <summary>航向監視:電羅經指示值與磁羅經差異 &gt; alerts.headingMonitor_deg</summary>
    public const string HeadingMonitor = "headingMonitor";
    /// <summary>BNWAS:第一階段 caution(視覺)、第二階段起 alarm(駕駛台聲響;第三階段另發 bnwasStage 事件)</summary>
    public const string Bnwas = "bnwas";
}

/// <summary>警報目錄(固定順序;評估順序與 NMEA 編號以此為準,改動會改變事件序列)。</summary>
public static class AlertCatalogue
{
    public static readonly AlertDefinition[] All =
    {
        new(FaultNames.GyroFailure, 1, AlertPriority.Alarm, AlertCategory.B, "gyro", "電羅經失效", "GYRO FAILURE"),
        new(FaultNames.GpsFailure, 2, AlertPriority.Alarm, AlertCategory.B, "gnss", "GNSS 定位失效", "GNSS POSITION LOST"),
        new(FaultNames.EchoSounderFailure, 3, AlertPriority.Warning, AlertCategory.B, "echoSounder", "測深儀失效", "ECHO SOUNDER FAILURE"),
        new(FaultNames.LogFailure, 4, AlertPriority.Warning, AlertCategory.B, "log", "計程儀失效", "SPEED LOG FAILURE"),
        new(FaultNames.AutopilotFailure, 5, AlertPriority.Alarm, AlertCategory.B, "autopilot", "自動舵失效", "AUTOPILOT FAILURE"),
        new(FaultNames.SteeringGear, 6, AlertPriority.Alarm, AlertCategory.B, "steering", "舵機故障", "STEERING GEAR FAILURE"),
        new(FaultNames.SteeringFuOnly, 7, AlertPriority.Alarm, AlertCategory.B, "steering", "隨動操舵(FU)失效", "FOLLOW-UP STEERING FAILURE"),
        new(FaultNames.SteeringPumpFailure, 8, AlertPriority.Alarm, AlertCategory.B, "steering", "舵機泵失效", "STEERING PUMP FAILURE"),
        new(FaultNames.MainEngine, 9, AlertPriority.Alarm, AlertCategory.B, "engine", "主機停車", "MAIN ENGINE SHUTDOWN"),
        new(FaultNames.BowThruster, 10, AlertPriority.Warning, AlertCategory.B, "thruster", "艏側推失效", "BOW THRUSTER FAILURE"),
        new(AlertIds.ShallowWater, 11, AlertPriority.Alarm, AlertCategory.A, "echoSounder", "淺水警報(龍骨下水深低於設定)", "SHALLOW WATER"),
        new(AlertIds.OffHeading, 12, AlertPriority.Alarm, AlertCategory.A, "autopilot", "自動舵偏航警報", "OFF HEADING"),
        new(AlertIds.HeadingMonitor, 13, AlertPriority.Warning, AlertCategory.A, "gyro", "航向監視:電羅經與磁羅經差異", "HEADING MONITOR DEVIATION"),
        new(AlertIds.Bnwas, 14, AlertPriority.Caution, AlertCategory.B, "bnwas", "BNWAS 值班警報", "BNWAS WATCH ALARM"),
    };

    private static readonly Dictionary<string, AlertDefinition> ById = All.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static AlertDefinition? Find(string id) => ById.TryGetValue(id, out var d) ? d : null;

    public static int IndexOf(string id) => Array.FindIndex(All, d => d.Id == id);

    /// <summary>有聲響的優先序(caution 只有視覺)。</summary>
    public static bool IsAudible(AlertPriority p) => p is AlertPriority.EmergencyAlarm or AlertPriority.Alarm or AlertPriority.Warning;
}

/// <summary>單一警報的可變紀錄(進快照;state.alerts[] 由此產生)。</summary>
public sealed class AlertRecord
{
    public string Id { get; set; } = "";
    public AlertPriority Priority { get; set; }
    public bool Escalated { get; set; }
    public AlertState State { get; set; } = AlertState.ActiveUnacknowledged;
    /// <summary>最近一次成立(raise / 再成立)的 tick</summary>
    public long RaisedTick { get; set; }
    /// <summary>確認的 tick(−1 = 未確認)</summary>
    public long AckTick { get; set; } = -1;
    /// <summary>靜音結束的 tick(−1 = 未靜音)</summary>
    public long SilencedUntilTick { get; set; } = -1;
    /// <summary>升級計時起點(warning 未確認;成立或重響時重設)</summary>
    public long EscalateFromTick { get; set; }
    /// <summary>成立次數</summary>
    public int Count { get; set; } = 1;

    [JsonIgnore] public AlertDefinition Definition => AlertCatalogue.Find(Id)!;
    [JsonIgnore] public bool Active => State is AlertState.ActiveUnacknowledged or AlertState.ActiveSilenced or AlertState.ActiveAcknowledged;
    [JsonIgnore] public bool Audible => State == AlertState.ActiveUnacknowledged && AlertCatalogue.IsAudible(Priority);
}
