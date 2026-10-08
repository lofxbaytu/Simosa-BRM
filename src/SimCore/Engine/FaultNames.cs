namespace SimosaBRM.SimCore.Engine;

/// <summary>引擎目前會改變行為的故障名稱(其餘名稱僅記錄並廣播,供儀器端自行反應;規劃書第 7.2 節故障目錄)。</summary>
public static class FaultNames
{
    /// <summary>舵機故障:舵角停在目前位置</summary>
    public const string SteeringGear = "steeringGear";
    /// <summary>主機故障:轉速衰減至 0,engine.state = failed</summary>
    public const string MainEngine = "mainEngine";
    /// <summary>艏側推不可用</summary>
    public const string BowThruster = "bowThruster";
}
