"""讀取 ``data/ships/<ID>/particulars.json`` 並依裝載狀態整理成模型需要的量(規劃書 3.1、6.3)。

- ``loading``(full / ballast)決定排水量、吃水、方形係數;particulars 中為 ``null`` 的欄位以經驗式估計,
  並在 ``estimated`` 清單中標註,係數檔與驗證報告會引用這份清單。
- 單位:SI(m、kg、s);轉速以 rpm 保存於資料,模型內部用 rps。
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from . import paths

RHO_WATER = 1025.0  # 海水密度 kg/m^3
RHO_AIR = 1.225  # 空氣密度 kg/m^3
KN_TO_MPS = 1852.0 / 3600.0
GRAVITY = 9.80665

# 車鐘名稱(state.schema.json / command.schema.json)對 particulars.engine.telegraph 鍵名
TELEGRAPH_KEYS = {
    "NAVF": "fullSea",
    "FAH": "fullAhead",
    "HAH": "halfAhead",
    "SAH": "slowAhead",
    "DSAH": "deadSlowAhead",
    "STOP": None,
    "DSAS": "deadSlowAstern",
    "SAS": "slowAstern",
    "HAS": "halfAstern",
    "FAS": "fullAstern",
    "EFAS": "emergencyFullAstern",
}


@dataclass
class LoadingCondition:
    name: str
    displacement_t: float
    draft_fore_m: float
    draft_aft_m: float
    draft_mean_m: float
    block_coefficient: float
    label: str = ""
    source: str = ""
    estimated: list[str] = field(default_factory=list)

    @property
    def mass_kg(self) -> float:
        return self.displacement_t * 1000.0

    @property
    def volume_m3(self) -> float:
        return self.mass_kg / RHO_WATER

    @property
    def trim_m(self) -> float:
        """艉傾為正。"""
        return self.draft_aft_m - self.draft_fore_m


@dataclass
class ShipParticulars:
    ship_id: str
    name: str
    raw: dict[str, Any]
    loading: LoadingCondition
    estimated: list[str] = field(default_factory=list)

    # ---- 主尺寸 ----
    @property
    def lpp(self) -> float:
        return float(self.raw["hull"]["lengthBetweenPerpendiculars_m"])

    @property
    def loa(self) -> float:
        return float(self.raw["hull"]["lengthOverall_m"])

    @property
    def breadth(self) -> float:
        return float(self.raw["hull"]["breadth_m"])

    @property
    def depth(self) -> float:
        return float(self.raw["hull"]["depth_m"])

    @property
    def draft(self) -> float:
        return self.loading.draft_mean_m

    @property
    def cb(self) -> float:
        return self.loading.block_coefficient

    @property
    def mass(self) -> float:
        return self.loading.mass_kg

    # ---- 主機 ----
    @property
    def engine(self) -> dict[str, Any]:
        return self.raw["engine"]

    @property
    def mcr_rpm(self) -> float:
        return float(self.engine["mcr_rpm"])

    def telegraph_rpm(self, order: str) -> float:
        """車鐘 → 指令轉速(rpm,倒車負);EFAS 未定義時用 FAS。"""
        key = TELEGRAPH_KEYS[order]
        if key is None:
            return 0.0
        table = self.engine["telegraph"]
        if key not in table and order == "EFAS":
            key = "fullAstern"
        return float(table[key]["rpm"])

    def telegraph_speed_kn(self, order: str) -> float | None:
        """海報車鐘對照速度(依裝載狀態);無資料回 None。"""
        key = TELEGRAPH_KEYS[order]
        if key is None:
            return 0.0
        entry = self.engine["telegraph"].get(key, {})
        k = "speedLoaded_kn" if self.loading.name == "full" else "speedBallast_kn"
        v = entry.get(k)
        return float(v) if v is not None else None

    @property
    def speed_trial(self) -> list[dict[str, float]]:
        return list(self.engine.get("speedTrial") or [])

    # ---- 螺槳 ----
    @property
    def propeller(self) -> dict[str, Any]:
        return self.raw["propeller"]

    # ---- 舵 ----
    @property
    def rudder(self) -> dict[str, Any]:
        return self.raw["rudder"]

    @property
    def is_schilling(self) -> bool:
        return "schilling" in str(self.rudder.get("type", "")).lower()

    @property
    def rudder_area(self) -> float:
        """舵面積;``null`` 時以 L×T/55(一般商船 1/50 至 1/60)估計並標註。"""
        a = self.rudder.get("area_m2")
        if a is not None:
            return float(a)
        return self.lpp * self.draft / 55.0

    @property
    def rudder_max_angle(self) -> float:
        return float(self.rudder.get("maxAngle_deg", 35.0))

    @property
    def rudder_rate_degps(self) -> float:
        """舵機速率:``hardOverTime35_s`` 解讀為 35° 一舷至 35° 另一舷(70° 行程)所需秒數;
        兩泵資料優先(操縱時兩泵並聯)。"""
        sg = self.rudder.get("steeringGear", {})
        t = sg.get("hardOverTime35_s") or sg.get("hardOverTime35_twoPumps_s") or sg.get("hardOverTime35_onePump_s")
        if not t:
            return 70.0 / 28.0
        return 70.0 / float(t)

    # ---- 艏側推 ----
    @property
    def bow_thruster(self) -> dict[str, Any]:
        return self.raw.get("bowThruster") or {}

    # ---- 受風面積(估計) ----
    def windage_areas(self) -> tuple[float, float, float, list[str]]:
        """回傳 (側面積 A_L, 正面積 A_T, 側面積形心距船舯 s_L(前正), 估計標註)。

        particulars.windage 為 null 時以 LOA×乾舷 + 上層建築估計:住艙約 4 層 11 m 高、長 0.15 LOA、位於船尾;
        甲板管路與裝卸設備(瀝青船加熱系統)以 3 m 高、0.5 LOA 計(規劃書 3.2)。
        """
        notes: list[str] = []
        w = self.raw.get("windage") or {}
        freeboard = max(self.depth - self.draft, 0.5)
        acc_h, acc_len = 11.0, 0.15 * self.loa
        deck_h, deck_len = 3.0, 0.5 * self.loa
        a_l = w.get("lateralArea_m2")
        if a_l is None:
            hull = self.loa * freeboard
            acc = acc_len * acc_h
            deck = deck_len * deck_h
            a_l = hull + acc + deck
            # 形心:船體乾舷在船舯、住艙在 -0.40 LOA、甲板設備在 -0.05 LOA
            s_l = (hull * 0.0 + acc * (-0.40 * self.loa) + deck * (-0.05 * self.loa)) / a_l
            notes.append(f"windage.lateralArea_m2 以 LOA×乾舷+上層建築估計 = {a_l:.0f} m^2")
        else:
            a_l = float(a_l)
            s_l = -0.10 * self.loa
            notes.append("windage 側面積形心假設在船舯後 0.10 LOA")
        a_t = w.get("frontalArea_m2")
        if a_t is None:
            a_t = self.breadth * freeboard + 0.9 * self.breadth * acc_h
            notes.append(f"windage.frontalArea_m2 以 B×乾舷+住艙估計 = {a_t:.0f} m^2")
        else:
            a_t = float(a_t)
        return float(a_l), float(a_t), float(s_l), notes


def _resolve_loading(raw: dict[str, Any], loading: str) -> LoadingCondition:
    """由 particulars.loadingConditions 取出裝載狀態;缺值以經驗式補並標註。"""
    conds = raw["loadingConditions"]
    if loading not in conds:
        raise KeyError(f"particulars 無裝載狀態 {loading!r}(可用:{list(conds)})")
    c = conds[loading]
    full = conds["full"]
    hull = raw["hull"]
    lpp = float(hull["lengthBetweenPerpendiculars_m"])
    b = float(hull["breadth_m"])
    est: list[str] = []

    df, da, dm = c.get("draftFore_m"), c.get("draftAft_m"), c.get("draftMean_m")
    if dm is None:
        if df is None or da is None:
            raise ValueError(f"{loading}: 吃水資料不足")
        dm = 0.5 * (float(df) + float(da))
        est.append("draftMean_m 由艏艉吃水平均")
    dm = float(dm)
    df = float(df) if df is not None else dm
    da = float(da) if da is not None else dm

    cb = c.get("blockCoefficient")
    if cb is None:
        # 方形係數隨吃水下降而減少:以 No.1 滿載→壓載的實測斜率(約 0.11 /(1-T/T_full))外推
        cb_full = float(full["blockCoefficient"])
        t_full = float(full["draftMean_m"])
        cb = cb_full - 0.11 * max(0.0, 1.0 - dm / t_full)
        est.append(f"blockCoefficient 由滿載 Cb 依吃水比估計 = {cb:.3f}")
    cb = float(cb)

    disp = c.get("displacement_t")
    if disp is None:
        disp = cb * lpp * b * dm * RHO_WATER / 1000.0
        est.append(f"displacement_t 由 Cb·L·B·T·ρ 估計 = {disp:.0f} t")
    return LoadingCondition(
        name=loading,
        displacement_t=float(disp),
        draft_fore_m=df,
        draft_aft_m=da,
        draft_mean_m=dm,
        block_coefficient=cb,
        label=str(c.get("label", "")),
        source=str(c.get("source", "")),
        estimated=est,
    )


def load_particulars(ship_id: str, loading: str = "full", path: Path | None = None) -> ShipParticulars:
    """讀取船舶資料。``loading`` 為 ``full`` 或 ``ballast``。"""
    p = path or paths.particulars_path(ship_id)
    with open(p, encoding="utf-8") as f:
        raw = json.load(f)
    if raw.get("id") != ship_id:
        raise ValueError(f"{p}: id={raw.get('id')!r} 與要求的 {ship_id!r} 不符")
    cond = _resolve_loading(raw, loading)
    sp = ShipParticulars(ship_id=ship_id, name=str(raw.get("name", ship_id)), raw=raw, loading=cond)
    sp.estimated.extend(cond.estimated)
    if raw["rudder"].get("area_m2") is None:
        sp.estimated.append(f"rudder.area_m2 以 L·T/55 估計 = {sp.rudder_area:.2f} m^2")
    return sp


def load_trial_targets(ship_id: str, path: Path | None = None) -> dict[str, Any]:
    p = path or paths.trial_targets_path(ship_id)
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def froude_number(speed_mps: float, lpp: float) -> float:
    return speed_mps / math.sqrt(GRAVITY * lpp)
