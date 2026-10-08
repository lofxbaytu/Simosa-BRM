using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Contracts;

/// <summary>指令型別(src/Contracts/command.schema.json 的 type 列舉;JSON 為 camelCase 字串)。</summary>
public enum SimCommandType
{
    Rudder,
    Telegraph,
    Rpm,
    Thruster,
    Autopilot,
    Freeze,
    Resume,
    Reset,
    TimeScale,
    LoadScenario,
    SetEnvironment,
    InjectFault,
    ClearFault,
    Snapshot,
    Restore,
}

/// <summary>
/// 各站送往核心的指令(command.schema.json)。<c>value</c> 與 <c>args</c> 保留為 JsonElement,
/// 由引擎依 type 解讀;JsonElement 不可變,可安全放入跨執行緒佇列與紀錄檔。
/// </summary>
public sealed record SimCommand
{
    [JsonPropertyName("type")] public required SimCommandType Type { get; init; }
    [JsonPropertyName("value")] public JsonElement? Value { get; init; }
    [JsonPropertyName("args")] public JsonElement? Args { get; init; }

    // ---- 便利建構 ----
    public static SimCommand Rudder(double degrees) => new() { Type = SimCommandType.Rudder, Value = Num(degrees) };
    public static SimCommand Telegraph(TelegraphOrder order) => new() { Type = SimCommandType.Telegraph, Value = Str(order.ToString()) };
    public static SimCommand Rpm(double rpm) => new() { Type = SimCommandType.Rpm, Value = Num(rpm) };
    public static SimCommand Thruster(double fraction) => new() { Type = SimCommandType.Thruster, Value = Num(fraction) };
    public static SimCommand Freeze() => new() { Type = SimCommandType.Freeze };
    public static SimCommand Resume() => new() { Type = SimCommandType.Resume };
    public static SimCommand Reset() => new() { Type = SimCommandType.Reset };
    public static SimCommand TimeScale(double scale) => new() { Type = SimCommandType.TimeScale, Value = Num(scale) };
    public static SimCommand Snapshot() => new() { Type = SimCommandType.Snapshot };
    public static SimCommand Restore() => new() { Type = SimCommandType.Restore };
    public static SimCommand InjectFault(string fault) => new() { Type = SimCommandType.InjectFault, Value = Str(fault) };
    public static SimCommand ClearFault(string fault) => new() { Type = SimCommandType.ClearFault, Value = Str(fault) };
    public static SimCommand LoadScenario(string path) => new() { Type = SimCommandType.LoadScenario, Value = Str(path) };

    public static SimCommand Autopilot(bool enabled, double? heading = null, double? rotLimit = null)
        => new() { Type = SimCommandType.Autopilot, Args = Obj(new { enabled, heading, rotLimit }) };

    public static SimCommand SetEnvironment(double? windTrueSpeedKn = null, double? windTrueDirDeg = null,
        double? currentSetDeg = null, double? currentDriftKn = null, double? waterDepth = null)
    {
        object? wind = windTrueSpeedKn is null && windTrueDirDeg is null ? null : new { trueSpeed = windTrueSpeedKn, trueDir = windTrueDirDeg };
        object? current = currentSetDeg is null && currentDriftKn is null ? null : new { set = currentSetDeg, drift = currentDriftKn };
        return new() { Type = SimCommandType.SetEnvironment, Args = Obj(new { wind, current, waterDepth }) };
    }

    // ---- 讀取輔助 ----
    public double? ValueAsDouble() => AsDouble(Value);
    public string? ValueAsString() => Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : Value?.ToString();

    public JsonElement? Arg(string name)
    {
        if (Args is not { ValueKind: JsonValueKind.Object } a) return null;
        return a.TryGetProperty(name, out var e) ? e : null;
    }

    public double? ArgAsDouble(string name) => AsDouble(Arg(name));

    public bool? ArgAsBool(string name) => Arg(name) is { } e
        ? e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;

    public static double? AsDouble(JsonElement? e)
    {
        if (e is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static JsonElement Num(double d) => JsonSerializer.SerializeToElement(d);
    public static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);
    public static JsonElement Obj(object o) => JsonSerializer.SerializeToElement(o, ContractJson.Options);

    public static SimCommand? Parse(string json) => ContractJson.Deserialize<SimCommand>(json);
    public string ToJson() => ContractJson.Serialize(this);
}
