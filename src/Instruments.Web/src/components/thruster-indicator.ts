// 艏側推指示:指令與實際推力(−1 左推 … +1 右推)。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatThruster } from '../lib/format.js';
import { clamp } from '../lib/angles.js';

const W = 200;
const H = 30;
const PAD = 14;

@customElement('brm-thruster')
export class ThrusterIndicator extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .bar {
        flex: 1;
        min-height: 26px;
      }
      .axis {
        stroke: var(--ob-scale);
      }
      .fill {
        fill: var(--ob-accent);
        opacity: 0.8;
      }
      .order {
        stroke: var(--ob-order);
        stroke-width: 2;
      }
      .zero {
        stroke: var(--ob-pointer);
        stroke-width: 1.5;
      }
      .readouts {
        display: flex;
        justify-content: space-between;
      }
    `,
  ];

  @property({ type: Number }) order = 0;
  @property({ type: Number }) actual = 0;
  @property({ type: Boolean }) available = true;

  private xOf(v: number): number {
    return W / 2 + clamp(v, -1, 1) * (W / 2 - PAD);
  }

  override render() {
    const x0 = this.xOf(0);
    const xa = this.xOf(this.actual);
    const xo = this.xOf(this.order);
    return html`
      <div class="tile">
        <div class="tile-title"><span>BOW THR</span><span class="zh">艏側推</span></div>
        <div class="bar">
          <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-label="艏側推">
            <line class="axis" x1=${PAD} y1="15" x2=${W - PAD} y2="15" />
            <rect class="fill" x=${Math.min(x0, xa)} y="8" width=${Math.abs(xa - x0)} height="14" />
            <line class="zero" x1=${x0} y1="4" x2=${x0} y2="26" />
            <line class="order" x1=${xo} y1="2" x2=${xo} y2="28" />
            <text x="2" y="19" font-size="10" font-weight="600">P</text>
            <text x=${W - 9} y="19" font-size="10" font-weight="600">S</text>
          </svg>
        </div>
        <div class="readouts">
          <span><span class="label">實際</span> <span class="value small">${formatThruster(this.actual)}</span></span>
          <span><span class="label">令</span> <span class="value small" style="color: var(--ob-order)">${formatThruster(this.order)}</span></span>
        </div>
        <div class="label ${this.available ? 'muted' : 'warning'}">${this.available ? '可用' : '不可用'}</div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-thruster': ThrusterIndicator;
  }
}
