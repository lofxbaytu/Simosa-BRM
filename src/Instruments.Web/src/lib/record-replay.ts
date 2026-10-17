// 重播檢視(講評站雛型,規劃書第 9.2 節)的紀錄解析:
//  - SimCore 的 JSON Lines 紀錄(src/SimCore/README.md 第 4 節;每行 kind = header | input | state | snapshot | event | summary | footer);
//  - 教官站自己的 SessionRecord JSON(session-record.ts;含 BRM 標記、核心事件與核心 summary)。
// 解析後提供依時間查狀態(二分搜尋)與航跡點,供時間軸拖曳檢視;不需核心連線。
// 未知的 kind 只計數(不逐行列為錯誤),舊紀錄(無 event/summary 行、header 無 parameterSet)仍可載入。
// 倒帶(restore/reset):核心紀錄在倒帶後 state 的 tick/t 會倒退;解析時依 tick 遞減切分,倒帶時丟棄 t > 倒帶點的舊分支狀態與事件
// (只保留練習最終的歷史),倒帶點記在 rewinds[];不對 states 做全域排序(設計文件 D31)。
// 倒帶點的 tick 取自 input 行的 restore args.tick(無 args 時最近一筆 snapshot 行的 tick):C# Restore 後第一筆 state 行晚
// BroadcastDivider 個 tick 才寫出,直接用該 state 的 t 會把倒帶標記畫晚、也漏丟該區間的舊分支事件。

import type { SimCommand } from '../types/command.js';
import type { SimEvent } from '../types/event.js';
import type { OwnShipState } from '../types/state.js';
import { parseAssessmentSummary, parseRecordParameterSet, type AssessmentSummary, type RecordParameterSet } from './assessment.js';
import type { BrmMarker } from './brm-markers.js';
import { isValidCommand } from './command.js';
import { parseState } from './state-parser.js';
import { isSessionRecord } from './session-record.js';
import { parseSimEvent } from './sim-event.js';
import type { TrackPoint } from './track-history.js';
import { BrmMarkerStore } from './brm-markers.js';
import type { OperationEvent } from './operation-log.js';

export interface ReplayHeader {
  version?: number;
  createdUtc?: string;
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

export interface ReplayData {
  source: 'simcore-jsonl' | 'instructor-json';
  header: ReplayHeader;
  /** 練習最終歷史的狀態(依時間遞增;倒帶的舊分支已丟棄) */
  states: OwnShipState[];
  /** 倒帶點(restore/reset;依發生順序) */
  rewinds: ReplayRewind[];
  inputs: ReplayInput[];
  snapshots: Array<{ tick: number; t: number }>;
  markers: BrmMarker[];
  operations: OperationEvent[];
  /** 核心事件(event 行;教官站紀錄的 events) */
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
  return empty('simcore-jsonl', ['無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)']);
}

function empty(source: ReplayData['source'], errors: string[]): ReplayData {
  return { source, header: { dt: DEFAULT_DT }, states: [], rewinds: [], inputs: [], snapshots: [], markers: [], operations: [], events: [], summary: null, footer: null, errors, unknownKinds: {}, tStart: 0, tEnd: 0 };
}

/**
 * 依序加入一筆狀態;tick(或 t)比前一筆小即為倒帶:丟棄 t > 倒帶點的舊分支狀態與事件(倒帶後核心從快照 tick 重新推進,
 * 快照 tick 當下的狀態與之前的歷史仍然有效),並記錄倒帶點。
 * rewindTick:restore 指令要求的 tick(已知時倒帶點 = rewindTick × dt,而非倒帶後第一筆狀態的 t)。
 */
function pushState(data: ReplayData, s: OwnShipState, rewindTick?: number): void {
  const prev = data.states[data.states.length - 1];
  if (prev && (Number.isFinite(s.tick) && Number.isFinite(prev.tick) ? s.tick < prev.tick : s.t < prev.t)) {
    const known = rewindTick !== undefined && Number.isFinite(rewindTick) && rewindTick <= s.tick;
    const tick = known ? rewindTick : s.tick;
    const t = known ? tick * data.header.dt : s.t;
    const nStates = data.states.length;
    const nEvents = data.events.length;
    data.states = data.states.filter((x) => x.t <= t);
    data.events = data.events.filter((e) => e.t <= t);
    data.rewinds.push({ t, tick, from: prev.t, droppedStates: nStates - data.states.length, droppedEvents: nEvents - data.events.length });
  }
  data.states.push(s);
}

function finishTimes(data: ReplayData): ReplayData {
  if (data.states.length > 0) {
    data.tStart = data.states[0]!.t;
    data.tEnd = data.states[data.states.length - 1]!.t;
  }
  return data;
}

/** SimCore JSON Lines:逐行解析,壞行記入 errors 後繼續。 */
export function parseJsonLines(text: string): ReplayData {
  const data = empty('simcore-jsonl', []);
  const lines = text.split(/\r?\n/);
  let prev: OwnShipState | undefined;
  /** 最近一筆 restore 指令要求的 tick;下一筆 state 行(倒帶後第一筆)消耗之 */
  let pendingRewindTick: number | undefined;
  lines.forEach((line, i) => {
    const s = line.trim();
    if (!s) return;
    let obj: unknown;
    try {
      obj = JSON.parse(s);
    } catch {
      data.errors.push(`第 ${i + 1} 行:JSON 解析失敗`);
      return;
    }
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
        data.events.push(ev);
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
          pendingRewindTick = typeof want === 'number' && Number.isFinite(want) ? want : data.snapshots[data.snapshots.length - 1]?.tick;
        }
        break;
      }
      case 'state': {
        const parsed = parseState(obj['state'], prev);
        if (!parsed) {
          data.errors.push(`第 ${i + 1} 行:state 無法解析`);
          return;
        }
        prev = parsed.state;
        pushState(data, parsed.state, pendingRewindTick);
        pendingRewindTick = undefined;
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
  });
  return finishTimes(data);
}

/** 教官站 SessionRecord JSON。 */
export function parseSessionJson(text: string): ReplayData {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return empty('instructor-json', ['JSON 解析失敗']);
  }
  if (!isSessionRecord(obj)) return empty('instructor-json', ['不是教官站紀錄(format 欄位不符)']);
  const data = empty('instructor-json', []);
  const sc = obj.scenario;
  const h: ReplayHeader = { dt: DEFAULT_DT };
  h.createdUtc = obj.createdUtc;
  h.shipId = obj.shipId;
  if (sc) {
    h.scenarioId = sc.id;
    h.scenarioName = sc.name;
    h.loading = sc.loading;
    h.seed = sc.seed;
    h.scenario = sc;
  }
  h.dynamics = `教官站紀錄(${obj.mode === 'simple' ? '簡化教官模式' : '教官站'},來源 ${obj.source})`;
  data.header = h;
  let prev: OwnShipState | undefined;
  obj.states.forEach((raw, i) => {
    const parsed = parseState(raw, prev);
    if (!parsed) {
      data.errors.push(`states[${i}] 無法解析`);
      return;
    }
    prev = parsed.state;
    pushState(data, parsed.state);
  });
  for (const c of obj.commands ?? []) {
    if (!isValidCommand(c.command)) continue;
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
    const events = obj.events.map((e) => parseSimEvent(e, DEFAULT_DT)).filter((e): e is SimEvent => e !== null);
    data.events = dropRewoundEvents(events, data.rewinds);
  }
  if (obj.coreSummary) data.summary = parseAssessmentSummary(obj.coreSummary);
  return finishTimes(data);
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

/** 二分搜尋:t 時刻(含)之前最後一筆狀態的索引;t 早於第一筆時回傳 0;沒有狀態回傳 -1。 */
export function stateIndexAt(states: readonly OwnShipState[], t: number): number {
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
export function trackPoints(states: readonly OwnShipState[], interval_s = 1): TrackPoint[] {
  const pts: TrackPoint[] = [];
  let last = Number.NEGATIVE_INFINITY;
  for (const s of states) {
    if (s.t - last < interval_s && pts.length > 0) continue;
    pts.push({ x: s.pos.x, y: s.pos.y, t: s.t });
    last = s.t;
  }
  return pts;
}
