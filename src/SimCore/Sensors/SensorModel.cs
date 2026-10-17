using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Sensors;

/// <summary>某一時刻的真值(引擎由運動狀態計算;對外單位:度、kn、m)。</summary>
public readonly record struct SensorTruth(double HeadingDeg, double X, double Y, double CogDeg, double SogKn, double StwKn, double DepthBelowKeelM);

/// <summary>
/// 指示值(儀器、NMEA、自動舵看到的值;設計文件 docs/design/sensors-alerts.md 第 2 節)。無效時數值為「最後有效值」(保持顯示),
/// 並以 *Valid = false 標示;消費端(儀器、ECDIS)應顯示為無效/可疑(規劃書第 7.2 節 conning「感測器失效造成的資料標記為可疑」)。
/// </summary>
public readonly record struct SensorReadings(
    double HeadingDeg, bool HeadingValid,
    double X, double Y, bool PositionValid,
    double CogDeg, double SogKn,
    double StwKn, bool StwValid,
    double DepthBelowKeelM, bool DepthValid,
    double MagneticHeadingDeg);

/// <summary>感測器層的可變狀態(進快照與雜湊;JSON 可往返)。</summary>
public sealed class SensorState
{
    // ---- 電羅經:漂移以封閉式計算(drift = base + rate × (tick − start) × dt / 60),不逐步累加,倒帶與重播位元一致
    public double GyroDriftBaseDeg { get; set; }
    /// <summary>漂移起算 tick(−1 = 未漂移)</summary>
    public long GyroDriftStartTick { get; set; } = -1;
    public double GyroDriftRateDegPerMin { get; set; }
    /// <summary>gyroFreeze 注入時的指示航向(度)</summary>
    public double? GyroFrozenDeg { get; set; }
    /// <summary>gyroFailure 注入時的最後指示航向(度;顯示保持值)</summary>
    public double? GyroHeldDeg { get; set; }

    // ---- GNSS(ENU m)
    public double GpsJumpE { get; set; }
    public double GpsJumpN { get; set; }
    public double GpsOffsetE { get; set; }
    public double GpsOffsetN { get; set; }
    /// <summary>gpsOffset 漸進起算 tick(−1 = 無偏移)</summary>
    public long GpsOffsetStartTick { get; set; } = -1;
    public double GpsOffsetRampS { get; set; }
    public double? GpsHeldX { get; set; }
    public double? GpsHeldY { get; set; }
    public double? GpsHeldCogDeg { get; set; }
    public double? GpsHeldSogKn { get; set; }

    // ---- 測深儀、計程儀
    public double? DepthHeldM { get; set; }
    public double? StwHeldKn { get; set; }
    /// <summary>logFailure 為偏差模式(STW = 真值 × factor + bias,仍為「有效」值,無警報)</summary>
    public bool LogBiased { get; set; }
    public double LogBiasKn { get; set; }
    public double LogFactor { get; set; } = 1.0;

    /// <summary>所有欄位皆為預設值(無感測器故障、無殘留):雜湊不追加感測器位元組。</summary>
    [JsonIgnore] public bool IsNominal =>
        GyroDriftBaseDeg == 0 && GyroDriftStartTick < 0 && GyroFrozenDeg is null && GyroHeldDeg is null &&
        GpsJumpE == 0 && GpsJumpN == 0 && GpsOffsetStartTick < 0 && GpsHeldX is null &&
        DepthHeldM is null && StwHeldKn is null && !LogBiased;
}

/// <summary>
/// 感測器層(規劃書第 7.2 節表列故障;設計文件第 2 節):依故障把真值轉為指示值。故障清單以引擎的 faults[] 為準(快照還原後一致),
/// 本類別只保存注入時需要記住的量(漂移起點、凍結/保持值、偏移量)。所有計算只用 tick 與注入參數,無亂數。
/// </summary>
public sealed class SensorModel
{
    private readonly IReadOnlyList<string> _faults;
    private readonly double _dt;
    private readonly ScenarioSensors _config;

    public SensorState State { get; private set; } = new();

    public SensorModel(ScenarioSensors config, IReadOnlyList<string> faults, double dt)
    {
        _config = config;
        _faults = faults;
        _dt = dt;
    }

    private bool Has(string f) => _faults.Contains(f);

    /// <summary>電羅經目前漂移量(度)。</summary>
    public double GyroDriftDeg(long tick)
        => State.GyroDriftStartTick >= 0
            ? State.GyroDriftBaseDeg + State.GyroDriftRateDegPerMin * (tick - State.GyroDriftStartTick) * _dt / 60.0
            : State.GyroDriftBaseDeg;

    /// <summary>電羅經指示航向(度,0–360)與是否有效。</summary>
    public (double Deg, bool Valid) GyroHeading(double truthDeg, long tick)
    {
        if (Has(FaultNames.GyroFailure)) return (State.GyroHeldDeg ?? Units.NormalizeHeadingDeg(truthDeg), false);
        if (Has(FaultNames.GyroFreeze) && State.GyroFrozenDeg is { } fz) return (fz, true);
        return (Units.NormalizeHeadingDeg(truthDeg + GyroDriftDeg(tick)), true);
    }

    /// <summary>磁羅經指示(修正後的真航向,度):真值 + 剩餘誤差;不受電羅經故障影響。</summary>
    public double MagneticHeading(double truthDeg) => Units.NormalizeHeadingDeg(truthDeg + _config.MagneticCompassErrorDeg);

    private (double E, double N) GpsDisplacement(long tick)
    {
        var e = State.GpsJumpE;
        var n = State.GpsJumpN;
        if (State.GpsOffsetStartTick >= 0)
        {
            var frac = State.GpsOffsetRampS > 0 ? Units.Clamp((tick - State.GpsOffsetStartTick) * _dt / State.GpsOffsetRampS, 0.0, 1.0) : 1.0;
            e += State.GpsOffsetE * frac;
            n += State.GpsOffsetN * frac;
        }
        return (e, n);
    }

    /// <summary>全部指示值。</summary>
    public SensorReadings Read(SensorTruth t, long tick)
    {
        var (hdg, hdgValid) = GyroHeading(t.HeadingDeg, tick);

        double x, y, cog, sog;
        bool posValid;
        if (Has(FaultNames.GpsFailure))
        {
            x = State.GpsHeldX ?? t.X; y = State.GpsHeldY ?? t.Y; cog = State.GpsHeldCogDeg ?? t.CogDeg; sog = State.GpsHeldSogKn ?? t.SogKn;
            posValid = false;
        }
        else
        {
            var (de, dn) = GpsDisplacement(tick);
            x = t.X + de; y = t.Y + dn; cog = t.CogDeg; sog = t.SogKn;
            posValid = true;
        }

        double stw;
        bool stwValid;
        if (Has(FaultNames.LogFailure))
        {
            if (State.LogBiased) { stw = t.StwKn * State.LogFactor + State.LogBiasKn; stwValid = true; }
            else { stw = State.StwHeldKn ?? t.StwKn; stwValid = false; }
        }
        else { stw = t.StwKn; stwValid = true; }

        var depthValid = !Has(FaultNames.EchoSounderFailure);
        var depth = depthValid ? t.DepthBelowKeelM : State.DepthHeldM ?? t.DepthBelowKeelM;

        return new SensorReadings(hdg, hdgValid, x, y, posValid, cog, sog, stw, stwValid, depth, depthValid, MagneticHeading(t.HeadingDeg));
    }

    /// <summary>logFailure 是否為「無效」模式(偏差模式不發警報)。</summary>
    public bool LogInvalid => Has(FaultNames.LogFailure) && !State.LogBiased;

    /// <summary>
    /// 故障注入(引擎已把名稱加入 faults[] 之前呼叫;<paramref name="alreadyActive"/> 為重複注入):記下注入時的指示值/參數。
    /// 回傳 true 表示此故障屬感測器層。
    /// </summary>
    public bool OnFaultInjected(string fault, JsonElement? args, long tick, SensorTruth truth, bool alreadyActive)
    {
        var s = State;
        switch (fault)
        {
            case FaultNames.GyroDrift:
            {
                var rate = Num(args, "rate_degPerMin") ?? 1.0;
                // 重複注入:自目前漂移量起以新速率續漂(不跳回 0)
                s.GyroDriftBaseDeg = alreadyActive ? GyroDriftDeg(tick) : 0.0;
                s.GyroDriftStartTick = tick;
                s.GyroDriftRateDegPerMin = rate;
                return true;
            }
            case FaultNames.GyroFreeze:
                if (!alreadyActive) s.GyroFrozenDeg = GyroHeading(truth.HeadingDeg, tick).Deg;
                return true;
            case FaultNames.GyroFailure:
                if (!alreadyActive) s.GyroHeldDeg = GyroHeading(truth.HeadingDeg, tick).Deg;
                return true;
            case FaultNames.GpsJump:
            {
                var d = Num(args, "distance_m") ?? 500.0;
                var b = Units.DegToRad(Num(args, "bearing_deg") ?? 90.0);
                s.GpsJumpE = d * Math.Sin(b);
                s.GpsJumpN = d * Math.Cos(b);
                return true;
            }
            case FaultNames.GpsOffset:
            {
                var d = Num(args, "distance_m") ?? 200.0;
                var b = Units.DegToRad(Num(args, "bearing_deg") ?? 45.0);
                s.GpsOffsetE = d * Math.Sin(b);
                s.GpsOffsetN = d * Math.Cos(b);
                s.GpsOffsetStartTick = tick;
                s.GpsOffsetRampS = Math.Max(0.0, Num(args, "ramp_s") ?? 60.0);
                return true;
            }
            case FaultNames.GpsFailure:
                if (!alreadyActive)
                {
                    var r = Read(truth, tick);
                    (s.GpsHeldX, s.GpsHeldY, s.GpsHeldCogDeg, s.GpsHeldSogKn) = (r.X, r.Y, r.CogDeg, r.SogKn);
                }
                return true;
            case FaultNames.EchoSounderFailure:
                if (!alreadyActive) s.DepthHeldM = truth.DepthBelowKeelM;
                return true;
            case FaultNames.LogFailure:
            {
                var bias = Num(args, "bias_kn");
                var factor = Num(args, "factor");
                if (bias is not null || factor is not null)
                {
                    s.LogBiased = true;
                    s.LogBiasKn = bias ?? 0.0;
                    s.LogFactor = factor ?? 1.0;
                    s.StwHeldKn = null;
                }
                else
                {
                    if (!alreadyActive || s.LogBiased) s.StwHeldKn = Read(truth, tick).StwKn;
                    s.LogBiased = false;
                    s.LogBiasKn = 0.0;
                    s.LogFactor = 1.0;
                }
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>故障清除(引擎已自 faults[] 移除後呼叫):回到真值。</summary>
    public void OnFaultCleared(string fault)
    {
        var s = State;
        switch (fault)
        {
            case FaultNames.GyroDrift: s.GyroDriftBaseDeg = 0; s.GyroDriftStartTick = -1; s.GyroDriftRateDegPerMin = 0; break;
            case FaultNames.GyroFreeze: s.GyroFrozenDeg = null; break;
            case FaultNames.GyroFailure: s.GyroHeldDeg = null; break;
            case FaultNames.GpsJump: s.GpsJumpE = 0; s.GpsJumpN = 0; break;
            case FaultNames.GpsOffset: s.GpsOffsetE = 0; s.GpsOffsetN = 0; s.GpsOffsetStartTick = -1; s.GpsOffsetRampS = 0; break;
            case FaultNames.GpsFailure: s.GpsHeldX = s.GpsHeldY = s.GpsHeldCogDeg = s.GpsHeldSogKn = null; break;
            case FaultNames.EchoSounderFailure: s.DepthHeldM = null; break;
            case FaultNames.LogFailure: s.StwHeldKn = null; s.LogBiased = false; s.LogBiasKn = 0; s.LogFactor = 1.0; break;
        }
    }

    private static double? Num(JsonElement? args, string name)
        => args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) ? SimCommand.AsDouble(v) : null;

    // ------------------------------------------------------------------ 快照 / 雜湊

    public const int HashLongs = 20;

    /// <summary>寫入雜湊位元組(<see cref="HashLongs"/> 個 int64;只在非 nominal 時由引擎呼叫)。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var s = State;
        static long D(double v) => BitConverter.DoubleToInt64Bits(v);
        long[] v =
        {
            D(s.GyroDriftBaseDeg), s.GyroDriftStartTick, D(s.GyroDriftRateDegPerMin), D(s.GyroFrozenDeg ?? double.NaN), D(s.GyroHeldDeg ?? double.NaN),
            D(s.GpsJumpE), D(s.GpsJumpN), D(s.GpsOffsetE), D(s.GpsOffsetN), s.GpsOffsetStartTick, D(s.GpsOffsetRampS),
            D(s.GpsHeldX ?? double.NaN), D(s.GpsHeldY ?? double.NaN), D(s.GpsHeldCogDeg ?? double.NaN), D(s.GpsHeldSogKn ?? double.NaN),
            D(s.DepthHeldM ?? double.NaN), D(s.StwHeldKn ?? double.NaN), s.LogBiased ? 1 : 0, D(s.LogBiasKn), D(s.LogFactor),
        };
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(buf[(i * 8)..], v[i]);
        return v.Length * 8;
    }

    public SensorState CreateSnapshot() => ContractJson.Deserialize<SensorState>(ContractJson.Serialize(State))!;

    public void Restore(SensorState? s) => State = s is null ? new SensorState() : ContractJson.Deserialize<SensorState>(ContractJson.Serialize(s))!;
}
