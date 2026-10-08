"""命令列入口 ``simosa-brm``:estimate / identify / validate / manoeuvre / serve。"""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import asdict, is_dataclass
from pathlib import Path
from typing import Any

from . import __version__, paths


def _ship_args(p: argparse.ArgumentParser) -> None:
    p.add_argument("--ship", required=True, choices=["FSB1", "FSB2"], help="船舶代號")
    p.add_argument("--loading", default="full", choices=["full", "ballast"], help="裝載狀態")


def _result_json(obj: Any) -> Any:
    if is_dataclass(obj) and not isinstance(obj, type):
        d = asdict(obj)
        d.pop("history", None)
        return d
    return obj


def cmd_estimate(a: argparse.Namespace) -> int:
    from .coefficients import estimate_coefficients, save_coefficients
    from .particulars import load_particulars, load_trial_targets

    sp = load_particulars(a.ship, a.loading)
    try:
        tt = load_trial_targets(a.ship)
    except FileNotFoundError:
        tt = None
    c = estimate_coefficients(sp, tt)
    out = Path(a.out) if a.out else paths.calibration_out_dir(a.ship) / f"coefficients.{a.loading}.estimate.json"
    save_coefficients(c, out)
    print(f"已寫入 {out}")
    if a.print:
        print(json.dumps({k: c[k] for k in ("mass", "hull", "propeller", "rudder")}, ensure_ascii=False, indent=2))
    return 0


def cmd_identify(a: argparse.Namespace) -> int:
    from .identify import identify_and_save

    params = [s.strip() for s in a.params.split(",")] if a.params else None
    identify_and_save(a.ship, a.loading, params=params, dt=a.dt, max_nfev=a.max_nfev, prior_weight=a.prior_weight,
                      workers=a.workers)
    return 0


def cmd_validate(a: argparse.Namespace) -> int:
    from .report import validate

    out = Path(a.out) if a.out else None
    validate(a.ship, a.loading, dt=a.dt, out_dir=out, make_plots=not a.no_plots)
    return 0


def cmd_manoeuvre(a: argparse.Namespace) -> int:
    from . import manoeuvres as M
    from .mmg import make_ship

    ship = make_ship(a.ship, a.loading, dt=a.dt)
    if a.depth is not None:
        ship.set_environment(water_depth_m=a.depth)
    if a.wind is not None:
        spd, d = a.wind.split(",")
        ship.set_environment(wind_speed_kn=float(spd), wind_dir_deg=float(d))
    kind = a.kind
    if kind == "turning":
        r = M.turning_circle(ship, a.rudder, a.side, a.speed)
    elif kind == "zigzag":
        r = M.zigzag(ship, a.rudder, a.rudder, a.side, a.speed, n_overshoots=a.overshoots)
    elif kind == "crashstop":
        r = M.crash_stop(ship, a.speed)
    elif kind == "inertia":
        r = M.inertia_stop(ship, a.speed, 5.0)
    elif kind == "thruster":
        r = M.thruster_turn(ship, a.side, 90.0, a.speed)
    elif kind == "speed":
        rpms = [float(x) for x in a.rpms.split(",")] if a.rpms else [float(v) for v in ship.eng["telegraph"].values() if v > 0]
        r = M.speed_rpm_table(ship, sorted(set(rpms)))
    else:
        raise SystemExit(f"未知操縱 {kind}")
    print(json.dumps(_result_json(r), ensure_ascii=False, indent=2, default=lambda o: None if isinstance(o, float) and math.isnan(o) else o))
    if a.csv and hasattr(r, "history"):
        with open(a.csv, "w", encoding="utf-8") as f:
            f.write("t,x,y,heading_deg,speed_kn,u,v,rot_degpm,rudder_deg,rpm\n")
            for h in r.history:
                f.write(f"{h.t},{h.x},{h.y},{h.heading_deg},{h.speed_kn},{h.u},{h.v},{h.rot_degpm},{h.rudder_deg},{h.rpm}\n")
        print(f"歷程已寫入 {a.csv}")
    return 0


def cmd_serve(a: argparse.Namespace) -> int:
    from .server import serve

    serve(a.ship, a.loading, host=a.host, port=a.port, time_scale=a.time_scale, speed_kn=a.speed, heading_deg=a.heading)
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="simosa-brm", description="Simosa BRM MMG 參考實作與校正工具")
    p.add_argument("--version", action="version", version=f"simosa-brm {__version__}")
    sub = p.add_subparsers(dest="cmd", required=True)

    e = sub.add_parser("estimate", help="由 particulars 以經驗公式估計係數(寫到 build/calibration/<ID>/,不覆蓋已識別檔)")
    _ship_args(e)
    e.add_argument("--out", help="輸出路徑(預設 build/calibration/<ID>/coefficients.<loading>.estimate.json)")
    e.add_argument("--print", action="store_true", help="同時印出主要係數")
    e.set_defaults(func=cmd_estimate)

    i = sub.add_parser("identify", help="參數識別,寫 data/ships/<ID>/coefficients.<loading>.json")
    _ship_args(i)
    i.add_argument("--dt", type=float, default=0.1, help="識別用步長(s),預設 0.1;驗證用 0.02")
    i.add_argument("--max-nfev", type=int, default=40)
    i.add_argument("--prior-weight", type=float, default=0.3, help="先驗殘差權重")
    i.add_argument("--params", help="逗號分隔的參數名稱(預設 12 個)")
    i.add_argument("--workers", type=int, default=None, help="平行行程數(預設 min(參數數+1, CPU)")
    i.set_defaults(func=cmd_identify)

    v = sub.add_parser("validate", help="驗證報告(markdown + png)到 build/calibration/<ID>/")
    _ship_args(v)
    v.add_argument("--dt", type=float, default=0.02)
    v.add_argument("--out", help="輸出資料夾")
    v.add_argument("--no-plots", action="store_true")
    v.set_defaults(func=cmd_validate)

    m = sub.add_parser("manoeuvre", help="單一操縱試驗")
    m.add_argument("kind", choices=["turning", "zigzag", "crashstop", "inertia", "thruster", "speed"])
    _ship_args(m)
    m.add_argument("--rudder", type=float, default=35.0, help="舵角(度)")
    m.add_argument("--side", default="starboard", help="port / starboard(迴旋方向或 Z 形首舵)")
    m.add_argument("--speed", type=float, default=10.0, help="進入速度(kn)")
    m.add_argument("--overshoots", type=int, default=3)
    m.add_argument("--rpms", help="speed:逗號分隔轉速")
    m.add_argument("--dt", type=float, default=0.02)
    m.add_argument("--depth", type=float, help="水深(m),省略為深水")
    m.add_argument("--wind", help="風 'kn,來向度'")
    m.add_argument("--csv", help="把歷程寫成 CSV")
    m.set_defaults(func=cmd_manoeuvre)

    s = sub.add_parser("serve", help="WebSocket 即時模擬(每 40 ms 送 state JSON)")
    _ship_args(s)
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=8765)
    s.add_argument("--time-scale", type=float, default=1.0)
    s.add_argument("--speed", type=float, default=0.0, help="初始速度(kn)")
    s.add_argument("--heading", type=float, default=0.0, help="初始航向(度)")
    s.set_defaults(func=cmd_serve)
    return p


def main(argv: list[str] | None = None) -> int:
    a = build_parser().parse_args(argv)
    return int(a.func(a) or 0)


if __name__ == "__main__":
    sys.exit(main())
