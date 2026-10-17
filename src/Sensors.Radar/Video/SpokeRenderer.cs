namespace SimosaBRM.Sensors.Radar.Video;

/// <summary>接收機參數(每幀由操作員控制與故障狀態算出)。</summary>
internal readonly record struct ReceiverParams(
    /// <summary>靈敏度損失(線性;調諧偏差、性能劣化)</summary>
    double LossLin,
    /// <summary>增益門檻(dB,相對雜訊平均)</summary>
    double ThresholdDb,
    /// <summary>FTC 強度(0–0.9):減去前段平均的比例</summary>
    double FtcK,
    /// <summary>強度刻度(每 dB 的強度值):顯示 255/30(門檻以上 30 dB);ARPA 處理影像 255/80(較寬動態範圍,質心不受飽和影響)</summary>
    float Scale = 255f / 30f);

/// <summary>
/// 把一條方位線的回波幾何畫到一組距離格(線性功率累加 → 加入雜訊 → 對數 → STC → FTC → 增益門檻 → 0–255 強度)。
/// 雜訊、海雜波與雨的起伏取自種子查表(確定性);只有含回波的格才取對數,純雜訊格直接查 dB 表。
/// </summary>
internal sealed class SpokeRenderer
{
    private float[] _p = Array.Empty<float>();
    private float[] _c = Array.Empty<float>();
    private float[] _ring = Array.Empty<float>();

    public void Render(VideoGrid g, SpokeGeometry geo, in ReceiverParams rp, ulong spokeBase, NoiseTables nt, Span<byte> output)
    {
        var n = g.Cells;
        if (_p.Length < n)
        {
            _p = new float[n];
            _c = new float[n];
        }
        var p = _p.AsSpan(0, n);
        var c = _c.AsSpan(0, n);
        p.Clear();
        c.Clear();
        var cell = g.CellM;
        var invCell = 1.0 / cell;
        var lastCell = 0;

        // 目標:距離段 [R0, R1 + 脈衝長度],邊界格依覆蓋比例(子格精度)
        for (var s = 0; s < geo.SegCount; s++)
        {
            ref readonly var seg = ref geo.Segs[s];
            var r0 = seg.R0;
            var r1 = seg.R1 + g.PulseM;
            if (r0 >= g.RangeM) continue;
            if (r1 > g.RangeM) r1 = g.RangeM - 1e-6;
            var i0 = (int)(r0 * invCell);
            var i1 = (int)(r1 * invCell);
            for (var i = i0; i <= i1; i++)
            {
                var lo = Math.Max(r0, i * cell);
                var hi = Math.Min(r1, (i + 1) * cell);
                var cover = (hi - lo) * invCell;
                if (cover <= 0) continue;
                p[i] += (float)(seg.Power * cover) * g.TgtGain[i];
            }
            if (i1 + 1 > lastCell) lastCell = i1 + 1;
        }

        // 陸地:自入射距離起,向內陸以 1.5 km 衰減(地形遮蔽的近似),至出陸距離或 3 km
        var clutterEnd = g.ClutterEnd;
        if (geo.LandEntry < g.RangeM)
        {
            var e = geo.LandEntry;
            var x = Math.Min(Math.Min(geo.LandExit, e + 3000.0), g.RangeM - 1e-6);
            var i0 = (int)(e * invCell);
            var i1 = (int)(x * invCell);
            for (var i = i0; i <= i1; i++)
            {
                var lo = Math.Max(e, i * cell);
                var hi = Math.Min(x, (i + 1) * cell);
                var cover = (hi - lo) * invCell;
                if (cover <= 0) continue;
                p[i] += (float)(geo.LandFactor * cover * Math.Exp(-(lo - e) / 1500.0)) * g.LandGain[i];
            }
            if (i1 + 1 > lastCell) lastCell = i1 + 1;
            clutterEnd = Math.Min(clutterEnd, i0); // 陸地後方海面被遮蔽
        }

        // 海雜波平均(起伏在下方逐格乘上)
        var az = (float)geo.SeaAzMod;
        for (var i = 0; i < clutterEnd; i++) c[i] = g.SeaMean[i] * az;
        var noiseLikeEnd = clutterEnd;

        // 雨:體積回波(起伏同雜波)與雙程衰減
        if (geo.RainCount > 0)
        {
            for (var r = 0; r < geo.RainCount; r++)
            {
                var r0 = geo.RainR0[r];
                var r1 = Math.Min(geo.RainR1[r], g.RangeM - 1e-6);
                if (r0 >= g.RangeM) continue;
                var i0 = (int)(r0 * invCell);
                var i1 = (int)(r1 * invCell);
                var zf = (float)geo.RainZ[r];
                for (var i = i0; i <= i1; i++)
                {
                    if (i * cell > geo.LandEntry) break;
                    c[i] += zf * g.RainUnit[i];
                }
                if (i1 + 1 > noiseLikeEnd) noiseLikeEnd = i1 + 1;
            }
            // 衰減:各格前方雨區路徑長 × 雙程比衰減
            for (var i = 0; i < n; i++)
            {
                var rr = (i + 0.5) * cell;
                var db = 0.0;
                for (var r = 0; r < geo.RainCount; r++)
                {
                    var path = Math.Min(rr, geo.RainR1[r]) - geo.RainR0[r];
                    if (path > 0) db += path * geo.RainGamma[r];
                }
                if (db <= 0) continue;
                var lin = (float)Math.Pow(10.0, -db / 10.0);
                p[i] *= lin;
                c[i] *= lin;
            }
        }

        var loss = (float)(rp.LossLin * geo.ShadowLin);
        var thr = (float)rp.ThresholdDb;
        var scale = rp.Scale;
        var ftc = (float)rp.FtcK;
        var w = g.FtcWindow;
        if (ftc > 0 && _ring.Length < w) _ring = new float[w];
        var ring = _ring;
        var ringSum = 0f;
        var ringPos = 0;
        if (ftc > 0) Array.Clear(ring, 0, w);
        var exp = nt.Exp;
        var expDb = nt.ExpDb;
        var stc = g.StcDb;
        var active = Math.Max(lastCell, noiseLikeEnd);
        for (var i = 0; i < n; i++)
        {
            var h = Hash.Cell(spokeBase, i);
            var ni = (int)(h & NoiseTables.Mask);
            float db;
            if (i < active)
            {
                var s = p[i];
                var cm = c[i];
                if (cm > 0) s += cm * exp[(int)((h >> 16) & NoiseTables.Mask)];
                s *= loss;
                db = s > 0 ? 10f * MathF.Log10(s + exp[ni]) : expDb[ni];
            }
            else db = expDb[ni];
            db -= stc[i];
            if (ftc > 0)
            {
                // FTC(微分):減去前段 w 格(正值)平均的比例,壓抑大面積雨回波、保留前緣
                var mean = ringSum / w;
                var v = db > 0 ? db : 0f;
                ringSum += v - ring[ringPos];
                ring[ringPos] = v;
                ringPos = ringPos + 1 == w ? 0 : ringPos + 1;
                db -= ftc * mean;
            }
            var level = (db - thr) * scale;
            output[i] = level <= 0 ? (byte)0 : level >= 255 ? (byte)255 : (byte)level;
        }
    }
}
