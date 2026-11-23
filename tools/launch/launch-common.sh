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

# console_service_args <埠規格> <鮑率> <ws 埠>:USB 操控台服務(app/console;src/Contracts/console-serial.md)的參數。
#   規格為序列埠名稱(COM5、/dev/ttyACM0)→ --port <埠> --baud <鮑率>;loopback:<讀取檔>[,<寫入檔>] → --loopback <…>(測試用假裝置)。
#   log 由 start_bg 收 stdout(--log -),不另寫檔。印出以換行分隔的參數列(呼叫端 mapfile -t)
console_service_args() {
  local spec="$1" baud="${2:-115200}" ws="${3:-8765}"
  if [[ "$spec" == loopback:* ]]; then printf '%s\n' --loopback "${spec#loopback:}"
  else printf '%s\n' --port "$spec" --baud "$baud"; fi
  printf '%s\n' --ws "ws://127.0.0.1:$ws" --log -
}

# nmea_host_args:config ports.nmeaTargets[](可多個)/nmeaBind/nmeaFile → SimCore.Host 的 --nmea-target <ip:port>(可重複)、--nmea-bind <ip>、--nmea-file <路徑>;
#   null/空字串 = 不加該參數(nmeaFile 相對路徑由 Host 以安裝根目錄解析且必須在根目錄內)。印出以換行分隔的參數列(呼叫端 mapfile -t;無參數時不印)
nmea_host_args() {
  local n i v
  n="$(cfg ports_nmeaTargets__n 0)"
  for ((i = 0; i < n; i++)); do v="$(cfg "ports_nmeaTargets_$i")"; [[ -n "$v" ]] && printf '%s\n' --nmea-target "$v"; done
  v="$(cfg ports_nmeaBind)"; [[ -n "$v" ]] && printf '%s\n' --nmea-bind "$v"
  v="$(cfg ports_nmeaFile)"; [[ -n "$v" ]] && printf '%s\n' --nmea-file "$v"
  return 0
}

# ---- 發行版狀態(教官站頁首的「發行版 <版本>」與降版提醒;docs/design/packaging-sync.md 第 5.1 節;Windows 版為 launch-common.ps1 的同名函式)----
# release_status_path <根目錄>:狀態檔 <根目錄>/build/run/release-status.json(執行期資料,不是程式檔;simosa-web --status-file 讀它,
#   以 /_status/release.json 提供教官站需要的欄位)
release_status_path() { echo "$1/build/run/release-status.json"; }

# release_status_tool <根目錄>:安裝版(根目錄有 manifest.json)才印出 simosa-pkg.dll 的路徑(tools/updater;沒有時用已建置的開發輸出)並回傳 0,
#   否則回傳 1。不建置:開發模式(儲存庫,沒有 manifest.json)沒有發行版本可顯示。與啟動同步服務無關(--no-sync 時照樣取狀態)。
release_status_tool() {
  local root="$1" c
  [[ -f "$root/manifest.json" ]] || return 1
  for c in "$root/tools/updater/simosa-pkg.dll" "$root/build/bin/Tools.Packaging.Cli/Release/net8.0/simosa-pkg.dll" "$root/build/bin/Tools.Packaging.Cli/Debug/net8.0/simosa-pkg.dll"; do
    [[ -f "$c" ]] && { echo "$c"; return 0; }
  done
  return 1
}

# write_release_status <根目錄> <log 目錄> [逾時秒=20] [simosa-pkg.dll]:啟動網頁伺服之前執行一次 simosa-pkg status --json,輸出存成 release_status_path。
#   先刪掉上一次的狀態檔:這次沒有產生(開發模式、找不到 simosa-pkg、逾時、失敗、輸出不是 JSON)時教官站就不顯示,不會顯示過期的版本。
#   逾時與失敗都不擋住啟動:只警告並在 <log 目錄>/launch.log 記一行(stderr 在 release-status.err.log);本函式一律回傳 0。
#   存的是 status 的完整輸出(留在本機);simosa-web 只把發行版本與降版資訊送給網頁,不會整份送出。
write_release_status() {
  local root="$1" logdir="$2" secs="${3:-20}" dll="${4:-}" out tmp err rc=0 summary
  out="$(release_status_path "$root")"; tmp="$out.tmp"
  rm -f "$out" "$tmp" 2>/dev/null || true
  if [[ -z "$dll" ]]; then
    dll="$(release_status_tool "$root")" || {
      [[ -f "$root/manifest.json" ]] && echo "警告:發行版狀態:找不到 tools/updater/simosa-pkg.dll,教官站不顯示發行版本" >&2
      return 0
    }
  fi
  mkdir -p "$(dirname "$out")" "$logdir" 2>/dev/null || { echo "警告:發行版狀態:無法建立 $(dirname "$out"),略過" >&2; return 0; }
  err="$logdir/release-status.err.log"
  timeout "$secs" dotnet "$dll" status --root "$root" --json > "$tmp" 2> "$err" || rc=$?
  if [[ $rc != 0 ]]; then
    rm -f "$tmp"
    if [[ $rc == 124 ]]; then summary="逾時($secs 秒),略過"; else summary="simosa-pkg status 結束碼 $rc,略過(見 $err)"; fi
    echo "警告:發行版狀態:$summary(教官站不顯示發行版本)" >&2
    echo "# $(date -u +%FT%TZ) release-status $summary" >> "$logdir/launch.log" 2>/dev/null || true
    return 0
  fi
  # 確認輸出是 JSON 物件(不是的話不留檔);版本只用來在啟動視窗與 launch.log 記一行(python3 與 load_config 相同的相依)
  summary="$(python3 -I - "$tmp" <<'PY'
import json, sys
st = json.load(open(sys.argv[1], encoding="utf-8-sig"))
if not isinstance(st, dict): raise SystemExit("not an object")
ver = st.get("releaseVersion")
s = f"發行版 {ver}" if ver else "status 沒有發行版本(manifest.json 讀不到,或 simosa-pkg 是較舊的版本),教官站不顯示"
basis = (st.get("releaseDowngrade") or {}).get("basisVersion")
if basis: s += f"(降版提醒:曾安裝或套用過 {basis};教官站頁首會顯示)"
print(s)
PY
)" || {
    rm -f "$tmp"
    echo "警告:發行版狀態:simosa-pkg status 的輸出不是 JSON 物件,略過(教官站不顯示發行版本)" >&2
    echo "# $(date -u +%FT%TZ) release-status 輸出不是 JSON 物件,略過" >> "$logdir/launch.log" 2>/dev/null || true
    return 0
  }
  mv -f "$tmp" "$out" || { rm -f "$tmp"; return 0; }
  echo "  發行版狀態:$summary → $out"
  echo "# $(date -u +%FT%TZ) release-status $summary" >> "$logdir/launch.log" 2>/dev/null || true
  return 0
}

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

# launch_git_info <根目錄>:版本資訊(soak 報告的 meta.json;第三十輪審查 S-F4)→ LAUNCH_GIT_REV(HEAD 的提交,可能為空)、
# LAUNCH_GIT_DIRTY(true = 工作樹有未提交的修改(含未追蹤檔)、false、unknown)。有 git 時以 git 取(git worktree 的 .git 是檔案也可);
# 沒有 git(船上筆電)或 LAUNCH_GIT_NO_GIT=1(測試用)時讀 .git/HEAD 與 refs(.git 是檔案時跟到 gitdir、worktree 的 commondir、packed-refs),
# 有沒有未提交的修改無法得知(unknown)。不會失敗。
launch_git_info() {
  local root="$1" gd head ref common
  LAUNCH_GIT_REV=""; LAUNCH_GIT_DIRTY="unknown"
  if [[ "${LAUNCH_GIT_NO_GIT:-0}" != 1 ]] && command -v git >/dev/null 2>&1 && LAUNCH_GIT_REV="$(git -C "$root" rev-parse HEAD 2>/dev/null)"; then
    if [[ -n "$(git -C "$root" status --porcelain 2>/dev/null)" ]]; then LAUNCH_GIT_DIRTY=true; else LAUNCH_GIT_DIRTY=false; fi
    return 0
  fi
  LAUNCH_GIT_REV=""
  gd="$root/.git"
  if [[ -f "$gd" ]]; then gd="$(sed -n 's/^gitdir: //p' "$gd" | head -1)"; [[ "$gd" == /* ]] || gd="$root/$gd"; fi
  [[ -f "$gd/HEAD" ]] || return 0
  head="$(head -1 "$gd/HEAD")"
  if [[ "$head" != ref:* ]]; then LAUNCH_GIT_REV="$head"; return 0; fi
  ref="${head#ref: }"; common="$gd"
  if [[ -f "$gd/commondir" ]]; then common="$(head -1 "$gd/commondir")"; [[ "$common" == /* ]] || common="$gd/$common"; fi
  if [[ -f "$gd/$ref" ]]; then LAUNCH_GIT_REV="$(head -1 "$gd/$ref")"
  elif [[ -f "$common/$ref" ]]; then LAUNCH_GIT_REV="$(head -1 "$common/$ref")"
  elif [[ -f "$common/packed-refs" ]]; then LAUNCH_GIT_REV="$(awk -v r="$ref" '$2 == r { print $1; exit }' "$common/packed-refs")"; fi
  return 0
}
