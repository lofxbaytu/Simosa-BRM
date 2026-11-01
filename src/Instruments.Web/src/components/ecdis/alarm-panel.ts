// <ecdis-alarm-panel>:頁面本地警報清單(docs/design/ecdis-lite.md 第 5 節):優先序、種類、文字、成立時間、狀態(未確認/已確認/已解除未確認)、
// 逐筆確認與全部確認。與核心 BAM 無關(不送指令)。

import { html, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { EcdisStoreElement, panelStyles } from './ecdis-shared.js';
import { ECDIS_ALARM_KIND_LABEL } from '../../lib/ecdis/alarms.js';
import { formatSimTime } from '../../lib/format.js';

const PRIORITY_LABEL = { alarm: '警報', warning: '警告', caution: '注意' } as const;

@customElement('ecdis-alarm-panel')
export class EcdisAlarmPanel extends EcdisStoreElement {
  static override styles = panelStyles;

  override render() {
    const s = this.store;
    const list = s.alarms.list();
    const unack = s.alarms.unacknowledged;
    return html`
      <h3>
        警報 ${list.length > 0 ? html`<span class="note">${list.length} 筆${unack > 0 ? `,未確認 ${unack}` : ''}</span>` : nothing}
        <span class="spacer"></span>
        ${unack > 0 ? html`<button class="alarm" @click=${() => s.acknowledgeAll()}>全部確認</button>` : nothing}
      </h3>
      ${list.length === 0 ? html`<div class="note">無警報</div>` : nothing}
      ${list.map(
        (a) => html`<div class="alarm-item ${a.priority} ${a.acknowledged ? '' : 'unack'} ${a.active && !a.acknowledged && a.priority === 'alarm' ? 'blink' : ''}" data-id=${a.id}>
          <span class="tag">${PRIORITY_LABEL[a.priority]} · ${ECDIS_ALARM_KIND_LABEL[a.kind]}</span>
          <span>
            <div class="text">${a.text}</div>
            <div class="meta">T ${formatSimTime(a.raisedAt_s)} · ${a.acknowledged ? '已確認' : a.active ? '未確認' : '已解除,待確認'}${a.count > 1 ? ` · ${a.count} 次` : ''}</div>
          </span>
          ${a.acknowledged ? nothing : html`<button @click=${() => s.acknowledge(a.id)}>確認</button>`}
        </div>`,
      )}
      <div class="note">頁面本地警報(安全等深線前方 ${s.settings.vectorMinutes} 分、XTD、CPA &lt; ${s.chart.minCpa_nm} nm 且 TCPA &lt; ${s.chart.tcpaLimit_min} 分、禁區、感測器);不送核心、不影響評估。</div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ecdis-alarm-panel': EcdisAlarmPanel;
  }
}
