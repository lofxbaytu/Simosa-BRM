#!/usr/bin/env bash
# 結束 start-ship-laptop.sh / start-instructor-laptop.sh 啟動的全部程序(讀 build/logs/simosa.pids;先 SIGINT 讓 SimCore 寫完紀錄 footer,5 s 後 SIGKILL)。
# 用法:stop-all.sh [--logs build/logs] [--keep-sync]
#   --keep-sync:保留直連同步服務(simosa-pkg serve,PID 檔中名稱為 sync 的程序)與其 PID 列,供教官筆電在模擬器關閉後仍能推送軟體更新包
#               (docs/ops/安裝與啟動.md 第 5.2 節);下次 start-ship-laptop.sh 會先結束它再重新啟動。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LOGS="build/logs"; KEEP_SYNC=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --logs) LOGS="$2"; shift 2 ;;
    --keep-sync) KEEP_SYNC=1; shift ;;
    -h|--help) sed -n 2,5p "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知參數:$1" >&2; exit 2 ;;
  esac
done
PIDFILE="$ROOT/$LOGS/simosa.pids"
[[ -f "$PIDFILE" ]] || { echo "沒有 $PIDFILE(未啟動或已結束)"; exit 0; }
pids=(); kept=()
while IFS=: read -r name pid; do
  [[ -n "$pid" ]] || continue
  if [[ $KEEP_SYNC == 1 && "$name" == sync ]] && kill -0 "$pid" 2>/dev/null; then echo "保留 $name(PID $pid)"; kept+=("$name:$pid"); continue; fi
  if kill -0 "$pid" 2>/dev/null; then echo "結束 $name(PID $pid)"; kill -INT "$pid" 2>/dev/null || true; pids+=("$pid"); fi
done < "$PIDFILE"
for ((i = 0; i < 20; i++)); do
  alive=0; for p in "${pids[@]:-}"; do [[ -n "$p" ]] && kill -0 "$p" 2>/dev/null && alive=1; done
  [[ $alive == 0 ]] && break; sleep 0.25
done
for p in "${pids[@]:-}"; do [[ -n "$p" ]] && kill -0 "$p" 2>/dev/null && { echo "強制結束 PID $p"; kill -KILL "$p" 2>/dev/null || true; }; done
if [[ ${#kept[@]} -gt 0 ]]; then printf '%s\n' "${kept[@]}" > "$PIDFILE"; echo "已結束全部程序(保留 ${kept[*]%%:*};PID 檔 $PIDFILE)"
else rm -f "$PIDFILE"; echo "已結束全部程序"; fi
