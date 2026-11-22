// 自動舵面板:開/關、設定航向(±1/±10 與輸入框、採用現在航向)、ROT 限制;送 autopilot 指令。
// 開關狀態由本面板保存並以 autopilot-changed 事件通知 <brm-app>;C# 核心另有 state.autopilot(sensors-alerts.md 第 2.3 節):
// available = false(電羅經或自動舵失效)時停用「開」並標「不可用」,核心脫開自動舵時 <brm-app> 呼叫 disable() 同步。

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
      :host {
        /* 尺寸容器:操船台只佔左欄寬度後這一格在 1366 寬時約 200 px(@container) */
        container-type: inline-size;
      }
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
        /* 放不下時「採用現在航向」鈕換到下一行(不擠壓文字、不被裁掉) */
        flex-wrap: wrap;
        gap: 2px 6px;
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      /* 窄格(內容寬 < 240 px):±1 / ±10 鈕與航向輸入框只縮左右內距,整列留在格內;字級與鈕 / 輸入框的高度不變 */
      @container (max-width: 240px) {
        .panel {
          gap: 4px;
        }
        .set button {
          padding: 4px 4px;
          min-width: 0;
        }
        .set input {
          padding: 3px 2px;
        }
      }
      /* 更窄(< 180 px,1093 寬的視窗):±10 移到第二列(−10 在左半、+10 在右半),第一列是 −1、航向輸入框、+1,輸入框放得下三位數;
         面板比格子高時(1093×614 約多 6–22 px)在面板內捲動,「採用現在航向」不被格子裁掉 */
      @container (max-width: 180px) {
        .panel {
          overflow-y: auto;
          scrollbar-width: thin;
        }
        .set {
          grid-template-columns: auto 1fr 1fr auto;
          grid-template-areas:
            'm1 in in p1'
            'm10 m10 p10 p10';
        }
        .set .m10 {
          grid-area: m10;
        }
        .set .m1 {
          grid-area: m1;
        }
        .set input {
          grid-area: in;
        }
        .set .p1 {
          grid-area: p1;
        }
        .set .p10 {
          grid-area: p10;
        }
      }
    `,
  ];

  /** 目前航向(供「採用現在航向」) */
  @property({ type: Number }) heading = 0;
  @property({ type: Number }) rotLimitMax = 60;
  /** 核心回報的自動舵可用性(state.autopilot.available;核心未回報時 true) */
  @property({ type: Boolean }) available = true;

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
        <div class="panel-title"><span>自動舵 AUTOPILOT</span><span class="label ${this.available ? '' : 'alarm'}">${this.available ? (this.enabled ? 'AUTO' : 'HAND') : '不可用'}</span></div>
        <button class="toggle ${this.enabled ? 'on' : ''}" aria-pressed=${this.enabled} ?disabled=${!this.available && !this.enabled} title=${this.available ? '' : '電羅經或自動舵失效,自動舵不可啟用'} @click=${this.toggle}>
          ${this.enabled ? '自動舵 開' : this.available ? '自動舵 關' : '自動舵 不可用'}
        </button>
        <div class="set">
          <button class="m10" title="−10°" @click=${() => this.adjust(-10)}>−10</button>
          <button class="m1" title="−1°" @click=${() => this.adjust(-1)}>−1</button>
          <input type="number" min="0" max="359" step="1" aria-label="設定航向" .value=${String(this.setHeading)} @change=${this.onHeadingInput} />
          <button class="p1" title="+1°" @click=${() => this.adjust(1)}>+1</button>
          <button class="p10" title="+10°" @click=${() => this.adjust(10)}>+10</button>
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
