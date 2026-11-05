#!/usr/bin/env bash
# PowerShell 腳本的測試與靜態檢查(Linux 容器/CI 與 Windows 開發機的 Git Bash 皆可;船上不需要):
#   1) tools/pscheck.py:全部 .ps1 的括號/字串/here-string 配對(純 Python,不需 pwsh)
#   2) tools/installer/check-iss.py:Windows 安裝程式腳本 tools/installer/SimosaBRM.iss 的區段與必要鍵(純 Python)
#   3) pwsh 解析器:全部 .ps1 以 System.Management.Automation.Language.Parser 解析,無語法錯誤
#   4) tools/launch/tests/launch-common.tests.ps1(啟動腳本、stop-all、WebView2 外殼案例)
#   5) tools/build-release.ps1 -WhatIf -SkipGodot -Installer(參數與工具鏈檢查,不建置、不寫檔)
#   6) tools/soak-test.ps1 -WhatIf(長時間穩定性測試的 Windows 版:參數、工具鏈與建置輸出檢查,不啟動程序、不寫檔;實跑以 tools/soak-test.sh 為準)
# pwsh 來源:.cache/dotnet-tools/pwsh(dotnet 全域工具版 PowerShell,固定版 PWSH_VERSION,留在專案內)→ PATH 的 pwsh;
#   沒有時只做 1)、2) 並回報略過(--install 以 dotnet tool install 安裝到 .cache/dotnet-tools;NuGet 走 nuget.config 與 .cache/nuget)。
#   PowerShell 7.4 以 .NET 8 建置;機器只有較新的執行階段(例如容器的 .NET 10)時以 DOTNET_ROLL_FORWARD=Major 執行。
# 用法:tools/run-ps-tests.sh [--install] [--require-pwsh] [--only pscheck|iss|syntax|tests|whatif|soak] [--list]
#   所有輸出留在專案內(build/test-tmp/launch-ps/、build/tmp/ps-tests/);結束碼 0 = 全部通過(略過不算失敗,--require-pwsh 時無 pwsh 為 2)。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
PWSH_VERSION="${SIMOSA_PWSH_VERSION:-7.4.22}"   # 與 tools/setup-windows.ps1 -InstallPwsh 相同的固定版(LTS,.NET 8)
TOOLS="$ROOT/.cache/dotnet-tools"
TMP="$ROOT/build/tmp/ps-tests"; mkdir -p "$TMP"
INSTALL=0; REQUIRE=0; ONLY=""; LIST=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) INSTALL=1; shift ;;
    --require-pwsh) REQUIRE=1; shift ;;
    --only) ONLY="$2"; shift 2 ;;
    --list) LIST=1; shift ;;
    -h|--help) sed -n 2,12p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
STEPS=(pscheck iss syntax tests whatif soak)
if [[ $LIST == 1 ]]; then printf '%s\n' "${STEPS[@]}"; exit 0; fi

# dotnet 與快取(全部留在專案內);dotnet 全域工具的 apphost 需要 DOTNET_ROOT 才找得到執行階段
if ! command -v dotnet >/dev/null 2>&1 && [[ -f /etc/profile.d/dotnet.sh ]]; then . /etc/profile.d/dotnet.sh; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}" NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget/packages}"
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
if [[ -z "${DOTNET_ROOT:-}" ]] && command -v dotnet >/dev/null 2>&1; then DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"; export DOTNET_ROOT; fi

find_pwsh() {
  local c
  for c in "$TOOLS/pwsh" "$TOOLS/pwsh.exe"; do [[ -x "$c" ]] && { echo "$c"; return 0; }; done
  command -v pwsh 2>/dev/null && return 0
  return 1
}
install_pwsh() {
  command -v dotnet >/dev/null 2>&1 || { echo "找不到 dotnet SDK,無法安裝 PowerShell 全域工具" >&2; return 1; }
  mkdir -p "$TOOLS"
  echo "安裝 PowerShell $PWSH_VERSION(dotnet tool → $TOOLS;NuGet 快取 $NUGET_PACKAGES)…"
  dotnet tool install PowerShell --tool-path "$TOOLS" --version "$PWSH_VERSION" --configfile "$ROOT/nuget.config"
}

PWSH="$(find_pwsh || true)"
if [[ -z "$PWSH" && $INSTALL == 1 ]]; then install_pwsh && PWSH="$(find_pwsh || true)"; fi
if [[ -n "$PWSH" ]]; then
  PWSH_VER="$("$PWSH" -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || true)"
  if [[ -z "$PWSH_VER" ]]; then echo "pwsh($PWSH)無法執行(需 .NET 執行階段;DOTNET_ROOT=${DOTNET_ROOT:-未設})" >&2; PWSH=""; fi
fi
if [[ -z "$PWSH" ]]; then
  echo "沒有 pwsh:只做靜態檢查(pscheck、iss)。安裝:tools/run-ps-tests.sh --install,或"
  echo "  dotnet tool install PowerShell --tool-path .cache/dotnet-tools --version $PWSH_VERSION --configfile nuget.config(Windows:tools\\setup-windows.ps1 -InstallPwsh)"
  [[ $REQUIRE == 1 ]] && exit 2
else
  echo "pwsh $PWSH_VER:$PWSH(.NET $(dotnet --version 2>/dev/null || echo ?),DOTNET_ROLL_FORWARD=$DOTNET_ROLL_FORWARD)"
fi

PS1_FILES=(tools/*.ps1 tools/launch/*.ps1 tools/launch/tests/*.ps1)
PASS=0; FAIL=0; SKIP=0; FAILED=()
run_step() {  # run_step <名稱> <需要 pwsh 0|1> <指令…>
  local name="$1" need="$2"; shift 2
  [[ -z "$ONLY" || "$ONLY" == "$name" ]] || return 0
  echo "== $name"
  if [[ $need == 1 && -z "$PWSH" ]]; then echo "    略過(無 pwsh)"; SKIP=$((SKIP + 1)); return 0; fi
  if "$@"; then PASS=$((PASS + 1)); echo "    通過"; else FAIL=$((FAIL + 1)); FAILED+=("$name"); echo "    失敗"; fi
}

step_pscheck() { python3 -I tools/pscheck.py "${PS1_FILES[@]}" | sed 's/^/    /'; [[ ${PIPESTATUS[0]} == 0 ]]; }
step_iss() { python3 -I tools/installer/check-iss.py tools/installer/SimosaBRM.iss | sed 's/^/    /'; [[ ${PIPESTATUS[0]} == 0 ]]; }
step_syntax() {
  # 檔案清單以環境變數傳入(-Command 之後的引數會被接到指令字串,不是 $args)
  SIMOSA_PS1_FILES="$(printf '%s\n' "${PS1_FILES[@]}")" "$PWSH" -NoProfile -Command '
    $bad = 0
    foreach ($f in ($env:SIMOSA_PS1_FILES -split "\n" | Where-Object { $_ })) {
      $tokens = $null; $errors = $null
      [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f).Path, [ref]$tokens, [ref]$errors) | Out-Null
      if ($errors.Count -gt 0) { $bad++; Write-Host "    FAIL $f"; foreach ($e in $errors) { Write-Host "        $($e.Extent.StartLineNumber): $($e.Message)" } }
      else { Write-Host "    ok   $f" }
    }
    exit $bad'
}
step_tests() {
  local out="$TMP/launch-common.tests.out"
  "$PWSH" -NoProfile -File tools/launch/tests/launch-common.tests.ps1 > "$out" 2>&1; local code=$?
  grep -v '^  啟動 tool\|^  外殼 \|^  啟動 shell-' "$out" | sed 's/^/    /'
  local summary; summary="$(grep -E '^通過 [0-9]+,失敗 [0-9]+' "$out" | tail -1)"
  [[ $code == 0 && -n "$summary" && "$summary" =~ 失敗\ 0 ]]
}
step_whatif() {
  local out="$TMP/build-release.whatif.out"
  # 專屬版號 0.0.0 並前後清理:避免機器上先前的真實發行組裝(例如 0.0.1)讓斷言誤判(審查第二十三輪)
  rm -rf build/release/0.0.0
  "$PWSH" -NoProfile -File tools/build-release.ps1 -WhatIf -SkipGodot -Installer -Version 0.0.0 > "$out" 2>&1; local code=$?
  sed 's/^/    /' "$out"
  [[ $code == 0 ]] || return 1
  grep -q '^WhatIf:' "$out" && grep -q '8/8 安裝程式' "$out" && grep -q 'tools\\installer\\SimosaBRM.iss' "$out" || { echo "    -WhatIf 輸出缺少計畫內容"; return 1; }
  [[ ! -d build/release/0.0.0 ]] || { echo "    -WhatIf 不應建立 build/release/0.0.0"; rm -rf build/release/0.0.0; return 1; }
  # 參數錯誤仍須以非 0 結束
  if "$PWSH" -NoProfile -File tools/build-release.ps1 -WhatIf -Version bad.version > "$TMP/build-release.whatif-bad.out" 2>&1; then echo "    -Version 不合法時應失敗"; return 1; fi
  grep -q '1–4 段數字' "$TMP/build-release.whatif-bad.out"
}

run_step pscheck 0 step_pscheck
run_step iss 0 step_iss
run_step syntax 1 step_syntax
run_step tests 1 step_tests
step_soak() {
  local out="$TMP/soak-test.whatif.out"
  "$PWSH" -NoProfile -File tools/soak-test.ps1 -WhatIf -Minutes 1 -Clients 2 -SkipBuild -Run ps-whatif > "$out" 2>&1; local code=$?
  sed 's/^/    /' "$out"
  [[ $code == 0 ]] || return 1
  grep -q '^WhatIf:' "$out" && grep -q 'clients.mjs ×2' "$out" && grep -q 'report.mjs' "$out" || { echo "    -WhatIf 輸出缺少計畫內容"; return 1; }
  [[ ! -d build/soak/ps-whatif ]] || { echo "    -WhatIf 不應建立 build/soak/ps-whatif"; rm -rf build/soak/ps-whatif; return 1; }
  # 參數錯誤仍須以非 0 結束(情境不存在)
  if "$PWSH" -NoProfile -File tools/soak-test.ps1 -WhatIf -Scenario does-not-exist > "$TMP/soak-test.whatif-bad.out" 2>&1; then echo "    情境不存在時應失敗"; return 1; fi
  grep -q '找不到' "$TMP/soak-test.whatif-bad.out"
}

run_step whatif 1 step_whatif
run_step soak 1 step_soak
echo "通過 $PASS,失敗 $FAIL,略過 $SKIP${FAILED[*]:+(${FAILED[*]})}"
[[ $FAIL == 0 ]]
