// 參與者與休息時間確認(規劃書第 5.5 節「休息時間相容」:練習啟動畫面要求帶領者確認已對照船上工時/休息時間紀錄核對,
// 確認紀錄隨紀錄包回傳;第 2.7 節 R9/R10、docs/design/packaging-sync.md:紀錄標頭 participants / restHoursConfirmation)。
// 本檔只有資料模型、驗證、正規化與讀回(純函式);sessionInfo 指令組裝在 instructor-command.ts,畫面在
// components/instructor/session-info-panel.ts。教官站只負責送出與本地保存;核心依同一契約寫入紀錄標頭。

/** 參與者職務(sessionInfo 契約的固定列舉)。 */
export const SESSION_PARTICIPANT_ROLES = ['master', 'oow', 'pilot', 'helmsman', 'lookout', 'observer', 'instructor'] as const;
export type SessionParticipantRole = (typeof SESSION_PARTICIPANT_ROLES)[number];

export const SESSION_ROLE_LABEL: Readonly<Record<SessionParticipantRole, string>> = {
  master: '船長',
  oow: '當值船副',
  pilot: '引水人(扮演)',
  helmsman: '舵工',
  lookout: '瞭望',
  observer: '觀察員',
  instructor: '教官/帶領者',
};

export function isSessionRole(v: unknown): v is SessionParticipantRole {
  return typeof v === 'string' && (SESSION_PARTICIPANT_ROLES as readonly string[]).includes(v);
}

export function sessionRoleLabel(role: string): string {
  return isSessionRole(role) ? SESSION_ROLE_LABEL[role] : role;
}

export interface SessionParticipant {
  name: string;
  /** master | oow | pilot | helmsman | lookout | observer | instructor */
  role: string;
  /** 職級(船長、大副、二副…;選填) */
  rank?: string;
  /** 證書號(選填;報告頁以此對應學員證書效期追蹤) */
  certificateNo?: string;
}

export interface RestHoursConfirmation {
  /** 確認人(船長/帶領者) */
  confirmedBy: string;
  /** ISO-8601;取核心 state.utc,無則瀏覽器 UTC */
  confirmedAtUtc: string;
  /** 是否符合 STCW A-VIII/1(MLC 2006 規則 2.3)與公司規定 */
  compliant: boolean;
  note?: string;
}

export interface SessionCourseInfo {
  id?: string;
  title?: string;
  instructor?: string;
}

/** sessionInfo 指令 args 與紀錄標頭的形狀。讀回舊核心紀錄時 restHoursConfirmation 可能缺(只寫了 participants)。 */
export interface SessionInfo {
  participants: SessionParticipant[];
  restHoursConfirmation?: RestHoursConfirmation;
  course?: SessionCourseInfo;
}

/** 教官站表單草稿(尚未正規化;confirmedAtUtc 在送出時才取)。 */
export interface SessionInfoDraft {
  participants: SessionParticipant[];
  restHours: { confirmedBy: string; compliant: boolean; note: string };
  course: { id: string; title: string; instructor: string };
}

export function emptySessionInfoDraft(): SessionInfoDraft {
  return { participants: [], restHours: { confirmedBy: '', compliant: true, note: '' }, course: { id: '', title: '', instructor: '' } };
}

/** 單行欄位:控制字元換成空白並去頭尾空白(姓名、證書號等進紀錄標頭與 CSV)。 */
function clean(v: unknown): string {
  return typeof v === 'string' ? v.replace(/[\u0000-\u001f\u007f]/g, ' ').trim() : '';
}

/**
 * 確認時刻:核心 state.utc(模擬時鐘的 UTC;情境可設定起始時刻)可解析時用它,否則瀏覽器 UTC;一律輸出 ISO-8601(毫秒 Z)。
 * 兩者都放進備註由讀者判斷不切實際,所以以核心時鐘優先(與紀錄內其他時刻一致)。
 */
export function confirmationTimeUtc(stateUtc: string | undefined, now = new Date()): string {
  if (stateUtc) {
    const d = new Date(stateUtc);
    if (!Number.isNaN(d.getTime())) return d.toISOString();
  }
  return now.toISOString();
}

/** 整列空白的參與者(職務選單永遠有預設值,所以只看姓名/職級/證書號)不計入。 */
function filledParticipants(draft: SessionInfoDraft): SessionParticipant[] {
  return draft.participants.filter((p) => clean(p.name) || clean(p.rank) || clean(p.certificateNo));
}

/** 驗證草稿(送出前的錯誤清單;空陣列 = 可送出)。 */
export function validateSessionInfoDraft(draft: SessionInfoDraft): string[] {
  const out: string[] = [];
  if (filledParticipants(draft).length === 0) out.push('至少填一位參與者');
  // 編號依表格列(含略過的空白列),方便對照畫面
  draft.participants.forEach((p, i) => {
    if (!(clean(p.name) || clean(p.rank) || clean(p.certificateNo))) return;
    if (!clean(p.name)) out.push(`參與者 ${i + 1} 未填姓名`);
    if (!isSessionRole(p.role)) out.push(`參與者 ${i + 1}(${clean(p.name) || '—'})職務不在列舉內:${SESSION_PARTICIPANT_ROLES.join('|')}`);
  });
  if (!clean(draft.restHours.confirmedBy)) out.push('未填休息時間確認人');
  return out;
}

/**
 * 由草稿組成 sessionInfo(正規化:去空白、略過整列空白的參與者、選填欄位空白時不放);
 * 草稿無效時丟 RangeError(畫面先以 validateSessionInfoDraft 提示)。
 */
export function buildSessionInfo(draft: SessionInfoDraft, confirmedAtUtc: string): SessionInfo {
  const issues = validateSessionInfoDraft(draft);
  if (issues.length > 0) throw new RangeError(issues.join(';'));
  const participants: SessionParticipant[] = filledParticipants(draft).map((p) => {
      const out: SessionParticipant = { name: clean(p.name), role: clean(p.role) };
      if (clean(p.rank)) out.rank = clean(p.rank);
      if (clean(p.certificateNo)) out.certificateNo = clean(p.certificateNo);
      return out;
    });
  const rest: RestHoursConfirmation = { confirmedBy: clean(draft.restHours.confirmedBy), confirmedAtUtc, compliant: draft.restHours.compliant === true };
  if (clean(draft.restHours.note)) rest.note = clean(draft.restHours.note);
  const info: SessionInfo = { participants, restHoursConfirmation: rest };
  const course: SessionCourseInfo = {};
  if (clean(draft.course.id)) course.id = clean(draft.course.id);
  if (clean(draft.course.title)) course.title = clean(draft.course.title);
  if (clean(draft.course.instructor)) course.instructor = clean(draft.course.instructor);
  if (Object.keys(course).length > 0) info.course = course;
  return info;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

export function parseRestHoursConfirmation(raw: unknown): RestHoursConfirmation | null {
  if (!isRecord(raw)) return null;
  const confirmedBy = clean(raw['confirmedBy']);
  // 舊建議欄名 confirmedUtc(packaging-sync.md)也接受
  const at = clean(raw['confirmedAtUtc']) || clean(raw['confirmedUtc']);
  if (!confirmedBy && !at) return null;
  const out: RestHoursConfirmation = { confirmedBy, confirmedAtUtc: at, compliant: raw['compliant'] === true };
  if (clean(raw['note'])) out.note = clean(raw['note']);
  return out;
}

/**
 * 讀回紀錄標頭/指令 args 的 sessionInfo(教官站紀錄 record.sessionInfo,或核心紀錄 header 的 participants /
 * restHoursConfirmation / course):格式不符的參與者略過;字串陣列的 participants(packaging-sync.md 允許)視為只有姓名。
 * 既無參與者也無確認時回傳 null。
 */
export function parseSessionInfo(raw: unknown): SessionInfo | null {
  if (!isRecord(raw)) return null;
  const participants: SessionParticipant[] = [];
  for (const p of Array.isArray(raw['participants']) ? raw['participants'] : []) {
    if (typeof p === 'string') {
      if (clean(p)) participants.push({ name: clean(p), role: 'observer' });
      continue;
    }
    if (!isRecord(p)) continue;
    const name = clean(p['name']);
    if (!name) continue;
    const out: SessionParticipant = { name, role: clean(p['role']).toLowerCase() || 'observer' };
    if (clean(p['rank'])) out.rank = clean(p['rank']);
    if (clean(p['certificateNo'])) out.certificateNo = clean(p['certificateNo']);
    participants.push(out);
  }
  const rest = parseRestHoursConfirmation(raw['restHoursConfirmation']);
  if (participants.length === 0 && !rest) return null;
  const info: SessionInfo = { participants };
  if (rest) info.restHoursConfirmation = rest;
  if (isRecord(raw['course'])) {
    const c = raw['course'];
    const course: SessionCourseInfo = {};
    if (clean(c['id'])) course.id = clean(c['id']);
    if (clean(c['title'])) course.title = clean(c['title']);
    if (clean(c['instructor'])) course.instructor = clean(c['instructor']);
    if (Object.keys(course).length > 0) info.course = course;
  }
  return info;
}

/** 休息時間確認的一行摘要(教官站狀態列、報告)。 */
export function restHoursSummary(rest: RestHoursConfirmation | undefined): string {
  if (!rest) return '未確認休息時間';
  return `${rest.confirmedBy || '—'} 於 ${rest.confirmedAtUtc || '—'} 確認:${rest.compliant ? '符合' : '不符合'} STCW A-VIII/1 與公司規定${rest.note ? `(${rest.note})` : ''}`;
}
