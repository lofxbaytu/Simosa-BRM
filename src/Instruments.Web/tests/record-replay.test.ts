// 重播紀錄解析:SimCore JSON Lines(header/input/state/snapshot/event/summary/footer)與教官站 SessionRecord JSON;
// 舊紀錄(無 event/summary 行、header 無 parameterSet)仍可解析,未知 kind 只計數。
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { detectRecordFormat, eventIndexAt, parseJsonLines, parseReplay, stateIndexAt, trackPoints } from '../src/lib/record-replay.js';
import { StateSampler, buildSessionRecord } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import { BrmMarkerStore } from '../src/lib/brm-markers.js';
import { cpaAlarmTargets } from '../src/lib/targets.js';
import { rewindTitle } from '../src/components/instructor/replay-view.js';
import type { SimEvent } from '../src/types/event.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const sample = path.resolve(here, '../../../build/records/20261008-120739-E01_baseline.jsonl');
const summaryLine = readFileSync(path.resolve(here, 'fixtures/summary-E07.json'), 'utf-8').replace(/\s*\n\s*/g, '');

function stateLine(t: number, x: number, y: number, extra: Record<string, unknown> = {}): string {
  return JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x, y }, ...extra } });
}

const header = JSON.stringify({
  kind: 'header',
  version: 1,
  createdUtc: '2026-10-08T12:07:39Z',
  shipId: 'FSB1',
  shipName: 'FS BITUMEN NO.1',
  loading: 'ballast',
  seed: 20260101,
  dt: 0.02,
  dynamics: 'placeholder-nomoto/1',
  scenario: { id: 'E01_baseline', name: '麥寮外海開闊水域 — 基線' },
});

describe('parseJsonLines', () => {
  it('解析 header / input / state / snapshot / footer,壞行記入 errors', () => {
    const text = [
      header,
      stateLine(0.04, 0, 0.15),
      '{"kind":"input","tick":118,"command":{"type":"rudder","value":12.5}}',
      stateLine(2.4, 1, 10),
      'garbage',
      '{"kind":"snapshot","tick":500,"snapshot":{"tick":500}}',
      '{"kind":"input","tick":600,"command":{"type":"warp"}}',
      stateLine(12, 5, 50, { flags: { frozen: true } }),
      '{"kind":"footer","finalTick":600,"stateHash":"ABC","lines":7,"inputs":1,"sha256":"DEF"}',
      '',
    ].join('\n');
    const r = parseJsonLines(text);
    expect(r.source).toBe('simcore-jsonl');
    expect(r.header).toMatchObject({ dt: 0.02, shipId: 'FSB1', scenarioId: 'E01_baseline', scenarioName: '麥寮外海開闊水域 — 基線', dynamics: 'placeholder-nomoto/1' });
    expect(r.states).toHaveLength(3);
    expect(r.inputs).toEqual([{ tick: 118, t: 2.36, command: { type: 'rudder', value: 12.5 } }]);
    expect(r.snapshots).toEqual([{ tick: 500, t: 10 }]);
    expect(r.footer).toEqual({ finalTick: 600, stateHash: 'ABC', lines: 7, inputs: 1, sha256: 'DEF' });
    expect(r.errors).toEqual(['第 5 行:JSON 解析失敗', '第 7 行:input 缺 tick 或 command 無效']);
    expect(r.tStart).toBe(0.04);
    expect(r.tEnd).toBe(12);
    expect(r.states[2]!.flags?.frozen).toBe(true);
    // 舊紀錄:無 event/summary 行
    expect(r.events).toEqual([]);
    expect(r.summary).toBeNull();
    expect(r.header.parameterSet).toBeUndefined();
    expect(r.unknownKinds).toEqual({});
  });

  it('version 2:event 行(舊 TrafficEvent 與新 SimEvent)、summary 行、header.parameterSet;未知 kind 只計數', () => {
    const header2 = JSON.stringify({
      kind: 'header',
      version: 2,
      shipId: 'FSB1',
      dt: 0.02,
      scenario: { id: 'E07_steering_failure', name: 'E07', origin: { lat: 23.78, lon: 120.1 }, route: { waypoints: [{ name: 'WP1', lat: 23.78, lon: 120.1 }, { name: 'WP2', lat: 23.796, lon: 120.122 }] } },
      parameterSet: { id: 'simosa-company-procedures', version: '2026.10.1', path: 'data/parameters/company-procedures.yaml', sha256: 'abc', resolved: [{ path: 'assessment.minCpa_nm', key: 'navigation.cpa.coastal_nm', value: 0.5, unit: 'nm', source: 'DWO-007', revision: '3' }] },
    });
    const text = [
      header2,
      stateLine(0, 0, 0),
      '{"kind":"event","tick":6000,"event":{"kind":"cpaAlarm","tick":6000,"t":120,"targetId":"T1","cpa_nm":0.4,"tcpa_min":8}}',
      '{"kind":"event","tick":21000,"event":{"kind":"faultInjected","tick":21000,"t":420,"source":"timeline","itemId":"steeringFail","phase":"degraded","visible":false,"seq":5,"data":{"fault":"steeringGear"}}}',
      '{"kind":"event","tick":21050,"event":"bad"}',
      '{"kind":"telemetry","tick":1}',
      '{"kind":"telemetry","tick":2}',
      stateLine(600, 100, 100),
      summaryLine,
      '{"kind":"footer","finalTick":30000,"stateHash":"ABC","lines":9,"inputs":0,"sha256":"DEF"}',
    ].join('\n');
    const r = parseJsonLines(text);
    expect(r.errors).toEqual(['第 5 行:event 無法解析']);
    expect(r.unknownKinds).toEqual({ telemetry: 2 });
    expect(r.events.map((e) => [e.kind, e.source, e.t])).toEqual([
      ['cpaAlarm', 'traffic', 120],
      ['faultInjected', 'timeline', 420],
    ]);
    expect(r.events[1]!.itemId).toBe('steeringFail');
    expect(r.summary?.final).toBe(true);
    expect(r.summary?.assessment.score).toBe(85);
    expect(r.summary?.assessment.required[2]).toMatchObject({ id: 'vtsReport', state: 'missed' });
    expect(r.header.version).toBe(2);
    expect(r.header.parameterSet?.version).toBe('2026.10.1');
    expect(r.header.parameterSet?.resolved[0]?.key).toBe('navigation.cpa.coastal_nm');
    expect(r.header.scenarioId).toBe('E07_steering_failure');
    expect(eventIndexAt(r.events, 100)).toBe(-1);
    expect(eventIndexAt(r.events, 120)).toBe(0);
    expect(eventIndexAt(r.events, 9999)).toBe(1);
  });

  it('實際的 SimCore 紀錄檔(build/records)可解析', () => {
    if (!existsSync(sample)) return; // 建置機器上可能沒有紀錄檔
    const r = parseReplay(readFileSync(sample, 'utf-8'));
    expect(r.errors).toEqual([]);
    expect(r.header.scenarioId).toBe('E01_baseline');
    expect(r.states.length).toBeGreaterThan(100);
    expect(r.inputs.length).toBe(r.footer?.inputs);
    expect(r.footer?.finalTick).toBe(400);
  });
});

describe('detectRecordFormat / parseReplay', () => {
  it('辨識 JSON Lines 與教官站 JSON', () => {
    expect(detectRecordFormat(header + '\n' + stateLine(0, 0, 0))).toBe('jsonl');
    expect(detectRecordFormat(header)).toBe('jsonl');
    expect(detectRecordFormat('{"format":"simosa-brm-instructor-record",\n"states":[]}')).toBe('json');
    expect(detectRecordFormat('hello')).toBe('unknown');
    expect(parseReplay('hello').errors[0]).toMatch(/無法辨識/);
  });

  it('教官站 SessionRecord:狀態、指令與 BRM 標記', () => {
    const sampler = new StateSampler(1);
    for (let i = 0; i <= 125; i++) sampler.push({ ...defaultState('FSB2'), t: i / 25, pos: { lat: 0, lon: 0, x: i / 25, y: 0 } });
    expect(sampler.all().length).toBe(6);
    const markers = new BrmMarkerStore();
    markers.add({ category: 'communication', t: 2, note: '未複誦', wall: new Date(0) });
    const record = buildSessionRecord({
      mode: 'simple',
      source: 'ws://localhost:8765',
      scenario: null,
      shipId: 'FSB2',
      sampleInterval_s: 1,
      states: sampler.all(),
      operations: [{ seq: 1, t: 1, wall: '1970-01-01T00:00:00.000Z', kind: 'rudder', label: '舵令 S 10°', value: 10 }],
      markers: markers.all(),
      commands: [{ seq: 1, t: 0, wall: '1970-01-01T00:00:00.000Z', command: { type: 'freeze' }, ack: { ok: true, tick: 0 } }],
      summary: null,
    });
    const r = parseReplay(JSON.stringify(record));
    expect(r.source).toBe('instructor-json');
    expect(r.errors).toEqual([]);
    expect(r.states).toHaveLength(6);
    expect(r.header.shipId).toBe('FSB2');
    expect(r.inputs).toEqual([{ tick: 0, t: 0, command: { type: 'freeze' }, ack: { ok: true } }]);
    expect(r.markers).toHaveLength(1);
    expect(r.markers[0]).toMatchObject({ category: 'communication', t: 2, note: '未複誦' });
    expect(r.operations).toHaveLength(1);
    expect(r.tEnd).toBe(5);
    expect(parseReplay('{"format":"other"}').errors[0]).toMatch(/不是教官站紀錄/);
  });
});

describe('stateIndexAt / trackPoints', () => {
  const states = [0, 1, 2, 3, 4].map((t) => ({ ...defaultState('FSB1'), t, pos: { lat: 0, lon: 0, x: t, y: 0 } }));
  it('二分搜尋取 t 之前最後一筆', () => {
    expect(stateIndexAt([], 1)).toBe(-1);
    expect(stateIndexAt(states, -1)).toBe(0);
    expect(stateIndexAt(states, 0)).toBe(0);
    expect(stateIndexAt(states, 2.5)).toBe(2);
    expect(stateIndexAt(states, 3)).toBe(3);
    expect(stateIndexAt(states, 99)).toBe(4);
  });
  it('航跡點依間隔抽樣', () => {
    expect(trackPoints(states, 2).map((p) => p.t)).toEqual([0, 2, 4]);
    expect(trackPoints(states, 1)).toHaveLength(5);
  });

describe('倒帶分段(restore/reset)', () => {
  const evLine = (t: number, kind: string, seq: number) => JSON.stringify({ kind: 'event', tick: Math.round(t / 0.02), event: { kind, tick: Math.round(t / 0.02), t, source: 'instructor', seq } });

  it('核心紀錄:倒帶時丟棄 t > 倒帶點的舊分支狀態與事件,航跡與事件索引只含最終歷史', () => {
    const text = [
      header,
      stateLine(1, 0, 10),
      stateLine(2, 0, 20),
      evLine(2, 'frozen', 0),
      stateLine(3, 0, 30),
      evLine(3.5, 'faultInjected', 1),
      stateLine(4, 0, 40),
      '{"kind":"input","tick":200,"command":{"type":"restore","args":{"tick":100}}}',
      stateLine(2.04, 0, 20.4), // 倒帶到 tick 100(t = 2)後重新推進
      stateLine(3, 5, 30),
      evLine(3, 'resumed', 1),
      stateLine(4, 10, 40),
      '{"kind":"input","tick":200,"command":{"type":"reset"}}',
      stateLine(0.04, 0, 0.4),
      evLine(0.5, 'message', 0),
      stateLine(1, 0, 10),
      '{"kind":"footer","finalTick":50,"stateHash":"AB","lines":16,"inputs":2,"sha256":"CD"}',
    ].join('\n');
    const r = parseJsonLines(text);
    expect(r.errors).toEqual([]);
    expect(r.rewinds).toEqual([
      { t: 2, tick: 100, from: 4, droppedStates: 2, droppedEvents: 1 }, // 倒帶點 = restore args.tick × dt,而非倒帶後第一筆 state 的 2.04
      { t: 0.04, tick: 2, from: 4, droppedStates: 5, droppedEvents: 2 },
    ]);
    expect(r.states.map((s) => s.t)).toEqual([0.04, 1]);
    expect(r.states.every((s, i, a) => i === 0 || s.t > a[i - 1]!.t)).toBe(true);
    expect(r.events.map((e) => e.kind)).toEqual(['message']);
    expect(r.tEnd).toBe(1);
    expect(trackPoints(r.states, 0.5).map((p) => p.y)).toEqual([0.4, 10]);
    expect(eventIndexAt(r.events, 0.7)).toBe(0);
    expect(eventIndexAt(r.events, 0.2)).toBe(-1);
    expect(r.inputs).toHaveLength(2); // 指令紀錄保留(教官做過的事)
  });

  it('倒帶點取 restore args.tick:BroadcastDivider 區間 (倒帶 tick, 第一筆新 state] 的舊分支事件也丟棄;rewindTitle 直接顯示該 tick', () => {
    const cpa = (t: number, targetId: string, kind = 'cpaAlarm') => JSON.stringify({ kind: 'event', tick: Math.round(t / 0.02), event: { kind, tick: Math.round(t / 0.02), t, source: 'traffic', seq: 0, targetId } });
    const text = [
      header,
      stateLine(2, 0, 20), // tick 100
      cpa(2.02, 'T1'), // tick 101 的舊分支事件
      stateLine(4, 0, 40),
      '{"kind":"input","tick":200,"command":{"type":"restore","args":{"tick":100}}}',
      stateLine(2.04, 0, 20.4), // C# Restore 後第一筆 state 行為 tick 102
      stateLine(3, 5, 30),
    ].join('\n');
    const r = parseJsonLines(text);
    expect(r.errors).toEqual([]);
    expect(r.rewinds).toEqual([{ t: 2, tick: 100, from: 4, droppedStates: 1, droppedEvents: 1 }]);
    expect(r.events).toEqual([]);
    expect([...cpaAlarmTargets(r.events, 3)]).toEqual([]);
    expect(r.states.map((s) => s.t)).toEqual([2, 2.04, 3]);
    expect(rewindTitle(r.rewinds[0]!, r.header.dt)).toBe('倒帶至 00:02(tick 100)← 自 00:04;丟棄 1 筆狀態、1 筆事件');
    // restore 無 args:取最近一筆 snapshot 行的 tick
    const text2 = [header, stateLine(2, 0, 20), '{"kind":"snapshot","tick":100}', cpa(2.02, 'T1'), stateLine(4, 0, 40), '{"kind":"input","tick":200,"command":{"type":"restore"}}', stateLine(2.04, 0, 20.4)].join('\n');
    const r2 = parseJsonLines(text2);
    expect(r2.rewinds).toEqual([{ t: 2, tick: 100, from: 4, droppedStates: 1, droppedEvents: 1 }]);
    // restore 被拒(無狀態倒退):待處理的倒帶 tick 不會套到之後的 reset 倒退
    const text3 = [header, stateLine(2, 0, 20), stateLine(4, 0, 40), '{"kind":"input","tick":200,"command":{"type":"restore","args":{"tick":100}}}', stateLine(6, 0, 60), '{"kind":"input","tick":300,"command":{"type":"reset"}}', stateLine(0.04, 0, 0.4)].join('\n');
    expect(parseJsonLines(text3).rewinds).toEqual([{ t: 0.04, tick: 2, from: 6, droppedStates: 3, droppedEvents: 0 }]);
  });

  it('只有一次倒帶:保留倒帶點之前的歷史與倒帶後的新分支;eventIndexAt 取得倒帶後的事件', () => {
    const text = [header, stateLine(1, 0, 10), evLine(1, 'frozen', 0), stateLine(2, 0, 20), stateLine(3, 0, 30), evLine(3, 'faultInjected', 1), stateLine(4, 0, 40), stateLine(2.04, 0, 20.4), evLine(2.5, 'resumed', 1), stateLine(3, 7, 30), stateLine(5, 9, 50)].join('\n');
    const r = parseJsonLines(text);
    expect(r.states.map((s) => [s.t, s.pos.x])).toEqual([
      [1, 0],
      [2, 0],
      [2.04, 0],
      [3, 7],
      [5, 9],
    ]);
    expect(r.rewinds).toEqual([{ t: 2.04, tick: 102, from: 4, droppedStates: 2, droppedEvents: 1 }]); // 無 input 行:取倒帶後第一筆狀態
    expect(r.events.map((e) => [e.kind, e.t])).toEqual([
      ['frozen', 1],
      ['resumed', 2.5],
    ]);
    expect(eventIndexAt(r.events, 3)).toBe(1);
    expect(stateIndexAt(r.states, 3.5)).toBe(3);
    expect(r.tStart).toBe(1);
    expect(r.tEnd).toBe(5);
  });

  it('教官站紀錄(狀態抽樣含倒帶)同樣分段;事件整批也依倒帶點丟棄舊分支', () => {
    const s = (t: number, x: number) => ({ ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x, y: 0 } });
    const e = (t: number, kind: string, seq: number): SimEvent => ({ kind, tick: Math.round(t / 0.02), t, source: 'instructor', seq, visible: false, phase: 'normal' });
    const record = buildSessionRecord({
      mode: 'full',
      source: 'ws://x',
      scenario: null,
      shipId: 'FSB1',
      sampleInterval_s: 1,
      states: [s(1, 1), s(2, 2), s(3, 3), s(4, 4), s(2, 2), s(3, 30), s(4, 40)],
      operations: [],
      markers: [],
      commands: [],
      summary: null,
      events: [e(1.5, 'frozen', 0), e(3.5, 'faultInjected', 1), e(2.5, 'resumed', 1), e(3.5, 'message', 2)],
      coreSummary: null,
    });
    const r = parseReplay(JSON.stringify(record));
    expect(r.errors).toEqual([]);
    expect(r.rewinds).toEqual([{ t: 2, tick: 100, from: 4, droppedStates: 2, droppedEvents: 0 }]);
    expect(r.states.map((st) => [st.t, st.pos.x])).toEqual([
      [1, 1],
      [2, 2],
      [2, 2],
      [3, 30],
      [4, 40],
    ]);
    expect(r.events.map((ev) => ev.kind)).toEqual(['frozen', 'resumed', 'message']);
  });
});
});
