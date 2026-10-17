// 雷達影像緩衝(極座標 → 直角座標):方形 RGBA 像素陣列,圓心為天線,半徑代表幀的 range_m。
// 預先計算「方位格 → 像素清單」查表(依像素角度把像素分到 spokesPerRev 個方位格),每條方位線只寫自己方位格的像素,
// 每像素一次查表:cell = (半徑比例 × cells) >> 16,顏色 = palette[強度]。效能:1024² 緩衝每幀(約 33 條線)約 2.6 萬次寫入,
// 只上傳髒矩形(putImageData dirty rect)。
// 穩定化:北向上儲存(真方位 = 相對方位 + heading_deg);航向無效時改存相對方位(只能艏向上)並清除舊影像。
// 每條方位線寫 floor 與 floor+1 兩個方位格,下一條線覆寫後者:航向變化時不留空隙(與實機掃描轉換相同的作法)。

import type { SpokeFrame } from './frame.js';

export interface Rgb {
  r: number;
  g: number;
  b: number;
}

/** 由回波顏色建立 256 色調色盤(Uint32 little-endian ABGR);強度 0 為透明,其餘 alpha 隨強度上升。 */
export function buildPalette(c: Rgb, minAlpha = 0.28, gamma = 0.7): Uint32Array {
  const p = new Uint32Array(256);
  for (let v = 1; v < 256; v++) {
    const a = Math.round(255 * (minAlpha + (1 - minAlpha) * Math.pow(v / 255, gamma)));
    p[v] = ((a & 0xff) << 24) | ((c.b & 0xff) << 16) | ((c.g & 0xff) << 8) | (c.r & 0xff);
  }
  p[0] = 0;
  return p;
}

export interface DirtyRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export class VideoBuffer {
  /** 緩衝邊長(px) */
  readonly size: number;
  readonly radius: number;
  readonly bytes: Uint8ClampedArray<ArrayBuffer>;
  readonly pixels: Uint32Array<ArrayBuffer>;
  palette: Uint32Array;

  private bins = 0;
  private binStart = new Int32Array(1);
  private pixIdx = new Int32Array(0);
  private pixR = new Uint16Array(0);
  private binBox = new Int16Array(0); // 每方位格 [minX, minY, maxX, maxY]

  /** 目前緩衝對應的影像量程(m);0 = 空 */
  rangeM = 0;
  /** 目前緩衝是否以北向上穩定化儲存 */
  stabilised = true;
  /** 最後一條方位線的緩衝方位(度;穩定化時為真方位,否則為相對方位) */
  sweepDeg = 0;
  /** 最後寫入的幀時間與航向 */
  lastT = Number.NaN;
  lastHeading = 0;
  lastCog = 0;
  framesWritten = 0;

  private dMinX = Infinity;
  private dMinY = Infinity;
  private dMaxX = -Infinity;
  private dMaxY = -Infinity;

  constructor(size: number, palette: Uint32Array) {
    this.size = Math.max(16, Math.floor(size));
    this.radius = this.size / 2;
    this.bytes = new Uint8ClampedArray(this.size * this.size * 4);
    this.pixels = new Uint32Array(this.bytes.buffer);
    this.palette = palette;
  }

  /** 方位格數(spokesPerRev);第一次寫幀時建立查表 */
  get binCount(): number {
    return this.bins;
  }

  /** 建立方位格查表(spokesPerRev 改變時重建)。 */
  buildLut(bins: number): void {
    const size = this.size;
    const R = this.radius;
    const c = size / 2;
    const counts = new Int32Array(bins + 1);
    const pixBin = new Int32Array(size * size).fill(-1);
    const step = 360 / bins;
    for (let y = 0; y < size; y++) {
      const dy = y + 0.5 - c;
      for (let x = 0; x < size; x++) {
        const dx = x + 0.5 - c;
        const r = Math.sqrt(dx * dx + dy * dy);
        if (r >= R) continue;
        let a = (Math.atan2(dx, -dy) * 180) / Math.PI;
        if (a < 0) a += 360;
        let b = Math.floor(a / step + 0.5);
        if (b >= bins) b -= bins;
        pixBin[y * size + x] = b;
        counts[b + 1]!++;
      }
    }
    for (let b = 0; b < bins; b++) counts[b + 1]! += counts[b]!;
    const total = counts[bins]!;
    const idx = new Int32Array(total);
    const rad = new Uint16Array(total);
    const fill = counts.slice(0, bins);
    const box = new Int16Array(bins * 4);
    for (let b = 0; b < bins; b++) {
      box[b * 4] = size;
      box[b * 4 + 1] = size;
      box[b * 4 + 2] = -1;
      box[b * 4 + 3] = -1;
    }
    for (let p = 0; p < size * size; p++) {
      const b = pixBin[p]!;
      if (b < 0) continue;
      const k = fill[b]!++;
      idx[k] = p;
      const x = p % size;
      const y = (p - x) / size;
      const dx = x + 0.5 - c;
      const dy = y + 0.5 - c;
      rad[k] = Math.min(65535, Math.floor((Math.sqrt(dx * dx + dy * dy) / R) * 65536));
      const o = b * 4;
      if (x < box[o]!) box[o] = x;
      if (y < box[o + 1]!) box[o + 1] = y;
      if (x > box[o + 2]!) box[o + 2] = x;
      if (y > box[o + 3]!) box[o + 3] = y;
    }
    this.bins = bins;
    this.binStart = counts;
    this.pixIdx = idx;
    this.pixR = rad;
    this.binBox = box;
  }

  /** 清除影像(量程或穩定化方式改變、待機)。 */
  clear(): void {
    this.pixels.fill(0);
    this.markAllDirty();
  }

  markAllDirty(): void {
    this.dMinX = 0;
    this.dMinY = 0;
    this.dMaxX = this.size - 1;
    this.dMaxY = this.size - 1;
  }

  /** 更換調色盤(主題切換);既有像素保留到下一圈覆寫。 */
  setPalette(p: Uint32Array): void {
    this.palette = p;
  }

  /**
   * 寫入一個幀。stabilise = 依幀的 heading_deg 存成北向上(需 headingValid)。
   * 量程或穩定化方式改變時先清除緩衝。
   */
  writeFrame(f: SpokeFrame, stabilise = f.headingValid): void {
    if (f.spokesPerRev !== this.bins) this.buildLut(f.spokesPerRev);
    if (this.rangeM === 0 || Math.abs(f.rangeM - this.rangeM) > this.rangeM * 1e-3 || stabilise !== this.stabilised) {
      this.pixels.fill(0);
      this.markAllDirty();
      this.rangeM = f.rangeM;
      this.stabilised = stabilise;
    }
    const bins = this.bins;
    const offBins = stabilise ? (f.headingDeg / 360) * bins : 0;
    const cells = f.cells;
    const data = f.data;
    const pal = this.palette;
    const px = this.pixels;
    const start = this.binStart;
    const idx = this.pixIdx;
    const rad = this.pixR;
    let lastBinF = 0;
    for (let j = 0; j < f.spokeCount; j++) {
      const binF = (f.firstSpoke + j + offBins) % bins;
      lastBinF = binF;
      const b0 = Math.floor(binF);
      const base = j * cells;
      for (let w = 0; w < 2; w++) {
        let b = b0 + w;
        if (b >= bins) b -= bins;
        if (b < 0) b += bins;
        const e = start[b + 1]!;
        for (let k = start[b]!; k < e; k++) {
          const cell = (rad[k]! * cells) >>> 16;
          px[idx[k]!] = pal[data[base + cell]!]!;
        }
        const o = b * 4;
        const bx0 = this.binBox[o]!;
        if (bx0 <= this.binBox[o + 2]!) {
          if (bx0 < this.dMinX) this.dMinX = bx0;
          if (this.binBox[o + 1]! < this.dMinY) this.dMinY = this.binBox[o + 1]!;
          if (this.binBox[o + 2]! > this.dMaxX) this.dMaxX = this.binBox[o + 2]!;
          if (this.binBox[o + 3]! > this.dMaxY) this.dMaxY = this.binBox[o + 3]!;
        }
      }
    }
    if (f.spokeCount > 0) this.sweepDeg = (lastBinF * 360) / bins;
    this.lastT = f.t;
    this.lastHeading = f.headingDeg;
    this.lastCog = f.cogDeg;
    this.framesWritten++;
  }

  /** 取出並重設髒矩形(無變更時回傳 null)。 */
  takeDirty(): DirtyRect | null {
    if (this.dMaxX < this.dMinX || this.dMaxY < this.dMinY) return null;
    const r = { x: this.dMinX, y: this.dMinY, w: this.dMaxX - this.dMinX + 1, h: this.dMaxY - this.dMinY + 1 };
    this.dMinX = this.dMinY = Infinity;
    this.dMaxX = this.dMaxY = -Infinity;
    return r;
  }

  /** 讀取緩衝內某點(緩衝方位度、距離比例 0–1)的像素值(測試用) */
  pixelAt(bearingDeg: number, rangeFraction: number): number {
    const a = (bearingDeg * Math.PI) / 180;
    const r = rangeFraction * this.radius;
    const x = Math.floor(this.size / 2 + r * Math.sin(a));
    const y = Math.floor(this.size / 2 - r * Math.cos(a));
    if (x < 0 || y < 0 || x >= this.size || y >= this.size) return 0;
    return this.pixels[y * this.size + x]!;
  }
}
