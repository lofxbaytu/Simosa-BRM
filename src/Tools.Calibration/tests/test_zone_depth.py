"""區域水深覆寫(情境 ``zones[].waterDepth_m``;docs/design/scenario-engine.md 第 4.3 節;C# DepthProviders.cs ZoneDepth / Polygon2D 的對應):

- ``depth.point_in_polygon``(ray casting、邊上視為內、凹多邊形)、``zone_depth``(多區取最淺、比基底深者不覆寫);
- ``scenario.normalise_depth_zones``(``{x, y}`` 原值、``{lat, lon}`` 換算 ENU、無 waterDepth_m 區域略過、任何 type 皆參與;``zones`` 原樣保留);
- MMGShip:有效水深隨位置取區域值 + 潮高、state_json 的 ``waterDepth``/``depthBelowKeel``、淺水修正於步首重算(進入區域前軌跡與無區域情境逐位元相同)、
  直航平衡轉速以基底 + 潮高(0)計算(不含區域覆寫;C# ResolveInitialPropulsion)、``set_environment waterDepth`` 只改基底、
  比基底深的區域不改任何輸出(雜湊與無區域情境相同)、快照還原後依還原位置重算;
- 跨語言夾具 tests/SimCore.Tests/Golden/zone_depth.csv(export_zone_depth_reference.py 產生)可重現(確定性)。
"""

from __future__ import annotations

import csv
import math
from pathlib import Path
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.depth import DepthZone, depth_zones_from_scenario, distance_to_segment, point_in_polygon, zone_depth
from simosa_brm.mmg import LocalTangentPlane
from simosa_brm.scenario import load_scenario, normalise_depth_zones, ship_from_scenario, validate_scenario

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"

SQUARE = ((-300.0, -300.0), (300.0, -300.0), (300.0, 300.0), (-300.0, 300.0))
# 凹多邊形(L 形):左下方塊 + 下臂
L_SHAPE = ((-900.0, -900.0), (-500.0, -900.0), (-500.0, -700.0), (-700.0, -700.0), (-700.0, -500.0), (-900.0, -500.0))


def _tree(zones: list[dict[str, Any]] | None, *, x: float = -1500.0, y: float = 0.0, depth: float = 25.0, tide: dict[str, Any] | None = None,
          speed: float = 8.0) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "ship": {"id": "FSB1", "loading": "full"},
        "origin": {"lat": 23.8, "lon": 120.15},
        "initial": {"position": {"x": x, "y": y}, "heading": 90, "speed": speed, "telegraph": "HAH"},
        "environment": {"wind": {"trueSpeed": 0, "trueDir": 0, "gustiness": 0}, "current": {"set": 0, "drift": 0}, "waterDepth": depth},
    }
    if tide is not None:
        t["environment"]["tide"] = tide
    if zones is not None:
        t["zones"] = zones
    return t


def _zone(zid: str, depth: float | None, poly: tuple[tuple[float, float], ...] = SQUARE, ztype: str = "ukcPolicy") -> dict[str, Any]:
    z: dict[str, Any] = {"id": zid, "type": ztype, "polygon": [{"x": px, "y": py} for px, py in poly]}
    if depth is not None:
        z["waterDepth_m"] = depth
    return z


def _steps_to(ship: Any, tick: int) -> None:
    while ship.state.tick < tick:
        ship.step()


# ------------------------------------------------------------------ depth.py


def test_point_in_polygon_edges_vertices_and_concave() -> None:
    assert point_in_polygon(SQUARE, 0.0, 0.0)
    assert point_in_polygon(SQUARE, -300.0, 0.0)            # 邊上
    assert point_in_polygon(SQUARE, 300.0, 300.0)           # 頂點
    assert point_in_polygon(SQUARE, -299.9999999995, 0.0)   # 1e-9 門檻內
    assert not point_in_polygon(SQUARE, -300.000001, 0.0)
    assert not point_in_polygon(SQUARE, 0.0, 301.0)
    assert point_in_polygon(L_SHAPE, -800.0, -800.0)
    assert point_in_polygon(L_SHAPE, -600.0, -800.0)
    assert not point_in_polygon(L_SHAPE, -600.0, -600.0)    # 缺口
    assert point_in_polygon(L_SHAPE, -700.0, -700.0)        # 凹頂點
    assert not point_in_polygon(L_SHAPE, -1000.0, -900.0)   # 與頂點同 y 的區外點
    assert not point_in_polygon(SQUARE[:2], 0.0, 0.0)       # 少於 3 點
    assert distance_to_segment(0.0, 1.0, -1.0, 0.0, 1.0, 0.0) == 1.0
    assert distance_to_segment(3.0, 4.0, 0.0, 0.0, 0.0, 0.0) == 5.0  # 退化線段取端點


def test_zone_depth_takes_shallowest_and_ignores_deeper_zones() -> None:
    a = DepthZone("A", SQUARE, 12.0)
    b = DepthZone("B", ((0.0, 0.0), (600.0, 0.0), (600.0, 600.0), (0.0, 600.0)), 8.0)
    deep = DepthZone("D", SQUARE, 40.0)
    assert zone_depth(25.0, [a, b, deep], 0.0, 0.0) == 8.0
    assert zone_depth(25.0, [b, a, deep], 0.0, 0.0) == 8.0
    assert zone_depth(25.0, [a, b, deep], -200.0, -200.0) == 12.0
    assert zone_depth(25.0, [a, b, deep], 500.0, 500.0) == 8.0
    assert zone_depth(25.0, [deep], 0.0, 0.0) == 25.0
    assert zone_depth(25.0, [], 0.0, 0.0) == 25.0
    assert zone_depth(25.0, [a, b], 2000.0, 2000.0) == 25.0


# ------------------------------------------------------------------ scenario.py


def test_normalise_depth_zones_resolves_vertices_and_skips_zones_without_depth() -> None:
    origin = {"lat": 23.8, "lon": 120.15}
    ltp = LocalTangentPlane(23.8, 120.15)
    zones = [
        _zone("A", 12.0),
        _zone("F", None, ztype="noGo"),
        _zone("G", 6.0, ztype="reporting"),
        {"id": "E", "type": "ukcPolicy", "waterDepth_m": 10,
         "polygon": [{"lat": 23.805, "lon": 120.158}, {"lat": 23.805, "lon": 120.162}, {"lat": 23.809, "lon": 120.160}]},
    ]
    dz = normalise_depth_zones(zones, origin)
    assert [z["id"] for z in dz] == ["A", "G", "E"]
    assert dz[0]["waterDepth_m"] == 12.0 and dz[0]["polygon"] == [[px, py] for px, py in SQUARE]
    assert dz[1]["waterDepth_m"] == 6.0
    assert dz[2]["polygon"][0] == list(ltp.to_local(23.805, 120.158))
    assert normalise_depth_zones(None, origin) == [] and normalise_depth_zones([], origin) == []
    models = depth_zones_from_scenario(dz)
    assert [m.id for m in models] == ["A", "G", "E"] and models[0].depth == 12.0 and models[0].polygon == SQUARE
    assert depth_zones_from_scenario(None) == []

    sc = validate_scenario(_tree(zones))
    assert [z["id"] for z in sc["depthZones"]] == ["A", "G", "E"]
    assert sc["zones"] == zones  # zones 原樣保留(跨語言比對)
    assert validate_scenario(_tree(None))["depthZones"] == []


def test_depth_zone_structural_rules_unchanged() -> None:
    with pytest.raises(ValueError, match=r"zones\[0\]\.waterDepth_m:區域水深必須為正"):
        validate_scenario(_tree([_zone("A", 0.0)]))


# ------------------------------------------------------------------ MMGShip


def test_effective_depth_follows_position_and_state_outputs() -> None:
    tide = {"harmonic": {"meanLevel_m": 0.5, "constituents": [{"amplitude_m": 0.4, "period_s": 44714.16, "phase_deg": 60}]}}
    ship = ship_from_scenario(validate_scenario(_tree([_zone("A", 12.0), _zone("B", 8.0, ((0.0, 0.0), (600.0, 0.0), (600.0, 600.0), (0.0, 600.0)))], tide=tide)))
    assert [z.id for z in ship.depth_zones] == ["A", "B"]
    h0 = ship.tide_height()
    assert h0 == pytest.approx(0.7, abs=1e-12)
    assert ship.state_json()["waterDepth"] == 25.0 + h0       # 初始位置在區外
    assert ship.depth_at(0.0, 0.0) == 8.0 + h0
    assert ship.depth_at(-200.0, -200.0) == 12.0 + h0
    assert ship.depth_at(2000.0, 2000.0) == 25.0 + h0
    ship.state.x, ship.state.y = -200.0, -200.0
    st = ship.state_json()
    assert st["waterDepth"] == 12.0 + h0
    assert st["depthBelowKeel"] == pytest.approx(12.0 + h0 - max(ship.sp.loading.draft_fore_m, ship.sp.loading.draft_aft_m) - st["squat"], abs=1e-12)
    # setEnvironment waterDepth 只改基底:區域 12 m 仍最淺;基底改到 5 m 時基底最淺
    ship.set_environment(water_depth_m=20.0)
    assert ship.state_json()["waterDepth"] == 12.0 + h0
    ship.set_environment(water_depth_m=5.0)
    assert ship.state_json()["waterDepth"] == 5.0 + h0
    # 清除區域 → 回到基底
    ship.set_depth_zones(None)
    assert ship.depth_zones == [] and ship.state_json()["waterDepth"] == 5.0 + h0


def test_shallow_factors_recomputed_at_step_start_and_trajectory_identical_before_entering_zone() -> None:
    # 自船自 x = -1500 以 8 kn(約 4.1 m/s)向東,約 290 s 後進入 x ≥ -300 的 12 m 區(h/T ≈ 2 → 淺水修正明顯)
    with_zone = ship_from_scenario(validate_scenario(_tree([_zone("A", 12.0)])))
    without = ship_from_scenario(validate_scenario(_tree(None)))
    assert with_zone.state.n == without.state.n           # 直航平衡轉速不含區域覆寫(初始位置在區外亦然)
    f_res_deep = with_zone.f_res
    entered_tick: int | None = None
    for _ in range(50 * 400):
        if entered_tick is None and with_zone.state.x >= -300.0:
            entered_tick = with_zone.state.tick
            # 進入區域前(此步首位置已在區內,但步末狀態尚未推進)兩者逐位元相同
            assert with_zone.state_hash() == without.state_hash()
            assert with_zone.f_res == f_res_deep
        with_zone.step()
        without.step()
        if entered_tick is not None and with_zone.state.tick == entered_tick + 1:
            # 進入後第一步:步首以區域水深重算淺水修正(係數改變),軌跡自此分岔
            assert with_zone.f_res > f_res_deep
            assert with_zone.state_json()["waterDepth"] == 12.0
            assert with_zone.state_hash() != without.state_hash()
            break
    assert entered_tick is not None
    _steps_to(with_zone, entered_tick + 50)
    _steps_to(without, entered_tick + 50)
    assert with_zone.state.u < without.state.u          # 淺水阻力增加 → 減速


def test_initial_rpm_uses_base_depth_even_when_starting_inside_zone() -> None:
    inside = ship_from_scenario(validate_scenario(_tree([_zone("A", 12.0)], x=0.0, y=0.0)))
    plain = ship_from_scenario(validate_scenario(_tree(None, x=0.0, y=0.0)))
    assert inside.state.n == plain.state.n              # C# ResolveInitialPropulsion:environment.waterDepth + 潮高(0)
    assert inside.state_json()["waterDepth"] == 12.0    # 但 tick 0 的狀態輸出已是區域水深(C# BuildState 的 DepthAt)
    assert plain.state_json()["waterDepth"] == 25.0
    inside.step()
    plain.step()
    assert inside.f_res > plain.f_res                   # 第一步步首起以區域水深積分
    # reset 後(C# Reset → Initialize)同樣:轉速依基底、狀態輸出依區域
    inside.reset(0.0, 0.0, 90.0, 8.0)
    plain.reset(0.0, 0.0, 90.0, 8.0)
    assert inside.state.n == plain.state.n and inside.state_json()["waterDepth"] == 12.0


def test_deeper_zone_changes_nothing_and_no_zone_scenario_has_empty_list() -> None:
    deeper = ship_from_scenario(validate_scenario(_tree([_zone("D", 40.0)], x=0.0, y=0.0)))
    plain = ship_from_scenario(validate_scenario(_tree(None, x=0.0, y=0.0)))
    assert len(deeper.depth_zones) == 1 and plain.depth_zones == []
    for _ in range(100):
        deeper.step()
        plain.step()
    assert deeper.state_hash() == plain.state_hash()
    assert deeper.state_json()["waterDepth"] == plain.state_json()["waterDepth"] == 25.0


def test_snapshot_restore_recomputes_from_restored_position() -> None:
    ship = ship_from_scenario(validate_scenario(_tree([_zone("A", 12.0)], x=-350.0, y=0.0)))
    snap = ship.snapshot()
    f_before = ship.f_res
    _steps_to(ship, 50 * 60)                             # 進入 12 m 區
    assert ship.state_json()["waterDepth"] == 12.0 and ship.f_res > f_before
    ship.restore(snap)
    assert ship.state_json()["waterDepth"] == 25.0 and ship.f_res == f_before
    assert [z.id for z in ship.depth_zones] == ["A"]      # 區域不進快照、還原後仍在


# ------------------------------------------------------------------ 跨語言夾具


def test_golden_zone_depth_reproducible() -> None:
    text = (GOLDEN / "zone_depth.csv").read_text(encoding="utf-8").splitlines()
    assert text[1] == "# scenario: zone_depth.yaml"
    rows = list(csv.DictReader(l for l in text if not l.startswith("#")))
    assert len(rows) >= 20
    sc = load_scenario(GOLDEN / "zone_depth.yaml")
    assert [z["id"] for z in sc["depthZones"]] == ["A", "B", "C", "D", "E", "G"]
    ship = ship_from_scenario(sc)
    assert ship.tide_height() == pytest.approx(0.7, abs=1e-12)
    for r in rows:
        x, y = float(r["x"]), float(r["y"])
        assert ship.depth_at(x, y) == float(r["waterDepth_m"]), (x, y)
    assert ship.depth_at(0.0, 0.0) == 8.0 + ship.tide_height()
    assert ship.depth_at(-800.0, -800.0) == 15.0 + ship.tide_height()
    assert ship.depth_at(1200.0, 0.0) == 25.0 + ship.tide_height()
    assert ship.depth_at(800.0, 800.0) == 6.0 + ship.tide_height()
    assert math.isfinite(ship.depth_at(2000.0, 2000.0) or math.nan)
