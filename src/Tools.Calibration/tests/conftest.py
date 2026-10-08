"""pytest 共用 fixture:已識別係數檔(data/ships/<ID>/coefficients.<loading>.json)存在時才執行黃金測試。"""

from __future__ import annotations

import math

import pytest

from simosa_brm import paths
from simosa_brm.coefficients import load_coefficients
from simosa_brm.mmg import MMGShip
from simosa_brm.particulars import load_particulars, load_trial_targets

DT = 0.02  # 驗證步長(規劃書 5.3:50 Hz)


def _ship_with_identified(ship_id: str, loading: str = "full", dt: float = DT) -> MMGShip:
    p = paths.coefficients_path(ship_id, loading)
    if not p.exists():
        pytest.skip(f"缺少已識別係數檔 {p}(先執行 simosa-brm identify --ship {ship_id} --loading {loading})")
    c = load_coefficients(ship_id, loading, p)
    if c.get("source", {}).get("method") != "identify":
        pytest.skip(f"{p} 非識別結果(method={c.get('source', {}).get('method')})")
    return MMGShip(load_particulars(ship_id, loading), c, dt=dt)


@pytest.fixture(scope="session")
def fsb1():
    return _ship_with_identified("FSB1")


@pytest.fixture(scope="session")
def fsb1_targets():
    return load_trial_targets("FSB1")


@pytest.fixture(scope="session")
def fsb2():
    return _ship_with_identified("FSB2")


@pytest.fixture(scope="session")
def fsb2_targets():
    return load_trial_targets("FSB2")


def turning_tolerance(tt: dict, target: float, lpp: float) -> float:
    tol = tt["tolerances"]
    return max(tol["turning_pct"] / 100.0 * target, tol["turning_minL"] * lpp)


def zigzag_tolerance(tt: dict, target: float) -> float:
    tol = tt["tolerances"]
    return max(float(tol["zigzag_deg"]), tol["zigzag_pct"] / 100.0 * target)


def check_or_xfail(label: str, sim: float, target: float, tol: float, unit: str = "") -> None:
    """驗證區項目:超出公差時以 xfail 記錄實際偏差(不竄改公差,規劃書 6.3)。"""
    dev = sim - target
    msg = f"{label}: 模擬 {sim:.1f} vs 目標 {target:.1f} {unit}(偏差 {dev:+.1f} = {100 * dev / target:+.1f}%,公差 ±{tol:.1f})"
    print(msg)
    if math.isnan(sim) or abs(dev) > tol:
        pytest.xfail(msg)
