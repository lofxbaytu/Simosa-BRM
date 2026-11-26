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
        // 到船上確認的指令要看得到那一項:有沒有降版看 status 的警告;發行版本在 verify-install 的第一行(status 的文字輸出不印發行版本)
        Assert.Contains("教官筆電讀不懂(格式不符),可能有降版提醒沒有顯示;請在船上執行 simosa-pkg status(有降版時會印「警告:降版:…」)", warning);
        Assert.Equal(unreadable.Contains("releaseVersion"), warning.Contains("與 simosa-pkg verify-install(第一行「發行清單:版本 …」是發行版本)"));
        Assert.EndsWith("確認", warning);
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
        Assert.Equal("船上筆電 FSB1 回報的發行版本,教官筆電讀不懂(格式不符);請在船上執行 simosa-pkg verify-install(第一行「發行清單:版本 …」是發行版本)確認", warnings[1]);
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

    // ── 審查後修正(2026-11-26):兩船版次清單的更新 ─────────────────

    private static string NewStoreDir(string prefix)
        => Path.Combine(SimCore.Ship.RepositoryPaths.FindRoot(), "build", "test-tmp", "packaging", prefix + "-" + Guid.NewGuid().ToString("N")[..8]);

    private static EnvironmentInfo EnvAt(DateTime collectedUtc) => new() { Os = "Windows", Dotnet = ".NET 8", CollectedUtc = collectedUtc };

    [Fact]
    public void UpdateFleet_SkipIfOlder_KeepsTheNewerEntry_ButNeverGetsStuckOnUnknownOrImplausibleTimes()
    {
        // 審查 A-1 / B-1:隨身碟匯入(skipIfOlder)時,船上產生時間比已有的舊的狀態不取代;直連同步一律取代。
        // 任一邊的時間不知道(沒有環境資訊)或不合理(比教官筆電現在的時間晚超過一天)時照原本的作法取代 — 不會因為一個寫著很久以後的時間的檔案而卡住
        var root = NewStoreDir("fleet-age");
        try
        {
            var now = new DateTime(2026, 11, 26, 6, 0, 0, DateTimeKind.Utc);
            var store = new RecordStore(root, new ManualClock(new DateTimeOffset(now)));
            ShipVersionEntry Entry(string version, DateTime? statusUtc, string via)
                => new() { ShipId = "FSB1", Version = version, Environment = statusUtc is { } t ? EnvAt(t) : null, Via = via };
            string Saved() => store.LoadFleet().Ships["FSB1"].Version!;

            var first = store.UpdateFleet(Entry("direct", now.AddHours(-1), "tcp"));
            Assert.True(first.Saved);
            Assert.Null(first.Previous);
            Assert.False(first.AgeUnknown);

            // 較舊的隨身碟狀態:不取代,檔案內容原封不動;回傳兩邊的時間與原有的項目(顯示用)
            var before = File.ReadAllText(store.FleetPath);
            var older = store.UpdateFleet(Entry("stale", now.AddDays(-3), "usb"), skipIfOlder: true);
            Assert.False(older.Saved);
            Assert.Equal(now.AddDays(-3), older.StatusUtc);
            Assert.Equal(now.AddHours(-1), older.PreviousStatusUtc);
            Assert.Equal("tcp", older.Previous?.Via);
            Assert.Equal(before, File.ReadAllText(store.FleetPath));

            // 同一份狀態(時間相同)與較新的:取代
            Assert.True(store.UpdateFleet(Entry("same", now.AddHours(-1), "usb"), skipIfOlder: true).Saved);
            Assert.Equal("same", Saved());
            Assert.True(store.UpdateFleet(Entry("newer", now.AddMinutes(-5), "usb"), skipIfOlder: true).Saved);
            Assert.Equal("newer", Saved());

            // 直連(不給 skipIfOlder):時間較舊也取代(拿到的是當下的狀態;船上時鐘可能被調回去過)
            Assert.True(store.UpdateFleet(Entry("live", now.AddDays(-10), "tcp")).Saved);
            Assert.Equal("live", Saved());

            // 沒有環境資訊(較舊的船上軟體):無法判斷 → 取代,AgeUnknown;environment 沿用上次的
            var unknown = store.UpdateFleet(Entry("no-time", null, "usb"), skipIfOlder: true);
            Assert.True(unknown.Saved);
            Assert.True(unknown.AgeUnknown);
            Assert.Null(unknown.StatusUtc);
            Assert.Equal("no-time", Saved());
            Assert.Equal(now.AddDays(-10), store.LoadFleet().Ships["FSB1"].Environment!.CollectedUtc);

            // 時間寫成很久以後(船上時鐘超前,或檔案被改過):當成不知道,照常取代 …
            var future = store.UpdateFleet(Entry("future", now.AddYears(70), "usb"), skipIfOlder: true);
            Assert.True(future.Saved);
            Assert.Null(future.StatusUtc);
            Assert.True(future.AgeUnknown);
            // … 而且之後正常的狀態不會被它擋住(清單裡那個不合理的時間不拿來比較)
            var afterFuture = store.UpdateFleet(Entry("normal", now.AddMinutes(-1), "usb"), skipIfOlder: true);
            Assert.True(afterFuture.Saved);
            Assert.Null(afterFuture.PreviousStatusUtc);
            Assert.Equal("normal", Saved());
            // 一天以內的超前(時區設錯、時鐘沒對準)仍當成可用的時間
            Assert.Equal(now.AddHours(20), ShipVersionEntry.StatusUtcOf(EnvAt(now.AddHours(20)), now));
            Assert.Null(ShipVersionEntry.StatusUtcOf(EnvAt(now.AddHours(25)), now));
            Assert.Null(ShipVersionEntry.StatusUtcOf(EnvAt(default), now));                         // 沒有 collectedUtc(預設值)
            Assert.Null(ShipVersionEntry.StatusUtcOf(null, now));

            // 另一艘船的項目不受影響
            store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB2", Version = "other", Environment = EnvAt(now.AddDays(-30)), Via = "usb" }, skipIfOlder: true);
            Assert.Equal("normal", Saved());
            Assert.Equal("other", store.LoadFleet().Ships["FSB2"].Version);
        }
        finally
        {
            PackagePaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void UpdateFleet_CleansAndBoundsTheTextTheShipReported_BeforeItIsWritten()
    {
        // 審查 A-3:船上狀態裡既有的文字欄位原本原樣存進 fleet-versions.json(長度不限、U+202E 原樣)。現在寫檔之前清理並截斷,清單項數設上限;合法的內容不變
        var root = NewStoreDir("fleet-clean");
        try
        {
            var store = new RecordStore(root);
            var huge = new string('W', 500_000);
            var components = Enumerable.Range(0, 200).ToDictionary(i => $"kind{i}\u001b[2J", i => new ComponentVersion { Version = "1.0\r\n同步完成", PackageId = "upd-x\u202e", Summary = huge });
            var hostile = new ShipVersionEntry
            {
                ShipId = "FSB1", DeviceId = "LT\u001b]0;pwned\u0007\n" + huge, Version = "\u001b[2J9.9\r\n同步完成\u202e", PackageId = huge, Via = "usb",
                Components = components,
                ParameterSet = new ParameterSetRef { Id = "p\u0000", Version = "v1\n同步完成", Sha256 = huge, Path = huge, SourceRevisions = Enumerable.Range(0, 200).ToDictionary(i => $"DOC-{i}\u2028", i => (string?)huge) },
                Environment = new EnvironmentInfo { Os = "\u001b[31mWindows\r\n" + huge, Dotnet = ".NET 8\u200b", WebView2 = huge, CollectedUtc = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc) },
            };
            store.UpdateFleet(hostile);
            var text = File.ReadAllText(store.FleetPath);
            Assert.True(text.Length < 40_000, $"fleet-versions.json {text.Length} 字");
            Assert.DoesNotContain("\\u001b", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\r", text);
            Assert.DoesNotContain("\\n", text);
            foreach (var rune in text.EnumerateRunes())
                Assert.False(rune.Value is not ('\n' or '\r') && System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator, $"檔案含控制 / 格式字元 U+{rune.Value:X4}");
            var saved = store.LoadFleet().Ships["FSB1"];
            Assert.Equal("[2J9.9同步完成", saved.Version);
            Assert.Equal("LT]0;pwned" + new string('W', 54) + "…", saved.DeviceId);
            Assert.Equal(128 + 1, saved.PackageId!.Length);
            Assert.Equal(ShipVersionEntry.MaxListedItems, saved.Components.Count);
            Assert.Equal("1.0同步完成", saved.Components["kind0[2J"].Version);
            Assert.Equal("upd-x", saved.Components["kind0[2J"].PackageId);
            Assert.Equal(300 + 1, saved.Components["kind0[2J"].Summary!.Length);
            Assert.Equal(("p", "v1同步完成"), (saved.ParameterSet!.Id, saved.ParameterSet.Version));
            Assert.Equal(ShipVersionEntry.MaxListedItems, saved.ParameterSet.SourceRevisions.Count);
            Assert.Equal("[31mWindows" + new string('W', 200 - 11) + "…", saved.Environment!.Os);
            Assert.Equal(".NET 8", saved.Environment.Dotnet);
            Assert.Equal(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), saved.Environment.CollectedUtc);

            // 合法的內容一字不變
            var env = new EnvironmentInfo { Os = "Microsoft Windows 10.0.26100", Dotnet = ".NET 8.0.11", WebView2 = "130.0.2849.56", CollectedUtc = new DateTime(2026, 11, 9, 1, 0, 0, DateTimeKind.Utc) };
            var normal = new ShipVersionEntry
            {
                ShipId = "FSB2", DeviceId = "FSB2-LT01", Version = "2026.10.2", PackageId = "upd-20261017T050000-0a1b2c3d", Via = "tcp", Environment = env,
                Components = new() { ["parameters"] = new ComponentVersion { Version = "2026.10.2", PackageId = "upd-1", AppliedUtc = new DateTime(2026, 10, 17, 5, 0, 0, DateTimeKind.Utc), Summary = "DWO-007 第 4 版" } },
                ParameterSet = new ParameterSetRef { Id = "simosa-company-procedures", Version = "2026.10.2", Sha256 = new string('a', 64), Path = "data/parameters/company-procedures.yaml", SourceRevisions = new() { ["DWO-007"] = "4", ["DWO-009"] = null } },
            };
            var expected = PackageJson.ToText(normal);
            store.UpdateFleet(normal);
            var back = store.LoadFleet().Ships["FSB2"];
            back.UpdatedUtc = default;
            Assert.Equal(expected, PackageJson.ToText(back));

            // 沒有船舶識別(清理後是空的)不寫
            Assert.Throws<InvalidDataException>(() => store.UpdateFleet(new ShipVersionEntry { ShipId = "\u001b\u0000" }));
            Assert.Equal(2, store.LoadFleet().Ships.Count);
        }
        finally
        {
            PackagePaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UpdateFleet_WaitsForAnotherProcessHoldingTheLock_InsteadOfOverwritingItsUpdate()
    {
        // 審查 A-7:「讀 → 改 → 寫」原本只有行程內的 lock;兩個行程同時更新(一個直連同步、一個匯入另一艘船的隨身碟)時後寫的會把先寫的那艘船蓋掉。
        // 現在以鎖定檔跨行程序列化。這裡以「另一個持有者獨占開啟鎖定檔」代表另一個行程(獨占開啟在同一個行程內也互斥)
        var root = NewStoreDir("fleet-lock");
        try
        {
            var store = new RecordStore(root);
            store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "1" });
            Assert.True(File.Exists(store.FleetLockPath));
            Task<FleetUpdate> waiting;
            using (new FileStream(store.FleetLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // 另一個行程此時寫入 FSB2(它持有鎖定)
                var other = store.LoadFleet();
                waiting = Task.Run(() => store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "2" }));
                await Task.Delay(400);
                Assert.False(waiting.IsCompleted, "鎖定檔被佔用時 UpdateFleet 應該等待");
                Assert.Equal("1", store.LoadFleet().Ships["FSB1"].Version);
                other.Ships["FSB2"] = new ShipVersionEntry { ShipId = "FSB2", Version = "other" };
                PackageJson.SaveAtomic(store.FleetPath, other);
            }
            Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Saved);
            var fleet = store.LoadFleet();
            Assert.Equal("2", fleet.Ships["FSB1"].Version);
            Assert.Equal("other", fleet.Ships["FSB2"].Version);                                    // 另一個行程寫的那艘船還在

            // 等不到(另一個行程一直佔著):明確的錯誤,不寫入
            store.FleetLockTimeout = TimeSpan.FromMilliseconds(300);
            using (new FileStream(store.FleetLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() => store.UpdateFleet(new ShipVersionEntry { ShipId = "FSB1", Version = "3" }));
                Assert.Contains("另一個 simosa-pkg 正在更新兩船版次清單", ex.Message);
                Assert.Equal("2", store.LoadFleet().Ships["FSB1"].Version);

                // store backup 不複製鎖定檔(它被獨占開啟時複製會失敗),其餘檔案照常備份
                var (target, _) = store.Backup(NewStoreDir("fleet-lock-backup"));
                try
                {
                    Assert.True(File.Exists(Path.Combine(target, "fleet-versions.json")));
                    Assert.False(File.Exists(Path.Combine(target, "fleet-versions.json.lock")));
                }
                finally
                {
                    PackagePaths.DeleteDirectory(Path.GetDirectoryName(target)!);
                }
            }
        }
        finally
        {
            PackagePaths.DeleteDirectory(root);
        }
    }
}
