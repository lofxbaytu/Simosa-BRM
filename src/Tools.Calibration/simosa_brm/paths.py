"""專案路徑:所有輸入(data/)與輸出(build/、.cache/)都在專案資料夾內(CLAUDE.md、規劃書第 11 章)。

根目錄的決定順序:環境變數 ``SIMOSA_BRM_ROOT`` → 本檔案往上三層(src/Tools.Calibration/simosa_brm)。
"""

from __future__ import annotations

import os
from pathlib import Path


def repo_root() -> Path:
    """回傳專案根目錄(Windows 上為 ``D:\\Simosa BRM``)。"""
    env = os.environ.get("SIMOSA_BRM_ROOT")
    if env:
        return Path(env).resolve()
    return Path(__file__).resolve().parents[3]


def data_dir() -> Path:
    return repo_root() / "data"


def ship_dir(ship_id: str) -> Path:
    return data_dir() / "ships" / ship_id


def particulars_path(ship_id: str) -> Path:
    return ship_dir(ship_id) / "particulars.json"


def trial_targets_path(ship_id: str) -> Path:
    return ship_dir(ship_id) / "trial_targets.json"


def coefficients_path(ship_id: str, loading: str) -> Path:
    """係數檔:``data/ships/<ID>/coefficients.<loading>.json``(C# 與 Python 共用)。"""
    return ship_dir(ship_id) / f"coefficients.{loading}.json"


def scenarios_dir() -> Path:
    """情境檔資料夾 ``data/scenarios``(規劃書第 11.3 節)。"""
    return data_dir() / "scenarios"


def contracts_dir() -> Path:
    return repo_root() / "src" / "Contracts"


def build_dir() -> Path:
    p = repo_root() / "build"
    p.mkdir(parents=True, exist_ok=True)
    return p


def calibration_out_dir(ship_id: str) -> Path:
    p = build_dir() / "calibration" / ship_id
    p.mkdir(parents=True, exist_ok=True)
    return p


def cache_dir(name: str) -> Path:
    """``.cache/<name>``(例如 matplotlib 的字型快取),不寫到使用者家目錄。"""
    p = repo_root() / ".cache" / name
    p.mkdir(parents=True, exist_ok=True)
    return p
