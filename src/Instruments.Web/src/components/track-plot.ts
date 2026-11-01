// 鳥瞰航跡圖(第一版沒有 3D 視景時的視覺參考):本地 ENU、北上,自船外形依 LPP/B 比例,
// 航跡線、航向向量(6 分鐘)、COG/SOG 向量(虛線)、格線與比例尺;滾輪或按鈕縮放,拖曳平移。
// 疊圖(overlay,設計文件第 6 節 W8):情境航線(航點、走廊半寬)、區域多邊形(禁區/速限/UKC/報告)、事件標記。
// 目標船(state.targets[],規劃書第 5.2 節):三角形依航向、船名/ID 標籤、COG/SOG 短向量;AIS 關閉者空心虛線,
// 處於 cpaAlarm 的目標紅色(alarmTargets 由事件流推得);targetTable 開啟時左上角列 CPA/TCPA 小表(教官站完整模式)。
// 拖船/錨/泊位(docs/design/physics-tugs-anchors-mooring.md):碼頭線(硬接觸時紅)、碰墊(依壓縮比例著色:綠 < 60 % 額定、黃、紅 = 過載)、
// 繫船柱、纜繩線段(導纜孔 → 繫船柱,依張力/MBL 著色)、拖船示意位置與作用於連接點的力向量(長度 ∝ 出力)、錨位與錨鏈(錨鏈孔 → 錨)。
// 流場層(第二十一輪;docs/design/instructor-plot.md 第 3 節;工具列「流」可開關):overlay.currentField 的網格節點箭頭(可見範圍內取樣、最多 40 × 40,
// 長度/顏色依流速、依 state 推得的 timeScale 比例 k(t) 縮放,一次批次繪製)與網格外框;左下角單一箭頭 + 數值顯示 state.current(自船位置的流,
// 均勻流/時變流時即為唯一的流資訊)與來源。示意幾何層(overlay.sketch;data/areas 的海岸線/防波堤/淺灘/錨地/速限區/岸壁)畫在區域之下。
// 點選編輯模式(editable;同文件第 4 節):handles[] 為可拖曳把手(航點/頂點/自船/目標船),點擊空白處發 plot-click(ENU 座標)、拖曳把手後發
// plot-handle-move、右鍵把手發 plot-handle-remove、選取發 plot-handle-select;方向鍵微調選取的把手(Shift ×10)、Delete 刪除、Esc 取消選取;
// 游標座標(x/y 與經緯度)顯示於左上讀數列。畫布無尺寸(headless 測試)時以 640 × 480 作像素換算。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { ifDefined } from 'lit/directives/if-defined.js';
import { tileStyles } from './shared.js';
import { CURRENT_SOURCE_LABELS, type OwnShipState } from '../types/state.js';
import type { ShipDisplayConfig } from '../lib/ship-config.js';
import type { TrackHistory } from '../lib/track-history.js';
import type { OverlaySketchShape, PlotOverlay } from '../lib/plot-overlay.js';
import type { TargetState } from '../types/target.js';
import { targetTableRows } from '../lib/targets.js';
import { bodyToEnu, chooseGridSpacing, hullOutline, superstructureOutline } from '../lib/ship-geometry.js';
import { degToRad, knotsToMps } from '../lib/angles.js';
import { formatHeading, formatKnots } from '../lib/format.js';
import { ANCHOR_SIDE_LABEL, anchorLevel, fenderLoadLevel, hawsePoint, lineLoadLevel, resolveBodyPoint, tugName, type LoadLevel } from '../lib/external-forces.js';
import { arrowLengthPx, fieldBounds, fieldMaxSpeed, fieldScaleFromState, sampleFieldArrows, speedColor, type FieldScale } from '../lib/current-field-plot.js';
import { coordinateReadout, enuToPx as viewEnuToPx, nearestHandle, nudgeStep_m, pxToEnu as viewPxToEnu, type PlotHandle, type PlotView } from '../lib/plot-editing.js';

const VECTOR_MINUTES = 6;
const ZOOM_MIN = 0.02; // px/m(約 5 km 寬)
const ZOOM_MAX = 8;
/** 畫布尚無版面尺寸(headless 測試)時的像素換算尺寸 */
const FALLBACK_W = 640;
const FALLBACK_H = 480;
/** 點擊與拖曳的判別距離(px) */
const CLICK_SLOP_PX = 4;
/** 流場箭頭顏色分桶數(批次繪製) */
const ARROW_COLOR_BUCKETS = 8;

export interface PlotClickDetail {
  x: number;
  y: number;
  px: number;
  py: number;
}

export interface PlotHandleMoveDetail {
  key: string;
  x: number;
  y: number;
}

export interface PlotHandleKeyDetail {
  key: string | null;
}

interface HandleDrag {
  key: string;
  startPx: number;
  startPy: number;
  x: number;
  y: number;
  moved: boolean;
}

@customElement('brm-track-plot')
export class TrackPlot extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        position: relative;
        height: 100%;
      }
      .frame {
        position: relative;
        height: 100%;
        background: var(--ob-plot-bg);
        border: 1px solid var(--ob-border);
        border-radius: 6px;
        overflow: hidden;
      }
      canvas {
        display: block;
        width: 100%;
        height: 100%;
        touch-action: none;
        cursor: grab;
      }
      canvas.dragging {
        cursor: grabbing;
      }
      .toolbar {
        position: absolute;
        top: 6px;
        right: 6px;
        display: flex;
        gap: 4px;
      }
      .toolbar button {
        min-width: 30px;
      }
      .legend {
        position: absolute;
        left: 8px;
        top: 6px;
        font-size: 11px;
        color: var(--ob-text-secondary);
        pointer-events: none;
      }
      .notice {
        position: absolute;
        left: 50%;
        top: 50%;
        transform: translate(-50%, -50%);
        font-size: 24px;
        font-weight: 700;
        letter-spacing: 0.2em;
        padding: 10px 24px;
        border-radius: 8px;
        pointer-events: none;
      }
      .notice.frozen {
        background: var(--ob-caution);
        color: #1a1600;
      }
      .notice.alarm {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      .targets {
        position: absolute;
        left: 8px;
        top: 24px;
        font-size: 11px;
        font-variant-numeric: tabular-nums;
        color: var(--ob-text-secondary);
        background: color-mix(in srgb, var(--ob-bg-surface) 78%, transparent);
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 2px 6px;
        pointer-events: none;
        border-collapse: collapse;
      }
      .targets th,
      .targets td {
        padding: 0 4px;
        text-align: right;
        font-weight: 400;
        white-space: nowrap;
      }
      .targets th {
        color: var(--ob-text-muted);
      }
      .targets td:first-child,
      .targets th:first-child {
        text-align: left;
      }
      .targets tr.alarm td {
        color: var(--ob-alarm);
        font-weight: 600;
      }
      .targets tr.aisoff td:first-child {
        font-style: italic;
      }
      .notice.nodata {
        background: var(--ob-bg-surface);
        color: var(--ob-text-secondary);
        border: 1px solid var(--ob-border);
        font-size: 16px;
        letter-spacing: 0.05em;
      }
      canvas.editable {
        cursor: crosshair;
      }
      canvas.editable.dragging {
        cursor: grabbing;
      }
      canvas:focus-visible {
        outline: 2px solid var(--ob-accent);
        outline-offset: -2px;
      }
      .readout {
        position: absolute;
        left: 8px;
        top: 24px;
        font-size: 11px;
        font-variant-numeric: tabular-nums;
        color: var(--ob-text);
        background: color-mix(in srgb, var(--ob-bg-surface) 78%, transparent);
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 2px 6px;
        pointer-events: none;
        white-space: nowrap;
      }
    `,
  ];

  @property({ attribute: false }) state!: OwnShipState;
  @property({ attribute: false }) config!: ShipDisplayConfig;
  @property({ attribute: false }) history!: TrackHistory;
  @property({ type: Boolean }) noData = false;
  /** 航線/區域/事件疊圖(無則不畫) */
  @property({ attribute: false }) overlay: PlotOverlay | null = null;
  /** 目標船(state.targets[];無則不畫) */
  @property({ attribute: false }) targets: readonly TargetState[] = [];
  /** 處於 CPA 警報中的目標 id(lib/targets.ts cpaAlarmTargets) */
  @property({ attribute: false }) alarmTargets: ReadonlySet<string> = new Set();
  /** 顯示 CPA/TCPA 小表(教官站完整模式) */
  @property({ type: Boolean, attribute: 'target-table' }) targetTable = false;
  /** 流場層開關(工具列「流」;網格箭頭與左下角 state.current 箭頭) */
  @property({ type: Boolean, attribute: 'show-current' }) showCurrent = true;
  /** 情境預覽(情境編輯器內嵌圖):把 overlay.currentField 視為作用中、k = 1,不依 state.currentSource 判斷 */
  @property({ type: Boolean, attribute: 'field-preview' }) fieldPreview = false;
  /** 點選編輯模式(情境編輯器內嵌圖) */
  @property({ type: Boolean }) editable = false;
  /** 可拖曳把手(editable 時畫出並可拖曳/右鍵刪除/鍵盤微調) */
  @property({ attribute: false }) handles: readonly PlotHandle[] = [];
  /** 選取中的把手鍵(方向鍵微調對象;宿主可設定) */
  @property({ attribute: false }) selectedKey: string | null = null;
  /** 經緯度讀數用的原點(無則只顯示 x/y) */
  @property({ attribute: false }) origin: { lat: number; lon: number } | null = null;
  /** 編輯模式的提示文字(游標離開畫布時顯示) */
  @property({ attribute: false }) editHint = '';

  /** 比例尺(px/m) */
  @state() private zoom = 0.6;
  @state() private follow = true;
  @state() private dragging = false;
  /** 游標座標讀數(editable 時更新) */
  @state() private cursorText = '';
  /** 平移偏移(公尺,ENU) */
  private panX = 0;
  private panY = 0;
  private dragStart: { px: number; py: number; panX: number; panY: number; moved: boolean } | null = null;
  private handleDrag: HandleDrag | null = null;
  /** 最近一次指標在畫布上的像素位置(選取變更時重算讀數) */
  private lastPointer: [number, number] | null = null;

  @query('canvas') private canvas!: HTMLCanvasElement;
  private resizeObserver: ResizeObserver | null = null;
  private raf = 0;

  override firstUpdated(): void {
    this.resizeObserver = new ResizeObserver(() => this.scheduleDraw());
    this.resizeObserver.observe(this.canvas);
    this.scheduleDraw();
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    if (this.raf) cancelAnimationFrame(this.raf);
  }

  override updated(): void {
    this.scheduleDraw();
  }

  private scheduleDraw(): void {
    if (this.raf) return;
    this.raf = requestAnimationFrame(() => {
      this.raf = 0;
      this.draw();
    });
  }

  override render() {
    const flags = this.state?.flags ?? {};
    const notice = this.noData
      ? html`<div class="notice nodata">等待模擬核心資料(未連線時可切換示範模式)</div>`
      : flags.collision
        ? html`<div class="notice alarm">碰撞</div>`
        : flags.aground
          ? html`<div class="notice alarm">擱淺</div>`
          : flags.frozen
            ? html`<div class="notice frozen">已凍結</div>`
            : null;
    const field = this.overlay?.currentField ?? null;
    return html`
      <div class="frame">
        <canvas
          class="${this.dragging ? 'dragging' : ''} ${this.editable ? 'editable' : ''}"
          tabindex=${ifDefined(this.editable ? '0' : undefined)}
          aria-label=${ifDefined(this.editable ? '海圖點選編輯:點擊新增、拖曳移動、右鍵刪除、方向鍵微調' : undefined)}
          @wheel=${this.onWheel}
          @pointerdown=${this.onPointerDown}
          @pointermove=${this.onPointerMove}
          @pointerup=${this.onPointerUp}
          @pointercancel=${this.onPointerUp}
          @pointerleave=${this.onPointerLeave}
          @contextmenu=${this.onContextMenu}
          @keydown=${this.onKeyDown}
        ></canvas>
        <div class="legend">
          鳥瞰航跡圖(北上,本地 ENU)· 實線 = 航向向量 ${VECTOR_MINUTES} min · 虛線 = COG/SOG${this.targets.length > 0 ? ' · 三角 = 目標船(虛線 = AIS 關)' : ''}${this.hasExternal() ? ' · ◆ = 拖船(箭頭 = 力) · ⚓ = 錨' : ''}${this.showCurrent && field ? ' · 箭頭 = 流場網格(長/色 = 流速)' : ''}${this.overlay?.sketch ? ' · 灰 = 示意幾何(非海圖)' : ''}
        </div>
        ${this.targetTable && this.targets.length > 0 ? this.renderTargetTable() : null}
        ${this.editable ? html`<div class="readout">${this.cursorText || this.editHint || '點擊新增 · 拖曳移動 · 右鍵刪除 · 方向鍵微調'}</div>` : nothing}
        <div class="toolbar">
          <button class="layer-current ${this.showCurrent ? 'active' : ''}" title="流場層:網格箭頭與自船位置的流" aria-pressed=${this.showCurrent} @click=${() => (this.showCurrent = !this.showCurrent)}>流</button>
          <button title="放大" @click=${() => this.setZoom(this.zoom * 1.5)}>+</button>
          <button title="縮小" @click=${() => this.setZoom(this.zoom / 1.5)}>−</button>
          <button class=${this.follow ? 'active' : ''} title="置中跟隨自船" @click=${this.recenter}>置中</button>
        </div>
        ${notice}
      </div>
    `;
  }

  /** 畫布像素尺寸(無版面時取 640 × 480,讓 headless 測試的像素換算確定)。 */
  private viewSize(): { w: number; h: number } {
    const w = this.canvas?.clientWidth ?? 0;
    const h = this.canvas?.clientHeight ?? 0;
    return w > 0 && h > 0 ? { w, h } : { w: FALLBACK_W, h: FALLBACK_H };
  }

  /** 目前視圖(中心 ENU、比例、像素尺寸)。 */
  view(): PlotView {
    const s = this.state;
    const { w, h } = this.viewSize();
    const ox = s?.pos.x ?? 0;
    const oy = s?.pos.y ?? 0;
    return { cx: this.follow ? ox : ox + this.panX, cy: this.follow ? oy : oy + this.panY, z: this.zoom, w, h };
  }

  /** 像素 → ENU(宿主/測試用)。 */
  pxToEnu(px: number, py: number): { x: number; y: number } {
    return viewPxToEnu(this.view(), px, py);
  }

  /** ENU → 像素(宿主/測試用)。 */
  enuToPx(x: number, y: number): [number, number] {
    return viewEnuToPx(this.view(), x, y);
  }

  private canvasPx(ev: PointerEvent | MouseEvent): [number, number] {
    const rect = this.canvas.getBoundingClientRect();
    return [ev.clientX - rect.left, ev.clientY - rect.top];
  }

  private emit<T>(name: string, detail: T): void {
    this.dispatchEvent(new CustomEvent<T>(name, { detail, bubbles: true, composed: true }));
  }

  private selectHandle(key: string | null): void {
    if (this.selectedKey === key) return;
    this.selectedKey = key;
    this.emit<PlotHandleKeyDetail>('plot-handle-select', { key });
    if (this.lastPointer) this.updateCursor(this.lastPointer[0], this.lastPointer[1]);
  }

  private updateCursor(px: number, py: number): void {
    if (!this.editable) return;
    this.lastPointer = [px, py];
    const p = this.pxToEnu(px, py);
    const sel = this.selectedKey ? this.handles.find((h) => h.key === this.selectedKey) : undefined;
    this.cursorText = `${coordinateReadout(p, this.origin)}${sel ? ` · 選取 ${sel.label}` : ''}`;
  }

  private renderTargetTable() {
    const rows = targetTableRows(this.targets, this.alarmTargets);
    return html`<table class="targets" aria-label="目標船 CPA/TCPA">
      <thead>
        <tr><th>目標</th><th>距離 nm</th><th>方位</th><th>CPA nm</th><th>TCPA min</th></tr>
      </thead>
      <tbody>
        ${rows.map(
          (r) => html`<tr class="${r.alarm ? 'alarm' : ''} ${r.aisOn ? '' : 'aisoff'}" data-target=${r.id} title=${r.aisOn ? r.id : `${r.id}(AIS 關)`}>
            <td>${r.label}</td>
            <td>${r.range_nm.toFixed(2)}</td>
            <td>${formatHeading(r.bearing_deg)}</td>
            <td>${r.cpa_nm.toFixed(2)}</td>
            <td>${r.tcpa_min.toFixed(1)}</td>
          </tr>`,
        )}
      </tbody>
    </table>`;
  }

  private setZoom(z: number): void {
    this.zoom = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, z));
  }

  private recenter = (): void => {
    this.follow = true;
    this.panX = 0;
    this.panY = 0;
    this.scheduleDraw();
  };

  private onWheel = (ev: WheelEvent): void => {
    ev.preventDefault();
    this.setZoom(this.zoom * (ev.deltaY < 0 ? 1.25 : 0.8));
  };

  private capture(ev: PointerEvent): void {
    try {
      this.canvas.setPointerCapture(ev.pointerId);
    } catch {
      /* 忽略(無指標擷取的環境) */
    }
  }

  private onPointerDown = (ev: PointerEvent): void => {
    if (this.editable) {
      this.canvas.focus?.();
      if (ev.button === 2) return; // 右鍵由 contextmenu 處理(刪除把手)
      const [px, py] = this.canvasPx(ev);
      this.updateCursor(px, py);
      const hit = nearestHandle(this.handles, px, py, this.view());
      if (hit) {
        this.capture(ev);
        this.handleDrag = { key: hit.key, startPx: px, startPy: py, x: hit.x, y: hit.y, moved: false };
        this.selectHandle(hit.key);
        this.dragging = true;
        return;
      }
    }
    this.capture(ev);
    this.dragStart = { px: ev.clientX, py: ev.clientY, panX: this.panX, panY: this.panY, moved: false };
    this.dragging = true;
  };

  private onPointerMove = (ev: PointerEvent): void => {
    if (this.editable) {
      const [px, py] = this.canvasPx(ev);
      this.updateCursor(px, py);
      if (this.handleDrag) {
        const p = this.pxToEnu(px, py);
        this.handleDrag.x = p.x;
        this.handleDrag.y = p.y;
        if (Math.hypot(px - this.handleDrag.startPx, py - this.handleDrag.startPy) > CLICK_SLOP_PX) this.handleDrag.moved = true;
        this.scheduleDraw();
        return;
      }
    }
    if (!this.dragStart) return;
    const dxPx = ev.clientX - this.dragStart.px;
    const dyPx = ev.clientY - this.dragStart.py;
    if (!this.dragStart.moved && Math.hypot(dxPx, dyPx) <= CLICK_SLOP_PX) return; // 尚未超過點擊容差:不平移
    this.dragStart.moved = true;
    this.panX = this.dragStart.panX - dxPx / this.zoom;
    this.panY = this.dragStart.panY + dyPx / this.zoom;
    this.follow = false;
    this.scheduleDraw();
  };

  private onPointerUp = (ev: PointerEvent): void => {
    if (this.dragStart || this.handleDrag) {
      try {
        this.canvas.releasePointerCapture(ev.pointerId);
      } catch {
        /* 忽略 */
      }
    }
    if (this.handleDrag) {
      const d = this.handleDrag;
      this.handleDrag = null;
      this.dragging = false;
      if (d.moved) this.emit<PlotHandleMoveDetail>('plot-handle-move', { key: d.key, x: d.x, y: d.y });
      this.scheduleDraw();
      return;
    }
    const start = this.dragStart;
    this.dragStart = null;
    this.dragging = false;
    if (start && !start.moved && this.editable && ev.button === 0 && ev.type === 'pointerup') {
      const [px, py] = this.canvasPx(ev);
      const p = this.pxToEnu(px, py);
      this.emit<PlotClickDetail>('plot-click', { x: p.x, y: p.y, px, py });
    }
  };

  private onPointerLeave = (): void => {
    if (this.editable && !this.handleDrag) {
      this.cursorText = '';
      this.lastPointer = null;
    }
  };

  private onContextMenu = (ev: MouseEvent): void => {
    if (!this.editable) return;
    ev.preventDefault();
    const [px, py] = this.canvasPx(ev);
    const hit = nearestHandle(this.handles, px, py, this.view());
    if (hit && hit.removable) this.emit<PlotHandleKeyDetail>('plot-handle-remove', { key: hit.key });
  };

  private onKeyDown = (ev: KeyboardEvent): void => {
    if (!this.editable) return;
    if (ev.key === 'Escape') {
      this.selectHandle(null);
      ev.preventDefault();
      return;
    }
    const sel = this.selectedKey ? this.handles.find((h) => h.key === this.selectedKey) : undefined;
    if (!sel) return;
    if (ev.key === 'Delete' || ev.key === 'Backspace') {
      if (sel.removable) this.emit<PlotHandleKeyDetail>('plot-handle-remove', { key: sel.key });
      ev.preventDefault();
      return;
    }
    const step = nudgeStep_m(this.zoom, ev.shiftKey);
    let dx = 0;
    let dy = 0;
    if (ev.key === 'ArrowLeft') dx = -step;
    else if (ev.key === 'ArrowRight') dx = step;
    else if (ev.key === 'ArrowUp') dy = step;
    else if (ev.key === 'ArrowDown') dy = -step;
    else return;
    ev.preventDefault();
    this.emit<PlotHandleMoveDetail>('plot-handle-move', { key: sel.key, x: sel.x + dx, y: sel.y + dy });
  };

  private cssVar(name: string): string {
    return getComputedStyle(this).getPropertyValue(name).trim() || '#888';
  }

  private draw(): void {
    const canvas = this.canvas;
    const s = this.state;
    if (!canvas || !s || !this.config) return;
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    if (w === 0 || h === 0) return;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    // 畫面中心對應的 ENU 座標
    const cx = this.follow ? s.pos.x : s.pos.x + this.panX;
    const cy = this.follow ? s.pos.y : s.pos.y + this.panY;
    const z = this.zoom;
    const toPx = (x: number, y: number): [number, number] => [w / 2 + (x - cx) * z, h / 2 - (y - cy) * z];

    // 格線
    const spacing = chooseGridSpacing(z);
    ctx.strokeStyle = this.cssVar('--ob-grid');
    ctx.lineWidth = 1;
    ctx.beginPath();
    const x0 = Math.floor((cx - w / 2 / z) / spacing) * spacing;
    const x1 = cx + w / 2 / z;
    for (let gx = x0; gx <= x1; gx += spacing) {
      const [px] = toPx(gx, 0);
      ctx.moveTo(Math.round(px) + 0.5, 0);
      ctx.lineTo(Math.round(px) + 0.5, h);
    }
    const y0 = Math.floor((cy - h / 2 / z) / spacing) * spacing;
    const y1 = cy + h / 2 / z;
    for (let gy = y0; gy <= y1; gy += spacing) {
      const [, py] = toPx(0, gy);
      ctx.moveTo(0, Math.round(py) + 0.5);
      ctx.lineTo(w, Math.round(py) + 0.5);
    }
    ctx.stroke();

    // 示意幾何(海岸線/防波堤/淺灘/錨地/速限區/岸壁;在區域之下)
    if (this.overlay?.sketch) this.drawSketch(ctx, toPx, z, this.overlay.sketch.shapes);
    // 疊圖:區域、航線走廊(在航跡之下)
    if (this.overlay) this.drawOverlay(ctx, toPx, z);
    // 泊位:碼頭線、碰墊、繫船柱(在航跡之下)
    if (this.overlay?.berth) this.drawBerth(ctx, toPx, z);

    // 航跡
    const pts = this.history?.all() ?? [];
    if (pts.length > 1) {
      ctx.strokeStyle = this.cssVar('--ob-track');
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      pts.forEach((p, i) => {
        const [px, py] = toPx(p.x, p.y);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      const [lx, ly] = toPx(s.pos.x, s.pos.y);
      ctx.lineTo(lx, ly);
      ctx.stroke();
    }

    // 向量
    const [sx, sy] = toPx(s.pos.x, s.pos.y);
    const hdgLen = knotsToMps(Math.abs(s.stw)) * VECTOR_MINUTES * 60 * z;
    const hdgRad = degToRad(s.heading);
    const dirSign = s.stw >= 0 ? 1 : -1;
    ctx.strokeStyle = this.cssVar('--ob-vector-hdg');
    ctx.lineWidth = 1.5;
    ctx.setLineDash([]);
    ctx.beginPath();
    ctx.moveTo(sx, sy);
    ctx.lineTo(sx + dirSign * Math.sin(hdgRad) * hdgLen, sy - dirSign * Math.cos(hdgRad) * hdgLen);
    ctx.stroke();
    const cogLen = knotsToMps(s.sog) * VECTOR_MINUTES * 60 * z;
    const cogRad = degToRad(s.cog);
    ctx.strokeStyle = this.cssVar('--ob-vector-cog');
    ctx.setLineDash([6, 4]);
    ctx.beginPath();
    ctx.moveTo(sx, sy);
    ctx.lineTo(sx + Math.sin(cogRad) * cogLen, sy - Math.cos(cogRad) * cogLen);
    ctx.stroke();
    ctx.setLineDash([]);

    // 自船外形(太小時以最小尺寸符號代替)
    const lppPx = this.config.lpp_m * z;
    ctx.fillStyle = this.cssVar('--ob-ship-fill');
    ctx.strokeStyle = this.cssVar('--ob-ship-stroke');
    ctx.lineWidth = 1.5;
    if (lppPx >= 12) {
      const origin = { x: s.pos.x, y: s.pos.y };
      const drawPoly = (poly: ReturnType<typeof hullOutline>, fill: boolean) => {
        ctx.beginPath();
        poly.forEach((p, i) => {
          const e = bodyToEnu(p, s.heading, origin);
          const [px, py] = toPx(e.x, e.y);
          if (i === 0) ctx.moveTo(px, py);
          else ctx.lineTo(px, py);
        });
        ctx.closePath();
        if (fill) ctx.fill();
        ctx.stroke();
      };
      drawPoly(hullOutline(this.config.lpp_m, this.config.breadth_m), true);
      drawPoly(superstructureOutline(this.config.lpp_m, this.config.breadth_m), false);
    } else {
      ctx.save();
      ctx.translate(sx, sy);
      ctx.rotate(hdgRad);
      ctx.beginPath();
      ctx.moveTo(0, -8);
      ctx.lineTo(5, 7);
      ctx.lineTo(-5, 7);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
      ctx.restore();
    }

    // 比例尺與北向
    ctx.fillStyle = this.cssVar('--ob-text-secondary');
    ctx.strokeStyle = this.cssVar('--ob-text-secondary');
    ctx.font = '11px system-ui, sans-serif';
    const barM = spacing;
    const barPx = barM * z;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(10, h - 12);
    ctx.lineTo(10 + barPx, h - 12);
    ctx.stroke();
    ctx.fillText(barM >= 1000 ? `${barM / 1000} km` : `${barM} m`, 10, h - 16);
    ctx.textAlign = 'right';
    ctx.fillText(`HDG ${formatHeading(s.heading)}  STW ${formatKnots(s.stw)} kn  COG ${formatHeading(s.cog)}  SOG ${formatKnots(s.sog)} kn`, w - 10, h - 8);
    ctx.textAlign = 'left';
    // 北向箭頭
    ctx.beginPath();
    ctx.moveTo(w - 20, 44);
    ctx.lineTo(w - 20, 26);
    ctx.lineTo(w - 24, 32);
    ctx.moveTo(w - 20, 26);
    ctx.lineTo(w - 16, 32);
    ctx.stroke();
    ctx.fillText('N', w - 24, 58);

    // 纜繩、錨鏈、拖船(在自船之上)
    if (this.hasExternal()) this.drawExternal(ctx, toPx, z);

    // 目標船(在自船之上、事件標記之下)
    if (this.targets.length > 0) this.drawTargets(ctx, toPx, z);

    // 事件標記(在自船之上)
    if (this.overlay && this.overlay.events.length > 0) this.drawEventMarks(ctx, toPx);

    // 流場層:網格箭頭(在區域之上、自船之下會被船體遮住,故畫在最上層但半透明)與左下角自船位置的流
    if (this.showCurrent) this.drawCurrentLayer(ctx, toPx, z, w, h);

    // 點選編輯的把手(最上層)
    if (this.editable && this.handles.length > 0) this.drawHandles(ctx, toPx);
  }

  /** 示意幾何:陸地/防波堤填色、淺灘虛線、錨地點線、速限區橙虛線、岸壁粗線;非海圖資料,只供定位參考。 */
  private drawSketch(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number], z: number, shapes: readonly OverlaySketchShape[]): void {
    const muted = this.cssVar('--ob-text-muted');
    const warning = this.cssVar('--ob-warning');
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    for (const sh of shapes) {
      ctx.beginPath();
      sh.points.forEach((p, i) => {
        const [px, py] = toPx(p.x, p.y);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.setLineDash([]);
      ctx.lineWidth = 1;
      ctx.globalAlpha = 1;
      switch (sh.kind) {
        case 'land':
          ctx.closePath();
          ctx.globalAlpha = 0.35;
          ctx.fillStyle = muted;
          ctx.fill();
          ctx.globalAlpha = 1;
          ctx.strokeStyle = muted;
          ctx.stroke();
          break;
        case 'breakwater':
          ctx.closePath();
          ctx.globalAlpha = 0.6;
          ctx.fillStyle = muted;
          ctx.fill();
          ctx.globalAlpha = 1;
          break;
        case 'shoal':
          ctx.closePath();
          ctx.globalAlpha = 0.15;
          ctx.fillStyle = warning;
          ctx.fill();
          ctx.globalAlpha = 1;
          ctx.strokeStyle = warning;
          ctx.setLineDash([3, 3]);
          ctx.stroke();
          break;
        case 'anchorage':
          ctx.closePath();
          ctx.strokeStyle = muted;
          ctx.setLineDash([1, 3]);
          ctx.stroke();
          break;
        case 'speedLimit':
          ctx.closePath();
          ctx.strokeStyle = warning;
          ctx.setLineDash([6, 3]);
          ctx.stroke();
          break;
        case 'bank':
          ctx.strokeStyle = muted;
          ctx.lineWidth = 3;
          ctx.stroke();
          break;
      }
      ctx.setLineDash([]);
      if (sh.name && z > 0.08 && sh.points.length > 0) {
        const c = sh.points.reduce((acc, p) => ({ x: acc.x + p.x / sh.points.length, y: acc.y + p.y / sh.points.length }), { x: 0, y: 0 });
        const [cx, cy] = toPx(c.x, c.y);
        ctx.fillStyle = muted;
        ctx.textAlign = 'center';
        ctx.fillText(sh.name, cx, cy);
        ctx.textAlign = 'left';
      }
    }
    ctx.restore();
  }

  /**
   * 流場層:overlay.currentField 的網格節點箭頭(可見範圍內取樣、最多 40 × 40;長度 = 格距 0.8 × 流速/最大流速,顏色依流速分 8 桶批次繪製;
   * 依 state 推得的 k(t) 縮放)、網格外框與說明;左下角畫 state.current(自船位置的流)的單一箭頭與數值、來源。
   */
  private drawCurrentLayer(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number], z: number, w: number, h: number): void {
    const s = this.state;
    const field = this.overlay?.currentField ?? null;
    let note = '';
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'left';
    if (field) {
      const scale: FieldScale = this.fieldPreview ? { active: true, k: 1, derived: false } : fieldScaleFromState(field, s);
      const g = field.grid;
      const max = fieldMaxSpeed(field);
      if (scale.active) {
        const view = this.view();
        const sampling = sampleFieldArrows(field, { cx: view.cx, cy: view.cy, halfWidth_m: w / 2 / z, halfHeight_m: h / 2 / z });
        const cellPx = sampling.cell_m * z;
        // 外框
        const b = fieldBounds(g);
        const [x0, y1] = toPx(b.minX, b.maxY);
        const [x1, y0] = toPx(b.maxX, b.minY);
        ctx.strokeStyle = this.cssVar('--ob-text-muted');
        ctx.lineWidth = 1;
        ctx.setLineDash([4, 4]);
        ctx.strokeRect(x0, y1, x1 - x0, y0 - y1);
        ctx.setLineDash([]);
        // 箭頭:依流速分桶,每桶一條路徑
        const buckets: Array<Array<{ px: number; py: number; ux: number; uy: number; len: number }>> = Array.from({ length: ARROW_COLOR_BUCKETS + 1 }, () => []);
        for (const a of sampling.arrows) {
          const speed = a.speed_kn * scale.k;
          const len = arrowLengthPx(speed, max, cellPx);
          const [px, py] = toPx(a.x, a.y);
          if (len < 1.5 || a.speed_kn <= 0) {
            buckets[0]!.push({ px, py, ux: 0, uy: 0, len: 0 });
            continue;
          }
          const ratio = max > 0 ? Math.min(1, speed / max) : 0;
          const bi = 1 + Math.min(ARROW_COLOR_BUCKETS - 1, Math.floor(ratio * ARROW_COLOR_BUCKETS));
          buckets[bi]!.push({ px, py, ux: a.u_kn / a.speed_kn, uy: -a.v_kn / a.speed_kn, len });
        }
        ctx.lineWidth = 1.5;
        ctx.globalAlpha = 0.9;
        buckets.forEach((list, bi) => {
          if (list.length === 0) return;
          if (bi === 0) {
            ctx.fillStyle = this.cssVar('--ob-text-muted');
            ctx.beginPath();
            for (const a of list) {
              ctx.moveTo(a.px + 1.5, a.py);
              ctx.arc(a.px, a.py, 1.5, 0, Math.PI * 2);
            }
            ctx.fill();
            return;
          }
          ctx.strokeStyle = speedColor(((bi - 0.5) / ARROW_COLOR_BUCKETS) * max, max);
          ctx.beginPath();
          for (const a of list) {
            const sx = a.px - (a.ux * a.len) / 2;
            const sy = a.py - (a.uy * a.len) / 2;
            const ex = a.px + (a.ux * a.len) / 2;
            const ey = a.py + (a.uy * a.len) / 2;
            ctx.moveTo(sx, sy);
            ctx.lineTo(ex, ey);
            const head = Math.min(6, a.len * 0.4);
            // 箭頭兩翼:與方向夾 ±150°
            const c = Math.cos(2.618);
            const sn = Math.sin(2.618);
            ctx.moveTo(ex, ey);
            ctx.lineTo(ex + head * (a.ux * c - a.uy * sn), ey + head * (a.ux * sn + a.uy * c));
            ctx.moveTo(ex, ey);
            ctx.lineTo(ex + head * (a.ux * c + a.uy * sn), ey + head * (-a.ux * sn + a.uy * c));
          }
          ctx.stroke();
        });
        ctx.globalAlpha = 1;
        const stride = sampling.strideX > 1 || sampling.strideY > 1 ? ` · 抽樣 1/${sampling.strideX}×1/${sampling.strideY}` : '';
        const kText = field.timeScale === 'none' ? '' : ` · k = ${scale.k.toFixed(2)}${scale.derived ? '' : '(預設)'}(${field.timeScale})`;
        note = `流場網格 ${g.nx}×${g.ny} · 格距 ${g.dx}×${g.dy} m · 最大 ${max.toFixed(1)} kn${kText}${stride}`;
      } else {
        note = `流場網格 ${g.nx}×${g.ny} 未作用(${s?.currentSource ? CURRENT_SOURCE_LABELS[s.currentSource] : '常數流/已覆寫'})`;
      }
    }
    // 左下角:自船位置的流(單一箭頭 + 數值 + 來源)
    const c = s?.current;
    if (c) {
      const color = this.cssVar('--ob-text-secondary');
      const ax = 24;
      const ay = h - 44;
      const rad = degToRad(c.set);
      const len = c.drift > 0.05 ? 24 : 0;
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = 2;
      if (len > 0) {
        const ex = ax + Math.sin(rad) * (len / 2);
        const ey = ay - Math.cos(rad) * (len / 2);
        ctx.beginPath();
        ctx.moveTo(ax - Math.sin(rad) * (len / 2), ay + Math.cos(rad) * (len / 2));
        ctx.lineTo(ex, ey);
        ctx.stroke();
        ctx.beginPath();
        ctx.moveTo(ex, ey);
        ctx.lineTo(ex - Math.sin(rad - 0.45) * 8, ey + Math.cos(rad - 0.45) * 8);
        ctx.lineTo(ex - Math.sin(rad + 0.45) * 8, ey + Math.cos(rad + 0.45) * 8);
        ctx.closePath();
        ctx.fill();
      } else {
        ctx.beginPath();
        ctx.arc(ax, ay, 3, 0, Math.PI * 2);
        ctx.fill();
      }
      const source = this.fieldPreview ? '情境預覽' : s?.currentSource ? CURRENT_SOURCE_LABELS[s.currentSource] : '常數流';
      ctx.fillText(`流 ${formatHeading(c.set)} ${formatKnots(c.drift)} kn · ${source}${field ? '(自船位置)' : ''}`, ax + 20, ay + 4);
    }
    if (note) {
      ctx.fillStyle = this.cssVar('--ob-text-secondary');
      ctx.fillText(note, 10, h - 60);
    }
    ctx.restore();
  }

  private handleColor(kind: PlotHandle['kind']): string {
    switch (kind) {
      case 'waypoint':
        return this.cssVar('--ob-accent');
      case 'vertex':
        return this.cssVar('--ob-warning');
      case 'own':
        return this.cssVar('--ob-ok');
      default:
        return this.cssVar('--ob-text');
    }
  }

  /** 可拖曳把手:圓點 + 標籤;選取者外加圓環;拖曳中畫在游標位置並以虛線連回原位。 */
  private drawHandles(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number]): void {
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'left';
    for (const hd of this.handles) {
      let x = hd.x;
      let y = hd.y;
      const drag = this.handleDrag;
      const color = this.handleColor(hd.kind);
      if (drag && drag.key === hd.key && drag.moved) {
        x = drag.x;
        y = drag.y;
        const [ox, oy] = toPx(hd.x, hd.y);
        const [nx, ny] = toPx(x, y);
        ctx.strokeStyle = color;
        ctx.lineWidth = 1;
        ctx.setLineDash([3, 3]);
        ctx.beginPath();
        ctx.moveTo(ox, oy);
        ctx.lineTo(nx, ny);
        ctx.stroke();
        ctx.setLineDash([]);
      }
      const [px, py] = toPx(x, y);
      const selected = hd.key === this.selectedKey;
      ctx.fillStyle = color;
      ctx.strokeStyle = this.cssVar('--ob-plot-bg');
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.arc(px, py, selected ? 6.5 : 5, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      if (selected) {
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(px, py, 11, 0, Math.PI * 2);
        ctx.stroke();
      }
      ctx.fillStyle = color;
      ctx.fillText(hd.label, px + 9, py - 7);
    }
    ctx.restore();
  }

  private drawTargets(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number], z: number): void {
    const alarmColor = this.cssVar('--ob-alarm');
    const normalColor = this.cssVar('--ob-text');
    const fill = this.cssVar('--ob-ship-fill');
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'left';
    for (const t of this.targets) {
      const alarm = this.alarmTargets.has(t.id);
      const color = alarm ? alarmColor : normalColor;
      const [px, py] = toPx(t.pos.x, t.pos.y);
      // COG/SOG 短向量(虛線,與自船相同的 6 分鐘)
      const cogLen = knotsToMps(t.sog) * VECTOR_MINUTES * 60 * z;
      if (cogLen > 2) {
        const cogRad = degToRad(t.cog);
        ctx.strokeStyle = color;
        ctx.lineWidth = 1;
        ctx.setLineDash([4, 3]);
        ctx.beginPath();
        ctx.moveTo(px, py);
        ctx.lineTo(px + Math.sin(cogRad) * cogLen, py - Math.cos(cogRad) * cogLen);
        ctx.stroke();
        ctx.setLineDash([]);
      }
      // 三角形依航向;尺寸隨 LOA 縮放但不小於符號尺寸
      const half = Math.max(7, Math.min(16, (t.loa * z) / 2));
      ctx.save();
      ctx.translate(px, py);
      ctx.rotate(degToRad(t.heading));
      ctx.beginPath();
      ctx.moveTo(0, -half);
      ctx.lineTo(half * 0.6, half * 0.8);
      ctx.lineTo(-half * 0.6, half * 0.8);
      ctx.closePath();
      ctx.lineWidth = alarm ? 2 : 1.5;
      ctx.strokeStyle = color;
      if (t.aisOn) {
        ctx.fillStyle = alarm ? alarmColor : fill;
        ctx.globalAlpha = alarm ? 0.6 : 1;
        ctx.fill();
        ctx.globalAlpha = 1;
        ctx.setLineDash([]);
      } else {
        ctx.setLineDash([3, 2]);
      }
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.restore();
      // 標籤:船名(無則 id);AIS 關以「(AIS 關)」標示
      ctx.fillStyle = color;
      ctx.fillText(`${t.name || t.id}${t.aisOn ? '' : '(AIS 關)'}`, px + half + 3, py + 4);
    }
    ctx.restore();
  }

  private hasExternal(): boolean {
    const s = this.state;
    return (s?.tugs?.length ?? 0) > 0 || (s?.anchors ?? []).some((a) => a.position !== undefined) || (s?.berth?.lines.length ?? 0) > 0;
  }

  private levelColor(level: LoadLevel): string {
    return level === 'alarm' ? this.cssVar('--ob-alarm') : level === 'caution' ? this.cssVar('--ob-caution') : this.cssVar('--ob-ok');
  }

  /** 碼頭線(硬接觸時紅)、碰墊(依壓縮比例著色)、繫船柱。 */
  private drawBerth(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number], z: number): void {
    const geo = this.overlay!.berth!;
    const contact = this.state?.berth;
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    // 碼頭線
    ctx.strokeStyle = contact?.quayContact ? this.cssVar('--ob-alarm') : this.cssVar('--ob-text-secondary');
    ctx.lineWidth = contact?.quayContact ? 4 : 3;
    ctx.beginPath();
    geo.quay.forEach((p, i) => {
      const [px, py] = toPx(p.x, p.y);
      if (i === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    });
    ctx.stroke();
    if (geo.quay.length > 0) {
      const [lx, ly] = toPx(geo.quay[0]!.x, geo.quay[0]!.y);
      ctx.fillStyle = this.cssVar('--ob-text-secondary');
      ctx.fillText(geo.name ?? geo.id, lx + 4, ly - 6);
    }
    // 碰墊:碼頭面向水域突出 height_m、沿線寬 width_m 的矩形(太小時以 4 px 方塊代替)
    const contacts = new Map((contact?.contacts ?? []).map((c) => [c.fenderId, c]));
    for (const f of geo.fenders) {
      const c = contacts.get(f.id);
      const hw = Math.max(f.width_m / 2, 2 / z);
      const h = Math.max(f.height_m, 4 / z);
      const corners = [
        [f.x - f.tx * hw, f.y - f.ty * hw],
        [f.x + f.tx * hw, f.y + f.ty * hw],
        [f.x + f.tx * hw + f.nx * h, f.y + f.ty * hw + f.ny * h],
        [f.x - f.tx * hw + f.nx * h, f.y - f.ty * hw + f.ny * h],
      ] as const;
      ctx.beginPath();
      corners.forEach(([x, y], i) => {
        const [px, py] = toPx(x, y);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.closePath();
      if (c) {
        ctx.fillStyle = this.levelColor(fenderLoadLevel(c, f.ratedDeflection_pct));
        ctx.fill();
      }
      ctx.strokeStyle = c ? ctx.fillStyle : this.cssVar('--ob-text-muted');
      ctx.lineWidth = 1;
      ctx.stroke();
      if (c && z > 0.6) {
        const [px, py] = toPx(f.x + f.nx * (h + 3 / z), f.y + f.ny * (h + 3 / z));
        ctx.fillStyle = ctx.strokeStyle as string;
        ctx.fillText(`${f.id} ${Math.round(c.deflection_pct ?? 0)}%`, px + 3, py);
      }
    }
    // 繫船柱
    ctx.fillStyle = this.cssVar('--ob-text');
    for (const b of geo.bollards) {
      const [px, py] = toPx(b.x, b.y);
      ctx.beginPath();
      ctx.arc(px, py, 2.5, 0, Math.PI * 2);
      ctx.fill();
      if (z > 1.2) ctx.fillText(b.id, px + 4, py + 10);
    }
    ctx.restore();
  }

  /** 纜繩線段、錨鏈與錨位、拖船示意位置與力向量。 */
  private drawExternal(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number], z: number): void {
    const s = this.state;
    const cfg = this.config;
    const origin = { x: s.pos.x, y: s.pos.y };
    const toEnu = (p: { x: number; y: number }) => bodyToEnu(p, s.heading, origin);
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    // 纜繩:導纜孔 → 繫船柱(繫著、絞/鬆中),依張力/MBL 著色
    const geo = this.overlay?.berth;
    const lineDefs = new Map((this.overlay?.mooringLines ?? []).map((l) => [l.id, l]));
    for (const l of s.berth?.lines ?? []) {
      if (!(l.status === 'fast' || l.status === 'heaving' || l.status === 'slacking') || !l.bollardId || !geo) continue;
      const bollard = geo.bollards.find((b) => b.id === l.bollardId);
      const fl = resolveBodyPoint(lineDefs.get(l.id)?.fairlead, cfg.lpp_m, cfg.breadth_m);
      if (!bollard || !fl) continue;
      const a = toEnu(fl);
      const [ax, ay] = toPx(a.x, a.y);
      const [bx, by] = toPx(bollard.x, bollard.y);
      ctx.strokeStyle = this.levelColor(lineLoadLevel(l));
      ctx.lineWidth = 2;
      ctx.setLineDash(l.status === 'fast' ? [] : [5, 3]);
      ctx.beginPath();
      ctx.moveTo(ax, ay);
      ctx.lineTo(bx, by);
      ctx.stroke();
      ctx.setLineDash([]);
      if (z > 0.8) {
        ctx.fillStyle = ctx.strokeStyle as string;
        ctx.fillText(`${l.id} ${l.tension_t.toFixed(1)} t`, (ax + bx) / 2 + 4, (ay + by) / 2 - 4);
      }
    }
    // 錨鏈與錨位
    for (const an of s.anchors ?? []) {
      if (!an.position) continue;
      const h = toEnu(hawsePoint(an.side, cfg.lpp_m, cfg.breadth_m, cfg.anchors));
      const [hx, hy] = toPx(h.x, h.y);
      const [px, py] = toPx(an.position.x, an.position.y);
      const color = an.status === 'dragging' ? this.cssVar('--ob-alarm') : an.status === 'holding' ? this.cssVar('--ob-ok') : this.cssVar('--ob-text-secondary');
      ctx.strokeStyle = color;
      ctx.lineWidth = anchorLevel(an) === 'ok' ? 1.5 : 2.5;
      ctx.setLineDash([2, 2]);
      ctx.beginPath();
      ctx.moveTo(hx, hy);
      ctx.lineTo(px, py);
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = color;
      ctx.textAlign = 'center';
      ctx.font = '15px system-ui, sans-serif';
      ctx.fillText('⚓', px, py + 5);
      ctx.font = '11px system-ui, sans-serif';
      ctx.fillText(`${ANCHOR_SIDE_LABEL[an.side]} ${(an.length_shackles ?? 0).toFixed(1)} 節`, px, py + 18);
      ctx.textAlign = 'left';
    }
    // 拖船:示意位置(◆)、連接點的力向量(長度 ∝ 出力)、拉時畫拖纜
    const tugDefs = new Map((this.overlay?.tugDefs ?? []).map((t) => [t.id, t]));
    for (const t of s.tugs ?? []) {
      const parted = t.status === 'parted' || t.lineParted === true;
      const color = parted ? this.cssVar('--ob-alarm') : t.status === 'working' ? this.cssVar('--ob-accent') : this.cssVar('--ob-text-secondary');
      const attach = resolveBodyPoint(tugDefs.get(t.id)?.attach ?? 'fwd', cfg.lpp_m, cfg.breadth_m) ?? { x: 0.45 * cfg.lpp_m, y: 0 };
      const a = toEnu(attach);
      const [ax, ay] = toPx(a.x, a.y);
      if (t.pos) {
        const [tx, ty] = toPx(t.pos.x, t.pos.y);
        if (t.mode === 'pull' && !parted && t.status !== 'free') {
          ctx.strokeStyle = color;
          ctx.lineWidth = 1;
          ctx.beginPath();
          ctx.moveTo(ax, ay);
          ctx.lineTo(tx, ty);
          ctx.stroke();
        }
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.moveTo(tx, ty - 6);
        ctx.lineTo(tx + 5, ty);
        ctx.lineTo(tx, ty + 6);
        ctx.lineTo(tx - 5, ty);
        ctx.closePath();
        ctx.fill();
        ctx.fillText(`${tugName(t)}${parted ? ' 斷纜' : ''}`, tx + 8, ty - 6);
      }
      if (t.force_t > 0.2) {
        const dir = degToRad(s.heading + t.direction_deg);
        const len = Math.min(90, 12 + t.force_t * 1.5);
        const ex = ax + Math.sin(dir) * len;
        const ey = ay - Math.cos(dir) * len;
        ctx.strokeStyle = color;
        ctx.fillStyle = color;
        ctx.lineWidth = 2.5;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(ex, ey);
        ctx.stroke();
        ctx.beginPath();
        ctx.moveTo(ex, ey);
        ctx.lineTo(ex - Math.sin(dir - 0.4) * 9, ey + Math.cos(dir - 0.4) * 9);
        ctx.lineTo(ex - Math.sin(dir + 0.4) * 9, ey + Math.cos(dir + 0.4) * 9);
        ctx.closePath();
        ctx.fill();
        ctx.fillText(`${t.force_t.toFixed(0)} t`, ex + 4, ey);
      }
    }
    ctx.restore();
  }

  private zoneColor(type: string): string {
    switch (type) {
      case 'noGo':
        return this.cssVar('--ob-alarm');
      case 'speedLimit':
        return this.cssVar('--ob-warning');
      case 'ukcPolicy':
        return this.cssVar('--ob-accent');
      default:
        return this.cssVar('--ob-text-muted');
    }
  }

  private drawOverlay(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number], z: number): void {
    const ov = this.overlay!;
    ctx.save();
    ctx.font = '11px system-ui, sans-serif';
    // 區域
    for (const zone of ov.zones) {
      const color = this.zoneColor(zone.type);
      ctx.beginPath();
      zone.polygon.forEach((p, i) => {
        const [px, py] = toPx(p.x, p.y);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.closePath();
      ctx.globalAlpha = 0.12;
      ctx.fillStyle = color;
      ctx.fill();
      ctx.globalAlpha = 1;
      ctx.strokeStyle = color;
      ctx.lineWidth = 1;
      ctx.setLineDash(zone.type === 'noGo' ? [] : [4, 3]);
      ctx.stroke();
      ctx.setLineDash([]);
      const c = zone.polygon.reduce((acc, p) => ({ x: acc.x + p.x / zone.polygon.length, y: acc.y + p.y / zone.polygon.length }), { x: 0, y: 0 });
      const [cx, cy] = toPx(c.x, c.y);
      ctx.fillStyle = color;
      ctx.textAlign = 'center';
      ctx.fillText(zone.name, cx, cy);
      ctx.textAlign = 'left';
    }
    // 航線:中心線、走廊、航點
    const route = ov.route;
    if (route) {
      const accent = this.cssVar('--ob-accent');
      const wps = route.waypoints;
      ctx.strokeStyle = accent;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      wps.forEach((p, i) => {
        const [px, py] = toPx(p.x, p.y);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.stroke();
      // 走廊:每段左右各一條平行線(到達航點的限制)
      ctx.setLineDash([3, 4]);
      ctx.lineWidth = 1;
      for (let i = 1; i < wps.length; i++) {
        const a = wps[i - 1]!;
        const b = wps[i]!;
        const limit = b.xtdLimit_m;
        if (limit === undefined || limit <= 0) continue;
        const dx = b.x - a.x;
        const dy = b.y - a.y;
        const len = Math.hypot(dx, dy);
        if (len < 1) continue;
        const nx = (dy / len) * limit;
        const ny = (-dx / len) * limit;
        for (const sign of [1, -1]) {
          const [x0, y0] = toPx(a.x + sign * nx, a.y + sign * ny);
          const [x1, y1] = toPx(b.x + sign * nx, b.y + sign * ny);
          ctx.beginPath();
          ctx.moveTo(x0, y0);
          ctx.lineTo(x1, y1);
          ctx.stroke();
        }
      }
      ctx.setLineDash([]);
      // 航點
      ctx.fillStyle = accent;
      for (const w of wps) {
        const [px, py] = toPx(w.x, w.y);
        ctx.beginPath();
        ctx.arc(px, py, w.checkpoint || w.report ? 5 : 3.5, 0, Math.PI * 2);
        if (w.checkpoint || w.report) {
          ctx.lineWidth = 1.5;
          ctx.stroke();
        } else ctx.fill();
        if (w.arrivalRadius_m !== undefined && w.arrivalRadius_m * z > 4) {
          ctx.beginPath();
          ctx.arc(px, py, w.arrivalRadius_m * z, 0, Math.PI * 2);
          ctx.setLineDash([2, 3]);
          ctx.stroke();
          ctx.setLineDash([]);
        }
        ctx.fillText(w.name, px + 7, py - 5);
      }
    }
    ctx.restore();
  }

  private drawEventMarks(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => [number, number]): void {
    const ov = this.overlay!;
    ctx.save();
    for (const m of ov.events) {
      const [px, py] = toPx(m.x, m.y);
      ctx.fillStyle = m.severity === 'alarm' ? this.cssVar('--ob-alarm') : m.severity === 'warning' ? this.cssVar('--ob-warning') : this.cssVar('--ob-accent');
      ctx.strokeStyle = this.cssVar('--ob-plot-bg');
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(px, py - 6);
      ctx.lineTo(px + 5, py + 3);
      ctx.lineTo(px - 5, py + 3);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
    }
    ctx.restore();
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-track-plot': TrackPlot;
  }
}
