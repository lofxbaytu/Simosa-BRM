using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 試俥目標值存入紀錄標頭(header.manoeuvreTargets {shipId, loading, path, sha256, content}):重播優先使用標頭目標值(目標檔日後更新也不改變舊紀錄的
/// 重播摘要),<c>rescoreTargets</c>(主控程序 --rescore)才讀目前檔案;舊紀錄無此欄位時退回讀檔並提示;情境未使用操縱量測時標頭不寫入。
/// 以「目標值已更新」的情況模擬:錄製時注入 crashStop.stopTime_s 改為 999 s 的目標內容(EngineOptions.ManoeuvreTargets)。
/// </summary>
public class ManoeuvreTargetsHeaderTests
{
    private static Scenario.Scenario CrashStopScenario()
    {
        var sc = MmgScenario(14.5);
        sc.Assessment.Required.Add(new RequiredEvent
        {
            Id = "measured", After = new AfterSpec { Event = ManoeuvreEventKinds.Started }, Expect = new ExpectSpec { Event = new EventRef { Kind = ManoeuvreEventKinds.Measured } }, WithinS = 7200,
        });
        return sc;
    }

    private static readonly Func<Ship.ShipParticulars, LoadingCondition, Physics.IShipDynamics> Dyn = (s, l) => new MmgDynamics(s, l, Fsb1Full);

    /// <summary>目前 FSB1 目標檔內容,crashStop.stopTime_s 改為 999(模擬「錄製時的舊版目標值」)。</summary>
    private static ManoeuvreTargets AlteredTargets()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "ships", "FSB1", "trial_targets.json")))!;
        node["validation"]!["crashStop"]!["stopTime_s"] = 999;
        var text = node.ToJsonString();
        using var doc = JsonDocument.Parse(text);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return ManoeuvreTargets.FromHeader(doc.RootElement, sha, ManoeuvreTargets.RelativePathFor("FSB1"));
    }

    private static string Record(ManoeuvreTargets? targets)
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"targets-{Guid.NewGuid():N}.jsonl");
        var e = new SimulationEngine(TestData.Fsb1(), CrashStopScenario(), Dyn, new EngineOptions { AutoSnapshotIntervalTicks = 0, ManoeuvreTargets = targets });
        using var w = new RecordWriter(path, e);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        TestData.RunTo(e, 360);
        w.Close();
        return path;
    }

    private static ManoeuvreComparison StopTime(AssessmentSummary s)
        => s.Manoeuvres!.Single(r => r.Id == "crashStop").Comparisons.Single(c => c.Key == "stopTime_s" && c.DataQuality == "trial");

    [Fact]
    public void HeaderCarriesTargetsAndReplayUsesThemUnlessRescore()
    {
        var altered = AlteredTargets();
        var path = Record(altered);
        var rec = RecordReader.Read(path);
        var h = rec.Header.ManoeuvreTargets!;
        Assert.Equal("FSB1", h.ShipId);
        Assert.Equal(LoadingCondition.Full, h.Loading);
        Assert.Equal("data/ships/FSB1/trial_targets.json", h.Path);
        Assert.Equal(altered.Sha256, h.Sha256);
        Assert.Equal(999, h.Content.GetProperty("validation").GetProperty("crashStop").GetProperty("stopTime_s").GetDouble());
        var recorded = rec.Summary!.Summary;
        Assert.Equal(999, StopTime(recorded).Target);
        Assert.Equal(altered.Sha256, recorded.Manoeuvres!.Single().TargetsSha256);

        // 重播:使用標頭目標值 → 摘要與紀錄相同(目前檔案為 259 s 不影響)
        var r = Replayer.Replay(path, TestData.Ship, Dyn);
        Assert.True(r.HashMatches && r.EventSequenceMatches);
        Assert.True(r.SummaryMatches);
        Assert.Equal("header", r.ManoeuvreTargetsSource);
        Assert.Equal(999, StopTime(r.Summary!).Target);
        Assert.Contains(r.Notes, n => n.Contains("已更新") && n.Contains("--rescore"));

        // --rescore:影子評估讀目前檔案(259 s);雜湊與事件序列不變(只重算 summary)
        var rs = Replayer.Replay(path, TestData.Ship, Dyn, rescoreTargets: true);
        Assert.True(rs.HashMatches && rs.EventSequenceMatches);
        Assert.Null(rs.SummaryMatches);
        Assert.Equal(259, StopTime(rs.Summary!).Target);
        Assert.Equal(ManoeuvreTargets.For("FSB1")!.Sha256, rs.Summary!.Manoeuvres!.Single().TargetsSha256);
        Assert.Equal(recorded.ParameterSet?.Sha256, rs.Summary.ParameterSet?.Sha256); // 未給參數集:沿用標頭參數值
    }

    [Fact]
    public void OldRecordWithoutHeaderTargetsFallsBackToFileWithWarning()
    {
        var path = Record(AlteredTargets());
        // 模擬舊紀錄:移除 header.manoeuvreTargets(完整性雜湊因而不符,重播仍可進行)
        var lines = File.ReadAllLines(path);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        Assert.True(header.Remove("manoeuvreTargets"));
        lines[0] = header.ToJsonString();
        var old = Path.Combine(TestData.TestRecordsDirectory, $"targets-old-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(old, lines);

        var r = Replayer.Replay(old, TestData.Ship, Dyn);
        Assert.True(r.HashMatches && r.EventSequenceMatches);
        Assert.Equal("file", r.ManoeuvreTargetsSource);
        Assert.Contains(r.Notes, n => n.Contains("舊紀錄") && n.Contains("trial_targets.json"));
        Assert.False(r.SummaryMatches);                               // 錄製時的目標值(999)已不可得:比較改用目前檔案
        Assert.Equal(259, StopTime(r.Summary!).Target);
    }

    [Fact]
    public void ScenarioWithoutManoeuvresWritesNoHeaderTargets()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"targets-none-{Guid.NewGuid():N}.jsonl");
        var e = TestData.Engine();
        using (var w = new RecordWriter(path, e))
        {
            TestData.RunTo(e, 5);
            w.Close();
        }
        Assert.DoesNotContain("manoeuvreTargets", File.ReadLines(path).First());
        var r = Replayer.Replay(path, TestData.Ship);
        Assert.Null(r.ManoeuvreTargetsSource);
        Assert.Empty(r.Notes);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void DefaultRecordingEmbedsCurrentFile()
    {
        var path = Record(null);
        var h = RecordReader.Read(path).Header.ManoeuvreTargets!;
        var file = Path.Combine(TestData.Root, "data", "ships", "FSB1", "trial_targets.json");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), h.Sha256);
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(doc.RootElement.GetProperty("condition").GetString(), h.Content.GetProperty("condition").GetString());
        var r = Replayer.Replay(path, TestData.Ship, Dyn);
        Assert.True(r.SummaryMatches);
        Assert.Equal("header", r.ManoeuvreTargetsSource);
        Assert.Empty(r.Notes);
    }
}
