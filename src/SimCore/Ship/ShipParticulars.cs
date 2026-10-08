using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Ship;

/// <summary>
/// data/ships/&lt;ID&gt;/particulars.json(src/Contracts/ship-particulars.schema.json)。
/// 造船廠文件尚未取得的欄位為 null(規劃書附錄 F),因此除 schema 的 required 欄位外全部可為 null;
/// 未列出的欄位保留在 <see cref="Extra"/> 供日後模組(錨、風壓面積、squat 表)讀取。
/// </summary>
public sealed class ShipParticulars
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("imo")] public string? Imo { get; set; }
    [JsonPropertyName("callSign")] public string? CallSign { get; set; }
    [JsonPropertyName("hull")] public HullParticulars Hull { get; set; } = new();
    [JsonPropertyName("loadingConditions")] public Dictionary<string, LoadingParticulars> LoadingConditions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("engine")] public EngineParticulars Engine { get; set; } = new();
    [JsonPropertyName("propeller")] public PropellerParticulars Propeller { get; set; } = new();
    [JsonPropertyName("rudder")] public RudderParticulars Rudder { get; set; } = new();
    [JsonPropertyName("bowThruster")] public BowThrusterParticulars? BowThruster { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>讀取指定檔案。</summary>
    public static ShipParticulars Load(string path)
    {
        using var stream = File.OpenRead(path);
        var p = JsonSerializer.Deserialize<ShipParticulars>(stream, ReadOptions)
                ?? throw new InvalidDataException($"無法解析船舶資料:{path}");
        p.Validate(path);
        return p;
    }

    /// <summary>讀取 &lt;repoRoot&gt;/data/ships/&lt;shipId&gt;/particulars.json。</summary>
    public static ShipParticulars LoadFromDataRoot(string repoRoot, string shipId)
        => Load(Path.Combine(repoRoot, "data", "ships", shipId, "particulars.json"));

    public static ShipParticulars FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<ShipParticulars>(json, ReadOptions)
                ?? throw new InvalidDataException("無法解析船舶資料 JSON");
        p.Validate("(json)");
        return p;
    }

    private void Validate(string source)
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException($"船舶資料缺少 id:{source}");
        if (Hull.LengthBetweenPerpendiculars_m <= 0) throw new InvalidDataException($"船舶資料缺少 hull.lengthBetweenPerpendiculars_m:{source}");
        if (!LoadingConditions.ContainsKey("full") || !LoadingConditions.ContainsKey("ballast"))
            throw new InvalidDataException($"船舶資料需含 loadingConditions.full 與 ballast:{source}");
        if (Engine.Telegraph.Count == 0) throw new InvalidDataException($"船舶資料缺少 engine.telegraph:{source}");
    }

    /// <summary>取得裝載狀態;intermediate 未定義時以 full 與 ballast 的平均值估計(註明來源)。</summary>
    public LoadingParticulars GetLoading(LoadingCondition condition)
    {
        var key = condition switch
        {
            LoadingCondition.Full => "full",
            LoadingCondition.Ballast => "ballast",
            _ => "intermediate",
        };
        if (LoadingConditions.TryGetValue(key, out var lc)) return lc;
        if (condition != LoadingCondition.Intermediate) throw new KeyNotFoundException($"裝載狀態 {key} 不存在");
        var f = LoadingConditions["full"];
        var b = LoadingConditions["ballast"];
        static double? Avg(double? a, double? c) => a is null || c is null ? (a ?? c) : (a + c) / 2.0;
        return new LoadingParticulars
        {
            Label = "中間狀態(由 full 與 ballast 平均估計)",
            Displacement_t = Avg(f.Displacement_t, b.Displacement_t),
            DraftFore_m = Avg(f.DraftFore_m, b.DraftFore_m),
            DraftAft_m = Avg(f.DraftAft_m, b.DraftAft_m),
            DraftMean_m = Avg(f.DraftMean_m, b.DraftMean_m),
            BlockCoefficient = Avg(f.BlockCoefficient, b.BlockCoefficient),
            Source = "estimated: average of full and ballast",
        };
    }

    /// <summary>車鐘 → particulars.engine.telegraph 的鍵名。</summary>
    public static string TelegraphKey(TelegraphOrder order) => order switch
    {
        TelegraphOrder.EFAS => "emergencyFullAstern",
        TelegraphOrder.FAS => "fullAstern",
        TelegraphOrder.HAS => "halfAstern",
        TelegraphOrder.SAS => "slowAstern",
        TelegraphOrder.DSAS => "deadSlowAstern",
        TelegraphOrder.STOP => "stop",
        TelegraphOrder.DSAH => "deadSlowAhead",
        TelegraphOrder.SAH => "slowAhead",
        TelegraphOrder.HAH => "halfAhead",
        TelegraphOrder.FAH => "fullAhead",
        TelegraphOrder.NAVF => "fullSea",
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    /// <summary>車鐘對應轉速(rpm,倒車負);STOP 為 0;EFAS 未定義時退回 fullAstern。</summary>
    public double TelegraphRpm(TelegraphOrder order)
    {
        if (order == TelegraphOrder.STOP) return 0.0;
        if (Engine.Telegraph.TryGetValue(TelegraphKey(order), out var s)) return s.Rpm;
        if (order == TelegraphOrder.EFAS && Engine.Telegraph.TryGetValue("fullAstern", out var fas)) return fas.Rpm;
        throw new KeyNotFoundException($"particulars.engine.telegraph 缺少 {TelegraphKey(order)}");
    }

    /// <summary>車鐘對應航速(kn,僅前進車令有定義;倒車與 STOP 回傳 null)。</summary>
    public double? TelegraphSpeedKn(TelegraphOrder order, LoadingCondition loading)
    {
        if (!Engine.Telegraph.TryGetValue(TelegraphKey(order), out var s)) return null;
        return loading == LoadingCondition.Full ? s.SpeedLoaded_kn
             : loading == LoadingCondition.Ballast ? s.SpeedBallast_kn
             : (s.SpeedLoaded_kn is { } a && s.SpeedBallast_kn is { } b ? (a + b) / 2.0 : s.SpeedLoaded_kn ?? s.SpeedBallast_kn);
    }

    /// <summary>
    /// 前進方向的 rpm–航速對照(由 particulars 車鐘表建立,含原點),供簡化模型內插;
    /// 回傳依 rpm 遞增排序的 (rpm, kn) 點。
    /// </summary>
    public IReadOnlyList<(double Rpm, double SpeedKn)> AheadSpeedCurve(LoadingCondition loading)
    {
        var pts = new List<(double, double)> { (0.0, 0.0) };
        foreach (var order in new[] { TelegraphOrder.DSAH, TelegraphOrder.SAH, TelegraphOrder.HAH, TelegraphOrder.FAH, TelegraphOrder.NAVF })
        {
            if (!Engine.Telegraph.TryGetValue(TelegraphKey(order), out var s)) continue;
            var kn = TelegraphSpeedKn(order, loading);
            if (kn is null || s.Rpm <= 0) continue;
            pts.Add((s.Rpm, kn.Value));
        }
        pts.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return pts;
    }
}

public sealed class HullParticulars
{
    [JsonPropertyName("lengthOverall_m")] public double LengthOverall_m { get; set; }
    [JsonPropertyName("lengthBetweenPerpendiculars_m")] public double LengthBetweenPerpendiculars_m { get; set; }
    [JsonPropertyName("breadth_m")] public double Breadth_m { get; set; }
    [JsonPropertyName("depth_m")] public double Depth_m { get; set; }
    [JsonPropertyName("bulbousBow")] public bool? BulbousBow { get; set; }
    [JsonPropertyName("transomStern")] public bool? TransomStern { get; set; }
    [JsonPropertyName("bilgeKeelArea_m2")] public double? BilgeKeelArea_m2 { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LoadingParticulars
{
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("displacement_t")] public double? Displacement_t { get; set; }
    [JsonPropertyName("draftFore_m")] public double? DraftFore_m { get; set; }
    [JsonPropertyName("draftAft_m")] public double? DraftAft_m { get; set; }
    [JsonPropertyName("draftMean_m")] public double? DraftMean_m { get; set; }
    [JsonPropertyName("blockCoefficient")] public double? BlockCoefficient { get; set; }
    [JsonPropertyName("kg_m")] public double? Kg_m { get; set; }
    [JsonPropertyName("gm_m")] public double? Gm_m { get; set; }
    [JsonPropertyName("propellerImmersion_pct")] public double? PropellerImmersion_pct { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>最大吃水(艏艉取大者;缺資料時用平均吃水)。</summary>
    public double MaxDraft_m => Math.Max(DraftFore_m ?? 0, Math.Max(DraftAft_m ?? 0, DraftMean_m ?? 0));
}

public sealed class EngineParticulars
{
    [JsonPropertyName("maker")] public string? Maker { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mcr_kW")] public double McrKw { get; set; }
    [JsonPropertyName("mcr_rpm")] public double McrRpm { get; set; }
    [JsonPropertyName("ncr_kW")] public double? NcrKw { get; set; }
    [JsonPropertyName("ncr_rpm")] public double? NcrRpm { get; set; }
    [JsonPropertyName("minimumRpm")] public double? MinimumRpm { get; set; }
    [JsonPropertyName("criticalRpmRange")] public double[]? CriticalRpmRange { get; set; }
    [JsonPropertyName("asternPowerFraction")] public double? AsternPowerFraction { get; set; }
    [JsonPropertyName("maxConsecutiveStarts")] public int? MaxConsecutiveStarts { get; set; }
    [JsonPropertyName("asternTimeLimit_min")] public double? AsternTimeLimit_min { get; set; }
    [JsonPropertyName("telegraph")] public Dictionary<string, TelegraphSetting> Telegraph { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TelegraphSetting
{
    [JsonPropertyName("rpm")] public double Rpm { get; set; }
    [JsonPropertyName("speedLoaded_kn")] public double? SpeedLoaded_kn { get; set; }
    [JsonPropertyName("speedBallast_kn")] public double? SpeedBallast_kn { get; set; }
}

public sealed class PropellerParticulars
{
    [JsonPropertyName("maker")] public string? Maker { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("blades")] public int? Blades { get; set; }
    [JsonPropertyName("diameter_m")] public double? Diameter_m { get; set; }
    [JsonPropertyName("pitch_m")] public double? Pitch_m { get; set; }
    [JsonPropertyName("pitchRatio")] public double? PitchRatio { get; set; }
    [JsonPropertyName("expandedAreaRatio")] public double? ExpandedAreaRatio { get; set; }
    [JsonPropertyName("rotation")] public string? Rotation { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RudderParticulars
{
    [JsonPropertyName("maker")] public string? Maker { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("area_m2")] public double? Area_m2 { get; set; }
    [JsonPropertyName("maxAngle_deg")] public double MaxAngle_deg { get; set; } = 35.0;
    /// <summary>一般操舵的最大舵角(Schilling 舵 70° 僅低速模式);未定義時取 maxAngle_deg。</summary>
    [JsonPropertyName("normalMaxAngle_deg")] public double? NormalMaxAngle_deg { get; set; }
    [JsonPropertyName("steeringGear")] public SteeringGearParticulars? SteeringGear { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public double NormalMaxAngleDeg => NormalMaxAngle_deg ?? MaxAngle_deg;

    /// <summary>35°→35°(或等效)滿舵時間;依序取 hardOverTime35_s、雙泵、單泵;均無時用 SOLAS 典型 28 s。</summary>
    public double HardOverTime35Seconds =>
        SteeringGear?.HardOverTime35_s ?? SteeringGear?.HardOverTime35_twoPumps_s ?? SteeringGear?.HardOverTime35_onePump_s ?? 28.0;
}

public sealed class SteeringGearParticulars
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("torque_kNm")] public double? Torque_kNm { get; set; }
    [JsonPropertyName("hardOverTime35_s")] public double? HardOverTime35_s { get; set; }
    [JsonPropertyName("hardOverTime35_onePump_s")] public double? HardOverTime35_onePump_s { get; set; }
    [JsonPropertyName("hardOverTime35_twoPumps_s")] public double? HardOverTime35_twoPumps_s { get; set; }
    [JsonPropertyName("pumps")] public int? Pumps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BowThrusterParticulars
{
    [JsonPropertyName("maker")] public string? Maker { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("power_kW")] public double? Power_kW { get; set; }
    [JsonPropertyName("tunnelDiameter_m")] public double? TunnelDiameter_m { get; set; }
    [JsonPropertyName("nominalThrust_kN")] public double? NominalThrust_kN { get; set; }
    [JsonPropertyName("fullThrustDelay_s")] public double? FullThrustDelay_s { get; set; }
    [JsonPropertyName("turningRateAtZeroSpeed_degPerMin")] public double? TurningRateAtZeroSpeed_degPerMin { get; set; }
    [JsonPropertyName("reversalTime_s")] public double? ReversalTime_s { get; set; }
    [JsonPropertyName("notEffectiveAbove_kn")] public double? NotEffectiveAbove_kn { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
