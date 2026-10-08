using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics;

/// <summary>致動器參數(由 particulars 推得)。</summary>
public sealed record ActuatorParameters
{
    /// <summary>舵機速率(度/秒)= 35 / hardOverTime35(規劃書第 6.2 節「舵機以速率限制模型化」)</summary>
    public required double RudderRateDegPerSec { get; init; }
    /// <summary>一般操舵最大舵角(度)</summary>
    public required double RudderMaxDeg { get; init; }
    /// <summary>主機轉速一階滯後時間常數(秒)</summary>
    public double RpmTimeConstantSec { get; init; } = 12.0;
    /// <summary>最低轉速(rpm);null 表示不限制</summary>
    public double? MinimumRpm { get; init; }
    /// <summary>側推由 0 到全推力所需時間(秒)</summary>
    public double ThrusterFullDelaySec { get; init; } = 35.0;
    public bool HasBowThruster { get; init; }

    public static ActuatorParameters FromParticulars(ShipParticulars p) => new()
    {
        RudderRateDegPerSec = 35.0 / Math.Max(1.0, p.Rudder.HardOverTime35Seconds),
        RudderMaxDeg = p.Rudder.NormalMaxAngleDeg,
        MinimumRpm = p.Engine.MinimumRpm,
        ThrusterFullDelaySec = p.BowThruster?.FullThrustDelay_s ?? 35.0,
        HasBowThruster = p.BowThruster is not null,
    };
}

/// <summary>
/// 舵機、主機、側推的致動器動態(含狀態,故不放在純函數的 <see cref="IShipDynamics"/> 內):
/// 舵角以速率限制追隨舵令、轉速一階滯後追隨令、側推推力以固定斜率追隨令。每步以 dt 作顯式更新。
/// 所有欄位皆為快照內容。
/// </summary>
public sealed class ActuatorModel
{
    public ActuatorParameters Parameters { get; }

    /// <summary>舵令(度,右正)</summary>
    public double RudderOrderDeg { get; set; }
    /// <summary>實際舵角(度)</summary>
    public double RudderDeg { get; set; }
    /// <summary>轉速令(rpm,倒車負)</summary>
    public double RpmOrder { get; set; }
    /// <summary>實際轉速</summary>
    public double Rpm { get; set; }
    /// <summary>側推令(−1 至 +1)</summary>
    public double ThrusterOrder { get; set; }
    /// <summary>側推實際推力比例</summary>
    public double ThrusterActual { get; set; }

    public ActuatorModel(ActuatorParameters parameters) => Parameters = parameters;

    public ControlInput ToControlInput() => new(Units.DegToRad(RudderDeg), Rpm, ThrusterActual);

    /// <summary>
    /// 推進一步。<paramref name="rudderJammed"/>:舵機故障(舵角不動);
    /// <paramref name="engineFailed"/>:主機故障(轉速衰減到 0);<paramref name="thrusterFailed"/>:側推不可用。
    /// </summary>
    public void Step(double dt, bool rudderJammed = false, bool engineFailed = false, bool thrusterFailed = false)
    {
        var p = Parameters;

        // 舵機:速率限制
        RudderOrderDeg = Units.Clamp(RudderOrderDeg, -p.RudderMaxDeg, p.RudderMaxDeg);
        if (!rudderJammed)
        {
            var maxDelta = p.RudderRateDegPerSec * dt;
            var delta = RudderOrderDeg - RudderDeg;
            RudderDeg += Units.Clamp(delta, -maxDelta, maxDelta);
        }

        // 主機:一階滯後;最低轉速以下的非零令視為最低轉速
        var target = RpmOrder;
        if (engineFailed) target = 0.0;
        else if (p.MinimumRpm is { } min && target != 0.0 && Math.Abs(target) < min) target = Math.Sign(target) * min;
        Rpm += (target - Rpm) * (dt / p.RpmTimeConstantSec);
        if (Math.Abs(Rpm) < 1e-6 && target == 0.0) Rpm = 0.0;

        // 側推:固定斜率
        ThrusterOrder = p.HasBowThruster ? Units.Clamp(ThrusterOrder, -1.0, 1.0) : 0.0;
        var thrTarget = thrusterFailed ? 0.0 : ThrusterOrder;
        var thrRate = dt / Math.Max(0.1, p.ThrusterFullDelaySec);
        ThrusterActual += Units.Clamp(thrTarget - ThrusterActual, -thrRate, thrRate);
    }
}
