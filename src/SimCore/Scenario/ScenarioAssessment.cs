using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>評估規則(scenario.schema.json <c>assessment</c>;規劃書第 8.3 節、情境引擎設計第 1.3 節)。數值欄位可引用參數集。</summary>
public sealed class ScenarioAssessment
{
    /// <summary>參數集路徑(選填;預設 data/parameters/company-procedures.yaml,主控程序 --parameters 可覆寫)</summary>
    public string? ParameterSet { get; set; }
    /// <summary>最小 CPA 門檻(nm);自船與任一目標的 CPA 低於此值且 TCPA &gt; 0 時記錄事件</summary>
    [JsonPropertyName("minCpa_nm")]
    public double MinCpaNm { get; set; } = 0.5;
    public EncounterSpec? Encounter { get; set; }
    public UkcSpec? Ukc { get; set; }
    public ManoeuvringSpec? Manoeuvring { get; set; }
    public List<HardFailRule> HardFail { get; set; } = new();
    public List<BandRule> Bands { get; set; } = new();
    public List<RequiredEvent> Required { get; set; } = new();
    public ScoringSpec? Scoring { get; set; }

    /// <summary>是否有任何評估規則(決定雜湊鏈是否追加評估/時間表位元組)。</summary>
    [JsonIgnore] public bool HasRules => HardFail.Count > 0 || Bands.Count > 0 || Required.Count > 0;
}

public sealed class EncounterSpec
{
    /// <summary>「首次偵測」= 目標進入此距離且 TCPA &gt; 0(預設 6 nm)</summary>
    [JsonPropertyName("detectionRange_nm")] public double DetectionRangeNm { get; set; } = 6.0;
    [JsonPropertyName("substantialCourseChange_deg")] public double SubstantialCourseChangeDeg { get; set; } = 30.0;
    [JsonPropertyName("substantialSpeedChange_kn")] public double SubstantialSpeedChangeKn { get; set; } = 2.0;
    /// <summary>會遇開始的 CPA 關注門檻(nm;預設 2 × minCpa_nm)</summary>
    [JsonPropertyName("cpaInterest_nm")] public double? CpaInterestNm { get; set; }
}

public sealed class UkcSpec
{
    /// <summary>全域門檻;zones ukcPolicy 覆寫</summary>
    public UkcLimit? Min { get; set; }
}

public sealed class ManoeuvringSpec
{
    [JsonPropertyName("rotLimit_degPerMin")] public double? RotLimitDegPerMin { get; set; }
    /// <summary>滿舵角(度;未給時取船舶 rudder.maxAngle_deg)</summary>
    [JsonPropertyName("hardOver_deg")] public double? HardOverDeg { get; set; }
}

/// <summary>硬性失敗規則:rule = grounding | collision | cpa | ukc | zoneEntered | event。</summary>
public sealed class HardFailRule
{
    public string Id { get; set; } = "";
    public string Rule { get; set; } = "";
    /// <summary>cpa:nm;ukc:{metres|fractionOfDraft}(以 LessThanUkc 讀)</summary>
    public JsonElement? LessThan { get; set; }
    public bool ExcludeEscorts { get; set; }
    public string? ZoneId { get; set; }
    public string? Kind { get; set; }

    [JsonIgnore] public double? LessThanNumber => LessThan is { } e ? SimCommand.AsDouble(e) : null;
    [JsonIgnore] public UkcLimit? LessThanUkc => LessThan is { ValueKind: JsonValueKind.Object } e
        ? JsonSerializer.Deserialize<UkcLimit>(e.GetRawText(), ContractJson.Options) : null;

    public static readonly string[] Rules = { "grounding", "collision", "cpa", "ukc", "zoneEntered", "event" };
}

/// <summary>容許帶:指標超過 warn/fail 門檻時扣分(fail 可寫 "2x" = warn × 2)。</summary>
public sealed class BandRule
{
    public string Id { get; set; } = "";
    public string Metric { get; set; } = "";
    /// <summary>above(預設,值越大越差)| below</summary>
    public string Direction { get; set; } = "above";
    public double? Warn { get; set; }
    public BandThreshold? Fail { get; set; }
    public double Penalty { get; set; }
    public double? FailPenalty { get; set; }
    public bool ExcludeEscorts { get; set; }

    [JsonIgnore] public bool Below => string.Equals(Direction, "below", StringComparison.OrdinalIgnoreCase);
    /// <summary>fail 門檻數值(倍數時以 warn 計)。</summary>
    [JsonIgnore] public double? FailValue => Fail is null ? null : Fail.Multiplier is { } m ? (Warn is { } w ? w * m : null) : Fail.Value;
}

/// <summary>數值或 "&lt;n&gt;x" 倍數。</summary>
[JsonConverter(typeof(BandThresholdConverter))]
public sealed class BandThreshold
{
    public double? Value { get; set; }
    public double? Multiplier { get; set; }
}

public sealed class BandThresholdConverter : JsonConverter<BandThreshold>
{
    public override BandThreshold Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return new BandThreshold { Value = reader.GetDouble() };
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString()!.Trim();
            if (s.EndsWith('x') && double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m))
                return new BandThreshold { Multiplier = m };
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return new BandThreshold { Value = v };
            throw new JsonException($"bands[].fail 須為數值或 \"<n>x\":{s}");
        }
        throw new JsonException("bands[].fail 須為數值或 \"<n>x\"");
    }

    public override void Write(Utf8JsonWriter writer, BandThreshold value, JsonSerializerOptions options)
    {
        if (value.Multiplier is { } m) writer.WriteStringValue($"{m.ToString(System.Globalization.CultureInfo.InvariantCulture)}x");
        else writer.WriteNumberValue(value.Value ?? 0);
    }
}

/// <summary>必要事件:after 事件發生後 within_s 內須達成 expect。</summary>
public sealed class RequiredEvent
{
    public string Id { get; set; } = "";
    public AfterSpec After { get; set; } = new();
    public ExpectSpec Expect { get; set; } = new();
    [JsonPropertyName("within_s")] public double WithinS { get; set; }
    public double Penalty { get; set; }
    [JsonPropertyName("latePenaltyPer_s")] public double? LatePenaltyPerS { get; set; }
}

/// <summary>計時起點事件。</summary>
public sealed class AfterSpec
{
    public string Event { get; set; } = "";
    public string? ItemId { get; set; }
    public string? TargetId { get; set; }
    public string? Waypoint { get; set; }
    public int? LegIndex { get; set; }
    public string? ZoneId { get; set; }
}

/// <summary>
/// 可偵測的期望動作:字串(rudderOrder | telegraphChange | rpmOrder | thrusterOrder | steeringModeChange | autopilotOn | autopilotOff | acknowledge)、
/// <c>{acknowledge: {kind}}</c>、<c>{event: {kind, itemId?, targetId?}}</c>、<c>{any: [...]}</c>。
/// </summary>
[JsonConverter(typeof(ExpectSpecConverter))]
public sealed class ExpectSpec
{
    public string? Name { get; set; }
    public AcknowledgeSpec? Acknowledge { get; set; }
    public EventRef? Event { get; set; }
    public List<ExpectSpec>? Any { get; set; }

    public static readonly string[] SimpleNames =
    {
        "rudderOrder", "telegraphChange", "rpmOrder", "thrusterOrder", "steeringModeChange", "autopilotOn", "autopilotOff", "acknowledge",
    };

    /// <summary>未給任何內容(YAML 缺 expect 時反序列化的預設值);Scenario.StructuralIssues 視為錯誤,與 Python scenario.py 一致。</summary>
    [JsonIgnore] public bool IsEmpty => Name is null && Acknowledge is null && Event is null && Any is null;

    /// <summary>所有 leaf。</summary>
    public IEnumerable<ExpectSpec> Leaves()
    {
        if (Any is not null) { foreach (var a in Any) foreach (var l in a.Leaves()) yield return l; }
        else yield return this;
    }

    /// <summary>人可讀的達成方式名稱(summary.required[].by)。</summary>
    public string Label => Name ?? (Acknowledge is { } a ? $"acknowledge:{a.Kind ?? "any"}" : Event is { } e ? $"event:{e.Kind}" : "any");
}

public sealed class AcknowledgeSpec
{
    public string? Kind { get; set; }
}

public sealed class EventRef
{
    public string Kind { get; set; } = "";
    public string? ItemId { get; set; }
    public string? TargetId { get; set; }
}

public sealed class ExpectSpecConverter : JsonConverter<ExpectSpec>
{
    public override ExpectSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new ExpectSpec { Name = reader.GetString() };
        using var doc = JsonDocument.ParseValue(ref reader);
        return FromElement(doc.RootElement, options);
    }

    private static ExpectSpec FromElement(JsonElement o, JsonSerializerOptions options)
    {
        if (o.ValueKind == JsonValueKind.String) return new ExpectSpec { Name = o.GetString() };
        if (o.ValueKind != JsonValueKind.Object) throw new JsonException("expect 須為字串或物件");
        var spec = new ExpectSpec();
        if (o.TryGetProperty("any", out var any) && any.ValueKind == JsonValueKind.Array)
            spec.Any = any.EnumerateArray().Select(e => FromElement(e, options)).ToList();
        if (o.TryGetProperty("acknowledge", out var ack))
            spec.Acknowledge = ack.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<AcknowledgeSpec>(ack.GetRawText(), options) : new AcknowledgeSpec();
        if (o.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
            spec.Event = JsonSerializer.Deserialize<EventRef>(ev.GetRawText(), options);
        if (o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) spec.Name = n.GetString();
        if (spec.Any is null && spec.Acknowledge is null && spec.Event is null && spec.Name is null)
            throw new JsonException("expect 物件須含 any | acknowledge | event");
        return spec;
    }

    public override void Write(Utf8JsonWriter writer, ExpectSpec value, JsonSerializerOptions options)
    {
        if (value.Name is not null && value.Any is null && value.Acknowledge is null && value.Event is null)
        {
            writer.WriteStringValue(value.Name);
            return;
        }
        writer.WriteStartObject();
        if (value.Any is not null)
        {
            writer.WritePropertyName("any");
            JsonSerializer.Serialize(writer, value.Any, options);
        }
        if (value.Acknowledge is not null)
        {
            writer.WritePropertyName("acknowledge");
            JsonSerializer.Serialize(writer, value.Acknowledge, options);
        }
        if (value.Event is not null)
        {
            writer.WritePropertyName("event");
            JsonSerializer.Serialize(writer, value.Event, options);
        }
        writer.WriteEndObject();
    }
}

public sealed class ScoringSpec
{
    public double Start { get; set; } = 100.0;
    public double PassMark { get; set; } = 70.0;
    public JsonElement? AppliesTo { get; set; }
}
