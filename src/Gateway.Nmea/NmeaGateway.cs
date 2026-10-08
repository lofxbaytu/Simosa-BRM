using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 掛在引擎上,依句型速率產生 NMEA(HDT/ROT/RSA 10 Hz,其餘 1 Hz;規劃書第 5.3 節),時間 = 情境起始 UTC + 模擬時間。
/// 在引擎執行緒上執行;輸出端應避免阻塞。
/// </summary>
public sealed class NmeaGateway : IDisposable
{
    private readonly SimulationEngine _engine;
    private readonly INmeaSink _sink;
    private readonly int _fastDivider;
    private readonly int _slowDivider;

    /// <summary>練習開始的 UTC 時刻</summary>
    public DateTime StartUtc { get; }
    public long SentencesSent { get; private set; }

    public NmeaGateway(SimulationEngine engine, INmeaSink sink, DateTime startUtc, double fastHz = 10.0, double slowHz = 1.0)
    {
        _engine = engine;
        _sink = sink;
        StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        _fastDivider = Math.Max(1, (int)Math.Round(1.0 / (engine.Dt * fastHz)));
        _slowDivider = Math.Max(1, (int)Math.Round(1.0 / (engine.Dt * slowHz)));
        engine.Stepped += OnStepped;
    }

    private void OnStepped(SimulationEngine e)
    {
        var fast = e.Tick % _fastDivider == 0;
        var slow = e.Tick % _slowDivider == 0;
        if (!fast && !slow) return;
        var state = e.BuildState();
        var utc = StartUtc.AddSeconds(e.Time);
        var batch = new List<string>(10);
        if (fast) batch.AddRange(NmeaEncoder.FastSentences(state));
        if (slow) batch.AddRange(NmeaEncoder.SlowSentences(state, utc));
        SentencesSent += batch.Count;
        _sink.Send(batch);
    }

    public void Dispose()
    {
        _engine.Stepped -= OnStepped;
        _sink.Dispose();
    }
}
