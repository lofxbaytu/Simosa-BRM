"""情境檔(data/scenarios/*.yaml;schema src/Contracts/scenario.schema.json;規劃書第 9.1、11.3 節)。

讀取、檢查與初始化的語意與 C# SimCore 相同(Scenario.Validate / SimulationEngine.Initialize / ResolveInitialPropulsion):

- 代號(``E01_baseline``)解析為 ``data/scenarios/<id>.yaml``;也接受絕對路徑或相對(工作目錄、專案根目錄)路徑。
- ``origin`` 未給時以初始經緯度為原點;初始位置用 {x, y} 時必須給 origin。經緯度 → 本地 ENU 用 ``LocalTangentPlane``。
- 初始車鐘/轉速:``initial.rpm`` 優先(車鐘未指定時以最接近者標示);否則車鐘指定時取其轉速;否則航速 > 0 取直航平衡轉速
  (``MMGShip.reset`` 的預設);否則停俥。
- 角度為度、速度為節;風向為來向、流向為去向;時間倍率夾到 [0.1, 10];``environment.visibility_nm``(schema 未定義)只記錄。
- ``environment.tide``(選填;情境引擎設計第 1.5.1 節):``table``(``[{t | utc, height_m}]``,utc 換算為相對 ``startTimeUtc`` 的秒數)或
  ``harmonic``(``{meanLevel_m, constituents: [{name?, amplitude_m, period_s, phase_deg}]}``)擇一,正規化為 ``{"table": [{t, height_m}]}`` 或
  ``{"harmonic": {...}}``;結構檢查(至少 2 點、t 嚴格遞增、period_s > 0 …)與 C# ``Scenario.StructuralIssues`` 一致。模型見 ``tide.py``:
  有效水深 = ``waterDepth``(基底)+ 潮高(t),``setEnvironment waterDepth`` 仍改基底。
- YAML 以 ``parameters.yaml_to_tree`` 讀取(純量型別推斷與 C# YamlJson 相同),``${params:<key>}`` 引用在檢查前以參數集替換
  (``parameters.resolve_references``;情境引擎設計第 2.3 節):有引用而未給參數集時依 ``assessment.parameterSet``
  (相對專案根目錄或情境檔目錄,預設 data/parameters/company-procedures.yaml)自動載入,仍無則 ``ValueError``。
  載入結果的 ``parameterSet`` 為 ``{id, version, path, sha256, resolved}``(無引用時 None),形狀同紀錄標頭 ``header.parameterSet``。
- timeline / route / zones / assessment / briefing 只做結構檢查(C# ``Scenario.StructuralIssues`` 的便宜規則:必要欄位、
  id 唯一、參照存在、動作/條件/規則名稱在允許清單、航點 ≥ 2 且不重合、多邊形 3–64 點且不自交 …),**不模擬**;解析後的子樹
  原樣保留在結果中。與 C# 相同:origin 未給時先以 initial.position 推導再做航點/多邊形幾何檢查;id/name 可為數字(讀成字串)、
  ``repeat`` 不分大小寫、``bands[].fail`` 字串 strip 後再比對、briefing 不檢查數值範圍。
  ``required[].expect`` 必填、``ukc`` 規則的 lessThan 需 metres|fractionOfDraft:C# ``Scenario.StructuralIssues`` 已同步檢查(兩邊一致)。
- ``sensors`` / ``alerts`` / ``bnwas``(感測器層、BAM、BNWAS;docs/design/sensors-alerts.md 第 8 節)同樣只結構檢查並保留:
  補 C# 預設值(``ScenarioBridge.cs``;未設的門檻 offHeading_deg / headingMonitor_deg / shallowWater_m 省略 = 關閉)、
  數值可寫 ``"${params:…}"``(``mode`` 不可)或數值字串、未知鍵忽略、``bnwas.mode`` 不分大小寫並轉小寫。不模擬感測器故障/警報/BNWAS。
"""

from __future__ import annotations

import copy
import math
import os
import re
from pathlib import Path
from typing import Any

from . import paths
from .external import normalise_sections
from .external import structural_issues as external_structural_issues
from .mmg import DEG, TELEGRAPH_ORDERS, LocalTangentPlane, MMGShip, make_ship, parse_utc
from .tide import tide_from_scenario
from .parameters import ParameterSet, find_parameter_set, has_references, resolve_references, yaml_to_tree

SCENARIO_SUFFIXES = (".yaml", ".yml")
MIN_TIME_SCALE, MAX_TIME_SCALE = 0.1, 10.0  # scenario.schema.json timeScale 範圍;C# EngineOptions.MaxTimeScale = 10
LOADINGS = ("full", "ballast", "intermediate")

# ---- 結構檢查用的允許清單(與 C# SimCore 相同;來源檔案見各註解)
TIMELINE_ACTIONS = (  # ScenarioTimeline.cs TimelineAction.AllowedKinds
    "injectFault", "clearFault", "setEnvironment", "targetControl", "voiceScript", "message", "phase", "freeze",
    "autopilot", "ownShip", "timeScale", "arm", "disarm", "endExercise", "tugControl",
)
TUG_CONTROL_MODES = ("push", "pull", "standby", "free", "unavailable")  # 時間表 tugControl.mode(C# Scenario.CheckTugControlAction)
FORBIDDEN_ACTIONS = ("loadScenario", "reset", "restore", "snapshot")  # TimelineAction.ForbiddenKinds
TRIGGER_LEAF_TYPES = (  # TriggerCondition.LeafTypes
    "time", "ownDistance", "ownHeading", "ownSpeed", "ukc", "xtd", "routeLeg", "zone", "phase", "event", "fault", "telegraph", "autopilot", "manual",
)
ZONE_TYPES = ("noGo", "speedLimit", "ukcPolicy", "reporting")  # ScenarioRoute.cs ZoneType
HARD_FAIL_RULES = ("grounding", "collision", "cpa", "ukc", "zoneEntered", "event")  # ScenarioAssessment.cs HardFailRule.Rules
EXPECT_NAMES = (  # ExpectSpec.SimpleNames
    "rudderOrder", "telegraphChange", "rpmOrder", "thrusterOrder", "steeringModeChange", "autopilotOn", "autopilotOff", "acknowledge",
)
METRIC_NAMES = (  # Assessment/MetricNames.cs
    "track.meanAbsXtd_m", "track.maxXtd_m", "track.timeOutsideCorridor_s", "track.corridorExcursions", "track.fractionOutsideCorridor",
    "track.minDistanceToCorridorEdge_m", "track.minDistanceToNoGo_m", "track.meanHeadingDeviation_deg",
    "ukc.min_m", "ukc.timeBelowLimit_s", "ukc.excursions", "ukc.groundings",
    "collisionAvoidance.minCpa_nm", "collisionAvoidance.encounters", "collisionAvoidance.collisions",
    "manoeuvring.hardOverCount", "manoeuvring.hardOverTime_s", "manoeuvring.rudderOrders", "manoeuvring.maxRot_degPerMin",
    "manoeuvring.timeRotOverLimit_s", "manoeuvring.rotExcursions", "manoeuvring.telegraphChanges", "manoeuvring.rpmOrders",
    "manoeuvring.thrusterOrders", "manoeuvring.thrusterTime_s", "manoeuvring.timeOverSpeedLimit_s", "manoeuvring.maxOverSpeed_kn",
    "manoeuvring.autopilotTime_s", "manoeuvring.autopilotSwitches",
    "general.duration_s", "general.distance_nm", "general.avgSog_kn", "general.maxSog_kn",
    # 警報與 BNWAS(docs/design/sensors-alerts.md 第 5 節;Python 不計算評估,只供結構檢查辨識名稱)
    "alerts.meanAckTime_s", "alerts.maxAckTime_s", "alerts.unacknowledged", "alerts.escalations", "bnwas.stage2Count", "bnwas.stage3Count",
    # 雷達操作(規劃書第 8.3 節第 7 類;radarControl 事件,MetricsCollector.RadarMetric)
    "radar.controlCount", "radar.acquisitions", "radar.rangeChanges",
    # 靠泊/錨泊(BerthingTracker.MetricNameList;physics-tugs-anchors-mooring.md 第 7 節)
    "berthing.maxLateralSpeed_mps", "berthing.maxLateralSpeed_kn", "berthing.maxContactSpeed_mps", "berthing.maxEnergy_kNm",
    "berthing.maxEnergyRatio", "berthing.fenderContacts", "berthing.fenderOverloads", "berthing.quayContacts",
    "berthing.lineBreaks", "berthing.maxLineTensionRatio", "berthing.tugLineBreaks", "berthing.maxTugForce_t",
    "berthing.tugWorkingTime_s", "berthing.allFastPositionError_m", "berthing.maxReContactSpeed_mps",
    "anchoring.letGoCount", "anchoring.maxLetGoSog_kn",
    "anchoring.dragEvents", "anchoring.dragTime_s", "anchoring.maxChainTension_t", "anchoring.minScopeRatio",
    "anchoring.dragResponseTime_s",
)
# 感測器/警報/BNWAS 區段(Scenario/ScenarioBridge.cs;sensors-alerts.md 第 8 節):(欄位, 預設值);預設 None = 可為 null(未設 = 關閉)
SENSORS_FIELDS = (("magneticCompassError_deg", 0.0),)
ALERTS_FIELDS = (("escalate_s", 300.0), ("silence_s", 30.0), ("offHeading_deg", None), ("offHeadingDelay_s", 10.0),
                 ("headingMonitor_deg", None), ("shallowWater_m", None))
BNWAS_FIELDS = (("dormant_s", 720.0), ("stage2_s", 15.0), ("stage3_s", 90.0))
BNWAS_MODES = ("off", "auto", "on")  # ScenarioBridge.cs BnwasMode(JsonStringEnumConverter 讀取不分大小寫)
BNWAS_MIN_DORMANT_S, BNWAS_MAX_DORMANT_S = 180.0, 720.0  # ScenarioBnwas.MinDormantS / MaxDormantS(MSC.128(75) 3–12 分鐘)
_MULTIPLIER = re.compile(r"^[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?x$")  # bands[].fail 的 "<n>x" 倍數寫法


def scenarios_dir() -> Path:
    """``data/scenarios``;套件不在專案內(且未設 SIMOSA_BRM_ROOT)時,由套件所在位置往上找含 data/scenarios 的資料夾。"""
    d = paths.scenarios_dir()
    if d.is_dir():
        return d
    for parent in Path(__file__).resolve().parents:
        cand = parent / "data" / "scenarios"
        if cand.is_dir():
            return cand
    return d


def resolve_scenario_path(ref: str | os.PathLike[str]) -> Path:
    """情境代號(``E01_baseline``)或檔案路徑 → 存在的檔案路徑;找不到時 ``FileNotFoundError``(訊息列出找過的位置)。"""
    text = str(ref).strip()
    if not text:
        raise FileNotFoundError("情境代號或路徑為空")
    p = Path(text)
    candidates: list[Path] = []
    if p.suffix.lower() in SCENARIO_SUFFIXES or p.is_absolute() or len(p.parts) > 1:
        candidates += [p, paths.repo_root() / p]
        if len(p.parts) == 1:
            candidates.append(scenarios_dir() / p.name)
    else:
        candidates += [scenarios_dir() / f"{text}{suffix}" for suffix in SCENARIO_SUFFIXES]
    for c in candidates:
        if c.is_file():
            return c.resolve()
    raise FileNotFoundError(f"找不到情境 {text!r}(找過:{'、'.join(str(c) for c in candidates)})")


def _num(obj: dict[str, Any], key: str, default: float | None = None, *, where: str) -> float | None:
    v = obj.get(key, default)
    if v is None:
        return None
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        raise ValueError(f"{where}.{key} 必須為數值,得到 {v!r}")
    return float(v)


# ---------------------------------------------------------------- 結構檢查(C# Scenario.StructuralIssues 的便宜規則)


def _is_num(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _has_position(p: Any) -> bool:
    return isinstance(p, dict) and ((_is_num(p.get("lat")) and _is_num(p.get("lon"))) or (_is_num(p.get("x")) and _is_num(p.get("y"))))


def _resolve_xy(ltp: LocalTangentPlane, p: dict[str, Any]) -> tuple[float, float]:
    if _is_num(p.get("x")) and _is_num(p.get("y")):
        return float(p["x"]), float(p["y"])
    return ltp.to_local(float(p["lat"]), float(p["lon"]))


def _lenient_str(v: Any) -> str | None:
    """字串欄位的寬鬆讀法(C# ContractJson LenientStringConverter):數字/布林讀成字串;其餘非字串為 None。"""
    if isinstance(v, str):
        return v
    if isinstance(v, bool):
        return "true" if v else "false"
    if _is_num(v):
        return str(v)
    return None


def _cross(a: tuple[float, float], b: tuple[float, float], p: tuple[float, float]) -> float:
    return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])


def _segments_intersect(p1: tuple[float, float], p2: tuple[float, float], p3: tuple[float, float], p4: tuple[float, float]) -> bool:
    d1, d2, d3, d4 = _cross(p3, p4, p1), _cross(p3, p4, p2), _cross(p1, p2, p3), _cross(p1, p2, p4)
    return ((d1 > 0 and d2 < 0) or (d1 < 0 and d2 > 0)) and ((d3 > 0 and d4 < 0) or (d3 < 0 and d4 > 0))


def polygon_self_intersects(pts: list[tuple[float, float]]) -> bool:
    """非相鄰邊是否相交(C# Geo.Polygon2D.SelfIntersects;自交多邊形在載入時為錯誤)。"""
    n = len(pts)
    if n < 4:
        return False
    for i in range(n):
        a1, a2 = pts[i], pts[(i + 1) % n]
        for j in range(i + 1, n):
            if j == i or (j + 1) % n == i or (i + 1) % n == j:
                continue  # 相鄰邊共用頂點
            if _segments_intersect(a1, a2, pts[j], pts[(j + 1) % n]):
                return True
    return False


def _action_string(payload: Any) -> str | None:
    """動作的字串值(或物件的 name 欄位;C# TimelineAction.AsString)。"""
    if isinstance(payload, str):
        return payload
    if isinstance(payload, dict) and isinstance(payload.get("name"), str):
        return payload["name"]
    if _is_num(payload):
        return str(payload)
    return None


def _check_condition(c: Any, path: str, error: Any, zone_ids: set[str], wp_names: set[str], target_ids: set[str]) -> None:
    if not isinstance(c, dict):
        error(path, "條件須為物件")
        return
    composite = any(k in c for k in ("all", "any", "not"))
    if composite:
        if c.get("type") is not None:
            error(path, "組合條件不可同時有 type")
        for key in ("all", "any"):
            if key in c:
                if not isinstance(c[key], list):
                    error(f"{path}.{key}", f"{key} 須為陣列")
                else:
                    for i, k in enumerate(c[key]):
                        _check_condition(k, f"{path}.{key}[{i}]", error, zone_ids, wp_names, target_ids)
        if "not" in c:
            _check_condition(c["not"], f"{path}.not", error, zone_ids, wp_names, target_ids)
        return
    t = c.get("type")
    if t is None or t not in TRIGGER_LEAF_TYPES:
        error(path, f"未知的條件型別:{t}")
        return
    lt, gt = c.get("lessThan"), c.get("greaterThan")
    if t == "time" and c.get("atLeast") is None:
        error(path, "time 條件需要 atLeast")
    elif t == "ownDistance":
        if c.get("point") is None and c.get("waypoint") is None and c.get("target") is None:
            error(path, "ownDistance 需要 point | waypoint | target")
        if lt is None and gt is None:
            error(path, "ownDistance 需要 lessThan / greaterThan")
        if isinstance(c.get("waypoint"), str) and c["waypoint"] not in wp_names:
            error(path, f"ownDistance 引用不存在的航點:{c['waypoint']}")
        if isinstance(c.get("target"), str) and c["target"].upper() not in target_ids:
            error(path, f"ownDistance 引用不存在的目標:{c['target']}")
    elif t in ("ownHeading", "ownSpeed") and lt is None and gt is None:
        error(path, f"{t} 需要 lessThan / greaterThan")
    elif t == "ukc" and lt is None and gt is None and c.get("lessThanFractionOfDraft") is None:
        error(path, "ukc 需要 lessThan / greaterThan / lessThanFractionOfDraft")
    elif t == "xtd" and gt is None:
        error(path, "xtd 需要 greaterThan")
    elif t == "routeLeg":
        if c.get("atLeast") is None and c.get("equals") is None and c.get("passedWaypoint") is None:
            error(path, "routeLeg 需要 atLeast | equals | passedWaypoint")
        if isinstance(c.get("passedWaypoint"), str) and c["passedWaypoint"] not in wp_names:
            error(path, f"routeLeg 引用不存在的航點:{c['passedWaypoint']}")
    elif t == "zone" and (not isinstance(c.get("id"), str) or c["id"] not in zone_ids):
        error(path, f"zone 引用不存在的區域:{c.get('id')}")
    elif t == "phase" and not str(c.get("equals") or "").strip():
        error(path, "phase 需要 equals")
    elif t == "event" and not str(c.get("kind") or "").strip():
        error(path, "event 需要 kind")
    elif t == "fault" and not str(c.get("name") or "").strip():
        error(path, "fault 需要 name")
    elif t == "telegraph" and c.get("in") is None and c.get("notIn") is None:
        error(path, "telegraph 需要 in / notIn")
    elif t == "autopilot" and c.get("enabled") is None:
        error(path, "autopilot 需要 enabled")
    since = c.get("since")
    if since is not None and (not isinstance(since, dict) or not str(since.get("event") or "").strip()):
        error(path, "since 需要 event")


def _is_numeric_text(text: str) -> bool:
    try:
        f = float(text)
    except ValueError:
        return False
    return f == f and f not in (float("inf"), float("-inf"))


def _expect_leaves(e: Any) -> list[Any]:
    if isinstance(e, dict) and isinstance(e.get("any"), list):
        return [leaf for sub in e["any"] for leaf in _expect_leaves(sub)]
    return [e]


def _lenient_number(v: Any) -> float | None:
    """數值欄位的寬鬆讀法(C# ContractJson NumberHandling.AllowReadingFromString):數字或可解析為有限數值的字串;其餘 None。"""
    if _is_num(v):
        return float(v)
    if isinstance(v, str) and _is_numeric_text(v):
        return float(v)
    return None


def _bridge_section(raw: dict[str, Any], name: str, fields: tuple[tuple[str, float | None], ...],
                    error: Any) -> dict[str, Any]:
    """sensors / alerts / bnwas 區段 → 補預設值後的 dict(形狀同 C# 序列化:null 欄位省略、未知鍵忽略)。型別錯誤經 ``error`` 回報。"""
    sec = raw.get(name)
    out: dict[str, Any] = {}
    if sec is None:
        sec = {}
    elif not isinstance(sec, dict):
        error(name, f"{name} 須為物件")
        sec = {}
    if name == "bnwas":
        mode = sec.get("mode", "off")
        if not (isinstance(mode, str) and mode.strip().lower() in BNWAS_MODES):
            error(f"{name}.mode", f"mode 須為 {' | '.join(BNWAS_MODES)}:{mode!r}")
            mode = "off"
        out["mode"] = mode.strip().lower()
    for key, default in fields:
        if key not in sec:
            v: float | None = default
        elif sec[key] is None and default is None:
            v = None
        else:
            v = _lenient_number(sec[key])
            if v is None:
                error(f"{name}.{key}", f"{key} 須為數值:{sec[key]!r}")
                v = default
        if v is not None:
            out[key] = v
    return out


def bridge_sections(raw: dict[str, Any], error: Any | None = None) -> dict[str, dict[str, Any]]:
    """情境的 sensors / alerts / bnwas(感測器層、BAM、BNWAS;docs/design/sensors-alerts.md 第 8 節)正規化並結構檢查
    (C# ``Scenario.StructuralIssues`` 的「警報/BNWAS 設定」規則):秒數與門檻必須為正、offHeadingDelay_s 不可為負、
    dormant_s 在 180–720 s。Python 參考實作不模擬感測器故障、警報與 BNWAS,只保留設定。"""
    issues: list[tuple[str, str]] = []

    def err(path: str, msg: str) -> None:
        (error or (lambda p, m: issues.append((p, m))))(path, msg)

    out = {
        "sensors": _bridge_section(raw, "sensors", SENSORS_FIELDS, err),
        "alerts": _bridge_section(raw, "alerts", ALERTS_FIELDS, err),
        "bnwas": _bridge_section(raw, "bnwas", BNWAS_FIELDS, err),
    }
    al, bn = out["alerts"], out["bnwas"]
    for key in ("escalate_s", "silence_s"):
        if al[key] <= 0:
            err(f"alerts.{key}", f"{key} 必須為正")
    if al.get("offHeading_deg") is not None and al["offHeading_deg"] <= 0:
        err("alerts.offHeading_deg", "offHeading_deg 必須為正")
    if al["offHeadingDelay_s"] < 0:
        err("alerts.offHeadingDelay_s", "offHeadingDelay_s 不可為負")
    for key in ("headingMonitor_deg", "shallowWater_m"):
        if al.get(key) is not None and al[key] <= 0:
            err(f"alerts.{key}", f"{key} 必須為正")
    if not BNWAS_MIN_DORMANT_S <= bn["dormant_s"] <= BNWAS_MAX_DORMANT_S:
        err("bnwas.dormant_s", f"dormant_s 須在 {BNWAS_MIN_DORMANT_S:g}–{BNWAS_MAX_DORMANT_S:g} s(MSC.128(75):3–12 分鐘)")
    for key in ("stage2_s", "stage3_s"):
        if bn[key] <= 0:
            err(f"bnwas.{key}", f"{key} 必須為正")
    if error is None and issues:
        raise ValueError(f"{issues[0][0]}:{issues[0][1]}")
    return out



def _item_id_refers_to_timeline(event_kind: Any) -> bool:
    """事件的 itemId 是否指時間表項目;警報與 BNWAS 事件(alert*、bnwas*)的 itemId 是警報 id(同 C# Scenario.ItemIdRefersToTimeline)。"""
    return not (isinstance(event_kind, str) and (event_kind.startswith("alert") or event_kind.startswith("bnwas")))

def structural_issues(raw: dict[str, Any], origin: dict[str, Any] | None = None) -> list[tuple[str, str]]:
    """timeline / route / zones / sensors / alerts / bnwas / assessment / briefing 的結構錯誤 ``[(路徑, 訊息)]``(不依賴初始狀態;情境引擎設計第 2.4 節 error 項)。
    參數引用須已解析(未解析的 "${params:…}" 字串在此視為一般字串)。``origin`` 未給時取 ``raw["origin"]``;航點重合與多邊形自交
    的幾何檢查需要原點(``validate_scenario`` 會先以 initial.position 推導後傳入,與 C# Scenario.Validate 相同)。"""
    issues: list[tuple[str, str]] = []
    if origin is None:
        origin = raw.get("origin")
    ltp = LocalTangentPlane(float(origin["lat"]), float(origin["lon"])) if isinstance(origin, dict) and _is_num(origin.get("lat")) and _is_num(origin.get("lon")) else None

    def error(path: str, msg: str) -> None:
        issues.append((path, msg))

    timeline = raw.get("timeline") or []
    route = raw.get("route")
    zones = raw.get("zones") or []
    assessment = raw.get("assessment") or {}
    briefing = raw.get("briefing")
    targets = raw.get("targets") or []
    for name, v, kind in (("timeline", timeline, list), ("zones", zones, list), ("assessment", assessment, dict), ("targets", targets, list)):
        if not isinstance(v, kind):
            error(name, f"{name} 須為{'陣列' if kind is list else '物件'}")
            return issues
    if route is not None and not isinstance(route, dict):
        error("route", "route 須為物件")
        return issues
    if briefing is not None and not isinstance(briefing, (str, dict)):
        error("briefing", "briefing 須為字串或物件")
        return issues

    waypoints = (route.get("waypoints") if route else None) or []
    if route and not isinstance(waypoints, list):
        error("route.waypoints", "waypoints 須為陣列")
        waypoints = []
    zone_ids = {s for z in zones if isinstance(z, dict) and (s := _lenient_str(z.get("id"))) is not None}
    wp_names = {s for w in waypoints if isinstance(w, dict) and (s := _lenient_str(w.get("name"))) is not None}
    target_ids = {s.upper() for t in targets if isinstance(t, dict) and (s := _lenient_str(t.get("id"))) is not None}
    all_item_ids = {s for t in timeline if isinstance(t, dict) and (s := _lenient_str(t.get("id"))) is not None}
    tug_ids = {s.upper() for t in (raw.get("tugs") or []) if isinstance(t, dict) and (s := _lenient_str(t.get("id"))) is not None} \
        if isinstance(raw.get("tugs"), list) else set()

    # ---- timeline
    item_ids: set[str] = set()
    for i, item in enumerate(timeline):
        path = f"timeline[{i}]"
        if not isinstance(item, dict):
            error(path, "時間表項目須為物件")
            continue
        iid = _lenient_str(item.get("id"))
        if iid is None or not iid.strip():
            error(path, "時間表項目缺少 id")
        elif iid in item_ids:
            error(path, f"時間表 id 重複:{iid}")
        else:
            item_ids.add(iid)
        if item.get("when") is not None:
            _check_condition(item["when"], f"{path}.when", error, zone_ids, wp_names, target_ids)
        rep = item.get("repeat", "once")
        if isinstance(rep, dict):
            if not (_is_num(rep.get("every_s")) and rep["every_s"] > 0):
                error(f"{path}.repeat", "every_s 必須為正")
        elif not (isinstance(rep, str) and rep.lower() in ("once", "always")):  # C# RepeatSpecConverter ToLowerInvariant
            error(f"{path}.repeat", f"repeat 須為 once | always | {{every_s, max}}:{rep!r}")
        actions = item.get("actions") or []
        if not isinstance(actions, list):
            error(f"{path}.actions", "actions 須為陣列")
            continue
        for a, act in enumerate(actions):
            ap = f"{path}.actions[{a}]"
            if not isinstance(act, dict) or len(act) != 1:
                error(ap, "動作須為只有一個鍵的物件(鍵即動作名)")
                continue
            kind, payload = next(iter(act.items()))
            if kind in FORBIDDEN_ACTIONS:
                error(ap, f"不允許的動作:{kind}")
            elif kind not in TIMELINE_ACTIONS:
                error(ap, f"未知的動作:{kind}")
            elif kind in ("arm", "disarm"):
                rid = _action_string(payload)
                if rid is None or rid not in all_item_ids:
                    error(ap, f"{kind} 引用不存在的時間表項目:{rid}")
            elif kind in ("injectFault", "clearFault"):
                if not (_action_string(payload) or "").strip():
                    error(ap, f"{kind} 缺少故障名稱")
                # recordOnly: true = 作者明示「只記錄,由儀器/腳本反應」(C# 驗證器據此不警告);須為布林值
                if kind == "injectFault" and isinstance(payload, dict) and "recordOnly" in payload and not isinstance(payload["recordOnly"], bool):
                    error(ap, "injectFault.recordOnly 須為布林值")
            elif kind == "tugControl":
                # 與指令相同的 args;id 須為情境 tugs[].id(C# Scenario.CheckTugControlAction)
                if not isinstance(payload, dict):
                    error(ap, "tugControl 須為物件 {id, mode?, direction_deg?, power_pct?}")
                else:
                    tid = _lenient_str(payload.get("id"))
                    if tid is None or not tid.strip():
                        error(ap, "tugControl 缺少 id")
                    elif tid.upper() not in tug_ids:
                        error(ap, f"tugControl 引用不存在的拖船:{tid}(須為 tugs[].id)")
                    if "mode" in payload and not (isinstance(payload["mode"], str) and payload["mode"].lower() in TUG_CONTROL_MODES):
                        error(ap, f"tugControl.mode 須為 push|pull|standby|free|unavailable:{payload['mode']}")
                    if "power_pct" in payload and not ((pw := _lenient_number(payload["power_pct"])) is not None and 0 <= pw <= 100):
                        error(ap, "tugControl.power_pct 須為 0–100")
                    if "direction_deg" in payload and _lenient_number(payload["direction_deg"]) is None:
                        error(ap, "tugControl.direction_deg 須為數值")
            elif kind == "phase":
                if not (_action_string(payload) or "").strip():
                    error(ap, "phase 缺少階段名稱")
            elif kind == "timeScale":
                if not (_is_num(payload) and payload > 0):
                    error(ap, "timeScale 必須為正數")
            elif kind == "voiceScript":
                if not isinstance(payload, dict) or not (str(payload.get("text") or "").strip() or str(payload.get("file") or "").strip()):
                    error(ap, "voiceScript 需要 text 或 file")
            elif kind == "message":
                if not isinstance(payload, dict) or not str(payload.get("text") or "").strip():
                    error(ap, "message 需要 text")

    # ---- route
    if route is not None:
        if len(waypoints) < 2:
            error("route.waypoints", "航線至少需要 2 個航點")
        names: set[str] = set()
        for i, w in enumerate(waypoints):
            wp = f"route.waypoints[{i}]"
            if not isinstance(w, dict):
                error(wp, "航點須為物件")
                continue
            n = _lenient_str(w.get("name"))
            if n is None or not n.strip():
                error(wp, "航點缺少 name")
            elif n in names:
                error(wp, f"航點名稱重複:{n}")
            else:
                names.add(n)
            if not _has_position(w):
                error(wp, "航點需為 {lat, lon} 或 {x, y}")
        start_leg = route.get("startLeg", 0)
        if not isinstance(start_leg, int) or isinstance(start_leg, bool) or start_leg < 0 or (len(waypoints) >= 2 and start_leg >= len(waypoints) - 1):
            error("route.startLeg", "startLeg 超出航段範圍")
        if ltp is not None:
            for i in range(len(waypoints) - 1):
                a, b = waypoints[i], waypoints[i + 1]
                if not (_has_position(a) and _has_position(b)):
                    continue
                (ax, ay), (bx, by) = _resolve_xy(ltp, a), _resolve_xy(ltp, b)
                if ((bx - ax) ** 2 + (by - ay) ** 2) ** 0.5 < 1.0:
                    error(f"route.waypoints[{i + 1}]", f"航點 {b.get('name')} 與前一航點重合(航段 < 1 m)")

    # ---- zones
    zids: set[str] = set()
    for i, z in enumerate(zones):
        path = f"zones[{i}]"
        if not isinstance(z, dict):
            error(path, "區域須為物件")
            continue
        zid = _lenient_str(z.get("id"))
        if zid is None or not zid.strip():
            error(path, "區域缺少 id")
        elif zid in zids:
            error(path, f"區域 id 重複:{zid}")
        else:
            zids.add(zid)
        if z.get("type") not in ZONE_TYPES:
            error(f"{path}.type", f"區域 type 須為 {' | '.join(ZONE_TYPES)}:{z.get('type')!r}")
        poly = z.get("polygon")
        if not isinstance(poly, list) or len(poly) < 3:
            error(f"{path}.polygon", "多邊形至少需要 3 點")
        elif len(poly) > 64:
            error(f"{path}.polygon", "多邊形點數上限 64")
        elif not all(_has_position(p) for p in poly):
            error(f"{path}.polygon", "頂點需為 {lat, lon} 或 {x, y}")
        elif ltp is not None and polygon_self_intersects([_resolve_xy(ltp, p) for p in poly]):
            error(f"{path}.polygon", "多邊形自交")
        wd = z.get("waterDepth_m")
        if wd is not None and (not _is_num(wd) or wd <= 0):
            error(f"{path}.waterDepth_m", "區域水深必須為正")

    # ---- 感測器/警報/BNWAS 設定(C# 在 zones 之後、assessment 之前檢查)
    bridge_sections(raw, error)

    # ---- assessment
    rule_ids: set[str] = set()

    def rule_id(path: str, r: dict[str, Any]) -> None:
        rid = str(r.get("id") or "")
        if rid in rule_ids:
            error(path, f"規則 id 重複:{rid}")
        rule_ids.add(rid)

    for name, v in (("hardFail", assessment.get("hardFail")), ("bands", assessment.get("bands")), ("required", assessment.get("required"))):
        if v is not None and not isinstance(v, list):
            error(f"assessment.{name}", f"{name} 須為陣列")
    for i, r in enumerate(assessment.get("hardFail") or []):
        path = f"assessment.hardFail[{i}]"
        if not isinstance(r, dict):
            error(path, "規則須為物件")
            continue
        rule_id(path, r)
        rule, lt = r.get("rule"), r.get("lessThan")
        if rule not in HARD_FAIL_RULES:
            error(path, f"未知的 rule:{rule}")
        if rule == "cpa" and not _is_num(lt):
            error(path, "cpa 規則需要 lessThan(nm)")
        if rule == "ukc" and not (isinstance(lt, dict) and (_is_num(lt.get("metres")) or _is_num(lt.get("fractionOfDraft")))):
            error(path, "ukc 規則需要 lessThan {metres|fractionOfDraft}")
        if rule == "zoneEntered" and (not isinstance(r.get("zoneId"), str) or r["zoneId"] not in zone_ids):
            error(path, f"zoneEntered 引用不存在的區域:{r.get('zoneId')}")
        if rule == "event" and not str(r.get("kind") or "").strip():
            error(path, "event 規則需要 kind")
    for i, b in enumerate(assessment.get("bands") or []):
        path = f"assessment.bands[{i}]"
        if not isinstance(b, dict):
            error(path, "容許帶須為物件")
            continue
        rule_id(path, b)
        if b.get("metric") not in METRIC_NAMES:
            error(path, f"未知的指標:{b.get('metric')}")
        warn, fail = b.get("warn"), b.get("fail")
        if warn is None and fail is None:
            error(path, "容許帶需要 warn 或 fail")
        if fail is not None and not _is_num(fail):
            # C# BandThresholdConverter:字串 Trim 後,"<n>x"(n 前後可有空白,如 "2 x")為倍數;否則須可解析為數值
            text = fail.strip() if isinstance(fail, str) else ""
            if text.endswith("x") and _MULTIPLIER.match(text[:-1].strip() + "x"):
                if warn is None:
                    error(path, "fail 為倍數時需要 warn")
            elif not _is_numeric_text(text):
                error(path, f"bands[].fail 須為數值或 \"<n>x\":{fail!r}")
        if warn is not None and not _is_num(warn):
            error(path, f"warn 須為數值:{warn!r}")
        if b.get("direction", "above") not in ("above", "below"):
            error(path, f"direction 須為 above | below:{b.get('direction')}")
    for i, r in enumerate(assessment.get("required") or []):
        path = f"assessment.required[{i}]"
        if not isinstance(r, dict):
            error(path, "必要事件須為物件")
            continue
        rule_id(path, r)
        after = r.get("after")
        if not isinstance(after, dict) or not str(after.get("event") or "").strip():
            error(f"{path}.after", "after 需要 event")
        else:
            if (isinstance(after.get("itemId"), str) and _item_id_refers_to_timeline(after.get("event"))
                    and after["itemId"] not in all_item_ids):
                error(f"{path}.after", f"after 引用不存在的時間表項目:{after['itemId']}")
            if isinstance(after.get("waypoint"), str) and after["waypoint"] not in wp_names:
                error(f"{path}.after", f"after 引用不存在的航點:{after['waypoint']}")
            if isinstance(after.get("zoneId"), str) and after["zoneId"] not in zone_ids:
                error(f"{path}.after", f"after 引用不存在的區域:{after['zoneId']}")
        within = r.get("within_s")
        if not (_is_num(within) and within > 0):
            error(path, "within_s 必須為正")
        expect = r.get("expect")
        if expect is None:
            error(f"{path}.expect", "必要事件需要 expect")
        for leaf in _expect_leaves(expect) if expect is not None else []:
            if isinstance(leaf, str):
                if leaf not in EXPECT_NAMES:
                    error(f"{path}.expect", f"未知的 expect:{leaf}")
            elif isinstance(leaf, dict):
                if isinstance(leaf.get("name"), str) and leaf["name"] not in EXPECT_NAMES:
                    error(f"{path}.expect", f"未知的 expect:{leaf['name']}")
                ev = leaf.get("event")
                if (isinstance(ev, dict) and isinstance(ev.get("itemId"), str)
                        and _item_id_refers_to_timeline(ev.get("kind")) and ev["itemId"] not in all_item_ids):
                    error(f"{path}.expect", f"expect 引用不存在的時間表項目:{ev['itemId']}")
            else:
                error(f"{path}.expect", "expect 須為字串或物件")

    # ---- briefing
    if isinstance(briefing, dict):
        roles = briefing.get("roles")
        if roles is not None:
            if not isinstance(roles, list):
                error("briefing.roles", "roles 須為陣列")
            else:
                for i, role in enumerate(roles):
                    if not isinstance(role, dict) or not str(role.get("role") or "").strip():
                        error(f"briefing.roles[{i}]", "角色需要 role")
        # minParticipants / duration_s 的數值範圍 C# 不檢查(只讀取),此處亦不檢查以免 C# 可載入的情境在此被拒

    # ---- 潮汐(environment.tide;C# 在 assessment 之後檢查)
    env = raw.get("environment")
    if isinstance(env, dict) and env.get("tide") is not None:
        tide_issues(env.get("tide"), raw.get("startTimeUtc"), error)
    return issues


def tide_issues(tide: Any, start_time_utc: Any, error: Any) -> None:
    """``environment.tide`` 的結構檢查(C# Scenario.StructuralIssues 的潮汐段;路徑與訊息相同):table 與 harmonic 擇一;
    table 至少 2 點、每點 t 或 utc 擇一、utc 需情境 startTimeUtc 可解析、t 嚴格遞增;harmonic 至少 1 個分潮、period_s > 0、amplitude_m ≥ 0。"""
    tp = "environment.tide"
    if not isinstance(tide, dict):
        error(tp, "tide 需為 table 或 harmonic 擇一")
        return
    table, harmonic = tide.get("table"), tide.get("harmonic")
    if (table is None) == (harmonic is None):
        error(tp, "tide 需為 table 或 harmonic 擇一")
    if table is not None:
        if not isinstance(table, list):
            error(f"{tp}.table", "潮高表至少需要 2 點")
            table = []
        elif len(table) < 2:
            error(f"{tp}.table", "潮高表至少需要 2 點")
        start = parse_utc(start_time_utc if isinstance(start_time_utc, str) else None)
        prev: float | None = None
        for i, p in enumerate(table):
            pp = f"{tp}.table[{i}]"
            if not isinstance(p, dict):
                error(pp, "表點需要 t 或 utc")
                continue
            t: float | None = None
            has_t, has_utc = p.get("t") is not None, p.get("utc") is not None
            if has_t and has_utc:
                error(pp, "表點的 t 與 utc 只能擇一")
            elif has_t:
                if not _is_num(p.get("t")):
                    error(pp, "t 必須為有限數值")
                else:
                    t = float(p["t"])
            elif has_utc:
                utc = parse_utc(str(p["utc"]))
                if utc is None:
                    error(pp, f"utc 無法解析:{p['utc']}")
                elif start is None:
                    error(pp, "表點以 utc 指定時情境必須有可解析的 startTimeUtc")
                else:
                    t = (utc - start).total_seconds()
            else:
                error(pp, "表點需要 t 或 utc")
            if not _is_num(p.get("height_m")):
                error(pp, "height_m 必須為有限數值")
            if t is not None:
                if not math.isfinite(t):
                    error(pp, "t 必須為有限數值")
                elif prev is not None and t <= prev:
                    error(pp, "潮高表的 t 必須嚴格遞增")
                prev = t
    if harmonic is not None:
        hp = f"{tp}.harmonic"
        if not isinstance(harmonic, dict):
            error(f"{hp}.constituents", "調和潮至少需要 1 個分潮")
            return
        mean = harmonic.get("meanLevel_m")
        if mean is not None and not _is_num(mean):
            error(f"{hp}.meanLevel_m", "meanLevel_m 必須為有限數值")
        cons = harmonic.get("constituents")
        if not isinstance(cons, list) or len(cons) == 0:
            error(f"{hp}.constituents", "調和潮至少需要 1 個分潮")
            cons = []
        for i, c in enumerate(cons):
            cp = f"{hp}.constituents[{i}]"
            if not isinstance(c, dict):
                error(cp, "period_s 必須為正")
                continue
            period, amp, phase = c.get("period_s"), c.get("amplitude_m"), c.get("phase_deg")
            if not _is_num(period) or float(period) <= 0.0:
                error(cp, "period_s 必須為正")
            if not _is_num(amp) or float(amp) < 0.0:
                error(cp, "amplitude_m 不可為負")
            if phase is not None and not _is_num(phase):
                error(cp, "phase_deg 必須為有限數值")


def normalise_tide(tide: Any, start_time_utc: Any) -> dict[str, Any] | None:
    """``environment.tide`` → 正規化形狀(結構檢查已通過):``{"table": [{"t", "height_m"}]}``(utc 換算為相對 startTimeUtc 的秒數)或
    ``{"harmonic": {"meanLevel_m", "constituents": [{"name", "amplitude_m", "period_s", "phase_deg"}]}}``;None → None。"""
    if not isinstance(tide, dict):
        return None
    if tide.get("table") is not None:
        start = parse_utc(start_time_utc if isinstance(start_time_utc, str) else None)
        points = []
        for p in tide["table"]:
            if p.get("t") is not None:
                t = float(p["t"])
            else:
                utc = parse_utc(str(p["utc"]))
                if utc is None or start is None:
                    raise ValueError("environment.tide.table 的 utc 需有可解析的情境 startTimeUtc")
                t = (utc - start).total_seconds()
            points.append({"t": t, "height_m": float(p["height_m"])})
        return {"table": points}
    h = tide.get("harmonic") or {}
    return {"harmonic": {
        "meanLevel_m": float(h.get("meanLevel_m") or 0.0),
        "constituents": [{"name": (str(c["name"]) if c.get("name") is not None else None), "amplitude_m": float(c["amplitude_m"]),
                          "period_s": float(c["period_s"]), "phase_deg": float(c.get("phase_deg") or 0.0)}
                         for c in h.get("constituents") or []],
    }}


def validate_scenario(raw: dict[str, Any], default_id: str = "", parameter_set: dict[str, Any] | None = None,
                      base_directory: str | os.PathLike[str] | None = None) -> dict[str, Any]:
    """檢查必要欄位並補齊預設值(C# Scenario.Validate 相同);回傳正規化後的新 dict(角度為度、速度為節)。
    ``raw`` 的參數引用須已解析(``load_scenario`` 會先做);timeline / route / zones / assessment / briefing 經
    ``structural_issues`` 檢查(第一個錯誤即 ``ValueError``)後原樣(深拷貝)保留,sensors / alerts / bnwas 補預設值後保留;``parameter_set`` 為參數集摘要(無引用時 None)。"""
    if not isinstance(raw, dict):
        raise ValueError("情境內容必須為物件(YAML mapping)")
    if has_references(raw):
        raise ValueError("情境含未解析的 ${params:…} 引用;請先以 parameters.resolve_references 解析(load_scenario 會自動處理)")
    sid = str(raw.get("id") or default_id).strip()
    if not sid:
        raise ValueError("情境缺少 id")
    ship = raw.get("ship") or {}
    if not isinstance(ship, dict):
        raise ValueError("ship 必須為物件")
    ship_id = str(ship.get("id") or "").strip().upper()
    if not ship_id:
        raise ValueError("情境缺少 ship.id")
    loading = str(ship.get("loading") or "ballast")
    if loading not in LOADINGS:
        raise ValueError(f"ship.loading 必須為 {LOADINGS} 之一,得到 {loading!r}")

    init = raw.get("initial") or {}
    if not isinstance(init, dict):
        raise ValueError("initial 必須為物件")
    pos = init.get("position")
    if not isinstance(pos, dict):
        raise ValueError("情境缺少 initial.position")
    lat, lon = _num(pos, "lat", where="initial.position"), _num(pos, "lon", where="initial.position")
    x, y = _num(pos, "x", where="initial.position"), _num(pos, "y", where="initial.position")
    has_geo = lat is not None and lon is not None
    has_local = x is not None and y is not None
    if not has_geo and not has_local:
        raise ValueError("initial.position 需為 {lat, lon} 或 {x, y}")
    origin = raw.get("origin")
    if origin is None:
        if not has_geo:
            raise ValueError("initial.position 以 {x, y} 指定時必須提供 origin {lat, lon}")
        origin = {"lat": lat, "lon": lon}
    else:
        if not isinstance(origin, dict):
            raise ValueError("origin 必須為物件")
        o_lat, o_lon = _num(origin, "lat", where="origin"), _num(origin, "lon", where="origin")
        if o_lat is None or o_lon is None:
            raise ValueError("origin 需有 lat 與 lon")
        origin = {"lat": o_lat, "lon": o_lon}
    # 結構檢查在 origin 推導之後(C# Scenario.Validate 相同):航點重合、多邊形自交等幾何檢查才一定會做
    issues = structural_issues(raw, origin=origin)
    if issues:
        raise ValueError(f"{issues[0][0]}:{issues[0][1]}")
    # 外力區段(拖船/錨/泊位/岸壁/底質):補 C# 預設值、berth.file 與 banks.file 解析內嵌,再做 C# 相同的結構檢查
    ext_sections = normalise_sections(raw, base_directory)
    ext_issues = external_structural_issues(ext_sections)
    if ext_issues:
        raise ValueError(f"{ext_issues[0][0]}:{ext_issues[0][1]}")
    heading = _num(init, "heading", where="initial")
    if heading is None:
        raise ValueError("情境缺少 initial.heading")
    speed = _num(init, "speed", 0.0, where="initial") or 0.0
    telegraph = init.get("telegraph")
    if telegraph is not None:
        telegraph = str(telegraph).strip().upper()
        if telegraph not in TELEGRAPH_ORDERS:
            raise ValueError(f"initial.telegraph 未知車令 {telegraph!r}")
    rpm = _num(init, "rpm", where="initial")
    rudder = _num(init, "rudder", 0.0, where="initial") or 0.0

    env = raw.get("environment") or {}
    if not isinstance(env, dict):
        raise ValueError("environment 必須為物件")
    wind = env.get("wind") or {}
    cur = env.get("current") or {}
    depth = _num(env, "waterDepth", where="environment")
    if depth is None or depth <= 0.0:
        raise ValueError("environment.waterDepth 必須為正")
    vis = _num(env, "visibility_nm", where="environment")
    if vis is None:
        vis = _num(env, "visibility", where="environment")
    tide = normalise_tide(env.get("tide"), raw.get("startTimeUtc"))  # 結構檢查已於 structural_issues 通過

    seed_raw = raw.get("seed", 1)
    if isinstance(seed_raw, bool) or not isinstance(seed_raw, int):
        raise ValueError(f"seed 必須為整數,得到 {seed_raw!r}")
    ts = _num(raw, "timeScale", 1.0, where="scenario") or 1.0
    ts = 1.0 if ts <= 0.0 else min(MAX_TIME_SCALE, max(MIN_TIME_SCALE, ts))

    return {
        "id": sid,
        "name": str(raw["name"]) if raw.get("name") is not None else None,
        "description": str(raw["description"]) if raw.get("description") is not None else None,
        "seed": int(seed_raw),
        "startTimeUtc": str(raw["startTimeUtc"]) if raw.get("startTimeUtc") is not None else None,
        "timeScale": ts,
        "ship": {"id": ship_id, "loading": loading},
        "origin": origin,
        "initial": {"lat": lat, "lon": lon, "x": x, "y": y, "heading": heading % 360.0, "speed": speed,
                    "telegraph": telegraph, "rpm": rpm, "rudder": rudder},
        "environment": {
            "wind": {"trueSpeed": max(0.0, _num(wind, "trueSpeed", 0.0, where="environment.wind") or 0.0),
                     "trueDir": (_num(wind, "trueDir", 0.0, where="environment.wind") or 0.0) % 360.0,
                     "gustiness": min(1.0, max(0.0, _num(wind, "gustiness", 0.0, where="environment.wind") or 0.0))},
            "current": {"set": (_num(cur, "set", 0.0, where="environment.current") or 0.0) % 360.0,
                        "drift": max(0.0, _num(cur, "drift", 0.0, where="environment.current") or 0.0)},
            "waterDepth": depth,
            "visibility_nm": vis,
            "tide": tide,
        },
        # 情境引擎區段(只結構檢查、不模擬;數值已代入參數)
        "timeline": copy.deepcopy(raw.get("timeline") or []),
        "route": copy.deepcopy(raw.get("route")),
        "zones": copy.deepcopy(raw.get("zones") or []),
        "assessment": copy.deepcopy(raw.get("assessment") or {}),
        "briefing": copy.deepcopy(raw.get("briefing")),
        # 感測器/警報/BNWAS 設定(補 C# 預設值;Python 不模擬,只保留供 state.scenario/講評與跨語言比對)
        **bridge_sections(raw),
        "parameterSet": copy.deepcopy(parameter_set),
        # 外力區段(external.normalise_sections;未定義者為 None):apply_scenario 交給 MMGShip.configure_external
        "external": ext_sections,
    }


def load_scenario(ref: str | os.PathLike[str], parameters: ParameterSet | None = None) -> dict[str, Any]:
    """讀取並檢查情境檔;回傳正規化 dict(含 ``path``、``parameterSet``)。id 未填時取檔名。
    ``parameters`` 未給而情境含 ``${params:…}`` 時依 ``assessment.parameterSet`` 自動載入(C# ScenarioLoader.Load 相同)。"""
    path = resolve_scenario_path(ref)
    raw = yaml_to_tree(path.read_text(encoding="utf-8"))
    if raw is None:
        raise ValueError(f"{path}: 情境 YAML 為空")
    if not isinstance(raw, dict):
        raise ValueError(f"{path}: 情境 YAML 須為物件")
    sc = scenario_from_tree(raw, default_id=path.stem, parameters=parameters, base_directory=path.parent)
    sc["path"] = str(path)
    return sc


def load_scenario_inline(raw: dict[str, Any], default_id: str = "", parameters: ParameterSet | None = None) -> dict[str, Any]:
    """內嵌情境物件(loadScenario 指令的 ``args.scenario``,結構與 YAML 相同;規劃書第 9.1 節靠離泊預演自動組題):
    與 ``load_scenario`` 同一條「節點樹 → 參數替換 → 正規化」路徑(C# ScenarioLoader.FromJsonElement),base_directory 為
    ``data/scenarios``(泊位檔 ``data/areas/…`` 相對專案根目錄);id 空白時以 ``default_id`` 代入,皆無時 ``inline``。
    輸入不被修改(先深拷貝);回傳 dict 無 ``path``。"""
    if not isinstance(raw, dict):
        raise ValueError("內嵌情境須為物件")
    tree = copy.deepcopy(raw)
    return scenario_from_tree(tree, default_id=(default_id or "").strip() or "inline", parameters=parameters, base_directory=scenarios_dir())


def scenario_from_tree(raw: dict[str, Any], default_id: str = "", parameters: ParameterSet | None = None,
                       base_directory: str | os.PathLike[str] | None = None) -> dict[str, Any]:
    """節點樹 → 參數替換(就地)→ 正規化情境;``base_directory`` 供自動尋找 assessment.parameterSet(C# ScenarioLoader.FromYaml)。"""
    pset = parameters
    if pset is None and has_references(raw):
        assessment = raw.get("assessment")
        declared = assessment.get("parameterSet") if isinstance(assessment, dict) else None
        found = find_parameter_set(declared if isinstance(declared, str) else None, base_directory)
        if found is not None:
            pset = ParameterSet.load(found)
    resolved = resolve_references(raw, pset)
    info = pset.info(resolved) if pset is not None and resolved else None
    return validate_scenario(raw, default_id=default_id, parameter_set=info, base_directory=base_directory)


def apply_scenario(ship: MMGShip, sc: dict[str, Any]) -> None:
    """把情境的初始狀態、環境與種子套到既有模型(船型/裝載須相符);tick/時間歸零、故障與自動舵清除。"""
    want = (sc["ship"]["id"], sc["ship"]["loading"])
    have = (ship.sp.ship_id.upper(), ship.sp.loading.name)
    if want != have:
        raise ValueError(f"情境需要船舶 {want[0]}/{want[1]},目前模型為 {have[0]}/{have[1]}")
    ship.seed = int(sc["seed"])
    ship.set_origin(sc["origin"]["lat"], sc["origin"]["lon"])
    init = sc["initial"]
    if init["x"] is not None and init["y"] is not None:
        x, y = init["x"], init["y"]
    else:
        x, y = ship.ltp.to_local(init["lat"], init["lon"])
    heading, speed = init["heading"], init["speed"]
    telegraph, rpm0 = init["telegraph"], init["rpm"]
    # 直航平衡轉速以情境水深(淺水修正)、無風計算(C# ResolveInitialPropulsion:SteadyRpmForSpeed(u, scenario waterDepth));
    # 先前在 reset 之後才設水深,淺水情境的初始轉速取到深水值(外力一致性測試 ext_bank_suction 發現,h/T 1.4 時差約 9 rpm)
    ship.set_environment(wind_speed_kn=0.0, water_depth_m=sc["environment"]["waterDepth"])
    # 潮汐模型(有效水深 = 基底 + 潮高(tick·dt);reset 後以 tick 0 的潮高重算淺水修正,與 C# 初始平衡轉速相同)
    ship.set_tide(tide_from_scenario(sc["environment"].get("tide")))
    if rpm0 is not None:
        ship.reset(x, y, heading, speed, rpm=rpm0)
        if telegraph is not None:
            ship.ctl.telegraph = telegraph  # C#:車鐘以指定值標示、轉速令 = initial.rpm
    elif telegraph is not None:
        table = ship.eng["telegraph"]
        if telegraph not in table:
            raise ValueError(f"係數檔的車鐘表無 {telegraph}(可用:{list(table)})")
        ship.reset(x, y, heading, speed, rpm=float(table[telegraph]), telegraph=telegraph)
    else:
        ship.reset(x, y, heading, speed)
    delta = max(-ship.delta_max, min(ship.delta_max, init["rudder"] * DEG))
    ship.state.delta = delta
    ship.ctl.rudder_order = delta
    env = sc["environment"]
    ship.set_environment(env["wind"]["trueSpeed"], env["wind"]["trueDir"], env["current"]["set"], env["current"]["drift"],
                         env["waterDepth"], gustiness=env["wind"]["gustiness"], visibility_nm=env["visibility_nm"])
    # 外力模組:以情境初始狀態與水深建立(拖船 initial、初始已錨泊、繫纜 initial fast、岸壁;C# Initialize)
    ship.configure_external(sc.get("external"))


def ship_from_scenario(sc: dict[str, Any], dt: float = 0.02, coeffs: dict[str, Any] | None = None,
                       prefer_file: bool = True) -> MMGShip:
    """依情境的 ship.id / ship.loading 建立模型(``make_ship`` 同一條路徑)並套用初始狀態。"""
    ship = make_ship(sc["ship"]["id"], sc["ship"]["loading"], dt=dt, coeffs=coeffs, prefer_file=prefer_file, seed=int(sc["seed"]))
    apply_scenario(ship, sc)
    return ship
