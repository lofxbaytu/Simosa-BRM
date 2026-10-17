// 水深/UKC:龍骨下水深為主,附水深、squat、吃水;UKC 低於警報門檻或擱淺時用紅色警報。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatMeters } from '../lib/format.js';

@customElement('brm-depth')
export class DepthReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .main {
        display: flex;
        align-items: baseline;
        justify-content: center;
        gap: 4px;
        flex: 1;
      }
      .main.alarm {
        border-radius: 6px;
      }
      .rows {
        display: flex;
        flex-direction: column;
        gap: 1px;
        font-size: 11px;
      }
    `,
  ];

  @property({ type: Number }) ukc = 0;
  @property({ type: Number }) waterDepth: number | undefined = undefined;
  @property({ type: Number }) squat: number | undefined = undefined;
  @property({ type: Number }) draftFore: number | undefined = undefined;
  @property({ type: Number }) draftAft: number | undefined = undefined;
  @property({ type: Boolean }) aground = false;
  /** UKC 警報門檻 (m),依公司 UKC 政策參數集(規劃書第 9.1 節),第一版預設 1.0 */
  @property({ type: Number }) alarmBelow = 1.0;
  /** UKC 警告門檻 (m) */
  @property({ type: Number }) warnBelow = 2.0;
  /** 尚未收到資料時為 false:只顯示佔位,不觸發警報 */
  @property({ type: Boolean }) valid = true;
  /** 測深儀無效(sensors.depthValid = false):顯示最後有效值並標「無效」,不以此觸發 UKC 警報 */
  @property({ type: Boolean }) sensorInvalid = false;

  override render() {
    const alarm = this.valid && (this.aground || (!this.sensorInvalid && this.ukc <= this.alarmBelow));
    const warn = this.valid && !this.sensorInvalid && !alarm && this.ukc <= this.warnBelow;
    return html`
      <div class="tile">
        <div class="tile-title"><span>UKC</span><span class="zh">龍骨下水深 m${this.sensorInvalid ? html` <span class="alarm" title="測深儀無效:顯示最後有效值">無效</span>` : nothing}</span></div>
        <div class="main ${alarm ? 'alarm' : ''}">
          <span class="value big ${warn ? 'warning' : ''}" style=${this.sensorInvalid ? 'opacity:0.45;text-decoration:line-through' : ''}>${this.valid ? formatMeters(this.ukc) : '--.-'}</span>
          ${this.aground ? html`<span class="label" style="color: inherit">擱淺</span>` : nothing}
        </div>
        <div class="rows">
          <div class="row"><span class="label">水深</span><span>${formatMeters(this.waterDepth)}</span></div>
          <div class="row"><span class="label">Squat</span><span>${formatMeters(this.squat, 2)}</span></div>
          <div class="row"><span class="label">吃水 F/A</span><span>${formatMeters(this.draftFore)} / ${formatMeters(this.draftAft)}</span></div>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-depth': DepthReadout;
  }
}
