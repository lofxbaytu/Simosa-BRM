// 重播紀錄解析:SimCore JSON Lines(header/input/state/snapshot/footer)與教官站 SessionRecord JSON。
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { detectRecordFormat, parseJsonLines, parseReplay, stateIndexAt, trackPoints } from '../src/lib/record-replay.js';
import { StateSampler, buildSessionRecord } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import { BrmMarkerStore } from '../src/lib/brm-markers.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const sample = path.resolve(here, '../../../build/records/20261008-120739-E01_baseline.jsonl');

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
});
