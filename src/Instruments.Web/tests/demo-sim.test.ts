import { describe, expect, it } from 'vitest';
import { DEMO_DT, DEMO_PARAMETER_SET_VERSION, DemoSim } from '../src/demo/demo-sim.js';
import { DemoSource } from '../src/demo/demo-source.js';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
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

describe('示範模式的情境引擎預覽(假事件、情境/航線狀態、假 summary)', () => {
  it('狀態含 scenario/route/visibility_nm;30 s 與 60 s 的假時間表事件;XTD 右正', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 8, current: { set: 0, drift: 0 } });
    expect(sim.current.visibility_nm).toBe(10);
    expect(sim.current.scenario).toMatchObject({ id: 'demo', phase: 'normal', parameterSetVersion: DEMO_PARAMETER_SET_VERSION, timelineFired: 0, timelinePending: 0 });
    expect(sim.current.route).toMatchObject({ legIndex: 0, nextWaypoint: 'WP2', xtd_m: 0, xtdLimit_m: 50, legCourse_deg: 0 });
    expect(parseState(JSON.parse(JSON.stringify(sim.current)))!.issues).toEqual([]);
    run(sim, 31);
    const events = sim.takeEvents();
    expect(events.map((e) => e.kind)).toEqual(['timelineTriggered', 'message']);
    expect(events[0]!.itemId).toBe('demoBrief');
    expect(events[0]!.source).toBe('timeline');
    expect(events[0]!.seq).toBe(0);
    expect(sim.current.scenario?.timelineFired).toBe(1);
    sim.apply({ type: 'rudder', value: 20 });
    run(sim, 30);
    const more = sim.takeEvents();
    expect(more.map((e) => e.kind)).toEqual(['timelineTriggered', 'voiceScript']);
    expect(more[1]!.data).toMatchObject({ role: 'ecr', channel: 'phone' });
    run(sim, 120); // 右轉一段時間後偏到航線右側:XTD 右正
    expect(headingDifference(0, sim.current.heading)).toBeGreaterThan(20);
    expect(sim.current.route!.xtd_m).toBeGreaterThan(10);
  });

  it('故障注入/確認/手動觸發/凍結/結束練習各產生事件;endExercise 後 summary 定稿', () => {
    const sim = new DemoSim({ shipId: 'FSB2', initialHeading: 90, initialSpeed_kn: 6 });
    run(sim, 10);
    sim.takeEvents();
    sim.apply({ type: 'injectFault', value: 'steeringGear' });
    sim.apply({ type: 'injectFault', args: { name: 'steeringGear' } });
    run(sim, 5);
    sim.apply({ type: 'acknowledge', args: { kind: 'notifyEcr', role: 'master' } });
    sim.apply({ type: 'triggerTimeline', args: { id: 'demoBrief' } });
    sim.apply({ type: 'triggerTimeline', args: { id: 'demoBrief' } });
    sim.apply({ type: 'triggerTimeline', args: { id: 'demoBrief', force: true } });
    sim.apply({ type: 'freeze' });
    sim.apply({ type: 'resume' });
    sim.apply({ type: 'clearFault', value: 'steeringGear' });
    sim.apply({ type: 'endExercise', args: { reason: '測試' } });
    const kinds = sim.takeEvents().map((e) => `${e.source}:${e.kind}`);
    expect(kinds).toEqual([
      'instructor:faultInjected',
      'instructor:phaseChanged',
      'instructor:faultInjected',
      'instructor:acknowledge',
      'instructor:timelineManual',
      'timeline:timelineTriggered',
      'timeline:message',
      'instructor:timelineManual',
      'instructor:timelineManual',
      'timeline:timelineTriggered',
      'timeline:message',
      'instructor:frozen',
      'instructor:resumed',
      'instructor:faultCleared',
      'instructor:exerciseEnded',
    ]);
    expect(sim.current.scenario?.phase).toBe('degraded');
    expect(sim.current.scenario?.ended).toBe(true);
    expect(sim.isFrozen).toBe(true);
    const summary = parseAssessmentSummary(sim.assessmentSummary())!;
    expect(summary.final).toBe(true);
    expect(summary.parameterSet?.version).toBe(DEMO_PARAMETER_SET_VERSION);
    expect(summary.faultResponses[0]).toMatchObject({ fault: 'steeringGear', source: 'instructor' });
    expect(summary.faultResponses[0]!.acknowledgements[0]).toMatchObject({ kind: 'notifyEcr', role: 'master', dt_s: 5 });
    expect(summary.assessment.required[0]).toMatchObject({ id: 'notifyEcr', state: 'met', dt_s: 5 });
    expect(summary.assessment.score).toBe(100);
    expect(summary.assessment.passed).toBe(true);
    sim.apply({ type: 'reset' });
    expect(sim.current.scenario).toMatchObject({ phase: 'normal', ended: false, timelineFired: 0 });
    expect(sim.takeEvents()).toEqual([]);
  });

  it('DemoSource 以 onMessage 推送事件框與 assessment 回覆', () => {
    const source = new DemoSource({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 5 });
    const frames: Record<string, unknown>[] = [];
    source.onMessage((m) => frames.push(m));
    source.send({ type: 'injectFault', value: 'mainEngine' });
    expect(frames.map((f) => f['type'])).toEqual(['event', 'event']);
    expect((frames[0]!['event'] as { kind: string }).kind).toBe('faultInjected');
    source.send({ type: 'getAssessment' });
    const last = frames.at(-1)!;
    expect(last['type']).toBe('assessment');
    expect(last['final']).toBe(false);
    expect(parseAssessmentSummary(last['summary'])?.faultResponses[0]?.fault).toBe('mainEngine');
    source.send({ type: 'endExercise' });
    expect(frames.at(-1)!['final']).toBe(true);
    // 結束後的後備 getAssessment 回覆仍為定稿(與 C# 主控程序 final = summary.Final 相同),不會把講評面板改回「未定稿」
    source.send({ type: 'getAssessment' });
    expect(frames.at(-1)!['type']).toBe('assessment');
    expect(frames.at(-1)!['final']).toBe(true);
    expect(parseAssessmentSummary(frames.at(-1)!['summary'])?.final).toBe(true);
  });
});
