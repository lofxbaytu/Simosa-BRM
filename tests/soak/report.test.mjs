// tests/soak/report.mjs 與 lib.mjs 的單元測試(Node 內建 test runner:node --test tests/soak/;無套件相依)。
// 以合成的執行目錄(meta.json、stats/*.jsonl)驗證判定準則:合格案例、掉拍/延遲/RSS 成長/斷線/學員未確認/主控程序未寫 footer 各自不合格;
// 客戶端收尾後的樣本(elapsed_s 超過設定時長或 clients.alive=false)不入 RSS 擬合;直方圖分位數與延遲估計器的行為。暫存目錄在專案內 build/test-tmp/soak/(CLAUDE.md)。
// 第三十輪(docs/design/testing.md 第 6.6 節):dotnet-counters 10.x 的計數器名稱帶單位與區間(前綴比對、MiB 換算);有 counters 時 RSS 準則的
// 各項(GC 次數、受控堆積、GC 已提交量、RSS − 已提交量、增量)——以懷疑者的 GC 簡化模型產生合成資料(洩漏有 / 無 counters、大預算、只 1 次 GC、
// 已提交量持續成長而受控堆積持平),以及 tests/soak/fixtures/ 的真實資料摘錄(實驗 B、e01 截取、e08)。
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { LatencyEstimator, MsHistogram, quantileOf } from './lib.mjs';
import { findCounter, gen0SettingText, loadRun, readCounters, renderMarkdown, slope, summarise } from './report.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const TMP = path.join(ROOT, 'build', 'test-tmp', 'soak');
const FIXTURES = path.join(ROOT, 'tests', 'soak', 'fixtures');

function writeRun(name, { minutes = 10, clients = 3, dropped = 0, rssSlopeMbPerMin = 0, latencyP95 = 12, disconnects = 0, unconfirmed = 0, rejected = 0, footer = true, hostExit = 0, tickRate = 50.0, tailSpikeMb = 0, clientsDeadFrom_s = Infinity, spikeAt_s = Infinity, spikeMb = 80 } = {}) {
  const dir = path.join(TMP, name);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, 'stats'), { recursive: true });
  fs.mkdirSync(path.join(dir, 'logs'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'meta.json'), JSON.stringify({ run: name, scenario: 'E05_pilot_traffic', clients, minutes, startedUtc: '2026-11-05T00:00:00Z', endedUtc: '2026-11-05T00:10:00Z', platform: 'test', hostExit, footer, aborted: false, gitRev: 'abc' }));
  // 主控程序每 10 s 取樣:RSS = 120 MB + 斜率 × 分鐘(暖機 120 s 前多加 20 MB 的上升段)
  const host = [];
  // clientsDeadFrom_s 起的樣本 clients.alive=false 並加 spikeMb;spikeAt_s 起(客戶端仍存活、時長內)加 spikeMb:對照組,應計入擬合
  const clientsJson = (alive) => (alive ? { alive: true, cpu_pct: 2, rss_mb: 88, threads: 7 } : { alive: false, cpu_pct: null, rss_mb: null, threads: null });
  for (let e = 0; e <= minutes * 60; e += 10) {
    const warm = e < 120 ? (20 * e) / 120 : 20;
    const dead = e >= clientsDeadFrom_s;
    const spike = dead || e >= spikeAt_s ? spikeMb : 0;
    host.push({ kind: 'sample', elapsed_s: e, host: { alive: true, cpu_pct: 35.5, rss_mb: 120 + warm + spike + (rssSlopeMbPerMin * e) / 60, threads: 30, stepsPerS: { n: 10, mean: 50, min: 48, max: 52 }, dropped: e >= minutes * 30 ? dropped : 0 }, radar: { alive: true, cpu_pct: 60, rss_mb: 200 }, clients: clientsJson(!dead) });
  }
  // 收尾樣本:客戶端時間到結束後腳本仍取到的一筆(elapsed 超過設定時長、clients.alive=false、RSS 尖峰)
  if (tailSpikeMb > 0) host.push({ kind: 'sample', elapsed_s: minutes * 60 + 6.2, host: { alive: true, cpu_pct: 12, rss_mb: 140 + tailSpikeMb, threads: 30, stepsPerS: { n: 10, mean: 50, min: 50, max: 50 }, dropped: 0 }, radar: { alive: true, cpu_pct: 60, rss_mb: 200 }, clients: clientsJson(false) });
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

test('客戶端收尾後的樣本不入 RSS 擬合:elapsed 超過設定時長的尖峰、clients.alive=false 的段落皆排除;時長內客戶端存活的尖峰仍計入', () => {
  // 1) 最後一筆在設定時長之後(實跑的典型:客戶端斷線時核心 +數十 MB):擬合排除、原始統計(最大值、樣本數)仍含
  const s = summarise(loadRun(writeRun('tail', { tailSpikeMb: 80 })));
  const rss = s.criteria.find((c) => c.id === 'rss');
  assert.equal(rss.pass, true, rss.measured);
  assert.ok(Math.abs(s.host.rssPostWarmup.slopeMbPerMin) < 0.01, `斜率應約 0:${s.host.rssPostWarmup.slopeMbPerMin}`);
  assert.equal(s.host.rssPostWarmup.tailExcluded, 1);
  assert.equal(s.host.rssPostWarmup.last, 140);
  assert.equal(s.host.rssMb.max, 220);
  assert.equal(s.host.samples, 62);
  assert.equal(s.host.elapsed_s, 606.2);
  assert.match(rss.measured, /排除收尾 1 筆/);
  assert.match(renderMarkdown(s), /收尾 1 筆不入擬合/);
  // 2) 時長內但 clients.alive=false(學員腳本仍在、客戶端已結束):該段落排除,斜率仍約 0
  const s2 = summarise(loadRun(writeRun('tail-dead', { clientsDeadFrom_s: 540 })));
  assert.equal(s2.criteria.find((c) => c.id === 'rss').pass, true);
  assert.equal(s2.host.rssPostWarmup.tailExcluded, 7);
  assert.ok(Math.abs(s2.host.rssPostWarmup.slopeMbPerMin) < 0.01, `斜率應約 0:${s2.host.rssPostWarmup.slopeMbPerMin}`);
  // 3) 對照:時長內、客戶端存活的 +80 MB 階梯仍計入 → 增量超過 64 MB 不合格(排除只針對收尾)
  const s3 = summarise(loadRun(writeRun('tail-ctl', { spikeAt_s: 540 })));
  assert.equal(s3.criteria.find((c) => c.id === 'rss').pass, false);
  assert.equal(s3.host.rssPostWarmup.tailExcluded, 0);
  assert.doesNotMatch(s3.criteria.find((c) => c.id === 'rss').measured, /排除收尾/);
  // 4) 全部樣本都是收尾(客戶端一開始就結束)→ 退回用全部樣本,RSS 準則仍有資料
  const s4 = summarise(loadRun(writeRun('tail-all', { clientsDeadFrom_s: 0 })));
  assert.equal(s4.host.rssPostWarmup.tailExcluded, 0);
  assert.equal(s4.host.rssPostWarmup.samples > 2, true);
  // 5) 舊 meta 無 minutes:只靠 clients.alive 排除
  const dir = writeRun('tail-nomin', { tailSpikeMb: 80 });
  const meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json'), 'utf8'));
  delete meta.minutes;
  fs.writeFileSync(path.join(dir, 'meta.json'), JSON.stringify(meta));
  const s5 = summarise(loadRun(dir));
  assert.equal(s5.host.rssPostWarmup.tailExcluded, 1);
  assert.equal(s5.criteria.find((c) => c.id === 'rss').pass, true);
});

// ---- 第三十輪:dotnet-counters 與 RSS 準則 ----

const MIB = 1048576;
/** dotnet-counters 時間戳(容器 en-US、UTC);報告不解析,只為了格式像真的 */
const ctrTs = (t) => { const d = new Date(Date.UTC(2026, 10, 22, 0, 0, t)); return `${d.getUTCMonth() + 1}/${d.getUTCDate()}/${d.getUTCFullYear()} ${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}:${String(d.getUTCSeconds()).padStart(2, '0')}`; };

/** 只為 RSS 準則的執行目錄:meta.json、host.jsonl、clients / student 摘要;counters 為 csv 字串或 null */
function writeMinimalRun(name, { minutes, host, counters, gc = undefined }) {
  const dir = path.join(TMP, name);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, 'stats'), { recursive: true });
  fs.mkdirSync(path.join(dir, 'logs'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'meta.json'), JSON.stringify({ run: name, scenario: 'E05_pilot_traffic', clients: 2, minutes, sample_s: 10, platform: 'synth', hostExit: 0, footer: true, aborted: false, counters: !!counters, ...(gc === undefined ? {} : { gc }) }));
  fs.writeFileSync(path.join(dir, 'stats', 'host.jsonl'), host.map((x) => JSON.stringify(x)).join('\n') + '\n');
  fs.writeFileSync(path.join(dir, 'stats', 'clients.jsonl'), [0, 1].map((id) => JSON.stringify({ kind: 'clientSummary', id, elapsed_s: minutes * 60, stateFrames: 25 * minutes * 60, eventFrames: 0, frozenFrames: 0, stateHz: 25, tickRate: 50, disconnects: 0, errors: 0 })).join('\n') + '\n');
  fs.writeFileSync(path.join(dir, 'stats', 'student.jsonl'), JSON.stringify({ kind: 'studentSummary', actions: 1, confirmed: 1, unconfirmed: 0, rejected: 0, disconnects: 0, errors: 0, events: {} }) + '\n');
  if (counters) fs.writeFileSync(path.join(dir, 'stats', 'counters.csv'), counters);
  fs.writeFileSync(path.join(dir, 'logs', 'simcore.log'), '紀錄已關閉:x.jsonl\n');
  return dir;
}
const hostSample = (e, rss, alive = true) => ({ kind: 'sample', elapsed_s: e, host: { alive: true, cpu_pct: 12, rss_mb: Math.round(rss * 10) / 10, threads: 22, stepsPerS: { n: 10, mean: 50, min: 50, max: 50 }, dropped: 0 }, radar: null, web: null, clients: { alive, cpu_pct: 2, rss_mb: 68, threads: 7 } });

/**
 * 第三十輪懷疑者的 GC 簡化模型(合成 soak 執行目錄;每秒推進,每 10 s 一筆取樣與一次 counters 刷新):
 * 配置速率 A MB/min、gen0 預算 B MB、存活物件洩漏 L MB/min(受控堆積)、原生洩漏 N MB/min(非 GC)、GC 已提交量自行成長 F MB/min(碎片;存活不變)、基準 R0 MB。
 * 配置量累積到 B 時 GC;GC 已提交量 = 第一次 GC 前為已配置量(gen0 預算填充,counters 讀到 0),之後只在 GC 時更新為 max(前值, B + 存活 + 8 + F·t),以 commitStep 進位;
 * RSS = R0 + 已提交量 + 原生洩漏。counters 用 dotnet-counters 10.x 的名稱(帶單位與區間),大小以 10^6 B 的 MB 或 B 寫出(報告換算回 MiB)。
 */
function writeGcRun(name, { minutes, A = 35, B = 136, L = 0, N = 0, F = 0, R0 = 95, counters = true, commitStep = 16, live0 = 8, gc, omit = [] }) {
  const host = [];
  const csv = ['Timestamp,Provider,Counter Name,Counter Type,Mean/Increment'];
  let allocSince = 0; let committed = 0; let firstGc = false; let liveAtGc = live0; let gcInRefresh = 0;
  for (let t = 1; t <= minutes * 60; t++) {
    allocSince += A / 60;
    const live = live0 + (L * t) / 60;
    if (allocSince >= B) {
      allocSince = 0; gcInRefresh++; firstGc = true; liveAtGc = live;
      committed = Math.max(committed, Math.ceil((B + live + 8 + (F * t) / 60) / commitStep) * commitStep);
    }
    if (t % 10) continue;
    const committedNow = firstGc ? committed : allocSince + live0;
    host.push(hostSample(t, R0 + committedNow + (N * t) / 60));
    const row = (n, ty, v) => { if (!omit.some((o) => n.startsWith(o))) csv.push(`${ctrTs(t)},System.Runtime,${n},${ty},${v}`); };
    row('GC Heap Size (MB)', 'Metric', ((allocSince + liveAtGc) * MIB / 1e6).toFixed(3));
    row('Gen 0 GC Count (Count / 10 sec)', 'Rate', gcInRefresh);
    row('Gen 1 GC Count (Count / 10 sec)', 'Rate', 0);
    row('Gen 2 GC Count (Count / 10 sec)', 'Rate', 0);
    row('Gen 0 GC Budget (MB)', 'Metric', Math.floor((B * MIB) / 1e6));
    row('Allocation Rate (B / 10 sec)', 'Rate', Math.round((A * MIB) / 6));
    row('GC Committed Bytes (MB)', 'Metric', firstGc ? ((committed * MIB) / 1e6).toFixed(6) : 0);
    row('Time paused by GC (ms / 10 sec)', 'Rate', gcInRefresh * 2);
    row('Gen 0 Size (B)', 'Metric', 0);
    row('Gen 1 Size (B)', 'Metric', firstGc ? MIB : 0);
    row('Gen 2 Size (B)', 'Metric', firstGc ? Math.round((liveAtGc - 1) * MIB) : 0);
    row('LOH Size (B)', 'Metric', firstGc ? 100000 : 0);
    row('POH (Pinned Object Heap) Size (B)', 'Metric', firstGc ? 60000 : 0);
    row('Exception Count (Count / 10 sec)', 'Rate', 0);
    gcInRefresh = 0;
  }
  return writeMinimalRun(name, { minutes, host, counters: counters ? csv.join('\n') + '\n' : null, gc });
}

/** tests/soak/fixtures/*.json(fixtures/extract.mjs 擷取的真實執行)→ 執行目錄;untilS 截到前 T 秒(取樣 ≤ T、counters 刷新時刻 ≤ 最後一筆取樣 + 1 s) */
function writeFixtureRun(file, { untilS = Infinity } = {}) {
  const fx = JSON.parse(fs.readFileSync(path.join(FIXTURES, file), 'utf8'));
  const rows = fx.host.rows.filter(([e]) => e <= untilS);
  const host = rows.map(([e, rss, alive]) => hostSample(e, rss, alive === 1));
  const lastE = rows[rows.length - 1][0];
  const names = { gc: ['Gen 0 GC Count (Count / 10 sec)', 'Rate', 1], committedMB: ['GC Committed Bytes (MB)', 'Metric', 1], budgetMB: ['Gen 0 GC Budget (MB)', 'Metric', 1],
    gen1KiB: ['Gen 1 Size (B)', 'Metric', 1024], gen2KiB: ['Gen 2 Size (B)', 'Metric', 1024], lohKiB: ['LOH Size (B)', 'Metric', 1024], pohKiB: ['POH (Pinned Object Heap) Size (B)', 'Metric', 1024] };
  const csv = ['Timestamp,Provider,Counter Name,Counter Type,Mean/Increment'];
  fx.counters.rows.forEach((r, i) => {
    const t = (i + 1) * fx.counters.intervalS;
    if (t > lastE + 1) return;
    fx.counters.fields.forEach((fname, k) => { if (r[k] !== null) csv.push(`${ctrTs(t)},System.Runtime,${names[fname][0]},${names[fname][1]},${r[k] * names[fname][2]}`); });
  });
  const minutes = Number.isFinite(untilS) ? untilS / 60 : fx.meta.minutes;
  return { fx, dir: writeMinimalRun(`fx-${path.basename(file, '.json')}-${Number.isFinite(untilS) ? untilS : 'all'}`, { minutes, host, counters: csv.join('\n') + '\n', gc: fx.meta.gc ?? undefined }) };
}
const rssOf = (s) => s.criteria.find((c) => c.id === 'rss');
const checkOf = (s, id) => s.rssEval.checks.find((c) => c.id === id);

test('dotnet-counters 10.x 的名稱帶單位與區間:前綴比對、MB(10^6 B)與 B 換算成 MiB、一次刷新跨秒也不拆開', () => {
  const names = ['Gen 0 GC Count (Count / 10 sec)', 'Gen 0 GC Budget (MB)', 'Gen 0 Size (B)', 'GC Heap Size (MB)', 'POH (Pinned Object Heap) Size (B)'];
  assert.equal(findCounter(names, 'Gen 0 GC Count'), 'Gen 0 GC Count (Count / 10 sec)');
  assert.equal(findCounter(names, 'Gen 0 Size'), 'Gen 0 Size (B)');            // 不會誤配到「Gen 0 GC …」
  assert.equal(findCounter(names, 'POH (Pinned Object Heap) Size'), 'POH (Pinned Object Heap) Size (B)');
  assert.equal(findCounter(['Gen 0 GC Count'], 'Gen 0 GC Count'), 'Gen 0 GC Count');   // 舊版沒有單位的名稱
  assert.equal(findCounter(names, 'Gen 1 GC Count'), null);
  const file = path.join(TMP, 'counters-units.csv');
  fs.mkdirSync(TMP, { recursive: true });
  // 第 2 次刷新跨秒(16:31:16 / 16:31:17):依名稱重複分組,仍是 3 次刷新
  fs.writeFileSync(file, ['Timestamp,Provider,Counter Name,Counter Type,Mean/Increment',
    '11/22/2026 16:31:06,System.Runtime,Gen 0 GC Count (Count / 10 sec),Rate,0', '11/22/2026 16:31:06,System.Runtime,GC Committed Bytes (MB),Metric,0', '11/22/2026 16:31:06,System.Runtime,Gen 2 Size (B),Metric,0', '11/22/2026 16:31:06,System.Runtime,Gen 0 GC Budget (MB),Metric,136',
    '11/22/2026 16:31:16,System.Runtime,Gen 0 GC Count (Count / 10 sec),Rate,1', '11/22/2026 16:31:17,System.Runtime,GC Committed Bytes (MB),Metric,140.75904', '11/22/2026 16:31:17,System.Runtime,Gen 2 Size (B),Metric,5242880', '11/22/2026 16:31:17,System.Runtime,Gen 0 GC Budget (MB),Metric,136',
    '11/22/2026 16:31:26,System.Runtime,Gen 0 GC Count (Count / 10 sec),Rate,2', '11/22/2026 16:31:26,System.Runtime,GC Committed Bytes (MB),Metric,140.75904', '11/22/2026 16:31:26,System.Runtime,Gen 2 Size (B),Metric,6291456', '11/22/2026 16:31:26,System.Runtime,Gen 0 GC Budget (MB),Metric,136'].join('\n') + '\n');
  const c = readCounters(file, 10);
  assert.equal(c.refreshes, 3);
  assert.deepEqual(c.missing, []);
  assert.equal(c.gen0, 3);
  assert.equal(c.firstGcS, 20);
  assert.equal(c.gen0BudgetMb.max, 129.7);              // 136 × 10^6 B = 129.7 MiB
  assert.equal(c.committedMb.first, 134.23);            // 140.75904 × 10^6 B = 134.23 MiB(第一次 GC 前的 0 不算首值)
  assert.deepEqual(c.timeline.map((x) => x.managedMb), [0, 5, 6]);
  assert.deepEqual(c.timeline.map((x) => x.t), [10, 20, 30]);
  // 修正前的程式以完整名稱「Gen 0 GC Count」比對,10.x 的名稱帶區間 → 一直是 null
  assert.notEqual(c.gen0, null);
});

test('懷疑者合成資料:2 小時每分鐘 5 MB 存活洩漏(gen0 預算 6 MB)有 counters → 不合格(受控堆積);無 counters → 不合格並註明無法分辨 gen0 預算填充', () => {
  const s = summarise(loadRun(writeGcRun('gc-leak-2h-6mb', { minutes: 120, B: 6, L: 5 })));
  const r = rssOf(s);
  assert.equal(r.pass, false);
  assert.equal(r.rule, 'gc-counters');
  assert.equal(r.undetermined, false);
  assert.equal(checkOf(s, 'managed').pass, false);
  assert.ok(Math.abs(checkOf(s, 'managed').value - 5) < 0.2, `受控堆積斜率應約 5:${checkOf(s, 'managed').value}`);
  assert.match(r.note, /存活物件累積/);
  const n = summarise(loadRun(writeGcRun('gc-leak-2h-6mb-noctr', { minutes: 120, B: 6, L: 5, counters: false })));
  const rn = rssOf(n);
  assert.equal(rn.pass, false);
  assert.equal(rn.rule, 'rss-only');
  assert.match(rn.note, /無 dotnet-counters/);
  assert.match(rn.note, /無法分辨 gen0 預算填充/);
  assert.equal(n.rssEval.checks, undefined);
});

test('大 gen0 預算(264 MB,L3 504 MiB 的雲端機器):2 小時洩漏 → 不合格;10 分鐘只 GC 1 次 → 無法判定(不合格並註明),不會因暖機延長而合格', () => {
  const long = summarise(loadRun(writeGcRun('gc-leak-2h-264mb', { minutes: 120, B: 264, L: 5 })));
  assert.equal(rssOf(long).pass, false);
  assert.equal(checkOf(long, 'managed').pass, false);
  // 第三十輪實驗 B 的型態:第一次 GC 之後 RSS 與受控堆積都還沒機會上升(修正前的提案規則把這種情形判合格)
  const short = summarise(loadRun(writeGcRun('gc-leak-10m-264mb', { minutes: 10, B: 264, L: 5 })));
  const r = rssOf(short);
  assert.equal(r.pass, false);
  assert.equal(r.undetermined, true);
  assert.equal(short.rssEval.gcCount, 1);
  assert.match(r.note, /無法判定/);
  assert.match(r.note, /只 GC 1 次/);
  assert.match(r.note, /--gen0-budget-mb/);
  assert.match(renderMarkdown(short), /無法判定\(不通過\)/);
  // 同樣 10 分鐘、沒有洩漏也一樣無法判定(GC 次數不足就不判,不看斜率)
  const ok = summarise(loadRun(writeGcRun('gc-noleak-10m-264mb', { minutes: 10, B: 264 })));
  assert.equal(rssOf(ok).pass, false);
  assert.equal(rssOf(ok).undetermined, true);
});

test('整段未發生 GC(gen0 預算未用完)→ 無法判定', () => {
  const s = summarise(loadRun(writeGcRun('gc-nogc', { minutes: 10, B: 500 })));
  const r = rssOf(s);
  assert.equal(r.pass, false);
  assert.equal(r.undetermined, true);
  assert.equal(s.rssEval.noGc, true);
  assert.match(r.note, /整段未發生 GC/);
});

test('GC 已提交量持續成長而受控堆積持平(碎片)→ 不合格;非 GC(原生)記憶體成長 → 不合格', () => {
  // 懷疑者 S10:10 分鐘、gen0 預算 6 MB、已提交量每分鐘 +4 MB、存活不變 → RSS 增量約 40 MB 未超過 64 MB,只靠已提交量的斜率攔下
  const s = summarise(loadRun(writeGcRun('gc-committed-grows', { minutes: 10, B: 6, F: 4, commitStep: 1 })));
  const r = rssOf(s);
  assert.equal(r.pass, false, r.measured);
  assert.equal(r.undetermined, false);
  assert.equal(checkOf(s, 'managed').pass, true);
  assert.equal(checkOf(s, 'nonGc').pass, true);
  assert.equal(checkOf(s, 'growth').pass, true);
  assert.equal(checkOf(s, 'committed').pass, false);
  assert.ok(checkOf(s, 'committed').value > 3, `已提交量斜率應約 4:${checkOf(s, 'committed').value}`);
  const nat = summarise(loadRun(writeGcRun('gc-native-grows', { minutes: 10, B: 6, N: 2 })));
  assert.equal(rssOf(nat).pass, false);
  assert.equal(checkOf(nat, 'nonGc').pass, false);
  assert.equal(checkOf(nat, 'managed').pass, true);
  assert.equal(checkOf(nat, 'committed').pass, true);
});

test('沒有洩漏:小預算 10 分鐘合格;大預算 2 小時合格(暖機延到第一次 GC 之後,gen0 預算填充不算成長)', () => {
  const small = summarise(loadRun(writeGcRun('gc-ok-10m-6mb', { minutes: 10, B: 6, gc: { gen0BudgetMb: 6, env: { DOTNET_GCgen0size: '0x600000' }, appliesTo: ['simcore', 'radar'], inherited: {} } })));
  const r = rssOf(small);
  assert.equal(r.pass, true, `${r.measured} / ${r.note}`);
  assert.equal(small.host.warmup_s, 120);
  assert.ok(small.rssEval.gcCount > 50);
  const md = renderMarkdown(small);
  assert.match(md, /gen0 預算設定:6 MB(simcore、radar 以 DOTNET_GCgen0size=0x600000 啟動/);
  assert.match(md, /RSS 準則逐項/);
  assert.match(md, /受控堆積(上次 GC 後 gen1\+gen2\+LOH\+POH)/);
  assert.match(md, /GC 已提交量/);
  assert.match(md, /第一次 GC:counters 開始後 \d+ s 內/);
  const big = summarise(loadRun(writeGcRun('gc-ok-2h-264mb', { minutes: 120, B: 264 })));
  assert.equal(rssOf(big).pass, true, rssOf(big).note);
  assert.ok(big.host.warmup_s > 400, `暖機應延到第一次 GC(約 453 s)之後:${big.host.warmup_s}`);
  assert.match(rssOf(big).note, /暖機延到第一次 GC/);
  // 同一條曲線沒有 counters:第一次 GC 前的上升落在擬合段內 → 只看 RSS 的規則不合格(這就是預設要設小 gen0 預算的原因)
  const bigNo = summarise(loadRun(writeGcRun('gc-ok-2h-264mb-noctr', { minutes: 120, B: 264, counters: false })));
  assert.equal(rssOf(bigNo).pass, false);
});

test('counters.csv 缺必要的計數器 → 以只看 RSS 的規則判定並註明;meta.gc 的設定寫進報告', () => {
  const s = summarise(loadRun(writeGcRun('gc-missing', { minutes: 10, B: 6, omit: ['GC Committed Bytes'] })));
  const r = rssOf(s);
  assert.equal(r.rule, 'rss-only');
  assert.deepEqual(s.gc.missing, ['GC Committed Bytes']);
  assert.match(r.note, /缺少 GC Committed Bytes/);
  assert.equal(gen0SettingText({}), '未記錄(meta.json 無 gc 欄位:舊版 soak 腳本,執行階段預設或外部環境變數)');
  assert.equal(gen0SettingText({ gc: { gen0BudgetMb: 0, env: {}, inherited: { DOTNET_GCHeapHardLimit: '0x6400000' } } }), '執行階段預設(--gen0-budget-mb 0);繼承的 GC 環境變數 DOTNET_GCHeapHardLimit=0x6400000');
});

test('真實資料摘錄(tests/soak/fixtures/):實驗 B 無法判定、e01 截到 12 分鐘不合格(受控堆積)、e08 25 分鐘與各截取點合格', () => {
  // 實驗 B:修正前程式、gen0 預算 264 MB、10 分鐘 → 修正前提案的規則六項全合格;現在 GC 只 1 次 → 無法判定
  const b = writeFixtureRun('exp-b-orig-gen0-264mb.json');
  assert.equal(b.fx.expect, 'undetermined');
  const sb = summarise(loadRun(b.dir));
  assert.equal(rssOf(sb).pass, false);
  assert.equal(rssOf(sb).undetermined, true);
  assert.equal(sb.rssEval.gcCount, 1);
  // e01:修正前程式、預設 gen0 預算(136 MB);10 分鐘時只 GC 2 次 → 無法判定,12 分鐘 GC 4 次 → 受控堆積 5 MB/min 不合格
  const e01 = summarise(loadRun(writeFixtureRun('e01-orig-default-720s.json').dir));
  assert.equal(rssOf(e01).pass, false);
  assert.equal(rssOf(e01).undetermined, false);
  assert.equal(checkOf(e01, 'managed').pass, false);
  assert.ok(checkOf(e01, 'managed').value > 4, `受控堆積斜率:${checkOf(e01, 'managed').value}`);
  const e01at600 = summarise(loadRun(writeFixtureRun('e01-orig-default-720s.json', { untilS: 600 }).dir));
  assert.equal(rssOf(e01at600).undetermined, true);
  // e08:修正後程式、gen0 預算 10 MB;全段與 4 / 8 / 12 分鐘截取點皆合格
  for (const untilS of [Infinity, 240, 480, 720]) {
    const s = summarise(loadRun(writeFixtureRun('e08-fix-hard100mb.json', { untilS }).dir));
    const r = rssOf(s);
    assert.equal(r.pass, true, `e08 截到 ${untilS} s:${r.measured} / ${r.note}`);
    assert.ok(Math.abs(checkOf(s, 'managed').value) < 0.3);
  }
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
