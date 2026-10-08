"""直航速度-轉速:與 speedTrial(No.1)/ 海報(No.2)吻合(規劃書 6.4:±0.5 kn;No.2 ±0.75 kn)。"""

from __future__ import annotations

import pytest

from simosa_brm.manoeuvres import speed_rpm_table, speed_rpm_timeseries
from simosa_brm.mmg import make_ship
from simosa_brm.particulars import load_trial_targets


def test_fsb1_speed_trial_within_half_knot() -> None:
    ship = make_ship("FSB1", "full", dt=0.02)
    tol = load_trial_targets("FSB1")["tolerances"]["speed_kn"]
    trial = ship.sp.speed_trial
    sim = speed_rpm_table(ship, [p["rpm"] for p in trial])
    for p, s in zip(trial, sim):
        print(f"FSB1 {p['rpm']} rpm: trial {p['speed_kn']:.2f} kn, sim {s['speed_kn']:.2f} kn")
        assert abs(s["speed_kn"] - p["speed_kn"]) <= tol


def test_fsb1_time_integration_agrees_with_steady_solution() -> None:
    ship = make_ship("FSB1", "full", dt=0.1)
    steady = speed_rpm_table(ship, [168.0])[0]["speed_kn"]
    integrated = speed_rpm_timeseries(ship, 168.0, seconds=2400.0)
    assert abs(integrated - steady) < 0.1


def test_fsb2_poster_full_sea_speed() -> None:
    ship = make_ship("FSB2", "full", dt=0.02)
    tt = load_trial_targets("FSB2")
    tol = tt["tolerances"]["speed_kn"]
    p = tt["validation"]["speedPower"][0]  # 海報 full sea 133.6 rpm / 12.8 kn
    s = speed_rpm_table(ship, [p["rpm"]])[0]
    print(f"FSB2 {p['rpm']} rpm: poster {p['speed_kn']} kn, sim {s['speed_kn']:.2f} kn")
    assert abs(s["speed_kn"] - p["speed_kn"]) <= tol


@pytest.mark.parametrize("ship_id", ["FSB1", "FSB2"])
def test_telegraph_table_trend(ship_id: str) -> None:
    """海報車鐘對照表:模擬速度單調遞增且與海報值差距在 1.5 kn 內(海報為服務狀態估計值)。"""
    ship = make_ship(ship_id, "full", dt=0.02)
    prev = -1.0
    for order in ("DSAH", "SAH", "HAH", "FAH", "NAVF"):
        rpm = ship.sp.telegraph_rpm(order)
        v = speed_rpm_table(ship, [rpm])[0]["speed_kn"]
        ref = ship.sp.telegraph_speed_kn(order)
        print(f"{ship_id} {order} {rpm} rpm: sim {v:.2f} kn, poster {ref}")
        assert v > prev
        prev = v
        if ref:
            assert abs(v - ref) <= 1.5
