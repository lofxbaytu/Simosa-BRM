"""WebSocket 即時模擬服務(給網頁儀器 / 教官站原型用;規劃書 5.2 Instruments、9.1 執行控制)。

- 以 50 Hz 固定步長積分,每 40 ms(25 Hz)廣播一次 state JSON(state.schema.json)。
- 接收 command JSON(command.schema.json):rudder / telegraph / rpm / thruster / autopilot / freeze / resume /
  reset / setEnvironment / timeScale / snapshot / restore / injectFault / clearFault / loadScenario;各命令語意見
  README「WebSocket 服務命令支援」。
- 每筆命令回 {"type": "ack", "command": ..., "ok": bool, "detail": ..., "tick": ...};未支援或失敗者回 ok=false
  並在 detail 說明,模型狀態不變。
"""

from __future__ import annotations

import asyncio
import json
import time
from typing import Any

from .mmg import FAULT_BOW_THRUSTER, FAULT_MAIN_ENGINE, FAULT_STEERING_GEAR, MMGShip, make_ship
from .scenario import apply_scenario, load_scenario, ship_from_scenario

BROADCAST_DT = 0.04

# 會改變模型行為的故障(src/SimCore/Engine/FaultNames.cs)之 ack 說明;其餘名稱只記錄並廣播
FAULT_DETAIL = {
    FAULT_STEERING_GEAR: "已套用:舵機故障,舵角停在目前位置(舵令照記但不作用)",
    FAULT_MAIN_ENGINE: "已套用:主機故障,轉速衰減至 0,engine.state = failed",
    FAULT_BOW_THRUSTER: "已套用:艏側推不可用,側推令視為 0",
}
RECORD_ONLY_DETAIL = "僅記錄並廣播(核心不改變行為,由儀器端自行反應)"


class SimServer:
    def __init__(self, ship: MMGShip, time_scale: float = 1.0, scenario: dict[str, Any] | None = None) -> None:
        self.ship = ship
        self.time_scale = max(0.0, float(time_scale))
        self.scenario: dict[str, Any] | None = scenario  # 目前載入的情境(正規化 dict;state JSON 無情境欄位,只供 ack/reset)
        self.clients: set[Any] = set()
        self.snapshots: dict[str, dict[str, Any]] = {}
        self._accum = 0.0

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
        return sc

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
                ship.set_autopilot(bool(args.get("enabled", True)), args.get("heading"), args.get("rotLimit"), args.get("rudderLimit"))
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
                name = str(v).strip() if v is not None else ""
                if not name:
                    ok, detail = False, "故障名稱不可為空"
                else:
                    modelled = ship.inject_fault(name)
                    detail = FAULT_DETAIL[name] if modelled else RECORD_ONLY_DETAIL
            elif t == "clearFault":
                name = str(v).strip() if v is not None else ""
                if name:
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
                        detail = (f"已載入情境 {sc['id']}" + (f"({sc['name']})" if sc.get("name") else "")
                                  + f":{sc['ship']['id']}/{sc['ship']['loading']},時間倍率 {self.time_scale:g}")
            else:
                ok, detail = False, f"未知命令 {t!r}"
        except Exception as exc:  # noqa: BLE001 - 回報給客戶端
            ok, detail = False, f"{type(exc).__name__}: {exc}"
        return {"type": "ack", "command": t, "ok": ok, "detail": detail, "tick": self.ship.state.tick}

    # ---- 迴圈 ----
    async def handler(self, ws: Any) -> None:
        from websockets.exceptions import ConnectionClosed

        self.clients.add(ws)
        try:
            await ws.send(json.dumps(self.ship.state_json()))
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
                msg = json.dumps(self.ship.state_json())
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
