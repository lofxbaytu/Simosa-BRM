// 車鐘:EFAS…NAVF 11 段,點選送 telegraph 指令;顯示核心回報的現行位置與對應轉速。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import { telegraphCommand } from '../lib/command.js';
import type { TelegraphStep } from '../lib/ship-config.js';
import type { TelegraphPosition } from '../types/state.js';

@customElement('brm-telegraph')
export class TelegraphControl extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        /* 尺寸容器:操船台只佔左欄寬度後這一格在 1280–1366 寬時約 155–175 px(@container) */
        container-type: inline-size;
      }
      .panel {
        height: 100%;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .list {
        flex: 1;
        display: grid;
        grid-template-rows: repeat(11, 1fr);
        gap: 2px;
        min-height: 0;
      }
      .list button {
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding: 0 8px;
        min-height: 0;
        font-size: 12px;
        line-height: 1;
        /* 窄格(1280–1366 寬的操船台約 155–175 px)也不換行;鈕很矮時(720 高每鈕約 13 px)文字不溢出到相鄰的鈕上 */
        white-space: nowrap;
        overflow: hidden;
        gap: 4px;
      }
      .list button.stop {
        border-color: var(--ob-accent-strong);
        font-weight: 700;
      }
      .list button.astern {
        background: color-mix(in srgb, var(--ob-warning) 12%, var(--ob-bg-surface-raised));
      }
      .list button.active {
        background: var(--ob-accent);
        color: var(--ob-text-on-accent);
      }
      .rpm {
        font-size: 10px;
        opacity: 0.8;
      }
      .zh {
        font-size: 11px;
        opacity: 0.85;
      }
      /* 窄格(內容寬 < 150 px):縮小左右內距,最長的「EFAS 緊急全速倒車 −150」留在一行內 */
      @container (max-width: 150px) {
        .list button {
          padding: 0 4px;
          gap: 2px;
        }
      }
    `,
  ];

  @property({ attribute: false }) steps: TelegraphStep[] = [];
  /** 核心回報的現行車鐘位置 */
  @property({ type: String }) position: TelegraphPosition = 'STOP';

  private select(p: TelegraphPosition): void {
    dispatchCommand(this, telegraphCommand(p));
  }

  override render() {
    // 由 NAVF(上)排到 EFAS(下),與實體車鐘一致
    const ordered = [...this.steps].reverse();
    return html`
      <div class="panel">
        <div class="panel-title"><span>車鐘 TELEGRAPH</span><span class="label">${this.position}</span></div>
        <div class="list" role="radiogroup" aria-label="車鐘">
          ${ordered.map(
            (s) => html`<button
              role="radio"
              aria-checked=${this.position === s.position}
              class="${this.position === s.position ? 'active' : ''} ${s.position === 'STOP' ? 'stop' : s.rpm < 0 ? 'astern' : ''}"
              @click=${() => this.select(s.position)}
            >
              <span><b>${s.position}</b> <span class="zh">${s.label}</span></span>
              <span class="rpm">${s.rpm}</span>
            </button>`,
          )}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-telegraph': TelegraphControl;
  }
}
