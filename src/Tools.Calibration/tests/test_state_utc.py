"""state.utc(state.schema.json 選填欄位):= 練習開始 UTC + t;情境 startTimeUtc 可解析者採用,未給時以現在時刻代入並寫回情境;
格式與 C# SimulationEngine.UtcFormat 相同(yyyy-MM-ddTHH:mm:ss.fffZ)。"""

from __future__ import annotations

import re
from datetime import datetime, timezone

from simosa_brm.mmg import format_utc, make_ship, parse_utc
from simosa_brm.server import SimServer

UTC_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")


def test_ship_without_start_utc_omits_field() -> None:
    ship = make_ship("FSB1", "full")
    assert "utc" not in ship.state_json()


def test_parse_and_format_utc() -> None:
    assert parse_utc("2026-01-01T02:00:00Z") == datetime(2026, 1, 1, 2, 0, 0, tzinfo=timezone.utc)
    assert parse_utc("2026-01-01T02:00:00") == datetime(2026, 1, 1, 2, 0, 0, tzinfo=timezone.utc)  # 無時區視為 UTC
    assert parse_utc("2026-01-01T10:00:00+08:00") == datetime(2026, 1, 1, 2, 0, 0, tzinfo=timezone.utc)
    assert parse_utc(None) is None and parse_utc("") is None and parse_utc("not-a-time") is None
    assert format_utc(datetime(2026, 1, 1, 2, 0, 5, 40000, tzinfo=timezone.utc)) == "2026-01-01T02:00:05.040Z"
    assert format_utc(datetime(2026, 1, 1, 2, 0, 5, 999700, tzinfo=timezone.utc)) == "2026-01-01T02:00:06.000Z"  # 四捨五入進位


def test_utc_follows_scenario_start_plus_t() -> None:
    srv = SimServer(make_ship("FSB1", "full"), 1.0)
    srv.load_scenario("E01_baseline")  # startTimeUtc: 2026-01-01T02:00:00Z
    assert srv.state_json()["utc"] == "2026-01-01T02:00:00.000Z"
    for _ in range(50 * 5 + 2):  # 5.04 s
        srv.ship.step()
    assert srv.state_json()["utc"] == "2026-01-01T02:00:05.040Z"
    # 重設後回到開始時刻(開始時刻不變)
    srv.apply_command({"type": "reset"})
    assert srv.state_json()["utc"] == "2026-01-01T02:00:00.000Z"


def test_missing_start_time_uses_clock_and_is_written_back() -> None:
    srv = SimServer(make_ship("FSB1", "full"), 1.0)
    sc = srv.load_scenario("E01_baseline")
    sc["startTimeUtc"] = None
    before = datetime.now(timezone.utc)
    srv._apply_start_utc()
    after = datetime.now(timezone.utc)
    utc = srv.state_json()["utc"]
    assert UTC_RE.match(utc)
    start = parse_utc(utc)
    assert start is not None
    assert before.replace(microsecond=0) <= start <= after
    # 寫回情境,之後(例如重播/NMEA)沿用同一起點
    assert sc["startTimeUtc"] == utc
    assert parse_utc(sc["startTimeUtc"]) == srv.ship.start_utc
