// 執行控制(規劃書第 9.1 節):凍結/恢復/重設(兩段確認)、時間倍率、快照/還原(列出最近快照)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, shortTime, wallTime } from './instructor-shared.js';
import { simpleCommand, timeScaleCommand } from '../../lib/command.js';
import { TIME_SCALES, restoreCommand, snapshotCommand } from '../../lib/instructor-command.js';

export interface SnapshotEntry {
  name: string;
  tick: number;
  t: number;
  wall: string;
}

@customElement('brm-run-control')
export class RunControlPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 4px;
      }
      .grid button {
        font-size: 13px;
        font-weight: 600;
        min-height: 32px;
      }
      .grid .reset {
        grid-column: span 2;
      }
      .seg {
        display: flex;
        border: 1px solid var(--ob-border-strong);
        border-radius: 4px;
        overflow: hidden;
      }
      .seg button {
        flex: 1;
        border: none;
        border-radius: 0;
      }
      .seg button + button {
        border-left: 1px solid var(--ob-border-strong);
      }
      .snap-list {
        max-height: 96px;
      }
      .snap-list button {
        min-height: 20px;
        padding: 0 6px;
        font-size: 11px;
      }
    `,
  ];

  @property({ type: Boolean }) frozen = false;
  @property({ type: Number }) tick = 0;
  @property({ type: Number }) simTime = 0;
  @property({ attribute: false }) snapshots: SnapshotEntry[] = [];
  /** 時間倍率由本頁保存(state.schema.json 無此欄位) */
  @property({ type: Number }) timeScale = 1;

  @state() private confirmReset = false;
  private confirmTimer: ReturnType<typeof setTimeout> | null = null;

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.confirmTimer) clearTimeout(this.confirmTimer);
  }

  private freeze(): void {
    dispatchCommand(this, simpleCommand('freeze'));
  }

  private resume(): void {
    dispatchCommand(this, simpleCommand('resume'));
  }

  private reset(): void {
    if (!this.confirmReset) {
      this.confirmReset = true;
      this.confirmTimer = setTimeout(() => (this.confirmReset = false), 4000);
      return;
    }
    if (this.confirmTimer) clearTimeout(this.confirmTimer);
    this.confirmReset = false;
    dispatchCommand(this, simpleCommand('reset'));
    this.dispatchEvent(new CustomEvent('sim-reset', { bubbles: true, composed: true }));
  }

  private setScale(scale: number): void {
    this.timeScale = scale;
    dispatchCommand(this, timeScaleCommand(scale));
    this.dispatchEvent(new CustomEvent<number>('time-scale-changed', { detail: scale, bubbles: true, composed: true }));
  }

  private snapshot(): void {
    const cmd = snapshotCommand(this.tick, this.simTime);
    dispatchCommand(this, cmd);
    const entry: SnapshotEntry = { name: String(cmd.value), tick: Math.trunc(this.tick), t: this.simTime, wall: new Date().toISOString() };
    this.dispatchEvent(new CustomEvent<SnapshotEntry>('snapshot-taken', { detail: entry, bubbles: true, composed: true }));
  }

  private restore(entry: SnapshotEntry): void {
    dispatchCommand(this, restoreCommand(entry.tick));
    this.dispatchEvent(new CustomEvent<SnapshotEntry>('snapshot-restored', { detail: entry, bubbles: true, composed: true }));
  }

  override render() {
    return html`
      <div class="panel">
        <div class="panel-title"><span>執行控制 RUN</span><span class="zh ${this.frozen ? 'caution' : ''}">${this.frozen ? '已凍結' : '執行中'} · ×${this.timeScale}</span></div>
        <div class="grid">
          <button class="freeze ${this.frozen ? 'frozen' : ''}" ?disabled=${this.frozen} @click=${this.freeze}>凍結</button>
          <button class="resume" ?disabled=${!this.frozen} @click=${this.resume}>恢復</button>
          <button class="reset ${this.confirmReset ? 'danger' : ''}" @click=${this.reset}>${this.confirmReset ? '再按一次確認重設' : '重設'}</button>
        </div>
        <div class="form">
          <label>時間倍率</label>
          <div class="seg" role="group" aria-label="時間倍率">
            ${TIME_SCALES.map((s) => html`<button class=${this.timeScale === s ? 'active' : ''} @click=${() => this.setScale(s)}>×${s}</button>`)}
          </div>
        </div>
        <div class="btn-row">
          <button class="snapshot" @click=${this.snapshot}>快照(${shortTime(this.simTime)})</button>
        </div>
        ${this.snapshots.length > 0
          ? html`<div class="list snap-list" aria-label="最近快照">
              ${[...this.snapshots]
                .slice(-8)
                .reverse()
                .map(
                  (s) => html`<div class="item">
                    <span class="time">${shortTime(s.t)}</span>
                    <span class="text muted">tick ${s.tick} · ${wallTime(s.wall)}</span>
                    <button @click=${() => this.restore(s)}>還原</button>
                  </div>`,
                )}
            </div>`
          : html`<div class="hint">尚無快照;核心每 10 s 也會自動快照(還原時回到該 tick 之前最近一份)</div>`}
        ${this.confirmReset ? html`<div class="hint bad">重設會回到情境初始狀態並清除航跡與操作紀錄</div>` : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-run-control': RunControlPanel;
  }
}
