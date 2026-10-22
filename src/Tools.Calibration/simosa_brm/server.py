"""WebSocket 即時模擬服務(給網頁儀器 / 教官站原型用;規劃書 5.2 Instruments、9.1 執行控制)。

- 以 50 Hz 固定步長積分,每 40 ms(25 Hz)廣播一次 state JSON(state.schema.json)。
- 接收 command JSON(command.schema.json):rudder / telegraph / rpm / thruster / autopilot / freeze / resume /
  reset / setEnvironment / timeScale / snapshot / restore / injectFault / clearFault / loadScenario;各命令語意見
  README「WebSocket 服務命令支援」。
- 每筆命令回 {"type": "ack", "command": ..., "ok": bool, "detail": ..., "tick": ...};未支援或失敗者回 ok=false
  並在 detail 說明,模型狀態不變。情境引擎指令 triggerTimeline / acknowledge / getAssessment / endExercise 固定回 ok=false
  (Python 參考實作不模擬時間表與評估;情境引擎設計第 2.3 節);警報/BNWAS 指令 alertAcknowledge / alertSilence / bnwasMode /
  bnwasReset 亦固定回 ok=false(不實作感測器層、BAM 與 BNWAS,不輸出 state.truth/sensors/alerts/bnwas;sensors-alerts.md 第 9 節);
  targetControl 固定回 ok=false(Python 無目標船)。
- steeringMode(fu / nfu / autopilot / emergency、pump、nfu direction;D32、D35)語意與 C# 相同,state 輸出 steering;
  emergency 中 autopilot 啟用/切換被拒(ok=false,detail 為拒絕原因 emergencySteering,狀態不變);autopilotFailure /
  gyroFailure 時同樣被拒(拒絕原因 autopilotUnavailable),自動舵啟用中注入這兩種故障則跳脫回 fu(C# TripAutopilot)。
- autopilot 指令的 enabled 只認布林;缺省時只更新航向 / rotLimit / rudderLimit、不改啟用狀態與操舵模式(與 C# 相同);
  rotLimit 兩條路徑都取 max(1, |值|) 度/分。
- 已載入情境時 state JSON 加 scenario {id, phase: "normal", parameterSetVersion?}(state.schema.json 的 scenario 物件;
  Python 不推進階段,phase 固定 normal)。
- 外力指令 tugControl {id, mode, direction_deg, power_pct} / anchor {side, action, targetLength_shackles | length_m} /
  mooring {lineId, action, bollard, tension_t, length_m}(command.schema.json;physics-tugs-anchors-mooring.md 第 6 節):
  語意與 C# ExternalForceModel 相同,拒絕時 ok=false、detail 為「拒絕(原因):…」,原因字串同 C# commandRejected
  (unknownTug、unknownTugMode、invalidPower、tugModeUnavailable、tugLineParted、noAnchorData、unknownAnchor、unknownAction、
  invalidTarget、anchorAlreadyOut、anchorStowed、chainFullyOut、noBerth、unknownMooringLine、unknownBollard、bollardOutOfReach、
  mooringLineNotFast、mooringLineParted);接受時 ack 附 event(tugCommand / anchorLetGo / anchorCommand / mooringCommand)。
  injectFault tugLineBreak 的 args.args.id 指定拖船(不存在 → 拒絕 unknownTug;故障參數只讀 args.args 物件,與 C# FaultArgString
  相同);clearFault 的名稱為 value 或 args.name(C# SimCommand.FaultName),兩者都缺或為 all 時清除全部;清除 tugLineBreak
  後斷纜拖船可重新帶纜。
  state 輸出 tugs[] / anchors[] / berth / bank(external.py;與 C# BuildState 相同的輸出條件)。
"""

from __future__ import annotations

import asyncio
import json
import math
import time
from typing import Any

from .mmg import (
    AUTOPILOT_BLOCKING_FAULTS,
    FAULT_BOW_THRUSTER,
    FAULT_MAIN_ENGINE,
    FAULT_STEERING_FU_ONLY,
    FAULT_STEERING_GEAR,
    FAULT_STEERING_PUMP,
    FAULT_TUG_LINE_BREAK,
    CommandRejected,
    MMGShip,
    make_ship,
    normalize_pump,
)
from .scenario import apply_scenario, load_scenario, ship_from_scenario

BROADCAST_DT = 0.04

# 會改變模型行為的故障(src/SimCore/Engine/FaultNames.cs)之 ack 說明;其餘名稱只記錄並廣播
FAULT_DETAIL = {
    FAULT_STEERING_GEAR: "已套用:舵機故障,舵角停在目前位置(舵令照記但不作用;steeringMode emergency 時以應急速率可動)",
    FAULT_STEERING_FU_ONLY: "已套用:FU 失效,fu 與自動舵動不了舵(nfu、emergency 可動)",
    FAULT_STEERING_PUMP: "已套用:舵機泵失效,所有操舵模式舵機速率減半",
    FAULT_MAIN_ENGINE: "已套用:主機故障,轉速衰減至 0,engine.state = failed",
    FAULT_BOW_THRUSTER: "已套用:艏側推不可用,側推令視為 0",
}
RECORD_ONLY_DETAIL = "僅記錄並廣播(核心不改變行為,由儀器端自行反應)"
# autopilotFailure / gyroFailure:Python 只模擬「自動舵不可用」(啟用被拒、啟用中跳脫),感測器層(電羅經指示值)不模擬
AUTOPILOT_FAULT_DETAIL = ("部分套用:自動舵不可用(autopilot {enabled: true} 與 steeringMode autopilot 拒絕 autopilotUnavailable);"
                          "電羅經指示值等感測器層 Python 不模擬,僅記錄並廣播")
# 情境引擎指令(command.schema.json):Python 參考實作只做情境的參數解析與結構檢查,不模擬時間表/評估
UNIMPLEMENTED_DETAIL = {
    "triggerTimeline": "未實作:Python 參考實作不執行時間表(情境引擎由 C# SimCore 執行)",
    "acknowledge": "未實作:Python 參考實作不記錄確認事件、不評估必要事件",
    "getAssessment": "未實作:Python 參考實作不計算評估指標與分數",
    "endExercise": "未實作:Python 參考實作無情境結束/講評摘要",
    # 警報管理(MSC.302(87))與 BNWAS(MSC.128(75)):docs/design/sensors-alerts.md 第 3.3、4 節;由 C# SimCore 執行
    "alertAcknowledge": "未實作:Python 參考實作無警報管理(BAM),不輸出 state.alerts",
    "alertSilence": "未實作:Python 參考實作無警報管理(BAM),不輸出 state.alerts",
    "bnwasMode": "未實作:Python 參考實作無 BNWAS(情境 bnwas 設定只保留、不模擬)",
    "bnwasReset": "未實作:Python 參考實作無 BNWAS(情境 bnwas 設定只保留、不模擬)",
    # 目標船(targets、AIS、COLREG 行為)由 C# SimCore 執行;Python 只模擬自船(情境 targets 不載入)
    "targetControl": "未實作:Python 參考實作無目標船(只模擬自船;情境 targets、AIS 與 COLREG 行為由 C# SimCore 執行),模型不變",
}
STEERING_MODE_LABEL = {"fu": "隨動(FU)", "nfu": "非隨動(NFU)", "autopilot": "自動舵", "emergency": "應急操舵"}
NFU_LABEL = {-1: "推左舵", 0: "停", 1: "推右舵"}
REJECT_DETAIL = "拒絕({reason}):{msg};狀態不變"


def _arg_str(args: dict[str, Any], name: str) -> str | None:
    """C# SimCommand.ArgAsString:只認 JSON 字串。"""
    v = args.get(name)
    return v if isinstance(v, str) else None


def _arg_num(args: dict[str, Any], *names: str) -> float | None:
    """C# SimCommand.ArgAsDouble:數值或數值字串(布林不算);多個名稱依序取第一個有值者。"""
    for name in names:
        v = args.get(name)
        if isinstance(v, bool) or v is None:
            continue
        if isinstance(v, (int, float)):
            return float(v)
        if isinstance(v, str):
            try:
                return float(v.strip())
            except ValueError:
                continue
    return None


def _value_str(v: Any) -> str | None:
    """C# SimCommand.ValueAsString:字串原樣,其他非 null 值取文字。"""
    if v is None:
        return None
    if isinstance(v, str):
        return v
    return json.dumps(v)


class SimServer:
    def __init__(self, ship: MMGShip, time_scale: float = 1.0, scenario: dict[str, Any] | None = None) -> None:
        self.ship = ship
        self.time_scale = max(0.0, float(time_scale))
        self.scenario: dict[str, Any] | None = scenario  # 目前載入的情境(正規化 dict;供 ack/reset 與 state.scenario)
        self.clients: set[Any] = set()
        self.snapshots: dict[str, dict[str, Any]] = {}
        self._accum = 0.0
        # state.utc 起點:情境 startTimeUtc,未給時為現在(與 C# 相同規則);寫回情境 dict 使之後的 ack/重設沿用同一起點
        self._apply_start_utc()

    def _apply_start_utc(self) -> None:
        """練習開始 UTC(MMGShip.set_start_utc):情境 startTimeUtc 可解析者採用,否則現在時刻並寫回情境(C# ResolveStartUtc 相同)。"""
        start_iso = self.scenario.get("startTimeUtc") if self.scenario is not None else None
        self.ship.set_start_utc(start_iso if isinstance(start_iso, str) else None)
        if self.scenario is not None and not isinstance(start_iso, str):
            self.scenario["startTimeUtc"] = self.ship.utc_at(0.0)

    # ---- 情境 ----
    def load_scenario(self, ref: str) -> dict[str, Any]:
        """載入情境(代號或路徑):依 ship.id/loading 建新模型、套初始狀態/環境/種子與時間倍率;tick 歸零、快照清空。
        任何錯誤都在換掉模型前拋出,原模型不受影響。"""
        sc = load_scenario(ref)
        ship = ship_from_scenario(sc, dt=self.ship.dt)
        self.ship = ship
        self.scenario = sc
        self.time_scale = float(sc["timeScale"])
        self.snapshots.clear()
        self._accum = 0.0
        self._apply_start_utc()
        return sc

    def state_json(self) -> dict[str, Any]:
        """廣播用狀態:自船狀態(MMGShip.state_json)加 scenario 物件(已載入情境時;state.schema.json)。"""
        s = self.ship.state_json()
        if self.scenario is not None:
            sc: dict[str, Any] = {"id": self.scenario["id"], "phase": "normal"}
            ps = self.scenario.get("parameterSet")
            if ps and ps.get("version"):
                sc["parameterSetVersion"] = ps["version"]
            s["scenario"] = sc
        return s

    # ---- 命令 ----
    def apply_command(self, cmd: dict[str, Any]) -> dict[str, Any]:
        t = cmd.get("type")
        v = cmd.get("value")
        args = cmd.get("args") or {}
        if not isinstance(args, dict):
            args = {}
        ship = self.ship
        ok = True
        detail = ""
        event: dict[str, Any] | None = None
        try:
            if t == "rudder":
                ship.set_rudder(float(v))
            elif t == "telegraph":
                ship.set_telegraph(str(v))
            elif t == "rpm":
                ship.set_rpm(float(v))
            elif t == "thruster":
                ship.set_thruster(float(v))
            elif t == "autopilot":
                # enabled 只認 JSON 布林(C# ArgAsBool);缺省或非布林時只更新設定、不改啟用狀態與操舵模式(與 C# 相同)
                enabled = args.get("enabled")
                if not isinstance(enabled, bool):
                    if "enabled" in args:
                        detail = f"enabled {enabled!r} 非布林,視同未指明;"
                    enabled = None
                ship.set_autopilot(enabled, args.get("heading"), args.get("rotLimit"), args.get("rudderLimit"))
                if enabled is None:
                    detail += ("只更新自動舵設定(未指明 enabled,啟用狀態不變:"
                               + ("自動舵啟用中" if ship.ctl.autopilot else f"未啟用,操舵模式維持 {ship.ctl.steering_mode}") + ")")
            elif t == "steeringMode":
                mode = v if v is not None else args.get("mode")
                info = ship.set_steering_mode(mode, args.get("pump"), args.get("direction"), args.get("heading"), args.get("rotLimit"))
                c = ship.ctl
                detail = (f"操舵模式 {info['from']} → {info['to']}({STEERING_MODE_LABEL[info['to']]})" if info["from"] != info["to"]
                          else f"操舵模式維持 {info['to']}") + f";泵 {c.steering_pump}"
                if info["pumpFrom"] != c.steering_pump:
                    detail += f"(由 {info['pumpFrom']} 改)"
                if args.get("pump") is not None and normalize_pump(args.get("pump")) is None:
                    detail += f";泵 {args.get('pump')!r} 不明,未變更"
                if c.steering_mode == "nfu":
                    detail += f";NFU {NFU_LABEL[c.nfu_direction]}"
                if c.steering_mode == "autopilot":
                    detail += f";航向 {math.degrees(c.autopilot_heading) % 360.0:.1f}°"
            elif t == "freeze":
                ship.ctl.frozen = True
            elif t == "resume":
                ship.ctl.frozen = False
            elif t == "reset":
                if not args and self.scenario is not None:
                    # 無參數且已載入情境:回到情境初始狀態(C# Reset 語意)
                    apply_scenario(ship, self.scenario)
                    detail = f"回到情境 {self.scenario['id']} 的初始狀態"
                else:
                    x, y = args.get("x"), args.get("y")
                    if (x is None or y is None) and args.get("lat") is not None and args.get("lon") is not None:
                        x, y = ship.ltp.to_local(float(args["lat"]), float(args["lon"]))
                    ship.reset(x=float(x or 0.0), y=float(y or 0.0), heading_deg=float(args.get("heading", 0.0)),
                               speed_kn=float(args.get("speed", 0.0)), rpm=args.get("rpm"), telegraph=args.get("telegraph"))
                    ignored = [k for k in ("loading", "tugs") if k in args]
                    if ignored:
                        detail = f"忽略未實作欄位 {', '.join(ignored)}"
            elif t == "setEnvironment":
                w = args.get("wind") or {}
                c = args.get("current") or {}
                depth = args.get("waterDepth", "keep")
                if depth != "keep" and depth is not None and float(depth) <= 0.0:
                    depth = "keep"  # C# ApplyEnvironment:非正水深忽略
                vis = args.get("visibility_nm", args.get("visibility", "keep"))
                ship.set_environment(w.get("trueSpeed"), w.get("trueDir"), c.get("set"), c.get("drift"), depth,
                                     gustiness=w.get("gustiness"), visibility_nm=vis)
                notes = []
                if w.get("gustiness") is not None:
                    notes.append(f"陣風 {ship.env.gustiness:.2f}(每秒以種子亂數更新風速係數 1±gustiness)")
                if vis != "keep":
                    notes.append("能見度 " + (f"{ship.env.visibility_nm:g} nm" if ship.env.visibility_nm is not None else "未指定")
                                 + " 已記錄(state.schema.json 無此欄位、運動模型不使用)")
                detail = ";".join(notes)
            elif t == "timeScale":
                self.time_scale = max(0.0, float(v))
            elif t == "snapshot":
                name = str(v or "default")
                self.snapshots[name] = ship.snapshot()
                detail = f"快照 {name}(tick {ship.state.tick})"
            elif t == "restore":
                name = str(v or "default")
                if name not in self.snapshots:
                    ok, detail = False, f"無快照 {name!r}"
                else:
                    ship.restore(self.snapshots[name])
                    detail = f"已還原快照 {name}"
            elif t == "injectFault":
                if v is None and isinstance(args.get("name"), str):
                    v = args["name"]  # C# SimCommand.FaultName:value 或 args.name
                name = str(v).strip() if v is not None else ""
                if not name:
                    ok, detail = False, "故障名稱不可為空"
                else:
                    was_autopilot = ship.ctl.autopilot
                    # 故障參數只讀 args.args 物件(C# FaultArgString);args.id 等非正規寫法視為未指定
                    fargs = args.get("args") if isinstance(args.get("args"), dict) else None
                    n_events = len(ship.external_events)
                    modelled = ship.inject_fault(name, fargs)
                    if modelled:
                        detail = FAULT_DETAIL[name]
                    elif name in AUTOPILOT_BLOCKING_FAULTS:
                        detail = AUTOPILOT_FAULT_DETAIL
                    elif name == FAULT_TUG_LINE_BREAK:
                        parted = list(ship.external_events)[n_events:]
                        detail = ("已套用:" + parted[0]["message"]) if parted else "已記錄:沒有可斷纜的拖船(拉中/待命中)"
                        if parted:
                            event = parted[0]
                    else:
                        detail = RECORD_ONLY_DETAIL
                    if was_autopilot and not ship.ctl.autopilot:
                        detail += ";自動舵跳脫:舵停在目前舵角,操舵模式回 fu"
            elif t == "clearFault":
                if v is None and isinstance(args.get("name"), str):
                    v = args["name"]  # 與 injectFault 相同:value 或 args.name(C# SimCommand.FaultName)
                name = str(v).strip() if v is not None else ""
                if name and name.lower() != "all":
                    n = ship.clear_fault(name)
                    detail = f"已清除 {name}" if n else f"{name} 不在故障清單中"
                else:
                    n = ship.clear_fault(None)
                    detail = f"已清除全部故障({n} 項)"
            elif t == "loadScenario":
                refs: list[str] = []
                for cand in (v, args.get("path"), args.get("id")):
                    if isinstance(cand, str) and cand.strip() and cand.strip() not in refs:
                        refs.append(cand.strip())
                if not refs:
                    ok, detail = False, "缺少情境代號或路徑(value 或 args.path / args.id)"
                else:
                    sc = None
                    errors: list[str] = []
                    for ref in refs:
                        try:
                            sc = self.load_scenario(ref)
                            break
                        except FileNotFoundError as exc:
                            errors.append(str(exc))
                    if sc is None:
                        ok, detail = False, ";".join(errors)
                    else:
                        ps = sc.get("parameterSet")
                        detail = (f"已載入情境 {sc['id']}" + (f"({sc['name']})" if sc.get("name") else "")
                                  + f":{sc['ship']['id']}/{sc['ship']['loading']},時間倍率 {self.time_scale:g}"
                                  + (f";參數集 {ps['id']} {ps['version']}({len(ps['resolved'])} 處引用)" if ps else ""))
            elif t == "tugControl":
                tid = _arg_str(args, "id") or _value_str(v)
                event = ship.tug_control(tid, _arg_str(args, "mode"), _arg_num(args, "direction_deg", "direction"),
                                         _arg_num(args, "power_pct", "power"))
                detail = event["message"]
            elif t == "anchor":
                side = _arg_str(args, "side") or _value_str(v)
                event = ship.anchor_command(side, _arg_str(args, "action"), _arg_num(args, "targetLength_shackles"), _arg_num(args, "length_m"))
                detail = event["message"]
            elif t == "mooring":
                lid = _arg_str(args, "lineId") or _arg_str(args, "id")
                event = ship.mooring_command(lid, _arg_str(args, "action"), _arg_str(args, "bollard"), _arg_num(args, "tension_t"),
                                             _arg_num(args, "length_m"))
                detail = event["message"]
            elif t in UNIMPLEMENTED_DETAIL:
                ok, detail = False, UNIMPLEMENTED_DETAIL[t]
            else:
                ok, detail = False, f"未知命令 {t!r}"
        except CommandRejected as exc:  # 指令被拒絕、狀態不變(C# commandRejected)
            ok, detail = False, REJECT_DETAIL.format(reason=exc.reason, msg=exc)
        except Exception as exc:  # noqa: BLE001 - 回報給客戶端
            ok, detail = False, f"{type(exc).__name__}: {exc}"
        ack = {"type": "ack", "command": t, "ok": ok, "detail": detail, "tick": self.ship.state.tick}
        if ok and event is not None:
            ack["event"] = event  # 外力指令接受時的事件(C# tugCommand / anchorLetGo / anchorCommand / mooringCommand / tugLineParted)
        return ack

    # ---- 迴圈 ----
    async def handler(self, ws: Any) -> None:
        from websockets.exceptions import ConnectionClosed

        self.clients.add(ws)
        try:
            await ws.send(json.dumps(self.state_json()))
            async for msg in ws:
                try:
                    cmd = json.loads(msg)
                except json.JSONDecodeError:
                    await ws.send(json.dumps({"type": "ack", "ok": False, "detail": "JSON 解析失敗"}, ensure_ascii=False))
                    continue
                if not isinstance(cmd, dict):
                    await ws.send(json.dumps({"type": "ack", "ok": False, "detail": "命令必須為 JSON 物件"}, ensure_ascii=False))
                    continue
                await ws.send(json.dumps(self.apply_command(cmd), ensure_ascii=False))
        except ConnectionClosed:
            pass  # 客戶端斷線屬正常
        finally:
            self.clients.discard(ws)

    async def loop(self) -> None:
        last = time.perf_counter()
        while True:
            await asyncio.sleep(BROADCAST_DT)
            now = time.perf_counter()
            self._accum += (now - last) * self.time_scale
            last = now
            dt = self.ship.dt
            steps = int(self._accum / dt)
            if steps > 0:
                steps = min(steps, int(50 * max(self.time_scale, 1.0)))  # 避免落後太多時一次補太多步
                for _ in range(steps):
                    self.ship.step()
                self._accum -= steps * dt
            if self.clients:
                msg = json.dumps(self.state_json())
                dead = []
                for c in list(self.clients):
                    try:
                        await c.send(msg)
                    except Exception:  # noqa: BLE001
                        dead.append(c)
                for c in dead:
                    self.clients.discard(c)


async def _main(ship_id: str, loading: str, host: str, port: int, time_scale: float | None, speed_kn: float, heading_deg: float,
                scenario: str | None = None) -> None:
    from websockets.asyncio.server import serve as ws_serve

    if scenario:
        sc = load_scenario(scenario)
        ship = ship_from_scenario(sc, dt=0.02)
        srv = SimServer(ship, sc["timeScale"] if time_scale is None else time_scale, sc)
        print(f"已載入情境 {sc['id']}:{sc['ship']['id']}/{sc['ship']['loading']}(--ship/--loading 由情境決定)")
    else:
        ship = make_ship(ship_id, loading, dt=0.02)
        ship.reset(heading_deg=heading_deg, speed_kn=speed_kn)
        srv = SimServer(ship, 1.0 if time_scale is None else time_scale)
    async with ws_serve(srv.handler, host, port):
        print(f"simosa-brm serve: ws://{host}:{port}  船 {srv.ship.sp.ship_id}/{srv.ship.sp.loading.name}  time_scale={srv.time_scale:g}")
        await srv.loop()


def serve(ship_id: str, loading: str = "full", host: str = "127.0.0.1", port: int = 8765, time_scale: float | None = None,
          speed_kn: float = 0.0, heading_deg: float = 0.0, scenario: str | None = None) -> None:
    try:
        asyncio.run(_main(ship_id, loading, host, port, time_scale, speed_kn, heading_deg, scenario))
    except KeyboardInterrupt:
        pass
