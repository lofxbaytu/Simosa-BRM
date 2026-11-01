// 情境編輯器的「海圖點選編輯」面板(第二十一輪;R4「海圖點選編輯」;docs/design/instructor-plot.md 第 4 節):內嵌一張 <brm-track-plot editable>,
// 底圖為草稿的航線/區域/泊位疊圖、流場網格與 data/areas 的示意幾何(無 ENC),把手為航點/編輯中區域的頂點/自船/目標船;
// 圖上點擊新增、拖曳移動、右鍵刪除、方向鍵微調,寫回草稿後表格同步更新(同一份草稿)。對象由航線/區域/船與初始/目標船各區段的
// 「在圖上編輯」鈕或此面板的選單切換;「流場網格預覽」只看不編。純函式渲染,透過 EditorHost 讀寫草稿與對象/選取狀態。

import { html, nothing, type TemplateResult } from 'lit';
import { getShipConfig } from '../../lib/ship-config.js';
import { TrackHistory } from '../../lib/track-history.js';
import { currentFieldIssues, currentField, positionMode } from '../../lib/scenario-editor.js';
import {
  addPlotPoint,
  editorPlotOverlay,
  editorPlotState,
  movePlotHandle,
  plotHandles,
  plotOrigin,
  plotTargetFromValue,
  plotTargetHint,
  plotTargetLabel,
  plotTargetValue,
  removePlotHandle,
  type PlotEditTarget,
} from '../../lib/scenario-editor-plot.js';
import type { PlotClickDetail, PlotHandleKeyDetail, PlotHandleMoveDetail, TrackPlot } from '../track-plot.js';
import type { EditorHost } from './scenario-editor-sections.js';
import { textOf } from './scenario-editor-sections.js';
import '../track-plot.js';

/** 內嵌圖不畫航跡:共用一個空的航跡紀錄。 */
const EMPTY_HISTORY = new TrackHistory(1, 1);

/** 把手命中後插入航段的容差(px;換成公尺時除以比例)。 */
const INSERT_TOLERANCE_PX = 12;

function str(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' ? String(v) : '';
}

/** 對象選單的全部選項(航線、各區域、自船、各目標船、流場網格預覽)。 */
export function plotTargetOptions(h: EditorHost): Array<{ value: string; label: string }> {
  const d = h.draft;
  const out: Array<{ value: string; label: string }> = [{ value: 'route', label: plotTargetLabel(d, { kind: 'route' }) }];
  d.zones.forEach((_, i) => out.push({ value: `zone:${i}`, label: plotTargetLabel(d, { kind: 'zone', index: i }) }));
  out.push({ value: 'own', label: plotTargetLabel(d, { kind: 'own' }) });
  d.targets.forEach((_, i) => out.push({ value: `target:${i}`, label: plotTargetLabel(d, { kind: 'target', index: i }) }));
  out.push({ value: 'field', label: plotTargetLabel(d, { kind: 'field' }) });
  return out;
}

/** 海圖點選編輯面板;h.plotTarget 為 null 時不渲染。 */
export function renderPlotEditor(h: EditorHost): TemplateResult | typeof nothing {
  const target = h.plotTarget;
  if (!target) return nothing;
  const d = h.draft;
  const origin = plotOrigin(d);
  const handles = plotHandles(d, target, origin);
  const overlay = editorPlotOverlay(d, origin);
  const state = editorPlotState(d, origin);
  const cfg = getShipConfig(d.ship.id);
  const field = currentField(d);
  const fieldIssues = field ? currentFieldIssues(field, d.currentModel) : [];
  const mode = positionMode(d.initial.position);
  const onClick = (ev: CustomEvent<PlotClickDetail>) => {
    const plot = ev.currentTarget as TrackPlot;
    const r = addPlotPoint(h.draft, target, { x: ev.detail.x, y: ev.detail.y }, origin, INSERT_TOLERANCE_PX / plot.view().z);
    if (r.draft !== h.draft) {
      h.setDraft(r.draft);
      h.setPlotSelected(r.key);
    }
  };
  const onMove = (ev: CustomEvent<PlotHandleMoveDetail>) => h.setDraft(movePlotHandle(h.draft, ev.detail.key, { x: ev.detail.x, y: ev.detail.y }, origin));
  const onRemove = (ev: CustomEvent<PlotHandleKeyDetail>) => {
    if (!ev.detail.key) return;
    const next = removePlotHandle(h.draft, ev.detail.key);
    if (next !== h.draft) {
      h.setDraft(next);
      h.setPlotSelected(null);
    }
  };
  const onSelect = (ev: CustomEvent<PlotHandleKeyDetail>) => h.setPlotSelected(ev.detail.key);
  return html`<div class="panel plot-editor">
    <div class="panel-title">
      <span>海圖點選編輯 PLOT</span>
      <span class="pair">
        <select class="plot-target" data-value=${plotTargetValue(target)} @change=${(e: Event) => h.setPlotTarget(plotTargetFromValue(textOf(e)))}>
          ${plotTargetOptions(h).map((o) => html`<option value=${o.value}>${o.label}</option>`)}
        </select>
        <button class="small plot-close" title="關閉圖上編輯" @click=${() => h.setPlotTarget(null)}>關閉</button>
      </span>
    </div>
    <brm-track-plot
      class="plot"
      editable
      field-preview
      .state=${state}
      .config=${cfg}
      .history=${EMPTY_HISTORY}
      .noData=${false}
      .overlay=${overlay}
      .handles=${handles}
      .selectedKey=${h.plotSelected}
      .origin=${origin}
      .editHint=${plotTargetHint(target)}
      @plot-click=${onClick}
      @plot-handle-move=${onMove}
      @plot-handle-remove=${onRemove}
      @plot-handle-select=${onSelect}
    ></brm-track-plot>
    <div class="hint plot-hint">
      ${origin
        ? `原點 ${origin.lat.toFixed(4)}°N ${origin.lon.toFixed(4)}°E(${(d.raw['origin'] as unknown) ? 'origin' : '經緯度初始位置'});新點以${mode === 'latlon' ? '經緯度' : 'ENU x/y'}寫入,既有點沿用原寫法`
        : '尚無原點:只能畫/編 ENU x/y 座標的點(經緯度點不顯示);請在範本 origin 或自船初始位置填經緯度'}
      ${overlay.sketch ? ` · 示意幾何來源:${overlay.sketch.files.join('、')}(手繪概略輪廓,非海圖;ENC 取得後替換)` : ' · 周圍無示意幾何(data/areas),只有格線'}
      ${target.kind === 'field' && field ? (fieldIssues.length > 0 ? ` · 網格有問題,未畫:${fieldIssues[0]}` : overlay.currentField ? ` · 網格 ${str(overlay.currentField.grid.nx)}×${str(overlay.currentField.grid.ny)} 已畫(左下角為環境面板的常數流)` : '') : ''}
      ${target.kind === 'field' && !field ? ' · 尚未建立 field(環境進階面板勾選)' : ''}
    </div>
  </div>`;
}

export type { PlotEditTarget };
