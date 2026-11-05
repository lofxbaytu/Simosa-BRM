#!/usr/bin/env node
// 合成 WebSocket 客戶端(長時間穩定性 soak 測試;tools/soak-test.sh / .ps1 啟動;規劃書第 14 章「效能」列、docs/design/testing.md 第 6 節):
// 一個程序內開 N 個客戶端,每個客戶端各一條核心狀態連線(ws://…:8765,25 Hz 狀態幀 + 事件框)與一條雷達影像連線(/radar/x,SRAD 二進位幀 + JSON),
// 選配每 30 s GET 一次靜態網頁(index.html)。每秒寫一筆統計到 --out(JSON Lines):狀態幀數、tick 區間、延遲(state.t 與到達牆鐘時間差;
// lib.mjs LatencyEstimator)p50/p95/max、雷達幀數與位元組、斷線/錯誤;結束時每客戶端一筆 clientSummary(整場延遲直方圖、首末 tick 與到達時刻、
// 總斷線/錯誤)。不送任何指令(「學員」由 student.mjs 扮演)。斷線後 1 s 重連並計數。SIGINT/SIGTERM 時提早收尾(仍寫 summary)。
// 用法:node tests/soak/clients.mjs --ws ws://127.0.0.1:8765/ [--radar ws://127.0.0.1:8766/radar/x] [--web http://127.0.0.1:8080] --clients 3 --minutes 10 --out build/soak/<run>/stats/clients.jsonl
import { LatencyEstimator, MsHistogram, isStateFrame, makeSink, nowMs, parseArgs, round } from './lib.mjs';

let opt;
try {
  opt = parseArgs(process.argv.slice(2), {
    ws: { type: 'string', default: 'ws://127.0.0.1:8765/' },
    radar: { type: 'string', default: '' },
    web: { type: 'string', default: '' },
    clients: { type: 'number', default: 3 },
    minutes: { type: 'number', default: 1 },
    out: { type: 'string', default: '' },
    interval: { type: 'number', default: 1 },
    'web-interval': { type: 'number', default: 30 },
    'connect-timeout': { type: 'number', default: 30 },
  });
} catch (err) {
  console.error(`${err.message}(用法見檔頭註解)`);
  process.exit(2);
}
if (opt.clients < 1 || opt.minutes <= 0) {
  console.error('--clients 須 ≥ 1、--minutes 須 > 0');
  process.exit(2);
}

const sink = makeSink(opt.out);
const startMs = nowMs();
const startWall = Date.now();
const endMs = startMs + opt.minutes * 60_000;
const SRAD = [0x53, 0x52, 0x41, 0x44]; // "SRAD" 幀 magic(src/Sensors.Radar/SpokeFrame.cs)
let stopping = false;

class Client {
  constructor(id) {
    this.id = id;
    this.latency = new LatencyEstimator();
    this.hist = new MsHistogram();
    this.secHist = new MsHistogram(600);
    this.state = { frames: 0, events: 0, tickFirst: null, tickLast: null, frozenFrames: 0, parseErrors: 0 };
    this.radar = { frames: 0, bytes: 0, json: 0, badMagic: 0 };
    this.web = { ok: 0, fail: 0, lastMs: null };
    this.totals = { disconnects: 0, errors: 0, stateFrames: 0, radarFrames: 0, radarBytes: 0, webOk: 0, webFail: 0, frozenFrames: 0, eventFrames: 0 };
    this.first = null; // { tick, arrivalMs }
    this.last = null;
    this.lastError = null;
    this.wsState = null;
    this.wsRadar = null;
    this.stateOpenedAt = null;
    this.radarOpenedAt = null;
  }

  connectState() {
    if (stopping) return;
    const ws = new WebSocket(opt.ws);
    this.wsState = ws;
    let opened = false;
    ws.addEventListener('open', () => {
      opened = true;
      this.stateOpenedAt = nowMs();
    });
    ws.addEventListener('message', (ev) => {
      if (typeof ev.data !== 'string') return;
      const arrival = nowMs();
      let f;
      try { f = JSON.parse(ev.data); } catch { this.state.parseErrors++; this.totals.errors++; this.lastError = 'state JSON 解析失敗'; return; }
      if (!isStateFrame(f)) { this.state.events++; this.totals.eventFrames++; return; }
      this.state.frames++;
      this.totals.stateFrames++;
      if (this.state.tickFirst === null) this.state.tickFirst = f.tick;
      this.state.tickLast = f.tick;
      if (this.first === null) this.first = { tick: f.tick, t: f.t, arrivalMs: arrival };
      this.last = { tick: f.tick, t: f.t, arrivalMs: arrival };
      if (f.flags && f.flags.frozen) { this.state.frozenFrames++; this.totals.frozenFrames++; return; }
      const lat = this.latency.observe(f.t, arrival);
      if (lat !== null) { this.hist.add(lat); this.secHist.add(lat); }
    });
    ws.addEventListener('error', () => {
      this.totals.errors++;
      this.lastError = opened ? '狀態連線錯誤' : `無法連線 ${opt.ws}`;
    });
    ws.addEventListener('close', () => {
      if (stopping) return;
      if (opened) this.totals.disconnects++;
      this.wsState = null;
      setTimeout(() => this.connectState(), 1000);
    });
  }

  connectRadar() {
    if (stopping || !opt.radar) return;
    const ws = new WebSocket(opt.radar);
    ws.binaryType = 'arraybuffer';
    this.wsRadar = ws;
    let opened = false;
    ws.addEventListener('open', () => {
      opened = true;
      this.radarOpenedAt = nowMs();
    });
    ws.addEventListener('message', (ev) => {
      if (typeof ev.data === 'string') { this.radar.json++; return; }
      const buf = new Uint8Array(ev.data);
      this.radar.frames++;
      this.radar.bytes += buf.byteLength;
      this.totals.radarFrames++;
      this.totals.radarBytes += buf.byteLength;
      if (buf.byteLength < 40 || buf[0] !== SRAD[0] || buf[1] !== SRAD[1] || buf[2] !== SRAD[2] || buf[3] !== SRAD[3]) {
        this.radar.badMagic++;
        this.totals.errors++;
        this.lastError = '雷達幀 magic 不符';
      }
    });
    ws.addEventListener('error', () => {
      this.totals.errors++;
      this.lastError = opened ? '雷達連線錯誤' : `無法連線 ${opt.radar}`;
    });
    ws.addEventListener('close', () => {
      if (stopping) return;
      if (opened) this.totals.disconnects++;
      this.wsRadar = null;
      setTimeout(() => this.connectRadar(), 1000);
    });
  }

  async checkWeb() {
    if (!opt.web) return;
    const t0 = nowMs();
    try {
      const r = await fetch(`${opt.web.replace(/\/$/, '')}/index.html`, { signal: AbortSignal.timeout(5000) });
      await r.arrayBuffer();
      if (r.ok) { this.web.ok++; this.totals.webOk++; } else { this.web.fail++; this.totals.webFail++; this.lastError = `網頁 HTTP ${r.status}`; }
    } catch (err) {
      this.web.fail++;
      this.totals.webFail++;
      this.lastError = `網頁 GET 失敗:${err?.message ?? err}`;
    }
    this.web.lastMs = round(nowMs() - t0, 1);
  }

  /** 每秒統計(寫一筆後歸零本秒計數) */
  flush(elapsedS) {
    const rec = {
      kind: 'client',
      id: this.id,
      wall: new Date().toISOString(),
      elapsed_s: elapsedS,
      state: {
        frames: this.state.frames,
        events: this.state.events,
        tickFirst: this.state.tickFirst,
        tickLast: this.state.tickLast,
        frozen: this.state.frozenFrames,
        latencyMs: this.secHist.count ? { n: this.secHist.count, p50: this.secHist.quantile(0.5), p95: this.secHist.quantile(0.95), max: round(this.secHist.max, 1) } : null,
      },
      radar: opt.radar ? { frames: this.radar.frames, bytes: this.radar.bytes, json: this.radar.json, badMagic: this.radar.badMagic } : null,
      web: opt.web ? { ok: this.web.ok, fail: this.web.fail, ms: this.web.lastMs } : null,
      connected: { state: this.wsState?.readyState === WebSocket.OPEN, radar: !opt.radar || this.wsRadar?.readyState === WebSocket.OPEN },
      disconnects: this.totals.disconnects,
      errors: this.totals.errors,
      lastError: this.lastError,
    };
    this.state = { frames: 0, events: 0, tickFirst: null, tickLast: null, frozenFrames: 0, parseErrors: 0 };
    this.radar = { frames: 0, bytes: 0, json: 0, badMagic: 0 };
    this.web = { ok: 0, fail: 0, lastMs: null };
    this.secHist = new MsHistogram(600);
    return rec;
  }

  summary(elapsedS) {
    const span = this.first && this.last && this.last.arrivalMs > this.first.arrivalMs ? (this.last.arrivalMs - this.first.arrivalMs) / 1000 : null;
    return {
      kind: 'clientSummary',
      id: this.id,
      elapsed_s: elapsedS,
      stateFrames: this.totals.stateFrames,
      eventFrames: this.totals.eventFrames,
      frozenFrames: this.totals.frozenFrames,
      stateHz: span ? round(this.totals.stateFrames / span, 2) : null,
      first: this.first,
      last: this.last,
      /** 以客戶端牆鐘量到的核心步率(tick/秒;規劃書 50 Hz) */
      tickRate: span && this.first && this.last ? round((this.last.tick - this.first.tick) / span, 3) : null,
      latencyMs: this.hist.toJSON(),
      radar: opt.radar ? { frames: this.totals.radarFrames, bytes: this.totals.radarBytes, framesPerS: span ? round(this.totals.radarFrames / span, 2) : null, kBPerS: span ? round(this.totals.radarBytes / 1024 / span, 1) : null } : null,
      web: opt.web ? { ok: this.totals.webOk, fail: this.totals.webFail } : null,
      disconnects: this.totals.disconnects,
      errors: this.totals.errors,
      lastError: this.lastError,
    };
  }

  close() {
    for (const ws of [this.wsState, this.wsRadar]) { try { ws?.close(); } catch { /* 已關 */ } }
  }
}

const clients = [];
for (let i = 0; i < opt.clients; i++) {
  const c = new Client(i);
  clients.push(c);
  c.connectState();
  c.connectRadar();
}
sink({ kind: 'clientsStart', wall: new Date(startWall).toISOString(), ws: opt.ws, radar: opt.radar || null, web: opt.web || null, clients: opt.clients, minutes: opt.minutes, pid: process.pid, node: process.version });

// 初次連線逾時:在 --connect-timeout 秒內沒有任何客戶端收到狀態幀就視為啟動失敗(結束碼 2)
const connectDeadline = nowMs() + opt['connect-timeout'] * 1000;
let anyFrame = false;
let lastWeb = nowMs();
let lastFlush = startMs;
let exitCode = 0;

function finish(reason) {
  if (stopping) return;
  stopping = true;
  const elapsedS = round((nowMs() - startMs) / 1000, 1);
  for (const c of clients) {
    sink(c.flush(elapsedS));
    sink(c.summary(elapsedS));
    c.close();
  }
  sink({ kind: 'clientsEnd', wall: new Date().toISOString(), elapsed_s: elapsedS, reason, exitCode });
  // WebSocket 關閉握手給 500 ms,之後強制結束(連線物件不會自動讓事件迴圈停下)
  setTimeout(() => process.exit(exitCode), 500).unref();
}

process.on('SIGINT', () => finish('SIGINT'));
process.on('SIGTERM', () => finish('SIGTERM'));

const timer = setInterval(() => {
  if (stopping) { clearInterval(timer); return; }
  const now = nowMs();
  if (!anyFrame) {
    anyFrame = clients.some((c) => c.totals.stateFrames > 0);
    if (!anyFrame && now > connectDeadline) {
      exitCode = 2;
      sink({ kind: 'error', wall: new Date().toISOString(), message: `${opt['connect-timeout']} s 內沒有收到任何狀態幀(${opt.ws})` });
      clearInterval(timer);
      finish('connectTimeout');
      return;
    }
  }
  if (opt.web && now - lastWeb >= opt['web-interval'] * 1000) {
    lastWeb = now;
    for (const c of clients) void c.checkWeb();
  }
  if (now - lastFlush >= opt.interval * 1000) {
    lastFlush = now;
    const elapsedS = round((now - startMs) / 1000, 1);
    for (const c of clients) sink(c.flush(elapsedS));
  }
  if (now >= endMs) {
    clearInterval(timer);
    finish('done');
  }
}, 100);
