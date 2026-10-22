using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.Visual.Shared.Smoke;

/// <summary>煙霧測試每幀的量測(build/visual/smoke.json;headless 無 GPU,幀時間只作功能驗證)。</summary>
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
    [JsonPropertyName("extrapolating")] public bool Extrapolating { get; init; }
    [JsonPropertyName("stalled")] public bool Stalled { get; init; }
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
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static SmokeReport Build(IReadOnlyList<SmokeFrame> frames, string quality, string view, IReadOnlyList<string> areas, bool headless, IReadOnlyList<string> notes, string? godotVersion = null, string? renderer = null, string? qualityReason = null, int harbourTriangles = 0, int ownShipTriangles = 0, IReadOnlyList<string>? qualityChanges = null)
    {
        var mean = frames.Count == 0 ? 0 : frames.Average(f => f.FrameTimeMs);
        var max = frames.Count == 0 ? 0 : frames.Max(f => f.FrameTimeMs);
        var moved = frames.Count >= 2 && (Math.Abs(frames[^1].OwnEast - frames[0].OwnEast) > 1.0 || Math.Abs(frames[^1].OwnNorth - frames[0].OwnNorth) > 1.0);
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
            Ok = frames.Count > 0 && moved && frames.All(f => f.Nodes > 0),
            Notes = notes,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }
}
