// 艏側推桿:垂直拖曳 −1(左推)至 +1(右推),接近 0 自動歸零;鍵盤 ↑/↓;快速鍵按鈕。

import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import { thrusterCommand } from '../lib/command.js';
import { formatThruster } from '../lib/format.js';
import { clamp } from '../lib/angles.js';

const SNAP = 0.06;
const SEND_INTERVAL_MS = 60;

@customElement('brm-thruster-lever')
export class ThrusterLever extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .panel {
        height: 100%;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .body {
        flex: 1;
        display: flex;
        gap: 8px;
        min-height: 0;
      }
      .track {
        position: relative;
        width: 44px;
        flex: none;
        background: var(--ob-bg-input);
        border: 1px solid var(--ob-border-strong);
        border-radius: 6px;
        touch-action: none;
        cursor: ns-resize;
        outline: none;
      }
      .track:focus-visible {
        box-shadow: var(--ob-focus);
      }
      .center {
        position: absolute;
        left: 0;
        right: 0;
        top: 50%;
        border-top: 2px solid var(--ob-scale);
      }
      .fill {
        position: absolute;
        left: 8px;
        right: 8px;
        background: var(--ob-accent);
        opacity: 0.5;
      }
      .knob {
        position: absolute;
        left: 2px;
        right: 2px;
        height: 14px;
        margin-top: -7px;
        background: var(--ob-bg-surface-raised);
        border: 2px solid var(--ob-accent-strong);
        border-radius: 4px;
      }
      .side {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 4px;
        justify-content: center;
        min-width: 0;
      }
      .quick {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 3px;
      }
      .quick button {
        font-size: 11px;
        padding: 3px 4px;
      }
      .ends {
        display: flex;
        flex-direction: column;
        justify-content: space-between;
        font-size: 10px;
        color: var(--ob-text-secondary);
        text-align: center;
        width: 44px;
      }
    `,
  ];

  /** 核心回報的指令值 */
  @property({ type: Number }) order = 0;
  @property({ type: Number }) actual = 0;

  @state() private local = 0;
  @state() private dragging = false;
  private sendTimer: ReturnType<typeof setTimeout> | null = null;
  private pending: number | null = null;

  override willUpdate(changed: Map<string, unknown>): void {
    if (changed.has('order') && !this.dragging) this.local = this.order;
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.sendTimer) clearTimeout(this.sendTimer);
  }

  private set(v: number, immediate = false): void {
    let value = clamp(v, -1, 1);
    if (Math.abs(value) < SNAP) value = 0;
    value = Math.round(value * 100) / 100;
    this.local = value;
    this.pending = value;
    if (immediate) this.flush();
    else if (!this.sendTimer) {
      this.sendTimer = setTimeout(() => {
        this.sendTimer = null;
        this.flush();
      }, SEND_INTERVAL_MS);
    }
  }

  private flush(): void {
    if (this.pending === null) return;
    const v = this.pending;
    this.pending = null;
    dispatchCommand(this, thrusterCommand(v));
  }

  private valueFromEvent(ev: PointerEvent): number {
    const el = ev.currentTarget as HTMLElement;
    const r = el.getBoundingClientRect();
    // 上 = 右推(+1)…實體側推桿左右推;此處以上右下左呈現,並在兩端標示
    const f = (ev.clientY - r.top) / r.height;
    return clamp(1 - f * 2, -1, 1);
  }

  private onPointerDown = (ev: PointerEvent): void => {
    const el = ev.currentTarget as HTMLElement;
    el.setPointerCapture(ev.pointerId);
    el.focus();
    this.dragging = true;
    this.set(this.valueFromEvent(ev));
  };

  private onPointerMove = (ev: PointerEvent): void => {
    if (!this.dragging) return;
    this.set(this.valueFromEvent(ev));
  };

  private onPointerUp = (ev: PointerEvent): void => {
    if (!this.dragging) return;
    this.dragging = false;
    try {
      (ev.currentTarget as HTMLElement).releasePointerCapture(ev.pointerId);
    } catch {
      /* 忽略 */
    }
    this.flush();
  };

  private onKeyDown = (ev: KeyboardEvent): void => {
    const step = ev.shiftKey ? 0.25 : 0.05;
    switch (ev.key) {
      case 'ArrowUp':
        this.set(this.local + step, true);
        break;
      case 'ArrowDown':
        this.set(this.local - step, true);
        break;
      case '0':
      case 'Home':
        this.set(0, true);
        break;
      default:
        return;
    }
    ev.preventDefault();
  };

  override render() {
    const knobTop = 50 - this.local * 50;
    const fillTop = Math.min(50, knobTop);
    const fillH = Math.abs(50 - knobTop);
    return html`
      <div class="panel">
        <div class="panel-title"><span>艏側推 BOW THR</span><span class="label">實際 ${formatThruster(this.actual)}</span></div>
        <div class="body">
          <div class="ends"><span>S 右</span><span>P 左</span></div>
          <div
            class="track"
            tabindex="0"
            role="slider"
            aria-label="側推桿"
            aria-valuemin="-1"
            aria-valuemax="1"
            aria-valuenow=${this.local}
            title="拖曳;鍵盤 ↑/↓,0 歸零"
            @pointerdown=${this.onPointerDown}
            @pointermove=${this.onPointerMove}
            @pointerup=${this.onPointerUp}
            @pointercancel=${this.onPointerUp}
            @keydown=${this.onKeyDown}
          >
            <div class="fill" style="top:${fillTop}%;height:${fillH}%"></div>
            <div class="center"></div>
            <div class="knob" style="top:${knobTop}%"></div>
          </div>
          <div class="side">
            <div class="label">側推令</div>
            <div class="value mid" style="color: var(--ob-order)">${formatThruster(this.local)}</div>
            <div class="quick">
              <button @click=${() => this.set(-1, true)}>P 100</button>
              <button @click=${() => this.set(1, true)}>S 100</button>
              <button @click=${() => this.set(-0.5, true)}>P 50</button>
              <button @click=${() => this.set(0.5, true)}>S 50</button>
              <button style="grid-column: span 2" @click=${() => this.set(0, true)}>停止 0</button>
            </div>
          </div>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-thruster-lever': ThrusterLever;
  }
}
