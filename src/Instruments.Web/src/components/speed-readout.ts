// 速度讀數:STW(對水)、SOG(對地)、COG、漂角。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatHeading, formatKnots, formatSignedDeg } from '../lib/format.js';

@customElement('brm-speed')
export class SpeedReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
    css`
      .rows {
        flex: 1;
        display: flex;
        flex-direction: column;
        justify-content: space-evenly;
      }
      .stale {
        opacity: 0.45;
        text-decoration: line-through;
      }
      /* 窄方塊:COG / 漂角的字級依方塊寬度縮小,125 px 以下標籤只留英文縮寫(中文部分仍在 DOM 內供讀屏與測試)。
         這個門檻單列與兩列相同(單列只有 13 格、視窗約 1980 px 以下時方塊才窄於 125 px);conning 條單列另有 STW / SOG 的字級規則,
         見本樣式末的樣式查詢區塊 */
      .rows .value.small {
        font-size: clamp(12px, min(1.1vw, 15cqi), 20px);
      }
      @container (max-width: 125px) {
        .label .zh {
          display: none;
        }
      }
      .inv {
        color: var(--ob-alarm);
        font-weight: 700;
        margin-left: 4px;
      }
      /* conning 條單列:STW / SOG 的字級不超過方塊寬度的 22%(13 格、方塊約 120 px 時約 26 px)。標籤的中文部分(對水 / 對地 / 航跡向)
         與兩列一樣只在方塊 125 px 以下才省略 —— 11 至 12 格(方塊約 129 至 142 px)照常顯示;「標籤 + 數值」一行放不下時
         (較寬的字型下兩位數的船速、倒俥兩位數、漂角三位數、「無效」標示)數值換到標籤的下一行(conningTileStyles 的 .row),
         不被方塊邊界裁切。第三十一輪的第一版把省略的門檻提到 150 px(11 至 13 格都只剩 STW / SOG / COG),審查後改回:
         11 格是最常見的畫面,方塊裡放得下 */
      @container style(--brm-conning-single: 1) {
        .rows .value.mid {
          font-size: clamp(18px, min(1.6vw, 22cqi), 28px);
        }
      }
    `,
  ];

  @property({ type: Number }) stw = 0;
  @property({ type: Number }) sog = 0;
  @property({ type: Number }) cog = 0;
  @property({ type: Number }) drift: number | undefined = undefined;
  /** 計程儀無效(sensors.stwValid = false) */
  @property({ type: Boolean }) stwInvalid = false;
  /** GNSS 無效(sensors.positionValid = false):SOG/COG 為最後有效值 */
  @property({ type: Boolean }) gnssInvalid = false;

  override render() {
    return html`
      <div class="tile">
        <div class="tile-title"><span>SPEED</span><span class="zh">速度 kn</span></div>
        <div class="rows">
          <div class="row"><span class="label">STW<span class="zh"> 對水</span>${this.stwInvalid ? html`<span class="inv">無效</span>` : ''}</span><span class="value mid ${this.stwInvalid ? 'stale' : ''}">${formatKnots(this.stw)}</span></div>
          <div class="row"><span class="label">SOG<span class="zh"> 對地</span>${this.gnssInvalid ? html`<span class="inv">無效</span>` : ''}</span><span class="value mid ${this.gnssInvalid ? 'stale' : ''}">${formatKnots(this.sog)}</span></div>
          <div class="row"><span class="label">COG<span class="zh"> 航跡向</span></span><span class="value small ${this.gnssInvalid ? 'stale' : ''}">${formatHeading(this.cog)}°</span></div>
          <div class="row"><span class="label">漂角</span><span class="value small muted">${formatSignedDeg(this.drift)}</span></div>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-speed': SpeedReadout;
  }
}
