namespace SimosaBRM.SimCore.Physics.Mmg;

/// <summary>
/// 螺槳推力/轉矩(移植自 Python simosa_brm.propeller.PropellerModel;規劃書第 6.2 節):
/// 第一象限用 Wageningen B 系列擬合的二次多項式 K_T(J) = k0 + k1·J + k2·J²、K_Q(J);
/// 倒車(n &lt; 0)與倒退(J &lt; 0)以同一多項式在負 J 區外推並乘倒車折減係數,逾 ±jClip 保持常數(簡化四象限,
/// 完整 Wageningen 四象限 C_T*(β) 傅立葉表尚未納入);軸停止(|n| &lt; lockRps)時逐漸切換為鎖定圓盤阻力。
/// </summary>
public sealed class MmgPropeller
{
    public double Diameter { get; }
    public double Kt0 { get; }
    public double Kt1 { get; }
    public double Kt2 { get; }
    public double Kq0 { get; }
    public double Kq1 { get; }
    public double Kq2 { get; }
    /// <summary>倒車推力折減(B 系列背面不對稱)</summary>
    public double AsternFactor { get; }
    /// <summary>負 J 外推的截止</summary>
    public double JClip { get; }
    /// <summary>鎖定螺槳以盤面積計的阻力係數</summary>
    public double LockedCd { get; }
    /// <summary>|n| 低於此值(rps)時逐漸切換為鎖定圓盤</summary>
    public double LockRps { get; }
    /// <summary>倒車時橫向力/推力(右旋槳:艉向左)</summary>
    public double SideForceFactor { get; }

    public MmgPropeller(PropellerCoefficients p)
    {
        Diameter = p.Diameter_m;
        var kt = p.Kt ?? throw new InvalidDataException("propeller.kt 缺少");
        var kq = p.Kq ?? throw new InvalidDataException("propeller.kq 缺少");
        (Kt0, Kt1, Kt2) = (kt[0], kt[1], kt[2]);
        (Kq0, Kq1, Kq2) = (kq[0], kq[1], kq[2]);
        AsternFactor = p.AsternThrustFactor;
        JClip = p.JClip;
        LockedCd = p.LockedDragCd;
        LockRps = p.LockRps;
        SideForceFactor = p.SideForceFactor;
    }

    public double KtAt(double j) => Kt0 + Kt1 * j + Kt2 * j * j;
    public double KqAt(double j) => Kq0 + Kq1 * j + Kq2 * j * j;

    /// <summary>
    /// 回傳 (推力 T [N], 轉矩 Q [N·m], 有效 J, 有效 K_T);<paramref name="n"/> 為 rps(倒車負),<paramref name="va"/> 為螺槳進速(m/s)。
    /// </summary>
    public (double T, double Q, double J, double Kt) ThrustTorque(double n, double va, double rho)
    {
        var d = Diameter;
        var an = Math.Abs(n);
        if (an < 1e-9)
            return (LockedDrag(va, rho), 0.0, 0.0, 0.0);
        var j = va / (an * d);
        double kt, t, q;
        if (n > 0)
        {
            var je = Math.Max(-JClip, Math.Min(j, 1.5));
            kt = KtAt(je);
            var kq = KqAt(je);
            t = rho * n * n * Math.Pow(d, 4) * kt;
            q = rho * n * n * Math.Pow(d, 5) * kq;
        }
        else
        {
            // 倒車:船前進時 j > 0 → 對倒轉的螺槳而言是負進速係數,推力(向後)隨前進速度增大
            var je = Math.Max(-JClip, Math.Min(-j, 1.5));
            kt = KtAt(je) * AsternFactor;
            var kq = KqAt(je) * AsternFactor;
            t = -rho * n * n * Math.Pow(d, 4) * kt;
            q = -rho * n * n * Math.Pow(d, 5) * kq;
        }
        if (an < LockRps)
        {
            var wgt = 1.0 - an / LockRps;
            t = (1.0 - wgt) * t + wgt * LockedDrag(va, rho);
        }
        return (t, q, j, kt);
    }

    private double LockedDrag(double va, double rho)
        => -LockedCd * 0.5 * rho * va * Math.Abs(va) * Math.PI * Diameter * Diameter / 4.0;
}
