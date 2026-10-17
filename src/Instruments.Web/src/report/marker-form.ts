// 行為標記評估表(規劃書第 8.2 節):十五項要素 1 至 5 分,2 分以下必須附時間戳與觀察描述;缺失依 SIRE 2.0 分為
// 硬體/程序/人員(人員再分熟悉度、任務設計、設備、緊張/語言、疲勞);BRM 快速標記可帶入為觀察證據;
// 雙評時以第二位評估員的表計算一致率(±1 分內比例,目標 ≥ 80%)。評估表只存在瀏覽器(IndexedDB)與匯出的 JSON,不經核心。

import { ANCHORS, allElements, isDeficiencyType, isHumanFactor, type BehaviouralAnchors, type DeficiencyType, type HumanFactor } from './anchors.js';
import { brmCategory, type BrmMarker } from '../lib/brm-markers.js';

export const MARKER_FORM_FORMAT = 'simosa-brm-marker-form';

export interface MarkerObservation {
  /** 模擬時間(秒);未填為 null */
  t_s: number | null;
  text: string;
  /** 由 BRM 快速標記帶入時的標記 id */
  markerId?: number;
  polarity?: 'plus' | 'delta';
}

export interface ElementRating {
  elementId: string;
  /** 1–5;未評分為 null */
  score: number | null;
  observations: MarkerObservation[];
  deficiency: { type: DeficiencyType | null; humanFactor: HumanFactor | null };
  comment: string;
}

export type AssessorRole = 'primary' | 'second';

export interface MarkerForm {
  format: typeof MARKER_FORM_FORMAT;
  version: 1;
  /** 所用錨定描述版本(behavioural-anchors.json 的 version) */
  anchorsVersion: string;
  formId: string;
  /** 被評估的練習紀錄(recordKey 由 debrief-report.recordKey 產生) */
  record: { key: string; scenarioId?: string; shipId?: string; createdUtc?: string };
  /** 受評者(個人或團隊;團隊時 id 留空、name 填「團隊」) */
  trainee: { id: string; name: string; rank: string; role: string };
  assessor: { name: string; role: AssessorRole };
  /** 評分完成時刻(ISO;本地時鐘) */
  assessedUtc: string;
  ratings: ElementRating[];
  overallComment: string;
}

export interface FormIssue {
  level: 'error' | 'warning';
  /** 要素 id;整張表的問題為 undefined */
  elementId?: string;
  /** observation 索引(觀察描述的問題) */
  index?: number;
  message: string;
}

let formSeq = 0;
function newFormId(now: Date): string {
  formSeq = (formSeq + 1) % 1_000_000;
  return `F${now.getTime().toString(36).toUpperCase()}${formSeq.toString(36).toUpperCase().padStart(2, '0')}`;
}

export interface NewFormInput {
  record: MarkerForm['record'];
  trainee?: Partial<MarkerForm['trainee']>;
  assessor?: Partial<MarkerForm['assessor']>;
  now?: Date;
  anchors?: BehaviouralAnchors;
}

/** 建立空白評估表(十五項要素依錨定描述順序,皆未評分)。 */
export function createMarkerForm(input: NewFormInput): MarkerForm {
  const now = input.now ?? new Date();
  const a = input.anchors ?? ANCHORS;
  return {
    format: MARKER_FORM_FORMAT,
    version: 1,
    anchorsVersion: a.version,
    formId: newFormId(now),
    record: { ...input.record },
    trainee: { id: input.trainee?.id ?? '', name: input.trainee?.name ?? '', rank: input.trainee?.rank ?? '', role: input.trainee?.role ?? '' },
    assessor: { name: input.assessor?.name ?? '', role: input.assessor?.role ?? 'primary' },
    assessedUtc: now.toISOString(),
    ratings: allElements(a).map((e) => ({ elementId: e.id, score: null, observations: [], deficiency: { type: null, humanFactor: null }, comment: '' })),
    overallComment: '',
  };
}

export function ratingOf(form: MarkerForm, elementId: string): ElementRating | undefined {
  return form.ratings.find((r) => r.elementId === elementId);
}

function validScore(v: unknown): v is number {
  return typeof v === 'number' && Number.isInteger(v) && v >= 1 && v <= 5;
}

/**
 * 表單驗證:
 *  - 錯誤:分數不在 1–5 整數;2 分以下沒有「時間戳 + 觀察描述」都齊的觀察;觀察的時間戳無效(負數、非數字);
 *    人員缺失未選子類、或非人員缺失卻選了人員子類;評估員姓名空白;未知要素。
 *  - 警告:未評分;2 分以下未標記缺失類型(建議依 SIRE 2.0 分類);觀察時間超出練習時長;受評者姓名空白。
 * duration_s:練習時長(秒;已知時檢查觀察時間範圍)。
 */
export function validateMarkerForm(form: MarkerForm, options: { duration_s?: number; anchors?: BehaviouralAnchors } = {}): FormIssue[] {
  const a = options.anchors ?? ANCHORS;
  const issues: FormIssue[] = [];
  if (!form.assessor.name.trim()) issues.push({ level: 'error', message: '請填評估員姓名' });
  if (!form.trainee.name.trim()) issues.push({ level: 'warning', message: '未填受評者(個人姓名或「團隊」)' });
  const known = new Set(allElements(a).map((e) => e.id));
  for (const id of known) if (!form.ratings.some((r) => r.elementId === id)) issues.push({ level: 'warning', elementId: id, message: '未評分' });
  for (const r of form.ratings) {
    if (!known.has(r.elementId)) {
      issues.push({ level: 'error', elementId: r.elementId, message: '未知的要素(錨定描述版本不符?)' });
      continue;
    }
    if (r.score === null) issues.push({ level: 'warning', elementId: r.elementId, message: '未評分' });
    else if (!validScore(r.score)) issues.push({ level: 'error', elementId: r.elementId, message: '分數須為 1 至 5 的整數' });
    r.observations.forEach((o, i) => {
      if (o.t_s !== null && !(typeof o.t_s === 'number' && Number.isFinite(o.t_s) && o.t_s >= 0)) issues.push({ level: 'error', elementId: r.elementId, index: i, message: '觀察的時間戳無效' });
      else if (o.t_s !== null && options.duration_s !== undefined && o.t_s > options.duration_s + 1) issues.push({ level: 'warning', elementId: r.elementId, index: i, message: '觀察時間超出練習時長' });
    });
    if (validScore(r.score) && r.score <= a.lowScoreThreshold) {
      const complete = r.observations.some((o) => typeof o.t_s === 'number' && Number.isFinite(o.t_s) && o.t_s >= 0 && o.text.trim().length > 0);
      if (!complete) {
        const hasText = r.observations.some((o) => o.text.trim().length > 0);
        const hasTime = r.observations.some((o) => o.t_s !== null);
        const missing = !hasText && !hasTime ? '時間戳與觀察描述' : !hasTime ? '時間戳' : !hasText ? '觀察描述' : '同一筆觀察的時間戳與描述';
        issues.push({ level: 'error', elementId: r.elementId, message: `${a.lowScoreThreshold} 分以下必須附${missing}(第 8.2 節)` });
      }
      if (!r.deficiency.type) issues.push({ level: 'warning', elementId: r.elementId, message: '建議依 SIRE 2.0 標記缺失類型(硬體/程序/人員)' });
    }
    if (r.deficiency.type !== null && !isDeficiencyType(r.deficiency.type)) issues.push({ level: 'error', elementId: r.elementId, message: '缺失類型無效' });
    if (r.deficiency.type === 'human' && !r.deficiency.humanFactor) issues.push({ level: 'error', elementId: r.elementId, message: '人員缺失須再分熟悉度、任務設計、設備、緊張/語言或疲勞' });
    if (r.deficiency.type !== 'human' && r.deficiency.humanFactor) issues.push({ level: 'error', elementId: r.elementId, message: '人員子類只適用於「人員」缺失' });
    if (r.deficiency.humanFactor && !isHumanFactor(r.deficiency.humanFactor)) issues.push({ level: 'error', elementId: r.elementId, message: '人員子類無效' });
  }
  return issues;
}

export function hasErrors(issues: readonly FormIssue[]): boolean {
  return issues.some((i) => i.level === 'error');
}

/** 評估表完成:無錯誤且十五項皆已評分。 */
export function isFormComplete(form: MarkerForm, issues: readonly FormIssue[] = validateMarkerForm(form)): boolean {
  return !hasErrors(issues) && form.ratings.every((r) => r.score !== null);
}

// ------------------------------------------------------------------ 時間輸入

/** "HH:MM:SS"、"MM:SS" 或秒數字串 → 秒;空白回傳 null,無法解析回傳 NaN。 */
export function parseSimTimeInput(text: string): number | null {
  const s = text.trim();
  if (!s) return null;
  if (/^\d+(\.\d+)?$/.test(s)) return Number(s);
  const m = /^(\d+):([0-5]?\d)(?::([0-5]?\d(?:\.\d+)?))?$/.exec(s);
  if (!m) return Number.NaN;
  const [, a, b, c] = m;
  return c === undefined ? Number(a) * 60 + Number(b) : Number(a) * 3600 + Number(b) * 60 + Number(c);
}

// ------------------------------------------------------------------ BRM 快速標記 → 觀察

/** 快速標記可帶入的要素(同類別的三項)。 */
export function elementsForMarker(marker: BrmMarker, a: BehaviouralAnchors = ANCHORS): string[] {
  return a.categories.find((c) => c.id === marker.category)?.elements.map((e) => e.id) ?? [];
}

/** 把 BRM 快速標記帶入為某要素的觀察(同一標記不重複帶入同一要素);回傳是否新增。 */
export function attachMarker(form: MarkerForm, elementId: string, marker: BrmMarker): boolean {
  const r = ratingOf(form, elementId);
  if (!r) return false;
  if (r.observations.some((o) => o.markerId === marker.id)) return false;
  const pol = marker.polarity === 'plus' ? '(+)' : marker.polarity === 'delta' ? '(Δ)' : '';
  const obs: MarkerObservation = { t_s: marker.t, text: `${pol}${marker.note || `${brmCategory(marker.category).label}快速標記`}`, markerId: marker.id };
  if (marker.polarity) obs.polarity = marker.polarity;
  r.observations.push(obs);
  r.observations.sort((x, y) => (x.t_s ?? Infinity) - (y.t_s ?? Infinity));
  return true;
}

// ------------------------------------------------------------------ 統計

export interface CategoryScore {
  categoryId: string;
  label: string;
  mean: number | null;
  min: number | null;
  rated: number;
}

export interface FormSummary {
  mean: number | null;
  min: number | null;
  rated: number;
  total: number;
  lowCount: number;
  categories: CategoryScore[];
  deficiencies: Record<DeficiencyType, number>;
  humanFactors: Record<HumanFactor, number>;
}

function mean(xs: number[]): number | null {
  return xs.length === 0 ? null : xs.reduce((s, x) => s + x, 0) / xs.length;
}

export function summarizeForm(form: MarkerForm, a: BehaviouralAnchors = ANCHORS): FormSummary {
  const scores = form.ratings.map((r) => r.score).filter(validScore);
  const deficiencies: Record<DeficiencyType, number> = { hardware: 0, procedure: 0, human: 0 };
  const humanFactors: Record<HumanFactor, number> = { familiarity: 0, taskDesign: 0, equipment: 0, stressLanguage: 0, fatigue: 0 };
  for (const r of form.ratings) {
    if (r.deficiency.type && isDeficiencyType(r.deficiency.type)) deficiencies[r.deficiency.type]++;
    if (r.deficiency.type === 'human' && r.deficiency.humanFactor && isHumanFactor(r.deficiency.humanFactor)) humanFactors[r.deficiency.humanFactor]++;
  }
  return {
    mean: mean(scores),
    min: scores.length ? Math.min(...scores) : null,
    rated: scores.length,
    total: allElements(a).length,
    lowCount: scores.filter((s) => s <= a.lowScoreThreshold).length,
    categories: a.categories.map((c) => {
      const cs = c.elements.map((e) => ratingOf(form, e.id)?.score).filter(validScore);
      return { categoryId: c.id, label: c.label, mean: mean(cs), min: cs.length ? Math.min(...cs) : null, rated: cs.length };
    }),
    deficiencies,
    humanFactors,
  };
}

// ------------------------------------------------------------------ 雙評一致率(第 8.2 節校準)

export interface AgreementResult {
  /** 兩表皆有評分的要素數 */
  compared: number;
  /** 錨定描述的要素總數(十五項) */
  total: number;
  /** 兩表都評完全部要素(missing 為空);未評完不判定是否達成(第 8.2 節校準以整張表計) */
  complete: boolean;
  /** 差距 ≤ tolerance 的要素數 */
  withinTolerance: number;
  exact: number;
  /** withinTolerance / compared;compared = 0 時 null */
  rate: number | null;
  exactRate: number | null;
  tolerance: number;
  target: number;
  /** complete 且 rate ≥ target */
  meetsTarget: boolean;
  /** 差距超過 tolerance 的要素(需協調並記錄原因與最終分數) */
  differences: Array<{ elementId: string; a: number; b: number; diff: number }>;
  /** 任一表未評分的要素 */
  missing: string[];
  /** 兩表不是同一練習紀錄或同一受評者時的提醒 */
  warnings: string[];
}

export function computeAgreement(a: MarkerForm, b: MarkerForm, anchors: BehaviouralAnchors = ANCHORS): AgreementResult {
  const tol = anchors.agreement.tolerance;
  const target = anchors.agreement.target;
  let compared = 0;
  let within = 0;
  let exact = 0;
  const differences: AgreementResult['differences'] = [];
  const missing: string[] = [];
  for (const e of allElements(anchors)) {
    const sa = ratingOf(a, e.id)?.score;
    const sb = ratingOf(b, e.id)?.score;
    if (!validScore(sa) || !validScore(sb)) {
      missing.push(e.id);
      continue;
    }
    compared++;
    const diff = Math.abs(sa - sb);
    if (diff <= tol) within++;
    else differences.push({ elementId: e.id, a: sa, b: sb, diff });
    if (diff === 0) exact++;
  }
  const warnings: string[] = [];
  if (a.record.key && b.record.key && a.record.key !== b.record.key) warnings.push('兩份評估表不是同一份練習紀錄');
  if ((a.trainee.id || b.trainee.id) && a.trainee.id !== b.trainee.id) warnings.push('兩份評估表的受評者不同');
  if (a.anchorsVersion !== b.anchorsVersion) warnings.push(`錨定描述版本不同(${a.anchorsVersion} / ${b.anchorsVersion})`);
  if (a.assessor.name.trim() && a.assessor.name.trim() === b.assessor.name.trim()) warnings.push('兩份評估表的評估員相同');
  const rate = compared ? within / compared : null;
  const total = allElements(anchors).length;
  const complete = missing.length === 0 && compared === total;
  return { compared, total, complete, withinTolerance: within, exact, rate, exactRate: compared ? exact / compared : null, tolerance: tol, target, meetsTarget: complete && rate !== null && rate >= target, differences, missing, warnings };
}

// ------------------------------------------------------------------ JSON 匯出/匯入

export interface MarkerFormBundle {
  format: 'simosa-brm-marker-forms';
  version: 1;
  exportedUtc: string;
  anchorsVersion: string;
  forms: MarkerForm[];
}

export function exportForms(forms: readonly MarkerForm[], now = new Date()): string {
  const bundle: MarkerFormBundle = { format: 'simosa-brm-marker-forms', version: 1, exportedUtc: now.toISOString(), anchorsVersion: ANCHORS.version, forms: [...forms] };
  return JSON.stringify(bundle, null, 2);
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function s(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

/** 正規化一份評估表(缺欄位補預設、未知要素保留以便驗證提示);不是評估表回傳 null。 */
export function normalizeForm(raw: unknown, anchors: BehaviouralAnchors = ANCHORS): MarkerForm | null {
  if (!isRecord(raw) || raw['format'] !== MARKER_FORM_FORMAT || !Array.isArray(raw['ratings'])) return null;
  const rec = isRecord(raw['record']) ? raw['record'] : {};
  const tr = isRecord(raw['trainee']) ? raw['trainee'] : {};
  const as = isRecord(raw['assessor']) ? raw['assessor'] : {};
  const record: MarkerForm['record'] = { key: s(rec['key']) };
  for (const k of ['scenarioId', 'shipId', 'createdUtc'] as const) if (typeof rec[k] === 'string') record[k] = rec[k] as string;
  const ratingsIn = (raw['ratings'] as unknown[]).filter(isRecord);
  const byId = new Map(ratingsIn.map((r) => [s(r['elementId']), r]));
  const ids = [...allElements(anchors).map((e) => e.id), ...[...byId.keys()].filter((k) => k && !allElements(anchors).some((e) => e.id === k))];
  const ratings: ElementRating[] = ids.map((id) => {
    const r = byId.get(id);
    const def = isRecord(r?.['deficiency']) ? (r['deficiency'] as Record<string, unknown>) : {};
    const sc = r?.['score'];
    return {
      elementId: id,
      score: typeof sc === 'number' ? sc : null,
      observations: (Array.isArray(r?.['observations']) ? (r['observations'] as unknown[]) : []).filter(isRecord).map((o) => {
        const obs: MarkerObservation = { t_s: typeof o['t_s'] === 'number' ? o['t_s'] : null, text: s(o['text']) };
        if (typeof o['markerId'] === 'number') obs.markerId = o['markerId'];
        if (o['polarity'] === 'plus' || o['polarity'] === 'delta') obs.polarity = o['polarity'];
        return obs;
      }),
      deficiency: { type: isDeficiencyType(def['type']) ? def['type'] : null, humanFactor: isHumanFactor(def['humanFactor']) ? def['humanFactor'] : null },
      comment: s(r?.['comment']),
    };
  });
  return {
    format: MARKER_FORM_FORMAT,
    version: 1,
    anchorsVersion: s(raw['anchorsVersion']) || anchors.version,
    formId: s(raw['formId']) || newFormId(new Date()),
    record,
    trainee: { id: s(tr['id']), name: s(tr['name']), rank: s(tr['rank']), role: s(tr['role']) },
    assessor: { name: s(as['name']), role: as['role'] === 'second' ? 'second' : 'primary' },
    assessedUtc: s(raw['assessedUtc']),
    ratings,
    overallComment: s(raw['overallComment']),
  };
}

/** 匯入:接受單張評估表或 exportForms 的整包;回傳表單與略過的筆數。 */
export function importForms(text: string): { forms: MarkerForm[]; skipped: number; error?: string } {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return { forms: [], skipped: 0, error: 'JSON 解析失敗' };
  }
  const list: unknown[] = isRecord(obj) && obj['format'] === 'simosa-brm-marker-forms' && Array.isArray(obj['forms']) ? obj['forms'] : [obj];
  const forms: MarkerForm[] = [];
  let skipped = 0;
  for (const raw of list) {
    const f = normalizeForm(raw);
    if (f) forms.push(f);
    else skipped++;
  }
  if (forms.length === 0) return { forms, skipped, error: '不是行為標記評估表(format 欄位不符)' };
  return { forms, skipped };
}
