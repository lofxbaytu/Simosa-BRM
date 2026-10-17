using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Recording;

public sealed record ReplayResult
{
    public required string Path { get; init; }
    public required bool IntegrityOk { get; init; }
    public required string? ExpectedHash { get; init; }
    public required string ActualHash { get; init; }
    public required long FinalTick { get; init; }
    public required long InputsReplayed { get; init; }
    public required string DynamicsRecorded { get; init; }
    public required string DynamicsUsed { get; init; }
    public string? Warning { get; init; }
    public bool HashMatches => ExpectedHash is not null && string.Equals(ExpectedHash, ActualHash, StringComparison.OrdinalIgnoreCase);
    /// <summary>紀錄中的事件數</summary>
    public long EventsRecorded { get; init; }
    /// <summary>重播產生的事件數</summary>
    public long EventsReplayed { get; init; }
    /// <summary>事件序列(seq、kind、tick、itemId、targetId)逐一相同</summary>
    public bool EventSequenceMatches { get; init; }
    /// <summary>重播最終的 summary JSON 與紀錄最後一行 summary 相同(紀錄無 summary 時為 null)</summary>
    public bool? SummaryMatches { get; init; }
    /// <summary>重播產生的最終摘要(--rescore 時為新參數集的評分)</summary>
    public Assessment.AssessmentSummary? Summary { get; init; }
    /// <summary>事件序列第一個不一致處的描述</summary>
    public string? FirstEventMismatch { get; init; }
}

/// <summary>
/// 重播:以紀錄檔標頭的情境重建引擎,依序在相同 tick 套用相同指令(與主控程序相同:在該 tick 推進前立即套用,倒帶指令因此立即生效),
/// 推進到相同 tick,結果雜湊必須與紀錄一致(規劃書第 5.1 節原則 1、第 14 章確定性驗收)。
/// 紀錄中若出現 <c>loadScenario</c> 指令(舊版紀錄;現行紀錄器在情境切換前關閉檔案、主控程序另開新檔),重播在該行中止並以 Warning 說明。
/// </summary>
public static class Replayer
{
    /// <summary>
    /// <paramref name="shipLoader"/> 依船舶 ID 提供 particulars;<paramref name="configure"/> 可在重播前掛上觀察者(例如再錄一份)。
    /// </summary>
    /// <param name="rescore">以另一參數集重新評分:物理、交通、時間表與雜湊仍依紀錄的原情境執行(雜湊與事件序列不變),
    /// 另以新參數集的門檻建立影子評估模組,<see cref="ReplayResult.Summary"/> 為其評分(情境引擎設計第 7.4 節)</param>
    public static ReplayResult Replay(string path, Func<string, ShipParticulars> shipLoader,
        Func<ShipParticulars, LoadingCondition, IShipDynamics>? dynamicsFactory = null,
        Action<SimulationEngine>? configure = null, Action<SimulationEngine>? onStep = null, Parameters.ParameterSet? rescore = null)
    {
        var rec = RecordReader.Read(path);
        var engine = CreateEngine(rec, shipLoader, dynamicsFactory, rescore);
        configure?.Invoke(engine);
        if (onStep is not null) engine.Stepped += onStep;
        var replayedEvents = new List<SimEvent>();
        engine.EventRaised += (_, ev) => replayedEvents.Add(ev);

        string? warning = null;
        long replayed = 0;
        var aborted = false;
        foreach (var input in rec.Inputs)
        {
            if (!AdvanceTo(engine, input.Tick, ref warning)) { aborted = true; break; }
            if (input.Command.Type == SimCommandType.LoadScenario)
            {
                warning = $"紀錄在 tick {input.Tick} 切換情境(loadScenario),之後的內容屬於另一情境,無法由此紀錄重播;重播在該 tick 中止";
                aborted = true;
                break;
            }
            // 引擎 tick 已等於紀錄 tick:立即套用(與主控程序在該 tick 的 Step 開頭 / 凍結中直接套用等價,事件 tick 相同);
            // restore/reset 倒帶後,下一筆 input 的 AdvanceTo 才會從倒帶後的 tick 推進,指令各自套在紀錄的 tick
            engine.Enqueue(input.Command);
            engine.ProcessPendingCommands();
            replayed++;
        }
        if (!aborted && rec.Footer is { } f) AdvanceTo(engine, f.FinalTick, ref warning);

        // 事件序列比對(seq、kind、tick、itemId、targetId)
        var recorded = rec.Events.Select(e => e.Event).ToList();
        string? mismatch = null;
        var n = Math.Min(recorded.Count, replayedEvents.Count);
        for (var i = 0; i < n; i++)
        {
            var a = recorded[i];
            var b = replayedEvents[i];
            if (a.Seq != b.Seq || a.Kind != b.Kind || a.Tick != b.Tick || a.ItemId != b.ItemId || a.TargetId != b.TargetId)
            {
                mismatch = $"事件 #{i}:紀錄 {a.Kind}@{a.Tick}({a.ItemId ?? a.TargetId}) / 重播 {b.Kind}@{b.Tick}({b.ItemId ?? b.TargetId})";
                break;
            }
        }
        if (mismatch is null && recorded.Count != replayedEvents.Count) mismatch = $"事件數不同:紀錄 {recorded.Count} / 重播 {replayedEvents.Count}";

        Assessment.AssessmentSummary? summary = null;
        bool? summaryMatches = null;
        if ((engine.RescoreAssessment ?? engine.Assessment) is { } assessment)
        {
            summary = engine.Ended ? assessment.LatestSummary : assessment.BuildSummary(engine.Tick, final: true);
            if (rec.Summary is { } recSummary && rescore is null && summary is not null)
                summaryMatches = string.Equals(ContractJson.Serialize(recSummary.Summary), ContractJson.Serialize(summary), StringComparison.Ordinal);
        }

        return new ReplayResult
        {
            Path = path,
            IntegrityOk = rec.IntegrityOk,
            ExpectedHash = rec.Footer?.StateHash,
            ActualHash = engine.StateHash,
            FinalTick = engine.Tick,
            InputsReplayed = replayed,
            DynamicsRecorded = rec.Header.Dynamics,
            DynamicsUsed = engine.Dynamics.ModelName,
            Warning = warning ?? (rec.Header.Dynamics != engine.Dynamics.ModelName ? "動力學模型與紀錄不同,雜湊預期不一致" : null),
            EventsRecorded = recorded.Count,
            EventsReplayed = replayedEvents.Count,
            EventSequenceMatches = mismatch is null,
            FirstEventMismatch = mismatch,
            SummaryMatches = summaryMatches,
            Summary = summary,
        };
    }

    /// <summary>
    /// 依標頭情境重建引擎;<paramref name="rescore"/> 時引擎仍以標頭情境執行(物理/交通/時間表/雜湊不變),
    /// 另以新參數集代入標頭 parameterSet.resolved 的欄位得到重新評分情境,交給 <see cref="SimulationEngine.EnableRescore"/> 建立影子評估模組。
    /// </summary>
    public static SimulationEngine CreateEngine(RecordFile rec, Func<string, ShipParticulars> shipLoader,
        Func<ShipParticulars, LoadingCondition, IShipDynamics>? dynamicsFactory = null, Parameters.ParameterSet? rescore = null)
    {
        var scenario = rec.Header.Scenario;
        scenario.Validate();
        scenario.ParameterSetInfo = rec.Header.ParameterSet;
        var ship = shipLoader(scenario.Ship.Id);
        var engine = new SimulationEngine(ship, scenario, dynamicsFactory, new EngineOptions { Dt = rec.Header.Dt });
        if (rescore is not null)
        {
            var info = rec.Header.ParameterSet ?? throw new InvalidDataException("紀錄無參數引用,無法重新評分");
            var node = System.Text.Json.Nodes.JsonNode.Parse(scenario.ToJson())!;
            var resolved = Parameters.ParameterResolver.Rescore(node, info.Resolved, rescore);
            var rescored = Scenario.Scenario.FromJson(node.ToJsonString());
            rescored.ParameterSetInfo = rescore.ToInfo(resolved);
            engine.EnableRescore(rescored);
        }
        return engine;
    }

    /// <summary>推進到指定 tick;凍結且佇列已空時無法前進,回傳 false 並記錄警告。</summary>
    private static bool AdvanceTo(SimulationEngine engine, long tick, ref string? warning)
    {
        while (engine.Tick < tick)
        {
            var before = engine.Tick;
            engine.Step();
            if (engine.Tick == before && engine.Frozen && engine.PendingCommandCount == 0)
            {
                warning ??= $"紀錄在 tick {before} 凍結但後續事件要求 tick {tick},重播中止";
                return false;
            }
        }
        return true;
    }
}
