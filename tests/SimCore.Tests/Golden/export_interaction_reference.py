"""匯出 Python 參考實作的船間交互作用夾具,供 C# ShipInteractionTests 跨語言數值比對(docs/design/physics-ship-interaction.md 第 9 節)。

執行(自專案根目錄):
    cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_interaction_reference.py

輸出到本資料夾(tests/SimCore.Tests/Golden/):
    interaction_force_table.csv     純函數取樣表:每列 (ship, loading, scale, depth_m, psi_deg, u, v, tgt_x, tgt_y, tgt_psi_deg, tgt_speed_kn,
                                    tgt_L, tgt_B, tgt_T, tgt_cb) → active, xi, eta, X_N, Y_N, N_Nm。C# 以同一船舶/裝載建立 ShipInteractionModel
                                    並以同狀態、水深與目標呼叫 Compute,逐列相對差 ≤ 1e-12(兩種語言逐項同運算順序;只差 libm 末位)。
    interaction_overtaking_run.yaml 積分案例情境(本腳本是唯一來源):FSB1 滿載 8 kn 航向北,目標船 OVT(cargo、L 130 m、B 22 m、T 7.5 m、
                                    排水量 16 490 t ≈ FSB1 的 2.1 倍)自左舷 50 m、後方 200 m 以 12 kn 同向追越(hold、無觸發),深水 1000 m,
                                    80 s 右舵 5°,150 s。
    interaction_overtaking_run.csv  ``#`` 標頭(產生指令、情境檔、末狀態雜湊、``# cmd: tick=<n> <JSON>``、``# event: tick=<n> shipInteraction <phase> <targetId>``)
                                    之後每 1 s(50 tick)一列(含 tick 0):tick,t,x,y,psi_deg,u,v,r,rpm,int_X,int_Y,int_N,xi,eta,tgt_x,tgt_y
                                    (步末狀態的交互作用力 kN、kN·m、主要目標的 ξ/η 與目標船 ENU 位置)。
                                    C# 以同一份 YAML 建立 MMG 引擎推進並比對(位置差 / 航程 < 1%、各序列相對 < 1%;事件 tick 與 phase 相同)。
``tests/test_ship_interaction.py`` 以同一路徑重算並比對(確定性)。
"""

from __future__ import annotations

import json
import math
from pathlib import Path

from simosa_brm import __version__
from simosa_brm.external import DEG2RAD, KN_TO_MPS, InteractionTarget, Kin, ShipInteractionModel
from simosa_brm.particulars import load_particulars
from simosa_brm.scenario import load_scenario, ship_from_scenario
from simosa_brm.server import SimServer

HERE = Path(__file__).resolve().parent
COMMAND = "cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_interaction_reference.py"
SAMPLE_EVERY = 50

# 純函數取樣表:船舶/裝載 × 倍率 × 水深 × 自船狀態 × 目標
SHIPS = (("FSB1", "full", 1.0), ("FSB1", "ballast", 1.0), ("FSB2", "full", 0.5))
DEPTHS = (1000.0, 50.0, 12.0)
# (psi_deg, u, v)
STATES = ((0.0, 8.0 * KN_TO_MPS, 0.0), (45.0, 6.0 * KN_TO_MPS, 0.3), (200.0, -2.0 * KN_TO_MPS, 0.0), (90.0, 0.0, 0.0))
# 目標:自船船體座標 (dx 前正, dy 右正) m、相對航向 (度)、航速 kn、L、B、T、Cb → 轉成 ENU 由自船航向旋轉
TARGETS = (
    # 追越:2 倍排水量船自左舷 50 m,縱向錯位 −1.5 L … +1.5 L
    (-150.0, -50.0, 0.0, 12.0, 130.0, 22.0, 7.5, 0.75),
    (-80.0, -50.0, 0.0, 12.0, 130.0, 22.0, 7.5, 0.75),
    (0.0, -50.0, 0.0, 12.0, 130.0, 22.0, 7.5, 0.75),
    (60.0, -50.0, 0.0, 12.0, 130.0, 22.0, 7.5, 0.75),
    (150.0, -50.0, 0.0, 12.0, 130.0, 22.0, 7.5, 0.75),
    # 右舷對遇(相對航向 180°)與 30° 交叉
    (100.0, 45.0, 180.0, 10.0, 99.0, 18.0, 5.7, 0.738),
    (-20.0, 60.0, 30.0, 9.0, 80.0, 14.0, 4.5, 0.65),
    # 漸減帶(橫向 0.9 倍限值、縱向 2.7 L)與範圍外(無作用)
    (0.0, 0.9 * 2.0 * (18.0 + 16.0), 0.0, 10.0, 100.0, 16.0, 5.0, 0.72),
    (2.7 * 99.0, -40.0, 0.0, 10.0, 100.0, 16.0, 5.0, 0.72),
    (0.0, 200.0, 0.0, 10.0, 100.0, 16.0, 5.0, 0.72),
    # 靜止目標(零力但在範圍內)與小艇
    (30.0, -40.0, 90.0, 0.0, 100.0, 16.0, 5.0, 0.72),
    (-10.0, -25.0, 0.0, 15.0, 20.0, 6.0, 2.0, 0.45),
)

RUN_CASE = {
    "name": "interaction_overtaking_run",
    "title": "FSB1 滿載 8 kn 被 OVT(L 130 m、16 490 t)以 12 kn 自左舷 50 m 追越、深水 1000 m,80 s 右舵 5°(210 s)",
    "duration_s": 210.0,
    "yaml": """\
seed: 1
ship: { id: FSB1, loading: full }
origin: { lat: 23.80, lon: 120.15 }
initial: { position: { x: 0, y: 0 }, heading: 0, speed: 8, telegraph: HAH }
environment:
  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }
  current: { set: 0, drift: 0 }
  waterDepth: 1000
  shipInteraction: { enabled: true, coefficientScale: 1.0, eventThreshold_t: 3.0 }
targets:
  - id: OVT
    name: OVERTAKER
    shipType: cargo
    loa: 130
    beam: 22
    draft: 7.5
    displacement_t: 16490
    initial: { position: { x: -50, y: -200 }, heading: 0, speed: 12 }
    behaviour: { mode: hold }
""",
    "commands": [
        (4000, {"type": "rudder", "value": 5}),
    ],
}


def _model(ship_id: str, loading: str, scale: float) -> ShipInteractionModel:
    raw = load_particulars(ship_id, loading).raw
    return ShipInteractionModel(raw, loading, {"enabled": True, "coefficientScale": scale, "targets": []})


def _target(k: Kin, spec: tuple) -> InteractionTarget:
    dx, dy, rel_deg, speed_kn, l, b, t, cb = spec
    sn, cs = math.sin(k.psi), math.cos(k.psi)
    x = k.x + dx * sn + dy * cs
    y = k.y + dx * cs - dy * sn
    psi = (k.psi + rel_deg * DEG2RAD) % (2.0 * math.pi)
    return InteractionTarget("T", x, y, psi, speed_kn * KN_TO_MPS, l, b, t, cb)


def force_table_rows() -> list[tuple]:
    rows: list[tuple] = []
    for ship_id, loading, scale in SHIPS:
        model = _model(ship_id, loading, scale)
        for depth in DEPTHS:
            for psi_deg, u, v in STATES:
                k = Kin(100.0, -200.0, psi_deg * DEG2RAD, u, v, 0.0)
                for spec in TARGETS:
                    t = _target(k, spec)
                    r = model.compute(k, depth, [t])
                    rows.append((ship_id, loading, scale, depth, psi_deg, u, v, t.x, t.y, t.psi / DEG2RAD, spec[3], t.L, t.B, t.T, t.cb,
                                 1 if r.active else 0, r.xi, r.eta, r.X, r.Y, r.N))
    return rows


def write_force_table() -> Path:
    out = HERE / "interaction_force_table.csv"
    lines = [
        f"# generated by export_interaction_reference.py (simosa_brm {__version__}); do not edit",
        f"# command: {COMMAND}",
        "ship,loading,scale,depth_m,psi_deg,u,v,tgt_x,tgt_y,tgt_psi_deg,tgt_speed_kn,tgt_L,tgt_B,tgt_T,tgt_cb,active,xi,eta,X_N,Y_N,N_Nm",
    ]
    for row in force_table_rows():
        lines.append(",".join(v if isinstance(v, str) else (repr(v) if isinstance(v, int) else repr(float(v))) for v in row))
    out.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    return out


def _sample(ship) -> str:
    s = ship.state
    r = ship.interaction.last
    tg = ship.interaction.targets[0]
    vals = [s.tick, s.t, s.x, s.y, math.degrees(s.psi) % 360.0, s.u, s.v, s.r, s.n * 60.0, r.X / 1000.0, r.Y / 1000.0, r.N / 1000.0, r.xi, r.eta, tg.x, tg.y]
    return ",".join(repr(int(v)) if i == 0 else repr(float(v)) for i, v in enumerate(vals))


def run_case(case: dict, write: bool = True) -> dict:
    """執行積分案例;回傳 {rows, events, hash, ship}(測試也用此函式檢查可重現性)。"""
    yaml_path = HERE / f"{case['name']}.yaml"
    text = f"# 由 export_interaction_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    if write:
        yaml_path.write_text(text, encoding="utf-8", newline="\n")
    sc = load_scenario(yaml_path)
    ship = ship_from_scenario(sc, dt=0.02)
    srv = SimServer(ship, scenario=sc)
    cmds = sorted(case["commands"], key=lambda c: c[0])
    rows = [_sample(ship)]
    events: list[str] = []
    steps = int(round(case["duration_s"] / ship.dt))
    ci = 0
    n_events = 0
    for _ in range(steps):
        while ci < len(cmds) and cmds[ci][0] == ship.state.tick:
            ack = srv.apply_command(cmds[ci][1])
            if not ack["ok"]:
                raise RuntimeError(f"{case['name']} tick {ship.state.tick} 指令失敗:{ack}")
            ci += 1
        ship.step()
        for ev in list(ship.external_events)[n_events:]:
            if ev["kind"] == "shipInteraction":
                events.append(f"tick={ev['tick']} shipInteraction {ev['data']['phase']} {ev['targetId']}")
        n_events = len(ship.external_events)
        if ship.state.tick % SAMPLE_EVERY == 0:
            rows.append(_sample(ship))
    if ci != len(cmds):
        raise RuntimeError(f"{case['name']}:有指令未執行")
    return {"rows": rows, "events": events, "hash": ship.state_hash(), "ship": ship}


def write_run(case: dict) -> Path:
    res = run_case(case)
    ship = res["ship"]
    header = [
        f"# simosa_brm {__version__} 參考實作匯出(船間交互作用):{case['title']}",
        f"# command: {COMMAND}",
        f"# scenario: {case['name']}.yaml",
        f"# coefficients: data/ships/FSB1/coefficients.full.json (method={ship.c['source']['method']}, generatedAt={ship.c['generatedAt']})",
        f"# state_hash_end: {res['hash']}",
    ]
    for tick, cmd in sorted(case["commands"], key=lambda c: c[0]):
        header.append(f"# cmd: tick={tick} {json.dumps(cmd, ensure_ascii=False, separators=(',', ':'))}")
    for ev in res["events"]:
        header.append(f"# event: {ev}")
    cols = ["tick", "t", "x", "y", "psi_deg", "u", "v", "r", "rpm", "int_X", "int_Y", "int_N", "xi", "eta", "tgt_x", "tgt_y"]
    path = HERE / f"{case['name']}.csv"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header) + "\n")
        f.write(",".join(cols) + "\n")
        f.write("\n".join(res["rows"]) + "\n")
    s = ship.state
    print(f"已寫入 {path.name}({len(res['rows'])} 列、{len(res['events'])} 個事件;末狀態 x={s.x:.1f} y={s.y:.1f} psi={math.degrees(s.psi) % 360:.2f}° "
          f"u={s.u:.3f} v={s.v:.3f})")
    for ev in res["events"]:
        print(f"  {ev}")
    return path


if __name__ == "__main__":
    print(write_force_table())
    write_run(RUN_CASE)
