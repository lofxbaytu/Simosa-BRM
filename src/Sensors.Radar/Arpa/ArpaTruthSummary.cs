namespace SimosaBRM.Sensors.Radar.Arpa;

/// <summary>
/// 雷達服務結束摘要的 ARPA 對真值統計(Sensors.Radar.Host 的「CPA 誤差」「最後追蹤數」與各配對的最後一筆),**有上限**:
/// 每台雷達只留累計值(筆數、總和、最大值;全部與穩定追蹤各一組)、最後一筆追蹤數,以及每組(追蹤編號, 真值目標)配對的最後一筆。
/// <para>
/// 修正前(第三十輪 2 小時搜尋 radar-R2)主控程式把每次追蹤清單的每筆比對都加進兩個 List,只在程序結束時讀取:
/// E02 ×4 實測 cpaErrors 6.4 筆/模擬秒、trackCounts 2 筆/模擬秒,2 小時約 4 MB(交通繁忙時約 28 MB,倍增時新舊陣列同在 LOH)。
/// 配對數受追蹤編號(1–99)× 真值目標數 × 雷達數限制,不隨時間成長。摘要輸出的格式與數值和修正前相同
/// (平均以同樣的加總順序計算;配對依追蹤編號排序,同編號依第一次出現的順序,與修正前的 GroupBy + OrderBy 相同)。
/// </para>
/// 非執行緒安全:由雷達處理執行緒(TracksPublished)寫入、程序結束時讀取。
/// </summary>
public sealed class ArpaTruthSummary
{
    /// <summary>一組 CPA 誤差的累計值</summary>
    public sealed class ErrorStats
    {
        public long Count { get; private set; }
        public double Sum { get; private set; }
        /// <summary>最大值(與 LINQ Max 相同:略過 NaN,全部是 NaN 時為 NaN;沒有資料時 NaN)</summary>
        public double Max { get; private set; } = double.NaN;
        /// <summary>平均(與 LINQ Average 相同的加總順序;沒有資料時 NaN)</summary>
        public double Average => Count == 0 ? double.NaN : Sum / Count;

        internal void Add(double err)
        {
            Count++;
            Sum += err;
            if (double.IsNaN(Max) || err > Max) Max = err;
        }
    }

    /// <summary>某組(追蹤編號, 真值目標)配對的最後一筆比對</summary>
    public readonly record struct PairLast(int TrackId, string TruthId, double T, double Err, bool Stable);

    /// <summary>某台雷達最後一次追蹤清單的追蹤數</summary>
    public readonly record struct TrackCount(double T, int Tracking, int Total);

    private sealed class RadarStats
    {
        public readonly ErrorStats All = new();
        public readonly ErrorStats Stable = new();
        public TrackCount? LastCount;
        /// <summary>配對 → 在 <see cref="Pairs"/> 的位置(第一次出現的順序)</summary>
        public readonly Dictionary<(int Id, string Truth), int> PairIndex = new();
        public readonly List<PairLast> Pairs = new();
    }

    private readonly Dictionary<string, RadarStats> _radars = new(StringComparer.Ordinal);

    private RadarStats For(string radarId)
    {
        if (!_radars.TryGetValue(radarId, out var r)) _radars[radarId] = r = new RadarStats();
        return r;
    }

    /// <summary>記錄一次追蹤清單的追蹤數(摘要只用最後一筆)。</summary>
    public void AddTrackCount(string radarId, double t, int tracking, int total) => For(radarId).LastCount = new TrackCount(t, tracking, total);

    /// <summary>記錄一筆追蹤中目標對真值的 CPA 誤差。</summary>
    public void AddCpaError(string radarId, double t, int trackId, string truthId, double err, bool stable)
    {
        var r = For(radarId);
        r.All.Add(err);
        if (stable) r.Stable.Add(err);
        var key = (trackId, truthId);
        var last = new PairLast(trackId, truthId, t, err, stable);
        if (r.PairIndex.TryGetValue(key, out var i)) r.Pairs[i] = last;
        else
        {
            r.PairIndex[key] = r.Pairs.Count;
            r.Pairs.Add(last);
        }
    }

    /// <summary>全部追蹤中比對的 CPA 誤差(沒有資料時 Count = 0)</summary>
    public ErrorStats AllErrors(string radarId) => _radars.TryGetValue(radarId, out var r) ? r.All : new ErrorStats();

    /// <summary>穩定追蹤(≥ StableTimeS)比對的 CPA 誤差</summary>
    public ErrorStats StableErrors(string radarId) => _radars.TryGetValue(radarId, out var r) ? r.Stable : new ErrorStats();

    /// <summary>最後一次追蹤清單的追蹤數(沒有時 null)</summary>
    public TrackCount? LastTrackCount(string radarId) => _radars.TryGetValue(radarId, out var r) ? r.LastCount : null;

    /// <summary>各配對的最後一筆,依追蹤編號排序(同編號依第一次出現的順序)</summary>
    public IReadOnlyList<PairLast> LastByPair(string radarId)
        => _radars.TryGetValue(radarId, out var r) ? r.Pairs.OrderBy(p => p.TrackId).ToList() : Array.Empty<PairLast>();

    /// <summary>目前保留的配對數(全部雷達;測試/診斷用:不隨比對筆數成長)</summary>
    public int PairCount => _radars.Values.Sum(r => r.Pairs.Count);
}
