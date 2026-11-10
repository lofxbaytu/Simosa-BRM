#!/usr/bin/env bash
# 長時間穩定性(soak)測試(規劃書第 14 章「效能」列:核心 50 Hz 無掉拍、網路延遲 < 50 ms、RSS 不持續成長、連續 2 小時;docs/design/testing.md 第 6 節;
# Windows 版 tools/soak-test.ps1 參數相同):
#   1) 建置 SimCore.Host、Sensors.Radar.Host、StaticWeb(Release;--skip-build 略過;網頁缺 build/web/instruments 時 pnpm build)
#   2) 啟動 SimCore.Host(--scenario,預設 E05;--record 到 build/soak/<run>/records/;VHF 與錄影端開啟於 --port-base+3/+4;--no-udp --no-nmea;--stop-file)、
#      雷達服務 Sensors.Radar.Host(x+s)、靜態網頁 simosa-web(build/web/instruments)
#   3) 啟動合成客戶端 tests/soak/clients.mjs(N 個:各訂閱狀態幀與雷達 X 頻影像、每 30 s GET 網頁;每秒統計幀率、延遲(state.t 與到達牆鐘差)、斷線)
#      與「學員」tests/soak/student.mjs(每 10 s 舵令/車令、每 60 s 注入/清除一個故障、每 5 min 快照;每個指令以狀態幀確認生效)
#   4) 每 --sample-s(預設 10)s 取樣:主控程序 CPU%、RSS、執行緒數(/proc)、steps/s 與掉拍(主控程序 stdout 行)、雷達/網頁/客戶端程序的 CPU 與 RSS;
#      客戶端與學員都已結束(時間到)後取到的那一筆不寫(客戶端斷線時核心釋放連線常有 RSS 尖峰;report.mjs 另排除 elapsed 超過設定時長或 clients.alive=false 的樣本);
#      dotnet-counters(PATH 或 .cache/dotnet-tools)可用時另收 System.Runtime GC 計數器到 stats/counters.csv,否則略過並註記
#   5) 時間到(客戶端自行結束)→ tools/launch/stop-all.sh(停止檔讓核心寫完紀錄 footer)→ tests/soak/report.mjs 寫 build/soak/report-<run>.md 與 .json 並判定
#      (掉拍 0 且 tick 率 50 ± 0.5;延遲 p95 < 50 ms;暖機後 RSS 斜率 ≤ 1 MB/min 且增量 ≤ 64 MB;無斷線/錯誤、學員指令皆生效、核心寫完 footer)
# 全部輸出在 build/soak/:<run>/(bin/ 建置輸出複本、logs/、stats/、records/、media/、meta.json)與 report-<run>.{md,json};<run> 預設為 UTC 時間戳(--run 指定)。
# 結束碼:0 合格、1 不合格(含中途 Ctrl+C 中止、核心提前結束)、2 前置條件/參數錯誤。需獨佔 CPU:不要與整套測試或端對端併行。
# 用法:tools/soak-test.sh [--minutes 10] [--clients 3] [--scenario E05_pilot_traffic|<path.yaml>] [--port-base 18800] [--no-radar] [--no-web] [--skip-build]
#       [--sample-s 10] [--max-latency-ms 50] [--max-rss-slope-mb-per-min 1] [--max-rss-growth-mb 64] [--warmup-s 120] [--run <名稱>] [--note <機器說明>]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# 啟動腳本共用函式:find_dll(缺少時建置)、start_bg(PID 記入 simosa.pids)、wait_tcp、check_ports_free、launch_init_logs、launch_stop_file、process_alive
source "$ROOT/tools/launch/launch-common.sh"

MINUTES=10; CLIENTS=3; SCENARIO="E05_pilot_traffic"; BASE=18800; RADAR_ON=1; WEB_ON=1; SKIP_BUILD=0; SAMPLE=10
MAX_LAT=50; MAX_SLOPE=1; MAX_GROWTH=64; WARMUP=120; RUN=""; NOTE=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --minutes) MINUTES="$2"; shift 2 ;;
    --clients) CLIENTS="$2"; shift 2 ;;
    --scenario) SCENARIO="$2"; shift 2 ;;
    --port-base) BASE="$2"; shift 2 ;;
    --no-radar) RADAR_ON=0; shift ;;
    --no-web) WEB_ON=0; shift ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --sample-s) SAMPLE="$2"; shift 2 ;;
    --max-latency-ms) MAX_LAT="$2"; shift 2 ;;
    --max-rss-slope-mb-per-min) MAX_SLOPE="$2"; shift 2 ;;
    --max-rss-growth-mb) MAX_GROWTH="$2"; shift 2 ;;
    --warmup-s) WARMUP="$2"; shift 2 ;;
    --run) RUN="$2"; shift 2 ;;
    --note) NOTE="$2"; shift 2 ;;
    -h|--help) sed -n 2,16p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
[[ "$MINUTES" =~ ^[0-9]+([.][0-9]+)?$ && "$CLIENTS" =~ ^[0-9]+$ && "$BASE" =~ ^[0-9]+$ && "$SAMPLE" =~ ^[0-9]+$ ]] || { echo "--minutes/--clients/--port-base/--sample-s 須為數字" >&2; exit 2; }
[[ "$CLIENTS" -ge 1 && "$SAMPLE" -ge 1 ]] || { echo "--clients 與 --sample-s 須 ≥ 1" >&2; exit 2; }
SECS="$(awk -v m="$MINUTES" 'BEGIN { printf "%d", m * 60 }')"
[[ "$SECS" -ge 10 ]] || { echo "--minutes 太短(至少 10 s)" >&2; exit 2; }
# 情境:代號 → data/scenarios/<代號>.yaml
if [[ "$SCENARIO" != *.yaml && "$SCENARIO" != */* ]]; then SCENARIO="data/scenarios/$SCENARIO.yaml"; fi
[[ -f "$SCENARIO" ]] || { echo "找不到情境檔 $SCENARIO" >&2; exit 2; }
SCEN_ID="$(basename "$SCENARIO" .yaml)"
command -v node >/dev/null || { echo "找不到 node(需 Node.js 22,內建 WebSocket)" >&2; exit 2; }
command -v dotnet >/dev/null || { echo "找不到 dotnet(需 .NET 8 SDK/執行階段)" >&2; exit 2; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 SIMOSA_BRM_ROOT="$ROOT"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}" NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget/packages}"

[[ -n "$RUN" ]] || RUN="$(date -u +%Y%m%d-%H%M%S)"
OUT_REL="build/soak/$RUN"; OUT="$ROOT/$OUT_REL"; LOGS_REL="$OUT_REL/logs"; STATS="$OUT/stats"
mkdir -p "$OUT/records" "$OUT/media" "$STATS" "$ROOT/$LOGS_REL"
REPORT_MD="$ROOT/build/soak/report-$RUN.md"; REPORT_JSON="$ROOT/build/soak/report-$RUN.json"
WS=$BASE; RADAR=$((BASE + 1)); WEB=$((BASE + 2)); VHF=$((BASE + 3)); MEDIA=$((BASE + 4))
CLK_TCK="$(getconf CLK_TCK 2>/dev/null || echo 100)"
GIT_REV=""; if [[ -f .git/HEAD ]]; then head="$(cat .git/HEAD)"; if [[ "$head" == ref:* ]]; then ref="${head#ref: }"; [[ -f ".git/$ref" ]] && GIT_REV="$(cat ".git/$ref")"; else GIT_REV="$head"; fi; fi
step() { echo; echo "=== [soak] $1"; }
now_ms() { echo $(( $(date +%s%N) / 1000000 )); }

echo "Simosa BRM soak 測試 $RUN:情境 $SCEN_ID、客戶端 $CLIENTS、$MINUTES 分鐘;埠 ws $WS / 雷達 $RADAR / 網頁 $WEB / VHF $VHF / 錄影 $MEDIA;輸出 $OUT_REL/"

if [[ $SKIP_BUILD == 0 ]]; then
  step "建置 SimCore.Host、Sensors.Radar.Host、StaticWeb(Release)"
  for p in src/SimCore.Host src/Sensors.Radar.Host tools/launch/StaticWeb; do dotnet build "$p" -c Release --nologo -v q; done
  if [[ $WEB_ON == 1 && ! -f build/web/instruments/index.html ]]; then
    step "建置網頁(src/Instruments.Web → build/web/instruments)"
    ( cd src/Instruments.Web && { [[ -d node_modules ]] || pnpm install --frozen-lockfile; } && pnpm build )
  fi
fi
HOST_DLL="$(find_dll simcore SimCore.Host SimosaBRM.SimCore.Host.dll "$ROOT")" || { echo "找不到 SimCore.Host(dotnet build src/SimCore.Host -c Release)" >&2; exit 2; }
RADAR_DLL=""; [[ $RADAR_ON == 1 ]] && { RADAR_DLL="$(find_dll radar Sensors.Radar.Host SimosaBRM.Sensors.Radar.Host.dll "$ROOT")" || { echo "找不到 Sensors.Radar.Host(或 --no-radar)" >&2; exit 2; }; }
WEB_DLL=""; WEB_DIR=""
if [[ $WEB_ON == 1 ]]; then
  WEB_DLL="$(find_dll web-server StaticWeb simosa-web.dll "$ROOT")" || { echo "找不到 simosa-web(或 --no-web)" >&2; exit 2; }
  for d in "$ROOT/build/web/instruments" "$ROOT/app/web"; do [[ -f "$d/index.html" ]] && { WEB_DIR="$d"; break; }; done
  [[ -n "$WEB_DIR" ]] || { echo "找不到網頁輸出 build/web/instruments/index.html(cd src/Instruments.Web && pnpm build,或 --no-web)" >&2; exit 2; }
fi
# 建置輸出複製一份到本次執行目錄(bin/):長時間執行中若有人重新 dotnet build,覆寫執行中的 dll 會讓核心崩潰;複本也讓報告對應到固定的二進位
snapshot_bin() { # <dll 路徑> <名稱> → 複本中的 dll 路徑
  local dir; dir="$(dirname "$1")"
  mkdir -p "$OUT/bin/$2" && cp -r "$dir/." "$OUT/bin/$2/"
  echo "$OUT/bin/$2/$(basename "$1")"
}
HOST_DLL="$(snapshot_bin "$HOST_DLL" simcore)"
[[ -n "$RADAR_DLL" ]] && RADAR_DLL="$(snapshot_bin "$RADAR_DLL" radar)"
[[ -n "$WEB_DLL" ]] && WEB_DLL="$(snapshot_bin "$WEB_DLL" web-server)"
# dotnet-counters(選配):PATH 或 .cache/dotnet-tools
DC=""; for c in "$ROOT/.cache/dotnet-tools/dotnet-counters" "$(command -v dotnet-counters 2>/dev/null || true)"; do [[ -n "$c" && -x "$c" ]] && { DC="$c"; break; }; done

PORTS=("SimCore ws:$WS" "VHF:$VHF" "錄影:$MEDIA"); [[ -n "$RADAR_DLL" ]] && PORTS+=("雷達:$RADAR"); [[ -n "$WEB_DLL" ]] && PORTS+=("網頁:$WEB")
check_ports_free "${PORTS[@]}" || exit 2
launch_init_logs "$ROOT" "$LOGS_REL"
STOPFILE="$(launch_stop_file "$ROOT" "$LOGS_REL")"
STARTED_UTC="$(date -u +%FT%TZ)"
ABORTED=0; HOST_EXIT="null"; FOOTER=false; ENDED_UTC=""; HOST_PID=""; CLIENTS_PID=""; STUDENT_PID=""; FINISHED=0

json_str() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
write_meta() {
  cat > "$OUT/meta.json" <<JSON
{
  "run": "$RUN", "scenario": "$SCEN_ID", "scenarioPath": "$(json_str "$SCENARIO")", "clients": $CLIENTS, "minutes": $MINUTES, "sample_s": $SAMPLE,
  "ports": { "ws": $WS, "radar": $([[ -n "$RADAR_DLL" ]] && echo "$RADAR" || echo 0), "web": $([[ -n "$WEB_DLL" ]] && echo "$WEB" || echo 0), "vhf": $VHF, "media": $MEDIA },
  "startedUtc": "$STARTED_UTC", "endedUtc": $([[ -n "$ENDED_UTC" ]] && echo "\"$ENDED_UTC\"" || echo null),
  "platform": "$(json_str "$(uname -srm 2>/dev/null || echo unknown)")", "cpuCount": $(nproc 2>/dev/null || echo 0), "machineNote": "$(json_str "${NOTE:-容器/開發機,非內顯基準筆電}")",
  "dotnet": "$(dotnet --version 2>/dev/null || echo ?)", "node": "$(node --version)", "gitRev": $([[ -n "$GIT_REV" ]] && echo "\"$GIT_REV\"" || echo null),
  "recordPath": "$(json_str "$OUT_REL/records/soak-$SCEN_ID.jsonl")", "hostPid": ${HOST_PID:-0}, "hostExit": $HOST_EXIT, "footer": $FOOTER, "aborted": $([[ $ABORTED == 1 ]] && echo true || echo false),
  "counters": $([[ -n "$DC" ]] && echo true || echo false),
  "thresholds": { "maxLatencyMs": $MAX_LAT, "maxRssSlopeMbPerMin": $MAX_SLOPE, "maxRssGrowthMb": $MAX_GROWTH, "warmupS": $WARMUP }
}
JSON
}

# proc_sample <pid> → "alive ticks rss_kb threads"(/proc;程序不在時 alive=0)
proc_sample() {
  local pid="$1" stat rss thr
  [[ -n "$pid" && -r "/proc/$pid/stat" ]] || { echo "0 0 0 0"; return; }
  stat="$(cat "/proc/$pid/stat" 2>/dev/null)" || { echo "0 0 0 0"; return; }
  stat="${stat##*) }"                      # 去掉 "pid (comm) " 後:state=f[0]、utime=f[11]、stime=f[12]
  read -r -a f <<<"$stat"
  rss="$(awk '/^VmRSS:/ { print $2 }' "/proc/$pid/status" 2>/dev/null)"; thr="$(awk '/^Threads:/ { print $2 }' "/proc/$pid/status" 2>/dev/null)"
  echo "1 $(( ${f[11]:-0} + ${f[12]:-0} )) ${rss:-0} ${thr:-0}"
}
declare -A PREV_TICKS=()
PROC_JSON=null
# proc_json <名稱> <pid> <Δ牆鐘 ms>:設定 PROC_JSON = {"alive":…,"cpu_pct":…,"rss_mb":…,"threads":…} 或 null(無 pid)。
# 不以 $(…) 回傳:命令替換在子 shell 執行,關聯陣列 PREV_TICKS(上次 CPU 時間)的更新會遺失,CPU% 就會變成自啟動以來的累計值
proc_json() {
  local name="$1" pid="$2" dwall="$3" alive ticks rss thr cpu
  [[ -n "$pid" ]] || { PROC_JSON=null; return 0; }
  read -r alive ticks rss thr <<<"$(proc_sample "$pid")"
  if [[ "$alive" == 1 ]]; then
    local prev="${PREV_TICKS[$name]:-$ticks}"
    cpu="$(awk -v dt="$((ticks - prev))" -v dw="$dwall" -v hz="$CLK_TCK" 'BEGIN { printf "%.1f", (dw > 0 ? dt / hz / (dw / 1000.0) * 100 : 0) }')"
    PREV_TICKS[$name]="$ticks"
    PROC_JSON="{\"alive\":true,\"cpu_pct\":$cpu,\"rss_mb\":$(awk -v kb="$rss" 'BEGIN { printf "%.1f", kb / 1024 }'),\"threads\":$thr}"
  else PROC_JSON='{"alive":false,"cpu_pct":null,"rss_mb":null,"threads":null}'; fi
  return 0
}
# 主控程序 stdout 的每秒狀態行:「steps/s N」與累計「掉拍 N」(src/SimCore.Host/Program.cs);只看上次取樣之後的新行
LOG_DONE=0
host_log_json() {
  local log="$ROOT/$LOGS_REL/simcore.log" total
  total="$(wc -l < "$log" 2>/dev/null || echo 0)"
  local out
  out="$(sed -n "$((LOG_DONE + 1)),${total}p" "$log" 2>/dev/null | awk '
    /steps\/s [0-9]+/ { x = $0; sub(/.*steps\/s /, "", x); sub(/[^0-9].*/, "", x); v = x + 0; sum += v; n++; if (n == 1 || v < mn) mn = v; if (n == 1 || v > mx) mx = v }
    /掉拍 [0-9]+/ { y = $0; sub(/.*掉拍 /, "", y); sub(/[^0-9].*/, "", y); d = y + 0 }
    END { if (n > 0) printf "{\"n\":%d,\"mean\":%.2f,\"min\":%d,\"max\":%d}|%s", n, sum / n, mn, mx, (d == "" ? "" : d); else printf "null|%s", (d == "" ? "" : d) }')"
  LOG_DONE="$total"
  STEPS_JSON="${out%%|*}"; local d="${out#*|}"
  if [[ -n "$d" ]]; then DROPPED="$d"; fi
  return 0
}
STEPS_JSON=null; DROPPED=0

finish() {
  [[ $FINISHED == 0 ]] || return 0
  FINISHED=1
  step "結束:等客戶端收尾 → 停止檔讓核心寫完紀錄 footer → 其他程序 SIGINT"
  local i
  for ((i = 0; i < 120; i++)); do
    if { [[ -z "$CLIENTS_PID" ]] || ! process_alive "$CLIENTS_PID"; } && { [[ -z "$STUDENT_PID" ]] || ! process_alive "$STUDENT_PID"; }; then break; fi
    if [[ $ABORTED == 1 ]]; then break; fi
    sleep 0.25
  done
  "$ROOT/tools/launch/stop-all.sh" --logs "$LOGS_REL" --timeout 20 || true
  if [[ -n "$HOST_PID" ]]; then
    local code=0; wait "$HOST_PID" 2>/dev/null || code=$?
    HOST_EXIT="$code"
  fi
  if grep -q "紀錄已關閉" "$ROOT/$LOGS_REL/simcore.log" 2>/dev/null; then FOOTER=true; fi
  ENDED_UTC="$(date -u +%FT%TZ)"
  write_meta
  # 核心在 loadScenario 切換情境時落到 build/records/ 的紀錄不會發生(本測試不切換情境);錄影檔在 $OUT/media
  step "報告"
  local rc=0
  node tests/soak/report.mjs --dir "$OUT" --md "$REPORT_MD" --json "$REPORT_JSON" --max-latency-ms "$MAX_LAT" --max-rss-slope-mb-per-min "$MAX_SLOPE" --max-rss-growth-mb "$MAX_GROWTH" --warmup-s "$WARMUP" || rc=$?
  if [[ $ABORTED == 1 ]]; then echo "測試中途中止(Ctrl+C / 核心提前結束),結果不作為合格依據"; rc=1; fi
  echo "全部輸出:$OUT_REL/(logs、stats、records、media、meta.json);報告 build/soak/report-$RUN.md"
  exit "$rc"
}
on_signal() { echo; echo "[soak] 收到中止訊號"; ABORTED=1; finish; }
# 腳本因錯誤提前結束(set -e)時也要收掉已啟動的程序(不留下核心/雷達/網頁與客戶端)
on_exit() { if [[ $FINISHED == 0 ]]; then FINISHED=1; "$ROOT/tools/launch/stop-all.sh" --logs "$LOGS_REL" --timeout 10 >/dev/null 2>&1 || true; fi; }
trap on_signal INT TERM
trap on_exit EXIT

step "啟動 SimCore.Host($SCEN_ID;紀錄 $OUT_REL/records/、VHF $VHF、錄影 $MEDIA)"
HOST_ARGS=(--scenario "$SCENARIO" --record "$OUT/records/soak-$SCEN_ID.jsonl" --port "$WS" --vhf-port "$VHF" --media-port "$MEDIA" --media-dir "$OUT_REL/media" --no-udp --no-nmea --stop-file "$STOPFILE")
start_bg simcore "$ROOT/$LOGS_REL/simcore.log" dotnet "$HOST_DLL" "${HOST_ARGS[@]}"
HOST_PID="$LAST_PID"
write_meta
rc=0; wait_tcp 127.0.0.1 "$WS" 45 "$HOST_PID" || rc=$?
if [[ $rc != 0 ]]; then echo "SimCore.Host 未開啟埠 $WS(或已結束),見 $LOGS_REL/simcore.log:" >&2; tail -n 5 "$ROOT/$LOGS_REL/simcore.log" >&2; ABORTED=1; exit 2; fi

if [[ -n "$RADAR_DLL" ]]; then
  step "啟動雷達服務(X+S)"
  start_bg radar "$ROOT/$LOGS_REL/radar.log" dotnet "$RADAR_DLL" --sim "ws://127.0.0.1:$WS/" --port "$RADAR" --radars x,s --areas data/areas --no-nmea
  RADAR_PID="$LAST_PID"
  wait_tcp 127.0.0.1 "$RADAR" 30 "$RADAR_PID" || { echo "雷達服務未開啟埠 $RADAR,見 $LOGS_REL/radar.log" >&2; ABORTED=1; finish; }
else RADAR_PID=""; fi
if [[ -n "$WEB_DLL" ]]; then
  step "啟動靜態網頁(simosa-web ← ${WEB_DIR#"$ROOT/"})"
  start_bg web "$ROOT/$LOGS_REL/web.log" dotnet "$WEB_DLL" --root "$WEB_DIR" --port "$WEB" --bind 127.0.0.1 --quiet
  WEB_PID="$LAST_PID"
  wait_tcp 127.0.0.1 "$WEB" 15 "$WEB_PID" || { echo "simosa-web 未開啟埠 $WEB,見 $LOGS_REL/web.log" >&2; ABORTED=1; finish; }
else WEB_PID=""; fi

if [[ -n "$DC" ]]; then
  step "dotnet-counters(System.Runtime,每 $SAMPLE s)→ stats/counters.csv"
  start_bg counters "$ROOT/$LOGS_REL/counters.log" "$DC" collect --process-id "$HOST_PID" --refresh-interval "$SAMPLE" --format csv --output "$STATS/counters.csv" --counters System.Runtime
else echo "  dotnet-counters 不可用(PATH 或 .cache/dotnet-tools 皆無):GC 計數器略過,RSS 以 /proc 取樣"; fi

step "啟動合成客戶端 ×$CLIENTS 與學員($MINUTES 分鐘)"
CLIENT_ARGS=(--ws "ws://127.0.0.1:$WS/" --clients "$CLIENTS" --minutes "$MINUTES" --out "$STATS/clients.jsonl")
[[ -n "$RADAR_DLL" ]] && CLIENT_ARGS+=(--radar "ws://127.0.0.1:$RADAR/radar/x")
[[ -n "$WEB_DLL" ]] && CLIENT_ARGS+=(--web "http://127.0.0.1:$WEB")
start_bg clients "$ROOT/$LOGS_REL/clients.log" node tests/soak/clients.mjs "${CLIENT_ARGS[@]}"
CLIENTS_PID="$LAST_PID"
start_bg student "$ROOT/$LOGS_REL/student.log" node tests/soak/student.mjs --ws "ws://127.0.0.1:$WS/" --minutes "$MINUTES" --out "$STATS/student.jsonl"
STUDENT_PID="$LAST_PID"

step "取樣(每 $SAMPLE s → stats/host.jsonl;Ctrl+C 可提前結束並仍產生報告)"
T0="$(now_ms)"; LAST="$T0"
# 第一次取樣只建立 CPU 基準
proc_json host "$HOST_PID" 0; proc_json radar "$RADAR_PID" 0; proc_json web "$WEB_PID" 0; proc_json clients "$CLIENTS_PID" 0
host_log_json
DEADLINE=$((SECS + 90))
while :; do
  for ((i = 0; i < SAMPLE; i++)); do sleep 1; if [[ $ABORTED == 1 ]]; then break 2; fi; done
  NOW="$(now_ms)"; DWALL=$((NOW - LAST)); LAST="$NOW"; ELAPSED="$(awk -v a="$NOW" -v b="$T0" 'BEGIN { printf "%.1f", (a - b) / 1000 }')"
  proc_json host "$HOST_PID" "$DWALL"; HJ="$PROC_JSON"; proc_json radar "$RADAR_PID" "$DWALL"; RJ="$PROC_JSON"
  proc_json web "$WEB_PID" "$DWALL"; WJ="$PROC_JSON"; proc_json clients "$CLIENTS_PID" "$DWALL"; CJ="$PROC_JSON"
  host_log_json
  # 客戶端與學員都已結束(時間到、核心仍在)→ 這一筆是收尾樣本,不寫:客戶端斷線時核心釋放連線與訂閱常有 RSS 尖峰,不該混入 RSS 擬合
  # (tools/soak-test.ps1 同步;report.mjs 對舊紀錄另以 elapsed_s > 設定時長或 clients.alive=false 排除)
  if ! process_alive "$CLIENTS_PID" && ! process_alive "$STUDENT_PID" && process_alive "$HOST_PID"; then
    printf '  %6ss  客戶端與學員已結束,本筆不寫(收尾)\n' "$ELAPSED"; break
  fi
  HJ="${HJ%\}},\"stepsPerS\":$STEPS_JSON,\"dropped\":$DROPPED}"
  printf '{"kind":"sample","wall":"%s","elapsed_s":%s,"host":%s,"radar":%s,"web":%s,"clients":%s}\n' "$(date -u +%FT%TZ)" "$ELAPSED" "$HJ" "$RJ" "$WJ" "$CJ" >> "$STATS/host.jsonl"
  printf '  %6ss  核心 %s  steps/s %s  掉拍 %s\n' "$ELAPSED" "$(sed 's/^{"alive":true,//; s/,"threads".*//' <<<"$HJ")" "$(sed 's/.*"mean":\([0-9.]*\).*/\1/; s/^null$/-/' <<<"$STEPS_JSON")" "$DROPPED"
  if ! process_alive "$HOST_PID"; then echo "[soak] SimCore.Host 提前結束(見 $LOGS_REL/simcore.log)" >&2; ABORTED=1; break; fi
  if ! process_alive "$CLIENTS_PID" && ! process_alive "$STUDENT_PID"; then break; fi
  if awk -v e="$ELAPSED" -v d="$DEADLINE" 'BEGIN { exit !(e >= d) }'; then echo "[soak] 客戶端逾時未結束,強制收尾" >&2; break; fi
done
finish
