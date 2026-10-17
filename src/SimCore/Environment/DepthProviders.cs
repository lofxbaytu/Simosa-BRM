namespace SimosaBRM.SimCore.Environment;

/// <summary>水深來源:常數,或由外部(水深網格、海圖)以 callback 提供。座標為本地 ENU(m)。</summary>
public interface IDepthProvider
{
    double DepthAt(double xEast, double yNorth);
}

/// <summary>常數水深(開闊水域情境)。</summary>
public sealed class ConstantDepth : IDepthProvider
{
    public double Depth { get; set; }
    public ConstantDepth(double depth) => Depth = depth;
    public double DepthAt(double xEast, double yNorth) => Depth;
}

/// <summary>由 callback 取得水深(日後接水深網格);快照無法保存 callback,還原時沿用原本的提供者。</summary>
public sealed class CallbackDepth : IDepthProvider
{
    private readonly Func<double, double, double> _fn;
    public CallbackDepth(Func<double, double, double> fn) => _fn = fn;
    public double DepthAt(double xEast, double yNorth) => _fn(xEast, yNorth);
}

/// <summary>
/// 區域水深覆寫(情境 zones[].waterDepth_m;情境引擎設計第 4.3 節):自船在多邊形內時用區域值(多區重疊取最淺),否則用基底常數水深。
/// 多邊形由情境重建,快照只需保存基底水深(<see cref="Engine.EnvironmentSnapshot.ConstantDepth"/>)。
/// </summary>
public sealed class ZoneDepth : IDepthProvider
{
    public sealed record Zone(string Id, (double X, double Y)[] Polygon, double Depth);

    public ConstantDepth Base { get; }
    public IReadOnlyList<Zone> Zones { get; }

    public ZoneDepth(ConstantDepth @base, IReadOnlyList<Zone> zones)
    {
        Base = @base;
        Zones = zones;
    }

    public double DepthAt(double xEast, double yNorth)
    {
        var d = Base.Depth;
        foreach (var z in Zones)
            if (Geo.Polygon2D.Contains(z.Polygon, xEast, yNorth) && z.Depth < d) d = z.Depth;
        return d;
    }
}
