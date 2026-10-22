#!/usr/bin/env bash
# 啟動 3D 視景(Linux 開發機;參數同 visual-run.ps1):tools/visual-run.sh --view=bridge --monitor=0 [--quality=low|mid|high|auto] [--ws=…] [--area=…]
# 低品質以 --rendering-method gl_compatibility 啟動;auto(預設)讀 build/visual/quality.json:manualOverride 為 low,或無覆寫且 lastAutoTier 為 low
# → Compatibility(與 Visual.Shared QualityPersistence.StartupRenderer 同一規則;visual-run.ps1 亦同)。Godot 使用者資料經 XDG_* 留在 .cache/godot/。
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
QUALITY=auto
for a in "$@"; do case "$a" in --quality=*) QUALITY="${a#--quality=}" ;; esac; done
COMPAT=0
if [[ "$QUALITY" == "low" ]]; then
  COMPAT=1
elif [[ "$QUALITY" == "auto" && -f "$ROOT/build/visual/quality.json" ]]; then
  QJ="$ROOT/build/visual/quality.json"
  OV="$(sed -n 's/.*"manualOverride" *: *"\([^"]*\)".*/\1/p' "$QJ" | head -1 | tr '[:upper:]' '[:lower:]')"
  LA="$(sed -n 's/.*"lastAutoTier" *: *"\([^"]*\)".*/\1/p' "$QJ" | head -1 | tr '[:upper:]' '[:lower:]')"
  if [[ "$OV" == "low" || ( ( -z "$OV" || "$OV" == "auto" ) && "$LA" == "low" ) ]]; then COMPAT=1; fi
  echo "quality.json:manualOverride=${OV:-} lastAutoTier=${LA:-} → 渲染器 $([[ $COMPAT == 1 ]] && echo Compatibility || echo Forward+)"
fi
[[ "$COMPAT" == 1 ]] && ENGINE+=(--rendering-method gl_compatibility)
exec "$GODOT" "${ENGINE[@]}" -- "--data-dir=$ROOT" "$@"
