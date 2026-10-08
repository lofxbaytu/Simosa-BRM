"""匯出 Python 參考實作(simosa_brm.mmg)的狀態時間序列,供 C# 數值一致性測試(規劃書第 14 章:C# 核心 vs Python 參考實作 軌跡差異 <1%)。

執行(自專案根目錄):
    cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_reference.py

輸出到本資料夾(tests/SimCore.Tests/Golden/),由 MmgGoldenTests.PythonConsistency 讀取:
    fsb1_full_turn_stbd35.csv   FSB1 滿載、14.5 kn 直航穩態、35° 右滿舵迴旋 600 s
    fsb1_full_zigzag10.csv      FSB1 滿載、14.5 kn、10°/10° Z 形(左舵先)600 s;反舵時刻由 Python 的航向判斷決定並記為事件,
                                C# 以相同 tick 開迴路重播同一舵令序列(不重作判斷)
    fsb1_full_crashstop.csv     FSB1 滿載、14.5 kn、t=0 下 EFAS(緊急全速退)600 s;涵蓋主機換向狀態機(燃油切斷→軸轉速衰減→反向點火)

格式:以 ``#`` 開頭的標頭列(產生指令、初始狀態、事件 tick 與舵令/車鐘),之後為 CSV:
    tick,t,x,y,psi_deg,u,v,r,delta_deg,rpm,thr
每 1 s(50 tick)一列,含 tick 0 的初始狀態。dt = 0.02 s、深水(水深 1000 m,淺水倍率為 1)、無風無流。
x 東、y 北(m),psi 航向 0–360°,u/v 對水速度(m/s),r 艏搖角速度(rad/s),delta 實際舵角(度,右正),rpm 軸轉速(倒車負),thr 側推。
"""

from __future__ import annotations

import math
from pathlib import Path

from simosa_brm import __version__
from simosa_brm.mmg import DEG, MMGShip, make_ship, wrap_pi

OUT_DIR = Path(__file__).resolve().parent
DT = 0.02
DURATION_S = 600.0
SAMPLE_EVERY = 50  # tick
DEPTH_M = 1000.0
SPEED_KN = 14.5

COMMAND = "cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_reference.py"


def _sample(ship: MMGShip) -> str:
    s = ship.state
    psi_deg = math.degrees(s.psi) % 360.0
    return ",".join(repr(float(v)) for v in (s.tick, s.t, s.x, s.y, psi_deg, s.u, s.v, s.r, math.degrees(s.delta), s.n * 60.0, s.thr))


def _run(name: str, title: str, driver) -> None:
    """driver(ship, events, tick) 在每步前呼叫,可下令並把 (tick, kind, value) 加進 events。"""
    ship = make_ship("FSB1", "full", dt=DT)
    ship.set_environment(wind_speed_kn=0.0, current_drift_kn=0.0, water_depth_m=DEPTH_M)
    ship.reset(speed_kn=SPEED_KN)
    rpm0 = ship.state.n * 60.0  # 該航速的直航平衡轉速(steady_rpm_for_speed)
    telegraph0 = ship.ctl.telegraph
    events: list[tuple[int, str, float | str]] = []
    rows = [_sample(ship)]
    steps = int(round(DURATION_S / DT))
    for _ in range(steps):
        driver(ship, events, ship.state.tick)
        ship.step()
        if ship.state.tick % SAMPLE_EVERY == 0:
            rows.append(_sample(ship))
    header = [
        f"# simosa_brm {__version__} 參考實作匯出:{title}",
        f"# command: {COMMAND}",
        f"# initial: ship=FSB1 loading=full speed_kn={SPEED_KN!r} heading_deg=0.0 x=0.0 y=0.0 rpm={rpm0!r} "
        f"telegraph={telegraph0} depth_m={DEPTH_M!r} dt={DT!r}",
        f"# coefficients: data/ships/FSB1/coefficients.full.json (method={ship.c['source']['method']}, generatedAt={ship.c['generatedAt']})",
        f"# state_hash_end: {ship.state_hash()}",
    ]
    for tick, kind, value in events:
        header.append(f"# event: tick={tick} {kind}={value}")
    path = OUT_DIR / name
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header) + "\n")
        f.write("tick,t,x,y,psi_deg,u,v,r,delta_deg,rpm,thr\n")
        f.write("\n".join(rows) + "\n")
    print(f"已寫入 {path}({len(rows)} 列,{len(events)} 個事件;末狀態 x={ship.state.x:.1f} y={ship.state.y:.1f} psi={math.degrees(ship.state.psi) % 360:.1f}°)")


def turning_driver(ship: MMGShip, events: list, tick: int) -> None:
    if tick == 0:
        ship.set_rudder(35.0)
        events.append((tick, "rudder_deg", 35.0))


class ZigzagDriver:
    """10/10 Z 形:航向變化達 ±10° 時反舵(與 manoeuvres.zigzag 相同的判斷,但不限制超越次數)。"""

    def __init__(self, rudder_deg: float = 10.0, heading_deg: float = 10.0, first_side: str = "port") -> None:
        self.rudder = rudder_deg
        self.heading = heading_deg
        self.cur = -1.0 if first_side.startswith("p") else 1.0
        self.psi0: float | None = None
        self.phase = "waiting"
        self.extreme = 0.0

    def __call__(self, ship: MMGShip, events: list, tick: int) -> None:
        s = ship.state
        if self.psi0 is None:
            self.psi0 = s.psi
            ship.set_rudder(self.cur * self.rudder)
            events.append((tick, "rudder_deg", self.cur * self.rudder))
            return
        dpsi = math.degrees(wrap_pi(s.psi - self.psi0))
        if self.phase == "waiting":
            if self.cur * dpsi >= self.heading:
                self.cur = -self.cur
                ship.set_rudder(self.cur * self.rudder)
                events.append((tick, "rudder_deg", self.cur * self.rudder))
                self.phase = "overshoot"
                self.extreme = dpsi
        else:
            if -self.cur * dpsi > -self.cur * self.extreme:
                self.extreme = dpsi
            elif (-self.cur * s.r) < 0.0:
                self.phase = "waiting"


def crashstop_driver(ship: MMGShip, events: list, tick: int) -> None:
    if tick == 0:
        ship.set_telegraph("EFAS")
        events.append((tick, "telegraph", "EFAS"))


def main() -> None:
    _run("fsb1_full_turn_stbd35.csv", "FSB1 滿載 14.5 kn 35° 右滿舵迴旋 600 s", turning_driver)
    _run("fsb1_full_zigzag10.csv", "FSB1 滿載 14.5 kn 10°/10° Z 形(左舵先)600 s", ZigzagDriver())
    _run("fsb1_full_crashstop.csv", "FSB1 滿載 14.5 kn 緊急停船(EFAS)600 s", crashstop_driver)


if __name__ == "__main__":
    main()
