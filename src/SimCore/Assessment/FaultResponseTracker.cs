using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Assessment;

public sealed class FaultResponseState
{
    public List<FaultResponseRecord> Records { get; set; } = new();
}

/// <summary>
/// 故障應變追蹤(第 4.6 節):每個 faultInjected(時間表或教官)建立紀錄;「相關動作」取 assessment.required[] 中 after 指向該故障的 expect,
/// 無規則時預設任一 telegraph/rpm/thruster/rudder/autopilot/steeringMode 指令(操舵模式變更 steeringModeChanged 以 ObservedAction steeringModeChange 進入);
/// acknowledge 可帶 itemId 或對應最近 120 s 內注入且未確認者。
/// </summary>
public sealed class FaultResponseTracker
{
    private readonly ScenarioAssessment _assessment;
    private readonly double _dt;

    public FaultResponseState State { get; private set; } = new();

    public FaultResponseTracker(ScenarioAssessment assessment, double dt)
    {
        _assessment = assessment;
        _dt = dt;
    }

    private IEnumerable<RequiredEvent> RulesFor(FaultResponseRecord rec)
        => _assessment.Required.Where(r => r.After.Event == SimEventKinds.FaultInjected && (r.After.ItemId is null || r.After.ItemId == rec.ItemId));

    public void OnEvent(SimEvent e)
    {
        switch (e.Kind)
        {
            case SimEventKinds.FaultInjected:
                if (e.DataBool("alreadyActive") == true) return;
                State.Records.Add(new FaultResponseRecord
                {
                    Fault = e.DataString("fault") ?? "", ItemId = e.ItemId, InjectedAtS = e.T, Source = e.Source,
                });
                break;
            case SimEventKinds.FaultCleared:
            {
                var name = e.DataString("fault");
                foreach (var rec in State.Records)
                    if (rec.ClearedS is null && (name is null || rec.Fault == name)) rec.ClearedS = e.T;
                break;
            }
            case SimEventKinds.Acknowledge:
            {
                var kind = e.DataString("kind") ?? "other";
                var role = e.DataString("role");
                var itemId = e.DataString("itemId") ?? e.ItemId;
                FaultResponseRecord? target = null;
                if (itemId is not null) target = State.Records.LastOrDefault(r => r.ItemId == itemId);
                target ??= State.Records.LastOrDefault(r => e.T - r.InjectedAtS <= 120.0 && !r.Acknowledgements.Any(a => a.Kind == kind));
                if (target is null) return;
                var dt = e.T - target.InjectedAtS;
                target.Acknowledgements.Add(new FaultAcknowledgement { Kind = kind, TS = e.T, DtS = dt, Role = role });
                if (kind == "notifyEcr")
                {
                    var rule = RulesFor(target).FirstOrDefault(r => r.Expect.Leaves().Any(l => l.Acknowledge?.Kind == "notifyEcr"));
                    if (rule is not null) target.WithinNotifyEcr = dt <= rule.WithinS;
                }
                if (target.FirstAction is null && MatchesFirstAction(target, null, kind)) SetFirstAction(target, $"acknowledge:{kind}", e.T);
                break;
            }
        }
    }

    public void OnAction(ObservedAction a)
    {
        foreach (var rec in State.Records)
        {
            if (rec.FirstAction is not null) continue;
            if (!MatchesFirstAction(rec, a.Kind, null)) continue;
            if (a.Kind == ActionKinds.RudderOrder && a.Magnitude < 1.0) continue;
            if (a.Kind == ActionKinds.ThrusterOrder && a.Magnitude < 0.05) continue;
            SetFirstAction(rec, a.Kind, a.Tick * _dt);
        }
    }

    private void SetFirstAction(FaultResponseRecord rec, string kind, double t)
    {
        var dt = t - rec.InjectedAtS;
        rec.FirstAction = new FaultAction { Kind = kind, TS = t, DtS = dt };
        var rule = RulesFor(rec).FirstOrDefault(r => r.Expect.Leaves().Any(l => l.Name is not null && l.Name != "acknowledge"));
        if (rule is not null) rec.WithinFirstAction = dt <= rule.WithinS;
    }

    /// <summary>動作(或確認 kind)是否為該故障的「相關動作」。</summary>
    private bool MatchesFirstAction(FaultResponseRecord rec, string? actionKind, string? ackKind)
    {
        var rules = RulesFor(rec).ToList();
        if (rules.Count == 0)
            return actionKind is ActionKinds.TelegraphChange or ActionKinds.RpmOrder or ActionKinds.ThrusterOrder or ActionKinds.RudderOrder
                or ActionKinds.AutopilotOn or ActionKinds.AutopilotOff or ActionKinds.SteeringModeChange;
        foreach (var leaf in rules.SelectMany(r => r.Expect.Leaves()))
        {
            if (actionKind is not null && leaf.Name is { } n && (n == actionKind || (n == ActionKinds.SteeringModeChange && actionKind is ActionKinds.AutopilotOff))) return true;
            if (ackKind is not null && leaf.Acknowledge is { } ack && (ack.Kind is null || ack.Kind == ackKind)) return true;
            if (ackKind is not null && leaf.Name == "acknowledge") return true;
            if (ackKind == "steeringMode" && leaf.Name == ActionKinds.SteeringModeChange) return true;
        }
        return false;
    }

    public void Restore(FaultResponseState s) => State = s;
}
