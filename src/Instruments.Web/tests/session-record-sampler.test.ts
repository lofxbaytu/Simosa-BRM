// 教官站狀態抽樣(第三十輪 W1;lib/session-record.ts StateSampler / sampleState):
//  1. 2 小時規模的合成資料(核心紀錄夾具 E02 目標船、E13 拖船/泊位、E17b 浪/升沉、E08 警報、E06b 潮流與潮高,另補放出的錨、橫搖、
//     船間交互作用;中途倒帶 300 s):修正前(每秒 structuredClone 整筆狀態、倒帶不截斷)與修正後(精簡投影、倒帶截斷、倒帶點另存)
//     的紀錄經 parseReplay 讀回後,重播檢視讀的每個欄位、講評摘要(summarize)、航跡點、事件標記與講評報告(buildDebriefReport)完全相同;
//     紀錄 JSON 變小。
//  2. 倒帶截斷、t 單調;核心重啟(呼叫者沒先 rewind)時同樣截斷。
//  3. 安全上限:1 Hz 剛好 2 小時(7201 筆含兩端)不降採樣;再多即降採樣為一半(保留第一筆與最後一筆,間隔加倍),筆數有上限。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { summarize } from '../src/lib/debrief.js';
import { eventMarks } from '../src/lib/plot-overlay.js';
import { parseReplay, trackPoints } from '../src/lib/record-replay.js';
import { MAX_STATE_SAMPLES, StateSampler, buildSessionRecord, sampleState, sessionRecordBlob, sessionRecordChunks, type SessionRecordInput } from '../src/lib/session-record.js';
import { defaultState, parseState } from '../src/lib/state-parser.js';
import { buildDebriefReport, emptyReportMeta } from '../src/report/debrief-report.js';
import type { SimEvent } from '../src/types/event.js';
import type { OwnShipState } from '../src/types/state.js';

const here = path.dirname(fileURLToPath(import.meta.url));

/** 夾具最後一筆原始狀態(核心廣播的 JSON 物件) */
function lastRawState(name: string): Record<string, unknown> {
  const lines = readFileSync(path.join(here, 'fixtures', `record-${name}-core.jsonl`), 'utf8').trim().split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const o = JSON.parse(lines[i]!) as { kind: string; state?: Record<string, unknown> };
    if (o.kind === 'state' && o.state) return o.state;
  }
  throw new Error(`夾具 ${name} 沒有狀態`);
}

/** 修正前的抽樣器(第三十輪以前的 StateSampler,作為對照組) */
class LegacySampler {
  readonly states: OwnShipState[] = [];
  private lastT = Number.NEGATIVE_INFINITY;
  push(s: OwnShipState): boolean {
    if (s.t < this.lastT) this.lastT = Number.NEGATIVE_INFINITY;
    if (s.t - this.lastT < 1 && this.states.length > 0) return false;
    this.states.push(structuredClone(s));
    this.lastT = s.t;
    return true;
  }
}

const TELEGRAPHS = ['STOP', 'DSAH', 'SAH', 'HAH', 'FAH', 'NAVF'] as const;

/** 第 k 秒(相對區段起點)的合成原始狀態:位置、航向、舵、車鐘、UKC、目標船、拖船、錨等都隨時間變化。 */
function synthRaw(template: Record<string, unknown>, t: number, segment: number): Record<string, unknown> {
  const s = structuredClone(template) as Record<string, any>;
  const k = Math.round(t);
  s['t'] = t;
  s['tick'] = Math.round(t / 0.02);
  s['utc'] = new Date(Date.UTC(2026, 10, 22, 0, 0, 0) + t * 1000).toISOString();
  s['pos'] = { lat: 24 + t * 1e-5, lon: 120 + t * 1.3e-5, x: 1000 + t * 3.1 + 40 * Math.sin(t / 97), y: -500 + t * 2.7 };
  s['heading'] = (360 + 30 + 25 * Math.sin(t / 211)) % 360;
  s['cog'] = (s['heading'] + 2 * Math.cos(t / 37) + 360) % 360;
  s['sog'] = 6 + 4 * Math.sin(t / 600) + (k % 17) * 0.01;
  s['stw'] = s['sog'] - 0.3;
  s['rot'] = 35 * Math.sin(t / 53);
  s['u'] = s['sog'] * 0.514;
  s['v'] = 0.1 * Math.sin(t / 13);
  s['r'] = 0.001 * Math.cos(t / 29);
  s['rudder'] = 36 * Math.sin(t / 47);
  s['rudderOrder'] = Math.round(35 * Math.sin(t / 47) / 5) * 5;
  s['telegraph'] = TELEGRAPHS[Math.floor(t / 400) % TELEGRAPHS.length];
  s['rpmOrder'] = 20 * TELEGRAPHS.indexOf(s['telegraph']);
  s['rpm'] = s['rpmOrder'] - 1 + Math.sin(t);
  s['depthBelowKeel'] = 2.5 + 2 * Math.sin(t / 333);
  s['flags'] = { frozen: k >= 1000 && k < 1060, aground: segment === 3 && k % 1440 > 1200, collision: false };
  if (Array.isArray(s['targets'])) {
    s['targets'] = s['targets'].map((tg: Record<string, any>, i: number) => ({
      ...tg,
      pos: { ...tg['pos'], x: tg['pos'].x + t * (i + 1) * 0.7, y: tg['pos'].y - t * 0.4 * i },
      cog: (tg['cog'] + t * 0.01) % 360,
      range_nm: tg['range_nm'] + Math.sin(t / 90 + i),
      cpa_nm: Math.abs(tg['cpa_nm'] + 0.3 * Math.sin(t / 120 + i)),
      tcpa_min: tg['tcpa_min'] - t / 60,
      aisOn: !(i === 1 && k % 900 < 300),
    }));
  }
  if (Array.isArray(s['tugs'])) s['tugs'] = s['tugs'].map((tg: Record<string, any>) => ({ ...tg, force_t: 10 + 5 * Math.sin(t / 30), direction_deg: (90 + t) % 360 }));
  // 錨:第 2 區段起左錨放出(有位置),右錨收著
  if (segment >= 2 && Array.isArray(s['anchors'])) {
    s['anchors'] = s['anchors'].map((a: Record<string, any>) => (a['side'] === 'port' ? { ...a, status: k % 600 < 300 ? 'holding' : 'dragging', length_m: 82.5, length_shackles: 3, tension_t: 12 + Math.sin(t), onBottom_m: 40, position: { x: 900 + t, y: -450 + t } } : a));
  }
  if (segment === 1) {
    s['roll'] = { angle_deg: 3 * Math.sin(t / 7), rate_degps: 0.4 * Math.cos(t / 7), naturalPeriod_s: 14, staticHeel_deg: 0.5 };
    s['shipInteraction'] = { active: k % 200 < 100, forceSway_t: 2 * Math.sin(t / 11), momentYaw_tm: 30 * Math.cos(t / 11), targetId: 'X1', xi: 0.2, eta: 0.6, targets: 1 };
  }
  if (Array.isArray(s['alerts'])) s['alerts'] = s['alerts'].map((a: Record<string, any>) => ({ ...a, state: k % 120 < 60 ? 'activeUnacknowledged' : 'activeAcknowledged' }));
  if (s['sensors']) s['sensors'] = { ...s['sensors'], headingValid: !(k % 1300 < 40), positionValid: !(k % 1700 < 25) };
  return s;
}

/** 重播檢視(conning 列、鳥瞰航跡圖、目標船小表)實際讀取的欄位 */
function replayView(s: OwnShipState) {
  return {
    t: s.t,
    tick: s.tick,
    shipId: s.shipId,
    pos: s.pos,
    heading: s.heading,
    cog: s.cog,
    sog: s.sog,
    stw: s.stw,
    rot: s.rot,
    drift: s.drift,
    rudder: s.rudder,
    rudderOrder: s.rudderOrder,
    rpm: s.rpm,
    rpmOrder: s.rpmOrder,
    telegraph: s.telegraph,
    engine: [s.engine?.state, s.engine?.load_pct],
    thruster: s.thruster,
    faults: s.faults,
    depth: [s.depthBelowKeel, s.waterDepth, s.tideHeight_m, s.squat, s.draft?.fore, s.draft?.aft],
    flags: s.flags,
    wind: s.wind,
    current: s.current,
    currentSource: s.currentSource,
    waves: s.waves,
    shipInteraction: s.shipInteraction,
    waveMotion: s.waveMotion,
    roll: s.roll,
    route: s.route,
    visibility: s.visibility_nm,
    sensors: s.sensors ? [s.sensors.headingValid, s.sensors.stwValid, s.sensors.positionValid, s.sensors.depthValid] : null,
    tugs: s.tugs,
    berth: s.berth,
    anchors: (s.anchors ?? []).filter((a) => a.position !== undefined).map((a) => ({ side: a.side, status: a.status, position: a.position, length_shackles: a.length_shackles })),
    targets: s.targets.map((t) => ({ id: t.id, name: t.name, x: t.pos.x, y: t.pos.y, heading: t.heading, cog: t.cog, sog: t.sog, loa: t.loa, aisOn: t.aisOn, range_nm: t.range_nm, bearing_deg: t.bearing_deg, cpa_nm: t.cpa_nm, tcpa_min: t.tcpa_min })),
  };
}

describe('2 小時合成資料:修正前後的重播、講評摘要與報告相同', () => {
  const templates = ['E02_coastal_traffic_strait', 'E17b_typhoon_seakeeping', 'E13_tug_berthing_mailiao', 'E08', 'E06b_tidal_stream'].map(lastRawState);
  const segmentOf = (t: number) => Math.min(templates.length - 1, Math.floor(t / 1440));
  const legacy = new LegacySampler();
  const sampler = new StateSampler(1);
  let prev: OwnShipState | undefined;
  const feed = (t: number) => {
    const seg = segmentOf(t);
    const parsed = parseState(synthRaw(templates[seg]!, t, seg), prev)!;
    prev = parsed.state;
    legacy.push(parsed.state);
    sampler.push(parsed.state);
  };
  // 0–3000 s,倒帶回 2700.5 s(核心 restore 後第一筆;呼叫者未先 rewind 時抽樣器自行截斷),再跑到 7199.5 s
  for (let t = 0; t <= 3000; t++) feed(t);
  for (let t = 2700.5; t < 7200; t++) feed(t);
  const events: SimEvent[] = [100, 950, 2650, 3100, 5000, 7100].map((t, seq) => ({ kind: 'cpaAlarm', tick: Math.round(t / 0.02), t, source: 'traffic', seq, visible: true, targetId: 'X1' }));
  const base: Omit<SessionRecordInput, 'states'> = { mode: 'full', source: 'ws://x', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, operations: [], markers: [], commands: [], summary: null, events, coreSummary: null, startedUtc: '2026-11-22T00:00:00.000Z' };
  const now = new Date(Date.UTC(2026, 10, 22, 3));
  const oldText = JSON.stringify(buildSessionRecord({ ...base, states: legacy.states }, now));
  const newRecord = buildSessionRecord({ ...base, states: sampler.all(), rewinds: sampler.rewinds() }, now);
  const newText = [...sessionRecordChunks(newRecord)].join('');
  const a = parseReplay(oldText);
  const b = parseReplay(newText);

  it('抽樣器本身:倒帶後截斷,t 單調;修正前保留了倒帶前的舊分支', () => {
    expect(legacy.states.length).toBe(3001 + 4500);
    expect(sampler.all().length).toBe(2701 + 4500);
    expect(sampler.all().every((s, i, arr) => i === 0 || s.t > arr[i - 1]!.t)).toBe(true);
    expect(sampler.rewinds()).toEqual([{ t: 2700.5, tick: Math.round(2700.5 / 0.02), from: 3000, droppedStates: 300, droppedEvents: 0 }]);
    expect(sampler.decimations).toBe(0);
  });

  it('讀回的狀態序列、倒帶點與重播檢視讀取的每個欄位都相同', () => {
    expect(b.errors).toEqual([]);
    expect(b.states.length).toBe(a.states.length);
    expect(b.states.length).toBe(7201);
    expect([b.tStart, b.tEnd]).toEqual([a.tStart, a.tEnd]);
    expect(b.rewinds).toEqual(a.rewinds);
    expect(b.events).toEqual(a.events);
    for (let i = 0; i < a.states.length; i++) {
      const va = replayView(a.states[i]!);
      const vb = replayView(b.states[i]!);
      if (JSON.stringify(va) !== JSON.stringify(vb)) expect(vb).toEqual(va); // 只在不同時做深比較(7201 筆)
    }
    // 涵蓋了各種欄位(避免比較的是空值)
    expect(a.states.filter((s) => s.targets.length > 0).length).toBeGreaterThan(1000);
    for (const key of ['tugs', 'berth', 'waves', 'waveMotion', 'roll', 'shipInteraction', 'route', 'currentSource', 'tideHeight_m'] as const) expect(b.states.some((s) => s[key] !== undefined)).toBe(true);
    expect(b.states.some((s) => (s.anchors ?? []).some((x) => x.position))).toBe(true);
    expect(b.states.some((s) => s.sensors && !s.sensors.headingValid)).toBe(true);
  });

  it('講評摘要、航跡點、事件標記與講評報告相同', () => {
    expect(summarize(b.states)).toEqual(summarize(a.states));
    expect(summarize(b.states).samples).toBe(7201);
    expect(trackPoints(b.states, 1)).toEqual(trackPoints(a.states, 1));
    expect(eventMarks(b.events, b.states)).toEqual(eventMarks(a.events, a.states));
    for (const t of [0, 1500, 2800, 7199.5]) expect(eventMarks(b.events, b.states, t)).toEqual(eventMarks(a.events, a.states, t));
    const meta = emptyReportMeta();
    expect(buildDebriefReport({ data: b, meta, now })).toEqual(buildDebriefReport({ data: a, meta, now }));
    expect(buildDebriefReport({ data: b, meta, now }).exercise.rewinds).toBe(1);
  });

  it('紀錄 JSON 變小(E02 區段:目標船只留航跡圖與小表用的欄位)', () => {
    expect(newText.length).toBeLessThan(oldText.length * 0.55);
    const e02Old = JSON.stringify(legacy.states.slice(0, 1000)).length / 1000;
    const e02New = JSON.stringify(sampler.all().slice(0, 1000)).length / 1000;
    expect(e02New).toBeLessThan(e02Old * 0.5);
  });

  it('Blob 匯出與單一字串 JSON.stringify 位元組相同', async () => {
    expect(newText).toBe(JSON.stringify(newRecord));
    const bytes = new Uint8Array(await sessionRecordBlob(newRecord).arrayBuffer());
    expect(Buffer.compare(Buffer.from(bytes), Buffer.from(new TextEncoder().encode(JSON.stringify(newRecord))))).toBe(0);
  });
});

describe('StateSampler 倒帶與上限', () => {
  const st = (t: number, x = t): OwnShipState => ({ ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x, y: 0 } });

  it('rewind():丟棄 t > 倒帶點,下一筆一律抽樣;倒帶點與丟棄筆數記入 rewinds;紀錄讀回時列為倒帶標記', () => {
    const s = new StateSampler(1);
    for (let t = 0; t <= 100; t++) s.push(st(t));
    expect(s.rewind(50, 2500, 3)).toBe(50);
    expect(s.all().at(-1)!.t).toBe(50);
    expect(s.push(st(50.04))).toBe(true); // 間隔不到 1 s 也抽(新分支起點)
    expect(s.push(st(50.5))).toBe(false);
    for (let t = 51.04; t < 60; t++) s.push(st(t));
    expect(s.all().map((x) => x.t).slice(48, 53)).toEqual([48, 49, 50, 50.04, 51.04]);
    expect(s.rewinds()).toEqual([{ t: 50, tick: 2500, from: 100, droppedStates: 50, droppedEvents: 3 }]);
    const r = parseReplay(JSON.stringify(buildSessionRecord({ mode: 'full', source: 'x', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: s.all(), operations: [], markers: [], commands: [], summary: null, rewinds: s.rewinds() })));
    expect(r.rewinds).toEqual([{ t: 50, tick: 2500, from: 100, droppedStates: 50, droppedEvents: 3 }]);
    expect(r.states.every((x, i, arr) => i === 0 || x.t > arr[i - 1]!.t)).toBe(true);
    // 沒有抽樣時不記倒帶(新練習開始時核心 tick 歸零)
    const empty = new StateSampler(1);
    expect(empty.rewind(0, 0)).toBe(0);
    expect(empty.rewinds()).toEqual([]);
    s.clear();
    expect([s.all().length, s.rewinds().length]).toEqual([0, 0]);
  });

  it('push() 遇到 t 倒退(呼叫者沒先 rewind,例如核心重啟):視為倒帶到該狀態的 t', () => {
    const s = new StateSampler(1);
    for (let t = 0; t <= 20; t++) s.push(st(t));
    expect(s.push(st(4.28))).toBe(true);
    expect(s.all().map((x) => x.t)).toEqual([0, 1, 2, 3, 4, 4.28]);
    expect(s.rewinds()).toEqual([{ t: 4.28, tick: 214, from: 20, droppedStates: 16, droppedEvents: 0 }]);
  });

  it('1 Hz 剛好 2 小時(t = 0…7200,7201 筆)不降採樣;再多即降為一半:保留第一筆與最後一筆、間隔加倍、筆數有上限', () => {
    const s = new StateSampler(1);
    for (let t = 0; t <= 7200; t++) s.push(st(t));
    expect(MAX_STATE_SAMPLES).toBe(7201);
    expect(s.all().length).toBe(7201);
    expect(s.decimations).toBe(0);
    expect(s.currentInterval_s).toBe(1);
    s.push(st(7201));
    expect(s.decimations).toBe(1);
    expect(s.currentInterval_s).toBe(2);
    expect(s.all()[0]!.t).toBe(0);
    expect(s.all().at(-1)!.t).toBe(7201);
    expect(s.all().length).toBe(3602);
    expect(s.all()[1]!.t - s.all()[0]!.t).toBe(2);
    // 一整天(1 Hz 輸入 86,400 s):筆數始終不超過上限,第一筆仍是 t = 0、最後一筆是最新的抽樣
    let max = 0;
    for (let t = 7202; t <= 86_400; t++) {
      s.push(st(t));
      max = Math.max(max, s.all().length);
    }
    expect(max).toBeLessThanOrEqual(MAX_STATE_SAMPLES);
    expect(s.all()[0]!.t).toBe(0);
    expect(s.all().at(-1)!.t).toBeGreaterThan(86_400 - s.currentInterval_s); // 最新的抽樣(間隔為降採樣後的間隔)
    expect(s.all().every((x, i, arr) => i === 0 || x.t > arr[i - 1]!.t)).toBe(true);
    expect(s.currentInterval_s).toBe(2 ** s.decimations);
    s.clear();
    expect([s.currentInterval_s, s.decimations]).toEqual([1, 0]);
  });

  it('sampleState:只留讀取端用到的欄位;深複本(來源物件之後被改寫不影響抽樣)', () => {
    const raw = lastRawState('E02_coastal_traffic_strait');
    const full = parseState(raw)!.state;
    const p = sampleState(full);
    for (const k of ['truth', 'autopilot', 'bnwas', 'steering', 'scenario', 'utc', 'u', 'v', 'r', 'loading', 'alerts', 'anchors'] as const) expect(p).not.toHaveProperty(k);
    expect(p.sensors).toEqual({ headingValid: true, positionValid: true, depthValid: true, stwValid: true });
    expect(Object.keys(p.targets[0]!).sort()).toEqual(['aisOn', 'bearing_deg', 'cog', 'cpa_nm', 'heading', 'id', 'loa', 'name', 'pos', 'range_nm', 'sog', 'tcpa_min']);
    expect(p.targets[0]!.pos).toEqual({ x: full.targets[0]!.pos.x, y: full.targets[0]!.pos.y });
    expect(JSON.stringify(p).length).toBeLessThan(JSON.stringify(full).length * 0.6);
    full.pos.x = -1;
    full.targets[0]!.pos.x = -1;
    expect(p.pos.x).not.toBe(-1);
    expect(p.targets[0]!.pos.x).not.toBe(-1);
  });
});
