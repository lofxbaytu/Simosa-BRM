"""驗證報告(規劃書 6.3 驗證、14 章「物理」驗收):模擬 vs 試俥/海報目標,輸出 markdown 與 png 到 build/calibration/<ID>/。

- 比對項目:識別區(迴旋、10/10 Z 形)與驗證區(20/20 Z 形、緊急停船、慣性停船、倒車→進車、側推迴轉、速度-功率)。
- 公差依 trial_targets.tolerances;超出者在表中標示並在「已知偏差」節說明。
- 圖:迴旋軌跡(含試俥前進距離/橫距/戰術直徑參考線)、Z 形時間歷程、停船曲線、速度-轉速。
"""

from __future__ import annotations

import datetime as _dt
import json
import math
import os
from pathlib import Path
from typing import Any

from . import __version__, paths

# matplotlib 字型快取等一律放在專案 .cache 內(CLAUDE.md)
os.environ.setdefault("MPLCONFIGDIR", str(paths.cache_dir("matplotlib")))

import matplotlib  # noqa: E402

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

from .coefficients import load_or_estimate  # noqa: E402
from .manoeuvres import run_all  # noqa: E402
from .mmg import MMGShip  # noqa: E402
from .particulars import KN_TO_MPS, load_particulars, load_trial_targets  # noqa: E402

CABLE_M = 185.2
# 圖的色彩角色(dataviz 參考調色盤,淺色模式):模擬 = 藍、試俥 = 橘、第三序列 = 青
C_SIM, C_TRIAL, C_THIRD = "#2a78d6", "#eb6834", "#1baf7a"
C_TEXT, C_TEXT2, C_GRID, C_SURFACE = "#0b0b0b", "#52514e", "#e6e5e1", "#fcfcfb"


def _style() -> None:
    plt.rcParams.update({
        "figure.facecolor": C_SURFACE, "axes.facecolor": C_SURFACE, "axes.edgecolor": C_GRID,
        "axes.labelcolor": C_TEXT2, "xtick.color": C_TEXT2, "ytick.color": C_TEXT2, "text.color": C_TEXT,
        "axes.grid": True, "grid.color": C_GRID, "grid.linewidth": 0.8, "axes.spines.top": False,
        "axes.spines.right": False, "lines.linewidth": 2.0, "font.size": 10, "legend.frameon": False,
        "figure.dpi": 120,
    })


class Row:
    def __init__(self, section: str, item: str, trial: float | None, sim: float | None, tol: float | None,
                 unit: str = "", note: str = "", formal: bool = True) -> None:
        self.section, self.item, self.trial, self.sim, self.tol, self.unit, self.note, self.formal = section, item, trial, sim, tol, unit, note, formal

    @property
    def deviation(self) -> float | None:
        if self.trial is None or self.sim is None or math.isnan(self.sim):
            return None
        return self.sim - self.trial

    @property
    def deviation_pct(self) -> float | None:
        d = self.deviation
        if d is None or not self.trial:
            return None
        return 100.0 * d / self.trial

    @property
    def within(self) -> bool | None:
        if self.tol is None or self.deviation is None:
            return None
        return abs(self.deviation) <= self.tol

    def as_dict(self) -> dict[str, Any]:
        return {"section": self.section, "item": self.item, "trial": self.trial, "sim": self.sim, "tolerance": self.tol,
                "unit": self.unit, "deviation": self.deviation, "deviation_pct": self.deviation_pct,
                "withinTolerance": self.within, "formal": self.formal, "note": self.note}


def _fmt(v: float | None, nd: int = 1) -> str:
    if v is None or (isinstance(v, float) and math.isnan(v)):
        return "—"
    return f"{v:.{nd}f}"


def compare(results: dict[str, Any], tt: dict[str, Any], lpp: float) -> list[Row]:
    tol = tt.get("tolerances", {})
    t_pct = float(tol.get("turning_pct", 10)) / 100.0
    t_minl = float(tol.get("turning_minL", 0.3)) * lpp
    z_deg = tol.get("zigzag_deg")
    z_pct = tol.get("zigzag_pct")
    s_pct = float(tol.get("stopping_pct", 15)) / 100.0
    v_kn = float(tol.get("speed_kn", 0.5))
    rows: list[Row] = []
    ident = tt.get("identification", {})
    val = tt.get("validation", {})

    tc = ident.get("turningCircle35") or val.get("turningCircle35")
    sec_turn = "35° 迴旋(識別)" if "turningCircle35" in ident else "35° 迴旋(驗證)"
    if tc:
        for side in ("port", "starboard"):
            r = results.get(f"turning_{side}")
            if r is None or side not in tc:
                continue
            d = tc[side]
            lab = "左" if side == "port" else "右"
            for key, attr, name in (("advance_m", "advance_m", "前進距離"), ("transfer_m", "transfer_m", "橫距"), ("tacticalDiameter_m", "tactical_diameter_m", "戰術直徑")):
                if key in d:
                    rows.append(Row(sec_turn, f"{lab}迴旋 {name}", float(d[key]), getattr(r, attr), max(t_pct * float(d[key]), t_minl), "m"))
            for mk in d.get("marks", []):
                hd = int(mk["heading_deg"])
                sm = r.marks.get(hd)
                if "t_s" in mk:
                    rows.append(Row(sec_turn, f"{lab}迴旋 {hd}° 歷時", float(mk["t_s"]), sm["t_s"] if sm else math.nan, max(t_pct * float(mk["t_s"]), 5.0), "s"))
                if "speed_kn" in mk:
                    rows.append(Row(sec_turn, f"{lab}迴旋 {hd}° 速度", float(mk["speed_kn"]), sm["speed_kn"] if sm else math.nan, v_kn, "kn"))
            if "steadySpeedInTurn_kn" in tc:
                lo, hi = tc["steadySpeedInTurn_kn"]
                rows.append(Row(sec_turn, f"{lab}迴旋 定常速度", 0.5 * (lo + hi), r.steady_speed_kn, v_kn + 0.5 * (hi - lo), "kn", f"試俥範圍 {lo}–{hi} kn"))
    for key, name in (("zigzag10_10", "10/10 Z 形"), ("zigzag20_20", "20/20 Z 形")):
        zz = ident.get(key) or val.get(key)
        if not zz:
            continue
        sec = f"{name}(識別)" if key in ident else f"{name}(驗證)"
        formal = z_deg is not None
        for first in ("portFirst", "starboardFirst"):
            z = results.get(f"{key}_{first}")
            if z is None or first not in zz:
                continue
            lab = "左起" if first == "portFirst" else "右起"
            for i, o in enumerate(zz[first]["overshoots_deg"]):
                sim = z.overshoots_deg[i] if i < len(z.overshoots_deg) else math.nan
                tl = max(float(z_deg or 3.0), float(z_pct or 25.0) / 100.0 * float(o)) if formal else None
                rows.append(Row(sec, f"{lab} 第 {i + 1} 超越角", float(o), sim, tl, "°", "" if formal else "海報資料:不列入數值驗收", formal))
    cs = val.get("crashStop")
    c = results.get("crashStop")
    if cs and c is not None:
        if "asternStart_s" in cs:
            rows.append(Row("緊急停船(驗證)", "倒車啟動時間", float(cs["asternStart_s"]), c.astern_start_s, s_pct * float(cs["asternStart_s"]), "s",
                            "機械參數(換向延遲)取自此值,非獨立驗證", False))
        if "stopTime_s" in cs:
            rows.append(Row("緊急停船(驗證)", "停船時間", float(cs["stopTime_s"]), c.stop_time_s, s_pct * float(cs["stopTime_s"]), "s"))
        if "trackReach_m" in cs:
            rows.append(Row("緊急停船(驗證)", "航跡距離", float(cs["trackReach_m"]), c.track_reach_m, s_pct * float(cs["trackReach_m"]), "m"))
        elif "trackReach_cables" in cs:
            tr = float(cs["trackReach_cables"]) * CABLE_M
            rows.append(Row("緊急停船(驗證)", "航跡距離", tr, c.track_reach_m, s_pct * tr, "m", f"海報 {cs['trackReach_cables']} 鏈"))
        rows.append(Row("緊急停船(驗證)", "停船時艏向變化", None, c.final_heading_change_deg, None, "°", "右旋槳倒車艉向左 → 艏向右", False))
    ins = val.get("inertiaStop")
    i = results.get("inertiaStop")
    if ins and i is not None and "approachSpeed_kn" in ins:
        rows.append(Row("慣性停船(驗證)", "減至 5 kn 時間", float(ins["timeTo5kn_s"]), i.time_to_target_s, s_pct * float(ins["timeTo5kn_s"]), "s",
                        "試俥航跡彎曲(head 1098 / side 402 m),直線滑行無法重現"))
        rows.append(Row("慣性停船(驗證)", "減至 5 kn 距離", float(ins["distanceTo5kn_m"]), i.distance_to_target_m, s_pct * float(ins["distanceTo5kn_m"]), "m"))
        if "headReach_m" in ins:
            rows.append(Row("慣性停船(驗證)", "head reach", float(ins["headReach_m"]), i.head_reach_m, None, "m", "參考", False))
            rows.append(Row("慣性停船(驗證)", "side reach", float(ins["sideReach_m"]), i.side_reach_m, None, "m", "參考(試俥含偏轉)", False))
    ca = val.get("crashAhead")
    a = results.get("crashAhead")
    if ca and a is not None:
        rows.append(Row("倒車→全速進(驗證)", "停船時間", float(ca["time_s"]), a.stop_time_s, s_pct * float(ca["time_s"]), "s", "換向延遲以進→退值假設", False))
        rows.append(Row("倒車→全速進(驗證)", "距離", float(ca["distance_m"]), a.track_reach_m, s_pct * float(ca["distance_m"]), "m", "", False))
    bt = val.get("bowThrusterTurn90")
    if bt:
        for side in ("port", "starboard"):
            t = results.get(f"thrusterTurn_{side}")
            if t is None:
                continue
            lab = "左" if side == "port" else "右"
            rows.append(Row("艏側推 90° 迴轉(驗證)", f"{lab}轉 90° 時間(約 {bt.get('speed_kn', 0)} kn)", float(bt[f"{side}_s"]), t.time_s, s_pct * float(bt[f"{side}_s"]), "s",
                            "側推有效推力由海報零速迴轉率校準;趨勢驗證", False))
    spd = val.get("speedPower") or []
    tbl = results.get("speedPower") or []
    for p, s in zip(spd, tbl):
        note = str(p.get("note", ""))
        # 阻力/推進係數即由同一組速度-轉速資料以推力恆等擬合(規劃書 6.3 識別第一步),此處為驗收項目但非獨立驗證
        note = (note + ";" if note else "") + "阻力由同一組速度資料擬合,非獨立驗證"
        rows.append(Row("速度-轉速(驗收)", f"{p['rpm']} rpm", float(p["speed_kn"]), s["speed_kn"], v_kn, "kn", note))
    return rows


# ---------------------------------------------------------------------------
# 圖
# ---------------------------------------------------------------------------
def _plot_turning(results: dict[str, Any], tt: dict[str, Any], out: Path, ship_id: str) -> Path | None:
    tc = (tt.get("identification", {}).get("turningCircle35") or tt.get("validation", {}).get("turningCircle35"))
    rs = [(s, results.get(f"turning_{s}")) for s in ("port", "starboard") if results.get(f"turning_{s}") is not None]
    if not rs:
        return None
    _style()
    fig, axes = plt.subplots(1, len(rs), figsize=(5.2 * len(rs), 5.6), squeeze=False)
    for ax, (side, r) in zip(axes[0], rs):
        xs = [h.x for h in r.history]
        ys = [h.y for h in r.history]
        ax.plot(xs, ys, color=C_SIM, label="Simulation")
        sign = 1.0 if side == "starboard" else -1.0
        if tc and side in tc:
            d = tc[side]
            if "advance_m" in d:
                ax.axhline(d["advance_m"], color=C_TRIAL, lw=1.2, ls="--")
                ax.text(sign * 5, d["advance_m"] + 6, f"trial advance {d['advance_m']:.0f} m", color=C_TEXT2, fontsize=8, ha="left" if sign > 0 else "right")
            y_lab = 0.35 * d.get("advance_m", 300.0)
            if "transfer_m" in d:
                ax.axvline(sign * d["transfer_m"], color=C_TRIAL, lw=1.2, ls="--")
                ax.text(sign * d["transfer_m"], y_lab, f"trial transfer {d['transfer_m']:.0f} m", color=C_TEXT2, fontsize=8, rotation=90, va="bottom", ha="right" if sign > 0 else "left")
            if "tacticalDiameter_m" in d:
                ax.axvline(sign * d["tacticalDiameter_m"], color=C_TRIAL, lw=1.2, ls=":")
                ax.text(sign * d["tacticalDiameter_m"], y_lab, f"trial tactical dia. {d['tacticalDiameter_m']:.0f} m", color=C_TEXT2, fontsize=8, rotation=90, va="bottom", ha="right" if sign > 0 else "left")
        for hd, mk in r.marks.items():
            ax.plot(mk["across_m"] * sign, mk["along_m"], "o", color=C_SIM, ms=4)
            ax.annotate(f"{hd}° {mk['t_s']:.0f}s {mk['speed_kn']:.1f}kn", (mk["across_m"] * sign, mk["along_m"]), textcoords="offset points", xytext=(6, 4), fontsize=7, color=C_TEXT2)
        ax.set_aspect("equal")
        ax.set_xlabel("Across (m, starboard +)")
        ax.set_ylabel("Along initial heading (m)")
        ax.set_title(f"{ship_id} 35° turn {side}: TD {r.tactical_diameter_m:.0f} m, adv {r.advance_m:.0f} m, Vs {r.steady_speed_kn:.1f} kn", fontsize=9)
        ax.legend(loc="lower right", fontsize=8)
    fig.tight_layout()
    p = out / "turning.png"
    fig.savefig(p)
    plt.close(fig)
    return p


def _plot_zigzag(results: dict[str, Any], tt: dict[str, Any], out: Path, ship_id: str) -> Path | None:
    keys = [k for k in ("zigzag10_10_portFirst", "zigzag10_10_starboardFirst", "zigzag20_20_portFirst", "zigzag20_20_starboardFirst") if k in results]
    if not keys:
        return None
    _style()
    fig, axes = plt.subplots(len(keys), 1, figsize=(9, 2.6 * len(keys)), squeeze=False, sharex=True)
    for ax, k in zip(axes[:, 0], keys):
        z = results[k]
        ts = [h.t for h in z.history]
        psi0 = z.history[0].heading_deg if z.history else 0.0
        hd = [((h.heading_deg - psi0 + 180) % 360) - 180 for h in z.history]
        ax.plot(ts, hd, color=C_SIM, label="Heading change")
        ax.plot(ts, [h.rudder_deg for h in z.history], color=C_THIRD, lw=1.4, label="Rudder")
        trial = (tt.get("identification", {}).get(k[:11]) or tt.get("validation", {}).get(k[:11]) or {}).get(k[12:], {}).get("overshoots_deg", [])
        ax.set_title(f"{ship_id} {k}: overshoots sim {[round(o, 1) for o in z.overshoots_deg]}°, trial {trial}°", fontsize=9)
        ax.set_ylabel("deg")
        ax.legend(loc="upper right", fontsize=8, ncol=2)
    axes[-1, 0].set_xlabel("Time (s)")
    fig.tight_layout()
    p = out / "zigzag.png"
    fig.savefig(p)
    plt.close(fig)
    return p


def _plot_stopping(results: dict[str, Any], tt: dict[str, Any], out: Path, ship_id: str) -> Path | None:
    keys = [k for k in ("crashStop", "inertiaStop", "crashAhead") if k in results]
    if not keys:
        return None
    _style()
    fig, axes = plt.subplots(1, 2, figsize=(11, 4.2))
    val = tt.get("validation", {})
    for k in keys:
        r = results[k]
        ts = [h.t for h in r.history]
        axes[0].plot(ts, [h.speed_kn for h in r.history], label=f"{k} (sim)", color={"crashStop": C_SIM, "inertiaStop": C_THIRD, "crashAhead": "#4a3aa7"}[k])
        axes[1].plot(ts, [h.rpm for h in r.history], color={"crashStop": C_SIM, "inertiaStop": C_THIRD, "crashAhead": "#4a3aa7"}[k], label=f"{k} rpm")
    cs = val.get("crashStop", {})
    if "stopTime_s" in cs:
        axes[0].plot([cs["stopTime_s"]], [0.0], "o", color=C_TRIAL, ms=7, label=f"trial crash stop {cs['stopTime_s']} s")
    ins = val.get("inertiaStop", {})
    if "timeTo5kn_s" in ins:
        axes[0].plot([ins["timeTo5kn_s"]], [5.0], "s", color=C_TRIAL, ms=7, label=f"trial 5 kn at {ins['timeTo5kn_s']} s")
    for pt in ins.get("fromFullSea", []):
        axes[0].plot([pt["t_min"] * 60.0], [pt["speed_kn"]], "s", color=C_TRIAL, ms=4)
    axes[0].set_xlabel("Time (s)")
    axes[0].set_ylabel("Speed (kn)")
    axes[0].set_title(f"{ship_id} stopping: speed", fontsize=9)
    axes[0].legend(fontsize=8)
    axes[1].set_xlabel("Time (s)")
    axes[1].set_ylabel("Shaft rpm")
    axes[1].set_title("Shaft speed", fontsize=9)
    axes[1].legend(fontsize=8)
    fig.tight_layout()
    p = out / "stopping.png"
    fig.savefig(p)
    plt.close(fig)
    return p


def _plot_speed_rpm(ship: MMGShip, tt: dict[str, Any], out: Path, ship_id: str) -> Path:
    from .manoeuvres import speed_rpm_table

    _style()
    rpms = [float(x) for x in range(40, int(ship.n_max * 60) + 10, 10)]
    tbl = speed_rpm_table(ship, rpms)
    fig, ax = plt.subplots(figsize=(6.5, 4))
    ax.plot([r["rpm"] for r in tbl], [r["speed_kn"] for r in tbl], color=C_SIM, label="Simulation (steady)")
    st = ship.sp.speed_trial
    if st:
        ax.plot([p["rpm"] for p in st], [p["speed_kn"] for p in st], "o", color=C_TRIAL, ms=7, label="Speed trial")
    tele = ship.sp.engine["telegraph"]
    xs, ys = [], []
    for k, v in tele.items():
        sk = "speedLoaded_kn" if ship.sp.loading.name == "full" else "speedBallast_kn"
        if v.get(sk):
            xs.append(v["rpm"])
            ys.append(v[sk])
    if xs:
        ax.plot(xs, ys, "^", color=C_THIRD, ms=6, label="Poster telegraph table")
    ax.set_xlabel("Shaft rpm")
    ax.set_ylabel("Speed (kn)")
    ax.set_title(f"{ship_id} speed vs rpm ({ship.sp.loading.name})", fontsize=9)
    ax.legend(fontsize=8)
    fig.tight_layout()
    p = out / "speed_rpm.png"
    fig.savefig(p)
    plt.close(fig)
    return p


# ---------------------------------------------------------------------------
# 報告
# ---------------------------------------------------------------------------
def validate(ship_id: str, loading: str = "full", dt: float = 0.02, out_dir: Path | None = None,
             coeffs: dict[str, Any] | None = None, make_plots: bool = True, verbose: bool = True) -> dict[str, Any]:
    sp = load_particulars(ship_id, loading)
    tt = load_trial_targets(ship_id)
    if coeffs is None:
        coeffs = load_or_estimate(sp)
    ship = MMGShip(sp, coeffs, dt=dt)
    results = run_all(ship, tt)
    rows = compare(results, tt, sp.lpp)
    out = out_dir or paths.calibration_out_dir(ship_id)
    out.mkdir(parents=True, exist_ok=True)
    figs: list[Path] = []
    if make_plots:
        for fn in (_plot_turning, _plot_zigzag, _plot_stopping):
            p = fn(results, tt, out, ship_id)
            if p:
                figs.append(p)
        figs.append(_plot_speed_rpm(ship, tt, out, ship_id))
    formal = [r for r in rows if r.formal and r.within is not None]
    n_ok = sum(1 for r in formal if r.within)
    md = render_markdown(ship_id, loading, sp, tt, coeffs, rows, figs, dt, n_ok, len(formal))
    md_path = out / f"validation.{loading}.md"
    md_path.write_text(md, encoding="utf-8")
    summary = {
        "shipId": ship_id, "loading": loading, "dt": dt, "coefficientsMethod": coeffs.get("source", {}).get("method"),
        "rows": [r.as_dict() for r in rows], "formalWithinTolerance": f"{n_ok}/{len(formal)}",
        "report": str(md_path), "figures": [str(f) for f in figs],
    }
    (out / f"validation.{loading}.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    if verbose:
        print(render_table(rows))
        print(f"正式驗收項目在公差內:{n_ok}/{len(formal)};報告:{md_path}")
    return summary


def render_table(rows: list[Row]) -> str:
    lines = ["| 項目 | 試俥/海報 | 模擬 | 偏差 | 公差 | 判定 |", "|---|---:|---:|---:|---:|:---:|"]
    cur = None
    for r in rows:
        if r.section != cur:
            cur = r.section
            lines.append(f"| **{cur}** | | | | | |")
        dev = r.deviation
        dev_s = "—" if dev is None else (f"{dev:+.1f} {r.unit}" + (f" ({r.deviation_pct:+.1f}%)" if r.deviation_pct is not None else ""))
        tol_s = "—" if r.tol is None else f"±{r.tol:.1f} {r.unit}"
        if r.within is None:
            verdict = "參考" if not r.formal else "—"
        else:
            verdict = ("✔" if r.within else "✘") + ("" if r.formal else " (參考)")
        lines.append(f"| {r.item} | {_fmt(r.trial)} {r.unit if r.trial is not None else ''} | {_fmt(r.sim)} {r.unit} | {dev_s} | {tol_s} | {verdict} |")
    return "\n".join(lines)


def render_markdown(ship_id: str, loading: str, sp, tt: dict[str, Any], coeffs: dict[str, Any], rows: list[Row],
                    figs: list[Path], dt: float, n_ok: int, n_formal: int) -> str:
    src = coeffs.get("source", {})
    ident = src.get("identification", {})
    lines = [
        f"# {ship_id} {sp.name} 船模驗證報告({loading})",
        "",
        f"- 產生日期:{_dt.date.today().isoformat()};工具 simosa-brm {__version__};步長 {dt} s(RK4)",
        f"- 係數檔:`data/ships/{ship_id}/coefficients.{loading}.json`(方法:{src.get('method')};資料等級:{src.get('dataGrade')};識別日期:{ident.get('date', src.get('generatedAt', coeffs.get('generatedAt')))})",
        f"- 目標來源:{tt.get('source', '')}",
        f"- 公差:{json.dumps(tt.get('tolerances', {}), ensure_ascii=False)}(規劃書 6.4)",
        f"- 正式驗收項目在公差內:**{n_ok}/{n_formal}**",
        "",
        "## 模擬 vs 目標",
        "",
        render_table(rows),
        "",
    ]
    if ident:
        lines += ["## 識別參數(初值 → 識別值)", "", "| 參數 | 經驗初值 | 識別值 | 範圍 |", "|---|---:|---:|---|"]
        for n, p in ident.get("parameters", {}).items():
            lines.append(f"| {n} | {p['initial']:.4f} | {p['final']:.4f} | [{p['lower']}, {p['upper']}] |")
        opt = ident.get("optimizer", {})
        lines += ["", f"- 最佳化:{opt.get('method')},函式評估 {opt.get('evaluations')} 次,狀態 {opt.get('status')}({opt.get('message')});"
                  f"識別成本 {ident.get('cost', {}).get('initial', 0):.3f} → {ident.get('cost', {}).get('final', 0):.3f};識別目標在公差內 {ident.get('withinTolerance')}", ""]
    notes = src.get("notes", [])
    if notes:
        lines += ["## 以經驗式補足的資料", ""] + [f"- {n}" for n in notes] + [""]
    bad = [r for r in rows if r.within is False]
    lines += ["## 已知偏差與可能原因", ""]
    if not bad:
        lines.append("- 全部比對項目在公差內。")
    for r in bad:
        reason = r.note or _default_reason(r)
        lines.append(f"- {r.section} / {r.item}:模擬 {_fmt(r.sim)} vs 目標 {_fmt(r.trial)} {r.unit}(偏差 {r.deviation_pct:+.1f}%,公差 ±{r.tol:.1f})。{reason}")
    lines += ["", "## 圖", ""] + [f"![{f.stem}]({f.name})" for f in figs] + [""]
    lines += ["## 模型與限制(摘要)", "",
              "- MMG 3 自由度(Yasukawa & Yoshimura 2015),線性導數 Kijima 1990,非線性導數以 KVLCC2 基準值為起點再識別;附加質量 Zhou 經驗式。",
              "- 螺槳 Wageningen B 系列多項式;倒車/風車/鎖定為簡化四象限;Schilling 舵為分段升力曲線(無實測升力曲線)。",
              "- 主機:一階滯後 + 速率限制 + 換向延遲;慣性停船為直線滑行(試俥航跡彎曲者無法重現)。",
              "- 風:Blendermann 參數式(油輪典型係數)、受風面積為估計;淺水:Kijima 型倍率(趨勢用);詳見 src/Tools.Calibration/README.md。", ""]
    return "\n".join(lines)


def _default_reason(r: Row) -> str:
    if "停船" in r.section or "倒車" in r.section:
        return "可能原因:簡化四象限螺槳(無 Wageningen C_T*/C_Q* 表)、風車/壓縮制動未建模、低速阻力係數由試俥速度曲線外推。"
    if "Z 形" in r.section:
        return "可能原因:線性導數與舵交互作用經驗式誤差(±20–30%)、Schilling 舵升力曲線假設、主機轉速保持假設。"
    if "迴旋" in r.section:
        return "可能原因:Schilling 舵 35° 升力/滑流旋轉不對稱模型、非線性導數以 KVLCC2 代用。"
    if "側推" in r.section:
        return "可能原因:零速/低速艏搖阻尼以橫流阻力近似、側推與船體交互作用。"
    return "可能原因:資料為海報讀值或經驗式估計。"
