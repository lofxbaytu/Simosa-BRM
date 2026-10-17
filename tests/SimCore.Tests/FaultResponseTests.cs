using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>故障應變時間戳(情境引擎設計第 4.6 節)。</summary>
public class FaultResponseTests
{
    private const string Rules = """
        timeline:
          - id: jam
            when: { type: time, atLeast: 100 }
            actions: [ { injectFault: steeringGear } ]
        assessment:
          required:
            - id: first
              after: { event: faultInjected, itemId: jam }
              expect: { any: [ telegraphChange, thrusterOrder, { acknowledge: { kind: steeringMode } } ] }
              within_s: 60
              penalty: 20
            - id: ecr
              after: { event: faultInjected, itemId: jam }
              expect: { acknowledge: { kind: notifyEcr } }
              within_s: 120
              penalty: 10
        """;

    [Fact]
    public void TelegraphAfter35SecondsIsFirstActionAndAcknowledgementsMatch()
    {
        var e = TestData.MinimalEngine(Rules);
        TestData.RunTo(e, 135);
        e.Enqueue(SimCommand.Rudder(10)); // 舵令不在 expect 內 → 不算首個相關動作
        e.ProcessPendingCommands();
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 182);
        e.Enqueue(SimCommand.Acknowledge("notifyEcr", role: "master")); // 無 itemId → 最近 120 s 內注入者
        e.ProcessPendingCommands();
        TestData.RunTo(e, 190);
        e.Enqueue(SimCommand.Acknowledge("verbal", itemId: "jam", role: "officer"));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 200);
        var rec = e.Assessment!.Faults.State.Records.Single();
        Assert.Equal("steeringGear", rec.Fault);
        Assert.Equal("jam", rec.ItemId);
        Assert.Equal(100.0, rec.InjectedAtS);
        Assert.Equal(SimEventSources.Timeline, rec.Source);
        Assert.Equal("telegraphChange", rec.FirstAction!.Kind);
        Assert.Equal(35.0, rec.FirstAction.DtS, 9);
        Assert.True(rec.WithinFirstAction);
        Assert.Equal(2, rec.Acknowledgements.Count);
        Assert.Equal("notifyEcr", rec.Acknowledgements[0].Kind);
        Assert.Equal(82.0, rec.Acknowledgements[0].DtS, 9);
        Assert.Equal("master", rec.Acknowledgements[0].Role);
        Assert.True(rec.WithinNotifyEcr);
        Assert.Equal("verbal", rec.Acknowledgements[1].Kind);
        Assert.Null(rec.ClearedS);
        var summary = e.LatestSummary!;
        Assert.Equal("met", summary.Assessment.Required.Single(r => r.Id == "first").State);
        Assert.Equal(35.0, summary.Assessment.Required.Single(r => r.Id == "first").DtS!.Value, 9);
        Assert.Equal("telegraphChange", summary.Assessment.Required.Single(r => r.Id == "first").By);
        Assert.Equal("met", summary.Assessment.Required.Single(r => r.Id == "ecr").State);
        Assert.Equal("acknowledge:notifyEcr", summary.Assessment.Required.Single(r => r.Id == "ecr").By);
    }

    [Fact]
    public void InstructorInjectionAlsoCreatesRecordAndDefaultRelevantActions()
    {
        var e = TestData.MinimalEngine();
        TestData.RunTo(e, 10);
        e.Enqueue(SimCommand.InjectFault(FaultNames.MainEngine));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 22);
        e.Enqueue(SimCommand.Rudder(15)); // 無規則時任一舵/車/側推/自動舵指令皆算
        e.ProcessPendingCommands();
        TestData.RunTo(e, 40);
        e.Enqueue(SimCommand.ClearFault(FaultNames.MainEngine));
        e.ProcessPendingCommands();
        var rec = e.Assessment!.Faults.State.Records.Single();
        Assert.Equal(SimEventSources.Instructor, rec.Source);
        Assert.Null(rec.ItemId);
        Assert.Equal("rudderOrder", rec.FirstAction!.Kind);
        Assert.Equal(12.0, rec.FirstAction.DtS, 9);
        Assert.Equal(40.0, rec.ClearedS);
        Assert.Null(rec.WithinFirstAction);
        Assert.Single(e.LatestSummary!.FaultResponses);
        // 重複注入同名故障不新建紀錄
        e.Enqueue(SimCommand.InjectFault("gyroDrift"));
        e.Enqueue(SimCommand.InjectFault("gyroDrift"));
        e.ProcessPendingCommands();
        Assert.Equal(2, e.Assessment.Faults.State.Records.Count);
    }
}
