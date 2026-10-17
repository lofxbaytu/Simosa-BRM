using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 感測器設定(情境 <c>sensors:</c>;規劃書第 7.2 節電羅經/磁羅經;設計文件 docs/design/sensors-alerts.md 第 2 節)。
/// 故障本身由時間表 injectFault 或教官指令注入;這裡只放感測器的常態參數。
/// </summary>
public sealed class ScenarioSensors
{
    /// <summary>磁羅經剩餘誤差(度,修正自差與磁差後;磁羅經指示真航向 = 真航向 + 此值)。航向監視比對電羅經指示值與此磁羅經值。</summary>
    [JsonPropertyName("magneticCompassError_deg")] public double MagneticCompassErrorDeg { get; set; }
}

/// <summary>
/// 警報管理設定(情境 <c>alerts:</c>;MSC.302(87) BAM;設計文件第 3 節)。門檻類警報(偏航、航向監視、淺水)未設定即關閉,
/// 使舊情境的事件序列與雜湊不變;故障類警報(感測器失效、舵機/主機/側推)一律啟用。
/// </summary>
public sealed class ScenarioAlerts
{
    /// <summary>warning 未確認升級為 alarm 的時間(秒;MSC.302 建議 5 分鐘)</summary>
    [JsonPropertyName("escalate_s")] public double EscalateS { get; set; } = 300.0;
    /// <summary>靜音(silence)後重響的時間(秒;MSC.302:30 s)</summary>
    [JsonPropertyName("silence_s")] public double SilenceS { get; set; } = 30.0;
    /// <summary>自動舵偏航警報門檻(度;|設定航向 − 指示航向| 超過且持續 <see cref="OffHeadingDelayS"/>;null = 關閉)</summary>
    [JsonPropertyName("offHeading_deg")] public double? OffHeadingDeg { get; set; }
    /// <summary>偏航警報的持續時間(秒)</summary>
    [JsonPropertyName("offHeadingDelay_s")] public double OffHeadingDelayS { get; set; } = 10.0;
    /// <summary>航向監視(電羅經 vs 磁羅經)差異門檻(度;null = 關閉)</summary>
    [JsonPropertyName("headingMonitor_deg")] public double? HeadingMonitorDeg { get; set; }
    /// <summary>測深儀淺水警報:指示龍骨下水深低於此值(m;null = 關閉;解除滯後 +0.2 m)</summary>
    [JsonPropertyName("shallowWater_m")] public double? ShallowWaterM { get; set; }
}

/// <summary>BNWAS 模式(MSC.128(75) 4.1.2:off / 自動(與自動舵連動)/ 手動 on;JSON 小寫)。</summary>
public enum BnwasMode
{
    Off,
    Auto,
    On,
}

/// <summary>
/// BNWAS 設定(情境 <c>bnwas:</c>;MSC.128(75);設計文件第 4 節):休眠期 3–12 分鐘、第一階段駕駛台視覺指示,
/// 15 s 後第二階段駕駛台聲響,再 <see cref="Stage3S"/> 秒第三階段通知後備值班官/船長。預設 off(舊情境不變)。
/// </summary>
public sealed class ScenarioBnwas
{
    /// <summary>off | auto(自動舵啟用時作動)| on</summary>
    public BnwasMode Mode { get; set; } = BnwasMode.Off;
    /// <summary>休眠期(秒;MSC.128:3–12 分鐘,即 180–720)</summary>
    [JsonPropertyName("dormant_s")] public double DormantS { get; set; } = 720.0;
    /// <summary>第一階段到第二階段(秒;MSC.128:15 s)</summary>
    [JsonPropertyName("stage2_s")] public double Stage2S { get; set; } = 15.0;
    /// <summary>第二階段到第三階段(秒;MSC.128:90 s,可設至 3 分鐘)</summary>
    [JsonPropertyName("stage3_s")] public double Stage3S { get; set; } = 90.0;

    public const double MinDormantS = 180.0;
    public const double MaxDormantS = 720.0;
}
