// <brm-alert-panel>:操船台的中央警報面板(BAM,MSC.302(87);規劃書第 7.2 節「中央警報面板」)。
//  - 清單依優先序(緊急警報 → 警報 → 警告 → 注意)與成立時間排序(lib/alerts.ts sortAlerts,與核心相同);
//  - 顏色/圖示依 IEC 62288 概念(alert-icon.ts):alarm 紅閃、warning 黃橙、caution 黃不閃;未確認閃爍、靜音與已解除未確認有附加圖示;
//  - 每列「確認」送 alertAcknowledge {id};標題列「靜音 30 s」送 alertSilence(全部)、「全部確認」送 alertAcknowledge(無 id);
//  - 「聲響 開/關」是本頁設定(不送核心),以 alert-sound-toggle 事件通知 <brm-app>;
//  - 核心未支援警報(Python 參考伺服器)時顯示「核心未回報警報」。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import { alertIcon, alertIconStyles } from './alert-icon.js';
import type { AlertItem } from '../types/alerts.js';
import {
  ALERT_PRIORITY_LABEL,
  ALERT_SOURCE_LABEL,
  ALERT_STATE_LABEL,
  alertAcknowledgeCommand,
  alertCounts,
  alertSilenceCommand,
  alertVisual,
  silenceRemaining,
  sortAlerts,
} from '../lib/alerts.js';
import { formatSimTime } from '../lib/format.js';

export const ALERT_SOUND_TOGGLE_EVENT = 'alert-sound-toggle';

@customElement('brm-alert-panel')
export class AlertPanel extends LitElement {
  static override styles = [
    tileStyles,
    alertIconStyles,
    css`
      :host {
        display: block;
      }
      .tile {
        gap: 4px;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 4px;
        flex-wrap: wrap;
      }
      .head .title {
        font-size: 12px;
        font-weight: 700;
        color: var(--ob-text-secondary);
        margin-right: auto;
        white-space: nowrap;
      }
      .head .count {
        font-size: 11px;
        font-weight: 700;
        border-radius: 9px;
        padding: 0 7px;
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      .head .count.warning {
        background: var(--ob-warning);
        color: #1a1000;
      }
      .head button {
        min-height: 24px;
        padding: 2px 6px;
        font-size: 11px;
      }
      .list {
        flex: 1;
        min-height: 0;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 3px;
      }
      .alert {
        display: grid;
        grid-template-columns: auto minmax(0, 1fr) auto;
        gap: 2px 6px;
        align-items: center;
        padding: 3px 5px;
        border-radius: 4px;
        border: 1px solid var(--ob-border);
        border-left-width: 4px;
        background: var(--ob-bg-surface-raised);
        font-size: 12px;
      }
      .alert.tone-alarm,
      .alert.tone-emergency {
        border-left-color: var(--ob-alarm);
      }
      .alert.tone-warning {
        border-left-color: var(--ob-warning);
      }
      .alert.tone-caution {
        border-left-color: var(--ob-caution);
      }
      .alert.unack.tone-alarm,
      .alert.unack.tone-emergency {
        background: color-mix(in srgb, var(--ob-alarm) 18%, var(--ob-bg-surface));
      }
      .alert.unack.tone-warning {
        background: color-mix(in srgb, var(--ob-warning) 16%, var(--ob-bg-surface));
      }
      .alert .text {
        font-weight: 700;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .alert .meta {
        grid-column: 2 / 3;
        grid-row: 2 / 3;
        font-size: 10.5px;
        color: var(--ob-text-secondary);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      /* 明確指定欄位:有 grid-row 的項目會先於自動排列放置,不指定欄位會把按鈕擠到第 2 欄 */
      .alert .icon {
        grid-column: 1 / 2;
        grid-row: 1 / 3;
        display: flex;
      }
      .alert .text {
        grid-column: 2 / 3;
        grid-row: 1 / 2;
      }
      .alert .ack,
      .alert .ack-slot {
        grid-column: 3 / 4;
        grid-row: 1 / 3;
        min-height: 26px;
        padding: 2px 8px;
        font-weight: 700;
      }
      .alert .state {
        font-weight: 600;
      }
      .alert.unack .state {
        color: var(--ob-alarm);
      }
      .alert.tone-warning.unack .state {
        color: var(--ob-warning);
      }
      .tag {
        font-size: 10px;
        border: 1px solid var(--ob-border-strong);
        border-radius: 3px;
        padding: 0 3px;
        margin-left: 3px;
      }
      .empty {
        font-size: 12px;
        color: var(--ob-ok);
        padding: 6px 2px;
      }
      .empty.muted {
        color: var(--ob-text-muted);
      }
      button.sound.off {
        border-color: var(--ob-warning);
        color: var(--ob-warning);
      }
    `,
  ];

  /** state.alerts(省略時 = 無警報) */
  @property({ attribute: false }) alerts: readonly AlertItem[] = [];
  /** 模擬時間(靜音倒數) */
  @property({ type: Number }) simTime = 0;
  /** 本頁聲響設定 */
  @property({ type: Boolean }) soundOn = true;
  /** 核心不回報警報(Python 參考伺服器、舊紀錄) */
  @property({ type: Boolean }) unsupported = false;

  private ack(id?: string): void {
    dispatchCommand(this, alertAcknowledgeCommand(id));
  }

  private silence(): void {
    dispatchCommand(this, alertSilenceCommand());
  }

  private toggleSound(): void {
    this.dispatchEvent(new CustomEvent<boolean>(ALERT_SOUND_TOGGLE_EVENT, { detail: !this.soundOn, bubbles: true, composed: true }));
  }

  override render() {
    const list = sortAlerts(this.alerts);
    const counts = alertCounts(list);
    // 徽章數字是需確認者,顏色也只看需確認者(已確認的 alarm 不讓徽章變紅)
    const countTone = counts.highestUnacknowledged === 'warning' || counts.highestUnacknowledged === 'caution' ? 'warning' : '';
    return html`
      <div class="tile" role="region" aria-label="中央警報面板">
        <div class="head">
          <span class="title">警報</span>
          ${counts.unacknowledged > 0 ? html`<span class="count ${countTone}" title="需確認的警報">未確認 ${counts.unacknowledged}</span>` : nothing}
          <button class="silence" title="靜音 30 s(期滿未確認會重響)" ?disabled=${counts.audible === 0} @click=${this.silence}>靜音 30s</button>
          <button class="ack-all" title="確認全部警報" ?disabled=${counts.unacknowledged === 0} @click=${() => this.ack()}>全部確認</button>
          <button class="sound ${this.soundOn ? '' : 'off'}" title="本頁警報聲響(不影響核心)" aria-pressed=${this.soundOn} @click=${this.toggleSound}>聲響 ${this.soundOn ? '開' : '關'}</button>
        </div>
        <div class="list" role="list">
          ${list.length === 0
            ? html`<div class="empty ${this.unsupported ? 'muted' : ''}">${this.unsupported ? '核心未回報警報(Python 參考伺服器不支援)' : '無警報'}</div>`
            : list.map((a) => this.renderAlert(a))}
        </div>
      </div>
    `;
  }

  private renderAlert(a: AlertItem) {
    const v = alertVisual(a);
    const silenced = silenceRemaining(a, this.simTime);
    const stateText = silenced !== undefined ? `${ALERT_STATE_LABEL[a.state]} ${Math.ceil(silenced)} s` : ALERT_STATE_LABEL[a.state];
    const title = `${ALERT_PRIORITY_LABEL[a.priority]}・${ALERT_STATE_LABEL[a.state]}`;
    return html`<div class="alert tone-${v.tone} ${v.needsAck ? 'unack' : ''}" role="listitem" data-alert=${a.id} data-state=${a.state}>
      <span class="icon">${alertIcon(v, title)}</span>
      <span class="text" title=${a.text}>${a.text}${a.count > 1 ? html`<span class="tag">×${a.count}</span>` : nothing}${a.escalated ? html`<span class="tag">已升級</span>` : nothing}</span>
      ${v.needsAck ? html`<button class="ack" data-ack=${a.id} @click=${() => this.ack(a.id)}>確認</button>` : html`<span class="ack-slot"></span>`}
      <span class="meta"><span class="state">${stateText}</span> · ${ALERT_PRIORITY_LABEL[a.priority]} ${a.category} · ${ALERT_SOURCE_LABEL[a.source] ?? a.source} · ${String(a.number).padStart(3, '0')} · ${formatSimTime(a.raisedAt_s)}</span>
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-alert-panel': AlertPanel;
  }
  interface HTMLElementEventMap {
    'alert-sound-toggle': CustomEvent<boolean>;
  }
}
