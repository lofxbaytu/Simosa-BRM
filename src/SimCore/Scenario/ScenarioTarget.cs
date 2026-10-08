using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 情境中的目標船(規劃書第 5.2 節 Traffic 模組、第 9.1 節「目標船」):船型/尺寸/燈號/AIS 靜態資料、
/// 初始狀態、行為模式、觸發條件、AIS 開關與錯誤資料注入。YAML 鍵為 camelCase(scenario.schema.json `targets[]`);
/// 同一型別也用於 <c>targetControl</c> 指令的 <c>add</c> 參數與快照(System.Text.Json)。
/// 角度為度、速度為節、距離為公尺(航點到達半徑、跟隨距離)或浬(CPA 門檻、觸發距離),依欄位註明。
/// </summary>
public sealed class ScenarioTarget
{
    /// <summary>目標識別字(情境內唯一;指令與狀態廣播以此引用)</summary>
    public string Id { get; set; } = "";
    /// <summary>船名(AIS 靜態資料;未指定時用 id)</summary>
    public string? Name { get; set; }
    /// <summary>MMSI(9 位;未指定時以 416000001 起依序配置,416 為臺灣 MID)</summary>
    public int? Mmsi { get; set; }
    /// <summary>船型:cargo、tanker、fishing、pilot、tug、passenger、pleasure、sar、hsc、other(決定 AIS 船型碼與預設燈號)</summary>
    public string ShipType { get; set; } = "cargo";
    /// <summary>總長(m)</summary>
    public double Loa { get; set; } = 100.0;
    /// <summary>船寬(m)</summary>
    public double Beam { get; set; } = 16.0;
    /// <summary>吃水(m)</summary>
    public double Draft { get; set; } = 5.0;
    public string? CallSign { get; set; }
    public string? Imo { get; set; }
    public string? Destination { get; set; }
    /// <summary>
    /// 燈號類別(規劃書第 9.1 節):powerDriven、anchored、notUnderCommand、restrictedManoeuvrability、
    /// constrainedByDraught、fishing、towing、pilot、sailing;未指定時依行為與船型推定
    /// </summary>
    public string? Lights { get; set; }
    /// <summary>AIS 發送開關</summary>
    public bool AisOn { get; set; } = true;
    /// <summary>AIS 錯誤資料注入(null = 正確資料)</summary>
    public TargetAisError? AisError { get; set; }
    public TargetInitial Initial { get; set; } = new();
    public TargetBehaviour Behaviour { get; set; } = new();
    /// <summary>出現條件(null = 情境開始即存在)</summary>
    public TargetTrigger? Trigger { get; set; }
    /// <summary>運動模型參數覆寫(未指定時依船長估計)</summary>
    public TargetMotion? Motion { get; set; }

    /// <summary>深拷貝(目標船執行期的定義會被指令修改,不可與情境物件共用)。</summary>
    public ScenarioTarget Clone()
        => Contracts.ContractJson.Deserialize<ScenarioTarget>(Contracts.ContractJson.Serialize(this))
           ?? throw new InvalidDataException("目標船定義複製失敗");
}

/// <summary>目標船初始狀態。</summary>
public sealed class TargetInitial
{
    /// <summary>位置:經緯度或本地 ENU(m),二擇一</summary>
    public ScenarioPosition? Position { get; set; }
    /// <summary>航向(度)</summary>
    public double Heading { get; set; }
    /// <summary>對水速度(kn)</summary>
    public double Speed { get; set; }
    /// <summary>初始迴轉率(度/分,右轉正)</summary>
    public double Rot { get; set; }
}

/// <summary>行為模式(JSON/YAML 為 camelCase 字串)。</summary>
public enum TargetBehaviourMode
{
    /// <summary>保持航向航速(指令可覆寫)</summary>
    Hold,
    /// <summary>航點航線</summary>
    Waypoints,
    /// <summary>時間觸發的航向/航速指令表</summary>
    Scripted,
    /// <summary>錨泊(定點微漂:繞錨鏈迴盪)</summary>
    Anchored,
    /// <summary>跟隨自船或其他目標的相對位置(拖船、引水船)</summary>
    Follow,
    /// <summary>COLREG 自動避碰(基底為航點航線或保持航向)</summary>
    Colreg,
}

/// <summary>
/// 行為定義:<c>mode</c> 決定主要行為;<c>colreg</c> 可疊加在 hold/waypoints/scripted 上(mode = colreg 時自動啟用)。
/// </summary>
public sealed class TargetBehaviour
{
    public TargetBehaviourMode Mode { get; set; } = TargetBehaviourMode.Hold;
    /// <summary>航點(waypoints / colreg 模式)</summary>
    public List<TargetWaypoint>? Waypoints { get; set; }
    /// <summary>到達最後航點後回到第一個</summary>
    public bool Loop { get; set; }
    /// <summary>到達最後航點後停船(否則保持最後航向航速)</summary>
    public bool StopAtEnd { get; set; }
    /// <summary>腳本(scripted 模式;時間自目標出現起算)</summary>
    public List<TargetScriptStep>? Script { get; set; }
    public TargetAnchor? Anchor { get; set; }
    public TargetFollow? Follow { get; set; }
    public TargetColreg? Colreg { get; set; }
}

/// <summary>航點:經緯度或本地 ENU;speed 為航向此航點的航速(kn,未指定沿用);radius 為到達半徑(m,預設 2 倍船長,最少 100 m)。</summary>
public sealed class TargetWaypoint
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Speed { get; set; }
    public double? Radius { get; set; }
}

/// <summary>腳本步:t 秒(自出現起)後下達航向(度)/航速(kn);rot 為此次轉向的迴轉率上限(度/分)。</summary>
public sealed class TargetScriptStep
{
    public double T { get; set; }
    public double? Heading { get; set; }
    public double? Speed { get; set; }
    public double? Rot { get; set; }
}

/// <summary>錨泊:以初始位置為錨位,船艏朝風流合成來向,以 swing 振幅、period 週期迴盪,船舯在錨位後方 scope 公尺。</summary>
public sealed class TargetAnchor
{
    /// <summary>迴盪振幅(度)</summary>
    public double Swing { get; set; } = 15.0;
    /// <summary>迴盪週期(秒)</summary>
    public double Period { get; set; } = 360.0;
    /// <summary>錨位到船舯的水平距離(m;未指定 = 船長)</summary>
    public double? Scope { get; set; }
}

/// <summary>跟隨:leader 為 "own"(自船)或目標 id;目標位於 leader 相對方位 bearing(度,右舷正)距離 range(m)處。</summary>
public sealed class TargetFollow
{
    public string Leader { get; set; } = "own";
    public double Bearing { get; set; } = 90.0;
    public double Range { get; set; } = 80.0;
    /// <summary>位置誤差 → 速度修正的增益(1/s)</summary>
    public double Gain { get; set; } = 0.05;
    /// <summary>最大航速(kn)</summary>
    public double MaxSpeed { get; set; } = 15.0;
}

/// <summary>COLREG 第一版參數:以 CPA/TCPA 門檻觸發;obey = false 為「不守規則」(辨識情境但不採取行動)。</summary>
public sealed class TargetColreg
{
    public bool Enabled { get; set; } = true;
    public bool Obey { get; set; } = true;
    /// <summary>觸發的 CPA 門檻(nm)</summary>
    public double CpaThreshold { get; set; } = 1.0;
    /// <summary>觸發的 TCPA 門檻(分)</summary>
    public double TcpaThreshold { get; set; } = 12.0;
    /// <summary>每次轉向幅度(度)</summary>
    public double Turn { get; set; } = 30.0;
    /// <summary>橫越讓路時的減速倍率(1 = 不減速)</summary>
    public double SpeedFactor { get; set; } = 0.7;
    /// <summary>行動後 CPA 仍低於門檻時再加轉向的間隔(秒)</summary>
    public double EscalateAfter { get; set; } = 60.0;
    /// <summary>直航船在對方未讓路時的最後手段(第 17 條;CPA &lt; 門檻/2 且 TCPA &lt; 門檻/3 時右轉)</summary>
    public bool LastResort { get; set; } = true;
}

public enum TargetTriggerType
{
    None,
    /// <summary>模擬時間 ≥ time 秒</summary>
    Time,
    /// <summary>自船與 point 的距離 &lt; lessThan 或 &gt; greaterThan(nm)</summary>
    OwnDistance,
    /// <summary>自船航向在 greaterThan 至 lessThan 的順時針扇區內(度;只給一個時為單邊比較)</summary>
    OwnHeading,
}

/// <summary>出現條件;滿足後目標開始運動、廣播與發送 AIS。</summary>
public sealed class TargetTrigger
{
    public TargetTriggerType Type { get; set; } = TargetTriggerType.None;
    public double? Time { get; set; }
    public ScenarioPosition? Point { get; set; }
    public double? LessThan { get; set; }
    public double? GreaterThan { get; set; }
}

/// <summary>運動模型參數(未指定依船長估計,見 Traffic/TargetMotionModel)。</summary>
public sealed class TargetMotion
{
    /// <summary>迴轉率一階響應時間常數(秒)</summary>
    public double? HeadingTau { get; set; }
    /// <summary>最大迴轉率(度/分)</summary>
    public double? MaxRot { get; set; }
    /// <summary>航速一階滯後時間常數(秒)</summary>
    public double? SpeedTau { get; set; }
    /// <summary>最大航速(kn)</summary>
    public double? MaxSpeed { get; set; }
}

/// <summary>AIS 錯誤資料注入:位置偏移、航向/COG/SOG 錯誤、靜態資料錯誤、不發送。</summary>
public sealed class TargetAisError
{
    /// <summary>位置偏移量(m)</summary>
    public double PositionOffset { get; set; }
    /// <summary>位置偏移方向(真方位,度)</summary>
    public double PositionOffsetBearing { get; set; }
    /// <summary>航向誤差(度,加到真航向)</summary>
    public double HeadingError { get; set; }
    /// <summary>COG 誤差(度)</summary>
    public double CogError { get; set; }
    /// <summary>SOG 誤差(kn)</summary>
    public double SogError { get; set; }
    /// <summary>靜態資料錯誤:未指定 name/shipType/mmsi 時以預設錯誤(船名加 " II"、船型 pleasure、尺寸減半)</summary>
    public bool StaticError { get; set; }
    public string? Name { get; set; }
    public string? ShipType { get; set; }
    public int? Mmsi { get; set; }
    /// <summary>不發送(AIS 開啟但無訊息,等同故障)</summary>
    public bool Silent { get; set; }
}

/// <summary>評估參數(規劃書第 8.3 節):最小 CPA 門檻等。</summary>
public sealed class ScenarioAssessment
{
    /// <summary>最小 CPA 門檻(nm);自船與任一目標的 CPA 低於此值且 TCPA &gt; 0 時記錄事件</summary>
    [YamlMember(Alias = "minCpa_nm", ApplyNamingConventions = false)]
    [JsonPropertyName("minCpa_nm")]
    public double MinCpaNm { get; set; } = 0.5;
}
