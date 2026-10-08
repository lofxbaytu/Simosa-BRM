// 自動講評摘要:最小 UKC、最大舵角、最大 ROT、車鐘變更、航程、平均速度、超速時間等。
import { describe, expect, it } from 'vitest';
import { DEFAULT_THRESHOLDS, DebriefAccumulator, summarize } from '../src/lib/debrief.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { OwnShipState } from '../src/types/state.js';

function straightRun(seconds: number, sog_kn: number, dt = 1): OwnShipState[] {
  const mps = (sog_kn * 1852) / 3600;
  const out: OwnShipState[] = [];
  for (let i = 0; i <= seconds / dt; i++) {
    const s = defaultState('FSB1');
    s.t = i * dt;
    s.tick = Math.round(s.t / 0.02);
    s.pos = { lat: 0, lon: 0, x: 0, y: mps * s.t };
    s.sog = sog_kn;
    s.stw = sog_kn;
    s.depthBelowKeel = 5;
    out.push(s);
  }
  return out;
}

describe('DebriefAccumulator', () => {
  it('直線航行 10 分鐘 6 kn:航程 1 nm、平均 6 kn、無超速', () => {
    const sum = summarize(straightRun(600, 6));
    expect(sum.samples).toBe(601);
    expect(sum.duration_s).toBe(600);
    expect(sum.distance_nm).toBeCloseTo(1, 5);
    expect(sum.avgSpeed_kn).toBeCloseTo(6, 5);
    expect(sum.timeOverSpeedLimit_s).toBe(0);
    expect(sum.minUkc_m).toBe(5);
    expect(sum.telegraphChanges).toBe(0);
    expect(sum.thresholds).toEqual(DEFAULT_THRESHOLDS);
  });

  it('超速、UKC、ROT 超限時間與極值時間戳', () => {
    const states = straightRun(100, 10); // 10 kn > 8 kn 速限
    states[30]!.depthBelowKeel = 0.8;
    states[31]!.depthBelowKeel = 0.6;
    states[50]!.rot = -35;
    states[70]!.rudder = 34.6;
    states[71]!.rudder = 35;
    states[72]!.rudder = 20;
    states[90]!.rudder = -35;
    const sum = summarize(states);
    expect(sum.timeOverSpeedLimit_s).toBe(100);
    expect(sum.minUkc_m).toBe(0.6);
    expect(sum.minUkcAt_s).toBe(31);
    expect(sum.timeUkcBelowAlarm_s).toBe(2);
    expect(sum.maxRot_degPerMin).toBe(35);
    expect(sum.maxRotAt_s).toBe(50);
    expect(sum.timeRotOverLimit_s).toBe(1);
    expect(sum.maxRudder_deg).toBe(35);
    expect(sum.maxRudderAt_s).toBe(71);
    expect(sum.hardOverCount).toBe(2);
    expect(sum.maxSog_kn).toBe(10);
  });

  it('車鐘變更次數與舵令次數;凍結時間不計入航程與超限', () => {
    const states = straightRun(20, 10);
    states[5]!.telegraph = 'HAH';
    for (let i = 5; i < 21; i++) states[i]!.telegraph = 'HAH';
    for (let i = 12; i < 21; i++) states[i]!.telegraph = 'FAH';
    states[8]!.rudderOrder = 10;
    for (let i = 8; i < 21; i++) states[i]!.rudderOrder = 10;
    for (let i = 15; i < 21; i++) states[i]!.flags = { frozen: true, aground: false, collision: false };
    const sum = summarize(states);
    expect(sum.telegraphChanges).toBe(2);
    expect(sum.rudderOrders).toBe(1);
    expect(sum.frozen_s).toBe(6);
    expect(sum.timeOverSpeedLimit_s).toBe(14);
    // 航程只算未凍結的 14 秒
    expect(sum.distance_nm).toBeCloseTo((10 * 14) / 3600, 6);
    expect(sum.avgSpeed_kn).toBeCloseTo(10, 6);
  });

  it('擱淺/碰撞事件計數,時間倒退不累計負時間;reset 清空', () => {
    const acc = new DebriefAccumulator();
    const states = straightRun(10, 5);
    states[3]!.flags = { frozen: false, aground: true, collision: false };
    states[4]!.flags = { frozen: false, aground: true, collision: true };
    for (const s of states) acc.push(s);
    const back = { ...states[0]!, t: 0 };
    acc.push(back);
    const sum = acc.summary();
    expect(sum.agroundEvents).toBe(1);
    expect(sum.collisionEvents).toBe(1);
    expect(sum.distance_nm).toBeGreaterThan(0);
    acc.reset();
    expect(acc.summary().samples).toBe(0);
    expect(acc.summary().minUkc_m).toBeNull();
  });
});
