using SimosaBRM.Sensors.Radar.Video;

namespace SimosaBRM.Sensors.Radar.Arpa;

/// <summary>
/// 一個 ARPA 偵測(影像中的一團回波):強度加權質心(ENU m,已以指示航向/位置穩定化、距離已扣除半個脈衝長度的展寬偏差)、
/// 量測時間、徑向/橫向延伸與格數。Large = 陸地/雨區等大面積回波,不擷取。
/// </summary>
public readonly record struct Detection(
    double T, double X, double Y, double RangeM, double RadialExtentM, double ArcExtentM,
    int Cells, int Spokes, byte Peak, bool Large);

/// <summary>
/// 串流式連通元件標記(依天線掃描時間順序逐條方位線處理,跨 2047→0 的回波自然連續):
/// 每條線取強度 ≥ 門檻的連續段,與前一條線重疊(8 連通)的段合併;某元件在本條線未延伸即結束並輸出偵測。
/// 這等同於以門檻 + 叢集(規劃書第 5.2 節 ARPA)從影像擷取目標。
/// </summary>
internal sealed class BlobDetector
{
    private struct Comp
    {
        public int Parent;
        public double W, WX, WY, WT, WR, RMin, RMax;
        public long First, Last;
        public int Cells;
        public byte Peak;
        public int Stamp;
        public bool Closed;
    }

    private struct Run
    {
        public int C0, C1, Comp;
    }

    private readonly RadarConfig _cfg;
    private readonly VideoGrid _grid;
    private Comp[] _comps = new Comp[256];
    private readonly Stack<int> _free = new();
    private int _next;
    private List<Run> _prev = new();
    private List<Run> _curr = new();
    private readonly List<int> _merged = new();
    private long _lastSpoke = long.MinValue;
    private int _stamp;

    public BlobDetector(RadarConfig cfg, VideoGrid grid)
    {
        _cfg = cfg;
        _grid = grid;
    }

    private int Alloc()
    {
        var id = _free.Count > 0 ? _free.Pop() : _next++;
        if (id >= _comps.Length) Array.Resize(ref _comps, _comps.Length * 2);
        _comps[id] = new Comp { Parent = id, RMin = double.PositiveInfinity, RMax = double.NegativeInfinity, First = long.MaxValue, Last = long.MinValue };
        return id;
    }

    private int Find(int i)
    {
        while (_comps[i].Parent != i)
        {
            _comps[i].Parent = _comps[_comps[i].Parent].Parent;
            i = _comps[i].Parent;
        }
        return i;
    }

    private int Union(int a, int b)
    {
        a = Find(a);
        b = Find(b);
        if (a == b) return a;
        if (b < a) (a, b) = (b, a); // 確定性:保留較小編號為根
        ref var ca = ref _comps[a];
        ref readonly var cb = ref _comps[b];
        ca.W += cb.W; ca.WX += cb.WX; ca.WY += cb.WY; ca.WT += cb.WT; ca.WR += cb.WR;
        ca.RMin = Math.Min(ca.RMin, cb.RMin);
        ca.RMax = Math.Max(ca.RMax, cb.RMax);
        ca.First = Math.Min(ca.First, cb.First);
        ca.Last = Math.Max(ca.Last, cb.Last);
        ca.Cells += cb.Cells;
        if (cb.Peak > ca.Peak) ca.Peak = cb.Peak;
        _comps[b].Parent = a;
        _merged.Add(b);
        return a;
    }

    /// <summary>重設(停止發射、量程跳變、情境重載)。</summary>
    public void Reset()
    {
        _prev.Clear();
        _curr.Clear();
        _free.Clear();
        _merged.Clear();
        _next = 0;
        _lastSpoke = long.MinValue;
    }

    /// <summary>結束所有開啟中的元件並輸出。</summary>
    public void Flush(List<Detection> output)
    {
        _stamp++;
        foreach (var r in _prev) Close(Find(r.Comp), output);
        _prev.Clear();
        _free.Clear();
        _next = 0;
        _merged.Clear();
    }

    /// <summary>
    /// 處理一條 ARPA 影像方位線。antX/antY 為以指示位置/航向推得的天線 ENU 位置,headingRad 為指示航向(穩定化)。
    /// </summary>
    public void Process(long globalSpoke, int k, ReadOnlySpan<byte> video, double t, double antX, double antY, double headingRad, List<Detection> output)
    {
        if (globalSpoke != _lastSpoke + 1 && _prev.Count > 0) Flush(output);
        _lastSpoke = globalSpoke;
        _stamp++;
        var thr = _cfg.Arpa.DetectThreshold;
        var cell = _grid.CellM;
        var halfPulse = 0.5 * _grid.PulseM;
        var theta = headingRad + k * _cfg.SpokeAngleRad;
        var sinT = 0.0;
        var cosT = 0.0;
        var trigDone = false;
        _curr.Clear();
        var n = video.Length;
        var pi = 0;
        for (var i = 0; i < n; i++)
        {
            if (video[i] < thr) continue;
            var c0 = i;
            double w = 0, wr = 0;
            byte peak = 0;
            while (i < n && video[i] >= thr)
            {
                var v = video[i];
                var r = (i + 0.5) * cell - halfPulse;
                // 權重 = 門檻以上的強度(對數剖面,邊緣權重趨近 0 → 方位/距離取樣量化誤差小)
                var wv = v - thr + 1;
                w += wv;
                wr += wv * r;
                if (v > peak) peak = v;
                i++;
            }
            var c1 = i - 1;
            if (!trigDone) { sinT = Math.Sin(theta); cosT = Math.Cos(theta); trigDone = true; }
            // 與前一條線的重疊段(8 連通:相差 1 格亦相連)
            var comp = -1;
            while (pi < _prev.Count && _prev[pi].C1 < c0 - 1) pi++;
            for (var j = pi; j < _prev.Count && _prev[j].C0 <= c1 + 1; j++)
                comp = comp < 0 ? Find(_prev[j].Comp) : Union(comp, _prev[j].Comp);
            if (comp < 0) comp = Alloc();
            ref var cc = ref _comps[comp];
            cc.W += w;
            cc.WR += wr;
            cc.WX += w * antX + sinT * wr;
            cc.WY += w * antY + cosT * wr;
            cc.WT += w * t;
            cc.RMin = Math.Min(cc.RMin, c0 * cell);
            cc.RMax = Math.Max(cc.RMax, (c1 + 1) * cell);
            cc.First = Math.Min(cc.First, globalSpoke);
            cc.Last = Math.Max(cc.Last, globalSpoke);
            cc.Cells += c1 - c0 + 1;
            if (peak > cc.Peak) cc.Peak = peak;
            _curr.Add(new Run { C0 = c0, C1 = c1, Comp = comp });
        }
        // 正規化為根並標記本線有延伸的元件
        for (var j = 0; j < _curr.Count; j++)
        {
            var r = _curr[j];
            r.Comp = Find(r.Comp);
            _curr[j] = r;
            _comps[r.Comp].Stamp = _stamp;
        }
        // 前一條線的元件若本線未延伸 → 結束
        foreach (var r in _prev)
        {
            var root = Find(r.Comp);
            if (_comps[root].Stamp != _stamp) Close(root, output);
        }
        foreach (var m in _merged) _free.Push(m);
        _merged.Clear();
        (_prev, _curr) = (_curr, _prev);
    }

    private void Close(int root, List<Detection> output)
    {
        ref var c = ref _comps[root];
        if (c.Closed) return;
        c.Closed = true;
        _free.Push(root);
        var spokes = (int)(c.Last - c.First + 1);
        var a = _cfg.Arpa;
        if (c.Cells < a.MinCells || spokes < a.MinSpokes || c.W <= 0) return;
        var meanR = c.WR / c.W;
        var radial = c.RMax - c.RMin;
        var arc = spokes * _cfg.SpokeAngleRad * Math.Max(meanR, 0);
        var large = radial > a.LargeExtentM || arc > a.LargeExtentM || spokes > _cfg.Spokes / 8;
        output.Add(new Detection(c.WT / c.W, c.WX / c.W, c.WY / c.W, meanR, radial, arc, c.Cells, spokes, c.Peak, large));
    }
}
