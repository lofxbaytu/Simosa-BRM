namespace SimosaBRM.Visual.Shared.Quality;

/// <summary>品質等級改變的紀錄(寫入 build/visual/quality.log 與 HUD)。</summary>
public sealed record QualityChange(double AtSeconds, QualityTier From, QualityTier To, string Reason);

/// <summary>
/// 執行中的自動降級(規劃書第 7.3 節、第 14 章:幀率連續低於 25 fps 時自動降一級並記入紀錄)。
/// 遲滯:以每幀 dt 累積「低於門檻的連續秒數」,任何一幀 ≥ 門檻 + 回復餘量(ReleaseFps)即歸零;
/// 達 SustainSeconds 才降級,降級後有 CooldownSeconds 不再評估(新設定需要時間生效);不自動升級(避免來回跳動),
/// 使用者可手動覆寫(覆寫後停用自動降級,直到清除覆寫)。
/// </summary>
public sealed class AdaptiveQualityController
{
    private double _lowSeconds;
    private double _cooldownUntil = double.NegativeInfinity;
    private readonly List<QualityChange> _changes = new();

    public QualityTier Current { get; private set; }
    public QualityTier? ManualOverride { get; private set; }
    public double ThresholdFps { get; init; } = 25.0;
    /// <summary>歸零門檻(避免 24.9/25.1 來回):幀率 ≥ 此值才算回復</summary>
    public double ReleaseFps { get; init; } = 27.0;
    public double SustainSeconds { get; init; } = 5.0;
    public double CooldownSeconds { get; init; } = 30.0;
    /// <summary>忽略過大的 dt(載入卡頓、視窗拖曳)</summary>
    public double MaxFrameSeconds { get; init; } = 0.5;
    public IReadOnlyList<QualityChange> Changes => _changes;
    public double LowSeconds => _lowSeconds;
    public event Action<QualityChange>? Changed;

    public AdaptiveQualityController(QualityTier initial)
    {
        Current = initial;
    }

    /// <summary>每顯示幀呼叫:dt 為該幀耗時(秒)、now 為執行秒數。回傳 true 表示本幀降級了。</summary>
    public bool Update(double dt, double now)
    {
        if (ManualOverride is not null) return false;
        if (dt <= 0 || dt > MaxFrameSeconds) return false;
        if (now < _cooldownUntil) { _lowSeconds = 0; return false; }
        var fps = 1.0 / dt;
        if (fps < ThresholdFps) _lowSeconds += dt;
        else if (fps >= ReleaseFps) _lowSeconds = 0;
        // 介於 Threshold 與 Release 之間:維持累積(遲滯帶)
        if (_lowSeconds < SustainSeconds) return false;
        if (Current == QualityTier.Low) { _lowSeconds = 0; return false; }
        var from = Current;
        Current -= 1;
        _lowSeconds = 0;
        _cooldownUntil = now + CooldownSeconds;
        var change = new QualityChange(now, from, Current, $"幀率連續 {SustainSeconds:F0} s 低於 {ThresholdFps:F0} fps,自動降級");
        _changes.Add(change);
        Changed?.Invoke(change);
        return true;
    }

    /// <summary>手動覆寫(null = 清除覆寫並回到自動)。</summary>
    public void SetOverride(QualityTier? tier, double now)
    {
        ManualOverride = tier;
        _lowSeconds = 0;
        if (tier is { } t && t != Current)
        {
            var change = new QualityChange(now, Current, t, "手動覆寫");
            Current = t;
            _changes.Add(change);
            Changed?.Invoke(change);
        }
        if (tier is null) _cooldownUntil = now + CooldownSeconds; // 回到自動後給一段觀察期
    }

    /// <summary>自動偵測結果套用(啟動時)。</summary>
    public void SetDetected(QualityTier tier, double now, string reason)
    {
        if (ManualOverride is not null || tier == Current) return;
        var change = new QualityChange(now, Current, tier, reason);
        Current = tier;
        _changes.Add(change);
        Changed?.Invoke(change);
    }
}
