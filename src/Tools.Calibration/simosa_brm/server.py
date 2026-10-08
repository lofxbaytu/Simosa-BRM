"""WebSocket 即時模擬服務(給網頁儀器 / 教官站原型用;規劃書 5.2 Instruments、9.1 執行控制)。

- 以 50 Hz 固定步長積分,每 40 ms(25 Hz)廣播一次 state JSON(state.schema.json)。
- 接收 command JSON(command.schema.json):rudder / telegraph / rpm / thruster / autopilot / freeze / resume /
  reset / setEnvironment / timeScale / snapshot / restore / injectFault / clearFault。
- 每筆命令回 {"type": "ack", "command": ..., "ok": bool, "tick": ...};未支援者回 ok=false。
"""

from __future__ import annotations

import asyncio
import copy
import json
import time
from typing import Any

from .mmg import MMGShip, make_ship

BROADCAST_DT = 0.04


class SimServer:
    def __init__(self, ship: MMGShip, time_scale: float = 1.0) -> None:
        self.ship = ship
        self.time_scale = max(0.0, float(time_scale))
        self.clients: set[Any] = set()
        self.snapshots: dict[str, Any] = {}
        self._accum = 0.0

    # ---- 命令 ----
    def apply_command(self, cmd: dict[str, Any]) -> dict[str, Any]:
        t = cmd.get("type")
        v = cmd.get("value")
        args = cmd.get("args") or {}
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
                ship.reset(x=float(args.get("x", 0.0)), y=float(args.get("y", 0.0)), heading_deg=float(args.get("heading", 0.0)),
                           speed_kn=float(args.get("speed", 0.0)), rpm=args.get("rpm"))
            elif t == "setEnvironment":
                w = args.get("wind") or {}
                c = args.get("current") or {}
                depth = args.get("waterDepth", "keep")
                ship.set_environment(w.get("trueSpeed"), w.get("trueDir"), c.get("set"), c.get("drift"), depth)
            elif t == "timeScale":
                self.time_scale = max(0.0, float(v))
            elif t == "snapshot":
                name = str(v or "default")
                self.snapshots[name] = (copy.deepcopy(ship.state), copy.deepcopy(ship.ctl), ship._eng_mode, ship._eng_timer, ship._n_target, ship._n_tau, ship.aground)
            elif t == "restore":
                name = str(v or "default")
                st, ctl, mode, timer, nt, ntau, ag = self.snapshots[name]
                ship.state, ship.ctl = copy.deepcopy(st), copy.deepcopy(ctl)
                ship._eng_mode, ship._eng_timer, ship._n_target, ship._n_tau, ship.aground = mode, timer, nt, ntau, ag
            elif t == "injectFault":
                ship.faults.append(str(v))
                detail = "僅記錄,故障行為尚未實作"
            elif t == "clearFault":
                ship.faults = [f for f in ship.faults if f != str(v)]
            elif t == "loadScenario":
                ok, detail = False, "情境載入尚未實作"
            else:
                ok, detail = False, f"未知命令 {t!r}"
        except Exception as exc:  # noqa: BLE001 - 回報給客戶端
            ok, detail = False, f"{type(exc).__name__}: {exc}"
        return {"type": "ack", "command": t, "ok": ok, "detail": detail, "tick": ship.state.tick}

    # ---- 迴圈 ----
    async def handler(self, ws: Any) -> None:
        self.clients.add(ws)
        try:
            await ws.send(json.dumps(self.ship.state_json()))
            async for msg in ws:
                try:
                    cmd = json.loads(msg)
                except json.JSONDecodeError:
                    await ws.send(json.dumps({"type": "ack", "ok": False, "detail": "JSON 解析失敗"}))
                    continue
                await ws.send(json.dumps(self.apply_command(cmd), ensure_ascii=False))
        finally:
            self.clients.discard(ws)

    async def loop(self) -> None:
        dt = self.ship.dt
        last = time.perf_counter()
        while True:
            await asyncio.sleep(BROADCAST_DT)
            now = time.perf_counter()
            self._accum += (now - last) * self.time_scale
            last = now
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


async def _main(ship_id: str, loading: str, host: str, port: int, time_scale: float, speed_kn: float, heading_deg: float) -> None:
    from websockets.asyncio.server import serve as ws_serve

    ship = make_ship(ship_id, loading, dt=0.02)
    ship.reset(heading_deg=heading_deg, speed_kn=speed_kn)
    srv = SimServer(ship, time_scale)
    async with ws_serve(srv.handler, host, port):
        print(f"simosa-brm serve: ws://{host}:{port}  船 {ship_id}/{loading}  time_scale={time_scale}")
        await srv.loop()


def serve(ship_id: str, loading: str = "full", host: str = "127.0.0.1", port: int = 8765, time_scale: float = 1.0,
          speed_kn: float = 0.0, heading_deg: float = 0.0) -> None:
    try:
        asyncio.run(_main(ship_id, loading, host, port, time_scale, speed_kn, heading_deg))
    except KeyboardInterrupt:
        pass
