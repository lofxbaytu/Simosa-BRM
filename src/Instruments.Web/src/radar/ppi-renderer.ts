// PPI 繪製(Canvas 2D):影像緩衝(VideoBuffer → 離屏 canvas,只上傳髒矩形)、掃描餘暉(圓錐漸層遮罩)、
// 回波軌跡(離屏 canvas,真軌跡依自船位移平移)、距離圈、方位刻度、艏向線、EBL/VRM、平行索引線、保護區、
// ARPA 符號(擷取中虛線圓、追蹤中圓 + 向量、危險紅色閃爍、失追交叉、選取方框、過去位置)、試操船、游標。
// 效能:每次重繪只有數個 GPU drawImage 與向量線段;影像資料由方位線增量寫入(見 video.ts)。

import type { SpokeFrame } from './frame.js';
import { VideoBuffer, buildPalette, type Rgb } from './video.js';
import {
  M_PER_NM,
  antennaOffsetEnu,
  bearingLine,
  eblBearings,
  eblReadout,
  enuToScreen,
  formatBearing,
  formatRangeNm,
  guardZoneTrueSector,
  parallelIndexLine,
  polarToScreen,
  pxPerNm,
  screenHeading,
  screenToPolar,
  vrmReadout,
  ORIENTATION_LABEL,
  type Motion,
  type Point,
  type PpiView,
} from './geometry.js';
import { formatRange, ringInterval, type RadarStatus, type RadarTrack, type TrialSetting } from './protocol.js';
import { symbolStyle, vectorEnu } from './arpa.js';
import { extrapolateTrack, type OwnNow, type PpiSettings } from './ppi-model.js';

const RAD = Math.PI / 180;
const KN = 1852 / 3600;
/** 掃描餘暉扇形數 */
const AFTERGLOW_WEDGES = 12;

export type PpiTheme = 'day' | 'dusk' | 'night';

interface Palette {
  bg: string;
  ppi: string;
  echo: Rgb;
  trail: string;
  ring: string;
  scale: string;
  text: string;
  heading: string;
  ebl: string;
  vrm: string;
  pi: string;
  arpa: string;
  danger: string;
  own: string;
  guard: string;
  trial: string;
  cursor: string;
  sweep: string;
}

const PALETTES: Record<PpiTheme, Palette> = {
  day: {
    bg: '#e3e8ee',
    ppi: '#06121c',
    echo: { r: 255, g: 214, b: 70 },
    trail: '#3f8fd6',
    ring: 'rgba(150,185,210,0.55)',
    scale: '#2b3a47',
    text: '#d8e6f0',
    heading: '#f2f6f9',
    ebl: '#56d4ff',
    vrm: '#56d4ff',
    pi: '#c9a6ff',
    arpa: '#7cf0a4',
    danger: '#ff4d4d',
    own: '#ffffff',
    guard: 'rgba(255,170,60,0.75)',
    trial: '#ffb347',
    cursor: '#ffffff',
    sweep: 'rgba(255,230,140,0.35)',
  },
  dusk: {
    bg: '#1b2024',
    ppi: '#03080d',
    echo: { r: 245, g: 200, b: 60 },
    trail: '#3477b8',
    ring: 'rgba(130,160,185,0.5)',
    scale: '#a3aeb8',
    text: '#c9d4dc',
    heading: '#e4e9ee',
    ebl: '#4cc3ee',
    vrm: '#4cc3ee',
    pi: '#b393ee',
    arpa: '#6ad893',
    danger: '#ff4d4d',
    own: '#eef2f5',
    guard: 'rgba(240,160,50,0.7)',
    trial: '#f0a040',
    cursor: '#e8edf1',
    sweep: 'rgba(240,220,130,0.3)',
  },
  night: {
    bg: '#000000',
    ppi: '#000000',
    echo: { r: 190, g: 120, b: 30 },
    trail: '#1f4a78',
    ring: 'rgba(90,105,115,0.55)',
    scale: '#6b747b',
    text: '#7b848c',
    heading: '#8f979e',
    ebl: '#2f7fa0',
    vrm: '#2f7fa0',
    pi: '#6f5a99',
    arpa: '#3f8f5e',
    danger: '#e03c3c',
    own: '#9aa3aa',
    guard: 'rgba(160,110,40,0.7)',
    trial: '#a06b25',
    cursor: '#9aa3aa',
    sweep: 'rgba(150,110,50,0.25)',
  },
};

export interface PpiDrawInput {
  settings: PpiSettings;
  view: PpiView;
  motion: Motion;
  headingDeg: number;
  headingValid: boolean;
  own: OwnNow;
  status: RadarStatus | null;
  tracks: readonly RadarTrack[];
  tracksT: number;
  tNow: number;
  ownHistory: ReadonlyArray<{ t: number; x: number; y: number }>;
  selectedId: number | null;
  acknowledged: ReadonlySet<string>;
  blinkOn: boolean;
  cursor: Point | null;
  transmitting: boolean;
  trial: TrialSetting | undefined;
  /** 每 CSS px 的裝置像素(線寬與字型縮放) */
  dpr: number;
}

export interface TargetScreenPoint {
  id: number;
  x: number;
  y: number;
}

type Ctx = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;
type AnyCanvas = HTMLCanvasElement | OffscreenCanvas;

function makeCanvas(w: number, h: number): AnyCanvas | null {
  if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(w, h);
  if (typeof document !== 'undefined') {
    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    return c;
  }
  return null;
}

function ctx2d(c: AnyCanvas): Ctx | null {
  try {
    return c.getContext('2d') as Ctx | null;
  } catch {
    return null;
  }
}

export class PpiRenderer {
  private readonly canvas: HTMLCanvasElement;
  private readonly ctx: CanvasRenderingContext2D | null;
  private video: VideoBuffer | null = null;
  private videoCanvas: AnyCanvas | null = null;
  private videoCtx: Ctx | null = null;
  private imageData: ImageData | null = null;
  private trailCanvas: AnyCanvas | null = null;
  private trailCtx: Ctx | null = null;
  private tmpCanvas: AnyCanvas | null = null;
  private tmpCtx: Ctx | null = null;
  private trailLastT = Number.NaN;
  private trailOwn: Point | null = null;
  private trailShift = { x: 0, y: 0 };
  private trailRangeM = 0;
  private theme: PpiTheme = 'dusk';
  private pal: Palette = PALETTES.dusk;
  /** 最近一次重繪的目標畫面位置(點選用) */
  targetPoints: TargetScreenPoint[] = [];
  /** 效能統計 */
  stats = { draws: 0, lastDrawMs: 0, avgDrawMs: 0, frames: 0 };

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    let c: CanvasRenderingContext2D | null = null;
    try {
      c = canvas.getContext('2d', { alpha: false });
    } catch {
      c = null;
    }
    this.ctx = c;
  }

  get available(): boolean {
    return this.ctx !== null;
  }

  get videoBuffer(): VideoBuffer | null {
    return this.video;
  }

  setTheme(t: PpiTheme): void {
    this.theme = t;
    this.pal = PALETTES[t];
    if (this.video) this.video.setPalette(buildPalette(this.pal.echo));
    this.clearTrails();
  }

  /**
   * 依畫面半徑建立影像緩衝與離屏 canvas:緩衝半徑 = PPI 半徑(裝置像素,16 px 量化,256–1600 px),
   * 置中且影像量程 = 顯示量程時 1:1 不縮放(北向上時為整數像素平移的直接複製,最省 CPU)。
   */
  ensureVideo(radiusDevicePx: number): void {
    const size = Math.min(1600, Math.max(256, Math.ceil((2 * radiusDevicePx) / 16) * 16));
    if (this.video && this.video.size === size) return;
    this.video = new VideoBuffer(size, buildPalette(this.pal.echo));
    this.videoCanvas = makeCanvas(size, size);
    this.videoCtx = this.videoCanvas ? ctx2d(this.videoCanvas) : null;
    this.imageData =
      this.videoCtx && typeof ImageData !== 'undefined' ? new ImageData(this.video.bytes, size, size) : null;
    this.trailCanvas = makeCanvas(size, size);
    this.trailCtx = this.trailCanvas ? ctx2d(this.trailCanvas) : null;
    this.tmpCanvas = makeCanvas(size, size);
    this.tmpCtx = this.tmpCanvas ? ctx2d(this.tmpCanvas) : null;
    this.clearTrails();
  }

  /** 寫入影像幀(不重繪) */
  pushFrame(f: SpokeFrame): void {
    if (!this.video) this.ensureVideo(256);
    this.video!.writeFrame(f, f.headingValid);
    this.stats.frames++;
  }

  clearVideo(): void {
    this.video?.clear();
    this.clearTrails();
  }

  clearTrails(): void {
    if (this.trailCtx && this.trailCanvas) this.trailCtx.clearRect(0, 0, this.trailCanvas.width, this.trailCanvas.height);
    this.trailLastT = Number.NaN;
    this.trailOwn = null;
    this.trailShift = { x: 0, y: 0 };
  }

  /** 把髒矩形上傳到離屏影像 canvas */
  private uploadVideo(): void {
    if (!this.video || !this.videoCtx || !this.imageData) return;
    const d = this.video.takeDirty();
    if (d) this.videoCtx.putImageData(this.imageData, 0, 0, d.x, d.y, d.w, d.h);
  }

  /** 回波軌跡:每 trailSec/20(≥ 1 s)模擬秒淡化一次並疊上目前影像;真軌跡依自船位移平移。 */
  private updateTrails(inp: PpiDrawInput): void {
    const s = inp.settings;
    const v = this.video;
    if (!v || !this.trailCtx || !this.trailCanvas || !this.tmpCtx || !this.tmpCanvas || !this.videoCanvas) return;
    if (s.trailSec <= 0 || !Number.isFinite(v.lastT)) {
      if (!Number.isNaN(this.trailLastT)) this.clearTrails();
      return;
    }
    if (this.trailRangeM !== v.rangeM) {
      this.clearTrails();
      this.trailRangeM = v.rangeM;
    }
    const step = Math.max(1, s.trailSec / 20);
    if (Number.isFinite(this.trailLastT) && v.lastT - this.trailLastT < step && v.lastT >= this.trailLastT) return;
    const size = v.size;
    const tc = this.trailCtx;
    // 真軌跡:畫布中心隨自船移動 → 內容反向平移
    if (inp.motion === 'true' && inp.own.valid && this.trailOwn) {
      const k = v.radius / v.rangeM;
      this.trailShift.x += -(inp.own.x - this.trailOwn.x) * k;
      this.trailShift.y += (inp.own.y - this.trailOwn.y) * k;
      const sx = Math.trunc(this.trailShift.x);
      const sy = Math.trunc(this.trailShift.y);
      if (sx !== 0 || sy !== 0) {
        this.tmpCtx.globalCompositeOperation = 'copy';
        this.tmpCtx.drawImage(this.trailCanvas, 0, 0);
        tc.clearRect(0, 0, size, size);
        tc.drawImage(this.tmpCanvas, sx, sy);
        this.trailShift.x -= sx;
        this.trailShift.y -= sy;
      }
    }
    this.trailOwn = inp.own.valid ? { x: inp.own.x, y: inp.own.y } : null;
    // 淡化:約 20 步衰減到 10%
    tc.globalCompositeOperation = 'destination-out';
    tc.fillStyle = 'rgba(0,0,0,0.11)';
    tc.fillRect(0, 0, size, size);
    // 疊上目前影像(上色為軌跡色)
    const t = this.tmpCtx;
    t.globalCompositeOperation = 'copy';
    t.drawImage(this.videoCanvas, 0, 0);
    t.globalCompositeOperation = 'source-in';
    t.fillStyle = this.pal.trail;
    t.fillRect(0, 0, size, size);
    tc.globalCompositeOperation = 'source-over';
    tc.globalAlpha = 0.55;
    tc.drawImage(this.tmpCanvas, 0, 0);
    tc.globalAlpha = 1;
    this.trailLastT = v.lastT;
  }

  draw(inp: PpiDrawInput): void {
    const ctx = this.ctx;
    if (!ctx) return;
    const t0 = typeof performance !== 'undefined' ? performance.now() : 0;
    const { view, settings: s, dpr } = inp;
    const W = this.canvas.width;
    const H = this.canvas.height;
    const p = this.pal;
    ctx.save();
    ctx.globalCompositeOperation = 'source-over';
    // 不使用圓形裁切(軟體繪製時每個繪圖指令都要套用整個裁切遮罩,代價為主要成本):
    // 先以 PPI 底色鋪滿,畫完圓內內容後再蓋上「圓外框」靜態層(圓外底色 + 方位刻度 + 北標,圓內透明)。
    ctx.fillStyle = p.ppi;
    ctx.fillRect(0, 0, W, H);
    ctx.save();

    this.uploadVideo();
    this.updateTrails(inp);
    this.drawVideo(ctx, inp);
    if (s.ringsOn) this.drawRings(ctx, view, dpr);
    if (s.showGuardZone && inp.status?.guardZone.enabled) this.drawGuardZone(ctx, inp);
    this.drawOwnShip(ctx, inp);
    this.drawPi(ctx, inp);
    this.drawEblVrm(ctx, inp);
    this.drawTargets(ctx, inp);
    if (inp.cursor) this.drawCursor(ctx, inp.cursor, dpr);
    ctx.restore();

    const frameLayer = this.staticLayer(W, H, inp);
    if (frameLayer) ctx.drawImage(frameLayer, 0, 0);
    else this.drawFrame(ctx, inp, W, H);
    this.drawCornerText(ctx, inp);
    ctx.restore();
    const dt = (typeof performance !== 'undefined' ? performance.now() : 0) - t0;
    this.stats.draws++;
    this.stats.lastDrawMs = dt;
    this.stats.avgDrawMs = this.stats.avgDrawMs === 0 ? dt : this.stats.avgDrawMs * 0.95 + dt * 0.05;
  }

  private staticCanvas: AnyCanvas | null = null;
  private staticKey = '';

  /** 靜態層快取:旋轉以 0.1° 量化(艏向上時航向變化 ≥ 0.1° 才重畫) */
  private staticLayer(W: number, H: number, inp: PpiDrawInput): AnyCanvas | null {
    const v = inp.view;
    const key = `${W}x${H}|${v.cx},${v.cy},${v.radiusPx}|${v.rotationDeg.toFixed(1)}|${this.theme}|${inp.headingValid}|${inp.dpr}`;
    if (this.staticCanvas && key === this.staticKey) return this.staticCanvas;
    if (!this.staticCanvas || this.staticCanvas.width !== W || this.staticCanvas.height !== H) {
      this.staticCanvas = makeCanvas(W, H);
    }
    const c = this.staticCanvas ? ctx2d(this.staticCanvas) : null;
    if (!c || !this.staticCanvas) return null;
    const sc = c as CanvasRenderingContext2D;
    sc.clearRect(0, 0, W, H);
    const q = Math.round(v.rotationDeg * 10) / 10;
    this.drawFrame(sc, { ...inp, view: { ...v, rotationDeg: q } }, W, H);
    this.staticKey = key;
    return this.staticCanvas;
  }

  /** 圓外框:圓外底色(圓內挖空)、方位刻度、北標 */
  private drawFrame(c: CanvasRenderingContext2D, inp: PpiDrawInput, W: number, H: number): void {
    const v = inp.view;
    c.save();
    c.fillStyle = this.pal.bg;
    c.beginPath();
    c.rect(0, 0, W, H);
    c.arc(v.cx, v.cy, v.radiusPx, 0, 2 * Math.PI, true);
    c.fill('evenodd');
    c.restore();
    this.drawBearingScale(c, inp);
  }

  /** 影像(與軌跡)以天線為中心、依緩衝穩定化方式旋轉縮放後畫上,再疊掃描餘暉 */
  private drawVideo(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const v = this.video;
    if (!v || !this.videoCanvas || v.rangeM <= 0) return;
    const view = inp.view;
    const st = inp.status;
    const ant = antennaOffsetEnu(screenHeadingOf(inp), st?.antennaFwd_m ?? 0, st?.antennaStbd_m ?? 0);
    const a = enuToScreen(view, ant.dE, ant.dN);
    const scale = ((v.rangeM / M_PER_NM) * pxPerNm(view)) / v.radius;
    const rot = v.stabilised ? -view.rotationDeg * RAD : 0;
    // 不旋轉且縮放≈1 → 整數像素對齊、關閉平滑(直接複製);其餘雙線性重取樣
    const direct = Math.abs(rot) < 1e-6 && Math.abs(scale - 1) < 0.02;
    ctx.save();
    if (direct) {
      ctx.translate(Math.round(a.x - v.radius) + v.radius, Math.round(a.y - v.radius) + v.radius);
    } else {
      ctx.translate(a.x, a.y);
      ctx.rotate(rot);
      ctx.scale(scale, scale);
    }
    ctx.imageSmoothingEnabled = !direct;
    if (inp.settings.trailSec > 0 && this.trailCanvas && Number.isFinite(this.trailLastT)) {
      ctx.drawImage(this.trailCanvas, -v.radius, -v.radius);
    }
    if (inp.transmitting) {
      ctx.drawImage(this.videoCanvas, -v.radius, -v.radius);
      // 掃描餘暉:掃描線前方(最舊)的回波以 PPI 底色半透明扇形覆蓋變暗。用 AFTERGLOW_WEDGES 個純色扇形
      // (不用圓錐漸層:軟體繪製時每像素 atan 代價高),覆蓋面積隨餘暉長短改變。
      const ag = inp.settings.afterglow;
      if (ag !== 'off') {
        const span = ag === 'short' ? 300 : 210; // 變暗的扇形總角度(度)
        const oldest = ag === 'short' ? 0.8 : 0.55;
        const n = AFTERGLOW_WEDGES;
        const w = span / n;
        ctx.fillStyle = this.pal.ppi;
        for (let k = 0; k < n; k++) {
          // k = 0 緊接掃描線前方(最舊);越往前越接近新
          const a0 = (v.sweepDeg + k * w - 90) * RAD;
          const a1 = (v.sweepDeg + (k + 1) * w - 90) * RAD + 0.004;
          ctx.globalAlpha = oldest * (1 - k / n);
          ctx.beginPath();
          ctx.moveTo(0, 0);
          ctx.arc(0, 0, v.radius + 1, a0, a1);
          ctx.closePath();
          ctx.fill();
        }
        ctx.globalAlpha = 1;
      }
      // 掃描線
      ctx.strokeStyle = this.pal.sweep;
      ctx.lineWidth = 1.5 / scale;
      ctx.beginPath();
      ctx.moveTo(0, 0);
      const sw = v.sweepDeg * RAD;
      ctx.lineTo(Math.sin(sw) * v.radius, -Math.cos(sw) * v.radius);
      ctx.stroke();
    }
    ctx.restore();
  }

  private drawRings(ctx: CanvasRenderingContext2D, view: PpiView, dpr: number): void {
    const step = ringInterval(view.rangeNm);
    const k = pxPerNm(view);
    ctx.strokeStyle = this.pal.ring;
    ctx.lineWidth = 1 * dpr;
    ctx.beginPath();
    // 距離圈以自船為中心;只畫到最遠圓邊(偏心時延伸到圓外的部分被裁切)
    const maxR = view.radiusPx + Math.hypot(view.ownX - view.cx, view.ownY - view.cy);
    for (let r = step; r * k <= maxR + 1; r += step) {
      ctx.moveTo(view.ownX + r * k, view.ownY);
      ctx.arc(view.ownX, view.ownY, r * k, 0, 2 * Math.PI);
    }
    ctx.stroke();
  }

  private drawGuardZone(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const gz = inp.status!.guardZone;
    const view = inp.view;
    const k = pxPerNm(view);
    const sec = guardZoneTrueSector(gz, screenHeadingOf(inp));
    ctx.save();
    ctx.strokeStyle = this.pal.guard;
    ctx.lineWidth = 1.5 * inp.dpr;
    ctx.setLineDash([6 * inp.dpr, 4 * inp.dpr]);
    ctx.beginPath();
    if (sec.full) {
      ctx.arc(view.ownX, view.ownY, gz.outer_nm * k, 0, 2 * Math.PI);
      ctx.moveTo(view.ownX + gz.inner_nm * k, view.ownY);
      ctx.arc(view.ownX, view.ownY, gz.inner_nm * k, 0, 2 * Math.PI);
    } else {
      const a0 = (sec.from - view.rotationDeg - 90) * RAD;
      let a1 = (sec.to - view.rotationDeg - 90) * RAD;
      if (a1 <= a0) a1 += 2 * Math.PI;
      ctx.arc(view.ownX, view.ownY, gz.outer_nm * k, a0, a1);
      ctx.arc(view.ownX, view.ownY, gz.inner_nm * k, a1, a0, true);
      ctx.closePath();
    }
    ctx.stroke();
    ctx.restore();
  }

  private drawOwnShip(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const view = inp.view;
    const p = this.pal;
    const dpr = inp.dpr;
    // 艏向線(CCRP 到圓邊)
    if (!inp.settings.headingLineOff) {
      const hl = bearingLine(view, screenHeadingOf(inp));
      ctx.strokeStyle = p.heading;
      ctx.lineWidth = 1.5 * dpr;
      ctx.beginPath();
      ctx.moveTo(hl.from.x, hl.from.y);
      ctx.lineTo(hl.to.x, hl.to.y);
      ctx.stroke();
    }
    // 自船向量(真向量模式:COG/SOG × 向量時間)
    if (inp.settings.vectorMode === 'true' && inp.own.valid && inp.headingValid) {
      const d = inp.own.sog * KN * inp.settings.vectorMin * 60;
      const e = enuToScreen(view, d * Math.sin(inp.own.cog * RAD), d * Math.cos(inp.own.cog * RAD));
      ctx.strokeStyle = p.own;
      ctx.lineWidth = 2 * dpr;
      ctx.beginPath();
      ctx.moveTo(view.ownX, view.ownY);
      ctx.lineTo(e.x, e.y);
      ctx.stroke();
    }
    // 試操船向量(延遲後新向量,虛線)
    if (inp.trial && inp.own.valid && inp.headingValid) {
      const own = inp.own;
      const dly = inp.trial.delay_min * 60;
      const d0 = own.sog * KN * dly;
      const p0 = enuToScreen(view, d0 * Math.sin(own.cog * RAD), d0 * Math.cos(own.cog * RAD));
      const d1 = inp.trial.speed * KN * inp.settings.vectorMin * 60;
      const p1 = enuToScreen(view, d1 * Math.sin(inp.trial.course * RAD), d1 * Math.cos(inp.trial.course * RAD), p0);
      ctx.save();
      ctx.strokeStyle = p.trial;
      ctx.lineWidth = 2 * dpr;
      ctx.setLineDash([5 * dpr, 4 * dpr]);
      ctx.beginPath();
      ctx.moveTo(view.ownX, view.ownY);
      ctx.lineTo(p0.x, p0.y);
      ctx.lineTo(p1.x, p1.y);
      ctx.stroke();
      ctx.restore();
    }
    // 自船符號:CCRP 圓 + 依船長比例的船形(量程夠小時)
    ctx.strokeStyle = p.own;
    ctx.lineWidth = 1.5 * dpr;
    ctx.beginPath();
    ctx.arc(view.ownX, view.ownY, 4 * dpr, 0, 2 * Math.PI);
    ctx.stroke();
  }

  private drawPi(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    ctx.save();
    ctx.strokeStyle = this.pal.pi;
    ctx.lineWidth = 1.2 * inp.dpr;
    ctx.setLineDash([10 * inp.dpr, 6 * inp.dpr]);
    for (const pi of inp.settings.pi) {
      if (!pi.on) continue;
      const b = pi.ref === 'T' ? pi.bearing : pi.bearing + screenHeadingOf(inp);
      const seg = parallelIndexLine(inp.view, b, pi.offsetNm);
      if (!seg) continue;
      ctx.beginPath();
      ctx.moveTo(seg.from.x, seg.from.y);
      ctx.lineTo(seg.to.x, seg.to.y);
      ctx.stroke();
    }
    ctx.restore();
  }

  private drawEblVrm(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const view = inp.view;
    const dpr = inp.dpr;
    ctx.save();
    inp.settings.ebl.forEach((e, i) => {
      if (!e.on) return;
      const { trueDeg } = eblBearings(e, screenHeadingOf(inp));
      const seg = bearingLine(view, trueDeg);
      ctx.strokeStyle = this.pal.ebl;
      ctx.lineWidth = 1.3 * dpr;
      ctx.setLineDash(i === 0 ? [8 * dpr, 4 * dpr] : [3 * dpr, 4 * dpr]);
      ctx.beginPath();
      ctx.moveTo(seg.from.x, seg.from.y);
      ctx.lineTo(seg.to.x, seg.to.y);
      ctx.stroke();
    });
    inp.settings.vrm.forEach((v, i) => {
      if (!v.on) return;
      ctx.strokeStyle = this.pal.vrm;
      ctx.lineWidth = 1.3 * dpr;
      ctx.setLineDash(i === 0 ? [8 * dpr, 4 * dpr] : [3 * dpr, 4 * dpr]);
      ctx.beginPath();
      ctx.arc(view.ownX, view.ownY, v.rangeNm * pxPerNm(view), 0, 2 * Math.PI);
      ctx.stroke();
    });
    ctx.restore();
  }

  private drawTargets(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const view = inp.view;
    const p = this.pal;
    const dpr = inp.dpr;
    const r = 8 * dpr;
    const pts: TargetScreenPoint[] = [];
    const ownHist = inp.ownHistory;
    const own = inp.own;
    const ownAt = (t: number): Point | null => {
      // 由自船歷史線性內插(相對過去位置用);早於歷史起點時以目前 COG/SOG 推算(推測航法)
      if (ownHist.length === 0 && !own.valid) return null;
      const dr = (ref: Point, tRef: number): Point => {
        const d = own.sog * KN * (tRef - t);
        return { x: ref.x - d * Math.sin(own.cog * RAD), y: ref.y - d * Math.cos(own.cog * RAD) };
      };
      if (ownHist.length === 0) return dr(own, inp.tNow);
      let prev = ownHist[0]!;
      if (t <= prev.t) return dr(prev, prev.t);
      for (const h of ownHist) {
        if (h.t >= t) {
          const f = h.t === prev.t ? 0 : (t - prev.t) / (h.t - prev.t);
          return { x: prev.x + (h.x - prev.x) * f, y: prev.y + (h.y - prev.y) * f };
        }
        prev = h;
      }
      return null;
    };
    ctx.save();
    ctx.font = `${11 * dpr}px "Segoe UI", "Microsoft JhengHei", sans-serif`;
    ctx.textBaseline = 'middle';
    for (const t of inp.tracks) {
      const pos = extrapolateTrack(t, inp.tracksT, inp.tNow);
      const sp = enuToScreen(view, pos.x - inp.own.x, pos.y - inp.own.y);
      pts.push({ id: t.id, x: sp.x, y: sp.y });
      const sty = symbolStyle(t, inp.acknowledged, inp.blinkOn);
      const col = sty.danger ? p.danger : p.arpa;
      // 過去位置(等間隔點)
      if (inp.settings.pastPositions && t.past.length > 0) {
        ctx.fillStyle = col;
        for (const pp of t.past) {
          let q: Point;
          if (inp.motion === 'relative') {
            const o = ownAt(pp.t);
            if (!o) continue;
            q = enuToScreen(view, pp.x - o.x, pp.y - o.y);
          } else {
            q = enuToScreen(view, pp.x - inp.own.x, pp.y - inp.own.y);
          }
          ctx.beginPath();
          ctx.arc(q.x, q.y, 1.8 * dpr, 0, 2 * Math.PI);
          ctx.fill();
        }
      }
      if (!sty.visible) continue;
      ctx.strokeStyle = col;
      ctx.fillStyle = col;
      ctx.lineWidth = (sty.danger ? 2.2 : 1.6) * dpr;
      ctx.setLineDash(sty.kind === 'acquiring' ? [3 * dpr, 3 * dpr] : []);
      ctx.beginPath();
      ctx.arc(sp.x, sp.y, r, 0, 2 * Math.PI);
      ctx.stroke();
      ctx.setLineDash([]);
      if (t.ais?.associated) {
        // IEC 62288 融合目標:雷達圓內加 AIS 三角(頂點向上)
        const h = r * 0.75;
        ctx.beginPath();
        ctx.moveTo(sp.x, sp.y - h);
        ctx.lineTo(sp.x + h * 0.85, sp.y + h * 0.6);
        ctx.lineTo(sp.x - h * 0.85, sp.y + h * 0.6);
        ctx.closePath();
        ctx.stroke();
      }
      if (sty.kind === 'lost') {
        const d = r * 1.1;
        ctx.beginPath();
        ctx.moveTo(sp.x - d, sp.y - d);
        ctx.lineTo(sp.x + d, sp.y + d);
        ctx.moveTo(sp.x + d, sp.y - d);
        ctx.lineTo(sp.x - d, sp.y + d);
        ctx.stroke();
      }
      if (sty.vector) {
        const ve = vectorEnu(t, inp.settings.vectorMode, inp.settings.vectorMin);
        if (ve) {
          const e = enuToScreen(view, ve.dE, ve.dN, sp);
          const len = Math.hypot(e.x - sp.x, e.y - sp.y);
          if (len > r) {
            const ux = (e.x - sp.x) / len;
            const uy = (e.y - sp.y) / len;
            ctx.beginPath();
            ctx.moveTo(sp.x + ux * r, sp.y + uy * r);
            ctx.lineTo(e.x, e.y);
            ctx.stroke();
          }
        }
      }
      if (t.id === inp.selectedId) {
        const q = r + 5 * dpr;
        ctx.lineWidth = 1.2 * dpr;
        ctx.strokeRect(sp.x - q, sp.y - q, 2 * q, 2 * q);
      }
      ctx.fillText(t.label, sp.x + r + 3 * dpr, sp.y - r);
    }
    ctx.restore();
    this.targetPoints = pts;
  }

  private drawCursor(ctx: CanvasRenderingContext2D, c: Point, dpr: number): void {
    ctx.save();
    ctx.strokeStyle = this.pal.cursor;
    ctx.lineWidth = 1 * dpr;
    const s = 9 * dpr;
    ctx.beginPath();
    ctx.moveTo(c.x - s, c.y);
    ctx.lineTo(c.x - 3 * dpr, c.y);
    ctx.moveTo(c.x + 3 * dpr, c.y);
    ctx.lineTo(c.x + s, c.y);
    ctx.moveTo(c.x, c.y - s);
    ctx.lineTo(c.x, c.y - 3 * dpr);
    ctx.moveTo(c.x, c.y + 3 * dpr);
    ctx.lineTo(c.x, c.y + s);
    ctx.stroke();
    ctx.restore();
  }

  /** 圓外方位刻度(每 1°/5°/10°,每 30° 標數字;刻度依定向旋轉)與北標 */
  private drawBearingScale(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const view = inp.view;
    const dpr = inp.dpr;
    const R = view.radiusPx;
    ctx.save();
    ctx.strokeStyle = this.pal.scale;
    ctx.fillStyle = this.pal.scale;
    ctx.lineWidth = 1 * dpr;
    ctx.beginPath();
    ctx.arc(view.cx, view.cy, R, 0, 2 * Math.PI);
    for (let b = 0; b < 360; b++) {
      const a = (b - view.rotationDeg) * RAD;
      const len = b % 10 === 0 ? 9 : b % 5 === 0 ? 6 : 3;
      const sx = Math.sin(a);
      const cy = -Math.cos(a);
      ctx.moveTo(view.cx + sx * R, view.cy + cy * R);
      ctx.lineTo(view.cx + sx * (R + len * dpr), view.cy + cy * (R + len * dpr));
    }
    ctx.stroke();
    ctx.font = `${11 * dpr}px "Segoe UI", "Microsoft JhengHei", sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (let b = 0; b < 360; b += 30) {
      const a = (b - view.rotationDeg) * RAD;
      const rr = R + 19 * dpr;
      ctx.fillText(String(b).padStart(3, '0'), view.cx + Math.sin(a) * rr, view.cy - Math.cos(a) * rr);
    }
    // 北標(穩定化時)
    if (inp.headingValid) {
      const a = (0 - view.rotationDeg) * RAD;
      // 圓外、尖端朝內(畫在靜態層,不被影像覆蓋)
      const tip = { x: view.cx + Math.sin(a) * (R + 1 * dpr), y: view.cy - Math.cos(a) * (R + 1 * dpr) };
      const w = 5 * dpr;
      const back = -10 * dpr;
      ctx.fillStyle = this.pal.heading;
      ctx.beginPath();
      ctx.moveTo(tip.x, tip.y);
      ctx.lineTo(tip.x - Math.sin(a) * back + Math.cos(a) * w, tip.y + Math.cos(a) * back + Math.sin(a) * w);
      ctx.lineTo(tip.x - Math.sin(a) * back - Math.cos(a) * w, tip.y + Math.cos(a) * back - Math.sin(a) * w);
      ctx.closePath();
      ctx.fill();
    }
    ctx.restore();
  }

  /** 圓外四角文字:左上定向/運動、右上量程/距離圈、左下 EBL/VRM、右下游標 */
  private drawCornerText(ctx: CanvasRenderingContext2D, inp: PpiDrawInput): void {
    const W = this.canvas.width;
    const H = this.canvas.height;
    const dpr = inp.dpr;
    const s = inp.settings;
    const pad = 8 * dpr;
    const lh = 15 * dpr;
    ctx.save();
    ctx.font = `600 ${12 * dpr}px "Segoe UI", "Microsoft JhengHei", sans-serif`;
    ctx.fillStyle = this.pal.scale;
    ctx.textBaseline = 'top';
    ctx.textAlign = 'left';
    const orient = inp.headingValid ? ORIENTATION_LABEL[s.orientation] : 'H UP(未穩定化)';
    ctx.fillText(orient, pad, pad);
    ctx.fillText(inp.motion === 'true' ? 'TM 真運動' : 'RM 相對運動', pad, pad + lh);
    ctx.fillText(`向量 ${s.vectorMode === 'true' ? 'T' : 'R'} ${s.vectorMin} min`, pad, pad + 2 * lh);
    if (s.trailSec > 0) ctx.fillText(`軌跡 ${inp.motion === 'true' ? 'T' : 'R'} ${trailLabel(s.trailSec)}`, pad, pad + 3 * lh);
    ctx.textAlign = 'right';
    ctx.fillText(`${formatRange(s.displayRangeNm)} NM`, W - pad, pad);
    ctx.fillText(s.ringsOn ? `圈距 ${formatRange(Number(ringInterval(s.displayRangeNm).toFixed(3)))} NM` : '距離圈 關', W - pad, pad + lh);
    if (inp.trial) {
      ctx.fillStyle = this.pal.trial;
      ctx.font = `700 ${16 * dpr}px "Segoe UI", sans-serif`;
      ctx.fillText('TRIAL', W - pad, pad + 2.4 * lh);
    }
    // 左下:EBL/VRM
    ctx.font = `${12 * dpr}px "Segoe UI", "Microsoft JhengHei", sans-serif`;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'bottom';
    ctx.fillStyle = this.pal.scale;
    let y = H - pad;
    for (let i = 1; i >= 0; i--) {
      const v = s.vrm[i]!;
      const e = s.ebl[i]!;
      if (v.on) {
        ctx.fillText(`VRM${i + 1} ${vrmReadout(v)}`, pad, y);
        y -= lh;
      }
      if (e.on) {
        ctx.fillText(`EBL${i + 1} ${eblReadout(e, screenHeadingOf(inp))}`, pad, y);
        y -= lh;
      }
    }
    // 右下:游標方位/距離
    if (inp.cursor) {
      const c = screenToPolar(inp.view, inp.cursor.x, inp.cursor.y);
      ctx.textAlign = 'right';
      // 航向無效時畫面為相對座標,游標方位為相對方位(R)
      ctx.fillText(`游標 ${formatBearing(c.bearing)} ${inp.headingValid ? 'T' : 'R'}  ${formatRangeNm(c.range)} NM`, W - pad, H - pad);
    }
    if (!inp.transmitting) {
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.font = `700 ${18 * dpr}px "Segoe UI", "Microsoft JhengHei", sans-serif`;
      ctx.fillStyle = this.pal.trial;
      const pt = polarToScreen(inp.view, inp.view.rotationDeg, inp.view.rangeNm * 0.35, { x: inp.view.cx, y: inp.view.cy });
      ctx.fillText(inp.status?.failed ? '雷達故障' : 'STANDBY 待機', pt.x, pt.y);
    }
    ctx.restore();
  }
}

/** 畫面參考航向(航向無效時 0;見 geometry.screenHeading) */
function screenHeadingOf(inp: Pick<PpiDrawInput, 'headingDeg' | 'headingValid'>): number {
  return screenHeading(inp.headingDeg, inp.headingValid);
}

export function trailLabel(sec: number): string {
  return sec <= 0 ? '關' : sec < 60 ? `${sec} s` : `${sec / 60} min`;
}

/** '#rrggbb' + alpha → rgba() */
export function withAlpha(hex: string, a: number): string {
  const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
  if (!m) return hex;
  return `rgba(${parseInt(m[1]!, 16)},${parseInt(m[2]!, 16)},${parseInt(m[3]!, 16)},${a.toFixed(3)})`;
}

