using System.Buffers.Binary;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>指令處理結果:Reject = 拒絕原因(null = 接受);接受時引擎發 Kind 事件(itemId、message、data)。</summary>
public readonly record struct ExternalCommandOutcome(string? Reject, string? Kind = null, string? ItemId = null, string? Message = null, object? Data = null)
{
    public static ExternalCommandOutcome Rejected(string reason) => new(reason);
}

/// <summary>
/// 外力模組(規劃書第 6.2 節 T 拖船、A 錨/纜與碰墊;設計文件 docs/design/physics-tugs-anchors-mooring.md):
/// 拖船、兩錨、泊位(碰墊 + 碼頭硬接觸 + 繫纜)的合力,供 RK4 每階段計算(<see cref="IExternalForceField"/>)。
/// 跨步狀態只在 <see cref="PreStep"/>(拖船出力/方向)與 <see cref="PostStep"/>(鏈長、錨位、接觸事件、絞纜)中改變。
/// <see cref="Active"/> 為 false 時(情境沒有拖船/泊位且錨未用過,或動力學不支援外力)引擎走舊積分路徑、不追加雜湊位元組。
/// </summary>
public sealed class ExternalForceModel : IExternalForceField
{
    public const long HashMarker = 0x45585446_4F524345; // "EXTFORCE"

    private readonly List<TugUnit> _tugs = new();
    private readonly List<AnchorUnit> _anchors = new();
    private readonly ShipParticulars _ship;

    public IReadOnlyList<TugUnit> Tugs => _tugs;
    public IReadOnlyList<AnchorUnit> Anchors => _anchors;
    public BerthModel? Berth { get; }
    public AnchorParticulars? AnchorSpec { get; }
    public Seabed Seabed { get; }
    public double HoldingFactor { get; }
    public double DragSpeedMps { get; }
    /// <summary>動力學模型接受外力(<see cref="IShipDynamics.SupportsExternalForces"/>)</summary>
    public bool Supported { get; }
    /// <summary>錨曾被使用(指令或初始已錨泊;黏著旗標,進快照)</summary>
    public bool AnchorsUsed { get; private set; }
    public double ShipMassKg { get; }
    public bool Active => Supported && (_tugs.Count > 0 || Berth is not null || AnchorsUsed);

    public ExternalForceModel(ShipParticulars ship, LoadingCondition loading, Scenario.Scenario sc, LocalTangentPlane proj, double dt, bool supported,
        in StateVector initial)
    {
        _ship = ship;
        Supported = supported;
        var lc = ship.GetLoading(loading);
        ShipMassKg = lc.Displacement_t is { } disp && disp > 0 ? disp * 1000.0
            : 1025.0 * ship.Hull.LengthBetweenPerpendiculars_m * ship.Hull.Breadth_m * (lc.DraftMean_m ?? lc.MaxDraft_m) * (lc.BlockCoefficient ?? 0.75);
        Seabed = (sc.Environment.Seabed ?? "sand") switch { "mud" => Seabed.Mud, "clay" => Seabed.Clay, "rock" => Seabed.Rock, _ => Seabed.Sand };
        HoldingFactor = sc.Anchors?.HoldingFactor ?? 1.0;
        DragSpeedMps = sc.Anchors?.DragSpeedMps ?? 0.5;
        foreach (var t in sc.Tugs ?? new List<Scenario.ScenarioTug>()) _tugs.Add(new TugUnit(t, ship, dt));
        AnchorSpec = AnchorParticulars.FromShip(ship);
        if (AnchorSpec is { } spec)
        {
            var draftFore = lc.DraftFore_m ?? lc.DraftMean_m ?? lc.MaxDraft_m;
            _anchors.Add(new AnchorUnit("port", spec, draftFore));
            _anchors.Add(new AnchorUnit("starboard", spec, draftFore));
            var depth = sc.Environment.WaterDepth;
            foreach (var (side, init) in new[] { ("port", sc.Anchors?.Port), ("starboard", sc.Anchors?.Starboard) })
            {
                var len = init?.LengthM ?? (init?.LengthShackles is { } sh ? sh * spec.ShackleLengthM : null);
                if (len is not > 0) continue;
                var bearing = Units.DegToRad(init!.BearingDeg ?? Units.RadToDeg(initial.Psi));
                Anchor(side)!.InitialiseAnchored(initial, len.Value, bearing, depth, Seabed, HoldingFactor);
                AnchorsUsed = true;
            }
        }
        if (sc.Berth is { Definition: { } def } b)
        {
            Berth = new BerthModel(def, b.Lines, ship, ShipMassKg, proj);
            var env0 = new EnvironmentSample(0, 0, 0, 0, sc.Environment.WaterDepth);
            foreach (var (line, cfg) in Berth.Lines.Zip(b.Lines))
                if (cfg.Initial == "fast") Berth.Command(line, "makeFast", null, null, null, initial, env0);
        }
    }

    public TugUnit? Tug(string? id) => id is null ? null : _tugs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    public AnchorUnit? Anchor(string? side) => side?.ToLowerInvariant() switch
    {
        "port" or "p" => _anchors.FirstOrDefault(a => a.Side == "port"),
        "starboard" or "stbd" or "s" => _anchors.FirstOrDefault(a => a.Side == "starboard"),
        _ => null,
    };

    // ------------------------------------------------------------------ 力

    public BodyForce Evaluate(in StateVector s, in EnvironmentSample env)
    {
        var total = BodyForce.Zero;
        foreach (var t in _tugs) total += t.Force();
        foreach (var a in _anchors) total += a.Force(s, env.WaterDepthM);
        if (Berth is not null) total += Berth.Force(s, env);
        return total;
    }

    /// <summary>步首:拖船出力與方向(以步首的自船對水速度計算效能)。</summary>
    public void PreStep(in StateVector s, double dt)
    {
        var speed = Math.Sqrt(s.U * s.U + s.V * s.V);
        foreach (var t in _tugs) t.PreStep(dt, speed);
    }

    /// <summary>步末:錨(鏈長/走錨/事件)、泊位(碰墊事件/硬接觸/繫纜)。</summary>
    public void PostStep(in StateVector s, in EnvironmentSample env, double dt, List<ExternalEvent> events)
    {
        if (_anchors.Count > 0 && AnchorsUsed)
        {
            var (ve, vn) = EnvironmentMath.GroundVelocity(s.U, s.V, s.Psi, env.CurrentEastMps, env.CurrentNorthMps);
            var sog = Math.Sqrt(ve * ve + vn * vn);
            foreach (var a in _anchors) a.PostStep(s, env.WaterDepthM, dt, Seabed, HoldingFactor, DragSpeedMps, sog, events);
        }
        Berth?.PostStep(s, env, dt, events);
    }

    // ------------------------------------------------------------------ 指令

    /// <summary>tugControl {id, mode, direction_deg, power_pct}。</summary>
    public ExternalCommandOutcome TugControl(string? id, string? mode, double? directionDeg, double? powerPct)
    {
        if (!Supported) return ExternalCommandOutcome.Rejected("externalForcesUnsupported");
        var tug = Tug(id);
        if (tug is null) return ExternalCommandOutcome.Rejected("unknownTug");
        // 已離開的拖船:任何指令皆拒絕(先於模式檢查,教官站可直接顯示原因)
        if (tug.Status == TugStatus.Unavailable) return ExternalCommandOutcome.Rejected(Engine.CommandRejectReasons.TugUnavailable);
        var m = mode is null ? tug.Mode : mode.ToLowerInvariant() switch
        {
            "push" => TugMode.Push, "pull" => TugMode.Pull, "standby" => TugMode.Standby, "free" => TugMode.Free, "unavailable" => TugMode.Unavailable, _ => (TugMode)(-1),
        };
        if ((int)m < 0) return ExternalCommandOutcome.Rejected("unknownTugMode");
        if (powerPct is < 0 or > 100) return ExternalCommandOutcome.Rejected("invalidPower");
        var from = tug.Mode;
        var fromStatus = tug.Status;
        var forceBefore = tug.ForceN;
        var reject = tug.Command(m, directionDeg, powerPct);
        if (reject is not null) return ExternalCommandOutcome.Rejected(reject);
        // 拖船離開(拖船縮減):發 tugUnavailable 而非 tugCommand(不是駕駛台的拖船令,不觸發「拖船令」類條件與必要事件)
        if (m == TugMode.Unavailable)
            return new ExternalCommandOutcome(null, Engine.SimEventKinds.TugUnavailable, tug.Id, $"拖船 {tug.Id} 離開(不再可用)",
                new { id = tug.Id, mode = TugUnit.ModeName(m), from = TugUnit.ModeName(from), fromStatus = TugUnit.StatusName(fromStatus), status = TugUnit.StatusName(tug.Status), force_t = forceBefore / TugUnit.TonneForce });
        return new ExternalCommandOutcome(null, "tugCommand", tug.Id, $"{tug.Id} {TugUnit.ModeName(m)} {Units.NormalizeHeadingDeg(Units.RadToDeg(tug.DirectionOrderRad)):F0}° {tug.PowerOrder * 100:F0}%",
            new
            {
                id = tug.Id, mode = TugUnit.ModeName(m), from = TugUnit.ModeName(from), status = TugUnit.StatusName(tug.Status),
                direction_deg = Units.NormalizeHeadingDeg(Units.RadToDeg(tug.DirectionOrderRad)), power_pct = tug.PowerOrder * 100.0,
            });
    }

    /// <summary>tugLineBreak 故障:斷 <paramref name="id"/>(省略 = 第一艘拉中/待命中的拖船);回傳 tugLineParted 事件(無可斷者 null)。</summary>
    public ExternalEvent? PartTugLine(string? id)
    {
        if (!Supported) return null;
        var tug = id is not null ? Tug(id)
            : _tugs.FirstOrDefault(t => !t.LineParted && t.Mode == TugMode.Pull && t.Status != TugStatus.Free)
              ?? _tugs.FirstOrDefault(t => !t.LineParted && t.Status is not (TugStatus.Free or TugStatus.Parted or TugStatus.Unavailable));
        if (tug is null || tug.LineParted || tug.Status == TugStatus.Unavailable) return null;
        var mode = tug.Mode;
        var before = tug.PartLine();
        return new ExternalEvent("tugLineParted", tug.Id, $"拖船 {tug.Id} 拖纜斷裂",
            new { id = tug.Id, force_t = before / TugUnit.TonneForce, mode = TugUnit.ModeName(mode), reason = "fault", stillPushing = tug.Mode == TugMode.Push });
    }

    /// <summary>tugLineBreak 清除:斷纜的拖船可重新帶纜。</summary>
    public void RestoreTugLines()
    {
        foreach (var t in _tugs) t.LineRestored();
    }

    /// <summary>anchor {side, action, targetLength_shackles | length_m}。</summary>
    public ExternalCommandOutcome AnchorCommand(string? side, string? action, double? targetShackles, double? lengthM, in StateVector s, double waterDepth)
    {
        if (!Supported) return ExternalCommandOutcome.Rejected("externalForcesUnsupported");
        if (AnchorSpec is null) return ExternalCommandOutcome.Rejected("noAnchorData");
        var a = Anchor(side);
        if (a is null) return ExternalCommandOutcome.Rejected("unknownAnchor");
        if (action is null) return ExternalCommandOutcome.Rejected("unknownAction");
        var target = lengthM ?? (targetShackles is { } sh ? sh * AnchorSpec.ShackleLengthM : null);
        if (target is < 0) return ExternalCommandOutcome.Rejected("invalidTarget");
        var reject = a.Command(action, target, s, waterDepth);
        if (reject is not null) return ExternalCommandOutcome.Rejected(reject);
        AnchorsUsed = true;
        var data = new
        {
            side = a.Side, action, targetLength_m = a.TargetLength, targetLength_shackles = a.TargetLength / AnchorSpec.ShackleLengthM,
            length_m = a.Length, status = a.Status,
        };
        return action == "letGo"
            ? new ExternalCommandOutcome(null, "anchorLetGo", a.Side, $"{a.Side} 拋錨", data)
            : new ExternalCommandOutcome(null, "anchorCommand", a.Side, $"{a.Side} {action}", data);
    }

    /// <summary>mooring {lineId, action, bollard?, tension_t?, length_m?}。</summary>
    public ExternalCommandOutcome MooringCommand(string? lineId, string? action, string? bollardId, double? tensionT, double? lengthM, in StateVector s, in EnvironmentSample env)
    {
        if (!Supported) return ExternalCommandOutcome.Rejected("externalForcesUnsupported");
        if (Berth is null) return ExternalCommandOutcome.Rejected("noBerth");
        var line = Berth.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return ExternalCommandOutcome.Rejected("unknownMooringLine");
        if (action is null) return ExternalCommandOutcome.Rejected("unknownAction");
        var reject = Berth.Command(line, action, bollardId, tensionT, lengthM, s, env);
        if (reject is not null) return ExternalCommandOutcome.Rejected(reject);
        return new ExternalCommandOutcome(null, "mooringCommand", line.Id, $"{line.Id} {action}",
            new
            {
                lineId = line.Id, action, status = ContractJson.EnumName(line.Status), bollardId = line.BollardIndex >= 0 ? Berth.Bollards[line.BollardIndex].Id : null,
                tension_t = tensionT, length_m = lengthM, restLength_m = line.RestLength,
            });
    }

    // ------------------------------------------------------------------ 雜湊

    public int HashLongs => 2 + _tugs.Count * 9 + _anchors.Count * 8
                            + (Berth is null ? 0 : 2 + Berth.Fenders.Count * 3 + Berth.Lines.Count * 5);

    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        void L(Span<byte> b, long v) { BinaryPrimitives.WriteInt64LittleEndian(b[o..], v); o += 8; }
        void D(Span<byte> b, double v) => L(b, BitConverter.DoubleToInt64Bits(v));
        L(buf, HashMarker);
        L(buf, AnchorsUsed ? 1 : 0);
        foreach (var t in _tugs)
        {
            L(buf, (long)t.Mode); L(buf, (long)t.Status); D(buf, t.ForceN); D(buf, t.DirectionRad); D(buf, t.DirectionOrderRad); D(buf, t.PowerOrder);
            L(buf, t.TimerTicks); L(buf, t.LineParted ? 1 : 0); L(buf, (long)t.LastWorkingMode);
        }
        foreach (var a in _anchors)
        {
            L(buf, (long)a.Mode); D(buf, a.Length); D(buf, a.TargetLength ?? -1.0);
            L(buf, (a.OnBottom ? 1 : 0) | (a.Dragging ? 2 : 0) | (a.BroughtUpPending ? 4 : 0) | (a.FullyOut ? 8 : 0) | (a.Slipping ? 16 : 0) | (a.Stalled ? 32 : 0));
            D(buf, a.AnchorX); D(buf, a.AnchorY);
            // 停轉/滑出遲滯計時只在期間內追加(未曾超載的錨雜湊與舊版相同)
            if (a.Stalled || a.Slipping) { D(buf, a.StallQuietS); D(buf, a.SlipQuietS); }
        }
        if (Berth is { } b)
        {
            L(buf, b.QuayInContact ? 1 : 0); D(buf, b.QuayMaxPenetration);
            foreach (var f in b.Fenders) { L(buf, (f.InContact ? 1 : 0) | (f.PeakReported ? 2 : 0)); D(buf, f.MaxDeflection); D(buf, f.ApproachSpeed); }
            foreach (var l in b.Lines) { L(buf, (long)l.Status); L(buf, l.BollardIndex); D(buf, l.RestLength); D(buf, l.TargetTensionN); D(buf, l.SlackRemaining); }
        }
        return o;
    }

    // ------------------------------------------------------------------ 快照

    public ExternalForceSnapshot CreateSnapshot() => new(
        AnchorsUsed,
        _tugs.Select(t => new TugSnapshot(t.Id, t.Mode, t.Status, t.ForceN, t.DirectionRad, t.DirectionOrderRad, t.PowerOrder, t.TimerTicks, t.LineParted, t.LastWorkingMode)).ToList(),
        _anchors.Select(a => new AnchorSnapshot(a.Side, a.Mode, a.Length, a.TargetLength, a.OnBottom, a.AnchorX, a.AnchorY, a.Dragging, a.BroughtUpPending, a.FullyOut, a.Slipping, a.Stalled, a.StallQuietS, a.SlipQuietS)).ToList(),
        Berth?.Fenders.Select(f => new FenderSnapshot(f.Id, f.InContact, f.PeakReported, f.MaxDeflection, f.ApproachSpeed)).ToList(),
        Berth is { } b ? new QuaySnapshot(b.QuayInContact, b.QuayMaxPenetration) : null,
        Berth?.Lines.Select(l => new MooringLineSnapshot(l.Id, l.Status, l.BollardIndex, l.RestLength, l.TargetTensionN, l.SlackRemaining)).ToList());

    public void Restore(ExternalForceSnapshot s)
    {
        AnchorsUsed = s.AnchorsUsed;
        foreach (var ts in s.Tugs ?? Array.Empty<TugSnapshot>())
            if (Tug(ts.Id) is { } t)
            {
                t.Mode = ts.Mode; t.Status = ts.Status; t.ForceN = ts.ForceN; t.DirectionRad = ts.DirectionRad; t.DirectionOrderRad = ts.DirectionOrderRad;
                t.PowerOrder = ts.PowerOrder; t.TimerTicks = ts.TimerTicks; t.LineParted = ts.LineParted; t.LastWorkingMode = ts.LastWorkingMode;
            }
        foreach (var asn in s.Anchors ?? Array.Empty<AnchorSnapshot>())
            if (Anchor(asn.Side) is { } a)
            {
                a.Mode = asn.Mode; a.Length = asn.Length; a.TargetLength = asn.TargetLength; a.OnBottom = asn.OnBottom; a.AnchorX = asn.AnchorX; a.AnchorY = asn.AnchorY;
                a.Dragging = asn.Dragging; a.BroughtUpPending = asn.BroughtUpPending; a.FullyOut = asn.FullyOut; a.Slipping = asn.Slipping; a.Stalled = asn.Stalled;
                a.StallQuietS = asn.StallQuietS; a.SlipQuietS = asn.SlipQuietS;
            }
        if (Berth is { } b)
        {
            if (s.Quay is { } q) { b.QuayInContact = q.InContact; b.QuayMaxPenetration = q.MaxPenetration; }
            foreach (var fs in s.Fenders ?? Array.Empty<FenderSnapshot>())
                if (b.Fenders.FirstOrDefault(f => f.Id == fs.Id) is { } f)
                {
                    f.InContact = fs.InContact; f.PeakReported = fs.PeakReported; f.MaxDeflection = fs.MaxDeflection; f.ApproachSpeed = fs.ApproachSpeed;
                }
            foreach (var ls in s.Lines ?? Array.Empty<MooringLineSnapshot>())
                if (b.Lines.FirstOrDefault(l => l.Id == ls.Id) is { } l)
                {
                    l.Status = ls.Status; l.BollardIndex = ls.BollardIndex; l.RestLength = ls.RestLength; l.TargetTensionN = ls.TargetTensionN; l.SlackRemaining = ls.SlackRemaining;
                }
        }
    }

    // ------------------------------------------------------------------ 狀態廣播

    public IReadOnlyList<TugStatusState>? TugStates(in StateVector s, LocalTangentPlane proj)
    {
        if (_tugs.Count == 0) return null;
        var list = new List<TugStatusState>(_tugs.Count);
        foreach (var t in _tugs)
        {
            var (ae, an) = BodyFrame.ToEnu(s, t.AttachX, t.AttachY);
            var dirTrue = s.Psi + t.DirectionRad;
            // 顯示位置:拉 = 連接點沿力方向一個纜長;推 = 連接點反方向(半船寬 + 15 m);其餘在連接點外側 30 m
            var off = t.Mode == TugMode.Pull ? t.LineLengthM : t.Mode == TugMode.Push ? -(0.5 * _ship.Hull.Breadth_m + 15.0) : 30.0;
            var (pe, pn) = (ae + off * Math.Sin(dirTrue), an + off * Math.Cos(dirTrue));
            var (lat, lon) = proj.ToGeodetic(pe, pn);
            list.Add(new TugStatusState
            {
                Id = t.Id, Name = t.Name, Mode = TugUnit.ModeName(t.Mode), Status = TugUnit.StatusName(t.Status),
                ForceT = t.ForceN / TugUnit.TonneForce, DirectionDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(t.DirectionRad)),
                DirectionOrderDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(t.DirectionOrderRad)), PowerPct = t.PowerOrder * 100.0,
                Efficiency = t.Efficiency, BollardPullT = t.BollardPullN / TugUnit.TonneForce, LineParted = t.LineParted,
                Pos = new GeoPosition { Lat = lat, Lon = lon, X = pe, Y = pn },
            });
        }
        return list;
    }

    public IReadOnlyList<AnchorStatusState>? AnchorStates(LocalTangentPlane proj)
    {
        if (_anchors.Count == 0 || AnchorSpec is not { } spec) return null;
        var list = new List<AnchorStatusState>(_anchors.Count);
        foreach (var a in _anchors)
        {
            GeoPosition? pos = null;
            if (a.Mode != AnchorMode.Stowed)
            {
                var (lat, lon) = proj.ToGeodetic(a.AnchorX, a.AnchorY);
                pos = new GeoPosition { Lat = lat, Lon = lon, X = a.AnchorX, Y = a.AnchorY };
            }
            list.Add(new AnchorStatusState
            {
                Side = a.Side, Status = a.Status, LengthM = a.Length, LengthShackles = a.Length / spec.ShackleLengthM,
                MaxLengthM = a.MaxLength, TensionT = a.Mode == AnchorMode.Stowed ? 0.0 : AnchorUnit.ToT(a.Last.TopTension),
                HorizontalTensionT = AnchorUnit.ToT(a.Last.H), OnBottomM = a.Last.OnBottom, HoldingT = AnchorUnit.ToT(a.LastHolding),
                Brake = a.Mode is AnchorMode.Braked or AnchorMode.Stowed, FullyOut = a.FullyOut, Position = pos,
            });
        }
        return list;
    }

    public BerthStatusState? BerthState()
    {
        if (Berth is not { } b) return null;
        return new BerthStatusState
        {
            Id = b.Id, Name = b.Name, QuayContact = b.QuayInContact,
            Contacts = b.Fenders.Where(f => f.LastDeflection > 0.0).Select(f => new FenderContactState
            {
                FenderId = f.Id, DeflectionM = f.LastDeflection, DeflectionPct = 100.0 * f.LastDeflection / f.Type.HeightM, ReactionKN = f.LastReactionN / 1000.0,
                Overload = f.LastDeflection > f.Type.RatedDeflectionM,
            }).ToList(),
            Lines = b.Lines.Select(l => new MooringLineState
            {
                Id = l.Id, Status = ContractJson.EnumName(l.Status), TensionT = l.LastTensionN / MooringLine.TonneForce, MblT = l.MblN / MooringLine.TonneForce,
                BollardId = l.BollardIndex >= 0 ? b.Bollards[l.BollardIndex].Id : null, LengthM = l.Loaded ? l.RestLength : null,
            }).ToList(),
        };
    }
}

public sealed record ExternalForceSnapshot(
    bool AnchorsUsed,
    IReadOnlyList<TugSnapshot>? Tugs,
    IReadOnlyList<AnchorSnapshot>? Anchors,
    IReadOnlyList<FenderSnapshot>? Fenders,
    QuaySnapshot? Quay,
    IReadOnlyList<MooringLineSnapshot>? Lines);

/// <summary>拖船快照;<paramref name="LastWorkingMode"/> 為就位工作側(舊快照缺此欄時視為 standby = 尚未就位)。</summary>
public sealed record TugSnapshot(string Id, TugMode Mode, TugStatus Status, double ForceN, double DirectionRad, double DirectionOrderRad, double PowerOrder, long TimerTicks, bool LineParted,
    TugMode LastWorkingMode = TugMode.Standby);

public sealed record AnchorSnapshot(string Side, AnchorMode Mode, double Length, double? TargetLength, bool OnBottom, double AnchorX, double AnchorY,
    bool Dragging, bool BroughtUpPending, bool FullyOut, bool Slipping, bool Stalled, double StallQuietS = 0.0, double SlipQuietS = 0.0);

public sealed record FenderSnapshot(string Id, bool InContact, bool PeakReported, double MaxDeflection, double ApproachSpeed);

public sealed record QuaySnapshot(bool InContact, double MaxPenetration);

public sealed record MooringLineSnapshot(string Id, MooringStatus Status, int BollardIndex, double RestLength, double TargetTensionN, double SlackRemaining);
