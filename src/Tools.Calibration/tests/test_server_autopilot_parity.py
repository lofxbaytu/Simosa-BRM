"""autopilot / steeringMode autopilot 與 C# SimulationEngine 的語意對照(審查發現的回歸測試)。

C# 規則(src/SimCore/Engine/SimulationEngine.cs SimCommandType.Autopilot / SteeringMode):
- autopilot 指令的 ``enabled`` 只認 JSON 布林;缺省(或非布林)時只更新航向/rotLimit 等設定、不改啟用狀態與操舵模式。
- rotLimit 兩條路徑(autopilot 與 steeringMode autopilot)都取 max(1, |值|) 度/分。
- autopilotFailure 或 gyroFailure 在 faults 中時,steeringMode autopilot 與 autopilot {enabled: true} 拒絕
  (commandRejected autopilotUnavailable,狀態不變);emergency 的拒絕(emergencySteering)優先。
  自動舵啟用中注入這兩種故障時自動舵跳脫:舵停在原處、模式回 fu(TripAutopilot)。
"""

from __future__ import annotations

import math

import pytest

from simosa_brm.mmg import (
    FAULT_AUTOPILOT,
    FAULT_GYRO,
    REJECT_AUTOPILOT_UNAVAILABLE,
    CommandRejected,
    make_ship,
)
from simosa_brm.server import SimServer

DT = 0.02
RAD2DEG = 180.0 / math.pi


def _server(speed_kn: float = 8.0) -> SimServer:
    ship = make_ship("FSB2", "ballast", dt=DT)
    ship.reset(speed_kn=speed_kn)
    return SimServer(ship, 1.0)


def _cmd(srv: SimServer, type_: str, value=None, **args) -> dict:
    c: dict = {"type": type_}
    if value is not None:
        c["value"] = value
    if args:
        c["args"] = args
    return srv.apply_command(c)


def _heading(srv: SimServer) -> float:
    return srv.state_json()["heading"]


# ---- 發現 1:enabled 缺省時不改啟用狀態 ----

def test_autopilot_without_enabled_keeps_nfu() -> None:
    """FSB2 壓載 8 kn:t=5 s nfu 右推、t=8 s autopilot {heading: 20}(不帶 enabled)→ 仍為 nfu、右滿舵。"""
    srv = _server()
    ship = srv.ship
    ship.run(5.0)
    assert _cmd(srv, "steeringMode", "nfu", direction="starboard")["ok"]
    ship.run(3.0)
    ack = _cmd(srv, "autopilot", heading=20)
    assert ack["ok"]
    assert ship.ctl.steering_mode == "nfu" and ship.ctl.nfu_direction == 1 and ship.ctl.autopilot is False
    assert ship.ctl.autopilot_heading * RAD2DEG == pytest.approx(20.0)  # 設定照樣更新
    ship.run(30.0)
    s = srv.state_json()
    assert s["steering"]["mode"] == "nfu" and s["steering"]["nfuActive"] is True
    assert s["rudderOrder"] == pytest.approx(ship.delta_max * RAD2DEG)
    assert s["rudder"] == pytest.approx(ship.delta_max * RAD2DEG, abs=1e-6)


def test_autopilot_without_enabled_in_fu_only_updates_settings() -> None:
    srv = _server()
    ship = srv.ship
    ack = _cmd(srv, "autopilot", heading=20, rotLimit=12)
    assert ack["ok"] and ship.ctl.autopilot is False and ship.ctl.steering_mode == "fu"
    assert ship.ctl.autopilot_heading * RAD2DEG == pytest.approx(20.0)
    assert ship.ctl.autopilot_rot_limit * RAD2DEG * 60.0 == pytest.approx(12.0)
    # 非布林的 enabled(C# ArgAsBool 只認 JSON true/false)視同缺省
    for bad in ("true", 1):
        ack = srv.apply_command({"type": "autopilot", "args": {"enabled": bad, "heading": 30}})
        assert ack["ok"] and ship.ctl.autopilot is False and ship.ctl.steering_mode == "fu"
    # 已在自動舵時省略 enabled:維持啟用、只改航向
    assert _cmd(srv, "autopilot", enabled=True, heading=10)["ok"]
    assert _cmd(srv, "autopilot", heading=40)["ok"]
    assert ship.ctl.autopilot is True and ship.ctl.steering_mode == "autopilot"
    assert ship.ctl.autopilot_heading * RAD2DEG == pytest.approx(40.0)


# ---- 發現 2:rotLimit 取 max(1, |值|) ----

def test_autopilot_rot_limit_clamped_like_steering_mode() -> None:
    ship = make_ship("FSB2", "ballast", dt=DT)
    ship.set_autopilot(True, 0.0, -10.0)
    assert ship.ctl.autopilot_rot_limit * RAD2DEG * 60.0 == pytest.approx(10.0)
    ship.set_autopilot(None, None, 0.2)
    assert ship.ctl.autopilot_rot_limit * RAD2DEG * 60.0 == pytest.approx(1.0)


def test_negative_rot_limit_paths_are_equivalent() -> None:
    """autopilot {enabled, heading 0, rotLimit −10} 與 steeringMode autopilot {heading 0, rotLimit −10} 的軌跡位元一致。"""
    hashes = []
    for kind in ("autopilot", "steeringMode"):
        srv = _server()
        srv.ship.run(5.0)
        if kind == "autopilot":
            ack = _cmd(srv, "autopilot", enabled=True, heading=0, rotLimit=-10)
        else:
            ack = _cmd(srv, "steeringMode", "autopilot", heading=0, rotLimit=-10)
        assert ack["ok"]
        srv.ship.run(120.0)
        h = _heading(srv)
        assert min(h, 360.0 - h) < 1.0  # 不再穩定偏離設定航向 ~3°
        hashes.append(srv.ship.state_hash())
    assert hashes[0] == hashes[1]


# ---- 發現 3:autopilotFailure / gyroFailure 下拒絕啟用 ----

@pytest.mark.parametrize("fault", [FAULT_AUTOPILOT, FAULT_GYRO])
def test_autopilot_unavailable_rejects_enable(fault: str) -> None:
    srv = _server()
    ship = srv.ship
    assert _cmd(srv, "injectFault", fault)["ok"]
    h0 = ship.state_hash()
    heading0 = ship.ctl.autopilot_heading
    for cmd in ({"type": "steeringMode", "value": "autopilot", "args": {"heading": 30}},
                {"type": "autopilot", "args": {"enabled": True, "heading": 30}}):
        ack = srv.apply_command(cmd)
        assert ack["ok"] is False and REJECT_AUTOPILOT_UNAVAILABLE in ack["detail"]
        assert ship.ctl.steering_mode == "fu" and ship.ctl.autopilot is False
        assert ship.ctl.autopilot_heading == heading0 and ship.state_hash() == h0
        assert srv.state_json()["steering"]["mode"] == "fu"
    with pytest.raises(CommandRejected):
        ship.set_steering_mode("autopilot")
    with pytest.raises(CommandRejected):
        ship.set_autopilot(True)
    # 未啟用的設定更新與解除照常
    assert _cmd(srv, "autopilot", heading=45)["ok"] and ship.ctl.autopilot is False
    assert _cmd(srv, "autopilot", enabled=False)["ok"]
    # 其他模式不受影響
    assert _cmd(srv, "steeringMode", "nfu")["ok"] and ship.ctl.steering_mode == "nfu"
    # 清除故障後可啟用
    assert _cmd(srv, "clearFault", fault)["ok"]
    assert _cmd(srv, "steeringMode", "autopilot", heading=30)["ok"] and ship.ctl.autopilot


def test_autopilot_failure_scenario_holds_heading_fu() -> None:
    """FSB2 壓載 8 kn:autopilotFailure 後 steeringMode autopilot {heading: 30} 被拒,120 s 後航向遠離 30°(仍 fu、舵 0)。"""
    srv = _server()
    ship = srv.ship
    ship.run(5.0)
    _cmd(srv, "injectFault", FAULT_AUTOPILOT)
    ack = _cmd(srv, "steeringMode", "autopilot", heading=30)
    assert ack["ok"] is False
    ship.run(115.0)
    assert srv.state_json()["steering"]["mode"] == "fu"
    assert abs(_heading(srv) - 30.0) > 20.0


def test_emergency_rejection_takes_precedence() -> None:
    srv = _server()
    _cmd(srv, "steeringMode", "emergency")
    _cmd(srv, "injectFault", FAULT_AUTOPILOT)
    ack = _cmd(srv, "steeringMode", "autopilot")
    assert ack["ok"] is False and "emergencySteering" in ack["detail"]


@pytest.mark.parametrize("fault", [FAULT_AUTOPILOT, FAULT_GYRO])
def test_fault_trips_engaged_autopilot(fault: str) -> None:
    """自動舵啟用中注入故障:跳脫回 fu、舵停在目前舵角(C# TripAutopilot)。"""
    srv = _server()
    ship = srv.ship
    assert _cmd(srv, "autopilot", enabled=True, heading=60)["ok"]
    ship.run(6.0)
    delta = ship.state.delta
    assert abs(delta) > 1e-3
    ack = _cmd(srv, "injectFault", fault)
    assert ack["ok"] and "跳脫" in ack["detail"]
    assert ship.ctl.autopilot is False and ship.ctl.steering_mode == "fu"
    assert ship.ctl.rudder_order == pytest.approx(delta)
    ship.run(5.0)
    assert ship.state.delta == pytest.approx(delta, abs=1e-9)
    # 未啟用時注入:不動模式與舵令
    srv2 = _server()
    _cmd(srv2, "rudder", 10)
    _cmd(srv2, "injectFault", fault)
    assert srv2.ship.ctl.rudder_order * RAD2DEG == pytest.approx(10.0)
