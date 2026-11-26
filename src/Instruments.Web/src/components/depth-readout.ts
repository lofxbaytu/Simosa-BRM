// 水深/UKC:龍骨下水深為主,附水深、潮高(情境有潮汐時)、squat、吃水;UKC 低於警報門檻或擱淺時用紅色警報。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatMeters } from '../lib/format.js';

@customElement('brm-depth')
export class DepthReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
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
      /* conning 條單列:大字級不超過方塊寬度的 33%,13 格(方塊約 155 px)時兩位數的 UKC(「19.2」)在最寬的 CI 字型下放得下
         (原本固定 56 px,四個字比方塊寬 8 至 33 px);100 m 以上(深水,五個字元)再縮到 26.5%;1000 m 以上(六個字元以上,
         例如「1234.5」;情境的水深沒有上限)依字元數再縮:方塊寬度的 130% 除以字元數(六個字元 21.7%、七個字元 18.6%)。
         「擱淺」放不下時換到下一行 */
      @container style(--brm-conning-single: 1) {
        .main {
          flex-wrap: wrap;
          align-content: flex-start;
          gap: 0 4px;
        }
        .main .value.big {
          font-size: clamp(28px, min(3.2vw, 33cqi), 56px);
        }
        .main .value.big.long {
          font-size: clamp(28px, min(3.2vw, 26.5cqi), 56px);
        }
        .main .value.big.xlong {
          font-size: clamp(28px, min(3.2vw, calc(130cqi / var(--ukc-chars, 6))), 56px);
        }
      }
    `,
  ];

  @property({ type: Number }) ukc = 0;
  @property({ type: Number }) waterDepth: number | undefined = undefined;
  /** 潮高 (m;state.tideHeight_m,情境有 environment.tide 時才有;undefined 時不顯示該列) */
  @property({ type: Number }) tideHeight: number | undefined = undefined;
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
    const ukcText = this.valid ? formatMeters(this.ukc) : '--.-';
    // 「100.0」以上(五個字元以上)比平常多一位數:conning 條單列的窄方塊用較小的字級(見上方樣式);
    // 「1000.0」以上(六個字元以上)再依字元數縮(--ukc-chars)
    const long = ukcText.length >= 5;
    const xlong = ukcText.length >= 6;
    const valueStyle = `${xlong ? `--ukc-chars:${ukcText.length};` : ''}${this.sensorInvalid ? 'opacity:0.45;text-decoration:line-through' : ''}`;
    return html`
      <div class="tile">
        <div class="tile-title"><span>UKC</span><span class="zh">龍骨下水深 m${this.sensorInvalid ? html` <span class="alarm" title="測深儀無效:顯示最後有效值">無效</span>` : nothing}</span></div>
        <div class="main ${alarm ? 'alarm' : ''}">
          <span class="value big ${warn ? 'warning' : ''} ${long ? 'long' : ''} ${xlong ? 'xlong' : ''}" style=${valueStyle}>${ukcText}</span>
          ${this.aground ? html`<span class="label" style="color: inherit">擱淺</span>` : nothing}
        </div>
        <div class="rows">
          <div class="row"><span class="label">水深</span><span>${formatMeters(this.waterDepth)}</span></div>
          ${this.tideHeight !== undefined && Number.isFinite(this.tideHeight)
            ? html`<div class="row tide"><span class="label">潮高</span><span>${formatTide(this.tideHeight)}</span></div>`
            : nothing}
          <div class="row"><span class="label">Squat</span><span>${formatMeters(this.squat, 2)}</span></div>
          <div class="row"><span class="label">吃水 F/A</span><span>${formatMeters(this.draftFore)} / ${formatMeters(this.draftAft)}</span></div>
        </div>
      </div>
    `;
  }
}

/** 潮高帶正負號(相對海圖基準面):+1.9 m、−0.3 m、0.0 m */
export function formatTide(h: number): string {
  const r = Math.round(h * 10) / 10;
  const sign = r > 0 ? '+' : r < 0 ? '−' : '';
  return `${sign}${Math.abs(r).toFixed(1)} m`;
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-depth': DepthReadout;
  }
}
