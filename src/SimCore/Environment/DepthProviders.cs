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
