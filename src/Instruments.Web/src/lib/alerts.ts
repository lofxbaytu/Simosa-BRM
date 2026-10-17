// 警報管理(BAM,MSC.302(87))、BNWAS(MSC.128(75))與感測器真值/指示值(docs/design/sensors-alerts.md):
//  - 解析:state.alerts[] / bnwas / truth / sensors / autopilot(state-parser 呼叫 applyAlertBlocks;舊狀態沒有時不記問題);
//  - 排序與顯示:sortAlerts(優先序 → 成立時間 → 編號,與核心相同)、alertVisual(IEC 62288 的顏色/圖示概念:
//    alarm 紅三角、warning 黃橙圓、caution 黃方;未確認閃爍、caution 不閃;靜音、已解除未確認的附加圖示);
//  - 指令:alertAcknowledge / alertSilence / bnwasMode / bnwasReset(command.schema.json);
//  - BNWAS 顯示(階段、倒數、等級);
//  - 由事件流推得警報歷史與 BNWAS 階段次數(教官站);事件文字與嚴重度(sim-event.ts 呼叫);
//  - 真值 vs 指示值比較列(教官站)。

import type { SimCommand } from '../types/command.js';
import type { SimEvent } from '../types/event.js';
import type { OwnShipState, Position } from '../types/state.js';
import {
  ALERT_CATEGORIES,
  ALERT_PRIORITIES,
  ALERT_STATES,
  BNWAS_MODES,
  type AlertCategory,
  type AlertItem,
  type AlertPriority,
  type AlertState,
  type AutopilotStatus,
  type BnwasMode,
  type BnwasStage,
  type BnwasStatus,
  type SensorStatus,
  type TruthState,
} from '../types/alerts.js';
import { clamp, headingDifference, normalizeHeading } from './angles.js';

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function finite(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

function oneOf<T extends string>(v: unknown, allowed: readonly T[]): T | undefined {
  return typeof v === 'string' && (allowed as readonly string[]).includes(v) ? (v as T) : undefined;
}

// ------------------------------------------------------------------ 標籤

export const ALERT_PRIORITY_LABEL: Readonly<Record<AlertPriority, string>> = {
  emergencyAlarm: '緊急警報',
  alarm: '警報',
  warning: '警告',
  caution: '注意',
};

export const ALERT_STATE_LABEL: Readonly<Record<AlertState, string>> = {
  activeUnacknowledged: '未確認',
  activeSilenced: '靜音中',
  activeAcknowledged: '已確認',
  rectifiedUnacknowledged: '已解除・未確認',
};

export const ALERT_SOURCE_LABEL: Readonly<Record<string, string>> = {
  gyro: '電羅經',
  gnss: 'GNSS',
  echoSounder: '測深儀',
  log: '計程儀',
  autopilot: '自動舵',
  steering: '舵機',
  engine: '主機',
  thruster: '側推',
  bnwas: 'BNWAS',
};

/** 警報目錄(設計文件第 3.1 節 AlertCatalogue.All;固定順序 = ALR 編號)。示範模式與預設文字使用。 */
export interface AlertCatalogueEntry {
  id: string;
  number: number;
  priority: AlertPriority;
  category: AlertCategory;
  source: string;
  text: string;
}

export const ALERT_CATALOGUE: readonly AlertCatalogueEntry[] = [
  { id: 'gyroFailure', number: 1, priority: 'alarm', category: 'B', source: 'gyro', text: '電羅經失效' },
  { id: 'gpsFailure', number: 2, priority: 'alarm', category: 'B', source: 'gnss', text: 'GNSS 失效' },
  { id: 'echoSounderFailure', number: 3, priority: 'warning', category: 'B', source: 'echoSounder', text: '測深儀失效' },
  { id: 'logFailure', number: 4, priority: 'warning', category: 'B', source: 'log', text: '計程儀失效' },
  { id: 'autopilotFailure', number: 5, priority: 'alarm', category: 'B', source: 'autopilot', text: '自動舵失效' },
  { id: 'steeringGear', number: 6, priority: 'alarm', category: 'B', source: 'steering', text: '舵機故障' },
  { id: 'steeringFuOnly', number: 7, priority: 'alarm', category: 'B', source: 'steering', text: 'FU 操舵失效' },
  { id: 'steeringPumpFailure', number: 8, priority: 'alarm', category: 'B', source: 'steering', text: '舵機泵失效' },
  { id: 'mainEngine', number: 9, priority: 'alarm', category: 'B', source: 'engine', text: '主機故障' },
  { id: 'bowThruster', number: 10, priority: 'warning', category: 'B', source: 'thruster', text: '側推故障' },
  { id: 'shallowWater', number: 11, priority: 'alarm', category: 'A', source: 'echoSounder', text: '淺水警報' },
  { id: 'offHeading', number: 12, priority: 'alarm', category: 'A', source: 'autopilot', text: '偏航警報' },
  { id: 'headingMonitor', number: 13, priority: 'warning', category: 'A', source: 'gyro', text: '航向監視' },
  { id: 'bnwas', number: 14, priority: 'caution', category: 'B', source: 'bnwas', text: 'BNWAS 值班警報' },
];

/** 核心未給 text 時的預設文字。 */
export const ALERT_TEXT: Readonly<Record<string, string>> = Object.fromEntries(ALERT_CATALOGUE.map((a) => [a.id, a.text]));

export const BNWAS_MODE_LABEL: Readonly<Record<BnwasMode, string>> = { off: '關', auto: '自動', on: '開' };

export const BNWAS_STAGE_LABEL: Readonly<Record<BnwasStage, string>> = {
  0: '休眠',
  1: '第 1 階段 · 駕駛台視覺',
  2: '第 2 階段 · 駕駛台聲響',
  3: '第 3 階段 · 呼叫後備值班官/船長',
};

/** BNWAS 休眠期範圍(MSC.128(75):3–12 分) */
export const BNWAS_DORMANT_MIN_S = 180;
export const BNWAS_DORMANT_MAX_S = 720;

// ------------------------------------------------------------------ 解析

/** 解析 alerts[]:缺 id 的項目略過並記入 issues;列舉外的優先序/類別/狀態以安全值補上並記入 issues。結果依 sortAlerts 排序。 */
export function parseAlertItems(raw: readonly unknown[], issues: string[]): AlertItem[] {
  const out: AlertItem[] = [];
  raw.forEach((item, i) => {
    const path = `alerts[${i}]`;
    if (!isRecord(item)) {
      issues.push(path);
      return;
    }
    const id = typeof item['id'] === 'string' ? item['id'] : '';
    if (!id) {
      issues.push(`${path}.id`);
      return;
    }
    const priority = oneOf(item['priority'], ALERT_PRIORITIES);
    if (!priority) issues.push(`${path}.priority`);
    const category = oneOf(item['category'], ALERT_CATEGORIES);
    if (!category) issues.push(`${path}.category`);
    const state = oneOf(item['state'], ALERT_STATES);
    if (!state) issues.push(`${path}.state`);
    const raisedAt = finite(item['raisedAt_s']);
    if (raisedAt === undefined) issues.push(`${path}.raisedAt_s`);
    const a: AlertItem = {
      id,
      number: Math.trunc(finite(item['number']) ?? 0),
      priority: priority ?? 'alarm',
      category: category ?? 'B',
      source: typeof item['source'] === 'string' ? item['source'] : '',
      state: state ?? 'activeUnacknowledged',
      text: typeof item['text'] === 'string' && item['text'] ? item['text'] : ALERT_TEXT[id] ?? id,
      raisedAt_s: raisedAt ?? 0,
      escalated: item['escalated'] === true,
      count: Math.max(1, Math.trunc(finite(item['count']) ?? 1)),
      audible: typeof item['audible'] === 'boolean' ? item['audible'] : defaultAudible(priority ?? 'alarm', state ?? 'activeUnacknowledged'),
    };
    const ack = finite(item['acknowledgedAt_s']);
    if (ack !== undefined) a.acknowledgedAt_s = ack;
    const sil = finite(item['silencedUntil_s']);
    if (sil !== undefined) a.silencedUntil_s = sil;
    out.push(a);
  });
  return sortAlerts(out);
}

/** 契約的 audible 規則(核心未給時使用):activeUnacknowledged 且優先序為 emergencyAlarm/alarm/warning。 */
export function defaultAudible(priority: AlertPriority, state: AlertState): boolean {
  return state === 'activeUnacknowledged' && priority !== 'caution';
}

export function parseBnwas(raw: Record<string, unknown>, issues: string[]): BnwasStatus | undefined {
  const mode = oneOf(raw['mode'], BNWAS_MODES);
  if (!mode) {
    issues.push('bnwas.mode');
    return undefined;
  }
  const stageRaw = finite(raw['stage']);
  const stage = (stageRaw !== undefined ? clamp(Math.trunc(stageRaw), 0, 3) : 0) as BnwasStage;
  if (stageRaw === undefined) issues.push('bnwas.stage');
  const dormant = finite(raw['dormant_s']);
  if (dormant === undefined) issues.push('bnwas.dormant_s');
  const out: BnwasStatus = { mode, active: raw['active'] === true, stage, dormant_s: dormant ?? BNWAS_DORMANT_MAX_S };
  const rem = finite(raw['remaining_s']);
  if (rem !== undefined) out.remaining_s = Math.max(0, rem);
  return out;
}

function parsePos(raw: unknown): Position | undefined {
  if (!isRecord(raw)) return undefined;
  const lat = finite(raw['lat']);
  const lon = finite(raw['lon']);
  const x = finite(raw['x']);
  const y = finite(raw['y']);
  if (lat === undefined || lon === undefined || x === undefined || y === undefined) return undefined;
  return { lat, lon, x, y };
}

export function parseTruth(raw: Record<string, unknown>, issues: string[]): TruthState | undefined {
  const heading = finite(raw['heading']);
  const pos = parsePos(raw['pos']);
  if (heading === undefined || !pos) {
    issues.push('truth');
    return undefined;
  }
  const n = (key: string, fallback: number): number => {
    const v = finite(raw[key]);
    if (v === undefined) {
      issues.push(`truth.${key}`);
      return fallback;
    }
    return v;
  };
  return {
    heading: normalizeHeading(heading),
    pos,
    cog: normalizeHeading(n('cog', heading)),
    sog: n('sog', 0),
    stw: n('stw', 0),
    depthBelowKeel: n('depthBelowKeel', 0),
    headingError_deg: n('headingError_deg', 0),
    positionError_m: n('positionError_m', 0),
  };
}

export function parseSensors(raw: Record<string, unknown>, issues: string[]): SensorStatus {
  const flag = (key: string): boolean => {
    const v = raw[key];
    if (typeof v === 'boolean') return v;
    issues.push(`sensors.${key}`);
    return true;
  };
  const mag = finite(raw['magneticHeading']);
  if (mag === undefined) issues.push('sensors.magneticHeading');
  return { headingValid: flag('headingValid'), positionValid: flag('positionValid'), depthValid: flag('depthValid'), stwValid: flag('stwValid'), magneticHeading: normalizeHeading(mag ?? 0) };
}

export function parseAutopilotStatus(raw: Record<string, unknown>, issues: string[]): AutopilotStatus {
  const enabled = raw['enabled'] === true;
  if (typeof raw['enabled'] !== 'boolean') issues.push('autopilot.enabled');
  const sh = finite(raw['setHeading']);
  if (sh === undefined) issues.push('autopilot.setHeading');
  const rot = finite(raw['rotLimit_degPerMin']);
  return { enabled, setHeading: normalizeHeading(sh ?? 0), rotLimit_degPerMin: rot ?? 15, available: raw['available'] !== false };
}

/**
 * 把原始狀態物件的 truth / sensors / autopilot / alerts / bnwas 解析到 state(state-parser 呼叫)。
 * 欄位省略(舊狀態、Python 參考伺服器)不記問題;型別錯誤記入 issues 但不影響其他欄位。
 */
export function applyAlertBlocks(raw: Record<string, unknown>, state: OwnShipState, issues: string[]): void {
  const present = (key: string): boolean => raw[key] !== undefined && raw[key] !== null;
  if (isRecord(raw['truth'])) {
    const t = parseTruth(raw['truth'], issues);
    if (t) state.truth = t;
  } else if (present('truth')) issues.push('truth');
  if (isRecord(raw['sensors'])) state.sensors = parseSensors(raw['sensors'], issues);
  else if (present('sensors')) issues.push('sensors');
  if (isRecord(raw['autopilot'])) state.autopilot = parseAutopilotStatus(raw['autopilot'], issues);
  else if (present('autopilot')) issues.push('autopilot');
  if (Array.isArray(raw['alerts'])) state.alerts = parseAlertItems(raw['alerts'], issues);
  else if (present('alerts')) issues.push('alerts');
  if (isRecord(raw['bnwas'])) {
    const b = parseBnwas(raw['bnwas'], issues);
    if (b) state.bnwas = b;
  } else if (present('bnwas')) issues.push('bnwas');
}

// ------------------------------------------------------------------ 排序與顯示

export const PRIORITY_RANK: Readonly<Record<AlertPriority, number>> = { emergencyAlarm: 0, alarm: 1, warning: 2, caution: 3 };

/** 排序:優先序(緊急警報 → 注意)→ 最近一次成立時間(早 → 晚)→ 編號;與核心 state.alerts[] 的順序相同。不改動輸入陣列。 */
export function sortAlerts(list: readonly AlertItem[]): AlertItem[] {
  return [...list].sort((a, b) => PRIORITY_RANK[a.priority] - PRIORITY_RANK[b.priority] || a.raisedAt_s - b.raisedAt_s || a.number - b.number || a.id.localeCompare(b.id));
}

export type AlertTone = 'emergency' | 'alarm' | 'warning' | 'caution';

/** IEC 62288 / MSC.302 圖示概念:alarm(含緊急)三角形、warning 圓形、caution 方形。 */
export type AlertShape = 'triangle' | 'circle' | 'square';

export interface AlertVisual {
  tone: AlertTone;
  shape: AlertShape;
  /** 未確認的 alarm/warning 閃爍;caution 不閃;已確認、靜音、已解除者不閃 */
  flashing: boolean;
  /** 靜音中(喇叭加斜線圖示) */
  silenced: boolean;
  /** 已解除未確認(打勾圖示) */
  rectified: boolean;
  /** 需要確認(caution 不需確認) */
  needsAck: boolean;
}

export function alertVisual(a: Pick<AlertItem, 'priority' | 'state'>): AlertVisual {
  const tone: AlertTone = a.priority === 'emergencyAlarm' ? 'emergency' : a.priority;
  const shape: AlertShape = a.priority === 'warning' ? 'circle' : a.priority === 'caution' ? 'square' : 'triangle';
  return {
    tone,
    shape,
    flashing: a.state === 'activeUnacknowledged' && a.priority !== 'caution',
    silenced: a.state === 'activeSilenced',
    rectified: a.state === 'rectifiedUnacknowledged',
    needsAck: a.state !== 'activeAcknowledged' && a.priority !== 'caution',
  };
}

export interface AlertCounts {
  total: number;
  /** 需要確認者(未確認、靜音中、已解除未確認;caution 除外) */
  unacknowledged: number;
  /** 目前有聲響者 */
  audible: number;
  /** 最高優先序(無警報 null) */
  highest: AlertPriority | null;
  /** 有聲響者中的最高優先序(決定聲響型式;無 null) */
  highestAudible: AlertPriority | null;
}

export function alertCounts(list: readonly AlertItem[] | undefined): AlertCounts {
  const out: AlertCounts = { total: 0, unacknowledged: 0, audible: 0, highest: null, highestAudible: null };
  for (const a of list ?? []) {
    out.total++;
    if (alertVisual(a).needsAck) out.unacknowledged++;
    if (out.highest === null || PRIORITY_RANK[a.priority] < PRIORITY_RANK[out.highest]) out.highest = a.priority;
    if (a.audible) {
      out.audible++;
      if (out.highestAudible === null || PRIORITY_RANK[a.priority] < PRIORITY_RANK[out.highestAudible]) out.highestAudible = a.priority;
    }
  }
  return out;
}

/** 靜音剩餘秒數(非靜音或無 silencedUntil_s 時 undefined)。 */
export function silenceRemaining(a: AlertItem, t: number): number | undefined {
  if (a.state !== 'activeSilenced' || a.silencedUntil_s === undefined) return undefined;
  return Math.max(0, a.silencedUntil_s - t);
}

/** 秒 → "m:ss"(倒數用;負值視為 0)。 */
export function formatCountdown(s: number): string {
  const total = Math.max(0, Math.ceil(s));
  const m = Math.floor(total / 60);
  const sec = total % 60;
  return `${m}:${sec.toString().padStart(2, '0')}`;
}

// ------------------------------------------------------------------ 指令

function alertIdArgs(type: 'alertAcknowledge' | 'alertSilence', id?: string): SimCommand {
  const trimmed = id?.trim();
  return trimmed && trimmed !== 'all' ? { type, args: { id: trimmed } } : { type };
}

/** alertAcknowledge:給 id 確認該警報;省略(或 "all")確認全部。 */
export function alertAcknowledgeCommand(id?: string): SimCommand {
  return alertIdArgs('alertAcknowledge', id);
}

/** alertSilence:靜音 30 s(期滿未確認會重響);省略 id = 全部。 */
export function alertSilenceCommand(id?: string): SimCommand {
  return alertIdArgs('alertSilence', id);
}

/** bnwasMode:value 為模式;dormant_s 夾在 180–720 並取整數。 */
export function bnwasModeCommand(mode: BnwasMode, dormant_s?: number): SimCommand {
  if (!BNWAS_MODES.includes(mode)) throw new RangeError(`無效的 BNWAS 模式:${String(mode)}`);
  const cmd: SimCommand = { type: 'bnwasMode', value: mode };
  if (typeof dormant_s === 'number' && Number.isFinite(dormant_s)) cmd.args = { dormant_s: Math.round(clamp(dormant_s, BNWAS_DORMANT_MIN_S, BNWAS_DORMANT_MAX_S)) };
  return cmd;
}

export function bnwasResetCommand(): SimCommand {
  return { type: 'bnwasReset' };
}

// ------------------------------------------------------------------ BNWAS 顯示

export type BnwasLevel = 'off' | 'standby' | 'normal' | 'caution' | 'alarm';

export interface BnwasDisplay {
  /** 主要文字(模式/階段) */
  label: string;
  /** 倒數文字(例如「第 1 階段 2:35」);無則空字串 */
  countdown: string;
  level: BnwasLevel;
  /** 第 2 階段以上閃爍 */
  flashing: boolean;
  /** 可重設(作動中) */
  resettable: boolean;
}

export function bnwasDisplay(b: BnwasStatus | undefined): BnwasDisplay {
  if (!b) return { label: '核心未回報', countdown: '', level: 'off', flashing: false, resettable: false };
  if (b.mode === 'off') return { label: 'BNWAS 關', countdown: '', level: 'off', flashing: false, resettable: false };
  if (!b.active) return { label: `待命(${BNWAS_MODE_LABEL[b.mode]};自動舵未啟用)`, countdown: '', level: 'standby', flashing: false, resettable: false };
  const next = b.stage < 3 ? (b.stage + 1) as BnwasStage : null;
  const countdown = next !== null && b.remaining_s !== undefined ? `第 ${next} 階段 ${formatCountdown(b.remaining_s)}` : '';
  const level: BnwasLevel = b.stage === 0 ? 'normal' : b.stage === 1 ? 'caution' : 'alarm';
  return { label: BNWAS_STAGE_LABEL[b.stage], countdown, level, flashing: b.stage >= 2, resettable: true };
}

// ------------------------------------------------------------------ 事件(教官站歷史、事件流文字)

export const ALERT_EVENT_KINDS = ['alertRaised', 'alertSilenced', 'alertReannunciated', 'alertAcknowledged', 'alertRectified', 'alertEscalated'] as const;
export const BNWAS_EVENT_KINDS = ['bnwasStage', 'bnwasReset', 'bnwasModeChanged'] as const;

export interface AlertHistoryEntry {
  id: string;
  priority: AlertPriority;
  category?: AlertCategory;
  source?: string;
  text: string;
  raisedAt_s: number;
  acknowledgedAt_s?: number;
  /** 確認 − 最近一次成立(核心 data.ackTime_s 優先) */
  ackTime_s?: number;
  rectifiedAt_s?: number;
  escalated: boolean;
  silences: number;
  /** rectifiedUnacknowledged 期間再成立 */
  repeat: boolean;
  /** 尚未回到 normal */
  open: boolean;
}

function dataOf(e: SimEvent): Record<string, unknown> {
  return e.data ?? {};
}

function alertIdOf(e: SimEvent): string {
  const d = dataOf(e);
  return typeof d['id'] === 'string' && d['id'] ? d['id'] : e.itemId ?? '';
}

/**
 * 由事件流推得警報歷史(每次 alertRaised 一筆,時間先後排列):核心 summary.alerts.records 的即時版。
 * repeat 的 alertRaised(rectifiedUnacknowledged 期間再成立)併入同一筆並標 repeat。
 */
export function alertHistory(events: readonly SimEvent[]): AlertHistoryEntry[] {
  const out: AlertHistoryEntry[] = [];
  const lastOf = (id: string): AlertHistoryEntry | undefined => {
    for (let i = out.length - 1; i >= 0; i--) if (out[i]!.id === id) return out[i];
    return undefined;
  };
  for (const e of events) {
    if (!(ALERT_EVENT_KINDS as readonly string[]).includes(e.kind)) continue;
    const id = alertIdOf(e);
    if (!id) continue;
    const d = dataOf(e);
    const st = typeof d['state'] === 'string' ? d['state'] : '';
    if (e.kind === 'alertRaised') {
      const prev = lastOf(id);
      if (d['repeat'] === true && prev && prev.open) {
        prev.repeat = true;
        prev.raisedAt_s = finite(d['raisedAt_s']) ?? e.t;
        delete prev.rectifiedAt_s;
        continue;
      }
      const entry: AlertHistoryEntry = {
        id,
        priority: oneOf(d['priority'], ALERT_PRIORITIES) ?? 'alarm',
        text: e.message || ALERT_TEXT[id] || id,
        raisedAt_s: finite(d['raisedAt_s']) ?? e.t,
        escalated: false,
        silences: 0,
        repeat: false,
        open: true,
      };
      const cat = oneOf(d['category'], ALERT_CATEGORIES);
      if (cat) entry.category = cat;
      if (typeof d['source'] === 'string') entry.source = d['source'];
      out.push(entry);
      continue;
    }
    let entry = lastOf(id);
    if (!entry) {
      // 歷史被截斷(只保留最近 N 筆事件)或中途連線:補一筆
      entry = { id, priority: oneOf(d['priority'], ALERT_PRIORITIES) ?? 'alarm', text: e.message || ALERT_TEXT[id] || id, raisedAt_s: finite(d['raisedAt_s']) ?? e.t, escalated: false, silences: 0, repeat: false, open: true };
      out.push(entry);
    }
    switch (e.kind) {
      case 'alertSilenced':
        entry.silences++;
        break;
      case 'alertAcknowledged':
        if (entry.acknowledgedAt_s === undefined) {
          entry.acknowledgedAt_s = e.t;
          entry.ackTime_s = finite(d['ackTime_s']) ?? Math.max(0, e.t - entry.raisedAt_s);
        }
        if (st === 'normal') entry.open = false;
        break;
      case 'alertRectified':
        entry.rectifiedAt_s = e.t;
        if (st === 'normal') entry.open = false;
        break;
      case 'alertEscalated': {
        entry.escalated = true;
        const to = oneOf(d['to'], ALERT_PRIORITIES);
        if (to) entry.priority = to;
        break;
      }
      default:
        break;
    }
  }
  return out;
}

export interface BnwasCounts {
  stage1: number;
  stage2: number;
  stage3: number;
  resets: number;
}

/** 由事件流計 BNWAS 各階段進入次數與重設次數(核心 summary.alerts.bnwas 的即時版)。 */
export function bnwasCounts(events: readonly SimEvent[]): BnwasCounts {
  const out: BnwasCounts = { stage1: 0, stage2: 0, stage3: 0, resets: 0 };
  for (const e of events) {
    if (e.kind === 'bnwasStage') {
      const st = finite(e.data?.['stage']);
      if (st === 1) out.stage1++;
      else if (st === 2) out.stage2++;
      else if (st === 3) out.stage3++;
    } else if (e.kind === 'bnwasReset') out.resets++;
  }
  return out;
}

const BNWAS_REASON_LABEL: Readonly<Record<string, string>> = { dormantElapsed: '休眠期滿', notReset: '未重設', inactive: '停止作動' };

/** 警報/BNWAS/自動舵脫開事件的一句話;其他事件回傳 null(由 sim-event.eventLabel 處理)。 */
export function alertEventLabel(e: SimEvent): string | null {
  const d = dataOf(e);
  const id = alertIdOf(e);
  const text = e.message || ALERT_TEXT[id] || id;
  const pr = oneOf(d['priority'], ALERT_PRIORITIES);
  const prText = pr ? ALERT_PRIORITY_LABEL[pr] : '';
  switch (e.kind) {
    case 'alertRaised':
      return `${prText || '警報'}成立:${text}${d['repeat'] === true ? '(再成立)' : ''}${finite(d['count']) !== undefined && (d['count'] as number) > 1 ? ` 第 ${d['count'] as number} 次` : ''}`;
    case 'alertSilenced':
      return `警報靜音:${text}${finite(d['silencedUntil_s']) !== undefined ? `(至 ${(d['silencedUntil_s'] as number).toFixed(0)} s)` : ''}`;
    case 'alertReannunciated':
      return `警報重響:${text}`;
    case 'alertAcknowledged':
      return `警報確認:${text}${finite(d['ackTime_s']) !== undefined ? ` +${(d['ackTime_s'] as number).toFixed(1)} s` : ''}`;
    case 'alertRectified':
      return `警報解除:${text}${d['state'] === 'rectifiedUnacknowledged' ? '(未確認)' : ''}`;
    case 'alertEscalated': {
      const from = oneOf(d['from'], ALERT_PRIORITIES);
      const to = oneOf(d['to'], ALERT_PRIORITIES);
      const reason = d['reason'] === 'unacknowledged' ? `未確認 ${finite(d['after_s'])?.toFixed(0) ?? '?'} s` : d['reason'] === 'source' ? '來源升級' : '';
      return `警報升級:${text} ${from ? ALERT_PRIORITY_LABEL[from] : '?'} → ${to ? ALERT_PRIORITY_LABEL[to] : '?'}${reason ? `(${reason})` : ''}`;
    }
    case 'bnwasStage': {
      const st = finite(d['stage']);
      const reason = typeof d['reason'] === 'string' ? BNWAS_REASON_LABEL[d['reason']] ?? d['reason'] : '';
      return `BNWAS ${st !== undefined && st >= 0 && st <= 3 ? BNWAS_STAGE_LABEL[st as BnwasStage] : `階段 ${String(d['stage'])}`}${reason ? `(${reason})` : ''}`;
    }
    case 'bnwasReset':
      return `BNWAS 重設${typeof d['by'] === 'string' ? `(${d['by']})` : ''}${finite(d['fromStage']) ? ` 自第 ${d['fromStage'] as number} 階段` : ''}`;
    case 'bnwasModeChanged': {
      const from = oneOf(d['from'], BNWAS_MODES);
      const to = oneOf(d['to'], BNWAS_MODES);
      return `BNWAS 模式 ${from ? BNWAS_MODE_LABEL[from] : '?'} → ${to ? BNWAS_MODE_LABEL[to] : '?'}${finite(d['dormant_s']) !== undefined ? `(休眠 ${d['dormant_s'] as number} s)` : ''}`;
    }
    case 'autopilotTripped':
      return `自動舵脫開(${d['reason'] === 'headingInvalid' ? '電羅經無效' : d['reason'] === 'autopilotFailure' ? '自動舵失效' : String(d['reason'] ?? '?')})`;
    default:
      return null;
  }
}

/** 警報類事件的嚴重度:alarm/緊急的成立與升級、BNWAS 第 2 階段以上為 alarm;warning 成立、BNWAS 第 1 階段、自動舵脫開為 warning。其他回傳 null。 */
export function alertEventSeverity(e: SimEvent): 'info' | 'warning' | 'alarm' | null {
  const d = dataOf(e);
  switch (e.kind) {
    case 'alertRaised':
    case 'alertReannunciated': {
      const pr = oneOf(d['priority'], ALERT_PRIORITIES);
      return pr === 'alarm' || pr === 'emergencyAlarm' ? 'alarm' : pr === 'warning' ? 'warning' : 'info';
    }
    case 'alertEscalated':
      return 'alarm';
    case 'bnwasStage': {
      const st = finite(d['stage']) ?? 0;
      return st >= 2 ? 'alarm' : st === 1 ? 'warning' : 'info';
    }
    case 'autopilotTripped':
      return 'warning';
    case 'alertSilenced':
    case 'alertAcknowledged':
    case 'alertRectified':
    case 'bnwasReset':
    case 'bnwasModeChanged':
      return 'info';
    default:
      return null;
  }
}

// ------------------------------------------------------------------ 真值 vs 指示值(教官站)

/** 各感測器相關的故障名稱(fault-catalogue.json)。 */
export const SENSOR_FAULTS: Readonly<Record<'heading' | 'position' | 'depth' | 'stw', readonly string[]>> = {
  heading: ['gyroDrift', 'gyroFreeze', 'gyroFailure'],
  position: ['gpsJump', 'gpsOffset', 'gpsFailure'],
  depth: ['echoSounderFailure'],
  stw: ['logFailure'],
};

export interface SensorComparisonRow {
  key: 'heading' | 'magnetic' | 'position' | 'cog' | 'sog' | 'stw' | 'depth';
  label: string;
  /** 指示值文字 */
  indicated: string;
  /** 真值文字 */
  truth: string;
  /** 差距文字(指示 − 真值) */
  error: string;
  /** 故障中或無效 → 教官站標紅 */
  faulty: boolean;
  /** 感測器旗標為無效 */
  invalid: boolean;
}

/** 差距超過此值視為「明顯」(未注入故障時也標紅;例如核心另有偏差) */
export const SENSOR_ERROR_LIMITS = { heading_deg: 1, position_m: 30, speed_kn: 0.3, depth_m: 0.3 } as const;

const fmt = (v: number, digits: number): string => (v === 0 ? 0 : v).toFixed(digits);
const signed = (v: number, digits: number): string => `${v > 0 ? '+' : ''}${fmt(Math.round(v * 10 ** digits) / 10 ** digits, digits)}`;

/**
 * 真值與指示值的比較列(state.truth 存在時;否則回傳空陣列)。
 * faulty:該感測器有相關故障、旗標無效,或差距超過 SENSOR_ERROR_LIMITS。
 */
export function sensorComparison(s: OwnShipState): SensorComparisonRow[] {
  const tr = s.truth;
  if (!tr) return [];
  const faults = new Set(s.faults ?? []);
  const has = (key: keyof typeof SENSOR_FAULTS) => SENSOR_FAULTS[key].some((f) => faults.has(f));
  const sen = s.sensors;
  const hdgErr = tr.headingError_deg !== 0 ? tr.headingError_deg : headingDifference(tr.heading, s.heading);
  const rows: SensorComparisonRow[] = [];
  const headingInvalid = sen ? !sen.headingValid : false;
  rows.push({
    key: 'heading',
    label: '航向(電羅經)',
    indicated: `${fmt(s.heading, 1)}°`,
    truth: `${fmt(tr.heading, 1)}°`,
    error: `${signed(hdgErr, 1)}°`,
    faulty: has('heading') || headingInvalid || Math.abs(hdgErr) > SENSOR_ERROR_LIMITS.heading_deg,
    invalid: headingInvalid,
  });
  if (sen) {
    const magErr = headingDifference(tr.heading, sen.magneticHeading);
    rows.push({ key: 'magnetic', label: '磁羅經', indicated: `${fmt(sen.magneticHeading, 1)}°`, truth: `${fmt(tr.heading, 1)}°`, error: `${signed(magErr, 1)}°`, faulty: false, invalid: false });
  }
  const posInvalid = sen ? !sen.positionValid : false;
  const posErr = tr.positionError_m !== 0 ? tr.positionError_m : Math.hypot(s.pos.x - tr.pos.x, s.pos.y - tr.pos.y);
  rows.push({
    key: 'position',
    label: '位置(GNSS)',
    indicated: `${fmt(s.pos.x, 0)}, ${fmt(s.pos.y, 0)} m`,
    truth: `${fmt(tr.pos.x, 0)}, ${fmt(tr.pos.y, 0)} m`,
    error: `${fmt(posErr, 0)} m`,
    faulty: has('position') || posInvalid || posErr > SENSOR_ERROR_LIMITS.position_m,
    invalid: posInvalid,
  });
  const cogErr = headingDifference(tr.cog, s.cog);
  rows.push({ key: 'cog', label: 'COG', indicated: `${fmt(s.cog, 1)}°`, truth: `${fmt(tr.cog, 1)}°`, error: `${signed(cogErr, 1)}°`, faulty: posInvalid || (has('position') && Math.abs(cogErr) > SENSOR_ERROR_LIMITS.heading_deg), invalid: posInvalid });
  const sogErr = s.sog - tr.sog;
  rows.push({ key: 'sog', label: 'SOG', indicated: `${fmt(s.sog, 1)} kn`, truth: `${fmt(tr.sog, 1)} kn`, error: `${signed(sogErr, 1)} kn`, faulty: posInvalid || Math.abs(sogErr) > SENSOR_ERROR_LIMITS.speed_kn, invalid: posInvalid });
  const stwInvalid = sen ? !sen.stwValid : false;
  const stwErr = s.stw - tr.stw;
  rows.push({ key: 'stw', label: 'STW(計程儀)', indicated: `${fmt(s.stw, 1)} kn`, truth: `${fmt(tr.stw, 1)} kn`, error: `${signed(stwErr, 1)} kn`, faulty: has('stw') || stwInvalid || Math.abs(stwErr) > SENSOR_ERROR_LIMITS.speed_kn, invalid: stwInvalid });
  const depthInvalid = sen ? !sen.depthValid : false;
  const depthErr = s.depthBelowKeel - tr.depthBelowKeel;
  rows.push({ key: 'depth', label: '龍骨下水深(測深儀)', indicated: `${fmt(s.depthBelowKeel, 1)} m`, truth: `${fmt(tr.depthBelowKeel, 1)} m`, error: `${signed(depthErr, 1)} m`, faulty: has('depth') || depthInvalid || Math.abs(depthErr) > SENSOR_ERROR_LIMITS.depth_m, invalid: depthInvalid });
  return rows;
}
