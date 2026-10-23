#!/usr/bin/env bash
# 啟動腳本共用函式(Linux/開發機;Windows 版為 launch-common.ps1):找安裝根目錄、讀 config/*.json、找各元件、背景啟動並記 PID。
# 安裝根目錄 = 本檔上兩層(儲存庫:tools/launch/../..;發行版:app/launch/../..),必須含 data/。
# 所有輸出留在根目錄內:build/logs/<元件>.log、build/logs/simosa.pids、build/logs/simcore.stop[file](規劃書第 11.2 節、CLAUDE.md)。
set -euo pipefail
# 容器/開發機:dotnet 不在 PATH 時先載入 /etc/profile.d/dotnet.sh(Windows 版不需要)
if ! command -v dotnet >/dev/null 2>&1 && [[ -f /etc/profile.d/dotnet.sh ]]; then . /etc/profile.d/dotnet.sh; fi

LAUNCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

launch_root() {
  local root; root="$(cd "$LAUNCH_DIR/../.." && pwd)"
  [[ -d "$root/data" ]] || { echo "安裝根目錄 $root 沒有 data/" >&2; exit 2; }
  echo "$root"
}

# 讀 JSON 設定為環境變數:CFG_<鍵路徑以 _ 相連>(陣列元素 CFG_a_b_0_x;陣列長度 CFG_a_b__n)。$comment 鍵略過。
load_config() {
  local file="$1"
  [[ -f "$file" ]] || { echo "找不到設定檔 $file" >&2; exit 2; }
  eval "$(python3 -I - "$file" <<'PY'
import json, sys, re, shlex
def walk(prefix, v, out):
    if isinstance(v, dict):
        for k, x in v.items():
            if k.startswith("$"): continue
            walk(prefix + "_" + re.sub(r"[^A-Za-z0-9_]", "_", k), x, out)
    elif isinstance(v, list):
        out.append(f"{prefix}__n={len(v)}")
        for i, x in enumerate(v): walk(f"{prefix}_{i}", x, out)
    else:
        s = "" if v is None else ("true" if v is True else "false" if v is False else str(v))
        out.append(f"{prefix}={shlex.quote(s)}")
out = []
walk("CFG", json.load(open(sys.argv[1], encoding="utf-8")), out)
print("\n".join(out))
PY
)"
}

# cfg <鍵路徑> [預設值]:鍵名中非 [A-Za-z0-9_] 的字元(例如 --ship 192.168.77.11 組出的 ships_192.168.77.11)換成 _,與 load_config 相同規則,
# 否則 ${!n} 會因 invalid variable name 讓 set -e 的腳本直接結束
cfg() { local n="CFG_${1//[^A-Za-z0-9_]/_}"; echo "${!n:-${2:-}}"; }

# 找元件的 .dll:發行版 app/<元件>/<dll>(simosa-pkg 另看 tools/updater/)→ 開發 build/bin/<專案>/Release|Debug/net8.0/<dll>(缺少且有原始碼時先建置 Release)
find_dll() {
  local comp="$1" proj="$2" dll="$3" root="$4"
  local c
  for c in "$root/app/$comp/$dll" "$root/tools/updater/$dll" "$root/build/bin/$proj/Release/net8.0/$dll" "$root/build/bin/$proj/Debug/net8.0/$dll"; do
    [[ -f "$c" ]] && { echo "$c"; return 0; }
  done
  local csproj
  for csproj in "$root/src/$proj/$proj.csproj" "$root/tools/launch/$proj/$proj.csproj"; do
    if [[ -f "$csproj" ]]; then
      echo "建置 $proj(Release)…" >&2
      dotnet build "$csproj" -c Release -nologo -v q >&2
      c="$root/build/bin/$proj/Release/net8.0/$dll"
      [[ -f "$c" ]] && { echo "$c"; return 0; }
    fi
  done
  return 1
}

PIDFILE=""
launch_init_logs() {
  local root="$1" logs="$2"
  mkdir -p "$root/$logs"
  PIDFILE="$root/$logs/simosa.pids"
  if [[ -s "$PIDFILE" ]]; then
    echo "注意:$PIDFILE 仍有上次的 PID,先執行 stop-all.sh" >&2
    "$LAUNCH_DIR/stop-all.sh" --logs "$logs" || true
  fi
  : > "$PIDFILE"
}

# port_in_use <埠> [主機…]:任一主機(預設 127.0.0.1)的埠已有程序監聽即為真(殘留的舊核心、其他程式)
port_in_use() {
  local port="$1" h; shift
  for h in "${@:-127.0.0.1}"; do
    timeout 1 bash -c "exec 3<>/dev/tcp/$h/$port" 2>/dev/null && return 0
  done
  return 1
}

# check_ports_free <名稱:埠>… :任一埠被占用即列出並回傳 1(啟動前檢查;否則 SimCore.Host 會在 WebSocketStateServer.Start() 崩潰而腳本仍以外部監聽者判定成功)
# 額外要檢查的主機以 PORT_CHECK_HOSTS(空白分隔)指定,例如直連 IP
check_ports_free() {
  local item name port bad=0 hosts=(127.0.0.1)
  [[ -n "${PORT_CHECK_HOSTS:-}" ]] && read -r -a extra <<<"$PORT_CHECK_HOSTS" && hosts+=("${extra[@]}")
  for item in "$@"; do
    name="${item%%:*}"; port="${item##*:}"
    if port_in_use "$port" "${hosts[@]}"; then
      echo "埠 $port 已被占用($name):先執行 stop-all.sh,或查占用程序(ss -ltnp | grep :$port;Windows:netstat -ano | findstr :$port)" >&2
      bad=1
    fi
  done
  return $bad
}

# start_bg <名稱> <log 路徑> <指令…>:背景啟動,stdout/stderr 寫 log,PID 記入 PIDFILE
start_bg() {
  local name="$1" log="$2"; shift 2
  echo "  啟動 $name → $log"
  echo "# $(date -u +%FT%TZ) $*" >> "$log"
  ( exec "$@" >> "$log" 2>&1 ) &
  LAST_PID=$!
  echo "$name:$LAST_PID" >> "$PIDFILE"
}
LAST_PID=""

# launch_stop_file <root> <logs>:SimCore.Host 的停止檔路徑(--stop-file;stop-all.sh 先寫此檔讓核心自行結束並寫完紀錄 footer,逾時才強制結束)。
# 路徑記在 <logs>/simcore.stopfile 供 stop-all.sh 讀取(PID 檔以「:」分隔,Windows 路徑含「:」,所以另存一檔);殘留的停止檔先刪
STOPFILE=""
launch_stop_file() {
  local root="$1" logs="$2"
  mkdir -p "$root/$logs"
  STOPFILE="$root/$logs/simcore.stop"
  rm -f "$STOPFILE"
  echo "$STOPFILE" > "$root/$logs/simcore.stopfile"
  echo "$STOPFILE"
}

# process_alive <PID>:程序仍在執行
process_alive() { kill -0 "$1" 2>/dev/null; }

# wait_tcp <host> <port> <秒> [PID]:等待埠開啟;給 PID 時程序已結束即回傳 2(不再等到逾時,也不會把別的程序的監聽當成自己的)
wait_tcp() {
  local host="$1" port="$2" secs="${3:-20}" pid="${4:-}" i
  for ((i = 0; i < secs * 4; i++)); do
    [[ -n "$pid" ]] && ! process_alive "$pid" && return 2
    # 以 timeout 包住 /dev/tcp 連線:對不可達的 IP(例如船上筆電未接線)connect 會等到 TCP 逾時,不能讓啟動腳本卡住
    if timeout 1 bash -c "exec 3<>/dev/tcp/$host/$port" 2>/dev/null; then return 0; fi
    sleep 0.25
  done
  return 1
}

# ws_check <host> <port> <路徑>:以 curl 做 WebSocket 握手,101 即成功
ws_check() {
  local code
  code="$(curl -s -o /dev/null -m 5 -w '%{http_code}' -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
          -H 'Sec-WebSocket-Key: c2ltb3NhLWJybS1zbW9rZQ==' "http://$1:$2$3" 2>/dev/null || true)"
  [[ "$code" == "101" ]]
}
