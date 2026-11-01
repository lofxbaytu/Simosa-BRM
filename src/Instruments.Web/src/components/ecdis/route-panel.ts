// <ecdis-route-panel>:航線監控面板(docs/design/ecdis-lite.md 第 4 節):航線名、目前航段、下一航點、DTG/TTG、XTD(走廊條)、預定航向、
// 到航點方位、下一轉向與轉向點倒數、剩餘總程、航點清單(點選手動切換航段)。無情境航線時說明。

import { html, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { EcdisStoreElement, panelStyles } from './ecdis-shared.js';
import { formatTtg } from '../../lib/ecdis/route-monitor.js';
import { formatBearing, formatRangeNm, M_PER_NM } from '../../radar/geometry.js';

@customElement('ecdis-route-panel')
export class EcdisRoutePanel extends EcdisStoreElement {
  static override styles = panelStyles;

  override render() {
    const s = this.store;
    const route = s.chart.route;
    const rs = s.routeStatus;
    if (!route) {
      return html`<h3>航線監控</h3>
        <div class="note">${s.chart.scenarioId ? `情境 ${s.chart.scenarioId} 無航線(route)` : '尚未載入情境:無航線可監控'}</div>`;
    }
    if (!rs) return html`<h3>航線監控</h3><div class="note">${route.name || '航線'}:等待狀態…</div>`;
    const limit = rs.xtdLimit_m;
    const pct = limit ? Math.max(-1, Math.min(1, rs.xtd_m / limit)) : 0;
    const left = 50 + pct * 50;
    const xtdText = `${Math.abs(rs.xtd_m) < 0.5 ? '0' : Math.round(Math.abs(rs.xtd_m))} m ${rs.xtd_m >= 0.5 ? 'R' : rs.xtd_m <= -0.5 ? 'L' : ''}`.trimEnd();
    return html`
      <h3>航線監控 <span class="spacer"></span><span class="note">${route.name || ''}</span></h3>
      <div class="kv">
        <span class="k">航段</span><span class="v">${rs.complete ? '已抵達終點' : `${rs.legIndex + 1} / ${rs.legCount}`}</span>
        <span class="k">下一航點</span><span class="v">${rs.nextWaypoint.name}${rs.nextWaypoint.checkpoint ? ' ✓' : ''}${rs.nextWaypoint.report ? ' 📡' : ''}</span>
        <span class="k">DTG</span><span class="v">${formatRangeNm(rs.dtg_m / M_PER_NM)} NM</span>
        <span class="k">TTG(SOG)</span><span class="v ${rs.ttg_s === null ? 'muted' : ''}">${formatTtg(rs.ttg_s)}</span>
        <span class="k">XTD</span><span class="v ${rs.xtdExceeded ? 'alarm' : ''}">${xtdText}${limit !== undefined ? html` <span class="muted">/ ${limit} m</span>` : html` <span class="muted">(走廊未知)</span>`}</span>
        <span class="k">預定航向</span><span class="v">${formatBearing(rs.plannedCourse_deg)}</span>
        <span class="k">到航點方位</span><span class="v">${formatBearing(rs.bearingToWaypoint_deg)}</span>
        <span class="k">下一轉向</span><span class="v ${rs.turn_deg === null ? 'muted' : ''}">${rs.turn_deg === null ? '終點' : `${rs.turn_deg >= 0 ? '右' : '左'} ${Math.abs(rs.turn_deg).toFixed(0)}° → ${formatBearing(rs.nextCourse_deg ?? 0)}`}</span>
        <span class="k">轉向點倒數</span><span class="v ${rs.turn_deg === null ? 'muted' : rs.ttg_s !== null && rs.ttg_s < 120 ? 'warn' : ''}">${rs.turn_deg === null ? '—' : formatTtg(rs.ttg_s)}</span>
        <span class="k">剩餘總程</span><span class="v muted">${formatRangeNm(rs.dtgTotal_m / M_PER_NM)} NM</span>
      </div>
      ${limit !== undefined
        ? html`<div class="bar" title="走廊 ±${limit} m"><span class="centre"></span><span class="mark ${rs.xtdExceeded ? 'over' : ''}" style="left:calc(${left.toFixed(1)}% - 1px)"></span></div>`
        : nothing}
      <div class="row" style="margin-top:6px">
        <span class="label">航點</span>
        ${route.waypoints.map(
          (wp, i) => html`<button class=${i === rs.nextIndex && !rs.complete ? 'active' : ''} ?disabled=${i === 0} title=${i === 0 ? '起點' : `切到航段 ${i}(到 ${wp.name})`} @click=${() => s.setLeg(i - 1)}>${wp.name}</button>`,
        )}
      </div>
      <div class="note">頁面本地計算(抵達半徑/越過航點垂線切換);核心 state.route 為評估依據。</div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ecdis-route-panel': EcdisRoutePanel;
  }
}
