using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>
/// 掛在引擎上,依句型速率產生 NMEA(HDT/ROT/RSA 10 Hz,其餘 1 Hz;規劃書第 5.3 節),時間 = 情境起始 UTC + 模擬時間。
/// 位置/航向/水深/STW 取 state 的指示值(感測器故障時與真值不同或無效,見 NmeaEncoder);警報 ALR 1 Hz。
/// 目標船:每目標 TTM 1 Hz;AIS !AIVDM 類型 1 位置報告每 10 s(固定,不依航速縮短)、類型 5 靜態資料於出現後數秒先送一次再每 6 分,
/// 各目標依其在 targets[] 的序號錯開秒數;AIS 關閉或不發送(錯誤注入 silent)的目標只有 TTM。在引擎執行緒上執行;輸出端應避免阻塞。
/// 起始 UTC:建構時給定則固定(測試);未給(主控程序)則每步取引擎的 <see cref="SimulationEngine.StartUtc"/>,
/// loadScenario 切換情境後 NMEA 時間與 state.utc 同步改為新情境的起點(<c>nmea_check</c> 的位置/時間一致性檢查依此)。
/// </summary>
public sealed class NmeaGateway : IDisposable
{
    private readonly SimulationEngine _engine;
    private readonly INmeaSink _sink;
    private readonly int _fastDivider;
    private readonly int _slowDivider;
    private readonly DateTime? _fixedStartUtc;
    private readonly DateTime _fallbackStartUtc;

    /// <summary>練習開始的 UTC 時刻(固定值,或目前引擎情境的起點;引擎無起點時為建構時的實際時鐘)</summary>
    public DateTime StartUtc => _fixedStartUtc ?? _engine.StartUtc ?? _fallbackStartUtc;
    public long SentencesSent { get; private set; }
    /// <summary>AIS 位置報告間隔(秒)</summary>
    public int AisPositionIntervalS { get; init; } = 10;
    /// <summary>AIS 靜態資料間隔(秒)</summary>
    public int AisStaticIntervalS { get; init; } = 360;

    /// <param name="startUtc">固定的起始 UTC;null = 跟隨引擎(情境 startTimeUtc,切換情境時更新)</param>
    public NmeaGateway(SimulationEngine engine, INmeaSink sink, DateTime? startUtc = null, double fastHz = 10.0, double slowHz = 1.0)
    {
        _engine = engine;
        _sink = sink;
        _fixedStartUtc = startUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null;
        _fallbackStartUtc = DateTime.UtcNow;
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
            batch.AddRange(NmeaEncoder.AlrSentences(state, utc)); // 警報狀態 1 Hz(BAM;每個非 normal 的警報一句)
            if (state.Targets is { Count: > 0 } targets)
            {
                batch.AddRange(NmeaEncoder.TtmSentences(state, utc));
                var sec = e.Tick / _slowDivider; // 整數秒(避免浮點時間的取整誤差)
                for (var i = 0; i < targets.Count; i++)
                {
                    var ais = targets[i].Ais;
                    if (ais is null) continue;
                    // 位置:第 i 艘在 sec ≡ i (mod 10);靜態:第 2+i 秒先送一次(讓 ECDIS 儘早取得船名),之後每 6 分
                    if ((sec - i) % AisPositionIntervalS == 0) batch.AddRange(AisEncoder.PositionReport(ais, utc, i % 2 == 0 ? 'A' : 'B'));
                    var staticAge = sec - 2 - i;
                    if (staticAge >= 0 && staticAge % AisStaticIntervalS == 0) batch.AddRange(AisEncoder.StaticAndVoyage(ais, i % 9 + 1, i % 2 == 0 ? 'A' : 'B'));
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
