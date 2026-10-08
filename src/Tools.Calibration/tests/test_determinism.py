"""確定性(規劃書 5.1、14 章 G3):相同輸入序列 → 相同狀態雜湊;不同裝載/環境 → 不同雜湊。"""

from __future__ import annotations

from simosa_brm.mmg import make_ship


def _scripted_run(dt: float = 0.02) -> tuple[str, dict]:
    ship = make_ship("FSB1", "full", dt=dt, prefer_file=False)
    ship.reset(heading_deg=45.0, speed_kn=12.0)
    ship.set_environment(wind_speed_kn=15.0, wind_dir_deg=120.0, current_set_deg=200.0, current_drift_kn=1.0, water_depth_m=20.0)
    script = [(0.0, lambda s: s.set_rudder(20.0)), (30.0, lambda s: s.set_telegraph("HAH")), (60.0, lambda s: s.set_rudder(-35.0)),
              (90.0, lambda s: s.set_thruster(-0.6)), (120.0, lambda s: s.set_autopilot(True, 300.0, 20.0, 25.0)), (200.0, lambda s: s.set_telegraph("FAS"))]
    t_end = 300.0
    i = 0
    while ship.state.t < t_end - 1e-9:
        while i < len(script) and ship.state.t >= script[i][0] - 1e-9:
            script[i][1](ship)
            i += 1
        ship.step()
    return ship.state_hash(), ship.state_json()


def test_same_inputs_same_hash() -> None:
    h1, s1 = _scripted_run()
    h2, s2 = _scripted_run()
    assert h1 == h2
    assert s1 == s2


def test_hash_changes_with_input() -> None:
    ship_a = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    ship_b = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    for s in (ship_a, ship_b):
        s.reset(speed_kn=10.0)
    ship_a.set_rudder(10.0)
    ship_b.set_rudder(10.5)
    for _ in range(500):
        ship_a.step()
        ship_b.step()
    assert ship_a.state_hash() != ship_b.state_hash()


def test_step_count_and_time() -> None:
    ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    ship.reset(speed_kn=10.0)
    ship.run(10.0)
    assert ship.state.tick == 500
    assert abs(ship.state.t - 10.0) < 1e-9
