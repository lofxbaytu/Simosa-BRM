using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Recording;

/// <summary>
/// 練習參與者與休息時間確認(規劃書第 5.5 節「休息時間相容」、需求追溯表 R9/R13):教官站在練習前以 WebSocket 指令
/// <c>sessionInfo</c> 送出,主控程序寫進紀錄 — 紀錄尚未開啟(或之後因情境切換另開新檔)時寫在 <see cref="RecordHeader"/> 的
/// <c>participants / restHoursConfirmation / course</c> 欄位;紀錄已開啟時追加一行 <see cref="RecordSession"/>(<c>kind: session</c>)。
/// 兩者都不進引擎狀態雜湊鏈(引擎不處理此指令;與 summary/snapshot 行相同只計入 footer 的檔案 SHA-256),重播不受影響。
/// 讀取規則(<see cref="RecordFile.SessionInfo"/>、Tools.Packaging RecordInspector):header 有就用 header,否則取最後一行 session。
/// </summary>
public sealed record RecordSessionInfo
{
    /// <summary>參與者職務列舉(sessionInfo 契約固定)</summary>
    public static readonly string[] Roles = { "master", "oow", "pilot", "helmsman", "lookout", "observer", "instructor" };

    [JsonPropertyName("participants")] public required IReadOnlyList<RecordParticipant> Participants { get; init; }
    [JsonPropertyName("restHoursConfirmation")] public RecordRestHoursConfirmation? RestHoursConfirmation { get; init; }
    [JsonPropertyName("course")] public RecordCourse? Course { get; init; }

    /// <summary>
    /// 由 <c>sessionInfo</c> 指令的 <c>args</c> 解析並驗證:participants 非空且每位有 name 與列舉內的 role(不分大小寫,存小寫);
    /// restHoursConfirmation 必填(confirmedBy 非空、confirmedAtUtc 可解析為 ISO-8601、compliant 布林);course 選填;未知欄位略過。
    /// 回傳 null 並以 <paramref name="error"/> 說明時整個指令拒絕、不寫任何東西。
    /// </summary>
    public static RecordSessionInfo? TryParse(JsonElement? args, out string error)
    {
        error = "";
        if (args is not { ValueKind: JsonValueKind.Object } a) { error = "缺少 args 物件"; return null; }
        var participants = new List<RecordParticipant>();
        if (!a.TryGetProperty("participants", out var ps) || ps.ValueKind != JsonValueKind.Array) { error = "缺少 participants 陣列"; return null; }
        var idx = 0;
        foreach (var p in ps.EnumerateArray())
        {
            idx++;
            if (p.ValueKind != JsonValueKind.Object) { error = $"participants[{idx}] 須為物件"; return null; }
            var name = Clean(Str(p, "name"));
            if (name.Length == 0) { error = $"participants[{idx}] 缺少 name"; return null; }
            var role = Clean(Str(p, "role")).ToLowerInvariant();
            if (!Roles.Contains(role)) { error = $"participants[{idx}]({name})的 role 不在列舉內:{string.Join("|", Roles)}"; return null; }
            participants.Add(new RecordParticipant
            {
                Name = name, Role = role,
                Rank = NullIfEmpty(Clean(Str(p, "rank"))),
                CertificateNo = NullIfEmpty(Clean(Str(p, "certificateNo"))),
            });
        }
        if (participants.Count == 0) { error = "participants 至少一位"; return null; }

        if (!a.TryGetProperty("restHoursConfirmation", out var rc) || rc.ValueKind != JsonValueKind.Object) { error = "缺少 restHoursConfirmation 物件"; return null; }
        var by = Clean(Str(rc, "confirmedBy"));
        if (by.Length == 0) { error = "restHoursConfirmation.confirmedBy 必填"; return null; }
        var at = Clean(Str(rc, "confirmedAtUtc"));
        if (at.Length == 0 || !DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var atUtc))
        { error = "restHoursConfirmation.confirmedAtUtc 須為 ISO-8601 時刻"; return null; }
        if (!rc.TryGetProperty("compliant", out var cp) || cp.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = "restHoursConfirmation.compliant 須為布林"; return null; }
        var rest = new RecordRestHoursConfirmation
        {
            ConfirmedBy = by,
            ConfirmedAtUtc = atUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Compliant = cp.ValueKind == JsonValueKind.True,
            Note = NullIfEmpty(Clean(Str(rc, "note"))),
        };

        RecordCourse? course = null;
        if (a.TryGetProperty("course", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            var id = NullIfEmpty(Clean(Str(c, "id")));
            var title = NullIfEmpty(Clean(Str(c, "title")));
            var instructor = NullIfEmpty(Clean(Str(c, "instructor")));
            if (id is not null || title is not null || instructor is not null) course = new RecordCourse { Id = id, Title = title, Instructor = instructor };
        }
        return new RecordSessionInfo { Participants = participants, RestHoursConfirmation = rest, Course = course };
    }

    /// <summary>一行摘要(主控台、ack detail)。</summary>
    public string Describe()
        => $"參與者 {Participants.Count} 位({string.Join("、", Participants.Select(p => $"{p.Name}/{p.Role}"))})"
           + (RestHoursConfirmation is { } r ? $";休息時間由 {r.ConfirmedBy} 於 {r.ConfirmedAtUtc} 確認{(r.Compliant ? "符合" : "不符合")}" : "")
           + (Course is { } c ? $";課程 {c.Id ?? c.Title ?? c.Instructor}" : "");

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>單行欄位:控制字元換成空白並去頭尾空白(與教官站 session-info.ts 的 clean 相同)。</summary>
    private static string Clean(string? v)
        => v is null ? "" : new string(v.Select(ch => ch < ' ' || ch == '\u007f' ? ' ' : ch).ToArray()).Trim();

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}

public sealed record RecordParticipant
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>master | oow | pilot | helmsman | lookout | observer | instructor</summary>
    [JsonPropertyName("role")] public required string Role { get; init; }
    /// <summary>職級(船長、大副…;選填)</summary>
    [JsonPropertyName("rank")] public string? Rank { get; init; }
    /// <summary>證書號(選填;報告頁以此對應證書效期追蹤)</summary>
    [JsonPropertyName("certificateNo")] public string? CertificateNo { get; init; }
}

public sealed record RecordRestHoursConfirmation
{
    [JsonPropertyName("confirmedBy")] public required string ConfirmedBy { get; init; }
    /// <summary>ISO-8601(毫秒 Z)</summary>
    [JsonPropertyName("confirmedAtUtc")] public required string ConfirmedAtUtc { get; init; }
    /// <summary>是否符合 STCW A-VIII/1(MLC 2006 規則 2.3)與公司規定</summary>
    [JsonPropertyName("compliant")] public required bool Compliant { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}

public sealed record RecordCourse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("instructor")] public string? Instructor { get; init; }
}

/// <summary>紀錄已開啟後收到 sessionInfo 時追加的行(<c>kind: session</c>;不進狀態雜湊鏈;講評站/紀錄包取最後一行)。</summary>
public sealed record RecordSession
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Session;
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    /// <summary>當前模擬時間(s)</summary>
    [JsonPropertyName("t")] public required double T { get; init; }
    [JsonPropertyName("participants")] public required IReadOnlyList<RecordParticipant> Participants { get; init; }
    [JsonPropertyName("restHoursConfirmation")] public RecordRestHoursConfirmation? RestHoursConfirmation { get; init; }
    [JsonPropertyName("course")] public RecordCourse? Course { get; init; }

    public RecordSessionInfo ToInfo() => new() { Participants = Participants, RestHoursConfirmation = RestHoursConfirmation, Course = Course };
}
