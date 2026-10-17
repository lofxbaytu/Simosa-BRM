namespace SimosaBRM.Sensors.Radar.Arpa;

/// <summary>
/// 等速(CV)卡爾曼濾波器,狀態 [x, y, vx, vy](ENU m、m/s),白加速度過程雜訊 q(m²/s³)。
/// 量測為位置(x, y)與 2×2 共變異數;回傳 NIS(正規化新息平方)供操船偵測與閘門。
/// </summary>
public sealed class KalmanCv
{
    public double T { get; private set; }
    public readonly double[] X = new double[4];
    public readonly double[,] P = new double[4, 4];

    public KalmanCv(double t, double x, double y, double vx, double vy, double posVar, double velVar)
    {
        T = t;
        X[0] = x; X[1] = y; X[2] = vx; X[3] = vy;
        P[0, 0] = posVar; P[1, 1] = posVar;
        P[2, 2] = velVar; P[3, 3] = velVar;
    }

    public KalmanCv Clone()
    {
        var k = new KalmanCv(T, X[0], X[1], X[2], X[3], 0, 0);
        Array.Copy(P, k.P, 16);
        return k;
    }

    /// <summary>預測到時間 t(不改變狀態)的位置。</summary>
    public (double X, double Y) PredictPosition(double t)
    {
        var dt = t - T;
        return (X[0] + X[2] * dt, X[1] + X[3] * dt);
    }

    /// <summary>預測到時間 t 的位置共變異數(2×2;不改變狀態,供閘門判定)。</summary>
    public (double P00, double P01, double P11) PredictPositionCov(double t, double q)
    {
        var dt = Math.Max(0.0, t - T);
        var dt2 = dt * dt;
        var q11 = q * dt2 * dt / 3.0;
        var p00 = P[0, 0] + 2 * dt * P[0, 2] + dt2 * P[2, 2] + q11;
        var p11 = P[1, 1] + 2 * dt * P[1, 3] + dt2 * P[3, 3] + q11;
        var p01 = P[0, 1] + dt * P[0, 3] + dt * P[1, 2] + dt2 * P[2, 3];
        return (p00, p01, p11);
    }

    /// <summary>時間更新到 t。</summary>
    public void Predict(double t, double q)
    {
        var dt = t - T;
        if (dt <= 0) return;
        // X = F X
        X[0] += X[2] * dt;
        X[1] += X[3] * dt;
        // P = F P F' + Q(逐軸;x/vx 與 y/vy 兩組 + 交叉項)
        var p = P;
        var n = new double[4, 4];
        // F = [[1,0,dt,0],[0,1,0,dt],[0,0,1,0],[0,0,0,1]]
        var fp = new double[4, 4];
        for (var j = 0; j < 4; j++)
        {
            fp[0, j] = p[0, j] + dt * p[2, j];
            fp[1, j] = p[1, j] + dt * p[3, j];
            fp[2, j] = p[2, j];
            fp[3, j] = p[3, j];
        }
        for (var i = 0; i < 4; i++)
        {
            n[i, 0] = fp[i, 0] + dt * fp[i, 2];
            n[i, 1] = fp[i, 1] + dt * fp[i, 3];
            n[i, 2] = fp[i, 2];
            n[i, 3] = fp[i, 3];
        }
        var dt2 = dt * dt;
        var q11 = q * dt2 * dt / 3.0;
        var q12 = q * dt2 / 2.0;
        var q22 = q * dt;
        n[0, 0] += q11; n[1, 1] += q11;
        n[0, 2] += q12; n[2, 0] += q12;
        n[1, 3] += q12; n[3, 1] += q12;
        n[2, 2] += q22; n[3, 3] += q22;
        Array.Copy(n, p, 16);
        T = t;
    }

    /// <summary>新息與其共變異數(在目前時間;呼叫前先 Predict)。</summary>
    public (double Nx, double Ny, double S00, double S01, double S11) Innovation(double zx, double zy, double r00, double r01, double r11)
        => (zx - X[0], zy - X[1], P[0, 0] + r00, P[0, 1] + r01, P[1, 1] + r11);

    public static double Nis(double nx, double ny, double s00, double s01, double s11)
    {
        var det = s00 * s11 - s01 * s01;
        if (det <= 0) return double.PositiveInfinity;
        return (s11 * nx * nx - 2 * s01 * nx * ny + s00 * ny * ny) / det;
    }

    /// <summary>量測更新;回傳 NIS。</summary>
    public double Update(double zx, double zy, double r00, double r01, double r11)
    {
        var (nx, ny, s00, s01, s11) = Innovation(zx, zy, r00, r01, r11);
        var det = s00 * s11 - s01 * s01;
        if (det <= 0) return double.PositiveInfinity;
        var i00 = s11 / det;
        var i01 = -s01 / det;
        var i11 = s00 / det;
        var nis = (i00 * nx * nx + 2 * i01 * nx * ny + i11 * ny * ny);
        // K = P H' S⁻¹(H 取前兩列)
        var k = new double[4, 2];
        for (var i = 0; i < 4; i++)
        {
            k[i, 0] = P[i, 0] * i00 + P[i, 1] * i01;
            k[i, 1] = P[i, 0] * i01 + P[i, 1] * i11;
        }
        for (var i = 0; i < 4; i++) X[i] += k[i, 0] * nx + k[i, 1] * ny;
        // P = (I − K H) P(Joseph 形式省略;以對稱化維持數值穩定)
        var np = new double[4, 4];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                np[i, j] = P[i, j] - (k[i, 0] * P[0, j] + k[i, 1] * P[1, j]);
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                P[i, j] = 0.5 * (np[i, j] + np[j, i]);
        return nis;
    }
}
