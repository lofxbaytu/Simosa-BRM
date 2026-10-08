"""FSB1 停船(驗證資料):緊急停船 ±15%;慣性停船、倒車→進車、側推迴轉為參考(超出公差 → xfail 並記錄偏差)。"""

from __future__ import annotations

import math

from simosa_brm.manoeuvres import crash_ahead, crash_stop, inertia_stop, thruster_turn

from .conftest import check_or_xfail


def test_crash_stop_validation(fsb1, fsb1_targets) -> None:
    cs = fsb1_targets["validation"]["crashStop"]
    pct = fsb1_targets["tolerances"]["stopping_pct"] / 100.0
    r = crash_stop(fsb1, cs["approachSpeed_kn"])
    assert r.completed and not math.isnan(r.astern_start_s)
    print(f"FSB1 crash stop: astern start {r.astern_start_s:.0f}s (trial {cs['asternStart_s']}), heading change {r.final_heading_change_deg:+.1f}°")
    assert r.final_heading_change_deg > 0.0, "右旋槳倒車:艉向左、艏向右"
    check_or_xfail("FSB1 緊急停船 航跡距離", r.track_reach_m, cs["trackReach_m"], pct * cs["trackReach_m"], "m")
    check_or_xfail("FSB1 緊急停船 停船時間", r.stop_time_s, cs["stopTime_s"], pct * cs["stopTime_s"], "s")


def test_inertia_stop_validation(fsb1, fsb1_targets) -> None:
    ins = fsb1_targets["validation"]["inertiaStop"]
    pct = fsb1_targets["tolerances"]["stopping_pct"] / 100.0
    r = inertia_stop(fsb1, ins["approachSpeed_kn"], 5.0)
    assert r.completed
    check_or_xfail("FSB1 慣性停船 減至 5 kn 時間(試俥航跡彎曲,直線滑行無法重現)", r.time_to_target_s, ins["timeTo5kn_s"], pct * ins["timeTo5kn_s"], "s")


def test_crash_ahead_validation(fsb1, fsb1_targets) -> None:
    ca = fsb1_targets["validation"]["crashAhead"]
    pct = fsb1_targets["tolerances"]["stopping_pct"] / 100.0
    r = crash_ahead(fsb1, ca["approachSpeedAstern_kn"])
    assert r.completed
    check_or_xfail("FSB1 倒車 6.2 kn → 全速進 停船時間", r.stop_time_s, ca["time_s"], pct * ca["time_s"], "s")


def test_bow_thruster_turn_validation(fsb1, fsb1_targets) -> None:
    bt = fsb1_targets["validation"]["bowThrusterTurn90"]
    pct = fsb1_targets["tolerances"]["stopping_pct"] / 100.0
    for side in ("port", "starboard"):
        r = thruster_turn(fsb1, side, 90.0, bt["speed_kn"])
        assert r.completed
        check_or_xfail(f"FSB1 艏側推 {side} 90°", r.time_s, bt[f"{side}_s"], pct * bt[f"{side}_s"], "s")
