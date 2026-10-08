"""標準操縱試驗(MSC.137(76) / ITTC 7.5-02-06-03)的模擬執行器(規劃書 6.3、14 章)。

每個函式以同一艘 ``MMGShip`` 重設初始狀態後執行,回傳含量測值與取樣歷程的結果物件。
座標:初始航向北(ψ=0),x 東、y 北;前進距離沿初始航向量,橫距取絕對值。
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any

from .mmg import DEG, MMGShip, wrap_pi
from .particulars import KN_TO_MPS


@dataclass
class Sample:
    t: float
    x: float
    y: float
    heading_deg: float
    speed_kn: float
    u: float
    v: float
    rot_degpm: float
    rudder_deg: float
    rpm: float
    thrust_kN: float = 0.0


def _sample(ship: MMGShip) -> Sample:
    s = ship.state
    return Sample(
        t=s.t, x=s.x, y=s.y, heading_deg=math.degrees(s.psi) % 360.0,
        speed_kn=math.hypot(s.u, s.v) / KN_TO_MPS, u=s.u, v=s.v,
        rot_degpm=math.degrees(s.r) * 60.0, rudder_deg=math.degrees(s.delta), rpm=s.n * 60.0,
    )


def _settle(ship: MMGShip, speed_kn: float, seconds: float = 0.0) -> None:
    """重設到直航穩態(必要時再跑幾秒讓致動器安定)。"""
    ship.reset(speed_kn=speed_kn)
    if seconds > 0:
        ship.run(seconds)


# ---------------------------------------------------------------------------
# 迴旋
# ---------------------------------------------------------------------------
@dataclass
class TurningResult:
    side: str
    rudder_deg: float
    approach_speed_kn: float
    advance_m: float = math.nan
    transfer_m: float = math.nan
    tactical_diameter_m: float = math.nan
    steady_speed_kn: float = math.nan
    steady_rot_degpm: float = math.nan
    steady_diameter_m: float = math.nan
    marks: dict[int, dict[str, float]] = field(default_factory=dict)  # 航向變化 90/180/270/360 → t、speed
    history: list[Sample] = field(default_factory=list)
    completed: bool = False


def turning_circle(ship: MMGShip, rudder_deg: float = 35.0, side: str = "starboard", approach_speed_kn: float = 10.0,
                   max_heading_change_deg: float = 540.0, max_time_s: float = 1800.0, sample_dt: float = 1.0) -> TurningResult:
    """定常迴旋:記錄 90° 時的前進距離/橫距、180° 時的戰術直徑、360° 後的定常速度與迴轉率。"""
    sign = 1.0 if side.lower().startswith("s") else -1.0
    res = TurningResult(side=side, rudder_deg=rudder_deg, approach_speed_kn=approach_speed_kn)
    _settle(ship, approach_speed_kn)
    ship.set_rudder(sign * rudder_deg)
    psi0 = ship.state.psi
    x0, y0 = ship.state.x, ship.state.y
    total = 0.0
    prev_psi = psi0
    next_mark = 90
    last_sample = -1e9
    speeds_after_360: list[float] = []
    rots_after_360: list[float] = []
    steps = int(round(max_time_s / ship.dt))
    prev = (0.0, 0.0, 0.0, 0.0, 0.0)  # (change, t, speed_kn, along, across) 前一步,供線性內插
    for _ in range(steps):
        ship.step()
        s = ship.state
        total += wrap_pi(s.psi - prev_psi)
        prev_psi = s.psi
        change = sign * math.degrees(total)
        if s.t - last_sample >= sample_dt - 1e-9:
            res.history.append(_sample(ship))
            last_sample = s.t
        # 船體座標中以初始航向為基準的前進(along)與橫向(across)位移
        dx, dy = s.x - x0, s.y - y0
        along = dx * math.sin(psi0) + dy * math.cos(psi0)
        across = sign * (dx * math.cos(psi0) - dy * math.sin(psi0))
        spd = math.hypot(s.u, s.v) / KN_TO_MPS
        cur = (change, s.t, spd, along, across)
        while next_mark <= 360 and change >= next_mark:
            # 在前一步與本步之間對航向變化做線性內插,使量測值對步長平滑(識別用)
            f = (next_mark - prev[0]) / (cur[0] - prev[0]) if cur[0] > prev[0] else 1.0
            f = min(max(f, 0.0), 1.0)
            t_m, v_m, al_m, ac_m = (prev[i] + f * (cur[i] - prev[i]) for i in (1, 2, 3, 4))
            res.marks[next_mark] = {"t_s": t_m, "speed_kn": v_m, "along_m": al_m, "across_m": ac_m}
            if next_mark == 90:
                res.advance_m = al_m
                res.transfer_m = ac_m
            elif next_mark == 180:
                res.tactical_diameter_m = ac_m
            next_mark += 90
        prev = cur
        if change >= 360.0:
            speeds_after_360.append(math.hypot(s.u, s.v))
            rots_after_360.append(s.r)
        if change >= max_heading_change_deg:
            res.completed = True
            break
    if speeds_after_360:
        n = len(speeds_after_360)
        tail = speeds_after_360[n // 2:]
        res.steady_speed_kn = sum(tail) / len(tail) / KN_TO_MPS
        rt = rots_after_360[n // 2:]
        r_mean = sum(rt) / len(rt)
        res.steady_rot_degpm = math.degrees(r_mean) * 60.0
        if abs(r_mean) > 1e-9:
            res.steady_diameter_m = 2.0 * (sum(tail) / len(tail)) / abs(r_mean)
    return res


# ---------------------------------------------------------------------------
# Z 形
# ---------------------------------------------------------------------------
@dataclass
class ZigzagResult:
    rudder_deg: float
    heading_deg: float
    first_side: str
    approach_speed_kn: float
    overshoots_deg: list[float] = field(default_factory=list)
    reversal_times_s: list[float] = field(default_factory=list)
    history: list[Sample] = field(default_factory=list)
    completed: bool = False


def zigzag(ship: MMGShip, rudder_deg: float = 10.0, heading_deg: float | None = None, first_side: str = "port",
           approach_speed_kn: float = 10.0, n_overshoots: int = 3, max_time_s: float = 1200.0, sample_dt: float = 0.5) -> ZigzagResult:
    """Z 形操舵:航向變化達到 ±heading 時反舵,量測各次超越角(度)。"""
    if heading_deg is None:
        heading_deg = rudder_deg
    sign = -1.0 if first_side.lower().startswith("p") else 1.0
    res = ZigzagResult(rudder_deg=rudder_deg, heading_deg=heading_deg, first_side=first_side, approach_speed_kn=approach_speed_kn)
    _settle(ship, approach_speed_kn)
    psi0 = ship.state.psi
    cur = sign
    ship.set_rudder(cur * rudder_deg)
    phase = "waiting"  # waiting: 等航向達到 ±heading;overshoot: 反舵後追蹤極值
    extreme = 0.0
    last_sample = -1e9
    steps = int(round(max_time_s / ship.dt))
    for _ in range(steps):
        ship.step()
        s = ship.state
        dpsi = math.degrees(wrap_pi(s.psi - psi0))
        if s.t - last_sample >= sample_dt - 1e-9:
            res.history.append(_sample(ship))
            last_sample = s.t
        if phase == "waiting":
            if cur * dpsi >= heading_deg:
                cur = -cur
                ship.set_rudder(cur * rudder_deg)
                res.reversal_times_s.append(s.t)
                phase = "overshoot"
                extreme = dpsi
        else:
            # 反舵後航向繼續往原方向變化,直到迴轉率改變方向
            if -cur * dpsi > -cur * extreme:
                extreme = dpsi
            elif (-cur * s.r) < 0.0:
                res.overshoots_deg.append(abs(extreme) - heading_deg)
                phase = "waiting"
                if len(res.overshoots_deg) >= n_overshoots:
                    res.completed = True
                    break
    return res


# ---------------------------------------------------------------------------
# 停船
# ---------------------------------------------------------------------------
@dataclass
class StopResult:
    kind: str
    approach_speed_kn: float
    astern_start_s: float = math.nan
    stop_time_s: float = math.nan
    track_reach_m: float = math.nan
    head_reach_m: float = math.nan
    side_reach_m: float = math.nan
    time_to_target_s: float = math.nan
    distance_to_target_m: float = math.nan
    target_speed_kn: float = math.nan
    final_heading_change_deg: float = math.nan
    history: list[Sample] = field(default_factory=list)
    completed: bool = False


def _run_stop(ship: MMGShip, res: StopResult, max_time_s: float, sample_dt: float, stop_when_u_zero: bool, target_kn: float | None) -> StopResult:
    psi0 = ship.state.psi
    x0, y0 = ship.state.x, ship.state.y
    last_sample = -1e9
    steps = int(round(max_time_s / ship.dt))
    track0 = ship.state.track
    for _ in range(steps):
        ship.step()
        s = ship.state
        if s.t - last_sample >= sample_dt - 1e-9:
            res.history.append(_sample(ship))
            last_sample = s.t
        if math.isnan(res.astern_start_s) and s.n < 0.0:
            res.astern_start_s = s.t
        spd_kn = math.hypot(s.u, s.v) / KN_TO_MPS
        dx, dy = s.x - x0, s.y - y0
        along = dx * math.sin(psi0) + dy * math.cos(psi0)
        across = dx * math.cos(psi0) - dy * math.sin(psi0)
        if target_kn is not None and math.isnan(res.time_to_target_s) and spd_kn <= target_kn:
            res.time_to_target_s = s.t
            res.distance_to_target_m = s.track - track0
            res.target_speed_kn = target_kn
            if not stop_when_u_zero:
                res.head_reach_m = along
                res.side_reach_m = abs(across)
                res.track_reach_m = s.track - track0
                res.final_heading_change_deg = math.degrees(wrap_pi(s.psi - psi0))
                res.completed = True
                break
        if stop_when_u_zero and s.u <= 0.0:
            res.stop_time_s = s.t
            res.track_reach_m = s.track - track0
            res.head_reach_m = along
            res.side_reach_m = abs(across)
            res.final_heading_change_deg = math.degrees(wrap_pi(s.psi - psi0))
            res.completed = True
            break
    return res


def crash_stop(ship: MMGShip, approach_speed_kn: float, astern_order: str | None = None, max_time_s: float = 1500.0, sample_dt: float = 1.0) -> StopResult:
    """緊急停船:直航穩態 → 下倒車令(預設緊急全速退 EFAS,未定義時 FAS);
    量倒車啟動時間、停船時間(u 過零)、航跡距離、head/side reach。"""
    if astern_order is None:
        astern_order = "EFAS" if ship.eng["telegraph"].get("EFAS", 0.0) < ship.eng["telegraph"].get("FAS", 0.0) else "FAS"
    res = StopResult(kind="crashStop", approach_speed_kn=approach_speed_kn)
    _settle(ship, approach_speed_kn)
    ship.set_telegraph(astern_order)
    return _run_stop(ship, res, max_time_s, sample_dt, stop_when_u_zero=True, target_kn=None)


def inertia_stop(ship: MMGShip, approach_speed_kn: float, target_kn: float = 5.0, max_time_s: float = 2400.0, sample_dt: float = 1.0) -> StopResult:
    """慣性停船(停俥滑行):到達 target_kn 的時間與距離。"""
    res = StopResult(kind="inertiaStop", approach_speed_kn=approach_speed_kn)
    _settle(ship, approach_speed_kn)
    ship.set_telegraph("STOP")
    return _run_stop(ship, res, max_time_s, sample_dt, stop_when_u_zero=False, target_kn=target_kn)


def crash_ahead(ship: MMGShip, astern_speed_kn: float, ahead_order: str = "FAH", max_time_s: float = 900.0, sample_dt: float = 1.0) -> StopResult:
    """倒退中下進車令至停船(u 由負過零)。"""
    res = StopResult(kind="crashAhead", approach_speed_kn=-astern_speed_kn)
    ship.reset(speed_kn=0.0, rpm=0.0)
    ship.state.u = -astern_speed_kn * KN_TO_MPS
    ship.state.n = ship.eng["telegraph"]["HAS"] / 60.0
    ship.ctl.rpm_order = ship.eng["telegraph"]["HAS"]
    ship.ctl.telegraph = "HAS"
    ship._eng_mode = "run"
    ship._n_target = ship.state.n
    ship.set_telegraph(ahead_order)
    psi0 = ship.state.psi
    x0, y0 = ship.state.x, ship.state.y
    last_sample = -1e9
    track0 = ship.state.track
    for _ in range(int(round(max_time_s / ship.dt))):
        ship.step()
        s = ship.state
        if s.t - last_sample >= sample_dt - 1e-9:
            res.history.append(_sample(ship))
            last_sample = s.t
        if s.u >= 0.0:
            res.stop_time_s = s.t
            res.track_reach_m = s.track - track0
            dx, dy = s.x - x0, s.y - y0
            res.head_reach_m = abs(dx * math.sin(psi0) + dy * math.cos(psi0))
            res.side_reach_m = abs(dx * math.cos(psi0) - dy * math.sin(psi0))
            res.completed = True
            break
    return res


# ---------------------------------------------------------------------------
# 艏側推迴轉
# ---------------------------------------------------------------------------
@dataclass
class ThrusterTurnResult:
    side: str
    speed_kn: float
    angle_deg: float
    time_s: float = math.nan
    steady_rot_degpm: float = math.nan
    history: list[Sample] = field(default_factory=list)
    completed: bool = False


def thruster_turn(ship: MMGShip, side: str = "port", angle_deg: float = 90.0, speed_kn: float = 0.0,
                  max_time_s: float = 1800.0, sample_dt: float = 2.0) -> ThrusterTurnResult:
    """主機停俥、艏側推全推,量 90° 迴轉時間與定常迴轉率。"""
    sign = 1.0 if side.lower().startswith("s") else -1.0
    res = ThrusterTurnResult(side=side, speed_kn=speed_kn, angle_deg=angle_deg)
    ship.reset(speed_kn=speed_kn, rpm=0.0)
    ship.set_thruster(sign)
    psi0 = ship.state.psi
    total = 0.0
    prev = psi0
    last_sample = -1e9
    rots: list[float] = []
    for _ in range(int(round(max_time_s / ship.dt))):
        ship.step()
        s = ship.state
        total += wrap_pi(s.psi - prev)
        prev = s.psi
        if s.t - last_sample >= sample_dt - 1e-9:
            res.history.append(_sample(ship))
            last_sample = s.t
        if s.t > 60.0:
            rots.append(sign * s.r)
        if sign * math.degrees(total) >= angle_deg:
            res.time_s = s.t
            res.completed = True
            break
    if rots:
        res.steady_rot_degpm = math.degrees(max(rots)) * 60.0
    return res


# ---------------------------------------------------------------------------
# 速度-轉速
# ---------------------------------------------------------------------------
def speed_rpm_table(ship: MMGShip, rpms: list[float]) -> list[dict[str, float]]:
    """直航穩態:各轉速的速度(解析平衡,不經時間積分)。"""
    out = []
    for rpm in rpms:
        u = ship.steady_speed_for_rpm(rpm)
        n = rpm / 60.0
        _, _, _, thrust = ship.forces(u, 0.0, 0.0, ship.delta_neutral, n, 0.0, 0.0)
        T, Q, J, KT = ship.prop.thrust_torque(n, u * (1.0 - ship.wP0), ship.rho)
        power_kw = 2.0 * math.pi * n * Q / 0.98 / 1000.0 if n > 0 else 0.0
        out.append({"rpm": rpm, "speed_kn": u / KN_TO_MPS, "J": J, "KT": KT, "thrust_kN": T / 1e3, "power_kW": power_kw})
    return out


def speed_rpm_timeseries(ship: MMGShip, rpm: float, seconds: float = 1800.0) -> float:
    """由靜止加速至穩態(時間積分版),回傳末速(kn);用於與解析穩態交叉檢查。"""
    ship.reset(speed_kn=0.0, rpm=0.0)
    ship.set_rpm(rpm)
    ship.run(seconds)
    return ship.state.u / KN_TO_MPS


def run_all(ship: MMGShip, targets: dict[str, Any]) -> dict[str, Any]:
    """依 trial_targets 的項目執行所有操縱(識別區與驗證區),回傳結果字典(報告與測試共用)。"""
    out: dict[str, Any] = {}
    ident = targets.get("identification", {})
    val = targets.get("validation", {})
    tc = ident.get("turningCircle35") or val.get("turningCircle35")
    if tc:
        for side in ("port", "starboard"):
            if side in tc:
                out[f"turning_{side}"] = turning_circle(ship, tc.get("rudder_deg", 35.0), side, tc["approachSpeed_kn"])
    for key, rud in (("zigzag10_10", 10.0), ("zigzag20_20", 20.0)):
        zz = ident.get(key) or val.get(key)
        if zz:
            for first in ("portFirst", "starboardFirst"):
                if first in zz:
                    n_os = max(len(zz[first].get("overshoots_deg", [])), 2)
                    out[f"{key}_{first}"] = zigzag(ship, rud, rud, first.replace("First", ""), zz["approachSpeed_kn"], n_overshoots=n_os)
    cs = val.get("crashStop") or ident.get("crashStop")
    if cs:
        out["crashStop"] = crash_stop(ship, cs["approachSpeed_kn"])
    ins = val.get("inertiaStop")
    if ins and "approachSpeed_kn" in ins:
        out["inertiaStop"] = inertia_stop(ship, ins["approachSpeed_kn"], 5.0)
    ca = val.get("crashAhead")
    if ca:
        out["crashAhead"] = crash_ahead(ship, ca["approachSpeedAstern_kn"])
    bt = val.get("bowThrusterTurn90")
    if bt:
        for side in ("port", "starboard"):
            out[f"thrusterTurn_{side}"] = thruster_turn(ship, side, 90.0, bt.get("speed_kn", 0.0))
    sp = val.get("speedPower") or []
    if sp:
        out["speedPower"] = speed_rpm_table(ship, [float(p["rpm"]) for p in sp])
    return out
