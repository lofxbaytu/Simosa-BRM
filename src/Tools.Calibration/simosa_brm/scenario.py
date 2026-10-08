"""情境檔(data/scenarios/*.yaml;schema src/Contracts/scenario.schema.json;規劃書第 9.1、11.3 節)。

讀取、檢查與初始化的語意與 C# SimCore 相同(Scenario.Validate / SimulationEngine.Initialize / ResolveInitialPropulsion):

- 代號(``E01_baseline``)解析為 ``data/scenarios/<id>.yaml``;也接受絕對路徑或相對(工作目錄、專案根目錄)路徑。
- ``origin`` 未給時以初始經緯度為原點;初始位置用 {x, y} 時必須給 origin。經緯度 → 本地 ENU 用 ``LocalTangentPlane``。
- 初始車鐘/轉速:``initial.rpm`` 優先(車鐘未指定時以最接近者標示);否則車鐘指定時取其轉速;否則航速 > 0 取直航平衡轉速
  (``MMGShip.reset`` 的預設);否則停俥。
- 角度為度、速度為節;風向為來向、流向為去向;時間倍率夾到 [0.1, 10];``environment.visibility_nm``(schema 未定義)只記錄。
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Any

import yaml

from . import paths
from .mmg import DEG, TELEGRAPH_ORDERS, MMGShip, make_ship

SCENARIO_SUFFIXES = (".yaml", ".yml")
MIN_TIME_SCALE, MAX_TIME_SCALE = 0.1, 10.0  # scenario.schema.json timeScale 範圍;C# EngineOptions.MaxTimeScale = 10
LOADINGS = ("full", "ballast", "intermediate")


def scenarios_dir() -> Path:
    """``data/scenarios``;套件不在專案內(且未設 SIMOSA_BRM_ROOT)時,由套件所在位置往上找含 data/scenarios 的資料夾。"""
    d = paths.scenarios_dir()
    if d.is_dir():
        return d
    for parent in Path(__file__).resolve().parents:
        cand = parent / "data" / "scenarios"
        if cand.is_dir():
            return cand
    return d


def resolve_scenario_path(ref: str | os.PathLike[str]) -> Path:
    """情境代號(``E01_baseline``)或檔案路徑 → 存在的檔案路徑;找不到時 ``FileNotFoundError``(訊息列出找過的位置)。"""
    text = str(ref).strip()
    if not text:
        raise FileNotFoundError("情境代號或路徑為空")
    p = Path(text)
    candidates: list[Path] = []
    if p.suffix.lower() in SCENARIO_SUFFIXES or p.is_absolute() or len(p.parts) > 1:
        candidates += [p, paths.repo_root() / p]
        if len(p.parts) == 1:
            candidates.append(scenarios_dir() / p.name)
    else:
        candidates += [scenarios_dir() / f"{text}{suffix}" for suffix in SCENARIO_SUFFIXES]
    for c in candidates:
        if c.is_file():
            return c.resolve()
    raise FileNotFoundError(f"找不到情境 {text!r}(找過:{'、'.join(str(c) for c in candidates)})")


def _num(obj: dict[str, Any], key: str, default: float | None = None, *, where: str) -> float | None:
    v = obj.get(key, default)
    if v is None:
        return None
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        raise ValueError(f"{where}.{key} 必須為數值,得到 {v!r}")
    return float(v)


def validate_scenario(raw: dict[str, Any], default_id: str = "") -> dict[str, Any]:
    """檢查必要欄位並補齊預設值(C# Scenario.Validate 相同);回傳正規化後的新 dict(角度為度、速度為節)。"""
    if not isinstance(raw, dict):
        raise ValueError("情境內容必須為物件(YAML mapping)")
    sid = str(raw.get("id") or default_id).strip()
    if not sid:
        raise ValueError("情境缺少 id")
    ship = raw.get("ship") or {}
    if not isinstance(ship, dict):
        raise ValueError("ship 必須為物件")
    ship_id = str(ship.get("id") or "").strip().upper()
    if not ship_id:
        raise ValueError("情境缺少 ship.id")
    loading = str(ship.get("loading") or "ballast")
    if loading not in LOADINGS:
        raise ValueError(f"ship.loading 必須為 {LOADINGS} 之一,得到 {loading!r}")

    init = raw.get("initial") or {}
    if not isinstance(init, dict):
        raise ValueError("initial 必須為物件")
    pos = init.get("position")
    if not isinstance(pos, dict):
        raise ValueError("情境缺少 initial.position")
    lat, lon = _num(pos, "lat", where="initial.position"), _num(pos, "lon", where="initial.position")
    x, y = _num(pos, "x", where="initial.position"), _num(pos, "y", where="initial.position")
    has_geo = lat is not None and lon is not None
    has_local = x is not None and y is not None
    if not has_geo and not has_local:
        raise ValueError("initial.position 需為 {lat, lon} 或 {x, y}")
    origin = raw.get("origin")
    if origin is None:
        if not has_geo:
            raise ValueError("initial.position 以 {x, y} 指定時必須提供 origin {lat, lon}")
        origin = {"lat": lat, "lon": lon}
    else:
        if not isinstance(origin, dict):
            raise ValueError("origin 必須為物件")
        o_lat, o_lon = _num(origin, "lat", where="origin"), _num(origin, "lon", where="origin")
        if o_lat is None or o_lon is None:
            raise ValueError("origin 需有 lat 與 lon")
        origin = {"lat": o_lat, "lon": o_lon}
    heading = _num(init, "heading", where="initial")
    if heading is None:
        raise ValueError("情境缺少 initial.heading")
    speed = _num(init, "speed", 0.0, where="initial") or 0.0
    telegraph = init.get("telegraph")
    if telegraph is not None:
        telegraph = str(telegraph).strip().upper()
        if telegraph not in TELEGRAPH_ORDERS:
            raise ValueError(f"initial.telegraph 未知車令 {telegraph!r}")
    rpm = _num(init, "rpm", where="initial")
    rudder = _num(init, "rudder", 0.0, where="initial") or 0.0

    env = raw.get("environment") or {}
    if not isinstance(env, dict):
        raise ValueError("environment 必須為物件")
    wind = env.get("wind") or {}
    cur = env.get("current") or {}
    depth = _num(env, "waterDepth", where="environment")
    if depth is None or depth <= 0.0:
        raise ValueError("environment.waterDepth 必須為正")
    vis = _num(env, "visibility_nm", where="environment")
    if vis is None:
        vis = _num(env, "visibility", where="environment")

    seed_raw = raw.get("seed", 1)
    if isinstance(seed_raw, bool) or not isinstance(seed_raw, int):
        raise ValueError(f"seed 必須為整數,得到 {seed_raw!r}")
    ts = _num(raw, "timeScale", 1.0, where="scenario") or 1.0
    ts = 1.0 if ts <= 0.0 else min(MAX_TIME_SCALE, max(MIN_TIME_SCALE, ts))

    return {
        "id": sid,
        "name": str(raw["name"]) if raw.get("name") is not None else None,
        "description": str(raw["description"]) if raw.get("description") is not None else None,
        "seed": int(seed_raw),
        "startTimeUtc": str(raw["startTimeUtc"]) if raw.get("startTimeUtc") is not None else None,
        "timeScale": ts,
        "ship": {"id": ship_id, "loading": loading},
        "origin": origin,
        "initial": {"lat": lat, "lon": lon, "x": x, "y": y, "heading": heading % 360.0, "speed": speed,
                    "telegraph": telegraph, "rpm": rpm, "rudder": rudder},
        "environment": {
            "wind": {"trueSpeed": max(0.0, _num(wind, "trueSpeed", 0.0, where="environment.wind") or 0.0),
                     "trueDir": (_num(wind, "trueDir", 0.0, where="environment.wind") or 0.0) % 360.0,
                     "gustiness": min(1.0, max(0.0, _num(wind, "gustiness", 0.0, where="environment.wind") or 0.0))},
            "current": {"set": (_num(cur, "set", 0.0, where="environment.current") or 0.0) % 360.0,
                        "drift": max(0.0, _num(cur, "drift", 0.0, where="environment.current") or 0.0)},
            "waterDepth": depth,
            "visibility_nm": vis,
        },
    }


def load_scenario(ref: str | os.PathLike[str]) -> dict[str, Any]:
    """讀取並檢查情境檔;回傳正規化 dict(含 ``path``)。id 未填時取檔名。"""
    path = resolve_scenario_path(ref)
    with open(path, encoding="utf-8") as f:
        raw = yaml.safe_load(f)
    if raw is None:
        raise ValueError(f"{path}: 情境 YAML 為空")
    sc = validate_scenario(raw, default_id=path.stem)
    sc["path"] = str(path)
    return sc


def apply_scenario(ship: MMGShip, sc: dict[str, Any]) -> None:
    """把情境的初始狀態、環境與種子套到既有模型(船型/裝載須相符);tick/時間歸零、故障與自動舵清除。"""
    want = (sc["ship"]["id"], sc["ship"]["loading"])
    have = (ship.sp.ship_id.upper(), ship.sp.loading.name)
    if want != have:
        raise ValueError(f"情境需要船舶 {want[0]}/{want[1]},目前模型為 {have[0]}/{have[1]}")
    ship.seed = int(sc["seed"])
    ship.set_origin(sc["origin"]["lat"], sc["origin"]["lon"])
    init = sc["initial"]
    if init["x"] is not None and init["y"] is not None:
        x, y = init["x"], init["y"]
    else:
        x, y = ship.ltp.to_local(init["lat"], init["lon"])
    heading, speed = init["heading"], init["speed"]
    telegraph, rpm0 = init["telegraph"], init["rpm"]
    if rpm0 is not None:
        ship.reset(x, y, heading, speed, rpm=rpm0)
        if telegraph is not None:
            ship.ctl.telegraph = telegraph  # C#:車鐘以指定值標示、轉速令 = initial.rpm
    elif telegraph is not None:
        table = ship.eng["telegraph"]
        if telegraph not in table:
            raise ValueError(f"係數檔的車鐘表無 {telegraph}(可用:{list(table)})")
        ship.reset(x, y, heading, speed, rpm=float(table[telegraph]), telegraph=telegraph)
    else:
        ship.reset(x, y, heading, speed)
    delta = max(-ship.delta_max, min(ship.delta_max, init["rudder"] * DEG))
    ship.state.delta = delta
    ship.ctl.rudder_order = delta
    env = sc["environment"]
    ship.set_environment(env["wind"]["trueSpeed"], env["wind"]["trueDir"], env["current"]["set"], env["current"]["drift"],
                         env["waterDepth"], gustiness=env["wind"]["gustiness"], visibility_nm=env["visibility_nm"])


def ship_from_scenario(sc: dict[str, Any], dt: float = 0.02, coeffs: dict[str, Any] | None = None,
                       prefer_file: bool = True) -> MMGShip:
    """依情境的 ship.id / ship.loading 建立模型(``make_ship`` 同一條路徑)並套用初始狀態。"""
    ship = make_ship(sc["ship"]["id"], sc["ship"]["loading"], dt=dt, coeffs=coeffs, prefer_file=prefer_file, seed=int(sc["seed"]))
    apply_scenario(ship, sc)
    return ship
