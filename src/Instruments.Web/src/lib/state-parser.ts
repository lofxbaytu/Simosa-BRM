// 自船狀態解析:把 WebSocket 收到的 JSON 轉成 OwnShipState,缺欄位或型別錯誤時以安全預設值補上,
// 並回報問題清單(供「資料可疑」標記,規劃書第 7.2 節)。

import {
  SHIP_IDS,
  TELEGRAPH_POSITIONS,
  type Current,
  type EngineStatus,
  type LoadingCondition,
  type OwnShipState,
  type Position,
  type ShipId,
  type StateFlags,
  type TelegraphPosition,
  type Thruster,
  type Wind,
} from '../types/state.js';
import { normalizeHeading } from './angles.js';

export interface ParseResult {
  state: OwnShipState;
  /** 缺少或無效而被補上預設值的欄位(以點路徑表示) */
  issues: string[];
}

const LOADINGS: readonly LoadingCondition[] = ['full', 'ballast', 'intermediate'];
const ENGINE_STATES = ['stopped', 'running', 'starting', 'failed'] as const;

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function num(
  obj: Record<string, unknown>,
  key: string,
  fallback: number,
  issues: string[],
  path: string,
): number {
  const v = obj[key];
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v))) return Number(v);
  issues.push(path);
  return fallback;
}

function optNum(obj: Record<string, unknown>, key: string, issues: string[], path: string): number | undefined {
  const v = obj[key];
  if (v === undefined || v === null) return undefined;
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  issues.push(path);
  return undefined;
}

function oneOf<T extends string>(
  obj: Record<string, unknown>,
  key: string,
  allowed: readonly T[],
  fallback: T,
  issues: string[],
  path: string,
): T {
  const v = obj[key];
  if (typeof v === 'string' && (allowed as readonly string[]).includes(v)) return v as T;
  issues.push(path);
  return fallback;
}

/** 建立一份全部為零的預設狀態(示範模式初始與解析失敗時使用)。 */
export function defaultState(shipId: ShipId = 'FSB1'): OwnShipState {
  return {
    t: 0,
    tick: 0,
    shipId,
    pos: { lat: 0, lon: 0, x: 0, y: 0 },
    heading: 0,
    cog: 0,
    sog: 0,
    stw: 0,
    rot: 0,
    u: 0,
    v: 0,
    r: 0,
    rudder: 0,
    rudderOrder: 0,
    rpm: 0,
    rpmOrder: 0,
    telegraph: 'STOP',
    thruster: { order: 0, actual: 0 },
    depthBelowKeel: 0,
    wind: { trueSpeed: 0, trueDir: 0 },
    current: { set: 0, drift: 0 },
    loading: 'full',
    flags: { frozen: false, aground: false, collision: false },
  };
}

/**
 * 解析狀態訊息。接受:
 *  - 直接的 OwnShipState 物件;
 *  - 包在 { type: "state", state: {...} } 或 { state: {...} } / { data: {...} } 的信封;
 *  - JSON 字串。
 * 無法辨識為狀態的訊息回傳 null。
 */
export function parseState(input: unknown, previous?: OwnShipState): ParseResult | null {
  let raw: unknown = input;
  if (typeof raw === 'string') {
    try {
      raw = JSON.parse(raw);
    } catch {
      return null;
    }
  }
  if (!isRecord(raw)) return null;

  // 信封拆解
  if (!('heading' in raw) && !('pos' in raw)) {
    const inner = raw['state'] ?? raw['data'] ?? raw['payload'];
    if (isRecord(inner)) raw = inner;
    else return null;
  }
  if (!isRecord(raw)) return null;
  if (!('heading' in raw) && !('pos' in raw)) return null;

  const issues: string[] = [];
  const base = previous ?? defaultState();

  const shipId = oneOf<ShipId>(raw, 'shipId', SHIP_IDS, base.shipId, issues, 'shipId');

  let pos: Position;
  if (isRecord(raw['pos'])) {
    const p = raw['pos'];
    pos = {
      lat: num(p, 'lat', base.pos.lat, issues, 'pos.lat'),
      lon: num(p, 'lon', base.pos.lon, issues, 'pos.lon'),
      x: num(p, 'x', base.pos.x, issues, 'pos.x'),
      y: num(p, 'y', base.pos.y, issues, 'pos.y'),
    };
  } else {
    issues.push('pos');
    pos = { ...base.pos };
  }

  let wind: Wind;
  if (isRecord(raw['wind'])) {
    const w = raw['wind'];
    wind = {
      trueSpeed: num(w, 'trueSpeed', base.wind.trueSpeed, issues, 'wind.trueSpeed'),
      trueDir: normalizeHeading(num(w, 'trueDir', base.wind.trueDir, issues, 'wind.trueDir')),
    };
    const rs = optNum(w, 'relSpeed', issues, 'wind.relSpeed');
    const rd = optNum(w, 'relDir', issues, 'wind.relDir');
    if (rs !== undefined) wind.relSpeed = rs;
    if (rd !== undefined) wind.relDir = normalizeHeading(rd);
  } else {
    issues.push('wind');
    wind = { ...base.wind };
  }

  let current: Current;
  if (isRecord(raw['current'])) {
    const c = raw['current'];
    current = {
      set: normalizeHeading(num(c, 'set', base.current.set, issues, 'current.set')),
      drift: num(c, 'drift', base.current.drift, issues, 'current.drift'),
    };
  } else {
    issues.push('current');
    current = { ...base.current };
  }

  let thruster: Thruster | undefined;
  if (isRecord(raw['thruster'])) {
    const th = raw['thruster'];
    thruster = {
      order: num(th, 'order', base.thruster?.order ?? 0, issues, 'thruster.order'),
      actual: num(th, 'actual', base.thruster?.actual ?? 0, issues, 'thruster.actual'),
    };
  } else if (raw['thruster'] !== undefined) {
    issues.push('thruster');
  }

  const state: OwnShipState = {
    t: num(raw, 't', base.t, issues, 't'),
    tick: Math.trunc(num(raw, 'tick', base.tick, issues, 'tick')),
    shipId,
    pos,
    heading: normalizeHeading(num(raw, 'heading', base.heading, issues, 'heading')),
    cog: normalizeHeading(num(raw, 'cog', base.cog, issues, 'cog')),
    sog: num(raw, 'sog', base.sog, issues, 'sog'),
    stw: num(raw, 'stw', base.stw, issues, 'stw'),
    rot: num(raw, 'rot', base.rot, issues, 'rot'),
    u: num(raw, 'u', base.u, issues, 'u'),
    v: num(raw, 'v', base.v, issues, 'v'),
    r: num(raw, 'r', base.r, issues, 'r'),
    rudder: num(raw, 'rudder', base.rudder, issues, 'rudder'),
    rudderOrder: num(raw, 'rudderOrder', base.rudderOrder, issues, 'rudderOrder'),
    rpm: num(raw, 'rpm', base.rpm, issues, 'rpm'),
    rpmOrder: num(raw, 'rpmOrder', base.rpmOrder, issues, 'rpmOrder'),
    telegraph: oneOf<TelegraphPosition>(raw, 'telegraph', TELEGRAPH_POSITIONS, base.telegraph, issues, 'telegraph'),
    depthBelowKeel: num(raw, 'depthBelowKeel', base.depthBelowKeel, issues, 'depthBelowKeel'),
    wind,
    current,
    loading: oneOf<LoadingCondition>(raw, 'loading', LOADINGS, base.loading, issues, 'loading'),
  };

  if (thruster) state.thruster = thruster;

  const drift = optNum(raw, 'drift', issues, 'drift');
  if (drift !== undefined) state.drift = drift;
  const waterDepth = optNum(raw, 'waterDepth', issues, 'waterDepth');
  if (waterDepth !== undefined) state.waterDepth = waterDepth;
  const squat = optNum(raw, 'squat', issues, 'squat');
  if (squat !== undefined) state.squat = squat;

  if (isRecord(raw['draft'])) {
    const d = raw['draft'];
    const fore = optNum(d, 'fore', issues, 'draft.fore');
    const aft = optNum(d, 'aft', issues, 'draft.aft');
    state.draft = {};
    if (fore !== undefined) state.draft.fore = fore;
    if (aft !== undefined) state.draft.aft = aft;
  }

  if (isRecord(raw['engine'])) {
    const e = raw['engine'];
    const engine: EngineStatus = {};
    const st = e['state'];
    if (typeof st === 'string' && (ENGINE_STATES as readonly string[]).includes(st)) {
      engine.state = st as (typeof ENGINE_STATES)[number];
    } else if (st !== undefined) {
      issues.push('engine.state');
    }
    const starts = optNum(e, 'startsRemaining', issues, 'engine.startsRemaining');
    if (starts !== undefined) engine.startsRemaining = Math.trunc(starts);
    const load = optNum(e, 'load_pct', issues, 'engine.load_pct');
    if (load !== undefined) engine.load_pct = load;
    state.engine = engine;
  }

  if (Array.isArray(raw['faults'])) {
    state.faults = raw['faults'].filter((f): f is string => typeof f === 'string');
  } else if (raw['faults'] !== undefined) {
    issues.push('faults');
  }

  const flags: StateFlags = { frozen: false, aground: false, collision: false };
  if (isRecord(raw['flags'])) {
    const f = raw['flags'];
    for (const key of ['frozen', 'aground', 'collision'] as const) {
      const v = f[key];
      if (typeof v === 'boolean') flags[key] = v;
      else if (v !== undefined) issues.push(`flags.${key}`);
    }
  }
  state.flags = flags;

  return { state, issues };
}
