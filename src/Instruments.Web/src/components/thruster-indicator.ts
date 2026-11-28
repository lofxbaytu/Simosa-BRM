// 艏側推指示:指令與實際推力(−1 左推 … +1 右推)。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatThruster } from '../lib/format.js';
import { clamp } from '../lib/angles.js';

const W = 200;
const H = 30;
const PAD = 14;

@customElement('brm-thruster')
export class ThrusterIndicator extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
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
      /* conning 條單列:「實際」「令」兩組放得下時同一行(各靠一邊);方塊 239 px 以下(「實際 P 100% 令 P 100%」一行放不下)時
         固定一組一行、數值靠右,不隨推力的位數(0% / S 45% / P 100%)跳動;數值字級不超過方塊寬度的 14.7%
         (123 px 的方塊約 18 px),「實際 P 100%」一行放得下;再放不下時數值換到標籤的下一行。
         原本兩組硬排在同一行,10 至 13 格時滿推力的讀數比方塊寬 14 至 69 px */
      @container style(--brm-conning-single: 1) {
        .readouts {
          flex-wrap: wrap;
          gap: 0 8px;
        }
        .readouts > span {
          display: flex;
          flex-wrap: wrap;
          align-items: baseline;
          justify-content: space-between;
          gap: 0 4px;
        }
        .readouts > span > .value {
          margin-left: auto;
        }
        .readouts .value.small {
          font-size: clamp(14px, min(1.1vw, 14.7cqi), 20px);
        }
      }
      @container style(--brm-conning-single: 1) and (max-width: 239px) {
        .readouts > span {
          flex-basis: 100%;
        }
      }
      /* conning 條兩列:「實際」「令」一律各一行、數值靠右(不看方塊寬度),不隨推力的位數(0% / S 45% / P 100%)在一行與兩行之間跳動。
         兩列時艏側推方塊最寬約 252 px(1900 px 寬的 10 至 12 格),數值的字級又跟著視窗寬度變大(1819 px 以上是上限 20 px):
         「實際 S 100% 令 P 100%」排一行在 CI 的字型下要 224 至 226 px 的內容寬度(方塊 242 至 244 px),字距再加大 3 % 就比方塊寬。
         原本與單列用同一個門檻(方塊 239 px 以下才各一行),視窗 1803 至 1827 px 寬時方塊是 239.1 至 242.4 px,排成一行而比
         .readouts 寬 2 至 3 px(第三十一輪審查發現)。再更早是兩組硬排在同一行,放不下時在標籤與數值之間折行(1366 寬以下),
         CI 的字型下 7 欄時兩個「S 100%」並排比方塊寬。推力條吃剩下的高度(至少 14 px);各列的行高固定 */
      @container style(--brm-conning-single: 0) {
        .bar {
          min-height: 14px;
        }
        .readouts {
          flex-direction: column;
          gap: 0 8px;
          line-height: 16px;
          white-space: nowrap;
        }
        .readouts > span {
          display: flex;
          align-items: baseline;
          justify-content: space-between;
          gap: 0 4px;
        }
        .tile > .label {
          line-height: 13px;
        }
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
