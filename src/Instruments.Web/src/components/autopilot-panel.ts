// 自動舵面板:開/關、設定航向(±1/±10 與輸入框、採用現在航向)、ROT 限制;送 autopilot 指令。
// state.schema 沒有自動舵狀態欄位,故開關狀態由本面板保存並以 autopilot-changed 事件通知 <brm-app>。

import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import { autopilotCommand } from '../lib/command.js';
import { formatHeading } from '../lib/format.js';
import { clamp, normalizeHeading } from '../lib/angles.js';

export interface AutopilotChangedDetail {
  enabled: boolean;
  heading: number;
  rotLimit: number;
}

@customElement('brm-autopilot')
export class AutopilotPanel extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .panel {
        height: 100%;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .toggle {
        font-weight: 700;
        font-size: 14px;
        min-height: 34px;
      }
      .toggle.on {
        background: var(--ob-accent);
        color: var(--ob-text-on-accent);
        border-color: var(--ob-accent-strong);
      }
      .set {
        display: grid;
        grid-template-columns: auto auto 1fr auto auto;
        gap: 3px;
        align-items: center;
      }
      .set input {
        width: 100%;
        text-align: center;
        font-size: 18px;
        font-weight: 600;
        min-width: 0;
      }
      .rot {
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .rot input {
        width: 56px;
        text-align: center;
      }
      .cur {
        display: flex;
        justify-content: space-between;
        align-items: center;
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
    `,
  ];

  /** 目前航向(供「採用現在航向」) */
  @property({ type: Number }) heading = 0;
  @property({ type: Number }) rotLimitMax = 60;

  @state() private enabled = false;
  @state() private setHeading = 0;
  @state() private rotLimit = 15;

  /** 由外部(例如重設)關閉自動舵 */
  disable(): void {
    if (!this.enabled) return;
    this.enabled = false;
    this.notify();
  }

  private notify(): void {
    const detail: AutopilotChangedDetail = { enabled: this.enabled, heading: this.setHeading, rotLimit: this.rotLimit };
    this.dispatchEvent(new CustomEvent<AutopilotChangedDetail>('autopilot-changed', { detail, bubbles: true, composed: true }));
  }

  private send(): void {
    dispatchCommand(this, autopilotCommand({ enabled: this.enabled, heading: this.setHeading, rotLimit: this.rotLimit }));
    this.notify();
  }

  private toggle(): void {
    if (!this.enabled) {
      // 開啟時以現在航向為設定值(避免瞬間大轉向),使用者再調整
      this.setHeading = Math.round(normalizeHeading(this.heading));
    }
    this.enabled = !this.enabled;
    this.send();
  }

  private adjust(delta: number): void {
    this.setHeading = Math.round(normalizeHeading(this.setHeading + delta));
    if (this.enabled) this.send();
    else this.notify();
  }

  private onHeadingInput(ev: Event): void {
    const v = Number((ev.target as HTMLInputElement).value);
    if (!Number.isFinite(v)) return;
    this.setHeading = Math.round(normalizeHeading(v));
    if (this.enabled) this.send();
    else this.notify();
  }

  private onRotInput(ev: Event): void {
    const v = Number((ev.target as HTMLInputElement).value);
    if (!Number.isFinite(v)) return;
    this.rotLimit = Math.round(clamp(v, 1, this.rotLimitMax));
    if (this.enabled) this.send();
    else this.notify();
  }

  private useCurrent(): void {
    this.setHeading = Math.round(normalizeHeading(this.heading));
    if (this.enabled) this.send();
    else this.notify();
  }

  override render() {
    return html`
      <div class="panel">
        <div class="panel-title"><span>自動舵 AUTOPILOT</span><span class="label">${this.enabled ? 'AUTO' : 'HAND'}</span></div>
        <button class="toggle ${this.enabled ? 'on' : ''}" aria-pressed=${this.enabled} @click=${this.toggle}>
          ${this.enabled ? '自動舵 開' : '自動舵 關'}
        </button>
        <div class="set">
          <button title="−10°" @click=${() => this.adjust(-10)}>−10</button>
          <button title="−1°" @click=${() => this.adjust(-1)}>−1</button>
          <input type="number" min="0" max="359" step="1" aria-label="設定航向" .value=${String(this.setHeading)} @change=${this.onHeadingInput} />
          <button title="+1°" @click=${() => this.adjust(1)}>+1</button>
          <button title="+10°" @click=${() => this.adjust(10)}>+10</button>
        </div>
        <div class="rot">
          <span class="label">ROT 限制</span>
          <input type="number" min="1" max=${this.rotLimitMax} step="1" aria-label="ROT 限制" .value=${String(this.rotLimit)} @change=${this.onRotInput} />
          <span class="label">°/min</span>
        </div>
        <div class="cur">
          <span>設定 HDG ${formatHeading(this.setHeading)} · 現在 ${formatHeading(this.heading)}</span>
          <button @click=${this.useCurrent}>採用現在航向</button>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-autopilot': AutopilotPanel;
  }
  interface HTMLElementEventMap {
    'autopilot-changed': CustomEvent<AutopilotChangedDetail>;
  }
}
