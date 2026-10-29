"""時變流場(情境 ``environment.current.table`` | ``tidal``;docs/design/scenario-engine.md 第 1.5.2 節;
C# ``src/SimCore/Environment/CurrentModel.cs`` 同公式;需求 R3「隨時間變化的流場」)。

均勻流的流向(去向)與流速為練習時間 t(秒)的純函數,``MMGShip`` 每步以 ``_refresh_current()`` 寫入 ``env.current_set`` /
``env.current_speed``;``set_environment`` 給 current 後時變停止(``current_overridden``),直到再 ``apply_scenario``。

- ``table``:``[{t | utc, speed_kn, direction_deg}]`` 以東/北分量線性內插(方向跨 360° 不繞遠路),範圍外取端點;
  輸出 set = atan2(E, N)、drift = √(E²+N²)。
- ``tidal``:``{flood: {speed_kn, direction_deg}, ebb: {...}, slackWindow_min}``:rate = 潮高 dH/dt ≥ 0 → 漲潮向/速,否則退潮;
  流速 = 該相速度 × scale,scale 由 ratio = |rate| / max_rate 經平潮門檻 s = sin(π·slackWindow/T_ref) 線性映射
  (ratio ≤ s → 0,否則 (ratio − s)/(1 − s));T_ref 為潮高模型的參考週期(``tide.reference_period_s``)。

運算順序與 C# 相同(``e0 + (e1 - e0) * f``、``(ratio - s) / (1.0 - s)``),使跨語言夾具(Golden/current_*.csv)比對到 1e-9。
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
    """時變流場基底:``source``(state.currentSource)與 ``at(t)`` → (set_rad, drift_mps)。"""

    source = ""

    def at(self, t: float) -> tuple[float, float]:  # pragma: no cover - 抽象
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

    def at(self, t: float) -> tuple[float, float]:
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

    def at(self, t: float) -> tuple[float, float]:
        scale, flood = self.scale_at(t)
        if flood:
            return _norm_rad(self.flood_set_rad), max(0.0, self.flood_mps) * scale
        return _norm_rad(self.ebb_set_rad), max(0.0, self.ebb_mps) * scale


def current_from_scenario(current: dict[str, Any] | None, tide: TideModel | None) -> CurrentModel | None:
    """由正規化後的情境 ``environment.current``(``scenario.validate_scenario`` 的輸出:table 的點已為 ``{t, speed_kn, direction_deg}``,
    tidal 為 ``{flood, ebb, slackWindow_min}``)建立模型;皆無 → None(常數流)。tidal 而無潮高模型時 ValueError(結構檢查已擋)。"""
    if not current:
        return None
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
