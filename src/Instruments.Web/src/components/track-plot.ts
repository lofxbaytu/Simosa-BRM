// 鳥瞰航跡圖(第一版沒有 3D 視景時的視覺參考):本地 ENU、北上,自船外形依 LPP/B 比例,
// 航跡線、航向向量(6 分鐘)、COG/SOG 向量(虛線)、格線與比例尺;滾輪或按鈕縮放,拖曳平移。
// 疊圖(overlay,設計文件第 6 節 W8):情境航線(航點、走廊半寬)、區域多邊形(禁區/速限/UKC/報告)、事件標記。
// 目標船(state.targets[],規劃書第 5.2 節):三角形依航向、船名/ID 標籤、COG/SOG 短向量;AIS 關閉者空心虛線,
// 處於 cpaAlarm 的目標紅色(alarmTargets 由事件流推得);targetTable 開啟時左上角列 CPA/TCPA 小表(教官站完整模式)。

import { LitElement, html, css } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import type { OwnShipState } from '../types/state.js';
import type { ShipDisplayConfig } from '../lib/ship-config.js';
import type { TrackHistory } from '../lib/track-history.js';
import type { PlotOverlay } from '../lib/plot-overlay.js';
import type { TargetState } from '../types/target.js';
import { targetTableRows } from '../lib/targets.js';
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
          鳥瞰航跡圖(北上,本地 ENU)· 實線 = 航向向量 ${VECTOR_MINUTES} min · 虛線 = COG/SOG${this.targets.length > 0 ? ' · 三角 = 目標船(虛線 = AIS 關)' : ''}
        </div>
        ${this.targetTable && this.targets.length > 0 ? this.renderTargetTable() : null}
        <div class="toolbar">
          <button title="放大" @click=${() => this.setZoom(this.zoom * 1.5)}>+</button>
          <button title="縮小" @click=${() => this.setZoom(this.zoom / 1.5)}>−</button>
          <button class=${this.follow ? 'active' : ''} title="置中跟隨自船" @click=${this.recenter}>置中</button>
        </div>
        ${notice}
      </div>
    `;
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

    // 疊圖:區域、航線走廊(在航跡之下)
    if (this.overlay) this.drawOverlay(ctx, toPx, z);

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

    // 目標船(在自船之上、事件標記之下)
    if (this.targets.length > 0) this.drawTargets(ctx, toPx, z);

    // 事件標記(在自船之上)
    if (this.overlay && this.overlay.events.length > 0) this.drawEventMarks(ctx, toPx);
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
