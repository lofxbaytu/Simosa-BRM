// ARPA 顯示邏輯:目標表排序、符號狀態(危險閃爍/確認、失追)、向量端點、CPA/TCPA 與試操船 CPA、協定解析與量程。
import { describe, expect, it } from 'vitest';
import {
  computeCpa,
  dangerKey,
  formatTcpa,
  isDangerous,
  lostKey,
  pendingAlarms,
  pruneAcknowledged,
  sortTracks,
  symbolStyle,
  trialCpa,
  vectorEnu,
  velocityEnu,
} from '../src/radar/arpa.js';
import {
  nearestRange,
  parseRadarMessage,
  rangeAtLeast,
  ringInterval,
  serializeRadarCommand,
  stepRange,
  type RadarTrack,
} from '../src/radar/protocol.js';

function track(id: number, p: Partial<RadarTrack> = {}): RadarTrack {
  return {
    id,
    label: String(id).padStart(2, '0'),
    state: 'tracking',
    acquisition: 'auto',
    age_s: 100,
    stable: false,
    manoeuvring: false,
    dangerous: false,
    swapRisk: false,
    misses: 0,
    x: 0,
    y: 0,
    lat: 0,
    lon: 0,
    range_nm: 3,
    bearing_deg: 0,
    relBearing_deg: 0,
    past: [],
    ...p,
  };
}

describe('ARPA 表排序', () => {
  const list = [
    track(1, { cpa_nm: 2.0, tcpa_min: 5, range_nm: 4 }),
    track(2, { cpa_nm: 0.4, tcpa_min: 9, range_nm: 6, dangerous: true }),
    track(3, { cpa_nm: 0.1, tcpa_min: -3, range_nm: 1 }), // 已通過
    track(4, { state: 'acquiring', range_nm: 2 }),
    track(5, { cpa_nm: 0.9, tcpa_min: 20, range_nm: 8 }),
    track(6, { state: 'lost', range_nm: 0.5 }),
    track(7, { cpa_nm: 0.2, tcpa_min: 3, range_nm: 3, dangerous: true }),
  ];

  it('CPA:危險優先(依 CPA)→ 接近中依 CPA → 已通過 → 擷取中 → 失追', () => {
    expect(sortTracks(list, 'cpa').map((t) => t.id)).toEqual([7, 2, 5, 1, 3, 4, 6]);
  });

  it('TCPA:危險優先(依 TCPA)→ 接近中依 TCPA → 已通過在後', () => {
    expect(sortTracks(list, 'tcpa').map((t) => t.id)).toEqual([7, 2, 1, 5, 3, 4, 6]);
  });

  it('距離與編號排序;不改原陣列', () => {
    expect(sortTracks(list, 'range').map((t) => t.id)).toEqual([6, 3, 4, 7, 1, 2, 5]);
    expect(sortTracks(list, 'id').map((t) => t.id)).toEqual([1, 2, 3, 4, 5, 6, 7]);
    expect(list[0]!.id).toBe(1);
  });

  it('同 CPA 時依 TCPA 再依編號(確定性)', () => {
    const a = [track(9, { cpa_nm: 1, tcpa_min: 4 }), track(8, { cpa_nm: 1, tcpa_min: 4 }), track(10, { cpa_nm: 1, tcpa_min: 2 })];
    expect(sortTracks(a, 'cpa').map((t) => t.id)).toEqual([10, 8, 9]);
  });
});

describe('ARPA 符號', () => {
  it('擷取中/追蹤中/失追;危險未確認時閃爍,確認後恆亮', () => {
    const ack = new Set<string>();
    expect(symbolStyle(track(1, { state: 'acquiring' }), ack, false)).toMatchObject({ kind: 'acquiring', visible: true, vector: false });
    const tr = track(2, { course_deg: 10, speed_kn: 5 });
    expect(symbolStyle(tr, ack, false)).toMatchObject({ kind: 'tracking', danger: false, visible: true, vector: true });
    const d = track(3, { dangerous: true, course_deg: 10, speed_kn: 5 });
    expect(symbolStyle(d, ack, true).visible).toBe(true);
    expect(symbolStyle(d, ack, false)).toMatchObject({ danger: true, visible: false });
    ack.add(dangerKey(3));
    expect(symbolStyle(d, ack, false).visible).toBe(true);
    const l = track(4, { state: 'lost' });
    expect(symbolStyle(l, ack, false)).toMatchObject({ kind: 'lost', visible: false, vector: false });
  });

  it('待確認警示與確認清理(目標不再危險後移除確認,再次危險會重新閃爍)', () => {
    const ack = new Set<string>([dangerKey(1), lostKey(9)]);
    const tracks = [track(1, { dangerous: true }), track(2, { dangerous: true }), track(3, { state: 'lost' })];
    expect(pendingAlarms(tracks, ack)).toEqual([dangerKey(2), lostKey(3)]);
    pruneAcknowledged([track(1, { dangerous: false })], ack);
    expect(ack.size).toBe(0);
  });

  it('真/相對向量端點(6 分鐘、10 kn 向東 = 1 nm)', () => {
    const t = track(1, { course_deg: 90, speed_kn: 10, relCourse_deg: 180, relSpeed_kn: 5 });
    const v = vectorEnu(t, 'true', 6)!;
    expect(v.dE).toBeCloseTo(1852, 6);
    expect(v.dN).toBeCloseTo(0, 6);
    const r = vectorEnu(t, 'relative', 12)!;
    expect(r.dN).toBeCloseTo(-1852, 6);
    expect(vectorEnu(track(2, { state: 'acquiring' }), 'true', 6)).toBeNull();
  });
});

describe('CPA / TCPA', () => {
  it('對遇:相距 2 nm、相對速度 20 kn → TCPA 6 分、CPA = 橫距', () => {
    const own = velocityEnu(0, 10);
    const tgt = velocityEnu(180, 10);
    const r = computeCpa(0.2 * 1852, 2 * 1852, tgt.vE - own.vE, tgt.vN - own.vN);
    expect(r.tcpaMin).toBeCloseTo(6, 6);
    expect(r.cpaNm).toBeCloseTo(0.2, 6);
    expect(isDangerous(r.cpaNm, r.tcpaMin, 1, 12)).toBe(true);
    expect(isDangerous(r.cpaNm, -1, 1, 12)).toBe(false);
  });

  it('已通過:TCPA 為負;相對靜止:TCPA 0、CPA = 目前距離', () => {
    expect(computeCpa(0, -1852, 0, -5).tcpaMin).toBeLessThan(0);
    expect(computeCpa(1852, 0, 0, 0)).toEqual({ cpaNm: 1, tcpaMin: 0 });
  });

  it('試操船:延遲 0 等於以新向量計算;延遲很長等於目前向量', () => {
    const tgt = velocityEnu(270, 12);
    const own = velocityEnu(0, 10);
    const trial = velocityEnu(60, 10);
    const dE = 3 * 1852;
    const dN = 1.5 * 1852;
    const now = computeCpa(dE, dN, tgt.vE - own.vE, tgt.vN - own.vN);
    const after = computeCpa(dE, dN, tgt.vE - trial.vE, tgt.vN - trial.vN);
    const t0 = trialCpa(dE, dN, tgt, own, trial, 0);
    expect(t0.cpaNm).toBeCloseTo(after.cpaNm, 9);
    expect(t0.tcpaMin).toBeCloseTo(after.tcpaMin, 9);
    const tl = trialCpa(dE, dN, tgt, own, trial, 120);
    expect(tl.cpaNm).toBeCloseTo(now.cpaNm, 9);
    expect(tl.tcpaMin).toBeCloseTo(now.tcpaMin, 9);
    // 中間延遲:與暴力取樣一致
    const D = 2 * 60;
    let best = Infinity;
    for (let t = 0; t <= 3600; t += 0.5) {
      const ox = t <= D ? own.vE * t : own.vE * D + trial.vE * (t - D);
      const oy = t <= D ? own.vN * t : own.vN * D + trial.vN * (t - D);
      best = Math.min(best, Math.hypot(dE + tgt.vE * t - ox, dN + tgt.vN * t - oy));
    }
    expect(trialCpa(dE, dN, tgt, own, trial, 2).cpaNm).toBeCloseTo(best / 1852, 4);
  });

  it('TCPA 文字', () => {
    expect(formatTcpa(12)).toBe('12.0');
    expect(formatTcpa(-3.21)).toBe('−3.2');
    expect(formatTcpa(undefined)).toBe('--.-');
  });
});

describe('協定解析與量程', () => {
  it('解析設計文件的 status/tracks/event/ack 範例', () => {
    const st = parseRadarMessage({
      type: 'status', radar: 'x', band: 'X', frequency_GHz: 9.41, transmitting: true, standby: false, failed: false, degraded: false,
      headingValid: true, range_nm: 6, rangeScales: [0.125, 0.25, 0.5, 0.75, 1.5, 3, 6, 12, 24, 48, 96], pulse: 'medium', pulseLength_m: 75,
      gain: 70, sea: 50, rain: 0, tune: 50, autoTune: true, rpm: 24, spokes: 2048, cells: 512, beamwidth_deg: 1, antennaHeight_m: 25.02,
      antennaFwd_m: -31.65, antennaStbd_m: 0, shadowSectors: [{ from: 174, to: 186, attenuation_dB: 60 }],
      guardZone: { enabled: true, inner_nm: 0.3, outer_nm: 8, from: 0, to: 360, relative: true }, cpaLimit_nm: 1, tcpaLimit_min: 12,
      arpaRange_nm: 16, maxTracks: 40, seaState: 2, rainCells: 0, landPolygons: 2, frame: { magic: 'SRAD', version: 1, headerBytes: 40 },
    });
    expect(st).toMatchObject({ type: 'status', range_nm: 6, antennaFwd_m: -31.65, guardZone: { enabled: true, outer_nm: 8 } });
    const tr = parseRadarMessage({
      type: 'tracks', radar: 'x', t: 129.0, scan: 51, transmitting: true,
      ownShip: { x: -2489.1, y: -2823.6, lat: 22.504517, lon: 120.215804, heading: 47.3, cog: 38.8, sog: 8.2 },
      cpaLimit_nm: 1, tcpaLimit_min: 12, trial: { course: 90, speed: 8, delay_min: 1 },
      targets: [
        { id: 2, label: '02', state: 'tracking', acquisition: 'auto', age_s: 119.0, stable: false, manoeuvring: false, dangerous: true,
          swapRisk: false, misses: 0, x: 1220.5, y: 4310.2, lat: 22.56892, lon: 120.25186, range_nm: 4.21, bearing_deg: 40.1,
          relBearing_deg: 352.8, course_deg: 231.8, speed_kn: 10.06, relCourse_deg: 226.4, relSpeed_kn: 18.21, cpa_nm: 0.428,
          tcpa_min: 12.0, bcr_nm: 0.512, bct_min: 11.4, trialCpa_nm: 1.37, trialTcpa_min: 10.8,
          past: [{ t: 72.5, x: 1583.2, y: 4596.0, lat: 22.571, lon: 120.2554 }] },
        { id: 3, label: '03', state: 'acquiring', acquisition: 'manual', age_s: 5, stable: false, manoeuvring: false, dangerous: false,
          swapRisk: false, misses: 1, x: 1, y: 2, lat: 0, lon: 0, range_nm: 1, bearing_deg: 10, relBearing_deg: 300 },
        { id: 4 }, // 缺必要欄位 → 略過
      ],
    });
    expect(tr?.type).toBe('tracks');
    if (tr?.type !== 'tracks') throw new Error();
    expect(tr.trial).toEqual({ course: 90, speed: 8, delay_min: 1 });
    expect(tr.targets).toHaveLength(2);
    expect(tr.targets[0]).toMatchObject({ cpa_nm: 0.428, bcr_nm: 0.512, trialCpa_nm: 1.37 });
    expect(tr.targets[0]!.past).toHaveLength(1);
    expect(tr.targets[1]!.cpa_nm).toBeUndefined();
    expect(parseRadarMessage({ type: 'event', radar: 'x', event: { kind: 'newTarget', t: 19.8, targetId: 1, message: '保護區新目標' } }))
      .toEqual({ type: 'event', radar: 'x', event: { kind: 'newTarget', t: 19.8, targetId: 1, message: '保護區新目標' } });
    expect(parseRadarMessage({ type: 'ack', radar: 'x', cmd: 'range', ok: true, reqId: 3, value: 6 })).toEqual({
      type: 'ack', radar: 'x', cmd: 'range', ok: true, reqId: 3, value: 6,
    });
    expect(parseRadarMessage({ type: 'state' })).toBeNull();
    expect(parseRadarMessage({ type: 'status' })).toBeNull();
    expect(parseRadarMessage('x')).toBeNull();
  });

  it('指令序列化(數值四捨五入到 1e-4)', () => {
    expect(serializeRadarCommand({ cmd: 'acquire', bearing: 45.123456, range: 3.1, reqId: 'a7' })).toBe(
      '{"cmd":"acquire","bearing":45.1235,"range":3.1,"reqId":"a7"}',
    );
    expect(JSON.parse(serializeRadarCommand({ cmd: 'cancel', id: 'all' }))).toEqual({ cmd: 'cancel', id: 'all' });
  });

  it('量程:最近標準量程、不小於、步進、距離圈間隔', () => {
    expect(nearestRange(5)).toBe(6);
    expect(nearestRange(2)).toBe(1.5);
    expect(nearestRange(200)).toBe(96);
    expect(rangeAtLeast(10.2)).toBe(12);
    expect(rangeAtLeast(6)).toBe(6);
    expect(rangeAtLeast(500)).toBe(96);
    expect(stepRange(6, 1)).toBe(12);
    expect(stepRange(0.25, -1)).toBe(0.25);
    expect(stepRange(96, 1)).toBe(96);
    expect(ringInterval(0.25)).toBe(0.05);
    expect(ringInterval(0.75)).toBe(0.25);
    expect(ringInterval(6)).toBe(1);
    expect(ringInterval(12)).toBe(2);
  });
});
