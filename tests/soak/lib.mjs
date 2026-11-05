// 長時間穩定性(soak)測試的共用小工具(tests/soak/clients.mjs、student.mjs、report.mjs;規劃書第 14 章「效能」列;docs/design/testing.md 第 6 節)。
// 只用 Node 22 內建模組(WebSocket、fs、perf_hooks),無套件相依,船上/開發機離線可跑。
import fs from 'node:fs';
import path from 'node:path';
import { performance } from 'node:perf_hooks';

/** 單調時鐘(毫秒;不受系統時間校正影響,延遲估計用) */
export const nowMs = () => performance.now();

/** 追加一行 JSON 到 JSON Lines 檔(--out 未給時印到 stdout) */
export function makeSink(outPath) {
  if (!outPath) return (obj) => process.stdout.write(JSON.stringify(obj) + '\n');
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  const fd = fs.openSync(outPath, 'a');
  return (obj) => fs.writeSync(fd, JSON.stringify(obj) + '\n');
}

/** 毫秒直方圖(0–bins-1 每格 1 ms,最後一格為溢位):整場 p50/p95/max 不必保存每一筆樣本(2 小時 × 25 Hz × N 客戶端) */
export class MsHistogram {
  constructor(bins = 2000) {
    this.bins = new Uint32Array(bins);
    this.count = 0;
    this.max = 0;
    this.sum = 0;
  }
  add(ms) {
    if (!Number.isFinite(ms)) return;
    const v = ms < 0 ? 0 : ms;
    const i = Math.min(this.bins.length - 1, Math.floor(v));
    this.bins[i]++;
    this.count++;
    this.sum += v;
    if (v > this.max) this.max = v;
  }
  /** 分位數(毫秒;直方圖上界,溢位格回傳 max) */
  quantile(q) {
    if (this.count === 0) return null;
    const target = Math.max(1, Math.ceil(q * this.count));
    let acc = 0;
    for (let i = 0; i < this.bins.length; i++) {
      acc += this.bins[i];
      if (acc >= target) return i === this.bins.length - 1 ? this.max : i + 1;
    }
    return this.max;
  }
  get mean() {
    return this.count ? this.sum / this.count : null;
  }
  /** 合併另一個直方圖(整場 / 全部客戶端彙總) */
  merge(other) {
    for (let i = 0; i < this.bins.length; i++) this.bins[i] += other.bins[i] ?? 0;
    this.count += other.count;
    this.sum += other.sum;
    if (other.max > this.max) this.max = other.max;
    return this;
  }
  toJSON() {
    return { count: this.count, max: round(this.max, 2), mean: round(this.mean, 2), p50: this.quantile(0.5), p95: this.quantile(0.95), p99: this.quantile(0.99), bins: Array.from(this.bins) };
  }
  static fromJSON(o) {
    const h = new MsHistogram((o?.bins?.length) || 2000);
    if (!o) return h;
    for (let i = 0; i < h.bins.length; i++) h.bins[i] = o.bins?.[i] ?? 0;
    h.count = o.count ?? 0;
    h.sum = (o.mean ?? 0) * h.count;
    h.max = o.max ?? 0;
    return h;
  }
}

export function round(x, digits = 1) {
  if (x === null || x === undefined || !Number.isFinite(x)) return null;
  const f = 10 ** digits;
  return Math.round(x * f) / f;
}

/** 排序陣列的分位數(小樣本用) */
export function quantileOf(sortedValues, q) {
  if (!sortedValues.length) return null;
  const idx = Math.min(sortedValues.length - 1, Math.max(0, Math.ceil(q * sortedValues.length) - 1));
  return sortedValues[idx];
}

/** 解析 --key value / --flag 參數(與 tools/launch 腳本相同的簡單規則;未知鍵回報錯誤) */
export function parseArgs(argv, spec) {
  const out = {};
  for (const [k, v] of Object.entries(spec)) out[k] = v.default;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith('--')) throw new Error(`未知參數:${a}`);
    const key = a.slice(2);
    const s = spec[key];
    if (!s) throw new Error(`未知參數:${a}`);
    if (s.type === 'boolean') out[key] = true;
    else {
      const val = argv[++i];
      if (val === undefined) throw new Error(`${a} 需要參數值`);
      out[key] = s.type === 'number' ? Number(val) : val;
      if (s.type === 'number' && !Number.isFinite(out[key])) throw new Error(`${a} 須為數字:${val}`);
    }
  }
  return out;
}

/** 核心狀態幀判定(與 tests/e2e/support/ws.ts 相同:有 tick 與 heading) */
export const isStateFrame = (f) => f && typeof f.tick === 'number' && typeof f.heading === 'number';

/**
 * 以牆鐘估計狀態幀延遲(規劃書第 14 章「網路延遲 <50 ms」的軟體面近似):
 * 核心以牆鐘推進(RealtimeRunner:t ≈ 牆鐘 × 倍率),第 k 幀的理想到達時刻 = 原點 + t_k;原點取連線初期(前 calibrateFrames 幀)
 * 「到達時刻 − t」的最小值(最快送達的那一幀),之後每幀延遲 = 到達 − t − 原點(≥ 0),含核心序列化、WebSocket 傳送與本程序事件迴圈;
 * 核心落後牆鐘(掉拍)時也會反映為延遲上升。凍結幀(flags.frozen)與倍率 ≠ 1 的區段不計。
 */
export class LatencyEstimator {
  constructor(calibrateFrames = 50) {
    this.calibrateFrames = calibrateFrames;
    this.seen = 0;
    this.origin = Infinity;
  }
  /** 回傳估計延遲(毫秒)或 null(校正中) */
  observe(tSeconds, arrivalMs) {
    const diff = arrivalMs - tSeconds * 1000;
    if (this.seen < this.calibrateFrames) {
      this.seen++;
      if (diff < this.origin) this.origin = diff;
      return null;
    }
    // 校正後仍允許原點下修(連線初期若恰好全是慢幀,後續更快的幀會修正原點;只會讓延遲估計變小,不會高估)
    if (diff < this.origin) this.origin = diff;
    return diff - this.origin;
  }
}
