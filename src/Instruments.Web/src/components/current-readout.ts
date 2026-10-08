// 流:流向(去向)與流速,附艏向上的箭頭(指向流去的方向)。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatBearing, formatKnots } from '../lib/format.js';

@customElement('brm-current')
export class CurrentReadout extends LitElement {
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
      }
      .ring {
        fill: none;
        stroke: var(--ob-scale-minor);
      }
      .arrow {
        stroke: var(--ob-vector-cog);
        stroke-width: 2.5;
        stroke-linecap: round;
        fill: var(--ob-vector-cog);
      }
      .bow {
        fill: var(--ob-scale);
      }
    `,
  ];

  @property({ type: Number }) set = 0;
  @property({ type: Number }) drift = 0;
  @property({ type: Number }) heading = 0;

  override render() {
    const rel = this.set - this.heading;
    return html`
      <div class="tile">
        <div class="tile-title"><span>CURRENT</span><span class="zh">流</span></div>
        <div class="body">
          <div class="rose">
            <svg viewBox="0 0 50 50" aria-label="流向">
              <circle class="ring" cx="25" cy="25" r="22" />
              <polygon class="bow" points="25,1 22,7 28,7" />
              <g transform="rotate(${rel} 25 25)">
                <line class="arrow" x1="25" y1="40" x2="25" y2="16" />
                <polygon class="arrow" points="25,8 20,18 30,18" />
              </g>
            </svg>
          </div>
          <div class="rows">
            <div class="row"><span class="label">流向 SET</span><span class="value small">${formatBearing(this.set)}°</span></div>
            <div class="row"><span class="label">流速 DRIFT</span><span class="value small">${formatKnots(this.drift)}<span class="unit">kn</span></span></div>
          </div>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-current': CurrentReadout;
  }
}
