using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Transfer;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// <see cref="ShipReleaseReport"/>(教官端對船上回報的發行版狀態的讀法)與 <see cref="UntrustedText"/> 的單元測試:
/// 回報 / 沒有回報 / 沒有發行清單的分辨、版本欄位的白名單、降版資訊的檢查與說明的重新產生(與船上的 <see cref="ReleaseDowngrade.Describe"/> 同一個出處)、
/// 寫進與讀回 fleet-versions.json 的項目、顯示用的文字。
/// </summary>
public sealed class ShipReleaseReportTests
{
    private static ShipReleaseReport Read(string json) => ShipReleaseReport.Read(JsonNode.Parse(json)!.AsObject());

    /// <summary>降版的船(發行版 2026.11.1,先前 2026.12.1 + 軟體更新包 2026.12.2)的 statusResult 片段;<paramref name="rest"/> 接在後面(以逗號開頭)。</summary>
    private static string WithDowngrade(string rest = "") => "{\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":" + Downgrade + rest + "}";

    private const string Downgrade = """
        {"releaseVersion":"2026.11.1","basisVersion":"2026.12.2","previousReleaseVersion":"2026.12.1","softwareVersion":"2026.12.2","highestVersion":"2026.12.2","softwareStale":true,"message":"船上送來的說明"}
        """;

    [Fact]
    public void NotReported_ReportedWithoutDowngrade_AndReportedWithoutManifest_AreThreeDifferentStates()
    {
        var old = Read("""{"type":"statusResult","shipId":"FSB1","version":"2026.10.1"}""");
        Assert.False(old.Reported);
        Assert.Null(old.ReleaseVersion);
        Assert.Null(old.Downgrade);
        Assert.Empty(old.Unreadable);
        Assert.StartsWith("沒有回報發行版(船上的軟體是較舊的版本", old.Describe());
        Assert.Equal("發行版未回報(船上的軟體較舊)", old.Describe(brief: true));
        Assert.Empty(old.Warnings("FSB1"));

        var ok = Read("""{"type":"statusResult","releaseVersion":"2026.11.31"}""");
        Assert.True(ok.Reported);
        Assert.Equal("2026.11.31", ok.ReleaseVersion);
        Assert.Null(ok.Downgrade);
        Assert.Equal("發行版 2026.11.31", ok.Describe());
        Assert.Equal("發行版 2026.11.31", ok.Describe(brief: true));
        Assert.Empty(ok.Warnings("FSB1"));

        var noManifest = Read("""{"type":"statusResult","releaseVersion":null}""");
        Assert.True(noManifest.Reported);
        Assert.Null(noManifest.ReleaseVersion);
        Assert.Empty(noManifest.Unreadable);
        Assert.StartsWith("發行版不明(船上回報安裝根目錄的發行清單 manifest.json 不在或讀不到", noManifest.Describe());
        Assert.Empty(noManifest.Warnings("FSB1"));

        // 明確的 "releaseDowngrade": null 也是「回報沒有降版」
        var explicitNull = Read("""{"releaseVersion":"2026.11.31","releaseDowngrade":null}""");
        Assert.True(explicitNull.Reported);
        Assert.Null(explicitNull.Downgrade);
        Assert.Empty(explicitNull.Unreadable);
    }

    [Fact]
    public void SoftwareUpdateVersion_OnlyWhenNewerThanTheReleaseAndNoDowngrade()
    {
        var applied = Read("""{"releaseVersion":"2026.12.1","components":{"software":{"version":"2026.12.2"}}}""");
        Assert.Equal("2026.12.2", applied.SoftwareUpdateVersion);
        Assert.Equal("發行版 2026.12.1,軟體更新包 2026.12.2", applied.Describe());
        // 軟體更新包不比發行版新(之後以更新的安裝程式重新安裝過):只寫發行版
        Assert.Null(Read("""{"releaseVersion":"2026.12.3","components":{"software":{"version":"2026.12.2"}}}""").SoftwareUpdateVersion);
        Assert.Null(Read("""{"releaseVersion":"2026.12.2","components":{"software":{"version":"2026.12.2"}}}""").SoftwareUpdateVersion);
        // 不是版本的內容、型別不對:當成沒有
        Assert.Null(Read("""{"releaseVersion":"2026.12.1","components":{"software":{"version":"9999.1.1\u001b[31m"}}}""").SoftwareUpdateVersion);
        Assert.Null(Read("""{"releaseVersion":"2026.12.1","components":{"software":"2026.12.2"}}""").SoftwareUpdateVersion);
        Assert.Null(Read("""{"releaseVersion":"2026.12.1","components":[]}""").SoftwareUpdateVersion);
        // 有降版時由降版提醒說明,不寫「軟體更新包」
        var down = Read(WithDowngrade(",\"components\":{\"software\":{\"version\":\"2026.12.2\"}}"));
        Assert.Null(down.SoftwareUpdateVersion);
        Assert.Equal("發行版 2026.11.1(有降版提醒,見下方的警告)", down.Describe());
        Assert.Equal("發行版 2026.11.1(有降版提醒)", down.Describe(brief: true));
    }

    [Fact]
    public void Downgrade_IsValidated_AndTheExplanationIsRegeneratedNotCopied()
    {
        var r = Read(WithDowngrade());
        Assert.Empty(r.Unreadable);
        var d = r.Downgrade!;
        Assert.Equal(("2026.11.1", "2026.12.2", "2026.12.1", "2026.12.2", "2026.12.2", true),
            (d.ReleaseVersion, d.BasisVersion, d.PreviousReleaseVersion, d.SoftwareVersion, d.HighestVersion, d.SoftwareStale));
        Assert.DoesNotContain("船上送來的說明", d.Message);
        Assert.Equal(ReleaseDowngrade.Describe("2026.11.1", "2026.12.1", "2026.12.2", "2026.12.2", "2026.12.2"), d.Message);
        Assert.StartsWith("降版:目前的發行版 2026.11.1 比先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 舊 — ", d.Message);
        Assert.Contains("simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式", d.Message);

        var warning = Assert.Single(r.Warnings("FSB1(裝置 FSB1-LT01)"));
        Assert.Equal($"船上筆電 FSB1(裝置 FSB1-LT01)有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— {d.Message}。船上處理之後再同步一次(或再匯入一次隨身碟),教官筆電保存的這則提醒才會消失", warning);
        Assert.EndsWith("教官筆電保存的這則提醒才會消失(來源說明)", Assert.Single(r.Warnings("FSB1", "來源說明")));
    }

    [Fact]
    public void Downgrade_PartsFollowTheSameRulesAsTheShip()
    {
        // 只有先前的發行版比較新(沒有軟體更新包):不寫「status 顯示的軟體版次不是實際執行的程式」
        var onlyPrevious = Read("""{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.12.1","previousReleaseVersion":"2026.12.1","softwareStale":false}}""");
        Assert.Equal(ReleaseDowngrade.Describe("2026.11.1", "2026.12.1", null, null, "2026.12.1"), onlyPrevious.Downgrade!.Message);
        Assert.DoesNotContain("不是實際執行的程式", onlyPrevious.Downgrade.Message);
        Assert.False(onlyPrevious.Downgrade.SoftwareStale);

        // 軟體版次沒有標 softwareStale(installed.json 的 software 元件已不是那個更新包):不算在內;被覆蓋的軟體更新包最高版本另列
        var highest = Read("""{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.12.5","previousReleaseVersion":"2026.10.1","softwareVersion":"2026.12.9","highestVersion":"2026.12.5","softwareStale":false}}""");
        Assert.Equal(ReleaseDowngrade.Describe("2026.11.1", null, null, "2026.12.5", "2026.12.5"), highest.Downgrade!.Message);
        Assert.Contains("比被安裝覆蓋的軟體更新包最高版本 2026.12.5 舊", highest.Downgrade.Message);
        Assert.Equal("2026.10.1", highest.Downgrade.PreviousReleaseVersion);   // 原樣保留(不比目前的新也在,與船上的欄位相同)
        Assert.Equal("2026.12.9", highest.Downgrade.SoftwareVersion);

        // 頂層沒有發行版本而降版資訊合格(與教官站的讀法相同):用降版資訊裡的
        var fallback = Read("{\"releaseDowngrade\":" + Downgrade + "}");
        Assert.True(fallback.Reported);
        Assert.Equal("2026.11.1", fallback.ReleaseVersion);
        Assert.NotNull(fallback.Downgrade);
        Assert.Empty(fallback.Unreadable);
    }

    public static TheoryData<string, string[]> UnreadableCases() => new()
    {
        { """{"releaseVersion":20261101}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":true}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":["2026.11.1"]}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":{"a":{"b":{}}}}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":""}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":" 2026.11.1"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"2026.11.1\n"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"2026.11.1.2.3"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"2026.11.1-dev"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"\u001b[31m2026.11.1"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"00000000000000000000000000000000000000000001.1"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"2026.11.1","ReleaseVersion":"2026.11.2"}""", new[] { "releaseVersion" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":"降版"}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":7}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":[1,2]}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":false}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{}}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1"}}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.12.2"}}""", new[] { "releaseDowngrade" } },                      // 沒有任何一項比目前的新
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.12.2","softwareVersion":"2026.12.2"}}""", new[] { "releaseDowngrade" } },   // 沒有 softwareStale
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.12.2","softwareVersion":"2026.12.2","softwareStale":"true"}}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.11.1","previousReleaseVersion":"2026.11.1"}}""", new[] { "releaseDowngrade" } },   // 不比目前的新
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2099.1.1","previousReleaseVersion":"2026.12.1"}}""", new[] { "releaseDowngrade" } },    // 比較基準對不上
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.10.1","basisVersion":"2026.12.1","previousReleaseVersion":"2026.12.1"}}""", new[] { "releaseDowngrade" } },   // 與頂層的發行版本對不上
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":{"x":1},"basisVersion":[],"message":{"x":[1,2]}}}""", new[] { "releaseDowngrade" } },
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","releaseVersion":"2026.11.1","basisVersion":"2026.12.1","previousReleaseVersion":"2026.12.1"}}""", new[] { "releaseDowngrade" } },   // 重複的鍵
        { """{"releaseVersion":"2026.11.1","releaseDowngrade":null,"RELEASEDOWNGRADE":null}""", new[] { "releaseDowngrade" } },
        { """{"ReleaseVersion":5,"RELEASEDOWNGRADE":"x"}""", new[] { "releaseVersion", "releaseDowngrade" } },
    };

    [Theory]
    [MemberData(nameof(UnreadableCases))]
    public void WrongTypesAndFormats_AreUnreadable_NeverAnException_AndTheFieldsAreRemovedBeforeTypedParsing(string json, string[] unreadable)
    {
        var status = JsonNode.Parse(json)!.AsObject();
        var r = ShipReleaseReport.Read(status);
        Assert.True(r.Reported);
        Assert.Equal(unreadable, r.Unreadable);
        if (unreadable.Contains("releaseDowngrade")) Assert.Null(r.Downgrade);
        if (r.ReleaseVersion is not null) Assert.True(ShipReleaseReport.IsVersion(r.ReleaseVersion));
        Assert.Contains("讀不懂", r.Describe());
        var warning = Assert.Single(r.Warnings("FSB1"));
        Assert.Contains("教官筆電讀不懂(格式不符),可能有降版提醒沒有顯示;請在船上執行 simosa-pkg status 確認", warning);
        Assert.Contains(unreadable.Length == 2 ? "回報的發行版本與降版資訊," : unreadable[0] == "releaseVersion" ? "回報的發行版本," : "回報的降版資訊,", warning);

        // 拿掉這兩欄之後,其餘欄位照一般的方式解析(鍵名不分大小寫也要拿乾淨,否則型別不對的 "ReleaseVersion": 5 仍會讓解析失敗)
        status["type"] = "statusResult";
        status["version"] = "2026.10.1";
        ShipReleaseReport.RemoveFields(status);
        Assert.DoesNotContain(status, kv => kv.Key.StartsWith("release", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("2026.10.1", SyncProtocol.As<ShipStatus>(status).Version);
    }

    [Fact]
    public void UnreadableVersionWithAReadableDowngrade_ShowsBoth_WithoutClaimingAReminderMayBeMissing()
    {
        var r = Read("{\"releaseVersion\":5,\"releaseDowngrade\":" + Downgrade + "}");
        Assert.Equal(new[] { "releaseVersion" }, r.Unreadable);
        Assert.Null(r.ReleaseVersion);
        Assert.NotNull(r.Downgrade);
        Assert.Equal("發行版 2026.11.1(有降版提醒,見下方的警告)(部分內容讀不懂,見下方的警告)", r.Describe());
        var warnings = r.Warnings("FSB1");
        Assert.Equal(2, warnings.Count);
        Assert.StartsWith("船上筆電 FSB1 有降版提醒", warnings[0]);
        Assert.Equal("船上筆電 FSB1 回報的發行版本,教官筆電讀不懂(格式不符);請在船上執行 simosa-pkg status 確認", warnings[1]);
        // 寫進該船項目再讀回(store list):同樣的摘要
        var entry = new ShipVersionEntry { ShipId = "FSB1" };
        r.ApplyTo(entry);
        Assert.Null(entry.ReleaseVersion);
        Assert.Equal(new[] { "releaseVersion" }, entry.ReleaseUnreadable);
        var back = ShipReleaseReport.FromEntry(PackageJson.FromText<ShipVersionEntry>(PackageJson.ToText(entry), "entry"));
        Assert.Equal("發行版 2026.11.1(有降版提醒)(部分內容讀不懂)", back.Describe(brief: true));
        Assert.Equal(2, back.Warnings("FSB1").Count);
    }

    [Fact]
    public void OptionalDowngradeFieldsOfTheWrongType_AreIgnored_WhenTheRestStillAddsUp()
    {
        // 先前的發行版是亂碼、軟體更新包那一項合格而且撐得起比較基準:降版照樣成立,亂碼那一項不用
        var r = Read("""{"releaseVersion":"2026.11.1","releaseDowngrade":{"releaseVersion":"2026.11.1","basisVersion":"2026.12.2","previousReleaseVersion":"\u001b[31m../../x","softwareVersion":"2026.12.2","highestVersion":7,"softwareStale":true,"extra":{"deep":[{}]}}}""");
        Assert.Empty(r.Unreadable);
        Assert.Null(r.Downgrade!.PreviousReleaseVersion);
        Assert.Null(r.Downgrade.HighestVersion);
        Assert.Equal(ReleaseDowngrade.Describe("2026.11.1", null, "2026.12.2", null, "2026.12.2"), r.Downgrade.Message);
    }

    [Theory]
    [InlineData("2026.11.31", true)]
    [InlineData("1", true)]
    [InlineData("2026.11.31.4", true)]
    [InlineData("2147483647.0.0.1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("2026.11.31.4.5", false)]
    [InlineData("2026..1", false)]
    [InlineData(".2026", false)]
    [InlineData("2026.11.31 ", false)]
    [InlineData("2026.11.31\n", false)]
    [InlineData("v2026.11.31", false)]
    [InlineData("2026.11.٣١", false)]             // 非 ASCII 的數字
    [InlineData("99999999999.1", false)]          // 超過 int
    [InlineData("0000000000000000000000000000000000000000002026.11", false)]   // 超過 43 個字元
    public void IsVersion_OnlyOneToFourNumericSegments(string? text, bool expected) => Assert.Equal(expected, ShipReleaseReport.IsVersion(text));

    [Fact]
    public void FleetEntry_RoundTrip_AndSavedValuesAreRevalidatedOnTheWayOut()
    {
        var entry = new ShipVersionEntry { ShipId = "FSB1" };
        Read(WithDowngrade()).ApplyTo(entry);
        Assert.True(entry.ReleaseReported);
        Assert.Equal("2026.11.1", entry.ReleaseVersion);
        Assert.Null(entry.ReleaseUnreadable);
        var json = PackageJson.ToText(entry);
        var back = ShipReleaseReport.FromEntry(PackageJson.FromText<ShipVersionEntry>(json, "entry"));
        Assert.True(back.Reported);
        Assert.Equal(entry.ReleaseDowngrade!.Message, back.Downgrade!.Message);
        Assert.Equal("發行版 2026.11.1(有降版提醒)", back.Describe(brief: true));

        // 沒有回報:只有 releaseReported = false;沒有降版:沒有 releaseDowngrade 鍵;舊項目:三個鍵都沒有 → 不知道
        var notReported = new ShipVersionEntry { ShipId = "FSB1" };
        Read("""{"version":"2026.10.1"}""").ApplyTo(notReported);
        var notReportedJson = JsonNode.Parse(PackageJson.ToText(notReported))!.AsObject();
        Assert.False((bool)notReportedJson["releaseReported"]!);
        Assert.False(notReportedJson.ContainsKey("releaseVersion"));
        Assert.False(ShipReleaseReport.FromEntry(notReported).Reported);
        var old = PackageJson.FromText<ShipVersionEntry>("""{"shipId":"FSB1","version":"2026.10.1","components":{},"updatedUtc":"2026-11-09T03:00:00Z","via":"tcp"}""", "entry");
        Assert.Null(old.ReleaseReported);
        Assert.Null(ShipReleaseReport.FromEntry(old).Reported);
        Assert.Equal("發行版沒有記錄(再同步一次就會有)", ShipReleaseReport.FromEntry(old).Describe(brief: true));
        Assert.Empty(ShipReleaseReport.FromEntry(old).Warnings("FSB1"));

        // 檔案被手動改過(說明換成別的文字、版本不是版本):印出來的說明仍是重新產生的;不合格的內容當成讀不懂
        var edited = PackageJson.FromText<ShipVersionEntry>(json.Replace(entry.ReleaseDowngrade.Message, "\\u001b[31m改過的說明"), "entry");
        Assert.Contains("改過的說明", edited.ReleaseDowngrade!.Message);
        Assert.Equal(entry.ReleaseDowngrade.Message, ShipReleaseReport.FromEntry(edited).Downgrade!.Message);
        var broken = PackageJson.FromText<ShipVersionEntry>("""{"shipId":"FSB1","releaseReported":true,"releaseVersion":"\u001b[2J","releaseDowngrade":{"releaseVersion":"x","basisVersion":"y","message":"z"}}""", "entry");
        var brokenReport = ShipReleaseReport.FromEntry(broken);
        Assert.Null(brokenReport.ReleaseVersion);
        Assert.Null(brokenReport.Downgrade);
        Assert.Equal(new[] { "releaseVersion", "releaseDowngrade" }, brokenReport.Unreadable);
        Assert.Equal("發行版讀不懂", brokenReport.Describe(brief: true));

        // 軟體更新包版次由保存的 components 還原
        var applied = new ShipVersionEntry { ShipId = "FSB1", Components = { ["software"] = new ComponentVersion { Version = "2026.12.2" } } };
        Read("""{"releaseVersion":"2026.12.1"}""").ApplyTo(applied);
        Assert.Equal("發行版 2026.12.1,軟體更新包 2026.12.2", ShipReleaseReport.FromEntry(applied).Describe(brief: true));
    }

    [Fact]
    public void UpdateFleet_DoesNotCarryReleaseFieldsOver_UnlikeEnvironment()
    {
        var root = Path.Combine(SimCore.Ship.RepositoryPaths.FindRoot(), "build", "test-tmp", "packaging", "fleet-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new RecordStore(root);
            var env = new EnvironmentInfo { Os = "Windows", Dotnet = ".NET 8", CollectedUtc = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc) };
            var first = new ShipVersionEntry { ShipId = "FSB1", Environment = env, Via = "tcp" };
            Read(WithDowngrade()).ApplyTo(first);
            store.UpdateFleet(first);
            Assert.NotNull(store.LoadFleet().Ships["FSB1"].ReleaseDowngrade);

            // 下一次同步:船上是舊版軟體(沒有回報 environment 也沒有回報發行版)— environment 沿用上次已知值,發行版狀態如實取代
            var second = new ShipVersionEntry { ShipId = "FSB1", Via = "usb" };
            Read("""{"version":"2026.10.1"}""").ApplyTo(second);
            store.UpdateFleet(second);
            var saved = store.LoadFleet().Ships["FSB1"];
            Assert.Equal(env, saved.Environment);
            Assert.False(saved.ReleaseReported);
            Assert.Null(saved.ReleaseVersion);
            Assert.Null(saved.ReleaseDowngrade);
        }
        finally
        {
            PackagePaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void UntrustedText_StripsControlAndFormatCharacters_AndTruncates()
    {
        Assert.Equal("", UntrustedText.Clean(null));
        Assert.Equal("", UntrustedText.Clean(""));
        Assert.Equal("FSB1-LT01 船上筆電", UntrustedText.Clean("FSB1-LT01 船上筆電"));
        Assert.Equal("[2J[31m紅字[0m", UntrustedText.Clean("\u001b[2J\u001b[31m紅字\u001b[0m"));          // ESC 拿掉後 ANSI 序列只是普通文字
        Assert.Equal("ab", UntrustedText.Clean("a\r\n\t\0\u0007\u007f\u0085\u009bb"));                        // C0、DEL、C1
        Assert.Equal("ab", UntrustedText.Clean("a\u202e\u200b\u200d\u2066\ufeff\u2028\u2029b"));              // 雙向控制、零寬、BOM、行 / 段分隔
        Assert.Equal("a\ufffdb", UntrustedText.Clean("a\ud800b"));                                           // 不成對的代理字元換成 U+FFFD
        Assert.Equal("𝒳𝒴", UntrustedText.Clean("𝒳𝒴"));                                                       // 成對的代理字元(增補平面)原樣
        Assert.Equal(new string('x', 64) + "…", UntrustedText.Clean(new string('x', 5000), 64));
        Assert.Equal(new string('x', 64), UntrustedText.Clean(new string('x', 64), 64));
        Assert.Equal("船船…", UntrustedText.Clean("\u001b船\u001b船\u001b船", 2));                              // 以保留下來的字數計

        Assert.Equal("FSB1(裝置 FSB1-LT01)", ShipReleaseReport.ShipLabel("FSB1", "FSB1-LT01"));
        Assert.Equal("FSB1", ShipReleaseReport.ShipLabel("FSB1", null));
        Assert.Equal("FSB1[31m(裝置 LT)", ShipReleaseReport.ShipLabel("FSB1\u001b[31m", "L\u0007T"));
        Assert.Equal("(沒有船舶識別)", ShipReleaseReport.ShipLabel("\u001b\u0000", ""));
        Assert.Equal(64 + 1, ShipReleaseReport.ShipLabel(new string('A', 9000)).Length);
    }

    [Fact]
    public void ShipStatus_AlwaysWritesTheReleaseVersionKey_AndStillOmitsAnAbsentDowngrade()
    {
        var none = JsonNode.Parse(PackageJson.ToText(new ShipStatus { ShipId = "FSB1" }))!.AsObject();
        Assert.True(none.ContainsKey("releaseVersion"));
        Assert.Null(none["releaseVersion"]);
        Assert.False(none.ContainsKey("releaseDowngrade"));
        Assert.False(none.ContainsKey("environment"));   // 其他 null 欄位照舊省略
        var some = JsonNode.Parse(PackageJson.ToText(new ShipStatus { ReleaseVersion = "2026.11.31" }, indented: false))!.AsObject();
        Assert.Equal("2026.11.31", (string?)some["releaseVersion"]);
        // 讀回:舊版教官端的一般反序列化也讀得懂明確的 null
        Assert.Null(PackageJson.FromText<ShipStatus>("""{"type":"statusResult","releaseVersion":null}""", "status").ReleaseVersion);
    }

    [Fact]
    public void ReadShipStatusFile_ToleratesBomAndHostileReleaseFields_ButRejectsOversizeAndNonObjects()
    {
        var dir = Path.Combine(SimCore.Ship.RepositoryPaths.FindRoot(), "build", "test-tmp", "packaging", "status-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "status.json");
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("""{"type":"statusResult","shipId":"FSB1","version":"2026.10.1","releaseVersion":[1],"RELEASEDOWNGRADE":"x"}""")).ToArray());
            var (status, release) = UsbTransfer.ReadShipStatusFile(path);
            Assert.Equal(("FSB1", "2026.10.1"), (status.ShipId, status.Version));
            Assert.Null(status.ReleaseVersion);
            Assert.Null(status.ReleaseDowngrade);
            Assert.Equal(2, release.Unreadable.Count);

            File.WriteAllText(path, "[]");
            Assert.Contains("不是 JSON 物件", Assert.Throws<InvalidDataException>(() => UsbTransfer.ReadShipStatusFile(path)).Message);
            File.WriteAllText(path, "{\"pad\":\"" + new string('x', UsbTransfer.MaxStatusFileBytes) + "\"}");
            Assert.Contains("超過上限", Assert.Throws<InvalidDataException>(() => UsbTransfer.ReadShipStatusFile(path)).Message);
        }
        finally
        {
            PackagePaths.DeleteDirectory(dir);
        }
    }
}
