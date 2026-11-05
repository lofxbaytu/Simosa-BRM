using System.Text.Json;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>
/// 核心狀態幀(state.schema.json)中操控台回寫需要的欄位(console-serial.md 第 6 節):
/// 實際舵角、轉速、艏向、車鐘檔位、操舵模式、警報摘要、BNWAS。只讀需要的屬性(JsonDocument),缺欄位時用預設值,
/// 不依賴 OwnShipState 的必填欄位,因此測試可用極簡 JSON。
/// </summary>
public sealed record ConsoleStateView
{
    public long Tick { get; init; }
    public double Rudder { get; init; }
    /// <summary>舵令(測試斷言用;回寫只用實際舵角)</summary>
    public double RudderOrder { get; init; }
    public double Rpm { get; init; }
    public double Heading { get; init; }
    /// <summary>車鐘檔位(大寫代號,例如 HAH);缺欄位時為空字串</summary>
    public string Telegraph { get; init; } = "";
    /// <summary>steering.mode:fu / nfu / autopilot / emergency(小寫);缺欄位時 "fu"</summary>
    public string SteeringMode { get; init; } = "fu";
    /// <summary>有未確認(activeUnacknowledged / rectifiedUnacknowledged)的警報</summary>
    public bool AlertUnacknowledged { get; init; }
    /// <summary>有已確認或已靜音但仍作用中(activeAcknowledged / activeSilenced)的警報</summary>
    public bool AlertActiveAcknowledged { get; init; }
    /// <summary>目前未確認且可聽的警報鍵(id@raisedAt_s;再次觸發的同 id 視為新警報)</summary>
    public IReadOnlyList<string> AudibleAlertKeys { get; init; } = Array.Empty<string>();
    public bool BnwasActive { get; init; }
    public int BnwasStage { get; init; }

    /// <summary>狀態幀 → 檢視;非狀態幀(有 "type" 字串屬性的事件/ack/assessment 框、非物件、壞 JSON)回傳 null。</summary>
    public static ConsoleStateView? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) return null;
            if (!root.TryGetProperty("tick", out var tickEl)) return null;

            var unacked = false; var acked = false; var keys = new List<string>();
            if (root.TryGetProperty("alerts", out var alerts) && alerts.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in alerts.EnumerateArray())
                {
                    var state = Str(a, "state");
                    var isUnacked = state is "activeUnacknowledged" or "rectifiedUnacknowledged";
                    if (isUnacked) unacked = true;
                    if (state is "activeAcknowledged" or "activeSilenced") acked = true;
                    if (isUnacked && Bool(a, "audible"))
                        keys.Add($"{Str(a, "id")}@{Num(a, "raisedAt_s").ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }
            var bnwasActive = false; var bnwasStage = 0;
            if (root.TryGetProperty("bnwas", out var bn) && bn.ValueKind == JsonValueKind.Object)
            {
                bnwasActive = Bool(bn, "active");
                bnwasStage = (int)Num(bn, "stage");
            }
            var mode = "fu";
            if (root.TryGetProperty("steering", out var st) && st.ValueKind == JsonValueKind.Object) mode = (Str(st, "mode") ?? "fu").ToLowerInvariant();
            return new ConsoleStateView
            {
                Tick = tickEl.ValueKind == JsonValueKind.Number ? tickEl.GetInt64() : 0,
                Rudder = Num(root, "rudder"),
                RudderOrder = Num(root, "rudderOrder"),
                Rpm = Num(root, "rpm"),
                Heading = Num(root, "heading"),
                Telegraph = (Str(root, "telegraph") ?? "").ToUpperInvariant(),
                SteeringMode = mode,
                AlertUnacknowledged = unacked,
                AlertActiveAcknowledged = acked,
                AudibleAlertKeys = keys,
                BnwasActive = bnwasActive,
                BnwasStage = bnwasStage,
            };
        }
        catch (JsonException) { return null; }
    }

    private static double Num(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0.0;
    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    private static bool Bool(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;
}
