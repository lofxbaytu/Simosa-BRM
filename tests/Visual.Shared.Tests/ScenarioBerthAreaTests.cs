using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Visual.Shared.Area;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>
/// 情境泊位與視景區域對照的一致性(審查發現 utc-mooring:E13/E14/E17 的泊位不在 scenario-areas.json 對到的區域檔內,繫纜永遠畫不出來)。
/// 1. data/scenarios 中每個有 berth 的情境,AreaCatalogue.AreasForScenario(id) 載入的 BerthGeometry 必須含該情境的 berth.id 與所有 bollard id;
/// 2. 即使對照表漏列或錯列,AreaCatalogue 依 state.berth.id 掃描 data/areas/*.json 補上泊位所在的區域檔(視景的後備)。
/// </summary>
public class ScenarioBerthAreaTests
{
    private static IEnumerable<Scenario> BerthScenarios()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(TestPaths.Root, "data", "scenarios"), "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var sc = ScenarioLoader.Load(path);
            if (sc.Berth?.Definition is not null) yield return sc;
        }
    }

    private static List<BerthGeometry> LoadBerths(AreaCatalogue cat, IEnumerable<string> areaIds, Scenario sc)
    {
        var origin = new LocalTangentPlane(sc.Initial.Position!.Lat ?? 0.0, sc.Initial.Position.Lon ?? 0.0);
        return areaIds.SelectMany(id => AreaLoader.Load(cat.ResolvePath(id), origin).Berths).ToList();
    }

    private static void AssertBerthDrawable(Scenario sc, IReadOnlyList<BerthGeometry> berths, string context)
    {
        var berthId = sc.Berth!.Id!;
        var geom = berths.FirstOrDefault(b => string.Equals(b.Id, berthId, StringComparison.OrdinalIgnoreCase));
        Assert.True(geom is not null, $"{sc.Id}:{context} 載入的泊位 [{string.Join(",", berths.Select(b => b.Id))}] 不含情境泊位 {berthId}");
        foreach (var line in sc.Berth.Lines)
        {
            Assert.False(string.IsNullOrEmpty(line.Bollard), $"{sc.Id}:繫纜 {line.Id} 無 bollard");
            Assert.True(geom!.Bollards.Any(b => string.Equals(b.Id, line.Bollard, StringComparison.OrdinalIgnoreCase)),
                $"{sc.Id}:泊位 {berthId} 無繫船柱 {line.Bollard}(繫纜 {line.Id})");
        }
    }

    [Fact]
    public void CatalogueAreasContainEveryScenarioBerthAndBollard()
    {
        var cat = AreaCatalogue.Load(TestPaths.Root);
        var count = 0;
        foreach (var sc in BerthScenarios())
        {
            count++;
            var areas = cat.AreasForScenario(sc.Id);
            Assert.True(areas.Count > 0, $"{sc.Id}:scenario-areas.json 未列此情境");
            AssertBerthDrawable(sc, LoadBerths(cat, areas, sc), $"對照表區域 [{string.Join(",", areas)}]");
        }
        Assert.True(count >= 4, $"預期至少 4 個有泊位的情境(E13/E14/E17/E20),實得 {count}");
    }

    [Fact]
    public void CatalogueScansAreaFilesForBerthId()
    {
        var cat = AreaCatalogue.Load(TestPaths.Root);
        Assert.Equal(new[] { "mailiao-berth" }, cat.AreaIdsWithBerth("MLO-OIL-3"));
        Assert.Equal(new[] { "mailiao-small-oil-berth" }, cat.AreaIdsWithBerth("mlo-sob-1")); // 不分大小寫
        Assert.Equal(new[] { "kaohsiung-south-oil-berth" }, cat.AreaIdsWithBerth("KHH-SOB-1"));
        Assert.Empty(cat.AreaIdsWithBerth("NO-SUCH-BERTH"));
        Assert.Empty(cat.AreaIdsWithBerth(""));
    }

    [Fact]
    public void AreasForScenarioAppendsBerthAreaWhenCatalogueMisses()
    {
        // 故意錯列的對照(原始缺陷:E13 → 台中、E17 → 安平、E99 未列):依 state.berth.id 補上泊位所在區域檔,已含者不重複
        var cat = new AreaCatalogue(TestPaths.Root);
        cat.Add("E13", "taichung-harbour-entrance", "taichung-inner-harbour");
        cat.Add("E17", "anping-approach");
        cat.Add("E20", "kaohsiung-approach", "kaohsiung-e20-passage");
        Assert.Equal(new[] { "taichung-harbour-entrance", "taichung-inner-harbour", "mailiao-berth" }, cat.AreasForScenario("E13_tug_berthing_mailiao", "MLO-OIL-3"));
        Assert.Equal(new[] { "anping-approach", "kaohsiung-south-oil-berth" }, cat.AreasForScenario("E17_typhoon_departure_kaohsiung", "KHH-SOB-1"));
        Assert.Equal(new[] { "kaohsiung-approach", "kaohsiung-e20-passage" }, cat.AreasForScenario("E20_comprehensive_assessment_default", "KHH-E20-OIL"));
        Assert.Equal(new[] { "mailiao-small-oil-berth" }, cat.AreasForScenario("E99_unlisted", "MLO-SOB-1"));
        // 無 berth 或找不到的 berth:維持對照表結果
        Assert.Equal(new[] { "anping-approach" }, cat.AreasForScenario("E17_typhoon_departure_kaohsiung", null));
        Assert.Equal(new[] { "anping-approach" }, cat.AreasForScenario("E17_typhoon_departure_kaohsiung", "SMOKE-BERTH"));
        Assert.Empty(cat.AreasForScenario("E99_unlisted", null));
        // 以路徑給的區域(--area=data/areas/x.json)亦視為已含
        Assert.Equal(new[] { "data/areas/mailiao-berth.json" }, cat.WithBerthArea(new[] { "data/areas/mailiao-berth.json" }, "MLO-OIL-3"));
    }

    [Fact]
    public void FallbackMakesEveryScenarioBerthDrawableEvenWithEmptyCatalogue()
    {
        var cat = new AreaCatalogue(TestPaths.Root); // 空對照表
        foreach (var sc in BerthScenarios())
        {
            var areas = cat.AreasForScenario(sc.Id, sc.Berth!.Id);
            AssertBerthDrawable(sc, LoadBerths(cat, areas, sc), $"後備區域 [{string.Join(",", areas)}]");
        }
    }
}
