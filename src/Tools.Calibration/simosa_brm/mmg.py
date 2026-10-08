"""3 自由度 MMG 船舶運動模型(規劃書 6.2;Yasukawa & Yoshimura 2015 式 (1) 至 (3))。

- 船舯座標,狀態 u(縱向)、v_m(橫向,右正)、r(艏搖,右轉正);內部一律 SI 與弧度。
- 固定步長 RK4(預設 0.02 s = 50 Hz,規劃書 5.3);所有狀態含致動器(舵、軸轉速、側推)一併積分。
- 均勻流以「相對水速」處理:u、v_m 為對水速度,對地位置積分時加上流速向量(均勻定常流時嚴格成立)。
- 確定性:純 Python 浮點運算、無亂數;相同輸入序列 → 相同狀態雜湊(``state_hash``)。
- 對外狀態欄位依 src/Contracts/state.schema.json(度、節、航向 0–360、舵右正、ROT 度/分)。
"""

from __future__ import annotations

import hashlib
import math
import struct
from dataclasses import dataclass, field
from typing import Any

from .coefficients import r0_prime
from .particulars import GRAVITY, KN_TO_MPS, ShipParticulars
from .propeller import PropellerModel

TWO_PI = 2.0 * math.pi
DEG = math.pi / 180.0
TELEGRAPH_ORDERS = ("EFAS", "FAS", "HAS", "SAS", "DSAS", "STOP", "DSAH", "SAH", "HAH", "FAH", "NAVF")


def wrap_pi(a: float) -> float:
    """包到 (−π, π]。"""
    a = math.fmod(a + math.pi, TWO_PI)
    if a < 0:
        a += TWO_PI
    return a - math.pi


def deg360(a_rad: float) -> float:
    d = math.degrees(a_rad) % 360.0
    return d if d >= 0 else d + 360.0


def _smoothstep(x: float, a: float, b: float) -> float:
    if x <= a:
        return 0.0
    if x >= b:
        return 1.0
    t = (x - a) / (b - a)
    return t * t * (3.0 - 2.0 * t)


@dataclass
class Environment:
    """環境:風(來向、kn 換成 m/s)、均勻流(去向)、水深(None 或 ≥10 倍吃水視為深水)。"""

    wind_speed: float = 0.0  # m/s(真風)
    wind_dir_from: float = 0.0  # rad,來向(自北順時針)
    current_speed: float = 0.0  # m/s
    current_set: float = 0.0  # rad,去向
    water_depth: float | None = None  # m

    @property
    def current_en(self) -> tuple[float, float]:
        return self.current_speed * math.sin(self.current_set), self.current_speed * math.cos(self.current_set)


@dataclass
class ShipState:
    """積分狀態(SI、弧度)。"""

    t: float = 0.0
    tick: int = 0
    x: float = 0.0  # 東 (m)
    y: float = 0.0  # 北 (m)
    psi: float = 0.0  # 航向 rad
    u: float = 0.0
    v: float = 0.0
    r: float = 0.0
    delta: float = 0.0  # 實際舵角 rad(右正)
    n: float = 0.0  # 軸轉速 rps(倒車負)
    thr: float = 0.0  # 側推實際(−1..1)
    track: float = 0.0  # 航跡累積距離 (m)

    def as_tuple(self) -> tuple[float, ...]:
        return (self.x, self.y, self.psi, self.u, self.v, self.r, self.delta, self.n, self.thr, self.track)


@dataclass
class Controls:
    rudder_order: float = 0.0  # rad
    telegraph: str = "STOP"
    rpm_order: float = 0.0  # rpm(倒車負)
    thruster_order: float = 0.0
    autopilot: bool = False
    autopilot_heading: float = 0.0  # rad
    autopilot_rot_limit: float = 15.0 * DEG / 60.0  # rad/s
    autopilot_rudder_limit: float = 20.0 * DEG
    frozen: bool = False


@dataclass
class ForceBreakdown:
    hull: tuple[float, float, float] = (0.0, 0.0, 0.0)
    propeller: tuple[float, float, float] = (0.0, 0.0, 0.0)
    rudder: tuple[float, float, float] = (0.0, 0.0, 0.0)
    thruster: tuple[float, float, float] = (0.0, 0.0, 0.0)
    wind: tuple[float, float, float] = (0.0, 0.0, 0.0)
    thrust: float = 0.0
    torque: float = 0.0
    j: float = 0.0
    kt: float = 0.0
    alpha_r: float = 0.0
    u_r: float = 0.0


class MMGShip:
    """一艘自船的 MMG 模型 + 致動器 + 環境。"""

    def __init__(self, sp: ShipParticulars, coeffs: dict[str, Any], dt: float = 0.02,
                 origin_lat: float = 23.80, origin_lon: float = 120.15) -> None:
        self.sp = sp
        self.c = coeffs
        self.dt = float(dt)
        self.origin_lat = origin_lat
        self.origin_lon = origin_lon
        self.env = Environment()
        self.state = ShipState()
        self.ctl = Controls()
        self.aground = False
        self.faults: list[str] = []
        self._eng_mode = "stopped"  # stopped | run | reversing | starting
        self._eng_timer = 0.0
        self._n_target = 0.0
        self._n_tau = 1.0
        self._prepare()

    # ------------------------------------------------------------------
    # 常數前處理
    # ------------------------------------------------------------------
    def _prepare(self) -> None:
        c = self.c
        ref = c["reference"]
        self.L = float(ref["length_m"])
        self.d = float(ref["draft_m"])
        self.rho = float(ref["density_kgm3"])
        self.rho_a = float(ref.get("airDensity_kgm3", 1.225))
        self.mass = float(ref["mass_kg"])
        self.xG = float(ref.get("xG_m", 0.0))
        L, d, rho = self.L, self.d, self.rho
        nd_m = 0.5 * rho * L * L * d
        nd_i = 0.5 * rho * L**4 * d
        m = c["mass"]
        self.m = float(m["m"]) * nd_m
        self.mx0 = float(m["mx"]) * nd_m
        self.my0 = float(m["my"]) * nd_m
        self.Izz = float(m["Izz"]) * nd_i
        self.Jzz0 = float(m["Jzz"]) * nd_i
        h = c["hull"]
        self.h = {k: float(h[k]) for k in ("Xvv", "Xvr", "Xrr", "Xvvvv", "Yv", "Yr", "Yvvv", "Yvvr", "Yvrr", "Yrrr",
                                            "Nv", "Nr", "Nvvv", "Nvvr", "Nvrr", "Nrrr")}
        self.resistance = h["resistance"]
        cf = h["crossFlow"]
        self.cf_cd = float(cf["Cd"])
        self.cf_a = float(cf["blendStart_deg"]) * DEG
        self.cf_b = float(cf["blendEnd_deg"]) * DEG
        self.u_floor = float(cf["uFloor_mps"])
        p = c["propeller"]
        self.prop = PropellerModel(
            diameter=float(p["diameter_m"]), kt=tuple(p["kt"]), kq=tuple(p["kq"]),
            astern_factor=float(p["asternThrustFactor"]), j_clip=float(p["jClip"]),
            locked_cd=float(p["lockedDragCd"]), lock_rps=float(p["lockRps"]), side_force_factor=float(p["sideForceFactor"]),
        )
        self.j_max = float(p.get("jMax", 1.0))
        self.wP0 = float(p["wP0"])
        self.tP = float(p["tP"])
        self.xP = float(p["xP"]) * L
        self.wake_c = float(p["wakeDriftFactor"])
        self.right_handed = str(p.get("rotation", "right")).lower().startswith("r")
        rd = c["rudder"]
        self.A_R = float(rd["area_m2"])
        self.f_alpha = float(rd["fAlpha"])
        sch = rd["schilling"]
        self.sch_enabled = bool(sch["enabled"])
        self.sch_lin = float(sch["linearLimit_deg"]) * DEG
        self.sch_max = float(sch["maxAngle_deg"]) * DEG
        self.sch_slope = float(sch["highLiftSlopeFactor"])
        self.delta_max = float(rd["maxAngle_deg"]) * DEG
        self.delta_rate = float(rd["rate_degps"]) * DEG
        self.delta_neutral = float(rd.get("neutralAngle_deg", 0.0)) * DEG
        self.swirl_angle = float(rd.get("swirlAngle_deg", 0.0)) * DEG
        self.tR = float(rd["tR"])
        self.aH = float(rd["aH"])
        self.xH = float(rd["xH"]) * L
        self.xR = float(rd["xR"]) * L
        self.gR_minus = float(rd["gammaRMinus"])
        self.gR_plus = float(rd["gammaRPlus"])
        self.lR = float(rd["lR"]) * L
        self.eps = float(rd["epsilon"])
        self.kappa = float(rd["kappa"])
        self.eta = float(rd["eta"])
        e = c["engine"]
        self.eng = e
        self.n_max = float(e["maxRpm"]) / 60.0
        self.n_min = float(e["minRpm"]) / 60.0
        self.n_tau = float(e["rpmTimeConstant_s"])
        self.n_rate = float(e["rpmRateLimit_rpmps"]) / 60.0
        self.n_start_delay = float(e["startDelay_s"])
        self.n_rev_delay = float(e["reversalDelay_s"])
        self.n_stop_tau = float(e["shaftStopTimeConstant_s"])
        self.n_start_threshold = 0.05 * float(e["mcr_rpm"]) / 60.0
        th = c["thruster"]
        self.thr_installed = bool(th.get("installed", True))
        self.thr_x = float(th["x_m"])
        self.thr_T = float(th["nominalThrust_kN"]) * 1e3 * float(th["effectiveness"])
        self.thr_rate = 1.0 / max(float(th["fullThrustDelay_s"]), 1.0)
        self.thr_u_half = float(th["halfThrustSpeed_kn"]) * KN_TO_MPS
        w = c["wind"]
        self.w_AL = float(w["lateralArea_m2"])
        self.w_AT = float(w["frontalArea_m2"])
        self.w_sL = float(w["lateralCentroid_m"])
        self.w_CDt = float(w["CDt"])
        self.w_CDlH = float(w["CDlHead"])
        self.w_CDlT = float(w["CDlTail"])
        self.w_delta = float(w["delta"])
        self.loa = float(self.sp.loa)
        self.sw = c["shallowWater"]
        self.squat_cs = float(c["squat"]["Cs"])
        self.vol = self.mass / rho
        self._update_shallow()

    def _shallow_factor(self, group: str) -> float:
        h = self.env.water_depth
        if h is None:
            return 1.0
        ratio = h / self.d
        if ratio < float(self.sw["minDepthRatio"]):
            ratio = float(self.sw["minDepthRatio"])
        x = max(0.0, 1.0 / (ratio - 1.0) - 0.1)  # h/T ≥ 11 時無修正
        g = self.sw[group]
        return 1.0 + float(g["a"]) * x ** float(g["n"])

    def _update_shallow(self) -> None:
        self.f_lin_sway = self._shallow_factor("linearSway")
        self.f_lin_yaw = self._shallow_factor("linearYaw")
        self.f_nonlin = self._shallow_factor("nonlinear")
        self.f_res = self._shallow_factor("resistance")
        self.mx = self.mx0 * self._shallow_factor("addedMassSurge")
        self.my = self.my0 * self._shallow_factor("addedMassSway")
        self.Jzz = self.Jzz0 * self._shallow_factor("addedInertia")

    # ------------------------------------------------------------------
    # 設定與命令
    # ------------------------------------------------------------------
    def set_environment(self, wind_speed_kn: float | None = None, wind_dir_deg: float | None = None,
                        current_set_deg: float | None = None, current_drift_kn: float | None = None,
                        water_depth_m: float | None | str = "keep") -> None:
        if wind_speed_kn is not None:
            self.env.wind_speed = float(wind_speed_kn) * KN_TO_MPS
        if wind_dir_deg is not None:
            self.env.wind_dir_from = float(wind_dir_deg) * DEG
        if current_set_deg is not None:
            self.env.current_set = float(current_set_deg) * DEG
        if current_drift_kn is not None:
            self.env.current_speed = float(current_drift_kn) * KN_TO_MPS
        if water_depth_m != "keep":
            self.env.water_depth = None if water_depth_m is None else float(water_depth_m)
        self._update_shallow()

    def reset(self, x: float = 0.0, y: float = 0.0, heading_deg: float = 0.0, speed_kn: float = 0.0,
              rpm: float | None = None, telegraph: str | None = None) -> None:
        """重設到直航狀態;rpm 省略時取該速度的穩態轉速(速度為 0 則主機停俥)。"""
        self.state = ShipState(x=x, y=y, psi=heading_deg * DEG, u=speed_kn * KN_TO_MPS)
        self.ctl = Controls()
        self.aground = False
        self.faults = []
        if rpm is None:
            rpm = self.steady_rpm_for_speed(self.state.u) if speed_kn > 0 else 0.0
        self.state.n = rpm / 60.0
        self.state.delta = self.delta_neutral
        self.ctl.rudder_order = 0.0
        if telegraph is not None:
            self.set_telegraph(telegraph)
        else:
            self.ctl.rpm_order = rpm
            self.ctl.telegraph = self._telegraph_for_rpm(rpm)
        self._eng_mode = "run" if abs(self.state.n) > 1e-6 else "stopped"
        self._eng_timer = 0.0
        self._n_target = self.state.n
        self._n_tau = self.n_tau

    def _telegraph_for_rpm(self, rpm: float) -> str:
        if abs(rpm) < 1e-6:
            return "STOP"
        best, bd = "STOP", 1e9
        for k, v in self.eng["telegraph"].items():
            dd = abs(float(v) - rpm)
            if dd < bd and (float(v) == 0.0) == (rpm == 0.0) and (float(v) > 0) == (rpm > 0):
                best, bd = k, dd
        return best

    def set_rudder(self, angle_deg: float) -> None:
        self.ctl.autopilot = False
        self.ctl.rudder_order = max(-self.delta_max, min(self.delta_max, angle_deg * DEG))

    def set_telegraph(self, order: str) -> None:
        if order not in TELEGRAPH_ORDERS:
            raise ValueError(f"未知車令 {order!r}")
        self.ctl.telegraph = order
        self.ctl.rpm_order = float(self.eng["telegraph"][order])

    def set_rpm(self, rpm: float) -> None:
        """直接下轉速(機側/駕駛台遙控);幅度限制在 [minRpm, maxRpm]。"""
        if abs(rpm) < 1e-6:
            rpm = 0.0
        else:
            mag = min(max(abs(rpm), self.n_min * 60.0), self.n_max * 60.0)
            rpm = math.copysign(mag, rpm)
        self.ctl.rpm_order = rpm
        self.ctl.telegraph = self._telegraph_for_rpm(rpm)

    def set_thruster(self, order: float) -> None:
        self.ctl.thruster_order = max(-1.0, min(1.0, float(order))) if self.thr_installed else 0.0

    def set_autopilot(self, enabled: bool, heading_deg: float | None = None, rot_limit_degpm: float | None = None,
                      rudder_limit_deg: float | None = None) -> None:
        self.ctl.autopilot = bool(enabled)
        if heading_deg is not None:
            self.ctl.autopilot_heading = heading_deg * DEG
        if rot_limit_degpm is not None:
            self.ctl.autopilot_rot_limit = rot_limit_degpm * DEG / 60.0
        if rudder_limit_deg is not None:
            self.ctl.autopilot_rudder_limit = rudder_limit_deg * DEG

    # ------------------------------------------------------------------
    # 穩態輔助
    # ------------------------------------------------------------------
    def surge_balance(self, u: float, n: float) -> float:
        """直航穩態:X_H + X_P(含舵零角的阻力)在 v=r=0 時的合力。"""
        X, _, _, _ = self.forces(u, 0.0, 0.0, self.delta_neutral, n, 0.0, 0.0, 0.0, 0.0)
        return X

    def steady_speed_for_rpm(self, rpm: float) -> float:
        """給定轉速的直航穩態速度(m/s),二分法。"""
        n = rpm / 60.0
        if n <= 0:
            return 0.0
        lo, hi = 0.0, 20.0
        for _ in range(60):
            mid = 0.5 * (lo + hi)
            if self.surge_balance(mid, n) > 0:
                lo = mid
            else:
                hi = mid
        return 0.5 * (lo + hi)

    def steady_rpm_for_speed(self, u: float) -> float:
        """給定直航速度所需轉速(rpm),二分法(可超過 maxRpm,供初始化用)。"""
        if u <= 0:
            return 0.0
        lo, hi = 0.0, 3.0 * self.n_max
        for _ in range(60):
            mid = 0.5 * (lo + hi)
            if self.surge_balance(u, mid) < 0:
                lo = mid
            else:
                hi = mid
        return 0.5 * (lo + hi) * 60.0

    # ------------------------------------------------------------------
    # 力
    # ------------------------------------------------------------------
    def forces(self, u: float, v: float, r: float, delta: float, n: float, thr: float, psi: float,
               ug_e: float = 0.0, ug_n: float = 0.0, breakdown: ForceBreakdown | None = None) -> tuple[float, float, float, float]:
        """回傳 (X, Y, N, 推力 T)。ug_e/ug_n 為對地速度(風的相對速度用)。"""
        L, d, rho = self.L, self.d, self.rho
        h = self.h
        U2 = u * u + v * v
        U = math.sqrt(U2)
        Ue = U if U > self.u_floor else self.u_floor
        vp = v / Ue
        rp = r * L / Ue
        beta = math.atan2(-v, u) if U > 1e-9 else 0.0

        # ---- 船體 ----
        q = 0.5 * rho * L * d * U2
        r0 = r0_prime(self.resistance, U, L, d) * self.f_res
        vp2 = vp * vp
        rp2 = rp * rp
        XH = q * (-r0 + h["Xvv"] * vp2 + h["Xvr"] * vp * rp + h["Xrr"] * rp2 + h["Xvvvv"] * vp2 * vp2)
        YH_lin = q * self.f_lin_sway * (h["Yv"] * vp + h["Yr"] * rp)
        NH_lin = q * L * self.f_lin_yaw * (h["Nv"] * vp + h["Nr"] * rp)
        YH_nl = q * self.f_nonlin * (h["Yvvv"] * vp2 * vp + h["Yvvr"] * vp2 * rp + h["Yvrr"] * vp * rp2 + h["Yrrr"] * rp2 * rp)
        NH_nl = q * L * self.f_nonlin * (h["Nvvv"] * vp2 * vp + h["Nvvr"] * vp2 * rp + h["Nvrr"] * vp * rp2 + h["Nrrr"] * rp2 * rp)
        # 大漂角:橫流阻力(截面)與多項式非線性項依漂角混合(規劃書 6.2)
        ab = abs(beta)
        if ab > math.pi / 2:
            ab = math.pi - ab
        w_cf = _smoothstep(ab, self.cf_a, self.cf_b) if U > 1e-6 else 0.0
        if w_cf > 0.0:
            Ycf = 0.0
            Ncf = 0.0
            nstrip = 10
            dx = L / nstrip
            coef = 0.5 * rho * d * self.cf_cd * self.f_nonlin * dx
            for i in range(nstrip):
                xs = -0.5 * L + (i + 0.5) * dx
                vl = v + xs * r
                f = -coef * vl * abs(vl)
                Ycf += f
                Ncf += xs * f
            YH = YH_lin + (1.0 - w_cf) * YH_nl + w_cf * Ycf
            NH = NH_lin + (1.0 - w_cf) * NH_nl + w_cf * Ncf
        else:
            YH = YH_lin + YH_nl
            NH = NH_lin + NH_nl

        # ---- 螺槳 ----
        beta_p = beta - self.xP / L * rp
        if beta_p > 1.5:
            beta_p = 1.5
        elif beta_p < -1.5:
            beta_p = -1.5
        wP = self.wP0 * math.exp(-self.wake_c * beta_p * beta_p)
        va = u * (1.0 - wP)
        T, Q, J, KT = self.prop.thrust_torque(n, va, rho) if abs(J_limit := self.j_max) > 0 else (0.0, 0.0, 0.0, 0.0)
        if n > 0 and J > J_limit:
            # 風車區:多項式截止在 jMax
            T, Q, _, KT = self.prop.thrust_torque(n, J_limit * n * self.prop.diameter, rho)
        XP = (1.0 - self.tP) * T
        YP = 0.0
        NP = 0.0
        if n < 0.0 and T < 0.0:
            # 倒車橫向力:右旋槳艉向左(bow to starboard)
            YP = -self.prop.side_force_factor * abs(T) if self.right_handed else self.prop.side_force_factor * abs(T)
            NP = self.xP * YP

        # ---- 舵 ----
        swirl = 0.0
        if n > 0.0 and KT > 0.0:
            slip = math.sqrt(va * va + 8.0 * KT * n * n * self.prop.diameter**2 / math.pi)
            ur_core = va + self.kappa * (slip - va)
            uR = self.eps * math.sqrt(self.eta * ur_core * ur_core + (1.0 - self.eta) * va * va)
            if va < 0:
                uR = -uR
            # 滑流旋轉造成的有效舵角偏移:隨螺槳負荷(滑流加速因子)增大,上限 5 倍
            if va > 1e-6 and self.swirl_angle != 0.0:
                sfac = slip / va - 1.0
                swirl = self.swirl_angle * (sfac if sfac < 5.0 else 5.0)
        else:
            uR = self.eps * va
        beta_r = beta - self.lR / L * rp
        gR = self.gR_plus if beta_r > 0 else self.gR_minus
        vR = U * gR * beta_r
        UR2 = uR * uR + vR * vR
        alpha = wrap_pi((delta - self.delta_neutral - swirl) - math.atan2(vR, uR))
        a = abs(alpha)
        if a <= self.sch_lin:
            cn = self.f_alpha * math.sin(a)
        elif self.sch_enabled and a <= self.sch_max:
            cn = self.f_alpha * (math.sin(self.sch_lin) + self.sch_slope * (a - self.sch_lin))
        elif self.sch_enabled:
            cn = self.f_alpha * (math.sin(self.sch_lin) + self.sch_slope * (self.sch_max - self.sch_lin))
        else:
            # 一般舵:超過線性區後以 sin 衰減(失速簡化)
            cn = self.f_alpha * math.sin(a) if a <= math.pi / 2 else self.f_alpha * math.sin(math.pi - a)
        FN = 0.5 * rho * self.A_R * UR2 * math.copysign(cn, alpha)
        cd = math.cos(delta)
        sd = math.sin(delta)
        XR = -(1.0 - self.tR) * FN * sd
        YR = -(1.0 + self.aH) * FN * cd
        NR = -(self.xR + self.aH * self.xH) * FN * cd

        # ---- 艏側推 ----
        XT = YT = NT = 0.0
        if thr != 0.0 and self.thr_T > 0.0:
            uu = abs(u) / self.thr_u_half
            fade = math.exp(-0.6931471805599453 * uu * uu)
            YT = thr * self.thr_T * fade
            NT = self.thr_x * YT

        # ---- 風 ----
        XW = YW = NW = 0.0
        if self.env.wind_speed > 0.0:
            Vw = self.env.wind_speed
            wd = self.env.wind_dir_from
            we = -Vw * math.sin(wd) - ug_e
            wn = -Vw * math.cos(wd) - ug_n
            sp_, cp_ = math.sin(psi), math.cos(psi)
            u_rw = we * sp_ + wn * cp_
            v_rw = we * cp_ - wn * sp_
            Vrw2 = u_rw * u_rw + v_rw * v_rw
            if Vrw2 > 1e-6:
                epsw = math.atan2(-v_rw, -u_rw)  # 0 頂風;正 = 風自右舷
                ae = abs(epsw)
                cdl = self.w_CDlH if ae <= math.pi / 2 else self.w_CDlT
                s2 = math.sin(2.0 * ae)
                den = 1.0 - 0.5 * self.w_delta * (1.0 - cdl / self.w_CDt) * s2 * s2
                qa = 0.5 * self.rho_a * Vrw2
                XW = -qa * self.w_AT * cdl * math.cos(ae) / den
                cy = self.w_CDt * math.sin(ae) / den
                cnw = (self.w_sL / self.loa - 0.18 * (ae - math.pi / 2)) * cy
                sgn = 1.0 if epsw >= 0 else -1.0
                YW = -sgn * qa * self.w_AL * cy
                NW = -sgn * qa * self.w_AL * self.loa * cnw

        X = XH + XP + XR + XT + XW
        Y = YH + YP + YR + YT + YW
        N = NH + NP + NR + NT + NW
        if breakdown is not None:
            breakdown.hull = (XH, YH, NH)
            breakdown.propeller = (XP, YP, NP)
            breakdown.rudder = (XR, YR, NR)
            breakdown.thruster = (XT, YT, NT)
            breakdown.wind = (XW, YW, NW)
            breakdown.thrust, breakdown.torque, breakdown.j, breakdown.kt = T, Q, J, KT
            breakdown.alpha_r, breakdown.u_r = alpha, uR
        return X, Y, N, T

    # ------------------------------------------------------------------
    # 積分
    # ------------------------------------------------------------------
    def _derivs(self, s: tuple[float, ...], delta_cmd: float, n_target: float, n_tau: float, thr_cmd: float) -> tuple[float, ...]:
        x, y, psi, u, v, r, delta, n, thr, _track = s
        ce, cn_ = self.env.current_en
        sp_, cp_ = math.sin(psi), math.cos(psi)
        ug_e = u * sp_ + v * cp_ + ce
        ug_n = u * cp_ - v * sp_ + cn_
        X, Y, N, _ = self.forces(u, v, r, delta, n, thr, psi, ug_e, ug_n)
        m, mx, my = self.m, self.mx, self.my
        xg = self.xG
        du = (X + (m + my) * v * r + xg * m * r * r) / (m + mx)
        # 2×2:[(m+my) xg·m; xg·m (Izz+Jzz+xg²m)]·[dv; dr] = [Y − (m+mx)·u·r; N − xg·m·u·r]
        a11 = m + my
        a12 = xg * m
        a22 = self.Izz + self.Jzz + xg * xg * m
        b1 = Y - (m + mx) * u * r
        b2 = N - xg * m * u * r
        det = a11 * a22 - a12 * a12
        dv = (b1 * a22 - a12 * b2) / det
        dr = (a11 * b2 - a12 * b1) / det
        # 致動器
        e_d = delta_cmd - delta
        dd = e_d / 1.0
        if dd > self.delta_rate:
            dd = self.delta_rate
        elif dd < -self.delta_rate:
            dd = -self.delta_rate
        e_n = n_target - n
        dn = e_n / n_tau
        if dn > self.n_rate:
            dn = self.n_rate
        elif dn < -self.n_rate:
            dn = -self.n_rate
        e_t = thr_cmd - thr
        dth = e_t / 2.0
        if dth > self.thr_rate:
            dth = self.thr_rate
        elif dth < -self.thr_rate:
            dth = -self.thr_rate
        return (ug_e, ug_n, r, du, dv, dr, dd, dn, dth, math.sqrt(ug_e * ug_e + ug_n * ug_n))

    def _engine_logic(self) -> None:
        """車鐘/轉速指令 → 本步的轉速目標與時間常數(規劃書 6.2「主機/推進控制」)。

        模式:stopped / stopping(停俥滑行)/ run(已點火朝指令轉速)/ reversing(換向:燃油切斷、等軸轉速降到起動門檻且逾
        reversalDelay_s)/ starting(停俥後重新起動,startDelay_s)。一旦進入 run,直到指令改變方向或歸零才離開。
        """
        n_cmd = self.ctl.rpm_order / 60.0
        n = self.state.n
        small = 0.02 * self.n_max
        dt = self.dt
        if abs(n_cmd) < 1e-9:
            self._eng_mode = "stopped" if abs(n) < small else "stopping"
            self._n_target, self._n_tau = 0.0, self.n_stop_tau
            self._eng_timer = 0.0
            return
        cmd_dir = 1.0 if n_cmd > 0 else -1.0
        if self._eng_mode == "run" and self._n_target * cmd_dir > 0:
            self._n_target, self._n_tau = n_cmd, self.n_tau
            return
        shaft_dir = 0.0 if abs(n) < small else (1.0 if n > 0 else -1.0)
        if shaft_dir == cmd_dir:
            self._eng_mode = "run"
            self._n_target, self._n_tau = n_cmd, self.n_tau
            self._eng_timer = 0.0
            return
        if shaft_dir != 0.0 or self._eng_mode == "reversing":
            # 換向:燃油切斷、軸轉速衰減;計時自下令起算;逾時且 |n| 低於起動門檻才反向點火
            if self._eng_mode != "reversing":
                self._eng_mode = "reversing"
                self._eng_timer = 0.0
            self._eng_timer += dt
            if self._eng_timer >= self.n_rev_delay and abs(n) <= self.n_start_threshold:
                self._eng_mode = "run"
                self._n_target, self._n_tau = n_cmd, self.n_tau
            else:
                self._n_target, self._n_tau = 0.0, self.n_stop_tau
            return
        # 軸靜止、未在換向:起動延遲
        if self._eng_mode != "starting":
            self._eng_mode = "starting"
            self._eng_timer = 0.0
        self._eng_timer += dt
        if self._eng_timer >= self.n_start_delay:
            self._eng_mode = "run"
            self._n_target, self._n_tau = n_cmd, self.n_tau
        else:
            self._n_target, self._n_tau = 0.0, self.n_stop_tau

    def _autopilot(self) -> None:
        """簡單 PID 航向控制(含 ROT 與舵角限制)。"""
        if not self.ctl.autopilot:
            return
        s = self.state
        err = wrap_pi(self.ctl.autopilot_heading - s.psi)
        rot_cmd = max(-self.ctl.autopilot_rot_limit, min(self.ctl.autopilot_rot_limit, 0.02 * err))
        kp, kd = 3.0, 60.0  # 舵角 = Kp·航向誤差(以 ROT 限制) − Kd·(r − rot_cmd)
        delta_cmd = kp * err - kd * (s.r - rot_cmd)
        lim = min(self.ctl.autopilot_rudder_limit, self.delta_max)
        self.ctl.rudder_order = max(-lim, min(lim, delta_cmd))

    def step(self) -> None:
        """前進一個固定步長(RK4)。凍結或擱淺時狀態不變(時間不前進)。"""
        if self.ctl.frozen:
            return
        self._autopilot()
        self._engine_logic()
        s = self.state
        if self.aground:
            s.u = s.v = s.r = 0.0
            s.t += self.dt
            s.tick += 1
            return
        dt = self.dt
        y0 = s.as_tuple()
        dcmd, nt, ntau, tcmd = self.ctl.rudder_order, self._n_target, self._n_tau, self.ctl.thruster_order
        k1 = self._derivs(y0, dcmd, nt, ntau, tcmd)
        y1 = tuple(a + 0.5 * dt * b for a, b in zip(y0, k1))
        k2 = self._derivs(y1, dcmd, nt, ntau, tcmd)
        y2 = tuple(a + 0.5 * dt * b for a, b in zip(y0, k2))
        k3 = self._derivs(y2, dcmd, nt, ntau, tcmd)
        y3 = tuple(a + dt * b for a, b in zip(y0, k3))
        k4 = self._derivs(y3, dcmd, nt, ntau, tcmd)
        yn = tuple(a + dt / 6.0 * (b1 + 2.0 * b2 + 2.0 * b3 + b4) for a, b1, b2, b3, b4 in zip(y0, k1, k2, k3, k4))
        s.x, s.y, s.psi, s.u, s.v, s.r, s.delta, s.n, s.thr, s.track = yn
        if s.psi >= TWO_PI or s.psi < 0.0:
            s.psi = math.fmod(s.psi, TWO_PI)
            if s.psi < 0.0:
                s.psi += TWO_PI
        # 舵角硬限制、轉速限制
        if s.delta > self.delta_max:
            s.delta = self.delta_max
        elif s.delta < -self.delta_max:
            s.delta = -self.delta_max
        s.t += dt
        s.tick += 1
        # 擱淺/水深檢查
        if self.env.water_depth is not None and self.depth_below_keel() <= 0.0:
            self.aground = True
            s.u = s.v = s.r = 0.0

    def run(self, seconds: float, callback=None) -> None:
        steps = int(round(seconds / self.dt))
        for _ in range(steps):
            self.step()
            if callback is not None and callback(self):
                break

    # ------------------------------------------------------------------
    # 輸出
    # ------------------------------------------------------------------
    def squat(self) -> float:
        h = self.env.water_depth
        if h is None:
            return 0.0
        U = math.hypot(self.state.u, self.state.v)
        fnh = U / math.sqrt(GRAVITY * h)
        if fnh >= 0.95:
            fnh = 0.95
        return self.squat_cs * self.vol / (self.L * self.L) * fnh * fnh / math.sqrt(1.0 - fnh * fnh)

    def depth_below_keel(self) -> float:
        h = self.env.water_depth
        if h is None:
            return 999.0
        return h - max(self.sp.loading.draft_fore_m, self.sp.loading.draft_aft_m) - self.squat()

    def ground_velocity(self) -> tuple[float, float]:
        s = self.state
        ce, cn_ = self.env.current_en
        return s.u * math.sin(s.psi) + s.v * math.cos(s.psi) + ce, s.u * math.cos(s.psi) - s.v * math.sin(s.psi) + cn_

    def state_hash(self) -> str:
        """狀態雜湊(sha256 over 全部浮點狀態的 IEEE-754 位元)。"""
        s = self.state
        payload = struct.pack("<d9dq", s.t, s.x, s.y, s.psi, s.u, s.v, s.r, s.delta, s.n, s.thr, s.tick)
        return hashlib.sha256(payload).hexdigest()

    def state_json(self) -> dict[str, Any]:
        """依 state.schema.json 的自船狀態。"""
        s = self.state
        ug_e, ug_n = self.ground_velocity()
        sog = math.hypot(ug_e, ug_n)
        cog = deg360(math.atan2(ug_e, ug_n)) if sog > 1e-3 else deg360(s.psi)
        lat = self.origin_lat + s.y / 111320.0
        lon = self.origin_lon + s.x / (111320.0 * math.cos(self.origin_lat * DEG))
        wind = self.env
        # 相對風(船體座標)
        we = -wind.wind_speed * math.sin(wind.wind_dir_from) - ug_e
        wn = -wind.wind_speed * math.cos(wind.wind_dir_from) - ug_n
        rel_speed = math.hypot(we, wn)
        rel_dir = deg360(math.atan2(-we, -wn) - s.psi) if rel_speed > 1e-6 else 0.0
        eng_state = "stopped" if self._eng_mode == "stopped" else ("starting" if self._eng_mode in ("starting", "reversing") else "running")
        load_pct = min(100.0, 100.0 * (abs(s.n) / self.n_max) ** 3) if self.n_max > 0 else 0.0
        return {
            "t": round(s.t, 6),
            "tick": s.tick,
            "shipId": self.sp.ship_id,
            "pos": {"lat": lat, "lon": lon, "x": s.x, "y": s.y},
            "heading": deg360(s.psi),
            "cog": cog,
            "sog": sog / KN_TO_MPS,
            "stw": s.u / KN_TO_MPS,
            "rot": math.degrees(s.r) * 60.0,
            "u": s.u,
            "v": s.v,
            "r": s.r,
            "drift": math.degrees(math.atan2(-s.v, s.u)) if math.hypot(s.u, s.v) > 1e-3 else 0.0,
            "rudder": math.degrees(s.delta),
            "rudderOrder": math.degrees(self.ctl.rudder_order),
            "rpm": s.n * 60.0,
            "rpmOrder": self.ctl.rpm_order,
            "telegraph": self.ctl.telegraph,
            "thruster": {"order": self.ctl.thruster_order, "actual": s.thr},
            "depthBelowKeel": self.depth_below_keel(),
            "waterDepth": self.env.water_depth if self.env.water_depth is not None else 999.0,
            "squat": self.squat(),
            "wind": {"trueSpeed": wind.wind_speed / KN_TO_MPS, "trueDir": deg360(wind.wind_dir_from),
                     "relSpeed": rel_speed / KN_TO_MPS, "relDir": rel_dir},
            "current": {"set": deg360(wind.current_set), "drift": wind.current_speed / KN_TO_MPS},
            "loading": self.sp.loading.name,
            "draft": {"fore": self.sp.loading.draft_fore_m, "aft": self.sp.loading.draft_aft_m},
            "engine": {"state": eng_state, "startsRemaining": int(self.sp.engine.get("maxConsecutiveStarts") or 0), "load_pct": load_pct},
            "faults": list(self.faults),
            "flags": {"frozen": self.ctl.frozen, "aground": self.aground, "collision": False},
        }


def make_ship(ship_id: str, loading: str = "full", dt: float = 0.02, coeffs: dict[str, Any] | None = None,
              prefer_file: bool = True) -> MMGShip:
    """便利函式:讀 particulars 與係數檔(無則即時估計)建立模型。"""
    from .coefficients import load_or_estimate
    from .particulars import load_particulars

    sp = load_particulars(ship_id, loading)
    c = coeffs if coeffs is not None else load_or_estimate(sp, prefer_file=prefer_file)
    return MMGShip(sp, c, dt=dt)
