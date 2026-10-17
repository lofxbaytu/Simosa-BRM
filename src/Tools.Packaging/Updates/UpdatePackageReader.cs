using System.IO.Compression;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;

namespace SimosaBRM.Packaging.Updates;

/// <summary>更新包驗證結果(套用前顯示簽章者;規劃書第 5.5 節)。</summary>
public sealed class UpdateVerification
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public UpdateManifest? Manifest { get; set; }
    public SignatureEnvelope? Signature { get; set; }
    /// <summary>信任清單中的簽章者(簽章驗證通過時才有)</summary>
    public string? SignerKeyId { get; set; }
    public string? SignerName { get; set; }
    public string? SignerRole { get; set; }
    public string? SignerDescription { get; set; }
    public string PackageSha256 { get; set; } = "";
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// 更新包讀取與驗證:簽章(信任清單)→ manifest 格式 → 檔案清單與 zip 內容一一對應(不得多、不得少)→ 每檔 SHA-256 與大小
/// → 版本大於曾套用最高版本(拒絕降版/重播)→ 船舶限定 → 驗證狀態(未驗證者拒絕)→ trust 類須備援金鑰簽章。
/// 簽章不符時不再解讀 manifest 內容。
/// </summary>
public static class UpdatePackageReader
{
    public static (byte[] ManifestBytes, SignatureEnvelope Signature) ReadSigned(ZipArchive zip)
    {
        var me = zip.GetEntry(UpdateManifest.ManifestEntry) ?? throw new InvalidDataException("更新包缺少 manifest.json");
        var se = zip.GetEntry(UpdateManifest.SignatureEntry) ?? throw new InvalidDataException("更新包缺少 manifest.sig");
        var mb = ReadEntry(me, 4 << 20);
        var sig = PackageJson.FromBytes<SignatureEnvelope>(ReadEntry(se, 64 << 10), "manifest.sig");
        return (mb, sig);
    }

    internal static byte[] ReadEntry(ZipArchiveEntry e, long max)
    {
        if (e.Length > max) throw new InvalidDataException($"{e.FullName} 過大");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>不驗證簽章,只讀 manifest(隨身碟匯入時排序用;套用前仍會完整驗證)。</summary>
    public static UpdateManifest PeekManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var (mb, _) = ReadSigned(zip);
        return PackageJson.FromBytes<UpdateManifest>(mb, "manifest.json");
    }

    public static UpdateVerification Verify(string path, TrustStore trust, InstalledState? installed, string? shipId, bool allowUnverified = false)
    {
        var v = new UpdateVerification();
        if (!File.Exists(path))
        {
            v.Errors.Add($"找不到更新包:{path}");
            return v;
        }
        v.PackageSha256 = Sha256Hex.OfFile(path);
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException ex)
        {
            v.Errors.Add($"不是有效的更新包(zip):{ex.Message}");
            return v;
        }
        using (zip)
        {
            byte[] mb;
            SignatureEnvelope sig;
            try { (mb, sig) = ReadSigned(zip); }
            catch (InvalidDataException ex)
            {
                v.Errors.Add(ex.Message);
                return v;
            }
            v.Signature = sig;
            if (sig.FormatError() is { } fe)
            {
                v.Errors.Add(fe);
                return v;
            }
            var key = trust.Find(sig.KeyId);
            if (key is null || !KeyRoles.IsSigner(key.Role))
            {
                v.Errors.Add($"簽章金鑰 {KeyFiles.Short(sig.KeyId)}(自稱「{sig.Signer}」)不在信任清單,拒絕套用");
                return v;
            }
            if (!sig.VerifyWith(key.Key, mb))
            {
                v.Errors.Add($"簽章驗證失敗(manifest 遭竄改或簽章損毀;金鑰 {key.Describe()})");
                return v;
            }
            v.SignerKeyId = key.KeyId;
            v.SignerName = key.Name;
            v.SignerRole = key.Role;
            v.SignerDescription = key.Describe();

            UpdateManifest m;
            try { m = PackageJson.FromBytes<UpdateManifest>(mb, "manifest.json"); }
            catch (InvalidDataException ex)
            {
                v.Errors.Add(ex.Message);
                return v;
            }
            v.Manifest = m;
            if (m.Format != UpdateManifest.FormatId) v.Errors.Add($"manifest 格式不支援:{m.Format}");
            if (!string.Equals(m.Signer.KeyId, sig.KeyId, StringComparison.OrdinalIgnoreCase)) v.Errors.Add("manifest.signer 與簽章金鑰不一致");
            if (!PackageVersion.TryParse(m.Version, out var ver)) v.Errors.Add($"版本格式錯誤:{m.Version}");
            if (!UpdateKinds.All.Contains(m.Kind)) v.Errors.Add($"未知的更新包種類:{m.Kind}");
            if (string.IsNullOrWhiteSpace(m.PackageId)) v.Errors.Add("manifest 缺少 packageId");

            // 檔案清單 ↔ zip 內容
            var listed = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
            foreach (var f in m.Files)
            {
                string p;
                try { p = PackagePaths.NormalizeInstallPath(f.Path); }
                catch (InvalidDataException ex)
                {
                    v.Errors.Add(ex.Message);
                    continue;
                }
                if (p != f.Path) v.Errors.Add($"檔案路徑未正規化:{f.Path}");
                if (!listed.TryAdd(p, f)) v.Errors.Add($"檔案重複列出:{p}");
                var prefixes = UpdateKinds.AllowedPrefixes(m.Kind);
                if (!prefixes.Any(pre => p.StartsWith(pre, StringComparison.Ordinal))) v.Errors.Add($"{m.Kind} 更新包不可包含 {p}");
            }
            foreach (var r in m.Remove)
            {
                try { PackagePaths.NormalizeInstallPath(r); }
                catch (InvalidDataException ex) { v.Errors.Add(ex.Message); }
            }
            var payloadSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (e.FullName is UpdateManifest.ManifestEntry or UpdateManifest.SignatureEntry) continue;
                if (e.FullName.EndsWith('/')) continue;
                if (!e.FullName.StartsWith(UpdateManifest.PayloadPrefix, StringComparison.Ordinal))
                {
                    v.Errors.Add($"更新包含未列於 manifest 的項目:{e.FullName}");
                    continue;
                }
                var p = e.FullName[UpdateManifest.PayloadPrefix.Length..];
                if (!listed.TryGetValue(p, out var f))
                {
                    v.Errors.Add($"更新包含未列於 manifest 的檔案:{p}");
                    continue;
                }
                if (!payloadSeen.Add(p))
                {
                    v.Errors.Add($"zip 內重複的檔案:{p}");
                    continue;
                }
                if (e.Length != f.Size) v.Errors.Add($"檔案大小不符:{p}");
                using var s = e.Open();
                if (!Sha256Hex.Equal(Sha256Hex.OfStream(s), f.Sha256)) v.Errors.Add($"檔案雜湊不符(遭竄改):{p}");
            }
            foreach (var p in listed.Keys.Where(p => !payloadSeen.Contains(p))) v.Errors.Add($"更新包缺少檔案:{p}");

            // 版本(單調遞增)與重複套用
            if (installed is not null && ver.Parts is not null)
            {
                var highest = installed.HighestVersion ?? installed.Version;
                if (highest is not null && PackageVersion.TryParse(highest, out var hv) && ver.CompareTo(hv) <= 0)
                    v.Errors.Add($"版本 {m.Version} 不大於船上曾套用的最高版本 {highest},拒絕降版或重複套用");
                if (installed.HasApplied(m.PackageId)) v.Errors.Add($"更新包 {m.PackageId} 已套用過");
            }
            if (m.Ships.Count > 0 && (shipId is null || !m.Ships.Contains(shipId, StringComparer.OrdinalIgnoreCase)))
                v.Errors.Add($"更新包限定船舶 {string.Join(", ", m.Ships)},本機為 {shipId ?? "(未設定)"}");

            // 驗證狀態(第 9.1 節:船上只套用已通過驗證並簽章的更新包)
            var touchesScenarioOrParams = m.Files.Any(f => f.Path.StartsWith("data/parameters/", StringComparison.Ordinal) || UpdatePackageBuilder.IsScenarioYaml(f.Path));
            if (touchesScenarioOrParams)
            {
                if (m.Validation.Status == ValidationStatuses.Forced)
                {
                    if (allowUnverified) v.Warnings.Add($"此更新包以 --force 產生(原因:{m.Validation.ForcedReason}),管理者已允許套用");
                    else v.Errors.Add($"此更新包未通過受影響情境驗證(以 --force 產生:{m.Validation.ForcedReason}),拒絕套用");
                }
                else if (m.Validation.Status != ValidationStatuses.Verified)
                    v.Errors.Add("含參數集或情境的更新包須標示已完成受影響情境驗證");
            }

            // 信任清單
            if (m.Kind == UpdateKinds.Trust)
            {
                if (key.Role != KeyRoles.Backup) v.Errors.Add($"信任更新包須以備援金鑰簽章(本包簽章者:{key.Describe()})");
                if (m.Trust is null) v.Errors.Add("信任更新包缺少 trust 區段");
                else
                {
                    foreach (var a in m.Trust.Add)
                        if (!listed.ContainsKey(a.Path)) v.Errors.Add($"trust.add 列出的 {a.Path} 不在檔案清單");
                    foreach (var p in listed.Keys)
                        if (!m.Trust.Add.Any(a => a.Path == p)) v.Errors.Add($"信任清單檔案 {p} 未列於 trust.add");
                }
            }
            else if (m.Files.Any(f => f.Path.StartsWith(PackagePaths.TrustedKeysPrefix, StringComparison.Ordinal)) || m.Remove.Any(r => r.StartsWith(PackagePaths.TrustedKeysPrefix, StringComparison.Ordinal)))
                v.Errors.Add("信任清單只能以 trust 更新包變更");
        }
        return v;
    }
}
