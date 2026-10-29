using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Sensors.Radar.Video;

/// <summary>確定性雜湊(SplitMix64 終結器):每格亂數由 (種子, 方位線全域序號, 格號) 決定,與執行緒與呼叫順序無關。</summary>
internal static class Hash
{
    public const ulong Golden = 0x9E3779B97F4A7C15UL;

    public static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>格亂數:兩次乘法的輕量混合(每格呼叫)。</summary>
    public static ulong Cell(ulong spokeBase, int cell)
    {
        var x = spokeBase + (ulong)(uint)cell * Golden;
        x ^= x >> 32;
        x *= 0xD6E8FEB86659FD93UL;
        x ^= x >> 29;
        return x;
    }
}

/// <summary>
/// 亂數查表(以情境種子產生一次):Exp(1) 分佈(Rayleigh 包絡的功率,雜訊與雜波起伏)與其 dB 值,
/// 避免每格呼叫 log;表長 65536。
/// </summary>
internal sealed class NoiseTables
{
    public const int Size = 1 << 16;
    public const int Mask = Size - 1;
    public readonly float[] Exp = new float[Size];
    public readonly float[] ExpDb = new float[Size];

    public NoiseTables(ulong seed)
    {
        var s = seed;
        for (var i = 0; i < Size; i++)
        {
            s += Hash.Golden;
            var u = ((Hash.Mix(s) >> 11) + 0.5) * (1.0 / (1UL << 53));
            var e = -Math.Log(u);
            Exp[i] = (float)e;
            ExpDb[i] = (float)(10.0 * Math.Log10(e));
        }
    }
}

/// <summary>
/// 一組距離格(量程、格數、脈衝長度)與其預算表(規劃書第 5.2 節:影像產生以預算表避免每格三角函數/對數):
/// 目標增益(雷達方程式 R⁻⁴)、海雜波平均(依海況、R⁻³ 至臨界距離後 R⁻⁷)、STC 曲線、陸地與雨的每格增益。
/// 顯示影像與 ARPA 處理影像各一組。
/// </summary>
public sealed class VideoGrid
{
    private readonly RadarConfig _cfg;
    public double RangeM { get; }
    public int Cells { get; }
    public double CellM { get; }
    public double PulseM { get; }
    public int FtcWindow { get; }

    internal readonly float[] TgtGain;
    internal readonly float[] SeaMean;
    internal readonly float[] StcDb;
    internal readonly float[] LandGain;
    internal readonly float[] RainUnit;
    internal int ClutterEnd;

    private double _seaState = double.NaN;
    private double _seaControl = double.NaN;

    public VideoGrid(RadarConfig cfg, double rangeM, int cells, double pulseM)
    {
        _cfg = cfg;
        RangeM = rangeM;
        Cells = cells;
        CellM = rangeM / cells;
        PulseM = pulseM;
        FtcWindow = Math.Max(3, (int)Math.Ceiling(2.0 * pulseM / CellM));
        TgtGain = new float[cells];
        SeaMean = new float[cells];
        StcDb = new float[cells];
        LandGain = new float[cells];
        RainUnit = new float[cells];
        var res = Math.Max(pulseM, CellM);
        var bwRad = Units.DegToRad(cfg.BeamwidthDeg);
        for (var i = 0; i < cells; i++)
        {
            var r = (i + 0.5) * CellM;
            var rn = Math.Max(r / Units.MetresPerNauticalMile, 0.01);
            var r4 = -40.0 * Math.Log10(rn);
            TgtGain[i] = (float)Math.Pow(10.0, (cfg.SystemConstantDb + r4) / 10.0);
            // 陸地:σ0 約 −15 dB,解析格面積 R·θ·ΔR
            var landDb = cfg.SystemConstantDb - 15.0 + 10.0 * Math.Log10(Math.Max(r, 1.0) * bwRad * res) + r4;
            LandGain[i] = (float)Math.Pow(10.0, landDb / 10.0);
            // 雨(體積散射 ∝ R²/R⁴):每單位 Z(mm⁶/m³)的訊雜比;3 nm、中脈衝、1° 時 dBZ − 8
            var rainDb = -8.0 + 20.0 * Math.Log10(3.0 / rn) + 10.0 * Math.Log10(res / 45.0) + 20.0 * Math.Log10(cfg.BeamwidthDeg) + cfg.RainOffsetDb;
            RainUnit[i] = (float)Math.Pow(10.0, rainDb / 10.0);
        }
    }

    public double RangeOf(int cell) => (cell + 0.5) * CellM;

    /// <summary>海況(Douglas)由真風速(kn)推估。</summary>
    public static double SeaStateFromWind(double windKn) => windKn switch
    {
        < 4 => 0,
        < 7 => 1,
        < 11 => 2,
        < 17 => 3,
        < 22 => 4,
        < 28 => 5,
        < 34 => 6,
        < 41 => 7,
        _ => 8,
    };

    /// <summary>
    /// 海況(Douglas)由有義波高 Hs(m)推估(docs/design/radar-arpa.md 第 2.1 節;核心 state.waves 存在且情境/教官未明示海況時用):
    /// Hs ≤ 0 → 0(平靜無波);&lt; 0.1 → 1;&lt; 0.5 → 2;&lt; 1.25 → 3;&lt; 2.5 → 4;&lt; 4 → 5;&lt; 6 → 6;&lt; 9 → 7;≥ 9 → 8(海雜波表上限)。
    /// </summary>
    public static double SeaStateFromWaveHeight(double hsM) => hsM switch
    {
        <= 0 => 0,
        < 0.1 => 1,
        < 0.5 => 2,
        < 1.25 => 3,
        < 2.5 => 4,
        < 4 => 5,
        < 6 => 6,
        < 9 => 7,
        _ => 8,
    };

    /// <summary>海雜波 1 nm 處的參考訊雜比(dB;X 頻、中脈衝、1°)依海況 0–8。</summary>
    private static readonly double[] SeaRefDb = { double.NegativeInfinity, 5, 12, 18, 23, 27, 30, 33, 35 };

    /// <summary>海況改變時重算海雜波平均表。</summary>
    public void UpdateClutter(double seaState)
    {
        if (seaState == _seaState) return;
        _seaState = seaState;
        var ss = Math.Clamp(seaState, 0, 8);
        var lo = (int)Math.Floor(ss);
        var hi = Math.Min(8, lo + 1);
        var frac = ss - lo;
        // 海況 0–1 之間以 20 log(ss) 漸弱,0 時無海雜波
        var refDb = lo == 0
            ? (ss <= 0.05 ? double.NegativeInfinity : SeaRefDb[1] + 20.0 * Math.Log10(ss))
            : SeaRefDb[lo] + (SeaRefDb[hi] - SeaRefDb[lo]) * frac;
        var rc = 0.3 + 0.25 * ss; // 臨界距離(nm)
        var res = Math.Max(PulseM, CellM);
        ClutterEnd = 0;
        for (var i = 0; i < Cells; i++)
        {
            if (double.IsNegativeInfinity(refDb)) { SeaMean[i] = 0f; continue; }
            var rn = Math.Max(RangeOf(i) / Units.MetresPerNauticalMile, 0.02);
            var db = refDb + _cfg.SeaClutterOffsetDb + 10.0 * Math.Log10(res / 45.0) + 10.0 * Math.Log10(_cfg.BeamwidthDeg)
                     - 30.0 * Math.Log10(rn) - (rn > rc ? 40.0 * Math.Log10(rn / rc) : 0.0);
            if (db < -20.0) { SeaMean[i] = 0f; continue; }
            SeaMean[i] = (float)Math.Pow(10.0, db / 10.0);
            ClutterEnd = i + 1;
        }
    }

    /// <summary>海雜波抑制(STC)曲線:sea% × min(60, 40 log(R_stc/R)) dB,R_stc = 3 nm(3 nm 外不影響)。</summary>
    public void UpdateStc(double seaControl)
    {
        if (seaControl == _seaControl) return;
        _seaControl = seaControl;
        var k = Math.Clamp(seaControl, 0, 100) / 100.0;
        for (var i = 0; i < Cells; i++)
        {
            var rn = Math.Max(RangeOf(i) / Units.MetresPerNauticalMile, 0.005);
            StcDb[i] = (float)(k * Math.Min(60.0, Math.Max(0.0, 40.0 * Math.Log10(3.0 / rn))));
        }
    }
}
