"""解耦 1 自由度橫搖(情境 ``ship.roll``;docs/design/physics-roll.md;C# RollModelTests 的對應):

- ``roll.DotNetRandom`` 與 .NET ``Random(seed).NextDouble()`` 逐位元相同(以 .NET 8 實際輸出核對),相位同種子同值、不同種子不同;
- 靜傾:右轉向外傾(左傾)、風自右舷來向下風(左傾)且 ∝ 風速平方;自然週期(Weiss 公式、FSB2 GM 預設)與自由衰減;
- 浪激勵 ∝ Hs、迎浪/順浪無激勵;``scenario`` 的結構檢查(路徑與訊息同 C# Scenario.StructuralIssues);
- MMGShip:無 roll 時 ``state_json`` 無 ``roll``、雜湊不變;啟用時 3-DOF 狀態與未啟用逐位元相同(不回饋)、雜湊不同;快照/還原;練習中變更浪;
- 跨語言夾具 tests/SimCore.Tests/Golden/roll_run.{yaml,csv}(export_roll_reference.py 產生)可重現。
"""

from __future__ import annotations

import copy
import importlib.util
import math
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.external import Kin
from simosa_brm.particulars import load_particulars
from simosa_brm.roll import (
    DEFAULT_DAMPING_RATIO,
    DEFAULT_GM,
    GRAVITY,
    RAD2DEG,
    TWO_PI,
    WAVE_COMPONENTS,
    DotNetRandom,
    RollModel,
    compute_turn_heel,
    compute_wind_heel,
    draw_phases,
    integrate,
    normalise_roll,
)
from simosa_brm.scenario import load_scenario, roll_issues, ship_from_scenario, structural_issues, validate_scenario

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"
DEG = math.pi / 180.0

# .NET 8 `new Random(seed).NextDouble()` 前 6 值(由 dotnet 實際執行取得;C# RollModelTests.PhasesAreDeterministicFromSeed 鎖定首值)
DOTNET_REFERENCE = {
    1: [0.24866858415709278, 0.11074397718102856, 0.46701067987224587, 0.7716041220219825, 0.657518893786482, 0.43278260130099144],
    7: [0.38322046929189024, 0.8712556827213874, 0.6609386227377405, 0.052261705534654534, 0.36643333237917786, 0.6761694413964494],
    42: [0.6681064659115423, 0.14090729837348093, 0.12551828945312568, 0.5227642760252413, 0.16843422416990353, 0.26259267528662117],
    -3: [0.2935192125353586, 0.6975812123611482, 0.8649866608274107, 0.19848998319287317, 0.5604903733173806, 0.1805782146661441],
    123456789: [0.5083497583439339, 0.17781306252712992, 0.6219475933452824, 0.40299016302590734, 0.9167651384681301, 0.6860584112284978],
}


def _tree(roll: dict[str, Any] | None, speed: float = 0.0, telegraph: str = "STOP", wind: tuple[float, float, float] = (0.0, 0.0, 0.0),
          waves: dict[str, Any] | None = None, depth: float = 30.0, loading: str = "full", seed: int = 7) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "seed": seed, "ship": {"id": "FSB1", "loading": loading},
        "origin": {"lat": 23.8, "lon": 120.15},
        "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": speed, "telegraph": telegraph},
        "environment": {"wind": {"trueSpeed": wind[0], "trueDir": wind[1], "gustiness": wind[2]}, "current": {"set": 0, "drift": 0}, "waterDepth": depth},
    }
    if roll is not None:
        t["ship"]["roll"] = roll
    if waves is not None:
        t["environment"]["waves"] = waves
    return t


def _ship(tree: dict[str, Any]):
    return ship_from_scenario(validate_scenario(tree), dt=0.02)


def _model(cfg: dict[str, Any] | None = None, seed: int = 7, loading: str = "full", ship: str = "FSB1") -> RollModel:
    return RollModel(load_particulars(ship, loading).raw, loading, cfg or {"enabled": True}, seed=seed)


# ---------------------------------------------------------------------------- 亂數與相位


@pytest.mark.parametrize("seed", sorted(DOTNET_REFERENCE))
def test_dotnet_random_matches_dotnet_output(seed: int) -> None:
    rng = DotNetRandom(seed)
    assert [rng.next_double() for _ in range(6)] == DOTNET_REFERENCE[seed]


def test_phases_are_deterministic_from_seed() -> None:
    a, b, c = draw_phases(7), draw_phases(7), draw_phases(8)
    assert len(a) == WAVE_COMPONENTS and a == b and a != c
    assert all(0.0 <= p < TWO_PI for p in a)
    assert a[0] == TWO_PI * DOTNET_REFERENCE[7][0]
    assert draw_phases(1)[0] == TWO_PI * DOTNET_REFERENCE[1][0]
    assert _model(seed=7).phases == a and _model(seed=8).phases == c


# ---------------------------------------------------------------------------- 設定與純函數


def test_default_natural_period_weiss_and_gm_fallback() -> None:
    full = _model()
    assert full.gm_from_particulars and full.gm == 1.96
    assert full.natural_period == 2.0 * 0.4 * 18.0 / math.sqrt(1.96)
    assert 10.2 < full.natural_period < 10.4
    ballast = _model(loading="ballast")
    assert ballast.gm == 4.15 and 7.0 < ballast.natural_period < 7.2
    fsb2 = _model(ship="FSB2")
    assert not fsb2.gm_from_particulars and fsb2.gm == DEFAULT_GM and 14.5 < fsb2.natural_period < 14.7
    fsb2_ballast = _model(ship="FSB2", loading="ballast")  # 排水量/Cb 缺值 → Cb·L·B·T·ρ 估計(與 particulars._resolve_loading 相同)
    assert fsb2_ballast.mass_kg == load_particulars("FSB2", "ballast").loading.mass_kg
    given = _model({"enabled": True, "naturalPeriod_s": 8.0, "dampingRatio": 0.05, "windHeelGain": 0.5, "turnHeelGain": 1.4, "waveGain": 0.8})
    assert (given.natural_period, given.damping_ratio, given.wind_heel_gain, given.turn_heel_gain, given.wave_gain) == (8.0, 0.05, 0.5, 1.4, 0.8)
    assert given.omega == TWO_PI / 8.0
    assert (full.damping_ratio, full.wind_heel_gain, full.turn_heel_gain, full.wave_gain) == (DEFAULT_DAMPING_RATIO, 1.0, 1.0, 1.0)
    # 側面積與 particulars.windage_areas 的估計相同(FSB1 滿載 575 m²)
    assert full.lateral_area == load_particulars("FSB1", "full").windage_areas()[0]
    assert 574.0 < full.lateral_area < 576.0 and 6.0 < full.wind_lever < 7.5


def test_turn_heel_is_outward() -> None:
    u, r = 5.0, 0.03
    right, left = compute_turn_heel(u, 0.0, r, 1.0), compute_turn_heel(u, 0.0, -r, 1.0)
    assert right < 0.0 < left and right == -math.atan(u * r / GRAVITY) and left == -right
    assert compute_turn_heel(3.0, 4.0, r, 1.0) == -math.atan(5.0 * r / GRAVITY)
    assert compute_turn_heel(u, 0.0, r, 2.0) == 2.0 * right
    assert compute_turn_heel(0.0, 0.0, r, 1.0) == 0.0 and compute_turn_heel(u, 0.0, 0.0, 1.0) == 0.0


def test_wind_heel_is_leeward_and_scales_with_speed_squared() -> None:
    m = _model()
    w = 18.0
    args = (m.lateral_area, m.wind_lever, m.mass_kg, m.gm, 1.0)
    stbd = compute_wind_heel(-w, 0.0, 0.0, 0.0, 0.0, *args)   # 船艏向北,風自東(右舷)
    port = compute_wind_heel(w, 0.0, 0.0, 0.0, 0.0, *args)
    head = compute_wind_heel(0.0, -w, 0.0, 0.0, 0.0, *args)
    assert stbd < 0.0 < port and port == -stbd and head == 0.0
    twice = compute_wind_heel(-2.0 * w, 0.0, 0.0, 0.0, 0.0, *args)
    assert math.tan(twice) == pytest.approx(4.0 * math.tan(stbd), rel=1e-12)
    # 視風:向東航行、無風 = 靜止受東風;航向轉 90° 後同一真風為迎艏風;gain 0 → 0
    assert compute_wind_heel(0.0, 0.0, w, 0.0, 0.0, *args) == stbd
    assert compute_wind_heel(-w, 0.0, 0.0, 0.0, math.pi / 2.0, *args) == pytest.approx(0.0, abs=1e-12)
    assert compute_wind_heel(-w, 0.0, 0.0, 0.0, 0.0, m.lateral_area, m.wind_lever, m.mass_kg, m.gm, 0.0) == 0.0
    assert 0.1 < abs(stbd * RAD2DEG) < 1.0  # FSB1 滿載 35 kn 正橫風約 0.2°


def test_free_decay_period_and_logarithmic_decrement() -> None:
    tn, zeta, dt = 10.0, 0.08, 0.02
    omega = TWO_PI / tn
    angle, rate, t = 10.0 * DEG, 0.0, 0.0
    crossings: list[float] = []
    peaks: list[float] = []
    prev, prev_rate = angle, rate
    for _ in range(6000):
        angle, rate = integrate(angle, rate, omega, zeta, 0.0, None, t, dt)
        t += dt
        if prev < 0.0 <= angle:
            crossings.append(t)
        if prev_rate > 0.0 >= rate and angle > 0.0:
            peaks.append(angle)
        prev, prev_rate = angle, rate
    td = tn / math.sqrt(1.0 - zeta * zeta)
    assert len(crossings) >= 5 and all(td * 0.99 < b - a < td * 1.01 for a, b in zip(crossings, crossings[1:]))
    ratio = math.exp(-2.0 * math.pi * zeta / math.sqrt(1.0 - zeta * zeta))
    assert len(peaks) >= 4 and all(b / a == pytest.approx(ratio, rel=0.02) for a, b in zip(peaks, peaks[1:]))
    angle, rate, t = 0.0, 0.0, 0.0
    for _ in range(15000):
        angle, rate = integrate(angle, rate, omega, zeta, 0.05, None, t, dt)
        t += dt
    assert angle == pytest.approx(0.05, abs=1e-6) and rate == pytest.approx(0.0, abs=1e-6)


def test_wave_slope_scales_with_hs_and_vanishes_for_head_seas() -> None:
    m = _model()
    assert not m.has_waves and m.wave_slope_at(0.0, 0.0, 0.0, 2.0, 12.3) == 0.0
    m.set_waves(8.0, 90.0 * DEG, "pm", 3.3)
    a1, a2 = m.wave_slope_at(0.0, 0.0, 0.0, 1.0, 12.3), m.wave_slope_at(0.0, 0.0, 0.0, 2.0, 12.3)
    assert a1 != 0.0 and a2 == pytest.approx(2.0 * a1, rel=1e-15) and a2 != m.wave_slope_at(0.0, 0.0, 0.0, 2.0, 13.3)
    assert m.wave_slope_at(0.0, 0.0, 90.0 * DEG, 2.0, 12.3) == pytest.approx(0.0, abs=1e-15)
    assert m.wave_slope_at(0.0, 0.0, 270.0 * DEG, 2.0, 12.3) == pytest.approx(0.0, abs=1e-15)
    assert m.wave_slope_at(0.0, 0.0, 180.0 * DEG, 2.0, 12.3) == pytest.approx(-a2, rel=1e-12)
    assert m.wave_slope_at(0.0, 0.0, 0.0, 0.0, 12.3) == 0.0
    max_slope = max(abs(m.wave_slope_at(0.0, 0.0, 0.0, 2.0, 0.1 * i)) for i in range(2000))
    assert 1.0 < max_slope * RAD2DEG < 10.0
    gained = _model({"enabled": True, "waveGain": 0.5})
    gained.set_waves(8.0, 90.0 * DEG, "pm", 3.3)
    assert gained.wave_slope_at(0.0, 0.0, 0.0, 2.0, 12.3) == pytest.approx(0.5 * a2, rel=1e-15)
    m.set_waves(None)
    assert not m.has_waves and m.wave_slope_at(0.0, 0.0, 0.0, 2.0, 12.3) == 0.0


# ---------------------------------------------------------------------------- 結構檢查與正規化


@pytest.mark.parametrize(("roll", "path", "message"), [
    ({"enabled": True, "naturalPeriod_s": 0}, "ship.roll.naturalPeriod_s", "必須為正"),
    ({"enabled": True, "naturalPeriod_s": -5}, "ship.roll.naturalPeriod_s", "必須為正"),
    ({"enabled": True, "naturalPeriod_s": "x"}, "ship.roll.naturalPeriod_s", "必須為正"),
    ({"enabled": True, "dampingRatio": 0}, "ship.roll.dampingRatio", "0–1"),
    ({"enabled": True, "dampingRatio": 1}, "ship.roll.dampingRatio", "0–1"),
    ({"enabled": False, "dampingRatio": 1.5}, "ship.roll.dampingRatio", "0–1"),
    ({"enabled": True, "windHeelGain": -0.1}, "ship.roll.windHeelGain", "≥ 0"),
    ({"enabled": True, "turnHeelGain": -1}, "ship.roll.turnHeelGain", "≥ 0"),
    ({"enabled": True, "waveGain": -2}, "ship.roll.waveGain", "≥ 0"),
    ("yes", "ship.roll", "須為物件"),
    ({"enabled": "false"}, "ship.roll.enabled", "須為布林值"),
    ({"enabled": 1}, "ship.roll.enabled", "須為布林值"),
])
def test_structural_issues_reject_bad_roll(roll: Any, path: str, message: str) -> None:
    issues = structural_issues(_tree(roll))
    assert any(p == path and message in m for p, m in issues), issues
    found: list[tuple[str, str]] = []
    roll_issues(roll, lambda p, m: found.append((p, m)))
    assert found == [i for i in issues if i[0].startswith("ship.roll")]
    with pytest.raises(ValueError):
        validate_scenario(_tree(roll))


def test_valid_roll_has_no_issues_and_normalises() -> None:
    assert structural_issues(_tree({"enabled": True})) == []
    assert structural_issues(_tree(None)) == []
    full = {"enabled": True, "naturalPeriod_s": 9, "dampingRatio": 0.05, "windHeelGain": 0, "turnHeelGain": 1.4, "waveGain": 0.8}
    assert structural_issues(_tree(full)) == []
    sc = validate_scenario(_tree(full))
    assert sc["ship"]["roll"] == {"enabled": True, "naturalPeriod_s": 9.0, "dampingRatio": 0.05, "windHeelGain": 0.0, "turnHeelGain": 1.4, "waveGain": 0.8}
    assert validate_scenario(_tree(None))["ship"]["roll"] is None
    assert validate_scenario(_tree({"enabled": True}))["ship"]["roll"] == {"enabled": True, "naturalPeriod_s": None, "dampingRatio": None, "windHeelGain": None, "turnHeelGain": None, "waveGain": None}
    assert normalise_roll("x") is None and normalise_roll({"enabled": True, "naturalPeriod_s": True})["naturalPeriod_s"] is None


# ---------------------------------------------------------------------------- MMGShip


def _run(ship, seconds: float, rudder_at: tuple[float, float] | None = None) -> None:
    while ship.state.t < seconds - 1e-9:
        if rudder_at is not None and abs(ship.state.t - rudder_at[0]) < 1e-9:
            ship.set_rudder(rudder_at[1])
        ship.step()


def test_without_roll_state_and_hash_unchanged_and_enabled_does_not_feed_back() -> None:
    waves = {"significantHeight_m": 1.5, "peakPeriod_s": 8.0, "direction_deg": 60}
    plain = _ship(_tree(None, speed=8.0, telegraph="HAH", wind=(20.0, 90.0, 0.3), waves=waves))
    disabled = _ship(_tree({"enabled": False, "dampingRatio": 0.1}, speed=8.0, telegraph="HAH", wind=(20.0, 90.0, 0.3), waves=waves))
    enabled = _ship(_tree({"enabled": True}, speed=8.0, telegraph="HAH", wind=(20.0, 90.0, 0.3), waves=waves))
    assert plain.roll is None and disabled.roll is None and enabled.roll is not None
    for s in (plain, disabled, enabled):
        s.set_rudder(15.0)
        for _ in range(400):
            s.step()
    assert plain.state_json() == disabled.state_json() and "roll" not in plain.state_json()
    assert plain.state_hash() == disabled.state_hash()
    assert enabled.state.as_tuple() == plain.state.as_tuple() and enabled.gust_factor == plain.gust_factor
    assert enabled.state_hash() != plain.state_hash()
    st = enabled.state_json()
    assert set(st["roll"]) == {"angle_deg", "rate_degps", "naturalPeriod_s", "staticHeel_deg"}
    assert st["roll"]["angle_deg"] != 0.0 and st["roll"]["naturalPeriod_s"] == enabled.roll.natural_period
    assert {k: v for k, v in st.items() if k != "roll"} == plain.state_json()


def test_static_heel_in_turn_and_wind_and_resonant_amplitude() -> None:
    # 15 kn、35 kn 正橫風自右舷、無浪:橫搖收斂到靜傾(左傾);35° 右舵迴轉後外傾加大;左轉右傾
    ship = _ship(_tree({"enabled": True}, speed=15.0, telegraph="NAVF", wind=(35.0, 90.0, 0.0), depth=1000.0))
    _run(ship, 90.0)
    st = ship.state_json()["roll"]
    assert ship.roll.wind_heel < 0.0 and 0.05 < abs(ship.roll.wind_heel * RAD2DEG) < 1.0
    assert abs(st["angle_deg"] - st["staticHeel_deg"]) < 0.05
    ship.set_rudder(35.0)
    _run(ship, 300.0)
    assert ship.state.r > 0.0 and ship.roll.turn_heel < 0.0 and 0.3 < abs(ship.roll.turn_heel * RAD2DEG) < 5.0
    left = _ship(_tree({"enabled": True}, speed=15.0, telegraph="NAVF", depth=1000.0))
    left.set_rudder(-35.0)
    _run(left, 200.0)
    assert left.state.r < 0.0 and left.roll.turn_heel > 0.0 and left.state_json()["roll"]["angle_deg"] > 0.0
    # 靜止、Hs 2 m 正橫浪:Tp = 自然週期時振幅數度到二十度,短波較小;Hs 線性
    tn = _model().natural_period

    def max_angle(tp: float) -> float:
        s = _ship(_tree({"enabled": True}, waves={"significantHeight_m": 2.0, "peakPeriod_s": tp, "direction_deg": 90}))
        mx = 0.0
        for _ in range(50 * 300):
            s.step()
            mx = max(mx, abs(s.roll.angle))
        return mx * RAD2DEG

    resonant, short_wave = max_angle(tn), max_angle(4.0)
    assert 3.0 < resonant < 30.0 and short_wave < 0.7 * resonant  # 短波無有效波斜率折減(physics-roll.md 第 7 節),約為共振的一半
    # 線性:相同運動輸入下 Hs 減半(或 waveGain 減半)→ φ 減半(引擎內 Hs 也改變漂移力與位置/航向,故以純模型同輸入比較)
    m1, m2, g5 = _model(), _model(), _model({"enabled": True, "waveGain": 0.5})
    for m in (m1, m2, g5):
        m.set_waves(9.0, 90.0 * DEG, "pm", 3.3)
    still = Kin(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    for i in range(6000):
        t = i * 0.02
        m1.step(still, 0.0, 0.0, 0.0, 0.0, 1.0, t, 0.02)
        m2.step(still, 0.0, 0.0, 0.0, 0.0, 2.0, t, 0.02)
        g5.step(still, 0.0, 0.0, 0.0, 0.0, 2.0, t, 0.02)
    assert m1.angle != 0.0 and m2.angle == pytest.approx(2.0 * m1.angle, rel=1e-12) and g5.angle == pytest.approx(m1.angle, rel=1e-12)


def test_set_waves_changes_excitation_and_snapshot_restore_bit_identical() -> None:
    ship = _ship(_tree({"enabled": True}))
    assert not ship.roll.has_waves
    _run(ship, 5.0)
    assert ship.roll.wave_slope == 0.0 and ship.roll.angle == 0.0
    ship.set_waves({"significantHeight_m": 2.0, "peakPeriod_s": 8.0, "direction_deg": 90})
    assert ship.roll.has_waves
    _run(ship, 60.0)
    assert ship.roll.wave_slope != 0.0 and ship.roll.angle != 0.0
    ship.set_waves(None)
    assert not ship.roll.has_waves
    ship.step()
    assert ship.roll.wave_slope == 0.0
    # 快照/還原(含浪、風、迴轉):續跑與連續執行逐位元相同;舊快照(無 roll 鍵)還原為初始
    sc = _tree({"enabled": True}, speed=8.0, telegraph="HAH", wind=(25.0, 60.0, 0.2), waves={"significantHeight_m": 2.0, "peakPeriod_s": 9.0, "direction_deg": 120, "spectrum": "jonswap"})
    s = _ship(sc)
    s.set_rudder(20.0)
    _run(s, 10.0)
    snap = s.snapshot()
    assert snap["roll"] is not None and snap["roll"].phases == s.roll.phases
    _run(s, 40.0)
    h, angle, rate, st = s.state_hash(), s.roll.angle, s.roll.rate, s.state_json()
    s.restore(snap)
    assert s.roll.angle == snap["roll"].angle and s.roll.has_waves
    _run(s, 40.0)
    assert s.state_hash() == h and s.roll.angle == angle and s.roll.rate == rate and s.state_json() == st
    again = _ship(sc)
    again.set_rudder(20.0)
    _run(again, 40.0)
    assert again.state_hash() == h
    legacy = {k: copy.deepcopy(v) for k, v in snap.items() if k != "roll"}
    s.restore(legacy)
    assert s.roll is not None and s.roll.angle == 0.0 and s.roll.phases == draw_phases(7)


def test_roll_steps_with_kin_inputs_match_pure_functions() -> None:
    m = _model()
    m.set_waves(8.0, 120.0 * DEG, "jonswap", 3.3)
    k = Kin(10.0, 20.0, 30.0 * DEG, 5.0, -0.3, 0.02)
    m.step(k, -8.0, 3.0, 0.2, -0.1, 2.0, 12.0, 0.02)
    s, c = math.sin(k.psi), math.cos(k.psi)
    vel_e, vel_n = k.u * s + k.v * c + 0.2, k.u * c - k.v * s - 0.1
    assert m.turn_heel == compute_turn_heel(k.u, k.v, k.r, 1.0)
    assert m.wind_heel == compute_wind_heel(-8.0, 3.0, vel_e, vel_n, k.psi, m.lateral_area, m.wind_lever, m.mass_kg, m.gm, 1.0)
    assert m.static_heel == m.turn_heel + m.wind_heel and m.wave_slope == m.wave_slope_at(k.x, k.y, k.psi, 2.0, 12.0)
    assert m.angle != 0.0 and m.state()["angle_deg"] == m.angle * RAD2DEG
    assert len(m.hash_bytes()) == 40


# ---------------------------------------------------------------------------- 跨語言夾具


def _export_module():
    spec = importlib.util.spec_from_file_location("export_roll_reference", GOLDEN / "export_roll_reference.py")
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def test_golden_roll_run_is_reproducible() -> None:
    """積分案例(C# RollModelTests.GoldenRollRunMatchesPythonReference 以 1e-9 比對 φ、φ')須由目前的 Python 參考實作重現:YAML 一致、數值 1e-12、末狀態雜湊相同。"""
    mod = _export_module()
    case = mod.RUN_CASE
    expected_yaml = f"# 由 export_roll_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    assert (GOLDEN / "roll_run.yaml").read_text(encoding="utf-8") == expected_yaml
    lines = (GOLDEN / "roll_run.csv").read_text(encoding="utf-8").splitlines()
    hash_end = next(l.split(":", 1)[1].strip() for l in lines if l.startswith("# state_hash_end:"))
    body = [l for l in lines if not l.startswith("#")]
    cols = body[0].split(",")
    assert cols == mod.COLUMNS
    exp = [[float(v) for v in l.split(",")] for l in body[1:]]
    res = mod.run_case(case, write=False)
    got = [[float(v) for v in l.split(",")] for l in res["rows"]]
    assert len(got) == len(exp) == 301 and res["hash"] == hash_end
    for j, c in enumerate(cols):
        sc = max(1.0, max(abs(r[j]) for r in exp))
        worst = max(abs(a[j] - b[j]) for a, b in zip(got, exp))
        assert worst <= 1e-12 * sc, f"{c}: 最大差 {worst:.3e}"
    j = cols.index("roll_deg")
    assert 3.0 < max(abs(r[j]) for r in exp) < 30.0
    sc_loaded = load_scenario(GOLDEN / "roll_run.yaml")
    assert sc_loaded["ship"]["roll"]["enabled"] is True
