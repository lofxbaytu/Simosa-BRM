using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.Sensors.Radar;

/// <summary>陸地多邊形(本地 ENU,m)與高度(m;用於雷達地平線)。</summary>
public sealed class LandPolygon
{
    public required string Name { get; init; }
    public required double HeightM { get; init; }
    public required (double X, double Y)[] Points { get; init; }
}

/// <summary>雨區(圓形;中心 ENU m、半徑 m、降雨率 mm/h)。</summary>
public sealed record RainCell(double X, double Y, double RadiusM, double RateMmH);

/// <summary>
/// 雷達環境(radar.schema.json <c>$defs/environment</c>):陸地多邊形、雨區、海況覆寫。
/// 檔案中的座標可為經緯度(points: [[lat, lon], …])或本地 ENU(pointsXY: [[x, y], …]);
/// 經緯度在第一個狀態抵達時以自船位置(lat/lon 與 pos.x/y)為錨點換算為 ENU(平面近似,數十公里內誤差數公尺)。
/// </summary>
public sealed class RadarEnvironment
{
    private readonly List<AreaFile> _files = new();
    private readonly List<RainCellDto> _rainDtos = new();
    private readonly List<LandPolygon> _directLand = new();
    private readonly List<RainCell> _directRain = new();
    private bool _anchored;
    private Func<double, double, (double, double)>? _toXY;

    public List<LandPolygon> Land { get; } = new();
    public List<RainCell> RainCells { get; } = new();
    /// <summary>海況覆寫(Douglas 0–9;null 依真風速推估)</summary>
    public double? SeaStateOverride { get; set; }
    /// <summary>變更序號(雨區/海況變更時遞增,影像產生器據以重算表)</summary>
    public int Version { get; private set; }

    public static RadarEnvironment Empty() => new();

    /// <summary>讀取一個檔案或資料夾內所有 *.json(依檔名排序,確定性)。不存在時回傳空環境。</summary>
    public static RadarEnvironment Load(string? path)
    {
        var env = new RadarEnvironment();
        if (string.IsNullOrEmpty(path)) return env;
        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            : File.Exists(path) ? new[] { path } : Array.Empty<string>();
        foreach (var f in files) env.AddFile(File.ReadAllText(f), Path.GetFileName(f));
        return env;
    }

    public void AddFile(string json, string source = "(json)")
    {
        var dto = JsonSerializer.Deserialize<AreaFile>(json, ReadOptions) ?? throw new InvalidDataException($"無法解析雷達環境檔:{source}");
        _files.Add(dto);
        if (dto.RainCells is { } rc) _rainDtos.AddRange(rc);
        if (dto.SeaState is { } ss) SeaStateOverride = ss;
        _anchored = false;
        Version++;
    }

    /// <summary>以自船位置(經緯度 + ENU)為錨點,把經緯度座標換成 ENU。已錨定時不重算。</summary>
    public void Anchor(double ownLat, double ownLon, double ownX, double ownY)
    {
        if (_anchored) return;
        var ltp = new LocalTangentPlane(ownLat, ownLon);
        (double, double) ToXY(double lat, double lon)
        {
            var (dx, dy) = ltp.ToLocal(lat, lon);
            return (ownX + dx, ownY + dy);
        }
        Land.Clear();
        foreach (var f in _files)
        {
            if (f.Land is null) continue;
            foreach (var p in f.Land)
            {
                (double X, double Y)[] pts;
                if (p.PointsXY is { Length: >= 3 } xy) pts = xy.Select(a => (a[0], a[1])).ToArray();
                else if (p.Points is { Length: >= 3 } ll) pts = ll.Select(a => ToXY(a[0], a[1])).ToArray();
                else continue;
                Land.Add(new LandPolygon { Name = p.Name ?? "land", HeightM = p.HeightM ?? 10.0, Points = pts });
            }
        }
        Land.AddRange(_directLand);
        RainCells.Clear();
        foreach (var r in _rainDtos) RainCells.Add(ToCell(r, ToXY));
        RainCells.AddRange(_directRain);
        _toXY = ToXY;
        _anchored = true;
        Version++;
    }

    /// <summary>解除錨定(情境切換或時間倒退時;下一個狀態以新的自船位置重新換算經緯度座標)。只含 ENU 座標的直接加入項目保留。</summary>
    public void Unanchor()
    {
        _anchored = false;
        _toXY = null;
    }

    /// <summary>陸地多邊形數(含尚未錨定換算的檔案內容)。</summary>
    public int LandPolygonCount => _files.Sum(f => f.Land?.Count ?? 0) + _directLand.Count;

    /// <summary>已以自船位置錨定(經緯度可換算)。</summary>
    public bool Anchored => _anchored;

    /// <summary>以控制指令(教官)設定雨區(取代檔案中的雨區);座標可為 lat/lon 或 x/y。尚未錨定時延到錨定時換算。</summary>
    public void SetRainCells(IEnumerable<RainCellDto> cells)
    {
        _rainDtos.Clear();
        _rainDtos.AddRange(cells);
        _directRain.Clear();
        RainCells.Clear();
        if (_toXY is { } f) foreach (var r in _rainDtos) RainCells.Add(ToCell(r, f));
        else foreach (var r in _rainDtos.Where(c => c.X is not null && c.Y is not null)) RainCells.Add(ToCell(r, (_, _) => (0, 0)));
        Version++;
    }

    public void SetSeaState(double? seaState)
    {
        SeaStateOverride = seaState;
        Version++;
    }

    /// <summary>直接加入 ENU 陸地(測試用)。</summary>
    public void AddLand(LandPolygon polygon)
    {
        _directLand.Add(polygon);
        Land.Add(polygon);
        Version++;
    }

    /// <summary>直接加入 ENU 雨區(測試用)。</summary>
    public void AddRain(RainCell cell)
    {
        _directRain.Add(cell);
        RainCells.Add(cell);
        Version++;
    }

    private static RainCell ToCell(RainCellDto r, Func<double, double, (double, double)> toXY)
    {
        var (x, y) = r.X is { } rx && r.Y is { } ry ? (rx, ry) : toXY(r.Lat ?? 0, r.Lon ?? 0);
        return new RainCell(x, y, Math.Max(1.0, r.RadiusM), Math.Max(0.0, r.RateMmH));
    }

    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class AreaFile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("land")] public List<LandDto>? Land { get; set; }
        [JsonPropertyName("rainCells")] public List<RainCellDto>? RainCells { get; set; }
        [JsonPropertyName("seaState")] public double? SeaState { get; set; }
    }

    private sealed class LandDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("height_m")] public double? HeightM { get; set; }
        [JsonPropertyName("points")] public double[][]? Points { get; set; }
        [JsonPropertyName("pointsXY")] public double[][]? PointsXY { get; set; }
    }
}

/// <summary>雨區(檔案/指令格式):lat/lon 或 x/y、radius_m、rate_mmh。</summary>
public sealed class RainCellDto
{
    [JsonPropertyName("lat")] public double? Lat { get; set; }
    [JsonPropertyName("lon")] public double? Lon { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("radius_m")] public double RadiusM { get; set; } = 1000.0;
    [JsonPropertyName("rate_mmh")] public double RateMmH { get; set; } = 4.0;
}
