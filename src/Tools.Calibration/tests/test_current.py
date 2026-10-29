"""時變流場(情境 ``environment.current.table`` | ``tidal``;docs/design/scenario-engine.md 第 1.5.2 節;C# TidalStreamTests 的對應):

- ``tide.TideTable/HarmonicTide`` 的 ``rate_at``/``max_rate``/``reference_period_s``;``current.CurrentTable``(向量內插、方向跨 360°)、
  ``TidalCurrent``(漲/退/平潮門檻);``scenario.normalise_current``(utc → 相對 startTimeUtc 的秒數、slackWindow_min 預設);
- ``structural_issues`` 的流場檢查(路徑與訊息同 C# Scenario.StructuralIssues);
- MMGShip:每步以 tick·dt 的模型值覆寫流向/流速、``state_json`` 的 ``currentSource``、``set_environment`` 給 current 後時變停止、
  ``reset``/``apply_scenario`` 恢復、常數流情境輸出與雜湊不變;
- 跨語言夾具 tests/SimCore.Tests/Golden/current_*.csv(export_current_reference.py 產生)可重現(確定性);E06b 潮流情境載入與漲 → 平潮 → 退。
- 空間變化流場 ``environment.current.field``(C# CurrentFieldTests 的對應):``current.CurrentField`` 雙線性內插(角點/中點/格內/格外 edge|zero)、
  timeScale tidal|table 的流速比例(方向取場的向量)、``normalise_current`` 的 field 正規化、``current_field_issues`` 結構檢查(訊息同 C#)、
  MMGShip 以自船位置刷新 state.current 與 currentSource = field|field+tidal|field+table、``current_en_at`` 其他位置取樣、
  船間交互作用目標船以自身位置取流、快照/倒帶、set_environment 覆寫語意、無 field 不變;跨語言夾具 current_field.csv 可重現。
"""

from __future__ import annotations

import copy
import csv
import math
from pathlib import Path
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.current import KN_TO_MPS, CurrentField, CurrentTable, TidalCurrent, current_from_scenario
from simosa_brm.external import Env
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
    assert sc["environment"]["current"] == {"set": 350.0, "drift": 1.0, "tidal": None, "field": None, "table": [
        {"t": -300.0, "speed_kn": 0.0, "direction_deg": 0.0}, {"t": 0.0, "speed_kn": 1.0, "direction_deg": 350.0}, {"t": 10.5, "speed_kn": 2.0, "direction_deg": 20.0}]}
    assert validate_scenario(_tree(None))["environment"]["current"] == {"set": 350.0, "drift": 1.0, "table": None, "tidal": None, "field": None}
    td = validate_scenario(_tree({"tidal": {"flood": {"speed_kn": 1, "direction_deg": 10}, "ebb": {"speed_kn": 2, "direction_deg": 190}}}, HARMONIC))["environment"]["current"]["tidal"]
    assert td == {"flood": {"speed_kn": 1.0, "direction_deg": 10.0}, "ebb": {"speed_kn": 2.0, "direction_deg": 190.0}, "slackWindow_min": 20.0}
    assert normalise_current(None, None) == {"set": 0.0, "drift": 0.0, "table": None, "tidal": None, "field": None}


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


# ---------------------------------------------------------------- 空間變化流場(environment.current.field)

# 3 × 2 網格:x ∈ {0, 100, 200}、y ∈ {0, 100};u 隨 x 增加(列同值)、v 只隨 y 變(C# CurrentFieldTests.Field)
FIELD = {"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 3, "ny": 2}, "u_kn": [[1, 2, 3], [1, 2, 3]], "v_kn": [[0, 0, 0], [1, 1, 1]]}}


def _field(**extra: Any) -> dict[str, Any]:
    f = copy.deepcopy(FIELD)
    f["field"].update(extra)
    return f


def _with(*parts: dict[str, Any]) -> dict[str, Any]:
    out: dict[str, Any] = {}
    for p in parts:
        out.update(copy.deepcopy(p))
    return out


def test_field_bilinear_interpolation_corners_midpoints_interior_and_outside() -> None:
    # 2 × 2 網格(x0 = 10, y0 = 20, dx = 100, dy = 50):u = [[1, 3], [5, 7]]、v = [[0, 2], [4, 8]](列為 y 索引)
    f = CurrentField(10.0, 20.0, 100.0, 50.0, 2, 2, (1.0, 3.0, 5.0, 7.0), (0.0, 2.0, 4.0, 8.0))
    assert f.source == "field" and f.spatially_varying and f.scaler is None and f.scale_at(999.0) == 1.0
    assert f.interpolate(10, 20) == (1.0, 0.0)
    assert f.interpolate(110, 20) == pytest.approx((3.0, 2.0), abs=1e-12)
    assert f.interpolate(10, 70) == pytest.approx((5.0, 4.0), abs=1e-12)
    assert f.interpolate(110, 70) == pytest.approx((7.0, 8.0), abs=1e-12)
    assert f.interpolate(60, 20)[0] == pytest.approx(2.0, abs=1e-12) and f.interpolate(60, 70)[1] == pytest.approx(6.0, abs=1e-12)
    assert f.interpolate(60, 45) == pytest.approx((4.0, 3.5), abs=1e-12)  # 格中心 = 四角平均
    tx, ty = 0.3, 0.8
    a = 1.0 + (3.0 - 1.0) * tx
    b = 5.0 + (7.0 - 5.0) * tx
    assert f.interpolate(10 + 100 * tx, 20 + 50 * ty)[0] == pytest.approx(a + (b - a) * ty, abs=1e-12)
    # 格外(edge):取最近邊緣值
    assert f.interpolate(-500, -500) == (1.0, 0.0) and f.interpolate(1e6, 1e6) == (7.0, 8.0)
    assert f.interpolate(60, -100)[0] == pytest.approx(2.0, abs=1e-12) and f.interpolate(60, 999)[0] == pytest.approx(6.0, abs=1e-12)
    # 格外(zero):0,格內與邊界上不受影響
    z = CurrentField(10.0, 20.0, 100.0, 50.0, 2, 2, (1.0, 3.0, 5.0, 7.0), (0.0, 2.0, 4.0, 8.0), outside_zero=True)
    assert z.interpolate(-500, 20) == (0.0, 0.0) and z.interpolate(60, 70.0001) == (0.0, 0.0)
    assert z.interpolate(60, 45) == f.interpolate(60, 45) and z.interpolate(110, 70) == (7.0, 8.0)
    # at:set = atan2(E, N)、drift = √(E²+N²);無 timeScale 時 t 無關;省略位置取原點
    set_rad, drift = f.at(123.4, 110, 70)
    assert _deg(set_rad) == pytest.approx(math.degrees(math.atan2(7, 8)) % 360.0, abs=1e-9) and drift == pytest.approx(math.sqrt(49 + 64), abs=1e-12)
    assert f.at(0.0) == f.at(0.0, 0.0, 0.0)
    for bad in ((0.0, 0.0, 0.0, 1.0, 2, 2, (0.0,) * 4, (0.0,) * 4), (0.0, 0.0, 1.0, 1.0, 1, 2, (0.0,) * 2, (0.0,) * 2),
                (0.0, 0.0, 1.0, 1.0, 2, 2, (0.0,) * 3, (0.0,) * 4)):
        with pytest.raises(ValueError):
            CurrentField(*bad)


def test_field_from_scenario_in_knots_with_defaults() -> None:
    sc = validate_scenario(_tree(FIELD))
    cur = sc["environment"]["current"]
    assert cur["field"] == {"grid": {"x0": 0.0, "y0": 0.0, "dx": 100.0, "dy": 100.0, "nx": 3, "ny": 2},
                            "u_kn": [[1.0, 2.0, 3.0], [1.0, 2.0, 3.0]], "v_kn": [[0.0, 0.0, 0.0], [1.0, 1.0, 1.0]], "outside": "edge", "timeScale": "none"}
    f = current_from_scenario(cur, None)
    assert isinstance(f, CurrentField) and f.source == "field" and f.scaler is None and not f.outside_zero
    assert (f.x0, f.y0, f.dx, f.dy, f.nx, f.ny) == (0.0, 0.0, 100.0, 100.0, 3, 2)
    assert f.u == tuple(v * KN_TO_MPS for v in (1, 2, 3, 1, 2, 3)) and f.v == (0.0, 0.0, 0.0, KN_TO_MPS, KN_TO_MPS, KN_TO_MPS)
    set_rad, drift = f.at(0.0, 150, 50)  # u 2.5 kn、v 0.5 kn
    assert _deg(set_rad) == pytest.approx(math.degrees(math.atan2(2.5, 0.5)), abs=1e-9) and drift == pytest.approx(math.sqrt(2.5 * 2.5 + 0.25) * KN_TO_MPS, abs=1e-12)
    zero = current_from_scenario(validate_scenario(_tree(_field(outside="Zero")))["environment"]["current"], None)
    assert isinstance(zero, CurrentField) and zero.outside_zero
    assert isinstance(current_from_scenario(validate_scenario(_tree(TABLE))["environment"]["current"], None), CurrentTable)
    assert current_from_scenario(validate_scenario(_tree(None))["environment"]["current"], None) is None


def test_field_time_scale_tidal_scales_speed_and_keeps_field_direction() -> None:
    sc = validate_scenario(_tree(_with(TIDAL, _field(timeScale="tidal")), HARMONIC))
    ship = ship_from_scenario(sc)
    f = ship.current_model
    assert isinstance(f, CurrentField) and isinstance(f.scaler, TidalCurrent) and f.source == "field+tidal"
    assert f.scaler_max_mps == 3.0 * KN_TO_MPS  # max(flood 2.5, ebb 3.0)
    tidal = f.scaler
    for t in (0.0, 7.5, 20.0, 50.0, 110.0):
        k = tidal.at(t)[1] / (3.0 * KN_TO_MPS)
        assert f.scale_at(t) == pytest.approx(min(1.0, k), abs=1e-12) and 0.0 <= f.scale_at(t) <= 1.0
        u, v = f.interpolate(150, 50)
        assert f.vector_at(t, 150, 50) == pytest.approx((u * f.scale_at(t), v * f.scale_at(t)), abs=1e-12)
    assert f.vector_at(20.0, 150, 50) == (0.0, 0.0) and f.at(20.0, 150, 50)[1] == 0.0  # 平潮:比例 0
    set_ebb, drift_ebb = f.at(50.0, 150, 50)  # 中退:比例 1,方向仍為場的向量(不反向)
    assert _deg(set_ebb) == pytest.approx(math.degrees(math.atan2(2.5, 0.5)), abs=1e-9)
    assert drift_ebb == pytest.approx(math.sqrt(2.5 * 2.5 + 0.25) * KN_TO_MPS, abs=1e-9)
    assert _deg(tidal.at(50.0)[0]) == pytest.approx(265.0, abs=1e-9)
    with pytest.raises(ValueError, match="timeScale tidal 需要 current.tidal"):
        current_from_scenario({"field": sc["environment"]["current"]["field"]}, None)


def test_field_time_scale_table_scales_by_table_speed_ratio_clamped() -> None:
    table = {"table": [{"t": 0, "speed_kn": 1, "direction_deg": 0}, {"t": 10, "speed_kn": 2, "direction_deg": 90}]}
    sc = validate_scenario(_tree(_with(table, _field(timeScale="table"))))
    f = current_from_scenario(sc["environment"]["current"], None)
    assert isinstance(f, CurrentField) and isinstance(f.scaler, CurrentTable) and f.source == "field+table"
    assert f.scaler_max_mps == pytest.approx(2.0 * KN_TO_MPS, abs=1e-12) and f.scaler.max_speed_mps == f.scaler_max_mps
    assert f.scale_at(-5.0) == pytest.approx(0.5, abs=1e-12) and f.scale_at(10.0) == pytest.approx(1.0, abs=1e-12) and f.scale_at(1e6) == 1.0
    mid = f.scaler.at(5.0)[1] / (2.0 * KN_TO_MPS)
    assert 0.5 <= mid <= 1.0 and f.scale_at(5.0) == pytest.approx(mid, abs=1e-12)
    u, v = f.interpolate(50, 0)
    assert f.vector_at(0.0, 50, 0) == (u * 0.5, v * 0.5)
    zero = CurrentField(0.0, 0.0, 1.0, 1.0, 2, 2, (1.0,) * 4, (0.0,) * 4, scaler=CurrentTable(((0.0, 0.0, 0.0), (10.0, 0.0, 0.0))))
    assert zero.scale_at(3.0) == 0.0 and zero.vector_at(3.0, 0.5, 0.5) == (0.0, 0.0)
    with pytest.raises(ValueError, match="timeScale table 需要 current.table"):
        current_from_scenario({"field": sc["environment"]["current"]["field"]}, None)
    with pytest.raises(ValueError, match="timeScale 只能為 table 或 tidal"):
        CurrentField(0.0, 0.0, 1.0, 1.0, 2, 2, (1.0,) * 4, (0.0,) * 4, scaler=CurrentField(0.0, 0.0, 1.0, 1.0, 2, 2, (1.0,) * 4, (0.0,) * 4))


@pytest.mark.parametrize(
    ("current", "tide", "path", "message"),
    [
        ({"field": {"u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.grid", "field 需要 grid"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 0, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.grid", "dx 必須為正"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": -1, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.grid", "dy 必須為正"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 1, "ny": 2}, "u_kn": [[1], [1]], "v_kn": [[0], [0]]}}, None, "environment.current.field.grid", "nx 必須為 ≥ 2 的整數"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 0}, "u_kn": [], "v_kn": []}}, None, "environment.current.field.grid", "ny 必須為 ≥ 2 的整數"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2.5, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.grid", "nx 必須為 ≥ 2 的整數"),
        ({"field": {"grid": {"x0": "a", "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.grid", "x0/y0 必須為有限數值"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.u_kn", "field 需要 u_kn"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]]}}, None, "environment.current.field.v_kn", "field 需要 v_kn"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.u_kn", "u_kn 須有 ny = 2 列(得到 3)"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0, 0]]}}, None, "environment.current.field.v_kn[1]", "v_kn 每列須有 nx = 2 個值(得到 3)"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, "x"], [1, 2]], "v_kn": [[0, 0], [0, 0]]}}, None, "environment.current.field.u_kn[0][1]", "u_kn 的值必須為有限數值"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]], "outside": "wrap"}}, None, "environment.current.field.outside", "outside 須為 edge | zero"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]], "timeScale": "moon"}}, None, "environment.current.field.timeScale", "timeScale 須為 none | tidal | table"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]], "timeScale": "tidal"}}, None, "environment.current.field.timeScale", "timeScale tidal 需要 current.tidal"),
        ({"field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]], "timeScale": "table"}}, None, "environment.current.field.timeScale", "timeScale table 需要 current.table"),
        ({"tidal": TIDAL["tidal"], "field": {"grid": {"x0": 0, "y0": 0, "dx": 100, "dy": 100, "nx": 2, "ny": 2}, "u_kn": [[1, 2], [1, 2]], "v_kn": [[0, 0], [0, 0]], "timeScale": "table"}}, HARMONIC, "environment.current.field.timeScale", "timeScale table 需要 current.table"),
        ({"field": "nope"}, None, "environment.current.field.grid", "field 需要 grid"),
    ],
)
def test_structural_issues_reject_bad_field(current: Any, tide: Any, path: str, message: str) -> None:
    tree = _tree(current, tide)
    issues = structural_issues(tree)
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match="environment.current"):
        validate_scenario(tree)


def test_valid_fields_have_no_structural_issues() -> None:
    assert structural_issues(_tree(FIELD)) == []
    assert structural_issues(_tree(_field(outside="zero", timeScale="none"))) == []
    assert structural_issues(_tree(_with(TIDAL, _field(timeScale="tidal")), HARMONIC)) == []
    assert structural_issues(_tree(_with(TABLE, _field(timeScale="table")))) == []
    assert structural_issues(_tree(_with(TABLE, FIELD))) == []  # timeScale none:table 只保留不使用
    assert structural_issues(_tree(_with(TIDAL, _field(outside="zero")), HARMONIC)) == []
    assert any("只能擇一" in m for _, m in structural_issues(_tree(_with(TABLE, TIDAL, FIELD), HARMONIC)))


def test_ship_refreshes_field_at_own_position_and_samples_other_positions() -> None:
    tree = _tree(FIELD)
    tree["initial"]["position"] = {"x": 150, "y": 50}
    ship = ship_from_scenario(validate_scenario(tree))
    assert ship.current_model_active and ship.current_field_active and ship.current_source == "field"
    s0 = ship.state_json()
    assert s0["currentSource"] == "field"
    assert s0["current"]["set"] == pytest.approx(math.degrees(math.atan2(2.5, 0.5)), abs=1e-9)   # 自船位置 (150, 50) 的值,非情境 set/drift
    assert s0["current"]["drift"] == pytest.approx(math.sqrt(2.5 * 2.5 + 0.25), abs=1e-9)
    e, n = ship.env.current_en
    assert ship.current_en_at(0.0, 150, 50) == (e, n)
    assert ship.current_en_at(0.0, 0, 0) == pytest.approx((KN_TO_MPS, 0.0), abs=1e-12)
    assert ship.current_en_at(0.0, 1e5, 1e5) == pytest.approx((3 * KN_TO_MPS, KN_TO_MPS), abs=1e-12)
    _steps_to(ship, 1000)  # 20 s
    st = ship.state
    set_rad, drift = ship.current_model.at(st.tick * ship.dt, st.x, st.y)
    assert (ship.env.current_set, ship.env.current_speed) == (set_rad, drift)
    assert st.x > 150 and st.y > 50 and ship.state_json()["current"]["drift"] > s0["current"]["drift"]
    assert ship.state_json()["currentSource"] == "field"


def test_constant_and_uniform_current_ships_are_unchanged_by_field_support() -> None:
    # 常數流:current_en_at 與位置/時間無關且等於 env.current_en;雜湊與參考模型相同(test_constant_current_scenarios_are_unchanged 的補充)
    ship = ship_from_scenario(validate_scenario(_tree(None)))
    assert not ship.current_field_active
    _steps_to(ship, 100)
    assert ship.current_en_at(999.0, 1.0, 2.0) == ship.env.current_en == ship.current_en_at(0.0, -5e4, 5e4)
    # 均勻時變流場(table):spatially_varying False、位置被忽略
    t = ship_from_scenario(validate_scenario(_tree(TABLE)))
    assert not t.current_model.spatially_varying and not t.current_field_active
    assert t.current_model.at(5.0) == t.current_model.at(5.0, 5000.0, 5000.0)
    _steps_to(t, 250)
    assert t.state_json()["currentSource"] == "table" and t.current_en_at(5.0, 5000.0, 5000.0) == t.env.current_en
    e06b_sc = load_scenario("E06b_tidal_stream")
    assert e06b_sc["environment"]["current"]["field"] is None
    e06b = ship_from_scenario(e06b_sc)
    assert isinstance(e06b.current_model, TidalCurrent) and not e06b.current_field_active and e06b.state_json()["currentSource"] == "tidal"


def test_interaction_targets_take_the_current_at_their_own_position() -> None:
    """船間交互作用的目標船(KinematicTarget)以自身位置取流(C# TrafficManager.Step 的 currentAt):自船在 y = 100 列(v = 1 kn),
    目標在 y = 0 列(v = 0)以 090° 5 kn 東行 → 目標只有東向流且 u 隨 x 增加。"""
    tree = _tree(FIELD)
    tree["initial"].update({"position": {"x": 100, "y": 100}, "speed": 0, "telegraph": "STOP"})
    tree["environment"]["shipInteraction"] = {"enabled": True}
    tree["targets"] = [{"id": "T", "loa": 100, "beam": 16, "initial": {"position": {"x": 0, "y": 0}, "heading": 90, "speed": 5}, "behaviour": {"mode": "hold"}}]
    ship = ship_from_scenario(validate_scenario(tree))
    assert ship.interaction is not None and ship.interaction.active and len(ship.interaction.targets) == 1
    tgt = ship.interaction.targets[0]
    x0, y0 = tgt.x, tgt.y
    expected_e = 5 * KN_TO_MPS + ship.current_en_at(0.0, x0, y0)[0]
    ship.step()
    assert tgt.x == pytest.approx(x0 + expected_e * ship.dt, abs=1e-9) and tgt.y == pytest.approx(y0, abs=1e-9)
    assert ship.env.current_speed * math.cos(ship.env.current_set) == pytest.approx(KN_TO_MPS, abs=1e-9)  # 自船列 v = 1 kn 不會套到目標
    prev_x = tgt.x
    speeds = []
    for _ in range(250):
        ship.step()
        speeds.append((tgt.x - prev_x) / ship.dt)
        prev_x = tgt.x
    assert all(b >= a - 1e-12 for a, b in zip(speeds, speeds[1:]))  # u 隨 x 線性增加 → 東向對地速度單調遞增
    assert tgt.y == pytest.approx(y0, abs=1e-9)
    # 均勻流時 advance_targets 不帶取樣函式,行為與舊版相同
    ship.interaction.advance_targets(Env(0.1, 0.2, math.inf), 1.0)
    assert tgt.y == pytest.approx(y0 + 0.2, abs=1e-9)


def test_field_snapshot_restore_and_override_semantics() -> None:
    sc = validate_scenario(_tree(_with(TIDAL, _field(timeScale="tidal")), HARMONIC))
    ship = ship_from_scenario(sc)
    assert ship.state_json()["currentSource"] == "field+tidal"
    _steps_to(ship, 500)
    snap = ship.snapshot()
    s10 = ship.state_json()
    _steps_to(ship, 2000)
    h40, s40 = ship.state_hash(), ship.state_json()
    assert s40["currentSource"] == "field+tidal" and s40["current"] != s10["current"]
    ship.restore(snap)
    assert ship.state_json()["current"] == s10["current"]
    _steps_to(ship, 2000)
    assert ship.state_hash() == h40 and ship.state_json()["current"] == s40["current"] and ship.state_json()["pos"] == s40["pos"]
    # 覆寫:場停用,各位置皆為常數;倒帶到覆寫前恢復;reset 恢復
    _steps_to(ship, 2250)
    snap2 = ship.snapshot()
    ship.set_environment(wind_speed_kn=10.0, wind_dir_deg=90.0)
    assert ship.current_field_active
    ship.set_environment(current_set_deg=100.0, current_drift_kn=0.5)
    assert ship.current_overridden and not ship.current_field_active and "currentSource" not in ship.state_json()
    _steps_to(ship, 2500)
    assert ship.current_en_at(0.0, 0.0, 0.0) == ship.current_en_at(50.0, 1e5, 1e5) == ship.env.current_en
    assert ship.env.current_en[0] == pytest.approx(0.5 * KN_TO_MPS * math.sin(math.radians(100)), abs=1e-12)
    assert ship.snapshot()["current_overridden"] is True
    ship.restore(snap2)
    assert not ship.current_overridden and ship.current_field_active and ship.state_json()["currentSource"] == "field+tidal"
    assert ship.current_en_at(45.0, 0.0, 0.0) != ship.current_en_at(45.0, 1e5, 1e5)
    ship.set_environment(current_set_deg=100.0, current_drift_kn=0.5)
    ship.reset(0.0, 0.0, 0.0, 8.0)
    assert not ship.current_overridden and ship.state_json()["currentSource"] == "field+tidal"


def test_golden_field_reference_is_reproducible() -> None:
    """export_current_reference.py 的 current_field.csv(C# CurrentFieldTests.GoldenFieldMatchesPythonReference 比對)以同一路徑重算必須完全一致。"""
    text = (GOLDEN / "current_field.csv").read_text(encoding="utf-8").splitlines()
    assert text[1] == "# scenario: current_field.yaml"
    rows = list(csv.DictReader(l for l in text if not l.startswith("#")))
    runs = [r for r in rows if r["kind"] == "run"]
    points = [r for r in rows if r["kind"] == "point"]
    assert len(runs) >= 10 and len(points) >= 10
    ship = ship_from_scenario(load_scenario(GOLDEN / "current_field.yaml"))
    model = ship.current_model
    assert isinstance(model, CurrentField) and model.source == "field+tidal"
    for r in runs:
        _steps_to(ship, int(r["tick"]))
        st = ship.state_json()
        e, n = ship.env.current_en
        assert st["t"] == pytest.approx(float(r["t"]), abs=1e-9) and st["currentSource"] == r["currentSource"]
        assert (ship.state.x, ship.state.y) == (float(r["x"]), float(r["y"])), r["tick"]
        assert st["current"]["set"] == float(r["set_deg"]) and st["current"]["drift"] == float(r["drift_kn"]), r["tick"]
        assert (e, n) == (float(r["east_mps"]), float(r["north_mps"])), r["tick"]
    assert ship.state.y > 100.0  # 已穿越 y = 100 的格線
    for r in points:
        t, x, y = float(r["t"]), float(r["x"]), float(r["y"])
        set_rad, drift = model.at(t, x, y)
        assert math.degrees(set_rad) % 360.0 == float(r["set_deg"]) and drift / KN_TO_MPS == float(r["drift_kn"]), (t, x, y)
        assert ship.current_en_at(t, x, y) == (float(r["east_mps"]), float(r["north_mps"])), (t, x, y)
    assert any(float(r["t"]) == 20.0 and float(r["drift_kn"]) == 0.0 for r in points)  # 平潮:零向量
