"""pytest 共用 fixture:已識別係數檔(data/ships/<ID>/coefficients.<loading>.json)存在時才執行黃金測試。

另負責把 tmp_path 的 basetemp 固定在專案內(CLAUDE.md:所有輸出留在 D:\\Simosa BRM):
pytest 的 ``--basetemp`` 以「執行時的目前目錄」解析相對路徑(不是 pyproject.toml 所在目錄),
寫在 addopts 會在別的 cwd 下落到專案外或 FileNotFoundError,因此改在此以本檔位置推導絕對路徑。
"""

from __future__ import annotations

import math
from pathlib import Path

import pytest

from simosa_brm import paths
from simosa_brm.coefficients import load_coefficients
from simosa_brm.mmg import MMGShip
from simosa_brm.particulars import load_particulars, load_trial_targets

DT = 0.02  # 驗證步長(規劃書 5.3:50 Hz)

# 專案根目錄:tests → Tools.Calibration → src → 根(刻意不用 SIMOSA_BRM_ROOT,測試暫存跟著被測的儲存庫走)
_PROJECT_ROOT = Path(__file__).resolve().parents[3]
# pytest 會清空此目錄(getbasetemp 先 rm_rf 再 mkdir),須為專用目錄
DEFAULT_BASETEMP = _PROJECT_ROOT / "build" / "test-tmp" / "pytest"


@pytest.hookimpl(tryfirst=True)
def pytest_configure(config: pytest.Config) -> None:
    """未在命令列明確給 --basetemp 時,tmp_path 一律放在 <專案>/build/test-tmp/pytest(絕對路徑)。

    須在內建 tmpdir 外掛的 pytest_configure 之前執行(tryfirst),因為 TempPathFactory
    在建立時就以 os.path.abspath 固定 basetemp;pytest 只建立最後一層,父目錄在此先建好。
    """
    if config.option.basetemp is None:
        DEFAULT_BASETEMP.parent.mkdir(parents=True, exist_ok=True)
        config.option.basetemp = str(DEFAULT_BASETEMP)


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
