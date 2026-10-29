"""時變/空間變化流場(情境 ``environment.current.table`` | ``tidal`` | ``field``;docs/design/scenario-engine.md 第 1.5.2 節;
C# ``src/SimCore/Environment/CurrentModel.cs`` 同公式;需求 R3「隨時間變化的流場」)。

流向(去向)與流速為練習時間 t(秒)的純函數(均勻模型 table/tidal),或再加上位置 (x, y) 的純函數(空間網格 field);
``MMGShip`` 每步以 ``_refresh_current()`` 以自船位置寫入 ``env.current_set`` / ``env.current_speed``,其他位置以
``MMGShip.current_en_at(t, x, y)`` 取樣;``set_environment`` 給 current 後時變/空間變化停止(``current_overridden``),直到再 ``apply_scenario``。

- ``table``:``[{t | utc, speed_kn, direction_deg}]`` 以東/北分量線性內插(方向跨 360° 不繞遠路),範圍外取端點;
  輸出 set = atan2(E, N)、drift = √(E²+N²)。
- ``tidal``:``{flood: {speed_kn, direction_deg}, ebb: {...}, slackWindow_min}``:rate = 潮高 dH/dt ≥ 0 → 漲潮向/速,否則退潮;
  流速 = 該相速度 × scale,scale 由 ratio = |rate| / max_rate 經平潮門檻 s = sin(π·slackWindow/T_ref) 線性映射
  (ratio ≤ s → 0,否則 (ratio − s)/(1 − s));T_ref 為潮高模型的參考週期(``tide.reference_period_s``)。

- ``field``:``{grid: {x0, y0, dx, dy, nx, ny}, u_kn: [[ny × nx]], v_kn: [[ny × nx]], outside: edge | zero, timeScale: none | tidal | table}``:
  規則網格上的東/北分量(kn)雙線性內插(``u_kn[j][i]`` 對應點 (x0 + i·dx, y0 + j·dy):外層列為 y 索引、內層行為 x 索引),格外依 outside
  取最近邊緣值或 0;timeScale 為 tidal/table 時整個場乘以該均勻模型的流速比例 k(t) = 當下流速 / 最大流速(table 取各點最大、tidal 取
  max(flood, ebb);夾到 [0, 1]),**方向永遠取場的向量**。有 field 時 table/tidal 只作為 timeScale 的來源。

運算順序與 C# 相同(``e0 + (e1 - e0) * f``、``(ratio - s) / (1.0 - s)``、``a + (b - a) * ty``),使跨語言夾具(Golden/current_*.csv)比對到 1e-9。
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

from .tide import DEG, TideModel

KN_TO_MPS = 1852.0 / 3600.0
TWO_PI = 2.0 * math.pi


def _norm_rad(a: float) -> float:
    """航向正規化到 [0, 2π)(C# Units.NormalizeHeadingRad)。"""
    a = math.fmod(a, TWO_PI)
    if a < 0.0:
        a += TWO_PI
    return 0.0 if a >= TWO_PI else a


class CurrentModel:
    """時變流場基底:``source``(state.currentSource)、``spatially_varying``(field 為 True)與 ``at(t, x, y)`` → (set_rad, drift_mps);
    均勻模型忽略位置(x, y 預設 0;C# ``At(t)`` / ``At(t, x, y)``),空間流場省略位置時取原點。"""

    source = ""
    spatially_varying = False

    def at(self, t: float, x: float = 0.0, y: float = 0.0) -> tuple[float, float]:  # pragma: no cover - 抽象
        raise NotImplementedError


@dataclass(frozen=True)
class CurrentTable(CurrentModel):
    """流場表:(t, 東分量 m/s, 北分量 m/s) 向量線性內插,t 在範圍外取端點值(至少 1 點;結構檢查要求 ≥ 2 點且 t 嚴格遞增)。"""

    points: tuple[tuple[float, float, float], ...]
    source = "table"

    def __post_init__(self) -> None:
        if not self.points:
            raise ValueError("流場表至少需要 1 點")

    def vector_at(self, t: float) -> tuple[float, float]:
        pts = self.points
        if t <= pts[0][0]:
            return pts[0][1], pts[0][2]
        t_last, e_last, n_last = pts[-1]
        if t >= t_last:
            return e_last, n_last
        for i in range(1, len(pts)):
            t1, e1, n1 = pts[i]
            if t > t1:
                continue
            t0, e0, n0 = pts[i - 1]
            f = (t - t0) / (t1 - t0)
            return e0 + (e1 - e0) * f, n0 + (n1 - n0) * f
        return e_last, n_last

    @property
    def max_speed_mps(self) -> float:
        """各表點流速的最大值(m/s);向量線性內插的流速不會超過此值(空間流場 timeScale table 的比例分母;C# MaxSpeedMps)。"""
        mx = 0.0
        for _, e, n in self.points:
            sp = math.sqrt(e * e + n * n)
            if sp > mx:
                mx = sp
        return mx

    def at(self, t: float, x: float = 0.0, y: float = 0.0) -> tuple[float, float]:
        e, n = self.vector_at(t)
        return _norm_rad(math.atan2(e, n)), math.sqrt(e * e + n * n)


@dataclass(frozen=True)
class TidalCurrent(CurrentModel):
    """潮流:由潮高模型的變化率決定漲/退與流速比例(C# TidalCurrent)。"""

    tide: TideModel
    flood_set_rad: float
    flood_mps: float
    ebb_set_rad: float
    ebb_mps: float
    slack_window_s: float
    source = "tidal"

    @staticmethod
    def threshold(slack_window_s: float, reference_period_s: float) -> float:
        """s = sin(π·W/T);W ≥ T/2 → 1(全程平潮);T ≤ 0 或 W ≤ 0 → 0。"""
        if slack_window_s <= 0.0 or reference_period_s <= 0.0:
            return 0.0
        x = math.pi * slack_window_s / reference_period_s
        return 1.0 if x >= math.pi / 2.0 else math.sin(x)

    @property
    def slack_threshold(self) -> float:
        return self.threshold(max(0.0, self.slack_window_s), self.tide.reference_period_s)

    def scale_at(self, t: float) -> tuple[float, bool]:
        """t 的流速縮放係數 [0, 1] 與相位(漲 True / 退 False)。"""
        rate = self.tide.rate_at(t)
        mx = self.tide.max_rate
        ratio = abs(rate) / mx if mx > 0.0 else 0.0
        s = self.slack_threshold
        scale = 0.0 if (s >= 1.0 or ratio <= s) else (ratio - s) / (1.0 - s)
        if scale > 1.0:
            scale = 1.0
        return scale, rate >= 0.0

    def at(self, t: float, x: float = 0.0, y: float = 0.0) -> tuple[float, float]:
        scale, flood = self.scale_at(t)
        if flood:
            return _norm_rad(self.flood_set_rad), max(0.0, self.flood_mps) * scale
        return _norm_rad(self.ebb_set_rad), max(0.0, self.ebb_mps) * scale


@dataclass(frozen=True)
class CurrentField(CurrentModel):
    """空間變化流場(情境 ``environment.current.field``;C# CurrentField 逐式對應):規則網格的東/北分量(m/s,列優先一維 tuple,
    索引 j·nx + i,j 為 y 索引、i 為 x 索引)雙線性內插;``outside_zero`` 時格外為 0,否則取最近邊緣值;``scaler``(table | tidal 模型,
    None = none)給流速比例 k(t) = scaler 當下流速 / ``scaler_max_mps``(夾到 [0, 1];最大流速 0 → 0),方向永遠取場的向量。"""

    x0: float
    y0: float
    dx: float
    dy: float
    nx: int
    ny: int
    u: tuple[float, ...]
    v: tuple[float, ...]
    outside_zero: bool = False
    scaler: CurrentModel | None = None
    spatially_varying = True

    def __post_init__(self) -> None:
        if not (self.dx > 0.0) or not (self.dy > 0.0):
            raise ValueError("流場網格的 dx/dy 必須為正")
        if self.nx < 2 or self.ny < 2:
            raise ValueError("流場網格的 nx/ny 必須 ≥ 2")
        if len(self.u) != self.nx * self.ny or len(self.v) != self.nx * self.ny:
            raise ValueError("流場網格的 u/v 長度必須為 nx·ny")
        if self.scaler is not None and not isinstance(self.scaler, (CurrentTable, TidalCurrent)):
            raise ValueError("timeScale 只能為 table 或 tidal 模型")

    @property
    def source(self) -> str:  # type: ignore[override]
        return "field" if self.scaler is None else "field+" + self.scaler.source

    @property
    def scaler_max_mps(self) -> float:
        """縮放來源的最大流速(m/s;table 取各點流速最大值、tidal 取 max(flood, ebb);無 scaler 時 0)。C# ScalerMaxMps。"""
        sc = self.scaler
        if isinstance(sc, CurrentTable):
            return sc.max_speed_mps
        if isinstance(sc, TidalCurrent):
            return max(sc.flood_mps, sc.ebb_mps)
        return 0.0

    def scale_at(self, t: float) -> float:
        """t 的流速比例 k ∈ [0, 1](none → 1;最大流速 0 → 0)。C# ScaleAt 相同。"""
        if self.scaler is None:
            return 1.0
        mx = self.scaler_max_mps
        if not mx > 0.0:
            return 0.0
        k = self.scaler.at(t)[1] / mx
        return 1.0 if k > 1.0 else k

    def interpolate(self, x: float, y: float) -> tuple[float, float]:
        """位置 (x, y) 未經時間縮放的東/北分量(m/s):雙線性內插、格外依 outside_zero。C# Interpolate 相同運算順序。"""
        fx = (x - self.x0) / self.dx
        fy = (y - self.y0) / self.dy
        max_x = float(self.nx - 1)
        max_y = float(self.ny - 1)
        if self.outside_zero and (fx < 0.0 or fx > max_x or fy < 0.0 or fy > max_y):
            return 0.0, 0.0
        if fx < 0.0:
            fx = 0.0
        elif fx > max_x:
            fx = max_x
        if fy < 0.0:
            fy = 0.0
        elif fy > max_y:
            fy = max_y
        i = int(math.floor(fx))
        if i > self.nx - 2:
            i = self.nx - 2
        j = int(math.floor(fy))
        if j > self.ny - 2:
            j = self.ny - 2
        tx = fx - i
        ty = fy - j
        k00 = j * self.nx + i
        k10 = k00 + 1
        k01 = k00 + self.nx
        k11 = k01 + 1
        u, v = self.u, self.v
        ua = u[k00] + (u[k10] - u[k00]) * tx
        ub = u[k01] + (u[k11] - u[k01]) * tx
        va = v[k00] + (v[k10] - v[k00]) * tx
        vb = v[k01] + (v[k11] - v[k01]) * tx
        return ua + (ub - ua) * ty, va + (vb - va) * ty

    def vector_at(self, t: float, x: float, y: float) -> tuple[float, float]:
        """t、位置 (x, y) 的東/北分量(m/s)= 內插值 × k(t)(none 時不乘)。C# VectorAt 相同。"""
        u, v = self.interpolate(x, y)
        if self.scaler is None:
            return u, v
        k = self.scale_at(t)
        return u * k, v * k

    def at(self, t: float, x: float = 0.0, y: float = 0.0) -> tuple[float, float]:
        e, n = self.vector_at(t, x, y)
        return _norm_rad(math.atan2(e, n)), math.sqrt(e * e + n * n)


def current_from_scenario(current: dict[str, Any] | None, tide: TideModel | None) -> CurrentModel | None:
    """由正規化後的情境 ``environment.current``(``scenario.validate_scenario`` 的輸出:table 的點已為 ``{t, speed_kn, direction_deg}``,
    tidal 為 ``{flood, ebb, slackWindow_min}``,field 為 ``{grid, u_kn, v_kn, outside, timeScale}``)建立模型;皆無 → None(常數流)。
    有 field 時回傳 ``CurrentField``(table/tidal 只作為 timeScale 的來源);tidal 而無潮高模型、timeScale 無對應模型時 ValueError(結構檢查已擋)。
    C# ``CurrentModel.FromScenario``。"""
    if not current:
        return None
    uniform = _uniform_from_scenario(current, tide)
    field = current.get("field")
    if field:
        return _field_from_scenario(field, uniform)
    return uniform


def _field_from_scenario(field: dict[str, Any], uniform: CurrentModel | None) -> CurrentField:
    """``field`` 區段 → ``CurrentField``(kn → m/s;C# CurrentField.FromScenario)。"""
    grid = field.get("grid")
    if not isinstance(grid, dict):
        raise ValueError("environment.current.field 需要 grid")
    nx, ny = int(grid["nx"]), int(grid["ny"])

    def flatten(rows: Any, name: str) -> tuple[float, ...]:
        if not isinstance(rows, list) or len(rows) != ny:
            raise ValueError(f"environment.current.field.{name} 須有 ny = {ny} 列")
        flat: list[float] = []
        for j, row in enumerate(rows):
            if not isinstance(row, list) or len(row) != nx:
                raise ValueError(f"environment.current.field.{name}[{j}] 須有 nx = {nx} 個值")
            flat.extend(float(v) * KN_TO_MPS for v in row)
        return tuple(flat)

    outside_zero = str(field.get("outside") or "edge").lower() == "zero"
    time_scale = str(field.get("timeScale") or "none").lower()
    scaler: CurrentModel | None
    if time_scale == "none":
        scaler = None
    elif time_scale == "tidal":
        if not isinstance(uniform, TidalCurrent):
            raise ValueError("environment.current.field.timeScale tidal 需要 current.tidal")
        scaler = uniform
    elif time_scale == "table":
        if not isinstance(uniform, CurrentTable):
            raise ValueError("environment.current.field.timeScale table 需要 current.table")
        scaler = uniform
    else:
        raise ValueError(f"environment.current.field.timeScale 須為 none | tidal | table:{field.get('timeScale')}")
    return CurrentField(float(grid["x0"]), float(grid["y0"]), float(grid["dx"]), float(grid["dy"]), nx, ny,
                        flatten(field.get("u_kn"), "u_kn"), flatten(field.get("v_kn"), "v_kn"), outside_zero, scaler)


def _uniform_from_scenario(current: dict[str, Any], tide: TideModel | None) -> CurrentModel | None:
    """均勻的時變模型(table | tidal);皆無 → None。"""
    table = current.get("table")
    if table:
        pts = []
        for p in table:
            speed = float(p["speed_kn"]) * KN_TO_MPS
            d = float(p["direction_deg"]) * DEG
            pts.append((float(p["t"]), speed * math.sin(d), speed * math.cos(d)))
        return CurrentTable(tuple(pts))
    tidal = current.get("tidal")
    if tidal:
        if tide is None:
            raise ValueError("environment.current.tidal 需要 environment.tide")
        flood, ebb = tidal["flood"], tidal["ebb"]
        return TidalCurrent(tide,
                            float(flood["direction_deg"]) * DEG, float(flood["speed_kn"]) * KN_TO_MPS,
                            float(ebb["direction_deg"]) * DEG, float(ebb["speed_kn"]) * KN_TO_MPS,
                            float(tidal.get("slackWindow_min") if tidal.get("slackWindow_min") is not None else 20.0) * 60.0)
    return None
