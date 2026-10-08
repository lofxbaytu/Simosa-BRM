// 教官用控制(第一版先放同頁,規劃書第 9.1 節執行控制):凍結 / 恢復 / 重設。

import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import { simpleCommand } from '../lib/command.js';

@customElement('brm-instructor')
export class InstructorControls extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .panel {
        height: 100%;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .buttons {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 4px;
        flex: 1;
      }
      .buttons button {
        font-size: 13px;
        font-weight: 600;
      }
      .buttons .reset {
        grid-column: span 2;
      }
      .buttons .frozen {
        background: var(--ob-caution);
        color: #1a1600;
        border-color: var(--ob-caution);
      }
      .confirm {
        font-size: 11px;
        color: var(--ob-warning);
      }
    `,
  ];

  @property({ type: Boolean }) frozen = false;

  @state() private confirmReset = false;
  private confirmTimer: ReturnType<typeof setTimeout> | null = null;

  private freeze(): void {
    dispatchCommand(this, simpleCommand('freeze'));
  }

  private resume(): void {
    dispatchCommand(this, simpleCommand('resume'));
  }

  private reset(): void {
    if (!this.confirmReset) {
      // 兩段確認,避免誤觸(重設會清除練習進度)
      this.confirmReset = true;
      this.confirmTimer = setTimeout(() => (this.confirmReset = false), 4000);
      return;
    }
    if (this.confirmTimer) clearTimeout(this.confirmTimer);
    this.confirmReset = false;
    dispatchCommand(this, simpleCommand('reset'));
    this.dispatchEvent(new CustomEvent('sim-reset', { bubbles: true, composed: true }));
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.confirmTimer) clearTimeout(this.confirmTimer);
  }

  override render() {
    return html`
      <div class="panel">
        <div class="panel-title"><span>教官 INSTRUCTOR</span><span class="label">${this.frozen ? '已凍結' : '執行中'}</span></div>
        <div class="buttons">
          <button class=${this.frozen ? 'frozen' : ''} ?disabled=${this.frozen} @click=${this.freeze}>凍結</button>
          <button ?disabled=${!this.frozen} @click=${this.resume}>恢復</button>
          <button class="reset" @click=${this.reset}>${this.confirmReset ? '再按一次確認重設' : '重設'}</button>
        </div>
        ${this.confirmReset ? html`<div class="confirm">重設會回到練習初始狀態並清除航跡</div>` : html`<div class="label muted">第一版教官控制暫放本頁</div>`}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-instructor': InstructorControls;
  }
}
