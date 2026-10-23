using System.Text.Json;
using SimosaBRM.Packaging.Records;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 紀錄包讀取參與者與休息時間確認(需求追溯表 R9/R13;教官站 sessionInfo → SimCore 寫入):
/// header 有 participants / restHoursConfirmation / course 就用 header,否則取最後一行 {"kind":"session",…};
/// 訓練時數彙整(RecordInspector.Summarize、紀錄包 manifest.trainingHours)納入兩種來源,並帶出 rank / certificateNo。
/// </summary>
public sealed class SessionInfoInspectorTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc);

    private static readonly object[] HeaderParticipants =
    {
        new { name = "王大副", role = "oow", rank = "大副", certificateNo = "C-123" },
        new { name = "李舵工", role = "helmsman" },
    };
    private static readonly object RestHours = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-12T01:00:00.000Z", compliant = true, note = "已核對" };
    private static readonly object Course = new { id = "BRM-2026-03", title = "BRM 複訓", instructor = "陳教官" };

    private static string SessionLine(long tick, double t, params object[] participants)
        => JsonSerializer.Serialize(new
        {
            kind = "session", tick, t, participants,
            restHoursConfirmation = new { confirmedBy = "帶領者", confirmedAtUtc = "2026-10-12T01:31:00.000Z", compliant = false, note = "值班後休息不足,訓練負責人決定照常" },
            course = new { id = "BRM-2026-04" },
        });

    [Fact]
    public void Header_SessionFields_AreRead_WithRankAndCertificate()
    {
        var dir = _env.PathIn("rec");
        var path = TestEnv.WriteRecord(dir, "header.jsonl", "E01_baseline", Start, 3600, HeaderParticipants, restHoursConfirmation: RestHours, course: Course);
        var s = RecordInspector.Inspect(path, "records/header.jsonl");
        Assert.Equal("header", s.SessionInfoSource);
        Assert.Equal(2, s.Participants.Count);
        Assert.Equal("王大副", s.Participants[0].Name);
        Assert.Equal("oow", s.Participants[0].Role);
        Assert.Equal("大副", s.Participants[0].Rank);
        Assert.Equal("C-123", s.Participants[0].CertificateNo);
        Assert.Null(s.Participants[1].Rank);
        Assert.True(s.RestHoursConfirmation!.Value.GetProperty("compliant").GetBoolean());
        Assert.Equal("船長", s.RestHoursConfirmation.Value.GetProperty("confirmedBy").GetString());
        Assert.Equal("BRM-2026-03", s.Course!.Value.GetProperty("id").GetString());
        Assert.True(s.IntegrityOk);

        var hours = RecordInspector.Summarize(new[] { s });
        Assert.Equal(2, hours.Participants.Count);
        var wang = Assert.Single(hours.Participants, p => p.Name == "王大副");
        Assert.Equal("大副", wang.Rank);
        Assert.Equal("C-123", wang.CertificateNo);
        Assert.Equal(1.0, wang.TotalHours, 3);
    }

    [Fact]
    public void SessionLine_UsedWhenHeaderHasNone_LastLineWins()
    {
        var dir = _env.PathIn("rec");
        var lines = new[]
        {
            SessionLine(100, 2.0, new { name = "甲", role = "master" }),
            SessionLine(500, 10.0, new { name = "王大副", role = "oow", rank = "大副", certificateNo = "C-123" }, new { name = "乙", role = "lookout" }),
        };
        var path = TestEnv.WriteRecord(dir, "session.jsonl", "E05_pilot_traffic", Start, 1800, participants: null, extraLines: lines);
        var s = RecordInspector.Inspect(path, "records/session.jsonl");
        Assert.Equal("session", s.SessionInfoSource);
        Assert.Equal(new[] { "王大副", "乙" }, s.Participants.Select(p => p.Name)); // 最後一行
        Assert.Equal("C-123", s.Participants[0].CertificateNo);
        Assert.False(s.RestHoursConfirmation!.Value.GetProperty("compliant").GetBoolean());
        Assert.Equal("帶領者", s.RestHoursConfirmation.Value.GetProperty("confirmedBy").GetString());
        Assert.Equal("BRM-2026-04", s.Course!.Value.GetProperty("id").GetString());
        Assert.True(s.IntegrityOk); // session 行計入 footer sha256

        var hours = RecordInspector.Summarize(new[] { s });
        Assert.Equal(2, hours.Participants.Count);
        Assert.Equal(0.5, Assert.Single(hours.Participants, p => p.Name == "乙").TotalHours, 3);
    }

    [Fact]
    public void Header_WinsOverSessionLine_AndOldStringParticipantsStillWork()
    {
        var dir = _env.PathIn("rec");
        var path = TestEnv.WriteRecord(dir, "both.jsonl", "E01_baseline", Start, 600, HeaderParticipants, restHoursConfirmation: RestHours,
            extraLines: new[] { SessionLine(100, 2.0, new { name = "丙", role = "observer" }) });
        var s = RecordInspector.Inspect(path, "records/both.jsonl");
        Assert.Equal("header", s.SessionInfoSource);
        Assert.Equal(new[] { "王大副", "李舵工" }, s.Participants.Select(p => p.Name));
        Assert.Equal("船長", s.RestHoursConfirmation!.Value.GetProperty("confirmedBy").GetString());
        // 舊格式:字串陣列的 participants(無 restHoursConfirmation)仍視為 header 來源
        var old = TestEnv.WriteRecord(dir, "old.jsonl", "E01_baseline", Start, 600, new object[] { "丁" });
        var so = RecordInspector.Inspect(old, "records/old.jsonl");
        Assert.Equal("header", so.SessionInfoSource);
        Assert.Equal("丁", Assert.Single(so.Participants).Name);
        Assert.Null(so.RestHoursConfirmation);
        // 兩者皆無
        var none = TestEnv.WriteRecord(dir, "none.jsonl", "E01_baseline", Start, 600);
        Assert.Null(RecordInspector.Inspect(none, "records/none.jsonl").SessionInfoSource);
    }

    [Fact]
    public void RecordPackage_TrainingHours_IncludeSessionLineParticipants()
    {
        var ship = _env.NewShip();
        TestEnv.WriteRecord(ship.RecordsDir, "20261012-013000-E01_baseline.jsonl", "E01_baseline", Start, 3600, HeaderParticipants, restHoursConfirmation: RestHours, course: Course);
        TestEnv.WriteRecord(ship.RecordsDir, "20261013-020000-E05_pilot_traffic.jsonl", "E05_pilot_traffic", Start.AddDays(1), 1800, participants: null,
            extraLines: new[] { SessionLine(50, 1.0, new { name = "王大副", role = "oow", rank = "大副", certificateNo = "C-123" }, new { name = "乙", role = "lookout" }) });
        var mgr = new ShipRecordManager(ship);
        var p = mgr.PackPending()!;
        var store = new RecordStore(_env.PathIn("store"));
        var r = store.Import(mgr.PackagePath(p.PackageId)!, enrollUnknownDevice: true);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        var m = r.Manifest!;
        Assert.Equal(2, m.Exercises.Count);
        var e05 = Assert.Single(m.Exercises, e => e.ScenarioId == "E05_pilot_traffic");
        Assert.Equal("session", e05.SessionInfoSource);
        Assert.Equal("帶領者", e05.RestHoursConfirmation!.Value.GetProperty("confirmedBy").GetString());
        var e01 = Assert.Single(m.Exercises, e => e.ScenarioId == "E01_baseline");
        Assert.Equal("header", e01.SessionInfoSource);
        Assert.Equal(5400, m.TrainingHours.TotalS, 3);
        var wang = Assert.Single(m.TrainingHours.Participants, x => x.Name == "王大副");
        Assert.Equal(2, wang.Exercises);
        Assert.Equal(1.5, wang.TotalHours, 3);
        Assert.Equal("大副", wang.Rank);
        Assert.Equal("C-123", wang.CertificateNo);
        Assert.Equal(0.5, Assert.Single(m.TrainingHours.Participants, x => x.Name == "乙").TotalHours, 3);
        // manifest.json 內確實含來源與職級(教官端報告可用)
        var json = File.ReadAllText(Path.Combine(store.Root, r.Directory!, "manifest.json"));
        Assert.Contains("\"sessionInfoSource\":\"session\"", json.Replace(" ", ""));
        Assert.Contains("C-123", json);
    }
}
