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

/// <summary>主機狀態(JSON 為小寫)。</summary>
public enum EngineRunState
{
    Stopped,
    Running,
    Starting,
    Failed,
}
