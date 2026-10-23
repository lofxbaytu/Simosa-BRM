// 講評報告頁 <brm-report-app>(report.html;規劃書第 8.2、8.4、9.1、9.2 節):
//  - 講評報告:開啟核心 JSON Lines 紀錄或教官站紀錄(教官站「講評報告」按鈕經 IndexedDB 交接),填練習資訊、參與者與第 8.4 節講評五段;
//  - 行為標記評估表:十五要素 1–5 分、錨定描述、觀察證據(可帶入 BRM 快速標記)、SIRE 2.0 缺失分類、雙評一致率;
//  - 個人訓練紀錄與課程班次紀錄:名冊、班次、練習出席,列印每位學員一份;班次匯出 CSV/JSON;
//  - 教官與證書(?tab=admin):教官資格紀錄(A-I/6、評估者校準)、學員證書效期雙軌追蹤、複訓排程(admin-records.ts;
//    IndexedDB admin 存放區,JSON 匯入/匯出跨機搬移)。
// 列印:瀏覽器「列印 → 另存為 PDF」(A4,@media print 只印 .sheet);資料只在本機 IndexedDB 與匯出檔,不連網。
// 使用 light DOM(createRenderRoot 回傳 this),讓 report.css 的列印樣式直接套用整頁。

import { LitElement, html, nothing, type TemplateResult } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { isBrmCategory, type BrmMarker } from '../lib/brm-markers.js';
import { downloadText, fileStamp } from '../lib/download.js';
import { formatSimTime } from '../lib/format.js';
import { parseReplay, type ReplayData } from '../lib/record-replay.js';
import { applyTheme, isTheme } from '../lib/theme.js';
import {
  ADMIN_RECORDS_FORMAT,
  addYears,
  alignWithStatutory,
  buildRefresherSchedule,
  dualTrackRows,
  emptyAdminRecords,
  emptyInstructor,
  emptyTrack,
  exportAdminRecords,
  EXPIRY_FLAG_LABEL,
  instructorRows,
  instructorsToCsv,
  normalizeAdminRecords,
  parseDate,
  refresherDueFrom,
  scheduleToCsv,
  scheduleToJson,
  syncTracksWithRoster,
  todayIso,
  tracksToCsv,
  unalignFromStatutory,
  validateAdminRecords,
  type AdminRecords,
  type CertificateTrack,
  type InstructorQualification,
} from './admin-records.js';
import { ANCHORS, allElements, type DeficiencyType, type HumanFactor } from './anchors.js';
import { applySessionInfoToMeta, buildDebriefReport, emptyReportMeta, PARTICIPANT_ROLES, recordKey, stcwText, type DebriefReport, type Participant, type ReportMeta } from './debrief-report.js';
import { attachMarker, computeAgreement, createMarkerForm, elementsForMarker, exportForms, importForms, isFormComplete, parseSimTimeInput, ratingOf, summarizeForm, validateMarkerForm, type ElementRating, type FormIssue, type MarkerForm } from './marker-form.js';
import { ADMIN_KEY, ADMIN_STORE, HANDOFF_KEY, LAST_RECORD_KEY, openReportStore, takeHandoff, WORKSPACE_KEY, type KvStore } from './report-store.js';
import { renderAdminDoc, renderAgreement, renderFormDoc, renderIndividualDoc, renderReportDoc, renderSessionDoc, tStr } from './report-views.js';
import {
  buildIndividualRecords,
  COURSE_SESSION_FORMAT,
  emptyCourseSession,
  exportCourseSession,
  exportRoster,
  incompleteFormsForReport,
  isTraineeVerdict,
  normalizeCourseSession,
  parseRoster,
  ROSTER_FORMAT,
  sessionToCsv,
  TRAINING_FORMS,
  upsertExerciseFromReport,
  validateCourseSession,
  VERDICT_LABEL,
  type CourseSession,
  type Roster,
  type Trainee,
} from './training-record.js';

export type ReportTab = 'report' | 'form' | 'individual' | 'session' | 'admin';

const TAB_LABEL: Record<ReportTab, string> = { report: '講評報告', form: '行為標記評估表', individual: '個人訓練紀錄', session: '課程班次紀錄', admin: '教官與證書' };

export function isReportTab(v: unknown): v is ReportTab {
  return typeof v === 'string' && Object.hasOwn(TAB_LABEL, v);
}

export interface ReportWorkspace {
  version: 1;
  /** 各練習紀錄的報告人工欄位(recordKey → meta) */
  metas: Record<string, ReportMeta>;
  forms: MarkerForm[];
  session: CourseSession;
}

export function emptyWorkspace(): ReportWorkspace {
  return { version: 1, metas: {}, forms: [], session: emptyCourseSession() };
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/** 讀回 IndexedDB 的工作資料(欄位不全時補預設)。 */
export function restoreWorkspace(raw: unknown): ReportWorkspace {
  const ws = emptyWorkspace();
  if (!isRecord(raw)) return ws;
  if (isRecord(raw['metas'])) {
    for (const [k, v] of Object.entries(raw['metas'])) {
      if (!isRecord(v)) continue;
      const base = emptyReportMeta();
      ws.metas[k] = { ...base, ...(v as Partial<ReportMeta>), debrief: { ...base.debrief, ...(isRecord(v['debrief']) ? (v['debrief'] as Partial<ReportMeta['debrief']>) : {}) }, participants: Array.isArray(v['participants']) ? (v['participants'] as Participant[]) : [] };
    }
  }
  if (Array.isArray(raw['forms'])) ws.forms = importForms(JSON.stringify({ format: 'simosa-brm-marker-forms', forms: raw['forms'] })).forms;
  const sess = normalizeCourseSession(raw['session']).session;
  if (sess) ws.session = sess;
  return ws;
}

/** 教官站「匯出 JSON」的 BRM 標記檔(simosa-brm-markers)→ 標記(保留原 id,與教官站紀錄內的標記去重)。 */
export function parseMarkerExport(obj: unknown): BrmMarker[] {
  const list = isRecord(obj) && Array.isArray(obj['markers']) ? obj['markers'] : Array.isArray(obj) ? obj : [];
  const out: BrmMarker[] = [];
  list.forEach((raw: unknown, i: number) => {
    if (!isRecord(raw) || !isBrmCategory(raw['category']) || typeof raw['t'] !== 'number') return;
    const m: BrmMarker = { id: typeof raw['id'] === 'number' ? raw['id'] : i + 1, category: raw['category'], t: raw['t'], wall: typeof raw['wall'] === 'string' ? raw['wall'] : '', note: typeof raw['note'] === 'string' ? raw['note'].trim() : '' };
    if (typeof raw['tick'] === 'number') m.tick = raw['tick'];
    if (raw['polarity'] === 'plus' || raw['polarity'] === 'delta') m.polarity = raw['polarity'];
    if (typeof raw['scenarioId'] === 'string') m.scenarioId = raw['scenarioId'];
    out.push(m);
  });
  return out.sort((a, b) => a.t - b.t);
}

/**
 * CSS 字串內容跳脫(@page 邊界方塊的 content):CSS 把 \n、\r、\f 都當換行,未跳脫會提前結束字串、讓後面的文字變成新規則;
 * 因此所有控制字元(U+0000–U+001F、U+007F)換成空白,反斜線與雙引號以反斜線跳脫。
 */
export function cssString(text: string): string {
  return text
    .replace(/[\u0000-\u001f\u007f]/g, ' ')
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"');
}

function splitList(text: string): string[] {
  return text
    .split(/[,、;;\n]/)
    .map((s) => s.trim())
    .filter(Boolean);
}

function val(e: Event): string {
  return (e.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
}

@customElement('brm-report-app')
export class ReportApp extends LitElement {
  @state() tab: ReportTab = 'report';
  @state() data: ReplayData | null = null;
  @state() recordName = '';
  @state() extraMarkers: BrmMarker[] = [];
  @state() ws: ReportWorkspace = emptyWorkspace();
  @state() activeFormId = '';
  @state() message = '';
  @state() messageLevel: 'info' | 'error' = 'info';
  @state() persistent = true;
  @state() individualTrainee = '';
  @state() ready = false;
  /** 教官資格紀錄與證書效期追蹤(IndexedDB admin 存放區;與報告工作資料分開保存) */
  @state() admin: AdminRecords = emptyAdminRecords();
  /** 到期狀態的計算日(預設今天;可改以模擬未來日期檢視) */
  @state() asOf = todayIso();
  /** 最近一次開啟檔案(可多選)時無法處理的檔案與原因 */
  fileErrors: string[] = [];

  private store: KvStore | null = null;
  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  private adminSaveTimer: ReturnType<typeof setTimeout> | null = null;
  private pageStyle: HTMLStyleElement | null = null;

  protected override createRenderRoot(): HTMLElement {
    return this;
  }

  override connectedCallback(): void {
    super.connectedCallback();
    const params = new URLSearchParams(location.search);
    // 報告是紙本文件:預設日間主題(不改儀器頁/教官站記住的主題),可用 ?theme= 覆寫
    const theme = params.get('theme');
    applyTheme(isTheme(theme) ? theme : 'day', false);
    const tab = params.get('tab');
    if (isReportTab(tab)) this.tab = tab;
    void this.init(params.get('from') === 'instructor');
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.saveTimer) clearTimeout(this.saveTimer);
    if (this.adminSaveTimer) clearTimeout(this.adminSaveTimer);
    this.pageStyle?.remove();
    this.pageStyle = null;
  }

  /** 開啟本機儲存、還原工作資料與最近的紀錄;from=instructor 時讀教官站交接的紀錄。 */
  async init(fromInstructor: boolean): Promise<void> {
    try {
      this.store = await openReportStore();
      this.persistent = this.store.persistent;
      this.ws = restoreWorkspace(await this.store.get(WORKSPACE_KEY));
      const adminRaw = await this.store.get(ADMIN_KEY, ADMIN_STORE);
      const adminRes = adminRaw === undefined ? null : normalizeAdminRecords(adminRaw);
      if (adminRes?.admin) this.admin = adminRes.admin;
      const handoff = fromInstructor ? await takeHandoff(this.store) : undefined;
      if (handoff) {
        this.loadRecordText(handoff.text, `教官站紀錄(${handoff.savedUtc})`);
        await this.store.delete(HANDOFF_KEY);
      } else {
        const last = await this.store.get<{ name: string; text: string }>(LAST_RECORD_KEY);
        if (last?.text) this.loadRecordText(last.text, last.name, false);
        if (fromInstructor) this.say('沒有找到教官站交接的紀錄,請以「開啟檔案」載入', 'error');
      }
    } catch (err) {
      this.persistent = false;
      this.say(`本機儲存無法使用:${String(err)}`, 'error');
    }
    this.ready = true;
  }

  private say(text: string, level: 'info' | 'error' = 'info'): void {
    this.message = text;
    this.messageLevel = level;
  }

  // ------------------------------------------------------------ 儲存
  private touch(): void {
    this.ws = { ...this.ws };
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => void this.saveNow(), 300);
  }

  async saveNow(): Promise<void> {
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.saveTimer = null;
    try {
      await this.store?.set(WORKSPACE_KEY, JSON.parse(JSON.stringify(this.ws)));
    } catch (err) {
      this.say(`儲存失敗:${String(err)}`, 'error');
    }
  }

  // ------------------------------------------------------------ 載入
  get key(): string {
    return this.data ? recordKey(this.data) : '';
  }

  get meta(): ReportMeta {
    const k = this.key;
    let m = this.ws.metas[k];
    if (!m) {
      m = emptyReportMeta();
      const s = this.ws.session;
      if (s.courseName) m.courseName = s.courseName;
      m.courseVersion = s.courseVersion;
      m.sessionId = s.sessionId;
      m.instructor = s.instructors[0] ?? '';
      // 教官站於練習前登錄的參與者與課程(紀錄標頭 sessionInfo)帶入新報告的參與者/教官/班次欄(規劃書第 5.5 節)
      applySessionInfoToMeta(m, this.data?.header.sessionInfo, s.trainees);
      this.ws.metas[k] = m;
    }
    return m;
  }

  get report(): DebriefReport | null {
    if (!this.data) return null;
    return buildDebriefReport({ data: this.data, meta: this.meta, forms: this.ws.forms, extraMarkers: this.extraMarkers });
  }

  get markers(): BrmMarker[] {
    if (!this.data) return [...this.extraMarkers];
    const out = [...this.data.markers];
    for (const m of this.extraMarkers) if (!out.some((x) => x.id === m.id && x.t === m.t)) out.push(m);
    return out.sort((a, b) => a.t - b.t);
  }

  /** 載入練習紀錄文字(核心 JSON Lines 或教官站 JSON)。 */
  loadRecordText(text: string, name: string, remember = true): boolean {
    const data = parseReplay(text);
    if (data.states.length === 0 && !data.summary && data.events.length === 0) {
      this.say(`無法載入紀錄「${name}」:${data.errors[0] ?? '紀錄沒有狀態或 summary'}`, 'error');
      return false;
    }
    this.data = data;
    this.recordName = name;
    void this.meta; // 建立預設 meta
    this.activeFormId = this.formsForRecord()[0]?.formId ?? '';
    this.say(`已載入 ${name}:${data.header.scenarioId ?? ''} 狀態 ${data.states.length} 筆、事件 ${data.events.length} 筆${data.summary ? '、核心 summary' : ''}${data.errors.length ? `(略過 ${data.errors.length} 行)` : ''}`);
    if (remember) void this.store?.set(LAST_RECORD_KEY, { name, text });
    this.touch();
    return true;
  }

  /** 依內容判斷檔案種類:評估表、BRM 標記、班次紀錄、名冊,其餘當作練習紀錄。 */
  async openFile(file: File): Promise<void> {
    const text = await file.text();
    this.importText(text, file.name);
  }

  /** 依序開啟多個檔案;某個檔案讀取或解析失敗時記下錯誤並繼續處理其餘檔案,最後彙總顯示。 */
  async openFiles(files: readonly File[]): Promise<void> {
    const errors: string[] = [];
    for (const f of files) {
      try {
        await this.openFile(f);
        if (this.messageLevel === 'error' && this.message.includes(f.name)) errors.push(this.message);
      } catch (err) {
        errors.push(`無法載入「${f.name}」:${String(err)}`);
      }
    }
    this.fileErrors = errors;
    if (errors.length > 0) this.say(errors.join(';'), 'error');
  }

  /** 依內容匯入一份文字;任何例外都轉成畫面上的錯誤訊息(損壞或惡意檔案不丟未處理例外)。 */
  importText(text: string, name: string): void {
    try {
      this.importTextUnsafe(text, name);
    } catch (err) {
      this.say(`無法載入「${name}」:${String(err)}`, 'error');
    }
  }

  private importTextUnsafe(text: string, name: string): void {
    let obj: unknown = undefined;
    try {
      obj = JSON.parse(text);
    } catch {
      /* JSON Lines */
    }
    const fmt = isRecord(obj) ? obj['format'] : undefined;
    if (fmt === 'simosa-brm-marker-form' || fmt === 'simosa-brm-marker-forms') {
      const res = importForms(text);
      for (const f of res.forms) {
        const i = this.ws.forms.findIndex((x) => x.formId === f.formId);
        if (i >= 0) this.ws.forms[i] = f;
        else this.ws.forms.push(f);
      }
      if (res.forms[0]) this.activeFormId = res.forms.find((f) => f.assessor.role === 'primary')?.formId ?? res.forms[0].formId;
      const unbound = res.forms.filter((f) => !f.record.key).length;
      this.say(
        `已匯入評估表 ${res.forms.length} 份${res.skipped ? `(略過 ${res.skipped})` : ''}:${res.forms.map((f) => `${f.trainee.name || '—'}/${f.assessor.name || '—'}(${f.assessor.role === 'primary' ? '主要' : '第二'})`).join('、')}${unbound ? `;其中 ${unbound} 份沒有練習紀錄鍵,請在評估表分頁綁定到目前紀錄` : ''}`,
      );
      this.touch();
      return;
    }
    if (fmt === 'simosa-brm-markers') {
      this.extraMarkers = parseMarkerExport(obj);
      this.say(`已載入 BRM 快速標記 ${this.extraMarkers.length} 筆(可在評估表帶入為觀察證據)`);
      return;
    }
    if (fmt === COURSE_SESSION_FORMAT) {
      const res = normalizeCourseSession(obj);
      if (res.session) {
        this.ws.session = res.session;
        this.say(`已匯入課程班次紀錄 ${res.session.sessionId}(學員 ${res.session.trainees.length}、練習 ${res.session.exercises.length})`);
        this.touch();
      } else this.say(res.error ?? '匯入失敗', 'error');
      return;
    }
    if (fmt === ROSTER_FORMAT) {
      const res = parseRoster(text);
      if (res.roster) {
        this.mergeRoster(res.roster);
        this.say(`已匯入學員名冊:學員 ${res.roster.trainees.length}、教官/評估員 ${res.roster.staff.length}`);
      } else this.say(res.error ?? '匯入失敗', 'error');
      return;
    }
    if (fmt === ADMIN_RECORDS_FORMAT) {
      const res = normalizeAdminRecords(obj);
      if (res.admin) {
        this.admin = res.admin;
        this.say(`已匯入教官與證書紀錄:教官 ${res.admin.instructors.length}、學員 ${res.admin.tracks.length}(取代本機既有資料)`);
        this.touchAdmin();
      } else this.say(res.error ?? '匯入失敗', 'error');
      return;
    }
    this.loadRecordText(text, name);
  }

  // ------------------------------------------------------------ 教官與證書(admin 存放區)
  private touchAdmin(): void {
    this.admin = { ...this.admin };
    if (this.adminSaveTimer) clearTimeout(this.adminSaveTimer);
    this.adminSaveTimer = setTimeout(() => void this.saveAdminNow(), 300);
  }

  async saveAdminNow(): Promise<void> {
    if (this.adminSaveTimer) clearTimeout(this.adminSaveTimer);
    this.adminSaveTimer = null;
    try {
      await this.store?.set(ADMIN_KEY, JSON.parse(JSON.stringify(this.admin)), ADMIN_STORE);
    } catch (err) {
      this.say(`教官與證書紀錄儲存失敗:${String(err)}`, 'error');
    }
  }

  /** 由學員名冊同步追蹤清單(新增缺少的學員、更新姓名/職級、補法定到期日)。 */
  syncAdminWithRoster(): number {
    const added = syncTracksWithRoster(this.admin, this.ws.session.trainees);
    this.say(`已由名冊同步:新增 ${added} 位學員(共 ${this.admin.tracks.length} 位)`);
    this.touchAdmin();
    return added;
  }

  addInstructor(): InstructorQualification {
    const id = `I${String(this.admin.instructors.length + 1).padStart(3, '0')}`;
    const i = emptyInstructor(this.admin.instructors.some((x) => x.id === id) ? `${id}-${Date.now().toString(36)}` : id);
    this.admin.instructors.push(i);
    this.touchAdmin();
    return i;
  }

  addTrack(): CertificateTrack {
    const id = `T${String(this.admin.tracks.length + 1).padStart(3, '0')}`;
    const t = emptyTrack(this.admin.tracks.some((x) => x.traineeId === id) ? `${id}-${Date.now().toString(36)}` : id);
    this.admin.tracks.push(t);
    this.touchAdmin();
    return t;
  }

  /** 法定證書到期日改動時同步回名冊的 certificateExpiry(個人訓練紀錄列印用同一日期)。 */
  private setStatutoryExpiry(t: CertificateTrack, v: string): void {
    t.statutoryExpiry = parseDate(v) === null ? '' : v;
    if (t.alignedWithStatutory) alignWithStatutory(t);
    const trainee = this.ws.session.trainees.find((x) => x.id === t.traineeId);
    if (trainee) {
      if (t.statutoryExpiry) trainee.certificateExpiry = t.statutoryExpiry;
      else delete trainee.certificateExpiry;
      this.touch();
    }
    this.touchAdmin();
  }

  private setLastCompleted(t: CertificateTrack, v: string): void {
    t.lastCompletedDate = parseDate(v) === null ? '' : v;
    if (!t.alignedWithStatutory) t.refresherDue = t.lastCompletedDate ? refresherDueFrom(t.lastCompletedDate, this.admin.refresherIntervalYears) : '';
    this.touchAdmin();
  }

  mergeRoster(roster: Roster): void {
    const s = this.ws.session;
    for (const t of roster.trainees) {
      const i = s.trainees.findIndex((x) => x.id === t.id);
      if (i >= 0) s.trainees[i] = t;
      else s.trainees.push(t);
    }
    for (const m of roster.staff) {
      const list = m.role === 'assessor' ? s.assessors : s.instructors;
      if (m.name && !list.includes(m.name)) list.push(m.name);
    }
    this.touch();
  }

  rosterJson(): string {
    const s = this.ws.session;
    const roster: Roster = {
      format: ROSTER_FORMAT,
      version: 1,
      trainees: s.trainees,
      staff: [...s.instructors.map((n) => ({ name: n, role: 'instructor' as const })), ...s.assessors.map((n) => ({ name: n, role: 'assessor' as const }))],
    };
    return exportRoster(roster);
  }

  private onFileInput(e: Event): void {
    const input = e.target as HTMLInputElement;
    const files = [...(input.files ?? [])];
    input.value = '';
    void this.openFiles(files);
  }

  // ------------------------------------------------------------ 評估表
  formsForRecord(): MarkerForm[] {
    const k = this.key;
    return this.ws.forms.filter((f) => f.record.key === k || (!k && !f.record.key));
  }

  /** 沒有練習紀錄鍵的評估表(未載入紀錄時建立,或匯入的表缺 record.key):不屬於任何報告或班次,需綁定。 */
  unboundForms(): MarkerForm[] {
    return this.ws.forms.filter((f) => !f.record.key);
  }

  /** 把沒有紀錄鍵的評估表綁定到目前載入的紀錄(寫入 record.key 與情境/船/建立時間)。 */
  bindUnboundForms(): number {
    if (!this.data) return 0;
    const list = this.unboundForms();
    for (const f of list) f.record = this.recordRef();
    if (list.length > 0 && !this.formsForRecord().some((f) => f.formId === this.activeFormId)) this.activeFormId = list[0]!.formId;
    this.touch();
    return list.length;
  }

  private recordRef(): MarkerForm['record'] {
    const d = this.data;
    const rec: MarkerForm['record'] = { key: this.key };
    if (d?.header.scenarioId) rec.scenarioId = d.header.scenarioId;
    if (d?.header.shipId) rec.shipId = d.header.shipId;
    if (d?.header.createdUtc) rec.createdUtc = d.header.createdUtc;
    return rec;
  }

  get activeForm(): MarkerForm | undefined {
    return this.ws.forms.find((f) => f.formId === this.activeFormId) ?? this.formsForRecord()[0];
  }

  newForm(role: 'primary' | 'second' = 'primary'): MarkerForm {
    const rec = this.recordRef();
    const assessor = role === 'primary' ? (this.ws.session.assessors[0] ?? this.meta.instructor) : (this.ws.session.assessors[1] ?? '');
    const form = createMarkerForm({ record: rec, assessor: { name: assessor ?? '', role } });
    this.ws.forms.push(form);
    this.activeFormId = form.formId;
    this.touch();
    return form;
  }

  private deleteForm(id: string): void {
    this.ws.forms = this.ws.forms.filter((f) => f.formId !== id);
    this.activeFormId = this.formsForRecord()[0]?.formId ?? '';
    this.touch();
  }

  private setTrainee(form: MarkerForm, value: string): void {
    if (value === '__team') form.trainee = { id: '', name: '團隊', rank: '', role: form.trainee.role };
    else {
      const t = this.ws.session.trainees.find((x) => x.id === value);
      form.trainee = t ? { id: t.id, name: t.name, rank: t.rank, role: this.meta.participants.find((p) => p.traineeId === t.id)?.role ?? form.trainee.role } : { id: '', name: '', rank: '', role: form.trainee.role };
    }
    this.touch();
  }

  private setScore(r: ElementRating, score: number): void {
    r.score = r.score === score ? null : score;
    this.touch();
  }

  // ------------------------------------------------------------ 列印
  private printDoc(): void {
    const old = document.title;
    const r = this.report;
    const base = this.tab === 'report' ? `講評報告-${r?.reportNo ?? ''}` : this.tab === 'form' ? `行為標記評估表-${this.activeForm?.trainee.name ?? ''}` : this.tab === 'individual' ? `個人訓練紀錄-${this.ws.session.sessionId}` : this.tab === 'admin' ? `教官資格與證書效期-${this.asOf}` : `課程班次紀錄-${this.ws.session.sessionId}`;
    document.title = base.replace(/[\\/:*?"<>|]/g, '_');
    try {
      window.print();
    } finally {
      document.title = old;
    }
  }

  protected override updated(): void {
    // A4 頁首/頁尾(@page 邊界方塊;Chromium 131+):每頁固定標示「中塑海運內部訓練」與課程版本、頁碼
    // 已移出文件後才執行的更新不再建立 <style>(否則留下過時的頁首規則)
    if (!this.isConnected) return;
    if (!this.pageStyle) {
      this.pageStyle = document.createElement('style');
      this.pageStyle.id = 'report-page-style';
      document.head.appendChild(this.pageStyle);
    }
    const m = this.data ? this.meta : null;
    const course = m ? `${m.courseName}${m.courseVersion ? ` 第 ${m.courseVersion} 版` : ''}` : `${this.ws.session.courseName}${this.ws.session.courseVersion ? ` 第 ${this.ws.session.courseVersion} 版` : ''}`;
    const esc = cssString;
    const css = `@page { size: A4; margin: 16mm 13mm 16mm 13mm; @top-left { content: "中塑海運內部訓練"; font-size: 8pt; color: #333; } @top-right { content: "${esc(course)}"; font-size: 8pt; color: #333; } @bottom-left { content: "${esc(TAB_LABEL[this.tab])}"; font-size: 8pt; color: #555; } @bottom-right { content: counter(page) " / " counter(pages); font-size: 8pt; color: #555; } }`;
    if (this.pageStyle.textContent !== css) this.pageStyle.textContent = css;
  }

  // ------------------------------------------------------------ 畫面
  override render(): TemplateResult {
    return html`
      <header class="rp-toolbar screen-only">
        <span class="rp-title">Simosa BRM 講評報告</span>
        <nav class="rp-tabs" role="tablist">
          ${(Object.keys(TAB_LABEL) as ReportTab[]).map((t) => html`<button role="tab" class="tab-${t} ${this.tab === t ? 'active' : ''}" aria-selected=${this.tab === t} @click=${() => (this.tab = t)}>${TAB_LABEL[t]}</button>`)}
        </nav>
        <span class="rp-spacer"></span>
        <label class="rp-btn file-btn">開啟檔案…<input class="file-input" type="file" multiple accept=".jsonl,.json,.txt,application/json" @change=${this.onFileInput} /></label>
        <button class="rp-btn primary print-btn" @click=${this.printDoc}>列印 / 另存 PDF</button>
        <span class="rp-notice">訓練模擬器,非航行設備</span>
      </header>
      <div class="rp-status screen-only">
        <span class="rec">${this.data ? html`紀錄:${this.recordName} · ${this.data.header.scenarioId ?? '—'} · ${this.data.header.shipId ?? ''}` : '尚未載入練習紀錄(開啟核心 JSON Lines 紀錄或教官站下載的紀錄 JSON;教官站按「講評報告」可直接帶入)'}</span>
        ${this.persistent ? html`<span class="ok">已保存於本機瀏覽器</span>` : html`<span class="warn">本機儲存不可用:資料只在此頁,請以 JSON 匯出</span>`}
        ${this.message ? html`<span class="msg ${this.messageLevel}">${this.message}</span>` : nothing}
      </div>
      <main class="rp-main tab-${this.tab}">${this.renderTab()}</main>
    `;
  }

  private renderTab(): TemplateResult {
    switch (this.tab) {
      case 'report':
        return this.renderReportTab();
      case 'form':
        return this.renderFormTab();
      case 'individual':
        return this.renderIndividualTab();
      case 'session':
        return this.renderSessionTab();
      case 'admin':
        return this.renderAdminTab();
    }
  }

  // ---------------------------------------------- 講評報告分頁
  private renderReportTab(): TemplateResult {
    const r = this.report;
    if (!r) return html`<section class="rp-editor screen-only"><h2>講評報告</h2><p>請以「開啟檔案」載入練習紀錄:核心的 JSON Lines 紀錄(<code>build/records/*.jsonl</code>)或教官站「下載紀錄 JSON」的檔案。也可同時選取 BRM 標記 JSON、評估表 JSON、學員名冊或班次紀錄 JSON。</p></section>`;
    const m = this.meta;
    const set = (k: keyof Omit<ReportMeta, 'participants' | 'debrief'>) => (e: Event) => {
      m[k] = val(e);
      this.touch();
    };
    return html`
      <section class="rp-editor screen-only">
        <h2>報告欄位</h2>
        <div class="fields">
          <label>課程名稱 <input class="f-course" .value=${m.courseName} @change=${set('courseName')} /></label>
          <label>課程版本 <input class="f-course-version" .value=${m.courseVersion} @change=${set('courseVersion')} /></label>
          <label>班次編號 <input .value=${m.sessionId} @change=${set('sessionId')} /></label>
          <label>練習編號 <input .value=${m.exerciseNo} placeholder=${r.exercise.exerciseNo} @change=${set('exerciseNo')} /></label>
          <label>報告編號 <input .value=${m.reportNo} placeholder=${r.reportNo} @change=${set('reportNo')} /></label>
          <label>教官 <input class="f-instructor" .value=${m.instructor} @change=${set('instructor')} list="rp-staff" /></label>
        </div>
        ${this.staffDatalist()}
        <h3>參與者與角色</h3>
        <table class="edit-tbl participants-edit">
          <thead><tr><th>學員</th><th>姓名</th><th>職級</th><th>角色</th><th></th></tr></thead>
          <tbody>
            ${m.participants.map(
              (p, i) => html`<tr>
                <td><select .value=${p.traineeId} @change=${(e: Event) => this.setParticipantTrainee(p, val(e))}><option value="">(非學員)</option>${this.ws.session.trainees.map((t) => html`<option value=${t.id} ?selected=${t.id === p.traineeId}>${t.id} ${t.name}</option>`)}</select></td>
                <td><input .value=${p.name} @change=${(e: Event) => ((p.name = val(e)), this.touch())} /></td>
                <td><input .value=${p.rank} @change=${(e: Event) => ((p.rank = val(e)), this.touch())} /></td>
                <td><select @change=${(e: Event) => ((p.role = val(e)), this.touch())}>${PARTICIPANT_ROLES.map((ro) => html`<option value=${ro.id} ?selected=${ro.id === p.role}>${ro.label}</option>`)}</select></td>
                <td><button class="rp-btn small" @click=${() => (m.participants.splice(i, 1), this.touch())}>刪除</button></td>
              </tr>`,
            )}
          </tbody>
        </table>
        <div class="row-actions">
          <button class="rp-btn add-participant" @click=${() => (m.participants.push({ traineeId: '', name: '', rank: '', role: m.participants.length === 0 ? 'master' : 'oow' }), this.touch())}>新增參與者</button>
          ${this.ws.session.trainees.length > 0 ? html`<button class="rp-btn" @click=${this.addAllTrainees}>由名冊全部加入</button>` : nothing}
        </div>
        <h3>講評紀錄(第 8.4 節)</h3>
        ${r.debrief.map(
          (d) => html`<label class="block">${d.title}<span class="hint">${d.hint}</span><textarea class="f-debrief-${d.id}" rows="3" .value=${m.debrief[d.id]} @change=${(e: Event) => ((m.debrief[d.id] = val(e)), this.touch())}></textarea></label>`,
        )}
        <label class="block">教官總評<textarea rows="3" .value=${m.remarks} @change=${set('remarks')}></textarea></label>
        <div class="row-actions">
          <button class="rp-btn add-to-session" @click=${this.addToSession}>加入課程班次紀錄${this.incompleteForms(r).length ? `(${this.incompleteForms(r).length} 份評估表未完成)` : ''}</button>
          <span class="hint">出席 = 參與者中有學員 ID 者;完成 = 核心 summary 已定稿或評估表完整;結果含核心評分(輔助)與該學員主要評估員的評估表</span>
          ${this.incompleteForms(r).map((f) => {
            const issues = validateMarkerForm(f, { duration_s: r.exercise.duration_s });
            const errors = issues.filter((i) => i.level === 'error').length;
            const sm = summarizeForm(f);
            return html`<span class="warn add-hint">${f.trainee.name || f.trainee.id} 的評估表未完成(錯誤 ${errors} 項、未評 ${sm.total - sm.rated} 項):加入後不計入行為標記分數</span>`;
          })}
        </div>
      </section>
      <section class="rp-preview">${renderReportDoc(r)}</section>
    `;
  }

  private staffDatalist(): TemplateResult {
    const s = this.ws.session;
    return html`<datalist id="rp-staff">${[...new Set([...s.instructors, ...s.assessors])].map((n) => html`<option value=${n}></option>`)}</datalist>`;
  }

  private setParticipantTrainee(p: Participant, id: string): void {
    p.traineeId = id;
    const t = this.ws.session.trainees.find((x) => x.id === id);
    if (t) {
      p.name = t.name;
      p.rank = t.rank;
    }
    this.touch();
  }

  private addAllTrainees(): void {
    const m = this.meta;
    for (const t of this.ws.session.trainees) if (!m.participants.some((p) => p.traineeId === t.id)) m.participants.push({ traineeId: t.id, name: t.name, rank: t.rank, role: 'oow' });
    this.touch();
  }

  private incompleteForms(r: DebriefReport): MarkerForm[] {
    return incompleteFormsForReport(this.ws.session, r, this.ws.forms);
  }

  addToSession(): void {
    const r = this.report;
    if (!r) return;
    const ex = upsertExerciseFromReport(this.ws.session, r, this.ws.forms);
    const incomplete = Object.values(ex.results).filter((x) => x.formIncomplete).length;
    this.say(`已加入課程班次紀錄:${ex.exerciseNo} ${ex.date}(${ex.hours} h,出席 ${ex.attended.length} 人)${incomplete ? `;${incomplete} 份評估表未完成,未計入行為標記分數` : ''}`);
    this.touch();
  }

  // ---------------------------------------------- 評估表分頁
  private renderFormTab(): TemplateResult {
    const forms = this.formsForRecord();
    const form = this.activeForm;
    const unbound = this.data ? this.unboundForms().length : 0;
    const others = this.ws.forms.length - forms.length - unbound;
    return html`
      <section class="rp-editor screen-only form-editor">
        <h2>行為標記評估表 <span class="draft-badge" title="behavioural-anchors.json">錨定描述 ${ANCHORS.version} · ${ANCHORS.statusLabel}</span></h2>
        <div class="form-list">
          ${forms.map((f) => {
            const done = isFormComplete(f);
            return html`<button class="form-chip ${f.formId === form?.formId ? 'active' : ''} ${done ? 'done' : ''}" @click=${() => (this.activeFormId = f.formId)}>${f.trainee.name || '(未選受評者)'} · ${f.assessor.name || '(評估員)'} · ${f.assessor.role === 'primary' ? '主要' : '第二'}${done ? ' ✓' : ''}</button>`;
          })}
          <button class="rp-btn new-form" ?disabled=${!this.data} @click=${() => this.newForm('primary')}>新增評估表</button>
          <button class="rp-btn new-form-second" ?disabled=${!this.data} @click=${() => this.newForm('second')}>新增第二評估員評估表</button>
        </div>
        ${this.data ? nothing : html`<p class="hint">請先載入練習紀錄再新增評估表(評估表以紀錄鍵對應練習)。</p>`}
        ${unbound > 0
          ? html`<p class="warn unbound-forms">有 ${unbound} 份評估表未綁定練習紀錄(未載入紀錄時建立,或匯入的表缺紀錄鍵),不會列入任何講評報告或班次。<button class="rp-btn small bind-forms" @click=${() => this.bindUnboundForms()}>綁定到目前紀錄</button></p>`
          : nothing}
        ${others > 0 ? html`<p class="hint">另有 ${others} 份評估表屬於其他練習紀錄(載入該紀錄後顯示)</p>` : nothing}
        <div class="row-actions">
          <button class="rp-btn export-forms" ?disabled=${forms.length === 0} @click=${() => downloadText(`brm-marker-forms-${fileStamp()}-${this.data?.header.scenarioId ?? 'forms'}.json`, exportForms(forms))}>匯出評估表 JSON</button>
          <label class="rp-btn file-btn">匯入評估表 JSON(含第二評估員)<input type="file" accept=".json,application/json" @change=${this.onFileInput} /></label>
          ${form ? html`<button class="rp-btn danger" @click=${() => this.deleteForm(form.formId)}>刪除此表</button>` : nothing}
        </div>
        ${form ? this.renderFormEditor(form) : html`<p>尚無評估表。按「新增評估表」開始評分(每位受評者一份;雙評時第二評估員另填一份或匯入其 JSON)。</p>`}
      </section>
      <section class="rp-preview">${form ? renderFormDoc(form, this.formCtx()) : nothing}</section>
    `;
  }

  private formCtx(): { scenario: string; reportNo: string; courseLine: string } {
    const r = this.report;
    const m = this.data ? this.meta : null;
    return {
      scenario: r ? `${r.exercise.exerciseNo} ${r.exercise.scenarioId} ${r.exercise.scenarioName} · ${r.exercise.shipId} ${r.exercise.loadingLabel}` : '—',
      reportNo: r?.reportNo ?? '—',
      courseLine: m ? `${m.courseName}${m.courseVersion ? ` 第 ${m.courseVersion} 版` : ''}` : this.ws.session.courseName,
    };
  }

  private renderFormEditor(form: MarkerForm): TemplateResult {
    const duration = this.data ? (this.data.summary?.general.duration_s ?? this.data.tEnd - this.data.tStart) : undefined;
    const issues = validateMarkerForm(form, duration !== undefined ? { duration_s: duration } : {});
    const errors = issues.filter((i) => i.level === 'error');
    const warnings = issues.filter((i) => i.level === 'warning');
    const markers = this.markers;
    const second = form.assessor.role === 'primary' ? this.formsForRecord().find((f) => f.assessor.role === 'second' && f.trainee.id === form.trainee.id && f.formId !== form.formId) : undefined;
    const traineeSel = form.trainee.name === '團隊' && !form.trainee.id ? '__team' : form.trainee.id;
    return html`
      <div class="fields">
        <label>受評者 <select class="f-trainee" @change=${(e: Event) => this.setTrainee(form, val(e))}>
          <option value="" ?selected=${!traineeSel}>(請選擇)</option>
          <option value="__team" ?selected=${traineeSel === '__team'}>團隊</option>
          ${this.ws.session.trainees.map((t) => html`<option value=${t.id} ?selected=${t.id === traineeSel}>${t.id} ${t.name}</option>`)}
        </select></label>
        <label>受評者姓名 <input .value=${form.trainee.name} @change=${(e: Event) => ((form.trainee.name = val(e)), this.touch())} /></label>
        <label>角色 <select @change=${(e: Event) => ((form.trainee.role = val(e)), this.touch())}><option value="">—</option>${PARTICIPANT_ROLES.map((r) => html`<option value=${r.id} ?selected=${r.id === form.trainee.role}>${r.label}</option>`)}</select></label>
        <label>評估員 <input class="f-assessor" .value=${form.assessor.name} list="rp-staff" @change=${(e: Event) => ((form.assessor.name = val(e)), this.touch())} /></label>
        <label>評估員身分 <select class="f-assessor-role" @change=${(e: Event) => ((form.assessor.role = val(e) === 'second' ? 'second' : 'primary'), this.touch())}><option value="primary" ?selected=${form.assessor.role === 'primary'}>主要評估員</option><option value="second" ?selected=${form.assessor.role === 'second'}>第二評估員(雙評)</option></select></label>
      </div>
      ${this.staffDatalist()}
      <div class="validation ${errors.length ? 'has-errors' : ''}">
        ${errors.length === 0 && warnings.length === 0 ? html`<span class="ok">評估表完整</span>` : nothing}
        ${errors.length > 0 ? html`<span class="err-count">錯誤 ${errors.length}</span>` : nothing}
        ${warnings.length > 0 ? html`<span class="warn-count">提醒 ${warnings.length}</span>` : nothing}
        ${issues.filter((i) => !i.elementId).map((i) => html`<span class="issue ${i.level}">${i.message}</span>`)}
      </div>
      ${ANCHORS.categories.map((c) => {
        const catMarkers = markers.filter((m) => m.category === c.id);
        return html`<fieldset class="cat cat-${c.id}">
          <legend>${c.label} <span class="en">${c.en}</span></legend>
          ${catMarkers.length > 0
            ? html`<div class="quick-markers"><span class="hint">BRM 快速標記:</span>${catMarkers.map((m) => this.renderMarkerAttach(form, m))}</div>`
            : nothing}
          ${c.elements.map((e) => this.renderElementEditor(form, e.id, issues.filter((i) => i.elementId === e.id)))}
        </fieldset>`;
      })}
      <label class="block">綜合評語<textarea rows="3" .value=${form.overallComment} @change=${(e: Event) => ((form.overallComment = val(e)), this.touch())}></textarea></label>
      ${form.assessor.role === 'primary'
        ? html`<div class="agreement-box">${second ? renderAgreement(second, computeAgreement(form, second)) : html`<p class="hint">雙評:匯入或新增同一受評者的「第二評估員」評估表後,自動計算一致率(±1 分內比例,目標 ≥ 80%)。</p>`}</div>`
        : nothing}
    `;
  }

  private renderMarkerAttach(form: MarkerForm, m: BrmMarker): TemplateResult {
    const els = elementsForMarker(m);
    const attachedTo = els.filter((id) => ratingOf(form, id)?.observations.some((o) => o.markerId === m.id));
    let chosen = els[0] ?? '';
    return html`<span class="qm ${m.polarity ?? ''}">
      <span class="qm-t">${formatSimTime(m.t)}</span>${m.polarity === 'plus' ? '+' : m.polarity === 'delta' ? 'Δ' : ''} ${m.note || '(無備註)'}
      <select @change=${(e: Event) => (chosen = val(e))}>${els.map((id) => html`<option value=${id}>${allElements().find((x) => x.id === id)?.label ?? id}</option>`)}</select>
      <button class="rp-btn small attach-marker" @click=${() => {
        if (attachMarker(form, chosen, m)) this.touch();
      }}>帶入</button>
      ${attachedTo.length ? html`<span class="hint">已帶入 ${attachedTo.length}</span>` : nothing}
    </span>`;
  }

  private renderElementEditor(form: MarkerForm, elementId: string, issues: FormIssue[]): TemplateResult {
    const e = allElements().find((x) => x.id === elementId)!;
    const r = ratingOf(form, elementId)!;
    const low = r.score !== null && r.score <= ANCHORS.lowScoreThreshold;
    return html`<div class="el el-${elementId.replace('.', '-')} ${low ? 'low' : ''}" data-element=${elementId}>
      <div class="el-head">
        <span class="el-label">${e.label}</span>
        <span class="el-stcw">${e.stcw.map((s) => html`<span class="stcw-tag" title=${stcwText(s)}>${s}</span>`)}</span>
        <span class="scores" role="radiogroup" aria-label=${`${e.label} 分數`}>
          ${[1, 2, 3, 4, 5].map((n) => html`<button class="score-btn s${n} ${r.score === n ? 'active' : ''}" role="radio" aria-checked=${r.score === n} data-score=${n} @click=${() => this.setScore(r, n)}>${n}</button>`)}
        </span>
      </div>
      <div class="anchors"><div><b>1</b> ${e.anchors['1']}</div><div><b>3</b> ${e.anchors['3']}</div><div><b>5</b> ${e.anchors['5']}</div></div>
      <div class="obs-list">
        ${r.observations.map(
          (o, i) => html`<div class="obs">
            <input class="obs-t" placeholder="時刻 HH:MM:SS" .value=${o.t_s === null ? '' : Number.isFinite(o.t_s) ? formatSimTime(o.t_s) : ''} @change=${(ev: Event) => ((o.t_s = parseSimTimeInput(val(ev))), this.touch())} />
            <input class="obs-text" placeholder="觀察描述" .value=${o.text} @change=${(ev: Event) => ((o.text = val(ev)), this.touch())} />
            ${o.markerId !== undefined ? html`<span class="hint">標記 #${o.markerId}</span>` : nothing}
            <button class="rp-btn small" @click=${() => (r.observations.splice(i, 1), this.touch())}>刪除</button>
          </div>`,
        )}
        <button class="rp-btn small add-obs" title="時刻請填實際觀察時刻(HH:MM:SS),或由 BRM 快速標記帶入" @click=${() => (r.observations.push({ t_s: null, text: '' }), this.touch())}>新增觀察</button>
      </div>
      <div class="el-foot">
        <label>缺失 <select class="def-type" @change=${(ev: Event) => {
          const v = val(ev);
          r.deficiency.type = v ? (v as DeficiencyType) : null;
          if (r.deficiency.type !== 'human') r.deficiency.humanFactor = null;
          this.touch();
        }}><option value="">—</option>${ANCHORS.deficiencyTypes.map((d) => html`<option value=${d.id} ?selected=${d.id === r.deficiency.type}>${d.label}</option>`)}</select></label>
        <label>人員子類 <select class="def-human" ?disabled=${r.deficiency.type !== 'human'} @change=${(ev: Event) => {
          const v = val(ev);
          r.deficiency.humanFactor = v ? (v as HumanFactor) : null;
          this.touch();
        }}><option value="">—</option>${ANCHORS.humanFactors.map((h) => html`<option value=${h.id} ?selected=${h.id === r.deficiency.humanFactor}>${h.label}</option>`)}</select></label>
        <input class="el-comment" placeholder="評語" .value=${r.comment} @change=${(ev: Event) => ((r.comment = val(ev)), this.touch())} />
      </div>
      ${issues.length > 0 ? html`<div class="el-issues">${issues.map((i) => html`<span class="issue ${i.level}">${i.index !== undefined ? `觀察 ${i.index + 1}:` : ''}${i.message}</span>`)}</div>` : nothing}
    </div>`;
  }

  // ---------------------------------------------- 個人訓練紀錄分頁
  private renderIndividualTab(): TemplateResult {
    const s = this.ws.session;
    const records = buildIndividualRecords(s).filter((r) => !this.individualTrainee || r.trainee.id === this.individualTrainee);
    return html`
      <section class="rp-editor screen-only">
        ${this.renderSessionEditor()}
        <h3>列印</h3>
        <label>學員 <select class="f-individual" @change=${(e: Event) => (this.individualTrainee = val(e))}><option value="">全部(每位學員一頁起)</option>${s.trainees.map((t) => html`<option value=${t.id} ?selected=${t.id === this.individualTrainee}>${t.id} ${t.name}</option>`)}</select></label>
        ${this.renderVerdictEditor()}
      </section>
      <section class="rp-preview">${records.length === 0 ? html`<p class="screen-only hint">名冊沒有學員</p>` : records.map((r) => renderIndividualDoc(r))}</section>
    `;
  }

  /** 評估員判定與備註(R9「通過/未通過」與備註;每位學員 × 出席的練習)。上方「學員」選擇時只列該學員。 */
  private renderVerdictEditor(): TemplateResult {
    const s = this.ws.session;
    const rows = s.trainees
      .filter((t) => !this.individualTrainee || t.id === this.individualTrainee)
      .flatMap((t) => s.exercises.filter((ex) => ex.attended.includes(t.id)).map((ex) => ({ t, ex })));
    return html`<h3>評估員判定與備註</h3>
      <p class="hint">評估員對個人的最終判定(通過/未通過)與備註;自動評分為團隊層級,僅作輔助。</p>
      ${rows.length === 0
        ? html`<p class="hint">尚無出席紀錄</p>`
        : html`<table class="edit-tbl verdict-edit">
            <thead><tr><th>學員</th><th>練習</th><th>自動評分(輔助)</th><th>判定</th><th>備註</th></tr></thead>
            <tbody>${rows.map(({ t, ex }) => {
              const r = ex.results[t.id];
              return html`<tr>
                <td>${t.id} ${t.name}</td>
                <td>${ex.exerciseNo} ${ex.date}</td>
                <td>${r?.score ?? '—'}${r?.formIncomplete ? html` <span class="warn">評估表未完成</span>` : nothing}</td>
                <td><select class="verdict-sel" @change=${(e: Event) => this.setVerdict(ex.results, t.id, val(e))}>
                  <option value="" ?selected=${!r?.verdict}>(未判定)</option>
                  ${(['pass', 'fail'] as const).map((v) => html`<option value=${v} ?selected=${r?.verdict === v}>${VERDICT_LABEL[v]}</option>`)}
                </select></td>
                <td><input class="verdict-remark" .value=${r?.remark ?? ''} @change=${(e: Event) => this.setRemark(ex.results, t.id, val(e))} /></td>
              </tr>`;
            })}</tbody>
          </table>`}`;
  }

  private setVerdict(results: CourseSession['exercises'][number]['results'], id: string, v: string): void {
    const r = (results[id] ??= {});
    if (isTraineeVerdict(v)) r.verdict = v;
    else delete r.verdict;
    this.touch();
  }

  private setRemark(results: CourseSession['exercises'][number]['results'], id: string, v: string): void {
    const r = (results[id] ??= {});
    if (v.trim()) r.remark = v;
    else delete r.remark;
    this.touch();
  }

  private renderSessionEditor(): TemplateResult {
    const s = this.ws.session;
    const setS = (k: 'sessionId' | 'courseName' | 'courseVersion' | 'trainingForm' | 'shipId' | 'startDate' | 'endDate' | 'equipment' | 'evaluation') => (e: Event) => {
      s[k] = val(e);
      this.touch();
    };
    return html`
      <h2>課程班次</h2>
      <div class="fields">
        <label>班次編號 <input class="f-session-id" .value=${s.sessionId} placeholder="例:BRM-FSB1-2026-01" @change=${setS('sessionId')} /></label>
        <label>課程名稱 <input .value=${s.courseName} @change=${setS('courseName')} /></label>
        <label>課程版本 <input class="f-session-version" .value=${s.courseVersion} @change=${setS('courseVersion')} /></label>
        <label>訓練形式 <select @change=${setS('trainingForm')}>${TRAINING_FORMS.map((f) => html`<option value=${f.id} ?selected=${f.id === s.trainingForm}>${f.label}</option>`)}</select></label>
        <label>船舶 <select @change=${setS('shipId')}><option value="">—</option>${['FSB1', 'FSB2'].map((id) => html`<option value=${id} ?selected=${id === s.shipId}>${id}</option>`)}</select></label>
        <label>開始日期 <input type="date" .value=${s.startDate} @change=${setS('startDate')} /></label>
        <label>結束日期 <input type="date" .value=${s.endDate} @change=${setS('endDate')} /></label>
        <label>授課教官(逗號分隔) <input class="f-instructors" .value=${s.instructors.join(', ')} @change=${(e: Event) => ((s.instructors = splitList(val(e))), this.touch())} /></label>
        <label>評估員(逗號分隔) <input class="f-assessors" .value=${s.assessors.join(', ')} @change=${(e: Event) => ((s.assessors = splitList(val(e))), this.touch())} /></label>
      </div>
      <label class="block">設備配置<input .value=${s.equipment} @change=${setS('equipment')} /></label>
      <label class="block">課後評估(L1 問卷摘要等)<textarea rows="2" .value=${s.evaluation} @change=${setS('evaluation')}></textarea></label>
      <h3>學員名冊</h3>
      <table class="edit-tbl roster-edit">
        <thead><tr><th>ID</th><th>姓名</th><th>職級</th><th>法定證書到期</th><th></th></tr></thead>
        <tbody>${s.trainees.map((t, i) => this.renderTraineeRow(t, i))}</tbody>
      </table>
      <div class="row-actions">
        <button class="rp-btn add-trainee" @click=${() => (s.trainees.push({ id: `T${String(s.trainees.length + 1).padStart(3, '0')}`, name: '', rank: '' }), this.touch())}>新增學員</button>
        <button class="rp-btn export-roster" @click=${() => downloadText(`brm-roster-${fileStamp()}.json`, this.rosterJson())}>匯出名冊 JSON</button>
        <label class="rp-btn file-btn">匯入名冊 JSON<input type="file" accept=".json,application/json" @change=${this.onFileInput} /></label>
      </div>
      <h3>練習與出席</h3>
      ${s.exercises.length === 0
        ? html`<p class="hint">尚無練習:在「講評報告」分頁載入紀錄、填參與者後按「加入課程班次紀錄」。</p>`
        : html`<table class="edit-tbl exercises-edit">
            <thead><tr><th>練習</th><th>日期</th><th>時數</th>${s.trainees.map((t) => html`<th title=${t.name}>${t.id}<div class="hint">出席/完成</div></th>`)}<th></th></tr></thead>
            <tbody>${s.exercises.map(
              (ex, i) => html`<tr>
                <td>${ex.exerciseNo}<div class="hint">${ex.reportNo}</div></td>
                <td><input type="date" .value=${ex.date} @change=${(e: Event) => ((ex.date = val(e)), this.touch())} /></td>
                <td><input class="ex-hours" type="number" min="0" step="0.25" .value=${String(ex.hours)} @change=${(e: Event) => ((ex.hours = Number(val(e)) || 0), this.touch())} /></td>
                ${s.trainees.map(
                  (t) => html`<td class="att">
                    <input type="checkbox" title="出席" .checked=${ex.attended.includes(t.id)} @change=${(e: Event) => this.toggle(ex.attended, t.id, (e.target as HTMLInputElement).checked)} />
                    <input type="checkbox" title="完成" .checked=${ex.completed.includes(t.id)} @change=${(e: Event) => this.toggle(ex.completed, t.id, (e.target as HTMLInputElement).checked)} />
                  </td>`,
                )}
                <td><button class="rp-btn small" @click=${() => (s.exercises.splice(i, 1), this.touch())}>刪除</button></td>
              </tr>`,
            )}</tbody>
          </table>`}
    `;
  }

  private renderTraineeRow(t: Trainee, i: number): TemplateResult {
    const s = this.ws.session;
    return html`<tr>
      <td><input class="t-id" .value=${t.id} @change=${(e: Event) => this.renameTrainee(t, val(e).trim())} /></td>
      <td><input class="t-name" .value=${t.name} @change=${(e: Event) => ((t.name = val(e)), this.touch())} /></td>
      <td><input class="t-rank" .value=${t.rank} @change=${(e: Event) => ((t.rank = val(e)), this.touch())} /></td>
      <td><input type="date" .value=${t.certificateExpiry ?? ''} @change=${(e: Event) => {
        const v = val(e);
        if (v) t.certificateExpiry = v;
        else delete t.certificateExpiry;
        this.touch();
      }} /></td>
      <td><button class="rp-btn small" @click=${() => (s.trainees.splice(i, 1), this.touch())}>刪除</button></td>
    </tr>`;
  }

  private renameTrainee(t: Trainee, id: string): void {
    if (!id || id === t.id) return;
    const old = t.id;
    t.id = id;
    for (const ex of this.ws.session.exercises) {
      ex.attended = ex.attended.map((x) => (x === old ? id : x));
      ex.completed = ex.completed.map((x) => (x === old ? id : x));
      if (ex.results[old]) {
        ex.results[id] = ex.results[old]!;
        delete ex.results[old];
      }
    }
    this.touch();
  }

  private toggle(list: string[], id: string, on: boolean): void {
    const i = list.indexOf(id);
    if (on && i < 0) list.push(id);
    if (!on && i >= 0) list.splice(i, 1);
    this.touch();
  }

  // ---------------------------------------------- 課程班次紀錄分頁
  private renderSessionTab(): TemplateResult {
    const s = this.ws.session;
    const issues = validateCourseSession(s);
    const name = s.sessionId || 'session';
    return html`
      <section class="rp-editor screen-only">
        <h2>課程班次紀錄匯出</h2>
        <p class="hint">開課日期與時數、參訓學員與職級、授課教官、使用之情境與設備配置、出席、課後評估(規劃書第 9.1 節)。欄位在「個人訓練紀錄」分頁編輯。</p>
        ${issues.length ? html`<ul class="warn">${issues.map((i) => html`<li>${i}</li>`)}</ul>` : html`<p class="ok">檢查通過</p>`}
        <div class="row-actions">
          <button class="rp-btn export-csv" @click=${() => downloadText(`brm-course-session-${name}-${fileStamp()}.csv`, sessionToCsv(s), 'text/csv')}>匯出 CSV</button>
          <button class="rp-btn export-session" @click=${() => downloadText(`brm-course-session-${name}-${fileStamp()}.json`, exportCourseSession(s))}>匯出 JSON</button>
          <label class="rp-btn file-btn">匯入班次 JSON<input type="file" accept=".json,application/json" @change=${this.onFileInput} /></label>
        </div>
      </section>
      <section class="rp-preview">${renderSessionDoc(s)}</section>
    `;
  }

  // ---------------------------------------------- 教官與證書分頁(?tab=admin)
  private renderAdminTab(): TemplateResult {
    const a = this.admin;
    const today = this.asOf;
    const issues = validateAdminRecords(a);
    const schedule = buildRefresherSchedule(a, today);
    const stamp = fileStamp();
    return html`
      <section class="rp-editor screen-only admin-editor">
        <h2>教官與證書</h2>
        <p class="hint">教官資格紀錄(STCW A-I/6、評估者校準;R10)、學員法定證書與公司 BRM 複訓到期雙軌追蹤、複訓排程(R9;規劃書第 8.0 節每 5 年與換證同步)。資料存於本機瀏覽器 IndexedDB(admin 存放區),可匯出 JSON 搬到另一台教官筆電;不連網。</p>
        <div class="fields">
          <label>狀態計算日 <input class="f-asof" type="date" .value=${today} @change=${(e: Event) => (this.asOf = parseDate(val(e)) === null ? todayIso() : val(e))} /></label>
          <label>複訓週期(年) <input class="f-interval" type="number" min="1" max="10" step="1" .value=${String(a.refresherIntervalYears)} @change=${(e: Event) => {
            const n = Number(val(e));
            a.refresherIntervalYears = Number.isFinite(n) && n > 0 ? Math.round(n) : 5;
            for (const t of a.tracks) if (!t.alignedWithStatutory && t.lastCompletedDate) t.refresherDue = refresherDueFrom(t.lastCompletedDate, a.refresherIntervalYears);
            this.touchAdmin();
          }} /></label>
        </div>
        ${issues.length ? html`<ul class="warn admin-issues">${issues.map((i) => html`<li>${i}</li>`)}</ul>` : nothing}
        <div class="row-actions">
          <button class="rp-btn export-admin" @click=${() => downloadText(`brm-admin-records-${stamp}.json`, exportAdminRecords(a, today))}>匯出 JSON(跨機搬移)</button>
          <label class="rp-btn file-btn">匯入 JSON<input type="file" accept=".json,application/json" @change=${this.onFileInput} /></label>
          <button class="rp-btn export-instructors-csv" @click=${() => downloadText(`brm-instructors-${stamp}.csv`, instructorsToCsv(a), 'text/csv')}>教官資格 CSV</button>
          <button class="rp-btn export-tracks-csv" @click=${() => downloadText(`brm-certificates-${stamp}.csv`, tracksToCsv(a, today), 'text/csv')}>證書效期 CSV</button>
        </div>

        <h3>(a)教官資格紀錄</h3>
        ${a.instructors.length === 0 ? html`<p class="hint">尚無教官;按「新增教官」。</p>` : nothing}
        ${instructorRows(a, today).map((r) => this.renderInstructorEditor(r.instructor, r.calibrated, r.flag))}
        <div class="row-actions"><button class="rp-btn add-instructor" @click=${() => this.addInstructor()}>新增教官</button></div>

        <h3>(b)學員證書效期雙軌追蹤</h3>
        <p class="hint">法定證書到期日與名冊的「法定 BRM 證書到期」同步;公司複訓到期 = 上次完成 + ${a.refresherIntervalYears} 年,按「對齊換證」改為與法定到期日同步。清單依最近到期日排序,90/180 天內到期者標示。</p>
        <table class="edit-tbl tracks-edit">
          <thead><tr><th>學員 ID</th><th>姓名</th><th>職級</th><th>STCW 證書類別</th><th>證書號</th><th>法定到期</th><th>上次完成</th><th>公司複訓到期</th><th>狀態</th><th></th></tr></thead>
          <tbody>${dualTrackRows(a, today).map((r) => {
            const t = r.track;
            return html`<tr class="flag-${r.flag}">
              <td><input class="tr-id" .value=${t.traineeId} @change=${(e: Event) => ((t.traineeId = val(e).trim() || t.traineeId), this.touchAdmin())} /></td>
              <td><input class="tr-name" .value=${t.name} @change=${(e: Event) => ((t.name = val(e)), this.touchAdmin())} /></td>
              <td><input class="tr-rank" .value=${t.rank} @change=${(e: Event) => ((t.rank = val(e)), this.touchAdmin())} /></td>
              <td><input class="tr-type" .value=${t.statutoryType} placeholder="例:領導統御與駕駛臺資源管理" @change=${(e: Event) => ((t.statutoryType = val(e)), this.touchAdmin())} /></td>
              <td><input class="tr-no" .value=${t.statutoryNo} @change=${(e: Event) => ((t.statutoryNo = val(e)), this.touchAdmin())} /></td>
              <td><input class="tr-statutory" type="date" .value=${t.statutoryExpiry} @change=${(e: Event) => this.setStatutoryExpiry(t, val(e))} /></td>
              <td><input class="tr-last" type="date" .value=${t.lastCompletedDate} @change=${(e: Event) => this.setLastCompleted(t, val(e))} /></td>
              <td>
                <input class="tr-due" type="date" .value=${t.refresherDue} ?disabled=${t.alignedWithStatutory} @change=${(e: Event) => ((t.refresherDue = parseDate(val(e)) === null ? '' : val(e)), this.touchAdmin())} />
                <button class="rp-btn small align-btn" title=${t.alignedWithStatutory ? '解除與換證同步,回到上次完成 + 週期' : '公司複訓到期日改為與法定證書換證同步'} @click=${() => {
                  if (t.alignedWithStatutory) unalignFromStatutory(t, a.refresherIntervalYears);
                  else alignWithStatutory(t);
                  this.touchAdmin();
                }}>${t.alignedWithStatutory ? '已對齊換證' : '對齊換證'}</button>
              </td>
              <td class="flag-cell ${r.flag}" title=${`法定:${EXPIRY_FLAG_LABEL[r.statutoryFlag]};公司:${EXPIRY_FLAG_LABEL[r.companyFlag]}`}>${EXPIRY_FLAG_LABEL[r.flag]}${r.nextDue ? html`<div class="hint">${r.nextDue}</div>` : nothing}</td>
              <td><button class="rp-btn small" @click=${() => (a.tracks.splice(a.tracks.indexOf(t), 1), this.touchAdmin())}>刪除</button></td>
            </tr>`;
          })}</tbody>
        </table>
        <div class="row-actions">
          <button class="rp-btn add-track" @click=${() => this.addTrack()}>新增學員</button>
          <button class="rp-btn sync-roster" ?disabled=${this.ws.session.trainees.length === 0} @click=${() => this.syncAdminWithRoster()}>由名冊同步(${this.ws.session.trainees.length} 位)</button>
        </div>

        <h3>(c)複訓排程(季度)</h3>
        <p class="hint">由到期日自動產生(到期前 90 天為建議完成日;已逾期者排本季);可匯出給訓練負責人排入航次/訪船計畫。</p>
        <div class="row-actions">
          <button class="rp-btn export-schedule-csv" ?disabled=${schedule.length === 0} @click=${() => downloadText(`brm-refresher-schedule-${stamp}.csv`, scheduleToCsv(schedule), 'text/csv')}>排程 CSV</button>
          <button class="rp-btn export-schedule-json" ?disabled=${schedule.length === 0} @click=${() => downloadText(`brm-refresher-schedule-${stamp}.json`, scheduleToJson(schedule, today))}>排程 JSON</button>
          <span class="hint">${schedule.reduce((n, q) => n + q.items.length, 0)} 項 / ${schedule.length} 季</span>
        </div>
      </section>
      <section class="rp-preview">${renderAdminDoc(a, today, schedule)}</section>
    `;
  }

  private renderInstructorEditor(i: InstructorQualification, calibrated: boolean, flag: string): TemplateResult {
    const a = this.admin;
    return html`<fieldset class="instructor-edit flag-${flag}">
      <legend>${i.name || '(未填姓名)'} <span class="hint mono">${i.id}</span> ${calibrated ? html`<span class="ok">已校準</span>` : html`<span class="warn">未校準/未達 80%</span>`}</legend>
      <div class="fields">
        <label>姓名 <input class="in-name" .value=${i.name} @change=${(e: Event) => ((i.name = val(e)), this.touchAdmin())} /></label>
        <label>A-I/6 訓練師課程 <input class="in-trainer" .value=${i.trainerCourse} placeholder="例:IMO 示範課程 6.09/6.10" @change=${(e: Event) => ((i.trainerCourse = val(e)), this.touchAdmin())} /></label>
        <label>訓練師資格日期 <input class="in-trainer-date" type="date" .value=${i.trainerDate} @change=${(e: Event) => ((i.trainerDate = parseDate(val(e)) === null ? '' : val(e)), this.touchAdmin())} /></label>
        <label>評估者課程(A-I/6 第 7–8 段) <input class="in-assessor" .value=${i.assessorCourse} @change=${(e: Event) => ((i.assessorCourse = val(e)), this.touchAdmin())} /></label>
        <label>評估者資格日期 <input class="in-assessor-date" type="date" .value=${i.assessorDate} @change=${(e: Event) => ((i.assessorDate = parseDate(val(e)) === null ? '' : val(e)), this.touchAdmin())} /></label>
        <label>資格到期日 <input class="in-expiry" type="date" .value=${i.expiryDate} @change=${(e: Event) => ((i.expiryDate = parseDate(val(e)) === null ? '' : val(e)), this.touchAdmin())} /></label>
      </div>
      <label class="block">該型模擬器實務經驗(監督下評估、雙評次數等)<input class="in-exp" .value=${i.simulatorExperience} @change=${(e: Event) => ((i.simulatorExperience = val(e)), this.touchAdmin())} /></label>
      <label class="block">備註<input class="in-note" .value=${i.note} @change=${(e: Event) => ((i.note = val(e)), this.touchAdmin())} /></label>
      <table class="edit-tbl calibration-edit">
        <thead><tr><th>校準日期</th><th>基準錄影</th><th>一致率 %</th><th>備註</th><th></th></tr></thead>
        <tbody>${i.calibrations.map(
          (c, ci) => html`<tr>
            <td><input class="cal-date" type="date" .value=${c.date} @change=${(e: Event) => ((c.date = parseDate(val(e)) === null ? '' : val(e)), this.touchAdmin())} /></td>
            <td><input class="cal-video" .value=${c.referenceVideo} placeholder="例:E20 基準錄影 1/3" @change=${(e: Event) => ((c.referenceVideo = val(e)), this.touchAdmin())} /></td>
            <td><input class="cal-rate" type="number" min="0" max="100" step="1" .value=${c.agreementRate === null ? '' : String(Math.round(c.agreementRate * 100))} @change=${(e: Event) => {
              const v = val(e).trim();
              const n = Number(v);
              c.agreementRate = v === '' || !Number.isFinite(n) ? null : Math.min(1, Math.max(0, n / 100));
              this.touchAdmin();
            }} /></td>
            <td><input class="cal-note" .value=${c.note ?? ''} @change=${(e: Event) => {
              const v = val(e);
              if (v.trim()) c.note = v;
              else delete c.note;
              this.touchAdmin();
            }} /></td>
            <td><button class="rp-btn small" @click=${() => (i.calibrations.splice(ci, 1), this.touchAdmin())}>刪除</button></td>
          </tr>`,
        )}</tbody>
      </table>
      <div class="row-actions">
        <button class="rp-btn small add-calibration" @click=${() => (i.calibrations.push({ date: this.asOf, referenceVideo: '', agreementRate: null }), this.touchAdmin())}>新增校準紀錄</button>
        <button class="rp-btn small danger" @click=${() => (a.instructors.splice(a.instructors.indexOf(i), 1), this.touchAdmin())}>刪除教官</button>
        ${i.trainerDate ? html`<span class="hint">資格 5 年參考到期:${addYears(i.trainerDate, 5)}</span>` : nothing}
      </div>
    </fieldset>`;
  }

  /** 觀察的時刻顯示(測試用)。 */
  static timeText(t: number | null): string {
    return tStr(t);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-report-app': ReportApp;
  }
}
