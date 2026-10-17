// <brm-sensor-truth>:教官站「真值 vs 指示值」比較(docs/design/sensors-alerts.md 第 2 節):
// 航向(電羅經)、磁羅經、位置(GNSS)、COG/SOG、STW(計程儀)、龍骨下水深(測深儀)的指示值、真值(state.truth)與差距;
// 該感測器有相關故障、旗標無效或差距明顯時整列標紅(學員端儀器不顯示真值)。另列核心自動舵狀態(state.autopilot)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles } from './instructor-shared.js';
import type { OwnShipState } from '../../types/state.js';
import { sensorComparison } from '../../lib/alerts.js';
import { formatHeading } from '../../lib/format.js';

@customElement('brm-sensor-truth')
export class SensorTruthPanel extends LitElement {
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
        font-size: 12px;
      }
      th,
      td {
        padding: 2px 4px;
        border-bottom: 1px solid var(--ob-border);
        text-align: right;
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }
      th {
        color: var(--ob-text-secondary);
        font-weight: 500;
      }
      th:first-child,
      td:first-child {
        text-align: left;
      }
      tr.faulty td {
        color: var(--ob-alarm);
        font-weight: 700;
      }
      tr.faulty td.err {
        background: color-mix(in srgb, var(--ob-alarm) 18%, transparent);
      }
      .inv {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
        border-radius: 3px;
        padding: 0 3px;
        margin-left: 3px;
        font-size: 10px;
      }
    `,
  ];

  @property({ attribute: false }) state: OwnShipState | null = null;

  override render() {
    const s = this.state;
    const rows = s ? sensorComparison(s) : [];
    const ap = s?.autopilot;
    const faulty = rows.filter((r) => r.faulty).length;
    return html`
      <div class="panel">
        <div class="panel-title"><span>真值 vs 指示值 SENSORS</span><span class="zh">${rows.length === 0 ? '' : faulty > 0 ? html`<span class="alarm">異常 ${faulty}</span>` : '一致'}</span></div>
        ${rows.length === 0
          ? html`<div class="hint">${s ? '核心未回報真值(state.truth;Python 參考伺服器不支援)' : '等待核心資料'}</div>`
          : html`<table aria-label="真值與指示值">
              <thead><tr><th>項目</th><th>指示</th><th>真值</th><th>差距</th></tr></thead>
              <tbody>
                ${rows.map(
                  (r) => html`<tr class=${r.faulty ? 'faulty' : ''} data-row=${r.key}>
                    <td>${r.label}${r.invalid ? html`<span class="inv">無效</span>` : nothing}</td>
                    <td>${r.indicated}</td>
                    <td>${r.truth}</td>
                    <td class="err">${r.error}</td>
                  </tr>`,
                )}
              </tbody>
            </table>`}
        ${ap
          ? html`<div class="hint" data-autopilot>
              自動舵 ${ap.enabled ? `啟用 設定 ${formatHeading(ap.setHeading)}° · ROT ${Math.round(ap.rotLimit_degPerMin)}°/min` : '未啟用'}${ap.available ? '' : html` · <span class="alarm">不可用</span>`}
            </div>`
          : nothing}
        ${(s?.faults ?? []).length > 0 ? html`<div class="hint">現行故障:${(s?.faults ?? []).join('、')}</div>` : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-sensor-truth': SensorTruthPanel;
  }
}
