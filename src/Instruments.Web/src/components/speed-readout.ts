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
    `,
  ];

  @property({ type: Number }) stw = 0;
  @property({ type: Number }) sog = 0;
  @property({ type: Number }) cog = 0;
  @property({ type: Number }) drift: number | undefined = undefined;

  override render() {
    return html`
      <div class="tile">
        <div class="tile-title"><span>SPEED</span><span class="zh">速度 kn</span></div>
        <div class="rows">
          <div class="row"><span class="label">STW 對水</span><span class="value mid">${formatKnots(this.stw)}</span></div>
          <div class="row"><span class="label">SOG 對地</span><span class="value mid">${formatKnots(this.sog)}</span></div>
          <div class="row"><span class="label">COG 航跡向</span><span class="value small">${formatHeading(this.cog)}°</span></div>
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
