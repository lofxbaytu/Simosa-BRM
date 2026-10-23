#!/usr/bin/env bash
# 船上筆電啟動(Linux/容器煙霧版;正式為 start-ship-laptop.ps1,參數與行為相同;規劃書第 5.4–5.5 節、第 14 章「船上部署」):
#   依 config/ship-laptop.json 啟動 SimCore.Host(WebSocket/UDP 多播/NMEA)、Sensors.Radar.Host、靜態網頁(simosa-web 提供 app/web)、
#   視景(匯出檔或 Godot 編輯器,每個 views[] 一個程序)、直連同步服務 simosa-pkg serve;各程序 log 在 build/logs/;Ctrl+C 一併結束。
# 用法:start-ship-laptop.sh [--config config/ship-laptop.json] [--scenario <yaml>] [--duration <秒>] [--no-visual] [--no-web] [--no-radar] [--no-sync]
#       [--check-only] [--wait](前景等待 Ctrl+C;預設啟動後即返回,之後用 stop-all.sh 結束)
#       --duration:模擬秒數到達後 SimCore 自動結束,本腳本於同一時間結束其他程序(煙霧測試;容器以 E05 60 s 驗證)。
#   啟動前檢查各埠未被殘留程序占住(占用即報錯 exit 1),SimCore.Host 啟動後若程序結束(而非只是埠未開)立即回報失敗;測試見 tests/launch-tests.sh。
source "$(dirname "${BASH_SOURCE[0]}")/launch-common.sh"
ROOT="$(launch_root)"
CONFIG="$ROOT/config/ship-laptop.json"
SCENARIO=""; DURATION=""; VISUAL=1; WEB=1; RADAR=1; SYNC=1; WAIT=0; CHECK_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    --scenario) SCENARIO="$2"; shift 2 ;;
    --duration) DURATION="$2"; shift 2 ;;
    --no-visual) VISUAL=0; shift ;;
    --no-web) WEB=0; shift ;;
    --no-radar) RADAR=0; shift ;;
    --no-sync) SYNC=0; shift ;;
    --wait) WAIT=1; shift ;;
    --check-only) CHECK_ONLY=1; shift ;;
    -h|--help) sed -n 2,8p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
[[ "$CONFIG" = /* ]] || CONFIG="$ROOT/$CONFIG"
load_config "$CONFIG"
cd "$ROOT"
export SIMOSA_BRM_ROOT="$ROOT" SIMOSA_INSTALL_ROOT="$ROOT" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}" NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget/packages}"
LOGS="$(cfg logs build/logs)"
SHIP_IP="$(cfg network_directLinkIp 127.0.0.1)"
WS_PORT="$(cfg ports_stateWs 8765)"; RADAR_PORT="$(cfg ports_radarWs 8766)"; WEB_PORT="$(cfg ports_web 8080)"; SYNC_PORT="$(cfg ports_sync 7300)"

echo "Simosa BRM 船上筆電啟動 — $(cfg shipId) / $(cfg deviceId);安裝根目錄 $ROOT;設定 $CONFIG"
command -v dotnet >/dev/null || { echo "找不到 dotnet(需 .NET 8 執行階段)" >&2; exit 2; }

HOST_DLL="$(find_dll simcore SimCore.Host SimosaBRM.SimCore.Host.dll "$ROOT")" || { echo "找不到 SimCore.Host" >&2; exit 2; }
RADAR_DLL=""; [[ $RADAR == 1 && "$(cfg radar_enabled true)" == true ]] && { RADAR_DLL="$(find_dll radar Sensors.Radar.Host SimosaBRM.Sensors.Radar.Host.dll "$ROOT")" || echo "警告:找不到 Sensors.Radar.Host,略過雷達" >&2; }
WEB_DLL=""; [[ $WEB == 1 && "$(cfg web_enabled true)" == true ]] && { WEB_DLL="$(find_dll web-server StaticWeb simosa-web.dll "$ROOT")" || echo "警告:找不到 simosa-web,略過網頁伺服" >&2; }
PKG_DLL=""; [[ $SYNC == 1 && "$(cfg sync_enabled true)" == true ]] && { PKG_DLL="$(find_dll simosa-pkg Tools.Packaging.Cli simosa-pkg.dll "$ROOT")" || echo "警告:找不到 simosa-pkg,略過同步服務" >&2; }
WEB_DIR=""; for d in "$ROOT/app/web" "$ROOT/build/web/instruments"; do [[ -f "$d/index.html" ]] && { WEB_DIR="$d"; break; }; done

if [[ $CHECK_ONLY == 1 ]]; then
  echo "SimCore.Host:$HOST_DLL"; echo "雷達:${RADAR_DLL:-略過}"; echo "網頁:${WEB_DLL:-略過} ← ${WEB_DIR:-(找不到 app/web)}"; echo "同步:${PKG_DLL:-略過}"
  exit 0
fi

launch_init_logs "$ROOT" "$LOGS"
trap 'echo; echo "結束全部程序…"; "$LAUNCH_DIR/stop-all.sh" --logs "$LOGS"' INT TERM

# 啟動前確認各埠沒有被殘留的舊程序占住(否則 SimCore.Host 會立刻崩潰,而 wait_tcp 連到的是別的程序);同時查 127.0.0.1 與直連 IP
PORTS=("SimCore ws:$WS_PORT")
[[ -n "$RADAR_DLL" ]] && PORTS+=("雷達 ws:$RADAR_PORT")
[[ -n "$WEB_DLL" && -n "$WEB_DIR" ]] && PORTS+=("網頁:$WEB_PORT")
[[ -n "$PKG_DLL" && -f "$ROOT/config/packaging.json" && -d "$ROOT/config/device" ]] && PORTS+=("同步服務:$SYNC_PORT")
PORT_CHECK_HOSTS="$([[ "$SHIP_IP" != 127.0.0.1 ]] && echo "$SHIP_IP")" check_ports_free "${PORTS[@]}" || { rm -f "$PIDFILE"; exit 1; }

# 1) SimCore.Host
HOST_ARGS=(--scenario "${SCENARIO:-$(cfg simcore_scenario data/scenarios/E01_baseline.yaml)}" --port "$WS_PORT")
[[ -n "$(cfg simcore_ship)" ]] && HOST_ARGS+=(--ship "$(cfg simcore_ship)")
[[ -n "$(cfg simcore_loading)" ]] && HOST_ARGS+=(--loading "$(cfg simcore_loading)")
[[ -n "$(cfg simcore_dynamics)" ]] && HOST_ARGS+=(--dynamics "$(cfg simcore_dynamics)")
[[ -n "$(cfg simcore_parameters)" && -f "$ROOT/$(cfg simcore_parameters)" ]] && HOST_ARGS+=(--parameters "$(cfg simcore_parameters)")
[[ "$(cfg simcore_record false)" == true ]] && HOST_ARGS+=(--record)
[[ -n "$(cfg simcore_timescale)" ]] && HOST_ARGS+=(--timescale "$(cfg simcore_timescale)")
[[ -n "$(cfg ports_stateMulticast)" ]] && HOST_ARGS+=(--multicast "$(cfg ports_stateMulticast)")
[[ -n "$(cfg ports_nmeaUdp)" ]] && HOST_ARGS+=(--nmea-port "$(cfg ports_nmeaUdp)")
[[ -n "$(cfg ports_nmeaMulticast)" ]] && HOST_ARGS+=(--nmea-multicast "$(cfg ports_nmeaMulticast)")
[[ -n "$DURATION" ]] && HOST_ARGS+=(--duration "$DURATION")
start_bg simcore "$ROOT/$LOGS/simcore.log" dotnet "$HOST_DLL" "${HOST_ARGS[@]}"
HOST_PID="$LAST_PID"
rc=0; wait_tcp 127.0.0.1 "$WS_PORT" 30 "$HOST_PID" || rc=$?
if [[ $rc != 0 ]]; then
  if [[ $rc == 2 ]]; then echo "SimCore.Host 已結束(啟動失敗),見 $LOGS/simcore.log 最後幾行:" >&2; tail -n 5 "$ROOT/$LOGS/simcore.log" >&2
  else echo "SimCore.Host 未在 30 s 內開啟 ws 埠 $WS_PORT,見 $LOGS/simcore.log" >&2; fi
  "$LAUNCH_DIR/stop-all.sh" --logs "$LOGS"; exit 1
fi

# 2) 雷達服務
if [[ -n "$RADAR_DLL" ]]; then
  RADAR_ARGS=(--sim "ws://127.0.0.1:$WS_PORT/" --port "$RADAR_PORT" --radars "$(cfg radar_radars x,s)" --areas "$(cfg radar_areas data/areas)"
              --nmea-port-x "$(cfg ports_radarNmeaX 10111)" --nmea-port-s "$(cfg ports_radarNmeaS 10112)")
  [[ -n "$DURATION" ]] && RADAR_ARGS+=(--duration "$((DURATION + 5))")
  start_bg radar "$ROOT/$LOGS/radar.log" dotnet "$RADAR_DLL" "${RADAR_ARGS[@]}"
  wait_tcp 127.0.0.1 "$RADAR_PORT" 15 "$LAST_PID" || echo "警告:雷達服務未開啟埠 $RADAR_PORT 或已結束,見 $LOGS/radar.log" >&2
fi

# 3) 靜態網頁
if [[ -n "$WEB_DLL" && -n "$WEB_DIR" ]]; then
  start_bg web "$ROOT/$LOGS/web.log" dotnet "$WEB_DLL" --root "$WEB_DIR" --port "$WEB_PORT" --bind "$(cfg web_bind 0.0.0.0)"
  wait_tcp 127.0.0.1 "$WEB_PORT" 10 "$LAST_PID" || echo "警告:simosa-web 未開啟埠 $WEB_PORT 或已結束,見 $LOGS/web.log" >&2
elif [[ -n "$WEB_DLL" ]]; then echo "警告:找不到網頁目錄(app/web 或 build/web/instruments),略過" >&2; fi

# 4) 視景(每個 views[] 一個程序;Linux:匯出檔 app/visual/*.x86_64 或 tools/visual-run.sh)
if [[ $VISUAL == 1 && "$(cfg visual_enabled true)" == true ]]; then
  VIS_EXE=""; for c in "$ROOT/app/visual/SimosaBRM.Visual.x86_64" "$ROOT/app/visual/SimosaBRM.Visual"; do [[ -x "$c" ]] && { VIS_EXE="$c"; break; }; done
  n="$(cfg visual_views__n 0)"
  for ((i = 0; i < n; i++)); do
    v="$(cfg visual_views_${i}_view bridge)"; m="$(cfg visual_views_${i}_monitor 0)"
    VARGS=("--view=$v" "--monitor=$m" "--quality=$(cfg visual_quality auto)" "--ws=ws://127.0.0.1:$WS_PORT" "--data-dir=$ROOT")
    [[ "$(cfg visual_views_${i}_fullscreen false)" == true ]] && VARGS+=(--fullscreen)
    if [[ -n "$VIS_EXE" ]]; then start_bg "visual-$v" "$ROOT/$LOGS/visual-$v.log" "$VIS_EXE" -- "${VARGS[@]}"
    elif [[ -x "$ROOT/tools/visual-run.sh" && -n "${DISPLAY:-}" ]]; then start_bg "visual-$v" "$ROOT/$LOGS/visual-$v.log" "$ROOT/tools/visual-run.sh" "${VARGS[@]}"
    else echo "  視景 $v:略過(無匯出檔 app/visual/、或無 DISPLAY/Godot;Windows 見 start-ship-laptop.ps1)"; fi
  done
fi

# 5) 直連同步服務(需安裝時完成 config/packaging.json 與 device init)
if [[ -n "$PKG_DLL" ]]; then
  if [[ -f "$ROOT/config/packaging.json" && -d "$ROOT/config/device" ]]; then
    start_bg sync "$ROOT/$LOGS/sync.log" dotnet "$PKG_DLL" serve --root "$ROOT" --port "$SYNC_PORT"
    sleep 1; process_alive "$LAST_PID" || echo "警告:同步服務已結束,見 $LOGS/sync.log" >&2
  else
    echo "  同步服務:略過(尚未安裝:缺 config/packaging.json 或 config/device/;見 docs/ops/安裝與啟動.md「首次啟動」)"
  fi
fi

echo "已啟動;PID 檔 $LOGS/simosa.pids。網頁:http://$SHIP_IP:$WEB_PORT/instructor.html?mode=simple  狀態 ws://$SHIP_IP:$WS_PORT  雷達 ws://$SHIP_IP:$RADAR_PORT/radar/x"
if [[ -n "$DURATION" ]]; then
  echo "等待 SimCore 於模擬 $DURATION s 後結束…"
  while process_alive "$HOST_PID"; do sleep 1; done
  "$LAUNCH_DIR/stop-all.sh" --logs "$LOGS"
elif [[ $WAIT == 1 ]]; then
  echo "Ctrl+C 結束全部程序"
  while :; do sleep 3600 & wait $!; done
fi
