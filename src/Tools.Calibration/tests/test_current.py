"""時變流場(情境 ``environment.current.table`` | ``tidal``;docs/design/scenario-engine.md 第 1.5.2 節;C# TidalStreamTests 的對應):

- ``tide.TideTable/HarmonicTide`` 的 ``rate_at``/``max_rate``/``reference_period_s``;``current.CurrentTable``(向量內插、方向跨 360°)、
  ``TidalCurrent``(漲/退/平潮門檻);``scenario.normalise_current``(utc → 相對 startTimeUtc 的秒數、slackWindow_min 預設);
- ``structural_issues`` 的流場檢查(路徑與訊息同 C# Scenario.StructuralIssues);
- MMGShip:每步以 tick·dt 的模型值覆寫流向/流速、``state_json`` 的 ``currentSource``、``set_environment`` 給 current 後時變停止、
  ``reset``/``apply_scenario`` 恢復、常數流情境輸出與雜湊不變;
- 跨語言夾具 tests/SimCore.Tests/Golden/current_*.csv(export_current_reference.py 產生)可重現(確定性);E06b 潮流情境載入與漲 → 平潮 → 退。
"""

from __future__ import annotations

import csv
import math
from pathlib import Path
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.current import KN_TO_MPS, CurrentTable, TidalCurrent, current_from_scenario
from simosa_brm.mmg import make_ship
from simosa_brm.scenario import load_scenario, normalise_current, ship_from_scenario, structural_issues, validate_scenario
from simosa_brm.tide import DEG, M2_PERIOD_S, HarmonicTide, TideTable

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"


def _tree(current: dict[str, Any] | None, tide: dict[str, Any] | None = None, **top: Any) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "ship": {"id": "FSB1", "loading": "ballast"},
        "origin": {"lat": 23.8, "lon": 120.05},
        "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": 8, "telegraph": "HAH"},
        "environment": {"wind": {"trueSpeed": 0, "trueDir": 0, "gustiness": 0}, "current": {"set": 350, "drift": 1}, "waterDepth": 30},
    }
    if current is not None:
        t["environment"]["current"].update(current)
    if tide is not None:
        t["environment"]["tide"] = tide
    t.update(top)
    return t


TABLE = {"table": [{"t": 0, "speed_kn": 1, "direction_deg": 350}, {"t": 10, "speed_kn": 1, "direction_deg": 20}, {"t": 30, "speed_kn": 2, "direction_deg": 90}]}
HARMONIC = {"harmonic": {"meanLevel_m": 1, "constituents": [{"name": "T1", "amplitude_m": 1, "period_s": 120, "phase_deg": -60}]}}
TIDAL = {"tidal": {"flood": {"speed_kn": 2.5, "direction_deg": 85}, "ebb": {"speed_kn": 3.0, "direction_deg": 265}, "slackWindow_min": 0.1}}


def _deg(rad: float) -> float:
    return math.degrees(rad) % 360.0


# ---------------------------------------------------------------- 模型


def test_tide_rate_is_piecewise_for_tables_and_analytic_for_harmonics() -> None:
    t = TideTable(((0.0, 1.0), (10.0, 2.0), (30.0, 1.0), (40.0, 1.0)))
    assert t.rate_at(0.0) == pytest.approx(0.1) and t.rate_at(5.0) == pytest.approx(0.1) and t.rate_at(10.0) == pytest.approx(0.1)
    assert t.rate_at(20.0) == pytest.approx(-0.05) and t.rate_at(35.0) == 0.0 and t.rate_at(-1.0) == 0.0 and t.rate_at(41.0) == 0.0
    assert t.max_rate == pytest.approx(0.1) and t.reference_period_s == M2_PERIOD_S
    h = HarmonicTide(0.5, ((1.0, 100.0, math.pi / 2), (0.25, 50.0, 0.0)))
    for x in (0.0, 7.3, 25.0, 61.0):
        fd = (h.height_at(x + 1e-4) - h.height_at(x - 1e-4)) / 2e-4
        assert h.rate_at(x) == pytest.approx(fd, abs=1e-6)
    assert h.max_rate == pytest.approx(2 * math.pi / 100 + 0.25 * 2 * math.pi / 50) and h.reference_period_s == 100.0
    assert HarmonicTide(0.0, ()).reference_period_s == M2_PERIOD_S


def test_current_table_interpolates_vectors_across_360() -> None:
    sc = validate_scenario(_tree(TABLE))
    m = current_from_scenario(sc["environment"]["current"], None)
    assert isinstance(m, CurrentTable) and m.source == "table"
    set5, drift5 = m.at(5.0)
    assert _deg(set5) == pytest.approx(5.0, abs=1e-9) and drift5 == pytest.approx(KN_TO_MPS * math.cos(15 * DEG), abs=1e-12)
    set0, drift0 = m.at(-1.0)
    assert _deg(set0) == pytest.approx(350.0, abs=1e-9) and drift0 == pytest.approx(KN_TO_MPS, abs=1e-12)
    assert m.at(1e6)[1] == pytest.approx(2 * KN_TO_MPS, abs=1e-12)
    with pytest.raises(ValueError):
        CurrentTable(())
    assert current_from_scenario(None, None) is None and current_from_scenario({"set": 0, "drift": 0, "table": None, "tidal": None}, None) is None


def test_tidal_current_follows_tide_rate_with_slack_window() -> None:
    tide = HarmonicTide(1.0, ((1.0, 120.0, -60 * DEG),))  # 高潮 t = 20、低潮 t = 80
    m = TidalCurrent(tide, 85 * DEG, 2.5 * KN_TO_MPS, 265 * DEG, 3.0 * KN_TO_MPS, 6.0)
    assert m.source == "tidal" and m.slack_threshold == pytest.approx(math.sin(math.pi * 6 / 120), abs=1e-12)
    assert _deg(m.at(0)[0]) == pytest.approx(85.0) and 0 < m.at(0)[1] < 2.5 * KN_TO_MPS
    assert m.at(110)[1] == pytest.approx(2.5 * KN_TO_MPS, abs=1e-9)
    assert m.at(19.0)[1] == 0.0 and _deg(m.at(19.0)[0]) == pytest.approx(85.0)
    assert m.at(21.0)[1] == 0.0 and _deg(m.at(21.0)[0]) == pytest.approx(265.0)
    assert m.at(27.0)[1] > 0.0
    assert m.at(50)[1] == pytest.approx(3.0 * KN_TO_MPS, abs=1e-9) and _deg(m.at(50)[0]) == pytest.approx(265.0)
    scale, flood = m.scale_at(10.0)
    ratio = abs(tide.rate_at(10.0)) / tide.max_rate
    assert flood and scale == pytest.approx((ratio - m.slack_threshold) / (1 - m.slack_threshold), abs=1e-12)
    assert TidalCurrent.threshold(0.0, 120.0) == 0.0 and TidalCurrent.threshold(60.0, 120.0) == 1.0
    assert TidalCurrent(tide, 0, 1, 0, 1, 3600.0).at(50)[1] == 0.0
    with pytest.raises(ValueError, match="需要 environment.tide"):
        current_from_scenario({"tidal": TIDAL["tidal"]}, None)


def test_normalise_current_shape_and_utc() -> None:
    raw = {"table": [{"utc": "2026-12-08T04:55:00Z", "speed_kn": 0, "direction_deg": 0}, {"t": 0, "speed_kn": 1, "direction_deg": 350},
                     {"utc": "2026-12-08T05:00:10.5Z", "speed_kn": 2, "direction_deg": 20}]}
    sc = validate_scenario(_tree(raw, startTimeUtc="2026-12-08T05:00:00Z"))
    assert sc["environment"]["current"] == {"set": 350.0, "drift": 1.0, "tidal": None, "table": [
        {"t": -300.0, "speed_kn": 0.0, "direction_deg": 0.0}, {"t": 0.0, "speed_kn": 1.0, "direction_deg": 350.0}, {"t": 10.5, "speed_kn": 2.0, "direction_deg": 20.0}]}
    assert validate_scenario(_tree(None))["environment"]["current"] == {"set": 350.0, "drift": 1.0, "table": None, "tidal": None}
    td = validate_scenario(_tree({"tidal": {"flood": {"speed_kn": 1, "direction_deg": 10}, "ebb": {"speed_kn": 2, "direction_deg": 190}}}, HARMONIC))["environment"]["current"]["tidal"]
    assert td == {"flood": {"speed_kn": 1.0, "direction_deg": 10.0}, "ebb": {"speed_kn": 2.0, "direction_deg": 190.0}, "slackWindow_min": 20.0}
    assert normalise_current(None, None) == {"set": 0.0, "drift": 0.0, "table": None, "tidal": None}


@pytest.mark.parametrize(
    ("current", "tide", "start", "path", "message"),
    [
        ({"table": [{"t": 0, "speed_kn": 1, "direction_deg": 0}]}, None, None, "environment.current.table", "至少需要 2 點"),
        ({"table": [{"t": 0, "speed_kn": 1, "direction_deg": 0}, {"t": 10, "speed_kn": 1, "direction_deg": 0}, {"t": 10, "speed_kn": 1, "direction_deg": 0}]}, None, None, "environment.current.table[2]", "嚴格遞增"),
        ({"table": [{"t": 0, "speed_kn": 1, "direction_deg": 0}, {"speed_kn": 1, "direction_deg": 0}]}, None, None, "environment.current.table[1]", "需要 t 或 utc"),
        ({"table": [{"t": 0, "utc": "2026-12-08T05:00:00Z", "speed_kn": 1, "direction_deg": 0}, {"t": 10, "speed_kn": 1, "direction_deg": 0}]}, None, "2026-12-08T05:00:00Z", "environment.current.table[0]", "只能擇一"),
        ({"table": [{"utc": "2026-12-08T05:00:00Z", "speed_kn": 1, "direction_deg": 0}, {"utc": "2026-12-08T05:10:00Z", "speed_kn": 1, "direction_deg": 0}]}, None, None, "environment.current.table[0]", "startTimeUtc"),
        ({"table": [{"utc": "nope", "speed_kn": 1, "direction_deg": 0}, {"t": 10, "speed_kn": 1, "direction_deg": 0}]}, None, "2026-12-08T05:00:00Z", "environment.current.table[0]", "utc 無法解析"),
        ({"table": [{"t": 0, "speed_kn": -1, "direction_deg": 0}, {"t": 10, "speed_kn": 1, "direction_deg": 0}]}, None, None, "environment.current.table[0]", "speed_kn 不可為負"),
        ({"table": [{"t": 0, "speed_kn": 1, "direction_deg": "x"}, {"t": 10, "speed_kn": 1, "direction_deg": 0}]}, None, None, "environment.current.table[0]", "direction_deg 必須為有限數值"),
        ({"tidal": {"flood": {"speed_kn": 1, "direction_deg": 0}, "ebb": {"speed_kn": 1, "direction_deg": 180}}}, None, None, "environment.current.tidal", "需要 environment.tide"),
        ({"tidal": {"flood": {"speed_kn": 1, "direction_deg": 0}}}, HARMONIC, None, "environment.current.tidal.ebb", "需要 ebb"),
        ({"tidal": {"flood": {"speed_kn": -1, "direction_deg": 0}, "ebb": {"speed_kn": 1, "direction_deg": 180}}}, HARMONIC, None, "environment.current.tidal.flood", "speed_kn 不可為負"),
        ({"tidal": {"flood": {"speed_kn": 1, "direction_deg": 0}, "ebb": {"speed_kn": 1, "direction_deg": 180}, "slackWindow_min": -5}}, HARMONIC, None, "environment.current.tidal.slackWindow_min", "不可為負"),
        ({"table": [{"t": 0, "speed_kn": 1, "direction_deg": 0}, {"t": 10, "speed_kn": 1, "direction_deg": 0}],
          "tidal": {"flood": {"speed_kn": 1, "direction_deg": 0}, "ebb": {"speed_kn": 1, "direction_deg": 180}}}, HARMONIC, None, "environment.current", "只能擇一"),
    ],
)
def test_structural_issues_reject_bad_current(current: Any, tide: Any, start: str | None, path: str, message: str) -> None:
    tree = _tree(current, tide, **({"startTimeUtc": start} if start else {}))
    issues = structural_issues(tree)
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match="environment.current"):
        validate_scenario(tree)


def test_valid_current_has_no_structural_issues() -> None:
    assert structural_issues(_tree(TABLE)) == []
    assert structural_issues(_tree(TIDAL, HARMONIC)) == []
    assert structural_issues(_tree(None)) == []
    assert structural_issues(_tree({"table": [{"utc": "2026-12-08T05:00:00Z", "speed_kn": 1, "direction_deg": 0}, {"t": 20, "speed_kn": 2, "direction_deg": 0}]},
                                   startTimeUtc="2026-12-08T05:00:00Z")) == []


# ---------------------------------------------------------------- MMGShip


def _steps_to(ship: Any, tick: int) -> None:
    while ship.state.tick < tick:
        ship.step()


def test_ship_refreshes_current_every_step_and_outputs_source() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(TABLE)))
    assert ship.current_model is not None and ship.current_model_active and ship.current_source == "table"
    s0 = ship.state_json()
    assert s0["currentSource"] == "table" and s0["current"]["set"] == pytest.approx(350.0, abs=1e-9) and s0["current"]["drift"] == pytest.approx(1.0, abs=1e-9)
    _steps_to(ship, 250)  # t = 5 s
    s5 = ship.state_json()
    assert s5["current"]["set"] == pytest.approx(5.0, abs=1e-9) and s5["current"]["drift"] == pytest.approx(math.cos(15 * DEG), abs=1e-9)
    set_rad, drift = ship.current_model.at(ship.state.tick * ship.dt)
    assert (ship.env.current_set, ship.env.current_speed) == (set_rad, drift)
    e, n = ship.env.current_en
    assert e == pytest.approx(drift * math.sin(set_rad), abs=1e-12) and n == pytest.approx(drift * math.cos(set_rad), abs=1e-12)
    _steps_to(ship, 2000)  # t = 40 s ≥ 表末點
    assert ship.state_json()["current"] == {"set": pytest.approx(90.0, abs=1e-9), "drift": pytest.approx(2.0, abs=1e-9)}


def test_constant_current_scenarios_are_unchanged() -> None:
    sc = validate_scenario(_tree(None))
    ship = ship_from_scenario(sc)
    assert ship.current_model is None and ship.current_source is None
    ref = make_ship("FSB1", "ballast", seed=sc["seed"])
    ref.set_environment(wind_speed_kn=0.0, current_set_deg=350.0, current_drift_kn=1.0, water_depth_m=30.0)
    ref.reset(0.0, 0.0, 0.0, 8.0, rpm=float(ref.eng["telegraph"]["HAH"]), telegraph="HAH")
    ref.set_origin(23.8, 120.05)
    _steps_to(ship, 150)
    _steps_to(ref, 150)
    s, r = ship.state_json(), ref.state_json()
    assert "currentSource" not in s and s["current"] == r["current"]
    assert (s["u"], s["v"], s["r"], s["pos"]["x"], s["pos"]["y"]) == (r["u"], r["v"], r["r"], r["pos"]["x"], r["pos"]["y"])
    assert ship.state_hash() == ref.state_hash()
    e06 = ship_from_scenario(load_scenario("E06_taichung_channel_flood"))
    assert e06.current_model is None and "currentSource" not in e06.state_json()


def test_set_environment_current_stops_the_model_and_reset_restores_it() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(TABLE)))
    _steps_to(ship, 500)
    ship.set_environment(wind_speed_kn=10.0, wind_dir_deg=90.0)  # 只改風:時變不受影響
    assert ship.current_model_active and ship.state_json()["currentSource"] == "table"
    ship.set_environment(current_set_deg=100.0, current_drift_kn=0.5)
    assert ship.current_overridden and not ship.current_model_active and "currentSource" not in ship.state_json()
    _steps_to(ship, 1000)
    assert ship.state_json()["current"] == {"set": pytest.approx(100.0, abs=1e-9), "drift": pytest.approx(0.5, abs=1e-9)}
    ship.reset(0.0, 0.0, 0.0, 8.0)
    assert not ship.current_overridden and ship.state_json()["currentSource"] == "table"
    assert ship.state_json()["current"]["set"] == pytest.approx(350.0, abs=1e-9)
    ship.set_current_model(None)
    assert ship.current_model is None and "currentSource" not in ship.state_json()


def test_set_environment_current_override_is_in_snapshot_and_rewind_restores_model() -> None:
    """C# TidalStreamTests.SetEnvironmentCurrentStopsTheModelAndRewindRestoresIt 的對應:覆寫旗標進快照,倒帶到覆寫前即恢復時變。"""
    ship = ship_from_scenario(validate_scenario(_tree(TABLE)))
    _steps_to(ship, 250)
    snap = ship.snapshot()
    before = ship.state_json()
    ship.set_environment(current_set_deg=180.0, current_drift_kn=0.7)
    _steps_to(ship, 500)
    assert ship.current_overridden and "currentSource" not in ship.state_json()
    ship.restore(snap)
    assert not ship.current_overridden and ship.current_model_active
    s = ship.state_json()
    assert s["currentSource"] == "table" and s["current"] == before["current"]
    _steps_to(ship, 300)
    assert ship.state_json()["currentSource"] == "table" and ship.state_json()["current"] != before["current"]
    # 覆寫後的快照:還原後仍為覆寫(流固定、無 currentSource)
    ship.set_environment(current_set_deg=180.0, current_drift_kn=0.7)
    snap2 = ship.snapshot()
    ship.reset(0.0, 0.0, 0.0, 8.0)
    assert not ship.current_overridden
    ship.restore(snap2)
    assert ship.current_overridden and "currentSource" not in ship.state_json()
    assert ship.state_json()["current"] == {"set": pytest.approx(180.0, abs=1e-9), "drift": pytest.approx(0.7, abs=1e-9)}


def test_tidal_current_in_ship_turns_with_tide() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(TIDAL, HARMONIC)))
    assert isinstance(ship.current_model, TidalCurrent) and ship.state_json()["currentSource"] == "tidal"
    assert ship.state_json()["current"]["set"] == pytest.approx(85.0)
    _steps_to(ship, 50 * 50)  # t = 50 s:中退
    s = ship.state_json()
    assert s["current"]["set"] == pytest.approx(265.0, abs=1e-9) and s["current"]["drift"] == pytest.approx(3.0, abs=1e-6)


@pytest.mark.parametrize("name", ["current_table", "current_tidal"])
def test_golden_current_reference_is_reproducible(name: str) -> None:
    """export_current_reference.py 的夾具(C# TidalStreamTests 以 1e-9 比對)以同一路徑重算必須完全一致。"""
    text = (GOLDEN / f"{name}.csv").read_text(encoding="utf-8").splitlines()
    assert text[1] == f"# scenario: {name}.yaml"
    rows = list(csv.DictReader(l for l in text if not l.startswith("#")))
    assert len(rows) >= 10
    ship = ship_from_scenario(load_scenario(GOLDEN / f"{name}.yaml"))
    for r in rows:
        _steps_to(ship, int(r["tick"]))
        st = ship.state_json()
        e, n = ship.env.current_en
        assert st["t"] == pytest.approx(float(r["t"]), abs=1e-9)
        assert st["currentSource"] == r["currentSource"]
        assert st["current"]["set"] == float(r["set_deg"]) and st["current"]["drift"] == float(r["drift_kn"]), (name, r["tick"])
        assert e == float(r["east_mps"]) and n == float(r["north_mps"]), (name, r["tick"])
    if name == "current_tidal":
        assert [float(r["set_deg"]) for r in rows if int(r["tick"]) >= 1275] and all(float(r["set_deg"]) == 265.0 for r in rows if int(r["tick"]) >= 1275)


# ---------------------------------------------------------------- E06b


def test_e06b_tidal_stream_scenario_loads_and_turns_flood_to_ebb() -> None:
    f, b = load_scenario("E06_taichung_channel_flood"), load_scenario("E06b_tidal_stream")
    assert f["environment"]["current"]["tidal"] is None and f["environment"]["tide"] is None
    assert b["environment"]["current"]["tidal"] == {"flood": {"speed_kn": 2.5, "direction_deg": 85.0}, "ebb": {"speed_kn": 3.0, "direction_deg": 265.0}, "slackWindow_min": 20.0}
    assert b["route"]["waypoints"] == f["route"]["waypoints"] and [z["id"] for z in b["zones"]] == [z["id"] for z in f["zones"]]
    assert [r["id"] for r in b["assessment"]["required"]] == [r["id"] for r in f["assessment"]["required"]]
    assert not any("setEnvironment" in a for t in b["timeline"] for a in t["actions"])
    assert any("setEnvironment" in a for t in f["timeline"] for a in t["actions"])
    ship = ship_from_scenario(b)
    assert isinstance(ship.current_model, TidalCurrent)
    expect = {300: (85.0, 2.5), 720: (None, 0.0), 1200: (265.0, 3.0)}
    for t, (set_deg, drift) in expect.items():
        _steps_to(ship, 50 * t)
        st = ship.state_json()
        assert st["currentSource"] == "tidal"
        if set_deg is not None:
            assert st["current"]["set"] == pytest.approx(set_deg, abs=1e-9)
        assert st["current"]["drift"] == pytest.approx(drift, abs=1e-6)
    assert ship.state_json()["tideHeight_m"] == pytest.approx(4.096, abs=1e-9)
