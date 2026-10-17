"""公司程序參數集與情境中 ``${params:<key>}`` 引用的解析(規劃書第 8.3、9.1 節 R14;情境引擎設計第 2 節)。

語意與 C# ``SimCore.Parameters``(ParameterSet.cs / ParameterResolver.cs)及 ``YamlJson.cs`` 相同:

- YAML → 通用節點樹(dict / list / 純量);**純量型別推斷與 C# 相同**:加引號者為字串;純量 ``true``/``false``(不分大小寫)
  → 布林、十進位整數/小數(含指數)→ 數值、``null``/``~``/空 → None,其餘(含日期、``yes``/``no``、``0x1F``、``1_000``)為字串。
- 參數集(``data/parameters/company-procedures.yaml``;schema ``src/Contracts/parameter-set.schema.json``)載入時做結構檢查:
  版次格式 ``YYYY.M.n``、單位列舉、來源文件存在、鍵名單位後綴與 unit 一致、min/max(數值或數值字串,否則錯誤)、
  ``external`` 須為布林。``ParameterSet.load`` 的 ``path`` 為相對專案根目錄(D34;不在專案內則原路徑)。
- 情境節點樹中整個字串恰為 ``${params:<key>}`` 者以參數 ``value`` 取代(不支援內嵌/運算);未知鍵、單位後綴不符、
  引用於不允許的欄位(id、name、seed …)、來源文件缺漏皆為 ``ValueError``。
- 解析清單 ``[{path, key, value, unit, source, revision, clause}]`` 與摘要 ``{id, version, path, sha256, resolved}``
  的形狀與紀錄標頭 ``header.parameterSet`` 相同(重播與講評不需原始參數集檔案)。
"""

from __future__ import annotations

import copy
import hashlib
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml

from . import paths

DEFAULT_PARAMETER_SET_PATH = "data/parameters/company-procedures.yaml"  # C# ScenarioLoader.DefaultParameterSetPath

VERSION_PATTERN = re.compile(r"^\d{4}\.\d{1,2}\.\d+$")
REF_PATTERN = re.compile(r"^\$\{params:([A-Za-z0-9_.\-]+)\}$")  # C# ParameterResolver.RefPattern
UNITS = ("nm", "m", "s", "min", "kn", "mps", "deg", "degPerMin", "fractionOfDraft", "score", "count", "bool", "text")
UNITLESS = ("score", "count", "bool", "text")
# 鍵名/欄位名後綴 → 單位(依序比對,長後綴在前;C# ParameterSet.SuffixUnits)
SUFFIX_UNITS = (
    ("_degPerMin", "degPerMin"), ("_fraction", "fractionOfDraft"), ("_mps", "mps"), ("_min", "min"),
    ("_nm", "nm"), ("_kn", "kn"), ("_deg", "deg"), ("_m", "m"), ("_s", "s"),
)
# 不得引用參數的欄位名(路徑最後一段;C# ParameterResolver.ForbiddenLeaves)
FORBIDDEN_LEAVES = frozenset({
    "id", "name", "seed", "loading", "kind", "type", "rule", "metric", "mode", "shipType", "role", "channel", "parameterSet",
})
# 欄位名 → 參數 unit 的額外對應(無單位後綴的慣用欄位;C# ParameterResolver.LeafUnits)
LEAF_UNITS = {"fractionOfDraft": "fractionOfDraft", "metres": "m", "passMark": "score"}


# ---------------------------------------------------------------- YAML → 節點樹(與 C# YamlJson 相同的純量規則)


class _TreeLoader(yaml.SafeLoader):
    """只保留與 C# YamlJson.ConvertScalar 相同的隱式型別:true/false、十進位數值、null;其餘純量一律字串。"""


# 清掉 SafeLoader 的 YAML 1.1 隱式解析(yes/no/on/off、timestamp、0x/0o/底線/60 進位、.inf/.nan、merge 等)
_TreeLoader.yaml_implicit_resolvers = {}
# C# 以 OrdinalIgnoreCase 比對 true/false/null:TRue、NuLL 亦為布林/null
_TreeLoader.add_implicit_resolver("tag:yaml.org,2002:bool", re.compile(r"^(?:true|false)$", re.IGNORECASE), list("tTfF"))
_TreeLoader.add_implicit_resolver("tag:yaml.org,2002:null", re.compile(r"^(?:~|null|)$", re.IGNORECASE), ["~", "n", "N", ""])
_TreeLoader.add_implicit_resolver("tag:yaml.org,2002:int", re.compile(r"^[-+]?[0-9]+$"), list("-+0123456789"))
_TreeLoader.add_implicit_resolver(
    "tag:yaml.org,2002:float",
    re.compile(r"^[-+]?(?:[0-9]+\.[0-9]*|\.[0-9]+|[0-9]+)(?:[eE][-+]?[0-9]+)?$"),
    list("-+0123456789."),
)


def _construct_scalar_str(loader: yaml.SafeLoader, node: yaml.ScalarNode) -> str:
    return loader.construct_scalar(node)


def _construct_int(loader: yaml.SafeLoader, node: yaml.ScalarNode) -> int:
    # C# long.TryParse(十進位):010 → 10、-010 → -10、08 → 8;不用 PyYAML 的八進位/底線規則
    return int(loader.construct_scalar(node), 10)


def _construct_float(loader: yaml.SafeLoader, node: yaml.ScalarNode) -> float:
    v = float(loader.construct_scalar(node))
    if v != v or v in (float("inf"), float("-inf")):
        return loader.construct_scalar(node)  # type: ignore[return-value]  # C#:非有限數值保留為字串
    return v


_TreeLoader.add_constructor("tag:yaml.org,2002:int", _construct_int)
_TreeLoader.add_constructor("tag:yaml.org,2002:float", _construct_float)
_TreeLoader.add_constructor("tag:yaml.org,2002:timestamp", _construct_scalar_str)


def yaml_to_tree(text: str) -> Any:
    """YAML 文字 → dict / list / 純量節點樹(第一份文件;空文件回 None)。鍵一律轉為字串(C# ConvertMap)。"""
    tree = yaml.load(text, Loader=_TreeLoader)  # noqa: S506 - 自訂 SafeLoader 子類別
    return _stringify_keys(tree)


def _stringify_keys(node: Any) -> Any:
    if isinstance(node, dict):
        return {("" if k is None else str(k)): _stringify_keys(v) for k, v in node.items()}
    if isinstance(node, list):
        return [_stringify_keys(v) for v in node]
    return node


def _is_number(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _opt_number(v: Any, where: str) -> float | None:
    """選填數值欄位:數值或可解析為十進位數值的字串(C# ContractJson NumberHandling.AllowReadingFromString);
    其餘型別(布林、物件、非數值字串)為 ``ValueError``。"""
    if v is None:
        return None
    if _is_number(v):
        return float(v)
    if isinstance(v, str):
        try:
            f = float(v.strip())
        except ValueError:
            f = float("nan")
        if f == f and f not in (float("inf"), float("-inf")):
            return f
    raise ValueError(f"{where} 須為數值:{v!r}")


def _project_relative(p: Path) -> str:
    """相對專案根目錄的路徑('/' 分隔;C# RepositoryPaths.ToProjectRelative,情境引擎設計 D34);不在專案內則原樣。"""
    try:
        return p.resolve().relative_to(paths.repo_root()).as_posix()
    except ValueError:
        return str(p)


# ---------------------------------------------------------------- 參數集


def unit_of_suffix(name: str) -> str | None:
    """名稱的單位後綴所對應的單位;無後綴時 None(C# ParameterSet.UnitOfSuffix)。"""
    for suffix, unit in SUFFIX_UNITS:
        if name.endswith(suffix):
            return unit
    return None


@dataclass
class ParameterSet:
    id: str = ""
    version: str = ""
    effective_from: str | None = None
    approved_by: str | None = None
    approved_on: str | None = None
    notes: str | None = None
    sources: dict[str, dict[str, Any]] = field(default_factory=dict)
    parameters: dict[str, dict[str, Any]] = field(default_factory=dict)
    overrides: Any = None  # 保留:依船舶 ID 覆寫(第一版不解析)
    path: str | None = None  # 載入來源路徑(由 load 填入)
    sha256: str = ""  # 檔案位元組的 SHA-256(hex 大寫;與 C# Convert.ToHexString 相同)

    def get(self, key: str) -> dict[str, Any] | None:
        return self.parameters.get(key)

    def info(self, resolved: list[dict[str, Any]] | None = None) -> dict[str, Any]:
        """參數引用摘要(紀錄標頭 header.parameterSet 的形狀)。"""
        return {"id": self.id, "version": self.version, "path": self.path, "sha256": self.sha256,
                "resolved": list(resolved or [])}

    # ---- 載入 ----
    @classmethod
    def from_tree(cls, tree: Any) -> "ParameterSet":
        if not isinstance(tree, dict):
            raise ValueError("參數集 YAML 須為物件" if tree is not None else "參數集 YAML 為空")

        def opt_str(key: str) -> str | None:
            v = tree.get(key)
            return None if v is None else str(v)

        sources_raw = tree.get("sources") or {}
        params_raw = tree.get("parameters") or {}
        if not isinstance(sources_raw, dict):
            raise ValueError("參數集 sources 須為物件")
        if not isinstance(params_raw, dict):
            raise ValueError("參數集 parameters 須為物件")
        sources: dict[str, dict[str, Any]] = {}
        for k, src in sources_raw.items():
            if not isinstance(src, dict):
                raise ValueError(f"sources.{k} 須為物件")
            external = src.get("external", False)
            if not isinstance(external, bool):  # C# bool 欄位嚴格:"no"/"false" 字串為 JsonException
                raise ValueError(f"sources.{k}.external 須為布林:{external!r}")
            sources[str(k)] = {"title": None if src.get("title") is None else str(src["title"]),
                               "revision": None if src.get("revision") is None else str(src["revision"]),
                               "approvedBy": None if src.get("approvedBy") is None else str(src["approvedBy"]),
                               "date": None if src.get("date") is None else str(src["date"]),
                               "external": external}
        params: dict[str, dict[str, Any]] = {}
        for k, p in params_raw.items():
            if not isinstance(p, dict):
                raise ValueError(f"parameters.{k} 須為物件")
            entry = {"value": p.get("value"), "unit": "" if p.get("unit") is None else str(p["unit"]),
                     "source": "" if p.get("source") is None else str(p["source"]),
                     "clause": None if p.get("clause") is None else str(p["clause"]),
                     "description": None if p.get("description") is None else str(p["description"]),
                     "min": _opt_number(p.get("min"), f"parameters.{k}.min"),
                     "max": _opt_number(p.get("max"), f"parameters.{k}.max")}
            params[str(k)] = entry
        return cls(id=opt_str("id") or "", version=opt_str("version") or "", effective_from=opt_str("effectiveFrom"),
                   approved_by=opt_str("approvedBy"), approved_on=opt_str("approvedOn"), notes=opt_str("notes"),
                   sources=sources, parameters=params, overrides=tree.get("overrides")).validate()

    @classmethod
    def from_yaml(cls, text: str) -> "ParameterSet":
        """自 YAML 文字載入(含結構檢查);sha256 為文字的 UTF-8 位元組雜湊。"""
        pset = cls.from_tree(yaml_to_tree(text))
        pset.sha256 = hashlib.sha256(text.encode("utf-8")).hexdigest().upper()
        return pset

    @classmethod
    def load(cls, path: str | os.PathLike[str]) -> "ParameterSet":
        p = Path(path)
        if not p.is_file():
            raise FileNotFoundError(f"找不到參數集:{p}")
        data = p.read_bytes()
        pset = cls.from_tree(yaml_to_tree(data.decode("utf-8")))
        pset.path = _project_relative(p)
        pset.sha256 = hashlib.sha256(data).hexdigest().upper()
        return pset

    # ---- 結構檢查(C# ParameterSet.Validate)----
    def validate(self) -> "ParameterSet":
        if not self.id.strip():
            raise ValueError("參數集缺少 id")
        if not VERSION_PATTERN.match(self.version):
            raise ValueError(f"參數集 version 格式須為 YYYY.M.n:{self.version}")
        for key, src in self.sources.items():
            if not (src.get("title") or "").strip():
                raise ValueError(f"sources.{key} 缺少 title")
            if src.get("revision") is None:
                raise ValueError(f"sources.{key} 缺少 revision")
        for key, p in self.parameters.items():
            v = p["value"]
            if not (_is_number(v) or isinstance(v, (str, bool))):
                raise ValueError(f"parameters.{key}.value 須為數值、布林或字串")
            if p["unit"] not in UNITS:
                raise ValueError(f"parameters.{key}.unit 未知:{p['unit']}")
            if not p["source"].strip() or p["source"] not in self.sources:
                raise ValueError(f"parameters.{key}.source 未在 sources 中定義:{p['source']}")
            suffix_unit = unit_of_suffix(key)
            if suffix_unit is not None and suffix_unit != p["unit"]:
                raise ValueError(f"parameters.{key} 的單位後綴({suffix_unit})與 unit({p['unit']})不符")
            if suffix_unit is None and p["unit"] not in UNITLESS:
                raise ValueError(f"parameters.{key} 缺少單位後綴(unit {p['unit']} 需以 _{p['unit']} 結尾)")
            if _is_number(v):
                lo, hi = p.get("min"), p.get("max")
                if _is_number(lo) and v < lo:
                    raise ValueError(f"parameters.{key}.value {v} 低於 min {lo}")
                if _is_number(hi) and v > hi:
                    raise ValueError(f"parameters.{key}.value {v} 高於 max {hi}")
        return self


def find_parameter_set(declared: str | None, base_directory: str | os.PathLike[str] | None = None) -> Path | None:
    """依 C# ScenarioLoader.TryAutoLoadParameterSet 的順序找參數集檔:絕對路徑;否則專案根目錄、情境檔目錄、工作目錄;無則 None。
    ``declared`` 為 None 時用預設路徑;空字串與 C# 相同視為「已宣告但找不到」(回 None)。"""
    text = DEFAULT_PARAMETER_SET_PATH if declared is None else declared
    if not text.strip():
        return None
    p = Path(text)
    candidates = [p] if p.is_absolute() else [paths.repo_root() / p] + ([Path(base_directory) / p] if base_directory else []) + [p]
    for c in candidates:
        if c.is_file():
            return c.resolve()
    return None


# ---------------------------------------------------------------- 情境中的引用


def _leaf_name(path: str) -> str:
    leaf = path.rsplit(".", 1)[-1]
    return leaf.split("[", 1)[0]


def _walk(node: Any, path: str) -> list[tuple[str, str]]:
    refs: list[tuple[str, str]] = []
    if isinstance(node, dict):
        for k, v in node.items():
            refs += _walk(v, k if not path else f"{path}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            refs += _walk(v, f"{path}[{i}]")
    elif isinstance(node, str):
        m = REF_PATTERN.match(node)
        if m:
            refs.append((path, m.group(1)))
    return refs


def find_references(tree: Any) -> list[tuple[str, str]]:
    """節點樹中所有引用 ``[(欄位路徑, 鍵)]``(走訪順序與 C# 相同:物件依鍵序、陣列依索引)。"""
    return _walk(tree, "")


def has_references(tree: Any) -> bool:
    return bool(find_references(tree))


def set_by_path(root: Any, path: str, value: Any) -> bool:
    """以路徑字串(a.b[2].c)設定節點值;路徑不存在時回 False(C# YamlJson.SetByPath)。"""
    segs = _split_path(path)
    cur = root
    for seg in segs[:-1]:
        if isinstance(seg, int):
            cur = cur[seg] if isinstance(cur, list) and seg < len(cur) else None
        else:
            cur = cur.get(seg) if isinstance(cur, dict) else None
        if cur is None:
            return False
    last = segs[-1]
    if isinstance(last, int):
        if isinstance(cur, list) and last < len(cur):
            cur[last] = value
            return True
        return False
    if isinstance(cur, dict):
        cur[last] = value
        return True
    return False


def get_by_path(root: Any, path: str) -> Any:
    """以路徑字串取得節點;不存在時 None。"""
    cur = root
    for seg in _split_path(path):
        if isinstance(seg, int):
            cur = cur[seg] if isinstance(cur, list) and seg < len(cur) else None
        else:
            cur = cur.get(seg) if isinstance(cur, dict) else None
        if cur is None:
            return None
    return cur


def _split_path(path: str) -> list[str | int]:
    segs: list[str | int] = []
    buf = ""
    i = 0
    while i < len(path):
        c = path[i]
        if c == ".":
            if buf:
                segs.append(buf)
                buf = ""
        elif c == "[":
            if buf:
                segs.append(buf)
                buf = ""
            end = path.index("]", i)
            segs.append(int(path[i + 1:end]))
            i = end
        else:
            buf += c
        i += 1
    if buf:
        segs.append(buf)
    return segs


def resolve_references(tree: Any, pset: ParameterSet | None) -> list[dict[str, Any]]:
    """就地替換所有引用;回傳解析清單(C# ParameterResolver.Resolve)。``pset`` 為 None 且有引用時 ``ValueError``。"""
    refs = find_references(tree)
    if not refs:
        return []
    if pset is None:
        raise ValueError(f"情境引用參數 {refs[0][1]}({refs[0][0]})但未提供參數集;請以 --parameters <path> 指定或在 assessment.parameterSet 設定")
    resolved: list[dict[str, Any]] = []
    for path, key in refs:
        leaf = _leaf_name(path)
        if leaf in FORBIDDEN_LEAVES:
            raise ValueError(f"欄位 {path} 不允許引用參數")
        entry = pset.get(key)
        if entry is None:
            raise ValueError(f"參數集 {pset.id} {pset.version} 無鍵 {key}(引用於 {path})")
        expected = unit_of_suffix(leaf) or LEAF_UNITS.get(leaf)
        if expected is not None and expected != entry["unit"]:
            raise ValueError(f"欄位 {path} 的單位後綴({expected})與參數 {key} 的 unit({entry['unit']})不符")
        src = pset.sources.get(entry["source"])
        if src is None:
            raise ValueError(f"參數 {key} 的來源文件 {entry['source']} 未在 sources 定義")
        set_by_path(tree, path, copy.deepcopy(entry["value"]))
        resolved.append({"path": path, "key": key, "value": entry["value"], "unit": entry["unit"], "source": entry["source"],
                         "revision": src.get("revision"), "clause": entry.get("clause")})
    return resolved
