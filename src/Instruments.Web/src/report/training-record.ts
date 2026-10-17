// 個人訓練紀錄與課程班次紀錄(規劃書第 9.1 節「多站與管理」、第 14 章評估列):
//  - 學員名冊(JSON 匯出/匯入);
//  - 課程班次紀錄 CourseSession:開課日期與時數、參訓學員與職級、授課教官、使用之情境與設備配置、出席、課後評估,
//    匯出 CSV(UTF-8 BOM,一列 = 一位學員 × 一個練習)與 JSON;
//  - 個人訓練紀錄:每位學員每期一份(課程名稱與版本、日期與時數、出席與完成之練習編號、教官與評估員、評估結果摘要與紀錄編號),
//    固定載明內部訓練聲明(INTERNAL_TRAINING_STATEMENT)。
// 資料只在瀏覽器(IndexedDB)與匯出檔;不連網、不經核心。

import { csvEscape, csvSafeText } from '../lib/brm-markers.js';
import { exerciseNoFromScenario, formsOfRecord, localDate, type DebriefReport } from './debrief-report.js';
import { isFormComplete, summarizeForm, validateMarkerForm, type MarkerForm } from './marker-form.js';

/** 第 9.1 節原文;個人訓練紀錄必須固定載明。 */
export const INTERNAL_TRAINING_STATEMENT = '中塑海運公司內部訓練紀錄,非依《船員訓練檢覈及申請核發證書辦法》核發之專業訓練證書';

export const ROSTER_FORMAT = 'simosa-brm-roster';
export const COURSE_SESSION_FORMAT = 'simosa-brm-course-session';

export interface Trainee {
  /** 學員 ID(跨課程保留,第 8.4 節 Kirkpatrick 長期追蹤) */
  id: string;
  name: string;
  /** 職級(船長、大副、二副、三副、實習生…) */
  rank: string;
  shipId?: string;
  /** 法定 BRM 證書到期日(YYYY-MM-DD;雙軌追蹤用,選填) */
  certificateExpiry?: string;
}

export interface StaffMember {
  name: string;
  /** instructor 授課教官 | assessor 評估員 | leader 船上帶領者 */
  role: 'instructor' | 'assessor' | 'leader';
  qualification?: string;
}

export interface Roster {
  format: typeof ROSTER_FORMAT;
  version: 1;
  trainees: Trainee[];
  staff: StaffMember[];
}

/** 第 8.0 節訓練形式。 */
export const TRAINING_FORMS: ReadonlyArray<{ id: string; label: string }> = [
  { id: 'full', label: '完整課程(模組化)' },
  { id: 'refresher', label: '定期複訓' },
  { id: 'continuous', label: '持續練習' },
  { id: 'familiarisation', label: '船舶熟悉' },
  { id: 'berthingRehearsal', label: '靠離泊預演' },
  { id: 'incident', label: '事故案例重演' },
];

export function trainingFormLabel(id: string): string {
  return TRAINING_FORMS.find((f) => f.id === id)?.label ?? id;
}

/** 評估員對個人的判定(R9「通過/未通過」;補訓與複查狀態待 D20)。 */
export type TraineeVerdict = 'pass' | 'fail';

export const VERDICT_LABEL: Readonly<Record<TraineeVerdict, string>> = { pass: '通過', fail: '未通過' };

export function isTraineeVerdict(v: unknown): v is TraineeVerdict {
  return v === 'pass' || v === 'fail';
}

export interface TraineeResult {
  /** 核心自動評分(團隊層級;自動評分僅輔助,不是個人判定) */
  score?: number;
  passed?: boolean;
  hardFailed?: boolean;
  /** 該學員主要評估員評估表的平均分數與最低分(只在評估表完整且合規時寫入) */
  markerMean?: number;
  markerMin?: number;
  formId?: string;
  /** 有主要評估員評估表但未完成或不合規(第 8.2 節;未評滿十五項或 2 分以下缺時間戳/觀察):不寫入平均/最低 */
  formIncomplete?: boolean;
  /** 評估員判定(通過/未通過;未判定時省略)。與自動評分分開,由評估員在個人訓練紀錄分頁輸入 */
  verdict?: TraineeVerdict;
  /** 評估員備註(判定理由、補訓建議等) */
  remark?: string;
}

export interface SessionExercise {
  /** 練習編號(E01、E07、F01…) */
  exerciseNo: string;
  scenarioId: string;
  /** 練習日期 YYYY-MM-DD */
  date: string;
  /** 簡報 + 執行 + 講評合計時數 */
  hours: number;
  /** 練習紀錄識別鍵(debrief-report.recordKey) */
  recordKey: string;
  /** 講評報告編號 */
  reportNo: string;
  instructor: string;
  assessors: string[];
  /** 設備配置(例:船上筆電 + 操控台;教官筆電直連) */
  equipment: string;
  attended: string[];
  completed: string[];
  results: Record<string, TraineeResult>;
}

export interface CourseSession {
  format: typeof COURSE_SESSION_FORMAT;
  version: 1;
  sessionId: string;
  courseName: string;
  courseVersion: string;
  trainingForm: string;
  shipId: string;
  startDate: string;
  endDate: string;
  instructors: string[];
  assessors: string[];
  /** 預設設備配置(各練習可覆寫) */
  equipment: string;
  trainees: Trainee[];
  exercises: SessionExercise[];
  /** 課後評估(Kirkpatrick L1 問卷摘要等) */
  evaluation: string;
}

export function emptyRoster(): Roster {
  return { format: ROSTER_FORMAT, version: 1, trainees: [], staff: [] };
}

export function emptyCourseSession(): CourseSession {
  return {
    format: COURSE_SESSION_FORMAT,
    version: 1,
    sessionId: '',
    courseName: '中塑海運 BRM 內部訓練課程',
    courseVersion: '',
    trainingForm: 'full',
    shipId: '',
    startDate: '',
    endDate: '',
    instructors: [],
    assessors: [],
    equipment: '船上筆電(SimCore + 儀器 + 視景)、USB 操控台;教官筆電以網路線直連',
    trainees: [],
    exercises: [],
    evaluation: '',
  };
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}
function s(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' ? String(v) : '';
}
/** 單行欄位:控制字元(含 CR/LF/FF/Tab)換成空白,避免匯入檔破壞頁首、CSV 或版面。 */
export function singleLine(v: string): string {
  return v.replace(/[\u0000-\u001f\u007f]/g, ' ');
}
function line(v: unknown): string {
  return singleLine(s(v));
}
function strList(v: unknown): string[] {
  return (Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : typeof v === 'string' && v ? [v] : []).map(singleLine);
}

function normalizeTrainee(v: unknown): Trainee | null {
  if (!isRecord(v)) return null;
  const id = line(v['id']).trim();
  const name = line(v['name']).trim();
  if (!id && !name) return null;
  const t: Trainee = { id: id || name, name, rank: line(v['rank']) };
  if (typeof v['shipId'] === 'string' && v['shipId']) t.shipId = v['shipId'];
  if (typeof v['certificateExpiry'] === 'string' && v['certificateExpiry']) t.certificateExpiry = v['certificateExpiry'];
  return t;
}

export function parseRoster(text: string): { roster: Roster | null; error?: string } {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return { roster: null, error: 'JSON 解析失敗' };
  }
  if (!isRecord(obj) || (obj['format'] !== ROSTER_FORMAT && obj['format'] !== COURSE_SESSION_FORMAT)) return { roster: null, error: '不是學員名冊(format 欄位不符)' };
  const trainees = (Array.isArray(obj['trainees']) ? obj['trainees'] : []).map(normalizeTrainee).filter((t): t is Trainee => t !== null);
  const staff: StaffMember[] = (Array.isArray(obj['staff']) ? obj['staff'] : []).filter(isRecord).map((m) => {
    const r = m['role'];
    const st: StaffMember = { name: s(m['name']), role: r === 'assessor' || r === 'leader' ? r : 'instructor' };
    if (typeof m['qualification'] === 'string') st.qualification = m['qualification'];
    return st;
  });
  return { roster: { format: ROSTER_FORMAT, version: 1, trainees, staff } };
}

export function exportRoster(roster: Roster): string {
  return JSON.stringify(roster, null, 2);
}

export function parseCourseSession(text: string): { session: CourseSession | null; error?: string } {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return { session: null, error: 'JSON 解析失敗' };
  }
  return normalizeCourseSession(obj);
}

export function normalizeCourseSession(obj: unknown): { session: CourseSession | null; error?: string } {
  if (!isRecord(obj) || obj['format'] !== COURSE_SESSION_FORMAT) return { session: null, error: '不是課程班次紀錄(format 欄位不符)' };
  const base = emptyCourseSession();
  const session: CourseSession = {
    ...base,
    sessionId: line(obj['sessionId']),
    courseName: line(obj['courseName']) || base.courseName,
    courseVersion: line(obj['courseVersion']),
    trainingForm: line(obj['trainingForm']) || base.trainingForm,
    shipId: line(obj['shipId']),
    startDate: line(obj['startDate']),
    endDate: line(obj['endDate']),
    instructors: strList(obj['instructors']),
    assessors: strList(obj['assessors']),
    equipment: typeof obj['equipment'] === 'string' ? singleLine(obj['equipment']) : base.equipment,
    trainees: (Array.isArray(obj['trainees']) ? obj['trainees'] : []).map(normalizeTrainee).filter((t): t is Trainee => t !== null),
    exercises: (Array.isArray(obj['exercises']) ? obj['exercises'] : []).filter(isRecord).map((e) => {
      const results: Record<string, TraineeResult> = {};
      if (isRecord(e['results'])) {
        for (const [id, r] of Object.entries(e['results'])) {
          if (!isRecord(r)) continue;
          const tr: TraineeResult = {};
          for (const k of ['score', 'markerMean', 'markerMin'] as const) if (typeof r[k] === 'number') tr[k] = r[k] as number;
          for (const k of ['passed', 'hardFailed'] as const) if (typeof r[k] === 'boolean') tr[k] = r[k] as boolean;
          for (const k of ['formId', 'remark'] as const) if (typeof r[k] === 'string') tr[k] = r[k] as string;
          if (r['formIncomplete'] === true) tr.formIncomplete = true;
          if (isTraineeVerdict(r['verdict'])) tr.verdict = r['verdict'];
          results[singleLine(id)] = tr;
        }
      }
      return {
        exerciseNo: line(e['exerciseNo']),
        scenarioId: line(e['scenarioId']),
        date: line(e['date']),
        hours: typeof e['hours'] === 'number' && Number.isFinite(e['hours']) ? e['hours'] : 0,
        recordKey: s(e['recordKey']),
        reportNo: line(e['reportNo']),
        instructor: line(e['instructor']),
        assessors: strList(e['assessors']),
        equipment: line(e['equipment']),
        attended: strList(e['attended']),
        completed: strList(e['completed']),
        results,
      };
    }),
    evaluation: s(obj['evaluation']),
  };
  return { session };
}

export function exportCourseSession(session: CourseSession, now = new Date()): string {
  return JSON.stringify({ ...session, exportedUtc: now.toISOString(), statement: INTERNAL_TRAINING_STATEMENT, totalHours: sessionHours(session) }, null, 2);
}

// ------------------------------------------------------------------ 時數

/** 預設練習時數:執行時間 + 0.75 h(簡報 15–20 min + 講評 30–40 min,第 8.1 節標準流程),進位到 0.25 h,至少 0.5 h。 */
export function defaultExerciseHours(duration_s: number): number {
  const h = Math.max(0, duration_s) / 3600 + 0.75;
  return Math.max(0.5, Math.ceil(h * 4 - 1e-9) / 4);
}

export function sessionHours(session: CourseSession): number {
  return round2(session.exercises.reduce((sum, e) => sum + (Number.isFinite(e.hours) ? e.hours : 0), 0));
}

function round2(v: number): number {
  return Math.round(v * 100) / 100;
}

/** 練習日期 YYYY-MM-DD(本地時間;與報告編號的日期相同)。 */
export function isoDate(iso: string | undefined): string {
  return localDate(iso);
}

// ------------------------------------------------------------------ 由講評報告加入班次

/** 評估表是否完整且合規(十五項皆評分、無驗證錯誤;第 8.2 節)。 */
export function formUsable(form: MarkerForm, duration_s?: number): boolean {
  return isFormComplete(form, validateMarkerForm(form, duration_s !== undefined ? { duration_s } : {}));
}

/** 加入班次前的提示:出席學員中主要評估員評估表未完成或不合規者(按鈕旁顯示)。 */
export function incompleteFormsForReport(session: CourseSession, report: DebriefReport, forms: readonly MarkerForm[]): MarkerForm[] {
  const ids = new Set(report.meta.participants.map((p) => p.traineeId).filter((id) => id && session.trainees.some((t) => t.id === id)));
  return formsOfRecord(forms, report.exercise.recordKey).filter((f) => f.assessor.role === 'primary' && ids.has(f.trainee.id) && !formUsable(f, report.exercise.duration_s));
}

/**
 * 由講評報告建立(或更新,依 recordKey)班次中的練習:出席 = 報告參與者中有學員 id 者;
 * 完成 = 出席且(核心 summary 已定稿 final,或該學員主要評估員的評估表完整且合規);
 * 結果 = 核心評分(團隊,輔助)+ 該學員主要評估員評估表的平均/最低分(評估表不完整時改標 formIncomplete,不寫分數);
 * 評估表只取 record.key 與報告紀錄鍵完全相同者。已有同一練習時,保留人工修改的時數、設備配置與評估員判定/備註。
 */
export function upsertExerciseFromReport(session: CourseSession, report: DebriefReport, forms: readonly MarkerForm[], options: { hours?: number; equipment?: string } = {}): SessionExercise {
  const attended = report.meta.participants.map((p) => p.traineeId).filter((id) => id && session.trainees.some((t) => t.id === id));
  const asm = report.summary?.assessment;
  const results: Record<string, TraineeResult> = {};
  const recordForms = formsOfRecord(forms, report.exercise.recordKey);
  const assessors = new Set<string>();
  for (const f of recordForms) if (f.assessor.name.trim()) assessors.add(f.assessor.name.trim());
  const old = session.exercises.find((e) => e.recordKey === report.exercise.recordKey);
  const summaryFinal = report.summary?.final === true;
  const completed: string[] = [];
  for (const id of attended) {
    const r: TraineeResult = {};
    if (asm?.score !== undefined) r.score = asm.score;
    if (asm?.passed !== undefined) r.passed = asm.passed;
    if (asm?.hardFailed !== undefined) r.hardFailed = asm.hardFailed;
    const form = recordForms.find((f) => f.trainee.id === id && f.assessor.role === 'primary');
    const usable = form ? formUsable(form, report.exercise.duration_s) : false;
    if (form) {
      r.formId = form.formId;
      if (usable) {
        const sum = summarizeForm(form);
        if (sum.mean !== null) r.markerMean = Math.round(sum.mean * 100) / 100;
        if (sum.min !== null) r.markerMin = sum.min;
      } else r.formIncomplete = true;
    }
    const prev = old?.results[id];
    if (prev?.verdict) r.verdict = prev.verdict;
    if (prev?.remark) r.remark = prev.remark;
    results[id] = r;
    if (summaryFinal || usable) completed.push(id);
  }
  const ex: SessionExercise = {
    exerciseNo: report.exercise.exerciseNo || exerciseNoFromScenario(report.exercise.scenarioId),
    scenarioId: report.exercise.scenarioId,
    date: isoDate(report.exercise.startedUtc ?? report.exercise.createdUtc),
    hours: options.hours ?? defaultExerciseHours(report.exercise.duration_s),
    recordKey: report.exercise.recordKey,
    reportNo: report.reportNo,
    instructor: report.meta.instructor,
    assessors: [...assessors],
    equipment: options.equipment ?? session.equipment,
    attended,
    completed,
    results,
  };
  const i = old ? session.exercises.indexOf(old) : -1;
  if (old && i >= 0) {
    // 保留人工修改的時數與設備配置(評估員判定/備註已在上面帶入)
    ex.hours = options.hours ?? old.hours;
    ex.equipment = options.equipment ?? old.equipment;
    session.exercises[i] = ex;
  } else session.exercises.push(ex);
  session.exercises.sort((a, b) => a.date.localeCompare(b.date) || a.exerciseNo.localeCompare(b.exerciseNo));
  if (ex.instructor && !session.instructors.includes(ex.instructor)) session.instructors.push(ex.instructor);
  for (const a of ex.assessors) if (!session.assessors.includes(a)) session.assessors.push(a);
  if (!session.shipId && report.exercise.shipId) session.shipId = report.exercise.shipId;
  if (ex.date && (!session.startDate || ex.date < session.startDate)) session.startDate = ex.date;
  if (ex.date && (!session.endDate || ex.date > session.endDate)) session.endDate = ex.date;
  return ex;
}

// ------------------------------------------------------------------ 個人訓練紀錄

export interface IndividualTrainingRecord {
  /** 紀錄編號 <班次編號>-<學員 ID> */
  recordNo: string;
  statement: string;
  trainee: Trainee;
  courseName: string;
  courseVersion: string;
  trainingForm: string;
  shipId: string;
  startDate: string;
  endDate: string;
  /** 本期課程總時數 */
  sessionHours: number;
  /** 該學員出席時數 */
  attendedHours: number;
  attendedExercises: string[];
  completedExercises: string[];
  instructors: string[];
  assessors: string[];
  results: Array<{ exerciseNo: string; scenarioId: string; date: string; hours: number; attended: boolean; completed: boolean; reportNo: string } & TraineeResult>;
  evaluation: string;
}

export function individualRecordNo(session: CourseSession, trainee: Trainee): string {
  return `${session.sessionId || 'SESSION'}-${trainee.id}`;
}

export function buildIndividualRecord(session: CourseSession, trainee: Trainee): IndividualTrainingRecord {
  const results = session.exercises.map((e) => ({
    exerciseNo: e.exerciseNo,
    scenarioId: e.scenarioId,
    date: e.date,
    hours: e.hours,
    attended: e.attended.includes(trainee.id),
    completed: e.completed.includes(trainee.id),
    reportNo: e.reportNo,
    ...(e.results[trainee.id] ?? {}),
  }));
  const attendedEx = session.exercises.filter((e) => e.attended.includes(trainee.id));
  const instructors = new Set<string>(session.instructors);
  const assessors = new Set<string>(session.assessors.filter(Boolean));
  for (const e of attendedEx) {
    if (e.instructor) instructors.add(e.instructor);
    for (const a of e.assessors) assessors.add(a);
  }
  return {
    recordNo: individualRecordNo(session, trainee),
    statement: INTERNAL_TRAINING_STATEMENT,
    trainee,
    courseName: session.courseName,
    courseVersion: session.courseVersion,
    trainingForm: session.trainingForm,
    shipId: session.shipId,
    startDate: session.startDate,
    endDate: session.endDate,
    sessionHours: sessionHours(session),
    attendedHours: round2(attendedEx.reduce((sum, e) => sum + e.hours, 0)),
    attendedExercises: attendedEx.map((e) => e.exerciseNo),
    completedExercises: session.exercises.filter((e) => e.completed.includes(trainee.id)).map((e) => e.exerciseNo),
    instructors: [...instructors].filter(Boolean),
    assessors: [...assessors],
    results,
    evaluation: session.evaluation,
  };
}

export function buildIndividualRecords(session: CourseSession): IndividualTrainingRecord[] {
  return session.trainees.map((t) => buildIndividualRecord(session, t));
}

// ------------------------------------------------------------------ CSV

/** 課程班次紀錄 CSV 欄位(一列 = 一位學員 × 一個練習;學員無練習時一列空白練習欄)。 */
export const SESSION_CSV_COLUMNS = [
  'sessionId',
  'courseName',
  'courseVersion',
  'trainingForm',
  'shipId',
  'startDate',
  'endDate',
  'sessionHours',
  'instructors',
  'assessors',
  'traineeId',
  'traineeName',
  'rank',
  'exerciseNo',
  'scenarioId',
  'exerciseDate',
  'exerciseHours',
  'exerciseInstructor',
  'exerciseAssessors',
  'equipment',
  'attended',
  'completed',
  'score',
  'passed',
  'hardFailed',
  'markerMean',
  'markerMin',
  'markerFormComplete',
  'verdict',
  'remark',
  'reportNo',
  'individualRecordNo',
  'evaluation',
] as const;

function yn(v: boolean | undefined): string {
  return v === undefined ? '' : v ? 'Y' : 'N';
}

/** 文字欄位(防公式注入後跳脫);數值與 Y/N 欄位由程式產生,直接跳脫。 */
function txt(v: string): string {
  return csvSafeText(v);
}

/**
 * 班次 CSV(UTF-8 BOM)。學員姓名、職級、教官、課後評估等文字欄位以 = + - @ Tab CR 開頭時前加單引號(防 Excel 公式注入);
 * markerFormComplete:有主要評估員評估表時 Y(完整)/N(未完成,不計分數);verdict:pass/fail(評估員判定)。
 */
export function sessionToCsv(session: CourseSession): string {
  const common = [txt(session.sessionId), txt(session.courseName), txt(session.courseVersion), txt(trainingFormLabel(session.trainingForm)), txt(session.shipId), txt(session.startDate), txt(session.endDate), String(sessionHours(session)), txt(session.instructors.join('; ')), txt(session.assessors.join('; '))];
  const rows: string[][] = [];
  for (const t of session.trainees) {
    const tcols = [txt(t.id), txt(t.name), txt(t.rank)];
    if (session.exercises.length === 0) {
      rows.push([...common, ...tcols, '', '', '', '', '', '', txt(session.equipment), '', '', '', '', '', '', '', '', '', '', '', txt(individualRecordNo(session, t)), txt(session.evaluation)]);
      continue;
    }
    for (const e of session.exercises) {
      const r = e.results[t.id] ?? {};
      rows.push([
        ...common,
        ...tcols,
        txt(e.exerciseNo),
        txt(e.scenarioId),
        txt(e.date),
        String(e.hours),
        txt(e.instructor),
        txt(e.assessors.join('; ')),
        txt(e.equipment),
        yn(e.attended.includes(t.id)),
        yn(e.completed.includes(t.id)),
        r.score === undefined ? '' : String(r.score),
        yn(r.passed),
        yn(r.hardFailed),
        r.markerMean === undefined ? '' : String(r.markerMean),
        r.markerMin === undefined ? '' : String(r.markerMin),
        r.formId === undefined ? '' : r.formIncomplete ? 'N' : 'Y',
        r.verdict ?? '',
        txt(r.remark ?? ''),
        txt(e.reportNo),
        txt(individualRecordNo(session, t)),
        txt(session.evaluation),
      ]);
    }
  }
  return '﻿' + [[...SESSION_CSV_COLUMNS], ...rows].map((r) => r.map(csvEscape).join(',')).join('\r\n') + '\r\n';
}

/** 班次紀錄的檢查(匯出前提示):缺班次編號、課程版本、無學員、練習無出席等。 */
export function validateCourseSession(session: CourseSession): string[] {
  const out: string[] = [];
  if (!session.sessionId.trim()) out.push('未填班次編號(個人訓練紀錄編號以此組成)');
  if (!session.courseVersion.trim()) out.push('未填課程版本');
  if (session.trainees.length === 0) out.push('名冊沒有學員');
  const ids = new Set<string>();
  for (const t of session.trainees) {
    if (ids.has(t.id)) out.push(`學員 ID 重複:${t.id}`);
    ids.add(t.id);
  }
  for (const e of session.exercises) {
    if (e.attended.length === 0) out.push(`${e.exerciseNo || e.scenarioId} 沒有出席學員`);
    if (!(e.hours > 0)) out.push(`${e.exerciseNo || e.scenarioId} 時數未填`);
  }
  return out;
}
