// 教官資格紀錄、學員證書效期雙軌追蹤與複訓排程(規劃書第 2.7 節 R9/R10、第 8.0 節定期複訓「每 5 年與法定 BRM 證書換證
// 同步」、第 8.3 節評估者校準、第 14 章「評估」列):
//  - 教官資格紀錄:姓名、STCW A-I/6 訓練師資格(課程/日期)、評估者資格(A-I/6 第 7–8 段;課程/日期)、該型模擬器實務經驗、
//    評估者校準紀錄(日期、基準錄影、一致率)、資格到期日;
//  - 學員證書效期雙軌追蹤:法定證書(STCW 證書類別、號碼、到期日)與公司 BRM 複訓到期日(上次完成日 + 5 年;
//    與換證同步時可手動對齊),依到期日排序並標示 90/180 天內到期;
//  - 複訓排程:由到期日產生建議季度排程(到期前 90 天,已逾期者排今日所在季度),匯出 CSV/JSON。
// 純函式與資料模型;畫面在 report-app.ts(?tab=admin),保存在 report-store.ts 的 IndexedDB admin 存放區;可匯入/匯出 JSON
// 跨機搬移;不連網。日期一律 YYYY-MM-DD(本地日曆日)。

import { csvEscape, csvSafeText } from '../lib/brm-markers.js';
import { singleLine, type Trainee } from './training-record.js';

export const ADMIN_RECORDS_FORMAT = 'simosa-brm-admin-records';
/** 公司 BRM 複訓週期(規劃書第 8.0 節、D19:每 5 年) */
export const DEFAULT_REFRESHER_INTERVAL_YEARS = 5;
/** 到期警示門檻(天) */
export const EXPIRY_WARN_DAYS = { soon: 90, upcoming: 180 } as const;
/** 建議複訓日期 = 到期日前 90 天(規劃書第 8.0 節:到期前一年內在船時排入;以季度列示) */
export const REFRESHER_LEAD_DAYS = 90;
/** 評估者校準一致率門檻(第 8.3 節:≥80% 要素在 ±1 分內) */
export const CALIBRATION_TARGET = 0.8;

export interface CalibrationRecord {
  date: string;
  /** 基準錄影代號/檔名(第 8.3 節:3 段基準錄影) */
  referenceVideo: string;
  /** 與標準評分 ±1 分內的要素比例(0–1);未計算時 null */
  agreementRate: number | null;
  note?: string;
}

export interface InstructorQualification {
  id: string;
  name: string;
  /** A-I/6 訓練師資格(示範課程 6.09/6.10 等):課程與完成日期 */
  trainerCourse: string;
  trainerDate: string;
  /** 評估者資格(A-I/6 第 7–8 段):課程與完成日期 */
  assessorCourse: string;
  assessorDate: string;
  /** 該型模擬器實務經驗(R10;例:監督下評估 2 次,2026-11) */
  simulatorExperience: string;
  calibrations: CalibrationRecord[];
  /** 資格到期日(公司規定;空白 = 不到期) */
  expiryDate: string;
  note: string;
}

export interface CertificateTrack {
  traineeId: string;
  name: string;
  rank: string;
  /** 法定證書:STCW 類別(例:A-II/1 BRM、領導統御與駕駛臺資源管理)、號碼、到期日 */
  statutoryType: string;
  statutoryNo: string;
  statutoryExpiry: string;
  /** 公司 BRM 複訓:上次完成日與到期日(= 上次完成 + 週期;對齊換證時 = 法定到期日) */
  lastCompletedDate: string;
  refresherDue: string;
  alignedWithStatutory: boolean;
  note: string;
}

export interface AdminRecords {
  format: typeof ADMIN_RECORDS_FORMAT;
  version: 1;
  refresherIntervalYears: number;
  instructors: InstructorQualification[];
  tracks: CertificateTrack[];
}

export function emptyAdminRecords(): AdminRecords {
  return { format: ADMIN_RECORDS_FORMAT, version: 1, refresherIntervalYears: DEFAULT_REFRESHER_INTERVAL_YEARS, instructors: [], tracks: [] };
}

export function emptyInstructor(id: string): InstructorQualification {
  return { id, name: '', trainerCourse: '', trainerDate: '', assessorCourse: '', assessorDate: '', simulatorExperience: '', calibrations: [], expiryDate: '', note: '' };
}

export function emptyTrack(traineeId: string): CertificateTrack {
  return { traineeId, name: '', rank: '', statutoryType: '', statutoryNo: '', statutoryExpiry: '', lastCompletedDate: '', refresherDue: '', alignedWithStatutory: false, note: '' };
}

// ------------------------------------------------------------------ 日期

const DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** YYYY-MM-DD → UTC 午夜的毫秒(避免時區影響天數計算);無效回傳 null。 */
export function parseDate(v: string): number | null {
  const m = DATE_RE.exec(v.trim());
  if (!m) return null;
  const y = Number(m[1]);
  const mo = Number(m[2]);
  const d = Number(m[3]);
  const ms = Date.UTC(y, mo - 1, d);
  const chk = new Date(ms);
  return chk.getUTCFullYear() === y && chk.getUTCMonth() === mo - 1 && chk.getUTCDate() === d ? ms : null;
}

export function formatDate(ms: number): string {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
}

/** 今天(本地日曆日)的 YYYY-MM-DD。 */
export function todayIso(now = new Date()): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

/** 加年(2 月 29 日 + N 年落在非閏年時取 2 月 28 日);無效日期回傳 ''。 */
export function addYears(date: string, years: number): string {
  const ms = parseDate(date);
  if (ms === null) return '';
  const d = new Date(ms);
  const y = d.getUTCFullYear() + years;
  const mo = d.getUTCMonth();
  const day = d.getUTCDate();
  const last = new Date(Date.UTC(y, mo + 1, 0)).getUTCDate();
  return formatDate(Date.UTC(y, mo, Math.min(day, last)));
}

export function addDays(date: string, days: number): string {
  const ms = parseDate(date);
  return ms === null ? '' : formatDate(ms + days * 86_400_000);
}

/** b − a 的天數(皆為 YYYY-MM-DD);任一無效回傳 null。 */
export function daysBetween(a: string, b: string): number | null {
  const ma = parseDate(a);
  const mb = parseDate(b);
  if (ma === null || mb === null) return null;
  return Math.round((mb - ma) / 86_400_000);
}

/** 公司複訓到期日 = 上次完成日 + 週期(年)。 */
export function refresherDueFrom(lastCompletedDate: string, years = DEFAULT_REFRESHER_INTERVAL_YEARS): string {
  return addYears(lastCompletedDate, years);
}

/** 到期狀態:none(無日期)| expired | soon(≤90 天)| upcoming(≤180 天)| ok。 */
export type ExpiryFlag = 'none' | 'expired' | 'soon' | 'upcoming' | 'ok';

export const EXPIRY_FLAG_LABEL: Readonly<Record<ExpiryFlag, string>> = { none: '未填', expired: '已逾期', soon: '90 天內到期', upcoming: '180 天內到期', ok: '有效' };

export function expiryFlag(date: string, today: string): ExpiryFlag {
  const days = daysBetween(today, date);
  if (days === null) return 'none';
  if (days < 0) return 'expired';
  if (days <= EXPIRY_WARN_DAYS.soon) return 'soon';
  if (days <= EXPIRY_WARN_DAYS.upcoming) return 'upcoming';
  return 'ok';
}

/** 旗標嚴重度(排序與彙總用;越小越急)。 */
const FLAG_ORDER: Readonly<Record<ExpiryFlag, number>> = { expired: 0, soon: 1, upcoming: 2, ok: 3, none: 4 };

export function worstFlag(a: ExpiryFlag, b: ExpiryFlag): ExpiryFlag {
  return FLAG_ORDER[a] <= FLAG_ORDER[b] ? a : b;
}

// ------------------------------------------------------------------ 雙軌追蹤

export interface DualTrackRow {
  track: CertificateTrack;
  statutoryDays: number | null;
  statutoryFlag: ExpiryFlag;
  companyDays: number | null;
  companyFlag: ExpiryFlag;
  /** 兩軌中較早的到期日(無則 '') */
  nextDue: string;
  nextDueKind: 'statutory' | 'company' | 'both' | 'none';
  flag: ExpiryFlag;
}

/** 填入公司複訓到期日(未對齊且有上次完成日時自動 = 上次完成 + 週期);回傳同一物件。 */
export function applyRefresherDue(track: CertificateTrack, years: number): CertificateTrack {
  if (track.alignedWithStatutory && track.statutoryExpiry) track.refresherDue = track.statutoryExpiry;
  else if (!track.alignedWithStatutory && track.lastCompletedDate && (!track.refresherDue || !parseDate(track.refresherDue))) track.refresherDue = refresherDueFrom(track.lastCompletedDate, years);
  return track;
}

/** 與法定證書換證同步:公司複訓到期日 = 法定證書到期日(規劃書第 8.0 節「與換證同步」)。 */
export function alignWithStatutory(track: CertificateTrack): CertificateTrack {
  track.alignedWithStatutory = true;
  if (track.statutoryExpiry) track.refresherDue = track.statutoryExpiry;
  return track;
}

/** 解除對齊:回到上次完成日 + 週期。 */
export function unalignFromStatutory(track: CertificateTrack, years: number): CertificateTrack {
  track.alignedWithStatutory = false;
  track.refresherDue = track.lastCompletedDate ? refresherDueFrom(track.lastCompletedDate, years) : '';
  return track;
}

/** 雙軌列(依較早到期日排序;無日期者排最後,同日依學員 ID)。 */
export function dualTrackRows(admin: AdminRecords, today: string): DualTrackRow[] {
  const rows = admin.tracks.map((track): DualTrackRow => {
    const statutoryDays = daysBetween(today, track.statutoryExpiry);
    const companyDays = daysBetween(today, track.refresherDue);
    const statutoryFlag = expiryFlag(track.statutoryExpiry, today);
    const companyFlag = expiryFlag(track.refresherDue, today);
    let nextDue = '';
    let nextDueKind: DualTrackRow['nextDueKind'] = 'none';
    if (statutoryDays !== null && companyDays !== null) {
      if (statutoryDays === companyDays) {
        nextDue = track.statutoryExpiry;
        nextDueKind = 'both';
      } else if (statutoryDays < companyDays) {
        nextDue = track.statutoryExpiry;
        nextDueKind = 'statutory';
      } else {
        nextDue = track.refresherDue;
        nextDueKind = 'company';
      }
    } else if (statutoryDays !== null) {
      nextDue = track.statutoryExpiry;
      nextDueKind = 'statutory';
    } else if (companyDays !== null) {
      nextDue = track.refresherDue;
      nextDueKind = 'company';
    }
    return { track, statutoryDays, statutoryFlag, companyDays, companyFlag, nextDue, nextDueKind, flag: worstFlag(statutoryFlag, companyFlag) };
  });
  return rows.sort((a, b) => {
    if (!a.nextDue && !b.nextDue) return a.track.traineeId.localeCompare(b.track.traineeId);
    if (!a.nextDue) return 1;
    if (!b.nextDue) return -1;
    return a.nextDue.localeCompare(b.nextDue) || a.track.traineeId.localeCompare(b.track.traineeId);
  });
}

// ------------------------------------------------------------------ 教官資格

export interface InstructorRow {
  instructor: InstructorQualification;
  expiryDays: number | null;
  flag: ExpiryFlag;
  /** 最近一次校準 */
  latestCalibration: CalibrationRecord | null;
  /** 最近一次校準一致率 ≥ 80%(第 8.3 節:方可獨立評分) */
  calibrated: boolean;
  /** 校準是否逾一年(第 8.3 節:每年再校準) */
  recalibrationDue: boolean;
}

export function latestCalibration(i: InstructorQualification): CalibrationRecord | null {
  const sorted = [...i.calibrations].filter((c) => parseDate(c.date) !== null).sort((a, b) => b.date.localeCompare(a.date));
  return sorted[0] ?? i.calibrations.at(-1) ?? null;
}

export function instructorRows(admin: AdminRecords, today: string): InstructorRow[] {
  return admin.instructors.map((instructor) => {
    const latest = latestCalibration(instructor);
    const sinceCal = latest ? daysBetween(latest.date, today) : null;
    return {
      instructor,
      expiryDays: daysBetween(today, instructor.expiryDate),
      flag: expiryFlag(instructor.expiryDate, today),
      latestCalibration: latest,
      calibrated: latest?.agreementRate !== null && latest?.agreementRate !== undefined && latest.agreementRate >= CALIBRATION_TARGET,
      recalibrationDue: latest === null || sinceCal === null || sinceCal > 365,
    };
  });
}

// ------------------------------------------------------------------ 複訓排程

export interface ScheduleItem {
  traineeId: string;
  name: string;
  rank: string;
  /** company 公司複訓 | statutory 法定換證 | both 同日(已對齊) */
  kind: 'company' | 'statutory' | 'both';
  dueDate: string;
  /** 建議完成日 = 到期前 90 天;已早於今日時 = 今日 */
  plannedDate: string;
  /** 建議季度 YYYY-Qn */
  quarter: string;
  overdue: boolean;
}

export interface ScheduleQuarter {
  quarter: string;
  items: ScheduleItem[];
}

export function quarterOf(date: string): string {
  const ms = parseDate(date);
  if (ms === null) return '';
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-Q${Math.floor(d.getUTCMonth() / 3) + 1}`;
}

/** 由雙軌到期日產生建議排程(每位學員每軌一項;對齊換證且同日時合為一項),依建議日期排序並依季度分組。 */
export function buildRefresherSchedule(admin: AdminRecords, today: string, leadDays = REFRESHER_LEAD_DAYS): ScheduleQuarter[] {
  const items: ScheduleItem[] = [];
  const push = (t: CertificateTrack, kind: ScheduleItem['kind'], dueDate: string) => {
    if (parseDate(dueDate) === null) return;
    let planned = addDays(dueDate, -leadDays);
    if (planned < today) planned = today;
    const overdue = dueDate < today;
    items.push({ traineeId: t.traineeId, name: t.name, rank: t.rank, kind, dueDate, plannedDate: planned, quarter: quarterOf(planned), overdue });
  };
  for (const t of admin.tracks) {
    const hasCompany = parseDate(t.refresherDue) !== null;
    const hasStatutory = parseDate(t.statutoryExpiry) !== null;
    if (hasCompany && hasStatutory && t.refresherDue === t.statutoryExpiry) push(t, 'both', t.refresherDue);
    else {
      if (hasCompany) push(t, 'company', t.refresherDue);
      if (hasStatutory) push(t, 'statutory', t.statutoryExpiry);
    }
  }
  items.sort((a, b) => a.plannedDate.localeCompare(b.plannedDate) || a.dueDate.localeCompare(b.dueDate) || a.traineeId.localeCompare(b.traineeId));
  const groups: ScheduleQuarter[] = [];
  for (const it of items) {
    let g = groups.find((x) => x.quarter === it.quarter);
    if (!g) {
      g = { quarter: it.quarter, items: [] };
      groups.push(g);
    }
    g.items.push(it);
  }
  return groups;
}

export const SCHEDULE_KIND_LABEL: Readonly<Record<ScheduleItem['kind'], string>> = { company: '公司 BRM 複訓', statutory: '法定證書換證', both: '複訓與換證同步' };

export const SCHEDULE_CSV_COLUMNS = ['quarter', 'plannedDate', 'dueDate', 'kind', 'traineeId', 'traineeName', 'rank', 'overdue'] as const;

/** 複訓排程 CSV(UTF-8 BOM;文字欄位防公式注入;一列 = 一項)。 */
export function scheduleToCsv(schedule: readonly ScheduleQuarter[]): string {
  const rows: string[][] = [];
  for (const g of schedule) for (const it of g.items) rows.push([csvSafeText(g.quarter), it.plannedDate, it.dueDate, it.kind, csvSafeText(it.traineeId), csvSafeText(it.name), csvSafeText(it.rank), it.overdue ? 'Y' : 'N']);
  return '﻿' + [[...SCHEDULE_CSV_COLUMNS], ...rows].map((r) => r.map(csvEscape).join(',')).join('\r\n') + '\r\n';
}

export function scheduleToJson(schedule: readonly ScheduleQuarter[], today: string, now = new Date()): string {
  return JSON.stringify({ format: 'simosa-brm-refresher-schedule', version: 1, generatedUtc: now.toISOString(), asOf: today, leadDays: REFRESHER_LEAD_DAYS, quarters: schedule }, null, 2);
}

export const INSTRUCTOR_CSV_COLUMNS = ['id', 'name', 'trainerCourse', 'trainerDate', 'assessorCourse', 'assessorDate', 'simulatorExperience', 'expiryDate', 'latestCalibrationDate', 'latestCalibrationVideo', 'latestAgreementRate', 'calibrationCount', 'note'] as const;

/** 教官資格紀錄 CSV(含最近一次校準;第 8.3 節校準紀錄可隨教官資格紀錄匯出)。 */
export function instructorsToCsv(admin: AdminRecords): string {
  const rows = admin.instructors.map((i) => {
    const c = latestCalibration(i);
    return [csvSafeText(i.id), csvSafeText(i.name), csvSafeText(i.trainerCourse), i.trainerDate, csvSafeText(i.assessorCourse), i.assessorDate, csvSafeText(i.simulatorExperience), i.expiryDate, c?.date ?? '', csvSafeText(c?.referenceVideo ?? ''), c?.agreementRate === null || c?.agreementRate === undefined ? '' : String(c.agreementRate), String(i.calibrations.length), csvSafeText(i.note)];
  });
  return '﻿' + [[...INSTRUCTOR_CSV_COLUMNS], ...rows].map((r) => r.map(csvEscape).join(',')).join('\r\n') + '\r\n';
}

export const TRACK_CSV_COLUMNS = ['traineeId', 'name', 'rank', 'statutoryType', 'statutoryNo', 'statutoryExpiry', 'statutoryFlag', 'lastCompletedDate', 'refresherDue', 'companyFlag', 'alignedWithStatutory', 'nextDue', 'nextDueKind', 'note'] as const;

export function tracksToCsv(admin: AdminRecords, today: string): string {
  const rows = dualTrackRows(admin, today).map((r) => {
    const t = r.track;
    return [csvSafeText(t.traineeId), csvSafeText(t.name), csvSafeText(t.rank), csvSafeText(t.statutoryType), csvSafeText(t.statutoryNo), t.statutoryExpiry, r.statutoryFlag, t.lastCompletedDate, t.refresherDue, r.companyFlag, t.alignedWithStatutory ? 'Y' : 'N', r.nextDue, r.nextDueKind, csvSafeText(t.note)];
  });
  return '﻿' + [[...TRACK_CSV_COLUMNS], ...rows].map((r) => r.map(csvEscape).join(',')).join('\r\n') + '\r\n';
}

// ------------------------------------------------------------------ 匯入/匯出與名冊同步

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}
function line(v: unknown): string {
  return typeof v === 'string' ? singleLine(v).trim() : typeof v === 'number' ? String(v) : '';
}
function dateField(v: unknown): string {
  const s = line(v);
  return parseDate(s) === null ? '' : s;
}

function normalizeCalibration(v: unknown): CalibrationRecord | null {
  if (!isRecord(v)) return null;
  const date = dateField(v['date']);
  const referenceVideo = line(v['referenceVideo']);
  if (!date && !referenceVideo) return null;
  let rate: number | null = null;
  if (typeof v['agreementRate'] === 'number' && Number.isFinite(v['agreementRate'])) {
    const r = v['agreementRate'];
    rate = r > 1 ? r / 100 : r; // 允許以百分比輸入
    rate = Math.min(1, Math.max(0, rate));
  }
  const out: CalibrationRecord = { date, referenceVideo, agreementRate: rate };
  if (line(v['note'])) out.note = line(v['note']);
  return out;
}

function normalizeInstructor(v: unknown, i: number): InstructorQualification | null {
  if (!isRecord(v)) return null;
  const name = line(v['name']);
  const id = line(v['id']) || (name ? `I${String(i + 1).padStart(3, '0')}` : '');
  if (!id && !name) return null;
  return {
    id,
    name,
    trainerCourse: line(v['trainerCourse']),
    trainerDate: dateField(v['trainerDate']),
    assessorCourse: line(v['assessorCourse']),
    assessorDate: dateField(v['assessorDate']),
    simulatorExperience: line(v['simulatorExperience']),
    calibrations: (Array.isArray(v['calibrations']) ? v['calibrations'] : []).map(normalizeCalibration).filter((c): c is CalibrationRecord => c !== null),
    expiryDate: dateField(v['expiryDate']),
    note: line(v['note']),
  };
}

function normalizeTrack(v: unknown, years: number): CertificateTrack | null {
  if (!isRecord(v)) return null;
  const traineeId = line(v['traineeId']);
  const name = line(v['name']);
  if (!traineeId && !name) return null;
  const t: CertificateTrack = {
    traineeId: traineeId || name,
    name,
    rank: line(v['rank']),
    statutoryType: line(v['statutoryType']),
    statutoryNo: line(v['statutoryNo']),
    statutoryExpiry: dateField(v['statutoryExpiry']),
    lastCompletedDate: dateField(v['lastCompletedDate']),
    refresherDue: dateField(v['refresherDue']),
    alignedWithStatutory: v['alignedWithStatutory'] === true,
    note: line(v['note']),
  };
  return applyRefresherDue(t, years);
}

export function normalizeAdminRecords(obj: unknown): { admin: AdminRecords | null; error?: string } {
  if (!isRecord(obj) || obj['format'] !== ADMIN_RECORDS_FORMAT) return { admin: null, error: '不是教官與證書紀錄(format 欄位不符)' };
  const years = typeof obj['refresherIntervalYears'] === 'number' && obj['refresherIntervalYears'] > 0 ? obj['refresherIntervalYears'] : DEFAULT_REFRESHER_INTERVAL_YEARS;
  const admin: AdminRecords = {
    format: ADMIN_RECORDS_FORMAT,
    version: 1,
    refresherIntervalYears: years,
    instructors: (Array.isArray(obj['instructors']) ? obj['instructors'] : []).map(normalizeInstructor).filter((i): i is InstructorQualification => i !== null),
    tracks: (Array.isArray(obj['tracks']) ? obj['tracks'] : []).map((t) => normalizeTrack(t, years)).filter((t): t is CertificateTrack => t !== null),
  };
  return { admin };
}

export function parseAdminRecords(text: string): { admin: AdminRecords | null; error?: string } {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return { admin: null, error: 'JSON 解析失敗' };
  }
  return normalizeAdminRecords(obj);
}

/** 匯出 JSON(含匯出時刻與以當日計算的雙軌狀態摘要,便於跨機搬移與稽核調閱)。 */
export function exportAdminRecords(admin: AdminRecords, today: string, now = new Date()): string {
  const rows = dualTrackRows(admin, today);
  const summary = { asOf: today, expired: rows.filter((r) => r.flag === 'expired').length, soon: rows.filter((r) => r.flag === 'soon').length, upcoming: rows.filter((r) => r.flag === 'upcoming').length };
  return JSON.stringify({ ...admin, exportedUtc: now.toISOString(), summary }, null, 2);
}

/**
 * 以學員名冊同步追蹤清單:名冊有而清單無的學員新增一列(法定到期日取名冊 certificateExpiry);已有者更新姓名/職級,
 * 名冊有到期日而清單空白時補上。回傳新增筆數。
 */
export function syncTracksWithRoster(admin: AdminRecords, trainees: readonly Trainee[]): number {
  let added = 0;
  for (const t of trainees) {
    let track = admin.tracks.find((x) => x.traineeId === t.id);
    if (!track) {
      track = emptyTrack(t.id);
      admin.tracks.push(track);
      added++;
    }
    track.name = t.name;
    track.rank = t.rank;
    if (!track.statutoryExpiry && t.certificateExpiry && parseDate(t.certificateExpiry) !== null) track.statutoryExpiry = t.certificateExpiry;
    applyRefresherDue(track, admin.refresherIntervalYears);
  }
  return added;
}

/** 檢查(匯出前提示):重複 ID、缺姓名、一致率超出 0–1、日期格式。 */
export function validateAdminRecords(admin: AdminRecords): string[] {
  const out: string[] = [];
  const ids = new Set<string>();
  for (const i of admin.instructors) {
    if (!i.name.trim()) out.push(`教官 ${i.id} 未填姓名`);
    if (ids.has(i.id)) out.push(`教官 ID 重複:${i.id}`);
    ids.add(i.id);
    for (const c of i.calibrations) {
      if (c.agreementRate !== null && (c.agreementRate < 0 || c.agreementRate > 1)) out.push(`${i.name || i.id} 校準 ${c.date} 一致率超出 0–100%`);
      if (c.date && parseDate(c.date) === null) out.push(`${i.name || i.id} 校準日期格式錯誤:${c.date}`);
    }
    for (const [k, v] of [['訓練師資格日期', i.trainerDate], ['評估者資格日期', i.assessorDate], ['資格到期日', i.expiryDate]] as const) if (v && parseDate(v) === null) out.push(`${i.name || i.id} ${k}格式錯誤:${v}`);
  }
  const tids = new Set<string>();
  for (const t of admin.tracks) {
    if (tids.has(t.traineeId)) out.push(`學員 ID 重複:${t.traineeId}`);
    tids.add(t.traineeId);
    for (const [k, v] of [['法定證書到期日', t.statutoryExpiry], ['上次完成日', t.lastCompletedDate], ['公司複訓到期日', t.refresherDue]] as const) if (v && parseDate(v) === null) out.push(`${t.name || t.traineeId} ${k}格式錯誤:${v}`);
  }
  return out;
}
