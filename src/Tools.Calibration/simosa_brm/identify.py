"""參數識別(規劃書 6.3「參數識別」):以 trial_targets.identification 區的操縱結果擬合 8 至 12 個 MMG 參數。

- 目標:No.1 為 35° 左右迴旋(前進距離、橫距、戰術直徑、定常速度)+ 10/10 Z 形超越角;
  No.2 為海報 35° 迴旋(含 90/180/270/360° 歷時與速度)。validation 區一律不用。
- 殘差以公差正規化(迴旋:max(turning_pct, turning_minL·L);Z 形:max(zigzag_deg, zigzag_pct);速度:speed_kn),
  另加弱先驗殘差 λ·(p−p0)/σ 使未受資料約束的參數留在經驗值附近(避免參數互相抵消)。
- 最佳化:scipy.optimize.least_squares(trf,有界);有限差分 Jacobian 以多行程平行計算(結果與序列計算相同,具確定性)。
"""

from __future__ import annotations

import copy
import datetime as _dt
import json
import math
import os
from concurrent.futures import ProcessPoolExecutor
from dataclasses import dataclass
from typing import Any

import numpy as np
from scipy.optimize import least_squares

from . import __version__, paths
from .coefficients import estimate_coefficients, save_coefficients
from .manoeuvres import turning_circle, zigzag
from .mmg import MMGShip
from .particulars import ShipParticulars, load_particulars, load_trial_targets

# 參數:名稱 → (係數檔路徑, 下界, 上界, 先驗標準差(相對或絕對))
PARAM_SPECS: dict[str, tuple[tuple[str, str], float, float, float | str]] = {
    "Yv": (("hull", "Yv"), -0.70, -0.12, "rel0.3"),
    "Yr": (("hull", "Yr"), 0.02, 0.25, "rel0.4"),
    "Nv": (("hull", "Nv"), -0.30, -0.04, "rel0.3"),
    "Nr": (("hull", "Nr"), -0.14, -0.015, "rel0.3"),
    "Yvvv": (("hull", "Yvvv"), -3.5, -0.2, "rel0.5"),
    "Nvvr": (("hull", "Nvvr"), -0.9, 0.0, "rel0.5"),
    "Nrrr": (("hull", "Nrrr"), -0.09, 0.0, "rel0.6"),
    "Nvrr": (("hull", "Nvrr"), -0.2, 0.3, "rel0.6"),
    "Yvrr": (("hull", "Yvrr"), -1.2, 0.0, "rel0.5"),
    "aH": (("rudder", "aH"), 0.08, 0.70, "rel0.4"),
    "xH": (("rudder", "xH"), -0.6, -0.3, "rel0.2"),
    "epsilon": (("rudder", "epsilon"), 0.6, 1.8, "rel0.3"),
    "gammaRMinus": (("rudder", "gammaRMinus"), 0.15, 0.95, "rel0.4"),
    "gammaRPlus": (("rudder", "gammaRPlus"), 0.15, 0.95, "rel0.4"),
    "swirlAngle": (("rudder", "swirlAngle_deg"), -3.0, 3.0, 1.5),
    "R0scale": (("hull", "resistance", "scale"), 0.7, 1.4, 0.15),
}

DEFAULT_PARAMS = ["Yv", "Yr", "Nv", "Nr", "Yvvv", "Nvvr", "Nrrr", "aH", "epsilon", "gammaRMinus", "gammaRPlus", "swirlAngle"]


def _get(c: dict[str, Any], path: tuple[str, ...]) -> float:
    d: Any = c
    for k in path:
        d = d.get(k, 0.0) if isinstance(d, dict) else 0.0
    return float(d or 0.0) if not isinstance(d, dict) else 0.0


def _set(c: dict[str, Any], path: tuple[str, ...], value: float) -> None:
    d: Any = c
    for k in path[:-1]:
        d = d.setdefault(k, {})
    d[path[-1]] = float(value)


def apply_params(base: dict[str, Any], names: list[str], values: np.ndarray | list[float]) -> dict[str, Any]:
    c = copy.deepcopy(base)
    for name, v in zip(names, values):
        _set(c, PARAM_SPECS[name][0], float(v))
    if "R0scale" in names:
        # 阻力倍率:以 lowSpeedFloor 與 wave.coefficient、formFactor 同比例縮放
        sc = float(values[names.index("R0scale")])
        r = c["hull"]["resistance"]
        r["viscous"]["formFactor"] = base["hull"]["resistance"]["viscous"]["formFactor"] * sc
        r["wave"]["coefficient"] = base["hull"]["resistance"]["wave"]["coefficient"] * sc
        if "lowSpeedFloor" in r:
            r["lowSpeedFloor"]["R0"] = base["hull"]["resistance"]["lowSpeedFloor"]["R0"] * sc
    return c


# ---------------------------------------------------------------------------
# 目標與殘差
# ---------------------------------------------------------------------------
@dataclass
class Target:
    key: str  # 例如 turning.port.advance_m
    value: float
    tolerance: float
    weight: float = 1.0


def build_targets(tt: dict[str, Any], lpp: float) -> list[Target]:
    tol = tt.get("tolerances", {})
    t_pct = float(tol.get("turning_pct", 10.0)) / 100.0
    t_minl = float(tol.get("turning_minL", 0.3)) * lpp
    z_deg = float(tol.get("zigzag_deg") or 3.0)
    z_pct = float(tol.get("zigzag_pct") or 25.0) / 100.0
    v_kn = float(tol.get("speed_kn", 0.5))
    ident = tt.get("identification", {})
    out: list[Target] = []
    tc = ident.get("turningCircle35")
    if tc:
        for side in ("port", "starboard"):
            if side not in tc:
                continue
            d = tc[side]
            for key in ("advance_m", "transfer_m", "tacticalDiameter_m"):
                if key in d:
                    out.append(Target(f"turning.{side}.{key}", float(d[key]), max(t_pct * float(d[key]), t_minl)))
            for mk in d.get("marks", []):
                hd = int(mk["heading_deg"])
                if "t_s" in mk:
                    out.append(Target(f"turning.{side}.t{hd}_s", float(mk["t_s"]), max(t_pct * float(mk["t_s"]), 5.0), 0.7))
                if "speed_kn" in mk:
                    out.append(Target(f"turning.{side}.v{hd}_kn", float(mk["speed_kn"]), v_kn, 0.7))
        if "steadySpeedInTurn_kn" in tc:
            lo, hi = tc["steadySpeedInTurn_kn"]
            for side in ("port", "starboard"):
                if side in tc:
                    out.append(Target(f"turning.{side}.steadySpeed_kn", 0.5 * (lo + hi), v_kn + 0.5 * (hi - lo)))
    zz = ident.get("zigzag10_10")
    if zz:
        for first in ("portFirst", "starboardFirst"):
            if first in zz:
                for i, o in enumerate(zz[first]["overshoots_deg"]):
                    out.append(Target(f"zigzag10_10.{first}.overshoot{i + 1}", float(o), max(z_deg, z_pct * float(o))))
    return out


def simulate_targets(ship: MMGShip, tt: dict[str, Any], targets: list[Target]) -> dict[str, float]:
    """執行識別所需操縱並回傳各目標的模擬值。"""
    sim: dict[str, float] = {}
    ident = tt.get("identification", {})
    tc = ident.get("turningCircle35")
    if tc:
        for side in ("port", "starboard"):
            if side not in tc:
                continue
            need_full = any(k.startswith(f"turning.{side}.t") or k.startswith(f"turning.{side}.v") or k.endswith("steadySpeed_kn") for k in (t.key for t in targets))
            r = turning_circle(ship, tc.get("rudder_deg", 35.0), side, tc["approachSpeed_kn"],
                               max_heading_change_deg=540.0 if need_full else 200.0)
            sim[f"turning.{side}.advance_m"] = r.advance_m
            sim[f"turning.{side}.transfer_m"] = r.transfer_m
            sim[f"turning.{side}.tacticalDiameter_m"] = r.tactical_diameter_m
            sim[f"turning.{side}.steadySpeed_kn"] = r.steady_speed_kn
            for hd, mk in r.marks.items():
                sim[f"turning.{side}.t{hd}_s"] = mk["t_s"]
                sim[f"turning.{side}.v{hd}_kn"] = mk["speed_kn"]
    zz = ident.get("zigzag10_10")
    if zz:
        for first in ("portFirst", "starboardFirst"):
            if first in zz:
                n_os = len(zz[first]["overshoots_deg"])
                z = zigzag(ship, 10.0, 10.0, first.replace("First", ""), zz["approachSpeed_kn"], n_overshoots=n_os)
                for i in range(n_os):
                    sim[f"zigzag10_10.{first}.overshoot{i + 1}"] = z.overshoots_deg[i] if i < len(z.overshoots_deg) else 60.0
    return sim


def residual_vector(sim: dict[str, float], targets: list[Target]) -> np.ndarray:
    res = []
    for t in targets:
        v = sim.get(t.key, math.nan)
        if v is None or math.isnan(v):
            v = t.value * 3.0 if t.value != 0 else 100.0
        res.append(t.weight * (v - t.value) / t.tolerance)
    return np.array(res)


# ---------------------------------------------------------------------------
# 工作行程(有限差分平行)
# ---------------------------------------------------------------------------
_WORKER: dict[str, Any] = {}


def _worker_init(ship_id: str, loading: str, base: dict[str, Any], names: list[str], tt: dict[str, Any], dt: float) -> None:
    _WORKER["sp"] = load_particulars(ship_id, loading)
    _WORKER["base"] = base
    _WORKER["names"] = names
    _WORKER["tt"] = tt
    _WORKER["dt"] = dt
    _WORKER["targets"] = build_targets(tt, _WORKER["sp"].lpp)


def _worker_eval(values: list[float]) -> dict[str, float]:
    c = apply_params(_WORKER["base"], _WORKER["names"], values)
    ship = MMGShip(_WORKER["sp"], c, dt=_WORKER["dt"])
    return simulate_targets(ship, _WORKER["tt"], _WORKER["targets"])


class Objective:
    """殘差函式與平行 Jacobian。"""

    def __init__(self, ship_id: str, loading: str, base: dict[str, Any], names: list[str], tt: dict[str, Any],
                 dt: float, prior_weight: float, workers: int) -> None:
        self.names = names
        self.base = base
        self.tt = tt
        self.sp = load_particulars(ship_id, loading)
        self.targets = build_targets(tt, self.sp.lpp)
        self.p0 = np.array([_get(base, PARAM_SPECS[n][0]) if n != "R0scale" else 1.0 for n in names])
        self.lb = np.array([PARAM_SPECS[n][1] for n in names])
        self.ub = np.array([PARAM_SPECS[n][2] for n in names])
        sig = []
        for n, p in zip(names, self.p0):
            s = PARAM_SPECS[n][3]
            sig.append(abs(p) * float(str(s)[3:]) if isinstance(s, str) else float(s))
        self.sigma = np.array([max(x, 1e-3) for x in sig])
        self.prior_weight = prior_weight
        self.dt = dt
        self.n_eval = 0
        self.workers = max(1, workers)
        self.pool: ProcessPoolExecutor | None = None
        if self.workers > 1:
            self.pool = ProcessPoolExecutor(max_workers=self.workers, initializer=_worker_init,
                                            initargs=(ship_id, loading, base, names, tt, dt))
        else:
            _worker_init(ship_id, loading, base, names, tt, dt)

    def close(self) -> None:
        if self.pool is not None:
            self.pool.shutdown()
            self.pool = None

    def _evaluate_many(self, vectors: list[np.ndarray]) -> list[dict[str, float]]:
        self.n_eval += len(vectors)
        if self.pool is None:
            return [_worker_eval(list(map(float, v))) for v in vectors]
        return list(self.pool.map(_worker_eval, [list(map(float, v)) for v in vectors]))

    def full_residual(self, sim: dict[str, float], x: np.ndarray) -> np.ndarray:
        data = residual_vector(sim, self.targets)
        prior = self.prior_weight * (x - self.p0) / self.sigma
        return np.concatenate([data, prior])

    def __call__(self, x: np.ndarray) -> np.ndarray:
        sim = self._evaluate_many([x])[0]
        return self.full_residual(sim, x)

    def jac(self, x: np.ndarray) -> np.ndarray:
        """前向差分 Jacobian,各欄平行計算。"""
        h = np.maximum(1e-3 * np.abs(x), 1e-4)
        vecs = [x] + [x + np.eye(len(x))[i] * h[i] for i in range(len(x))]
        sims = self._evaluate_many(vecs)
        f0 = self.full_residual(sims[0], x)
        cols = []
        for i in range(len(x)):
            fi = self.full_residual(sims[i + 1], vecs[i + 1])
            cols.append((fi - f0) / h[i])
        return np.array(cols).T


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def identify(ship_id: str, loading: str = "full", params: list[str] | None = None, dt: float = 0.1,
             max_nfev: int = 40, prior_weight: float = 0.3, workers: int | None = None, verbose: bool = True,
             base: dict[str, Any] | None = None) -> dict[str, Any]:
    """執行識別並回傳含診斷資訊的係數字典(呼叫者負責寫檔)。"""
    names = list(params or DEFAULT_PARAMS)
    sp = load_particulars(ship_id, loading)
    tt = load_trial_targets(ship_id)
    base = copy.deepcopy(base) if base is not None else estimate_coefficients(sp, tt)
    if workers is None:
        workers = min(len(names) + 1, os.cpu_count() or 1)
    obj = Objective(ship_id, loading, base, names, tt, dt, prior_weight, workers)
    x0 = np.clip(obj.p0, obj.lb, obj.ub)
    if verbose:
        print(f"[identify] {ship_id} {loading}: {len(obj.targets)} 個目標、{len(names)} 個參數、dt={dt}s、workers={workers}")
        for n, p, lo, hi in zip(names, x0, obj.lb, obj.ub):
            print(f"   {n:12s} x0={p:9.4f}  [{lo}, {hi}]")
    f0 = obj(x0)
    cost0 = 0.5 * float(np.sum(f0[: len(obj.targets)] ** 2))

    def cb_print(xk: np.ndarray, fk: np.ndarray) -> None:
        pass

    sol = least_squares(obj, x0, jac=obj.jac, bounds=(obj.lb, obj.ub), method="trf", x_scale="jac",
                        max_nfev=max_nfev, ftol=1e-4, xtol=1e-4, gtol=1e-6, verbose=2 if verbose else 0)
    x = sol.x
    sim = obj._evaluate_many([x])[0]
    obj.close()
    data_res = residual_vector(sim, obj.targets)
    cost1 = 0.5 * float(np.sum(data_res**2))
    coeffs = apply_params(base, names, x)
    rows = []
    n_in = 0
    for t, r in zip(obj.targets, data_res):
        v = sim.get(t.key, math.nan)
        ok = abs(v - t.value) <= t.tolerance
        n_in += int(ok)
        rows.append({"target": t.key, "trial": t.value, "sim": v, "tolerance": t.tolerance,
                     "deviation": v - t.value, "deviation_pct": 100.0 * (v - t.value) / t.value if t.value else None,
                     "withinTolerance": ok})
    coeffs["source"]["method"] = "identify"
    coeffs["source"]["trialTargets"] = f"data/ships/{ship_id}/trial_targets.json"
    coeffs["source"]["identifiedParameters"] = names
    coeffs["source"]["identification"] = {
        "date": _dt.date.today().isoformat(),
        "toolVersion": __version__,
        "dt_s": dt,
        "priorWeight": prior_weight,
        "optimizer": {"method": "scipy least_squares trf", "nfev": int(sol.nfev), "status": int(sol.status),
                      "message": str(sol.message), "evaluations": obj.n_eval},
        "cost": {"initial": cost0, "final": cost1},
        "parameters": {n: {"initial": float(p0), "final": float(xf), "lower": float(lo), "upper": float(hi)}
                       for n, p0, xf, lo, hi in zip(names, obj.p0, x, obj.lb, obj.ub)},
        "targets": rows,
        "withinTolerance": f"{n_in}/{len(rows)}",
    }
    coeffs["generatedAt"] = _dt.date.today().isoformat()
    if verbose:
        print(f"[identify] cost {cost0:.3f} → {cost1:.3f};在公差內 {n_in}/{len(rows)}")
        for r in rows:
            flag = "OK " if r["withinTolerance"] else "OUT"
            print(f"   {flag} {r['target']:40s} trial={r['trial']:8.2f} sim={r['sim']:8.2f} tol=±{r['tolerance']:.2f}")
    return coeffs


def identify_and_save(ship_id: str, loading: str = "full", **kw: Any) -> dict[str, Any]:
    coeffs = identify(ship_id, loading, **kw)
    path = paths.coefficients_path(ship_id, loading)
    save_coefficients(coeffs, path)
    if kw.get("verbose", True):
        print(f"[identify] 已寫入 {path}")
    return coeffs
