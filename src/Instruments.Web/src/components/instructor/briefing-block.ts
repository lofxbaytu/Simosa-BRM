// 簡報結構區塊(規劃書第 9.1 節簡報內容:規劃時間、同儕評估提示;第 11 章講評引導問題;R7):
// 情境面板詳細區(完整教官站)與簡化教官模式的簡報頁共用。規劃時間為教官站本地倒數計時器(開始/暫停/重設;剩 1 分鐘變色;
// 到時顯示「規劃時間結束」並發 planning-time-ended 事件,教官站記入學員操作紀錄),不送核心指令、不影響紀錄雜湊。
// mode="debrief" 時只列講評引導問題與同儕評估提示(練習結束頁的講評摘要旁),兩者預設展開。
// 情境沒有這三個欄位時整個區塊不顯示(既有情境畫面不變)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { PropertyValues } from 'lit';
import { instructorStyles } from './instructor-shared.js';
import type { ScenarioBriefing } from '../../lib/timeline.js';
import { PlanningTimer, formatCountdown } from '../../lib/planning-timer.js';

export interface PlanningTimeEndedDetail {
  /** 規劃時間總長(分鐘) */
  planningTimeMin: number;
}
export type PlanningTimeEndedEvent = CustomEvent<PlanningTimeEndedDetail>;

/** 倒數重繪間隔(毫秒;顯示整秒即可) */
const TICK_MS = 250;

/** 簡報是否有結構欄位可顯示(規劃時間 > 0、提示或引導問題) */
export function hasBriefingGuide(b: ScenarioBriefing | null | undefined): boolean {
  return !!b && ((b.planningTimeMin ?? 0) > 0 || b.peerAssessmentPrompts.length > 0 || b.debriefQuestions.length > 0);
}

@customElement('brm-briefing-block')
export class BriefingBlock extends LitElement {
  static override styles = [
    instructorStyles,
    css`
      :host {
        display: block;
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      :host([large]) {
        font-size: 16px;
      }
      .block {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .timer {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 4px 8px;
      }
      .timer .label {
        font-weight: 600;
        color: var(--ob-text);
      }
      .countdown {
        font-variant-numeric: tabular-nums;
        font-weight: 700;
        font-size: 1.6em;
        color: var(--ob-text);
        min-width: 4.5ch;
      }
      :host([large]) .countdown {
        font-size: 2.4em;
      }
      .countdown.warning {
        color: var(--ob-warning);
      }
      .countdown.ended,
      .ended-text {
        color: var(--ob-alarm);
        font-weight: 700;
      }
      .timer button {
        font-size: 1em;
        padding: 2px 10px;
        min-height: 24px;
      }
      :host([large]) .timer button {
        font-size: 1em;
        min-height: 40px;
        padding: 4px 16px;
      }
      details {
        border-left: 2px solid var(--ob-border);
        padding-left: 8px;
      }
      summary {
        cursor: pointer;
        color: var(--ob-text);
        font-weight: 600;
        user-select: none;
      }
      summary .count {
        color: var(--ob-text-muted);
        font-weight: 400;
        margin-left: 4px;
      }
      ol {
        margin: 2px 0 2px 0;
        padding-left: 1.6em;
        color: var(--ob-text);
      }
      ol li {
        margin: 1px 0;
        white-space: pre-wrap;
      }
    `,
  ];

  /** 情境簡報物件(parseBriefing 的結果);null 或無結構欄位時不顯示 */
  @property({ attribute: false }) briefing: ScenarioBriefing | null = null;
  /** briefing:計時器 + 提示 + 引導問題(可摺疊);debrief:只列引導問題與提示(展開) */
  @property({ type: String }) mode: 'briefing' | 'debrief' = 'briefing';
  /** 簡化教官模式大字 */
  @property({ type: Boolean, reflect: true }) large = false;
  /** 毫秒時鐘(測試注入;預設 Date.now) */
  @property({ attribute: false }) now: () => number = () => Date.now();

  @state() private timer: PlanningTimer | null = null;
  /** 每次 tick 遞增以觸發重繪 */
  @state() private tickSeq = 0;
  private interval: ReturnType<typeof setInterval> | null = null;
  private timerMinutes = 0;

  /** 目前剩餘秒數(測試/其他面板讀取;無計時器時 null) */
  get remainingSeconds(): number | null {
    return this.timer?.enabled ? this.timer.remaining_s(this.now()) : null;
  }

  override willUpdate(changed: PropertyValues<this>): void {
    if (changed.has('briefing')) {
      const minutes = this.briefing?.planningTimeMin ?? 0;
      // 換情境或規劃時間改變才重建計時器(同一情境重新渲染不打斷倒數)
      if (!this.timer || minutes !== this.timerMinutes) {
        this.stopTicking();
        this.timerMinutes = minutes;
        this.timer = minutes > 0 ? new PlanningTimer(minutes) : null;
      }
    }
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.stopTicking();
  }

  /** 開始/繼續倒數(本地) */
  start(): void {
    if (!this.timer || !this.timer.start(this.now())) return;
    this.startTicking();
    this.tickSeq++;
  }

  pause(): void {
    if (!this.timer || !this.timer.pause(this.now())) return;
    this.stopTicking();
    this.tickSeq++;
  }

  reset(): void {
    if (!this.timer) return;
    this.timer.reset();
    this.stopTicking();
    this.tickSeq++;
  }

  /** 推進一次(interval 與測試呼叫);到時發 planning-time-ended。 */
  tick(): void {
    if (!this.timer) return;
    if (this.timer.tick(this.now())) {
      this.stopTicking();
      this.dispatchEvent(
        new CustomEvent<PlanningTimeEndedDetail>('planning-time-ended', { detail: { planningTimeMin: this.timerMinutes }, bubbles: true, composed: true }),
      );
    }
    this.tickSeq++;
  }

  private startTicking(): void {
    if (this.interval) return;
    this.interval = setInterval(() => this.tick(), TICK_MS);
  }

  private stopTicking(): void {
    if (this.interval) {
      clearInterval(this.interval);
      this.interval = null;
    }
  }

  override render() {
    const b = this.briefing;
    if (!hasBriefingGuide(b)) return nothing;
    void this.tickSeq;
    if (this.mode === 'debrief') {
      return html`<div class="block debrief">
        ${this.renderList('questions', '講評引導問題', b!.debriefQuestions, true)}
        ${this.renderList('prompts', '同儕評估提示', b!.peerAssessmentPrompts, true)}
      </div>`;
    }
    return html`<div class="block">
      ${this.timer ? this.renderTimer(this.timer) : nothing}
      ${this.renderList('prompts', '同儕評估提示', b!.peerAssessmentPrompts, true)}
      ${this.renderList('questions', '講評引導問題', b!.debriefQuestions, false)}
    </div>`;
  }

  private renderTimer(timer: PlanningTimer) {
    const v = timer.view(this.now());
    const cls = v.phase === 'ended' ? 'ended' : v.warning ? 'warning' : '';
    return html`<div class="timer" role="group" aria-label="規劃時間">
      <span class="label">規劃時間 ${this.timerMinutes} 分鐘</span>
      <span class="countdown ${cls}" role="timer" aria-live=${v.warning || v.phase === 'ended' ? 'assertive' : 'off'}>${formatCountdown(v.remaining_s)}</span>
      ${v.phase === 'ended'
        ? html`<span class="ended-text" role="status">規劃時間結束</span>`
        : v.phase === 'running'
          ? html`<button class="pause" @click=${this.pause}>暫停</button>`
          : html`<button class="primary start" @click=${this.start}>${v.phase === 'paused' ? '繼續' : '開始規劃時間'}</button>`}
      ${v.phase !== 'idle' ? html`<button class="reset" @click=${this.reset}>重設</button>` : nothing}
      ${v.phase === 'paused' ? html`<span class="muted">已暫停</span>` : nothing}
    </div>`;
  }

  private renderList(cls: string, title: string, items: readonly string[], open: boolean) {
    if (items.length === 0) return nothing;
    return html`<details class=${cls} ?open=${open}>
      <summary>${title}<span class="count">${items.length} 條</span></summary>
      <ol>
        ${items.map((q) => html`<li>${q}</li>`)}
      </ol>
    </details>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-briefing-block': BriefingBlock;
  }
  interface HTMLElementEventMap {
    'planning-time-ended': PlanningTimeEndedEvent;
  }
}
