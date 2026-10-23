// 參與者與休息時間確認面板(規劃書第 5.5 節「休息時間相容」、第 9.1 節 R9/R10):練習開始前填參與者清單
// (姓名、職務、職級、證書號)與休息時間確認(確認人、是否符合 STCW A-VIII/1 與公司規定、備註),按「確認並送出」
// 以 sessionInfo 指令送核心(核心寫入紀錄標頭)並發 session-info-submitted 事件給教官站(寫入教官站紀錄)。
// 草稿存 localStorage(同一台教官筆電重新整理不必重填);確認時刻取核心 state.utc,無則瀏覽器 UTC。
// 未確認時只顯示警示、不阻擋開始練習(訓練負責人決策;警示在 run-control 與簡化模式的開始鈕旁)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles } from './instructor-shared.js';
import { sessionInfoCommand } from '../../lib/instructor-command.js';
import {
  buildSessionInfo,
  confirmationTimeUtc,
  emptySessionInfoDraft,
  SESSION_PARTICIPANT_ROLES,
  SESSION_ROLE_LABEL,
  validateSessionInfoDraft,
  type SessionInfo,
  type SessionInfoDraft,
  type SessionParticipant,
} from '../../lib/session-info.js';

export const SESSION_INFO_DRAFT_KEY = 'simosa-brm:instructor:sessionInfoDraft';

/** 讀 localStorage 草稿(私密視窗/測試環境無 localStorage 或內容損壞時回傳空草稿)。 */
export function loadSessionInfoDraft(storage: Pick<Storage, 'getItem'> | null = typeof localStorage === 'undefined' ? null : localStorage): SessionInfoDraft {
  const draft = emptySessionInfoDraft();
  try {
    const raw = storage?.getItem(SESSION_INFO_DRAFT_KEY);
    if (!raw) return draft;
    const obj = JSON.parse(raw) as Partial<SessionInfoDraft>;
    if (Array.isArray(obj.participants)) {
      for (const p of obj.participants as unknown[]) {
        if (typeof p !== 'object' || p === null) continue;
        const r = p as Record<string, unknown>;
        draft.participants.push({ name: typeof r['name'] === 'string' ? r['name'] : '', role: typeof r['role'] === 'string' ? r['role'] : 'oow', rank: typeof r['rank'] === 'string' ? r['rank'] : '', certificateNo: typeof r['certificateNo'] === 'string' ? r['certificateNo'] : '' });
      }
    }
    if (obj.restHours && typeof obj.restHours === 'object') {
      draft.restHours.confirmedBy = typeof obj.restHours.confirmedBy === 'string' ? obj.restHours.confirmedBy : '';
      draft.restHours.compliant = obj.restHours.compliant !== false;
      draft.restHours.note = typeof obj.restHours.note === 'string' ? obj.restHours.note : '';
    }
    if (obj.course && typeof obj.course === 'object') {
      draft.course.id = typeof obj.course.id === 'string' ? obj.course.id : '';
      draft.course.title = typeof obj.course.title === 'string' ? obj.course.title : '';
      draft.course.instructor = typeof obj.course.instructor === 'string' ? obj.course.instructor : '';
    }
  } catch {
    /* 損壞或無法存取:用空草稿 */
  }
  return draft;
}

export function saveSessionInfoDraft(draft: SessionInfoDraft, storage: Pick<Storage, 'setItem'> | null = typeof localStorage === 'undefined' ? null : localStorage): void {
  try {
    storage?.setItem(SESSION_INFO_DRAFT_KEY, JSON.stringify(draft));
  } catch {
    /* 配額或私密視窗:略過 */
  }
}

@customElement('brm-session-info-panel')
export class SessionInfoPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 12px;
      }
      th,
      td {
        padding: 2px 3px;
        text-align: left;
        border-bottom: 1px solid var(--ob-border);
        white-space: nowrap;
      }
      td input,
      td select {
        width: 100%;
        min-width: 0;
        box-sizing: border-box;
      }
      td.role {
        width: 110px;
      }
      td.rank,
      td.cert {
        width: 80px;
      }
      td.del {
        width: 1%;
      }
      .status {
        font-size: 12px;
        padding: 3px 6px;
        border-radius: 4px;
        border: 1px solid var(--ob-border-strong);
      }
      .status.ok {
        border-color: var(--ob-ok);
      }
      .status.bad {
        border-color: var(--ob-warning);
      }
      .issues {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      :host([compact]) table,
      :host([compact]) .form,
      :host([compact]) .status,
      :host([compact]) .hint {
        font-size: 14px;
      }
      :host([compact]) button {
        min-height: 36px;
        font-size: 15px;
      }
    `,
  ];

  /** 核心 state.utc(確認時刻的來源);無則瀏覽器 UTC */
  @property({ type: String }) simUtc = '';
  /** 最近一次送出的 sessionInfo(教官站保存;重新整理後由 localStorage 草稿還原表單但不視為已送出) */
  @property({ attribute: false }) submitted: SessionInfo | null = null;
  /** 簡化教官模式(字體放大) */
  @property({ type: Boolean, reflect: true }) compact = false;

  @state() private draft: SessionInfoDraft = emptySessionInfoDraft();
  @state() private issues: string[] = [];
  @state() private showIssues = false;

  override connectedCallback(): void {
    super.connectedCallback();
    this.draft = loadSessionInfoDraft();
    if (this.draft.participants.length === 0) this.draft.participants.push(this.newParticipant('master'));
  }

  private newParticipant(role: string): SessionParticipant {
    return { name: '', role, rank: '', certificateNo: '' };
  }

  private touch(): void {
    this.draft = { ...this.draft, participants: [...this.draft.participants] };
    if (this.showIssues) this.issues = validateSessionInfoDraft(this.draft);
    saveSessionInfoDraft(this.draft);
  }

  private addParticipant(): void {
    const used = new Set(this.draft.participants.map((p) => p.role));
    const next = SESSION_PARTICIPANT_ROLES.find((r) => !used.has(r)) ?? 'observer';
    this.draft.participants.push(this.newParticipant(next));
    this.touch();
  }

  private removeParticipant(i: number): void {
    this.draft.participants.splice(i, 1);
    this.touch();
  }

  private submit(): void {
    this.issues = validateSessionInfoDraft(this.draft);
    this.showIssues = true;
    if (this.issues.length > 0) return;
    const info = buildSessionInfo(this.draft, confirmationTimeUtc(this.simUtc || undefined));
    dispatchCommand(this, sessionInfoCommand(info));
    this.dispatchEvent(new CustomEvent<SessionInfo>('session-info-submitted', { detail: info, bubbles: true, composed: true }));
    saveSessionInfoDraft(this.draft);
    this.showIssues = false;
  }

  private clear(): void {
    this.draft = emptySessionInfoDraft();
    this.draft.participants.push(this.newParticipant('master'));
    this.issues = [];
    this.showIssues = false;
    saveSessionInfoDraft(this.draft);
    this.dispatchEvent(new CustomEvent('session-info-cleared', { bubbles: true, composed: true }));
  }

  private set<K extends keyof SessionInfoDraft['restHours']>(k: K, v: SessionInfoDraft['restHours'][K]): void {
    this.draft.restHours[k] = v;
    this.touch();
  }

  private setCourse(k: keyof SessionInfoDraft['course'], v: string): void {
    this.draft.course[k] = v;
    this.touch();
  }

  private val(e: Event): string {
    return (e.target as HTMLInputElement | HTMLSelectElement).value;
  }

  override render() {
    const rest = this.submitted?.restHoursConfirmation;
    return html`
      <div class="panel">
        <div class="panel-title"><span>參與者與休息時間確認 SESSION</span><span class="zh">規劃書第 5.5 節 · STCW A-VIII/1</span></div>
        <table class="participants" aria-label="參與者">
          <thead><tr><th>姓名</th><th>職務</th><th>職級</th><th>證書號</th><th></th></tr></thead>
          <tbody>
            ${this.draft.participants.map(
              (p, i) => html`<tr>
                <td><input class="p-name" .value=${p.name} placeholder="姓名" @input=${(e: Event) => ((p.name = this.val(e)), this.touch())} /></td>
                <td class="role"><select class="p-role" .value=${p.role} @change=${(e: Event) => ((p.role = this.val(e)), this.touch())}>${SESSION_PARTICIPANT_ROLES.map((r) => html`<option value=${r} ?selected=${r === p.role}>${SESSION_ROLE_LABEL[r]}</option>`)}</select></td>
                <td class="rank"><input class="p-rank" .value=${p.rank ?? ''} placeholder="職級" @input=${(e: Event) => ((p.rank = this.val(e)), this.touch())} /></td>
                <td class="cert"><input class="p-cert" .value=${p.certificateNo ?? ''} placeholder="選填" @input=${(e: Event) => ((p.certificateNo = this.val(e)), this.touch())} /></td>
                <td class="del"><button title="移除" @click=${() => this.removeParticipant(i)}>×</button></td>
              </tr>`,
            )}
          </tbody>
        </table>
        <div class="btn-row">
          <button class="add-participant" @click=${this.addParticipant}>新增參與者</button>
        </div>
        <div class="form">
          <label>確認人</label>
          <input class="rest-by" .value=${this.draft.restHours.confirmedBy} placeholder="船長/帶領者姓名" @input=${(e: Event) => this.set('confirmedBy', this.val(e))} />
          <label>休息時間</label>
          <label class="full"><input class="rest-compliant" type="checkbox" .checked=${this.draft.restHours.compliant} @change=${(e: Event) => this.set('compliant', (e.target as HTMLInputElement).checked)} /> 已對照船上工時/休息時間紀錄核對,符合 STCW A-VIII/1(MLC 2006 規則 2.3)與公司規定</label>
          <label>備註</label>
          <input class="rest-note" .value=${this.draft.restHours.note} placeholder="例外或補充(選填)" @input=${(e: Event) => this.set('note', this.val(e))} />
          <label>課程</label>
          <div class="btn-row">
            <input class="course-id" .value=${this.draft.course.id} placeholder="班次/課程編號" @input=${(e: Event) => this.setCourse('id', this.val(e))} />
            <input class="course-title" .value=${this.draft.course.title} placeholder="課程名稱" @input=${(e: Event) => this.setCourse('title', this.val(e))} />
            <input class="course-instructor" .value=${this.draft.course.instructor} placeholder="教官" @input=${(e: Event) => this.setCourse('instructor', this.val(e))} />
          </div>
        </div>
        <div class="hint">確認時刻取${this.simUtc ? `核心時鐘 ${this.simUtc}` : '瀏覽器 UTC(核心未回報 state.utc)'};送出後寫入教官站紀錄,核心同時寫入紀錄標頭</div>
        ${this.showIssues && this.issues.length > 0 ? html`<div class="issues">${this.issues.map((i) => html`<span class="hint bad">${i}</span>`)}</div>` : nothing}
        <div class="btn-row">
          <button class="primary submit" @click=${this.submit}>確認並送出 sessionInfo</button>
          <button class="clear" @click=${this.clear}>清除</button>
        </div>
        ${rest
          ? html`<div class="status ok">已送出:${rest.confirmedBy} 於 ${rest.confirmedAtUtc} 確認 ${rest.compliant ? '符合' : '不符合'} · 參與者 ${this.submitted?.participants.length ?? 0} 人</div>`
          : html`<div class="status bad">尚未送出休息時間確認(不阻擋練習;由訓練負責人決策)</div>`}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-session-info-panel': SessionInfoPanel;
  }
}
