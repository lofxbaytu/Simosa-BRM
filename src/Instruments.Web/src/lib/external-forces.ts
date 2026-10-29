// 拖船、錨與錨鏈、泊位(碰墊、繫纜)的網頁端處理(docs/design/physics-tugs-anchors-mooring.md;規劃書第 6.2 節 T/A 項、
// 第 8.1 節 E12–E14、第 8.3 節第 4 類靠泊指標):
//  - 解析:state.tugs[] / anchors[] / berth(state-parser 呼叫 applyExternalForceBlocks;舊狀態沒有欄位時不記問題、面板不顯示);
//  - 標籤:模式/狀態/動作的中文、commandRejected 的 19 種拒絕原因、新事件的一句話與嚴重度(sim-event.ts 呼叫);
//  - 指令:tugControl / anchor / mooring(command.schema.json $defs.tugControlArgs / anchorArgs / mooringArgs;對外度/節/噸);
//  - 幾何:情境 berth 定義(碼頭線 quay[]、waterSide、碰墊/繫船柱里程)轉本地 ENU(與核心 LocalTangentPlane 相同的 WGS-84 曲率半徑)、
//    船體具名點(fwd/aft/fwdPort…,與核心 ScenarioBodyPoint 相同的估計式)、錨鏈孔估計位置;
//  - 靠泊讀數:艏/艉橫向速度(state 沒有對應欄位,由 v、r 與 LPP 推算)與對碼頭的法向接近速度/距離。

import type { SimCommand } from '../types/command.js';
import type { SimEvent } from '../types/event.js';
import type { OwnShipState, Position } from '../types/state.js';
import {
  ANCHOR_ACTIONS,
  ANCHOR_SIDES,
  ANCHOR_STATUSES,
  MOORING_ACTIONS,
  MOORING_STATUSES,
  TUG_MODES,
  TUG_STATUSES,
  type AnchorAction,
  type AnchorSide,
  type AnchorState,
  type AnchorStatus,
  type BerthState,
  type FenderContactState,
  type MooringAction,
  type MooringLineState,
  type MooringStatus,
  type TugMode,
  type TugState,
  type TugStatus,
} from '../types/external.js';
import { clamp, knotsToMps, normalizeHeading } from './angles.js';

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function finite(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

function oneOf<T extends string>(v: unknown, allowed: readonly T[]): T | undefined {
  return typeof v === 'string' && (allowed as readonly string[]).includes(v) ? (v as T) : undefined;
}

/** 一節錨鏈長度(m;particulars anchors.shackleLength_m 預設) */
export const SHACKLE_LENGTH_M = 27.5;

// ------------------------------------------------------------------ 標籤

export const TUG_MODE_LABEL: Readonly<Record<TugMode, string>> = { push: '推頂', pull: '拖拉', standby: '待命', free: '解纜', unavailable: '已離開' };

export const TUG_STATUS_LABEL: Readonly<Record<TugStatus, string>> = {
  standby: '待命',
  connecting: '帶纜/靠上中',
  repositioning: '推↔拉換位中',
  working: '出力中',
  free: '已解纜',
  parted: '拖纜斷裂',
  unavailable: '已離開(本練習不可用)',
};

export const ANCHOR_SIDE_LABEL: Readonly<Record<AnchorSide, string>> = { port: '左錨', starboard: '右錨' };

export const ANCHOR_STATUS_LABEL: Readonly<Record<AnchorStatus, string>> = {
  stowed: '收存',
  running: '送出中',
  holding: '抓住',
  dragging: '走錨',
  heaving: '起錨中',
  aweigh: '錨離底',
};

export const ANCHOR_ACTION_LABEL: Readonly<Record<AnchorAction, string>> = {
  letGo: '拋錨',
  veer: '鬆鏈',
  heave: '起錨',
  brake: '剎車',
  release: '放鏈',
};

export const MOORING_STATUS_LABEL: Readonly<Record<MooringStatus, string>> = {
  stowed: '收存',
  fast: '已繫',
  heaving: '絞纜中',
  slacking: '鬆纜中',
  letGo: '已解纜',
  parted: '斷裂',
};

export const MOORING_ACTION_LABEL: Readonly<Record<MooringAction, string>> = { makeFast: '帶纜', heave: '絞纜', slack: '鬆纜', letGo: '解纜' };

/** commandRejected 的 reason(核心 CommandRejectReasons;設計文件第 6.2 節的 19 種)。 */
export const EXTERNAL_REJECT_REASON_LABEL: Readonly<Record<string, string>> = {
  externalForcesUnsupported: '動力學模型不支援外力(拖船/錨/繫纜)',
  unknownTug: '未定義的拖船',
  unknownTugMode: '未知的拖船模式',
  tugModeUnavailable: '此拖船不可用該模式',
  tugLineParted: '拖纜已斷,不能拖拉',
  tugUnavailable: '拖船已離開,本練習不可用',
  invalidPower: '出力需在 0–100 %',
  noAnchorData: '船舶資料沒有錨具規格',
  unknownAnchor: '未知的錨(左/右)',
  anchorAlreadyOut: '錨已拋出(著底或送出中)',
  anchorStowed: '錨仍收存,需先拋錨',
  chainFullyOut: '錨鏈已全部送出',
  invalidTarget: '目標鏈長無效',
  unknownAction: '未知的動作',
  noBerth: '情境沒有泊位',
  unknownMooringLine: '未定義的纜繩',
  unknownBollard: '未定義的繫船柱',
  bollardOutOfReach: '繫船柱超出纜長',
  mooringLineNotFast: '纜繩未繫妥',
  mooringLineParted: '纜繩已斷',
};

// ------------------------------------------------------------------ 解析

function parsePosition(v: unknown, issues: string[], path: string): Position | undefined {
  if (v === undefined || v === null) return undefined;
  if (!isRecord(v)) {
    issues.push(path);
    return undefined;
  }
  const x = finite(v['x']);
  const y = finite(v['y']);
  if (x === undefined || y === undefined) {
    issues.push(path);
    return undefined;
  }
  return { lat: finite(v['lat']) ?? 0, lon: finite(v['lon']) ?? 0, x, y };
}

function reqNum(item: Record<string, unknown>, key: string, issues: string[], path: string): number {
  const v = finite(item[key]);
  if (v === undefined) {
    issues.push(`${path}.${key}`);
    return 0;
  }
  return v;
}

function optNum(item: Record<string, unknown>, key: string, issues: string[], path: string): number | undefined {
  const v = item[key];
  if (v === undefined || v === null) return undefined;
  const n = finite(v);
  if (n === undefined) issues.push(`${path}.${key}`);
  return n;
}

/** 解析 tugs[]:缺 id 的項目略過並記入 issues;列舉外的模式/狀態以 standby 補上並記入 issues;方向正規化至 0–360。 */
export function parseTugs(raw: readonly unknown[], issues: string[]): TugState[] {
  const out: TugState[] = [];
  raw.forEach((item, i) => {
    const path = `tugs[${i}]`;
    if (!isRecord(item)) {
      issues.push(path);
      return;
    }
    const id = typeof item['id'] === 'string' ? item['id'] : '';
    if (!id) {
      issues.push(`${path}.id`);
      return;
    }
    const mode = oneOf(item['mode'], TUG_MODES);
    if (!mode) issues.push(`${path}.mode`);
    const status = oneOf(item['status'], TUG_STATUSES);
    if (!status) issues.push(`${path}.status`);
    const t: TugState = {
      id,
      mode: mode ?? 'standby',
      status: status ?? 'standby',
      force_t: Math.max(0, reqNum(item, 'force_t', issues, path)),
      direction_deg: normalizeHeading(reqNum(item, 'direction_deg', issues, path)),
    };
    if (typeof item['name'] === 'string' && item['name']) t.name = item['name'];
    const order = optNum(item, 'directionOrder_deg', issues, path);
    if (order !== undefined) t.directionOrder_deg = normalizeHeading(order);
    for (const key of ['power_pct', 'efficiency', 'bollardPull_t'] as const) {
      const v = optNum(item, key, issues, path);
      if (v !== undefined) t[key] = v;
    }
    if (typeof item['lineParted'] === 'boolean') t.lineParted = item['lineParted'];
    const pos = parsePosition(item['pos'], issues, `${path}.pos`);
    if (pos) t.pos = pos;
    out.push(t);
  });
  return out;
}

/** 解析 anchors[]:side 無效的項目略過並記入 issues;length_shackles 缺時由 length_m / 27.5 推得。 */
export function parseAnchors(raw: readonly unknown[], issues: string[]): AnchorState[] {
  const out: AnchorState[] = [];
  raw.forEach((item, i) => {
    const path = `anchors[${i}]`;
    if (!isRecord(item)) {
      issues.push(path);
      return;
    }
    const side = oneOf(item['side'], ANCHOR_SIDES);
    if (!side) {
      issues.push(`${path}.side`);
      return;
    }
    const status = oneOf(item['status'], ANCHOR_STATUSES);
    if (!status) issues.push(`${path}.status`);
    const a: AnchorState = {
      side,
      status: status ?? 'stowed',
      length_m: Math.max(0, reqNum(item, 'length_m', issues, path)),
      tension_t: Math.max(0, reqNum(item, 'tension_t', issues, path)),
      onBottom_m: Math.max(0, reqNum(item, 'onBottom_m', issues, path)),
    };
    for (const key of ['length_shackles', 'maxLength_m', 'horizontalTension_t', 'holding_t'] as const) {
      const v = optNum(item, key, issues, path);
      if (v !== undefined) a[key] = v;
    }
    if (a.length_shackles === undefined) a.length_shackles = a.length_m / SHACKLE_LENGTH_M;
    if (typeof item['brake'] === 'boolean') a.brake = item['brake'];
    if (typeof item['fullyOut'] === 'boolean') a.fullyOut = item['fullyOut'];
    const pos = parsePosition(item['position'], issues, `${path}.position`);
    if (pos) a.position = pos;
    out.push(a);
  });
  // 固定順序:左錨、右錨(面板與讀數排列)
  return out.sort((x, y) => ANCHOR_SIDES.indexOf(x.side) - ANCHOR_SIDES.indexOf(y.side));
}

/** 解析 berth:缺 id 時記入 issues 仍保留;contacts[]/lines[] 缺欄位的項目略過。 */
export function parseBerth(raw: Record<string, unknown>, issues: string[]): BerthState {
  const id = typeof raw['id'] === 'string' ? raw['id'] : '';
  if (!id) issues.push('berth.id');
  const b: BerthState = { id, contacts: [], lines: [] };
  if (typeof raw['name'] === 'string' && raw['name']) b.name = raw['name'];
  if (typeof raw['quayContact'] === 'boolean') b.quayContact = raw['quayContact'];
  if (Array.isArray(raw['contacts'])) {
    raw['contacts'].forEach((c, i) => {
      const path = `berth.contacts[${i}]`;
      if (!isRecord(c) || typeof c['fenderId'] !== 'string' || !c['fenderId']) {
        issues.push(path);
        return;
      }
      const fc: FenderContactState = {
        fenderId: c['fenderId'],
        deflection_m: Math.max(0, reqNum(c, 'deflection_m', issues, path)),
        reaction_kN: Math.max(0, reqNum(c, 'reaction_kN', issues, path)),
      };
      const pct = optNum(c, 'deflection_pct', issues, path);
      if (pct !== undefined) fc.deflection_pct = pct;
      if (typeof c['overload'] === 'boolean') fc.overload = c['overload'];
      b.contacts.push(fc);
    });
  } else if (raw['contacts'] !== undefined) issues.push('berth.contacts');
  if (Array.isArray(raw['lines'])) {
    raw['lines'].forEach((l, i) => {
      const path = `berth.lines[${i}]`;
      if (!isRecord(l) || typeof l['id'] !== 'string' || !l['id']) {
        issues.push(path);
        return;
      }
      const status = oneOf(l['status'], MOORING_STATUSES);
      if (!status) issues.push(`${path}.status`);
      const ml: MooringLineState = { id: l['id'], status: status ?? 'stowed', tension_t: Math.max(0, reqNum(l, 'tension_t', issues, path)) };
      const mbl = optNum(l, 'mbl_t', issues, path);
      if (mbl !== undefined) ml.mbl_t = mbl;
      const len = optNum(l, 'length_m', issues, path);
      if (len !== undefined) ml.length_m = len;
      if (typeof l['bollardId'] === 'string' && l['bollardId']) ml.bollardId = l['bollardId'];
      b.lines.push(ml);
    });
  } else if (raw['lines'] !== undefined) issues.push('berth.lines');
  return b;
}

/** state-parser 呼叫:有欄位才放進 state(舊狀態、Python 參考伺服器、無拖船/泊位的情境省略,不記問題)。 */
export function applyExternalForceBlocks(raw: Record<string, unknown>, state: OwnShipState, issues: string[]): void {
  if (Array.isArray(raw['tugs'])) state.tugs = parseTugs(raw['tugs'], issues);
  else if (raw['tugs'] !== undefined && raw['tugs'] !== null) issues.push('tugs');
  if (Array.isArray(raw['anchors'])) state.anchors = parseAnchors(raw['anchors'], issues);
  else if (raw['anchors'] !== undefined && raw['anchors'] !== null) issues.push('anchors');
  if (isRecord(raw['berth'])) state.berth = parseBerth(raw['berth'], issues);
  else if (raw['berth'] !== undefined && raw['berth'] !== null) issues.push('berth');
}

// ------------------------------------------------------------------ 指令

function round1(v: number): number {
  const r = Math.round(v * 10) / 10;
  return r === 0 ? 0 : r;
}

export interface TugOrder {
  mode?: TugMode;
  /** 作用於自船的力方向(相對船艏順時針,度) */
  direction_deg?: number;
  /** 出力 (%) */
  power_pct?: number;
}

/** tugControl:只放有給的欄位(省略 = 核心保持);方向正規化至 [0, 360) 取整數度、出力限 0–100 取整數。 */
export function tugControlCommand(id: string, order: TugOrder = {}): SimCommand {
  const trimmed = id.trim();
  if (!trimmed) throw new RangeError('拖船 id 不可為空');
  const args: Record<string, unknown> = { id: trimmed };
  if (order.mode && (TUG_MODES as readonly string[]).includes(order.mode)) args['mode'] = order.mode;
  if (finite(order.direction_deg) !== undefined) args['direction_deg'] = Math.round(normalizeHeading(order.direction_deg!)) % 360;
  if (finite(order.power_pct) !== undefined) args['power_pct'] = Math.round(clamp(order.power_pct!, 0, 100));
  return { type: 'tugControl', args };
}

export interface AnchorTarget {
  /** 目標鏈長(節) */
  shackles?: number;
  /** 目標鏈長(m;優先於節) */
  length_m?: number;
}

/** anchor:length_m 優先(有給就不送節數);負值或非有限值略過(letGo/heave 省略目標 = 鏈端/收妥)。brake 不帶目標。 */
export function anchorCommand(side: AnchorSide, action: AnchorAction, target: AnchorTarget = {}): SimCommand {
  if (!(ANCHOR_SIDES as readonly string[]).includes(side)) throw new RangeError(`無效的錨:${String(side)}`);
  if (!(ANCHOR_ACTIONS as readonly string[]).includes(action)) throw new RangeError(`無效的錨動作:${String(action)}`);
  const args: Record<string, unknown> = { side, action };
  if (action !== 'brake' && action !== 'release') {
    const m = finite(target.length_m);
    const sh = finite(target.shackles);
    if (m !== undefined && m >= 0) args['length_m'] = round1(m);
    else if (sh !== undefined && sh >= 0) args['targetLength_shackles'] = Math.round(sh * 100) / 100;
  }
  return { type: 'anchor', args };
}

export interface MooringOrder {
  /** makeFast 的繫船柱(省略 = 纜的預設繫船柱) */
  bollard?: string;
  /** makeFast 預張力 / heave 目標張力 (t) */
  tension_t?: number;
  /** slack 放出長度 (m,> 0) */
  length_m?: number;
}

/** mooring:依動作只放相關欄位(makeFast:bollard、tension_t;heave:tension_t;slack:length_m;letGo:無)。 */
export function mooringCommand(lineId: string, action: MooringAction, order: MooringOrder = {}): SimCommand {
  const trimmed = lineId.trim();
  if (!trimmed) throw new RangeError('纜繩 id 不可為空');
  if (!(MOORING_ACTIONS as readonly string[]).includes(action)) throw new RangeError(`無效的繫纜動作:${String(action)}`);
  const args: Record<string, unknown> = { lineId: trimmed, action };
  const tension = finite(order.tension_t);
  if (action === 'makeFast') {
    if (order.bollard && order.bollard.trim()) args['bollard'] = order.bollard.trim();
    if (tension !== undefined && tension >= 0) args['tension_t'] = round1(tension);
  } else if (action === 'heave') {
    if (tension !== undefined && tension >= 0) args['tension_t'] = round1(tension);
  } else if (action === 'slack') {
    const len = finite(order.length_m);
    if (len !== undefined && len > 0) args['length_m'] = round1(len);
  }
  return { type: 'mooring', args };
}

/** 注入拖纜斷裂故障(fault-catalogue tugLineBreak;id 省略 = 核心選第一艘拉中者)。 */
export function tugLineBreakCommand(id?: string): SimCommand {
  return id && id.trim() ? { type: 'injectFault', args: { name: 'tugLineBreak', args: { id: id.trim() } } } : { type: 'injectFault', value: 'tugLineBreak' };
}

// ------------------------------------------------------------------ 顯示用分級

export type LoadLevel = 'ok' | 'caution' | 'alarm';

/** 纜繩張力相對 MBL:< 50 % ok、50–80 % caution、≥ 80 % 或斷裂 alarm(**假設**:常見繫泊作業管理以 MBL 的 50 % 為工作上限)。 */
export function lineLoadLevel(l: Pick<MooringLineState, 'tension_t' | 'mbl_t' | 'status'>): LoadLevel {
  if (l.status === 'parted') return 'alarm';
  const r = lineLoadRatio(l);
  return r >= 0.8 ? 'alarm' : r >= 0.5 ? 'caution' : 'ok';
}

export function lineLoadRatio(l: Pick<MooringLineState, 'tension_t' | 'mbl_t'>): number {
  return l.mbl_t && l.mbl_t > 0 ? Math.max(0, l.tension_t / l.mbl_t) : 0;
}

/** 碰墊壓縮相對額定壓縮(預設 72 %):< 60 % ok、60–100 % caution、過載 alarm。 */
export function fenderLoadLevel(c: Pick<FenderContactState, 'deflection_pct' | 'overload'>, ratedDeflection_pct = 72): LoadLevel {
  if (c.overload) return 'alarm';
  const r = (c.deflection_pct ?? 0) / (ratedDeflection_pct > 0 ? ratedDeflection_pct : 72);
  return r >= 1 ? 'alarm' : r >= 0.6 ? 'caution' : 'ok';
}

/** 錨:走錨 alarm;張力超過抓地力 80 % 或剎車滑出風險 caution。 */
export function anchorLevel(a: Pick<AnchorState, 'status' | 'horizontalTension_t' | 'tension_t' | 'holding_t'>): LoadLevel {
  if (a.status === 'dragging') return 'alarm';
  const h = a.horizontalTension_t ?? a.tension_t;
  if (a.holding_t !== undefined && a.holding_t > 0 && h >= 0.8 * a.holding_t && a.status !== 'stowed') return 'caution';
  return 'ok';
}

/** 錨鏈長度的顯示:「4.0 節(110 m)」。 */
export function chainText(a: Pick<AnchorState, 'length_m' | 'length_shackles'>): string {
  const sh = a.length_shackles ?? a.length_m / SHACKLE_LENGTH_M;
  return `${sh.toFixed(1)} 節(${Math.round(a.length_m)} m)`;
}

/** 拖船顯示名稱:name(id) 或 id。 */
export function tugName(t: Pick<TugState, 'id' | 'name'>): string {
  return t.name && t.name !== t.id ? `${t.name}(${t.id})` : t.id;
}

/** 拖船力方向的口語描述(相對船艏;0 向前、90 向右舷):例如 270 → 「推/拉向左舷」。 */
export function tugDirectionText(deg: number): string {
  const d = normalizeHeading(deg);
  const sector = Math.round(d / 45) % 8;
  return ['向前', '向右前', '向右舷', '向右後', '向後', '向左後', '向左舷', '向左前'][sector]!;
}

// ------------------------------------------------------------------ 事件文字與嚴重度

const EXTERNAL_KINDS: ReadonlySet<string> = new Set([
  'tugCommand',
  'tugLineParted',
  'shipInteraction',
  'anchorLetGo',
  'anchorCommand',
  'anchorBroughtUp',
  'anchorDragging',
  'anchorHolding',
  'anchorAweigh',
  'anchorHoused',
  'anchorChainFullyOut',
  'anchorBrakeSlipping',
  'anchorWindlassStalled',
  'fenderContact',
  'quayContact',
  'mooringCommand',
  'mooringLineParted',
]);

/** 由指令產生的事件 kind(來源依指令來源;其餘外力事件來源為 engine)。 */
export const EXTERNAL_COMMAND_EVENT_KINDS: ReadonlySet<string> = new Set(['tugCommand', 'anchorLetGo', 'anchorCommand', 'mooringCommand']);

export function isExternalForceEvent(kind: string): boolean {
  return EXTERNAL_KINDS.has(kind);
}

/** 靠泊接觸時的法向速度警示門檻(m/s;**假設**:萬噸級以下油輪靠泊速度一般建議 ≤ 0.15 m/s,待公司程序參數集) */
export const BERTHING_SPEED_CAUTION_MPS = 0.15;

function dn(data: Record<string, unknown> | undefined, key: string, digits = 1): string {
  const v = data?.[key];
  return typeof v === 'number' && Number.isFinite(v) ? v.toFixed(digits) : '?';
}

function ds(data: Record<string, unknown> | undefined, key: string): string {
  const v = data?.[key];
  return typeof v === 'string' ? v : '';
}

function sideText(e: SimEvent): string {
  const side = ds(e.data, 'side') || e.itemId || '';
  return ANCHOR_SIDE_LABEL[side as AnchorSide] ?? side;
}

function targetText(data: Record<string, unknown> | undefined): string {
  const m = finite(data?.['targetLength_m']);
  if (m === undefined) return '';
  const sh = finite(data?.['targetLength_shackles']) ?? m / SHACKLE_LENGTH_M;
  return ` 目標 ${sh.toFixed(1)} 節(${Math.round(m)} m)`;
}

/** 外力事件的一句話(繁體中文);非外力事件回傳 null。 */
export function externalEventLabel(e: SimEvent): string | null {
  if (!EXTERNAL_KINDS.has(e.kind)) return null;
  const d = e.data;
  switch (e.kind) {
    case 'tugCommand': {
      const id = ds(d, 'id') || e.itemId || '?';
      const mode = ds(d, 'mode');
      const from = ds(d, 'from');
      const status = ds(d, 'status');
      const modeText = TUG_MODE_LABEL[mode as TugMode] ?? mode;
      const change = from && from !== mode ? `${TUG_MODE_LABEL[from as TugMode] ?? from} → ${modeText}` : modeText;
      const dir = finite(d?.['direction_deg']);
      const dirText = dir !== undefined ? ` ${Math.round(dir).toString().padStart(3, '0')}°(${tugDirectionText(dir)})` : '';
      return `拖船令 ${id} ${change}${dirText} ${dn(d, 'power_pct', 0)} %${status ? `(${TUG_STATUS_LABEL[status as TugStatus] ?? status})` : ''}`;
    }
    case 'tugLineParted':
      return `拖纜斷裂 ${ds(d, 'id') || e.itemId || '?'}(斷前 ${dn(d, 'force_t')} t${d?.['stillPushing'] === true ? ',推頂中仍可續推' : ''})`;
    case 'shipInteraction': {
      // 核心 RaiseInteractionTransition:data {phase enter|exit, targetId, forceSway_t, momentYaw_tm, threshold_t}
      const target = ds(d, 'targetId') || e.targetId || '?';
      if (ds(d, 'phase') === 'exit') return `船間交互作用結束 ${target}`;
      const sway = finite(d?.['forceSway_t']);
      const yaw = finite(d?.['momentYaw_tm']);
      return `船間交互作用 ${target}:側向力 ${sway !== undefined ? `${sway >= 0 ? '+' : ''}${sway.toFixed(1)} t` : '?'}、艏搖 ${yaw !== undefined ? `${yaw >= 0 ? '+' : ''}${Math.round(yaw)} t·m` : '?'}(超過門檻)`;
    }
    case 'anchorLetGo':
      return `${sideText(e)}拋錨${targetText(d) || '(至鏈端)'}`;
    case 'anchorCommand': {
      const action = ds(d, 'action');
      return `${sideText(e)} ${ANCHOR_ACTION_LABEL[action as AnchorAction] ?? action}${targetText(d)}${finite(d?.['length_m']) !== undefined ? ` · 目前 ${Math.round(finite(d?.['length_m'])!)} m` : ''}`;
    }
    case 'anchorBroughtUp':
      return `${sideText(e)}已帶上 鏈長 ${dn(d, 'length_m', 0)} m 張力 ${dn(d, 'tension_t')} t`;
    case 'anchorDragging':
      return `${sideText(e)}走錨 張力 ${dn(d, 'tension_t')} t > 抓地力 ${dn(d, 'holding_t')} t`;
    case 'anchorHolding':
      return `${sideText(e)}抓住 張力 ${dn(d, 'tension_t')} t / 抓地力 ${dn(d, 'holding_t')} t`;
    case 'anchorAweigh':
      return `${sideText(e)}離底(anchor aweigh)`;
    case 'anchorHoused':
      return `${sideText(e)}收妥`;
    case 'anchorChainFullyOut':
      return `${sideText(e)}錨鏈全出 ${dn(d, 'shackles', 1)} 節(${dn(d, 'length_m', 0)} m)`;
    case 'anchorBrakeSlipping':
      return `${sideText(e)}剎車滑出 張力 ${dn(d, 'tension_t')} t > 握持 ${dn(d, 'brakeHolding_t')} t`;
    case 'anchorWindlassStalled':
      return `${sideText(e)}絞盤超載停轉 張力 ${dn(d, 'tension_t')} t(限 ${dn(d, 'windlassLimit_t')} t)`;
    case 'fenderContact': {
      const id = ds(d, 'fenderId') || e.itemId || '?';
      return `碰墊 ${id} 接觸 v ${dn(d, 'normalSpeed_mps', 2)} m/s 能量 ${dn(d, 'energy_kNm', 0)} kN·m(額定 ${dn(d, 'ratedEnergy_kNm', 0)})壓縮 ${dn(d, 'deflection_pct', 0)} %${d?.['overload'] === true ? ' — 過載' : ''}`;
    }
    case 'quayContact':
      return `船體觸碰碼頭 ${ds(d, 'berthId') || e.itemId || ''} 穿入 ${dn(d, 'penetration_m', 2)} m v ${dn(d, 'normalSpeed_mps', 2)} m/s`.replace(/\s+/g, ' ');
    case 'mooringCommand': {
      const id = ds(d, 'lineId') || e.itemId || '?';
      const action = ds(d, 'action');
      const parts = [`繫纜 ${id} ${MOORING_ACTION_LABEL[action as MooringAction] ?? action}`];
      if (ds(d, 'bollardId') && action === 'makeFast') parts.push(`→ ${ds(d, 'bollardId')}`);
      if (finite(d?.['tension_t']) !== undefined) parts.push(`${action === 'heave' ? '目標' : '預張力'} ${dn(d, 'tension_t')} t`);
      if (finite(d?.['length_m']) !== undefined && action === 'slack') parts.push(`放 ${dn(d, 'length_m')} m`);
      return parts.join(' ');
    }
    case 'mooringLineParted':
      return `纜繩斷裂 ${ds(d, 'lineId') || e.itemId || '?'} 張力 ${dn(d, 'tension_t')} t > MBL ${dn(d, 'mbl_t', 0)} t${ds(d, 'bollardId') ? `(${ds(d, 'bollardId')})` : ''}`;
    default:
      return null;
  }
}

/** 外力事件的嚴重度;非外力事件回傳 null。斷纜、走錨、碼頭硬接觸、碰墊過載為 alarm。 */
export function externalEventSeverity(e: SimEvent): 'info' | 'warning' | 'alarm' | null {
  if (!EXTERNAL_KINDS.has(e.kind)) return null;
  switch (e.kind) {
    case 'tugLineParted':
    case 'mooringLineParted':
    case 'anchorDragging':
    case 'quayContact':
      return 'alarm';
    case 'shipInteraction':
      return e.data?.['phase'] === 'exit' ? 'info' : 'warning';
    case 'fenderContact': {
      if (e.data?.['overload'] === true) return 'alarm';
      const v = finite(e.data?.['normalSpeed_mps']);
      return v !== undefined && v > BERTHING_SPEED_CAUTION_MPS ? 'warning' : 'info';
    }
    case 'anchorBrakeSlipping':
    case 'anchorWindlassStalled':
    case 'anchorChainFullyOut':
      return 'warning';
    default:
      return 'info';
  }
}

// ------------------------------------------------------------------ 幾何

/**
 * 經緯度 → 本地 ENU(m),與核心 Geo/LocalTangentPlane 相同:以原點緯度的 WGS-84 子午圈/卯酉圈曲率半徑為尺度。
 * (航線疊圖既有的 latLonToEnu 用球面 111,320 m/度,在北緯 24° 約有 0.5 % 尺度差;碼頭/碰墊需要公尺級,故另用此式。)
 */
export function wgs84ToEnu(origin: { lat: number; lon: number }, lat: number, lon: number): { x: number; y: number } {
  const a = 6378137.0;
  const f = 1 / 298.257223563;
  const e2 = f * (2 - f);
  const phi = (origin.lat * Math.PI) / 180;
  const s2 = Math.sin(phi) ** 2;
  const m = (a * (1 - e2)) / Math.pow(1 - e2 * s2, 1.5);
  const n = a / Math.sqrt(1 - e2 * s2);
  const perDegLat = (m * Math.PI) / 180;
  const perDegLon = (n * Math.cos(phi) * Math.PI) / 180;
  return { x: (lon - origin.lon) * perDegLon, y: (lat - origin.lat) * perDegLat };
}

/** 船體座標點規格:具名或 {x, y}(m;x 向艏、y 向右舷、原點船舯)。 */
export type BodyPointSpec = string | { x: number; y: number };

export const BODY_POINT_NAMES: readonly string[] = ['fwd', 'aft', 'bow', 'stern', 'fwdPort', 'fwdStbd', 'midPort', 'midStbd', 'aftPort', 'aftStbd'];

/** 具名點換算(與核心 ScenarioBodyPoint.Resolve 相同):fwd/bow = (+0.45 L, 0)…;未知名稱回傳 null。 */
export function resolveBodyPoint(spec: BodyPointSpec | undefined, lpp_m: number, breadth_m: number): { x: number; y: number } | null {
  if (spec === undefined) return null;
  if (typeof spec === 'object') return Number.isFinite(spec.x) && Number.isFinite(spec.y) ? { x: spec.x, y: spec.y } : null;
  const l = lpp_m;
  const b = breadth_m;
  switch (spec.toLowerCase()) {
    case 'fwd':
    case 'bow':
      return { x: 0.45 * l, y: 0 };
    case 'aft':
    case 'stern':
      return { x: -0.45 * l, y: 0 };
    case 'fwdport':
      return { x: 0.4 * l, y: -0.45 * b };
    case 'fwdstbd':
      return { x: 0.4 * l, y: 0.45 * b };
    case 'midport':
      return { x: 0, y: -0.5 * b };
    case 'midstbd':
      return { x: 0, y: 0.5 * b };
    case 'aftport':
      return { x: -0.4 * l, y: -0.45 * b };
    case 'aftstbd':
      return { x: -0.4 * l, y: 0.45 * b };
    default:
      return null;
  }
}

/** 錨鏈孔位置(船體座標):particulars anchors.hawsePipe(經 ship-config)優先;否則核心的估計式 x = 0.47 L、|y| = 0.15 B。 */
export function hawsePoint(side: AnchorSide, lpp_m: number, breadth_m: number, hawse?: { hawseX_m: number; hawseY_m: number }): { x: number; y: number } {
  const sign = side === 'port' ? -1 : 1;
  return hawse ? { x: hawse.hawseX_m, y: sign * hawse.hawseY_m } : { x: 0.47 * lpp_m, y: sign * 0.15 * breadth_m };
}

function parseBodyPoint(v: unknown): BodyPointSpec | undefined {
  if (typeof v === 'string' && v) return v;
  if (isRecord(v) && finite(v['x']) !== undefined && finite(v['y']) !== undefined) return { x: v['x'] as number, y: v['y'] as number };
  return undefined;
}

/** 情境 tugs[] 的顯示所需欄位。 */
export interface ScenarioTugDef {
  id: string;
  name?: string;
  bollardPull_t?: number;
  attach?: BodyPointSpec;
  lineLength_m?: number;
  /** 可用模式(省略 = 推與拉) */
  modes?: Array<'push' | 'pull'>;
}

/** 情境 berth.lines[] 的顯示所需欄位。 */
export interface ScenarioMooringLineDef {
  id: string;
  fairlead?: BodyPointSpec;
  bollard?: string;
  mbl_t?: number;
  length_m?: number;
  winchPull_t?: number;
  pretension_t?: number;
  /** 情境開始時的纜繩狀態(scenario.schema.json berth.lines[].initial;省略 = stowed;fast 需 bollard) */
  initial?: 'stowed' | 'fast';
}

export function parseScenarioTugs(raw: unknown): ScenarioTugDef[] {
  if (!Array.isArray(raw)) return [];
  const out: ScenarioTugDef[] = [];
  for (const t of raw) {
    if (!isRecord(t) || typeof t['id'] !== 'string' || !t['id']) continue;
    const def: ScenarioTugDef = { id: t['id'] };
    if (typeof t['name'] === 'string' && t['name']) def.name = t['name'];
    const bp = finite(t['bollardPull_t']);
    if (bp !== undefined) def.bollardPull_t = bp;
    const attach = parseBodyPoint(t['attach']);
    if (attach !== undefined) def.attach = attach;
    const ll = finite(t['lineLength_m']);
    if (ll !== undefined) def.lineLength_m = ll;
    if (Array.isArray(t['modes'])) def.modes = t['modes'].filter((m): m is 'push' | 'pull' => m === 'push' || m === 'pull');
    out.push(def);
  }
  return out;
}

export function parseScenarioMooringLines(raw: unknown): ScenarioMooringLineDef[] {
  if (!Array.isArray(raw)) return [];
  const out: ScenarioMooringLineDef[] = [];
  for (const l of raw) {
    if (!isRecord(l) || typeof l['id'] !== 'string' || !l['id']) continue;
    const def: ScenarioMooringLineDef = { id: l['id'] };
    const fl = parseBodyPoint(l['fairlead']);
    if (fl !== undefined) def.fairlead = fl;
    if (typeof l['bollard'] === 'string' && l['bollard']) def.bollard = l['bollard'];
    for (const key of ['mbl_t', 'length_m', 'winchPull_t', 'pretension_t'] as const) {
      const v = finite(l[key]);
      if (v !== undefined) def[key] = v;
    }
    if (l['initial'] === 'stowed' || l['initial'] === 'fast') def.initial = l['initial'];
    out.push(def);
  }
  return out;
}

export interface BerthFenderGeometry {
  id: string;
  /** 碼頭面上的碰墊中心(ENU) */
  x: number;
  y: number;
  /** 指向水域的單位法向量(ENU) */
  nx: number;
  ny: number;
  /** 沿碼頭的單位切向量 */
  tx: number;
  ty: number;
  height_m: number;
  width_m: number;
  ratedDeflection_pct: number;
  ratedReaction_kN?: number;
  ratedEnergy_kNm?: number;
}

export interface BerthBollardGeometry {
  id: string;
  x: number;
  y: number;
  swl_t?: number;
}

/** 泊位幾何(本地 ENU;由情境 berth.definition 或泊位檔解析)。 */
export interface BerthGeometry {
  id: string;
  name?: string;
  /** 碼頭面折線 */
  quay: Array<{ x: number; y: number }>;
  waterSide: 'left' | 'right';
  fenders: BerthFenderGeometry[];
  bollards: BerthBollardGeometry[];
}

interface Segment {
  ax: number;
  ay: number;
  tx: number;
  ty: number;
  nx: number;
  ny: number;
  len: number;
  chain: number;
}

function segmentsOf(quay: ReadonlyArray<{ x: number; y: number }>, waterSide: 'left' | 'right'): Segment[] {
  const segs: Segment[] = [];
  let chain = 0;
  for (let i = 0; i + 1 < quay.length; i++) {
    const a = quay[i]!;
    const b = quay[i + 1]!;
    const len = Math.hypot(b.x - a.x, b.y - a.y);
    if (len < 1e-6) continue;
    const tx = (b.x - a.x) / len;
    const ty = (b.y - a.y) / len;
    // 與核心相同:ENU 中沿 t 前進的右手側 = (ty, −tx)
    const [nx, ny] = waterSide === 'right' ? [ty, -tx] : [-ty, tx];
    segs.push({ ax: a.x, ay: a.y, tx, ty, nx, ny, len, chain });
    chain += len;
  }
  return segs;
}

function locate(segs: readonly Segment[], chainage: number): { seg: Segment; s: number } {
  for (let i = 0; i < segs.length; i++) {
    const g = segs[i]!;
    if (chainage <= g.chain + g.len || i === segs.length - 1) return { seg: g, s: clamp(chainage - g.chain, 0, g.len) };
  }
  return { seg: segs[0]!, s: 0 };
}

/**
 * 泊位定義(scenario.schema.json $defs.berthDefinition 或泊位檔 berths[] 項目)→ ENU 幾何。
 * 碼頭點可為 {x, y} 或 {lat, lon}(需 origin);碰墊位於碼頭面里程處、向水域突出 height_m;繫船柱向陸側退縮 setback_m(預設 2 m)。
 * 少於 2 個可換算的碼頭點時回傳 null。
 */
export function berthGeometry(def: unknown, origin: { lat: number; lon: number } | null): BerthGeometry | null {
  if (!isRecord(def) || !Array.isArray(def['quay'])) return null;
  const quay: Array<{ x: number; y: number }> = [];
  for (const p of def['quay']) {
    if (!isRecord(p)) continue;
    const x = finite(p['x']);
    const y = finite(p['y']);
    if (x !== undefined && y !== undefined) quay.push({ x, y });
    else {
      const lat = finite(p['lat']);
      const lon = finite(p['lon']);
      if (lat !== undefined && lon !== undefined && origin) quay.push(wgs84ToEnu(origin, lat, lon));
    }
  }
  const waterSide = def['waterSide'] === 'left' ? 'left' : 'right';
  const segs = segmentsOf(quay, waterSide);
  if (segs.length === 0) return null;
  const types = isRecord(def['fenderTypes']) ? def['fenderTypes'] : {};
  const fenders: BerthFenderGeometry[] = [];
  if (Array.isArray(def['fenders'])) {
    for (const f of def['fenders']) {
      if (!isRecord(f) || typeof f['id'] !== 'string') continue;
      const ch = finite(f['chainage_m']);
      if (ch === undefined) continue;
      const type = typeof f['type'] === 'string' && isRecord(types[f['type']]) ? (types[f['type']] as Record<string, unknown>) : {};
      const { seg, s } = locate(segs, ch);
      const fg: BerthFenderGeometry = {
        id: f['id'],
        x: seg.ax + s * seg.tx,
        y: seg.ay + s * seg.ty,
        nx: seg.nx,
        ny: seg.ny,
        tx: seg.tx,
        ty: seg.ty,
        height_m: finite(type['height_m']) ?? 1,
        width_m: finite(type['width_m']) ?? 2,
        ratedDeflection_pct: finite(type['ratedDeflection_pct']) ?? 72,
      };
      const rr = finite(type['ratedReaction_kN']);
      if (rr !== undefined) fg.ratedReaction_kN = rr;
      const re = finite(type['ratedEnergy_kNm']);
      if (re !== undefined) fg.ratedEnergy_kNm = re;
      fenders.push(fg);
    }
  }
  const bollards: BerthBollardGeometry[] = [];
  if (Array.isArray(def['bollards'])) {
    for (const b of def['bollards']) {
      if (!isRecord(b) || typeof b['id'] !== 'string') continue;
      const ch = finite(b['chainage_m']);
      if (ch === undefined) continue;
      const setback = finite(b['setback_m']) ?? 2;
      const { seg, s } = locate(segs, ch);
      const bg: BerthBollardGeometry = { id: b['id'], x: seg.ax + s * seg.tx - setback * seg.nx, y: seg.ay + s * seg.ty - setback * seg.ny };
      const swl = finite(b['swl_t']);
      if (swl !== undefined) bg.swl_t = swl;
      bollards.push(bg);
    }
  }
  const out: BerthGeometry = { id: typeof def['id'] === 'string' ? def['id'] : '', quay, waterSide, fenders, bollards };
  if (typeof def['name'] === 'string' && def['name']) out.name = def['name'];
  return out;
}

/** 點到碼頭線的距離(m;水域側為正、陸側為負)與最近線段的水域法向量;無線段時 null。 */
export function quayDistance(geo: BerthGeometry, x: number, y: number): { distance_m: number; nx: number; ny: number } | null {
  const segs = segmentsOf(geo.quay, geo.waterSide);
  let best: { distance_m: number; nx: number; ny: number; abs: number } | null = null;
  for (const g of segs) {
    const along = clamp((x - g.ax) * g.tx + (y - g.ay) * g.ty, 0, g.len);
    const px = g.ax + along * g.tx;
    const py = g.ay + along * g.ty;
    const abs = Math.hypot(x - px, y - py);
    const signed = (x - px) * g.nx + (y - py) * g.ny;
    if (!best || abs < best.abs) best = { distance_m: signed >= 0 ? abs : -abs, nx: g.nx, ny: g.ny, abs };
  }
  return best ? { distance_m: best.distance_m, nx: best.nx, ny: best.ny } : null;
}

// ------------------------------------------------------------------ 靠泊讀數

export interface LateralSpeeds {
  /** 艏(+LPP/2)橫向速度 (m/s),右舷正 */
  bow_mps: number;
  /** 艉(−LPP/2)橫向速度 (m/s),右舷正 */
  stern_mps: number;
  /** 資料來源:'uvr' = 由 state.v、state.r 與 LPP 推算(對水);核心日後提供欄位時改用之 */
  source: 'uvr';
}

/** 艏/艉橫向速度:v_point = v + r·x(x = ±LPP/2;r 為 rad/s、右轉正;v 右舷正)。state 沒有對應欄位,由 u/v/r 推算(對水)。 */
export function lateralSpeeds(s: Pick<OwnShipState, 'v' | 'r'>, lpp_m: number): LateralSpeeds {
  const half = lpp_m / 2;
  return { bow_mps: s.v + s.r * half, stern_mps: s.v - s.r * half, source: 'uvr' };
}

export interface QuayApproach {
  /** 船舯到碼頭面的距離 (m,水域側正) */
  distance_m: number;
  /** 艏/艉到碼頭面的距離 (m) */
  bowDistance_m: number;
  sternDistance_m: number;
  /** 艏/艉朝碼頭的法向速度 (m/s,接近為正;對地:u/v/r 推算的對水速度加海流,與核心碰墊接觸速度、靠泊指標相同) */
  bowApproach_mps: number;
  sternApproach_mps: number;
}

/**
 * 對碼頭的接近量:艏/艉點(±LPP/2)的 ENU 對地速度投影到最近碼頭線段的水域法向量(接近 = 正)。
 * 對地速度 = 船體速度 (u, v + r·x) 轉 ENU + 海流(state.current:set 流向、drift kn),與核心 BodyFrame.PointGroundVelocity /
 * EnvironmentMath.GroundVelocity 同式;核心的碰墊接觸速度(fenderContact normalSpeed_mps)與 summary.berthing 都以它為準。
 * 沒有 current 欄位時只算對水速度。
 */
export function quayApproach(s: Pick<OwnShipState, 'pos' | 'heading' | 'u' | 'v' | 'r'> & { current?: { set: number; drift: number } }, geo: BerthGeometry, lpp_m: number): QuayApproach | null {
  const psi = (s.heading * Math.PI) / 180;
  const sn = Math.sin(psi);
  const c = Math.cos(psi);
  const cur = s.current;
  const curMps = cur && Number.isFinite(cur.drift) && Number.isFinite(cur.set) ? knotsToMps(cur.drift) : 0;
  const curSet = cur ? (cur.set * Math.PI) / 180 : 0;
  const ce = curMps * Math.sin(curSet);
  const cn = curMps * Math.cos(curSet);
  const pointOf = (xb: number) => {
    const e = s.pos.x + xb * sn;
    const n = s.pos.y + xb * c;
    // 船體速度 (u, v + r·x) → ENU,加海流 = 對地
    const vb = s.v + s.r * xb;
    const ve = s.u * sn + vb * c + ce;
    const vn = s.u * c - vb * sn + cn;
    const q = quayDistance(geo, e, n);
    return q ? { d: q.distance_m, approach: -(ve * q.nx + vn * q.ny) } : null;
  };
  const mid = quayDistance(geo, s.pos.x, s.pos.y);
  const bow = pointOf(lpp_m / 2);
  const stern = pointOf(-lpp_m / 2);
  if (!mid || !bow || !stern) return null;
  return { distance_m: mid.distance_m, bowDistance_m: bow.d, sternDistance_m: stern.d, bowApproach_mps: bow.approach, sternApproach_mps: stern.approach };
}

/** 操船台顯示靠泊讀數的距離門檻(m;船舯到碼頭面) */
export const BERTH_APPROACH_SHOW_M = 500;
/** 無泊位幾何(只有 state.berth)時以對地速度判斷「接近泊位」的門檻 (kn) */
export const BERTH_APPROACH_SHOW_SOG_KN = 4;

/** 帶符號速度的顯示:「右 0.12」「左 0.05」(m/s)。 */
export function signedLateralText(mps: number): string {
  const v = Math.abs(mps) < 0.005 ? 0 : mps;
  return v === 0 ? '0.00' : `${v > 0 ? '右' : '左'} ${Math.abs(v).toFixed(2)}`;
}

// ------------------------------------------------------------------ 情境 berth 解析

/** 情境 berth 區段(定義已解析:直接給的 definition,或 file + id 由泊位檔取得;重播時為紀錄 header 內嵌的 definition)。 */
export interface ScenarioBerth {
  file?: string;
  id?: string;
  /** 泊位定義(berthDefinition);找不到泊位檔/泊位 id 時為 null(面板仍可列纜繩) */
  definition: Record<string, unknown> | null;
  lines: ScenarioMooringLineDef[];
}

/**
 * 解析情境 berth:有 definition 直接用;否則以 file 的檔名在 areaFiles(鍵為路徑、值為泊位檔 JSON)中找,取 berths[] 中 id 相符者
 * (id 省略 = 第一個)。不是物件時回傳 null。
 */
export function resolveScenarioBerth(raw: unknown, areaFiles: Readonly<Record<string, unknown>> = {}): ScenarioBerth | null {
  if (!isRecord(raw)) return null;
  const out: ScenarioBerth = { definition: null, lines: parseScenarioMooringLines(raw['lines']) };
  if (typeof raw['file'] === 'string' && raw['file']) out.file = raw['file'];
  if (typeof raw['id'] === 'string' && raw['id']) out.id = raw['id'];
  if (isRecord(raw['definition'])) {
    out.definition = raw['definition'];
    return out;
  }
  if (out.file) {
    const base = out.file.replace(/\\/g, '/').split('/').pop() ?? out.file;
    const key = Object.keys(areaFiles).find((k) => (k.replace(/\\/g, '/').split('/').pop() ?? k) === base);
    const doc = key ? areaFiles[key] : undefined;
    if (isRecord(doc) && Array.isArray(doc['berths'])) {
      const berths = doc['berths'].filter(isRecord);
      const found = out.id ? berths.find((b) => b['id'] === out.id) : berths[0];
      if (found) {
        // 泊位檔頂層的 fenderTypes 逐鍵併入、泊位自己的型式優先(與核心 ScenarioExternalForces.LoadBerth 的 TryAdd 相同)
        const def: Record<string, unknown> = { ...found };
        if (isRecord(doc['fenderTypes'])) def['fenderTypes'] = { ...doc['fenderTypes'], ...(isRecord(found['fenderTypes']) ? found['fenderTypes'] : {}) };
        out.definition = def;
      }
    }
  }
  return out;
}
