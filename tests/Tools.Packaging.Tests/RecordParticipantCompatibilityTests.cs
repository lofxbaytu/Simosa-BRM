using SimosaBRM.Packaging.Records;
using SimosaBRM.SimCore.Recording;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 舊/外部紀錄的字串陣列 participants(第十六輪已知限制 3;docs/design/packaging-sync.md 第 7 節):
/// 紀錄包的 <see cref="RecordInspector"/> 與核心的 <see cref="RecordReader"/> 讀到相同的參與者(字串 → name、role 為 null),
/// 訓練時數彙總不受影響;物件與字串混用的陣列亦可。
/// </summary>
public sealed class RecordParticipantCompatibilityTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StringArrayParticipants_ReadBySimCoreRecordReaderAndInspectorAlike()
    {
        var dir = _env.PathIn("rec");
        var path = TestEnv.WriteRecord(dir, "old-strings.jsonl", "E01_baseline", Start, 1800,
            new object[] { "丁", new { name = "王大副", role = "oow", rank = "大副", certificateNo = "C-123" } });

        var inspected = RecordInspector.Inspect(path, "records/old-strings.jsonl");
        Assert.Equal("header", inspected.SessionInfoSource);
        Assert.True(inspected.IntegrityOk);
        Assert.Equal(new[] { "丁", "王大副" }, inspected.Participants.Select(p => p.Name));
        Assert.Equal(new string?[] { null, "oow" }, inspected.Participants.Select(p => p.Role));

        var rec = RecordReader.Read(path);                        // 先前對字串元素丟 JsonException
        Assert.True(rec.IntegrityOk);
        Assert.Equal("header", rec.SessionInfoSource);
        Assert.Equal(inspected.Participants.Select(p => p.Name), rec.SessionInfo!.Participants.Select(p => p.Name));
        Assert.Equal(inspected.Participants.Select(p => p.Role), rec.SessionInfo.Participants.Select(p => p.Role));
        Assert.Equal("大副", rec.SessionInfo.Participants[1].Rank);
        Assert.Equal("C-123", rec.SessionInfo.Participants[1].CertificateNo);

        var hours = RecordInspector.Summarize(new[] { inspected });
        Assert.Equal(2, hours.Participants.Count);
        Assert.Equal(0.5, Assert.Single(hours.Participants, p => p.Name == "丁").TotalHours, 3);
    }
}
