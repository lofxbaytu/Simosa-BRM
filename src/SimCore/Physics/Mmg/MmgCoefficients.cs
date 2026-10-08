using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Physics.Mmg;

/// <summary>
/// MMG 係數檔 data/ships/&lt;ID&gt;/coefficients.&lt;loading&gt;.json(schema:src/Contracts/coefficients.schema.json),
/// 由 src/Tools.Calibration(simosa-brm estimate / identify)產生,C# 與 Python 參考實作讀同一份。
/// 欄位依 schema 命名;各區段未列出的鍵(說明文字、識別紀錄、trialFit…)保留在 <c>Extra</c> 以便原樣回寫。
/// 無因次化(Yasukawa &amp; Yoshimura 2015):X、Y 以 ½ρLdU²,N 以 ½ρL²dU²,質量以 ½ρL²d,慣性矩以 ½ρL⁴d;L = LPP、d = 平均吃水。
/// </summary>
public sealed class MmgCoefficients
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("shipId")] public string ShipId { get; set; } = "";
    [JsonPropertyName("shipName")] public string? ShipName { get; set; }
    [JsonPropertyName("loading")] public string Loading { get; set; } = "full";
    [JsonPropertyName("loadingLabel")] public string? LoadingLabel { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("source")] public CoefficientSource Source { get; set; } = new();
    /// <summary>複製自 trial_targets.tolerances(turning_pct、turning_minL、zigzag_deg、zigzag_pct、stopping_pct、speed_kn)</summary>
    [JsonPropertyName("tolerances")] public Dictionary<string, JsonElement>? Tolerances { get; set; }
    [JsonPropertyName("reference")] public ReferenceCoefficients Reference { get; set; } = new();
    [JsonPropertyName("mass")] public MassCoefficients Mass { get; set; } = new();
    [JsonPropertyName("hull")] public HullCoefficients Hull { get; set; } = new();
    [JsonPropertyName("propeller")] public PropellerCoefficients Propeller { get; set; } = new();
    [JsonPropertyName("rudder")] public RudderCoefficients Rudder { get; set; } = new();
    [JsonPropertyName("engine")] public EngineCoefficients Engine { get; set; } = new();
    [JsonPropertyName("thruster")] public ThrusterCoefficients Thruster { get; set; } = new();
    [JsonPropertyName("wind")] public WindCoefficients Wind { get; set; } = new();
    [JsonPropertyName("shallowWater")] public ShallowWaterCoefficients ShallowWater { get; set; } = new();
    [JsonPropertyName("squat")] public SquatCoefficients Squat { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>是否為參數識別結果(否則為經驗公式估計,黃金測試應標註)。</summary>
    public bool IsIdentified => string.Equals(Source.Method, "identify", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>裝載狀態 → 檔名中的字串(full / ballast / intermediate)。</summary>
    public static string LoadingKey(LoadingCondition loading) => loading switch
    {
        LoadingCondition.Full => "full",
        LoadingCondition.Ballast => "ballast",
        _ => "intermediate",
    };

    /// <summary>係數檔路徑:&lt;repoRoot&gt;/data/ships/&lt;shipId&gt;/coefficients.&lt;loading&gt;.json(與 Python paths.coefficients_path 相同)。</summary>
    public static string PathFor(string repoRoot, string shipId, LoadingCondition loading)
        => Path.Combine(repoRoot, "data", "ships", shipId, $"coefficients.{LoadingKey(loading)}.json");

    public static MmgCoefficients Load(string path)
    {
        using var stream = File.OpenRead(path);
        var c = JsonSerializer.Deserialize<MmgCoefficients>(stream, ReadOptions)
                ?? throw new InvalidDataException($"無法解析係數檔:{path}");
        c.Validate(path);
        return c;
    }

    /// <summary>讀取指定船舶與裝載狀態的係數檔;檔案不存在時丟出 <see cref="FileNotFoundException"/>(訊息含產生指令)。</summary>
    public static MmgCoefficients LoadFromDataRoot(string repoRoot, string shipId, LoadingCondition loading)
    {
        var path = PathFor(repoRoot, shipId, loading);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"找不到 MMG 係數檔 {path};請先以 src/Tools.Calibration 產生(uv run simosa-brm identify --ship {shipId} --loading {LoadingKey(loading)},或 estimate --out {path})",
                path);
        return Load(path);
    }

    public static MmgCoefficients FromJson(string json)
    {
        var c = JsonSerializer.Deserialize<MmgCoefficients>(json, ReadOptions)
                ?? throw new InvalidDataException("無法解析係數 JSON");
        c.Validate("(json)");
        return c;
    }

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    private void Validate(string source)
    {
        void Positive(double v, string name)
        {
            if (!(v > 0.0)) throw new InvalidDataException($"係數檔 {source}:{name} 必須為正(目前 {v})");
        }
        if (string.IsNullOrWhiteSpace(ShipId)) throw new InvalidDataException($"係數檔 {source} 缺少 shipId");
        Positive(Reference.Length_m, "reference.length_m");
        Positive(Reference.Draft_m, "reference.draft_m");
        Positive(Reference.Density_kgm3, "reference.density_kgm3");
        Positive(Reference.Mass_kg, "reference.mass_kg");
        Positive(Mass.M, "mass.m");
        Positive(Propeller.Diameter_m, "propeller.diameter_m");
        if (Propeller.Kt is null || Propeller.Kt.Length != 3) throw new InvalidDataException($"係數檔 {source}:propeller.kt 須為 3 個係數");
        if (Propeller.Kq is null || Propeller.Kq.Length != 3) throw new InvalidDataException($"係數檔 {source}:propeller.kq 須為 3 個係數");
        Positive(Rudder.Area_m2, "rudder.area_m2");
        Positive(Rudder.MaxAngle_deg, "rudder.maxAngle_deg");
        Positive(Rudder.Rate_degps, "rudder.rate_degps");
        Positive(Engine.MaxRpm, "engine.maxRpm");
        Positive(Engine.RpmTimeConstant_s, "engine.rpmTimeConstant_s");
        Positive(Engine.ShaftStopTimeConstant_s, "engine.shaftStopTimeConstant_s");
        if (Hull.Resistance.Model is not ("viscous+wave" or "constant"))
            throw new InvalidDataException($"係數檔 {source}:hull.resistance.model 須為 viscous+wave 或 constant(目前 {Hull.Resistance.Model})");
        Positive(Hull.CrossFlow.UFloor_mps, "hull.crossFlow.uFloor_mps");
    }
}

public sealed class CoefficientSource
{
    /// <summary>estimate(經驗公式)或 identify(參數識別)</summary>
    [JsonPropertyName("method")] public string Method { get; set; } = "estimate";
    [JsonPropertyName("particulars")] public string? Particulars { get; set; }
    [JsonPropertyName("trialTargets")] public string? TrialTargets { get; set; }
    /// <summary>trial:試俥實測;poster:IMO 駕駛台海報讀值</summary>
    [JsonPropertyName("dataGrade")] public string? DataGrade { get; set; }
    [JsonPropertyName("identifiedParameters")] public List<string> IdentifiedParameters { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ReferenceCoefficients
{
    /// <summary>LPP(m)</summary>
    [JsonPropertyName("length_m")] public double Length_m { get; set; }
    [JsonPropertyName("breadth_m")] public double Breadth_m { get; set; }
    /// <summary>平均吃水(m)</summary>
    [JsonPropertyName("draft_m")] public double Draft_m { get; set; }
    [JsonPropertyName("draftFore_m")] public double? DraftFore_m { get; set; }
    [JsonPropertyName("draftAft_m")] public double? DraftAft_m { get; set; }
    [JsonPropertyName("blockCoefficient")] public double BlockCoefficient { get; set; }
    [JsonPropertyName("displacement_t")] public double Displacement_t { get; set; }
    [JsonPropertyName("mass_kg")] public double Mass_kg { get; set; }
    /// <summary>重心距船舯(前正)</summary>
    [JsonPropertyName("xG_m")] public double XG_m { get; set; }
    [JsonPropertyName("density_kgm3")] public double Density_kgm3 { get; set; } = 1025.0;
    [JsonPropertyName("airDensity_kgm3")] public double AirDensity_kgm3 { get; set; } = 1.225;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MassCoefficients
{
    /// <summary>m' = m/(½ρL²d)</summary>
    [JsonPropertyName("m")] public double M { get; set; }
    [JsonPropertyName("mx")] public double Mx { get; set; }
    [JsonPropertyName("my")] public double My { get; set; }
    [JsonPropertyName("Izz")] public double Izz { get; set; }
    [JsonPropertyName("Jzz")] public double Jzz { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HullCoefficients
{
    [JsonPropertyName("resistance")] public ResistanceCoefficients Resistance { get; set; } = new();
    [JsonPropertyName("Xvv")] public double Xvv { get; set; }
    [JsonPropertyName("Xvr")] public double Xvr { get; set; }
    [JsonPropertyName("Xrr")] public double Xrr { get; set; }
    [JsonPropertyName("Xvvvv")] public double Xvvvv { get; set; }
    [JsonPropertyName("Yv")] public double Yv { get; set; }
    [JsonPropertyName("Yr")] public double Yr { get; set; }
    [JsonPropertyName("Yvvv")] public double Yvvv { get; set; }
    [JsonPropertyName("Yvvr")] public double Yvvr { get; set; }
    [JsonPropertyName("Yvrr")] public double Yvrr { get; set; }
    [JsonPropertyName("Yrrr")] public double Yrrr { get; set; }
    [JsonPropertyName("Nv")] public double Nv { get; set; }
    [JsonPropertyName("Nr")] public double Nr { get; set; }
    [JsonPropertyName("Nvvv")] public double Nvvv { get; set; }
    [JsonPropertyName("Nvvr")] public double Nvvr { get; set; }
    [JsonPropertyName("Nvrr")] public double Nvrr { get; set; }
    [JsonPropertyName("Nrrr")] public double Nrrr { get; set; }
    [JsonPropertyName("crossFlow")] public CrossFlowCoefficients CrossFlow { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>總阻力係數 R0'(U):viscous+wave(ITTC-57·形狀因子·S/(L·d) + c_w·(Fn/Fn_ref)^q)或 constant(KVLCC2 基準)。</summary>
public sealed class ResistanceCoefficients
{
    [JsonPropertyName("model")] public string Model { get; set; } = "viscous+wave";
    [JsonPropertyName("viscous")] public ViscousResistance? Viscous { get; set; }
    [JsonPropertyName("wave")] public WaveResistance? Wave { get; set; }
    [JsonPropertyName("lowSpeedFloor")] public LowSpeedFloor? LowSpeedFloor { get; set; }
    /// <summary>model = constant 時的固定 R0'</summary>
    [JsonPropertyName("R0")] public double? R0 { get; set; }
    [JsonPropertyName("scale")] public double? Scale { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ViscousResistance
{
    [JsonPropertyName("wettedSurface_m2")] public double WettedSurface_m2 { get; set; }
    [JsonPropertyName("formFactor")] public double FormFactor { get; set; } = 1.0;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class WaveResistance
{
    [JsonPropertyName("coefficient")] public double Coefficient { get; set; }
    [JsonPropertyName("froudeExponent")] public double FroudeExponent { get; set; } = 4.0;
    [JsonPropertyName("froudeRef")] public double FroudeRef { get; set; } = 0.2;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>U 低於最低試俥資料點時 R0' 不低於該點模型值。</summary>
public sealed class LowSpeedFloor
{
    [JsonPropertyName("speed_mps")] public double Speed_mps { get; set; }
    [JsonPropertyName("R0")] public double R0 { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>大漂角/低速橫流阻力與多項式的混合設定(mmg.py forces)。</summary>
public sealed class CrossFlowCoefficients
{
    [JsonPropertyName("Cd")] public double Cd { get; set; } = 1.0;
    [JsonPropertyName("blendStart_deg")] public double BlendStart_deg { get; set; } = 20.0;
    [JsonPropertyName("blendEnd_deg")] public double BlendEnd_deg { get; set; } = 40.0;
    /// <summary>艏搖通道 atan(½L|r|/|u|) 的混合範圍;舊係數檔無此鍵時用 30/45</summary>
    [JsonPropertyName("yawBlendStart_deg")] public double YawBlendStart_deg { get; set; } = 30.0;
    [JsonPropertyName("yawBlendEnd_deg")] public double YawBlendEnd_deg { get; set; } = 45.0;
    [JsonPropertyName("uFloor_mps")] public double UFloor_mps { get; set; } = 0.5;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PropellerCoefficients
{
    [JsonPropertyName("diameter_m")] public double Diameter_m { get; set; }
    [JsonPropertyName("pitchRatio")] public double? PitchRatio { get; set; }
    [JsonPropertyName("expandedAreaRatio")] public double? ExpandedAreaRatio { get; set; }
    [JsonPropertyName("blades")] public int? Blades { get; set; }
    [JsonPropertyName("rotation")] public string Rotation { get; set; } = "right";
    /// <summary>K_T = k0 + k1·J + k2·J²</summary>
    [JsonPropertyName("kt")] public double[]? Kt { get; set; }
    [JsonPropertyName("kq")] public double[]? Kq { get; set; }
    [JsonPropertyName("ktFitRange_J")] public double? KtFitRange_J { get; set; }
    /// <summary>多項式適用上限(風車區截止)</summary>
    [JsonPropertyName("jMax")] public double JMax { get; set; } = 1.0;
    [JsonPropertyName("wP0")] public double WP0 { get; set; }
    [JsonPropertyName("tP")] public double TP { get; set; }
    /// <summary>x_P' = x_P/L</summary>
    [JsonPropertyName("xP")] public double XP { get; set; } = -0.48;
    [JsonPropertyName("wakeDriftFactor")] public double WakeDriftFactor { get; set; } = 4.0;
    [JsonPropertyName("asternThrustFactor")] public double AsternThrustFactor { get; set; } = 0.85;
    [JsonPropertyName("jClip")] public double JClip { get; set; } = 0.9;
    [JsonPropertyName("lockedDragCd")] public double LockedDragCd { get; set; } = 0.5;
    [JsonPropertyName("lockRps")] public double LockRps { get; set; } = 0.3;
    [JsonPropertyName("sideForceFactor")] public double SideForceFactor { get; set; } = 0.08;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RudderCoefficients
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("area_m2")] public double Area_m2 { get; set; }
    [JsonPropertyName("span_m")] public double? Span_m { get; set; }
    /// <summary>Fujii 升力斜率 6.13Λ/(Λ+2.25)</summary>
    [JsonPropertyName("fAlpha")] public double FAlpha { get; set; }
    [JsonPropertyName("schilling")] public SchillingCoefficients Schilling { get; set; } = new();
    [JsonPropertyName("maxAngle_deg")] public double MaxAngle_deg { get; set; } = 35.0;
    /// <summary>舵機速率(度/秒;70° 行程 / hardOverTime35_s)</summary>
    [JsonPropertyName("rate_degps")] public double Rate_degps { get; set; }
    [JsonPropertyName("neutralAngle_deg")] public double NeutralAngle_deg { get; set; }
    /// <summary>螺槳滑流旋轉造成的有效舵角偏移(右正),乘以滑流加速因子(上限 5)</summary>
    [JsonPropertyName("swirlAngle_deg")] public double SwirlAngle_deg { get; set; }
    [JsonPropertyName("tR")] public double TR { get; set; }
    [JsonPropertyName("aH")] public double AH { get; set; }
    /// <summary>x_H' = x_H/L</summary>
    [JsonPropertyName("xH")] public double XH { get; set; }
    /// <summary>x_R' = x_R/L</summary>
    [JsonPropertyName("xR")] public double XR { get; set; } = -0.5;
    [JsonPropertyName("gammaRMinus")] public double GammaRMinus { get; set; }
    [JsonPropertyName("gammaRPlus")] public double GammaRPlus { get; set; }
    /// <summary>l_R' = l_R/L</summary>
    [JsonPropertyName("lR")] public double LR { get; set; }
    [JsonPropertyName("epsilon")] public double Epsilon { get; set; }
    [JsonPropertyName("kappa")] public double Kappa { get; set; }
    /// <summary>D_P / H_R</summary>
    [JsonPropertyName("eta")] public double Eta { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Schilling 舵分段升力:|α| ≤ linearLimit 用 f_α sin α,之後以 highLiftSlopeFactor·f_α 的斜率延伸至 maxAngle。</summary>
public sealed class SchillingCoefficients
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("linearLimit_deg")] public double LinearLimit_deg { get; set; } = 35.0;
    [JsonPropertyName("maxAngle_deg")] public double MaxAngle_deg { get; set; } = 35.0;
    [JsonPropertyName("highLiftSlopeFactor")] public double HighLiftSlopeFactor { get; set; } = 0.5;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class EngineCoefficients
{
    [JsonPropertyName("mcr_rpm")] public double Mcr_rpm { get; set; }
    [JsonPropertyName("maxRpm")] public double MaxRpm { get; set; }
    [JsonPropertyName("minRpm")] public double MinRpm { get; set; }
    [JsonPropertyName("criticalRpmRange")] public double[]? CriticalRpmRange { get; set; }
    /// <summary>車鐘 → rpm(倒車負),鍵為 state.schema.json 的 telegraph 列舉(EFAS…NAVF)</summary>
    [JsonPropertyName("telegraph")] public Dictionary<string, double> Telegraph { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("rpmTimeConstant_s")] public double RpmTimeConstant_s { get; set; } = 6.0;
    [JsonPropertyName("rpmRateLimit_rpmps")] public double RpmRateLimit_rpmps { get; set; } = 3.0;
    [JsonPropertyName("startDelay_s")] public double StartDelay_s { get; set; } = 5.0;
    [JsonPropertyName("reversalDelay_s")] public double ReversalDelay_s { get; set; } = 90.0;
    [JsonPropertyName("shaftStopTimeConstant_s")] public double ShaftStopTimeConstant_s { get; set; } = 20.0;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ThrusterCoefficients
{
    [JsonPropertyName("installed")] public bool Installed { get; set; } = true;
    /// <summary>側推距船舯(前正)</summary>
    [JsonPropertyName("x_m")] public double X_m { get; set; }
    [JsonPropertyName("nominalThrust_kN")] public double NominalThrust_kN { get; set; }
    [JsonPropertyName("effectiveness")] public double Effectiveness { get; set; } = 1.0;
    [JsonPropertyName("fullThrustDelay_s")] public double FullThrustDelay_s { get; set; } = 30.0;
    /// <summary>推力降至一半的前進速度(kn)</summary>
    [JsonPropertyName("halfThrustSpeed_kn")] public double HalfThrustSpeed_kn { get; set; } = 2.5;
    [JsonPropertyName("zeroSpeedTurningRate_degPerMin")] public double? ZeroSpeedTurningRate_degPerMin { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Blendermann (1994) 參數式風力。</summary>
public sealed class WindCoefficients
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("lateralArea_m2")] public double LateralArea_m2 { get; set; }
    [JsonPropertyName("frontalArea_m2")] public double FrontalArea_m2 { get; set; }
    /// <summary>側面積形心距船舯(前正)</summary>
    [JsonPropertyName("lateralCentroid_m")] public double LateralCentroid_m { get; set; }
    [JsonPropertyName("CDt")] public double CDt { get; set; } = 0.7;
    [JsonPropertyName("CDlHead")] public double CDlHead { get; set; } = 0.9;
    [JsonPropertyName("CDlTail")] public double CDlTail { get; set; } = 0.55;
    [JsonPropertyName("delta")] public double Delta { get; set; } = 0.4;
    [JsonPropertyName("airDensity_kgm3")] public double AirDensity_kgm3 { get; set; } = 1.225;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Kijima 型淺水倍率 f = 1 + a·(T/(h−T))^n,各組一對 (a, n)。</summary>
public sealed class ShallowWaterCoefficients
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("linearSway")] public ShallowWaterGroup LinearSway { get; set; } = new();
    [JsonPropertyName("linearYaw")] public ShallowWaterGroup LinearYaw { get; set; } = new();
    [JsonPropertyName("nonlinear")] public ShallowWaterGroup Nonlinear { get; set; } = new();
    [JsonPropertyName("addedMassSway")] public ShallowWaterGroup AddedMassSway { get; set; } = new();
    [JsonPropertyName("addedMassSurge")] public ShallowWaterGroup AddedMassSurge { get; set; } = new();
    [JsonPropertyName("addedInertia")] public ShallowWaterGroup AddedInertia { get; set; } = new();
    [JsonPropertyName("resistance")] public ShallowWaterGroup Resistance { get; set; } = new();
    [JsonPropertyName("minDepthRatio")] public double MinDepthRatio { get; set; } = 1.05;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ShallowWaterGroup
{
    [JsonPropertyName("a")] public double A { get; set; }
    [JsonPropertyName("n")] public double N { get; set; } = 1.0;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>ICORELS squat:S = Cs·∇/L²·Fnh²/√(1−Fnh²)。</summary>
public sealed class SquatCoefficients
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("Cs")] public double Cs { get; set; } = 2.0;
    [JsonPropertyName("fittedFromTable")] public bool? FittedFromTable { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
