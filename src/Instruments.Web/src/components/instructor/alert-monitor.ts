// <brm-alert-monitor>:教官站的警報與 BNWAS 監看(docs/design/sensors-alerts.md 第 3、4 節;規劃書第 9.1 節):
//  - 現行警報(state.alerts,唯讀顯示 + 「代為確認」送 alertAcknowledge {id}、「全部確認」、「靜音」);
//  - 警報歷史(由事件流 alertRaised/Acknowledged/Rectified/Escalated/Silenced 推得;最新在上,含確認耗時);
//  - BNWAS 狀態(階段、倒數、各階段次數)與模式切換(bnwasMode off|auto|on + 休眠期 180–720 s)、重設(bnwasReset);
//  - compact:簡化教官模式用,只有現行警報與 BNWAS 狀態列。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import { alertIcon, alertIconStyles } from '../alert-icon.js';
import type { AlertItem, BnwasMode, BnwasStatus } from '../../types/alerts.js';
import { BNWAS_MODES } from '../../types/alerts.js';
import type { SimEvent } from '../../types/event.js';
import {
  ALERT_PRIORITY_LABEL,
  ALERT_SOURCE_LABEL,
  ALERT_STATE_LABEL,
  BNWAS_DORMANT_MAX_S,
  BNWAS_DORMANT_MIN_S,
  BNWAS_MODE_LABEL,
  ALERT_STATION_LABEL,
  alertAcknowledgeCommand,
  alertCounts,
  alertHistory,
  alertSilenceCommand,
  alertVisual,
  responsibleStation,
  bnwasCounts,
  bnwasDisplay,
  bnwasModeCommand,
  bnwasResetCommand,
  silenceRemaining,
  sortAlerts,
} from '../../lib/alerts.js';

const HISTORY_MAX = 40;

@customElement('brm-alert-monitor')
export class AlertMonitor extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    alertIconStyles,
    css`
      :host {
        display: block;
      }
      .alert-icon {
        width: 18px;
        height: 18px;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 12px;
      }
      td {
        padding: 2px 4px;
        border-bottom: 1px solid var(--ob-border);
        vertical-align: middle;
      }
      td.t {
        font-variant-numeric: tabular-nums;
        color: var(--ob-text-muted);
        white-space: nowrap;
      }
      td.txt {
        font-weight: 600;
      }
      td.st {
        white-space: nowrap;
      }
      tr.unack td.st {
        color: var(--ob-alarm);
        font-weight: 700;
      }
      tr.tone-warning.unack td.st {
        color: var(--ob-warning);
      }
      td button {
        min-height: 22px;
        padding: 1px 6px;
        font-size: 11px;
        white-space: nowrap;
      }
      td.pr {
        white-space: nowrap;
      }
      .sub {
        font-size: 11px;
        font-weight: 600;
        color: var(--ob-text-secondary);
        margin-top: 2px;
      }
      .bnwas {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 4px 8px;
        align-items: center;
        padding: 4px 6px;
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        font-size: 12px;
      }
      .bnwas.level-caution {
        border-color: var(--ob-caution);
        background: color-mix(in srgb, var(--ob-caution) 20%, var(--ob-bg-surface));
      }
      .bnwas.level-alarm {
        border-color: var(--ob-alarm);
        background: color-mix(in srgb, var(--ob-alarm) 28%, var(--ob-bg-surface));
      }
      .bnwas .stage {
        font-weight: 700;
      }
      .bnwas .cd {
        font-variant-numeric: tabular-nums;
        color: var(--ob-text-secondary);
      }
      .mode-row {
        display: flex;
        gap: 4px;
        align-items: center;
        flex-wrap: wrap;
        font-size: 12px;
      }
      .mode-row input {
        width: 64px;
      }
      .seg {
        display: inline-flex;
        border: 1px solid var(--ob-border-strong);
        border-radius: 4px;
        overflow: hidden;
      }
      .seg button {
        border: none;
        border-radius: 0;
        min-height: 24px;
        padding: 2px 8px;
      }
      .seg button + button {
        border-left: 1px solid var(--ob-border-strong);
      }
      .hist {
        max-height: 150px;
        overflow-y: auto;
      }
      .late {
        color: var(--ob-warning);
      }
      .open {
        color: var(--ob-alarm);
      }
    `,
  ];

  @property({ attribute: false }) alerts: readonly AlertItem[] = [];
  @property({ attribute: false }) bnwas: BnwasStatus | undefined = undefined;
  @property({ attribute: false }) events: readonly SimEvent[] = [];
  @property({ type: Number }) simTime = 0;
  /** 核心不回報警報/BNWAS(Python 參考伺服器):按鈕停用、標示僅顯示 */
  @property({ type: Boolean }) unsupported = false;
  @property({ type: Boolean, reflect: true }) compact = false;

  /** 模式切換表單的休眠期(空白 = 不變更) */
  @state() private dormantInput = '';

  private ack(id?: string): void {
    dispatchCommand(this, alertAcknowledgeCommand(id));
  }

  private setMode(mode: BnwasMode): void {
    const v = this.dormantInput.trim() === '' ? undefined : Number(this.dormantInput);
    dispatchCommand(this, bnwasModeCommand(mode, v !== undefined && Number.isFinite(v) ? v : undefined));
  }

  override render() {
    const list = sortAlerts(this.alerts);
    const counts = alertCounts(list);
    const d = bnwasDisplay(this.bnwas);
    const bc = bnwasCounts(this.events);
    return html`
      <div class="panel">
        <div class="panel-title">
          <span>警報 / BNWAS ALERTS</span>
          <span class="zh">${this.unsupported ? html`<span class="tag unsupported">核心不支援,僅顯示</span>` : counts.total > 0 ? `現行 ${counts.total} · 未確認 ${counts.unacknowledged}` : '無警報'}</span>
        </div>
        ${list.length > 0
          ? html`<table aria-label="現行警報"><tbody>
              ${list.map((a) => {
                const v = alertVisual(a);
                const sil = silenceRemaining(a, this.simTime);
                return html`<tr class="tone-${v.tone} ${v.needsAck ? 'unack' : ''}" data-alert=${a.id}>
                  <td>${alertIcon(v, `${ALERT_PRIORITY_LABEL[a.priority]}・${ALERT_STATE_LABEL[a.state]}`)}</td>
                  <td class="txt" title="${a.id} · ${ALERT_SOURCE_LABEL[a.source] ?? a.source} · ${a.category} · 負責站 ${ALERT_STATION_LABEL[responsibleStation(a)]}">${a.text}${a.escalated ? html`<span class="tag">已升級</span>` : nothing}${a.count > 1 ? html`<span class="tag">×${a.count}</span>` : nothing}${a.transferredTo ? html`<span class="tag transferred" title="責任已轉移">→ ${ALERT_STATION_LABEL[a.transferredTo]}</span>` : a.category === 'A' ? html`<span class="tag" title="Category A:只能在負責站確認">${ALERT_STATION_LABEL[responsibleStation(a)]}</span>` : nothing}</td>
                  <td class="st">${ALERT_STATE_LABEL[a.state]}${sil !== undefined ? ` ${Math.ceil(sil)} s` : ''}</td>
                  <td class="t">${shortTime(a.raisedAt_s)}</td>
                  <td>${v.needsAck ? html`<button class="ack-for" title="教官代為確認(送 alertAcknowledge)" ?disabled=${this.unsupported} @click=${() => this.ack(a.id)}>代為確認</button>` : nothing}</td>
                </tr>`;
              })}
            </tbody></table>
            ${this.compact
              ? nothing
              : html`<div class="btn-row">
                  <button class="ack-all" ?disabled=${this.unsupported || counts.unacknowledged === 0} @click=${() => this.ack()}>全部確認</button>
                  <button class="silence-all" ?disabled=${this.unsupported || counts.audible === 0} @click=${() => dispatchCommand(this, alertSilenceCommand())}>靜音 30 s</button>
                </div>`}`
          : nothing}
        <div class="bnwas level-${d.level}" data-stage=${this.bnwas?.stage ?? -1}>
          <span><span class="stage">BNWAS ${d.label}</span>${d.countdown ? html` · <span class="cd">${d.countdown}</span>` : nothing}</span>
          <button class="bnwas-reset" ?disabled=${this.unsupported || !d.resettable} @click=${() => dispatchCommand(this, bnwasResetCommand())}>重設</button>
          ${this.compact ? nothing : html`<span class="cd">第 1 階段 ${bc.stage1} 次 · 第 2 階段 ${bc.stage2} 次 · 第 3 階段 ${bc.stage3} 次 · 重設 ${bc.resets} 次</span>`}
        </div>
        ${this.compact ? nothing : this.renderModeRow()}
        ${this.compact ? nothing : this.renderHistory()}
      </div>
    `;
  }

  private renderModeRow() {
    const mode = this.bnwas?.mode;
    return html`<div class="mode-row">
      <span class="label">模式</span>
      <span class="seg" role="group" aria-label="BNWAS 模式">
        ${BNWAS_MODES.map((m) => html`<button class="bnwas-mode ${mode === m ? 'active' : ''}" data-mode=${m} ?disabled=${this.unsupported} @click=${() => this.setMode(m)}>${BNWAS_MODE_LABEL[m]}</button>`)}
      </span>
      <label class="label">休眠 s <input type="number" min=${BNWAS_DORMANT_MIN_S} max=${BNWAS_DORMANT_MAX_S} step="30" placeholder=${String(Math.round(this.bnwas?.dormant_s ?? BNWAS_DORMANT_MAX_S))} .value=${this.dormantInput} @input=${(e: Event) => (this.dormantInput = (e.target as HTMLInputElement).value)} /></label>
      <span class="hint">實船受鑰匙/密碼保護;休眠期 ${BNWAS_DORMANT_MIN_S}–${BNWAS_DORMANT_MAX_S} s</span>
    </div>`;
  }

  private renderHistory() {
    const hist = alertHistory(this.events).slice(-HISTORY_MAX).reverse();
    return html`<div class="sub">警報歷史(事件流;最新在上)</div>
      ${hist.length === 0
        ? html`<div class="hint">尚無警報事件</div>`
        : html`<div class="hist"><table aria-label="警報歷史"><tbody>
            ${hist.map(
              (h) => html`<tr data-history=${h.id}>
                <td class="t">${shortTime(h.raisedAt_s)}</td>
                <td class="txt">${h.text}${h.escalated ? html`<span class="tag">已升級</span>` : nothing}${h.repeat ? html`<span class="tag">再成立</span>` : nothing}</td>
                <td class="pr">${ALERT_PRIORITY_LABEL[h.priority]}</td>
                <td class="st ${h.ackTime_s === undefined ? (h.open ? 'open' : '') : h.ackTime_s > 30 ? 'late' : ''}">${h.ackTime_s !== undefined ? `確認 +${h.ackTime_s.toFixed(1)} s` : h.open ? '未確認' : '未確認即解除'}</td>
                <td class="t">${h.rectifiedAt_s !== undefined ? `解除 ${shortTime(h.rectifiedAt_s)}` : h.open ? '現行' : ''}${h.silences > 0 ? ` · 靜音 ${h.silences}` : ''}</td>
              </tr>`,
            )}
          </tbody></table></div>`}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-alert-monitor': AlertMonitor;
  }
}
