using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 外力一致性黃金檔的擴充案例與涵蓋檢查(規劃書第 14 章;physics-tugs-anchors-mooring.md 第 10.3 節、physics-bank-effect.md 第 10 節)。
/// 擴充案例(Golden/export_external_reference.py 產生,本測試只讀)覆蓋原四案例未觸發的路徑:碼頭硬接觸 quayContact、
/// 繫纜超過 MBL 斷裂、繫纜 slack 到最大長度、拖船由 free 帶纜、錨鏈送到鏈端、heave 經懸鏈線 suspended/taut 到離底與收妥
/// (含絞盤超載與剎車滑出)、倒俥沿岸。比對方式與 <see cref="ExternalForceGoldenTests"/> 完全相同(直接呼叫其比對方法)。
/// </summary>
public class ExternalForceGoldenCoverageTests
{
    private readonly ITestOutputHelper _out;
    public ExternalForceGoldenCoverageTests(ITestOutputHelper output) => _out = output;

    /// <summary>與 ExternalForceGoldenTests.ExternalEventKinds 相同(外力事件序列比對的種類)。</summary>
    private static readonly string[] ExternalEventKinds =
    {
        "tugCommand", "tugLineParted", "anchorLetGo", "anchorCommand", "anchorDragging", "anchorHolding", "anchorBrakeSlipping",
        "anchorBroughtUp", "anchorAweigh", "anchorHoused", "anchorChainFullyOut", "anchorWindlassStalled", "fenderContact", "quayContact",
        "mooringCommand", "mooringLineParted",
    };

    /// <summary>標頭之後固定的共同欄數(tick,t,x,y,psi_deg,u,v,r,rpm,ext_X,ext_Y,ext_N);其後為案例自選的項目欄。</summary>
    private const int CommonColumns = 12;

    [Theory]
    [InlineData("ext_quay_line_part.csv")]
    [InlineData("ext_anchor_heave_house.csv")]
    [InlineData("ext_bank_astern.csv")]
    public void PythonConsistency_ExtendedExternalCases(string file)
        => new ExternalForceGoldenTests(_out).PythonConsistency_ExternalForcesWithinOnePercent(file);

    private static IEnumerable<string> ComparedFiles()
    {
        foreach (var (type, method) in new[]
                 {
                     (typeof(ExternalForceGoldenTests), nameof(ExternalForceGoldenTests.PythonConsistency_ExternalForcesWithinOnePercent)),
                     (typeof(ExternalForceGoldenCoverageTests), nameof(PythonConsistency_ExtendedExternalCases)),
                 })
            foreach (var d in type.GetMethod(method)!.GetCustomAttributes<InlineDataAttribute>())
                yield return (string)d.GetData(null!).Single()[0];
    }

    private static (string[] Columns, List<double[]> Rows, List<string> EventKinds) Read(string file)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string[]? cols = null;
        var rows = new List<double[]>();
        var kinds = new List<string>();
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, file)))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var body = line.TrimStart('#').Trim();
                if (body.StartsWith("event: tick=", StringComparison.Ordinal))
                    kinds.Add(body.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
                continue;
            }
            if (cols is null) { cols = line.Split(','); continue; }
            rows.Add(line.Split(',').Select(v => double.Parse(v, inv)).ToArray());
        }
        return (cols ?? throw new InvalidDataException(file), rows, kinds);
    }

    [Fact]
    public void EveryExternalGoldenFileIsCompared()
    {
        var onDisk = Directory.GetFiles(GoldenDirectory, "ext_*.csv").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var compared = ComparedFiles().OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(onDisk, compared);
    }

    [Fact]
    public void GoldenItemColumnsAreNotIdenticallyZero()
    {
        // 全程為 0 的項目欄只做「絕對差 < 0.01」的空比較,沒有回歸保護
        foreach (var file in ComparedFiles())
        {
            var (cols, rows, _) = Read(file);
            for (var j = CommonColumns; j < cols.Length; j++)
                Assert.True(rows.Max(r => Math.Abs(r[j])) > 0.0, $"{file} {cols[j]} 全程為 0");
        }
    }

    [Fact]
    public void GoldenFilesCoverAllExternalEventKinds()
    {
        var kinds = ComparedFiles().SelectMany(f => Read(f).EventKinds).ToHashSet(StringComparer.Ordinal);
        var missing = ExternalEventKinds.Where(k => !kinds.Contains(k)).ToArray();
        Assert.True(missing.Length == 0, $"黃金檔未涵蓋:{string.Join(", ", missing)}");
    }

    [Fact]
    public void BankAsternGoldenReversesSpeedWithNonZeroBankForce()
    {
        // 倒俥沿岸(u < 0 時前導/後隨對調、力矩反號)須在比對範圍內
        var (cols, rows, _) = Read("ext_bank_astern.csv");
        int iu = Array.IndexOf(cols, "u"), iy = Array.IndexOf(cols, "bank:Y_kN");
        Assert.True(rows.Max(r => r[iu]) > 1.0);
        Assert.Contains(rows, r => r[iu] < -0.5 && Math.Abs(r[iy]) > 1.0);
    }
}
