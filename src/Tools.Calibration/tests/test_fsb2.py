"""FSB2(僅海報資料,公差為 No.1 的 1.5 倍):以海報迴旋粗識別的係數檔比對;超出公差 → xfail 並報告偏差。"""

from __future__ import annotations

import pytest

from simosa_brm.manoeuvres import crash_stop, turning_circle

from .conftest import check_or_xfail, turning_tolerance


@pytest.mark.parametrize("side", ["port", "starboard"])
def test_fsb2_poster_turning(fsb2, fsb2_targets, side) -> None:
    tc = fsb2_targets["identification"]["turningCircle35"]
    r = turning_circle(fsb2, tc["rudder_deg"], side, tc["approachSpeed_kn"])
    assert r.completed
    d = tc[side]
    for key, sim in (("advance_m", r.advance_m), ("transfer_m", r.transfer_m), ("tacticalDiameter_m", r.tactical_diameter_m)):
        print(f"FSB2 {side} {key}: poster {d[key]} sim {sim:.1f}")
    for mk in d["marks"]:
        sm = r.marks[int(mk["heading_deg"])]
        print(f"FSB2 {side} {mk['heading_deg']}°: poster {mk['t_s']} s / {mk['speed_kn']} kn, sim {sm['t_s']:.0f} s / {sm['speed_kn']:.1f} kn")
    for key, sim in (("advance_m", r.advance_m), ("tacticalDiameter_m", r.tactical_diameter_m), ("transfer_m", r.transfer_m)):
        check_or_xfail(f"FSB2 {side} {key}", sim, d[key], turning_tolerance(fsb2_targets, d[key], fsb2.L), "m")


def test_fsb2_poster_crash_stop(fsb2, fsb2_targets) -> None:
    cs = fsb2_targets["validation"]["crashStop"]
    pct = fsb2_targets["tolerances"]["stopping_pct"] / 100.0
    r = crash_stop(fsb2, cs["approachSpeed_kn"])
    assert r.completed
    track = cs["trackReach_cables"] * 185.2
    print(f"FSB2 crash stop: astern start {r.astern_start_s:.0f}s stop {r.stop_time_s:.0f}s track {r.track_reach_m:.0f} m (poster {cs['stopTime_s']} s / {track:.0f} m)")
    check_or_xfail("FSB2 緊急停船 航跡距離", r.track_reach_m, track, pct * track, "m")
    check_or_xfail("FSB2 緊急停船 停船時間", r.stop_time_s, cs["stopTime_s"], pct * cs["stopTime_s"], "s")
