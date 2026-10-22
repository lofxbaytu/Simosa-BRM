"""公司程序參數集引用(情境引擎設計第 2 節)與跨語言一致性:

- data/scenarios/*.yaml 逐一以 Python 載入 + 解析參數,與 C# 序列化的情境(紀錄夾具 src/Instruments.Web/tests/fixtures/
  record-*-core.jsonl 第一行 header.scenario / header.parameterSet)比對:已解析欄位的數值(1e-9)、解析路徑集合、鍵集合、版次;
  另把 route / zones / assessment / timeline 子樹的葉值逐一與 C# 比對(Python 的葉值須為 C# 輸出的子集;C# 會補預設欄位)。
- 未解析鍵、單位後綴不符、不允許的欄位、缺參數集、參數集結構錯誤皆為 ValueError;YAML 純量型別推斷與 C# YamlJson 相同。
- 結構檢查(C# Scenario.StructuralIssues 的便宜規則)與伺服器對情境引擎指令回 ok=false。
"""

from __future__ import annotations

import copy
import hashlib
import json
import math
import textwrap
from pathlib import Path
from typing import Any

import pytest

from simosa_brm import paths
from simosa_brm.mmg import make_ship
from simosa_brm.parameters import (
    DEFAULT_PARAMETER_SET_PATH,
    ParameterSet,
    find_parameter_set,
    find_references,
    get_by_path,
    resolve_references,
    unit_of_suffix,
    yaml_to_tree,
)
from simosa_brm.scenario import load_scenario, scenario_from_tree, scenarios_dir, structural_issues, validate_scenario
from simosa_brm.server import SimServer

FIXTURES_DIR = paths.repo_root() / "src" / "Instruments.Web" / "tests" / "fixtures"
TOL = 1e-9

# 夾具錄製後情境 YAML / 參數集又被編輯過的欄位(路徑前綴;情境 id → 路徑集合)。夾具與目前檔案一致時應為空;
# 再度編輯情境/參數集後可暫列於此,並以 dotnet run --project src/SimCore.Host -- --scenario <id> --record … 重新錄製後清空
#(2026-10-17 已重新錄製 E01/E07)。
KNOWN_FIXTURE_DRIFT: dict[str, set[str]] = {}
# 夾具錄製時主控程序以 --timescale 覆寫、或 C# 另行正規化而不比對的欄位
SKIP_LEAVES = {"timeScale"}
# 一定要有 C# 紀錄夾具的情境(缺夾具時失敗而非略過;其餘情境無夾具時略過)。2026-10-17 加入 E05(無參數集的情境)、
# E08(sensors/alerts/bnwas 區段與 bnwas.dormant_s 參數引用)。
REQUIRED_FIXTURES = {
    "E01_baseline",
    "E02_coastal_traffic_strait",
    "E03_restricted_visibility_strait",
    "E04_pilot_boarding_kaohsiung",
    "E05_pilot_traffic",
    "E06_taichung_channel_ebb",
    "E06_taichung_channel_flood",
    "E07_steering_failure",
    "E08_gyro_failure",
    "E09_blackout",
    "E10_engine_failure_lee_shore",
    "E11_man_overboard",
    "E12_anchoring_kaohsiung",
    "E13_tug_berthing_mailiao",
    "E14_unberthing_no_tug",
    "E15_distraction_workload_kaohsiung",
    "E16_night_approach_keelung",
    "E17_typhoon_departure_kaohsiung",
    "E18_ukc_squat_anping",
    "E19_fire_isps_pilotage_kaohsiung",
    "E20_comprehensive_assessment_default",
    "E20_comprehensive_assessment_kaohsiung",
    "E21_cargo_heating_pressure_anping",
    "E22a_handover_conflict",
    "E22b_fatigue_misjudgement",
    "E22c_challenge_senior",
    "E22d_cultural_communication",
    "F01_FSB1_ballast",
    "F01_FSB1_full",
    "F01_FSB2_ballast",
    "F01_FSB2_full",
    "F02_FSB1_low_speed",
    "F02_FSB2_low_speed",
    "F03_FSB1_channel",
    "F03_FSB2_channel",
}  # data/scenarios 下全部情境都必須有核心紀錄夾具(缺少即失敗)
# 感測器/警報/BNWAS 區段(第六輪新增):C# 一律序列化(含預設值);第六輪前錄製的夾具(E01/E05/E07)header 無這些鍵,
# 其 YAML 也沒有這些區段,故只在 header 有該鍵時比對(YAML 有該區段而 header 沒有則為失敗)。
BRIDGE_SECTIONS = ("sensors", "alerts", "bnwas")


def _fixture_header(scenario_id: str) -> dict[str, Any] | None:
    for p in sorted(FIXTURES_DIR.glob("record-*-core.jsonl")) if FIXTURES_DIR.is_dir() else []:
        with open(p, encoding="utf-8") as f:
            header = json.loads(f.readline())
        if header.get("kind") == "header" and header.get("scenario", {}).get("id") == scenario_id:
            return header
    return None


def _leaves(node: Any, path: str = "") -> dict[str, Any]:
    """節點樹 → {路徑: 葉值}(路徑寫法同 C# ParameterRef.path:a.b[2].c)。"""
    out: dict[str, Any] = {}
    if isinstance(node, dict):
        for k, v in node.items():
            out.update(_leaves(v, k if not path else f"{path}.{k}"))
    elif isinstance(node, list):
        for i, v in enumerate(node):
            out.update(_leaves(v, f"{path}[{i}]"))
    else:
        out[path] = node
    return out


def _same(a: Any, b: Any) -> bool:
    if isinstance(a, bool) or isinstance(b, bool):
        return a is b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return math.isclose(a, b, rel_tol=0.0, abs_tol=TOL)
    return a == b


def _drifted(path: str, drift: set[str]) -> bool:
    return any(path == d or path.startswith(d + ".") or path.startswith(d + "[") for d in drift)


def _scenario_files() -> list[Path]:
    return sorted(scenarios_dir().glob("*.yaml"))


# ---------------------------------------------------------------- 參數集


def test_parameter_set_loads_with_sha256_and_suffix_rules() -> None:
    path = paths.repo_root() / DEFAULT_PARAMETER_SET_PATH
    pset = ParameterSet.load(path)
    assert pset.id == "simosa-company-procedures" and pset.version.count(".") == 2
    assert pset.path == DEFAULT_PARAMETER_SET_PATH  # 相對專案根目錄、'/' 分隔(C# ToProjectRelative,D34)
    assert pset.sha256 == hashlib.sha256(path.read_bytes()).hexdigest().upper()
    assert len(pset.parameters) >= 20 and "navigation.cpa.openSea_nm" in pset.parameters
    for key, p in pset.parameters.items():
        assert p["source"] in pset.sources
        suffix = unit_of_suffix(key)
        assert suffix == p["unit"] or (suffix is None and p["unit"] in ("score", "count", "bool", "text")), key
    info = pset.info([])
    assert set(info) == {"id", "version", "path", "sha256", "resolved"}
    assert find_parameter_set(None) == path.resolve()
    assert find_parameter_set("data/parameters/company-procedures.yaml") == path.resolve()
    assert find_parameter_set("nowhere/params.yaml") is None
    with pytest.raises(FileNotFoundError):
        ParameterSet.load(path.parent / "missing.yaml")


def _pset_yaml(**overrides: str) -> str:
    base = {
        "id": "id: test-set",
        "version": 'version: "2026.10.1"',
        "sources": "sources:\n  DOC-1: { title: 文件一, revision: \"1\" }",
        "parameters": "parameters:\n  a.b.min_nm: { value: 0.5, unit: nm, source: DOC-1, clause: \"1\" }",
    }
    base.update(overrides)
    return "\n".join(base.values()) + "\n"


@pytest.mark.parametrize(
    ("override", "message"),
    [
        ({"version": 'version: "v1"'}, "version 格式"),
        ({"id": "id: ''"}, "缺少 id"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 0.5, unit: furlong, source: DOC-1 }"}, "unit 未知"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 0.5, unit: nm, source: DOC-9 }"}, "未在 sources 中定義"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 0.5, unit: m, source: DOC-1 }"}, "單位後綴"),
        ({"parameters": "parameters:\n  a.b.min: { value: 0.5, unit: nm, source: DOC-1 }"}, "缺少單位後綴"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 0.5, unit: nm, source: DOC-1, min: 1.0 }"}, "低於 min"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 5, unit: nm, source: DOC-1, max: 1.0 }"}, "高於 max"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: [1], unit: nm, source: DOC-1 }"}, "須為數值、布林或字串"),
        ({"sources": "sources:\n  DOC-1: { title: 文件一 }"}, "缺少 revision"),
    ],
)
def test_parameter_set_validation_errors(override: dict[str, str], message: str) -> None:
    with pytest.raises(ValueError, match=message):
        ParameterSet.from_yaml(_pset_yaml(**override))


@pytest.mark.parametrize(
    ("override", "message"),
    [
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 3, unit: nm, source: DOC-1, min: \"5\" }"}, "低於 min"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 3, unit: nm, source: DOC-1, min: five }"}, "min 須為數值"),
        ({"parameters": "parameters:\n  a.b.min_nm: { value: 3, unit: nm, source: DOC-1, max: true }"}, "max 須為數值"),
        ({"sources": "sources:\n  DOC-1: { title: 文件一, revision: \"1\", external: \"no\" }"}, "external 須為布林"),
        ({"sources": "sources:\n  DOC-1: { title: 文件一, revision: \"1\", external: 1 }"}, "external 須為布林"),
    ],
)
def test_parameter_set_min_max_external_typing_matches_csharp(override: dict[str, str], message: str) -> None:
    """C# ContractJson:min/max 可自數值字串讀取(仍檢查範圍)、非數值字串為錯誤;external 為嚴格布林。"""
    with pytest.raises(ValueError, match=message):
        ParameterSet.from_yaml(_pset_yaml(**override))


def test_parameter_set_numeric_string_min_max_are_read() -> None:
    pset = ParameterSet.from_yaml(_pset_yaml(
        parameters="parameters:\n  a.b.min_nm: { value: 3, unit: nm, source: DOC-1, min: \"1\", max: \" 5.5 \" }",
        sources="sources:\n  DOC-1: { title: 文件一, revision: \"1\", external: true }",
    ))
    assert pset.get("a.b.min_nm")["min"] == 1.0 and pset.get("a.b.min_nm")["max"] == 5.5
    assert pset.sources["DOC-1"]["external"] is True


def test_parameter_set_unitless_units_need_no_suffix() -> None:
    pset = ParameterSet.from_yaml(_pset_yaml(parameters=textwrap.dedent("""
        parameters:
          assessment.passMark: { value: 70, unit: score, source: DOC-1 }
          ops.allowTugs: { value: true, unit: bool, source: DOC-1 }
          ops.note: { value: "文字", unit: text, source: DOC-1 }
          ops.maxStarts: { value: 12, unit: count, source: DOC-1, min: 1, max: 20 }
    """).strip()))
    assert pset.get("ops.allowTugs")["value"] is True and pset.get("ops.note")["value"] == "文字"
    assert pset.sha256 and len(pset.sha256) == 64 and pset.path is None


# ---------------------------------------------------------------- YAML 純量規則(C# YamlJson)


def test_yaml_to_tree_scalar_rules_match_csharp() -> None:
    tree = yaml_to_tree(textwrap.dedent("""
        date: 2026-10-01
        yes_word: yes
        on_word: on
        hex: 0x1F
        octal: 0o17
        underscore: 1_000
        sexagesimal: 1:30
        inf: .inf
        nan: .nan
        t: true
        f: FALSE
        n1: ~
        n2: null
        n3:
        i: 42
        neg: -7
        fl: 2.5
        exp: 1e3
        dot: 1.
        lead: .5
        quoted_num: "3"
        quoted_bool: "true"
        ref: "${params:a.b_nm}"
        plus: +3
    """))
    assert tree["date"] == "2026-10-01" and tree["yes_word"] == "yes" and tree["on_word"] == "on"
    assert tree["hex"] == "0x1F" and tree["octal"] == "0o17" and tree["underscore"] == "1_000" and tree["sexagesimal"] == "1:30"
    assert tree["inf"] == ".inf" and tree["nan"] == ".nan"
    assert tree["t"] is True and tree["f"] is False
    assert tree["n1"] is None and tree["n2"] is None and tree["n3"] is None
    assert tree["i"] == 42 and isinstance(tree["i"], int) and tree["neg"] == -7 and tree["plus"] == 3
    assert tree["fl"] == 2.5 and tree["exp"] == 1000.0 and isinstance(tree["exp"], float) and tree["dot"] == 1.0 and tree["lead"] == 0.5
    assert tree["quoted_num"] == "3" and tree["quoted_bool"] == "true" and tree["ref"] == "${params:a.b_nm}"
    assert yaml_to_tree("") is None and yaml_to_tree("- 1\n- a\n") == [1, "a"]


def test_yaml_to_tree_leading_zero_ints_and_case_insensitive_bool_null() -> None:
    """C# long.TryParse 為十進位(010 → 10、08 → 8),不是 PyYAML 的八進位;true/false/null 不分大小寫(OrdinalIgnoreCase)。"""
    tree = yaml_to_tree(textwrap.dedent("""
        a: 010
        b: -010
        c: 08
        d: 09
        e: 0042
        f: +007
        g: 0
        t: TRue
        f2: fAlSe
        n: NuLL
        n2: Null
    """))
    assert tree["a"] == 10 and tree["b"] == -10 and tree["c"] == 8 and tree["d"] == 9 and tree["e"] == 42 and tree["f"] == 7 and tree["g"] == 0
    assert all(isinstance(tree[k], int) for k in "abcdefg")
    assert tree["t"] is True and tree["f2"] is False and tree["n"] is None and tree["n2"] is None


# ---------------------------------------------------------------- 引用解析


def _scenario_tree(**assessment: Any) -> dict[str, Any]:
    return {
        "id": "T", "ship": {"id": "FSB1", "loading": "ballast"},
        "initial": {"position": {"lat": 23.8, "lon": 120.05}, "heading": 0},
        "environment": {"waterDepth": 30},
        "assessment": {"minCpa_nm": "${params:navigation.cpa.coastal_nm}", **assessment},
    }


def test_resolve_references_substitutes_and_records() -> None:
    pset = ParameterSet.load(paths.repo_root() / DEFAULT_PARAMETER_SET_PATH)
    tree = _scenario_tree(scoring={"passMark": "${params:assessment.passMark}"},
                          bands=[{"id": "b", "metric": "collisionAvoidance.minCpa_nm", "warn": "${params:navigation.cpa.openSea_nm}"}])
    assert find_references(tree) == [("assessment.minCpa_nm", "navigation.cpa.coastal_nm"),
                                     ("assessment.scoring.passMark", "assessment.passMark"),
                                     ("assessment.bands[0].warn", "navigation.cpa.openSea_nm")]
    resolved = resolve_references(tree, pset)
    assert tree["assessment"]["minCpa_nm"] == 0.5 and tree["assessment"]["scoring"]["passMark"] == 70 and tree["assessment"]["bands"][0]["warn"] == 1.0
    assert [r["path"] for r in resolved] == ["assessment.minCpa_nm", "assessment.scoring.passMark", "assessment.bands[0].warn"]
    assert resolved[0] == {"path": "assessment.minCpa_nm", "key": "navigation.cpa.coastal_nm", "value": 0.5, "unit": "nm",
                           "source": "DWO-007", "revision": "3", "clause": "4.2"}
    assert find_references(tree) == [] and resolve_references(tree, pset) == []
    assert resolve_references({"a": 1}, None) == []  # 無引用時不需要參數集
    sc = validate_scenario(tree, parameter_set=pset.info(resolved))
    assert sc["parameterSet"]["version"] == pset.version and len(sc["parameterSet"]["resolved"]) == 3


def test_unresolved_key_is_error() -> None:
    pset = ParameterSet.load(paths.repo_root() / DEFAULT_PARAMETER_SET_PATH)
    with pytest.raises(ValueError, match=r"無鍵 navigation\.cpa\.nope_nm"):
        resolve_references(_scenario_tree(minCpa_nm="${params:navigation.cpa.nope_nm}"), pset)
    with pytest.raises(ValueError, match="未提供參數集"):
        resolve_references(_scenario_tree(), None)
    with pytest.raises(ValueError, match="未解析"):
        validate_scenario(_scenario_tree())  # 未先解析就檢查


def test_unit_suffix_mismatch_is_error() -> None:
    pset = ParameterSet.load(paths.repo_root() / DEFAULT_PARAMETER_SET_PATH)
    with pytest.raises(ValueError, match=r"單位後綴\(nm\)與參數 navigation\.xtd\.channel_m 的 unit\(m\)不符"):
        resolve_references(_scenario_tree(minCpa_nm="${params:navigation.xtd.channel_m}"), pset)
    with pytest.raises(ValueError, match="單位後綴"):  # 慣用欄位名 metres / fractionOfDraft / passMark 也檢查
        resolve_references(_scenario_tree(ukc={"min": {"metres": "${params:navigation.ukc.channel_fraction}"}}), pset)
    with pytest.raises(ValueError, match="單位後綴"):
        resolve_references(_scenario_tree(scoring={"passMark": "${params:navigation.cpa.openSea_nm}"}), pset)
    # 無後綴的欄位(lessThan、warn、atLeast)不檢查單位
    tree = _scenario_tree(hardFail=[{"id": "c", "rule": "cpa", "lessThan": "${params:navigation.cpa.hardFail_nm}"}])
    resolve_references(tree, pset)
    assert tree["assessment"]["hardFail"][0]["lessThan"] == 0.2


def test_forbidden_fields_cannot_reference_parameters() -> None:
    pset = ParameterSet.load(paths.repo_root() / DEFAULT_PARAMETER_SET_PATH)
    tree = _scenario_tree()
    tree["seed"] = "${params:assessment.passMark}"
    with pytest.raises(ValueError, match="seed 不允許引用參數"):
        resolve_references(tree, pset)
    tree = _scenario_tree()
    tree["assessment"]["parameterSet"] = "${params:assessment.passMark}"
    with pytest.raises(ValueError, match="不允許引用參數"):
        resolve_references(tree, pset)



def _expected_path(p: Path) -> str:
    """ParameterSet.load 的 path 規則:專案內 → 相對專案根目錄('/' 分隔);專案外 → 原路徑。"""
    try:
        return p.resolve().relative_to(paths.repo_root().resolve()).as_posix()
    except ValueError:
        return str(p)

def test_auto_load_parameter_set_by_declared_path(tmp_path: Path) -> None:
    pset_file = tmp_path / "params" / "my-set.yaml"
    pset_file.parent.mkdir()
    pset_file.write_text(_pset_yaml(parameters="parameters:\n  navigation.cpa.coastal_nm: { value: 0.75, unit: nm, source: DOC-1 }"), encoding="utf-8")
    sc_file = tmp_path / "T02.yaml"
    sc_file.write_text(textwrap.dedent("""
        ship: { id: FSB1, loading: full }
        initial: { position: { lat: 23.8, lon: 120.0 }, heading: 10 }
        environment: { waterDepth: 20 }
        assessment:
          parameterSet: params/my-set.yaml
          minCpa_nm: "${params:navigation.cpa.coastal_nm}"
    """), encoding="utf-8")
    sc = load_scenario(sc_file)  # 相對情境檔目錄
    assert sc["id"] == "T02" and sc["assessment"]["minCpa_nm"] == 0.75
    # tmp_path 在專案內(pyproject --basetemp),路徑依 C# ToProjectRelative 規則轉為相對專案根目錄、'/' 分隔
    assert sc["parameterSet"]["id"] == "test-set" and sc["parameterSet"]["path"] == _expected_path(pset_file)
    assert sc["parameterSet"]["resolved"][0]["key"] == "navigation.cpa.coastal_nm"
    # 明確給參數集時優先於宣告路徑
    sc2 = load_scenario(sc_file, parameters=ParameterSet.load(paths.repo_root() / DEFAULT_PARAMETER_SET_PATH))
    assert sc2["assessment"]["minCpa_nm"] == 0.5 and sc2["parameterSet"]["id"] == "simosa-company-procedures"
    # 宣告路徑不存在 → 錯誤並提示 --parameters
    sc_file.write_text(sc_file.read_text(encoding="utf-8").replace("params/my-set.yaml", "nowhere.yaml"), encoding="utf-8")
    with pytest.raises(ValueError, match="--parameters"):
        load_scenario(sc_file)
    # 未宣告 → 預設 data/parameters/company-procedures.yaml(專案根目錄)
    sc_file.write_text(sc_file.read_text(encoding="utf-8").replace("parameterSet: nowhere.yaml\n", ""), encoding="utf-8")
    assert load_scenario(sc_file)["parameterSet"]["id"] == "simosa-company-procedures"
    # 無引用的情境:parameterSet 為 None,也不需要參數集檔案
    sc_file.write_text(sc_file.read_text(encoding="utf-8").replace('"${params:navigation.cpa.coastal_nm}"', "0.3"), encoding="utf-8")
    assert load_scenario(sc_file)["parameterSet"] is None


def test_empty_parameter_set_declaration_is_not_default(tmp_path: Path) -> None:
    """assessment.parameterSet 為空字串/空白:與 C# TryAutoLoadParameterSet 相同視為已宣告但找不到 → 引用參數時錯誤。"""
    assert find_parameter_set("") is None and find_parameter_set("   ") is None
    assert find_parameter_set(None) is not None
    for declared in ('""', '"  "'):
        sc_file = tmp_path / "T03.yaml"
        sc_file.write_text(textwrap.dedent(f"""
            ship: {{ id: FSB1, loading: full }}
            initial: {{ position: {{ lat: 23.8, lon: 120.0 }}, heading: 10 }}
            environment: {{ waterDepth: 20 }}
            assessment:
              parameterSet: {declared}
              minCpa_nm: "${{params:navigation.cpa.openSea_nm}}"
        """), encoding="utf-8")
        with pytest.raises(ValueError, match="未提供參數集"):
            load_scenario(sc_file)


def test_loaded_parameter_set_path_is_project_relative(tmp_path: Path) -> None:
    """紀錄標頭 parameterSet.path:專案內為相對根目錄('/' 分隔);專案外保留原路徑(C# RepositoryPaths.ToProjectRelative)。"""
    assert load_scenario("E07_steering_failure")["parameterSet"]["path"] == "data/parameters/company-procedures.yaml"
    inside = tmp_path / "ps.yaml"   # tmp_path 在專案內(build/test-tmp/pytest):相對專案根目錄
    inside.write_text(_pset_yaml(), encoding="utf-8")
    assert ParameterSet.load(inside).path == _expected_path(inside)
    assert not ParameterSet.load(inside).path.startswith("/") and "\\" not in ParameterSet.load(inside).path
    # 專案外的路徑保留原樣(只比對字串規則,不在專案外寫檔)
    assert _expected_path(Path("/definitely/outside/ps.yaml")) == str(Path("/definitely/outside/ps.yaml"))


# ---------------------------------------------------------------- 跨語言一致性(與 C# 紀錄標頭比對)


@pytest.mark.parametrize("scenario_file", _scenario_files(), ids=lambda p: p.stem)
def test_scenarios_load_and_match_csharp_header(scenario_file: Path) -> None:
    sc = load_scenario(scenario_file)
    assert sc["id"] == scenario_file.stem
    raw = yaml_to_tree(scenario_file.read_text(encoding="utf-8"))
    refs = find_references(raw)
    if not refs:
        assert sc["parameterSet"] is None
    else:
        ps = sc["parameterSet"]
        assert ps is not None and [r["path"] for r in ps["resolved"]] == [p for p, _ in refs]
        assert [r["key"] for r in ps["resolved"]] == [k for _, k in refs]
        for r in ps["resolved"]:  # 代入後的情境在該路徑的值 = 參數值
            assert _same(get_by_path(sc, r["path"]), r["value"]), r

    header = _fixture_header(sc["id"])
    if header is None:
        assert sc["id"] not in REQUIRED_FIXTURES, f"缺少 C# 紀錄夾具 record-*-core.jsonl(header.scenario.id == {sc['id']})"
        pytest.skip(f"無 C# 紀錄夾具(record-*-core.jsonl header.scenario.id == {sc['id']})")
    cs_sc, cs_ps = header["scenario"], header.get("parameterSet")
    drift = KNOWN_FIXTURE_DRIFT.get(sc["id"], set())
    current_sha = hashlib.sha256((paths.repo_root() / DEFAULT_PARAMETER_SET_PATH).read_bytes()).hexdigest().upper()

    if sc["parameterSet"] is None:
        assert "parameterSet" not in header, "無參數引用時 C# header 應省略 parameterSet 鍵"  # E05:鍵省略,非 null/空物件
        assert cs_ps is None
    else:
        ps = sc["parameterSet"]
        assert cs_ps is not None
        assert ps["id"] == cs_ps["id"] and ps["version"] == cs_ps["version"]
        py_by_path = {r["path"]: r for r in ps["resolved"]}
        cs_by_path = {r["path"]: r for r in cs_ps["resolved"]}
        assert set(py_by_path) == set(cs_by_path)  # 解析路徑集合
        mismatched_keys = {p for p in py_by_path if py_by_path[p]["key"] != cs_by_path[p]["key"]}
        unexpected = {p for p in mismatched_keys if not _drifted(p, drift)}
        assert not unexpected, f"鍵不同於夾具且不在 KNOWN_FIXTURE_DRIFT:{unexpected}"
        if mismatched_keys:
            assert cs_ps["sha256"] != current_sha, "參數集未變但夾具鍵名不同:請重新錄製夾具或更新 KNOWN_FIXTURE_DRIFT"
        assert ({r["key"] for r in ps["resolved"] if not _drifted(r["path"], drift)}
                == {r["key"] for r in cs_ps["resolved"] if not _drifted(r["path"], drift)})
        for path, py in py_by_path.items():
            cs = cs_by_path[path]
            assert _same(get_by_path(cs_sc, path), cs["value"]), f"夾具 {path} 的情境值與 resolved 不一致"
            if _drifted(path, drift):
                continue
            assert py["key"] == cs["key"] and py["unit"] == cs["unit"] and py["source"] == cs["source"], path
            assert py["revision"] == cs.get("revision") and py["clause"] == cs.get("clause"), path
            assert _same(py["value"], cs["value"]), f"{path}: Python {py['value']} vs C# {cs['value']}"
            assert _same(get_by_path(sc, path), get_by_path(cs_sc, path)), path

    # route / zones / assessment / timeline / briefing:Python 葉值須為 C# 序列化輸出的子集(C# 會補預設欄位,如 checkpoint、direction、repeat)
    for section in ("route", "zones", "assessment", "timeline", "briefing"):
        py_leaves = _leaves(sc[section], section) if sc[section] is not None else {}
        cs_leaves = _leaves(cs_sc.get(section), section) if cs_sc.get(section) is not None else {}
        for path, value in py_leaves.items():
            if _drifted(path, drift) or path.rsplit(".", 1)[-1] in SKIP_LEAVES:
                continue
            assert path in cs_leaves, f"C# 輸出缺少 {path}"
            assert _same(value, cs_leaves[path]), f"{path}: Python {value!r} vs C# {cs_leaves[path]!r}"

    # sensors / alerts / bnwas:Python 補的預設值與 C# 序列化完全相同(鍵集合相同:null 門檻兩邊都省略)
    for section in BRIDGE_SECTIONS:
        if section not in cs_sc:
            assert section not in raw, f"YAML 有 {section} 區段但 C# 夾具 header 沒有:請重新錄製夾具"
            continue
        py_leaves, cs_leaves = _leaves(sc[section], section), _leaves(cs_sc[section], section)
        assert set(py_leaves) == set(cs_leaves), f"{section}: Python {sorted(py_leaves)} vs C# {sorted(cs_leaves)}"
        for path, value in py_leaves.items():
            assert _same(value, cs_leaves[path]), f"{path}: Python {value!r} vs C# {cs_leaves[path]!r}"


def test_e08_bnwas_dormant_period_comes_from_parameter_set() -> None:
    """E08 的 bnwas.dormant_s 引用 bridge.bnwas.dormantPeriod_s(540 s);sensors/alerts 照寫、未設的 headingMonitor_deg 省略(關閉)。"""
    sc = load_scenario("E08_gyro_failure")
    assert sc["bnwas"] == {"mode": "on", "dormant_s": 540, "stage2_s": 15, "stage3_s": 90}
    assert sc["sensors"] == {"magneticCompassError_deg": 1.0}
    assert sc["alerts"] == {"escalate_s": 300, "silence_s": 30, "offHeading_deg": 10, "offHeadingDelay_s": 10, "shallowWater_m": 5.0}
    ref = next(r for r in sc["parameterSet"]["resolved"] if r["path"] == "bnwas.dormant_s")
    assert ref["key"] == "bridge.bnwas.dormantPeriod_s" and ref["value"] == 540 and ref["unit"] == "s"


# ---------------------------------------------------------------- 結構檢查


def _full_tree() -> dict[str, Any]:
    return {
        "id": "S", "ship": {"id": "FSB1", "loading": "full"},
        "origin": {"lat": 23.8, "lon": 120.0},
        "initial": {"position": {"lat": 23.8, "lon": 120.0}, "heading": 0},
        "environment": {"waterDepth": 20},
        "targets": [{"id": "T1", "initial": {"position": {"x": 0, "y": 1000}, "heading": 180}}],
        "route": {"waypoints": [{"name": "A", "lat": 23.8, "lon": 120.0}, {"name": "B", "lat": 23.81, "lon": 120.01}]},
        "zones": [{"id": "z1", "type": "noGo", "polygon": [{"x": 0, "y": 0}, {"x": 10, "y": 0}, {"x": 0, "y": 10}]}],
        "timeline": [
            {"id": "t1", "when": {"type": "time", "atLeast": 10}, "actions": [{"message": {"to": "instructor", "text": "hi"}}]},
            {"id": "t2", "manual": True, "actions": [{"arm": "t1"}, {"injectFault": "steeringGear"}, {"phase": "degraded"}, {"timeScale": 2}]},
        ],
        "assessment": {
            "hardFail": [{"id": "h1", "rule": "cpa", "lessThan": 0.2}, {"id": "h2", "rule": "zoneEntered", "zoneId": "z1"},
                         {"id": "h3", "rule": "ukc", "lessThan": {"metres": 1}}],
            "bands": [{"id": "b1", "metric": "track.maxXtd_m", "warn": 50, "fail": "2x"}],
            "required": [{"id": "r1", "after": {"event": "faultInjected", "itemId": "t2"}, "expect": {"any": ["rpmOrder", {"acknowledge": {"kind": "notifyEcr"}}]}, "within_s": 60}],
        },
        "briefing": {"text": "x", "roles": [{"role": "pilot"}], "minParticipants": 2},
    }


def test_structural_check_accepts_valid_tree() -> None:
    assert structural_issues(_full_tree()) == []
    sc = validate_scenario(_full_tree())
    assert sc["route"]["waypoints"][1]["name"] == "B" and sc["zones"][0]["id"] == "z1" and len(sc["timeline"]) == 2
    assert sc["assessment"]["bands"][0]["fail"] == "2x" and sc["briefing"]["minParticipants"] == 2 and sc["parameterSet"] is None
    assert validate_scenario({k: v for k, v in _full_tree().items() if k not in ("route", "zones", "timeline", "assessment", "briefing")})["route"] is None


def _mutate(fn: Any) -> dict[str, Any]:
    t = _full_tree()
    fn(t)
    return t


@pytest.mark.parametrize(
    ("mutate", "path", "message"),
    [
        (lambda t: t["timeline"].append({"id": "t1", "actions": []}), "timeline[2]", "id 重複"),
        (lambda t: t["timeline"].append({"actions": []}), "timeline[2]", "缺少 id"),
        (lambda t: t["timeline"][0]["actions"].append({"loadScenario": "E01"}), "timeline[0].actions[1]", "不允許的動作"),
        (lambda t: t["timeline"][0]["actions"].append({"teleport": {}}), "timeline[0].actions[1]", "未知的動作"),
        (lambda t: t["timeline"][0]["actions"].append({"arm": "ghost"}), "timeline[0].actions[1]", "不存在的時間表項目"),
        (lambda t: t["timeline"][0]["actions"].append({"injectFault": ""}), "timeline[0].actions[1]", "缺少故障名稱"),
        (lambda t: t["timeline"][0]["actions"].append({"voiceScript": {"role": "ecr"}}), "timeline[0].actions[1]", "text 或 file"),
        (lambda t: t["timeline"][0]["actions"].append({"timeScale": 0}), "timeline[0].actions[1]", "必須為正數"),
        (lambda t: t["timeline"][0]["actions"].append({"message": {"to": "all"}}), "timeline[0].actions[1]", "message 需要 text"),
        (lambda t: t["timeline"][0].update(when={"type": "warp"}), "timeline[0].when", "未知的條件型別"),
        (lambda t: t["timeline"][0].update(when={"type": "time"}), "timeline[0].when", "需要 atLeast"),
        (lambda t: t["timeline"][0].update(when={"type": "zone", "id": "nope"}), "timeline[0].when", "不存在的區域"),
        (lambda t: t["timeline"][0].update(when={"type": "ownDistance", "waypoint": "Z", "lessThan": 1}), "timeline[0].when", "不存在的航點"),
        (lambda t: t["timeline"][0].update(when={"type": "ownDistance", "target": "T9", "lessThan": 1}), "timeline[0].when", "不存在的目標"),
        (lambda t: t["timeline"][0].update(when={"all": [{"type": "fault"}]}), "timeline[0].when.all[0]", "fault 需要 name"),
        (lambda t: t["timeline"][0].update(when={"type": "time", "atLeast": 1, "all": []}), "timeline[0].when", "不可同時有 type"),
        (lambda t: t["timeline"][0].update(repeat={"every_s": 0}), "timeline[0].repeat", "every_s 必須為正"),
        (lambda t: t["route"]["waypoints"].pop(), "route.waypoints", "至少需要 2 個航點"),
        (lambda t: t["route"]["waypoints"].append({"name": "B", "x": 5, "y": 5}), "route.waypoints[2]", "航點名稱重複"),
        (lambda t: t["route"]["waypoints"].append({"name": "C"}), "route.waypoints[2]", "航點需為"),
        (lambda t: t["route"]["waypoints"].append({"name": "C", "lat": 23.81, "lon": 120.01}), "route.waypoints[2]", "重合"),
        (lambda t: t["route"].update(startLeg=1), "route.startLeg", "超出航段範圍"),
        (lambda t: t["zones"][0]["polygon"].pop(), "zones[0].polygon", "至少需要 3 點"),
        (lambda t: t["zones"][0].update(type="harbour"), "zones[0].type", "區域 type"),
        (lambda t: t["zones"].append({"id": "z1", "type": "noGo", "polygon": [{"x": 0, "y": 0}] * 3}), "zones[1]", "區域 id 重複"),
        (lambda t: t["zones"][0].update(waterDepth_m=0), "zones[0].waterDepth_m", "水深必須為正"),
        (lambda t: t["assessment"]["hardFail"].append({"id": "h1", "rule": "collision"}), "assessment.hardFail[3]", "規則 id 重複"),
        (lambda t: t["assessment"]["hardFail"].append({"id": "h9", "rule": "sink"}), "assessment.hardFail[3]", "未知的 rule"),
        (lambda t: t["assessment"]["hardFail"].append({"id": "h9", "rule": "cpa"}), "assessment.hardFail[3]", "需要 lessThan"),
        (lambda t: t["assessment"]["hardFail"].append({"id": "h9", "rule": "zoneEntered", "zoneId": "z9"}), "assessment.hardFail[3]", "不存在的區域"),
        (lambda t: t["assessment"]["bands"].append({"id": "b9", "metric": "track.magic", "warn": 1}), "assessment.bands[1]", "未知的指標"),
        (lambda t: t["assessment"]["bands"].append({"id": "b9", "metric": "track.maxXtd_m"}), "assessment.bands[1]", "需要 warn 或 fail"),
        (lambda t: t["assessment"]["bands"].append({"id": "b9", "metric": "track.maxXtd_m", "fail": "2x"}), "assessment.bands[1]", "倍數時需要 warn"),
        (lambda t: t["assessment"]["bands"].append({"id": "b9", "metric": "track.maxXtd_m", "warn": 1, "direction": "sideways"}), "assessment.bands[1]", "direction"),
        (lambda t: t["assessment"]["required"].append({"id": "r9", "after": {"event": "x", "itemId": "ghost"}, "expect": "rpmOrder", "within_s": 1}), "assessment.required[1].after", "不存在的時間表項目"),
        (lambda t: t["assessment"]["required"].append({"id": "r9", "after": {}, "expect": "rpmOrder", "within_s": 1}), "assessment.required[1].after", "after 需要 event"),
        (lambda t: t["assessment"]["required"].append({"id": "r9", "after": {"event": "x"}, "expect": "dance", "within_s": 1}), "assessment.required[1].expect", "未知的 expect"),
        (lambda t: t["assessment"]["required"].append({"id": "r9", "after": {"event": "x"}, "expect": "rpmOrder", "within_s": 0}), "assessment.required[1]", "within_s 必須為正"),
        (lambda t: t["briefing"].update(roles=[{"playedBy": "leader"}]), "briefing.roles[0]", "需要 role"),
        # 感測器/警報/BNWAS(C# Scenario.StructuralIssues「警報/BNWAS 設定」)
        (lambda t: t.update(alerts={"escalate_s": 0}), "alerts.escalate_s", "escalate_s 必須為正"),
        (lambda t: t.update(alerts={"silence_s": -1}), "alerts.silence_s", "silence_s 必須為正"),
        (lambda t: t.update(alerts={"offHeading_deg": 0}), "alerts.offHeading_deg", "offHeading_deg 必須為正"),
        (lambda t: t.update(alerts={"offHeadingDelay_s": -0.1}), "alerts.offHeadingDelay_s", "不可為負"),
        (lambda t: t.update(alerts={"headingMonitor_deg": -5}), "alerts.headingMonitor_deg", "headingMonitor_deg 必須為正"),
        (lambda t: t.update(alerts={"shallowWater_m": 0}), "alerts.shallowWater_m", "shallowWater_m 必須為正"),
        (lambda t: t.update(alerts={"escalate_s": "soon"}), "alerts.escalate_s", "須為數值"),
        (lambda t: t.update(bnwas={"dormant_s": 179}), "bnwas.dormant_s", "180–720"),
        (lambda t: t.update(bnwas={"dormant_s": 721}), "bnwas.dormant_s", "180–720"),
        (lambda t: t.update(bnwas={"stage2_s": 0}), "bnwas.stage2_s", "stage2_s 必須為正"),
        (lambda t: t.update(bnwas={"stage3_s": -90}), "bnwas.stage3_s", "stage3_s 必須為正"),
        (lambda t: t.update(bnwas={"mode": "sometimes"}), "bnwas.mode", "mode 須為"),
        (lambda t: t.update(bnwas={"mode": True}), "bnwas.mode", "mode 須為"),
        (lambda t: t.update(bnwas={"dormant_s": None}), "bnwas.dormant_s", "須為數值"),
        (lambda t: t.update(bnwas="on"), "bnwas", "bnwas 須為物件"),
        (lambda t: t.update(sensors={"magneticCompassError_deg": [1]}), "sensors.magneticCompassError_deg", "須為數值"),
    ],
)
def test_structural_check_errors(mutate: Any, path: str, message: str) -> None:
    issues = structural_issues(_mutate(mutate))
    assert issues, "預期有結構錯誤"
    assert any(p == path and message in m for p, m in issues), issues
    with pytest.raises(ValueError, match=message):
        validate_scenario(_mutate(mutate))


def test_structural_check_derives_origin_before_geometry_checks() -> None:
    """C# Scenario.Validate 先以 initial.position 補 origin 再做 StructuralIssues:無 origin 的情境也會檢查航點重合與多邊形自交。"""
    t = _full_tree()
    del t["origin"]
    t["route"]["waypoints"].append({"name": "C", "lat": 23.81, "lon": 120.01})  # 與 B 重合
    assert structural_issues(t) == []  # 單獨呼叫且無 origin 時無幾何檢查(與 C# StructuralIssues 的 Origin is null 相同)
    with pytest.raises(ValueError, match=r"route\.waypoints\[2\]:航點 C 與前一航點重合"):
        validate_scenario(t)
    assert any("重合" in m for _, m in structural_issues(t, origin={"lat": 23.8, "lon": 120.0}))


@pytest.mark.parametrize("with_origin", [True, False])
def test_structural_check_rejects_self_intersecting_polygon(with_origin: bool) -> None:
    t = _full_tree()
    if not with_origin:
        del t["origin"]
    bowtie = [{"x": 0, "y": 0}, {"x": 10, "y": 10}, {"x": 10, "y": 0}, {"x": 0, "y": 10}]
    t["zones"][0]["polygon"] = bowtie
    with pytest.raises(ValueError, match=r"zones\[0\]\.polygon:多邊形自交"):
        validate_scenario(t)
    # 經緯度頂點亦經原點投影後檢查;凸四邊形與三角形不自交
    t["zones"][0]["polygon"] = [{"lat": 23.80, "lon": 120.00}, {"lat": 23.81, "lon": 120.01}, {"lat": 23.81, "lon": 120.00}, {"lat": 23.80, "lon": 120.01}]
    with pytest.raises(ValueError, match="多邊形自交"):
        validate_scenario(t)
    t["zones"][0]["polygon"] = [{"x": 0, "y": 0}, {"x": 10, "y": 0}, {"x": 10, "y": 10}, {"x": 0, "y": 10}]
    assert validate_scenario(t)["zones"][0]["polygon"] == t["zones"][0]["polygon"]


@pytest.mark.parametrize(
    "mutate",
    [
        lambda t: t["briefing"].update(minParticipants=0, duration_s=0),  # C# 無 briefing 數值檢查
        lambda t: t["timeline"].append({"id": 1, "actions": []}),  # LenientStringConverter 讀成 "1"
        lambda t: t["timeline"][1].update(repeat="ONCE"),  # RepeatSpecConverter ToLowerInvariant
        lambda t: t["timeline"][1].update(repeat="Always"),
        lambda t: t["assessment"]["bands"][0].update(fail="2 x"),  # BandThresholdConverter:Trim 後 s[..^1] 的 TryParse 允許空白
        lambda t: t["assessment"]["bands"][0].update(fail=" 3x "),
        lambda t: t["assessment"]["bands"][0].update(fail="120"),  # 數值字串
        lambda t: t["zones"].append({"id": 7, "type": "noGo", "polygon": [{"x": 0, "y": 0}, {"x": 1, "y": 0}, {"x": 0, "y": 1}]}),
        lambda t: t["route"]["waypoints"][0].update(name=1),
        lambda t: t.update(bnwas={"mode": "ON", "dormant_s": "180"}),  # JsonStringEnumConverter 不分大小寫;AllowReadingFromString
        lambda t: t.update(bnwas={"dormant_s": 720}, alerts={"offHeadingDelay_s": 0, "offHeading_deg": None}),  # 邊界值;null = 關閉
        lambda t: t.update(sensors={"magneticCompassError_deg": -2.5, "note": "x"}, alerts={}, bnwas=None),  # 未知鍵忽略;空/null 區段 = 預設
    ],
)
def test_structural_check_accepts_csharp_lenient_forms(mutate: Any) -> None:
    t = _mutate(mutate)
    assert structural_issues(t) == []
    validate_scenario(t)


def test_structural_check_lenient_ids_still_resolve_references() -> None:
    """數字 id 讀成字串後,arm 與 after.itemId 仍須找得到;錯誤寫法仍被拒。"""
    t = _full_tree()
    t["timeline"][0]["id"] = 1
    t["timeline"][1]["actions"][0] = {"arm": 1}
    assert structural_issues(t) == []
    t["timeline"][1]["actions"][0] = {"arm": "1"}
    assert structural_issues(t) == []
    t["timeline"][1]["repeat"] = "twice"
    assert any(p == "timeline[1].repeat" for p, _ in structural_issues(t))
    t["timeline"][1]["repeat"] = "once"
    t["assessment"]["bands"][0]["fail"] = "2 y"
    assert any(p == "assessment.bands[0]" and "<n>x" in m for p, m in structural_issues(t))


def test_bridge_sections_defaults_and_normalisation() -> None:
    """sensors/alerts/bnwas 未給時補 C# 預設值(ScenarioBridge.cs),門檻類省略 = 關閉;mode 轉小寫、數值字串轉數值。"""
    sc = validate_scenario(_full_tree())
    assert sc["sensors"] == {"magneticCompassError_deg": 0.0}
    assert sc["alerts"] == {"escalate_s": 300.0, "silence_s": 30.0, "offHeadingDelay_s": 10.0}
    assert sc["bnwas"] == {"mode": "off", "dormant_s": 720.0, "stage2_s": 15.0, "stage3_s": 90.0}
    t = _full_tree()
    t["bnwas"] = {"mode": "Auto", "dormant_s": "300", "extra": 1}
    t["alerts"] = {"headingMonitor_deg": 5, "offHeading_deg": None}
    sc = validate_scenario(t)
    assert sc["bnwas"] == {"mode": "auto", "dormant_s": 300.0, "stage2_s": 15.0, "stage3_s": 90.0}
    assert sc["alerts"] == {"escalate_s": 300.0, "silence_s": 30.0, "offHeadingDelay_s": 10.0, "headingMonitor_deg": 5.0}


def test_bridge_sections_resolve_parameter_references() -> None:
    """sensors/alerts/bnwas 的數值欄位可引用參數(單位後綴檢查照常);mode 不可引用。"""
    tree = _full_tree()
    tree["bnwas"] = {"mode": "on", "dormant_s": "${params:bridge.bnwas.dormantPeriod_s}"}
    sc = scenario_from_tree(copy.deepcopy(tree))
    assert sc["bnwas"]["dormant_s"] == 540
    assert [r["path"] for r in sc["parameterSet"]["resolved"]] == ["bnwas.dormant_s"]
    tree["alerts"] = {"offHeading_deg": "${params:bridge.bnwas.dormantPeriod_s}"}  # deg ≠ s
    with pytest.raises(ValueError, match="單位後綴"):
        scenario_from_tree(copy.deepcopy(tree))
    tree.pop("alerts")
    tree["bnwas"]["mode"] = "${params:bridge.bnwas.dormantPeriod_s}"
    with pytest.raises(ValueError, match="不允許引用參數"):
        scenario_from_tree(tree)


def test_scenario_from_tree_resolves_then_checks() -> None:
    tree = _full_tree()
    tree["assessment"]["bands"][0]["warn"] = "${params:navigation.xtd.channel_m}"
    tree["assessment"]["hardFail"][0]["lessThan"] = "${params:navigation.cpa.hardFail_nm}"
    sc = scenario_from_tree(copy.deepcopy(tree))
    assert sc["assessment"]["bands"][0]["warn"] == 50 and sc["assessment"]["hardFail"][0]["lessThan"] == 0.2
    assert [r["path"] for r in sc["parameterSet"]["resolved"]] == ["assessment.hardFail[0].lessThan", "assessment.bands[0].warn"]
    tree["assessment"]["bands"][0]["warn"] = "${params:navigation.cpa.nope_nm}"
    with pytest.raises(ValueError, match="無鍵"):
        scenario_from_tree(tree)


# ---------------------------------------------------------------- 伺服器


def _server() -> SimServer:
    ship = make_ship("FSB1", "full", dt=0.02, prefer_file=False)
    return SimServer(ship, 1.0)


def test_server_scenario_engine_commands_report_unimplemented() -> None:
    srv = _server()
    assert "scenario" not in srv.state_json()
    for cmd in ({"type": "triggerTimeline", "args": {"id": "windShift", "force": True}}, {"type": "acknowledge", "args": {"kind": "notifyEcr"}},
                {"type": "getAssessment"}, {"type": "endExercise", "args": {"reason": "test"}}):
        ack = srv.apply_command(cmd)
        assert ack["ok"] is False and "未實作" in ack["detail"] and ack["command"] == cmd["type"], ack
    ack = srv.apply_command({"type": "loadScenario", "value": "E07_steering_failure"})
    assert ack["ok"] and "參數集 simosa-company-procedures 2026." in ack["detail"] and "18 處引用" in ack["detail"]
    s = srv.state_json()
    assert s["scenario"]["id"] == "E07_steering_failure" and s["scenario"]["phase"] == "normal"
    assert s["scenario"]["parameterSetVersion"] == srv.scenario["parameterSet"]["version"]
    assert "scenario" not in srv.ship.state_json()  # 自船狀態本身不變
    for cmd in ({"type": "triggerTimeline", "args": {"id": "steeringFail"}}, {"type": "acknowledge", "args": {"kind": "steeringMode"}},
                {"type": "getAssessment"}, {"type": "endExercise"}):
        ack = srv.apply_command(cmd)
        assert ack["ok"] is False and "未實作" in ack["detail"]
    assert srv.ship.state_json()["faults"] == [] and srv.ship.state.tick == 0  # 模型不受影響
    assert srv.apply_command({"type": "loadScenario", "value": "E05_pilot_traffic"})["ok"]
    assert "parameterSetVersion" not in srv.state_json()["scenario"] and srv.state_json()["scenario"]["id"] == "E05_pilot_traffic"


def test_server_alert_and_bnwas_commands_report_unimplemented() -> None:
    """alertAcknowledge / alertSilence / bnwasMode / bnwasReset(command.schema.json;sensors-alerts.md 第 3.3、4 節):
    Python 不實作 BAM/BNWAS,回 ok=false「未實作」,不當機、模型不變;E08 載入後亦同。"""
    srv = _server()
    cmds = ({"type": "alertAcknowledge", "args": {"id": "gyroFailure"}}, {"type": "alertAcknowledge"}, {"type": "alertSilence", "value": "bnwas"},
            {"type": "alertSilence"}, {"type": "bnwasMode", "value": "on", "args": {"dormant_s": 540}}, {"type": "bnwasReset"})
    for _ in range(2):
        before = srv.ship.state_json()
        for cmd in cmds:
            ack = srv.apply_command(cmd)
            assert ack["ok"] is False and "未實作" in ack["detail"] and ack["command"] == cmd["type"], ack
        assert srv.ship.state_json() == before
        for key in ("alerts", "bnwas", "truth", "sensors"):
            assert key not in srv.state_json()
        assert srv.apply_command({"type": "loadScenario", "value": "E08_gyro_failure"})["ok"]
        assert srv.scenario["bnwas"]["dormant_s"] == 540
