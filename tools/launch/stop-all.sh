#!/usr/bin/env bash
# 結束 start-ship-laptop.sh / start-instructor-laptop.sh 啟動的全部程序(讀 build/logs/simosa.pids)。
#   SimCore.Host:先寫停止檔(路徑在 build/logs/simcore.stopfile;啟動腳本以 --stop-file 傳給核心),核心每秒檢查到即自行結束並寫完紀錄 footer;
#   其他程序送 SIGINT。最多等 --timeout 秒(預設 10 s),逾時仍在執行者 SIGKILL。沒有停止檔紀錄時核心也改送 SIGINT(同樣優雅)。
# 用法:stop-all.sh [--logs build/logs] [--keep-sync] [--timeout 10]
#   --keep-sync:保留直連同步服務(simosa-pkg serve,PID 檔中名稱為 sync 的程序)與其 PID 列,供教官筆電在模擬器關閉後仍能推送軟體更新包
#               (docs/ops/安裝與啟動.md 第 5.2 節);下次 start-ship-laptop.sh 會先結束它再重新啟動。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LOGS="build/logs"; KEEP_SYNC=0; TIMEOUT=10
while [[ $# -gt 0 ]]; do
  case "$1" in
    --logs) LOGS="$2"; shift 2 ;;
    --keep-sync) KEEP_SYNC=1; shift ;;
    --timeout) TIMEOUT="$2"; [[ "$TIMEOUT" =~ ^[0-9]+$ ]] || { echo "--timeout 須為整數秒(收到 $TIMEOUT)" >&2; exit 2; }; shift 2 ;;
    -h|--help) sed -n 2,7p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
PIDFILE="$ROOT/$LOGS/simosa.pids"
STOPFILE_REC="$ROOT/$LOGS/simcore.stopfile"
[[ -f "$PIDFILE" ]] || { echo "沒有 $PIDFILE(未啟動或已結束)"; rm -f "$STOPFILE_REC"; exit 0; }
STOPFILE=""; [[ -f "$STOPFILE_REC" ]] && STOPFILE="$(head -n 1 "$STOPFILE_REC")"
pids=(); kept=()
while IFS=: read -r name pid; do
  [[ -n "$pid" ]] || continue
  if [[ $KEEP_SYNC == 1 && "$name" == sync ]] && kill -0 "$pid" 2>/dev/null; then echo "保留 $name(PID $pid)"; kept+=("$name:$pid"); continue; fi
  if kill -0 "$pid" 2>/dev/null; then
    if [[ "$name" == simcore && -n "$STOPFILE" ]]; then
      # 核心:寫停止檔,讓它自行結束練習並寫完紀錄 footer(不送訊號;Windows 版 stop-all.ps1 同一機制)
      echo "結束 $name(PID $pid):寫入停止檔 $STOPFILE,等核心寫完紀錄 footer(最多 $TIMEOUT s)"
      : > "$STOPFILE" || { echo "無法寫入停止檔,改送 SIGINT" >&2; kill -INT "$pid" 2>/dev/null || true; }
    else
      echo "結束 $name(PID $pid)"; kill -INT "$pid" 2>/dev/null || true
    fi
    pids+=("$pid")
  fi
done < "$PIDFILE"
for ((i = 0; i < TIMEOUT * 4; i++)); do
  alive=0; for p in "${pids[@]:-}"; do [[ -n "$p" ]] && kill -0 "$p" 2>/dev/null && alive=1; done
  [[ $alive == 0 ]] && break; sleep 0.25
done
for p in "${pids[@]:-}"; do [[ -n "$p" ]] && kill -0 "$p" 2>/dev/null && { echo "強制結束 PID $p(逾時 $TIMEOUT s;紀錄可能缺 footer)"; kill -KILL "$p" 2>/dev/null || true; }; done
# 核心未消化的停止檔與路徑紀錄一併清掉(下次啟動時核心也會刪殘留檔)
[[ -n "$STOPFILE" ]] && rm -f "$STOPFILE"
rm -f "$STOPFILE_REC"
if [[ ${#kept[@]} -gt 0 ]]; then printf '%s\n' "${kept[@]}" > "$PIDFILE"; echo "已結束全部程序(保留 ${kept[*]%%:*};PID 檔 $PIDFILE)"
else rm -f "$PIDFILE"; echo "已結束全部程序"; fi
