#!/usr/bin/env bash
# 啟動腳本測試(Linux/容器;規劃書第 5.4–5.5 節、第 14 章「船上部署」):以真實的 SimCore.Host / simosa-web 與假程序驗證
#   tools/launch/*.sh 的失敗回報、埠檢查、stop-all --keep-sync、--logs 傳遞、--ship <IP>、--check-wifi、停止檔優雅停止(footer)、
#   USB 操控台服務 --console loopback:…(假裝置:ACK/LED/DISP 回寫、指令轉送核心、stop-all 結束)、
#   NMEA 選項(config ports.nmeaTargets/nmeaBind/nmeaFile → --nmea-target/--nmea-bind/--nmea-file;UDP 單播目標真的收到、檔案寫出)、
#   VHF 8767/錄影 8768 埠預檢(第二十四輪),以及 docs/驗收/需求追溯表.md 的數字。
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
  grep -Eq "結束(原因:(SIGINT|Ctrl\+C))" "$log" || fail "stop-all 後 console.log 應有「結束(原因:SIGINT 或 Ctrl+C)」:$(tail -3 "$log")" || return 1
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

TESTS=(test_port_busy_fails test_simcore_death_detected test_stop_all_keep_sync test_instructor_ship_ip test_web_sigint_exit0 test_logs_alt_cleanup test_instructor_check_wifi_flag test_stop_file_graceful test_console_loopback test_port_busy_vhf_media_fails test_nmea_options_passed test_traceability_numbers)
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
