"""NMEA 一致性檢驗工具(``simosa_brm.nmea_check``;規劃書第 14 章「儀器 NMEA」列)。

夾具 ``fixtures/nmea/E05_gpsfailure_10s.nmea`` 由 C# SimCore.Host 以 ``--nmea-file`` 實跑 10 s 錄下
(E05 複本 + ``ship.mmsi: 416100001``(自船 VDO)+ 時間表第 5 秒 ``injectFault: gpsFailure``;產生用的情境在 build/tmp,未加入 data/scenarios;
第二十四輪重錄:加 THS 10 Hz、ALF/ALC、自船 !AIVDO),
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
    expected = {"GGA": 14, "RMC": 12, "VTG": 9, "VBW": 10, "DPT": 3, "MWV-R": 5, "MWV-T": 5, "HDT": 2, "THS": 2, "ROT": 2, "RSA": 4, "TTM": 15, "ALR": 5, "ALF": 13, "VDM": 6, "VDO": 6}
    for key, n in expected.items():
        assert n in nc.SPECS[key].field_counts, key
    assert nc.SPECS["HDT"].rate_hz == 10.0 and nc.SPECS["GGA"].rate_hz == 1.0 and nc.SPECS["TTM"].rate_hz == 1.0
    # 第二十四輪:THS 與 HDT 同 10 Hz(必送:電羅經失效時改模式 V)、ALF 每警報 1 Hz(識別碼在第 9 欄)、ALC 4 + 4n 欄、VDO 與 VDM 同為 ais 範圍
    assert nc.SPECS["THS"].rate_hz == 10.0 and nc.SPECS["THS"].mandatory and nc.SPECS["THS"].talker == "HE"
    assert nc.SPECS["ALF"].rate_hz == 1.0 and nc.SPECS["ALF"].scope == "alert" and nc.SPECS["ALF"].ident_index == 8
    assert nc.SPECS["ALC"].field_counts[:3] == (4, 8, 12) and nc.SPECS["ALC"].rate_hz is None
    assert nc.SPECS["VDO"].scope == "ais" and nc.SPECS["VDO"].talker == "AI"


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
    # 414(第二十二輪)+ THS 100 + ALF 5 + ALC 2(第 1 秒空清單、第 6 秒清單改變)+ 自船 VDO 3(類型 5 兩句 + 第 10 秒類型 1)= 524
    assert result.sentences_total == 524 and result.seconds_observed == 10 and result.windows_used == 9 and result.marker == "GGA"
    assert result.first_time == "223001.00" and result.last_time == "223010.00"  # E05 startTimeUtc 22:30:00Z + 1..10 s
    for key, hz in {"HDT": 10.0, "THS": 10.0, "ROT": 10.0, "RSA": 10.0, "GGA": 1.0, "RMC": 1.0, "VTG": 1.0, "VBW": 1.0, "DPT": 1.0, "MWV-R": 1.0, "MWV-T": 1.0}.items():
        row = result.rate(key)
        assert row is not None and row.status == "ok" and row.rate_hz == pytest.approx(hz), key
    ttm = result.rate("TTM")
    assert ttm is not None and ttm.count == 30 and ttm.status == "ok" and "01:9/9s" in ttm.note
    alr = result.rate("ALR")
    assert alr is not None and alr.status == "ok"
    alf = result.rate("ALF")
    assert alf is not None and alf.count == 5 and alf.status == "ok" and "002:4/4s" in alf.note
    alc = result.rate("ALC")
    assert alc is not None and alc.count == 2 and alc.status == "n/a"
    assert result.rate("VDO") is not None and result.rate("VDO").count == 3
    # 故障注入(第 5 秒 gpsFailure):GGA 品質 1 → 0、衛星 10 → 00
    quality = [q["quality"] for q in result.gga_quality]
    assert quality == ["1"] * 4 + ["0"] * 6
    assert [q["satellites"] for q in result.gga_quality][:5] == ["10", "10", "10", "10", "00"]
    # ALR 002 GNSS POSITION LOST 自第 6 秒起每秒一句;ALF 同秒窗同識別碼、狀態 V(成立未確認)、類別 B、優先序 A、修訂 1、改變時刻 = 成立時刻 223005.02
    assert result.alerts == [{"number": "002", "text": "GNSS POSITION LOST", "count": 5, "conditions": "A", "acks": "V", "seconds": 5}]
    assert result.alf == [{"identifier": "002", "text": "GNSS POSITION LOST", "count": 5, "states": "V", "category": "B", "priority": "A", "revisions": "1", "seconds": 5}]
    alf_lines = [l for l in fixture_lines() if l.startswith("$IIALF")]
    assert [l.split(",")[3] for l in alf_lines] == ["0", "1", "2", "3", "4"] and all(",223005.02,B,A,V,SIM,002,1,1,0,GNSS POSITION LOST*" in l for l in alf_lines)
    # ALC:第 1 秒空清單(0 項)、第 6 秒清單改變(1 項 002 修訂 1);序號 00、01
    assert result.alc == {"count": 2, "seconds": [1, 6], "maxGap_s": 5, "lastList": ["002"]}
    assert [l.split("*")[0] for l in fixture_lines() if l.startswith("$IIALC")] == ["$IIALC,01,01,00,0", "$IIALC,01,01,01,1,SIM,002,1,1"]
    # AIS:自船 VDO(416100001;類型 5 第 1 秒、類型 1 第 10 秒,此時 GNSS 已失效 → 位置 91/181、COG 360、SOG 102.3、航向仍有效 45)
    # 與三艘目標的類型 1 位置報告(錯開秒數)與類型 5 靜態(船名)
    assert [t.mmsi for t in result.ais] == [416100001, 416300101, 416300102, 416300103]
    assert [t.own for t in result.ais] == [True, False, False, False]
    assert [t.name for t in result.ais] == ["FS BITUMEN NO.1", "EVER ORIENT", "GOLDEN WAVE", "CHIN FU 66"]
    assert all(len(t.positions) == 1 and t.static is not None for t in result.ais)
    own = result.ais[0]
    assert own.static is not None and own.static.call_sign == "BIAX" and own.static.ship_type == 80 and own.static.imo == 9692428
    assert (own.positions[0].lat, own.positions[0].lon, own.positions[0].cog, own.positions[0].sog, own.positions[0].heading) == (91.0, 181.0, 360.0, 102.3, 45)
    assert own.positions[0].sec == 10 and own.positions[0].utc_second == 10
    assert any(i.severity == "info" and "416100001 位置報告無定位" in i.message for i in result.issues)
    t1 = result.ais[1]
    assert t1.static is not None and t1.static.call_sign == "BKEO" and t1.static.ship_type == 70
    assert t1.positions[0].lat == pytest.approx(22.5497, abs=1e-4) and t1.positions[0].lon == pytest.approx(120.2546, abs=1e-4)
    assert 0 <= (t1.positions[0].cog or 0) < 360 and t1.positions[0].utc_second == 10
    # 與 state 比對:10 秒都對得上,位置差在 0.0001′ 量化內、航向/水深/SOG/COG 在 0.1 單位的捨入內
    c = result.state_compare
    assert c is not None and c.compared == 10 and c.unmatched == 0 and c.mismatches == 0
    assert c.max_position_m < 0.5 and c.max_heading_deg <= 0.05 and c.max_depth_m <= 0.05 and c.max_sog_kn <= 0.05 and c.max_cog_deg <= 0.05
    # THS 與 HDT 同批同值、模式 A(電羅經正常)
    ths = [l for l in fixture_lines() if l.startswith("$HETHS")]
    assert len(ths) == 100 and all(l.split(",")[2].startswith("A*") for l in ths)


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


def test_ths_consistency_with_hdt_detected():
    # THS 航向改掉 → 與同批 HDT 不同;HDT 全部拿掉但 THS 仍模式 A → 錯誤;THS 改模式 V 但同批仍有 HDT → 錯誤
    lines = fixture_lines()
    i = next(k for k, l in enumerate(lines) if l.startswith("$HETHS"))
    lines[i] = rebuild("$HETHS,46.0,A*00")
    r = check_lines(lines)
    assert any(i.check == "content" and "THS 航向 46.0 ≠ 同批 HDT 45.0" in i.message for i in r.errors)
    r2 = check_lines([l for l in fixture_lines() if not l.startswith("$HEHDT")])
    assert any("THS 模式 A 但同批沒有 HDT" in i.message for i in r2.errors) is False  # HDT 完全不存在(整個擷取無 HDT)時不算
    r2b = check_lines([l for l in fixture_lines() if not l.startswith("$HEHDT")][:40] + [l for l in fixture_lines() if l.startswith("$HEHDT")][:1] + fixture_lines()[40:60])
    assert any("THS 模式 A 但同批沒有 HDT" in i.message for i in r2b.errors)
    lines3 = fixture_lines()
    lines3[i] = rebuild("$HETHS,,V*00")
    r3 = check_lines(lines3)
    assert any("THS 模式 V 但同批仍送 HDT" in i.message for i in r3.errors)
    # 與 state 比對:headingValid=false 時 THS 應為 V
    states = nc.read_states(STATE)
    states[2]["sensors"]["headingValid"] = False
    r4 = nc.check_file(NMEA, states=states)
    assert any(i.check == "state" and "THS 模式 A 與 state.sensors.headingValid=False" in i.message for i in r4.errors)


def test_alf_alc_errors_detected():
    lines = fixture_lines()
    # ALF 欄位:狀態 X、優先序 Z、修訂 0、文字空 → 四個錯誤;狀態 A(已確認)但同秒 ALR 確認 V → 不一致
    i = next(k for k, l in enumerate(lines) if l.startswith("$IIALF"))
    lines[i] = rebuild("$IIALF,1,1,0,223005.02,B,Z,X,SIM,002,1,0,0,*00")
    r = check_lines(lines)
    msgs = [x.message for x in r.errors if x.check == "alf"]
    assert any("優先序 'Z'" in m for m in msgs) and any("狀態 'X'" in m for m in msgs) and any("修訂計數 '0'" in m for m in msgs) and any("說明文字" in m for m in msgs)
    lines = fixture_lines()
    lines[i] = rebuild("$IIALF,1,1,0,223005.02,B,A,A,SIM,002,1,2,0,GNSS POSITION LOST*00")
    r = check_lines(lines)
    assert any("ALF 002 狀態 A 與 ALR 確認 V 不一致" in x.message for x in r.errors)
    assert any("ALC 項目 002 修訂 1 ≠ 同秒窗 ALF 的 2" in x.message for x in r.errors)
    # ALF 識別碼沒有對應 ALR;ALR 沒有對應 ALF
    lines = fixture_lines()
    lines[i] = rebuild("$IIALF,1,1,0,223005.02,B,A,V,SIM,003,1,1,0,ECHO SOUNDER FAILURE*00")
    r = check_lines(lines)
    assert any("ALF 003 在同秒窗沒有對應的 ALR" in x.message for x in r.errors) and any("ALR 002 沒有對應的 ALF" in x.message for x in r.errors)
    # ALC:項目數與欄位數不符;項目缺漏(清單有 ALF 002 但 ALC 空)
    lines = fixture_lines()
    j = next(k for k, l in enumerate(lines) if l.startswith("$IIALC,01,01,01"))
    lines[j] = rebuild("$IIALC,01,01,01,2,SIM,002,1,1*00")
    r = check_lines(lines)
    assert any("ALC 項目數 2 與欄位數 8 不符" in x.message for x in r.errors)
    lines = fixture_lines()
    lines[j] = rebuild("$IIALC,01,01,01,0*00")
    r = check_lines(lines)
    assert any("缺少同秒窗 ALF 的警報 ['002']" in x.message for x in r.errors)
    # 週期:40 個秒窗只有第 1 與第 35 秒有 ALC → 間隔 34 s > 30 s
    seq = []
    for sec in range(1, 41):
        seq.append(rebuild(f"$GPGGA,2230{sec:02d}.00,2229.7018,N,12012.3014,E,1,10,0.9,0.0,M,0.0,M,,*00"))
        if sec in (1, 35):
            seq.append(rebuild("$IIALC,01,01,00,0*00"))
    r = check_lines(seq)
    assert r.alc == {"count": 2, "seconds": [1, 35], "maxGap_s": 34, "lastList": []}
    assert any(x.check == "alc" and "ALC 間隔 34 s > 30 s" in x.message for x in r.errors)


def test_vdo_own_ship_checks_detected():
    lines = fixture_lines()
    # 把 T1 的 VDM 位置報告改成 VDO:自船 MMSI 不一致(416100001 vs 416300101)、位置與同批 GGA 相差數 nm、同一 MMSI 同時為 VDM(靜態)與 VDO
    k = next(i for i, l in enumerate(lines) if l.startswith("!AIVDM,1,1,,A,"))
    lines[k] = rebuild(lines[k].replace("!AIVDM", "!AIVDO"))
    r = check_lines(lines)
    msgs = [x.message for x in r.errors if x.check == "ais"]
    assert any("自船 VDO 的 MMSI 不一致" in m for m in msgs)
    assert any("同時出現在 VDM(他船)與 VDO(自船)" in m for m in msgs)
    # 第 10 秒的 VDO:GNSS 失效 → 位置必須為 91/181;把它換成有位置的句子(同一 MMSI 416100001 的類型 1,取自第 1 秒 GGA 位置)→ 錯誤
    valid_lines = [l for l in fixture_lines() if l.startswith("$GPGGA") and ",1,10," in l]
    assert valid_lines
    # 自製類型 1:MMSI 416100001、位置 = 第 1 秒 GGA(22°29.7018′N 120°12.3014′E)、COG 36.5、SOG 8.1、航向 45、UTC 秒 10
    bits = ""
    def u(v, n):
        nonlocal bits
        bits += format(int(v) & ((1 << n) - 1), f"0{n}b")
    u(1, 6); u(0, 2); u(416100001, 30); u(0, 4); u(0, 8); u(81, 10); u(1, 1)
    u(round((120 + 12.3014 / 60) * 600000), 28); u(round((22 + 29.7018 / 60) * 600000), 27); u(365, 12); u(45, 9); u(10, 6); u(0, 2); u(0, 3); u(0, 1); u(0, 19)
    payload = "".join(chr(v + (48 if v < 40 else 56)) for v in (int(bits[i:i + 6], 2) for i in range(0, 168, 6)))  # 6 位元裝甲(AisEncoder.ArmorChar)
    vdo = rebuild(f"!AIVDO,1,1,,A,{payload},0*00")
    lines = fixture_lines()
    k10 = next(i for i, l in enumerate(lines) if l.startswith("!AIVDO,1,1"))
    lines[k10] = vdo
    r = check_lines(lines)
    assert any("但同批 GGA 品質 0(應為無資料值 91/181)" in x.message for x in r.errors)
    assert any("GNSS 失效時 COG/SOG 應為無資料值" in x.message for x in r.errors)
    # 同一句放到第 1 秒(GNSS 正常):位置與 GGA 一致 → 無位置錯誤;航向 45 = THS 45.0
    lines = [l for l in fixture_lines() if not l.startswith("!AIVDO,1,1")]
    g1 = next(i for i, l in enumerate(lines) if l.startswith("$GPGGA,223001.00"))
    lines.insert(g1 + 7, vdo)
    r = check_lines(lines)
    assert not [x for x in r.errors if x.check == "ais"], [x.message for x in r.errors]
    own = next(t for t in r.ais if t.own)
    assert own.mmsi == 416100001 and len(own.positions) == 1 and own.positions[0].sec == 1
    # 航向與同批 THS 不符(改 THS)
    lines2 = list(lines)
    for i in range(g1 - 4, g1):
        if lines2[i].startswith("$HETHS"):
            lines2[i] = rebuild("$HETHS,50.0,A*00")
    r2 = check_lines(lines2)
    assert any("自船 VDO 航向 45 ≠ 同批 THS 50.0" in x.message for x in r2.errors)


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
    assert any("THS" in m and "state.heading" in m for m in msgs)  # 第二十四輪:THS 亦與 state.heading 比對
    assert any("positionValid=False 不一致" in m for m in msgs)
    assert r.state_compare is not None and r.state_compare.mismatches == 4
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
    assert "| HDT | HE | 100 | 10.00 | 10.00 | ok |" in md and "| THS | HE | 100 | 10.00 | 10.00 | ok |" in md
    assert "| 416300101 | VDM | EVER ORIENT | BKEO | 1 |" in md and "| 416100001 | VDO 自船 | FS BITUMEN NO.1 | BIAX | 1 |" in md
    assert "| 002 | GNSS POSITION LOST | 5 | 5 | V | B/A | 1 |" in md and "ALC 週期清單:2 次(秒窗 1,6;最大間隔 5 s;最後清單 ['002'])" in md
    assert "**通過**" in md and "tools/opencpn-check.md" in md
    j = nc.result_to_json(r)
    assert j["passed"] is True and j["ggaQuality"][4]["quality"] == "0" and j["stateCompare"]["compared"] == 10
    assert j["alf"][0]["identifier"] == "002" and j["alc"]["count"] == 2 and j["ais"][0]["own"] is True and j["ais"][1]["own"] is False


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
