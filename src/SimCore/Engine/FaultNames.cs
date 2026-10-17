namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 引擎會改變行為的故障名稱(其餘名稱僅記錄並廣播,供儀器端自行反應;規劃書第 7.2 節故障目錄、src/Contracts/fault-catalogue.json)。
/// 感測器類故障改變「指示值」(state 的 heading/pos/depthBelowKeel/stw 與 NMEA),真值仍由 <c>state.truth</c> 提供(設計文件 docs/design/sensors-alerts.md)。
/// </summary>
public static class FaultNames
{
    /// <summary>舵機故障:舵角停在目前位置(切 emergency 應急操舵後以降低速率再度可動)</summary>
    public const string SteeringGear = "steeringGear";
    /// <summary>主機故障:轉速衰減至 0,engine.state = failed</summary>
    public const string MainEngine = "mainEngine";
    /// <summary>艏側推不可用</summary>
    public const string BowThruster = "bowThruster";

    /// <summary>隨動操舵(FU)失效:fu 與 autopilot 模式下舵停在原處,只剩 NFU 與應急操舵可動舵;警報 alarm</summary>
    public const string SteeringFuOnly = "steeringFuOnly";
    /// <summary>舵機泵失效:舵機速率減半(所有模式);警報 alarm</summary>
    public const string SteeringPumpFailure = "steeringPumpFailure";

    /// <summary>電羅經漂移:指示航向以 args.rate_degPerMin(預設 1°/分,正 = 指示值往右)累積誤差;無警報</summary>
    public const string GyroDrift = "gyroDrift";
    /// <summary>電羅經凍結:指示航向停在注入時的值;無警報(航向監視可偵測)</summary>
    public const string GyroFreeze = "gyroFreeze";
    /// <summary>電羅經失效:指示航向無效(保持最後值、sensors.headingValid = false、HDT 停送)、自動舵脫開;警報 alarm</summary>
    public const string GyroFailure = "gyroFailure";
    /// <summary>GNSS 跳點:指示位置於注入時一次跳 args.distance_m(預設 500 m)/ args.bearing_deg(預設 90°),持續到清除</summary>
    public const string GpsJump = "gpsJump";
    /// <summary>GNSS 偏移:指示位置以 args.ramp_s(預設 60 s)漸進偏移到 args.distance_m(預設 200 m)/ args.bearing_deg(預設 45°)</summary>
    public const string GpsOffset = "gpsOffset";
    /// <summary>GNSS 失效:指示位置/COG/SOG 無效(保持最後值、GGA 品質 0、RMC 狀態 V);警報 alarm</summary>
    public const string GpsFailure = "gpsFailure";
    /// <summary>測深儀失效:龍骨下水深無效(保持最後值、DPT 停送);警報 warning</summary>
    public const string EchoSounderFailure = "echoSounderFailure";
    /// <summary>計程儀失效:args 無 bias_kn/factor 時 STW 無效(警報 warning);有則 STW = 真值 × factor + bias_kn(無警報)</summary>
    public const string LogFailure = "logFailure";
    /// <summary>自動舵失效:自動舵脫開(fu、舵停在原處)且不能再啟用;警報 alarm</summary>
    public const string AutopilotFailure = "autopilotFailure";

    /// <summary>拖纜斷裂:args.id 指定拖船(省略 = 第一艘拉中/待命中者);拉中者失去出力(tugLineParted 事件),推頂者續推但不能再拉;清除後可重新帶纜</summary>
    public const string TugLineBreak = "tugLineBreak";

    /// <summary>引擎會改變行為的故障(fault-catalogue.json modelled = true)。</summary>
    public static readonly string[] Modelled =
    {
        SteeringGear, MainEngine, BowThruster, SteeringFuOnly, SteeringPumpFailure,
        GyroDrift, GyroFreeze, GyroFailure, GpsJump, GpsOffset, GpsFailure, EchoSounderFailure, LogFailure, AutopilotFailure,
        TugLineBreak,
    };
}
