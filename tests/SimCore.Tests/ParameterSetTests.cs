using SimosaBRM.SimCore.Parameters;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>公司程序參數集載入與檢查(情境引擎設計第 2.1、2.2、7.1 節)。</summary>
public class ParameterSetTests
{
    private const string Base = """
        id: test-set
        version: "2026.10.1"
        sources:
          DOC-1: { title: 文件一, revision: "3" }
        parameters:

        """;

    [Fact]
    public void LoadsRepositoryParameterSet()
    {
        var set = TestData.Parameters();
        Assert.Equal("simosa-company-procedures", set.Id);
        Assert.Equal("2026.10.1", set.Version);
        Assert.Equal(64, set.Sha256.Length);
        Assert.True(set.TryGet("navigation.xtd.channel_m", out var xtd));
        Assert.Equal(50.0, xtd.NumberValue);
        Assert.Equal("m", xtd.Unit);
        Assert.Equal("SMS-NAV-012", xtd.Source);
        Assert.Equal("4.1", xtd.Clause);
        Assert.Equal("2", set.Sources["SMS-NAV-012"].Revision);
        Assert.True(set.TryGet("navigation.ukc.channel_fraction", out var f) && f.Unit == "fractionOfDraft");
        Assert.True(set.TryGet("assessment.passMark", out var pm) && pm.Unit == "score" && pm.NumberValue == 70);
        Assert.True(set.Sources["PORT-MLW"].External);
    }

    [Fact]
    public void MissingSourceDefinitionIsRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ParameterSetLoader.FromYaml(Base + "  a.b.c_nm: { value: 1, unit: nm, source: DOC-9 }\n"));
        Assert.Contains("DOC-9", ex.Message);
    }

    [Fact]
    public void UnitSuffixMismatchIsRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ParameterSetLoader.FromYaml(Base + "  a.b.c_nm: { value: 1, unit: m, source: DOC-1 }\n"));
        Assert.Contains("後綴", ex.Message);
        Assert.Throws<InvalidDataException>(() => ParameterSetLoader.FromYaml(Base + "  a.b.c: { value: 1, unit: m, source: DOC-1 }\n"));
        // 無後綴鍵只允許 score/count/bool/text
        var ok = ParameterSetLoader.FromYaml(Base + "  a.b.passMark: { value: 70, unit: score, source: DOC-1 }\n  a.b.flag: { value: true, unit: bool, source: DOC-1 }\n");
        Assert.Equal(2, ok.Parameters.Count);
    }

    [Fact]
    public void VersionFormatAndRangeAreChecked()
    {
        Assert.Throws<InvalidDataException>(() => ParameterSetLoader.FromYaml(Base.Replace("\"2026.10.1\"", "v1") + "  a.b.c_m: { value: 1, unit: m, source: DOC-1 }\n"));
        Assert.Throws<InvalidDataException>(() => ParameterSetLoader.FromYaml(Base + "  a.b.c_m: { value: 1, unit: m, source: DOC-1, min: 2 }\n"));
        Assert.Throws<InvalidDataException>(() => ParameterSetLoader.FromYaml(Base + "  a.b.c_m: { value: 1, unit: xyz, source: DOC-1 }\n"));
    }

    [Fact]
    public void Sha256IsStableForSameBytes()
    {
        var a = TestData.Parameters();
        var b = TestData.Parameters();
        Assert.Equal(a.Sha256, b.Sha256);
        var c = ParameterSetLoader.FromYaml(File.ReadAllText(TestData.ParameterSetPath));
        Assert.Equal(a.Sha256, c.Sha256);
        Assert.NotEqual(a.Sha256, ParameterSetLoader.FromYaml(Base + "  a.b.c_m: { value: 1, unit: m, source: DOC-1 }\n").Sha256);
    }
}
