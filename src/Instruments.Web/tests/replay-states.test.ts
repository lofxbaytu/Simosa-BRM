// 重播檢視的紀錄載入(lib/replay-states.ts;第三十一輪「重播檢視讀大檔」):
//  - 沒有超過上限的紀錄:與整份讀入(record-replay.parseReplay;每筆保留完整狀態,修改前的作法)相同 —— 狀態筆數與時刻(每筆 = 完整狀態的精簡投影)、
//    事件(含碰墊合併列)、輸入、標記、rewinds、錯誤訊息與行號、tStart / tEnd;
//  - 超過上限:狀態是原序列的子集(每 stride 筆取 1 筆,第一筆與最後一筆保留,不超過上限),其餘全部相同;
//  - 超不超過上限看最終的歷史(審查後修正 DATA-1 / MEM-1 / DATA-4):倒帶前超過上限、倒帶後不到上限的紀錄每一筆都在(含倒帶把狀態全部丟光的情形);
//    最終仍超過上限的,間隔 = 自歷史上一次不超過上限以來到過的最大筆數所需的間隔(以一個獨立的模型對照隨機的 run / restore / reset 結構);
//  - 間隔的秒數取時刻有前進的相鄰兩筆(審查後修正 DATA-2:凍結中 t 不變的狀態占多數時不是 0);
//  - 講評摘要、航跡點、航跡圖上的事件位置一律以**全部**狀態計算(= summarize / trackPoints / eventMarks 對全部狀態的結果),不受降採樣影響;
//  - 分段讀取(Blob,隨機切段、CRLF、BOM)與整份文字的結果相同;
//  - 語料:全部核心紀錄夾具、各版本的教官站紀錄(版本 6 精簡投影、版本 4 以前的完整狀態與 t 倒退的倒帶、帶核心 summary)、
//    倒帶(restore 帶 tick / 不帶 / 沒有 input 行)與重設(reset)、凍結時 t 不變的狀態、損壞的紀錄(隨機破壞,含 t 不遞增)。
//  - ReplayHistoryStore 與預設的陣列存放(ArrayStateStore)在隨機的 加入 / 倒帶 / 清空 序列下行為相同。
import { describe, expect, it, vi } from 'vitest';
import { readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { DebriefAccumulator, summarize } from '../src/lib/debrief.js';
import { eventMarks, eventMarksAt } from '../src/lib/plot-overlay.js';
import { ArrayStateStore, parseReplay, stateIndexAt, trackPoints, type ReplayData } from '../src/lib/record-replay.js';
import { loadReplayBlob, loadReplayText, REPLAY_MAX_STATES, ReplayHistoryStore, replayDisplayState, replayShownCount, replayStride, type ReplayViewData } from '../src/lib/replay-states.js';
import { MAX_STATE_SAMPLES, sampleState, SAMPLED_STATE_KEYS } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { OwnShipState, TelegraphPosition } from '../src/types/state.js';
import { fixtureText, mutate, rng, sessionRecordText, syntheticSessionRecord } from './support/record-corpus.js';

const fixturesDir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures');
const fixtures = readdirSync(fixturesDir).filter((f) => /^record-.*\.jsonl$/.test(f));

/** 2 的次方 */
function isPowerOfTwo(n: number): boolean {
  return Number.isInteger(n) && n >= 1 && (n & (n - 1)) === 0;
}

/** n 筆、每 stride 筆取 1 筆(序號為 stride 倍數)加最後一筆:保留的筆數 */
function shownCount(n: number, stride: number): number {
  return n === 0 ? 0 : Math.floor((n - 1) / stride) + 1 + ((n - 1) % stride !== 0 ? 1 : 0);
}

/** 事件位置比對用的時刻:負無限大、開始、中間、結束、正無限大 */
function markTimes(full: ReplayData): number[] {
  return [Number.NEGATIVE_INFINITY, full.tStart, (full.tStart + full.tEnd) / 2, full.tEnd, Number.POSITIVE_INFINITY];
}

/**
 * 與整份讀入比對:狀態以外的欄位完全相同;摘要、航跡、事件位置 = 由全部狀態算出的結果;保留的狀態是全部狀態(精簡投影)的子序列、不超過上限。
 * 回傳保留的各筆在全部狀態中的序號。
 */
function expectSameExceptStates(view: ReplayViewData, full: ReplayData, label: string): number[] {
  const { states, stateStats, debrief, track, eventPositions, ...rest } = view;
  const { states: all, ...fullRest } = full;
  expect(rest, `${label}:狀態以外的欄位`).toStrictEqual(fullRest);
  expect(stateStats.total, `${label}:全部狀態的筆數`).toBe(all.length);
  expect(stateStats.shown, `${label}:保留的筆數`).toBe(states.length);
  expect(states.length, `${label}:不超過上限`).toBeLessThanOrEqual(stateStats.maxStates);
  expect(isPowerOfTwo(stateStats.stride), `${label}:stride ${stateStats.stride}`).toBe(true);
  expect(debrief, `${label}:摘要以全部狀態累計`).toStrictEqual(all.length > 0 ? summarize(all) : null);
  expect(track, `${label}:航跡點由全部狀態取`).toStrictEqual(trackPoints(all, 1));
  expect(eventPositions, `${label}:事件位置的筆數`).toHaveLength(full.events.length);
  for (const tMax of markTimes(full)) expect(eventMarksAt(view.events, eventPositions, tMax), `${label}:事件標記(t ≤ ${tMax})`).toStrictEqual(eventMarks(full.events, all, tMax));
  // 子序列:依序在全部狀態(精簡投影)中找到每一筆
  const projected = all.map(sampleState);
  const index: number[] = [];
  let j = 0;
  for (const s of states) {
    while (j < projected.length && !(projected[j]!.t === s.t && projected[j]!.tick === s.tick && isDeepStrictEqual(projected[j], s))) j++;
    if (j >= projected.length) throw new Error(`${label}:保留的狀態(t = ${s.t})不在全部狀態裡,或順序不同`);
    index.push(j++);
  }
  return index;
}

/** 沒有降採樣:每一筆都在(筆數、時刻、內容 = 完整狀態的精簡投影)。 */
function expectAllKept(view: ReplayViewData, full: ReplayData, label: string): void {
  expectSameExceptStates(view, full, label);
  expect(view.stateStats.stride, `${label}:沒有降採樣`).toBe(1);
  expect(view.states, `${label}:每一筆狀態`).toStrictEqual(full.states.map(sampleState));
}

/** n 筆放得進 max 的最小間隔(2 的次方;與 lib 的 replayStride 各寫各的) */
function minimalStride(n: number, max: number): number {
  let stride = 1;
  while (shownCount(n, stride) > max) stride *= 2;
  return stride;
}

/**
 * 格式正常的紀錄(狀態依時間遞增)在上限 max 下:序號為 stride 倍數的那些加最後一筆;最終歷史不超過上限時一定每一筆都在(stride 1)。
 * stride:'minimal' = 放得下的最小間隔(沒有倒帶的紀錄);'any' = 不小於最小間隔(倒帶過而最終仍超過上限的紀錄沿用倒帶前的間隔);數字 = 就是這個間隔。
 */
function expectDecimated(text: string, max: number, label: string, expectStride: 'minimal' | 'any' | number = 'minimal'): ReplayViewData {
  const full = parseReplay(text);
  const view = loadReplayText(text, { maxStates: max });
  expectSameExceptStates(view, full, label);
  const n = full.states.length;
  const stride = view.stateStats.stride;
  const expected: number[] = [];
  for (let i = 0; i < n; i += stride) expected.push(i);
  if (n > 0 && (n - 1) % stride !== 0) expected.push(n - 1);
  // 逐筆直接比(內容完全相同的相鄰狀態用子序列比對會對到前一筆)
  expect(view.states, `${label}:保留序號為 ${stride} 的倍數與最後一筆`).toStrictEqual(expected.map((i) => sampleState(full.states[i]!)));
  if (n > 0) {
    expect(view.states[0]!.t, `${label}:第一筆保留`).toBe(full.states[0]!.t);
    expect(view.states[view.states.length - 1]!.t, `${label}:最後一筆保留`).toBe(full.states[n - 1]!.t);
  }
  expect(shownCount(n, stride), `${label}:筆數`).toBe(view.states.length);
  // 超不超過上限看最終的歷史:不到上限就不降採樣,不論倒帶前多長
  if (n <= max) expect(stride, `${label}:最終 ${n} 筆不超過上限 ${max},每一筆都在`).toBe(1);
  if (expectStride === 'minimal') expect(stride, `${label}:間隔是放得下的最小者`).toBe(minimalStride(n, max));
  else if (expectStride === 'any') expect(stride, `${label}:間隔不小於最小者`).toBeGreaterThanOrEqual(minimalStride(n, max));
  else expect(stride, `${label}:間隔`).toBe(expectStride);
  return view;
}

const TELEGRAPH: TelegraphPosition[] = ['FAH', 'HAH', 'SAH', 'DSAH', 'STOP'];

/** 合成的核心狀態(小;每一項講評累計都有變化):k = 第幾筆,25 Hz(tick 每 2)。奇數筆有滿舵與高 ROT,降採樣後留下的偶數筆看不到。 */
function miniState(k: number, tick = 2 * k): OwnShipState {
  const s = defaultState('FSB1');
  s.tick = tick;
  s.t = tick * 0.02;
  s.pos = { lat: 23.5, lon: 120.1, x: 6 * s.t + (k % 2 ? 3 : -3), y: 0.5 * s.t + (k % 2 ? -3 : 3) };
  s.heading = (k * 0.7) % 360;
  s.cog = s.heading;
  s.sog = 6 + (k % 50) * 0.1;
  s.stw = s.sog - 0.3;
  s.rot = k % 2 ? 40 : 2;
  s.rudder = k % 2 ? 35 : (k % 21) - 10;
  s.rudderOrder = (k % 7) * 3;
  s.telegraph = TELEGRAPH[Math.floor(k / 40) % TELEGRAPH.length]!;
  s.depthBelowKeel = k % 2 ? 0.4 : 12 + (k % 9);
  s.flags = { frozen: k % 97 === 96, aground: k % 61 === 59, collision: k % 131 === 129 };
  s.targets = [{ id: 'T1', name: '目標一', mmsi: 416000001, shipType: 'cargo', loa: 120, beam: 20, pos: { lat: 23.6, lon: 120.2, x: 900 - k, y: 400 + k }, heading: 200, cog: 201, sog: 9, rot: 0, range_nm: 1.2, bearing_deg: 40, cpa_nm: 0.4, tcpa_min: 6, aisOn: k % 3 !== 0, lights: 'powerDriven', behaviour: 'hold' }];
  return s;
}

const HEADER = JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', loading: 'full', dt: 0.02, scenario: { id: 'E01_baseline' } });
const stateLine = (s: OwnShipState) => JSON.stringify({ kind: 'state', state: s });
const eventLine = (tick: number, kind: string, extra: Record<string, unknown> = {}) => JSON.stringify({ kind: 'event', tick, event: { kind, tick, t: tick * 0.02, source: 'assessment', ...extra } });

/** n 筆合成狀態的核心紀錄;每 250 筆一筆警告事件(航跡圖會標)與一筆時間表事件。 */
function syntheticJsonl(n: number): string {
  const lines = [HEADER];
  for (let k = 0; k < n; k++) {
    if (k > 0 && k % 250 === 0) lines.push(eventLine(2 * k - 1, 'xtdExceeded', { seq: k, data: { xtd_m: 80 } }), eventLine(2 * k, 'timelineTriggered', { source: 'timeline', itemId: `e${k}`, seq: k + 1 }));
    lines.push(stateLine(miniState(k)));
  }
  lines.push(JSON.stringify({ kind: 'footer', finalTick: 2 * (n - 1), stateHash: 'AB', lines: lines.length + 1, inputs: 0, sha256: 'CD' }));
  return lines.join('\n');
}

/** 倒帶 / 重設的核心紀錄(狀態依時間遞增;各種倒帶點的來源)。 */
function rewindRecords(): Array<[string, string]> {
  const st = (k: number, tick?: number) => stateLine(miniState(k, tick));
  const run = (from: number, to: number) => Array.from({ length: to - from }, (_, i) => st(from + i));
  const input = (tick: number, command: unknown) => JSON.stringify({ kind: 'input', tick, command });
  const snap = (tick: number) => JSON.stringify({ kind: 'snapshot', tick });
  return [
    ['restore 帶 tick(倒帶點在兩筆狀態之間)', [HEADER, ...run(0, 60), eventLine(100, 'xtdExceeded', { seq: 1 }), snap(41), input(120, { type: 'restore', args: { tick: 41 } }), ...run(22, 70), eventLine(130, 'cpaAlarm', { seq: 2, targetId: 'T1' })].join('\n')],
    ['restore 不帶 tick(取最近的 snapshot 行)', [HEADER, ...run(0, 40), snap(30), ...run(40, 80), input(160, { type: 'restore' }), ...run(17, 50)].join('\n')],
    ['沒有 input 行(倒帶點 = 倒帶後第一筆狀態)', [HEADER, ...run(0, 50), eventLine(90, 'xtdExceeded', { seq: 1 }), ...run(30, 90)].join('\n')],
    ['連續兩次倒帶', [HEADER, ...run(0, 80), input(160, { type: 'restore', args: { tick: 100 } }), ...run(51, 120), input(240, { type: 'restore', args: { tick: 20 } }), ...run(11, 45)].join('\n')],
    ['倒帶到第一筆之前(全部丟掉)', [HEADER, ...run(10, 60), ...run(0, 30)].join('\n')],
    ['reset(舊分支全部作廢;重設後、第一筆狀態之前的事件屬於新分支)', [HEADER, ...run(0, 70), eventLine(2, 'fenderContact', { seq: 1, itemId: 'S03', data: { fenderId: 'S03' } }), input(140, { type: 'reset' }), eventLine(2, 'fenderContact', { seq: 1, itemId: 'S03', data: { fenderId: 'S03' } }), ...run(1, 40)].join('\n')],
    ['reset 之後又 restore', [HEADER, ...run(0, 70), input(140, { type: 'reset' }), ...run(1, 60), input(120, { type: 'restore', args: { tick: 60 } }), ...run(31, 66)].join('\n')],
    ['倒帶後核心在倒帶點再寫一筆相同的狀態(真實核心的 restore:同 tick、內容相同的兩筆相鄰)', [HEADER, ...run(0, 60), input(118, { type: 'restore', args: { tick: 40 } }), ...run(20, 71)].join('\n')],
    ['凍結(t 不變的狀態)之後倒帶到凍結的那個時刻', [HEADER, ...run(0, 20), ...Array.from({ length: 12 }, (_, i) => stateLine({ ...miniState(20), heading: i + 1 })), ...run(21, 50), input(100, { type: 'restore', args: { tick: 40 } }), ...run(21, 44)].join('\n')],
  ];
}

describe('重播檢視的紀錄載入:沒有超過上限 = 與整份讀入相同', () => {
  it('上限與教官站抽樣相同(教官站紀錄版本 5 / 6 最多 7201 筆,不會被降採樣)', () => {
    expect(REPLAY_MAX_STATES).toBe(MAX_STATE_SAMPLES);
    expect(REPLAY_MAX_STATES).toBe(7201);
  });

  it('全部核心紀錄夾具(LF / CRLF):狀態筆數與時刻、事件、輸入、rewinds、錯誤訊息、摘要、航跡、事件位置', () => {
    expect(fixtures.length).toBeGreaterThanOrEqual(30);
    for (const f of fixtures) {
      const text = fixtureText(f);
      const full = parseReplay(text);
      expect(full.states.length, f).toBeGreaterThan(0);
      expectAllKept(loadReplayText(text), full, f);
      const crlf = text.replace(/\n/g, '\r\n');
      expectAllKept(loadReplayText(crlf), parseReplay(crlf), `${f}(CRLF)`);
    }
  }, 60_000);

  it('各版本的教官站紀錄(版本 6 精簡投影 / 帶核心 summary / 倒帶;版本 4 以前的完整狀態 / t 倒退的倒帶)', () => {
    const cases: Array<[string, Parameters<typeof syntheticSessionRecord>[0]]> = [
      ['版本 6', { samples: 400, events: 40 }],
      ['版本 6 + 核心 summary', { samples: 300, events: 30, coreSummary: true }],
      ['版本 6 + rewinds[]', { samples: 700, events: 50, rewindAfter: 500 }],
      ['版本 4 以前', { samples: 300, events: 30, legacy: true }],
      ['版本 4 以前 + t 倒退的倒帶', { samples: 700, events: 50, legacy: true, rewindAfter: 500 }],
    ];
    for (const [label, options] of cases) {
      const text = sessionRecordText(syntheticSessionRecord(options));
      const full = parseReplay(text);
      expect(full.errors, label).toEqual([]);
      expectAllKept(loadReplayText(text), full, label);
    }
    // 2 小時的教官站紀錄(7201 筆 = 上限):一筆不少
    const twoHours = sessionRecordText(syntheticSessionRecord({ events: 60 }));
    const view = loadReplayText(twoHours);
    expect(view.stateStats).toEqual({ total: 7201, shown: 7201, stride: 1, maxStates: 7201, interval_s: 1 });
    expectAllKept(view, parseReplay(twoHours), '2 小時的教官站紀錄');
  }, 60_000);

  it('倒帶與重設:rewinds(倒帶點、丟棄的狀態與事件筆數)、留下的狀態與事件都相同', () => {
    for (const [label, text] of rewindRecords()) {
      const full = parseReplay(text);
      expect(full.rewinds.length, label).toBeGreaterThanOrEqual(1);
      expect(full.errors, label).toEqual([]);
      expectAllKept(loadReplayText(text), full, label);
    }
    // 倒帶點寫在標記的懸停文字裡:數字與整份讀入相同(第一個案例:restore 到 tick 41,丟棄 t > 0.82 的狀態與事件)
    const view = loadReplayText(rewindRecords()[0]![1]);
    expect(view.rewinds).toEqual([{ t: 41 * 0.02, tick: 41, from: 118 * 0.02, droppedStates: 39, droppedEvents: 1 }]);
  });

  it('壞行、截斷、無法辨識、不是教官站紀錄:錯誤訊息與行號相同', () => {
    const lines = fixtureText('record-E07-core.jsonl').trimEnd().split('\n');
    const cases: Array<[string, string, string | null]> = [
      ['壞行', [lines[0], lines[1], 'not json', lines[2], '{"kind":"state","state":5}', '[1]', lines[3]].join('\n'), '第 3 行:JSON 解析失敗'],
      ['最後一行被截斷(核心仍在寫入)', lines.slice(0, 12).join('\n') + '\n' + lines[12]!.slice(0, 200), '第 13 行:JSON 解析失敗'],
      ['開頭有空行', `\n\n${lines.slice(0, 5).join('\n')}\nxx`, '第 8 行:JSON 解析失敗'],
      ['無法辨識', 'hello', '無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)'],
      ['空檔', '', '無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)'],
      ['不是教官站紀錄', '{"format":"simosa-brm-roster",\n"trainees":[]}', '不是教官站紀錄(format 欄位不符)'],
      ['被截斷的教官站紀錄', sessionRecordText(syntheticSessionRecord({ samples: 5, events: 2 })).slice(0, -30), null],
      ['只有事件、沒有狀態', [HEADER, eventLine(100, 'xtdExceeded', { seq: 1 }), eventLine(200, 'hardFail', { seq: 2, itemId: 'grounding' }), 'oops'].join('\n'), '第 4 行:JSON 解析失敗'],
    ];
    for (const [label, text, first] of cases) {
      const full = parseReplay(text);
      const view = loadReplayText(text);
      expectAllKept(view, full, label);
      if (first !== null) expect(view.errors[0], label).toBe(first);
      else expect(view.errors.length, label).toBeGreaterThan(0);
    }
    // 沒有狀態:事件沒有位置,航跡圖上不標(原本 eventMarks 對空的狀態序列也是不標)
    const noStates = loadReplayText(cases[cases.length - 1]![1]);
    expect(noStates.events).toHaveLength(2);
    expect(noStates.eventPositions).toEqual([null, null]);
    expect(eventMarksAt(noStates.events, noStates.eventPositions)).toEqual([]);
    expect(noStates.debrief).toBeNull();
    expect(noStates.track).toEqual([]);
  });

  it('第一行同時像教官站紀錄(format + states)又有 kind 的核心紀錄:兩種格式的建構器各用各的存放,第一行裡的 states 不混進結果', () => {
    const inner = [miniState(900), miniState(901), miniState(902)];
    const first = JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02, format: 'simosa-brm-instructor-record', states: inner });
    const text = [first, ...Array.from({ length: 40 }, (_, k) => stateLine(miniState(k)))].join('\n');
    const full = parseReplay(text);
    expect(full.source).toBe('simcore-jsonl');
    expect(full.states).toHaveLength(40);
    expectAllKept(loadReplayText(text), full, '第一行帶 states');
    expectDecimated(text, 8, '第一行帶 states / 上限 8');
    // 反過來:單行、沒有 kind → 教官站紀錄;states 在 format 之前(先收著、結尾才解析)也一樣
    const rec = JSON.parse(sessionRecordText(syntheticSessionRecord({ samples: 30, events: 5 }))) as Record<string, unknown>;
    const { format, states, ...rest } = rec;
    const late = JSON.stringify({ states, ...rest, format });
    expectAllKept(loadReplayText(late), parseReplay(late), 'states 在 format 之前');
    expectDecimated(late, 8, 'states 在 format 之前 / 上限 8');
  });

  it('分段讀取(Blob;隨機的段長、CRLF、開頭的 BOM)與整份文字相同', async () => {
    const rand = rng(3101);
    const enc = new TextEncoder();
    const texts: Array<[string, string]> = [
      ...fixtures.filter((_, i) => i % 6 === 0).map((f): [string, string] => [f, fixtureText(f)]),
      ['E07(CRLF)', fixtureText('record-E07-core.jsonl').replace(/\n/g, '\r\n')],
      ['教官站紀錄', sessionRecordText(syntheticSessionRecord({ samples: 200, events: 20, rewindAfter: 150 }))],
      ['版本 4 以前 + 倒帶', sessionRecordText(syntheticSessionRecord({ samples: 200, events: 20, legacy: true, rewindAfter: 150 }))],
      ...rewindRecords(),
    ];
    for (const [label, text] of texts) {
      const ref = loadReplayText(text);
      const chunkBytes = 1 + Math.floor(rand() * 3000);
      expect(await loadReplayBlob(new Blob([enc.encode(text)]), { chunkBytes, yieldEveryMs: Number.POSITIVE_INFINITY }), `${label}:每段 ${chunkBytes} 位元組`).toStrictEqual(ref);
      // BOM:Blob 解碼時去掉(與 Blob.text() 相同)
      expect(await loadReplayBlob(new Blob([new Uint8Array([0xef, 0xbb, 0xbf]), enc.encode(text)]), { chunkBytes: 4096, yieldEveryMs: Number.POSITIVE_INFINITY }), `${label}:BOM`).toStrictEqual(ref);
      // 降採樣時也一樣
      expect(await loadReplayBlob(new Blob([enc.encode(text)]), { chunkBytes, yieldEveryMs: Number.POSITIVE_INFINITY, maxStates: 5 }), `${label}:上限 5`).toStrictEqual(loadReplayText(text, { maxStates: 5 }));
    }
  }, 60_000);
});

describe('重播檢視的紀錄載入:超過上限才降採樣', () => {
  it('剛好等於上限不降採樣;多一筆起每 2 筆取 1 筆(保留第一筆與最後一筆),再超過就再對半', () => {
    const at = expectDecimated(syntheticJsonl(64), 64, '64 筆 / 上限 64');
    expect(at.stateStats).toMatchObject({ total: 64, shown: 64, stride: 1 });
    const over = expectDecimated(syntheticJsonl(65), 64, '65 筆 / 上限 64');
    expect(over.stateStats).toMatchObject({ total: 65, shown: 33, stride: 2 });
    expect(over.states.map((s) => s.tick).slice(0, 4)).toEqual([0, 4, 8, 12]);
    const odd = expectDecimated(syntheticJsonl(66), 64, '66 筆 / 上限 64');
    // 最後一筆(序號 65)不是 2 的倍數:另外保留
    expect(odd.stateStats).toMatchObject({ total: 66, shown: 34, stride: 2 });
    expect(odd.states.slice(-2).map((s) => s.tick)).toEqual([128, 130]);
    for (const [n, max] of [[1000, 64], [1000, 100], [129, 64], [130, 64], [4097, 16], [9000, 4], [333, 5], [20, 4], [5, 4]] as const) expectDecimated(syntheticJsonl(n), max, `${n} 筆 / 上限 ${max}`);
  }, 60_000);

  it('預設上限(7201 筆):25 Hz 的核心紀錄 7202 筆起每 2 筆取 1 筆,資訊用的間隔 = 相鄰兩筆的時間', () => {
    const exact = loadReplayText(syntheticJsonl(7201));
    expect(exact.stateStats).toMatchObject({ total: 7201, shown: 7201, stride: 1, maxStates: 7201 });
    expect(exact.stateStats.interval_s).toBeCloseTo(0.04, 9);
    const text = syntheticJsonl(7202);
    const view = expectDecimated(text, REPLAY_MAX_STATES, '7202 筆');
    expect(view.stateStats.total).toBe(7202);
    expect(view.stateStats.stride).toBe(2);
    expect(view.stateStats.shown).toBe(3602);
    expect(view.stateStats.interval_s).toBeCloseTo(0.08, 9);
    // 30,000 筆(20 分鐘):每 8 筆取 1 筆
    const long = expectDecimated(syntheticJsonl(30_000), REPLAY_MAX_STATES, '30,000 筆');
    expect(long.stateStats).toMatchObject({ total: 30_000, shown: 3751, stride: 8 });
    expect(long.stateStats.interval_s).toBeCloseTo(0.32, 9);
    expect(long.tEnd).toBe(29_999 * 0.04);
  }, 60_000);

  it('摘要、航跡、事件位置以全部狀態計算:只用留下來的狀態算會得到不同的結果', () => {
    const text = syntheticJsonl(2000);
    const full = parseReplay(text);
    const view = expectDecimated(text, 100, '2000 筆 / 上限 100');
    expect(view.stateStats.stride).toBe(32);
    const all = summarize(full.states);
    const kept = summarize(view.states);
    // 奇數筆才有滿舵、高 ROT、低 UKC:留下來的(序號為 32 的倍數與最後一筆 1999)只碰得到最後那一筆
    expect(view.debrief).toStrictEqual(all);
    expect(all.samples).toBe(2000);
    expect(all.hardOverCount).toBe(1000);
    expect(kept.hardOverCount).toBe(1);
    expect(all.timeRotOverLimit_s).toBeGreaterThan(30);
    expect(kept.timeRotOverLimit_s).toBeLessThan(2);
    expect(all.distance_nm).toBeGreaterThan(kept.distance_nm * 5);
    expect(all.telegraphChanges).toBe(49);
    // 航跡:全部狀態每 1 秒一點(留下來的狀態相隔 1.28 秒,由它們取不到每秒一點)
    expect(view.track).toHaveLength(trackPoints(full.states, 1).length);
    expect(view.track.length).toBeGreaterThan(trackPoints(view.states, 1).length);
    // 事件位置:t = 9.98 的警告事件落在序號 249 的狀態(不在留下來的狀態裡)
    const i = view.events.findIndex((e) => e.kind === 'xtdExceeded');
    const s = full.states[stateIndexAt(full.states, view.events[i]!.t)]!;
    expect(s.tick).toBe(498);
    expect(view.states.some((x) => x.tick === 498)).toBe(false);
    expect(view.eventPositions[i]).toEqual({ x: s.pos.x, y: s.pos.y });
    expect(eventMarksAt(view.events, view.eventPositions)).toStrictEqual(eventMarks(full.events, full.states));
    expect(eventMarksAt(view.events, view.eventPositions)).not.toEqual(eventMarks(view.events, view.states));
  });

  it('夾具與各版本的教官站紀錄在小的上限下:狀態是子集(第一筆與最後一筆保留),其餘全部相同', () => {
    for (const f of fixtures) for (const max of [4, 5, 7]) expectDecimated(fixtureText(f), max, `${f} / 上限 ${max}`);
    for (const options of [{ samples: 400, events: 40 }, { samples: 400, events: 40, coreSummary: true }, { samples: 400, events: 40, legacy: true }]) {
      const text = sessionRecordText(syntheticSessionRecord(options));
      for (const max of [4, 50, 399]) expectDecimated(text, max, `教官站紀錄 ${JSON.stringify(options)} / 上限 ${max}`);
    }
  }, 60_000);

  it('倒帶與重設在降採樣時:rewinds、事件、摘要與整份讀入相同;留下的狀態仍是序號為 stride 倍數的那些加最後一筆', () => {
    for (const [label, text] of rewindRecords()) for (const max of [4, 6, 16, 40]) expectDecimated(text, max, `${label} / 上限 ${max}`, 'any');
    for (const options of [{ samples: 700, events: 50, rewindAfter: 500 }, { samples: 700, events: 50, legacy: true, rewindAfter: 500 }]) {
      const text = sessionRecordText(syntheticSessionRecord(options));
      for (const max of [16, 300]) expectDecimated(text, max, `教官站紀錄 ${JSON.stringify(options)} / 上限 ${max}`, 'any');
    }
    // 重設之後重新開始:新分支很短時每一筆都留(不沿用舊分支的降採樣間隔)
    const st = (k: number) => stateLine(miniState(k));
    const reset = [HEADER, ...Array.from({ length: 500 }, (_, k) => st(k)), JSON.stringify({ kind: 'input', tick: 1000, command: { type: 'reset' } }), ...Array.from({ length: 10 }, (_, k) => st(k + 1))].join('\n');
    const view = expectDecimated(reset, 16, '500 筆後重設、新分支 10 筆 / 上限 16');
    expect(view.rewinds).toEqual([{ t: 0.04, tick: 2, from: 499 * 0.04, droppedStates: 500, droppedEvents: 0 }]);
    expect(view.stateStats).toMatchObject({ total: 10, shown: 10, stride: 1 });
  }, 60_000);

  it('倒帶前超過上限、最終歷史不到上限(審查 DATA-1 / MEM-1 的重現;預設上限):每一筆都在,與整份讀入相同', () => {
    // 25 Hz × 13,500 筆(9 分鐘)→ restore 回 tick 3000(1 分鐘)→ 再 3,000 筆:最終歷史 4,500 筆 < 7,201。
    // 修正前:倒帶只截掉尾端、沿用倒帶前的間隔 → total 4500、shown 2251、stride 2
    const lines = [HEADER];
    for (let k = 1; k <= 13_500; k++) lines.push(stateLine(miniState(k)));
    lines.push(JSON.stringify({ kind: 'input', tick: 27_000, command: { type: 'restore', args: { tick: 3000 } } }));
    for (let k = 1501; k <= 4500; k++) lines.push(stateLine(miniState(k)));
    const text = lines.join('\n');
    const full = parseReplay(text);
    expect(full.states).toHaveLength(4500);
    const view = loadReplayText(text);
    expect(view.rewinds).toEqual([{ t: 60, tick: 3000, from: 540, droppedStates: 12_000, droppedEvents: 0 }]);
    expect(view.stateStats).toMatchObject({ total: 4500, shown: 4500, stride: 1, maxStates: 7201 });
    expect(view.stateStats.interval_s).toBeCloseTo(0.04, 9);
    expectAllKept(view, full, '13,500 筆後倒帶、最終 4,500 筆');
  }, 60_000);

  it('倒帶跨過降採樣的間隔(小上限):不到上限 → 每一筆都在(含全部丟光、連續倒帶);仍超過上限 → 沿用倒帶前的間隔', () => {
    const st = (k: number) => stateLine(miniState(k));
    const run = (from: number, to: number) => Array.from({ length: to - from }, (_, i) => st(from + i));
    const restore = (at: number, tick: number) => JSON.stringify({ kind: 'input', tick: at, command: { type: 'restore', args: { tick } } });
    // 2,010 筆(上限 64 時間隔 64)→ 倒帶回第 10 筆 → 再 30 筆:最終 41 筆
    const far = [HEADER, ...run(0, 2010), restore(4018, 20), ...run(11, 41)].join('\n');
    expect(expectDecimated(far, 64, '2,010 筆後倒帶、最終 41 筆 / 上限 64').stateStats).toMatchObject({ total: 41, shown: 41, stride: 1 });
    // 倒帶截到剛好等於上限(64 筆)→ 每一筆都在;截到多一筆(65 筆)就仍超過上限:後段只剩倒帶前間隔(8)的倍數,沿用(最小是 2)
    const atCap = [HEADER, ...run(0, 300), restore(598, 124), ...run(63, 64)].join('\n');
    expect(expectDecimated(atCap, 64, '倒帶截到 63 筆、再 1 筆 = 64 筆 / 上限 64').stateStats).toMatchObject({ total: 64, shown: 64, stride: 1 });
    const overCap = [HEADER, ...run(0, 300), restore(598, 128), ...run(65, 66)].join('\n');
    expect(minimalStride(66, 64)).toBe(2);
    expect(expectDecimated(overCap, 64, '倒帶截到 65 筆、再 1 筆 / 上限 64', 8).stateStats).toMatchObject({ total: 66, stride: 8 });
    // 截到剛好 64 筆(= 上限,前段每一筆都在)再 1 筆 = 65 筆:間隔由 1 重新算起 → 2(不是沿用 8)
    const fromCap = [HEADER, ...run(0, 300), restore(598, 126), ...run(64, 65)].join('\n');
    expect(expectDecimated(fromCap, 64, '倒帶截到 64 筆、再 1 筆 / 上限 64').stateStats).toMatchObject({ total: 65, stride: 2 });
    // 仍超過上限:300 筆(間隔 8)→ 倒帶回第 100 筆 → 再 50 筆 = 151 筆(最小間隔 4):沿用 8,畫面上寫的是實際的間隔
    const still = [HEADER, ...run(0, 300), restore(598, 200), ...run(101, 151)].join('\n');
    expect(minimalStride(151, 64)).toBe(4);
    const stillView = expectDecimated(still, 64, '300 筆後倒帶、最終 151 筆 / 上限 64', 8);
    expect(stillView.stateStats).toMatchObject({ total: 151, shown: shownCount(151, 8), stride: 8 });
    expect(stillView.stateStats.interval_s).toBeCloseTo(0.32, 9);
    // 之後又長過倒帶前的長度:間隔照最大筆數所需的(700 筆 → 16),等於沒有倒帶時的最小間隔
    const regrow = [HEADER, ...run(0, 300), restore(598, 200), ...run(101, 700)].join('\n');
    expect(expectDecimated(regrow, 64, '倒帶後再長到 700 筆 / 上限 64').stateStats).toMatchObject({ total: 700, stride: 16 });
    // 倒帶到第一筆之前(狀態全部丟光;不是重設):之後的歷史重新開始,每一筆都在(審查 DATA-4:這一行原本沒有測試)
    const emptied = [HEADER, ...run(10, 400), ...run(0, 12)].join('\n');
    const emptiedView = expectDecimated(emptied, 16, '390 筆後倒帶到第一筆之前、新分支 12 筆 / 上限 16');
    expect(emptiedView.rewinds).toEqual([{ t: 0, tick: 0, from: 399 * 0.04, droppedStates: 390, droppedEvents: 0 }]);
    expect(emptiedView.stateStats).toMatchObject({ total: 12, shown: 12, stride: 1 });
    // 連續倒帶:500 → 回到 100 → 130 → 回到 10 → 14 筆
    const twice = [HEADER, ...run(0, 500), restore(998, 198), ...run(100, 130), restore(258, 18), ...run(10, 14)].join('\n');
    expect(expectDecimated(twice, 16, '連續兩次倒帶、最終 14 筆 / 上限 16').stateStats).toMatchObject({ total: 14, shown: 14, stride: 1 });
    // 回到上限以內之後又超過:間隔由 1 重新算起(不沿用第一次超過時的 32)
    const again = [HEADER, ...run(0, 500), restore(998, 18), ...run(10, 40)].join('\n');
    expect(expectDecimated(again, 16, '500 筆後倒帶到 10 筆、再長到 40 筆 / 上限 16').stateStats).toMatchObject({ total: 40, stride: 4 });
  }, 60_000);

  it('隨機的 run / restore / reset 結構(固定種子)× 各種上限:最終不到上限的每一筆都在;間隔與獨立的模型相同', () => {
    const rand = rng(3104);
    const int = (lo: number, hi: number) => lo + Math.floor(rand() * (hi - lo + 1));
    let rewound = 0;
    let backUnderCap = 0;
    let keptCoarser = 0;
    for (let round = 0; round < 160; round++) {
      const max = [4, 5, 8, 16, 64][round % 5]!;
      const lines = [HEADER];
      /** 模型:目前歷史各筆的 tick(與建構器的倒帶規則相同:丟掉 tick 大於倒帶點的那些)與間隔 */
      let hist: number[] = [];
      let stride = 1;
      let lowered = false;
      const dropTo = (limitTick: number): void => {
        const before = hist.length;
        hist = hist.filter((tick) => tick <= limitTick);
        if (hist.length === before) return;
        rewound++;
        // 截到上限以內:間隔回到 1;仍超過:沿用
        if (hist.length <= max) {
          if (stride > 1) lowered = true;
          stride = 1;
        }
      };
      const push = (k: number): void => {
        lines.push(stateLine(miniState(k)));
        hist.push(2 * k);
        while (shownCount(hist.length, stride) > max) stride *= 2;
      };
      let k = 1;
      const ops = int(3, 9);
      for (let op = 0; op < ops; op++) {
        const r = rand();
        if (hist.length < 3 || r < 0.5) {
          for (let n = int(1, r < 0.1 ? 400 : 90); n > 0; n--) push(k++);
        } else if (r < 0.92) {
          // 倒帶:回到歷史中的某一筆(偶爾是最後一筆 = 沒有倒帶);有 / 沒有 restore 的 input 行;倒帶後第一筆與倒帶點同 tick(真實核心)或下一個 tick
          const last = hist[hist.length - 1]!;
          const target = hist[int(0, hist.length - 1)]!;
          const withInput = rand() < 0.7;
          const sameTick = rand() < 0.4;
          if (withInput) lines.push(JSON.stringify({ kind: 'input', tick: last, command: { type: 'restore', args: { tick: target } } }));
          k = target / 2 + (sameTick ? 0 : 1);
          // 建構器:tick 比前一筆小才是倒帶;倒帶點 = restore 要求的 tick(有 input 行)或倒帶後第一筆狀態的 tick
          if (2 * k < last) dropTo(withInput ? target : 2 * k);
          push(k++);
        } else if (hist[hist.length - 1]! > 2) {
          // 重設:舊分支全部作廢
          lines.push(JSON.stringify({ kind: 'input', tick: hist[hist.length - 1], command: { type: 'reset' } }));
          dropTo(-1);
          k = 1;
          push(k++);
        }
      }
      const label = `第 ${round} 輪(上限 ${max})`;
      const view = expectDecimated(lines.join('\n'), max, label, stride);
      expect(view.stateStats.total, `${label}:模型的筆數`).toBe(hist.length);
      if (lowered && hist.length <= max) backUnderCap++;
      if (stride > minimalStride(hist.length, max)) keptCoarser++;
    }
    // 涵蓋:有倒帶的、曾超過上限後回到上限以內而且到結尾都沒有再超過的、最終仍超過上限而沿用較大間隔的
    expect(rewound).toBeGreaterThan(100);
    expect(backUnderCap).toBeGreaterThan(10);
    expect(keptCoarser).toBeGreaterThan(10);
  }, 120_000);

  it('凍結(時刻不變的狀態)占多數(審查 DATA-2):間隔取時刻有前進的相鄰兩筆,不是 0;整份都在凍結時才是 0', () => {
    // 核心凍結時每 200 ms 記一筆 tick / t 不變的狀態:航行 100 筆 + 凍結 600 筆 + 航行 100 筆,上限 64 → 每 16 筆取 1 筆
    const lines = [HEADER];
    for (let k = 1; k <= 100; k++) lines.push(stateLine(miniState(k)));
    for (let j = 0; j < 600; j++) lines.push(stateLine({ ...miniState(100), flags: { frozen: true, aground: false, collision: false } }));
    for (let k = 101; k <= 200; k++) lines.push(stateLine(miniState(k)));
    const view = expectDecimated(lines.join('\n'), 64, '航行 100 + 凍結 600 + 航行 100 / 上限 64');
    expect(view.stateStats).toMatchObject({ total: 800, stride: 16 });
    // 留下的 52 筆中,相鄰兩筆時刻相同的(凍結段)占多數:連它們一起取中位數會是 0
    const gaps = view.states.slice(1).map((x, i) => x.t - view.states[i]!.t);
    expect(gaps.filter((g) => g === 0).length).toBeGreaterThan(gaps.length / 2);
    expect(view.stateStats.interval_s).toBeCloseTo(16 * 0.04, 9);
    // 沒有降採樣的紀錄也一樣(間隔只用在說明文字,這裡只確認不是 0)
    expect(loadReplayText(lines.join('\n')).stateStats.interval_s).toBeCloseTo(0.04, 9);
    // 整份紀錄的時刻都相同:沒有任何兩筆時刻不同 → 0(說明文字那時不寫秒數,見 replay-view-load.test.ts)
    const allFrozen = [HEADER, ...Array.from({ length: 200 }, () => stateLine({ ...miniState(7), flags: { frozen: true, aground: false, collision: false } }))].join('\n');
    const frozenView = loadReplayText(allFrozen, { maxStates: 64 });
    expect(frozenView.stateStats).toEqual({ total: 200, shown: shownCount(200, 4), stride: 4, maxStates: 64, interval_s: 0 });
    // 只有一筆、沒有狀態:0
    expect(loadReplayText([HEADER, stateLine(miniState(1))].join('\n')).stateStats.interval_s).toBe(0);
    expect(loadReplayText(HEADER).stateStats).toEqual({ total: 0, shown: 0, stride: 1, maxStates: 7201, interval_s: 0 });
  });

  it('損壞的紀錄(隨機破壞,固定種子;含 t 不遞增):不擲例外,狀態以外與整份讀入相同,留下的狀態是子集且不超過上限', () => {
    // BRM 標記缺 wall 時以現在時刻補上:固定時鐘,兩次解析才可比
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(Date.UTC(2026, 10, 25, 5, 0, 0)));
    try {
      const rand = rng(3102);
      const jsonl = fixtureText('record-E05-core.jsonl').split('\n');
      const mini = syntheticJsonl(40).split('\n');
      // t 不遞增(tick 遞增):倒帶的 filter 會丟掉不在尾端的狀態
      const scrambled = [HEADER, ...Array.from({ length: 30 }, (_, k) => stateLine({ ...miniState(k), t: ((k * 7) % 30) * 0.5 })), stateLine(miniState(3, 20)), ...Array.from({ length: 10 }, (_, k) => stateLine({ ...miniState(40 + k), t: 20 - k }))].join('\n');
      const seeds = [
        sessionRecordText(syntheticSessionRecord({ samples: 12, events: 6 })),
        sessionRecordText(syntheticSessionRecord({ samples: 14, events: 6, legacy: true, rewindAfter: 9 })),
        [jsonl[0], ...jsonl.slice(1, 40), ...jsonl.slice(-3)].join('\n'),
        mini.join('\n'),
        rewindRecords()[0]![1],
        rewindRecords()[5]![1],
        scrambled,
      ];
      let withStates = 0;
      let decimated = 0;
      for (let round = 0; round < 700; round++) {
        let text = seeds[round % seeds.length]!;
        // 前幾輪不破壞(種子本身;t 不遞增的那一份也在內)
        const depth = round < seeds.length ? 0 : 1 + Math.floor(rand() * 3);
        for (let d = 0; d < depth; d++) text = mutate(text, rand);
        const label = `第 ${round} 輪`;
        const full = parseReplay(text);
        expectAllKept(loadReplayText(text), full, label);
        const max = 4 + Math.floor(rand() * 6);
        const view = loadReplayText(text, { maxStates: max });
        expectSameExceptStates(view, full, `${label}(上限 ${max})`);
        if (full.states.length > 0) withStates++;
        if (view.stateStats.stride > 1) decimated++;
      }
      expect(withStates).toBeGreaterThan(300);
      expect(decimated).toBeGreaterThan(200);
    } finally {
      vi.useRealTimers();
    }
  }, 120_000);
});

describe('ReplayHistoryStore', () => {
  it('與陣列存放在隨機的 加入 / 倒帶 / 清空 序列下相同(筆數、第一筆、最後一筆、丟棄筆數;摘要、航跡、位置查詢)', () => {
    const rand = rng(3103);
    for (let round = 0; round < 60; round++) {
      const max = [4, 5, 8, 31, 1000][round % 5]!;
      const store = new ReplayHistoryStore(max);
      const ref = new ArrayStateStore();
      let k = 0;
      let tick = 0;
      const ops = 200 + Math.floor(rand() * 400);
      for (let i = 0; i < ops; i++) {
        const r = rand();
        if (r < 0.9) {
          // 多數依序;偶爾 t 不遞增(損壞的紀錄)
          tick += 1 + Math.floor(rand() * 3);
          const s = miniState(k++, tick);
          if (round % 4 === 3 && rand() < 0.05) s.t = rand() * tick * 0.02;
          store.push(s);
          ref.push(s);
        } else if (r < 0.985) {
          const limit = rand() < 0.2 ? -1 : rand() * tick * 0.02 * 1.1;
          expect(store.dropAfter(limit), `第 ${round} 輪:丟棄筆數`).toBe(ref.dropAfter(limit));
        } else {
          store.clear();
          ref.clear();
        }
        expect(store.length).toBe(ref.length);
        const [f, l] = [ref.first(), ref.last()];
        expect(store.first()).toEqual(f ? { t: f.t, tick: f.tick } : undefined);
        expect(store.last()).toEqual(l ? { t: l.t, tick: l.tick } : undefined);
      }
      const all = ref.finish();
      const kept = store.finish();
      expect(store.finished).toBe(true);
      expect(kept.length).toBeLessThanOrEqual(max);
      const stride = store.currentStride;
      // 不得有無聲的降採樣:少了狀態時間隔一定大於 1
      if (stride === 1) expect(kept, `第 ${round} 輪:間隔 1 = 每一筆都在`).toStrictEqual(all.map(sampleState));
      else expect(kept.length, `第 ${round} 輪:間隔 ${stride}`).toBeLessThan(all.length);
      if (round % 4 !== 3) {
        // 時刻依序的歷史:不超過上限就每一筆都在(不論先前多長、倒帶幾次);超過時是序號為間隔倍數的那些加最後一筆
        if (all.length <= max) expect(stride, `第 ${round} 輪:${all.length} 筆不超過上限 ${max}`).toBe(1);
        expect(stride).toBeGreaterThanOrEqual(replayStride(all.length, max));
        const want: number[] = [];
        for (let i = 0; i < all.length; i += stride) want.push(i);
        if (all.length > 0 && (all.length - 1) % stride !== 0) want.push(all.length - 1);
        expect(kept, `第 ${round} 輪:序號為 ${stride} 的倍數與最後一筆`).toStrictEqual(want.map((i) => sampleState(all[i]!)));
      }
      // 子序列
      let j = 0;
      for (const s of kept) {
        while (j < all.length && !(all[j]!.t === s.t && all[j]!.tick === s.tick)) j++;
        expect(j, `第 ${round} 輪:留下的狀態在全部狀態裡`).toBeLessThan(all.length);
        j++;
      }
      expect(store.summarize()).toStrictEqual(summarize(all));
      expect(store.trackPoints(1)).toStrictEqual(trackPoints(all, 1));
      expect(store.trackPoints(0.5)).toStrictEqual(trackPoints(all, 0.5));
      for (let q = 0; q < 20; q++) {
        const t = (rand() * 1.2 - 0.1) * tick * 0.02;
        const i = stateIndexAt(all, t);
        expect(store.positionAt(t)).toEqual(i < 0 ? null : { x: all[i]!.pos.x, y: all[i]!.pos.y });
      }
      store.release();
      expect(store.length).toBe(0);
    }
  }, 60_000);

  it('全部狀態的精簡欄位分塊存放(每塊 4096 筆):跨塊的倒帶(丟尾端、丟中間)與之後的加入都與陣列存放相同', () => {
    const store = new ReplayHistoryStore(64);
    const ref = new ArrayStateStore();
    const both = (s: OwnShipState) => {
      store.push(s);
      ref.push(s);
    };
    for (let k = 0; k < 13_000; k++) both(miniState(k));
    // 丟尾端:跨過兩個塊的邊界(8192、12288)
    expect(store.dropAfter(7000 * 0.04)).toBe(ref.dropAfter(7000 * 0.04));
    expect(store.length).toBe(7001);
    for (let k = 7001; k < 9500; k++) both(miniState(k));
    // t 不遞增:中間一段的 t 很大,倒帶時丟掉的是中間(第 2 塊開頭附近)的那些,後面的往前搬(跨塊複製)
    for (let k = 0; k < 300; k++) both({ ...miniState(9500 + k), t: 1e6 + k });
    for (let k = 9800; k < 10_500; k++) both(miniState(k));
    expect(store.dropAfter(10_400 * 0.04)).toBe(ref.dropAfter(10_400 * 0.04));
    expect(store.length).toBe(ref.length);
    // 0–9499 與 9800–10400 留下(中間 t 很大的 300 筆與 t > 416 的 99 筆丟掉)
    expect(store.length).toBe(10_101);
    for (let k = 10_401; k < 12_400; k++) both(miniState(k));
    const all = ref.finish();
    expect(store.last()).toEqual({ t: all[all.length - 1]!.t, tick: all[all.length - 1]!.tick });
    expect(store.summarize()).toStrictEqual(summarize(all));
    expect(store.trackPoints(1)).toStrictEqual(trackPoints(all, 1));
    for (const t of [0, 163.84, 163.86, 280, 327.66, 327.68, 327.7, 400.01, 495.96, 1e9]) {
      const i = stateIndexAt(all, t);
      expect(store.positionAt(t), `t = ${t}`).toEqual({ x: all[i]!.pos.x, y: all[i]!.pos.y });
    }
    const kept = store.finish();
    expect(kept.length).toBeLessThanOrEqual(64);
    expect(kept[kept.length - 1]).toStrictEqual(sampleState(all[all.length - 1]!));
  }, 60_000);

  it('全部狀態的精簡欄位原樣保存:非有限值、負零、未列在清單裡的車鐘字串、沒有 flags 的狀態', () => {
    const store = new ReplayHistoryStore(4);
    const odd: OwnShipState[] = [];
    for (let k = 0; k < 9000; k++) {
      const s = miniState(k);
      if (k % 1000 === 1) s.depthBelowKeel = Number.POSITIVE_INFINITY;
      if (k % 1000 === 2) s.depthBelowKeel = Number.NaN;
      if (k % 1000 === 3) s.rot = -0;
      if (k % 1000 === 4) (s as { telegraph: string }).telegraph = `X${k}`;
      if (k % 1000 === 5) delete (s as { flags?: unknown }).flags;
      odd.push(s);
      store.push(s);
    }
    expect(store.summarize()).toStrictEqual(summarize(odd));
    const acc = new DebriefAccumulator();
    for (const s of odd) acc.push(s);
    expect(store.summarize()).toStrictEqual(acc.summary());
    expect(store.length).toBe(9000);
    expect(store.finish().length).toBeLessThanOrEqual(4);
  }, 60_000);

  it('上限太小(< 4)時拒絕', () => {
    expect(() => new ReplayHistoryStore(3)).toThrow(RangeError);
  });

  it('replayShownCount / replayStride:序號為間隔倍數的筆數加最後一筆;放得進上限的最小間隔', () => {
    expect([0, 1, 2, 3, 4, 5].map((n) => replayShownCount(n, 2))).toEqual([0, 1, 2, 2, 3, 3]);
    expect(replayShownCount(7202, 2)).toBe(3602);
    expect(replayShownCount(180_000, 32)).toBe(5626);
    expect([0, 1, 7200, 7201].map((n) => replayStride(n, 7201))).toEqual([1, 1, 1, 1]);
    expect([7202, 14_401, 14_402, 28_801, 28_802, 180_000, 360_000].map((n) => replayStride(n, 7201))).toEqual([2, 2, 4, 4, 8, 32, 64]);
    for (const max of [4, 5, 16, 64, 7201]) for (const n of [0, 1, max - 1, max, max + 1, 2 * max, 2 * max + 1, 10 * max + 3, 1000 * max]) expect(replayStride(n, max), `${n} 筆 / 上限 ${max}`).toBe(minimalStride(n, max));
  });

  it('讀取期間留著的筆數有上限(前段全留 + 後段對半):倒帶回到上限以內時每一筆都在,不必重讀', () => {
    const max = 100;
    const store = new ReplayHistoryStore(max);
    const all: OwnShipState[] = [];
    const heldNow = () => (store as unknown as { kept: unknown[] }).kept.length;
    let heldPeak = 0;
    for (let k = 0; k < 20_000; k++) {
      const s = miniState(k);
      all.push(s);
      store.push(s);
      heldPeak = Math.max(heldPeak, heldNow());
    }
    // 前段 100 筆 + 後段不到 100 筆
    expect(heldPeak).toBeLessThanOrEqual(2 * max);
    expect(heldNow()).toBeGreaterThanOrEqual(max);
    expect(store.currentStride).toBe(replayStride(20_000, max));
    // 截到比上限多一筆:仍超過,間隔沿用;截到剛好等於上限、再截到第 61 筆:前段每一筆都還在,間隔回到 1
    const strideBefore = store.currentStride;
    expect(store.dropAfter(max * 0.04)).toBe(20_000 - (max + 1));
    expect(store.currentStride).toBe(strideBefore);
    expect(store.dropAfter((max - 1) * 0.04)).toBe(1);
    expect(store.length).toBe(max);
    expect(store.currentStride).toBe(1);
    expect(store.dropAfter(60 * 0.04)).toBe(max - 61);
    expect(store.currentStride).toBe(1);
    store.push(miniState(61));
    const kept = store.finish();
    expect(kept).toStrictEqual([...all.slice(0, 62)].map(sampleState));
    // finish() 之後只留顯示的那些
    expect(heldNow()).toBe(62);
  }, 60_000);

  it('t 不遞增的損壞紀錄:倒帶丟的是中間、後段對半過的狀態前移到前段時,不說每一筆都在(間隔沿用,不是無聲的降採樣)', () => {
    const max = 8;
    const store = new ReplayHistoryStore(max);
    const ref = new ArrayStateStore();
    const both = (s: OwnShipState) => {
      store.push(s);
      ref.push(s);
    };
    // 前 6 筆的 t 很大(倒帶時丟掉),之後 40 筆正常:間隔到 8,後段只留 8 的倍數
    for (let k = 0; k < 6; k++) both({ ...miniState(k), t: 1e6 + k });
    for (let k = 6; k < 46; k++) both(miniState(k));
    expect(store.currentStride).toBe(8);
    // 丟 t > 0.6:留下 k = 6…15 的 10 筆(> 8,間隔沿用);再丟 t > 0.44:留下 k = 6…11 的 6 筆(≤ 8),但它們原本在後段、多數已被對半掉
    expect(store.dropAfter(15 * 0.04)).toBe(ref.dropAfter(15 * 0.04));
    expect(store.length).toBe(10);
    expect(store.currentStride).toBe(8);
    expect(store.dropAfter(11 * 0.04)).toBe(ref.dropAfter(11 * 0.04));
    expect(store.length).toBe(6);
    expect(store.currentStride).toBe(8);
    both(miniState(12));
    const all = ref.finish();
    const kept = store.finish();
    expect(all).toHaveLength(7);
    expect(kept.length).toBeLessThan(all.length);
    expect(store.currentStride).toBeGreaterThan(1);
    // 最後一筆仍保留;留下的是全部狀態的子集
    expect(kept[kept.length - 1]).toStrictEqual(sampleState(all[all.length - 1]!));
    for (const s of kept) expect(all.some((x) => x.tick === s.tick)).toBe(true);
  });
});

describe('顯示用的狀態(精簡投影 → 元件的 OwnShipState)', () => {
  it('保留的欄位原樣;沒有保留的必填欄位補預設值(元件不讀,但不會拿到 undefined)', () => {
    const full = parseReplay(fixtureText('record-E02_coastal_traffic_strait-core.jsonl')).states[3]!;
    const shown = replayDisplayState(sampleState(full));
    for (const key of SAMPLED_STATE_KEYS) expect(shown[key], key).toStrictEqual(full[key]);
    expect([shown.u, shown.v, shown.r, shown.loading]).toEqual([0, 0, 0, 'full']);
    expect(shown.targets.length).toBe(full.targets.length);
    expect(shown.targets.length).toBeGreaterThan(0);
    shown.targets.forEach((t, i) => {
      const src = full.targets[i]!;
      expect([t.id, t.name, t.heading, t.cog, t.sog, t.loa, t.aisOn, t.range_nm, t.bearing_deg, t.cpa_nm, t.tcpa_min, t.pos.x, t.pos.y]).toEqual([src.id, src.name, src.heading, src.cog, src.sog, src.loa, src.aisOn, src.range_nm, src.bearing_deg, src.cpa_nm, src.tcpa_min, src.pos.x, src.pos.y]);
      expect([t.mmsi, t.shipType, t.beam, t.rot, t.lights, t.behaviour, t.pos.lat, t.pos.lon]).toEqual([0, 'other', 0, 0, 'powerDriven', 'hold', 0, 0]);
    });
  });
});
