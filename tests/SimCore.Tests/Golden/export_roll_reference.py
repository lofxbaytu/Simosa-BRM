"""匯出 Python 參考實作的解耦 1 自由度橫搖夾具,供 C# RollModelTests 跨語言數值比對(docs/design/physics-roll.md 第 8 節)。

執行(自專案根目錄):
    cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_roll_reference.py

輸出到本資料夾(tests/SimCore.Tests/Golden/):
    roll_run.yaml   積分案例情境(本腳本是唯一來源):FSB1 滿載 10 kn(FAH),ship.roll 啟用(預設:T = 2·0.4·B/√GM = 10.29 s、ζ 0.08、gain 1),
                    真風 25 kn 自 060、Hs 2 m / Tp 9 s 自 120 JONSWAP(近共振的斜後浪)、水深 30 m;50 s 右舵 20°、150 s 正舵,300 s。
    roll_run.csv    ``#`` 標頭(產生指令、情境檔、末狀態雜湊、``# cmd: tick=<n> <JSON>``)之後每 1 s(50 tick)一列(含 tick 0):
                    tick,t,x,y,psi_deg,u,v,r,roll_deg,rollRate_degps,staticHeel_deg,turnHeel_deg,windHeel_deg,waveSlope_deg
                    (步末 φ、φ' 與該步步首算出的靜傾/波斜率,度、度/秒)。
                    C# 以同一份 YAML 建立 MMG 引擎推進並比對:φ、φ' 與靜傾/波斜率逐列 |差| ≤ 1e-9·max(1, |值|)(兩種語言逐項同運算順序;
                    只差 libm 末位),位置差 / 航程 < 1%。
``tests/test_roll.py`` 以同一路徑重算並比對(確定性)。
"""

from __future__ import annotations

import json
import math
from pathlib import Path

from simosa_brm import __version__
from simosa_brm.roll import RAD2DEG
from simosa_brm.scenario import load_scenario, ship_from_scenario
from simosa_brm.server import SimServer

HERE = Path(__file__).resolve().parent
COMMAND = "cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_roll_reference.py"
SAMPLE_EVERY = 50

RUN_CASE = {
    "name": "roll_run",
    "title": "FSB1 滿載 10 kn、ship.roll 預設、真風 25 kn 自 060、Hs 2 m / Tp 9 s 自 120 JONSWAP、水深 30 m,50 s 右舵 20°、150 s 正舵(300 s)",
    "duration_s": 300.0,
    "yaml": """\
seed: 3
ship: { id: FSB1, loading: full, roll: { enabled: true } }
origin: { lat: 23.80, lon: 120.15 }
initial: { position: { x: 0, y: 0 }, heading: 0, speed: 10, telegraph: FAH }
environment:
  wind: { trueSpeed: 25, trueDir: 60, gustiness: 0 }
  current: { set: 0, drift: 0 }
  waterDepth: 30
  waves: { significantHeight_m: 2.0, peakPeriod_s: 9.0, direction_deg: 120, spectrum: jonswap, gamma: 3.3 }
""",
    "commands": [
        (2500, {"type": "rudder", "value": 20}),
        (7500, {"type": "rudder", "value": 0}),
    ],
}

COLUMNS = ["tick", "t", "x", "y", "psi_deg", "u", "v", "r", "roll_deg", "rollRate_degps", "staticHeel_deg", "turnHeel_deg", "windHeel_deg", "waveSlope_deg"]


def _sample(ship) -> str:
    s = ship.state
    r = ship.roll
    vals = [s.tick, s.tick * ship.dt, s.x, s.y, math.degrees(s.psi) % 360.0, s.u, s.v, s.r,
            r.angle * RAD2DEG, r.rate * RAD2DEG, r.static_heel * RAD2DEG, r.turn_heel * RAD2DEG, r.wind_heel * RAD2DEG, r.wave_slope * RAD2DEG]
    return ",".join(repr(int(v)) if i == 0 else repr(float(v)) for i, v in enumerate(vals))


def run_case(case: dict, write: bool = True) -> dict:
    """執行積分案例;回傳 {rows, hash, ship}(測試也用此函式檢查可重現性)。"""
    yaml_path = HERE / f"{case['name']}.yaml"
    text = f"# 由 export_roll_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    if write:
        yaml_path.write_text(text, encoding="utf-8", newline="\n")
    sc = load_scenario(yaml_path)
    ship = ship_from_scenario(sc, dt=0.02)
    if ship.roll is None:
        raise RuntimeError(f"{case['name']}:情境未啟用 ship.roll")
    srv = SimServer(ship, scenario=sc)
    cmds = sorted(case["commands"], key=lambda c: c[0])
    rows = [_sample(ship)]
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
            rows.append(_sample(ship))
    if ci != len(cmds):
        raise RuntimeError(f"{case['name']}:有指令未執行")
    return {"rows": rows, "hash": ship.state_hash(), "ship": ship}


def write_run(case: dict) -> Path:
    res = run_case(case)
    ship = res["ship"]
    r = ship.roll
    header = [
        f"# simosa_brm {__version__} 參考實作匯出(解耦 1 自由度橫搖):{case['title']}",
        f"# command: {COMMAND}",
        f"# scenario: {case['name']}.yaml",
        f"# coefficients: data/ships/FSB1/coefficients.full.json (method={ship.c['source']['method']}, generatedAt={ship.c['generatedAt']})",
        f"# roll: naturalPeriod_s={r.natural_period!r} dampingRatio={r.damping_ratio!r} GM={r.gm!r} lateralArea_m2={r.lateral_area!r} windLever_m={r.wind_lever!r}",
        f"# phases: {' '.join(repr(p) for p in r.phases)}",
        f"# state_hash_end: {res['hash']}",
    ]
    for tick, cmd in sorted(case["commands"], key=lambda c: c[0]):
        header.append(f"# cmd: tick={tick} {json.dumps(cmd, ensure_ascii=False, separators=(',', ':'))}")
    path = HERE / f"{case['name']}.csv"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header) + "\n")
        f.write(",".join(COLUMNS) + "\n")
        f.write("\n".join(res["rows"]) + "\n")
    s = ship.state
    print(f"已寫入 {path.name}({len(res['rows'])} 列;末狀態 x={s.x:.1f} y={s.y:.1f} psi={math.degrees(s.psi) % 360:.1f}° "
          f"φ={r.angle * RAD2DEG:.3f}° φ'={r.rate * RAD2DEG:.3f}°/s 靜傾={r.static_heel * RAD2DEG:.3f}°)")
    return path


if __name__ == "__main__":
    write_run(RUN_CASE)
