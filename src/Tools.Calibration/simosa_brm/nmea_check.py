"""NMEA 0183 一致性檢驗(規劃書第 14 章「儀器 NMEA」列:校驗和、句型速率、OpenCPN 正確顯示;IEC 61162-1)。

接收 SimCore.Host 的 NMEA(UDP 單播/多播即時擷取,或 Host ``--nmea-file`` 寫出的 .nmea 檔)並檢查:

- 校驗和、句長(≤ 82)、位址欄(talker + 句型)、IEC 61162-1 欄位數(``SPECS``);
- 各句型速率:HDT/ROT/RSA 10 Hz,GGA/RMC/VTG/VBW/DPT/MWV 1 Hz(規劃書第 5.3 節),每警報 ALR 1 Hz(sensors-alerts.md 第 6 節),
  每目標 TTM 1 Hz(radar-arpa.md 第 6 節),AIS 類型 1 位置報告每 10 s、類型 5 靜態資料(NmeaGateway);容差 ±20%;
- 時間戳單調(GGA 時刻遞增、同批 RMC/TTM/ALR 時刻相同、RMC 日期可解析);
- 有效旗標一致(GGA 品質 0 ⇔ RMC 狀態 V ⇔ VTG 模式 N ⇒ VBW 對地 V;故障注入後 GGA 品質欄位變化逐秒列出);
- !AIVDM 可解碼(多句組合、6 位元解裝甲、類型 1 的 MMSI/位置/COG/SOG/航向/UTC 秒、類型 5 的船名/呼號)、ALR 欄位;
- 可選:同時連 WebSocket 狀態(``--ws``)或離線狀態 .jsonl(``--state``,核心紀錄或 WebSocket 擷取)比對同一時刻的位置/航向/水深/SOG/COG。

輸出 Markdown 報告(``--report``,預設 build/nmea/report.md)與可選 JSON;結束碼 0 = 全部通過、1 = 有不合格項、2 = 參數/輸入錯誤。
本工具只讀取輸出,不影響核心狀態;所有輸出留在專案資料夾內(CLAUDE.md)。

用法::

    uv run python -m simosa_brm.nmea_check --file build/nmea/E05.nmea --report build/nmea/report.md
    uv run python -m simosa_brm.nmea_check --udp 10110 --seconds 30 --ws ws://127.0.0.1:8765
    uv run python -m simosa_brm.nmea_check --multicast 239.192.0.1:60001 --seconds 30 --iface 192.168.77.21

實機驗收步驟見 tools/opencpn-check.md;OpenCPN 連線設定見 config/opencpn/README.md。
"""

from __future__ import annotations

import argparse
import asyncio
import json
import math
import socket
import struct
import sys
import time
from dataclasses import asdict, dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Iterable

from . import paths

# ---------------------------------------------------------------------------------------------------------------------
# 句型規格
# ---------------------------------------------------------------------------------------------------------------------

RATE_TOLERANCE = 0.20
"""速率容差(±20%)"""

MAX_SENTENCE_CHARS = 82
"""NMEA 0183 句長上限(含 '$' 不含 CR LF;與 NmeaTests 相同的判準)"""

AIS_POSITION_INTERVAL_S = 10
"""NmeaGateway.AisPositionIntervalS 預設值"""


@dataclass(frozen=True)
class SentenceSpec:
    """一種句型的檢查規格。"""

    field_counts: tuple[int, ...]
    """允許的欄位數(不含位址欄;IEC 61162-1 Ed.4/5;多值 = 不同版次)"""
    rate_hz: float | None
    """預期速率(None = 事件/條件式,不檢查速率)"""
    scope: str
    """own = 自船每批;target = 每目標編號;alert = 每警報編號;ais = 每 MMSI;none = 只檢查欄位"""
    source: str
    """速率/欄位依據"""
    mandatory: bool = False
    """自船必送(感測器有效時);缺少即 error"""
    talker: str | None = None
    """閘道的預期 talker(不同只 warning;OpenCPN 不依 talker 篩選)"""


SPECS: dict[str, SentenceSpec] = {
    "GGA": SentenceSpec((14,), 1.0, "own", "規劃書 5.3(GGA 1 Hz)", True, "GP"),
    "RMC": SentenceSpec((12, 13), 1.0, "own", "規劃書 5.3(與 GGA 同批 1 Hz;NMEA 2.3 = 12 欄、4.10 = 13 欄)", True, "GP"),
    "VTG": SentenceSpec((9,), 1.0, "own", "NmeaEncoder.SlowSentences 1 Hz", True, "GP"),
    "VBW": SentenceSpec((6, 10), 1.0, "own", "NmeaEncoder.SlowSentences 1 Hz(NMEA 2.3 = 10 欄)", True, "VD"),
    "DPT": SentenceSpec((2, 3), 1.0, "own", "NmeaEncoder.SlowSentences 1 Hz(測深儀失效停送)", False, "SD"),
    "MWV-R": SentenceSpec((5,), 1.0, "own", "NmeaEncoder.SlowSentences 1 Hz(相對風)", True, "WI"),
    "MWV-T": SentenceSpec((5,), 1.0, "own", "NmeaEncoder.SlowSentences 1 Hz(真風)", True, "WI"),
    "HDT": SentenceSpec((2,), 10.0, "own", "規劃書 5.3(HDT 10 Hz;電羅經失效停送)", False, "HE"),
    "THS": SentenceSpec((2,), 10.0, "own", "IEC 61162-1(本版閘道未送;若有則 10 Hz)", False, "HE"),
    "ROT": SentenceSpec((2,), 10.0, "own", "NmeaEncoder.FastSentences 10 Hz", True, "TI"),
    "RSA": SentenceSpec((4,), 10.0, "own", "NmeaEncoder.FastSentences 10 Hz", True, "ER"),
    "ALR": SentenceSpec((5,), 1.0, "alert", "sensors-alerts.md 第 6 節(每個非 normal 警報 1 Hz)", False, "II"),
    "TTM": SentenceSpec((15,), 1.0, "target", "radar-arpa.md 第 6 節 / NmeaGateway(每目標 1 Hz)", False, "RA"),
    "TLL": SentenceSpec((9,), 1.0, "target", "radar-arpa.md 第 6 節(雷達服務每目標 1 Hz)", False, "RA"),
    "VDM": SentenceSpec((6,), None, "ais", "NmeaGateway(類型 1 每 10 s、類型 5 出現後數秒再每 6 min)", False, "AI"),
    "VDO": SentenceSpec((6,), None, "none", "IEC 61162-1(自船 AIS;本版未送)", False, "AI"),
    "DBT": SentenceSpec((6,), None, "none", "IEC 61162-1", False, "SD"),
    "VHW": SentenceSpec((8,), None, "none", "IEC 61162-1", False, "VD"),
    "HDG": SentenceSpec((5,), None, "none", "IEC 61162-1", False, "HE"),
    "ZDA": SentenceSpec((6,), None, "none", "IEC 61162-1", False, "GP"),
    "GLL": SentenceSpec((7,), None, "none", "IEC 61162-1", False, "GP"),
    "RPM": SentenceSpec((5,), None, "none", "IEC 61162-1", False, "ER"),
}

RESERVED_CHARS = set("$!*,\\^~")
"""IEC 61162-1 保留字元(欄位內不得出現)"""

# ---------------------------------------------------------------------------------------------------------------------
# 解析
# ---------------------------------------------------------------------------------------------------------------------


def checksum(payload: str) -> str:
    """'$' 與 '*' 之間所有字元的 XOR,兩位大寫十六進位(與 C# NmeaSentence.Checksum 相同)。"""
    x = 0
    for ch in payload:
        x ^= ord(ch) & 0xFF
    return f"{x:02X}"


@dataclass
class Issue:
    severity: str
    """error | warning | info"""
    check: str
    """checksum | format | fields | rate | time | content | ais | alr | state | input"""
    message: str
    line: int | None = None
    sentence: str | None = None


@dataclass
class Sentence:
    index: int
    """輸入序號(1 起算;檔案 = 行號)"""
    raw: str
    """不含 CR LF 與 TAG block"""
    start: str
    talker: str
    formatter: str
    fields: list[str]
    checksum_ok: bool
    tag: str | None = None
    recv_t: float | None = None
    """即時擷取的接收時刻(time.monotonic)"""
    sec: int = 0
    """所屬秒窗(第 n 個批次標記;首個標記之前為 0)"""

    @property
    def key(self) -> str:
        """統計鍵:MWV 依參考欄分 MWV-R / MWV-T,其餘為句型。"""
        if self.formatter == "MWV" and len(self.fields) >= 2:
            return f"MWV-{self.fields[1] or '?'}"
        return self.formatter


def parse_line(raw: str, index: int, issues: list[Issue], recv_t: float | None = None) -> Sentence | None:
    """解析一行;格式/校驗和問題寫入 ``issues``。回傳 None 表示空行或完全無法辨識。"""
    line = raw.rstrip("\r\n")
    if not line.strip():
        return None
    tag = None
    if line.startswith("\\"):
        # IEC 61162-450 TAG block:\...*hh\ 之後才是句型
        end = line.find("\\", 1)
        if end > 0:
            tag = line[1:end]
            line = line[end + 1 :]
    if not line or line[0] not in "$!":
        issues.append(Issue("error", "format", "不是以 '$' 或 '!' 開頭", index, line[:82]))
        return None
    if len(line) > MAX_SENTENCE_CHARS:
        issues.append(Issue("error", "format", f"句長 {len(line)} > {MAX_SENTENCE_CHARS}", index, line[:82]))
    star = line.rfind("*")
    ok = False
    if star < 0 or star + 3 != len(line):
        issues.append(Issue("error", "checksum", "缺少 '*hh' 校驗和", index, line))
        body = line[1:] if star < 0 else line[1:star]
    else:
        body = line[1:star]
        given = line[star + 1 :]
        try:
            int(given, 16)
            ok = checksum(body) == given.upper()
        except ValueError:
            ok = False
        if not ok:
            issues.append(Issue("error", "checksum", f"校驗和不符:句內 {given},計算 {checksum(body)}", index, line))
    bad = [c for c in body if not (0x20 <= ord(c) <= 0x7E)]
    if bad:
        issues.append(Issue("error", "format", f"含非可列印 ASCII 字元 {bad[:3]!r}", index, line))
    parts = body.split(",")
    address = parts[0]
    if address.startswith("P"):
        talker, formatter = "P", address[1:]
    elif len(address) == 5:
        talker, formatter = address[:2], address[2:]
    else:
        issues.append(Issue("warning", "format", f"位址欄 '{address}' 不是 talker(2)+ 句型(3)", index, line))
        talker, formatter = address[:2], address[2:]
    fields = parts[1:]
    for f in fields:
        if any(c in RESERVED_CHARS for c in f):
            issues.append(Issue("error", "format", f"欄位含保留字元:{f!r}", index, line))
            break
    return Sentence(index, line, line[0], talker, formatter, fields, ok, tag, recv_t)


def parse_stream(lines: Iterable[tuple[str, float | None]]) -> tuple[list[Sentence], list[Issue]]:
    """解析 (行, 接收時刻) 序列。"""
    issues: list[Issue] = []
    out: list[Sentence] = []
    for i, (raw, t) in enumerate(lines, start=1):
        s = parse_line(raw, i, issues, t)
        if s is not None:
            out.append(s)
    return out, issues


# ---------------------------------------------------------------------------------------------------------------------
# 欄位工具
# ---------------------------------------------------------------------------------------------------------------------


def _num(s: str) -> float | None:
    try:
        return float(s)
    except ValueError:
        return None


def parse_hms(s: str) -> float | None:
    """hhmmss[.ss] → 當日秒數。"""
    if len(s) < 6 or not s[:6].isdigit():
        return None
    try:
        h, m, sec = int(s[0:2]), int(s[2:4]), float(s[4:])
    except ValueError:
        return None
    if h > 23 or m > 59 or sec >= 61:
        return None
    return h * 3600 + m * 60 + sec


def parse_ddmmyy(s: str) -> datetime | None:
    if len(s) != 6 or not s.isdigit():
        return None
    try:
        return datetime(2000 + int(s[4:6]), int(s[2:4]), int(s[0:2]), tzinfo=timezone.utc)
    except ValueError:
        return None


def parse_lat(value: str, hemi: str) -> float | None:
    """ddmm.mmmm + N/S → 度。"""
    if len(value) < 4 or hemi not in ("N", "S"):
        return None
    d, m = _num(value[:2]), _num(value[2:])
    if d is None or m is None or m >= 60 or d > 90:
        return None
    v = d + m / 60.0
    return -v if hemi == "S" else v


def parse_lon(value: str, hemi: str) -> float | None:
    """dddmm.mmmm + E/W → 度。"""
    if len(value) < 5 or hemi not in ("E", "W"):
        return None
    d, m = _num(value[:3]), _num(value[3:])
    if d is None or m is None or m >= 60 or d > 180:
        return None
    v = d + m / 60.0
    return -v if hemi == "W" else v


def distance_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    """球面距離(m;haversine,R = 6371 km;比對用)。"""
    r = 6_371_000.0
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dp, dl = p2 - p1, math.radians(lon2 - lon1)
    a = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * r * math.asin(min(1.0, math.sqrt(a)))


def angle_diff(a: float, b: float) -> float:
    """角度差(0–180)。"""
    d = abs((a - b + 180.0) % 360.0 - 180.0)
    return d


# ---------------------------------------------------------------------------------------------------------------------
# AIS 解碼
# ---------------------------------------------------------------------------------------------------------------------

SIX_BIT_TABLE = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_ !\"#$%&'()*+,-./0123456789:;<=>?"


def dearmor(payload: str) -> str:
    """AIS 裝甲字元 → 位元字串(每字 6 位元)。"""
    bits: list[str] = []
    for c in payload:
        v = ord(c) - 48
        if v > 40:
            v -= 8
        if v < 0 or v > 63:
            raise ValueError(f"非 AIS 裝甲字元 {c!r}")
        bits.append(f"{v:06b}")
    return "".join(bits)


def _u(bits: str, start: int, length: int) -> int:
    return int(bits[start : start + length], 2)


def _s(bits: str, start: int, length: int) -> int:
    v = _u(bits, start, length)
    return v - (1 << length) if v >= (1 << (length - 1)) else v


def _text(bits: str, start: int, chars: int) -> str:
    out = []
    for i in range(chars):
        seg = bits[start + 6 * i : start + 6 * i + 6]
        if len(seg) < 6:
            break
        out.append(SIX_BIT_TABLE[int(seg, 2)])
    return "".join(out).split("@", 1)[0].rstrip()


@dataclass
class AisMessage:
    msg_type: int
    mmsi: int
    bit_length: int
    sec: int
    line: int
    nav_status: int | None = None
    rot: int | None = None
    sog: float | None = None
    lon: float | None = None
    lat: float | None = None
    cog: float | None = None
    heading: int | None = None
    utc_second: int | None = None
    imo: int | None = None
    call_sign: str | None = None
    name: str | None = None
    ship_type: int | None = None
    draught: float | None = None
    destination: str | None = None


def decode_ais(bits: str, sec: int, line: int) -> AisMessage:
    """解碼類型 1/2/3(位置報告)與 5(靜態與航程資料);其他類型只取類型與 MMSI。"""
    if len(bits) < 38:
        raise ValueError(f"位元數 {len(bits)} < 38,無法取得類型與 MMSI")
    t = _u(bits, 0, 6)
    m = AisMessage(t, _u(bits, 8, 30), len(bits), sec, line)
    if t in (1, 2, 3):
        if len(bits) < 168:
            raise ValueError(f"類型 {t} 位元數 {len(bits)} < 168")
        m.nav_status = _u(bits, 38, 4)
        m.rot = _s(bits, 42, 8)
        m.sog = _u(bits, 50, 10) / 10.0
        m.lon = _s(bits, 61, 28) / 600000.0
        m.lat = _s(bits, 89, 27) / 600000.0
        m.cog = _u(bits, 116, 12) / 10.0
        m.heading = _u(bits, 128, 9)
        m.utc_second = _u(bits, 137, 6)
    elif t == 5:
        if len(bits) < 420:
            raise ValueError(f"類型 5 位元數 {len(bits)} < 420")
        m.imo = _u(bits, 40, 30)
        m.call_sign = _text(bits, 70, 7)
        m.name = _text(bits, 112, 20)
        m.ship_type = _u(bits, 232, 8)
        m.draught = _u(bits, 294, 8) / 10.0
        m.destination = _text(bits, 302, 20)
    return m


@dataclass
class AisTrack:
    mmsi: int
    positions: list[AisMessage] = field(default_factory=list)
    static: AisMessage | None = None
    intervals_s: list[int] = field(default_factory=list)

    @property
    def name(self) -> str:
        return self.static.name if self.static and self.static.name else ""


# ---------------------------------------------------------------------------------------------------------------------
# 結果
# ---------------------------------------------------------------------------------------------------------------------


@dataclass
class RateRow:
    key: str
    talkers: str
    count: int
    windows: int
    rate_hz: float | None
    expected_hz: float | None
    status: str
    """ok | fail | insufficient | missing | n/a"""
    note: str = ""


@dataclass
class StateCompare:
    compared: int = 0
    unmatched: int = 0
    max_position_m: float = 0.0
    max_heading_deg: float = 0.0
    max_depth_m: float = 0.0
    max_sog_kn: float = 0.0
    max_cog_deg: float = 0.0
    mismatches: int = 0


@dataclass
class CheckResult:
    source: str
    sentences_total: int
    seconds_observed: int
    windows_used: int
    marker: str
    rates: list[RateRow]
    issues: list[Issue]
    ais: list[AisTrack]
    alerts: list[dict[str, Any]]
    gga_quality: list[dict[str, str]]
    first_time: str | None
    last_time: str | None
    state_compare: StateCompare | None = None
    capture_seconds: float | None = None

    @property
    def errors(self) -> list[Issue]:
        return [i for i in self.issues if i.severity == "error"]

    @property
    def warnings(self) -> list[Issue]:
        return [i for i in self.issues if i.severity == "warning"]

    @property
    def passed(self) -> bool:
        return not self.errors

    def rate(self, key: str) -> RateRow | None:
        for r in self.rates:
            if r.key == key:
                return r
        return None


# ---------------------------------------------------------------------------------------------------------------------
# 檢查
# ---------------------------------------------------------------------------------------------------------------------


def _assign_windows(sentences: list[Sentence]) -> tuple[str, int]:
    """以批次標記切秒窗:GGA → RMC → 帶時刻欄的 TTM/TLL/ALR 時刻改變 → 接收時刻的整數秒;回傳 (標記, 秒窗數)。"""
    keys = {s.key for s in sentences}
    marker = "GGA" if "GGA" in keys else "RMC" if "RMC" in keys else None
    n = 0
    if marker:
        for s in sentences:
            if s.key == marker:
                n += 1
            s.sec = n
        return marker, n
    timed = {"TTM": 13, "TLL": 6, "ALR": 0}  # 時刻欄索引(不含位址欄)
    if any(k in keys for k in timed):
        last = None
        for s in sentences:
            if s.formatter in timed and len(s.fields) > timed[s.formatter]:
                t = s.fields[timed[s.formatter]]
                if t != last:
                    n += 1
                    last = t
            s.sec = n
        return "time-field", n
    if sentences and all(s.recv_t is not None for s in sentences):
        t0 = sentences[0].recv_t or 0.0
        for s in sentences:
            s.sec = int((s.recv_t or 0.0) - t0) + 1
        return "receive-time", (sentences[-1].sec if sentences else 0)
    for s in sentences:
        s.sec = 1
    return "none", 1 if sentences else 0


def _rate_status(rate: float | None, expected: float | None, tolerance: float) -> str:
    if expected is None or rate is None:
        return "n/a"
    return "ok" if abs(rate - expected) <= tolerance * expected else "fail"


def check_sentences(
    sentences: list[Sentence],
    issues: list[Issue],
    *,
    source: str,
    tolerance: float = RATE_TOLERANCE,
    ais_interval_s: int = AIS_POSITION_INTERVAL_S,
    states: list[dict[str, Any]] | None = None,
    capture_seconds: float | None = None,
) -> CheckResult:
    """對已解析的句型做全部檢查。"""
    marker, n_windows = _assign_windows(sentences)
    # 完整秒窗:1..N-1(最後一個窗可能被擷取截斷);只有 1 個窗時用它但標示不足
    if n_windows >= 2:
        lo, hi = 1, n_windows - 1
    else:
        lo, hi = 1, 1
    windows = hi - lo + 1 if n_windows >= 1 else 0

    # ---- 欄位數、talker
    for s in sentences:
        spec = SPECS.get(s.key) or SPECS.get(s.formatter)
        if spec is None:
            continue
        if len(s.fields) not in spec.field_counts:
            issues.append(Issue("error", "fields", f"{s.talker}{s.formatter} 欄位數 {len(s.fields)},IEC 61162-1 應為 {'/'.join(map(str, spec.field_counts))}", s.index, s.raw))
        if spec.talker and s.talker != spec.talker and s.talker != "P":
            issues.append(Issue("warning", "fields", f"{s.formatter} talker {s.talker}(閘道預期 {spec.talker})", s.index, s.raw))
    unknown = sorted({s.formatter for s in sentences if s.key not in SPECS and s.formatter not in SPECS})
    for f in unknown:
        issues.append(Issue("info", "fields", f"未列入規格表的句型 {f}(只檢查校驗和)"))

    # ---- 速率
    rates: list[RateRow] = []
    by_key: dict[str, list[Sentence]] = {}
    for s in sentences:
        by_key.setdefault(s.key, []).append(s)
    insufficient = n_windows < 2
    for key, spec in SPECS.items():
        group = by_key.get(key, [])
        talkers = "/".join(sorted({s.talker for s in group}))
        if spec.scope == "own":
            in_win = [s for s in group if lo <= s.sec <= hi]
            if not group:
                if spec.mandatory:
                    rates.append(RateRow(key, "", 0, windows, None, spec.rate_hz, "missing", "自船必送句型缺少"))
                    issues.append(Issue("error", "rate", f"缺少自船必送句型 {key}({spec.source})"))
                continue
            rate = len(in_win) / windows if windows else None
            status = _rate_status(rate, spec.rate_hz, tolerance)
            note = spec.source
            if insufficient:
                status = "insufficient" if status != "fail" or windows == 0 else status
                note += ";觀測不足 2 個完整秒窗"
            if status == "fail":
                issues.append(Issue("error", "rate", f"{key} 速率 {rate:.2f} Hz,預期 {spec.rate_hz:g} Hz ±{tolerance:.0%}({spec.source})"))
            rates.append(RateRow(key, talkers, len(group), windows, rate, spec.rate_hz, status, note))
        elif spec.scope in ("target", "alert"):
            if not group:
                continue
            idx = 0 if spec.scope == "target" else 1
            per: dict[str, list[Sentence]] = {}
            for s in group:
                if len(s.fields) > idx:
                    per.setdefault(s.fields[idx], []).append(s)
            worst = "ok"
            notes = []
            for ident, lst in sorted(per.items()):
                secs = [s.sec for s in lst if lo <= s.sec <= hi]
                if not secs:
                    continue
                span = max(secs) - min(secs) + 1
                rate = len(secs) / span
                st = _rate_status(rate, spec.rate_hz, tolerance)
                if span < 3:
                    st = "insufficient" if st != "fail" else st
                if st == "fail":
                    issues.append(Issue("error", "rate", f"{key} {ident} 速率 {rate:.2f} Hz(窗 {span} s),預期 {spec.rate_hz:g} Hz ±{tolerance:.0%}"))
                    worst = "fail"
                elif st == "insufficient" and worst == "ok":
                    worst = "insufficient"
                notes.append(f"{ident}:{len(secs)}/{span}s")
            rates.append(RateRow(key, talkers, len(group), windows, None, spec.rate_hz, worst, "、".join(notes) or spec.source))
        elif spec.scope == "ais":
            if group:
                rates.append(RateRow(key, talkers, len(group), windows, None, None, "n/a", f"類型 1 每 {ais_interval_s} s/目標;見 AIS 表"))
        elif group:
            rates.append(RateRow(key, talkers, len(group), windows, None, None, "n/a", spec.source))

    # ---- 時間戳與自船內容(逐秒窗)
    gga_quality: list[dict[str, str]] = []
    prev_t: float | None = None
    first_time = last_time = None
    win_time: dict[int, str] = {}
    last_fast: dict[str, Sentence] = {}
    batch_fast: dict[int, dict[str, Sentence]] = {}
    gga_by_sec: dict[int, Sentence] = {}
    rmc_by_sec: dict[int, Sentence] = {}
    for s in sentences:
        if s.formatter in ("HDT", "ROT", "RSA", "THS"):
            last_fast[s.formatter] = s
        if s.formatter == "GGA" and len(s.fields) >= 14:
            batch_fast[s.sec] = dict(last_fast)
            gga_by_sec[s.sec] = s
            t = parse_hms(s.fields[0])
            if t is None:
                issues.append(Issue("error", "time", f"GGA 時刻 '{s.fields[0]}' 無法解析", s.index, s.raw))
            else:
                if prev_t is not None:
                    dt = t - prev_t
                    if dt <= 0 and not (prev_t > 86000 and t < 400):
                        issues.append(Issue("error", "time", f"GGA 時刻未遞增:{prev_t:.2f} → {t:.2f} s", s.index, s.raw))
                    elif abs(dt - 1.0) > 0.05 and dt > 0:
                        issues.append(Issue("warning", "time", f"GGA 時刻增量 {dt:.2f} s(非 1 s:時間倍率≠1、凍結或掉拍?)", s.index, s.raw))
                prev_t = t
                first_time = first_time or s.fields[0]
                last_time = s.fields[0]
            win_time[s.sec] = s.fields[0]
            q = s.fields[5]
            gga_quality.append({"time": s.fields[0], "quality": q, "satellites": s.fields[6], "hdop": s.fields[7]})
            if not q.isdigit():
                issues.append(Issue("error", "content", f"GGA 品質欄 '{q}' 非數字", s.index, s.raw))
            elif q != "0":
                if parse_lat(s.fields[1], s.fields[2]) is None or parse_lon(s.fields[3], s.fields[4]) is None:
                    issues.append(Issue("error", "content", "GGA 位置欄無法解析(ddmm.mmmm,N,dddmm.mmmm,E)", s.index, s.raw))
                if not s.fields[6].isdigit():
                    issues.append(Issue("error", "content", "GGA 衛星數非數字", s.index, s.raw))
            else:
                if s.fields[1] or s.fields[3]:
                    issues.append(Issue("warning", "content", "GGA 品質 0(無定位)但位置欄非空", s.index, s.raw))
        elif s.formatter == "RMC" and len(s.fields) >= 12:
            rmc_by_sec[s.sec] = s
            if parse_hms(s.fields[0]) is None:
                issues.append(Issue("error", "time", f"RMC 時刻 '{s.fields[0]}' 無法解析", s.index, s.raw))
            if parse_ddmmyy(s.fields[8]) is None:
                issues.append(Issue("error", "time", f"RMC 日期 '{s.fields[8]}' 無法解析(ddmmyy)", s.index, s.raw))
            if s.fields[1] not in ("A", "V"):
                issues.append(Issue("error", "content", f"RMC 狀態 '{s.fields[1]}' 非 A/V", s.index, s.raw))
            if s.fields[11] not in ("A", "D", "E", "M", "S", "N", "F", "R", "P"):
                issues.append(Issue("error", "content", f"RMC 模式 '{s.fields[11]}' 非 NMEA 2.3 模式字元", s.index, s.raw))
            if s.fields[1] == "A":
                if parse_lat(s.fields[2], s.fields[3]) is None or parse_lon(s.fields[4], s.fields[5]) is None:
                    issues.append(Issue("error", "content", "RMC 位置欄無法解析", s.index, s.raw))
                sog, cog = _num(s.fields[6]), _num(s.fields[7])
                if sog is None or sog < 0 or cog is None or not (0 <= cog <= 360):
                    issues.append(Issue("error", "content", "RMC SOG/COG 超出範圍", s.index, s.raw))
            gga = gga_by_sec.get(s.sec)
            if gga is not None:
                if gga.fields[0] != s.fields[0]:
                    issues.append(Issue("error", "time", f"同批 RMC 時刻 {s.fields[0]} ≠ GGA {gga.fields[0]}", s.index, s.raw))
                invalid = gga.fields[5] == "0"
                if invalid != (s.fields[1] == "V"):
                    issues.append(Issue("error", "content", f"GGA 品質 {gga.fields[5]} 與 RMC 狀態 {s.fields[1]} 不一致", s.index, s.raw))
                if not invalid and (gga.fields[1], gga.fields[2], gga.fields[3], gga.fields[4]) != (s.fields[2], s.fields[3], s.fields[4], s.fields[5]):
                    issues.append(Issue("error", "content", "同批 RMC 位置 ≠ GGA 位置", s.index, s.raw))
        elif s.formatter == "VTG" and len(s.fields) >= 9:
            if (s.fields[1], s.fields[3], s.fields[5], s.fields[7]) != ("T", "M", "N", "K"):
                issues.append(Issue("error", "content", "VTG 單位欄應為 T,M,N,K", s.index, s.raw))
            gga = gga_by_sec.get(s.sec)
            if gga is not None and (gga.fields[5] == "0") != (s.fields[8] == "N"):
                issues.append(Issue("error", "content", f"GGA 品質 {gga.fields[5]} 與 VTG 模式 {s.fields[8]} 不一致", s.index, s.raw))
            if s.fields[8] != "N":
                cog, kn, kmh = _num(s.fields[0]), _num(s.fields[4]), _num(s.fields[6])
                if cog is None or not (0 <= cog <= 360) or kn is None or kmh is None:
                    issues.append(Issue("error", "content", "VTG COG/SOG 無法解析", s.index, s.raw))
                elif abs(kmh - kn * 1.852) > 0.11:
                    issues.append(Issue("error", "content", f"VTG km/h {kmh} ≠ 1.852 × {kn} kn", s.index, s.raw))
        elif s.formatter == "VBW" and len(s.fields) >= 6:
            flags = [s.fields[i] for i in (2, 5) + ((7, 9) if len(s.fields) >= 10 else ())]
            if any(f not in ("A", "V") for f in flags):
                issues.append(Issue("error", "content", "VBW 狀態欄非 A/V", s.index, s.raw))
            if s.fields[2] == "A" and (_num(s.fields[0]) is None or _num(s.fields[1]) is None):
                issues.append(Issue("error", "content", "VBW 對水速度欄(狀態 A)無法解析", s.index, s.raw))
            if s.fields[5] == "A" and (_num(s.fields[3]) is None or _num(s.fields[4]) is None):
                issues.append(Issue("error", "content", "VBW 對地速度欄(狀態 A)無法解析", s.index, s.raw))
            gga = gga_by_sec.get(s.sec)
            if gga is not None and gga.fields[5] == "0" and s.fields[5] == "A":
                issues.append(Issue("error", "content", "GGA 品質 0 但 VBW 對地狀態 A", s.index, s.raw))
        elif s.formatter == "HDT" and len(s.fields) >= 2:
            h = _num(s.fields[0])
            if h is None or not (0 <= h <= 360) or s.fields[1] != "T":
                issues.append(Issue("error", "content", "HDT 航向超出 0–360 或單位非 T", s.index, s.raw))
        elif s.formatter == "THS" and len(s.fields) >= 2:
            h = _num(s.fields[0])
            if (s.fields[1] != "V" and (h is None or not (0 <= h <= 360))) or s.fields[1] not in ("A", "E", "M", "S", "V"):
                issues.append(Issue("error", "content", "THS 航向或模式欄無效", s.index, s.raw))
        elif s.formatter == "ROT" and len(s.fields) >= 2:
            if s.fields[1] not in ("A", "V") or (s.fields[1] == "A" and _num(s.fields[0]) is None):
                issues.append(Issue("error", "content", "ROT 數值或狀態欄無效", s.index, s.raw))
        elif s.formatter == "RSA" and len(s.fields) >= 4:
            if s.fields[1] not in ("A", "V") or s.fields[3] not in ("A", "V") or (s.fields[1] == "A" and _num(s.fields[0]) is None):
                issues.append(Issue("error", "content", "RSA 舵角或狀態欄無效", s.index, s.raw))
        elif s.formatter == "DPT" and len(s.fields) >= 2:
            d, off = _num(s.fields[0]), _num(s.fields[1])
            if d is None or d < 0 or off is None:
                issues.append(Issue("error", "content", "DPT 水深/偏移欄無效", s.index, s.raw))
        elif s.formatter == "MWV" and len(s.fields) >= 5:
            a, v = _num(s.fields[0]), _num(s.fields[2])
            if s.fields[1] not in ("R", "T") or s.fields[3] not in ("N", "M", "K") or s.fields[4] not in ("A", "V"):
                issues.append(Issue("error", "content", "MWV 參考/單位/狀態欄無效", s.index, s.raw))
            elif s.fields[4] == "A" and (a is None or not (0 <= a <= 360) or v is None or v < 0):
                issues.append(Issue("error", "content", "MWV 風角/風速超出範圍", s.index, s.raw))
        elif s.formatter == "TTM" and len(s.fields) >= 15:
            f = s.fields
            rng, brg, spd, crs, cpa, tcpa = (_num(f[i]) for i in (1, 2, 4, 5, 7, 8))
            if not (len(f[0]) == 2 and f[0].isdigit()):
                issues.append(Issue("error", "content", "TTM 目標編號應為 2 位數字", s.index, s.raw))
            if f[11] not in ("T", "Q", "L"):
                issues.append(Issue("error", "content", f"TTM 狀態 '{f[11]}' 非 T/Q/L", s.index, s.raw))
            if f[3] not in ("T", "R") or f[6] not in ("T", "R") or f[9] not in ("N", "K", "S") or f[14] not in ("A", "M", "R"):
                issues.append(Issue("error", "content", "TTM 單位/參考/取得方式欄無效", s.index, s.raw))
            if f[11] == "T" and (rng is None or rng < 0 or brg is None or not (0 <= brg <= 360) or spd is None or spd < 0 or crs is None or not (0 <= crs <= 360) or cpa is None or cpa < 0 or tcpa is None):
                issues.append(Issue("error", "content", "TTM 追蹤中的距離/方位/速度/航向/CPA/TCPA 無效", s.index, s.raw))
            if parse_hms(f[13]) is None:
                issues.append(Issue("error", "time", f"TTM 時刻 '{f[13]}' 無法解析", s.index, s.raw))
            elif s.sec in win_time and f[13] != win_time[s.sec]:
                issues.append(Issue("error", "time", f"同批 TTM 時刻 {f[13]} ≠ GGA {win_time[s.sec]}", s.index, s.raw))
            if len(f[10]) > 20:
                issues.append(Issue("warning", "content", f"TTM 目標名稱 {len(f[10])} 字(建議 ≤ 20)", s.index, s.raw))

    # ---- ALR
    alerts: dict[str, dict[str, Any]] = {}
    for s in sentences:
        if s.formatter != "ALR" or len(s.fields) < 5:
            continue
        f = s.fields
        if parse_hms(f[0]) is None:
            issues.append(Issue("error", "alr", f"ALR 時刻 '{f[0]}' 無法解析", s.index, s.raw))
        elif s.sec in win_time and f[0] != win_time[s.sec]:
            issues.append(Issue("error", "time", f"同批 ALR 時刻 {f[0]} ≠ GGA {win_time[s.sec]}", s.index, s.raw))
        if not (len(f[1]) == 3 and f[1].isdigit()):
            issues.append(Issue("error", "alr", f"ALR 警報編號 '{f[1]}' 應為 3 位數字", s.index, s.raw))
        if f[2] not in ("A", "V") or f[3] not in ("A", "V"):
            issues.append(Issue("error", "alr", "ALR 條件/確認欄非 A/V", s.index, s.raw))
        if not f[4] or any(ord(c) > 0x7E or ord(c) < 0x20 for c in f[4]):
            issues.append(Issue("error", "alr", "ALR 說明文字須為非空 ASCII", s.index, s.raw))
        a = alerts.setdefault(f[1], {"number": f[1], "text": f[4], "count": 0, "conditions": set(), "acks": set(), "first": s.sec, "last": s.sec})
        a["count"] += 1
        a["conditions"].add(f[2])
        a["acks"].add(f[3])
        a["last"] = s.sec
        if a["text"] != f[4]:
            issues.append(Issue("warning", "alr", f"ALR {f[1]} 文字改變:'{a['text']}' → '{f[4]}'", s.index, s.raw))
    alert_rows = [
        {"number": a["number"], "text": a["text"], "count": a["count"], "conditions": "/".join(sorted(a["conditions"])), "acks": "/".join(sorted(a["acks"])), "seconds": a["last"] - a["first"] + 1}
        for a in sorted(alerts.values(), key=lambda x: x["number"])
    ]

    # ---- AIS
    tracks: dict[int, AisTrack] = {}
    pending: dict[tuple[str, str], list[Sentence]] = {}
    own_pos: dict[int, tuple[float, float]] = {}
    for sec, g in gga_by_sec.items():
        if g.fields[5] != "0":
            la, lo_ = parse_lat(g.fields[1], g.fields[2]), parse_lon(g.fields[3], g.fields[4])
            if la is not None and lo_ is not None:
                own_pos[sec] = (la, lo_)

    def _decode_group(parts: list[Sentence]) -> None:
        try:
            payload = "".join(p.fields[4] for p in parts)
            fill = int(parts[-1].fields[5] or "0")
            bits = dearmor(payload)
            if fill:
                bits = bits[:-fill]
            msg = decode_ais(bits, parts[-1].sec, parts[0].index)
        except (ValueError, IndexError) as ex:
            issues.append(Issue("error", "ais", f"AIVDM 無法解碼:{ex}", parts[0].index, parts[0].raw))
            return
        if not (100_000_000 <= msg.mmsi <= 999_999_999):
            issues.append(Issue("error", "ais", f"AIS MMSI {msg.mmsi} 非 9 位數", msg.line, parts[0].raw))
        tr = tracks.setdefault(msg.mmsi, AisTrack(msg.mmsi))
        if msg.msg_type in (1, 2, 3):
            if msg.bit_length != 168:
                issues.append(Issue("warning", "ais", f"類型 {msg.msg_type} 位元數 {msg.bit_length} ≠ 168", msg.line, parts[0].raw))
            if msg.lat is None or msg.lon is None or abs(msg.lat) > 90 or abs(msg.lon) > 180:
                if msg.lat == 91.0 or msg.lon == 181.0:
                    issues.append(Issue("info", "ais", f"MMSI {msg.mmsi} 位置報告無定位(91/181)", msg.line))
                else:
                    issues.append(Issue("error", "ais", f"MMSI {msg.mmsi} 位置 {msg.lat},{msg.lon} 超出範圍", msg.line, parts[0].raw))
            if msg.cog is not None and msg.cog != 360.0 and not (0 <= msg.cog < 360):
                issues.append(Issue("error", "ais", f"MMSI {msg.mmsi} COG {msg.cog} 超出範圍", msg.line, parts[0].raw))
            if msg.heading is not None and msg.heading != 511 and not (0 <= msg.heading < 360):
                issues.append(Issue("error", "ais", f"MMSI {msg.mmsi} 航向 {msg.heading} 超出範圍", msg.line, parts[0].raw))
            if msg.sog is not None and msg.sog > 102.3:
                issues.append(Issue("error", "ais", f"MMSI {msg.mmsi} SOG {msg.sog} 超出範圍", msg.line, parts[0].raw))
            gga_t = win_time.get(msg.sec)
            if gga_t is not None and msg.utc_second is not None and msg.utc_second < 60:
                t = parse_hms(gga_t)
                if t is not None and int(round(t)) % 60 != msg.utc_second:
                    issues.append(Issue("warning", "ais", f"MMSI {msg.mmsi} 位置報告 UTC 秒 {msg.utc_second} ≠ 同批 GGA 秒 {int(round(t)) % 60}", msg.line, parts[0].raw))
            if msg.sec in own_pos and msg.lat is not None and msg.lon is not None and abs(msg.lat) <= 90 and abs(msg.lon) <= 180:
                d = distance_m(own_pos[msg.sec][0], own_pos[msg.sec][1], msg.lat, msg.lon)
                if d > 200 * 1852:
                    issues.append(Issue("error", "ais", f"MMSI {msg.mmsi} 距自船 {d / 1852:.0f} nm(> 200 nm,編碼錯誤?)", msg.line, parts[0].raw))
            if tr.positions:
                dt = msg.sec - tr.positions[-1].sec
                tr.intervals_s.append(dt)
                if lo <= tr.positions[-1].sec and msg.sec <= hi and abs(dt - ais_interval_s) > tolerance * ais_interval_s:
                    issues.append(Issue("error", "rate", f"MMSI {msg.mmsi} 位置報告間隔 {dt} s,預期 {ais_interval_s} s ±{tolerance:.0%}", msg.line, parts[0].raw))
            tr.positions.append(msg)
        elif msg.msg_type == 5:
            if msg.bit_length not in (420, 422, 424, 426):
                issues.append(Issue("warning", "ais", f"類型 5 位元數 {msg.bit_length}(預期 424)", msg.line, parts[0].raw))
            if not msg.name:
                issues.append(Issue("warning", "ais", f"MMSI {msg.mmsi} 類型 5 船名空白", msg.line, parts[0].raw))
            tr.static = msg
        else:
            issues.append(Issue("info", "ais", f"MMSI {msg.mmsi} 類型 {msg.msg_type}(未進一步解碼)", msg.line))

    for s in sentences:
        if s.formatter not in ("VDM", "VDO") or len(s.fields) < 6:
            continue
        total, num = s.fields[0], s.fields[1]
        if not (total.isdigit() and num.isdigit()) or int(total) < 1 or not (1 <= int(num) <= int(total)):
            issues.append(Issue("error", "ais", f"AIVDM 句數/序號 '{total}/{num}' 無效", s.index, s.raw))
            continue
        if s.fields[3] not in ("A", "B", "1", "2", ""):
            issues.append(Issue("warning", "ais", f"AIVDM 通道 '{s.fields[3]}'", s.index, s.raw))
        if not s.fields[5].isdigit() or int(s.fields[5]) > 5:
            issues.append(Issue("error", "ais", f"AIVDM 填充位元 '{s.fields[5]}' 無效", s.index, s.raw))
            continue
        if int(total) == 1:
            _decode_group([s])
            continue
        key = (s.fields[2], s.fields[3])
        lst = pending.setdefault(key, [])
        if int(num) != len(lst) + 1:
            issues.append(Issue("warning", "ais", f"AIVDM 多句序號不連續(seq {key[0]} 第 {num} 句)", s.index, s.raw))
            pending[key] = []
            continue
        lst.append(s)
        if len(lst) == int(total):
            _decode_group(lst)
            pending.pop(key, None)
    for key, lst in pending.items():
        issues.append(Issue("warning", "ais", f"AIVDM 多句未完整(seq {key[0]},收到 {len(lst)} 句)", lst[0].index, lst[0].raw))
    n_mmsi = len([t for t in tracks.values() if t.positions])
    for tr in tracks.values():
        if tr.positions and tr.static is None and n_windows >= 2 + n_mmsi + 1:
            # 靜態資料在目標出現後數秒先送一次、之後每 6 分:擷取窗短於一個週期時可能在擷取前已送(info);觀測滿一週期仍沒有才 warning
            sev = "warning" if n_windows >= 360 + 2 + n_mmsi else "info"
            issues.append(Issue(sev, "ais", f"MMSI {tr.mmsi} 有位置報告但 {n_windows} s 內無類型 5 靜態資料(船名){'' if sev == 'warning' else ';擷取窗 < 6 min,可能在擷取前已送'}"))
        if tr.static is not None and not tr.positions:
            issues.append(Issue("info", "ais", f"MMSI {tr.mmsi} 只有類型 5,無位置報告"))

    # ---- 與狀態比對
    compare: StateCompare | None = None
    if states:
        compare = _compare_states(states, sentences, gga_by_sec, rmc_by_sec, batch_fast, issues)

    return CheckResult(
        source=source,
        sentences_total=len(sentences),
        seconds_observed=n_windows,
        windows_used=windows,
        marker=marker,
        rates=rates,
        issues=issues,
        ais=sorted(tracks.values(), key=lambda t: t.mmsi),
        alerts=alert_rows,
        gga_quality=gga_quality,
        first_time=first_time,
        last_time=last_time,
        state_compare=compare,
        capture_seconds=capture_seconds,
    )


def _parse_state_utc(s: str) -> datetime | None:
    try:
        return datetime.strptime(s, "%Y-%m-%dT%H:%M:%S.%fZ").replace(tzinfo=timezone.utc)
    except ValueError:
        try:
            return datetime.fromisoformat(s.replace("Z", "+00:00"))
        except ValueError:
            return None


def _compare_states(
    states: list[dict[str, Any]],
    sentences: list[Sentence],
    gga_by_sec: dict[int, Sentence],
    rmc_by_sec: dict[int, Sentence],
    batch_fast: dict[int, dict[str, Sentence]],
    issues: list[Issue],
) -> StateCompare:
    """以 RMC 日期 + GGA 時刻找同一時刻(±0.021 s)的 state,比對位置/航向/水深/SOG/COG 與 sensors 旗標。"""
    cmp = StateCompare()
    by_utc: list[tuple[datetime, dict[str, Any]]] = []
    for st in states:
        u = st.get("utc")
        if isinstance(u, str):
            d = _parse_state_utc(u)
            if d is not None:
                by_utc.append((d, st))
    by_utc.sort(key=lambda x: x[0])
    if not by_utc:
        issues.append(Issue("warning", "state", "狀態資料沒有 utc 欄,無法比對"))
        return cmp
    dpt_by_sec: dict[int, Sentence] = {}
    vbw_by_sec: dict[int, Sentence] = {}
    for s in sentences:
        if s.formatter == "DPT":
            dpt_by_sec[s.sec] = s
        elif s.formatter == "VBW":
            vbw_by_sec[s.sec] = s
    import bisect

    keys = [d for d, _ in by_utc]
    for sec, gga in sorted(gga_by_sec.items()):
        rmc = rmc_by_sec.get(sec)
        if rmc is None:
            continue
        date = parse_ddmmyy(rmc.fields[8])
        t = parse_hms(gga.fields[0])
        if date is None or t is None:
            continue
        when = date + timedelta(seconds=t)
        i = bisect.bisect_left(keys, when)
        best = None
        for j in (i - 1, i):
            if 0 <= j < len(keys) and abs((keys[j] - when).total_seconds()) <= 0.021:
                best = by_utc[j][1]
                break
        if best is None:
            cmp.unmatched += 1
            continue
        cmp.compared += 1
        sensors = best.get("sensors") or {}
        pos_valid = bool(sensors.get("positionValid", True))
        if pos_valid != (gga.fields[5] != "0"):
            cmp.mismatches += 1
            issues.append(Issue("error", "state", f"{gga.fields[0]} GGA 品質 {gga.fields[5]} 與 state.sensors.positionValid={pos_valid} 不一致", gga.index, gga.raw))
        if pos_valid and gga.fields[5] != "0":
            la, lo_ = parse_lat(gga.fields[1], gga.fields[2]), parse_lon(gga.fields[3], gga.fields[4])
            pos = best.get("pos") or {}
            if la is not None and lo_ is not None and "lat" in pos and "lon" in pos:
                d = distance_m(la, lo_, float(pos["lat"]), float(pos["lon"]))
                cmp.max_position_m = max(cmp.max_position_m, d)
                if d > 3.0:
                    cmp.mismatches += 1
                    issues.append(Issue("error", "state", f"{gga.fields[0]} GGA 位置與 state.pos 相差 {d:.1f} m(> 3 m;0.0001′ 量化 ≈ 0.19 m)", gga.index, gga.raw))
            if rmc.fields[1] == "A":
                sog, cog = _num(rmc.fields[6]), _num(rmc.fields[7])
                if sog is not None and "sog" in best:
                    d = abs(sog - float(best["sog"]))
                    cmp.max_sog_kn = max(cmp.max_sog_kn, d)
                    if d > 0.051:
                        cmp.mismatches += 1
                        issues.append(Issue("error", "state", f"{gga.fields[0]} RMC SOG {sog} 與 state.sog {float(best['sog']):.2f} 相差 {d:.2f} kn", rmc.index, rmc.raw))
                if cog is not None and "cog" in best:
                    d = angle_diff(cog, float(best["cog"]))
                    cmp.max_cog_deg = max(cmp.max_cog_deg, d)
                    if d > 0.051:
                        cmp.mismatches += 1
                        issues.append(Issue("error", "state", f"{gga.fields[0]} RMC COG {cog} 與 state.cog {float(best['cog']):.2f} 相差 {d:.2f}°", rmc.index, rmc.raw))
        hdt = batch_fast.get(sec, {}).get("HDT")
        heading_valid = bool(sensors.get("headingValid", True))
        if hdt is not None and heading_valid and "heading" in best:
            h = _num(hdt.fields[0])
            if h is not None:
                d = angle_diff(h, float(best["heading"]))
                cmp.max_heading_deg = max(cmp.max_heading_deg, d)
                if d > 0.051:
                    cmp.mismatches += 1
                    issues.append(Issue("error", "state", f"{gga.fields[0]} HDT {h} 與 state.heading {float(best['heading']):.2f} 相差 {d:.2f}°", hdt.index, hdt.raw))
        elif hdt is not None and not heading_valid:
            cmp.mismatches += 1
            issues.append(Issue("error", "state", f"{gga.fields[0]} state.sensors.headingValid=false 但仍送 HDT", hdt.index, hdt.raw))
        dpt = dpt_by_sec.get(sec)
        depth_valid = bool(sensors.get("depthValid", True))
        if dpt is not None and depth_valid and "depthBelowKeel" in best:
            d_nmea = _num(dpt.fields[0])
            if d_nmea is not None:
                d = abs(d_nmea - max(0.0, float(best["depthBelowKeel"])))
                cmp.max_depth_m = max(cmp.max_depth_m, d)
                if d > 0.051:
                    cmp.mismatches += 1
                    issues.append(Issue("error", "state", f"{gga.fields[0]} DPT {d_nmea} 與 state.depthBelowKeel {float(best['depthBelowKeel']):.2f} 相差 {d:.2f} m", dpt.index, dpt.raw))
        elif dpt is not None and not depth_valid:
            cmp.mismatches += 1
            issues.append(Issue("error", "state", f"{gga.fields[0]} state.sensors.depthValid=false 但仍送 DPT", dpt.index, dpt.raw))
        vbw = vbw_by_sec.get(sec)
        if vbw is not None and len(vbw.fields) >= 3 and bool(sensors.get("stwValid", True)) != (vbw.fields[2] == "A"):
            cmp.mismatches += 1
            issues.append(Issue("error", "state", f"{gga.fields[0]} VBW 對水狀態 {vbw.fields[2]} 與 state.sensors.stwValid 不一致", vbw.index, vbw.raw))
    if cmp.compared == 0:
        issues.append(Issue("warning", "state", f"沒有任何 GGA 時刻能對上狀態 utc(狀態 {len(by_utc)} 筆;時間基準不同或未在同一次執行擷取)"))
    return cmp


# ---------------------------------------------------------------------------------------------------------------------
# 輸入
# ---------------------------------------------------------------------------------------------------------------------


def read_nmea_file(path: Path) -> list[tuple[str, float | None]]:
    """讀 .nmea(每行一句;ASCII/UTF-8;容忍 BOM)。"""
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    return [(line, None) for line in text.splitlines()]


def read_states(path: Path) -> list[dict[str, Any]]:
    """讀狀態 .jsonl:核心紀錄({"kind":"state","state":{…}};其他 kind 略過)或每行一個 state 物件(WebSocket 擷取)。"""
    out: list[dict[str, Any]] = []
    with path.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(o, dict):
                continue
            if o.get("kind") == "state" and isinstance(o.get("state"), dict):
                out.append(o["state"])
            elif "kind" in o or o.get("type") in ("event", "assessment", "ack"):
                continue
            elif "utc" in o and "pos" in o:
                out.append(o)
    return out


class _UdpCollector(asyncio.DatagramProtocol):
    def __init__(self) -> None:
        self.lines: list[tuple[str, float]] = []
        self.datagrams = 0

    def datagram_received(self, data: bytes, addr: Any) -> None:  # noqa: ANN401
        self.datagrams += 1
        t = time.monotonic()
        for line in data.decode("ascii", errors="replace").splitlines():
            if line:
                self.lines.append((line, t))


def _udp_socket(port: int, bind: str, multicast: str | None, iface: str | None) -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if hasattr(socket, "SO_REUSEPORT"):
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)  # type: ignore[attr-defined]
        except OSError:
            pass
    if multicast:
        # Windows 不能綁多播群組位址,綁 0.0.0.0(或指定介面);Linux 兩者皆可
        sock.bind(("" if sys.platform == "win32" else multicast if bind == "0.0.0.0" else bind, port))
        mreq = struct.pack("4s4s", socket.inet_aton(multicast), socket.inet_aton(iface or "0.0.0.0"))
        sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, mreq)
    else:
        sock.bind((bind, port))
    sock.setblocking(False)
    return sock


async def _capture_ws(url: str, seconds: float, out: list[dict[str, Any]]) -> None:
    from websockets.asyncio.client import connect

    deadline = time.monotonic() + seconds
    try:
        async with connect(url, max_size=8 * 1024 * 1024) as ws:
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return
                try:
                    msg = await asyncio.wait_for(ws.recv(), timeout=remaining)
                except asyncio.TimeoutError:
                    return
                if isinstance(msg, bytes):
                    msg = msg.decode("utf-8", errors="replace")
                try:
                    o = json.loads(msg)
                except json.JSONDecodeError:
                    continue
                if isinstance(o, dict) and "utc" in o and "pos" in o:
                    out.append(o)
    except Exception as ex:  # noqa: BLE001 — 連不上狀態只是少一項比對
        out.append({"_error": str(ex)})


async def capture_live(
    *, udp_port: int | None, multicast: str | None, bind: str, iface: str | None, seconds: float, ws_url: str | None
) -> tuple[list[tuple[str, float | None]], list[dict[str, Any]], int]:
    """即時擷取 UDP(單播或多播)與可選的 WebSocket 狀態 ``seconds`` 秒;回傳 (行, 狀態, 資料包數)。"""
    group, port = (None, udp_port)
    if multicast:
        g, p = multicast.rsplit(":", 1)
        group, port = g, int(p)
    assert port is not None
    loop = asyncio.get_running_loop()
    sock = _udp_socket(port, bind, group, iface)
    transport, protocol = await loop.create_datagram_endpoint(_UdpCollector, sock=sock)
    states: list[dict[str, Any]] = []
    tasks = []
    if ws_url:
        tasks.append(asyncio.create_task(_capture_ws(ws_url, seconds, states)))
    try:
        await asyncio.sleep(seconds)
    finally:
        transport.close()
    if tasks:
        await asyncio.gather(*tasks)
    return [(l, t) for l, t in protocol.lines], states, protocol.datagrams  # type: ignore[attr-defined]


# ---------------------------------------------------------------------------------------------------------------------
# 報告
# ---------------------------------------------------------------------------------------------------------------------


def _fmt_rate(v: float | None) -> str:
    return "—" if v is None else f"{v:.2f}"


def render_markdown(r: CheckResult) -> str:
    """Markdown 報告。"""
    verdict = "**通過**" if r.passed else f"**不合格**({len(r.errors)} 項 error)"
    lines = [
        "# NMEA 一致性檢驗報告",
        "",
        f"- 來源:`{r.source}`",
        f"- 產生時間:{datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}(UTC)",
        f"- 句數:{r.sentences_total};觀測秒窗:{r.seconds_observed}(標記 {r.marker};速率以 {r.windows_used} 個完整秒窗計)"
        + (f";擷取 {r.capture_seconds:.0f} s" if r.capture_seconds else ""),
        f"- NMEA 時刻:{r.first_time or '—'} → {r.last_time or '—'}",
        f"- 結果:{verdict};warning {len(r.warnings)} 項",
        "",
        "## 1. 句型與速率(IEC 61162-1 欄位數;容差 ±20%)",
        "",
        "| 句型 | talker | 句數 | 速率 Hz | 預期 Hz | 判定 | 依據/備註 |",
        "|---|---|---|---|---|---|---|",
    ]
    for row in r.rates:
        lines.append(f"| {row.key} | {row.talkers or '—'} | {row.count} | {_fmt_rate(row.rate_hz)} | {_fmt_rate(row.expected_hz)} | {row.status} | {row.note} |")
    cs = [i for i in r.issues if i.check in ("checksum", "format", "fields")]
    lines += ["", "## 2. 校驗和、格式與欄位數", "", f"- 問題 {len(cs)} 項(error {sum(1 for i in cs if i.severity == 'error')})"]
    for i in cs[:20]:
        lines.append(f"  - [{i.severity}] 第 {i.line} 行:{i.message}" + (f" — `{i.sentence}`" if i.sentence else ""))
    tm = [i for i in r.issues if i.check == "time"]
    lines += ["", "## 3. 時間戳", "", f"- GGA 時刻單調遞增且同批 RMC/TTM/ALR 相同:{'通過' if not any(i.severity == 'error' for i in tm) else '不合格'}({len(tm)} 項問題)"]
    for i in tm[:10]:
        lines.append(f"  - [{i.severity}] 第 {i.line} 行:{i.message}")
    lines += ["", "## 4. GGA 定位品質逐秒(故障注入 → 品質 0、RMC V、VTG N)", "", "| 時刻 | 品質 | 衛星 | HDOP |", "|---|---|---|---|"]
    for q in r.gga_quality[:120]:
        lines.append(f"| {q['time']} | {q['quality']} | {q['satellites']} | {q['hdop'] or '—'} |")
    if len(r.gga_quality) > 120:
        lines.append(f"| … | 共 {len(r.gga_quality)} 秒 | | |")
    lines += ["", "## 5. AIS(!AIVDM 解碼)", ""]
    if r.ais:
        lines += ["| MMSI | 船名(類型 5) | 呼號 | 位置報告數 | 間隔 s | 最後位置 | COG / SOG / HDG | 航行狀態 |", "|---|---|---|---|---|---|---|---|"]
        for t in r.ais:
            last = t.positions[-1] if t.positions else None
            pos = f"{last.lat:.4f}, {last.lon:.4f}" if last and last.lat is not None else "—"
            mot = f"{last.cog:.1f} / {last.sog:.1f} / {last.heading}" if last else "—"
            lines.append(f"| {t.mmsi} | {t.name or '—'} | {(t.static.call_sign if t.static else '') or '—'} | {len(t.positions)} | {','.join(map(str, t.intervals_s)) or '—'} | {pos} | {mot} | {last.nav_status if last else '—'} |")
    else:
        lines.append("- 無 AIVDM(情境無 AIS 目標或 AIS 關閉)")
    lines += ["", "## 6. 警報 ALR", ""]
    if r.alerts:
        lines += ["| 編號 | 文字 | 句數 | 出現秒數 | 條件 | 確認 |", "|---|---|---|---|---|---|"]
        for a in r.alerts:
            lines.append(f"| {a['number']} | {a['text']} | {a['count']} | {a['seconds']} | {a['conditions']} | {a['acks']} |")
    else:
        lines.append("- 無 ALR(無警報)")
    lines += ["", "## 7. 與核心狀態比對", ""]
    if r.state_compare is None:
        lines.append("- 未提供狀態(`--ws` 或 `--state`),略過")
    else:
        c = r.state_compare
        lines += [
            f"- 比對秒數 {c.compared}(對不上 {c.unmatched});不一致 {c.mismatches} 項",
            f"- 最大差:位置 {c.max_position_m:.2f} m、航向 {c.max_heading_deg:.3f}°、水深 {c.max_depth_m:.3f} m、SOG {c.max_sog_kn:.3f} kn、COG {c.max_cog_deg:.3f}°",
        ]
    lines += ["", "## 8. 全部問題", ""]
    if r.issues:
        lines += ["| 等級 | 檢查 | 行 | 說明 |", "|---|---|---|---|"]
        for i in r.issues[:200]:
            lines.append(f"| {i.severity} | {i.check} | {i.line or ''} | {i.message.replace('|', '/')} |")
        if len(r.issues) > 200:
            lines.append(f"| … | | | 共 {len(r.issues)} 項 |")
    else:
        lines.append("- 無")
    lines += [
        "",
        "## 9. 結論",
        "",
        f"- 自動檢驗:{verdict}。",
        "- OpenCPN 顯示(自船/AIS/深度、GNSS 失效後顯示)需實機核對:`tools/opencpn-check.md`;連線設定 `config/opencpn/README.md`。",
        "",
    ]
    return "\n".join(lines)


def result_to_json(r: CheckResult) -> dict[str, Any]:
    d: dict[str, Any] = {
        "source": r.source,
        "passed": r.passed,
        "sentences": r.sentences_total,
        "secondsObserved": r.seconds_observed,
        "windowsUsed": r.windows_used,
        "marker": r.marker,
        "firstTime": r.first_time,
        "lastTime": r.last_time,
        "rates": [asdict(x) for x in r.rates],
        "issues": [asdict(x) for x in r.issues],
        "ais": [
            {"mmsi": t.mmsi, "name": t.name, "positions": len(t.positions), "intervals_s": t.intervals_s, "static": t.static is not None}
            for t in r.ais
        ],
        "alerts": r.alerts,
        "ggaQuality": r.gga_quality,
        "stateCompare": asdict(r.state_compare) if r.state_compare else None,
    }
    return d


# ---------------------------------------------------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------------------------------------------------


def check_file(path: Path, *, states: list[dict[str, Any]] | None = None, tolerance: float = RATE_TOLERANCE, ais_interval_s: int = AIS_POSITION_INTERVAL_S) -> CheckResult:
    """檢驗一個 .nmea 檔。"""
    sentences, issues = parse_stream(read_nmea_file(path))
    return check_sentences(sentences, issues, source=str(path), tolerance=tolerance, ais_interval_s=ais_interval_s, states=states)


def _default_report() -> Path:
    return paths.repo_root() / "build" / "nmea" / "report.md"


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="python -m simosa_brm.nmea_check", description="SimCore.Host NMEA 0183 一致性檢驗(校驗和/欄位數/速率/時間/AIS/ALR/狀態比對)")
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument("--udp", type=int, metavar="PORT", help="接收 UDP 單播(Host --nmea-port 或 --nmea-target 的埠)")
    src.add_argument("--multicast", metavar="GROUP:PORT", help="加入多播群組接收(Host --nmea-multicast,預設 239.192.0.1:60001)")
    src.add_argument("--file", metavar="PATH", help="離線檢驗 Host --nmea-file 寫出的 .nmea")
    p.add_argument("--bind", default="0.0.0.0", help="UDP 綁定位址(預設 0.0.0.0)")
    p.add_argument("--iface", help="多播加入的本機介面 IP(多網卡時指定,例如教官筆電 192.168.77.21)")
    p.add_argument("--seconds", type=float, default=30.0, help="即時擷取秒數(預設 30)")
    p.add_argument("--ws", metavar="URL", help="同時連核心 WebSocket(ws://127.0.0.1:8765)比對位置/航向/水深")
    p.add_argument("--state", metavar="PATH", help="離線狀態 .jsonl(核心紀錄或 WebSocket 擷取)比對")
    p.add_argument("--report", metavar="PATH", help="Markdown 報告路徑(預設 build/nmea/report.md)")
    p.add_argument("--json", metavar="PATH", help="另輸出 JSON 結果")
    p.add_argument("--ais-interval", type=int, default=AIS_POSITION_INTERVAL_S, help="AIS 位置報告預期間隔 s(預設 10)")
    p.add_argument("--tolerance", type=float, default=RATE_TOLERANCE, help="速率容差(預設 0.2 = ±20%%)")
    p.add_argument("--quiet", action="store_true", help="不印摘要")
    return p


def main(argv: list[str] | None = None) -> int:
    a = build_parser().parse_args(argv)
    states: list[dict[str, Any]] | None = None
    capture_seconds = None
    try:
        if a.file:
            path = Path(a.file)
            if not path.is_file():
                print(f"找不到檔案 {path}", file=sys.stderr)
                return 2
            lines = read_nmea_file(path)
            source = str(path)
        else:
            if a.seconds <= 0:
                print("--seconds 須 > 0", file=sys.stderr)
                return 2
            lines, ws_states, datagrams = asyncio.run(
                capture_live(udp_port=a.udp, multicast=a.multicast, bind=a.bind, iface=a.iface, seconds=a.seconds, ws_url=a.ws)
            )
            source = f"udp://{a.bind}:{a.udp}" if a.udp else f"multicast://{a.multicast}"
            capture_seconds = a.seconds
            if a.ws:
                errs = [s["_error"] for s in ws_states if "_error" in s]
                states = [s for s in ws_states if "_error" not in s]
                if errs and not a.quiet:
                    print(f"WebSocket {a.ws} 無法連線:{errs[0]}", file=sys.stderr)
            if not lines:
                print(f"{a.seconds:g} s 內沒有收到任何 NMEA(資料包 {datagrams});確認 Host 已啟動且 --nmea-port/--nmea-multicast 與本工具一致", file=sys.stderr)
        if a.state:
            states = (states or []) + read_states(Path(a.state))
    except OSError as ex:
        print(f"輸入錯誤:{ex}", file=sys.stderr)
        return 2
    sentences, issues = parse_stream(lines)
    if not sentences:
        issues.append(Issue("error", "input", "沒有任何可解析的 NMEA 句"))
    result = check_sentences(sentences, issues, source=source, tolerance=a.tolerance, ais_interval_s=a.ais_interval, states=states, capture_seconds=capture_seconds)
    report = Path(a.report) if a.report else _default_report()
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(render_markdown(result), encoding="utf-8")
    if a.json:
        jp = Path(a.json)
        jp.parent.mkdir(parents=True, exist_ok=True)
        jp.write_text(json.dumps(result_to_json(result), ensure_ascii=False, indent=2), encoding="utf-8")
    if not a.quiet:
        print(f"NMEA 檢驗 {'通過' if result.passed else '不合格'}:{result.sentences_total} 句、{result.seconds_observed} 秒窗、error {len(result.errors)}、warning {len(result.warnings)};報告 {report}")
        for i in result.errors[:10]:
            print(f"  [error] {i.check} 第 {i.line} 行:{i.message}" if i.line else f"  [error] {i.check}:{i.message}")
    return 0 if result.passed else 1


if __name__ == "__main__":
    sys.exit(main())
