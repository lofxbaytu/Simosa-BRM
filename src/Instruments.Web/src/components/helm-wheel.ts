// 舵輪(FU 隨動)與 NFU 舵柄:拖曳或鍵盤轉舵輪送舵令;NFU 按住時舵以舵機速率移動,放開即停在當時舵角。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import { rudderCommand } from '../lib/command.js';
import { formatRudder } from '../lib/format.js';
import { clamp } from '../lib/angles.js';

/** 舵輪轉角與舵角的比例(舵輪度/舵角度) */
const WHEEL_RATIO = 3;
const SEND_INTERVAL_MS = 50;
const NFU_INTERVAL_MS = 100;

@customElement('brm-helm')
export class HelmWheel extends LitElement {
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
      .wheel {
        flex: 1;
        min-width: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: grab;
        outline: none;
        border-radius: 50%;
        touch-action: none;
      }
      .wheel:focus-visible {
        box-shadow: var(--ob-focus);
      }
      .wheel.dragging {
        cursor: grabbing;
      }
      .wheel svg {
        height: 100%;
        width: auto;
        max-height: 100%;
        aspect-ratio: 1;
      }
      .rim {
        fill: none;
        stroke: var(--ob-border-strong);
        stroke-width: 9;
      }
      .spoke {
        stroke: var(--ob-border-strong);
        stroke-width: 4;
        stroke-linecap: round;
      }
      .king {
        fill: var(--ob-accent);
      }
      .hub {
        fill: var(--ob-bg-surface-raised);
        stroke: var(--ob-border-strong);
        stroke-width: 2;
      }
      .side {
        display: flex;
        flex-direction: column;
        gap: 4px;
        justify-content: center;
      }
      .side button {
        min-width: 64px;
      }
      .presets {
        display: grid;
        grid-template-columns: repeat(9, 1fr);
        gap: 3px;
      }
      .presets button {
        padding: 3px 2px;
        min-height: 24px;
        font-size: 11px;
      }
      .nfu button {
        font-weight: 600;
      }
      .mode {
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      .mode b {
        color: var(--ob-accent-strong);
      }
    `,
  ];

  /** 核心回報的舵令(度) */
  @property({ type: Number }) order = 0;
  /** 實際舵角(度) */
  @property({ type: Number }) rudder = 0;
  /** 舵角上限(FSB1 70、FSB2 35) */
  @property({ type: Number }) max = 35;
  /** 一般操船上限(預設快速鍵) */
  @property({ type: Number }) normalMax = 35;
  /** 舵機速率(度/秒),NFU 用 */
  @property({ type: Number }) rate = 3;
  /** 自動舵開啟時鎖定舵輪 */
  @property({ type: Boolean }) autopilot = false;

  @state() private localOrder = 0;
  @state() private dragging = false;
  @state() private nfuDir: -1 | 0 | 1 = 0;

  private dragStartAngle = 0;
  private dragStartOrder = 0;
  private lastPointerAngle = 0;
  private sendTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingSend: number | null = null;
  private nfuTimer: ReturnType<typeof setInterval> | null = null;
  private nfuOrder = 0;

  override willUpdate(changed: Map<string, unknown>): void {
    // 未操作時跟隨核心回報的舵令
    if (changed.has('order') && !this.dragging && this.nfuDir === 0) this.localOrder = this.order;
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.sendTimer) clearTimeout(this.sendTimer);
    if (this.nfuTimer) clearInterval(this.nfuTimer);
  }

  private setOrder(deg: number, immediate = false): void {
    const v = Math.round(clamp(deg, -this.max, this.max) * 10) / 10;
    this.localOrder = v;
    this.pendingSend = v;
    if (immediate) {
      this.flush();
    } else if (!this.sendTimer) {
      this.sendTimer = setTimeout(() => {
        this.sendTimer = null;
        this.flush();
      }, SEND_INTERVAL_MS);
    }
  }

  private flush(): void {
    if (this.pendingSend === null) return;
    const v = this.pendingSend;
    this.pendingSend = null;
    dispatchCommand(this, rudderCommand(v, this.max, 'FU'));
  }

  // --- 舵輪拖曳 ---
  private pointerAngle(ev: PointerEvent, el: HTMLElement): number {
    const r = el.getBoundingClientRect();
    const cx = r.left + r.width / 2;
    const cy = r.top + r.height / 2;
    return (Math.atan2(ev.clientX - cx, -(ev.clientY - cy)) * 180) / Math.PI;
  }

  private onPointerDown = (ev: PointerEvent): void => {
    if (this.autopilot) return;
    const el = ev.currentTarget as HTMLElement;
    el.setPointerCapture(ev.pointerId);
    el.focus();
    this.dragging = true;
    this.dragStartAngle = this.pointerAngle(ev, el);
    this.lastPointerAngle = this.dragStartAngle;
    this.dragStartOrder = this.localOrder;
  };

  private onPointerMove = (ev: PointerEvent): void => {
    if (!this.dragging) return;
    const el = ev.currentTarget as HTMLElement;
    const a = this.pointerAngle(ev, el);
    // 連續角度:處理跨越 ±180 的情況
    let delta = a - this.lastPointerAngle;
    if (delta > 180) delta -= 360;
    if (delta < -180) delta += 360;
    this.lastPointerAngle = a;
    this.dragStartOrder += delta / WHEEL_RATIO;
    this.dragStartOrder = clamp(this.dragStartOrder, -this.max, this.max);
    this.setOrder(this.dragStartOrder);
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
    if (this.autopilot) return;
    const step = ev.shiftKey ? 5 : 1;
    switch (ev.key) {
      case 'ArrowLeft':
      case 'a':
        this.setOrder(this.localOrder - step, true);
        break;
      case 'ArrowRight':
      case 'd':
        this.setOrder(this.localOrder + step, true);
        break;
      case '0':
      case 'Home':
      case 's':
        this.setOrder(0, true);
        break;
      default:
        return;
    }
    ev.preventDefault();
  };

  // --- NFU ---
  private nfuStart(dir: -1 | 1): void {
    if (this.nfuTimer) return;
    this.nfuDir = dir;
    // 由實際舵角起算,以舵機速率推進舵令
    this.nfuOrder = this.rudder;
    const stepDeg = this.rate * (NFU_INTERVAL_MS / 1000);
    const tick = () => {
      this.nfuOrder = clamp(this.nfuOrder + dir * stepDeg, -this.max, this.max);
      this.localOrder = Math.round(this.nfuOrder * 10) / 10;
      dispatchCommand(this, rudderCommand(this.nfuOrder, this.max, 'NFU'));
    };
    tick();
    this.nfuTimer = setInterval(tick, NFU_INTERVAL_MS);
  }

  private nfuStop = (): void => {
    if (!this.nfuTimer) return;
    clearInterval(this.nfuTimer);
    this.nfuTimer = null;
    this.nfuDir = 0;
    // 放開即停:舵令改為當時實際舵角
    this.localOrder = Math.round(this.rudder * 10) / 10;
    dispatchCommand(this, rudderCommand(this.rudder, this.max, 'NFU'));
  };

  override render() {
    const rotation = this.localOrder * WHEEL_RATIO;
    const presets = [-this.normalMax, -20, -10, -5, 0, 5, 10, 20, this.normalMax];
    const spokes = [0, 45, 90, 135, 180, 225, 270, 315];
    const mode = this.autopilot ? 'AUTO 自動舵' : this.nfuDir !== 0 ? 'NFU 非隨動' : 'FU 隨動';
    return html`
      <div class="panel">
        <div class="panel-title"><span>操舵 STEERING</span><span class="mode">模式 <b>${mode}</b></span></div>
        <div class="body">
          <div
            class="wheel ${this.dragging ? 'dragging' : ''}"
            tabindex="0"
            role="slider"
            aria-label="舵輪"
            aria-valuemin=${-this.max}
            aria-valuemax=${this.max}
            aria-valuenow=${this.localOrder}
            title="拖曳轉動;鍵盤 ←/→ 每 1°(Shift 5°),0 回正"
            @pointerdown=${this.onPointerDown}
            @pointermove=${this.onPointerMove}
            @pointerup=${this.onPointerUp}
            @pointercancel=${this.onPointerUp}
            @keydown=${this.onKeyDown}
          >
            <svg viewBox="0 0 120 120" aria-hidden="true">
              <g transform="rotate(${rotation} 60 60)">
                <circle class="rim" cx="60" cy="60" r="50" />
                ${spokes.map((a) => svg`<line class="spoke" x1="60" y1="60" x2=${60 + 48 * Math.sin((a * Math.PI) / 180)} y2=${60 - 48 * Math.cos((a * Math.PI) / 180)} />`)}
                <circle class="hub" cx="60" cy="60" r="12" />
                <circle class="king" cx="60" cy="10" r="5" />
              </g>
            </svg>
          </div>
          <div class="side">
            <div class="label">舵令</div>
            <div class="value mid" style="color: var(--ob-order)">${formatRudder(this.localOrder)}</div>
            <div class="label">舵角 <span class="value small">${formatRudder(this.rudder)}</span></div>
            <div class="nfu">
              <button
                ?disabled=${this.autopilot}
                class=${this.nfuDir < 0 ? 'active' : ''}
                @pointerdown=${(e: PointerEvent) => {
                  e.preventDefault();
                  this.nfuStart(-1);
                }}
                @pointerup=${this.nfuStop}
                @pointercancel=${this.nfuStop}
                @pointerleave=${this.nfuStop}
                title="NFU 左舵(按住)"
              >
                ◀ NFU
              </button>
              <button
                ?disabled=${this.autopilot}
                class=${this.nfuDir > 0 ? 'active' : ''}
                @pointerdown=${(e: PointerEvent) => {
                  e.preventDefault();
                  this.nfuStart(1);
                }}
                @pointerup=${this.nfuStop}
                @pointercancel=${this.nfuStop}
                @pointerleave=${this.nfuStop}
                title="NFU 右舵(按住)"
              >
                NFU ▶
              </button>
            </div>
          </div>
        </div>
        <div class="presets">
          ${presets.map(
            (p) => html`<button ?disabled=${this.autopilot} class=${Math.abs(this.localOrder - p) < 0.05 ? 'active' : ''} @click=${() => this.setOrder(p, true)}>
              ${p === 0 ? '正舵' : formatRudder(p)}
            </button>`,
          )}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-helm': HelmWheel;
  }
}
