// 雷達二進位影像幀(SRAD v1)的解析與編碼。格式見 docs/design/radar-arpa.md 第 4 節與
// src/Contracts/radar.schema.json $defs/spokeFrame:40 位元組 little-endian 標頭 + spokeCount × cells 個 uint8 強度。
// 編碼只用於示範模式與測試(與 C# SpokeFrame.WriteHeader 相同排列)。

export const SRAD_MAGIC = 0x44415253; // 位元組 53 52 41 44 = "SRAD"
export const SRAD_VERSION = 1;
export const SRAD_HEADER_BYTES = 40;

export const FLAG_TRANSMITTING = 1;
export const FLAG_HEADING_VALID = 2;
export const FLAG_DEGRADED = 4;

export interface SpokeFrame {
  radarId: 'X' | 'S' | string;
  flags: number;
  transmitting: boolean;
  headingValid: boolean;
  degraded: boolean;
  scan: number;
  /** 第一條方位線索引(相對艏向、順時針) */
  firstSpoke: number;
  spokeCount: number;
  spokesPerRev: number;
  cells: number;
  /** 顯示量程(m);格 i 中心距離 = (i + 0.5) × rangeM / cells */
  rangeM: number;
  /** 指示航向(度) */
  headingDeg: number;
  /** 對地航向(度) */
  cogDeg: number;
  /** 模擬時間(s) */
  t: number;
  /** spokeCount × cells 個強度(0–255;指向原 ArrayBuffer,不複製) */
  data: Uint8Array;
}

export class FrameError extends Error {}

/** 解析一個二進位幀;格式不符時丟 FrameError。 */
export function parseSpokeFrame(buf: ArrayBuffer | ArrayBufferView): SpokeFrame {
  const bytes =
    buf instanceof ArrayBuffer ? new Uint8Array(buf) : new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
  if (bytes.byteLength < SRAD_HEADER_BYTES) throw new FrameError(`幀長度不足標頭 ${bytes.byteLength} < ${SRAD_HEADER_BYTES}`);
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (dv.getUint32(0, true) !== SRAD_MAGIC) throw new FrameError('不是雷達影像幀(magic 不符)');
  const version = dv.getUint8(4);
  if (version !== SRAD_VERSION) throw new FrameError(`不支援的幀版本 ${version}`);
  const flags = dv.getUint8(6);
  const firstSpoke = dv.getUint16(12, true);
  const spokeCount = dv.getUint16(14, true);
  const spokesPerRev = dv.getUint16(16, true);
  const cells = dv.getUint16(18, true);
  if (spokesPerRev === 0) throw new FrameError('spokesPerRev 為 0');
  if (firstSpoke >= spokesPerRev) throw new FrameError(`firstSpoke ${firstSpoke} 超出 ${spokesPerRev}`);
  const need = SRAD_HEADER_BYTES + spokeCount * cells;
  if (bytes.byteLength < need) throw new FrameError(`幀長度不足 ${bytes.byteLength} < ${need}`);
  return {
    radarId: String.fromCharCode(dv.getUint8(5)),
    flags,
    transmitting: (flags & FLAG_TRANSMITTING) !== 0,
    headingValid: (flags & FLAG_HEADING_VALID) !== 0,
    degraded: (flags & FLAG_DEGRADED) !== 0,
    scan: dv.getUint32(8, true),
    firstSpoke,
    spokeCount,
    spokesPerRev,
    cells,
    rangeM: dv.getFloat32(20, true),
    headingDeg: dv.getFloat32(24, true),
    cogDeg: dv.getFloat32(28, true),
    t: dv.getFloat64(32, true),
    data: bytes.subarray(SRAD_HEADER_BYTES, need),
  };
}

export interface SpokeFrameInit {
  radarId: string;
  flags: number;
  scan: number;
  firstSpoke: number;
  spokeCount: number;
  spokesPerRev: number;
  cells: number;
  rangeM: number;
  headingDeg: number;
  cogDeg: number;
  t: number;
  /** spokeCount × cells 個強度;省略時全為 0 */
  data?: Uint8Array;
}

/** 編碼一個二進位幀(示範模式與測試用)。 */
export function encodeSpokeFrame(f: SpokeFrameInit): ArrayBuffer {
  const n = f.spokeCount * f.cells;
  const buf = new ArrayBuffer(SRAD_HEADER_BYTES + n);
  const dv = new DataView(buf);
  dv.setUint32(0, SRAD_MAGIC, true);
  dv.setUint8(4, SRAD_VERSION);
  dv.setUint8(5, f.radarId.charCodeAt(0) & 0xff);
  dv.setUint8(6, f.flags & 0xff);
  dv.setUint8(7, 0);
  dv.setUint32(8, f.scan >>> 0, true);
  dv.setUint16(12, f.firstSpoke, true);
  dv.setUint16(14, f.spokeCount, true);
  dv.setUint16(16, f.spokesPerRev, true);
  dv.setUint16(18, f.cells, true);
  dv.setFloat32(20, f.rangeM, true);
  dv.setFloat32(24, f.headingDeg, true);
  dv.setFloat32(28, f.cogDeg, true);
  dv.setFloat64(32, f.t, true);
  if (f.data) new Uint8Array(buf, SRAD_HEADER_BYTES, n).set(f.data.subarray(0, n));
  return buf;
}

/** 方位線 j 的相對方位(度,0 = 船首,順時針)。 */
export function spokeRelativeDeg(frame: Pick<SpokeFrame, 'firstSpoke' | 'spokesPerRev'>, j: number): number {
  return (((frame.firstSpoke + j) % frame.spokesPerRev) * 360) / frame.spokesPerRev;
}

/** 距離格 i 的中心距離(m)。 */
export function cellCentreM(frame: Pick<SpokeFrame, 'rangeM' | 'cells'>, i: number): number {
  return ((i + 0.5) * frame.rangeM) / frame.cells;
}
