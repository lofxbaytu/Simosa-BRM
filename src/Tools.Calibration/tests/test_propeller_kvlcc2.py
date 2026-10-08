"""螺槳多項式與 MMG 實作本身的回歸測試(不依賴兩船的經驗係數)。

- Wageningen B 系列:已知開水特性範圍(B4-70 P/D 1.0:K_T0 ≈ 0.45、J0 ≈ 1.0、η_max ≈ 0.65–0.70)。
- KVLCC2(Yasukawa & Yoshimura 2015 係數)35° 迴旋與 Z 形落在 SIMMAN 公開結果的寬鬆範圍(規劃書 6.3)。
"""

from __future__ import annotations

import pytest

from simosa_brm.coefficients import kvlcc2_coefficients
from simosa_brm.manoeuvres import turning_circle, zigzag
from simosa_brm.mmg import MMGShip
from simosa_brm.particulars import load_particulars
from simosa_brm.propeller import kq_bseries, kt_bseries, open_water_efficiency, zero_thrust_j


@pytest.mark.parametrize("z,ear,pd,kt0,j0,eta", [
    (4, 0.70, 1.0, (0.42, 0.48), (0.98, 1.10), (0.62, 0.72)),
    (4, 0.55, 0.7, (0.26, 0.32), (0.70, 0.82), (0.56, 0.66)),
    (5, 0.75, 1.0, (0.43, 0.50), (0.98, 1.10), (0.62, 0.72)),
])
def test_bseries_open_water(z, ear, pd, kt0, j0, eta) -> None:
    assert kt0[0] <= kt_bseries(0.0, pd, ear, z) <= kt0[1]
    assert j0[0] <= zero_thrust_j(pd, ear, z) <= j0[1]
    assert eta[0] <= max(open_water_efficiency(j / 100.0, pd, ear, z) for j in range(0, 150)) <= eta[1]
    assert kq_bseries(0.0, pd, ear, z) > 0.0


@pytest.fixture(scope="module")
def kvlcc2() -> MMGShip:
    return MMGShip(load_particulars("FSB1", "full"), kvlcc2_coefficients(), dt=0.1)


def test_kvlcc2_turning_circle(kvlcc2: MMGShip) -> None:
    L = 320.0
    for side in ("port", "starboard"):
        r = turning_circle(kvlcc2, 35.0, side, 15.5)
        assert r.completed
        assert 2.5 <= r.advance_m / L <= 3.7, f"{side} advance/L={r.advance_m / L:.2f}"
        assert 2.4 <= r.tactical_diameter_m / L <= 3.7, f"{side} TD/L={r.tactical_diameter_m / L:.2f}"
        assert 0.25 <= r.steady_speed_kn / 15.5 <= 0.5


def test_kvlcc2_zigzag(kvlcc2: MMGShip) -> None:
    z = zigzag(kvlcc2, 10.0, 10.0, "starboard", 15.5, n_overshoots=2)
    assert z.completed
    assert 4.0 <= z.overshoots_deg[0] <= 12.0
    assert 12.0 <= z.overshoots_deg[1] <= 28.0, "KVLCC2 第二超越角(方向不穩定船)應明顯大於第一"
