// 自動講評摘要顯示(規劃書第 8.3 節指標子集):表格列出各指標、發生時間與閾值;超限以警告色標示。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import type { DebriefSummary } from '../../lib/debrief.js';

@customElement('brm-debrief-summary')
export class DebriefSummaryView extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 13px;
      }
      :host([large]) table {
        font-size: 18px;
      }
      td,
      th {
        padding: 3px 6px;
        border-bottom: 1px solid var(--ob-border);
        text-align: left;
      }
      th {
        color: var(--ob-text-secondary);
        font-weight: 500;
        white-space: nowrap;
      }
      td.num {
        text-align: right;
        font-variant-numeric: tabular-nums;
        font-weight: 600;
        white-space: nowrap;
      }
      td.at {
        color: var(--ob-text-muted);
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }
      tr.over td.num {
        color: var(--ob-warning);
      }
      tr.alarm td.num {
        color: var(--ob-alarm);
      }
    `,
  ];

  @property({ attribute: false }) summary: DebriefSummary | null = null;
  @property({ type: Boolean, reflect: true }) large = false;

  override render() {
    const s = this.summary;
    if (!s || s.samples === 0) return html`<div class="hint">尚無資料</div>`;
    const th = s.thresholds;
    const at = (t: number | null) => (t === null ? '' : shortTime(t));
    const row = (label: string, value: string, time = '', cls = '') => html`<tr class=${cls}><th>${label}</th><td class="num">${value}</td><td class="at">${time}</td></tr>`;
    return html`
      <table aria-label="自動講評摘要">
        <tbody>
          ${row('練習時間', shortTime(s.duration_s), s.frozen_s > 0 ? `凍結 ${shortTime(s.frozen_s)}` : '')}
          ${row('航程', `${s.distance_nm.toFixed(2)} nm`)}
          ${row('平均速度(對地)', `${s.avgSpeed_kn.toFixed(1)} kn`, `最大 ${s.maxSog_kn.toFixed(1)} kn`)}
          ${row(`超過港區速限 ${th.portSpeedLimit_kn} kn 的時間`, shortTime(s.timeOverSpeedLimit_s), '', s.timeOverSpeedLimit_s > 0 ? 'over' : '')}
          ${row('最小 UKC', s.minUkc_m === null ? '--' : `${s.minUkc_m.toFixed(2)} m`, at(s.minUkcAt_s), s.minUkc_m !== null && s.minUkc_m < th.ukcAlarm_m ? 'alarm' : '')}
          ${row(`UKC 低於 ${th.ukcAlarm_m.toFixed(1)} m 的時間`, shortTime(s.timeUkcBelowAlarm_s), '', s.timeUkcBelowAlarm_s > 0 ? 'alarm' : '')}
          ${row('最大舵角', `${s.maxRudder_deg.toFixed(1)}°`, at(s.maxRudderAt_s), s.hardOverCount > 0 ? 'over' : '')}
          ${row('滿舵次數', `${s.hardOverCount}`, `≥ ${th.hardOver_deg}°`)}
          ${row('舵令變更次數', `${s.rudderOrders}`)}
          ${row('最大 ROT', `${s.maxRot_degPerMin.toFixed(1)}°/min`, at(s.maxRotAt_s), s.maxRot_degPerMin > th.rotLimit_degPerMin ? 'over' : '')}
          ${row(`ROT 超過 ${th.rotLimit_degPerMin}°/min 的時間`, shortTime(s.timeRotOverLimit_s), '', s.timeRotOverLimit_s > 0 ? 'over' : '')}
          ${row('車鐘變更次數', `${s.telegraphChanges}`)}
          ${s.agroundEvents > 0 || s.collisionEvents > 0 ? row('擱淺 / 碰撞', `${s.agroundEvents} / ${s.collisionEvents}`, '', 'alarm') : nothing}
        </tbody>
      </table>
      <div class="hint">閾值為第一版暫用常數(速限 ${th.portSpeedLimit_kn} kn、UKC ${th.ukcAlarm_m} m、ROT ${th.rotLimit_degPerMin}°/min、滿舵 ${th.hardOver_deg}°),之後由公司程序參數集提供(規劃書第 9.1 節)</div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-debrief-summary': DebriefSummaryView;
  }
}
