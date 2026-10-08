"""由主尺寸、Cb、吃水、螺槳/舵資料以經驗公式估計 MMG 係數(規劃書 6.3「經驗公式推估」步驟)。

參數定義依 Yasukawa & Yoshimura (2015) MMG 標準模型(船舯座標;u、v_m、r)。無因次化:
X、Y 以 ½ρLdU²,N 以 ½ρL²dU²,質量以 ½ρL²d,慣性矩以 ½ρL⁴d,L = LPP,d = 平均吃水。

來源(各函式 docstring 註明):
- 線性船體導數:Kijima et al. (1990) / Inoue (1981) 回歸式(k = 2d/L)。
- 非線性船體導數:以 KVLCC2 基準值(Yasukawa & Yoshimura 2015 Table 3)為起點(規劃書 6.1 明示可作起點;
  本船 L/B、d/B 與 KVLCC2 相近),再由參數識別調整。Yoshimura & Masumoto (2012) 回歸常數尚待依原文核對後替換(README)。
- 附加質量:Zhou (周昭明) 經驗式(Motora 圖表的回歸版)。
- 阻力:黏性(ITTC-57 + 形狀因子)+ 興波項,興波項由 engine.speedTrial 以推力恆等(thrust identity)反推。
- 螺槳:Wageningen B 系列;伴流 w_P0、推力減額 t_P 用經驗值(Taylor / Holtrop 型)。
- 舵:MMG 標準舵模型(Fujii f_α;t_R、a_H、x_H、γ_R、l_R、ε、κ 依 Kijima 1990 / KVLCC2);Schilling 舵為分段升力曲線。
"""

from __future__ import annotations

import datetime as _dt
import json
import math
from pathlib import Path
from typing import Any

import numpy as np

from . import __version__, paths
from .particulars import GRAVITY, KN_TO_MPS, RHO_AIR, RHO_WATER, ShipParticulars, froude_number, load_trial_targets
from .propeller import fit_quadratic, kq_bseries, kt_bseries, zero_thrust_j

SCHEMA_VERSION = "1.0"
NU_WATER = 1.19e-6  # 海水運動黏度 m^2/s(15 °C)

# 大漂角/低速橫流阻力與多項式的混合設定(mmg.forces):漂角通道 20–40°;艏搖通道(atan(½L|r|/|u|))35–60°(r' 約 1.4–3.5),
# 下限高於 35° 定常迴旋的值(r' ≈ 1,約 27°),使正常操縱區不受影響,只在多項式有效範圍外的低速迴轉(側推、停船末段)提供艏搖阻尼。
CROSS_FLOW_DEFAULT: dict[str, float] = {
    "Cd": 1.0, "blendStart_deg": 20.0, "blendEnd_deg": 40.0, "yawBlendStart_deg": 35.0, "yawBlendEnd_deg": 60.0, "uFloor_mps": 0.5,
}

# KVLCC2 基準非線性導數(Yasukawa & Yoshimura 2015 Table 3)
KVLCC2 = {
    "Xvv": -0.040, "Xvr": 0.002, "Xrr": 0.011, "Xvvvv": 0.771,
    "Yv": -0.315, "Yr": 0.083, "Yvvv": -1.607, "Yvvr": 0.379, "Yvrr": -0.391, "Yrrr": 0.008,
    "Nv": -0.137, "Nr": -0.049, "Nvvv": -0.030, "Nvvr": -0.294, "Nvrr": 0.055, "Nrrr": -0.013,
    "mx": 0.022, "my": 0.223, "Jzz": 0.011, "m": 0.2936,
    "tP": 0.220, "wP0": 0.40, "xP": -0.48,
    "tR": 0.387, "aH": 0.312, "xH": -0.464, "epsilon": 1.09, "kappa": 0.50,
    "gammaRMinus": 0.395, "gammaRPlus": 0.640, "lR": -0.710, "xR": -0.5,
    "kt": (0.2931, -0.2753, -0.1385),
}


# ---------------------------------------------------------------------------
# 附加質量(Zhou 經驗式)
# ---------------------------------------------------------------------------
def added_mass_zhou(cb: float, lpp: float, b: float, d: float, mass: float) -> tuple[float, float, float]:
    """回傳 (m_x, m_y, J_zz)[kg, kg, kg·m²],Zhou 經驗式(以 Motora 圖表回歸,適用一般商船)。"""
    lb = lpp / b
    db = d / b
    mx_ratio = 0.01 * (
        0.398 + 11.97 * cb * (1 + 3.73 * db) - 2.89 * cb * lb * (1 + 1.13 * db)
        + 0.175 * cb * lb * lb * (1 + 0.541 * db) - 1.107 * lb * db
    )
    my_ratio = (
        0.882 - 0.54 * cb * (1 - 1.6 * db) - 0.156 * lb * (1 - 0.673 * cb)
        + 0.826 * db * lb * (1 - 0.678 * db) - 0.638 * cb * db * lb * (1 - 0.669 * db)
    )
    kzz = (lpp / 100.0) * (33 - 76.85 * cb * (1 - 0.784 * cb) + 3.43 * lb * (1 - 0.63 * cb))
    jzz = mass * kzz * kzz
    mx_ratio = min(max(mx_ratio, 0.02), 0.15)
    my_ratio = min(max(my_ratio, 0.4), 1.2)
    return mx_ratio * mass, my_ratio * mass, jzz


# ---------------------------------------------------------------------------
# 船體導數
# ---------------------------------------------------------------------------
def hull_derivatives(cb: float, lpp: float, b: float, d: float, mx_prime: float = 0.0) -> dict[str, float]:
    """線性導數:Kijima (1990) / Inoue (1981)(k = 2d/L,β = −v' 故 Y'v = −Y'β、N'v = −N'β);非線性:KVLCC2 基準值(見模組 docstring)。

    Kijima/Inoue 的回歸式給的是 ``Y'_r − m'_x = ¼πk``(其運動方程式左側為 (m'+m'_x)u'r'),
    因此 MMG 的純水動力導數 Y'_r = ¼πk + m'_x;``mx_prime`` 為縱向附加質量 m'_x = m_x/(½ρL²d)。
    """
    k = 2.0 * d / lpp
    cbbl = cb * b / lpp
    out = {
        "Yv": -(0.5 * math.pi * k + 1.4 * cbbl),
        "Yr": 0.25 * math.pi * k + mx_prime,
        "Nv": -k,
        "Nr": -0.54 * k + k * k,
    }
    for key in ("Xvv", "Xvr", "Xrr", "Xvvvv", "Yvvv", "Yvvr", "Yvrr", "Yrrr", "Nvvv", "Nvvr", "Nvrr", "Nrrr"):
        out[key] = KVLCC2[key]
    return out


# ---------------------------------------------------------------------------
# 阻力與推進
# ---------------------------------------------------------------------------
def wetted_surface_mumford(lpp: float, b: float, d: float, cb: float) -> float:
    """Mumford 濕面積估計:S = 1.025·L·(Cb·B + 1.7·T)。"""
    return 1.025 * lpp * (cb * b + 1.7 * d)


def viscous_r0(u: float, lpp: float, d: float, s_wet: float, form_factor: float) -> float:
    """黏性阻力無因次係數 R0'_v(U) = C_F(Re)·(1+k)·S/(L·d),ITTC-57;Re 下限 1e6。"""
    re = max(abs(u) * lpp / NU_WATER, 1.0e6)
    cf = 0.075 / (math.log10(re) - 2.0) ** 2
    return cf * form_factor * s_wet / (lpp * d)


def wake_fraction_estimate(cb: float, single_screw: bool = True) -> float:
    """伴流分數經驗值:Taylor 單俥 w = 0.5·Cb − 0.05(肥大型船通常 0.30 至 0.38)。"""
    return 0.5 * cb - 0.05


def thrust_deduction_estimate(w: float) -> float:
    """推力減額經驗值:t ≈ 0.6·w(單俥)。"""
    return 0.6 * w


def fit_resistance_from_trial(sp: ShipParticulars, prop: dict[str, Any], w0: float, t0: float) -> dict[str, Any]:
    """由 speedTrial 的(轉速、航速)以推力恆等反推各點總阻力係數 R0'(U),
    扣除黏性部分後把興波部分擬合為 c_w·(Fn/Fn_ref)^q。

    無 speedTrial 時以車鐘對照表(海報)代替;都沒有時用純經驗值(興波係數 0.004、q = 4)。
    """
    lpp, d, b, cb = sp.lpp, sp.draft, sp.breadth, sp.cb
    s_wet = wetted_surface_mumford(lpp, b, d, cb)
    form_factor = 1.0 + 0.6 * max(cb - 0.6, 0.0) + 0.15  # 肥大型船形狀因子約 1.2 至 1.3
    dp = prop["diameter_m"]
    k0, k1, k2 = prop["kt"]
    points = []
    # speedTrial 為試俥裝載(滿載)的實測;其他裝載狀態改用海報車鐘對照表的該狀態航速
    trial_loading = "full"
    if sp.loading.name == trial_loading:
        for pt in sp.speed_trial:
            points.append((float(pt["rpm"]), float(pt["speed_kn"]), pt.get("power_kW")))
    source = "engine.speedTrial"
    if not points:
        for order in ("NAVF", "FAH", "HAH", "SAH", "DSAH"):
            v = sp.telegraph_speed_kn(order)
            if v:
                points.append((sp.telegraph_rpm(order), v, None))
        source = "engine.telegraph(海報車鐘對照)"
    rows = []
    points.sort(key=lambda p: p[1])
    fn_ref = froude_number(points[-1][1] * KN_TO_MPS, lpp) if points else 0.2
    for rpm, v_kn, p_kw in points:
        n = rpm / 60.0
        u = v_kn * KN_TO_MPS
        j = u * (1.0 - w0) / (n * dp)
        kt = k0 + k1 * j + k2 * j * j
        thrust = RHO_WATER * n * n * dp**4 * kt
        r_total = (1.0 - t0) * thrust
        r0_total = r_total / (0.5 * RHO_WATER * lpp * d * u * u)
        r0_v = viscous_r0(u, lpp, d, s_wet, form_factor)
        kq = kq_bseries(j, prop["pitchRatio"], prop["expandedAreaRatio"], prop["blades"])
        p_model = 2.0 * math.pi * n * RHO_WATER * n * n * dp**5 * kq / 0.98 / 1000.0
        rows.append({
            "rpm": rpm, "speed_kn": v_kn, "froude": froude_number(u, lpp), "J": j, "KT": kt,
            "thrust_kN": thrust / 1e3, "resistance_kN": r_total / 1e3, "R0_total": r0_total, "R0_viscous": r0_v,
            "R0_wave": r0_total - r0_v, "power_trial_kW": p_kw, "power_model_kW": p_model,
        })
    # 三參數擬合:形狀因子(黏性倍率)、興波係數 c_w、Froude 指數 q(有界最小平方);點數不足時固定形狀因子
    from scipy.optimize import least_squares

    fns = np.array([r["froude"] for r in rows])
    us = np.array([r["speed_kn"] * KN_TO_MPS for r in rows])
    r0s = np.array([r["R0_total"] for r in rows])
    cf_base = np.array([viscous_r0(u, lpp, d, s_wet, 1.0) for u in us])

    def model(p):
        ff, cw, q = p
        return cf_base * ff + cw * (fns / fn_ref) ** q

    def resid(p):
        return (model(p) - r0s) / r0s

    if len(rows) >= 4:
        x0 = [form_factor, 0.004, 4.0]
        sol = least_squares(resid, x0, bounds=([1.0, 0.0, 1.0], [3.0, 0.05, 8.0]))
        form_factor, c_w, q = (float(v) for v in sol.x)
    elif len(rows) >= 2:
        sol = least_squares(lambda p: resid([form_factor, p[0], p[1]]), [0.004, 4.0], bounds=([0.0, 1.0], [0.05, 8.0]))
        c_w, q = (float(v) for v in sol.x)
    elif len(rows) == 1:
        c_w, q = max(float(r0s[0] - cf_base[0] * form_factor), 0.0), 4.0
    else:
        c_w, q = 0.004, 4.0
    for r in rows:
        r["R0_viscous"] = viscous_r0(r["speed_kn"] * KN_TO_MPS, lpp, d, s_wet, form_factor)
        r["R0_wave"] = r["R0_total"] - r["R0_viscous"]
    for r in rows:
        r["R0_model"] = r["R0_viscous"] + c_w * (r["froude"] / fn_ref) ** q
    # 低速下限:不低於最低試俥速度點的模型值(避免黏性+興波式在低速低估阻力)
    u_min = min((r["speed_kn"] for r in rows), default=0.0) * KN_TO_MPS
    r0_floor = min((r["R0_model"] for r in rows), default=0.0)
    return {
        "model": "viscous+wave",
        "viscous": {"method": "ITTC-57 摩擦線 × 形狀因子 × S/(L·d),S 用 Mumford 式;形狀因子為擬合值(≥4 點時)",
                    "wettedSurface_m2": s_wet, "formFactor": form_factor},
        "wave": {"method": f"由 {source} 以推力恆等反推(w_P0、t_P 為經驗值)後擬合 c_w·(Fn/Fn_ref)^q",
                 "coefficient": c_w, "froudeExponent": q, "froudeRef": fn_ref},
        "lowSpeedFloor": {"speed_mps": u_min, "R0": r0_floor, "note": "U 低於最低資料點時 R0' 不低於該點模型值"},
        "trialFit": rows,
    }


def r0_prime(resistance: dict[str, Any], u: float, lpp: float, d: float) -> float:
    """總阻力係數 R0'(U)(模擬時每步呼叫)。``model == "constant"`` 時回傳固定 R0(KVLCC2 基準用)。"""
    if resistance.get("model") == "constant":
        return float(resistance["R0"]) * float(resistance.get("scale", 1.0))
    v = resistance["viscous"]
    w = resistance["wave"]
    fn = froude_number(abs(u), lpp)
    r0 = viscous_r0(u, lpp, d, v["wettedSurface_m2"], v["formFactor"]) + w["coefficient"] * (fn / w["froudeRef"]) ** w["froudeExponent"]
    fl = resistance.get("lowSpeedFloor")
    if fl and abs(u) < fl["speed_mps"] and r0 < fl["R0"]:
        return fl["R0"]
    return r0


# ---------------------------------------------------------------------------
# 舵
# ---------------------------------------------------------------------------
def rudder_coefficients(sp: ShipParticulars) -> dict[str, Any]:
    """MMG 標準舵模型參數;Schilling 舵啟用分段升力曲線(0 至 35° 線性、35 至 70° 高升力區)。"""
    cb, lpp, b = sp.cb, sp.lpp, sp.breadth
    cbbl = cb * b / lpp
    area = sp.rudder_area
    schilling = sp.is_schilling
    aspect = 1.5 if schilling else 1.6  # 幾何展弦比(無圖面,假設值)
    span = math.sqrt(aspect * area)
    aspect_eff = aspect * (1.5 if schilling else 1.0)  # Schilling 端板提高有效展弦比
    f_alpha = 6.13 * aspect_eff / (aspect_eff + 2.25)  # Fujii
    eta = min(sp.propeller["diameter_m"] / span, 1.0)
    epsilon = -156.2 * cbbl**2 + 41.6 * cbbl - 1.76  # Kijima 1990
    a_h = 0.627 * cb - 0.153  # Kijima 1990
    t_r = 1.0 - (0.28 * cb + 0.55)  # Kijima 1990:1 − t_R = 0.28 Cb + 0.55
    max_angle = sp.rudder_max_angle
    normal_max = float(sp.rudder.get("normalMaxAngle_deg", min(max_angle, 35.0)))
    return {
        "type": str(sp.rudder.get("type", "")),
        "area_m2": area,
        "span_m": span,
        "aspectRatio": aspect,
        "aspectRatioEffective": aspect_eff,
        "fAlpha": f_alpha,
        "schilling": {
            "enabled": schilling,
            "linearLimit_deg": normal_max if schilling else max_angle,
            "maxAngle_deg": max_angle,
            "highLiftSlopeFactor": 0.5,
            "note": "35° 以上以 sin(α) 的 highLiftSlopeFactor 倍斜率延伸至 70°(係數可調;無實測升力曲線)",
        },
        "maxAngle_deg": max_angle,
        "rate_degps": sp.rudder_rate_degps,
        "neutralAngle_deg": 0.0,
        "swirlAngle_deg": 0.0,
        "tR": t_r,
        "aH": a_h,
        "xH": -0.45,
        "xR": -0.5,
        "gammaRMinus": KVLCC2["gammaRMinus"],
        "gammaRPlus": KVLCC2["gammaRPlus"],
        "lR": KVLCC2["lR"],
        "epsilon": epsilon,
        "kappa": KVLCC2["kappa"],
        "eta": eta,
    }


# ---------------------------------------------------------------------------
# 主機、側推、風、淺水、squat
# ---------------------------------------------------------------------------
def engine_coefficients(sp: ShipParticulars, trial_targets: dict[str, Any] | None = None) -> dict[str, Any]:
    """主機轉速響應參數。換向延遲屬機械特性,無法由船型推估:有試俥紀錄(crashStop.asternStart_s)時採用並註明。"""
    e = sp.engine
    rev_delay, rev_note = 90.0, "reversalDelay_s 為 2 行程直接換向主機的典型假設值"
    cs = ((trial_targets or {}).get("validation") or {}).get("crashStop") or {}
    if cs.get("asternStart_s"):
        rev_delay = float(cs["asternStart_s"]) - 5.0  # 倒車起動紀錄 = 延遲 + 轉速過零所需時間(約 5 s)
        rev_note = "reversalDelay_s 取自試俥緊急停船紀錄的倒車啟動時間(機械特性,非水動力擬合;停船時間與航跡距離仍為獨立驗證)"
    tele = {k: sp.telegraph_rpm(k) for k in ("NAVF", "FAH", "HAH", "SAH", "DSAH", "STOP", "DSAS", "SAS", "HAS", "FAS", "EFAS")}
    max_rpm = max(float(p["rpm"]) for p in sp.speed_trial) if sp.speed_trial else float(e["mcr_rpm"])
    return {
        "mcr_rpm": float(e["mcr_rpm"]),
        "maxRpm": max(max_rpm, float(e["mcr_rpm"])),
        "minRpm": float(e.get("minimumRpm") or 0.3 * float(e["mcr_rpm"])),
        "criticalRpmRange": e.get("criticalRpmRange"),
        "telegraph": tele,
        "rpmTimeConstant_s": 6.0,
        "rpmRateLimit_rpmps": 3.0,
        "startDelay_s": 5.0,
        "reversalDelay_s": rev_delay,
        "shaftStopTimeConstant_s": 20.0,
        "note": "一階滯後+速率限制;換向:燃油切斷→軸轉速衰減→逾 reversalDelay_s 且軸轉速低於 5% MCR 後反向起動。" + rev_note,
    }


def thruster_coefficients(sp: ShipParticulars) -> dict[str, Any]:
    bt = sp.bow_thruster
    notes = []
    thrust = bt.get("nominalThrust_kN")
    if thrust is None and bt.get("power_kW"):
        thrust = 0.15 * float(bt["power_kW"])  # 隧道式側推約 0.15 kN/kW(No.1:49 kN / 325 kW)
        notes.append(f"bowThruster.nominalThrust_kN 以 0.15 kN/kW 估計 = {thrust:.1f} kN")
    return {
        "installed": bool(bt),
        "x_m": 0.43 * sp.lpp,
        "nominalThrust_kN": float(thrust or 0.0),
        "effectiveness": 1.0,
        "fullThrustDelay_s": float(bt.get("fullThrustDelay_s") or 30.0),
        "halfThrustSpeed_kn": 2.5,
        "zeroSpeedTurningRate_degPerMin": bt.get("turningRateAtZeroSpeed_degPerMin"),
        "notes": notes,
    }


def wind_coefficients(sp: ShipParticulars) -> dict[str, Any]:
    a_l, a_t, s_l, notes = sp.windage_areas()
    ballast = sp.loading.name == "ballast"
    return {
        "model": "Blendermann (1994) 參數式(油輪典型係數;Fujiwara 1998 / Isherwood 1972 回歸表尚未數位化)",
        "lateralArea_m2": a_l,
        "frontalArea_m2": a_t,
        "lateralCentroid_m": s_l,
        "CDt": 0.70,
        "CDlHead": 0.75 if ballast else 0.90,
        "CDlTail": 0.55,
        "delta": 0.40,
        "airDensity_kgm3": RHO_AIR,
        "notes": notes,
    }


def shallow_water_coefficients() -> dict[str, Any]:
    """Kijima 型淺水倍率 f = 1 + a·(T/(h−T))^n,對線性導數、非線性導數與附加質量各一組(趨勢用,未經驗證)。"""
    return {
        "model": "f(h/T) = 1 + a·(T/(h−T))^n(Kijima & Nakiri 型;h/T ≥ 1.05)",
        "linearSway": {"a": 0.40, "n": 1.0},
        "linearYaw": {"a": 0.30, "n": 1.0},
        "nonlinear": {"a": 0.40, "n": 1.0},
        "addedMassSway": {"a": 0.30, "n": 1.2},
        "addedMassSurge": {"a": 0.10, "n": 1.0},
        "addedInertia": {"a": 0.30, "n": 1.2},
        "resistance": {"a": 0.20, "n": 1.0},
        "minDepthRatio": 1.05,
    }


def squat_coefficients(sp: ShipParticulars) -> dict[str, Any]:
    """ICORELS 式 S = C_s·∇/L²·Fnh²/√(1−Fnh²);C_s 由 particulars.squatTable 最小平方擬合(無表時 2.0)。"""
    table = sp.raw.get("squatTable") or []
    vol = sp.loading.volume_m3
    lpp = sp.lpp
    num = den = 0.0
    for row in table:
        h = sp.draft + float(row["ukc_m"])
        u = float(row["speed_kn"]) * KN_TO_MPS
        fnh = u / math.sqrt(GRAVITY * h)
        if fnh >= 0.95:
            continue
        basis = vol / lpp**2 * fnh**2 / math.sqrt(1 - fnh**2)
        num += basis * float(row["bowSquat_m"])
        den += basis * basis
    cs = num / den if den > 0 else 2.0
    return {"model": "ICORELS", "Cs": cs, "fittedFromTable": den > 0}


# ---------------------------------------------------------------------------
# 總成
# ---------------------------------------------------------------------------
def estimate_coefficients(sp: ShipParticulars, trial_targets: dict[str, Any] | None = None) -> dict[str, Any]:
    """完整係數估計;回傳可直接寫成 coefficients.<loading>.json 的字典。"""
    lpp, b, d, cb, mass = sp.lpp, sp.breadth, sp.draft, sp.cb, sp.mass
    nd_m = 0.5 * RHO_WATER * lpp * lpp * d
    nd_i = 0.5 * RHO_WATER * lpp**4 * d
    mx, my, jzz = added_mass_zhou(cb, lpp, b, d, mass)
    kzz = 0.25 * lpp
    izz = mass * kzz * kzz

    pr = sp.propeller
    pd, ear, z = float(pr["pitchRatio"]), float(pr["expandedAreaRatio"]), int(pr["blades"])
    j0 = zero_thrust_j(pd, ear, z)
    kt = fit_quadratic(lambda j: kt_bseries(j, pd, ear, z), j0)
    kq = fit_quadratic(lambda j: kq_bseries(j, pd, ear, z), j0)
    w0 = wake_fraction_estimate(cb)
    t0 = thrust_deduction_estimate(w0)
    prop = {
        "diameter_m": float(pr["diameter_m"]), "pitchRatio": pd, "expandedAreaRatio": ear, "blades": z,
        "rotation": str(pr.get("rotation", "right")),
        "kt": [float(x) for x in kt], "kq": [float(x) for x in kq], "ktFitRange_J": j0, "jMax": 1.2 * j0,
        "wP0": w0, "tP": t0, "xP": KVLCC2["xP"], "wakeDriftFactor": 4.0,
        "asternThrustFactor": 0.85, "jClip": 0.9, "lockedDragCd": 0.50, "lockRps": 0.3, "sideForceFactor": 0.08,
        "note": "K_T、K_Q 為 Wageningen B 系列在 0≤J≤J0 的二次擬合;倒車/鎖定為簡化四象限(README)",
    }
    resistance = fit_resistance_from_trial(sp, prop, w0, t0)
    hull = hull_derivatives(cb, lpp, b, d, mx / nd_m)
    hull_out: dict[str, Any] = {"resistance": resistance}
    hull_out.update(hull)
    hull_out["crossFlow"] = CROSS_FLOW_DEFAULT.copy()
    notes = list(sp.estimated)
    cs = ((trial_targets or {}).get("validation") or {}).get("crashStop") or {}
    if cs.get("asternStart_s"):
        notes.append("engine.reversalDelay_s 取自 trial_targets.validation.crashStop.asternStart_s(主機換向的機械特性,"
                     "非水動力擬合;緊急停船的停船時間與航跡距離仍為獨立驗證項目)")

    coeffs: dict[str, Any] = {
        "$schema": "../../../src/Contracts/coefficients.schema.json",
        "schemaVersion": SCHEMA_VERSION,
        "shipId": sp.ship_id,
        "shipName": sp.name,
        "loading": sp.loading.name,
        "loadingLabel": sp.loading.label,
        "version": __version__,
        "generatedAt": _dt.date.today().isoformat(),
        "source": {
            "method": "estimate",
            "particulars": f"data/ships/{sp.ship_id}/particulars.json",
            "dataGrade": (trial_targets or {}).get("dataGrade", "trial"),
            "identifiedParameters": [],
            "notes": notes,
        },
        "tolerances": (trial_targets or {}).get("tolerances", {}),
        "reference": {
            "length_m": lpp, "breadth_m": b, "draft_m": d, "draftFore_m": sp.loading.draft_fore_m,
            "draftAft_m": sp.loading.draft_aft_m, "blockCoefficient": cb, "displacement_t": sp.loading.displacement_t,
            "mass_kg": mass, "xG_m": 0.0, "density_kgm3": RHO_WATER, "airDensity_kgm3": RHO_AIR,
            "nondimensionalisation": "X,Y:0.5·ρ·L·d·U²;N:0.5·ρ·L²·d·U²;m:0.5·ρ·L²·d;I:0.5·ρ·L⁴·d;L=LPP,d=平均吃水",
        },
        "mass": {"m": mass / nd_m, "mx": mx / nd_m, "my": my / nd_m, "Izz": izz / nd_i, "Jzz": jzz / nd_i},
        "hull": hull_out,
        "propeller": prop,
        "rudder": rudder_coefficients(sp),
        "engine": engine_coefficients(sp, trial_targets),
        "thruster": thruster_coefficients(sp),
        "wind": wind_coefficients(sp),
        "shallowWater": shallow_water_coefficients(),
        "squat": squat_coefficients(sp),
    }
    return coeffs


def save_coefficients(coeffs: dict[str, Any], path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(coeffs, f, ensure_ascii=False, indent=2)
        f.write("\n")


def load_coefficients(ship_id: str, loading: str, path: Path | None = None) -> dict[str, Any]:
    p = path or paths.coefficients_path(ship_id, loading)
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def load_or_estimate(sp: ShipParticulars, prefer_file: bool = True) -> dict[str, Any]:
    """優先讀取已識別的係數檔;不存在時即時估計。"""
    p = paths.coefficients_path(sp.ship_id, sp.loading.name)
    if prefer_file and p.exists():
        return load_coefficients(sp.ship_id, sp.loading.name, p)
    try:
        tt = load_trial_targets(sp.ship_id)
    except FileNotFoundError:
        tt = None
    return estimate_coefficients(sp, tt)


def kvlcc2_coefficients() -> dict[str, Any]:
    """KVLCC2(SIMMAN 基準船)的 MMG 係數(Yasukawa & Yoshimura 2015 Table 2、3),供 MMG 實作本身的回歸測試(規劃書 6.3)。

    主尺寸:L 320 m、B 58 m、d 20.8 m、∇ 312,622 m³、x_G 11.2 m(船舯前)、D_P 9.86 m、A_R 112.5 m²、H_R 15.8 m。
    """
    lpp, b, d = 320.0, 58.0, 20.8
    x_g = 11.2  # 重心距船舯(前正),Yasukawa & Yoshimura 2015 Table 1;式 (1)–(3) 的 x_G 耦合項由此進入
    vol = 312622.0
    mass = vol * RHO_WATER
    nd_m = 0.5 * RHO_WATER * lpp * lpp * d
    nd_i = 0.5 * RHO_WATER * lpp**4 * d
    izz = mass * (0.25 * lpp) ** 2
    k = KVLCC2
    hull: dict[str, Any] = {"resistance": {"model": "constant", "R0": 0.022}}
    for key in ("Xvv", "Xvr", "Xrr", "Xvvvv", "Yv", "Yr", "Yvvv", "Yvvr", "Yvrr", "Yrrr", "Nv", "Nr", "Nvvv", "Nvvr", "Nvrr", "Nrrr"):
        hull[key] = k[key]
    hull["crossFlow"] = CROSS_FLOW_DEFAULT.copy()
    tele = {"NAVF": 76.0, "FAH": 60.0, "HAH": 45.0, "SAH": 35.0, "DSAH": 25.0, "STOP": 0.0, "DSAS": -25.0, "SAS": -35.0, "HAS": -45.0, "FAS": -60.0, "EFAS": -60.0}
    return {
        "schemaVersion": SCHEMA_VERSION, "shipId": "FSB1", "shipName": "KVLCC2 benchmark", "loading": "full",
        "version": __version__, "generatedAt": _dt.date.today().isoformat(),
        "source": {"method": "estimate", "particulars": "KVLCC2 (Yasukawa & Yoshimura 2015)", "dataGrade": "trial", "identifiedParameters": [], "notes": []},
        "tolerances": {},
        "reference": {"length_m": lpp, "breadth_m": b, "draft_m": d, "blockCoefficient": 0.81, "displacement_t": mass / 1000.0,
                      "mass_kg": mass, "xG_m": x_g, "density_kgm3": RHO_WATER, "airDensity_kgm3": RHO_AIR},
        "mass": {"m": mass / nd_m, "mx": k["mx"], "my": k["my"], "Izz": izz / nd_i, "Jzz": k["Jzz"]},
        "hull": hull,
        "propeller": {"diameter_m": 9.86, "pitchRatio": 0.721, "expandedAreaRatio": 0.431, "blades": 4, "rotation": "right",
                      "kt": list(k["kt"]), "kq": [0.03, -0.03, -0.01], "jMax": 1.0, "wP0": k["wP0"], "tP": k["tP"], "xP": k["xP"],
                      "wakeDriftFactor": 4.0, "asternThrustFactor": 0.85, "jClip": 0.9, "lockedDragCd": 0.5, "lockRps": 0.3, "sideForceFactor": 0.08},
        "rudder": {"type": "conventional", "area_m2": 112.5, "span_m": 15.8, "fAlpha": 2.747,
                   "schilling": {"enabled": False, "linearLimit_deg": 35.0, "maxAngle_deg": 35.0, "highLiftSlopeFactor": 0.5},
                   "maxAngle_deg": 35.0, "rate_degps": 2.32, "neutralAngle_deg": 0.0, "swirlAngle_deg": 0.0,
                   "tR": k["tR"], "aH": k["aH"], "xH": k["xH"], "xR": k["xR"], "gammaRMinus": k["gammaRMinus"], "gammaRPlus": k["gammaRPlus"],
                   "lR": k["lR"], "epsilon": k["epsilon"], "kappa": k["kappa"], "eta": 9.86 / 15.8},
        "engine": {"mcr_rpm": 76.0, "maxRpm": 90.0, "minRpm": 20.0, "criticalRpmRange": None, "telegraph": tele,
                   "rpmTimeConstant_s": 10.0, "rpmRateLimit_rpmps": 1.0, "startDelay_s": 5.0, "reversalDelay_s": 120.0, "shaftStopTimeConstant_s": 30.0},
        "thruster": {"installed": False, "x_m": 0.43 * lpp, "nominalThrust_kN": 0.0, "effectiveness": 1.0, "fullThrustDelay_s": 30.0, "halfThrustSpeed_kn": 2.5},
        "wind": {"model": "Blendermann", "lateralArea_m2": 3000.0, "frontalArea_m2": 900.0, "lateralCentroid_m": -30.0,
                 "CDt": 0.70, "CDlHead": 0.90, "CDlTail": 0.55, "delta": 0.40, "airDensity_kgm3": RHO_AIR},
        "shallowWater": shallow_water_coefficients(),
        "squat": {"model": "ICORELS", "Cs": 2.0},
    }
