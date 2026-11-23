// 教官站事件流合併碰墊的反覆接觸(第三十輪 ENG-C9 的教官站端處理;lib/event-merge.ts、lib/sim-event.ts EventStore):
//  - 合併規則:同一個碰墊、未過載、法向速度不超過警示門檻、與該碰墊上一筆接觸相隔不超過門檻 → 併入一列(次數、第一次與最近一次的時刻、最大值);
//    第一次接觸、過載、超過警示速度、隔了超過門檻的再接觸各自一列;別的事件不打斷合併列;
//  - 上限:合併列算一列(2 小時 1956 筆接觸的真實事件序列只佔幾十列,其他事件不被擠掉);
//  - 倒帶:dropAfter 把合併列截到倒帶點,結果與「只收到倒帶點之前的事件」相同;
//  - 匯出往返:教官站紀錄(版本 6)帶 merged,重播 / 報告讀回同樣的列;版本 5 以前的紀錄與核心 JSON Lines 讀入時以同一規則合併。
import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { FENDER_MERGE_GAP_S, FENDER_MERGE_MAX_COUNT, rawEventCount } from '../src/lib/event-merge.js';
import { BERTHING_SPEED_CAUTION_MPS } from '../src/lib/external-forces.js';
import { eventMarks } from '../src/lib/plot-overlay.js';
import { parseJsonLines, parseReplay } from '../src/lib/record-replay.js';
import { parseReplayText } from '../src/lib/record-stream.js';
import { SESSION_RECORD_VERSION, buildSessionRecord, sessionRecordBlob, type SessionRecord } from '../src/lib/session-record.js';
import { EventStore, eventLabel, eventSeverity, mergeEventRows, parseSimEvent } from '../src/lib/sim-event.js';
import { defaultState } from '../src/lib/state-parser.js';
import { buildTimeline } from '../src/report/debrief-report.js';
import type { SimEvent } from '../src/types/event.js';

const DT = 0.02;
let seq = 0;

/** 核心形狀的 fenderContact 事件(BerthModel.ReportFender 的欄位) */
function contact(t: number, fenderId = 'F1', over: Partial<{ v: number; energy: number; pct: number; overload: boolean; phase: string }> = {}): SimEvent {
  const v = over.v ?? 0.03;
  const energy = over.energy ?? 2;
  const pct = over.pct ?? 5;
  const overload = over.overload ?? false;
  return {
    kind: 'fenderContact',
    tick: Math.round(t / DT),
    t,
    source: 'engine',
    itemId: fenderId,
    message: overload ? `碰墊 ${fenderId} 過載` : `碰墊 ${fenderId} 接觸`,
    phase: over.phase ?? 'normal',
    visible: true,
    seq: seq++,
    data: { fenderId, normalSpeed_mps: v, energy_kNm: energy, maxReaction_kN: energy * 30, maxDeflection_m: pct / 125, deflection_pct: pct, ratedEnergy_kNm: 282.3, ratedReaction_kN: 640, overload },
  };
}

function other(t: number, kind = 'alertRaised', source: SimEvent['source'] = 'engine'): SimEvent {
  return { kind, tick: Math.round(t / DT), t, source, visible: false, seq: seq++, data: { id: `x${t}` } };
}

function storeOf(events: readonly SimEvent[], max = Number.POSITIVE_INFINITY): EventStore {
  const s = new EventStore(max);
  for (const e of events) s.push(e);
  return s;
}

/** 列的摘要:碰墊列寫「碰墊 id × 次數 @ 第一次–最近一次」,其他寫 kind@t */
function rows(events: readonly SimEvent[]): string[] {
  return events.map((e) => (e.kind === 'fenderContact' ? `${e.data?.['fenderId']}${e.data?.['overload'] === true ? '!' : ''}×${e.merged?.count ?? 1}@${e.merged ? `${e.merged.firstT}–` : ''}${e.t}` : `${e.kind}@${e.t}`));
}

describe('合併規則', () => {
  it('第一次接觸自己一列;之後門檻內的低速接觸併成一列:次數、第一次與最近一次的時刻、各次的最大值;列是最近一次接觸的事件', () => {
    const first = contact(10, 'F1', { v: 0.12, energy: 40, pct: 20 });
    const swings = [contact(20, 'F1', { v: 0.03, energy: 2, pct: 5 }), contact(45, 'F1', { v: 0.06, energy: 9, pct: 4, phase: 'degraded' }), contact(50, 'F1', { v: 0.02, energy: 1, pct: 11 })];
    const s = storeOf([first, ...swings]);
    expect(rows(s.all())).toEqual(['F1×1@10', 'F1×3@20–50']);
    expect(s.all()[0]).toBe(first); // 第一次接觸原封不動
    const row = s.all()[1]!;
    const last = swings[2]!;
    expect(row).toEqual({ ...last, merged: { count: 3, firstT: 20, firstTick: 1000, lastT: 50, maxNormalSpeed_mps: 0.06, maxEnergy_kNm: 9, maxDeflection_pct: 11, maxReaction_kN: 270 } });
    expect(row.phase).toBe('normal');
    expect(s.length).toBe(2);
    expect(rawEventCount(s.all())).toBe(4);
    expect(s.count('fenderContact')).toBe(4);
    expect(s.count('fenderContact', { itemId: 'F1' })).toBe(4);
    expect(s.last('fenderContact')).toBe(row);
  });

  it('只有一次的反覆接觸不帶 merged(就是原事件);第二次起才是合併列;併入時換成新物件,先前拿到的列不被改寫', () => {
    const s = new EventStore();
    s.push(contact(0, 'F1'));
    const second = contact(30, 'F1');
    s.push(second);
    expect(s.all()[1]).toBe(second);
    expect(second.merged).toBeUndefined();
    s.push(contact(60, 'F1'));
    const two = s.all()[1]!;
    expect(two.merged?.count).toBe(2);
    const snapshot = structuredClone(two);
    s.push(contact(90, 'F1'));
    expect(s.all()[1]!.merged?.count).toBe(3);
    expect(two).toEqual(snapshot);
    expect(second.merged).toBeUndefined();
  });

  it('過載、法向速度超過警示門檻的接觸各自一列,並結束進行中的合併列;之後的反覆接觸另起一列', () => {
    const s = storeOf([
      contact(0, 'F1'),
      contact(10, 'F1'),
      contact(20, 'F1'),
      contact(30, 'F1', { overload: true, v: 0.05, energy: 300 }),
      contact(40, 'F1'),
      contact(50, 'F1'),
      contact(60, 'F1', { v: BERTHING_SPEED_CAUTION_MPS + 0.01 }),
      contact(70, 'F1', { v: BERTHING_SPEED_CAUTION_MPS }), // 剛好等於門檻:info,可合併
      contact(80, 'F1'),
    ]);
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×2@10–20', 'F1!×1@30', 'F1×2@40–50', 'F1×1@60', 'F1×2@70–80']);
    expect(s.all().map(eventSeverity)).toEqual(['info', 'info', 'alarm', 'info', 'warning', 'info']);
    // 過載與超過警示速度的列:原事件、不帶 merged
    expect(s.all()[2]!.merged).toBeUndefined();
    expect(s.all()[4]!.merged).toBeUndefined();
  });

  it(`門檻 ${FENDER_MERGE_GAP_S} 模擬秒:剛好等於門檻仍合併;超過則視為離開後再碰回,自己一列,之後另起合併列`, () => {
    const s = storeOf([contact(0, 'F1'), contact(100, 'F1'), contact(100 + FENDER_MERGE_GAP_S, 'F1'), contact(220.02 + FENDER_MERGE_GAP_S, 'F1'), contact(400, 'F1'), contact(410, 'F1')]);
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×2@100–220', 'F1×1@340.02', 'F1×2@400–410']);
  });

  it('各碰墊分開合併;別的事件(其他碰墊的接觸、警報、VHF)不打斷合併列,合併列每併入一次就移到清單最後、清單維持依 t 遞增', () => {
    const s = storeOf([contact(0, 'F1'), contact(0, 'F2'), contact(10, 'F1'), other(12), contact(15, 'F2'), other(18, 'vhfText', 'vhf'), contact(20, 'F1'), other(25, 'cpaAlarm', 'traffic'), contact(30, 'F2'), contact(40, 'F1')]);
    expect(rows(s.all())).toEqual(['F1×1@0', 'F2×1@0', 'alertRaised@12', 'vhfText@18', 'cpaAlarm@25', 'F2×2@15–30', 'F1×3@10–40']);
    const ts = s.all().map((e) => e.t);
    expect([...ts].sort((a, b) => a - b)).toEqual(ts);
  });

  it(`一列最多合併 ${FENDER_MERGE_MAX_COUNT} 次,滿了另起一列`, () => {
    const s = new EventStore(Number.POSITIVE_INFINITY, { maxCount: 4 });
    for (let i = 0; i <= 10; i++) s.push(contact(i * 10, 'F1'));
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×4@10–40', 'F1×4@50–80', 'F1×2@90–100']);
    const d = new EventStore();
    for (let i = 0; i <= FENDER_MERGE_MAX_COUNT + 1; i++) d.push(contact(i, 'F1'));
    expect(d.all().map((e) => e.merged?.count ?? 1)).toEqual([1, FENDER_MERGE_MAX_COUNT, 1]);
  });

  it('門檻設 0 = 不合併(每筆事件各自一列);沒有 fenderId 的事件以 itemId 區分碰墊', () => {
    const off = new EventStore(2000, { gap_s: 0 });
    for (let i = 0; i < 5; i++) off.push(contact(i * 10, 'F1'));
    expect(off.length).toBe(5);
    expect(off.all().every((e) => !e.merged)).toBe(true);
    const bare = (t: number, itemId: string): SimEvent => ({ kind: 'fenderContact', tick: Math.round(t / DT), t, source: 'engine', itemId, visible: true, seq: seq++ });
    const s = storeOf([bare(0, 'A'), bare(1, 'B'), bare(2, 'A'), bare(3, 'A'), bare(4, 'B')]);
    expect(s.all().map((e) => `${e.itemId}×${e.merged?.count ?? 1}`)).toEqual(['A×1', 'B×1', 'A×2', 'B×1']);
    // 沒有數值欄:最大值記 0,壓縮與反力省略
    expect(s.all()[2]!.merged).toEqual({ count: 2, firstT: 2, firstTick: 100, lastT: 3, maxNormalSpeed_mps: 0, maxEnergy_kNm: 0 });
  });

  it('時間倒退的接觸(核心重啟、尚未收到倒帶後的狀態)不併入先前的列', () => {
    const s = storeOf([contact(100, 'F1'), contact(110, 'F1'), contact(120, 'F1'), contact(5, 'F1'), contact(15, 'F1')]);
    expect(rows(s.all())).toEqual(['F1×1@100', 'F1×2@110–120', 'F1×1@5', 'F1×1@15']);
  });
});

describe('文字、嚴重度與標記', () => {
  const s = storeOf([contact(17, 'S03', { v: 0.066, energy: 7.06, pct: 8.6 }), contact(38.76, 'S03', { v: 0.034, energy: 2.4, pct: 4.9 }), contact(7197.22, 'S03', { v: 0.03, energy: 2.2, pct: 4.8 })], Number.POSITIVE_INFINITY);
  const big = new EventStore(Number.POSITIVE_INFINITY, { gap_s: 1e9 });
  for (const e of [contact(17, 'S03', { v: 0.066, energy: 7.06, pct: 8.6 }), contact(38.76, 'S03', { v: 0.034, energy: 2.4, pct: 4.9 }), contact(7197.22, 'S03', { v: 0.03, energy: 2.2, pct: 4.8 })]) big.push(e);

  it('合併列的一句話:次數、第一次到最近一次的時刻、最大法向速度 / 能量(額定)/ 壓縮;單筆接觸的文字不變', () => {
    expect(eventLabel(s.all()[0]!)).toBe('碰墊 S03 接觸 v 0.07 m/s 能量 7 kN·m(額定 282)壓縮 9 %');
    expect(eventLabel(big.all()[1]!)).toBe('碰墊 S03 反覆接觸 ×2(00:38–1:59:57)最大 v 0.03 m/s、能量 2 kN·m(額定 282)、壓縮 5 %');
    const bare: SimEvent = { kind: 'fenderContact', tick: 50, t: 1, source: 'engine', itemId: 'A', visible: false, seq: 1, merged: { count: 3, firstT: 0, firstTick: 0, lastT: 1, maxNormalSpeed_mps: 0.1, maxEnergy_kNm: 12.4 } };
    expect(eventLabel(bare)).toBe('碰墊 A 反覆接觸 ×3(00:00–00:01)最大 v 0.10 m/s、能量 12 kN·m(額定 ?)');
  });

  it('合併列的嚴重度是 info(只合併 info 的接觸):航跡圖不畫事件標記;讀回的合併列以其最大值判定', () => {
    const row = big.all()[1]!;
    expect(eventSeverity(row)).toBe('info');
    expect(eventMarks(big.all(), [{ t: 0, pos: { lat: 0, lon: 0, x: 0, y: 0 } }])).toEqual([]);
    expect(eventSeverity({ ...row, merged: { ...row.merged!, maxNormalSpeed_mps: 0.3 } })).toBe('warning');
  });

  it('講評時間軸:合併列排在第一次接觸的時刻並帶次數,其餘列照舊', () => {
    const tl = buildTimeline([...big.all(), other(100, 'frozen', 'instructor')]);
    expect(tl.rows.map((r) => [r.t, r.kind, r.count])).toEqual([
      [17, 'fenderContact', undefined],
      [38.76, 'fenderContact', 2],
      [100, 'frozen', undefined],
    ]);
    // 報告時間軸的時刻欄是 HH:MM:SS,文字裡的起訖時刻用同一個格式(事件流是 00:38–1:59:57:滿一小時前後在同一個括號裡混用兩種寫法)
    expect(tl.rows[1]!.label).toBe('碰墊 S03 反覆接觸 ×2(00:00:38–01:59:57)最大 v 0.03 m/s、能量 2 kN·m(額定 282)、壓縮 5 %');
    expect(eventLabel(big.all()[1]!)).toContain('反覆接觸 ×2(00:38–1:59:57)');
    expect(tl.rows[1]!.severity).toBe('info');
  });
});

interface Fixture {
  contactColumns: string[];
  contactCommon: { kind: string; source: SimEvent['source']; visible: boolean; data: Record<string, unknown> };
  contacts: Array<[number, number, number, string, string, number, number, number, number, number]>;
  others: unknown[];
}

/** E14 維持吹攏風 25 kn、不操作 2 小時的核心事件序列(夾具說明見檔內 _comment),依核心的事件序號排回原順序 */
function e14vEvents(): SimEvent[] {
  const fx = JSON.parse(fs.readFileSync(path.join(import.meta.dirname, 'fixtures', 'events-E14v_onshore25-2h.json'), 'utf8')) as Fixture;
  const contacts = fx.contacts.map(([t, tick, s, fenderId, phase, v, energy, reaction, defl, pct]): SimEvent => ({
    kind: fx.contactCommon.kind,
    tick,
    t,
    source: fx.contactCommon.source,
    itemId: fenderId,
    message: `碰墊 ${fenderId} 接觸`,
    phase,
    visible: fx.contactCommon.visible,
    seq: s,
    data: { fenderId, normalSpeed_mps: v, energy_kNm: energy, maxReaction_kN: reaction, maxDeflection_m: defl, deflection_pct: pct, ...fx.contactCommon.data },
  }));
  const others = fx.others.map((o) => parseSimEvent(o, DT)!);
  return [...contacts, ...others].sort((a, b) => a.seq - b.seq);
}

describe('上限(2 小時的真實事件序列)', () => {
  const events = e14vEvents();

  it('夾具:1974 筆事件,其中 1956 筆 fenderContact(4 個碰墊),都未過載、法向速度都在警示門檻以下', () => {
    expect(events.length).toBe(1974);
    const fc = events.filter((e) => e.kind === 'fenderContact');
    expect(fc.length).toBe(1956);
    expect(new Set(fc.map((e) => e.itemId)).size).toBe(4);
    expect(fc.every((e) => eventSeverity(e) === 'info')).toBe(true);
    expect(events.map((e) => e.seq)).toEqual(events.map((_, i) => i));
  });

  it('不合併(修正前):2 小時已有 1974 列;再 40 分鐘(每 4 s 一筆)就超過 2000 列的上限,開頭的故障注入與必要事件逾時被擠出清單', () => {
    const before = new EventStore(2000, { gap_s: 0 });
    for (const e of events) before.push(e);
    expect(before.length).toBe(1974);
    expect(before.count('faultInjected')).toBe(1);
    for (let i = 0; i < 600; i++) before.push(contact(7200 + i * 4, 'S03'));
    expect(before.length).toBe(2000);
    expect(before.count('faultInjected')).toBe(0);
    expect(before.count('requiredEventMissed')).toBe(0);
  });

  it('合併後:1956 筆接觸只佔 33 列(S03 2、S04 4、S05 6、S06 21:開頭與隔超過 120 s 才再碰回的各自一列,其間的反覆接觸各併成一列),其他 18 筆事件都在;再 40 分鐘也不會擠掉', () => {
    const s = new EventStore(2000);
    for (const e of events) s.push(e);
    const fenderRows = s.all().filter((e) => e.kind === 'fenderContact');
    expect(s.length).toBe(51);
    expect(fenderRows.length).toBe(33);
    expect(Object.fromEntries(['S03', 'S04', 'S05', 'S06'].map((id) => [id, fenderRows.filter((e) => e.itemId === id).length]))).toEqual({ S03: 2, S04: 4, S05: 6, S06: 21 });
    // 門檻放寬到 300 s 時(這份序列同一個碰墊最長的間隔 282 s)只剩每個碰墊兩列
    const wide = new EventStore(2000, { gap_s: 300 });
    for (const e of events) wide.push(e);
    expect(wide.all().filter((e) => e.kind === 'fenderContact').length).toBe(8);
    expect(s.all().filter((e) => e.kind !== 'fenderContact').length).toBe(18);
    expect(rawEventCount(s.all())).toBe(1974);
    expect(s.count('fenderContact')).toBe(1956);
    expect(s.count('faultInjected')).toBe(1);
    expect(s.count('requiredEventMissed')).toBe(2);
    // 每個碰墊的次數與核心 summary.berthing.heldContactsByFender 相同(S03 698、S04 570、S05 418、S06 270)
    expect(Object.fromEntries(['S03', 'S04', 'S05', 'S06'].map((id) => [id, s.count('fenderContact', { itemId: id })]))).toEqual({ S03: 698, S04: 570, S05: 418, S06: 270 });
    // 最大值與逐筆的最大值相同
    const s03 = events.filter((e) => e.itemId === 'S03').slice(1);
    const s03Rows = fenderRows.filter((e) => e.itemId === 'S03' && e.merged);
    expect(s03Rows.length).toBe(1);
    expect(s03Rows[0]!.merged).toMatchObject({ count: 697, firstT: 16.92, lastT: 7197.22, maxNormalSpeed_mps: Math.max(...s03.map((e) => e.data!['normalSpeed_mps'] as number)), maxEnergy_kNm: Math.max(...s03.map((e) => e.data!['energy_kNm'] as number)) });
    // 清單依 t 遞增
    const ts = s.all().map((e) => e.t);
    expect([...ts].sort((a, b) => a - b)).toEqual(ts);
    // 再 40 分鐘的擺盪:列數不變(併入同一列),其他事件仍在
    for (let i = 1; i <= 600; i++) s.push(contact(7197.22 + i * 4, 'S03'));
    expect(s.length).toBe(52); // S03 的合併列滿 1000 次後另起一列
    expect(s.count('fenderContact', { itemId: 'S03' })).toBe(1298);
    expect(s.count('faultInjected')).toBe(1);
    expect(s.count('requiredEventMissed')).toBe(2);
  });

  it('上限以列計:合併列算一列;被擠出的是最久沒有活動的列,進行中的合併列留在清單裡', () => {
    const s = new EventStore(10);
    for (const e of events) s.push(e);
    expect(s.length).toBe(10);
    // 最後 10 列都是碰墊列(其他事件都在前 420 s),四個碰墊進行中的合併列都在
    const open = s.all().filter((e) => e.merged && e.t > 7190);
    expect(open.map((e) => e.itemId).sort()).toEqual(['S03', 'S04', 'S05', 'S06']);
    // 上限 2 時每次只留兩列:被擠出的合併列之後的接觸另起一列,不出錯
    const tiny = new EventStore(2);
    for (const e of events) tiny.push(e);
    expect(tiny.length).toBe(2);
  });
});

/** 固定種子的亂數(mulberry32) */
function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** 隨機事件序列:三個碰墊的接觸(間隔有長有短、偶爾過載或超過警示速度)夾雜其他事件;t 遞增、seq 連續 */
function randomSequence(seed: number, n: number): SimEvent[] {
  const r = rng(seed);
  const out: SimEvent[] = [];
  let t = 0;
  for (let i = 0; i < n; i++) {
    t = Math.round((t + (r() < 0.1 ? 150 + r() * 200 : r() * 20)) * 50) / 50;
    const pick = r();
    if (pick < 0.15) out.push({ ...other(t, r() < 0.5 ? 'alertRaised' : 'vhfText', r() < 0.5 ? 'engine' : 'vhf'), seq: i });
    else {
      const fender = `F${1 + Math.floor(r() * 3)}`;
      const kind = r();
      const e = contact(t, fender, { v: kind < 0.05 ? 0.2 + r() : r() * 0.1, energy: r() * 50, pct: r() * 30, overload: kind > 0.96, phase: r() < 0.5 ? 'normal' : 'degraded' });
      out.push({ ...e, seq: i });
    }
  }
  return out;
}

describe('倒帶(dropAfter)', () => {
  it('倒帶點落在合併列中間:截到倒帶點為止,次數、最近時刻、最大值重算;回傳丟棄的事件數(每次接觸各算一筆)', () => {
    const s = storeOf([
      contact(0, 'F1'),
      contact(10, 'F1', { v: 0.02, energy: 1, pct: 3 }),
      other(15),
      contact(20, 'F1', { v: 0.03, energy: 2, pct: 4, phase: 'degraded' }),
      contact(30, 'F1', { v: 0.09, energy: 30, pct: 12 }),
      other(35),
      contact(40, 'F1', { v: 0.01, energy: 1, pct: 2 }),
    ]);
    expect(rows(s.all())).toEqual(['F1×1@0', 'alertRaised@15', 'alertRaised@35', 'F1×4@10–40']);
    expect(s.dropAfter(25)).toBe(3); // 30 與 40 的接觸、35 的警報
    expect(rows(s.all())).toEqual(['F1×1@0', 'alertRaised@15', 'F1×2@10–20']);
    const row = s.all()[2]!;
    expect(row.merged).toEqual({ count: 2, firstT: 10, firstTick: 500, lastT: 20, maxNormalSpeed_mps: 0.03, maxEnergy_kNm: 2, maxDeflection_pct: 4, maxReaction_kN: 60 });
    expect(row.phase).toBe('degraded');
    expect(row.tick).toBe(1000);
    expect(row.data).toMatchObject({ fenderId: 'F1', normalSpeed_mps: 0.03, energy_kNm: 2, deflection_pct: 4, ratedEnergy_kNm: 282.3, overload: false });
    // 倒帶後的接觸接著併入同一列
    s.push(contact(28, 'F1', { v: 0.05, energy: 4, pct: 6 }));
    expect(rows(s.all())).toEqual(['F1×1@0', 'alertRaised@15', 'F1×3@10–28']);
    expect(s.all()[2]!.merged?.maxNormalSpeed_mps).toBe(0.05);
    // 只剩一次:回到原事件(不帶 merged);全部在倒帶點之後:整列丟棄
    expect(s.dropAfter(12)).toBe(3);
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×1@10']);
    expect(s.all()[1]!.merged).toBeUndefined();
    expect(s.dropAfter(5)).toBe(1);
    expect(rows(s.all())).toEqual(['F1×1@0']);
    expect(s.dropAfter(5)).toBe(0);
  });

  it('沒有 data / phase 的精簡事件:截斷後重建的列與「只收到倒帶點之前的事件」相同(不多出空的 data)', () => {
    const bare = (t: number): SimEvent => ({ kind: 'fenderContact', tick: Math.round(t / DT), t, source: 'engine', itemId: 'A', visible: true, seq: Math.round(t) });
    const events = [bare(0), bare(1), bare(2), bare(3), bare(4)];
    const all = storeOf(events);
    expect(all.dropAfter(3)).toBe(1);
    expect(all.all()).toEqual(storeOf(events.slice(0, 4)).all());
    expect(all.all()[1]).toEqual({ ...bare(3), merged: { count: 3, firstT: 1, firstTick: 50, lastT: 3, maxNormalSpeed_mps: 0, maxEnergy_kNm: 0 } });
    expect('data' in all.all()[1]!).toBe(false);
  });

  it('倒帶點之後才結束合併列的那筆接觸(過載)被丟掉後,合併列回到進行中;被丟掉的再碰回列之後的接觸重新判定', () => {
    const s = storeOf([contact(0, 'F1'), contact(10, 'F1'), contact(20, 'F1'), contact(30, 'F1', { overload: true }), contact(40, 'F1'), contact(50, 'F1')]);
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×2@10–20', 'F1!×1@30', 'F1×2@40–50']);
    expect(s.dropAfter(25)).toBe(3);
    s.push(contact(35, 'F1'));
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×3@10–35']);
  });

  it('隨機事件序列 × 隨機倒帶點:倒帶後的清單與「只收到倒帶點之前的事件」完全相同,之後再收到的事件也一樣;丟棄數 = t > 倒帶點的事件數', () => {
    // 兩種設定:(a) 明細不設保留時間、倒帶點任意;(b) 預設(明細保留 1 小時)、倒帶點在最後 50 分鐘內(核心的倒帶快照只有最近 10 分鐘)
    for (const mode of ['a', 'b'] as const) {
      for (let seedNo = 1; seedNo <= 40; seedNo++) {
        const r = rng(seedNo * 7919);
        const events = randomSequence(seedNo, 250);
        const tEnd = events[events.length - 1]!.t;
        const from = mode === 'a' ? 0 : Math.max(0, tEnd - 3000);
        const cut = seedNo % 5 === 0 ? events.filter((e) => e.t >= from)[Math.floor(r() * events.filter((e) => e.t >= from).length)]!.t : from + r() * (tEnd - from); // 有時剛好落在某筆事件的時刻
        const all = new EventStore(Number.POSITIVE_INFINITY, mode === 'a' ? { detailWindow_s: Number.POSITIVE_INFINITY } : {});
        for (const e of events) all.push(e);
        const dropped = all.dropAfter(cut);
        const only = storeOf(events.filter((e) => e.t <= cut));
        expect(dropped, `${mode} 種子 ${seedNo}`).toBe(events.filter((e) => e.t > cut).length);
        expect(all.all(), `${mode} 種子 ${seedNo} 倒帶到 ${cut}`).toEqual(only.all());
        // 新分支:倒帶點之後另一串事件
        const branch = randomSequence(seedNo + 1000, 60).map((e, i) => ({ ...e, t: Math.round((cut + e.t) * 50) / 50, tick: Math.round((cut + e.t) / DT), seq: 100_000 + i }));
        for (const e of branch) {
          all.push(e);
          only.push(e);
        }
        expect(all.all(), `${mode} 種子 ${seedNo} 新分支`).toEqual(only.all());
        expect(rawEventCount(all.all())).toBe(events.filter((e) => e.t <= cut).length + branch.length);
      }
    }
  });

  it('逐次明細只留最近 1 小時內有活動的列:24 小時的擺盪下明細筆數不隨時間成長;倒帶點落在已無明細的列中間時整列丟棄', () => {
    const s = new EventStore(2000);
    const peaks: number[] = [];
    // 兩個碰墊各每 8 s 一次;每 30 分鐘停 5 分鐘(隔超過門檻,另起新列)
    let n = 0;
    for (let t = 0; t < 24 * 3600; t += 4) {
      if (t % 1800 >= 1500) continue;
      s.push(contact(t, n++ % 2 === 0 ? 'F1' : 'F2'));
      if (t % 3600 === 0) peaks.push(s.retainedDetail);
    }
    expect(s.count('fenderContact')).toBe(n);
    expect(n).toBeGreaterThan(17_000);
    expect(Math.max(...peaks.slice(3))).toBeLessThanOrEqual(Math.max(...peaks.slice(1, 3)) + 2); // 第 2 小時以後不再增加
    expect(Math.max(...peaks)).toBeLessThan(1200); // 1 小時內約 750 次接觸,加上跨在一小時邊界上的兩列(每列約 190 次)
    // 列數:48 段 × 2 個碰墊 ×(該段的第一次接觸一列 + 合併列一列)
    expect(s.all().find((e) => e.merged)!.merged).toMatchObject({ count: 186, firstT: 12, lastT: 1492 });
    expect(s.length).toBe(48 * 2 * 2);
    // 已無明細的列(最近一次接觸早於最新事件超過 1 小時):倒帶到它中間時整列丟棄;最近一小時內的列照常截斷
    const recent = new EventStore(2000);
    for (const e of [contact(0, 'F1'), contact(10, 'F1'), contact(20, 'F1'), contact(5000, 'F1'), contact(5010, 'F1'), contact(5020, 'F1'), contact(5030, 'F1')]) recent.push(e);
    expect(rows(recent.all())).toEqual(['F1×1@0', 'F1×2@10–20', 'F1×1@5000', 'F1×3@5010–5030']);
    expect(recent.retainedDetail).toBe(3);
    expect(recent.dropAfter(5015)).toBe(2);
    expect(rows(recent.all())).toEqual(['F1×1@0', 'F1×2@10–20', 'F1×1@5000', 'F1×1@5010']);
    expect(recent.dropAfter(15)).toBe(4); // 10–20 那一列沒有明細:整列(2 次)丟棄,連同 5000、5010
    expect(rows(recent.all())).toEqual(['F1×1@0']);
  });

  it('紀錄讀回的合併列沒有逐次明細:最近時刻晚於倒帶點時整列丟棄(次數全數計入),早於倒帶點者保留', () => {
    const merged = (t: number, firstT: number, count: number): SimEvent => ({ ...contact(t, 'F1'), merged: { count, firstT, firstTick: Math.round(firstT / DT), lastT: t, maxNormalSpeed_mps: 0.05, maxEnergy_kNm: 3 } });
    const s = storeOf([contact(0, 'F1'), merged(50, 10, 7), other(60), merged(300, 200, 9)]);
    expect(s.dropAfter(250)).toBe(9);
    expect(rows(s.all())).toEqual(['F1×1@0', 'F1×7@10–50', 'alertRaised@60']);
  });
});

describe('教官站紀錄匯出往返(版本 6)與舊紀錄', () => {
  const events = e14vEvents();
  const state = (t: number) => ({ ...defaultState('FSB1'), t, tick: Math.round(t / DT) });

  function record(evs: readonly SimEvent[]): SessionRecord {
    return buildSessionRecord({ mode: 'full', source: 'ws://x', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: [state(0), state(3600), state(7200)], operations: [], markers: [], commands: [], summary: null, events: evs, startedUtc: '2026-11-23T00:00:00.000Z' });
  }

  it(`匯出的紀錄版本 ${SESSION_RECORD_VERSION}:合併列帶 merged(count / firstT / lastT / 最大值),不含逐次明細;重播解析與分段解析讀回同樣的列`, async () => {
    expect(SESSION_RECORD_VERSION).toBe(6);
    const live = storeOf(events, 2000);
    const rec = record(live.all());
    expect(rec.version).toBe(6);
    const text = await sessionRecordBlob(rec).text();
    const raw = JSON.parse(text) as { events: Array<Record<string, unknown>> };
    expect(raw.events.length).toBe(51);
    const s03 = raw.events.find((e) => e['itemId'] === 'S03' && e['merged'])!;
    expect(s03['merged']).toEqual({ count: 697, firstT: 16.92, firstTick: 846, lastT: 7197.22, maxNormalSpeed_mps: 0.0657822, maxEnergy_kNm: 7.06002, maxDeflection_pct: 8.55807, maxReaction_kN: 203.578 });
    expect(Object.keys(s03).sort()).toEqual(['data', 'itemId', 'kind', 'merged', 'message', 'phase', 'seq', 'source', 't', 'tick', 'visible']);
    expect(text.length).toBeLessThan(40_000); // 逐筆匯出時光事件就約 0.7 MB
    expect(JSON.stringify(events).length).toBeGreaterThan(600_000);
    const back = parseReplay(text);
    expect(back.errors).toEqual([]);
    expect(back.events).toEqual(live.all());
    expect(parseReplayText(text).data.events).toEqual(live.all());
    expect(rawEventCount(back.events)).toBe(1974);
  });

  it('版本 5 以前的教官站紀錄(逐筆的接觸;上限 2000 筆)讀入時以同樣的規則合併', () => {
    const rec = { ...record(events), version: 5 };
    const back = parseReplay(JSON.stringify(rec));
    expect(back.events).toEqual(storeOf(events).all());
    expect(back.events.length).toBe(51);
  });

  it('parseSimEvent 的 merged:次數須為 ≥ 2 的整數、第一次的時刻不晚於列的 t,否則當作單筆接觸;lastT 取列的 t、缺的最大值退回列上的值;非 fenderContact 不讀', () => {
    const base = { kind: 'fenderContact', tick: 500, t: 10, source: 'engine', itemId: 'F1', seq: 4, data: { fenderId: 'F1', normalSpeed_mps: 0.04, energy_kNm: 3 } };
    expect(parseSimEvent({ ...base, merged: { count: 3, firstT: 2, lastT: 99 } })!.merged).toEqual({ count: 3, firstT: 2, firstTick: 100, lastT: 10, maxNormalSpeed_mps: 0.04, maxEnergy_kNm: 3 });
    expect(parseSimEvent({ ...base, merged: { count: 3, firstT: 2, firstTick: 101, maxNormalSpeed_mps: 0.08, maxEnergy_kNm: 9, maxDeflection_pct: 12, maxReaction_kN: 200 } })!.merged).toEqual({ count: 3, firstT: 2, firstTick: 101, lastT: 10, maxNormalSpeed_mps: 0.08, maxEnergy_kNm: 9, maxDeflection_pct: 12, maxReaction_kN: 200 });
    for (const bad of [{ count: 1, firstT: 2 }, { count: 2.5, firstT: 2 }, { count: '3', firstT: 2 }, { count: 3 }, { count: 3, firstT: 11 }, { count: Number.NaN, firstT: 2 }, 'x', null, [1]]) {
      expect(parseSimEvent({ ...base, merged: bad })!.merged, JSON.stringify(bad)).toBeUndefined();
    }
    expect(parseSimEvent({ ...base, kind: 'quayContact', merged: { count: 3, firstT: 2 } })!.merged).toBeUndefined();
  });

  it('mergeEventRows:已合併的列原樣保留,之後門檻內的逐筆接觸另起一列(讀回的列沒有明細,不再併入)', () => {
    const live = storeOf([contact(0, 'F1'), contact(10, 'F1'), contact(20, 'F1')]);
    const again = mergeEventRows([...live.all(), contact(30, 'F1'), contact(40, 'F1')]);
    expect(rows(again)).toEqual(['F1×1@0', 'F1×2@10–20', 'F1×2@30–40']);
    expect(again[1]).toBe(live.all()[1]);
  });
});

describe('核心 JSON Lines 紀錄(逐筆)的重播解析', () => {
  const header = JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: DT, scenario: { id: 'E14_unberthing_no_tug' } });
  const stateLine = (t: number) => JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / DT) } });
  const eventLine = (e: SimEvent) => JSON.stringify({ kind: 'event', tick: e.tick, event: e });

  it('讀入時合併成與教官站即時事件流相同的列;分段解析結果相同', () => {
    const events = e14vEvents();
    const text = [header, stateLine(0), ...events.map(eventLine), stateLine(7200)].join('\n');
    const data = parseJsonLines(text);
    expect(data.errors).toEqual([]);
    expect(data.events).toEqual(storeOf(events).all());
    expect(data.events.length).toBe(51);
    expect(parseReplayText(text).data.events).toEqual(data.events);
  });

  it('倒帶(restore):合併列截到倒帶點,rewinds[].droppedEvents 計逐筆的事件數;結果與教官站即時倒帶相同', () => {
    const before = [contact(0, 'F1'), contact(10, 'F1'), contact(20, 'F1'), other(22), contact(30, 'F1'), contact(40, 'F1')];
    const after = [contact(26, 'F1'), contact(36, 'F1')];
    const text = [
      header,
      stateLine(0),
      ...before.map(eventLine),
      stateLine(40),
      JSON.stringify({ kind: 'input', tick: 1250, command: { type: 'restore', args: { tick: 1250 } } }),
      stateLine(25.04),
      ...after.map(eventLine),
      stateLine(36),
    ].join('\n');
    const data = parseJsonLines(text);
    expect(data.rewinds).toEqual([{ t: 25, tick: 1250, from: 40, droppedStates: 1, droppedEvents: 2 }]);
    expect(rows(data.events)).toEqual(['F1×1@0', 'alertRaised@22', 'F1×4@10–36']);
    const live = storeOf(before);
    expect(live.dropAfter(25)).toBe(2);
    for (const e of after) live.push(e);
    expect(data.events).toEqual(live.all());
  });
});
