// RadarStore:時間倒退時自船歷史維持單調(相對運動過去位置以正確時間軸換算);
// 試操船以最新的 status/tracks 為準(情境切換後不沿用舊 status.trial)。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { RadarStore } from '../src/radar/radar-store.js';
import { PpiRenderer, type PpiDrawInput } from '../src/radar/ppi-renderer.js';
import { defaultSettings } from '../src/radar/ppi-model.js';
import { parseRadarMessage, parseTrack, type RadarTracks } from '../src/radar/protocol.js';

function tracks(t: number, x: number, extra: Partial<RadarTracks> = {}): RadarTracks {
  return {
    type: 'tracks', radar: 'x', t, scan: 0, transmitting: true,
    ownShip: { x, y: 0, lat: 0, lon: 0, heading: 0, cog: 90, sog: 0 },
    cpaLimit_nm: 1, tcpaLimit_min: 12, targets: [],
    ...extra,
  };
}

describe('自船歷史:時間倒退', () => {
  it('倒帶到歷史視窗內的時間 → 丟棄 t ≥ 新時間的舊點,歷史維持單調', () => {
    const s = new RadarStore();
    for (let t = 0; t <= 300; t++) s.handleMessage(tracks(t, 0)); // 舊時間軸:自船停在 x = 0
    for (let t = 100; t <= 160; t++) s.handleMessage(tracks(t, 500)); // 倒帶到 100 後:自船在 x = 500
    const ts = s.ownHistory.map((h) => h.t);
    expect(ts.every((v, i) => i === 0 || v > ts[i - 1]!)).toBe(true);
    expect(ts.at(-1)).toBe(160);
    expect(s.ownHistory.find((h) => h.t === 130)!.x).toBe(500);
    expect(s.ownHistory.filter((h) => h.t >= 100).every((h) => h.x === 500)).toBe(true);
  });

  it('相對運動過去位置以新時間軸的自船位置換算', () => {
    const s = new RadarStore();
    for (let t = 0; t <= 300; t++) s.handleMessage(tracks(t, 0));
    for (let t = 100; t <= 160; t++) s.handleMessage(tracks(t, 500));
    const track = parseTrack({
      id: 1, state: 'tracking', x: 1500, y: 0, range_nm: 1000 / 1852, bearing_deg: 90,
      course_deg: 0, speed_kn: 0, past: [{ t: 130, x: 1500, y: 0 }],
    })!;
    const arcs: number[][] = [];
    const ctx = new Proxy({} as Record<string | symbol, unknown>, {
      get: (_t, p) => (p === 'arc' ? (...a: number[]) => arcs.push(a) : p === 'measureText' ? () => ({ width: 0 }) : () => undefined),
      set: () => true,
    }) as unknown as CanvasRenderingContext2D;
    const r = new PpiRenderer({ width: 800, height: 800, getContext: () => ctx } as unknown as HTMLCanvasElement);
    const view = { cx: 400, cy: 400, radiusPx: 370, rangeNm: 1.5, rotationDeg: 0, ownX: 400, ownY: 400 };
    const inp: PpiDrawInput = {
      settings: defaultSettings(), view, motion: 'relative', headingDeg: 0, headingValid: true,
      own: { x: 500, y: 0, heading: 0, cog: 90, sog: 0, valid: true }, status: null, tracks: [track], tracksT: 160, tNow: 160,
      ownHistory: s.ownHistory, selectedId: null, acknowledged: new Set(), blinkOn: true, cursor: null, transmitting: true,
      trial: undefined, dpr: 1,
    };
    (r as unknown as { drawTargets(c: CanvasRenderingContext2D, i: PpiDrawInput): void }).drawTargets(ctx, inp);
    const k = 370 / (1.5 * 1852);
    const pastDot = arcs.find((a) => Math.abs(a[2]! - 1.8) < 1e-9)!;
    expect(pastDot[0]).toBeCloseTo(400 + 1000 * k, 1); // 相對 +1000 m(不是舊時間軸的 +1500 m)
  });
});

describe('試操船設定:以最新訊息為準', () => {
  beforeAll(async () => {
    await import('../src/radar/radar-arpa-panels.js');
  });

  const statusWithTrial = {
    type: 'status', radar: 'x', range_nm: 6, trial: { course: 90, speed: 8, delay_min: 1 }, cpaLimit_nm: 0.5, tcpaLimit_min: 6,
  };

  it('情境切換後 tracks 不含 trial → 不沿用舊 status.trial', async () => {
    const s = new RadarStore();
    s.handleMessage(parseRadarMessage(statusWithTrial)!);
    expect(s.trial).toEqual({ course: 90, speed: 8, delay_min: 1 });
    s.handleMessage(tracks(3, 0));
    expect(s.trial).toBeUndefined();
    await new Promise((r) => setTimeout(r, 0));
    const el = document.createElement('radar-trial-panel') as HTMLElement & { store: RadarStore; updateComplete: Promise<unknown> };
    el.store = s;
    document.body.appendChild(el);
    await el.updateComplete;
    const txt = (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');
    expect(txt).not.toContain('TRIAL 090.0°');
    expect(txt).toContain('啟用試操船');
    el.remove();
  });

  it('啟用試操船後的 status 先到(tracks 尚未更新)→ 立即顯示;之後 tracks 帶 trial 仍維持', () => {
    const s = new RadarStore();
    s.handleMessage(tracks(1, 0));
    s.handleMessage(parseRadarMessage(statusWithTrial)!);
    expect(s.trial?.course).toBe(90);
    s.handleMessage(tracks(2, 0, { trial: { course: 90, speed: 8, delay_min: 1 } }));
    expect(s.trial?.course).toBe(90);
    // 關閉:status 不含 trial
    s.handleMessage(parseRadarMessage({ ...statusWithTrial, trial: undefined })!);
    expect(s.trial).toBeUndefined();
  });
});
