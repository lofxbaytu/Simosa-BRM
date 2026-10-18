"""外力一致性黃金檔(tests/SimCore.Tests/Golden/ext_*.csv)的完整性與涵蓋範圍(審查發現:黃金比對未觸發碼頭硬接觸、
繫纜斷裂、起錨與絞盤路徑,且有全程為 0 的比對欄;Python 端重現測試未比對標頭事件與末狀態雜湊)。

- 每個案例:YAML 與匯出腳本一致、數值列相對 1e-9、``# event`` 標頭序列相同;數值列逐位元相同時末狀態雜湊也須相同
  (其他平台的 libm 末位差只放寬數值列,不放寬事件)。
- 每個 ``items`` 欄位至少有一個非零值(全程為 0 的欄位沒有比對保護)。
- 全部黃金檔合起來涵蓋 C# ExternalForceGoldenTests 比對的每一種外力事件,並走過懸鏈線 suspended / taut 區與倒俥沿岸。
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

from simosa_brm import external as ex
from simosa_brm.paths import repo_root

GOLDEN = repo_root() / "tests" / "SimCore.Tests" / "Golden"

# C# ExternalForceGoldenTests.ExternalEventKinds(比對的外力事件種類)
EXTERNAL_EVENT_KINDS = {
    "tugCommand", "tugLineParted", "anchorLetGo", "anchorCommand", "anchorDragging", "anchorHolding", "anchorBrakeSlipping",
    "anchorBroughtUp", "anchorAweigh", "anchorHoused", "anchorChainFullyOut", "anchorWindlassStalled", "fenderContact", "quayContact",
    "mooringCommand", "mooringLineParted",
}


def _export_module():
    spec = importlib.util.spec_from_file_location("export_external_reference", GOLDEN / "export_external_reference.py")
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


MOD = _export_module()
CASE_NAMES = [c["name"] for c in MOD.CASES]


def _read(path: Path) -> dict:
    cols: list[str] | None = None
    rows: list[str] = []
    events: list[str] = []
    h = ""
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("#"):
            if line.startswith("# state_hash_end:"):
                h = line.split(":", 1)[1].strip()
            elif line.startswith("# event: "):
                events.append(line[len("# event: "):])
            continue
        if cols is None:
            cols = line.split(",")
        else:
            rows.append(line)
    assert cols is not None
    return {"cols": cols, "rows": rows, "events": events, "hash": h}


def test_every_golden_csv_has_an_export_case() -> None:
    files = sorted(p.stem for p in GOLDEN.glob("ext_*.csv"))
    assert files == sorted(CASE_NAMES)


@pytest.mark.parametrize("name", CASE_NAMES)
def test_golden_external_events_and_hash_are_reproducible(name: str) -> None:
    case = next(c for c in MOD.CASES if c["name"] == name)
    expected_yaml = f"# 由 export_external_reference.py 產生(請勿手改):{case['title']}\nid: {case['name']}\n" + case["yaml"]
    assert (GOLDEN / f"{name}.yaml").read_text(encoding="utf-8") == expected_yaml
    g = _read(GOLDEN / f"{name}.csv")
    res = MOD.run_case(case, write=False)
    assert len(res["rows"]) == len(g["rows"])
    exp = [[float(v) for v in r.split(",")] for r in g["rows"]]
    got = [[float(v) for v in r.split(",")] for r in res["rows"]]
    for j, c in enumerate(g["cols"]):
        sc = max(1.0, max(abs(r[j]) for r in exp))
        worst = max(abs(a[j] - b[j]) for a, b in zip(got, exp))
        assert worst <= 1e-9 * sc, f"{name} {c}: 最大差 {worst:.3e}"
    got_events = [f"tick={ev['tick']} {ev['kind']} {ev['itemId']}" for ev in res["events"]]
    assert got_events == g["events"], f"{name}:外力事件序列與黃金檔標頭不同"
    if res["rows"] == g["rows"]:
        assert res["hash"] == g["hash"], f"{name}:數值列逐位元相同但末狀態雜湊不同(雜湊欄位或順序改變)"


@pytest.mark.parametrize("name", CASE_NAMES)
def test_golden_item_columns_are_not_identically_zero(name: str) -> None:
    case = next(c for c in MOD.CASES if c["name"] == name)
    g = _read(GOLDEN / f"{name}.csv")
    assert g["cols"][12:] == case["items"]
    rows = [[float(v) for v in r.split(",")] for r in g["rows"]]
    for j, c in enumerate(g["cols"]):
        if j < 12:  # 共同欄(轉速、外力合計 X 等)可依案例為 0;只要求案例自選的項目欄
            continue
        assert max(abs(r[j]) for r in rows) > 0.0, f"{name} {c}:全程為 0,沒有比對保護"


def test_golden_files_cover_all_external_event_kinds() -> None:
    kinds: set[str] = set()
    for name in CASE_NAMES:
        kinds |= {e.split(" ")[1] for e in _read(GOLDEN / f"{name}.csv")["events"]}
    assert EXTERNAL_EVENT_KINDS - kinds == set(), f"黃金檔未涵蓋:{sorted(EXTERNAL_EVENT_KINDS - kinds)}"


def test_golden_cases_cover_catenary_regimes_astern_bank_and_tug_connect(monkeypatch: pytest.MonkeyPatch) -> None:
    """起錨案例走過懸鏈線 suspended 與 taut 區;倒俥沿岸案例 u 由正轉負且岸壁力非零;拖船由 free 帶纜(connecting)。"""
    regimes: set[str] = set()
    orig = ex.AnchorUnit.post_step

    def post_step(self, *a, **k):
        orig(self, *a, **k)
        regimes.add(self.last.regime)

    monkeypatch.setattr(ex.AnchorUnit, "post_step", post_step)
    MOD.run_case(next(c for c in MOD.CASES if c["name"] == "ext_anchor_heave_house"), write=False)
    assert {"suspended", "taut", "touchdown", "slack", "hanging"} <= regimes

    g = _read(GOLDEN / "ext_bank_astern.csv")
    iu, iy = g["cols"].index("u"), g["cols"].index("bank:Y_kN")
    rows = [[float(v) for v in r.split(",")] for r in g["rows"]]
    assert max(r[iu] for r in rows) > 1.0 and min(r[iu] for r in rows) < -0.5
    assert any(r[iu] < 0 and abs(r[iy]) > 1.0 for r in rows), "倒俥時岸壁力為 0"

    case = next(c for c in MOD.CASES if c["name"] == "ext_quay_line_part")
    res = MOD.run_case(case, write=False)
    connects = [ev for ev in res["events"] if ev["kind"] == "tugCommand" and ev["data"]["from"] == "free"]
    assert connects and connects[0]["data"]["status"] == "connecting"
    stern = next(ln for ln in res["ship"].external.berth.lines if ln.id == "STERN")
    assert stern.rest_length == pytest.approx(stern.max_length) and stern.status == "fast"
