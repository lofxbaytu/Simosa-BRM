// 測試輔助:練習紀錄的語料(record-stream 的差異測試、講評報告的前後比對)——
//  - 可重現的亂數(mulberry32);
//  - 2 小時規模的合成教官站紀錄:以 E02(交通密度高,6 艘目標船)核心夾具的狀態為樣板,1 Hz × 7201 筆,
//    舵令 / 車鐘 / 水深 / 凍結 / 擱淺 / 碰撞隨時間變化(講評累計的每一項都有非零值);版本 5(精簡投影)或版本 4 以前(完整狀態、
//    倒帶時 t 倒退);
//  - 對一段紀錄文字做隨機破壞(截斷、刪字、插字、換行、空白、重複、對調)。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { BrmMarker } from '../../src/lib/brm-markers.js';
import type { OperationEvent } from '../../src/lib/operation-log.js';
import { parseReplay } from '../../src/lib/record-replay.js';
import { buildSessionRecord, sampleState, sessionRecordChunks, type CommandLogEntry, type SampledRewind, type SampledState, type SessionRecord } from '../../src/lib/session-record.js';
import type { SimEvent } from '../../src/types/event.js';
import type { OwnShipState, TelegraphPosition } from '../../src/types/state.js';

const here = path.dirname(fileURLToPath(import.meta.url));

export function fixtureText(name: string): string {
  return readFileSync(path.join(here, '../fixtures', name), 'utf8');
}

/** 可重現的亂數(mulberry32;回傳 [0, 1))。 */
export function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export interface SyntheticRecordOptions {
  /** 狀態筆數(預設 7201 = 1 Hz × 2 小時,含兩端) */
  samples?: number;
  seed?: number;
  /** true:版本 4 以前的寫法(完整 OwnShipState、version 4、沒有 rewinds 欄;倒帶以 t 倒退表示) */
  legacy?: boolean;
  /** 帶核心 summary(E02 夾具的 summary 原始物件);預設不帶,報告改用狀態累計的本地摘要 */
  coreSummary?: boolean;
  /** 在第幾筆之後倒帶 300 s(0 = 不倒帶):版本 5 記在 rewinds[] 並截掉舊分支,版本 4 以前留下 t 倒退的狀態 */
  rewindAfter?: number;
  /** 事件筆數(預設 400) */
  events?: number;
}

const TELEGRAPH: TelegraphPosition[] = ['FAH', 'HAH', 'SAH', 'DSAH', 'STOP'];

/** 合成的教官站紀錄(物件);同一組選項每次產生相同內容。 */
export function syntheticSessionRecord(options: SyntheticRecordOptions = {}): SessionRecord {
  const n = options.samples ?? 7201;
  const rand = rng(options.seed ?? 20261123);
  const core = parseReplay(fixtureText('record-E02_coastal_traffic_strait-core.jsonl'));
  const templates = core.states;
  const full: OwnShipState[] = [];
  let x = 0;
  let y = 0;
  let heading = 20;
  let rudderOrder = 0;
  for (let k = 0; k < n; k++) {
    const s = structuredClone(templates[k % templates.length]!);
    const t = k;
    if (k % 97 === 0) rudderOrder = Math.round((rand() - 0.5) * 70);
    const rudder = k % 1800 > 1790 ? 35 : rudderOrder * 0.9;
    const sog = 6 + 6 * Math.abs(Math.sin(k / 900)) + rand() * 0.2;
    heading = (heading + rudder * 0.02 + 360) % 360;
    x += sog * 0.5144 * Math.sin((heading * Math.PI) / 180);
    y += sog * 0.5144 * Math.cos((heading * Math.PI) / 180);
    s.t = t;
    s.tick = t * 50;
    s.pos = { ...s.pos, x, y };
    s.heading = heading;
    s.cog = heading;
    s.sog = sog;
    s.stw = sog - 0.4;
    s.rot = rudder * 1.1 + (rand() - 0.5);
    s.rudder = rudder;
    s.rudderOrder = rudderOrder;
    s.telegraph = TELEGRAPH[Math.floor(k / 613) % TELEGRAPH.length]!;
    s.depthBelowKeel = 0.6 + 30 * Math.abs(Math.cos(k / 500)) + rand() * 0.05;
    s.flags = { frozen: k >= 3000 && k < 3060, aground: k >= 5000 && k < 5030, collision: k === 6100 };
    for (const tg of s.targets) {
      tg.range_nm += Math.sin(k / 300) * 0.5;
      tg.cpa_nm = Math.abs(tg.cpa_nm + (rand() - 0.5) * 0.01);
      tg.pos = { ...tg.pos, x: tg.pos.x + k, y: tg.pos.y - k };
    }
    full.push(s);
  }

  const rewindAfter = options.rewindAfter ?? 0;
  let states: SampledState[] = options.legacy ? full : full.map(sampleState);
  const rewinds: SampledRewind[] = [];
  if (rewindAfter > 0 && rewindAfter < n) {
    const back = Math.max(0, rewindAfter - 300);
    if (options.legacy) {
      // 版本 4 以前:舊分支留在 states,倒帶後的狀態 t / tick 倒退
      states = [...states.slice(0, rewindAfter + 1), ...states.slice(back + 1).map((s) => ({ ...s }))];
    } else {
      rewinds.push({ t: back, tick: back * 50, from: rewindAfter, droppedStates: rewindAfter - back, droppedEvents: 3 });
    }
  }

  const nEvents = options.events ?? 400;
  const events: SimEvent[] = [];
  for (let i = 0; i < nEvents; i++) {
    const src = core.events[i % core.events.length]!;
    const t = Math.floor((i * (n - 1)) / nEvents);
    events.push({ ...src, t, tick: t * 50, seq: i });
  }
  const operations: OperationEvent[] = [];
  for (let i = 0; i < 60; i++) operations.push({ seq: i + 1, t: i * 100, wall: `2026-11-23T02:${String(i % 60).padStart(2, '0')}:00.000Z`, kind: i % 2 ? 'rudder' : 'telegraph', label: i % 2 ? `舵令 S ${i}°` : '車鐘 HAH', value: i });
  operations.push({ seq: 61, t: 0, wall: '2026-11-23T01:55:00.000Z', kind: 'planningTime', label: '規劃時間結束(10 分鐘)', value: 10 });
  const markers: BrmMarker[] = [
    { id: 1, category: 'communication', t: 320, tick: 16000, wall: '2026-11-23T02:05:20.000Z', note: '未複誦「右舵 15」', polarity: 'delta' },
    { id: 2, category: 'teamwork', t: 4100, tick: 205000, wall: '2026-11-23T03:08:20.000Z', note: '主動提出疑慮', polarity: 'plus' },
  ];
  const commands: CommandLogEntry[] = [
    { seq: 1, t: 0, wall: '2026-11-23T02:00:00.000Z', command: { type: 'timeScale', value: 1 } },
    { seq: 2, t: 3000, wall: '2026-11-23T02:50:00.000Z', command: { type: 'freeze' }, ack: { ok: true, tick: 150000 } },
    { seq: 3, t: 3060, wall: '2026-11-23T02:51:00.000Z', command: { type: 'resume' }, ack: { ok: true, detail: 'ok' } },
  ];
  const record = buildSessionRecord(
    {
      mode: 'full',
      source: 'ws://127.0.0.1:8765',
      scenario: { id: 'E02_coastal_traffic_strait', name: '沿岸航行:海峽交通(合成 2 小時)', file: 'E02_coastal_traffic_strait.yaml', shipId: 'FSB1', loading: 'full', seed: 20261120 } as unknown as SessionRecord['scenario'],
      shipId: 'FSB1',
      sampleInterval_s: 1,
      states,
      operations,
      markers,
      commands,
      summary: null,
      events,
      coreSummary: options.coreSummary ? core.summary : null,
      startedUtc: '2026-11-23T02:00:00.000Z',
      sessionInfo: null,
      rewinds,
    },
    new Date(Date.UTC(2026, 10, 23, 4, 0, 0)),
  );
  if (options.legacy) {
    record.version = 4;
    delete record.rewinds;
  }
  return record;
}

/** 紀錄物件 → 教官站下載 / 交接的 JSON 文字(與 sessionRecordBlob 的內容相同)。 */
export function sessionRecordText(record: SessionRecord): string {
  return [...sessionRecordChunks(record)].join('');
}

const INSERTS = ['{', '}', '[', ']', '"', ',', ':', '\\', ' ', '\n', '\r', '\t', '\u00a0', '\ufeff', '\u2028', '0', '1', '-', 'e', 'x', 'null', 'true', '"kind":"state",', '"states":[', '"format":"x",'];
const PADS = ['', ' ', '\n', '\r\n', '\t', '\u00a0', '\ufeff', '\u2028', ' \n ', '\n\n', 'x', '{}', ',', '\n{"kind":"footer"}'];

/** 對文字做一次隨機破壞(可能仍是合法紀錄)。 */
export function mutate(text: string, rand: () => number): string {
  const pick = <T>(list: readonly T[]): T => list[Math.floor(rand() * list.length)]!;
  const at = () => Math.floor(rand() * (text.length + 1));
  const span = () => {
    const a = at();
    const b = Math.min(text.length, a + Math.floor(rand() * 40));
    return [a, b] as const;
  };
  switch (Math.floor(rand() * 9)) {
    case 0:
      return text.slice(0, at());
    case 1: {
      const [a, b] = span();
      return text.slice(0, a) + text.slice(b);
    }
    case 2: {
      const a = at();
      return text.slice(0, a) + pick(INSERTS) + text.slice(a);
    }
    case 3: {
      const [a, b] = span();
      return text.slice(0, a) + pick(INSERTS) + text.slice(b);
    }
    case 4: {
      const [a, b] = span();
      return text.slice(0, b) + text.slice(a);
    }
    case 5:
      return pick(PADS) + text;
    case 6:
      return text + pick(PADS);
    case 7: {
      // 在引號或標點旁插入換行(JSON 文件變多行、JSON Lines 的行被拆開)
      const marks = [...text.matchAll(/[,{}[\]:]/g)];
      if (marks.length === 0) return `\n${text}`;
      const a = pick(marks).index + 1;
      return `${text.slice(0, a)}\n${text.slice(a)}`;
    }
    default: {
      const [a, b] = span();
      const [c, d] = span();
      if (b > c) return text.slice(c) + text.slice(0, c);
      return text.slice(0, a) + text.slice(c, d) + text.slice(b, c) + text.slice(a, b) + text.slice(d);
    }
  }
}

/** 把文字隨機切成數段(含空段與單一字元的段)。 */
export function randomChunks(text: string, rand: () => number): string[] {
  const out: string[] = [];
  const max = [1, 2, 3, 7, 64, 1024][Math.floor(rand() * 6)]!;
  for (let i = 0; i < text.length; ) {
    const len = Math.floor(rand() * (max + 1));
    out.push(text.slice(i, i + len));
    i += len;
  }
  return out;
}
