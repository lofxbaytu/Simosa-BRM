using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Traffic;

/// <summary>交通模組快照(進入 <see cref="Engine.EngineSnapshot"/>;舊快照無此欄位時沿用引擎目前的目標船)。</summary>
public sealed record TrafficSnapshot
{
    public required IReadOnlyList<TargetSnapshot> Targets { get; init; }
    public required bool Collision { get; init; }
    public required int NextDefaultMmsi { get; init; }
}

/// <summary>單一目標船的完整狀態(含目前有效定義,因指令可能已修改)。</summary>
public sealed record TargetSnapshot
{
    public required ScenarioTarget Spec { get; init; }
    public required int Mmsi { get; init; }
    public required bool Active { get; init; }
    public required long ActivatedTick { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Psi { get; init; }
    public required double Speed { get; init; }
    public required double R { get; init; }
    public required double VelE { get; init; }
    public required double VelN { get; init; }
    public required double HeadingCmdRad { get; init; }
    public required double SpeedCmdMps { get; init; }
    public required double RotLimitRadps { get; init; }
    public bool Manual { get; init; }
    public double ManualHeadingRad { get; init; }
    public double ManualSpeedMps { get; init; }
    public int WaypointIndex { get; init; }
    public int ScriptIndex { get; init; }
    public double AnchorX { get; init; }
    public double AnchorY { get; init; }
    public double AnchorBaseRad { get; init; }
    public ColregPhase ColregPhase { get; init; }
    public ColregForce ColregForce { get; init; }
    public double ColregHeadingRad { get; init; }
    public double ColregSpeedMps { get; init; }
    public double ColregTurnAccumRad { get; init; }
    public long ColregActionTick { get; init; } = -1;
    public long ColregResumeTick { get; init; } = -1;
    public string? ColregSituation { get; init; }
    /// <summary>最小 CPA(nm;尚無時為 null,因 JSON 不能表示無限大)</summary>
    public double? MinCpaNm { get; init; }
    public double? MinRangeNm { get; init; }
    public bool CpaAlarm { get; init; }
    public string? Sound { get; init; }
    public long SoundUntilTick { get; init; } = -1;

    public static TargetSnapshot From(TargetShip t) => new()
    {
        Spec = t.Spec,
        Mmsi = t.Mmsi,
        Active = t.Active,
        ActivatedTick = t.ActivatedTick,
        X = t.X, Y = t.Y, Psi = t.Psi, Speed = t.Speed, R = t.R, VelE = t.VelE, VelN = t.VelN,
        HeadingCmdRad = t.HeadingCmdRad, SpeedCmdMps = t.SpeedCmdMps, RotLimitRadps = t.RotLimitRadps,
        Manual = t.Manual, ManualHeadingRad = t.ManualHeadingRad, ManualSpeedMps = t.ManualSpeedMps,
        WaypointIndex = t.WaypointIndex, ScriptIndex = t.ScriptIndex,
        AnchorX = t.AnchorX, AnchorY = t.AnchorY, AnchorBaseRad = t.AnchorBaseRad,
        ColregPhase = t.ColregPhase, ColregForce = t.ColregForce,
        ColregHeadingRad = t.ColregHeadingRad, ColregSpeedMps = t.ColregSpeedMps, ColregTurnAccumRad = t.ColregTurnAccumRad,
        ColregActionTick = t.ColregActionTick, ColregResumeTick = t.ColregResumeTick, ColregSituation = t.ColregSituation,
        MinCpaNm = double.IsFinite(t.MinCpaNm) ? t.MinCpaNm : null,
        MinRangeNm = double.IsFinite(t.MinRangeNm) ? t.MinRangeNm : null,
        CpaAlarm = t.CpaAlarm,
        Sound = t.Sound, SoundUntilTick = t.SoundUntilTick,
    };

    public TargetShip ToTarget()
    {
        var t = new TargetShip(Spec, Mmsi)
        {
            Active = Active,
            ActivatedTick = ActivatedTick,
            X = X, Y = Y, Psi = Psi, Speed = Speed, R = R, VelE = VelE, VelN = VelN,
            HeadingCmdRad = HeadingCmdRad, SpeedCmdMps = SpeedCmdMps, RotLimitRadps = RotLimitRadps,
            Manual = Manual, ManualHeadingRad = ManualHeadingRad, ManualSpeedMps = ManualSpeedMps,
            WaypointIndex = WaypointIndex, ScriptIndex = ScriptIndex,
            AnchorX = AnchorX, AnchorY = AnchorY, AnchorBaseRad = AnchorBaseRad,
            ColregPhase = ColregPhase, ColregForce = ColregForce,
            ColregHeadingRad = ColregHeadingRad, ColregSpeedMps = ColregSpeedMps, ColregTurnAccumRad = ColregTurnAccumRad,
            ColregActionTick = ColregActionTick, ColregResumeTick = ColregResumeTick, ColregSituation = ColregSituation,
            MinCpaNm = MinCpaNm ?? double.PositiveInfinity,
            MinRangeNm = MinRangeNm ?? double.PositiveInfinity,
            CpaAlarm = CpaAlarm,
            Sound = Sound, SoundUntilTick = SoundUntilTick,
        };
        return t;
    }
}
