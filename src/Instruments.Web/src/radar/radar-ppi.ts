// <radar-ppi>:PPI 畫布元件。訂閱 RadarStore 的影像幀(寫入影像緩衝),以 requestAnimationFrame 重繪
// (有變更時才畫,上限 fps 預設 30;?fps= 可改);滑鼠:移動顯示游標方位/距離,點選依目前游標工具動作
// (選取目標、手動擷取、設定 EBL/VRM/PI、偏心),滾輪切換量程,右鍵回到選取工具。

import { LitElement, css, html } from 'lit';
import { customElement, property, query } from 'lit/decorators.js';
import { PpiRenderer, type PpiTheme } from './ppi-renderer.js';
import type { RadarStore } from './radar-store.js';
import { computeView, effectiveMotion, hitTestTarget, updateTmAnchor } from './ppi-model.js';
import {
  clampOffcentre,
  eblVrmFromScreen,
  insidePpi,
  pxPerNm,
  screenToPolar,
  signedDistanceToLine,
  polarToScreen,
  type Point,
  type PpiView,
} from './geometry.js';
import { normalizeHeading } from '../lib/angles.js';

const MARGIN_CSS_PX = 30;

@customElement('radar-ppi')
export class RadarPpi extends LitElement {
  static override styles = css`
    :host {
      display: block;
      position: relative;
      min-width: 0;
      min-height: 0;
      overflow: hidden;
    }
    canvas {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      display: block;
      cursor: crosshair;
      touch-action: none;
    }
    .fallback {
      position: absolute;
      inset: 0;
      display: grid;
      place-items: center;
      color: var(--ob-text-muted);
    }
  `;

  @property({ attribute: false }) store!: RadarStore;
  /** 重繪上限(fps) */
  @property({ type: Number }) fps = 30;
  @property({ type: String }) theme: PpiTheme = 'dusk';
  /** 效能量測:每個 rAF 都重繪(不等資料變更) */
  @property({ type: Boolean }) continuous = false;

  @query('canvas') private canvas!: HTMLCanvasElement;

  renderer: PpiRenderer | null = null;
  private raf = 0;
  private lastDraw = 0;
  private dirty = true;
  private lastBlink = false;
  private cursor: Point | null = null;
  private dpr = 1;
  private ro: ResizeObserver | null = null;
  private resizeTimer: ReturnType<typeof setTimeout> | null = null;
  private unsubFrame: (() => void) | null = null;
  private unsubStore: (() => void) | null = null;
  private wasTransmitting = true;
  private view: PpiView | null = null;
  /** 量測:最近 1 s 的重繪次數 */
  measuredFps = 0;
  private fpsCount = 0;
  private fpsT0 = 0;

  override render() {
    return html`<canvas
      @pointermove=${this.onMove}
      @pointerleave=${this.onLeave}
      @pointerdown=${this.onDown}
      @wheel=${this.onWheel}
      @contextmenu=${(e: Event) => e.preventDefault()}
      aria-label="雷達 PPI 畫面"
      role="img"
    ></canvas>`;
  }

  override firstUpdated(): void {
    this.renderer = new PpiRenderer(this.canvas);
    this.renderer.setTheme(this.theme);
    this.resize();
    if (typeof ResizeObserver !== 'undefined') {
      this.ro = new ResizeObserver(() => this.resize());
      this.ro.observe(this);
    }
    this.bindStore();
    if (typeof requestAnimationFrame !== 'undefined') this.raf = requestAnimationFrame(this.loop);
  }

  override updated(changed: Map<string, unknown>): void {
    if (changed.has('theme') && this.renderer) {
      this.renderer.setTheme(this.theme);
      this.dirty = true;
    }
    if (changed.has('store') && this.renderer) this.bindStore();
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.raf && typeof cancelAnimationFrame !== 'undefined') cancelAnimationFrame(this.raf);
    this.raf = 0;
    this.ro?.disconnect();
    this.unsubFrame?.();
    this.unsubStore?.();
  }

  private bindStore(): void {
    this.unsubFrame?.();
    this.unsubStore?.();
    if (!this.store) return;
    this.unsubFrame = this.store.onFrame((f) => {
      this.renderer?.pushFrame(f);
      this.dirty = true;
    });
    this.unsubStore = this.store.subscribe(() => {
      this.dirty = true;
    });
  }

  private resize(): void {
    if (!this.canvas) return;
    const dpr = Math.min(2, Math.max(1, (typeof devicePixelRatio === 'number' ? devicePixelRatio : 1) || 1));
    const w = Math.max(1, Math.round(this.clientWidth * dpr));
    const h = Math.max(1, Math.round(this.clientHeight * dpr));
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.dpr = dpr;
    // 影像緩衝依 PPI 半徑重建(重建查表約數十 ms;視窗拖曳時延後 200 ms 再做)
    const radius = Math.min(w, h) / 2 - MARGIN_CSS_PX * dpr;
    if (this.resizeTimer) clearTimeout(this.resizeTimer);
    if (!this.renderer?.videoBuffer) this.renderer?.ensureVideo(radius);
    else this.resizeTimer = setTimeout(() => {
      this.resizeTimer = null;
      this.renderer?.ensureVideo(radius);
      this.dirty = true;
    }, 200);
    this.dirty = true;
  }

  /** 由目前狀態計算畫面幾何(也更新真運動錨點) */
  computeView(): PpiView {
    const s = this.store;
    const own = s.ownNow();
    const inp = {
      width: this.canvas?.width ?? 600,
      height: this.canvas?.height ?? 600,
      marginPx: MARGIN_CSS_PX * this.dpr,
      settings: s.settings,
      headingDeg: s.headingDeg,
      headingValid: s.headingValid,
      own,
      tm: s.tm,
    };
    let view = computeView(inp);
    const active = effectiveMotion(s.settings, own, s.headingValid) === 'true';
    const tm = updateTmAnchor(s.tm, view, own, active);
    if (tm !== s.tm) {
      s.tm = tm;
      view = computeView({ ...inp, tm });
    }
    return view;
  }

  private loop = (ts: number): void => {
    this.raf = requestAnimationFrame(this.loop);
    if (!this.store || !this.renderer) return;
    const blink = Math.floor(ts / 500) % 2 === 0;
    const blinkChanged = blink !== this.lastBlink;
    this.lastBlink = blink;
    const needBlink = blinkChanged && this.store.pendingAlarms().length > 0;
    if (!this.dirty && !needBlink && !this.continuous) return;
    if (ts - this.lastDraw < 1000 / this.fps - 4) return;
    this.lastDraw = ts;
    this.dirty = false;
    this.drawNow(blink);
    this.fpsCount++;
    if (ts - this.fpsT0 >= 1000) {
      this.measuredFps = (this.fpsCount * 1000) / (ts - this.fpsT0);
      this.fpsCount = 0;
      this.fpsT0 = ts;
    }
  };

  /** 立即重繪(測試與截圖用) */
  drawNow(blinkOn = true): void {
    const s = this.store;
    const r = this.renderer;
    if (!s || !r) return;
    const tx = s.transmitting;
    if (this.wasTransmitting && !tx) r.clearVideo();
    this.wasTransmitting = tx;
    const view = this.computeView();
    this.view = view;
    const tr = s.tracks;
    r.draw({
      settings: s.settings,
      view,
      motion: s.motion(),
      headingDeg: s.headingDeg,
      headingValid: s.headingValid,
      own: s.ownNow(),
      status: s.status,
      tracks: tr?.targets ?? [],
      tracksT: tr?.t ?? 0,
      tNow: s.simTime,
      ownHistory: s.ownHistory,
      selectedId: s.selectedId,
      acknowledged: s.acknowledged,
      blinkOn,
      cursor: this.cursor,
      transmitting: tx,
      trial: tr?.trial ?? s.status?.trial,
      dpr: this.dpr,
    });
  }

  private toCanvas(e: PointerEvent | WheelEvent): Point {
    const rect = this.canvas.getBoundingClientRect();
    return { x: (e.clientX - rect.left) * this.dpr, y: (e.clientY - rect.top) * this.dpr };
  }

  private onMove = (e: PointerEvent): void => {
    const p = this.toCanvas(e);
    const v = this.view ?? this.computeView();
    this.cursor = insidePpi(v, p.x, p.y) ? p : null;
    this.dirty = true;
    this.dispatchEvent(
      new CustomEvent('ppi-cursor', {
        detail: this.cursor ? screenToPolar(v, p.x, p.y) : null,
        bubbles: true,
        composed: true,
      }),
    );
  };

  private onLeave = (): void => {
    this.cursor = null;
    this.dirty = true;
    this.dispatchEvent(new CustomEvent('ppi-cursor', { detail: null, bubbles: true, composed: true }));
  };

  private onWheel = (e: WheelEvent): void => {
    e.preventDefault();
    this.store.stepDisplayRange(e.deltaY > 0 ? 1 : -1);
  };

  private onDown = (e: PointerEvent): void => {
    if (e.button === 2) {
      this.store.setSettings({ tool: 'select' });
      return;
    }
    if (e.button !== 0) return;
    const p = this.toCanvas(e);
    this.clickAt(p.x, p.y);
  };

  /** 依游標工具處理畫面點選(畫布裝置像素座標)。 */
  clickAt(x: number, y: number): void {
    const s = this.store;
    const view = this.view ?? this.computeView();
    if (!insidePpi(view, x, y)) return;
    const hdg = s.headingDeg;
    const st = s.settings;
    switch (st.tool) {
      case 'select': {
        const id = hitTestTarget(this.renderer?.targetPoints ?? [], x, y, 14 * this.dpr);
        s.select(id);
        break;
      }
      case 'acquire': {
        const pol = screenToPolar(view, x, y);
        if (s.headingValid) s.send({ cmd: 'acquire', bearing: pol.bearing, range: pol.range });
        else s.send({ cmd: 'acquire', bearing: pol.bearing, range: pol.range, relative: true });
        break;
      }
      case 'ebl1':
      case 'ebl2': {
        const i = st.tool === 'ebl1' ? 0 : 1;
        const e = st.ebl[i]!;
        const r = eblVrmFromScreen(view, x, y, e.ref, hdg);
        const ebl = [...st.ebl] as typeof st.ebl;
        ebl[i] = { ...e, on: true, bearing: Math.round(r.bearing * 10) / 10 };
        s.setSettings({ ebl });
        break;
      }
      case 'vrm1':
      case 'vrm2': {
        const i = st.tool === 'vrm1' ? 0 : 1;
        const r = screenToPolar(view, x, y);
        const vrm = [...st.vrm] as typeof st.vrm;
        vrm[i] = { on: true, rangeNm: Math.round(r.range * 1000) / 1000 };
        s.setSettings({ vrm });
        break;
      }
      case 'pi1':
      case 'pi2': {
        const i = st.tool === 'pi1' ? 0 : 1;
        const pi = [...st.pi] as typeof st.pi;
        const line = pi[i]!;
        const tb = line.ref === 'T' ? line.bearing : normalizeHeading(line.bearing + hdg);
        const own = { x: view.ownX, y: view.ownY };
        const ahead = polarToScreen(view, tb, view.rangeNm * 0.5);
        const off = signedDistanceToLine(own, ahead, { x, y }) / pxPerNm(view);
        pi[i] = { ...line, on: true, offsetNm: Math.round(off * 1000) / 1000 };
        s.setSettings({ pi });
        break;
      }
      case 'offcentre': {
        const c = clampOffcentre(x - view.cx, y - view.cy, view.radiusPx);
        const fx = c.x / view.radiusPx;
        const fy = c.y / view.radiusPx;
        if (s.motion() === 'true') {
          const own = s.ownNow();
          s.tm = { x: own.x, y: own.y, fx, fy, rotation: view.rotationDeg };
          s.notify();
        } else {
          s.setSettings({ offcentre: { x: fx, y: fy } });
        }
        s.setSettings({ tool: 'select' });
        break;
      }
    }
    this.dirty = true;
  }

  /** 目前畫面幾何(測試用) */
  get currentView(): PpiView | null {
    return this.view;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'radar-ppi': RadarPpi;
  }
}
