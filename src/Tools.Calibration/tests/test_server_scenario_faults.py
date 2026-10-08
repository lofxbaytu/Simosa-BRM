"""WebSocket 服務命令(不開 socket,直接呼叫 SimServer.apply_command):loadScenario、clearFault、三種會改變行為的故障、
setEnvironment 的陣風/能見度、快照含故障與亂數。與 C# SimCore 語意相同(FaultNames.cs、SimulationEngine.Initialize)。"""

from __future__ import annotations

import textwrap
from pathlib import Path

import pytest

from simosa_brm.mmg import (
    FAULT_BOW_THRUSTER,
    FAULT_MAIN_ENGINE,
    FAULT_STEERING_GEAR,
    ForceBreakdown,
    LocalTangentPlane,
    make_ship,
)
from simosa_brm.scenario import load_scenario, resolve_scenario_path, scenarios_dir, validate_scenario
from simosa_brm.server import SimServer


def _server(speed_kn: float = 0.0) -> SimServer:
    ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    ship.reset(speed_kn=speed_kn)
    return SimServer(ship, 1.0)


# ---------------------------------------------------------------- loadScenario


def test_load_scenario_e01_baseline() -> None:
    srv = _server(speed_kn=10.0)
    old = srv.ship
    old.set_rudder(10.0)
    old.run(2.0)
    assert old.state.tick == 100
    srv.apply_command({"type": "snapshot", "value": "before"})
    srv.apply_command({"type": "timeScale", "value": 5})

    ack = srv.apply_command({"type": "loadScenario", "value": "E01_baseline"})
    assert ack["ok"], ack
    assert "E01_baseline" in ack["detail"] and ack["tick"] == 0
    assert srv.ship is not old
    assert srv.scenario is not None and srv.scenario["id"] == "E01_baseline" and srv.scenario["seed"] == 20260101
    s = srv.ship.state_json()
    assert s["shipId"] == "FSB1" and s["loading"] == "ballast"
    assert s["tick"] == 0 and s["t"] == 0.0
    assert s["heading"] == pytest.approx(0.0) and s["stw"] == pytest.approx(7.8)
    assert s["telegraph"] == "HAH" and s["rpm"] == pytest.approx(s["rpmOrder"]) and s["rpm"] > 0
    assert s["rudder"] == 0.0 and s["rudderOrder"] == 0.0 and s["faults"] == []
    assert s["wind"]["trueSpeed"] == pytest.approx(15.0) and s["wind"]["trueDir"] == pytest.approx(45.0)
    assert s["current"]["set"] == pytest.approx(200.0) and s["current"]["drift"] == pytest.approx(0.5)
    assert s["waterDepth"] == 30.0
    assert s["pos"]["x"] == 0.0 and s["pos"]["y"] == 0.0
    assert s["pos"]["lat"] == pytest.approx(23.80) and s["pos"]["lon"] == pytest.approx(120.05)
    assert srv.ship.env.gustiness == pytest.approx(0.1) and srv.ship.seed == 20260101
    assert srv.time_scale == 1.0  # 情境 timeScale 取代先前的 5
    assert srv.snapshots == {}  # 快照屬於舊模型,已清空
    assert srv.ship.state_hash() != ""
    # 之後的命令作用在新模型
    assert srv.apply_command({"type": "rudder", "value": -15})["ok"]
    srv.ship.run(1.0)
    assert srv.ship.state_json()["tick"] == 50 and srv.ship.state_json()["rudderOrder"] == -15.0


def test_load_scenario_web_station_command_shape() -> None:
    """教官站送 value=路徑、args={id, path}(instructor-command.ts loadScenarioCommand)。"""
    srv = _server()
    cmd = {"type": "loadScenario", "value": "data/scenarios/E01_baseline.yaml",
           "args": {"id": "E01_baseline", "path": "data/scenarios/E01_baseline.yaml"}}
    assert srv.apply_command(cmd)["ok"]
    assert srv.ship.sp.loading.name == "ballast"
    # 路徑錯但 args.id 對:仍載入
    srv2 = _server()
    ack = srv2.apply_command({"type": "loadScenario", "value": "nowhere/E01.yaml", "args": {"id": "E01_baseline"}})
    assert ack["ok"] and srv2.scenario["id"] == "E01_baseline"


def test_load_scenario_unknown_id_keeps_model() -> None:
    srv = _server(speed_kn=8.0)
    old = srv.ship
    old.run(1.0)
    ack = srv.apply_command({"type": "loadScenario", "value": "no_such_scenario"})
    assert ack["ok"] is False and "no_such_scenario" in ack["detail"]
    assert srv.ship is old and srv.scenario is None and old.state.tick == 50
    assert ack["tick"] == 50
    assert srv.apply_command({"type": "loadScenario"})["ok"] is False
    assert srv.apply_command({"type": "loadScenario", "value": ""})["ok"] is False
    assert srv.apply_command({"type": "loadScenario", "value": 42})["ok"] is False
    # 模型仍可用
    assert srv.apply_command({"type": "rudder", "value": 5})["ok"]


def test_resolve_scenario_path_variants(tmp_path: Path) -> None:
    e01 = scenarios_dir() / "E01_baseline.yaml"
    assert resolve_scenario_path("E01_baseline") == e01.resolve()
    assert resolve_scenario_path("E01_baseline.yaml") == e01.resolve()
    assert resolve_scenario_path(str(e01)) == e01.resolve()
    assert resolve_scenario_path("data/scenarios/E01_baseline.yaml") == e01.resolve()
    with pytest.raises(FileNotFoundError):
        resolve_scenario_path("")
    with pytest.raises(FileNotFoundError):
        resolve_scenario_path(str(tmp_path / "missing.yaml"))


def _write_yaml(path: Path, text: str) -> Path:
    path.write_text(textwrap.dedent(text), encoding="utf-8")
    return path


def test_scenario_latlon_to_enu_matches_local_tangent_plane(tmp_path: Path) -> None:
    p = _write_yaml(tmp_path / "T01_offset.yaml", """
        ship: { id: FSB1, loading: full }
        seed: 7
        timeScale: 2
        origin: { lat: 23.80, lon: 120.05 }
        initial:
          position: { lat: 23.81, lon: 120.06 }
          heading: 275.5
          speed: 5
          rpm: 60
          rudder: -10
        environment:
          wind: { trueSpeed: 10, trueDir: 400, gustiness: 2 }
          current: { set: -90, drift: 1 }
          waterDepth: 15
          visibility_nm: 2.5
    """)
    srv = _server()
    ack = srv.apply_command({"type": "loadScenario", "value": str(p)})
    assert ack["ok"], ack
    sc = srv.scenario
    assert sc["id"] == "T01_offset"  # id 未填時取檔名
    assert sc["timeScale"] == 2.0 and srv.time_scale == 2.0 and srv.ship.seed == 7
    ltp = LocalTangentPlane(23.80, 120.05)
    x, y = ltp.to_local(23.81, 120.06)
    s = srv.ship.state_json()
    assert s["pos"]["x"] == pytest.approx(x) and s["pos"]["y"] == pytest.approx(y)
    assert 900.0 < x < 1100.0 and 1050.0 < y < 1150.0  # 約 0.01° ≈ 1.02 km(東)、1.11 km(北)
    assert s["pos"]["lat"] == pytest.approx(23.81, abs=1e-9) and s["pos"]["lon"] == pytest.approx(120.06, abs=1e-9)
    assert s["heading"] == pytest.approx(275.5) and s["stw"] == pytest.approx(5.0)
    assert s["rpm"] == pytest.approx(60.0) and s["rpmOrder"] == pytest.approx(60.0) and s["telegraph"] != "STOP"
    assert s["rudder"] == pytest.approx(-10.0) and s["rudderOrder"] == pytest.approx(-10.0)
    assert s["wind"]["trueDir"] == pytest.approx(40.0) and s["current"]["set"] == pytest.approx(270.0)
    assert srv.ship.env.gustiness == 1.0 and srv.ship.env.visibility_nm == 2.5 and s["waterDepth"] == 15.0


def test_scenario_validation_errors(tmp_path: Path) -> None:
    base = {"id": "X", "ship": {"id": "FSB1"}, "initial": {"position": {"lat": 23.8, "lon": 120.0}, "heading": 0},
            "environment": {"waterDepth": 20}}
    sc = validate_scenario(base)
    assert sc["ship"]["loading"] == "ballast" and sc["origin"] == {"lat": 23.8, "lon": 120.0} and sc["seed"] == 1
    with pytest.raises(ValueError, match="origin"):
        validate_scenario({**base, "initial": {"position": {"x": 0, "y": 0}, "heading": 0}})
    with pytest.raises(ValueError, match="waterDepth"):
        validate_scenario({**base, "environment": {"waterDepth": 0}})
    with pytest.raises(ValueError, match="ship.id"):
        validate_scenario({**base, "ship": {}})
    with pytest.raises(ValueError, match="telegraph"):
        validate_scenario({**base, "initial": {**base["initial"], "telegraph": "WARP"}})
    # 船型與係數檔車鐘表不符 / 裝載不存在 → ack ok=false,模型不變
    p = _write_yaml(tmp_path / "bad_loading.yaml", """
        id: bad
        ship: { id: FSB1, loading: intermediate }
        initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0 }
        environment: { waterDepth: 20 }
    """)
    srv = _server()
    old = srv.ship
    ack = srv.apply_command({"type": "loadScenario", "value": str(p)})
    assert ack["ok"] is False and srv.ship is old
    assert load_scenario("E01_baseline")["path"].endswith("E01_baseline.yaml")


def test_reset_after_scenario_returns_to_initial_state() -> None:
    srv = _server()
    assert srv.apply_command({"type": "loadScenario", "value": "E01_baseline"})["ok"]
    ship = srv.ship
    srv.apply_command({"type": "rudder", "value": 20})
    srv.apply_command({"type": "injectFault", "value": FAULT_MAIN_ENGINE})
    ship.run(5.0)
    assert ship.state.tick == 250 and ship.state.psi != 0.0
    ack = srv.apply_command({"type": "reset"})
    assert ack["ok"] and "E01_baseline" in ack["detail"]
    s = ship.state_json()
    assert s["tick"] == 0 and s["heading"] == 0.0 and s["stw"] == pytest.approx(7.8) and s["faults"] == [] and s["rudderOrder"] == 0.0
    # 有參數的 reset:自船覆寫(經緯度經 LTP 轉本地)
    ack = srv.apply_command({"type": "reset", "args": {"lat": 23.81, "lon": 120.05, "heading": 90, "speed": 3, "tugs": 1}})
    assert ack["ok"] and "tugs" in ack["detail"]
    s = ship.state_json()
    assert s["pos"]["x"] == pytest.approx(0.0) and 1050.0 < s["pos"]["y"] < 1150.0 and s["heading"] == 90.0 and s["stw"] == pytest.approx(3.0)


# ---------------------------------------------------------------- clearFault


def test_clear_fault_with_and_without_name() -> None:
    srv = _server()
    ship = srv.ship
    ack = srv.apply_command({"type": "injectFault", "value": "gyroDrift"})
    assert ack["ok"] and "僅記錄" in ack["detail"]
    ack = srv.apply_command({"type": "injectFault", "value": FAULT_STEERING_GEAR})
    assert ack["ok"] and "已套用" in ack["detail"]
    assert srv.apply_command({"type": "injectFault", "value": FAULT_STEERING_GEAR})["ok"]  # 不重複
    assert ship.faults == ["gyroDrift", FAULT_STEERING_GEAR]
    assert srv.apply_command({"type": "injectFault"})["ok"] is False
    assert srv.apply_command({"type": "injectFault", "value": " "})["ok"] is False

    ack = srv.apply_command({"type": "clearFault", "value": "gyroDrift"})
    assert ack["ok"] and ship.faults == [FAULT_STEERING_GEAR]
    ack = srv.apply_command({"type": "clearFault", "value": "notThere"})
    assert ack["ok"] and "不在" in ack["detail"] and ship.faults == [FAULT_STEERING_GEAR]

    srv.apply_command({"type": "injectFault", "value": FAULT_MAIN_ENGINE})
    srv.apply_command({"type": "injectFault", "value": FAULT_BOW_THRUSTER})
    assert len(ship.faults) == 3
    ack = srv.apply_command({"type": "clearFault"})  # 無 value:清除全部(C# 語意)
    assert ack["ok"] and "3" in ack["detail"] and ship.faults == []
    srv.apply_command({"type": "injectFault", "value": FAULT_MAIN_ENGINE})
    assert srv.apply_command({"type": "clearFault", "value": None})["ok"] and ship.faults == []
    srv.apply_command({"type": "injectFault", "value": FAULT_MAIN_ENGINE})
    assert srv.apply_command({"type": "clearFault", "value": ""})["ok"] and ship.faults == []
    assert ship.state_json()["faults"] == []


# ---------------------------------------------------------------- 故障行為


def test_steering_gear_fault_holds_rudder() -> None:
    srv = _server(speed_kn=10.0)
    ship = srv.ship
    srv.apply_command({"type": "rudder", "value": 10})
    ship.run(8.0)
    held = ship.state_json()["rudder"]
    assert held == pytest.approx(10.0, abs=0.05)
    srv.apply_command({"type": "injectFault", "value": FAULT_STEERING_GEAR})
    assert srv.apply_command({"type": "rudder", "value": 20})["ok"]
    ship.run(10.0)
    s = ship.state_json()
    assert s["rudder"] == pytest.approx(held, abs=1e-9)  # 舵角不動
    assert s["rudderOrder"] == 20.0 and s["faults"] == [FAULT_STEERING_GEAR]
    # 自動舵也動不了舵
    srv.apply_command({"type": "autopilot", "args": {"enabled": True, "heading": 90}})
    ship.run(5.0)
    assert ship.state_json()["rudder"] == pytest.approx(held, abs=1e-9)
    srv.apply_command({"type": "clearFault", "value": FAULT_STEERING_GEAR})
    srv.apply_command({"type": "rudder", "value": 20})
    ship.run(10.0)
    assert ship.state_json()["rudder"] == pytest.approx(20.0, abs=0.05)


def test_main_engine_fault_decays_rpm_and_reports_failed() -> None:
    srv = _server(speed_kn=10.0)
    ship = srv.ship
    rpm0 = ship.state_json()["rpm"]
    assert rpm0 > 20.0
    srv.apply_command({"type": "injectFault", "value": FAULT_MAIN_ENGINE})
    s = ship.state_json()
    assert s["engine"]["state"] == "failed" and s["rpm"] == pytest.approx(rpm0)
    ship.run(120.0)
    s = ship.state_json()
    assert s["engine"]["state"] == "failed"
    assert abs(s["rpm"]) < 0.5  # 停俥時間常數 20 s → 120 s 後 < 0.3 %
    assert s["rpmOrder"] == pytest.approx(rpm0) and s["telegraph"] != "STOP"  # 車鐘/轉速令照記
    assert ship.state_json()["stw"] < 10.0  # 減速中
    # 車鐘改令也無效
    srv.apply_command({"type": "telegraph", "value": "FAH"})
    ship.run(30.0)
    assert abs(ship.state_json()["rpm"]) < 0.5
    # 清除後依起動延遲重新起動
    srv.apply_command({"type": "clearFault", "value": FAULT_MAIN_ENGINE})
    assert ship.state_json()["engine"]["state"] != "failed"
    ship.run(60.0)
    assert ship.state_json()["rpm"] > 20.0 and ship.state_json()["engine"]["state"] == "running"


def test_bow_thruster_fault_gives_no_lateral_force() -> None:
    faulted, healthy = _server(), _server()
    assert faulted.ship.thr_installed
    faulted.apply_command({"type": "injectFault", "value": FAULT_BOW_THRUSTER})
    for srv in (faulted, healthy):
        assert srv.apply_command({"type": "thruster", "value": 1.0})["ok"]
        srv.ship.run(30.0)
    sf, sh = faulted.ship.state_json(), healthy.ship.state_json()
    assert sf["thruster"]["order"] == 1.0 and sf["thruster"]["actual"] == 0.0
    assert sf["r"] == 0.0 and sf["v"] == 0.0 and sf["heading"] == 0.0
    assert sh["thruster"]["actual"] > 0.5 and abs(sh["r"]) > 1e-4  # 對照:正常側推會迴轉
    b = ForceBreakdown()
    st = faulted.ship.state
    faulted.ship.forces(st.u, st.v, st.r, st.delta, st.n, st.thr, st.psi, breakdown=b)
    assert b.thruster == (0.0, 0.0, 0.0)
    # 側推已在出力時故障:實際推力依延遲衰減到 0
    healthy.apply_command({"type": "injectFault", "value": FAULT_BOW_THRUSTER})
    healthy.ship.run(90.0)
    assert abs(healthy.ship.state_json()["thruster"]["actual"]) < 1e-3


# ---------------------------------------------------------------- 環境、陣風、快照


def test_set_environment_gustiness_and_visibility() -> None:
    srv = _server()
    ship = srv.ship
    ack = srv.apply_command({"type": "setEnvironment", "args": {"wind": {"trueSpeed": 20, "trueDir": 40, "gustiness": 0.25},
                                                                  "waterDepth": 12, "visibility_nm": 5}})
    assert ack["ok"] and "能見度" in ack["detail"] and "陣風" in ack["detail"]
    assert ship.env.gustiness == 0.25 and ship.env.visibility_nm == 5.0 and ship.env.water_depth == 12.0
    assert srv.apply_command({"type": "setEnvironment", "args": {"visibility": 1.5}})["ok"] and ship.env.visibility_nm == 1.5
    assert srv.apply_command({"type": "setEnvironment", "args": {"waterDepth": 0}})["ok"] and ship.env.water_depth == 12.0  # 非正忽略
    assert srv.apply_command({"type": "setEnvironment", "args": {"wind": {"gustiness": 3}}})["ok"] and ship.env.gustiness == 1.0
    assert "visibility" not in ship.state_json()  # state.schema.json 無此欄位,不新增


def test_gust_is_deterministic_by_seed() -> None:
    def run(seed: int, gust: float) -> tuple[str, list[float]]:
        ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False, seed=seed)
        ship.reset(speed_kn=8.0)
        ship.set_environment(wind_speed_kn=20.0, wind_dir_deg=90.0, gustiness=gust)
        speeds = []
        for _ in range(5):
            ship.run(1.0)
            speeds.append(ship.state_json()["wind"]["trueSpeed"])
        return ship.state_hash(), speeds

    h1, w1 = run(20260101, 0.3)
    h2, w2 = run(20260101, 0.3)
    h3, w3 = run(20260102, 0.3)
    assert h1 == h2 and w1 == w2
    assert h1 != h3
    assert all(14.0 <= w <= 26.0 for w in w1) and len(set(w1)) > 1  # 1 ± 0.3 內且每秒變化
    _, w0 = run(20260101, 0.0)
    assert all(w == pytest.approx(20.0) for w in w0)  # 無陣風:有效風速恆為設定值


def test_snapshot_restores_faults_and_random_state() -> None:
    srv = _server(speed_kn=8.0)
    ship = srv.ship
    srv.apply_command({"type": "setEnvironment", "args": {"wind": {"trueSpeed": 15, "trueDir": 60, "gustiness": 0.3}}})
    srv.apply_command({"type": "injectFault", "value": "gyroDrift"})
    ship.run(2.5)
    assert srv.apply_command({"type": "snapshot", "value": "s1"})["ok"]
    h_snap = ship.state_hash()
    ship.run(5.0)
    h_later = ship.state_hash()
    srv.apply_command({"type": "injectFault", "value": FAULT_STEERING_GEAR})
    srv.apply_command({"type": "clearFault", "value": "gyroDrift"})
    assert srv.apply_command({"type": "restore", "value": "s1"})["ok"]
    assert ship.state_hash() == h_snap and ship.faults == ["gyroDrift"]
    ship.run(5.0)
    assert ship.state_hash() == h_later  # 亂數狀態一併還原 → 陣風序列相同
    assert srv.apply_command({"type": "restore", "value": "missing"})["ok"] is False


def test_local_tangent_plane_round_trip() -> None:
    ltp = LocalTangentPlane(23.80, 120.05)
    assert ltp.to_local(23.80, 120.05) == (0.0, 0.0)
    x, y = ltp.to_local(23.81, 120.06)
    lat, lon = ltp.to_geodetic(x, y)
    assert lat == pytest.approx(23.81, abs=1e-12) and lon == pytest.approx(120.06, abs=1e-12)
    assert ltp.m_per_deg_lat == pytest.approx(110_700.0, rel=0.01)  # 子午圈,緯度 23.8°
    assert ltp.m_per_deg_lon == pytest.approx(111_320.0 * 0.9149, rel=0.01)
