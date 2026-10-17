"""FSB2 壓載係數檔(data/ships/FSB2/coefficients.ballast.json;估計、未識別)的合理性測試。

No.2 壓載沒有操縱資料(海報只有車鐘壓載航速),係數由經驗公式估計並轉移滿載識別修正(``estimate --transfer-from full``)。
本檔檢查:檔案為工具產生且標註為估計;航速-轉速重現海報壓載航速;與同船滿載的標準操縱趨勢一致
(壓載迴旋圈較小、停船較短、受風較大;規劃書第 3 章),並與 FSB1 壓載/滿載的趨勢方向相同。
"""

from __future__ import annotations

import json
import math

import pytest

from simosa_brm import paths
from simosa_brm.coefficients import estimate_coefficients, load_coefficients
from simosa_brm.identify import PARAM_SPECS, _get, transfer_identification
from simosa_brm.manoeuvres import LOADING_TRENDS, loading_trend_metrics, loading_trends, speed_rpm_table
from simosa_brm.mmg import MMGShip, make_ship
from simosa_brm.particulars import load_particulars, load_trial_targets

TREND_DT = 0.1  # 趨勢比較用步長(與 0.02 s 的差異遠小於裝載間差異)


@pytest.fixture(scope="module")
def ballast_coeffs() -> dict:
    p = paths.coefficients_path("FSB2", "ballast")
    assert p.exists(), f"缺少 {p}(simosa-brm estimate --ship FSB2 --loading ballast --transfer-from full --out {p})"
    return load_coefficients("FSB2", "ballast", p)


def test_ballast_file_is_marked_estimated(ballast_coeffs) -> None:
    c = ballast_coeffs
    assert c["shipId"] == "FSB2" and c["loading"] == "ballast"
    src = c["source"]
    assert src["method"] == "estimate" and src["identifiedParameters"] == []
    assert src["dataGrade"] == "poster"
    assert any("未識別" in n for n in src["notes"]), "notes 須註明未識別(estimated)"
    assert any("speedBallast_kn" in n for n in src["notes"]), "notes 須註明阻力/推進的資料來源(海報壓載航速)"
    xfer = src["identificationTransfer"]
    assert xfer["from"] == "data/ships/FSB2/coefficients.full.json" and xfer["fromLoading"] == "full"
    full = load_coefficients("FSB2", "full")
    assert set(xfer["parameters"]) == set(full["source"]["identifiedParameters"])
    # 參考尺寸:particulars 壓載吃水(艏艉平均)、依吃水比估計的 Cb 與排水量
    sp = load_particulars("FSB2", "ballast")
    ref = c["reference"]
    assert ref["draft_m"] == pytest.approx(5.45) and ref["draftFore_m"] == 4.4 and ref["draftAft_m"] == 6.5
    assert ref["blockCoefficient"] == pytest.approx(sp.cb) and ref["displacement_t"] == pytest.approx(sp.loading.displacement_t)
    assert ref["displacement_t"] < full["reference"]["displacement_t"]


def test_ballast_file_matches_tool_output(ballast_coeffs) -> None:
    """係數檔 = 目前工具的經驗估計 + 滿載識別修正轉移(滿載重新識別或經驗式改變後須重新產生壓載檔)。"""
    sp = load_particulars("FSB2", "ballast")
    est = estimate_coefficients(sp, load_trial_targets("FSB2"))
    regen = transfer_identification(est, load_coefficients("FSB2", "full"))
    msg = "data/ships/FSB2/coefficients.ballast.json 與工具輸出不一致,請以 README 的 estimate --transfer-from full 指令重新產生"
    for section in ("mass", "propeller", "rudder", "engine", "thruster", "wind", "squat", "reference"):
        assert json.dumps(regen[section], sort_keys=True) == json.dumps(ballast_coeffs[section], sort_keys=True), f"{section}:{msg}"
    for key in ("Xvv", "Xvr", "Xrr", "Xvvvv", "Yv", "Yr", "Yvvv", "Yvvr", "Yvrr", "Yrrr", "Nv", "Nr", "Nvvv", "Nvvr", "Nvrr", "Nrrr"):
        assert regen["hull"][key] == pytest.approx(ballast_coeffs["hull"][key], rel=1e-12), f"hull.{key}:{msg}"
    r_new, r_old = regen["hull"]["resistance"], ballast_coeffs["hull"]["resistance"]
    assert r_new["viscous"]["formFactor"] == pytest.approx(r_old["viscous"]["formFactor"], rel=1e-6), msg
    assert r_new["wave"]["coefficient"] == pytest.approx(r_old["wave"]["coefficient"], rel=1e-6, abs=1e-12), msg


def test_transfer_identification_rule() -> None:
    """倍率轉移(初值 ≠ 0)、加法轉移(初值 = 0)、夾在 PARAM_SPECS 範圍內;未識別來源原樣回傳。"""
    est = estimate_coefficients(load_particulars("FSB2", "ballast"), load_trial_targets("FSB2"))
    full = load_coefficients("FSB2", "full")
    out = transfer_identification(est, full)
    for name, p in full["source"]["identification"]["parameters"].items():
        path, lo, hi, _ = PARAM_SPECS[name]
        base = _get(est, path)
        expected = base * p["final"] / p["initial"] if p["initial"] else base + p["final"] - p["initial"]
        assert _get(out, path) == pytest.approx(min(max(expected, lo), hi))
    # 未在 identification 的參數不動
    assert out["hull"]["Yv"] == est["hull"]["Yv"] and out["hull"]["Nv"] == est["hull"]["Nv"]
    # 範圍夾限
    fake = {"shipId": "FSB2", "loading": "full", "source": {"identification": {"parameters": {"Nr": {"initial": -0.01, "final": -1.0}}}}}
    assert transfer_identification(est, fake)["hull"]["Nr"] == PARAM_SPECS["Nr"][1]
    # 來源未識別 → 不變
    assert transfer_identification(est, {"source": {"method": "estimate"}}) == est


def test_rudder_area_does_not_scale_with_loading() -> None:
    """舵面積 null 時以滿載吃水估計(舵為固定幾何);滿載的值不變。"""
    full, ballast = load_particulars("FSB2", "full"), load_particulars("FSB2", "ballast")
    assert full.rudder_area == pytest.approx(104.0 * 7.2 / 55.0)
    assert ballast.rudder_area == pytest.approx(full.rudder_area)
    assert any("舵為固定幾何" in n for n in ballast.estimated)
    # 有面積資料的 FSB1 不受影響
    assert load_particulars("FSB1", "ballast").rudder_area == load_particulars("FSB1", "full").rudder_area == 10.27


def test_ballast_speed_matches_poster_telegraph(ballast_coeffs) -> None:
    ship = MMGShip(load_particulars("FSB2", "ballast"), ballast_coeffs, dt=0.02)
    for order in ("NAVF", "FAH", "HAH", "SAH", "DSAH"):
        rpm = ship.sp.telegraph_rpm(order)
        v = ship.sp.telegraph_speed_kn(order)
        sim = speed_rpm_table(ship, [rpm])[0]["speed_kn"]
        assert sim == pytest.approx(v, abs=0.3), f"{order} {rpm} rpm:模擬 {sim:.2f} kn vs 海報壓載 {v} kn"


def test_make_ship_uses_ballast_file(ballast_coeffs) -> None:
    ship = make_ship("FSB2", "ballast", dt=0.1)
    assert ship.c["source"].get("identificationTransfer") is not None
    assert ship.A_R == pytest.approx(ballast_coeffs["rudder"]["area_m2"])


def _trends(ship_id: str) -> list[dict]:
    ref = make_ship(ship_id, "full", dt=TREND_DT)
    other = make_ship(ship_id, "ballast", dt=TREND_DT)
    return loading_trends(loading_trend_metrics(ref), loading_trend_metrics(other))


@pytest.fixture(scope="module")
def fsb2_trends() -> list[dict]:
    return _trends("FSB2")


@pytest.fixture(scope="module")
def fsb1_trends() -> list[dict]:
    return _trends("FSB1")


def test_fsb2_ballast_trends_vs_full(fsb2_trends) -> None:
    """壓載 vs 滿載(各自 NAVF 速度):迴旋圈較小、停船時間/距離較短、受風較大。"""
    for t in fsb2_trends:
        print(f"FSB2 {t['item']}: full {t['reference']:.2f} ballast {t['value']:.2f} ratio {t['ratio']:.2f}")
        assert not math.isnan(t["value"]), t["item"]
        if t["expect"] != 0:
            assert t["ok"], f"{t['item']}:滿載 {t['reference']:.2f} → 壓載 {t['value']:.2f} 不符預期方向"
    by = {t["key"]: t for t in fsb2_trends}
    # 幅度合理:戰術直徑縮小不超過 30%(壓載通常 5–20%),受風漂角至少大 1.3 倍
    for side in ("port", "starboard"):
        assert 0.7 < by[f"turning.{side}.tacticalDiameter_L"]["ratio"] < 1.0
    assert by["wind.lowSpeedDrift_deg"]["ratio"] > 1.3
    # Z 形超越角在合理範圍(海報無資料;10/10 第 1 超越角約 3–15°)
    assert 3.0 < by["zigzag10_10.overshoot1_deg"]["value"] < 15.0


def test_fsb2_trend_directions_match_fsb1(fsb1_trends, fsb2_trends) -> None:
    """兩船壓載相對滿載的趨勢方向一致(有預期方向的項目)。"""
    f1 = {t["key"]: t for t in fsb1_trends}
    for t in fsb2_trends:
        if t["expect"] == 0:
            continue
        assert (f1[t["key"]]["ratio"] < 1.0) == (t["ratio"] < 1.0), f"{t['item']}:FSB1 比值 {f1[t['key']]['ratio']:.2f} vs FSB2 {t['ratio']:.2f}"
    assert [k for k, _, _ in LOADING_TRENDS] == [t["key"] for t in fsb2_trends]
