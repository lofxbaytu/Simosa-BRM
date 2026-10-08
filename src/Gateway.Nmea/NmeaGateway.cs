using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 掛在引擎上,依句型速率產生 NMEA(HDT/ROT/RSA 10 Hz,其餘 1 Hz;規劃書第 5.3 節),時間 = 情境起始 UTC + 模擬時間。
/// 目標船:每目標 TTM 1 Hz;AIS !AIVDM 類型 1 位置報告每 10 s(固定,不依航速縮短)、類型 5 靜態資料每 6 分,
/// 各目標依其在 targets[] 的序號錯開秒數;AIS 關閉或不發送(錯誤注入 silent)的目標只有 TTM。在引擎執行緒上執行;輸出端應避免阻塞。
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
    /// <summary>AIS 位置報告間隔(秒)</summary>
    public int AisPositionIntervalS { get; init; } = 10;
    /// <summary>AIS 靜態資料間隔(秒)</summary>
    public int AisStaticIntervalS { get; init; } = 360;

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
        if (slow)
        {
            batch.AddRange(NmeaEncoder.SlowSentences(state, utc));
            if (state.Targets is { Count: > 0 } targets)
            {
                batch.AddRange(NmeaEncoder.TtmSentences(state, utc));
                var sec = e.Tick / _slowDivider; // 整數秒(避免浮點時間的取整誤差)
                for (var i = 0; i < targets.Count; i++)
                {
                    var ais = targets[i].Ais;
                    if (ais is null) continue;
                    var offset = i % AisPositionIntervalS;
                    if (sec % AisPositionIntervalS == offset) batch.AddRange(AisEncoder.PositionReport(ais, utc, i % 2 == 0 ? 'A' : 'B'));
                    if (sec % AisStaticIntervalS == offset) batch.AddRange(AisEncoder.StaticAndVoyage(ais, i % 9 + 1, i % 2 == 0 ? 'A' : 'B'));
                }
            }
        }
        SentencesSent += batch.Count;
        _sink.Send(batch);
    }

    public void Dispose()
    {
        _engine.Stepped -= OnStepped;
        _sink.Dispose();
    }
}
