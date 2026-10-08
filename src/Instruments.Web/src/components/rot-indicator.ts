// 迴轉率指示器:±30°/min 弧形(IMO 最低刻度),右轉正;超出量程時指針停在端點並顯示數值。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatRot } from '../lib/format.js';
import { clamp } from '../lib/angles.js';

const SWEEP = 70; // 單側弧度(度),0 在正上方
const CX = 60;
const CY = 62;
const R = 50;

function polar(angleDeg: number, radius: number): [number, number] {
  const a = ((angleDeg - 90) * Math.PI) / 180;
  return [CX + radius * Math.cos(a), CY + radius * Math.sin(a)];
}

@customElement('brm-rot')
export class RotIndicator extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .arc {
        flex: 1;
        min-height: 40px;
      }
      .arcpath {
        fill: none;
        stroke: var(--ob-scale-minor);
        stroke-width: 2;
      }
      .needle {
        stroke: var(--ob-pointer);
        stroke-width: 3;
        stroke-linecap: round;
      }
      .tick {
        stroke: var(--ob-scale);
        stroke-width: 1.5;
      }
      .hub {
        fill: var(--ob-pointer);
      }
      .readout {
        text-align: center;
      }
    `,
  ];

  @property({ type: Number }) rot = 0;
  @property({ type: Number }) range = 30;

  override render() {
    const range = this.range > 0 ? this.range : 30;
    const over = Math.abs(this.rot) > range;
    const angle = clamp((this.rot / range) * SWEEP, -SWEEP - 6, SWEEP + 6);
    const [nx, ny] = polar(angle, R - 6);
    const [sx, sy] = polar(-SWEEP, R);
    const [ex, ey] = polar(SWEEP, R);
    const ticks = [];
    for (let v = -range; v <= range; v += 5) {
      const a = (v / range) * SWEEP;
      const major = v % 10 === 0;
      const [x1, y1] = polar(a, R);
      const [x2, y2] = polar(a, R - (major ? 10 : 5));
      ticks.push(svg`<line class="tick" x1=${x1} y1=${y1} x2=${x2} y2=${y2} />`);
      if (major) {
        const [tx, ty] = polar(a, R - 18);
        ticks.push(svg`<text x=${tx} y=${ty + 3} font-size="9" text-anchor="middle">${Math.abs(v)}</text>`);
      }
    }
    return html`
      <div class="tile">
        <div class="tile-title"><span>ROT</span><span class="zh">迴轉率 °/min</span></div>
        <div class="arc">
          <svg viewBox="0 0 120 72" aria-label="迴轉率">
            <path class="arcpath" d="M ${sx} ${sy} A ${R} ${R} 0 0 1 ${ex} ${ey}" />
            ${ticks}
            <text x="14" y="70" font-size="9" text-anchor="middle">PORT</text>
            <text x="106" y="70" font-size="9" text-anchor="middle">STBD</text>
            <line class="needle" x1=${CX} y1=${CY} x2=${nx} y2=${ny} />
            <circle class="hub" cx=${CX} cy=${CY} r="3" />
          </svg>
        </div>
        <div class="readout value mid ${over ? 'caution' : ''}">${formatRot(this.rot)}</div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-rot': RotIndicator;
  }
}
