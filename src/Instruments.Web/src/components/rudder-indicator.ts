// 舵角指示器:實際舵角指針(下)與舵令標記(上),量程 ±35° 或 ±70° 依船(FSB1 Schilling 舵 70°)。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatRudder } from '../lib/format.js';
import { clamp } from '../lib/angles.js';

const W = 320;
const H = 58;
const PAD = 18;

@customElement('brm-rudder')
export class RudderIndicator extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .scale {
        flex: 1;
        min-height: 40px;
      }
      .axis {
        stroke: var(--ob-scale);
        stroke-width: 1.5;
      }
      .tick {
        stroke: var(--ob-scale);
      }
      .tick.minor {
        stroke: var(--ob-scale-minor);
      }
      .actual {
        fill: var(--ob-pointer);
      }
      .order {
        fill: none;
        stroke: var(--ob-order);
        stroke-width: 2;
      }
      .normal-zone {
        stroke: var(--ob-accent-soft);
        stroke-width: 4;
      }
      .readouts {
        display: flex;
        justify-content: space-between;
        gap: 8px;
      }
    `,
  ];

  /** 實際舵角(度,右正) */
  @property({ type: Number }) rudder = 0;
  /** 舵令(度) */
  @property({ type: Number }) order = 0;
  /** 量程(度) */
  @property({ type: Number }) max = 35;
  /** 一般操船上限(度),用淡色標示 */
  @property({ type: Number }) normalMax = 35;

  private xOf(deg: number): number {
    const max = this.max > 0 ? this.max : 35;
    return W / 2 + (clamp(deg, -max, max) / max) * (W / 2 - PAD);
  }

  override render() {
    const max = this.max > 0 ? this.max : 35;
    const labelStep = max > 40 ? 10 : 5;
    const ticks = [];
    for (let d = -max; d <= max; d += 5) {
      const x = this.xOf(d);
      const major = d % labelStep === 0;
      ticks.push(svg`<line class="tick ${major ? '' : 'minor'}" x1=${x} y1="26" x2=${x} y2=${major ? 36 : 32} />`);
      if (major) ticks.push(svg`<text x=${x} y="48" font-size="10" text-anchor="middle">${Math.abs(d)}</text>`);
    }
    const xa = this.xOf(this.rudder);
    const xo = this.xOf(this.order);
    return html`
      <div class="tile">
        <div class="tile-title"><span>RUDDER</span><span class="zh">舵角 ±${max}°</span></div>
        <div class="scale">
          <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-label="舵角指示器">
            <text x="6" y="30" font-size="11" font-weight="600">P</text>
            <text x=${W - 12} y="30" font-size="11" font-weight="600">S</text>
            <line class="normal-zone" x1=${this.xOf(-this.normalMax)} y1="24" x2=${this.xOf(this.normalMax)} y2="24" />
            <line class="axis" x1=${PAD} y1="26" x2=${W - PAD} y2="26" />
            ${ticks}
            <polygon class="order" points="${xo - 6},4 ${xo + 6},4 ${xo},14" />
            <polygon class="actual" points="${xa - 7},${H - 2} ${xa + 7},${H - 2} ${xa},28" />
          </svg>
        </div>
        <div class="readouts">
          <span><span class="label">舵角</span> <span class="value small">${formatRudder(this.rudder)}</span></span>
          <span><span class="label">舵令</span> <span class="value small" style="color: var(--ob-order)">${formatRudder(this.order)}</span></span>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-rudder': RudderIndicator;
  }
}
