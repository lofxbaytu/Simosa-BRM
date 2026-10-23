// 講評報告(規劃書第 9.2 節「匯出:PDF/HTML 講評報告(頁首標示「中塑海運內部訓練」與課程版本)」、第 8.4 節講評結構、
// 第 8.3 節「講評報告顯示參數集版次與來源程序文件編號」):由核心 JSON Lines 紀錄或教官站紀錄(record-replay.parseReplay)
// 加上行為標記評估表組成。本檔只產生資料模型(純函式,供 vitest 檢查),畫面在 report-app.ts。

import type { SimEvent } from '../types/event.js';
import type { AssessmentSummary } from '../lib/assessment.js';
import type { BrmMarker } from '../lib/brm-markers.js';
import { summarize, type DebriefSummary } from '../lib/debrief.js';
import type { ReplayData } from '../lib/record-replay.js';
import { eventLabel, eventSeverity, SOURCE_LABEL, type EventSeverity } from '../lib/sim-event.js';
import type { SessionInfo } from '../lib/session-info.js';
import { getShipConfig, isShipId } from '../lib/ship-config.js';
import { ANCHORS, allElements, criterion, type BehaviouralAnchors } from './anchors.js';
import { computeAgreement, ratingOf, summarizeForm, type AgreementResult, type FormSummary, type MarkerForm } from './marker-form.js';

/** 每頁頁首固定標示(第 9.2 節)。 */
export const REPORT_HEADER_LABEL = '中塑海運內部訓練';

/** 講評結構(第 8.4 節五段)。 */
export const DEBRIEF_SECTIONS = [
  { id: 'reaction', title: '(1)反應階段', hint: '學員對練習的第一反應與感受;先讓團隊抒發,不評論' },
  { id: 'selfReview', title: '(2)團隊自我回顧', hint: '團隊依事件標記的重播回顧經過:發生了什麼、各自當時在做什麼' },
  { id: 'advocacyInquiry', title: '(3)倡議-探詢分析', hint: '「我在 14:32 看到 X,我擔心的是…,你們當時的想法是?」' },
  { id: 'sopTransfer', title: '(4)推廣到船上標準作業程序與公司程序', hint: '與 SMS 程序、常規命令、檢查表的對應;船上實務如何應用' },
  { id: 'summaryActions', title: '(5)總結與個人行動點', hint: '每位學員 1 至 2 個具體行動點' },
] as const;

export type DebriefSectionId = (typeof DEBRIEF_SECTIONS)[number]['id'];

/** 參與者角色(第 8.1 節共同角色)。 */
export const PARTICIPANT_ROLES: ReadonlyArray<{ id: string; label: string }> = [
  { id: 'master', label: '船長' },
  { id: 'oow', label: '當值船副' },
  { id: 'secondOfficer', label: '第二船副/實習生' },
  { id: 'helmsman', label: '舵工' },
  { id: 'lookout', label: '瞭望' },
  { id: 'pilot', label: '引水人(扮演)' },
  { id: 'ecr', label: '機控室/輪機員' },
  { id: 'leader', label: '帶領者' },
  { id: 'observer', label: '觀察員' },
];

export function participantRoleLabel(id: string): string {
  return PARTICIPANT_ROLES.find((r) => r.id === id)?.label ?? id;
}

export interface Participant {
  /** 學員名冊 id(非學員者留空) */
  traineeId: string;
  name: string;
  rank: string;
  role: string;
}

/** 報告的人工欄位(存在 IndexedDB,依 recordKey 分開)。 */
export interface ReportMeta {
  courseName: string;
  courseVersion: string;
  /** 課程班次編號(training-record.CourseSession.sessionId) */
  sessionId: string;
  /** 練習編號(E07、F01…;空白時由情境 id 推得) */
  exerciseNo: string;
  /** 報告/紀錄編號(空白時自動產生) */
  reportNo: string;
  instructor: string;
  participants: Participant[];
  debrief: Record<DebriefSectionId, string>;
  /** 教官總評 */
  remarks: string;
}

export function emptyReportMeta(): ReportMeta {
  return {
    courseName: '中塑海運 BRM 內部訓練課程',
    courseVersion: '',
    sessionId: '',
    exerciseNo: '',
    reportNo: '',
    instructor: '',
    participants: [],
    debrief: { reaction: '', selfReview: '', advocacyInquiry: '', sopTransfer: '', summaryActions: '' },
    remarks: '',
  };
}

/** 教官站 sessionInfo 職務 → 報告參與者角色(PARTICIPANT_ROLES;instructor 對應帶領者)。 */
export function participantRoleFromSession(role: string): string {
  return role === 'instructor' ? 'leader' : role;
}

/**
 * 由紀錄標頭的參與者與休息時間確認(教官站 sessionInfo)帶入報告人工欄位:只在報告尚無參與者時填入;
 * 學員 ID 以姓名(或證書號對應名冊 certificateExpiry 無法比對,故只比姓名)對應名冊;課程 id/標題/教官填到空白的班次/課程/教官欄。
 * 回傳是否有變更。
 */
export function applySessionInfoToMeta(meta: ReportMeta, info: SessionInfo | undefined, trainees: ReadonlyArray<{ id: string; name: string; rank: string }> = []): boolean {
  if (!info) return false;
  let changed = false;
  if (meta.participants.length === 0 && info.participants.length > 0) {
    for (const p of info.participants) {
      const t = trainees.find((x) => x.name && x.name === p.name);
      meta.participants.push({ traineeId: t?.id ?? '', name: p.name, rank: p.rank ?? t?.rank ?? '', role: participantRoleFromSession(p.role) });
    }
    changed = true;
  }
  const c = info.course;
  if (c?.instructor && !meta.instructor) {
    meta.instructor = c.instructor;
    changed = true;
  }
  if (c?.id && !meta.sessionId) {
    meta.sessionId = c.id;
    changed = true;
  }
  if (c?.title && meta.courseName === emptyReportMeta().courseName) {
    meta.courseName = c.title;
    changed = true;
  }
  return changed;
}

// ------------------------------------------------------------------ 紀錄識別

/**
 * 練習紀錄的識別鍵:核心紀錄用 footer.sha256(紀錄檔雜湊);教官站紀錄用 情境 id + 練習開始時刻 startedUtc
 * (同一練習多次匯出不變;舊紀錄無 startedUtc 時退回建立時間 createdUtc,與先前存下的評估表/報告欄位相容)。
 */
export function recordKey(data: ReplayData): string {
  if (data.footer?.sha256) return `sha256:${data.footer.sha256}`;
  return `${data.header.scenarioId ?? data.header.shipId ?? 'record'}@${data.header.startedUtc ?? data.header.createdUtc ?? data.tStart}`;
}

/** 練習日期的依據:教官站紀錄的練習開始時刻,否則紀錄建立時間(ISO)。 */
export function exerciseTimeUtc(data: ReplayData): string | undefined {
  return data.header.startedUtc ?? data.header.createdUtc;
}

/** ISO 時刻 → 本地日期 YYYY-MM-DD(報告編號、班次練習日期與報告「練習日期」一律用本地時間);無效回傳 ''。 */
export function localDate(iso: string | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** FNV-1a 32 位元雜湊(UTF-16 碼元)→ 8 位大寫十六進位;報告編號尾碼用,非安全用途。 */
export function fnv1aHex(text: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(16).toUpperCase().padStart(8, '0');
}

/** 由情境 id 取練習編號(E07_steering_failure → E07;F01_FSB1_full → F01)。 */
export function exerciseNoFromScenario(id: string | undefined): string {
  const m = /^([EF]\d{2})/i.exec(id ?? '');
  return m ? m[1]!.toUpperCase() : (id ?? '');
}

/**
 * 預設報告編號:RPT-<本地 yyyyMMdd>-<練習編號>-<6 碼>。6 碼:核心紀錄取紀錄檔 SHA-256 前 6 碼;
 * 教官站紀錄取紀錄鍵的 FNV-1a 雜湊前 6 碼(紀錄鍵含練習開始時刻,同情境同日的不同練習編號不同)。
 * 日期與班次練習日期(training-record.isoDate)相同,皆為本地日期。
 */
export function defaultReportNo(data: ReplayData): string {
  const ymd = localDate(exerciseTimeUtc(data)).replace(/-/g, '') || '00000000';
  const sha = data.footer?.sha256?.replace(/[^0-9A-Za-z]/g, '');
  const tail = sha ? sha.slice(0, 6).toUpperCase() : fnv1aHex(recordKey(data)).slice(0, 6);
  return `RPT-${ymd}-${exerciseNoFromScenario(data.header.scenarioId) || 'X'}-${tail}`;
}

// ------------------------------------------------------------------ 操縱量測(F01–F03;summary.manoeuvres[])

export interface ManoeuvreComparisonRow {
  key: string;
  label: string;
  unit: string;
  measured: number;
  target: number;
  targetMin?: number;
  targetMax?: number;
  deviation: number;
  deviationPct?: number;
  tolerance: number;
  withinTolerance: boolean;
  comparable: boolean;
  dataQuality: string;
  source: string;
}

export interface ManoeuvreRow {
  id: string;
  label: string;
  kind: string;
  state: string;
  complete: boolean;
  start_s?: number;
  end_s?: number;
  approachSpeed_kn?: number;
  rudder_deg?: number;
  order?: string;
  dataQuality: string;
  values: Array<{ key: string; label: string; unit: string; value: number }>;
  comparisons: ManoeuvreComparisonRow[];
  notes: string[];
}

const MANOEUVRE_KIND_LABEL: Readonly<Record<string, string>> = {
  turning: '迴旋',
  zigzag: 'Z 形',
  crashStop: '全速倒俥緊急停船',
  inertiaStop: '停俥慣性停船',
  thrusterTurn: '側推原地迴轉',
  rudderSwing: '舵機擺動(轉舵時間)',
  manOverboard: '人員落水迴旋',
};

const SIDE_LABEL: Readonly<Record<string, string>> = { port: '左', starboard: '右', portFirst: '先左', starboardFirst: '先右', toPort: '向左', toStarboard: '向右' };

export function manoeuvreLabel(id: string, kind: string): string {
  const [head, side] = id.split('.');
  const turn = /^turning(\d+)$/.exec(head ?? '');
  const zz = /^zigzag(\d+)$/.exec(head ?? '');
  let base = MANOEUVRE_KIND_LABEL[kind] ?? kind;
  if (turn) base = `${turn[1]}° 迴旋`;
  if (zz) base = `${zz[1]}/${zz[1]} Z 形`;
  return side ? `${base}(${SIDE_LABEL[side] ?? side})` : base;
}

const VALUE_LABEL: Readonly<Record<string, string>> = {
  advance: '縱距(前進距離)',
  transfer: '橫距',
  tacticalDiameter: '戰術直徑',
  steadyDiameter: '定常迴旋直徑',
  steadySpeed: '定常迴旋速度',
  steadyRot: '定常迴轉率',
  timeTo90: '轉 90° 時間',
  timeTo180: '轉 180° 時間',
  overshoot1: '第一超越角',
  overshoot2: '第二超越角',
  overshoot3: '第三超越角',
  timeToExecute: '到達執行航向時間',
  timeToSecondExecute: '第二次執行時間',
  firstRudder: '首舵角',
  headReach: 'Head reach',
  sideReach: '側向偏移',
  lateralOffset: '橫向偏移',
  trackReach: '航跡距離',
  stopTime: '停船時間',
  asternStart: '倒俥開始',
  headingChange: '航向變化',
  headingChange60: '60 s 航向變化',
  totalHeadingChange: '總航向變化',
  endSpeed: '結束速度',
  time: '時間',
  distance: '距離',
  time90: '轉 90° 時間',
  maxRot: '最大迴轉率',
  time30: '到 30° 時間',
  timeTo30: '到 30° 時間',
  time35to35: '35° 至另舷 35° 時間',
  timeToOrder: '到令時間',
  timeToMidships: '回正時間',
  from: '起始舵角',
  to: '目標舵角',
  initialRange: '初始距離',
  minDistance: '最近距離',
  minDistanceAt: '最近距離時刻',
  speedAtMin: '最近時速度',
  reactionTime: '反應時間',
  reciprocalAt: '轉至反航向時刻',
  rangeAtReciprocal: '反航向時距離',
  returnTime: '回到落水者時間',
  returnRadius: '回航半徑',
  returnSpeed: '回航速度',
  returnLateralOffset: '回航橫向偏移',
  returnTrackOffset: '回航航跡偏移',
};

const UNIT_LABEL: Readonly<Record<string, string>> = { m: 'm', s: 's', kn: 'kn', deg: '°', degPerMin: '°/min', min: 'min', nm: 'nm', L: 'L' };

/** 量測值鍵(advance_m、firstOvershoot_deg…)→ 中文名稱與單位。 */
export function splitValueKey(key: string): { label: string; unit: string } {
  const i = key.lastIndexOf('_');
  const base = i > 0 ? key.slice(0, i) : key;
  const unit = i > 0 ? key.slice(i + 1) : '';
  return { label: VALUE_LABEL[base] ?? base, unit: UNIT_LABEL[unit] ?? unit };
}

export const DATA_QUALITY_LABEL: Readonly<Record<string, string>> = { trial: '實測', poster: '海報讀值', estimate: '估計值', particulars: '交船圖說', none: '無目標值' };

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}
function num(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}
function str(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

/** 解析 summary.manoeuvres[](核心 ManoeuvreRecord;舊核心或非操縱練習沒有此段 → 空陣列)。 */
export function parseManoeuvres(raw: Record<string, unknown> | undefined): ManoeuvreRow[] {
  const list = raw && Array.isArray(raw['manoeuvres']) ? raw['manoeuvres'] : [];
  return list.filter(isRecord).map((m) => {
    const id = str(m['id']);
    const kind = str(m['kind']);
    const approach = isRecord(m['approach']) ? m['approach'] : {};
    const values = isRecord(m['values'])
      ? Object.entries(m['values'])
          .filter(([, v]) => num(v) !== undefined)
          .map(([k, v]) => ({ key: k, ...splitValueKey(k), value: v as number }))
      : [];
    const comparisons: ManoeuvreComparisonRow[] = (Array.isArray(m['comparisons']) ? m['comparisons'] : []).filter(isRecord).map((c) => {
      const key = str(c['key']);
      const row: ManoeuvreComparisonRow = {
        key,
        ...splitValueKey(key),
        measured: num(c['measured']) ?? NaN,
        target: num(c['target']) ?? NaN,
        deviation: num(c['deviation']) ?? NaN,
        tolerance: num(c['tolerance']) ?? NaN,
        withinTolerance: c['withinTolerance'] === true,
        comparable: c['comparable'] === true,
        dataQuality: str(c['dataQuality']),
        source: str(c['source']),
      };
      const tmin = num(c['targetMin']);
      const tmax = num(c['targetMax']);
      const pct = num(c['deviation_pct']);
      if (tmin !== undefined) row.targetMin = tmin;
      if (tmax !== undefined) row.targetMax = tmax;
      if (pct !== undefined) row.deviationPct = pct;
      return row;
    });
    const row: ManoeuvreRow = {
      id,
      label: manoeuvreLabel(id, kind),
      kind,
      state: str(m['state']) || 'measured',
      complete: m['complete'] === true,
      dataQuality: str(m['dataQuality']) || 'none',
      values,
      comparisons,
      notes: (Array.isArray(m['notes']) ? m['notes'] : []).filter((n): n is string => typeof n === 'string'),
    };
    const st = num(m['start_s']);
    const en = num(m['end_s']);
    const sp = num(approach['speed_kn']);
    const rd = num(m['rudder_deg']);
    if (st !== undefined) row.start_s = st;
    if (en !== undefined) row.end_s = en;
    if (sp !== undefined) row.approachSpeed_kn = sp;
    if (rd !== undefined) row.rudder_deg = rd;
    if (typeof m['order'] === 'string') row.order = m['order'];
    return row;
  });
}

// ------------------------------------------------------------------ 參數集與來源文件

export interface ParameterRow {
  key: string;
  value?: number | string | boolean;
  unit?: string;
  source?: string;
  revision?: string;
  clause?: string;
  /** 情境中引用此參數的路徑(header.parameterSet.resolved[].path) */
  paths: string[];
}

export interface SourceDocument {
  source: string;
  revisions: string[];
  clauses: string[];
  /** 引用此文件的參數鍵 */
  keys: string[];
}

export interface ParameterSection {
  id?: string;
  version?: string;
  path?: string;
  sha256?: string;
  rows: ParameterRow[];
  documents: SourceDocument[];
  /** header 與 summary 的參數集版次不一致時的提醒 */
  warnings: string[];
}

export function buildParameterSection(data: ReplayData): ParameterSection {
  const h = data.header.parameterSet;
  const s = data.summary?.parameterSet;
  const out: ParameterSection = { rows: [], documents: [], warnings: [] };
  const id = h?.id ?? s?.id;
  const version = h?.version ?? s?.version;
  const path = h?.path ?? s?.path;
  const sha = h?.sha256 ?? s?.sha256;
  if (id) out.id = id;
  if (version) out.version = version;
  if (path) out.path = path;
  if (sha) out.sha256 = sha;
  if (h?.version && s?.version && h.version !== s.version) out.warnings.push(`紀錄 header 參數集版次 ${h.version} 與 summary ${s.version} 不同`);
  if (h?.sha256 && s?.sha256 && h.sha256 !== s.sha256) out.warnings.push('紀錄 header 與 summary 的參數集雜湊不同');
  const byKey = new Map<string, ParameterRow>();
  const add = (p: { key: string; value?: number | string | boolean; unit?: string; source?: string; revision?: string; clause?: string }, path?: string) => {
    if (!p.key) return;
    let row = byKey.get(p.key);
    if (!row) {
      row = { key: p.key, paths: [] };
      byKey.set(p.key, row);
    }
    if (row.value === undefined && p.value !== undefined) row.value = p.value;
    for (const k of ['unit', 'source', 'revision', 'clause'] as const) if (row[k] === undefined && p[k] !== undefined) row[k] = p[k];
    if (path && !row.paths.includes(path)) row.paths.push(path);
  };
  for (const r of h?.resolved ?? []) add(r, r.path);
  for (const p of data.summary?.assessment.parameters ?? []) add(p);
  // 容許帶與必要事件的來源(summary 只有 paramKey + source)
  for (const b of [...(data.summary?.assessment.bands ?? []), ...(data.summary?.assessment.required ?? [])]) if (b.paramKey) add({ key: b.paramKey, ...(b.source ? { source: b.source } : {}), ...(b.revision ? { revision: b.revision } : {}), ...(b.clause ? { clause: b.clause } : {}) });
  out.rows = [...byKey.values()].sort((a, b) => a.key.localeCompare(b.key));
  const docs = new Map<string, SourceDocument>();
  for (const r of out.rows) {
    if (!r.source) continue;
    let d = docs.get(r.source);
    if (!d) {
      d = { source: r.source, revisions: [], clauses: [], keys: [] };
      docs.set(r.source, d);
    }
    if (r.revision && !d.revisions.includes(r.revision)) d.revisions.push(r.revision);
    if (r.clause && !d.clauses.includes(r.clause)) d.clauses.push(r.clause);
    d.keys.push(r.key);
  }
  out.documents = [...docs.values()].sort((a, b) => a.source.localeCompare(b.source));
  return out;
}

// ------------------------------------------------------------------ 事件時間軸

export interface TimelineRow {
  t: number;
  kind: string;
  source: string;
  label: string;
  severity: EventSeverity;
  phase?: string;
}

/** 講評時間軸不列的高頻/內部事件(仍計入 summary.events)。 */
const TIMELINE_SKIP = new Set(['timelineArmed', 'timelineDisarmed', 'environmentRampDone']);
export const TIMELINE_MAX_ROWS = 400;

export function buildTimeline(events: readonly SimEvent[], markers: readonly BrmMarker[] = []): { rows: TimelineRow[]; omitted: number } {
  const rows: TimelineRow[] = events
    .filter((e) => !TIMELINE_SKIP.has(e.kind))
    .map((e) => {
      const row: TimelineRow = { t: e.t, kind: e.kind, source: SOURCE_LABEL[e.source] ?? e.source, label: eventLabel(e), severity: eventSeverity(e) };
      if (e.phase) row.phase = e.phase;
      return row;
    });
  for (const m of markers) {
    const pol = m.polarity === 'plus' ? '+' : m.polarity === 'delta' ? 'Δ' : '';
    rows.push({ t: m.t, kind: 'brmMarker', source: 'BRM 標記', label: `${pol ? `${pol} ` : ''}${categoryLabel(m.category)}${m.note ? `:${m.note}` : ''}`, severity: 'info' });
  }
  rows.sort((a, b) => a.t - b.t);
  const omitted = Math.max(0, rows.length - TIMELINE_MAX_ROWS);
  return { rows: omitted ? rows.slice(0, TIMELINE_MAX_ROWS) : rows, omitted };
}

function categoryLabel(id: string): string {
  return ANCHORS.categories.find((c) => c.id === id)?.label ?? id;
}

// ------------------------------------------------------------------ 行為標記結果

export interface BehaviouralElementRow {
  elementId: string;
  categoryLabel: string;
  label: string;
  score: number | null;
  observations: Array<{ t_s: number | null; text: string }>;
  deficiency: string;
  comment: string;
  stcw: string[];
}

export interface BehaviouralResult {
  form: MarkerForm;
  summary: FormSummary;
  rows: BehaviouralElementRow[];
  /** 同一受評者第二評估員的表與一致率 */
  second?: { form: MarkerForm; agreement: AgreementResult };
}

export function buildBehavioural(forms: readonly MarkerForm[], anchors: BehaviouralAnchors = ANCHORS): BehaviouralResult[] {
  const primaries = forms.filter((f) => f.assessor.role === 'primary');
  const seconds = forms.filter((f) => f.assessor.role === 'second');
  // 只有第二評估員的表(主要評估員的表尚未載入)也列出
  const list = primaries.length > 0 ? primaries : seconds;
  return list.map((form) => {
    const rows = allElements(anchors).map((e) => {
      const r = ratingOf(form, e.id);
      const def = r?.deficiency;
      const defText = def?.type ? (anchors.deficiencyTypes.find((d) => d.id === def.type)?.label ?? def.type) + (def.type === 'human' && def.humanFactor ? `/${anchors.humanFactors.find((h) => h.id === def.humanFactor)?.label ?? def.humanFactor}` : '') : '';
      return {
        elementId: e.id,
        categoryLabel: e.categoryLabel,
        label: e.label,
        score: r?.score ?? null,
        observations: (r?.observations ?? []).map((o) => ({ t_s: o.t_s, text: o.text })),
        deficiency: defText,
        comment: r?.comment ?? '',
        stcw: e.stcw,
      };
    });
    const res: BehaviouralResult = { form, summary: summarizeForm(form, anchors), rows };
    if (form.assessor.role === 'primary') {
      const second = seconds.find((s) => s.trainee.id === form.trainee.id && s.trainee.name === form.trainee.name) ?? seconds.find((s) => s.trainee.id === form.trainee.id);
      if (second) res.second = { form: second, agreement: computeAgreement(form, second, anchors) };
    }
    return res;
  });
}

/** STCW 準則 → 對應的要素(報告「自動列出對應 STCW A-II/1 BRM 準則」)。 */
export function stcwMapping(anchors: BehaviouralAnchors = ANCHORS): Array<{ id: string; group: string; text: string; elements: string[] }> {
  return anchors.stcwCriteria.map((c) => ({
    id: c.id,
    group: anchors.criteriaGroups.find((g) => g.id === c.group)?.label ?? c.group,
    text: c.text,
    elements: allElements(anchors)
      .filter((e) => e.stcw.includes(c.id))
      .map((e) => `${e.categoryLabel}:${e.label}`),
  }));
}

export function stcwText(id: string): string {
  return criterion(id)?.text ?? id;
}

// ------------------------------------------------------------------ 報告

/** 屬於該練習紀錄的評估表(record.key 完全相同;空鍵不屬於任何紀錄)。 */
export function formsOfRecord(forms: readonly MarkerForm[], key: string): MarkerForm[] {
  return key ? forms.filter((f) => f.record.key === key) : [];
}

export interface ExerciseInfo {
  scenarioId: string;
  scenarioName: string;
  exerciseNo: string;
  shipId: string;
  shipName: string;
  loading: string;
  loadingLabel: string;
  /** 紀錄建立時間(ISO;教官站紀錄為匯出時刻) */
  createdUtc?: string;
  /** 練習開始時刻(教官站紀錄;ISO)。練習日期 = startedUtc ?? createdUtc */
  startedUtc?: string;
  /** 情境的模擬開始時刻(scenario.startTimeUtc) */
  scenarioStartUtc?: string;
  duration_s: number;
  seed?: number;
  dynamics?: string;
  source: ReplayData['source'];
  recordKey: string;
  stateHash?: string;
  sha256?: string;
  rewinds: number;
}

export interface DebriefReport {
  headerLabel: string;
  reportNo: string;
  meta: ReportMeta;
  exercise: ExerciseInfo;
  summary: AssessmentSummary | null;
  /** 無核心 summary 時的本地推導(debrief.ts) */
  local: DebriefSummary | null;
  manoeuvres: ManoeuvreRow[];
  timeline: TimelineRow[];
  timelineOmitted: number;
  markers: BrmMarker[];
  behavioural: BehaviouralResult[];
  anchorsVersion: string;
  anchorsStatus: string;
  debrief: Array<{ id: DebriefSectionId; title: string; hint: string; text: string }>;
  parameters: ParameterSection;
  /** 紀錄標頭的參與者與休息時間確認(教官站 sessionInfo;規劃書第 5.5 節);紀錄沒有時 null */
  sessionInfo: SessionInfo | null;
  generatedUtc: string;
}

const LOADING_LABEL: Readonly<Record<string, string>> = { full: '滿載', ballast: '壓載', intermediate: '中間' };

export interface BuildReportInput {
  data: ReplayData;
  meta?: ReportMeta;
  forms?: readonly MarkerForm[];
  /** 另外載入的 BRM 快速標記(核心紀錄不含標記;教官站紀錄已含者一併列出,依 id 去重) */
  extraMarkers?: readonly BrmMarker[];
  now?: Date;
  anchors?: BehaviouralAnchors;
}

export function buildDebriefReport(input: BuildReportInput): DebriefReport {
  const { data } = input;
  const anchors = input.anchors ?? ANCHORS;
  const meta = input.meta ?? emptyReportMeta();
  const h = data.header;
  const sc = isRecord(h.scenario) ? h.scenario : {};
  const shipId = h.shipId ?? data.summary?.shipId ?? '';
  const loading = h.loading ?? data.summary?.loading ?? (isRecord(sc['ship']) ? str(sc['ship']['loading']) : '');
  const exercise: ExerciseInfo = {
    scenarioId: h.scenarioId ?? data.summary?.scenarioId ?? '',
    scenarioName: h.scenarioName ?? '',
    exerciseNo: meta.exerciseNo || exerciseNoFromScenario(h.scenarioId ?? data.summary?.scenarioId),
    shipId,
    shipName: h.shipName ?? (isShipId(shipId) ? getShipConfig(shipId).name : shipId),
    loading,
    loadingLabel: LOADING_LABEL[loading] ?? loading,
    duration_s: data.summary?.general.duration_s ?? Math.max(0, data.tEnd - data.tStart),
    source: data.source,
    recordKey: recordKey(data),
    rewinds: data.rewinds.length,
  };
  if (h.createdUtc) exercise.createdUtc = h.createdUtc;
  if (h.startedUtc) exercise.startedUtc = h.startedUtc;
  if (typeof sc['startTimeUtc'] === 'string') exercise.scenarioStartUtc = sc['startTimeUtc'];
  if (h.seed !== undefined) exercise.seed = h.seed;
  if (h.dynamics) exercise.dynamics = h.dynamics;
  if (data.footer?.stateHash) exercise.stateHash = data.footer.stateHash;
  if (data.footer?.sha256) exercise.sha256 = data.footer.sha256;

  const markers = [...data.markers];
  for (const m of input.extraMarkers ?? []) if (!markers.some((x) => x.id === m.id && x.t === m.t)) markers.push(m);
  markers.sort((a, b) => a.t - b.t);
  const tl = buildTimeline(data.events, markers);
  // 嚴格比對紀錄鍵:沒有紀錄鍵的評估表不屬於任何練習(報告頁提示綁定到目前紀錄)
  const recordForms = formsOfRecord(input.forms ?? [], exercise.recordKey);
  return {
    headerLabel: REPORT_HEADER_LABEL,
    reportNo: meta.reportNo || defaultReportNo(data),
    meta,
    exercise,
    summary: data.summary,
    local: data.summary || data.states.length === 0 ? null : summarize(data.states),
    manoeuvres: parseManoeuvres(data.summary?.raw),
    timeline: tl.rows,
    timelineOmitted: tl.omitted,
    markers,
    behavioural: buildBehavioural(recordForms, anchors),
    anchorsVersion: anchors.version,
    anchorsStatus: anchors.statusLabel,
    debrief: DEBRIEF_SECTIONS.map((s) => ({ id: s.id, title: s.title, hint: s.hint, text: meta.debrief[s.id] ?? '' })),
    parameters: buildParameterSection(data),
    sessionInfo: data.header.sessionInfo ?? null,
    generatedUtc: (input.now ?? new Date()).toISOString(),
  };
}

/** 報告的純文字大綱(測試與無障礙檢查用;依章節列出關鍵值)。 */
export function reportOutline(r: DebriefReport): string[] {
  const out: string[] = [`${r.headerLabel} 講評報告 ${r.reportNo}`, `課程 ${r.meta.courseName} ${r.meta.courseVersion}`.trim()];
  out.push(`練習 ${r.exercise.exerciseNo} ${r.exercise.scenarioId} ${r.exercise.scenarioName}`);
  out.push(`船 ${r.exercise.shipId} ${r.exercise.shipName} ${r.exercise.loadingLabel}`);
  out.push(`時長 ${Math.round(r.exercise.duration_s)} s`);
  const a = r.summary?.assessment;
  if (a) out.push(`評分 ${a.score ?? '—'} / 及格 ${a.passMark ?? '—'} ${a.hardFailed ? '硬性失敗' : a.passed ? '通過' : a.passed === false ? '未通過' : ''}`.trim());
  if (r.parameters.version) out.push(`參數集 ${r.parameters.id ?? ''} ${r.parameters.version}`.trim());
  if (r.parameters.documents.length) out.push(`來源文件 ${r.parameters.documents.map((d) => `${d.source} 第 ${d.revisions.join('/')} 版`).join('、')}`);
  return out;
}
