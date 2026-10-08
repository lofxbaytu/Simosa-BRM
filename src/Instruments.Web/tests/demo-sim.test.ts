import { describe, expect, it } from 'vitest';
import { DEMO_DT, DemoSim } from '../src/demo/demo-sim.js';
import { headingDifference } from '../src/lib/angles.js';
import { parseState } from '../src/lib/state-parser.js';

function run(sim: DemoSim, seconds: number): void {
  const n = Math.round(seconds / DEMO_DT);
  for (let i = 0; i < n; i++) sim.step();
}

describe('示範模式運動學', () => {
  it('初始狀態符合 state.schema 且可被解析器接受', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 45, initialSpeed_kn: 10 });
    const s = sim.current;
    expect(s.shipId).toBe('FSB1');
    expect(s.heading).toBe(45);
    expect(s.stw).toBeCloseTo(10);
    expect(parseState(JSON.parse(JSON.stringify(s)))!.issues).toEqual([]);
  });
  it('右舵使航向右轉、ROT 為正、速度下降', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 14.5, current: { set: 0, drift: 0 } });
    sim.apply({ type: 'rudder', value: 35 });
    run(sim, 60);
    const s = sim.current;
    expect(s.rudder).toBeCloseTo(35, 0);
    expect(s.rot).toBeGreaterThan(5);
    expect(headingDifference(0, s.heading)).toBeGreaterThan(10);
    expect(s.stw).toBeLessThan(14.5);
    expect(s.tick).toBe(3000);
    expect(s.t).toBeCloseTo(60, 6);
  });
  it('左舵使航向左轉', () => {
    const sim = new DemoSim({ shipId: 'FSB2', initialHeading: 90, initialSpeed_kn: 12, current: { set: 0, drift: 0 } });
    sim.apply({ type: 'rudder', value: -20 });
    run(sim, 60);
    expect(sim.current.rot).toBeLessThan(0);
    expect(headingDifference(90, sim.current.heading)).toBeLessThan(-5);
  });
  it('舵機速率受限:舵角不會瞬間到位', () => {
    const sim = new DemoSim({ shipId: 'FSB2', initialSpeed_kn: 10 });
    sim.apply({ type: 'rudder', value: 35 });
    sim.step();
    expect(sim.current.rudder).toBeLessThan(1);
    run(sim, 4);
    expect(sim.current.rudder).toBeGreaterThan(10);
    expect(sim.current.rudder).toBeLessThan(35);
  });
  it('車鐘 NAVF 使轉速與速度一階上升', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialSpeed_kn: 0, current: { set: 0, drift: 0 } });
    sim.apply({ type: 'telegraph', value: 'NAVF' });
    expect(sim.current.rpmOrder).toBe(169);
    run(sim, 30);
    const mid = sim.current;
    expect(mid.rpm).toBeGreaterThan(100);
    expect(mid.rpm).toBeLessThan(169);
    expect(mid.stw).toBeGreaterThan(1);
    run(sim, 600);
    expect(sim.current.rpm).toBeCloseTo(169, 0);
    expect(sim.current.stw).toBeCloseTo(13, 0);
  });
  it('倒車使速度變負', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialSpeed_kn: 0, current: { set: 0, drift: 0 } });
    sim.apply({ type: 'telegraph', value: 'HAS' });
    run(sim, 300);
    expect(sim.current.rpm).toBeLessThan(-90);
    expect(sim.current.stw).toBeLessThan(-1);
  });
  it('側推在低速時使船迴轉', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 0, current: { set: 0, drift: 0 } });
    sim.apply({ type: 'thruster', value: 1 });
    run(sim, 120);
    expect(sim.current.thruster?.actual).toBeCloseTo(1, 1);
    expect(sim.current.rot).toBeGreaterThan(10);
    expect(headingDifference(0, sim.current.heading)).toBeGreaterThan(15);
  });
  it('自動舵把航向帶到設定值,ROT 不超過限制太多', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 12, current: { set: 0, drift: 0 } });
    sim.apply({ type: 'autopilot', args: { enabled: true, heading: 60, rotLimit: 15 } });
    let maxRot = 0;
    for (let i = 0; i < 600 / DEMO_DT; i++) {
      sim.step();
      maxRot = Math.max(maxRot, sim.current.rot);
    }
    expect(Math.abs(headingDifference(sim.current.heading, 60))).toBeLessThan(2);
    expect(maxRot).toBeLessThan(25);
    expect(sim.autopilotState.enabled).toBe(true);
  });
  it('手動舵令會解除自動舵', () => {
    const sim = new DemoSim();
    sim.apply({ type: 'autopilot', args: { enabled: true, heading: 10 } });
    sim.apply({ type: 'rudder', value: 5 });
    expect(sim.autopilotState.enabled).toBe(false);
  });
  it('凍結停止積分,恢復後繼續,重設回初始', () => {
    const sim = new DemoSim({ initialSpeed_kn: 8, current: { set: 0, drift: 0 } });
    run(sim, 10);
    sim.apply({ type: 'freeze' });
    const t0 = sim.current.t;
    run(sim, 5);
    expect(sim.current.t).toBe(t0);
    expect(sim.current.flags?.frozen).toBe(true);
    sim.apply({ type: 'resume' });
    run(sim, 1);
    expect(sim.current.t).toBeGreaterThan(t0);
    sim.apply({ type: 'reset' });
    expect(sim.current.t).toBe(0);
    expect(sim.current.pos.x).toBe(0);
    expect(sim.current.flags?.frozen).toBe(false);
  });
  it('均勻流使 COG/SOG 與 HDG/STW 不同,緯經度隨位置改變', () => {
    const sim = new DemoSim({ initialHeading: 0, initialSpeed_kn: 10, current: { set: 90, drift: 2 }, originLat: 23.8, originLon: 120.12 });
    run(sim, 60);
    const s = sim.current;
    expect(s.cog).toBeGreaterThan(5);
    expect(s.cog).toBeLessThan(30);
    expect(s.sog).toBeGreaterThan(s.stw);
    expect(s.pos.lat).toBeGreaterThan(23.8);
    expect(s.pos.lon).toBeGreaterThan(120.12);
  });
  it('相對風:頂風前進時相對風速大於真風速', () => {
    const sim = new DemoSim({ initialHeading: 0, initialSpeed_kn: 10, wind: { trueSpeed: 10, trueDir: 0 }, current: { set: 0, drift: 0 } });
    sim.step();
    expect(sim.current.wind.relSpeed).toBeCloseTo(20, 0);
    expect(sim.current.wind.relDir).toBeCloseTo(0, 0);
  });
  it('水深不足時 UKC ≤ 0 並標記擱淺', () => {
    const sim = new DemoSim({ shipId: 'FSB2', waterDepth_m: 7 });
    sim.step();
    expect(sim.current.depthBelowKeel).toBeLessThanOrEqual(0);
    expect(sim.current.flags?.aground).toBe(true);
  });
});
