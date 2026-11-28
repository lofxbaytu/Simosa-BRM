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
      /* conning 條兩列(方塊約 107 至 135 px 高):大字加下方三至四列(水深、潮高、Squat、吃水)原本比方塊高 20 至 31 px,
         1366×768 時最後一列「吃水 F/A」整列在方塊下緣之外,看不到。
         - 大字的行高縮成 0.82(數字沒有降部);下方各列的行高固定(方塊 115 px 高以下 12 px,125 px 以上 14 px,其間依高度)、
           列與列之間不另留間距;
         - 方塊 215 px 以上:下方各列排成兩欄(兩行)。兩欄時每一格的數值緊接在自己的標籤後面(間距 6 px),兩欄之間至少 14 px:
           數值若照單欄的排法靠欄的右緣,左欄的數值離自己的標籤 60 px 以上、離右欄的標籤只有 14 px(畫面上讀成「水深 ……… 18.0 潮高」,
           第三十一輪審查發現);
         - 方塊窄於 215 px:下方各列仍是單欄(三行;情境有潮汐時多「潮高」一列,四行);
         - 大字在標題列與下方各列之間上下置中(.main 改成一列的格線:align-content 置中,大字與「擱淺」仍對齊基線)。行高縮成 0.82 之後
           數字幾乎貼著行框的上緣(各字型的升部不同:CI 的字型下數字比行框高出 0.5 px),原本大字又排在 .main 的最上面 —— 警報的紅底
           (.main.alarm)因此上緣貼著數字、下面空一大片(兩欄時 20 px 以上),數字離標題文字只有 0 至 2 px(第三十一輪審查發現)。
           置中之後多出來的高度上下各半,不論哪一種字型數字都在紅底裡;沒有警報時大字也不再貼著標題;
         - 大字的字級以方塊高度設上限(--ukc-h),只在方塊很矮時起作用:兩欄 119cqb − 64px(不受限);單欄三行 119cqb − 79px
           (方塊 107 px 高時 48 px);單欄四行(有潮高)119cqb − 93px(107 px 時 34 px、112 px 時 40 px,120 px 以上不受限);
         - 多一位數的值依方塊寬度再縮:100 m 以上(五個字元)不超過方塊寬度的 26.5%,1000 m 以上依字元數(方塊寬度的 130% 除以字元數),
           與單列相同;
         - 標題列的間距與「無效」標示的左右內距各少 2 px(13 格時 UKC 在第一列,只有約 155 px 寬,「UKC 龍骨下水深 m 無效」一行剛好放得下) */
      @container style(--brm-conning-single: 0) {
        .tile {
          --ukc-h: calc(119cqb - 79px);
        }
        .tile:has(.row.tide) {
          --ukc-h: calc(119cqb - 93px);
        }
        .tile-title {
          column-gap: 4px;
        }
        .tile-title .alarm {
          padding: 0 3px;
        }
        .main {
          flex: 1 1 auto;
          display: grid;
          grid-auto-flow: column;
          align-content: safe center;
          gap: 0 4px;
        }
        .main .value.big {
          font-size: clamp(28px, min(3.2vw, var(--ukc-h)), 56px);
          line-height: 0.82;
        }
        .main .value.big.long {
          font-size: clamp(24px, min(3.2vw, 26.5cqi, var(--ukc-h)), 56px);
        }
        .main .value.big.xlong {
          font-size: clamp(20px, min(3.2vw, calc(130cqi / var(--ukc-chars, 6)), var(--ukc-h)), 56px);
        }
        .main .label {
          line-height: 13px;
        }
        .rows {
          gap: 0;
          line-height: clamp(12px, calc(20cqb - 11px), 14px);
        }
        .rows .label {
          line-height: inherit;
        }
      }
      @container style(--brm-conning-single: 0) and (min-width: 215px) {
        .tile,
        .tile:has(.row.tide) {
          --ukc-h: calc(119cqb - 64px);
        }
        .rows {
          display: grid;
          grid-template-columns: minmax(max-content, 1fr) minmax(max-content, 1fr);
          column-gap: 14px;
        }
        .rows .row {
          justify-content: flex-start;
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
