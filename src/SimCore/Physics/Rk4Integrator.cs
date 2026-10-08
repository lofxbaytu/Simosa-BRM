using SimosaBRM.SimCore.Environment;

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
    {
        var k1 = dynamics.Derivative(t, y, env, control);
        var k2 = dynamics.Derivative(t + 0.5 * dt, y + 0.5 * dt * k1, env, control);
        var k3 = dynamics.Derivative(t + 0.5 * dt, y + 0.5 * dt * k2, env, control);
        var k4 = dynamics.Derivative(t + dt, y + dt * k3, env, control);
        var next = y + (dt / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4);
        return next with { Psi = Contracts.Units.NormalizeHeadingRad(next.Psi) };
    }
}
