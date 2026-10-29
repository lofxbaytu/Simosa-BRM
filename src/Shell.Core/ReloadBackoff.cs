// 頁面載入失敗(網頁伺服尚未啟動、暫時斷線)的自動重試退避:指數增加、有上限,成功後歸零;外殼以 Timer 依此排程 Navigate。
namespace SimosaBRM.Shell.Core;

public sealed class ReloadBackoff
{
    public static readonly TimeSpan DefaultInitial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultMax = TimeSpan.FromSeconds(15);

    public ReloadBackoff(TimeSpan? initial = null, TimeSpan? max = null, double factor = 2.0)
    {
        Initial = initial ?? DefaultInitial;
        Max = max ?? DefaultMax;
        if (Initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial), "初始延遲須為正");
        if (Max < Initial) throw new ArgumentOutOfRangeException(nameof(max), "上限不可小於初始延遲");
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor), "倍率不可小於 1");
        Factor = factor;
    }

    public TimeSpan Initial { get; }
    public TimeSpan Max { get; }
    public double Factor { get; }
    /// <summary>目前連續失敗次數(成功後 <see cref="Reset"/> 歸零)。</summary>
    public int Attempt { get; private set; }

    /// <summary>第 n 次失敗(n ≥ 1)後的等待時間:Initial × Factor^(n−1),不超過 Max。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "次數自 1 起算");
        var ms = Initial.TotalMilliseconds * Math.Pow(Factor, attempt - 1);
        if (double.IsInfinity(ms) || ms > Max.TotalMilliseconds) return Max;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>記一次失敗並回傳這次應等待的時間。</summary>
    public TimeSpan NextDelay() => DelayFor(++Attempt);

    public void Reset() => Attempt = 0;

    /// <summary>標題列/log 用的中文說明,例如「載入失敗(第 3 次),4 秒後重試」。</summary>
    public static string Describe(int attempt, TimeSpan delay, string? reason = null)
    {
        var secs = delay.TotalSeconds >= 10 ? $"{delay.TotalSeconds:0}" : $"{delay.TotalSeconds:0.#}";
        var r = string.IsNullOrWhiteSpace(reason) ? "" : $":{reason}";
        return $"載入失敗(第 {attempt} 次{r}),{secs} 秒後重試";
    }
}
