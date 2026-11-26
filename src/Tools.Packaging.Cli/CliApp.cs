using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Impact;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Transfer;
using SimosaBRM.Packaging.Updates;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Cli;

/// <summary>
/// simosa-pkg 指令實作(設計文件 docs/design/packaging-sync.md 第 6 節「操作步驟」)。回傳碼:0 成功、1 失敗/拒絕、2 用法錯誤。
/// </summary>
public sealed class CliApp
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly LocalDataEnvironment _localData;

    /// <param name="localData">本機資料守則的檢查環境(作業系統、系統磁碟、SIMOSA_ALLOW_SYSTEM_DRIVE;預設目前程序,測試可注入)</param>
    public CliApp(TextWriter? stdout = null, TextWriter? stderr = null, LocalDataEnvironment? localData = null)
    {
        _out = stdout ?? Console.Out;
        _err = stderr ?? Console.Error;
        _localData = localData ?? LocalDataEnvironment.FromProcess();
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
          verify-install [--root <安裝目錄>] [--json] [--max-items <N>]   (程式檔完整性核對;同 release verify --dir <安裝目錄> --installed)
          device init --ship <船舶ID> [--device-id <ID>] [--root] [--overwrite] [--pem]
          device show [--root]
          records pack|list [--root] [--media-max-mb <MB>]   (pack:錄影 media/*.webm 超過上限者只放旁檔並標 omitted;預設 packaging.json mediaMaxMb=2048)
          records accept-report <檔.sbbak> [--root]
        教官筆電紀錄庫
          store import <檔.sbrec> [--store <目錄>] [--enroll-device]
          store register-device --ship <船舶ID> --device <裝置ID> --public <公鑰.pem> [--store]
          store mark-backed-up [--all | --package <ID>...] [--target <備份目的地說明>] [--store]
          store backup --to <加密外接碟目錄> [--store]
          store report --ship <船舶ID> --out <檔.sbbak> (--key | --cert) [--store]
          store list [--store]
        發行版(tools/build-release.*;規劃書第 5.4–5.5 節)
          release manifest --dir <發行目錄> --version <版本> [--git <版次>] [--branch <分支>] [--runtime win-x64|linux-x64]
                           [--component <名稱>=<版本>[=<相對路徑>[=<狀態>]]]... [--note <註記>]...
                           (目錄內有安裝程式不安裝的檔案 *.pdb 時失敗並列出:清單只能列安裝後會存在的檔案)
          release verify --dir <發行目錄> [--json]                      (安裝前:目錄內容須與 manifest.json 完全相同)
          release verify --dir <安裝根目錄> --installed [--json] [--max-items <N>]
                           (安裝後/使用中:manifest.json 疊上更新包覆蓋清單 state/installed-files.json;執行期資料不算多出、
                            本機修改的 config\ 設定只列提醒;程式/資料檔缺少或雜湊不符、app\ 內多出檔案或目錄連結為錯誤;每類預設列前 10 筆)
        直連同步與隨身碟備援
          serve [--root] [--listen <直連 IP>] [--port 7300]                            (船上筆電)
          sync --ship <IP|船舶ID> (--key | --cert) [--push <檔.sbupd>]... [--store] [--port 7300]
               [--enroll-device] [--no-pull] [--no-report]                           (教官筆電)
          export --to <隨身碟目錄> [--role ship|instructor] [--push <檔.sbupd>]... [--key|--cert] [--ship <ID>]...
          import --from <隨身碟目錄> [--role ship|instructor] [--enroll-device]
          (教官筆電的 sync 與 import 結束時印出各船的發行版;船上有降版時另列警告,並存進紀錄庫的 fleet-versions.json — store list 可以再看)

        共通:--root 安裝根目錄(預設 SIMOSA_INSTALL_ROOT → 程式所在的安裝/專案根目錄 → D:\Simosa BRM 或 G:\Simosa BRM;不會自動落到 C:);--store 預設 <root>/store;
              私鑰密碼可用 --key-password 或環境變數 SIMOSA_KEY_PASSWORD。
        寫入位置(本機資料守則;docs/ops/本機資料位置.md 第 4 節):
              寫入用的相對路徑 — keygen / pack / store report 的 --out、--store、store backup 與 export 的 --to、import 的 --from、
              verification add 的 --file、release manifest 的 --dir — 以根目錄(--root;未指定時為預設安裝根目錄,該資料夾不存在時為目前目錄)為準。
              讀取本工具產生的檔案 — --key、trust add 的 --file、verification list 的 --file、pack --verification、--push、store register-device --public、
              release verify --dir、inspect / verify / apply / records accept-report / store import 的檔案 — 相對路徑先找目前目錄,沒有時再找根目錄
              (所以 keygen --out → pack --key、verification add → pack --verification、pack --out → sync --push 在任何目錄都找得到;兩處都有時用目前目錄的並提醒);
              其他輸入(--scenario、--parameters、--baseline、--data-root、--record、pack 的 --file / --dir 來源)與 --root 本身以目前目錄為準。
              寫入位置(含 config/packaging.json 的 store,以及 records accept-report、import --role ship、serve 會刪除舊紀錄的 recordsDir)
              在根目錄與專案根目錄之外、而且位於 Windows 系統磁碟(C:)時拒絕(結束碼 1);隨身碟(E: 等)與網路路徑(\\server\share)照常。
              確實要寫到系統磁碟須先取得訓練負責人同意,同意後設環境變數 SIMOSA_ALLOW_SYSTEM_DRIVE=1 再執行。
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
                "verify-install" => VerifyInstalled(a.Get("root") ?? a.Get("dir") ?? InstallLayout.DefaultRoot(), a),
                "device" => Device(a),
                "records" => RecordsCmd(a),
                "store" => Store(a),
                "serve" => Serve(a),
                "sync" => Sync(a),
                "export" => Export(a),
                "import" => Import(a),
                "release" => ReleaseCmd(a),
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
            // 參數集/情境 YAML 語法錯誤(YamlException,含行列)與型別錯誤(JsonException)也依約定回 1,不以未捕捉例外中止。
            // sync 的錯誤訊息可能帶著船端送來的文字(握手時自稱的船舶 / 裝置識別、error 訊息、解析失敗時例外訊息裡的鍵名片段):
            // 印出之前去掉控制 / 格式字元並截斷 — 合法的訊息不受影響,結束碼不變
            _err.WriteLine($"錯誤:{(cmd == "sync" ? Shown(ex.Message, 1000) : ex.Message)}");
            return 1;
        }
    }

    // ── 共用 ──────────────────────────────────────────────

    private static InstallLayout Layout(CliArgs a) => new(a.Get("root") ?? InstallLayout.DefaultRoot());

    private RecordStore StoreOf(CliArgs a)
    {
        if (a.Get("store") is { } s) return new RecordStore(WritePath(a, "--store", s));
        var layout = Layout(a);
        // packaging.json store:相對路徑以安裝根目錄為準(預設 store);絕對路徑在根目錄外的系統磁碟時拒絕
        return new RecordStore(layout.Config.Store is { Length: > 0 } cfg
            ? ConfigWritePath(a, "config/packaging.json 的 store", cfg)
            : Path.Combine(layout.Root, "store"));
    }

    // ── 本機資料守則:寫入位置(docs/ops/本機資料位置.md 第 4 節;判斷邏輯在 SimCore LocalDataGuard,各主控程式共用)──

    /// <summary>根目錄(--root 或預設安裝根目錄)的完整路徑,與是否以 --root 明確指定。</summary>
    private (string Root, bool Explicit) RootOf(CliArgs a)
    {
        var explicitRoot = a.Get("root");
        return (LocalDataGuard.FullPath(explicitRoot ?? InstallLayout.DefaultRoot(), Directory.GetCurrentDirectory(), _localData), explicitRoot is not null);
    }

    /// <summary>寫入用相對路徑的基準:--root,或已存在的預設安裝根目錄;兩者皆非(找不到根目錄)時維持原行為,以目前目錄為準。</summary>
    private string WriteBase(CliArgs a)
    {
        var (root, isExplicit) = RootOf(a);
        return isExplicit || Directory.Exists(root) ? root : Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// 命令列的寫入位置:相對路徑以 <see cref="WriteBase"/> 為準;在安裝根目錄與專案根目錄之外、而且位於 Windows 系統磁碟時
    /// 丟 <see cref="LocalDataRejectedException"/>(→ 結束碼 1);設了 SIMOSA_ALLOW_SYSTEM_DRIVE=1 則照做並提醒一行。回傳完整路徑。
    /// </summary>
    internal string WritePath(CliArgs a, string what, string path)
        => LocalDataGuard.Require(what, path, WriteBase(a), new[] { RootOf(a).Root, SimCore.Ship.RepositoryPaths.TryFindRoot() }, _localData, n => _err.WriteLine(n));

    /// <summary>
    /// 讀取本工具產生的檔案(--key、--verification、--push、trust add --file、store register-device --public、release verify --dir、
    /// inspect / verify / apply / records accept-report / store import 的檔案):絕對路徑原樣;相對路徑先找目前目錄(原本的行為),
    /// 目前目錄沒有時再找寫入用相對路徑的基準 <see cref="WriteBase"/> — 本工具以相對路徑寫出的檔案都在那裡,所以在根目錄以外的目錄以相對路徑操作時,
    /// keygen --out → pack --key、verification add → verification list / pack --verification、pack --out → sync --push 彼此找得到。
    /// 兩處都有(而且不是同一處)時用目前目錄的,並提醒一行;兩處都沒有時原樣回傳(錯誤訊息與原本相同)。只讀,不做寫入位置檢查。
    /// </summary>
    internal string ReadPath(CliArgs a, string what, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;
        string inCwd, inRoot;
        try
        {
            inCwd = Path.GetFullPath(path);
            inRoot = Path.GetFullPath(Path.Combine(WriteBase(a), path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        if (string.Equals(inCwd, inRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return path;
        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
        if (Exists(inCwd))
        {
            if (Exists(inRoot)) _err.WriteLine($"注意:{what} 的相對路徑 {path} 在目前目錄與根目錄都有,使用目前目錄的 {inCwd}(根目錄的 {inRoot} 未使用;要用它請給絕對路徑)");
            return path;
        }
        return Exists(inRoot) ? inRoot : path;
    }

    /// <summary>config/packaging.json 的位置設定(store、recordsDir):相對路徑以安裝根目錄為準(與 <see cref="InstallLayout"/> 相同),檢查同 <see cref="WritePath"/>。</summary>
    internal string ConfigWritePath(CliArgs a, string what, string configured)
    {
        var root = RootOf(a).Root;
        return LocalDataGuard.Require(what, configured, root, new[] { root, SimCore.Ship.RepositoryPaths.TryFindRoot() }, _localData, n => _err.WriteLine(n));
    }

    /// <summary>船上依已備份回報刪除舊紀錄的指令(records accept-report、import --role ship、serve)用:packaging.json recordsDir 也是寫入位置。</summary>
    private InstallLayout ShipRecordsLayout(CliArgs a)
    {
        var layout = Layout(a);
        if (layout.Config.RecordsDir is { Length: > 0 } r) ConfigWritePath(a, "config/packaging.json 的 recordsDir", r);
        return layout;
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
        if (a.Get("key") is { } keyArg)
        {
            var path = ReadPath(a, "--key", keyArg);   // 相對路徑:目前目錄,沒有時根目錄(keygen --out 以根目錄為準)
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
        var basePath = WritePath(a, "--out", a.Require("out"));   // 相對路徑以根目錄為準;根目錄外的系統磁碟拒絕
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
                var file = ReadPath(a, "--file", a.Require("file"));
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
        // --file:add 會寫入(相對路徑以根目錄為準、根目錄外的系統磁碟拒絕);list 只讀,與 pack --verification 相同(目前目錄,沒有時根目錄)
        var file = sub == "add" ? WritePath(a, "--file", a.Require("file")) : ReadPath(a, "--file", a.Require("file"));
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
            var rf = RecordReader.Read(recPath, includeSnapshots: false);   // 只需標頭與完整性檢查
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
        var outPath = WritePath(a, "--out", a.Require("out"));   // 先檢查寫入位置,再讀來源與簽章
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
            ValidationRoot = a.Get("data-root"), BaselineParameters = a.Get("baseline"),
            VerificationRecordPath = a.Get("verification") is { } vr ? ReadPath(a, "--verification", vr) : null,
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
        var res = UpdatePackageBuilder.Build(req, key, outPath);
        if (res.Impact is not null) _out.Write(ImpactAnalyzer.Format(res.Impact));
        foreach (var w in res.Warnings) _err.WriteLine($"警告:{w}");
        _out.WriteLine($"已產生更新包 {res.OutputPath}");
        _out.WriteLine($"  {res.Manifest.PackageId} 版本 {res.Manifest.Version}({res.Manifest.Kind}),{res.Manifest.Files.Count} 個檔案,驗證狀態 {res.Manifest.Validation.Status}");
        _out.WriteLine($"  sha256 {res.PackageSha256}");
        return 0;
    }

    private int Inspect(CliArgs a)
    {
        var path = ReadPath(a, "檔案", a.Pos(0, "檔案"));
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
        if (path.EndsWith(".sbrec", StringComparison.OrdinalIgnoreCase))
        {
            // 紀錄包:練習與錄影摘要(docs/design/screen-recording.md 第 4 節)
            var m = PackageJson.FromBytes<RecordPackageManifest>(mb, "manifest.json");
            _out.WriteLine($"練習 {m.Exercises.Count} 個、錄影 {m.Media.Count} 段(包內 {m.Media.Count(x => !x.Omitted)}、只有旁檔 {m.Media.Count(x => x.Omitted)})");
            foreach (var e in m.Exercises)
                _out.WriteLine($"  練習 {e.ScenarioId}  {e.StartUtc:yyyy-MM-dd HH:mm}Z  {FormatDuration(e.DurationS)}  錄影 {e.Media.Count} 段");
            foreach (var x in m.Media)
                _out.WriteLine($"  錄影 {x.File}  {x.StartUtc:yyyy-MM-dd HH:mm:ss}Z  {FormatDuration(x.DurationS)}{(x.DurationEstimated ? "(估計)" : "")}  {x.SizeBytes / (1024.0 * 1024):F1} MB  站別 {x.Station ?? "—"}  紀錄 {x.Record ?? "—"}{(x.Truncated ? "  已截斷(上傳超過上限)" : "")}{(x.Omitted ? $"  未納入:{x.OmitReason}" : "")}{(x.Error is null ? "" : $"  旁檔錯誤:{x.Error}")}");
        }
        return 0;
    }

    private static string FormatDuration(double? s)
        => s is { } d ? (d >= 3600 ? $"{(int)(d / 3600)} h {(int)(d % 3600 / 60):D2} min" : $"{(int)(d / 60)} min {(int)(d % 60):D2} s") : "—";

    private int Verify(CliArgs a)
    {
        var layout = Layout(a);
        var v = new UpdateApplier(layout).Verify(ReadPath(a, "更新包", a.Pos(0, "更新包")), a.Flag("allow-unverified"));
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
        var r = new UpdateApplier(layout).Apply(ReadPath(a, "更新包", a.Pos(0, "更新包")), new UpdateApplyOptions { AllowUnverified = a.Flag("allow-unverified"), Operator = a.Get("operator") ?? System.Environment.UserName });
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
        // 降版(以較舊的安裝程式重新安裝過):上面的 software 版次不是實際執行的程式 — 與 verify-install 的「狀態」提醒同一段文字
        if (st.ReleaseDowngrade is { } dg) _err.WriteLine($"警告:{dg.Message}");
        if (st.ParameterSet is { } ps) _out.WriteLine($"  參數集 {ps.Id} 版次 {ps.Version}");
        foreach (var d in st.SmsDocuments) _out.WriteLine($"  SMS {d.DocumentId} 第 {d.Revision} 版(核准人 {d.ApprovedBy})");
        _out.WriteLine($"信任金鑰:{string.Join("、", st.TrustedKeys.Select(k => $"{k.Name}({TrustedKey.RoleLabel(k.Role)})"))}");
        _out.WriteLine($"待回傳紀錄包 {st.OutstandingRecordPackages} 個;訪船 #{st.VisitSeq};{(st.RollbackAvailable ? "可回復上一版" : "無上一版")}");
        // 程式檔完整性不在 status 內做(逐檔 SHA-256 太慢):指向安裝模式核對(規劃書第 5.5 節;packaging-sync.md 第 5.1 節)
        _out.WriteLine("程式檔完整性核對(逐檔 SHA-256,需一些時間):simosa-pkg verify-install(同 release verify --dir <安裝根目錄> --installed)");
        // 環境版次(作業系統 / .NET / WebView2 Runtime;規劃書第 5.5 節兩船版次清單):同步後寫進教官端 fleet-versions.json
        if (st.Environment is { } env) _out.WriteLine($"環境:{env.Describe()}");
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
        // accept-report 會刪除 recordsDir 內已備份的紀錄:packaging.json recordsDir 也是寫入位置
        var layout = sub == "accept-report" ? ShipRecordsLayout(a) : Layout(a);
        var mgr = a.Get("media-max-mb") is { } mm
            ? new ShipRecordManager(layout) { MediaMaxBytes = (long.TryParse(mm, out var mmv) && mmv >= 0 ? mmv : throw new CliUsageException("--media-max-mb 須為非負整數(MB)")) * 1024L * 1024L }
            : new ShipRecordManager(layout);
        switch (sub)
        {
            case "pack":
            {
                var p = mgr.PackPending();
                var mediaCount = p?.Files.Count(f => f.Kind == "media") ?? 0;
                _out.WriteLine(p is null ? "沒有新的紀錄需要打包" : $"已打包 {p.PackageId}:{p.Files.Count} 個檔案、{p.Exercises} 個練習、{p.TrainingS / 3600:F2} 小時{(mediaCount > 0 ? $"、錄影 {mediaCount} 段" : "")}");
                foreach (var w in mgr.RetentionWarnings().Where(w => w.Contains("錄影"))) _err.WriteLine($"警告:{w}");
                return 0;
            }
            case "list":
                foreach (var p in mgr.Outstanding())
                    _out.WriteLine($"{p.PackageId}  {p.CreatedUtc:yyyy-MM-dd HH:mm}Z  {p.Files} 檔 {p.Exercises} 練習  訪船 #{p.VisitSeq}  {(p.PulledUtc is null ? "未取回" : "已取回,待備份回報")}");
                foreach (var w in mgr.RetentionWarnings()) _err.WriteLine($"警告:{w}");
                return 0;
            case "accept-report":
            {
                var r = mgr.AcceptBackupReport(File.ReadAllBytes(ReadPath(a, "回報檔", a.Pos(1, "回報檔"))));
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
        // 寫入位置先檢查(被拒絕時不先建立紀錄庫資料夾)
        var backupTo = sub == "backup" ? WritePath(a, "--to", a.Require("to")) : null;
        var reportOut = sub == "report" ? WritePath(a, "--out", a.Require("out")) : null;
        var store = StoreOf(a);
        switch (sub)
        {
            case "import":
            {
                var r = store.Import(ReadPath(a, "紀錄包", a.Pos(1, "紀錄包")), a.Flag("enroll-device"));
                foreach (var e in r.Errors) _err.WriteLine($"  - {e}");
                if (r.DeviceEnrolled) _err.WriteLine($"注意:已首次登錄裝置 {r.ShipId}/{r.DeviceId},請與船上 device show 的金鑰 ID 核對");
                if (r.Ok) _out.WriteLine(r.AlreadyImported ? $"{r.PackageId} 已在紀錄庫" : $"已匯入 {r.PackageId} → {r.Directory}({r.Manifest?.Exercises.Count} 個練習,{r.Manifest?.TrainingHours.TotalHours} 小時)");
                return r.Ok ? 0 : 1;
            }
            case "register-device":
            {
                using var k = store.RegisterDevice(a.Require("ship"), a.Require("device"), File.ReadAllText(ReadPath(a, "--public", a.Require("public"))));
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
                var (target, marked) = store.Backup(backupTo!);
                _out.WriteLine($"已備份紀錄庫到 {target},標記 {marked.Count} 個紀錄包完成岸上備份");
                return 0;
            }
            case "report":
            {
                var outPath = reportOut!;
                using var key = SigningKey(a);
                var c = store.CreateBackupReport(a.Require("ship"), key);
                if (c is null)
                {
                    _out.WriteLine("沒有已備份且待船上清理的紀錄包");
                    return 0;
                }
                File.WriteAllBytes(outPath, c.Value.Document.ToBytes());
                _out.WriteLine($"已產生已備份回報 {outPath}({c.Value.Report.Packages.Count} 個紀錄包)");
                return 0;
            }
            case "list":
                foreach (var p in store.LoadLedger().Packages)
                    _out.WriteLine($"{p.ShipId} {p.PackageId} {p.Directory} 匯入 {p.ImportedUtc:yyyy-MM-dd} 備份 {(p.BackedUpUtc is { } b ? b.ToString("yyyy-MM-dd") : "未")} 船上 {(p.ShipDeletedUtc is null ? "保留" : "已刪除")}");
                foreach (var (ship, v) in store.LoadFleet().Ships)
                {
                    // 保存的發行版狀態(上一次同步 / 隨身碟匯入時船上回報的):同一行帶出發行版,有降版提醒時另列警告
                    var release = ShipReleaseReport.FromEntry(v);
                    // 文字欄位是同步時船上回報、原樣存進 fleet-versions.json 的(隨身碟途徑連船舶識別都沒有簽章):印出之前去掉控制字元並截斷
                    _out.WriteLine($"版次 {Shown(ship)}:{Shown(v.Version)}(參數集 {Shown(v.ParameterSet?.Version)},更新於 {v.UpdatedUtc:yyyy-MM-dd},經 {Shown(v.Via, 16)};環境 {(v.Environment is { } env ? $"{Shown(env.Describe(), 300)}(收集於 {env.CollectedUtc:yyyy-MM-dd})" : "未回報")};{release.Describe(brief: true)})");
                    // 句尾寫明兩個時間(都是 UTC,句尾的 Z;與 status 的「套用於 …Z」相同的寫法):船上回報這份狀態的時間(船上的時鐘)與教官筆電保存它的時間 —
                    // 只寫保存的日期會把「很久以前匯出、今天才匯入」的隨身碟狀態看成今天的
                    var reportedAt = ShipVersionEntry.StatusUtcOf(v.Environment, DateTime.UtcNow) is { } t ? $"船上於 {UtcText(t)} 回報、" : "";
                    var saved = $"這是{reportedAt}教官筆電於 {UtcText(v.UpdatedUtc)} {(v.Via == "usb" ? "匯入隨身碟" : "直連同步")}時保存的提醒{(v.Via == "usb" ? ";隨身碟上的 status.json 沒有簽章,以船上的 simosa-pkg status 為準" : "")}";
                    foreach (var w in release.Warnings(ShipReleaseReport.ShipLabel(string.IsNullOrEmpty(v.ShipId) ? ship : v.ShipId, v.DeviceId), saved)) _err.WriteLine($"警告:{w}");
                }
                return 0;
            default: throw new CliUsageException($"未知的 store 子指令:{sub}");
        }
    }

    // ── 發行版清單 ────────────────────────────────────────

    private int ReleaseCmd(CliArgs a)
    {
        var sub = a.Pos(0, "子指令(manifest|verify)");
        var dir = a.Require("dir");
        switch (sub)
        {
            case "manifest":
            {
                var components = new Dictionary<string, ReleaseComponent>(StringComparer.Ordinal);
                foreach (var spec in a.All("component"))
                {
                    // <名稱>=<版本>[=<相對路徑>[=<狀態>]]
                    var parts = spec.Split('=', 4);
                    if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new CliUsageException($"--component 格式為 <名稱>=<版本>[=<相對路徑>[=<狀態>]]:{spec}");
                    components[parts[0]] = new ReleaseComponent { Version = parts[1], Path = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null, Status = parts.Length > 3 ? parts[3] : null };
                }
                var outDir = WritePath(a, "--dir", dir);   // manifest.json 寫進此目錄
                var m = ReleaseManifestBuilder.Build(outDir, a.Require("version"), a.Get("git"), a.Get("branch"), a.Get("runtime"), components, a.All("note"));
                var path = ReleaseManifestBuilder.Save(m, outDir);
                _out.WriteLine($"已寫入發行清單 {path}");
                _out.WriteLine($"  版本 {m.Version}{(m.GitRevision is { } g ? $",git {KeyFiles.Short(g)}" : "")},{m.FileCount} 個檔案,{m.TotalSize / 1024.0 / 1024.0:F1} MiB,{m.Components.Count} 個元件");
                foreach (var n in m.Notes) _out.WriteLine($"  註記:{n}");
                return 0;
            }
            case "verify":
            {
                dir = ReadPath(a, "--dir", dir);   // 只讀:目前目錄,沒有時根目錄(release manifest --dir 以根目錄為準)
                if (a.Flag("installed")) return VerifyInstalled(dir, a);
                var v = ReleaseManifestBuilder.Verify(dir);
                // 使用中的安裝根目錄(有 state\、build\records…)以一般模式核對時,執行期資料與本機設定都會被列為不符:提示改用 --installed(不改結束碼)
                var hint = InstalledVerifier.LooksInstalled(dir) is { } sign
                    ? $"這個目錄看起來是使用中的安裝根目錄(有 {sign}):執行期資料、本機設定與更新包換過的檔案在這個模式都會列為不符;安裝後請改用 release verify --dir <安裝根目錄> --installed"
                    : null;
                if (a.Flag("json"))
                {
                    _out.WriteLine(PackageJson.ToText(new
                    {
                        format = "simosa-release-verify/1", mode = "release", ok = v.Ok, dir = Path.GetFullPath(dir),
                        version = v.Manifest?.Version, gitRevision = v.Manifest?.GitRevision, runtime = v.Manifest?.Runtime, files = v.Manifest?.FileCount,
                        @checked = v.Checked, errors = v.Errors, warnings = v.Warnings, hint,
                    }));
                    return v.Ok ? 0 : 1;
                }
                if (v.Manifest is { } m)
                    _out.WriteLine($"發行清單:版本 {m.Version}{(m.GitRevision is { } g ? $",git {KeyFiles.Short(g)}" : "")},{m.Runtime ?? "(未標示執行階段)"},建置於 {m.BuiltUtc:yyyy-MM-dd HH:mm}Z,{m.FileCount} 個檔案");
                foreach (var w in v.Warnings) _err.WriteLine($"警告:{w}");
                foreach (var e in v.Errors) _err.WriteLine($"  - {e}");
                (v.Ok ? _out : _err).WriteLine(v.Ok ? $"核對通過({v.Checked} 個檔案)" : $"核對失敗({v.Errors.Count} 個問題)");
                if (hint is not null) _err.WriteLine($"提示:{hint}");
                return v.Ok ? 0 : 1;
            }
            default:
                throw new CliUsageException($"未知的 release 子指令:{sub}");
        }
    }

    /// <summary>
    /// 安裝模式核對(release verify --installed 與 verify-install 共用;<see cref="InstalledVerifier"/>):結束碼 0 = 沒有錯誤(可有提醒)、1 = 有錯誤。
    /// </summary>
    private int VerifyInstalled(string root, CliArgs a)
    {
        // 先解析參數再核對:--max-items 寫錯時立刻回用法錯誤,不要等全部檔案的雜湊算完(程式在隨身碟、檔案數千個時要等很久)才把結果丟掉
        var maxItems = a.Int("max-items", 10);
        var rep = InstalledVerifier.Verify(root);
        if (a.Flag("json")) _out.WriteLine(PackageJson.ToText(rep));
        else InstalledVerifier.Write(rep, _out, _err, maxItems);
        return rep.Ok ? 0 : 1;
    }

    // ── 同步 ──────────────────────────────────────────────

    private int Serve(CliArgs a)
    {
        var layout = ShipRecordsLayout(a);   // 同步時依已備份回報刪除 recordsDir 內的舊紀錄
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
        var store = StoreOf(a);   // 先檢查紀錄庫的寫入位置,再載入簽章金鑰
        using var key = SigningKey(a);
        var report = new SyncReport();
        try
        {
            SyncClient.SyncAsync(new SyncOptions
            {
                Host = host, Port = a.Int("port", layout.Config.Port), InstructorKey = key, Store = store, PushPackages = a.All("push").Select(p => ReadPath(a, "--push", p)).ToList(),
                EnrollDevice = a.Flag("enroll-device"), PullRecords = !a.Flag("no-pull"), SendBackupReport = !a.Flag("no-report"), Log = _out,
            }, report: report).GetAwaiter().GetResult();
        }
        catch when (report.Release is not null && !report.FleetSaved)
        {
            // 同步在最後一步失敗(船上狀態的其餘欄位讀不進來、fleet-versions.json 讀不進來或寫不進去):這次已經收到的發行版與降版提醒照樣印出來,
            // 再由上層報錯(「錯誤:…」,結束碼與原本相同)— 提醒不跟著存檔一起不見
            PrintSyncStatus(report);
            throw;
        }
        PrintSyncStatus(report);
        // 船端回報的警告(打包問題、保留上限)沒有簽章:印出之前同樣去掉控制字元並截斷 — 它們緊接在降版提醒之後,ANSI 跳脫序列可以把剛印的提醒從畫面上清掉
        foreach (var w in report.Warnings) _err.WriteLine($"警告:{Shown(w, 600)}");
        _out.WriteLine(report.Ok ? "同步完成" : "同步完成,但有項目失敗(見上方訊息)");
        return report.Ok ? 0 : 1;
    }

    /// <summary>sync 結束時印船上的現行版本一行與發行版狀態(同步在最後一步失敗時也印已經收到的部分)。</summary>
    private void PrintSyncStatus(SyncReport report)
    {
        // 這一行的文字欄位都來自船上的 statusResult(沒有自己的簽章):印出之前去掉控制字元並截斷(合法的內容不受影響)
        if (report.Status is { } st) _out.WriteLine($"{Shown(report.ShipId)} 現行版本 {Shown(st.Version ?? "(初始)")},參數集 {Shown(st.ParameterSet?.Version ?? "—")},待回傳紀錄包 {st.OutstandingRecordPackages}{(st.Environment is { } env ? $";環境 {Shown(env.Describe(), 300)}" : "")}");
        // 船上的發行版與降版提醒(沒有開教官站、只做同步時也看得到;降版只是提醒,結束碼不變)
        if (report.Release is { } release) PrintShipRelease(report.ShipId, report.DeviceId, release, summaryNote: null, source: null, saved: report.FleetSaved);
    }

    /// <summary>船上回報的文字(statusResult / status.json / fleet-versions.json 裡保存的欄位、船端的警告與錯誤訊息)印到終端機之前:去掉控制字元並截斷。</summary>
    private static string Shown(string? text, int max = 64) => UntrustedText.Clean(text, max);

    /// <summary>時間的寫法:UTC,句尾加 Z(與 status 的「套用於 2026-11-26 04:48Z」相同;台灣時間 = 加 8 小時)</summary>
    private static string UtcText(DateTime utc, bool seconds = false) => seconds ? $"{utc:yyyy-MM-dd HH:mm:ss}Z" : $"{utc:yyyy-MM-dd HH:mm}Z";

    /// <summary>
    /// 教官端同步 / 隨身碟匯入後印船上的發行版狀態(docs/design/packaging-sync.md 第 5.1 節末):stdout 一行摘要
    /// (「FSB1 船上筆電:發行版 2026.11.31」;船上沒有回報時說明是較舊的軟體);有降版或有讀不懂的內容時 stderr 另列「警告:」。
    /// 文字都由 <see cref="ShipReleaseReport"/> 產生(版本只有通過檢查的數字、降版說明與船上 status 同一個出處),船舶與裝置識別去掉控制字元。
    /// <paramref name="summaryNote"/>:接在摘要行後面的括號(隨身碟途徑:這份狀態在船上產生的時間);<paramref name="source"/>:接在警告句尾的來源說明;
    /// <paramref name="saved"/> false:這次的內容沒有存進兩船版次清單(另列一則警告,原因由呼叫端接著印)。
    /// </summary>
    private void PrintShipRelease(string? shipId, string? deviceId, ShipReleaseReport release, string? summaryNote, string? source, bool saved)
    {
        _out.WriteLine($"{ShipReleaseReport.ShipLabel(shipId)} 船上筆電:{release.Describe()}{(summaryNote is null ? "" : $"({summaryNote})")}");
        foreach (var w in release.Warnings(ShipReleaseReport.ShipLabel(shipId, deviceId), source)) _err.WriteLine($"警告:{w}");
        if (!saved)
            _err.WriteLine($"警告:船上筆電 {ShipReleaseReport.ShipLabel(shipId)} 這次回報的內容沒有存進教官筆電的兩船版次清單 fleet-versions.json(原因見下方的訊息);simosa-pkg store list 顯示的仍是先前保存的內容");
    }

    /// <summary>
    /// 隨身碟匯入:一艘船的 status.json。比教官筆電已有的舊 → 只印一行說明(不印它的提醒:那是過時的內容,教官筆電保存的不變);
    /// 其餘照 <see cref="PrintShipRelease"/> 印,摘要與警告句尾註明這份狀態在船上產生的時間(隨身碟上的檔可能是很久以前匯出的)與沒有簽章。
    /// </summary>
    private void PrintUsbShipRelease(UsbShipRelease s)
    {
        if (s.Fleet is { Saved: false } skipped)
        {
            var how = skipped.Previous?.Via == "usb" ? "匯入隨身碟" : "直連同步";
            _out.WriteLine($"{ShipReleaseReport.ShipLabel(s.ShipId)} 船上筆電:隨身碟上的狀態是船上於 {UtcText(skipped.StatusUtc ?? default, seconds: true)} 產生的,比教官筆電已有的(船上於 {UtcText(skipped.PreviousStatusUtc ?? default, seconds: true)} 產生,{how}時取得)舊,沒有採用;"
                           + "教官筆電保存的版次與提醒不變(simosa-pkg store list 可以再看)");
            return;
        }
        var when = s.StatusUtc is { } t ? $"船上於 {UtcText(t)} 產生"
            : s.Fleet is { AgeUnknown: true } ? "沒有可用的產生時間,無法判斷是不是比教官筆電原有的舊"
            : "沒有可用的產生時間";
        PrintShipRelease(s.ShipId, s.DeviceId, s.Release, $"隨身碟上的狀態,{when}", $"這是隨身碟上 status.json 的內容,{when};這個檔沒有簽章,以船上的 simosa-pkg status 為準", saved: s.Fleet is not null);
    }

    private string Role(CliArgs a) => a.Get("role") ?? Layout(a).Config.Role;

    private int Export(CliArgs a)
    {
        var to = WritePath(a, "--to", a.Require("to"));   // 隨身碟(E: 等)照常;根目錄外的系統磁碟拒絕
        UsbReport r;
        if (Role(a) == "ship") r = UsbTransfer.ShipExport(new ShipNode(Layout(a)), to);
        else
        {
            IKeyProvider? key = a.Get("key") is not null || a.Get("cert") is not null ? SigningKey(a) : null;
            using (key)
                r = UsbTransfer.InstructorExport(StoreOf(a), to, a.All("push").Select(p => ReadPath(a, "--push", p)).ToList(), key, a.All("ship") is { Count: > 0 } s ? s : null);
            if (key is null) _err.WriteLine("注意:未指定 --key/--cert,未產生已備份回報");
        }
        foreach (var w in r.Written) _out.WriteLine($"已寫入 {w}");
        foreach (var w in r.Warnings) _err.WriteLine($"警告:{w}");
        return 0;
    }

    private int Import(CliArgs a)
    {
        var from = WritePath(a, "--from", a.Require("from"));   // 匯入時也寫回隨身碟(清理通知、待回傳紀錄包)並刪除已處理的檔案
        UsbReport r;
        var shipRole = Role(a) == "ship";
        if (shipRole)
        {
            r = UsbTransfer.ShipImport(new ShipNode(ShipRecordsLayout(a)), from);   // 處理已備份回報時刪除 recordsDir 內的舊紀錄
            foreach (var u in r.Updates) PrintApply(u);
            if (r.Cleanup is { Ok: true } c) _out.WriteLine($"已備份回報 {c.ReportId}:刪除 {c.Deleted.Count} 個紀錄包");
        }
        else
        {
            r = UsbTransfer.InstructorImport(StoreOf(a), from, a.Flag("enroll-device"));
            foreach (var i in r.Imported.Where(i => i.Ok)) _out.WriteLine(i.AlreadyImported ? $"{i.PackageId} 已在紀錄庫" : $"已匯入 {i.PackageId} → {i.Directory}");
            // 隨身碟上各船 status.json 的發行版與降版提醒(與直連同步印的相同;status.json 沒有簽章,提醒的句尾註明)
            foreach (var s in r.ShipReleases) PrintUsbShipRelease(s);
        }
        foreach (var w in r.Written) _out.WriteLine($"已寫入 {w}");
        // 教官端匯入時,警告與錯誤裡可能有隨身碟上未經驗證的內容(驗證失敗的紀錄包 / 清理通知自己寫的船舶識別等):去掉控制字元並截斷再印
        // — 它們緊接在降版提醒之後,ANSI 跳脫序列可以把剛印的提醒從畫面上清掉。船端匯入的輸出照舊
        foreach (var w in r.Warnings) _err.WriteLine($"警告:{(shipRole ? w : Shown(w, 600))}");
        foreach (var e in r.Errors) _err.WriteLine($"  - {(shipRole ? e : Shown(e, 600))}");
        var ok = r.Errors.Count == 0 && r.Updates.All(u => u.Ok);
        return ok ? 0 : 1;
    }
}
