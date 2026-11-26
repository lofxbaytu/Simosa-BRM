// 重播檢視(講評站雛型,規劃書第 9.2 節)的紀錄解析:
//  - SimCore 的 JSON Lines 紀錄(src/SimCore/README.md 第 4 節;每行 kind = header | input | state | snapshot | event | summary | session | vhf | footer;
//    vhf 行為內建 VHF 角色扮演的通話/文字/DSC(帶外、不在事件序列內),其 event 併入 events 供講評時間軸與通訊統計);
//  - 教官站自己的 SessionRecord JSON(session-record.ts;含 BRM 標記、核心事件與核心 summary)。
// 解析後提供依時間查狀態(二分搜尋)與航跡點,供時間軸拖曳檢視;不需核心連線。
// 未知的 kind 只計數(不逐行列為錯誤),舊紀錄(無 event/summary 行、header 無 parameterSet)仍可載入。
// 倒帶(restore/reset):核心紀錄在倒帶後 state 的 tick/t 會倒退;解析時依 tick 遞減切分,倒帶時丟棄 t > 倒帶點的舊分支狀態與事件
// (只保留練習最終的歷史),倒帶點記在 rewinds[];不對 states 做全域排序(設計文件 D31)。
// reset(重設)不是倒帶到某個快照,而是整個練習從 tick 0 重新開始:舊分支的狀態與事件**全部**作廢(不是只丟 t > 倒帶後第一筆狀態的那些);
// 核心在同一個 tick 先寫事件行、再寫狀態行,所以 reset 的 input 行之後、下一筆 state 行之前的事件屬於新分支(例:開始即繫妥的 E14,
// 四個碰墊在 tick 2 的第一次接觸)—— 先暫存,確認 tick 倒退(重設生效)後清掉舊分支再放回(第三十輪審查 A-rA-5:原本舊分支 tick ≤ 2 的
// 事件會留下來,與新分支的同一批事件重複,報告的合併次數多 1)。
// 倒帶點的 tick 取自 input 行的 restore args.tick(無 args 時最近一筆 snapshot 行的 tick):C# Restore 後第一筆 state 行晚
// BroadcastDivider 個 tick 才寫出,直接用該 state 的 t 會把倒帶標記畫晚、也漏丟該區間的舊分支事件。
//
// 兩種格式各有一個逐筆的建構器(JsonLinesReplayBuilder 逐行、SessionReplayBuilder 逐筆狀態):整份文字的 parseReplay() 與
// 分段讀取的 record-stream.ts(ReplayStreamParser;講評報告頁不把整份紀錄讀成一個字串)共用同一份邏輯,結果相同。
// 建構器可帶「狀態投影」:解析後每筆狀態只留呼叫端需要的欄位(講評報告頁只需要講評累計讀取的欄位,見 debrief.ts DebriefState)。
// 狀態放在「狀態存放」(ReplayStateStore)裡:預設是一個陣列(ArrayStateStore;每筆都留,可帶投影);重播檢視改用
// lib/replay-states.ts 的 ReplayHistoryStore(保留的筆數有上限、超過才降採樣,摘要 / 航跡 / 事件位置仍以全部狀態計算)。
// 倒帶的判斷與丟棄(狀態、事件、rewinds[])都在建構器裡,存放端只負責「加入一筆 / 丟掉 t 之後的 / 全部丟掉」,兩種存放的倒帶結果相同。
//
// 事件(events):與教官站即時事件流相同的列 — 同一個碰墊連續的低速接觸合併成一列(SimEvent.merged;規則見 lib/event-merge.ts)。
// 核心 JSON Lines 紀錄仍是逐筆的 fenderContact,讀入時經同一個 EventStore 合併(倒帶時合併列截到倒帶點);教官站紀錄版本 6 起
// 已是合併後的列,原樣讀回;版本 5 以前的教官站紀錄(逐筆)讀入時合併。重播檢視與講評報告因此與教官站即時看到的列相同。

import type { SimCommand } from '../types/command.js';
import type { SimEvent } from '../types/event.js';
import type { OwnShipState } from '../types/state.js';
import { parseAssessmentSummary, parseRecordParameterSet, type AssessmentSummary, type RecordParameterSet } from './assessment.js';
import type { BrmMarker } from './brm-markers.js';
import { isValidCommand } from './command.js';
import { parseState } from './state-parser.js';
import { isSessionRecord, type SessionRecord } from './session-record.js';
import { parseSessionInfo, type SessionInfo } from './session-info.js';
import { parseRecordings, type RecordingSummary } from './screen-recording.js';
import { EventStore, mergeEventRows, parseSimEvent } from './sim-event.js';
import type { TrackPoint } from './track-history.js';
import { BrmMarkerStore } from './brm-markers.js';
import type { OperationEvent } from './operation-log.js';

export interface ReplayHeader {
  version?: number;
  createdUtc?: string;
  /** 練習開始時刻(教官站紀錄 startedUtc;核心紀錄無) */
  startedUtc?: string;
  shipId?: string;
  shipName?: string;
  loading?: string;
  seed?: number;
  /** 核心步長(秒),輸入事件 tick → 時間用 */
  dt: number;
  dynamics?: string;
  scenarioId?: string;
  scenarioName?: string;
  /** header.scenario:解析後的完整情境(version 2 含 timeline/route/zones/assessment/briefing) */
  scenario?: unknown;
  /** header.parameterSet(version 2;無參數集時省略) */
  parameterSet?: RecordParameterSet;
  /** 參與者與休息時間確認:教官站紀錄 sessionInfo,或核心紀錄 header 的 participants / restHoursConfirmation / course(規劃書第 5.5 節) */
  sessionInfo?: SessionInfo;
  /** 畫面錄影摘要(教官站紀錄 version 4 的 recordings;核心 JSON Lines 紀錄沒有,錄影由紀錄包旁檔關聯) */
  recordings?: RecordingSummary[];
}

export interface ReplayInput {
  tick: number;
  /** 由 tick × dt 推得(SessionRecord 則直接有 t) */
  t: number;
  command: SimCommand;
  /** 核心回應(SessionRecord) */
  ack?: { ok: boolean; detail?: string };
}

export interface ReplayFooter {
  finalTick?: number;
  stateHash?: string;
  lines?: number;
  inputs?: number;
  sha256?: string;
}

/** 倒帶點:自 from(倒帶前最後一筆狀態的 t)回到 t(= tick × dt) */
export interface ReplayRewind {
  t: number;
  /** 倒帶到的核心 tick(restore args.tick / snapshot tick;無法得知時為倒帶後第一筆狀態的 tick) */
  tick: number;
  from: number;
  /** 倒帶時丟棄的舊分支狀態數 */
  droppedStates: number;
  /** 倒帶時丟棄的舊分支事件數 */
  droppedEvents: number;
}

/** 倒帶判斷需要的狀態欄位(狀態投影至少要保留這兩欄) */
export interface ReplayStateBase {
  t: number;
  tick: number;
}

/** 狀態投影:解析出的完整狀態 → 要保留的欄位(回傳的物件會被長期持有;省略 = 保留完整狀態) */
export type StateProjection<S extends ReplayStateBase> = (state: OwnShipState) => S;

/**
 * 建構器存放狀態的地方(練習最終的歷史;倒帶的舊分支由建構器呼叫 dropAfter / clear 丟掉)。
 * length / first / last 指的是**全部**狀態(存放端可以只保留其中一部分給 finish();倒帶判斷、丟棄筆數、tStart / tEnd 都以全部狀態為準)。
 */
export interface ReplayStateStore<S extends ReplayStateBase> {
  /** 目前歷史的狀態筆數 */
  readonly length: number;
  /** 第一筆狀態(只需要 t / tick);沒有狀態時 undefined */
  first(): ReplayStateBase | undefined;
  /** 最後一筆狀態(只需要 t / tick);沒有狀態時 undefined */
  last(): ReplayStateBase | undefined;
  /** 加入一筆解析出的完整狀態(要保留哪些欄位、保留哪幾筆由存放端決定) */
  push(state: OwnShipState): void;
  /** 丟棄 t > t 的狀態(倒帶:與 Array.filter((x) => x.t <= t) 相同);回傳丟棄的筆數 */
  dropAfter(t: number): number;
  /** 全部丟棄(重設,或重新開始解析) */
  clear(): void;
  /** 讀完:要放進 ReplayData.states 的狀態(依時間遞增) */
  finish(): S[];
}

/** 每個建構器各要一個存放(分段解析器同時準備兩種格式的建構器,只有其中一個會用到) */
export type ReplayStateStoreFactory<S extends ReplayStateBase> = () => ReplayStateStore<S>;

/** 預設的狀態存放:每一筆都留在陣列裡(可帶投影)。講評報告頁與 parseReplay() 用這一種。 */
export class ArrayStateStore<S extends ReplayStateBase = OwnShipState> implements ReplayStateStore<S> {
  private states: S[] = [];

  constructor(private readonly project?: StateProjection<S>) {}

  get length(): number {
    return this.states.length;
  }

  first(): S | undefined {
    return this.states[0];
  }

  last(): S | undefined {
    return this.states[this.states.length - 1];
  }

  push(state: OwnShipState): void {
    this.states.push(this.project ? this.project(state) : (state as unknown as S));
  }

  dropAfter(t: number): number {
    const n = this.states.length;
    this.states = this.states.filter((x) => x.t <= t);
    return n - this.states.length;
  }

  clear(): void {
    this.states = [];
  }

  finish(): S[] {
    return this.states;
  }
}

/** 建構器的狀態去向:投影(存進預設的陣列)或自備的存放;省略 = 保留完整狀態的陣列。 */
export type ReplayStateTarget<S extends ReplayStateBase> = StateProjection<S> | ReplayStateStore<S>;

function storeOf<S extends ReplayStateBase>(target: ReplayStateTarget<S> | undefined): ReplayStateStore<S> {
  return typeof target === 'object' ? target : new ArrayStateStore<S>(target);
}

export interface ReplayDataOf<S> {
  source: 'simcore-jsonl' | 'instructor-json';
  header: ReplayHeader;
  /** 練習最終歷史的狀態(依時間遞增;倒帶的舊分支已丟棄) */
  states: S[];
  /** 倒帶點(restore/reset;依發生順序) */
  rewinds: ReplayRewind[];
  inputs: ReplayInput[];
  snapshots: Array<{ tick: number; t: number }>;
  markers: BrmMarker[];
  operations: OperationEvent[];
  /** 核心事件(event 行;教官站紀錄的 events),依時間遞增;同一個碰墊連續的低速接觸已合併成一列(SimEvent.merged) */
  events: SimEvent[];
  /** 核心 summary(最後一行 summary;教官站紀錄的 coreSummary);舊紀錄為 null */
  summary: AssessmentSummary | null;
  footer: ReplayFooter | null;
  /** 解析時略過的行與原因 */
  errors: string[];
  /** 未知 kind 的行數(kind → 次數) */
  unknownKinds: Record<string, number>;
  tStart: number;
  tEnd: number;
}

/** 完整狀態的重播資料(重播檢視、測試);講評報告頁用 ReplayDataOf<DebriefState>。 */
export type ReplayData = ReplayDataOf<OwnShipState>;

const DEFAULT_DT = 0.02;

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/** 判斷文字是 JSON Lines(多行、每行一個物件)還是單一 JSON 文件。 */
export function detectRecordFormat(text: string): 'jsonl' | 'json' | 'unknown' {
  const trimmed = text.trimStart();
  if (!trimmed.startsWith('{')) return 'unknown';
  const firstNl = trimmed.indexOf('\n');
  if (firstNl < 0) {
    // 單行:可能是只有 header 的 JSON Lines 或壓成一行的 JSON
    try {
      const obj = JSON.parse(trimmed) as unknown;
      return isRecord(obj) && typeof obj['kind'] === 'string' ? 'jsonl' : 'json';
    } catch {
      return 'unknown';
    }
  }
  const firstLine = trimmed.slice(0, firstNl).trim();
  try {
    const obj = JSON.parse(firstLine) as unknown;
    return isRecord(obj) && typeof obj['kind'] === 'string' ? 'jsonl' : 'json';
  } catch {
    return 'json';
  }
}

/** 解析紀錄文字(自動判斷格式)。 */
export function parseReplay(text: string): ReplayData {
  const fmt = detectRecordFormat(text);
  if (fmt === 'jsonl') return parseJsonLines(text);
  if (fmt === 'json') return parseSessionJson(text);
  return unrecognizedReplay();
}

function empty<S>(source: ReplayData['source'], errors: string[]): ReplayDataOf<S> {
  return { source, header: { dt: DEFAULT_DT }, states: [], rewinds: [], inputs: [], snapshots: [], markers: [], operations: [], events: [], summary: null, footer: null, errors, unknownKinds: {}, tStart: 0, tEnd: 0 };
}

/** 無法辨識格式時的結果(parseReplay 與分段解析共用同一則訊息)。 */
export function unrecognizedReplay<S>(): ReplayDataOf<S> {
  return empty('simcore-jsonl', ['無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)']);
}

/** 單一 JSON 文件解析失敗時的結果。 */
export function invalidJsonReplay<S>(): ReplayDataOf<S> {
  return empty('instructor-json', ['JSON 解析失敗']);
}

/** JSON 文件不是教官站紀錄(format 不符或沒有 states 陣列)時的結果。 */
export function notSessionRecordReplay<S>(): ReplayDataOf<S> {
  return empty('instructor-json', ['不是教官站紀錄(format 欄位不符)']);
}

/**
 * 依序加入一筆狀態;tick(或 t)比前一筆小即為倒帶:丟棄 t > 倒帶點的舊分支狀態與事件(倒帶後核心從快照 tick 重新推進,
 * 快照 tick 當下的狀態與之前的歷史仍然有效),並記錄倒帶點。
 * rewindTick:restore 指令要求的 tick(已知時倒帶點 = rewindTick × dt,而非倒帶後第一筆狀態的 t)。
 * dropEvents:丟棄 t > 倒帶點的事件並回傳丟棄的事件數(核心紀錄:事件在 EventStore 裡,合併列截到倒帶點;省略時直接濾 data.events)。
 */
function pushState<S extends ReplayStateBase>(data: ReplayDataOf<S>, store: ReplayStateStore<S>, s: OwnShipState, rewindTick?: number, dropEvents?: (t: number) => number): void {
  const prev = store.last();
  if (prev && wentBack(prev, s)) {
    const known = rewindTick !== undefined && Number.isFinite(rewindTick) && rewindTick <= s.tick;
    const tick = known ? rewindTick : s.tick;
    const t = known ? tick * data.header.dt : s.t;
    const from = prev.t;
    const droppedStates = store.dropAfter(t);
    let droppedEvents: number;
    if (dropEvents) droppedEvents = dropEvents(t);
    else {
      const nEvents = data.events.length;
      data.events = data.events.filter((e) => e.t <= t);
      droppedEvents = nEvents - data.events.length;
    }
    data.rewinds.push({ t, tick, from, droppedStates, droppedEvents });
  }
  store.push(s);
}

/** s 的 tick(沒有 tick 時比 t)比前一筆 prev 小:倒帶或重設之後的第一筆狀態。 */
function wentBack(prev: ReplayStateBase, s: ReplayStateBase): boolean {
  return Number.isFinite(s.tick) && Number.isFinite(prev.tick) ? s.tick < prev.tick : s.t < prev.t;
}

/** 讀完:狀態由存放端交出,tStart / tEnd 取全部狀態的第一筆與最後一筆(存放端只保留一部分時也一樣)。 */
function finishStates<S extends ReplayStateBase>(data: ReplayDataOf<S>, store: ReplayStateStore<S>): ReplayDataOf<S> {
  if (store.length > 0) {
    data.tStart = store.first()!.t;
    data.tEnd = store.last()!.t;
  }
  data.states = store.finish();
  return data;
}

/**
 * SimCore JSON Lines 的逐行建構器:addLine() 每行呼叫一次(行號 i 由 0 起,錯誤訊息寫「第 i + 1 行」),壞行記入 errors 後繼續;
 * 讀完呼叫 finish()。parseJsonLines(整份文字)與 record-stream.ts(分段讀取)共用。
 */
export class JsonLinesReplayBuilder<S extends ReplayStateBase = OwnShipState> {
  private readonly data: ReplayDataOf<S> = empty('simcore-jsonl', []);
  /** 上一筆解析出的完整狀態(parseState 以它補缺漏的欄位;與投影後存入 states 的物件分開) */
  private prev: OwnShipState | undefined;
  /** 最近一筆 restore 指令要求的 tick;下一筆 state 行(倒帶後第一筆)消耗之 */
  private pendingRewindTick: number | undefined;
  /** 事件先進 EventStore(不設上限):與教官站即時事件流相同的合併規則;finish() 時才放進 data.events */
  private readonly eventStore = new EventStore(Number.POSITIVE_INFINITY);
  private readonly dropEvents = (t: number): number => this.eventStore.dropAfter(t);
  /**
   * reset 指令的 input 行之後、下一筆 state 行之前的事件(沒有待確認的重設時 null):那一筆狀態的 tick 倒退(重設生效)時它們屬於新分支,
   * 清掉舊分支後才放進 EventStore;沒有倒退時照原順序放回。
   */
  private afterReset: SimEvent[] | null = null;

  private readonly store: ReplayStateStore<S>;

  /** target:狀態投影或自備的狀態存放(省略 = 每筆保留完整狀態)。 */
  constructor(target?: ReplayStateTarget<S>) {
    this.store = storeOf(target);
  }

  /** 事件進 EventStore;有待確認的重設時先暫存(見 afterReset)。 */
  private pushEvent(ev: SimEvent): void {
    if (this.afterReset) this.afterReset.push(ev);
    else this.eventStore.push(ev);
  }

  /** 一行文字(不含換行;前後空白會去掉,空行略過)。 */
  addLine(line: string, i: number): void {
    const s = line.trim();
    if (!s) return;
    let obj: unknown;
    try {
      obj = JSON.parse(s);
    } catch {
      this.data.errors.push(`第 ${i + 1} 行:JSON 解析失敗`);
      return;
    }
    this.addObject(obj, i);
  }

  /** 已解析的一行(JSON.parse 的結果)。 */
  addObject(obj: unknown, i: number): void {
    const data = this.data;
    if (!isRecord(obj)) {
      data.errors.push(`第 ${i + 1} 行:不是物件`);
      return;
    }
    switch (obj['kind']) {
      case 'header': {
        const h: ReplayHeader = { dt: typeof obj['dt'] === 'number' && obj['dt'] > 0 ? obj['dt'] : DEFAULT_DT };
        if (typeof obj['version'] === 'number') h.version = obj['version'];
        if (typeof obj['createdUtc'] === 'string') h.createdUtc = obj['createdUtc'];
        if (typeof obj['shipId'] === 'string') h.shipId = obj['shipId'];
        if (typeof obj['shipName'] === 'string') h.shipName = obj['shipName'];
        if (typeof obj['loading'] === 'string') h.loading = obj['loading'];
        if (typeof obj['seed'] === 'number') h.seed = obj['seed'];
        if (typeof obj['dynamics'] === 'string') h.dynamics = obj['dynamics'];
        if (isRecord(obj['scenario'])) {
          h.scenario = obj['scenario'];
          const sc = obj['scenario'];
          if (typeof sc['id'] === 'string') h.scenarioId = sc['id'];
          if (typeof sc['name'] === 'string') h.scenarioName = sc['name'];
        }
        const ps = parseRecordParameterSet(obj['parameterSet']);
        if (ps) h.parameterSet = ps;
        // 核心把 sessionInfo 指令的 args 攤平寫在標頭(packaging-sync.md 建議欄位);也接受整包 sessionInfo 物件
        const si = parseSessionInfo(isRecord(obj['sessionInfo']) ? obj['sessionInfo'] : { participants: obj['participants'], restHoursConfirmation: obj['restHoursConfirmation'], course: obj['course'] });
        if (si) h.sessionInfo = si;
        data.header = h;
        break;
      }
      case 'event': {
        const ev = parseSimEvent(obj['event'], data.header.dt);
        if (!ev) {
          data.errors.push(`第 ${i + 1} 行:event 無法解析`);
          return;
        }
        if (typeof obj['tick'] === 'number' && !Number.isFinite(ev.tick)) ev.tick = obj['tick'];
        this.pushEvent(ev);
        break;
      }
      case 'vhf': {
        // VHF 角色扮演行(docs/design/vhf-roleplay.md 第 4 節):event 為 SimEvent 形狀(source "vhf"),列入事件供講評;
        // 行的 tick/t 為轉送當下的引擎時刻,event 缺 tick/t 時補上
        const raw = isRecord(obj['event']) ? { ...obj['event'] } : null;
        if (raw) {
          if (typeof raw['tick'] !== 'number' && typeof obj['tick'] === 'number') raw['tick'] = obj['tick'];
          if (typeof raw['t'] !== 'number' && typeof obj['t'] === 'number') raw['t'] = obj['t'];
        }
        const ev = parseSimEvent(raw, data.header.dt);
        if (!ev) {
          data.errors.push(`第 ${i + 1} 行:vhf 無法解析`);
          return;
        }
        this.pushEvent(ev);
        break;
      }
      case 'summary': {
        const sum = parseAssessmentSummary(obj['summary']);
        if (!sum) {
          data.errors.push(`第 ${i + 1} 行:summary 無法解析`);
          return;
        }
        // 取最後一行(endExercise 時寫一行、Close 時再寫 final 一行)
        data.summary = sum;
        break;
      }
      case 'input': {
        const tick = typeof obj['tick'] === 'number' ? obj['tick'] : NaN;
        const cmd = obj['command'];
        if (!Number.isFinite(tick) || !isValidCommand(cmd)) {
          data.errors.push(`第 ${i + 1} 行:input 缺 tick 或 command 無效`);
          return;
        }
        data.inputs.push({ tick, t: tick * data.header.dt, command: cmd });
        if (cmd.type === 'restore') {
          const want = cmd.args?.['tick'];
          this.pendingRewindTick = typeof want === 'number' && Number.isFinite(want) ? want : data.snapshots[data.snapshots.length - 1]?.tick;
        } else if (cmd.type === 'reset') this.afterReset ??= [];
        break;
      }
      case 'state': {
        const parsed = parseState(obj['state'], this.prev);
        if (!parsed) {
          data.errors.push(`第 ${i + 1} 行:state 無法解析`);
          return;
        }
        this.prev = parsed.state;
        const s = parsed.state;
        const store = this.store;
        const fresh = this.afterReset;
        this.afterReset = null;
        const last = store.last();
        if (fresh && last && wentBack(last, s)) {
          // 重設生效:舊分支的狀態與事件全部作廢(倒帶點照舊記在重設後第一筆狀態),重設之後才發的事件放回
          const droppedEvents = this.eventStore.dropAfter(Number.NEGATIVE_INFINITY);
          data.rewinds.push({ t: s.t, tick: s.tick, from: last.t, droppedStates: store.length, droppedEvents });
          store.clear();
          store.push(s);
          for (const ev of fresh) this.eventStore.push(ev);
        } else {
          if (fresh) for (const ev of fresh) this.eventStore.push(ev);
          pushState(data, store, s, this.pendingRewindTick, this.dropEvents);
        }
        this.pendingRewindTick = undefined;
        break;
      }
      case 'snapshot': {
        const tick = typeof obj['tick'] === 'number' ? obj['tick'] : NaN;
        if (!Number.isFinite(tick)) {
          data.errors.push(`第 ${i + 1} 行:snapshot 缺 tick`);
          return;
        }
        data.snapshots.push({ tick, t: tick * data.header.dt });
        break;
      }
      case 'footer': {
        const f: ReplayFooter = {};
        if (typeof obj['finalTick'] === 'number') f.finalTick = obj['finalTick'];
        if (typeof obj['stateHash'] === 'string') f.stateHash = obj['stateHash'];
        if (typeof obj['lines'] === 'number') f.lines = obj['lines'];
        if (typeof obj['inputs'] === 'number') f.inputs = obj['inputs'];
        if (typeof obj['sha256'] === 'string') f.sha256 = obj['sha256'];
        data.footer = f;
        break;
      }
      default: {
        const kind = String(obj['kind']);
        data.unknownKinds[kind] = (data.unknownKinds[kind] ?? 0) + 1;
      }
    }
  }

  finish(): ReplayDataOf<S> {
    // 紀錄結束在 reset 之後、下一筆狀態之前:分不出重設有沒有生效,事件照原順序放回
    if (this.afterReset) for (const ev of this.afterReset) this.eventStore.push(ev);
    this.afterReset = null;
    this.data.events = [...this.eventStore.all()];
    return finishStates(this.data, this.store);
  }
}

/** SimCore JSON Lines:逐行解析,壞行記入 errors 後繼續。 */
export function parseJsonLines(text: string): ReplayData {
  const b = new JsonLinesReplayBuilder();
  text.split(/\r?\n/).forEach((line, i) => b.addLine(line, i));
  return b.finish();
}

/**
 * 教官站 SessionRecord JSON 的建構器:states 逐筆 addState()(i = 在 states 陣列中的索引),其餘頂層欄位在 finish() 一次給。
 * parseSessionJson(整份文字)與 record-stream.ts(分段讀取:states 不必整個留在記憶體)共用。
 */
export class SessionReplayBuilder<S extends ReplayStateBase = OwnShipState> {
  private data: ReplayDataOf<S> = empty('instructor-json', []);
  private prev: OwnShipState | undefined;
  private readonly store: ReplayStateStore<S>;

  /** target:狀態投影或自備的狀態存放(省略 = 每筆保留完整狀態)。 */
  constructor(target?: ReplayStateTarget<S>) {
    this.store = storeOf(target);
  }

  /** 丟掉已加入的狀態重新開始(JSON 的重複鍵以後者為準)。 */
  resetStates(): void {
    this.data = empty('instructor-json', []);
    this.prev = undefined;
    this.store.clear();
  }

  addState(raw: unknown, i: number): void {
    const parsed = parseState(raw, this.prev);
    if (!parsed) {
      this.data.errors.push(`states[${i}] 無法解析`);
      return;
    }
    this.prev = parsed.state;
    pushState(this.data, this.store, parsed.state);
  }

  /** rest:紀錄的頂層物件(states 欄不讀;呼叫端已確認 format 與 states 為陣列)。 */
  finish(rest: Omit<SessionRecord, 'states'>): ReplayDataOf<S> {
    const obj = rest;
    const data = this.data;
    const sc = obj.scenario;
    const h: ReplayHeader = { dt: DEFAULT_DT };
    h.createdUtc = obj.createdUtc;
    if (typeof obj.startedUtc === 'string' && obj.startedUtc) h.startedUtc = obj.startedUtc;
    h.shipId = obj.shipId;
    if (sc) {
      h.scenarioId = sc.id;
      h.scenarioName = sc.name;
      h.loading = sc.loading;
      h.seed = sc.seed;
      h.scenario = sc;
    }
    h.dynamics = `教官站紀錄(${obj.mode === 'simple' ? '簡化教官模式' : '教官站'},來源 ${obj.source})`;
    const si = parseSessionInfo(obj.sessionInfo);
    if (si) h.sessionInfo = si;
    const recordings = parseRecordings((obj as { recordings?: unknown }).recordings);
    if (recordings.length > 0) h.recordings = recordings;
    data.header = h;
    // 版本 5:教官站已在倒帶當下截掉舊分支抽樣(states 的 t 不再倒退),倒帶點另存於 rewinds[](依發生順序)
    for (const rw of Array.isArray(obj.rewinds) ? obj.rewinds : []) {
      if (!isRecord(rw) || typeof rw['t'] !== 'number' || !Number.isFinite(rw['t'])) continue;
      const num = (v: unknown, fallback: number) => (typeof v === 'number' && Number.isFinite(v) ? v : fallback);
      data.rewinds.push({ t: rw['t'], tick: num(rw['tick'], NaN), from: num(rw['from'], rw['t']), droppedStates: num(rw['droppedStates'], 0), droppedEvents: num(rw['droppedEvents'], 0) });
    }
    // 損壞的檔案:commands 不是陣列或元素不是物件時略過(不丟例外)
    for (const c of Array.isArray(obj.commands) ? obj.commands : []) {
      if (!isRecord(c) || !isValidCommand(c.command)) continue;
      const input: ReplayInput = { tick: typeof c.ack?.tick === 'number' ? c.ack.tick : Math.round(c.t / DEFAULT_DT), t: c.t, command: c.command };
      if (c.ack) {
        const ack: { ok: boolean; detail?: string } = { ok: c.ack.ok };
        if (c.ack.detail) ack.detail = c.ack.detail;
        input.ack = ack;
      }
      data.inputs.push(input);
    }
    const store = new BrmMarkerStore();
    store.load(obj.markers ?? []);
    data.markers = [...store.all()];
    data.operations = Array.isArray(obj.operations) ? obj.operations.filter((o) => typeof o === 'object' && o !== null && typeof o.t === 'number') : [];
    if (Array.isArray(obj.events)) {
      // 教官站紀錄的事件在狀態之後才整批加入:倒帶點之前的舊分支事件(t > 倒帶點且早於倒帶發生)一併丟棄
      // 之後照教官站事件流的規則合併碰墊的反覆接觸(版本 6 起紀錄裡已是合併列,原樣保留;版本 5 以前是逐筆)
      const events = obj.events.map((e) => parseSimEvent(e, DEFAULT_DT)).filter((e): e is SimEvent => e !== null);
      data.events = mergeEventRows(dropRewoundEvents(events, data.rewinds));
    }
    if (obj.coreSummary) data.summary = parseAssessmentSummary(obj.coreSummary);
    return finishStates(data, this.store);
  }
}

/** 教官站 SessionRecord JSON。 */
export function parseSessionJson(text: string): ReplayData {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return invalidJsonReplay();
  }
  if (!isSessionRecord(obj)) return notSessionRecordReplay();
  const b = new SessionReplayBuilder();
  obj.states.forEach((raw, i) => b.addState(raw, i));
  return b.finish(obj);
}

/** 依倒帶點(依發生順序)逐段丟棄舊分支事件:事件依檔案順序,倒帶前 t > 倒帶點者屬舊分支。 */
function dropRewoundEvents(events: readonly SimEvent[], rewinds: readonly ReplayRewind[]): SimEvent[] {
  if (rewinds.length === 0) return [...events];
  let out: SimEvent[] = [];
  let ri = 0;
  for (const e of events) {
    // 事件 t 回到倒帶點之下(或等於)且前一筆更晚 → 到達下一個倒帶點
    while (ri < rewinds.length && out.length > 0 && e.t < out[out.length - 1]!.t && e.t <= rewinds[ri]!.from) {
      const r = rewinds[ri]!;
      out = out.filter((x) => x.t <= r.t);
      ri++;
    }
    out.push(e);
  }
  return out;
}

/** t 時刻(含)之前最後一筆事件的索引(事件依時間遞增;倒帶分支已於解析時丟棄);沒有回傳 -1。 */
export function eventIndexAt(events: readonly SimEvent[], t: number): number {
  let idx = -1;
  for (let i = 0; i < events.length; i++) {
    if (events[i]!.t <= t) idx = i;
  }
  return idx;
}

/** 二分搜尋:t 時刻(含)之前最後一筆狀態的索引;t 早於第一筆時回傳 0;沒有狀態回傳 -1(狀態或教官站抽樣皆可)。 */
export function stateIndexAt(states: readonly { readonly t: number }[], t: number): number {
  if (states.length === 0) return -1;
  let lo = 0;
  let hi = states.length - 1;
  if (t <= states[0]!.t) return 0;
  if (t >= states[hi]!.t) return hi;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (states[mid]!.t <= t) lo = mid;
    else hi = mid - 1;
  }
  return lo;
}

/** 航跡點(每 interval 秒一點),供鳥瞰圖畫到任一時刻為止。 */
export function trackPoints(states: readonly Pick<OwnShipState, 't' | 'pos'>[], interval_s = 1): TrackPoint[] {
  const pts: TrackPoint[] = [];
  let last = Number.NEGATIVE_INFINITY;
  for (const s of states) {
    if (s.t - last < interval_s && pts.length > 0) continue;
    pts.push({ x: s.pos.x, y: s.pos.y, t: s.t });
    last = s.t;
  }
  return pts;
}
