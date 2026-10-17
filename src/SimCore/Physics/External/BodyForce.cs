using SimosaBRM.SimCore.Environment;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>
/// 船體座標的外力與力矩(規劃書第 6.2 節 X_T/X_A、Y_T/Y_A、N_T/N_A 項;N、N·m):x 向艏、y 向右舷、原點船舯,
/// N 正 = 艏向右(與 MMG 慣例相同)。拖船、錨鏈、碰墊、碼頭硬接觸與繫纜的合力。
/// </summary>
public readonly record struct BodyForce(double X, double Y, double N)
{
    public static readonly BodyForce Zero = new(0.0, 0.0, 0.0);

    public static BodyForce operator +(BodyForce a, BodyForce b) => new(a.X + b.X, a.Y + b.Y, a.N + b.N);

    /// <summary>船體座標力 (fx, fy) 作用於船體座標點 (xb, yb):N = xb·fy − yb·fx。</summary>
    public static BodyForce AtPoint(double fx, double fy, double xb, double yb) => new(fx, fy, xb * fy - yb * fx);

    /// <summary>ENU 力 (fe, fn) 作用於船體座標點 (xb, yb),航向 <paramref name="psi"/>(自北順時針,弧度)。</summary>
    public static BodyForce FromEnu(double fe, double fn, double xb, double yb, double psi)
    {
        var s = Math.Sin(psi);
        var c = Math.Cos(psi);
        return AtPoint(fe * s + fn * c, fe * c - fn * s, xb, yb);
    }
}

/// <summary>
/// RK4 各階段的外力場:以該階段的狀態(位置、航向、對水速度)與步首環境取樣計算船體座標外力。
/// 必須是純函數(跨步狀態——拖船出力、錨位、鏈長、纜長——只在步與步之間由引擎更新),確定性才不受積分階段數影響。
/// </summary>
public interface IExternalForceField
{
    BodyForce Evaluate(in StateVector state, in EnvironmentSample env);
}

/// <summary>船體座標點與 ENU 的換算(位置、速度)。</summary>
public static class BodyFrame
{
    /// <summary>船體座標點 → ENU 位置。</summary>
    public static (double E, double N) ToEnu(in StateVector s, double xb, double yb)
    {
        var sn = Math.Sin(s.Psi);
        var c = Math.Cos(s.Psi);
        return (s.X + xb * sn + yb * c, s.Y + xb * c - yb * sn);
    }

    /// <summary>ENU 位置 → 船體座標點。</summary>
    public static (double Xb, double Yb) FromEnu(in StateVector s, double e, double n)
    {
        var sn = Math.Sin(s.Psi);
        var c = Math.Cos(s.Psi);
        var de = e - s.X;
        var dn = n - s.Y;
        return (de * sn + dn * c, de * c - dn * sn);
    }

    /// <summary>船體座標點的對地速度(ENU,m/s):對水速度 (u − r·yb, v + r·xb) 轉 ENU 再加均勻流。</summary>
    public static (double VE, double VN) PointGroundVelocity(in StateVector s, in EnvironmentSample env, double xb, double yb)
    {
        var ub = s.U - s.R * yb;
        var vb = s.V + s.R * xb;
        var sn = Math.Sin(s.Psi);
        var c = Math.Cos(s.Psi);
        return (ub * sn + vb * c + env.CurrentEastMps, ub * c - vb * sn + env.CurrentNorthMps);
    }
}
