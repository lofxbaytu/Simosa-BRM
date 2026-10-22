#!/usr/bin/env bash
# 啟動 3D 視景(Linux 開發機;參數同 visual-run.ps1):tools/visual-run.sh --view=bridge --monitor=0 [--quality=low|mid|high|auto] [--ws=…] [--area=…]
# 低品質以 --rendering-method gl_compatibility 啟動;Godot 使用者資料經 XDG_* 留在 .cache/godot/。
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT="${GODOT:-${SIMOSA_GODOT:-}}"
if [[ -z "$GODOT" ]]; then
  for c in "$ROOT"/.cache/godot/*/Godot_v4.*mono_linux.x86_64; do [[ -x "$c" ]] && GODOT="$c" && break; done
fi
[[ -x "${GODOT:-}" ]] || { echo "找不到 Godot;設定 GODOT=<路徑>" >&2; exit 2; }
export XDG_DATA_HOME="$ROOT/.cache/godot/xdg/data" XDG_CONFIG_HOME="$ROOT/.cache/godot/xdg/config" XDG_CACHE_HOME="$ROOT/.cache/godot/xdg/cache"
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME"
PROJ="$ROOT/src/Visual.Godot"
[[ -f "$PROJ/.godot/mono/temp/bin/Debug/SimosaBRM.Visual.Godot.dll" ]] || dotnet build "$PROJ/Visual.Godot.csproj" -nologo -v q
ENGINE=(--path "$PROJ")
for a in "$@"; do [[ "$a" == "--quality=low" ]] && ENGINE+=(--rendering-method gl_compatibility); done
exec "$GODOT" "${ENGINE[@]}" -- "--data-dir=$ROOT" "$@"
