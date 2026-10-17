// 教官站紀錄(SessionRecord)版本 2:核心事件與核心 summary 往返(buildSessionRecord → parseReplay),舊版本 1 紀錄仍可讀。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
import { overlayFromHeaderScenario, overlayFromScenario } from '../src/lib/plot-overlay.js';
import { parseScenario } from '../src/lib/scenario.js';
import { parseReplay } from '../src/lib/record-replay.js';
import { buildSessionRecord, isSessionRecord } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { SimEvent } from '../src/types/event.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const summaryRaw = (JSON.parse(readFileSync(path.resolve(here, 'fixtures/summary-E07.json'), 'utf-8')) as { summary: Record<string, unknown> }).summary;

describe('SessionRecord v2', () => {
  it('coreSummary 與 events 往返;下載後重播檢視可讀回核心評分', () => {
    const events: SimEvent[] = [
      { kind: 'timelineTriggered', tick: 1500, t: 30, source: 'timeline', itemId: 'ecrCheck', visible: false, seq: 0, phase: 'normal' },
      { kind: 'faultInjected', tick: 21000, t: 420, source: 'timeline', itemId: 'steeringFail', visible: false, seq: 1, phase: 'degraded', data: { fault: 'steeringGear' } },
    ];
    const core = parseAssessmentSummary(summaryRaw)!;
    const record = buildSessionRecord(
      {
        mode: 'full',
        source: 'ws://localhost:8765',
        scenario: null,
        shipId: 'FSB1',
        sampleInterval_s: 1,
        states: [defaultState('FSB1'), { ...defaultState('FSB1'), t: 1, tick: 50 }],
        operations: [],
        markers: [],
        commands: [{ seq: 1, t: 0.5, wall: '2026-10-17T00:00:00.000Z', command: { type: 'acknowledge', args: { kind: 'notifyEcr', role: 'master' } } }],
        summary: null,
        events,
        coreSummary: core,
      },
      new Date(Date.UTC(2026, 9, 17)),
    );
    expect(record.version).toBe(2);
    expect(record.coreSummary).toBe(summaryRaw);
    expect(record.events).toHaveLength(2);
    const json = JSON.stringify(record);
    expect(isSessionRecord(JSON.parse(json))).toBe(true);
    const r = parseReplay(json);
    expect(r.source).toBe('instructor-json');
    expect(r.errors).toEqual([]);
    expect(r.events.map((e) => e.kind)).toEqual(['timelineTriggered', 'faultInjected']);
    expect(r.events[1]!.data).toEqual({ fault: 'steeringGear' });
    expect(r.summary?.assessment.score).toBe(85);
    expect(r.summary?.parameterSet?.version).toBe('2026.10.1');
    expect(r.inputs[0]!.command.type).toBe('acknowledge');
  });

  it('無核心 summary 時 coreSummary 為 null;版本 1 紀錄(無 events/coreSummary)仍可讀', () => {
    const record = buildSessionRecord({ mode: 'simple', source: 'demo:FSB1', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: [defaultState('FSB1')], operations: [], markers: [], commands: [], summary: null });
    expect(record.coreSummary).toBeNull();
    expect(record.events).toEqual([]);
    const v1 = { format: 'simosa-brm-instructor-record', version: 1, createdUtc: '2026-10-08T00:00:00Z', mode: 'full', source: 'x', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: [defaultState('FSB1')], operations: [], markers: [], commands: [], summary: null };
    const r = parseReplay(JSON.stringify(v1));
    expect(r.errors).toEqual([]);
    expect(r.events).toEqual([]);
    expect(r.summary).toBeNull();
  });

describe('header.scenario 為已解析的 ScenarioSummary 時的疊圖', () => {
  it('走廊半寬(xtdLimit_m)與速限經下載 → 重播檢視往返不變', () => {
    const yaml = readFileSync(path.resolve(here, 'fixtures/E07_steering_failure.yaml'), 'utf-8');
    const scenario = parseScenario(yaml, 'E07_steering_failure.yaml');
    const live = overlayFromScenario(scenario);
    expect(live.route?.waypoints.map((w) => w.xtdLimit_m)).toBeDefined();
    const record = buildSessionRecord({ mode: 'full', source: 'ws://x', scenario, shipId: 'FSB1', sampleInterval_s: 1, states: [defaultState('FSB1')], operations: [], markers: [], commands: [], summary: null, events: [], coreSummary: null });
    const r = parseReplay(JSON.stringify(record));
    const replay = overlayFromHeaderScenario(r.header.scenario);
    expect(replay.route).toEqual(live.route);
    expect(replay.zones).toEqual(live.zones);
    // 以數值設定走廊的情境
    const sc = parseScenario(yaml.replace('xtdLimit_m: "${params:navigation.xtd.channel_m}"', 'xtdLimit_m: 50'), 'x.yaml');
    const rec2 = buildSessionRecord({ mode: 'full', source: 'ws://x', scenario: sc, shipId: 'FSB1', sampleInterval_s: 1, states: [defaultState('FSB1')], operations: [], markers: [], commands: [], summary: null, events: [], coreSummary: null });
    const o2 = overlayFromHeaderScenario(parseReplay(JSON.stringify(rec2)).header.scenario);
    expect(o2.route?.waypoints.map((w) => w.xtdLimit_m)).toEqual(overlayFromScenario(sc).route?.waypoints.map((w) => w.xtdLimit_m));
    expect(o2.route?.waypoints.some((w) => w.xtdLimit_m === 50)).toBe(true);
  });
});
});
