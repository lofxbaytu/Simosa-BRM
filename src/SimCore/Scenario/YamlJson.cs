using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// YAML 與 JSON 節點樹的互轉(情境與參數集都走「YAML → 通用節點樹 → 參數替換 → JSON → System.Text.Json」這條路,
/// 讓多型欄位(時間表動作、觸發條件、簡報字串/物件)只需一套 JSON 轉換器;情境引擎設計第 2.3 節)。
/// 純量型別推斷:加引號者為字串;純量 true/false → 布林、整數/小數 → 數值、null/~/空 → null,其餘為字串。
/// </summary>
public static class YamlJson
{
    public static JsonNode? ToJsonNode(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0) return null;
        return Convert(stream.Documents[0].RootNode);
    }

    public static string ToJson(string yaml) => ToJsonNode(yaml)?.ToJsonString() ?? "null";

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => ConvertMap(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null,
    };

    private static JsonObject ConvertMap(YamlMappingNode map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map.Children)
        {
            var key = k is YamlScalarNode ks ? ks.Value ?? "" : k.ToString() ?? "";
            o[key] = Convert(v);
        }
        return o;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style != ScalarStyle.Plain) return JsonValue.Create(v);
        if (v.Length == 0 || v == "~" || v.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (LooksNumeric(v))
        {
            if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return JsonValue.Create(d);
        }
        return JsonValue.Create(v);
    }

    /// <summary>只接受十進位整數/小數(含指數);排除 0x、底線、.inf、.nan 等 YAML 特殊寫法,以免船名/代號被誤判。</summary>
    private static bool LooksNumeric(string v)
    {
        var i = 0;
        if (v[i] is '+' or '-') i++;
        if (i >= v.Length) return false;
        var digits = 0;
        var dot = false;
        var exp = false;
        for (; i < v.Length; i++)
        {
            var c = v[i];
            if (char.IsAsciiDigit(c)) { digits++; continue; }
            if (c == '.' && !dot && !exp) { dot = true; continue; }
            if ((c == 'e' || c == 'E') && digits > 0 && !exp)
            {
                exp = true;
                if (i + 1 < v.Length && v[i + 1] is '+' or '-') i++;
                if (i + 1 >= v.Length) return false;
                continue;
            }
            return false;
        }
        return digits > 0;
    }

    // ------------------------------------------------------------------ JSON → YAML(ToYaml 用)

    /// <summary>JSON 文字 → YAML(以 YamlDotNet 序列化通用物件圖;鍵名保持原樣)。</summary>
    public static string JsonToYaml(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var graph = ToGraph(doc.RootElement);
        var serializer = new YamlDotNet.Serialization.SerializerBuilder().Build();
        return serializer.Serialize(graph);
    }

    private static object? ToGraph(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToGraph(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToGraph).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>以路徑字串(a.b[2].c)取得節點;不存在時 null。</summary>
    public static JsonNode? GetByPath(JsonNode root, string path)
    {
        JsonNode? cur = root;
        foreach (var seg in SplitPath(path))
        {
            if (cur is null) return null;
            cur = seg.Index is { } idx ? (cur as JsonArray)?.ElementAtOrDefault(idx) : (cur as JsonObject)?[seg.Key!];
        }
        return cur;
    }

    /// <summary>以路徑字串設定節點值(路徑須已存在)。</summary>
    public static bool SetByPath(JsonNode root, string path, JsonNode? value)
    {
        var segs = SplitPath(path);
        JsonNode? cur = root;
        for (var i = 0; i < segs.Count - 1; i++)
        {
            var seg = segs[i];
            cur = seg.Index is { } idx ? (cur as JsonArray)?.ElementAtOrDefault(idx) : (cur as JsonObject)?[seg.Key!];
            if (cur is null) return false;
        }
        var last = segs[^1];
        if (last.Index is { } li) { if (cur is JsonArray a && li < a.Count) { a[li] = value; return true; } return false; }
        if (cur is JsonObject o) { o[last.Key!] = value; return true; }
        return false;
    }

    private static List<(string? Key, int? Index)> SplitPath(string path)
    {
        var list = new List<(string?, int?)>();
        var sb = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '.') { if (sb.Length > 0) { list.Add((sb.ToString(), null)); sb.Clear(); } }
            else if (c == '[')
            {
                if (sb.Length > 0) { list.Add((sb.ToString(), null)); sb.Clear(); }
                var end = path.IndexOf(']', i);
                list.Add((null, int.Parse(path.AsSpan(i + 1, end - i - 1), CultureInfo.InvariantCulture)));
                i = end;
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) list.Add((sb.ToString(), null));
        return list;
    }
}
