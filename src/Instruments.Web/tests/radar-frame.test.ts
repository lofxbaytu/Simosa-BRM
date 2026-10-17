// 雷達二進位影像幀(SRAD v1):編碼/解析往返、位元組排列與 C# SpokeFrame.WriteHeader 相同、錯誤處理。
import { describe, expect, it } from 'vitest';
import {
  FLAG_DEGRADED,
  FLAG_HEADING_VALID,
  FLAG_TRANSMITTING,
  FrameError,
  SRAD_HEADER_BYTES,
  cellCentreM,
  encodeSpokeFrame,
  parseSpokeFrame,
  spokeRelativeDeg,
} from '../src/radar/frame.js';

function sample(data?: Uint8Array) {
  return {
    radarId: 'X',
    flags: FLAG_TRANSMITTING | FLAG_HEADING_VALID,
    scan: 51,
    firstSpoke: 2016,
    spokeCount: 32,
    spokesPerRev: 2048,
    cells: 4,
    rangeM: 11112,
    headingDeg: 47.25,
    cogDeg: 38.5,
    t: 129.04,
    ...(data ? { data } : {}),
  };
}

describe('SRAD 幀', () => {
  it('編碼後解析回相同欄位與資料', () => {
    const data = new Uint8Array(32 * 4).map((_v, i) => i & 0xff);
    const buf = encodeSpokeFrame(sample(data));
    expect(buf.byteLength).toBe(SRAD_HEADER_BYTES + 128);
    const f = parseSpokeFrame(buf);
    expect(f.radarId).toBe('X');
    expect(f.transmitting).toBe(true);
    expect(f.headingValid).toBe(true);
    expect(f.degraded).toBe(false);
    expect(f.scan).toBe(51);
    expect(f.firstSpoke).toBe(2016);
    expect(f.spokeCount).toBe(32);
    expect(f.spokesPerRev).toBe(2048);
    expect(f.cells).toBe(4);
    expect(f.rangeM).toBe(11112);
    expect(f.headingDeg).toBeCloseTo(47.25, 5);
    expect(f.cogDeg).toBeCloseTo(38.5, 5);
    expect(f.t).toBe(129.04);
    expect(Array.from(f.data)).toEqual(Array.from(data));
  });

  it('位元組排列(little-endian):magic 53 52 41 44、版本 1、radarId ASCII、各欄位偏移', () => {
    const buf = encodeSpokeFrame({ ...sample(), radarId: 'S', flags: FLAG_DEGRADED | FLAG_TRANSMITTING });
    const b = new Uint8Array(buf);
    expect(Array.from(b.subarray(0, 8))).toEqual([0x53, 0x52, 0x41, 0x44, 1, 'S'.charCodeAt(0), 5, 0]);
    const dv = new DataView(buf);
    expect(dv.getUint32(8, true)).toBe(51);
    expect(dv.getUint16(12, true)).toBe(2016);
    expect(dv.getUint16(14, true)).toBe(32);
    expect(dv.getUint16(16, true)).toBe(2048);
    expect(dv.getUint16(18, true)).toBe(4);
    expect(dv.getFloat32(20, true)).toBe(11112);
    expect(dv.getFloat64(32, true)).toBe(129.04);
    const f = parseSpokeFrame(buf);
    expect(f.degraded).toBe(true);
    expect(f.headingValid).toBe(false);
  });

  it('可解析位於較大緩衝中間的幀(ArrayBufferView 偏移)', () => {
    const inner = new Uint8Array(encodeSpokeFrame(sample(new Uint8Array(128).fill(7))));
    const big = new Uint8Array(inner.length + 10);
    big.set(inner, 5);
    const f = parseSpokeFrame(big.subarray(5, 5 + inner.length));
    expect(f.scan).toBe(51);
    expect(f.data[127]).toBe(7);
  });

  it('方位與距離換算:相對方位 = 索引 × 360/2048、格中心 = (i + 0.5) × range/cells', () => {
    const f = parseSpokeFrame(encodeSpokeFrame(sample()));
    expect(spokeRelativeDeg(f, 0)).toBeCloseTo((2016 * 360) / 2048, 9);
    expect(spokeRelativeDeg({ firstSpoke: 0, spokesPerRev: 2048 }, 512)).toBe(90);
    expect(cellCentreM(f, 0)).toBeCloseTo(11112 / 8, 9);
    expect(cellCentreM(f, 3)).toBeCloseTo((3.5 * 11112) / 4, 9);
  });

  it('格式錯誤時丟 FrameError', () => {
    expect(() => parseSpokeFrame(new ArrayBuffer(10))).toThrow(FrameError);
    const bad = encodeSpokeFrame(sample());
    new DataView(bad).setUint32(0, 0x12345678, true);
    expect(() => parseSpokeFrame(bad)).toThrow(/magic/);
    const v2 = encodeSpokeFrame(sample());
    new DataView(v2).setUint8(4, 2);
    expect(() => parseSpokeFrame(v2)).toThrow(/版本/);
    const short = encodeSpokeFrame(sample()).slice(0, SRAD_HEADER_BYTES + 10);
    expect(() => parseSpokeFrame(short)).toThrow(/長度不足/);
  });
});
