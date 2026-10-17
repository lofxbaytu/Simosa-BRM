using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>
/// 操縱量測的目標值(data/ships/&lt;ID&gt;/trial_targets.json;規劃書第 3.5 節)與比較:同船同裝載才比較;No.1 滿載為試俥實測(trial),
/// No.2 為駕駛台海報圖面讀值(poster,dataGrade),posterEstimates 段為估計值(estimate;No.1 壓載、淺水 h/T 1.2);舵機擺動以交船圖說的
/// 35°→35° 時間比較(particulars)。公差依 trial_targets.tolerances(規劃書第 6.4 節;No.2 為 No.1 的 1.5 倍)。
/// 檔案依船舶 ID 讀一次並快取(內容只影響 summary 比較,不影響物理與雜湊)。
/// </summary>
public sealed class ManoeuvreTargets
{
    public const double MetresPerCable = 185.2;
    private const double DeepWaterDepthRatio = 4.0;   // MSC.137(76) 試驗水深 ≥ 4 T
    private const double CalmWindKn = 10.0;
    private const double CalmCurrentKn = 0.2;

    private static readonly ConcurrentDictionary<string, ManoeuvreTargets?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly JsonElement _root;
    private readonly string _grade;
    private readonly string _condition;

    private ManoeuvreTargets(JsonElement root)
    {
        _root = root;
        _grade = Str(root, "dataGrade") ?? "trial";
        _condition = Str(root, "condition") ?? "full";
        SourceText = Str(root, "source") ?? "";
    }

    /// <summary>trial_targets.json 的 source 說明(試俥日期/海報版次)</summary>
    public string SourceText { get; }

    /// <summary>目標檔內容的 sha256(小寫 hex;記入每筆比較過的量測 <see cref="ManoeuvreRecord.TargetsSha256"/>,目標值日後更新時可追溯重播摘要差異)</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>主要目標值的資料品質:trial(No.1 試俥)| poster(No.2 海報讀值)</summary>
    public string Grade => _grade;

    /// <summary>目標檔相對專案根目錄的路徑('/' 分隔,例如 data/ships/FSB1/trial_targets.json;測試建立者為 null)</summary>
    public string? RelativePath { get; init; }

    /// <summary>目標檔內容(紀錄標頭 manoeuvreTargets.content;重播優先使用,不讀目前檔案)</summary>
    public JsonElement Content => _root;

    /// <summary>目標值來源:file(讀目前檔案)| header(紀錄標頭)| test</summary>
    public string Origin { get; init; } = "test";

    /// <summary>目標檔的專案相對路徑。</summary>
    public static string RelativePathFor(string shipId) => $"data/ships/{shipId}/trial_targets.json";

    /// <summary>依船舶 ID 讀取(找不到專案根目錄或檔案時 null;每個程序快取一次)。</summary>
    public static ManoeuvreTargets? For(string shipId) => Cache.GetOrAdd(shipId, LoadCurrent);

    /// <summary>讀目前的目標檔(不經快取;重播 --rescore 以目前檔案重新評分時用)。</summary>
    public static ManoeuvreTargets? LoadCurrent(string shipId)
    {
        var root = RepositoryPaths.TryFindRoot();
        if (root is null) return null;
        var path = Path.Combine(root, "data", "ships", shipId, "trial_targets.json");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        return new ManoeuvreTargets(doc.RootElement.Clone())
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), RelativePath = RelativePathFor(shipId), Origin = "file",
        };
    }

    /// <summary>由紀錄標頭的 manoeuvreTargets(content + sha256)重建;sha256 沿用標頭值(檔案原始位元組的雜湊,content 重新序列化後位元組可能不同)。</summary>
    public static ManoeuvreTargets FromHeader(JsonElement content, string sha256, string? path)
        => new(content.Clone()) { Sha256 = sha256, RelativePath = path, Origin = "header" };

    /// <summary>由 JSON 文字建立(測試用)。</summary>
    public static ManoeuvreTargets FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ManoeuvreTargets(doc.RootElement.Clone());
    }

    // ------------------------------------------------------------------ 公差

    private double? TolValue(string key)
        => _root.TryGetProperty("tolerances", out var t) && t.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private double TurningTol(double target, double lpp) => Math.Max((TolValue("turning_pct") ?? 10) / 100.0 * Math.Abs(target), (TolValue("turning_minL") ?? 0.3) * lpp);
    private double TimeTol(double target) => (TolValue("turning_pct") ?? 10) / 100.0 * Math.Abs(target);
    private double? ZigzagTol(double target) => TolValue("zigzag_deg") is { } d ? Math.Max(d, (TolValue("zigzag_pct") ?? 25) / 100.0 * Math.Abs(target)) : null;
    private double StopTol(double target) => (TolValue("stopping_pct") ?? 15) / 100.0 * Math.Abs(target);
    private double SpeedTol => TolValue("speed_kn") ?? 0.5;

    // ------------------------------------------------------------------ 比較

    /// <summary>依量測種類填入 <see cref="ManoeuvreRecord.Comparisons"/>、<see cref="ManoeuvreRecord.DataQuality"/> 與說明。</summary>
    public void Compare(ManoeuvreRecord r, double lpp)
    {
        switch (r.Kind)
        {
            case ManoeuvreKinds.Turning: CompareTurning(r, lpp); break;
            case ManoeuvreKinds.Zigzag: CompareZigzag(r); break;
            case ManoeuvreKinds.CrashStop: CompareCrashStop(r); break;
            case ManoeuvreKinds.InertiaStop: CompareInertiaStop(r); break;
            case ManoeuvreKinds.ThrusterTurn: CompareThruster(r); break;
        }
        if (r.Comparisons.Any(c => c.DataQuality == "poster"))
            AddNote(r, "海報目標值為 IMO 駕駛台海報圖面讀值(精度約 ±5%,非試俥報告;規劃書第 3.5 節)");
        if (_grade == "poster" && r.Comparisons.Count > 0)
            AddNote(r, $"{r.ShipId} 無試俥報告,公差為 No.1 的 1.5 倍(trial_targets.tolerances)");
        if (r.Comparisons.Any(c => c.DataQuality == "estimate"))
            AddNote(r, "目標值為駕駛台海報估計值(非實測;壓載與淺水操縱特性須以船長/引水人驗證,第 3.5 節)");
        r.DataQuality = BestQuality(r.Comparisons);
    }

    /// <summary>舵機擺動與交船圖說的 35°→35° 時間比較(依使用中的泵)。</summary>
    public static void CompareSteering(ManoeuvreRecord r, ShipParticulars ship, double tolerancePct)
    {
        var sg = ship.Rudder.SteeringGear;
        double? target = r.Pump == "both" ? sg?.HardOverTime35_twoPumps_s ?? sg?.HardOverTime35_s : sg?.HardOverTime35_onePump_s ?? sg?.HardOverTime35_s;
        if (target is not { } t || !r.Values.ContainsKey("time35to35_s")) return;
        var reasons = new List<string>();
        if (!r.Dynamics.StartsWith("mmg", StringComparison.OrdinalIgnoreCase)) reasons.Add("暫代模型(非 MMG)");
        var from = r.Values.GetValueOrDefault("from_deg");
        if (Math.Abs(Math.Abs(from) - 35.0) > 1.0 || Math.Abs(Math.Abs(r.RudderDeg ?? 0) - 35.0) > 1.0)
            reasons.Add("非 35° → 對舷 35° 的擺動");
        var key = r.Pump == "both" ? "rudder.steeringGear.hardOverTime35_twoPumps_s" : sg?.HardOverTime35_onePump_s is not null ? "rudder.steeringGear.hardOverTime35_onePump_s" : "rudder.steeringGear.hardOverTime35_s";
        Add(r, "time35to35_s", t, null, null, tolerancePct / 100.0 * t, "particulars", $"particulars.{key}", reasons);
        foreach (var reason in reasons) AddNote(r, $"particulars:不可比 — {reason}");
        r.DataQuality = BestQuality(r.Comparisons);
    }

    private List<string> BaseReasons(ManoeuvreRecord r, bool deepWater = true)
    {
        var reasons = new List<string>();
        if (!r.Dynamics.StartsWith("mmg", StringComparison.OrdinalIgnoreCase)) reasons.Add($"暫代模型({r.Dynamics},非 MMG),量測不具代表性");
        if (!r.Approach.Steady) reasons.Add("開始前未穩定直航(MSC.137(76) 要求定速直航)");
        if (r.Environment.WindSpeedKn > CalmWindKn) reasons.Add($"風速 {F1(r.Environment.WindSpeedKn)} kn(試俥為平靜海況)");
        if (r.Environment.CurrentDriftKn > CalmCurrentKn) reasons.Add($"流 {F1(r.Environment.CurrentDriftKn)} kn");
        if (deepWater && r.Environment.DepthRatio < DeepWaterDepthRatio) reasons.Add($"淺水 h/T = {F1(r.Environment.DepthRatio)}(目標值為深水)");
        return reasons;
    }

    private void CheckApproach(ManoeuvreRecord r, double? approachKn, List<string> reasons)
    {
        if (approachKn is { } a && Math.Abs(r.Approach.SpeedKn - a) > SpeedTol)
            reasons.Add($"進入速度 {F1(r.Approach.SpeedKn)} kn 與目標 {F1(a)} kn 差超過 {F1(SpeedTol)} kn");
    }

    private void CompareTurning(ManoeuvreRecord r, double lpp)
    {
        var nominal = Math.Round(Math.Abs(r.RudderDeg ?? 0));
        if (nominal != 35)
        {
            AddNote(r, $"舵角 {nominal:F0}° 無試俥/海報目標值(目標值為 35° 迴旋)");
            return;
        }
        var side = r.Side ?? "port";
        if (IsCondition(r.Loading))
        {
            if (Section("identification", "turningCircle35", out var tc, out var section))
            {
                var reasons = BaseReasons(r);
                CheckApproach(r, Num(tc, "approachSpeed_kn"), reasons);
                if (tc.TryGetProperty(side, out var s))
                {
                    foreach (var key in new[] { "advance_m", "transfer_m", "tacticalDiameter_m" })
                        if (Num(s, key) is { } target) Add(r, key, target, null, null, TurningTol(target, lpp), _grade, $"{section}.turningCircle35.{side}.{key}", reasons);
                    AddMarks(r, s, "marks", "t_s", 1.0, _grade, $"{section}.turningCircle35.{side}.marks", reasons);
                }
                if (tc.TryGetProperty("steadySpeedInTurn_kn", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2)
                {
                    var (lo, hi) = (range[0].GetDouble(), range[1].GetDouble());
                    Add(r, "steadySpeed_kn", (lo + hi) / 2.0, lo, hi, SpeedTol, _grade, $"{section}.turningCircle35.steadySpeedInTurn_kn", reasons);
                }
                NoteReasons(r, $"{section}.turningCircle35", reasons);
            }
            if (Poster("shallowTurning_hT1_2", out var sh) && Math.Abs(r.Environment.DepthRatio - 1.2) <= 0.2)
            {
                var reasons = BaseReasons(r, deepWater: false);
                CheckApproach(r, Num(sh, "approachSpeed_kn"), reasons);
                AddMarks(r, sh, "marks", "t_min", 60.0, "estimate", "posterEstimates.shallowTurning_hT1_2.marks", reasons);
                AddNote(r, "海報淺水迴旋估計值未註明左右舷");
                NoteReasons(r, "posterEstimates.shallowTurning_hT1_2", reasons);
            }
        }
        else if (string.Equals(r.Loading, "ballast", StringComparison.OrdinalIgnoreCase) && Poster("ballastTurning", out var bt))
        {
            var reasons = BaseReasons(r);
            CheckApproach(r, Num(bt, "approachSpeed_kn"), reasons);
            AddMarks(r, bt, "marks", "t_min", 60.0, "estimate", "posterEstimates.ballastTurning.marks", reasons);
            AddNote(r, "海報壓載迴旋估計值未註明左右舷");
            NoteReasons(r, "posterEstimates.ballastTurning", reasons);
        }
        else AddNote(r, $"{r.ShipId} {r.Loading} 無迴旋目標值");
    }

    /// <summary>迴旋標記(heading_deg → timeTo{h}_s、speedAt{h}_kn)。</summary>
    private void AddMarks(ManoeuvreRecord r, JsonElement parent, string prop, string timeKey, double timeScale, string quality, string source, List<string> reasons)
    {
        if (!parent.TryGetProperty(prop, out var marks) || marks.ValueKind != JsonValueKind.Array) return;
        foreach (var m in marks.EnumerateArray())
        {
            if (Num(m, "heading_deg") is not { } h) continue;
            var hk = h.ToString("F0", CultureInfo.InvariantCulture);
            if (Num(m, timeKey) is { } t) Add(r, $"timeTo{hk}_s", t * timeScale, null, null, TimeTol(t * timeScale), quality, $"{source}[{hk}°].{timeKey}", reasons);
            if (Num(m, "speed_kn") is { } v) Add(r, $"speedAt{hk}_kn", v, null, null, SpeedTol, quality, $"{source}[{hk}°].speed_kn", reasons);
        }
    }

    private void CompareZigzag(ManoeuvreRecord r)
    {
        var psi = Math.Round(r.CheckHeadingDeg ?? 0);
        var rud = Math.Round(Math.Abs(r.RudderDeg ?? 0));
        var name = $"zigzag{psi:F0}_{rud:F0}";
        if (psi != rud || !IsCondition(r.Loading) || !Section(null, name, out var zz, out var section))
        {
            AddNote(r, $"{r.ShipId} {r.Loading} 無 {psi:F0}/{rud:F0} Z 形目標值" + (_grade == "poster" ? "(No.2 海報無 Z 形資料,不列入數值驗收)" : ""));
            return;
        }
        var reasons = BaseReasons(r);
        CheckApproach(r, Num(zz, "approachSpeed_kn"), reasons);
        var first = r.Side == "starboard" ? "starboardFirst" : "portFirst";
        // MSC/Circ.1053:於航向變化達執行航向 ψ 時反舵;第 k 次反舵與 ψ 差超過 ReversalToleranceDeg 時,第 k 個及之後的超越角不可比
        int? offFrom = null;
        string? offReason = null;
        for (var k = 1; r.Values.TryGetValue($"reversalLate{k}_deg", out var late); k++)
        {
            if (Math.Abs(late) <= ManoeuvreTracker.ReversalToleranceDeg) continue;
            offFrom = k;
            offReason = $"第 {k} 次反舵{(late > 0 ? "晚" : "早")}於執行航向 {F1(Math.Abs(late))}°(容許 {F1(ManoeuvreTracker.ReversalToleranceDeg)}°;第 {k} 個及之後的超越角)";
            break;
        }
        if (zz.TryGetProperty(first, out var f) && f.TryGetProperty("overshoots_deg", out var os) && os.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var o in os.EnumerateArray())
            {
                i++;
                var target = o.GetDouble();
                var rs = offFrom is { } k0 && i >= k0 ? new List<string>(reasons) { offReason! } : reasons;
                if (ZigzagTol(target) is { } tol) Add(r, $"overshoot{i}_deg", target, null, null, tol, _grade, $"{section}.{name}.{first}.overshoots_deg[{i - 1}]", rs);
            }
        }
        if (offReason is not null && r.Comparisons.Any(c => !c.Comparable)) NoteReasons(r, $"{section}.{name}", new List<string> { offReason });
        NoteReasons(r, $"{section}.{name}", reasons);
    }

    /// <summary>先停俥再倒俥(order "STOP→EFAS")時,停俥到倒俥超過此秒數即不可比(≤ 此值只註明條件差異)</summary>
    public const double StopToAsternComparableS = 5.0;

    private void CompareCrashStop(ManoeuvreRecord r)
    {
        var final = r.Order is { } o && o.Contains('→') ? o[(o.LastIndexOf('→') + 1)..] : r.Order;
        var fullAstern = final is "EFAS" or "FAS";
        if (IsCondition(r.Loading))
        {
            if (Section("validation", "crashStop", out var cs, out var section))
            {
                var reasons = BaseReasons(r);
                StopFirst(r, reasons);
                if (!fullAstern) reasons.Add($"車令 {r.Order}(目標為全速進 → 全速退)");
                CheckApproach(r, Num(cs, "approachSpeed_kn"), reasons);
                if (Num(cs, "stopTime_s") is { } st) Add(r, "stopTime_s", st, null, null, StopTol(st), _grade, $"{section}.crashStop.stopTime_s", reasons);
                if (Num(cs, "trackReach_m") is { } tr) Add(r, "trackReach_m", tr, null, null, StopTol(tr), _grade, $"{section}.crashStop.trackReach_m", reasons);
                else if (Num(cs, "trackReach_cables") is { } tc) Add(r, "trackReach_m", tc * MetresPerCable, null, null, StopTol(tc * MetresPerCable), _grade, $"{section}.crashStop.trackReach_cables", reasons);
                if (Num(cs, "asternStart_s") is { } a) Add(r, "asternStart_s", a, null, null, StopTol(a), _grade, $"{section}.crashStop.asternStart_s", reasons);
                NoteReasons(r, $"{section}.crashStop", reasons);
            }
            if (Poster("loadedCrashStopPoster", out var lp))
                PosterCrashStop(r, lp, "poster", "posterEstimates.loadedCrashStopPoster", fullAstern);
        }
        else if (string.Equals(r.Loading, "ballast", StringComparison.OrdinalIgnoreCase) && Poster("ballastCrashStop", out var bc))
            PosterCrashStop(r, bc, "estimate", "posterEstimates.ballastCrashStop", fullAstern);
        else AddNote(r, $"{r.ShipId} {r.Loading} 無緊急停船目標值");
    }

    /// <summary>先停俥再倒俥:停俥到倒俥 ≤ 5 s 只註明條件差異,超過即不可比。</summary>
    private static void StopFirst(ManoeuvreRecord r, List<string> reasons)
    {
        if (!r.Values.TryGetValue("stopToAstern_s", out var d)) return;
        if (d > StopToAsternComparableS) reasons.Add($"停俥 {F1(d)} s 後才倒俥(試俥為由全速進直接倒俥)");
        else AddNote(r, $"停俥 {F1(d)} s 後倒俥(試俥為直接倒俥;差異 ≤ {StopToAsternComparableS:F0} s,仍比較)");
    }

    private void PosterCrashStop(ManoeuvreRecord r, JsonElement p, string quality, string source, bool fullAstern)
    {
        var reasons = BaseReasons(r);
        StopFirst(r, reasons);
        if (!fullAstern) reasons.Add($"車令 {r.Order}(目標為全速進 → 全速退)");
        if (Num(p, "time_min") is { } tm) Add(r, "stopTime_s", tm * 60.0, null, null, StopTol(tm * 60.0), quality, $"{source}.time_min", reasons);
        if (Num(p, "trackReach_cables") is { } c) Add(r, "trackReach_m", c * MetresPerCable, null, null, StopTol(c * MetresPerCable), quality, $"{source}.trackReach_cables", reasons);
        AddNote(r, $"{source}:海報未註明進入速度(以海上全速計)");
        NoteReasons(r, source, reasons);
    }

    private void CompareInertiaStop(ManoeuvreRecord r)
    {
        if (r.Mode == "drift")
        {
            AddNote(r, "停俥漂移(低速停俥)無試俥/海報目標值,只供講評(艏偏、漂移距離)");
            return;
        }
        if (!IsCondition(r.Loading) || !Section("validation", "inertiaStop", out var ins, out var section))
        {
            AddNote(r, $"{r.ShipId} {r.Loading} 無慣性停船目標值");
            return;
        }
        var reasons = BaseReasons(r);
        if (r.Order is not ("STOP" or "rpm 0")) reasons.Add($"車令 {r.Order}(目標為停俥)");
        if (Num(ins, "approachSpeed_kn") is { } approach)
        {
            CheckApproach(r, approach, reasons);
            var end = r.Values.GetValueOrDefault("endSpeed_kn");
            if (Math.Abs(end - 5.0) > 1e-6) reasons.Add($"結束速度 {F1(end)} kn(目標為減至 5 kn)");
            if (Num(ins, "timeTo5kn_s") is { } t) Add(r, "time_s", t, null, null, StopTol(t), _grade, $"{section}.inertiaStop.timeTo5kn_s", reasons);
            if (Num(ins, "distanceTo5kn_m") is { } d) Add(r, "distance_m", d, null, null, StopTol(d), _grade, $"{section}.inertiaStop.distanceTo5kn_m", reasons);
            if (Num(ins, "headReach_m") is { } h) Add(r, "headReach_m", h, null, null, StopTol(h), _grade, $"{section}.inertiaStop.headReach_m", reasons);
            if (Num(ins, "sideReach_m") is { } s) Add(r, "sideReach_m", s, null, null, StopTol(s), _grade, $"{section}.inertiaStop.sideReach_m", reasons);
            AddNote(r, "No.1 試俥慣性停船航跡彎曲(head reach 1,098 m、side reach 402 m),直線滑行模型的時間偏長為已知偏差(MmgGoldenTests Skip)");
        }
        else if (ins.TryGetProperty("fromFullSea", out var series) && series.ValueKind == JsonValueKind.Array)
        {
            double? v0 = null;
            foreach (var p in series.EnumerateArray())
            {
                var tmin = Num(p, "t_min") ?? 0;
                var v = Num(p, "speed_kn") ?? 0;
                if (tmin == 0) { v0 = v; CheckApproach(r, v, reasons); continue; }
                Add(r, $"speedAt{tmin:F0}min_kn", v, null, null, SpeedTol, _grade, $"{section}.inertiaStop.fromFullSea[{tmin:F0} min]", reasons);
            }
            if (Str(ins, "note") is { } note) AddNote(r, $"{section}.inertiaStop:{note}");
        }
        NoteReasons(r, $"{section}.inertiaStop", reasons);
    }

    private void CompareThruster(ManoeuvreRecord r)
    {
        if (!IsCondition(r.Loading) || !Section("validation", "bowThrusterTurn90", out var bt, out var section))
        {
            AddNote(r, $"{r.ShipId} {r.Loading} 無艏側推迴轉目標值");
            return;
        }
        var reasons = BaseReasons(r);
        reasons.RemoveAll(x => x.StartsWith("開始前未穩定直航", StringComparison.Ordinal)); // 低速側推試驗不要求定速直航
        CheckApproach(r, Num(bt, "speed_kn"), reasons);
        if (Math.Abs(r.Approach.Rpm) > 1.0) reasons.Add("主機未停俥(目標為停俥、艏側推全推)");
        var key = r.Side == "starboard" ? "starboard_s" : "port_s";
        if (Num(bt, key) is { } t) Add(r, "time90_s", t, null, null, StopTol(t), _grade, $"{section}.bowThrusterTurn90.{key}", reasons);
        NoteReasons(r, $"{section}.bowThrusterTurn90", reasons);
    }

    // ------------------------------------------------------------------ 工具

    private bool IsCondition(string loading) => string.Equals(loading, _condition, StringComparison.OrdinalIgnoreCase);

    /// <summary>於 identification / validation(<paramref name="preferred"/> 為 null 時兩者皆找)尋找試驗段。</summary>
    private bool Section(string? preferred, string name, out JsonElement value, out string section)
    {
        foreach (var s in preferred is null ? new[] { "identification", "validation" } : new[] { preferred, preferred == "identification" ? "validation" : "identification" })
        {
            if (_root.TryGetProperty(s, out var sec) && sec.ValueKind == JsonValueKind.Object && sec.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
            {
                section = s;
                return true;
            }
        }
        value = default;
        section = "";
        return false;
    }

    private bool Poster(string name, out JsonElement value)
    {
        if (_root.TryGetProperty("posterEstimates", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object) return true;
        value = default;
        return false;
    }

    private static void Add(ManoeuvreRecord r, string key, double target, double? min, double? max, double tol, string quality, string source, List<string> reasons)
    {
        if (!r.Values.TryGetValue(key, out var m) || double.IsNaN(m)) return;
        double dev;
        if (min is { } lo && max is { } hi) dev = m < lo ? m - lo : m > hi ? m - hi : 0.0;
        else dev = m - target;
        r.Comparisons.Add(new ManoeuvreComparison
        {
            Key = key, Measured = m, Target = target, TargetMin = min, TargetMax = max, Deviation = dev,
            DeviationPct = target != 0 ? 100.0 * (min is null ? m - target : dev) / target : null,
            Tolerance = tol, WithinTolerance = Math.Abs(dev) <= tol, Comparable = reasons.Count == 0, DataQuality = quality, Source = source,
        });
    }

    private static void NoteReasons(ManoeuvreRecord r, string source, List<string> reasons)
    {
        foreach (var reason in reasons) AddNote(r, $"{source}:不可比 — {reason}");
    }

    private static void AddNote(ManoeuvreRecord r, string note)
    {
        if (!r.Notes.Contains(note)) r.Notes.Add(note);
    }

    private static readonly string[] QualityOrder = { "trial", "particulars", "poster", "estimate" };

    private static string BestQuality(List<ManoeuvreComparison> comps)
    {
        foreach (var pool in new[] { comps.Where(c => c.Comparable), comps })
        {
            var list = pool.ToList();
            foreach (var q in QualityOrder)
                if (list.Any(c => c.DataQuality == q)) return q;
        }
        return "none";
    }

    private static double? Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
