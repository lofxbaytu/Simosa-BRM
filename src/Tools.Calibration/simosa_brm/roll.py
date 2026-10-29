"""解耦 1 自由度線性橫搖(情境 ``ship.roll``;docs/design/physics-roll.md;C# ``src/SimCore/Physics/RollModel.cs`` 逐式對應)。

- 方程式:φ'' + 2ζω φ' + ω² φ = ω²·(φ_turn + φ_wind) + ω²·φ_wave(t);φ 右傾為正(弧度)。
- φ_turn = −atan(v_c·r/g)·turnHeelGain(右轉 r > 0 向外 = 左傾,負);φ_wind = atan(F_y·z_w/(Δ g GM))·windHeelGain(視風橫向力 F_y 右舷正,
  風自右舷來 → 向下風 = 左傾);φ_wave 為自船位置的有效波斜率(地固波場 5 個浪譜成分的正弦和,相位由情境種子的 .NET ``Random(seed)`` 序列
  抽取一次 — ``DotNetRandom`` 為其逐位元移植,與 C# ``DeterministicRandom`` 相同;遭遇頻率由船在波場中移動自然產生)。
- 固定步長 RK4,與主迴圈同步:``MMGShip.step`` 於 3-DOF RK4 之前以步首狀態、步首環境與目前 Hs 推進;不回饋到 3-DOF。
- 未啟用(情境無 ``ship.roll`` 或 ``enabled`` 為 False)時 ``MMGShip.roll`` 為 None:``state_json`` 無 ``roll``、雜湊不追加區塊,數值與舊版相同。
- 確定性:純 Python 浮點運算、相位只由種子決定;跨語言夾具 ``tests/SimCore.Tests/Golden/roll_run.{yaml,csv}``(``export_roll_reference.py``)。
"""

from __future__ import annotations

import math
import struct
from typing import Any, Callable

from .external import Kin

GRAVITY = 9.80665
TWO_PI = 2.0 * math.pi
DEG2RAD = math.pi / 180.0
RAD2DEG = 180.0 / math.pi

# C# RollModel 常數
HASH_MARKER = 0x524F4C4C444F4631  # "ROLLDOF1"
HASH_LONGS = 5
WAVE_COMPONENTS = 5
OMEGA_MIN_RATIO = 0.6
OMEGA_MAX_RATIO = 2.0
DEFAULT_GM = 1.2  # m;particulars gm_m 缺值時(ASSUMED)
DEFAULT_DAMPING_RATIO = 0.08
WEISS_COEFFICIENT = 0.4  # T = 2·c·B/√GM
AIR_DENSITY = 1.225
WIND_DRAG_COEFFICIENT = 0.7
WATER_DENSITY = 1025.0

# .NET System.Random(seed)(Net5CompatSeedImpl / Knuth 減法產生器)的常數
_MBIG = 2147483647
_MSEED = 161803398


class DotNetRandom:
    """.NET ``System.Random(int seed)`` 的逐位元移植(``NextDouble()`` = InternalSample()·(1/int.MaxValue)),
    供橫搖相位與 C# ``DeterministicRandom`` 得到相同序列(``tests/test_roll.py`` 以 .NET 實際輸出核對)。"""

    def __init__(self, seed: int) -> None:
        seed = int(seed)
        subtraction = _MBIG if seed == -2147483648 else abs(seed)
        mj = _MSEED - subtraction
        arr = [0] * 56
        arr[55] = mj
        mk = 1
        for i in range(1, 55):
            ii = (21 * i) % 55
            arr[ii] = mk
            mk = mj - mk
            if mk < 0:
                mk += _MBIG
            mj = arr[ii]
        for _ in range(1, 5):
            for i in range(1, 56):
                n = i + 30
                if n >= 55:
                    n -= 55
                arr[i] -= arr[1 + n]
                if arr[i] < 0:
                    arr[i] += _MBIG
        self._arr = arr
        self._inext = 0
        self._inextp = 21

    def _internal_sample(self) -> int:
        a = self._inext + 1
        if a >= 56:
            a = 1
        b = self._inextp + 1
        if b >= 56:
            b = 1
        ret = self._arr[a] - self._arr[b]
        if ret == _MBIG:
            ret -= 1
        if ret < 0:
            ret += _MBIG
        self._arr[a] = ret
        self._inext = a
        self._inextp = b
        return ret

    def next_double(self) -> float:
        return self._internal_sample() * (1.0 / _MBIG)


def draw_phases(seed: int) -> list[float]:
    """浪激勵相位 ε_i = 2π·NextDouble(),以情境種子建立獨立的產生器依序抽 WAVE_COMPONENTS 個(C# RollModel.DrawPhases)。"""
    rng = DotNetRandom(seed)
    return [TWO_PI * rng.next_double() for _ in range(WAVE_COMPONENTS)]


def normalise_roll(raw: Any) -> dict[str, Any] | None:
    """``ship.roll`` → 正規化 dict(結構檢查已於 scenario.roll_issues 通過):enabled 布林、其餘數值轉 float 或 None;非物件 → None。"""
    if not isinstance(raw, dict):
        return None

    def num(key: str) -> float | None:
        v = raw.get(key)
        return None if v is None or isinstance(v, bool) else float(v)

    return {
        "enabled": raw.get("enabled") is True,  # 非布林值在 roll_issues 已報錯(網頁編輯器同訊息)
        "naturalPeriod_s": num("naturalPeriod_s"),
        "dampingRatio": num("dampingRatio"),
        "windHeelGain": num("windHeelGain"),
        "turnHeelGain": num("turnHeelGain"),
        "waveGain": num("waveGain"),
    }


def compute_turn_heel(u: float, v: float, r: float, gain: float) -> float:
    """迴轉離心傾角(弧度):−atan(v_c·r/g)·gain;右轉向外傾 = 左傾(負)。"""
    vc = math.sqrt(u * u + v * v)
    return -math.atan(vc * r / GRAVITY) * gain


def compute_wind_heel(wind_e: float, wind_n: float, vel_e: float, vel_n: float, psi: float,
                      lateral_area: float, lever: float, mass: float, gm: float, gain: float) -> float:
    """風傾等效靜傾角(弧度;C# RollModel.ComputeWindHeel):視風 = 風向量 − 對地速度,船體座標分量 (a_x, a_y),
    F_y = ½ρ_a·C_Dt·A_L·|a|·a_y(右舷正),φ = atan(F_y·z_w/(Δ g GM))·gain。"""
    a_e = wind_e - vel_e
    a_n = wind_n - vel_n
    c = math.cos(psi)
    s = math.sin(psi)
    ax = a_e * s + a_n * c
    ay = a_e * c - a_n * s
    speed = math.sqrt(ax * ax + ay * ay)
    fy = 0.5 * AIR_DENSITY * WIND_DRAG_COEFFICIENT * lateral_area * speed * ay
    return math.atan(fy * lever / (mass * GRAVITY * gm)) * gain


def compute_wave_slope(omega: list[float], wavenumber: list[float], slope_per_hs: list[float], phases: list[float], dir_rad: float,
                       x: float, y: float, psi: float, hs: float, t: float, gain: float) -> float:
    """有效波斜率 φ_wave(弧度;C# RollModel.ComputeWaveSlope):s = −(x·sin D + y·cos D) 沿傳播方向座標,
    φ_wave = −sin χ·Hs·Σ (α_i/Hs)·cos(ω_i t − k_i s + ε_i)·gain,χ = 來向 − 航向。"""
    chi = math.fmod(dir_rad - psi, TWO_PI)
    if chi > math.pi:
        chi -= TWO_PI
    if chi <= -math.pi:
        chi += TWO_PI
    sin_chi = math.sin(chi)
    s = -(x * math.sin(dir_rad) + y * math.cos(dir_rad))
    total = 0.0
    for i in range(len(omega)):
        theta = omega[i] * t - wavenumber[i] * s + phases[i]
        total += slope_per_hs[i] * math.cos(theta)
    return -sin_chi * hs * total * gain


def integrate(angle: float, rate: float, omega: float, zeta: float, static_heel: float,
              wave_at: Callable[[float], float] | None, t: float, dt: float) -> tuple[float, float]:
    """一步 RK4(C# RollModel.Integrate 同運算順序):φ'' = ω²·(φ_static + φ_wave(t) − φ) − 2ζω·φ'。"""
    w2 = omega * omega
    c1 = 2.0 * zeta * omega
    target0 = static_heel + (wave_at(t) if wave_at is not None else 0.0)
    target_h = static_heel + (wave_at(t + 0.5 * dt) if wave_at is not None else 0.0)
    target1 = static_heel + (wave_at(t + dt) if wave_at is not None else 0.0)
    k1a = rate
    k1r = w2 * (target0 - angle) - c1 * rate
    a2 = angle + 0.5 * dt * k1a
    r2 = rate + 0.5 * dt * k1r
    k2a = r2
    k2r = w2 * (target_h - a2) - c1 * r2
    a3 = angle + 0.5 * dt * k2a
    r3 = rate + 0.5 * dt * k2r
    k3a = r3
    k3r = w2 * (target_h - a3) - c1 * r3
    a4 = angle + dt * k3a
    r4 = rate + dt * k3r
    k4a = r4
    k4r = w2 * (target1 - a4) - c1 * r4
    return (angle + dt / 6.0 * (k1a + 2.0 * k2a + 2.0 * k3a + k4a), rate + dt / 6.0 * (k1r + 2.0 * k2r + 2.0 * k3r + k4r))


class RollModel:
    """解耦 1 自由度橫搖(C# RollModel 逐式對應):建構時由 particulars 與 ``ship.roll`` 定案 T、ζ、gain、GM、Δ、側面積與力臂,
    相位由種子抽取;``set_waves`` 設定浪激勵的頻率成分(Hs 於每步傳入);``step`` 推進 φ、φ'。"""

    def __init__(self, raw_particulars: dict[str, Any], loading: str, cfg: dict[str, Any], seed: int | None = None,
                 phases: list[float] | None = None) -> None:
        if phases is None:
            phases = draw_phases(seed if seed is not None else 1)
        if len(phases) != WAVE_COMPONENTS:
            raise ValueError(f"相位數須為 {WAVE_COMPONENTS}")
        self.phases = list(phases)
        self.enabled = bool(cfg.get("enabled", False))
        hull = raw_particulars["hull"]
        conds = raw_particulars.get("loadingConditions") or {}
        lc = conds.get(loading) or {}
        lpp = float(hull["lengthBetweenPerpendiculars_m"])
        loa_raw = hull.get("lengthOverall_m")
        loa = float(loa_raw) if loa_raw is not None and float(loa_raw) > 0.0 else lpp
        self.B = float(hull["breadth_m"])
        # 平均吃水:draftMean_m,缺值時艏艉平均(C# 以 0.5·((fore ?? 0) + (aft ?? 0)))
        dm = lc.get("draftMean_m")
        self.T = float(dm) if dm is not None else 0.5 * (float(lc.get("draftFore_m") or 0.0) + float(lc.get("draftAft_m") or 0.0))
        gm = lc.get("gm_m")
        self.gm_from_particulars = gm is not None and float(gm) > 0.0
        self.gm = float(gm) if self.gm_from_particulars else DEFAULT_GM
        # 排水量:particulars 值,缺值時 Cb·L·B·T·ρ(Cb 缺值時由滿載 Cb 依吃水比估計;與 particulars._resolve_loading 同式)
        disp = lc.get("displacement_t")
        if disp is not None:
            disp_t = float(disp)
        else:
            cb_given = lc.get("blockCoefficient")
            if cb_given is not None:
                cb = float(cb_given)
            else:
                full = conds.get("full") or {}
                cb_full = float(full["blockCoefficient"]) if full.get("blockCoefficient") is not None else 0.75
                t_full = float(full["draftMean_m"]) if full.get("draftMean_m") is not None else self.T
                cb = cb_full - 0.11 * max(0.0, 1.0 - self.T / t_full)
            disp_t = cb * lpp * self.B * self.T * WATER_DENSITY / 1000.0
        self.mass_kg = disp_t * 1000.0
        # 風壓側面積與形心高度(particulars windage.lateralArea_m2 缺值時 LOA×乾舷 + 住艙 0.15 LOA × 11 m + 甲板設備 0.5 LOA × 3 m;particulars.windage_areas 同式)
        freeboard = max(float(hull["depth_m"]) - self.T, 0.5)
        w = raw_particulars.get("windage") or {}
        given_al = w.get("lateralArea_m2") if isinstance(w, dict) else None
        if given_al is not None and not isinstance(given_al, bool) and float(given_al) > 0.0:
            self.lateral_area = float(given_al)
            z_above = 0.5 * (self.lateral_area / loa)
        else:
            hull_area = loa * freeboard
            acc_len = 0.15 * loa
            acc = acc_len * 11.0
            deck_len = 0.5 * loa
            deck = deck_len * 3.0
            self.lateral_area = hull_area + acc + deck
            z_above = (hull_area * (0.5 * freeboard) + acc * (freeboard + 5.5) + deck * (freeboard + 1.5)) / self.lateral_area
        self.wind_lever = z_above + 0.5 * self.T
        tn = cfg.get("naturalPeriod_s")
        self.natural_period = float(tn) if tn is not None else 2.0 * WEISS_COEFFICIENT * self.B / math.sqrt(self.gm)
        self.omega = TWO_PI / self.natural_period
        zeta = cfg.get("dampingRatio")
        self.damping_ratio = float(zeta) if zeta is not None else DEFAULT_DAMPING_RATIO
        self.wind_heel_gain = float(cfg["windHeelGain"]) if cfg.get("windHeelGain") is not None else 1.0
        self.turn_heel_gain = float(cfg["turnHeelGain"]) if cfg.get("turnHeelGain") is not None else 1.0
        self.wave_gain = float(cfg["waveGain"]) if cfg.get("waveGain") is not None else 1.0
        # 浪激勵成分(set_waves)
        self.has_waves = False
        self.wave_dir_rad = 0.0
        self.wave_omega = [0.0] * WAVE_COMPONENTS
        self.wave_k = [0.0] * WAVE_COMPONENTS
        self.slope_per_hs = [0.0] * WAVE_COMPONENTS
        # 狀態
        self.angle = 0.0
        self.rate = 0.0
        self.static_heel = 0.0
        self.turn_heel = 0.0
        self.wind_heel = 0.0
        self.wave_slope = 0.0

    # ------------------------------------------------------------------ 浪設定

    def set_waves(self, tp: float | None, direction_rad: float = 0.0, spectrum: str = "pm", gamma: float = 3.3) -> None:
        """設定(``tp`` 為 None 時清除)浪激勵的頻率成分(C# RollModel.SetWaves):ω_i = ω_p·ρ_i(ρ_i 在 [0.6, 2.0] 等分取中點),
        權重 PM / JONSWAP 正規化,振幅 a_i = (Hs/4)·√(2 w_i),深水 k_i = ω_i²/g,每單位 Hs 的波斜率 k_i·√(2 w_i)/4。相位不變。"""
        if tp is None:
            self.has_waves = False
            return
        spectrum = (spectrum or "pm").lower()
        self.wave_dir_rad = direction_rad
        omega_p = TWO_PI / float(tp)
        weights = [0.0] * WAVE_COMPONENTS
        total = 0.0
        for i in range(WAVE_COMPONENTS):
            rr = OMEGA_MIN_RATIO + (i + 0.5) * ((OMEGA_MAX_RATIO - OMEGA_MIN_RATIO) / WAVE_COMPONENTS)
            r4 = rr * rr * rr * rr
            f = math.exp(-1.25 / r4) / (r4 * rr)
            if spectrum == "jonswap":
                sig = 0.07 if rr <= 1.0 else 0.09
                d = rr - 1.0
                f *= gamma ** math.exp(-(d * d) / (2.0 * sig * sig))
            self.wave_omega[i] = omega_p * rr
            weights[i] = f
            total += f
        for i in range(WAVE_COMPONENTS):
            w = weights[i] / total
            k = self.wave_omega[i] * self.wave_omega[i] / GRAVITY
            self.wave_k[i] = k
            self.slope_per_hs[i] = k * math.sqrt(2.0 * w) / 4.0
        self.has_waves = True

    def set_waves_from(self, waves: Any) -> None:
        """由 ``external.WaveDriftModel``(或 None)取 Tp/來向/浪譜/γ(C# 引擎以目前浪設定 ScenarioWaves 呼叫 SetWaves)。"""
        if waves is None:
            self.set_waves(None)
        else:
            self.set_waves(waves.tp, waves.direction, waves.spectrum, waves.gamma)

    # ------------------------------------------------------------------ 步進

    def wave_slope_at(self, x: float, y: float, psi: float, hs: float, t: float) -> float:
        if not self.has_waves or not hs > 0.0:
            return 0.0
        return compute_wave_slope(self.wave_omega, self.wave_k, self.slope_per_hs, self.phases, self.wave_dir_rad, x, y, psi, hs, t, self.wave_gain)

    def step(self, k: Kin, wind_e: float, wind_n: float, cur_e: float, cur_n: float, hs: float, t: float, dt: float) -> None:
        """推進一步(C# RollModel.Step):輸入皆為步首值 — 狀態、含陣風的風向量(ENU,m/s)、自船位置的流(ENU)、目前 Hs、步首時刻 t。"""
        s = math.sin(k.psi)
        c = math.cos(k.psi)
        vel_e = k.u * s + k.v * c + cur_e
        vel_n = k.u * c - k.v * s + cur_n
        self.turn_heel = compute_turn_heel(k.u, k.v, k.r, self.turn_heel_gain)
        self.wind_heel = compute_wind_heel(wind_e, wind_n, vel_e, vel_n, k.psi, self.lateral_area, self.wind_lever, self.mass_kg, self.gm, self.wind_heel_gain)
        self.static_heel = self.turn_heel + self.wind_heel
        wave_at: Callable[[float], float] | None = None
        if self.has_waves and hs > 0.0:
            x, y, psi = k.x, k.y, k.psi

            def wave_at(tt: float, _x: float = x, _y: float = y, _psi: float = psi, _hs: float = hs) -> float:
                return self.wave_slope_at(_x, _y, _psi, _hs, tt)
        self.wave_slope = wave_at(t) if wave_at is not None else 0.0
        self.angle, self.rate = integrate(self.angle, self.rate, self.omega, self.damping_ratio, self.static_heel, wave_at, t, dt)

    # ------------------------------------------------------------------ 雜湊 / 狀態

    def hash_bytes(self) -> bytes:
        """雜湊區塊(C# RollModel.WriteHash 同配置):標記 ROLLDOF1、φ、φ'、靜傾、步首波斜率。"""
        return struct.pack("<q4d", HASH_MARKER, self.angle, self.rate, self.static_heel, self.wave_slope)

    def state(self) -> dict[str, Any]:
        """state.roll(度、度/秒;C# RollModel.State)。"""
        return {
            "angle_deg": self.angle * RAD2DEG,
            "rate_degps": self.rate * RAD2DEG,
            "naturalPeriod_s": self.natural_period,
            "staticHeel_deg": self.static_heel * RAD2DEG,
        }
