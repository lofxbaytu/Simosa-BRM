using System.Text.Json;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.Visual.Shared.Area;

/// <summary>ENU 平面點(m)。</summary>
public readonly record struct EnuPoint(double East, double North);

/// <summary>擠出的多邊形(陸地、防波堤、淺灘、碼頭區);高度為海平面以上(m),淺灘為負值(水深)。</summary>
public sealed record ExtrudedPolygon(string Kind, string Name, IReadOnlyList<EnuPoint> Points, double HeightM, double? DepthM = null);

/// <summary>碼頭線(從 quay[0] 到 quay[1];waterSide 表示水在線的左/右側)、碰墊與繫船柱的 ENU 位置。</summary>
public sealed record BerthGeometry(string Id, string Name, IReadOnlyList<EnuPoint> Quay, string WaterSide, IReadOnlyList<(string Id, EnuPoint Pos)> Fenders, IReadOnlyList<(string Id, EnuPoint Pos)> Bollards);

/// <summary>導燈(前/後、方位)。</summary>
public sealed record LeadingLightsGeometry(double BearingDeg, EnuPoint Front, EnuPoint Rear);

/// <summary>點狀物(引水站、防波堤頭、錨位、航點)。</summary>
public sealed record MarkerGeometry(string Kind, string Name, EnuPoint Pos);

/// <summary>
/// 一個區域檔(data/areas/*.json)投影到情境 ENU 後的幾何:視景只用來畫低面數港口(示意),不做任何物理或評估。
/// 區域檔格式不一(land.points 為 [lat, lon] 陣列、其他為 {lat, lon} 物件),載入器寬鬆處理;缺的區段視為空。
/// </summary>
public sealed class AreaGeometry
{
    public required string Id { get; init; }
    public required string SourceFile { get; init; }
    public IReadOnlyList<ExtrudedPolygon> Land { get; init; } = Array.Empty<ExtrudedPolygon>();
    public IReadOnlyList<ExtrudedPolygon> Breakwaters { get; init; } = Array.Empty<ExtrudedPolygon>();
    public IReadOnlyList<ExtrudedPolygon> Shoals { get; init; } = Array.Empty<ExtrudedPolygon>();
    /// <summary>航道/分道/錨地等水域多邊形(只用於鳥瞰視角的示意線)</summary>
    public IReadOnlyList<ExtrudedPolygon> WaterZones { get; init; } = Array.Empty<ExtrudedPolygon>();
    public IReadOnlyList<BerthGeometry> Berths { get; init; } = Array.Empty<BerthGeometry>();
    public LeadingLightsGeometry? LeadingLights { get; init; }
    public IReadOnlyList<MarkerGeometry> Markers { get; init; } = Array.Empty<MarkerGeometry>();

    public int PolygonCount => Land.Count + Breakwaters.Count + Shoals.Count + WaterZones.Count;

    /// <summary>所有幾何的 ENU 外框(空時為 null)。</summary>
    public (EnuPoint Min, EnuPoint Max)? Bounds()
    {
        double minE = double.PositiveInfinity, minN = double.PositiveInfinity, maxE = double.NegativeInfinity, maxN = double.NegativeInfinity;
        void Add(EnuPoint p)
        {
            minE = Math.Min(minE, p.East); minN = Math.Min(minN, p.North);
            maxE = Math.Max(maxE, p.East); maxN = Math.Max(maxN, p.North);
        }
        foreach (var poly in Land.Concat(Breakwaters).Concat(Shoals).Concat(WaterZones)) foreach (var p in poly.Points) Add(p);
        foreach (var b in Berths) foreach (var p in b.Quay) Add(p);
        foreach (var m in Markers) Add(m.Pos);
        return double.IsInfinity(minE) ? null : (new EnuPoint(minE, minN), new EnuPoint(maxE, maxN));
    }
}

/// <summary>載入 data/areas/*.json 並以情境原點投影為 ENU。</summary>
public static class AreaLoader
{
    public const double DefaultBreakwaterHeightM = 5.0;
    public const double DefaultLandHeightM = 10.0;
    public const double DefaultQuayHeightM = 3.0;

    public static AreaGeometry Load(string path, LocalTangentPlane origin)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return FromJson(doc.RootElement, origin, path);
    }

    public static AreaGeometry FromJson(JsonElement root, LocalTangentPlane origin, string sourceFile = "(json)")
    {
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : Path.GetFileNameWithoutExtension(sourceFile);
        var land = new List<ExtrudedPolygon>();
        var breakwaters = new List<ExtrudedPolygon>();
        var shoals = new List<ExtrudedPolygon>();
        var zones = new List<ExtrudedPolygon>();
        var berths = new List<BerthGeometry>();
        var markers = new List<MarkerGeometry>();
        LeadingLightsGeometry? leading = null;

        if (root.TryGetProperty("land", out var landEl) && landEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in landEl.EnumerateArray())
            {
                var pts = ReadPoints(l, "points", origin) ?? ReadPoints(l, "polygon", origin);
                if (pts is null || pts.Count < 3) continue;
                land.Add(new ExtrudedPolygon("land", Name(l), pts, ReadDouble(l, "height_m") ?? DefaultLandHeightM));
            }
        }
        if (root.TryGetProperty("breakwaters", out var bwEl) && bwEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bwEl.EnumerateArray())
            {
                var pts = ReadPoints(b, "polygon", origin);
                if (pts is null || pts.Count < 3) continue;
                breakwaters.Add(new ExtrudedPolygon("breakwater", Name(b), pts, ReadDouble(b, "height_m") ?? DefaultBreakwaterHeightM, ReadDouble(b, "depth_m")));
                if (b.TryGetProperty("head", out var head) && TryReadLatLon(head, origin, out var hp))
                    markers.Add(new MarkerGeometry("breakwaterHead", Name(b) + " 堤頭", hp));
            }
        }
        if (root.TryGetProperty("shoals", out var shEl) && shEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in shEl.EnumerateArray())
            {
                var pts = ReadPoints(s, "polygon", origin);
                if (pts is null || pts.Count < 3) continue;
                var depth = ReadDouble(s, "depth_m") ?? 5.0;
                shoals.Add(new ExtrudedPolygon("shoal", Name(s), pts, -depth, depth));
            }
        }
        foreach (var zoneKey in new[] { "channel", "fairway", "holdingArea", "berthSpeedZone" })
        {
            if (root.TryGetProperty(zoneKey, out var z) && z.ValueKind == JsonValueKind.Object)
            {
                var pts = ReadPoints(z, "polygon", origin);
                if (pts is not null && pts.Count >= 3) zones.Add(new ExtrudedPolygon(zoneKey, Name(z, zoneKey), pts, 0.0, ReadDouble(z, "depth_m")));
            }
        }
        foreach (var listKey in new[] { "anchorages", "speedLimitZones" })
        {
            if (root.TryGetProperty(listKey, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var z in arr.EnumerateArray())
                {
                    var pts = ReadPoints(z, "polygon", origin);
                    if (pts is not null && pts.Count >= 3) zones.Add(new ExtrudedPolygon(listKey, Name(z, listKey), pts, 0.0, ReadDouble(z, "waterDepth_m")));
                }
        }
        if (root.TryGetProperty("lanes", out var lanes) && lanes.ValueKind == JsonValueKind.Object)
        {
            foreach (var lane in lanes.EnumerateObject())
            {
                var pts = ReadPoints(lane.Value, "polygon", origin);
                if (pts is not null && pts.Count >= 3) zones.Add(new ExtrudedPolygon("lane:" + lane.Name, Name(lane.Value, lane.Name), pts, 0.0));
            }
        }
        if (root.TryGetProperty("berths", out var berthEl) && berthEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in berthEl.EnumerateArray())
            {
                var quay = ReadPoints(b, "quay", origin);
                if (quay is null || quay.Count < 2) continue;
                var waterSide = b.TryGetProperty("waterSide", out var ws) && ws.ValueKind == JsonValueKind.String ? ws.GetString()! : "right";
                var fenders = new List<(string, EnuPoint)>();
                var bollards = new List<(string, EnuPoint)>();
                if (b.TryGetProperty("fenders", out var fs) && fs.ValueKind == JsonValueKind.Array)
                    foreach (var f in fs.EnumerateArray())
                        fenders.Add((Id(f), AlongQuay(quay, waterSide, ReadDouble(f, "chainage_m") ?? 0, 0.0)));
                if (b.TryGetProperty("bollards", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var bo in bs.EnumerateArray())
                        bollards.Add((Id(bo), AlongQuay(quay, waterSide, ReadDouble(bo, "chainage_m") ?? 0, -(ReadDouble(bo, "setback_m") ?? 2.0))));
                berths.Add(new BerthGeometry(Id(b), Name(b), quay, waterSide, fenders, bollards));
            }
        }
        if (root.TryGetProperty("leadingLights", out var ll) && ll.ValueKind == JsonValueKind.Object
            && ll.TryGetProperty("front", out var fr) && ll.TryGetProperty("rear", out var re)
            && TryReadLatLon(fr, origin, out var fp) && TryReadLatLon(re, origin, out var rp))
        {
            leading = new LeadingLightsGeometry(ReadDouble(ll, "bearing_deg") ?? 0, fp, rp);
        }
        foreach (var key in new[] { "pilotStations", "harbourEntrances" })
        {
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (TryReadLatLon(m, origin, out var mp)) markers.Add(new MarkerGeometry(key, Name(m), mp));
        }
        if (root.TryGetProperty("harbourEntrance", out var he) && TryReadLatLon(he, origin, out var hep))
            markers.Add(new MarkerGeometry("harbourEntrance", Name(he, "口門"), hep));
        if (root.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.Array)
            foreach (var wp in route.EnumerateArray())
                if (TryReadLatLon(wp, origin, out var wpp)) markers.Add(new MarkerGeometry("waypoint", Name(wp), wpp));

        return new AreaGeometry
        {
            Id = id,
            SourceFile = sourceFile,
            Land = land,
            Breakwaters = breakwaters,
            Shoals = shoals,
            WaterZones = zones,
            Berths = berths,
            LeadingLights = leading,
            Markers = markers,
        };
    }

    /// <summary>沿碼頭線 chainage(m,自 quay[0] 起)處、往水側偏 offset(負值 = 往陸側)的 ENU 位置。</summary>
    public static EnuPoint AlongQuay(IReadOnlyList<EnuPoint> quay, string waterSide, double chainage, double offsetTowardsWater)
    {
        var a = quay[0];
        var b = quay[^1];
        var dx = b.East - a.East;
        var dy = b.North - a.North;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return a;
        var ux = dx / len;
        var uy = dy / len;
        // 沿行進方向的右側法向量 (uy, −ux);waterSide right 表示水在右側
        var sign = string.Equals(waterSide, "left", StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;
        var nx = uy * sign;
        var ny = -ux * sign;
        return new EnuPoint(a.East + ux * chainage + nx * offsetTowardsWater, a.North + uy * chainage + ny * offsetTowardsWater);
    }

    private static string Name(JsonElement e, string fallback = "")
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : (Id(e) is { Length: > 0 } i ? i : fallback);

    private static string Id(JsonElement e)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";

    private static double? ReadDouble(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool TryReadLatLon(JsonElement e, LocalTangentPlane origin, out EnuPoint p)
    {
        p = default;
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2 && e[0].ValueKind == JsonValueKind.Number && e[1].ValueKind == JsonValueKind.Number)
        {
            var (x, y) = origin.ToLocal(e[0].GetDouble(), e[1].GetDouble());
            p = new EnuPoint(x, y);
            return true;
        }
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (e.TryGetProperty("lat", out var lat) && e.TryGetProperty("lon", out var lon) && lat.ValueKind == JsonValueKind.Number && lon.ValueKind == JsonValueKind.Number)
        {
            var (x, y) = origin.ToLocal(lat.GetDouble(), lon.GetDouble());
            p = new EnuPoint(x, y);
            return true;
        }
        if (e.TryGetProperty("x", out var ex) && e.TryGetProperty("y", out var ey) && ex.ValueKind == JsonValueKind.Number && ey.ValueKind == JsonValueKind.Number)
        {
            p = new EnuPoint(ex.GetDouble(), ey.GetDouble());
            return true;
        }
        return false;
    }

    private static List<EnuPoint>? ReadPoints(JsonElement e, string key, LocalTangentPlane origin)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<EnuPoint>();
        foreach (var item in arr.EnumerateArray())
            if (TryReadLatLon(item, origin, out var p)) list.Add(p);
        // 去掉與首點重複的尾點
        if (list.Count > 1 && Math.Abs(list[0].East - list[^1].East) < 1e-6 && Math.Abs(list[0].North - list[^1].North) < 1e-6) list.RemoveAt(list.Count - 1);
        return list;
    }
}
