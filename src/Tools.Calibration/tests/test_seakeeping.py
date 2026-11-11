"""浪一階運動:升沉與縱搖(情境 ``ship.seakeeping``;docs/design/physics-waves.md 第 11 節;C# SeakeepingModelTests 的對應):

- 自然週期與 ASSUMED 預設(A_33 = ∇、C_wp = (1 + 2C_b)/3、GM_L 估計、k_yy/L 0.25、阻尼比 0.35)與情境覆寫;
- RAO / 長度濾波的極限;長波極限(升沉 → 波面、縱搖 → 波斜率);橫浪無縱搖、頂浪/順浪最大;速率為沿航跡的時間微分;
- 與橫搖共用同一波場與相位;Hs 線性;無浪或 Hs = 0 無輸出;
- ``scenario`` 的結構檢查(路徑與訊息同 C# Scenario.StructuralIssues)與正規化;
- MMGShip:未啟用時 ``state_json`` 無 ``waveMotion``、雜湊不變;啟用時 3-DOF 狀態與未啟用逐位元相同(不回饋)、雜湊不同;
  啟用但無浪時雜湊與未啟用相同;練習中變更浪;快照/還原;``reset``;
- 跨語言夾具 tests/SimCore.Tests/Golden/seakeeping_table.csv、seakeeping_run.{yaml,csv}(export_seakeeping_reference.py 產生)可重現。
"""

from __future__ import annotations

import copy
import importlib.util
import math
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.external import Kin
from simosa_brm.particulars import load_particulars
from simosa_brm.roll import GRAVITY, RAD2DEG, RollModel, draw_phases
from simosa_brm.scenario import load_scenario, seakeeping_issues, ship_from_scenario, structural_issues, validate_scenario
from simosa_brm.seakeeping import (
    DEFAULT_HEAVE_DAMPING_RATIO,
    DEFAULT_PITCH_DAMPING_RATIO,
    DEFAULT_PITCH_GYRADIUS_RATIO,
    HASH_MARKER,
    PITCH_FILTER_SERIES_X,
    SeakeepingModel,
    normalise_seakeeping,
    pitch_filter,
    response,
    sinc,
)

GOLDEN = paths.repo_root() / "tests" / "SimCore.Tests" / "Golden"
DEG = math.pi / 180.0
QUARTERING = {"significantHeight_m": 2.5, "peakPeriod_s": 8.0, "direction_deg": 45, "spectrum": "jonswap"}


def _tree(seakeeping: dict[str, Any] | None, speed: float = 0.0, wind: tuple[float, float, float] = (0.0, 0.0, 0.0),
          waves: dict[str, Any] | None = None, depth: float = 1000.0, ship: str = "FSB2", loading: str = "full", seed: int = 7,
          roll: dict[str, Any] | None = None) -> dict[str, Any]:
    t: dict[str, Any] = {
        "id": "T", "seed": seed, "ship": {"id": ship, "loading": loading},
        "origin": {"lat": 23.8, "lon": 120.15},
        "initial": {"position": {"x": 0, "y": 0}, "heading": 0, "speed": speed},
        "environment": {"wind": {"trueSpeed": wind[0], "trueDir": wind[1], "gustiness": wind[2]}, "current": {"set": 0, "drift": 0}, "waterDepth": depth},
    }
    if seakeeping is not None:
        t["ship"]["seakeeping"] = seakeeping
    if roll is not None:
        t["ship"]["roll"] = roll
    if waves is not None:
        t["environment"]["waves"] = waves
    return t


def _ship(tree: dict[str, Any]):
    return ship_from_scenario(validate_scenario(tree), dt=0.02)


def _model(cfg: dict[str, Any] | None = None, seed: int = 7, loading: str = "full", ship: str = "FSB2") -> SeakeepingModel:
    return SeakeepingModel(load_particulars(ship, loading).raw, loading, cfg or {"enabled": True}, seed=seed)


def _amplitudes(m: SeakeepingModel, psi_deg: float, hs: float, u: float = 0.0, seconds: float = 1200.0) -> tuple[float, float]:
    """等速直線航行取樣的最大 |升沉|(m)與 |縱搖|(度)(C# SeakeepingModelTests.Amplitudes 同取樣)。"""
    psi = psi_deg * DEG
    max_h = max_p = 0.0
    for i in range(int(seconds * 5) + 1):
        t = i * 0.2
        r = m.evaluate(Kin(u * math.sin(psi) * t, u * math.cos(psi) * t, psi, u, 0.0, 0.0), 0.0, 0.0, hs, t)
        assert r is not None
        max_h = max(max_h, abs(r[0]))
        max_p = max(max_p, abs(r[1]))
    return max_h, max_p * RAD2DEG


def _run(ship, seconds: float) -> None:
    while ship.state.tick * ship.dt < seconds - 1e-9:
        ship.step()


# ---------------------------------------------------------------------------- 設定與純函數


def test_natural_periods_and_assumed_defaults() -> None:
    fsb2 = _model()
    assert (fsb2.L, fsb2.B, fsb2.T, fsb2.cb) == (104.0, 20.0, 7.2, 0.765)
    assert fsb2.cwp == (1.0 + 2.0 * 0.765) / 3.0
    assert fsb2.volume == pytest.approx(11773.2 * 1000.0 / 1025.0, rel=1e-15)
    assert fsb2.heave_omega == pytest.approx(math.sqrt(GRAVITY * fsb2.waterplane_area / (2.0 * fsb2.volume)), rel=1e-15)
    assert 7.2 < fsb2.heave_natural_period < 7.3 and 5.5 < fsb2.pitch_natural_period < 5.7
    assert not fsb2.gml_from_scenario and 1.0 < fsb2.gml / fsb2.L < 1.2
    assert fsb2.gml == 3.0 * fsb2.cwp * fsb2.cwp * 104.0 * 104.0 / (40.0 * 0.765 * 7.2)
    assert (fsb2.heave_damping_ratio, fsb2.pitch_damping_ratio, fsb2.pitch_gyradius_ratio) == (
        DEFAULT_HEAVE_DAMPING_RATIO, DEFAULT_PITCH_DAMPING_RATIO, DEFAULT_PITCH_GYRADIUS_RATIO) == (0.35, 0.35, 0.25)
    fsb1 = _model(ship="FSB1")
    assert 1.0 < fsb1.gml / fsb1.L < 1.25 and 6.3 < fsb1.heave_natural_period < 6.5 and 4.9 < fsb1.pitch_natural_period < 5.1
    fsb1_ballast = _model(ship="FSB1", loading="ballast")
    assert fsb1_ballast.gml > fsb1.gml and fsb1_ballast.heave_natural_period < fsb1.heave_natural_period
    # FSB2 壓載:C_b 與排水量為估計(particulars._resolve_loading 同式)
    fsb2_ballast = _model(loading="ballast")
    lc = load_particulars("FSB2", "ballast").loading
    assert fsb2_ballast.cb == lc.block_coefficient and fsb2_ballast.volume == pytest.approx(lc.mass_kg / 1025.0, rel=1e-14)
    given = _model({"enabled": True, "heaveDampingRatio": 0.2, "pitchDampingRatio": 0.5, "pitchGyradiusRatio": 0.22, "gmL_m": 95.0})
    assert given.gml_from_scenario and (given.gml, given.pitch_gyradius_ratio, given.heave_damping_ratio, given.pitch_damping_ratio) == (95.0, 0.22, 0.2, 0.5)
    assert given.pitch_omega == pytest.approx(math.sqrt(GRAVITY * 95.0 / (1.2 * (0.22 * 104.0) ** 2)), rel=1e-15)
    assert given.heave_omega == fsb2.heave_omega


def test_response_limits_and_resonance_peak() -> None:
    zeta = 0.35
    assert response(0.0, zeta) == (1.0, 0.0)
    rao1, lag1 = response(1.0, zeta)
    assert rao1 == pytest.approx(1.0 / (2.0 * zeta), rel=1e-12) and lag1 == pytest.approx(math.pi / 2.0, rel=1e-12)
    rao_high, lag_high = response(20.0, zeta)
    assert rao_high < 0.003 and math.pi - 0.05 < lag_high <= math.pi
    r_peak = math.sqrt(1.0 - 2.0 * zeta * zeta)
    peak = response(r_peak, zeta)[0]
    assert peak == pytest.approx(1.0 / (2.0 * zeta * math.sqrt(1.0 - zeta * zeta)), rel=1e-12)
    best = max((i * 0.01 for i in range(1, 301)), key=lambda r: response(r, zeta)[0])
    assert abs(best - r_peak) < 0.011
    assert all(response(r, zeta)[0] < peak for r in (0.2, 0.5, 1.3, 2.0, 3.0))
    assert response(1.0, 0.1)[0] > 3.0 * rao1
    neg, pos = response(-0.7, zeta), response(0.7, zeta)
    assert neg[0] == pos[0] and neg[1] == -pos[1]  # 遭遇頻率為負:RAO 相同、滯後變號


def test_length_filter_limits() -> None:
    assert sinc(0.0) == 1.0 and sinc(1e-9) == 1.0 and abs(sinc(math.pi)) < 1e-15 and sinc(1.3) == sinc(-1.3)
    assert pitch_filter(0.0) == 1.0 and pitch_filter(0.8) == pitch_filter(-0.8)
    assert pitch_filter(PITCH_FILTER_SERIES_X - 1e-9) == pytest.approx(pitch_filter(PITCH_FILTER_SERIES_X + 1e-9), abs=1e-9)
    assert pitch_filter(2.0) == 3.0 * (math.sin(2.0) - 2.0 * math.cos(2.0)) / 8.0
    assert abs(pitch_filter(4.493409457909064)) < 1e-12 and pitch_filter(4.0) > 0.0 > pitch_filter(5.0)


def test_long_waves_heave_follows_elevation_and_pitch_follows_slope() -> None:
    m = _model()
    m.set_waves(120.0, 0.0, "pm", 3.3)
    hs, x, y = 2.0, 30.0, -50.0
    times = [i * 0.5 for i in range(1200)]
    max_eta = max(abs(m.wave_elevation_at(x, y, hs, t)) for t in times)
    max_slope = max(abs(m.wave_slope_along_propagation_at(x, y, hs, t)) for t in times)
    assert 0.5 < max_eta < 2.0 and 1e-4 < max_slope < 2e-3
    for t in times:
        eta = m.wave_elevation_at(x, y, hs, t)
        slope = m.wave_slope_along_propagation_at(x, y, hs, t)
        head = m.evaluate(Kin(x, y, 0.0, 0.0, 0.0, 0.0), 0.0, 0.0, hs, t)
        following = m.evaluate(Kin(x, y, 180.0 * DEG, 0.0, 0.0, 0.0), 0.0, 0.0, hs, t)
        beam = m.evaluate(Kin(x, y, 90.0 * DEG, 0.0, 0.0, 0.0), 0.0, 0.0, hs, t)
        assert abs(head[0] - eta) < 0.08 * max_eta and abs(beam[0] - eta) < 0.08 * max_eta
        assert abs(head[1] + slope) < 0.08 * max_slope and abs(following[1] - slope) < 0.08 * max_slope  # 頂浪艏仰 = 波面朝艏升高
        assert abs(beam[1]) < 1e-12


def test_beam_seas_give_no_pitch_and_head_seas_most() -> None:
    m = _model()
    m.set_waves(14.0, 0.0, "pm", 3.3)
    head, oblique, beam, following = (_amplitudes(m, h, 2.5) for h in (0.0, 45.0, 90.0, 180.0))
    assert beam[1] < 1e-9 and head[1] > oblique[1] > 0.5 and head[1] == pytest.approx(following[1], abs=1e-9) and 0.8 < head[1] < 4.0
    assert beam[0] > oblique[0] > head[0] and 0.8 < beam[0] < 2.5
    m.set_waves(8.0, 0.0, "pm", 3.3)
    head8, beam8 = _amplitudes(m, 0.0, 2.5), _amplitudes(m, 90.0, 2.5)
    assert beam8[1] < 1e-9 and 0.5 < head8[1] < 3.0 and head8[0] < 0.4 * beam8[0]


def test_rates_are_time_derivatives_along_track() -> None:
    m = _model()
    m.set_waves(9.0, 30.0 * DEG, "jonswap", 3.3)
    psi, u, v, cur_e, cur_n, hs, h = 70.0 * DEG, 5.5, -0.3, 0.4, -0.6, 2.5, 1e-4
    vel_e = u * math.sin(psi) + v * math.cos(psi) + cur_e
    vel_n = u * math.cos(psi) - v * math.sin(psi) + cur_n

    def at(t: float) -> tuple[float, float, float, float]:
        r = m.evaluate(Kin(100.0 + vel_e * t, -40.0 + vel_n * t, psi, u, v, 0.0), cur_e, cur_n, hs, t)
        assert r is not None
        return r

    for t in (3.0, 17.7, 123.4, 600.5):
        r = at(t)
        dz = (at(t + h)[0] - at(t - h)[0]) / (2.0 * h)
        dp = (at(t + h)[1] - at(t - h)[1]) / (2.0 * h)
        assert abs(r[2] - dz) < 1e-6 and abs(r[3] - dp) < 1e-7 and r[2] != 0.0 and r[3] != 0.0


def test_wave_field_is_the_same_as_roll_excitation() -> None:
    raw = load_particulars("FSB2", "full").raw
    for seed in (3, 7, 20261111):
        sea = _model(seed=seed)
        roll = RollModel(raw, "full", {"enabled": True}, seed=seed)
        assert sea.phases == draw_phases(seed) == roll.phases
        for tp, dir_deg, spectrum, gamma in ((9.0, 120.0, "jonswap", 3.3), (7.0, 300.0, "pm", 3.3), (12.0, 45.0, "jonswap", 2.0)):
            sea.set_waves(tp, dir_deg * DEG, spectrum, gamma)
            roll.set_waves(tp, dir_deg * DEG, spectrum, gamma)
            assert sea.wave_omega == roll.wave_omega and sea.wave_k == roll.wave_k
            psi = (dir_deg - 90.0) * DEG  # χ = +90°
            for x, y, t in ((0.0, 0.0, 0.0), (120.0, -340.0, 55.5), (-2500.0, 900.0, 1234.56)):
                assert sea.wave_slope_along_propagation_at(x, y, 2.0, t) == pytest.approx(roll.wave_slope_at(x, y, psi, 2.0, t), rel=1e-13, abs=1e-16)
    a, b = _model(seed=7), _model(seed=8)
    for m in (a, b):
        m.set_waves(8.0, 45.0 * DEG, "pm", 3.3)
    still = Kin(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    assert a.evaluate(still, 0.0, 0.0, 2.5, 10.0) != b.evaluate(still, 0.0, 0.0, 2.5, 10.0)


def test_linear_in_hs_and_no_output_without_waves_or_zero_hs() -> None:
    m = _model()
    still = Kin(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    assert not m.has_waves and m.evaluate(still, 0.0, 0.0, 2.0, 5.0) is None and m.wave_elevation_at(0.0, 0.0, 2.0, 5.0) == 0.0
    m.set_waves(8.0, 45.0 * DEG, "pm", 3.3)
    assert m.has_waves
    for hs in (0.0, -1.0, math.nan):
        assert m.evaluate(still, 0.0, 0.0, hs, 5.0) is None
    k = Kin(50.0, 80.0, 20.0 * DEG, 4.0, 0.2, 0.0)
    r1, r2 = m.evaluate(k, 0.0, 0.0, 1.0, 33.3), m.evaluate(k, 0.0, 0.0, 2.0, 33.3)
    assert r1[0] != 0.0 and all(b == pytest.approx(2.0 * a, rel=1e-15) for a, b in zip(r1, r2))
    m.update_last(k, 0.0, 0.0, 2.0, 33.3)
    assert m.last == r2
    st = m.state()
    assert st == {"heave_m": r2[0], "pitch_deg": r2[1] * RAD2DEG, "heaveRate_mps": r2[2], "pitchRate_dps": r2[3] * RAD2DEG}
    hb = m.hash_bytes()
    assert len(hb) == 40 and int.from_bytes(hb[:8], "little") == HASH_MARKER and hb[:8][::-1] == b"SEAKEEP1"
    m.set_waves(None)
    assert m.last is None and m.state() is None


# ---------------------------------------------------------------------------- 情境:結構檢查與正規化


@pytest.mark.parametrize(("seakeeping", "path", "message"), [
    ({"enabled": True, "heaveDampingRatio": 0}, "ship.seakeeping.heaveDampingRatio", "heaveDampingRatio 須在 0–1 之間(不含)"),
    ({"enabled": True, "heaveDampingRatio": 1}, "ship.seakeeping.heaveDampingRatio", "heaveDampingRatio 須在 0–1 之間(不含)"),
    ({"enabled": True, "pitchDampingRatio": -0.1}, "ship.seakeeping.pitchDampingRatio", "pitchDampingRatio 須在 0–1 之間(不含)"),
    ({"enabled": False, "pitchDampingRatio": 1.5}, "ship.seakeeping.pitchDampingRatio", "pitchDampingRatio 須在 0–1 之間(不含)"),
    ({"enabled": True, "pitchGyradiusRatio": 0}, "ship.seakeeping.pitchGyradiusRatio", "pitchGyradiusRatio 須大於 0 且不超過 0.5"),
    ({"enabled": True, "pitchGyradiusRatio": 0.6}, "ship.seakeeping.pitchGyradiusRatio", "pitchGyradiusRatio 須大於 0 且不超過 0.5"),
    ({"enabled": True, "gmL_m": 0}, "ship.seakeeping.gmL_m", "gmL_m 必須為正"),
    ({"enabled": True, "gmL_m": -100}, "ship.seakeeping.gmL_m", "gmL_m 必須為正"),
    ({"enabled": "yes"}, "ship.seakeeping.enabled", "enabled 須為布林值"),
    ("on", "ship.seakeeping", "seakeeping 須為物件"),
])
def test_structural_issues_reject_bad_seakeeping(seakeeping: Any, path: str, message: str) -> None:
    """路徑與訊息同 C# Scenario.StructuralIssues(SeakeepingModelTests.StructuralIssuesRejectBadSeakeeping;後兩例 C# 於反序列化即拒絕)。"""
    issues = structural_issues(_tree(seakeeping))
    assert (path, message) in issues, issues
    found: list[tuple[str, str]] = []
    seakeeping_issues(seakeeping, lambda p, m: found.append((p, m)))
    assert found == [i for i in issues if i[0].startswith("ship.seakeeping")]
    with pytest.raises(ValueError):
        validate_scenario(_tree(seakeeping))


def test_valid_seakeeping_normalises_and_defaults_apply() -> None:
    assert structural_issues(_tree({"enabled": True})) == [] and structural_issues(_tree(None)) == []
    full = {"enabled": True, "heaveDampingRatio": 0.3, "pitchDampingRatio": 0.4, "pitchGyradiusRatio": 0.26, "gmL_m": 110}
    sc = validate_scenario(_tree(full))
    assert sc["ship"]["seakeeping"] == {"enabled": True, "heaveDampingRatio": 0.3, "pitchDampingRatio": 0.4, "pitchGyradiusRatio": 0.26, "gmL_m": 110.0}
    assert validate_scenario(_tree(None))["ship"]["seakeeping"] is None
    assert normalise_seakeeping({"enabled": False}) == {"enabled": False, "heaveDampingRatio": None, "pitchDampingRatio": None, "pitchGyradiusRatio": None, "gmL_m": None}
    assert normalise_seakeeping(None) is None
    ship = _ship(_tree(full, waves=QUARTERING))
    sk = ship.seakeeping
    assert sk is not None and (sk.heave_damping_ratio, sk.pitch_damping_ratio, sk.pitch_gyradius_ratio, sk.gml) == (0.3, 0.4, 0.26, 110.0)
    assert _ship(_tree({"enabled": False, "gmL_m": 90})).seakeeping is None and _ship(_tree(None)).seakeeping is None


# ---------------------------------------------------------------------------- MMGShip


def test_without_seakeeping_state_and_hash_unchanged_and_enabled_does_not_feed_back() -> None:
    kw = {"speed": 10.0, "wind": (20.0, 90.0, 0.3), "waves": QUARTERING}
    plain = _ship(_tree(None, **kw))
    disabled = _ship(_tree({"enabled": False, "heaveDampingRatio": 0.2, "gmL_m": 90}, **kw))
    enabled = _ship(_tree({"enabled": True}, **kw))
    assert plain.seakeeping is None and disabled.seakeeping is None and enabled.seakeeping is not None
    assert "waveMotion" in enabled.state_json() and "waveMotion" not in plain.state_json()  # tick 0 即有輸出
    for s in (plain, disabled, enabled):
        s.set_rudder(15.0)
        for _ in range(500):
            s.step()
    assert plain.state_json() == disabled.state_json() and "waveMotion" not in plain.state_json() and "waves" in plain.state_json()
    assert plain.state_hash() == disabled.state_hash()
    assert enabled.state.as_tuple() == plain.state.as_tuple() and enabled.gust_factor == plain.gust_factor
    assert enabled.state_hash() != plain.state_hash()
    st = enabled.state_json()
    assert set(st["waveMotion"]) == {"heave_m", "pitch_deg", "heaveRate_mps", "pitchRate_dps"}
    assert st["waveMotion"]["heave_m"] != 0.0 and st["waveMotion"]["pitch_deg"] != 0.0
    assert {k: v for k, v in st.items() if k != "waveMotion"} == plain.state_json()
    # 步末結果 = 步末狀態、步末時刻 tick·dt 與目前 Hs 的純函數
    s = enabled.state
    assert enabled.seakeeping.last == enabled.seakeeping.evaluate(Kin(s.x, s.y, s.psi, s.u, s.v, s.r), 0.0, 0.0, enabled.waves.hs, s.tick * enabled.dt)
    # 與橫搖可同時啟用;橫搖啟用與否不影響升沉/縱搖
    both = _ship(_tree({"enabled": True}, roll={"enabled": True}, **kw))
    both.set_rudder(15.0)
    for _ in range(500):
        both.step()
    assert both.state_json()["waveMotion"] == st["waveMotion"] and "roll" in both.state_json()


def test_enabled_without_waves_gives_no_output_and_hash_unchanged() -> None:
    plain, enabled = _ship(_tree(None, speed=8.0)), _ship(_tree({"enabled": True}, speed=8.0))
    _run(plain, 20.0)
    _run(enabled, 20.0)
    assert enabled.seakeeping is not None and not enabled.seakeeping.has_waves and enabled.seakeeping.last is None
    assert "waveMotion" not in enabled.state_json() and enabled.state_json() == plain.state_json()
    assert enabled.state_hash() == plain.state_hash()


def test_magnitudes_are_plausible_for_quartering_seas() -> None:
    ship = _ship(_tree({"enabled": True}, speed=12.0, waves=QUARTERING))
    max_h = max_p = 0.0
    for _ in range(50 * 300):
        ship.step()
        wm = ship.state_json()["waveMotion"]
        max_h, max_p = max(max_h, abs(wm["heave_m"])), max(max_p, abs(wm["pitch_deg"]))
    assert 0.15 < max_h < 1.5 and 0.8 < max_p < 4.0


def test_set_waves_snapshot_restore_and_reset() -> None:
    ship = _ship(_tree({"enabled": True}, speed=8.0))
    _run(ship, 5.0)
    assert "waveMotion" not in ship.state_json()
    h0 = ship.state_hash()
    # 指令加浪:立即有輸出(Python 參考實作無 Hs 漸變),雜湊追加區塊;來向改為橫浪 → 縱搖為零;關閉 → 無輸出
    ship.set_waves({"significantHeight_m": 2.0, "peakPeriod_s": 8.0, "direction_deg": 45})
    assert ship.seakeeping.has_waves and "waveMotion" in ship.state_json() and ship.state_hash() != h0
    _run(ship, 30.0)
    ship.set_waves({"direction_deg": (math.degrees(ship.state.psi) + 90.0) % 360.0})
    assert abs(ship.state_json()["waveMotion"]["pitch_deg"]) < 1e-9 and ship.state_json()["waveMotion"]["heave_m"] != 0.0
    ship.set_waves(None)
    assert not ship.seakeeping.has_waves and "waveMotion" not in ship.state_json()
    ship.step()
    assert ship.seakeeping.last is None
    # 快照/還原(含浪、風、迴轉):續跑與連續執行逐位元相同;舊快照(無 seakeeping 鍵)依設定重建、輸出相同(純函數)
    sc = _tree({"enabled": True}, speed=10.0, wind=(25.0, 60.0, 0.2), waves=QUARTERING)
    s = _ship(sc)
    s.set_rudder(20.0)
    _run(s, 10.0)
    snap = s.snapshot()
    at10 = s.state_json()["waveMotion"]
    _run(s, 40.0)
    h, st = s.state_hash(), s.state_json()
    s.restore(snap)
    assert s.state_json()["waveMotion"] == at10
    _run(s, 40.0)
    assert s.state_hash() == h and s.state_json() == st
    again = _ship(sc)
    again.set_rudder(20.0)
    _run(again, 40.0)
    assert again.state_hash() == h and again.state_json()["waveMotion"] == st["waveMotion"]
    legacy = {k: copy.deepcopy(v) for k, v in snap.items() if k != "seakeeping"}
    s.restore(legacy)
    assert s.seakeeping is not None and s.state_json()["waveMotion"] == at10
    # reset:回到情境初始,輸出與新建相同
    fresh = _ship(sc)
    s.reset(0.0, 0.0, 0.0, 10.0)
    assert s.seakeeping.phases == draw_phases(7) and s.seakeeping.last == fresh.seakeeping.last


# ---------------------------------------------------------------------------- 跨語言夾具


def _export_module():
    spec = importlib.util.spec_from_file_location("export_seakeeping_reference", GOLDEN / "export_seakeeping_reference.py")
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def test_golden_table_is_reproducible() -> None:
    """純函數取樣表(C# SeakeepingModelTests.GoldenTableMatchesPythonReference 以 1e-12 比對)須由目前的 Python 參考實作逐位元重現。"""
    mod = _export_module()
    lines = [l for l in (GOLDEN / "seakeeping_table.csv").read_text(encoding="utf-8").splitlines() if l and not l.startswith("#")]
    assert lines[0].split(",") == mod.TABLE_COLUMNS
    rows = mod.table_rows()
    assert len(rows) == len(lines) - 1 == 420
    for row, line in zip(rows, lines[1:]):
        assert ",".join(mod._fmt(v) for v in row) == line
    # 涵蓋遭遇頻率為負的列(順浪且船速超過短波波速)與橫浪列
    assert any(r[12] > 7.0 and r[6] == 0.0 and r[11] == 180.0 for r in rows) and any(r[6] == 90.0 and r[11] == 0.0 for r in rows)


def test_golden_seakeeping_run_is_reproducible() -> None:
    """積分案例(C# SeakeepingModelTests.GoldenSeakeepingRunMatchesPythonReference 以 1e-9 比對)須由目前的 Python 參考實作重現:
    YAML 一致、數值 1e-12、末狀態雜湊相同。"""
    mod = _export_module()
    case = mod.RUN_CASE
    expected_yaml = f"# 由 export_seakeeping_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    assert (GOLDEN / "seakeeping_run.yaml").read_text(encoding="utf-8") == expected_yaml
    lines = (GOLDEN / "seakeeping_run.csv").read_text(encoding="utf-8").splitlines()
    hash_end = next(l.split(":", 1)[1].strip() for l in lines if l.startswith("# state_hash_end:"))
    body = [l for l in lines if not l.startswith("#")]
    cols = body[0].split(",")
    assert cols == mod.COLUMNS
    exp = [[float(v) for v in l.split(",")] for l in body[1:]]
    res = mod.run_case(case, write=False)
    got = [[float(v) for v in l.split(",")] for l in res["rows"]]
    assert len(got) == len(exp) == 121 and res["hash"] == hash_end
    for j, c in enumerate(cols):
        sc = max(1.0, max(abs(r[j]) for r in exp))
        worst = max(abs(a[j] - b[j]) for a, b in zip(got, exp))
        assert worst <= 1e-12 * sc, f"{c}: 最大差 {worst:.3e}"
    assert 0.1 < max(abs(r[cols.index("heave_m")]) for r in exp) < 1.5
    assert 0.5 < max(abs(r[cols.index("pitch_deg")]) for r in exp) < 4.0
    sc_loaded = load_scenario(GOLDEN / "seakeeping_run.yaml")
    assert sc_loaded["ship"]["seakeeping"]["enabled"] is True and sc_loaded["ship"]["id"] == "FSB2"


def test_e17b_scenario_enables_seakeeping_with_harbour_swell() -> None:
    """E17b(E17 的變體;E17 本體不變):ship.seakeeping 啟用、港內湧浪 Hs 0.5 m / Tp 10 s 自 225°;泊位上有升沉/縱搖輸出。"""
    e17, e17b = load_scenario("E17_typhoon_departure_kaohsiung"), load_scenario("E17b_typhoon_seakeeping")
    assert e17["ship"]["seakeeping"] is None and e17b["ship"]["seakeeping"]["enabled"] is True
    assert e17["ship"]["id"] == e17b["ship"]["id"] and e17["ship"]["loading"] == e17b["ship"]["loading"] and e17["seed"] == e17b["seed"]
    ship = ship_from_scenario(e17b, dt=0.02)
    assert ship.waves is not None and (ship.waves.hs, ship.waves.tp, ship.waves.direction_deg) == (0.5, 10.0, 225.0)
    max_h = max_p = 0.0
    for _ in range(50 * 120):
        ship.step()
        wm = ship.state_json()["waveMotion"]
        max_h, max_p = max(max_h, abs(wm["heave_m"])), max(max_p, abs(wm["pitch_deg"]))
    assert 0.03 < max_h < 0.5 and 0.03 < max_p < 1.0
