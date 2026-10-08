"""螺槳:Wageningen B 系列開水特性多項式(Oosterveld & van Oossanen 1975)與簡化四象限推力(規劃書 6.2)。

- 第一象限(正車、前進)用 B 系列 K_T(J)、K_Q(J) 多項式(依葉數 Z、盤面比 A_E/A_0、螺距比 P/D)。
- 倒車(n<0)與倒退(u<0):以同一多項式在負 J 區外推並乘以倒車折減係數,逾 ±J_clip 保持常數;
  完整 Wageningen 四象限 C_T*(β) 傅立葉表尚未納入(README「限制」)。
- 軸停止時(|n| 小)螺槳視為鎖定圓盤,阻力 = C_D·½ρ(u(1-w))²·πD²/4,隨 |n| 線性淡出。
"""

from __future__ import annotations

import math
from dataclasses import dataclass

# (係數, J 次方 s, P/D 次方 t, A_E/A_0 次方 u, Z 次方 v)
# 註:Z 次方欄依 B4-70、B4-55、B5-75、B3-50 的開水特性(K_T0、零推力 J、最大效率)核對過
_KT_TERMS = [
    (+0.00880496, 0, 0, 0, 0), (-0.204554, 1, 0, 0, 0), (+0.166351, 0, 1, 0, 0), (+0.158114, 0, 2, 0, 0),
    (-0.147581, 2, 0, 1, 0), (-0.481497, 1, 1, 1, 0), (+0.415437, 0, 2, 1, 0), (+0.0144043, 0, 0, 0, 1),
    (-0.0530054, 2, 0, 0, 1), (+0.0143481, 0, 1, 0, 1), (+0.0606826, 1, 1, 0, 1), (-0.0125894, 0, 0, 1, 1),
    (+0.0109689, 1, 0, 1, 1), (-0.133698, 0, 3, 0, 0), (+0.00638407, 0, 6, 0, 0), (-0.00132718, 2, 6, 0, 0),
    (+0.168496, 3, 0, 1, 0), (-0.0507214, 0, 0, 2, 0), (+0.0854559, 2, 0, 2, 0), (-0.0504475, 3, 0, 2, 0),
    (+0.010465, 1, 6, 2, 0), (-0.00648272, 2, 6, 2, 0), (-0.00841728, 0, 3, 0, 1), (+0.0168424, 1, 3, 0, 1),
    (-0.00102296, 3, 3, 0, 1), (-0.0317791, 0, 3, 1, 1), (+0.018604, 1, 0, 2, 1), (-0.00410798, 0, 2, 2, 1),
    (-0.000606848, 0, 0, 0, 2), (-0.0049819, 1, 0, 0, 2), (+0.0025983, 2, 0, 0, 2), (-0.000560528, 3, 0, 0, 2),
    (-0.00163652, 1, 2, 0, 2), (-0.000328787, 1, 6, 0, 2), (+0.000116502, 2, 6, 0, 2), (+0.000690904, 0, 0, 1, 2),
    (+0.00421749, 0, 3, 1, 2), (+5.65229e-05, 3, 6, 1, 2), (-0.00146564, 0, 3, 2, 2),
]

_KQ_TERMS = [
    (+0.00379368, 0, 0, 0, 0), (+0.00886523, 2, 0, 0, 0), (-0.032241, 1, 1, 0, 0), (+0.00344778, 0, 2, 0, 0),
    (-0.0408811, 0, 1, 1, 0), (-0.108009, 1, 1, 1, 0), (-0.0885381, 2, 1, 1, 0), (+0.188561, 0, 2, 1, 0),
    (-0.00370871, 1, 0, 0, 1), (+0.00513696, 0, 1, 0, 1), (+0.0209449, 1, 1, 0, 1), (+0.00474319, 2, 1, 0, 1),
    (-0.00723408, 2, 0, 1, 1), (+0.00438388, 1, 1, 1, 1), (-0.0269403, 0, 2, 1, 1), (+0.0558082, 3, 0, 1, 0),
    (+0.0161886, 0, 3, 1, 0), (+0.00318086, 1, 3, 1, 0), (+0.015896, 0, 0, 2, 0), (+0.0471729, 1, 0, 2, 0),
    (+0.0196283, 3, 0, 2, 0), (-0.0502782, 0, 1, 2, 0), (-0.030055, 3, 1, 2, 0), (+0.0417122, 2, 2, 2, 0),
    (-0.0397722, 0, 3, 2, 0), (-0.00350024, 0, 6, 2, 0), (-0.0106854, 3, 0, 0, 1), (+0.00110903, 3, 3, 0, 1),
    (-0.000313912, 0, 6, 0, 1), (+0.0035985, 3, 0, 1, 1), (-0.00142121, 0, 6, 1, 1), (-0.00383637, 1, 0, 2, 1),
    (+0.0126803, 0, 2, 2, 1), (-0.00318278, 2, 3, 2, 1), (+0.00334268, 0, 6, 2, 1), (-0.00183491, 1, 1, 0, 2),
    (+0.000112451, 3, 2, 0, 2), (-0.0000297228, 3, 6, 0, 2), (+0.000269551, 1, 0, 1, 2), (+0.00083265, 2, 0, 1, 2),
    (+0.00155334, 0, 2, 1, 2), (+0.000302683, 0, 6, 1, 2), (-0.0001843, 0, 0, 2, 2), (-0.000425399, 0, 3, 2, 2),
    (+0.0000869243, 3, 3, 2, 2), (-0.0004659, 0, 6, 2, 2), (+0.0000554194, 1, 6, 2, 2),
]


def _poly(terms, j: float, pd: float, ear: float, z: int) -> float:
    s = 0.0
    for c, sj, tp, ue, vz in terms:
        s += c * (j**sj) * (pd**tp) * (ear**ue) * (z**vz)
    return s


def kt_bseries(j: float, pd: float, ear: float, z: int) -> float:
    """Wageningen B 系列推力係數 K_T(J)。適用 2≤Z≤7、0.3≤A_E/A_0≤1.05、0.5≤P/D≤1.4。"""
    return _poly(_KT_TERMS, j, pd, ear, z)


def kq_bseries(j: float, pd: float, ear: float, z: int) -> float:
    """Wageningen B 系列轉矩係數 K_Q(J)。"""
    return _poly(_KQ_TERMS, j, pd, ear, z)


def fit_quadratic(fn, j_max: float, n_pts: int = 25) -> tuple[float, float, float]:
    """以最小平方法把 K(J) 在 [0, j_max] 擬合為 k0 + k1 J + k2 J²(MMG 慣用形式)。"""
    xs = [j_max * i / (n_pts - 1) for i in range(n_pts)]
    ys = [fn(x) for x in xs]
    # 正規方程(3×3)
    import numpy as np

    a = np.vstack([np.ones(n_pts), xs, np.square(xs)]).T
    sol, *_ = np.linalg.lstsq(a, np.array(ys), rcond=None)
    return float(sol[0]), float(sol[1]), float(sol[2])


def zero_thrust_j(pd: float, ear: float, z: int) -> float:
    """K_T 為零的進速係數(二分法)。"""
    lo, hi = 0.0, 1.6
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        if kt_bseries(mid, pd, ear, z) > 0:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


@dataclass
class PropellerModel:
    """模擬用的螺槳推力/轉矩模型(由係數檔建立)。"""

    diameter: float
    kt: tuple[float, float, float]  # k0, k1, k2
    kq: tuple[float, float, float]
    astern_factor: float = 0.85  # 倒車推力折減(B 系列背面不對稱)
    j_clip: float = 0.9  # 負 J 外推的截止
    locked_cd: float = 0.35  # 鎖定螺槳以盤面積計的阻力係數
    lock_rps: float = 0.3  # |n| 低於此值時逐漸切換為鎖定圓盤
    side_force_factor: float = 0.08  # 倒車時橫向力/推力(右旋槳:艉向左)

    def kt_at(self, j: float) -> float:
        k0, k1, k2 = self.kt
        return k0 + k1 * j + k2 * j * j

    def kq_at(self, j: float) -> float:
        k0, k1, k2 = self.kq
        return k0 + k1 * j + k2 * j * j

    def thrust_torque(self, n: float, va: float, rho: float) -> tuple[float, float, float, float]:
        """回傳 (推力 T [N], 轉矩 Q [N·m], 有效 J, 有效 K_T);n 為 rps(倒車負),va 為螺槳進速(m/s)。

        四象限簡化:n>0 用 B 系列多項式(J 可為負並截止);n<0 以 |n| 計、J 取反向並乘倒車折減;
        |n| 小時混入鎖定螺槳阻力。
        """
        d = self.diameter
        an = abs(n)
        if an < 1e-9:
            t_locked = -self.locked_cd * 0.5 * rho * va * abs(va) * math.pi * d * d / 4.0
            return t_locked, 0.0, 0.0, 0.0
        j = va / (an * d)
        if n > 0:
            je = max(-self.j_clip, min(j, 1.5))
            kt = self.kt_at(je)
            kq = self.kq_at(je)
            t = rho * n * n * d**4 * kt
            q = rho * n * n * d**5 * kq
        else:
            # 倒車:船前進時 j>0 → 對倒轉的螺槳而言是負進速係數,推力(向後)隨前進速度增大
            je = max(-self.j_clip, min(-j, 1.5))
            kt = self.kt_at(je) * self.astern_factor
            kq = self.kq_at(je) * self.astern_factor
            t = -rho * n * n * d**4 * kt
            q = -rho * n * n * d**5 * kq
        if an < self.lock_rps:
            wgt = 1.0 - an / self.lock_rps
            t_locked = -self.locked_cd * 0.5 * rho * va * abs(va) * math.pi * d * d / 4.0
            t = (1.0 - wgt) * t + wgt * t_locked
        return t, q, j, kt


def open_water_efficiency(j: float, pd: float, ear: float, z: int) -> float:
    kq = kq_bseries(j, pd, ear, z)
    if kq <= 0:
        return 0.0
    return j * kt_bseries(j, pd, ear, z) / (2.0 * math.pi * kq)
