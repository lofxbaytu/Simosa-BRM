"""潮汐模型(情境 ``environment.tide``;docs/design/scenario-engine.md 第 1.5.1 節;C# TideTests 的對應):

- ``tide.TideTable``(線性內插、端點)、``HarmonicTide``(公式);``scenario.normalise_tide``(utc → 相對 startTimeUtc 的秒數);
- ``structural_issues`` 的潮汐檢查(路徑與訊息同 C# Scenario.StructuralIssues);
- MMGShip:有效水深 = 基底 + 潮高(tick·dt)、state_json 的 ``tideHeight_m``/``waterDepth``、淺水修正隨潮高每步重算、
  ``set_environment waterDepth`` 只改基底、無潮汐時輸出與係數不變;
- 跨語言夾具 tests/SimCore.Tests/Golden/tide_*.csv(export_tide_reference.py 產生)可重現(確定性);E18b 情境載入與夾具標頭。
"""

from __future__ import annotations

import csv
import math
from pathlib import Path
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.mmg import make_ship
from simosa_brm.scenario import load_scenario, normalise_tide, ship_from_scenario, structural_issues, validate_scenario
from simosa_brm.tide import DEG, HarmonicTide, TideTable, tide_from_scenario

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"


def _tree(tide: dict[str, Any] | None, **top: Any) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "ship": {"id": "FSB1", "loading": "ballast"},
        "origin": {"lat": 23.8, "lon": 120.05},
        "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": 8, "telegraph": "HAH"},
        "environment": {"wind": {"trueSpeed": 0, "trueDir": 0, "gustiness": 0}, "current": {"set": 0, "drift": 0}, "waterDepth": 30},
    }
    if tide is not None:
        t["environment"]["tide"] = tide
    t.update(top)
    return t


TABLE = {"table": [{"t": 0, "height_m": 1}, {"t": 10, "height_m": 3}, {"t": 30, "height_m": 2}]}


def test_table_interpolates_linearly_and_clamps_ends() -> None:
    t = TideTable(((0.0, 1.0), (10.0, 2.0), (30.0, 1.0)))
    assert t.height_at(-5) == 1.0 and t.height_at(0) == 1.0 and t.height_at(10) == 2.0 and t.height_at(30) == 1.0 and t.height_at(1e6) == 1.0
    assert t.height_at(5) == pytest.approx(1.5, abs=1e-12) and t.height_at(20) == pytest.approx(1.5, abs=1e-12) and t.height_at(25) == pytest.approx(1.25, abs=1e-12)
    with pytest.raises(ValueError):
        TideTable(())


def test_harmonic_matches_formula() -> None:
    h = HarmonicTide(0.5, ((1.0, 100.0, math.pi / 2), (0.25, 50.0, 0.0)))
    assert h.height_at(25) == pytest.approx(0.5 - 1.0 - 0.25, abs=1e-12)
    assert h.height_at(0) == pytest.approx(0.5 + math.cos(math.pi / 2) + 0.25, abs=1e-12)
    m = tide_from_scenario({"harmonic": {"meanLevel_m": 0.5, "constituents": [{"amplitude_m": 1, "period_s": 100, "phase_deg": 90}]}})
    assert isinstance(m, HarmonicTide) and m.constituents[0][2] == 90.0 * DEG
    assert tide_from_scenario(None) is None and tide_from_scenario({}) is None


def test_normalise_tide_converts_utc_relative_to_start() -> None:
    raw = {"table": [{"utc": "2026-11-04T04:50:00Z", "height_m": 2.1}, {"t": 0, "height_m": 1.9}, {"utc": "2026-11-04T05:00:45.5Z", "height_m": 1.1}]}
    sc = validate_scenario(_tree(raw, startTimeUtc="2026-11-04T05:00:00Z"))
    assert sc["environment"]["tide"] == {"table": [{"t": -600.0, "height_m": 2.1}, {"t": 0.0, "height_m": 1.9}, {"t": 45.5, "height_m": 1.1}]}
    assert validate_scenario(_tree(None))["environment"]["tide"] is None
    h = validate_scenario(_tree({"harmonic": {"constituents": [{"name": "M2", "amplitude_m": 0.9, "period_s": 44714.16}]}}))["environment"]["tide"]
    assert h == {"harmonic": {"meanLevel_m": 0.0, "constituents": [{"name": "M2", "amplitude_m": 0.9, "period_s": 44714.16, "phase_deg": 0.0}]}}
    assert normalise_tide(None, None) is None


@pytest.mark.parametrize(
    ("tide", "start", "path", "message"),
    [
        ({"table": [{"t": 0, "height_m": 1}]}, None, "environment.tide.table", "至少需要 2 點"),
        ({"table": [{"t": 0, "height_m": 1}, {"t": 10, "height_m": 2}, {"t": 10, "height_m": 3}]}, None, "environment.tide.table[2]", "嚴格遞增"),
        ({"table": [{"t": 0, "height_m": 1}, {"height_m": 2}]}, None, "environment.tide.table[1]", "需要 t 或 utc"),
        ({"table": [{"t": 0, "utc": "2026-11-04T05:00:00Z", "height_m": 1}, {"t": 10, "height_m": 2}]}, "2026-11-04T05:00:00Z", "environment.tide.table[0]", "只能擇一"),
        ({"table": [{"utc": "2026-11-04T05:00:00Z", "height_m": 1}, {"utc": "2026-11-04T05:10:00Z", "height_m": 2}]}, None, "environment.tide.table[0]", "startTimeUtc"),
        ({"table": [{"utc": "not-a-time", "height_m": 1}, {"t": 10, "height_m": 2}]}, "2026-11-04T05:00:00Z", "environment.tide.table[0]", "utc 無法解析"),
        ({"table": [{"t": 0, "height_m": "x"}, {"t": 10, "height_m": 2}]}, None, "environment.tide.table[0]", "height_m 必須為有限數值"),
        ({"harmonic": {"meanLevel_m": 0, "constituents": [{"amplitude_m": 1, "period_s": 0}]}}, None, "environment.tide.harmonic.constituents[0]", "period_s 必須為正"),
        ({"harmonic": {"meanLevel_m": 0, "constituents": [{"amplitude_m": -1, "period_s": 100}]}}, None, "environment.tide.harmonic.constituents[0]", "不可為負"),
        ({"harmonic": {"meanLevel_m": 0, "constituents": []}}, None, "environment.tide.harmonic.constituents", "至少需要 1 個分潮"),
        ({"table": [{"t": 0, "height_m": 1}, {"t": 10, "height_m": 2}], "harmonic": {"constituents": [{"amplitude_m": 1, "period_s": 100}]}}, None, "environment.tide", "擇一"),
        ({}, None, "environment.tide", "擇一"),
        ("table", None, "environment.tide", "擇一"),
    ],
)
def test_structural_issues_reject_bad_tide(tide: Any, start: str | None, path: str, message: str) -> None:
    tree = _tree(tide, **({"startTimeUtc": start} if start else {}))
    issues = structural_issues(tree)
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match="environment.tide"):
        validate_scenario(tree)


def test_valid_tide_has_no_structural_issues() -> None:
    assert structural_issues(_tree(TABLE)) == []
    assert structural_issues(_tree({"harmonic": {"meanLevel_m": 0.8, "constituents": [{"name": "M2", "amplitude_m": 0.9, "period_s": 44714.16, "phase_deg": 30}]}})) == []
    assert structural_issues(_tree({"table": [{"utc": "2026-11-04T05:00:00Z", "height_m": 1}, {"t": 20, "height_m": 2}]}, startTimeUtc="2026-11-04T05:00:00Z")) == []


def _steps_to(ship: Any, tick: int) -> None:
    while ship.state.tick < tick:
        ship.step()


def test_ship_effective_depth_adds_tide_and_set_environment_changes_base_only() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(TABLE)))
    assert ship.tide is not None and ship.tide_height() == 1.0
    s0 = ship.state_json()
    assert s0["tideHeight_m"] == 1.0 and s0["waterDepth"] == 31.0
    f0 = ship.f_res
    _steps_to(ship, 250)  # t = 5 s → 潮高 2.0
    s5 = ship.state_json()
    assert s5["tideHeight_m"] == pytest.approx(2.0, abs=1e-12) and s5["waterDepth"] == pytest.approx(32.0, abs=1e-12)
    max_draft = max(ship.sp.loading.draft_fore_m, ship.sp.loading.draft_aft_m)
    assert s5["depthBelowKeel"] == pytest.approx(s5["waterDepth"] - max_draft - s5["squat"], abs=1e-9)  # UKC 以有效水深計算
    assert ship.f_res != f0  # 淺水修正隨有效水深每步重算
    ship.set_environment(water_depth_m=15.0)
    assert ship.env.water_depth == 15.0 and ship.state_json()["waterDepth"] == pytest.approx(15.0 + ship.tide_height(), abs=1e-12)
    _steps_to(ship, 2000)  # t = 40 s ≥ 表末點 → 端點值 2 m
    assert ship.state_json()["tideHeight_m"] == pytest.approx(2.0, abs=1e-12)


def test_without_tide_outputs_and_factors_are_unchanged() -> None:
    sc = validate_scenario(_tree(None))
    ship = ship_from_scenario(sc)
    assert ship.tide is None and ship.tide_height() == 0.0
    ref = make_ship("FSB1", "ballast", seed=sc["seed"])
    ref.set_environment(wind_speed_kn=0.0, water_depth_m=30.0)
    ref.reset(0.0, 0.0, 0.0, 8.0, rpm=float(ref.eng["telegraph"]["HAH"]), telegraph="HAH")  # apply_scenario 的 initial.telegraph 路徑
    ref.set_origin(23.8, 120.05)
    assert (ship.f_res, ship.f_lin_sway, ship.my) == (ref.f_res, ref.f_lin_sway, ref.my)
    _steps_to(ship, 150)
    _steps_to(ref, 150)
    s, r = ship.state_json(), ref.state_json()
    assert "tideHeight_m" not in s and s["waterDepth"] == 30.0
    assert (s["u"], s["v"], s["r"], s["pos"]["x"], s["pos"]["y"]) == (r["u"], r["v"], r["r"], r["pos"]["x"], r["pos"]["y"])
    assert ship.state_hash() == ref.state_hash()


def test_tide_uses_tick_time_and_reset_recomputes_shallow_factors() -> None:
    ship = ship_from_scenario(validate_scenario(_tree(TABLE)))
    _steps_to(ship, 500)
    assert ship.tide_height() == ship.tide.height_at(500 * ship.dt) == 3.0
    f10 = ship.f_res
    ship.reset(0.0, 0.0, 0.0, 8.0)
    assert ship.state.tick == 0 and ship.tide_height() == 1.0 and ship.f_res != f10
    ship.set_tide(None)
    assert ship.tide is None and "tideHeight_m" not in ship.state_json()


@pytest.mark.parametrize("name", ["tide_table", "tide_harmonic"])
def test_golden_tide_reference_is_reproducible(name: str) -> None:
    """export_tide_reference.py 的夾具(C# TideTests 以 1e-9 比對)以同一路徑重算必須完全一致。"""
    text = (GOLDEN / f"{name}.csv").read_text(encoding="utf-8").splitlines()
    assert text[1] == f"# scenario: {name}.yaml"
    rows = list(csv.DictReader(l for l in text if not l.startswith("#")))
    assert len(rows) >= 10
    ship = ship_from_scenario(load_scenario(GOLDEN / f"{name}.yaml"))
    for r in rows:
        _steps_to(ship, int(r["tick"]))
        st = ship.state_json()
        assert st["t"] == pytest.approx(float(r["t"]), abs=1e-9)
        assert st["tideHeight_m"] == float(r["tideHeight_m"]), (name, r["tick"])
        assert st["waterDepth"] == float(r["waterDepth_m"]), (name, r["tick"])
        assert st["waterDepth"] == ship.env.water_depth + st["tideHeight_m"]


def test_e18b_tide_table_scenario_loads_and_matches_e18_geometry() -> None:
    e18, e18b = load_scenario("E18_ukc_squat_anping"), load_scenario("E18b_tide_table")
    assert e18["environment"]["tide"] is None and e18["environment"]["waterDepth"] == 8.9
    assert e18b["environment"]["waterDepth"] == 7.0
    assert [p["height_m"] for p in e18b["environment"]["tide"]["table"]] == [1.9, 1.85, 1.6, 1.5, 1.4, 1.2]
    assert e18b["route"]["waypoints"] == e18["route"]["waypoints"] and [z["id"] for z in e18b["zones"]] == [z["id"] for z in e18["zones"]]
    assert [t["id"] for t in e18b["timeline"]] == [t["id"] for t in e18["timeline"]]
    assert not any("setEnvironment" in a for t in e18b["timeline"] for a in t["actions"])
    ship = ship_from_scenario(e18b)
    st = ship.state_json()
    assert st["tideHeight_m"] == 1.9 and st["waterDepth"] == 8.9 and st["depthBelowKeel"] > 0.5
    _steps_to(ship, 50 * 600)  # t = 600 s:1.85 → 1.6 的中點
    assert ship.state_json()["tideHeight_m"] == pytest.approx(1.725, abs=1e-12)
