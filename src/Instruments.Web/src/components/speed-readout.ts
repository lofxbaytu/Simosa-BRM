// 速度讀數:STW(對水)、SOG(對地)、COG、漂角。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatHeading, formatKnots, formatSignedDeg } from '../lib/format.js';

@customElement('brm-speed')
export class SpeedReadout extends LitElement {
  static override styles = [
    tileStyles,
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
      /* 窄方塊(conning 條單列 13 格,1920 寬約 115 px):數值字級依方塊寬度縮小,標籤只留英文縮寫(中文部分仍在 DOM 內供讀屏與測試) */
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
