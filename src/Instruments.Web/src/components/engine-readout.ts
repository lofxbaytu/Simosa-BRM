// 主機:轉速(倒車負)橫條與數字、轉速令標記、車鐘位置、主機狀態。

import { LitElement, html, svg, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatRpm } from '../lib/format.js';
import { clamp } from '../lib/angles.js';
import type { EngineRunState, TelegraphPosition } from '../types/state.js';

const W = 300;
const H = 30;
const PAD = 10;

const ENGINE_STATE_LABEL: Record<EngineRunState, string> = {
  running: '運轉中',
  stopped: '停車',
  starting: '啟動中',
  failed: '故障',
};

@customElement('brm-engine')
export class EngineReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .top {
        display: flex;
        justify-content: space-between;
        align-items: baseline;
      }
      .bar {
        min-height: 26px;
        margin: 2px 0;
      }
      .axis {
        stroke: var(--ob-scale);
        stroke-width: 1;
      }
      .fill {
        fill: var(--ob-accent);
        opacity: 0.8;
      }
      .fill.astern {
        fill: var(--ob-warning);
      }
      .order {
        stroke: var(--ob-order);
        stroke-width: 2;
      }
      .zero {
        stroke: var(--ob-pointer);
        stroke-width: 1.5;
      }
      .bottom {
        display: flex;
        justify-content: space-between;
        align-items: baseline;
        gap: 6px;
        font-size: 11px;
      }
      .teleg {
        font-weight: 700;
        font-size: 14px;
        color: var(--ob-text);
      }
    `,
  ];

  @property({ type: Number }) rpm = 0;
  @property({ type: Number }) rpmOrder = 0;
  @property({ type: Number }) rpmMax = 180;
  @property({ type: String }) telegraph: TelegraphPosition = 'STOP';
  @property({ type: String }) telegraphLabel = '停車';
  @property({ type: String }) engineState: EngineRunState | undefined = undefined;
  @property({ type: Number }) loadPct: number | undefined = undefined;

  private xOf(rpm: number): number {
    const max = this.rpmMax > 0 ? this.rpmMax : 180;
    return W / 2 + (clamp(rpm, -max, max) / max) * (W / 2 - PAD);
  }

  override render() {
    const x0 = this.xOf(0);
    const xr = this.xOf(this.rpm);
    const xo = this.xOf(this.rpmOrder);
    const astern = this.rpm < 0;
    const failed = this.engineState === 'failed';
    return html`
      <div class="tile">
        <div class="tile-title"><span>RPM</span><span class="zh">主機轉速</span></div>
        <div class="top">
          <span class="value big ${astern ? 'warning' : ''}">${formatRpm(this.rpm)}</span>
          <span class="label">令 <span class="value small" style="color: var(--ob-order)">${formatRpm(this.rpmOrder)}</span></span>
        </div>
        <div class="bar">
          <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-label="轉速">
            <line class="axis" x1=${PAD} y1="15" x2=${W - PAD} y2="15" />
            <rect class="fill ${astern ? 'astern' : ''}" x=${Math.min(x0, xr)} y="8" width=${Math.abs(xr - x0)} height="14" />
            <line class="zero" x1=${x0} y1="4" x2=${x0} y2="26" />
            <line class="order" x1=${xo} y1="2" x2=${xo} y2="28" />
            <text x=${PAD} y="29" font-size="9">ASTERN</text>
            <text x=${W - PAD} y="29" font-size="9" text-anchor="end">AHEAD</text>
          </svg>
        </div>
        <div class="bottom">
          <span class="teleg">${this.telegraph} <span class="label">${this.telegraphLabel}</span></span>
          <span class="${failed ? 'alarm' : 'muted'}">${this.engineState ? ENGINE_STATE_LABEL[this.engineState] : '—'}${this.loadPct !== undefined ? html` ${Math.round(this.loadPct)}%` : nothing}</span>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-engine': EngineReadout;
  }
}
