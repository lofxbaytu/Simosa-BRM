using System.Text.RegularExpressions;
using SimosaBRM.Packaging.Common;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 專案根目錄 VERSION 檔(tools/build-release.* 無 --version 時的預設發行版本;CHANGELOG.md 最新條目同版)
/// 與更新包版本規則(<see cref="PackageVersion"/>:1–4 段數字)一致,且 CHANGELOG.md 第一個版本標題與之相同。
/// </summary>
public sealed class VersionFileTests
{
    private static string Root => RepositoryPaths.FindRoot();

    [Fact]
    public void VersionFile_IsSingleLinePackageVersion()
    {
        var path = Path.Combine(Root, "VERSION");
        Assert.True(File.Exists(path), "專案根目錄應有 VERSION 檔(tools/build-release.* 預設版本)");
        var text = File.ReadAllText(path);
        var version = text.Trim();
        Assert.Matches(@"^[0-9]+(\.[0-9]+){0,3}$", version);          // 與 build-release.{sh,ps1} 的檢查相同
        Assert.True(PackageVersion.TryParse(version, out var parsed), $"VERSION 須可作為更新包版本:{version}");
        Assert.Equal(version, parsed.ToString());
        Assert.DoesNotContain("\n", text.TrimEnd('\n', '\r'));          // 只有一行
        Assert.True(PackageVersion.Compare(version, "0.1.0") > 0);
        // 版本方案 <年>.<月>.<輪次>:必須大於 build-release 舊後備方案 0.<日期>.<時分> 的任何值與文件示例 2026.11.1,否則船上會判為降版而拒絕更新包
        Assert.True(PackageVersion.Compare(version, "0.99999999.9999") > 0, $"VERSION {version} 須大於舊的 0.<日期>.<時分> 方案");
        Assert.True(PackageVersion.Compare(version, "2026.11.1") > 0, $"VERSION {version} 須大於文件示例 2026.11.1");
    }

    [Fact]
    public void Changelog_FirstVersionHeadingMatchesVersionFile()
    {
        var version = File.ReadAllText(Path.Combine(Root, "VERSION")).Trim();
        var changelog = Path.Combine(Root, "CHANGELOG.md");
        Assert.True(File.Exists(changelog), "專案根目錄應有 CHANGELOG.md");
        var first = Regex.Match(File.ReadAllText(changelog), @"^## \[?([0-9]+(?:\.[0-9]+){0,3})\]?", RegexOptions.Multiline);
        Assert.True(first.Success, "CHANGELOG.md 應有「## <版本>」標題");
        Assert.Equal(version, first.Groups[1].Value);
    }
}
