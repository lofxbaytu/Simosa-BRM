using System.Text.Json;
using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Sync;

/// <summary>
/// 教官端對「船上回報的發行版狀態」的讀法與顯示(<c>simosa-pkg sync</c>、隨身碟 <c>import --role instructor</c>、<c>store list</c> 共用;
/// docs/design/packaging-sync.md 第 5.1 節末「同步與隨身碟匯入時的降版提醒」、第 7 節)。
/// 來源是直連 <c>statusResult</c> 與隨身碟 <c>status.json</c> 的 <c>releaseVersion</c> / <c>releaseDowngrade</c> — 與船上
/// <c>simosa-pkg status --json</c> 同一份欄位(<see cref="ShipStatus"/>)。
/// <para>
/// <b>這些內容不可信</b>:statusResult 沒有自己的簽章(握手時船上的裝置金鑰簽過 nonce,之後同一條 TCP 連線上的訊息沒有加密也沒有訊息驗證碼),
/// 隨身碟的 status.json 完全沒有簽章。所以這裡不用一般的反序列化(型別不對會讓整個同步在最後一步失敗),而是逐欄寬鬆讀取:
/// </para>
/// <list type="bullet">
/// <item>版本欄位只接受 1 至 4 段數字(<see cref="IsVersion"/>;與 simosa-web 送給教官站的白名單相同),其他內容一律不用。</item>
/// <item>降版說明<b>不採用船上送來的文字</b>:以通過檢查的版本欄位,用與船上 status 相同的 <see cref="ReleaseDowngrade.Describe"/> 重新產生
/// (船上與教官端同一版時,文字與船上 <c>simosa-pkg status</c> 的警告一字不差)。印到終端機與寫進 fleet-versions.json 的因此只有數字版本與程式自己的文字,
/// 控制字元、ANSI 跳脫序列、超長字串都到不了輸出。</item>
/// <item>型別或格式不對的欄位記在 <see cref="Unreadable"/>(同步照常完成、結束碼不變,另印一則「讀不懂」的警告 — 不把可能存在的降版提醒悄悄丟掉)。</item>
/// </list>
/// 「船上沒有回報」與「船上回報沒有降版」分開:兩個鍵都不在 = 沒有回報(加入這兩欄之前的船上軟體);有 <c>releaseVersion</c> 鍵(本版起沒有發行清單時寫成
/// <c>null</c>)而沒有降版資訊 = 回報沒有降版。
/// </summary>
public sealed class ShipReleaseReport
{
    public const string VersionField = "releaseVersion";
    public const string DowngradeField = "releaseDowngrade";

    /// <summary>
    /// 船上有沒有回報發行版狀態:true = 有(statusResult / status.json 有 <c>releaseVersion</c> 或 <c>releaseDowngrade</c> 任一個鍵);
    /// false = 沒有(船上的軟體較舊);null = 不知道(fleet-versions.json 裡加入這個欄位之前寫的項目,只有 <see cref="FromEntry"/> 會給)。
    /// </summary>
    public bool? Reported { get; init; }
    /// <summary>船上安裝根目錄 manifest.json 的發行版本(通過檢查的才有;船上回報沒有發行清單、沒有回報或讀不懂時 null)</summary>
    public string? ReleaseVersion { get; init; }
    /// <summary>降版(通過檢查、說明已重新產生;沒有時 null)</summary>
    public ReleaseDowngrade? Downgrade { get; init; }
    /// <summary>
    /// 套用在這個發行版之上的軟體更新包版次(status 的 components.software.version):沒有降版而且比發行版新時才有 — 這時船上實際執行的是它的程式
    /// (與教官站頁首「軟體更新 &lt;版次&gt;」同一個規則);其餘 null。
    /// </summary>
    public string? SoftwareUpdateVersion { get; init; }
    /// <summary>船上回報了、但讀不懂的欄位(<see cref="VersionField"/>、<see cref="DowngradeField"/>);都讀得懂時是空的</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = Array.Empty<string>();

    /// <summary>版本:1 至 4 段數字(<see cref="PackageVersion"/>),只有數字與句點、最長 43 個字元(4 段 × 10 位數 + 3 個句點)。</summary>
    public static bool IsVersion(string? text)
        => text is { Length: > 0 and <= 43 } && text.All(c => char.IsAsciiDigit(c) || c == '.') && PackageVersion.TryParse(text, out _);

    /// <summary>
    /// 讀 statusResult / status.json(已解析成 JSON 物件)裡的發行版狀態。<b>不丟例外</b>:任何型別或格式不對都記在 <see cref="Unreadable"/>。
    /// 鍵名不分大小寫(與一般的反序列化相同);同一個欄位出現不只一種大小寫時當成讀不懂。
    /// </summary>
    public static ShipReleaseReport Read(JsonObject status)
    {
        try
        {
            return ReadCore(status);
        }
        catch (Exception ex) when (IsJsonAccessError(ex))
        {
            // 連鍵都列不出來(頂層有重複的鍵等):當成回報了但全部讀不懂
            return new ShipReleaseReport { Reported = true, Unreadable = new[] { VersionField, DowngradeField } };
        }
    }

    /// <summary>
    /// 把這兩個欄位從 JSON 物件拿掉(不分大小寫):它們已由 <see cref="Read"/> 寬鬆讀取,其餘欄位接著才交給一般的反序列化 —
    /// 這兩欄的型別不對時不會讓整則 statusResult / status.json 解析失敗。
    /// </summary>
    public static void RemoveFields(JsonObject status)
    {
        foreach (var key in Keys(status, VersionField).Concat(Keys(status, DowngradeField)).ToList()) status.Remove(key);
    }

    /// <summary>JsonNode 存取時可能出現的例外:重複的鍵(ArgumentException)、字串含不合法的 UTF-8(InvalidOperationException)等。</summary>
    internal static bool IsJsonAccessError(Exception ex)
        => ex is InvalidOperationException or ArgumentException or FormatException or JsonException or KeyNotFoundException;

    private static List<string> Keys(JsonObject o, string name)
        => o.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList();

    private static ShipReleaseReport ReadCore(JsonObject status)
    {
        var versionKeys = Keys(status, VersionField);
        var downgradeKeys = Keys(status, DowngradeField);
        if (versionKeys.Count == 0 && downgradeKeys.Count == 0) return new ShipReleaseReport { Reported = false };

        var unreadable = new List<string>();
        string? version = null;
        if (versionKeys.Count > 1) unreadable.Add(VersionField);
        else if (versionKeys.Count == 1 && status[versionKeys[0]] is { } v)   // JSON null:船上回報沒有發行清單(manifest.json 不在或讀不到)
        {
            version = VersionOf(v);
            if (version is null) unreadable.Add(VersionField);
        }

        ReleaseDowngrade? downgrade = null;
        if (downgradeKeys.Count > 1) unreadable.Add(DowngradeField);
        else if (downgradeKeys.Count == 1 && status[downgradeKeys[0]] is { } d)
        {
            downgrade = ReadDowngrade(d);
            // 降版資訊裡「目前的發行版」與頂層的發行版本是同一個值(船上由同一份 manifest.json 產生);對不上就不是我們認得的內容
            if (downgrade is not null && version is not null && PackageVersion.Compare(version, downgrade.ReleaseVersion) != 0) downgrade = null;
            if (downgrade is null) unreadable.Add(DowngradeField);
        }
        // 有降版資訊而頂層沒有(或是 null)發行版本:用降版資訊裡的(與教官站的讀法相同)
        if (version is null && downgrade is not null && !unreadable.Contains(VersionField)) version = downgrade.ReleaseVersion;

        string? softwareUpdate = null;
        if (downgrade is null && version is not null && !unreadable.Contains(DowngradeField)
            && SoftwareComponentVersion(status) is { } software && PackageVersion.Compare(software, version) > 0)
            softwareUpdate = software;
        return new ShipReleaseReport { Reported = true, ReleaseVersion = version, Downgrade = downgrade, SoftwareUpdateVersion = softwareUpdate, Unreadable = unreadable };
    }

    /// <summary>字串而且是版本才回傳;其他型別、其他內容一律 null。</summary>
    private static string? VersionOf(JsonNode? node)
    {
        try
        {
            return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && IsVersion(text) ? text : null;
        }
        catch (Exception ex) when (IsJsonAccessError(ex))
        {
            return null;
        }
    }

    private static string? SoftwareComponentVersion(JsonObject status)
    {
        try
        {
            return status["components"] is JsonObject components && components[UpdateKinds.Software] is JsonObject software ? VersionOf(software["version"]) : null;
        }
        catch (Exception ex) when (IsJsonAccessError(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// 降版資訊(status 的 releaseDowngrade 物件)→ 通過檢查的 <see cref="ReleaseDowngrade"/>;不是物件、缺目前的發行版、
    /// 沒有任何一項比目前的發行版新、或比較基準與各項對不上時回傳 null(讀不懂)。規則與船上的 <see cref="DowngradeRecord.Evaluate"/> 相同:
    /// 先前的發行版與被覆蓋的軟體更新包最高版本只在比目前的發行版新時列出;軟體元件那一項另外要 <c>softwareStale</c> 為 true。
    /// 個別的選用欄位型別或格式不對時當成沒有;說明文字(<c>message</c>)與其他多出的欄位不看。
    /// </summary>
    private static ReleaseDowngrade? ReadDowngrade(JsonNode node)
    {
        try
        {
            if (node is not JsonObject o) return null;
            var release = VersionOf(o["releaseVersion"]);
            var reportedBasis = VersionOf(o["basisVersion"]);
            if (release is null || reportedBasis is null) return null;
            bool Newer(string? version) => version is not null && PackageVersion.Compare(version, release) > 0;
            var previousRaw = VersionOf(o["previousReleaseVersion"]);
            var softwareRaw = VersionOf(o["softwareVersion"]);
            var highestRaw = VersionOf(o["highestVersion"]);
            var stale = o["softwareStale"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.True;
            var previous = Newer(previousRaw) ? previousRaw : null;
            var software = stale && Newer(softwareRaw) ? softwareRaw : null;
            var highest = Newer(highestRaw) ? highestRaw : null;
            var basis = DowngradeRecord.Max(DowngradeRecord.Max(previous, software), highest);
            // 比較基準是上面三項中的最大者(船上也是這樣算);對不上表示內容被改過或是這一版不認得的新格式 → 讀不懂,請操作人員到船上確認
            if (basis is null || PackageVersion.Compare(basis, reportedBasis) != 0) return null;
            return new ReleaseDowngrade
            {
                ReleaseVersion = release, BasisVersion = basis, PreviousReleaseVersion = previousRaw, SoftwareVersion = softwareRaw, HighestVersion = highestRaw,
                SoftwareStale = software is not null,
                Message = ReleaseDowngrade.Describe(release, previous, software, highest, basis),
            };
        }
        catch (Exception ex) when (IsJsonAccessError(ex))
        {
            return null;
        }
    }

    // ── fleet-versions.json ───────────────────────────────

    /// <summary>寫進兩船版次清單的該船項目(每次同步整筆取代:這次沒有降版,上一次保存的提醒就不在了)。</summary>
    public void ApplyTo(ShipVersionEntry entry)
    {
        entry.ReleaseReported = Reported;
        entry.ReleaseVersion = ReleaseVersion;
        entry.ReleaseDowngrade = Downgrade;
        entry.ReleaseUnreadable = Unreadable.Count > 0 ? Unreadable.ToList() : null;
    }

    /// <summary>
    /// 由兩船版次清單的項目還原(<c>store list</c> 顯示保存的提醒)。保存的欄位再經過一次 <see cref="Read"/> 的檢查,
    /// 降版說明也是重新產生的 — 檔案被手動改過或是別的版本寫的,印出來的仍只有通過檢查的版本與程式自己的文字。
    /// 沒有這些欄位的舊項目(加入這項功能之前同步的)<see cref="Reported"/> 為 null。
    /// </summary>
    public static ShipReleaseReport FromEntry(ShipVersionEntry entry)
    {
        var reported = entry.ReleaseReported ?? (entry.ReleaseVersion is not null || entry.ReleaseDowngrade is not null ? true : null);
        if (reported is not true) return new ShipReleaseReport { Reported = reported };
        var status = new JsonObject { [VersionField] = entry.ReleaseVersion };
        if (entry.ReleaseDowngrade is { } downgrade) status[DowngradeField] = JsonSerializer.SerializeToNode(downgrade, PackageJson.Compact);
        if (entry.Components?.GetValueOrDefault(UpdateKinds.Software)?.Version is { } software)
            status["components"] = new JsonObject { [UpdateKinds.Software] = new JsonObject { ["version"] = software } };
        var read = Read(status);
        var saved = (entry.ReleaseUnreadable ?? new()).Where(f => f is VersionField or DowngradeField);
        var unreadable = read.Unreadable.Concat(saved).Distinct().ToList();
        return new ShipReleaseReport
        {
            Reported = true, ReleaseVersion = read.ReleaseVersion, Downgrade = read.Downgrade, Unreadable = unreadable,
            // 保存時降版資訊讀不懂的話,不能說船上執行的是軟體更新包
            SoftwareUpdateVersion = unreadable.Contains(DowngradeField) ? null : read.SoftwareUpdateVersion,
        };
    }

    // ── 顯示(給船上 / 教官端的操作人員看)──────────────────

    /// <summary>「FSB1」或「FSB1(裝置 FSB1-LT01)」;識別來自對方,先經 <see cref="UntrustedText.Clean"/>(去控制字元、最多 64 字)。</summary>
    public static string ShipLabel(string? shipId, string? deviceId = null)
    {
        var ship = UntrustedText.Clean(shipId, 64);
        if (ship.Length == 0) ship = "(沒有船舶識別)";
        var device = UntrustedText.Clean(deviceId, 64);
        return device.Length == 0 ? ship : $"{ship}(裝置 {device})";
    }

    /// <summary>
    /// 一行以內的摘要(不含船名)。<paramref name="brief"/>:<c>store list</c> 的「版次」行用的短版;否則是同步 / 隨身碟匯入時印的那一行。
    /// 例:「發行版 2026.11.31」、「發行版 2026.12.1,軟體更新包 2026.12.2」、「發行版 2026.11.1(有降版提醒,見下方的警告)」、
    /// 「沒有回報發行版(船上的軟體是較舊的版本…)」。
    /// </summary>
    public string Describe(bool brief = false)
    {
        if (Reported is null) return brief ? "發行版沒有記錄(再同步一次就會有)" : "發行版沒有記錄(這筆資料是加入這項功能之前同步的,再同步一次就會有)";
        if (Reported is false)
            return brief ? "發行版未回報(船上的軟體較舊)"
                : "沒有回報發行版(船上的軟體是較舊的版本,還不會回報發行版與降版提醒;船上更新軟體之後,再同步一次就會顯示)";
        var version = ReleaseVersion ?? Downgrade?.ReleaseVersion;
        var below = brief ? "" : ",見下方的警告";
        if (version is null && Unreadable.Contains(VersionField)) return brief ? "發行版讀不懂" : "發行版讀不懂(見下方的警告)";
        var text = version is not null
            ? $"發行版 {version}" + (SoftwareUpdateVersion is { } software ? $",軟體更新包 {software}" : "")
            : brief ? "發行版不明(船上沒有發行清單 manifest.json)"
            : "發行版不明(船上回報安裝根目錄的發行清單 manifest.json 不在或讀不到;在船上執行 simosa-pkg verify-install 可以確認安裝是否完整)";
        if (Downgrade is not null) text += $"(有降版提醒{below})";
        if (Unreadable.Count > 0) text += $"(部分內容讀不懂{below})";
        return text;
    }

    /// <summary>
    /// 要提醒操作人員的事(每則一行,不含開頭的「警告:」):船上的降版提醒(哪一艘船 + 與船上 status 相同的說明:目前執行的發行版、
    /// 先前的版本、怎麼處理 + 教官端保存的提醒何時消失),以及讀不懂的內容。沒有時是空的。
    /// <paramref name="source"/>:接在句尾的來源說明(隨身碟的 status.json 沒有簽章、<c>store list</c> 的保存時間);直連同步不必給。
    /// </summary>
    public IReadOnlyList<string> Warnings(string ship, string? source = null)
    {
        var list = new List<string>();
        var tail = string.IsNullOrEmpty(source) ? "" : $"({source})";
        var subject = ship.EndsWith(')') ? ship : ship + " ";   // 「FSB1(裝置 FSB1-LT01)有…」/「FSB1 有…」
        if (Downgrade is { } downgrade)
            list.Add($"船上筆電 {subject}有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— {downgrade.Message}。"
                     + $"船上處理之後再同步一次(或再匯入一次隨身碟),教官筆電保存的這則提醒才會消失{tail}");
        if (Unreadable.Count > 0)
        {
            var version = Unreadable.Contains(VersionField);
            var what = version ? (Unreadable.Contains(DowngradeField) ? "發行版本與降版資訊" : "發行版本") : "降版資訊";
            // 降版資訊本身讀得懂(上面已列出提醒)時,不說「可能有降版提醒沒有顯示」
            var missed = Downgrade is null ? ",可能有降版提醒沒有顯示" : "";
            // 到船上確認的指令要真的看得到那一項:船上的 status(文字輸出)有降版時會印「警告:降版:…」,但不印發行版本;
            // 發行版本在 verify-install 的第一行(「發行清單:版本 …」)
            var check = new List<string>();
            if (Downgrade is null) check.Add("simosa-pkg status(有降版時會印「警告:降版:…」)");
            if (version) check.Add("simosa-pkg verify-install(第一行「發行清單:版本 …」是發行版本)");
            list.Add($"船上筆電 {subject}回報的{what},教官筆電讀不懂(格式不符){missed};請在船上執行 {string.Join("與 ", check)}確認{tail}");
        }
        return list;
    }
}
