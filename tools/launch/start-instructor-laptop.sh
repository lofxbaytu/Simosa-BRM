#!/usr/bin/env bash
# 教官筆電啟動(Linux 開發版;正式為 start-instructor-laptop.ps1;規劃書第 5.4 節教官筆電):
#   依 config/instructor-laptop.json 提供教官站網頁(本機 simosa-web,頁面的 ws 參數指向船上筆電)、雷達鏡像頁,並印出 simosa-pkg sync 指令說明;
#   standalone.enabled=true 時另啟動本機 SimCore.Host(離船備課)。log 在 build/logs/;stop-all.sh 結束。
# 用法:start-instructor-laptop.sh [--config config/instructor-laptop.json] [--ship FSB1|FSB2|<IP>] [--standalone] [--check-wifi] [--wait]
#   --check-wifi:直連模式下以 nmcli/iw 檢查無線介面(與 ps1 的 -CheckWifi 相同),Wi-Fi 仍啟用時拒絕繼續(exit 1);沒有這些工具時只提示。
source "$(dirname "${BASH_SOURCE[0]}")/launch-common.sh"
ROOT="$(launch_root)"
CONFIG="$ROOT/config/instructor-laptop.json"; SHIP=""; STANDALONE=""; WAIT=0; CHECK_WIFI=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    --ship) SHIP="$2"; shift 2 ;;
    --standalone) STANDALONE=1; shift ;;
    --check-wifi) CHECK_WIFI=1; shift ;;
    --wait) WAIT=1; shift ;;
    -h|--help) sed -n 2,7p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
[[ "$CONFIG" = /* ]] || CONFIG="$ROOT/$CONFIG"
load_config "$CONFIG"
cd "$ROOT"
export SIMOSA_BRM_ROOT="$ROOT" SIMOSA_INSTALL_ROOT="$ROOT" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
LOGS="$(cfg logs build/logs)"
SHIP="${SHIP:-$(cfg ship FSB1)}"
# --ship 可為 ships 表的名稱(FSB1/FSB2)或直接給 IP;IP 不查表(cfg 會把「.」換成 _,查不到也只回傳預設值)
if [[ "$SHIP" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then SHIP_IP="$SHIP"; else SHIP_IP="$(cfg "ships_$SHIP" "$SHIP")"; fi
[[ -z "$STANDALONE" ]] && STANDALONE="$(cfg standalone_enabled false)"
WS_PORT="$(cfg ports_stateWs 8765)"; RADAR_PORT="$(cfg ports_radarWs 8766)"; WEB_PORT="$(cfg ports_web 8080)"; SYNC_PORT="$(cfg ports_sync 7300)"
[[ "$STANDALONE" == true || "$STANDALONE" == 1 ]] && SHIP_IP="127.0.0.1"

echo "Simosa BRM 教官筆電 — 目標船 $SHIP($SHIP_IP);安裝根目錄 $ROOT"
launch_init_logs "$ROOT" "$LOGS"
trap 'echo; "$LAUNCH_DIR/stop-all.sh" --logs "$LOGS"' INT TERM

# 直連模式:Wi-Fi/藍牙須停用(規劃書第 5.4 節;第 14 章「船上部署」);Linux 以 nmcli 或 iw 檢查,Windows 版見 start-instructor-laptop.ps1 -CheckWifi
wifi_up() {
  if command -v nmcli >/dev/null 2>&1; then nmcli -t -f WIFI radio 2>/dev/null | grep -qx enabled && return 0; return 1; fi
  if command -v iw >/dev/null 2>&1; then iw dev 2>/dev/null | grep -q '^\s*Interface' && return 0; return 1; fi
  return 2
}
if [[ "$STANDALONE" != true && "$STANDALONE" != 1 ]]; then
  w=0; wifi_up || w=$?
  case $w in
    0) echo "直連模式:Wi-Fi 仍啟用,請停用後再同步(nmcli radio wifi off)" >&2
       [[ $CHECK_WIFI == 1 ]] && { echo "教官筆電 Wi-Fi 開啟時直連模式拒絕同步(--check-wifi)" >&2; rm -f "$PIDFILE"; exit 1; } ;;
    2) [[ $CHECK_WIFI == 1 ]] && echo "  Wi-Fi 檢查:本機沒有 nmcli/iw,無法自動確認;請手動確認 Wi-Fi 與藍牙已停用" ;;
    *) echo "  Wi-Fi 檢查:無線介面已停用" ;;
  esac
fi

if [[ "$STANDALONE" == true || "$STANDALONE" == 1 ]]; then
  HOST_DLL="$(find_dll simcore SimCore.Host SimosaBRM.SimCore.Host.dll "$ROOT")" || { echo "找不到 SimCore.Host" >&2; exit 2; }
  HOST_ARGS=(--scenario "$(cfg standalone_scenario data/scenarios/E01_baseline.yaml)" --port "$WS_PORT" --no-nmea)
  [[ -n "$(cfg standalone_ship)" ]] && HOST_ARGS+=(--ship "$(cfg standalone_ship)")
  [[ -n "$(cfg standalone_loading)" ]] && HOST_ARGS+=(--loading "$(cfg standalone_loading)")
  [[ "$(cfg standalone_record false)" == true ]] && HOST_ARGS+=(--record)
  start_bg simcore "$ROOT/$LOGS/simcore.log" dotnet "$HOST_DLL" "${HOST_ARGS[@]}"
  wait_tcp 127.0.0.1 "$WS_PORT" 30 || echo "警告:SimCore.Host 未開啟 $WS_PORT" >&2
  if RADAR_DLL="$(find_dll radar Sensors.Radar.Host SimosaBRM.Sensors.Radar.Host.dll "$ROOT")"; then
    start_bg radar "$ROOT/$LOGS/radar.log" dotnet "$RADAR_DLL" --sim "ws://127.0.0.1:$WS_PORT/" --port "$RADAR_PORT" --localhost-only --no-nmea
  fi
else
  echo "直連模式:請確認 Wi-Fi 與藍牙已停用、網路線已接上船上筆電($SHIP_IP);教官筆電直連埠固定 IP $(cfg network_directLinkIp)"
  if wait_tcp "$SHIP_IP" "$WS_PORT" 3; then echo "  船上 SimCore.Host 狀態流 ws://$SHIP_IP:$WS_PORT 可連線"; else echo "  注意:尚未連到 ws://$SHIP_IP:$WS_PORT(船上筆電未啟動或線未接)"; fi
fi

WEB_DIR=""; for d in "$ROOT/app/web" "$ROOT/build/web/instruments"; do [[ -f "$d/index.html" ]] && { WEB_DIR="$d"; break; }; done
if [[ "$(cfg web_enabled true)" == true && -n "$WEB_DIR" ]] && WEB_DLL="$(find_dll web-server StaticWeb simosa-web.dll "$ROOT")"; then
  start_bg web "$ROOT/$LOGS/web.log" dotnet "$WEB_DLL" --root "$WEB_DIR" --port "$WEB_PORT" --bind "$(cfg web_bind 127.0.0.1)"
  n="$(cfg web_openPages__n 0)"
  echo "教官站頁面(以瀏覽器開啟):"
  for ((i = 0; i < n; i++)); do
    p="$(cfg web_openPages_$i)"; p="${p//\{ship\}/$SHIP_IP}"; p="${p//\{stateWs\}/$WS_PORT}"; p="${p//\{radarWs\}/$RADAR_PORT}"
    echo "  http://127.0.0.1:$WEB_PORT/$p"
  done
fi

cat <<MSG
同步(訪船時,須先在教官筆電完成受影響情境驗證;docs/design/packaging-sync.md 第 6.2–6.3 節):
  simosa-pkg sync --ship $SHIP --cert <硬體金鑰憑證指紋> [--push build/packages/<更新包>.sbupd]...   # 推更新包、取回紀錄包、送已備份回報
  simosa-pkg store backup --to <加密外接碟>                                                       # 回岸後備份紀錄庫
  simosa-pkg export --to <隨身碟> --role instructor --push <更新包> --cert <指紋>                   # 網路線不可用時的隨身碟備援
MSG
if [[ $WAIT == 1 ]]; then echo "Ctrl+C 結束全部程序"; while :; do sleep 3600 & wait $!; done; fi
