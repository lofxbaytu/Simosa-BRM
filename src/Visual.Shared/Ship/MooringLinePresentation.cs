using SimosaBRM.SimCore.Contracts;
using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.State;

namespace SimosaBRM.Visual.Shared.Ship;

/// <summary>張力等級(張力 / MBL):&lt; 50 % 正常、50–80 % 黃、&gt; 80 % 紅(docs/design/visual-client.md 第 4 節繫纜)。</summary>
public enum MooringTensionLevel
{
    Normal,
    High,
    Critical,
}

/// <summary>ENU 位置加高度(m)。</summary>
public readonly record struct EnuPoint3(double East, double North, double Up);

/// <summary>一條繫纜的呈現參數(純資料;Godot 端只畫線與閃爍)。</summary>
public sealed record MooringLineVisual
{
    public required string Id { get; init; }
    /// <summary>契約 status:stowed | fast | heaving | slacking | letGo | parted</summary>
    public required string Status { get; init; }
    public string? BollardId { get; init; }
    /// <summary>要畫線(fast/heaving/slacking 且找得到繫船柱)</summary>
    public required bool Drawn { get; init; }
    /// <summary>斷纜:不畫線、在導纜孔處短暫閃爍</summary>
    public required bool Parted { get; init; }
    /// <summary>張力 / MBL(mbl 未知時 0)</summary>
    public required double TensionRatio { get; init; }
    public required MooringTensionLevel Level { get; init; }
    /// <summary>自船導纜孔(ENU;契約無導纜孔座標,一律依纜名/繫船柱方位估計)</summary>
    public required EnuPoint3 Fairlead { get; init; }
    /// <summary>繫船柱頂(ENU;繫船柱找不到時 null)</summary>
    public EnuPoint3? Bollard { get; init; }
    /// <summary>導纜孔為估計(目前永遠 true;state.berth.lines[] 無導纜孔座標)</summary>
    public required bool FairleadEstimated { get; init; }
}

/// <summary>
/// 繫纜呈現(docs/design/visual-client.md 第 4 節;規劃書第 7.2 節泊位繫纜):state.berth.lines[](status、tension_t/mbl_t、bollardId)
/// × 區域檔泊位的繫船柱位置(<see cref="BerthGeometry.Bollards"/>)× 自船姿態 → 纜線兩端 ENU 與顏色等級。
/// 導纜孔:契約沒有導纜孔座標,依纜名(head/bow/fwd* → 艏、stern/aft* → 艉;無法判斷時依繫船柱在船體座標的前後)與繫船柱所在舷側估計,
/// 位置與 SimCore ScenarioBodyPoint 具名點相同:fwdPort/fwdStbd = (+0.40 Lpp, ∓0.45 B)、aftPort/aftStbd = (−0.40 Lpp, ∓0.45 B),高度為上甲板(型深 − 吃水)。
/// 張力比著色:&lt; 50 % 正常、50–80 % 黃、&gt; 80 % 紅;parted 不畫線,導纜孔處閃爍 <see cref="PartedFlashSeconds"/> 秒;stowed/letGo 不畫。純計算,無 Godot 依賴。
/// </summary>
public static class MooringLinePresentation
{
    public const double HighRatio = 0.5;
    public const double CriticalRatio = 0.8;
    /// <summary>斷纜閃爍持續秒數與頻率</summary>
    public const double PartedFlashSeconds = 3.0;
    public const double PartedFlashHz = 4.0;
    /// <summary>估計導纜孔的縱向位置(Lpp 比例)與橫向位置(B 比例;與 ScenarioBodyPoint 具名點相同)</summary>
    public const double FairleadForwardFraction = 0.40;
    public const double FairleadBeamFraction = 0.45;
    /// <summary>繫船柱頂高度(碼頭面 + 繫船柱高;HarbourNode 的繫船柱方塊高 0.8 m)</summary>
    public const double BollardTopAboveQuayM = 0.4;

    /// <summary>張力等級。</summary>
    public static MooringTensionLevel Level(double tensionRatio)
        => tensionRatio > CriticalRatio ? MooringTensionLevel.Critical : tensionRatio >= HighRatio ? MooringTensionLevel.High : MooringTensionLevel.Normal;

    /// <summary>張力 / MBL(mbl 未給或非正時 0;負張力視為 0)。</summary>
    public static double TensionRatio(MooringLineState line)
        => line.MblT is { } mbl && mbl > 0 ? Math.Max(0.0, line.TensionT) / mbl : 0.0;

    /// <summary>等級顏色(RGB 0–1):正常為纜繩本色、黃、紅。</summary>
    public static (double R, double G, double B) Colour(MooringTensionLevel level) => level switch
    {
        MooringTensionLevel.Critical => (1.0, 0.15, 0.10),
        MooringTensionLevel.High => (1.0, 0.85, 0.10),
        _ => (0.85, 0.80, 0.60),
    };

    /// <summary>status 是否為受力中(要畫線)。</summary>
    public static bool IsLoaded(string? status)
        => status is not null && (status.Equals("fast", StringComparison.OrdinalIgnoreCase) || status.Equals("heaving", StringComparison.OrdinalIgnoreCase) || status.Equals("slacking", StringComparison.OrdinalIgnoreCase));

    public static bool IsParted(string? status) => status is not null && status.Equals("parted", StringComparison.OrdinalIgnoreCase);

    /// <summary>斷纜閃爍:自斷纜起 <paramref name="sinceSeconds"/> 秒,持續 PartedFlashSeconds、每秒 PartedFlashHz 次亮滅;之後不顯示。</summary>
    public static bool PartedFlashVisible(double sinceSeconds)
    {
        if (sinceSeconds < 0 || sinceSeconds >= PartedFlashSeconds) return false;
        return ((int)Math.Floor(sinceSeconds * PartedFlashHz * 2.0)) % 2 == 0;
    }

    /// <summary>
    /// 依纜名判斷導纜孔在艏或艉:head/bow/fwd*/fore* → 艏(true);stern/aft* → 艉(false);無法判斷 → null。
    /// (情境庫慣例 HEAD / FWDSPRING / AFTSPRING / STERN。)
    /// </summary>
    public static bool? ForwardFromLineId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Trim().ToLowerInvariant();
        if (s.Contains("head") || s.Contains("bow") || s.StartsWith("fwd") || s.StartsWith("fore")) return true;
        if (s.Contains("stern") || s.StartsWith("aft")) return false;
        return null;
    }

    /// <summary>
    /// 估計導纜孔的船體座標(x 向艏、y 向右舷、z 自水線向上):艏艉依纜名,否則依繫船柱在船體座標的前後;舷側依繫船柱所在側。
    /// </summary>
    public static (double Forward, double Starboard, double Up) FairleadBody(string? lineId, double bollardForward, double bollardStarboard, HullParameters hull)
    {
        var fwd = ForwardFromLineId(lineId) ?? bollardForward >= 0;
        var lpp = hull.Lpp > 0 ? hull.Lpp : hull.Loa / 1.06;
        var x = (fwd ? 1 : -1) * FairleadForwardFraction * lpp;
        var y = (bollardStarboard >= 0 ? 1 : -1) * FairleadBeamFraction * hull.Beam;
        return (x, y, Math.Max(0.5, hull.Depth - hull.Draft));
    }

    /// <summary>ENU 位置 → 自船船體座標(前、右)。</summary>
    public static (double Forward, double Starboard) ToBody(double east, double north, Pose own)
    {
        var h = Units.DegToRad(own.HeadingDeg);
        var dx = east - own.East;
        var dy = north - own.North;
        return (dx * Math.Sin(h) + dy * Math.Cos(h), dx * Math.Cos(h) - dy * Math.Sin(h));
    }

    /// <summary>船體座標 → ENU(含自船位置)。</summary>
    public static (double East, double North) FromBody(double forward, double starboard, Pose own)
    {
        var h = Units.DegToRad(own.HeadingDeg);
        return (own.East + forward * Math.Sin(h) + starboard * Math.Cos(h), own.North + forward * Math.Cos(h) - starboard * Math.Sin(h));
    }

    /// <summary>找繫船柱:優先 state.berth.id 對應的泊位,找不到再搜所有泊位(區域檔泊位 id 與情境 berth.id 相同時直接命中)。</summary>
    public static (EnuPoint Pos, double QuayHeightM)? FindBollard(IReadOnlyList<BerthGeometry> berths, string? berthId, string? bollardId)
    {
        if (string.IsNullOrEmpty(bollardId)) return null;
        BerthGeometry? preferred = null;
        foreach (var b in berths)
            if (string.Equals(b.Id, berthId, StringComparison.OrdinalIgnoreCase)) { preferred = b; break; }
        if (preferred is not null)
            foreach (var (id, pos) in preferred.Bollards)
                if (string.Equals(id, bollardId, StringComparison.OrdinalIgnoreCase)) return (pos, AreaLoader.DefaultQuayHeightM);
        foreach (var b in berths)
        {
            if (ReferenceEquals(b, preferred)) continue;
            foreach (var (id, pos) in b.Bollards)
                if (string.Equals(id, bollardId, StringComparison.OrdinalIgnoreCase)) return (pos, AreaLoader.DefaultQuayHeightM);
        }
        return null;
    }

    /// <summary>
    /// 建立本幀所有繫纜的呈現。state 無 berth 或無 lines 時回空清單;stowed/letGo 不列入;parted 列入但 Drawn = false;
    /// 繫船柱找不到者 Drawn = false(Bollard = null),導纜孔仍以纜名估計(艏艉無法判斷時取艏、舷側取左舷)。
    /// </summary>
    public static IReadOnlyList<MooringLineVisual> Build(BerthStatusState? berth, Pose own, HullParameters hull, IReadOnlyList<BerthGeometry> berths)
    {
        if (berth is null || berth.Lines.Count == 0) return Array.Empty<MooringLineVisual>();
        var list = new List<MooringLineVisual>(berth.Lines.Count);
        foreach (var line in berth.Lines)
        {
            var loaded = IsLoaded(line.Status);
            var parted = IsParted(line.Status);
            if (!loaded && !parted) continue;
            var found = FindBollard(berths, berth.Id, line.BollardId);
            double bf = 1.0, bs = -1.0; // 找不到繫船柱時的預設:艏、左舷
            EnuPoint3? bollard = null;
            if (found is { } f)
            {
                (bf, bs) = ToBody(f.Pos.East, f.Pos.North, own);
                bollard = new EnuPoint3(f.Pos.East, f.Pos.North, f.QuayHeightM + BollardTopAboveQuayM);
            }
            var (fx, fy, fz) = FairleadBody(line.Id, bf, bs, hull);
            var (fe, fn) = FromBody(fx, fy, own);
            var ratio = TensionRatio(line);
            list.Add(new MooringLineVisual
            {
                Id = line.Id,
                Status = line.Status,
                BollardId = line.BollardId,
                Drawn = loaded && bollard is not null,
                Parted = parted,
                TensionRatio = ratio,
                Level = Level(ratio),
                Fairlead = new EnuPoint3(fe, fn, fz),
                Bollard = bollard,
                FairleadEstimated = true,
            });
        }
        return list;
    }
}
