// 練習紀錄的分段解析(lib/record-stream.ts;講評報告頁不把整份紀錄讀成一個字串):
// 分段解析的結果必須與 record-replay.parseReplay(全文) 完全相同(toStrictEqual)——核心紀錄夾具(LF / CRLF)、各版本的教官站紀錄
// (版本 5 精簡投影、版本 4 以前的完整狀態與 t 倒退的倒帶、帶核心 summary)、2 小時規模的合成紀錄(7201 筆 E02)、
// 格式判斷與空白的邊界情況、JSON 的重複鍵 / 鍵順序 / __proto__、隨機破壞 × 隨機切段;
// 「全文是否為單一 JSON 物件與其 format」(報告頁分辨評估表、名冊等檔案)與 JSON.parse(全文) 相同;
// Blob 分段解碼(多位元組字元跨段、BOM、不合法位元組)與 Blob.text() 相同、整個 Blob 只以 stream() 讀一次;states 邊讀邊解析(不是讀完才解析);
// 讀取中定時讓出主執行緒(資料一直備妥的檔案也一樣):巨集任務(計時器、畫面更新)在讀取中途跑得到、看得到小於 100% 的進度。
import { describe, expect, it, vi } from 'vitest';
import { readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { debriefState, summarize } from '../src/lib/debrief.js';
import { parseReplay } from '../src/lib/record-replay.js';
import { RECORD_YIELD_MS, ReplayStreamParser, TopLevelObjectScanner, forEachBlobTextChunk, isJsWhitespace, parseReplayBlob, parseReplayText, recordReadErrorText, yieldToEventLoop, type TopLevelSink } from '../src/lib/record-stream.js';
import { SESSION_RECORD_FORMAT } from '../src/lib/session-record.js';
import type { OwnShipState } from '../src/types/state.js';
import { fixtureText, mutate, randomChunks, rng, sessionRecordText, syntheticSessionRecord } from './support/record-corpus.js';

const fixturesDir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures');

function streamed(chunks: readonly string[]) {
  const p = new ReplayStreamParser();
  for (const c of chunks) p.push(c);
  return p.finish();
}

/** JSON.parse(全文) 的判斷(報告頁 importText 原本的作法) */
function sniff(text: string): { jsonObject: boolean; jsonFormat: unknown } {
  try {
    const o = JSON.parse(text) as unknown;
    if (typeof o === 'object' && o !== null && !Array.isArray(o)) return { jsonObject: true, jsonFormat: (o as Record<string, unknown>)['format'] };
  } catch {
    /* 不是單一 JSON 文件 */
  }
  return { jsonObject: false, jsonFormat: undefined };
}

/** 整份一次、隨機切段、(短文字)逐字切段:都與 parseReplay(全文) 相同;格式判斷與 JSON.parse(全文) 相同。 */
function expectSame(text: string, rand: () => number, label: string): void {
  const ref = parseReplay(text);
  const whole = streamed([text]);
  expect(whole.data, `${label}:整份`).toStrictEqual(ref);
  expect({ jsonObject: whole.jsonObject, jsonFormat: whole.jsonFormat }, `${label}:格式`).toStrictEqual(sniff(text));
  const parts = streamed(randomChunks(text, rand));
  expect(parts.data, `${label}:切段`).toStrictEqual(ref);
  expect({ jsonObject: parts.jsonObject, jsonFormat: parts.jsonFormat }, `${label}:切段格式`).toStrictEqual(sniff(text));
  if (text.length <= 4000) expect(streamed([...text]).data, `${label}:逐字`).toStrictEqual(ref);
}

describe('isJsWhitespace', () => {
  it('與 String.prototype.trim() 去掉的字元相同(全部 UTF-16 碼元)', () => {
    for (let c = 0; c <= 0xffff; c++) {
      if (isJsWhitespace(c) !== (String.fromCharCode(c).trim() === '')) throw new Error(`U+${c.toString(16)} 不一致`);
    }
  });
});

describe('TopLevelObjectScanner', () => {
  function scan(text: string, chunk = text.length || 1) {
    const calls: unknown[] = [];
    const sink: TopLevelSink = {
      member: (k, v) => calls.push(['member', k, v]),
      arrayStart: (k) => calls.push(['start', k]),
      arrayItem: (k, i, v) => calls.push(['item', k, i, v]),
      arrayEnd: (k) => calls.push(['end', k]),
    };
    const s = new TopLevelObjectScanner(sink);
    for (let i = 0; i < text.length; i += chunk) s.feed(text, i, Math.min(text.length, i + chunk));
    return { ok: !s.failed && s.done && s.trailingJsonWs, calls };
  }

  it('頂層成員逐一給、頂層陣列逐筆給;巢狀的值整個交給 JSON.parse', () => {
    const text = ' {"a" : 1 , "b":[ {"x":[1,2,{"y":"]}"}]} , [3], "s\\"t", -1.5e3, null ] ,"c":{"d":[]},"e":"z","f":[],"g":true}\n';
    const want = [
      ['member', 'a', 1],
      ['start', 'b'],
      ['item', 'b', 0, { x: [1, 2, { y: ']}' }] }],
      ['item', 'b', 1, [3]],
      ['item', 'b', 2, 's"t'],
      ['item', 'b', 3, -1500],
      ['item', 'b', 4, null],
      ['end', 'b'],
      ['member', 'c', { d: [] }],
      ['member', 'e', 'z'],
      ['start', 'f'],
      ['end', 'f'],
      ['member', 'g', true],
    ];
    for (const chunk of [text.length, 1, 2, 5, 13]) {
      const r = scan(text.trimStart(), chunk);
      expect(r.ok).toBe(true);
      expect(r.calls).toEqual(want);
    }
    expect(scan('{}').ok).toBe(true);
    expect(scan('{ }  \r\n').ok).toBe(true);
  });

  it('與 JSON.parse 一樣嚴格:多餘 / 缺少的逗號與冒號、未閉合、物件之後還有內容、不合法的值都失敗', () => {
    const bad = ['{', '{"a"}', '{"a":}', '{"a":1,}', '{,"a":1}', '{"a":1 "b":2}', '{"a":[1,]}', '{"a":[,1]}', '{"a":[1 2]}', '{"a":[1}', '{"a":1]', '{"a":tru}', '{"a":01}', '{"a":"x\ny"}', "{'a':1}", '{a:1}', '{"a":1}}', '{"a":1} x', '{"a":{"b":[1,}}', '{"a":[{"b":1,}]}', '{"a":1}\u00a0', '{"a"\u00a0:1}', '{"a":NaN}', '{"a":1e}', '{"a":"\\x"}'];
    for (const text of bad) {
      expect(() => JSON.parse(text), text).toThrow();
      for (const chunk of [text.length, 1, 3]) expect(scan(text, chunk).ok, text).toBe(false);
    }
  });
});

describe('ReplayStreamParser:與 parseReplay(全文) 相同', () => {
  it('全部核心紀錄夾具(LF 與 CRLF;結尾有無換行)', () => {
    const rand = rng(101);
    const files = readdirSync(fixturesDir).filter((f) => f.endsWith('.jsonl'));
    expect(files.length).toBeGreaterThan(30);
    for (const f of files) {
      const text = fixtureText(f);
      expectSame(text, rand, f);
      expectSame(text.replace(/\n/g, '\r\n'), rand, `${f}(CRLF)`);
      expectSame(text.trimEnd(), rand, `${f}(結尾無換行)`);
    }
  }, 60_000);

  it('教官站紀錄:版本 5、版本 4 以前(完整狀態、t 倒退的倒帶)、帶核心 summary、縮排的 JSON', () => {
    const rand = rng(102);
    const v5 = sessionRecordText(syntheticSessionRecord({ samples: 121, events: 30, rewindAfter: 80 }));
    const v4 = sessionRecordText(syntheticSessionRecord({ samples: 121, events: 30, legacy: true, rewindAfter: 80 }));
    const withSummary = sessionRecordText(syntheticSessionRecord({ samples: 61, coreSummary: true }));
    for (const [label, text] of [['v5', v5], ['v4', v4], ['summary', withSummary]] as const) {
      expectSame(text, rand, label);
      expectSame(`${text}\n`, rand, `${label}(結尾換行)`);
      expectSame(JSON.stringify(JSON.parse(text), null, 2), rand, `${label}(縮排)`);
      expectSame(JSON.stringify(JSON.parse(text), null, '\t').replace(/\n/g, '\r\n'), rand, `${label}(Tab 縮排、CRLF)`);
    }
    const r5 = streamed([v5]).data;
    expect(r5.source).toBe('instructor-json');
    expect(r5.states).toHaveLength(121);
    expect(r5.rewinds).toEqual([{ t: 0, tick: 0, from: 80, droppedStates: 80, droppedEvents: 3 }]);
    const r4 = streamed([v4]).data;
    expect(r4.rewinds).toHaveLength(1);
    expect(r4.rewinds[0]).toMatchObject({ from: 80, droppedStates: 79 });
    expect(streamed([withSummary]).data.summary).not.toBeNull();
    // 版本 1(只有最基本的欄位)
    const v1 = JSON.parse(v5) as Record<string, unknown>;
    for (const k of ['events', 'coreSummary', 'sessionInfo', 'recordings', 'rewinds', 'startedUtc']) delete v1[k];
    v1['version'] = 1;
    expectSame(JSON.stringify(v1), rand, 'v1');
  }, 60_000);

  it('鍵的順序、重複鍵、__proto__、states 不是陣列、format 不符:與 JSON.parse 的語意相同', () => {
    const rand = rng(103);
    const rec = JSON.parse(sessionRecordText(syntheticSessionRecord({ samples: 5, events: 4 }))) as Record<string, unknown>;
    const states = JSON.stringify(rec['states']);
    const other = JSON.stringify((rec['states'] as unknown[]).slice(0, 2));
    const { format: _f, states: _s, ...rest } = rec;
    const body = JSON.stringify(rest).slice(1, -1);
    const fmt = `"format":"${SESSION_RECORD_FORMAT}"`;
    const cases: Array<[string, string, number | null]> = [
      ['format 在 states 之後', `{"states":${states},${body},${fmt}}`, 5],
      ['format 在最後、states 在最前', `{"states":${states},${fmt},${body}}`, 5],
      ['states 重複:後者為準', `{${fmt},"states":${states},${body},"states":${other}}`, 2],
      ['states 重複:後者不是陣列', `{${fmt},"states":${states},${body},"states":5}`, null],
      ['states 重複:先不是陣列', `{${fmt},"states":null,${body},"states":${other}}`, 2],
      ['format 重複:後者不符', `{${fmt},"states":${states},${body},"format":"x"}`, null],
      ['format 重複:後者才符合', `{"format":"x","states":${states},${body},${fmt}}`, 5],
      ['沒有 states', `{${fmt},${body}}`, null],
      ['states 是物件', `{${fmt},"states":{"0":1},${body}}`, null],
      ['__proto__ 鍵', `{${fmt},"__proto__":{"states":[1]},"states":${states},${body},"__proto__":[1,2]}`, 5],
      ['constructor / toString 鍵', `{"constructor":1,"toString":[2],${fmt},"states":${states},${body}}`, 5],
      ['其他 format(名冊)帶 states', `{"format":"simosa-brm-roster","states":${states},"trainees":[]}`, null],
      ['空物件', '{}', null],
      ['states 空陣列', `{${fmt},"states":[],${body}}`, 0],
      ['states 內有不是狀態的元素', `{${fmt},"states":[1,null,"x",{},[],${states.slice(1, -1)}],${body}}`, 5],
    ];
    for (const [label, text, n] of cases) {
      expectSame(text, rand, label);
      const r = streamed([text]).data;
      if (n === null) expect(r.errors[0], label).toMatch(/不是教官站紀錄/);
      else expect(r.states, label).toHaveLength(n);
    }
    expect(streamed([cases[14]![1]]).data.errors).toEqual(['states[0] 無法解析', 'states[1] 無法解析', 'states[2] 無法解析', 'states[3] 無法解析', 'states[4] 無法解析']);
  });

  it('格式判斷與空白的邊界情況(開頭 / 結尾的空白與雜訊、只有一行、空行、壞行的行號)', () => {
    const rand = rng(104);
    const jsonl = fixtureText('record-E22d_cultural_communication-core.jsonl');
    const lines = jsonl.trimEnd().split('\n');
    const head = lines[0]!;
    const short = [head, ...lines.slice(1, 6), lines[lines.length - 1]].join('\n');
    const rec = sessionRecordText(syntheticSessionRecord({ samples: 4, events: 3 }));
    const cases: string[] = [
      '',
      ' ',
      '\n\n',
      'hello',
      '[1,2]',
      '"x"',
      '{',
      '{"kind":"header"',
      head,
      `${head}\n`,
      `${head}\r\n`,
      `\n\n${short}`,
      `  \t${short}\n\n`,
      `\ufeff${short}`,
      `\u00a0${short}`,
      `\u2028\n${short}`,
      `${head}\u00a0\n${lines[1]}`,
      `${head} x\n${lines[1]}`,
      `${head}\n\n \n${lines[1]}\n \n`,
      `${head}\nnot json\n${lines[1]}\n[1]\n"x"\n5\n{"kind":5}\n{"nokind":1}\n`,
      `\n${head}\nnot json\n${lines[1]}`,
      `${lines[1]}\n${lines[2]}`,
      short.replace(/\n/g, '\r'),
      rec,
      ` ${rec} `,
      `\n${rec}\n`,
      `\ufeff${rec}`,
      `\u00a0${rec}`,
      `${rec}\u00a0`,
      `${rec}\n\u00a0`,
      `${rec} x`,
      `${rec}\n${rec}`,
      `${rec}\n{}`,
      rec.slice(0, -1),
      rec.slice(0, rec.length >> 1),
      `{"kind":"header","format":"simosa-brm-roster","states":[1,2]}`,
      `{"kind":"header","format":"simosa-brm-roster","states":[1,2]}\n`,
      `{"kind":"header","format":"simosa-brm-roster"}\n \r\n\t`,
      `{"kind":"header","format":"simosa-brm-roster"}\n\u00a0`,
      `{"kind":"header","format":"simosa-brm-roster"}\u00a0\n`,
      `{"kind":1,"format":"${SESSION_RECORD_FORMAT}","states":[]}`,
      `{"format":"${SESSION_RECORD_FORMAT}",\n"states":[]}`,
      `{"format":"simosa-brm-roster","trainees":[{"id":"T1"}],"staff":[]}`,
      `{"format":"simosa-brm-roster",\n "trainees":[]}\n`,
    ];
    cases.forEach((text, i) => expectSame(text, rand, `case#${i}`));
    // 行號:開頭的空行也算(與 parseJsonLines 相同)
    expect(streamed([`\n${head}\nnot json\n${lines[1]}`]).data.errors).toEqual(['第 3 行:JSON 解析失敗']);
    expect(streamed(['hello']).data.errors[0]).toMatch(/無法辨識/);
    expect(streamed([rec.slice(0, -1)]).data.errors[0]).toMatch(/無法辨識/);
    expect(streamed([`${rec} x\n`]).data.errors[0]).toBe('JSON 解析失敗');
  });

  it('隨機破壞 × 隨機切段(固定種子):解析結果與格式判斷都相同', () => {
    // BRM 標記缺 wall 時以現在時刻補上:固定時鐘,兩次解析才可比
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(Date.UTC(2026, 10, 23, 5, 0, 0)));
    try {
      const rand = rng(105);
      const jsonl = fixtureText('record-E22d_cultural_communication-core.jsonl').split('\n');
      const seeds = [
        sessionRecordText(syntheticSessionRecord({ samples: 6, events: 5 })),
        sessionRecordText(syntheticSessionRecord({ samples: 8, events: 5, legacy: true, rewindAfter: 5 })),
        [jsonl[0], ...jsonl.slice(1, 8), ...jsonl.slice(-3)].join('\n'),
        JSON.stringify({ format: 'simosa-brm-roster', version: 1, trainees: [{ id: 'T1', name: '甲' }], staff: [] }),
        JSON.stringify(JSON.parse(sessionRecordText(syntheticSessionRecord({ samples: 3, events: 2 }))), null, 1),
        '{"kind":"header","dt":0.02}',
        '{"kind":"header","states":[1,2],"format":"simosa-brm-roster"}\n',
      ];
      let stillRecords = 0;
      for (let round = 0; round < 1500; round++) {
        let text = seeds[round % seeds.length]!;
        const depth = 1 + Math.floor(rand() * 3);
        for (let k = 0; k < depth; k++) text = mutate(text, rand);
        expectSame(text, rand, `fuzz#${round}`);
        if (parseReplay(text).states.length > 0) stillRecords++;
      }
      // 破壞後仍解析出狀態與完全讀不進來的都要有(兩種路徑都測到)
      expect(stillRecords).toBeGreaterThan(100);
      expect(stillRecords).toBeLessThan(1400);
    } finally {
      vi.useRealTimers();
    }
  }, 120_000);
});

describe('2 小時規模的合成教官站紀錄(7201 筆 E02)', () => {
  const text = sessionRecordText(syntheticSessionRecord());

  it('由 Blob 分段解析與全文解析相同;狀態投影只差每筆的欄位,講評累計結果相同', async () => {
    expect(text.length).toBeGreaterThan(15_000_000);
    const ref = parseReplay(text);
    expect(ref.states).toHaveLength(7201);
    const viaBlob = await parseReplayBlob(new Blob([text]));
    expect(viaBlob.data).toStrictEqual(ref);
    expect(viaBlob.jsonFormat).toBe(SESSION_RECORD_FORMAT);
    const slim = await parseReplayBlob(new Blob([text]), { project: debriefState });
    expect(slim.data).toStrictEqual({ ...ref, states: ref.states.map(debriefState) });
    const local = summarize(slim.data.states);
    expect(local).toStrictEqual(summarize(ref.states));
    // 合成紀錄讓每一項累計都有值(投影若漏了欄位,這裡會不同)
    expect(local).toMatchObject({ samples: 7201, duration_s: 7200, agroundEvents: 1, collisionEvents: 1, frozen_s: 60 });
    for (const k of ['minUkc_m', 'maxRudder_deg', 'maxRot_degPerMin', 'telegraphChanges', 'rudderOrders', 'hardOverCount', 'distance_nm', 'maxSog_kn', 'timeOverSpeedLimit_s', 'timeUkcBelowAlarm_s', 'timeRotOverLimit_s'] as const) expect(local[k], k).toBeGreaterThan(0);
  }, 120_000);

  it('states 邊讀邊解析:讀到一半時已解析約一半的狀態(不是整份讀完才解析);進度依位元組回報', async () => {
    const seen: number[] = [];
    let count = 0;
    const blob = new Blob([text]);
    await parseReplayBlob(blob, {
      project: (s: OwnShipState) => {
        count++;
        return debriefState(s);
      },
      onProgress: (read, total) => {
        expect(total).toBe(blob.size);
        seen.push(count / 7201 - read / total);
      },
    });
    expect(count).toBe(7201);
    expect(seen.length).toBeGreaterThanOrEqual(Math.floor(blob.size / (1 << 20)));
    // 每讀完一段,已解析的狀態比例與已讀位元組比例相差不到 3%(標頭與結尾的其他欄位很小)
    for (const d of seen) expect(Math.abs(d)).toBeLessThan(0.03);
  }, 120_000);
});

describe('Blob 分段解碼', () => {
  it('多位元組字元跨段、BOM、不合法位元組:各段串接後與 Blob.text() 相同', async () => {
    const enc = new TextEncoder();
    const samples: Uint8Array[] = [
      enc.encode('{"note":"中文 🚢 Ω≈ç√","x":"\u00e9\u20ac"}\n第二行 𠮷野家'),
      new Uint8Array([0xef, 0xbb, 0xbf, ...enc.encode('{"kind":"header"}\n')]),
      new Uint8Array([0x7b, 0xff, 0xfe, 0xe4, 0xb8, 0x7d, 0xf0, 0x9f]),
      new Uint8Array([]),
    ];
    for (const bytes of samples) {
      const blob = new Blob([new Uint8Array(bytes)]);
      const want = await blob.text();
      for (const chunkBytes of [1, 2, 3, 4, 5, 7, 1 << 20]) {
        const parts: string[] = [];
        let last = 0;
        await forEachBlobTextChunk(
          blob,
          (t, read) => {
            parts.push(t);
            expect(read).toBeGreaterThanOrEqual(last);
            last = read;
          },
          chunkBytes,
        );
        expect(parts.join(''), `chunk ${chunkBytes}`).toBe(want);
      }
    }
  });

  it('整個 Blob 只以 stream() 讀一次、不分次 slice(核心仍在寫入的紀錄,分次讀取會因檔案變動而失敗);沒有 stream() 時才退回 slice', async () => {
    const text = fixtureText('record-E22d_cultural_communication-core.jsonl');
    const blob = new Blob([text]);
    const stream = vi.spyOn(blob, 'stream');
    const slice = vi.spyOn(blob, 'slice');
    expect((await parseReplayBlob(blob, { chunkBytes: 4096 })).data).toStrictEqual(parseReplay(text));
    expect(stream).toHaveBeenCalledTimes(1);
    expect(slice).not.toHaveBeenCalled();
    // 退回 slice:只有 size / slice 的物件(舊環境、測試替身)
    const slices: Array<[number, number]> = [];
    const sliceOnly = {
      size: blob.size,
      slice: (a: number, b: number) => {
        slices.push([a, b]);
        return new Blob([text]).slice(a, b);
      },
    } as unknown as Blob;
    expect((await parseReplayBlob(sliceOnly, { chunkBytes: 10_000 })).data).toStrictEqual(parseReplay(text));
    expect(slices.length).toBe(Math.ceil(blob.size / 10_000));
    expect(slices[0]).toEqual([0, 10_000]);
    expect(slices[slices.length - 1]![1]).toBe(blob.size);
    // 解析端擲出例外:停止讀取並把例外往外丟
    let chunks = 0;
    await expect(
      forEachBlobTextChunk(
        new Blob([text]),
        () => {
          chunks++;
          throw new Error('解析失敗');
        },
        1000,
      ),
    ).rejects.toThrow('解析失敗');
    expect(chunks).toBe(1);
  });

  it('stream 交出的位元組比 blob.size 少(讀取途中檔案被截短:Chromium 不報錯、交出一部分就結束;第三十一輪審查 MEM-2):以 NotReadableError 拒絕;大小相符時照常', async () => {
    const text = fixtureText('record-E07-core.jsonl');
    const bytes = new TextEncoder().encode(text);
    /** 大小是完整的檔案,stream 只交出前 n 個位元組(每段 pieces 位元組)就結束 */
    const early = (n: number, piece = 3000) =>
      ({
        size: bytes.byteLength,
        stream: () => {
          let off = 0;
          return new ReadableStream<Uint8Array>({
            pull(c) {
              if (off >= n) return c.close();
              c.enqueue(bytes.subarray(off, Math.min(n, off + piece)));
              off += piece;
            },
          });
        },
      }) as unknown as Blob;
    for (const n of [0, 1, 4096, bytes.byteLength - 1]) {
      let last = 0;
      const err = await forEachBlobTextChunk(early(n), (_t, read) => void (last = read), 1000).then(
        () => null,
        (e: unknown) => e,
      );
      expect(err, `只交出 ${n} 位元組`).toBeInstanceOf(Error);
      expect((err as Error).name).toBe('NotReadableError');
      expect((err as Error).message).toBe(`檔案只讀到 ${n} / ${bytes.byteLength} 位元組(讀取途中檔案被截短或改動)`);
      // 已經交出的那些照常交給解析端(之後才知道不夠);呼叫端顯示的是讀取失敗的中文說明
      expect(last).toBe(n);
      expect(recordReadErrorText(err)).toBe('檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)');
    }
    await expect(parseReplayBlob(early(40_000))).rejects.toMatchObject({ name: 'NotReadableError' });
    // 大小相符:照常(與全文解析相同)
    expect((await parseReplayBlob(early(bytes.byteLength))).data).toStrictEqual(parseReplay(text));
    // 最後一行不完整但大小相符(核心仍在寫入的紀錄讀到選取當下的大小):不是讀取失敗,壞行記在 errors
    const partial = new Blob([bytes.subarray(0, 40_000)]);
    const res = await parseReplayBlob(partial);
    expect(res.data.states.length).toBeGreaterThan(0);
    expect(res.data.errors).toHaveLength(1);
    // 空檔:沒有讀到東西也不是讀取失敗
    expect((await parseReplayBlob(new Blob([]))).data.errors[0]).toMatch(/無法辨識/);
  });

  it('parseReplayBlob:小段(7 位元組)讀取含中文的核心紀錄與教官站紀錄,結果與全文解析相同;空檔案 = 無法辨識', async () => {
    const jsonl = fixtureText('record-E22d_cultural_communication-core.jsonl');
    const rec = sessionRecordText(syntheticSessionRecord({ samples: 12, events: 6 }));
    for (const text of [jsonl, rec, `\ufeff${jsonl}`]) {
      const blob = new Blob([text]);
      const viaBlob = await parseReplayBlob(blob, { chunkBytes: 7 });
      expect(viaBlob.data).toStrictEqual(parseReplay(await blob.text()));
    }
    expect((await parseReplayBlob(new Blob([]))).data.errors[0]).toMatch(/無法辨識/);
    expect(parseReplayText(rec).data).toStrictEqual(parseReplay(rec));
  });
});

describe('讀取中讓出主執行緒(第三十輪審查 A-rA-2)', () => {
  /**
   * 資料一直備妥的 Blob 替身:read() 回傳已經兌現的 Promise(只經過微任務)—— Chromium 讀已在快取的檔案時就是這樣,
   * 讀取迴圈不自己讓出的話,整個讀取是一個長任務,計時器與畫面更新都要等它結束。
   */
  function alwaysReadyBlob(text: string, pieceBytes: number): Blob {
    const bytes = new TextEncoder().encode(text);
    return {
      size: bytes.byteLength,
      stream: () => {
        let offset = 0;
        return {
          getReader: () => ({
            read: () => {
              if (offset >= bytes.byteLength) return Promise.resolve({ done: true, value: undefined });
              const value = bytes.subarray(offset, offset + pieceBytes);
              offset += pieceBytes;
              return Promise.resolve({ done: false, value });
            },
            cancel: () => Promise.resolve(),
          }),
        };
      },
    } as unknown as Blob;
  }

  /** 讀取期間每個巨集任務記一次當時的進度(%);回傳 [停止函式, 紀錄] */
  function sampleProgress(progress: () => number): [() => void, number[]] {
    const seen: number[] = [];
    let stopped = false;
    const tick = (): void => {
      if (stopped) return;
      seen.push(progress());
      setTimeout(tick, 0);
    };
    setTimeout(tick, 0);
    return [() => void (stopped = true), seen];
  }

  const text = fixtureText('record-E05-core.jsonl');

  it('yieldToEventLoop:等到下一個巨集任務才繼續(先排的計時器先跑)', async () => {
    const order: string[] = [];
    setTimeout(() => order.push('timer'), 0);
    void Promise.resolve().then(() => order.push('microtask'));
    await yieldToEventLoop();
    order.push('resumed');
    await new Promise((r) => setTimeout(r, 5));
    expect(order.slice(0, 2)).toEqual(['microtask', expect.stringMatching(/^(timer|resumed)$/) as unknown as string]);
    expect(order).toContain('timer');
    expect(RECORD_YIELD_MS).toBeGreaterThan(0);
    expect(RECORD_YIELD_MS).toBeLessThanOrEqual(100);
  });

  it('資料一直備妥的檔案:每段之後讓出(yieldEveryMs 0)時,讀取中途巨集任務跑得到、看得到小於 100% 的進度;結果與全文解析相同', async () => {
    let pct = 0;
    const [stop, seen] = sampleProgress(() => pct);
    const res = await parseReplayBlob(alwaysReadyBlob(text, 8192), { chunkBytes: 8192, yieldEveryMs: 0, onProgress: (read, total) => (pct = Math.floor((read / total) * 100)) });
    stop();
    expect(res.data).toStrictEqual(parseReplay(text));
    expect(pct).toBe(100);
    const partial = seen.filter((p) => p > 0 && p < 100);
    expect(partial.length, `讀取中途看到的進度:${seen.join(',')}`).toBeGreaterThanOrEqual(3);
    expect([...partial].sort((a, b) => a - b)).toEqual(partial);
  });

  it('對照:不讓出(yieldEveryMs Infinity)時整個讀取是一個長任務,中途沒有任何巨集任務跑得到(修正前的行為)', async () => {
    let pct = 0;
    const [stop, seen] = sampleProgress(() => pct);
    const res = await parseReplayBlob(alwaysReadyBlob(text, 8192), { chunkBytes: 8192, yieldEveryMs: Number.POSITIVE_INFINITY, onProgress: (read, total) => (pct = Math.floor((read / total) * 100)) });
    stop();
    expect(res.data).toStrictEqual(parseReplay(text));
    expect(seen.filter((p) => p > 0 && p < 100)).toEqual([]);
  });

  it('預設(每 50 ms 的工作讓出一次):解析久於門檻的紀錄讀取中途至少讓出一次;沒有 stream() 的退回路徑也一樣', async () => {
    // 每段的解析另外空轉 4 ms(模擬大檔:1 MB 的一段在 Chromium 約 7 ms),整份約 40 段
    const big = fixtureText('record-E02_coastal_traffic_strait-core.jsonl');
    const piece = Math.ceil(new TextEncoder().encode(big).byteLength / 40);
    const spin = (): void => {
      const until = performance.now() + 4;
      while (performance.now() < until) {
        /* 佔用主執行緒 */
      }
    };
    for (const blob of [alwaysReadyBlob(big, piece), { size: new Blob([big]).size, slice: (a: number, b: number) => ({ arrayBuffer: () => Promise.resolve(new TextEncoder().encode(big).buffer.slice(a, b)) }) } as unknown as Blob]) {
      let read = 0;
      const [stop, seen] = sampleProgress(() => read);
      let chunks = 0;
      await forEachBlobTextChunk(
        blob,
        (_t, n) => {
          chunks++;
          read = n;
          spin();
        },
        piece,
      );
      stop();
      expect(chunks).toBeGreaterThanOrEqual(40);
      expect(seen.filter((n) => n > 0 && n < blob.size).length, `讀取中途跑到的巨集任務看到的已讀位元組:${seen.join(',')}`).toBeGreaterThanOrEqual(1);
    }
  }, 30_000);
});
