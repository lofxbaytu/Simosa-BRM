using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Impact;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Updates;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 測試環境:暫存目錄放在專案內 build/test-tmp/packaging/(CLAUDE.md:輸出不離開專案資料夾),
/// 含主/備援 PEM 金鑰、船上安裝目錄(資料自專案 data/ 複製、信任清單、裝置金鑰)與教官端資料根目錄。
/// </summary>
public sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        RepoRoot = RepositoryPaths.FindRoot();
        Dir = Path.Combine(RepoRoot, "build", "test-tmp", "packaging", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Dir);
        (PrimaryPriv, PrimaryPub, _) = KeyFiles.Generate(new Dictionary<string, string?> { ["role"] = KeyRoles.Primary, ["name"] = "訪船船長(測試)", ["holder"] = "訓練負責人" });
        (BackupPriv, BackupPub, _) = KeyFiles.Generate(new Dictionary<string, string?> { ["role"] = KeyRoles.Backup, ["name"] = "DPA 備援(測試)", ["holder"] = "DPA" });
        InstructorRoot = Path.Combine(Dir, "instructor");
        CopyData(InstructorRoot);
    }

    public string RepoRoot { get; }
    public string Dir { get; }
    public string PrimaryPriv { get; }
    public string PrimaryPub { get; }
    public string BackupPriv { get; }
    public string BackupPub { get; }
    /// <summary>教官端驗證根目錄(含 data/ 的複本,可自由修改)</summary>
    public string InstructorRoot { get; }

    public PemKeyProvider Primary() => new(PrimaryPriv);
    public PemKeyProvider Backup() => new(BackupPriv);

    public static (string Priv, string Pub) NewKey(string role, string name)
    {
        var (priv, pub, _) = KeyFiles.Generate(new Dictionary<string, string?> { ["role"] = role, ["name"] = name });
        return (priv, pub);
    }

    /// <summary>測試固定使用的情境(其他工程師新增的情境不納入,避免測試結果隨資料夾內容變動)</summary>
    public static readonly string[] FixtureScenarios = { "E01_baseline", "E05_pilot_traffic", "E07_steering_failure", "E09_blackout" };

    public void CopyData(string root)
    {
        foreach (var sub in new[] { "parameters", "ships" })
        {
            var src = Path.Combine(RepoRoot, "data", sub);
            if (Directory.Exists(src)) PackagePaths.CopyDirectory(src, Path.Combine(root, "data", sub));
        }
        var scenarios = Path.Combine(root, "data", "scenarios");
        Directory.CreateDirectory(scenarios);
        foreach (var id in FixtureScenarios)
        {
            var src = Path.Combine(RepoRoot, "data", "scenarios", id + ".yaml");
            if (File.Exists(src)) File.Copy(src, Path.Combine(scenarios, id + ".yaml"));
            var assets = Path.Combine(RepoRoot, "data", "scenarios", id);
            if (Directory.Exists(assets)) PackagePaths.CopyDirectory(assets, Path.Combine(scenarios, id));
        }
    }

    /// <summary>建立船上安裝目錄:app/(假軟體)、data/(複本)、信任清單(主 + 備援)、裝置金鑰(PEM)、packaging.json。</summary>
    public InstallLayout NewShip(string name = "ship", string shipId = "FSB1", int maxVisitIntervals = 2)
    {
        var root = Path.Combine(Dir, name);
        CopyData(root);
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "version.txt"), "app 1.0\n");
        var trust = Path.Combine(root, "config", "trusted-keys");
        Directory.CreateDirectory(trust);
        File.WriteAllText(Path.Combine(trust, "primary.pem"), PrimaryPub);
        File.WriteAllText(Path.Combine(trust, "backup.pem"), BackupPub);
        var cfg = new PackagingConfig { Role = "ship", ShipId = shipId, DeviceId = $"{shipId}-LT01", MaxVisitIntervals = maxVisitIntervals };
        cfg.Save(Path.Combine(root, "config", "packaging.json"));
        var layout = new InstallLayout(root);
        DeviceKeyStore.Initialize(layout, shipId, $"{shipId}-LT01", forcePem: true);
        return layout;
    }

    public string PathIn(params string[] parts) => Path.Combine(new[] { Dir }.Concat(parts).ToArray());

    /// <summary>簡單的 software 更新包(app/version.txt)。</summary>
    public string SoftwarePackage(string version, IKeyProvider key, string content = "app 2.0\n", string? name = null)
    {
        var src = PathIn("src-" + version + "-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(src, content);
        var req = new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Software, Summary = $"軟體 {version}",
            Files = { new PackageSource("app/version.txt", src) },
        };
        return UpdatePackageBuilder.Build(req, key, PathIn(name ?? $"sw-{version}.sbupd")).OutputPath;
    }

    /// <summary>修改教官端參數集中某鍵的值(文字取代),回傳參數集路徑。</summary>
    public string EditParameter(string key, string oldValue, string newValue, string? bumpVersion = "2026.10.2")
    {
        var path = Path.Combine(InstructorRoot, "data", "parameters", "company-procedures.yaml");
        var text = File.ReadAllText(path);
        var idx = text.IndexOf(key + ":", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"參數集中找不到 {key}");
        var lineEnd = text.IndexOf('\n', idx);
        var line = text[idx..lineEnd];
        var replaced = line.Replace($"value: {oldValue},", $"value: {newValue},");
        Assert.NotEqual(line, replaced);
        text = text[..idx] + replaced + text[lineEnd..];
        if (bumpVersion is not null) text = text.Replace("version: \"2026.10.1\"", $"version: \"{bumpVersion}\"");
        var outPath = PathIn("new-params-" + Guid.NewGuid().ToString("N")[..6] + ".yaml");
        File.WriteAllText(outPath, text);
        return outPath;
    }

    public string BaselineParameters => Path.Combine(InstructorRoot, "data", "parameters", "company-procedures.yaml");

    /// <summary>為情境加一筆通過的驗證紀錄。</summary>
    public static void Verify(VerificationRecord rec, string scenarioPath, ParameterSet set, string scenarioId)
        => rec.Entries.Add(new VerificationEntry
        {
            ScenarioId = scenarioId, ScenarioSha256 = Sha256Hex.OfFile(scenarioPath), ParameterSetId = set.Id, ParameterSetVersion = set.Version,
            ParameterSetSha256 = set.Sha256, Method = "run", Result = "pass", VerifiedBy = "測試", VerifiedUtc = DateTime.UtcNow,
        });

    /// <summary>
    /// 寫一個合成的練習紀錄(header + state + footer;footer.sha256 與 RecordReader 相同算法)。
    /// 有 footer 時把檔案修改時間設為牆鐘結束時間(預設 = 起始 + 模擬時長,即 1 倍速;<paramref name="wallS"/> 可指定實際經過秒數),
    /// 與 SimCore 實際寫檔的結果一致;<paramref name="closedUtc"/> 不為 null 時在 footer 寫入牆鐘結束時間。
    /// </summary>
    public static string WriteRecord(string dir, string fileName, string scenarioId, DateTime startUtc, double durationS, object[]? participants = null, bool footer = true,
                                     double? wallS = null, DateTime? closedUtc = null)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["kind"] = "header", ["version"] = 2, ["createdUtc"] = startUtc.ToString("O"), ["shipId"] = "FSB1", ["loading"] = "ballast", ["seed"] = 1, ["dt"] = 0.02,
                ["dynamics"] = "test", ["scenario"] = new { id = scenarioId, name = scenarioId + " 測試" }, ["participants"] = participants,
            }),
        };
        var ticks = (long)Math.Round(durationS / 0.02);
        lines.Add($"{{\"kind\":\"state\",\"state\":{{\"t\":{(durationS / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)},\"tick\":{ticks / 2}}}}}");
        lines.Add($"{{\"kind\":\"state\",\"state\":{{\"t\":{durationS.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"tick\":{ticks}}}}}");
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var l in lines)
        {
            h.AppendData(Encoding.UTF8.GetBytes(l));
            h.AppendData("\n"u8);
        }
        if (footer)
        {
            var f = new Dictionary<string, object?> { ["kind"] = "footer", ["finalTick"] = ticks, ["stateHash"] = "00", ["lines"] = lines.Count, ["inputs"] = 0, ["sha256"] = Convert.ToHexString(h.GetHashAndReset()) };
            if (closedUtc is { } c) f["closedUtc"] = c.ToString("O");
            lines.Add(JsonSerializer.Serialize(f));
        }
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        if (footer) File.SetLastWriteTimeUtc(path, startUtc.AddSeconds(wallS ?? durationS));
        return path;
    }

    /// <summary>重寫 zip:對指定項目套用轉換(竄改測試)。</summary>
    public static void RewriteZip(string path, string entryName, Func<byte[], byte[]> transform)
    {
        var tmp = path + ".rewrite";
        using (var src = ZipFile.OpenRead(path))
        using (var fs = new FileStream(tmp, FileMode.Create))
        using (var dst = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var found = false;
            foreach (var e in src.Entries)
            {
                using var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (e.FullName == entryName)
                {
                    bytes = transform(bytes);
                    found = true;
                }
                var ne = dst.CreateEntry(e.FullName);
                using var o = ne.Open();
                o.Write(bytes);
            }
            Assert.True(found, $"zip 中沒有 {entryName}");
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 修改更新包 manifest 後以 <paramref name="key"/> 重新簽章(模擬持有有效金鑰者自行組出的 manifest,例如 builder 不會產生的 remove 或 packageId)。
    /// </summary>
    public static void ResignManifest(string path, IKeyProvider key, Action<UpdateManifest> edit)
    {
        byte[] mb;
        using (var zip = ZipFile.OpenRead(path))
            mb = UpdatePackageReader.ReadSigned(zip).ManifestBytes;
        var m = PackageJson.FromBytes<UpdateManifest>(mb, "manifest.json");
        edit(m);
        m.Signer = new SignerRef { KeyId = key.KeyId, Name = key.DisplayName };
        var nb = PackageJson.ToBytes(m);
        var sig = PackageJson.ToBytes(SignatureEnvelope.Create(key, nb, DateTime.UtcNow));
        RewriteZip(path, UpdateManifest.ManifestEntry, _ => nb);
        RewriteZip(path, UpdateManifest.SignatureEntry, _ => sig);
    }

    /// <summary>在 zip 中加一個項目。</summary>
    public static void AddZipEntry(string path, string entryName, byte[] content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var e = zip.CreateEntry(entryName);
        using var s = e.Open();
        s.Write(content);
    }

    public void Dispose()
    {
        try { PackagePaths.DeleteDirectory(Dir); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>可手動推進的時鐘(訪船間隔等需要時間差的測試)。</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
