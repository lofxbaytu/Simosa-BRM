using System.Text.Json;
using System.Text.Json.Nodes;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 紀錄標頭 participants 相容(第十六輪已知限制 3;docs/design/packaging-sync.md 第 7 節):核心一律寫 {name, role, rank?, certificateNo?} 物件陣列,
/// 讀取另接受舊/外部紀錄的字串元素(→ {name, role: null});<see cref="RecordReader"/> 與 <see cref="Replayer"/> 讀含字串陣列的紀錄不再丟例外,
/// 寫出的位元組與原自動序列化相同。
/// </summary>
public class RecordParticipantCompatibilityTests
{
    [Fact]
    public void Deserialize_AcceptsStringAndObjectElements()
    {
        const string json = """{"participants":["王大副",{"name":"李舵工","role":"helmsman","rank":"舵工"},{"NAME":"丙","Role":"oow","extra":{"x":1},"certificateNo":null}]}""";
        var info = ContractJson.Deserialize<RecordSession>(json.Replace("{\"participants\"", "{\"kind\":\"session\",\"tick\":0,\"t\":0,\"participants\""))!;
        Assert.Equal(new[] { "王大副", "李舵工", "丙" }, info.Participants.Select(p => p.Name));
        Assert.Equal(new string?[] { null, "helmsman", "oow" }, info.Participants.Select(p => p.Role));
        Assert.Equal("舵工", info.Participants[1].Rank);
        Assert.Null(info.Participants[2].CertificateNo);
        // 標頭:字串陣列也算有參與者(SessionInfo 不為 null)
        var header = ContractJson.Deserialize<RecordHeader>(
            """{"kind":"header","version":2,"createdUtc":"2026-10-29T00:00:00Z","shipId":"FSB1","loading":"ballast","seed":1,"dt":0.02,"dynamics":"test","scenario":{"id":"X"},"participants":["丁","戊"]}""")!;
        Assert.NotNull(header.SessionInfo);
        Assert.Equal(new[] { "丁", "戊" }, header.SessionInfo!.Participants.Select(p => p.Name));
        Assert.All(header.SessionInfo.Participants, p => Assert.Null(p.Role));
        // 不接受的型別仍為錯誤
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<RecordSession>("""{"kind":"session","tick":0,"t":0,"participants":[42]}"""));
    }

    [Fact]
    public void Deserialize_RejectsNullElement()
    {
        var ex = Assert.Throws<JsonException>(() => ContractJson.Deserialize<RecordSession>("""{"kind":"session","tick":0,"t":0,"participants":[null,"甲"]}"""));
        Assert.Contains("不可為 null", ex.Message);
    }

    [Fact]
    public void Serialize_IsUnchangedFromAutomaticShape()
    {
        Assert.Equal("""{"name":"王大副","role":"oow","rank":"大副","certificateNo":"C-123"}""",
            ContractJson.Serialize(new RecordParticipant { Name = "王大副", Role = "oow", Rank = "大副", CertificateNo = "C-123" }));
        Assert.Equal("""{"name":"李舵工","role":"helmsman"}""", ContractJson.Serialize(new RecordParticipant { Name = "李舵工", Role = "helmsman" }));
        Assert.Equal("""{"name":"丁"}""", ContractJson.Serialize(new RecordParticipant { Name = "丁" }));
        // 往返:字串 → 物件(role 省略)
        var back = ContractJson.Deserialize<RecordParticipant>(ContractJson.Serialize(ContractJson.Deserialize<RecordParticipant>("\"丁\"")!))!;
        Assert.Equal("丁", back.Name);
        Assert.Null(back.Role);
    }

    [Fact]
    public void RecordReaderAndReplayer_ReadRecordWhoseHeaderParticipantsAreStrings()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"participants-strings-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        var session = new RecordSessionInfo
        {
            Participants = new[] { new RecordParticipant { Name = "王大副", Role = "oow" }, new RecordParticipant { Name = "李舵工", Role = "helmsman" } },
            RestHoursConfirmation = new RecordRestHoursConfirmation { ConfirmedBy = "船長", ConfirmedAtUtc = "2026-10-29T01:00:00.000Z", Compliant = true },
        };
        using (var writer = new RecordWriter(path, engine, includeStates: false, session))
        {
            TestData.RunTo(engine, 2);
            writer.Close();
        }
        var original = RecordReader.Read(path);
        Assert.True(original.IntegrityOk);
        Assert.Equal("header", original.SessionInfoSource);

        // 以舊/外部格式改寫標頭:participants 為字串陣列(混入一個物件);其餘行不動
        var lines = File.ReadAllLines(path);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        header["participants"] = new JsonArray("王大副", new JsonObject { ["name"] = "李舵工", ["role"] = "helmsman" });
        lines[0] = header.ToJsonString(ContractJson.Options);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        var rec = RecordReader.Read(path);                        // 不再丟例外
        Assert.Equal("header", rec.SessionInfoSource);
        Assert.Equal(new[] { "王大副", "李舵工" }, rec.SessionInfo!.Participants.Select(p => p.Name));
        Assert.Null(rec.SessionInfo.Participants[0].Role);
        Assert.Equal("helmsman", rec.SessionInfo.Participants[1].Role);
        Assert.Equal("船長", rec.SessionInfo.RestHoursConfirmation!.ConfirmedBy);
        Assert.False(rec.IntegrityOk);                            // 標頭已被改寫(footer.sha256 不符),但仍可讀與重播
        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} / {result.ActualHash}");
        Assert.Equal(original.Footer!.FinalTick, result.FinalTick);
    }
}
