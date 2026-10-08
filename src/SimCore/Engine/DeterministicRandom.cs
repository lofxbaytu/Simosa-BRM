namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 以 <see cref="Random"/>(seed) 產生的確定性亂數(CLAUDE.md:所有亂數由情境種子產生)。
/// <see cref="Random"/> 不能匯出內部狀態,因此快照保存 (seed, 已抽取次數),還原時重建並跳過相同次數;
/// 只允許透過 <see cref="NextDouble"/> 抽取,確保重建序列一致。
/// </summary>
public sealed class DeterministicRandom
{
    private Random _rng;

    public int Seed { get; }
    /// <summary>已抽取次數(快照內容)</summary>
    public long Count { get; private set; }

    public DeterministicRandom(int seed, long skip = 0)
    {
        Seed = seed;
        _rng = new Random(seed);
        for (long i = 0; i < skip; i++) _rng.NextDouble();
        Count = skip;
    }

    public double NextDouble()
    {
        Count++;
        return _rng.NextDouble();
    }

    /// <summary>重建到指定抽取次數(還原快照)。</summary>
    public void ResetTo(long count)
    {
        _rng = new Random(Seed);
        for (long i = 0; i < count; i++) _rng.NextDouble();
        Count = count;
    }
}
