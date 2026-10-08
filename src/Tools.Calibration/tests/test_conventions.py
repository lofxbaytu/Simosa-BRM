"""單位與正負慣例(src/Contracts/state.schema.json):舵右正 → 右轉(r>0、ROT>0、航向增加);節/度/度每分換算;欄位齊全。"""

from __future__ import annotations

import json
import math

import pytest

from simosa_brm import paths
from simosa_brm.mmg import MMGShip, make_ship
from simosa_brm.particulars import KN_TO_MPS


@pytest.fixture(scope="module")
def ship() -> MMGShip:
    return make_ship("FSB1", "full", dt=0.02, prefer_file=False)


def test_starboard_rudder_turns_right(ship: MMGShip) -> None:
    ship.reset(heading_deg=0.0, speed_kn=10.0)
    ship.set_rudder(20.0)
    ship.run(40.0)
    s = ship.state_json()
    assert s["rudder"] > 0 and s["rudderOrder"] == 20.0
    assert ship.state.r > 0.0 and s["rot"] > 0.0
    assert 0.0 < s["heading"] < 90.0
    assert ship.state.v < 0.0, "右轉時船舯橫向速度向左(MMG 慣例 v_m 右正)"
    assert s["drift"] > 0.0, "漂角 β = atan(−v/u) 右轉為正"
    assert s["pos"]["x"] > 0.0 and s["pos"]["y"] > 0.0, "北向起航右轉 → 東北象限"


def test_port_rudder_turns_left(ship: MMGShip) -> None:
    ship.reset(heading_deg=90.0, speed_kn=10.0)
    ship.set_rudder(-20.0)
    ship.run(40.0)
    s = ship.state_json()
    assert ship.state.r < 0.0 and s["rot"] < 0.0
    assert s["heading"] < 90.0
    assert s["pos"]["x"] > 0.0 and s["pos"]["y"] > 0.0, "東向起航左轉 → 往北偏"


def test_heading_wraps_0_360(ship: MMGShip) -> None:
    ship.reset(heading_deg=350.0, speed_kn=10.0)
    ship.set_rudder(30.0)
    ship.run(60.0)
    h = ship.state_json()["heading"]
    assert 0.0 <= h < 360.0 and h < 180.0, "過 360 後應回到 0 附近"


def test_units_kn_deg_rot(ship: MMGShip) -> None:
    ship.reset(heading_deg=0.0, speed_kn=12.0)
    s = ship.state_json()
    assert abs(s["stw"] - 12.0) < 1e-9 and abs(s["sog"] - 12.0) < 1e-6
    assert abs(s["u"] - 12.0 * KN_TO_MPS) < 1e-9
    ship.state.r = math.radians(1.0)  # 1 °/s = 60 °/min
    assert abs(ship.state_json()["rot"] - 60.0) < 1e-9
    ship.state.delta = math.radians(-15.0)
    assert abs(ship.state_json()["rudder"] + 15.0) < 1e-9


def test_rudder_rate_limit(ship: MMGShip) -> None:
    ship.reset(speed_kn=10.0)
    ship.set_rudder(35.0)
    ship.run(2.0)
    assert ship.state_json()["rudder"] <= 2.0 * ship.delta_rate / math.pi * 180.0 + 0.5
    ship.run(30.0)
    assert abs(ship.state_json()["rudder"] - 35.0) < 0.5


def test_rudder_order_clamped_to_max(ship: MMGShip) -> None:
    ship.reset(speed_kn=5.0)
    ship.set_rudder(90.0)
    assert math.degrees(ship.ctl.rudder_order) == pytest.approx(ship.sp.rudder_max_angle)


def test_telegraph_and_rpm_sign(ship: MMGShip) -> None:
    ship.reset(speed_kn=0.0)
    ship.set_telegraph("HAH")
    ship.run(60.0)
    assert ship.state_json()["rpm"] > 0 and ship.state_json()["telegraph"] == "HAH"
    ship.set_telegraph("HAS")
    ship.run(240.0)
    s = ship.state_json()
    assert s["rpm"] < 0 and s["rpmOrder"] < 0, "倒車轉速為負"
    assert ship.state.u < 0.0, "倒車後船應開始後退"


def test_thruster_pushes_bow_to_starboard(ship: MMGShip) -> None:
    ship.reset(speed_kn=0.0)
    ship.set_thruster(1.0)
    ship.run(60.0)
    assert ship.state.r > 0.0 and ship.state_json()["thruster"]["actual"] > 0.5


def test_current_moves_ground_track_not_water_speed(ship: MMGShip) -> None:
    ship.reset(heading_deg=0.0, speed_kn=0.0)
    ship.set_environment(current_set_deg=90.0, current_drift_kn=2.0)
    ship.run(60.0)
    s = ship.state_json()
    assert abs(s["stw"]) < 1e-6 and s["sog"] == pytest.approx(2.0, abs=1e-6)
    assert s["pos"]["x"] == pytest.approx(2.0 * KN_TO_MPS * 60.0, rel=1e-6)
    assert s["cog"] == pytest.approx(90.0, abs=1e-6)
    ship.set_environment(current_drift_kn=0.0)


def test_wind_drifts_downwind(ship: MMGShip) -> None:
    ship.reset(heading_deg=0.0, speed_kn=0.0)
    ship.set_environment(wind_speed_kn=40.0, wind_dir_deg=90.0)
    s0 = ship.state_json()
    assert s0["wind"]["trueSpeed"] == pytest.approx(40.0) and s0["wind"]["relDir"] == pytest.approx(90.0, abs=1e-6), "靜止時相對風 = 真風,自右舷"
    ship.run(120.0)
    s = ship.state_json()
    assert ship.state.v < 0.0 and s["pos"]["x"] < 0.0, "右舷風 → 向左(西)漂"
    assert s["sog"] > 0.3, "40 kn 側風下應明顯漂移"
    ship.set_environment(wind_speed_kn=0.0)


def test_shallow_water_and_grounding(ship: MMGShip) -> None:
    ship.reset(heading_deg=0.0, speed_kn=10.0)
    ship.set_environment(water_depth_m=1.2 * ship.d)
    assert ship.f_lin_sway > 1.5 and ship.my > ship.my0
    s = ship.state_json()
    assert 0.0 < s["depthBelowKeel"] < 1.2 * ship.d - ship.d + 0.01
    assert s["squat"] > 0.0
    ship.set_environment(water_depth_m=ship.d + 0.05)
    ship.run(5.0)
    assert ship.state_json()["flags"]["aground"] is True and ship.state.u == 0.0
    ship.set_environment(water_depth_m=None)
    assert ship.f_lin_sway == 1.0


def test_state_json_matches_schema(ship: MMGShip) -> None:
    schema = json.loads((paths.contracts_dir() / "state.schema.json").read_text(encoding="utf-8"))
    ship.reset(speed_kn=8.0)
    ship.run(1.0)
    s = ship.state_json()
    for key in schema["required"]:
        assert key in s, f"缺少欄位 {key}"
    assert s["telegraph"] in schema["properties"]["telegraph"]["enum"]
    assert s["loading"] in schema["properties"]["loading"]["enum"]
    assert set(s["pos"]) >= {"lat", "lon", "x", "y"}
    assert set(s["wind"]) >= {"trueSpeed", "trueDir"} and set(s["current"]) >= {"set", "drift"}
    json.dumps(s)  # 可序列化


def test_coefficients_file_matches_schema_required() -> None:
    schema = json.loads((paths.contracts_dir() / "coefficients.schema.json").read_text(encoding="utf-8"))
    from simosa_brm.coefficients import estimate_coefficients
    from simosa_brm.particulars import load_particulars, load_trial_targets

    for ship_id in ("FSB1", "FSB2"):
        for loading in ("full", "ballast"):
            c = estimate_coefficients(load_particulars(ship_id, loading), load_trial_targets(ship_id))
            for key in schema["required"]:
                assert key in c, f"{ship_id}/{loading} 缺 {key}"
            for sec in ("mass", "hull", "propeller", "rudder", "engine", "thruster", "wind", "shallowWater"):
                for key in schema["properties"][sec].get("required", []):
                    assert key in c[sec], f"{ship_id}/{loading} {sec} 缺 {key}"
