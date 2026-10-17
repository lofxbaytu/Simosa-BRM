using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>航線(scenario.schema.json <c>route</c>;情境引擎設計第 1.2 節):航點、走廊半寬、速限、檢查點/報告點。</summary>
public sealed class ScenarioRoute
{
    public string? Name { get; set; }
    /// <summary>預設走廊半寬(m;可被航點覆寫)</summary>
    [JsonPropertyName("xtdLimit_m")] public double? XtdLimitM { get; set; }
    /// <summary>初始航段</summary>
    public int StartLeg { get; set; }
    public List<RouteWaypoint> Waypoints { get; set; } = new();
}

/// <summary>航點:經緯度或 ENU;限制作用於「到達該航點的那一段」。</summary>
public sealed class RouteWaypoint
{
    public string Name { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    [JsonPropertyName("xtdLimit_m")] public double? XtdLimitM { get; set; }
    [JsonPropertyName("speedLimit_kn")] public double? SpeedLimitKn { get; set; }
    /// <summary>到達半徑(m;預設 max(2·LOA, 150))</summary>
    [JsonPropertyName("arrivalRadius_m")] public double? ArrivalRadiusM { get; set; }
    public bool Checkpoint { get; set; }
    /// <summary>應報告的說明(事件 reportPointPassed)</summary>
    public string? Report { get; set; }

    [JsonIgnore] public bool HasPosition => (Lat is not null && Lon is not null) || (X is not null && Y is not null);
}

public enum ZoneType
{
    NoGo,
    SpeedLimit,
    UkcPolicy,
    Reporting,
}

/// <summary>區域多邊形(≥ 3 點,經緯度或 ENU 混用;上限 64 點)。</summary>
public sealed class ScenarioZone
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public ZoneType Type { get; set; } = ZoneType.NoGo;
    public List<ScenarioPosition> Polygon { get; set; } = new();
    /// <summary>區域內水深覆寫(m;ukcPolicy)</summary>
    [JsonPropertyName("waterDepth_m")] public double? WaterDepthM { get; set; }
    /// <summary>區域內 UKC 門檻(ukcPolicy;優先於 assessment.ukc)</summary>
    public UkcLimit? UkcMin { get; set; }
    [JsonPropertyName("speedLimit_kn")] public double? SpeedLimitKn { get; set; }
}

/// <summary>UKC 門檻:<c>{ metres }</c> 或 <c>{ fractionOfDraft }</c>。</summary>
public sealed class UkcLimit
{
    public double? Metres { get; set; }
    public double? FractionOfDraft { get; set; }

    /// <summary>換算為公尺(maxDraft 為最大吃水 m);兩者皆給時取較大者。</summary>
    public double ToMetres(double maxDraft)
    {
        var m = Metres ?? double.NegativeInfinity;
        var f = FractionOfDraft is { } fr ? fr * maxDraft : double.NegativeInfinity;
        var v = Math.Max(m, f);
        return double.IsFinite(v) ? v : 0.0;
    }
}
