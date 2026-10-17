// 航線/能見度方塊(規劃書第 7.2 節 conning display 的 XTD/下一航點最低資訊;設計文件第 6 節 W11):
// 由 state.route 顯示航跡偏差 XTD(右正左負,顯示為 L/R + 走廊半寬條)、下一航點名稱/距離/方位、航段航向、速限;
// state.visibility_nm 顯示能見度。核心無航線(無 route 欄位)時顯示「無航線」。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import type { RouteStatus } from '../types/state.js';
import { formatHeading } from '../lib/format.js';

@customElement('brm-route')
export class RouteReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .xtd {
        display: flex;
        align-items: baseline;
        gap: 4px;
      }
      .xtd .side {
        font-size: 12px;
        font-weight: 600;
        color: var(--ob-text-secondary);
      }
      .bar {
        position: relative;
        height: 8px;
        border: 1px solid var(--ob-border-strong);
        border-radius: 4px;
        margin: 3px 0;
        background: var(--ob-bg-input);
      }
      .bar .center {
        position: absolute;
        left: 50%;
        top: -2px;
        bottom: -2px;
        width: 1px;
        background: var(--ob-scale);
      }
      .bar .pos {
        position: absolute;
        top: -1px;
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: var(--ob-accent);
        transform: translateX(-50%);
      }
      .bar.over .pos {
        background: var(--ob-warning);
      }
      .rows {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 1px 6px;
        font-size: 11px;
        margin-top: 2px;
      }
      .rows .v {
        text-align: right;
        font-variant-numeric: tabular-nums;
        font-weight: 600;
      }
      .value.warn {
        color: var(--ob-warning);
      }
    `,
  ];

  @property({ attribute: false }) route: RouteStatus | undefined = undefined;
  @property({ type: Number }) visibility: number | undefined = undefined;

  override render() {
    const r = this.route;
    const xtd = r?.xtd_m;
    const limit = r?.xtdLimit_m;
    const over = xtd !== undefined && limit !== undefined && limit > 0 && Math.abs(xtd) > limit;
    const frac = xtd !== undefined && limit !== undefined && limit > 0 ? Math.max(-1, Math.min(1, xtd / limit)) : 0;
    return html`
      <div class="tile">
        <div class="tile-title"><span>ROUTE</span><span class="zh">${r?.complete ? '航線完成' : r?.nextWaypoint ? `→ ${r.nextWaypoint}` : '航線'}</span></div>
        ${r && xtd !== undefined
          ? html`<div class="xtd">
                <span class="value mid ${over ? 'warn' : ''}">${Math.abs(xtd) < 0.5 ? '0' : Math.round(Math.abs(xtd))}<span class="unit">m</span></span>
                <span class="side">${xtd > 0.5 ? 'R 偏右' : xtd < -0.5 ? 'L 偏左' : '在線上'}</span>
                ${limit !== undefined ? html`<span class="label">限 ${Math.round(limit)} m</span>` : nothing}
              </div>
              ${limit !== undefined && limit > 0
                ? html`<div class="bar ${over ? 'over' : ''}" aria-label="XTD"><span class="center"></span><span class="pos" style="left:${50 + frac * 50}%"></span></div>`
                : nothing}`
          : html`<div class="label muted">${r ? 'XTD --' : '無航線'}</div>`}
        <div class="rows">
          ${r?.nextWaypoint ? html`<span class="label">下一航點</span><span class="v">${r.nextWaypoint}${r.distanceToWaypoint_nm !== undefined ? ` ${r.distanceToWaypoint_nm.toFixed(2)} nm` : ''}</span>` : nothing}
          ${r?.bearingToWaypoint_deg !== undefined ? html`<span class="label">方位 / 航段</span><span class="v">${formatHeading(r.bearingToWaypoint_deg)}${r.legCourse_deg !== undefined ? ` / ${formatHeading(r.legCourse_deg)}` : ''}</span>` : nothing}
          ${r?.speedLimit_kn !== undefined ? html`<span class="label">速限</span><span class="v">${r.speedLimit_kn} kn</span>` : nothing}
          ${this.visibility !== undefined ? html`<span class="label">能見度</span><span class="v ${this.visibility < 1 ? 'warning' : ''}">${this.visibility.toFixed(1)} nm</span>` : nothing}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-route': RouteReadout;
  }
}
