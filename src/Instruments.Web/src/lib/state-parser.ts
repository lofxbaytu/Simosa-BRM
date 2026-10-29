// 自船狀態解析:把 WebSocket 收到的 JSON 轉成 OwnShipState,缺欄位或型別錯誤時以安全預設值補上,
// 並回報問題清單(供「資料可疑」標記,規劃書第 7.2 節)。

import {
  SHIP_IDS,
  TELEGRAPH_POSITIONS,
  type Current,
  CURRENT_SOURCES,
  type CurrentSource,
  type EngineStatus,
  type LoadingCondition,
  type OwnShipState,
  type Position,
  type RollState,
  type RouteStatus,
  type ScenarioStatus,
  type ShipId,
  type ShipInteractionState,
  type StateFlags,
  type TelegraphPosition,
  type Thruster,
  WAVE_SPECTRA,
  type WaveSpectrum,
  type WaveState,
  type Wind,
} from '../types/state.js';
import type { TargetAisReport, TargetState } from '../types/target.js';
import { normalizeHeading } from './angles.js';
import { applyAlertBlocks } from './alerts.js';
import { applyExternalForceBlocks } from './external-forces.js';

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
    targets: [],
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
    targets: [],
  };

  if (thruster) state.thruster = thruster;

  const drift = optNum(raw, 'drift', issues, 'drift');
  if (drift !== undefined) state.drift = drift;
  const waterDepth = optNum(raw, 'waterDepth', issues, 'waterDepth');
  if (waterDepth !== undefined) state.waterDepth = waterDepth;
  // 潮高(情境有 environment.tide 時才有)與流場來源(時變流場時才有):型別錯誤記入 issues,不影響其他欄位
  const tide = optNum(raw, 'tideHeight_m', issues, 'tideHeight_m');
  if (tide !== undefined) state.tideHeight_m = tide;
  const source = raw['currentSource'];
  if (typeof source === 'string' && (CURRENT_SOURCES as readonly string[]).includes(source)) state.currentSource = source as CurrentSource;
  else if (source !== undefined && source !== null) issues.push('currentSource');
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

  // 情境引擎新增的選填欄位(設計文件第 5.1 節):有就解析,型別錯誤記入 issues 但不影響其他欄位
  const vis = optNum(raw, 'visibility_nm', issues, 'visibility_nm');
  if (vis !== undefined) state.visibility_nm = vis;
  // utc(選填):ISO 8601 字串;型別錯誤記入 issues,不影響其他欄位(缺省時沿用前一幀不合理,故不繼承 base)
  const utc = raw['utc'];
  if (typeof utc === 'string' && utc.trim() !== '') state.utc = utc;
  else if (utc !== undefined && utc !== null) issues.push('utc');
  if (isRecord(raw['scenario'])) {
    const sc = parseScenarioStatus(raw['scenario'], issues);
    if (sc) state.scenario = sc;
  } else if (raw['scenario'] !== undefined && raw['scenario'] !== null) {
    issues.push('scenario');
  }
  if (isRecord(raw['route'])) {
    state.route = parseRouteStatus(raw['route'], issues);
  } else if (raw['route'] !== undefined && raw['route'] !== null) {
    issues.push('route');
  }

  // 目標船(state.targets[];規劃書第 5.2 節):舊狀態或情境無目標船時省略 → 空陣列(不記問題);非陣列才記入 issues
  if (Array.isArray(raw['targets'])) {
    state.targets = parseTargets(raw['targets'], issues);
  } else if (raw['targets'] !== undefined && raw['targets'] !== null) {
    issues.push('targets');
  }

  // 感測器真值/有效性、自動舵、警報與 BNWAS(docs/design/sensors-alerts.md;C# SimCore 才有,省略時不記問題)
  applyAlertBlocks(raw, state, issues);

  // 拖船、錨、泊位(docs/design/physics-tugs-anchors-mooring.md 第 5 節;C# SimCore 才有,省略時不記問題)
  applyExternalForceBlocks(raw, state, issues);

  // 浪與船間交互作用(physics-waves.md 第 5 節、physics-ship-interaction.md 第 5 節;情境有該區段時才有,省略時不記問題;關閉浪後省略 → 不繼承前一幀)
  if (isRecord(raw['waves'])) state.waves = parseWaves(raw['waves'], issues);
  else if (raw['waves'] !== undefined && raw['waves'] !== null) issues.push('waves');
  if (isRecord(raw['shipInteraction'])) state.shipInteraction = parseShipInteraction(raw['shipInteraction'], issues);
  else if (raw['shipInteraction'] !== undefined && raw['shipInteraction'] !== null) issues.push('shipInteraction');

  // 橫搖(state.roll;情境 ship.roll.enabled 時才有,省略時不記問題、不繼承前一幀)
  if (isRecord(raw['roll'])) state.roll = parseRoll(raw['roll'], issues);
  else if (raw['roll'] !== undefined && raw['roll'] !== null) issues.push('roll');

  return { state, issues };
}

/** 解析 state.roll:四個欄位皆必填,缺少或型別錯誤以 0 補上並記入 issues(naturalPeriod_s 非正時記入 issues 並以 0 表示未知)。 */
export function parseRoll(r: Record<string, unknown>, issues: string[]): RollState {
  const period = num(r, 'naturalPeriod_s', 0, issues, 'roll.naturalPeriod_s');
  if (period <= 0 && !issues.includes('roll.naturalPeriod_s')) issues.push('roll.naturalPeriod_s');
  return {
    angle_deg: num(r, 'angle_deg', 0, issues, 'roll.angle_deg'),
    rate_degps: num(r, 'rate_degps', 0, issues, 'roll.rate_degps'),
    naturalPeriod_s: Math.max(0, period),
    staticHeel_deg: num(r, 'staticHeel_deg', 0, issues, 'roll.staticHeel_deg'),
  };
}

/** 解析 state.waves:必填欄位缺少或型別錯誤以 0 補上並記入 issues;來向正規化至 0–360;spectrum 不明時省略並記入 issues。 */
export function parseWaves(w: Record<string, unknown>, issues: string[]): WaveState {
  const out: WaveState = {
    significantHeight_m: Math.max(0, num(w, 'significantHeight_m', 0, issues, 'waves.significantHeight_m')),
    peakPeriod_s: num(w, 'peakPeriod_s', 0, issues, 'waves.peakPeriod_s'),
    direction_deg: normalizeHeading(num(w, 'direction_deg', 0, issues, 'waves.direction_deg')),
    wavelength_m: num(w, 'wavelength_m', 0, issues, 'waves.wavelength_m'),
  };
  const spectrum = w['spectrum'];
  if (typeof spectrum === 'string' && (WAVE_SPECTRA as readonly string[]).includes(spectrum.toLowerCase())) out.spectrum = spectrum.toLowerCase() as WaveSpectrum;
  else if (spectrum !== undefined && spectrum !== null) issues.push('waves.spectrum');
  for (const key of ['X_kN', 'Y_kN', 'N_kNm'] as const) {
    const v = optNum(w, key, issues, `waves.${key}`);
    if (v !== undefined) out[key] = v;
  }
  return out;
}

/** 解析 state.shipInteraction:active/forceSway_t/momentYaw_tm 必填(缺少以 false/0 補上並記入 issues),其餘選填。 */
export function parseShipInteraction(si: Record<string, unknown>, issues: string[]): ShipInteractionState {
  const active = si['active'];
  if (typeof active !== 'boolean') issues.push('shipInteraction.active');
  const out: ShipInteractionState = {
    active: active === true,
    forceSway_t: num(si, 'forceSway_t', 0, issues, 'shipInteraction.forceSway_t'),
    momentYaw_tm: num(si, 'momentYaw_tm', 0, issues, 'shipInteraction.momentYaw_tm'),
  };
  if (typeof si['targetId'] === 'string' && si['targetId']) out.targetId = si['targetId'];
  else if (si['targetId'] !== undefined && si['targetId'] !== null) issues.push('shipInteraction.targetId');
  for (const key of ['forceSurge_t', 'xi', 'eta'] as const) {
    const v = optNum(si, key, issues, `shipInteraction.${key}`);
    if (v !== undefined) out[key] = v;
  }
  const targets = optNum(si, 'targets', issues, 'shipInteraction.targets');
  if (targets !== undefined) out.targets = Math.max(0, Math.trunc(targets));
  if (typeof si['event'] === 'boolean') out.event = si['event'];
  else if (si['event'] !== undefined && si['event'] !== null) issues.push('shipInteraction.event');
  return out;
}

const TARGET_REQUIRED_NUMS = ['loa', 'beam', 'sog', 'rot', 'range_nm', 'cpa_nm', 'tcpa_min'] as const;
const TARGET_OPT_NUMS = ['draft', 'stw', 'bcr_nm', 'bct_min'] as const;
const AIS_NUMS = ['mmsi', 'imo', 'shipType', 'navStatus', 'lat', 'lon', 'cog', 'sog', 'heading', 'rot', 'dimToBow', 'dimToStern', 'dimToPort', 'dimToStarboard', 'draught'] as const;

/** 解析 targets[]:缺 id 的項目略過(記入 issues),其他缺欄位以 0/空字串補上並記入 issues;航向類角度正規化至 0–360。 */
export function parseTargets(raw: readonly unknown[], issues: string[]): TargetState[] {
  const out: TargetState[] = [];
  raw.forEach((item, i) => {
    const path = `targets[${i}]`;
    if (!isRecord(item)) {
      issues.push(path);
      return;
    }
    const id = typeof item['id'] === 'string' ? item['id'] : typeof item['id'] === 'number' ? String(item['id']) : '';
    if (!id) {
      issues.push(`${path}.id`);
      return;
    }
    let pos: Position;
    if (isRecord(item['pos'])) {
      const p = item['pos'];
      pos = { lat: num(p, 'lat', 0, issues, `${path}.pos.lat`), lon: num(p, 'lon', 0, issues, `${path}.pos.lon`), x: num(p, 'x', 0, issues, `${path}.pos.x`), y: num(p, 'y', 0, issues, `${path}.pos.y`) };
    } else {
      issues.push(`${path}.pos`);
      pos = { lat: 0, lon: 0, x: 0, y: 0 };
    }
    const t: TargetState = {
      id,
      name: typeof item['name'] === 'string' ? item['name'] : id,
      mmsi: Math.trunc(num(item, 'mmsi', 0, issues, `${path}.mmsi`)),
      shipType: typeof item['shipType'] === 'string' ? item['shipType'] : 'other',
      loa: 0,
      beam: 0,
      pos,
      heading: normalizeHeading(num(item, 'heading', 0, issues, `${path}.heading`)),
      cog: normalizeHeading(num(item, 'cog', 0, issues, `${path}.cog`)),
      sog: 0,
      rot: 0,
      range_nm: 0,
      bearing_deg: normalizeHeading(num(item, 'bearing_deg', 0, issues, `${path}.bearing_deg`)),
      cpa_nm: 0,
      tcpa_min: 0,
      aisOn: true,
      lights: typeof item['lights'] === 'string' ? item['lights'] : 'powerDriven',
      behaviour: typeof item['behaviour'] === 'string' ? item['behaviour'] : 'hold',
    };
    for (const key of TARGET_REQUIRED_NUMS) t[key] = num(item, key, 0, issues, `${path}.${key}`);
    for (const key of TARGET_OPT_NUMS) {
      const v = optNum(item, key, issues, `${path}.${key}`);
      if (v !== undefined) t[key] = v;
    }
    const rel = optNum(item, 'relBearing_deg', issues, `${path}.relBearing_deg`);
    if (rel !== undefined) t.relBearing_deg = normalizeHeading(rel);
    if (typeof item['aisOn'] === 'boolean') t.aisOn = item['aisOn'];
    else issues.push(`${path}.aisOn`);
    if (typeof item['sound'] === 'string' && item['sound']) t.sound = item['sound'];
    if (typeof item['colreg'] === 'string' && item['colreg']) t.colreg = item['colreg'];
    if (isRecord(item['ais'])) {
      const a = item['ais'];
      const ais: TargetAisReport = {};
      for (const key of AIS_NUMS) {
        const v = a[key];
        if (typeof v === 'number' && Number.isFinite(v)) ais[key] = v;
      }
      for (const key of ['name', 'callSign', 'destination'] as const) {
        if (typeof a[key] === 'string') ais[key] = a[key] as string;
      }
      t.ais = ais;
    }
    out.push(t);
  });
  return out;
}

function parseScenarioStatus(sc: Record<string, unknown>, issues: string[]): ScenarioStatus | null {
  const id = typeof sc['id'] === 'string' ? sc['id'] : '';
  const phase = typeof sc['phase'] === 'string' && sc['phase'] ? sc['phase'] : 'normal';
  if (!id) {
    issues.push('scenario.id');
  }
  const out: ScenarioStatus = { id, phase };
  if (typeof sc['parameterSetVersion'] === 'string') out.parameterSetVersion = sc['parameterSetVersion'];
  const fired = optNum(sc, 'timelineFired', issues, 'scenario.timelineFired');
  if (fired !== undefined) out.timelineFired = Math.trunc(fired);
  const pending = optNum(sc, 'timelinePending', issues, 'scenario.timelinePending');
  if (pending !== undefined) out.timelinePending = Math.trunc(pending);
  if (typeof sc['hardFailed'] === 'boolean') out.hardFailed = sc['hardFailed'];
  const score = optNum(sc, 'score', issues, 'scenario.score');
  if (score !== undefined) out.score = score;
  if (typeof sc['ended'] === 'boolean') out.ended = sc['ended'];
  return out;
}

function parseRouteStatus(r: Record<string, unknown>, issues: string[]): RouteStatus {
  const out: RouteStatus = {};
  const leg = optNum(r, 'legIndex', issues, 'route.legIndex');
  if (leg !== undefined) out.legIndex = Math.trunc(leg);
  if (typeof r['nextWaypoint'] === 'string') out.nextWaypoint = r['nextWaypoint'];
  for (const key of ['xtd_m', 'xtdLimit_m', 'distanceToWaypoint_nm', 'speedLimit_kn'] as const) {
    const v = optNum(r, key, issues, `route.${key}`);
    if (v !== undefined) out[key] = v;
  }
  for (const key of ['bearingToWaypoint_deg', 'legCourse_deg'] as const) {
    const v = optNum(r, key, issues, `route.${key}`);
    if (v !== undefined) out[key] = normalizeHeading(v);
  }
  if (typeof r['complete'] === 'boolean') out.complete = r['complete'];
  return out;
}
