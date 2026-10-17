import { describe, expect, it } from 'vitest';
import { DEMO_CPA_ALARM_NM, DEMO_DT, DEMO_PARAMETER_SET_VERSION, DemoSim } from '../src/demo/demo-sim.js';
import { DemoSource } from '../src/demo/demo-source.js';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
import { headingDifference } from '../src/lib/angles.js';
import { parseState } from '../src/lib/state-parser.js';

function run(sim: DemoSim, seconds: number): void {
  const n = Math.round(seconds / DEMO_DT);
  for (let i = 0; i < n; i++) sim.step();
}

/** 取走事件但略過假目標船的交通事件(cpaAlarm / cpaClear;另有專門測試)。 */
function takeNonTraffic(sim: DemoSim) {
  return sim.takeEvents().filter((e) => e.source !== 'traffic');
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
    const events = takeNonTraffic(sim);
    expect(events.map((e) => e.kind)).toEqual(['timelineTriggered', 'message']);
    expect(events[0]!.itemId).toBe('demoBrief');
    expect(events[0]!.source).toBe('timeline');
    expect(events[0]!.seq).toBe(1); // seq 0 為 t = 0 的假目標船 cpaAlarm
    expect(sim.current.scenario?.timelineFired).toBe(1);
    sim.apply({ type: 'rudder', value: 20 });
    run(sim, 30);
    const more = takeNonTraffic(sim);
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
    const kinds = takeNonTraffic(sim).map((e) => `${e.source}:${e.kind}`);
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
    expect(takeNonTraffic(sim)).toEqual([]);
    expect(sim.steeringState).toEqual({ mode: 'fu', pump: 'both' });
  });

  it('DemoSource 以 onMessage 推送事件框與 assessment 回覆', () => {
    const source = new DemoSource({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 5 });
    const frames: Record<string, unknown>[] = [];
    source.onMessage((m) => {
      if (m['type'] === 'event' && (m['event'] as { source: string }).source === 'traffic') return; // 假目標船事件另測
      frames.push(m);
    });
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

describe('示範模式的操舵模式(steeringMode / state.steering)', () => {
  it('初始 fu/both;切 nfu 推桿推向滿舵、放開舵停在目前舵角;離開 nfu 舵令同步到舵角;模式/泵變更各發一次 steeringModeChanged', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 8 });
    expect(sim.current.steering).toEqual({ mode: 'fu', pump: 'both' });
    sim.takeEvents();
    sim.apply({ type: 'steeringMode', value: 'nfu', args: { pump: '1', direction: 'starboard' } });
    expect(sim.current.steering).toEqual({ mode: 'nfu', pump: '1', nfuActive: true });
    expect(sim.current.rudderOrder).toBe(sim.cfg.rudderMax_deg);
    run(sim, 2);
    const midRudder = sim.current.rudder;
    expect(midRudder).toBeGreaterThan(3);
    sim.apply({ type: 'steeringMode', value: 'nfu', args: { direction: 'stop' } });
    expect(sim.current.steering).toEqual({ mode: 'nfu', pump: '1', nfuActive: false });
    expect(sim.current.rudderOrder).toBeCloseTo(midRudder, 6);
    sim.apply({ type: 'steeringMode', value: 'nfu', args: { direction: 'left' } }); // 不明方向視同 stop(D35)
    expect(sim.current.steering!.nfuActive).toBe(false);
    sim.apply({ type: 'steeringMode', value: 'nfu', args: { direction: 'port' } });
    run(sim, 1);
    sim.apply({ type: 'steeringMode', value: 'fu' });
    expect(sim.current.steering).toEqual({ mode: 'fu', pump: '1' });
    expect(sim.current.rudderOrder).toBeCloseTo(sim.current.rudder, 6);
    sim.apply({ type: 'steeringMode', value: 'fu', args: { pump: 'both' } });
    const ev = sim.takeEvents().filter((e) => e.kind === 'steeringModeChanged');
    expect(ev.map((e) => e.data)).toEqual([
      { from: 'fu', to: 'nfu', pump: '1' },
      { from: 'nfu', to: 'fu', pump: '1' },
      { from: 'fu', to: 'fu', pump: 'both' },
    ]);
    expect(ev[0]!.source).toBe('instructor');
    expect(parseState(JSON.parse(JSON.stringify(sim.current)))!.issues).toEqual([]);
  });

  it('autopilot 模式與 autopilot 指令同義;人工舵令切回 fu;應急操舵拒絕自動舵並發 commandRejected、舵機速率減半', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 8 });
    sim.apply({ type: 'steeringMode', value: 'autopilot', args: { heading: 30, rotLimit: 10 } });
    expect(sim.current.steering!.mode).toBe('autopilot');
    expect(sim.autopilotState).toEqual({ enabled: true, heading: 30, rotLimit: 10 });
    sim.apply({ type: 'rudder', value: 5 });
    expect(sim.current.steering!.mode).toBe('fu');
    expect(sim.autopilotState.enabled).toBe(false);
    sim.apply({ type: 'autopilot', args: { enabled: true, heading: 45 } });
    expect(sim.current.steering!.mode).toBe('autopilot');
    sim.apply({ type: 'autopilot', args: { enabled: false } });
    expect(sim.current.steering!.mode).toBe('fu');
    sim.takeEvents();
    sim.apply({ type: 'steeringMode', value: 'emergency' });
    sim.apply({ type: 'steeringMode', value: 'autopilot' });
    sim.apply({ type: 'autopilot', args: { enabled: true } });
    expect(sim.current.steering!.mode).toBe('emergency');
    expect(sim.autopilotState.enabled).toBe(false);
    const ev = sim.takeEvents().filter((e) => e.source !== 'traffic');
    expect(ev.map((e) => e.kind)).toEqual(['steeringModeChanged', 'commandRejected', 'commandRejected']);
    expect(ev[1]!.data).toMatchObject({ command: 'steeringMode', reason: 'emergencySteering' });
    expect(ev[2]!.data).toMatchObject({ command: 'autopilot', reason: 'emergencySteering' });
    // 應急操舵:舵機速率減半
    sim.apply({ type: 'rudder', value: 35 });
    run(sim, 2);
    const emergencyRudder = sim.current.rudder;
    const ref = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 8 });
    ref.apply({ type: 'rudder', value: 35 });
    run(ref, 2);
    expect(emergencyRudder).toBeCloseTo(ref.current.rudder / 2, 1);
  });
});

describe('示範模式的假目標船', () => {
  it('兩艘目標船:交叉船 CPA 小於門檻且 t = 0 即發 cpaAlarm;錨泊船 AIS 關、不在警報;狀態可被解析器接受', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 90, initialSpeed_kn: 8, current: { set: 0, drift: 0 } });
    const s = sim.current;
    expect(s.targets.map((t) => t.id)).toEqual(['DEMO-01', 'DEMO-02']);
    const crossing = s.targets[0]!;
    const anchored = s.targets[1]!;
    expect(crossing.range_nm).toBeCloseTo(1.5, 1);
    expect(crossing.bearing_deg).toBeCloseTo(135, 0);
    expect(crossing.cpa_nm).toBeLessThan(DEMO_CPA_ALARM_NM);
    expect(crossing.tcpa_min).toBeGreaterThan(0);
    expect(crossing.aisOn).toBe(true);
    expect(crossing.ais?.mmsi).toBe(416000001);
    expect(anchored.sog).toBe(0);
    expect(anchored.aisOn).toBe(false);
    expect(anchored.bearing_deg).toBeCloseTo(0, 0);
    expect(anchored.range_nm).toBeCloseTo(1.2, 1);
    const first = sim.takeEvents();
    expect(first.map((e) => `${e.source}:${e.kind}:${e.targetId}`)).toEqual(['traffic:cpaAlarm:DEMO-01']);
    expect(first[0]!.visible).toBe(true);
    expect(first[0]!.cpa_nm).toBeLessThan(DEMO_CPA_ALARM_NM);
    expect(parseState(JSON.parse(JSON.stringify(s)))!.issues).toEqual([]);
  });

  it('目標船隨時間直線前進、距離縮短;通過 CPA 後發 cpaClear;reset 後重新進入警報', () => {
    const sim = new DemoSim({ shipId: 'FSB1', initialHeading: 0, initialSpeed_kn: 0, current: { set: 0, drift: 0 } });
    const r0 = sim.current.targets[0]!.range_nm;
    run(sim, 60);
    expect(sim.current.targets[0]!.range_nm).toBeLessThan(r0 - 0.1);
    expect(sim.current.targets[1]!.range_nm).toBeCloseTo(1.2, 2); // 錨泊船不動、自船靜止
    run(sim, 12 * 60);
    const kinds = sim.takeEvents().filter((e) => e.source === 'traffic').map((e) => e.kind);
    expect(kinds).toEqual(['cpaAlarm', 'cpaClear']);
    expect(sim.current.targets[0]!.tcpa_min).toBeLessThan(0);
    sim.apply({ type: 'reset' });
    expect(sim.takeEvents().map((e) => e.kind)).toEqual(['cpaAlarm']);
  });
});
