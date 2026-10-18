"""外力項參考實作:拖船、錨與錨鏈(準靜態懸鏈線)、泊位(碰墊 + 碼頭硬接觸 + 繫纜)、岸壁效應(規劃書第 6.2 節 T、A、B 項)。

與 C# ``src/SimCore/Physics/External/`` 逐式對應(TugModel.cs、Catenary.cs、AnchorModel.cs、BerthModel.cs、BankEffect.cs、
ExternalForceModel.cs;設計文件 docs/design/physics-tugs-anchors-mooring.md、physics-bank-effect.md 的「Python 參考實作」章節):

- 力為船體座標 (X, Y, N)(N、N·m):x 向艏、y 向右舷、原點船舯,N 正 = 艏向右(與 MMG 相同)。
- RK4 每個階段以該階段的狀態(位置、航向、對水速度)與步首環境取樣重新計算(``evaluate`` 為純函數);
  跨步狀態(拖船出力/方向、錨位、鏈長、纜長、接觸事件)只在 ``pre_step``(步首)與 ``post_step``(步末)改變。
- 拒絕原因字串與 C# ``ExternalForceModel`` / ``AnchorUnit.Command`` / ``BerthModel.Command`` 相同。
- 單位:內部 SI 與弧度;對外(state、指令)度、噸力(t = 9.80665 kN)、kN。
"""

from __future__ import annotations

import json
import math
import os
import struct
from pathlib import Path
from typing import Any, NamedTuple

GRAVITY = 9.80665
TONNE_FORCE = GRAVITY * 1000.0
KN_TO_MPS = 1852.0 / 3600.0
TWO_PI = 2.0 * math.pi
DEG2RAD = math.pi / 180.0
RAD2DEG = 180.0 / math.pi

# 雜湊標記(C# ExternalForceModel.HashMarker "EXTFORCE"、BankEffectModel.HashMarker "BANKEFCT")
EXTERNAL_HASH_MARKER = 0x45585446_4F524345
BANK_HASH_MARKER = 0x42414E4B_45464354

# 列舉順序與 C# 相同(雜湊用索引;對外名稱 = ContractJson.EnumName 的 camelCase)
TUG_MODES = ("standby", "push", "pull", "free", "unavailable")  # C# TugMode 的序數(雜湊);unavailable 列在最後
TUG_STATUSES = ("standby", "connecting", "repositioning", "working", "free", "parted", "unavailable")
ANCHOR_MODES = ("stowed", "running", "veering", "heaving", "braked")
MOORING_STATUSES = ("stowed", "fast", "heaving", "slacking", "letGo", "parted")
SEABED_TYPES = ("mud", "sand", "clay", "rock")
BODY_POINT_NAMES = ("fwd", "aft", "bow", "stern", "fwdPort", "fwdStbd", "midPort", "midStbd", "aftPort", "aftStbd")
BANK_TYPES = ("vertical", "sloped")


class Kin(NamedTuple):
    """外力計算看到的運動狀態:ENU 位置 (x 東、y 北)、航向 psi(自北順時針,弧度)、船體座標對水速度 u、v、艏搖 r。"""

    x: float
    y: float
    psi: float
    u: float
    v: float
    r: float


class Env(NamedTuple):
    """步首環境取樣(C# EnvironmentSample 的外力部分):均勻流(ENU,m/s)與水深(m;深水 math.inf)。"""

    current_e: float
    current_n: float
    depth: float


ZERO = (0.0, 0.0, 0.0)


def wrap_rad_pi(a: float) -> float:
    """(−π, π](與 C# Units.WrapRadPi 同式:先取餘數再折一次)。"""
    a = math.fmod(a, TWO_PI)
    if a > math.pi:
        a -= TWO_PI
    if a <= -math.pi:
        a += TWO_PI
    return a


def normalize_heading_deg(d: float) -> float:
    """[0, 360)(C# Units.NormalizeHeadingDeg)。"""
    if math.isnan(d) or math.isinf(d):
        return 0.0
    h = math.fmod(d, 360.0)
    if h < 0:
        h += 360.0
    if h >= 360.0:
        h -= 360.0
    return h


def to_t(newtons: float) -> float:
    return newtons / TONNE_FORCE


# ---------------------------------------------------------------------------- 船體座標


def at_point(fx: float, fy: float, xb: float, yb: float) -> tuple[float, float, float]:
    """船體座標力 (fx, fy) 作用於 (xb, yb):N = xb·fy − yb·fx(C# BodyForce.AtPoint)。"""
    return (fx, fy, xb * fy - yb * fx)


def from_enu(fe: float, fn: float, xb: float, yb: float, psi: float) -> tuple[float, float, float]:
    """ENU 力作用於船體座標點(C# BodyForce.FromEnu)。"""
    s = math.sin(psi)
    c = math.cos(psi)
    return at_point(fe * s + fn * c, fe * c - fn * s, xb, yb)


def add(a: tuple[float, float, float], b: tuple[float, float, float]) -> tuple[float, float, float]:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def body_to_enu(k: Kin, xb: float, yb: float) -> tuple[float, float]:
    sn = math.sin(k.psi)
    c = math.cos(k.psi)
    return (k.x + xb * sn + yb * c, k.y + xb * c - yb * sn)


def enu_to_body(k: Kin, e: float, n: float) -> tuple[float, float]:
    sn = math.sin(k.psi)
    c = math.cos(k.psi)
    de = e - k.x
    dn = n - k.y
    return (de * sn + dn * c, de * c - dn * sn)


def point_ground_velocity(k: Kin, env: Env, xb: float, yb: float) -> tuple[float, float]:
    """船體座標點的對地速度(ENU;C# BodyFrame.PointGroundVelocity)。"""
    ub = k.u - k.r * yb
    vb = k.v + k.r * xb
    sn = math.sin(k.psi)
    c = math.cos(k.psi)
    return (ub * sn + vb * c + env.current_e, ub * c - vb * sn + env.current_n)


def resolve_body_point(p: Any, lpp: float, b: float) -> tuple[float, float]:
    """具名點或 {x, y} → 船體座標(C# ScenarioBodyPoint.Resolve)。"""
    if isinstance(p, dict):
        return (float(_num(p.get("x")) or 0.0), float(_num(p.get("y")) or 0.0))
    name = str(p).lower()
    table = {
        "fwd": (0.45 * lpp, 0.0), "bow": (0.45 * lpp, 0.0), "aft": (-0.45 * lpp, 0.0), "stern": (-0.45 * lpp, 0.0),
        "fwdport": (0.40 * lpp, -0.45 * b), "fwdstbd": (0.40 * lpp, 0.45 * b), "midport": (0.0, -0.5 * b),
        "midstbd": (0.0, 0.5 * b), "aftport": (-0.40 * lpp, -0.45 * b), "aftstbd": (-0.40 * lpp, 0.45 * b),
    }
    return table.get(name, (0.0, 0.0))


def _num(v: Any) -> float | None:
    """JSON 數值或數值字串(C# SimCommand.AsDouble 與情境寬鬆數值);布林、其他 → None。"""
    if v is None or isinstance(v, bool):
        return None
    if isinstance(v, (int, float)):
        return float(v)
    if isinstance(v, str):
        try:
            return float(v.strip())
        except ValueError:
            return None
    return None


def _json_num(o: dict[str, Any] | None, key: str) -> float | None:
    """particulars 的嚴格 JSON 數值(C# AnchorParticulars.Num:ValueKind == Number)。"""
    if not isinstance(o, dict):
        return None
    v = o.get(key)
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        return None
    return float(v)


# ============================================================================ 拖船(TugModel.cs)


class TugUnit:
    """單艘拖船(設計文件第 2 節):出力一階上升、方向有限速率迴轉、速度效能遞減、推拉換位與帶纜時間、拖纜斷裂。"""

    def __init__(self, t: dict[str, Any], lpp: float, breadth: float, dt: float) -> None:
        self.id = str(t["id"])
        self.name = t.get("name")
        self.bollard_pull_n = float(t["bollardPull_t"]) * TONNE_FORCE
        self.attach_x, self.attach_y = resolve_body_point(t.get("attach") or "fwd", lpp, breadth)
        self.line_length_m = float(t["lineLength_m"])
        modes = t.get("modes") or ["push", "pull"]
        self.allow_push = "push" in modes
        self.allow_pull = "pull" in modes
        self.rise_tau_s = float(t["riseTime_s"])
        trd = t.get("turnRate_degPerSec")
        if trd is None:
            trd = (float(t["repositionSpeed_kn"]) * KN_TO_MPS / max(1.0, self.line_length_m)) * RAD2DEG
        self.turn_rate = float(trd) * DEG2RAD
        self.push_full = float(t["pushFullUpTo_kn"]) * KN_TO_MPS
        self.push_half = float(t["pushHalfAt_kn"]) * KN_TO_MPS
        self.pull_full = float(t["pullFullUpTo_kn"]) * KN_TO_MPS
        self.pull_half = float(t["pullHalfAt_kn"]) * KN_TO_MPS
        sps = 1.0 / dt
        self.mode_change_ticks = int(round(max(0.0, float(t["modeChange_s"])) * sps))
        self.connect_ticks = int(round(max(0.0, float(t["connect_s"])) * sps))
        self._alpha = 1.0 - math.exp(-dt / self.rise_tau_s)

        init = t.get("initial") or {}
        mode = {"push": "push", "pull": "pull", "free": "free"}.get(init.get("mode"), "standby")
        d0 = init.get("direction_deg")
        self.direction = self.direction_order = wrap_rad_pi((90.0 if d0 is None else float(d0)) * DEG2RAD)
        p0 = init.get("power_pct")
        self.power_order = min(1.0, max(0.0, (0.0 if p0 is None else float(p0)) / 100.0))
        self.mode = mode
        self.status = {"push": "working", "pull": "working", "free": "free"}.get(mode, "standby")
        self.timer_ticks = 0
        self.line_parted = False
        self.last_working_mode = mode if mode in ("push", "pull") else "standby"
        self.force_n = self.bollard_pull_n * self.power_order if self.status == "working" else 0.0
        self.efficiency = 1.0

    def speed_efficiency(self, mode: str, speed: float) -> float:
        """e(V) = 1(V ≤ V_full);否則 1 / (1 + ((V − V_full)/(V_half − V_full))²)。"""
        full, half = (self.push_full, self.push_half) if mode == "push" else (self.pull_full, self.pull_half)
        if speed <= full:
            return 1.0
        z = (speed - full) / max(1e-6, half - full)
        return 1.0 / (1.0 + z * z)

    def command(self, mode: str, direction_deg: float | None, power_pct: float | None) -> str | None:
        """指令;None = 接受,否則拒絕原因(tugUnavailable、tugModeUnavailable、tugLineParted)。
        mode unavailable:拖船立即失去出力並離開,之後任何指令(含再下 unavailable)以 tugUnavailable 拒絕(C# TugUnit.Command)。"""
        if self.status == "unavailable":
            return "tugUnavailable"
        if mode == "unavailable":
            self.mode = "unavailable"
            self.status = "unavailable"
            self.force_n = 0.0
            self.timer_ticks = 0
            self.last_working_mode = "standby"
            return None
        if (mode == "push" and not self.allow_push) or (mode == "pull" and not self.allow_pull):
            return "tugModeUnavailable"
        if mode == "pull" and self.line_parted:
            return "tugLineParted"
        if direction_deg is not None:
            self.direction_order = wrap_rad_pi(direction_deg * DEG2RAD)
        if power_pct is not None:
            self.power_order = min(1.0, max(0.0, power_pct / 100.0))
        side = self.last_working_mode
        self.mode = mode
        if mode == "free":
            if self.status != "parted":
                self.status = "free"
            self.force_n = 0.0
            self.timer_ticks = 0
            self.last_working_mode = "standby"
        elif mode == "standby":
            if self.status in ("free", "parted"):
                self.timer_ticks = self.connect_ticks
                self.status = "connecting" if self.timer_ticks > 0 else "standby"
            elif self.status not in ("connecting", "repositioning"):
                self.status = "standby"
        else:
            if self.status in ("free", "parted"):
                self.timer_ticks = self.connect_ticks
                self.status = "connecting" if self.timer_ticks > 0 else "working"
            elif self.status in ("connecting", "repositioning"):
                pass
            elif side in ("push", "pull") and side != mode:
                self.timer_ticks = self.mode_change_ticks
                self.status = "repositioning" if self.timer_ticks > 0 else "working"
            else:
                self.status = "working"
            self.last_working_mode = mode
        return None

    def part_line(self) -> float:
        if self.status == "unavailable":
            return 0.0
        before = self.force_n
        self.line_parted = True
        if self.mode in ("pull", "standby"):
            self.force_n = 0.0
            self.mode = "free"
            self.status = "parted"
            self.timer_ticks = 0
            self.last_working_mode = "standby"
        return before

    def line_restored(self) -> None:
        self.line_parted = False
        if self.status == "parted":
            self.status = "free"

    def pre_step(self, dt: float, speed: float) -> None:
        if self.timer_ticks > 0:
            self.timer_ticks -= 1
            if self.timer_ticks == 0:
                self.status = "working" if self.mode in ("push", "pull") else "standby"
        self.efficiency = self.speed_efficiency(self.mode, speed) if self.mode in ("push", "pull") else 1.0
        target = self.bollard_pull_n * self.power_order * self.efficiency if self.status == "working" else 0.0
        if self.status in ("free", "parted", "unavailable"):
            self.force_n = 0.0
        else:
            self.force_n += (target - self.force_n) * self._alpha
        if self.status not in ("free", "parted", "unavailable"):
            diff = wrap_rad_pi(self.direction_order - self.direction)
            step = self.turn_rate * dt
            self.direction = wrap_rad_pi(self.direction + (step if diff > step else (-step if diff < -step else diff)))

    def force(self) -> tuple[float, float, float]:
        if self.force_n == 0.0:
            return ZERO
        return at_point(self.force_n * math.cos(self.direction), self.force_n * math.sin(self.direction), self.attach_x, self.attach_y)


# ============================================================================ 懸鏈線(Catenary.cs)


class CatenaryResult(NamedTuple):
    H: float
    top_tension: float
    on_bottom: float
    suspended: float
    uplift: float
    regime: str  # hanging | slack | touchdown | suspended | taut


CATENARY_ITERATIONS = 80
CATENARY_ZERO = CatenaryResult(0.0, 0.0, 0.0, 0.0, 0.0, "hanging")


def _asinh(z: float) -> float:
    return math.log(z + math.sqrt(z * z + 1.0))


def _atanh(z: float) -> float:
    return 0.5 * math.log((1.0 + z) / (1.0 - z))


def touchdown_distance(a: float, length: float, h: float) -> float:
    """躺底區給定 a = H/w 的水平距離 X = (L − s) + a·asinh(s/a),s = √(h² + 2ha)。"""
    s = math.sqrt(h * h + 2.0 * h * a)
    if s > length:
        s = length
    return (length - s) + a * _asinh(s / a)


def max_touchdown_a(length: float, h: float) -> float:
    return (length * length - h * h) / (2.0 * h)


def _capped(h_cap: float, length: float, h: float, w: float, regime: str, on_bottom: float, suspended: float) -> CatenaryResult:
    cos = math.sqrt(max(1e-12, 1.0 - (h / length) * (h / length)))
    return CatenaryResult(h_cap, max(h_cap / cos, h_cap + w * h), on_bottom, suspended,
                          0.0 if on_bottom > 0 else math.asin(min(1.0, h / length)), regime)


def catenary_solve(x: float, length: float, h: float, w: float, h_cap: float) -> CatenaryResult:
    """準靜態非彈性懸鏈線(設計文件第 3.2 節;C# Catenary.Solve 逐式對應,對數二分固定 80 次)。"""
    if not (h > 0.0):
        h = 1e-3
    if length <= h or w <= 0.0:
        return CatenaryResult(0.0, w * max(0.0, length), 0.0, max(0.0, length), 0.0, "hanging")
    if x <= length - h:
        return CatenaryResult(0.0, w * h, length - h, h, 0.0, "slack")
    a_max = max_touchdown_a(length, h)
    x_td = a_max * _asinh(length / a_max)
    if x <= x_td:
        lo = math.log(max(1e-9, 1e-9 * h))
        hi = math.log(a_max)
        for _ in range(CATENARY_ITERATIONS):
            mid = 0.5 * (lo + hi)
            if touchdown_distance(math.exp(mid), length, h) < x:
                lo = mid
            else:
                hi = mid
        a = math.exp(0.5 * (lo + hi))
        h_t = w * a
        s = min(length, math.sqrt(h * h + 2.0 * h * a))
        if h_t > h_cap:
            return _capped(h_cap, length, h, w, "touchdown", length - s, s)
        return CatenaryResult(h_t, h_t + w * h, length - s, s, 0.0, "touchdown")
    chord = math.sqrt(length * length - h * h)
    if x >= chord:
        return _capped(h_cap, length, h, w, "taut", 0.0, length)
    a_lo = a_max
    a_hi = a_max
    i = 0
    while i < 200 and 2.0 * a_hi * math.sinh(x / (2.0 * a_hi)) > chord:
        a_hi *= 2.0
        i += 1
    l_lo = math.log(a_lo)
    l_hi = math.log(a_hi)
    for _ in range(CATENARY_ITERATIONS):
        mid = 0.5 * (l_lo + l_hi)
        am = math.exp(mid)
        if 2.0 * am * math.sinh(x / (2.0 * am)) > chord:
            l_lo = mid
        else:
            l_hi = mid
    a_s = math.exp(0.5 * (l_lo + l_hi))
    h_s = w * a_s
    if h_s > h_cap:
        return _capped(h_cap, length, h, w, "suspended", 0.0, length)
    x1 = a_s * _atanh(h / length) - 0.5 * x
    uplift = math.atan(math.sinh(x1 / a_s)) if x1 > 0.0 else 0.0
    top = h_s * math.cosh((x1 + x) / a_s)
    return CatenaryResult(h_s, top, 0.0, length, uplift, "suspended")


# ============================================================================ 錨(AnchorModel.cs)

RHO_WATER = 1025.0
RHO_STEEL = 7850.0


class AnchorParticulars:
    """particulars.json anchors(缺欄以估計式補齊,``estimated`` 列出;C# AnchorParticulars.FromShip)。"""

    def __init__(self, **kw: Any) -> None:
        self.__dict__.update(kw)

    @property
    def windlass_overload_pull_n(self) -> float:
        return 1.5 * self.windlass_pull_n

    @property
    def chain_submerged_weight(self) -> float:
        return self.chain_mass_per_m * GRAVITY * (1.0 - RHO_WATER / RHO_STEEL)

    def holding_coefficient(self, bed: str) -> float:
        hhp = self.holding_class == "hhp"
        if hhp:
            return {"mud": 5.0, "sand": 7.0, "clay": 8.0, "rock": 1.5}[bed]
        return {"mud": 2.5, "sand": 3.5, "clay": 4.0}.get(bed, 1.0)

    @staticmethod
    def chain_friction(bed: str) -> float:
        return {"mud": 0.6, "sand": 0.75, "clay": 0.8}.get(bed, 0.5)

    @classmethod
    def from_raw(cls, raw: dict[str, Any]) -> "AnchorParticulars | None":
        a = raw.get("anchors")
        if not isinstance(a, dict):
            return None
        est: list[str] = []
        d = _json_num(a, "chainDiameter_mm") or 0.0
        if d <= 0:
            return None
        shackle = _json_num(a, "shackleLength_m")
        shackle = 27.5 if shackle is None else shackle
        mass = _json_num(a, "mass_kg")
        if mass is None:
            mass = _json_num(a, "massEstimate_kg")
            est.append("mass_kg")
        if mass is None:
            mass = 1.25 * d * d
        chain = a.get("chain") if isinstance(a.get("chain"), dict) else None
        mpm = _json_num(chain, "mass_kgPerM")
        if mpm is None:
            mpm = 0.0219 * d * d
            est.append("chain.mass_kgPerM")
        brk = _json_num(chain, "breakingLoad_kN")
        if brk is None:
            brk = 0.0196 * d * d * (44.0 - 0.08 * d)
            est.append("chain.breakingLoad_kN")
        heave = _json_num(a, "heavingRate_mPerMin")
        if heave is None:
            heave = _json_num(a, "heavingRateEstimate_mPerMin")
            heave = 9.0 if heave is None else heave
            est.append("heavingRate_mPerMin")
        wl = a.get("windlass") if isinstance(a.get("windlass"), dict) else None
        pull = _json_num(wl, "pull_kN")
        if pull is None:
            pull = 42.5 * d * d / 1000.0
            est.append("windlass.pull_kN")
        brake = _json_num(wl, "brakeHolding_kN")
        if brake is None:
            brake = 0.45 * brk
            est.append("windlass.brakeHolding_kN")
        hawse = a.get("hawsePipe") if isinstance(a.get("hawsePipe"), dict) else None
        hull = raw["hull"]
        lpp = float(hull["lengthBetweenPerpendiculars_m"])
        hx, hy, hz = _json_num(hawse, "x_m"), _json_num(hawse, "y_m"), _json_num(hawse, "heightAboveKeel_m")
        if hx is None or hy is None or hz is None:
            est.append("hawsePipe")
        typ = a.get("type") if isinstance(a.get("type"), str) else ""
        hc = a.get("holdingClass")
        cls_ = hc.lower() if isinstance(hc, str) else ("hhp" if typ.upper().startswith("AC") else "stockless")
        sp = _json_num(a, "shacklesPort")
        ss = _json_num(a, "shacklesStbd")
        return cls(
            type=typ, anchor_mass_kg=mass, chain_diameter_mm=d, shackle_length_m=shackle,
            port_length_m=(9.0 if sp is None else sp) * shackle, starboard_length_m=(9.0 if ss is None else ss) * shackle,
            chain_mass_per_m=mpm, chain_breaking_load_n=brk * 1000.0, heaving_rate_mps=heave / 60.0,
            windlass_pull_n=pull * 1000.0, brake_holding_n=brake * 1000.0,
            hawse_x=hx if hx is not None else 0.5 * lpp - 0.03 * lpp,
            hawse_half_breadth=abs(hy if hy is not None else 0.15 * float(hull["breadth_m"])),
            hawse_height_above_keel=hz if hz is not None else float(hull["depth_m"]) - 0.5,
            holding_class=cls_, estimated=est,
        )


class AnchorUnit:
    """單側錨與錨鏈(設計文件第 3 節;C# AnchorUnit 逐式對應)。"""

    RUN_RATE = 2.0
    VEER_RATE = 0.5
    SLIP_RATE = 1.0
    UPLIFT_ZERO = 15.0 * math.pi / 180.0
    DRAG_HYSTERESIS = 0.95
    BROUGHT_UP_SOG = 0.15
    # 絞盤停轉與剎車滑出的遲滯(C# OverloadReleaseFraction / OverloadClearS):超載期間旗標,
    # 張力連續 5 s 低於門檻 0.9 倍才解除;停轉期間張力 ≥ 0.9 × 過載拉力時不收鏈
    OVERLOAD_RELEASE = 0.9
    OVERLOAD_CLEAR_S = 5.0

    def __init__(self, side: str, spec: AnchorParticulars, draft_fore: float) -> None:
        self.side = side
        self.spec = spec
        self.max_length = spec.port_length_m if side == "port" else spec.starboard_length_m
        self.hawse_xb = spec.hawse_x
        self.hawse_yb = -spec.hawse_half_breadth if side == "port" else spec.hawse_half_breadth
        self.hawse_freeboard = max(0.5, spec.hawse_height_above_keel - draft_fore)
        # 跨步狀態
        self.mode = "stowed"
        self.length = 0.0
        self.target_length: float | None = None
        self.on_bottom = False
        self.anchor_x = 0.0
        self.anchor_y = 0.0
        self.dragging = False
        self.brought_up_pending = False
        self.fully_out = False
        self.slipping = False
        self.stalled = False
        self.stall_quiet_s = 0.0
        self.slip_quiet_s = 0.0
        # 最近步末(廣播)
        self.last = CATENARY_ZERO
        self.last_holding = 0.0

    def vertical_span(self, depth: float) -> float:
        return max(0.5, depth) + self.hawse_freeboard

    def solve(self, k: Kin, depth: float) -> tuple[CatenaryResult, float, float]:
        """(懸鏈線解, 指向錨的單位向量 E, N)。"""
        if not self.on_bottom or self.mode == "stowed":
            return CatenaryResult(0.0, self.spec.chain_submerged_weight * self.length, 0.0, self.length, 0.0, "hanging"), 0.0, 0.0
        he, hn = body_to_enu(k, self.hawse_xb, self.hawse_yb)
        de = self.anchor_x - he
        dn = self.anchor_y - hn
        x = math.sqrt(de * de + dn * dn)
        dir_e = dir_n = 0.0
        if x > 1e-9:
            dir_e, dir_n = de / x, dn / x
        r = catenary_solve(x, self.length, self.vertical_span(depth), self.spec.chain_submerged_weight, self.spec.chain_breaking_load_n)
        return r, dir_e, dir_n

    def force(self, k: Kin, depth: float) -> tuple[float, float, float]:
        r, de, dn = self.solve(k, depth)
        if r.H <= 0.0:
            return ZERO
        return from_enu(r.H * de, r.H * dn, self.hawse_xb, self.hawse_yb, k.psi)

    def holding_power(self, r: CatenaryResult, bed: str, factor: float) -> float:
        uplift = max(0.0, 1.0 - r.uplift / self.UPLIFT_ZERO)
        anchor = self.spec.anchor_mass_kg * GRAVITY * self.spec.holding_coefficient(bed) * uplift
        chain = AnchorParticulars.chain_friction(bed) * self.spec.chain_submerged_weight * r.on_bottom
        return factor * (anchor + chain)

    def command(self, action: str, target: float | None) -> str | None:
        if action == "letGo":
            if self.mode != "stowed" and (self.on_bottom or self.mode in ("running", "veering")):
                return "anchorAlreadyOut"
            if self.fully_out:
                return "chainFullyOut"
            if target is not None and target < self.length - 1e-9:
                return "invalidTarget"
            self.mode = "running"
            self.target_length = min(max(target, self.length), self.max_length) if target is not None else None
            self.brought_up_pending = True
            self._clear_overload()
            return None
        if action == "veer":
            if self.mode == "stowed":
                return "anchorStowed"
            if self.fully_out or self.length >= self.max_length - 1e-9:
                return "chainFullyOut"
            self.mode = "veering"
            self.target_length = min(max(target, self.length), self.max_length) if target is not None else None
            self._clear_overload()
            return None
        if action == "heave":
            if self.mode == "stowed":
                return "anchorStowed"
            if target is not None and target >= self.length:
                return "invalidTarget"
            self.mode = "heaving"
            self.target_length = max(0.0, target) if target is not None else None
            self.fully_out = False
            self._clear_overload()
            return None
        if action == "brake":
            if self.mode == "stowed":
                return "anchorStowed"
            self.mode = "braked"
            self.target_length = None
            self._clear_overload()
            return None
        if action == "release":
            if self.mode == "stowed":
                self.brought_up_pending = True
            if self.fully_out:
                return "chainFullyOut"
            self.mode = "running"
            self.target_length = None
            self._clear_overload()
            return None
        return "unknownAction"

    def post_step(self, k: Kin, depth: float, dt: float, bed: str, holding_factor: float, drag_speed: float, sog: float,
                  events: list[dict[str, Any]]) -> None:
        """步末(C# AnchorUnit.PostStep):鏈長、著底/離底、走錨、帶上、事件。"""
        if self.mode == "stowed":
            self.last = CATENARY_ZERO
            self.last_holding = 0.0
            return
        h = self.vertical_span(depth)
        r, _, _ = self.solve(k, depth)
        sp = self.spec
        if self.mode in ("running", "veering"):
            rate = self.VEER_RATE if self.mode == "veering" else self.RUN_RATE
            limit = max(min(self.target_length if self.target_length is not None else self.max_length, self.max_length), self.length)
            self.length = min(limit, self.length + rate * dt)
            if self.length >= limit - 1e-9:
                self.length = limit
                if self.length >= self.max_length - 1e-9:
                    self._reach_bitter_end(events)
                self.mode = "braked"
                self.target_length = None
        elif self.mode == "heaving":
            # 遲滯:超過過載拉力即進入停轉期間(發一次事件);期間內張力 ≥ 0.9 × 過載拉力時不收鏈,
            # 連續 OVERLOAD_CLEAR_S 秒低於 0.9 倍才解除(再超載才再發事件)
            limit = sp.windlass_overload_pull_n
            over = r.top_tension > limit
            if self.stalled:
                released, self.stall_quiet_s = self._relax(r.top_tension, limit, dt, self.stall_quiet_s)
                self.stalled = not released
            if over and not self.stalled:
                self.stalled = True
                events.append(_event("anchorWindlassStalled", self.side, f"{self.side} 絞盤超載停轉",
                                     {"side": self.side, "tension_t": r.top_tension / TONNE_FORCE,
                                      "windlassLimit_t": limit / TONNE_FORCE}))
            blocked = over or (self.stalled and r.top_tension >= self.OVERLOAD_RELEASE * limit)
            if not blocked:
                self.length = max(self.target_length if self.target_length is not None else 0.0, self.length - sp.heaving_rate_mps * dt)
                if self.target_length is not None and self.length <= self.target_length + 1e-9:
                    self.mode = "braked"
                    self.target_length = None
                self._relax_slip(r.top_tension, dt)
            else:
                self._slip_if_over_brake(k, depth, dt, r, events)
        elif self.mode == "braked":
            self._slip_if_over_brake(k, depth, dt, r, events)
        if self.mode not in ("braked", "heaving"):
            self.slipping = False
            self.slip_quiet_s = 0.0
        if self.mode != "heaving":
            self._clear_stall()

        if not self.on_bottom and self.length > h:
            self.on_bottom = True
            self.anchor_x, self.anchor_y = body_to_enu(k, self.hawse_xb, self.hawse_yb)
        elif self.on_bottom and self.length <= h:
            self.on_bottom = False
            self.dragging = False
            events.append(_event("anchorAweigh", self.side, f"{self.side} 錨離底(anchor aweigh)", {"side": self.side, "length_m": self.length}))
        if not self.on_bottom:
            self.anchor_x, self.anchor_y = body_to_enu(k, self.hawse_xb, self.hawse_yb)
            if self.mode == "heaving" and self.length <= 1e-9:
                self.length = 0.0
                self.mode = "stowed"
                self.target_length = None
                self._clear_stall()
                events.append(_event("anchorHoused", self.side, f"{self.side} 錨收妥", {"side": self.side}))
            self.last = self.solve(k, depth)[0]
            self.last_holding = 0.0
            return

        r, dir_e, dir_n = self.solve(k, depth)
        holding = self.holding_power(r, bed, holding_factor)
        excess = r.H - holding
        if not self.dragging and excess > 0.0:
            self.dragging = True
            events.append(_event("anchorDragging", self.side, f"{self.side} 走錨",
                                 {"side": self.side, "tension_t": to_t(r.H), "holding_t": to_t(holding), "length_m": self.length}))
        elif self.dragging and r.H <= self.DRAG_HYSTERESIS * holding:
            self.dragging = False
            events.append(_event("anchorHolding", self.side, f"{self.side} 錨抓住",
                                 {"side": self.side, "tension_t": to_t(r.H), "holding_t": to_t(holding), "length_m": self.length}))
        if self.dragging and excess > 0.0:
            v = drag_speed * min(1.0, excess / max(1.0, 0.25 * holding))
            self.anchor_x -= dir_e * v * dt
            self.anchor_y -= dir_n * v * dt
            r = self.solve(k, depth)[0]
        if self.brought_up_pending and self.mode == "braked" and not self.dragging and r.H > 0.0 and sog < self.BROUGHT_UP_SOG:
            self.brought_up_pending = False
            events.append(_event("anchorBroughtUp", self.side, f"{self.side} 錨已帶上(brought up)",
                                 {"side": self.side, "length_m": self.length, "tension_t": to_t(r.top_tension)}))
        self.last = r
        self.last_holding = holding

    def _slip_if_over_brake(self, k: Kin, depth: float, dt: float, r: CatenaryResult, events: list[dict[str, Any]]) -> None:
        sp = self.spec
        if self.fully_out or not self.on_bottom:
            self.slipping = False
            self.slip_quiet_s = 0.0
            return
        if not (r.top_tension > sp.brake_holding_n):
            # 遲滯:滑出期間須連續 5 s 低於 0.9 × 剎車握持力才清除旗標(期間再滑出不重發事件)
            self._relax_slip(r.top_tension, dt)
            return
        self.slip_quiet_s = 0.0
        l0 = self.length
        l1 = min(self.max_length, self.length + self.SLIP_RATE * dt)
        for _ in range(40):
            self.length = 0.5 * (l0 + l1)
            if self.solve(k, depth)[0].top_tension > sp.brake_holding_n:
                l0 = self.length
            else:
                l1 = self.length
        self.length = l1
        if not self.slipping:
            self.slipping = True
            events.append(_event("anchorBrakeSlipping", self.side, f"{self.side} 錨鏈剎車滑出",
                                 {"side": self.side, "tension_t": r.top_tension / TONNE_FORCE,
                                  "brakeHolding_t": sp.brake_holding_n / TONNE_FORCE, "length_m": self.length}))
        if self.length >= self.max_length - 1e-9:
            self.length = self.max_length
            self._reach_bitter_end(events)

    @classmethod
    def _relax(cls, tension: float, threshold: float, dt: float, quiet: float) -> tuple[bool, float]:
        """停轉/滑出期間的解除計時(C# Relax):回傳 (是否解除, 新計時)。"""
        if tension < cls.OVERLOAD_RELEASE * threshold:
            quiet += dt
            if quiet >= cls.OVERLOAD_CLEAR_S - 1e-9:
                return True, 0.0
            return False, quiet
        return False, 0.0

    def _relax_slip(self, tension: float, dt: float) -> None:
        if not self.slipping:
            return
        released, self.slip_quiet_s = self._relax(tension, self.spec.brake_holding_n, dt, self.slip_quiet_s)
        if released:
            self.slipping = False

    def _clear_stall(self) -> None:
        self.stalled = False
        self.stall_quiet_s = 0.0

    def _clear_overload(self) -> None:
        """改下指令時立即解除停轉與滑出旗標(C# AnchorUnit.ClearOverload;設計文件第 3.3 節)。"""
        self._clear_stall()
        self.slipping = False
        self.slip_quiet_s = 0.0

    def _reach_bitter_end(self, events: list[dict[str, Any]]) -> None:
        if self.fully_out:
            return
        self.fully_out = True
        events.append(_event("anchorChainFullyOut", self.side, f"{self.side} 錨鏈全部送出(到鏈端)",
                             {"side": self.side, "length_m": self.max_length, "shackles": self.max_length / self.spec.shackle_length_m}))

    def initialise_anchored(self, k: Kin, length: float, bearing: float, depth: float, bed: str, holding_factor: float) -> None:
        """初始已錨泊:錨在錨鏈孔 ``bearing`` 方向、水平張力約 0.25 × 錨抓力的距離(C# InitialiseAnchored)。"""
        self.length = min(length, self.max_length)
        h = self.vertical_span(depth)
        he, hn = body_to_enu(k, self.hawse_xb, self.hawse_yb)
        if self.length <= h:
            self.mode = "braked"
            self.on_bottom = False
            self.anchor_x, self.anchor_y = he, hn
            return
        w = self.spec.chain_submerged_weight
        h0 = 0.25 * holding_factor * self.spec.anchor_mass_kg * GRAVITY * self.spec.holding_coefficient(bed)
        a = min(h0 / w, max_touchdown_a(self.length, h))
        x = touchdown_distance(a, self.length, h)
        self.mode = "braked"
        self.on_bottom = True
        self.anchor_x = he + x * math.sin(bearing)
        self.anchor_y = hn + x * math.cos(bearing)
        self.fully_out = self.length >= self.max_length - 1e-9

    @property
    def status(self) -> str:
        if self.mode == "stowed":
            return "stowed"
        if not self.on_bottom:
            return "running" if self.mode in ("running", "veering") else "aweigh"
        if self.dragging:
            return "dragging"
        if self.mode == "heaving":
            return "heaving"
        if self.mode in ("running", "veering"):
            return "running"
        return "holding"


def _event(kind: str, item_id: str, message: str, data: dict[str, Any]) -> dict[str, Any]:
    """外力事件(C# ExternalEvent → SimEvent:kind、itemId、message、data)。"""
    return {"kind": kind, "itemId": item_id, "message": message, "data": data}


# ============================================================================ 泊位(BerthModel.cs)


class FenderType:
    """碰墊型式:分段線性壓縮–反力曲線,超過最後一點以觸底剛度 20·R/H 外插。"""

    def __init__(self, d: dict[str, Any]) -> None:
        self.height = float(d["height_m"])
        self.width = float(d["width_m"])
        self.rated_reaction = float(d["ratedReaction_kN"]) * 1000.0
        rd_pct = float(d["ratedDeflection_pct"])
        self.rated_deflection = rd_pct / 100.0 * self.height
        self.damping = float(d["damping_kNs_per_m"]) * 1000.0
        self.friction = float(d["friction"])
        curve = d.get("curve")
        if not curve or len(curve) < 2:
            curve = [[0.0, 0.0], [rd_pct, 100.0]]
        self._d = [float(p[0]) / 100.0 * self.height for p in curve]
        self._r = [float(p[1]) / 100.0 * self.rated_reaction for p in curve]
        self._e = [0.0] * len(self._d)
        for i in range(1, len(self._d)):
            self._e[i] = self._e[i - 1] + 0.5 * (self._r[i] + self._r[i - 1]) * (self._d[i] - self._d[i - 1])
        self._bottom = 20.0 * self.rated_reaction / self.height
        re = d.get("ratedEnergy_kNm")
        self.rated_energy = float(re) * 1000.0 if re is not None else self.energy(self.rated_deflection)

    def reaction(self, delta: float) -> float:
        if delta <= 0.0:
            return 0.0
        d, r = self._d, self._r
        n = len(d)
        if delta >= d[n - 1]:
            return r[n - 1] + self._bottom * (delta - d[n - 1])
        i = 1
        while d[i] < delta:
            i += 1
        f = (delta - d[i - 1]) / (d[i] - d[i - 1])
        return r[i - 1] + f * (r[i] - r[i - 1])

    def energy(self, delta: float) -> float:
        if delta <= 0.0:
            return 0.0
        d = self._d
        n = len(d)
        if delta >= d[n - 1]:
            x = delta - d[n - 1]
            return self._e[n - 1] + self._r[n - 1] * x + 0.5 * self._bottom * x * x
        i = 1
        while d[i] < delta:
            i += 1
        rd = self.reaction(delta)
        return self._e[i - 1] + 0.5 * (self._r[i - 1] + rd) * (delta - d[i - 1])

    def max_reaction(self, delta: float) -> float:
        m = self.reaction(delta)
        for i in range(len(self._d)):
            if self._d[i] > delta:
                break
            m = max(m, self._r[i])
        return m


class QuaySegment(NamedTuple):
    ax: float
    ay: float
    tx: float
    ty: float
    nx: float
    ny: float
    length: float
    chainage: float


class Fender:
    def __init__(self, fid: str, ftype: FenderType, segment: int, s: float) -> None:
        self.id = fid
        self.type = ftype
        self.segment = segment
        self.s = s
        self.in_contact = False
        self.peak_reported = False
        self.max_deflection = 0.0
        self.approach_speed = 0.0
        self.last_deflection = 0.0
        self.last_reaction = 0.0


class Bollard(NamedTuple):
    id: str
    x: float
    y: float
    swl_t: float | None


class MooringLine:
    """繫纜:T = MBL·(ε/ε_b)^n(只拉不推)+ Kelvin–Voigt 阻尼(τ = 1 s),夾在 [0, 1.5·MBL]。"""

    DAMPING_TAU = 1.0

    def __init__(self, line: dict[str, Any], lpp: float, breadth: float) -> None:
        self.id = str(line["id"])
        self.fair_x, self.fair_y = resolve_body_point(line.get("fairlead"), lpp, breadth)
        self.mbl = float(line["mbl_t"]) * TONNE_FORCE
        self.break_strain = float(line["breakStrain"])
        self.exponent = float(line["exponent"])
        self.max_length = float(line["length_m"])
        self.heave_rate = float(line["heaveRate_mPerMin"]) / 60.0
        self.winch_pull = float(line["winchPull_t"]) * TONNE_FORCE
        self.pretension = float(line["pretension_t"]) * TONNE_FORCE
        self.default_bollard = line.get("bollard")
        self.status = "stowed"
        self.bollard_index = -1
        self.rest_length = 0.0
        self.target_tension = 0.0
        self.slack_remaining = 0.0
        self.last_tension = 0.0

    @property
    def loaded(self) -> bool:
        return self.status in ("fast", "heaving", "slacking")

    def static_tension(self, distance: float) -> float:
        if self.rest_length <= 0:
            return 0.0
        eps = distance / self.rest_length - 1.0
        return 0.0 if eps <= 0.0 else self.mbl * math.pow(eps / self.break_strain, self.exponent)

    def strain_for(self, tension: float) -> float:
        return 0.0 if tension <= 0 else self.break_strain * math.pow(tension / self.mbl, 1.0 / self.exponent)

    def tension(self, distance: float, rate: float) -> float:
        if not self.loaded or self.rest_length <= 0:
            return 0.0
        eps = distance / self.rest_length - 1.0
        if eps <= 0.0:
            return 0.0
        ts = self.mbl * math.pow(eps / self.break_strain, self.exponent)
        k = self.mbl * self.exponent * math.pow(eps / self.break_strain, self.exponent - 1.0) / (self.break_strain * self.rest_length)
        t = ts + k * self.DAMPING_TAU * rate
        return 0.0 if t < 0.0 else min(t, 1.5 * self.mbl)


def _resolve_xy(p: dict[str, Any], ltp: Any) -> tuple[float, float]:
    x, y = _num(p.get("x")), _num(p.get("y"))
    if x is not None and y is not None:
        return x, y
    return ltp.to_local(float(p["lat"]), float(p["lon"]))


class BerthModel:
    """泊位:碼頭面折線、碰墊、繫船柱與自船繫纜(設計文件第 4 節;C# BerthModel)。"""

    def __init__(self, definition: dict[str, Any], lines: list[dict[str, Any]], raw_particulars: dict[str, Any],
                 ship_mass_kg: float, ltp: Any) -> None:
        hull = raw_particulars["hull"]
        lpp = float(hull["lengthBetweenPerpendiculars_m"])
        breadth = float(hull["breadth_m"])
        loa = float(hull.get("lengthOverall_m") or 0.0)
        self.id = str(definition["id"])
        self.name = definition.get("name")
        pts = [_resolve_xy(p, ltp) for p in definition["quay"]]
        right = definition.get("waterSide", "right") != "left"
        segs: list[QuaySegment] = []
        chain = 0.0
        for i in range(len(pts) - 1):
            ax, ay = pts[i]
            bx, by = pts[i + 1]
            ln = math.sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay))
            if ln < 1e-6:
                continue
            tx = (bx - ax) / ln
            ty = (by - ay) / ln
            nx, ny = (ty, -tx) if right else (-ty, tx)
            segs.append(QuaySegment(ax, ay, tx, ty, nx, ny, ln, chain))
            chain += ln
        self.segments = segs
        types = {k: FenderType(v) for k, v in (definition.get("fenderTypes") or {}).items()}
        self.fenders: list[Fender] = []
        for f in definition.get("fenders") or []:
            seg, s = self._locate(float(f["chainage_m"]))
            self.fenders.append(Fender(str(f["id"]), types[f["type"]], seg, s))
        self.bollards: list[Bollard] = []
        for b in definition.get("bollards") or []:
            seg, s = self._locate(float(b["chainage_m"]))
            q = segs[seg]
            sb = float(b["setback_m"])
            self.bollards.append(Bollard(str(b["id"]), q.ax + s * q.tx - sb * q.nx, q.ay + s * q.ty - sb * q.ny, b.get("swl_t")))
        self.lines = [MooringLine(line, lpp, breadth) for line in lines]
        qs = definition.get("quayStiffness_kNpm")
        self.quay_stiffness = (20000.0 if qs is None else float(qs)) * 1000.0
        self.quay_damping = math.sqrt(self.quay_stiffness * max(1.0, ship_mass_kg))
        l2 = 0.5 * (loa if loa > 0 else lpp)
        b2 = 0.5 * breadth
        self.hull = [
            (l2, 0.0), (0.84 * l2, 0.60 * b2), (0.60 * l2, 0.96 * b2), (0.50 * l2, b2), (-0.80 * l2, b2), (-l2, 0.84 * b2),
            (-l2, -0.84 * b2), (-0.80 * l2, -b2), (0.50 * l2, -b2), (0.60 * l2, -0.96 * b2), (0.84 * l2, -0.60 * b2),
        ]
        self.quay_in_contact = False
        self.quay_max_penetration = 0.0

    def _locate(self, chainage: float) -> tuple[int, float]:
        n = len(self.segments)
        for i, q in enumerate(self.segments):
            if chainage <= q.chainage + q.length or i == n - 1:
                return i, min(max(chainage - q.chainage, 0.0), q.length)
        return 0, 0.0

    def bollard_index(self, bid: str) -> int:
        for i, b in enumerate(self.bollards):
            if b.id == bid:
                return i
        return -1

    def hull_enu(self, k: Kin) -> list[tuple[float, float]]:
        return [body_to_enu(k, x, y) for x, y in self.hull]

    def fender_geometry(self, f: Fender, hull: list[tuple[float, float]]) -> tuple[bool, float, float, float]:
        """(接觸, 壓縮 δ, 接觸點 E, N)(C# FenderGeometry)。"""
        q = self.segments[f.segment]
        lo = f.s - 0.5 * f.type.width
        hi = f.s + 0.5 * f.type.width
        best = math.inf
        n = len(hull)
        for i in range(n):
            e0, n0 = hull[i]
            e1, n1 = hull[(i + 1) % n]
            s0 = (e0 - q.ax) * q.tx + (n0 - q.ay) * q.ty
            d0 = (e0 - q.ax) * q.nx + (n0 - q.ay) * q.ny
            s1 = (e1 - q.ax) * q.tx + (n1 - q.ay) * q.ty
            d1 = (e1 - q.ax) * q.nx + (n1 - q.ay) * q.ny
            ds = s1 - s0
            if abs(ds) < 1e-12:
                if s0 < lo or s0 > hi:
                    continue
                ta, tb = 0.0, 1.0
            else:
                ta = (lo - s0) / ds
                tb = (hi - s0) / ds
                if ta > tb:
                    ta, tb = tb, ta
                ta = max(0.0, ta)
                tb = min(1.0, tb)
                if ta > tb:
                    continue
            da = d0 + ta * (d1 - d0)
            if da < best:
                best = da
            db = d0 + tb * (d1 - d0)
            if db < best:
                best = db
        if math.isinf(best):
            return False, -math.inf, 0.0, 0.0
        delta = f.type.height - best
        pe = q.ax + f.s * q.tx + best * q.nx
        pn = q.ay + f.s * q.ty + best * q.ny
        return (delta > 0.0 and best >= -2.0 * self.hull[3][1]), delta, pe, pn

    def force(self, k: Kin, env: Env) -> tuple[float, float, float]:
        hull = self.hull_enu(k)
        X = Y = N = 0.0
        for f in self.fenders:
            contact, delta, pe, pn = self.fender_geometry(f, hull)
            if not contact:
                continue
            q = self.segments[f.segment]
            xb, yb = enu_to_body(k, pe, pn)
            ve, vn = point_ground_velocity(k, env, xb, yb)
            v_normal = -(ve * q.nx + vn * q.ny)
            fn = f.type.reaction(delta) + f.type.damping * v_normal
            if fn <= 0.0:
                continue
            vt = ve * q.tx + vn * q.ty
            ft = -f.type.friction * fn * math.tanh(vt / 0.05)
            fx, fy, fz = from_enu(fn * q.nx + ft * q.tx, fn * q.ny + ft * q.ty, xb, yb, k.psi)
            X += fx
            Y += fy
            N += fz
        lim = -2.0 * self.hull[3][1]
        for q in self.segments:
            for i, (e, n) in enumerate(hull):
                sa = (e - q.ax) * q.tx + (n - q.ay) * q.ty
                if sa < 0.0 or sa > q.length:
                    continue
                d = (e - q.ax) * q.nx + (n - q.ay) * q.ny
                if d >= 0.0 or d < lim:
                    continue
                xb, yb = self.hull[i]
                ve, vn = point_ground_velocity(k, env, xb, yb)
                v_normal = -(ve * q.nx + vn * q.ny)
                fn = self.quay_stiffness * -d + self.quay_damping * v_normal
                if fn <= 0.0:
                    continue
                fx, fy, fz = from_enu(fn * q.nx, fn * q.ny, xb, yb, k.psi)
                X += fx
                Y += fy
                N += fz
        for line in self.lines:
            if not line.loaded or line.bollard_index < 0:
                continue
            b = self.bollards[line.bollard_index]
            fe, fnn = body_to_enu(k, line.fair_x, line.fair_y)
            de = b.x - fe
            dn = b.y - fnn
            dist = math.sqrt(de * de + dn * dn)
            if dist < 1e-6:
                continue
            ue, un = de / dist, dn / dist
            ve, vn = point_ground_velocity(k, env, line.fair_x, line.fair_y)
            rate = -(ve * ue + vn * un)
            t = line.tension(dist, rate)
            if t <= 0.0:
                continue
            fx, fy, fz = from_enu(t * ue, t * un, line.fair_x, line.fair_y, k.psi)
            X += fx
            Y += fy
            N += fz
        return (X, Y, N)

    def line_geometry(self, line: MooringLine, k: Kin, env: Env) -> tuple[float, float]:
        b = self.bollards[line.bollard_index]
        fe, fn = body_to_enu(k, line.fair_x, line.fair_y)
        de = b.x - fe
        dn = b.y - fn
        dist = math.sqrt(de * de + dn * dn)
        if dist < 1e-6:
            return dist, 0.0
        ve, vn = point_ground_velocity(k, env, line.fair_x, line.fair_y)
        return dist, -(ve * de + vn * dn) / dist

    def post_step(self, k: Kin, env: Env, dt: float, events: list[dict[str, Any]]) -> None:
        hull = self.hull_enu(k)
        for f in self.fenders:
            contact, delta, pe, pn = self.fender_geometry(f, hull)
            q = self.segments[f.segment]
            v_normal = 0.0
            if contact:
                xb, yb = enu_to_body(k, pe, pn)
                ve, vn = point_ground_velocity(k, env, xb, yb)
                v_normal = -(ve * q.nx + vn * q.ny)
            f.last_deflection = delta if contact else 0.0
            f.last_reaction = f.type.reaction(delta) if contact else 0.0
            if contact and not f.in_contact:
                f.in_contact = True
                f.peak_reported = False
                f.max_deflection = delta
                f.approach_speed = v_normal
            elif contact:
                if delta > f.max_deflection:
                    f.max_deflection = delta
                if not f.peak_reported and v_normal <= 0.0:
                    self._report_fender(f, events)
            elif f.in_contact:
                if not f.peak_reported:
                    self._report_fender(f, events)
                f.in_contact = False
                f.max_deflection = 0.0
                f.approach_speed = 0.0

        max_pen = 0.0
        v_at = 0.0
        lim = -2.0 * self.hull[3][1]
        for q in self.segments:
            for i, (e, n) in enumerate(hull):
                sa = (e - q.ax) * q.tx + (n - q.ay) * q.ty
                if sa < 0.0 or sa > q.length:
                    continue
                d = (e - q.ax) * q.nx + (n - q.ay) * q.ny
                if d >= 0.0 or d < lim:
                    continue
                if -d > max_pen:
                    max_pen = -d
                    ve, vn = point_ground_velocity(k, env, self.hull[i][0], self.hull[i][1])
                    v_at = -(ve * q.nx + vn * q.ny)
        if max_pen > 0.0 and not self.quay_in_contact:
            self.quay_in_contact = True
            self.quay_max_penetration = max_pen
            events.append(_event("quayContact", self.id, "船體觸碰碼頭(硬接觸)",
                                 {"berthId": self.id, "penetration_m": max_pen, "normalSpeed_mps": v_at}))
        elif max_pen > 0.0:
            self.quay_max_penetration = max(self.quay_max_penetration, max_pen)
        elif self.quay_in_contact:
            self.quay_in_contact = False
            self.quay_max_penetration = 0.0

        for line in self.lines:
            if not line.loaded or line.bollard_index < 0:
                line.last_tension = 0.0
                continue
            dist, rate = self.line_geometry(line, k, env)
            t = line.tension(dist, rate)
            line.last_tension = t
            if t > line.mbl:
                line.status = "parted"
                line.last_tension = 0.0
                events.append(_event("mooringLineParted", line.id, f"繫纜 {line.id} 斷裂",
                                     {"lineId": line.id, "tension_t": t / TONNE_FORCE, "mbl_t": line.mbl / TONNE_FORCE,
                                      "bollardId": self.bollards[line.bollard_index].id}))
                continue
            if line.status == "heaving":
                if line.static_tension(dist) >= line.target_tension:
                    line.status = "fast"
                else:
                    line.rest_length = max(1.0, line.rest_length - line.heave_rate * dt)
            elif line.status == "slacking":
                dl = min(line.slack_remaining, line.heave_rate * dt)
                line.rest_length = min(line.max_length, line.rest_length + dl)
                line.slack_remaining -= dl
                if line.slack_remaining <= 1e-9 or line.rest_length >= line.max_length:
                    line.slack_remaining = 0.0
                    line.status = "fast"

    @staticmethod
    def _report_fender(f: Fender, events: list[dict[str, Any]]) -> None:
        f.peak_reported = True
        energy = f.type.energy(f.max_deflection)
        max_r = f.type.max_reaction(f.max_deflection)
        overload = f.max_deflection > f.type.rated_deflection + 1e-9 or energy > f.type.rated_energy + 1e-6
        events.append(_event("fenderContact", f.id, f"碰墊 {f.id} 過載" if overload else f"碰墊 {f.id} 接觸", {
            "fenderId": f.id, "normalSpeed_mps": f.approach_speed, "energy_kNm": energy / 1000.0, "maxReaction_kN": max_r / 1000.0,
            "maxDeflection_m": f.max_deflection, "deflection_pct": 100.0 * f.max_deflection / f.type.height,
            "ratedEnergy_kNm": f.type.rated_energy / 1000.0, "ratedReaction_kN": f.type.rated_reaction / 1000.0, "overload": overload,
        }))

    def command(self, line: MooringLine, action: str, bollard_id: str | None, tension_t: float | None, length_m: float | None,
                k: Kin, env: Env) -> str | None:
        """繫纜指令(makeFast|heave|slack|letGo);None = 接受,否則拒絕原因(C# BerthModel.Command)。"""
        if action == "makeFast":
            if line.status == "parted":
                return "mooringLineParted"
            bid = bollard_id if bollard_id is not None else line.default_bollard
            bi = -1 if bid is None else self.bollard_index(bid)
            if bi < 0:
                return "unknownBollard"
            line.bollard_index = bi
            dist, _ = self.line_geometry(line, k, env)
            if dist > line.max_length:
                line.bollard_index = -1
                return "bollardOutOfReach"
            pre = tension_t * TONNE_FORCE if tension_t is not None else line.pretension
            line.rest_length = dist / (1.0 + line.strain_for(pre))
            line.status = "fast"
            line.slack_remaining = 0.0
            return None
        if action == "heave":
            if not line.loaded:
                return "mooringLineNotFast"
            line.target_tension = min(line.winch_pull, tension_t * TONNE_FORCE if tension_t is not None else line.winch_pull)
            line.status = "heaving"
            return None
        if action == "slack":
            if not line.loaded:
                return "mooringLineNotFast"
            line.slack_remaining = max(0.1, 2.0 if length_m is None else length_m)
            line.status = "slacking"
            return None
        if action == "letGo":
            if line.status == "parted":
                return "mooringLineParted"
            line.status = "letGo"
            line.last_tension = 0.0
            return None
        return "unknownAction"


# ============================================================================ 外力模組(ExternalForceModel.cs)


class ExternalForceModel:
    """拖船、兩錨、泊位的合力與跨步狀態(C# ExternalForceModel)。``active`` 為 False 時不影響積分與雜湊。"""

    def __init__(self, raw_particulars: dict[str, Any], loading: str, sections: dict[str, Any], ltp: Any, dt: float,
                 initial: Kin, water_depth: float, supported: bool = True) -> None:
        self.supported = supported
        self.ltp = ltp
        hull = raw_particulars["hull"]
        self._lpp = float(hull["lengthBetweenPerpendiculars_m"])
        self._breadth = float(hull["breadth_m"])
        lc = (raw_particulars.get("loadingConditions") or {}).get(loading) or {}
        disp = lc.get("displacement_t")
        dm = lc.get("draftMean_m") if lc.get("draftMean_m") is not None else lc.get("maxDraft_m")
        if disp is not None and float(disp) > 0:
            self.ship_mass_kg = float(disp) * 1000.0
        else:
            cb = lc.get("blockCoefficient")
            self.ship_mass_kg = 1025.0 * self._lpp * self._breadth * float(dm or 0.0) * (0.75 if cb is None else float(cb))
        self.seabed = sections.get("seabed") if sections.get("seabed") in ("mud", "clay", "rock") else "sand"
        anchors_cfg = sections.get("anchors") or {}
        hf = anchors_cfg.get("holdingFactor")
        ds = anchors_cfg.get("dragSpeed_mps")
        self.holding_factor = 1.0 if hf is None else float(hf)
        self.drag_speed = 0.5 if ds is None else float(ds)
        self.tugs = [TugUnit(t, self._lpp, self._breadth, dt) for t in sections.get("tugs") or []]
        self.anchor_spec = AnchorParticulars.from_raw(raw_particulars)
        self.anchors: list[AnchorUnit] = []
        self.anchors_used = False
        if self.anchor_spec is not None:
            spec = self.anchor_spec
            draft_fore = lc.get("draftFore_m")
            if draft_fore is None:
                draft_fore = dm
            self.anchors = [AnchorUnit("port", spec, float(draft_fore)), AnchorUnit("starboard", spec, float(draft_fore))]
            for side in ("port", "starboard"):
                init = anchors_cfg.get(side)
                if not isinstance(init, dict):
                    continue
                length = init.get("length_m")
                if length is None and init.get("length_shackles") is not None:
                    length = float(init["length_shackles"]) * spec.shackle_length_m
                if length is None or not float(length) > 0:
                    continue
                bd = init.get("bearing_deg")
                bearing = (float(bd) if bd is not None else initial.psi * RAD2DEG) * DEG2RAD
                self.anchor(side).initialise_anchored(initial, float(length), bearing, water_depth, self.seabed, self.holding_factor)
                self.anchors_used = True
        self.berth: BerthModel | None = None
        b = sections.get("berth")
        if isinstance(b, dict) and isinstance(b.get("definition"), dict):
            self.berth = BerthModel(b["definition"], b.get("lines") or [], raw_particulars, self.ship_mass_kg, ltp)
            env0 = Env(0.0, 0.0, water_depth)
            for line, cfg in zip(self.berth.lines, b.get("lines") or []):
                if cfg.get("initial") == "fast":
                    self.berth.command(line, "makeFast", None, None, None, initial, env0)

    @property
    def active(self) -> bool:
        return self.supported and (len(self.tugs) > 0 or self.berth is not None or self.anchors_used)

    def tug(self, tid: str | None) -> TugUnit | None:
        if tid is None:
            return None
        for t in self.tugs:
            if t.id.lower() == str(tid).lower():
                return t
        return None

    def anchor(self, side: str | None) -> AnchorUnit | None:
        if side is None:
            return None
        s = str(side).lower()
        want = "port" if s in ("port", "p") else ("starboard" if s in ("starboard", "stbd", "s") else None)
        for a in self.anchors:
            if a.side == want:
                return a
        return None

    # ---- 力 ----
    def evaluate(self, k: Kin, env: Env) -> tuple[float, float, float]:
        X = Y = N = 0.0
        for t in self.tugs:
            f = t.force()
            X += f[0]
            Y += f[1]
            N += f[2]
        for a in self.anchors:
            f = a.force(k, env.depth)
            X += f[0]
            Y += f[1]
            N += f[2]
        if self.berth is not None:
            f = self.berth.force(k, env)
            X += f[0]
            Y += f[1]
            N += f[2]
        return (X, Y, N)

    def pre_step(self, k: Kin, dt: float) -> None:
        speed = math.sqrt(k.u * k.u + k.v * k.v)
        for t in self.tugs:
            t.pre_step(dt, speed)

    def post_step(self, k: Kin, env: Env, dt: float, events: list[dict[str, Any]]) -> None:
        if self.anchors and self.anchors_used:
            s = math.sin(k.psi)
            c = math.cos(k.psi)
            ve = k.u * s + k.v * c + env.current_e
            vn = k.u * c - k.v * s + env.current_n
            sog = math.sqrt(ve * ve + vn * vn)
            for a in self.anchors:
                a.post_step(k, env.depth, dt, self.seabed, self.holding_factor, self.drag_speed, sog, events)
        if self.berth is not None:
            self.berth.post_step(k, env, dt, events)

    # ---- 指令(回傳 (拒絕原因, 事件));拒絕原因與 C# 相同 ----
    def tug_control(self, tid: str | None, mode: str | None, direction_deg: float | None, power_pct: float | None) -> tuple[str | None, dict | None]:
        if not self.supported:
            return "externalForcesUnsupported", None
        tug = self.tug(tid)
        if tug is None:
            return "unknownTug", None
        if tug.status == "unavailable":  # 已離開的拖船:任何指令皆拒絕(先於模式檢查,同 C#)
            return "tugUnavailable", None
        m = tug.mode if mode is None else str(mode).lower()
        if m not in TUG_MODES:
            return "unknownTugMode", None
        if power_pct is not None and (power_pct < 0 or power_pct > 100):
            return "invalidPower", None
        frm = tug.mode
        frm_status = tug.status
        force_before = tug.force_n
        reject = tug.command(m, direction_deg, power_pct)
        if reject is not None:
            return reject, None
        if m == "unavailable":  # 拖船離開:tugUnavailable 而非 tugCommand(C# ExternalForceModel.TugControl)
            return None, _event("tugUnavailable", tug.id, f"拖船 {tug.id} 離開(不再可用)", {
                "id": tug.id, "mode": m, "from": frm, "fromStatus": frm_status, "status": tug.status, "force_t": force_before / TONNE_FORCE})
        dir_deg = normalize_heading_deg(tug.direction_order * RAD2DEG)
        return None, _event("tugCommand", tug.id, f"{tug.id} {m} {dir_deg:.0f}° {tug.power_order * 100:.0f}%", {
            "id": tug.id, "mode": m, "from": frm, "status": tug.status, "direction_deg": dir_deg, "power_pct": tug.power_order * 100.0})

    def part_tug_line(self, tid: str | None) -> dict | None:
        """tugLineBreak 故障:斷 ``tid``(省略 = 第一艘拉中/待命中的拖船);回傳 tugLineParted 事件(無可斷者 None)。"""
        if not self.supported:
            return None
        if tid is not None:
            tug = self.tug(tid)
        else:
            tug = next((t for t in self.tugs if not t.line_parted and t.mode == "pull" and t.status != "free"), None)
            if tug is None:
                tug = next((t for t in self.tugs if not t.line_parted and t.status not in ("free", "parted", "unavailable")), None)
        if tug is None or tug.line_parted or tug.status == "unavailable":
            return None
        mode = tug.mode
        before = tug.part_line()
        return _event("tugLineParted", tug.id, f"拖船 {tug.id} 拖纜斷裂", {
            "id": tug.id, "force_t": before / TONNE_FORCE, "mode": mode, "reason": "fault", "stillPushing": tug.mode == "push"})

    def restore_tug_lines(self) -> None:
        for t in self.tugs:
            t.line_restored()

    def anchor_command(self, side: str | None, action: str | None, target_shackles: float | None, length_m: float | None) -> tuple[str | None, dict | None]:
        if not self.supported:
            return "externalForcesUnsupported", None
        if self.anchor_spec is None:
            return "noAnchorData", None
        a = self.anchor(side)
        if a is None:
            return "unknownAnchor", None
        if action is None:
            return "unknownAction", None
        target = length_m if length_m is not None else (target_shackles * self.anchor_spec.shackle_length_m if target_shackles is not None else None)
        if target is not None and target < 0:
            return "invalidTarget", None
        reject = a.command(action, target)
        if reject is not None:
            return reject, None
        self.anchors_used = True
        data = {"side": a.side, "action": action, "targetLength_m": a.target_length,
                "targetLength_shackles": a.target_length / self.anchor_spec.shackle_length_m if a.target_length is not None else None,
                "length_m": a.length, "status": a.status}
        if action == "letGo":
            return None, _event("anchorLetGo", a.side, f"{a.side} 拋錨", data)
        return None, _event("anchorCommand", a.side, f"{a.side} {action}", data)

    def mooring_command(self, line_id: str | None, action: str | None, bollard_id: str | None, tension_t: float | None,
                        length_m: float | None, k: Kin, env: Env) -> tuple[str | None, dict | None]:
        if not self.supported:
            return "externalForcesUnsupported", None
        if self.berth is None:
            return "noBerth", None
        line = next((x for x in self.berth.lines if x.id == line_id), None)
        if line is None:
            return "unknownMooringLine", None
        if action is None:
            return "unknownAction", None
        reject = self.berth.command(line, action, bollard_id, tension_t, length_m, k, env)
        if reject is not None:
            return reject, None
        return None, _event("mooringCommand", line.id, f"{line.id} {action}", {
            "lineId": line.id, "action": action, "status": line.status,
            "bollardId": self.berth.bollards[line.bollard_index].id if line.bollard_index >= 0 else None,
            "tension_t": tension_t, "length_m": length_m, "restLength_m": line.rest_length})

    # ---- 雜湊(與 C# WriteHash 相同的欄位與順序;只在 active 時追加) ----
    def hash_bytes(self) -> bytes:
        out = [struct.pack("<qq", EXTERNAL_HASH_MARKER, 1 if self.anchors_used else 0)]
        for t in self.tugs:
            out.append(struct.pack("<qqddddqqq", TUG_MODES.index(t.mode), TUG_STATUSES.index(t.status), t.force_n, t.direction,
                                   t.direction_order, t.power_order, t.timer_ticks, 1 if t.line_parted else 0, TUG_MODES.index(t.last_working_mode)))
        for a in self.anchors:
            flags = (1 if a.on_bottom else 0) | (2 if a.dragging else 0) | (4 if a.brought_up_pending else 0) | (8 if a.fully_out else 0) \
                | (16 if a.slipping else 0) | (32 if a.stalled else 0)
            out.append(struct.pack("<qddqdd", ANCHOR_MODES.index(a.mode), a.length, -1.0 if a.target_length is None else a.target_length,
                                   flags, a.anchor_x, a.anchor_y))
            # 停轉/滑出遲滯計時只在期間內追加(C# WriteHash;未曾超載的錨雜湊與舊版相同)
            if a.stalled or a.slipping:
                out.append(struct.pack("<dd", a.stall_quiet_s, a.slip_quiet_s))
        if self.berth is not None:
            b = self.berth
            out.append(struct.pack("<qd", 1 if b.quay_in_contact else 0, b.quay_max_penetration))
            for f in b.fenders:
                out.append(struct.pack("<qdd", (1 if f.in_contact else 0) | (2 if f.peak_reported else 0), f.max_deflection, f.approach_speed))
            for line in b.lines:
                out.append(struct.pack("<qqddd", MOORING_STATUSES.index(line.status), line.bollard_index, line.rest_length,
                                       line.target_tension, line.slack_remaining))
        return b"".join(out)

    # ---- 狀態廣播(state.schema.json tugs[] / anchors[] / berth;C# TugStates / AnchorStates / BerthState) ----
    def tug_states(self, k: Kin) -> list[dict[str, Any]] | None:
        if not self.tugs:
            return None
        out = []
        for t in self.tugs:
            ae, an = body_to_enu(k, t.attach_x, t.attach_y)
            dir_true = k.psi + t.direction
            off = t.line_length_m if t.mode == "pull" else (-(0.5 * self._breadth + 15.0) if t.mode == "push" else 30.0)
            pe, pn = ae + off * math.sin(dir_true), an + off * math.cos(dir_true)
            lat, lon = self.ltp.to_geodetic(pe, pn)
            d: dict[str, Any] = {"id": t.id}
            if t.name is not None:
                d["name"] = t.name
            d.update({"mode": t.mode, "status": t.status, "force_t": t.force_n / TONNE_FORCE,
                      "direction_deg": normalize_heading_deg(t.direction * RAD2DEG),
                      "directionOrder_deg": normalize_heading_deg(t.direction_order * RAD2DEG), "power_pct": t.power_order * 100.0,
                      "efficiency": t.efficiency, "bollardPull_t": t.bollard_pull_n / TONNE_FORCE, "lineParted": t.line_parted,
                      "pos": {"lat": lat, "lon": lon, "x": pe, "y": pn}})
            out.append(d)
        return out

    def anchor_states(self) -> list[dict[str, Any]] | None:
        if not self.anchors or self.anchor_spec is None:
            return None
        spec = self.anchor_spec
        out = []
        for a in self.anchors:
            d: dict[str, Any] = {
                "side": a.side, "status": a.status, "length_m": a.length, "length_shackles": a.length / spec.shackle_length_m,
                "maxLength_m": a.max_length, "tension_t": 0.0 if a.mode == "stowed" else to_t(a.last.top_tension),
                "horizontalTension_t": to_t(a.last.H), "onBottom_m": a.last.on_bottom, "holding_t": to_t(a.last_holding),
                "brake": a.mode in ("braked", "stowed"), "fullyOut": a.fully_out,
            }
            if a.mode != "stowed":
                lat, lon = self.ltp.to_geodetic(a.anchor_x, a.anchor_y)
                d["position"] = {"lat": lat, "lon": lon, "x": a.anchor_x, "y": a.anchor_y}
            out.append(d)
        return out

    def berth_state(self) -> dict[str, Any] | None:
        b = self.berth
        if b is None:
            return None
        d: dict[str, Any] = {"id": b.id}
        if b.name is not None:
            d["name"] = b.name
        d["contacts"] = [{"fenderId": f.id, "deflection_m": f.last_deflection, "deflection_pct": 100.0 * f.last_deflection / f.type.height,
                          "reaction_kN": f.last_reaction / 1000.0, "overload": f.last_deflection > f.type.rated_deflection}
                         for f in b.fenders if f.last_deflection > 0.0]
        d["quayContact"] = b.quay_in_contact
        lines = []
        for line in b.lines:
            ld: dict[str, Any] = {"id": line.id, "status": line.status, "tension_t": line.last_tension / TONNE_FORCE, "mbl_t": line.mbl / TONNE_FORCE}
            if line.bollard_index >= 0:
                ld["bollardId"] = b.bollards[line.bollard_index].id
            if line.loaded:
                ld["length_m"] = line.rest_length
            lines.append(ld)
        d["lines"] = lines
        return d


# ============================================================================ 岸壁效應(BankEffect.cs)

BANK_DEFAULTS = {
    "CYLead": -0.005, "CYTrail": 0.045, "LeverLead": 0.30, "LeverTrail": -0.20, "CYP": 0.004, "LeverP": -0.40,
    "AY": 0.40, "AN": 0.30, "MinDepthRatio": 1.05, "InfluenceWidthB": 5.0, "MinClearanceB": 0.1, "WindowL": 1.0, "Rho": 1025.0,
}
_GAUSS_X = (-0.7745966692414834, 0.0, 0.7745966692414834)
_GAUSS_W = (0.5555555555555556, 0.8888888888888888, 0.5555555555555556)


class BankLine:
    """解析到本地 ENU 的岸壁折線(C# BankLine)。"""

    def __init__(self, d: dict[str, Any], ltp: Any) -> None:
        self.id = str(d["id"])
        self.name = d.get("name")
        self.sloped = str(d.get("type", "vertical")).lower() == "sloped"
        slope = d.get("slope")
        self.slope = (float(slope) if slope is not None else 0.0) if self.sloped else 0.0
        self.top_depth = float(d.get("topDepth_m") or 0.0)
        cd = d.get("channelDepth_m")
        self.channel_depth = None if cd is None else float(cd)
        pts = [_resolve_xy(p, ltp) for p in d.get("line") or []]
        self.e = [p[0] for p in pts]
        self.n = [p[1] for p in pts]
        segs = max(0, len(pts) - 1)
        self.land_e = [0.0] * segs
        self.land_n = [0.0] * segs
        water_right = str(d.get("waterSide", "right")).lower() != "left"
        for i in range(segs):
            te = self.e[i + 1] - self.e[i]
            tn = self.n[i + 1] - self.n[i]
            ln = math.sqrt(te * te + tn * tn)
            if ln < 1e-9:
                continue
            te /= ln
            tn /= ln
            self.land_e[i], self.land_n[i] = (-tn, te) if water_right else (tn, -te)

    def equivalent(self, local_depth: float) -> tuple[float, float]:
        """等效垂直岸壁的向陸偏移(m)與岸型強度倍率 φ。"""
        h = self.channel_depth if self.channel_depth is not None else local_depth
        if not (h > 0.0) or math.isinf(h):
            return 0.0, 1.0
        span = max(0.0, h - self.top_depth)
        phi = min(max(span / h, 0.0), 1.0)
        offset = self.slope * 0.5 * span if self.sloped else 0.0
        return offset, phi


class BankResult(NamedTuple):
    Y: float
    N: float
    D: float
    d_fore: float
    d_aft: float
    depth_ratio: float
    influence_m: float
    port_m: float | None
    starboard_m: float | None
    port_id: str | None
    starboard_id: str | None


def _segment_distance(xa: float, ya: float, xb: float, yb: float) -> float:
    dx = xb - xa
    dy = yb - ya
    l2 = dx * dx + dy * dy
    t = min(max(-(xa * dx + ya * dy) / l2, 0.0), 1.0) if l2 > 0.0 else 0.0
    px = xa + t * dx
    py = ya + t * dy
    return math.sqrt(px * px + py * py)


def _seg_at(seg: tuple, x: float) -> float:
    sigma, xa, ya, kk, offset, half_b, y_min, inv_inf, win = seg
    ys = sigma * (ya + kk * (x - xa)) + offset
    if ys <= -half_b:
        return 0.0
    q = 1.0 / max(ys, y_min) - inv_inf
    if not (q > 0.0):
        return 0.0
    if ys < half_b:
        q *= (ys + half_b) / (2.0 * half_b)
    return (1.0 - abs(x) / win) * q


def _integrate(seg: tuple, a: float, b: float, max_piece: float) -> float:
    if not (b > a):
        return 0.0
    m = max(1, int(math.ceil((b - a) / max_piece)))
    h = (b - a) / m
    total = 0.0
    for kk in range(m):
        mid = a + (kk + 0.5) * h
        for g in range(3):
            total += _GAUSS_W[g] * _seg_at(seg, mid + 0.5 * h * _GAUSS_X[g])
    return total * 0.5 * h


class BankEffectModel:
    """岸壁效應(設計文件 physics-bank-effect.md;C# BankEffectModel 逐式對應)。``active`` 為 False 時不影響積分與雜湊。"""

    HASH_LONGS = 9

    def __init__(self, raw_particulars: dict[str, Any], loading: str, banks: dict[str, Any], ltp: Any, supported: bool = True,
                 coefficients: dict[str, float] | None = None) -> None:
        self.supported = supported
        self.c = dict(BANK_DEFAULTS, **(coefficients or {}))
        sc = banks.get("scale")
        self.scale = 1.0 if sc is None else float(sc)
        hull = raw_particulars["hull"]
        lc = (raw_particulars.get("loadingConditions") or {}).get(loading) or {}
        self.L = float(hull["lengthBetweenPerpendiculars_m"])
        self.B = float(hull["breadth_m"])
        self.T = float(lc["draftMean_m"] if lc.get("draftMean_m") is not None else lc["maxDraft_m"])
        self.prop_d = float((raw_particulars.get("propeller") or {}).get("diameter_m") or 0.0)
        defs = list(banks.get("lines") or []) + list(banks.get("definitions") or [])
        self.lines = [ln for ln in (BankLine(d, ltp) for d in defs) if len(ln.e) >= 2]
        self._rpm = 0.0
        self.last = BankResult(0.0, 0.0, 0.0, 0.0, 0.0, math.inf, 0.0, None, None, None, None)

    @property
    def active(self) -> bool:
        return self.supported and len(self.lines) > 0

    def pre_step(self, rpm: float) -> None:
        self._rpm = rpm

    def update_last(self, k: Kin, depth: float, rpm: float) -> None:
        self.last = self.compute(k, depth, rpm, detail=True)

    def evaluate(self, k: Kin, env: Env) -> tuple[float, float, float]:
        r = self.compute(k, env.depth, self._rpm, detail=False)
        return (0.0, r.Y, r.N)

    def compute(self, k: Kin, depth: float, rpm: float, detail: bool) -> BankResult:
        c = self.c
        L, B, T = self.L, self.B, self.T
        deep = not (depth > 0.0) or math.isinf(depth)
        depth_ratio = math.inf if deep else depth / T
        hr = max(depth_ratio, c["MinDepthRatio"])
        tau = 0.0 if deep else 1.0 / (hr - 1.0)
        speed = math.sqrt(k.u * k.u + k.v * k.v)
        fnh = 0.0 if deep else min(0.95, speed / math.sqrt(GRAVITY * depth))
        fnh2 = fnh * fnh
        fnh8 = fnh2 * fnh2 * fnh2 * fnh2
        y_inf = c["InfluenceWidthB"] * B * (1.0 + 16.0 * fnh8)
        y_min = (0.5 + c["MinClearanceB"]) * B
        inv_inf = 1.0 / y_inf
        win = c["WindowL"] * L
        reach = win + y_inf
        sin_p = math.sin(k.psi)
        cos_p = math.cos(k.psi)
        i_fore = 0.0
        i_aft = 0.0
        port_d = stbd_d = None
        port_id = stbd_id = None
        for line in self.lines:
            offset, phi = line.equivalent(depth)
            if phi <= 0.0:
                continue
            for i in range(len(line.e) - 1):
                le = line.land_e[i]
                ln = line.land_n[i]
                if le == 0.0 and ln == 0.0:
                    continue
                dea = line.e[i] - k.x
                dna = line.n[i] - k.y
                deb = line.e[i + 1] - k.x
                dnb = line.n[i + 1] - k.y
                xa = dea * sin_p + dna * cos_p
                ya = dea * cos_p - dna * sin_p
                xb = deb * sin_p + dnb * cos_p
                yb = deb * cos_p - dnb * sin_p
                if _segment_distance(xa, ya, xb, yb) > reach:
                    continue
                if abs(xb - xa) < 1e-9:
                    continue
                ny = le * cos_p - ln * sin_p
                if abs(ny) < 1e-12:
                    continue
                sigma = 1.0 if ny > 0.0 else -1.0
                x0 = min(xa, xb)
                x1 = max(xa, xb)
                slope_y = (yb - ya) / (xb - xa)
                if detail and x0 <= 0.0 <= x1:
                    ys0 = sigma * (ya + slope_y * (0.0 - xa))
                    if ys0 + offset > -0.5 * B:
                        y_eq = max(0.0, ys0 + offset)
                        if y_eq <= y_inf:
                            if sigma > 0 and (stbd_d is None or y_eq < stbd_d):
                                stbd_d, stbd_id = y_eq, line.id
                            if sigma < 0 and (port_d is None or y_eq < port_d):
                                port_d, port_id = y_eq, line.id
                lo = max(x0, -win)
                hi = min(x1, win)
                if not (hi > lo):
                    continue
                seg = (sigma, xa, ya, slope_y, offset, 0.5 * B, y_min, inv_inf, win)
                if hi > 0.0:
                    i_fore += sigma * phi * _integrate(seg, max(lo, 0.0), hi, 0.1 * win)
                if lo < 0.0:
                    i_aft += sigma * phi * _integrate(seg, lo, min(hi, 0.0), 0.1 * win)
        d_fore = 0.5 * B * (2.0 / win) * i_fore
        d_aft = 0.5 * B * (2.0 / win) * i_aft
        d = 0.5 * (d_fore + d_aft)
        f_y = 1.0 + c["AY"] * tau
        f_n = 1.0 + c["AN"] * tau
        q_lt = 0.5 * c["Rho"] * L * T
        u = k.u
        d_lead, d_trail, direction = (d_fore, d_aft, 1.0) if u >= 0.0 else (d_aft, d_fore, -1.0)
        u2 = u * u
        nd = rpm / 60.0 * self.prop_d if rpm > 0.0 and self.prop_d > 0.0 else 0.0
        nd2 = nd * nd
        y = self.scale * f_y * q_lt * (u2 * (c["CYLead"] * d_lead + c["CYTrail"] * d_trail) + nd2 * c["CYP"] * d_aft)
        n = self.scale * f_n * q_lt * L * (direction * u2 * (c["LeverLead"] * c["CYLead"] * d_lead + c["LeverTrail"] * c["CYTrail"] * d_trail)
                                           + nd2 * c["LeverP"] * c["CYP"] * d_aft)
        return BankResult(y, n, d, d_fore, d_aft, depth_ratio, y_inf, port_d, stbd_d, port_id, stbd_id)

    def hash_bytes(self) -> bytes:
        r = self.last
        return struct.pack("<qqddddddd", BANK_HASH_MARKER, len(self.lines), self._rpm, r.D, r.d_fore, r.d_aft, r.Y, r.N, r.influence_m)

    def state(self) -> dict[str, Any]:
        """state.bank(kN、kN·m、m;C# BankEffectModel.State)。"""
        r = self.last
        nearest = side = bank_id = None
        if r.starboard_m is not None:
            nearest, side, bank_id = r.starboard_m, "starboard", r.starboard_id
        if r.port_m is not None and (nearest is None or r.port_m < nearest):
            nearest, side, bank_id = r.port_m, "port", r.port_id
        out: dict[str, Any] = {"Y_kN": r.Y / 1000.0, "N_kNm": r.N / 1000.0,
                               "side": "none" if abs(r.Y) < 1e-6 else ("starboard" if r.Y > 0 else "port"), "parameter": r.D}
        opt = {"distance_m": nearest, "clearance_m": None if nearest is None else nearest - 0.5 * self.B, "nearestSide": side,
               "bankId": bank_id, "port_m": r.port_m, "starboard_m": r.starboard_m,
               "depthRatio": None if math.isinf(r.depth_ratio) else r.depth_ratio, "influence_m": r.influence_m}
        out.update({k: v for k, v in opt.items() if v is not None})
        return out


# ============================================================================ 情境區段(C# ScenarioExternalForces / ScenarioBanks)

TUG_DEFAULTS: dict[str, Any] = {
    "bollardPull_t": 40.0, "lineLength_m": 40.0, "riseTime_s": 30.0, "turnRate_degPerSec": None, "repositionSpeed_kn": 5.0,
    "pushFullUpTo_kn": 3.0, "pushHalfAt_kn": 5.0, "pullFullUpTo_kn": 4.0, "pullHalfAt_kn": 7.0, "modeChange_s": 60.0, "connect_s": 90.0,
}
FENDER_TYPE_DEFAULTS: dict[str, Any] = {
    "height_m": 1.0, "width_m": 2.0, "ratedReaction_kN": 1000.0, "ratedDeflection_pct": 72.0, "ratedEnergy_kNm": None,
    "damping_kNs_per_m": 0.0, "friction": 0.2,
}
MOORING_LINE_DEFAULTS: dict[str, Any] = {
    "mbl_t": 50.0, "breakStrain": 0.18, "exponent": 1.5, "length_m": 150.0, "heaveRate_mPerMin": 15.0, "winchPull_t": 10.0, "pretension_t": 2.0,
}


def _fill(d: dict[str, Any], defaults: dict[str, Any]) -> dict[str, Any]:
    out = dict(d)
    for k, v in defaults.items():
        cur = _num(out.get(k)) if out.get(k) is not None else None
        out[k] = cur if cur is not None else v
    return out


def _find_file(ref: str, base_directory: str | os.PathLike[str] | None) -> Path:
    from . import paths

    cands: list[Path] = []
    p = Path(ref)
    if p.is_absolute():
        cands.append(p)
    else:
        try:
            cands.append(paths.repo_root() / ref)
        except Exception:  # noqa: BLE001 - 找不到專案根目錄時略過
            pass
        if base_directory is not None:
            cands.append(Path(base_directory) / ref)
        cands.append(p)
    for c in cands:
        if c.is_file():
            return c
    raise FileNotFoundError(ref)


def load_berth(path: str | os.PathLike[str], berth_id: str | None) -> dict[str, Any]:
    """泊位檔中指定 id(省略 = 第一個)的泊位定義;檔案頂層 fenderTypes 合併(泊位優先;C# ScenarioExternalForces.LoadBerth)。"""
    root = json.loads(Path(path).read_text(encoding="utf-8"))
    berths = root.get("berths") if isinstance(root, dict) else None
    if not berths:
        raise ValueError(f"泊位檔沒有 berths[]:{path}")
    if berth_id is None:
        node = berths[0]
    else:
        node = next((b for b in berths if b.get("id") == berth_id), None)
        if node is None:
            raise ValueError(f"泊位檔 {Path(path).name} 沒有泊位 {berth_id}")
    d = json.loads(json.dumps(node))
    types = dict(d.get("fenderTypes") or {})
    for k, v in (root.get("fenderTypes") or {}).items():
        types.setdefault(k, v)
    d["fenderTypes"] = types
    if d.get("source") is None and isinstance(root.get("source"), str):
        d["source"] = root["source"]
    return d


def load_banks(path: str | os.PathLike[str], ids: list[str] | None) -> list[dict[str, Any]]:
    """岸壁檔中指定 id(省略 = 全部)的岸壁(C# ScenarioBankLoader.LoadBanks)。"""
    root = json.loads(Path(path).read_text(encoding="utf-8"))
    banks = root.get("banks") if isinstance(root, dict) else None
    if not banks:
        raise ValueError(f"岸壁檔沒有 banks[]:{path}")
    src = root.get("source") if isinstance(root.get("source"), str) else None
    allb = [dict(b, source=b.get("source") or src) for b in banks]
    if not ids:
        return allb
    picked = []
    for i in ids:
        b = next((x for x in allb if x.get("id") == i), None)
        if b is None:
            raise ValueError(f"岸壁檔 {Path(path).name} 沒有岸壁 {i}")
        picked.append(b)
    return picked


def normalise_sections(raw: dict[str, Any], base_directory: str | os.PathLike[str] | None = None) -> dict[str, Any]:
    """情境的 tugs / anchors / berth / banks / environment.seabed → 補 C# 預設值的正規化 dict(berth.file、banks.file 於此解析並內嵌)。"""
    out: dict[str, Any] = {"tugs": None, "anchors": None, "berth": None, "banks": None, "seabed": None}
    env = raw.get("environment") if isinstance(raw.get("environment"), dict) else {}
    if env.get("seabed") is not None:
        out["seabed"] = str(env["seabed"])
    tugs = raw.get("tugs")
    if isinstance(tugs, list):
        norm = []
        for t in tugs:
            if not isinstance(t, dict):
                continue
            nt = _fill(t, TUG_DEFAULTS)
            nt["id"] = "" if t.get("id") is None else str(t["id"])
            init = t.get("initial")
            if isinstance(init, dict):
                nt["initial"] = {"mode": init.get("mode"), "direction_deg": _num(init.get("direction_deg")), "power_pct": _num(init.get("power_pct"))}
            norm.append(nt)
        out["tugs"] = norm
    an = raw.get("anchors")
    if isinstance(an, dict):
        na: dict[str, Any] = {"holdingFactor": _num(an.get("holdingFactor")), "dragSpeed_mps": _num(an.get("dragSpeed_mps"))}
        if na["holdingFactor"] is None:
            na["holdingFactor"] = 1.0
        if na["dragSpeed_mps"] is None:
            na["dragSpeed_mps"] = 0.5
        for side in ("port", "starboard"):
            s = an.get(side)
            na[side] = {k: _num(s.get(k)) for k in ("length_m", "length_shackles", "bearing_deg")} if isinstance(s, dict) else None
        out["anchors"] = na
    b = raw.get("berth")
    if isinstance(b, dict):
        nb: dict[str, Any] = {"file": b.get("file"), "id": b.get("id"), "departedDistance_m": _num(b.get("departedDistance_m"))}
        definition = b.get("definition")
        if not isinstance(definition, dict) and isinstance(b.get("file"), str) and b["file"].strip():
            try:
                path = _find_file(b["file"], base_directory)
            except FileNotFoundError:
                raise ValueError(f"berth.file 找不到:{b['file']}") from None
            definition = load_berth(path, None if b.get("id") is None else str(b["id"]))
        if isinstance(definition, dict):
            d = dict(definition)
            d["waterSide"] = d.get("waterSide") or "right"
            d["fenderTypes"] = {k: _fill(v, FENDER_TYPE_DEFAULTS) for k, v in (d.get("fenderTypes") or {}).items()}
            d["fenders"] = [dict(f, chainage_m=_num(f.get("chainage_m")) or 0.0) for f in d.get("fenders") or []]
            d["bollards"] = [dict(x, chainage_m=_num(x.get("chainage_m")) or 0.0,
                                  setback_m=2.0 if _num(x.get("setback_m")) is None else _num(x.get("setback_m"))) for x in d.get("bollards") or []]
            nb["definition"] = d
        else:
            nb["definition"] = None
        nb["lines"] = [_fill(dict(ln, id="" if ln.get("id") is None else str(ln["id"])), MOORING_LINE_DEFAULTS)
                       for ln in b.get("lines") or [] if isinstance(ln, dict)]
        out["berth"] = nb
    bk = raw.get("banks")
    if isinstance(bk, dict):
        nbk: dict[str, Any] = {"file": bk.get("file"), "ids": bk.get("ids"), "scale": 1.0 if _num(bk.get("scale")) is None else _num(bk.get("scale")),
                               "lines": [dict(x) for x in bk.get("lines") or [] if isinstance(x, dict)]}
        defs = bk.get("definitions")
        if defs is None and isinstance(bk.get("file"), str) and bk["file"].strip():
            try:
                path = _find_file(bk["file"], base_directory)
            except FileNotFoundError:
                raise ValueError(f"banks.file 找不到:{bk['file']}") from None
            ids = [str(i) for i in bk["ids"]] if isinstance(bk.get("ids"), list) else None
            defs = load_banks(path, ids)
        nbk["definitions"] = [dict(x) for x in defs] if isinstance(defs, list) else None
        for d in nbk["lines"] + (nbk["definitions"] or []):
            d["type"] = d.get("type") or "vertical"
            d["waterSide"] = d.get("waterSide") or "right"
            d["topDepth_m"] = _num(d.get("topDepth_m")) or 0.0
            for key in ("slope", "channelDepth_m"):
                if d.get(key) is not None:
                    d[key] = _num(d[key])
        out["banks"] = nbk
    return out


def structural_issues(sec: dict[str, Any]) -> list[tuple[str, str]]:
    """拖船/錨/泊位/岸壁區段的結構檢查(C# ScenarioExternalForces.CheckStructure、ScenarioBankLoader.CheckStructure 同規則)。"""
    issues: list[tuple[str, str]] = []
    err = lambda p, m: issues.append((p, m))  # noqa: E731
    ids: set[str] = set()
    for i, t in enumerate(sec.get("tugs") or []):
        path = f"tugs[{i}]"
        tid = t.get("id") or ""
        if not tid.strip():
            err(path, "拖船缺少 id")
        elif tid.lower() in ids:
            err(path, f"拖船 id 重複:{tid}")
        else:
            ids.add(tid.lower())
        if not t["bollardPull_t"] > 0:
            err(path, "bollardPull_t 必須為正")
        if not t["riseTime_s"] > 0:
            err(path, "riseTime_s 必須為正")
        if not t["lineLength_m"] > 0:
            err(path, "lineLength_m 必須為正")
        if t.get("turnRate_degPerSec") is not None and t["turnRate_degPerSec"] <= 0:
            err(path, "turnRate_degPerSec 必須為正")
        at = t.get("attach")
        if at is not None:
            ok = (str(at).lower() in [n.lower() for n in BODY_POINT_NAMES]) if not isinstance(at, dict) else \
                (_num(at.get("x")) is not None and _num(at.get("y")) is not None)
            if not ok:
                err(f"{path}.attach", f"連接點須為 {'|'.join(BODY_POINT_NAMES)} 或 {{x, y}}")
        for m in t.get("modes") or []:
            if m not in ("push", "pull"):
                err(f"{path}.modes", f"未知的拖船模式:{m}")
        init = t.get("initial") or {}
        im = init.get("mode")
        if im is not None and im not in TUG_MODES:
            err(f"{path}.initial.mode", f"未知的拖船模式:{im}")
        elif im in ("push", "pull") and t.get("modes") and im not in t["modes"]:
            err(f"{path}.initial.mode", f"初始模式 {im} 不在 modes [{', '.join(t['modes'])}] 內")
        pp = init.get("power_pct")
        if pp is not None and (pp < 0 or pp > 100):
            err(f"{path}.initial.power_pct", "power_pct 須在 0–100")
        if not (t["pushHalfAt_kn"] > t["pushFullUpTo_kn"]) or not (t["pullHalfAt_kn"] > t["pullFullUpTo_kn"]):
            err(path, "效能遞減速度須 halfAt_kn > fullUpTo_kn")
    if sec.get("seabed") is not None and sec["seabed"] not in SEABED_TYPES:
        err("environment.seabed", f"seabed 須為 {'|'.join(SEABED_TYPES)}:{sec['seabed']}")
    an = sec.get("anchors")
    if an is not None:
        for side in ("port", "starboard"):
            a = an.get(side)
            if a is not None and ((a.get("length_m") is not None and a["length_m"] < 0) or (a.get("length_shackles") is not None and a["length_shackles"] < 0)):
                err(f"anchors.{side}", "鏈長不可為負")
        if not an["holdingFactor"] > 0:
            err("anchors.holdingFactor", "holdingFactor 必須為正")
        if not an["dragSpeed_mps"] > 0:
            err("anchors.dragSpeed_mps", "dragSpeed_mps 必須為正")
    b = sec.get("berth")
    if isinstance(b, dict) and b.get("departedDistance_m") is not None:
        dd = b["departedDistance_m"]
        if not (isinstance(dd, (int, float)) and not isinstance(dd, bool) and math.isfinite(dd) and dd > 0):
            err("berth.departedDistance_m", "departedDistance_m 必須為有限正數(m)")  # 同 C# ScenarioExternalForces 結構檢查
    if b is not None:
        d = b.get("definition")
        if d is None:
            err("berth", "berth 需要 definition,或 file(+ id)由載入時解析")
        else:
            quay = d.get("quay") or []
            if len(quay) < 2:
                err("berth.definition.quay", "碼頭線至少需要 2 點")
            for p in quay:
                if not ((_num(p.get("lat")) is not None and _num(p.get("lon")) is not None) or (_num(p.get("x")) is not None and _num(p.get("y")) is not None)):
                    err("berth.definition.quay", "碼頭線頂點需為 {lat, lon} 或 {x, y}")
                    break
            if d.get("waterSide") not in ("left", "right"):
                err("berth.definition.waterSide", "waterSide 須為 left | right")
            for k, ft in d["fenderTypes"].items():
                if not (ft["height_m"] > 0 and ft["ratedReaction_kN"] > 0 and ft["width_m"] > 0 and ft["ratedDeflection_pct"] > 0):
                    err(f"berth.definition.fenderTypes.{k}", "碰墊 height_m/width_m/ratedReaction_kN/ratedDeflection_pct 必須為正")
                c = ft.get("curve")
                if c is not None:
                    if len(c) < 2 or any(not isinstance(pt, list) or len(pt) != 2 for pt in c):
                        err(f"berth.definition.fenderTypes.{k}.curve", "曲線須為至少 2 個 [壓縮 %, 反力 %] 點")
                    else:
                        if c[0][0] != 0 or c[0][1] != 0:
                            err(f"berth.definition.fenderTypes.{k}.curve", "曲線須自 [0, 0] 起")
                        for i in range(1, len(c)):
                            if not (c[i][0] > c[i - 1][0]) or c[i][1] < 0:
                                err(f"berth.definition.fenderTypes.{k}.curve", "壓縮 % 須遞增、反力 % 不可為負")
                                break
            fids: set[str] = set()
            for f in d.get("fenders") or []:
                if f.get("id") in fids:
                    err("berth.definition.fenders", f"碰墊 id 重複:{f.get('id')}")
                fids.add(f.get("id"))
                if f.get("type") not in d["fenderTypes"]:
                    err("berth.definition.fenders", f"碰墊 {f.get('id')} 的型式不存在:{f.get('type')}")
            bids: set[str] = set()
            for bo in d.get("bollards") or []:
                if bo.get("id") in bids:
                    err("berth.definition.bollards", f"繫船柱 id 重複:{bo.get('id')}")
                bids.add(bo.get("id"))
            lids: set[str] = set()
            for i, ln in enumerate(b.get("lines") or []):
                path = f"berth.lines[{i}]"
                lid = ln.get("id") or ""
                if not lid.strip():
                    err(path, "繫纜缺少 id")
                elif lid in lids:
                    err(path, f"繫纜 id 重複:{lid}")
                lids.add(lid)
                fl = ln.get("fairlead")
                valid = fl is not None and ((str(fl).lower() in [n.lower() for n in BODY_POINT_NAMES]) if not isinstance(fl, dict)
                                            else (_num(fl.get("x")) is not None and _num(fl.get("y")) is not None))
                if not valid:
                    err(f"{path}.fairlead", "繫纜需要導纜孔(具名點或 {x, y})")
                if ln.get("bollard") is not None and ln["bollard"] not in bids:
                    err(f"{path}.bollard", f"繫船柱不存在:{ln['bollard']}")
                if not (ln["mbl_t"] > 0 and ln["breakStrain"] > 0 and ln["exponent"] > 0 and ln["length_m"] > 0):
                    err(path, "mbl_t/breakStrain/exponent/length_m 必須為正")
                if ln.get("initial") is not None and ln["initial"] not in ("stowed", "fast"):
                    err(f"{path}.initial", "initial 須為 stowed | fast")
                if ln.get("initial") == "fast" and ln.get("bollard") is None:
                    err(f"{path}.initial", "initial fast 需要 bollard")
    bk = sec.get("banks")
    if bk is not None:
        if not (bk["scale"] >= 0) or math.isinf(bk["scale"]):
            err("banks.scale", "scale 必須 ≥ 0")
        if not bk["lines"] and bk["definitions"] is None:
            err("banks", "banks 需要 lines,或 file(+ ids)由載入時解析")
        else:
            bids2: set[str] = set()
            alld = bk["lines"] + (bk["definitions"] or [])
            for i, d in enumerate(alld):
                path = f"banks.lines[{i}]" if i < len(bk["lines"]) else f"banks.definitions[{i - len(bk['lines'])}]"
                bid = "" if d.get("id") is None else str(d["id"])
                if not bid.strip():
                    err(path, "岸壁缺少 id")
                elif bid in bids2:
                    err(path, f"岸壁 id 重複:{bid}")
                bids2.add(bid)
                line = d.get("line") or []
                if len(line) < 2:
                    err(f"{path}.line", "岸壁折線至少需要 2 點")
                for p in line:
                    if not ((_num(p.get("lat")) is not None and _num(p.get("lon")) is not None) or (_num(p.get("x")) is not None and _num(p.get("y")) is not None)):
                        err(f"{path}.line", "岸壁頂點需為 {lat, lon} 或 {x, y}")
                        break
                if d["waterSide"] not in ("left", "right"):
                    err(f"{path}.waterSide", "waterSide 須為 left | right")
                if d["type"] not in BANK_TYPES:
                    err(f"{path}.type", f"type 須為 {' | '.join(BANK_TYPES)}:{d['type']}")
                sl = d.get("slope")
                if d["type"].lower() == "sloped" and not (sl is not None and sl > 0):
                    err(f"{path}.slope", "斜坡岸需要 slope(1:n 的 n,> 0)")
                if sl is not None and sl <= 0:
                    err(f"{path}.slope", "slope 必須為正")
                if not (d["topDepth_m"] >= 0):
                    err(f"{path}.topDepth_m", "topDepth_m 不可為負")
                cd = d.get("channelDepth_m")
                if cd is not None and not (cd > d["topDepth_m"]):
                    err(f"{path}.channelDepth_m", "channelDepth_m 必須為正且大於 topDepth_m")
    return issues
