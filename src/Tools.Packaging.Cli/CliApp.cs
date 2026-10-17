using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Impact;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Transfer;
using SimosaBRM.Packaging.Updates;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.Packaging.Cli;

/// <summary>
/// simosa-pkg 指令實作(設計文件 docs/design/packaging-sync.md 第 6 節「操作步驟」)。回傳碼:0 成功、1 失敗/拒絕、2 用法錯誤。
/// </summary>
public sealed class CliApp
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public CliApp(TextWriter? stdout = null, TextWriter? stderr = null)
    {
        _out = stdout ?? Console.Out;
        _err = stderr ?? Console.Error;
    }

    public const string Usage = """
        simosa-pkg — Simosa BRM 簽章更新包、紀錄包與兩筆電直連同步(規劃書第 5.5、9.1 節)

        金鑰與信任清單
          keygen --out <基底路徑> --name <名稱> --role primary|backup [--holder <保管人>] [--password <密碼>]
          key-info (--key <私鑰.pem> | --cert <憑證指紋>)
          trust list|add|remove [--root <安裝目錄>] [--file <公鑰.pem>] [--key-id <金鑰ID>] [--admin <管理者>]
        教官筆電:影響分析、驗證紀錄、產生更新包
          impact --parameters <新參數集.yaml> [--baseline <舊參數集.yaml>] [--data-root <目錄>] [--json]
          verification add --file <驗證紀錄.json> --scenario <情境.yaml> --parameters <參數集.yaml> --by <驗證者>
                           [--method run|replay] [--record <紀錄.jsonl>] [--result pass|acceptable|fail] [--notes <備註>]
          pack --kind software|scenarios|parameters|charts|trust|mixed --version <版本> --summary <摘要> --out <檔.sbupd>
               (--key <私鑰.pem> | --cert <憑證指紋>) [--file <安裝路徑>=<來源檔>]... [--dir <安裝前綴>=<來源目錄>]...
               [--sms <編號>:<版次>:<核准人>[:<核准日>]]... [--change <說明>]... [--ship <船舶ID>]...
               [--data-root <目錄>] [--baseline <舊參數集>] [--verification <驗證紀錄.json>] [--force --force-reason <原因>]
               [--remove-key <金鑰ID>]... [--reason <信任變更原因>]
          inspect <檔.sbupd|.sbrec|.sbbak>
        船上筆電:驗證、套用、回復、狀態、紀錄
          verify <檔.sbupd> [--root <安裝目錄>]
          apply <檔.sbupd> [--root <安裝目錄>] [--operator <操作者>] [--allow-unverified]
          rollback [--root <安裝目錄>] [--operator <操作者>]
          status [--root <安裝目錄>] [--json]
          device init --ship <船舶ID> [--device-id <ID>] [--root] [--overwrite] [--pem]
          device show [--root]
          records pack|list [--root]
          records accept-report <檔.sbbak> [--root]
        教官筆電紀錄庫
          store import <檔.sbrec> [--store <目錄>] [--enroll-device]
          store register-device --ship <船舶ID> --device <裝置ID> --public <公鑰.pem> [--store]
          store mark-backed-up [--all | --package <ID>...] [--target <備份目的地說明>] [--store]
          store backup --to <加密外接碟目錄> [--store]
          store report --ship <船舶ID> --out <檔.sbbak> (--key | --cert) [--store]
          store list [--store]
        直連同步與隨身碟備援
          serve [--root] [--listen <直連 IP>] [--port 7300]                            (船上筆電)
          sync --ship <IP|船舶ID> (--key | --cert) [--push <檔.sbupd>]... [--store] [--port 7300]
               [--enroll-device] [--no-pull] [--no-report]                           (教官筆電)
          export --to <隨身碟目錄> [--role ship|instructor] [--push <檔.sbupd>]... [--key|--cert] [--ship <ID>]...
          import --from <隨身碟目錄> [--role ship|instructor] [--enroll-device]

        共通:--root 安裝根目錄(預設 SIMOSA_INSTALL_ROOT,Windows 為 D:\Simosa BRM);--store 預設 <root>/store;
              私鑰密碼可用 --key-password 或環境變數 SIMOSA_KEY_PASSWORD。
        """;

    public int Run(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
        {
            _out.WriteLine(Usage);
            return argv.Length == 0 ? 2 : 0;
        }
        var cmd = argv[0];
        var a = new CliArgs(argv.Skip(1));
        try
        {
            return cmd switch
            {
                "keygen" => Keygen(a),
                "key-info" => KeyInfo(a),
                "trust" => Trust(a),
                "impact" => ImpactCmd(a),
                "verification" => Verification(a),
                "pack" => Pack(a),
                "inspect" => Inspect(a),
                "verify" => Verify(a),
                "apply" => Apply(a),
                "rollback" => Rollback(a),
                "status" => Status(a),
                "device" => Device(a),
                "records" => RecordsCmd(a),
                "store" => Store(a),
                "serve" => Serve(a),
                "sync" => Sync(a),
                "export" => Export(a),
                "import" => Import(a),
                _ => throw new CliUsageException($"未知的指令:{cmd}"),
            };
        }
        catch (CliUsageException ex)
        {
            _err.WriteLine($"用法錯誤:{ex.Message}(simosa-pkg --help)");
            return 2;
        }
        catch (PackageBuildException ex)
        {
            if (ex.Impact is not null) _out.Write(ImpactAnalyzer.Format(ex.Impact));
            foreach (var p in ex.Problems) _err.WriteLine($"  - {p}");
            _err.WriteLine($"拒絕產生更新包:{ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or InvalidOperationException
                                       or UnauthorizedAccessException or PlatformNotSupportedException or SyncException
                                       or System.Net.Sockets.SocketException or ArgumentException
                                       or YamlDotNet.Core.YamlException or System.Text.Json.JsonException)
        {
            // 參數集/情境 YAML 語法錯誤(YamlException,含行列)與型別錯誤(JsonException)也依約定回 1,不以未捕捉例外中止
            _err.WriteLine($"錯誤:{ex.Message}");
            return 1;
        }
    }

    // ── 共用 ──────────────────────────────────────────────

    private static InstallLayout Layout(CliArgs a) => new(a.Get("root") ?? InstallLayout.DefaultRoot());

    private static RecordStore StoreOf(CliArgs a)
    {
        if (a.Get("store") is { } s) return new RecordStore(s);
        var layout = Layout(a);
        var rel = layout.Config.Store ?? "store";
        return new RecordStore(Path.IsPathRooted(rel) ? rel : Path.Combine(layout.Root, rel));
    }

    private IKeyProvider SigningKey(CliArgs a)
    {
        var password = a.Get("key-password") ?? System.Environment.GetEnvironmentVariable("SIMOSA_KEY_PASSWORD");
        if (a.Get("cert") is { } tp)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("--cert(Windows 憑證存放區/硬體金鑰)僅支援 Windows;開發/測試請用 --key <PEM>");
            var loc = a.Get("cert-store") == "machine" ? System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine : System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser;
            var k = new CertificateStoreKeyProvider(tp, loc);
            _out.WriteLine($"簽章金鑰:{k.DisplayName}(憑證 {k.Thumbprint},{(k.IsHardwareBacked ? "硬體保護" : "注意:私鑰可匯出")},金鑰 {KeyFiles.Short(k.KeyId)})");
            return k;
        }
        if (a.Get("key") is { } path)
        {
            var k = PemKeyProvider.FromFile(path, null, password);
            _err.WriteLine($"警告:使用 PEM 檔金鑰 {Path.GetFileName(path)}(僅限開發/測試;正式更新包須以訓練負責人的硬體金鑰 --cert 簽章,規劃書第 5.5 節)");
            _out.WriteLine($"簽章金鑰:{k.DisplayName}(金鑰 {KeyFiles.Short(k.KeyId)})");
            return k;
        }
        throw new CliUsageException("需指定簽章金鑰 --cert <憑證指紋>(硬體金鑰)或 --key <PEM>(開發/測試)");
    }

    private static (string Install, string Source) SplitMapping(string spec, string opt)
    {
        var idx = spec.IndexOf('=');
        if (idx <= 0 || idx == spec.Length - 1) throw new CliUsageException($"--{opt} 格式為 <安裝路徑>=<來源>:{spec}");
        return (spec[..idx], spec[(idx + 1)..]);
    }

    private void PrintApply(UpdateApplyResult r)
    {
        if (r.Signer is not null) _out.WriteLine($"簽章者:{r.Signer}");
        if (r.Version is not null) _out.WriteLine($"更新包:{r.PackageId} 版本 {r.Version}({r.Kind}){(r.Summary is { } s ? " — " + s : "")}");
        foreach (var w in r.Warnings) _err.WriteLine($"警告:{w}");
        foreach (var e in r.Errors) _err.WriteLine($"  - {e}");
        (r.Ok ? _out : _err).WriteLine(r.Message);
    }

    // ── 金鑰 ──────────────────────────────────────────────

    private int Keygen(CliArgs a)
    {
        var basePath = a.Require("out");
        var role = a.Get("role") ?? KeyRoles.Primary;
        if (!KeyRoles.IsSigner(role)) throw new CliUsageException("--role 須為 primary 或 backup");
        var meta = new Dictionary<string, string?> { ["role"] = role, ["name"] = a.Require("name"), ["holder"] = a.Get("holder"), ["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd") };
        var (priv, pub, keyId) = KeyFiles.Generate(meta, a.Get("password"));
        var privPath = basePath + ".key.pem";
        var pubPath = basePath + ".pub.pem";
        if (File.Exists(privPath) && !a.Flag("overwrite")) throw new IOException($"{privPath} 已存在(--overwrite 覆寫)");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privPath))!);
        File.WriteAllText(privPath, priv);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(pubPath, pub);
        _err.WriteLine("警告:PEM 檔私鑰僅供開發/測試;正式金鑰應於硬體金鑰內產生(私鑰不可匯出),只匯出公鑰放入信任清單。");
        _out.WriteLine($"已產生 {role} 金鑰 {KeyFiles.Short(keyId)}:私鑰 {privPath},公鑰 {pubPath}");
        _out.WriteLine($"金鑰 ID:{keyId}");
        return 0;
    }

    private int KeyInfo(CliArgs a)
    {
        using var k = SigningKey(a);
        _out.WriteLine($"金鑰 ID:{k.KeyId}");
        _out.WriteLine(k.PublicKeyPem);
        return 0;
    }

    private int Trust(CliArgs a)
    {
        var sub = a.Pos(0, "子指令(list|add|remove)");
        var layout = Layout(a);
        switch (sub)
        {
            case "list":
            {
                using var t = TrustStore.LoadDirectory(layout.TrustedKeysDir);
                if (t.Keys.Count == 0) _out.WriteLine($"信任清單為空:{layout.TrustedKeysDir}");
                foreach (var k in t.Keys) _out.WriteLine($"{k.Describe()}  {Path.GetFileName(k.FilePath)}  {k.KeyId}");
                return 0;
            }
            case "add":
            {
                // 安裝時(系統映像)或訪船船長以管理者帳號手動更換;記入稽核(規劃書第 5.5 節)
                var file = a.Require("file");
                using var k = TrustedKey.Parse(File.ReadAllText(file), file, KeyRoles.Primary);
                if (!KeyRoles.IsSigner(k.Role)) throw new CliUsageException("信任清單只接受 role 為 primary 或 backup 的公鑰");
                Directory.CreateDirectory(layout.TrustedKeysDir);
                var dest = Path.Combine(layout.TrustedKeysDir, Path.GetFileName(file));
                File.WriteAllText(dest, KeyFiles.WithMeta(k.PublicKeyPem, new Dictionary<string, string?> { ["role"] = k.Role, ["name"] = k.Name, ["holder"] = k.Holder, ["keyId"] = k.KeyId }, "Simosa BRM 信任公鑰"));
                layout.Audit().Write(AuditEvents.TrustManualChange, new { action = "add", k.KeyId, k.Name, k.Role, admin = a.Get("admin") ?? System.Environment.UserName });
                _out.WriteLine($"已加入信任清單:{k.Describe()}");
                return 0;
            }
            case "remove":
            {
                var id = a.Require("key-id");
                using var t = TrustStore.LoadDirectory(layout.TrustedKeysDir);
                var k = t.Keys.FirstOrDefault(x => x.KeyId.StartsWith(id, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException($"信任清單沒有金鑰 {id}");
                if (t.Keys.Count(x => x.Role == KeyRoles.Backup && x.KeyId != k.KeyId) == 0 && !a.Flag("force"))
                    throw new InvalidOperationException("移除後將沒有備援金鑰(--force 強制)");
                File.Delete(k.FilePath!);
                layout.Audit().Write(AuditEvents.TrustManualChange, new { action = "remove", k.KeyId, k.Name, k.Role, admin = a.Get("admin") ?? System.Environment.UserName });
                _out.WriteLine($"已自信任清單移除:{k.Describe()}");
                return 0;
            }
            default: throw new CliUsageException($"未知的 trust 子指令:{sub}");
        }
    }

    // ── 影響分析、驗證紀錄 ─────────────────────────────────

    private int ImpactCmd(CliArgs a)
    {
        var root = a.Get("data-root") ?? SimCore.Ship.RepositoryPaths.TryFindRoot() ?? Directory.GetCurrentDirectory();
        var newSet = ParameterSetLoader.Load(a.Require("parameters"));
        var deployed = Path.Combine(root, UpdatePackageBuilder.DefaultParameterSetInstallPath.Replace('/', Path.DirectorySeparatorChar));
        ParameterSet? baseline = null;
        if (a.Get("baseline") is { } b) baseline = ParameterSetLoader.Load(b);
        else if (File.Exists(deployed) && Path.GetFullPath(deployed) != Path.GetFullPath(a.Require("parameters"))) baseline = ParameterSetLoader.Load(deployed);
        else _err.WriteLine("注意:未提供 --baseline,全部參數鍵視為變更");
        var scenarioDir = Path.Combine(root, "data", "scenarios");
        var files = Directory.Exists(scenarioDir) ? Directory.EnumerateFiles(scenarioDir, "*.yaml").ToList() : new List<string>();
        var rep = ImpactAnalyzer.Analyze(new ImpactRequest { NewSet = newSet, Baseline = baseline, ScenarioFiles = files, DataRoot = root, DisplayRoot = root });
        if (a.Flag("json")) _out.WriteLine(PackageJson.ToText(rep));
        else _out.Write(ImpactAnalyzer.Format(rep));
        return rep.HasErrors ? 1 : 0;
    }

    private int Verification(CliArgs a)
    {
        var sub = a.Pos(0, "子指令(add|list)");
        var file = a.Require("file");
        var rec = VerificationRecord.LoadOrNew(file);
        if (sub == "list")
        {
            foreach (var e in rec.Entries) _out.WriteLine($"{e.VerifiedUtc:O} {e.ScenarioId} {e.Method} {e.Result} 參數集 {e.ParameterSetVersion} 驗證者 {e.VerifiedBy} {e.RecordRef}");
            return 0;
        }
        if (sub != "add") throw new CliUsageException($"未知的 verification 子指令:{sub}");
        var scenarioPath = a.Require("scenario");
        var set = ParameterSetLoader.Load(a.Require("parameters"));
        var sc = ScenarioLoader.Load(scenarioPath, set);
        var method = a.Get("method") ?? "run";
        if (!VerificationRecord.Methods.Contains(method)) throw new CliUsageException("--method 須為 run 或 replay");
        var result = a.Get("result") ?? "pass";
        if (result is not ("pass" or "acceptable" or "fail")) throw new CliUsageException("--result 須為 pass、acceptable 或 fail");
        var entry = new VerificationEntry
        {
            ScenarioId = sc.Id, ScenarioSha256 = Sha256Hex.OfFile(scenarioPath), ParameterSetId = set.Id, ParameterSetVersion = set.Version,
            ParameterSetSha256 = set.Sha256.ToLowerInvariant(), Method = method, Result = result, VerifiedBy = a.Require("by"), VerifiedUtc = DateTime.UtcNow, Notes = a.Get("notes"),
        };
        if (a.Get("record") is { } recPath)
        {
            var rf = RecordReader.Read(recPath);
            if (!rf.IntegrityOk) throw new InvalidDataException($"紀錄 {recPath} 完整性檢查失敗(footer 雜湊不符或缺少 footer)");
            if (rf.Header.Scenario.Id != sc.Id) throw new InvalidDataException($"紀錄的情境為 {rf.Header.Scenario.Id},不是 {sc.Id}");
            if (method == "run" && rf.Header.ParameterSet is { } ps && !Sha256Hex.Equal(ps.Sha256, set.Sha256))
                throw new InvalidDataException($"紀錄執行時的參數集(版次 {ps.Version})不是本參數集 {set.Version};以既有紀錄重播評分請用 --method replay");
            entry.RecordRef = SimCore.Ship.RepositoryPaths.ToProjectRelative(recPath);
            entry.RecordSha256 = Sha256Hex.OfFile(recPath);
        }
        rec.Entries.Add(entry);
        rec.Save(file);
        _out.WriteLine($"已記錄驗證:{sc.Id}({method},{result},參數集 {set.Version})→ {file}");
        return 0;
    }

    // ── 更新包 ────────────────────────────────────────────

    private int Pack(CliArgs a)
    {
        var sms = a.All("sms").Select(s =>
        {
            var parts = s.Split(':');
            if (parts.Length < 3) throw new CliUsageException($"--sms 格式為 <編號>:<版次>:<核准人>[:<核准日>]:{s}");
            return new SmsDocumentRef { DocumentId = parts[0], Revision = parts[1], ApprovedBy = parts[2], ApprovedOn = parts.Length > 3 ? string.Join(':', parts[3..]) : null };
        }).ToList();
        var req = new UpdateBuildRequest
        {
            PackageId = a.Get("package-id"), Version = a.Require("version"), Kind = a.Require("kind"), Summary = a.Require("summary"),
            Changes = a.All("change"), SmsDocuments = sms, Ships = a.All("ship"),
            RemoveKeyIds = a.All("remove-key"), TrustReason = a.Get("reason"),
            ValidationRoot = a.Get("data-root"), BaselineParameters = a.Get("baseline"), VerificationRecordPath = a.Get("verification"),
            Force = a.Flag("force"), ForceReason = a.Get("force-reason"),
            Audit = a.Get("store") is not null || a.Get("root") is not null ? StoreOf(a).Audit : null,
        };
        foreach (var f in a.All("file"))
        {
            var (inst, src) = SplitMapping(f, "file");
            req.Files.Add(new PackageSource(inst, src));
        }
        foreach (var d in a.All("dir"))
        {
            var (inst, src) = SplitMapping(d, "dir");
            if (!Directory.Exists(src)) throw new IOException($"找不到目錄:{src}");
            req.AddDirectory(inst, src);
        }
        using var key = SigningKey(a);
        var res = UpdatePackageBuilder.Build(req, key, a.Require("out"));
        if (res.Impact is not null) _out.Write(ImpactAnalyzer.Format(res.Impact));
        foreach (var w in res.Warnings) _err.WriteLine($"警告:{w}");
        _out.WriteLine($"已產生更新包 {res.OutputPath}");
        _out.WriteLine($"  {res.Manifest.PackageId} 版本 {res.Manifest.Version}({res.Manifest.Kind}),{res.Manifest.Files.Count} 個檔案,驗證狀態 {res.Manifest.Validation.Status}");
        _out.WriteLine($"  sha256 {res.PackageSha256}");
        return 0;
    }

    private int Inspect(CliArgs a)
    {
        var path = a.Pos(0, "檔案");
        if (path.EndsWith(".sbbak", StringComparison.OrdinalIgnoreCase))
        {
            var doc = SignedDocument.Parse(File.ReadAllBytes(path), "回報");
            _out.WriteLine($"簽章:{doc.Signature.Signer}(金鑰 {KeyFiles.Short(doc.Signature.KeyId)},{doc.Signature.SignedUtc:O})");
            _out.WriteLine(doc.Payload);
            return 0;
        }
        using var zip = ZipFile.OpenRead(path);
        var (mb, sig) = UpdatePackageReader.ReadSigned(zip);
        _out.WriteLine($"簽章:自稱「{sig.Signer}」(金鑰 {KeyFiles.Short(sig.KeyId)},{sig.SignedUtc:O};未比對信任清單,套用前請用 verify)");
        _out.WriteLine(System.Text.Encoding.UTF8.GetString(mb));
        return 0;
    }

    private int Verify(CliArgs a)
    {
        var layout = Layout(a);
        var v = new UpdateApplier(layout).Verify(a.Pos(0, "更新包"), a.Flag("allow-unverified"));
        if (v.SignerDescription is not null) _out.WriteLine($"簽章者:{v.SignerDescription}");
        if (v.Manifest is { } m)
        {
            _out.WriteLine($"更新包:{m.PackageId} 版本 {m.Version}({m.Kind})— {m.Summary}");
            foreach (var d in m.SmsDocuments) _out.WriteLine($"  依據 {d.DocumentId} 第 {d.Revision} 版(核准人 {d.ApprovedBy}{(d.ApprovedOn is { } on ? $",{on}" : "")})");
            if (m.ParameterSet is { } ps) _out.WriteLine($"  參數集 {ps.Id} 版次 {ps.Version}");
            foreach (var s in m.AffectedScenarios) _out.WriteLine($"  受影響情境 {s.ScenarioId}:{(s.Verification is { } ve ? $"已驗證({ve.Method},{ve.VerifiedBy},{ve.VerifiedUtc:yyyy-MM-dd})" : "未驗證")}");
        }
        foreach (var w in v.Warnings) _err.WriteLine($"警告:{w}");
        foreach (var e in v.Errors) _err.WriteLine($"  - {e}");
        (v.Ok ? _out : _err).WriteLine(v.Ok ? "驗證通過" : "驗證失敗");
        return v.Ok ? 0 : 1;
    }

    private int Apply(CliArgs a)
    {
        var layout = Layout(a);
        if (a.Flag("allow-unverified")) _err.WriteLine("警告:--allow-unverified 允許套用未通過情境驗證的更新包,將記入稽核紀錄");
        var r = new UpdateApplier(layout).Apply(a.Pos(0, "更新包"), new UpdateApplyOptions { AllowUnverified = a.Flag("allow-unverified"), Operator = a.Get("operator") ?? System.Environment.UserName });
        PrintApply(r);
        return r.Ok ? 0 : 1;
    }

    private int Rollback(CliArgs a)
    {
        var r = new UpdateApplier(Layout(a)).Rollback(a.Get("operator") ?? System.Environment.UserName);
        PrintApply(r);
        return r.Ok ? 0 : 1;
    }

    private int Status(CliArgs a)
    {
        var st = new ShipNode(Layout(a)).Status();
        if (a.Flag("json"))
        {
            _out.WriteLine(PackageJson.ToText(st));
            return 0;
        }
        _out.WriteLine($"船舶 {st.ShipId ?? "(未設定)"} 裝置 {st.DeviceId ?? "(未產生)"}");
        _out.WriteLine(st.Version is null ? "現行更新包:(初始安裝,尚未套用更新包)"
            : $"現行更新包 {st.Version} {st.PackageId}  套用於 {st.AppliedUtc:yyyy-MM-dd HH:mm}Z  簽章者 {st.Signer?.Name}({TrustedKey.RoleLabel(st.Signer?.Role ?? "")})");
        foreach (var (k, c) in st.Components) _out.WriteLine($"  {k,-11} {c.Version}({c.AppliedUtc:yyyy-MM-dd}){c.Summary}");
        if (st.ParameterSet is { } ps) _out.WriteLine($"  參數集 {ps.Id} 版次 {ps.Version}");
        foreach (var d in st.SmsDocuments) _out.WriteLine($"  SMS {d.DocumentId} 第 {d.Revision} 版(核准人 {d.ApprovedBy})");
        _out.WriteLine($"信任金鑰:{string.Join("、", st.TrustedKeys.Select(k => $"{k.Name}({TrustedKey.RoleLabel(k.Role)})"))}");
        _out.WriteLine($"待回傳紀錄包 {st.OutstandingRecordPackages} 個;訪船 #{st.VisitSeq};{(st.RollbackAvailable ? "可回復上一版" : "無上一版")}");
        foreach (var w in st.Warnings) _err.WriteLine($"警告:{w}");
        return 0;
    }

    // ── 裝置、紀錄 ─────────────────────────────────────────

    private int Device(CliArgs a)
    {
        var sub = a.Pos(0, "子指令(init|show)");
        var layout = Layout(a);
        if (sub == "init")
        {
            var info = DeviceKeyStore.Initialize(layout, a.Require("ship"), a.Get("device-id"), a.Flag("overwrite"), a.Flag("pem"));
            if (info.Protection != "dpapi") _err.WriteLine("警告:裝置私鑰以 PEM 檔保存(僅限開發/測試;正式船上筆電於 Windows 以 DPAPI 保護)");
            if (layout.Config.ShipId is null)
            {
                layout.Config.ShipId = info.ShipId;
                layout.Config.DeviceId = info.DeviceId;
                layout.Config.Role = "ship";
                layout.Config.Save(layout.ConfigPath);
            }
            _out.WriteLine($"已產生裝置金鑰 {info.ShipId}/{info.DeviceId}(金鑰 {KeyFiles.Short(info.KeyId)},{info.Protection})");
            _out.WriteLine($"請將 {Path.Combine(layout.DeviceDir, DeviceKeyStore.PublicFile)} 登錄到教官筆電:simosa-pkg store register-device");
            return 0;
        }
        if (sub == "show")
        {
            var info = DeviceKeyStore.LoadInfo(layout);
            _out.WriteLine($"{info.ShipId}/{info.DeviceId} 金鑰 {info.KeyId}({info.Protection})");
            _out.Write(DeviceKeyStore.PublicPem(layout));
            return 0;
        }
        throw new CliUsageException($"未知的 device 子指令:{sub}");
    }

    private int RecordsCmd(CliArgs a)
    {
        var sub = a.Pos(0, "子指令(pack|list|accept-report)");
        var mgr = new ShipRecordManager(Layout(a));
        switch (sub)
        {
            case "pack":
            {
                var p = mgr.PackPending();
                _out.WriteLine(p is null ? "沒有新的紀錄需要打包" : $"已打包 {p.PackageId}:{p.Files.Count} 個檔案、{p.Exercises} 個練習、{p.TrainingS / 3600:F2} 小時");
                return 0;
            }
            case "list":
                foreach (var p in mgr.Outstanding())
                    _out.WriteLine($"{p.PackageId}  {p.CreatedUtc:yyyy-MM-dd HH:mm}Z  {p.Files} 檔 {p.Exercises} 練習  訪船 #{p.VisitSeq}  {(p.PulledUtc is null ? "未取回" : "已取回,待備份回報")}");
                foreach (var w in mgr.RetentionWarnings()) _err.WriteLine($"警告:{w}");
                return 0;
            case "accept-report":
            {
                var r = mgr.AcceptBackupReport(File.ReadAllBytes(a.Pos(1, "回報檔")));
                foreach (var e in r.Errors) _err.WriteLine($"  - {e}");
                if (r.Ok) _out.WriteLine($"回報 {r.ReportId}(簽章者 {r.Signer}):刪除 {r.Deleted.Count} 個紀錄包、{r.DeletedFiles.Count} 個檔案;保留 {r.Retained.Count} 個");
                return r.Ok ? 0 : 1;
            }
            default: throw new CliUsageException($"未知的 records 子指令:{sub}");
        }
    }

    private int Store(CliArgs a)
    {
        var sub = a.Pos(0, "子指令");
        var store = StoreOf(a);
        switch (sub)
        {
            case "import":
            {
                var r = store.Import(a.Pos(1, "紀錄包"), a.Flag("enroll-device"));
                foreach (var e in r.Errors) _err.WriteLine($"  - {e}");
                if (r.DeviceEnrolled) _err.WriteLine($"注意:已首次登錄裝置 {r.ShipId}/{r.DeviceId},請與船上 device show 的金鑰 ID 核對");
                if (r.Ok) _out.WriteLine(r.AlreadyImported ? $"{r.PackageId} 已在紀錄庫" : $"已匯入 {r.PackageId} → {r.Directory}({r.Manifest?.Exercises.Count} 個練習,{r.Manifest?.TrainingHours.TotalHours} 小時)");
                return r.Ok ? 0 : 1;
            }
            case "register-device":
            {
                using var k = store.RegisterDevice(a.Require("ship"), a.Require("device"), File.ReadAllText(a.Require("public")));
                store.Audit.Write(AuditEvents.StoreDeviceEnrolled, new { ship = a.Require("ship"), device = a.Require("device"), k.KeyId, via = "register" });
                _out.WriteLine($"已登錄裝置 {a.Require("ship")}/{a.Require("device")}(金鑰 {KeyFiles.Short(k.KeyId)})");
                return 0;
            }
            case "mark-backed-up":
            {
                if (!a.Flag("all") && a.All("package").Count == 0) throw new CliUsageException("需指定 --all 或 --package <ID>");
                var marked = store.MarkBackedUp(a.Flag("all") ? null : a.All("package"), a.Get("target"));
                _out.WriteLine($"已標記 {marked.Count} 個紀錄包完成岸上備份;下次同步時送出已備份回報");
                return 0;
            }
            case "backup":
            {
                var (target, marked) = store.Backup(a.Require("to"));
                _out.WriteLine($"已備份紀錄庫到 {target},標記 {marked.Count} 個紀錄包完成岸上備份");
                return 0;
            }
            case "report":
            {
                using var key = SigningKey(a);
                var c = store.CreateBackupReport(a.Require("ship"), key);
                if (c is null)
                {
                    _out.WriteLine("沒有已備份且待船上清理的紀錄包");
                    return 0;
                }
                var outPath = a.Require("out");
                File.WriteAllBytes(outPath, c.Value.Document.ToBytes());
                _out.WriteLine($"已產生已備份回報 {outPath}({c.Value.Report.Packages.Count} 個紀錄包)");
                return 0;
            }
            case "list":
                foreach (var p in store.LoadLedger().Packages)
                    _out.WriteLine($"{p.ShipId} {p.PackageId} {p.Directory} 匯入 {p.ImportedUtc:yyyy-MM-dd} 備份 {(p.BackedUpUtc is { } b ? b.ToString("yyyy-MM-dd") : "未")} 船上 {(p.ShipDeletedUtc is null ? "保留" : "已刪除")}");
                foreach (var (ship, v) in store.LoadFleet().Ships)
                    _out.WriteLine($"版次 {ship}:{v.Version}(參數集 {v.ParameterSet?.Version},更新於 {v.UpdatedUtc:yyyy-MM-dd},經 {v.Via})");
                return 0;
            default: throw new CliUsageException($"未知的 store 子指令:{sub}");
        }
    }

    // ── 同步 ──────────────────────────────────────────────

    private int Serve(CliArgs a)
    {
        var layout = Layout(a);
        var listen = a.Get("listen") ?? layout.Config.Listen;
        if (listen is null) _err.WriteLine("警告:未指定直連介面 IP(--listen 或 packaging.json listen),將綁定所有介面");
        var ip = listen is null ? IPAddress.Any : IPAddress.Parse(listen);
        var port = a.Int("port", layout.Config.Port);
        var ship = new ShipNode(layout);
        _ = DeviceKeyStore.LoadInfo(layout);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var server = new SyncServer(ship, ip, port, _out);
        server.Start();
        _out.WriteLine($"船端同步服務 {ship.ShipId} 監聽 {ip}:{server.Port}(Ctrl+C 結束)");
        try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); }
        catch (AggregateException) { }
        server.DisposeAsync().AsTask().Wait();
        return 0;
    }

    private int Sync(CliArgs a)
    {
        var target = a.Require("ship");
        var layout = Layout(a);
        var host = layout.Config.Ships.TryGetValue(target, out var ip) ? ip : target;
        using var key = SigningKey(a);
        var store = StoreOf(a);
        var report = SyncClient.SyncAsync(new SyncOptions
        {
            Host = host, Port = a.Int("port", layout.Config.Port), InstructorKey = key, Store = store, PushPackages = a.All("push"),
            EnrollDevice = a.Flag("enroll-device"), PullRecords = !a.Flag("no-pull"), SendBackupReport = !a.Flag("no-report"), Log = _out,
        }).GetAwaiter().GetResult();
        if (report.Status is { } st) _out.WriteLine($"{report.ShipId} 現行版本 {st.Version ?? "(初始)"},參數集 {st.ParameterSet?.Version ?? "—"},待回傳紀錄包 {st.OutstandingRecordPackages}");
        foreach (var w in report.Warnings) _err.WriteLine($"警告:{w}");
        _out.WriteLine(report.Ok ? "同步完成" : "同步完成,但有項目失敗(見上方訊息)");
        return report.Ok ? 0 : 1;
    }

    private string Role(CliArgs a) => a.Get("role") ?? Layout(a).Config.Role;

    private int Export(CliArgs a)
    {
        var to = a.Require("to");
        UsbReport r;
        if (Role(a) == "ship") r = UsbTransfer.ShipExport(new ShipNode(Layout(a)), to);
        else
        {
            IKeyProvider? key = a.Get("key") is not null || a.Get("cert") is not null ? SigningKey(a) : null;
            using (key)
                r = UsbTransfer.InstructorExport(StoreOf(a), to, a.All("push"), key, a.All("ship") is { Count: > 0 } s ? s : null);
            if (key is null) _err.WriteLine("注意:未指定 --key/--cert,未產生已備份回報");
        }
        foreach (var w in r.Written) _out.WriteLine($"已寫入 {w}");
        foreach (var w in r.Warnings) _err.WriteLine($"警告:{w}");
        return 0;
    }

    private int Import(CliArgs a)
    {
        var from = a.Require("from");
        UsbReport r;
        if (Role(a) == "ship")
        {
            r = UsbTransfer.ShipImport(new ShipNode(Layout(a)), from);
            foreach (var u in r.Updates) PrintApply(u);
            if (r.Cleanup is { Ok: true } c) _out.WriteLine($"已備份回報 {c.ReportId}:刪除 {c.Deleted.Count} 個紀錄包");
        }
        else
        {
            r = UsbTransfer.InstructorImport(StoreOf(a), from, a.Flag("enroll-device"));
            foreach (var i in r.Imported.Where(i => i.Ok)) _out.WriteLine(i.AlreadyImported ? $"{i.PackageId} 已在紀錄庫" : $"已匯入 {i.PackageId} → {i.Directory}");
        }
        foreach (var w in r.Written) _out.WriteLine($"已寫入 {w}");
        foreach (var w in r.Warnings) _err.WriteLine($"警告:{w}");
        foreach (var e in r.Errors) _err.WriteLine($"  - {e}");
        var ok = r.Errors.Count == 0 && r.Updates.All(u => u.Ok);
        return ok ? 0 : 1;
    }
}
