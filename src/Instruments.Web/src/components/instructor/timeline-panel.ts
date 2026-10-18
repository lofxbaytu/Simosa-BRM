// 時間表面板(設計文件第 6 節 W3、W10;規劃書第 9.1 節注入時間表):
//  - 由情境 YAML 解析的 timeline[] 列出 id/名稱/條件摘要/動作摘要與狀態(待觸發、已觸發 n 次、已停用);
//    狀態來源:timelineTriggered/timelineArmed/timelineDisarmed 事件 + state.scenario.timelineFired/Pending;
//  - 手動觸發鈕:manual 項目直接送 triggerTimeline;其他項目需按住「強制」0.8 s(force: true);
//  - acknowledge 快速鈕列(口頭辨識/通知機控室/呼叫船長/報告/切換操舵模式/備錨)含角色與備註;
//  - 必要事件倒數:after 事件(例如 faultInjected)發生後依 assessment.required 的 within_s 倒數,
//    requiredEventMet/Missed 事件更新為達成/逾時。within_s 為參數引用(教官站不解析)時改由事件 data 取得:
//    規則 id 的事件(含核心「開始計時」事件)或 after 事件本身的 data.deadline_t 優先,否則 data.within_s + 該事件 t,
//    都沒有就維持「計時中」(對兩種欄位名寬容,見設計文件第 3.2 節事件表)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { PropertyValues } from 'lit';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import type { SimEvent } from '../../types/event.js';
import type { ScenarioStatus } from '../../types/state.js';
import { actionSummary, conditionSummary, formatNumberOrParam, type TimelineItem } from '../../lib/timeline.js';
import type { AssessmentRules, ExpectItem, RequiredEventRule } from '../../lib/assessment.js';
import { acknowledgeCommand, triggerTimelineCommand } from '../../lib/instructor-command.js';
import { ACK_KIND_LABEL, ROLE_LABEL } from '../../lib/sim-event.js';

export const HOLD_MS = 800;

/** 確認快速鈕;gyroCheck = 羅經交叉核對發現異常(E08,sensors-alerts.md 第 8 節) */
const QUICK_ACKS = ['verbal', 'notifyEcr', 'callMaster', 'report', 'steeringMode', 'anchorReady', 'checklist', 'gyroCheck'] as const;
const ROLES = ['officer', 'master', 'pilot', 'helmsman', 'lookout', 'leader'] as const;

export interface TimelineItemStatus {
  item: TimelineItem;
  fireCount: number;
  lastFiredAt_s: number | null;
  armed: boolean;
  /** 仍可能(再)觸發 */
  pending: boolean;
  /** 最近一次手動觸發被核心拒絕 */
  rejected: boolean;
}

export interface RequiredCountdown {
  rule: RequiredEventRule;
  /** after 事件發生的時間;null = 尚未開始(不適用) */
  startedAt_s: number | null;
  /** 有效時限(秒):規則數值,或由事件 data 的 deadline_t / within_s 推得;皆無為 null */
  within_s: number | null;
  /** 期限的模擬時刻(秒);within_s 為 null 時亦為 null */
  deadline_t: number | null;
  state: 'inactive' | 'counting' | 'overdue' | 'met' | 'missed' | 'late';
  /** 剩餘秒數(counting)或逾時秒數(overdue) */
  remaining_s: number | null;
  by?: string;
}

/** 由事件推得每個時間表項目的狀態。 */
export function timelineStatuses(timeline: readonly TimelineItem[], events: readonly SimEvent[]): TimelineItemStatus[] {
  return timeline.map((item) => {
    let fireCount = 0;
    let lastFiredAt: number | null = null;
    let armed = item.armed;
    let rejected = false;
    for (const e of events) {
      if (e.itemId !== item.id) continue;
      if (e.kind === 'timelineTriggered') {
        fireCount++;
        lastFiredAt = e.t;
        rejected = false;
      } else if (e.kind === 'timelineArmed') armed = true;
      else if (e.kind === 'timelineDisarmed') armed = false;
      else if (e.kind === 'timelineManual') rejected = e.data?.['ok'] === false;
    }
    const exhausted = item.repeat.mode === 'once' ? fireCount > 0 : item.repeat.mode === 'every' && item.repeat.max !== undefined ? fireCount >= item.repeat.max : false;
    return { item, fireCount, lastFiredAt_s: lastFiredAt, armed, pending: armed && !exhausted, rejected };
  });
}

function afterMatches(e: SimEvent, rule: RequiredEventRule): boolean {
  const a = rule.after;
  if (!a || e.kind !== a.event) return false;
  if (a.itemId !== undefined && e.itemId !== a.itemId) return false;
  if (a.targetId !== undefined && e.targetId !== a.targetId) return false;
  if (a.waypoint !== undefined && e.data?.['waypoint'] !== a.waypoint) return false;
  return true;
}

/** 必要事件倒數:以最後一次 after 事件起算;之後的 requiredEventMet/Missed(itemId = 規則 id)決定狀態。 */
export function requiredCountdowns(rules: AssessmentRules, events: readonly SimEvent[], simTime: number): RequiredCountdown[] {
  return rules.required.map((rule) => {
    let startedAt: number | null = null;
    let startIdx = -1;
    events.forEach((e, i) => {
      if (afterMatches(e, rule)) {
        startedAt = e.t;
        startIdx = i;
      }
    });
    const ruleWithin = rule.within_s?.value ?? null;
    const out: RequiredCountdown = { rule, startedAt_s: startedAt, within_s: ruleWithin, deadline_t: null, state: 'inactive', remaining_s: null };
    if (startedAt === null) return out;
    // 期限:規則數值優先;否則從 after 事件本身與之後規則 id 的事件 data 取 deadline_t(優先)或 within_s(+ 該事件 t)
    let deadline: number | null = ruleWithin !== null ? startedAt + ruleWithin : null;
    const fromEventData = (e: SimEvent): void => {
      if (ruleWithin !== null) return;
      const d = e.data;
      if (!d) return;
      const dl = d['deadline_t'];
      if (typeof dl === 'number' && Number.isFinite(dl)) {
        deadline = dl;
        return;
      }
      const w = d['within_s'];
      if (typeof w === 'number' && Number.isFinite(w) && deadline === null) deadline = e.t + w;
    };
    fromEventData(events[startIdx]!);
    for (let i = startIdx + 1; i < events.length; i++) {
      const e = events[i]!;
      if (e.itemId !== rule.id) continue;
      // Met/Missed 的 within_s 是自 after 事件起算的時限(不是自 Met/Missed 事件起算)
      if (e.kind === 'requiredEventMet' || e.kind === 'requiredEventMissed') {
        const w = e.data?.['within_s'];
        if (ruleWithin === null && deadline === null && typeof w === 'number' && Number.isFinite(w)) deadline = startedAt + w;
      } else fromEventData(e);
      if (e.kind === 'requiredEventMet') {
        out.state = e.data?.['late'] === true ? 'late' : 'met';
        const by = e.data?.['by'];
        if (typeof by === 'string') out.by = by;
        return out;
      }
      if (e.kind === 'requiredEventMissed') out.state = 'missed';
    }
    if (deadline !== null) {
      out.deadline_t = deadline;
      out.within_s = deadline - startedAt;
    }
    if (out.state === 'missed') return out;
    if (deadline === null) {
      out.state = 'counting';
      return out;
    }
    const remaining = deadline - simTime;
    out.state = remaining >= 0 ? 'counting' : 'overdue';
    out.remaining_s = Math.abs(remaining);
    return out;
  });
}

function expectLabel(items: ExpectItem[]): string {
  const EXPECT_LABEL: Record<string, string> = {
    rudderOrder: '舵令',
    telegraphChange: '車令',
    rpmOrder: '轉速令',
    thrusterOrder: '側推',
    steeringModeChange: '切換操舵模式',
    autopilotOn: '自動舵開',
    autopilotOff: '自動舵關',
    acknowledge: '確認',
  };
  return items
    .map((it) => (it.type === 'action' ? (EXPECT_LABEL[it.name] ?? it.name) : it.type === 'acknowledge' ? `確認:${ACK_KIND_LABEL[it.kind] ?? it.kind}` : `事件 ${it.kind}${it.itemId ? ` ${it.itemId}` : ''}`))
    .join(' / ');
}

@customElement('brm-timeline-panel')
export class TimelinePanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
        min-height: 0;
      }
      .panel {
        height: 100%;
      }
      .item {
        flex-direction: column;
        align-items: stretch;
        gap: 1px;
      }
      .item .head {
        display: flex;
        gap: 6px;
        align-items: center;
      }
      .item .id {
        font-weight: 600;
        font-variant-numeric: tabular-nums;
      }
      .item .name {
        flex: 1;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .item .cond,
      .item .acts {
        font-size: 11px;
        color: var(--ob-text-muted);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .item.fired {
        border-color: var(--ob-ok);
      }
      .item.disarmed {
        opacity: 0.55;
      }
      .item.manual .head .id::before {
        content: '手動 ';
        font-size: 10px;
        color: var(--ob-accent);
      }
      .item button {
        min-height: 20px;
        padding: 0 6px;
        font-size: 11px;
      }
      button.force {
        border-color: var(--ob-warning);
        color: var(--ob-warning);
      }
      button.force.holding {
        background: var(--ob-warning);
        color: #1a1000;
      }
      .status {
        font-size: 10px;
        white-space: nowrap;
      }
      .section {
        font-size: 11px;
        font-weight: 600;
        color: var(--ob-text-secondary);
        margin-top: 4px;
      }
      .ack-row {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 3px;
      }
      .ack-row button {
        min-height: 24px;
        font-size: 11px;
        padding: 2px 4px;
      }
      .ack-form {
        display: flex;
        gap: 4px;
      }
      .ack-form select,
      .ack-form input {
        min-height: 22px;
        font-size: 11px;
        min-width: 0;
      }
      .ack-form input {
        flex: 1;
      }
      .countdown {
        display: flex;
        gap: 6px;
        align-items: baseline;
        font-size: 12px;
        padding: 2px 4px;
        border-radius: 3px;
        border: 1px solid var(--ob-border);
      }
      .countdown .rid {
        font-weight: 600;
      }
      .countdown .exp {
        flex: 1;
        min-width: 0;
        color: var(--ob-text-muted);
        font-size: 11px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .countdown .rem {
        font-variant-numeric: tabular-nums;
        font-weight: 700;
        white-space: nowrap;
      }
      .countdown.counting {
        border-color: var(--ob-caution);
      }
      .countdown.overdue,
      .countdown.missed {
        border-color: var(--ob-alarm);
      }
      .countdown.overdue .rem,
      .countdown.missed .rem {
        color: var(--ob-alarm);
      }
      .countdown.met .rem,
      .countdown.late .rem {
        color: var(--ob-ok);
      }
    `,
  ];

  @property({ attribute: false }) timeline: readonly TimelineItem[] = [];
  @property({ attribute: false }) events: readonly SimEvent[] = [];
  @property({ attribute: false }) scenarioStatus: ScenarioStatus | null = null;
  @property({ attribute: false }) assessment: AssessmentRules | null = null;
  @property({ type: Number }) simTime = 0;
  /** 核心不支援時間表(Python 參考伺服器):只顯示,停用觸發鈕 */
  @property({ type: Boolean }) unsupported = false;
  /** 只顯示倒數與確認鈕(簡化教官模式) */
  @property({ type: Boolean }) compact = false;

  @state() private holdingId: string | null = null;
  @state() private ackRole: string = 'officer';
  @state() private note = '';
  @state() private ackItemId = '';
  private holdTimer: ReturnType<typeof setTimeout> | null = null;

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.cancelHold();
  }

  private trigger(id: string, force: boolean): void {
    dispatchCommand(this, triggerTimelineCommand(id, force));
    this.dispatchEvent(new CustomEvent<{ id: string; force: boolean }>('timeline-trigger', { detail: { id, force }, bubbles: true, composed: true }));
  }

  private startHold(id: string): void {
    this.cancelHold();
    this.holdingId = id;
    this.holdTimer = setTimeout(() => {
      this.holdTimer = null;
      if (this.holdingId === id) this.trigger(id, true);
      this.holdingId = null;
    }, HOLD_MS);
  }

  private cancelHold(): void {
    if (this.holdTimer) {
      clearTimeout(this.holdTimer);
      this.holdTimer = null;
    }
    this.holdingId = null;
  }

  /**
   * 快速鈕:常用種類 + 本情境必要事件 expect 用到的 acknowledge 種類(例如 F01 的 prediction、E03 的 lookoutReport、
   * E10 的 driftCalculation),否則教官只能送「其他」,這些必要事件永遠無法達成。
   */
  quickAcks(): string[] {
    const kinds = new Set<string>(QUICK_ACKS);
    for (const r of this.assessment?.required ?? [])
      for (const e of r.expect) if (e.type === 'acknowledge' && e.kind !== 'other') kinds.add(e.kind);
    return [...kinds];
  }

  private ack(kind: string): void {
    const input: Parameters<typeof acknowledgeCommand>[0] = { kind, role: this.ackRole };
    if (this.note.trim()) input.note = this.note.trim();
    // 只在「對應項目」仍可選(已觸發的故障項目)時才附上 itemId,避免重設/換情境後帶著過期的 itemId
    if (this.ackItemId && this.faultItems(timelineStatuses(this.timeline, this.events)).some((f) => f.item.id === this.ackItemId)) input.itemId = this.ackItemId;
    dispatchCommand(this, acknowledgeCommand(input));
    this.note = '';
  }

  /** 可作為確認對象的項目:已觸發且含故障注入動作者。 */
  private faultItems(statuses: TimelineItemStatus[]): TimelineItemStatus[] {
    return statuses.filter((s) => s.fireCount > 0 && s.item.actions.some((a) => a.kind === 'injectFault'));
  }

  /** 事件清空/換情境使下拉選單消失時,一併清掉已選的對應項目。 */
  override willUpdate(changed: PropertyValues<this>): void {
    if ((changed.has('events') || changed.has('timeline')) && this.ackItemId) {
      const faultItems = this.faultItems(timelineStatuses(this.timeline, this.events));
      if (!faultItems.some((f) => f.item.id === this.ackItemId)) this.ackItemId = '';
    }
  }

  override render() {
    const statuses = timelineStatuses(this.timeline, this.events);
    const countdowns = this.assessment ? requiredCountdowns(this.assessment, this.events, this.simTime) : [];
    const active = countdowns.filter((c) => c.state !== 'inactive');
    const st = this.scenarioStatus;
    const fired = st?.timelineFired ?? statuses.filter((s) => s.fireCount > 0).length;
    // 「待觸發」= armed 且尚未達 once/max 的項目數,由事件推導;核心的 state.scenario.timelinePending 是 delay_s 排程中待執行的動作數(另顯示)
    const pending = statuses.filter((s) => s.pending).length;
    const delayed = st?.timelinePending ?? 0;
    const faultItems = this.faultItems(statuses);
    return html`
      <div class="panel">
        <div class="panel-title">
          <span>時間表 TIMELINE</span>
          <span class="zh">${this.timeline.length === 0 ? '情境無時間表' : `已觸發 ${fired} · 待觸發 ${pending}`}${delayed > 0 ? ` · 延遲動作 ${delayed}` : ''}${st?.phase && st.phase !== 'normal' ? html` · <span class="warning">階段 ${st.phase}</span>` : nothing}${this.unsupported ? ' · 核心不支援,僅顯示' : ''}</span>
        </div>
        ${this.compact
          ? nothing
          : html`<div class="list" aria-label="時間表項目">
              ${statuses.map((s) => this.renderItem(s))}
            </div>`}
        ${active.length > 0
          ? html`<div class="section">必要事件倒數</div>
              ${active.map((c) => this.renderCountdown(c))}`
          : nothing}
        <div class="section">確認(核心偵測不到的動作)</div>
        <div class="ack-row" role="group" aria-label="確認快速鈕">
          ${this.quickAcks().map((k) => html`<button class="ack" data-ack=${k} ?disabled=${this.unsupported} @click=${() => this.ack(k)}>${ACK_KIND_LABEL[k] ?? k}</button>`)}
          <button class="ack" data-ack="other" ?disabled=${this.unsupported} @click=${() => this.ack('other')}>其他</button>
        </div>
        <div class="ack-form">
          <select class="role" .value=${this.ackRole} @change=${(e: Event) => (this.ackRole = (e.target as HTMLSelectElement).value)} aria-label="角色">
            ${ROLES.map((r) => html`<option value=${r} ?selected=${r === this.ackRole}>${ROLE_LABEL[r] ?? r}</option>`)}
          </select>
          ${faultItems.length > 0
            ? html`<select class="ack-item" .value=${this.ackItemId} @change=${(e: Event) => (this.ackItemId = (e.target as HTMLSelectElement).value)} aria-label="對應項目">
                <option value="">自動對應</option>
                ${faultItems.map((f) => html`<option value=${f.item.id} ?selected=${f.item.id === this.ackItemId}>${f.item.id}</option>`)}
              </select>`
            : nothing}
          <input class="note" placeholder="備註(選填)" .value=${this.note} @input=${(e: Event) => (this.note = (e.target as HTMLInputElement).value)} />
        </div>
      </div>
    `;
  }

  private renderItem(s: TimelineItemStatus) {
    const it = s.item;
    const statusText = !s.armed ? '已停用' : s.fireCount > 0 ? `已觸發 ${s.fireCount} 次${s.lastFiredAt_s !== null ? ` @${shortTime(s.lastFiredAt_s)}` : ''}` : it.manual ? '待手動' : '待觸發';
    const repeat = it.repeat.mode === 'always' ? '每次' : it.repeat.mode === 'every' ? `每 ${it.repeat.every_s} s${it.repeat.max !== undefined ? ` ≤ ${it.repeat.max}` : ''}` : '';
    return html`<div class="item ${s.fireCount > 0 ? 'fired' : ''} ${s.armed ? '' : 'disarmed'} ${it.manual ? 'manual' : ''}" data-item=${it.id} title=${it.description || it.name}>
      <div class="head">
        <span class="id">${it.id}</span>
        <span class="name">${it.name}${it.phase ? html` <span class="tag">${it.phase}</span>` : nothing}${it.visibility === 'all' ? html`<span class="tag">學員可見</span>` : nothing}${repeat ? html`<span class="tag">${repeat}</span>` : nothing}</span>
        <span class="status ${s.rejected ? 'bad' : s.fireCount > 0 ? 'ok' : 'muted'}">${s.rejected ? '核心拒絕' : statusText}</span>
        ${it.manual
          ? html`<button class="trigger" ?disabled=${this.unsupported || !s.pending} @click=${() => this.trigger(it.id, false)}>觸發</button>`
          : html`<button
              class="force ${this.holdingId === it.id ? 'holding' : ''}"
              ?disabled=${this.unsupported}
              title="按住 ${HOLD_MS / 1000} 秒強制觸發(跳過條件)"
              @pointerdown=${() => this.startHold(it.id)}
              @pointerup=${this.cancelHold}
              @pointerleave=${this.cancelHold}
              @pointercancel=${this.cancelHold}
            >
              ${this.holdingId === it.id ? '按住…' : '強制'}
            </button>`}
      </div>
      <div class="cond">條件:${it.manual && !it.when ? '手動觸發' : conditionSummary(it.when)}${it.holdFor_s > 0 ? `(需持續 ${it.holdFor_s} s)` : ''}${it.delay_s > 0 ? `,延遲 ${it.delay_s} s` : ''}</div>
      <div class="acts">動作:${it.actions.map(actionSummary).join(';')}</div>
    </div>`;
  }

  private renderCountdown(c: RequiredCountdown) {
    const r = c.rule;
    let rem = '';
    switch (c.state) {
      case 'counting':
        rem = c.remaining_s === null ? '計時中' : `剩 ${Math.ceil(c.remaining_s)} s`;
        break;
      case 'overdue':
        rem = `逾時 ${Math.floor(c.remaining_s ?? 0)} s`;
        break;
      case 'met':
        rem = `達成${c.by ? `(${c.by})` : ''}`;
        break;
      case 'late':
        rem = '逾時後達成';
        break;
      case 'missed':
        rem = '逾時';
        break;
      default:
        rem = '';
    }
    const within = c.within_s !== null ? `限 ${Math.round(c.within_s)} s` : r.within_s ? formatNumberOrParam(r.within_s, 's', 0) : '無時限';
    return html`<div class="countdown ${c.state}" data-rule=${r.id}>
      <span class="rid">${r.id}</span>
      <span class="exp" title=${expectLabel(r.expect)}>${expectLabel(r.expect)} · ${within}${c.startedAt_s !== null ? ` · 自 ${shortTime(c.startedAt_s)}` : ''}</span>
      <span class="rem">${rem}</span>
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-timeline-panel': TimelinePanel;
  }
}
