using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>時間表項目(scenario.schema.json <c>$defs.timelineItem</c>;情境引擎設計第 1.1 節)。</summary>
public sealed class TimelineItem
{
    /// <summary>情境內唯一;事件、手動觸發、評估規則以此引用</summary>
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>觸發時把情境階段設為此值(normal → degraded → emergency,規劃書第 8.1 節三步升級)</summary>
    public string? Phase { get; set; }
    /// <summary>instructor(預設)| all:all 時事件以 visible=true 廣播</summary>
    public TimelineVisibility Visibility { get; set; } = TimelineVisibility.Instructor;
    public RepeatSpec Repeat { get; set; } = RepeatSpec.Once;
    /// <summary>repeat 非 once 時兩次觸發的最短間隔(秒)</summary>
    [JsonPropertyName("cooldown_s")] public double CooldownS { get; set; }
    /// <summary>條件需連續成立多少秒才觸發</summary>
    [JsonPropertyName("holdFor_s")] public double HoldForS { get; set; }
    /// <summary>觸發後延遲多少秒執行動作</summary>
    [JsonPropertyName("delay_s")] public double DelayS { get; set; }
    /// <summary>只能由教官 triggerTimeline 觸發</summary>
    public bool Manual { get; set; }
    /// <summary>初始是否啟用(false 者需被 arm)</summary>
    public bool Armed { get; set; } = true;
    public TriggerCondition? When { get; set; }
    public List<TimelineAction> Actions { get; set; } = new();

    /// <summary>項目是否只能手動觸發(manual: true、無 when、或 when.type = manual)。</summary>
    [JsonIgnore] public bool IsManual => Manual || When is null || When.IsManualLeaf;
}

public enum TimelineVisibility
{
    Instructor,
    All,
}

/// <summary>重複規則:<c>once</c>(預設)、<c>always</c>、或 <c>{ every_s, max }</c>。</summary>
[JsonConverter(typeof(RepeatSpecConverter))]
public sealed class RepeatSpec
{
    public RepeatMode Mode { get; set; } = RepeatMode.Once;
    /// <summary>every 模式的間隔(秒)</summary>
    public double EveryS { get; set; }
    /// <summary>every 模式的最大次數(null = 不限)</summary>
    public int? Max { get; set; }

    public static RepeatSpec Once => new();
}

public enum RepeatMode
{
    Once,
    Always,
    Every,
}

public sealed class RepeatSpecConverter : JsonConverter<RepeatSpec>
{
    public override RepeatSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString()!;
            return s.ToLowerInvariant() switch
            {
                "once" => new RepeatSpec(),
                "always" => new RepeatSpec { Mode = RepeatMode.Always },
                _ => throw new JsonException($"repeat 須為 once | always | {{every_s, max}}:{s}"),
            };
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        if (o.ValueKind != JsonValueKind.Object) throw new JsonException("repeat 須為 once | always | {every_s, max}");
        var spec = new RepeatSpec { Mode = RepeatMode.Every };
        if (o.TryGetProperty("every_s", out var e)) spec.EveryS = SimCommand.AsDouble(e) ?? 0;
        if (o.TryGetProperty("max", out var m) && SimCommand.AsDouble(m) is { } mv) spec.Max = (int)mv;
        if (spec.EveryS <= 0) throw new JsonException("repeat.every_s 必須為正");
        return spec;
    }

    public override void Write(Utf8JsonWriter writer, RepeatSpec value, JsonSerializerOptions options)
    {
        switch (value.Mode)
        {
            case RepeatMode.Once: writer.WriteStringValue("once"); break;
            case RepeatMode.Always: writer.WriteStringValue("always"); break;
            default:
                writer.WriteStartObject();
                writer.WriteNumber("every_s", value.EveryS);
                if (value.Max is { } m) writer.WriteNumber("max", m);
                writer.WriteEndObject();
                break;
        }
    }
}

/// <summary>
/// 觸發條件(遞迴):leaf(<c>type</c> 必填)或組合(<c>all</c> / <c>any</c> / <c>not</c>);欄位依 type 選用(情境引擎設計第 1.1.1 節)。
/// </summary>
public sealed class TriggerCondition
{
    public string? Type { get; set; }
    public List<TriggerCondition>? All { get; set; }
    public List<TriggerCondition>? Any { get; set; }
    public TriggerCondition? Not { get; set; }

    // time
    public double? AtLeast { get; set; }
    public SinceSpec? Since { get; set; }
    // ownDistance
    public ScenarioPosition? Point { get; set; }
    public string? Waypoint { get; set; }
    public string? Target { get; set; }
    public double? LessThan { get; set; }
    public double? GreaterThan { get; set; }
    // ownSpeed
    public string? Metric { get; set; }
    // ukc
    public double? LessThanFractionOfDraft { get; set; }
    // xtd
    public string? Side { get; set; }
    // routeLeg(數值)/ phase(字串)
    [JsonPropertyName("equals")] public JsonElement? EqualsRaw { get; set; }
    public string? PassedWaypoint { get; set; }
    // zone / event / fault
    public string? Id { get; set; }
    public bool? Inside { get; set; }
    public string? Kind { get; set; }
    public string? ItemId { get; set; }
    public string? TargetId { get; set; }
    public CountSpec? Count { get; set; }
    public string? Name { get; set; }
    public bool? Active { get; set; }
    // telegraph
    public List<string>? In { get; set; }
    public List<string>? NotIn { get; set; }
    // autopilot
    public bool? Enabled { get; set; }

    [JsonIgnore] public double? EqualsNumber => EqualsRaw is { } e ? SimCommand.AsDouble(e) : null;
    [JsonIgnore] public string? EqualsString => EqualsRaw is { ValueKind: JsonValueKind.String } e ? e.GetString() : EqualsRaw?.ToString();
    [JsonIgnore] public bool IsComposite => All is not null || Any is not null || Not is not null;
    [JsonIgnore] public bool IsManualLeaf => string.Equals(Type, "manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>所有 leaf(含巢狀)。</summary>
    public IEnumerable<TriggerCondition> Leaves()
    {
        if (All is not null) foreach (var c in All) foreach (var l in c.Leaves()) yield return l;
        if (Any is not null) foreach (var c in Any) foreach (var l in c.Leaves()) yield return l;
        if (Not is not null) foreach (var l in Not.Leaves()) yield return l;
        if (!IsComposite) yield return this;
    }

    public static readonly string[] LeafTypes =
    {
        "time", "ownDistance", "ownHeading", "ownSpeed", "ukc", "xtd", "routeLeg", "zone", "phase", "event", "fault", "telegraph", "autopilot", "manual",
    };
}

/// <summary>time 條件的起算事件:自該事件最後一次發生起算。</summary>
public sealed class SinceSpec
{
    public string Event { get; set; } = "";
    public string? ItemId { get; set; }
    public string? TargetId { get; set; }
    public int? LegIndex { get; set; }
    public string? Waypoint { get; set; }
}

public sealed class CountSpec
{
    public int AtLeast { get; set; } = 1;
}

/// <summary>
/// 時間表動作:YAML 為只有一個鍵的物件(鍵即動作名);<see cref="Kind"/> 為動作名、<see cref="Payload"/> 為原始值,
/// 各動作以 <c>As…</c> 取得型別化內容(情境引擎設計第 1.1.2 節)。
/// </summary>
[JsonConverter(typeof(TimelineActionConverter))]
public sealed class TimelineAction
{
    public string Kind { get; set; } = "";
    public JsonElement Payload { get; set; }

    public static readonly string[] AllowedKinds =
    {
        "injectFault", "clearFault", "setEnvironment", "targetControl", "voiceScript", "message", "phase", "freeze",
        "autopilot", "ownShip", "timeScale", "arm", "disarm", "endExercise",
    };
    public static readonly string[] ForbiddenKinds = { "loadScenario", "reset", "restore", "snapshot" };

    public static TimelineAction Of(string kind, object? payload) => new()
    {
        Kind = kind,
        Payload = payload is JsonElement e ? e : JsonSerializer.SerializeToElement(payload, ContractJson.Options),
    };

    /// <summary>字串值(或物件的 name 欄位)。</summary>
    public string? AsString() => Payload.ValueKind switch
    {
        JsonValueKind.String => Payload.GetString(),
        JsonValueKind.Object when Payload.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
        JsonValueKind.Number => Payload.GetRawText(),
        _ => null,
    };

    public double? AsNumber() => SimCommand.AsDouble(Payload);

    public string? Field(string name)
        => Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public double? NumberField(string name)
        => Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(name, out var e) ? SimCommand.AsDouble(e) : null;

    /// <summary>injectFault 的 args(物件形式;無則 null)。</summary>
    public JsonElement? FaultArgs()
        => Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty("args", out var a) ? a : null;

    public VoiceScriptSpec? AsVoiceScript()
        => Payload.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<VoiceScriptSpec>(Payload.GetRawText(), ContractJson.Options) : null;

    public MessageSpec? AsMessage()
        => Payload.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<MessageSpec>(Payload.GetRawText(), ContractJson.Options)
            : Payload.ValueKind == JsonValueKind.String ? new MessageSpec { Text = Payload.GetString() ?? "" } : null;
}

public sealed class TimelineActionConverter : JsonConverter<TimelineAction>
{
    public override TimelineAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        if (o.ValueKind != JsonValueKind.Object) throw new JsonException("時間表動作須為只有一個鍵的物件");
        string? kind = null;
        JsonElement payload = default;
        var n = 0;
        foreach (var p in o.EnumerateObject())
        {
            n++;
            kind = p.Name;
            payload = p.Value.Clone();
        }
        if (n != 1 || kind is null) throw new JsonException("時間表動作須為只有一個鍵的物件(鍵即動作名)");
        return new TimelineAction { Kind = kind, Payload = payload };
    }

    public override void Write(Utf8JsonWriter writer, TimelineAction value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(value.Kind);
        if (value.Payload.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else value.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }
}

/// <summary>語音腳本(核心只記錄事件,由教官站/簡化教官模式播放)。</summary>
public sealed class VoiceScriptSpec
{
    /// <summary>vts | pilot | ecr | tug | otherShip | master | agent | helmsman | lookout | narrator</summary>
    public string? Role { get; set; }
    /// <summary>vhf16 | vhf13 | vhfPort | intercom | phone | pa | bridge</summary>
    public string? Channel { get; set; }
    public string? Text { get; set; }
    /// <summary>相對於情境包目錄 data/scenarios/&lt;id&gt;/</summary>
    public string? File { get; set; }
    public string? Lang { get; set; }
    [JsonPropertyName("durationEstimate_s")] public double? DurationEstimateS { get; set; }
}

/// <summary>文字訊息(教官站事件流;to: all 時學員站可顯示)。</summary>
public sealed class MessageSpec
{
    /// <summary>instructor | all</summary>
    public string To { get; set; } = "instructor";
    public string Text { get; set; } = "";
    /// <summary>info | warning | alarm</summary>
    public string Severity { get; set; } = "info";
}
