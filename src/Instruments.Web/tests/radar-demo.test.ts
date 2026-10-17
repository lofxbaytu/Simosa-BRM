// 示範模式與頁面狀態:合成影像幀格式(≤ 32 線、不跨 2047→0、每圈 2.5 s)、確定性、假追蹤目標(T1 危險)、指令(量程/擷取/待機/試操船),
// RadarStore(量程同步、偏心加大影像量程、警示確認)、PPI 模型(真運動錨點、影像量程、點選)。
import { describe, expect, it } from 'vitest';
import { DemoRadarSource } from '../src/radar/demo-radar.js';
import type { SpokeFrame } from '../src/radar/frame.js';
import type { RadarMessage, RadarTracks } from '../src/radar/protocol.js';
import { RadarStore } from '../src/radar/radar-store.js';
import {
  computeView,
  defaultSettings,
  extrapolateOwn,
  hitTestTarget,
  updateTmAnchor,
  videoRangeFor,
  type OwnNow,
} from '../src/radar/ppi-model.js';

function collect(src: DemoRadarSource) {
  const frames: SpokeFrame[] = [];
  const msgs: RadarMessage[] = [];
  src.onFrame((f) => frames.push({ ...f, data: f.data.slice() }));
  src.onMessage((m) => msgs.push(m));
  return { frames, msgs };
}

const flush = () => new Promise<void>((r) => setTimeout(r, 0));

describe('示範雷達', () => {
  it('影像幀:每幀 ≤ 32 線、不跨 2047→0、2.5 s 一圈(24 rpm)', () => {
    const src = new DemoRadarSource({ autoTick: false });
    const { frames } = collect(src);
    src.start();
    for (let i = 0; i < 63; i++) src.tick(0.04);
    const spokes = frames.reduce((n, f) => n + f.spokeCount, 0);
    expect(spokes).toBe(Math.floor((2.52 * 24 * 2048) / 60));
    for (const f of frames) {
      expect(f.spokeCount).toBeLessThanOrEqual(32);
      expect(f.firstSpoke + f.spokeCount).toBeLessThanOrEqual(2048);
      expect(f.cells).toBe(512);
      expect(f.transmitting && f.headingValid).toBe(true);
    }
    expect(frames.some((f) => f.scan === 1)).toBe(true);
    src.stop();
  });

  it('確定性:同種子同時間序列 → 相同影像;換種子不同', () => {
    const run = (seed: number) => {
      const s = new DemoRadarSource({ autoTick: false, seed });
      const { frames } = collect(s);
      s.start();
      for (let i = 0; i < 25; i++) s.tick(0.04);
      return frames.flatMap((f) => Array.from(f.data));
    };
    const a = run(1);
    expect(run(1)).toEqual(a);
    expect(run(2)).not.toEqual(a);
  });

  it('影像含目標回波:T1 方位方向的距離格有強回波', () => {
    const src = new DemoRadarSource({ autoTick: false });
    const { frames } = collect(src);
    src.start();
    for (let i = 0; i < 63; i++) src.tick(0.04);
    // T1:真方位 075、3.6 nm;航向約 045 → 相對約 30°
    const own = src.ownAt(0);
    const rel = (75 - own.heading + 360) % 360;
    const cell = Math.round((3.6 / 6) * 512);
    let max = 0;
    for (const f of frames) {
      for (let j = 0; j < f.spokeCount; j++) {
        const b = ((f.firstSpoke + j) * 360) / 2048;
        if (Math.abs(b - rel) > 2) continue;
        for (let i = cell - 4; i <= cell + 6; i++) max = Math.max(max, f.data[j * 512 + i]!);
      }
    }
    expect(max).toBeGreaterThan(200);
  });

  it('假追蹤目標:2 個追蹤中,T1 為危險目標(CPA < 1 nm、TCPA ≤ 12 分)', () => {
    const src = new DemoRadarSource({ autoTick: false });
    const { msgs } = collect(src);
    src.start();
    src.tick(1.01);
    const tracks = msgs.filter((m): m is RadarTracks => m.type === 'tracks').at(-1)!;
    expect(tracks.targets).toHaveLength(2);
    const t1 = tracks.targets.find((t) => t.id === 1)!;
    expect(t1.state).toBe('tracking');
    expect(t1.cpa_nm!).toBeLessThan(1);
    expect(t1.tcpa_min!).toBeGreaterThan(0);
    expect(t1.tcpa_min!).toBeLessThanOrEqual(12);
    expect(t1.dangerous).toBe(true);
    expect(t1.past.length).toBeGreaterThan(0);
    const t2 = tracks.targets.find((t) => t.id === 2)!;
    expect(t2.dangerous).toBe(false);
    expect(msgs.some((m) => m.type === 'event' && m.event.kind === 'dangerousTarget' && m.event.targetId === 1)).toBe(true);
  });

  it('指令:量程換成標準量程、手動擷取 T3、待機停止影像並清除追蹤、試操船給 trialCpa', async () => {
    const src = new DemoRadarSource({ autoTick: false });
    const { frames, msgs } = collect(src);
    src.start();
    const rid = src.send({ cmd: 'range', value: 5 });
    await flush();
    expect(msgs).toContainEqual(expect.objectContaining({ type: 'ack', cmd: 'range', ok: true, value: 6, reqId: rid }));
    // T3:真方位 320、2.0 nm
    src.send({ cmd: 'acquire', bearing: 320, range: 2.0 });
    await flush();
    const ack = msgs.find((m) => m.type === 'ack' && m.cmd === 'acquire');
    expect(ack).toMatchObject({ ok: true, id: 3 });
    src.tick(1.0);
    let tr = msgs.filter((m): m is RadarTracks => m.type === 'tracks').at(-1)!;
    expect(tr.targets.find((t) => t.id === 3)?.state).toBe('acquiring');
    for (let i = 0; i < 31; i++) src.tick(1);
    tr = msgs.filter((m): m is RadarTracks => m.type === 'tracks').at(-1)!;
    expect(tr.targets.find((t) => t.id === 3)?.state).toBe('tracking');
    src.send({ cmd: 'trialManoeuvre', course: 120, speed: 10, delay: 1 });
    await flush();
    tr = msgs.filter((m): m is RadarTracks => m.type === 'tracks').at(-1)!;
    expect(tr.trial).toEqual({ course: 120, speed: 10, delay_min: 1 });
    expect(tr.targets.every((t) => t.state !== 'tracking' || t.trialCpa_nm !== undefined)).toBe(true);
    src.send({ cmd: 'standby' });
    await flush();
    const n = frames.length;
    src.tick(1);
    expect(frames.length).toBe(n);
    tr = msgs.filter((m): m is RadarTracks => m.type === 'tracks').at(-1)!;
    expect(tr.targets).toHaveLength(0);
    expect(tr.transmitting).toBe(false);
    // 空白處擷取失敗
    src.send({ cmd: 'transmit' });
    src.send({ cmd: 'acquire', bearing: 200, range: 5 });
    await flush();
    src.tick(8);
    expect(msgs.some((m) => m.type === 'event' && m.event.kind === 'acquireFailed')).toBe(true);
  });

  it('保護區:啟用後未追蹤的 T3 被自動擷取(newTarget)', async () => {
    const src = new DemoRadarSource({ autoTick: false });
    const { msgs } = collect(src);
    src.start();
    src.send({ cmd: 'guardZone', enabled: true, inner_nm: 0.5, outer_nm: 2.5, from: 0, to: 360, relative: true });
    await flush();
    src.tick(0.5);
    expect(msgs.some((m) => m.type === 'event' && m.event.kind === 'newTarget')).toBe(true);
  });
});

describe('RadarStore 與 PPI 模型', () => {
  it('第一次 status 決定顯示量程;偏心時要求較大的影像量程,伺服器回報後顯示量程不變', async () => {
    const src = new DemoRadarSource({ autoTick: false });
    const store = new RadarStore();
    store.attach(src);
    src.start();
    expect(store.status?.range_nm).toBe(6);
    expect(store.settings.displayRangeNm).toBe(6);
    store.setSettings({ offcentre: { x: 0, y: 0.5 } });
    await flush();
    expect(store.status?.range_nm).toBe(12);
    expect(store.settings.displayRangeNm).toBe(6);
    store.setSettings({ offcentre: { x: 0, y: 0 } });
    await flush();
    expect(store.status?.range_nm).toBe(6);
    // 其他客戶端改量程 → 顯示量程跟隨
    src.send({ cmd: 'range', value: 3 });
    await flush();
    expect(store.settings.displayRangeNm).toBe(3);
  });

  it('擷取成功的 ack 自動選取新目標;危險警示確認後不再待確認', async () => {
    const src = new DemoRadarSource({ autoTick: false });
    const store = new RadarStore();
    store.attach(src);
    src.start();
    src.tick(1.01);
    await flush();
    expect(store.pendingAlarms()).toEqual(['danger:1']);
    store.acknowledgeAlarms();
    expect(store.pendingAlarms()).toEqual([]);
    store.send({ cmd: 'acquire', bearing: 320, range: 2 });
    await flush();
    expect(store.selectedId).toBe(3);
    expect(store.log.some((e) => e.kind === 'acquireStarted')).toBe(true);
    store.send({ cmd: 'cancel', id: 42 });
    await flush();
    expect(store.lastError).toMatch(/cancel/);
  });

  it('真運動:錨點在航向反側 0.6 R,自船隨位移移動,超過 0.7 R 重設', () => {
    const s = { ...defaultSettings(), motion: 'true' as const, orientation: 'north-up' as const, displayRangeNm: 1 };
    const own0: OwnNow = { x: 0, y: 0, heading: 0, cog: 0, sog: 10, valid: true };
    const base = { width: 600, height: 600, marginPx: 50, settings: s, headingDeg: 0, headingValid: true };
    const v0 = computeView({ ...base, own: own0, tm: null });
    const tm0 = updateTmAnchor(null, v0, own0, true)!;
    expect(tm0.fy).toBeCloseTo(0.6, 9);
    const v1 = computeView({ ...base, own: own0, tm: tm0 });
    expect(v1.ownY).toBeCloseTo(300 + 0.6 * 250, 6);
    // 向北 0.5 nm → 自船上移 0.5 R
    const own1 = { ...own0, y: 926 };
    const v2 = computeView({ ...base, own: own1, tm: tm0 });
    expect(v2.ownY).toBeCloseTo(300 + 0.1 * 250, 6);
    expect(updateTmAnchor(tm0, v2, own1, true)).toBe(tm0);
    // 再向北 0.8 nm → 超出 0.7 R → 重設
    const own2 = { ...own0, y: 926 + 1482 };
    const v3 = computeView({ ...base, own: own2, tm: tm0 });
    const tm1 = updateTmAnchor(tm0, v3, own2, true)!;
    expect(tm1).not.toBe(tm0);
    expect(tm1.fy).toBeCloseTo(0.6, 9);
    expect(updateTmAnchor(tm1, v3, own2, false)).toBeNull();
  });

  it('自船外推、影像量程、點選目標', () => {
    const o = extrapolateOwn({ x: 0, y: 0, lat: 0, lon: 0, heading: 90, cog: 90, sog: 10 }, 10, 11, 91);
    expect(o.x).toBeCloseTo(1852 / 360, 6);
    expect(o.heading).toBe(91);
    expect(extrapolateOwn(undefined, 0, 1, 5).valid).toBe(false);
    expect(videoRangeFor(6, 0)).toBe(6);
    expect(videoRangeFor(6, 0.5)).toBe(12);
    expect(videoRangeFor(0.75, 0.66)).toBe(1.5);
    expect(videoRangeFor(96, 0.5)).toBe(96);
    expect(hitTestTarget([{ id: 1, x: 10, y: 10 }, { id: 2, x: 30, y: 10 }], 26, 11, 10)).toBe(2);
    expect(hitTestTarget([{ id: 1, x: 10, y: 10 }], 60, 60, 10)).toBeNull();
  });
});
