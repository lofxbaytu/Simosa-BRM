using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Sensors.Radar;

/// <summary>保護區(自動擷取區;MSC.192(79) 擷取區):距離環 inner–outer(nm)、方位 From→To 順時針(相對或真方位,度)。</summary>
public sealed record GuardZone(bool Enabled, double InnerNm, double OuterNm, double FromDeg = 0.0, double ToDeg = 360.0, bool Relative = true)
{
    public bool AllRound => Math.Abs(ToDeg - FromDeg) >= 360.0 - 1e-9 || (Units.NormalizeHeadingDeg(FromDeg) == Units.NormalizeHeadingDeg(ToDeg) && ToDeg != FromDeg);

    public bool Contains(double rangeNm, double trueBearingDeg, double headingDeg)
    {
        if (!Enabled || rangeNm < InnerNm || rangeNm > OuterNm) return false;
        if (AllRound) return true;
        var b = Units.NormalizeHeadingDeg(Relative ? trueBearingDeg - headingDeg : trueBearingDeg);
        var f = Units.NormalizeHeadingDeg(FromDeg);
        var t = Units.NormalizeHeadingDeg(ToDeg);
        return f <= t ? b >= f && b <= t : b >= f || b <= t;
    }
}

/// <summary>試操船(MSC.192(79) 第 5.33 節):延遲 DelayMin 分鐘後改為 Course(度)/Speed(kn),各目標 CPA/TCPA 依此重算。</summary>
public sealed record TrialManoeuvre(double CourseDeg, double SpeedKn, double DelayMin);

/// <summary>脈衝長度設定(依量程自動選擇;cτ/2 公尺)。</summary>
public enum PulseLength
{
    Short,
    Medium,
    Long,
}

/// <summary>操作員控制(增益/海雜波 STC/雨雜波 FTC/調諧/量程/待機)。數值 0–100(%)。</summary>
public sealed class RadarControls
{
    /// <summary>標準量程(nm;MSC.192(79) 0.25–96 nm)</summary>
    public static readonly double[] StandardRanges = { 0.125, 0.25, 0.5, 0.75, 1.5, 3, 6, 12, 24, 48, 96 };

    public double RangeNm { get; set; } = 6.0;
    public double Gain { get; set; } = 70.0;
    public double Sea { get; set; } = 50.0;
    public double Rain { get; set; }
    public double Tune { get; set; } = 50.0;
    public bool AutoTune { get; set; } = true;
    public bool Transmit { get; set; } = true;
    public GuardZone Guard { get; set; } = new(false, 0.5, 3.0);
    public TrialManoeuvre? Trial { get; set; }

    /// <summary>依量程選擇脈衝:≤ 0.75 nm 短(0.07 µs,10.5 m)、≤ 3 nm 中(0.3 µs,45 m)、≤ 12 nm 中長(0.5 µs,75 m)、以上長(1.2 µs,180 m)。</summary>
    public static (PulseLength Pulse, double PulseM) PulseFor(double rangeNm) => rangeNm switch
    {
        <= 0.75 => (PulseLength.Short, 10.5),
        <= 3.0 => (PulseLength.Medium, 45.0),
        <= 12.0 => (PulseLength.Medium, 75.0),
        _ => (PulseLength.Long, 180.0),
    };

    /// <summary>調諧損失(dB):偏離最佳點(50)時靈敏度下降;自動調諧為 0。</summary>
    public double TuneLossDb => AutoTune ? 0.0 : 20.0 * Math.Pow((Tune - 50.0) / 50.0, 2);

    /// <summary>增益門檻(dB,相對雜訊平均功率):增益 100 → −3 dB(雜訊斑點滿佈)、0 → 40 dB。</summary>
    public double GainThresholdDb => 40.0 - 0.43 * Math.Clamp(Gain, 0, 100);

    public RadarControls Clone() => (RadarControls)MemberwiseClone();
}
