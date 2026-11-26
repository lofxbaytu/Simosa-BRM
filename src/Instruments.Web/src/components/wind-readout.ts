// 風:真風(來向/速度)與相對風(相對艏向),附艏向上的小羅盤箭頭(箭頭指向風吹去的方向)。

import { LitElement, html, svg, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatBearing, formatKnots } from '../lib/format.js';

@customElement('brm-wind')
export class WindReadout extends LitElement {
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
        margin-left: auto;
      }
      /* 保險:字型比預期寬(數字寬的字型,例如 DejaVu Sans)而一列放不下時,數值換到標籤下一行(靠右),不被方塊邊界裁切 */
      .rows .row {
        flex-wrap: wrap;
        row-gap: 0;
      }
      /* 窄方塊(≤ 185 px:1093–1366 寬的視窗):玫瑰圖縮小並放到數值上方,兩列數值用滿方塊寬度。
         並排時數值列在 1280×720 只剩 105 px,「040° 12.0 kn」加標籤以 DejaVu Sans 要 110 px(CI 的 Linux 執行器;
         本機的 Inter 剛好 105 px),1366×768 只剩約 4 px 餘裕(第二十九輪 CI run 92;原本門檻 150 / 165 px)。字級下限不變 */
      @container (max-width: 185px) {
        .rose {
          width: 28px;
          height: 28px;
        }
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
      /* conning 條單列的窄方塊(≤ 185 px,玫瑰圖在數值上方):數值用滿方塊寬度,字級可到方塊寬度的 9.5%
         (13 格、方塊約 139 px 時約 13 px,與原本 165 px 的方塊相同) */
      @container style(--brm-conning-single: 1) and (max-width: 185px) {
        .rows .value {
          font-size: clamp(11px, min(0.95vw, 9.5cqi), 18px);
        }
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
