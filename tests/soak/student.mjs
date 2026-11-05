#!/usr/bin/env node
// 「學員」腳本(長時間穩定性 soak 測試;tools/soak-test.sh / .ps1 啟動;docs/design/testing.md 第 6 節):一條核心狀態連線,
// 每 --order-interval s(預設 10)輪流下舵令與車令、每 --fault-interval s(預設 60)輪流注入/清除一個故障、每 --snapshot-interval s(預設 300)
// 送 snapshot;每個指令以後續狀態幀確認(rudderOrder / telegraph / faults[])是否在 --confirm-timeout s 內生效,並計數 commandRejected 事件。
// 指令皆為 src/Contracts/command.schema.json 既有指令(不加後門、不改核心);舵令與車令取值溫和(±10° 以內、微速/慢速/停俥)避免 2 小時內離開練習區太遠。
// 輸出 JSON Lines(--out):每個動作一筆 {kind:"action", …, confirmed, confirmMs} 與結束時 studentSummary。
// 用法:node tests/soak/student.mjs --ws ws://127.0.0.1:8765/ --minutes 10 --out build/soak/<run>/stats/student.jsonl
import { isStateFrame, makeSink, nowMs, parseArgs, round } from './lib.mjs';

let opt;
try {
  opt = parseArgs(process.argv.slice(2), {
    ws: { type: 'string', default: 'ws://127.0.0.1:8765/' },
    minutes: { type: 'number', default: 1 },
    out: { type: 'string', default: '' },
    'order-interval': { type: 'number', default: 10 },
    'fault-interval': { type: 'number', default: 60 },
    'snapshot-interval': { type: 'number', default: 300 },
    'confirm-timeout': { type: 'number', default: 3 },
    'connect-timeout': { type: 'number', default: 30 },
  });
} catch (err) {
  console.error(`${err.message}(用法見檔頭註解)`);
  process.exit(2);
}

const sink = makeSink(opt.out);
const startMs = nowMs();
const endMs = startMs + opt.minutes * 60_000;
// 舵令(度;右正左負)與車鐘(TelegraphOrder 代號)循環;故障取不影響操縱的感測器/儀器故障(src/Contracts/fault-catalogue.json)
const RUDDER_CYCLE = [10, 0, -10, 0, 5, 0, -5, 0];
const TELEGRAPH_CYCLE = ['SAH', 'DSAH', 'STOP', 'DSAH'];
const FAULT_CYCLE = ['gyroDrift', 'radarDegraded', 'gpsOffset', 'echoSounderFailure', 'logFailure', 'aisFailure'];

let ws = null;
let latest = null;
let stopping = false;
let exitCode = 0;
const totals = { actions: 0, confirmed: 0, unconfirmed: 0, unverified: 0, rejected: 0, disconnects: 0, errors: 0, stateFrames: 0, events: {} };
const pending = []; // { action, deadlineMs, check(state) → boolean, sentMs }
let orderIndex = 0;
let rudderIndex = 0;
let telegraphIndex = 0;
let faultIndex = 0;
let faultActive = null;
let lastOrder = startMs;
let lastFault = startMs;
let lastSnapshot = startMs;
let lastError = null;

function connect() {
  if (stopping) return;
  const s = new WebSocket(opt.ws);
  ws = s;
  let opened = false;
  s.addEventListener('open', () => { opened = true; });
  s.addEventListener('message', (ev) => {
    if (typeof ev.data !== 'string') return;
    let f;
    try { f = JSON.parse(ev.data); } catch { totals.errors++; lastError = '狀態 JSON 解析失敗'; return; }
    if (isStateFrame(f)) {
      latest = f;
      totals.stateFrames++;
      const now = nowMs();
      for (let i = pending.length - 1; i >= 0; i--) {
        const p = pending[i];
        if (p.check(f)) {
          pending.splice(i, 1);
          totals.confirmed++;
          sink({ ...p.action, confirmed: true, confirmMs: round(now - p.sentMs, 1), tick: f.tick });
        }
      }
      return;
    }
    if (f.type === 'event' && f.event && typeof f.event.kind === 'string') {
      totals.events[f.event.kind] = (totals.events[f.event.kind] ?? 0) + 1;
      if (f.event.kind === 'commandRejected') {
        totals.rejected++;
        lastError = `commandRejected:${JSON.stringify(f.event.data ?? f.event.message ?? '')}`;
        sink({ kind: 'rejected', wall: new Date().toISOString(), elapsed_s: elapsed(), event: f.event });
      }
    }
  });
  s.addEventListener('error', () => { totals.errors++; lastError = opened ? '連線錯誤' : `無法連線 ${opt.ws}`; });
  s.addEventListener('close', () => {
    if (stopping) return;
    if (opened) totals.disconnects++;
    ws = null;
    setTimeout(connect, 1000);
  });
}

const elapsed = () => round((nowMs() - startMs) / 1000, 1);

function send(cmd, describe, check) {
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    totals.errors++;
    lastError = '未連線,指令未送出';
    sink({ kind: 'action', wall: new Date().toISOString(), elapsed_s: elapsed(), ...describe, sent: false });
    return;
  }
  ws.send(JSON.stringify(cmd));
  totals.actions++;
  const action = { kind: 'action', wall: new Date().toISOString(), elapsed_s: elapsed(), ...describe, sent: true };
  if (check) pending.push({ action, check, sentMs: nowMs(), deadlineMs: nowMs() + opt['confirm-timeout'] * 1000 });
  else { totals.unverified++; sink({ ...action, confirmed: null }); }
}

function doOrder() {
  if (orderIndex++ % 2 === 0) {
    const v = RUDDER_CYCLE[rudderIndex++ % RUDDER_CYCLE.length];
    send({ type: 'rudder', value: v }, { type: 'rudder', value: v }, (s) => Math.abs(s.rudderOrder - v) < 0.01);
  } else {
    const v = TELEGRAPH_CYCLE[telegraphIndex++ % TELEGRAPH_CYCLE.length];
    send({ type: 'telegraph', value: v }, { type: 'telegraph', value: v }, (s) => s.telegraph === v);
  }
}

function doFault() {
  if (faultActive === null) {
    const name = FAULT_CYCLE[faultIndex++ % FAULT_CYCLE.length];
    faultActive = name;
    send({ type: 'injectFault', value: name }, { type: 'injectFault', value: name }, (s) => Array.isArray(s.faults) && s.faults.includes(name));
  } else {
    const name = faultActive;
    faultActive = null;
    send({ type: 'clearFault', value: name }, { type: 'clearFault', value: name }, (s) => Array.isArray(s.faults) && !s.faults.includes(name));
  }
}

function doSnapshot() {
  // snapshot 沒有事件/狀態回饋(核心只加入快照歷史並寫 input 行),只計送出
  send({ type: 'snapshot' }, { type: 'snapshot' }, null);
}

function finish(reason) {
  if (stopping) return;
  stopping = true;
  for (const p of pending) { totals.unconfirmed++; sink({ ...p.action, confirmed: false, reason: '結束時仍未確認' }); }
  pending.length = 0;
  // 收尾:舵回正、清除殘留故障(不等確認)
  try {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'rudder', value: 0 }));
      if (faultActive) ws.send(JSON.stringify({ type: 'clearFault', value: faultActive }));
    }
  } catch { /* 忽略 */ }
  sink({ kind: 'studentSummary', wall: new Date().toISOString(), elapsed_s: elapsed(), ...totals, lastError, reason, exitCode });
  try { ws?.close(); } catch { /* 已關 */ }
  setTimeout(() => process.exit(exitCode), 500).unref();
}

process.on('SIGINT', () => finish('SIGINT'));
process.on('SIGTERM', () => finish('SIGTERM'));

sink({ kind: 'studentStart', wall: new Date().toISOString(), ws: opt.ws, minutes: opt.minutes, orderInterval_s: opt['order-interval'], faultInterval_s: opt['fault-interval'], snapshotInterval_s: opt['snapshot-interval'], pid: process.pid });
connect();
const connectDeadline = nowMs() + opt['connect-timeout'] * 1000;
let started = false;

const timer = setInterval(() => {
  if (stopping) { clearInterval(timer); return; }
  const now = nowMs();
  if (!started) {
    if (latest) { started = true; lastOrder = lastFault = lastSnapshot = now; }
    else if (now > connectDeadline) {
      exitCode = 2;
      sink({ kind: 'error', wall: new Date().toISOString(), message: `${opt['connect-timeout']} s 內沒有收到狀態幀(${opt.ws})` });
      clearInterval(timer);
      finish('connectTimeout');
      return;
    }
    return;
  }
  for (let i = pending.length - 1; i >= 0; i--) {
    const p = pending[i];
    if (now > p.deadlineMs) {
      pending.splice(i, 1);
      totals.unconfirmed++;
      lastError = `${p.action.type} ${p.action.value ?? ''} 未在 ${opt['confirm-timeout']} s 內生效`;
      sink({ ...p.action, confirmed: false, reason: lastError, rudderOrder: latest?.rudderOrder, telegraph: latest?.telegraph, faults: latest?.faults });
    }
  }
  if (now - lastOrder >= opt['order-interval'] * 1000) { lastOrder = now; doOrder(); }
  if (now - lastFault >= opt['fault-interval'] * 1000) { lastFault = now; doFault(); }
  if (now - lastSnapshot >= opt['snapshot-interval'] * 1000) { lastSnapshot = now; doSnapshot(); }
  if (now >= endMs) { clearInterval(timer); finish('done'); }
}, 100);
