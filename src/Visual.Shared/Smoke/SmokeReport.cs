using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using System.Text.Json.Serialization;

namespace SimosaBRM.Visual.Shared.Smoke;

/// <summary>煙霧測試每幀的量測(build/visual/smoke.json;headless 無 GPU,幀時間只作功能驗證)。simTime_s 為顯示(插值後)時間,sog_kn 為插值後自船速度。</summary>
public sealed record SmokeFrame
{
    [JsonPropertyName("frame")] public required int Frame { get; init; }
    [JsonPropertyName("simTime_s")] public required double SimTime { get; init; }
    [JsonPropertyName("frameTime_ms")] public required double FrameTimeMs { get; init; }
    [JsonPropertyName("nodes")] public required int Nodes { get; init; }
    [JsonPropertyName("targetsDrawn")] public required int TargetsDrawn { get; init; }
    [JsonPropertyName("harbourMeshes")] public required int HarbourMeshes { get; init; }
    [JsonPropertyName("ownEast_m")] public required double OwnEast { get; init; }
    [JsonPropertyName("ownNorth_m")] public required double OwnNorth { get; init; }
    [JsonPropertyName("heading_deg")] public required double Heading { get; init; }
    [JsonPropertyName("sog_kn")] public double SogKn { get; init; }
    [JsonPropertyName("extrapolating")] public bool Extrapolating { get; init; }
    [JsonPropertyName("stalled")] public bool Stalled { get; init; }
    /// <summary>狀態中受力的繫纜數(fast/heaving/slacking;無泊位為 0)</summary>
    [JsonPropertyName("mooringLinesInState")] public int MooringLinesInState { get; init; }
    /// <summary>本幀畫出的繫纜數(找得到繫船柱者)</summary>
    [JsonPropertyName("mooringLinesDrawn")] public int MooringLinesDrawn { get; init; }
}

public sealed record SmokeReport
{
    [JsonPropertyName("generatedUtc")] public required string GeneratedUtc { get; init; }
    [JsonPropertyName("godotVersion")] public string? GodotVersion { get; init; }
    [JsonPropertyName("renderer")] public string? Renderer { get; init; }
    [JsonPropertyName("headless")] public required bool Headless { get; init; }
    [JsonPropertyName("quality")] public required string Quality { get; init; }
    [JsonPropertyName("qualityReason")] public string? QualityReason { get; init; }
    [JsonPropertyName("view")] public required string View { get; init; }
    [JsonPropertyName("areas")] public required IReadOnlyList<string> Areas { get; init; }
    [JsonPropertyName("harbourTriangles")] public int HarbourTriangles { get; init; }
    [JsonPropertyName("ownShipTriangles")] public int OwnShipTriangles { get; init; }
    [JsonPropertyName("frames")] public required IReadOnlyList<SmokeFrame> Frames { get; init; }
    [JsonPropertyName("meanFrameTime_ms")] public required double MeanFrameTimeMs { get; init; }
    [JsonPropertyName("maxFrameTime_ms")] public required double MaxFrameTimeMs { get; init; }
    [JsonPropertyName("qualityChanges")] public IReadOnlyList<string> QualityChanges { get; init; } = Array.Empty<string>();
    /// <summary>ok 判準見 <see cref="Build"/>(與幀數、牆鐘幀距無關)。</summary>
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// ok 判準(審查發現:原「首尾位移 > 1 m」依賴幀數與牆鐘幀距,--smoke=30 必定失敗):
    /// 有幀、每幀節點 > 0、最後幀港口網格 > 0、顯示時間單調不減且有前進(最後 simTime > 第一幀)、無以 "ERROR" 開頭的備註;
    /// 另外當模擬前進 ≥ 0.5 s 時,自船位移必須與平均 SOG × Δt 一致(0.5–1.5 倍)以驗證姿態確有更新;
    /// 最後一幀狀態有受力繫纜時必須有畫出的繫纜(繫船柱找得到;煙霧場景自第 SmokeSequence.BerthSegmentStartFrame 幀起帶示意泊位)。
    /// 判準失敗的原因附在 notes("ok 失敗:…")。
    /// </summary>
    public static SmokeReport Build(IReadOnlyList<SmokeFrame> frames, string quality, string view, IReadOnlyList<string> areas, bool headless, IReadOnlyList<string> notes, string? godotVersion = null, string? renderer = null, string? qualityReason = null, int harbourTriangles = 0, int ownShipTriangles = 0, IReadOnlyList<string>? qualityChanges = null)
    {
        var mean = frames.Count == 0 ? 0 : frames.Average(f => f.FrameTimeMs);
        var max = frames.Count == 0 ? 0 : frames.Max(f => f.FrameTimeMs);
        var failures = new List<string>();
        if (frames.Count == 0) failures.Add("沒有任何幀");
        else
        {
            if (frames.Any(f => f.Nodes <= 0)) failures.Add("有幀的節點數為 0");
            if (frames[^1].HarbourMeshes <= 0) failures.Add("港口網格為 0");
            for (var i = 1; i < frames.Count; i++)
                if (frames[i].SimTime < frames[i - 1].SimTime - 1e-9) { failures.Add($"顯示時間倒退(第 {i} 幀 {frames[i - 1].SimTime:F3} → {frames[i].SimTime:F3} s)"); break; }
            var dt = frames[^1].SimTime - frames[0].SimTime;
            if (dt <= 1e-9) failures.Add("模擬時間沒有前進");
            else if (dt >= 0.5)
            {
                var dist = Math.Sqrt(Math.Pow(frames[^1].OwnEast - frames[0].OwnEast, 2) + Math.Pow(frames[^1].OwnNorth - frames[0].OwnNorth, 2));
                var expected = frames.Average(f => f.SogKn) * Units.MetresPerNauticalMile / 3600.0 * dt;
                if (expected > 0.5 && (dist < expected * 0.5 || dist > expected * 1.5)) failures.Add($"自船位移 {dist:F2} m 與 SOG × Δt = {expected:F2} m 不符");
            }
            if (frames[^1].MooringLinesInState > 0 && frames[^1].MooringLinesDrawn <= 0) failures.Add($"狀態有 {frames[^1].MooringLinesInState} 條受力繫纜但未畫出(繫船柱找不到或節點未更新)");
        }
        if (notes.Any(n => n.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))) failures.Add("備註含 ERROR");
        var allNotes = failures.Count == 0 ? notes : notes.Concat(failures.Select(f => "ok 失敗:" + f)).ToList();
        return new SmokeReport
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            GodotVersion = godotVersion,
            Renderer = renderer,
            Headless = headless,
            Quality = quality,
            QualityReason = qualityReason,
            View = view,
            Areas = areas,
            HarbourTriangles = harbourTriangles,
            OwnShipTriangles = ownShipTriangles,
            Frames = frames,
            MeanFrameTimeMs = mean,
            MaxFrameTimeMs = max,
            QualityChanges = qualityChanges ?? Array.Empty<string>(),
            Ok = failures.Count == 0,
            Notes = allNotes,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }
}
