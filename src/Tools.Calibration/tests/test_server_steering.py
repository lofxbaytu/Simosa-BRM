"""操舵模式 steeringMode(fu / nfu / autopilot / emergency、pump;情境引擎設計 D32、D35;command.schema.json)。

不開 socket,直接呼叫 SimServer.apply_command;語意對照 C# tests/SimCore.Tests/SteeringModeTests.cs:
NFU 推桿以舵機速率移動、stop 停在原處、進出 nfu 舵令同步到目前舵角、不明方向視同 stop、rudder 指令把 nfu/autopilot 切回 fu、
emergency 在 steeringGear 故障下以一半速率可動且拒絕自動舵、泵依單/雙泵轉舵時間換算速率、非預設模式才進雜湊。
"""

from __future__ import annotations

import hashlib
import struct

import pytest

from simosa_brm.manoeuvres import turning_circle
from simosa_brm.mmg import (
    FAULT_STEERING_FU_ONLY,
    FAULT_STEERING_GEAR,
    FAULT_STEERING_PUMP,
    CommandRejected,
    make_ship,
    normalize_pump,
)
from simosa_brm.server import SimServer

DT = 0.02


def _server(ship_id: str = "FSB1", speed_kn: float = 8.0) -> SimServer:
    ship = make_ship(ship_id, "full", dt=DT)
    ship.reset(speed_kn=speed_kn)
    return SimServer(ship, 1.0)


def _cmd(srv: SimServer, type_: str, value=None, **args) -> dict:
    c: dict = {"type": type_}
    if value is not None:
        c["value"] = value
    if args:
        c["args"] = args
    return srv.apply_command(c)


def _rate(srv: SimServer) -> float:
    """FSB1 舵機速率(度/秒;無單/雙泵資料,不依泵換算)。"""
    return srv.ship.delta_rate * 180.0 / 3.141592653589793


def _old_hash(ship) -> str:
    """操舵模式加入前的雜湊配置(fu / autopilot、泵 1 時必須與此相同)。"""
    s = ship.state
    payload = struct.pack("<d10dq", s.t, s.x, s.y, s.psi, s.u, s.v, s.r, s.delta, s.n, s.thr, ship.gust_factor, s.tick)
    return hashlib.sha256(payload).hexdigest()


def test_default_state_is_fu_pump_one_and_hash_unchanged() -> None:
    srv = _server()
    s = srv.state_json()
    assert s["steering"] == {"mode": "fu", "pump": "1"}  # nfuActive 只在 nfu 輸出
    srv.ship.run(5.0)
    assert srv.ship.state_hash() == _old_hash(srv.ship)
    # autopilot 模式與舊版自動舵同義:雜湊配置不變
    assert _cmd(srv, "steeringMode", "autopilot", heading=10)["ok"]
    srv.ship.run(2.0)
    assert srv.ship.state_hash() == _old_hash(srv.ship)


def test_nfu_drives_rudder_at_gear_rate_and_stop_holds_angle() -> None:
    srv = _server()
    ship = srv.ship
    rate = _rate(srv)
    ack = _cmd(srv, "steeringMode", "nfu", pump="both", direction="starboard")
    assert ack["ok"] and "fu → nfu" in ack["detail"] and "both" in ack["detail"]
    s = srv.state_json()
    # 推桿:舵令為滿舵(FSB1 Schilling 舵 maxAngle 70°)
    assert s["steering"] == {"mode": "nfu", "pump": "both", "nfuActive": True}
    assert s["rudderOrder"] == pytest.approx(ship.delta_max * 57.29577951308232)
    ship.run(2.0)
    assert ship.state_json()["rudder"] == pytest.approx(2.0 * rate, abs=1e-6)
    ack = _cmd(srv, "steeringMode", "nfu", direction="stop")
    assert ack["ok"] and ship.ctl.nfu_direction == 0
    held = ship.state_json()["rudder"]
    assert srv.state_json()["steering"]["nfuActive"] is False
    ship.run(3.0)
    assert ship.state_json()["rudder"] == pytest.approx(held, abs=1e-9)  # 推桿放開舵停在原處
    _cmd(srv, "steeringMode", "nfu", direction="port")
    ship.run(3.0)
    assert ship.state_json()["rudder"] < held - 5.0
    # 隨動舵令(使用舵輪)切回 fu,泵選擇保留
    assert _cmd(srv, "rudder", 5)["ok"]
    assert srv.state_json()["steering"] == {"mode": "fu", "pump": "both"}


def test_entering_nfu_with_unknown_or_missing_direction_holds_rudder() -> None:
    srv = _server()
    ship = srv.ship
    rate = _rate(srv)
    _cmd(srv, "rudder", 20)
    ship.run(1.0)
    held = ship.state_json()["rudder"]
    assert held == pytest.approx(rate, abs=1e-6)
    ack = _cmd(srv, "steeringMode", "nfu", direction="left")  # 不明方向視同 stop
    assert ack["ok"] and ship.ctl.nfu_direction == 0
    assert ship.state_json()["rudderOrder"] == pytest.approx(held, abs=1e-6)
    ship.run(3.0)
    assert ship.state_json()["rudder"] == pytest.approx(held, abs=1e-9)  # 不走到 20°
    _cmd(srv, "steeringMode", "nfu", direction="starboard")
    ship.run(1.0)
    held2 = ship.state_json()["rudder"]
    assert held2 > held + 1.0
    _cmd(srv, "steeringMode", "nfu", direction="sideways")  # 推桿中送不明方向亦視同放開
    ship.run(2.0)
    assert ship.ctl.nfu_direction == 0 and ship.state_json()["rudder"] == pytest.approx(held2, abs=1e-9)
    # 省略 direction:維持目前推動方向
    _cmd(srv, "steeringMode", "nfu", direction="port")
    _cmd(srv, "steeringMode", "nfu")
    assert ship.ctl.nfu_direction == -1


def test_leaving_nfu_holds_rudder_instead_of_running_to_hard_over() -> None:
    srv = _server()
    ship = srv.ship
    rate = _rate(srv)
    _cmd(srv, "steeringMode", "nfu", direction="starboard")
    ship.run(2.0)
    held = ship.state_json()["rudder"]
    assert held == pytest.approx(2.0 * rate, abs=1e-6)
    ack = _cmd(srv, "steeringMode", "fu")
    assert ack["ok"] and srv.state_json()["steering"] == {"mode": "fu", "pump": "1"}
    assert ship.state_json()["rudderOrder"] == pytest.approx(held, abs=1e-6)
    ship.run(3.0)
    assert ship.state_json()["rudder"] == pytest.approx(held, abs=1e-9)
    _cmd(srv, "rudder", 10)
    ship.run(10.0)
    assert ship.state_json()["rudder"] == pytest.approx(10.0, abs=1e-3)  # 接近舵令時為 1 s 一階趨近
    # nfu → emergency、nfu → autopilot 同樣先同步舵令
    _cmd(srv, "steeringMode", "nfu", direction="port")
    ship.run(1.0)
    _cmd(srv, "steeringMode", "emergency")
    assert ship.state_json()["rudderOrder"] == pytest.approx(ship.state_json()["rudder"], abs=1e-6)
    assert ship.ctl.nfu_direction == 0


def test_emergency_moves_jammed_rudder_at_half_rate_and_rejects_autopilot() -> None:
    srv = _server()
    ship = srv.ship
    rate = _rate(srv)
    assert ship.delta_rate_emergency == pytest.approx(0.5 * ship.delta_rate)  # 無 emergencyHardOverTime35_s
    ack = _cmd(srv, "injectFault", FAULT_STEERING_GEAR)
    assert ack["ok"] and "emergency" in ack["detail"]
    _cmd(srv, "rudder", 20)
    ship.run(2.0)
    assert ship.state_json()["rudder"] == 0.0  # 卡舵
    ack = _cmd(srv, "steeringMode", "emergency")
    assert ack["ok"] and srv.state_json()["steering"]["mode"] == "emergency"
    _cmd(srv, "rudder", 20)  # 電話下令仍以 rudder 指令表示,模式保持 emergency
    assert ship.ctl.steering_mode == "emergency"
    ship.run(4.0)
    assert ship.state_json()["rudder"] == pytest.approx(4.0 * rate / 2.0, abs=1e-6)
    assert FAULT_STEERING_GEAR in ship.faults

    # emergency 中自動舵:啟用與切換都拒絕、狀態不變(D35)
    h0 = ship.state_hash()
    order0 = ship.ctl.rudder_order
    for cmd in ({"type": "autopilot", "args": {"enabled": True, "heading": 90}},
                {"type": "steeringMode", "value": "autopilot", "args": {"heading": 90}}):
        ack = srv.apply_command(cmd)
        assert ack["ok"] is False and "emergencySteering" in ack["detail"] and "fu" in ack["detail"]
        assert ship.ctl.steering_mode == "emergency" and ship.ctl.autopilot is False
        assert ship.ctl.rudder_order == order0 and ship.state_hash() == h0
    with pytest.raises(CommandRejected):
        ship.set_autopilot(True)
    # 解除照常;未指明 enabled 時不啟用
    assert _cmd(srv, "autopilot", enabled=False)["ok"]
    ack = _cmd(srv, "autopilot", heading=45)
    assert ack["ok"] and ship.ctl.autopilot is False and ship.ctl.steering_mode == "emergency"
    # 先回 fu 才能接自動舵
    assert _cmd(srv, "steeringMode", "fu")["ok"]
    ack = _cmd(srv, "steeringMode", "autopilot")
    assert ack["ok"] and ship.ctl.autopilot and srv.state_json()["steering"]["mode"] == "autopilot"


def test_autopilot_mode_is_equivalent_to_autopilot_command() -> None:
    srv = _server()
    ship = srv.ship
    psi0 = srv.state_json()["heading"]
    ack = _cmd(srv, "steeringMode", "autopilot", rotLimit=0.2)  # 未給航向:保持目前航向;rotLimit 下限 1°/分
    assert ack["ok"] and ship.ctl.autopilot
    assert ship.ctl.autopilot_heading * 57.29577951308232 == pytest.approx(psi0)
    assert ship.ctl.autopilot_rot_limit * 57.29577951308232 * 60.0 == pytest.approx(1.0)
    _cmd(srv, "steeringMode", "autopilot", heading=480)  # 正規化到 0–360
    assert ship.ctl.autopilot_heading * 57.29577951308232 == pytest.approx(120.0)
    assert _cmd(srv, "autopilot", enabled=False)["ok"]
    assert ship.ctl.steering_mode == "fu" and not ship.ctl.autopilot
    assert _cmd(srv, "autopilot", enabled=True, heading=30)["ok"]
    assert ship.ctl.steering_mode == "autopilot"
    assert _cmd(srv, "rudder", 0)["ok"]  # 手動舵令解除自動舵
    assert ship.ctl.steering_mode == "fu" and not ship.ctl.autopilot
    # nfu 中 autopilot {enabled:false} 不改模式(C#:只有 autopilot 模式才切回 fu)
    _cmd(srv, "steeringMode", "nfu", direction="starboard")
    _cmd(srv, "autopilot", enabled=False)
    assert ship.ctl.steering_mode == "nfu" and ship.ctl.nfu_direction == 1


def test_fu_only_fault_blocks_follow_up_but_not_nfu() -> None:
    srv = _server()
    ship = srv.ship
    ack = _cmd(srv, "injectFault", FAULT_STEERING_FU_ONLY)
    assert ack["ok"] and "已套用" in ack["detail"]
    _cmd(srv, "rudder", 15)
    ship.run(2.0)
    assert ship.state_json()["rudder"] == 0.0
    _cmd(srv, "autopilot", enabled=True, heading=90)
    ship.run(2.0)
    assert ship.state_json()["rudder"] == 0.0
    _cmd(srv, "steeringMode", "nfu", direction="starboard")
    ship.run(2.0)
    assert ship.state_json()["rudder"] == pytest.approx(2.0 * _rate(srv), abs=1e-6)


def test_pump_failure_halves_rate_in_all_modes() -> None:
    srv = _server()
    ship = srv.ship
    rate = _rate(srv)
    assert "已套用" in _cmd(srv, "injectFault", FAULT_STEERING_PUMP)["detail"]
    _cmd(srv, "rudder", 20)
    ship.run(2.0)
    assert ship.state_json()["rudder"] == pytest.approx(rate, abs=1e-6)
    _cmd(srv, "steeringMode", "emergency")
    _cmd(srv, "rudder", -20)
    s0 = ship.state_json()["rudder"]
    ship.run(2.0)
    assert ship.state_json()["rudder"] == pytest.approx(s0 - 2.0 * rate / 4.0, abs=1e-6)


def test_fsb2_pump_selection_scales_rate_like_csharp() -> None:
    """No.2 單泵 28 s / 雙泵 14 s(35° → 35°):預設泵 "1"(C# 相同);fu 與 nfu 都依泵換算。"""
    srv = _server("FSB2", speed_kn=6.0)
    ship = srv.ship
    assert ship.c["rudder"]["rate_degps"] == pytest.approx(70.0 / 14.0)
    _cmd(srv, "rudder", -35)
    ship.run(30.0)
    assert ship.state_json()["rudder"] == pytest.approx(-35.0)
    _cmd(srv, "rudder", 35)
    ship.run(14.0)
    assert ship.state_json()["rudder"] == pytest.approx(0.0, abs=1e-6)  # 單泵 2.5°/s:14 s 只到中央
    ship.run(6.0)
    assert ship.state_json()["rudder"] == pytest.approx(15.0, abs=1e-6)
    ship.run(14.0)
    assert ship.state_json()["rudder"] == pytest.approx(35.0, abs=0.01)
    start = ship.state_json()["rudder"]
    ack = _cmd(srv, "steeringMode", "nfu", pump="both", direction="port")
    assert ack["ok"] and srv.state_json()["steering"]["pump"] == "both"
    ship.run(10.0)
    assert ship.state_json()["rudder"] == pytest.approx(start - 50.0, abs=1e-6)  # 雙泵 5°/s
    # 數值泵、不明泵
    assert _cmd(srv, "steeringMode", "nfu", pump=2)["ok"] and ship.ctl.steering_pump == "2"
    ack = _cmd(srv, "steeringMode", "nfu", pump="3")
    assert ack["ok"] and ship.ctl.steering_pump == "2" and "不明" in ack["detail"]
    assert [normalize_pump(x) for x in (1, 2.0, "both", " ALL ", "1+2", 1.5, True, None, "x")] == \
        ["1", "2", "both", "both", "both", None, None, None, None]


def test_trial_manoeuvres_use_both_pumps() -> None:
    """標準操縱試驗以兩泵(識別條件);即時模擬 reset 後回到泵 "1"。"""
    ship = make_ship("FSB2", "full", dt=0.1)
    r = turning_circle(ship, 35.0, "starboard", 12.8, max_heading_change_deg=100.0)
    assert ship.ctl.steering_pump == "both"
    first30 = next(h.t for h in r.history if h.rudder_deg >= 30.0)
    assert first30 <= 6.5  # 30° / 5°/s(單泵需 12 s)
    ship.reset()
    assert ship.ctl.steering_pump == "1" and ship.ctl.steering_mode == "fu"


def test_unknown_mode_and_target_control_rejected() -> None:
    srv = _server()
    h0 = srv.ship.state_hash()
    ack = _cmd(srv, "steeringMode", "manual")
    assert ack["ok"] is False and "未知操舵模式" in ack["detail"]
    ack = _cmd(srv, "steeringMode")
    assert ack["ok"] is False
    ack = srv.apply_command({"type": "steeringMode", "args": {"mode": "NFU"}})  # 模式也可放 args.mode、不分大小寫
    assert ack["ok"] and srv.ship.ctl.steering_mode == "nfu"
    srv.apply_command({"type": "steeringMode", "value": "fu"})
    ack = _cmd(srv, "targetControl", id="T1", heading=90)
    assert ack["ok"] is False and "無目標船" in ack["detail"] and "未知命令" not in ack["detail"]
    assert srv.ship.state_hash() == h0


def test_steering_is_hashed_snapshotted_and_deterministic() -> None:
    def run(cmds: list[dict]) -> tuple[str, SimServer]:
        srv = _server()
        for c in cmds:
            srv.apply_command(c)
            srv.ship.run(3.0)
        return srv.ship.state_hash(), srv

    script = [{"type": "steeringMode", "value": "nfu", "args": {"pump": "both", "direction": "starboard"}},
              {"type": "steeringMode", "value": "nfu", "args": {"direction": "stop"}},
              {"type": "injectFault", "value": FAULT_STEERING_GEAR},
              {"type": "steeringMode", "value": "emergency"},
              {"type": "rudder", "value": -10},
              {"type": "steeringMode", "value": "nfu", "args": {"direction": "port"}}]
    h1, srv = run(script)
    h2, _ = run(script)
    assert h1 == h2
    # 快照包含操舵模式;還原後繼續推進與參考一致
    ship = srv.ship
    srv.apply_command({"type": "snapshot", "value": "s"})
    ship.run(5.0)
    ref_hash, ref_rudder = ship.state_hash(), ship.state.delta
    srv.apply_command({"type": "steeringMode", "value": "fu", "args": {"pump": "1"}})
    srv.apply_command({"type": "restore", "value": "s"})
    assert ship.ctl.steering_mode == "nfu" and ship.ctl.steering_pump == "both" and ship.ctl.nfu_direction == -1
    ship.run(5.0)
    assert ship.state_hash() == ref_hash and ship.state.delta == ref_rudder
    # 只改模式(運動相同)時雜湊不同
    a, b = _server(), _server()
    b.apply_command({"type": "steeringMode", "value": "nfu"})
    a.ship.run(2.0)
    b.ship.run(2.0)
    assert a.ship.state.as_tuple() == b.ship.state.as_tuple()
    assert a.ship.state_hash() != b.ship.state_hash()
    # reset 回到 fu / 泵 1
    b.apply_command({"type": "reset", "args": {"speed": 5}})
    assert b.state_json()["steering"] == {"mode": "fu", "pump": "1"}
