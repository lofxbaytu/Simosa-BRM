using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Assessment;

public sealed class ZoneState
{
    /// <summary>目前在內的區域 id(依情境順序)</summary>
    public List<string> Inside { get; set; } = new();
    public double? MinDistanceToNoGo { get; set; }
}

/// <summary>區域追蹤(第 4.3 節):多邊形內外(ray casting,邊上視為內)、進出事件、區域速限/UKC 門檻、至禁區邊的最小距離。</summary>
public sealed class ZoneTracker
{
    private readonly List<(ScenarioZone Zone, (double X, double Y)[] Poly)> _zones = new();

    public ZoneState State { get; private set; } = new();

    public ZoneTracker(IEnumerable<ScenarioZone> zones, LocalTangentPlane projection)
    {
        foreach (var z in zones)
            _zones.Add((z, z.Polygon.Select(p => Scenario.Scenario.Resolve(projection, p)).ToArray()));
    }

    public bool IsInside(string id) => State.Inside.Contains(id);

    public IEnumerable<ScenarioZone> InsideZones() => _zones.Where(z => State.Inside.Contains(z.Zone.Id)).Select(z => z.Zone);

    /// <summary>區域速限(在內的 speedLimit 區中最嚴者;無則 null)。</summary>
    public (double Limit, string Id)? SpeedLimit()
    {
        (double, string)? best = null;
        foreach (var z in InsideZones())
            if (z.Type == ZoneType.SpeedLimit && z.SpeedLimitKn is { } lim && (best is null || lim < best.Value.Item1)) best = (lim, z.Id);
        return best;
    }

    /// <summary>區域 UKC 門檻(在內的 ukcPolicy 區中最嚴者;無則 null)。</summary>
    public (double LimitM, string Id)? UkcLimit(double maxDraft)
    {
        (double, string)? best = null;
        foreach (var z in InsideZones())
            if (z.Type == ZoneType.UkcPolicy && z.UkcMin is { } u)
            {
                var m = u.ToMetres(maxDraft);
                if (best is null || m > best.Value.Item1) best = (m, z.Id);
            }
        return best;
    }

    public void Update(Situation sit, Action<string, string?, object?> raise)
    {
        var st = State;
        foreach (var (zone, poly) in _zones)
        {
            var inside = Polygon2D.Contains(poly, sit.X, sit.Y);
            var was = st.Inside.Contains(zone.Id);
            if (inside && !was)
            {
                st.Inside.Add(zone.Id);
                raise(SimEventKinds.ZoneEntered, null, new { zoneId = zone.Id, type = Contracts.ContractJson.EnumName(zone.Type) });
                if (zone.Type == ZoneType.Reporting) raise(SimEventKinds.ReportPointPassed, null, new { zoneId = zone.Id, report = zone.Name ?? zone.Id });
            }
            else if (!inside && was)
            {
                st.Inside.Remove(zone.Id);
                raise(SimEventKinds.ZoneExited, null, new { zoneId = zone.Id, type = Contracts.ContractJson.EnumName(zone.Type) });
            }
            if (zone.Type == ZoneType.NoGo)
            {
                var d = inside ? 0.0 : Polygon2D.DistanceToBoundary(poly, sit.X, sit.Y);
                if (st.MinDistanceToNoGo is not { } m || d < m) st.MinDistanceToNoGo = d;
            }
        }
    }

    public void Restore(ZoneState s) => State = s;

    /// <summary>ukcPolicy 區的水深覆寫(供 ZoneDepth)。</summary>
    public IReadOnlyList<Environment.ZoneDepth.Zone> DepthZones()
        => _zones.Where(z => z.Zone.WaterDepthM is not null).Select(z => new Environment.ZoneDepth.Zone(z.Zone.Id, z.Poly, z.Zone.WaterDepthM!.Value)).ToArray();
}
