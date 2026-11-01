"""NMEA 一致性檢驗工具(``simosa_brm.nmea_check``;規劃書第 14 章「儀器 NMEA」列)。

夾具 ``fixtures/nmea/E05_gpsfailure_10s.nmea`` 由 C# SimCore.Host 以 ``--nmea-file`` 實跑 10 s 錄下
(E05 複本 + 時間表第 5 秒 ``injectFault: gpsFailure``;產生用的情境在 build/tmp,未加入 data/scenarios),
``E05_gpsfailure_10s.state.jsonl`` 為同一次執行 ``--record`` 紀錄中整秒(tick % 50 == 0)的 state 行,供位置/航向/水深一致性比對。
其餘測試以改寫夾具句型(重算校驗和)驗證各項檢查會把問題抓出來。
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from simosa_brm import nmea_check as nc

FIXTURES = Path(__file__).parent / "fixtures" / "nmea"
NMEA = FIXTURES / "E05_gpsfailure_10s.nmea"
STATE = FIXTURES / "E05_gpsfailure_10s.state.jsonl"


def rebuild(sentence: str) -> str:
    """重算校驗和(改寫句型內容後用)。"""
    body = sentence[1 : sentence.rfind("*")]
    return sentence[0] + body + "*" + nc.checksum(body)


def fixture_lines() -> list[str]:
    return NMEA.read_text(encoding="ascii").splitlines()


def check_lines(lines: list[str], **kw) -> nc.CheckResult:
    sentences, issues = nc.parse_stream((l, None) for l in lines)
    return nc.check_sentences(sentences, issues, source="test", **kw)


# ---------------------------------------------------------------------------------------------------------------------
# 解析與校驗和


def test_checksum_matches_known_example_and_csharp():
    assert nc.checksum("GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,") == "47"
    assert nc.checksum("HEHDT,274.1,T") == "2F"  # NmeaTests.ChecksumMatchesKnownExample


def test_parse_line_flags_bad_checksum_missing_star_and_tag_block():
    issues: list[nc.Issue] = []
    ok = nc.parse_line("$HEHDT,274.1,T*2F\r\n", 1, issues)
    assert ok is not None and ok.checksum_ok and ok.talker == "HE" and ok.formatter == "HDT" and ok.fields == ["274.1", "T"]
    bad = nc.parse_line("$HEHDT,274.1,T*2E", 2, issues)
    assert bad is not None and not bad.checksum_ok
    assert any(i.check == "checksum" and i.line == 2 for i in issues)
    nc.parse_line("$HEHDT,274.1,T", 3, issues)
    assert any(i.check == "checksum" and i.line == 3 and "缺少" in i.message for i in issues)
    tagged = nc.parse_line("\\s:SI0001,c:1700000000*4C\\$HEHDT,274.1,T*2F", 4, issues)
    assert tagged is not None and tagged.tag == "s:SI0001,c:1700000000*4C" and tagged.checksum_ok
    assert nc.parse_line("", 5, issues) is None
    assert nc.parse_line("garbage", 6, issues) is None
    assert any(i.check == "format" and i.line == 6 for i in issues)


def test_specs_match_csharp_iec_field_counts():
    # 與 NmeaTests.Iec61162FieldCounts 相同
    expected = {"GGA": 14, "RMC": 12, "VTG": 9, "VBW": 10, "DPT": 3, "MWV-R": 5, "MWV-T": 5, "HDT": 2, "ROT": 2, "RSA": 4, "TTM": 15, "ALR": 5, "VDM": 6}
    for key, n in expected.items():
        assert n in nc.SPECS[key].field_counts, key
    assert nc.SPECS["HDT"].rate_hz == 10.0 and nc.SPECS["GGA"].rate_hz == 1.0 and nc.SPECS["TTM"].rate_hz == 1.0


# ---------------------------------------------------------------------------------------------------------------------
# 夾具(Host 實跑)


def test_fixture_file_is_ascii_crlf_without_prefix():
    raw = NMEA.read_bytes()
    assert raw.endswith(b"\r\n") and b"\n" not in raw.replace(b"\r\n", b"")
    assert all(0x0A <= b <= 0x7E for b in raw)
    assert raw.startswith(b"$") or raw.startswith(b"!")


def test_fixture_passes_all_checks_with_state_comparison():
    result = nc.check_file(NMEA, states=nc.read_states(STATE))
    assert result.passed, [i.message for i in result.errors]
    assert result.warnings == []
    assert result.sentences_total == 414 and result.seconds_observed == 10 and result.windows_used == 9 and result.marker == "GGA"
    assert result.first_time == "223001.00" and result.last_time == "223010.00"  # E05 startTimeUtc 22:30:00Z + 1..10 s
    for key, hz in {"HDT": 10.0, "ROT": 10.0, "RSA": 10.0, "GGA": 1.0, "RMC": 1.0, "VTG": 1.0, "VBW": 1.0, "DPT": 1.0, "MWV-R": 1.0, "MWV-T": 1.0}.items():
        row = result.rate(key)
        assert row is not None and row.status == "ok" and row.rate_hz == pytest.approx(hz), key
    ttm = result.rate("TTM")
    assert ttm is not None and ttm.count == 30 and ttm.status == "ok" and "01:9/9s" in ttm.note
    alr = result.rate("ALR")
    assert alr is not None and alr.status == "ok"
    # 故障注入(第 5 秒 gpsFailure):GGA 品質 1 → 0、衛星 10 → 00
    quality = [q["quality"] for q in result.gga_quality]
    assert quality == ["1"] * 4 + ["0"] * 6
    assert [q["satellites"] for q in result.gga_quality][:5] == ["10", "10", "10", "10", "00"]
    # ALR 002 GNSS POSITION LOST 自第 6 秒起每秒一句
    assert result.alerts == [{"number": "002", "text": "GNSS POSITION LOST", "count": 5, "conditions": "A", "acks": "V", "seconds": 5}]
    # AIS:三艘目標的類型 1 位置報告(錯開秒數)與類型 5 靜態(船名)
    assert [t.mmsi for t in result.ais] == [416300101, 416300102, 416300103]
    assert [t.name for t in result.ais] == ["EVER ORIENT", "GOLDEN WAVE", "CHIN FU 66"]
    assert all(len(t.positions) == 1 and t.static is not None for t in result.ais)
    t1 = result.ais[0]
    assert t1.static is not None and t1.static.call_sign == "BKEO" and t1.static.ship_type == 70
    assert t1.positions[0].lat == pytest.approx(22.5497, abs=1e-4) and t1.positions[0].lon == pytest.approx(120.2546, abs=1e-4)
    assert 0 <= (t1.positions[0].cog or 0) < 360 and t1.positions[0].utc_second == 10
    # 與 state 比對:10 秒都對得上,位置差在 0.0001′ 量化內、航向/水深/SOG/COG 在 0.1 單位的捨入內
    c = result.state_compare
    assert c is not None and c.compared == 10 and c.unmatched == 0 and c.mismatches == 0
    assert c.max_position_m < 0.5 and c.max_heading_deg <= 0.05 and c.max_depth_m <= 0.05 and c.max_sog_kn <= 0.05 and c.max_cog_deg <= 0.05


def test_fixture_gnss_failure_flags_consistent_across_sentences():
    lines = fixture_lines()
    after = [l for l in lines if ",22300[5-9]" and l.startswith(("$GPGGA", "$GPRMC", "$GPVTG", "$VDVBW"))]
    gga_invalid = [l for l in lines if l.startswith("$GPGGA") and ",0,00," in l]
    rmc_invalid = [l for l in lines if l.startswith("$GPRMC,") and ",V," in l]
    vtg_invalid = [l for l in lines if l.startswith("$GPVTG") and l.split("*")[0].endswith(",N")]
    assert len(gga_invalid) == len(rmc_invalid) == len(vtg_invalid) == 6
    assert after  # 保留變數用途:句型存在
    # 失效期間仍送 HDT/DPT(電羅經與測深儀正常),VBW 對地欄位 V、對水 A
    vbw_after = [l for l in lines[lines.index(gga_invalid[0]) :] if l.startswith("$VDVBW")]
    assert all(l.split(",")[3] == "A" and l.split(",")[6] == "V" for l in vbw_after)


# ---------------------------------------------------------------------------------------------------------------------
# 各檢查會抓出問題


def test_rate_failure_detected_when_half_of_hdt_dropped():
    lines = fixture_lines()
    kept, seen = [], 0
    for l in lines:
        if l.startswith("$HEHDT"):
            seen += 1
            if seen % 2 == 0:
                continue
        kept.append(l)
    r = check_lines(kept)
    assert not r.passed
    hdt = r.rate("HDT")
    assert hdt is not None and hdt.status == "fail" and hdt.rate_hz == pytest.approx(5.0, abs=0.2)
    assert any(i.check == "rate" and "HDT" in i.message for i in r.errors)
    # 容差放寬到 60% 就通過
    assert check_lines(kept, tolerance=0.6).rate("HDT").status == "ok"


def test_missing_mandatory_sentence_detected():
    r = check_lines([l for l in fixture_lines() if not l.startswith("$TIROT")])
    assert any(i.check == "rate" and "缺少自船必送句型 ROT" in i.message for i in r.errors)
    assert r.rate("ROT").status == "missing"


def test_field_count_error_detected():
    lines = fixture_lines()
    i = next(k for k, l in enumerate(lines) if l.startswith("$GPGGA"))
    body = lines[i][: lines[i].rfind("*")]
    lines[i] = rebuild(body[: body.rfind(",")] + "*00")  # 少一欄
    r = check_lines(lines)
    assert any(x.check == "fields" and "GGA 欄位數 13" in x.message for x in r.errors)


def test_time_not_monotonic_and_batch_time_mismatch_detected():
    lines = fixture_lines()
    idx = [k for k, l in enumerate(lines) if l.startswith("$GPGGA")]
    lines[idx[3]] = rebuild(lines[idx[3]].replace("223004.00", "223002.50"))
    r = check_lines(lines)
    msgs = [i.message for i in r.errors]
    assert any("GGA 時刻未遞增" in m for m in msgs)
    assert any("同批 RMC 時刻" in m for m in msgs)
    assert any("同批 TTM 時刻" in m for m in msgs)


def test_gga_rmc_vtg_vbw_consistency_detected():
    lines = fixture_lines()
    k = next(i for i, l in enumerate(lines) if l.startswith("$GPGGA") and ",0,00," in l)
    lines[k] = rebuild(lines[k].replace(",,,,,0,00,,,M,,M,,", ",2229.7018,N,12012.3014,E,1,10,0.9,0.0,M,0.0,M,,"))
    r = check_lines(lines)
    msgs = [i.message for i in r.errors]
    assert any("與 RMC 狀態 V 不一致" in m for m in msgs)
    assert any("與 VTG 模式 N 不一致" in m for m in msgs)


def test_vbw_ground_flag_with_gnss_failure_detected():
    lines = fixture_lines()
    k = next(i for i, l in enumerate(lines) if l.startswith("$GPGGA") and ",0,00," in l)
    j = next(i for i in range(k, len(lines)) if lines[i].startswith("$VDVBW"))
    f = lines[j][: lines[j].rfind("*")].split(",")
    f[4], f[5], f[6] = "8.0", "0.0", "A"
    lines[j] = rebuild(",".join(f) + "*00")
    r = check_lines(lines)
    assert any("GGA 品質 0 但 VBW 對地狀態 A" in i.message for i in r.errors)


def test_ais_decoder_known_example_and_bad_payload():
    # gpsd 文件範例:MMSI 477553000、繫泊、西雅圖附近、COG 51°、HDG 181°
    bits = nc.dearmor("177KQJ5000G?tO`K>RA1wUbN0TKH")
    m = nc.decode_ais(bits, 1, 1)
    assert (m.msg_type, m.mmsi, m.nav_status) == (1, 477553000, 5)
    assert m.lon == pytest.approx(-122.3458, abs=1e-4) and m.lat == pytest.approx(47.5828, abs=1e-4)
    assert m.cog == 51.0 and m.heading == 181 and m.utc_second == 15 and m.sog == 0.0
    r = check_lines(["!AIVDM,1,1,,B,177KQJ5000G?tO`K>RA1wUbN0TKH,0*5C"])
    assert r.ais and r.ais[0].mmsi == 477553000
    with pytest.raises(ValueError):
        nc.dearmor("abc~")
    r = check_lines([rebuild("!AIVDM,1,1,,A,177KQJ5000G?tO`K>RA1wUbN0TKH,7*00")])  # 填充位元 7 無效
    assert any(i.check == "ais" and "填充位元" in i.message for i in r.errors)


def test_ais_multipart_incomplete_and_interval_checked():
    lines = fixture_lines()
    static_t1 = [l for l in lines if l.startswith("!AIVDM,2,") and ",1,A," in l]
    assert len(static_t1) == 2
    r = check_lines([l for l in lines if l != static_t1[1]])
    assert any(i.check == "ais" and "多句未完整" in i.message for i in r.warnings)
    # 位置報告間隔:複製 T2 的位置報告到第 4 秒(間隔 3 s ≠ 10 s)
    pos_t2 = next(l for l in lines if l.startswith("!AIVDM,1,1,,B,"))
    k = next(i for i, l in enumerate(lines) if l.startswith("$GPGGA,223004.00"))
    lines2 = lines[: k + 12] + [pos_t2] + lines[k + 12 :]
    r2 = check_lines(lines2)
    assert any(i.check == "rate" and "位置報告間隔 3 s" in i.message for i in r2.errors)
    assert any(i.check == "ais" and "UTC 秒" in i.message for i in r2.warnings)  # 複製句的 UTC 秒 1 ≠ 第 4 秒


def test_alr_field_errors_detected():
    r = check_lines([rebuild("$IIALR,223006.00,2,X,V,*00"), "$GPGGA,223006.00,,,,,0,00,,,M,,M,,*4D"])
    msgs = [i.message for i in r.errors]
    assert any("警報編號" in m for m in msgs) and any("條件/確認欄" in m for m in msgs) and any("說明文字" in m for m in msgs)


def test_state_mismatch_detected():
    states = nc.read_states(STATE)
    assert len(states) == 10 and all("utc" in s for s in states)
    states[1]["pos"]["lat"] += 0.0005  # ≈ 55 m
    states[2]["heading"] += 1.0
    states[3]["sensors"]["positionValid"] = False
    r = nc.check_file(NMEA, states=states)
    msgs = [i.message for i in r.errors]
    assert any("GGA 位置與 state.pos 相差" in m for m in msgs)
    assert any("HDT" in m and "state.heading" in m for m in msgs)
    assert any("positionValid=False 不一致" in m for m in msgs)
    assert r.state_compare is not None and r.state_compare.mismatches == 3
    # 時間對不上的狀態 → warning,不比對
    shifted = [dict(s, utc=s["utc"].replace("2026-05-05", "2026-05-06")) for s in nc.read_states(STATE)]
    r2 = nc.check_file(NMEA, states=shifted)
    assert r2.state_compare is not None and r2.state_compare.compared == 0 and any(i.check == "state" for i in r2.warnings)


def test_read_states_accepts_record_and_bare_frames(tmp_path: Path):
    p = tmp_path / "s.jsonl"
    p.write_text(
        '{"kind":"header","version":2}\n{"type":"event","event":{}}\n{"kind":"state","state":{"utc":"2026-05-05T22:30:01.000Z","pos":{"lat":1,"lon":2}}}\n'
        '{"utc":"2026-05-05T22:30:02.000Z","pos":{"lat":1,"lon":2},"heading":0}\n',
        encoding="utf-8",
    )
    assert [s["utc"] for s in nc.read_states(p)] == ["2026-05-05T22:30:01.000Z", "2026-05-05T22:30:02.000Z"]


def test_windows_fallback_without_gga():
    # 只有雷達服務的 TTM(無 GGA):以 TTM 時刻切秒窗
    lines = [l for l in fixture_lines() if l.startswith("$RATTM")]
    r = check_lines(lines)
    assert r.marker == "time-field" and r.seconds_observed == 10
    assert r.rate("TTM").status == "ok"
    assert any(i.check == "rate" and "缺少自船必送句型 GGA" in i.message for i in r.errors)


# ---------------------------------------------------------------------------------------------------------------------
# 報告與 CLI


def test_render_markdown_has_all_sections():
    r = nc.check_file(NMEA, states=nc.read_states(STATE))
    md = nc.render_markdown(r)
    for h in ("# NMEA 一致性檢驗報告", "## 1. 句型與速率", "## 2. 校驗和", "## 3. 時間戳", "## 4. GGA 定位品質", "## 5. AIS", "## 6. 警報 ALR", "## 7. 與核心狀態比對", "## 8. 全部問題", "## 9. 結論"):
        assert h in md
    assert "| HDT | HE | 100 | 10.00 | 10.00 | ok |" in md
    assert "| 416300101 | EVER ORIENT | BKEO | 1 |" in md
    assert "**通過**" in md and "tools/opencpn-check.md" in md
    j = nc.result_to_json(r)
    assert j["passed"] is True and j["ggaQuality"][4]["quality"] == "0" and j["stateCompare"]["compared"] == 10


def test_cli_exit_codes_and_outputs(tmp_path: Path):
    report, js = tmp_path / "r.md", tmp_path / "r.json"
    assert nc.main(["--file", str(NMEA), "--state", str(STATE), "--report", str(report), "--json", str(js), "--quiet"]) == 0
    assert report.is_file() and json.loads(js.read_text(encoding="utf-8"))["passed"] is True
    # 竄改一個校驗和 → 不合格(1)
    bad = tmp_path / "bad.nmea"
    lines = fixture_lines()
    lines[0] = lines[0][:-1] + ("0" if lines[0][-1] != "0" else "1")
    bad.write_text("\r\n".join(lines) + "\r\n", encoding="ascii")
    assert nc.main(["--file", str(bad), "--report", str(tmp_path / "bad.md"), "--quiet"]) == 1
    assert "校驗和不符" in (tmp_path / "bad.md").read_text(encoding="utf-8")
    # 找不到檔案 → 2;空檔 → 1(沒有可解析的句)
    assert nc.main(["--file", str(tmp_path / "missing.nmea"), "--report", str(tmp_path / "m.md"), "--quiet"]) == 2
    empty = tmp_path / "empty.nmea"
    empty.write_text("", encoding="ascii")
    assert nc.main(["--file", str(empty), "--report", str(tmp_path / "e.md"), "--quiet"]) == 1
    # --seconds 無效
    assert nc.main(["--udp", "19999", "--seconds", "0", "--report", str(tmp_path / "u.md"), "--quiet"]) == 2


def test_live_udp_capture_roundtrip(tmp_path: Path):
    """本機 UDP 單播:把夾具當 Host 送到未使用的埠,工具即時擷取並檢驗通過(多播與 WebSocket 比對需實跑 Host,見 tools/opencpn-check.md)。"""
    import asyncio
    import socket
    import threading
    import time

    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    lines = fixture_lines()
    # 依 GGA 批次切成資料包(與 UdpNmeaSender 一批一包相同),每批間隔 20 ms
    batches: list[list[str]] = [[]]
    for l in lines:
        if l.startswith("$HEHDT") and batches[-1]:
            batches.append([])
        batches[-1].append(l)
    stop = threading.Event()

    def sender() -> None:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        time.sleep(0.3)
        for b in batches:
            if stop.is_set():
                break
            s.sendto(("\r\n".join(b) + "\r\n").encode("ascii"), ("127.0.0.1", port))
            time.sleep(0.02)
        s.close()

    th = threading.Thread(target=sender, daemon=True)
    th.start()
    try:
        got, states, datagrams = asyncio.run(nc.capture_live(udp_port=port, multicast=None, bind="127.0.0.1", iface=None, seconds=4.0, ws_url=None))
    finally:
        stop.set()
        th.join(timeout=5)
    assert datagrams == len(batches) and len(got) == len(lines)
    sentences, issues = nc.parse_stream(got)
    r = nc.check_sentences(sentences, issues, source="udp-test", capture_seconds=4.0)
    assert r.passed and r.seconds_observed == 10 and all(s.recv_t is not None for s in sentences)
