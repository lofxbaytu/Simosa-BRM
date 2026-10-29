"""平均波浪漂移力(情境 ``environment.waves``;docs/design/physics-waves.md;C# WaveDriftTests 的對應):

- ``external.WaveDriftModel``:方向對稱性(左右舷鏡像、迎浪/順浪 sway = 0)、量級(FSB1 Hs 2 m 橫浪十餘 t、與強風同量級、∝ Hs²、長波衰減、
  壓載較小)、遭遇頻率修正、浪譜加權、淺水波長(有限水深色散關係)、Bessel/Ursell 近似;
- ``scenario`` 的結構檢查與正規化(路徑與訊息同 C# Scenario.StructuralIssues)、``normalise_sections`` 帶出 waves;
- MMGShip:施力與漂移、``state_json`` 的 ``waves``、雜湊區塊、快照/還原、無 waves 時輸出與雜湊不變;
- 跨語言夾具 tests/SimCore.Tests/Golden/waves_force_table.csv(逐值相同)與 waves_drift_run.{yaml,csv}(export_waves_reference.py 產生)可重現。
"""

from __future__ import annotations

import csv
import importlib.util
import math
from pathlib import Path
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.external import (
    DEG2RAD,
    GRAVITY,
    TWO_PI,
    Kin,
    WaveDriftModel,
    bessel_i1,
    bessel_k1,
    normalise_sections,
    normalise_waves,
    peak_wavelength,
    ursell_reflection,
    wave_number,
)
from simosa_brm.mmg import make_ship
from simosa_brm.particulars import load_particulars
from simosa_brm.scenario import load_scenario, ship_from_scenario, structural_issues, validate_scenario

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"
TONNE = 9806.65


def _tree(waves: dict[str, Any] | None, speed: float = 0.0, telegraph: str = "STOP", depth: float = 30.0, loading: str = "full", **top: Any) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "seed": 7, "ship": {"id": "FSB1", "loading": loading},
        "origin": {"lat": 23.8, "lon": 120.15},
        "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": speed, "telegraph": telegraph},
        "environment": {"wind": {"trueSpeed": 0, "trueDir": 0, "gustiness": 0}, "current": {"set": 0, "drift": 0}, "waterDepth": depth},
    }
    if waves is not None:
        t["environment"]["waves"] = waves
    t.update(top)
    return t


BEAM = {"significantHeight_m": 2.0, "peakPeriod_s": 7.0, "direction_deg": 90}


def _model(hs: float = 2.0, tp: float = 7.0, direction: float = 90.0, spectrum: str | None = None, gamma: float | None = None,
           loading: str = "full", ship: str = "FSB1") -> WaveDriftModel:
    w: dict[str, Any] = {"significantHeight_m": hs, "peakPeriod_s": tp, "direction_deg": direction}
    if spectrum is not None:
        w["spectrum"] = spectrum
    if gamma is not None:
        w["gamma"] = gamma
    return WaveDriftModel(load_particulars(ship, loading).raw, loading, w)


def _at(psi_deg: float, u: float = 0.0, v: float = 0.0) -> Kin:
    return Kin(0.0, 0.0, psi_deg * DEG2RAD, u, v, 0.0)


def _steps_to(ship: Any, tick: int) -> None:
    while ship.state.tick < tick:
        ship.step()


# ---------------------------------------------------------------------------- 純函數


@pytest.mark.parametrize(("direction", "psi", "u", "v"), [(45.0, 0.0, 0.0, 0.0), (45.0, 0.0, 3.0, 0.2), (120.0, 30.0, 5.0, -0.4), (170.0, 200.0, -1.5, 0.0)])
def test_port_starboard_mirror_symmetry(direction: float, psi: float, u: float, v: float) -> None:
    a = _model(direction=direction).compute(_at(psi, u, v), 30.0)
    b = _model(direction=(2.0 * psi - direction) % 360.0).compute(_at(psi, u, -v), 30.0)
    assert a.X == pytest.approx(b.X, rel=1e-9, abs=1e-9)
    assert a.Y == pytest.approx(-b.Y, rel=1e-9, abs=1e-9)
    assert a.N == pytest.approx(-b.N, rel=1e-9, abs=1e-9)
    assert a.wavelength == b.wavelength


@pytest.mark.parametrize(("direction", "psi"), [(0.0, 0.0), (180.0, 0.0), (90.0, 90.0), (250.0, 70.0)])
def test_head_and_following_seas_give_no_sway_or_yaw(direction: float, psi: float) -> None:
    r = _model(direction=direction).compute(_at(psi, 2.0), 30.0)
    assert abs(r.Y) <= 1e-9 and abs(r.N) <= 1e-9 and r.X != 0.0
    rel = (direction - psi + 180.0) % 360.0 - 180.0
    assert (r.X < 0.0) if abs(rel) < 1.0 else (r.X > 0.0)


def test_beam_seas_magnitude_comparable_to_strong_wind_and_scaling() -> None:
    r = _model().compute(_at(0.0), 50.0)
    assert -25.0 < r.Y / TONNE < -10.0 and abs(r.X) < 1e-6 and abs(r.N) < 1e-6
    wind35 = 0.5 * 1.225 * (35.0 * 1852.0 / 3600.0) ** 2 * 574.87 * 0.7  # Blendermann 正橫風 35 kn ≈ 8 t
    assert 1.0 < abs(r.Y) / wind35 < 4.0
    half = _model(hs=1.0).compute(_at(0.0), 50.0)
    assert half.Y == pytest.approx(r.Y / 4.0, rel=1e-12)
    long_wave = _model(tp=12.0).compute(_at(0.0), 50.0)
    short_wave = _model(tp=4.0).compute(_at(0.0), 50.0)
    assert abs(long_wave.Y) < abs(r.Y) / 3.0 and 0.95 <= short_wave.factor <= 1.0 and 0.0 <= long_wave.factor <= 0.25
    ballast = _model(loading="ballast").compute(_at(0.0), 50.0)
    assert 0.3 * abs(r.Y) < abs(ballast.Y) < abs(r.Y)
    # 靜止 Hs = 0 → 零力但波長照算
    zero = _model(hs=0.0).compute(_at(0.0), 50.0)
    assert zero.X == 0.0 and zero.Y == 0.0 and zero.N == 0.0 and zero.wavelength == r.wavelength


def test_bow_quartering_turns_bow_away_and_speed_changes_encounter_factor() -> None:
    r = _model(direction=45.0).compute(_at(0.0), 50.0)
    assert r.Y < 0.0 and r.N < 0.0 and r.X < 0.0
    still = _model(direction=0.0).compute(_at(0.0), 50.0)
    ahead = _model(direction=0.0).compute(_at(0.0, 10.0 * 1852.0 / 3600.0), 50.0)
    following = _model(direction=180.0).compute(_at(0.0, 10.0 * 1852.0 / 3600.0), 50.0)
    assert 1.5 < ahead.factor / still.factor < 3.0 and 0.0 <= following.factor / still.factor < 0.4
    # 順浪快於波速:(1 + U_w/c) 夾到 0 → 力為 0
    fast = _model(direction=180.0, tp=3.0).compute(_at(0.0, 10.0), 50.0)
    assert fast.factor == 0.0 and fast.X == 0.0
    pm = _model(spectrum="pm").compute(_at(0.0), 50.0)
    js = _model(spectrum="jonswap", gamma=3.3).compute(_at(0.0), 50.0)
    js1 = _model(spectrum="jonswap", gamma=1.0).compute(_at(0.0), 50.0)
    assert pm.factor != js.factor and js1.factor == pytest.approx(pm.factor, rel=1e-12)
    m = _model(spectrum="jonswap")
    assert sum(m.weight) == pytest.approx(1.0, abs=1e-12) and max(m.weight) > max(_model().weight)  # JONSWAP 較尖


def test_shallow_water_wavelength_follows_finite_depth_dispersion() -> None:
    tp = 7.0
    omega = TWO_PI / tp
    lambda0 = TWO_PI * GRAVITY / (omega * omega)
    assert lambda0 * 0.999 < peak_wavelength(tp, 50.0) < lambda0  # 一律有限水深色散:h = 50 m 與深水差約 0.06 %,連續
    assert peak_wavelength(tp, math.inf) == peak_wavelength(tp, 0.0) == pytest.approx(lambda0, abs=1e-12)
    for h in (38.0, 20.0, 10.0, 5.0, 2.0):
        lam = peak_wavelength(tp, h)
        k = TWO_PI / lam
        assert lam < lambda0 and abs(GRAVITY * k * math.tanh(k * h) - omega * omega) < 1e-9
    assert wave_number(omega, 5.0) > wave_number(omega, 20.0) > wave_number(omega, 50.0)
    deep = _model().compute(_at(0.0), 50.0)
    shallow = _model().compute(_at(0.0), 8.0)
    assert deep.wavelength == pytest.approx(peak_wavelength(tp, 50.0), abs=1e-12) and shallow.wavelength == pytest.approx(peak_wavelength(tp, 8.0), abs=1e-12)
    assert shallow.factor > deep.factor


def test_bessel_and_ursell_approximations() -> None:
    assert bessel_i1(1.0) == pytest.approx(0.5651591, abs=1e-6) and bessel_k1(1.0) == pytest.approx(0.6019072, abs=1e-6)
    assert bessel_i1(5.0) == pytest.approx(24.33564, abs=1e-4) and bessel_k1(5.0) == pytest.approx(0.004044613, abs=1e-8)
    assert ursell_reflection(0.0) == 0.0 and ursell_reflection(-1.0) == 0.0
    prev = 0.0
    for x in (0.1, 0.3, 0.5, 0.7, 1.0, 1.5, 2.0, 3.0, 3.74, 3.76, 5.0, 10.0, 100.0, 1000.0):
        a = ursell_reflection(x)
        assert prev - 1e-6 <= a <= 1.0
        prev = a
    assert ursell_reflection(1000.0) == pytest.approx(1.0, abs=1e-12) and 0.89 < ursell_reflection(1.0) < 0.91


# ---------------------------------------------------------------------------- 情境


@pytest.mark.parametrize(
    ("waves", "path", "message"),
    [
        ({"significantHeight_m": -1, "peakPeriod_s": 7, "direction_deg": 90}, "environment.waves.significantHeight_m", "≥ 0"),
        ({"peakPeriod_s": 7, "direction_deg": 90}, "environment.waves.significantHeight_m", "significantHeight_m"),
        ({"significantHeight_m": "x", "peakPeriod_s": 7, "direction_deg": 90}, "environment.waves.significantHeight_m", "有限數值"),
        ({"significantHeight_m": 1, "peakPeriod_s": 0, "direction_deg": 90}, "environment.waves.peakPeriod_s", "必須為正"),
        ({"significantHeight_m": 1, "direction_deg": 90}, "environment.waves.peakPeriod_s", "必須為正"),
        ({"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 400}, "environment.waves.direction_deg", "0–360"),
        ({"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": -5}, "environment.waves.direction_deg", "0–360"),
        ({"significantHeight_m": 1, "peakPeriod_s": 7}, "environment.waves.direction_deg", "0–360"),
        ({"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 90, "spectrum": "bretschneider"}, "environment.waves.spectrum", "pm | jonswap"),
        ({"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 90, "gamma": 0.5}, "environment.waves.gamma", "≥ 1"),
        ("x", "environment.waves.peakPeriod_s", "必須為正"),
    ],
)
def test_structural_issues_reject_bad_waves(waves: Any, path: str, message: str) -> None:
    tree = _tree(waves)
    issues = structural_issues(tree)
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match="environment.waves"):
        validate_scenario(tree)


def test_valid_waves_normalise_with_defaults() -> None:
    assert structural_issues(_tree(BEAM)) == []
    assert structural_issues(_tree({"significantHeight_m": 0, "peakPeriod_s": 12.5, "direction_deg": 360, "spectrum": "JONSWAP", "gamma": 1})) == []
    sc = validate_scenario(_tree({"significantHeight_m": 1.2, "peakPeriod_s": 6, "direction_deg": 360, "spectrum": "JONSWAP"}))
    expect = {"significantHeight_m": 1.2, "peakPeriod_s": 6.0, "direction_deg": 360.0, "spectrum": "jonswap", "gamma": 3.3}
    assert sc["environment"]["waves"] == expect and sc["external"]["waves"] == expect
    assert validate_scenario(_tree(None))["environment"]["waves"] is None
    assert normalise_waves(None) is None and normalise_sections({})["waves"] is None
    m = WaveDriftModel(load_particulars("FSB1", "full").raw, "full", expect)
    assert m.spectrum == "jonswap" and m.gamma == 3.3 and m.direction_deg == 0.0 and m.state()["spectrum"] == "jonswap"
    assert not WaveDriftModel(load_particulars("FSB1", "full").raw, "full", expect, supported=False).active


# ---------------------------------------------------------------------------- MMGShip


def test_ship_drifts_in_beam_seas_and_state_reports_waves() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(BEAM)))
    assert ship.waves is not None and ship.waves.active
    s0 = ship.state_json()["waves"]
    assert (s0["significantHeight_m"], s0["peakPeriod_s"], s0["direction_deg"], s0["spectrum"]) == (2.0, 7.0, 90.0, "pm")
    assert s0["wavelength_m"] == pytest.approx(peak_wavelength(7.0, 30.0), abs=1e-12) and s0["Y_kN"] < -100.0
    assert set(s0) == {"significantHeight_m", "peakPeriod_s", "direction_deg", "wavelength_m", "spectrum", "X_kN", "Y_kN", "N_kNm"}
    _steps_to(ship, 1500)  # 30 s
    assert ship.state.v < -0.05 and ship.state.x < -1.0 and ship.state_json()["waves"]["Y_kN"] < 0.0
    calm = ship_from_scenario(validate_scenario(_tree(None)))
    _steps_to(calm, 1500)
    assert abs(calm.state.x) < 1e-6 and abs(calm.state.v) < 1e-9 and "waves" not in calm.state_json()


def test_without_waves_outputs_and_hash_are_unchanged_and_zero_hs_moves_identically() -> None:
    sc = validate_scenario(_tree(None, speed=6.0, telegraph="HAH"))
    ship = ship_from_scenario(sc)
    assert ship.waves is None
    ref = make_ship("FSB1", "full", seed=sc["seed"])
    ref.set_environment(wind_speed_kn=0.0, water_depth_m=30.0)
    ref.reset(0.0, 0.0, 0.0, 6.0, rpm=float(ref.eng["telegraph"]["HAH"]), telegraph="HAH")
    ref.set_origin(23.8, 120.15)
    _steps_to(ship, 250)
    _steps_to(ref, 250)
    assert ship.state_hash() == ref.state_hash() and "waves" not in ship.state_json()
    # Hs = 0:力恰為 0,運動逐位元相同;state.waves 仍輸出零力;雜湊因追加區塊而不同
    zero = ship_from_scenario(validate_scenario(_tree({"significantHeight_m": 0, "peakPeriod_s": 7, "direction_deg": 90}, speed=6.0, telegraph="HAH")))
    _steps_to(zero, 250)
    assert (zero.state.u, zero.state.v, zero.state.r, zero.state.x, zero.state.y, zero.state.psi) == (ship.state.u, ship.state.v, ship.state.r, ship.state.x, ship.state.y, ship.state.psi)
    w = zero.state_json()["waves"]
    assert w["X_kN"] == 0.0 and w["Y_kN"] == 0.0 and w["significantHeight_m"] == 0.0
    assert zero.state_hash() != ship.state_hash()
    assert len(zero.waves.hash_bytes()) == 8 * WaveDriftModel.HASH_LONGS


def test_snapshot_restore_and_reset_rebuild_waves() -> None:
    ship = ship_from_scenario(validate_scenario(_tree({"significantHeight_m": 2.5, "peakPeriod_s": 8.0, "direction_deg": 135, "spectrum": "jonswap"},
                                                      speed=6.0, telegraph="HAH", depth=15.0)))
    _steps_to(ship, 500)
    snap = ship.snapshot()
    _steps_to(ship, 1000)
    h, last = ship.state_hash(), ship.waves.last
    ship.restore(snap)
    assert ship.state.tick == 500 and ship.waves.last == ship.waves.compute(ship._kin(), ship._water_depth_or_inf())
    _steps_to(ship, 1000)
    assert ship.state_hash() == h and ship.waves.last == last
    ship.reset(0.0, 0.0, 0.0, 6.0)
    assert ship.waves is not None and ship.state.tick == 0 and ship.waves.last.Y != 0.0
    ship.configure_external(None)
    assert ship.waves is None and "waves" not in ship.state_json()


# ---------------------------------------------------------------------------- 跨語言夾具


def _export_module():
    spec = importlib.util.spec_from_file_location("export_waves_reference", GOLDEN / "export_waves_reference.py")
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def test_golden_force_table_is_reproducible() -> None:
    """純函數取樣表(C# WaveDriftTests.GoldenForceTableMatchesPythonReference 以相對 1e-12 比對)以同一路徑重算須逐值相同。"""
    text = (GOLDEN / "waves_force_table.csv").read_text(encoding="utf-8").splitlines()
    rows = list(csv.DictReader(l for l in text if not l.startswith("#")))
    assert len(rows) >= 100
    mod = _export_module()
    got = mod.force_table_rows()
    assert len(got) == len(rows)
    for r, g in zip(rows, got):
        assert (r["ship"], r["loading"], r["spectrum"]) == (g[0], g[1], g[5])
        for key, val in (("wavelength_m", g[11]), ("factor", g[12]), ("X_N", g[13]), ("Y_N", g[14]), ("N_Nm", g[15])):
            assert float(r[key]) == val, (r, key)


def test_golden_drift_run_is_reproducible() -> None:
    """積分案例(C# 以 < 1% 比對軌跡與漂移力)須由目前的 Python 參考實作重現:YAML 一致、數值相對 1e-9、末狀態雜湊相同。"""
    mod = _export_module()
    case = mod.RUN_CASE
    expected_yaml = f"# 由 export_waves_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    assert (GOLDEN / "waves_drift_run.yaml").read_text(encoding="utf-8") == expected_yaml
    lines = (GOLDEN / "waves_drift_run.csv").read_text(encoding="utf-8").splitlines()
    hash_end = next(l.split(":", 1)[1].strip() for l in lines if l.startswith("# state_hash_end:"))
    body = [l for l in lines if not l.startswith("#")]
    cols = body[0].split(",")
    exp = [[float(v) for v in l.split(",")] for l in body[1:]]
    res = mod.run_case(case, write=False)
    got = [[float(v) for v in l.split(",")] for l in res["rows"]]
    assert len(got) == len(exp) == 301
    for j, c in enumerate(cols):
        sc = max(1.0, max(abs(r[j]) for r in exp))
        worst = max(abs(a[j] - b[j]) for a, b in zip(got, exp))
        assert worst <= 1e-9 * sc, f"{c}: 最大差 {worst:.3e}"
    if got == exp:
        assert res["hash"] == hash_end
    sc_loaded = load_scenario(GOLDEN / "waves_drift_run.yaml")
    assert sc_loaded["environment"]["waves"]["spectrum"] == "jonswap" and sc_loaded["environment"]["waterDepth"] == 20.0


# ---------------------------------------------------------------------------- 練習中變更(setEnvironment waves;C# WaveEnvironmentCommandTests)


def _timeline_tree(payload: Any) -> dict[str, Any]:
    return _tree(None, timeline=[{"id": "it", "when": {"type": "time", "atLeast": 1}, "actions": [{"setEnvironment": payload}]}])


def test_set_environment_waves_matches_scenario_waves_bit_identically() -> None:
    """無浪情境 + set_environment(waves=…) 在 tick 0 設定與情境相同的浪 → 運動與雜湊逐位元相同(模型以同一設定建立;C# CommandAddsWaves…)。"""
    by_scenario = ship_from_scenario(validate_scenario(_tree(BEAM, speed=6.0, telegraph="HAH")))
    by_command = ship_from_scenario(validate_scenario(_tree(None, speed=6.0, telegraph="HAH")))
    assert by_command.waves is None
    by_command.set_environment(waves={"significantHeight_m": 2.0, "peakPeriod_s": 7.0, "direction_deg": 90})
    assert by_command.waves is not None and by_command.waves.active
    _steps_to(by_scenario, 500)
    _steps_to(by_command, 500)
    assert by_command.state_hash() == by_scenario.state_hash()
    s = by_command.state_json()["waves"]
    assert (s["significantHeight_m"], s["direction_deg"]) == (2.0, 90.0) and s["Y_kN"] < -50.0


def test_set_waves_partial_update_off_and_invalid() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(BEAM, speed=6.0, telegraph="HAH")))
    # 只改來向:Hs/Tp 沿用;浪譜與 γ 大小寫不拘
    ship.set_waves({"direction_deg": 270})
    assert (ship.waves.hs, ship.waves.tp, ship.waves.direction_deg) == (2.0, 7.0, 270.0)
    ship.set_waves({"spectrum": "JONSWAP", "gamma": 2.0})
    assert (ship.waves.spectrum, ship.waves.gamma, ship.waves.direction_deg) == ("jonswap", 2.0, 270.0)
    # 來向 360 → 0(C# 以正規化後的來向重建模型)
    ship.set_waves({"direction_deg": 360})
    assert ship.waves.direction_deg == 0.0 and ship.waves.direction == 0.0
    _steps_to(ship, 100)
    h_waves = ship.state_hash()
    # Hs = 0 → 關閉:state 不輸出、雜湊不再追加區塊
    ship.set_waves({"significantHeight_m": 0})
    assert ship.waves is None and "waves" not in ship.state_json()
    ship.step()
    assert ship.state_hash() != h_waves
    # 無浪時只給 Hs → 不合法(缺 Tp/來向),狀態不變
    with pytest.raises(ValueError, match="invalidWaves"):
        ship.set_waves({"significantHeight_m": 1.5})
    assert ship.waves is None
    for bad in ({"significantHeight_m": -1, "peakPeriod_s": 7, "direction_deg": 90}, {"significantHeight_m": 1, "peakPeriod_s": 0, "direction_deg": 90},
                {"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 90, "spectrum": "bretschneider"},
                {"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 90, "gamma": 0.5},
                {"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 90, "spectrum": 3}, "rough", [1, 7, 90],
                # 存在但非數值(None/布林)→ 拒絕(C# ParseWavesArg 同語意)
                {"significantHeight_m": None, "peakPeriod_s": 7, "direction_deg": 90}, {"significantHeight_m": True, "peakPeriod_s": 7, "direction_deg": 90},
                {"significantHeight_m": 1, "peakPeriod_s": 7, "direction_deg": 90, "gamma": None}):
        with pytest.raises(ValueError, match="invalidWaves"):
            ship.set_waves(bad)
        assert ship.waves is None
    # 完整設定再以 None 關閉;set_environment(waves="keep") 不變
    ship.set_waves({"significantHeight_m": 1.5, "peakPeriod_s": 6, "direction_deg": 45})
    assert ship.waves is not None and ship.waves.hs == 1.5
    ship.set_environment(wind_speed_kn=5.0)
    assert ship.waves is not None and ship.waves.hs == 1.5
    ship.set_environment(waves=None)
    assert ship.waves is None


def test_set_waves_snapshot_restore_and_reset() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(BEAM, speed=6.0, telegraph="HAH")))
    _steps_to(ship, 250)
    snap_before = ship.snapshot()
    ship.set_waves({"significantHeight_m": 3.5, "peakPeriod_s": 8.0, "direction_deg": 135, "spectrum": "jonswap"})
    _steps_to(ship, 750)
    snap_mid = ship.snapshot()
    _steps_to(ship, 2000)
    h, last = ship.state_hash(), ship.waves.last
    ship.restore(snap_mid)
    assert ship.state.tick == 750 and ship.waves.hs == 3.5 and ship.waves.spectrum == "jonswap"
    _steps_to(ship, 2000)
    assert ship.state_hash() == h and ship.waves.last == last
    # 倒帶到變更前:恢復情境浪
    ship.restore(snap_before)
    assert (ship.waves.hs, ship.waves.direction_deg, ship.waves.spectrum) == (2.0, 90.0, "pm")
    # 關閉後 reset 回到情境浪
    ship.set_waves(None)
    assert ship.waves is None
    ship.reset(0.0, 0.0, 0.0, 6.0)
    assert ship.waves is not None and (ship.waves.hs, ship.waves.direction_deg) == (2.0, 90.0)


def test_server_set_environment_waves_ack_and_rejection() -> None:
    from simosa_brm.server import SimServer

    ship = ship_from_scenario(validate_scenario(_tree(None, speed=6.0, telegraph="HAH")))
    srv = SimServer(ship, 1.0)
    ack = srv.apply_command({"type": "setEnvironment", "args": {"waves": {"significantHeight_m": 2, "peakPeriod_s": 7, "direction_deg": 90}, "ramp_s": 60}})
    assert ack["ok"] and "浪 Hs 2 m" in ack["detail"] and "立即" in ack["detail"]
    assert ship.waves is not None and ship.waves.hs == 2.0
    # 不合法:其他欄位已套用、只拒絕浪(C# commandRejected invalidWaves)
    bad = srv.apply_command({"type": "setEnvironment", "args": {"wind": {"trueSpeed": 12, "trueDir": 45}, "waves": {"peakPeriod_s": 0}}})
    assert not bad["ok"] and "invalidWaves" in bad["detail"]
    assert ship.env.wind_speed == pytest.approx(12.0 * 0.514444, rel=1e-6) and ship.waves is not None and ship.waves.tp == 7.0
    # 部分更新與關閉
    assert srv.apply_command({"type": "setEnvironment", "args": {"waves": {"direction_deg": 180}}})["ok"] and ship.waves.direction_deg == 180.0
    off = srv.apply_command({"type": "setEnvironment", "args": {"waves": None}})
    assert off["ok"] and "浪關閉" in off["detail"] and ship.waves is None and "waves" not in ship.state_json()
    assert srv.apply_command({"type": "setEnvironment", "args": {"waves": {"significantHeight_m": 0, "peakPeriod_s": 5, "direction_deg": 0}}})["ok"] and ship.waves is None


@pytest.mark.parametrize(
    ("payload", "path", "message"),
    [
        ({"waves": {"significantHeight_m": -1}}, "timeline[0].actions[0].setEnvironment.waves.significantHeight_m", "≥ 0"),
        ({"waves": {"peakPeriod_s": 0}}, "timeline[0].actions[0].setEnvironment.waves.peakPeriod_s", "必須為正"),
        ({"waves": {"direction_deg": 361}}, "timeline[0].actions[0].setEnvironment.waves.direction_deg", "0–360"),
        ({"waves": {"spectrum": "bretschneider"}}, "timeline[0].actions[0].setEnvironment.waves.spectrum", "pm | jonswap"),
        ({"waves": {"gamma": 0.5}}, "timeline[0].actions[0].setEnvironment.waves.gamma", "≥ 1"),
        ({"waves": "rough"}, "timeline[0].actions[0].setEnvironment.waves", "物件或 null"),
        ("rough", "timeline[0].actions[0]", "setEnvironment 須為物件"),
    ],
)
def test_timeline_set_environment_waves_structural_issues(payload: Any, path: str, message: str) -> None:
    """時間表 setEnvironment waves 的結構檢查:路徑與訊息同 C# Scenario.CheckSetEnvironmentAction(WaveEnvironmentCommandTests.StructuralIssuesRejectBadTimelineWaves)。"""
    issues = structural_issues(_timeline_tree(payload))
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match=message):
        validate_scenario(_timeline_tree(payload))
    # 合法形狀(欄位皆選填、null 關閉)無錯誤
    for good in ({"waves": {"significantHeight_m": 0, "peakPeriod_s": 5, "direction_deg": 360, "spectrum": "JONSWAP", "gamma": 1}, "ramp_s": 10},
                 {"waves": None}, {"waves": {"direction_deg": 90}}, {"visibility_nm": 2}):
        assert structural_issues(_timeline_tree(good)) == []
