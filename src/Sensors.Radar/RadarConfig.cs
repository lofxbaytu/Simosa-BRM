using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Sensors.Radar;

/// <summary>雷達頻帶(規劃書第 7.2 節:X 頻雷達 + 第二台 S 頻雷達)。</summary>
public enum RadarBand
{
    X,
    S,
}

/// <summary>陰影扇區(相對艏向,度;自 From 順時針至 To;天線位置與艏樓/煙囪/桅杆遮蔽)。</summary>
public sealed record ShadowSector(double FromRelDeg, double ToRelDeg, double AttenuationDb = 60.0)
{
    /// <summary>相對方位(度)是否位於扇區內(處理跨 0° 的扇區)。</summary>
    public bool Contains(double relDeg)
    {
        var a = Units.NormalizeHeadingDeg(relDeg);
        var f = Units.NormalizeHeadingDeg(FromRelDeg);
        var t = Units.NormalizeHeadingDeg(ToRelDeg);
        return f <= t ? a >= f && a <= t : a >= f || a <= t;
    }
}

/// <summary>
/// ARPA 追蹤參數(MSC.192(79) 第 5.32 節:至少 30 個追蹤目標、1 分鐘內趨勢、3 分鐘內預測運動達規定精度)。
/// 距離 m、時間 s;對外訊息才轉成 nm/分。
/// </summary>
public sealed class ArpaConfig
{
    /// <summary>ARPA 偵測門檻(增益門檻以上 dB)</summary>
    public double DetectThresholdDb { get; set; } = 7.5;
    /// <summary>ARPA 處理影像的動態範圍(dB;增益門檻以上對應 0–255)。較顯示影像寬,使強回波的方位/距離剖面不飽和、質心有次格精度</summary>
    public double DynamicRangeDb { get; set; } = 80.0;
    /// <summary>ARPA 偵測門檻(ARPA 影像強度 0–255)</summary>
    public int DetectThreshold => (int)Math.Ceiling(DetectThresholdDb * 255.0 / DynamicRangeDb);
    /// <summary>偵測最少格數與最少方位線數(低於者視為雜訊/雜波斑點)</summary>
    public int MinCells { get; set; } = 3;
    public int MinSpokes { get; set; } = 2;
    /// <summary>回波徑向或橫向延伸超過此值(m)視為陸地/雨區等大面積回波,不擷取</summary>
    public double LargeExtentM { get; set; } = 800.0;
    /// <summary>最多追蹤目標數(MSC.192(79) 500–10,000 總噸等級至少 30)</summary>
    public int MaxTracks { get; set; } = 40;
    /// <summary>手動擷取的搜尋半徑(m)</summary>
    public double AcquireGateM { get; set; } = 300.0;
    /// <summary>追蹤閘門:Mahalanobis 平方距離上限,以及最小/最大歐氏半徑(m)</summary>
    public double GateChi2 { get; set; } = 16.0;
    public double MinGateM { get; set; } = 40.0;
    public double MaxGateM { get; set; } = 600.0;
    /// <summary>連續漏掃描次數達此值即失追(lost)</summary>
    public int LostMisses { get; set; } = 6;
    /// <summary>擷取中連續漏掃描次數達此值即擷取失敗</summary>
    public int AcquireMisses { get; set; } = 3;
    /// <summary>擷取後至顯示向量(趨勢)的秒數</summary>
    public double TrendTimeS { get; set; } = 60.0;
    /// <summary>擷取後至穩定追蹤(規定精度)的秒數</summary>
    public double StableTimeS { get; set; } = 180.0;
    /// <summary>過去位置取樣間隔(s)與點數</summary>
    public double PastIntervalS { get; set; } = 30.0;
    public int PastCount { get; set; } = 10;
    /// <summary>失追目標保留顯示的秒數</summary>
    public double LostHoldS { get; set; } = 60.0;
    /// <summary>卡爾曼濾波器白加速度過程雜訊(m²/s³):穩定與操船中</summary>
    public double ProcessNoise { get; set; } = 2e-4;
    public double ManoeuvreNoise { get; set; } = 0.5;
    /// <summary>並行的快速濾波器過程雜訊(m²/s³):緩慢轉向時精確濾波器的平均 NIS 明顯高於快速濾波器即判定操船並切換</summary>
    public double FastNoise { get; set; } = 0.02;
    /// <summary>NIS 超過此值(2 自由度)連續 2 次判定目標操船</summary>
    public double ManoeuvreNis { get; set; } = 9.2;
    /// <summary>量測方位標準差(度)</summary>
    public double SigmaBearingDeg { get; set; } = 0.03;
    /// <summary>保護區候選:M-of-N 擷取(連續掃描中命中 M 次)與候選閘門(m)</summary>
    public int GuardHits { get; set; } = 5;
    public int GuardWindowScans { get; set; } = 7;
    public double GuardGateM { get; set; } = 150.0;
    /// <summary>保護區候選第 3 次起的命中須落在等速外推位置的此半徑內(m;排除海雜波斑點)</summary>
    public double GuardPredictGateM { get; set; } = 50.0;
    /// <summary>自動擷取的最少回波格數(雜波斑點通常較小)</summary>
    public int AutoMinCells { get; set; } = 10;
    /// <summary>候選目標最大速度(kn)</summary>
    public double GuardMaxSpeedKn { get; set; } = 40.0;
    /// <summary>危險目標判定(CPA/TCPA 限值;可由控制指令變更)</summary>
    public double CpaLimitNm { get; set; } = 1.0;
    public double TcpaLimitMin { get; set; } = 12.0;
    /// <summary>兩追蹤目標距離小於此值(m)時標示交換風險</summary>
    public double SwapRiskM { get; set; } = 250.0;

    // AIS 融合(IEC 62288 / IEC 62388 第 11.4 節的關聯概念;docs/design/radar-arpa.md 第 3.1 節)
    /// <summary>關聯距離門檻(m):|ARPA 預測位置 − AIS 報告位置| ≤ AisGateM + AisGateFraction × 距離</summary>
    public double AisGateM { get; set; } = 150.0;
    public double AisGateFraction { get; set; } = 0.02;
    /// <summary>已關聯者的保持門檻倍數(超過 AisGate × 此倍數連續 AisMisses 次才解除;期間 positionOffset_m 顯示差異)</summary>
    public double AisHoldGateFactor { get; set; } = 2.0;
    /// <summary>航向/速度一致性(兩者 SOG 皆 ≥ AisMinSpeedKn 時才檢查)</summary>
    public double AisCourseDeg { get; set; } = 30.0;
    public double AisSpeedKn { get; set; } = 3.0;
    public double AisMinSpeedKn { get; set; } = 2.0;
    /// <summary>連續 N 次(追蹤清單週期 1 Hz)同一 MMSI 通過門檻才判定關聯;連續 M 次失敗才解除</summary>
    public int AisHits { get; set; } = 3;
    public int AisMisses { get; set; } = 3;
}

/// <summary>
/// 單台雷達的固定參數(規劃書第 5.2 節 Radar、第 7.2 節雷達列)。天線高度為水線上(m);天線位置相對船舶參考點(船舯,與 SimCore 相同)。
/// ARPA 量測以船舶參考點為共同參考點(CCRP),使 CPA/TCPA 與核心真值同一定義。
/// </summary>
public sealed class RadarConfig
{
    public required string Id { get; init; }
    public required RadarBand Band { get; init; }
    public double FrequencyGHz { get; init; }
    /// <summary>水平波束寬(單程 −3 dB,度):X 頻約 1°、S 頻約 2°</summary>
    public double BeamwidthDeg { get; init; }
    public double Rpm { get; set; } = 24.0;
    /// <summary>每圈方位線數</summary>
    public int Spokes { get; init; } = 2048;
    /// <summary>顯示影像每線距離格數</summary>
    public int DisplayCells { get; set; } = 512;
    /// <summary>ARPA 處理影像:固定量程與格數(與顯示量程無關,換量程不影響追蹤)</summary>
    public double ArpaRangeNm { get; set; } = 16.0;
    public int ArpaCells { get; set; } = 2048;
    /// <summary>ARPA 處理用脈衝長度(m,cτ/2;中脈衝)</summary>
    public double ArpaPulseM { get; set; } = 45.0;
    /// <summary>天線水線上高度(m)</summary>
    public double AntennaHeightM { get; set; } = 20.0;
    /// <summary>天線相對船舯:向艏(m)、向右舷(m)</summary>
    public double AntennaFwdM { get; set; }
    public double AntennaStbdM { get; set; }
    public List<ShadowSector> ShadowSectors { get; set; } = new();
    /// <summary>系統常數(dB):σ = 1 m²、距離 1 nm 的目標訊雜比(雷達方程式 SNR = 10log σ − 40log R_nm + K)</summary>
    public double SystemConstantDb { get; init; }
    /// <summary>海雜波相對 X 頻的偏移(dB;S 頻約 −10)</summary>
    public double SeaClutterOffsetDb { get; init; }
    /// <summary>雨回波相對 X 頻的偏移(dB;S 頻約 −19.5,∝ f⁴)</summary>
    public double RainOffsetDb { get; init; }
    /// <summary>雨衰減(單程 dB/km = k·R^α)</summary>
    public double RainAttenK { get; init; }
    public double RainAttenAlpha { get; init; }
    /// <summary>性能劣化時的靈敏度損失(dB)</summary>
    public double DegradedLossDb { get; set; } = 15.0;
    /// <summary>使本台停止發射的故障名稱(state.faults[];只讀)</summary>
    public List<string> FailureFaults { get; set; } = new();
    /// <summary>使本台靈敏度下降的故障名稱</summary>
    public List<string> DegradedFaults { get; set; } = new();
    /// <summary>每個影像幀的方位線數</summary>
    public int BatchSpokes { get; set; } = 32;
    /// <summary>追蹤清單輸出間隔(模擬秒)</summary>
    public double TrackIntervalS { get; set; } = 1.0;
    public ArpaConfig Arpa { get; set; } = new();

    public double ScanPeriodS => 60.0 / Rpm;
    public double SpokeAngleRad => Units.TwoPi / Spokes;

    /// <summary>X 頻(9.41 GHz、波束 1.0°)預設值。</summary>
    public static RadarConfig XBand(double antennaHeightM = 20.0) => new()
    {
        Id = "x",
        Band = RadarBand.X,
        FrequencyGHz = 9.41,
        BeamwidthDeg = 1.0,
        AntennaHeightM = antennaHeightM,
        SystemConstantDb = 39.0,
        SeaClutterOffsetDb = 0.0,
        RainOffsetDb = 0.0,
        RainAttenK = 0.0101,
        RainAttenAlpha = 1.276,
        ShadowSectors = { new ShadowSector(174.0, 186.0) },
        FailureFaults = { "radarFailure", "radarFailureX", "blackout" },
        DegradedFaults = { "radarDegraded", "radarDegradedX" },
    };

    /// <summary>S 頻(3.05 GHz、波束 2.0°)預設值。</summary>
    public static RadarConfig SBand(double antennaHeightM = 18.0) => new()
    {
        Id = "s",
        Band = RadarBand.S,
        FrequencyGHz = 3.05,
        BeamwidthDeg = 2.0,
        AntennaHeightM = antennaHeightM,
        SystemConstantDb = 38.0,
        SeaClutterOffsetDb = -10.0,
        RainOffsetDb = -19.5,
        RainAttenK = 0.00035,
        RainAttenAlpha = 0.9,
        ShadowSectors = { new ShadowSector(172.0, 188.0) },
        FailureFaults = { "radarFailureS", "blackout" },
        DegradedFaults = { "radarDegradedS" },
    };

    /// <summary>
    /// 由船舶資料設定天線高度與位置:水線上高度 = bridge.mastTopAboveBase_m − 天線低於桅頂距離 − 平均吃水;
    /// 天線縱向位置預設在駕駛台頂(船舯後 0.30 LOA)。資料缺少時保留預設值。
    /// </summary>
    public RadarConfig ApplyParticulars(ShipParticulars ship, LoadingCondition loading, double? draftMeanM = null)
    {
        var draft = draftMeanM ?? ship.GetLoading(loading).DraftMean_m ?? ship.GetLoading(loading).MaxDraft_m;
        double? mastTop = null;
        if (ship.Extra is { } extra && extra.TryGetValue("bridge", out var bridge) && bridge.ValueKind == JsonValueKind.Object
            && bridge.TryGetProperty("mastTopAboveBase_m", out var mt) && mt.ValueKind == JsonValueKind.Number)
            mastTop = mt.GetDouble();
        if (mastTop is { } top)
        {
            var below = Band == RadarBand.X ? 1.0 : 3.0;
            AntennaHeightM = Math.Max(3.0, top - below - draft);
        }
        var loa = ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : ship.Hull.LengthBetweenPerpendiculars_m;
        AntennaFwdM = -(Band == RadarBand.X ? 0.30 : 0.32) * loa;
        return this;
    }

    /// <summary>天線至海面的雷達地平線(nm;4/3 等效地球半徑,d = 2.21(√h₁ + √h₂))。</summary>
    public static double HorizonNm(double antennaHeightM, double targetHeightM)
        => 2.21 * (Math.Sqrt(Math.Max(0.0, antennaHeightM)) + Math.Sqrt(Math.Max(0.0, targetHeightM)));

    /// <summary>距離 rangeNm 處被地球曲率遮住的高度(m)。</summary>
    public static double HiddenHeightM(double antennaHeightM, double rangeNm)
    {
        var d = rangeNm / 2.21 - Math.Sqrt(Math.Max(0.0, antennaHeightM));
        return d <= 0 ? 0.0 : d * d;
    }
}
