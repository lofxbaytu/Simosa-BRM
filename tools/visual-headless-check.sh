#!/usr/bin/env bash
# 3D 視景無頭驗證(docs/design/visual-client.md 第 9 節;規劃書第 14 章效能驗收的功能部分):
# 1) dotnet build 視景專案(Godot.NET.Sdk)、2) Godot --headless --import 匯入專案、3) --headless 執行煙霧場景
#    (載入 E05 示意區域與假狀態序列,跑 N 幀,輸出每幀時間與物件數到 build/visual/smoke.json)。
# headless 無 GPU:幀時間只作功能驗證,不代表內顯機幀率(實機量測步驟見設計文件第 10 節)。
# 用法:tools/visual-headless-check.sh [--godot <Godot 執行檔>] [--frames N] [--quality low|mid|high]
# 所有快取與輸出留在專案內:.cache/godot/(Godot 編輯器/使用者資料,經 XDG_* 環境變數)、.cache/nuget、src/Visual.Godot/.godot/、build/visual/。
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT="${GODOT:-${SIMOSA_GODOT:-}}"
FRAMES=200
QUALITY=low
EXPORT=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --godot) GODOT="$2"; shift 2 ;;
    --frames) FRAMES="$2"; shift 2 ;;
    --quality) QUALITY="$2"; shift 2 ;;
    --export) EXPORT=1; shift ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
if [[ -z "$GODOT" ]]; then
  for c in "$ROOT"/.cache/godot/*/Godot_v4.*mono_linux.x86_64 "$ROOT"/.cache/godot/*/Godot*.x86_64; do
    [[ -x "$c" ]] && GODOT="$c" && break
  done
fi
if [[ -z "$GODOT" || ! -x "$GODOT" ]]; then
  echo "找不到 Godot 4.5 .NET 執行檔;以 --godot <路徑> 或環境變數 GODOT 指定,或放在 .cache/godot/<版本>/ 下" >&2
  exit 2
fi
# Godot 的編輯器/使用者資料目錄 → 專案內(Linux 依 XDG;Windows 版本見 visual-headless-check.ps1)
export XDG_DATA_HOME="$ROOT/.cache/godot/xdg/data"
export XDG_CONFIG_HOME="$ROOT/.cache/godot/xdg/config"
export XDG_CACHE_HOME="$ROOT/.cache/godot/xdg/cache"
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME" "$ROOT/build/visual"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}"
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
PROJ="$ROOT/src/Visual.Godot"
LOG="$ROOT/build/visual"

echo "== Godot:$GODOT"
"$GODOT" --headless --version | tail -1
echo "== dotnet build(Debug;Godot 從 .godot/mono/temp/bin/Debug 載入)"
dotnet build "$PROJ/Visual.Godot.csproj" -nologo -v q
echo "== 匯入專案(--import)"
"$GODOT" --headless --path "$PROJ" --import > "$LOG/import.log" 2>&1 || true
grep -iE "error|fail" "$LOG/import.log" | grep -v "no error" || true
echo "== 煙霧場景($FRAMES 幀,品質 $QUALITY)"
set +e
"$GODOT" --headless --path "$PROJ" -- --smoke="$FRAMES" --quality="$QUALITY" --data-dir="$ROOT" --smoke-out="$LOG/smoke.json" > "$LOG/smoke.log" 2>&1
RC=$?
set -e
grep -E "^\[visual\]|^\[quality\]|ERROR|Unhandled" "$LOG/smoke.log" || true
if [[ -f "$LOG/smoke.json" ]]; then
  python3 - "$LOG/smoke.json" <<'PY' || true
import json, sys
r = json.load(open(sys.argv[1]))
print(f"smoke.json:ok={r['ok']} frames={len(r['frames'])} mean={r['meanFrameTime_ms']:.2f} ms max={r['maxFrameTime_ms']:.2f} ms nodes={r['frames'][-1]['nodes'] if r['frames'] else 0} targets={r['frames'][-1]['targetsDrawn'] if r['frames'] else 0} harbourMeshes={r['harbourTriangles']} tri areas={r['areas']} renderer={r.get('renderer')} godot={r.get('godotVersion')}")
PY
fi
if [[ "$EXPORT" == "1" ]]; then
  echo "== 匯出(需匯出範本;無法取得時為待辦)"
  "$GODOT" --headless --path "$PROJ" --export-release "Linux" "$ROOT/build/visual/export/SimosaBRM.Visual.x86_64" > "$LOG/export.log" 2>&1 || echo "匯出失敗(通常為缺少匯出範本),見 $LOG/export.log"
fi
echo "== 結束碼 $RC(0 = 煙霧測試 ok)"
exit $RC
