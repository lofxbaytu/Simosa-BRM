#!/usr/bin/env node
// 長時間穩定性(soak)測試報告(tools/soak-test.sh / .ps1 於結束後呼叫;docs/design/testing.md 第 6 節):
// 讀一次執行目錄 build/soak/<run>/ 的 meta.json、stats/host.jsonl(每 10 s 的主控程序 CPU%/RSS/steps/s/掉拍)、stats/clients.jsonl(合成客戶端每秒統計與
// 整場延遲直方圖)、stats/student.jsonl(學員指令確認)、選配 stats/counters.csv(dotnet-counters),寫 Markdown 與 JSON 報告並依規劃書第 14 章「效能」列判定:
//   1) 核心 50 Hz 無掉拍:主控程序「掉拍」計數為 0,且客戶端以牆鐘量到的 tick 率在 50 ± --core-hz-tol;
//   2) 延遲 < --max-latency-ms(預設 50):全部客戶端合併的狀態幀延遲 p95(最大值另列);
//   3) RSS 不持續成長(docs/design/testing.md 第 6.3、6.6 節;斜率門檻 --max-rss-slope-mb-per-min、增量門檻 --max-rss-growth-mb):
//      有 stats/counters.csv(dotnet-counters)時以 GC 計數器判定,下列全部成立才合格——(a) 擬合時段內 GC 次數 ≥ 3(不足 = 無法判定,不合格並註明);
//      (b) 第一次 GC 之後受控堆積(上次 GC 後 gen1+gen2+LOH+POH)全段斜率;(c) 暖機後 GC 已提交量全段斜率;
//      (d) 穩定段(max(暖機, --steady-from-s 720) 起、至少 --min-steady-s 600 s)「RSS − GC 已提交量」(非 GC 記憶體)斜率;(e) 穩定段 RSS 總斜率,
//      四者皆 ≤ 斜率門檻((b)(c) 的擬合時段至少 --min-slope-span-s 300 s);(f) 暖機後 RSS 增量 ≤ 增量門檻。任一項不通過 = 不通過;
//      沒有不通過但有資料不足的項目(穩定段不夠長等;最短可判定的執行約 22 分鐘)= 無法判定。
//      暖機為 --warmup-s(短於 10 分鐘的執行取總時長 × 20%),有 counters 時至少延到第一次 GC 之後一個取樣間隔(gen0 預算填充不算成長)。
//      沒有 counters(或缺必要的計數器)時:暖機後 RSS 斜率 ≤ 斜率門檻且增量 ≤ 增量門檻,並註明無法分辨 gen0 預算填充;
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
  // 有 dotnet-counters 時 RSS 準則至少要的 GC 次數(第一次 GC 之後至少再兩次,受控堆積才有斜率;不足 → 無法判定)
  minGcCount: 3,
  // 有 dotnet-counters 時「RSS − GC 已提交量」與 RSS 總斜率的穩定段起點(秒;實際取 max(暖機, 本值)):前約 12 分鐘有一次性的非 GC 階梯
  // (學員腳本 6 種故障每 120 s 輪到一種新的、最後一種約 660 s;情境事件;新程式路徑第一次執行的 JIT 與組件載入),全段斜率在 10–12 分鐘的執行分不出有沒有洩漏
  // (第三十輪審查 S-F1:修正後 1.443、修正前 1.241 MB/min)。docs/design/testing.md 第 6.6 節
  steadyFromS: 720,
  // 穩定段至少要涵蓋的秒數(不足 → 這兩項無法判定):第 12 分鐘之後仍偶有數 MB 的一次性階梯(r30-fix-default-25 約 860–930 s +4 MB),
  // 一次 4 MB 的階梯在 W 分鐘的視窗中最多讓斜率 1.5 × 4 / W MB/min — 5 分鐘 1.2、10 分鐘 0.6;最短可判定的執行 = 720 + 600 s = 22 分鐘
  minSteadyS: 600,
  // 受控堆積與 GC 已提交量斜率的擬合時段至少要涵蓋的秒數(不足 → 該項無法判定;前一兩分鐘 GC 已提交量有階梯,2 分鐘的執行斜率可達 3 MB/min)
  minSlopeSpanS: 300,
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

const MIB = 1048576;

/** dotnet-counters 的計數器名稱:完全相同,或後接「 (單位[ / 區間])」——dotnet-counters 10.x 的名稱帶單位與區間(「Gen 0 GC Count (Count / 10 sec)」「Gen 0 Size (B)」) */
export function findCounter(names, base) {
  return names.find((k) => k === base || k.startsWith(`${base} (`)) ?? null;
}

/** 大小類計數器 → MiB(與 RSS 同單位):名稱單位 (MB) 是 10^6 B(System.Runtime 的 EventCounters 以 1 000 000 除)、(B) 是位元組;名稱沒有單位時依 defaultUnit */
function toMiB(v, key, defaultUnit) {
  const unit = /\(([^()]*)\)$/.exec(key)?.[1]?.split('/')[0].trim() ?? defaultUnit;
  return unit === 'MB' ? (v * 1e6) / MIB : unit === 'B' ? v / MIB : v;
}

/** RSS 準則用 counters 判定時必要的計數器(缺任何一個 → 退回只看 RSS 的規則並註明) */
export const REQUIRED_COUNTERS = ['Gen 0 GC Count', 'GC Committed Bytes', 'Gen 2 Size'];

/**
 * dotnet-counters csv 時間戳的時刻(當日秒數);解析不了回 null。只取「時:分:秒」與上午/下午標記(日期部分的格式隨地區設定而異:
 * 「11/22/2026 17:59:30」「2026/11/22 下午 05:59:30」「22.11.2026 17:59:30」),跨午夜由呼叫端展開。
 */
export function parseCounterTimeOfDay(ts) {
  const m = /(\d{1,2}):(\d{2}):(\d{2})/.exec(ts ?? '');
  if (!m) return null;
  let h = Number(m[1]);
  const pm = /PM|下午|午後/i.test(ts);
  const am = /AM|上午|午前/i.test(ts);
  if (pm && h < 12) h += 12;
  if (am && h === 12) h = 0;
  if (h > 23 || Number(m[2]) > 59 || Number(m[3]) > 59) return null;
  return h * 3600 + Number(m[2]) * 60 + Number(m[3]);
}

/**
 * csv 列 → 刷新分組:[{ ts: 該次刷新的秒數(時間戳可解析時)或 null, rows }]。
 * 時間戳都解析得了:依時間排序(穩定排序,同一時刻保留檔案順序),與該組第一列相差 ≥ intervalS / 2 或同一名稱再出現即下一次刷新——
 * dotnet-counters 偶爾把第一次刷新拆成兩段、後半段寫在第二次刷新之後(第三十輪審查 S-F3:r30-fix-default 的第一次刷新先寫 3 列、接著第二次刷新整批、
 * 再寫第一次刷新的其餘各列),只靠名稱分組會讓之後每次刷新都晚一個間隔;一次刷新也可能跨秒(差 1 s 仍是同一組)。
 * 有任一列解析不了:退回檔案順序、同一名稱再出現即下一次刷新。
 */
export function groupCounterRows(rows, intervalS) {
  const tods = rows.map((r) => parseCounterTimeOfDay(r[0]));
  if (rows.length && tods.every((x) => x !== null)) {
    // 跨午夜展開:比目前最大值小 12 h 以上 → 下一天;大 12 h 以上 → 前一天的遲到列
    let day = 0;
    let max = null;
    const abs = tods.map((tod) => {
      let a = tod + day;
      if (max !== null && a < max - 43200) { day += 86400; a += 86400; }
      else if (max !== null && a > max + 43200) a -= 86400;
      max = max === null ? a : Math.max(max, a);
      return a;
    });
    const order = rows.map((r, k) => ({ r, ts: abs[k] })).sort((a, b) => a.ts - b.ts);
    const groups = [];
    for (const { r, ts } of order) {
      const g = groups[groups.length - 1];
      const name = r[2].trim();
      if (!g || ts - g.ts >= intervalS / 2 || g.names.has(name)) groups.push({ ts, rows: [r], names: new Set([name]) });
      else { g.rows.push(r); g.names.add(name); }
    }
    return groups;
  }
  const groups = [];
  for (const r of rows) {
    const g = groups[groups.length - 1];
    const name = r[2].trim();
    if (!g || g.names.has(name)) groups.push({ ts: null, rows: [r], names: new Set([name]) });
    else { g.rows.push(r); g.names.add(name); }
  }
  return groups;
}

/**
 * 讀 dotnet-counters 的 csv(Timestamp,Provider,Counter Name,Counter Type,Mean/Increment);不存在回 null。
 * 一次刷新列出每個計數器各一次(分組見 groupCounterRows:時間戳可解析時依時間,否則依「同一名稱再出現」)。
 * 時間軸:第一次刷新 = intervalS 秒(自 dotnet-counters 開始;soak 腳本在客戶端啟動前一刻開始收),之後依時間戳的實際間隔(時間戳解析不了時第 i 次 = (i + 1) × intervalS);
 * 與取樣的 elapsed_s 通常差 1 s 以內。
 * 大小一律換算成 MiB;Rate 類(Count / 10 sec、B / 10 sec、ms / 10 sec)是該次刷新區間內的增量。
 */
export function readCounters(file, intervalS = 10) {
  if (!fs.existsSync(file)) return null;
  const rows = fs.readFileSync(file, 'utf8').split('\n').slice(1).map((l) => l.split(',')).filter((r) => r.length >= 5);
  const groups = groupCounterRows(rows, intervalS);
  const t0 = groups[0]?.ts ?? null;
  const groupT = groups.map((g, i) => (t0 !== null ? intervalS + (g.ts - t0) : (i + 1) * intervalS));
  const by = new Map();
  groups.forEach((g, i) => {
    for (const r of g.rows) {
      const name = r[2].trim();
      const v = Number(r[4]);
      if (!Number.isFinite(v)) continue;
      if (!by.has(name)) by.set(name, []);
      by.get(name).push({ i, v });
    }
  });
  const refreshes = groups.length;
  const names = [...by.keys()];
  /** 逐刷新的值陣列(缺的刷新為 null);conv(v, key) 換算單位;找不到計數器回 null */
  const series = (base, conv = (v) => v) => {
    const key = findCounter(names, base);
    if (!key) return null;
    const a = new Array(refreshes).fill(null);
    for (const { i, v } of by.get(key)) a[i] = conv(v, key);
    return a;
  };
  const mbUnit = (v, k) => toMiB(v, k, 'MB');
  const bUnit = (v, k) => toMiB(v, k, 'B');
  const gcCount = series('Gen 0 GC Count');   // 每次 GC(任何代)都讓 gen0 計數加 1 → 總 GC 次數
  const gen1Count = series('Gen 1 GC Count');
  const gen2Count = series('Gen 2 GC Count');
  const committed = series('GC Committed Bytes', mbUnit);
  const budget = series('Gen 0 GC Budget', mbUnit);
  const heap = series('GC Heap Size', mbUnit);
  const ws = series('Working Set', mbUnit);
  const g0 = series('Gen 0 Size', bUnit);
  const g1 = series('Gen 1 Size', bUnit);
  const g2 = series('Gen 2 Size', bUnit);
  const loh = series('LOH Size', bUnit);
  const poh = series('POH (Pinned Object Heap) Size', bUnit);
  const alloc = series('Allocation Rate', bUnit);
  const pause = series('Time paused by GC');
  const gcPct = series('% Time in GC since last GC');
  const exc = series('Exception Count');
  const vals = (a) => (a ? a.filter((x) => x !== null) : []);
  const sum = (a) => (a ? vals(a).reduce((x, y) => x + y, 0) : null);
  const last = (a) => { const v = vals(a); return v.length ? round(v[v.length - 1], 2) : null; };
  const first = (a) => { const v = vals(a); return v.length ? round(v[0], 2) : null; };
  const max = (a) => { const v = vals(a); return v.length ? round(Math.max(...v), 2) : null; };
  const firstGcIdx = gcCount ? gcCount.findIndex((x) => x > 0) : -1;
  const timeline = [];
  for (let i = 0; i < refreshes; i++) {
    const managed = g2 && g2[i] !== null ? (g1?.[i] ?? 0) + g2[i] + (loh?.[i] ?? 0) + (poh?.[i] ?? 0) : null;
    timeline.push({ t: groupT[i], gc: gcCount?.[i] ?? null, committedMb: committed?.[i] === null || committed?.[i] === undefined ? null : round(committed[i], 3),
      managedMb: managed === null ? null : round(managed, 3), budgetMb: budget?.[i] === null || budget?.[i] === undefined ? null : round(budget[i], 2), pauseMs: pause?.[i] ?? null });
  }
  const committedPos = committed ? committed.filter((x) => x !== null && x > 0) : [];
  return {
    samples: rows.length,
    refreshes,
    intervalS,
    // 刷新分組依據:'timestamp'(時間戳)或 'name'(時間戳解析不了,依名稱重複)
    grouping: groups.length && groups[0].ts !== null ? 'timestamp' : 'name',
    // RSS 準則需要但 csv 沒有的計數器(非空 → 以只看 RSS 的規則判定)
    missing: REQUIRED_COUNTERS.filter((b) => !findCounter(names, b)),
    gen0: sum(gcCount), gen1: sum(gen1Count), gen2: sum(gen2Count),
    // 第一次 GC 所在刷新的時刻(秒,刷新邊界的上界);null = 無 Gen 0 GC Count,Infinity = 整段沒有 GC(gen0 預算未用完)
    firstGcS: gcCount ? (firstGcIdx >= 0 ? groupT[firstGcIdx] : Infinity) : null,
    gen0BudgetMb: budget ? { first: first(budget), last: last(budget), min: round(Math.min(...vals(budget)), 2), max: max(budget) } : null,
    // GC 已提交量(第一次 GC 前計數器為 0,不列入首值)
    committedMb: committed ? { first: committedPos.length ? round(committedPos[0], 2) : null, last: last(committed), max: max(committed) } : null,
    gcHeapMb: { last: last(heap), max: max(heap) },
    workingSetMb: { last: last(ws), max: max(ws) },
    // 上一次 GC 後各代大小(gen0 GC 前為 0)
    afterGcMb: { gen0: last(g0), gen1: last(g1), gen2: last(g2), loh: last(loh), poh: last(poh) },
    allocMb: alloc ? round(sum(alloc), 1) : null,
    gcPauseMs: pause ? round(sum(pause), 1) : null,
    gcPauseMaxMsPerRefresh: pause ? max(pause) : null,
    gcTimePctMax: gcPct ? max(gcPct) : null,
    exceptions: exc ? sum(exc) : null,
    counters: names,
    // 逐刷新:t 秒、該次刷新的 GC 次數、GC 已提交量、受控堆積(gen1+gen2+LOH+POH)、gen0 預算(MiB)、GC 暫停 ms
    timeline,
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
    counters: readCounters(path.join(stats, 'counters.csv'), Number(meta.sample_s) > 0 ? Number(meta.sample_s) : 10),
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
const fx = (x, d = 1) => (x === null || x === undefined ? '—' : String(round(x, d)));

/** [分鐘, MB] 點列的摘要:首、末、筆數、全段最小平方斜率(MB/min) */
function seriesStats(pts) {
  return { first: pts.length ? round(pts[0][1], 2) : null, last: pts.length ? round(pts[pts.length - 1][1], 2) : null, samples: pts.length, slopeMbPerMin: pts.length >= 2 ? round(slope(pts), 3) : null };
}

/**
 * RSS 準則(docs/design/testing.md 第 6.3、6.6 節)。
 * 有可用的 counters(REQUIRED_COUNTERS 都在):逐項判定,全部通過才合格——
 *   (1) 擬合時段內 GC ≥ minGcCount 次;不足(或整段沒有 GC)= 整個準則無法判定(gen0 預算填充與洩漏分不開);
 *   (2) 第一次 GC 起受控堆積(gen1+gen2+LOH+POH)全段斜率;(3) 暖機後 GC 已提交量全段斜率;
 *   (4) 穩定段(max(暖機, steadyFromS) 起)RSS − GC 已提交量的斜率;(5) 穩定段 RSS 總斜率;(2)–(5) 皆 ≤ 斜率門檻;(6) 暖機後 RSS 增量 ≤ 增量門檻。
 *   每項為 pass / fail / undetermined(資料不足:受控堆積或已提交量不到 3 筆、穩定段不到 minSteadyS 秒);
 *   有任何一項 fail → 不通過;沒有 fail 但有 undetermined → 無法判定(不合格並註明原因);全部 pass → 通過。
 * 理由:RSS = GC 已提交量 + 非 GC 記憶體;洩漏的存活物件反映在受控堆積(gen0 預算大時 RSS 可能長時間不動),GC 自己多提交(碎片)反映在已提交量,
 * 原生記憶體 / 執行緒反映在 RSS − 已提交量;RSS 總斜率防各分量各自略低於門檻、合計約兩倍門檻的分攤洩漏(第三十輪審查 S-F2)。
 * 受控堆積與已提交量從頭判(真的洩漏在 10 分鐘內就判得出來:第三十輪懷疑者的實驗 B、審查的 r30-v-prefix);
 * 非 GC 與 RSS 總斜率只看穩定段:前約 12 分鐘的一次性階梯讓全段斜率分不出有沒有洩漏(審查 S-F1)。
 * 沒有可用的 counters:暖機後 RSS 全段斜率 ≤ 門檻且增量 ≤ 門檻,並註明無法分辨 gen0 預算填充。
 */
function evalRss({ th, ctr, ctrUsable, firstGcS, warmup, baseWarmup, rssSeries, rssPts, winEndS }) {
  const rpSlope = rssPts.length >= 2 ? slope(rssPts) : null;
  const growth = rssSeries.length ? rssSeries[rssSeries.length - 1].host.rss_mb - rssSeries[0].host.rss_mb : null;
  const growthOk = growth !== null && growth <= th.maxRssGrowthMb;
  const notes = [];
  if (!ctrUsable) {
    notes.push(ctr
      ? `counters.csv 缺少 ${ctr.missing.join('、') || '刷新資料'}(或計數器名稱不同):以只看 RSS 的規則判定,無法分辨 gen0 預算填充`
      : '無 dotnet-counters:無法分辨 gen0 預算填充(第一次 GC 前 RSS 依配置速率上升;大快取機器的 gen0 預算可達數百 MB),以只看 RSS 的規則判定');
    const slopeOk = rssSeries.length >= 2 && Math.abs(rpSlope ?? Infinity) <= th.maxRssSlopeMbPerMin;
    if (!slopeOk) notes.push(`RSS 斜率 ${fx(rpSlope, 3)} MB/min 超過門檻`);
    if (!growthOk) notes.push(`RSS 增量 ${fx(growth)} MB 超過門檻`);
    return { rule: 'rss-only', pass: slopeOk && growthOk, undetermined: false, warmupS: round(warmup, 0), baseWarmupS: round(baseWarmup, 0), notes };
  }
  const tl = ctr.timeline.filter((x) => x.t <= winEndS);
  const gcCount = tl.reduce((a, x) => a + (x.gc ?? 0), 0);
  const managedPts = Number.isFinite(firstGcS) ? tl.filter((x) => x.t >= firstGcS && x.managedMb !== null).map((x) => [x.t / 60, x.managedMb]) : [];
  const committedPts = tl.filter((x) => x.t >= warmup && x.committedMb > 0).map((x) => [x.t / 60, x.committedMb]);
  // 穩定段:max(暖機, steadyFromS) 起的 RSS 取樣(一次性的非 GC 階梯之後);涵蓋不到 minSteadyS 秒 → 非 GC 與 RSS 總斜率無法判定
  const steadyFromS = Math.max(warmup, th.steadyFromS);
  const steady = rssSeries.filter((s) => s.elapsed_s >= steadyFromS);
  const steadySpanS = steady.length >= 2 ? steady[steady.length - 1].elapsed_s - steady[0].elapsed_s : 0;
  const steadyOk = steady.length >= 3 && steadySpanS >= th.minSteadyS;
  // RSS − GC 已提交量:每筆穩定段的 RSS 取樣配當時(之前最近一次刷新)的已提交量;已提交量為 0(第一次 GC 前)的不配
  const nonGcPts = [];
  let j = -1;
  for (const s of steady) {
    while (j + 1 < tl.length && tl[j + 1].t <= s.elapsed_s + 1) j++;
    const c = j >= 0 ? tl[j].committedMb : null;
    if (c > 0) nonGcPts.push([s.elapsed_s / 60, s.host.rss_mb - c]);
  }
  const managed = seriesStats(managedPts);
  const committed = seriesStats(committedPts);
  const nonGc = seriesStats(nonGcPts);
  const rssSteady = seriesStats(steady.map((s) => [s.elapsed_s / 60, s.host.rss_mb]));
  /** [分鐘, MB] 點列的時段(秒)不足 minSlopeSpanS 或不到 3 筆 → 資料不足的原因;足夠 → null */
  const spanWhy = (pts, what) => {
    const span = pts.length >= 2 ? (pts[pts.length - 1][0] - pts[0][0]) * 60 : 0;
    return pts.length < 3 || span < th.minSlopeSpanS ? `${what}只有 ${pts.length} 筆、${round(span, 0)} s(需 ≥ 3 筆且 ≥ ${th.minSlopeSpanS} s)` : null;
  };
  // 整個準則無法判定:GC 次數不足(第一次 GC 前 RSS 依配置速率上升,洩漏與 gen0 預算填充分不開)
  const reasons = [];
  if (!Number.isFinite(firstGcS)) reasons.push(`整段未發生 GC(gen0 預算 ${fx(ctr.gen0BudgetMb?.max)} MB 尚未用完)`);
  else if (gcCount < th.minGcCount) reasons.push(`擬合時段內只 GC ${gcCount} 次(< ${th.minGcCount})`);
  const gcShort = reasons.length > 0;
  const within = (v) => v !== null && Math.abs(v) <= th.maxRssSlopeMbPerMin;
  const steadyWhy = `穩定段(${round(steadyFromS, 0)} s 起)只有 ${round(steadySpanS, 0)} s / ${steady.length} 筆(需 ≥ ${th.minSteadyS} s)`;
  const slopeLimit = `≤ ${th.maxRssSlopeMbPerMin} MB/min`;
  /** 逐項:status = pass / fail / undetermined(why = 資料不足的原因);GC 次數不足時全部無法判定 */
  const check = (id, name, value, limit, ok, why = null) => {
    const status = gcShort ? 'undetermined' : why ? 'undetermined' : ok ? 'pass' : 'fail';
    return { id, name, value, limit, status, pass: status === 'pass', why: gcShort && id !== 'gcCount' ? null : why };
  };
  const checks = [
    check('gcCount', 'GC 次數', gcCount, `≥ ${th.minGcCount}`, gcCount >= th.minGcCount, gcShort ? reasons.join(';') : null),
    check('managed', '受控堆積斜率(第一次 GC 起)', managed.slopeMbPerMin, slopeLimit, within(managed.slopeMbPerMin), spanWhy(managedPts, '第一次 GC 之後受控堆積')),
    check('committed', 'GC 已提交量斜率(暖機後)', committed.slopeMbPerMin, slopeLimit, within(committed.slopeMbPerMin), spanWhy(committedPts, '暖機後 GC 已提交量')),
    check('nonGc', `RSS − GC 已提交量斜率(穩定段 ${round(steadyFromS, 0)} s 起)`, nonGc.slopeMbPerMin, slopeLimit, within(nonGc.slopeMbPerMin),
      !steadyOk ? steadyWhy : nonGc.samples < 3 ? `穩定段能與 GC 已提交量配對的 RSS 取樣只有 ${nonGc.samples} 筆` : null),
    check('rss', `RSS 斜率(穩定段 ${round(steadyFromS, 0)} s 起)`, rssSteady.slopeMbPerMin, slopeLimit, within(rssSteady.slopeMbPerMin), !steadyOk ? steadyWhy : null),
    check('growth', 'RSS 增量(暖機後)', growth === null ? null : round(growth, 1), `≤ ${th.maxRssGrowthMb} MB`, growthOk),
  ];
  const failed = checks.filter((c) => c.status === 'fail');
  const pending = checks.filter((c) => c.status === 'undetermined');
  // 有任何一項確定不通過 → 不通過(不是「無法判定」);沒有不通過但有資料不足的項目 → 無法判定
  const undetermined = failed.length === 0 && pending.length > 0;
  if (gcShort) notes.push(`無法判定(不合格):${reasons.join(';')}——延長執行,或以 --gen0-budget-mb(DOTNET_GCgen0size)降低 gen0 預算後重跑`);
  else {
    const steadyPending = pending.filter((c) => c.id === 'nonGc' || c.id === 'rss');
    const otherPending = pending.filter((c) => c.id !== 'nonGc' && c.id !== 'rss');
    const why = [...otherPending.map((c) => `${c.name}:${c.why}`)];
    if (steadyPending.length) why.push(`${steadyPending.map((c) => c.id === 'nonGc' ? 'RSS − GC 已提交量' : 'RSS 總斜率').join('、')}:${steadyWhy}——前約 12 分鐘有一次性的非 GC 階梯(學員腳本第一輪故障注入、情境事件、新程式路徑第一次執行),` +
      `短於約 ${round((steadyFromS + th.minSteadyS) / 60, 0)} 分鐘的執行不足以判定,請以 --minutes 25 以上重跑`);
    if (why.length) notes.push(`${failed.length ? '另有無法判定的項目' : '無法判定(不合格)'}:${why.join(';')}`);
  }
  for (const c of failed) notes.push(`${c.name} ${c.value ?? '—'} 未達 ${c.limit}`);
  if (warmup > baseWarmup) notes.push(`暖機延到第一次 GC(counters 第 ${firstGcS} s 內)之後;--warmup-s 為 ${round(baseWarmup, 0)} s`);
  if (failed.some((c) => c.id === 'managed')) notes.push('受控堆積上升 = 存活物件累積:以 dotnet-gcdump 相隔 10 分鐘以上取兩次比較型別數量');
  return {
    rule: 'gc-counters', pass: failed.length === 0 && pending.length === 0, undetermined, reasons: gcShort ? reasons : pending.map((c) => `${c.name}:${c.why}`),
    warmupS: round(warmup, 0), baseWarmupS: round(baseWarmup, 0), firstGcS: Number.isFinite(firstGcS) ? firstGcS : null, noGc: firstGcS === Infinity,
    steadyFromS: round(steadyFromS, 0), steadySpanS: round(steadySpanS, 0),
    gcCount, managed, committed, nonGc, rssSteady, rssSlopeMbPerMin: round(rpSlope, 3), checks, notes,
  };
}

export function summarise(run, thresholds = {}) {
  const th = { ...DEFAULT_THRESHOLDS, ...thresholds };
  const { meta } = run;

  // ---- 主控程序取樣 ----
  const samples = run.host.filter((s) => s.kind === 'sample' && s.host);
  const elapsedMax = samples.length ? samples[samples.length - 1].elapsed_s : 0;
  // 暖機:--warmup-s(短於 10 分鐘的執行取總時長 × 20%);有可用的 dotnet-counters 時至少延到第一次 GC 之後一個取樣間隔——
  // .NET 工作站 GC 第一次 GC 前 RSS 依配置速率上升到 gen0 預算(大快取機器可達數百 MB),之後重用同一批已提交的記憶體,這段不是洩漏
  // (docs/design/testing.md 第 6.6 節)。延長暖機不會掩蓋洩漏:受控堆積的斜率從第一次 GC 起算,另有 GC 次數下限
  const ctr = run.counters;
  const ctrUsable = !!ctr && ctr.missing.length === 0 && ctr.refreshes > 0;
  const ctrInterval = ctr?.intervalS ?? 10;
  const baseWarmup = Math.min(th.warmupS, elapsedMax * 0.2);
  const firstGcS = ctrUsable ? ctr.firstGcS : null;
  const warmup = ctrUsable && Number.isFinite(firstGcS) ? Math.max(baseWarmup, firstGcS + ctrInterval) : baseWarmup;
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
  const rssEval = evalRss({ th, ctr, ctrUsable, firstGcS, warmup, baseWarmup, rssSeries, rssPts, winEndS: (fitSamples.length ? fitSamples[fitSamples.length - 1].elapsed_s : elapsedMax) + ctrInterval / 2 });
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
  const rssMeasured = rp ? `RSS 起 ${rp.first} → 訖 ${rp.last} MB(增量 ${rp.growthMb} MB;斜率 ${rp.slopeMbPerMin} MB/min,後半段 ${rp.secondHalfSlopeMbPerMin} MB/min;${rp.samples} 筆${rp.tailExcluded ? `,排除收尾 ${rp.tailExcluded} 筆` : ''})` : 'RSS 無資料';
  if (rssEval.rule === 'gc-counters') {
    const c = Object.fromEntries(rssEval.checks.map((x) => [x.id, x]));
    add('rss', 'RSS 不持續成長',
      `有 dotnet-counters:GC ≥ ${th.minGcCount} 次;第一次 GC 後受控堆積、暖機 ${hostSummary.warmup_s} s 後 GC 已提交量,以及穩定段(${rssEval.steadyFromS} s 起、至少 ${th.minSteadyS} s)RSS − 已提交量與 RSS 的斜率皆 ≤ ${th.maxRssSlopeMbPerMin} MB/min;` +
        `暖機後 RSS 增量 ≤ ${th.maxRssGrowthMb} MB(全部成立;資料不足的項目 = 無法判定)`,
      `GC ${rssEval.gcCount} 次;受控堆積 ${fx(rssEval.managed.first)} → ${fx(rssEval.managed.last)} MB(${fx(c.managed.value, 3)} MB/min);GC 已提交 ${fx(rssEval.committed.first)} → ${fx(rssEval.committed.last)} MB(${fx(c.committed.value, 3)} MB/min);` +
        `穩定段 RSS − 已提交 ${fx(rssEval.nonGc.first)} → ${fx(rssEval.nonGc.last)} MB(${fx(c.nonGc.value, 3)} MB/min)、RSS ${fx(rssEval.rssSteady.first)} → ${fx(rssEval.rssSteady.last)} MB(${fx(c.rss.value, 3)} MB/min;${rssEval.steadySpanS} s);${rssMeasured}`,
      rssEval.pass, rssEval.notes.join(';') || null);
  } else {
    add('rss', 'RSS 不持續成長', `暖機 ${hostSummary.warmup_s} s 後斜率 ≤ ${th.maxRssSlopeMbPerMin} MB/min 且增量 ≤ ${th.maxRssGrowthMb} MB(無 dotnet-counters 的規則)`,
      rssMeasured, rssEval.pass, rssEval.notes.join(';') || null);
  }
  Object.assign(criteria[criteria.length - 1], { rule: rssEval.rule, undetermined: rssEval.undetermined });
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
    rssEval,
    gc: run.counters,
    hostSamples: samples.map((s) => ({ elapsed_s: s.elapsed_s, cpu_pct: s.host.cpu_pct, rss_mb: s.host.rss_mb, threads: s.host.threads ?? null, steps: s.host.stepsPerS?.mean ?? null, dropped: s.host.dropped ?? 0, radarRss: s.radar?.rss_mb ?? null, radarCpu: s.radar?.cpu_pct ?? null })),
  };
}

const f = (x, d = 1) => (x === null || x === undefined ? '—' : typeof x === 'number' ? String(round(x, d)) : String(x));

/** meta.gc(tools/soak-test.* 的 --gen0-budget-mb)→ 說明文字;舊版腳本沒有這個欄位 */
export function gen0SettingText(meta) {
  const g = meta?.gc;
  if (!g) return '未記錄(meta.json 無 gc 欄位:舊版 soak 腳本,執行階段預設或外部環境變數)';
  const env = Object.entries(g.env ?? {}).map(([k, v]) => `${k}=${v}`).join('、');
  const inherited = Object.entries(g.inherited ?? {}).filter(([k]) => !(env && k === 'DOTNET_GCgen0size')).map(([k, v]) => `${k}=${v}`).join('、');
  const base = Number(g.gen0BudgetMb) > 0 ? `${g.gen0BudgetMb} MB(${(g.appliesTo ?? ['simcore', 'radar']).join('、')} 以 ${env || 'DOTNET_GCgen0size'} 啟動;--gen0-budget-mb)` : '執行階段預設(--gen0-budget-mb 0)';
  return `${base}${inherited ? `;繼承的 GC 環境變數 ${inherited}` : ''}`;
}

/**
 * meta.gitRev / gitDirty / hostDllSha256 → 說明文字(第三十輪審查 S-F4:工作樹有未提交的修改時,gitRev 對不到實際執行的程式,
 * 以 bin/ 複本的 SHA-256 為準;舊版 soak 腳本沒有 gitDirty 與雜湊)
 */
export function gitRevText(meta) {
  const rev = meta?.gitRev ? meta.gitRev.slice(0, 12) : '?';
  const dirty = meta?.gitDirty === true ? '(工作樹有未提交的修改:實際執行的程式以 SimCore.Host.dll 的雜湊為準)'
    : meta?.gitDirty === false ? '(無未提交的修改)' : meta && 'gitDirty' in meta ? '(是否有未提交的修改:未知)' : '(舊版 soak 腳本:未記錄是否有未提交的修改)';
  const sha = meta?.hostDllSha256 ? `;SimCore.Host.dll SHA-256 ${meta.hostDllSha256.slice(0, 16)}…` : '';
  return `${rev}${dirty}${sha}`;
}

export function renderMarkdown(s) {
  const m = s.meta;
  const lines = [];
  lines.push(`# 長時間穩定性(soak)測試報告 — ${s.run}`, '');
  lines.push(`> 規劃書第 14 章「效能」列(核心 50 Hz 無掉拍、延遲 < 50 ms、RSS 不持續成長);工具 \`tools/soak-test.sh\`(docs/design/testing.md 第 6 節)。`);
  lines.push(`> 情境 ${m.scenario ?? '?'};合成客戶端 ${m.clients ?? '?'} + 學員 1;設定時長 ${m.minutes ?? '?'} 分鐘(實際 ${f(s.host.elapsed_s, 0)} s);開始 ${m.startedUtc ?? '?'};結束 ${m.endedUtc ?? '?'};` +
    `平台 ${m.platform ?? '?'}(${m.cpuCount ?? '?'} 核;${m.machineNote ?? ''});git ${gitRevText(m)};.NET SDK ${m.dotnet ?? '?'}(執行階段 ${m.dotnetRuntimes || '未記錄'});Node ${m.node ?? '?'};gen0 預算設定 ${gen0SettingText(m)}。`, '');
  lines.push(`## 判定:**${s.pass ? '合格' : '不合格'}**`, '', '| 準則 | 門檻 | 量測 | 結果 |', '|---|---|---|---|');
  for (const c of s.criteria) lines.push(`| ${c.name} | ${c.threshold} | ${c.measured} | ${c.pass ? '通過' : c.undetermined ? '**無法判定(不通過)**' : '**不通過**'} |`);
  lines.push('');
  for (const c of s.criteria) if (c.note && c.id !== 'latency') lines.push(`- ${c.name}:${c.note}`);
  if (s.criteria.some((c) => c.note && c.id !== 'latency')) lines.push('');
  const ev = s.rssEval;
  if (ev?.rule === 'gc-counters') {
    lines.push('RSS 準則逐項(有 dotnet-counters;全部通過才合格;有一項不通過 = 不通過,否則有資料不足的項目 = 無法判定):', '', '| 項目 | 值 | 門檻 | 結果 | 說明 |', '|---|---|---|---|---|');
    for (const k of ev.checks) lines.push(`| ${k.name} | ${f(k.value, 3)} | ${k.limit} | ${k.status === 'pass' ? '通過' : k.status === 'undetermined' ? '**無法判定**' : '**不通過**'} | ${k.why ?? ''} |`);
    lines.push('');
  }
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
  lines.push('## GC 與 gen0 預算(dotnet-counters,核心)', '');
  lines.push(`gen0 預算設定:${gen0SettingText(m)}。`, '');
  if (s.gc) {
    const g = s.gc;
    const a = g.afterGcMb ?? {};
    const durMin = g.refreshes ? (g.refreshes * g.intervalS) / 60 : null;
    lines.push(`每 ${g.intervalS} s 一次刷新,共 ${g.refreshes} 次(${g.samples} 列);大小皆為 MiB(dotnet-counters 的「(MB)」是 10^6 B,已換算,與 RSS 同單位)。` +
      `${g.missing?.length ? `缺少計數器 ${g.missing.join('、')}:RSS 準則以只看 RSS 的規則判定。` : ''}`, '');
    lines.push('| 項目 | 值 |', '|---|---|');
    lines.push(`| gen0 預算(Gen 0 GC Budget) | ${g.gen0BudgetMb ? `首 ${f(g.gen0BudgetMb.first, 2)} / 末 ${f(g.gen0BudgetMb.last, 2)} / 最小 ${f(g.gen0BudgetMb.min, 2)} / 最大 ${f(g.gen0BudgetMb.max, 2)} MB` : '—'} |`);
    lines.push(`| GC 次數(gen0 計數含所有代) | 共 ${f(g.gen0, 0)}(gen1 ${f(g.gen1, 0)}、gen2 ${f(g.gen2, 0)});第一次 GC:${g.firstGcS === null ? '無計數器' : Number.isFinite(g.firstGcS) ? `counters 開始後 ${g.firstGcS} s 內` : '整段未發生'}${ev?.gcCount !== undefined ? `;擬合時段內 ${ev.gcCount} 次` : ''} |`);
    lines.push(`| GC 暫停 | 合計 ${f(g.gcPauseMs, 0)} ms、單次刷新最多 ${f(g.gcPauseMaxMsPerRefresh, 1)} ms / ${g.intervalS} s;GC 時間比例最大 ${f(g.gcTimePctMax, 1)} % |`);
    lines.push(`| GC 已提交量(GC Committed Bytes) | 第一次 GC 後首值 ${f(g.committedMb?.first)} → 末 ${f(g.committedMb?.last)}(最大 ${f(g.committedMb?.max)})MB${ev?.committed ? `;暖機後斜率 ${f(ev.committed.slopeMbPerMin, 3)} MB/min` : ''} |`);
    lines.push(`| 受控堆積(上次 GC 後 gen1+gen2+LOH+POH) | ${ev?.managed ? `第一次 GC 起 ${f(ev.managed.first)} → ${f(ev.managed.last)} MB(${ev.managed.samples} 次刷新;斜率 ${f(ev.managed.slopeMbPerMin, 3)} MB/min)` : '—'} |`);
    lines.push(`| RSS − GC 已提交量(非 GC 記憶體) | ${ev?.nonGc ? `穩定段 ${ev.steadyFromS} s 起 ${f(ev.nonGc.first)} → ${f(ev.nonGc.last)} MB(${ev.nonGc.samples} 筆;斜率 ${f(ev.nonGc.slopeMbPerMin, 3)} MB/min)` : '—'} |`);
    lines.push(`| RSS(穩定段) | ${ev?.rssSteady ? `${ev.steadyFromS} s 起 ${f(ev.rssSteady.first)} → ${f(ev.rssSteady.last)} MB(${ev.rssSteady.samples} 筆、${ev.steadySpanS} s;斜率 ${f(ev.rssSteady.slopeMbPerMin, 3)} MB/min)` : '—'} |`);
    lines.push(`| 上次 GC 後各代 | gen0 ${f(a.gen0, 2)} / gen1 ${f(a.gen1, 2)} / gen2 ${f(a.gen2, 2)} / LOH ${f(a.loh, 2)} / POH ${f(a.poh, 2)} MB |`);
    lines.push(`| 配置總量 | ${f(g.allocMb, 0)} MB${durMin ? `(平均 ${f(g.allocMb / durMin, 1)} MB/min)` : ''} |`);
    lines.push(`| 例外次數(Exception Count) | ${f(g.exceptions, 0)} |`);
    lines.push(`| GC 堆積(含未回收的 gen0)/ Working Set | 最後 ${f(g.gcHeapMb?.last)} / ${f(g.workingSetMb?.last)} MB(最大 ${f(g.gcHeapMb?.max)} / ${f(g.workingSetMb?.max)}) |`);
    lines.push('');
    lines.push('判讀:第一次 GC 之前 RSS 的上升約等於 gen0 預算(配置速率 × 時間;之後重用同一批已提交的記憶體),不是洩漏;洩漏的存活物件反映在受控堆積,' +
      'GC 自己多提交反映在 GC 已提交量,原生記憶體 / 執行緒反映在 RSS − 已提交量。gen2 / LOH 在兩次 gen2 GC 之間只增不減,單看短時間的上升不等於洩漏,所以斜率取全段。', '');
  } else lines.push('略過:找不到 dotnet-counters(PATH 或 .cache/dotnet-tools);RSS 由 /proc(Windows:Get-Process)取樣,RSS 準則無法分辨 gen0 預算填充。', '');
  lines.push('## 方法與限制', '');
  lines.push('- 延遲為牆鐘估計(`tests/soak/lib.mjs` LatencyEstimator):核心以牆鐘推進,第 k 幀理想到達時刻 = 原點 + state.t,原點取連線初期最快一幀;量到的是核心序列化 + WebSocket 傳送 + 客戶端事件迴圈的總延遲,核心落後牆鐘也會反映為延遲上升。凍結幀不計;時間倍率須為 1。');
  lines.push('- tick 率以客戶端牆鐘量測(首末狀態幀的 tick 差 / 到達時間差),與主控程序每秒輸出的 `steps/s` 互相印證;「掉拍」取自主控程序輸出的累計值。');
  lines.push('- RSS 準則:有 dotnet-counters 時 GC 次數 ≥ 下限,且第一次 GC 起受控堆積、暖機後 GC 已提交量的全段最小平方斜率,以及穩定段(預設 720 s 起、至少 600 s)RSS − 已提交量與 RSS 的斜率 ≤ 門檻,且暖機後 RSS 增量 ≤ 門檻' +
    '(全部成立;GC 次數不足或擬合時段不夠長 = 無法判定;前約 12 分鐘有一次性的非 GC 階梯,短於約 22 分鐘的執行不足以判定,建議 25 分鐘以上);' +
    '暖機至少延到第一次 GC 之後。沒有 dotnet-counters 時只看暖機後 RSS 斜率與增量(分辨不出 gen0 預算填充)。.NET 8 工作站 GC 的 gen0 預算約 L3 / 2(雲端大快取機器 136–264 MB,10 分鐘內只 GC 一兩次),' +
    'soak 腳本預設以 --gen0-budget-mb 6(DOTNET_GCgen0size)模擬船上內顯筆電的小快取。超過設定時長或合成客戶端已結束(clients.alive=false)的收尾樣本不入擬合(客戶端斷線時核心釋放連線常有一筆尖峰;起訖/最大值等原始統計仍含)。' +
    '後半段斜率為參考值;門檻為軟體面判準,是否洩漏以實機 2 小時(同一工具)為準。');
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
      'steady-from-s': { type: 'number', default: DEFAULT_THRESHOLDS.steadyFromS },
      'min-steady-s': { type: 'number', default: DEFAULT_THRESHOLDS.minSteadyS },
      'min-slope-span-s': { type: 'number', default: DEFAULT_THRESHOLDS.minSlopeSpanS },
      quiet: { type: 'boolean', default: false },
    });
    if (!opt.dir) throw new Error('--dir 為必要參數');
  } catch (err) {
    console.error(err.message);
    return 2;
  }
  let run;
  try { run = loadRun(opt.dir); } catch (err) { console.error(err.message); return 2; }
  const s = summarise(run, { coreHz: opt['core-hz'], coreHzTol: opt['core-hz-tol'], maxLatencyMs: opt['max-latency-ms'], maxRssSlopeMbPerMin: opt['max-rss-slope-mb-per-min'], maxRssGrowthMb: opt['max-rss-growth-mb'], warmupS: opt['warmup-s'],
    steadyFromS: opt['steady-from-s'], minSteadyS: opt['min-steady-s'], minSlopeSpanS: opt['min-slope-span-s'] });
  const md = renderMarkdown(s);
  if (opt.md) { fs.mkdirSync(path.dirname(opt.md), { recursive: true }); fs.writeFileSync(opt.md, md); }
  if (opt.json) { fs.mkdirSync(path.dirname(opt.json), { recursive: true }); fs.writeFileSync(opt.json, JSON.stringify(s, null, 2)); }
  if (!opt.quiet) {
    console.log(`soak ${s.run}:${s.pass ? '合格' : '不合格'}`);
    for (const c of s.criteria) console.log(`  ${c.pass ? 'ok  ' : c.undetermined ? 'FAIL(無法判定)' : 'FAIL'} ${c.name}:${c.measured}`);
    if (opt.md) console.log(`  報告 ${opt.md}${opt.json ? `、${opt.json}` : ''}`);
  }
  return s.pass ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exit(main());
