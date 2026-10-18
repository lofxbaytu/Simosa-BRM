"""injectFault / clearFault 的參數解析與 C# SimulationEngine 一致(審查發現:非正規寫法兩邊結果不同)。

- injectFault 的故障參數只讀 args.args 物件(C# FaultArgString);寫成 args.id 時等於沒指定拖船
  (斷第一艘拉中/待命中的拖船),指定不存在的 id 也不會被拒絕。
- clearFault 的故障名稱與 injectFault 相同:value 字串,或 args.name(C# SimCommand.FaultName);
  只有兩者都缺(或值為 all)時才清除全部。
"""

from __future__ import annotations

from simosa_brm.mmg import FAULT_BOW_THRUSTER, FAULT_TUG_LINE_BREAK
from simosa_brm.scenario import scenario_from_tree, ship_from_scenario
from simosa_brm.server import SimServer


def _server(tg1_mode: str = "push", tg2: bool = True) -> SimServer:
    tugs = [{"id": "TG1", "bollardPull_t": 40, "attach": "fwdStbd",
             "initial": {"mode": tg1_mode, "direction_deg": 270 if tg1_mode == "push" else 90, "power_pct": 80}}]
    if tg2:
        tugs.append({"id": "TG2", "bollardPull_t": 35, "attach": "aftStbd", "initial": {"mode": "pull", "direction_deg": 90, "power_pct": 80}})
    tree = {"id": "T_fault_args", "seed": 3, "ship": {"id": "FSB1", "loading": "full"}, "origin": {"lat": 23.8, "lon": 120.15},
            "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": 0, "telegraph": "STOP"},
            "environment": {"wind": {"trueSpeed": 0, "trueDir": 0, "gustiness": 0}, "current": {"set": 0, "drift": 0}, "waterDepth": 20},
            "tugs": tugs}
    sc = scenario_from_tree(tree)
    srv = SimServer(ship_from_scenario(sc), scenario=sc)
    for _ in range(500):
        srv.ship.step()
    return srv


def _parted(srv: SimServer) -> list[str]:
    return [ev["itemId"] for ev in srv.ship.external_events if ev["kind"] == "tugLineParted"]


def test_inject_tug_line_break_args_id_is_not_a_fault_argument() -> None:
    """{"args": {"id": "TG1"}}(不是 args.args.id):C# 視為未指定拖船 → 斷第一艘拉中/待命中的 TG2,TG1 照推。"""
    srv = _server()
    ack = srv.apply_command({"type": "injectFault", "value": FAULT_TUG_LINE_BREAK, "args": {"id": "TG1"}})
    assert ack["ok"], ack
    assert _parted(srv) == ["TG2"]
    assert srv.ship.external.tug("TG2").line_parted and not srv.ship.external.tug("TG1").line_parted
    assert FAULT_TUG_LINE_BREAK in srv.ship.faults


def test_inject_tug_line_break_unknown_args_id_is_accepted_like_csharp() -> None:
    """args.id = TG9(非正規位置):C# 不檢查 → 接受並斷 TG2;正規 args.args.id = TG9 仍以 unknownTug 拒絕。"""
    srv = _server()
    ack = srv.apply_command({"type": "injectFault", "value": FAULT_TUG_LINE_BREAK, "args": {"args": {"id": "TG9"}}})
    assert not ack["ok"] and "unknownTug" in ack["detail"] and FAULT_TUG_LINE_BREAK not in srv.ship.faults
    ack = srv.apply_command({"type": "injectFault", "value": FAULT_TUG_LINE_BREAK, "args": {"id": "TG9"}})
    assert ack["ok"], ack
    assert _parted(srv) == ["TG2"] and FAULT_TUG_LINE_BREAK in srv.ship.faults


def test_inject_fault_name_from_args_name_with_args_args_id() -> None:
    """正規物件形式 {name, args:{id}}(教官站送出的寫法)照常指定拖船。"""
    srv = _server(tg1_mode="pull")
    ack = srv.apply_command({"type": "injectFault", "args": {"name": FAULT_TUG_LINE_BREAK, "args": {"id": "TG2"}}})
    assert ack["ok"] and _parted(srv) == ["TG2"]


def test_clear_fault_args_name_clears_only_that_fault() -> None:
    """clearFault {args:{name:"bowThruster"}}:只清 bowThruster;tugLineBreak 仍在,斷纜的拖船不能重新帶纜(tugLineParted)。"""
    srv = _server(tg1_mode="pull", tg2=False)
    assert srv.apply_command({"type": "injectFault", "value": FAULT_TUG_LINE_BREAK})["ok"]
    assert srv.apply_command({"type": "injectFault", "value": FAULT_BOW_THRUSTER})["ok"]
    ack = srv.apply_command({"type": "clearFault", "args": {"name": FAULT_BOW_THRUSTER}})
    assert ack["ok"] and srv.ship.faults == [FAULT_TUG_LINE_BREAK], ack
    assert srv.ship.external.tug("TG1").line_parted
    ack = srv.apply_command({"type": "tugControl", "args": {"id": "TG1", "mode": "pull", "direction_deg": 90, "power_pct": 80}})
    assert not ack["ok"] and "tugLineParted" in ack["detail"], ack


def test_clear_fault_all_keyword_and_missing_name() -> None:
    """value 或 args.name 為 all(不分大小寫)時清除全部(C# ClearFault);兩者都缺時亦清除全部。"""
    for cmd in ({"type": "clearFault", "value": "all"}, {"type": "clearFault", "args": {"name": "ALL"}}, {"type": "clearFault"},
                {"type": "clearFault", "args": {}}):
        srv = _server(tg1_mode="pull", tg2=False)
        srv.apply_command({"type": "injectFault", "value": FAULT_TUG_LINE_BREAK})
        srv.apply_command({"type": "injectFault", "value": FAULT_BOW_THRUSTER})
        ack = srv.apply_command(cmd)
        assert ack["ok"] and srv.ship.faults == [] and "全部" in ack["detail"], (cmd, ack)
        assert not srv.ship.external.tug("TG1").line_parted
