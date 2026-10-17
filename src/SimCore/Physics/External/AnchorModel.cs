using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>海床底質(情境 environment.seabed;預設 sand)。</summary>
public enum Seabed
{
    Mud,
    Sand,
    Clay,
    Rock,
}

/// <summary>
/// 錨與錨鏈資料(particulars.json <c>anchors</c>;規劃書第 3 章:FSB1 AC-14 2,140 kg × 2、Φ42 mm、各 9 節;FSB2 Spek、Φ58 mm、各 10 節;一節 27.5 m)。
/// particulars 缺的欄位以估計式補齊(<see cref="EstimatedFields"/> 列出),估計來源見設計文件第 3.1 節。
/// </summary>
public sealed class AnchorParticulars
{
    public const double Gravity = 9.80665;
    /// <summary>海水密度(kg/m³)與鋼(kg/m³):鏈水中重 = 空氣中重 × (1 − ρ_w/ρ_s)</summary>
    public const double RhoWater = 1025.0, RhoSteel = 7850.0;
    public const double DefaultShackleLengthM = 27.5;

    public string Type { get; init; } = "";
    /// <summary>錨重(kg)</summary>
    public double AnchorMassKg { get; init; }
    public double ChainDiameterMm { get; init; }
    public double ShackleLengthM { get; init; } = DefaultShackleLengthM;
    public double PortLengthM { get; init; }
    public double StarboardLengthM { get; init; }
    /// <summary>鏈單位長度質量(kg/m;有檔錨鏈估計 0.0219·d²)</summary>
    public double ChainMassPerM { get; init; }
    /// <summary>鏈斷裂負荷(N;U2 估計 0.0196·d²·(44 − 0.08d) kN)</summary>
    public double ChainBreakingLoadN { get; init; }
    /// <summary>起錨速率(m/s;FSB1 海報 21.7 m/min)</summary>
    public double HeavingRateMps { get; init; }
    /// <summary>絞盤額定拉力(N;IACS UR A3 U2:42.5·d² N)與過載上限(× 1.5)</summary>
    public double WindlassPullN { get; init; }
    public double WindlassOverloadPullN => 1.5 * WindlassPullN;
    /// <summary>剎車握持力(N;UR A3:鏈斷裂負荷的 45 %)</summary>
    public double BrakeHoldingN { get; init; }
    /// <summary>錨鏈孔(船體座標:x 向艏、|y|、龍骨上高度)</summary>
    public double HawseX { get; init; }
    public double HawseHalfBreadth { get; init; }
    public double HawseHeightAboveKeel { get; init; }
    /// <summary>錨型抓著類別:hhp(高抓力,如 AC-14)或 stockless(無桿錨,如 Spek/Hall)</summary>
    public string HoldingClass { get; init; } = "stockless";
    /// <summary>以估計值補齊的欄位名稱(state/文件標示「估計」用)</summary>
    public IReadOnlyList<string> EstimatedFields { get; init; } = Array.Empty<string>();

    /// <summary>鏈水中單位重(N/m)</summary>
    public double ChainSubmergedWeightPerM => ChainMassPerM * Gravity * (1.0 - RhoWater / RhoSteel);

    /// <summary>錨重 × 抓著係數(依底質與錨型;設計文件第 3.3 節表)。</summary>
    public double AnchorHoldingCoefficient(Seabed bed) => (HoldingClass == "hhp", bed) switch
    {
        (true, Seabed.Mud) => 5.0,
        (true, Seabed.Sand) => 7.0,
        (true, Seabed.Clay) => 8.0,
        (true, Seabed.Rock) => 1.5,
        (false, Seabed.Mud) => 2.5,
        (false, Seabed.Sand) => 3.5,
        (false, Seabed.Clay) => 4.0,
        _ => 1.0,
    };

    /// <summary>躺底鏈摩擦係數(依底質)。</summary>
    public static double ChainFriction(Seabed bed) => bed switch
    {
        Seabed.Mud => 0.6,
        Seabed.Sand => 0.75,
        Seabed.Clay => 0.8,
        _ => 0.5,
    };

    /// <summary>由 particulars 的 anchors 區段建立(無此區段時 null)。</summary>
    public static AnchorParticulars? FromShip(ShipParticulars ship)
    {
        if (ship.Extra is null || !ship.Extra.TryGetValue("anchors", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var est = new List<string>();
        var d = Num(a, "chainDiameter_mm") ?? 0.0;
        if (d <= 0) return null;
        var shackle = Num(a, "shackleLength_m") ?? DefaultShackleLengthM;
        var mass = Num(a, "mass_kg");
        if (mass is null)
        {
            mass = Num(a, "massEstimate_kg");
            est.Add("mass_kg");
        }
        // 錨重缺資料:IACS UR A1 舾裝數表中與鏈徑對應的錨重(約 1.25·d² kg)
        if (mass is null) mass = 1.25 * d * d;
        var chain = Obj(a, "chain");
        var massPerM = chain is { } c1 ? Num(c1, "mass_kgPerM") : null;
        if (massPerM is null) { massPerM = 0.0219 * d * d; est.Add("chain.mass_kgPerM"); }
        var breaking = chain is { } c2 ? Num(c2, "breakingLoad_kN") : null;
        if (breaking is null) { breaking = 0.0196 * d * d * (44.0 - 0.08 * d); est.Add("chain.breakingLoad_kN"); }
        var heave = Num(a, "heavingRate_mPerMin");
        if (heave is null) { heave = Num(a, "heavingRateEstimate_mPerMin") ?? 9.0; est.Add("heavingRate_mPerMin"); }
        var windlass = Obj(a, "windlass");
        var pull = windlass is { } w1 ? Num(w1, "pull_kN") : null;
        if (pull is null) { pull = 42.5 * d * d / 1000.0; est.Add("windlass.pull_kN"); }
        var brake = windlass is { } w2 ? Num(w2, "brakeHolding_kN") : null;
        if (brake is null) { brake = 0.45 * breaking.Value; est.Add("windlass.brakeHolding_kN"); }
        var hawse = Obj(a, "hawsePipe");
        var lpp = ship.Hull.LengthBetweenPerpendiculars_m;
        var hx = hawse is { } h1 ? Num(h1, "x_m") : null;
        var hy = hawse is { } h2 ? Num(h2, "y_m") : null;
        var hz = hawse is { } h3 ? Num(h3, "heightAboveKeel_m") : null;
        if (hx is null || hy is null || hz is null) est.Add("hawsePipe");
        var type = a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        var cls = a.TryGetProperty("holdingClass", out var hc) && hc.ValueKind == JsonValueKind.String ? hc.GetString()!.ToLowerInvariant()
            : type.StartsWith("AC", StringComparison.OrdinalIgnoreCase) ? "hhp" : "stockless";
        return new AnchorParticulars
        {
            Type = type,
            AnchorMassKg = mass.Value,
            ChainDiameterMm = d,
            ShackleLengthM = shackle,
            PortLengthM = (Num(a, "shacklesPort") ?? 9.0) * shackle,
            StarboardLengthM = (Num(a, "shacklesStbd") ?? 9.0) * shackle,
            ChainMassPerM = massPerM.Value,
            ChainBreakingLoadN = breaking.Value * 1000.0,
            HeavingRateMps = heave.Value / 60.0,
            WindlassPullN = pull.Value * 1000.0,
            BrakeHoldingN = brake.Value * 1000.0,
            // 錨鏈孔估計:艏垂標後約 0.03 L、半寬約 0.15 B、龍骨上高度 = 型深 − 0.5 m(船殼側鏈孔開口)
            HawseX = hx ?? 0.5 * lpp - 0.03 * lpp,
            HawseHalfBreadth = Math.Abs(hy ?? 0.15 * ship.Hull.Breadth_m),
            HawseHeightAboveKeel = hz ?? ship.Hull.Depth_m - 0.5,
            HoldingClass = cls,
            EstimatedFields = est,
        };
    }

    private static double? Num(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private static JsonElement? Obj(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;
}

/// <summary>錨的內部模式(對外 status 由 <see cref="AnchorUnit.Status"/> 推得)。</summary>
public enum AnchorMode
{
    /// <summary>收存於錨鏈孔</summary>
    Stowed,
    /// <summary>剎車放開、鏈自由送出(letGo / release)</summary>
    Running,
    /// <summary>以控制速率鬆鏈(veer)</summary>
    Veering,
    /// <summary>絞盤起錨(heave)</summary>
    Heaving,
    /// <summary>剎車握持</summary>
    Braked,
}

/// <summary>錨物理事件(由 <see cref="AnchorUnit.PostStep"/> 回報,引擎轉成 SimEvent)。</summary>
public readonly record struct ExternalEvent(string Kind, string ItemId, string Message, object Data);

/// <summary>
/// 單側錨與錨鏈(設計文件 physics-tugs-anchors-mooring.md 第 3 節):鏈長狀態機(letGo/veer/heave/brake/release)、
/// 準靜態懸鏈線水平張力、抓地力(錨重 × 抓著係數 × 上拔折減 + 躺底鏈摩擦)、走錨(有限速率拖動)、剎車握持上限(超過時鏈被拉出)、
/// 絞盤速率與負荷上限、鏈全部送出的終端事件。所有跨步狀態在本類別欄位(快照、雜湊)。
/// </summary>
public sealed class AnchorUnit
{
    /// <summary>自由落下/送出速率(m/s;letGo、release)、鬆鏈速率(veer)、剎車滑出速率上限、上拔角折減的零點</summary>
    public const double RunRateMps = 2.0, VeerRateMps = 0.5, SlipRateMps = 1.0, UpliftZeroRad = 15.0 * Math.PI / 180.0;
    /// <summary>止住走錨的遲滯(水平張力 ≤ 0.95 × 抓地力)與帶上(brought up)的對地速度門檻</summary>
    public const double DragHysteresis = 0.95, BroughtUpSogMps = 0.15;

    public string Side { get; }
    public AnchorParticulars Spec { get; }
    public double MaxLength { get; }
    /// <summary>船體座標錨鏈孔(port y 負、starboard y 正)</summary>
    public double HawseXb { get; }
    public double HawseYb { get; }
    /// <summary>錨鏈孔在水線上的高度(m)= 龍骨上高度 − 艏吃水</summary>
    public double HawseFreeboard { get; }

    // ---- 跨步狀態 ----
    public AnchorMode Mode { get; set; } = AnchorMode.Stowed;
    public double Length { get; set; }
    public double? TargetLength { get; set; }
    public bool OnBottom { get; set; }
    public double AnchorX { get; set; }
    public double AnchorY { get; set; }
    public bool Dragging { get; set; }
    /// <summary>letGo 後尚未發 anchorBroughtUp</summary>
    public bool BroughtUpPending { get; set; }
    /// <summary>已到鏈端(bitter end):剎車不再滑出</summary>
    public bool FullyOut { get; set; }
    public bool Slipping { get; set; }
    /// <summary>絞盤超載停轉中</summary>
    public bool Stalled { get; set; }

    // ---- 最近一次步末的量(狀態廣播;不進雜湊,由狀態重算) ----
    public CatenaryResult Last { get; private set; }
    public double LastHolding { get; private set; }

    public AnchorUnit(string side, AnchorParticulars spec, double draftFore)
    {
        Side = side;
        Spec = spec;
        MaxLength = side == "port" ? spec.PortLengthM : spec.StarboardLengthM;
        HawseXb = spec.HawseX;
        HawseYb = side == "port" ? -spec.HawseHalfBreadth : spec.HawseHalfBreadth;
        HawseFreeboard = Math.Max(0.5, spec.HawseHeightAboveKeel - draftFore);
    }

    /// <summary>錨鏈孔至海床的垂直距離(m)。</summary>
    public double VerticalSpan(double waterDepth) => Math.Max(0.5, waterDepth) + HawseFreeboard;

    /// <summary>某狀態下的懸鏈線解(錨未著底時為 Hanging)。</summary>
    public CatenaryResult Solve(in StateVector s, double waterDepth, out double dirE, out double dirN)
    {
        dirE = dirN = 0.0;
        if (!OnBottom || Mode == AnchorMode.Stowed) return new CatenaryResult(0, Spec.ChainSubmergedWeightPerM * Length, 0, Length, 0, CatenaryRegime.Hanging);
        var (he, hn) = BodyFrame.ToEnu(s, HawseXb, HawseYb);
        var de = AnchorX - he;
        var dn = AnchorY - hn;
        var x = Math.Sqrt(de * de + dn * dn);
        if (x > 1e-9) { dirE = de / x; dirN = dn / x; }
        return Catenary.Solve(x, Length, VerticalSpan(waterDepth), Spec.ChainSubmergedWeightPerM, Spec.ChainBreakingLoadN);
    }

    /// <summary>作用於船的水平力(錨鏈孔處,指向錨;垂直分量不計,3 自由度)。</summary>
    public BodyForce Force(in StateVector s, double waterDepth)
    {
        var r = Solve(s, waterDepth, out var de, out var dn);
        if (r.H <= 0.0) return BodyForce.Zero;
        return BodyForce.FromEnu(r.H * de, r.H * dn, HawseXb, HawseYb, s.Psi);
    }

    /// <summary>抓地力(N):錨重 × g × 抓著係數 × 上拔折減 × 倍率 + 躺底鏈摩擦。</summary>
    public double HoldingPower(in CatenaryResult r, Seabed bed, double factor)
    {
        var uplift = Math.Max(0.0, 1.0 - r.UpliftRad / UpliftZeroRad);
        var anchor = Spec.AnchorMassKg * AnchorParticulars.Gravity * Spec.AnchorHoldingCoefficient(bed) * uplift;
        var chain = AnchorParticulars.ChainFriction(bed) * Spec.ChainSubmergedWeightPerM * r.OnBottom;
        return factor * (anchor + chain);
    }

    /// <summary>
    /// 指令(設計文件第 3.4 節);回傳 null = 接受,否則為拒絕原因。<paramref name="target"/> 為鏈長目標(m,已由節換算)。
    /// </summary>
    public string? Command(string action, double? target, in StateVector s, double waterDepth)
    {
        switch (action)
        {
            case "letGo":
                // 已著底,或仍在送出/鬆鏈中:不能再拋(避免重發 anchorLetGo、以新目標把鏈長瞬間縮短)
                if (Mode != AnchorMode.Stowed && (OnBottom || Mode is AnchorMode.Running or AnchorMode.Veering)) return "anchorAlreadyOut";
                if (FullyOut) return "chainFullyOut";
                // 懸吊中(起錨到離底後、或剎車停在水中)再拋:只能送出,目標不可短於目前鏈長
                if (target is { } tg && tg < Length - 1e-9) return "invalidTarget";
                Mode = AnchorMode.Running;
                TargetLength = target is { } t0 ? Math.Min(Math.Max(t0, Length), MaxLength) : null;
                BroughtUpPending = true;
                Stalled = false;
                return null;
            case "veer":
                if (Mode == AnchorMode.Stowed) return "anchorStowed";
                if (FullyOut || Length >= MaxLength - 1e-9) return "chainFullyOut";
                Mode = AnchorMode.Veering;
                TargetLength = target is { } t1 ? Math.Min(Math.Max(t1, Length), MaxLength) : null;
                Stalled = false;
                return null;
            case "heave":
                if (Mode == AnchorMode.Stowed) return "anchorStowed";
                if (target is { } t2 && t2 >= Length) return "invalidTarget";
                Mode = AnchorMode.Heaving;
                TargetLength = target is { } t3 ? Math.Max(0.0, t3) : null;
                FullyOut = false;
                Stalled = false;
                return null;
            case "brake":
                if (Mode == AnchorMode.Stowed) return "anchorStowed";
                Mode = AnchorMode.Braked;
                TargetLength = null;
                Stalled = false;
                return null;
            case "release":
                if (Mode == AnchorMode.Stowed) BroughtUpPending = true;
                if (FullyOut) return "chainFullyOut";
                Mode = AnchorMode.Running;
                TargetLength = null;
                Stalled = false;
                return null;
            default:
                return "unknownAction";
        }
    }

    /// <summary>
    /// 步末更新(引擎在 RK4 之後呼叫):鏈長(送出/起錨/剎車滑出)、著底/離底、走錨、事件。<paramref name="sogMps"/> 為對地速度(帶上判定)。
    /// </summary>
    public void PostStep(in StateVector s, double waterDepth, double dt, Seabed bed, double holdingFactor, double dragSpeedMps, double sogMps, List<ExternalEvent> events)
    {
        if (Mode == AnchorMode.Stowed) { Last = default; LastHolding = 0; return; }
        var h = VerticalSpan(waterDepth);
        var r = Solve(s, waterDepth, out _, out _);

        // ---- 鏈長 ----
        switch (Mode)
        {
            case AnchorMode.Running:
            case AnchorMode.Veering:
            {
                var rate = Mode == AnchorMode.Veering ? VeerRateMps : RunRateMps;
                // 送出/鬆鏈只會加長:目標短於目前鏈長時(例如舊快照)就地剎車,不靠絞盤縮短
                var limit = Math.Max(Math.Min(TargetLength ?? MaxLength, MaxLength), Length);
                Length = Math.Min(limit, Length + rate * dt);
                if (Length >= limit - 1e-9)
                {
                    Length = limit;
                    if (Length >= MaxLength - 1e-9) ReachBitterEnd(events);
                    Mode = AnchorMode.Braked;
                    TargetLength = null;
                }
                break;
            }
            case AnchorMode.Heaving:
            {
                var stalled = r.TopTension > Spec.WindlassOverloadPullN;
                if (stalled != Stalled)
                {
                    Stalled = stalled;
                    if (stalled) events.Add(new ExternalEvent("anchorWindlassStalled", Side, $"{Side} 絞盤超載停轉",
                        new { side = Side, tension_t = r.TopTension / (AnchorParticulars.Gravity * 1000.0), windlassLimit_t = Spec.WindlassOverloadPullN / (AnchorParticulars.Gravity * 1000.0) }));
                }
                if (!stalled)
                {
                    Length = Math.Max(TargetLength ?? 0.0, Length - Spec.HeavingRateMps * dt);
                    if (TargetLength is { } tl && Length <= tl + 1e-9) { Mode = AnchorMode.Braked; TargetLength = null; }
                    Slipping = false;
                }
                // 停轉中的絞盤(馬達剎車 + 鏈輪)握持上限與剎車相同:超過剎車握持力時鏈同樣被拉出(不比剎車強)
                else SlipIfOverBrake(s, waterDepth, dt, r, events);
                break;
            }
            case AnchorMode.Braked:
                SlipIfOverBrake(s, waterDepth, dt, r, events);
                break;
        }
        if (Mode is not (AnchorMode.Braked or AnchorMode.Heaving)) Slipping = false;

        // ---- 著底 / 離底 ----
        if (!OnBottom && Length > h)
        {
            OnBottom = true;
            var (he, hn) = BodyFrame.ToEnu(s, HawseXb, HawseYb);
            AnchorX = he;
            AnchorY = hn;
        }
        else if (OnBottom && Length <= h)
        {
            OnBottom = false;
            Dragging = false;
            events.Add(new ExternalEvent("anchorAweigh", Side, $"{Side} 錨離底(anchor aweigh)", new { side = Side, length_m = Length }));
        }
        if (!OnBottom)
        {
            // 懸吊中的錨隨錨鏈孔移動
            (AnchorX, AnchorY) = BodyFrame.ToEnu(s, HawseXb, HawseYb);
            if (Mode == AnchorMode.Heaving && Length <= 1e-9)
            {
                Length = 0.0;
                Mode = AnchorMode.Stowed;
                TargetLength = null;
                Stalled = false;
                events.Add(new ExternalEvent("anchorHoused", Side, $"{Side} 錨收妥", new { side = Side }));
            }
            Last = Solve(s, waterDepth, out _, out _);
            LastHolding = 0.0;
            return;
        }

        // ---- 走錨 ----
        r = Solve(s, waterDepth, out var dirE, out var dirN);
        var holding = HoldingPower(r, bed, holdingFactor);
        var excess = r.H - holding;
        if (!Dragging && excess > 0.0)
        {
            Dragging = true;
            events.Add(new ExternalEvent("anchorDragging", Side, $"{Side} 走錨", new { side = Side, tension_t = ToT(r.H), holding_t = ToT(holding), length_m = Length }));
        }
        else if (Dragging && r.H <= DragHysteresis * holding)
        {
            Dragging = false;
            events.Add(new ExternalEvent("anchorHolding", Side, $"{Side} 錨抓住", new { side = Side, tension_t = ToT(r.H), holding_t = ToT(holding), length_m = Length }));
        }
        if (Dragging && excess > 0.0)
        {
            // 錨朝錨鏈孔方向以有限速率被拖動:v = v_max · min(1, 超出量 / (0.25 × 抓地力))
            var v = dragSpeedMps * Math.Min(1.0, excess / Math.Max(1.0, 0.25 * holding));
            AnchorX -= dirE * v * dt;
            AnchorY -= dirN * v * dt;
            r = Solve(s, waterDepth, out _, out _);
        }

        // ---- 帶上(brought up):letGo 後剎車握持、錨抓住、船已被鏈止住 ----
        if (BroughtUpPending && Mode == AnchorMode.Braked && !Dragging && r.H > 0.0 && sogMps < BroughtUpSogMps)
        {
            BroughtUpPending = false;
            events.Add(new ExternalEvent("anchorBroughtUp", Side, $"{Side} 錨已帶上(brought up)", new { side = Side, length_m = Length, tension_t = ToT(r.TopTension) }));
        }
        Last = r;
        LastHolding = holding;
    }

    /// <summary>
    /// 剎車(或停轉絞盤)滑出:錨著底、未到鏈端且錨鏈孔張力 &gt; 剎車握持力時,找 ΔL ∈ (0, SlipRate·dt] 使張力回到剎車握持力;
    /// 每次滑出發一次 anchorBrakeSlipping。否則清除滑出旗標。
    /// </summary>
    private void SlipIfOverBrake(in StateVector s, double waterDepth, double dt, in CatenaryResult r, List<ExternalEvent> events)
    {
        if (FullyOut || !OnBottom || !(r.TopTension > Spec.BrakeHoldingN))
        {
            Slipping = false;
            return;
        }
        var l0 = Length;
        var l1 = Math.Min(MaxLength, Length + SlipRateMps * dt);
        for (var i = 0; i < 40; i++)
        {
            Length = 0.5 * (l0 + l1);
            if (Solve(s, waterDepth, out _, out _).TopTension > Spec.BrakeHoldingN) l0 = Length; else l1 = Length;
        }
        Length = l1;
        if (!Slipping)
        {
            Slipping = true;
            events.Add(new ExternalEvent("anchorBrakeSlipping", Side, $"{Side} 錨鏈剎車滑出",
                new { side = Side, tension_t = r.TopTension / (AnchorParticulars.Gravity * 1000.0), brakeHolding_t = Spec.BrakeHoldingN / (AnchorParticulars.Gravity * 1000.0), length_m = Length }));
        }
        if (Length >= MaxLength - 1e-9) { Length = MaxLength; ReachBitterEnd(events); }
    }

    private void ReachBitterEnd(List<ExternalEvent> events)
    {
        if (FullyOut) return;
        FullyOut = true;
        events.Add(new ExternalEvent("anchorChainFullyOut", Side, $"{Side} 錨鏈全部送出(到鏈端)", new { side = Side, length_m = MaxLength, shackles = MaxLength / Spec.ShackleLengthM }));
    }

    /// <summary>初始已錨泊(情境 anchors.port|starboard.length_*):錨在錨鏈孔 <paramref name="bearingRad"/> 方向、水平張力約 0.25 × 錨抓力的距離。</summary>
    public void InitialiseAnchored(in StateVector s, double length, double bearingRad, double waterDepth, Seabed bed, double holdingFactor)
    {
        Length = Math.Min(length, MaxLength);
        var h = VerticalSpan(waterDepth);
        var (he, hn) = BodyFrame.ToEnu(s, HawseXb, HawseYb);
        if (Length <= h)
        {
            Mode = AnchorMode.Braked;
            OnBottom = false;
            (AnchorX, AnchorY) = (he, hn);
            return;
        }
        var w = Spec.ChainSubmergedWeightPerM;
        var h0 = 0.25 * holdingFactor * Spec.AnchorMassKg * AnchorParticulars.Gravity * Spec.AnchorHoldingCoefficient(bed);
        var a = Math.Min(h0 / w, Catenary.MaxTouchdownA(Length, h));
        var x = Catenary.TouchdownDistance(a, Length, h);
        Mode = AnchorMode.Braked;
        OnBottom = true;
        AnchorX = he + x * Math.Sin(bearingRad);
        AnchorY = hn + x * Math.Cos(bearingRad);
        FullyOut = Length >= MaxLength - 1e-9;
    }

    /// <summary>對外狀態(stowed|running|holding|dragging|heaving|aweigh)。</summary>
    public string Status => Mode switch
    {
        AnchorMode.Stowed => "stowed",
        _ when !OnBottom => Mode is AnchorMode.Running or AnchorMode.Veering ? "running" : "aweigh",
        _ when Dragging => "dragging",
        AnchorMode.Heaving => "heaving",
        AnchorMode.Running or AnchorMode.Veering => "running",
        _ => "holding",
    };

    public static double ToT(double newtons) => newtons / (AnchorParticulars.Gravity * 1000.0);
}
