// 純 AIS 目標(tracks.aisTargets[];radar.schema.json aisTarget;docs/design/radar-arpa.md 第 3.2 節):解析、PPI 三角符號與點選擷取、
// 目標資料面板的 AIS 資料與「擷取」鈕、示範模式產生純 AIS 目標。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { parseRadarMessage, type AisTarget, type RadarCommand, type RadarTracks } from '../src/radar/protocol.js';
import { DemoRadarSource } from '../src/radar/demo-radar.js';
import { RadarStore } from '../src/radar/radar-store.js';
import { PpiRenderer, type PpiDrawInput } from '../src/radar/ppi-renderer.js';
import { defaultSettings } from '../src/radar/ppi-model.js';
import { polarToScreen, type PpiView } from '../src/radar/geometry.js';

const flush = () => new Promise<void>((r) => setTimeout(r, 0));

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}): Promise<T> {
  const el = document.createElement(tag) as T & { updateComplete: Promise<unknown> };
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

const text = (el: Element) => (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');

function ais(over: Partial<AisTarget> = {}): AisTarget {
  return { mmsi: 416001009, name: 'CHARLIE', x: 0, y: 3704, lat: 22.53, lon: 120.24, range_nm: 2, bearing_deg: 0, relBearing_deg: 0, cog_deg: 180, sog_kn: 8, heading_deg: 180, cpa_nm: 0.1, tcpa_min: 6, dangerous: true, ...over };
}

function tracksMsg(aisTargets: AisTarget[] | undefined, t = 10): RadarTracks {
  const m: RadarTracks = { type: 'tracks', radar: 'x', t, scan: 1, transmitting: true, ownShip: { x: 0, y: 0, lat: 22.5, lon: 120.2, heading: 0, cog: 0, sog: 8 }, cpaLimit_nm: 1, tcpaLimit_min: 12, targets: [] };
  if (aisTargets) m.aisTargets = aisTargets;
  return m;
}

beforeAll(async () => {
  await import('../src/radar/radar-arpa-panels.js');
  await import('../src/radar/radar-ppi.js');
});

describe('解析', () => {
  it('tracks.aisTargets[] 解析;缺 mmsi/位置者略過;無欄位時 aisTargets 省略', () => {
    const m = parseRadarMessage({
      type: 'tracks', radar: 'x', t: 10, scan: 4, transmitting: true, ownShip: { x: 0, y: 0, lat: 22.5, lon: 120.2, heading: 0, cog: 0, sog: 8 }, cpaLimit_nm: 1, tcpaLimit_min: 12, targets: [],
      aisTargets: [
        { mmsi: 416001001, name: 'ALFA', x: 100, y: 200, lat: 22.5, lon: 120.2, range_nm: 1.2, bearing_deg: 26.6, relBearing_deg: 26.6, cog_deg: 250, sog_kn: 10, heading_deg: 251, cpa_nm: 0.8, tcpa_min: 5, dangerous: true, candidateTrackId: 3 },
        { name: 'NO-MMSI', x: 1, y: 2, range_nm: 1, bearing_deg: 0 },
      ],
    }) as RadarTracks;
    expect(m.aisTargets).toHaveLength(1);
    expect(m.aisTargets![0]).toEqual({ mmsi: 416001001, name: 'ALFA', x: 100, y: 200, lat: 22.5, lon: 120.2, range_nm: 1.2, bearing_deg: 26.6, relBearing_deg: 26.6, cog_deg: 250, sog_kn: 10, heading_deg: 251, cpa_nm: 0.8, tcpa_min: 5, dangerous: true, candidateTrackId: 3 });
    const none = parseRadarMessage(tracksMsg(undefined)) as RadarTracks;
    expect(none.aisTargets).toBeUndefined();
  });
});

describe('store 與目標資料面板', () => {
  it('選取純 AIS 目標與 ARPA 目標互斥;目標消失時清除;acquireAis 以 AIS 方位/距離送 acquire(航向無效時相對方位)', () => {
    const store = new RadarStore();
    const sent: RadarCommand[] = [];
    store.source = { url: '', radar: 'x', status: 'connected', start() {}, stop() {}, send: (c) => (sent.push(c), 'r1'), onFrame: () => () => {}, onMessage: () => () => {}, onConn: () => () => {} };
    store.handleMessage(tracksMsg([ais({ bearing_deg: 45.26, range_nm: 2.0004, relBearing_deg: 30.3 })]));
    expect(store.aisTargets).toHaveLength(1);
    store.selectAis(416001009);
    expect(store.selectedAisMmsi).toBe(416001009);
    expect(store.selectedId).toBeNull();
    store.select(7);
    expect(store.selectedAisMmsi).toBeNull();
    store.selectAis(416001009);
    expect(store.acquireAis(416001009)).toBe('r1');
    expect(sent.at(-1)).toEqual({ cmd: 'acquire', bearing: 45.3, range: 2 });
    (store as unknown as { status: { headingValid: boolean } }).status = { headingValid: false };
    store.acquireAis(416001009);
    expect(sent.at(-1)).toEqual({ cmd: 'acquire', bearing: 30.3, range: 2, relative: true });
    expect(store.acquireAis(1)).toBeNull();
    store.handleMessage(tracksMsg([]));
    expect(store.selectedAisMmsi).toBeNull();
  });

  it('目標資料面板:選取純 AIS 目標顯示 MMSI/COG/SOG/CPA 與「擷取此目標」鈕;未選取時提示純 AIS 目標數', async () => {
    const store = new RadarStore();
    const sent: RadarCommand[] = [];
    store.source = { url: '', radar: 'x', status: 'connected', start() {}, stop() {}, send: (c) => (sent.push(c), 'r1'), onFrame: () => () => {}, onMessage: () => () => {}, onConn: () => () => {} };
    store.handleMessage(tracksMsg([ais()]));
    const el = await mount('radar-target-data', { store });
    expect(text(el)).toContain('純 AIS 目標 1');
    store.selectAis(416001009);
    await flush();
    await (el as HTMLElement & { updateComplete: Promise<unknown> }).updateComplete;
    const t = text(el);
    expect(t).toContain('AIS CHARLIE');
    expect(t).toContain('416001009');
    expect(t).toContain('180.0° T');
    expect(t).toContain('0.10 NM');
    expect(t).toContain('危險');
    el.shadowRoot!.querySelector<HTMLButtonElement>('button.acquire-ais')!.click();
    expect(sent.at(-1)).toEqual({ cmd: 'acquire', bearing: 0, range: 2 });
  });
});

describe('PPI 符號與點選', () => {
  it('畫出三角符號(每目標一個 closePath 三角形)、危險者警報色;aisPoints 供點選', () => {
    const calls: string[] = [];
    const ctx = new Proxy({} as Record<string | symbol, unknown>, {
      get: (_t, p) => (p === 'measureText' ? () => ({ width: 0 }) : (...a: unknown[]) => calls.push(`${String(p)}(${a.join(',')})`)),
      set: () => true,
    }) as unknown as CanvasRenderingContext2D;
    const r = new PpiRenderer({ width: 800, height: 800, getContext: () => ctx } as unknown as HTMLCanvasElement);
    const view: PpiView = { cx: 400, cy: 400, radiusPx: 370, rangeNm: 3, rotationDeg: 0, ownX: 400, ownY: 400 };
    const inp: PpiDrawInput = {
      settings: defaultSettings(), view, motion: 'relative', headingDeg: 0, headingValid: true,
      own: { x: 0, y: 0, heading: 0, cog: 0, sog: 8, valid: true }, status: null, tracks: [], aisTargets: [ais(), ais({ mmsi: 2, name: 'DELTA', x: 1852, y: 0, bearing_deg: 90, range_nm: 1, dangerous: false })], selectedAisMmsi: 2,
      tracksT: 10, tNow: 10, ownHistory: [], selectedId: null, acknowledged: new Set(), blinkOn: true, cursor: null, transmitting: true, trial: undefined, dpr: 1,
    };
    (r as unknown as { drawAisTargets(c: CanvasRenderingContext2D, i: PpiDrawInput): void }).drawAisTargets(ctx, inp);
    expect(calls.filter((c) => c === 'closePath()')).toHaveLength(2);
    expect(calls.some((c) => c.startsWith('fillText(CHARLIE'))).toBe(true);
    expect(calls.some((c) => c.startsWith('strokeRect('))).toBe(true); // 選取方框
    expect(r.aisPoints.map((p) => p.id)).toEqual([416001009, 2]);
    const k = 370 / (3 * 1852);
    expect(r.aisPoints[0]!.y).toBeCloseTo(400 - 3704 * k, 1);
    expect(r.aisPoints[1]!.x).toBeCloseTo(400 + 1852 * k, 1);
  });

  it('點選工具:點在 AIS 符號上選取純 AIS 目標;擷取工具點在符號上以 AIS 方位/距離擷取', async () => {
    const src = new DemoRadarSource({ autoTick: false });
    const sent: RadarCommand[] = [];
    const orig = src.send.bind(src);
    src.send = (c: RadarCommand) => {
      sent.push(c);
      return orig(c);
    };
    const store = new RadarStore();
    store.attach(src);
    src.start();
    src.tick(1.01);
    await flush();
    // 示範:T1/T2 有 AIS 且尚未追蹤 → 純 AIS 目標
    expect(store.aisTargets.map((a) => a.mmsi)).toEqual([416001001, 416001002]);
    const ppi = (await mount('radar-ppi', { store })) as HTMLElement & { clickAt(x: number, y: number): void; computeView(): PpiView; drawNow(): void };
    ppi.drawNow();
    const v = ppi.computeView();
    const t1 = store.aisTargets[0]!;
    const p = polarToScreen(v, t1.bearing_deg, t1.range_nm);
    store.setSettings({ tool: 'select' });
    ppi.clickAt(p.x, p.y);
    expect(store.selectedAisMmsi).toBe(416001001);
    expect(store.selectedId).toBeNull();
    store.setSettings({ tool: 'acquire' });
    ppi.clickAt(p.x, p.y);
    const acq = sent.at(-1) as { cmd: string; bearing: number; range: number };
    expect(acq.cmd).toBe('acquire');
    expect(acq.bearing).toBeCloseTo(Math.round(t1.bearing_deg * 10) / 10, 6);
    expect(acq.range).toBeCloseTo(Math.round(t1.range_nm * 1000) / 1000, 6);
    // 示範:擷取並關聯後 T1 離開純 AIS 清單(T2 位置差大,仍為純 AIS 且附候選編號)
    src.tick(60);
    const ids = store.aisTargets.map((a) => a.mmsi);
    expect(ids).not.toContain(416001001);
    ppi.remove();
  });
});
