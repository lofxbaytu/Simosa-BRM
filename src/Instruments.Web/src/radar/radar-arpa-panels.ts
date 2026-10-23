// ARPA 面板:<radar-arpa-table>(CPA/TCPA 目標表、排序、擷取/取消/警示確認)、<radar-target-data>(點選目標資料)、
// <radar-trial-panel>(試操船、危險目標限值、保護區)、<radar-event-log>(雷達事件)。

import { html, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { StoreElement, inputNumber } from './store-element.js';
import { panelStyles } from './radar-styles.js';
import { SORT_LABEL, formatTcpa, sortTracks, targetSource, trackStateLabel, type ArpaSortKey } from './arpa.js';
import { formatBearing, formatRangeNm } from './geometry.js';
import type { RadarTrack } from './protocol.js';
import { normalizeHeading } from '../lib/angles.js';

const fmt = (v: number | undefined, d = 1) => (v === undefined || !Number.isFinite(v) ? '--' : v.toFixed(d));
const brg = (v: number | undefined) => (v === undefined ? '---' : formatBearing(v).replace('°', ''));

@customElement('radar-arpa-table')
export class RadarArpaTable extends StoreElement {
  static override styles = panelStyles;
  @state() sortKey: ArpaSortKey = 'cpa';

  override render() {
    const s = this.store;
    const tr = s.tracks;
    const list = sortTracks(tr?.targets ?? [], this.sortKey);
    const trial = Boolean(tr?.trial);
    const pending = s.pendingAlarms().length;
    const th = (k: ArpaSortKey | null, label: string) =>
      k
        ? html`<th class=${this.sortKey === k ? 'sorted' : ''} @click=${() => (this.sortKey = k)} title="依 ${SORT_LABEL[k]} 排序">${label}</th>`
        : html`<th>${label}</th>`;
    return html`<div class="panel">
      <h3>
        <span>ARPA <span class="zh">目標 ${list.length}${s.status ? ` / ${s.status.maxTracks}` : ''}</span></span>
        <span class="zh">CPA 限 ${fmt(tr?.cpaLimit_nm ?? s.status?.cpaLimit_nm, 1)} nm · TCPA 限 ${fmt(tr?.tcpaLimit_min ?? s.status?.tcpaLimit_min, 0)} min</span>
      </h3>
      <div class="row">
        <button
          class=${s.settings.tool === 'acquire' ? 'active' : ''}
          ?disabled=${!s.transmitting}
          title="開啟後在 PPI 上點選回波以手動擷取"
          @click=${() => s.setSettings({ tool: s.settings.tool === 'acquire' ? 'select' : 'acquire' })}
        >
          ACQ 擷取
        </button>
        <button ?disabled=${s.selectedId === null} @click=${() => s.selectedId !== null && s.send({ cmd: 'cancel', id: s.selectedId })}>
          取消選取
        </button>
        <button ?disabled=${list.length === 0} @click=${() => s.send({ cmd: 'cancel', id: 'all' })}>全部取消</button>
        <button class=${pending > 0 ? 'alarm' : ''} ?disabled=${pending === 0} @click=${() => s.acknowledgeAlarms()}>
          確認警示${pending > 0 ? ` (${pending})` : ''}
        </button>
      </div>
      ${trial ? html`<div class="muted">試操船中:CRS/SPD 欄改顯示試操船 CPA/TCPA(T-)</div>` : nothing}
      <div style="max-height:190px;overflow:auto">
        <table aria-label="ARPA 目標表">
          <thead>
            <tr>
              ${th('id', 'No')}<th title="來源:○ 雷達、◬ 融合(AIS 關聯)、○△ AIS 未關聯">SRC</th><th>狀態</th>${th('range', 'RNG')}<th>BRG</th>${trial ? nothing : html`<th>CRS</th><th>SPD</th>`}${th('cpa', 'CPA')}${th('tcpa', 'TCPA')}
              ${trial ? html`<th title="試操船 CPA / TCPA">T-CPA</th><th>T-TCPA</th>` : nothing}
            </tr>
          </thead>
          <tbody>
            ${list.map((t) => this.row(t, trial))}
          </tbody>
        </table>
        ${list.length === 0 ? html`<div class="muted" style="padding:4px">無追蹤目標(按 ACQ 後點選回波,或啟用保護區)</div>` : nothing}
      </div>
    </div>`;
  }

  private row(t: RadarTrack, trial: boolean) {
    const s = this.store;
    const cls = [
      t.id === s.selectedId ? 'sel' : '',
      t.state === 'tracking' && t.dangerous ? 'danger' : '',
      t.state === 'lost' ? 'lost' : '',
    ].join(' ');
    const src = targetSource(t);
    return html`<tr class=${cls} @click=${() => s.select(t.id)}>
      <td>${t.label}${t.acquisition === 'auto' ? html`<span class="muted"> A</span>` : nothing}</td>
      <td class="src ${src.source}" title=${src.label}>${src.symbol}${t.ais?.associated ? html` <span class="muted">${t.ais.name}</span>` : nothing}</td>
      <td>${trackStateLabel(t)}${t.manoeuvring ? html`<span class="warning" title="目標操船中"> M</span>` : nothing}</td>
      <td>${formatRangeNm(t.range_nm)}</td>
      <td>${brg(t.bearing_deg)}</td>
      ${trial ? nothing : html`<td>${brg(t.course_deg)}</td><td>${fmt(t.speed_kn)}</td>`}
      <td>${fmt(t.cpa_nm, 2)}</td>
      <td>${formatTcpa(t.tcpa_min)}</td>
      ${trial ? html`<td>${fmt(t.trialCpa_nm, 2)}</td><td>${formatTcpa(t.trialTcpa_min)}</td>` : nothing}
    </tr>`;
  }
}

@customElement('radar-target-data')
export class RadarTargetData extends StoreElement {
  static override styles = panelStyles;

  override render() {
    const s = this.store;
    const t = s.tracks?.targets.find((x) => x.id === s.selectedId);
    const item = (label: string, value: string, cls = '') =>
      html`<span class="label">${label}</span><span class="value ${cls}">${value}</span><span></span>`;
    if (!t) {
      const a = s.selectedAisMmsi !== null ? s.aisTargets.find((x) => x.mmsi === s.selectedAisMmsi) : undefined;
      if (a) {
        // 純 AIS 目標(未被雷達追蹤):AIS 報告值與 CPA/TCPA;「擷取」以其方位/距離送 acquire,之後由 ARPA 以回波追蹤並經 AIS 融合關聯
        const danger = a.dangerous;
        return html`<div class="panel ais-target">
          <h3>
            <span>AIS ${a.name || a.mmsi} <span class="zh">純 AIS 目標(未追蹤)</span></span>
            <span class=${danger ? 'danger' : 'zh'}>${danger ? '危險' : a.candidateTrackId !== undefined ? `候選 ${String(a.candidateTrackId).padStart(2, '0')}` : '△'}</span>
          </h3>
          <div class="grid2" style="grid-template-columns:auto 1fr auto auto 1fr auto">
            ${item('MMSI', String(a.mmsi))} ${item('船名', a.name || '--')}
            ${item('BRG 方位', `${formatBearing(a.bearing_deg)} T`)} ${item('RNG 距離', `${formatRangeNm(a.range_nm)} NM`)}
            ${item('COG', `${formatBearing(a.cog_deg)} T`)} ${item('SOG', `${fmt(a.sog_kn)} kn`)}
            ${item('HDG', `${formatBearing(a.heading_deg)} T`)} ${item('相對方位', formatBearing(a.relBearing_deg))}
            ${item('CPA', `${fmt(a.cpa_nm, 2)} NM`, danger ? 'danger' : '')} ${item('TCPA', `${formatTcpa(a.tcpa_min)} min`, danger ? 'danger' : '')}
          </div>
          <div class="row">
            <button class="acquire-ais" @click=${() => s.acquireAis(a.mmsi)}>擷取此目標(ARPA)</button>
            <button @click=${() => s.selectAis(null)}>取消選取</button>
            <span class="muted">${a.lat.toFixed(5)}, ${a.lon.toFixed(5)}</span>
          </div>
        </div>`;
      }
      return html`<div class="panel">
        <h3>TARGET <span class="zh">目標資料</span></h3>
        <div class="muted">點選 PPI 或目標表上的目標以顯示資料${s.aisTargets.length > 0 ? `;純 AIS 目標 ${s.aisTargets.length}(△ 符號,點選後可擷取)` : ''}</div>
      </div>`;
    }
    const danger = t.state === 'tracking' && t.dangerous;
    return html`<div class="panel">
      <h3>
        <span>TARGET ${t.label} <span class="zh">${t.acquisition === 'auto' ? '自動擷取' : '手動擷取'}</span></span>
        <span class=${danger ? 'danger' : 'zh'}>${trackStateLabel(t)}${t.stable ? ' · 穩定' : ''}${t.swapRisk ? ' · 交換風險' : ''}</span>
      </h3>
      <div class="grid2" style="grid-template-columns:auto 1fr auto auto 1fr auto">
        ${item('BRG 方位', `${formatBearing(t.bearing_deg)} T`)} ${item('RNG 距離', `${formatRangeNm(t.range_nm)} NM`)}
        ${item('CRS 航向', t.course_deg === undefined ? '---' : `${formatBearing(t.course_deg)} T`)}
        ${item('SPD 速度', `${fmt(t.speed_kn)} kn`)}
        ${item('CPA', `${fmt(t.cpa_nm, 2)} NM`, danger ? 'danger' : '')} ${item('TCPA', `${formatTcpa(t.tcpa_min)} min`, danger ? 'danger' : '')}
        ${item('BCR 艏越', t.bcr_nm === undefined ? '--' : `${t.bcr_nm.toFixed(2)} NM ${t.bcr_nm >= 0 ? '艏前' : '艉後'}`)}
        ${item('BCT', `${formatTcpa(t.bct_min)} min`)}
        ${item('相對航向', t.relCourse_deg === undefined ? '---' : formatBearing(t.relCourse_deg))}
        ${item('相對速度', `${fmt(t.relSpeed_kn)} kn`)}
        ${t.trialCpa_nm !== undefined
          ? html`${item('試操船 CPA', `${fmt(t.trialCpa_nm, 2)} NM`)} ${item('試操船 TCPA', `${formatTcpa(t.trialTcpa_min)} min`)}`
          : nothing}
        ${item('追蹤時間', `${Math.floor(t.age_s / 60)}:${String(Math.floor(t.age_s % 60)).padStart(2, '0')}`)}
        ${item('漏掃描', String(t.misses))}
        ${item('來源', `${targetSource(t).symbol} ${t.ais ? (t.ais.associated ? '融合' : 'AIS 未關聯') : '雷達'}`)}
        ${t.ais
          ? html`${item('AIS', `${t.ais.name} (${t.ais.mmsi})`)} ${item('AIS 位置差', `${t.ais.positionOffset_m.toFixed(0)} m`, t.ais.associated ? '' : 'warning')}`
          : nothing}
      </div>
      <div class="row">
        <button @click=${() => s.send({ cmd: 'cancel', id: t.id })}>取消此目標</button>
        <button @click=${() => s.select(null)}>取消選取</button>
        <span class="muted">${t.lat.toFixed(5)}, ${t.lon.toFixed(5)}</span>
      </div>
    </div>`;
  }
}

@customElement('radar-trial-panel')
export class RadarTrialPanel extends StoreElement {
  static override styles = panelStyles;
  @state() course = 0;
  @state() speed = 10;
  @state() delay = 1;
  @state() private initialised = false;
  @state() gz = { enabled: false, inner_nm: 0.5, outer_nm: 3, from: 0, to: 360, relative: true };
  @state() private gzDirty = false;
  @state() cpa = 1;
  @state() tcpa = 12;
  @state() private limDirty = false;

  override willUpdate(changed: Map<string, unknown>): void {
    super.willUpdate(changed);
    const s = this.store;
    if (!s) return;
    if (!this.initialised && s.tracks) {
      const o = s.tracks.ownShip;
      this.course = Math.round(normalizeHeading(o.cog));
      this.speed = Math.round(o.sog * 10) / 10;
      this.initialised = true;
    }
    if (s.status && !this.gzDirty) this.gz = { ...s.status.guardZone };
    if (s.status && !this.limDirty) {
      this.cpa = s.status.cpaLimit_nm;
      this.tcpa = s.status.tcpaLimit_min;
    }
  }

  override render() {
    const s = this.store;
    const trial = s.trial;
    const sel = s.tracks?.targets.find((t) => t.id === s.selectedId);
    const minTrial = (s.tracks?.targets ?? []).reduce<RadarTrack | null>(
      (m, t) => (t.trialCpa_nm !== undefined && (m === null || t.trialCpa_nm < (m.trialCpa_nm ?? Infinity)) ? t : m),
      null,
    );
    const shown = sel?.trialCpa_nm !== undefined ? sel : minTrial;
    const gz = this.gz;
    return html`<div class="panel">
        <h3>TRIAL <span class="zh">試操船</span></h3>
        <div class="row">
          <label>航向</label>
          <input type="number" min="0" max="359.9" step="1" aria-label="試操船航向" .value=${String(this.course)}
            @change=${(e: Event) => (this.course = normalizeHeading(inputNumber(e, this.course)))} />
          <label>速度</label>
          <input type="number" min="0" max="30" step="0.5" aria-label="試操船速度" .value=${String(this.speed)}
            @change=${(e: Event) => (this.speed = Math.max(0, inputNumber(e, this.speed)))} />
          <label>延遲</label>
          <input type="number" min="0" max="30" step="0.5" aria-label="試操船延遲" style="width:52px" .value=${String(this.delay)}
            @change=${(e: Event) => (this.delay = Math.max(0, inputNumber(e, this.delay)))} />
          <span class="muted">min</span>
        </div>
        <div class="row">
          <button class=${trial ? 'active' : ''} @click=${() => s.send({ cmd: 'trialManoeuvre', course: this.course, speed: this.speed, delay: this.delay })}>
            ${trial ? '更新試操船' : '啟用試操船'}
          </button>
          <button ?disabled=${!trial} @click=${() => s.send({ cmd: 'trialManoeuvre', active: false })}>關閉</button>
          ${trial ? html`<span class="warning">TRIAL ${formatBearing(trial.course)} ${trial.speed} kn / ${trial.delay_min} min</span>` : nothing}
        </div>
        ${trial && shown
          ? html`<div class="row">
              <span class="label">預測 CPA(${shown.label}${sel ? '' : ' 最近'})</span>
              <span class="value ${(shown.trialCpa_nm ?? 9) < (s.tracks?.cpaLimit_nm ?? 1) ? 'danger' : ''}">${fmt(shown.trialCpa_nm, 2)} NM</span>
              <span class="label">TCPA</span><span class="value">${formatTcpa(shown.trialTcpa_min)} min</span>
            </div>`
          : nothing}
      </div>
      <div class="panel">
        <h3>LIMITS / GUARD <span class="zh">危險限值、保護區</span></h3>
        <div class="row">
          <label>CPA</label>
          <input type="number" min="0" step="0.1" aria-label="CPA 限值" style="width:56px" .value=${String(this.cpa)}
            @change=${(e: Event) => { this.cpa = Math.max(0, inputNumber(e, this.cpa)); this.limDirty = true; }} />
          <label>TCPA</label>
          <input type="number" min="0" step="1" aria-label="TCPA 限值" style="width:56px" .value=${String(this.tcpa)}
            @change=${(e: Event) => { this.tcpa = Math.max(0, inputNumber(e, this.tcpa)); this.limDirty = true; }} />
          <button ?disabled=${!this.limDirty} @click=${() => { s.send({ cmd: 'cpaLimits', cpa_nm: this.cpa, tcpa_min: this.tcpa }); this.limDirty = false; }}>
            套用
          </button>
        </div>
        <div class="row">
          <button class=${gz.enabled ? 'active' : ''} @click=${() => { this.gz = { ...gz, enabled: !gz.enabled }; this.gzDirty = true; }}>保護區</button>
          <input type="number" step="0.1" min="0" aria-label="保護區內緣" style="width:52px" .value=${String(gz.inner_nm)}
            @change=${(e: Event) => { this.gz = { ...gz, inner_nm: inputNumber(e, gz.inner_nm) }; this.gzDirty = true; }} />
          <span class="muted">–</span>
          <input type="number" step="0.1" min="0" aria-label="保護區外緣" style="width:52px" .value=${String(gz.outer_nm)}
            @change=${(e: Event) => { this.gz = { ...gz, outer_nm: inputNumber(e, gz.outer_nm) }; this.gzDirty = true; }} />
          <span class="muted">NM</span>
          <input type="number" step="1" aria-label="保護區起方位" style="width:52px" .value=${String(gz.from)}
            @change=${(e: Event) => { this.gz = { ...gz, from: inputNumber(e, gz.from) }; this.gzDirty = true; }} />
          <span class="muted">→</span>
          <input type="number" step="1" aria-label="保護區迄方位" style="width:52px" .value=${String(gz.to)}
            @change=${(e: Event) => { this.gz = { ...gz, to: inputNumber(e, gz.to) }; this.gzDirty = true; }} />
          <button title="相對艏向 R / 真方位 T" @click=${() => { this.gz = { ...gz, relative: !gz.relative }; this.gzDirty = true; }}>${gz.relative ? 'R' : 'T'}</button>
          <button ?disabled=${!this.gzDirty} @click=${() => { s.send({ cmd: 'guardZone', ...gz }); this.gzDirty = false; }}>套用</button>
          <button class=${s.settings.showGuardZone ? 'active' : ''} title="在 PPI 上顯示保護區" @click=${() => s.setSettings({ showGuardZone: !s.settings.showGuardZone })}>
            顯示
          </button>
        </div>
      </div>`;
  }
}

@customElement('radar-event-log')
export class RadarEventLog extends StoreElement {
  static override styles = panelStyles;

  override render() {
    const s = this.store;
    return html`<div class="panel">
      <h3>EVENTS <span class="zh">雷達事件</span></h3>
      <div style="max-height:110px;overflow:auto">
        ${s.log.length === 0 ? html`<div class="muted">尚無事件</div>` : nothing}
        ${s.log.map(
          (e) => html`<div class=${e.error || e.kind === 'dangerousTarget' || e.kind === 'radarFailure' ? 'danger' : e.kind === 'targetLost' || e.kind === 'acquireFailed' ? 'warning' : ''}>
            <span class="muted">${formatSimTime(e.t)}</span> ${e.message}
          </div>`,
        )}
      </div>
    </div>`;
  }
}

/** 模擬時間 mm:ss */
export function formatSimTime(t: number): string {
  if (!Number.isFinite(t)) return '--:--';
  const s = Math.max(0, Math.floor(t));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${String(m).padStart(2, '0')}:${ss}`;
}

declare global {
  interface HTMLElementTagNameMap {
    'radar-arpa-table': RadarArpaTable;
    'radar-target-data': RadarTargetData;
    'radar-trial-panel': RadarTrialPanel;
    'radar-event-log': RadarEventLog;
  }
}
