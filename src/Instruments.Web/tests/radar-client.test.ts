// 雷達 WebSocket 客戶端與影像緩衝:網址解析、二進位幀/JSON 分派、reqId、斷線重連、離線指令補送;
// VideoBuffer 方位線寫入位置(穩定化/未穩定化)、量程改變清除、髒矩形。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RadarClient, resolveRadarUrl, parseRadarId, type RadarConnStatus } from '../src/radar/radar-client.js';
import { encodeSpokeFrame, parseSpokeFrame, FLAG_HEADING_VALID, FLAG_TRANSMITTING } from '../src/radar/frame.js';
import { VideoBuffer, buildPalette } from '../src/radar/video.js';
import type { RadarMessage } from '../src/radar/protocol.js';

class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  binaryType = 'blob';
  sent: string[] = [];
  onopen: (() => void) | null = null;
  onclose: ((ev: { reason: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }
  send(d: string): void {
    this.sent.push(d);
  }
  close(): void {
    this.readyState = 3;
  }
  open(): void {
    this.readyState = 1;
    this.onopen?.();
  }
  message(data: unknown): void {
    this.onmessage?.({ data });
  }
  drop(reason = ''): void {
    this.readyState = 3;
    this.onclose?.({ reason });
  }
}

function frame(firstSpoke: number, count: number, value: number, heading = 0, rangeM = 1852 * 6, flags = FLAG_TRANSMITTING | FLAG_HEADING_VALID) {
  const cells = 64;
  return encodeSpokeFrame({
    radarId: 'X',
    flags,
    scan: 1,
    firstSpoke,
    spokeCount: count,
    spokesPerRev: 2048,
    cells,
    rangeM,
    headingDeg: heading,
    cogDeg: heading,
    t: 1,
    data: new Uint8Array(count * cells).fill(value),
  });
}

describe('網址', () => {
  it('?ws= 基底或完整網址、預設 ws://<主機>:8766/radar/<台>', () => {
    expect(resolveRadarUrl(new URLSearchParams(''), 'pc1', 'x')).toBe('ws://pc1:8766/radar/x');
    expect(resolveRadarUrl(new URLSearchParams(''), '', 's')).toBe('ws://localhost:8766/radar/s');
    expect(resolveRadarUrl(new URLSearchParams('ws=ws://10.0.0.2:9000/'), 'pc1', 's')).toBe('ws://10.0.0.2:9000/radar/s');
    expect(resolveRadarUrl(new URLSearchParams('ws=ws://h:8766/radar/x'), 'pc1', 's')).toBe('ws://h:8766/radar/s');
    expect(resolveRadarUrl(new URLSearchParams(''), '::1', 'x')).toBe('ws://[::1]:8766/radar/x');
    expect(parseRadarId('S')).toBe('s');
    expect(parseRadarId(null)).toBe('x');
  });
});

describe('RadarClient', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeWebSocket.instances = [];
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  function make() {
    const c = new RadarClient({
      url: 'ws://t:8766/radar/x',
      radar: 'x',
      webSocketFactory: (u) => new FakeWebSocket(u) as unknown as WebSocket,
    });
    const conn: RadarConnStatus[] = [];
    const msgs: RadarMessage[] = [];
    const frames: number[] = [];
    c.onConn((s) => conn.push(s));
    c.onMessage((m) => msgs.push(m));
    c.onFrame((f) => frames.push(f.firstSpoke));
    return { c, conn, msgs, frames };
  }

  it('連線後設定 binaryType、分派二進位幀與 JSON;壞幀計數不丟例外', () => {
    const { c, conn, msgs, frames } = make();
    c.start();
    const ws = FakeWebSocket.instances[0]!;
    expect(ws.binaryType).toBe('arraybuffer');
    ws.open();
    expect(conn).toEqual(['disconnected', 'connecting', 'connected']);
    ws.message(frame(100, 4, 9));
    ws.message(JSON.stringify({ type: 'event', radar: 'x', event: { kind: 'tracking', t: 3, message: 'm' } }));
    ws.message('not json');
    ws.message(new ArrayBuffer(12));
    expect(frames).toEqual([100]);
    expect(msgs.map((m) => m.type)).toEqual(['event']);
    expect(c.stats).toMatchObject({ frames: 1, messages: 1, badFrames: 1 });
  });

  it('指令帶 reqId;未連線時只補送設定類指令(每類最後一筆)', () => {
    const { c } = make();
    c.start();
    expect(c.send({ cmd: 'gain', value: 10 })).toBeNull();
    c.send({ cmd: 'gain', value: 60 });
    c.send({ cmd: 'acquire', bearing: 10, range: 2 });
    c.send({ cmd: 'standby' });
    c.send({ cmd: 'transmit' });
    const ws = FakeWebSocket.instances[0]!;
    ws.open();
    const sent = ws.sent.map((s) => JSON.parse(s) as Record<string, unknown>);
    expect(sent.map((s) => s['cmd'])).toEqual(['gain', 'transmit']);
    expect(sent[0]).toMatchObject({ cmd: 'gain', value: 60 });
    const id = c.send({ cmd: 'range', value: 3 });
    expect(id).toMatch(/^w\d+$/);
    expect(JSON.parse(ws.sent.at(-1)!)).toEqual({ cmd: 'range', value: 3, reqId: id });
  });

  it('斷線後指數退避重連(1 s → 1.7 s …,上限 5 s)', () => {
    const { c, conn } = make();
    c.start();
    FakeWebSocket.instances[0]!.drop('x');
    expect(conn.at(-1)).toBe('disconnected');
    vi.advanceTimersByTime(999);
    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeWebSocket.instances).toHaveLength(2);
    FakeWebSocket.instances[1]!.drop();
    vi.advanceTimersByTime(1699);
    expect(FakeWebSocket.instances).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(FakeWebSocket.instances).toHaveLength(3);
    c.stop();
    vi.advanceTimersByTime(10000);
    expect(FakeWebSocket.instances).toHaveLength(3);
  });
});

describe('VideoBuffer', () => {
  const pal = buildPalette({ r: 255, g: 200, b: 0 });

  it('調色盤:0 透明、alpha 隨強度上升', () => {
    expect(pal[0]).toBe(0);
    expect(pal[255]! >>> 24).toBe(255);
    expect(pal[1]! >>> 24).toBeLessThan(pal[200]! >>> 24);
    expect(pal[255]! & 0xff).toBe(255); // R 在最低位元組(little-endian RGBA)
  });

  it('未穩定化(航向無效):方位線索引 512 = 相對 090 → 緩衝右方', () => {
    const v = new VideoBuffer(256, pal);
    v.writeFrame(parseSpokeFrame(frame(508, 8, 255, 30, 11112, FLAG_TRANSMITTING)));
    expect(v.stabilised).toBe(false);
    expect(v.pixelAt(89.6, 0.5)).toBe(pal[255]);
    expect(v.pixelAt(120, 0.5)).toBe(0);
    expect(v.pixelAt(0, 0.5)).toBe(0);
  });

  it('穩定化:北向上方位 = 相對方位 + 航向(索引 0、航向 90 → 緩衝真方位 090)', () => {
    const v = new VideoBuffer(256, pal);
    v.writeFrame(parseSpokeFrame(frame(0, 2, 200, 90)));
    expect(v.stabilised).toBe(true);
    expect(v.pixelAt(90.1, 0.7)).toBe(pal[200]);
    expect(v.pixelAt(0, 0.7)).toBe(0);
    expect(v.sweepDeg).toBeCloseTo(90 + 360 / 2048, 6);
  });

  it('距離格對應:前半格有回波、後半無', () => {
    const v = new VideoBuffer(256, pal);
    const cells = 64;
    const data = new Uint8Array(cells * 8);
    for (let j = 0; j < 8; j++) data.fill(255, j * cells, j * cells + 32);
    const f = parseSpokeFrame(
      encodeSpokeFrame({ radarId: 'X', flags: 3, scan: 0, firstSpoke: 0, spokeCount: 8, spokesPerRev: 2048, cells, rangeM: 1000, headingDeg: 0, cogDeg: 0, t: 0, data }),
    );
    v.writeFrame(f);
    expect(v.pixelAt(0.8, 0.45)).toBe(pal[255]);
    expect(v.pixelAt(0.8, 0.55)).toBe(0);
  });

  it('量程改變時清除舊影像;髒矩形涵蓋寫入區', () => {
    const v = new VideoBuffer(256, pal);
    v.writeFrame(parseSpokeFrame(frame(0, 32, 255, 0)));
    v.takeDirty();
    v.writeFrame(parseSpokeFrame(frame(512, 32, 255, 0)));
    const d = v.takeDirty()!;
    expect(d.x).toBeGreaterThanOrEqual(127);
    expect(d.x + d.w).toBeLessThanOrEqual(256);
    expect(d.y).toBeLessThanOrEqual(128);
    expect(d.y + d.h).toBeGreaterThan(128 + 5);
    expect(d.w * d.h).toBeLessThan(256 * 256 / 4);
    expect(v.takeDirty()).toBeNull();
    expect(v.pixelAt(1, 0.5)).toBe(pal[255]);
    v.writeFrame(parseSpokeFrame(frame(1024, 1, 255, 0, 1852 * 12)));
    expect(v.pixelAt(1, 0.5)).toBe(0);
    expect(v.rangeM).toBe(1852 * 12);
  });

  it('連續兩圈:每個方位格都被寫到(航向變化時無空隙)', () => {
    const v = new VideoBuffer(256, pal);
    for (let s = 0; s < 2048; s += 32) v.writeFrame(parseSpokeFrame(frame(s, 32, 255, (s / 2048) * 3)));
    let empty = 0;
    for (let b = 0; b < 360; b += 0.25) if (v.pixelAt(b, 0.9) === 0) empty++;
    expect(empty).toBe(0);
  });
});
