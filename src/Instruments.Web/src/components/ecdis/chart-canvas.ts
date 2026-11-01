// <ecdis-chart>:簡化 ECDIS 的海圖畫布(docs/design/ecdis-lite.md 第 2 節):S-52 風格四階水深著色與安全等深線、示意幾何底圖、
// 情境區域、航線與走廊、距離圈、航跡、AIS 目標(符號/向量/CPA 標籤)、自船(真比例外形或雙圓符號、航向線、6 分鐘 COG/SOG 向量、DR 標示)、
// EBL/VRM ×2、游標十字、比例尺、北箭頭、前方淺水命中點。互動:拖曳平移、滾輪量程、點選目標、EBL 工具點選。
// Canvas 2D 不可用(happy-dom)時只保留事件處理。

import { css, html } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { EcdisStoreElement, ECDIS_PALETTE, type EcdisPalette } from './ecdis-shared.js';
import type { Theme } from '../../lib/theme.js';
import { M_PER_NM, formatBearing, formatRangeNm } from '../../radar/geometry.js';
import { CHART_ORIENTATION_LABEL, chartToEnu, eblLine, eblVrmFromChart, enuToChart, pxPerMetre, rangeRingSpacingNm, scaleBarMetres, vectorEnd, vrmRadiusPx, type ChartView } from '../../lib/ecdis/projection.js';
import { classifyDepth, effectiveDepth, type DepthClass } from '../../lib/ecdis/safety-depth.js';
import { bodyToEnu, hullOutline } from '../../lib/ship-geometry.js';
import { getShipConfig, isShipId } from '../../lib/ship-config.js';
import type { EcdisTarget } from '../../lib/ecdis/ecdis-store.js';

const TARGET_HIT_PX = 14;
const DRAG_THRESHOLD_PX = 4;

@customElement('ecdis-chart')
export class EcdisChart extends EcdisStoreElement {
  static override styles = css`
    :host {
      display: block;
      position: relative;
      min-height: 0;
      overflow: hidden;
      touch-action: none;
      user-select: none;
    }
    canvas {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      display: block;
      cursor: crosshair;
    }
    canvas.pan {
      cursor: grab;
    }
    canvas.dragging {
      cursor: grabbing;
    }
  `;

  @property({ type: String }) theme: Theme = 'day';

  private canvas: HTMLCanvasElement | null = null;
  private raf = 0;
  private resizeObserver: ResizeObserver | null = null;
  private drag: { x: number; y: number; moved: boolean; lastX: number; lastY: number } | null = null;
  private dragging = false;
  /** 最近一次繪製的目標畫面座標(點選用) */
  private targetScreen: Array<{ id: string; x: number; y: number }> = [];
  /** 繪製統計(效能檢查) */
  lastDrawMs = 0;

  override connectedCallback(): void {
    super.connectedCallback();
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => this.scheduleDraw());
      this.resizeObserver.observe(this);
    }
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
  }

  override firstUpdated(): void {
    this.canvas = this.renderRoot.querySelector('canvas');
    this.scheduleDraw();
  }

  override updated(): void {
    this.scheduleDraw();
  }

  override render() {
    const tool = this.store?.settings.tool ?? 'pan';
    return html`<canvas
      class="${tool === 'pan' ? 'pan' : ''} ${this.dragging ? 'dragging' : ''}"
      @pointerdown=${this.onDown}
      @pointermove=${this.onMove}
      @pointerup=${this.onUp}
      @pointercancel=${this.onCancel}
      @pointerleave=${this.onLeave}
      @wheel=${this.onWheel}
    ></canvas>`;
  }

  // ------------------------------------------------------------ 幾何

  /** 目前畫面幾何(測試可直接呼叫)。 */
  view(width = this.canvas?.clientWidth ?? 0, height = this.canvas?.clientHeight ?? 0): ChartView {
    const s = this.store;
    const c = s.centre;
    return { width, height, centreX: c.x, centreY: c.y, rangeNm: s.settings.rangeNm, rotationDeg: s.rotationDeg };
  }

  private localPoint(e: PointerEvent): { x: number; y: number } {
    const r = (this.canvas ?? this).getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  /** 畫面點附近的目標 id(14 px 內最近者)。 */
  targetAt(px: number, py: number): string | null {
    let best: { id: string; d: number } | null = null;
    for (const t of this.targetScreen) {
      const d = Math.hypot(t.x - px, t.y - py);
      if (d <= TARGET_HIT_PX && (!best || d < best.d)) best = { id: t.id, d };
    }
    return best?.id ?? null;
  }

  // ------------------------------------------------------------ 互動

  private onDown = (e: PointerEvent): void => {
    if (e.button !== 0) return;
    const p = this.localPoint(e);
    this.drag = { x: p.x, y: p.y, moved: false, lastX: p.x, lastY: p.y };
    (e.target as HTMLElement).setPointerCapture?.(e.pointerId);
  };

  private onMove = (e: PointerEvent): void => {
    const p = this.localPoint(e);
    if (this.drag) {
      if (!this.drag.moved && Math.hypot(p.x - this.drag.x, p.y - this.drag.y) > DRAG_THRESHOLD_PX) {
        this.drag.moved = true;
        this.dragging = true;
        this.requestUpdate();
      }
      if (this.drag.moved && this.store.settings.tool === 'pan') {
        const v = this.view();
        const a = chartToEnu(v, this.drag.lastX, this.drag.lastY);
        const b = chartToEnu(v, p.x, p.y);
        this.store.pan(a.x - b.x, a.y - b.y);
      }
      this.drag.lastX = p.x;
      this.drag.lastY = p.y;
    }
    if (this.store.state) this.store.setCursor(chartToEnu(this.view(), p.x, p.y));
  };

  private onUp = (e: PointerEvent): void => {
    const p = this.localPoint(e);
    const d = this.drag;
    this.drag = null;
    if (this.dragging) {
      this.dragging = false;
      this.requestUpdate();
    }
    if (!d || d.moved || !this.store.state) return;
    this.clickAt(p.x, p.y);
  };

  /** 點選(無拖曳):EBL 工具 → 設定該組 EBL/VRM;平移工具 → 選取/取消目標。 */
  clickAt(px: number, py: number): void {
    const s = this.store;
    if (!s.state) return;
    const tool = s.settings.tool;
    if (tool === 'ebl1' || tool === 'ebl2') {
      const r = eblVrmFromChart(this.view(), s.state.pos, px, py);
      s.placeEblVrm(tool === 'ebl1' ? 0 : 1, r.bearing_deg, r.range_nm);
      return;
    }
    s.selectTarget(this.targetAt(px, py));
  }

  private onCancel = (): void => {
    this.drag = null;
    this.dragging = false;
  };

  private onLeave = (): void => {
    this.store.setCursor(null);
  };

  private onWheel = (e: WheelEvent): void => {
    e.preventDefault();
    this.store.stepRange(e.deltaY > 0 ? 1 : -1);
  };

  // ------------------------------------------------------------ 繪製

  private scheduleDraw(): void {
    if (this.raf || typeof requestAnimationFrame === 'undefined') return;
    this.raf = requestAnimationFrame(() => {
      this.raf = 0;
      this.draw();
    });
  }

  draw(): void {
    const canvas = this.canvas;
    const s = this.store;
    if (!canvas || !s) return;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    if (w === 0 || h === 0) return;
    const dpr = typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    let ctx: CanvasRenderingContext2D | null = null;
    try {
      ctx = canvas.getContext('2d');
    } catch {
      ctx = null;
    }
    if (!ctx) return;
    const t0 = typeof performance !== 'undefined' ? performance.now() : 0;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const pal = ECDIS_PALETTE[this.theme] ?? ECDIS_PALETTE.day;
    const view = this.view(w, h);
    const state = s.state;
    const tide = state?.tideHeight_m;
    const toPx = (x: number, y: number) => enuToChart(view, x, y);
    const ppm = pxPerMetre(view);
    ctx.font = '11px system-ui, sans-serif';

    // 1. 水深底色與水深區(S-52 四階)
    const layers = s.settings.layers;
    const contours = s.chart.contours;
    const baseClass = classifyDepth(effectiveDepth(s.chart.depth.baseDepth_m, tide), contours);
    ctx.fillStyle = layers.depth ? this.depthColor(pal, baseClass) : pal.deep;
    ctx.fillRect(0, 0, w, h);
    if (layers.depth) {
      for (const a of s.chart.depth.areas) {
        const cls = classifyDepth(effectiveDepth(a.depth_m, tide), contours);
        this.path(ctx, toPx, a.polygon, true);
        ctx.fillStyle = this.depthColor(pal, cls);
        ctx.fill();
        // 安全等深線:分類跨越安全深度的邊界以粗線標示
        const unsafe = cls === 'veryShallow' || cls === 'shallow';
        const baseUnsafe = baseClass === 'veryShallow' || baseClass === 'shallow';
        ctx.strokeStyle = pal.contour;
        ctx.lineWidth = unsafe !== baseUnsafe ? 2 : 1;
        ctx.setLineDash(unsafe !== baseUnsafe ? [] : [4, 3]);
        ctx.stroke();
        ctx.setLineDash([]);
        if (layers.labels && ppm * 300 > 20) this.label(ctx, toPx, a.polygon, `${a.depth_m} m`, pal.textMuted);
      }
    }

    // 2. 示意幾何
    if (layers.sketch && s.chart.sketch) {
      for (const sh of s.chart.sketch.shapes) {
        if (sh.kind === 'shoal' && layers.depth) continue; // 淺灘已由水深區著色
        this.path(ctx, toPx, sh.points, sh.kind !== 'bank');
        ctx.lineWidth = 1;
        ctx.setLineDash([]);
        switch (sh.kind) {
          case 'land':
            ctx.fillStyle = pal.land;
            ctx.fill();
            ctx.strokeStyle = pal.landLine;
            ctx.stroke();
            break;
          case 'breakwater':
            ctx.fillStyle = pal.breakwater;
            ctx.fill();
            break;
          case 'shoal':
            ctx.strokeStyle = pal.contour;
            ctx.setLineDash([3, 3]);
            ctx.stroke();
            break;
          case 'anchorage':
            ctx.strokeStyle = pal.zone;
            ctx.setLineDash([1, 4]);
            ctx.stroke();
            break;
          case 'speedLimit':
            ctx.strokeStyle = pal.zone;
            ctx.setLineDash([6, 3]);
            ctx.stroke();
            break;
          case 'bank':
            ctx.strokeStyle = pal.landLine;
            ctx.lineWidth = 3;
            ctx.stroke();
            break;
        }
        ctx.setLineDash([]);
        if (layers.labels && sh.name && sh.kind !== 'bank') this.label(ctx, toPx, sh.points, sh.name, pal.textMuted);
      }
    }

    // 3. 情境區域
    if (layers.zones) {
      for (const z of s.chart.zones) {
        this.path(ctx, toPx, z.polygon, true);
        const noGo = z.type === 'noGo';
        ctx.strokeStyle = noGo ? pal.zoneNoGo : pal.zone;
        ctx.lineWidth = noGo ? 2 : 1;
        ctx.setLineDash(noGo ? [] : z.type === 'speedLimit' ? [8, 3, 2, 3] : z.type === 'ukcPolicy' ? [6, 4] : [2, 4]);
        ctx.stroke();
        if (noGo) {
          ctx.globalAlpha = 0.12;
          ctx.fillStyle = pal.zoneNoGo;
          ctx.fill();
          ctx.globalAlpha = 1;
        }
        ctx.setLineDash([]);
        if (layers.labels) this.label(ctx, toPx, z.polygon, `${z.name}${z.speedLimit_kn !== undefined ? ` ≤${z.speedLimit_kn} kn` : ''}`, noGo ? pal.zoneNoGo : pal.zone);
      }
    }

    // 4. 航線與走廊
    const route = s.chart.route;
    const rs = s.routeStatus;
    if (layers.route && route) {
      const wps = route.waypoints;
      for (let i = 0; i < wps.length - 1; i++) {
        const a = wps[i]!;
        const b = wps[i + 1]!;
        const limit = b.xtdLimit_m;
        const active = rs !== null && !rs.complete && rs.legIndex === i;
        if (limit !== undefined && limit > 0) {
          const dx = b.x - a.x;
          const dy = b.y - a.y;
          const len = Math.hypot(dx, dy) || 1;
          const nx = (-dy / len) * limit;
          const ny = (dx / len) * limit;
          this.path(ctx, toPx, [{ x: a.x + nx, y: a.y + ny }, { x: b.x + nx, y: b.y + ny }, { x: b.x - nx, y: b.y - ny }, { x: a.x - nx, y: a.y - ny }], true);
          ctx.fillStyle = pal.routeCorridor;
          ctx.fill();
          ctx.strokeStyle = active ? pal.routeActive : pal.route;
          ctx.lineWidth = 1;
          ctx.setLineDash([2, 4]);
          ctx.stroke();
          ctx.setLineDash([]);
        }
        const pa = toPx(a.x, a.y);
        const pb = toPx(b.x, b.y);
        ctx.beginPath();
        ctx.moveTo(pa.x, pa.y);
        ctx.lineTo(pb.x, pb.y);
        ctx.strokeStyle = active ? pal.routeActive : pal.route;
        ctx.lineWidth = active ? 2.5 : 1.5;
        ctx.setLineDash(active ? [] : [8, 5]);
        ctx.stroke();
        ctx.setLineDash([]);
      }
      wps.forEach((wp, i) => {
        const p = toPx(wp.x, wp.y);
        const isNext = rs !== null && rs.nextIndex === i && !rs.complete;
        ctx.beginPath();
        ctx.arc(p.x, p.y, isNext ? 7 : 5, 0, Math.PI * 2);
        ctx.strokeStyle = isNext ? pal.routeActive : pal.route;
        ctx.lineWidth = isNext ? 2 : 1.5;
        ctx.stroke();
        if (isNext && wp.arrivalRadius_m) {
          ctx.beginPath();
          ctx.arc(p.x, p.y, wp.arrivalRadius_m * ppm, 0, Math.PI * 2);
          ctx.setLineDash([3, 3]);
          ctx.stroke();
          ctx.setLineDash([]);
        }
        if (layers.labels) {
          ctx.fillStyle = pal.text;
          ctx.textAlign = 'left';
          ctx.fillText(wp.name, p.x + 8, p.y - 6);
        }
      });
    }

    const own = state?.pos ?? { x: 0, y: 0 };
    const ownPx = toPx(own.x, own.y);

    // 5. 距離圈
    if (s.settings.rangeRings && state) {
      const spacing = rangeRingSpacingNm(view.rangeNm);
      ctx.strokeStyle = pal.rings;
      ctx.lineWidth = 1;
      ctx.setLineDash([2, 4]);
      ctx.fillStyle = pal.textMuted;
      ctx.textAlign = 'left';
      for (let k = 1; k <= 8; k++) {
        const r = spacing * k * M_PER_NM * ppm;
        if (r > Math.hypot(w, h)) break;
        ctx.beginPath();
        ctx.arc(ownPx.x, ownPx.y, r, 0, Math.PI * 2);
        ctx.stroke();
        ctx.fillText(formatRangeNm(spacing * k), ownPx.x + 3, ownPx.y - r - 2);
      }
      ctx.setLineDash([]);
    }

    // 6. 航跡
    if (s.settings.trailInterval_s > 0 && s.trail.length > 1) {
      ctx.strokeStyle = pal.track;
      ctx.lineWidth = 1;
      ctx.beginPath();
      s.trail.all().forEach((p, i) => {
        const q = toPx(p.x, p.y);
        if (i === 0) ctx.moveTo(q.x, q.y);
        else ctx.lineTo(q.x, q.y);
      });
      ctx.stroke();
    }

    // 7. AIS 目標
    this.targetScreen = [];
    if (layers.targets && state) {
      for (const t of s.targets) this.drawTarget(ctx, view, pal, t, s.selectedTargetId === t.id, s.settings.vectorMinutes, layers.labels);
    }

    // 8. 自船
    if (state) this.drawOwnShip(ctx, view, pal, ownPx);

    // 9. 前方淺水命中點
    if (state && s.ahead.hit && s.ahead.distance_m !== undefined) {
      const hit = vectorEnd(own, state.cog, 1, (s.ahead.distance_m / M_PER_NM) * 60);
      const p = toPx(hit.x, hit.y);
      ctx.strokeStyle = pal.targetDanger;
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(p.x - 7, p.y - 7);
      ctx.lineTo(p.x + 7, p.y + 7);
      ctx.moveTo(p.x + 7, p.y - 7);
      ctx.lineTo(p.x - 7, p.y + 7);
      ctx.stroke();
    }

    // 10. EBL / VRM
    if (state) {
      const accent = this.cssVar('--ob-accent');
      for (let i = 0; i < 2; i++) {
        const ebl = s.ebl[i]!;
        const vrm = s.vrm[i]!;
        ctx.strokeStyle = accent;
        ctx.fillStyle = accent;
        ctx.lineWidth = 1;
        ctx.setLineDash(i === 0 ? [6, 4] : [2, 3]);
        if (ebl.on) {
          const l = eblLine(view, own, ebl.bearing_deg);
          ctx.beginPath();
          ctx.moveTo(l.from.x, l.from.y);
          ctx.lineTo(l.to.x, l.to.y);
          ctx.stroke();
        }
        if (vrm.on) {
          const r = vrmRadiusPx(view, vrm.range_nm);
          ctx.beginPath();
          ctx.arc(ownPx.x, ownPx.y, r, 0, Math.PI * 2);
          ctx.stroke();
          ctx.setLineDash([]);
          ctx.textAlign = 'left';
          ctx.fillText(`VRM${i + 1} ${formatRangeNm(vrm.range_nm)} NM`, ownPx.x + r * Math.SQRT1_2 + 4, ownPx.y - r * Math.SQRT1_2 - 4);
        }
        ctx.setLineDash([]);
      }
    }

    // 11. 游標十字
    if (s.cursor) {
      const c = toPx(s.cursor.x, s.cursor.y);
      ctx.strokeStyle = this.cssVar('--ob-accent');
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(c.x - 8, c.y);
      ctx.lineTo(c.x + 8, c.y);
      ctx.moveTo(c.x, c.y - 8);
      ctx.lineTo(c.x, c.y + 8);
      ctx.stroke();
    }

    // 12. 文字:定向/量程、北箭頭、比例尺、非海圖提示
    this.drawFurniture(ctx, view, pal, s.settings.orientation, !!state);
    this.lastDrawMs = (typeof performance !== 'undefined' ? performance.now() : 0) - t0;
  }

  private depthColor(pal: EcdisPalette, cls: DepthClass): string {
    switch (cls) {
      case 'veryShallow':
        return pal.veryShallow;
      case 'shallow':
        return pal.shallow;
      case 'medium':
        return pal.medium;
      default:
        return pal.deep;
    }
  }

  private cssVar(name: string): string {
    try {
      return getComputedStyle(this).getPropertyValue(name).trim() || '#5aa2ff';
    } catch {
      return '#5aa2ff';
    }
  }

  private path(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => { x: number; y: number }, pts: ReadonlyArray<{ x: number; y: number }>, close: boolean): void {
    ctx.beginPath();
    pts.forEach((p, i) => {
      const q = toPx(p.x, p.y);
      if (i === 0) ctx.moveTo(q.x, q.y);
      else ctx.lineTo(q.x, q.y);
    });
    if (close) ctx.closePath();
  }

  private label(ctx: CanvasRenderingContext2D, toPx: (x: number, y: number) => { x: number; y: number }, pts: ReadonlyArray<{ x: number; y: number }>, text: string, color: string): void {
    if (pts.length === 0 || !text) return;
    const c = pts.reduce((acc, p) => ({ x: acc.x + p.x / pts.length, y: acc.y + p.y / pts.length }), { x: 0, y: 0 });
    const q = toPx(c.x, c.y);
    ctx.fillStyle = color;
    ctx.textAlign = 'center';
    ctx.fillText(text, q.x, q.y);
    ctx.textAlign = 'left';
  }

  private drawTarget(ctx: CanvasRenderingContext2D, view: ChartView, pal: EcdisPalette, t: EcdisTarget, selected: boolean, vectorMinutes: number, labels: boolean): void {
    const p = enuToChart(view, t.x, t.y);
    this.targetScreen.push({ id: t.id, x: p.x, y: p.y });
    const color = t.dangerous ? pal.targetDanger : pal.target;
    const a = ((t.heading - view.rotationDeg) * Math.PI) / 180;
    const size = 9;
    ctx.save();
    ctx.translate(p.x, p.y);
    ctx.rotate(a);
    ctx.beginPath();
    ctx.moveTo(0, -size * 1.3);
    ctx.lineTo(size * 0.7, size);
    ctx.lineTo(-size * 0.7, size);
    ctx.closePath();
    ctx.strokeStyle = color;
    ctx.lineWidth = t.dangerous ? 2.5 : 1.5;
    ctx.stroke();
    ctx.restore();
    // COG/SOG 向量
    if (t.sog > 0.1) {
      const e = vectorEnd(t, t.cog, t.sog, vectorMinutes);
      const q = enuToChart(view, e.x, e.y);
      ctx.strokeStyle = color;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(p.x, p.y);
      ctx.lineTo(q.x, q.y);
      ctx.stroke();
    }
    if (selected) {
      ctx.strokeStyle = color;
      ctx.setLineDash([3, 3]);
      ctx.strokeRect(p.x - 14, p.y - 14, 28, 28);
      ctx.setLineDash([]);
    }
    if (labels) {
      ctx.fillStyle = color;
      ctx.textAlign = 'left';
      ctx.fillText(t.label, p.x + 12, p.y - 2);
      ctx.fillStyle = t.dangerous ? pal.targetDanger : pal.textMuted;
      ctx.fillText(`CPA ${t.cpa_nm.toFixed(2)} / ${t.tcpa_min.toFixed(0)}'`, p.x + 12, p.y + 11);
    }
  }

  private drawOwnShip(ctx: CanvasRenderingContext2D, view: ChartView, pal: EcdisPalette, ownPx: { x: number; y: number }): void {
    const s = this.store;
    const state = s.state!;
    const ppm = pxPerMetre(view);
    const cfg = isShipId(state.shipId) ? getShipConfig(state.shipId) : null;
    const headingValid = s.headingValid;
    const posValid = s.positionValid;
    ctx.strokeStyle = pal.own;
    ctx.fillStyle = pal.own;
    ctx.lineWidth = 1.5;
    ctx.setLineDash(posValid ? [] : [4, 3]);
    if (cfg && cfg.lpp_m * ppm > 24 && headingValid) {
      // 真比例外形(S-52:放大到可辨時以實際外形顯示)
      const outline = hullOutline(cfg.lpp_m, cfg.breadth_m).map((p) => bodyToEnu(p, state.heading, state.pos));
      this.path(ctx, (x, y) => enuToChart(view, x, y), outline, true);
      ctx.stroke();
    } else {
      ctx.beginPath();
      ctx.arc(ownPx.x, ownPx.y, 6, 0, Math.PI * 2);
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(ownPx.x, ownPx.y, 2, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.setLineDash([]);
    // 航向線(電羅經有效時;長度到畫面邊緣的 1/3)
    if (headingValid) {
      const a = ((state.heading - view.rotationDeg) * Math.PI) / 180;
      const len = Math.min(view.width, view.height) / 3;
      ctx.beginPath();
      ctx.moveTo(ownPx.x, ownPx.y);
      ctx.lineTo(ownPx.x + len * Math.sin(a), ownPx.y - len * Math.cos(a));
      ctx.lineWidth = 1;
      ctx.stroke();
    }
    // COG/SOG 向量(虛線,每分鐘一個刻度)
    if (posValid && state.sog > 0.1) {
      const minutes = s.settings.vectorMinutes;
      const e = vectorEnd(state.pos, state.cog, state.sog, minutes);
      const q = enuToChart(view, e.x, e.y);
      ctx.setLineDash([6, 4]);
      ctx.beginPath();
      ctx.moveTo(ownPx.x, ownPx.y);
      ctx.lineTo(q.x, q.y);
      ctx.lineWidth = 1.5;
      ctx.stroke();
      ctx.setLineDash([]);
      for (let m = 1; m <= minutes; m++) {
        const k = vectorEnd(state.pos, state.cog, state.sog, m);
        const kp = enuToChart(view, k.x, k.y);
        ctx.beginPath();
        ctx.arc(kp.x, kp.y, 2, 0, Math.PI * 2);
        ctx.fill();
      }
    }
    if (!posValid) {
      ctx.fillStyle = pal.targetDanger;
      ctx.font = 'bold 13px system-ui, sans-serif';
      ctx.textAlign = 'left';
      ctx.fillText('DR', ownPx.x + 10, ownPx.y + 16);
      ctx.font = '11px system-ui, sans-serif';
    }
  }

  private drawFurniture(ctx: CanvasRenderingContext2D, view: ChartView, pal: EcdisPalette, orientation: 'north-up' | 'course-up', hasState: boolean): void {
    const pad = 8;
    ctx.fillStyle = pal.text;
    ctx.textAlign = 'left';
    ctx.font = 'bold 12px system-ui, sans-serif';
    ctx.fillText(`${CHART_ORIENTATION_LABEL[orientation]}  量程 ${formatRangeNm(view.rangeNm)} NM`, pad, pad + 12);
    ctx.font = '11px system-ui, sans-serif';
    if (!hasState) ctx.fillText('等待狀態…', pad, pad + 28);
    // 北箭頭(右上)
    const nx = view.width - pad - 16;
    const ny = pad + 18;
    const a = (-view.rotationDeg * Math.PI) / 180;
    ctx.save();
    ctx.translate(nx, ny);
    ctx.rotate(a);
    ctx.beginPath();
    ctx.moveTo(0, -14);
    ctx.lineTo(5, 6);
    ctx.lineTo(0, 2);
    ctx.lineTo(-5, 6);
    ctx.closePath();
    ctx.fillStyle = pal.text;
    ctx.fill();
    ctx.textAlign = 'center';
    ctx.fillText('N', 0, -17);
    ctx.restore();
    // 比例尺(左下)
    const metres = scaleBarMetres(view);
    const px = metres * pxPerMetre(view);
    const y = view.height - pad - 6;
    ctx.strokeStyle = pal.text;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(pad, y);
    ctx.lineTo(pad + px, y);
    ctx.moveTo(pad, y - 4);
    ctx.lineTo(pad, y + 4);
    ctx.moveTo(pad + px, y - 4);
    ctx.lineTo(pad + px, y + 4);
    ctx.stroke();
    ctx.textAlign = 'left';
    ctx.fillStyle = pal.text;
    ctx.fillText(metres >= M_PER_NM ? `${formatRangeNm(metres / M_PER_NM)} NM` : `${metres} m`, pad, y - 6);
    // 非海圖提示(下方置中)
    ctx.textAlign = 'center';
    ctx.fillStyle = pal.textMuted;
    ctx.fillText('簡化 ECDIS — 示意幾何,非 ENC,不可用於航行(docs/design/ecdis-lite.md)', view.width / 2, view.height - pad);
    // 游標讀數(右下)
    const c = this.store.cursor;
    if (c) {
      ctx.textAlign = 'right';
      ctx.fillStyle = pal.text;
      ctx.fillText(`游標 ${formatBearing(c.bearing_deg)} T  ${formatRangeNm(c.distance_nm)} NM${c.depth_m !== undefined ? `  深 ${c.depth_m.toFixed(1)} m` : ''}`, view.width - pad, view.height - pad);
    }
    ctx.textAlign = 'left';
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ecdis-chart': EcdisChart;
  }
}
