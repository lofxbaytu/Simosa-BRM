"""主控程序層指令 sessionInfo / shutdown(與 C# SimCore.Host HostCommandRouter 語意一致):
sessionInfo 驗證並保存(參考實作不寫紀錄)、格式不符拒絕且不保存;shutdown 回 ack 後廣播迴圈在 1 s 內結束,非本機/私有網段來源拒絕。"""

from __future__ import annotations

import asyncio
import json
import socket
import time

from simosa_brm.mmg import make_ship
from simosa_brm.server import SimServer, parse_session_info, shutdown_allowed

VALID_ARGS = {
    "participants": [
        {"name": "王大副", "role": "OOW", "rank": "大副", "certificateNo": "C-123"},
        {"name": "李舵工", "role": "helmsman"},
    ],
    "restHoursConfirmation": {"confirmedBy": "船長", "confirmedAtUtc": "2026-10-08T01:30:00Z", "compliant": True, "note": "已核對休息時間紀錄"},
    "course": {"id": "BRM-2026-03", "title": "BRM 複訓", "instructor": "陳教官"},
}


def _srv() -> SimServer:
    ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    return SimServer(ship, 1.0)


def test_session_info_accepted_and_normalized() -> None:
    srv = _srv()
    ack = srv.apply_command({"type": "sessionInfo", "args": VALID_ARGS})
    assert ack["ok"] and ack["command"] == "sessionInfo" and "王大副/oow" in ack["detail"]
    info = srv.session_info
    assert info is not None
    assert [p["role"] for p in info["participants"]] == ["oow", "helmsman"]  # role 正規化為小寫
    assert info["participants"][0]["rank"] == "大副" and info["participants"][0]["certificateNo"] == "C-123"
    assert "rank" not in info["participants"][1]
    assert info["restHoursConfirmation"] == {"confirmedBy": "船長", "confirmedAtUtc": "2026-10-08T01:30:00.000Z", "compliant": True, "note": "已核對休息時間紀錄"}
    assert info["course"] == {"id": "BRM-2026-03", "title": "BRM 複訓", "instructor": "陳教官"}
    # 模型狀態不受影響(與重設後相同 tick)
    assert srv.ship.state.tick == 0


def test_session_info_rejected_keeps_previous() -> None:
    srv = _srv()
    assert srv.apply_command({"type": "sessionInfo", "args": VALID_ARGS})["ok"]
    bad_role = {**VALID_ARGS, "participants": [{"name": "甲", "role": "captain"}]}
    ack = srv.apply_command({"type": "sessionInfo", "args": bad_role})
    assert not ack["ok"] and "role" in ack["detail"]
    assert srv.session_info is not None and srv.session_info["participants"][0]["name"] == "王大副"  # 先前的保留
    assert not srv.apply_command({"type": "sessionInfo", "args": {"participants": [], "restHoursConfirmation": VALID_ARGS["restHoursConfirmation"]}})["ok"]
    assert not srv.apply_command({"type": "sessionInfo", "args": {"participants": VALID_ARGS["participants"]}})["ok"]  # 缺 restHoursConfirmation
    no_time = {**VALID_ARGS, "restHoursConfirmation": {"confirmedBy": "船長", "confirmedAtUtc": "昨天", "compliant": True}}
    assert not srv.apply_command({"type": "sessionInfo", "args": no_time})["ok"]
    not_bool = {**VALID_ARGS, "restHoursConfirmation": {"confirmedBy": "船長", "confirmedAtUtc": "2026-10-08T01:30:00Z", "compliant": "yes"}}
    assert not srv.apply_command({"type": "sessionInfo", "args": not_bool})["ok"]
    assert not srv.apply_command({"type": "sessionInfo"})["ok"]
    # 直接呼叫解析器:控制字元清理、course 全空則省略
    info = parse_session_info({"participants": [{"name": " 王\x01大副 ", "role": "Master"}],
                               "restHoursConfirmation": {"confirmedBy": "船長", "confirmedAtUtc": "2026-10-08T09:30:00+08:00", "compliant": False},
                               "course": {"id": "  "}})
    assert info["participants"] == [{"name": "王 大副", "role": "master"}]
    assert info["restHoursConfirmation"]["confirmedAtUtc"] == "2026-10-08T01:30:00.000Z" and "course" not in info


def test_shutdown_policy() -> None:
    srv = _srv()
    assert shutdown_allowed(None) and shutdown_allowed(("127.0.0.1", 5000)) and shutdown_allowed(("192.168.77.21", 5000))
    assert shutdown_allowed(("::1", 5000, 0, 0)) and shutdown_allowed(("::ffff:10.0.0.5", 5000, 0, 0))
    assert not shutdown_allowed(("8.8.8.8", 5000)) and not shutdown_allowed(("2606:4700::1111", 5000, 0, 0))
    # is_private 會接受的特殊網段,C# 不接受 → 這裡也拒絕
    assert not any(shutdown_allowed((a, 5000)) for a in ("0.0.0.0", "192.0.0.1", "198.18.0.1", "240.0.0.1"))
    assert shutdown_allowed(("fe80::1", 5000, 0, 0)) and shutdown_allowed(("fd12::1", 5000, 0, 0)) and shutdown_allowed(("172.31.9.9", 5000))
    ack = srv.apply_command({"type": "shutdown"}, remote=("8.8.8.8", 5000))
    assert not ack["ok"] and not srv.shutdown_requested
    ack = srv.apply_command({"type": "shutdown", "args": {"reason": "stop-all"}}, remote=("192.168.77.21", 5000))
    assert ack["ok"] and srv.shutdown_requested


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def test_websocket_shutdown_ends_loop_within_one_second() -> None:
    from websockets.asyncio.client import connect
    from websockets.asyncio.server import serve

    port = _free_port()

    async def run() -> None:
        ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
        srv = SimServer(ship, time_scale=1.0)
        async with serve(srv.handler, "127.0.0.1", port):
            loop_task = asyncio.create_task(srv.loop())
            async with connect(f"ws://127.0.0.1:{port}") as ws:
                await asyncio.wait_for(ws.recv(), 5)  # 第一幀 state
                await ws.send(json.dumps({"type": "sessionInfo", "args": VALID_ARGS}))
                ack = None
                for _ in range(100):
                    msg = json.loads(await asyncio.wait_for(ws.recv(), 5))
                    if msg.get("type") == "ack":
                        ack = msg
                        break
                assert ack is not None and ack["ok"] and ack["command"] == "sessionInfo"
                await ws.send(json.dumps({"type": "shutdown"}))
                for _ in range(100):
                    msg = json.loads(await asyncio.wait_for(ws.recv(), 5))
                    if msg.get("type") == "ack":
                        ack = msg
                        break
                assert ack["ok"] and ack["command"] == "shutdown"
                t0 = time.perf_counter()
                await asyncio.wait_for(loop_task, 1.0)  # 回覆後 ≤ 1 s 廣播迴圈結束
                assert time.perf_counter() - t0 < 1.0
            assert srv.session_info is not None and srv.shutdown_requested

    asyncio.run(run())
