namespace SimosaBRM.SimCore.Contracts;

/// <summary>車鐘位置(state.schema.json telegraph 列舉;JSON 以大寫代號輸出,見 ContractJson 的命名原則)。</summary>
public enum TelegraphOrder
{
    /// <summary>緊急全速退(Emergency Full Astern)</summary>
    EFAS,
    /// <summary>全速退</summary>
    FAS,
    /// <summary>半速退</summary>
    HAS,
    /// <summary>慢速退</summary>
    SAS,
    /// <summary>微速退</summary>
    DSAS,
    /// <summary>停俥</summary>
    STOP,
    /// <summary>微速進</summary>
    DSAH,
    /// <summary>慢速進</summary>
    SAH,
    /// <summary>半速進</summary>
    HAH,
    /// <summary>全速進(港內全速)</summary>
    FAH,
    /// <summary>航海全速(Navigation Full / Full Sea)</summary>
    NAVF,
}

/// <summary>裝載狀態(JSON 為小寫 full / ballast / intermediate)。</summary>
public enum LoadingCondition
{
    Full,
    Ballast,
    Intermediate,
}

/// <summary>
/// 操舵模式(state.schema.json <c>steering.mode</c>、指令 <c>steeringMode</c>;規劃書第 7.2 節操舵模式選擇;JSON 為小寫 fu / nfu / autopilot / emergency)。
/// fu:隨動(舵令 = 目標舵角);nfu:非隨動(舵以舵機速率依 port/starboard/stop 推動);autopilot:等同 <c>autopilot {enabled: true}</c>;
/// emergency:應急操舵(舵機房/電話下令,速率降低;舵機故障時仍可動)。
/// </summary>
public enum SteeringMode
{
    Fu,
    Nfu,
    Autopilot,
    Emergency,
}

/// <summary>主機狀態(JSON 為小寫)。</summary>
public enum EngineRunState
{
    Stopped,
    Running,
    Starting,
    Failed,
}
