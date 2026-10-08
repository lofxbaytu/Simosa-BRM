// 重播檢視(講評站雛型,規劃書第 9.2 節)的紀錄解析:
//  - SimCore 的 JSON Lines 紀錄(src/SimCore/README.md 第 4 節;每行 kind = header | input | state | snapshot | footer);
//  - 教官站自己的 SessionRecord JSON(session-record.ts;含 BRM 標記)。
// 解析後提供依時間查狀態(二分搜尋)與航跡點,供時間軸拖曳檢視;不需核心連線。

import type { SimCommand } from '../types/command.js';
import type { OwnShipState } from '../types/state.js';
import type { BrmMarker } from './brm-markers.js';
import { isValidCommand } from './command.js';
import { parseState } from './state-parser.js';
import { isSessionRecord } from './session-record.js';
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
  scenario?: unknown;
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

export interface ReplayData {
  source: 'simcore-jsonl' | 'instructor-json';
  header: ReplayHeader;
  states: OwnShipState[];
  inputs: ReplayInput[];
  snapshots: Array<{ tick: number; t: number }>;
  markers: BrmMarker[];
  operations: OperationEvent[];
  footer: ReplayFooter | null;
  /** 解析時略過的行與原因 */
  errors: string[];
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
  return { source, header: { dt: DEFAULT_DT }, states: [], inputs: [], snapshots: [], markers: [], operations: [], footer: null, errors, tStart: 0, tEnd: 0 };
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
        data.header = h;
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
        break;
      }
      case 'state': {
        const parsed = parseState(obj['state'], prev);
        if (!parsed) {
          data.errors.push(`第 ${i + 1} 行:state 無法解析`);
          return;
        }
        prev = parsed.state;
        data.states.push(parsed.state);
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
      default:
        data.errors.push(`第 ${i + 1} 行:未知的 kind ${String(obj['kind'])}`);
    }
  });
  // 輸入事件的時間若有對應狀態(同 tick 或之後第一筆),用該狀態的 t 較準(header.dt 之外的重設不影響)
  data.states.sort((a, b) => a.t - b.t);
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
    data.states.push(parsed.state);
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
  return finishTimes(data);
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
