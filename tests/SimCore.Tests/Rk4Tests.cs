using SimosaBRM.SimCore.Physics;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class Rk4Tests
{
    [Fact]
    public void ExponentialDecayMatchesAnalytic()
    {
        // y' = −y,y(0) = 1 → y(1) = e⁻¹
        double[] y = { 1.0 };
        var dt = 0.01;
        var t = 0.0;
        for (var i = 0; i < 100; i++) { y = Rk4Integrator.Step((_, s) => new[] { -s[0] }, t, y, dt); t += dt; }
        Assert.Equal(Math.Exp(-1.0), y[0], 1e-9);
    }

    [Fact]
    public void HarmonicOscillatorConservesEnergy()
    {
        // x'' = −x:x(0)=1, v(0)=0 → x(t) = cos t
        var y = new StateVector(1.0, 0.0, 0, 0, 0, 0); // 用 U 當 x、V 當 v
        var dt = 0.02;
        var t = 0.0;
        var steps = (int)Math.Round(2 * Math.PI / dt);
        for (var i = 0; i < steps; i++) { y = Rk4Integrator.Step((_, s) => new StateVector(s.V, -s.U, 0, 0, 0, 0), t, y, dt); t += dt; }
        Assert.Equal(Math.Cos(t), y.U, 1e-7);
        Assert.Equal(-Math.Sin(t), y.V, 1e-7);
        Assert.Equal(1.0, y.U * y.U + y.V * y.V, 1e-7);
    }

    [Fact]
    public void FourthOrderConvergence()
    {
        // 步長減半,全域誤差約降為 1/16(y' = cos t)
        static double Err(double dt)
        {
            double[] y = { 0.0 };
            var t = 0.0;
            var n = (int)Math.Round(1.0 / dt);
            for (var i = 0; i < n; i++) { y = Rk4Integrator.Step((tt, _) => new[] { Math.Cos(tt) }, t, y, dt); t += dt; }
            return Math.Abs(y[0] - Math.Sin(1.0));
        }
        var e1 = Err(0.1);
        var e2 = Err(0.05);
        Assert.True(e1 > 0 && e2 > 0);
        Assert.InRange(e1 / e2, 12.0, 20.0);
    }
}
