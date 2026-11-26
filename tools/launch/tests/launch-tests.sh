#!/usr/bin/env bash
# 啟動腳本測試(Linux/容器;規劃書第 5.4–5.5 節、第 14 章「船上部署」):以真實的 SimCore.Host / simosa-web 與假程序驗證
#   tools/launch/*.sh 的失敗回報、埠檢查、stop-all --keep-sync、--logs 傳遞、--ship <IP>、--check-wifi、停止檔優雅停止(footer)、
#   USB 操控台服務 --console loopback:…(假裝置:ACK/LED/DISP 回寫、指令轉送核心、stop-all 結束)、
#   NMEA 選項(config ports.nmeaTargets/nmeaBind/nmeaFile → --nmea-target/--nmea-bind/--nmea-file;UDP 單播目標真的收到、檔案寫出)、
#   VHF 8767/錄影 8768 埠預檢(第二十四輪),以及 docs/驗收/需求追溯表.md 的數字;
#   發行版狀態(第三十輪;教官站頁首的「發行版 <版本>」與降版提醒):simosa-web --status-file 與 /_status/release.json(只含白名單欄位、
#   路徑安全、狀態檔不存在 / 不是 status 的輸出時各欄 null)、write_release_status(開發模式不產生、逾時與失敗不擋住啟動並刪掉舊檔)、
#   以真實的 simosa-pkg 做出降版後從「安裝版」資料夾啟動教官筆電腳本(狀態檔 → simosa-web → 降版資訊);
#   船上筆電的版本(第三十一輪;教官筆電的教官站跨來源向船上的 simosa-web 取):/_status/release.json 的 CORS 標頭只給本機迴路位址的頁面
#   (各種 Origin、只有這個網址有、HEAD)、ports.shipWeb → 教官站網址的 shipWeb 參數(ship_web_url / ship_web_page 與啟動腳本的輸出)。
#   同步與隨身碟匯入時的降版提醒(第三十一輪):真實的 simosa-pkg serve + sync、export + import — 降版的船印出並保存提醒、處理後消失。
# 用法:tools/launch/tests/launch-tests.sh [--only <測試名>] [--list]
#   所有輸出留在專案內:build/test-tmp/launch/(設定檔、假 PID 檔)、build/logs-test*/(各測試的 log);埠自 LAUNCH_TEST_PORT_BASE(預設 18760)起。
#   Windows 版(launch-common.ps1、stop-all.ps1)另見 launch-common.tests.ps1(pwsh 7 於 Linux 亦可執行)。
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LAUNCH="$(cd "$HERE/.." && pwd)"
ROOT="$(cd "$LAUNCH/../.." && pwd)"
if ! command -v dotnet >/dev/null 2>&1 && [[ -f /etc/profile.d/dotnet.sh ]]; then . /etc/profile.d/dotnet.sh; fi
cd "$ROOT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.cache/dotnet-home}" NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget/packages}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$ROOT/.cache/nuget/http}" NUGET_PLUGINS_CACHE_PATH="${NUGET_PLUGINS_CACHE_PATH:-$ROOT/.cache/nuget/plugins}" NUGET_SCRATCH="${NUGET_SCRATCH:-$ROOT/.cache/nuget/scratch}"  # NuGet HTTP/外掛快取與暫存只能用環境變數導回專案內(nuget.config 的註解)

TMP="$ROOT/build/test-tmp/launch"; mkdir -p "$TMP"
BASE="${LAUNCH_TEST_PORT_BASE:-18760}"
WS=$((BASE + 5)); RADAR=$((BASE + 6)); WEB=$((BASE + 7)); SYNC=$((BASE + 8)); VHF=$((BASE + 9)); MEDIA=$((BASE + 10)); NMEA_T=$((BASE + 11))
LOGS="build/logs-test"           # 相對安裝根目錄(與 config 的 logs 欄位相同用法)
ONLY=""; LIST=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --only) ONLY="$2"; shift 2 ;;
    --list) LIST=1; shift ;;
    -h|--help) sed -n 2,6p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done

PASS=0; FAIL=0; FAILED=()
HELPERS=()
fail() { echo "    失敗:$*"; return 1; }   # 用法:cond || fail "…" || return 1;反向:cond && { fail "…"; return 1; }
cleanup_helpers() {
  local p
  for p in "${HELPERS[@]:-}"; do [[ -n "$p" ]] && kill -KILL "$p" 2>/dev/null; done
  HELPERS=()
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1 || true
  "$LAUNCH/stop-all.sh" --logs "$LOGS-alt" >/dev/null 2>&1 || true
}
# 產生測試用設定檔:以儲存庫 config/*.json 為底,改埠、logs 與其他鍵(python 以 a.b=值 的形式覆寫;值為 JSON)
make_cfg() {
  local src="$1" out="$2"; shift 2
  python3 -I - "$ROOT/config/$src" "$out" "$@" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
for kv in sys.argv[3:]:
    path, val = kv.split("=", 1)
    cur = cfg
    parts = path.split(".")
    for p in parts[:-1]:
        cur = cur.setdefault(p, {})
    cur[parts[-1]] = json.loads(val)
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
PY
}
ship_cfg() {  # ship_cfg <檔名> [覆寫…]
  local out="$TMP/$1"; shift
  make_cfg ship-laptop.json "$out" "ports.stateWs=$WS" "ports.radarWs=$RADAR" "ports.web=$WEB" "ports.sync=$SYNC" "ports.vhfWs=$VHF" "ports.mediaHttp=$MEDIA" \
    'ports.stateMulticast=""' 'ports.nmeaUdp=""' 'ports.nmeaMulticast=""' "logs=\"$LOGS\"" 'simcore.scenario="data/scenarios/E01_baseline.yaml"' \
    'simcore.record=false' 'simcore.parameters=""' 'web.bind="127.0.0.1"' 'visual.enabled=false' "$@"
  echo "$out"
}
instructor_cfg() {
  local out="$TMP/$1"; shift
  make_cfg instructor-laptop.json "$out" "ports.stateWs=$WS" "ports.radarWs=$RADAR" "ports.web=$WEB" "ports.sync=$SYNC" "logs=\"$LOGS\"" 'web.enabled=false' "$@"
  echo "$out"
}
tcp_open() { timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/$1" 2>/dev/null; }
# 以 python 佔住一個埠(假的殘留程序;不 accept,backlog 16 讓 hold_port 的等待探測與腳本預檢的多次連線都能成功 — backlog 1 只容許約 2 次)
hold_port() {
  python3 -I -c 'import socket,sys,time
s=socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", int(sys.argv[1]))); s.listen(16)
print("held", flush=True)
time.sleep(600)' "$1" >/dev/null 2>&1 &
  HELPERS+=("$!"); disown
  local i; for ((i = 0; i < 40; i++)); do tcp_open "$1" && return 0; sleep 0.25; done
  return 1
}
# 假的背景程序(stop-all 測試用):sleep;PID 放在 FAKE_PID(不能用 $(…),背景程序會把命令替換的管線卡住)
fake_proc() { sleep 600 >/dev/null 2>&1 & FAKE_PID=$!; HELPERS+=("$FAKE_PID"); disown; }
run_launch() {  # run_launch <輸出檔> <指令…>:記錄輸出與結束碼(OUT、CODE)
  local outf="$1"; shift
  "$@" > "$outf" 2>&1; CODE=$?
  OUT="$(cat "$outf")"
}

# ---- 測試 ----

# 發現 1:埠被占用時必須報錯 exit 1,不得印「已啟動」;SimCore 不得啟動(log 無 Unhandled exception)
test_port_busy_fails() {
  local cfg; cfg="$(ship_cfg ship-portbusy.json)"
  rm -f "$ROOT/$LOGS/simcore.log"
  hold_port "$WS" || fail "測試前置:無法佔住埠 $WS" || return 1
  run_launch "$TMP/portbusy.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --no-radar --no-web --no-sync --no-visual
  [[ $CODE == 1 ]] || fail "結束碼應為 1,實際 $CODE;輸出:$OUT" || return 1
  grep -q "埠 $WS 已被占用" <<<"$OUT" || fail "應回報「埠 $WS 已被占用」;輸出:$OUT" || return 1
  grep -q "已啟動" <<<"$OUT" && { fail "不得印「已啟動」;輸出:$OUT"; return 1; }
  [[ ! -f "$ROOT/$LOGS/simcore.log" ]] || ! grep -q "Unhandled exception" "$ROOT/$LOGS/simcore.log" || fail "SimCore 不應被啟動到崩潰" || return 1
  [[ ! -s "$ROOT/$LOGS/simosa.pids" ]] || fail "PID 檔應為空或不存在" || return 1
}

# 發現 1(續):SimCore 啟動後立即結束(例如情境不存在)時,須在 30 s 逾時前就回報失敗
test_simcore_death_detected() {
  local cfg; cfg="$(ship_cfg ship-dead.json)"
  local t0=$SECONDS
  run_launch "$TMP/dead.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --scenario data/scenarios/does-not-exist.yaml --no-radar --no-web --no-sync --no-visual
  local dt=$((SECONDS - t0))
  [[ $CODE == 1 ]] || fail "結束碼應為 1,實際 $CODE;輸出:$OUT" || return 1
  grep -q "SimCore.Host 已結束" <<<"$OUT" || fail "應回報「SimCore.Host 已結束」;輸出:$OUT" || return 1
  [[ $dt -lt 20 ]] || fail "應在程序結束後立即回報(耗時 $dt s)" || return 1
  [[ ! -f "$ROOT/$LOGS/simosa.pids" ]] || fail "失敗後 PID 檔應被 stop-all 移除" || return 1
}

# 發現 2:stop-all.sh --keep-sync 保留 sync 程序與其 PID 列;之後不帶參數的 stop-all 再收掉
test_stop_all_keep_sync() {
  mkdir -p "$ROOT/$LOGS"
  local web sync; fake_proc; web=$FAKE_PID; fake_proc; sync=$FAKE_PID
  printf 'web:%s\nsync:%s\n' "$web" "$sync" > "$ROOT/$LOGS/simosa.pids"
  run_launch "$TMP/keepsync.out" bash "$LAUNCH/stop-all.sh" --logs "$LOGS" --keep-sync
  [[ $CODE == 0 ]] || fail "結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  sleep 0.5
  kill -0 "$web" 2>/dev/null && { fail "web 程序應已結束"; return 1; }
  kill -0 "$sync" 2>/dev/null || fail "sync 程序應被保留;輸出:$OUT" || return 1
  grep -q "^sync:$sync$" "$ROOT/$LOGS/simosa.pids" 2>/dev/null || fail "PID 檔應保留 sync 列" || return 1
  grep -q "^web:" "$ROOT/$LOGS/simosa.pids" && { fail "PID 檔不應再有 web 列"; return 1; }
  run_launch "$TMP/keepsync2.out" bash "$LAUNCH/stop-all.sh" --logs "$LOGS"
  sleep 0.5
  kill -0 "$sync" 2>/dev/null && { fail "第二次 stop-all 應結束 sync"; return 1; }
  [[ ! -f "$ROOT/$LOGS/simosa.pids" ]] || fail "PID 檔應被移除" || return 1
}

# 發現 3:--ship <IP>(含「.」)不得造成 cfg() 的 invalid variable name
test_instructor_ship_ip() {
  local cfg; cfg="$(instructor_cfg instructor-ip.json)"
  run_launch "$TMP/shipip.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  [[ $CODE == 0 ]] || fail "結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "invalid variable name" <<<"$OUT" && { fail "不得出現 invalid variable name"; return 1; }
  grep -q "目標船 127.0.0.2(127.0.0.2)" <<<"$OUT" || fail "應印出目標船 127.0.0.2(127.0.0.2);輸出:$OUT" || return 1
  # cfg() 對含「.」的鍵名回傳預設值而非報錯
  local v; v="$(bash -c "source '$LAUNCH/launch-common.sh'; load_config '$cfg'; cfg ships_192.168.77.99 預設" 2>&1)" || fail "cfg 含「.」鍵名出錯:$v" || return 1
  [[ "$v" == "預設" ]] || fail "cfg 應回傳預設值,實際:$v" || return 1
}

# 發現 4:simosa-web 收到 SIGINT 須以 0 結束,不得留下 ObjectDisposedException
test_web_sigint_exit0() {
  local dll
  dll="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll web-server StaticWeb simosa-web.dll '$ROOT'")" || fail "找不到/無法建置 simosa-web" || return 1
  local log="$TMP/web-sigint.log"; : > "$log"
  # 與 start_bg 相同的啟動方式(直接 cmd & 會讓背景程序忽略 SIGINT)
  ( exec dotnet "$dll" --root "$ROOT/build/web/instruments" --port "$WEB" --bind 127.0.0.1 > "$log" 2>&1 ) &
  local pid=$!; HELPERS+=("$pid")
  local i; for ((i = 0; i < 40; i++)); do tcp_open "$WEB" && break; sleep 0.25; done
  tcp_open "$WEB" || fail "simosa-web 未開啟埠 $WEB:$(cat "$log")" || return 1
  kill -INT "$pid"
  local code=0; wait "$pid" || code=$?
  [[ $code == 0 ]] || fail "SIGINT 後結束碼應為 0,實際 $code;log:$(tail -5 "$log")" || return 1
  grep -q "Unhandled exception" "$log" && { fail "log 不得有 Unhandled exception:$(tail -5 "$log")"; return 1; }
  return 0
}

# 發現 5:設定檔 logs 非 build/logs 時,--duration 結束後仍須經 stop-all --logs 收掉網頁程序並移除 PID 檔
test_logs_alt_cleanup() {
  local alt="$LOGS-alt"
  local cfg; cfg="$(ship_cfg ship-altlogs.json "logs=\"$alt\"")"
  rm -rf "$ROOT/$alt"
  run_launch "$TMP/altlogs.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --no-radar --no-sync --no-visual --duration 2
  [[ $CODE == 0 ]] || fail "結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "沒有 .*simosa.pids" <<<"$OUT" && { fail "stop-all 未帶 --logs(印出「沒有 … simosa.pids」);輸出:$OUT"; return 1; }
  grep -q "已結束全部程序" <<<"$OUT" || fail "應印「已結束全部程序」;輸出:$OUT" || return 1
  [[ ! -f "$ROOT/$alt/simosa.pids" ]] || fail "$alt/simosa.pids 應被移除(內容:$(cat "$ROOT/$alt/simosa.pids"))" || return 1
  sleep 0.5
  tcp_open "$WEB" && { fail "網頁埠 $WEB 仍開啟(simosa-web 被遺留)"; return 1; }
  return 0
}

# 發現 7:.sh 版須接受 --check-wifi(與 ps1 的 -CheckWifi、config 註解一致)
test_instructor_check_wifi_flag() {
  local cfg; cfg="$(instructor_cfg instructor-wifi.json)"
  run_launch "$TMP/wifi.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2 --check-wifi
  grep -q "未知參數" <<<"$OUT" && { fail "--check-wifi 被當成未知參數;輸出:$OUT"; return 1; }
  [[ $CODE != 2 ]] || fail "結束碼不應為 2(參數錯誤);輸出:$OUT" || return 1
  grep -q "Wi-Fi" <<<"$OUT" || fail "應印出 Wi-Fi 檢查結果;輸出:$OUT" || return 1
  grep -q "check-wifi" "$ROOT/config/instructor-laptop.json" || fail "config 註解應說明 --check-wifi / -CheckWifi" || return 1
  grep -q "CheckWifi" "$ROOT/config/instructor-laptop.json" || fail "config 註解應同時提及 Windows 的 -CheckWifi" || return 1
}

# 優雅停止(交接紀錄待辦第 1 項):stop-all.sh 先寫停止檔,SimCore.Host(E05、不設 --duration)自行結束並寫完紀錄 footer,
# 不送 SIGINT、不 SIGKILL;停止檔與 simcore.stopfile 事後都不殘留
test_stop_file_graceful() {
  local dll
  dll="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll simcore SimCore.Host SimosaBRM.SimCore.Host.dll '$ROOT'")" || fail "找不到/無法建置 SimCore.Host" || return 1
  mkdir -p "$ROOT/$LOGS"; rm -f "$ROOT/$LOGS/simcore.stop" "$ROOT/$LOGS/simcore.stopfile" "$ROOT/$LOGS/simosa.pids"
  local rec="$TMP/stopfile.jsonl" log="$TMP/stopfile.log" stop="$ROOT/$LOGS/simcore.stop"
  rm -f "$rec"; : > "$log"
  # 與 start_bg 相同的啟動方式;紀錄寫到專案內的測試目錄,不污染 build/records
  ( exec dotnet "$dll" --scenario data/scenarios/E05_pilot_traffic.yaml --record "$rec" --stop-file "$stop" --port "$WS" --no-udp --no-nmea > "$log" 2>&1 ) &
  local pid=$!; HELPERS+=("$pid")
  local i; for ((i = 0; i < 120; i++)); do tcp_open "$WS" && break; kill -0 "$pid" 2>/dev/null || break; sleep 0.25; done
  tcp_open "$WS" || fail "SimCore.Host 未開啟埠 $WS:$(tail -5 "$log")" || return 1
  printf 'simcore:%s\n' "$pid" > "$ROOT/$LOGS/simosa.pids"
  echo "$stop" > "$ROOT/$LOGS/simcore.stopfile"
  sleep 2   # 先錄一些狀態
  local t0=$SECONDS
  run_launch "$TMP/stopfile.out" bash "$LAUNCH/stop-all.sh" --logs "$LOGS"
  [[ $CODE == 0 ]] || fail "stop-all 結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  local code=0; wait "$pid" || code=$?
  local dt=$((SECONDS - t0))
  [[ $code == 0 ]] || fail "核心結束碼應為 0(自行結束),實際 $code;log:$(tail -5 "$log")" || return 1
  grep -q "寫入停止檔" <<<"$OUT" || fail "stop-all 應回報寫入停止檔;輸出:$OUT" || return 1
  grep -q "強制結束" <<<"$OUT" && { fail "不應強制結束;輸出:$OUT"; return 1; }
  grep -q "收到停止請求(stopFile)" "$log" || fail "核心 log 應有「收到停止請求(stopFile)」:$(tail -5 "$log")" || return 1
  grep -q "紀錄已關閉" "$log" || fail "核心 log 應有「紀錄已關閉」(footer):$(tail -5 "$log")" || return 1
  [[ $dt -lt 10 ]] || fail "核心應在逾時(10 s)前自行結束(耗時 $dt s)" || return 1
  python3 -I - "$rec" <<'PYCHK' || fail "紀錄最後一行應為 footer" || return 1
import json, sys
lines = [l for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
last = json.loads(lines[-1])
assert last["kind"] == "footer" and last["finalTick"] > 0 and "closedUtc" in last, last
print("    footer:tick", last["finalTick"])
PYCHK
  [[ ! -f "$stop" ]] || fail "停止檔應已被核心刪除" || return 1
  [[ ! -f "$ROOT/$LOGS/simcore.stopfile" ]] || fail "simcore.stopfile 應被 stop-all 清掉" || return 1
  [[ ! -f "$ROOT/$LOGS/simosa.pids" ]] || fail "PID 檔應被移除" || return 1
  return 0
}

# USB 操控台服務(src/Contracts/console-serial.md):--console loopback:<讀取檔>,<寫入檔> 以檔案假裝置啟動 app/console 服務,
# 預先寫入 HELLO/RUD/TEL/HB 行 → 回寫檔應有 ACK、LED,LINK,1、DISP,TEL,HAH;console.log 應有轉送到核心的 rudder/telegraph 指令;
# PID 登錄為 console、stop-all(SIGINT)後服務以「結束(原因:SIGINT 或 Ctrl+C)」收尾、無 Unhandled exception
test_console_loopback() {
  local cfg; cfg="$(ship_cfg ship-console.json)"
  local infile="$TMP/console-in.txt" outfile="$TMP/console-out.txt"
  rm -f "$outfile" "$ROOT/$LOGS/console.log"
  printf 'HELLO,1.0.0,RUD+TEL+BTN+LED+DISP\nRUD,10\nTEL,SAH\nBTN,ACK,1\nHB\n' > "$infile"
  run_launch "$TMP/console.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --console "loopback:$infile,$outfile" --no-radar --no-web --no-sync --no-visual --duration 4
  [[ $CODE == 0 ]] || fail "結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "啟動 console" <<<"$OUT" || fail "應啟動 console 服務;輸出:$OUT" || return 1
  grep -q "操控台服務已結束" <<<"$OUT" && { fail "操控台服務不應提早結束;log:$(tail -5 "$ROOT/$LOGS/console.log")"; return 1; }
  [[ -f "$outfile" ]] || fail "應有回寫檔 $outfile" || return 1
  grep -q "^ACK$" "$outfile" || fail "回寫檔應有 ACK:$(head -5 "$outfile")" || return 1
  grep -q "^LED,LINK,1$" "$outfile" || fail "回寫檔應有 LED,LINK,1(已連上核心):$(head -20 "$outfile")" || return 1
  grep -q "^DISP,TEL,SAH$" "$outfile" || fail "回寫檔應有 DISP,TEL,SAH(車鐘指令已到核心):$(grep DISP,TEL "$outfile")" || return 1
  grep -q "^LED,FU,1$" "$outfile" || fail "回寫檔應有 LED,FU,1:$(head -20 "$outfile")" || return 1
  local log="$ROOT/$LOGS/console.log"
  grep -q '"type":"rudder","value":10' "$log" || fail "console.log 應有轉送的 rudder 指令:$(tail -10 "$log")" || return 1
  grep -q '"type":"telegraph","value":"SAH"' "$log" || fail "console.log 應有轉送的 telegraph 指令" || return 1
  grep -q '"type":"alertAcknowledge"' "$log" || fail "console.log 應有轉送的 alertAcknowledge 指令" || return 1
  # SIGINT 同時觸發 Console.CancelKeyPress(原因 Ctrl+C)與 PosixSignalRegistration(原因 SIGINT),先後依 .NET 執行階段而異:兩者皆為優雅結束
  { grep -Fq "結束(原因:SIGINT)" "$log" || grep -Fq "結束(原因:Ctrl+C)" "$log"; } || fail "stop-all 後 console.log 應有「結束(原因:SIGINT 或 Ctrl+C)」:$(tail -3 "$log")" || return 1
  grep -q "Unhandled exception" "$log" && { fail "console.log 不得有 Unhandled exception"; return 1; }
  [[ ! -f "$ROOT/$LOGS/simosa.pids" ]] || fail "PID 檔應被移除" || return 1
  return 0
}

# 第二十四輪:VHF 8767 / 錄影 8768 在 SimCore.Host 內,啟動前一併預檢(占用即 exit 1、不啟動核心);操控台服務無監聽埠不在清單
test_port_busy_vhf_media_fails() {
  local cfg; cfg="$(ship_cfg ship-portbusy-vhf.json)"
  hold_port "$VHF" || fail "測試前置:無法佔住埠 $VHF" || return 1
  run_launch "$TMP/portbusy-vhf.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --no-radar --no-web --no-sync --no-visual
  [[ $CODE == 1 ]] || fail "結束碼應為 1,實際 $CODE;輸出:$OUT" || return 1
  grep -q "埠 $VHF 已被占用(VHF ws)" <<<"$OUT" || fail "應回報「埠 $VHF 已被占用(VHF ws)」;輸出:$OUT" || return 1
  grep -q "已啟動" <<<"$OUT" && { fail "不得印「已啟動」;輸出:$OUT"; return 1; }
  [[ ! -s "$ROOT/$LOGS/simosa.pids" ]] || fail "PID 檔應為空或不存在(核心不應啟動)" || return 1
  cleanup_helpers
  hold_port "$MEDIA" || fail "測試前置:無法佔住埠 $MEDIA" || return 1
  run_launch "$TMP/portbusy-media.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --no-radar --no-web --no-sync --no-visual
  [[ $CODE == 1 ]] || fail "結束碼應為 1,實際 $CODE;輸出:$OUT" || return 1
  grep -q "埠 $MEDIA 已被占用(錄影 HTTP)" <<<"$OUT" || fail "應回報「埠 $MEDIA 已被占用(錄影 HTTP)」;輸出:$OUT" || return 1
  # 腳本的預檢清單不含操控台(無監聽埠)
  grep -n 'PORTS=(' "$LAUNCH/start-ship-laptop.sh" | grep -q "操控台" && { fail "埠預檢清單不應含操控台"; return 1; }
  return 0
}

# 第二十四輪:config ports.nmeaTargets/nmeaBind/nmeaFile → Host --nmea-target(可重複)/--nmea-bind/--nmea-file;以本機 UDP 接收端確認單播目標真的收到
# 完整句型集(含 THS/ALF/ALC;E01 無 ship.mmsi → 無 VDO 並記一次 log),檔案亦寫出;nmea_host_args 的輸出規則(空陣列/null 不加參數)
test_nmea_options_passed() {
  local cfg; cfg="$(ship_cfg ship-nmea.json "ports.nmeaTargets=[\"127.0.0.1:$NMEA_T\", \"127.0.0.1\"]" 'ports.nmeaBind="127.0.0.1"' 'ports.nmeaFile="build/test-tmp/launch/nmea-opts.nmea"')"
  # 純函式:nmea_host_args 的輸出(逐行);預設 config(空陣列/null)不印任何參數
  local args; args="$(bash -c "source '$LAUNCH/launch-common.sh'; load_config '$cfg'; nmea_host_args" 2>&1 | tr '\n' ' ')" || fail "nmea_host_args 出錯:$args" || return 1
  [[ "$args" == "--nmea-target 127.0.0.1:$NMEA_T --nmea-target 127.0.0.1 --nmea-bind 127.0.0.1 --nmea-file build/test-tmp/launch/nmea-opts.nmea " ]] || fail "nmea_host_args 輸出不符:$args" || return 1
  local none; none="$(bash -c "source '$LAUNCH/launch-common.sh'; load_config '$ROOT/config/ship-laptop.json'; nmea_host_args" 2>&1)" || fail "nmea_host_args(預設 config)出錯:$none" || return 1
  [[ -z "$none" ]] || fail "預設 config(nmeaTargets 空、nmeaBind/nmeaFile null)不應產生參數:$none" || return 1
  # 本機 UDP 接收端(假的教官筆電 OpenCPN):收到的資料包寫到檔案
  local got="$TMP/nmea-udp-got.txt"; : > "$got"
  python3 -I -c 'import socket,sys,time
s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("127.0.0.1", int(sys.argv[1]))); s.settimeout(1.0)
t0=time.time(); n=0
with open(sys.argv[2], "w") as f:
    while time.time()-t0 < 40:
        try: d,_=s.recvfrom(65535)
        except socket.timeout: continue
        f.write(d.decode("ascii", "replace")); f.flush(); n+=1
        if n >= 60: break' "$NMEA_T" "$got" >/dev/null 2>&1 &
  HELPERS+=("$!"); disown
  run_launch "$TMP/nmea-opts.out" bash "$LAUNCH/start-ship-laptop.sh" --config "$cfg" --no-radar --no-web --no-sync --no-visual --duration 3
  [[ $CODE == 0 ]] || fail "結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  local log="$ROOT/$LOGS/simcore.log"
  grep -q -- "--nmea-target 127.0.0.1:$NMEA_T --nmea-target 127.0.0.1 --nmea-bind 127.0.0.1 --nmea-file build/test-tmp/launch/nmea-opts.nmea" "$log" || fail "simcore.log 的啟動列應含 NMEA 參數:$(grep -m1 '^#' "$log")" || return 1
  grep -q -- "--vhf-port $VHF --media-port $MEDIA" "$log" || fail "啟動列應含 --vhf-port/--media-port:$(grep -m1 '^#' "$log")" || return 1
  grep -q "與 127.0.0.1:$NMEA_T、127.0.0.1:10110(綁定 127.0.0.1)" "$log" || fail "Host 應回報額外單播端點與綁定位址:$(grep 'NMEA 0183' "$log")" || return 1
  grep -q "NMEA 檔案:build/test-tmp/launch/nmea-opts.nmea" "$log" || fail "Host 應回報 NMEA 檔案路徑:$(grep 'NMEA' "$log")" || return 1
  grep -q "NMEA:自船 AIS !AIVDO 不送" "$log" || fail "E01 無 ship.mmsi:Host 應記一次「自船 AIS !AIVDO 不送」:$(grep 'NMEA' "$log")" || return 1
  local f="$ROOT/build/test-tmp/launch/nmea-opts.nmea"
  [[ -s "$f" ]] || fail "應寫出 NMEA 檔 $f" || return 1
  grep -q '^\$GPGGA' "$f" && grep -q '^\$HETHS' "$f" && grep -q '^\$IIALC,01,01,00,0' "$f" || fail "NMEA 檔應含 GGA/THS/ALC:$(head -3 "$f")" || return 1
  grep -q 'AIVDO' "$f" && { fail "E01 無 MMSI 不應有 VDO"; return 1; }
  local i; for ((i = 0; i < 20; i++)); do [[ -s "$got" ]] && grep -q '^\$GPGGA' "$got" && break; sleep 0.25; done
  grep -q '^\$GPGGA' "$got" || fail "UDP 單播目標 127.0.0.1:$NMEA_T 應收到 GGA(收到:$(head -c 200 "$got" 2>/dev/null))" || return 1
  grep -q '^\$HETHS' "$got" || fail "UDP 單播目標應收到 THS" || return 1
  return 0
}

# 發現 8:需求追溯表的情境檔數與 xfail 歸屬須與儲存庫一致
test_traceability_numbers() {
  local doc="$ROOT/docs/驗收/需求追溯表.md"
  local n; n="$(ls "$ROOT"/data/scenarios/*.yaml | wc -l)"
  local t; t="$(find "$ROOT/data/scenarios/templates" -name '*.yaml' | wc -l)"
  [[ $(grep -c "($n 檔" "$doc") -ge 2 ]] || fail "追溯表(R4 列與第 2 節情境列)的情境檔數應為 $n(data/scenarios/*.yaml)" || return 1
  grep -q "($((n - 1)) 檔\|($((n + 1)) 檔" "$doc" && { fail "追溯表仍有舊的情境檔數($((n - 1)) 或 $((n + 1)) 檔)"; return 1; }
  grep -Eq "templates/examples\`?[^|]{0,20}$t 檔" "$doc" || fail "追溯表應註明 templates/examples $t 檔" || return 1
  grep -q "FSB1 4 項" "$doc" && grep -q "FSB2 3 項" "$doc" || fail "R2 狀態應寫明 pytest 7 xfail = FSB1 4 項 + FSB2 3 項" || return 1
  grep -q "7 項 xfail:右迴旋橫距" "$doc" && { fail "R2 狀態仍把 7 項 xfail 全歸 FSB1 滿載"; return 1; }
  return 0
}

# 第三十輪審查 S-F4:launch_git_info(soak 報告的 gitRev / gitDirty)— 有 git:乾淨 / 修改 / 未追蹤檔;沒有 git(LAUNCH_GIT_NO_GIT=1):
# 鬆散 ref、packed-refs、git worktree(.git 是檔案 + commondir)、不是儲存庫。暫存儲存庫在 build/test-tmp/launch/gitinfo/(不動本儲存庫)
test_git_info() {
  command -v git >/dev/null || { echo "    略過:沒有 git"; return 0; }
  local d="$TMP/gitinfo" g=(git -c user.name=test -c user.email=test@example.invalid -c init.defaultBranch=main -c core.hooksPath=/dev/null)
  rm -rf "$d"; mkdir -p "$d/repo" "$d/plain"
  "${g[@]}" -C "$d/repo" init -q && echo a > "$d/repo/a.txt" && "${g[@]}" -C "$d/repo" add a.txt && "${g[@]}" -C "$d/repo" commit -q -m init || fail "建立暫存儲存庫失敗" || return 1
  local head; head="$(git -C "$d/repo" rev-parse HEAD)"
  ( . "$LAUNCH/launch-common.sh"
    launch_git_info "$d/repo"; [[ "$LAUNCH_GIT_REV" == "$head" && "$LAUNCH_GIT_DIRTY" == false ]] || { echo "    乾淨:$LAUNCH_GIT_REV / $LAUNCH_GIT_DIRTY"; exit 1; }
    echo b >> "$d/repo/a.txt"; launch_git_info "$d/repo"; [[ "$LAUNCH_GIT_DIRTY" == true ]] || { echo "    修改追蹤檔應為 dirty:$LAUNCH_GIT_DIRTY"; exit 1; }
    git -C "$d/repo" checkout -q -- a.txt; echo n > "$d/repo/new.txt"; launch_git_info "$d/repo"; [[ "$LAUNCH_GIT_DIRTY" == true ]] || { echo "    未追蹤檔應為 dirty:$LAUNCH_GIT_DIRTY"; exit 1; }
    rm -f "$d/repo/new.txt"
    LAUNCH_GIT_NO_GIT=1 launch_git_info "$d/repo"; [[ "$LAUNCH_GIT_REV" == "$head" && "$LAUNCH_GIT_DIRTY" == unknown ]] || { echo "    沒有 git(鬆散 ref):$LAUNCH_GIT_REV / $LAUNCH_GIT_DIRTY"; exit 1; }
    git -C "$d/repo" pack-refs --all; [[ ! -f "$d/repo/.git/refs/heads/main" ]] || { echo "    pack-refs 後鬆散 ref 應已移除"; exit 1; }
    LAUNCH_GIT_NO_GIT=1 launch_git_info "$d/repo"; [[ "$LAUNCH_GIT_REV" == "$head" ]] || { echo "    沒有 git(packed-refs):$LAUNCH_GIT_REV"; exit 1; }
    git -C "$d/repo" worktree add -q -b wt "$d/wt" >/dev/null 2>&1 || { echo "    git worktree add 失敗"; exit 1; }
    [[ -f "$d/wt/.git" ]] || { echo "    worktree 的 .git 應是檔案"; exit 1; }
    launch_git_info "$d/wt"; [[ "$LAUNCH_GIT_REV" == "$head" && "$LAUNCH_GIT_DIRTY" == false ]] || { echo "    worktree(git):$LAUNCH_GIT_REV / $LAUNCH_GIT_DIRTY"; exit 1; }
    LAUNCH_GIT_NO_GIT=1 launch_git_info "$d/wt"; [[ "$LAUNCH_GIT_REV" == "$head" && "$LAUNCH_GIT_DIRTY" == unknown ]] || { echo "    worktree(沒有 git):$LAUNCH_GIT_REV / $LAUNCH_GIT_DIRTY"; exit 1; }
    LAUNCH_GIT_NO_GIT=1 launch_git_info "$d/plain"; [[ -z "$LAUNCH_GIT_REV" && "$LAUNCH_GIT_DIRTY" == unknown ]] || { echo "    不是儲存庫:$LAUNCH_GIT_REV / $LAUNCH_GIT_DIRTY"; exit 1; }
    exit 0 ) || return 1
  git -C "$d/repo" worktree remove --force "$d/wt" >/dev/null 2>&1; rm -rf "$d"
  return 0
}

# ---- 發行版狀態(教官站頁首的「發行版 <版本>」與降版提醒;docs/design/packaging-sync.md 第 5.1 節)----
# http_get <網址> <標頭輸出檔> <本體輸出檔> [curl 參數…]:印出 HTTP 狀態碼(--path-as-is:不讓 curl 先把 .. 與 // 化簡)
http_get() { local url="$1" hdr="$2" body="$3"; shift 3; curl -s --path-as-is -m 5 -D "$hdr" -o "$body" -w '%{http_code}' "$@" "$url" 2>/dev/null; }
# start_web <log> <simosa-web 參數…>:以真實的 simosa-web 在 $WEB 埠啟動(PID 放 WEB_PID),等埠開啟
start_web() {
  local log="$1"; shift
  local dll; dll="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll web-server StaticWeb simosa-web.dll '$ROOT'")" || return 1
  : > "$log"
  ( exec dotnet "$dll" --port "$WEB" --bind 127.0.0.1 "$@" > "$log" 2>&1 ) &
  WEB_PID=$!; HELPERS+=("$WEB_PID")
  local i; for ((i = 0; i < 40; i++)); do tcp_open "$WEB" && return 0; kill -0 "$WEB_PID" 2>/dev/null || return 1; sleep 0.25; done
  return 1
}
# 與 simosa-pkg status --json 相同形式的狀態檔(另帶網頁用不到、不得送出的欄位):status_fixture <輸出檔> <releaseVersion 的 JSON> [releaseDowngrade 的 JSON]
status_fixture() {
  python3 -I - "$1" "$2" "${3:-}" <<'PY'
import json, sys
st = {"type": "statusResult", "shipId": "FSB1", "deviceId": "SECRET-DEVICE", "version": "2026.12.2",
      "signer": {"keyId": "SECRET-KEYID", "name": "SECRET-SIGNER", "role": "primary"},
      "components": {"software": {"version": "2026.12.2", "summary": "SECRET-SUMMARY"}},
      "trustedKeys": [{"keyId": "SECRET-KEYID", "name": "SECRET-KEYNAME", "role": "primary", "holder": "SECRET-HOLDER"}],
      "environment": {"os": "SECRET-OS", "dotnet": ".NET 8", "collectedUtc": "2026-11-23T00:00:00Z"},
      "warnings": ["SECRET-WARNING D:\\Simosa BRM\\state"], "releaseVersion": json.loads(sys.argv[2])}
if sys.argv[3]: st["releaseDowngrade"] = json.loads(sys.argv[3])
json.dump(st, open(sys.argv[1], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}

# raw_request <方法> <路徑>:以原始 TCP 送一個請求到 $WEB,把整個回應(標頭 + 本體)原樣寫到 stdout(curl -I 不讀本體,量不到 HEAD 有沒有多送)
raw_request() {
  python3 -I - "$WEB" "$1" "$2" <<'PY'
import socket, sys
s = socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=5)
s.sendall(f"{sys.argv[2]} {sys.argv[3]} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n".encode("ascii"))
buf = b""
while True:
    chunk = s.recv(65536)
    if not chunk: break
    buf += chunk
sys.stdout.buffer.write(buf)
PY
}

# simosa-web --status-file:固定網址 /_status/release.json 只送白名單欄位(發行版本、軟體更新包版次、降版資訊、狀態檔時間)、no-store;
# 狀態檔不存在 / 不是 JSON / 字串含不合法的 UTF-8 / 不是 status 的輸出 / 太大 → 200 與各欄 null 的同一格式(「沒有發行版狀態」;原因記在 log,
# 內容不外洩 —— 回 404 的話教官站每次載入瀏覽器都記一筆 console error);/_status/ 之下其他路徑一律 404(不落到網頁目錄,字首不分大小寫);
# 沒有 --status-file 是 404
test_web_status_file() {
  local d="$TMP/statusweb"; rm -rf "$d"; mkdir -p "$d/web/_status" "$d/web/_STATUS"
  echo '<html>ok</html>' > "$d/web/index.html"
  echo '{"secret":"WEBROOT-SECRET"}' > "$d/web/_status/release.json"   # 網頁目錄內同名的檔不得被送出(/_status/ 是保留的字首)
  # 字首不分大小寫:大小寫不同的資料夾(Linux 上是另一個資料夾;Windows 上與 _status 同一個,已存在時這一行只是覆寫同一個檔)也不得被送出
  echo '{"secret":"WEBROOT-SECRET-UPPER"}' > "$d/web/_STATUS/release.json"
  local st="$d/release-status.json" h="$d/h.txt" b="$d/b.txt" code u
  local EMPTY='{"format":"simosa-release-status/1","generatedUtc":null,"releaseVersion":null,"softwareVersion":null,"releaseDowngrade":null}'
  # (1) 沒有 --status-file:404;網頁目錄的檔案照常
  start_web "$d/web1.log" --root "$d/web" || fail "simosa-web 未啟動:$(cat "$d/web1.log")" || return 1
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  [[ "$code" == 404 ]] || fail "沒有 --status-file 時應為 404,實際 $code" || return 1
  grep -q "WEBROOT-SECRET" "$b" && { fail "網頁目錄內的 _status/release.json 不得被送出"; return 1; }
  [[ "$(http_get "http://127.0.0.1:$WEB/index.html" "$h" "$b")" == 200 ]] && grep -q '<html>ok</html>' "$b" || fail "網頁目錄的 index.html 應照常提供" || return 1
  kill -INT "$WEB_PID"; wait "$WEB_PID" 2>/dev/null
  # (2) --status-file 指到還不存在的檔(開發模式、status 逾時或失敗):照常啟動,回 200 與各欄 null(教官站不顯示、瀏覽器不記 console error),
  #     原因記在 log;之後檔案出現即送出內容(每次請求重讀)
  start_web "$d/web2.log" --root "$d/web" --status-file "$st" || fail "simosa-web(--status-file)未啟動:$(cat "$d/web2.log")" || return 1
  grep -q "發行版狀態:$st → /_status/release.json" "$d/web2.log" || fail "啟動訊息應列出狀態檔與網址:$(cat "$d/web2.log")" || return 1
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  [[ "$code" == 200 && "$(cat "$b")" == "$EMPTY" ]] || fail "狀態檔不存在時應為 200 與各欄 null,實際 $code:$(cat "$b")" || return 1
  grep -qi '^cache-control: no-store' "$h" && grep -qi '^content-type: application/json' "$h" || fail "「沒有發行版狀態」也應為 no-store 的 JSON:$(cat "$h")" || return 1
  grep -q "200 GET /_status/release.json HTTP/1.1(狀態檔不存在;回覆「沒有發行版狀態」)" "$d/web2.log" || fail "log 應記下狀態檔不存在:$(tail -3 "$d/web2.log")" || return 1
  status_fixture "$st" '"2026.11.30"'
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  [[ "$code" == 200 ]] || fail "狀態檔存在時應為 200,實際 $code;log:$(tail -3 "$d/web2.log")" || return 1
  grep -qi '^cache-control: no-store' "$h" || fail "應為 Cache-Control: no-store:$(cat "$h")" || return 1
  grep -qi '^content-type: application/json' "$h" || fail "應為 application/json:$(cat "$h")" || return 1
  grep -q "SECRET" "$b" && { fail "不得送出 status 的其他欄位(裝置、金鑰、環境、警告):$(cat "$b")"; return 1; }
  python3 -I - "$b" <<'PY' || fail "回應應只有白名單欄位、版本 2026.11.30、沒有降版:$(cat "$b")" || return 1
import json, re, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
assert sorted(j) == ["format", "generatedUtc", "releaseDowngrade", "releaseVersion", "softwareVersion"], sorted(j)
assert j["format"] == "simosa-release-status/1" and j["releaseVersion"] == "2026.11.30" and j["releaseDowngrade"] is None, j
assert j["softwareVersion"] == "2026.12.2", j   # status 的 components.software.version(軟體更新包版次);其餘 components 的內容不送
assert re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", j["generatedUtc"]), j
PY
  # HEAD:相同的標頭、沒有本體(以原始 TCP 讀整個回應:curl -I 不讀本體,本體有沒有多送它量不到);POST:405;查詢字串不影響
  raw_request HEAD /_status/release.json > "$d/head.raw" && raw_request GET /_status/release.json > "$d/get.raw" || fail "原始請求失敗" || return 1
  python3 -I - "$d/head.raw" "$d/get.raw" <<'PY' || fail "HEAD 應回 200、與 GET 相同的 Content-Length 與 no-store、標頭之後沒有任何位元組:$(head -c 600 "$d/head.raw")" || return 1
import sys
def split(path):
    raw = open(path, "rb").read()
    head, sep, body = raw.partition(b"\r\n\r\n")
    assert sep, raw[:200]
    lines = head.decode("ascii").split("\r\n")
    return lines[0], {k.strip().lower(): v.strip() for k, v in (l.split(":", 1) for l in lines[1:])}, body
hs, hh, hb = split(sys.argv[1])
gs, gh, gb = split(sys.argv[2])
assert hs == gs == "HTTP/1.1 200 OK", (hs, gs)
assert len(gb) > 0 and int(gh["content-length"]) == len(gb), (gh, len(gb))
assert hh == gh, (hh, gh)
assert hh["cache-control"] == "no-store", hh
assert hb == b"", ("HEAD 的回應帶了本體", hb[:80])
PY
  [[ "$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b" -X POST)" == 405 ]] || fail "POST 應為 405" || return 1
  [[ "$(http_get "http://127.0.0.1:$WEB/_status/release.json?x=../../etc/passwd" "$h" "$b")" == 200 ]] || fail "查詢字串不影響" || return 1
  # /_status/ 之下其他路徑(含大小寫不同、結尾斜線、多一層、雙斜線)一律 404,不落到網頁目錄;.. 為 400
  #   /_STATUS/release.json:網頁目錄裡真的有這個檔(上面建的),字首若區分大小寫它就會被當成一般檔案送出
  for u in /_status /_status/ /_status/other.json /_STATUS/release.json /_STATUS/ /_status/release.json/ /_status/release.json/x //_status/release.json /_Status/Release.json; do
    code="$(http_get "http://127.0.0.1:$WEB$u" "$h" "$b")"
    [[ "$code" == 404 ]] || fail "$u 應為 404,實際 $code" || return 1
    grep -q "SECRET" "$b" && { fail "$u 的回應不得有狀態檔或網頁目錄 _status 的內容"; return 1; }
  done
  for u in /_status/../index.html /_status/..%2f..%2fetc/passwd '/_status/release.json/..%5c..%5cx'; do
    code="$(http_get "http://127.0.0.1:$WEB$u" "$h" "$b")"
    [[ "$code" == 400 ]] || fail "$u 應為 400(bad path),實際 $code" || return 1
  done
  # 降版資訊原樣帶出(版本欄位只接受數字版本;其他型別 / 內容以 null、false 帶過);releaseVersion 不是版本 → null
  status_fixture "$st" '"<script>alert(1)</script>"' '{"releaseVersion":"2026.11.1","basisVersion":"2026.12.2","previousReleaseVersion":"2026.12.1","softwareVersion":"../../x","highestVersion":7,"softwareStale":true,"message":"降版:測試訊息","extra":"SECRET-EXTRA"}'
  [[ "$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")" == 200 ]] || fail "降版狀態檔應為 200" || return 1
  grep -q "SECRET\|script" "$b" && { fail "不得送出白名單以外的內容:$(cat "$b")"; return 1; }
  python3 -I - "$b" <<'PY' || fail "降版資訊的欄位不符:$(cat "$b")" || return 1
import json, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
assert j["releaseVersion"] is None, j
assert j["softwareVersion"] == "2026.12.2", j
d = j["releaseDowngrade"]
assert sorted(d) == ["basisVersion", "highestVersion", "message", "previousReleaseVersion", "releaseVersion", "softwareStale", "softwareVersion"], sorted(d)
assert (d["releaseVersion"], d["basisVersion"], d["previousReleaseVersion"], d["softwareVersion"], d["highestVersion"], d["softwareStale"], d["message"]) == ("2026.11.1", "2026.12.2", "2026.12.1", None, None, True, "降版:測試訊息"), d
PY
  # components.software.version 不是版本 / components 不是物件:softwareVersion 為 null(其他欄位照常)
  python3 -I - "$st" <<'PY'
import json, sys
st = json.load(open(sys.argv[1], encoding="utf-8"))
st["releaseVersion"] = "2026.11.30"; st.pop("releaseDowngrade", None)
st["components"] = {"software": {"version": "SECRET-NOT-A-VERSION"}, "charts": {"version": "9.9.9"}}
json.dump(st, open(sys.argv[1], "w", encoding="utf-8"))
PY
  [[ "$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")" == 200 ]] && grep -q '"releaseVersion":"2026.11.30","softwareVersion":null' "$b" || fail "software 版次不是版本時應為 null:$(cat "$b")" || return 1
  grep -q "SECRET\|9.9.9" "$b" && { fail "不得送出 components 的其他內容:$(cat "$b")"; return 1; }
  # 狀態檔不是 JSON / 不是 status 的輸出 / 是陣列 / 字串含不合法的 UTF-8(寫到一半斷電、磁碟錯誤)/ 太大:200 與各欄 null,
  # 內容不外洩(伺服器 log 也不記內容),伺服器不因此斷線(修正前:不合法的 UTF-8 讓連線直接關閉、log 沒有任何一行)
  local bad n=0
  for bad in 'SECRET-NOT-JSON root:x:0:0' '{"type":"other","releaseVersion":"9.9.9","password":"SECRET-PW"}' '["SECRET-ARRAY"]' '{"releaseVersion":"9.9.9","note":"SECRET-NOTYPE"}' \
             '{"type":"statusResult","releaseVersion":"9.9.9","releaseDowngrade":{"releaseVersion":"9.9.1","basisVersion":"9.9.2","message":"SECRET \xa4\xa4\xa4\xe5 \xff\xfe"}}' \
             '{"type":"\xff\xfe","releaseVersion":"9.9.9","note":"SECRET-BADTYPE"}' \
             '{"type":"statusResult","releaseVersion":"9.9.\xff9","components":{"software":{"version":"9.9.9"}}}'; do
    n=$((n + 1))
    printf "$bad" > "$st"
    code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
    [[ "$code" == 200 && "$(cat "$b")" == "$EMPTY" ]] || fail "不合格的狀態檔(第 $n 種)應為 200 與各欄 null,實際 $code:$(head -c 300 "$b");log:$(tail -2 "$d/web2.log")" || return 1
    grep -qi '^cache-control: no-store' "$h" || fail "不合格的狀態檔(第 $n 種)也應為 no-store" || return 1
  done
  python3 -I -c 'import sys; open(sys.argv[1], "w").write("{\"type\":\"statusResult\",\"releaseVersion\":\"2026.11.30\",\"pad\":\"" + "x" * 300000 + "\"}")' "$st"
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  [[ "$code" == 200 && "$(cat "$b")" == "$EMPTY" ]] || fail "超過大小上限的狀態檔應為 200 與各欄 null,實際 $code" || return 1
  grep -q "SECRET" "$d/web2.log" && { fail "伺服器 log 不得記下狀態檔內容"; return 1; }
  grep -q "狀態檔不是有效的 JSON;回覆「沒有發行版狀態」" "$d/web2.log" && grep -q "狀態檔不是 simosa-pkg status --json 的輸出" "$d/web2.log" && grep -q "字串含不合法的 UTF-8" "$d/web2.log" && grep -q "超過上限" "$d/web2.log" \
    || fail "伺服器 log 應記下各種原因:$(tail -8 "$d/web2.log")" || return 1
  grep -q "內部錯誤" "$d/web2.log" && { fail "不應有未預期的內部錯誤:$(grep "內部錯誤" "$d/web2.log" | head -3)"; return 1; }
  [[ "$(grep -c ' /_status/release.json' "$d/web2.log")" -ge 12 ]] || fail "每個請求都應在 log 留下一行" || return 1
  kill -INT "$WEB_PID"; local wcode=0; wait "$WEB_PID" || wcode=$?
  [[ $wcode == 0 ]] || fail "simosa-web 應正常結束(結束碼 $wcode)" || return 1
  # (3) --status-file 必須是 .json 檔的路徑(不是資料夾、不是其他副檔名):結束碼 2,不啟動
  local dll; dll="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll web-server StaticWeb simosa-web.dll '$ROOT'")"
  #     前面加 timeout:檢查退化(沒有拒絕)時伺服器會正常啟動、在前景一直跑,測試要以結束碼 124 失敗,不可以卡到整個工作逾時
  timeout 15 dotnet "$dll" --root "$d/web" --port "$WEB" --bind 127.0.0.1 --status-file /etc/passwd > "$d/web3.log" 2>&1; code=$?
  [[ $code == 2 ]] && grep -q -- "--status-file 必須是 .json" "$d/web3.log" || fail "--status-file /etc/passwd 應以結束碼 2 拒絕(實際 $code;124 = 沒有拒絕、伺服器啟動了):$(cat "$d/web3.log")" || return 1
  mkdir -p "$d/dir.json"
  timeout 15 dotnet "$dll" --root "$d/web" --port "$WEB" --bind 127.0.0.1 --status-file "$d/dir.json" > "$d/web4.log" 2>&1; code=$?
  [[ $code == 2 ]] || fail "--status-file 指到資料夾應以結束碼 2 拒絕(實際 $code;124 = 沒有拒絕、伺服器啟動了)" || return 1
  # 狀態檔放在網頁目錄內會被當成一般檔案整份送出:拒絕
  timeout 15 dotnet "$dll" --root "$d/web" --port "$WEB" --bind 127.0.0.1 --status-file "$d/web/status.json" > "$d/web5.log" 2>&1; code=$?
  [[ $code == 2 ]] && grep -q "不可放在網頁目錄" "$d/web5.log" || fail "--status-file 在網頁目錄內應以結束碼 2 拒絕(實際 $code;124 = 沒有拒絕、伺服器啟動了):$(cat "$d/web5.log")" || return 1
  return 0
}

# raw_headers <方法> <路徑> <額外的標頭行…>:以原始 TCP 送請求(標頭行原樣送出:可以送重複的、大小寫不同的、前後有空白的 Origin),
#   把回應的狀態行與標頭印成「小寫名稱: 值」一行一個(本體不印)。環境變數 RAW_BODY 有值時接在標頭結尾的空行之後一起送出(請求的本體;同一次寫入)
raw_headers() {
  python3 -I - "$WEB" "$@" <<'PY'
import os, socket, sys
port, method, path, extra = int(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4:]
s = socket.create_connection(("127.0.0.1", port), timeout=5)
s.sendall(("\r\n".join([f"{method} {path} HTTP/1.1", "Host: 127.0.0.1", *extra, "Connection: close", "", ""]) + os.environ.get("RAW_BODY", "")).encode("latin-1"))
buf = b""
while True:
    chunk = s.recv(65536)
    if not chunk: break
    buf += chunk
head = buf.partition(b"\r\n\r\n")[0].decode("latin-1").split("\r\n")
print(head[0])
for line in head[1:]:
    k, _, v = line.partition(":")
    print(f"{k.strip().lower()}: {v.strip()}")
PY
}

# 第三十一輪:教官筆電的教官站(頁面由教官筆電自己的 simosa-web 提供:http://127.0.0.1:<埠>)要跨來源讀「船上筆電」的 /_status/release.json。
# simosa-web 只在 Origin 是 http://127.0.0.1[:埠] 或 http://localhost[:埠](完全相符)時回 Access-Control-Allow-Origin: <該 Origin> 與 Vary: Origin;
# 其他 Origin(別的主機、https、null、[::1]、大小寫不同、結尾斜線、看起來像迴路位址的主機名、不合法的埠、重複的 Origin)與沒有 Origin 都不加 CORS 標頭。
# 只有這個網址有:靜態檔、/_status/ 之下其他路徑(404)、POST / OPTIONS(405)、沒有 --status-file(404)都沒有;HEAD 與 GET 相同。
test_web_status_cors() {
  local d="$TMP/statuscors"; rm -rf "$d"; mkdir -p "$d/web"
  echo '<html>ok</html>' > "$d/web/index.html"
  local st="$d/release-status.json" h="$d/h.txt" b="$d/b.txt" code o out
  status_fixture "$st" '"2026.11.30"'
  start_web "$d/web.log" --root "$d/web" --status-file "$st" || fail "simosa-web 未啟動:$(cat "$d/web.log")" || return 1
  local url="http://127.0.0.1:$WEB/_status/release.json"
  # (1) 允許:本機迴路位址的頁面(帶 / 不帶埠);回應原樣帶回該 Origin 與 Vary: Origin,內容與沒有 Origin 時相同
  for o in "http://127.0.0.1:8080" "http://127.0.0.1" "http://localhost:8080" "http://localhost" "http://127.0.0.1:1" "http://localhost:65535"; do
    code="$(http_get "$url" "$h" "$b" -H "Origin: $o")"
    [[ "$code" == 200 ]] || fail "Origin $o 應為 200,實際 $code" || return 1
    [[ "$(grep -ci '^access-control-allow-origin:' "$h")" == 1 ]] && grep -qi "^access-control-allow-origin: $o"$'\r''$' "$h" || fail "Origin $o 應回 Access-Control-Allow-Origin: $o(只有一個):$(tr -d '\r' < "$h")" || return 1
    grep -qi $'^vary: Origin\r$' "$h" || fail "Origin $o 應回 Vary: Origin:$(tr -d '\r' < "$h")" || return 1
    grep -qi '^access-control-allow-credentials\|^access-control-allow-headers\|^access-control-allow-methods' "$h" && { fail "不應有其他 Access-Control-* 標頭(簡單請求、不帶認證):$(tr -d '\r' < "$h")"; return 1; }
    grep -qi '^cache-control: no-store' "$h" && grep -q '"releaseVersion":"2026.11.30"' "$b" || fail "Origin $o 的回應內容應與同源相同(no-store、版本 2026.11.30)" || return 1
  done
  # (2) 不允許:沒有任何 CORS 標頭(瀏覽器會擋下;內容仍是 200,同源 / curl 照舊)
  for o in "http://192.168.77.21:8080" "http://192.168.77.21" "http://example.com" "https://127.0.0.1:8080" "https://localhost" "null" "http://[::1]:8080" "http://[::1]" \
           "HTTP://127.0.0.1:8080" "http://LOCALHOST:8080" "http://Localhost" "http://127.0.0.1:8080/" "http://localhost/" "http://127.0.0.1.evil.example" "http://127.0.0.1.evil.example:8080" \
           "http://localhost.evil.example" "http://localhost.evil.example:8080" "http://evil.example/http://127.0.0.1:8080" "http://127.0.0.1:8080.evil.example" "http://127.0.0.1:8080@evil.example" \
           "http://user@127.0.0.1:8080" "http://127.0.0.2:8080" "http://127.0.0.10" "http://0.0.0.0:8080" "http://localhost:0" "http://localhost:08080" "http://localhost:65536" "http://localhost:123456" \
           "http://localhost:" "http://localhost:80a" "http://localhost:-1" "http://127.0.0.1:8080 http://evil.example" "http://127.0.0.1:8080, http://evil.example" "ws://127.0.0.1:8080" "127.0.0.1:8080" "localhost" "*"; do
    code="$(http_get "$url" "$h" "$b" -H "Origin: $o")"
    [[ "$code" == 200 ]] || fail "Origin $o:內容仍應為 200,實際 $code" || return 1
    grep -qi '^access-control-\|^vary:' "$h" && { fail "Origin「$o」不應有 CORS 標頭:$(tr -d '\r' < "$h")"; return 1; }
    grep -q '"releaseVersion":"2026.11.30"' "$b" || fail "Origin $o:內容不變" || return 1
  done
  # (3) 沒有 Origin(同源的頁面、curl、啟動腳本的檢查):照舊,沒有 CORS 標頭
  code="$(http_get "$url" "$h" "$b")"
  [[ "$code" == 200 ]] || fail "沒有 Origin 應為 200" || return 1
  grep -qi '^access-control-\|^vary:' "$h" && { fail "沒有 Origin 時不應有 CORS 標頭:$(tr -d '\r' < "$h")"; return 1; }
  # (4) 標頭名稱不分大小寫、值的前後空白不計;重複的 Origin(瀏覽器不會送)不猜哪一個:不加
  for o in "origin: http://127.0.0.1:8080" "ORIGIN:http://127.0.0.1:8080" "Origin:   http://127.0.0.1:8080  "; do
    out="$(raw_headers GET /_status/release.json "$o")"
    grep -q '^access-control-allow-origin: http://127.0.0.1:8080$' <<<"$out" && grep -q '^vary: Origin$' <<<"$out" || fail "標頭行「$o」應允許:$out" || return 1
  done
  out="$(raw_headers GET /_status/release.json "Origin: http://127.0.0.1:8080" "Origin: http://evil.example")"
  grep -q '^HTTP/1.1 200' <<<"$out" || fail "重複的 Origin:內容仍應為 200:$out" || return 1
  grep -qi '^access-control-\|^vary:' <<<"$out" && { fail "重複的 Origin 不應有 CORS 標頭:$out"; return 1; }
  out="$(raw_headers GET /_status/release.json "Origin: http://127.0.0.1:8080" "Origin: http://127.0.0.1:8080")"
  grep -qi '^access-control-' <<<"$out" && { fail "重複的 Origin(即使相同)不應有 CORS 標頭:$out"; return 1; }
  out="$(raw_headers GET /_status/release.json "Origin:")"
  grep -qi '^access-control-' <<<"$out" && { fail "空的 Origin 不應有 CORS 標頭:$out"; return 1; }
  out="$(raw_headers GET /_status/release.json "X-Origin: http://127.0.0.1:8080" "Referer: http://127.0.0.1:8080/")"
  grep -qi '^access-control-' <<<"$out" && { fail "不是 Origin 的標頭不算:$out"; return 1; }
  # 審查 S-2:名稱以 Origin 開頭的其他標頭(Origin-Agent-Cluster 等)不是 Origin —— 不可以讓真正的 Origin 變成「重複」而不給 CORS 標頭,自己也不算 Origin
  out="$(raw_headers GET /_status/release.json "Origin: http://127.0.0.1:8080" "Origin-Agent-Cluster: ?1")"
  grep -q '^access-control-allow-origin: http://127.0.0.1:8080$' <<<"$out" && grep -q '^vary: Origin$' <<<"$out" || fail "Origin 與 Origin-Agent-Cluster 並存時仍應允許:$out" || return 1
  out="$(raw_headers GET /_status/release.json "Origin-Agent-Cluster: http://127.0.0.1:8080" "Origin: http://localhost:8080" "OriginX: http://127.0.0.1:8080")"
  grep -q '^access-control-allow-origin: http://localhost:8080$' <<<"$out" || fail "名稱以 Origin 開頭的其他標頭不算 Origin(應允許真正的 Origin):$out" || return 1
  out="$(raw_headers GET /_status/release.json "Origin-Agent-Cluster: http://127.0.0.1:8080")"
  grep -qi '^access-control-\|^vary:' <<<"$out" && { fail "只有 Origin-Agent-Cluster(沒有 Origin)不應有 CORS 標頭:$out"; return 1; }
  # 審查 S-2:只看標頭 —— 空行之後的本體裡的「Origin:」不算(沒有 Origin 標頭的請求不加;有 Origin 標頭的不因為本體而變成「重複」)
  out="$(RAW_BODY=$'Origin: http://127.0.0.1:8080\r\n\r\n' raw_headers GET /_status/release.json "Content-Length: 33")"
  grep -q '^HTTP/1.1 200' <<<"$out" || fail "帶本體的 GET 仍應為 200:$out" || return 1
  grep -qi '^access-control-\|^vary:' <<<"$out" && { fail "本體裡的 Origin 不算(不應有 CORS 標頭):$out"; return 1; }
  out="$(RAW_BODY=$'Origin: http://evil.example\r\n\r\n' raw_headers GET /_status/release.json "Origin: http://127.0.0.1:8080" "Content-Length: 31")"
  grep -q '^access-control-allow-origin: http://127.0.0.1:8080$' <<<"$out" || fail "本體裡的 Origin 不影響標頭的 Origin(仍應允許):$out" || return 1
  # (5) HEAD:與 GET 相同的標頭(含 CORS)、沒有本體
  out="$(raw_headers HEAD /_status/release.json "Origin: http://localhost:8080")"
  [[ "$out" == "$(raw_headers GET /_status/release.json "Origin: http://localhost:8080")" ]] || fail "HEAD 的標頭應與 GET 相同:$out" || return 1
  grep -q '^access-control-allow-origin: http://localhost:8080$' <<<"$out" || fail "HEAD 也應有 CORS 標頭:$out" || return 1
  # (6) 只有這個網址有:靜態檔(200)、/_status/ 之下其他路徑(404)、查無此檔(404)、POST / OPTIONS(405;簡單請求不需要 preflight)都沒有
  local req
  for req in "GET /index.html 200" "GET / 200" "GET /_status/other.json 404" "GET /_STATUS/release.json 404" "GET /_status/ 404" "GET /nope.json 404" \
             "POST /_status/release.json 405" "OPTIONS /_status/release.json 405" "OPTIONS /index.html 405"; do
    set -- $req
    out="$(raw_headers "$1" "$2" "Origin: http://127.0.0.1:8080" "Access-Control-Request-Method: GET")"
    grep -q "^HTTP/1.1 $3 " <<<"$out" || fail "$1 $2 應為 $3:$out" || return 1
    grep -qi '^access-control-\|^vary:' <<<"$out" && { fail "$1 $2 不應有 CORS 標頭:$out"; return 1; }
  done
  # (7) log:允許的 Origin 記下來(形式固定);不允許的只記「不是本機迴路位址的頁面」,不記內容
  code="$(http_get "$url" "$h" "$b" -H "Origin: http://SECRET-ORIGIN.example")"
  grep -q "200 GET /_status/release.json HTTP/1.1(跨來源 http://127.0.0.1:8080)" "$d/web.log" || fail "log 應記下允許的 Origin:$(tail -3 "$d/web.log")" || return 1
  grep -q "跨來源的 Origin 不是本機迴路位址的頁面,不加 CORS 標頭" "$d/web.log" || fail "log 應記下不允許的跨來源請求" || return 1
  grep -q "SECRET-ORIGIN\|evil.example" "$d/web.log" && { fail "log 不得記下不允許的 Origin 內容"; return 1; }
  grep -q "內部錯誤" "$d/web.log" && { fail "不應有未預期的內部錯誤:$(grep "內部錯誤" "$d/web.log" | head -3)"; return 1; }
  # (8) 狀態檔不存在(船上是開發模式、status 逾時):「沒有發行版狀態」也照樣帶 CORS 標頭 —— 教官筆電讀得到各欄 null(不顯示),瀏覽器不記錯誤
  rm -f "$st"
  code="$(http_get "$url" "$h" "$b" -H "Origin: http://127.0.0.1:8080")"
  [[ "$code" == 200 ]] && grep -q '"releaseVersion":null' "$b" && grep -qi $'^access-control-allow-origin: http://127.0.0.1:8080\r$' "$h" || fail "狀態檔不存在時也應帶 CORS 標頭(200、各欄 null):$(tr -d '\r' < "$h")" || return 1
  kill -INT "$WEB_PID"; local wcode=0; wait "$WEB_PID" || wcode=$?
  [[ $wcode == 0 ]] || fail "simosa-web 應正常結束(結束碼 $wcode)" || return 1
  # (9) 沒有 --status-file(這個伺服器不提供發行版狀態):404,沒有 CORS 標頭
  start_web "$d/web2.log" --root "$d/web" || fail "simosa-web(沒有 --status-file)未啟動" || return 1
  out="$(raw_headers GET /_status/release.json "Origin: http://127.0.0.1:8080")"
  grep -q '^HTTP/1.1 404 ' <<<"$out" || fail "沒有 --status-file 應為 404:$out" || return 1
  grep -qi '^access-control-\|^vary:' <<<"$out" && { fail "404 不應有 CORS 標頭:$out"; return 1; }
  kill -INT "$WEB_PID"; wait "$WEB_PID" 2>/dev/null
  # --help 說明跨來源的規則
  local dll; dll="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll web-server StaticWeb simosa-web.dll '$ROOT'")"
  dotnet "$dll" --help | grep -q "http://127.0.0.1\[:埠\] 或 http://localhost\[:埠\]" || fail "--help 應說明哪些來源讀得到 /_status/release.json" || return 1
  return 0
}

# 第三十一輪:船上筆電的網頁位址。ship_web_url / ship_web_page(launch-common.sh)的規則,與教官筆電腳本的輸出:
# 沒設 ports.shipWeb → 教官站網址不加參數(網頁由 ws 的主機推得 http://<船上 IP>:8080),提示寫 8080;設了 → 只有教官站頁面加上 shipWeb=http://<船上 IP>:<埠>;
# 不是埠的值 → 警告並略過。standalone(核心在本機)不取船上的版本:ship_web_url 不印任何東西
test_instructor_ship_web() {
  local out
  # 各組以空白分詞成 <standalone> <IP> <埠>;'false  8081' 分詞後只有兩個值(IP = 8081、沒有埠)
  out="$(bash -c "source '$LAUNCH/launch-common.sh'
    for a in 'false 192.168.77.11 8081' 'true 192.168.77.11 8081' '1 192.168.77.11 8081' 'false 192.168.77.11 ' 'false 192.168.77.11 abc' 'false 192.168.77.11 0' \
             'false 192.168.77.11 65535' 'false 192.168.77.11 65536' 'false 192.168.77.11 08081' 'false  8081' 'false 192.168.77.11 80;x'; do
      set -- \$a; echo \"[\$a]=[\$(ship_web_url \"\$1\" \"\${2:-}\" \"\${3:-}\")]\"; done" 2>&1)" || fail "ship_web_url 出錯:$out" || return 1
  local want='[false 192.168.77.11 8081]=[http://192.168.77.11:8081]
[true 192.168.77.11 8081]=[]
[1 192.168.77.11 8081]=[]
[false 192.168.77.11 ]=[]
[false 192.168.77.11 abc]=[]
[false 192.168.77.11 0]=[]
[false 192.168.77.11 65535]=[http://192.168.77.11:65535]
[false 192.168.77.11 65536]=[]
[false 192.168.77.11 08081]=[]
[false  8081]=[]
[false 192.168.77.11 80;x]=[]'
  [[ "$out" == "$want" ]] || fail "ship_web_url 的輸出不符:$out" || return 1
  out="$(bash -c "source '$LAUNCH/launch-common.sh'; ship_web_url false '' 8081; echo \"[\$(ship_web_url false '' 8081)]\"")"
  [[ "$out" == "[]" ]] || fail "沒有船上 IP 時不應印出位址:$out" || return 1
  out="$(bash -c "source '$LAUNCH/launch-common.sh'
    for p in 'instructor.html?ws=ws://192.168.77.11:8765' 'instructor.html' 'instructor.html?mode=simple&shipWeb=http://x:1' 'instructor.html?shipWeb=0&ws=ws://a:1' \
             'radar.html?radar=x&ws=ws://192.168.77.11:8766' 'report.html' 'xinstructor.html?a=1' 'instructor.htmlx' 'index.html?next=instructor.html' \
             'instructor.html?ws=ws://192.168.77.11:8765#frag' 'instructor.html#frag' 'instructor.html?a=1#x&shipWeb=1' 'instructor.html?shipWeb=0#f' 'radar.html#instructor.html' 'instructor.html?xshipWeb=1'; do
      echo \"\$(ship_web_page \"\$p\" http://192.168.77.11:8081)|\$(ship_web_page \"\$p\" '')\"; done" 2>&1)" || fail "ship_web_page 出錯:$out" || return 1
  want='instructor.html?ws=ws://192.168.77.11:8765&shipWeb=http://192.168.77.11:8081|instructor.html?ws=ws://192.168.77.11:8765
instructor.html?shipWeb=http://192.168.77.11:8081|instructor.html
instructor.html?mode=simple&shipWeb=http://x:1|instructor.html?mode=simple&shipWeb=http://x:1
instructor.html?shipWeb=0&ws=ws://a:1|instructor.html?shipWeb=0&ws=ws://a:1
radar.html?radar=x&ws=ws://192.168.77.11:8766|radar.html?radar=x&ws=ws://192.168.77.11:8766
report.html|report.html
xinstructor.html?a=1|xinstructor.html?a=1
instructor.htmlx|instructor.htmlx
index.html?next=instructor.html|index.html?next=instructor.html
instructor.html?ws=ws://192.168.77.11:8765&shipWeb=http://192.168.77.11:8081#frag|instructor.html?ws=ws://192.168.77.11:8765#frag
instructor.html?shipWeb=http://192.168.77.11:8081#frag|instructor.html#frag
instructor.html?a=1&shipWeb=http://192.168.77.11:8081#x&shipWeb=1|instructor.html?a=1#x&shipWeb=1
instructor.html?shipWeb=0#f|instructor.html?shipWeb=0#f
radar.html#instructor.html|radar.html#instructor.html
instructor.html?xshipWeb=1&shipWeb=http://192.168.77.11:8081|instructor.html?xshipWeb=1'
  # 第 10–14 列(審查 S-4):頁面帶 # 片段時參數加在片段之前(加在後面會落在片段裡,網頁讀不到);片段裡的 shipWeb= 不算已經寫了。
  # 最後一列:名稱只是以 shipWeb 結尾的其他參數(xshipWeb=)不算已經寫了
  [[ "$out" == "$want" ]] || fail "ship_web_page 的輸出不符:$out" || return 1
  # 啟動腳本(直連模式;船上 IP 用 127.0.0.2,沒有船上筆電也照常啟動)
  local cfg; cfg="$(instructor_cfg instructor-shipweb0.json 'web.enabled=true' 'web.bind="127.0.0.1"')"
  run_launch "$TMP/shipweb0.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
  [[ $CODE == 0 ]] || fail "教官筆電腳本結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "^  http://127.0.0.1:$WEB/instructor.html?ws=ws://127.0.0.2:$WS\$" <<<"$OUT" || fail "沒設 ports.shipWeb 時教官站網址不變(沒有 shipWeb 參數);輸出:$OUT" || return 1
  grep -q "shipWeb=" <<<"$OUT" && { fail "沒設 ports.shipWeb 時不應出現 shipWeb=;輸出:$OUT"; return 1; }
  grep -q "船上筆電的版本:教官站連上船上的核心後,頁首向 http://127.0.0.2:8080/_status/release.json 取" <<<"$OUT" || fail "應提示船上版本的來源(預設埠 8080);輸出:$OUT" || return 1
  python3 -I -c 'import json,sys; assert json.load(open(sys.argv[1],encoding="utf-8"))["ports"]["shipWeb"] is None' "$ROOT/config/instructor-laptop.json" || fail "config/instructor-laptop.json 的 ports.shipWeb 預設應為 null" || return 1
  grep -q "shipWeb 是「船上筆電」的網頁埠" "$ROOT/config/instructor-laptop.json" || fail "config 的註解應說明 ports.shipWeb" || return 1
  cfg="$(instructor_cfg instructor-shipweb1.json 'web.enabled=true' 'web.bind="127.0.0.1"' 'ports.shipWeb=18081')"
  run_launch "$TMP/shipweb1.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
  [[ $CODE == 0 ]] || fail "教官筆電腳本(ports.shipWeb)結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "^  http://127.0.0.1:$WEB/instructor.html?ws=ws://127.0.0.2:$WS&shipWeb=http://127.0.0.2:18081\$" <<<"$OUT" || fail "設了 ports.shipWeb 時教官站網址應加上 shipWeb=http://<船上 IP>:<埠>;輸出:$OUT" || return 1
  [[ "$(grep -c "shipWeb=" <<<"$OUT")" == 1 ]] || fail "只有教官站頁面加 shipWeb(雷達、ECDIS、講評頁不加);輸出:$OUT" || return 1
  grep -q "頁首向 http://127.0.0.2:18081/_status/release.json 取" <<<"$OUT" || fail "提示應寫設定的埠;輸出:$OUT" || return 1
  cfg="$(instructor_cfg instructor-shipweb2.json 'web.enabled=true' 'web.bind="127.0.0.1"' 'ports.shipWeb="abc"')"
  run_launch "$TMP/shipweb2.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
  [[ $CODE == 0 ]] && grep -q "ports.shipWeb(abc)不是 1–65535 的埠,略過" <<<"$OUT" || fail "ports.shipWeb 不是埠時應警告、照常啟動(結束碼 $CODE);輸出:$OUT" || return 1
  grep -q "shipWeb=" <<<"$OUT" && { fail "ports.shipWeb 不是埠時不應加參數;輸出:$OUT"; return 1; }
  # 不是整數的 JSON 值(審查 S-5;與 start-instructor-laptop.ps1 的 Get-LaunchShipWebPort 相同):8081.0、陣列、物件、true 都不是埠;
  #   ship_web_port_cfg 印出設定值的文字(陣列或物件 →「陣列或物件」),ship_web_url 不給位址;沒有這個鍵、null、"" = 沒設
  local v
  for v in '8081|[8081] [http://127.0.0.2:8081]' '"8081"|[8081] [http://127.0.0.2:8081]' '8081.0|[8081.0] []' '[8081]|[陣列或物件] []' '[]|[陣列或物件] []' '{"port":8081}|[陣列或物件] []' \
           'true|[true] []' '0|[0] []' '65536|[65536] []' '-1|[-1] []' '" 8081"|[ 8081] []' 'null|[] []' '""|[] []'; do
    cfg="$(instructor_cfg instructor-shipweb3.json "ports.shipWeb=${v%%|*}")"
    out="$(bash -c "source '$LAUNCH/launch-common.sh'; load_config '$cfg'; p=\"\$(ship_web_port_cfg)\"; echo \"[\$p] [\$(ship_web_url false 127.0.0.2 \"\$p\")]\"" 2>&1)"
    [[ "$out" == "${v#*|}" ]] || fail "ports.shipWeb = ${v%%|*}:ship_web_port_cfg / ship_web_url 應為「${v#*|}」,實際「$out」" || return 1
  done
  python3 -I -c 'import json,sys; c=json.load(open(sys.argv[1],encoding="utf-8")); del c["ports"]["shipWeb"]; json.dump(c,open(sys.argv[1],"w",encoding="utf-8"))' "$cfg"
  out="$(bash -c "source '$LAUNCH/launch-common.sh'; load_config '$cfg'; echo \"[\$(ship_web_port_cfg)]\"" 2>&1)"
  [[ "$out" == "[]" ]] || fail "沒有 ports.shipWeb 這個鍵(舊的設定檔)= 沒設,實際「$out」" || return 1
  cfg="$(instructor_cfg instructor-shipweb3.json 'web.enabled=true' 'web.bind="127.0.0.1"' 'ports.shipWeb=[8081]')"
  run_launch "$TMP/shipweb3.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
  [[ $CODE == 0 ]] && grep -q "ports.shipWeb(陣列或物件)不是 1–65535 的埠,略過" <<<"$OUT" || fail "ports.shipWeb = [8081] 應警告「(陣列或物件)不是 1–65535 的埠」並照常啟動(結束碼 $CODE);輸出:$OUT" || return 1
  grep -q "shipWeb=" <<<"$OUT" && { fail "ports.shipWeb = [8081] 不應加參數;輸出:$OUT"; return 1; }
  grep -q "頁首向 http://127.0.0.2:8080/_status/release.json 取" <<<"$OUT" || fail "ports.shipWeb = [8081] 時提示應寫預設的 8080;輸出:$OUT" || return 1
  # 頁面帶 # 片段(審查 S-4):啟動腳本印出的網址把 shipWeb 放在片段之前
  cfg="$(instructor_cfg instructor-shipweb4.json 'web.enabled=true' 'web.bind="127.0.0.1"' 'ports.shipWeb=18081' 'web.openPages=["instructor.html?ws=ws://{ship}:{stateWs}#frag","instructor.html#top","report.html#instructor.html"]')"
  run_launch "$TMP/shipweb4.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
  [[ $CODE == 0 ]] || fail "教官筆電腳本(openPages 帶片段)結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "^  http://127.0.0.1:$WEB/instructor.html?ws=ws://127.0.0.2:$WS&shipWeb=http://127.0.0.2:18081#frag\$" <<<"$OUT" && grep -q "^  http://127.0.0.1:$WEB/instructor.html?shipWeb=http://127.0.0.2:18081#top\$" <<<"$OUT" \
    && grep -q "^  http://127.0.0.1:$WEB/report.html#instructor.html\$" <<<"$OUT" || fail "頁面帶 # 片段時 shipWeb 應加在片段之前(其他頁面不加);輸出:$OUT" || return 1
  # standalone(核心在本機,沒有另一台電腦;審查 S-1:實際啟動,不只比對原始碼):即使設了 ports.shipWeb 也不加參數、不提示船上版本的來源
  cfg="$(instructor_cfg instructor-shipweb5.json 'web.enabled=true' 'web.bind="127.0.0.1"' 'ports.shipWeb=18081')"
  local via
  for via in flag config; do
    if [[ $via == flag ]]; then run_launch "$TMP/shipweb5.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --standalone
    else cfg="$(instructor_cfg instructor-shipweb6.json 'web.enabled=true' 'web.bind="127.0.0.1"' 'ports.shipWeb=18081' 'standalone.enabled=true')"; run_launch "$TMP/shipweb6.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg"; fi
    "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
    [[ $CODE == 0 ]] || fail "教官筆電腳本(standalone:$via)結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
    grep -q "^  http://127.0.0.1:$WEB/instructor.html?ws=ws://127.0.0.1:$WS\$" <<<"$OUT" || fail "standalone($via)的教官站網址應指向本機核心、沒有 shipWeb 參數;輸出:$OUT" || return 1
    grep -q "shipWeb=\|船上筆電的版本" <<<"$OUT" && { fail "standalone($via)不應加 shipWeb 參數、不應提示船上版本的來源;輸出:$OUT"; return 1; }
    grep -q "SimCore.Host 未開啟" <<<"$OUT" && { fail "standalone($via)的本機核心應已啟動;輸出:$OUT"; return 1; }
  done
  # --help 只印檔頭的註解(審查 S-3:原本多印一行程式碼 source …)
  out="$(bash "$LAUNCH/start-instructor-laptop.sh" --help)"
  grep -q "^#   --check-wifi:" <<<"$(tail -n 1 <<<"$out")" && ! grep -qv '^#' <<<"$out" || fail "--help 應只印檔頭的註解(最後一行是 --check-wifi 的說明):$(tail -n 2 <<<"$out")" || return 1
  grep -q "ports.shipWeb" <<<"$out" || fail "--help 應說明 ports.shipWeb" || return 1
  # Windows 版的同一套規則(實際啟動在 launch-common.tests.ps1)
  grep -q 'SHIP_WEB_URL="$(ship_web_url "$STANDALONE" "$SHIP_IP" "$SHIP_WEB_PORT")"' "$LAUNCH/start-instructor-laptop.sh" || fail "start-instructor-laptop.sh 應以 ship_web_url 決定船上網頁位址(standalone 不取)" || return 1
  grep -q 'Get-LaunchShipWebUrl (\[bool\]$standalone) $shipIp $shipWebPort' "$LAUNCH/start-instructor-laptop.ps1" && grep -q 'Add-LaunchShipWebParam (\[string\]$_) $shipWebUrl' "$LAUNCH/start-instructor-laptop.ps1" \
    || fail "start-instructor-laptop.ps1 應以 Get-LaunchShipWebUrl / Add-LaunchShipWebParam 處理教官站網址" || return 1
  return 0
}

# write_release_status(launch-common.sh):開發模式(沒有 manifest.json)不產生、不警告、不建置;安裝版以 simosa-pkg status --json 寫出狀態檔並記 launch.log;
# 逾時、結束碼非 0、輸出不是 JSON 都只警告、回傳 0(不擋住啟動),而且刪掉上一次的狀態檔(不顯示過期的版本)
test_release_status_write() {
  [[ ! -f "$ROOT/manifest.json" ]] || fail "測試前置:儲存庫根目錄不應有 manifest.json(開發模式)" || return 1
  local pkg; pkg="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll simosa-pkg Tools.Packaging.Cli simosa-pkg.dll '$ROOT'")" || fail "找不到/無法建置 simosa-pkg" || return 1
  local d="$TMP/relstatus"; rm -rf "$d"; mkdir -p "$d/root/data" "$d/root/app/web" "$d/fakebin"
  echo '<html></html>' > "$d/root/app/web/index.html"
  local out; out="$(bash -c "source '$LAUNCH/launch-common.sh'; release_status_path '$d/root'")"
  [[ "$out" == "$d/root/build/run/release-status.json" ]] || fail "狀態檔應為 <根目錄>/build/run/release-status.json:$out" || return 1
  # 開發模式:儲存庫與沒有 manifest.json 的資料夾都沒有工具;write 不輸出任何東西、不產生檔
  bash -c "source '$LAUNCH/launch-common.sh'; release_status_tool '$ROOT'" >/dev/null && { fail "儲存庫(開發模式)不應有發行版狀態工具"; return 1; }
  run_launch "$d/dev.out" bash -c "source '$LAUNCH/launch-common.sh'; write_release_status '$d/root' '$d/root/build/logs'"
  [[ $CODE == 0 && -z "$OUT" && ! -e "$out" ]] || fail "開發模式應不輸出、不產生檔(結束碼 $CODE;輸出:$OUT)" || return 1
  run_launch "$d/checkonly.out" bash "$LAUNCH/start-ship-laptop.sh" --check-only
  grep -q "^發行版狀態:略過(開發模式" <<<"$OUT" || fail "--check-only 在開發模式應印「發行版狀態:略過」;輸出:$OUT" || return 1
  # 安裝版(有 manifest.json)但找不到 simosa-pkg:警告、不產生檔
  dotnet "$pkg" release manifest --dir "$d/root" --version 2026.11.30 --runtime linux-x64 > "$d/manifest.out" 2>&1 || fail "release manifest 失敗:$(cat "$d/manifest.out")" || return 1
  run_launch "$d/notool.out" bash -c "source '$LAUNCH/launch-common.sh'; write_release_status '$d/root' '$d/root/build/logs'"
  [[ $CODE == 0 && ! -e "$out" ]] && grep -q "找不到 tools/updater/simosa-pkg.dll" <<<"$OUT" || fail "有 manifest.json 但沒有 simosa-pkg 時應警告(結束碼 $CODE):$OUT" || return 1
  # 成功:狀態檔是 status --json 的輸出;launch.log 記一行
  run_launch "$d/ok.out" bash -c "source '$LAUNCH/launch-common.sh'; write_release_status '$d/root' '$d/root/build/logs' 20 '$pkg'"
  [[ $CODE == 0 ]] && grep -q "發行版狀態:發行版 2026.11.30 → $out" <<<"$OUT" || fail "應回報發行版 2026.11.30(結束碼 $CODE):$OUT" || return 1
  python3 -I -c 'import json,sys; j=json.load(open(sys.argv[1],encoding="utf-8")); assert j["type"]=="statusResult" and j["releaseVersion"]=="2026.11.30" and "releaseDowngrade" not in j, j' "$out" || fail "狀態檔內容不符:$(head -c 300 "$out")" || return 1
  grep -q "release-status 發行版 2026.11.30" "$d/root/build/logs/launch.log" || fail "launch.log 應記一行:$(cat "$d/root/build/logs/launch.log")" || return 1
  [[ ! -e "$out.tmp" ]] || fail "不應留下暫存檔 $out.tmp" || return 1
  # 結束碼非 0(simosa-pkg.dll 不存在):警告、回傳 0、上一次的狀態檔被刪掉
  run_launch "$d/bad.out" bash -c "source '$LAUNCH/launch-common.sh'; write_release_status '$d/root' '$d/root/build/logs' 20 '$d/nope.dll'"
  [[ $CODE == 0 && ! -e "$out" ]] && grep -q "simosa-pkg status 結束碼" <<<"$OUT" || fail "失敗時應警告、回傳 0 並刪掉舊的狀態檔(結束碼 $CODE):$OUT" || return 1
  # 逾時(假的 dotnet 不結束):1 秒後放棄,不擋住啟動
  printf '#!/bin/sh\nexec sleep 30\n' > "$d/fakebin/dotnet"; chmod +x "$d/fakebin/dotnet"
  cp "$d/ok.out" "$out"   # 假的舊狀態檔
  local t0=$SECONDS
  run_launch "$d/timeout.out" env PATH="$d/fakebin:$PATH" bash -c "source '$LAUNCH/launch-common.sh'; write_release_status '$d/root' '$d/root/build/logs' 1 '$pkg'"
  [[ $CODE == 0 && ! -e "$out" && ! -e "$out.tmp" ]] && grep -q "逾時(1 秒)" <<<"$OUT" || fail "逾時應警告、回傳 0 並刪掉舊的狀態檔(結束碼 $CODE):$OUT" || return 1
  [[ $((SECONDS - t0)) -lt 10 ]] || fail "逾時 1 秒應很快返回(耗時 $((SECONDS - t0)) s)" || return 1
  grep -q "release-status 逾時" "$d/root/build/logs/launch.log" || fail "launch.log 應記下逾時" || return 1
  # 輸出不是 JSON 物件:警告、不留檔
  printf '#!/bin/sh\necho "not json"\n' > "$d/fakebin/dotnet"
  run_launch "$d/notjson.out" env PATH="$d/fakebin:$PATH" bash -c "source '$LAUNCH/launch-common.sh'; write_release_status '$d/root' '$d/root/build/logs' 5 '$pkg'"
  [[ $CODE == 0 && ! -e "$out" && ! -e "$out.tmp" ]] && grep -q "輸出不是 JSON 物件" <<<"$OUT" || fail "輸出不是 JSON 時應警告、不留檔(結束碼 $CODE):$OUT" || return 1
  return 0
}

# 啟動腳本帶 --status-file(開發模式:沒有狀態檔,/_status/release.json 回 200 與各欄 null、啟動不受影響);
# 「安裝版」資料夾(app/launch、app/web、app/web-server、tools/updater、manifest.json)以真實的 simosa-pkg 做出降版
# (2026.12.1 套用軟體更新包 2026.12.2 後以 2026.11.1 重新安裝)→ 從該資料夾啟動教官筆電腳本 → 狀態檔 → simosa-web → 網頁拿到降版資訊(只有白名單欄位)
test_release_status_launch() {
  # 不論哪一步失敗,都要結束從「安裝版」資料夾啟動的程序(它的 PID 檔不在儲存庫的 log 資料夾,cleanup_helpers 收不到;留著會占住網頁埠)
  local rc=0
  release_status_launch_body || rc=$?
  [[ -f "$TMP/installed/root/app/launch/stop-all.sh" ]] && bash "$TMP/installed/root/app/launch/stop-all.sh" >/dev/null 2>&1
  return $rc
}
release_status_launch_body() {
  local h="$TMP/rs-h.txt" b="$TMP/rs-b.txt" code
  # (1) 開發模式:儲存庫的啟動腳本
  local cfg; cfg="$(instructor_cfg instructor-status.json 'web.enabled=true' 'web.bind="127.0.0.1"')"
  rm -f "$ROOT/build/run/release-status.json"
  run_launch "$TMP/rs-dev.out" bash "$LAUNCH/start-instructor-laptop.sh" --config "$cfg" --ship 127.0.0.2
  [[ $CODE == 0 ]] || fail "教官筆電腳本結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "發行版狀態" <<<"$OUT" && { fail "開發模式不應有發行版狀態的訊息或警告;輸出:$OUT"; return 1; }
  local i; for ((i = 0; i < 40; i++)); do tcp_open "$WEB" && break; sleep 0.25; done
  # web.log 是追加寫入:只看這一次(最後一行)的啟動列
  grep '^# ' "$ROOT/$LOGS/web.log" | tail -n 1 | grep -q -- "--status-file $ROOT/build/run/release-status.json" || fail "web.log 的啟動列應帶 --status-file:$(grep '^# ' "$ROOT/$LOGS/web.log" | tail -n 1)" || return 1
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  [[ "$code" == 200 && "$(cat "$b")" == '{"format":"simosa-release-status/1","generatedUtc":null,"releaseVersion":null,"softwareVersion":null,"releaseDowngrade":null}' ]] \
    || fail "開發模式 /_status/release.json 應為 200 與各欄 null(沒有發行版狀態;不是 404:教官站每次載入都會取,404 會在瀏覽器留下 console error),實際 $code:$(cat "$b")" || return 1
  [[ "$(http_get "http://127.0.0.1:$WEB/instructor.html" "$h" "$b")" == 200 ]] || fail "教官站頁面應照常提供" || return 1
  [[ ! -e "$ROOT/build/run/release-status.json" ]] || fail "開發模式不應產生 build/run/release-status.json" || return 1
  "$LAUNCH/stop-all.sh" --logs "$LOGS" >/dev/null 2>&1
  grep -q -- "--status-file \"\$(release_status_path \"\$ROOT\")\"" "$LAUNCH/start-ship-laptop.sh" && grep -q 'write_release_status "$ROOT" "$ROOT/$LOGS"' "$LAUNCH/start-ship-laptop.sh" || fail "start-ship-laptop.sh 應在啟動網頁伺服前取狀態並帶 --status-file" || return 1

  # (2) 安裝版資料夾:發行版先在乾淨的資料夾組好並產生清單(與真的發行版一樣不含 build/、state/),再「安裝」到根目錄
  local pkg web
  pkg="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll simosa-pkg Tools.Packaging.Cli simosa-pkg.dll '$ROOT'")" || fail "找不到/無法建置 simosa-pkg" || return 1
  web="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll web-server StaticWeb simosa-web.dll '$ROOT'")" || fail "找不到/無法建置 simosa-web" || return 1
  local d="$TMP/installed" r="$TMP/installed/root" q="$TMP/installed/pkg.out"
  rm -rf "$d"; mkdir -p "$r" "$d/keys" "$d/src/app/web"
  make_release() {  # make_release <版本>:$d/rel-<版本>/(app/launch、app/web、app/web-server、tools/updater、data、config、manifest.json)
    local rel="$d/rel-$1"
    mkdir -p "$rel/data" "$rel/config" "$rel/app/web" "$rel/app/launch" "$rel/tools"
    cp "$LAUNCH/launch-common.sh" "$LAUNCH/start-instructor-laptop.sh" "$LAUNCH/start-ship-laptop.sh" "$LAUNCH/stop-all.sh" "$rel/app/launch/"
    cp -r "$(dirname "$web")" "$rel/app/web-server"; cp -r "$(dirname "$pkg")" "$rel/tools/updater"
    find "$rel" -name '*.pdb' -delete   # 發行版不含偵錯符號(release manifest 會拒絕;tools/build-release.* 也在產生清單前刪除)
    echo "<html>release $1</html>" > "$rel/app/web/index.html"
    echo "data $1" > "$rel/data/readme.txt"
    make_cfg instructor-laptop.json "$rel/config/instructor-laptop.json" "ports.stateWs=$WS" "ports.radarWs=$RADAR" "ports.web=$WEB" "ports.sync=$SYNC" 'logs="build/logs"' 'web.enabled=true' 'web.bind="127.0.0.1"'
    dotnet "$pkg" release manifest --dir "$rel" --version "$1" --runtime linux-x64
  }
  install_release() {  # 與安裝程式相同:先清空程式目錄 app/ 與 tools/updater/,再放入發行版的檔案(config/、data/、state/、build/ 不清)
    rm -rf "$r/app" "$r/tools/updater"
    cp -r "$d/rel-$1/." "$r/"
  }
  local P=(dotnet "$r/tools/updater/simosa-pkg.dll")
  {
    make_release 2026.12.1 && make_release 2026.11.1 && install_release 2026.12.1 &&
    "${P[@]}" keygen --out "$d/keys/dev" --name "launch-test" --role primary --password test-pw &&
    "${P[@]}" trust add --root "$r" --file "$d/keys/dev.pub.pem" --admin test &&
    "${P[@]}" device init --root "$r" --ship FSB1 --device-id FSB1-LAUNCHTEST --pem &&
    cp -r "$r/app/launch" "$r/app/web-server" "$d/src/app/" && echo '<html>update 2026.12.2</html>' > "$d/src/app/web/index.html" &&
    "${P[@]}" pack --kind software --version 2026.12.2 --summary "launch test" --out "$d/sw.sbupd" --key "$d/keys/dev.key.pem" --key-password test-pw --dir "app/=$d/src/app" &&
    "${P[@]}" apply "$d/sw.sbupd" --root "$r" --operator test
  } > "$q" 2>&1 || fail "安裝版前置(發行版、金鑰、更新包、套用)失敗:$(tail -8 "$q")" || return 1
  grep -q 'update 2026.12.2' "$r/app/web/index.html" || fail "套用軟體更新包後 app/web 應為更新包的內容" || return 1
  # 套用更新包後(沒有重新安裝):沒有降版
  run_launch "$d/noreinstall.out" bash -c "source '$r/app/launch/launch-common.sh'; write_release_status '$r' '$r/build/logs'"
  grep -q "發行版狀態:發行版 2026.12.1 → $r/build/run/release-status.json" <<<"$OUT" || fail "套用更新包後應為發行版 2026.12.1、沒有降版提醒:$OUT" || return 1
  grep -q "降版" <<<"$OUT" && { fail "沒有重新安裝時不應有降版提醒:$OUT"; return 1; }
  # 這時實際執行的是軟體更新包 2026.12.2 的程式:網頁拿到發行版 2026.12.1 與軟體更新包版次 2026.12.2(教官站頁首兩個都顯示)、沒有降版
  start_web "$d/web-applied.log" --root "$r/app/web" --status-file "$r/build/run/release-status.json" || fail "simosa-web 未啟動:$(cat "$d/web-applied.log")" || return 1
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  kill -INT "$WEB_PID"; wait "$WEB_PID" 2>/dev/null
  [[ "$code" == 200 ]] && grep -q '"releaseVersion":"2026.12.1","softwareVersion":"2026.12.2","releaseDowngrade":null' "$b" || fail "套用軟體更新包後應為發行版 2026.12.1、軟體更新包 2026.12.2、沒有降版,實際 $code:$(cat "$b")" || return 1
  # 以較舊的安裝程式(2026.11.1)重新安裝到同一位置:降版
  install_release 2026.11.1
  grep -q 'release 2026.11.1' "$r/app/web/index.html" || fail "重新安裝後 app/web 應為 2026.11.1 的內容" || return 1
  run_launch "$d/checkonly.out" bash -c "source '$r/app/launch/launch-common.sh'; release_status_tool '$r'"
  [[ $CODE == 0 && "$OUT" == "$r/tools/updater/simosa-pkg.dll" ]] || fail "安裝版應找到 tools/updater/simosa-pkg.dll:$OUT" || return 1
  run_launch "$d/launch.out" bash "$r/app/launch/start-instructor-laptop.sh" --ship 127.0.0.2
  [[ $CODE == 0 ]] || fail "安裝版的教官筆電腳本結束碼應為 0,實際 $CODE;輸出:$OUT" || return 1
  grep -q "安裝根目錄 $r" <<<"$OUT" || fail "應以安裝版資料夾為根目錄:$OUT" || return 1
  grep -q "發行版狀態:發行版 2026.11.1(降版提醒:曾安裝或套用過 2026.12.2;教官站頁首會顯示) → $r/build/run/release-status.json" <<<"$OUT" || fail "啟動輸出應回報發行版 2026.11.1 與降版提醒:$OUT" || return 1
  for ((i = 0; i < 40; i++)); do tcp_open "$WEB" && break; sleep 0.25; done
  code="$(http_get "http://127.0.0.1:$WEB/_status/release.json" "$h" "$b")"
  [[ "$code" == 200 ]] || fail "安裝版 /_status/release.json 應為 200,實際 $code;web.log:$(tail -3 "$r/build/logs/web.log")" || { bash "$r/app/launch/stop-all.sh" >/dev/null 2>&1; return 1; }
  bash "$r/app/launch/stop-all.sh" > "$d/stop.out" 2>&1
  grep -q "FSB1-LAUNCHTEST\|launch-test\|trustedKeys\|deviceId\|warnings\|environment" "$b" && { fail "網頁拿到的狀態不得含裝置、金鑰、環境或警告:$(cat "$b")"; return 1; }
  grep -q "FSB1-LAUNCHTEST" "$r/build/run/release-status.json" || fail "狀態檔本身應是 status 的完整輸出(含裝置 ID)" || return 1
  python3 -I - "$b" <<'PY' || fail "網頁拿到的降版資訊不符:$(cat "$b")" || return 1
import json, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
assert sorted(j) == ["format", "generatedUtc", "releaseDowngrade", "releaseVersion", "softwareVersion"], sorted(j)
d = j["releaseDowngrade"]
assert j["releaseVersion"] == "2026.11.1" and d["releaseVersion"] == "2026.11.1" and d["basisVersion"] == "2026.12.2", j
assert j["softwareVersion"] == "2026.12.2", j   # status 仍顯示的軟體版次(降版後不是實際執行的程式:教官站這時不把它當成「軟體更新」顯示)
assert d["previousReleaseVersion"] == "2026.12.1" and d["softwareVersion"] == "2026.12.2" and d["softwareStale"] is True, d
assert "simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式" in d["message"] and "版本不低於 2026.12.2 的安裝程式" in d["message"], d["message"]
PY
  # 安裝模式核對:執行期的 build/run/release-status.json、build/logs 不算「多出的檔案」(降版是提醒,不是錯誤 → 結束碼 0)
  "${P[@]}" verify-install --root "$r" > "$d/verify.out" 2>&1; code=$?
  [[ $code == 0 ]] || fail "verify-install 應通過(狀態檔在執行期資料位置 build/),結束碼 $code:$(tail -12 "$d/verify.out")" || return 1
  grep -q "release-status.json" "$d/verify.out" && { fail "verify-install 不應把 build/run/release-status.json 列為問題:$(grep release-status "$d/verify.out")"; return 1; }
  [[ ! -f "$r/build/logs/simosa.pids" ]] || fail "stop-all 後 PID 檔應被移除" || return 1
  return 0
}

# ---- 同步與隨身碟匯入時的降版提醒(使用者 2026-11-25 的選擇;docs/design/packaging-sync.md 第 5.1 節末「同步與隨身碟匯入時的降版提醒」)----
# 全部是真實的程序:「安裝版」船上資料夾(tools/updater 的 simosa-pkg、manifest.json)以真實的 simosa-pkg 做出降版
# (2026.12.1 套用軟體更新包 2026.12.2 後以 2026.11.1 重新安裝)→ 船上真的起 simosa-pkg serve → 教官端真的跑 simosa-pkg sync:
# 輸出有哪一艘船與船上 status 相同的降版說明、結束碼 0,fleet-versions.json 有 releaseVersion / releaseDowngrade,store list 顯示保存的提醒;
# 隨身碟途徑同樣真的 export(船上)再 import(教官端);船上以夠新的安裝程式(2026.12.3)重新安裝後再同步 / 再走一次隨身碟,提醒消失。
test_sync_release_reminder() {
  # 不論哪一步失敗,都要結束船上的同步服務(留著會占住同步埠)
  local rc=0
  SERVE_PID=""
  sync_release_reminder_body || rc=$?
  [[ -n "$SERVE_PID" ]] && kill -KILL "$SERVE_PID" 2>/dev/null
  return $rc
}
sync_release_reminder_body() {
  local pkg; pkg="$(bash -c "source '$LAUNCH/launch-common.sh'; find_dll simosa-pkg Tools.Packaging.Cli simosa-pkg.dll '$ROOT'")" || fail "找不到/無法建置 simosa-pkg" || return 1
  local d="$TMP/syncrel" r="$TMP/syncrel/ship" q="$TMP/syncrel/prep.out"
  rm -rf "$d"; mkdir -p "$r" "$d/keys" "$d/src/app" "$d/instructor"
  local key="$d/keys/dev.key.pem" store="$d/instructor/store" store_usb="$d/instructor/store-usb" usb="$d/usb"
  local S=(dotnet "$r/tools/updater/simosa-pkg.dll")   # 船上:安裝版自己的 simosa-pkg
  local I=(dotnet "$pkg")                               # 教官端:儲存庫建置的 simosa-pkg(紀錄庫與根目錄都在測試資料夾)
  sr_release() {  # sr_release <版本>:$d/rel-<版本>/(app/、tools/updater、data/、manifest.json;與真的發行版一樣不含 state/、build/)
    local rel="$d/rel-$1"
    mkdir -p "$rel/app" "$rel/data" "$rel/tools"
    echo "app $1" > "$rel/app/version.txt"; echo "data $1" > "$rel/data/readme.txt"
    cp -r "$(dirname "$pkg")" "$rel/tools/updater"
    find "$rel" -name '*.pdb' -delete
    dotnet "$pkg" release manifest --dir "$rel" --version "$1" --runtime linux-x64
  }
  sr_install() {  # 與安裝程式相同:先清空程式目錄 app/ 與 tools/updater/,再放入發行版的檔案(config/、data/、state/ 不清)
    rm -rf "$r/app" "$r/tools/updater"
    cp -r "$d/rel-$1/." "$r/"
  }
  sr_serve() {  # 船上的同步服務(真實的 simosa-pkg serve;PID 放 SERVE_PID),等同步埠開啟
    : > "$d/serve.log"
    ( exec "${S[@]}" serve --root "$r" --listen 127.0.0.1 --port "$SYNC" >> "$d/serve.log" 2>&1 ) &
    SERVE_PID=$!; HELPERS+=("$SERVE_PID")
    local i; for ((i = 0; i < 80; i++)); do tcp_open "$SYNC" && return 0; kill -0 "$SERVE_PID" 2>/dev/null || return 1; sleep 0.25; done
    return 1
  }
  sr_serve_stop() { [[ -n "$SERVE_PID" ]] && { kill -INT "$SERVE_PID" 2>/dev/null; wait "$SERVE_PID" 2>/dev/null; }; SERVE_PID=""; }
  sr_sync() {  # sr_sync <名稱>:教官端真的跑 simosa-pkg sync;stdout / stderr 存 $d/<名稱>.out / .err,結束碼放 CODE
    SIMOSA_KEY_PASSWORD=test-pw "${I[@]}" sync --ship 127.0.0.1 --port "$SYNC" --key "$key" --store "$store" --root "$d/instructor" --enroll-device > "$d/$1.out" 2> "$d/$1.err"; CODE=$?
  }
  sr_usb() {  # sr_usb <名稱>:船上真的 export 到隨身碟資料夾,教官端真的 import;教官端的輸出存 $d/<名稱>.out / .err,結束碼放 CODE
    "${S[@]}" export --to "$usb" --role ship --root "$r" > "$d/$1.export.out" 2>&1 || { CODE=$?; return 1; }
    "${I[@]}" import --from "$usb" --role instructor --store "$store_usb" --root "$d/instructor" --enroll-device > "$d/$1.out" 2> "$d/$1.err"; CODE=$?
  }
  sr_fleet() {  # sr_fleet <紀錄庫> <python 斷言(j = FSB1 的項目)> [參數…]
    local st="$1" expr="$2"; shift 2
    python3 -I -c 'import json,sys; j=json.load(open(sys.argv[1],encoding="utf-8"))["ships"]["FSB1"]; a=sys.argv[3:]; exec(sys.argv[2])' "$st/fleet-versions.json" "$expr" "$@"
  }
  {
    sr_release 2026.12.1 && sr_release 2026.11.1 && sr_release 2026.12.3 && sr_install 2026.12.1 &&
    "${S[@]}" keygen --out "$d/keys/dev" --name "sync-test" --role primary --password test-pw &&
    "${S[@]}" trust add --root "$r" --file "$d/keys/dev.pub.pem" --admin test &&
    "${S[@]}" device init --root "$r" --ship FSB1 --device-id FSB1-SYNCTEST --pem &&
    echo "app 2026.12.2" > "$d/src/app/version.txt" &&
    "${S[@]}" pack --kind software --version 2026.12.2 --summary "sync test" --out "$d/sw.sbupd" --key "$key" --key-password test-pw --dir "app/=$d/src/app" &&
    "${S[@]}" apply "$d/sw.sbupd" --root "$r" --operator test
  } > "$q" 2>&1 || fail "前置(發行版、金鑰、更新包、套用)失敗:$(tail -8 "$q")" || return 1

  # (1) 沒有降版(發行版 2026.12.1 上套用軟體更新包 2026.12.2):一行帶出發行版與軟體更新包版次,沒有警告
  sr_serve || fail "simosa-pkg serve 未啟動:$(tail -5 "$d/serve.log")" || return 1
  sr_sync normal
  [[ $CODE == 0 ]] && grep -qx "FSB1 船上筆電:發行版 2026.12.1,軟體更新包 2026.12.2" "$d/normal.out" && grep -qx "同步完成" "$d/normal.out" \
    || fail "沒有降版時 sync 應印「FSB1 船上筆電:發行版 2026.12.1,軟體更新包 2026.12.2」(結束碼 $CODE):$(cat "$d/normal.out" "$d/normal.err")" || return 1
  grep -q "降版\|沒有回報" "$d/normal.out" "$d/normal.err" && { fail "沒有降版時不應有降版或沒有回報的訊息:$(cat "$d/normal.out" "$d/normal.err")"; return 1; }
  sr_fleet "$store" 'assert j["releaseReported"] is True and j["releaseVersion"] == "2026.12.1" and "releaseDowngrade" not in j and j["via"] == "tcp", j' \
    || fail "沒有降版時 fleet-versions.json 應有 releaseReported / releaseVersion、沒有 releaseDowngrade:$(cat "$store/fleet-versions.json")" || return 1
  sr_serve_stop

  # (2) 以較舊的安裝程式(2026.11.1)重新安裝:降版。船上 status 的警告就是教官端要印的說明
  sr_install 2026.11.1
  "${S[@]}" status --root "$r" > "$d/ship-status.out" 2> "$d/ship-status.err" || fail "船上 status 失敗:$(cat "$d/ship-status.err")" || return 1
  local msg; msg="$(grep '^警告:降版:' "$d/ship-status.err" | head -n 1)"; msg="${msg#警告:}"
  [[ "$msg" == "降版:目前的發行版 2026.11.1 比先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 舊"* ]] || fail "船上 status 應有降版警告:$(cat "$d/ship-status.err")" || return 1
  sr_serve || fail "simosa-pkg serve(降版後)未啟動:$(tail -5 "$d/serve.log")" || return 1
  sr_sync down
  [[ $CODE == 0 ]] || fail "降版只是提醒,sync 的結束碼應為 0,實際 $CODE:$(cat "$d/down.err")" || return 1
  grep -qx "FSB1 船上筆電:發行版 2026.11.1(有降版提醒,見下方的警告)" "$d/down.out" && grep -qx "同步完成" "$d/down.out" || fail "sync 的輸出應有降版的摘要行與「同步完成」:$(cat "$d/down.out")" || return 1
  grep -qxF "警告:船上筆電 FSB1(裝置 FSB1-SYNCTEST)有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— ${msg}。船上處理之後再同步一次(或再匯入一次隨身碟),教官筆電保存的這則提醒才會消失" "$d/down.err" \
    || fail "sync 的警告應寫明哪一艘船,說明與船上 status 的警告一字不差:$(cat "$d/down.err")" || return 1
  grep -q "版本不低於 2026.12.2 的安裝程式重新安裝" "$d/down.err" || fail "警告應寫明怎麼處理:$(cat "$d/down.err")" || return 1
  sr_fleet "$store" 'd = j["releaseDowngrade"]
assert j["releaseReported"] is True and j["releaseVersion"] == "2026.11.1" and j["via"] == "tcp" and j["deviceId"] == "FSB1-SYNCTEST", j
assert (d["releaseVersion"], d["basisVersion"], d["previousReleaseVersion"], d["softwareVersion"], d["softwareStale"]) == ("2026.11.1", "2026.12.2", "2026.12.1", "2026.12.2", True), d
assert d["message"] == a[0], d["message"]' "$msg" || fail "fleet-versions.json 應保存降版資訊(與船上 status 相同的欄位與說明):$(cat "$store/fleet-versions.json")" || return 1
  # 與船上 status --json 的 releaseDowngrade 是同一份欄位與內容
  "${S[@]}" status --root "$r" --json > "$d/ship-status.json" 2>/dev/null
  python3 -I -c 'import json,sys; a=json.load(open(sys.argv[1],encoding="utf-8")); b=json.load(open(sys.argv[2],encoding="utf-8"))["ships"]["FSB1"]; assert a["releaseVersion"] == b["releaseVersion"] and a["releaseDowngrade"] == b["releaseDowngrade"], (a.get("releaseDowngrade"), b.get("releaseDowngrade"))' \
    "$d/ship-status.json" "$store/fleet-versions.json" || fail "fleet-versions.json 的 releaseVersion / releaseDowngrade 應與船上 status --json 相同" || return 1
  # store list:保存的提醒也顯示(沒有重新同步)
  "${I[@]}" store list --store "$store" --root "$d/instructor" > "$d/list.out" 2> "$d/list.err"; CODE=$?
  [[ $CODE == 0 ]] && grep -q "^版次 FSB1:.*;發行版 2026.11.1(有降版提醒))$" "$d/list.out" && grep -qF "有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— ${msg}。" "$d/list.err" && grep -q "直連同步時保存的提醒" "$d/list.err" \
    || fail "store list 應顯示保存的降版提醒(結束碼 $CODE):$(cat "$d/list.out" "$d/list.err")" || return 1

  # (3) 隨身碟途徑:船上真的匯出(status.json)、教官端真的匯入 — 同樣的提醒,句尾註明 status.json 沒有簽章
  sr_usb usb-down || fail "隨身碟匯出 / 匯入失敗(結束碼 $CODE):$(cat "$d/usb-down.export.out" "$d/usb-down.err" 2>/dev/null)" || return 1
  [[ $CODE == 0 && -f "$usb/simosa-transfer/from-ship/FSB1/status.json" ]] || fail "隨身碟匯入的結束碼應為 0(實際 $CODE),隨身碟上應有 status.json" || return 1
  grep -qx "FSB1 船上筆電:發行版 2026.11.1(有降版提醒,見下方的警告)" "$d/usb-down.out" || fail "隨身碟匯入的輸出應有降版的摘要行:$(cat "$d/usb-down.out")" || return 1
  grep -qxF "警告:船上筆電 FSB1(裝置 FSB1-SYNCTEST)有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— ${msg}。船上處理之後再同步一次(或再匯入一次隨身碟),教官筆電保存的這則提醒才會消失(這是隨身碟上 status.json 的內容;這個檔沒有簽章,以船上的 simosa-pkg status 為準)" "$d/usb-down.err" \
    || fail "隨身碟匯入的警告應與直連相同並註明 status.json 沒有簽章:$(cat "$d/usb-down.err")" || return 1
  sr_fleet "$store_usb" 'assert j["via"] == "usb" and j["releaseReported"] is True and j["releaseVersion"] == "2026.11.1" and j["releaseDowngrade"]["basisVersion"] == "2026.12.2" and j["releaseDowngrade"]["message"] == a[0], j' "$msg" \
    || fail "隨身碟途徑的 fleet-versions.json 應保存降版資訊:$(cat "$store_usb/fleet-versions.json")" || return 1
  sr_serve_stop

  # (4) 船上處理掉(以不低於比較基準 2026.12.2 的安裝程式 2026.12.3 重新安裝):下一次同步 / 隨身碟匯入提醒消失,保存的也不在了
  sr_install 2026.12.3
  sr_serve || fail "simosa-pkg serve(處理後)未啟動:$(tail -5 "$d/serve.log")" || return 1
  sr_sync fixed
  [[ $CODE == 0 ]] && grep -qx "FSB1 船上筆電:發行版 2026.12.3" "$d/fixed.out" || fail "處理後 sync 應印「FSB1 船上筆電:發行版 2026.12.3」(結束碼 $CODE):$(cat "$d/fixed.out" "$d/fixed.err")" || return 1
  grep -q "降版" "$d/fixed.out" "$d/fixed.err" && { fail "處理後不應再有降版提醒:$(cat "$d/fixed.out" "$d/fixed.err")"; return 1; }
  sr_fleet "$store" 'assert j["releaseVersion"] == "2026.12.3" and "releaseDowngrade" not in j, j' || fail "處理後 fleet-versions.json 不應再有 releaseDowngrade:$(cat "$store/fleet-versions.json")" || return 1
  "${I[@]}" store list --store "$store" --root "$d/instructor" > "$d/list2.out" 2> "$d/list2.err"
  grep -q ";發行版 2026.12.3)$" "$d/list2.out" || fail "處理後 store list 應為發行版 2026.12.3:$(cat "$d/list2.out")" || return 1
  grep -q "降版" "$d/list2.out" "$d/list2.err" && { fail "處理後 store list 不應再有降版提醒:$(cat "$d/list2.out" "$d/list2.err")"; return 1; }
  sr_usb usb-fixed || fail "處理後的隨身碟匯出 / 匯入失敗(結束碼 $CODE)" || return 1
  [[ $CODE == 0 ]] && grep -qx "FSB1 船上筆電:發行版 2026.12.3" "$d/usb-fixed.out" || fail "處理後隨身碟匯入應印「FSB1 船上筆電:發行版 2026.12.3」(結束碼 $CODE):$(cat "$d/usb-fixed.out" "$d/usb-fixed.err")" || return 1
  grep -q "降版" "$d/usb-fixed.out" "$d/usb-fixed.err" && { fail "處理後隨身碟匯入不應再有降版提醒:$(cat "$d/usb-fixed.out" "$d/usb-fixed.err")"; return 1; }
  sr_fleet "$store_usb" 'assert j["releaseVersion"] == "2026.12.3" and "releaseDowngrade" not in j, j' || fail "處理後隨身碟途徑的 fleet-versions.json 不應再有 releaseDowngrade" || return 1
  sr_serve_stop
  tcp_open "$SYNC" && { fail "測試結束後同步埠 $SYNC 不應仍有服務"; return 1; }
  return 0
}

TESTS=(test_port_busy_fails test_simcore_death_detected test_stop_all_keep_sync test_instructor_ship_ip test_web_sigint_exit0 test_logs_alt_cleanup test_instructor_check_wifi_flag test_stop_file_graceful test_console_loopback test_port_busy_vhf_media_fails test_nmea_options_passed test_traceability_numbers test_git_info test_web_status_file test_web_status_cors test_instructor_ship_web test_release_status_write test_release_status_launch test_sync_release_reminder)
if [[ $LIST == 1 ]]; then printf '%s\n' "${TESTS[@]}"; exit 0; fi
command -v dotnet >/dev/null || { echo "找不到 dotnet" >&2; exit 2; }
cleanup_helpers
for t in "${TESTS[@]}"; do
  [[ -z "$ONLY" || "$t" == "$ONLY" ]] || continue
  echo "== $t"
  if "$t"; then PASS=$((PASS + 1)); echo "    通過"; else FAIL=$((FAIL + 1)); FAILED+=("$t"); fi
  cleanup_helpers
done
echo "通過 $PASS,失敗 $FAIL${FAILED[*]:+(${FAILED[*]})}"
[[ $FAIL == 0 ]]
