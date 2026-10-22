using SimosaBRM.SimCore.Contracts;
using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.Smoke;
using SimosaBRM.Visual.Shared.State;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>繫纜呈現(docs/design/visual-client.md 第 4 節):端點、張力比著色、斷纜閃爍、導纜孔估計。</summary>
public class MooringLinePresentationTests
{
    private static readonly HullParameters Hull = HullParameters.ForTarget("FSB1", "FSB1", "tanker", 105.5, 18.0, 5.7);

    [Theory]
    [InlineData(0.0, MooringTensionLevel.Normal)]
    [InlineData(0.49, MooringTensionLevel.Normal)]
    [InlineData(0.5, MooringTensionLevel.High)]
    [InlineData(0.8, MooringTensionLevel.High)]
    [InlineData(0.81, MooringTensionLevel.Critical)]
    [InlineData(1.3, MooringTensionLevel.Critical)]
    public void TensionLevelThresholds(double ratio, MooringTensionLevel expected)
    {
        Assert.Equal(expected, MooringLinePresentation.Level(ratio));
    }

    [Fact]
    public void TensionRatioHandlesMissingMbl()
    {
        Assert.Equal(0.0, MooringLinePresentation.TensionRatio(new MooringLineState { Id = "A", Status = "fast", TensionT = 10 }));
        Assert.Equal(0.0, MooringLinePresentation.TensionRatio(new MooringLineState { Id = "A", Status = "fast", TensionT = 10, MblT = 0 }));
        Assert.Equal(0.2, MooringLinePresentation.TensionRatio(new MooringLineState { Id = "A", Status = "fast", TensionT = 10, MblT = 50 }), 12);
        Assert.Equal(0.0, MooringLinePresentation.TensionRatio(new MooringLineState { Id = "A", Status = "fast", TensionT = -1, MblT = 50 }));
    }

    [Fact]
    public void ColoursDifferPerLevel()
    {
        var n = MooringLinePresentation.Colour(MooringTensionLevel.Normal);
        var h = MooringLinePresentation.Colour(MooringTensionLevel.High);
        var c = MooringLinePresentation.Colour(MooringTensionLevel.Critical);
        Assert.NotEqual(n, h);
        Assert.NotEqual(h, c);
        Assert.True(c.R > 0.9 && c.G < 0.3, "紅");
        Assert.True(h.R > 0.9 && h.G > 0.7 && h.B < 0.3, "黃");
    }

    [Fact]
    public void PartedFlashIsBriefAndBlinks()
    {
        Assert.True(MooringLinePresentation.PartedFlashVisible(0.0));
        Assert.False(MooringLinePresentation.PartedFlashVisible(0.125 + 1e-6)); // 4 Hz:每 125 ms 亮滅切換
        Assert.True(MooringLinePresentation.PartedFlashVisible(0.25 + 1e-6));
        Assert.False(MooringLinePresentation.PartedFlashVisible(MooringLinePresentation.PartedFlashSeconds));
        Assert.False(MooringLinePresentation.PartedFlashVisible(10));
        Assert.False(MooringLinePresentation.PartedFlashVisible(-1));
    }

    [Theory]
    [InlineData("HEAD", true)]
    [InlineData("FWDSPRING", true)]
    [InlineData("fwdBreast", true)]
    [InlineData("bowLine", true)]
    [InlineData("STERN", false)]
    [InlineData("AFTSPRING", false)]
    [InlineData("aft_breast", false)]
    [InlineData("L3", null)]
    [InlineData(null, null)]
    public void FairleadEndFromLineName(string? id, bool? expected)
    {
        Assert.Equal(expected, MooringLinePresentation.ForwardFromLineId(id));
    }

    [Fact]
    public void FairleadEstimateFollowsNameThenBollardSide()
    {
        // 名稱決定艏艉;繫船柱在左舷 → 左舷導纜孔(y 負);位置與 SimCore ScenarioBodyPoint 具名點相同(±0.40 Lpp, ∓0.45 B)
        var (x, y, z) = MooringLinePresentation.FairleadBody("HEAD", -30, -20, Hull);
        Assert.InRange(x, 0.40 * Hull.Lpp - 1e-9, 0.40 * Hull.Lpp + 1e-9);
        Assert.InRange(y, -0.45 * Hull.Beam - 1e-9, -0.45 * Hull.Beam + 1e-9);
        Assert.True(z > 0);
        var (xs, ys, _) = MooringLinePresentation.FairleadBody("STERN", 30, 20, Hull);
        Assert.True(xs < 0 && ys > 0);
        // 名稱無法判斷 → 依繫船柱前後
        var (xa, _, _) = MooringLinePresentation.FairleadBody("L1", 25, -5, Hull);
        var (xb, _, _) = MooringLinePresentation.FairleadBody("L1", -25, -5, Hull);
        Assert.True(xa > 0 && xb < 0);
    }

    [Fact]
    public void BodyConversionsRoundTrip()
    {
        var own = new Pose(100, 200, 45, 0, 45);
        var (e, n) = MooringLinePresentation.FromBody(40, -8, own);
        var (f, s) = MooringLinePresentation.ToBody(e, n, own);
        Assert.InRange(f, 40 - 1e-9, 40 + 1e-9);
        Assert.InRange(s, -8 - 1e-9, -8 + 1e-9);
        // 艏向 045 時,艏前 10 m 在東北
        var (e2, n2) = MooringLinePresentation.FromBody(10, 0, own);
        Assert.True(e2 > own.East && n2 > own.North);
    }

    [Fact]
    public void SmokeBerthSegmentBuildsThreeDrawnLinesAndOneParted()
    {
        var frame = SmokeSequence.Frame(SmokeSequence.BerthSegmentStartFrame);
        Assert.NotNull(frame.Berth);
        Assert.Null(SmokeSequence.Frame(SmokeSequence.BerthSegmentStartFrame - 1).Berth);
        var own = new Pose(frame.Pos.X, frame.Pos.Y, frame.Heading, frame.Sog, frame.Cog);
        var area = SmokeSequence.SmokeBerthArea();
        var visuals = MooringLinePresentation.Build(frame.Berth, own, Hull, area.Berths);
        Assert.Equal(4, visuals.Count);
        var drawn = visuals.Where(v => v.Drawn).ToList();
        Assert.Equal(3, drawn.Count);
        Assert.Single(visuals, v => v.Parted);
        Assert.False(visuals.Single(v => v.Parted).Drawn);
        Assert.Equal(MooringTensionLevel.Normal, visuals.Single(v => v.Id == "HEAD").Level);
        Assert.Equal(MooringTensionLevel.High, visuals.Single(v => v.Id == "FWDSPRING").Level);
        Assert.Equal(MooringTensionLevel.Critical, visuals.Single(v => v.Id == "AFTSPRING").Level);
        Assert.All(visuals, v => Assert.True(v.FairleadEstimated));
        // 繫船柱在左舷(碼頭在左舷 22 m),所有導纜孔也在左舷;艏纜導纜孔在前、艉倒纜在後
        foreach (var v in drawn)
        {
            var (bf, bs) = MooringLinePresentation.ToBody(v.Bollard!.Value.East, v.Bollard.Value.North, own);
            var (ff, fs) = MooringLinePresentation.ToBody(v.Fairlead.East, v.Fairlead.North, own);
            Assert.True(bs < 0 && fs < 0, $"{v.Id} 繫船柱 {bs:F1} / 導纜孔 {fs:F1} 應在左舷");
            Assert.InRange(Math.Abs(bs), 20, 30);
            if (v.Id == "HEAD") Assert.True(bf > ff && ff > 0);
            if (v.Id == "AFTSPRING") Assert.True(bf > ff && ff < 0);
            if (v.Id == "FWDSPRING") Assert.True(bf < ff && ff > 0);
            Assert.True(v.Bollard.Value.Up > v.Fairlead.Up - 20 && v.Bollard.Value.Up > 0);
            var len = Math.Sqrt(Math.Pow(v.Bollard.Value.East - v.Fairlead.East, 2) + Math.Pow(v.Bollard.Value.North - v.Fairlead.North, 2));
            Assert.InRange(len, 10, 80);
        }
    }

    [Fact]
    public void LinesWithoutBollardOrNotLoadedAreNotDrawn()
    {
        var frame = SmokeSequence.Frame(SmokeSequence.BerthSegmentStartFrame);
        var own = new Pose(frame.Pos.X, frame.Pos.Y, frame.Heading, frame.Sog, frame.Cog);
        var berth = frame.Berth! with
        {
            Lines = new[]
            {
                new MooringLineState { Id = "HEAD", Status = "fast", TensionT = 10, MblT = 50, BollardId = "NOPE" },
                new MooringLineState { Id = "STERN", Status = "stowed", TensionT = 0, MblT = 50 },
                new MooringLineState { Id = "AFTSPRING", Status = "letGo", TensionT = 0, MblT = 50, BollardId = "P2" },
                new MooringLineState { Id = "FWDSPRING", Status = "slacking", TensionT = 5, MblT = 50, BollardId = "P3" },
            },
        };
        var visuals = MooringLinePresentation.Build(berth, own, Hull, SmokeSequence.SmokeBerthArea().Berths);
        Assert.Equal(2, visuals.Count); // stowed/letGo 不列入
        Assert.False(visuals.Single(v => v.Id == "HEAD").Drawn);
        Assert.Null(visuals.Single(v => v.Id == "HEAD").Bollard);
        Assert.True(visuals.Single(v => v.Id == "FWDSPRING").Drawn);
        Assert.Empty(MooringLinePresentation.Build(null, own, Hull, SmokeSequence.SmokeBerthArea().Berths));
        Assert.DoesNotContain(MooringLinePresentation.Build(berth, own, Hull, Array.Empty<BerthGeometry>()), v => v.Drawn);
    }

    [Fact]
    public void FindBollardPrefersStateBerthThenAnyBerth()
    {
        var a = new BerthGeometry("A", "A", new[] { new EnuPoint(0, 0), new EnuPoint(100, 0) }, "right", Array.Empty<(string, EnuPoint)>(), new[] { ("P1", new EnuPoint(10, -3)) });
        var b = new BerthGeometry("B", "B", new[] { new EnuPoint(0, 500), new EnuPoint(100, 500) }, "right", Array.Empty<(string, EnuPoint)>(), new[] { ("P1", new EnuPoint(10, 497)), ("P9", new EnuPoint(90, 497)) });
        var berths = new[] { a, b };
        Assert.Equal(497, MooringLinePresentation.FindBollard(berths, "B", "P1")!.Value.Pos.North);
        Assert.Equal(-3, MooringLinePresentation.FindBollard(berths, "A", "P1")!.Value.Pos.North);
        Assert.Equal(497, MooringLinePresentation.FindBollard(berths, "A", "P9")!.Value.Pos.North); // 不在 A,搜其他泊位
        Assert.Equal(-3, MooringLinePresentation.FindBollard(berths, "ZZ", "P1")!.Value.Pos.North); // 未知泊位 → 第一個找到者
        Assert.Null(MooringLinePresentation.FindBollard(berths, "A", "P2"));
        Assert.Null(MooringLinePresentation.FindBollard(berths, "A", null));
    }

    [Fact]
    public void SmokeReportRequiresDrawnLinesWhenStateHasLines()
    {
        SmokeFrame F(int i, int inState, int drawn) => new()
        {
            Frame = i, SimTime = i * 0.1, FrameTimeMs = 1, Nodes = 10, TargetsDrawn = 1, HarbourMeshes = 1,
            OwnEast = i * 0.41, OwnNorth = 0, Heading = 90, SogKn = 8, MooringLinesInState = inState, MooringLinesDrawn = drawn,
        };
        var frames = Enumerable.Range(0, 20).Select(i => F(i, 3, 3)).ToList();
        Assert.True(SmokeReport.Build(frames, "low", "bridge", Array.Empty<string>(), true, Array.Empty<string>()).Ok);
        var bad = frames.Take(19).Append(F(19, 3, 0)).ToList();
        var r = SmokeReport.Build(bad, "low", "bridge", Array.Empty<string>(), true, Array.Empty<string>());
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("繫纜"));
    }
}
