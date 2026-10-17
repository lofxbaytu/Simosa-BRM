// 核心與教官站的契約一致性:用 C# 主控程序實際產生的紀錄(tests/fixtures/record-E07-core.jsonl、record-E01-core.jsonl、record-E05-core.jsonl;
// `dotnet run --project src/SimCore.Host -- --scenario data/scenarios/E07_steering_failure.yaml --record … --duration 300 --timescale 10`
// 後縮短:保留 header、每 10 s(E07、E05)/20 s(E01)一筆 state、全部 event、3 筆 snapshot、summary、footer)驗證:
//  - record-replay 解析器讀得到 header v2 / parameterSet / event / summary / footer;
//  - 事件解析(sim-event)對核心每一種實際輸出的 kind 都有 source、t、data 與中文標籤;
//  - summary 解析(assessment)欄位名與核心 AssessmentSummary.cs 一致(freezes、t_s、clause、escort、legIndex…);
//  - state 解析(state-parser)對核心 state 行(含 scenario/route/visibility_nm)不回報任何 issue;
//  - header.scenario(C# 序列化的 JSON 情境)與 data/scenarios 的 YAML 經同一個 parseScenario 解析後結構相同。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
import { eventIndexAt, parseJsonLines, parseReplay, stateIndexAt } from '../src/lib/record-replay.js';
import { parseScenario } from '../src/lib/scenario.js';
import { defaultSourceFor, eventLabel, eventSeverity, parseSimEvent, voiceScriptData } from '../src/lib/sim-event.js';
import { parseState } from '../src/lib/state-parser.js';
import { SIM_EVENT_KINDS } from '../src/types/event.js';
import type { OwnShipState } from '../src/types/state.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(here, '../../..');
const e07Text = readFileSync(path.resolve(here, 'fixtures/record-E07-core.jsonl'), 'utf-8');
const e01Text = readFileSync(path.resolve(here, 'fixtures/record-E01-core.jsonl'), 'utf-8');
const e05Text = readFileSync(path.resolve(here, 'fixtures/record-E05-core.jsonl'), 'utf-8');
const e07Lines = e07Text.split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l) as Record<string, unknown>);

describe('核心紀錄(E07_steering_failure,MMG,300 s)', () => {
  const r = parseReplay(e07Text);

  it('格式辨識為 SimCore JSON Lines,逐行皆可解析且無未知 kind', () => {
    expect(r.source).toBe('simcore-jsonl');
    expect(r.errors).toEqual([]);
    expect(r.unknownKinds).toEqual({});
    expect(r.states).toHaveLength(30);
    expect(r.snapshots.map((s) => s.tick)).toEqual([500, 5000, 15000]);
    expect(r.inputs).toEqual([]);
    expect(r.tStart).toBeCloseTo(0.04, 9);
    expect(r.tEnd).toBeCloseTo(290.04, 9);
  });

  it('header:version 2、動力學 mmg/1、完整情境與參數集(18 個引用含出處/版次/條款)', () => {
    expect(r.header).toMatchObject({ version: 2, dt: 0.02, shipId: 'FSB1', shipName: 'FS BITUMEN NO.1', loading: 'full', seed: 20260707, dynamics: 'mmg/1', scenarioId: 'E07_steering_failure' });
    expect(r.header.scenarioName).toContain('舵機故障');
    const ps = r.header.parameterSet!;
    expect(ps.id).toBe('simosa-company-procedures');
    expect(ps.version).toBe('2026.10.1');
    expect(ps.sha256).toMatch(/^[0-9A-F]{64}$/);
    expect(ps.resolved).toHaveLength(18);
    expect(ps.resolved[0]).toEqual({ path: 'route.xtdLimit_m', key: 'navigation.xtd.channel_m', value: 50, unit: 'm', source: 'SMS-NAV-012', revision: '2', clause: '4.1' });
    for (const item of ps.resolved) {
      expect(item.path).not.toBe('');
      expect(item.key).toMatch(/^[a-zA-Z]+(\.[a-zA-Z_]+)+$/);
      expect(item.value).toBeDefined();
      expect(item.source).toBeTruthy();
    }
    // 參數已替換:情境內不再有 ${params:…}
    expect(JSON.stringify(r.header.scenario)).not.toContain('${params:');
  });

  // 航道 CPA 門檻為 navigation.cpa.channel_nm = 0.05 nm:OUT1(CPA 0.20 nm)與錨泊的 ANCH1 都不觸發 cpaAlarm/encounterStarted,
  // 故 300 s 內只有時間表(pilotBoarded)、目標船出現與 XTD 相關事件共 7 個。
  it('event 行:7 個事件、seq 連續、source 與 kind 推得的預設一致、每種 kind 都有中文標籤', () => {
    expect(r.events).toHaveLength(7);
    expect(r.events.map((e) => e.kind)).toEqual(['timelineTriggered', 'voiceScript', 'message', 'targetActivated', 'xtdExceeded', 'bandWarn', 'bandWarn']);
    expect(r.events.map((e) => e.seq)).toEqual([0, 1, 2, 3, 4, 5, 6]);
    for (const e of r.events) {
      expect(e.source).toBe(defaultSourceFor(e.kind));
      expect(e.t).toBeCloseTo(e.tick * 0.02, 9);
      expect(e.phase).toBe('normal');
      expect((SIM_EVENT_KINDS as readonly string[]).includes(e.kind)).toBe(true);
      const label = eventLabel(e);
      expect(label).not.toMatch(/\?|undefined|NaN/);
      expect(label.startsWith(e.kind)).toBe(false); // 不是 default 分支的原樣輸出
    }
    const voice = r.events.find((e) => e.kind === 'voiceScript')!;
    expect(voiceScriptData(voice)).toEqual({ role: 'pilot', channel: 'bridge', text: voice.message, lang: 'zh-TW', file: 'audio/E07/pilot_boarded.ogg' });
    expect(eventLabel(voice)).toContain('語音 引水人(駕駛台)');
    const trig = r.events.find((e) => e.kind === 'timelineTriggered')!;
    expect(trig.itemId).toBe('pilotBoarded');
    expect(trig.data).toEqual({ fireCount: 1, manual: false, forced: false });
    expect(r.events.find((e) => e.kind === 'cpaAlarm')).toBeUndefined();
    const activated = r.events.find((e) => e.kind === 'targetActivated')!;
    expect(activated).toMatchObject({ source: 'traffic', targetId: 'OUT1', t: 240 });
    expect(eventSeverity(activated)).toBe('info');
    const xtd = r.events.find((e) => e.kind === 'xtdExceeded')!;
    expect(xtd.data).toMatchObject({ limit_m: 50, legIndex: 0 });
    expect(eventLabel(xtd)).toBe('XTD 超限 50 m(限 50 m)');
    const band = r.events.filter((e) => e.kind === 'bandWarn');
    expect(band.map((e) => e.itemId)).toEqual(['xtdMax', 'xtdTime']);
    expect(band[0]!.data).toMatchObject({ metric: 'track.maxXtd_m', warn: 50, penalty: 10 });
    expect(eventIndexAt(r.events, 60)).toBe(2);
    expect(eventIndexAt(r.events, 240)).toBe(3);
    expect(eventIndexAt(r.events, 0.5)).toBe(-1);
  });

  it('summary 行:分數/及格/參數集版次/容許帶/必要事件/事件統計欄位名與核心一致', () => {
    const s = r.summary!;
    expect(s).toMatchObject({ version: 1, advisory: true, final: true, scenarioId: 'E07_steering_failure', shipId: 'FSB1', loading: 'full' });
    expect(s.parameterSet).toEqual({ id: 'simosa-company-procedures', version: '2026.10.1', sha256: r.header.parameterSet!.sha256 });
    expect(s.phases).toEqual([{ phase: 'normal', from_s: 0, to_s: 300 }]);
    expect(s.general).toMatchObject({ duration_s: 300, freezes: 0 });
    expect(s.general.frozen_s).toBeUndefined();
    expect(s.general.distance_nm).toBeCloseTo(0.477, 2);
    expect(s.assessment).toMatchObject({ score: 85, passMark: 70, passed: true, hardFailed: false, hardFails: [] });
    expect(s.assessment.bands.map((b) => [b.id, b.state])).toEqual([
      ['xtdMax', 'warn'],
      ['xtdTime', 'warn'],
      ['ukcTime', 'ok'],
      ['speed', 'ok'],
      ['minCpa', 'ok'],
    ]);
    expect(s.assessment.bands[0]).toMatchObject({ metric: 'track.maxXtd_m', warn: 50, fail: 100, penalty: 10, paramKey: 'navigation.xtd.channel_m', source: 'SMS-NAV-012', revision: '2', clause: '4.1' });
    expect(s.assessment.required.map((q) => [q.id, q.state])).toEqual([
      ['steeringFirstAction', 'notApplicable'],
      ['notifyEcr', 'notApplicable'],
      ['emergencySteering', 'notApplicable'],
      ['vtsReport', 'notApplicable'],
    ]);
    expect(s.assessment.required[0]).toMatchObject({ within_s: 60, penalty: 0, paramKey: 'emergency.steeringFailure.firstAction_s', source: 'SMS-EMG-004', revision: '1', clause: '2.1' });
    expect(s.assessment.parameters.length).toBeGreaterThan(0);
    expect(s.assessment.parameters.map((p) => p.key)).toContain('navigation.xtd.channel_m');
    expect(s.events.total).toBe(7);
    expect(s.events.byKind).toEqual({ bandWarn: 2, message: 1, targetActivated: 1, timelineTriggered: 1, voiceScript: 1, xtdExceeded: 1 });
    // 事件統計與 event 行一致
    const counted: Record<string, number> = {};
    for (const e of r.events) counted[e.kind] = (counted[e.kind] ?? 0) + 1;
    expect(counted).toEqual(s.events.byKind);
    // 避碰:航道門檻 0.05 nm 下無會遇(targets 空、minCpa_nm 省略),minCpa 容許帶因而 ok 且無 value;航跡、UKC、操縱
    expect(s.collisionAvoidance).toEqual({ encounters: 0, collisions: 0, targets: [] });
    expect(s.collisionAvoidance.minCpa_nm).toBeUndefined();
    expect(s.assessment.bands[4]).toMatchObject({ id: 'minCpa', metric: 'collisionAvoidance.minCpa_nm', warn: 0.05, fail: 0.025, state: 'ok', paramKey: 'navigation.cpa.channel_nm' });
    expect(s.assessment.bands[4]!.value).toBeUndefined();
    expect(s.track).toMatchObject({ maxXtdLeg: 0, corridorExcursions: 1, legIndex: 0, complete: false, offRoute: false, waypoints: [] });
    expect(s.track!.maxXtd_m).toBeCloseTo(60.0, 0);
    expect(s.track!.timeOutsideCorridor_s).toBe(30);
    expect(s.ukc).toMatchObject({ timeBelowLimit_s: 0, excursions: 0, groundings: 0, limit_m: 1, limitSource: 'navigation.ukc.minimum_m' });
    expect(s.manoeuvring.rudderDistribution_s).toEqual({ '0-5': 300, '5-10': 0, '10-20': 0, '20-35': 0, '>35': 0 });
    expect(s.manoeuvring.telegraphOrders).toEqual([{ order: 'HAH', from_s: 0, to_s: 300, duration_s: 300 }]);
    expect(s.faultResponses).toEqual([]);
  });

  it('footer:最終 tick、狀態雜湊、summary 行數', () => {
    expect(r.footer).toMatchObject({ finalTick: 15000, inputs: 0 });
    expect(r.footer!.stateHash).toMatch(/^[0-9A-F]{64}$/);
    expect(r.footer!.sha256).toMatch(/^[0-9A-F]{64}$/);
    const footerLine = e07Lines.find((l) => l['kind'] === 'footer')!;
    expect(footerLine['summaryLines']).toBe(1);
  });

  it('state 行:scenario/route/visibility_nm 解析無 issue;route 隨時間更新', () => {
    let prev: OwnShipState | undefined;
    for (const line of e07Lines.filter((l) => l['kind'] === 'state')) {
      const parsed = parseState(line['state'], prev)!;
      expect(parsed.issues).toEqual([]);
      prev = parsed.state;
    }
    const first = r.states[0]!;
    expect(first.visibility_nm).toBe(6);
    expect(first.scenario).toEqual({ id: 'E07_steering_failure', phase: 'normal', parameterSetVersion: '2026.10.1', timelineFired: 0, timelinePending: 0, hardFailed: false, ended: false });
    const at100 = r.states[stateIndexAt(r.states, 100)]!;
    expect(at100.scenario).toMatchObject({ timelineFired: 1, score: 100, ended: false });
    expect(at100.route).toMatchObject({ legIndex: 0, nextWaypoint: 'WP2', xtdLimit_m: 50, speedLimit_kn: 8, complete: false });
    expect(at100.route!.distanceToWaypoint_nm).toBeGreaterThan(2);
    expect(at100.route!.legCourse_deg).toBeCloseTo(51.68, 1);
    const last = r.states[r.states.length - 1]!;
    expect(last.scenario?.score).toBe(85);
    expect(Math.abs(last.route!.xtd_m!)).toBeGreaterThan(50);
    // 目標船 targets[](真實核心紀錄每幀含所有已出現的目標):解析為 TargetState,原始行同樣有 2 個目標
    expect((e07Lines.filter((l) => l['kind'] === 'state').at(-1)!['state'] as { targets: unknown[] }).targets).toHaveLength(2);
    expect(last.targets.map((t) => t.id)).toEqual(['OUT1', 'ANCH1']);
    expect(last.targets[0]).toMatchObject({ name: 'TAI PING 12', shipType: 'cargo', aisOn: true, behaviour: 'waypoints' });
    expect(last.targets[0]!.ais?.mmsi).toBe(416400201);
    expect(last.targets[1]).toMatchObject({ name: 'SHENG LI', lights: 'anchored', behaviour: 'anchored' });
    expect(last.targets[1]!.cpa_nm).toBeCloseTo(1.01, 1);
    expect(last.targets[0]!.cpa_nm).toBeCloseTo(0.60, 1);
    // 2026-10-17 新增的操舵模式欄位(state.steering)已進紀錄
    expect((e07Lines.filter((l) => l['kind'] === 'state').at(-1)!['state'] as { steering: unknown }).steering).toEqual({ mode: 'fu', pump: '1' });
  });

  it('header.scenario(核心序列化)與 data/scenarios/E07_steering_failure.yaml 經 parseScenario 結構相同', () => {
    const fromYaml = parseScenario(readFileSync(path.join(projectRoot, 'data/scenarios/E07_steering_failure.yaml'), 'utf-8'), 'data/scenarios/E07_steering_failure.yaml');
    const fromHeader = parseScenario(JSON.stringify(r.header.scenario), 'data/scenarios/E07_steering_failure.yaml');
    expect(fromYaml.issues).toEqual([]);
    expect(fromHeader.issues).toEqual([]);
    expect(fromHeader.id).toBe(fromYaml.id);
    expect(fromHeader.timeline.map((i) => i.id)).toEqual(fromYaml.timeline.map((i) => i.id));
    expect(fromHeader.timeline.map((i) => i.id)).toEqual(['pilotBoarded', 'vtsCall', 'steeringFail', 'ecrPrompt', 'emergencySteering', 'speedCoach', 'end']);
    expect(fromHeader.timeline.map((i) => i.actions.map((a) => a.kind))).toEqual(fromYaml.timeline.map((i) => i.actions.map((a) => a.kind)));
    expect(fromHeader.route!.waypoints.map((w) => w.name)).toEqual(fromYaml.route!.waypoints.map((w) => w.name));
    expect(fromHeader.route!.waypoints).toHaveLength(4);
    expect(fromHeader.zones.map((z) => [z.id, z.type])).toEqual(fromYaml.zones.map((z) => [z.id, z.type]));
    expect(fromHeader.zones).toHaveLength(3);
    expect(fromHeader.assessment.ruleCount).toBe(fromYaml.assessment.ruleCount);
    expect(fromHeader.assessment.ruleCount).toBe(13);
    expect(fromHeader.assessment.required.map((q) => q.id)).toEqual(fromYaml.assessment.required.map((q) => q.id));
    expect(fromHeader.briefingDoc.roles.map((x) => x.role)).toEqual(fromYaml.briefingDoc.roles.map((x) => x.role));
    // YAML 版有參數引用;核心已替換為數值
    expect(fromYaml.paramRefs).toHaveLength(18);
    expect(fromHeader.paramRefs).toHaveLength(0);
    expect(fromHeader.route!.xtdLimit_m).toEqual({ value: 50 });
    expect(fromHeader.assessment.minCpa_nm).toEqual({ value: 0.05 }); // navigation.cpa.channel_nm(航道專用門檻)
  });
});

describe('核心紀錄(E01_baseline,MMG,300 s)', () => {
  const r = parseJsonLines(e01Text);

  it('無 route 的情境:state 無 route、summary.track 為 null、13 個事件(含 every 重複觸發與語音)', () => {
    expect(r.errors).toEqual([]);
    expect(r.unknownKinds).toEqual({});
    expect(r.header).toMatchObject({ version: 2, scenarioId: 'E01_baseline', dynamics: 'mmg/1' });
    expect(r.header.parameterSet?.version).toBe('2026.10.1');
    expect(r.states).toHaveLength(15);
    for (const s of r.states) {
      expect(s.route).toBeUndefined();
      expect(s.scenario?.id).toBe('E01_baseline');
    }
    expect(r.events).toHaveLength(13);
    expect(r.events.filter((e) => e.kind === 'timelineTriggered' && e.itemId === 'cpaCoach')).toHaveLength(3);
    expect(r.events.filter((e) => e.kind === 'timelineTriggered' && e.itemId === 'cpaCoach').map((e) => e.data?.['fireCount'])).toEqual([1, 2, 3]);
    expect(r.events.filter((e) => e.kind === 'encounterStarted').map((e) => e.targetId)).toEqual(['T1', 'T2']);
    const voice = r.events.find((e) => e.kind === 'voiceScript')!;
    expect(voiceScriptData(voice)).toMatchObject({ role: 'ecr', channel: 'phone' });
    const s = r.summary!;
    expect(s.track).toBeNull();
    expect(s.assessment).toMatchObject({ score: 90, passMark: 70, passed: true });
    expect(s.assessment.bands.map((b) => [b.id, b.state])).toEqual([
      ['minCpa', 'warn'],
      ['rotOver', 'ok'],
    ]);
    expect(s.collisionAvoidance.targets.map((t) => t.targetId)).toEqual(['T1', 'T2']);
    expect(s.events.total).toBe(13);
  });

  it('單獨解析 event 行與 summary 行的物件(推送框共用同一解析器)', () => {
    const lines = e01Text.split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l) as Record<string, unknown>);
    const ev = lines.filter((l) => l['kind'] === 'event').map((l) => parseSimEvent(l['event'], 0.02));
    expect(ev.every((e) => e !== null)).toBe(true);
    const sum = parseAssessmentSummary(lines.find((l) => l['kind'] === 'summary')!['summary']);
    expect(sum?.final).toBe(true);
    expect(sum?.raw['general']).toEqual({ duration_s: 300, freezes: 0, distance_nm: sum!.general.distance_nm, avgSog_kn: sum!.general.avgSog_kn, maxSog_kn: sum!.general.maxSog_kn });
  });
});

// E05 無任何 ${params:…} 引用、無 route/zones/timeline/評估規則:核心紀錄的 header 與 summary 都「省略」parameterSet 鍵
//(不是 null、不是空物件),解析器也不補;PILOT 以 ownDistance 觸發,300 s 內自船距引水站仍 > 1.5 nm,故只有 3 個目標出現。
describe('核心紀錄(E05_pilot_traffic,MMG,300 s,無參數集)', () => {
  const r = parseReplay(e05Text);
  const lines = e05Text.split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l) as Record<string, unknown>);

  it('格式與分段:30 筆 state、3 筆 snapshot、無輸入', () => {
    expect(r.source).toBe('simcore-jsonl');
    expect(r.errors).toEqual([]);
    expect(r.unknownKinds).toEqual({});
    expect(r.states).toHaveLength(30);
    expect(r.snapshots.map((s) => s.tick)).toEqual([500, 5000, 15000]);
    expect(r.inputs).toEqual([]);
    expect(r.footer).toMatchObject({ finalTick: 15000, inputs: 0 });
    expect(r.footer!.stateHash).toMatch(/^[0-9A-F]{64}$/);
  });

  it('header:無參數集時 parameterSet 鍵省略(原始行與解析結果皆 undefined),情境無引用', () => {
    expect(r.header).toMatchObject({ version: 2, dt: 0.02, shipId: 'FSB1', loading: 'full', seed: 20260505, dynamics: 'mmg/1', scenarioId: 'E05_pilot_traffic' });
    const headerLine = lines.find((l) => l['kind'] === 'header')!;
    expect('parameterSet' in headerLine).toBe(false);
    expect(r.header.parameterSet).toBeUndefined();
    expect(JSON.stringify(r.header.scenario)).not.toContain('${params:');
    const fromHeader = parseScenario(JSON.stringify(r.header.scenario), 'data/scenarios/E05_pilot_traffic.yaml');
    const fromYaml = parseScenario(readFileSync(path.join(projectRoot, 'data/scenarios/E05_pilot_traffic.yaml'), 'utf-8'), 'data/scenarios/E05_pilot_traffic.yaml');
    expect(fromHeader.issues).toEqual([]);
    expect(fromYaml.paramRefs).toHaveLength(0);
    expect(fromHeader.paramRefs).toHaveLength(0);
    expect(fromHeader.timeline).toHaveLength(0);
    expect(fromHeader.route).toBeNull();
    expect(fromHeader.zones).toHaveLength(0);
    expect(fromHeader.assessment.minCpa_nm).toEqual({ value: 0.5 });
  });

  it('目標船:情境 4 艘,300 s 內出現 3 艘(PILOT 的 ownDistance 觸發未達),最後一幀 targets 為 T1/T2/T3', () => {
    expect((r.header.scenario as { targets: unknown[] }).targets).toHaveLength(4);
    const last = r.states[r.states.length - 1]!;
    expect(last.targets.map((t) => t.id)).toEqual(['T1', 'T2', 'T3']);
    expect(last.targets.map((t) => t.behaviour)).toEqual(['colreg', 'colreg', 'colreg']);
    expect(last.targets[0]).toMatchObject({ name: 'EVER ORIENT', shipType: 'cargo', aisOn: true });
    expect(last.targets[2]!.ais?.mmsi).toBe(416300103);
    expect(last.scenario).toMatchObject({ id: 'E05_pilot_traffic', phase: 'normal', timelineFired: 0, timelinePending: 0, hardFailed: false, score: 100, ended: false });
    expect(last.scenario?.parameterSetVersion).toBeUndefined();
    expect(last.route).toBeUndefined();
    let prev: OwnShipState | undefined;
    for (const line of lines.filter((l) => l['kind'] === 'state')) {
      const parsed = parseState(line['state'], prev)!;
      expect(parsed.issues).toEqual([]);
      prev = parsed.state;
    }
  });

  it('event 行:11 個事件(colreg ×4、cpaAlarm ×3、encounterStarted ×3、cpaClear ×1),皆屬 traffic/assessment 來源', () => {
    expect(r.events).toHaveLength(11);
    expect(r.events.map((e) => e.seq)).toEqual([...Array(11).keys()]);
    expect(r.events.slice(0, 9).every((e) => e.tick === 50)).toBe(true);
    expect(r.events.map((e) => e.kind)).toEqual([
      'colreg', 'colreg', 'colreg', 'cpaAlarm', 'cpaAlarm', 'cpaAlarm', 'encounterStarted', 'encounterStarted', 'encounterStarted', 'colreg', 'cpaClear',
    ]);
    expect(r.events.slice(0, 3).map((e) => [e.targetId, e.message])).toEqual([['T1', 'headOn/giveWay'], ['T2', 'crossing/standOn'], ['T3', 'crossing/giveWay']]);
    expect(r.events.at(-1)).toMatchObject({ kind: 'cpaClear', targetId: 'T1', t: 148 });
    for (const e of r.events) {
      expect(e.source).toBe(defaultSourceFor(e.kind));
      expect((SIM_EVENT_KINDS as readonly string[]).includes(e.kind)).toBe(true);
      expect(eventLabel(e)).not.toMatch(/\?|undefined|NaN/);
      expect(eventLabel(e).startsWith(e.kind)).toBe(false);
    }
    expect(eventSeverity(r.events[3]!)).not.toBe('info');
  });

  it('summary 行:無參數集時 parameterSet 省略、無規則時 bands/required/parameters 為空、track 為 null、3 筆會遇', () => {
    const s = r.summary!;
    const summaryLine = lines.find((l) => l['kind'] === 'summary')!['summary'] as Record<string, unknown>;
    expect('parameterSet' in summaryLine).toBe(false);
    expect(s.parameterSet).toBeUndefined();
    expect(s).toMatchObject({ version: 1, advisory: true, final: true, scenarioId: 'E05_pilot_traffic', shipId: 'FSB1', loading: 'full' });
    expect(s.assessment).toMatchObject({ score: 100, passMark: 70, passed: true, hardFailed: false, hardFails: [], bands: [], required: [], parameters: [] });
    expect(s.track).toBeNull();
    expect(s.collisionAvoidance).toMatchObject({ encounters: 3, collisions: 0 });
    expect(s.collisionAvoidance.targets.map((t) => [t.targetId, t.situation])).toEqual([['T1', 'headOn/giveWay'], ['T2', 'crossing/standOn'], ['T3', 'crossing/giveWay']]);
    expect(s.events.total).toBe(11);
    expect(s.events.byKind).toEqual({ colreg: 4, cpaAlarm: 3, cpaClear: 1, encounterStarted: 3 });
    expect(s.ukc).toMatchObject({ timeBelowLimit_s: 0, excursions: 0, groundings: 0 });
    expect(s.ukc!.limit_m).toBeUndefined();
    expect(s.manoeuvring.telegraphOrders).toEqual([{ order: 'HAH', from_s: 0, to_s: 300, duration_s: 300 }]);
    expect(parseAssessmentSummary(summaryLine)?.final).toBe(true);
  });
});
