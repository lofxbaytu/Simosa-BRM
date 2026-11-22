namespace SimosaBRM.SimCore.Ship;

/// <summary>
/// 寫入位置檢查用的環境(可注入,讓 Linux 上的單元測試模擬 Windows + 系統磁碟 C:):
/// <paramref name="IsWindows"/> 為 false 時不檢查;<paramref name="SystemDrive"/> 為 Windows 系統磁碟(%SystemDrive%,例如 "C:";null = 無法判斷,不檢查);
/// <paramref name="AllowSystemDrive"/> 為經同意的例外開關(環境變數 SIMOSA_ALLOW_SYSTEM_DRIVE=1)。
/// </summary>
public sealed record LocalDataEnvironment(bool IsWindows, string? SystemDrive, bool AllowSystemDrive)
{
    /// <summary>目前程序的環境:作業系統、%SystemDrive%(未設定時取 Windows 目錄所在的磁碟)、SIMOSA_ALLOW_SYSTEM_DRIVE。</summary>
    public static LocalDataEnvironment FromProcess()
    {
        var isWindows = OperatingSystem.IsWindows();
        var drive = System.Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(drive) && isWindows)
        {
            try { drive = Path.GetPathRoot(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows))?.TrimEnd('\\', '/'); }
            catch (Exception) { drive = null; }
        }
        return new LocalDataEnvironment(isWindows, string.IsNullOrWhiteSpace(drive) ? null : drive,
            System.Environment.GetEnvironmentVariable(LocalDataGuard.AllowSystemDriveVariable) == "1");
    }
}

/// <summary>寫入位置的判定結果。</summary>
public enum LocalDataVerdict
{
    /// <summary>非 Windows(或無法判斷系統磁碟):不檢查</summary>
    NotChecked,
    /// <summary>在根目錄之內(根目錄本身在系統磁碟也允許;那由啟動腳本警告)</summary>
    InsideRoot,
    /// <summary>在根目錄之外,但不在系統磁碟(例如 E: 隨身碟、UNC 網路路徑)</summary>
    NotSystemDrive,
    /// <summary>在根目錄之外的系統磁碟,但已設 SIMOSA_ALLOW_SYSTEM_DRIVE=1(經同意的例外)</summary>
    AllowedBySwitch,
    /// <summary>在根目錄之外的系統磁碟:拒絕</summary>
    Rejected,
}

/// <summary>解析後的寫入位置與判定;<see cref="Roots"/> 為判定時採用的根目錄(訊息用)。</summary>
public sealed record LocalDataTarget(string Path, LocalDataVerdict Verdict, string? SystemDrive, IReadOnlyList<string> Roots)
{
    public bool Allowed => Verdict != LocalDataVerdict.Rejected;
}

/// <summary>寫入位置在根目錄之外的系統磁碟而被拒絕(訊息已含守則與例外開關的說明)。</summary>
public sealed class LocalDataRejectedException : ArgumentException
{
    public LocalDataRejectedException(string message, LocalDataTarget target) : base(message) => Target = target;
    public LocalDataTarget Target { get; }
}

/// <summary>
/// 本機資料守則(CLAUDE.md;docs/ops/本機資料位置.md 第 1、4 節)的寫入位置檢查,各主控程式共用這一份:
/// <list type="number">
/// <item>相對路徑以根目錄(安裝根目錄或專案根目錄;各程式沿用自己的根目錄判斷)為準,不以目前目錄為準 — <see cref="FullPath"/>;</item>
/// <item>寫入位置在根目錄之外、而且位於 Windows 系統磁碟(%SystemDrive%,不分大小寫)時拒絕 — <see cref="Check"/>;
///   設了 SIMOSA_ALLOW_SYSTEM_DRIVE=1(經訓練負責人同意)則照做並提醒。根目錄之內不檢查;非 Windows 不檢查。</item>
/// </list>
/// 路徑形式:正斜線、<c>..</c>、大小寫、<c>\\?\C:\</c> / <c>\\.\C:\</c> 前綴都先正規化;UNC(<c>\\server\share</c>、<c>\\?\UNC\…</c>)不是系統磁碟。
/// Windows 語意的判斷以字串完成(不依賴目前作業系統的 System.IO.Path),所以 Linux 上可用注入的 <see cref="LocalDataEnvironment"/> 測試。
/// </summary>
public static class LocalDataGuard
{
    /// <summary>經同意後放行寫到根目錄外系統磁碟的環境變數(值須為 1;與 tools/launch/launch-common.ps1 相同)</summary>
    public const string AllowSystemDriveVariable = "SIMOSA_ALLOW_SYSTEM_DRIVE";

    /// <summary>
    /// 完整路徑:絕對路徑原樣(正規化),相對路徑以 <paramref name="relativeBase"/>(通常是根目錄)為準。
    /// <paramref name="env"/> 為 Windows 但目前不是 Windows(測試)時以 Windows 語意的字串運算組合;其餘情況用 System.IO.Path。
    /// </summary>
    public static string FullPath(string path, string relativeBase, LocalDataEnvironment env)
    {
        var p = path.Trim();
        if (env.IsWindows && !OperatingSystem.IsWindows()) return WindowsCombine(relativeBase, p);
        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(relativeBase, p));
    }

    /// <summary>判定 <paramref name="fullPath"/>(已是完整路徑)是否可寫;<paramref name="roots"/> 為允許的根目錄(null 與空字串略過)。</summary>
    public static LocalDataTarget Check(string fullPath, IEnumerable<string?> roots, LocalDataEnvironment env)
    {
        var rootList = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sys = DriveLetter(env.SystemDrive);
        if (!env.IsWindows || sys is null) return new LocalDataTarget(fullPath, LocalDataVerdict.NotChecked, env.SystemDrive, rootList);
        var norm = NormalizeWindows(fullPath);
        if (rootList.Any(r => IsUnderWindows(norm, NormalizeWindows(r))))
            return new LocalDataTarget(fullPath, LocalDataVerdict.InsideRoot, env.SystemDrive, rootList);
        var drive = norm.Length >= 2 && norm[1] == ':' ? char.ToUpperInvariant(norm[0]) : (char?)null;
        if (drive != sys) return new LocalDataTarget(fullPath, LocalDataVerdict.NotSystemDrive, env.SystemDrive, rootList);
        return new LocalDataTarget(fullPath, env.AllowSystemDrive ? LocalDataVerdict.AllowedBySwitch : LocalDataVerdict.Rejected, env.SystemDrive, rootList);
    }

    /// <summary><see cref="FullPath"/> + <see cref="Check"/>。</summary>
    public static LocalDataTarget Evaluate(string path, string relativeBase, IEnumerable<string?> roots, LocalDataEnvironment env)
        => Check(FullPath(path, relativeBase, env), roots, env);

    /// <summary>
    /// 解析並檢查;被拒絕時丟 <see cref="LocalDataRejectedException"/>(訊息說明守則與開關),經同意的例外以 <paramref name="notice"/> 提醒一行。
    /// <paramref name="what"/> 為選項或設定名稱(例如 "--record"、"packaging.json recordsDir")。回傳完整路徑。
    /// </summary>
    public static string Require(string what, string path, string relativeBase, IEnumerable<string?> roots, LocalDataEnvironment env, Action<string>? notice = null)
        => Require(what, Evaluate(path, relativeBase, roots, env), notice);

    /// <summary>已判定的 <paramref name="target"/>:被拒絕時丟例外,經同意的例外以 <paramref name="notice"/> 提醒;回傳完整路徑。</summary>
    public static string Require(string what, LocalDataTarget target, Action<string>? notice = null)
    {
        if (target.Verdict == LocalDataVerdict.Rejected) throw new LocalDataRejectedException(RejectionMessage(what, target), target);
        if (target.Verdict == LocalDataVerdict.AllowedBySwitch) notice?.Invoke(ConsentNotice(what, target));
        return target.Path;
    }

    /// <summary>拒絕訊息:位置、根目錄、守則、替代做法與例外開關。</summary>
    public static string RejectionMessage(string what, LocalDataTarget target)
    {
        var sys = SystemDriveLabel(target.SystemDrive);
        var roots = target.Roots.Count == 0 ? "(找不到根目錄)" : string.Join("、", target.Roots);
        // 磁碟代號本身以冒號結尾("C:"):後面接句號,不接冒號(避免「C::」)
        return $"{what} 的寫入位置 {target.Path} 在根目錄 {roots} 之外,而且位於系統磁碟 {sys}。" +
               $"本機資料守則為所有資料放在專案(安裝)資料夾內(D: 或 G:),不寫 {sys}。請改用根目錄內的路徑(相對路徑以根目錄為準)或其他磁碟(例如隨身碟 E:\\);" +
               $"確實要寫到 {sys} 須先取得訓練負責人同意,同意後設環境變數 {AllowSystemDriveVariable}=1 再執行(docs/ops/本機資料位置.md 第 4 節)。";
    }

    /// <summary>經同意的例外(SIMOSA_ALLOW_SYSTEM_DRIVE=1)照做時的一行提醒。</summary>
    public static string ConsentNotice(string what, LocalDataTarget target)
        => $"注意:{what} 的寫入位置 {target.Path} 在根目錄外的系統磁碟 {SystemDriveLabel(target.SystemDrive)}(已設 {AllowSystemDriveVariable}=1,經同意的例外)";

    private static string SystemDriveLabel(string? systemDrive) => DriveLetter(systemDrive) is { } c ? $"{c}:" : "(系統磁碟)";

    /// <summary>"C:"、"c:\"、"C" → 'C';其他 → null。</summary>
    public static char? DriveLetter(string? systemDrive)
    {
        var s = systemDrive?.Trim();
        if (string.IsNullOrEmpty(s) || !char.IsAsciiLetter(s[0])) return null;
        if (s.Length == 1 || s[1] == ':') return char.ToUpperInvariant(s[0]);
        return null;
    }

    // ---------------------------------------------------------------- Windows 路徑(字串運算;不依賴目前作業系統)

    /// <summary>
    /// Windows 路徑正規化:'/' → '\'、去掉 \\?\ 與 \\.\(\\?\UNC\ → \\)前綴、處理 . 與 ..(不超出根)、段落尾端的句點與空白(見下)、
    /// 去掉結尾的 '\'(磁碟根目錄保留 "C:\")。根的形式:"C:\"(磁碟)、"\\server\share"(UNC);其他(相對路徑、"\foo")不加根原樣處理段落。
    /// <para>
    /// 段落尾端與 GetFullPathNameW 相同(Microsoft〈File path formats on Windows systems〉的 Trim characters):後面接 '\' 的段落只刪
    /// <b>一個</b>結尾句點("Simosa BRM.\x" 即 "Simosa BRM\x"),結尾空白保留("Simosa BRM \x" 是根目錄旁的另一個資料夾 "Simosa BRM ");
    /// 只有路徑不以 '\' 結尾時,最後一段才刪去全部結尾的空白與句點。以 '\' 結尾而最後一段仍以空白或句點結尾時,結果保留結尾的 '\'
    /// (再正規化一次也不會把那段的空白刪掉;"C:\Simosa BRM \" 不等於 "C:\Simosa BRM")。
    /// </para>
    /// </summary>
    internal static string NormalizeWindows(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];

        string prefix;
        string rest;
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', 3);
            var server = parts.Length > 0 ? parts[0] : "";
            var share = parts.Length > 1 ? parts[1] : "";
            prefix = $@"\\{server}\{share}";
            rest = parts.Length > 2 ? parts[2] : "";
        }
        else if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            prefix = char.ToUpperInvariant(p[0]) + @":\";
            rest = p[2..].TrimStart('\\');
        }
        else
        {
            prefix = p.StartsWith('\\') ? @"\" : "";
            rest = p.TrimStart('\\');
        }

        var segments = new List<string>();
        var raws = rest.Split('\\');
        for (var i = 0; i < raws.Length; i++)
        {
            var raw = raws[i];
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            // 後面接 '\' 的段落:只刪一個結尾句點,空白保留;最後一段(路徑不以 '\' 結尾)在迴圈後處理
            var seg = i < raws.Length - 1 && raw.EndsWith('.') ? raw[..^1] : raw;
            if (seg.Length > 0) segments.Add(seg);
        }
        var endsWithSeparator = raws.Length > 0 && raws[^1].Length == 0 && rest.Length > 0;
        var keepSeparator = false;
        if (segments.Count > 0)
        {
            var last = segments[^1];
            if (!endsWithSeparator)
            {
                // 路徑不以 '\' 結尾:最後一段(含 . 與 .. 處理後成為最後的那段)刪去全部結尾的空白與句點
                last = last.TrimEnd(' ', '.');
                if (last.Length == 0) segments.RemoveAt(segments.Count - 1);
                else segments[^1] = last;
            }
            else keepSeparator = last.EndsWith(' ') || last.EndsWith('.');
        }
        var joined = string.Join('\\', segments) + (keepSeparator ? @"\" : "");
        if (prefix.EndsWith('\\')) return prefix + joined;
        if (prefix.Length == 0) return joined;
        return joined.Length == 0 ? prefix : prefix + '\\' + joined;
    }

    /// <summary>Windows 語意的組合:<paramref name="path"/> 為磁碟絕對路徑、UNC 或 \\?\ 形式時原樣;"\foo" 接在 <paramref name="baseDir"/> 的磁碟/UNC 根;"X:foo" 視為 "X:\foo";其餘接在 baseDir 之後。</summary>
    internal static string WindowsCombine(string baseDir, string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return NormalizeWindows(p);
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            return NormalizeWindows(p.Length >= 3 && p[2] == '\\' ? p : p[..2] + '\\' + p[2..]);
        var b = NormalizeWindows(baseDir);
        if (p.StartsWith('\\'))
        {
            var root = b.Length >= 3 && b[1] == ':' ? b[..3] : b.StartsWith(@"\\", StringComparison.Ordinal) ? NormalizeWindows(string.Join('\\', b.Split('\\').Take(4))) : "";
            return NormalizeWindows(root + p);
        }
        return NormalizeWindows(b + '\\' + p);
    }

    /// <summary>兩個已正規化的 Windows 路徑:<paramref name="path"/> 等於 <paramref name="root"/> 或在其下(不分大小寫)。</summary>
    internal static bool IsUnderWindows(string path, string root)
    {
        if (root.Length == 0) return false;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
