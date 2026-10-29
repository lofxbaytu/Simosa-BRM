"""船間交互作用(情境 ``environment.shipInteraction``;docs/design/physics-ship-interaction.md;C# ShipInteractionTests 的對應):

- ``external.ShipInteractionModel``:左右舷鏡像對稱、追越各階段的力與力矩方向(並行吸引、對方艏到自船艉時艉被推離/艏轉向對方、對方艏越過自船艏時艏被推離)、
  對遇、距離衰減與作用範圍(漸減帶、範圍外零力)、量級(FSB1 8 kn 被 2 倍排水量船以 12 kn 近距 50 m 追越:深水約十餘 t、淺水放大)、
  靜止目標零力、倍率、核函數鏡像級數收斂與導數、剖面積曲線與目標船體預設;
- ``scenario`` 的結構檢查與正規化(路徑與訊息同 C# Scenario.StructuralIssues)、``normalise_sections`` 帶出 shipInteraction 與直線等速目標;
- MMGShip:施力與艏搖、``state_json`` 的 ``shipInteraction``、事件進入/離開各一次、雜湊區塊、快照/還原、未啟用時輸出與雜湊不變;
- 跨語言夾具 tests/SimCore.Tests/Golden/interaction_force_table.csv(逐值相同)與 interaction_overtaking_run.{yaml,csv}(export_interaction_reference.py 產生)可重現。
"""

from __future__ import annotations

import csv
import importlib.util
import math
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.external import (
    DEG2RAD,
    KN_TO_MPS,
    TONNE_FORCE,
    InteractionTarget,
    Kin,
    ShipInteractionModel,
    hull_profile,
    interaction_kernel,
    interaction_taper,
    normalise_sections,
    normalise_ship_interaction,
    target_block_coefficient,
    target_default_cb,
    target_draft,
    SHIP_INTERACTION_DEFAULTS,
)
from simosa_brm.mmg import make_ship
from simosa_brm.particulars import load_particulars
from simosa_brm.scenario import load_scenario, ship_from_scenario, structural_issues, validate_scenario

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"
DEEP = 1000.0

OVERTAKER = {
    "id": "OVT", "shipType": "cargo", "loa": 130, "beam": 22, "draft": 7.5, "displacement_t": 16490,
    "initial": {"position": {"x": -50, "y": -200}, "heading": 0, "speed": 12}, "behaviour": {"mode": "hold"},
}


def _tree(si: dict[str, Any] | None, targets: list[dict[str, Any]] | None = None, speed: float = 8.0, telegraph: str = "HAH", depth: float = DEEP,
          loading: str = "full", **top: Any) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "seed": 7, "ship": {"id": "FSB1", "loading": loading},
        "origin": {"lat": 23.8, "lon": 120.15},
        "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": speed, "telegraph": telegraph},
        "environment": {"wind": {"trueSpeed": 0, "trueDir": 0, "gustiness": 0}, "current": {"set": 0, "drift": 0}, "waterDepth": depth},
    }
    if si is not None:
        t["environment"]["shipInteraction"] = si
    if targets is not None:
        t["targets"] = targets
    t.update(top)
    return t


def _model(scale: float | None = None, threshold: float | None = None, loading: str = "full", ship: str = "FSB1") -> ShipInteractionModel:
    cfg: dict[str, Any] = {"enabled": True, "coefficientScale": scale, "eventThreshold_t": threshold, "targets": []}
    return ShipInteractionModel(load_particulars(ship, loading).raw, loading, cfg)


def _at(psi_deg: float, u: float = 0.0, v: float = 0.0, x: float = 0.0, y: float = 0.0) -> Kin:
    return Kin(x, y, psi_deg * DEG2RAD, u, v, 0.0)


def _target(own: Kin, dx: float, dy: float, rel_deg: float = 0.0, speed_kn: float = 12.0, l: float = 130.0, b: float = 22.0, t: float = 7.5,
            cb: float = 0.75, tid: str = "T") -> InteractionTarget:
    sn, cs = math.sin(own.psi), math.cos(own.psi)
    return InteractionTarget(tid, own.x + dx * sn + dy * cs, own.y + dx * cs - dy * sn, (own.psi + rel_deg * DEG2RAD) % (2.0 * math.pi),
                             speed_kn * KN_TO_MPS, l, b, t, cb)


def _overtaking(m: ShipInteractionModel, xi: float, dy: float = -50.0, depth: float = DEEP, own_kn: float = 8.0, target_kn: float = 12.0):
    own = _at(0.0, own_kn * KN_TO_MPS)
    return m.compute(own, depth, [_target(own, xi * m.L, dy, speed_kn=target_kn)])


def _steps_to(ship: Any, tick: int) -> None:
    while ship.state.tick < tick:
        ship.step()


def _interaction_events(ship: Any) -> list[dict[str, Any]]:
    return [ev for ev in ship.external_events if ev["kind"] == "shipInteraction"]


# ---------------------------------------------------------------------------- 純函數


@pytest.mark.parametrize(("psi", "u", "v", "xi"), [(0.0, 4.0, 0.0, -0.8), (30.0, 3.0, 0.2, 0.0), (200.0, -1.5, 0.0, 0.6), (120.0, 5.0, -0.4, -1.2)])
def test_port_starboard_mirror_symmetry(psi: float, u: float, v: float, xi: float) -> None:
    m = _model()
    own_a = _at(psi, u, v, 100.0, -50.0)
    own_b = _at(psi, u, -v, 100.0, -50.0)
    a = m.compute(own_a, 20.0, [_target(own_a, xi * m.L, -45.0)])
    b = m.compute(own_b, 20.0, [_target(own_b, xi * m.L, 45.0)])
    assert a.active and b.active
    assert a.X == pytest.approx(b.X, rel=1e-9, abs=1e-9)
    assert a.Y == pytest.approx(-b.Y, rel=1e-9, abs=1e-9)
    assert a.N == pytest.approx(-b.N, rel=1e-9, abs=1e-9)
    assert a.xi == pytest.approx(b.xi, rel=1e-12) and a.eta == pytest.approx(-b.eta, rel=1e-12)


def test_overtaking_stages_have_classic_signs_and_magnitude() -> None:
    m = _model()
    abreast, stern_approach, bow_passed = _overtaking(m, 0.0), _overtaking(m, -1.0), _overtaking(m, 1.0)
    # 並行:吸引(目標在左舷 → Y 負),無艏搖
    assert -20.0 < abreast.Y / TONNE_FORCE < -8.0 and abs(abreast.N) < 1e-6 * abs(abreast.Y) * m.L and abs(abreast.X) < 1e-6 * abs(abreast.Y)
    assert abreast.target_id == "T" and abreast.xi == pytest.approx(0.0, abs=1e-12) and abreast.eta == pytest.approx(-50.0 / m.L, rel=1e-12)
    # 對方艏到自船艉:艉被推離(Y 正)、艏轉向對方(N 負);對方艏越過自船艏:艏被推離(N 正)
    assert stern_approach.Y > 0.0 and stern_approach.N < 0.0 and 2.0 < stern_approach.Y / TONNE_FORCE < 12.0 and 0.3 < -stern_approach.N / 1e6 < 3.0
    assert bow_passed.Y > 0.0 and bow_passed.N > 0.0
    # 淺水放大(12 m,h/T 2.1)2–6 倍;50 m 介於兩者
    shallow, mid = _overtaking(m, 0.0, depth=12.0), _overtaking(m, 0.0, depth=50.0)
    assert 2.0 < shallow.Y / abreast.Y < 6.0 and abs(abreast.Y) < abs(mid.Y) < abs(shallow.Y)
    # 與舵力同量級(10° 舵在 8 kn 約 6.5 t)
    assert 1.0 < abs(abreast.Y) / (6.5 * TONNE_FORCE) < 4.0
    # 靜止目標零力;自船靜止(繫泊)仍受壓力梯度項
    own = _at(0.0, 8.0 * KN_TO_MPS)
    still = m.compute(own, DEEP, [_target(own, 0.0, -50.0, speed_kn=0.0)])
    assert still.active and still.X == 0.0 and still.Y == 0.0 and still.N == 0.0
    moored = m.compute(_at(0.0), DEEP, [_target(_at(0.0), 0.0, -50.0)])
    assert moored.Y < -3.0 * TONNE_FORCE
    # 倍率
    assert _overtaking(_model(scale=2.0), 0.0).Y == pytest.approx(2.0 * abreast.Y, rel=1e-12)
    zero = _overtaking(_model(scale=0.0), 0.0)
    assert zero.active and zero.Y == 0.0 and zero.N == 0.0


def test_head_on_encounter_repels_approaching_bows_and_attracts_abreast() -> None:
    m = _model()
    own = _at(0.0, 8.0 * KN_TO_MPS)

    def oncoming(xi: float) -> InteractionTarget:
        return _target(own, xi * m.L, -50.0, rel_deg=180.0, speed_kn=10.0)

    assert m.compute(own, DEEP, [oncoming(1.3)]).Y > 0.0          # 艏對艏接近:兩船被推開
    bow_alongside = m.compute(own, DEEP, [oncoming(0.4)])
    assert bow_alongside.N < 0.0                                   # 對方低壓中體到自船艏旁:艏被吸向對方
    abreast = m.compute(own, DEEP, [oncoming(0.0)])
    assert abreast.Y < 0.0 and abs(abreast.N) < 1e-6 * abs(abreast.Y) * m.L
    passed = m.compute(own, DEEP, [oncoming(-0.4)])
    assert passed.N > 0.0 and passed.N == pytest.approx(-bow_alongside.N, rel=1e-9)
    crossing = m.compute(own, DEEP, [_target(own, -20.0, 60.0, rel_deg=30.0, speed_kn=9.0, l=80.0, b=14.0, t=4.5, cb=0.65)])
    assert crossing.active and crossing.Y != 0.0 and crossing.N != 0.0


def test_force_decays_with_separation_and_vanishes_outside_range() -> None:
    m = _model()
    own = _at(0.0, 8.0 * KN_TO_MPS)
    prev = math.inf
    for dy in (30.0, 40.0, 50.0, 60.0, 70.0):
        y = abs(_overtaking(m, 0.0, dy=-dy).Y)
        assert y < prev
        prev = y
    yl = 2.0 * (m.B + 22.0)
    full = _overtaking(m, 0.0, dy=-(0.8 * yl - 0.01))
    taper_in = _overtaking(m, 0.0, dy=-(0.8 * yl + 0.01))
    assert taper_in.Y == pytest.approx(full.Y, rel=2e-2)
    assert abs(_overtaking(m, 0.0, dy=-(yl - 0.01)).Y) < 1e-3 * abs(full.Y)
    outside = _overtaking(m, 0.0, dy=-(yl + 0.01))
    assert not outside.active and outside.X == outside.Y == outside.N == 0.0 and outside.target_id is None and outside.count == 0
    assert not _overtaking(m, 3.0).active
    in_range = _overtaking(m, 2.99)
    assert in_range.active and abs(in_range.Y) < 1e-3 * abs(full.Y)
    assert interaction_taper(0.5, 0.8) == 1.0 and interaction_taper(1.0, 0.8) == 0.0 and interaction_taper(0.9, 0.8) == pytest.approx(0.5, abs=1e-12)
    near, far = _target(own, 0.0, -50.0, tid="NEAR"), _target(own, 0.0, 70.0, tid="FAR")
    both, a, b = m.compute(own, DEEP, [far, near]), m.compute(own, DEEP, [near]), m.compute(own, DEEP, [far])
    assert both.Y == pytest.approx(a.Y + b.Y, rel=1e-12) and both.N == pytest.approx(a.N + b.N, rel=1e-12)
    assert both.target_id == "NEAR" and both.count == 2


def test_kernel_image_series_converges_and_matches_deep_water_limit() -> None:
    g6, dg6 = interaction_kernel(2500.0, 12.0, False, 6)
    g60, dg60 = interaction_kernel(2500.0, 12.0, False, 60)
    assert g6 == pytest.approx(g60, rel=1e-3) and dg6 == pytest.approx(dg60, rel=1e-3)  # 尾項為積分近似:6 個鏡像時誤差約 2e-4
    gd, dgd = interaction_kernel(2500.0, 0.0, True, 6)
    assert gd == pytest.approx(2500.0 ** -1.5, rel=1e-15) and dgd == pytest.approx(-1.5 * gd / 2500.0, rel=1e-15)
    assert interaction_kernel(2500.0, 5000.0, False, 6)[0] == pytest.approx(gd, rel=1e-3)
    assert interaction_kernel(1e6, 5.0, False, 6)[0] == pytest.approx(1.0 / (5.0 * 1e6), rel=1e-2)  # 淺水 2D 極限 1/(h r²)
    r2, eps = 3000.0, 1e-3
    fd = (interaction_kernel(r2 + eps, 20.0, False, 6)[0] - interaction_kernel(r2 - eps, 20.0, False, 6)[0]) / (2.0 * eps)
    assert interaction_kernel(r2, 20.0, False, 6)[1] == pytest.approx(fd, rel=1e-6)


def test_hull_profile_and_target_defaults() -> None:
    m = _model()
    assert 0.3 < m.own.le / m.L < 0.45
    own = hull_profile(m.L, m.B, m.T, m.cb, SHIP_INTERACTION_DEFAULTS)
    assert abs(sum(own.a)) < 1e-9 * max(own.a) and sum(own.s) == pytest.approx(m.cb * m.L * m.B * m.T, rel=2e-2)
    assert own.a[-1] > 0.0 and own.a[0] < 0.0
    spec = {"loa": 130, "beam": 22, "draft": 7.5, "shipType": "cargo", "displacement_t": 16490}
    assert target_block_coefficient(spec, 1025.0) == pytest.approx(16490.0 * 1000.0 / (1025.0 * 130.0 * 22.0 * 7.5), rel=1e-12)
    assert target_block_coefficient({"loa": 50, "beam": 10, "draft": 3, "displacement_t": 5000}, 1025.0) == 0.95
    assert target_block_coefficient({"loa": 100, "beam": 20, "draft": 8, "displacement_t": 10}, 1025.0) == 0.35
    assert target_block_coefficient({"shipType": "tanker"}, 1025.0) == 0.82
    assert target_default_cb("CARGO") == 0.72 and target_default_cb("other") == 0.65 and target_default_cb("hsc") == 0.45
    assert target_draft({}) == 5.0 and target_draft({"draft": 0, "beam": 16}) == pytest.approx(4.8, abs=1e-12)


# ---------------------------------------------------------------------------- 情境


@pytest.mark.parametrize(
    ("si", "path", "message"),
    [
        ({"enabled": True, "coefficientScale": -1}, "environment.shipInteraction.coefficientScale", "≥ 0"),
        ({"enabled": True, "eventThreshold_t": -0.5}, "environment.shipInteraction.eventThreshold_t", "≥ 0"),
        ({"enabled": False, "coefficientScale": "x"}, "environment.shipInteraction.coefficientScale", "有限數值"),
        ("x", "environment.shipInteraction.eventThreshold_t", "≥ 0"),
    ],
)
def test_structural_issues_reject_bad_interaction(si: Any, path: str, message: str) -> None:
    tree = _tree(si)
    issues = structural_issues(tree)
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match="environment.shipInteraction"):
        validate_scenario(tree)


def test_valid_interaction_normalises_with_defaults_and_hold_targets() -> None:
    assert structural_issues(_tree({"enabled": True})) == []
    assert structural_issues(_tree({"enabled": True, "coefficientScale": 0, "eventThreshold_t": 0})) == []
    assert structural_issues(_tree({})) == []
    later = dict(OVERTAKER, id="LATER", trigger={"type": "time", "time": 30})
    waypoints = dict(OVERTAKER, id="WP", behaviour={"mode": "waypoints", "waypoints": [{"x": 0, "y": 500}]})
    sc = validate_scenario(_tree({"enabled": True, "coefficientScale": 1.5}, [OVERTAKER, later, waypoints]))
    si = sc["environment"]["shipInteraction"]
    assert si == sc["external"]["shipInteraction"]
    assert si["enabled"] is True and si["coefficientScale"] == 1.5 and si["eventThreshold_t"] is None
    assert [t["id"] for t in si["targets"]] == ["OVT"]  # 有觸發或非 hold 的目標不在參考實作內
    assert si["targets"][0]["displacement_t"] == 16490.0 and si["targets"][0]["initial"]["position"]["x"] == -50.0
    assert validate_scenario(_tree(None, [OVERTAKER]))["environment"]["shipInteraction"] is None
    assert validate_scenario(_tree({"enabled": False, "coefficientScale": 2}, [OVERTAKER]))["environment"]["shipInteraction"] is None
    assert validate_scenario(_tree({"coefficientScale": 2}, [OVERTAKER]))["environment"]["shipInteraction"] is None
    assert normalise_ship_interaction(None, None) is None and normalise_sections({})["shipInteraction"] is None
    m = ShipInteractionModel(load_particulars("FSB1", "full").raw, "full", si)
    assert m.scale == 1.5 and m.event_threshold == pytest.approx(3.0 * TONNE_FORCE) and m.active and len(m.targets) == 1
    assert m.targets[0].cb == pytest.approx(target_block_coefficient(OVERTAKER, 1025.0), rel=1e-12)
    assert not ShipInteractionModel(load_particulars("FSB1", "full").raw, "full", si, supported=False).active


# ---------------------------------------------------------------------------- MMGShip


def test_ship_is_pushed_and_yawed_by_overtaker_and_state_reports_interaction() -> None:
    ship = ship_from_scenario(validate_scenario(_tree({"enabled": True}, [OVERTAKER])))
    calm = ship_from_scenario(validate_scenario(_tree(None, [OVERTAKER])))
    assert ship.interaction is not None and ship.interaction.active and calm.interaction is None
    s0 = ship.state_json()["shipInteraction"]
    assert s0["active"] is True and s0["targetId"] == "OVT" and s0["targets"] == 1 and s0["event"] is False
    assert s0["xi"] == pytest.approx(-200.0 / ship.interaction.L, rel=1e-12) and s0["eta"] == pytest.approx(-50.0 / ship.interaction.L, rel=1e-12)
    assert set(s0) == {"active", "targetId", "forceSurge_t", "forceSway_t", "momentYaw_tm", "xi", "eta", "targets", "event"}
    _steps_to(ship, 3000)  # 60 s
    _steps_to(calm, 3000)
    s60 = ship.state_json()["shipInteraction"]
    assert s60["event"] is True and s60["forceSway_t"] > 0.0 and s60["momentYaw_tm"] < 0.0
    assert abs(math.degrees(ship.state.psi - calm.state.psi)) > 0.5
    assert "shipInteraction" not in calm.state_json()
    r = ship.interaction.compute(ship._kin(), ship._water_depth_or_inf(), [t.kin() for t in ship.interaction.targets])
    assert r == ship.interaction.last
    # 目標船直線等速前進(含零流):12 kn × 60 s
    assert ship.interaction.targets[0].y == pytest.approx(-200.0 + 12.0 * KN_TO_MPS * 60.0, rel=1e-9) and ship.interaction.targets[0].x == -50.0


def test_event_is_raised_once_on_enter_and_once_on_exit() -> None:
    fast = dict(OVERTAKER, initial={"position": {"x": -50, "y": -200}, "heading": 0, "speed": 16})
    ship = ship_from_scenario(validate_scenario(_tree({"enabled": True}, [fast])))
    _steps_to(ship, 6500)  # 130 s:相對速度 8 kn,追越船自後方 200 m 越過到前方 330 m
    evs = _interaction_events(ship)
    assert [e["data"]["phase"] for e in evs] == ["enter", "exit"]
    assert all(e["targetId"] == "OVT" and e["itemId"] is None for e in evs)
    assert evs[0]["data"]["threshold_t"] == 3.0 and evs[0]["tick"] < evs[1]["tick"] and "OVT" in evs[0]["message"]
    assert not ship.interaction.event_active
    none = ship_from_scenario(validate_scenario(_tree({"enabled": True, "eventThreshold_t": 0}, [OVERTAKER])))
    _steps_to(none, 3000)
    assert _interaction_events(none) == []
    zero = ship_from_scenario(validate_scenario(_tree({"enabled": True, "coefficientScale": 0}, [OVERTAKER])))
    _steps_to(zero, 3000)
    assert _interaction_events(zero) == [] and zero.state_json()["shipInteraction"]["active"] is True


def test_without_interaction_outputs_and_hash_are_unchanged() -> None:
    plain = ship_from_scenario(validate_scenario(_tree(None, [OVERTAKER])))
    disabled = ship_from_scenario(validate_scenario(_tree({"enabled": False, "coefficientScale": 2}, [OVERTAKER])))
    far = ship_from_scenario(validate_scenario(_tree({"enabled": True}, [dict(OVERTAKER, initial={"position": {"x": -600, "y": -200}, "heading": 0, "speed": 12})])))
    ref = make_ship("FSB1", "full", seed=7)
    ref.set_environment(wind_speed_kn=0.0, water_depth_m=DEEP)
    ref.reset(0.0, 0.0, 0.0, 8.0, rpm=float(ref.eng["telegraph"]["HAH"]), telegraph="HAH")
    ref.set_origin(23.8, 120.15)
    for s in (plain, disabled, far, ref):
        _steps_to(s, 250)
    assert plain.state_hash() == disabled.state_hash() == ref.state_hash()
    assert "shipInteraction" not in plain.state_json() and "shipInteraction" not in disabled.state_json()
    st = (plain.state.u, plain.state.v, plain.state.r, plain.state.x, plain.state.y, plain.state.psi)
    assert (far.state.u, far.state.v, far.state.r, far.state.x, far.state.y, far.state.psi) == st
    assert far.state_hash() != plain.state_hash()
    fs = far.state_json()["shipInteraction"]
    assert fs["active"] is False and "targetId" not in fs and "xi" not in fs and fs["forceSway_t"] == 0.0
    assert len(far.interaction.hash_bytes()) == 8 * ShipInteractionModel.HASH_LONGS


def test_snapshot_restore_and_reset_rebuild_interaction() -> None:
    ship = ship_from_scenario(validate_scenario(_tree({"enabled": True}, [OVERTAKER])))
    _steps_to(ship, 250)
    snap = ship.snapshot()
    assert not ship.interaction.event_active
    _steps_to(ship, 3000)
    h, last, n_events = ship.state_hash(), ship.interaction.last, len(_interaction_events(ship))
    assert n_events == 1
    ship.restore(snap)
    assert ship.state.tick == 250 and not ship.interaction.event_active and ship.interaction.targets[0].y == pytest.approx(-200.0 + 12.0 * KN_TO_MPS * 5.0, rel=1e-9)
    assert ship.interaction.last == ship.interaction.compute(ship._kin(), ship._water_depth_or_inf(), [t.kin() for t in ship.interaction.targets])
    _steps_to(ship, 3000)
    assert ship.state_hash() == h and ship.interaction.last == last and len(_interaction_events(ship)) == 2
    snap2 = ship.snapshot()
    _steps_to(ship, 3500)
    ship.restore(snap2)
    assert ship.interaction.event_active and ship.interaction.event_target_id == "OVT"
    _steps_to(ship, 3500)
    assert len(_interaction_events(ship)) == 2
    ship.reset(0.0, 0.0, 0.0, 8.0)
    assert ship.interaction is not None and ship.state.tick == 0 and ship.interaction.targets[0].y == -200.0 and not ship.interaction.event_active
    ship.configure_external(None)
    assert ship.interaction is None and "shipInteraction" not in ship.state_json()


# ---------------------------------------------------------------------------- 跨語言夾具


def _export_module():
    spec = importlib.util.spec_from_file_location("export_interaction_reference", GOLDEN / "export_interaction_reference.py")
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def test_golden_force_table_is_reproducible() -> None:
    """純函數取樣表(C# ShipInteractionTests.GoldenForceTableMatchesPythonReference 以相對 1e-12 比對)以同一路徑重算須逐值相同。"""
    text = (GOLDEN / "interaction_force_table.csv").read_text(encoding="utf-8").splitlines()
    rows = list(csv.DictReader(l for l in text if not l.startswith("#")))
    assert len(rows) >= 400
    mod = _export_module()
    got = mod.force_table_rows()
    assert len(got) == len(rows)
    for r, g in zip(rows, got):
        assert (r["ship"], r["loading"]) == (g[0], g[1]) and int(r["active"]) == g[15]
        for key, val in (("xi", g[16]), ("eta", g[17]), ("X_N", g[18]), ("Y_N", g[19]), ("N_Nm", g[20])):
            assert float(r[key]) == val, (r, key)
    assert sum(int(r["active"]) for r in rows) > len(rows) // 2


def test_golden_overtaking_run_is_reproducible() -> None:
    """積分案例(C# 以 < 1% 比對軌跡/力/目標位置、事件 tick 相同)須由目前的 Python 參考實作重現:YAML 一致、數值相對 1e-9、事件相同、末狀態雜湊相同。"""
    mod = _export_module()
    case = mod.RUN_CASE
    expected_yaml = f"# 由 export_interaction_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    assert (GOLDEN / "interaction_overtaking_run.yaml").read_text(encoding="utf-8") == expected_yaml
    lines = (GOLDEN / "interaction_overtaking_run.csv").read_text(encoding="utf-8").splitlines()
    hash_end = next(l.split(":", 1)[1].strip() for l in lines if l.startswith("# state_hash_end:"))
    events = [l[len("# event: "):] for l in lines if l.startswith("# event: ")]
    body = [l for l in lines if not l.startswith("#")]
    cols = body[0].split(",")
    exp = [[float(v) for v in l.split(",")] for l in body[1:]]
    res = mod.run_case(case, write=False)
    got = [[float(v) for v in l.split(",")] for l in res["rows"]]
    assert len(got) == len(exp) == 211
    for j, c in enumerate(cols):
        sc = max(1.0, max(abs(r[j]) for r in exp))
        worst = max(abs(a[j] - b[j]) for a, b in zip(got, exp))
        assert worst <= 1e-9 * sc, f"{c}: 最大差 {worst:.3e}"
    assert res["events"] == events and len(events) == 2 and events[0].endswith("enter OVT") and events[1].endswith("exit OVT")
    if got == exp:
        assert res["hash"] == hash_end
    sc_loaded = load_scenario(GOLDEN / "interaction_overtaking_run.yaml")
    assert sc_loaded["environment"]["shipInteraction"]["targets"][0]["id"] == "OVT" and sc_loaded["environment"]["waterDepth"] == 1000.0
