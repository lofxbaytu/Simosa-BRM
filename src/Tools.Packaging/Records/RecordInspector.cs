using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimosaBRM.Packaging.Records;

/// <summary>
/// 自 JSON Lines 紀錄檔讀出練習摘要(只解析 header、最後一行 state/footer 與 summary 是否存在;不依賴 SimCore 型別,
/// 以便舊版/新版紀錄都能打包)。完整性:footer.sha256 與 footer 前所有行(含換行)的 SHA-256 比對(同 RecordReader)。
/// 參與者:header.participants(字串陣列或 {name, role} 物件陣列;日後由練習啟動畫面寫入)。
/// </summary>
public static class RecordInspector
{
    public static ExerciseSummary Inspect(string path, string packagePath)
    {
        var s = new ExerciseSummary { File = packagePath };
        JsonDocument? header = null;
        JsonDocument? footer = null;
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
                if (h.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in parts.EnumerateArray())
                    {
                        if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } n) s.Participants.Add(new Participant { Name = n });
                        else if (p.ValueKind == JsonValueKind.Object && Str(p, "name") is { Length: > 0 } pn) s.Participants.Add(new Participant { Name = pn, Role = Str(p, "role") });
                    }
                }
                if (h.TryGetProperty("restHoursConfirmation", out var rc)) s.RestHoursConfirmation = rc.Clone();
                double? dtStep = h.TryGetProperty("dt", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                if (footer is not null)
                {
                    var f = footer.RootElement;
                    if (f.TryGetProperty("finalTick", out var ft) && ft.ValueKind == JsonValueKind.Number && dtStep is { } step) s.DurationS = ft.GetInt64() * step;
                    s.IntegrityOk = f.TryGetProperty("sha256", out var fs) && fs.ValueKind == JsonValueKind.String && string.Equals(fs.GetString(), computed, StringComparison.OrdinalIgnoreCase);
                }
                s.DurationS ??= lastStateT;
                if (s.StartUtc is { } st && s.DurationS is { } dur) s.EndUtc = st.AddSeconds(dur);
            }
        }
        finally
        {
            header?.Dispose();
            footer?.Dispose();
        }
        return s;
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
        var hours = new TrainingHours { ExerciseCount = list.Count, TotalS = list.Sum(e => e.DurationS ?? 0) };
        var byName = new Dictionary<string, ParticipantHours>(StringComparer.Ordinal);
        foreach (var e in list)
        {
            foreach (var p in e.Participants)
            {
                var k = p.Name + "\u0001" + (p.Role ?? "");
                if (!byName.TryGetValue(k, out var ph)) byName[k] = ph = new ParticipantHours { Name = p.Name, Role = p.Role };
                ph.Exercises++;
                ph.TotalS += e.DurationS ?? 0;
                ph.Sessions.Add(new Session { ScenarioId = e.ScenarioId, StartUtc = e.StartUtc, EndUtc = e.EndUtc, DurationS = e.DurationS });
            }
        }
        hours.Participants = byName.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        return hours;
    }
}
