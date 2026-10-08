using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 情境(data/scenarios/*.yaml;JSON Schema 見 src/Contracts/scenario.schema.json,與 Python 側共用)。
/// 最小內容:自船 ID 與裝載、初始位置/航向/速度、環境、亂數種子。
/// 屬性同時供 YamlDotNet(camelCase 命名)與 System.Text.Json(紀錄檔標頭)序列化。
/// </summary>
public sealed class Scenario
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>亂數種子(所有亂數由 Random(seed) 產生,CLAUDE.md 確定性要求)</summary>
    public int Seed { get; set; } = 1;
    /// <summary>練習開始的 UTC 時刻(ISO 8601),供 NMEA 時間欄位;未指定時主控程序以實際時鐘代入</summary>
    public string? StartTimeUtc { get; set; }
    /// <summary>初始時間倍率(1 = 即時)</summary>
    public double TimeScale { get; set; } = 1.0;
    public ScenarioShip Ship { get; set; } = new();
    /// <summary>本地 ENU 原點;未指定時以初始位置的經緯度為原點</summary>
    public GeoPoint? Origin { get; set; }
    public ScenarioInitial Initial { get; set; } = new();
    public ScenarioEnvironment Environment { get; set; } = new();

    /// <summary>檢查必要欄位並補齊原點;回傳自身以便串接。</summary>
    public Scenario Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("情境缺少 id");
        if (string.IsNullOrWhiteSpace(Ship.Id)) throw new InvalidDataException("情境缺少 ship.id");
        var pos = Initial.Position ?? throw new InvalidDataException("情境缺少 initial.position");
        var hasGeo = pos.Lat is not null && pos.Lon is not null;
        var hasLocal = pos.X is not null && pos.Y is not null;
        if (!hasGeo && !hasLocal) throw new InvalidDataException("initial.position 需為 {lat, lon} 或 {x, y}");
        if (Origin is null)
        {
            if (!hasGeo) throw new InvalidDataException("initial.position 以 {x, y} 指定時必須提供 origin {lat, lon}");
            Origin = new GeoPoint { Lat = pos.Lat!.Value, Lon = pos.Lon!.Value };
        }
        if (Environment.WaterDepth <= 0) throw new InvalidDataException("environment.waterDepth 必須為正");
        return this;
    }

    public string ToJson() => ContractJson.Serialize(this);
    public static Scenario FromJson(string json)
        => (ContractJson.Deserialize<Scenario>(json) ?? throw new InvalidDataException("無法解析情境 JSON")).Validate();
}

public sealed class ScenarioShip
{
    /// <summary>FSB1 / FSB2</summary>
    public string Id { get; set; } = "FSB1";
    public LoadingCondition Loading { get; set; } = LoadingCondition.Ballast;
}

public sealed class GeoPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
}

/// <summary>初始位置:經緯度(度)或本地 ENU(m),二擇一。</summary>
public sealed class ScenarioPosition
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
}

public sealed class ScenarioInitial
{
    public ScenarioPosition? Position { get; set; }
    /// <summary>航向(度,0–360)</summary>
    public double Heading { get; set; }
    /// <summary>對水速度(kn)</summary>
    public double Speed { get; set; }
    /// <summary>初始車鐘代號(EFAS…NAVF);未指定時取航速最接近的前進車令(0 kn → STOP)</summary>
    public string? Telegraph { get; set; }
    /// <summary>初始舵角(度)</summary>
    public double Rudder { get; set; }
}

public sealed class ScenarioEnvironment
{
    public ScenarioWind Wind { get; set; } = new();
    public ScenarioCurrent Current { get; set; } = new();
    /// <summary>水深(m,常數;日後可改為水深網格參照)</summary>
    public double WaterDepth { get; set; } = 50.0;
}

public sealed class ScenarioWind
{
    /// <summary>真風速(kn)</summary>
    public double TrueSpeed { get; set; }
    /// <summary>真風來向(度)</summary>
    public double TrueDir { get; set; }
    /// <summary>陣風強度(0–1,每秒以種子亂數更新 ±gustiness 的係數)</summary>
    public double Gustiness { get; set; }
}

public sealed class ScenarioCurrent
{
    /// <summary>流向(去向,度)</summary>
    public double Set { get; set; }
    /// <summary>流速(kn)</summary>
    public double Drift { get; set; }
}
