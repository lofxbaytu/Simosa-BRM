using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Contracts;

/// <summary>指令型別(src/Contracts/command.schema.json 的 type 列舉;JSON 為 camelCase 字串)。</summary>
public enum SimCommandType
{
    Rudder,
    Telegraph,
    Rpm,
    Thruster,
    Autopilot,
    Freeze,
    Resume,
    Reset,
    TimeScale,
    LoadScenario,
    SetEnvironment,
    InjectFault,
    ClearFault,
    Snapshot,
    Restore,
    /// <summary>目標船控制(args:id、heading/speed 覆寫、add/remove、waypoints、aisOn/aisError、behaviour、colreg、lights/sound、activate、position)</summary>
    TargetControl,
    /// <summary>教官手動觸發時間表項目(args:id、force;情境引擎設計第 5.2 節)</summary>
    TriggerTimeline,
    /// <summary>教官/帶領者記錄口頭辨識、通知機控室等核心偵測不到的動作(args:kind、itemId、role、note)</summary>
    Acknowledge,
    /// <summary>結束練習:凍結並定稿 summary(args:reason)</summary>
    EndExercise,
    /// <summary>查詢目前評估摘要(主控程序直接回覆,不進引擎佇列、不寫 input 行;引擎收到時忽略)</summary>
    GetAssessment,
    /// <summary>操舵模式(value: fu|nfu|autopilot|emergency;args:pump 1|2|both、nfu 的 direction port|starboard|stop、autopilot 的 heading/rotLimit;規劃書第 7.2 節)</summary>
    SteeringMode,
    /// <summary>警報確認(value 或 args.id:警報 id,省略或 "all" = 全部;args.station bridge|portWing|stbdWing|ecr,省略 = bridge;Category A 或已轉移的警報只能在負責站確認;BAM MSC.302(87));駕駛台三站未被拒絕的確認視為駕駛台操作(BNWAS 重設),ecr 不重設</summary>
    AlertAcknowledge,
    /// <summary>警報靜音 silence_s(value 或 args.id,省略或 "all" = 全部;args.station 省略 = bridge,不限制靜音、只決定是否視為駕駛台操作重設 BNWAS)</summary>
    AlertSilence,
    /// <summary>BNWAS 模式(value: off|auto|on;args.dormant_s 180–720、args.stage3_s;MSC.128(75) 保護的模式開關,由教官站操作)</summary>
    BnwasMode,
    /// <summary>BNWAS 重設鍵</summary>
    BnwasReset,
    /// <summary>拖船命令(args:id、mode push|pull|standby|free、direction_deg(力方向相對船艏)、power_pct;設計文件 physics-tugs-anchors-mooring.md 第 2 節)</summary>
    TugControl,
    /// <summary>錨(args:side port|starboard、action letGo|veer|heave|brake|release、targetLength_shackles 或 length_m;第 3 節)</summary>
    Anchor,
    /// <summary>繫纜(args:lineId、action makeFast|heave|slack|letGo、bollard、tension_t(預張力/絞纜目標)、length_m(slack 長度);第 4 節)</summary>
    Mooring,
    /// <summary>警報責任轉移(args:id、station bridge|portWing|stbdWing|ecr;MSC.302(87) responsibility transferred;只有 AlertCatalogue.TransferTargets 允許的站)</summary>
    AlertTransfer,
    /// <summary>
    /// 雷達控制操作紀錄(由雷達服務轉送;args:radar x|s、client、cmd、ok、error?、id?、value?、request{原指令欄位}):
    /// 只寫 input 行並發 radarControl 事件,不影響物理;無情境引擎時雜湊不變,有情境引擎時只有事件序號計入雜湊(與 acknowledge 相同;規劃書第 8.3 節第 7 類)
    /// </summary>
    RadarControl,
    /// <summary>
    /// 參與者與休息時間確認(規劃書第 5.5 節;args:participants[]{name, role, rank?, certificateNo?}、restHoursConfirmation{confirmedBy, confirmedAtUtc, compliant, note?}、course?{id, title, instructor}):
    /// 主控程序層指令,不進引擎佇列、不寫 input 行、不進雜湊;寫進紀錄 header(紀錄未開啟時)或追加 session 行,並回 ack
    /// </summary>
    SessionInfo,
    /// <summary>
    /// 優雅停止(主控程序層指令;僅接受本機/直連私有網段的客戶端,例如教官站):結束練習 → 寫紀錄 footer → 程序結束,回 ack 後 ≤ 1 s 內結束;args:reason?
    /// </summary>
    Shutdown,
}

/// <summary>
/// 各站送往核心的指令(command.schema.json)。<c>value</c> 與 <c>args</c> 保留為 JsonElement,
/// 由引擎依 type 解讀;JsonElement 不可變,可安全放入跨執行緒佇列與紀錄檔。
/// </summary>
public sealed record SimCommand
{
    [JsonPropertyName("type")] public required SimCommandType Type { get; init; }
    [JsonPropertyName("value")] public JsonElement? Value { get; init; }
    [JsonPropertyName("args")] public JsonElement? Args { get; init; }

    // ---- 便利建構 ----
    public static SimCommand Rudder(double degrees) => new() { Type = SimCommandType.Rudder, Value = Num(degrees) };
    public static SimCommand Telegraph(TelegraphOrder order) => new() { Type = SimCommandType.Telegraph, Value = Str(order.ToString()) };
    public static SimCommand Rpm(double rpm) => new() { Type = SimCommandType.Rpm, Value = Num(rpm) };
    public static SimCommand Thruster(double fraction) => new() { Type = SimCommandType.Thruster, Value = Num(fraction) };
    public static SimCommand Freeze() => new() { Type = SimCommandType.Freeze };
    public static SimCommand Resume() => new() { Type = SimCommandType.Resume };
    public static SimCommand Reset() => new() { Type = SimCommandType.Reset };
    public static SimCommand TimeScale(double scale) => new() { Type = SimCommandType.TimeScale, Value = Num(scale) };
    public static SimCommand Snapshot() => new() { Type = SimCommandType.Snapshot };
    public static SimCommand Restore() => new() { Type = SimCommandType.Restore };
    public static SimCommand InjectFault(string fault) => new() { Type = SimCommandType.InjectFault, Value = Str(fault) };
    public static SimCommand ClearFault(string fault) => new() { Type = SimCommandType.ClearFault, Value = Str(fault) };
    public static SimCommand LoadScenario(string path) => new() { Type = SimCommandType.LoadScenario, Value = Str(path) };
    /// <summary>以物件形式注入故障(args {name, args};第一版引擎只記錄 args)。</summary>
    public static SimCommand InjectFault(string fault, object? faultArgs)
        => new() { Type = SimCommandType.InjectFault, Args = Obj(new { name = fault, args = faultArgs }) };
    /// <summary>手動觸發時間表項目(force = 略過條件/once 限制)。</summary>
    public static SimCommand TriggerTimeline(string id, bool force = false)
        => new() { Type = SimCommandType.TriggerTimeline, Args = Obj(new { id, force }) };
    /// <summary>確認/通知紀錄:kind 為 verbal|notifyEcr|callMaster|report|checklist|steeringMode|anchorReady|gyroCheck|other。</summary>
    public static SimCommand Acknowledge(string kind, string? itemId = null, string? role = null, string? note = null)
        => new() { Type = SimCommandType.Acknowledge, Args = Obj(new { kind, itemId, role, note }) };
    public static SimCommand EndExercise(string? reason = null)
        => new() { Type = SimCommandType.EndExercise, Args = Obj(new { reason }) };
    public static SimCommand GetAssessment() => new() { Type = SimCommandType.GetAssessment };
    /// <summary>參與者與休息時間確認(<paramref name="args"/> 為匿名物件或字典,鍵名見 RecordSessionInfo.TryParse)。</summary>
    public static SimCommand SessionInfo(object args) => new() { Type = SimCommandType.SessionInfo, Args = Obj(args) };
    /// <summary>優雅停止主控程序(結束練習 → footer → 程序結束)。</summary>
    public static SimCommand Shutdown(string? reason = null) => new() { Type = SimCommandType.Shutdown, Args = reason is null ? null : Obj(new { reason }) };
    /// <summary>
    /// 操舵模式:<paramref name="pump"/> 為 "1"|"2"|"both"(未給時不變);<paramref name="direction"/> 只在 nfu 有效(port|starboard|stop;未給時不變,首次進入 nfu 視為 stop)。
    /// </summary>
    public static SimCommand SteeringMode(SteeringMode mode, string? pump = null, string? direction = null, double? heading = null)
        => new() { Type = SimCommandType.SteeringMode, Value = Str(ContractJson.EnumName(mode)), Args = pump is null && direction is null && heading is null ? null : Obj(new { pump, direction, heading }) };

    /// <summary>警報確認(<paramref name="id"/> 省略 = 全部;<paramref name="station"/> 省略 = bridge)。</summary>
    public static SimCommand AlertAcknowledge(string? id = null, string? station = null)
        => new() { Type = SimCommandType.AlertAcknowledge, Args = id is null && station is null ? null : Obj(new { id, station }) };
    /// <summary>警報責任轉移到 <paramref name="station"/>(bridge|portWing|stbdWing|ecr;轉回原站即解除轉移)。</summary>
    public static SimCommand AlertTransfer(string id, string station)
        => new() { Type = SimCommandType.AlertTransfer, Args = Obj(new { id, station }) };
    /// <summary>雷達控制操作紀錄(雷達服務轉送用;<paramref name="request"/> 為原指令物件去掉 cmd/reqId)。</summary>
    public static SimCommand RadarControl(string radar, string client, string cmd, bool ok, string? error = null, int? id = null, double? value = null, object? request = null)
        => new() { Type = SimCommandType.RadarControl, Args = Obj(new { radar, client, cmd, ok, error, id, value, request }) };
    /// <summary>警報靜音(<paramref name="id"/> 省略 = 全部;<paramref name="station"/> 只決定是否視為駕駛台操作重設 BNWAS,省略 = bridge)。</summary>
    public static SimCommand AlertSilence(string? id = null, string? station = null)
        => new() { Type = SimCommandType.AlertSilence, Args = id is null && station is null ? null : Obj(new { id, station }) };
    /// <summary>BNWAS 模式(off|auto|on;休眠期 180–720 s)。</summary>
    public static SimCommand BnwasMode(Scenario.BnwasMode mode, double? dormantS = null)
        => new() { Type = SimCommandType.BnwasMode, Value = Str(ContractJson.EnumName(mode)), Args = dormantS is null ? null : Obj(new { dormant_s = dormantS }) };
    public static SimCommand BnwasReset() => new() { Type = SimCommandType.BnwasReset };

    /// <summary>拖船命令:<paramref name="mode"/> push|pull|standby|free;<paramref name="directionDeg"/> 力方向(相對船艏,度);<paramref name="powerPct"/> 0–100。</summary>
    public static SimCommand TugControl(string id, string? mode = null, double? directionDeg = null, double? powerPct = null)
        => new() { Type = SimCommandType.TugControl, Args = Obj(new { id, mode, direction_deg = directionDeg, power_pct = powerPct }) };
    /// <summary>錨指令:<paramref name="side"/> port|starboard;<paramref name="action"/> letGo|veer|heave|brake|release;鏈長目標以節或 m 給定。</summary>
    public static SimCommand Anchor(string side, string action, double? targetShackles = null, double? lengthM = null)
        => new() { Type = SimCommandType.Anchor, Args = Obj(new { side, action, targetLength_shackles = targetShackles, length_m = lengthM }) };
    /// <summary>繫纜指令:<paramref name="action"/> makeFast|heave|slack|letGo。</summary>
    public static SimCommand Mooring(string lineId, string action, string? bollard = null, double? tensionT = null, double? lengthM = null)
        => new() { Type = SimCommandType.Mooring, Args = Obj(new { lineId, action, bollard, tension_t = tensionT, length_m = lengthM }) };

    /// <summary>目標船控制:<paramref name="args"/> 為匿名物件或字典,鍵名見 README「targetControl」。</summary>
    public static SimCommand TargetControl(object args) => new() { Type = SimCommandType.TargetControl, Args = Obj(args) };
    /// <summary>目標船航向/航速覆寫(教官手動接管;release 解除)。</summary>
    public static SimCommand TargetOverride(string id, double? headingDeg = null, double? speedKn = null)
        => TargetControl(new { id, heading = headingDeg, speed = speedKn });
    public static SimCommand TargetRelease(string id) => TargetControl(new { id, release = true });
    public static SimCommand TargetRemove(string id) => TargetControl(new { id, remove = true });
    public static SimCommand TargetAis(string id, bool aisOn) => TargetControl(new { id, aisOn });

    public static SimCommand Autopilot(bool enabled, double? heading = null, double? rotLimit = null)
        => new() { Type = SimCommandType.Autopilot, Args = Obj(new { enabled, heading, rotLimit }) };

    /// <summary>環境設定;<paramref name="visibilityNm"/> 與 <paramref name="rampS"/>(漸變秒數,0 = 立即)為情境引擎新增欄位。</summary>
    public static SimCommand SetEnvironment(double? windTrueSpeedKn = null, double? windTrueDirDeg = null,
        double? currentSetDeg = null, double? currentDriftKn = null, double? waterDepth = null,
        double? visibilityNm = null, double? rampS = null)
    {
        object? wind = windTrueSpeedKn is null && windTrueDirDeg is null ? null : new { trueSpeed = windTrueSpeedKn, trueDir = windTrueDirDeg };
        object? current = currentSetDeg is null && currentDriftKn is null ? null : new { set = currentSetDeg, drift = currentDriftKn };
        return new() { Type = SimCommandType.SetEnvironment, Args = Obj(new { wind, current, waterDepth, visibility_nm = visibilityNm, ramp_s = rampS }) };
    }

    /// <summary>injectFault 的故障名稱:value 字串,或 args.name(物件形式)。</summary>
    public string? FaultName()
    {
        if (Value is { ValueKind: JsonValueKind.String } v) return v.GetString();
        return Arg("name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
    }

    public string? ArgAsString(string name) => Arg(name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    // ---- 讀取輔助 ----
    public double? ValueAsDouble() => AsDouble(Value);
    public string? ValueAsString() => Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : Value?.ToString();

    public JsonElement? Arg(string name)
    {
        if (Args is not { ValueKind: JsonValueKind.Object } a) return null;
        return a.TryGetProperty(name, out var e) ? e : null;
    }

    public double? ArgAsDouble(string name) => AsDouble(Arg(name));

    public bool? ArgAsBool(string name) => Arg(name) is { } e
        ? e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;

    public static double? AsDouble(JsonElement? e)
    {
        if (e is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static JsonElement Num(double d) => JsonSerializer.SerializeToElement(d);
    public static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);
    public static JsonElement Obj(object o) => JsonSerializer.SerializeToElement(o, ContractJson.Options);

    public static SimCommand? Parse(string json) => ContractJson.Deserialize<SimCommand>(json);
    public string ToJson() => ContractJson.Serialize(this);
}
