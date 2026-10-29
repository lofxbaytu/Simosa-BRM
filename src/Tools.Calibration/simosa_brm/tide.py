"""潮高模型(情境 ``environment.tide``;docs/design/scenario-engine.md 第 1.5.1 節;C# ``src/SimCore/Environment/TideModel.cs`` 同公式)。

潮高(m)為練習時間 t(秒,自練習開始)的純函數;有效水深 = 基底水深(海圖水深,``setEnvironment waterDepth`` 仍改基底)+ 潮高(t)。

- ``table``:``[{t | utc, height_m}]`` 線性內插,範圍外取端點;``utc`` 於 ``scenario.validate_scenario`` 換算為相對情境 ``startTimeUtc`` 的秒數。
- ``harmonic``:``meanLevel_m + Σ amplitude_m·cos(2π·t/period_s + phase_deg)``。

運算順序與 C# 相同(``h0 + (h1 - h0) * (t - t0) / (t1 - t0)``、``a * cos(2π·t / T + φ)``),使跨語言夾具比對到 1e-9。

潮高變化率 ``rate_at(t)``(dH/dt,m/s;漲為正)、``max_rate``(|dH/dt| 最大值)與 ``reference_period_s``(平潮門檻參考週期)
供潮流模型 ``current.TidalCurrent`` 使用(C# TideModel.RateAt / MaxRate / ReferencePeriodS):表為分段常數(範圍外 0),
調和潮為解析導數 Σ −A·(2π/T)·sin(2π·t/T + φ)、max_rate = Σ A·2π/T(上界)、參考週期取最大振幅分潮;表取 M2。
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any

DEG = math.pi / 180.0
M2_PERIOD_S = 44714.16  # M2 分潮週期(12.4206 h);潮高表的平潮門檻參考週期(C# TideModel.M2PeriodS)


class TideModel:
    """潮高模型基底:``height_at(t)``、``rate_at(t)``、``max_rate``、``reference_period_s``。"""

    def height_at(self, t: float) -> float:  # pragma: no cover - 抽象
        raise NotImplementedError

    def rate_at(self, t: float) -> float:  # pragma: no cover - 抽象
        raise NotImplementedError

    @property
    def max_rate(self) -> float:  # pragma: no cover - 抽象
        raise NotImplementedError

    @property
    def reference_period_s(self) -> float:  # pragma: no cover - 抽象
        raise NotImplementedError


@dataclass(frozen=True)
class TideTable(TideModel):
    """潮高表:(t, 高度) 線性內插,t 在範圍外取端點值(至少 1 點;結構檢查要求 ≥ 2 點且 t 嚴格遞增)。"""

    points: tuple[tuple[float, float], ...]
    # 各段斜率(m/s):slopes[i] 為 (i−1, i] 段,索引 0 未用(C# TideTable._slopes)
    slopes: tuple[float, ...] = field(init=False, repr=False, compare=False)

    def __post_init__(self) -> None:
        if not self.points:
            raise ValueError("潮高表至少需要 1 點")
        slopes = [0.0]
        for i in range(1, len(self.points)):
            t0, h0 = self.points[i - 1]
            t1, h1 = self.points[i]
            slopes.append((h1 - h0) / (t1 - t0))
        object.__setattr__(self, "slopes", tuple(slopes))

    @property
    def max_rate(self) -> float:
        return max((abs(s) for s in self.slopes[1:]), default=0.0)

    @property
    def reference_period_s(self) -> float:
        return M2_PERIOD_S

    def rate_at(self, t: float) -> float:
        """分段常數:t 在 [t0, tLast] 內取所在段(與 height_at 相同:第一個 t ≤ t_i 的段 (i−1, i]),範圍外 0。"""
        pts = self.points
        if len(pts) < 2 or t < pts[0][0] or t > pts[-1][0]:
            return 0.0
        for i in range(1, len(pts)):
            if t > pts[i][0]:
                continue
            return self.slopes[i]
        return 0.0

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

    @property
    def max_rate(self) -> float:
        """Σ A·2π/T(與 C# HarmonicTide 建構時的累加順序相同)。"""
        m = 0.0
        for a, period, _ in self.constituents:
            m += a * 2.0 * math.pi / period
        return m

    @property
    def reference_period_s(self) -> float:
        """最大振幅分潮的週期(相同時取先出現者);無分潮時 M2。"""
        ref_period, ref_amp = M2_PERIOD_S, -math.inf
        for a, period, _ in self.constituents:
            if a > ref_amp:
                ref_amp, ref_period = a, period
        return ref_period

    def rate_at(self, t: float) -> float:
        """解析導數:dH/dt = Σ −A·(2π/T)·sin(2π·t/T + φ)。"""
        rate = 0.0
        for a, period, phase in self.constituents:
            rate += -a * (2.0 * math.pi / period) * math.sin(2.0 * math.pi * t / period + phase)
        return rate

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
