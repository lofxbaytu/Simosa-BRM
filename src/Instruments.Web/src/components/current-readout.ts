// 流:流向(去向)與流速,附艏向上的箭頭(指向流去的方向);時變流場(state.currentSource)時標示來源(流場表/潮流)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatBearing, formatKnots } from '../lib/format.js';
import { CURRENT_SOURCE_LABELS, type CurrentSource } from '../types/state.js';

@customElement('brm-current')
export class CurrentReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
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
      /* min(…vw, …cqi):在 conning 條內(方塊是尺寸容器)字級不超過方塊寬度的 8%,窄方塊的數值不被裁切;
         不在尺寸容器內時 cqi 以視窗寬度計,min() 取到的仍是原本的 vw 值 */
      .rows .value {
        font-size: clamp(11px, min(0.95vw, 8cqi), 18px);
      }
      @container (max-width: 165px) {
        .rose {
          width: 28px;
          height: 28px;
        }
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
      /* conning 條單列:並排時數值字級可到方塊寬度的 9%;方塊 185 px 以下(與風方塊同一門檻)玫瑰圖移到數值上方,
         兩列數值用滿方塊寬度、字級可到 10%(13 格、方塊約 134 px 時約 13 px)。並排時數值列只剩約 100 px,
         「流速 DRIFT 9.9 kn」在 CI 的字型下放不下 */
      @container style(--brm-conning-single: 1) {
        .rows {
          min-width: 0;
        }
        .rows .value {
          font-size: clamp(11px, min(0.95vw, 9cqi), 18px);
        }
      }
      @container style(--brm-conning-single: 1) and (max-width: 185px) {
        .body {
          flex-direction: column;
          align-items: stretch;
          gap: 2px;
        }
        .rose {
          align-self: center;
        }
        .rows {
          flex: 0 0 auto;
        }
        .rows .value {
          font-size: clamp(11px, min(0.95vw, 10cqi), 18px);
        }
      }
    `,
  ];

  @property({ type: Number }) set = 0;
  @property({ type: Number }) drift = 0;
  @property({ type: Number }) heading = 0;
  /** 流場來源(時變流場時才有):顯示「流(潮流)」/「流(流場表)」 */
  @property({ type: String }) source: CurrentSource | undefined = undefined;

  override render() {
    const rel = this.set - this.heading;
    const label = this.source ? CURRENT_SOURCE_LABELS[this.source] : undefined;
    return html`
      <div class="tile">
        <div class="tile-title"><span>CURRENT</span><span class="zh">流${label ? html`<span class="source" title="時變流場:流向/流速隨時間變化">(${label})</span>` : nothing}</span></div>
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
