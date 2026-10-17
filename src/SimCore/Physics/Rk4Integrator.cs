using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics.External;

namespace SimosaBRM.SimCore.Physics;

/// <summary>固定步長四階 Runge–Kutta(規劃書第 5.3 節:dt = 0.02 s、50 Hz;CLAUDE.md 確定性要求)。</summary>
public static class Rk4Integrator
{
    /// <summary>一般 ODE y' = f(t, y),陣列版(測試與日後目標船/拖船等次系統用)。</summary>
    public static double[] Step(Func<double, double[], double[]> f, double t, double[] y, double dt)
    {
        var n = y.Length;
        var k1 = f(t, y);
        var tmp = new double[n];
        for (var i = 0; i < n; i++) tmp[i] = y[i] + 0.5 * dt * k1[i];
        var k2 = f(t + 0.5 * dt, tmp);
        for (var i = 0; i < n; i++) tmp[i] = y[i] + 0.5 * dt * k2[i];
        var k3 = f(t + 0.5 * dt, tmp);
        for (var i = 0; i < n; i++) tmp[i] = y[i] + dt * k3[i];
        var k4 = f(t + dt, tmp);
        var result = new double[n];
        for (var i = 0; i < n; i++) result[i] = y[i] + dt / 6.0 * (k1[i] + 2.0 * k2[i] + 2.0 * k3[i] + k4[i]);
        return result;
    }

    /// <summary>狀態向量版(無配置)。</summary>
    public static StateVector Step(Func<double, StateVector, StateVector> f, double t, StateVector y, double dt)
    {
        var k1 = f(t, y);
        var k2 = f(t + 0.5 * dt, y + 0.5 * dt * k1);
        var k3 = f(t + 0.5 * dt, y + 0.5 * dt * k2);
        var k4 = f(t + dt, y + dt * k3);
        return y + (dt / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4);
    }

    /// <summary>
    /// 自船一步:環境與控制在整步內視為常數(致動器以 50 Hz 另行更新,環境取樣於步首位置)。
    /// 回傳的 Psi 已正規化到 [0, 2π)。
    /// </summary>
    public static StateVector Step(IShipDynamics dynamics, double t, StateVector y, in EnvironmentSample env, in ControlInput control, double dt)
        => Step(dynamics, t, y, env, ControlStages.Constant(control), dt);

    /// <summary>
    /// 自船一步,控制量逐階段給定(<see cref="ControlStages"/>):致動器以同一 RK4 在引擎內先行積分,
    /// 第 2/3 階段用半步、第 4 階段用整步的致動器狀態,與 Python 參考實作把舵角/軸轉速/側推放進同一狀態向量積分的結果一致。
    /// 回傳的 Psi 已正規化到 [0, 2π)。
    /// </summary>
    public static StateVector Step(IShipDynamics dynamics, double t, StateVector y, in EnvironmentSample env, in ControlStages c, double dt)
    {
        var k1 = dynamics.Derivative(t, y, env, c.C1);
        var k2 = dynamics.Derivative(t + 0.5 * dt, y + 0.5 * dt * k1, env, c.C2);
        var k3 = dynamics.Derivative(t + 0.5 * dt, y + 0.5 * dt * k2, env, c.C3);
        var k4 = dynamics.Derivative(t + dt, y + dt * k3, env, c.C4);
        var next = y + (dt / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4);
        return next with { Psi = Contracts.Units.NormalizeHeadingRad(next.Psi) };
    }

    /// <summary>
    /// 自船一步,含外力場(拖船/錨鏈/碰墊/繫纜;設計文件 physics-tugs-anchors-mooring.md 第 1 節):外力於每個 RK4 階段以該階段狀態重新計算
    /// (錨鏈懸鏈線、碰墊壓縮、纜繩伸長隨位置變化),跨步狀態(拖船出力、錨位、鏈長、纜長)在整步內固定。
    /// 只在情境使用外力時由引擎呼叫;未使用時走上面的多載,位元與舊版相同。回傳的 Psi 已正規化到 [0, 2π)。
    /// </summary>
    public static StateVector Step(IShipDynamics dynamics, double t, StateVector y, in EnvironmentSample env, in ControlStages c, double dt, IExternalForceField field)
    {
        var k1 = dynamics.Derivative(t, y, env, c.C1, field.Evaluate(y, env));
        var y2 = y + 0.5 * dt * k1;
        var k2 = dynamics.Derivative(t + 0.5 * dt, y2, env, c.C2, field.Evaluate(y2, env));
        var y3 = y + 0.5 * dt * k2;
        var k3 = dynamics.Derivative(t + 0.5 * dt, y3, env, c.C3, field.Evaluate(y3, env));
        var y4 = y + dt * k3;
        var k4 = dynamics.Derivative(t + dt, y4, env, c.C4, field.Evaluate(y4, env));
        var next = y + (dt / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4);
        return next with { Psi = Contracts.Units.NormalizeHeadingRad(next.Psi) };
    }
}
