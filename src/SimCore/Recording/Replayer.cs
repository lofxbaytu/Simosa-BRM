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
}

/// <summary>
/// 重播:以紀錄檔標頭的情境重建引擎,依序在相同 tick 送入相同指令,推進到相同 tick,
/// 結果雜湊必須與紀錄一致(規劃書第 5.1 節原則 1、第 14 章確定性驗收)。
/// </summary>
public static class Replayer
{
    /// <summary>
    /// <paramref name="shipLoader"/> 依船舶 ID 提供 particulars;<paramref name="configure"/> 可在重播前掛上觀察者(例如再錄一份)。
    /// </summary>
    public static ReplayResult Replay(string path, Func<string, ShipParticulars> shipLoader,
        Func<ShipParticulars, LoadingCondition, IShipDynamics>? dynamicsFactory = null,
        Action<SimulationEngine>? configure = null, Action<SimulationEngine>? onStep = null)
    {
        var rec = RecordReader.Read(path);
        var engine = CreateEngine(rec, shipLoader, dynamicsFactory);
        configure?.Invoke(engine);
        if (onStep is not null) engine.Stepped += onStep;

        string? warning = null;
        long replayed = 0;
        foreach (var input in rec.Inputs)
        {
            if (!AdvanceTo(engine, input.Tick, ref warning)) break;
            engine.Enqueue(input.Command);
            replayed++;
        }
        if (rec.Footer is { } f) AdvanceTo(engine, f.FinalTick, ref warning);
        engine.ProcessPendingCommands();

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
        };
    }

    public static SimulationEngine CreateEngine(RecordFile rec, Func<string, ShipParticulars> shipLoader,
        Func<ShipParticulars, LoadingCondition, IShipDynamics>? dynamicsFactory = null)
    {
        var scenario = rec.Header.Scenario.Validate();
        var ship = shipLoader(scenario.Ship.Id);
        return new SimulationEngine(ship, scenario, dynamicsFactory, new EngineOptions { Dt = rec.Header.Dt });
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
