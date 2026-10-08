// 鳥瞰航跡圖(第一版沒有 3D 視景時的視覺參考):本地 ENU、北上,自船外形依 LPP/B 比例,
// 航跡線、航向向量(6 分鐘)、COG/SOG 向量(虛線)、格線與比例尺;滾輪或按鈕縮放,拖曳平移。

import { LitElement, html, css } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import type { OwnShipState } from '../types/state.js';
import type { ShipDisplayConfig } from '../lib/ship-config.js';
import type { TrackHistory } from '../lib/track-history.js';
import { bodyToEnu, chooseGridSpacing, hullOutline, superstructureOutline } from '../lib/ship-geometry.js';
import { degToRad, knotsToMps } from '../lib/angles.js';
import { formatHeading, formatKnots } from '../lib/format.js';

const VECTOR_MINUTES = 6;
const ZOOM_MIN = 0.02; // px/m(約 5 km 寬)
const ZOOM_MAX = 8;

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
      .notice.nodata {
        background: var(--ob-bg-surface);
        color: var(--ob-text-secondary);
        border: 1px solid var(--ob-border);
        font-size: 16px;
        letter-spacing: 0.05em;
      }
    `,
  ];

  @property({ attribute: false }) state!: OwnShipState;
  @property({ attribute: false }) config!: ShipDisplayConfig;
  @property({ attribute: false }) history!: TrackHistory;
  @property({ type: Boolean }) noData = false;

  /** 比例尺(px/m) */
  @state() private zoom = 0.6;
  @state() private follow = true;
  @state() private dragging = false;
  /** 平移偏移(公尺,ENU) */
  private panX = 0;
  private panY = 0;
  private dragStart: { px: number; py: number; panX: number; panY: number } | null = null;

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
    return html`
      <div class="frame">
        <canvas
          class=${this.dragging ? 'dragging' : ''}
          @wheel=${this.onWheel}
          @pointerdown=${this.onPointerDown}
          @pointermove=${this.onPointerMove}
          @pointerup=${this.onPointerUp}
          @pointercancel=${this.onPointerUp}
        ></canvas>
        <div class="legend">
          鳥瞰航跡圖(北上,本地 ENU)· 實線 = 航向向量 ${VECTOR_MINUTES} min · 虛線 = COG/SOG
        </div>
        <div class="toolbar">
          <button title="放大" @click=${() => this.setZoom(this.zoom * 1.5)}>+</button>
          <button title="縮小" @click=${() => this.setZoom(this.zoom / 1.5)}>−</button>
          <button class=${this.follow ? 'active' : ''} title="置中跟隨自船" @click=${this.recenter}>置中</button>
        </div>
        ${notice}
      </div>
    `;
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

  private onPointerDown = (ev: PointerEvent): void => {
    this.canvas.setPointerCapture(ev.pointerId);
    this.dragStart = { px: ev.clientX, py: ev.clientY, panX: this.panX, panY: this.panY };
    this.dragging = true;
  };

  private onPointerMove = (ev: PointerEvent): void => {
    if (!this.dragStart) return;
    const dx = (ev.clientX - this.dragStart.px) / this.zoom;
    const dy = (ev.clientY - this.dragStart.py) / this.zoom;
    this.panX = this.dragStart.panX - dx;
    this.panY = this.dragStart.panY + dy;
    this.follow = false;
    this.scheduleDraw();
  };

  private onPointerUp = (ev: PointerEvent): void => {
    if (this.dragStart) {
      try {
        this.canvas.releasePointerCapture(ev.pointerId);
      } catch {
        /* 忽略 */
      }
    }
    this.dragStart = null;
    this.dragging = false;
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
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-track-plot': TrackPlot;
  }
}
