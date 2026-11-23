// 教官站紀錄匯出(第三十輪 W2;lib/session-record.ts sessionRecordChunks / sessionRecordBlob、report-store saveHandoff/takeHandoff):
// 逐欄、逐筆序列化後以 BlobPart[] 組成的 Blob,與修正前「整份 JSON.stringify 成單一字串再包成 Blob」的位元組完全相同
//(中文、表情符號、孤立代理字元、undefined/NaN/-0、巢狀 Date、子 Blob 切得很碎時皆同);講評報告交接存 Blob、報告頁讀回文字;
// 舊版交接({ text })仍可讀。
import { describe, expect, it } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import { SESSION_RECORD_MIME, buildSessionRecord, sessionRecordBlob, sessionRecordChunks, type SessionRecord } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { OwnShipState } from '../src/types/state.js';
import { HANDOFF_KEY, MemoryKvStore, resetReportStoreForTest, saveHandoff, takeHandoff, openReportStore } from '../src/report/report-store.js';

const utf8 = (s: string) => Buffer.from(new TextEncoder().encode(s));

function trickyRecord(): SessionRecord {
  const rec = buildSessionRecord(
    {
      mode: 'full',
      source: 'ws://192.168.77.11:8765/?a="b"&c=\\d',
      scenario: null,
      shipId: 'FSB1',
      sampleInterval_s: 1,
      states: [
        { ...defaultState('FSB1'), t: 0, heading: -0, sog: Number.NaN, stw: Number.POSITIVE_INFINITY, rot: 1e-7, drift: undefined } as unknown as OwnShipState,
        { ...defaultState('FSB1'), t: 1, tick: 50, targets: [{ id: 'X1', name: '海巡 01 號 🚢', heading: 1, cog: 2, sog: 3, loa: 30, aisOn: true, range_nm: 1, bearing_deg: 2, cpa_nm: 0.1, tcpa_min: -3, pos: { x: 1, y: 2 } }] },
      ],
      operations: [{ seq: 1, t: 1, wall: '2026-11-22T00:00:00.000Z', kind: 'rudder', label: '舵令 S 10°\n「複誦」', value: 10 }],
      markers: [],
      commands: [{ seq: 1, t: 0.5, wall: '2026-11-22T00:00:00.000Z', command: { type: 'sessionInfo', args: { note: '孤立代理 \ud800 與   行分隔', when: new Date(Date.UTC(2026, 10, 22)) as unknown as string, nested: [1, undefined, null] } } }],
      summary: null,
      events: [{ kind: 'message', tick: 1, t: 0.02, source: 'instructor', seq: 0, visible: true, data: { text: 'Ω≈ç√ 測試' } }],
      startedUtc: '2026-11-22T00:00:00.000Z',
    },
    new Date(Date.UTC(2026, 10, 22, 1)),
  );
  (rec as unknown as Record<string, unknown>)['extraUndefined'] = undefined;
  (rec as unknown as Record<string, unknown>)['extraFn'] = () => 1;
  // 頂層陣列欄位的元素本身是 undefined / 函式 / symbol:JSON.stringify 寫成 null(逐筆序列化時 JSON.stringify(元素) 回 undefined,須補 'null')
  (rec as unknown as Record<string, unknown>)['extraArray'] = [1, undefined, () => 1, Symbol('x'), null];
  return rec;
}

describe('sessionRecordChunks / sessionRecordBlob', () => {
  it('片段串接 = JSON.stringify(record)(含 undefined、NaN、-0、Date、函式欄位、陣列中的 undefined/函式/symbol)', () => {
    const rec = trickyRecord();
    const chunks = [...sessionRecordChunks(rec)];
    expect(chunks.length).toBeGreaterThan(10);
    expect(chunks.join('')).toBe(JSON.stringify(rec));
    expect(chunks.join('')).toContain('"extraArray":[1,null,null,null,null]');
    // 空陣列、空物件
    const empty = buildSessionRecord({ mode: 'simple', source: '', scenario: null, shipId: 'FSB2', sampleInterval_s: 1, states: [], operations: [], markers: [], commands: [], summary: null });
    expect([...sessionRecordChunks(empty)].join('')).toBe(JSON.stringify(empty));
  });

  it('Blob 位元組與單一字串 UTF-8 相同(子 Blob 切得很碎也一樣);MIME 與原本 downloadText 相同', async () => {
    const rec = trickyRecord();
    const expected = utf8(JSON.stringify(rec));
    for (const chunkChars of [1, 7, 64, 1 << 20]) {
      const blob = sessionRecordBlob(rec, chunkChars);
      expect(blob.type).toBe(SESSION_RECORD_MIME);
      expect(blob.size).toBe(expected.length);
      expect(Buffer.compare(Buffer.from(await blob.arrayBuffer()), expected)).toBe(0);
    }
    expect(SESSION_RECORD_MIME).toBe('application/json;charset=utf-8');
    // 讀回可解析
    const r = parseReplay(await sessionRecordBlob(rec, 7).text());
    expect(r.errors).toEqual([]);
    expect(r.states).toHaveLength(2);
    expect(r.states[1]!.targets[0]!.name).toBe('海巡 01 號 🚢');
  });
});

describe('講評報告交接存 Blob', () => {
  it('saveHandoff(Blob) → takeHandoff 讀回同一段文字(記憶體儲存;IndexedDB 路徑見 report-store-idb.test.ts);舊版 { text } 仍可讀', async () => {
    resetReportStoreForTest();
    const rec = trickyRecord();
    expect(await saveHandoff(sessionRecordBlob(rec, 64))).toBe(false); // node 環境沒有 IndexedDB:記憶體儲存(未保存)
    const store = await openReportStore();
    const raw = await store.get<{ savedUtc: string; blob?: Blob; text?: string }>(HANDOFF_KEY);
    expect(raw?.blob).toBeInstanceOf(Blob);
    expect(raw?.text).toBeUndefined();
    const h = await takeHandoff(store);
    expect(h?.text).toBe(JSON.stringify(rec));
    expect(typeof h?.savedUtc).toBe('string');
    const legacy = new MemoryKvStore();
    await legacy.set(HANDOFF_KEY, { savedUtc: 't', text: '{"format":"x"}' });
    expect(await takeHandoff(legacy)).toEqual({ savedUtc: 't', text: '{"format":"x"}' });
    await legacy.set(HANDOFF_KEY, { savedUtc: 't' });
    expect(await takeHandoff(legacy)).toBeUndefined();
    resetReportStoreForTest();
  });
});
