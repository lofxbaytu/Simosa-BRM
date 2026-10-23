#!/usr/bin/env bash
# 發行組裝(Linux/容器;Windows 版 build-release.ps1 同參數):規劃書第 5.4–5.5 節(船上部署)、第 11.2 節(資料夾結構)、第 12.1 節 M4 交付(安裝程式)。
#   1) dotnet build SimosaBRM.sln -c Release;2) 網頁 pnpm build;3) dotnet publish(framework-dependent,需 .NET 8 執行階段)
#      SimCore.Host → app/simcore、Sensors.Radar.Host → app/radar、simosa-web → app/web-server、simosa-pkg → tools/updater
#      (simosa-pkg 放在 app/ 之外:套用軟體更新包時 app/ 整個被替換,執行中的更新工具不可位於其中;docs/design/packaging-sync.md 第 5 節);
#      Gateway.Nmea 為類別庫,隨 SimCore.Host 一併發布;Python 校正工具不打包,附說明於 docs/;
#   4) 網頁 → app/web、啟動腳本 → app/launch、data/(排除 ships/*/confidential/)、contracts/、docs/、config/ 範例;
#   5) Godot 視景:有 Godot 與匯出範本時 --export-release → app/visual/,否則略過並記入清單註記;
#   6) 以 simosa-pkg pack 把 app/ 包成簽章更新包 packages/simosa-<版本>-software.sbupd(開發用 PEM 金鑰 build/release/dev-keys/,僅供測試);
#   7) 發行清單 manifest.json(版本、git 版次、每檔 SHA-256、元件版本)→ release verify、更新包 verify(暫時船上根目錄)。
# 用法:tools/build-release.sh [--version <1–4 段數字>] [--git <版次>] [--branch <分支>] [--rid linux-x64|win-x64] [--out build/release]
#       [--skip-web] [--skip-godot] [--godot <Godot 執行檔>] [--no-package] [--zip]
# 所有輸出留在專案內(build/release/、.cache/);不執行任何 git 指令(版次由 --git 或直接讀 .git/HEAD)。
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
VERSION=""; GIT_REV=""; GIT_BRANCH=""; RID="linux-x64"; OUT_BASE="build/release"; SKIP_WEB=0; SKIP_GODOT=0; GODOT="${GODOT:-${SIMOSA_GODOT:-}}"; PACKAGE=1; ZIP=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --git) GIT_REV="$2"; shift 2 ;;
    --branch) GIT_BRANCH="$2"; shift 2 ;;
    --rid) RID="$2"; shift 2 ;;
    --out) OUT_BASE="$2"; shift 2 ;;
    --skip-web) SKIP_WEB=1; shift ;;
    --skip-godot) SKIP_GODOT=1; shift ;;
    --godot) GODOT="$2"; shift 2 ;;
    --no-package) PACKAGE=0; shift ;;
    --zip) ZIP=1; shift ;;
    -h|--help) sed -n 2,12p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
[[ -n "$VERSION" ]] || VERSION="0.$(date -u +%Y%m%d).$(date -u +%H%M | sed 's/^0*//;s/^$/0/')"
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){0,3}$ ]] || { echo "--version 須為 1–4 段數字(與更新包版本規則相同):$VERSION" >&2; exit 2; }

# git 版次:不執行 git,直接讀 .git/HEAD → refs/heads/<分支> 或 packed-refs
if [[ -z "$GIT_REV" && -f .git/HEAD ]]; then
  head="$(cat .git/HEAD)"
  if [[ "$head" == ref:* ]]; then
    ref="${head#ref: }"; GIT_BRANCH="${GIT_BRANCH:-${ref#refs/heads/}}"
    if [[ -f ".git/$ref" ]]; then GIT_REV="$(cat ".git/$ref")"
    elif [[ -f .git/packed-refs ]]; then GIT_REV="$(awk -v r="$ref" '$2 == r { print $1 }' .git/packed-refs | head -1)"; fi
  else GIT_REV="$head"; fi
fi

# 工具鏈與快取(全部留在專案內)
if ! command -v dotnet >/dev/null && [[ -f /etc/profile.d/dotnet.sh ]]; then . /etc/profile.d/dotnet.sh; fi
command -v dotnet >/dev/null || { echo "找不到 dotnet SDK" >&2; exit 2; }
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}" NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget/packages}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export SIMOSA_BRM_ROOT="$ROOT"
OUT="$ROOT/$OUT_BASE/$VERSION"
KEYS="$ROOT/$OUT_BASE/dev-keys"
PKG_DIR="$OUT/packages"
NOTES=()
COMPONENTS=()
note() { echo "  註記:$1"; NOTES+=("$1"); }
step() { echo; echo "== $1"; }

echo "Simosa BRM 發行組裝 $VERSION(git ${GIT_REV:-?} ${GIT_BRANCH:-};$RID)→ $OUT"
[[ -d "$OUT" ]] && { echo "  清除既有 $OUT"; rm -rf "$OUT"; }
mkdir -p "$OUT/app" "$OUT/tools" "$PKG_DIR"

step "1/7 dotnet build SimosaBRM.sln -c Release"
dotnet build SimosaBRM.sln -c Release -nologo -v q -clp:ErrorsOnly

step "2/7 網頁 pnpm build(src/Instruments.Web → build/web/instruments)"
WEB_VERSION="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' src/Instruments.Web/package.json | head -1)"
if [[ $SKIP_WEB == 1 ]]; then note "網頁未重新建置(--skip-web),沿用 build/web/instruments"
elif command -v pnpm >/dev/null; then (cd src/Instruments.Web && pnpm build >/dev/null) && echo "  pnpm build 完成"
else note "找不到 pnpm,網頁沿用既有 build/web/instruments"; fi
[[ -f build/web/instruments/index.html ]] || { echo "沒有網頁輸出 build/web/instruments/index.html" >&2; exit 1; }

step "3/7 dotnet publish(framework-dependent,$RID)"
publish() { # <專案路徑> <目的地>
  dotnet publish "$1" -c Release -r "$RID" --self-contained false -o "$2" -nologo -v q
  local n; n="$(find "$2" -type f | wc -l)"; echo "  $(basename "$1" .csproj) → ${2#"$OUT/"}($n 個檔案)"
}
publish src/SimCore.Host/SimCore.Host.csproj "$OUT/app/simcore"
publish src/Sensors.Radar.Host/Sensors.Radar.Host.csproj "$OUT/app/radar"
publish tools/launch/StaticWeb/StaticWeb.csproj "$OUT/app/web-server"
publish src/Tools.Packaging.Cli/Tools.Packaging.Cli.csproj "$OUT/tools/updater"
find "$OUT/app" "$OUT/tools" -name '*.pdb' -delete
PKG="dotnet $OUT/tools/updater/simosa-pkg.dll"
COMPONENTS+=("simcore=$VERSION=app/simcore" "radar=$VERSION=app/radar" "web-server=$VERSION=app/web-server" "simosa-pkg=$VERSION=tools/updater" "gateway-nmea=$VERSION=app/simcore=類別庫,隨 SimCore.Host 發布")

step "4/7 網頁、啟動腳本、資料、契約、文件、設定範例"
cp -r build/web/instruments "$OUT/app/web"
COMPONENTS+=("web=$WEB_VERSION=app/web")
mkdir -p "$OUT/app/launch"
cp tools/launch/*.sh tools/launch/*.ps1 "$OUT/app/launch/"
mkdir -p "$OUT/data"
tar -C data --exclude='./ships/*/confidential' --exclude='./ships/*/confidential/*' -cf - . | tar -C "$OUT/data" -xf -
[[ -z "$(find "$OUT/data" -type d -name confidential)" ]] || { echo "data/ 複製後仍含 confidential/" >&2; exit 1; }
PARAM_VERSION="$(sed -n 's/^version: *"\([^"]*\)".*/\1/p' data/parameters/company-procedures.yaml | head -1)"
COMPONENTS+=("data=${PARAM_VERSION:-$VERSION}=data=參數集 company-procedures.yaml 版次;ships/*/confidential 已排除")
mkdir -p "$OUT/contracts" && cp src/Contracts/* "$OUT/contracts/"
COMPONENTS+=("contracts=$VERSION=contracts")
mkdir -p "$OUT/docs/design" "$OUT/docs/ops" "$OUT/docs/驗收" "$OUT/docs/訓練" "$OUT/docs/components"
cp docs/Simosa-BRM-操船模擬器規劃書.md README.md "$OUT/docs/"
cp docs/design/*.md "$OUT/docs/design/"
[[ -d docs/ops ]] && cp docs/ops/*.md "$OUT/docs/ops/"
[[ -d docs/驗收 ]] && cp docs/驗收/*.md "$OUT/docs/驗收/"
[[ -d docs/訓練 ]] && cp docs/訓練/*.md "$OUT/docs/訓練/"
cp src/Tools.Calibration/README.md "$OUT/docs/components/校正工具-Tools.Calibration.md"
cp src/Instruments.Web/README.md "$OUT/docs/components/網頁儀器-Instruments.Web.md"
cp src/Instruments.Web/RADAR.md "$OUT/docs/components/雷達PPI-RADAR.md"
cp src/SimCore/README.md "$OUT/docs/components/模擬核心-SimCore.md"
cp src/Contracts/README.md "$OUT/docs/components/契約-Contracts.md"
COMPONENTS+=("docs=$VERSION=docs")
mkdir -p "$OUT/config" && cp config/*.json config/README.md "$OUT/config/"
note "Python 校正工具(src/Tools.Calibration)不隨發行版散布;係數檔 data/ships/*/coefficients.*.json 已由該工具產生並隨 data/ 發布,說明見 docs/components/校正工具-Tools.Calibration.md"
cat > "$OUT/README.txt" <<TXT
Simosa BRM 發行版 $VERSION($RID,framework-dependent:需 .NET 8 執行階段)git ${GIT_REV:-?} ${GIT_BRANCH:-}
app/simcore      SimCore.Host(50 Hz 核心、WebSocket 8765、UDP 多播、NMEA)   app/radar        Sensors.Radar.Host(雷達/ARPA 8766)
app/web          網頁:index.html 儀器、instructor.html 教官站(?mode=simple 簡化教官模式)、radar.html、report.html
app/web-server   simosa-web 靜態網頁伺服                                  app/launch       start-ship-laptop / start-instructor-laptop / stop-all
app/visual       Godot 視景匯出檔(有匯出範本時)                           tools/updater    simosa-pkg(更新包、紀錄包、直連同步;放在 app/ 之外)
data/            船模、情境、區域、參數集、視景對照(不含 ships/*/confidential)   contracts/       JSON Schema   config/  設定範例(無金鑰)
docs/            規劃書、設計文件、ops/安裝與啟動.md、驗收/需求追溯表.md     packages/        簽章更新包(開發金鑰簽章,僅供測試)
manifest.json    發行清單(每檔 SHA-256):安裝前 simosa-pkg release verify --dir <本目錄>
安裝步驟:docs/ops/安裝與啟動.md
TXT

step "5/7 Godot 視景匯出"
if [[ $SKIP_GODOT == 1 ]]; then
  note "視景未匯出(--skip-godot);以 Godot 編輯器 --path src/Visual.Godot 或 tools/visual-run.* 執行"; COMPONENTS+=("visual=$VERSION==略過:--skip-godot")
else
  if [[ -z "$GODOT" ]]; then for c in "$ROOT"/.cache/godot/*/Godot_v4.*mono_linux.x86_64 "$ROOT"/.cache/godot/*/Godot_v4.*mono_win64.exe; do [[ -f "$c" ]] && { GODOT="$c"; break; }; done; fi
  TPL_DIR="$ROOT/.cache/godot/xdg/data/godot/export_templates/4.5.1.stable.mono"
  if [[ "$RID" == win-x64 ]]; then PRESET="Windows Desktop"; TPL="$TPL_DIR/windows_release_x86_64.exe"; VIS_OUT="$OUT/app/visual/SimosaBRM.Visual.exe"
  else PRESET="Linux"; TPL="$TPL_DIR/linux_release.x86_64"; VIS_OUT="$OUT/app/visual/SimosaBRM.Visual.x86_64"; fi
  if [[ -z "$GODOT" || ! -f "$GODOT" ]]; then
    note "視景未匯出:找不到 Godot 4.5.1 .NET 執行檔(--godot 或 GODOT 環境變數;.cache/godot/<版本>/)"; COMPONENTS+=("visual=$VERSION==略過:無 Godot")
  elif [[ ! -f "$TPL" ]]; then
    note "視景未匯出:缺匯出範本 $TPL(取得 Godot_v4.5.1-stable_mono_export_templates.tpz 後放入該目錄;docs/design/visual-client.md 第 9 節);船上以 Godot 編輯器 --path src/Visual.Godot 執行"
    COMPONENTS+=("visual=$VERSION==略過:缺匯出範本")
  else
    export XDG_DATA_HOME="$ROOT/.cache/godot/xdg/data" XDG_CONFIG_HOME="$ROOT/.cache/godot/xdg/config" XDG_CACHE_HOME="$ROOT/.cache/godot/xdg/cache"
    mkdir -p "$(dirname "$VIS_OUT")" build/visual
    dotnet build src/Visual.Godot/Visual.Godot.csproj -c Release -nologo -v q
    "$GODOT" --headless --path src/Visual.Godot --import > build/visual/release-import.log 2>&1 || true
    if "$GODOT" --headless --path src/Visual.Godot --export-release "$PRESET" "$VIS_OUT" > build/visual/release-export.log 2>&1 && [[ -f "$VIS_OUT" ]]; then
      echo "  視景已匯出 → ${VIS_OUT#"$OUT/"}"; COMPONENTS+=("visual=$VERSION=app/visual=已匯出($PRESET)")
    else note "視景匯出失敗(見 build/visual/release-export.log)"; COMPONENTS+=("visual=$VERSION==匯出失敗"); rm -rf "$OUT/app/visual"; fi
  fi
fi

step "6/7 簽章更新包(app/ → packages/;開發用 PEM 金鑰,僅供測試)"
if [[ $PACKAGE == 1 ]]; then
  mkdir -p "$KEYS"
  if [[ ! -f "$KEYS/dev-primary.key.pem" ]]; then
    $PKG keygen --out "$KEYS/dev-primary" --name "開發用主金鑰(僅供測試)" --role primary --holder "開發團隊" 2>/dev/null
    $PKG keygen --out "$KEYS/dev-backup" --name "開發用備援金鑰(僅供測試)" --role backup --holder "開發團隊" 2>/dev/null
    cat > "$KEYS/README.txt" <<TXT
本目錄的 PEM 金鑰由 tools/build-release.* 產生,僅供開發/測試:正式更新包必須以訓練負責人的硬體金鑰(--cert)簽章(規劃書第 5.5 節、docs/design/packaging-sync.md 第 2、8 節)。
不得放入系統映像、船上信任清單(config/trusted-keys/)或儲存庫。
TXT
  fi
  SW_PKG="$PKG_DIR/simosa-$VERSION-software.sbupd"
  $PKG pack --kind software --version "$VERSION" --summary "Simosa BRM 發行版 $VERSION 軟體(app/)" --change "git ${GIT_REV:-?} ${GIT_BRANCH:-}" --change "執行階段 $RID" \
       --dir "app=$OUT/app" --key "$KEYS/dev-primary.key.pem" --out "$SW_PKG" 2>/dev/null | sed 's/^/  /'
  COMPONENTS+=("software-package=$VERSION=packages/$(basename "$SW_PKG")=開發金鑰簽章,僅供測試")
  note "packages/*.sbupd 以 build/release/dev-keys/ 的開發用 PEM 金鑰簽章,僅供測試;正式派送以硬體金鑰 --cert 重新 pack"
else note "未產生更新包(--no-package)"; fi

step "7/7 發行清單與核對"
MF_ARGS=(release manifest --dir "$OUT" --version "$VERSION" --runtime "$RID")
[[ -n "$GIT_REV" ]] && MF_ARGS+=(--git "$GIT_REV")
[[ -n "$GIT_BRANCH" ]] && MF_ARGS+=(--branch "$GIT_BRANCH")
for c in "${COMPONENTS[@]}"; do MF_ARGS+=(--component "$c"); done
for n in "${NOTES[@]}"; do MF_ARGS+=(--note "$n"); done
$PKG "${MF_ARGS[@]}" | sed 's/^/  /'
$PKG release verify --dir "$OUT" | sed 's/^/  /'
if [[ $PACKAGE == 1 ]]; then
  VR="$ROOT/$OUT_BASE/.verify-$VERSION"
  rm -rf "$VR"; mkdir -p "$VR/config/trusted-keys"
  cp "$KEYS/dev-primary.pub.pem" "$VR/config/trusted-keys/dev-primary.pem"
  printf '{ "role": "ship", "shipId": "FSB1", "deviceId": "FSB1-VERIFY" }\n' > "$VR/config/packaging.json"
  $PKG verify "$SW_PKG" --root "$VR" | sed 's/^/  /'
  rm -rf "$VR"
fi
if [[ $ZIP == 1 ]]; then
  ZIPF="$ROOT/$OUT_BASE/simosa-brm-$VERSION-$RID.zip"
  (cd "$ROOT/$OUT_BASE" && python3 -I -c "import shutil,sys; shutil.make_archive(sys.argv[1][:-4], 'zip', '.', sys.argv[2])" "$ZIPF" "$VERSION")
  echo "  壓縮檔 $ZIPF($(du -h "$ZIPF" | cut -f1))"
fi
echo
echo "完成:$OUT($(du -sh "$OUT" | cut -f1));安裝步驟見 docs/ops/安裝與啟動.md"
