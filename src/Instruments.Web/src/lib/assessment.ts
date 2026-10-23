// 評估:情境 YAML 的 assessment 規則(設計文件第 1.3 節)與核心 summary(第 4.8 節 summary 紀錄行、
// WebSocket {"type":"assessment","final","summary"} 查詢回覆)的解析。教官站只顯示、不自己評分;
// 所有欄位對缺值寬容(舊核心/Python 伺服器沒有 summary 時回傳 null,由 debrief.ts 的本地推導退回)。

import { numberOrParam, parseUkcLimit, type NumberOrParam, type UkcLimitSpec } from './timeline.js';

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function num(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v);
}

// ------------------------------------------------------------------ 情境 assessment 規則(教官站顯示用)

export type HardFailRuleKind = 'grounding' | 'collision' | 'cpa' | 'ukc' | 'zoneEntered' | 'event';

export interface HardFailRule {
  id: string;
  rule: HardFailRuleKind | (string & {});
  lessThan?: NumberOrParam | UkcLimitSpec;
  zoneId?: string;
  eventKind?: string;
  excludeEscorts: boolean;
}

export interface BandRule {
  id: string;
  metric: string;
  direction: 'above' | 'below';
  warn?: NumberOrParam;
  /** 數值、參數引用或 "2x"(warn 的倍數) */
  fail?: NumberOrParam | { multiplier: number };
  penalty: number;
  failPenalty: number;
  excludeEscorts: boolean;
}

export interface RequiredAfter {
  event: string;
  itemId?: string;
  targetId?: string;
  waypoint?: string;
}

/** expect 的可偵測項:字串(rudderOrder/telegraphChange/…)、{acknowledge:{kind}}、{event:{kind,…}}。 */
export type ExpectItem = { type: 'action'; name: string } | { type: 'acknowledge'; kind: string } | { type: 'event'; kind: string; itemId?: string; targetId?: string };

export interface RequiredEventRule {
  id: string;
  after: RequiredAfter | null;
  expect: ExpectItem[];
  within_s?: NumberOrParam;
  penalty: number;
  latePenaltyPer_s?: number;
}

export interface AssessmentRules {
  parameterSet?: string;
  minCpa_nm?: NumberOrParam;
  encounter?: { detectionRange_nm?: NumberOrParam; substantialCourseChange_deg?: NumberOrParam; substantialSpeedChange_kn?: NumberOrParam };
  ukc?: { min?: UkcLimitSpec };
  manoeuvring?: { rotLimit_degPerMin?: NumberOrParam; hardOver_deg?: NumberOrParam };
  hardFail: HardFailRule[];
  bands: BandRule[];
  required: RequiredEventRule[];
  scoring: { start: NumberOrParam; passMark?: NumberOrParam };
  /** 規則總數(硬性失敗 + 容許帶 + 必要事件) */
  ruleCount: number;
}

function parseExpectItem(v: unknown, issues: string[], path: string): ExpectItem | null {
  if (typeof v === 'string') return { type: 'action', name: v };
  if (!isRecord(v)) {
    issues.push(`${path} 無效`);
    return null;
  }
  if (isRecord(v['acknowledge'])) return { type: 'acknowledge', kind: str(v['acknowledge']['kind']) || 'other' };
  if (isRecord(v['event'])) {
    const e = v['event'];
    const item: ExpectItem = { type: 'event', kind: str(e['kind']) };
    if (e['itemId'] !== undefined) item.itemId = str(e['itemId']);
    if (e['targetId'] !== undefined) item.targetId = str(e['targetId']);
    return item;
  }
  issues.push(`${path} 需為可偵測項名稱、acknowledge 或 event`);
  return null;
}

function parseExpect(v: unknown, issues: string[], path: string): ExpectItem[] {
  if (isRecord(v) && Array.isArray(v['any'])) return v['any'].map((x, i) => parseExpectItem(x, issues, `${path}.any[${i}]`)).filter((x): x is ExpectItem => x !== null);
  const one = parseExpectItem(v, issues, path);
  return one ? [one] : [];
}

/** 解析情境的 assessment 物件(缺則回傳空規則);既有 minCpa_nm 欄位保留。 */
export function parseAssessmentRules(raw: unknown, issues: string[] = []): AssessmentRules {
  const rules: AssessmentRules = { hardFail: [], bands: [], required: [], scoring: { start: { value: 100 } }, ruleCount: 0 };
  if (raw === undefined || raw === null) return rules;
  if (!isRecord(raw)) {
    issues.push('assessment 不是物件');
    return rules;
  }
  if (typeof raw['parameterSet'] === 'string' && raw['parameterSet'].trim()) rules.parameterSet = raw['parameterSet'].trim();
  const minCpa = numberOrParam(raw['minCpa_nm']);
  if (minCpa) rules.minCpa_nm = minCpa;
  if (isRecord(raw['encounter'])) {
    const e = raw['encounter'];
    rules.encounter = {};
    const dr = numberOrParam(e['detectionRange_nm']);
    if (dr) rules.encounter.detectionRange_nm = dr;
    const cc = numberOrParam(e['substantialCourseChange_deg']);
    if (cc) rules.encounter.substantialCourseChange_deg = cc;
    const sc = numberOrParam(e['substantialSpeedChange_kn']);
    if (sc) rules.encounter.substantialSpeedChange_kn = sc;
  }
  if (isRecord(raw['ukc'])) {
    const min = parseUkcLimit(raw['ukc']['min']);
    rules.ukc = min ? { min } : {};
  }
  if (isRecord(raw['manoeuvring'])) {
    const m = raw['manoeuvring'];
    rules.manoeuvring = {};
    const rot = numberOrParam(m['rotLimit_degPerMin']);
    if (rot) rules.manoeuvring.rotLimit_degPerMin = rot;
    const ho = numberOrParam(m['hardOver_deg']);
    if (ho) rules.manoeuvring.hardOver_deg = ho;
  }
  if (Array.isArray(raw['hardFail'])) {
    raw['hardFail'].forEach((h, i) => {
      if (!isRecord(h)) return issues.push(`assessment.hardFail[${i}] 不是物件`);
      const rule: HardFailRule = { id: str(h['id']).trim() || `hardFail${i}`, rule: str(h['rule']).trim() || 'event', excludeEscorts: h['excludeEscorts'] === true };
      const lt = h['lessThan'];
      const ltNum = numberOrParam(lt);
      const ltUkc = parseUkcLimit(lt);
      if (ltNum) rule.lessThan = ltNum;
      else if (ltUkc) rule.lessThan = ltUkc;
      if (h['zoneId'] !== undefined) rule.zoneId = str(h['zoneId']);
      if (h['kind'] !== undefined) rule.eventKind = str(h['kind']);
      else if (isRecord(h['event'])) rule.eventKind = str(h['event']['kind']);
      rules.hardFail.push(rule);
    });
  }
  if (Array.isArray(raw['bands'])) {
    raw['bands'].forEach((b, i) => {
      if (!isRecord(b)) return issues.push(`assessment.bands[${i}] 不是物件`);
      const metric = str(b['metric']).trim();
      if (!metric) issues.push(`assessment.bands[${i}] 缺 metric`);
      const rule: BandRule = {
        id: str(b['id']).trim() || metric || `band${i}`,
        metric,
        direction: b['direction'] === 'below' ? 'below' : 'above',
        penalty: num(b['penalty']) ?? 0,
        failPenalty: num(b['failPenalty']) ?? num(b['penalty']) ?? 0,
        excludeEscorts: b['excludeEscorts'] === true,
      };
      const warn = numberOrParam(b['warn']);
      if (warn) rule.warn = warn;
      const fail = b['fail'];
      const mult = typeof fail === 'string' && /^\d+(\.\d+)?x$/i.test(fail.trim()) ? Number(fail.trim().slice(0, -1)) : undefined;
      if (mult !== undefined) rule.fail = { multiplier: mult };
      else {
        const f = numberOrParam(fail);
        if (f) rule.fail = f;
      }
      rules.bands.push(rule);
    });
  }
  if (Array.isArray(raw['required'])) {
    raw['required'].forEach((r, i) => {
      if (!isRecord(r)) return issues.push(`assessment.required[${i}] 不是物件`);
      const path = `assessment.required[${i}]`;
      let after: RequiredAfter | null = null;
      if (isRecord(r['after'])) {
        const a = r['after'];
        after = { event: str(a['event']).trim() };
        if (a['itemId'] !== undefined) after.itemId = str(a['itemId']);
        if (a['targetId'] !== undefined) after.targetId = str(a['targetId']);
        if (a['waypoint'] !== undefined) after.waypoint = str(a['waypoint']);
        if (!after.event) issues.push(`${path}.after 缺 event`);
      } else issues.push(`${path} 缺 after`);
      const rule: RequiredEventRule = { id: str(r['id']).trim() || `required${i}`, after, expect: parseExpect(r['expect'], issues, `${path}.expect`), penalty: num(r['penalty']) ?? 0 };
      const within = numberOrParam(r['within_s']);
      if (within) rule.within_s = within;
      const late = num(r['latePenaltyPer_s']);
      if (late !== undefined) rule.latePenaltyPer_s = late;
      rules.required.push(rule);
    });
  }
  if (isRecord(raw['scoring'])) {
    const s = raw['scoring'];
    const start = numberOrParam(s['start']);
    if (start) rules.scoring.start = start;
    const pass = numberOrParam(s['passMark']);
    if (pass) rules.scoring.passMark = pass;
  }
  rules.ruleCount = rules.hardFail.length + rules.bands.length + rules.required.length;
  return rules;
}

/** 必要事件規則是否由該故障注入事件起算(after.event = faultInjected 且 itemId 相符或未指定)。 */
export function requiredRulesAfterFault(rules: AssessmentRules, itemId: string | undefined): RequiredEventRule[] {
  return rules.required.filter((r) => r.after?.event === 'faultInjected' && (r.after.itemId === undefined || r.after.itemId === itemId));
}

export const HARD_FAIL_RULE_LABEL: Readonly<Record<string, string>> = {
  grounding: '擱淺',
  collision: '碰撞',
  cpa: 'CPA 低於門檻',
  ukc: 'UKC 低於門檻',
  zoneEntered: '進入禁區',
  event: '事件發生',
};

export function isUkcLimitSpec(v: NumberOrParam | UkcLimitSpec | undefined): v is UkcLimitSpec {
  return !!v && ('metres' in v || 'fractionOfDraft' in v);
}

// ------------------------------------------------------------------ 核心 summary

export interface ParameterSetRef {
  id?: string;
  version?: string;
  path?: string;
  sha256?: string;
}

export interface SummaryPhase {
  phase: string;
  from_s: number;
  to_s: number;
}

export interface SummaryGeneral {
  duration_s?: number;
  /** 凍結次數(核心輸出;設計文件 D4:凍結期間模擬時間不前進,不輸出秒數) */
  freezes?: number;
  /** 舊版/示範摘要的凍結秒數(核心不輸出) */
  frozen_s?: number;
  distance_nm?: number;
  avgSog_kn?: number;
  maxSog_kn?: number;
}

export interface SummaryEncounter {
  targetId: string;
  name?: string;
  situation?: string;
  detectedAt_s?: number;
  firstActionAt_s?: number;
  timeToAction_s?: number;
  firstAction?: string;
  minCpa_nm?: number;
  minCpaAt_s?: number;
  tcpaAtDetection_min?: number;
  minRange_nm?: number;
  bcrAtDetection_nm?: number;
  bcrMin_nm?: number;
  courseChange_deg?: number;
  speedChange_kn?: number;
  substantial?: boolean;
  cpaAlarmTime_s?: number;
  closedAt_s?: number;
  /** 跟隨自船的 escort 目標(核心不建立會遇紀錄,設計文件 D5;保留欄位以相容) */
  escort?: boolean;
}

export interface SummaryCollisionAvoidance {
  minCpa_nm?: number;
  encounters?: number;
  collisions?: number;
  targets: SummaryEncounter[];
}

export interface SummaryTrack {
  meanAbsXtd_m?: number;
  maxXtd_m?: number;
  maxXtdAt_s?: number;
  maxXtdLeg?: number;
  timeOutsideCorridor_s?: number;
  corridorExcursions?: number;
  fractionOutsideCorridor?: number;
  minDistanceToCorridorEdge_m?: number;
  minDistanceToNoGo_m?: number;
  meanHeadingDeviation_deg?: number;
  /** 目前航段(核心輸出) */
  legIndex?: number;
  /** 航線是否走完(核心輸出) */
  complete?: boolean;
  offRoute?: boolean;
  waypoints: Array<{ name: string; passedAt_s?: number; xtd_m?: number; distance_m?: number }>;
}

export interface SummaryUkc {
  min_m?: number;
  minAt_s?: number;
  minSquat_m?: number;
  timeBelowLimit_s?: number;
  excursions?: number;
  groundings?: number;
  limit_m?: number;
  limitSource?: string;
}

export interface SummaryManoeuvring {
  rudderDistribution_s?: Record<string, number>;
  hardOverCount?: number;
  hardOverTime_s?: number;
  rudderOrders?: number;
  maxRot_degPerMin?: number;
  maxRotAt_s?: number;
  timeRotOverLimit_s?: number;
  rotExcursions?: number;
  telegraphChanges?: number;
  telegraphOrders: Array<{ order: string; from_s?: number; to_s?: number; duration_s?: number }>;
  rpmOrders?: number;
  thrusterOrders?: number;
  thrusterTime_s?: number;
  timeOverSpeedLimit_s?: number;
  maxOverSpeed_kn?: number;
  speedLimitSource?: string;
  autopilotTime_s?: number;
  autopilotSwitches?: number;
}

export interface SummaryFaultResponse {
  fault: string;
  itemId?: string;
  injectedAt_s?: number;
  source?: string;
  firstAction?: { kind: string; t_s?: number; dt_s?: number };
  acknowledgements: Array<{ kind: string; t_s?: number; dt_s?: number; role?: string }>;
  cleared_s?: number;
  withinFirstAction?: boolean;
  withinNotifyEcr?: boolean;
  /** 該故障對應警報的第一次確認時刻與距注入秒數(sensors-alerts.md 第 5 節) */
  alertAckAt_s?: number;
  alertAckDt_s?: number;
}

/** summary.alerts.records[]:每次警報成立一筆。 */
export interface SummaryAlertRecord {
  id: string;
  priority?: string;
  raisedAt_s?: number;
  acknowledgedAt_s?: number;
  ackTime_s?: number;
  rectifiedAt_s?: number;
  escalated: boolean;
  silences: number;
}

/** summary.alerts(規劃書第 8.3 節第 5、7 類;C# 核心才有)。 */
export interface SummaryAlerts {
  raised?: number;
  acknowledged?: number;
  unacknowledged?: number;
  escalations?: number;
  silences?: number;
  meanAckTime_s?: number;
  maxAckTime_s?: number;
  records: SummaryAlertRecord[];
  bnwas: { stage1?: number; stage2?: number; stage3?: number; resets?: number };
}

/** 雷達操作指標(summary.radar;規劃書第 8.3 節第 7 類;C# SummaryRadar):有雷達操作時核心才輸出。 */
export interface SummaryRadar {
  controlCount: number;
  acquisitions: number;
  rangeChanges: number;
  /** 依雷達 id(x、s)分列 */
  byRadar: Record<string, { controlCount: number; acquisitions: number; rangeChanges: number }>;
}

export interface SummaryHardFail {
  id: string;
  rule?: string;
  /** 發生時間(秒;核心欄位 t_s,與 faultResponses 的 t_s 同名) */
  t_s?: number;
  value?: number;
  limit?: number;
  paramKey?: string;
}

export type BandState = 'ok' | 'warn' | 'fail' | 'n/a';

export interface SummaryBand {
  id: string;
  metric?: string;
  value?: number;
  warn?: number;
  fail?: number;
  state: BandState;
  penalty?: number;
  paramKey?: string;
  source?: string;
  revision?: string;
  clause?: string;
}

export type RequiredState = 'met' | 'missed' | 'late' | 'pending' | 'notApplicable';

export interface SummaryRequired {
  id: string;
  state: RequiredState;
  dt_s?: number;
  within_s?: number;
  by?: string;
  penalty?: number;
  paramKey?: string;
  source?: string;
  revision?: string;
  clause?: string;
}

export interface SummaryParameter {
  key: string;
  value?: number | string | boolean;
  unit?: string;
  source?: string;
  revision?: string;
  clause?: string;
}

export interface SummaryAssessment {
  score?: number;
  passMark?: number;
  passed?: boolean;
  hardFailed?: boolean;
  hardFails: SummaryHardFail[];
  bands: SummaryBand[];
  required: SummaryRequired[];
  parameters: SummaryParameter[];
}

/** 核心自動量測與評分摘要(設計文件第 4.8 節 JSON)。 */
export interface AssessmentSummary {
  version: number;
  advisory: boolean;
  final: boolean;
  scenarioId?: string;
  shipId?: string;
  loading?: string;
  parameterSet?: ParameterSetRef;
  phases: SummaryPhase[];
  general: SummaryGeneral;
  collisionAvoidance: SummaryCollisionAvoidance;
  track: SummaryTrack | null;
  ukc: SummaryUkc;
  manoeuvring: SummaryManoeuvring;
  faultResponses: SummaryFaultResponse[];
  events: { total?: number; byKind: Record<string, number> };
  assessment: SummaryAssessment;
  /** 警報確認與 BNWAS 階段次數(舊核心/示範沒有時 null) */
  alerts: SummaryAlerts | null;
  /** 雷達操作指標(無雷達操作或舊核心時 null) */
  radar: SummaryRadar | null;
  /** 原始物件(供未列欄位的顯示與匯出) */
  raw: Record<string, unknown>;
}

function pickNums<K extends string>(src: Record<string, unknown>, keys: readonly K[]): { [P in K]?: number } {
  const out: { [P in K]?: number } = {};
  for (const k of keys) {
    const v = num(src[k]);
    if (v !== undefined) out[k] = v;
  }
  return out;
}

function parseParameterSetRef(v: unknown): ParameterSetRef | undefined {
  if (!isRecord(v)) return undefined;
  const out: ParameterSetRef = {};
  if (typeof v['id'] === 'string') out.id = v['id'];
  if (typeof v['version'] === 'string') out.version = v['version'];
  if (typeof v['path'] === 'string') out.path = v['path'];
  if (typeof v['sha256'] === 'string') out.sha256 = v['sha256'];
  return out;
}

/** 解析 summary 物件(紀錄 summary 行的 summary、assessment 框的 summary);不是物件回傳 null。 */
export function parseAssessmentSummary(raw: unknown): AssessmentSummary | null {
  if (!isRecord(raw)) return null;
  const gen = isRecord(raw['general']) ? raw['general'] : {};
  const ca = isRecord(raw['collisionAvoidance']) ? raw['collisionAvoidance'] : {};
  const tr = isRecord(raw['track']) ? raw['track'] : null;
  const ukc = isRecord(raw['ukc']) ? raw['ukc'] : {};
  const man = isRecord(raw['manoeuvring']) ? raw['manoeuvring'] : {};
  const asm = isRecord(raw['assessment']) ? raw['assessment'] : {};
  const ev = isRecord(raw['events']) ? raw['events'] : {};

  const targets: SummaryEncounter[] = (Array.isArray(ca['targets']) ? ca['targets'] : [])
    .filter(isRecord)
    .map((t) => {
      const enc: SummaryEncounter = {
        targetId: str(t['targetId']),
        ...pickNums(t, ['detectedAt_s', 'firstActionAt_s', 'timeToAction_s', 'minCpa_nm', 'minCpaAt_s', 'tcpaAtDetection_min', 'minRange_nm', 'bcrAtDetection_nm', 'bcrMin_nm', 'courseChange_deg', 'speedChange_kn', 'cpaAlarmTime_s', 'closedAt_s']),
      };
      if (typeof t['escort'] === 'boolean') enc.escort = t['escort'];
      if (typeof t['name'] === 'string') enc.name = t['name'];
      if (typeof t['situation'] === 'string') enc.situation = t['situation'];
      if (typeof t['firstAction'] === 'string') enc.firstAction = t['firstAction'];
      if (typeof t['substantial'] === 'boolean') enc.substantial = t['substantial'];
      return enc;
    });

  let track: SummaryTrack | null = null;
  if (tr) {
    track = {
      ...pickNums(tr, ['meanAbsXtd_m', 'maxXtd_m', 'maxXtdAt_s', 'maxXtdLeg', 'timeOutsideCorridor_s', 'corridorExcursions', 'fractionOutsideCorridor', 'minDistanceToCorridorEdge_m', 'minDistanceToNoGo_m', 'meanHeadingDeviation_deg', 'legIndex']),
      waypoints: (Array.isArray(tr['waypoints']) ? tr['waypoints'] : []).filter(isRecord).map((w) => ({ name: str(w['name']), ...pickNums(w, ['passedAt_s', 'xtd_m', 'distance_m']) })),
    };
    if (typeof tr['offRoute'] === 'boolean') (track as SummaryTrack).offRoute = tr['offRoute'];
    if (typeof tr['complete'] === 'boolean') (track as SummaryTrack).complete = tr['complete'];
  }

  const manoeuvring: SummaryManoeuvring = {
    ...pickNums(man, ['hardOverCount', 'hardOverTime_s', 'rudderOrders', 'maxRot_degPerMin', 'maxRotAt_s', 'timeRotOverLimit_s', 'rotExcursions', 'telegraphChanges', 'rpmOrders', 'thrusterOrders', 'thrusterTime_s', 'timeOverSpeedLimit_s', 'maxOverSpeed_kn', 'autopilotTime_s', 'autopilotSwitches']),
    telegraphOrders: (Array.isArray(man['telegraphOrders']) ? man['telegraphOrders'] : []).filter(isRecord).map((o) => ({ order: str(o['order']), ...pickNums(o, ['from_s', 'to_s', 'duration_s']) })),
  };
  if (isRecord(man['rudderDistribution_s'])) {
    const dist: Record<string, number> = {};
    for (const [k, v] of Object.entries(man['rudderDistribution_s'])) {
      const n = num(v);
      if (n !== undefined) dist[k] = n;
    }
    manoeuvring.rudderDistribution_s = dist;
  }
  if (typeof man['speedLimitSource'] === 'string') manoeuvring.speedLimitSource = man['speedLimitSource'];

  const faultResponses: SummaryFaultResponse[] = (Array.isArray(raw['faultResponses']) ? raw['faultResponses'] : []).filter(isRecord).map((f) => {
    const fr: SummaryFaultResponse = {
      fault: str(f['fault']),
      ...pickNums(f, ['injectedAt_s', 'cleared_s', 'alertAckAt_s', 'alertAckDt_s']),
      acknowledgements: (Array.isArray(f['acknowledgements']) ? f['acknowledgements'] : []).filter(isRecord).map((a) => {
        const ack: SummaryFaultResponse['acknowledgements'][number] = { kind: str(a['kind']), ...pickNums(a, ['t_s', 'dt_s']) };
        if (typeof a['role'] === 'string') ack.role = a['role'];
        return ack;
      }),
    };
    if (typeof f['itemId'] === 'string') fr.itemId = f['itemId'];
    if (typeof f['source'] === 'string') fr.source = f['source'];
    if (isRecord(f['firstAction'])) fr.firstAction = { kind: str(f['firstAction']['kind']), ...pickNums(f['firstAction'], ['t_s', 'dt_s']) };
    if (typeof f['withinFirstAction'] === 'boolean') fr.withinFirstAction = f['withinFirstAction'];
    if (typeof f['withinNotifyEcr'] === 'boolean') fr.withinNotifyEcr = f['withinNotifyEcr'];
    return fr;
  });

  const byKind: Record<string, number> = {};
  if (isRecord(ev['byKind'])) for (const [k, v] of Object.entries(ev['byKind'])) if (num(v) !== undefined) byKind[k] = v as number;

  const assessment: SummaryAssessment = {
    ...pickNums(asm, ['score', 'passMark']),
    hardFails: (Array.isArray(asm['hardFails']) ? asm['hardFails'] : []).filter(isRecord).map((h) => {
      const hf: SummaryHardFail = { id: str(h['id']), ...pickNums(h, ['t_s', 'value', 'limit']) };
      // 舊示範/夾具用 at_s
      if (hf.t_s === undefined) {
        const at = num(h['at_s']);
        if (at !== undefined) hf.t_s = at;
      }
      if (typeof h['rule'] === 'string') hf.rule = h['rule'];
      if (typeof h['paramKey'] === 'string') hf.paramKey = h['paramKey'];
      return hf;
    }),
    bands: (Array.isArray(asm['bands']) ? asm['bands'] : []).filter(isRecord).map((b) => {
      const st = b['state'];
      const band: SummaryBand = { id: str(b['id']), state: st === 'warn' || st === 'fail' || st === 'n/a' ? st : 'ok', ...pickNums(b, ['value', 'warn', 'fail', 'penalty']) };
      for (const k of ['metric', 'paramKey', 'source', 'revision', 'clause'] as const) if (typeof b[k] === 'string') band[k] = b[k] as string;
      return band;
    }),
    required: (Array.isArray(asm['required']) ? asm['required'] : []).filter(isRecord).map((r) => {
      const st = str(r['state']);
      const req: SummaryRequired = { id: str(r['id']), state: st === 'met' || st === 'missed' || st === 'late' || st === 'notApplicable' ? st : 'pending', ...pickNums(r, ['dt_s', 'within_s', 'penalty']) };
      for (const k of ['by', 'paramKey', 'source', 'revision', 'clause'] as const) if (typeof r[k] === 'string') req[k] = r[k] as string;
      return req;
    }),
    parameters: (Array.isArray(asm['parameters']) ? asm['parameters'] : []).filter(isRecord).map((p) => {
      const param: SummaryParameter = { key: str(p['key']) };
      const v = p['value'];
      if (typeof v === 'number' || typeof v === 'string' || typeof v === 'boolean') param.value = v;
      for (const k of ['unit', 'source', 'revision', 'clause'] as const) if (typeof p[k] === 'string') param[k] = p[k] as string;
      return param;
    }),
  };
  if (typeof asm['passed'] === 'boolean') assessment.passed = asm['passed'];
  if (typeof asm['hardFailed'] === 'boolean') assessment.hardFailed = asm['hardFailed'];

  const summary: AssessmentSummary = {
    version: num(raw['version']) ?? 1,
    advisory: raw['advisory'] !== false,
    final: raw['final'] === true,
    phases: (Array.isArray(raw['phases']) ? raw['phases'] : []).filter(isRecord).map((p) => ({ phase: str(p['phase']) || 'normal', from_s: num(p['from_s']) ?? 0, to_s: num(p['to_s']) ?? 0 })),
    general: pickNums(gen, ['duration_s', 'freezes', 'frozen_s', 'distance_nm', 'avgSog_kn', 'maxSog_kn']),
    collisionAvoidance: { ...pickNums(ca, ['minCpa_nm', 'encounters', 'collisions']), targets },
    track,
    ukc: { ...pickNums(ukc, ['min_m', 'minAt_s', 'minSquat_m', 'timeBelowLimit_s', 'excursions', 'groundings', 'limit_m']) },
    manoeuvring,
    faultResponses,
    events: { byKind },
    assessment,
    alerts: parseSummaryAlerts(raw['alerts']),
    radar: parseSummaryRadar(raw['radar']),
    raw,
  };
  if (typeof ukc['limitSource'] === 'string') summary.ukc.limitSource = ukc['limitSource'];
  const total = num(ev['total']);
  if (total !== undefined) summary.events.total = total;
  if (typeof raw['scenarioId'] === 'string') summary.scenarioId = raw['scenarioId'];
  if (typeof raw['shipId'] === 'string') summary.shipId = raw['shipId'];
  if (typeof raw['loading'] === 'string') summary.loading = raw['loading'];
  const ps = parseParameterSetRef(raw['parameterSet']);
  if (ps) summary.parameterSet = ps;
  return summary;
}

/** summary.alerts → SummaryAlerts;不是物件回傳 null。 */
export function parseSummaryAlerts(v: unknown): SummaryAlerts | null {
  if (!isRecord(v)) return null;
  const bn = isRecord(v['bnwas']) ? v['bnwas'] : {};
  return {
    ...pickNums(v, ['raised', 'acknowledged', 'unacknowledged', 'escalations', 'silences', 'meanAckTime_s', 'maxAckTime_s']),
    records: (Array.isArray(v['records']) ? v['records'] : []).filter(isRecord).map((r) => {
      const rec: SummaryAlertRecord = { id: str(r['id']), escalated: r['escalated'] === true, silences: num(r['silences']) ?? 0, ...pickNums(r, ['raisedAt_s', 'acknowledgedAt_s', 'ackTime_s', 'rectifiedAt_s']) };
      if (typeof r['priority'] === 'string') rec.priority = r['priority'];
      return rec;
    }),
    bnwas: pickNums(bn, ['stage1', 'stage2', 'stage3', 'resets']),
  };
}

/** summary.radar → SummaryRadar;不是物件回傳 null;byRadar 只收數值欄位。 */
export function parseSummaryRadar(v: unknown): SummaryRadar | null {
  if (!isRecord(v)) return null;
  const byRadar: SummaryRadar['byRadar'] = {};
  if (isRecord(v['byRadar']))
    for (const [k, c] of Object.entries(v['byRadar'])) {
      if (!isRecord(c)) continue;
      byRadar[k] = { controlCount: num(c['controlCount']) ?? 0, acquisitions: num(c['acquisitions']) ?? 0, rangeChanges: num(c['rangeChanges']) ?? 0 };
    }
  return { controlCount: num(v['controlCount']) ?? 0, acquisitions: num(v['acquisitions']) ?? 0, rangeChanges: num(v['rangeChanges']) ?? 0, byRadar };
}

/** WebSocket 回覆框 {"type":"assessment","final":bool,"summary":{…}} → summary(含 final 旗標);非評估框回傳 null。 */
export function assessmentFromFrame(frame: Record<string, unknown>): AssessmentSummary | null {
  if (frame['type'] !== 'assessment') return null;
  const s = parseAssessmentSummary(frame['summary']);
  if (!s) return null;
  if (typeof frame['final'] === 'boolean') s.final = frame['final'];
  return s;
}

/** 紀錄 header.parameterSet(設計文件第 5.3 節)。 */
export interface RecordParameterSet extends ParameterSetRef {
  resolved: Array<{ path: string; key: string; value?: number | string | boolean; unit?: string; source?: string; revision?: string; clause?: string }>;
}

export function parseRecordParameterSet(v: unknown): RecordParameterSet | null {
  if (!isRecord(v)) return null;
  const base = parseParameterSetRef(v) ?? {};
  const resolved: RecordParameterSet['resolved'] = (Array.isArray(v['resolved']) ? v['resolved'] : []).filter(isRecord).map((r) => {
    const item: RecordParameterSet['resolved'][number] = { path: str(r['path']), key: str(r['key']) };
    const val = r['value'];
    if (typeof val === 'number' || typeof val === 'string' || typeof val === 'boolean') item.value = val;
    for (const k of ['unit', 'source', 'revision', 'clause'] as const) if (typeof r[k] === 'string') item[k] = r[k] as string;
    return item;
  });
  return { ...base, resolved };
}

export const BAND_STATE_LABEL: Readonly<Record<BandState, string>> = { ok: '正常', warn: '警告', fail: '失敗', 'n/a': '不適用' };
export const REQUIRED_STATE_LABEL: Readonly<Record<RequiredState, string>> = { met: '達成', missed: '逾時', late: '逾時後達成', pending: '計時中', notApplicable: '不適用' };
