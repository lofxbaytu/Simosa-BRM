using System.Text.Json;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Visual.Shared.State;

/// <summary>WebSocket 文字框的種類(Host 在同一條連線上送狀態幀與 event/assessment 框)。</summary>
public enum FrameKind
{
    State,
    Event,
    Assessment,
    Unknown,
}

/// <summary>
/// 解析 Host(src/SimCore.Host/WebSocketStateServer.cs)送來的文字框:
/// 有 <c>type</c> 欄位者為 event/assessment 框,其餘為 state.schema.json 的狀態幀(含 targets/tugs/anchors/berth/truth/sensors/alerts/radarEnvironment)。
/// 解析失敗回傳 Unknown 而不丟例外(視景不因單一壞框中斷)。
/// </summary>
public static class StateFrameParser
{
    public static FrameKind Classify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return FrameKind.Unknown;
            if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() switch
                {
                    "event" => FrameKind.Event,
                    "assessment" => FrameKind.Assessment,
                    _ => FrameKind.Unknown,
                };
            }
            return doc.RootElement.TryGetProperty("t", out _) && doc.RootElement.TryGetProperty("pos", out _) ? FrameKind.State : FrameKind.Unknown;
        }
        catch (JsonException)
        {
            return FrameKind.Unknown;
        }
    }

    /// <summary>解析狀態幀;非狀態幀或格式錯誤時回傳 null。</summary>
    public static OwnShipState? TryParseState(string json)
    {
        if (Classify(json) != FrameKind.State) return null;
        try
        {
            return ContractJson.Deserialize<OwnShipState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>事件框的 kind(例如 collision、cpaAlarm);非事件框回傳 null。</summary>
    public static string? TryGetEventKind(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return null;
            return ev.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
