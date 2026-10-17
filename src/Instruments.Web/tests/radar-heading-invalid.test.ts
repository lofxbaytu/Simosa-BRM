// 航向無效(電羅經失效,E08/E09)時 PPI 為艏向上、影像未穩定化:畫面正上方 = 船首、方位皆為相對。
// 天線偏移、EBL/PI 的 R 參考、點選換算都必須以「畫面參考航向」0 計算,不可再加核心保留的最後航向值
// (審查發現:保留航向 090° 時 EBL 000.0° R 畫到正橫、點選船首讀數 270.0° R、影像中心偏到左方)。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { PpiRenderer, type PpiDrawInput } from '../src/radar/ppi-renderer.js';
import { RadarStore } from '../src/radar/radar-store.js';
import { encodeSpokeFrame, parseSpokeFrame, FLAG_TRANSMITTING } from '../src/radar/frame.js';
import { parseStatus } from '../src/radar/protocol.js';
import { defaultSettings } from '../src/radar/ppi-model.js';
import { screenHeading, type PpiView } from '../src/radar/geometry.js';

const M_PER_NM = 1852;
const HELD_HDG = 90; // 核心保留的最後指示航向

type Call = { name: string; args: number[] };

function fakeCtx(calls: Call[]): CanvasRenderingContext2D {
  return new Proxy({} as Record<string | symbol, unknown>, {
    get: (_t, p) => {
      if (p === 'measureText') return () => ({ width: 0 });
      return (...args: unknown[]) => {
        calls.push({ name: String(p), args: args.map((a) => (typeof a === 'number' ? a : Number.NaN)) });
      };
    },
    set: () => true,
  }) as unknown as CanvasRenderingContext2D;
}

function textCtx(texts: string[]): CanvasRenderingContext2D {
  return new Proxy({} as Record<string | symbol, unknown>, {
    get: (_t, p) => {
      if (p === 'measureText') return () => ({ width: 0 });
      if (p === 'fillText') return (s: string) => texts.push(s);
      return () => undefined;
    },
    set: () => true,
  }) as unknown as CanvasRenderingContext2D;
}

const view: PpiView = { cx: 400, cy: 400, radiusPx: 370, rangeNm: 0.25, rotationDeg: 0, ownX: 400, ownY: 400 };

function input(over: Partial<PpiDrawInput>): PpiDrawInput {
  return {
    settings: { ...defaultSettings(), orientation: 'head-up' },
    view,
    motion: 'relative',
    headingDeg: HELD_HDG,
    headingValid: false,
    own: { x: 0, y: 0, heading: HELD_HDG, cog: HELD_HDG, sog: 0, valid: true },
    status: null,
    tracks: [],
    tracksT: 0,
    tNow: 0,
    ownHistory: [],
    selectedId: null,
    acknowledged: new Set(),
    blinkOn: true,
    cursor: null,
    transmitting: true,
    trial: undefined,
    dpr: 1,
    ...over,
  };
}

describe('screenHeading', () => {
  it('航向有效時為航向,無效時為 0(相對座標)', () => {
    expect(screenHeading(HELD_HDG, true)).toBe(HELD_HDG);
    expect(screenHeading(HELD_HDG, false)).toBe(0);
  });
});

describe('PpiRenderer:航向無效時的幾何', () => {
  it('影像中心(天線)在 CCRP 正後方,不依保留航向旋轉', () => {
    const calls: Call[] = [];
    const ctx = fakeCtx(calls);
    const r = new PpiRenderer({ width: 800, height: 800, getContext: () => ctx } as unknown as HTMLCanvasElement);
    const f = parseSpokeFrame(
      encodeSpokeFrame({
        radarId: 'X', flags: FLAG_TRANSMITTING, scan: 0, firstSpoke: 0, spokeCount: 1, spokesPerRev: 2048, cells: 512,
        rangeM: 0.25 * M_PER_NM, headingDeg: HELD_HDG, cogDeg: HELD_HDG, t: 1,
      }),
    );
    r.pushFrame(f);
    (r as unknown as { videoCanvas: object }).videoCanvas = {}; // 測試環境的畫布替身
    const status = parseStatus({ range_nm: 0.25, antennaFwd_m: -31.65, antennaStbd_m: 0 })!;
    (r as unknown as { drawVideo(c: CanvasRenderingContext2D, i: PpiDrawInput): void }).drawVideo(ctx, input({ status }));
    const tr = calls.find((c) => c.name === 'translate')!;
    const k = 370 / (0.25 * M_PER_NM);
    expect(tr.args[0]).toBeCloseTo(400, 0);
    expect(tr.args[1]).toBeCloseTo(400 + 31.65 * k, 0);
  });

  it('EBL 000.0° R 與艏向線同方向(畫面正上方)', () => {
    const calls: Call[] = [];
    const ctx = fakeCtx(calls);
    const r = new PpiRenderer({ width: 800, height: 800, getContext: () => ctx } as unknown as HTMLCanvasElement);
    const settings = { ...defaultSettings(), orientation: 'head-up' as const };
    settings.ebl = [{ on: true, bearing: 0, ref: 'R' }, { ...settings.ebl[1], on: false }];
    (r as unknown as { drawEblVrm(c: CanvasRenderingContext2D, i: PpiDrawInput): void }).drawEblVrm(ctx, input({ settings }));
    const to = calls.find((c) => c.name === 'lineTo')!;
    expect(to.args[0]).toBeCloseTo(400, 3);
    expect(to.args[1]).toBeLessThan(400 - 300);
  });

  it('PI 000° R(偏移 0)沿畫面正上方', () => {
    const calls: Call[] = [];
    const ctx = fakeCtx(calls);
    const r = new PpiRenderer({ width: 800, height: 800, getContext: () => ctx } as unknown as HTMLCanvasElement);
    const settings = { ...defaultSettings(), orientation: 'head-up' as const };
    settings.pi = [{ on: true, bearing: 0, offsetNm: 0, ref: 'R' }, { ...settings.pi[1], on: false }];
    (r as unknown as { drawPi(c: CanvasRenderingContext2D, i: PpiDrawInput): void }).drawPi(ctx, input({ settings }));
    const from = calls.find((c) => c.name === 'moveTo')!;
    const to = calls.find((c) => c.name === 'lineTo')!;
    expect(from.args[0]).toBeCloseTo(400, 3);
    expect(to.args[0]).toBeCloseTo(400, 3);
  });

  it('游標讀數標示相對方位(R)', () => {
    const texts: string[] = [];
    const ctx = textCtx(texts);
    const r = new PpiRenderer({ width: 800, height: 800, getContext: () => ctx } as unknown as HTMLCanvasElement);
    (r as unknown as { drawCornerText(c: CanvasRenderingContext2D, i: PpiDrawInput): void }).drawCornerText(
      ctx,
      input({ cursor: { x: 400, y: 300 } }),
    );
    expect(texts.find((t) => t.startsWith('游標'))).toMatch(/^游標 000\.0° R/);
  });
});

describe('<radar-ppi> 點選:航向無效時', () => {
  beforeAll(async () => {
    await import('../src/radar/radar-ppi.js');
  });

  function store(): RadarStore {
    const s = new RadarStore();
    // 影像幀:航向無效(flags 無 bit1)、保留航向 090
    s.handleFrame(
      parseSpokeFrame(
        encodeSpokeFrame({
          radarId: 'X', flags: FLAG_TRANSMITTING, scan: 0, firstSpoke: 0, spokeCount: 1, spokesPerRev: 2048, cells: 64,
          rangeM: 3 * M_PER_NM, headingDeg: HELD_HDG, cogDeg: HELD_HDG, t: 1,
        }),
      ),
    );
    s.setSettings({ orientation: 'head-up', displayRangeNm: 3 });
    return s;
  }

  it('EBL(R)點選艏向線正上方 → 000.0° R;PI(R 000)點選右方 1 nm → 偏移 +1 nm', async () => {
    const s = store();
    expect(s.headingValid).toBe(false);
    expect(s.screenHeadingDeg).toBe(0);
    const el = document.createElement('radar-ppi') as HTMLElement & {
      store: RadarStore;
      updateComplete: Promise<unknown>;
      clickAt(x: number, y: number): void;
      computeView(): PpiView;
    };
    el.store = s;
    document.body.appendChild(el);
    await el.updateComplete;
    const v = el.computeView();
    expect(v.rotationDeg).toBe(0);
    const ebl = [...s.settings.ebl] as typeof s.settings.ebl;
    ebl[0] = { on: false, bearing: 45, ref: 'R' };
    s.setSettings({ ebl, tool: 'ebl1' });
    el.clickAt(v.ownX, v.ownY - v.radiusPx * 0.5);
    expect(s.settings.ebl[0].bearing).toBeCloseTo(0, 6);
    const pi = [...s.settings.pi] as typeof s.settings.pi;
    pi[0] = { on: false, bearing: 0, offsetNm: 0, ref: 'R' };
    s.setSettings({ pi, tool: 'pi1' });
    el.clickAt(v.ownX + v.radiusPx / 3, v.ownY); // 3 nm 量程 → 1 nm
    expect(s.settings.pi[0].offsetNm).toBeCloseTo(1, 2);
    el.remove();
  });
});
