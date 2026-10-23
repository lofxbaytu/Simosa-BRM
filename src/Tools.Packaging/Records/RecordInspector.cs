using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimosaBRM.Packaging.Records;

/// <summary>
/// 自 JSON Lines 紀錄檔讀出練習摘要(只解析 header、最後一行 state/footer 與 summary 是否存在;不依賴 SimCore 型別,
/// 以便舊版/新版紀錄都能打包)。完整性:footer.sha256 與 footer 前所有行(含換行)的 SHA-256 比對(同 RecordReader)。
/// 參與者與休息時間確認(教官站 sessionInfo 指令;規劃書第 5.5 節):header.participants / restHoursConfirmation / course(紀錄開啟前送達或
/// 情境切換另開新檔時由 SimCore 寫入 header),否則取最後一行 {"kind":"session", participants, restHoursConfirmation, course, tick, t}
/// (紀錄開啟後才送達);participants 為字串陣列或 {name, role, rank?, certificateNo?} 物件陣列。來源記於 sessionInfoSource(header | session)。
/// 起訖與時長一律用<b>牆鐘</b>(供轉錄至船上工時/休息時間紀錄;倍速或凍結時模擬時間與實際經過時間不同):
/// 開始 = header.createdUtc;結束 = footer.closedUtc(SimCore 寫入時)→ 否則紀錄檔最後修改時間(估計值)→ 否則開始 + 模擬時長(估計值)。
/// 模擬時長(footer.finalTick × dt,無 footer 時取最後一行 state 的 t)另列為 simDurationS。
/// </summary>
public static class RecordInspector
{
    public static ExerciseSummary Inspect(string path, string packagePath)
    {
        var s = new ExerciseSummary { File = packagePath };
        JsonDocument? header = null;
        JsonDocument? footer = null;
        JsonDocument? lastSession = null;
        double? lastStateT = null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                var kind = PeekKind(line);
                if (kind == "footer")
                {
                    footer = TryParse(line);
                    break;
                }
                hash.AppendData(Encoding.UTF8.GetBytes(line));
                hash.AppendData("\n"u8);
                if (kind == "header" && header is null) header = TryParse(line);
                else if (kind == "summary") s.HasSummary = true;
                else if (kind == "session")
                {
                    // 只保留最後一行(教官站可重送修正)
                    if (TryParse(line) is { } doc) { lastSession?.Dispose(); lastSession = doc; }
                }
                else if (kind == "state" && line.Length < 64 * 1024)
                {
                    // 只取時間;以字串搜尋避免每行完整解析
                    var idx = line.IndexOf("\"t\":", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var end = line.IndexOfAny(new[] { ',', '}' }, idx + 4);
                        if (end > idx && double.TryParse(line.AsSpan(idx + 4, end - idx - 4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                            lastStateT = t;
                    }
                }
            }
            var computed = Convert.ToHexString(hash.GetHashAndReset());
            if (header is not null)
            {
                var h = header.RootElement;
                s.ShipId = Str(h, "shipId");
                s.Loading = Str(h, "loading");
                if (h.TryGetProperty("createdUtc", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTime(out var dt)) s.StartUtc = dt.ToUniversalTime();
                if (h.TryGetProperty("scenario", out var sc) && sc.ValueKind == JsonValueKind.Object)
                {
                    s.ScenarioId = Str(sc, "id");
                    s.ScenarioName = Str(sc, "name");
                }
                if (h.TryGetProperty("parameterSet", out var ps) && ps.ValueKind == JsonValueKind.Object) s.ParameterSetVersion = Str(ps, "version");
                // 參與者資訊:header 有就用 header,否則取最後一行 session
                if (HasSessionFields(h)) { ReadSessionFields(h, s); s.SessionInfoSource = "header"; }
                else if (lastSession is not null && HasSessionFields(lastSession.RootElement)) { ReadSessionFields(lastSession.RootElement, s); s.SessionInfoSource = "session"; }
                double? dtStep = h.TryGetProperty("dt", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                DateTime? closedUtc = null;
                if (footer is not null)
                {
                    var f = footer.RootElement;
                    if (f.TryGetProperty("finalTick", out var ft) && ft.ValueKind == JsonValueKind.Number && dtStep is { } step) s.SimDurationS = ft.GetInt64() * step;
                    s.IntegrityOk = f.TryGetProperty("sha256", out var fs) && fs.ValueKind == JsonValueKind.String && string.Equals(fs.GetString(), computed, StringComparison.OrdinalIgnoreCase);
                    if (f.TryGetProperty("closedUtc", out var cu) && cu.ValueKind == JsonValueKind.String && cu.TryGetDateTime(out var cdt)) closedUtc = cdt.ToUniversalTime();
                }
                s.SimDurationS ??= lastStateT;
                if (s.StartUtc is { } st)
                {
                    var fileTime = File.GetLastWriteTimeUtc(path);
                    if (closedUtc is { } closed && closed >= st) s.EndUtc = closed;
                    else
                    {
                        s.EndEstimated = true;
                        if (fileTime >= st) s.EndUtc = fileTime;
                        else if (s.SimDurationS is { } simDur) s.EndUtc = st.AddSeconds(simDur);
                    }
                    if (s.EndUtc is { } end) s.DurationS = (end - st).TotalSeconds;
                }
                else
                {
                    // 沒有開始時間:無法得到牆鐘時長,只能以模擬時長估計
                    s.DurationS = s.SimDurationS;
                    s.EndEstimated = true;
                }
            }
        }
        finally
        {
            header?.Dispose();
            footer?.Dispose();
            lastSession?.Dispose();
        }
        return s;
    }

    private static bool HasSessionFields(JsonElement e)
        => (e.TryGetProperty("participants", out var p) && p.ValueKind == JsonValueKind.Array)
           || (e.TryGetProperty("restHoursConfirmation", out var r) && r.ValueKind == JsonValueKind.Object);

    /// <summary>自 header 或 session 行讀 participants / restHoursConfirmation / course(欄位形狀相同)。</summary>
    private static void ReadSessionFields(JsonElement e, ExerciseSummary s)
    {
        if (e.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parts.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } n) s.Participants.Add(new Participant { Name = n });
                else if (p.ValueKind == JsonValueKind.Object && Str(p, "name") is { Length: > 0 } pn)
                    s.Participants.Add(new Participant { Name = pn, Role = Str(p, "role"), Rank = Str(p, "rank"), CertificateNo = Str(p, "certificateNo") });
            }
        }
        if (e.TryGetProperty("restHoursConfirmation", out var rc) && rc.ValueKind == JsonValueKind.Object) s.RestHoursConfirmation = rc.Clone();
        if (e.TryGetProperty("course", out var c) && c.ValueKind == JsonValueKind.Object) s.Course = c.Clone();
    }

    /// <summary>是否已寫入 footer(練習已結束)。</summary>
    public static bool HasFooter(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = fs.Length;
        if (len == 0) return false;
        var n = (int)Math.Min(len, 4096);
        fs.Seek(len - n, SeekOrigin.Begin);
        var buf = new byte[n];
        fs.ReadExactly(buf);
        var tail = Encoding.UTF8.GetString(buf).TrimEnd('\n', '\r');
        var last = tail[(tail.LastIndexOf('\n') + 1)..];
        return PeekKind(last) == "footer";
    }

    private static string? PeekKind(string line)
    {
        var idx = line.IndexOf("\"kind\":\"", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + 8;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    private static JsonDocument? TryParse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>練習清單 → 訓練時數彙總(總時數 + 每位參與者)。</summary>
    public static TrainingHours Summarize(IEnumerable<ExerciseSummary> exercises)
    {
        var list = exercises.ToList();
        var hours = new TrainingHours
        {
            ExerciseCount = list.Count, TotalS = list.Sum(e => e.DurationS ?? 0), SimTotalS = list.Sum(e => e.SimDurationS ?? 0),
            Estimated = list.Any(e => e.EndEstimated),
        };
        var byName = new Dictionary<string, ParticipantHours>(StringComparer.Ordinal);
        foreach (var e in list)
        {
            foreach (var p in e.Participants)
            {
                var k = p.Name + "\u0001" + (p.Role ?? "");
                if (!byName.TryGetValue(k, out var ph)) byName[k] = ph = new ParticipantHours { Name = p.Name, Role = p.Role };
                // 職級/證書號以最近一次練習填寫者為準(同人同職務)
                if (p.Rank is { Length: > 0 }) ph.Rank = p.Rank;
                if (p.CertificateNo is { Length: > 0 }) ph.CertificateNo = p.CertificateNo;
                ph.Exercises++;
                ph.TotalS += e.DurationS ?? 0;
                ph.Sessions.Add(new Session { ScenarioId = e.ScenarioId, StartUtc = e.StartUtc, EndUtc = e.EndUtc, DurationS = e.DurationS, SimDurationS = e.SimDurationS, EndEstimated = e.EndEstimated });
            }
        }
        hours.Participants = byName.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        return hours;
    }
}
