// 航向顯示:大數字 + 航向帶(±25°,每 1° 刻度、每 10° 標示),含自動舵設定航向標記與 COG 標記。

import { LitElement, html, svg, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
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
    `,
  ];

  /** 真航向(度) */
  @property({ type: Number }) heading = 0;
  /** 自動舵設定航向,undefined 表示未設定 */
  @property({ type: Number }) setHeading: number | undefined = undefined;
  @property({ type: Number }) cog: number | undefined = undefined;
  /** 感測器資料可疑(由解析器回報缺欄位) */
  @property({ type: Boolean }) suspect = false;

  override render() {
    const h = normalizeHeading(this.heading);
    return html`
      <div class="tile">
        <div class="tile-title"><span>HDG</span><span class="zh">航向 ${this.suspect ? html`<span class="warning">可疑</span>` : nothing}</span></div>
        <div class="hdg">
          <span class="value big">${formatHeading(h)}</span>
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
