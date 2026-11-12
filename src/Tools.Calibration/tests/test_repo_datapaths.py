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
    text = SETUP_SCRIPT.read_text(encoding="utf-8-sig")
    m = re.search(rf"'{var}'\s*=\s*\(Join-Path \$cache '([^']+)'\)", text)
    assert m, f"setup-windows.ps1 未設定 {var}(uv 會寫到 C:\\Users)"
    assert m.group(1).replace("/", "\\").split("\\")[0] == subdir, f"{var} 指到 {m.group(1)},預期 .cache\\{subdir}"


# ---- 其他工具:只能用環境變數導回專案內的目錄(nuget.config / .npmrc 無對應鍵) ----

OTHER_DIR_VARS = {
    # NuGet HTTP 快取;預設 %LOCALAPPDATA%\NuGet\v3-cache(nuget.config 沒有對應的設定鍵)
    "NUGET_HTTP_CACHE_PATH": "nuget",
    # NuGet 外掛快取;預設 %LOCALAPPDATA%\NuGet\plugins-cache
    "NUGET_PLUGINS_CACHE_PATH": "nuget",
    # NuGet 還原暫存;預設 %TEMP%\NuGetScratch
    "NUGET_SCRATCH": "nuget",
    # npm / npx 快取;預設 %LOCALAPPDATA%\npm-cache
    "npm_config_cache": "npm",
    # Node.js 編譯快取(corepack / pnpm / npm 啟動時寫);預設 %TEMP%\node-compile-cache
    "NODE_COMPILE_CACHE": "node-compile-cache",
    # Playwright 轉譯快取;預設 %TEMP%\playwright-transform-cache*
    "PWTEST_CACHE_DIR": "playwright-transform",
}


@pytest.mark.parametrize("var,subdir", sorted(OTHER_DIR_VARS.items()))
def test_setup_windows_sets_other_tool_dirs_inside_project(var: str, subdir: str):
    """setup-windows.ps1 必須把 NuGet HTTP/外掛快取與暫存、npm 快取、Node 編譯快取、Playwright 轉譯快取都以 $cache(= <專案>\\.cache)為基底設定。"""
    text = SETUP_SCRIPT.read_text(encoding="utf-8-sig")
    m = re.search(rf"'{var}'\s*=\s*\(Join-Path \$cache '([^']+)'\)", text)
    assert m, f"setup-windows.ps1 未設定 {var}(會寫到 C:\\Users)"
    assert m.group(1).replace("/", "\\").split("\\")[0] == subdir, f"{var} 指到 {m.group(1)},預期 .cache\\{subdir}"


def test_nuget_config_does_not_pretend_to_redirect_http_cache():
    """nuget.config 的 <config> 只有 globalPackagesFolder 有效;http-cache / plugins-cache 不是有效的設定鍵
    (曾誤以為有效,實際 HTTP 快取仍落在 %LOCALAPPDATA%\\NuGet),必須改用環境變數並在檔內註明。"""
    text = (PROJECT_ROOT / "nuget.config").read_text(encoding="utf-8")
    assert 'key="globalPackagesFolder"' in text
    assert 'key="http-cache"' not in text and 'key="plugins-cache"' not in text, "nuget.config 不應再有無效的 http-cache / plugins-cache 鍵"
    for var in ("NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH", "NUGET_SCRATCH"):
        assert var in text, f"nuget.config 應註明 {var}(HTTP/外掛快取與暫存只能用環境變數改)"


def test_setup_windows_refuses_system_drive_and_redirects_temp():
    """setup-windows.ps1:專案在系統磁碟時預設拒絕(-AllowSystemDrive 才繼續);TEMP/TMP 導到 build\\tmp(-SystemTemp 才留在系統預設位置);
    不寫死磁碟代號(D:、G: 皆可)。檔案以 UTF-8 BOM 儲存(Windows PowerShell 5.1),讀取時用 utf-8-sig 才不會讓第一行開頭多一個 U+FEFF。"""
    text = SETUP_SCRIPT.read_text(encoding="utf-8-sig")
    assert "$AllowSystemDrive" in text and "$env:SystemDrive" in text, "應檢查專案是否位於系統磁碟"
    assert "$SystemTemp" in text and re.search(r"\$env:TEMP = \$tmp; \$env:TMP = \$tmp", text), "應把 TEMP/TMP 導到 build\\tmp"
    assert "$SessionOnly" in text, "應提供只設定目前工作階段的 -SessionOnly"
    code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))
    assert not re.search(r"(?<![A-Za-z$])[A-Za-z]:\\", code), "setup-windows.ps1 的程式碼(註解外)不應寫死磁碟代號(C:\\、D:\\、G:\\ …)"


def test_setup_windows_keeps_pnpm_usable_and_checks_filesystem():
    """審查(第二十八輪):
    - 不改 COREPACK_HOME:corepack 下載的 pnpm 本體屬應用程式;改位置會讓已下載的 pnpm 與固定版本紀錄失效(離線無法使用、連線改抓最新版);
    - pnpm 檢查期間禁止 corepack 連網(尚未下載時立即失敗,而不是在被丟棄的 stderr 上等使用者回答);建議固定版本並警告 11 以上;
    - 檢查專案磁碟的檔案系統(非 NTFS 上 pnpm install 會失敗)與抽取式磁碟(建議 -SessionOnly);-Unregister 可移除使用者層級變數;
    - SIMOSA_BRM_ROOT 不寫成使用者層級(專案複製到另一個磁碟後會指向舊位置)。"""
    text = SETUP_SCRIPT.read_text(encoding="utf-8-sig")
    assert not re.search(r"'COREPACK_HOME'\s*=\s*\(Join-Path", text), "不應再把 COREPACK_HOME 指到專案內"
    assert "COREPACK_ENABLE_NETWORK" in text, "pnpm 檢查期間應禁止 corepack 連網"
    assert re.search(r"corepack install -g pnpm@\$PnpmVersion", text) and re.search(r"\$PnpmVersion = '10\.\d+\.\d+'", text), "應建議固定的 pnpm 10.x 版本"
    assert "DriveFormat" in text and "NTFS" in text, "應檢查專案磁碟的檔案系統"
    assert "Removable" in text and "$Unregister" in text, "抽取式磁碟應提醒;應提供 -Unregister"
    m = re.search(r"\$vars = @\{(.*?)\n\}", text, re.S)
    assert m and "SIMOSA_BRM_ROOT" not in m.group(1), "SIMOSA_BRM_ROOT 不應在寫入使用者層級的 $vars 內"
    assert (PROJECT_ROOT / "tools" / "dev-shell.cmd").read_bytes().count(b"setup-windows.ps1' -SessionOnly") == 1, "應有 tools/dev-shell.cmd(開啟已設定好的終端機)"


def _tracked_ps1() -> list[Path]:
    out = subprocess.run(["git", "ls-files", "-co", "--exclude-standard", "*.ps1"], cwd=PROJECT_ROOT, capture_output=True, text=True, check=True).stdout
    return [PROJECT_ROOT / line for line in out.splitlines() if line.strip()]


def test_every_ps1_that_runs_dotnet_or_godot_redirects_temp_into_project():
    """審查(第二十八輪):會執行 dotnet build/publish/test/run/tool 或 Godot 的 .ps1,必須自己把暫存與快取導回專案內
    (Set-LaunchProjectEnv,或直接設定 $env:TEMP)— setup-windows.ps1 不改使用者層級的 TEMP,不能假設使用者先執行過它。"""
    runs = re.compile(r"(?im)^(?!\s*#).*(\bdotnet\b['\"]?\s*(@\()?\s*['\"]?(build|publish|test|run|tool)\b|&\s*\$Godot\b|-File\s+\$editor\b)")
    missing = []
    for path in _tracked_ps1():
        text = path.read_text(encoding="utf-8-sig")
        if path.name.endswith(".tests.ps1"):
            continue
        if runs.search(text) and "Set-LaunchProjectEnv" not in text and "$env:TEMP =" not in text:
            missing.append(path.relative_to(PROJECT_ROOT).as_posix())
    assert not missing, f"以下腳本會執行 dotnet / Godot 卻沒有把暫存導回專案內:{missing}"


def test_check_local_data_watches_the_real_godot_user_dir():
    """check-local-data.ps1 檢查的視景使用者資料夾名稱,必須與 project.godot 的 custom_user_dir_name 相同(曾寫成另一個名稱而漏報);
    啟動腳本對匯出檔與編輯器兩種啟動方式都要導向 APPDATA(Invoke-LaunchWithGodotUserDir)。"""
    godot = (PROJECT_ROOT / "src" / "Visual.Godot" / "project.godot").read_text(encoding="utf-8")
    m = re.search(r'config/custom_user_dir_name="([^"]+)"', godot)
    assert m and 'config/use_custom_user_dir=true' in godot
    check = (PROJECT_ROOT / "tools" / "check-local-data.ps1").read_text(encoding="utf-8-sig")
    assert f"(Join-Base $roaming '{m.group(1)}')" in check, f"check-local-data.ps1 應檢查 %APPDATA%\\{m.group(1)}"
    ship = (PROJECT_ROOT / "tools" / "launch" / "start-ship-laptop.ps1").read_text(encoding="utf-8-sig")
    block = ship[ship.index("Invoke-LaunchWithGodotUserDir $root {"):]
    assert block.index("-File $exe") > 0 and block.index("-File $editor") > 0, "匯出檔與編輯器兩個分支都應在 Invoke-LaunchWithGodotUserDir 之內"
    assert "$env:APPDATA =" not in ship, "start-ship-laptop.ps1 不應自行改 APPDATA(由 Invoke-LaunchWithGodotUserDir 設定並還原)"


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
