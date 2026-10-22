"""資料路徑守則測試(CLAUDE.md:所有建置輸出、快取、套件、紀錄都必須留在專案資料夾內,不寫到 C:\\)。

審查發現(資料路徑審查,leftovers):
1. pytest ``--basetemp`` 寫在 pyproject 的 addopts 時以「執行時的目前目錄」解析相對路徑,
   不在 src/Tools.Calibration 下執行會把 tmp_path 放到專案外(或 FileNotFoundError)。
   修正:tests/conftest.py 在 pytest_configure 以 conftest 位置推導絕對路徑。
2. uv 自動下載的受管 Python 預設裝到 %APPDATA%\\uv\\python(C:\\),只能用環境變數改;
   tools/setup-windows.ps1 必須設定 UV_PYTHON_INSTALL_DIR 等變數到 .cache\\ 之下。
3. uv 的 cache-dir 設定也以目前目錄解析;根目錄 uv.toml 與 pyproject [tool.uv] 兩份設定
   必須指向同一個專案 .cache/uv,Windows 則以 UV_CACHE_DIR(絕對路徑)為準。
"""

from __future__ import annotations

import re
import subprocess
import sys
import tomllib
from pathlib import Path

import pytest

TESTS_DIR = Path(__file__).resolve().parent
CALIB_DIR = TESTS_DIR.parent  # src/Tools.Calibration(pytest rootdir)
PROJECT_ROOT = CALIB_DIR.parents[1]  # D:\Simosa BRM
EXPECTED_BASETEMP = PROJECT_ROOT / "build" / "test-tmp" / "pytest"
EXPECTED_UV_CACHE = PROJECT_ROOT / ".cache" / "uv"
SETUP_SCRIPT = PROJECT_ROOT / "tools" / "setup-windows.ps1"


def _pyproject() -> dict:
    return tomllib.loads((CALIB_DIR / "pyproject.toml").read_text(encoding="utf-8"))


# ---- 發現 1:pytest basetemp -------------------------------------------------


def test_pyproject_addopts_has_no_relative_basetemp():
    """addopts 不得含 --basetemp:相對路徑會以 cwd 解析,不是以 pyproject 所在目錄。"""
    addopts = _pyproject()["tool"]["pytest"]["ini_options"].get("addopts", "")
    assert "--basetemp" not in addopts, f"addopts 含 --basetemp(以 cwd 解析):{addopts!r}"


def test_basetemp_option_is_absolute_inside_project(request):
    """conftest 設定的 basetemp 為絕對路徑,且固定在專案的 build/test-tmp/pytest。

    此測試不使用 tmp_path(不觸發 basetemp 的建立/清空),因此可被
    test_basetemp_resolves_inside_project_from_foreign_cwd 以子行程重跑而不影響本行程。
    """
    basetemp = request.config.option.basetemp
    assert basetemp is not None, "basetemp 未設定(tmp_path 會落到系統暫存目錄,專案之外)"
    p = Path(basetemp)
    assert p.is_absolute(), f"basetemp 為相對路徑,會以 cwd 解析:{basetemp!r}"
    assert p == EXPECTED_BASETEMP, f"basetemp {p} != {EXPECTED_BASETEMP}"


def test_tmp_path_is_inside_project(tmp_path):
    """實際的 tmp_path 必須在專案 build/test-tmp/pytest 之下。"""
    resolved = tmp_path.resolve()
    assert resolved.is_relative_to(EXPECTED_BASETEMP.resolve()), f"tmp_path 在專案外:{resolved}"


def test_basetemp_resolves_inside_project_from_foreign_cwd(tmp_path):
    """從任意目前目錄(此處為一個深層暫存目錄)執行 pytest,basetemp 仍為專案內的絕對路徑。

    重現審查發現的情境:cwd 不是 src/Tools.Calibration 時,舊設定會解析成 cwd/../../build/...。
    子行程只跑不使用 tmp_path 的測試,避免清空本行程正在使用的 basetemp。
    """
    foreign_cwd = tmp_path / "a" / "b"
    foreign_cwd.mkdir(parents=True)
    cmd = [
        sys.executable,
        "-m",
        "pytest",
        str(TESTS_DIR / "test_repo_datapaths.py"),
        "-k",
        "test_basetemp_option_is_absolute_inside_project",
        "-p",
        "no:cacheprovider",
        "-q",
    ]
    r = subprocess.run(cmd, cwd=foreign_cwd, capture_output=True, text=True, timeout=300)
    assert r.returncode == 0, f"從 {foreign_cwd} 執行失敗:\n{r.stdout}\n{r.stderr}"
    assert "1 passed" in r.stdout, r.stdout
    # 子行程不得在 cwd 旁邊另建 build/(舊設定的症狀)
    assert not (tmp_path / "build").exists(), "子行程在 cwd/../../build 建立了暫存目錄"


def test_pytest_cache_dir_is_project_root_cache(request):
    """pytest 快取(cache_dir 相對 rootdir)落在專案根目錄的 .cache/pytest。"""
    cachedir = Path(request.config.cache._cachedir).resolve()
    assert cachedir == (PROJECT_ROOT / ".cache" / "pytest").resolve(), f"pytest cache_dir = {cachedir}"


# ---- 發現 2:uv 受管 Python 安裝目錄 -------------------------------------------

UV_DIR_VARS = {
    # 受管 Python(uv sync/run 自動下載的 CPython);預設 %APPDATA%\uv\python
    "UV_PYTHON_INSTALL_DIR": "uv-python",
    # uv tool install 的工具環境;預設 %APPDATA%\uv\tools
    "UV_TOOL_DIR": "uv-tools",
    # uv tool / uv python install 放可執行檔捷徑的目錄;預設 %USERPROFILE%\.local\bin
    "UV_TOOL_BIN_DIR": "uv-bin",
    "UV_PYTHON_BIN_DIR": "uv-bin",
    # 套件快取;預設 %LOCALAPPDATA%\uv\cache
    "UV_CACHE_DIR": "uv",
}


@pytest.mark.parametrize("var,subdir", sorted(UV_DIR_VARS.items()))
def test_setup_windows_sets_uv_dirs_inside_project(var: str, subdir: str):
    """setup-windows.ps1 必須把 uv 會寫入的每個目錄都以 $cache(= <專案>\\.cache)為基底設定。"""
    text = SETUP_SCRIPT.read_text(encoding="utf-8")
    m = re.search(rf"'{var}'\s*=\s*\(Join-Path \$cache '([^']+)'\)", text)
    assert m, f"setup-windows.ps1 未設定 {var}(uv 會寫到 C:\\Users)"
    assert m.group(1).replace("/", "\\").split("\\")[0] == subdir, f"{var} 指到 {m.group(1)},預期 .cache\\{subdir}"


def test_readme_mentions_managed_python_location():
    """README 須說明:沒有 Python 3.12+ 時 uv 會代為下載到 .cache\\uv-python(不是 C:\\)。"""
    readme = (PROJECT_ROOT / "README.md").read_text(encoding="utf-8")
    assert "UV_PYTHON_INSTALL_DIR" in readme, "README 未說明 uv 受管 Python 的安裝位置"


# ---- 發現 3:uv cache-dir 以 cwd 解析 -----------------------------------------


def test_uv_cache_dir_configs_agree_on_project_cache():
    """uv.toml(根目錄)與 pyproject [tool.uv](src/Tools.Calibration)的 cache-dir,
    各自以其所在目錄解析後必須都是專案 .cache/uv:uv 以 cwd 解析相對路徑,
    因此 uv 指令須在這兩個目錄之一執行(或以 uv --directory 指定),詳見 README。"""
    root_cfg = tomllib.loads((PROJECT_ROOT / "uv.toml").read_text(encoding="utf-8"))
    assert (PROJECT_ROOT / root_cfg["cache-dir"]).resolve() == EXPECTED_UV_CACHE.resolve()
    calib_cfg = _pyproject()["tool"]["uv"]
    assert (CALIB_DIR / calib_cfg["cache-dir"]).resolve() == EXPECTED_UV_CACHE.resolve()


def test_readme_documents_uv_working_directory_rule():
    """Tools.Calibration README 須說明 uv 指令的執行目錄規則(cache-dir 以 cwd 解析)。"""
    readme = (CALIB_DIR / "README.md").read_text(encoding="utf-8")
    assert "--directory" in readme, "README 未說明 uv 須在根目錄或 src/Tools.Calibration 執行(或用 uv --directory)"
