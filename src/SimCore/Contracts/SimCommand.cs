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
    /// <summary>目標船控制(args:id、heading/speed 覆寫、add/remove、waypoints、aisOn/aisError、behaviour、colreg、lights/sound、activate、position)</summary>
    TargetControl,
    /// <summary>教官手動觸發時間表項目(args:id、force;情境引擎設計第 5.2 節)</summary>
    TriggerTimeline,
    /// <summary>教官/帶領者記錄口頭辨識、通知機控室等核心偵測不到的動作(args:kind、itemId、role、note)</summary>
    Acknowledge,
    /// <summary>結束練習:凍結並定稿 summary(args:reason)</summary>
    EndExercise,
    /// <summary>查詢目前評估摘要(主控程序直接回覆,不進引擎佇列、不寫 input 行;引擎收到時忽略)</summary>
    GetAssessment,
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
    /// <summary>以物件形式注入故障(args {name, args};第一版引擎只記錄 args)。</summary>
    public static SimCommand InjectFault(string fault, object? faultArgs)
        => new() { Type = SimCommandType.InjectFault, Args = Obj(new { name = fault, args = faultArgs }) };
    /// <summary>手動觸發時間表項目(force = 略過條件/once 限制)。</summary>
    public static SimCommand TriggerTimeline(string id, bool force = false)
        => new() { Type = SimCommandType.TriggerTimeline, Args = Obj(new { id, force }) };
    /// <summary>確認/通知紀錄:kind 為 verbal|notifyEcr|callMaster|report|checklist|steeringMode|anchorReady|other。</summary>
    public static SimCommand Acknowledge(string kind, string? itemId = null, string? role = null, string? note = null)
        => new() { Type = SimCommandType.Acknowledge, Args = Obj(new { kind, itemId, role, note }) };
    public static SimCommand EndExercise(string? reason = null)
        => new() { Type = SimCommandType.EndExercise, Args = Obj(new { reason }) };
    public static SimCommand GetAssessment() => new() { Type = SimCommandType.GetAssessment };

    /// <summary>目標船控制:<paramref name="args"/> 為匿名物件或字典,鍵名見 README「targetControl」。</summary>
    public static SimCommand TargetControl(object args) => new() { Type = SimCommandType.TargetControl, Args = Obj(args) };
    /// <summary>目標船航向/航速覆寫(教官手動接管;release 解除)。</summary>
    public static SimCommand TargetOverride(string id, double? headingDeg = null, double? speedKn = null)
        => TargetControl(new { id, heading = headingDeg, speed = speedKn });
    public static SimCommand TargetRelease(string id) => TargetControl(new { id, release = true });
    public static SimCommand TargetRemove(string id) => TargetControl(new { id, remove = true });
    public static SimCommand TargetAis(string id, bool aisOn) => TargetControl(new { id, aisOn });

    public static SimCommand Autopilot(bool enabled, double? heading = null, double? rotLimit = null)
        => new() { Type = SimCommandType.Autopilot, Args = Obj(new { enabled, heading, rotLimit }) };

    /// <summary>環境設定;<paramref name="visibilityNm"/> 與 <paramref name="rampS"/>(漸變秒數,0 = 立即)為情境引擎新增欄位。</summary>
    public static SimCommand SetEnvironment(double? windTrueSpeedKn = null, double? windTrueDirDeg = null,
        double? currentSetDeg = null, double? currentDriftKn = null, double? waterDepth = null,
        double? visibilityNm = null, double? rampS = null)
    {
        object? wind = windTrueSpeedKn is null && windTrueDirDeg is null ? null : new { trueSpeed = windTrueSpeedKn, trueDir = windTrueDirDeg };
        object? current = currentSetDeg is null && currentDriftKn is null ? null : new { set = currentSetDeg, drift = currentDriftKn };
        return new() { Type = SimCommandType.SetEnvironment, Args = Obj(new { wind, current, waterDepth, visibility_nm = visibilityNm, ramp_s = rampS }) };
    }

    /// <summary>injectFault 的故障名稱:value 字串,或 args.name(物件形式)。</summary>
    public string? FaultName()
    {
        if (Value is { ValueKind: JsonValueKind.String } v) return v.GetString();
        return Arg("name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
    }

    public string? ArgAsString(string name) => Arg(name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

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
