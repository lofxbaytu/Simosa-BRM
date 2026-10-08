"""物理模型對抗式審查的回歸測試(規劃書 6.2、6.3;Yasukawa & Yoshimura 2015)。

每個測試對應審查清單的一項:(a) 運動方程式與 x_G、無因次化;(b) 船體多項式;(c) 螺槳;(d) 舵;(e) 正負慣例;
(g) 風;(h) 迴旋幾何;(i) Z 形超越角;(j) 識別只用 identification 區;以及審查時修正的錯誤
(倒退阻力方向、純橫移的虛假縱向力/艏搖力矩、零速艏搖阻尼、倒退中正車的舵流向、Blendermann 分母、Kijima Y'_r)。
"""

from __future__ import annotations

import copy
import math

import pytest

from simosa_brm.coefficients import hull_derivatives, kvlcc2_coefficients
from simosa_brm.identify import build_targets, simulate_targets
from simosa_brm.manoeuvres import thruster_turn, turning_circle, zigzag
from simosa_brm.mmg import ForceBreakdown, MMGShip, make_ship
from simosa_brm.particulars import KN_TO_MPS, load_particulars, load_trial_targets


@pytest.fixture(scope="module")
def fsb1_est() -> MMGShip:
    """FSB1 經驗係數(不依賴識別檔)。"""
    return make_ship("FSB1", "full", dt=0.02, prefer_file=False)


@pytest.fixture(scope="module")
def kvlcc2() -> MMGShip:
    return MMGShip(load_particulars("FSB1", "full"), kvlcc2_coefficients(), dt=0.1)


# ---------------------------------------------------------------------------
# (a) 運動方程式(式 (1)–(3))、x_G 耦合、無因次化
# ---------------------------------------------------------------------------
def test_equations_of_motion_with_xg_match_paper(fsb1_est: MMGShip) -> None:
    """以非零 x_G 直接核對 _derivs:
    (m+m_x)u̇ − (m+m_y)v r − x_G m r² = X;(m+m_y)v̇ + (m+m_x)u r + x_G m ṙ = Y;(I_zG + x_G² m + J_z)ṙ + x_G m (v̇ + u r) = N。"""
    ship = fsb1_est
    xg_saved = ship.xG
    ship.xG = 4.0
    try:
        u, v, r = 5.0, -0.6, 0.012
        delta, n = math.radians(15.0), 2.5
        X, Y, N, _ = ship.forces(u, v, r, delta, n, 0.0, 0.0)
        s = (0.0, 0.0, 0.0, u, v, r, delta, n, 0.0, 0.0)
        d = ship._derivs(s, delta, n, ship.n_tau, 0.0)
        du, dv, dr = d[3], d[4], d[5]
        m, mx, my, xg = ship.m, ship.mx, ship.my, ship.xG
        assert (m + mx) * du - (m + my) * v * r - xg * m * r * r == pytest.approx(X, rel=1e-9)
        assert (m + my) * dv + (m + mx) * u * r + xg * m * dr == pytest.approx(Y, rel=1e-9)
        assert (ship.Izz + xg * xg * m + ship.Jzz) * dr + xg * m * (dv + u * r) == pytest.approx(N, rel=1e-9)
    finally:
        ship.xG = xg_saved


def test_hull_polynomial_nondimensionalisation(kvlcc2: MMGShip) -> None:
    """船體力 = ½ρLdU²·(多項式),N 多乘 L;v' = v_m/U、r' = rL/U(KVLCC2 Table 3 係數,小漂角不觸發橫流混合)。"""
    ship = kvlcc2
    U = 7.0
    vp, rp = -0.10, 0.20  # β ≈ 5.7°
    v = vp * U
    u = math.sqrt(U * U - v * v)
    r = rp * U / ship.L
    bd = ForceBreakdown()
    ship.forces(u, v, r, 0.0, 0.0, 0.0, 0.0, breakdown=bd)
    q = 0.5 * ship.rho * ship.L * ship.d * U * U
    h = ship.h
    r0 = 0.022
    X_ref = q * (-r0 + h["Xvv"] * vp**2 + h["Xvr"] * vp * rp + h["Xrr"] * rp**2 + h["Xvvvv"] * vp**4)
    Y_ref = q * (h["Yv"] * vp + h["Yr"] * rp + h["Yvvv"] * vp**3 + h["Yvvr"] * vp**2 * rp + h["Yvrr"] * vp * rp**2 + h["Yrrr"] * rp**3)
    N_ref = q * ship.L * (h["Nv"] * vp + h["Nr"] * rp + h["Nvvv"] * vp**3 + h["Nvvr"] * vp**2 * rp + h["Nvrr"] * vp * rp**2 + h["Nrrr"] * rp**3)
    assert bd.hull[0] == pytest.approx(X_ref, rel=1e-9)
    assert bd.hull[1] == pytest.approx(Y_ref, rel=1e-9)
    assert bd.hull[2] == pytest.approx(N_ref, rel=1e-9)
    assert ship.m == pytest.approx(ship.c["mass"]["m"] * 0.5 * ship.rho * ship.L**2 * ship.d)
    assert ship.Izz == pytest.approx(ship.c["mass"]["Izz"] * 0.5 * ship.rho * ship.L**4 * ship.d)


def test_kvlcc2_benchmark_uses_paper_xg() -> None:
    assert kvlcc2_coefficients()["reference"]["xG_m"] == pytest.approx(11.2)


# ---------------------------------------------------------------------------
# 修正:阻力方向、純橫移、零速艏搖阻尼
# ---------------------------------------------------------------------------
def test_resistance_opposes_astern_motion(fsb1_est: MMGShip) -> None:
    ship = fsb1_est
    ahead = ForceBreakdown()
    astern = ForceBreakdown()
    ship.forces(3.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, breakdown=ahead)
    ship.forces(-3.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, breakdown=astern)
    assert ahead.hull[0] < 0.0
    assert astern.hull[0] > 0.0, "倒退時船體阻力應向前(修正前為向後,會使船愈退愈快)"
    assert astern.hull[0] == pytest.approx(-ahead.hull[0], rel=1e-9)


def test_pure_sway_gives_no_surge_force_and_no_yaw_moment(fsb1_est: MMGShip) -> None:
    """β = 90°:多項式 X'_vvvv v'⁴ 不得產生向前推力;線性 Munk 項 N'_v v'(∝ sin 2β)在 90° 應為零。"""
    ship = fsb1_est
    for v in (0.5, 1.0, 2.0):
        bd = ForceBreakdown()
        ship.forces(0.0, v, 0.0, 0.0, 0.0, 0.0, 0.0, breakdown=bd)
        XH, YH, NH = bd.hull
        assert abs(XH) < 1e-6 * abs(YH)
        assert YH < 0.0
        assert YH == pytest.approx(-0.5 * ship.rho * ship.d * ship.L * ship.cf_cd * v * v, rel=1e-9), "純橫移 = 截面橫流阻力"
        assert abs(NH) < 1e-6 * abs(YH) * ship.L


def test_cross_flow_blend_off_in_normal_regime(fsb1_est: MMGShip) -> None:
    """|β| < 20° 且 r' 不大時多項式完全保留(識別/驗證區不受橫流混合影響)。"""
    ship = fsb1_est
    U = 6.0
    beta = math.radians(15.0)
    u, v = U * math.cos(beta), -U * math.sin(beta)
    r = 0.9 * U / ship.L  # r' = 0.9(35° 定常迴旋量級)
    bd = ForceBreakdown()
    ship.forces(u, v, r, 0.0, 0.0, 0.0, 0.0, breakdown=bd)
    q = 0.5 * ship.rho * ship.L * ship.d * U * U
    vp, rp = v / U, r * ship.L / U
    h = ship.h
    Y_ref = q * (h["Yv"] * vp + h["Yr"] * rp + h["Yvvv"] * vp**3 + h["Yvvr"] * vp**2 * rp + h["Yvrr"] * vp * rp**2 + h["Yrrr"] * rp**3)
    assert bd.hull[1] == pytest.approx(Y_ref, rel=1e-9)


def test_pure_yaw_at_zero_speed_has_damping(fsb1_est: MMGShip) -> None:
    ship = fsb1_est
    prev = 0.0
    for r in (0.003, 0.006, 0.012):
        _, _, N, _ = ship.forces(0.0, 0.0, r, 0.0, 0.0, 0.0, 0.0)
        assert N < prev, "零速純艏搖必須有阻尼力矩(修正前為 0)"
        prev = N
    _, _, N_neg, _ = ship.forces(0.0, 0.0, -0.006, 0.0, 0.0, 0.0, 0.0)
    _, _, N_pos, _ = ship.forces(0.0, 0.0, 0.006, 0.0, 0.0, 0.0, 0.0)
    assert N_neg == pytest.approx(-N_pos, rel=1e-9)
    assert N_pos == pytest.approx(-0.5 * ship.rho * ship.d * ship.cf_cd * 0.006 * 0.006 * 0.030625 * ship.L**4, rel=1e-6), "10 條帶 ∫x|x|x dx"


def test_zero_speed_thruster_turn_rot_bounded(fsb1_est: MMGShip) -> None:
    """零速側推迴轉:迴轉率須有界(橫流阻尼),不隨時間無限增大;側推力隨船體旋轉轉為前進(旋轉力的運動學),
    船速以側推推力/((m+m_x)r) 的圓周運動為上限。"""
    ship = fsb1_est
    res = thruster_turn(ship, "starboard", 360.0, 0.0, max_time_s=1500.0)
    rots = [h.rot_degpm for h in res.history]
    assert rots and 0.0 < max(rots) < 120.0
    late = [h for h in res.history if h.t > 300.0]
    assert max(h.rot_degpm for h in late) <= max(rots) + 1e-9, "300 s 後迴轉率不得再增大"
    assert all(h.rot_degpm > 0.0 for h in late)
    u_max = max(h.u for h in res.history)
    r_late = min(h.rot_degpm for h in late) * math.pi / 180.0 / 60.0
    assert 0.0 < u_max < ship.thr_T / ((ship.m + ship.mx) * r_late) * 1.5


# ---------------------------------------------------------------------------
# (c) 螺槳
# ---------------------------------------------------------------------------
def test_propeller_thrust_deduction_and_wake(fsb1_est: MMGShip) -> None:
    ship = fsb1_est
    u, n = 6.0, 2.5
    bd = ForceBreakdown()
    ship.forces(u, 0.0, 0.0, 0.0, n, 0.0, 0.0, breakdown=bd)
    assert bd.propeller[0] == pytest.approx((1.0 - ship.tP) * bd.thrust, rel=1e-12)
    va = u * (1.0 - ship.wP0)
    J = va / (n * ship.prop.diameter)
    assert bd.j == pytest.approx(J)
    k0, k1, k2 = ship.prop.kt
    assert bd.kt == pytest.approx(k0 + k1 * J + k2 * J * J)
    assert bd.thrust == pytest.approx(ship.rho * n * n * ship.prop.diameter**4 * bd.kt)
    # 伴流隨螺槳處漂角減小:w_P = w_P0·exp(−C β_P²) → 有漂角時 J 變大、K_T 變小
    bd2 = ForceBreakdown()
    beta = math.radians(15.0)
    ship.forces(u * math.cos(beta), -u * math.sin(beta), 0.0, 0.0, n, 0.0, 0.0, breakdown=bd2)
    assert bd2.j > bd.j and bd2.kt < bd.kt


def test_propeller_astern_quadrants(fsb1_est: MMGShip) -> None:
    prop = fsb1_est.prop
    rho = fsb1_est.rho
    t_ahead, _, _, _ = prop.thrust_torque(2.0, 4.0, rho)
    t_astern_moving_ahead, _, _, _ = prop.thrust_torque(-2.0, 4.0, rho)
    t_astern_moving_astern, _, _, _ = prop.thrust_torque(-2.0, -2.0, rho)
    t_ahead_moving_astern, _, _, _ = prop.thrust_torque(2.0, -2.0, rho)
    assert t_ahead > 0.0 and t_ahead_moving_astern > 0.0
    assert t_astern_moving_ahead < 0.0 and t_astern_moving_astern < 0.0
    # 倒車時前進速度愈高,倒車推力愈大(負 J 外推),且有 jClip 上限
    t_fast, _, _, _ = prop.thrust_torque(-2.0, 7.0, rho)
    assert t_fast < t_astern_moving_ahead
    t_clip1, _, _, _ = prop.thrust_torque(-2.0, prop.j_clip * 2.0 * prop.diameter, rho)
    t_clip2, _, _, _ = prop.thrust_torque(-2.0, 1.5 * prop.j_clip * 2.0 * prop.diameter, rho)
    assert t_clip1 == pytest.approx(t_clip2)
    # 軸停止:鎖定螺槳阻力與航向相反
    t_lock, _, _, _ = prop.thrust_torque(0.0, 3.0, rho)
    assert t_lock < 0.0 and prop.thrust_torque(0.0, -3.0, rho)[0] > 0.0


def test_astern_propeller_side_force_bow_to_starboard(fsb1_est: MMGShip) -> None:
    ship = fsb1_est
    bd = ForceBreakdown()
    ship.forces(3.0, 0.0, 0.0, 0.0, -2.0, 0.0, 0.0, breakdown=bd)
    assert bd.propeller[1] < 0.0 and bd.propeller[2] > 0.0, "右旋槳倒車:艉向左、艏向右"


# ---------------------------------------------------------------------------
# (d) 舵
# ---------------------------------------------------------------------------
def test_rudder_inflow_and_normal_force_match_mmg(kvlcc2: MMGShip) -> None:
    """u_R = ε u_P √(η(1+κ(√(1+8K_T/πJ²)−1))² + 1−η)、v_R = Uγ_Rβ_R、α_R = δ − atan(v_R/u_R)、
    X_R = −(1−t_R)F_N sinδ、Y_R = −(1+a_H)F_N cosδ、N_R = −(x_R + a_H x_H)F_N cosδ。"""
    ship = kvlcc2
    U, beta = 7.0, math.radians(10.0)
    u, v = U * math.cos(beta), -U * math.sin(beta)
    r = 0.3 * U / ship.L
    delta, n = math.radians(20.0), 1.2
    bd = ForceBreakdown()
    ship.forces(u, v, r, delta, n, 0.0, 0.0, breakdown=bd)
    rp = r * ship.L / U
    beta_p = beta - ship.xP / ship.L * rp
    wP = ship.wP0 * math.exp(-ship.wake_c * beta_p**2)
    uP = u * (1.0 - wP)
    J = uP / (n * ship.prop.diameter)
    KT = bd.kt
    uR = ship.eps * uP * math.sqrt(ship.eta * (1.0 + ship.kappa * (math.sqrt(1.0 + 8.0 * KT / (math.pi * J * J)) - 1.0))**2 + 1.0 - ship.eta)
    beta_r = beta - ship.lR / ship.L * rp
    gamma = ship.gR_plus if beta_r > 0 else ship.gR_minus
    vR = U * gamma * beta_r
    alpha = delta - math.atan2(vR, uR)
    FN = 0.5 * ship.rho * ship.A_R * (uR * uR + vR * vR) * ship.f_alpha * math.sin(alpha)
    assert bd.u_r == pytest.approx(uR, rel=1e-9)
    assert bd.alpha_r == pytest.approx(alpha, rel=1e-9)
    assert bd.rudder[0] == pytest.approx(-(1.0 - ship.tR) * FN * math.sin(delta), rel=1e-9)
    assert bd.rudder[1] == pytest.approx(-(1.0 + ship.aH) * FN * math.cos(delta), rel=1e-9)
    assert bd.rudder[2] == pytest.approx(-(ship.xR + ship.aH * ship.xH) * FN * math.cos(delta), rel=1e-9)
    # 右轉中 β_R > 0 → 用 γ_R(+),有效舵角小於舵角;右舵 → N_R > 0、Y_R < 0
    assert beta_r > 0 and gamma == ship.gR_plus and 0.0 < alpha < delta
    assert bd.rudder[2] > 0.0 and bd.rudder[1] < 0.0


def test_rudder_gamma_r_switches_with_beta_r_sign(kvlcc2: MMGShip) -> None:
    ship = kvlcc2
    U = 7.0
    out = {}
    for sgn in (+1.0, -1.0):
        beta = sgn * math.radians(8.0)
        u, v = U * math.cos(beta), -U * math.sin(beta)
        bd = ForceBreakdown()
        ship.forces(u, v, 0.0, 0.0, 1.2, 0.0, 0.0, breakdown=bd)
        out[sgn] = bd.alpha_r
    # β_R > 0 用 γ+ = 0.640,β_R < 0 用 γ− = 0.395 → 流入角大小不對稱
    assert abs(out[1.0]) > abs(out[-1.0])
    assert abs(out[1.0]) / abs(out[-1.0]) == pytest.approx(ship.gR_plus / ship.gR_minus, rel=0.05)


def test_rudder_inflow_positive_when_moving_astern_with_ahead_propeller(fsb1_est: MMGShip) -> None:
    bd = ForceBreakdown()
    fsb1_est.forces(-2.0, 0.0, 0.0, math.radians(20.0), 2.0, 0.0, 0.0, breakdown=bd)
    assert bd.thrust > 0.0
    assert bd.u_r > 0.0, "倒退中正車:螺槳滑流仍由前向後流過舵,u_R 應為正(修正前為負)"


# ---------------------------------------------------------------------------
# (e) 正負慣例:右舵 → 右轉、漂角、ROT
# ---------------------------------------------------------------------------
def test_sign_conventions_turning(fsb1_est: MMGShip) -> None:
    ship = fsb1_est
    ship.reset(heading_deg=0.0, speed_kn=10.0)
    ship.set_rudder(20.0)
    ship.run(60.0)
    s = ship.state_json()
    assert ship.state.r > 0 and s["rot"] == pytest.approx(math.degrees(ship.state.r) * 60.0)
    assert 0.0 < s["heading"] < 90.0 and s["drift"] > 0.0 and ship.state.v < 0.0
    assert s["drift"] == pytest.approx(math.degrees(math.atan2(-ship.state.v, ship.state.u)))


# ---------------------------------------------------------------------------
# (g) 風:Blendermann (1994),以 MSS blendermann94 的寫法獨立計算
# ---------------------------------------------------------------------------
def _blendermann_reference(ship: MMGShip, v_rw: float, gamma: float) -> tuple[float, float, float]:
    w = ship.c["wind"]
    AL, AF, sL, Loa = w["lateralArea_m2"], w["frontalArea_m2"], w["lateralCentroid_m"], ship.loa
    CDt, delta = w["CDt"], w["delta"]
    CDlAF = w["CDlHead"] if abs(gamma) <= math.pi / 2 else w["CDlTail"]
    CDl = CDlAF * AF / AL
    den = 1.0 - 0.5 * delta * (1.0 - CDl / CDt) * math.sin(2.0 * gamma) ** 2
    CX = -CDl * (AL / AF) * math.cos(gamma) / den
    CY = CDt * math.sin(gamma) / den
    CN = (sL / Loa - 0.18 * (gamma - math.pi / 2)) * CY
    qa = 0.5 * w["airDensity_kgm3"] * v_rw * v_rw
    return qa * CX * AF, qa * CY * AL, qa * CN * AL * Loa


@pytest.mark.parametrize("wind_from_deg", [0.0, 45.0, 90.0, 135.0, 180.0, 225.0, 300.0])
def test_wind_blendermann_matches_reference(fsb1_est: MMGShip, wind_from_deg: float) -> None:
    ship = fsb1_est
    V = 30.0 * KN_TO_MPS
    ship.set_environment(wind_speed_kn=30.0, wind_dir_deg=wind_from_deg)
    try:
        bd = ForceBreakdown()
        ship.forces(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, breakdown=bd)  # 靜止、航向北 → 相對風 = 真風
    finally:
        ship.set_environment(wind_speed_kn=0.0)
    gamma = math.radians(wind_from_deg)  # 風自右舷為正
    gamma_abs = abs(math.atan2(math.sin(gamma), math.cos(gamma)))
    X_ref, Y_ref, N_ref = _blendermann_reference(ship, V, gamma_abs)
    sgn = 1.0 if math.sin(gamma) >= 0 else -1.0
    assert bd.wind[0] == pytest.approx(X_ref, rel=1e-9, abs=1e-6)
    assert bd.wind[1] == pytest.approx(-sgn * Y_ref, rel=1e-9, abs=1e-6), "風自右舷 → 向左的橫向力"
    assert bd.wind[2] == pytest.approx(-sgn * N_ref, rel=1e-9, abs=1e-6)
    if wind_from_deg == 0.0:
        assert bd.wind[0] == pytest.approx(-0.5 * ship.rho_a * V * V * ship.w_AT * ship.w_CDlH)
        assert bd.wind[1] == 0.0 and bd.wind[2] == 0.0


def test_wind_denominator_uses_lateral_area_referenced_cdl(fsb1_est: MMGShip) -> None:
    """修正:分母 1 − δ/2(1 − C_Dl/C_Dt)sin²2ε 的 C_Dl 須以側面積為基準(C_Dl,AF·A_F/A_L),否則側向力在 45° 偏小約 15%。"""
    ship = fsb1_est
    V = 30.0 * KN_TO_MPS
    ship.set_environment(wind_speed_kn=30.0, wind_dir_deg=45.0)
    try:
        bd = ForceBreakdown()
        ship.forces(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, breakdown=bd)
    finally:
        ship.set_environment(wind_speed_kn=0.0)
    cdl_al = ship.w_CDlH * ship.w_AT / ship.w_AL
    den = 1.0 - 0.5 * ship.w_delta * (1.0 - cdl_al / ship.w_CDt)
    assert bd.wind[1] == pytest.approx(-0.5 * ship.rho_a * V * V * ship.w_AL * ship.w_CDt * math.sin(math.pi / 4) / den, rel=1e-9)


# ---------------------------------------------------------------------------
# 線性導數經驗式(Kijima 1990 / Inoue 1981)
# ---------------------------------------------------------------------------
def test_kijima_yr_includes_added_mass_mx() -> None:
    cb, lpp, b, d = 0.738, 99.0, 18.0, 5.701
    k = 2.0 * d / lpp
    h0 = hull_derivatives(cb, lpp, b, d)
    h1 = hull_derivatives(cb, lpp, b, d, mx_prime=0.0172)
    assert h0["Yr"] == pytest.approx(0.25 * math.pi * k)
    assert h1["Yr"] == pytest.approx(0.25 * math.pi * k + 0.0172), "Kijima:Y'_r − m'_x = ¼πk"
    assert h1["Yv"] == pytest.approx(-(0.5 * math.pi * k + 1.4 * cb * b / lpp))
    assert h1["Nv"] == pytest.approx(-k) and h1["Nr"] == pytest.approx(-0.54 * k + k * k)
    est = make_ship("FSB1", "full", prefer_file=False)
    assert est.c["hull"]["Yr"] == pytest.approx(0.25 * math.pi * k + est.c["mass"]["mx"])


# ---------------------------------------------------------------------------
# (h) 迴旋幾何(MSC.137(76) / MSC/Circ.1053)與 (i) Z 形超越角
# ---------------------------------------------------------------------------
def test_turning_geometry_definitions(fsb1_est: MMGShip) -> None:
    """前進距離/橫距在航向變化 90° 時、戰術直徑在 180° 時量,皆以下舵令時的船舯位置與原航向為基準;左右迴旋同號。"""
    ship = fsb1_est
    for side, sgn in (("starboard", 1.0), ("port", -1.0)):
        res = turning_circle(ship, 35.0, side, 10.0, max_heading_change_deg=200.0)
        hist = res.history
        h0 = hist[0]
        psi0 = math.radians(h0.heading_deg)

        def along_across(h):
            dx, dy = h.x - 0.0, h.y - 0.0
            return dx * math.sin(psi0) + dy * math.cos(psi0), sgn * (dx * math.cos(psi0) - dy * math.sin(psi0))

        # 以 1 s 取樣歷程(航向累積展開)找 90°/180° 的鄰近樣本,量測值應落在相鄰樣本之間(內插)
        def bracket(target):
            prev = None
            total = 0.0
            last_hd = h0.heading_deg
            for h in hist:
                total += ((h.heading_deg - last_hd + 540.0) % 360.0) - 180.0
                last_hd = h.heading_deg
                ch = sgn * total
                if prev is not None and prev[0] < target <= ch:
                    return prev[1], h
                prev = (ch, h)
            raise AssertionError("歷程未涵蓋 " + str(target))

        a, b = bracket(90.0)
        al_a, ac_a = along_across(a)
        al_b, ac_b = along_across(b)
        assert min(al_a, al_b) - 1e-6 <= res.advance_m <= max(al_a, al_b) + 1e-6
        assert min(ac_a, ac_b) - 1e-6 <= res.transfer_m <= max(ac_a, ac_b) + 1e-6
        a, b = bracket(180.0)
        _, ac_a = along_across(a)
        _, ac_b = along_across(b)
        assert min(ac_a, ac_b) - 1e-6 <= res.tactical_diameter_m <= max(ac_a, ac_b) + 1e-6
        assert res.advance_m > 0 and res.transfer_m > 0 and res.tactical_diameter_m > res.transfer_m
        assert res.marks[90]["along_m"] == pytest.approx(res.advance_m) and res.marks[180]["across_m"] == pytest.approx(res.tactical_diameter_m)


def test_zigzag_overshoot_definition(fsb1_est: MMGShip) -> None:
    """第一超越角 = 第一次反舵後航向繼續偏離的最大值 − 10°(以 0.5 s 歷程重算)。"""
    ship = fsb1_est
    z = zigzag(ship, 10.0, 10.0, "starboard", 10.0, n_overshoots=2, sample_dt=0.5)
    assert z.completed and len(z.reversal_times_s) >= 2
    psi0 = z.history[0].heading_deg
    t1, t2 = z.reversal_times_s[0], z.reversal_times_s[1]
    seg = [((h.heading_deg - psi0 + 540.0) % 360.0) - 180.0 for h in z.history if t1 <= h.t <= t2]
    assert max(seg) - 10.0 == pytest.approx(z.overshoots_deg[0], abs=0.05)
    # 反舵時刻航向變化應剛好達到 10°(在一個步長內)
    h_rev = min(z.history, key=lambda h: abs(h.t - t1))
    assert abs(((h_rev.heading_deg - psi0 + 540.0) % 360.0) - 180.0 - 10.0) < 0.5


# ---------------------------------------------------------------------------
# (j) 識別只用 identification 區
# ---------------------------------------------------------------------------
def test_identification_targets_exclude_validation_data() -> None:
    tt = load_trial_targets("FSB1")
    targets = build_targets(tt, 99.0)
    keys = [t.key for t in targets]
    assert keys and all(k.startswith("turning.") or k.startswith("zigzag10_10.") for k in keys)
    assert not any("20_20" in k or "crash" in k.lower() or "inertia" in k.lower() or "speed" in k.lower().replace("steadyspeed", "") for k in keys)
    tt2 = copy.deepcopy(tt)
    tt2["identification"] = {}
    assert build_targets(tt2, 99.0) == []
    ship = make_ship("FSB1", "full", dt=0.5, prefer_file=False)
    assert simulate_targets(ship, tt2, []) == {}
