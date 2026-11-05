// tests/soak/report.mjs 與 lib.mjs 的單元測試(Node 內建 test runner:node --test tests/soak/;無套件相依)。
// 以合成的執行目錄(meta.json、stats/*.jsonl)驗證判定準則:合格案例、掉拍/延遲/RSS 成長/斷線/學員未確認/主控程序未寫 footer 各自不合格;
// 直方圖分位數與延遲估計器的行為。暫存目錄在專案內 build/test-tmp/soak/(CLAUDE.md)。
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { LatencyEstimator, MsHistogram, quantileOf } from './lib.mjs';
import { loadRun, renderMarkdown, slope, summarise } from './report.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const TMP = path.join(ROOT, 'build', 'test-tmp', 'soak');

function writeRun(name, { minutes = 10, clients = 3, dropped = 0, rssSlopeMbPerMin = 0, latencyP95 = 12, disconnects = 0, unconfirmed = 0, rejected = 0, footer = true, hostExit = 0, tickRate = 50.0 } = {}) {
  const dir = path.join(TMP, name);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, 'stats'), { recursive: true });
  fs.mkdirSync(path.join(dir, 'logs'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'meta.json'), JSON.stringify({ run: name, scenario: 'E05_pilot_traffic', clients, minutes, startedUtc: '2026-11-05T00:00:00Z', endedUtc: '2026-11-05T00:10:00Z', platform: 'test', hostExit, footer, aborted: false, gitRev: 'abc' }));
  // 主控程序每 10 s 取樣:RSS = 120 MB + 斜率 × 分鐘(暖機 120 s 前多加 20 MB 的上升段)
  const host = [];
  for (let e = 0; e <= minutes * 60; e += 10) {
    const warm = e < 120 ? (20 * e) / 120 : 20;
    host.push({ kind: 'sample', elapsed_s: e, host: { alive: true, cpu_pct: 35.5, rss_mb: 120 + warm + (rssSlopeMbPerMin * e) / 60, threads: 30, stepsPerS: { n: 10, mean: 50, min: 48, max: 52 }, dropped: e >= minutes * 30 ? dropped : 0 }, radar: { alive: true, cpu_pct: 60, rss_mb: 200 } });
  }
  fs.writeFileSync(path.join(dir, 'stats', 'host.jsonl'), host.map((x) => JSON.stringify(x)).join('\n') + '\n');
  // 客戶端:每秒一筆 + 結束 summary(直方圖:p95 落在 latencyP95)
  const lines = [];
  for (let id = 0; id < clients; id++) {
    for (let e = 1; e <= minutes * 60; e++) lines.push({ kind: 'client', id, elapsed_s: e, state: { frames: 25, tickFirst: e * 50, tickLast: e * 50 + 48 }, disconnects: 0, errors: 0 });
    const h = new MsHistogram();
    for (let i = 0; i < 1000; i++) h.add(i < 940 ? Math.min(latencyP95 - 1, 5 + (i % 7)) : latencyP95 + (i % 5)); // 第 950 筆落在高群 → p95 = latencyP95
    const span = minutes * 60;
    lines.push({ kind: 'clientSummary', id, elapsed_s: span, stateFrames: 25 * span, eventFrames: 3, frozenFrames: 0, stateHz: 25, first: { tick: 100, arrivalMs: 0 }, last: { tick: 100 + Math.round(tickRate * span), arrivalMs: span * 1000 }, tickRate, latencyMs: h.toJSON(), radar: { frames: 24 * span, bytes: 1024 * span, framesPerS: 24, kBPerS: 1 }, web: { ok: 20, fail: 0 }, disconnects: id === 0 ? disconnects : 0, errors: 0, lastError: null });
  }
  fs.writeFileSync(path.join(dir, 'stats', 'clients.jsonl'), lines.map((x) => JSON.stringify(x)).join('\n') + '\n');
  const student = [{ kind: 'studentStart' }];
  for (let i = 0; i < 60; i++) student.push({ kind: 'action', type: i % 2 ? 'telegraph' : 'rudder', value: i % 2 ? 'SAH' : 10, elapsed_s: 10 * (i + 1), sent: true, confirmed: i < 60 - unconfirmed, confirmMs: 45 });
  student.push({ kind: 'studentSummary', actions: 62, confirmed: 60 - unconfirmed, unconfirmed, unverified: 2, rejected, disconnects: 0, errors: 0, events: { faultInjected: 5, faultCleared: 5 }, lastError: null });
  fs.writeFileSync(path.join(dir, 'stats', 'student.jsonl'), student.map((x) => JSON.stringify(x)).join('\n') + '\n');
  fs.writeFileSync(path.join(dir, 'logs', 'simcore.log'), footer ? '執行中…\n紀錄已關閉:x.jsonl(tick 30000)\n結束(原因:stopFile)。\n' : '執行中…\n');
  return dir;
}

test('合格案例:全部準則通過,Markdown 含判定與每客戶端列', () => {
  const dir = writeRun('pass');
  const s = summarise(loadRun(dir));
  assert.equal(s.pass, true, JSON.stringify(s.criteria.filter((c) => !c.pass)));
  assert.equal(s.criteria.length, 6);
  assert.equal(s.clients.count, 3);
  assert.equal(s.clients.latencyMs.p95 < 50, true);
  assert.equal(s.host.dropped, 0);
  assert.ok(Math.abs(s.host.rssPostWarmup.slopeMbPerMin) < 0.01, `斜率應約 0:${s.host.rssPostWarmup.slopeMbPerMin}`);
  const md = renderMarkdown(s);
  assert.match(md, /判定:\*\*合格\*\*/);
  assert.match(md, /\| 0 \| 15000 \|/);
  assert.match(md, /掉拍 0/);
});

test('掉拍 > 0 → 核心準則不合格', () => {
  const s = summarise(loadRun(writeRun('dropped', { dropped: 3 })));
  assert.equal(s.pass, false);
  assert.equal(s.criteria.find((c) => c.id === 'coreRate').pass, false);
  assert.equal(s.criteria.filter((c) => !c.pass).length, 1);
});

test('tick 率偏離 50 Hz → 核心準則不合格', () => {
  const s = summarise(loadRun(writeRun('tickrate', { tickRate: 48.9 })));
  assert.equal(s.criteria.find((c) => c.id === 'coreRate').pass, false);
});

test('延遲 p95 ≥ 50 ms → 延遲準則不合格;門檻可調', () => {
  const run = loadRun(writeRun('latency', { latencyP95: 60 }));
  const s = summarise(run);
  assert.equal(s.criteria.find((c) => c.id === 'latency').pass, false);
  assert.ok(s.clients.latencyMs.p95 >= 60);
  const relaxed = summarise(run, { maxLatencyMs: 100 });
  assert.equal(relaxed.criteria.find((c) => c.id === 'latency').pass, true);
});

test('RSS 持續成長(斜率 3 MB/min)→ RSS 準則不合格;暖機段的上升不計', () => {
  const s = summarise(loadRun(writeRun('rss', { rssSlopeMbPerMin: 3 })));
  const c = s.criteria.find((x) => x.id === 'rss');
  assert.equal(c.pass, false);
  assert.ok(Math.abs(s.host.rssPostWarmup.slopeMbPerMin - 3) < 0.05, `斜率應約 3:${s.host.rssPostWarmup.slopeMbPerMin}`);
  // 暖機 120 s 內 +20 MB 的上升不應讓合格案例失敗(writeRun 預設已含暖機段)
  const ok = summarise(loadRun(writeRun('rss-warm')));
  assert.equal(ok.criteria.find((x) => x.id === 'rss').pass, true);
});

test('客戶端斷線 → 連線準則不合格', () => {
  const s = summarise(loadRun(writeRun('disc', { disconnects: 1 })));
  assert.equal(s.criteria.find((c) => c.id === 'connections').pass, false);
  assert.equal(s.clients.disconnects, 1);
});

test('學員指令未確認或被拒 → 學員準則不合格', () => {
  assert.equal(summarise(loadRun(writeRun('stu1', { unconfirmed: 1 }))).criteria.find((c) => c.id === 'student').pass, false);
  assert.equal(summarise(loadRun(writeRun('stu2', { rejected: 1 }))).criteria.find((c) => c.id === 'student').pass, false);
});

test('主控程序未寫 footer 或結束碼非 0 → 主控程序準則不合格', () => {
  assert.equal(summarise(loadRun(writeRun('footer', { footer: false }))).criteria.find((c) => c.id === 'host').pass, false);
  assert.equal(summarise(loadRun(writeRun('exit', { hostExit: 134 }))).criteria.find((c) => c.id === 'host').pass, false);
});

test('MsHistogram 分位數與合併', () => {
  const h = new MsHistogram(100);
  for (let i = 1; i <= 100; i++) h.add(i - 0.5); // 0.5, 1.5, … 99.5 → 格 0..99
  assert.equal(h.count, 100);
  assert.equal(h.quantile(0.5), 50);
  assert.equal(h.quantile(0.95), 95);
  assert.equal(h.quantile(1), 99.5); // 溢位格回傳 max
  const g = MsHistogram.fromJSON(h.toJSON());
  assert.equal(g.quantile(0.5), 50);
  g.merge(h);
  assert.equal(g.count, 200);
  assert.equal(g.quantile(0.5), 50);
  assert.equal(quantileOf([1, 2, 3, 4], 0.5), 2);
});

test('LatencyEstimator:校正期後以最快一幀為原點,延遲 ≥ 0,核心落後時延遲上升', () => {
  const e = new LatencyEstimator(5);
  // 前 5 幀校正(到達 = 1000 + t×1000 + 抖動)
  for (let k = 0; k < 5; k++) assert.equal(e.observe(k * 0.04, 1000 + k * 40 + (k === 2 ? 0 : 8)), null);
  assert.equal(e.observe(0.2, 1000 + 200 + 10), 10);
  assert.equal(e.observe(0.24, 1000 + 240), 0);
  // 核心落後 30 ms(t 沒有跟上牆鐘)
  assert.equal(e.observe(0.28, 1000 + 280 + 30), 30);
});

test('slope 最小平方', () => {
  assert.equal(slope([[0, 1], [1, 3], [2, 5]]), 2);
  assert.equal(slope([[0, 1]]), null);
});
