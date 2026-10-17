using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>簡報(scenario.schema.json <c>briefing</c>:字串或物件;字串等同 {text});規劃書第 9.1 節角色與最少人數。</summary>
[JsonConverter(typeof(ScenarioBriefingConverter))]
public sealed class ScenarioBriefing
{
    public string? Text { get; set; }
    public List<BriefingAttachment> Attachments { get; set; } = new();
    public List<BriefingRole> Roles { get; set; } = new();
    public int? MinParticipants { get; set; }
    /// <summary>情境預估長度(秒;驗證器用)</summary>
    [JsonPropertyName("duration_s")] public double? DurationS { get; set; }
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
    }

    public override ScenarioBriefing? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return new ScenarioBriefing { Text = reader.GetString() };
        var p = JsonSerializer.Deserialize<Plain>(ref reader, options) ?? new Plain();
        return new ScenarioBriefing
        {
            Text = p.Text, Attachments = p.Attachments ?? new(), Roles = p.Roles ?? new(), MinParticipants = p.MinParticipants, DurationS = p.DurationS,
        };
    }

    public override void Write(Utf8JsonWriter writer, ScenarioBriefing value, JsonSerializerOptions options)
    {
        if (value.Attachments.Count == 0 && value.Roles.Count == 0 && value.MinParticipants is null && value.DurationS is null)
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
        }, options);
    }
}
