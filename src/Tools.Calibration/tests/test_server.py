"""WebSocket 服務:連線即收到 state JSON;送 command JSON 收到 ack 且狀態反映命令。"""

from __future__ import annotations

import asyncio
import json
import socket

import pytest

from simosa_brm.mmg import make_ship
from simosa_brm.server import SimServer


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def test_apply_commands_directly() -> None:
    ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    srv = SimServer(ship, 1.0)
    assert srv.apply_command({"type": "rudder", "value": -20})["ok"]
    assert ship.state_json()["rudderOrder"] == -20.0
    assert srv.apply_command({"type": "telegraph", "value": "HAH"})["ok"]
    assert srv.apply_command({"type": "autopilot", "args": {"enabled": True, "heading": 275, "rotLimit": 15}})["ok"]
    assert srv.apply_command({"type": "setEnvironment", "args": {"wind": {"trueSpeed": 20, "trueDir": 40}, "current": {"set": 265, "drift": 2.5}, "waterDepth": 12}})["ok"]
    s = ship.state_json()
    assert s["wind"]["trueSpeed"] == pytest.approx(20.0) and s["current"]["set"] == pytest.approx(265.0) and s["waterDepth"] == 12.0
    assert srv.apply_command({"type": "snapshot"})["ok"]
    assert srv.apply_command({"type": "freeze"})["ok"] and ship.ctl.frozen
    assert srv.apply_command({"type": "resume"})["ok"] and not ship.ctl.frozen
    assert srv.apply_command({"type": "timeScale", "value": 4})["ok"] and srv.time_scale == 4.0
    assert srv.apply_command({"type": "restore"})["ok"]
    assert not srv.apply_command({"type": "loadScenario", "value": "x"})["ok"]
    assert not srv.apply_command({"type": "telegraph", "value": "BOGUS"})["ok"]


def test_websocket_roundtrip() -> None:
    from websockets.asyncio.client import connect
    from websockets.asyncio.server import serve

    port = _free_port()

    async def run() -> None:
        ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
        ship.reset(speed_kn=8.0)
        srv = SimServer(ship, time_scale=10.0)
        async with serve(srv.handler, "127.0.0.1", port):
            loop_task = asyncio.create_task(srv.loop())
            try:
                async with connect(f"ws://127.0.0.1:{port}") as ws:
                    first = json.loads(await asyncio.wait_for(ws.recv(), 5))
                    assert first["shipId"] == "FSB1" and "heading" in first
                    await ws.send(json.dumps({"type": "rudder", "value": 15}))
                    ack = None
                    for _ in range(50):
                        msg = json.loads(await asyncio.wait_for(ws.recv(), 5))
                        if msg.get("type") == "ack":
                            ack = msg
                            break
                    assert ack and ack["ok"] and ack["command"] == "rudder"
                    last = None
                    for _ in range(30):
                        msg = json.loads(await asyncio.wait_for(ws.recv(), 5))
                        if "tick" in msg and msg.get("type") != "ack":
                            last = msg
                    assert last is not None and last["tick"] > first["tick"] and last["rudderOrder"] == 15.0
            finally:
                loop_task.cancel()

    asyncio.run(run())
