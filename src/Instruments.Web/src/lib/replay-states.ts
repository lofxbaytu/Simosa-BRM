// 重播檢視的紀錄載入(規劃書第 9.2 節「重播」、第 14 章「記憶體不持續成長」;第三十一輪「重播檢視讀大檔」):
// 2 小時的核心 JSON Lines 紀錄約 1.35 GB、25 Hz × 180,000 筆狀態(每行約 7.5 KB)。原本重播檢視把整份讀成一個字串再整份解析、
// 每筆保留完整狀態:30 分鐘的紀錄(337 MB)尖峰約 +1.55 GB、常駐 +290 MB,2 小時的紀錄讀到 +1.2 GB 時分頁當掉(數字見 README「重播檢視」)。
//
// 現在與講評報告頁一樣分段讀取(lib/record-stream.ts:只開一次 Blob.stream()、逐筆解析、定時讓出主執行緒),狀態放進這裡的
// ReplayHistoryStore(record-replay.ts 的 ReplayStateStore;倒帶 / 重設的判斷仍在建構器裡,與整份讀入相同):
//  - 「顯示用的狀態」:教官站抽樣的精簡投影(session-record.ts sampleState;教官站紀錄版本 5 / 6 的 states 就是這種投影,
//    重播檢視的 conning 列、航跡圖、目標船小表只讀這些欄位),最多 REPLAY_MAX_STATES 筆。沒有超過上限的紀錄每一筆都保留(筆數、時刻與原本相同);
//    超過時才降採樣:每 2 筆取 1 筆(依狀態在歷史中的序號,保留第一筆),仍超過就再對半 —— 每 stride 筆取 1 筆(stride = 2 的次方),
//    最後一筆一律保留(作法與教官站即時抽樣 StateSampler 超過上限時相同)。
//    倒帶(第三十一輪審查 DATA-1 / MEM-1):超不超過上限看的是練習**最終的歷史**(倒帶 / 重設丟掉舊分支之後剩下的狀態),不是倒帶前曾經有多長 ——
//    練習跑到超過上限後倒帶回較早的快照、最後不到上限的紀錄,每一筆都在(原本倒帶只截掉尾端、沿用倒帶前的間隔,不到上限的紀錄也被降採樣)。
//    作法:前 REPLAY_MAX_STATES 筆在讀取期間一律全留(見 ReplayHistoryStore)。最終仍超過上限、而倒帶前更長的紀錄沿用倒帶前的間隔
//    (核心最多倒回約 10 分鐘 = 15,000 筆,所以多半是最終筆數所需的 2 倍,最多 4 倍;畫面上寫的是實際的間隔)—— 要連這種情形都取到
//    最小的間隔,讀取期間得留約 23,600 筆候選(2 小時的紀錄),實測讀取尖峰 +382–421 MB(採用的作法 +298–309 MB、只留顯示用的那些時
//    +224–241 MB),沒有採用(數字見 README「重播檢視」的「讀大檔」)。
//  - 「全部狀態的精簡欄位」(HistoryLog;每筆 9 個數字 + 車鐘 + 旗標,約 76 位元組,放在分塊的 typed array):倒帶時要丟掉哪些、丟了幾筆、
//    tStart / tEnd、講評摘要(與 summarize(全部狀態) 相同)、航跡點(與 trackPoints(全部狀態) 相同)、各事件發生時的自船位置
//    (與 eventMarks 以全部狀態查到的相同)都由它算出 —— 這些**不受降採樣影響**。算完就丟掉(release),不是常駐記憶體;
//    讀取期間它隨紀錄長度成長(2 小時 180,000 筆約 14 MB),是唯一與紀錄長度成正比的暫存。
//    (顯示用的狀態在讀取期間最多約 2 × REPLAY_MAX_STATES 筆 —— 前 REPLAY_MAX_STATES 筆全留加上之後降採樣的那些,讀完剩最多 REPLAY_MAX_STATES 筆。)
//
// 結果(ReplayViewData)= 與 parseReplay() 相同的 ReplayData(states 換成顯示用的狀態)加上 stateStats / debrief / track / eventPositions。
// 等價性由 tests/replay-states.test.ts 以全部夾具、各版本的教官站紀錄、倒帶 / 重設、損壞的紀錄與隨機切段驗證。

import type { OwnShipState } from '../types/state.js';
import type { SimEvent } from '../types/event.js';
import { DebriefAccumulator, DEFAULT_THRESHOLDS, type DebriefState, type DebriefSummary, type DebriefThresholds } from './debrief.js';
import type { ReplayDataOf, ReplayStateBase, ReplayStateStore } from './record-replay.js';
import { parseReplayBlob, parseReplayText, type ParseReplayBlobOptions } from './record-stream.js';
import { MAX_STATE_SAMPLES, sampleState, type SampledState } from './session-record.js';
import type { TrackPoint } from './track-history.js';

/**
 * 重播檢視保留(顯示用)的狀態筆數上限:與教官站抽樣的上限相同(1 Hz × 2 小時含兩端 = 7201 筆),所以教官站自己下載的紀錄
 * (版本 5 / 6,最多 7201 筆)不會被降採樣;核心 25 Hz 的紀錄超過 4 分 48 秒就會(每 2 / 4 / 8 … 筆取 1 筆;2 小時是每 32 筆 = 1.28 秒)。
 * 每筆約 2–4 KB(6 艘目標船時約 3.9 KB),上限時約 15–30 MB —— 與 2 小時的教官站紀錄相同量級。
 */
export const REPLAY_MAX_STATES = MAX_STATE_SAMPLES;

/** 重播檢視保留的一筆狀態:教官站抽樣的精簡投影(session-record.ts SampledState) */
export type ReplayState = SampledState;

/** 顯示用:精簡投影 → 儀器元件吃的 OwnShipState(沒有保留的必填欄位補預設值;元件只讀保留的欄位,見 SAMPLED_STATE_KEYS)。 */
export function replayDisplayState(s: ReplayState): OwnShipState {
  const targets = s.targets.map((t) => ({ mmsi: 0, shipType: 'other', beam: 0, rot: 0, lights: 'powerDriven', behaviour: 'hold', ...t, pos: { lat: 0, lon: 0, ...t.pos } }));
  return { u: 0, v: 0, r: 0, loading: 'full', ...s, targets } as unknown as OwnShipState;
}

// ------------------------------------------------------------------ 全部狀態的精簡欄位

/** 每塊的筆數 */
const LOG_CHUNK = 4096;
/** 每筆的 Float64 欄數與各欄的位置 */
const LOG_FIELDS = 9;
const F_T = 0;
const F_TICK = 1;
const F_X = 2;
const F_Y = 3;
const F_SOG = 4;
const F_ROT = 5;
const F_RUDDER = 6;
const F_RUDDER_ORDER = 7;
const F_UKC = 8;
/** 每筆的 Uint16 欄:車鐘(字串表的索引)、旗標 */
const LOG_WORDS = 2;
const W_TELEGRAPH = 0;
const W_FLAGS = 1;
const FLAG_PRESENT = 1;
const FLAG_FROZEN = 2;
const FLAG_AGROUND = 4;
const FLAG_COLLISION = 8;

/**
 * 全部狀態的精簡欄位(講評累計讀取的欄位 DebriefState 與 tick),分塊存在 typed array 裡(不隨筆數重新配置、不產生逐筆的物件)。
 * 數值原樣保存(Float64),所以由它累計的摘要與由完整狀態累計的逐位元相同。
 */
class HistoryLog {
  length = 0;
  private nums: Float64Array[] = [];
  private words: Uint16Array[] = [];
  /** 車鐘位置的字串表(parseState 只會給 TELEGRAPH_POSITIONS 內的值;表是動態的,不依賴那個清單) */
  private readonly telegraphs: string[] = [];
  private readonly telegraphIndex = new Map<string, number>();

  append(s: OwnShipState): void {
    const i = this.length;
    const c = i >> 12;
    if (c === this.nums.length) {
      this.nums.push(new Float64Array(LOG_CHUNK * LOG_FIELDS));
      this.words.push(new Uint16Array(LOG_CHUNK * LOG_WORDS));
    }
    const o = (i & (LOG_CHUNK - 1)) * LOG_FIELDS;
    const f = this.nums[c]!;
    f[o + F_T] = s.t;
    f[o + F_TICK] = s.tick;
    f[o + F_X] = s.pos.x;
    f[o + F_Y] = s.pos.y;
    f[o + F_SOG] = s.sog;
    f[o + F_ROT] = s.rot;
    f[o + F_RUDDER] = s.rudder;
    f[o + F_RUDDER_ORDER] = s.rudderOrder;
    f[o + F_UKC] = s.depthBelowKeel;
    let tg = this.telegraphIndex.get(s.telegraph);
    if (tg === undefined) {
      tg = this.telegraphs.length;
      this.telegraphs.push(s.telegraph);
      this.telegraphIndex.set(s.telegraph, tg);
    }
    const w = this.words[c]!;
    const wo = (i & (LOG_CHUNK - 1)) * LOG_WORDS;
    w[wo + W_TELEGRAPH] = tg;
    const fl = s.flags;
    w[wo + W_FLAGS] = fl ? FLAG_PRESENT | (fl.frozen ? FLAG_FROZEN : 0) | (fl.aground ? FLAG_AGROUND : 0) | (fl.collision ? FLAG_COLLISION : 0) : 0;
    this.length = i + 1;
  }

  num(i: number, field: number): number {
    return this.nums[i >> 12]![(i & (LOG_CHUNK - 1)) * LOG_FIELDS + field]!;
  }

  t(i: number): number {
    return this.num(i, F_T);
  }

  /** 第 i 筆的講評累計欄位(新物件)。 */
  row(i: number): DebriefState {
    const f = this.nums[i >> 12]!;
    const o = (i & (LOG_CHUNK - 1)) * LOG_FIELDS;
    const w = this.words[i >> 12]!;
    const wo = (i & (LOG_CHUNK - 1)) * LOG_WORDS;
    const out: DebriefState = {
      t: f[o + F_T]!,
      tick: f[o + F_TICK]!,
      pos: { x: f[o + F_X]!, y: f[o + F_Y]! },
      sog: f[o + F_SOG]!,
      rot: f[o + F_ROT]!,
      rudder: f[o + F_RUDDER]!,
      rudderOrder: f[o + F_RUDDER_ORDER]!,
      telegraph: this.telegraphs[w[wo + W_TELEGRAPH]!] as DebriefState['telegraph'],
      depthBelowKeel: f[o + F_UKC]!,
    };
    const fl = w[wo + W_FLAGS]!;
    if (fl & FLAG_PRESENT) out.flags = { frozen: (fl & FLAG_FROZEN) !== 0, aground: (fl & FLAG_AGROUND) !== 0, collision: (fl & FLAG_COLLISION) !== 0 };
    return out;
  }

  /** 丟棄 t > limit 的筆(與 Array.filter((x) => x.t <= limit) 相同:t 不遞增的損壞紀錄也逐筆判斷);回傳丟棄的筆數。 */
  dropAfter(limit: number): number {
    const n = this.length;
    let w = 0;
    for (let i = 0; i < n; i++) {
      if (!(this.t(i) <= limit)) continue;
      if (w !== i) this.copy(i, w);
      w++;
    }
    this.truncate(w);
    return n - w;
  }

  private copy(from: number, to: number): void {
    const fo = (from & (LOG_CHUNK - 1)) * LOG_FIELDS;
    const to_ = (to & (LOG_CHUNK - 1)) * LOG_FIELDS;
    const src = this.nums[from >> 12]!;
    const dst = this.nums[to >> 12]!;
    for (let k = 0; k < LOG_FIELDS; k++) dst[to_ + k] = src[fo + k]!;
    const ws = this.words[from >> 12]!;
    const wd = this.words[to >> 12]!;
    for (let k = 0; k < LOG_WORDS; k++) wd[(to & (LOG_CHUNK - 1)) * LOG_WORDS + k] = ws[(from & (LOG_CHUNK - 1)) * LOG_WORDS + k]!;
  }

  private truncate(n: number): void {
    this.length = n;
    const chunks = Math.ceil(n / LOG_CHUNK);
    this.nums.length = chunks;
    this.words.length = chunks;
  }

  clear(): void {
    this.truncate(0);
  }
}

// ------------------------------------------------------------------ 狀態存放

/** 歷史有 n 筆、每 stride 筆取 1 筆(序號為 stride 的倍數)再加最後一筆時,顯示的筆數。 */
export function replayShownCount(n: number, stride: number): number {
  if (n <= 0) return 0;
  const last = n - 1;
  return Math.floor(last / stride) + 1 + (last % stride !== 0 ? 1 : 0);
}

/** 歷史有 n 筆時每幾筆取 1 筆:顯示的筆數(replayShownCount)放得進 maxStates 的最小間隔(1、2、4 …;n ≤ maxStates 時 1)。 */
export function replayStride(n: number, maxStates: number): number {
  let stride = 1;
  while (replayShownCount(n, stride) > maxStates) stride *= 2;
  return stride;
}

export interface ReplayStateStats {
  /** 練習最終歷史的狀態筆數(全部;倒帶的舊分支已丟棄) */
  total: number;
  /** 保留下來顯示的筆數(= states.length) */
  shown: number;
  /**
   * 每幾筆取 1 筆(1 = 全部保留;超過上限時為 2 的次方)。total ≤ maxStates 時一定是 1;沒有倒帶的紀錄是放得下的最小間隔
   * replayStride(total, maxStates);倒帶過而最終仍超過上限的紀錄可能更大(沿用倒帶前的間隔,見 ReplayHistoryStore)。
   */
  stride: number;
  /** 保留筆數的上限 */
  maxStates: number;
  /**
   * 顯示的狀態相鄰兩筆的間隔(模擬秒)——「逐筆」前進 / 後退一次的時間。取**時刻有前進**的相鄰間隔的中位數:凍結時核心每 200 ms
   * 記一筆 t 不變的狀態,長時間凍結的紀錄這種間隔(0)占多數,連它們一起取中位數會得到 0(第三十一輪審查 DATA-2:說明寫成「約每 0 秒一筆」)。
   * 沒有任何兩筆時刻不同時 0。
   */
  interval_s: number;
}

/**
 * 重播檢視的狀態存放:全部狀態的精簡欄位(HistoryLog)+ 顯示用的狀態(精簡投影,最多 maxStates 筆;見檔頭)。
 * 建構器讀完(finish)之後,由 summarize / trackPoints / positionAt 取以全部狀態計算的結果,再 release() 丟掉精簡欄位。
 *
 * 顯示用的狀態:歷史 n 筆、間隔 stride 時是序號為 stride 倍數的那些,再加最後一筆。
 *  - n ≤ maxStates:stride 1,每一筆都在。讀取當下不知道之後會不會倒帶回到上限以內,所以**序號小於 maxStates 的狀態在讀取期間一律留著**
 *    (「前段」);超過上限後它們多數不在顯示的那些裡,只是備用,finish() 才依最後的間隔挑。
 *  - 序號 ≥ maxStates 的(「後段」)只留目前間隔的倍數;筆數再長、放不下時間隔加倍,後段就地對半(與原本相同)。
 *  - 倒帶(dropAfter)截到 maxStates 以內:後段整個沒了,間隔回到 1。截短後仍超過 maxStates:後段較細的那些已經丟了,間隔沿用
 *    (≥ replayStride(n);之後照舊只增不減)—— 所以 stride = 自歷史上一次不超過 maxStates 以來到過的最大筆數所需的間隔。
 * 讀取期間留著的筆數最多約 2 × maxStates(前段 maxStates 筆 + 後段不到 maxStates 筆);finish() 之後只剩顯示的那些(≤ maxStates)。
 */
export class ReplayHistoryStore implements ReplayStateStore<ReplayState> {
  /** 建構器已經 finish()(分段解析器準備的兩個存放中,產生結果的那一個) */
  finished = false;

  private log = new HistoryLog();
  /** 讀取期間:前段全部 + 後段中序號為 stride 倍數的(見類別說明);finish() 之後:要顯示的那些 */
  private kept: ReplayState[] = [];
  /** kept 各筆在歷史中的序號(遞增;t 不遞增的損壞紀錄倒帶時丟的不是尾端,留下的序號跟著前移) */
  private keptAt: number[] = [];
  /** 每幾筆取 1 筆(見類別說明;歷史不超過 maxStates 時 1) */
  private stride = 1;
  /** 最後一筆的完整狀態(不在挑出的那些裡時,finish() 才投影補上:不必每一筆都投影) */
  private lastFull: OwnShipState | null = null;

  constructor(readonly maxStates: number = REPLAY_MAX_STATES) {
    if (!(maxStates >= 4)) throw new RangeError(`maxStates 至少為 4(收到 ${maxStates})`);
  }

  get length(): number {
    return this.log.length;
  }

  first(): ReplayStateBase | undefined {
    return this.log.length > 0 ? { t: this.log.num(0, F_T), tick: this.log.num(0, F_TICK) } : undefined;
  }

  last(): ReplayStateBase | undefined {
    const n = this.log.length;
    return n > 0 ? { t: this.log.num(n - 1, F_T), tick: this.log.num(n - 1, F_TICK) } : undefined;
  }

  push(state: OwnShipState): void {
    const i = this.log.length;
    this.log.append(state);
    this.lastFull = state;
    // 連這一筆在內放不下:間隔加倍(筆數只加一,maxStates ≥ 4 時一次就夠;用 while 只是不依賴這一點),後段只留新間隔的倍數
    while (replayShownCount(i + 1, this.stride) > this.maxStates) {
      this.stride *= 2;
      this.thinTail();
    }
    // 前段(i < maxStates)加入時歷史還不到上限、間隔是 1,所以每一筆都留;之後 thinTail() 不動它們
    if (i % this.stride === 0) {
      this.kept.push(sampleState(state));
      this.keptAt.push(i);
    }
  }

  /** 後段(序號 ≥ maxStates)只留目前間隔的倍數;前段不動。 */
  private thinTail(): void {
    const kept = this.kept;
    const at = this.keptAt;
    let w = 0;
    for (let k = 0; k < kept.length; k++) {
      const i = at[k]!;
      if (i >= this.maxStates && i % this.stride !== 0) continue;
      kept[w] = kept[k]!;
      at[w] = i;
      w++;
    }
    kept.length = w;
    at.length = w;
  }

  dropAfter(t: number): number {
    const log = this.log;
    const n = log.length;
    const kept = this.kept;
    const at = this.keptAt;
    // 一趟:留著的那些連同它在截短後歷史中的新序號(正常紀錄丟的是尾端,序號不變;t 不遞增的損壞紀錄才會前移)
    let k = 0;
    let keptAfter = 0;
    let headKept = 0;
    let after = 0;
    for (let i = 0; i < n; i++) {
      const stays = log.t(i) <= t;
      if (k < at.length && at[k] === i) {
        if (stays) {
          kept[keptAfter] = kept[k]!;
          at[keptAfter] = after;
          keptAfter++;
          if (after < this.maxStates) headKept++;
        }
        k++;
      }
      if (stays) after++;
    }
    if (after === n) return 0;
    kept.length = keptAfter;
    at.length = keptAfter;
    log.dropAfter(t);
    if (this.lastFull && !(this.lastFull.t <= t)) this.lastFull = null;
    // 截到上限以內:前段每一筆都在,間隔回到 1(原本只有全部丟光才回到 1:倒帶前曾超過上限、倒帶後不到上限的紀錄仍被降採樣)。
    // 仍超過上限:後段只有目前間隔的倍數,沿用。
    // 前段不齊(t 不遞增的損壞紀錄:丟的是中間,後段已對半過的狀態前移到前段的位置)時也沿用 —— 少了狀態就不能說每一筆都在。
    if (after <= this.maxStates && headKept === after) this.stride = 1;
    return n - after;
  }

  clear(): void {
    this.log.clear();
    this.kept = [];
    this.keptAt = [];
    this.stride = 1;
    this.lastFull = null;
  }

  finish(): ReplayState[] {
    this.finished = true;
    const n = this.log.length;
    const stride = this.stride;
    const kept = this.kept;
    const at = this.keptAt;
    const out: ReplayState[] = [];
    let lastAt = -1;
    for (let k = 0; k < kept.length; k++) {
      if (at[k]! % stride !== 0) continue;
      out.push(kept[k]!);
      lastAt = at[k]!;
    }
    // 最後一筆(序號 n − 1)一律保留:已經留著(前段)就用那一筆,否則由完整狀態投影
    // (建構器倒帶後一定接著加入新的狀態;只截短而沒有再加入時 lastFull 可能已不在,那時最後一筆只在它剛好留著時才有)
    if (n > 0 && lastAt !== n - 1) {
      const k = kept.length - 1;
      if (k >= 0 && at[k] === n - 1) out.push(kept[k]!);
      else if (this.lastFull) out.push(sampleState(this.lastFull));
    }
    this.kept = out;
    this.keptAt = [];
    this.lastFull = null;
    return out;
  }

  /** 每幾筆取 1 筆(1 = 全部保留;見類別說明) */
  get currentStride(): number {
    return this.stride;
  }

  /** 以全部狀態累計的講評摘要(與 debrief.ts summarize(全部狀態) 相同)。 */
  summarize(thresholds: DebriefThresholds = DEFAULT_THRESHOLDS): DebriefSummary {
    const acc = new DebriefAccumulator(thresholds);
    const n = this.log.length;
    for (let i = 0; i < n; i++) acc.push(this.log.row(i));
    return acc.summary();
  }

  /** 航跡點:每 interval_s 秒一點(與 record-replay.ts trackPoints(全部狀態, interval_s) 相同)。 */
  trackPoints(interval_s = 1): TrackPoint[] {
    const pts: TrackPoint[] = [];
    const n = this.log.length;
    let last = Number.NEGATIVE_INFINITY;
    for (let i = 0; i < n; i++) {
      const t = this.log.t(i);
      if (t - last < interval_s && pts.length > 0) continue;
      pts.push({ x: this.log.num(i, F_X), y: this.log.num(i, F_Y), t });
      last = t;
    }
    return pts;
  }

  /** t 時刻(含)之前最後一筆狀態的位置(與 record-replay.ts stateIndexAt(全部狀態, t) 取到的那一筆相同;同樣的二分搜尋);沒有狀態時 null。 */
  positionAt(t: number): { x: number; y: number } | null {
    const n = this.log.length;
    if (n === 0) return null;
    let i: number;
    if (t <= this.log.t(0)) i = 0;
    else if (t >= this.log.t(n - 1)) i = n - 1;
    else {
      let lo = 0;
      let hi = n - 1;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (this.log.t(mid) <= t) lo = mid;
        else hi = mid - 1;
      }
      i = lo;
    }
    return { x: this.log.num(i, F_X), y: this.log.num(i, F_Y) };
  }

  /** 丟掉全部狀態的精簡欄位(之後不能再呼叫 summarize / trackPoints / positionAt)。 */
  release(): void {
    this.log = new HistoryLog();
  }
}

// ------------------------------------------------------------------ 載入

/** 重播檢視的資料:ReplayData(states 是顯示用的精簡狀態,可能已降採樣)加上以全部狀態算出的結果。 */
export interface ReplayViewData extends ReplayDataOf<ReplayState> {
  stateStats: ReplayStateStats;
  /** 本地推導的講評摘要:以**全部**狀態累計(沒有狀態時 null) */
  debrief: DebriefSummary | null;
  /** 航跡點:由**全部**狀態每 1 秒取一點(與降採樣無關) */
  track: TrackPoint[];
  /** 各事件(與 events 同索引)發生時的自船位置:**全部**狀態中該時刻(含)之前最後一筆;沒有狀態時 null */
  eventPositions: Array<{ x: number; y: number } | null>;
}

export interface LoadReplayOptions {
  /** 顯示用狀態的筆數上限(預設 REPLAY_MAX_STATES) */
  maxStates?: number;
}

/** 相鄰兩筆的間隔的中位數:只算時刻有前進的(見 ReplayStateStats.interval_s);沒有時 0。 */
function medianInterval(states: readonly ReplayState[]): number {
  const d: number[] = [];
  for (let i = 1; i < states.length; i++) {
    const dt = states[i]!.t - states[i - 1]!.t;
    if (dt > 0) d.push(dt);
  }
  if (d.length === 0) return 0;
  d.sort((a, b) => a - b);
  return d[d.length >> 1]!;
}

/** 一次解析用的存放工廠:記下產生的存放,解析完找出建構器用到(已 finish)的那一個。 */
function historyStores(maxStates: number): { factory: () => ReplayHistoryStore; used: () => ReplayHistoryStore | null } {
  const made: ReplayHistoryStore[] = [];
  return {
    factory: () => {
      const s = new ReplayHistoryStore(maxStates);
      made.push(s);
      return s;
    },
    used: () => made.find((s) => s.finished) ?? null,
  };
}

/** 解析結果 + 存放 → 重播檢視的資料(以全部狀態算摘要、航跡、事件位置,之後丟掉全部狀態的精簡欄位)。 */
function toViewData(data: ReplayDataOf<ReplayState>, store: ReplayHistoryStore | null, maxStates: number): ReplayViewData {
  const total = store ? store.length : 0;
  const out: ReplayViewData = {
    ...data,
    stateStats: { total, shown: data.states.length, stride: store ? store.currentStride : 1, maxStates, interval_s: medianInterval(data.states) },
    debrief: store && total > 0 ? store.summarize() : null,
    track: store ? store.trackPoints(1) : [],
    eventPositions: data.events.map((e: SimEvent) => (store ? store.positionAt(e.t) : null)),
  };
  store?.release();
  return out;
}

/** 由整份文字載入(測試與已在手上的文字;結果與分段讀取相同)。 */
export function loadReplayText(text: string, options: LoadReplayOptions = {}): ReplayViewData {
  const maxStates = options.maxStates ?? REPLAY_MAX_STATES;
  const stores = historyStores(maxStates);
  const res = parseReplayText<ReplayState>(text, undefined, stores.factory);
  return toViewData(res.data, stores.used(), maxStates);
}

export type LoadReplayBlobOptions = LoadReplayOptions & Pick<ParseReplayBlobOptions<ReplayState>, 'chunkBytes' | 'yieldEveryMs' | 'onProgress'>;

/** 分段讀取並載入一份紀錄(Blob / File):不把整份紀錄讀成一個字串,讀取中定時讓出主執行緒;onProgress 擲出例外即中止。 */
export async function loadReplayBlob(blob: Blob, options: LoadReplayBlobOptions = {}): Promise<ReplayViewData> {
  const maxStates = options.maxStates ?? REPLAY_MAX_STATES;
  const stores = historyStores(maxStates);
  const parse: ParseReplayBlobOptions<ReplayState> = { stores: stores.factory };
  if (options.chunkBytes !== undefined) parse.chunkBytes = options.chunkBytes;
  if (options.yieldEveryMs !== undefined) parse.yieldEveryMs = options.yieldEveryMs;
  if (options.onProgress) parse.onProgress = options.onProgress;
  const res = await parseReplayBlob<ReplayState>(blob, parse);
  return toViewData(res.data, stores.used(), maxStates);
}
