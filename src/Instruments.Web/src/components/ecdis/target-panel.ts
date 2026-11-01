// <ecdis-target-panel>:AIS 目標表(危險者在前、依距離)與選取目標的資料(MMSI、呼號、船型、尺寸、航向/COG/SOG、ROT、吃水、目的地、CPA/TCPA/BCR)。
// 資料來源 state.targets(aisOn 者);位置/COG/SOG 優先取 AIS 報告值(含注入錯誤)。

import { html, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { EcdisStoreElement, panelStyles } from './ecdis-shared.js';
import { formatBearing, formatRangeNm } from '../../radar/geometry.js';

const NAV_STATUS: Readonly<Record<number, string>> = {
  0: '航行中(主機)',
  1: '錨泊',
  2: '失控',
  3: '操縱能力受限',
  4: '吃水受限',
  5: '繫泊',
  6: '擱淺',
  7: '捕魚',
  8: '帆船航行',
  15: '未定義',
};

@customElement('ecdis-target-panel')
export class EcdisTargetPanel extends EcdisStoreElement {
  static override styles = panelStyles;

  override render() {
    const s = this.store;
    const targets = s.targets;
    const sel = s.selectedTarget;
    return html`
      <h3>AIS 目標 <span class="note">${targets.length} 艘${targets.filter((t) => t.dangerous).length > 0 ? `,危險 ${targets.filter((t) => t.dangerous).length}` : ''}</span></h3>
      ${targets.length === 0
        ? html`<div class="note">無 AIS 目標</div>`
        : html`<table>
            <thead><tr><th>船名</th><th>BRG</th><th>RNG</th><th>CPA</th><th>TCPA</th><th>COG/SOG</th></tr></thead>
            <tbody>
              ${targets.slice(0, 12).map(
                (t) => html`<tr class="${t.dangerous ? 'danger' : ''} ${sel?.id === t.id ? 'selected' : ''}" data-id=${t.id} @click=${() => s.selectTarget(sel?.id === t.id ? null : t.id)}>
                  <td>${t.label}</td>
                  <td>${formatBearing(t.bearing_deg)}</td>
                  <td>${formatRangeNm(t.range_nm)}</td>
                  <td>${t.cpa_nm.toFixed(2)}</td>
                  <td>${t.tcpa_min.toFixed(1)}</td>
                  <td>${formatBearing(t.cog)}/${t.sog.toFixed(1)}</td>
                </tr>`,
              )}
            </tbody>
          </table>`}
      ${sel
        ? html`<div class="kv" style="margin-top:6px">
            <span class="k">選取</span><span class="v">${sel.label}</span>
            <span class="k">MMSI</span><span class="v">${sel.mmsi || '—'}</span>
            <span class="k">呼號</span><span class="v">${sel.raw.ais?.callSign ?? '—'}</span>
            <span class="k">船型</span><span class="v">${sel.shipType}${sel.raw.ais?.shipType !== undefined ? ` (${sel.raw.ais.shipType})` : ''}</span>
            <span class="k">航行狀態</span><span class="v">${sel.raw.ais?.navStatus !== undefined ? NAV_STATUS[sel.raw.ais.navStatus] ?? String(sel.raw.ais.navStatus) : '—'}</span>
            <span class="k">尺寸</span><span class="v">${sel.loa.toFixed(0)} × ${sel.beam.toFixed(0)} m${sel.raw.ais?.draught !== undefined ? `,吃水 ${sel.raw.ais.draught.toFixed(1)} m` : ''}</span>
            <span class="k">HDG / COG</span><span class="v">${formatBearing(sel.heading)} / ${formatBearing(sel.cog)}</span>
            <span class="k">SOG / ROT</span><span class="v">${sel.sog.toFixed(1)} kn / ${(sel.raw.ais?.rot ?? sel.raw.rot).toFixed(0)}°/min</span>
            <span class="k">CPA / TCPA</span><span class="v ${sel.dangerous ? 'alarm' : ''}">${sel.cpa_nm.toFixed(2)} NM / ${sel.tcpa_min.toFixed(1)} 分</span>
            <span class="k">BCR / BCT</span><span class="v muted">${sel.raw.bcr_nm !== undefined ? `${sel.raw.bcr_nm.toFixed(2)} NM` : '—'} / ${sel.raw.bct_min !== undefined ? `${sel.raw.bct_min.toFixed(1)} 分` : '—'}</span>
            <span class="k">目的地</span><span class="v muted">${sel.raw.ais?.destination ?? '—'}</span>
          </div>`
        : nothing}
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ecdis-target-panel': EcdisTargetPanel;
  }
}
