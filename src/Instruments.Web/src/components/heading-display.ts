// 航向顯示:大數字 + 航向帶(±25°,每 1° 刻度、每 10° 標示),含自動舵設定航向標記與 COG 標記。

import { LitElement, html, svg, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatHeading, formatHeadingDecimal } from '../lib/format.js';
import { headingDifference, normalizeHeading } from '../lib/angles.js';

const HALF_SPAN = 25; // 航向帶半寬(度)
const VIEW_W = 300;
const VIEW_H = 46;
const PX_PER_DEG = VIEW_W / (HALF_SPAN * 2);

@customElement('brm-heading')
export class HeadingDisplay extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
    css`
      .tile {
        align-items: stretch;
      }
      .hdg {
        display: flex;
        align-items: baseline;
        justify-content: center;
        gap: 8px;
      }
      /* 大字級不超過方塊寬度的 33%(conning 條兩列時的窄方塊;單列另有規則,見本樣式末的樣式查詢區塊) */
      .hdg .value.big {
        font-size: clamp(28px, min(3.2vw, 33cqi), 56px);
      }
      .tape {
        flex: 1;
        min-height: 36px;
        margin-top: 2px;
      }
      .sub {
        display: flex;
        justify-content: space-between;
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      .bug {
        fill: var(--ob-order);
      }
      .cogmark {
        fill: none;
        stroke: var(--ob-vector-cog);
        stroke-width: 1.5;
      }
      .lubber {
        stroke: var(--ob-pointer);
        stroke-width: 2;
      }
      .tick {
        stroke: var(--ob-scale);
      }
      .tick.minor {
        stroke: var(--ob-scale-minor);
      }
      .invalid-tag {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
        border-radius: 3px;
        padding: 0 4px;
        font-weight: 700;
      }
      .value.stale {
        opacity: 0.45;
        text-decoration: line-through;
        text-decoration-thickness: 2px;
      }
      /* conning 條單列(shared.ts 的 conningTileStyles):三位數的航向與含小數的航向放得下時同一行;放不下(12 至 13 格,
         方塊約 150 至 170 px)時小數換到下一行(置中),不被方塊邊界裁切。大字級不超過方塊寬度的 38%(方塊 148 px 以上就是 56 px)。
         原本以縮小大字(33cqi)硬排成一行,12 / 13 格時仍被左右兩側各切掉 2 至 5 px。下方的「設定 / COG」放不下一行時 COG 換到下一行 */
      @container style(--brm-conning-single: 1) {
        .hdg {
          flex-wrap: wrap;
          gap: 0 8px;
        }
        .hdg .value.big {
          font-size: clamp(28px, min(3.2vw, 38cqi), 56px);
        }
        .sub {
          flex-wrap: wrap;
          gap: 0 6px;
          white-space: nowrap;
        }
        .sub > :last-child {
          margin-left: auto;
        }
      }
      /* conning 條兩列(方塊約 107 至 135 px 高):大字、航向帶、「設定 / COG」三列加起來原本比方塊高 12 至 23 px,最後一列的下緣被切掉
         (1366×768 的教官站與重播檢視、瀏覽器沒有全螢幕時更多)。大字的行高縮成 0.82(數字沒有降部);航向帶吃剩下的高度
         (原本至少 36 px;1366×768 的教官站約 33 px,最少 24 px —— 字型的行高比預期大時由它讓,文字不被切掉);最後一列的行高固定 13 px。
         大字的字級只在方塊很矮時以方塊高度設上限(119cqb − 86px:方塊 107 px 高時 41 px,航向帶還有約 30 px;
         112 px 以上 —— 1366×768 與 1536×864 的三個頁面 —— 不受限) */
      @container style(--brm-conning-single: 0) {
        .hdg .value.big {
          font-size: clamp(28px, min(3.2vw, 33cqi, calc(119cqb - 86px)), 56px);
          line-height: 0.82;
        }
        .hdg .unit {
          line-height: 13px;
        }
        .tape {
          flex: 1 1 0;
          min-height: 24px;
        }
        .sub {
          line-height: 13px;
          white-space: nowrap;
        }
      }
    `,
  ];

  /** 真航向(度) */
  @property({ type: Number }) heading = 0;
  /** 自動舵設定航向,undefined 表示未設定 */
  @property({ type: Number }) setHeading: number | undefined = undefined;
  @property({ type: Number }) cog: number | undefined = undefined;
  /** 感測器資料可疑(由解析器回報缺欄位) */
  @property({ type: Boolean }) suspect = false;
  /** 電羅經無效(state.sensors.headingValid = false):顯示保持的最後值並標「無效」(sensors-alerts.md 第 2.1 節) */
  @property({ type: Boolean }) invalid = false;

  override render() {
    const h = normalizeHeading(this.heading);
    return html`
      <div class="tile">
        <div class="tile-title"><span>HDG</span><span class="zh">航向 ${this.invalid ? html`<span class="invalid-tag" title="電羅經無效:顯示最後有效值">無效</span>` : this.suspect ? html`<span class="warning">可疑</span>` : nothing}</span></div>
        <div class="hdg">
          <span class="value big ${this.invalid ? 'stale' : ''}">${formatHeading(h)}</span>
          <span class="unit">${formatHeadingDecimal(h)}</span>
        </div>
        <div class="tape">${this.renderTape(h)}</div>
        <div class="sub">
          <span>設定 ${this.setHeading === undefined ? '---' : formatHeading(this.setHeading)}</span>
          <span>COG ${formatHeading(this.cog)}</span>
        </div>
      </div>
    `;
  }

  private renderTape(h: number) {
    const start = Math.floor(h - HALF_SPAN);
    const end = Math.ceil(h + HALF_SPAN);
    const ticks = [];
    for (let d = start; d <= end; d++) {
      const x = VIEW_W / 2 + (d - h) * PX_PER_DEG;
      if (x < 0 || x > VIEW_W) continue;
      const deg = normalizeHeading(d);
      const major = deg % 10 === 0;
      const medium = deg % 5 === 0;
      const len = major ? 14 : medium ? 9 : 5;
      ticks.push(svg`<line class="tick ${major || medium ? '' : 'minor'}" x1=${x} y1=${VIEW_H - 4} x2=${x} y2=${VIEW_H - 4 - len} />`);
      if (major) {
        ticks.push(svg`<text x=${x} y="12" font-size="11" text-anchor="middle">${formatHeading(deg)}</text>`);
      }
    }
    const marks = [];
    if (this.setHeading !== undefined && Number.isFinite(this.setHeading)) {
      const diff = headingDifference(h, this.setHeading);
      const xRaw = VIEW_W / 2 + diff * PX_PER_DEG;
      const x = Math.max(6, Math.min(VIEW_W - 6, xRaw));
      // 在範圍內畫三角標記,超出則貼邊
      marks.push(svg`<polygon class="bug" points="${x - 6},${VIEW_H} ${x + 6},${VIEW_H} ${x},${VIEW_H - 8}" />`);
    }
    if (this.cog !== undefined && Number.isFinite(this.cog)) {
      const diff = headingDifference(h, this.cog);
      if (Math.abs(diff) <= HALF_SPAN) {
        const x = VIEW_W / 2 + diff * PX_PER_DEG;
        marks.push(svg`<circle class="cogmark" cx=${x} cy="19" r="3.5" />`);
      }
    }
    return svg`<svg viewBox="0 0 ${VIEW_W} ${VIEW_H}" preserveAspectRatio="none" aria-label="航向帶">
      ${ticks}
      ${marks}
      <line class="lubber" x1=${VIEW_W / 2} y1="14" x2=${VIEW_W / 2} y2=${VIEW_H - 2} />
    </svg>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-heading': HeadingDisplay;
  }
}
