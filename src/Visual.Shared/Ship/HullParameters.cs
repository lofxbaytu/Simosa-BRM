using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Visual.Shared.Ship;

/// <summary>
/// 程序化船體的參數(自 particulars.json 推得;FSB1/FSB2 無 3D 模型,先以參數化船體代表 — **待替換為實船模型**)。
/// 船體座標:x 向艏(船舯原點)、y 向右舷、z 自基線向上;節點放置時水線在 y = 0(吃水以 −Draft 下沉)。
/// 位置估計依瀝青船/小型油輪常見布置(機艙與上層建築在艉、艏樓在艏);particulars 有值者優先,其餘以比例估計並註明。
/// </summary>
public sealed record HullParameters
{
    public required string ShipId { get; init; }
    public required string Name { get; init; }
    public required double Loa { get; init; }
    public required double Lpp { get; init; }
    public required double Beam { get; init; }
    /// <summary>型深(基線到上甲板)</summary>
    public required double Depth { get; init; }
    /// <summary>吃水(m;依裝載狀態)</summary>
    public required double Draft { get; init; }
    public bool BulbousBow { get; init; }
    public bool TransomStern { get; init; }
    /// <summary>艏樓長(m,自艏柱向後)</summary>
    public required double ForecastleLength { get; init; }
    public required double ForecastleHeight { get; init; }
    /// <summary>上層建築前壁距船舯的 x(負 = 艉側)</summary>
    public required double SuperstructureForeX { get; init; }
    public required double SuperstructureAftX { get; init; }
    /// <summary>駕駛台甲板高度(基線以上)</summary>
    public required double BridgeDeckHeight { get; init; }
    /// <summary>眼高(基線以上;規劃書 particulars.bridge.eyeHeightAboveBase_m;無則估計)</summary>
    public required double EyeHeightAboveBase { get; init; }
    /// <summary>駕駛台前窗位置 x(船舯原點)</summary>
    public required double BridgeX { get; init; }
    /// <summary>翼橋半寬(= 半船寬)</summary>
    public double WingHalfWidth => Beam / 2;
    public required double MastTopAboveBase { get; init; }
    /// <summary>是否為估計(眼高無實測)</summary>
    public required bool EyeHeightEstimated { get; init; }
    public string Note { get; init; } = "";

    /// <summary>眼高(水線以上)</summary>
    public double EyeHeightAboveWater => EyeHeightAboveBase - Draft;

    public static HullParameters FromParticulars(ShipParticulars p, LoadingCondition loading)
    {
        var hull = p.Hull;
        var lc = p.GetLoading(loading);
        var draft = lc.DraftMean_m ?? lc.MaxDraft_m;
        var loa = hull.LengthOverall_m > 0 ? hull.LengthOverall_m : hull.LengthBetweenPerpendiculars_m * 1.06;
        var depth = hull.Depth_m > 0 ? hull.Depth_m : hull.Breadth_m * 0.5;

        double? eye = null;
        double? mast = null;
        if (p.Extra is not null && p.Extra.TryGetValue("bridge", out var bridge) && bridge.ValueKind == JsonValueKind.Object)
        {
            if (bridge.TryGetProperty("eyeHeightAboveBase_m", out var e) && e.ValueKind == JsonValueKind.Number) eye = e.GetDouble();
            if (bridge.TryGetProperty("mastTopAboveBase_m", out var m) && m.ValueKind == JsonValueKind.Number) mast = m.GetDouble();
        }
        // 估計:上層建築 3 層(每層 2.8 m)在上甲板上,駕駛台甲板再加 1 層,眼高 = 駕駛台甲板 + 1.7 m
        var bridgeDeck = depth + 3 * 2.8;
        var eyeEstimated = eye is null;
        var eyeHeight = eye ?? bridgeDeck + 1.7;
        var superAft = -loa * 0.47;      // 艉段
        var superFore = -loa * 0.28;     // 上層建築前壁(約 LOA 的 19 % 長)
        return new HullParameters
        {
            ShipId = p.Id,
            Name = p.Name,
            Loa = loa,
            Lpp = hull.LengthBetweenPerpendiculars_m,
            Beam = hull.Breadth_m,
            Depth = depth,
            Draft = draft,
            BulbousBow = hull.BulbousBow ?? false,
            TransomStern = hull.TransomStern ?? false,
            ForecastleLength = loa * 0.12,
            ForecastleHeight = 2.5,
            SuperstructureForeX = superFore,
            SuperstructureAftX = superAft,
            BridgeDeckHeight = bridgeDeck,
            EyeHeightAboveBase = eyeHeight,
            BridgeX = superFore - 1.0,     // 駕駛台前窗略在前壁後方(眼位)
            MastTopAboveBase = mast ?? bridgeDeck + 8.0,
            EyeHeightEstimated = eyeEstimated,
            Note = "程序化船體(待替換為實船模型);上層建築位置與眼高為依 LOA/型深的估計" + (eyeEstimated ? "" : ",眼高取 particulars.bridge.eyeHeightAboveBase_m"),
        };
    }

    /// <summary>目標船(只知 LOA/B/吃水、船型)的近似參數。</summary>
    public static HullParameters ForTarget(string id, string name, string shipType, double loa, double beam, double? draft)
    {
        var depth = Math.Max(2.0, beam * 0.5);
        var d = draft ?? depth * 0.65;
        var fishing = string.Equals(shipType, "fishing", StringComparison.OrdinalIgnoreCase);
        var pilot = shipType.Contains("pilot", StringComparison.OrdinalIgnoreCase) || shipType.Contains("tug", StringComparison.OrdinalIgnoreCase);
        var superFore = fishing || pilot ? loa * 0.05 : -loa * 0.28; // 漁船/引水船駕駛台在前段
        var superAft = fishing || pilot ? -loa * 0.25 : -loa * 0.47;
        var bridgeDeck = depth + (fishing || pilot ? 1 : Math.Clamp(Math.Round(loa / 60), 2, 5)) * 2.8;
        return new HullParameters
        {
            ShipId = id,
            Name = name,
            Loa = loa,
            Lpp = loa * 0.94,
            Beam = beam,
            Depth = depth,
            Draft = Math.Min(d, depth * 0.9),
            BulbousBow = loa > 80,
            TransomStern = true,
            ForecastleLength = loa * 0.1,
            ForecastleHeight = 2.0,
            SuperstructureForeX = superFore,
            SuperstructureAftX = superAft,
            BridgeDeckHeight = bridgeDeck,
            EyeHeightAboveBase = bridgeDeck + 1.7,
            BridgeX = superFore - 1.0,
            MastTopAboveBase = bridgeDeck + 6.0,
            EyeHeightEstimated = true,
            Note = "目標船近似參數(依 LOA/B)",
        };
    }
}
