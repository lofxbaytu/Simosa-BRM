// 風:真風(來向/速度)與相對風(相對艏向),附艏向上的小羅盤箭頭(箭頭指向風吹去的方向)。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatBearing, formatKnots } from '../lib/format.js';

@customElement('brm-wind')
export class WindReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .body {
        flex: 1;
        display: flex;
        gap: 6px;
        align-items: center;
        min-height: 0;
      }
      .rose {
        width: 40px;
        height: 40px;
        flex: none;
      }
      .rows .value {
        font-size: clamp(13px, 0.95vw, 18px);
      }
      .rows {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
      }
      .ring {
        fill: none;
        stroke: var(--ob-scale-minor);
      }
      .arrow {
        stroke: var(--ob-pointer);
        stroke-width: 2.5;
        stroke-linecap: round;
        fill: var(--ob-pointer);
      }
      .bow {
        fill: var(--ob-scale);
      }
    `,
  ];

  @property({ type: Number }) trueDir = 0;
  @property({ type: Number }) trueSpeed = 0;
  @property({ type: Number }) relDir: number | undefined = undefined;
  @property({ type: Number }) relSpeed: number | undefined = undefined;
  @property({ type: Number }) heading = 0;

  override render() {
    // 羅盤為艏向上,箭頭由風的來向指向中心(風吹去的方向)
    const rel = this.relDir ?? this.trueDir - this.heading;
    return html`
      <div class="tile">
        <div class="tile-title"><span>WIND</span><span class="zh">風</span></div>
        <div class="body">
          <div class="rose">
            <svg viewBox="0 0 50 50" aria-label="相對風向">
              <circle class="ring" cx="25" cy="25" r="22" />
              <polygon class="bow" points="25,1 22,7 28,7" />
              <g transform="rotate(${rel} 25 25)">
                <line class="arrow" x1="25" y1="6" x2="25" y2="30" />
                <polygon class="arrow" points="25,36 20,26 30,26" />
              </g>
            </svg>
          </div>
          <div class="rows">
            <div class="row"><span class="label">真風</span><span class="value small">${formatBearing(this.trueDir)}° ${formatKnots(this.trueSpeed)}<span class="unit">kn</span></span></div>
            <div class="row"><span class="label">相對</span><span class="value small">${formatBearing(rel)}° ${formatKnots(this.relSpeed)}<span class="unit">kn</span></span></div>
          </div>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-wind': WindReadout;
  }
}
