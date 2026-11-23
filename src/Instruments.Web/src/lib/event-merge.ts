// 教官站事件流:同一個碰墊連續的低速接觸合併成一列(docs/design/scenario-engine.md 第 6.2 節;第三十輪 ENG-C9)。
//
// 背景:核心的碰墊接觸判定沒有滯後,繫泊中船在纜繩上隨陣風擺盪時每離開碰墊再碰回就發一個 fenderContact 事件
// (E14 吹攏風 25 kn 不操作約 16 筆/min,2 小時 1956 筆),教官站事件流的 2000 筆上限約 2 小時就被它填滿、把故障與警報擠掉。
// 核心的事件序列不改(改了會影響時間表 when: event 與重播),合併只在教官站這一端做;核心的 JSON Lines 紀錄仍是逐筆。
//
// 規則(EventStore.push 套用;重播檢視與講評報告讀核心紀錄時用同一個 EventStore,結果相同):
//  - 事件本身沒有「接近接觸 / 再碰回」的標記(那是核心評估 BerthingTracker 依船體離開碰墊的距離判定的,只在 summary.berthing),
//    教官站改以時間與嚴重度判斷:一筆 fenderContact 與**同一個碰墊的上一筆接觸**相隔不超過門檻(FENDER_MERGE_GAP_S,模擬秒)、
//    未過載、接觸點法向速度不超過警示門檻(事件嚴重度 info)→ 併入該碰墊的合併列。
//  - 各自一列、不合併:該碰墊的第一次接觸、隔了超過門檻之後的再接觸(離開後再碰回)、過載(紅)、法向速度超過警示門檻(橙)。
//    這些列也結束該碰墊進行中的合併列,之後的反覆接觸另起一列(同一個碰墊的列依時間先後讀得通)。
//  - 別的事件(警報、VHF、其他碰墊的接觸…)**不打斷**合併列:門檻內的下一次接觸仍併入同一列。理由:每兩次擺盪之間只要夾一個別的事件
//    就另起一列的話,週期性的事件(警報重複提醒、VHF 通話、區域邊界進出)會把合併列切成和它們一樣多段,2 小時又回到上千列。
//  - 合併列以「最近一次接觸」為準:列的 tick / t / seq / phase / data 是最近一次接觸的,每併入一次就移到 **EventStore 清單**的最後
//    (清單維持依 t 遞增,也就是依最近活動的先後:2000 列上限先擠掉最久沒有活動的列,倒帶依 t 截斷);merged 欄帶次數、第一次的時刻與各次的最大值。
//  - **畫面上的位置另計**(第三十輪審查 BC-rBC-5):事件流元件依「第一次接觸」的時刻排(components/instructor/event-stream.ts displayRows),
//    合併列留在它建立時的位置、之後只在原地更新次數與最大值,不跳到最上面 —— 每併入一次就移到最上面的話,持續擺盪時清單可見的部分一直被
//    進行中的合併列占著,新的故障、警報幾秒就被推出去,畫面和合併前一樣吵。講評報告的時間軸同樣把合併列排在第一次接觸的時刻。
//  - 一列最多合併 FENDER_MERGE_MAX_COUNT 次,滿了另起一列:每列的逐次明細(下述)因此有上限。
//
// 倒帶(EventStore.dropAfter):合併列在記憶體裡另存每次接觸的精簡明細(每次 8 個數字與階段名稱,不存整個事件物件),
// 倒帶點落在合併列中間時依明細截到倒帶點為止,次數、最近時刻與最大值都重算 — 結果與「只收到倒帶點之前的事件」相同。
// 明細不寫進教官站紀錄(紀錄裡的合併列只有 merged 的彙總)。
// 明細只留最近活動的列:最近一次接觸比最新事件早超過 FENDER_MERGE_DETAIL_WINDOW_S(1 小時)的列丟掉明細(記憶體不隨練習時間成長;
// 25 kn 吹攏風每小時約 1000 次接觸、每次約 0.1 KB)。核心的倒帶快照預設只保留最近 10 分鐘(60 份 × 每 10 s),倒帶不會回到這些列的中間;
// 萬一倒帶點落在沒有明細的合併列中間(含由紀錄讀回的合併列),該列整列丟棄。

import type { FenderContactMerge, SimEvent } from '../types/event.js';
import { externalEventSeverity } from './external-forces.js';

/** 合併門檻(模擬秒):與同一個碰墊上一筆接觸相隔不超過此值才併入合併列。 */
export const FENDER_MERGE_GAP_S = 120;

/** 一列最多合併的次數(滿了另起一列)。 */
export const FENDER_MERGE_MAX_COUNT = 1000;

/** 逐次明細的保留時間(模擬秒):最近一次接觸比最新事件早超過此值的合併列丟掉明細(倒帶到它中間時整列丟棄)。 */
export const FENDER_MERGE_DETAIL_WINDOW_S = 3600;

export interface EventMergeOptions {
  /** 合併門檻(模擬秒;預設 FENDER_MERGE_GAP_S);0 或負值 = 不合併,每筆事件各自一列 */
  gap_s?: number;
  /** 一列最多合併的次數(預設 FENDER_MERGE_MAX_COUNT;至少 2) */
  maxCount?: number;
  /** 逐次明細的保留時間(模擬秒;預設 FENDER_MERGE_DETAIL_WINDOW_S;不小於合併門檻) */
  detailWindow_s?: number;
}

/** fenderContact 事件的碰墊 id(data.fenderId,缺時取 itemId);不是 fenderContact 回 null。 */
export function fenderIdOf(e: SimEvent): string | null {
  if (e.kind !== 'fenderContact') return null;
  const id = e.data?.['fenderId'];
  return typeof id === 'string' && id ? id : (e.itemId ?? '');
}

/** 可以併入合併列的接觸:未過載、法向速度不超過警示門檻(嚴重度 info),且不是讀回的已合併列。 */
export function isMergeableContact(e: SimEvent): boolean {
  return e.kind === 'fenderContact' && !e.merged && externalEventSeverity(e) === 'info';
}

/** 清單所代表的核心事件數:合併列算它合併的次數,其餘每列一筆。 */
export function rawEventCount(events: readonly SimEvent[]): number {
  let n = 0;
  for (const e of events) n += e.merged?.count ?? 1;
  return n;
}

/** 每次接觸存的數字:t、tick、seq,以及 data 的五個數值欄(缺的存 NaN)。 */
const STRIDE = 8;
const DATA_KEYS = ['normalSpeed_mps', 'energy_kNm', 'maxReaction_kN', 'maxDeflection_m', 'deflection_pct'] as const;
const COL_SPEED = 3;
const COL_ENERGY = 4;
const COL_REACTION = 5;
const COL_DEFLECTION_PCT = 7;

/**
 * 一個碰墊的一段反覆接觸:第一筆原始事件、每次接觸的精簡明細,以及目前放在事件清單裡的那一列(current)。
 * 只有一次時 current 就是原始事件(不帶 merged);兩次以上是「最近一次接觸的事件 + merged」。
 */
export class FenderContactRun {
  /** 目前放在事件清單裡的列物件(每次併入或截斷都換成新物件,清單的使用端不會看到被改寫的舊物件) */
  current: SimEvent;
  private readonly nums: number[] = [];
  private readonly phases: Array<string | undefined> = [];
  /** 最近一次接觸的事件(截斷後由明細重建) */
  private last: SimEvent;
  private n = 0;
  private detailed = true;

  constructor(
    readonly fenderId: string,
    /** 這一段的第一筆事件 */
    private first: SimEvent,
  ) {
    this.last = first;
    this.current = first;
    this.record(first);
  }

  /** 已合併的接觸次數 */
  get count(): number {
    return this.n;
  }

  /** 最近一次接觸的時刻 */
  get lastT(): number {
    return this.last.t;
  }

  /** 還留著逐次明細(可併入新的接觸、倒帶時可截斷) */
  get hasDetail(): boolean {
    return this.detailed;
  }

  /** 留著的明細筆數(診斷與測試用) */
  get detailCount(): number {
    return this.phases.length;
  }

  /** 丟掉逐次明細(久未活動的列):列本身不變;之後不再併入新的接觸,倒帶到它中間時只能整列丟棄。 */
  dropDetail(): void {
    this.detailed = false;
    this.nums.length = 0;
    this.phases.length = 0;
    // 第一筆與最近一筆的原始事件不再需要(只剩 lastT 要用):改指向列本身,讓它們可以回收
    this.first = this.current;
    this.last = this.current;
  }

  /** 併入一次接觸(e.t 不早於上一次;須仍有明細);回傳新的列物件。 */
  add(e: SimEvent): SimEvent {
    this.record(e);
    this.last = e;
    this.current = this.build();
    return this.current;
  }

  /**
   * 倒帶:丟掉 t > tMax 的接觸並重建列(次數、最近時刻、最大值都只算留下的);回傳丟掉的次數。
   * 全部丟掉時 count 為 0(呼叫端把整列移除)。
   */
  truncate(tMax: number): number {
    const n = this.n;
    if (!this.detailed) {
      // 沒有明細:最近一次接觸晚於倒帶點時整列丟棄
      if (this.last.t <= tMax) return 0;
      this.n = 0;
      return n;
    }
    let k = n;
    while (k > 0 && this.nums[(k - 1) * STRIDE]! > tMax) k--;
    if (k === n) return 0;
    this.nums.length = k * STRIDE;
    this.phases.length = k;
    this.n = k;
    if (k > 0) {
      this.last = k === 1 ? this.first : this.sampleEvent(k - 1);
      this.current = this.build();
    }
    return n - k;
  }

  private record(e: SimEvent): void {
    this.nums.push(e.t, e.tick, e.seq);
    for (const key of DATA_KEYS) {
      const v = e.data?.[key];
      this.nums.push(typeof v === 'number' && Number.isFinite(v) ? v : Number.NaN);
    }
    this.phases.push(e.phase);
    this.n++;
  }

  /** 某一欄的最大值(略過缺值);全部缺值回 undefined。 */
  private max(col: number): number | undefined {
    let best: number | undefined;
    for (let o = col; o < this.nums.length; o += STRIDE) {
      const v = this.nums[o]!;
      if (!Number.isNaN(v) && (best === undefined || v > best)) best = v;
    }
    return best;
  }

  private build(): SimEvent {
    const n = this.count;
    if (n === 1) return this.first;
    const merged: FenderContactMerge = {
      count: n,
      firstT: this.nums[0]!,
      firstTick: this.nums[1]!,
      lastT: this.last.t,
      maxNormalSpeed_mps: this.max(COL_SPEED) ?? 0,
      maxEnergy_kNm: this.max(COL_ENERGY) ?? 0,
    };
    const deflection = this.max(COL_DEFLECTION_PCT);
    if (deflection !== undefined) merged.maxDeflection_pct = deflection;
    const reaction = this.max(COL_REACTION);
    if (reaction !== undefined) merged.maxReaction_kN = reaction;
    return { ...this.last, merged };
  }

  /**
   * 由明細重建第 i 次接觸的事件:tick / t / seq / phase 與 data 的五個數值欄取自明細,其餘欄位(來源、碰墊 id、訊息、額定值…)
   * 同一個碰墊的未過載接觸都相同,取自第一筆。
   */
  private sampleEvent(i: number): SimEvent {
    const o = i * STRIDE;
    const data: Record<string, unknown> = { ...(this.first.data ?? {}) };
    DATA_KEYS.forEach((key, j) => {
      const v = this.nums[o + 3 + j]!;
      if (Number.isNaN(v)) delete data[key];
      else data[key] = v;
    });
    const ev: SimEvent = { ...this.first, t: this.nums[o]!, tick: this.nums[o + 1]!, seq: this.nums[o + 2]! };
    // 第一筆沒有 data、這一次也沒有任何數值欄時不補空的 data(與原事件相同)
    if (this.first.data !== undefined || Object.keys(data).length > 0) ev.data = data;
    const phase = this.phases[i];
    if (phase === undefined) delete ev.phase;
    else ev.phase = phase;
    return ev;
  }
}
