namespace SimosaBRM.SimCore.Physics;

/// <summary>
/// 自船 3 自由度狀態向量(規劃書第 6.2 節):
/// u、v 為船體座標對水速度(m/s;x 向艏、y 向右舷,原點船舯),r 艏搖角速度(rad/s,右轉正),
/// x、y 為本地 ENU 位置(東、北,m),psi 航向(弧度,自北順時針)。
/// 同一型別也用來表示導數(du, dv, dr, dx, dy, dpsi),以便 RK4 做線性組合。
/// </summary>
public readonly record struct StateVector(double U, double V, double R, double X, double Y, double Psi)
{
    public static StateVector operator +(StateVector a, StateVector b)
        => new(a.U + b.U, a.V + b.V, a.R + b.R, a.X + b.X, a.Y + b.Y, a.Psi + b.Psi);

    public static StateVector operator *(double k, StateVector a)
        => new(k * a.U, k * a.V, k * a.R, k * a.X, k * a.Y, k * a.Psi);

    public static StateVector operator *(StateVector a, double k) => k * a;

    public static readonly StateVector Zero = new(0, 0, 0, 0, 0, 0);

    public double[] ToArray() => new[] { U, V, R, X, Y, Psi };
    public static StateVector FromArray(ReadOnlySpan<double> a) => new(a[0], a[1], a[2], a[3], a[4], a[5]);
}

/// <summary>
/// 動力學模型看到的控制輸入(已經過舵機、主機、側推的致動器動態):
/// 實際舵角(弧度,右正)、實際螺槳轉速(rpm,倒車負)、側推實際推力比例(−1 至 +1,正 = 推艏向右)。
/// </summary>
public readonly record struct ControlInput(double RudderRad, double Rpm, double Thruster);
