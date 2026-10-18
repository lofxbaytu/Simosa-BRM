"""外力項參考實作(simosa_brm.external;拖船、錨與懸鏈線、碰墊/碼頭硬接觸/繫纜、岸壁效應)的解析值、引擎接線、
伺服器指令與 C# 一致性黃金檔(tests/SimCore.Tests/Golden/ext_*.csv;C# 端 ExternalForceGoldenTests 讀同一份檔)。"""

from __future__ import annotations

import importlib.util
import math
from pathlib import Path

import pytest

from simosa_brm import external as ex
from simosa_brm.external import Env, Kin
from simosa_brm.mmg import CommandRejected, make_ship
from simosa_brm.paths import repo_root
from simosa_brm.scenario import load_scenario, scenario_from_tree, ship_from_scenario
from simosa_brm.server import SimServer

GOLDEN = repo_root() / "tests" / "SimCore.Tests" / "Golden"
G = ex.GRAVITY


def _raw_fsb1() -> dict:
    return make_ship("FSB1", "full").sp.raw


def _tree(extra: dict, depth: float = 20.0, heading: float = 0.0, speed: float = 0.0, telegraph: str = "STOP",
          wind: float = 0.0, wind_dir: float = 0.0, seabed: str | None = None, x: float = 0.0, y: float = 0.0) -> dict:
    env = {"wind": {"trueSpeed": wind, "trueDir": wind_dir, "gustiness": 0}, "current": {"set": 0, "drift": 0}, "waterDepth": depth}
    if seabed:
        env["seabed"] = seabed
    t = {"id": "T_ext", "seed": 3, "ship": {"id": "FSB1", "loading": "full"}, "origin": {"lat": 23.8, "lon": 120.15},
         "initial": {"position": {"x": x, "y": y}, "heading": heading, "speed": speed, "telegraph": telegraph}, "environment": env}
    t.update(extra)
    return t


def _ship(extra: dict, **kw):
    sc = scenario_from_tree(_tree(extra, **kw))
    return ship_from_scenario(sc), sc


LINEAR_BERTH = {
    "definition": {
        "id": "TB", "quay": [{"x": -200, "y": 30}, {"x": 200, "y": 30}], "waterSide": "right",
        "fenderTypes": {"lin": {"height_m": 1.0, "width_m": 2.5, "ratedReaction_kN": 1000, "ratedDeflection_pct": 72,
                                "curve": [[0, 0], [72, 100]], "friction": 0}},
        "fenders": [{"id": "F1", "type": "lin", "chainage_m": 180}, {"id": "F2", "type": "lin", "chainage_m": 220}],
        "bollards": [{"id": "B1", "chainage_m": 200, "setback_m": 3}, {"id": "B2", "chainage_m": 150, "setback_m": 3}],
    },
    "lines": [{"id": "L1", "fairlead": "midPort", "bollard": "B1", "mbl_t": 50}],
}


# ---------------------------------------------------------------------------- 拖船


def test_tug_force_and_moment_at_attach_point() -> None:
    """推頂 90°(向右舷)於艏部 (0.45 L, 0):Y = F、N = 0.45 L·F;方向 0° 向前時 X = F、N = −y·F。"""
    t = ex.TugUnit(ex._fill({"id": "T", "attach": "fwd", "initial": {"mode": "push", "direction_deg": 90, "power_pct": 100}},
                            ex.TUG_DEFAULTS), 99.0, 18.0, 0.02)
    f = 40.0 * ex.TONNE_FORCE
    X, Y, N = t.force()
    assert X == pytest.approx(0.0, abs=1e-6) and Y == pytest.approx(f) and N == pytest.approx(0.45 * 99.0 * f)
    t2 = ex.TugUnit(ex._fill({"id": "T", "attach": {"x": 10, "y": 5}, "initial": {"mode": "pull", "direction_deg": 0, "power_pct": 50}},
                             ex.TUG_DEFAULTS), 99.0, 18.0, 0.02)
    X, Y, N = t2.force()
    assert (X, Y, N) == pytest.approx((0.5 * f, 0.0, -5.0 * 0.5 * f))


def test_tug_rise_efficiency_mode_change_and_parting() -> None:
    dt = 0.02
    t = ex.TugUnit(ex._fill({"id": "T", "riseTime_s": 30, "modeChange_s": 10, "turnRate_degPerSec": 2,
                             "initial": {"mode": "standby", "direction_deg": 90}}, ex.TUG_DEFAULTS), 99.0, 18.0, dt)
    assert t.command("push", 90, 100) is None and t.status == "working"
    for _ in range(int(30 / dt)):
        t.pre_step(dt, 0.0)
    assert t.force_n == pytest.approx(t.bollard_pull_n * (1 - math.exp(-1.0)), rel=1e-9)  # 一階上升:τ 時 63.2 %
    # 速度效能:V_half 時 0.5、V_full 以下 1
    assert t.speed_efficiency("push", 5.0 * ex.KN_TO_MPS) == pytest.approx(0.5)
    assert t.speed_efficiency("pull", 4.0 * ex.KN_TO_MPS) == 1.0
    # 推 → 拉:換位 10 s 無出力目標;方向以 2°/s 迴轉
    assert t.command("pull", 0, None) is None and t.status == "repositioning"
    for _ in range(int(10 / dt)):
        t.pre_step(dt, 0.0)
    assert t.status == "working"
    assert math.degrees(t.direction) == pytest.approx(70.0, abs=1e-6)  # 90° − 2°/s × 10 s
    # 推 → standby → 拉 仍要換位(審查項目)
    t.command("push", None, None)
    for _ in range(int(10 / dt)):
        t.pre_step(dt, 0.0)
    t.command("standby", None, None)
    assert t.command("pull", None, None) is None and t.status == "repositioning"
    # 拖纜斷裂:拉中者 parted、不能再拉;恢復後 free → 帶纜
    t.status = "working"
    t.part_line()
    assert t.status == "parted" and t.command("pull", None, None) == "tugLineParted"
    t.line_restored()
    assert t.status == "free" and t.command("push", None, None) is None and t.status == "connecting"


# ---------------------------------------------------------------------------- 懸鏈線與錨


@pytest.mark.parametrize("a", [5.0, 50.0, 400.0])
def test_catenary_touchdown_inverts_analytic_distance(a: float) -> None:
    length, h, w = 200.0, 30.0, 300.0
    x = ex.touchdown_distance(a, length, h)
    r = ex.catenary_solve(x, length, h, w, 1e12)
    assert r.regime == "touchdown"
    assert r.H == pytest.approx(w * a, rel=1e-9)
    s = math.sqrt(h * h + 2 * h * a)
    assert r.suspended == pytest.approx(s, rel=1e-9) and r.on_bottom == pytest.approx(length - s, rel=1e-9)
    assert r.top_tension == pytest.approx(w * a + w * h, rel=1e-9)  # T_top = H + w·h


def test_catenary_regimes_and_continuity() -> None:
    length, h, w = 100.0, 20.0, 300.0
    assert ex.catenary_solve(50.0, length, h, w, 1e12).regime == "slack"  # X ≤ L − h
    assert ex.catenary_solve(10.0, 15.0, h, w, 1e12).regime == "hanging"  # L ≤ h
    a_max = ex.max_touchdown_a(length, h)
    x_td = a_max * ex._asinh(length / a_max)
    lo = ex.catenary_solve(x_td * (1 - 1e-9), length, h, w, 1e12)
    hi = ex.catenary_solve(x_td * (1 + 1e-9), length, h, w, 1e12)
    assert lo.regime == "touchdown" and hi.regime == "suspended"
    assert hi.H == pytest.approx(lo.H, rel=1e-5)  # 兩區在 a_max 連續
    sus = ex.catenary_solve(0.5 * (x_td + math.sqrt(length**2 - h**2)), length, h, w, 1e12)
    assert sus.uplift > 0.0 and sus.H > lo.H
    taut = ex.catenary_solve(math.sqrt(length**2 - h**2), length, h, w, 5e5)
    assert taut.regime == "taut" and taut.H == 5e5


def test_anchor_particulars_fsb1_and_holding_power() -> None:
    spec = ex.AnchorParticulars.from_raw(_raw_fsb1())
    assert spec.holding_class == "hhp" and spec.anchor_mass_kg == 2140 and spec.port_length_m == 9 * 27.5
    assert spec.chain_submerged_weight == pytest.approx(38.6 * G * (1 - 1025 / 7850))
    a = ex.AnchorUnit("starboard", spec, 5.69)
    r = ex.CatenaryResult(0.0, 0.0, 50.0, 30.0, 0.0, "touchdown")
    # 錨重 × g × 抓著係數(HHP 泥 5)+ 躺底鏈摩擦 0.6 × w × 躺底長
    want = 2140 * G * 5.0 + 0.6 * spec.chain_submerged_weight * 50.0
    assert a.holding_power(r, "mud", 1.0) == pytest.approx(want)
    # 上拔角 15° 時錨的抓力歸零
    assert a.holding_power(r._replace(uplift=15 * math.pi / 180, on_bottom=0.0), "mud", 1.0) == pytest.approx(0.0, abs=1e-9)


def test_anchor_command_reasons_match_csharp() -> None:
    ship, _ = _ship({})
    with pytest.raises(CommandRejected) as e:
        ship.anchor_command("port", "veer")
    assert e.value.reason == "anchorStowed"
    for side, action, reason in (("bow", "letGo", "unknownAnchor"), ("port", None, "unknownAction"), ("port", "drop", "unknownAction")):
        with pytest.raises(CommandRejected) as e:
            ship.anchor_command(side, action)
        assert e.value.reason == reason
    with pytest.raises(CommandRejected) as e:
        ship.anchor_command("port", "letGo", length_m=-1)
    assert e.value.reason == "invalidTarget"
    assert not ship.external.active
    ev = ship.anchor_command("port", "letGo", target_shackles=3)
    assert ev["kind"] == "anchorLetGo" and ev["data"]["targetLength_m"] == pytest.approx(82.5) and ship.external.active
    with pytest.raises(CommandRejected) as e:
        ship.anchor_command("port", "letGo")
    assert e.value.reason == "anchorAlreadyOut"
    ship.run(60.0)
    kinds = [x["kind"] for x in ship.external_events]
    assert "anchorBroughtUp" in kinds or ship.external.anchor("port").on_bottom
    st = {a["side"]: a for a in ship.state_json()["anchors"]}
    assert st["port"]["length_m"] == pytest.approx(82.5) and st["port"]["status"] == "holding" and "position" in st["port"]
    assert st["starboard"]["status"] == "stowed" and "position" not in st["starboard"]


def test_initial_anchored_tension_is_quarter_of_anchor_holding() -> None:
    ship, _ = _ship({"anchors": {"starboard": {"length_shackles": 5}}}, depth=25.0, seabed="sand")
    a = ship.external.anchor("starboard")
    assert ship.external.active and a.on_bottom and a.mode == "braked"
    r, _, _ = a.solve(ship._kin(), 25.0)
    assert r.H == pytest.approx(0.25 * 2140 * G * 7.0, rel=1e-6)  # HHP 砂 7


# ---------------------------------------------------------------------------- 碰墊、碼頭、繫纜


def test_fender_curve_reaction_energy_and_bottoming() -> None:
    ft = ex.FenderType(ex._fill({"height_m": 2.0, "ratedReaction_kN": 1000, "ratedDeflection_pct": 50,
                                 "curve": [[0, 0], [25, 80], [50, 100]]}, ex.FENDER_TYPE_DEFAULTS))
    assert ft.reaction(0.25) == pytest.approx(400e3)  # 12.5 % 壓縮 = 40 %
    assert ft.reaction(1.0) == pytest.approx(1000e3)
    assert ft.reaction(1.1) == pytest.approx(1000e3 + 20 * 1000e3 / 2.0 * 0.1)  # 觸底剛度 20 R/H
    assert ft.energy(1.0) == pytest.approx(0.5 * 800e3 * 0.5 + 0.5 * (800e3 + 1000e3) * 0.5)
    assert ft.rated_energy == pytest.approx(ft.energy(1.0))


def test_mooring_line_tension_and_strain_inverse() -> None:
    ln = ex.MooringLine(ex._fill({"id": "L", "fairlead": "fwd"}, ex.MOORING_LINE_DEFAULTS), 99.0, 18.0)
    ln.status = "fast"
    ln.rest_length = 100.0
    eps = ln.strain_for(10 * ex.TONNE_FORCE)
    assert ln.static_tension(100.0 * (1 + eps)) == pytest.approx(10 * ex.TONNE_FORCE, rel=1e-12)
    assert ln.static_tension(100.0 * (1 + 0.18)) == pytest.approx(ln.mbl)  # 斷裂應變時 = MBL
    assert ln.tension(99.0, 0.0) == 0.0  # 只拉不推
    assert ln.tension(110.0, 1.0) > ln.tension(110.0, 0.0)  # 伸長速率阻尼


def test_linear_fender_spring_and_line_force_at_rest() -> None:
    """東西向碼頭 y = 30、航向 090 左舷靠:左舷壓入 δ 時兩碰墊各 δ/0.72 × 1000 kN(線性曲線),合力向南、無力矩。"""
    ship, _ = _ship({"berth": LINEAR_BERTH}, heading=90.0, y=20.2)
    m = ship.external
    k = ship._kin()
    X, Y, N = m.berth.force(k, Env(0.0, 0.0, 20.0))
    delta = 1.0 - (30.0 - (20.2 + 9.0))
    assert Y == pytest.approx(2 * delta / 0.72 * 1000e3, rel=1e-9)  # 船體座標 +y(右舷)= 南,推離碼頭
    assert X == pytest.approx(0.0, abs=1e-6) and N == pytest.approx(0.0, abs=1e-3)
    # 繫纜
    with pytest.raises(CommandRejected) as e:
        ship.mooring_command("L1", "heave")
    assert e.value.reason == "mooringLineNotFast"
    with pytest.raises(CommandRejected) as e:
        ship.mooring_command("L1", "makeFast", bollard="B9")
    assert e.value.reason == "unknownBollard"
    with pytest.raises(CommandRejected) as e:
        ship.mooring_command("XX", "makeFast")
    assert e.value.reason == "unknownMooringLine"
    ev = ship.mooring_command("L1", "makeFast", tension_t=3)
    assert ev["kind"] == "mooringCommand" and ev["data"]["status"] == "fast"
    line = m.berth.lines[0]
    dist, _ = m.berth.line_geometry(line, k, Env(0.0, 0.0, 20.0))
    assert line.static_tension(dist) == pytest.approx(3 * ex.TONNE_FORCE, rel=1e-9)


def test_quay_hard_contact_event_and_no_berth_rejections() -> None:
    ship, _ = _ship({"berth": dict(LINEAR_BERTH, definition=dict(LINEAR_BERTH["definition"], fenders=[]))}, heading=90.0, y=21.5)
    ship.step()
    assert ship.external.berth.quay_in_contact and any(e["kind"] == "quayContact" for e in ship.external_events)
    plain, _ = _ship({})
    with pytest.raises(CommandRejected) as e:
        plain.mooring_command("L1", "makeFast")
    assert e.value.reason == "noBerth"
    with pytest.raises(CommandRejected) as e:
        plain.tug_control("TG1", "push")
    assert e.value.reason == "unknownTug"


# ---------------------------------------------------------------------------- 岸壁效應


def _bank_model(lines: list[dict], scale: float = 1.0) -> ex.BankEffectModel:
    ship = make_ship("FSB1", "full")
    sec = ex.normalise_sections({"banks": {"lines": lines, "scale": scale}})
    return ex.BankEffectModel(ship.sp.raw, "full", sec["banks"], ship.ltp)


def test_bank_symmetric_canal_cancels_and_force_scales_with_u_squared() -> None:
    east = {"id": "E", "line": [{"x": 40, "y": -500}, {"x": 40, "y": 500}], "waterSide": "left"}
    west = {"id": "W", "line": [{"x": -40, "y": -500}, {"x": -40, "y": 500}], "waterSide": "right"}
    canal = _bank_model([east, west])
    r = canal.compute(Kin(0, 0, 0, 3.0, 0, 0), 10.0, 0.0, True)
    assert r.Y == pytest.approx(0.0, abs=1e-6) and r.N == pytest.approx(0.0, abs=1e-6) and r.D == pytest.approx(0.0, abs=1e-12)
    one = _bank_model([east])
    r1 = one.compute(Kin(0, 0, 0, 2.0, 0, 0), math.inf, 0.0, True)
    r2 = one.compute(Kin(0, 0, 0, 4.0, 0, 0), math.inf, 0.0, True)
    assert r1.Y > 0.0 and r1.N < 0.0  # 右舷岸:吸向岸(+Y)、艏離岸(N 向左)
    assert r2.Y == pytest.approx(4.0 * r1.Y, rel=1e-6)  # 深水(Fnh ≈ 0、影響寬度不變)∝ u²
    assert r1.starboard_m == pytest.approx(40.0) and r1.port_m is None
    # 平行岸壁:C_Y = c_lead + c_trail、C_N = −(ℓ_lead·c_lead + ℓ_trail·c_trail)
    c = ex.BANK_DEFAULTS
    q = 0.5 * c["Rho"] * one.L * one.T * 4.0
    assert r1.d_fore == pytest.approx(r1.d_aft, rel=1e-12)
    assert r1.Y == pytest.approx(q * (c["CYLead"] + c["CYTrail"]) * r1.d_fore, rel=1e-9)
    assert r1.N == pytest.approx(q * one.L * (c["LeverLead"] * c["CYLead"] + c["LeverTrail"] * c["CYTrail"]) * r1.d_fore, rel=1e-9)
    assert _bank_model([east], scale=0.0).compute(Kin(0, 0, 0, 3.0, 0, 0), 10.0, 0.0, False).Y == 0.0


def test_bank_depth_ratio_factor_and_propeller_term() -> None:
    east = {"id": "E", "line": [{"x": 40, "y": -500}, {"x": 40, "y": 500}], "waterSide": "left"}
    m = _bank_model([east])
    k = Kin(0, 0, 0, 1e-9, 0, 0)  # 幾乎靜止:只剩螺槳項
    r = m.compute(k, math.inf, 120.0, True)
    nd = 120.0 / 60.0 * m.prop_d
    c = ex.BANK_DEFAULTS
    assert r.Y == pytest.approx(0.5 * c["Rho"] * m.L * m.T * nd * nd * c["CYP"] * r.d_aft, rel=1e-6)
    deep = m.compute(Kin(0, 0, 0, 2.0, 0, 0), math.inf, 0.0, True)
    h = 2.0 * m.T
    shallow = m.compute(Kin(0, 0, 0, 2.0, 0, 0), h, 0.0, True)
    assert shallow.depth_ratio == pytest.approx(2.0)
    # F_Y = 1 + a_Y·T/(h − T) = 1.4(h/T = 2);Fnh 改變影響寬度,故比較 D 正規化後的值
    assert (shallow.Y / shallow.d_fore) / (deep.Y / deep.d_fore) == pytest.approx(1.0 + c["AY"], rel=1e-9)


# ---------------------------------------------------------------------------- 引擎接線、雜湊、快照


def test_unused_external_keeps_legacy_hash_and_state() -> None:
    """未使用外力的情境:外力不啟用、雜湊與「無外力模組」逐位元相同(舊情境雜湊不變)。"""
    sc = load_scenario("E01_baseline")
    a = ship_from_scenario(sc)
    b = ship_from_scenario(sc)
    b.external = None
    b.bank = None
    for _ in range(300):
        a.step()
        b.step()
    assert not a.external_active
    assert a.state_hash() == b.state_hash()
    assert "anchors" in a.state_json() and "tugs" not in a.state_json() and "berth" not in a.state_json()


def test_external_state_in_hash_snapshot_and_replay() -> None:
    sc = scenario_from_tree(_tree({"tugs": [{"id": "TG1", "attach": "fwdStbd", "initial": {"mode": "push", "direction_deg": 270, "power_pct": 60}}],
                                   "berth": LINEAR_BERTH}, heading=90.0, y=10.0))
    a = ship_from_scenario(sc)
    for _ in range(200):
        a.step()
    snap = a.snapshot()
    h0 = a.state_hash()
    for _ in range(300):
        a.step()
    h1 = a.state_hash()
    a.restore(snap)
    assert a.state_hash() == h0
    for _ in range(300):
        a.step()
    assert a.state_hash() == h1
    b = ship_from_scenario(sc)
    for _ in range(500):
        b.step()
    assert b.state_hash() == h1
    # 外力狀態進雜湊:改拖船出力令 → 雜湊不同,但運動狀態在下令當下尚未改變
    c = ship_from_scenario(sc)
    d = ship_from_scenario(sc)
    d.tug_control("TG1", power_pct=61)
    assert (c.state.x, c.state.y) == (d.state.x, d.state.y) and c.state_hash() != d.state_hash()


# ---------------------------------------------------------------------------- 伺服器指令


def test_server_external_commands_and_state() -> None:
    sc = scenario_from_tree(_tree({"tugs": [{"id": "TG1", "modes": ["push"]}], "berth": LINEAR_BERTH}, heading=90.0, y=10.0))
    srv = SimServer(ship_from_scenario(sc), scenario=sc)
    ack = srv.apply_command({"type": "tugControl", "args": {"id": "TG1", "mode": "push", "direction_deg": 270, "power_pct": 50}})
    assert ack["ok"] and ack["event"]["kind"] == "tugCommand" and ack["event"]["data"]["power_pct"] == pytest.approx(50.0)
    for args, reason in (({"id": "TG9"}, "unknownTug"), ({"id": "TG1", "mode": "tow"}, "unknownTugMode"),
                         ({"id": "TG1", "power_pct": 120}, "invalidPower"), ({"id": "TG1", "mode": "pull"}, "tugModeUnavailable")):
        ack = srv.apply_command({"type": "tugControl", "args": args})
        assert not ack["ok"] and f"拒絕({reason})" in ack["detail"], (args, ack)
    ack = srv.apply_command({"type": "mooring", "args": {"lineId": "L1", "action": "makeFast"}})
    assert ack["ok"] and ack["event"]["data"]["bollardId"] == "B1"
    ack = srv.apply_command({"type": "anchor", "args": {"side": "starboard", "action": "letGo", "length_m": 60}})
    assert ack["ok"] and ack["event"]["kind"] == "anchorLetGo"
    ack = srv.apply_command({"type": "anchor", "args": {"side": "port", "action": "heave"}})
    assert not ack["ok"] and "anchorStowed" in ack["detail"]
    ack = srv.apply_command({"type": "injectFault", "value": "tugLineBreak", "args": {"args": {"id": "TG7"}}})
    assert not ack["ok"] and "unknownTug" in ack["detail"] and "tugLineBreak" not in srv.ship.faults
    for _ in range(100):
        srv.ship.step()
    st = srv.state_json()
    assert st["tugs"][0]["mode"] == "push" and st["tugs"][0]["force_t"] > 0 and "pos" in st["tugs"][0]
    assert st["berth"]["id"] == "TB" and st["berth"]["lines"][0]["status"] == "fast" and st["berth"]["lines"][0]["bollardId"] == "B1"
    assert {a["side"] for a in st["anchors"]} == {"port", "starboard"}
    ack = srv.apply_command({"type": "injectFault", "value": "tugLineBreak", "args": {"args": {"id": "TG1"}}})
    assert ack["ok"] and ack["event"]["kind"] == "tugLineParted" and ack["event"]["data"]["stillPushing"] is True
    ack = srv.apply_command({"type": "clearFault", "value": "tugLineBreak"})
    assert ack["ok"] and not srv.ship.external.tug("TG1").line_parted
    # 無參數 reset:回到情境初始(拖船 standby、繫纜收存)
    srv.apply_command({"type": "reset"})
    assert srv.ship.external.tug("TG1").status == "standby" and srv.ship.external.berth.lines[0].status == "stowed"


@pytest.mark.parametrize("sid", ["E12_anchoring_kaohsiung", "E13_tug_berthing_mailiao", "E14_unberthing_no_tug", "E06_taichung_channel_flood"])
def test_library_scenarios_load_external_sections(sid: str) -> None:
    sc = load_scenario(sid)
    ship = ship_from_scenario(sc)
    for _ in range(100):
        ship.step()
    st = ship.state_json()
    sec = sc["external"]
    if sec["tugs"]:
        assert [t["id"] for t in st["tugs"]] == [t["id"] for t in sec["tugs"]]
    if sec["berth"]:
        assert st["berth"]["id"] == sec["berth"]["definition"]["id"] and len(st["berth"]["lines"]) == len(sec["berth"]["lines"])
    if sec["banks"]:
        assert ship.bank is not None and ship.bank.active and "bank" in st


def test_external_structure_errors() -> None:
    with pytest.raises(ValueError, match="bollardPull_t"):
        scenario_from_tree(_tree({"tugs": [{"id": "T", "bollardPull_t": 0}]}))
    with pytest.raises(ValueError, match="繫船柱不存在"):
        scenario_from_tree(_tree({"berth": dict(LINEAR_BERTH, lines=[{"id": "L", "fairlead": "fwd", "bollard": "BX"}])}))
    with pytest.raises(ValueError, match="seabed"):
        scenario_from_tree(_tree({}, seabed="gravel"))
    with pytest.raises(ValueError, match="slope"):
        scenario_from_tree(_tree({"banks": {"lines": [{"id": "S", "type": "sloped", "line": [{"x": 0, "y": 0}, {"x": 0, "y": 10}]}]}}))


# ---------------------------------------------------------------------------- C# 一致性黃金檔(Python 端:重跑須與檔案一致)


def _load_export_module():
    spec = importlib.util.spec_from_file_location("export_external_reference", GOLDEN / "export_external_reference.py")
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def _read_csv(path: Path) -> tuple[list[str], list[list[float]], str]:
    cols: list[str] | None = None
    rows: list[list[float]] = []
    h = ""
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("#"):
            if line.startswith("# state_hash_end:"):
                h = line.split(":", 1)[1].strip()
            continue
        if cols is None:
            cols = line.split(",")
        else:
            rows.append([float(v) for v in line.split(",")])
    assert cols is not None
    return cols, rows, h


@pytest.mark.parametrize("name", ["ext_tug_push_turn", "ext_anchor_wind_drag", "ext_berth_fender_lines", "ext_bank_suction"])
def test_golden_external_reference_is_reproducible(name: str) -> None:
    """黃金檔(C# ExternalForceGoldenTests 的比對基準)須由目前的 Python 參考實作重現:欄位逐值相對 1e-9(平台浮點差容許)。
    失敗表示 Python 參考實作或係數改變,需重新執行 export_external_reference.py 並確認 C# 測試。"""
    mod = _load_export_module()
    case = next(c for c in mod.CASES if c["name"] == name)
    expected_yaml = f"# 由 export_external_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    assert (GOLDEN / f"{name}.yaml").read_text(encoding="utf-8") == expected_yaml
    res = mod.run_case(case, write=False)
    cols, rows, _ = _read_csv(GOLDEN / f"{name}.csv")
    got = [[float(v) for v in r.split(",")] for r in res["rows"]]
    assert len(got) == len(rows)
    for j, c in enumerate(cols):
        sc = max(1.0, max(abs(r[j]) for r in rows))
        worst = max(abs(g[j] - r[j]) for g, r in zip(got, rows))
        assert worst <= 1e-9 * sc, f"{name} {c}: 最大差 {worst:.3e}"


@pytest.mark.parametrize("value, bad", [(0, True), (-5, True), (float("inf"), True), (20, False)])
def test_berth_departed_distance_must_be_positive(value, bad) -> None:
    """berth.departedDistance_m 給定時須為有限正數(同 C# ScenarioExternalForces 結構檢查)。"""
    from simosa_brm.external import structural_issues
    paths = [p for p, _ in structural_issues({"berth": {"departedDistance_m": value}})]
    assert ("berth.departedDistance_m" in paths) is bad
