using System.Buffers.Binary;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>
/// 岸壁效應的經驗係數(設計文件 docs/design/physics-bank-effect.md 第 3、4 節)。
/// 方程式結構依 Lataire &amp; Vantorre(Flanders Hydraulics / Ghent University)與 AMC(Ch'ng, Doctors &amp; Renilson)的回歸模型:
/// 力 ∝ ½ρLT·u²(與螺槳載荷項 ∝ (nD)²)× 加權距離參數 × 水深吃水比修正。
/// <b>數值為文獻量級的估計值</b>(兩船無岸壁模型試驗;離線開發未能逐式核對原文),須以實船/引水人經驗或模型試驗校正(情境 banks.scale 可整體調整)。
/// </summary>
public sealed record BankEffectCoefficients
{
    /// <summary>
    /// 前導半船(正俥時船艏半)的橫向力係數 c_lead(Y' 以 ½ρLTu² 無因次化,乘該半的加權距離參數 D_lead):
    /// 負值 = 艏部壓力墊(bow cushion)把船艏推離岸。
    /// </summary>
    public double CYLead { get; init; } = -0.005;
    /// <summary>後隨半船(船艉半)的橫向吸力係數 c_trail(正 = 吸向岸)。平行岸壁時 C_Y = c_lead + c_trail = 0.040。</summary>
    public double CYTrail { get; init; } = 0.045;
    /// <summary>前導半船力的作用點(× L,自船舯向前導端為正)</summary>
    public double LeverLead { get; init; } = 0.30;
    /// <summary>後隨半船力的作用點(× L;負 = 船舯後方)。平行岸壁時 C_N = −(0.30·c_lead − 0.20·c_trail) = 0.0105(艏離岸)</summary>
    public double LeverTrail { get; init; } = -0.20;
    /// <summary>螺槳載荷橫向吸力係數 C_YP(以 ½ρLT(nD)² 無因次化,乘船艉半 D_aft;只計正車)</summary>
    public double CYP { get; init; } = 0.004;
    /// <summary>螺槳載荷吸力作用點(× L;艉部 −0.40 → 艏離岸力矩)</summary>
    public double LeverP { get; init; } = -0.40;
    /// <summary>水深修正 F_Y = 1 + a_Y·T/(h − T)</summary>
    public double AY { get; init; } = 0.40;
    /// <summary>水深修正 F_N = 1 + a_N·T/(h − T)</summary>
    public double AN { get; init; } = 0.30;
    /// <summary>h/T 下限(外推限制;低於此值以此值計)</summary>
    public double MinDepthRatio { get; init; } = 1.05;
    /// <summary>影響寬度 y_inf = k·B·(1 + 16 Fnh⁸)(自船舯;AMC 影響寬度概念,k 為估計)</summary>
    public double InfluenceWidthB { get; init; } = 5.0;
    /// <summary>
    /// 船舯至等效岸壁距離下限 = (0.5 + clearanceMin)·B(外推限制:船側距岸壁 0.1 B 以內以此值計)。
    /// 船寬跨越等效岸壁線(船舯距離 &lt; B/2)時再乘「船寬在水域側的比例」淡出,整個船寬在陸側時為 0(設計文件第 3.3 節)。
    /// </summary>
    public double MinClearanceB { get; init; } = 0.1;
    /// <summary>沿船長的加權窗半寬(× L,窗 [−L, +L]、三角權重 1 − |x|/L)</summary>
    public double WindowL { get; init; } = 1.0;
    /// <summary>海水密度(kg/m³)</summary>
    public double Rho { get; init; } = 1025.0;

    public static BankEffectCoefficients Default { get; } = new();
}

/// <summary>一次岸壁效應計算的結果(船體座標;N、N·m)。</summary>
public readonly record struct BankEffectResult(
    double Y, double N, double D, double DFore, double DAft, double DepthRatio, double InfluenceWidthM,
    double? PortDistanceM, double? StarboardDistanceM, string? PortBankId, string? StarboardBankId)
{
    public BodyForce Force => new(0.0, Y, N);
}

/// <summary>解析到本地 ENU 的岸壁折線。</summary>
public sealed class BankLine
{
    public string Id { get; }
    public string? Name { get; }
    public bool Sloped { get; }
    /// <summary>斜坡 1:n 的 n(垂直岸壁 0)</summary>
    public double Slope { get; }
    public double TopDepthM { get; }
    public double? ChannelDepthM { get; }
    /// <summary>頂點(ENU,m)</summary>
    public double[] E { get; }
    public double[] N { get; }
    /// <summary>各線段指向陸側的單位法向量(ENU)</summary>
    public double[] LandE { get; }
    public double[] LandN { get; }

    public BankLine(BankDefinition d, LocalTangentPlane proj)
    {
        Id = d.Id;
        Name = d.Name;
        Sloped = d.IsSloped;
        Slope = Sloped ? d.Slope ?? 0.0 : 0.0;
        TopDepthM = d.TopDepthM;
        ChannelDepthM = d.ChannelDepthM;
        var pts = d.Line.Select(p => p.X is not null && p.Y is not null ? (p.X.Value, p.Y.Value) : proj.ToLocal(p.Lat!.Value, p.Lon!.Value)).ToArray();
        E = pts.Select(p => p.Item1).ToArray();
        N = pts.Select(p => p.Item2).ToArray();
        var segs = Math.Max(0, pts.Length - 1);
        LandE = new double[segs];
        LandN = new double[segs];
        var waterRight = !string.Equals(d.WaterSide, "left", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < segs; i++)
        {
            var te = E[i + 1] - E[i];
            var tn = N[i + 1] - N[i];
            var len = Math.Sqrt(te * te + tn * tn);
            if (len < 1e-9) continue; // 重合點:法向量 0,該段不計
            te /= len; tn /= len;
            // 沿折線方向看,右側法向量 = (tn, −te);水域在右 → 陸側在左 = (−tn, te)
            (LandE[i], LandN[i]) = waterRight ? (-tn, te) : (tn, -te);
        }
    }

    /// <summary>等效垂直岸壁相對折線向陸側的偏移(m)與岸型強度倍率 φ(淹沒岸 = (h − h_top)/h)。</summary>
    public (double Offset, double Phi) Equivalent(double localDepth)
    {
        var h = ChannelDepthM ?? localDepth;
        if (!(h > 0.0) || double.IsInfinity(h)) return (0.0, 1.0);
        var span = Math.Max(0.0, h - TopDepthM);
        var phi = Math.Clamp(span / h, 0.0, 1.0);
        // 斜坡岸:以邊坡上「岸腳與岸頂之間的半深處」為等效垂直岸壁位置(折線 = 坡腳)
        var offset = Sloped ? Slope * 0.5 * span : 0.0;
        return (offset, phi);
    }
}

/// <summary>
/// 岸壁效應(規劃書第 6.2 節下標 B、第 458 行;R2;設計文件 docs/design/physics-bank-effect.md):
/// 以「沿船長加權的至岸距離倒數」(y_inf 型加權距離參數)、水深吃水比 h/T、船速平方與螺槳載荷,計算向岸吸力 Y_B 與艏離岸力矩 N_B。
/// 準靜態、純函數(除步首螺槳轉速外沒有跨步狀態);只在情境有 banks 區段且動力學接受外力時啟用(<see cref="Active"/>)。
/// </summary>
public sealed class BankEffectModel : IExternalForceField
{
    public const long HashMarker = 0x42414E4B_45464354; // "BANKEFCT"
    public const int HashLongs = 9;
    private const double Gravity = 9.80665;
    // 3 點 Gauss–Legendre(區間 [−1, 1])
    private static readonly double[] GaussX = { -0.7745966692414834, 0.0, 0.7745966692414834 };
    private static readonly double[] GaussW = { 0.5555555555555556, 0.8888888888888888, 0.5555555555555556 };

    private readonly List<BankLine> _lines;
    private double _rpm;

    public IReadOnlyList<BankLine> Lines => _lines;
    public BankEffectCoefficients Coefficients { get; }
    /// <summary>情境倍率(banks.scale)</summary>
    public double Scale { get; }
    /// <summary>動力學模型接受外力</summary>
    public bool Supported { get; }
    public bool Active => Supported && _lines.Count > 0;
    public double L { get; }
    public double B { get; }
    public double T { get; }
    /// <summary>螺槳直徑(m;particulars 無資料時 0 = 不計螺槳項)</summary>
    public double PropellerDiameter { get; }
    /// <summary>步首螺槳轉速(rpm;RK4 四階段共用,與拖船出力相同處理)</summary>
    public double StepRpm => _rpm;
    /// <summary>最近一次步末計算(雜湊與狀態廣播;由引擎在每步末與還原後更新)</summary>
    public BankEffectResult Last { get; private set; }

    public BankEffectModel(ShipParticulars ship, LoadingCondition loading, ScenarioBanks banks, LocalTangentPlane proj, bool supported,
        BankEffectCoefficients? coefficients = null)
    {
        Supported = supported;
        Coefficients = coefficients ?? BankEffectCoefficients.Default;
        Scale = banks.Scale;
        var lc = ship.GetLoading(loading);
        L = ship.Hull.LengthBetweenPerpendiculars_m;
        B = ship.Hull.Breadth_m;
        T = lc.DraftMean_m ?? lc.MaxDraft_m;
        PropellerDiameter = ship.Propeller.Diameter_m ?? 0.0;
        _lines = banks.All.Select(d => new BankLine(d, proj)).Where(l => l.E.Length >= 2).ToList();
    }

    /// <summary>情境有 banks 時建立(否則 null)。</summary>
    public static BankEffectModel? Create(ShipParticulars ship, LoadingCondition loading, Scenario.Scenario sc, LocalTangentPlane proj, bool supported)
        => sc.Banks is { } b && b.All.Any() ? new BankEffectModel(ship, loading, b, proj, supported) : null;

    /// <summary>步首:記下螺槳轉速(四個 RK4 階段共用)。</summary>
    public void PreStep(double rpm) => _rpm = rpm;

    /// <summary>步末(與還原後):以目前狀態、水深與實際轉速重算 <see cref="Last"/>(雜湊與 state.bank)。</summary>
    public void UpdateLast(in StateVector s, double waterDepth, double rpm) => Last = Compute(s, waterDepth, rpm, detail: true);

    public BodyForce Evaluate(in StateVector state, in EnvironmentSample env) => Compute(state, env.WaterDepthM, _rpm, detail: false).Force;

    /// <summary>
    /// 岸壁力(設計文件第 3 節):
    /// D_half = (B/2)·(2/L)·∫_half w(x)·Σ σ·φ·β(y_eq)·q(y_eq(x)) dx,w = 1 − |x|/L,q(y) = max(0, 1/max(y, y_min) − 1/y_inf),
    /// β(y) = clamp((y + B/2)/B, 0, 1)(船寬在等效岸壁水域側的比例);D = (D_fore + D_aft)/2(正 = 右舷岸較近);
    /// Y = s·F_Y·½ρLT·[u²·(c_lead·D_lead + c_trail·D_trail) + (nD)²·C_YP·D_aft];
    /// N = s·F_N·½ρL²T·[±u²·(ℓ_lead·c_lead·D_lead + ℓ_trail·c_trail·D_trail) + (nD)²·ℓ_P·C_YP·D_aft](正俥 lead = fore,倒退 lead = aft 且取負號)。
    /// </summary>
    public BankEffectResult Compute(in StateVector s, double waterDepth, double rpm, bool detail)
    {
        var c = Coefficients;
        var deep = !(waterDepth > 0.0) || double.IsInfinity(waterDepth);
        var depthRatio = deep ? double.PositiveInfinity : waterDepth / T;
        var hr = Math.Max(depthRatio, c.MinDepthRatio);
        var tau = deep ? 0.0 : 1.0 / (hr - 1.0);
        var speed = Math.Sqrt(s.U * s.U + s.V * s.V);
        var fnh = deep ? 0.0 : Math.Min(0.95, speed / Math.Sqrt(Gravity * waterDepth));
        var fnh2 = fnh * fnh;
        var fnh8 = fnh2 * fnh2 * fnh2 * fnh2;
        var yInf = c.InfluenceWidthB * B * (1.0 + 16.0 * fnh8);
        var yMin = (0.5 + c.MinClearanceB) * B;
        var invInf = 1.0 / yInf;
        var win = c.WindowL * L;
        var reach = win + yInf;

        var sinP = Math.Sin(s.Psi);
        var cosP = Math.Cos(s.Psi);
        double iFore = 0.0, iAft = 0.0;
        double? portD = null, stbdD = null;
        string? portId = null, stbdId = null;

        foreach (var line in _lines)
        {
            var (offset, phi) = line.Equivalent(waterDepth);
            if (phi <= 0.0) continue;
            for (var i = 0; i + 1 < line.E.Length; i++)
            {
                var le = line.LandE[i];
                var ln = line.LandN[i];
                if (le == 0.0 && ln == 0.0) continue;
                // 船體座標(x 向艏、y 向右舷)
                var dea = line.E[i] - s.X; var dna = line.N[i] - s.Y;
                var deb = line.E[i + 1] - s.X; var dnb = line.N[i + 1] - s.Y;
                var xa = dea * sinP + dna * cosP; var ya = dea * cosP - dna * sinP;
                var xb = deb * sinP + dnb * cosP; var yb = deb * cosP - dnb * sinP;
                // 粗略剔除:線段離船舯超過窗半寬 + 影響寬度
                if (SegmentDistance(xa, ya, xb, yb) > reach) continue;
                if (Math.Abs(xb - xa) < 1e-9) continue; // 與船垂直:沿船長投影長度 0
                // 陸側在船的哪一舷:σ = sign(n_land · ŷ_body),ŷ_body(ENU)= (cos ψ, −sin ψ)
                var ny = le * cosP - ln * sinP;
                if (Math.Abs(ny) < 1e-12) continue;
                var sigma = ny > 0.0 ? 1.0 : -1.0;
                var x0 = Math.Min(xa, xb);
                var x1 = Math.Max(xa, xb);
                var slopeY = (yb - ya) / (xb - xa);
                // 船舯(x = 0)的橫向距離(狀態顯示)
                if (detail && x0 <= 0.0 && 0.0 <= x1)
                {
                    var ys0 = sigma * (ya + slopeY * (0.0 - xa));
                    // 與被積函數一致:船寬仍有一部分在水域側(β > 0)才算「旁邊的岸」(薄堤背面、已整個越過的淹沒岸不顯示)
                    if (ys0 + offset > -0.5 * B)
                    {
                        var yEq = Math.Max(0.0, ys0 + offset);
                        if (yEq <= yInf)
                        {
                            if (sigma > 0 && (stbdD is null || yEq < stbdD)) { stbdD = yEq; stbdId = line.Id; }
                            if (sigma < 0 && (portD is null || yEq < portD)) { portD = yEq; portId = line.Id; }
                        }
                    }
                }
                var lo = Math.Max(x0, -win);
                var hi = Math.Min(x1, win);
                if (!(hi > lo)) continue;
                var seg = new Segment(sigma, xa, ya, slopeY, offset, 0.5 * B, yMin, invInf, win);
                if (hi > 0.0) iFore += sigma * phi * Integrate(seg, Math.Max(lo, 0.0), hi, 0.1 * win);
                if (lo < 0.0) iAft += sigma * phi * Integrate(seg, lo, Math.Min(hi, 0.0), 0.1 * win);
            }
        }

        var dFore = 0.5 * B * (2.0 / win) * iFore;
        var dAft = 0.5 * B * (2.0 / win) * iAft;
        var d = 0.5 * (dFore + dAft);
        var fY = 1.0 + c.AY * tau;
        var fN = 1.0 + c.AN * tau;
        var qLT = 0.5 * c.Rho * L * T;
        var u = s.U;
        // 倒退時船艉為前導端:前導/後隨對調、力矩臂反號
        var (dLead, dTrail, dir) = u >= 0.0 ? (dFore, dAft, 1.0) : (dAft, dFore, -1.0);
        var u2 = u * u;
        var nD = rpm > 0.0 && PropellerDiameter > 0.0 ? rpm / 60.0 * PropellerDiameter : 0.0;
        var nD2 = nD * nD;
        var y = Scale * fY * qLT * (u2 * (c.CYLead * dLead + c.CYTrail * dTrail) + nD2 * c.CYP * dAft);
        var n = Scale * fN * qLT * L * (dir * u2 * (c.LeverLead * c.CYLead * dLead + c.LeverTrail * c.CYTrail * dTrail) + nD2 * c.LeverP * c.CYP * dAft);
        return new BankEffectResult(y, n, d, dFore, dAft, depthRatio, yInf, portD, stbdD, portId, stbdId);
    }

    /// <summary>單一線段的被積函數參數:y_eq(x) = σ·(ya + k·(x − xa)) + offset;<paramref name="HalfB"/> = B/2。</summary>
    private readonly record struct Segment(double Sigma, double Xa, double Ya, double K, double Offset, double HalfB, double YMin, double InvInf, double Win)
    {
        /// <summary>
        /// w(x)·β(y_eq)·q(y_eq(x))(設計文件第 3.3 節):
        /// y_eq ≥ y_min 時 q = 1/y_eq − 1/y_inf;B/2 ≤ y_eq &lt; y_min(船側距等效岸壁 0–0.1 B)以 y_min 計;
        /// 船寬跨越等效岸壁線(|y_eq| &lt; B/2;淹沒岸/斜坡岸上方為合法狀態)時再乘 β = (y_eq + B/2)/B 線性淡出,
        /// y_eq ≤ −B/2(整個船寬在陸側:已在淺灘上方,或是薄堤/突堤背面這種被陸地隔開的岸)為 0。全程連續,無階躍。
        /// </summary>
        public double At(double x)
        {
            var ys = Sigma * (Ya + K * (x - Xa)) + Offset;
            if (ys <= -HalfB) return 0.0;
            var q = 1.0 / Math.Max(ys, YMin) - InvInf;
            if (!(q > 0.0)) return 0.0;
            if (ys < HalfB) q *= (ys + HalfB) / (2.0 * HalfB);
            return (1.0 - Math.Abs(x) / Win) * q;
        }
    }

    /// <summary>[a, b] 等分為不超過 <paramref name="maxPiece"/> 的小段,各以 3 點 Gauss–Legendre 積分。</summary>
    private static double Integrate(in Segment f, double a, double b, double maxPiece)
    {
        if (!(b > a)) return 0.0;
        var m = Math.Max(1, (int)Math.Ceiling((b - a) / maxPiece));
        var h = (b - a) / m;
        var sum = 0.0;
        for (var k = 0; k < m; k++)
        {
            var mid = a + (k + 0.5) * h;
            for (var g = 0; g < 3; g++) sum += GaussW[g] * f.At(mid + 0.5 * h * GaussX[g]);
        }
        return sum * 0.5 * h;
    }

    /// <summary>原點到線段 (xa, ya)–(xb, yb) 的距離。</summary>
    private static double SegmentDistance(double xa, double ya, double xb, double yb)
    {
        var dx = xb - xa; var dy = yb - ya;
        var l2 = dx * dx + dy * dy;
        var t = l2 > 0.0 ? Math.Clamp(-(xa * dx + ya * dy) / l2, 0.0, 1.0) : 0.0;
        var px = xa + t * dx; var py = ya + t * dy;
        return Math.Sqrt(px * px + py * py);
    }

    // ------------------------------------------------------------------ 雜湊

    /// <summary>雜湊區塊(只在 Active 時追加):標記、岸壁數、步首轉速、D、D_fore、D_aft、Y、N、y_inf。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        void L64(Span<byte> b, long v) { BinaryPrimitives.WriteInt64LittleEndian(b[o..], v); o += 8; }
        void D64(Span<byte> b, double v) => L64(b, BitConverter.DoubleToInt64Bits(v));
        var r = Last;
        L64(buf, HashMarker);
        L64(buf, _lines.Count);
        D64(buf, _rpm);
        D64(buf, r.D); D64(buf, r.DFore); D64(buf, r.DAft);
        D64(buf, r.Y); D64(buf, r.N); D64(buf, r.InfluenceWidthM);
        return o;
    }

    // ------------------------------------------------------------------ 狀態

    /// <summary>state.bank(對外 kN、kN·m、m)。</summary>
    public BankEffectState State()
    {
        var r = Last;
        double? nearest = null;
        string? side = null, bankId = null;
        if (r.StarboardDistanceM is { } sd) { nearest = sd; side = "starboard"; bankId = r.StarboardBankId; }
        if (r.PortDistanceM is { } pd && (nearest is null || pd < nearest)) { nearest = pd; side = "port"; bankId = r.PortBankId; }
        return new BankEffectState
        {
            YKN = r.Y / 1000.0,
            NKNm = r.N / 1000.0,
            // 力的方向(Y 的正負):船艏先進入岸壁時艏部壓力墊使 Y 與 D 反號,岸在哪一舷看 nearestSide / parameter
            Side = Math.Abs(r.Y) < 1e-6 ? "none" : r.Y > 0 ? "starboard" : "port",
            Parameter = r.D,
            DistanceM = nearest,
            ClearanceM = nearest is { } nd ? nd - 0.5 * B : null,
            NearestSide = side,
            BankId = bankId,
            PortM = r.PortDistanceM,
            StarboardM = r.StarboardDistanceM,
            DepthRatio = double.IsInfinity(r.DepthRatio) ? null : r.DepthRatio,
            InfluenceM = r.InfluenceWidthM,
        };
    }
}

/// <summary>兩個外力場的和(拖船/錨/泊位 + 岸壁;RK4 每階段各自計算後相加)。</summary>
public sealed class SumForceField : IExternalForceField
{
    private readonly IExternalForceField _a, _b;
    public SumForceField(IExternalForceField a, IExternalForceField b) { _a = a; _b = b; }
    public BodyForce Evaluate(in StateVector state, in EnvironmentSample env) => _a.Evaluate(state, env) + _b.Evaluate(state, env);
}

/// <summary>
/// 岸壁效應狀態(state.bank;情境有 banks 區段且動力學接受外力時輸出;C# 產生,Python 不輸出;設計文件 physics-bank-effect.md 第 5 節)。
/// 力為船體座標:Y 右舷正、N 艏向右正(與 u/v/r、MMG 相同慣例)。
/// </summary>
public sealed record BankEffectState
{
    /// <summary>岸壁橫向力(kN,右舷正;向岸吸力)</summary>
    [JsonPropertyName("Y_kN")] public required double YKN { get; init; }
    /// <summary>岸壁艏搖力矩(kN·m,艏向右正;艏離岸 = 與 Y 反號)</summary>
    [JsonPropertyName("N_kNm")] public required double NKNm { get; init; }
    /// <summary>
    /// 岸壁橫向力 Y 指向的舷側 port | starboard | none(Y 的正負;|Y| &lt; 1e-6 N 時 none)。
    /// 不等於岸在哪一舷:船艏先進入岸壁時艏部壓力墊使力推離岸(岸在右舷、side = port);岸的舷側見 <see cref="NearestSide"/>、D 的正負見 <see cref="Parameter"/>。
    /// </summary>
    [JsonPropertyName("side")] public required string Side { get; init; }
    /// <summary>加權距離參數 D(無因次,正 = 右舷岸較近;兩岸等距時 0)</summary>
    [JsonPropertyName("parameter")] public required double Parameter { get; init; }
    /// <summary>船舯至最近等效岸壁的橫向距離(m;影響寬度內無岸壁時省略)</summary>
    [JsonPropertyName("distance_m")] public double? DistanceM { get; init; }
    /// <summary>船側至最近等效岸壁(distance_m − B/2,m)</summary>
    [JsonPropertyName("clearance_m")] public double? ClearanceM { get; init; }
    /// <summary>最近岸壁在哪一舷(port | starboard)</summary>
    [JsonPropertyName("nearestSide")] public string? NearestSide { get; init; }
    /// <summary>最近岸壁 id</summary>
    [JsonPropertyName("bankId")] public string? BankId { get; init; }
    /// <summary>左舷最近等效岸壁距離(船舯,m;影響寬度內無時省略)</summary>
    [JsonPropertyName("port_m")] public double? PortM { get; init; }
    /// <summary>右舷最近等效岸壁距離(船舯,m)</summary>
    [JsonPropertyName("starboard_m")] public double? StarboardM { get; init; }
    /// <summary>水深吃水比 h/T(深水/無水深時省略)</summary>
    [JsonPropertyName("depthRatio")] public double? DepthRatio { get; init; }
    /// <summary>影響寬度 y_inf(自船舯,m)</summary>
    [JsonPropertyName("influence_m")] public double? InfluenceM { get; init; }
}
