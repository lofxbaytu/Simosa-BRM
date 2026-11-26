// 主機:轉速(倒車負)橫條與數字、轉速令標記、車鐘位置、主機狀態。

import { LitElement, html, svg, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatRpm } from '../lib/format.js';
import { clamp } from '../lib/angles.js';
import type { EngineRunState, TelegraphPosition } from '../types/state.js';

const W = 300;
const H = 30;
const PAD = 10;

const ENGINE_STATE_LABEL: Record<EngineRunState, string> = {
  running: '運轉中',
  stopped: '停車',
  starting: '啟動中',
  failed: '故障',
};

@customElement('brm-engine')
export class EngineReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
    css`
      .top {
        display: flex;
        justify-content: space-between;
        align-items: baseline;
      }
      .bar {
        min-height: 26px;
        margin: 2px 0;
      }
      .axis {
        stroke: var(--ob-scale);
        stroke-width: 1;
      }
      .fill {
        fill: var(--ob-accent);
        opacity: 0.8;
      }
      .fill.astern {
        fill: var(--ob-warning);
      }
      .order {
        stroke: var(--ob-order);
        stroke-width: 2;
      }
      .zero {
        stroke: var(--ob-pointer);
        stroke-width: 1.5;
      }
      .bottom {
        display: flex;
        justify-content: space-between;
        align-items: baseline;
        gap: 6px;
        font-size: 11px;
      }
      .teleg {
        font-weight: 700;
        font-size: 14px;
        color: var(--ob-text);
      }
      /* conning 條單列:
         - 轉速(大字)的字級不超過方塊寬度的 32.5%,13 格(方塊約 158 px)時「-150」放得下(原本固定 56 px,四個字比方塊寬);
         - 轉速令放不下同一行時換到下一行(靠右);方塊窄於 232 px(「-150 令 -150」一行放不下)時固定在下一行,
           不隨轉速的位數(0 / 70 / 100 / -100)在兩行之間跳動;
         - 車鐘位置與主機狀態放不下一行時主機狀態換到下一行(靠右);
         - 轉速條的高度平常是 26 px,方塊不夠高時(視窗很矮,conning 條只有 150 px 左右)可以縮到 10 px:換行之後內容比原本高,
           把高度讓給文字,主機狀態那一行不被方塊下緣切掉(轉速條是 preserveAspectRatio="none" 的圖形,壓扁仍看得出轉速與轉速令的位置) */
      @container style(--brm-conning-single: 1) {
        .top {
          flex-wrap: wrap;
        }
        .bar {
          flex: 0 1 26px;
          min-height: 10px;
        }
        .top .value.big {
          font-size: clamp(28px, min(3.2vw, 32.5cqi), 56px);
        }
        .top .label {
          margin-left: auto;
          text-align: right;
        }
        .bottom {
          flex-wrap: wrap;
          gap: 0 6px;
        }
        .bottom > :last-child {
          margin-left: auto;
          white-space: nowrap;
        }
      }
      @container style(--brm-conning-single: 1) and (max-width: 231px) {
        .top .label {
          flex-basis: 100%;
        }
      }
    `,
  ];

  @property({ type: Number }) rpm = 0;
  @property({ type: Number }) rpmOrder = 0;
  @property({ type: Number }) rpmMax = 180;
  @property({ type: String }) telegraph: TelegraphPosition = 'STOP';
  @property({ type: String }) telegraphLabel = '停車';
  @property({ type: String }) engineState: EngineRunState | undefined = undefined;
  @property({ type: Number }) loadPct: number | undefined = undefined;

  private xOf(rpm: number): number {
    const max = this.rpmMax > 0 ? this.rpmMax : 180;
    return W / 2 + (clamp(rpm, -max, max) / max) * (W / 2 - PAD);
  }

  override render() {
    const x0 = this.xOf(0);
    const xr = this.xOf(this.rpm);
    const xo = this.xOf(this.rpmOrder);
    const astern = this.rpm < 0;
    const failed = this.engineState === 'failed';
    return html`
      <div class="tile">
        <div class="tile-title"><span>RPM</span><span class="zh">主機轉速</span></div>
        <div class="top">
          <span class="value big ${astern ? 'warning' : ''}">${formatRpm(this.rpm)}</span>
          <span class="label">令 <span class="value small" style="color: var(--ob-order)">${formatRpm(this.rpmOrder)}</span></span>
        </div>
        <div class="bar">
          <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-label="轉速">
            <line class="axis" x1=${PAD} y1="15" x2=${W - PAD} y2="15" />
            <rect class="fill ${astern ? 'astern' : ''}" x=${Math.min(x0, xr)} y="8" width=${Math.abs(xr - x0)} height="14" />
            <line class="zero" x1=${x0} y1="4" x2=${x0} y2="26" />
            <line class="order" x1=${xo} y1="2" x2=${xo} y2="28" />
            <text x=${PAD} y="29" font-size="9">ASTERN</text>
            <text x=${W - PAD} y="29" font-size="9" text-anchor="end">AHEAD</text>
          </svg>
        </div>
        <div class="bottom">
          <span class="teleg">${this.telegraph} <span class="label">${this.telegraphLabel}</span></span>
          <span class="${failed ? 'alarm' : 'muted'}">${this.engineState ? ENGINE_STATE_LABEL[this.engineState] : '—'}${this.loadPct !== undefined ? html` ${Math.round(this.loadPct)}%` : nothing}</span>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-engine': EngineReadout;
  }
}
