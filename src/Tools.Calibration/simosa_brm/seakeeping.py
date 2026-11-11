"""浪一階振盪運動:升沉(heave)與縱搖(pitch)(情境 ``ship.seakeeping``;規劃書第 6.2 節環境外力的浪、R2「浪」;
docs/design/physics-waves.md 第 11 節;C# ``src/SimCore/Physics/SeakeepingModel.cs`` 逐式對應、同運算順序)。

- 線性、只輸出、不回饋 3 自由度運動(與解耦橫搖 ``roll.py`` 同原則):啟用後 u/v/r/x/y/ψ、陣風亂數序列與未啟用時逐位元相同。
- 波場與橫搖的浪激勵是同一個:``roll.WAVE_COMPONENTS`` 個浪譜成分(ω_i = ω_p·ρ_i,ρ_i 在 [0.6, 2.0] 等分取中點;PM / JONSWAP 權重正規化;
  振幅 a_i = (Hs/4)·√(2 w_i);深水波數 k_i = ω_i²/g),相位 ε_i 由情境種子以 ``roll.draw_phases`` 抽取(同一序列,不另造亂數);
  地固波場 η = Σ a_i·sin(θ_i),θ_i = ω_i t − k_i s + ε_i,s = −(x·sin D + y·cos D) 為沿浪傳播方向的座標(D = 來向)。
- 每個成分以閉式解(線性單自由度對簡諧激勵的穩態響應)合成,不積分、無跨步狀態:
  遭遇頻率 ω_e,i = ω_i + k_i·V_D(V_D = 對地速度朝浪源的分量);χ = 來向 − 航向(0 = 頂浪、±90° = 橫浪),x_i = k_i·L_pp·cos χ / 2。
  升沉:ω_h² = g·A_wp/(∇ + A_33)(A_33 = ∇,ASSUMED;A_wp = C_wp·L·B,C_wp = (1 + 2C_b)/3);激勵 = a_i·sinc(x_i)·exp(−k_i T/2);
  z = Σ 激勵·RAO(r)·sin(θ_i − δ),r = ω_e/ω_h。
  縱搖:ω_p² = g·GM_L/(1.2·(k_yy L)²)(I_55 = ρ∇(k_yy L)²·1.2,ASSUMED;GM_L 情境值或 3·C_wp²·L²/(40·C_b·T));
  激勵 = a_i·k_i·cos χ·F_p(x_i)·exp(−k_i T/2),F_p(x) = 3(sin x − x cos x)/x³;θ = Σ 激勵·RAO(r)·cos(θ_i − δ)(艏仰正)。
  RAO(r) = 1/√((1 − r²)² + (2ζr)²)、相位滯後 δ = atan2(2ζr, 1 − r²);速率為解析微分(RAO、δ、χ 視為緩變)。
- 只在情境 ``ship.seakeeping.enabled`` 且有浪(Hs > 0)時 ``MMGShip.state_json`` 輸出 ``waveMotion``、雜湊追加 ``SEAKEEP1`` 區塊;
  未啟用(``MMGShip.seakeeping`` 為 None)或無浪時省略,數值與舊版相同。
- 確定性:純 Python 浮點運算,輸出只由步末狀態、時刻、目前 Hs、浪設定與種子決定;跨語言夾具
  ``tests/SimCore.Tests/Golden/seakeeping_run.{yaml,csv}``(``export_seakeeping_reference.py``)。
"""

from __future__ import annotations

import math
import struct
from typing import Any

from .external import Kin
from .roll import GRAVITY, OMEGA_MAX_RATIO, OMEGA_MIN_RATIO, RAD2DEG, TWO_PI, WATER_DENSITY, WAVE_COMPONENTS, draw_phases

# C# SeakeepingModel 常數
HASH_MARKER = 0x5345414B45455031  # "SEAKEEP1"
HASH_LONGS = 5
DEFAULT_HEAVE_DAMPING_RATIO = 0.35  # ASSUMED(升沉/縱搖興波阻尼大,文獻量級 0.2–0.5)
DEFAULT_PITCH_DAMPING_RATIO = 0.35  # ASSUMED
DEFAULT_PITCH_GYRADIUS_RATIO = 0.25  # k_yy/L;ASSUMED(商船 0.24–0.26)
HEAVE_ADDED_MASS_RATIO = 1.0  # A_33/(ρ∇);ASSUMED
PITCH_ADDED_INERTIA_FACTOR = 1.2  # I_55 = ρ∇(k_yy L)²·1.2;ASSUMED
SINC_SMALL_X = 1e-8
PITCH_FILTER_SERIES_X = 0.05

SEAKEEPING_KEYS = ("heaveDampingRatio", "pitchDampingRatio", "pitchGyradiusRatio", "gmL_m")


def normalise_seakeeping(raw: Any) -> dict[str, Any] | None:
    """``ship.seakeeping`` → 正規化 dict(結構檢查已於 scenario.seakeeping_issues 通過):enabled 布林、其餘數值轉 float 或 None;非物件 → None。"""
    if not isinstance(raw, dict):
        return None

    def num(key: str) -> float | None:
        v = raw.get(key)
        return None if v is None or isinstance(v, bool) else float(v)

    out: dict[str, Any] = {"enabled": raw.get("enabled") is True}  # 非布林值在 seakeeping_issues 已報錯
    for key in SEAKEEPING_KEYS:
        out[key] = num(key)
    return out


def sinc(x: float) -> float:
    """sinc(x) = sin x / x(|x| < 1e-8 時 1):沿船長均勻平均的波面相對於船舯波面的比值(升沉的長度濾波;可為負 = 反相)。"""
    return 1.0 if abs(x) < SINC_SMALL_X else math.sin(x) / x


def pitch_filter(x: float) -> float:
    """縱搖的長度濾波 F_p(x) = 3(sin x − x cos x)/x³(x → 0 時 1;|x| < 0.05 用級數 1 − x²/10 + x⁴/280):
    均勻水線面上波面一次矩 (12/L³)∫ξ·η dξ 相對於船舯波斜率的比值(偶函數;可為負)。"""
    if abs(x) < PITCH_FILTER_SERIES_X:
        x2 = x * x
        return 1.0 - x2 / 10.0 + x2 * x2 / 280.0
    return 3.0 * (math.sin(x) - x * math.cos(x)) / (x * x * x)


def response(r: float, zeta: float) -> tuple[float, float]:
    """線性單自由度的穩態響應(C# SeakeepingModel.Response):頻率比 r = ω_e/ω_n(可為負)、阻尼比 ζ →
    (RAO = 1/√((1 − r²)² + (2ζr)²), 相位滯後 δ = atan2(2ζr, 1 − r²))。"""
    d = 1.0 - r * r
    q = 2.0 * zeta * r
    return 1.0 / math.sqrt(d * d + q * q), math.atan2(q, d)


def compute(omega: list[float], wavenumber: list[float], amp_per_hs: list[float], smith: list[float], phases: list[float], dir_rad: float,
            x: float, y: float, psi: float, vel_east: float, vel_north: float, hs: float, t: float,
            lpp: float, heave_omega: float, heave_zeta: float, pitch_omega: float, pitch_zeta: float) -> tuple[float, float, float, float]:
    """升沉/縱搖與速率(純函數;C# SeakeepingModel.Compute 同運算順序):回傳 (heave m, pitch rad, heaveRate m/s, pitchRate rad/s)。"""
    chi = math.fmod(dir_rad - psi, TWO_PI)
    if chi > math.pi:
        chi -= TWO_PI
    if chi <= -math.pi:
        chi += TWO_PI
    cos_chi = math.cos(chi)
    sin_dir = math.sin(dir_rad)
    cos_dir = math.cos(dir_rad)
    s = -(x * sin_dir + y * cos_dir)
    # 對地速度朝浪源的分量(= −ds/dt):頂浪前進為正 → 遭遇頻率升高
    towards = vel_east * sin_dir + vel_north * cos_dir
    heave = 0.0
    pitch = 0.0
    heave_rate = 0.0
    pitch_rate = 0.0
    for i in range(len(omega)):
        k = wavenumber[i]
        omega_e = omega[i] + k * towards
        theta = omega[i] * t - k * s + phases[i]
        xh = 0.5 * k * lpp * cos_chi
        a = amp_per_hs[i] * smith[i]
        rao_h, lag_h = response(omega_e / heave_omega, heave_zeta)
        ah = a * sinc(xh) * rao_h
        ph = theta - lag_h
        heave += ah * math.sin(ph)
        heave_rate += ah * omega_e * math.cos(ph)
        rao_p, lag_p = response(omega_e / pitch_omega, pitch_zeta)
        ap = a * k * cos_chi * pitch_filter(xh) * rao_p
        pp = theta - lag_p
        pitch += ap * math.cos(pp)
        pitch_rate -= ap * omega_e * math.sin(pp)
    return hs * heave, hs * pitch, hs * heave_rate, hs * pitch_rate


class SeakeepingModel:
    """浪一階運動(C# SeakeepingModel 逐式對應):建構時由 particulars 與 ``ship.seakeeping`` 定案 L、B、T、C_b、C_wp、A_wp、∇、GM_L、
    ω_h、ω_p 與阻尼比,相位由種子抽取(與橫搖同一序列);``set_waves`` 設定浪成分(Hs 於每次計算傳入);
    ``update_last`` 於步末(與重建/還原/浪設定變更後)重算 ``last``(None = 無浪或 Hs ≤ 0:不輸出、不進雜湊)。"""

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
        self.L = float(hull["lengthBetweenPerpendiculars_m"])
        self.B = float(hull["breadth_m"])
        # 平均吃水:draftMean_m,缺值時艏艉平均(roll.RollModel / C# 相同)
        dm = lc.get("draftMean_m")
        self.T = float(dm) if dm is not None else 0.5 * (float(lc.get("draftFore_m") or 0.0) + float(lc.get("draftAft_m") or 0.0))
        # C_b:particulars 值;缺值時由滿載 C_b 依吃水比估計(particulars._resolve_loading 同式)
        cb_given = lc.get("blockCoefficient")
        if cb_given is not None:
            cb = float(cb_given)
        else:
            full = conds.get("full") or {}
            cb_full = float(full["blockCoefficient"]) if full.get("blockCoefficient") is not None else 0.75
            t_full = float(full["draftMean_m"]) if full.get("draftMean_m") is not None else self.T
            cb = cb_full - 0.11 * max(0.0, 1.0 - self.T / t_full)
        self.cb = cb
        # 排水體積:particulars 排水量 / ρ;缺值時 C_b·L·B·T·ρ / ρ
        disp = lc.get("displacement_t")
        disp_t = float(disp) if disp is not None else cb * self.L * self.B * self.T * WATER_DENSITY / 1000.0
        self.volume = disp_t * 1000.0 / WATER_DENSITY
        self.cwp = (1.0 + 2.0 * cb) / 3.0
        self.waterplane_area = self.cwp * self.L * self.B
        # 升沉:ω_h² = ρ g A_wp / (ρ∇ + A_33),A_33 = HEAVE_ADDED_MASS_RATIO·ρ∇
        self.heave_omega = math.sqrt(GRAVITY * self.waterplane_area / (self.volume * (1.0 + HEAVE_ADDED_MASS_RATIO)))
        hz = cfg.get("heaveDampingRatio")
        self.heave_damping_ratio = float(hz) if hz is not None else DEFAULT_HEAVE_DAMPING_RATIO
        # 縱搖:ω_p² = g·GM_L / (factor·(k_yy L)²)
        gml = cfg.get("gmL_m")
        self.gml_from_scenario = gml is not None
        self.gml = float(gml) if gml is not None else 3.0 * self.cwp * self.cwp * self.L * self.L / (40.0 * cb * self.T)
        kr = cfg.get("pitchGyradiusRatio")
        self.pitch_gyradius_ratio = float(kr) if kr is not None else DEFAULT_PITCH_GYRADIUS_RATIO
        kyy = self.pitch_gyradius_ratio * self.L
        self.pitch_omega = math.sqrt(GRAVITY * self.gml / (PITCH_ADDED_INERTIA_FACTOR * (kyy * kyy)))
        pz = cfg.get("pitchDampingRatio")
        self.pitch_damping_ratio = float(pz) if pz is not None else DEFAULT_PITCH_DAMPING_RATIO
        # 浪成分(set_waves)
        self.has_waves = False
        self.wave_dir_rad = 0.0
        self.wave_omega = [0.0] * WAVE_COMPONENTS
        self.wave_k = [0.0] * WAVE_COMPONENTS
        self.amp_per_hs = [0.0] * WAVE_COMPONENTS
        self.smith = [0.0] * WAVE_COMPONENTS
        # 最近一次結果 (heave m, pitch rad, heaveRate m/s, pitchRate rad/s);None = 無輸出
        self.last: tuple[float, float, float, float] | None = None

    @property
    def heave_natural_period(self) -> float:
        return TWO_PI / self.heave_omega

    @property
    def pitch_natural_period(self) -> float:
        return TWO_PI / self.pitch_omega

    # ------------------------------------------------------------------ 浪設定

    def set_waves(self, tp: float | None, direction_rad: float = 0.0, spectrum: str = "pm", gamma: float = 3.3) -> None:
        """設定(``tp`` 為 None 時清除,``last`` 一併清除)浪成分(C# SeakeepingModel.SetWaves;成分、權重與波數的算式與 roll.RollModel.set_waves 逐項相同),
        另存每單位 Hs 的波幅 √(2 w_i)/4 與 Smith 修正 exp(−k_i·T/2)。相位不變。"""
        if tp is None:
            self.has_waves = False
            self.last = None
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
            self.amp_per_hs[i] = math.sqrt(2.0 * w) / 4.0
            self.smith[i] = math.exp(-0.5 * k * self.T)
        self.has_waves = True

    def set_waves_from(self, waves: Any) -> None:
        """由 ``external.WaveDriftModel``(或 None)取 Tp/來向/浪譜/γ(C# 引擎以目前浪設定 ScenarioWaves 呼叫 SetWaves)。"""
        if waves is None:
            self.set_waves(None)
        else:
            self.set_waves(waves.tp, waves.direction, waves.spectrum, waves.gamma)

    # ------------------------------------------------------------------ 診斷(測試)

    def wave_elevation_at(self, x: float, y: float, hs: float, t: float) -> float:
        """自船位置的波面高 η(m,向上正)= Hs·Σ (a_i/Hs)·sin(ω_i t − k_i s + ε_i)(長波極限下升沉趨近此值;無浪或 Hs ≤ 0 為 0)。"""
        if not self.has_waves or not hs > 0.0:
            return 0.0
        s = -(x * math.sin(self.wave_dir_rad) + y * math.cos(self.wave_dir_rad))
        total = 0.0
        for i in range(WAVE_COMPONENTS):
            total += self.amp_per_hs[i] * math.sin(self.wave_omega[i] * t - self.wave_k[i] * s + self.phases[i])
        return hs * total

    def wave_slope_along_propagation_at(self, x: float, y: float, hs: float, t: float) -> float:
        """自船位置沿浪傳播方向的波斜率 ∂η/∂s(rad)= −Hs·Σ k_i·(a_i/Hs)·cos θ_i(等於正橫浪 χ = +90°、waveGain 1 時橫搖的有效波斜率,
        用以核對兩個模型是同一個波場)。"""
        if not self.has_waves or not hs > 0.0:
            return 0.0
        s = -(x * math.sin(self.wave_dir_rad) + y * math.cos(self.wave_dir_rad))
        total = 0.0
        for i in range(WAVE_COMPONENTS):
            total += self.wave_k[i] * self.amp_per_hs[i] * math.cos(self.wave_omega[i] * t - self.wave_k[i] * s + self.phases[i])
        return -hs * total

    # ------------------------------------------------------------------ 計算

    def evaluate(self, k: Kin, cur_e: float, cur_n: float, hs: float, t: float) -> tuple[float, float, float, float] | None:
        """以狀態、自船位置的流(ENU,m/s;對地速度 = 對水速度 + 流,決定遭遇頻率)、目前 Hs 與時刻 t 計算(C# SeakeepingModel.Evaluate);
        無浪設定或 Hs ≤ 0 → None(不輸出)。"""
        if not self.has_waves or not hs > 0.0:
            return None
        c = math.cos(k.psi)
        s = math.sin(k.psi)
        vel_e = k.u * s + k.v * c + cur_e
        vel_n = k.u * c - k.v * s + cur_n
        return compute(self.wave_omega, self.wave_k, self.amp_per_hs, self.smith, self.phases, self.wave_dir_rad, k.x, k.y, k.psi, vel_e, vel_n, hs, t,
                       self.L, self.heave_omega, self.heave_damping_ratio, self.pitch_omega, self.pitch_damping_ratio)

    def update_last(self, k: Kin, cur_e: float, cur_n: float, hs: float, t: float) -> None:
        """步末(與重建/還原/浪設定變更後):以目前狀態重算 ``last``(雜湊與 state.waveMotion;C# UpdateLast)。"""
        self.last = self.evaluate(k, cur_e, cur_n, hs, t)

    # ------------------------------------------------------------------ 雜湊 / 狀態

    def hash_bytes(self) -> bytes:
        """雜湊區塊(只在 ``last`` 有值時追加;C# SeakeepingModel.WriteHash 同配置):標記 SEAKEEP1、升沉、縱搖(rad)、升沉速率、縱搖速率(rad/s)。"""
        heave, pitch, heave_rate, pitch_rate = self.last if self.last is not None else (0.0, 0.0, 0.0, 0.0)
        return struct.pack("<q4d", HASH_MARKER, heave, pitch, heave_rate, pitch_rate)

    def state(self) -> dict[str, Any] | None:
        """state.waveMotion(m、度、m/s、度/秒;C# SeakeepingModel.State);無輸出時 None。"""
        if self.last is None:
            return None
        heave, pitch, heave_rate, pitch_rate = self.last
        return {
            "heave_m": heave,
            "pitch_deg": pitch * RAD2DEG,
            "heaveRate_mps": heave_rate,
            "pitchRate_dps": pitch_rate * RAD2DEG,
        }
