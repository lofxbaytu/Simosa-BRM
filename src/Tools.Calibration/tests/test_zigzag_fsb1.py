"""FSB1 Z 形:10/10(識別目標)在公差內;20/20(驗證資料,未用於擬合)超出公差時 xfail 並記錄偏差。"""

from __future__ import annotations

import pytest

from simosa_brm.manoeuvres import zigzag

from .conftest import check_or_xfail, zigzag_tolerance


@pytest.mark.parametrize("first", ["portFirst", "starboardFirst"])
def test_zigzag_10_10_within_tolerance(fsb1, fsb1_targets, first) -> None:
    zz = fsb1_targets["identification"]["zigzag10_10"]
    trial = zz[first]["overshoots_deg"]
    z = zigzag(fsb1, 10.0, 10.0, first.replace("First", ""), zz["approachSpeed_kn"], n_overshoots=len(trial))
    assert z.completed and len(z.overshoots_deg) == len(trial)
    for i, (sim, tgt) in enumerate(zip(z.overshoots_deg, trial)):
        tol = zigzag_tolerance(fsb1_targets, tgt)
        print(f"FSB1 10/10 {first} overshoot {i + 1}: trial {tgt} sim {sim:.2f} tol ±{tol:.1f}")
        assert abs(sim - tgt) <= tol


@pytest.mark.parametrize("first", ["portFirst", "starboardFirst"])
def test_zigzag_20_20_validation(fsb1, fsb1_targets, first) -> None:
    zz = fsb1_targets["validation"]["zigzag20_20"]
    trial = zz[first]["overshoots_deg"]
    z = zigzag(fsb1, 20.0, 20.0, first.replace("First", ""), zz["approachSpeed_kn"], n_overshoots=len(trial))
    assert z.completed
    for i, (sim, tgt) in enumerate(zip(z.overshoots_deg, trial)):
        check_or_xfail(f"FSB1 20/20 {first} 第 {i + 1} 超越角", sim, tgt, zigzag_tolerance(fsb1_targets, tgt), "°")
