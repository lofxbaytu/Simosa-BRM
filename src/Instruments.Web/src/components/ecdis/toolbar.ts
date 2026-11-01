// <ecdis-toolbar>:簡化 ECDIS 的顯示控制(定向、量程、距離圈、置中、航跡間隔、向量長度、圖層、工具與 EBL/VRM ×2、聲響)
// 與安全等深線資訊(吃水、UKC 下限來源、安全/深水等深線、潮高、自船位置有效水深)。

import { html, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { EcdisStoreElement, panelStyles } from './ecdis-shared.js';
import { CHART_RANGES_NM, type ChartOrientation } from '../../lib/ecdis/projection.js';
import { TRAIL_INTERVAL_CHOICES, type ChartTool, type EcdisSettings } from '../../lib/ecdis/ecdis-store.js';
import { formatBearing, formatRangeNm } from '../../radar/geometry.js';
import { normalizeHeading } from '../../lib/angles.js';

const LAYER_LABEL: Readonly<Record<keyof EcdisSettings['layers'], string>> = {
  depth: '水深著色',
  sketch: '示意幾何',
  zones: '區域',
  route: '航線',
  targets: 'AIS 目標',
  labels: '標籤',
};

function inputNum(e: Event, fallback: number): number {
  const v = Number((e.target as HTMLInputElement).value);
  return Number.isFinite(v) ? v : fallback;
}

@customElement('ecdis-toolbar')
export class EcdisToolbar extends EcdisStoreElement {
  static override styles = panelStyles;

  private setOrientation(o: ChartOrientation): void {
    this.store.setSettings({ orientation: o });
  }

  private setTool(t: ChartTool): void {
    this.store.setSettings({ tool: this.store.settings.tool === t ? 'pan' : t });
  }

  override render() {
    const s = this.store;
    const st = s.settings;
    const c = s.chart;
    const depth = s.ownDepth;
    const tide = s.state?.tideHeight_m;
    return html`
      <h3>顯示</h3>
      <div class="row">
        <span class="label">定向</span>
        <button class=${st.orientation === 'north-up' ? 'active' : ''} @click=${() => this.setOrientation('north-up')}>N UP</button>
        <button class=${st.orientation === 'course-up' ? 'active' : ''} ?disabled=${!s.headingValid} title=${s.headingValid ? '以目前航向為上' : '電羅經無效:不可用'} @click=${() => this.setOrientation('course-up')}>C UP</button>
        ${st.orientation === 'course-up' ? html`<button title="以目前航向重設參考" @click=${() => s.resetCourseUp()}>重設 ${formatBearing(s.courseRef_deg)}</button>` : nothing}
      </div>
      <div class="row">
        <span class="label">量程</span>
        <button @click=${() => s.stepRange(-1)} title="量程 −">−</button>
        <select .value=${String(st.rangeNm)} @change=${(e: Event) => s.setSettings({ rangeNm: Number((e.target as HTMLSelectElement).value) })}>
          ${CHART_RANGES_NM.map((r) => html`<option value=${String(r)} ?selected=${r === st.rangeNm}>${formatRangeNm(r)} NM</option>`)}
        </select>
        <button @click=${() => s.stepRange(1)} title="量程 +">+</button>
        <label class="chk"><input type="checkbox" .checked=${st.rangeRings} @change=${(e: Event) => s.setSettings({ rangeRings: (e.target as HTMLInputElement).checked })} /> 距離圈</label>
        <button class=${st.follow ? 'active' : ''} @click=${() => s.recentre()} title="畫面跟隨自船(拖曳後關閉)">置中</button>
      </div>
      <div class="row">
        <span class="label">航跡</span>
        <select .value=${String(st.trailInterval_s)} @change=${(e: Event) => s.setSettings({ trailInterval_s: Number((e.target as HTMLSelectElement).value) })}>
          ${TRAIL_INTERVAL_CHOICES.map((v) => html`<option value=${String(v)} ?selected=${v === st.trailInterval_s}>${v === 0 ? '關' : `${v} s`}</option>`)}
        </select>
        <span class="label">向量</span>
        <select .value=${String(st.vectorMinutes)} @change=${(e: Event) => s.setSettings({ vectorMinutes: Number((e.target as HTMLSelectElement).value) })}>
          ${[3, 6, 12].map((v) => html`<option value=${String(v)} ?selected=${v === st.vectorMinutes}>${v} 分</option>`)}
        </select>
      </div>
      <div class="row">
        <span class="label">圖層</span>
        ${(Object.keys(LAYER_LABEL) as Array<keyof EcdisSettings['layers']>).map(
          (k) => html`<label class="chk"><input type="checkbox" .checked=${st.layers[k]} @change=${(e: Event) => s.setLayer(k, (e.target as HTMLInputElement).checked)} /> ${LAYER_LABEL[k]}</label>`,
        )}
      </div>
      <h3>EBL / VRM <span class="spacer"></span><span class="note">工具:點海圖設定該組</span></h3>
      <div class="row">
        <button class=${st.tool === 'pan' ? 'active' : ''} @click=${() => s.setSettings({ tool: 'pan' })}>平移/選取</button>
        <button class=${st.tool === 'ebl1' ? 'active' : ''} @click=${() => this.setTool('ebl1')}>EBL1</button>
        <button class=${st.tool === 'ebl2' ? 'active' : ''} @click=${() => this.setTool('ebl2')}>EBL2</button>
      </div>
      ${([0, 1] as const).map(
        (i) => html`<div class="row">
          <label class="chk"><input type="checkbox" .checked=${s.ebl[i].on} @change=${(e: Event) => s.setEbl(i, { on: (e.target as HTMLInputElement).checked })} /> EBL${i + 1}</label>
          <input type="number" min="0" max="359.9" step="0.1" .value=${s.ebl[i].bearing_deg.toFixed(1)} @change=${(e: Event) => s.setEbl(i, { bearing_deg: normalizeHeading(inputNum(e, s.ebl[i].bearing_deg)) })} />°T
          <label class="chk"><input type="checkbox" .checked=${s.vrm[i].on} @change=${(e: Event) => s.setVrm(i, { on: (e.target as HTMLInputElement).checked })} /> VRM${i + 1}</label>
          <input type="number" min="0.01" max="48" step="0.05" .value=${s.vrm[i].range_nm.toFixed(2)} @change=${(e: Event) => s.setVrm(i, { range_nm: inputNum(e, s.vrm[i].range_nm) })} /> NM
        </div>`,
      )}
      <h3>安全等深線</h3>
      <div class="kv">
        <span class="k">最大吃水</span><span class="v">${c.maxDraft_m.toFixed(2)} m</span>
        <span class="k">UKC 下限</span><span class="v" title=${c.ukc.note}>${c.ukc.ukc_m.toFixed(2)} m <span class="muted">(${c.ukc.source === 'scenario' ? '情境' : '公司'})</span></span>
        <span class="k">安全等深線</span><span class="v">${c.contours.safety_m.toFixed(2)} m</span>
        <span class="k">深水等深線</span><span class="v muted">${c.contours.deep_m.toFixed(1)} m</span>
        <span class="k">潮高</span><span class="v ${tide === undefined ? 'muted' : ''}">${tide === undefined ? '—' : `${tide >= 0 ? '+' : ''}${tide.toFixed(2)} m`}</span>
        <span class="k">自船有效水深</span><span class="v ${depth.effective_m < c.contours.safety_m ? 'alarm' : ''}">${depth.effective_m.toFixed(1)} m${c.baseDepthKnown ? '' : ' (基底未知)'}</span>
        <span class="k">龍骨下(測深)</span><span class="v ${s.state?.sensors && !s.state.sensors.depthValid ? 'warn' : ''}">${s.state ? `${s.state.depthBelowKeel.toFixed(1)} m` : '—'}</span>
      </div>
      <div class="row" style="margin-top:6px">
        <label class="chk"><input type="checkbox" .checked=${!s.sounder.muted} @change=${(e: Event) => s.setSound((e.target as HTMLInputElement).checked)} /> 警報聲響</label>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ecdis-toolbar': EcdisToolbar;
  }
}
