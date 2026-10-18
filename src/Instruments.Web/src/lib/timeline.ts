// 情境 YAML 擴充欄位的解析(設計文件 docs/design/scenario-engine.md 第 1 節):timeline(時間表)、route(航線)、
// zones(區域)、briefing(簡報物件)與 ${params:key} 參數引用。教官站不解析參數集(核心載入時解析),
// 只把參數引用顯示為「參數引用(鍵)」;所有解析對未知欄位寬容、缺欄位有預設、不丟例外。

import { normalizeHeading } from './angles.js';

// ------------------------------------------------------------------ 參數引用

const PARAM_REF_RE = /^\$\{params:([A-Za-z0-9_.\-]+)\}$/;

/** 字串是否為 ${params:key} 參數引用。 */
export function isParamRef(v: unknown): v is string {
  return typeof v === 'string' && PARAM_REF_RE.test(v.trim());
}

/** 取出參數引用的鍵;不是引用回傳 null。 */
export function paramRefKey(v: unknown): string | null {
  if (typeof v !== 'string') return null;
  const m = PARAM_REF_RE.exec(v.trim());
  return m ? m[1]! : null;
}

/** 可引用參數的數值欄位:數值,或尚未解析的參數引用(value 為 undefined、param 為鍵)。 */
export interface NumberOrParam {
  value?: number;
  param?: string;
}

/** 解析 numberOrParam 欄位;已解析的 {value}/{param} 物件原樣接受(parseRoute/parseZones 對 ScenarioSummary 冪等);不是數值也不是引用回傳 null。 */
export function numberOrParam(v: unknown): NumberOrParam | null {
  if (typeof v === 'number' && Number.isFinite(v)) return { value: v };
  if (typeof v === 'object' && v !== null && !Array.isArray(v)) {
    const o = v as Record<string, unknown>;
    if (typeof o['value'] === 'number' && Number.isFinite(o['value'])) return { value: o['value'] };
    if (typeof o['param'] === 'string' && o['param']) return { param: o['param'] };
    return null;
  }
  if (typeof v === 'string') {
    const key = paramRefKey(v);
    if (key) return { param: key };
    const s = v.trim();
    if (s !== '' && Number.isFinite(Number(s))) return { value: Number(s) };
  }
  return null;
}

/** 顯示用:數值最多 digits 位小數(去尾零)加單位,參數引用顯示為「參數 key」。 */
export function formatNumberOrParam(v: NumberOrParam | null | undefined, unit = '', digits = 3): string {
  if (!v) return '—';
  if (v.param) return `參數 ${v.param}`;
  if (v.value === undefined) return '—';
  const n = Number.isInteger(v.value) ? String(v.value) : String(Number(v.value.toFixed(digits)));
  return unit ? `${n} ${unit}` : n;
}

/** 走訪任意 YAML 物件樹,收集所有 ${params:…} 引用(路徑 → 鍵),供情境面板「參數引用 n 個」與不一致警示。 */
export function collectParamRefs(node: unknown, path = ''): Array<{ path: string; key: string }> {
  const out: Array<{ path: string; key: string }> = [];
  const walk = (v: unknown, p: string): void => {
    if (typeof v === 'string') {
      const key = paramRefKey(v);
      if (key) out.push({ path: p, key });
    } else if (Array.isArray(v)) {
      v.forEach((item, i) => walk(item, `${p}[${i}]`));
    } else if (isRecord(v)) {
      for (const [k, item] of Object.entries(v)) walk(item, p ? `${p}.${k}` : k);
    }
  };
  walk(node, path);
  return out;
}

// ------------------------------------------------------------------ 共用

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function optNumber(v: unknown): number | undefined {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '' && !isParamRef(v) && Number.isFinite(Number(v))) return Number(v);
  return undefined;
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v);
}

/** 經緯度或 ENU 座標點(情境 YAML 可混用)。 */
export interface GeoPoint {
  lat?: number;
  lon?: number;
  x?: number;
  y?: number;
}

export function parseGeoPoint(v: unknown): GeoPoint | null {
  if (!isRecord(v)) return null;
  const p: GeoPoint = {};
  const lat = optNumber(v['lat']);
  const lon = optNumber(v['lon']);
  const x = optNumber(v['x']);
  const y = optNumber(v['y']);
  if (lat !== undefined) p.lat = lat;
  if (lon !== undefined) p.lon = lon;
  if (x !== undefined) p.x = x;
  if (y !== undefined) p.y = y;
  if ((lat === undefined || lon === undefined) && (x === undefined || y === undefined)) return null;
  return p;
}

// ------------------------------------------------------------------ 觸發條件

export type TriggerLeafType =
  | 'time'
  | 'ownDistance'
  | 'ownHeading'
  | 'ownSpeed'
  | 'ukc'
  | 'xtd'
  | 'routeLeg'
  | 'zone'
  | 'phase'
  | 'event'
  | 'fault'
  | 'telegraph'
  | 'autopilot'
  | 'manual';

export const TRIGGER_LEAF_TYPES: readonly TriggerLeafType[] = ['time', 'ownDistance', 'ownHeading', 'ownSpeed', 'ukc', 'xtd', 'routeLeg', 'zone', 'phase', 'event', 'fault', 'telegraph', 'autopilot', 'manual'] as const;

/** 遞迴觸發條件:leaf(type)或 all/any/not 組合;leaf 的欄位保留原始值(含參數引用)供摘要顯示。 */
export type TriggerCondition =
  | { op: 'all'; items: TriggerCondition[] }
  | { op: 'any'; items: TriggerCondition[] }
  | { op: 'not'; item: TriggerCondition }
  | { op: 'leaf'; type: TriggerLeafType | (string & {}); fields: Record<string, unknown> };

export function parseTriggerCondition(v: unknown, issues: string[] = [], path = 'when'): TriggerCondition | null {
  if (!isRecord(v)) {
    issues.push(`${path} 不是物件`);
    return null;
  }
  if (Array.isArray(v['all'])) return { op: 'all', items: v['all'].map((c, i) => parseTriggerCondition(c, issues, `${path}.all[${i}]`)).filter((c): c is TriggerCondition => c !== null) };
  if (Array.isArray(v['any'])) return { op: 'any', items: v['any'].map((c, i) => parseTriggerCondition(c, issues, `${path}.any[${i}]`)).filter((c): c is TriggerCondition => c !== null) };
  if (v['not'] !== undefined) {
    const inner = parseTriggerCondition(v['not'], issues, `${path}.not`);
    return inner ? { op: 'not', item: inner } : null;
  }
  const type = str(v['type']).trim();
  if (!type) {
    issues.push(`${path} 缺 type`);
    return null;
  }
  if (!(TRIGGER_LEAF_TYPES as readonly string[]).includes(type)) issues.push(`${path} 未知的條件型別 ${type}`);
  const fields: Record<string, unknown> = {};
  for (const [k, val] of Object.entries(v)) if (k !== 'type') fields[k] = val;
  return { op: 'leaf', type, fields };
}

function fmtNum(v: unknown, unit = ''): string {
  const n = numberOrParam(v);
  return n ? formatNumberOrParam(n, unit) : str(v);
}

function sinceText(since: unknown): string {
  if (!isRecord(since)) return '';
  const parts = [str(since['event'])];
  if (since['itemId'] !== undefined) parts.push(str(since['itemId']));
  if (since['targetId'] !== undefined) parts.push(str(since['targetId']));
  if (since['legIndex'] !== undefined) parts.push(`航段 ${str(since['legIndex'])}`);
  return `自 ${parts.filter(Boolean).join(' ')} 起`;
}

/** 條件的一句話摘要(時間表面板)。 */
export function conditionSummary(c: TriggerCondition | null): string {
  if (!c) return '(手動)';
  switch (c.op) {
    case 'all':
      return c.items.map((i) => wrap(i)).join(' 且 ');
    case 'any':
      return c.items.map((i) => wrap(i)).join(' 或 ');
    case 'not':
      return `非(${conditionSummary(c.item)})`;
    case 'leaf':
      return leafSummary(c.type, c.fields);
  }
}

function wrap(c: TriggerCondition): string {
  const s = conditionSummary(c);
  return c.op === 'all' || c.op === 'any' ? `(${s})` : s;
}

function leafSummary(type: string, f: Record<string, unknown>): string {
  const lt = f['lessThan'];
  const gt = f['greaterThan'];
  switch (type) {
    case 'time':
      return `時間 ≥ ${fmtNum(f['atLeast'], 's')}${f['since'] !== undefined ? ` ${sinceText(f['since'])}` : ''}`;
    case 'ownDistance': {
      const ref = f['waypoint'] !== undefined ? `航點 ${str(f['waypoint'])}` : f['target'] !== undefined ? `目標 ${str(f['target'])}` : '定點';
      const parts: string[] = [];
      if (lt !== undefined) parts.push(`< ${fmtNum(lt, 'nm')}`);
      if (gt !== undefined) parts.push(`> ${fmtNum(gt, 'nm')}`);
      return `距 ${ref} ${parts.join(' 且 ') || '?'}`;
    }
    case 'ownHeading': {
      const parts: string[] = [];
      if (gt !== undefined) parts.push(`> ${fmtNum(gt, '°')}`);
      if (lt !== undefined) parts.push(`< ${fmtNum(lt, '°')}`);
      return `航向 ${parts.join(' 且 ') || '?'}`;
    }
    case 'ownSpeed': {
      const parts: string[] = [];
      if (lt !== undefined) parts.push(`< ${fmtNum(lt, 'kn')}`);
      if (gt !== undefined) parts.push(`> ${fmtNum(gt, 'kn')}`);
      return `${str(f['metric']) === 'stw' ? 'STW' : 'SOG'} ${parts.join(' 且 ') || '?'}`;
    }
    case 'ukc': {
      if (f['lessThanFractionOfDraft'] !== undefined) return `UKC < 吃水 × ${fmtNum(f['lessThanFractionOfDraft'])}`;
      const parts: string[] = [];
      if (lt !== undefined) parts.push(`< ${fmtNum(lt, 'm')}`);
      if (gt !== undefined) parts.push(`> ${fmtNum(gt, 'm')}`);
      return `UKC ${parts.join(' 且 ') || '?'}`;
    }
    case 'xtd':
      return `XTD > ${fmtNum(gt, 'm')}${f['side'] !== undefined && f['side'] !== 'any' ? `(${f['side'] === 'port' ? '左' : '右'})` : ''}`;
    case 'routeLeg':
      if (f['passedWaypoint'] !== undefined) return `通過航點 ${str(f['passedWaypoint'])}`;
      if (f['equals'] !== undefined) return `航段 = ${str(f['equals'])}`;
      return `航段 ≥ ${str(f['atLeast'])}`;
    case 'zone':
      return `${f['inside'] === false ? '不在' : '在'}區域 ${str(f['id'])} 內`;
    case 'phase':
      return `階段 = ${str(f['equals'])}`;
    case 'event': {
      const parts = [str(f['kind'])];
      if (f['itemId'] !== undefined) parts.push(str(f['itemId']));
      if (f['targetId'] !== undefined) parts.push(str(f['targetId']));
      const count = isRecord(f['count']) ? optNumber(f['count']['atLeast']) : undefined;
      return `曾發生 ${parts.join(' ')}${count !== undefined && count > 1 ? ` ≥ ${count} 次` : ''}`;
    }
    case 'fault':
      return `故障 ${str(f['name'])} ${f['active'] === false ? '未啟用' : '啟用中'}`;
    case 'telegraph':
      if (Array.isArray(f['notIn'])) return `車鐘不在 ${f['notIn'].map(str).join('/')}`;
      return `車鐘在 ${Array.isArray(f['in']) ? f['in'].map(str).join('/') : '?'}`;
    case 'autopilot':
      return `自動舵${f['enabled'] === false ? '關' : '開'}`;
    case 'manual':
      return '手動';
    default:
      return `${type} ${JSON.stringify(f)}`;
  }
}

// ------------------------------------------------------------------ 動作

export type TimelineActionKind =
  | 'injectFault'
  | 'clearFault'
  | 'setEnvironment'
  | 'targetControl'
  | 'voiceScript'
  | 'message'
  | 'phase'
  | 'freeze'
  | 'autopilot'
  | 'ownShip'
  | 'timeScale'
  | 'arm'
  | 'disarm'
  | 'endExercise'
  | 'tugControl';

export const TIMELINE_ACTION_KINDS: readonly TimelineActionKind[] = ['injectFault', 'clearFault', 'setEnvironment', 'targetControl', 'voiceScript', 'message', 'phase', 'freeze', 'autopilot', 'ownShip', 'timeScale', 'arm', 'disarm', 'endExercise', 'tugControl'] as const;

/** 不允許作為時間表動作的指令(設計文件第 1.1.2 節)。 */
export const FORBIDDEN_ACTIONS: readonly string[] = ['loadScenario', 'reset', 'restore', 'snapshot'] as const;

export interface TimelineAction {
  kind: TimelineActionKind | (string & {});
  /** 動作的原始值(字串或物件) */
  value: unknown;
}

export function parseTimelineAction(v: unknown, issues: string[] = [], path = 'actions'): TimelineAction | null {
  if (!isRecord(v)) {
    issues.push(`${path} 不是物件`);
    return null;
  }
  const keys = Object.keys(v);
  if (keys.length !== 1) {
    issues.push(`${path} 動作需為單鍵物件(得到 ${keys.length} 個鍵)`);
    if (keys.length === 0) return null;
  }
  const kind = keys[0]!;
  if (FORBIDDEN_ACTIONS.includes(kind)) issues.push(`${path} 不允許的動作 ${kind}`);
  else if (!(TIMELINE_ACTION_KINDS as readonly string[]).includes(kind)) issues.push(`${path} 未知的動作 ${kind}`);
  return { kind, value: v[kind] };
}

/** 故障動作的名稱(字串或 {name, args})。 */
export function faultActionName(a: TimelineAction): string {
  if (typeof a.value === 'string') return a.value;
  if (isRecord(a.value)) return str(a.value['name']);
  return '';
}

/** 動作的一句話摘要。 */
export function actionSummary(a: TimelineAction): string {
  const v = a.value;
  switch (a.kind as TimelineActionKind) {
    case 'injectFault':
      return `注入故障 ${faultActionName(a)}`;
    case 'clearFault':
      return v === 'all' ? '清除全部故障' : `清除故障 ${faultActionName(a)}`;
    case 'setEnvironment': {
      if (!isRecord(v)) return '環境改變';
      const parts: string[] = [];
      if (isRecord(v['wind'])) parts.push(`風 ${fmtNum(v['wind']['trueSpeed'], 'kn')}/${fmtNum(v['wind']['trueDir'], '°')}`);
      if (isRecord(v['current'])) parts.push(`流 ${fmtNum(v['current']['set'], '°')}/${fmtNum(v['current']['drift'], 'kn')}`);
      if (v['waterDepth'] !== undefined) parts.push(`水深 ${fmtNum(v['waterDepth'], 'm')}`);
      if (v['visibility_nm'] !== undefined) parts.push(`能見度 ${fmtNum(v['visibility_nm'], 'nm')}`);
      const ramp = optNumber(v['ramp_s']);
      return `環境 ${parts.join(' · ')}${ramp ? `(${ramp} s 漸變)` : ''}`;
    }
    case 'targetControl':
      return `目標船控制 ${isRecord(v) ? str(v['targetId'] ?? v['id']) : ''}`.trimEnd();
    case 'voiceScript':
      return `語音 ${isRecord(v) ? `${str(v['role'])}:${str(v['text']) || str(v['file'])}` : str(v)}`;
    case 'message':
      return `訊息 ${isRecord(v) ? str(v['text']) : str(v)}`;
    case 'phase':
      return `階段 → ${str(v)}`;
    case 'freeze':
      return `凍結${isRecord(v) && v['reason'] ? `:${str(v['reason'])}` : ''}`;
    case 'autopilot':
      return `自動舵 ${isRecord(v) ? (v['enabled'] === false ? '關' : `開${v['heading'] !== undefined ? ` ${fmtNum(v['heading'], '°')}` : ''}`) : ''}`;
    case 'ownShip':
      return `自船覆寫 ${isRecord(v) ? Object.entries(v).map(([k, x]) => `${k} ${str(x)}`).join(' ') : ''}`;
    case 'timeScale':
      return `時間倍率 ×${str(v)}`;
    case 'arm':
      return `啟用項目 ${str(v)}`;
    case 'disarm':
      return `停用項目 ${str(v)}`;
    case 'endExercise':
      return `結束練習${isRecord(v) && v['reason'] ? `:${str(v['reason'])}` : ''}`;
    case 'tugControl':
      return `拖船令 ${isRecord(v) ? `${str(v['id'])} ${str(v['mode'])}`.trim() : str(v)}`;
    default:
      return `${a.kind} ${typeof v === 'string' ? v : JSON.stringify(v)}`;
  }
}

// ------------------------------------------------------------------ 時間表項目

export type RepeatSpec = { mode: 'once' } | { mode: 'always' } | { mode: 'every'; every_s: number; max?: number };

export interface TimelineItem {
  id: string;
  name: string;
  description: string;
  phase?: string;
  visibility: 'instructor' | 'all';
  repeat: RepeatSpec;
  cooldown_s: number;
  holdFor_s: number;
  delay_s: number;
  manual: boolean;
  armed: boolean;
  when: TriggerCondition | null;
  actions: TimelineAction[];
}

export function parseTimelineItem(v: unknown, index: number, issues: string[]): TimelineItem | null {
  const path = `timeline[${index}]`;
  if (!isRecord(v)) {
    issues.push(`${path} 不是物件`);
    return null;
  }
  const id = str(v['id']).trim();
  if (!id) issues.push(`${path} 缺 id`);
  const manual = v['manual'] === true;
  const when = v['when'] === undefined ? null : parseTriggerCondition(v['when'], issues, `${path}.when`);
  if (!manual && !when) issues.push(`${path} 非 manual 項目需有 when`);
  let repeat: RepeatSpec = { mode: 'once' };
  const rep = v['repeat'];
  if (rep === 'always') repeat = { mode: 'always' };
  else if (isRecord(rep)) {
    const every = optNumber(rep['every_s']);
    if (every !== undefined) {
      repeat = { mode: 'every', every_s: every };
      const max = optNumber(rep['max']);
      if (max !== undefined) repeat.max = max;
    } else issues.push(`${path}.repeat 缺 every_s`);
  } else if (rep !== undefined && rep !== 'once') issues.push(`${path}.repeat 無效`);
  const actionsRaw = Array.isArray(v['actions']) ? v['actions'] : (issues.push(`${path} 缺 actions`), []);
  const actions = actionsRaw.map((a, i) => parseTimelineAction(a, issues, `${path}.actions[${i}]`)).filter((a): a is TimelineAction => a !== null);
  const item: TimelineItem = {
    id: id || `item${index}`,
    name: str(v['name']).trim() || id || `item${index}`,
    description: str(v['description']).trim(),
    visibility: v['visibility'] === 'all' ? 'all' : 'instructor',
    repeat,
    cooldown_s: optNumber(v['cooldown_s']) ?? 0,
    holdFor_s: optNumber(v['holdFor_s']) ?? 0,
    delay_s: optNumber(v['delay_s']) ?? 0,
    manual,
    armed: v['armed'] !== false,
    when,
    actions,
  };
  const phase = str(v['phase']).trim();
  if (phase) item.phase = phase;
  return item;
}

/** 解析 timeline[](缺或非陣列回傳空陣列;重複 id 記入 issues)。 */
export function parseTimeline(raw: unknown, issues: string[] = []): TimelineItem[] {
  if (raw === undefined || raw === null) return [];
  if (!Array.isArray(raw)) {
    issues.push('timeline 不是陣列');
    return [];
  }
  const items = raw.map((v, i) => parseTimelineItem(v, i, issues)).filter((x): x is TimelineItem => x !== null);
  const seen = new Set<string>();
  for (const it of items) {
    if (seen.has(it.id)) issues.push(`timeline id 重複:${it.id}`);
    seen.add(it.id);
  }
  return items;
}

// ------------------------------------------------------------------ 航線與區域

export interface RouteWaypoint extends GeoPoint {
  name: string;
  xtdLimit_m?: NumberOrParam;
  speedLimit_kn?: NumberOrParam;
  checkpoint: boolean;
  /** 報告點說明(有值即為報告點) */
  report?: string;
  arrivalRadius_m?: number;
}

export interface ScenarioRoute {
  name: string;
  xtdLimit_m?: NumberOrParam;
  startLeg: number;
  waypoints: RouteWaypoint[];
}

export function parseRoute(raw: unknown, issues: string[] = []): ScenarioRoute | null {
  if (raw === undefined || raw === null) return null;
  if (!isRecord(raw)) {
    issues.push('route 不是物件');
    return null;
  }
  const wpsRaw = Array.isArray(raw['waypoints']) ? raw['waypoints'] : (issues.push('route 缺 waypoints'), []);
  const waypoints: RouteWaypoint[] = [];
  wpsRaw.forEach((w, i) => {
    const p = parseGeoPoint(w);
    if (!p || !isRecord(w)) {
      issues.push(`route.waypoints[${i}] 需 lat/lon 或 x/y`);
      return;
    }
    const wp: RouteWaypoint = { ...p, name: str(w['name']).trim() || `WP${i + 1}`, checkpoint: w['checkpoint'] === true };
    const xtd = numberOrParam(w['xtdLimit_m']);
    if (xtd) wp.xtdLimit_m = xtd;
    const spd = numberOrParam(w['speedLimit_kn']);
    if (spd) wp.speedLimit_kn = spd;
    if (typeof w['report'] === 'string' && w['report'].trim()) wp.report = w['report'].trim();
    else if (w['report'] === true) wp.report = '報告點';
    const ar = optNumber(w['arrivalRadius_m']);
    if (ar !== undefined) wp.arrivalRadius_m = ar;
    waypoints.push(wp);
  });
  if (waypoints.length < 2) issues.push('route 航點少於 2');
  const route: ScenarioRoute = { name: str(raw['name']).trim(), startLeg: optNumber(raw['startLeg']) ?? 0, waypoints };
  const xtd = numberOrParam(raw['xtdLimit_m']);
  if (xtd) route.xtdLimit_m = xtd;
  return route;
}

export type ZoneType = 'noGo' | 'speedLimit' | 'ukcPolicy' | 'reporting';

export const ZONE_TYPES: readonly ZoneType[] = ['noGo', 'speedLimit', 'ukcPolicy', 'reporting'] as const;

export interface UkcLimitSpec {
  metres?: NumberOrParam;
  fractionOfDraft?: NumberOrParam;
}

export interface ScenarioZone {
  id: string;
  name: string;
  type: ZoneType | (string & {});
  polygon: GeoPoint[];
  speedLimit_kn?: NumberOrParam;
  ukcMin?: UkcLimitSpec;
  waterDepth_m?: number;
}

export function parseUkcLimit(v: unknown): UkcLimitSpec | null {
  if (!isRecord(v)) return null;
  const out: UkcLimitSpec = {};
  const m = numberOrParam(v['metres']);
  if (m) out.metres = m;
  const f = numberOrParam(v['fractionOfDraft']);
  if (f) out.fractionOfDraft = f;
  return m || f ? out : null;
}

export function parseZones(raw: unknown, issues: string[] = []): ScenarioZone[] {
  if (raw === undefined || raw === null) return [];
  if (!Array.isArray(raw)) {
    issues.push('zones 不是陣列');
    return [];
  }
  const zones: ScenarioZone[] = [];
  raw.forEach((z, i) => {
    if (!isRecord(z)) {
      issues.push(`zones[${i}] 不是物件`);
      return;
    }
    const id = str(z['id']).trim() || `zone${i}`;
    if (!str(z['id']).trim()) issues.push(`zones[${i}] 缺 id`);
    const type = str(z['type']).trim() || 'noGo';
    if (!(ZONE_TYPES as readonly string[]).includes(type)) issues.push(`zones[${i}] 未知的 type ${type}`);
    const polygon = (Array.isArray(z['polygon']) ? z['polygon'] : []).map(parseGeoPoint).filter((p): p is GeoPoint => p !== null);
    if (polygon.length < 3) issues.push(`zones[${i}] 多邊形少於 3 點`);
    const zone: ScenarioZone = { id, name: str(z['name']).trim() || id, type, polygon };
    const spd = numberOrParam(z['speedLimit_kn']);
    if (spd) zone.speedLimit_kn = spd;
    const ukc = parseUkcLimit(z['ukcMin']);
    if (ukc) zone.ukcMin = ukc;
    const depth = optNumber(z['waterDepth_m']);
    if (depth !== undefined) zone.waterDepth_m = depth;
    zones.push(zone);
  });
  return zones;
}

// ------------------------------------------------------------------ 簡報

export interface BriefingAttachment {
  title: string;
  file: string;
}

export interface BriefingRole {
  role: string;
  /** leader | voiceScript | instructor | 自訂 */
  playedBy: string;
  required: boolean;
}

export interface ScenarioBriefing {
  text: string;
  attachments: BriefingAttachment[];
  roles: BriefingRole[];
  minParticipants?: number;
  /** 情境預估長度(秒;驗證器用,選填) */
  duration_s?: number;
}

/** briefing:字串等同 {text};缺則 text 為空。 */
export function parseBriefing(raw: unknown, issues: string[] = []): ScenarioBriefing {
  const out: ScenarioBriefing = { text: '', attachments: [], roles: [] };
  if (raw === undefined || raw === null) return out;
  if (typeof raw === 'string') {
    out.text = raw.trim();
    return out;
  }
  if (!isRecord(raw)) {
    issues.push('briefing 需為字串或物件');
    return out;
  }
  out.text = str(raw['text']).trim();
  if (Array.isArray(raw['attachments'])) {
    raw['attachments'].forEach((a, i) => {
      if (!isRecord(a) || !str(a['file']).trim()) {
        issues.push(`briefing.attachments[${i}] 缺 file`);
        return;
      }
      out.attachments.push({ title: str(a['title']).trim() || str(a['file']), file: str(a['file']).trim() });
    });
  }
  if (Array.isArray(raw['roles'])) {
    raw['roles'].forEach((r, i) => {
      if (!isRecord(r) || !str(r['role']).trim()) {
        issues.push(`briefing.roles[${i}] 缺 role`);
        return;
      }
      out.roles.push({ role: str(r['role']).trim(), playedBy: str(r['playedBy']).trim() || 'leader', required: r['required'] === true });
    });
  }
  const min = optNumber(raw['minParticipants']);
  if (min !== undefined) out.minParticipants = Math.trunc(min);
  const dur = optNumber(raw['duration_s']);
  if (dur !== undefined) out.duration_s = dur;
  return out;
}

export const PLAYED_BY_LABEL: Readonly<Record<string, string>> = {
  leader: '帶領者',
  voiceScript: '語音腳本',
  instructor: '教官',
  student: '學員',
};

// ------------------------------------------------------------------ 座標換算(航跡圖疊圖)

/** 經緯度 → 本地 ENU(公尺;小範圍平面近似,與 demo-sim 相同的 111 320 m/度)。 */
export function latLonToEnu(origin: { lat: number; lon: number }, lat: number, lon: number): { x: number; y: number } {
  const latRad = (origin.lat * Math.PI) / 180;
  return { x: (lon - origin.lon) * 111_320 * Math.cos(latRad), y: (lat - origin.lat) * 111_320 };
}

/** GeoPoint → ENU;無 origin 且只有經緯度時回傳 null。 */
export function geoPointToEnu(p: GeoPoint, origin: { lat: number; lon: number } | null): { x: number; y: number } | null {
  if (p.x !== undefined && p.y !== undefined) return { x: p.x, y: p.y };
  if (p.lat !== undefined && p.lon !== undefined && origin) return latLonToEnu(origin, p.lat, p.lon);
  return null;
}

/** 航段航向(度;ENU,x 東 y 北)。 */
export function legCourse(a: { x: number; y: number }, b: { x: number; y: number }): number {
  return normalizeHeading((Math.atan2(b.x - a.x, b.y - a.y) * 180) / Math.PI);
}
