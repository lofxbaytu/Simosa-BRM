"""潮高模型(情境 ``environment.tide``;docs/design/scenario-engine.md 第 1.5.1 節;C# ``src/SimCore/Environment/TideModel.cs`` 同公式)。

潮高(m)為練習時間 t(秒,自練習開始)的純函數;有效水深 = 基底水深(海圖水深,``setEnvironment waterDepth`` 仍改基底)+ 潮高(t)。

- ``table``:``[{t | utc, height_m}]`` 線性內插,範圍外取端點;``utc`` 於 ``scenario.validate_scenario`` 換算為相對情境 ``startTimeUtc`` 的秒數。
- ``harmonic``:``meanLevel_m + Σ amplitude_m·cos(2π·t/period_s + phase_deg)``。

運算順序與 C# 相同(``h0 + (h1 - h0) * (t - t0) / (t1 - t0)``、``a * cos(2π·t / T + φ)``),使跨語言夾具比對到 1e-9。
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

DEG = math.pi / 180.0


class TideModel:
    """潮高模型基底:``height_at(t)``。"""

    def height_at(self, t: float) -> float:  # pragma: no cover - 抽象
        raise NotImplementedError


@dataclass(frozen=True)
class TideTable(TideModel):
    """潮高表:(t, 高度) 線性內插,t 在範圍外取端點值(至少 1 點;結構檢查要求 ≥ 2 點且 t 嚴格遞增)。"""

    points: tuple[tuple[float, float], ...]

    def __post_init__(self) -> None:
        if not self.points:
            raise ValueError("潮高表至少需要 1 點")

    def height_at(self, t: float) -> float:
        pts = self.points
        if t <= pts[0][0]:
            return pts[0][1]
        t_last, h_last = pts[-1]
        if t >= t_last:
            return h_last
        for i in range(1, len(pts)):
            t1, h1 = pts[i]
            if t > t1:
                continue
            t0, h0 = pts[i - 1]
            return h0 + (h1 - h0) * (t - t0) / (t1 - t0)
        return h_last


@dataclass(frozen=True)
class HarmonicTide(TideModel):
    """調和潮:高度 = mean_level + Σ amplitude·cos(2π·t/period + phase)(phase 為弧度)。"""

    mean_level: float
    constituents: tuple[tuple[float, float, float], ...]  # (amplitude_m, period_s, phase_rad)

    def height_at(self, t: float) -> float:
        h = self.mean_level
        for a, period, phase in self.constituents:
            h += a * math.cos(2.0 * math.pi * t / period + phase)
        return h


def tide_from_scenario(tide: dict[str, Any] | None) -> TideModel | None:
    """由正規化後的情境 ``environment.tide``(``scenario.validate_scenario`` 的輸出:table 的點已為 ``{t, height_m}``)建立模型;None → None。"""
    if not tide:
        return None
    table = tide.get("table")
    if table:
        return TideTable(tuple((float(p["t"]), float(p["height_m"])) for p in table))
    harmonic = tide.get("harmonic")
    if harmonic:
        return HarmonicTide(float(harmonic.get("meanLevel_m") or 0.0),
                            tuple((float(c["amplitude_m"]), float(c["period_s"]), float(c.get("phase_deg") or 0.0) * DEG)
                                  for c in harmonic.get("constituents") or []))
    return None
