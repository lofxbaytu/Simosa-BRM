"""匯出 Python 參考實作的外力案例(拖船、錨與懸鏈線、碰墊與繫纜、岸壁效應)時間序列,供 C# 數值一致性測試
(規劃書第 14 章:C# 核心 vs Python 參考實作(相同係數)軌跡差異 <1%;設計文件 physics-tugs-anchors-mooring.md 與
physics-bank-effect.md 的「Python 參考實作」章節)。

執行(自專案根目錄):
    cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_external_reference.py

輸出到本資料夾(tests/SimCore.Tests/Golden/),由 ExternalForceGoldenTests 讀取:
    ext_<案例>.yaml   情境(兩邊以同一份 YAML 建立初始狀態;本腳本是唯一來源)
    ext_<案例>.csv    以 ``#`` 開頭的標頭列(產生指令、情境檔、末狀態雜湊、``# command: tick=<n> <JSON>`` 指令序列),之後為 CSV:
                      tick,t,x,y,psi_deg,u,v,r,rpm,ext_X,ext_Y,ext_N,<項目欄>
    每 1 s(50 tick)一列,含 tick 0。ext_X/Y/N 為該時刻狀態下的船體座標外力合計(kN、kN、kN·m;拖船 + 錨 + 泊位 + 岸壁),
    項目欄依案例:tug:<id>:force_t|direction_deg、anchor:<side>:length_m|H_t|anchorX|anchorY、fender:<id>:reaction_kN、
    line:<id>:tension_t、bank:Y_kN|N_kNm|parameter。

案例(FSB1 滿載已識別係數、dt 0.02 s、陣風 0):
    ext_tug_push_turn       兩艘拖船艏右/艉左推頂原地迴轉;TG1 推 → 拉換位(modeChange)、TG2 轉向後解纜、tugLineBreak 斷 TG1 拖纜(600 s)
    ext_anchor_wind_drag    右錨 4 節已錨泊、35 kn 風、軟泥抓著倍率 0.5:受風擺盪、走錨、鬆鏈到 7 節;左錨 letGo(600 s)
    ext_berth_fender_lines  拖船橫推靠碼頭(非線性碰墊、阻尼、摩擦)、艏艉纜 makeFast / heave、拖船解纜後吹開風(600 s)
    ext_bank_suction        6 kn 沿右舷垂直岸壁、左舷淹沒斜坡岸,h/T 1.4(300 s)
    ext_quay_line_part      無碰墊碼頭硬接觸(quayContact)、MBL 6 t 艏纜被拖船拉斷(mooringLineParted)、艉纜 slack 到 maxLength
                            後回 fast、拖船由 free 帶纜(connect_s)拉 → standby → push(500 s)
    ext_anchor_heave_house  右錨 letGo 12 節(夾到鏈端 anchorChainFullyOut)、左錨 3 節後 heave 經懸鏈線 suspended/taut 到
                            anchorAweigh / anchorHoused(含 anchorWindlassStalled、anchorBrakeSlipping;400 s)
    ext_bank_astern         沿折線斜坡岸與垂直岸壁前進後全速倒俥(u < 0 時前導/後隨對調),中途水深變更(300 s)

每個 ``items`` 欄位在案例中至少有一個非零值(全程為 0 的欄位不提供比對保護,不列入);
``tests/test_external_golden_coverage.py`` 檢查此規則、標頭事件與末狀態雜湊,以及黃金檔合起來涵蓋的外力事件種類。
"""

from __future__ import annotations

import json
import math
from pathlib import Path

from simosa_brm import __version__
from simosa_brm.external import TONNE_FORCE, Env, Kin
from simosa_brm.scenario import load_scenario, ship_from_scenario
from simosa_brm.server import SimServer

OUT_DIR = Path(__file__).resolve().parent
SAMPLE_EVERY = 50
COMMAND = "cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_external_reference.py"

COMMON = """\
seed: 1
ship: { id: FSB1, loading: full }
origin: { lat: 23.80, lon: 120.15 }
"""

CASES: list[dict] = [
    {
        "name": "ext_tug_push_turn",
        "title": "拖船推頂原地迴轉、推拉換位、解纜與斷纜(600 s)",
        "duration_s": 600.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 0 }, heading: 0, speed: 2, telegraph: DSAH }
environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 15 }
tugs:
  - id: TG1
    bollardPull_t: 40
    attach: fwdStbd
    lineLength_m: 30
    riseTime_s: 25
    modeChange_s: 60
    initial: { mode: standby, direction_deg: 270, power_pct: 0 }
  - id: TG2
    bollardPull_t: 35
    attach: aftPort
    lineLength_m: 30
    turnRate_degPerSec: 3
    initial: { mode: push, direction_deg: 90, power_pct: 50 }
""",
        "commands": [
            (0, {"type": "tugControl", "args": {"id": "TG1", "mode": "push", "direction_deg": 270, "power_pct": 80}}),
            (2500, {"type": "telegraph", "value": "STOP"}),
            (10000, {"type": "tugControl", "args": {"id": "TG1", "mode": "pull", "direction_deg": 60, "power_pct": 60}}),
            (15000, {"type": "tugControl", "args": {"id": "TG2", "direction_deg": 30, "power_pct": 100}}),
            (20000, {"type": "tugControl", "args": {"id": "TG2", "mode": "free"}}),
            (22500, {"type": "injectFault", "value": "tugLineBreak", "args": {"args": {"id": "TG1"}}}),
        ],
        "items": ["tug:TG1:force_t", "tug:TG1:direction_deg", "tug:TG2:force_t", "tug:TG2:direction_deg"],
    },
    {
        "name": "ext_anchor_wind_drag",
        "title": "右錨已錨泊受 35 kn 風擺盪與走錨、鬆鏈、左錨 letGo(600 s)",
        "duration_s": 600.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 0 }, heading: 0, speed: 0, telegraph: STOP }
environment: { wind: { trueSpeed: 35, trueDir: 60, gustiness: 0 }, current: { set: 200, drift: 0.8 }, waterDepth: 25, seabed: mud }
anchors:
  starboard: { length_shackles: 4, bearing_deg: 10 }
  holdingFactor: 0.5
  dragSpeed_mps: 0.4
""",
        "commands": [
            (15000, {"type": "anchor", "args": {"side": "starboard", "action": "veer", "targetLength_shackles": 7}}),
            (20000, {"type": "anchor", "args": {"side": "port", "action": "letGo", "targetLength_shackles": 3}}),
        ],
        "items": ["anchor:starboard:length_m", "anchor:starboard:H_t", "anchor:starboard:anchorX", "anchor:starboard:anchorY",
                  "anchor:port:length_m", "anchor:port:H_t"],
    },
    {
        "name": "ext_berth_fender_lines",
        "title": "拖船橫推靠碼頭、碰墊接觸、艏艉纜繫妥與絞纜、吹開風(600 s)",
        "duration_s": 600.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 12 }, heading: 90, speed: 0, telegraph: STOP }
environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 15 }
tugs:
  - id: TG1
    bollardPull_t: 30
    attach: midStbd
    riseTime_s: 20
    initial: { mode: push, direction_deg: 270, power_pct: 25 }
berth:
  definition:
    id: GB
    quay: [ { x: -200, y: 30 }, { x: 200, y: 30 } ]
    waterSide: right
    fenderTypes:
      cone: { height_m: 1.0, width_m: 2.5, ratedReaction_kN: 800, ratedDeflection_pct: 72, curve: [[0, 0], [10, 35], [30, 85], [50, 100], [72, 100]], friction: 0.15, damping_kNs_per_m: 50 }
    fenders: [ { id: F1, type: cone, chainage_m: 140 }, { id: F2, type: cone, chainage_m: 170 }, { id: F3, type: cone, chainage_m: 230 }, { id: F4, type: cone, chainage_m: 260 } ]
    bollards: [ { id: B1, chainage_m: 110, setback_m: 3 }, { id: B2, chainage_m: 290, setback_m: 3 } ]
  lines:
    - { id: HEAD, fairlead: fwdPort, bollard: B2, mbl_t: 40, length_m: 120, winchPull_t: 8, heaveRate_mPerMin: 12 }
    - { id: STERN, fairlead: aftPort, bollard: B1, mbl_t: 40, length_m: 120, winchPull_t: 8, heaveRate_mPerMin: 12 }
""",
        "commands": [
            (7500, {"type": "mooring", "args": {"lineId": "HEAD", "action": "makeFast", "tension_t": 1}}),
            (7500, {"type": "mooring", "args": {"lineId": "STERN", "action": "makeFast"}}),
            (9000, {"type": "mooring", "args": {"lineId": "HEAD", "action": "heave", "tension_t": 5}}),
            (9000, {"type": "mooring", "args": {"lineId": "STERN", "action": "heave", "tension_t": 5}}),
            (15000, {"type": "tugControl", "args": {"id": "TG1", "mode": "free"}}),
            (15000, {"type": "setEnvironment", "args": {"wind": {"trueSpeed": 30, "trueDir": 10}}}),
            (25000, {"type": "mooring", "args": {"lineId": "STERN", "action": "slack", "length_m": 3}}),
        ],
        # F1、F4 全程未接觸(反力恆為 0),不列入比對欄
        "items": ["fender:F2:reaction_kN", "fender:F3:reaction_kN", "line:HEAD:tension_t", "line:STERN:tension_t", "tug:TG1:force_t"],
    },
    {
        "name": "ext_bank_suction",
        "title": "6 kn 沿右舷垂直岸壁與左舷淹沒斜坡岸航行,h/T 1.4(300 s)",
        "duration_s": 300.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 0 }, heading: 0, speed: 6 }
environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 8 }
banks:
  lines:
    - { id: EAST, type: vertical, line: [ { x: 42, y: -500 }, { x: 42, y: 3000 } ], waterSide: left }
    - { id: WEST, type: sloped, slope: 3, topDepth_m: 2, line: [ { x: -75, y: -500 }, { x: -75, y: 3000 } ], waterSide: right }
""",
        "commands": [
            (5000, {"type": "rudder", "value": 5}),
        ],
        "items": ["bank:Y_kN", "bank:N_kNm", "bank:parameter"],
    },
    {
        "name": "ext_quay_line_part",
        "title": "無碰墊碼頭硬接觸、艏纜超過 MBL 斷裂、艉纜鬆到最大長度、拖船由 free 帶纜(500 s)",
        "duration_s": 500.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 14 }, heading: 90, speed: 0, telegraph: STOP }
environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 15 }
tugs:
  - { id: TG1, bollardPull_t: 50, attach: midStbd, riseTime_s: 10, initial: { mode: push, direction_deg: 270, power_pct: 100 } }
  - { id: TG2, bollardPull_t: 40, attach: aftStbd, connect_s: 30, initial: { mode: free } }
berth:
  definition:
    id: QB
    quay: [ { x: -200, y: 30 }, { x: 0, y: 31 }, { x: 200, y: 30 } ]
    waterSide: right
    quayStiffness_kNpm: 5000
    bollards: [ { id: B1, chainage_m: 100, setback_m: 3 }, { id: B2, chainage_m: 300, setback_m: 3 } ]
  lines:
    - { id: HEAD, fairlead: fwdPort, bollard: B2, mbl_t: 6, length_m: 150, winchPull_t: 5, heaveRate_mPerMin: 20 }
    - { id: STERN, fairlead: aftPort, bollard: B1, mbl_t: 80, length_m: 100 }
""",
        "commands": [
            (3000, {"type": "mooring", "args": {"lineId": "HEAD", "action": "makeFast"}}),
            (3000, {"type": "mooring", "args": {"lineId": "STERN", "action": "makeFast"}}),
            (3500, {"type": "tugControl", "args": {"id": "TG1", "mode": "free"}}),
            (3500, {"type": "tugControl", "args": {"id": "TG2", "mode": "pull", "direction_deg": 120, "power_pct": 100}}),
            (9000, {"type": "mooring", "args": {"lineId": "STERN", "action": "slack", "length_m": 200}}),
            (12000, {"type": "tugControl", "args": {"id": "TG2", "mode": "standby"}}),
            (13000, {"type": "tugControl", "args": {"id": "TG2", "mode": "push", "direction_deg": 270, "power_pct": 50}}),
        ],
        "items": ["line:HEAD:tension_t", "line:STERN:tension_t", "tug:TG1:force_t", "tug:TG2:force_t", "tug:TG2:direction_deg"],
    },
    {
        "name": "ext_anchor_heave_house",
        "title": "右錨送到鏈端、左錨 heave 經離底到收妥(400 s)",
        "duration_s": 400.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 0 }, heading: 0, speed: 0, telegraph: STOP }
environment: { wind: { trueSpeed: 0, trueDir: 180, gustiness: 0 }, current: { set: 0, drift: 0.3 }, waterDepth: 14, seabed: sand }
""",
        "commands": [
            (100, {"type": "anchor", "args": {"side": "port", "action": "letGo", "targetLength_shackles": 3}}),
            (100, {"type": "anchor", "args": {"side": "starboard", "action": "letGo", "targetLength_shackles": 12}}),
            (6000, {"type": "anchor", "args": {"side": "port", "action": "heave"}}),
        ],
        "items": ["anchor:port:length_m", "anchor:port:H_t", "anchor:port:anchorX", "anchor:port:anchorY", "anchor:starboard:length_m"],
    },
    {
        "name": "ext_bank_astern",
        "title": "沿折線斜坡岸與垂直岸壁前進後全速倒俥、中途水深變更(300 s)",
        "duration_s": 300.0,
        "yaml": COMMON + """\
initial: { position: { x: 0, y: 0 }, heading: 10, speed: 3, telegraph: SAH }
environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 9 }
banks:
  lines:
    - { id: EAST, type: sloped, slope: 2, line: [ { x: 40, y: -800 }, { x: 45, y: 0 }, { x: 35, y: 800 } ], waterSide: left }
    - { id: WEST, type: vertical, line: [ { x: -50, y: -800 }, { x: -50, y: 800 } ], waterSide: right }
""",
        "commands": [
            (2000, {"type": "telegraph", "value": "FAS"}),
            (8000, {"type": "setEnvironment", "args": {"waterDepth": 12}}),
        ],
        "items": ["bank:Y_kN", "bank:N_kNm", "bank:parameter"],
    },
]


def _item(ship, key: str) -> float:
    kind, *rest = key.split(":")
    ext = ship.external
    if kind == "tug":
        t = ext.tug(rest[0])
        return t.force_n / TONNE_FORCE if rest[1] == "force_t" else math.degrees(t.direction) % 360.0
    if kind == "anchor":
        a = ext.anchor(rest[0])
        return {"length_m": a.length, "H_t": a.last.H / TONNE_FORCE, "anchorX": a.anchor_x, "anchorY": a.anchor_y}[rest[1]]
    if kind == "fender":
        f = next(f for f in ext.berth.fenders if f.id == rest[0])
        return f.last_reaction / 1000.0
    if kind == "line":
        ln = next(x for x in ext.berth.lines if x.id == rest[0])
        return ln.last_tension / TONNE_FORCE
    if kind == "bank":
        r = ship.bank.last
        return {"Y_kN": r.Y / 1000.0, "N_kNm": r.N / 1000.0, "parameter": r.D}[rest[0]]
    raise KeyError(key)


def _external_force(ship) -> tuple[float, float, float]:
    """取樣時刻狀態下的外力合計(拖船出力為最近一步、岸壁螺槳項為最近一步步首轉速;與 C# 測試端相同的取法)。"""
    s = ship.state
    k = Kin(s.x, s.y, s.psi, s.u, s.v, s.r)
    ce, cn = ship.env.current_en
    env = Env(ce, cn, ship.env.water_depth if ship.env.water_depth is not None else math.inf)
    X = Y = N = 0.0
    if ship.external is not None and ship.external.active:
        X, Y, N = ship.external.evaluate(k, env)
    if ship.bank is not None and ship.bank.active:
        _, by, bn = ship.bank.evaluate(k, env)
        Y += by
        N += bn
    return X, Y, N


def _sample(ship, items: list[str]) -> str:
    s = ship.state
    fx, fy, fn = _external_force(ship)
    vals = [s.tick, s.t, s.x, s.y, math.degrees(s.psi) % 360.0, s.u, s.v, s.r, s.n * 60.0, fx / 1000.0, fy / 1000.0, fn / 1000.0]
    vals += [_item(ship, k) for k in items]
    return ",".join(repr(int(v)) if i == 0 else repr(float(v)) for i, v in enumerate(vals))


def run_case(case: dict, write: bool = True) -> dict:
    """執行一個案例;回傳 {rows, events, hash, ship}(測試也用此函式檢查可重現性)。"""
    yaml_path = OUT_DIR / f"{case['name']}.yaml"
    text = f"# 由 export_external_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    if write:
        yaml_path.write_text(text, encoding="utf-8", newline="\n")
    sc = load_scenario(yaml_path)
    ship = ship_from_scenario(sc, dt=0.02)
    srv = SimServer(ship, scenario=sc)
    cmds = sorted(case["commands"], key=lambda c: c[0])
    rows = [_sample(ship, case["items"])]
    steps = int(round(case["duration_s"] / ship.dt))
    ci = 0
    for _ in range(steps):
        while ci < len(cmds) and cmds[ci][0] == ship.state.tick:
            ack = srv.apply_command(cmds[ci][1])
            if not ack["ok"]:
                raise RuntimeError(f"{case['name']} tick {ship.state.tick} 指令失敗:{ack}")
            ci += 1
        ship.step()
        if ship.state.tick % SAMPLE_EVERY == 0:
            rows.append(_sample(ship, case["items"]))
    if ci != len(cmds):
        raise RuntimeError(f"{case['name']}:有指令未執行")
    return {"rows": rows, "events": list(ship.external_events), "hash": ship.state_hash(), "ship": ship}


def export(case: dict) -> None:
    res = run_case(case)
    ship = res["ship"]
    header = [
        f"# simosa_brm {__version__} 參考實作匯出(外力):{case['title']}",
        f"# command: {COMMAND}",
        f"# scenario: {case['name']}.yaml",
        f"# coefficients: data/ships/FSB1/coefficients.full.json (method={ship.c['source']['method']}, generatedAt={ship.c['generatedAt']})",
        f"# state_hash_end: {res['hash']}",
    ]
    for tick, cmd in sorted(case["commands"], key=lambda c: c[0]):
        header.append(f"# cmd: tick={tick} {json.dumps(cmd, ensure_ascii=False, separators=(',', ':'))}")
    for ev in res["events"]:
        header.append(f"# event: tick={ev['tick']} {ev['kind']} {ev['itemId']}")
    cols = ["tick", "t", "x", "y", "psi_deg", "u", "v", "r", "rpm", "ext_X", "ext_Y", "ext_N"] + case["items"]
    path = OUT_DIR / f"{case['name']}.csv"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header) + "\n")
        f.write(",".join(cols) + "\n")
        f.write("\n".join(res["rows"]) + "\n")
    s = ship.state
    print(f"已寫入 {path.name}({len(res['rows'])} 列、{len(res['events'])} 個外力事件;末狀態 x={s.x:.1f} y={s.y:.1f} "
          f"psi={math.degrees(s.psi) % 360:.1f}° 航跡 {s.track:.1f} m)")


def main() -> None:
    for case in CASES:
        export(case)


if __name__ == "__main__":
    main()
