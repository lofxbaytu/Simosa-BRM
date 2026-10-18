using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Sensors.Radar;

/// <summary>場景中的目標船(ENU m、航向弧度、外形與雷達截面積)。</summary>
public readonly record struct SceneTarget(string Id, double X, double Y, double HeadingRad, double Loa, double Beam, double HeightM, double RcsM2);

/// <summary>
/// 一個狀態幀對雷達而言的場景:
/// 回波幾何用真值(state.truth;無真值時用指示值):天線隨船實際轉動,目標與陸地的物理位置為真;
/// ARPA 穩定化(相對 → 真方位、相對 → 地理位置、真向量)用指示值(電羅經航向、GNSS 位置、COG/SOG),
/// 因此電羅經/GNSS 故障會如實傳遞到 ARPA(規劃書第 7.2 節「能力、限制與可能誤差」)。
/// </summary>
public sealed class RadarScene
{
    public double T { get; init; }
    public long Tick { get; init; }
    public string? ScenarioId { get; init; }
    public string ShipId { get; init; } = "";
    /// <summary>船舶參考點(船舯)真值 ENU(m)與真航向(弧度)</summary>
    public double TrueX { get; init; }
    public double TrueY { get; init; }
    public double TrueHeadingRad { get; init; }
    /// <summary>指示值:航向(度)、位置(ENU m 與經緯度)、COG(度)、SOG(kn)</summary>
    public double IndHeadingDeg { get; init; }
    public bool HeadingValid { get; init; } = true;
    public double IndX { get; init; }
    public double IndY { get; init; }
    public double IndLat { get; init; }
    public double IndLon { get; init; }
    public double IndCogDeg { get; init; }
    public double IndSogKn { get; init; }
    public bool PositionValid { get; init; } = true;
    public double WindSpeedKn { get; init; }
    public double WindFromDeg { get; init; }
    public IReadOnlyList<string> Faults { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SceneTarget> Targets { get; init; } = Array.Empty<SceneTarget>();
    /// <summary>原始目標真值(主控程式比對 ARPA 誤差用;學員端不輸出)</summary>
    public IReadOnlyList<TargetState>? TruthTargets { get; init; }
    public LoadingCondition Loading { get; init; } = LoadingCondition.Full;
    public double? DraftMeanM { get; init; }
    /// <summary>情境雷達環境(state.radarEnvironment:雨區與海況;情境未給時 null)</summary>
    public SimCore.Scenario.RadarEnvironmentState? RadarEnvironment { get; init; }

    /// <summary>由 SimCore 狀態廣播建立場景(src/Contracts/state.schema.json)。</summary>
    public static RadarScene FromState(OwnShipState s, RadarBand band)
    {
        var truthPos = s.Truth?.Pos ?? s.Pos;
        var truthHeading = s.Truth?.Heading ?? s.Heading;
        var targets = new List<SceneTarget>(s.Targets?.Count ?? 0);
        if (s.Targets is { } ts)
            foreach (var t in ts)
                targets.Add(new SceneTarget(t.Id, t.Pos.X, t.Pos.Y, Units.DegToRad(t.Heading), Math.Max(1.0, t.Loa), Math.Max(0.5, t.Beam),
                    TargetModel.HeightM(t.ShipType, t.Loa), TargetModel.RcsM2(band, t.ShipType, t.Loa, t.Beam, t.Draft)));
        return new RadarScene
        {
            T = s.T,
            Tick = s.Tick,
            ScenarioId = s.Scenario?.Id,
            ShipId = s.ShipId,
            TrueX = truthPos.X,
            TrueY = truthPos.Y,
            TrueHeadingRad = Units.DegToRad(truthHeading),
            IndHeadingDeg = s.Heading,
            HeadingValid = s.Sensors?.HeadingValid ?? true,
            IndX = s.Pos.X,
            IndY = s.Pos.Y,
            IndLat = s.Pos.Lat,
            IndLon = s.Pos.Lon,
            IndCogDeg = s.Cog,
            IndSogKn = s.Sog,
            PositionValid = s.Sensors?.PositionValid ?? true,
            WindSpeedKn = s.Wind.TrueSpeed,
            WindFromDeg = s.Wind.TrueDir,
            Faults = s.Faults ?? Array.Empty<string>(),
            Targets = targets,
            TruthTargets = s.Targets,
            Loading = s.Loading,
            DraftMeanM = s.Draft is { } d ? (d.Fore + d.Aft) / 2.0 : null,
            RadarEnvironment = s.RadarEnvironment,
        };
    }
}

/// <summary>目標船雷達特性近似(BRM 可信度等級,規劃書 D5)。</summary>
public static class TargetModel
{
    /// <summary>
    /// 雷達截面積(m²):Skolnik 船舶經驗式 σ = 52 √f(GHz) · D(千噸)^1.5,排水量以 L·B·T·Cb·1.025 估計(Cb 依船型);
    /// 小船下限 5 m²(含雷達反射器)、上限 10⁶ m²。吃水未知時取 0.06 L。
    /// </summary>
    public static double RcsM2(RadarBand band, string shipType, double loa, double beam, double? draft)
    {
        var f = band == RadarBand.X ? 9.41 : 3.05;
        var t = draft is > 0 ? draft.Value : 0.06 * loa;
        var cb = shipType.ToLowerInvariant() switch
        {
            "tanker" or "bulk" or "cargo" => 0.75,
            "container" or "passenger" => 0.62,
            _ => 0.5,
        };
        var dispKt = loa * beam * t * cb * 1.025 / 1000.0;
        var sigma = 52.0 * Math.Sqrt(f) * Math.Pow(Math.Max(dispKt, 0.0), 1.5);
        return Math.Clamp(sigma, 5.0, 1e6);
    }

    /// <summary>目標上層結構高度(m,用於雷達地平線):大船約 0.12 L + 5、小船(漁船/引水船/拖船)約 0.25 L,範圍 3–50 m。</summary>
    public static double HeightM(string shipType, double loa)
    {
        var small = shipType.ToLowerInvariant() is "fishing" or "pilot" or "tug" or "sailing" or "pleasure";
        return Math.Clamp(small ? 0.25 * loa + 1.0 : 0.12 * loa + 5.0, 3.0, 50.0);
    }
}
