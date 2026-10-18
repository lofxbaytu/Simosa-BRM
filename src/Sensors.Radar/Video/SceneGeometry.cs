using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.Sensors.Radar.Video;

/// <summary>一條方位線上的目標回波段(距離 m,未含脈衝展寬;功率為 σ × 波束因子,m²)。</summary>
internal struct TargetSeg
{
    public double R0;
    public double R1;
    public double Power;
}

/// <summary>一條方位線的回波幾何(與距離格無關;顯示與 ARPA 兩組距離格共用)。</summary>
internal sealed class SpokeGeometry
{
    public TargetSeg[] Segs = new TargetSeg[16];
    public int SegCount;
    public double LandEntry = double.PositiveInfinity;
    public double LandExit = double.PositiveInfinity;
    public double LandFactor;
    public double[] RainR0 = new double[8];
    public double[] RainR1 = new double[8];
    public double[] RainZ = new double[8];
    public double[] RainGamma = new double[8];
    public int RainCount;
    /// <summary>陰影扇區衰減(線性,1 = 無遮蔽)</summary>
    public double ShadowLin = 1.0;
    /// <summary>海雜波方位調制(迎風較強,線性)</summary>
    public double SeaAzMod = 1.0;

    public void Reset()
    {
        SegCount = 0;
        LandEntry = double.PositiveInfinity;
        LandExit = double.PositiveInfinity;
        LandFactor = 0;
        RainCount = 0;
        ShadowLin = 1.0;
        SeaAzMod = 1.0;
    }

    public void AddSeg(double r0, double r1, double power)
    {
        if (SegCount == Segs.Length) Array.Resize(ref Segs, Segs.Length * 2);
        Segs[SegCount++] = new TargetSeg { R0 = r0, R1 = r1, Power = power };
    }

    public void AddRain(double r0, double r1, double z, double gamma)
    {
        if (RainCount == RainR0.Length)
        {
            Array.Resize(ref RainR0, RainCount * 2);
            Array.Resize(ref RainR1, RainCount * 2);
            Array.Resize(ref RainZ, RainCount * 2);
            Array.Resize(ref RainGamma, RainCount * 2);
        }
        RainR0[RainCount] = r0;
        RainR1[RainCount] = r1;
        RainZ[RainCount] = z;
        RainGamma[RainCount] = gamma;
        RainCount++;
    }
}

/// <summary>
/// 每個狀態幀準備一次的場景幾何(天線座標系:x 向右舷、y 向艏;相對方位 φ = atan2(x, y)):
/// 目標外形(SimCore 船體五邊形)轉到天線座標、角度範圍、雷達地平線與截面積;
/// 每條方位線再以預算的 sin/cos 表求射線與外形交段(Cyrus–Beck)、波束加權(高斯雙程波束的 erf 積分)、陸地與雨區交段。
/// 只有每目標/每方位線的少量三角函數,每格運算只查表。
/// </summary>
internal sealed class SceneGeometry
{
    private sealed class PreparedTarget
    {
        public double AC, OffMin, OffMax, Ext, Range, Sigma, RNear, RFar;
        public bool Small;
        public readonly double[] Nx = new double[5], Ny = new double[5], Na = new double[5];
        public double DirMinX, DirMinY, DirMaxX, DirMaxY;
    }

    private readonly RadarConfig _cfg;
    private readonly double _sigmaB;
    private readonly int _smear;
    private readonly double[] _spokeSin;
    private readonly double[] _spokeCos;
    private readonly double[] _shadowLin;
    private PreparedTarget[] _targets = Array.Empty<PreparedTarget>();
    private int _targetCount;
    private double _antX, _antY, _sinH, _cosH, _windSin, _windCos;
    private RadarEnvironment _env = RadarEnvironment.Empty();
    private readonly double[] _landEntry, _landExit, _landFactor;
    private readonly int[] _landStamp;
    private int _stamp;

    public SceneGeometry(RadarConfig cfg)
    {
        _cfg = cfg;
        // 單程 −3 dB 波束寬 BW → 雙程高斯 exp(−x²/(2σ²)),σ = BW / 3.33
        _sigmaB = Units.DegToRad(cfg.BeamwidthDeg) / 3.3302;
        _smear = (int)Math.Ceiling(0.5 * Units.DegToRad(cfg.BeamwidthDeg) / cfg.SpokeAngleRad);
        _spokeSin = new double[cfg.Spokes];
        _spokeCos = new double[cfg.Spokes];
        _shadowLin = new double[cfg.Spokes];
        for (var k = 0; k < cfg.Spokes; k++)
        {
            var a = k * cfg.SpokeAngleRad;
            _spokeSin[k] = Math.Sin(a);
            _spokeCos[k] = Math.Cos(a);
            var deg = Units.RadToDeg(a);
            var lin = 1.0;
            foreach (var s in cfg.ShadowSectors)
                if (s.Contains(deg)) lin *= Math.Pow(10.0, -s.AttenuationDb / 10.0);
            _shadowLin[k] = lin;
        }
        _landEntry = new double[cfg.Spokes];
        _landExit = new double[cfg.Spokes];
        _landFactor = new double[cfg.Spokes];
        _landStamp = new int[cfg.Spokes];
    }

    public double AntennaX => _antX;
    public double AntennaY => _antY;

    /// <summary>為本幀準備目標外形與陸地快取。</summary>
    public void Prepare(RadarScene scene, RadarEnvironment env, double maxRangeM)
    {
        _env = env;
        _stamp++;
        _sinH = Math.Sin(scene.TrueHeadingRad);
        _cosH = Math.Cos(scene.TrueHeadingRad);
        _antX = scene.TrueX + _cfg.AntennaFwdM * _sinH + _cfg.AntennaStbdM * _cosH;
        _antY = scene.TrueY + _cfg.AntennaFwdM * _cosH - _cfg.AntennaStbdM * _sinH;
        var w = Units.DegToRad(scene.WindFromDeg);
        _windSin = Math.Sin(w);
        _windCos = Math.Cos(w);

        if (_targets.Length < scene.Targets.Count)
        {
            var old = _targets;
            _targets = new PreparedTarget[scene.Targets.Count];
            Array.Copy(old, _targets, old.Length);
            for (var i = old.Length; i < _targets.Length; i++) _targets[i] = new PreparedTarget();
        }
        _targetCount = 0;
        var hA = _cfg.AntennaHeightM;
        Span<double> sx = stackalloc double[5];
        Span<double> fy = stackalloc double[5];
        foreach (var t in scene.Targets)
        {
            var dx = t.X - _antX;
            var dy = t.Y - _antY;
            var range = Math.Sqrt(dx * dx + dy * dy);
            if (range - t.Loa > maxRangeM || range < 1.0) continue;
            var rn = range / Units.MetresPerNauticalMile;
            var hidden = RadarConfig.HiddenHeightM(hA, rn);
            var vis = Math.Clamp(1.0 - hidden / t.HeightM, 0.0, 1.0);
            if (vis <= 0.0) continue;
            var outline = Encounter.ShipOutline(t.X, t.Y, t.HeadingRad, t.Loa, t.Beam);
            double cx = 0, cy = 0;
            for (var i = 0; i < 5; i++)
            {
                var ex = outline[i].E - _antX;
                var ny = outline[i].N - _antY;
                sx[i] = ex * _cosH - ny * _sinH;
                fy[i] = ex * _sinH + ny * _cosH;
                cx += sx[i];
                cy += fy[i];
            }
            cx /= 5;
            cy /= 5;
            var p = _targets[_targetCount];
            var tsx = dx * _cosH - dy * _sinH;
            var tfy = dx * _sinH + dy * _cosH;
            p.AC = Math.Atan2(tsx, tfy);
            p.Range = range;
            double omin = double.PositiveInfinity, omax = double.NegativeInfinity;
            var imin = 0;
            var imax = 0;
            for (var i = 0; i < 5; i++)
            {
                var o = Units.WrapRadPi(Math.Atan2(sx[i], fy[i]) - p.AC);
                if (o < omin) { omin = o; imin = i; }
                if (o > omax) { omax = o; imax = i; }
            }
            double rn0 = double.PositiveInfinity, rf0 = 0;
            for (var i = 0; i < 5; i++)
            {
                var d = Math.Sqrt(sx[i] * sx[i] + fy[i] * fy[i]);
                if (d < rn0) rn0 = d;
                if (d > rf0) rf0 = d;
            }
            p.RNear = rn0;
            p.RFar = rf0;
            p.OffMin = omin;
            p.OffMax = omax;
            p.Ext = Math.Max(omax - omin, 1e-7);
            // 角寬小於波束寬:每條線都照到整個船體 → 距離段取船體全部徑向範圍(遠距目標回波為完整矩形)
            p.Small = p.Ext < Units.DegToRad(_cfg.BeamwidthDeg);
            var lmin = Math.Sqrt(sx[imin] * sx[imin] + fy[imin] * fy[imin]);
            var lmax = Math.Sqrt(sx[imax] * sx[imax] + fy[imax] * fy[imax]);
            p.DirMinX = sx[imin] / lmin; p.DirMinY = fy[imin] / lmin;
            p.DirMaxX = sx[imax] / lmax; p.DirMaxY = fy[imax] / lmax;
            for (var i = 0; i < 5; i++)
            {
                var j = (i + 1) % 5;
                var nx = fy[j] - fy[i];
                var ny = -(sx[j] - sx[i]);
                if (nx * (cx - sx[i]) + ny * (cy - fy[i]) > 0) { nx = -nx; ny = -ny; }
                p.Nx[i] = nx;
                p.Ny[i] = ny;
                p.Na[i] = nx * sx[i] + ny * fy[i];
            }
            // 視角:正橫最強(0.4 + 0.6|sin 視角|);地平線部分遮蔽以可見高度比例平方衰減
            var bearing = Math.Atan2(dx, dy);
            var aspect = t.HeadingRad - bearing;
            p.Sigma = t.RcsM2 * vis * vis * (0.4 + 0.6 * Math.Abs(Math.Sin(aspect)));
            _targetCount++;
        }
    }

    /// <summary>計算方位線 k(相對艏向)的回波幾何。</summary>
    public void Compute(int k, SpokeGeometry g)
    {
        g.Reset();
        var sinPhi = _spokeSin[k];
        var cosPhi = _spokeCos[k];
        g.ShadowLin = _shadowLin[k];
        // 絕對方向 θ = ψ + φ
        var sinT = _sinH * cosPhi + _cosH * sinPhi;
        var cosT = _cosH * cosPhi - _sinH * sinPhi;
        g.SeaAzMod = Math.Pow(10.0, 0.3 * (cosT * _windCos + sinT * _windSin));

        // 陸地:波束寬內取最近的入射距離(海岸線受波束展寬)
        if (_env.Land.Count > 0)
        {
            for (var j = -_smear; j <= _smear; j++)
            {
                var kk = ((k + j) % _cfg.Spokes + _cfg.Spokes) % _cfg.Spokes;
                if (_landStamp[kk] != _stamp) ComputeLand(kk);
                if (_landEntry[kk] < g.LandEntry)
                {
                    g.LandEntry = _landEntry[kk];
                    g.LandExit = _landExit[kk];
                    g.LandFactor = _landFactor[kk];
                }
            }
        }

        var phi = k * _cfg.SpokeAngleRad;
        var limit = 4.5 * _sigmaB; // 雙程主瓣 −88 dB 處截斷(強回波的方位剖面完整)
        var inv = 1.0 / (Math.Sqrt(2.0) * _sigmaB);
        var norm = _sigmaB * Math.Sqrt(2.0 * Math.PI);
        for (var i = 0; i < _targetCount; i++)
        {
            var p = _targets[i];
            var d = Units.WrapRadPi(phi - p.AC);
            if (d < p.OffMin - limit || d > p.OffMax + limit) continue;
            var f = 0.5 * (Erf((p.OffMax - d) * inv) - Erf((p.OffMin - d) * inv));
            var beam = f * norm / p.Ext;
            if (beam < 1e-9) continue;
            if (p.Small)
            {
                if (p.RNear > g.LandEntry) continue;
                g.AddSeg(p.RNear, p.RFar, p.Sigma * beam);
                continue;
            }
            double rx, ry;
            if (d < p.OffMin) { rx = p.DirMinX; ry = p.DirMinY; }
            else if (d > p.OffMax) { rx = p.DirMaxX; ry = p.DirMaxY; }
            else { rx = sinPhi; ry = cosPhi; }
            double tIn = 0.0, tOut = double.PositiveInfinity;
            var hit = true;
            for (var e = 0; e < 5; e++)
            {
                var nd = p.Nx[e] * rx + p.Ny[e] * ry;
                var na = p.Na[e];
                if (Math.Abs(nd) < 1e-12)
                {
                    if (na < 0) { hit = false; break; }
                    continue;
                }
                var t = na / nd;
                if (nd < 0) { if (t > tIn) tIn = t; }
                else if (t < tOut) tOut = t;
            }
            if (!hit || tIn > tOut || double.IsInfinity(tOut)) { tIn = p.Range; tOut = p.Range; }
            if (tIn > g.LandEntry) continue; // 陸地後方被遮蔽
            g.AddSeg(tIn, tOut, p.Sigma * beam);
        }

        // 雨區(圓;檔案/教官與情境):射線與圓的交段
        foreach (var rc in _env.RainCells) AddRainCell(g, rc, sinT, cosT);
        foreach (var rc in _env.ScenarioRainCells) AddRainCell(g, rc, sinT, cosT);
        // 雨區(多邊形;情境):射線與多邊形的交段(凹多邊形可有多段;天線在雨區內時自 0 起)
        foreach (var rp in _env.ScenarioRainPolygons) AddRainPolygon(g, rp, sinT, cosT);
    }

    private void AddRainCell(SpokeGeometry g, RainCell rc, double sinT, double cosT)
    {
        var cxr = rc.X - _antX;
        var cyr = rc.Y - _antY;
        var proj = cxr * sinT + cyr * cosT;
        var perp2 = cxr * cxr + cyr * cyr - proj * proj;
        var r2 = rc.RadiusM * rc.RadiusM;
        if (perp2 >= r2) return;
        var h = Math.Sqrt(r2 - perp2);
        var r0 = Math.Max(0.0, proj - h);
        var r1 = proj + h;
        if (r1 <= 0 || rc.RateMmH <= 0) return;
        AddRainSegment(g, r0, r1, rc.RateMmH);
    }

    /// <summary>雨段:反射率 Z = 200 R^1.6(Marshall–Palmer)、雙程衰減 2·k·R^α dB/km。</summary>
    private void AddRainSegment(SpokeGeometry g, double r0, double r1, double rateMmH)
    {
        var z = 200.0 * Math.Pow(rateMmH, 1.6);
        var gamma = 2.0 * _cfg.RainAttenK * Math.Pow(rateMmH, _cfg.RainAttenAlpha) / 1000.0; // 雙程 dB/m
        g.AddRain(r0, r1, z, gamma);
    }

    private readonly List<double> _rainHits = new();

    private void AddRainPolygon(SpokeGeometry g, RainPolygon rp, double sinT, double cosT)
    {
        if (rp.RateMmH <= 0) return;
        var pts = rp.Points;
        _rainHits.Clear();
        var inside = false;
        for (var i = 0; i < pts.Length; i++)
        {
            var (ax, ay) = pts[i];
            var (bx, by) = pts[(i + 1) % pts.Length];
            ax -= _antX; ay -= _antY; bx -= _antX; by -= _antY;
            // 天線(原點)是否在多邊形內(射線法,水平向右)
            if ((ay > 0) != (by > 0) && ax + (0 - ay) * (bx - ax) / (by - ay) > 0) inside = !inside;
            var ex = bx - ax;
            var ey = by - ay;
            var den = sinT * ey - cosT * ex;
            if (Math.Abs(den) < 1e-12) continue;
            // 射線 t·(sinT, cosT) = a + u·e
            var t = (ax * ey - ay * ex) / den;
            var u = (ax * cosT - ay * sinT) / den;
            if (u < 0 || u >= 1 || t <= 0) continue;
            _rainHits.Add(t);
        }
        _rainHits.Sort();
        var start = inside ? 0.0 : double.NaN;
        foreach (var t in _rainHits)
        {
            if (double.IsNaN(start)) start = t;
            else
            {
                if (t > start) AddRainSegment(g, start, t, rp.RateMmH);
                start = double.NaN;
            }
        }
    }

    private void ComputeLand(int k)
    {
        _landStamp[k] = _stamp;
        var sinPhi = _spokeSin[k];
        var cosPhi = _spokeCos[k];
        var dx = _sinH * cosPhi + _cosH * sinPhi;
        var dy = _cosH * cosPhi - _sinH * sinPhi;
        var bestEntry = double.PositiveInfinity;
        var bestExit = double.PositiveInfinity;
        var bestFactor = 0.0;
        foreach (var poly in _env.Land)
        {
            double t1 = double.PositiveInfinity, t2 = double.PositiveInfinity;
            var pts = poly.Points;
            var crossings = 0;
            for (var i = 0; i < pts.Length; i++)
            {
                var (ax, ay) = pts[i];
                var (bx, by) = pts[(i + 1) % pts.Length];
                ax -= _antX; ay -= _antY; bx -= _antX; by -= _antY;
                var ex = bx - ax;
                var ey = by - ay;
                var den = dx * ey - dy * ex;
                if (Math.Abs(den) < 1e-12) continue;
                // 射線 t·d = a + u·e
                var t = (ax * ey - ay * ex) / den;
                var u = (ax * dy - ay * dx) / den;
                if (u < 0 || u >= 1 || t <= 0) continue;
                crossings++;
                if (t < t1) { t2 = t1; t1 = t; }
                else if (t < t2) t2 = t;
            }
            if (crossings == 0) continue;
            if ((crossings & 1) == 1) { continue; } // 天線位於陸地內(不合理的情況)略過
            var hidden = RadarConfig.HiddenHeightM(_cfg.AntennaHeightM, t1 / Units.MetresPerNauticalMile);
            if (hidden >= poly.HeightM) continue;
            if (t1 < bestEntry)
            {
                bestEntry = t1;
                bestExit = t2;
                bestFactor = 1.0 - hidden / poly.HeightM;
            }
        }
        _landEntry[k] = bestEntry;
        _landExit[k] = bestExit;
        _landFactor[k] = bestFactor;
    }

    /// <summary>誤差函數(Abramowitz–Stegun 7.1.26,絕對誤差 &lt; 1.5e-7)。</summary>
    internal static double Erf(double x)
    {
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);
        if (x > 6) return sign;
        var t = 1.0 / (1.0 + 0.3275911 * x);
        var y = 1.0 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}
