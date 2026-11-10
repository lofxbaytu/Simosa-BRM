using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 簡報(scenario.schema.json <c>briefing</c>:字串或物件;字串等同 {text});規劃書第 9.1 節角色與最少人數,
/// 第二十六輪(R7)補「規劃時間」「同儕評估提示」「講評引導問題」:核心只讀入並原樣寫進紀錄標頭(重播/講評報告用),
/// 不做數值檢查、不進快照與狀態雜湊。
/// </summary>
[JsonConverter(typeof(ScenarioBriefingConverter))]
public sealed class ScenarioBriefing
{
    public string? Text { get; set; }
    public List<BriefingAttachment> Attachments { get; set; } = new();
    public List<BriefingRole> Roles { get; set; } = new();
    public int? MinParticipants { get; set; }
    /// <summary>情境預估長度(秒;驗證器用)</summary>
    [JsonPropertyName("duration_s")] public double? DurationS { get; set; }
    /// <summary>簡報後給團隊的規劃時間(分鐘;教官站本地倒數,規劃書第 9.1 節)</summary>
    public double? PlanningTimeMin { get; set; }
    /// <summary>同儕評估提示(規劃書第 9.1 節)</summary>
    public List<string> PeerAssessmentPrompts { get; set; } = new();
    /// <summary>講評引導問題(規劃書第 11 章)</summary>
    public List<string> DebriefQuestions { get; set; } = new();

    /// <summary>除文字外沒有任何結構欄位(序列化時可寫成純字串)。</summary>
    public bool IsTextOnly =>
        Attachments.Count == 0 && Roles.Count == 0 && MinParticipants is null && DurationS is null
        && PlanningTimeMin is null && PeerAssessmentPrompts.Count == 0 && DebriefQuestions.Count == 0;
}

public sealed class BriefingAttachment
{
    public string? Title { get; set; }
    public string? File { get; set; }
}

public sealed class BriefingRole
{
    public string Role { get; set; } = "";
    /// <summary>leader | voiceScript | instructor | trainee</summary>
    public string? PlayedBy { get; set; }
    public bool Required { get; set; }
}

/// <summary>字串 ↔ {text};物件照常。</summary>
public sealed class ScenarioBriefingConverter : JsonConverter<ScenarioBriefing>
{
    private sealed class Plain
    {
        public string? Text { get; set; }
        public List<BriefingAttachment>? Attachments { get; set; }
        public List<BriefingRole>? Roles { get; set; }
        public int? MinParticipants { get; set; }
        [JsonPropertyName("duration_s")] public double? DurationS { get; set; }
        // 三個結構欄位以 JsonElement 讀入後嚴格檢查(與 scenario.schema.json、Python structural_issues 一致):
        // ContractJson 的 AllowReadingFromString / LenientStringConverter 會把 "15"、[1, true, null] 寬鬆讀入,null 還會原樣寫進紀錄標頭(審查第二十六輪)
        public JsonElement? PlanningTimeMin { get; set; }
        public JsonElement? PeerAssessmentPrompts { get; set; }
        public JsonElement? DebriefQuestions { get; set; }
    }

    private static double? StrictNumber(JsonElement? e, string name)
    {
        if (e is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind != JsonValueKind.Number) throw new JsonException($"briefing.{name} 須為數值(分鐘)");
        return v.GetDouble();
    }

    private static List<string> StrictStringList(JsonElement? e, string name)
    {
        var list = new List<string>();
        if (e is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return list;
        if (v.ValueKind != JsonValueKind.Array) throw new JsonException($"briefing.{name} 須為字串陣列");
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new JsonException($"briefing.{name} 須為字串陣列");
            list.Add(item.GetString()!);
        }
        return list;
    }

    public override ScenarioBriefing? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return new ScenarioBriefing { Text = reader.GetString() };
        var p = JsonSerializer.Deserialize<Plain>(ref reader, options) ?? new Plain();
        return new ScenarioBriefing
        {
            Text = p.Text, Attachments = p.Attachments ?? new(), Roles = p.Roles ?? new(), MinParticipants = p.MinParticipants, DurationS = p.DurationS,
            PlanningTimeMin = StrictNumber(p.PlanningTimeMin, "planningTimeMin"),
            PeerAssessmentPrompts = StrictStringList(p.PeerAssessmentPrompts, "peerAssessmentPrompts"),
            DebriefQuestions = StrictStringList(p.DebriefQuestions, "debriefQuestions"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ScenarioBriefing value, JsonSerializerOptions options)
    {
        if (value.IsTextOnly)
        {
            writer.WriteStringValue(value.Text ?? "");
            return;
        }
        JsonSerializer.Serialize(writer, new Plain
        {
            Text = value.Text,
            Attachments = value.Attachments.Count > 0 ? value.Attachments : null,
            Roles = value.Roles.Count > 0 ? value.Roles : null,
            MinParticipants = value.MinParticipants,
            DurationS = value.DurationS,
            PlanningTimeMin = value.PlanningTimeMin is { } minutes ? JsonSerializer.SerializeToElement(minutes, options) : null,
            PeerAssessmentPrompts = value.PeerAssessmentPrompts.Count > 0 ? JsonSerializer.SerializeToElement(value.PeerAssessmentPrompts, options) : null,
            DebriefQuestions = value.DebriefQuestions.Count > 0 ? JsonSerializer.SerializeToElement(value.DebriefQuestions, options) : null,
        }, options);
    }
}
