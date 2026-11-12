#!/usr/bin/env node
// 長時間穩定性(soak)測試報告(tools/soak-test.sh / .ps1 於結束後呼叫;docs/design/testing.md 第 6 節):
// 讀一次執行目錄 build/soak/<run>/ 的 meta.json、stats/host.jsonl(每 10 s 的主控程序 CPU%/RSS/steps/s/掉拍)、stats/clients.jsonl(合成客戶端每秒統計與
// 整場延遲直方圖)、stats/student.jsonl(學員指令確認)、選配 stats/counters.csv(dotnet-counters),寫 Markdown 與 JSON 報告並依規劃書第 14 章「效能」列判定:
//   1) 核心 50 Hz 無掉拍:主控程序「掉拍」計數為 0,且客戶端以牆鐘量到的 tick 率在 50 ± --core-hz-tol;
//   2) 延遲 < --max-latency-ms(預設 50):全部客戶端合併的狀態幀延遲 p95(最大值另列);
//   3) RSS 不持續成長:暖機(--warmup-s)後最小平方斜率 ≤ --max-rss-slope-mb-per-min 且暖機後增量 ≤ --max-rss-growth-mb;
//      擬合排除客戶端收尾後的樣本(elapsed_s > meta.minutes×60 或 clients.alive=false:客戶端斷線時核心釋放連線常有一筆尖峰);
//   4) 無斷線/錯誤:客戶端斷線 0、錯誤 0、網頁失敗 0;學員指令全部於時限內生效、無 commandRejected;主控程序存活到結束並寫完紀錄 footer。
// 結束碼:0 合格、1 不合格、2 讀檔/參數錯誤。可當模組匯入(summarise / renderMarkdown;tests/soak/report.test.mjs)。
// 用法:node tests/soak/report.mjs --dir build/soak/<run> --md build/soak/report-<run>.md --json build/soak/report-<run>.json [門檻參數]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { MsHistogram, parseArgs, quantileOf, round } from './lib.mjs';

export const DEFAULT_THRESHOLDS = {
  coreHz: 50,
  coreHzTol: 0.5,
  maxLatencyMs: 50,
  maxRssSlopeMbPerMin: 1.0,
  maxRssGrowthMb: 64,
  warmupS: 120,
};

function readJsonl(file) {
  if (!fs.existsSync(file)) return [];
  const out = [];
  for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
    const s = line.trim();
    if (!s) continue;
    try { out.push(JSON.parse(s)); } catch { /* 寫到一半的行(例如程序被強制結束)略過 */ }
  }
  return out;
}

/** 讀 dotnet-counters 的 csv(Timestamp,Provider,Counter Name,Counter Type,Mean/Increment);不存在回 null */
function readCounters(file) {
  if (!fs.existsSync(file)) return null;
  const rows = fs.readFileSync(file, 'utf8').split('\n').slice(1).map((l) => l.split(',')).filter((r) => r.length >= 5);
  const by = {};
  for (const r of rows) {
    const name = r[2].trim();
    const v = Number(r[4]);
    if (!Number.isFinite(v)) continue;
    (by[name] ??= []).push(v);
  }
  const pick = (n) => by[n] ?? null;
  const last = (a) => (a && a.length ? a[a.length - 1] : null);
  const sum = (a) => (a && a.length ? a.reduce((x, y) => x + y, 0) : null);
  const max = (a) => (a && a.length ? Math.max(...a) : null);
  return {
    samples: rows.length,
    gcHeapMb: { last: last(pick('GC Heap Size (MB)')), max: max(pick('GC Heap Size (MB)')) },
    gen0: sum(pick('Gen 0 GC Count')),
    gen1: sum(pick('Gen 1 GC Count')),
    gen2: sum(pick('Gen 2 GC Count')),
    workingSetMb: { last: last(pick('Working Set (MB)')), max: max(pick('Working Set (MB)')) },
    counters: Object.keys(by),
  };
}

export function loadRun(dir) {
  const metaFile = path.join(dir, 'meta.json');
  if (!fs.existsSync(metaFile)) throw new Error(`找不到 ${metaFile}`);
  const meta = JSON.parse(fs.readFileSync(metaFile, 'utf8').replace(/^\uFEFF/, ''));   // 容許開頭的 BOM(Windows PowerShell 5.1 寫的檔)
  const stats = path.join(dir, 'stats');
  return {
    dir,
    meta,
    host: readJsonl(path.join(stats, 'host.jsonl')),
    clients: readJsonl(path.join(stats, 'clients.jsonl')),
    student: readJsonl(path.join(stats, 'student.jsonl')),
    counters: readCounters(path.join(stats, 'counters.csv')),
    simcoreLog: fs.existsSync(path.join(dir, 'logs', 'simcore.log')) ? fs.readFileSync(path.join(dir, 'logs', 'simcore.log'), 'utf8') : '',
  };
}

/** 最小平方斜率(y 對 x) */
export function slope(points) {
  const n = points.length;
  if (n < 2) return null;
  let sx = 0, sy = 0, sxx = 0, sxy = 0;
  for (const [x, y] of points) { sx += x; sy += y; sxx += x * x; sxy += x * y; }
  const d = n * sxx - sx * sx;
  return d === 0 ? null : (n * sxy - sx * sy) / d;
}

const mean = (a) => (a.length ? a.reduce((x, y) => x + y, 0) / a.length : null);

export function summarise(run, thresholds = {}) {
  const th = { ...DEFAULT_THRESHOLDS, ...thresholds };
  const { meta } = run;

  // ---- 主控程序取樣 ----
  const samples = run.host.filter((s) => s.kind === 'sample' && s.host);
  const elapsedMax = samples.length ? samples[samples.length - 1].elapsed_s : 0;
  const warmup = Math.min(th.warmupS, elapsedMax * 0.2);
  // RSS 擬合排除客戶端收尾後的樣本:超過設定時長(meta.minutes×60)或合成客戶端程序已結束(clients.alive=false)——
  // 客戶端斷線時核心釋放連線與訂閱常有一筆 RSS 尖峰,不代表持續成長;CPU/RSS 起訖/執行緒等原始統計與 hostSamples 仍含全部樣本。
  // 全部都是收尾樣本時(客戶端提前結束)退回用全部樣本,免得 RSS 準則變成「無資料」
  const plannedS = Number.isFinite(Number(meta.minutes)) && Number(meta.minutes) > 0 ? Number(meta.minutes) * 60 : Infinity;
  const isTail = (s) => s.elapsed_s > plannedS || s.clients?.alive === false;
  const kept = samples.filter((s) => !isTail(s));
  const fitSamples = kept.length >= 2 ? kept : samples;
  const tailExcluded = samples.length - fitSamples.length;
  const post = fitSamples.filter((s) => s.elapsed_s >= warmup && Number.isFinite(s.host.rss_mb));
  const rssSeries = post.length >= 3 ? post : fitSamples.filter((s) => Number.isFinite(s.host.rss_mb));
  const rssPts = rssSeries.map((s) => [s.elapsed_s / 60, s.host.rss_mb]);
  const rssAll = samples.map((s) => s.host.rss_mb).filter(Number.isFinite);
  const cpuAll = samples.map((s) => s.host.cpu_pct).filter(Number.isFinite);
  const stepsMeans = samples.map((s) => s.host.stepsPerS?.mean).filter(Number.isFinite);
  const stepsMin = samples.map((s) => s.host.stepsPerS?.min).filter(Number.isFinite);
  const stepsMax = samples.map((s) => s.host.stepsPerS?.max).filter(Number.isFinite);
  const dropped = Math.max(0, ...samples.map((s) => s.host.dropped ?? 0));
  const hostSummary = {
    samples: samples.length,
    interval_s: samples.length > 1 ? round((samples[samples.length - 1].elapsed_s - samples[0].elapsed_s) / (samples.length - 1), 1) : null,
    elapsed_s: elapsedMax,
    warmup_s: round(warmup, 0),
    cpuPct: cpuAll.length ? { mean: round(mean(cpuAll), 1), max: round(Math.max(...cpuAll), 1) } : null,
    rssMb: rssAll.length ? { first: rssAll[0], last: rssAll[rssAll.length - 1], min: Math.min(...rssAll), max: Math.max(...rssAll) } : null,
    rssPostWarmup: rssSeries.length
      ? { first: rssSeries[0].host.rss_mb, last: rssSeries[rssSeries.length - 1].host.rss_mb, growthMb: round(rssSeries[rssSeries.length - 1].host.rss_mb - rssSeries[0].host.rss_mb, 1), slopeMbPerMin: round(slope(rssPts), 3), samples: rssSeries.length,
          // 收尾樣本(elapsed_s > 設定時長或 clients.alive=false)數:不入擬合
          tailExcluded,
          // 後半段斜率(參考值,不作判定):.NET GC 堆積在前幾分鐘常呈階梯式擴張,後半段仍上升才較可能是洩漏;短於 30 分鐘的執行請看 2 小時結果
          secondHalfSlopeMbPerMin: round(slope(rssPts.slice(Math.floor(rssPts.length / 2))), 3) }
      : null,
    threads: samples.length ? { first: samples[0].host.threads ?? null, last: samples[samples.length - 1].host.threads ?? null } : null,
    stepsPerS: stepsMeans.length ? { mean: round(mean(stepsMeans), 2), min: Math.min(...stepsMin), max: Math.max(...stepsMax), lines: samples.reduce((a, s) => a + (s.host.stepsPerS?.n ?? 0), 0) } : null,
    dropped,
    aliveAtEnd: samples.length ? samples[samples.length - 1].host.alive !== false : false,
    radar: (() => {
      const r = samples.map((s) => s.radar).filter((x) => x && Number.isFinite(x.rss_mb));
      if (!r.length) return null;
      const cpu = r.map((x) => x.cpu_pct).filter(Number.isFinite);
      return { cpuPct: cpu.length ? { mean: round(mean(cpu), 1), max: round(Math.max(...cpu), 1) } : null, rssMb: { first: r[0].rss_mb, last: r[r.length - 1].rss_mb, max: Math.max(...r.map((x) => x.rss_mb)) } };
    })(),
  };

  // ---- 客戶端 ----
  const clientSummaries = run.clients.filter((r) => r.kind === 'clientSummary');
  const perSecond = run.clients.filter((r) => r.kind === 'client');
  const merged = new MsHistogram();
  for (const c of clientSummaries) merged.merge(MsHistogram.fromJSON(c.latencyMs));
  const hzSeries = perSecond.map((r) => r.state?.frames).filter(Number.isFinite).sort((a, b) => a - b);
  const tickRates = clientSummaries.map((c) => c.tickRate).filter(Number.isFinite);
  const clientsSummary = {
    count: clientSummaries.length,
    configured: meta.clients ?? null,
    stateFrames: clientSummaries.reduce((a, c) => a + (c.stateFrames ?? 0), 0),
    eventFrames: clientSummaries.reduce((a, c) => a + (c.eventFrames ?? 0), 0),
    frozenFrames: clientSummaries.reduce((a, c) => a + (c.frozenFrames ?? 0), 0),
    stateHz: hzSeries.length ? { p50: quantileOf(hzSeries, 0.5), min: hzSeries[0], max: hzSeries[hzSeries.length - 1] } : null,
    tickRate: tickRates.length ? { mean: round(mean(tickRates), 3), min: Math.min(...tickRates), max: Math.max(...tickRates) } : null,
    latencyMs: merged.count ? { n: merged.count, p50: merged.quantile(0.5), p95: merged.quantile(0.95), p99: merged.quantile(0.99), max: round(merged.max, 1), mean: round(merged.mean, 2) } : null,
    radar: clientSummaries.some((c) => c.radar)
      ? { frames: clientSummaries.reduce((a, c) => a + (c.radar?.frames ?? 0), 0), kBPerS: round(mean(clientSummaries.map((c) => c.radar?.kBPerS).filter(Number.isFinite)), 1), framesPerS: round(mean(clientSummaries.map((c) => c.radar?.framesPerS).filter(Number.isFinite)), 2) }
      : null,
    web: clientSummaries.some((c) => c.web) ? { ok: clientSummaries.reduce((a, c) => a + (c.web?.ok ?? 0), 0), fail: clientSummaries.reduce((a, c) => a + (c.web?.fail ?? 0), 0) } : null,
    disconnects: clientSummaries.reduce((a, c) => a + (c.disconnects ?? 0), 0),
    errors: clientSummaries.reduce((a, c) => a + (c.errors ?? 0), 0),
    lastErrors: clientSummaries.map((c) => c.lastError).filter(Boolean),
    perClient: clientSummaries.map((c) => ({
      id: c.id, stateFrames: c.stateFrames, stateHz: c.stateHz, tickRate: c.tickRate,
      latency: c.latencyMs ? { p50: c.latencyMs.p50, p95: c.latencyMs.p95, p99: c.latencyMs.p99, max: c.latencyMs.max } : null,
      radarFramesPerS: c.radar?.framesPerS ?? null, radarKBPerS: c.radar?.kBPerS ?? null, webFail: c.web?.fail ?? null, disconnects: c.disconnects, errors: c.errors,
    })),
  };

  // ---- 學員 ----
  const stu = run.student.find((r) => r.kind === 'studentSummary') ?? null;
  const unconfirmedActions = run.student.filter((r) => r.kind === 'action' && r.confirmed === false);
  const confirmMs = run.student.filter((r) => r.kind === 'action' && r.confirmed === true).map((r) => r.confirmMs).filter(Number.isFinite).sort((a, b) => a - b);
  const studentSummary = stu
    ? { actions: stu.actions, confirmed: stu.confirmed, unconfirmed: stu.unconfirmed, unverified: stu.unverified, rejected: stu.rejected, disconnects: stu.disconnects, errors: stu.errors, events: stu.events ?? {}, lastError: stu.lastError ?? null, confirmMs: confirmMs.length ? { p50: quantileOf(confirmMs, 0.5), max: confirmMs[confirmMs.length - 1] } : null, unconfirmedList: unconfirmedActions.slice(0, 10).map((a) => `${a.type} ${a.value ?? ''} @${a.elapsed_s}s`) }
    : null;

  // ---- 判定 ----
  const footer = meta.footer ?? /紀錄已關閉/.test(run.simcoreLog);
  const hostOk = hostSummary.aliveAtEnd && !meta.aborted && (meta.hostExit === undefined || meta.hostExit === null || meta.hostExit === 0) && footer;
  const criteria = [];
  const add = (id, name, threshold, measured, pass, note) => criteria.push({ id, name, threshold, measured, pass: !!pass, note: note ?? null });
  const tr = clientsSummary.tickRate;
  add('coreRate', '核心 50 Hz 無掉拍', `掉拍 0;tick 率平均 ${th.coreHz} ± ${th.coreHzTol}、各客戶端 ± ${th.coreHzTol + 0.5}`,
    `掉拍 ${dropped};tick 率 ${tr ? `${tr.mean}(${tr.min}–${tr.max})` : '無資料'};主控程序 steps/s ${hostSummary.stepsPerS ? `${hostSummary.stepsPerS.mean}(${hostSummary.stepsPerS.min}–${hostSummary.stepsPerS.max})` : '無資料'}`,
    dropped === 0 && tr && Math.abs(tr.mean - th.coreHz) <= th.coreHzTol && tr.min >= th.coreHz - th.coreHzTol - 0.5 && tr.max <= th.coreHz + th.coreHzTol + 0.5);
  const lat = clientsSummary.latencyMs;
  add('latency', '狀態幀延遲 < 門檻', `p95 < ${th.maxLatencyMs} ms`, lat ? `p50 ${lat.p50} / p95 ${lat.p95} / p99 ${lat.p99} / max ${lat.max} ms(n = ${lat.n})` : '無資料', lat && lat.p95 < th.maxLatencyMs, '牆鐘估計:含核心序列化、WebSocket 與客戶端事件迴圈(lib.mjs LatencyEstimator)');
  const rp = hostSummary.rssPostWarmup;
  add('rss', 'RSS 不持續成長', `暖機 ${hostSummary.warmup_s} s 後斜率 ≤ ${th.maxRssSlopeMbPerMin} MB/min 且增量 ≤ ${th.maxRssGrowthMb} MB`,
    rp ? `起 ${rp.first} → 訖 ${rp.last} MB(增量 ${rp.growthMb} MB;斜率 ${rp.slopeMbPerMin} MB/min,後半段 ${rp.secondHalfSlopeMbPerMin} MB/min;${rp.samples} 筆${rp.tailExcluded ? `,排除收尾 ${rp.tailExcluded} 筆` : ''})` : '無資料',
    rp && rp.samples >= 2 && Math.abs(rp.slopeMbPerMin ?? Infinity) <= th.maxRssSlopeMbPerMin && rp.growthMb <= th.maxRssGrowthMb);
  add('connections', '無斷線與錯誤', '客戶端斷線 0、錯誤 0、網頁失敗 0',
    `斷線 ${clientsSummary.disconnects}、錯誤 ${clientsSummary.errors}、網頁失敗 ${clientsSummary.web?.fail ?? 0}${clientsSummary.lastErrors.length ? `(${clientsSummary.lastErrors.join(';')})` : ''}`,
    clientsSummary.count > 0 && clientsSummary.disconnects === 0 && clientsSummary.errors === 0 && (clientsSummary.web?.fail ?? 0) === 0);
  add('student', '學員指令皆生效', '未確認 0、commandRejected 0、斷線 0',
    studentSummary ? `動作 ${studentSummary.actions}(確認 ${studentSummary.confirmed}、未確認 ${studentSummary.unconfirmed}、snapshot ${studentSummary.unverified}、拒絕 ${studentSummary.rejected}、斷線 ${studentSummary.disconnects})${studentSummary.unconfirmedList.length ? `:${studentSummary.unconfirmedList.join('、')}` : ''}` : '無資料',
    studentSummary && studentSummary.actions > 0 && studentSummary.unconfirmed === 0 && studentSummary.rejected === 0 && studentSummary.disconnects === 0 && studentSummary.errors === 0);
  add('host', '主控程序存活到結束並寫完紀錄 footer', '存活、結束碼 0、紀錄已關閉', `存活 ${hostSummary.aliveAtEnd ? '是' : '否'}、結束碼 ${meta.hostExit ?? '?'}、footer ${footer ? '是' : '否'}${meta.aborted ? '、測試中途中止' : ''}`, hostOk);
  const pass = criteria.every((c) => c.pass);

  return {
    format: 'simosa-soak-report/1',
    run: meta.run ?? path.basename(run.dir),
    pass,
    thresholds: th,
    meta,
    criteria,
    host: hostSummary,
    clients: clientsSummary,
    student: studentSummary,
    gc: run.counters,
    hostSamples: samples.map((s) => ({ elapsed_s: s.elapsed_s, cpu_pct: s.host.cpu_pct, rss_mb: s.host.rss_mb, threads: s.host.threads ?? null, steps: s.host.stepsPerS?.mean ?? null, dropped: s.host.dropped ?? 0, radarRss: s.radar?.rss_mb ?? null, radarCpu: s.radar?.cpu_pct ?? null })),
  };
}

const f = (x, d = 1) => (x === null || x === undefined ? '—' : typeof x === 'number' ? String(round(x, d)) : String(x));

export function renderMarkdown(s) {
  const m = s.meta;
  const lines = [];
  lines.push(`# 長時間穩定性(soak)測試報告 — ${s.run}`, '');
  lines.push(`> 規劃書第 14 章「效能」列(核心 50 Hz 無掉拍、延遲 < 50 ms、RSS 不持續成長);工具 \`tools/soak-test.sh\`(docs/design/testing.md 第 6 節)。`);
  lines.push(`> 情境 ${m.scenario ?? '?'};合成客戶端 ${m.clients ?? '?'} + 學員 1;設定時長 ${m.minutes ?? '?'} 分鐘(實際 ${f(s.host.elapsed_s, 0)} s);開始 ${m.startedUtc ?? '?'};結束 ${m.endedUtc ?? '?'};` +
    `平台 ${m.platform ?? '?'}(${m.cpuCount ?? '?'} 核;${m.machineNote ?? ''});git ${m.gitRev ? m.gitRev.slice(0, 12) : '?'};.NET ${m.dotnet ?? '?'};Node ${m.node ?? '?'}。`, '');
  lines.push(`## 判定:**${s.pass ? '合格' : '不合格'}**`, '', '| 準則 | 門檻 | 量測 | 結果 |', '|---|---|---|---|');
  for (const c of s.criteria) lines.push(`| ${c.name} | ${c.threshold} | ${c.measured} | ${c.pass ? '通過' : '**不通過**'} |`);
  lines.push('');
  const c = s.clients;
  lines.push('## 狀態幀、延遲與雷達幀(合成客戶端)', '');
  lines.push(`全部客戶端合併:狀態幀 ${c.stateFrames}(事件框 ${c.eventFrames}、凍結幀 ${c.frozenFrames});每秒幀率 p50 ${f(c.stateHz?.p50)}(最小 ${f(c.stateHz?.min)}、最大 ${f(c.stateHz?.max)};核心廣播 25 Hz);` +
    `tick 率平均 ${f(c.tickRate?.mean, 3)}/s;延遲 p50 ${f(c.latencyMs?.p50)} / p95 ${f(c.latencyMs?.p95)} / p99 ${f(c.latencyMs?.p99)} / max ${f(c.latencyMs?.max)} ms(n = ${c.latencyMs?.n ?? 0});` +
    `雷達 ${c.radar ? `${f(c.radar.framesPerS, 2)} 幀/s、${f(c.radar.kBPerS)} KB/s(每客戶端平均)` : '未訂閱'};網頁 GET ${c.web ? `${c.web.ok} 成功 / ${c.web.fail} 失敗` : '未檢查'}。`, '');
  lines.push('| 客戶端 | 狀態幀 | 幀率 Hz | tick 率 /s | 延遲 p50 | p95 | p99 | max (ms) | 雷達幀/s | 雷達 KB/s | 斷線 | 錯誤 |', '|---|---|---|---|---|---|---|---|---|---|---|---|');
  for (const p of c.perClient) lines.push(`| ${p.id} | ${p.stateFrames} | ${f(p.stateHz, 2)} | ${f(p.tickRate, 3)} | ${f(p.latency?.p50)} | ${f(p.latency?.p95)} | ${f(p.latency?.p99)} | ${f(p.latency?.max)} | ${f(p.radarFramesPerS, 2)} | ${f(p.radarKBPerS)} | ${p.disconnects} | ${p.errors} |`);
  lines.push('');
  const h = s.host;
  lines.push(`## 主控程序取樣(每 ${f(h.interval_s, 0)} s,共 ${h.samples} 筆;暖機 ${h.warmup_s} s)`, '', '| 項目 | 起 | 訖 | 最小 | 最大 | 平均 | 備註 |', '|---|---|---|---|---|---|---|');
  lines.push(`| CPU %(程序,可 > 100) | — | — | — | ${f(h.cpuPct?.max)} | ${f(h.cpuPct?.mean)} | utime+stime / 牆鐘 |`);
  lines.push(`| RSS MB | ${f(h.rssMb?.first)} | ${f(h.rssMb?.last)} | ${f(h.rssMb?.min)} | ${f(h.rssMb?.max)} | — | 暖機後增量 ${f(h.rssPostWarmup?.growthMb)} MB、斜率 ${f(h.rssPostWarmup?.slopeMbPerMin, 3)} MB/min(後半段 ${f(h.rssPostWarmup?.secondHalfSlopeMbPerMin, 3)} MB/min,參考)${h.rssPostWarmup?.tailExcluded ? `;收尾 ${h.rssPostWarmup.tailExcluded} 筆不入擬合` : ''} |`);
  lines.push(`| 執行緒 | ${f(h.threads?.first, 0)} | ${f(h.threads?.last, 0)} | — | — | — | |`);
  lines.push(`| steps/s(主控程序每秒輸出) | — | — | ${f(h.stepsPerS?.min, 0)} | ${f(h.stepsPerS?.max, 0)} | ${f(h.stepsPerS?.mean, 2)} | ${h.stepsPerS?.lines ?? 0} 行;每秒值因 1 s 輪詢對齊可 ±2 |`);
  lines.push(`| 掉拍(累計) | 0 | ${h.dropped} | — | — | — | RealtimeRunner.DroppedSteps |`);
  if (h.radar) lines.push(`| 雷達服務 RSS MB / CPU % | ${f(h.radar.rssMb.first)} | ${f(h.radar.rssMb.last)} | — | ${f(h.radar.rssMb.max)} | CPU 平均 ${f(h.radar.cpuPct?.mean)}、最大 ${f(h.radar.cpuPct?.max)} | X+S 兩台 |`);
  lines.push('');
  const st = s.student;
  lines.push('## 學員指令', '');
  if (st) {
    lines.push(`動作 ${st.actions}:確認生效 ${st.confirmed}(生效時間 p50 ${f(st.confirmMs?.p50)} ms、最大 ${f(st.confirmMs?.max)} ms)、未確認 ${st.unconfirmed}、snapshot(無回饋)${st.unverified}、commandRejected ${st.rejected}、斷線 ${st.disconnects}、錯誤 ${st.errors}。` +
      `收到事件:${Object.entries(st.events).map(([k, v]) => `${k} ${v}`).join('、') || '無'}。${st.lastError ? `最後錯誤:${st.lastError}` : ''}`, '');
  } else lines.push('(無學員統計)', '');
  lines.push('## GC(dotnet-counters)', '');
  if (s.gc) lines.push(`取樣 ${s.gc.samples} 筆:GC 堆積 最後 ${f(s.gc.gcHeapMb.last)} MB / 最大 ${f(s.gc.gcHeapMb.max)} MB;Gen0 ${f(s.gc.gen0, 0)}、Gen1 ${f(s.gc.gen1, 0)}、Gen2 ${f(s.gc.gen2, 0)} 次;Working Set 最後 ${f(s.gc.workingSetMb.last)} MB。`, '');
  else lines.push('略過:找不到 dotnet-counters(PATH 或 .cache/dotnet-tools);RSS 由 /proc(Windows:Get-Process)取樣。', '');
  lines.push('## 方法與限制', '');
  lines.push('- 延遲為牆鐘估計(`tests/soak/lib.mjs` LatencyEstimator):核心以牆鐘推進,第 k 幀理想到達時刻 = 原點 + state.t,原點取連線初期最快一幀;量到的是核心序列化 + WebSocket 傳送 + 客戶端事件迴圈的總延遲,核心落後牆鐘也會反映為延遲上升。凍結幀不計;時間倍率須為 1。');
  lines.push('- tick 率以客戶端牆鐘量測(首末狀態幀的 tick 差 / 到達時間差),與主控程序每秒輸出的 `steps/s` 互相印證;「掉拍」取自主控程序輸出的累計值。');
  lines.push('- RSS 斜率為暖機後樣本的最小平方直線;超過設定時長或合成客戶端已結束(clients.alive=false)的收尾樣本不入擬合(客戶端斷線時核心釋放連線常有一筆尖峰;起訖/最大值等原始統計仍含)。.NET GC 的堆積在前幾分鐘常呈階梯式擴張(每次 +數 MB),10 分鐘內的斜率可能只反映這個過程,後半段斜率為參考值;門檻為軟體面判準,是否洩漏以實機 2 小時(同一工具)為準。');
  lines.push('- 合成客戶端在同一個 Node 程序內(N 條狀態連線 + N 條雷達連線),不含瀏覽器繪圖負載;視景幀率、內顯機 CPU/延遲數字需實機(docs/驗收/需求追溯表.md 第 14 章效能列)。');
  lines.push('');
  return lines.join('\n');
}

function main() {
  let opt;
  try {
    opt = parseArgs(process.argv.slice(2), {
      dir: { type: 'string', default: '' },
      md: { type: 'string', default: '' },
      json: { type: 'string', default: '' },
      'core-hz': { type: 'number', default: DEFAULT_THRESHOLDS.coreHz },
      'core-hz-tol': { type: 'number', default: DEFAULT_THRESHOLDS.coreHzTol },
      'max-latency-ms': { type: 'number', default: DEFAULT_THRESHOLDS.maxLatencyMs },
      'max-rss-slope-mb-per-min': { type: 'number', default: DEFAULT_THRESHOLDS.maxRssSlopeMbPerMin },
      'max-rss-growth-mb': { type: 'number', default: DEFAULT_THRESHOLDS.maxRssGrowthMb },
      'warmup-s': { type: 'number', default: DEFAULT_THRESHOLDS.warmupS },
      quiet: { type: 'boolean', default: false },
    });
    if (!opt.dir) throw new Error('--dir 為必要參數');
  } catch (err) {
    console.error(err.message);
    return 2;
  }
  let run;
  try { run = loadRun(opt.dir); } catch (err) { console.error(err.message); return 2; }
  const s = summarise(run, { coreHz: opt['core-hz'], coreHzTol: opt['core-hz-tol'], maxLatencyMs: opt['max-latency-ms'], maxRssSlopeMbPerMin: opt['max-rss-slope-mb-per-min'], maxRssGrowthMb: opt['max-rss-growth-mb'], warmupS: opt['warmup-s'] });
  const md = renderMarkdown(s);
  if (opt.md) { fs.mkdirSync(path.dirname(opt.md), { recursive: true }); fs.writeFileSync(opt.md, md); }
  if (opt.json) { fs.mkdirSync(path.dirname(opt.json), { recursive: true }); fs.writeFileSync(opt.json, JSON.stringify(s, null, 2)); }
  if (!opt.quiet) {
    console.log(`soak ${s.run}:${s.pass ? '合格' : '不合格'}`);
    for (const c of s.criteria) console.log(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}:${c.measured}`);
    if (opt.md) console.log(`  報告 ${opt.md}${opt.json ? `、${opt.json}` : ''}`);
  }
  return s.pass ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exit(main());
