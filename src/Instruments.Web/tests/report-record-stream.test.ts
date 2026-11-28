// 講評報告頁的紀錄讀取(第三十輪「講評報告站的記憶體尖峰」;happy-dom):
//  - 「開啟檔案」選的檔案、教官站交接的 Blob、重新整理後還原的最近紀錄都分段讀取(lib/record-stream.ts),不呼叫 Blob.text() 把整份紀錄讀成字串;
//  - 2 小時規模的合成教官站紀錄(7201 筆 E02):報告物件與列印版 HTML 與「全文解析 + 完整狀態」(修正前的作法)完全相同;
//  - 最近載入的紀錄在本機儲存存 Blob(不存文字);舊版存的整份文字({ name, text })仍讀得進來並改存成 Blob;舊版的文字交接仍可讀;
//  - 評估表、BRM 標記、名冊、班次、教官與證書等小檔案照舊依 format 匯入;壞檔與讀取失敗顯示錯誤、不影響已載入的紀錄;
//  - 大檔案讀取中顯示進度;超過上限的紀錄不另存;
//  - 另存失敗(IndexedDB 的交易在提交時才中止:核心仍在寫入的紀錄、空間不足):照樣載入,清掉先前記下的那一份並在狀態列說明(第三十輪審查 A-rA-1)。
//  - 核心仍在寫入的紀錄(第三十一輪;lib/record-source.ts):可以重新取得檔案的來源(檔案對話框的 handle、拖放)讀取失敗時從頭重讀,
//    每次都失敗時讀到上限就結束、顯示中文說明;重讀期間又開了別的紀錄時舊的不蓋掉新的;一般檔案輸入的 File 不重讀(中文說明,不是瀏覽器的英文錯誤)。
//    審查後修正:取得檔案的當下狀態列還沒有為這次載入改過字、也沒有排著的重繪(開啟就失敗的重讀不更新狀態列);
//    在「開啟檔案…」上按兩下(對話框還開著又按一下)不停用 showOpenFilePicker、不另開檔案輸入自己的對話框。
// @vitest-environment happy-dom
import { render } from 'lit';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import type { RecordSource } from '../src/lib/record-source.js';
import { sessionRecordBlob } from '../src/lib/session-record.js';
import { buildDebriefReport } from '../src/report/debrief-report.js';
import { createMarkerForm, exportForms } from '../src/report/marker-form.js';
import type { ReportApp } from '../src/report/report-app.js';
import { NOT_REMEMBERED_NOTE } from '../src/report/report-app.js';
import { FakeIDBFactory } from './support/fake-indexeddb.js';
import { HANDOFF_KEY, LAST_RECORD_KEY, LAST_RECORD_MAX_BYTES, openReportStore, resetReportStoreForTest, saveHandoff } from '../src/report/report-store.js';
import { renderReportDoc } from '../src/report/report-views.js';
import { fixtureText, sessionRecordText, syntheticSessionRecord } from './support/record-corpus.js';

const E07 = fixtureText('record-E07-core.jsonl');

type App = ReportApp & { updateComplete: Promise<unknown> };

async function mount(query = '/report.html'): Promise<App> {
  history.replaceState(null, '', query);
  const el = document.createElement('brm-report-app') as App;
  document.body.appendChild(el);
  for (let i = 0; i < 400 && !el.ready; i++) await new Promise((r) => setTimeout(r, 5));
  expect(el.ready).toBe(true);
  await el.updateComplete;
  return el;
}

/** Lit 的範本註解去掉後的 HTML */
function htmlOf(el: Element): string {
  return el.outerHTML.replace(/<!--[^>]*-->/g, '');
}

function sheetHtml(app: App): string {
  return htmlOf(app.querySelector('.sheet[data-doc="report"]')!);
}

describe('講評報告頁:紀錄分段讀取', () => {
  let textSpy: ReturnType<typeof vi.spyOn>;

  beforeAll(async () => {
    await import('../src/report/report-app.js');
  });
  beforeEach(() => {
    resetReportStoreForTest();
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(Date.UTC(2026, 10, 23, 5, 0, 0)));
    textSpy = vi.spyOn(Blob.prototype, 'text');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    resetReportStoreForTest();
  });

  it('開啟 2 小時的教官站紀錄檔:不把整份讀成字串;報告物件與列印版 HTML 與全文解析(修正前的作法)相同;重新整理後由 Blob 還原', async () => {
    const text = sessionRecordText(syntheticSessionRecord());
    const file = new File([text], 'brm-record-2h.json', { type: 'application/json' });
    expect(file.size).toBeGreaterThan(15_000_000);
    const app = await mount();
    const messages: string[] = [];
    const say = (app as unknown as { say(text: string, level?: string): void }).say.bind(app);
    (app as unknown as { say(text: string, level?: string): void }).say = (t, level) => {
      messages.push(t);
      say(t, level);
    };
    await app.openFiles([file]);
    await app.updateComplete;
    expect(textSpy).not.toHaveBeenCalled();
    expect(app.messageLevel).toBe('info');
    expect(app.message).toContain('已載入 brm-record-2h.json:E02_coastal_traffic_strait 狀態 7201 筆、事件 400 筆');
    // 讀取中的進度(大檔案):百分比遞增到 100
    const progress = messages.filter((m) => m.startsWith('讀取 brm-record-2h.json…')).map((m) => Number(/ (\d+)%/.exec(m)![1]));
    expect(progress.length).toBeGreaterThan(5);
    expect([...progress].sort((a, b) => a - b)).toEqual(progress);
    expect(progress[progress.length - 1]).toBe(100);

    // 修正前的作法:整份文字 parseReplay(完整狀態)→ buildDebriefReport
    const before = buildDebriefReport({ data: parseReplay(text), meta: app.meta, forms: app.ws.forms, extraMarkers: [] });
    expect(app.report).toStrictEqual(before);
    expect(before.local).toMatchObject({ samples: 7201, agroundEvents: 1, collisionEvents: 1 });
    const holder = document.createElement('div');
    render(renderReportDoc(before), holder);
    const wantHtml = htmlOf(holder.querySelector('.sheet[data-doc="report"]')!);
    expect(wantHtml.length).toBeGreaterThan(20_000);
    expect(sheetHtml(app)).toBe(wantHtml);

    // 每筆狀態只留講評累計用到的欄位
    expect(Object.keys(app.data!.states[100]!).sort()).toEqual(['depthBelowKeel', 'flags', 'pos', 'rot', 'rudder', 'rudderOrder', 'sog', 't', 'telegraph', 'tick']);

    // 本機儲存:最近載入的紀錄是 Blob(同一個檔案),沒有文字
    const store = await openReportStore();
    const last = await store.get<{ name?: string; blob?: Blob; text?: string }>(LAST_RECORD_KEY);
    expect(last?.name).toBe('brm-record-2h.json');
    expect(last?.text).toBeUndefined();
    expect(last?.blob?.size).toBe(file.size);

    // 重新整理:由 Blob 分段還原,報告相同
    document.body.innerHTML = '';
    const again = await mount();
    expect(textSpy).not.toHaveBeenCalled();
    expect(again.recordName).toBe('brm-record-2h.json');
    expect(again.message).toContain('狀態 7201 筆');
    expect(again.report).toStrictEqual(buildDebriefReport({ data: parseReplay(text), meta: again.meta, forms: again.ws.forms, extraMarkers: [] }));
    expect(sheetHtml(again)).toBe(wantHtml);
  }, 120_000);

  it('教官站交接的 Blob:分段讀取、交接後刪除、記成最近載入的紀錄(Blob);版本 4 以前的紀錄(完整狀態、t 倒退的倒帶)照樣讀', async () => {
    for (const legacy of [false, true]) {
      resetReportStoreForTest();
      const rec = syntheticSessionRecord({ samples: 301, events: 40, legacy, rewindAfter: 200 });
      const text = sessionRecordText(rec);
      expect(await saveHandoff(sessionRecordBlob(rec, 4096))).toBe(false); // happy-dom 沒有 IndexedDB:記憶體儲存
      textSpy.mockClear();
      const app = await mount('/report.html?from=instructor');
      expect(textSpy).not.toHaveBeenCalled();
      expect(app.recordName).toContain('教官站紀錄(');
      expect(app.messageLevel).toBe('info');
      expect(app.data?.source).toBe('instructor-json');
      expect(app.data?.rewinds).toHaveLength(1);
      expect(app.report).toStrictEqual(buildDebriefReport({ data: parseReplay(text), meta: app.meta, forms: [], extraMarkers: [] }));
      const store = await openReportStore();
      expect(await store.get(HANDOFF_KEY)).toBeUndefined();
      const last = await store.get<{ name?: string; blob?: Blob; text?: string }>(LAST_RECORD_KEY);
      expect(last?.text).toBeUndefined();
      expect(new TextDecoder().decode(await last!.blob!.arrayBuffer())).toBe(text);
      document.body.innerHTML = '';
    }
  });

  it('舊版本機儲存:最近載入的紀錄是整份文字({ name, text })→ 讀得進來,並改存成 Blob;舊版的文字交接也可讀', async () => {
    const store = await openReportStore();
    await store.set(LAST_RECORD_KEY, { name: 'old-E07.jsonl', text: E07 });
    const app = await mount();
    expect(app.recordName).toBe('old-E07.jsonl');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.report).toStrictEqual(buildDebriefReport({ data: parseReplay(E07), meta: app.meta, forms: [], extraMarkers: [] }));
    const migrated = await store.get<{ name?: string; blob?: Blob; text?: string }>(LAST_RECORD_KEY);
    expect(migrated?.name).toBe('old-E07.jsonl');
    expect(migrated?.text).toBeUndefined();
    expect(new TextDecoder().decode(await migrated!.blob!.arrayBuffer())).toBe(E07);
    document.body.innerHTML = '';

    // 讀不進來的舊內容:不動它、不丟例外
    await store.set(LAST_RECORD_KEY, { name: 'junk', text: 'not a record' });
    const junk = await mount();
    expect(junk.data).toBeNull();
    expect(await store.get(LAST_RECORD_KEY)).toEqual({ name: 'junk', text: 'not a record' });
    document.body.innerHTML = '';

    // 舊版教官站的文字交接
    const text = sessionRecordText(syntheticSessionRecord({ samples: 31, events: 5 }));
    await saveHandoff(text);
    const fromText = await mount('/report.html?from=instructor');
    expect(fromText.data?.states).toHaveLength(31);
    expect(await store.get(HANDOFF_KEY)).toBeUndefined();
    const last = await store.get<{ blob?: Blob; text?: string }>(LAST_RECORD_KEY);
    expect(last?.text).toBeUndefined();
    expect(new TextDecoder().decode(await last!.blob!.arrayBuffer())).toBe(text);
  });

  it('開啟核心 JSON Lines 紀錄檔與其他檔案(名冊、BRM 標記、評估表)一起選:紀錄分段讀取,其餘依 format 匯入', async () => {
    const app = await mount();
    const record = new File([E07], 'record-E07-core.jsonl');
    const roster = new File([JSON.stringify({ format: 'simosa-brm-roster', version: 1, trainees: [{ id: 'T009', name: '甲' }], staff: [{ name: '陳船長', role: 'instructor' }] })], 'roster.json');
    const markers = new File([JSON.stringify({ format: 'simosa-brm-markers', version: 1, markers: [{ id: 5, category: 'communication', t: 200, wall: '2026-10-17T08:00:00Z', note: '未分享意圖', polarity: 'delta' }] }, null, 2)], 'markers.json');
    await app.openFiles([record, roster, markers]);
    expect(app.fileErrors).toEqual([]);
    expect(app.data?.source).toBe('simcore-jsonl');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.report).toStrictEqual(buildDebriefReport({ data: parseReplay(E07), meta: app.meta, forms: [], extraMarkers: app.extraMarkers }));
    expect(app.ws.session.trainees.map((t) => t.id)).toEqual(['T009']);
    expect(app.ws.session.instructors).toContain('陳船長');
    expect(app.extraMarkers.map((m) => m.note)).toEqual(['未分享意圖']);
    // 小檔案(名冊、標記)才以整份文字匯入;練習紀錄沒有
    expect(textSpy).toHaveBeenCalledTimes(2);
    // 評估表(綁定目前紀錄)
    const form = createMarkerForm({ record: { key: app.key }, assessor: { name: '林船長', role: 'second' } });
    await app.openFiles([new File([exportForms([form])], 'forms.json')]);
    expect(app.ws.forms.map((f) => f.formId)).toEqual([form.formId]);
    expect(app.message).toContain('已匯入評估表 1 份');
    // 最近載入的紀錄仍是那份核心紀錄(其他檔案不會蓋掉)
    const last = await (await openReportStore()).get<{ name?: string; blob?: Blob }>(LAST_RECORD_KEY);
    expect(last?.name).toBe('record-E07-core.jsonl');
    expect(last?.blob?.size).toBe(record.size);
  });

  it('壞檔與讀取失敗:顯示錯誤、保留已載入的紀錄與最近紀錄;其餘檔案照常處理', async () => {
    const app = await mount();
    await app.openFiles([new File([E07], 'good.jsonl')]);
    const failing = { name: 'unreadable.jsonl', size: 3_000_000, slice: () => ({ arrayBuffer: () => Promise.reject(new Error('讀取失敗')) }) } as unknown as File;
    const garbage = new File(['this is not a record'], 'bad.txt');
    const truncated = new File([sessionRecordText(syntheticSessionRecord({ samples: 20 })).slice(0, 5000)], 'truncated.json');
    const roster = new File([JSON.stringify({ format: 'simosa-brm-roster', trainees: [{ id: 'T1', name: '乙' }], staff: [] })], 'roster.json');
    // 核心仍在寫入的紀錄:選取後檔案又被改動,瀏覽器以 NotReadableError 拒絕讀取 → 中文說明(不是瀏覽器的英文訊息)
    const live = { name: 'live.jsonl', size: 3_000_000, slice: () => ({ arrayBuffer: () => Promise.reject(new DOMException('The requested file could not be read', 'NotReadableError')) }) } as unknown as File;
    await app.openFiles([failing, garbage, truncated, roster, live]);
    await app.updateComplete;
    expect(app.fileErrors).toHaveLength(4);
    expect(app.fileErrors[3]).toBe('無法載入「live.jsonl」:檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)');
    expect(app.fileErrors[0]).toBe('無法載入「unreadable.jsonl」:無法讀取檔案(請重新選取一次;核心仍在寫入這份紀錄時也可以待練習結束後再開啟);原始錯誤:Error: 讀取失敗');
    expect(app.fileErrors[1]).toBe('無法載入紀錄「bad.txt」:無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)');
    expect(app.fileErrors[2]).toBe('無法載入紀錄「truncated.json」:無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)');
    expect(app.querySelector('.msg.error')!.textContent).toContain('unreadable.jsonl');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.recordName).toBe('good.jsonl');
    expect(app.ws.session.trainees.map((t) => t.id)).toEqual(['T1']);
    expect((await (await openReportStore()).get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('good.jsonl');
  });

  it('讀取途中檔案被截短(stream 交出一部分就結束、不報錯;第三十一輪審查 MEM-2):當作讀取失敗,不把半份紀錄當成整份載入', async () => {
    const E01 = fixtureText('record-E01-core.jsonl');
    const app = await mount();
    await app.openFiles([new File([E07], 'good.jsonl')]);
    const bytes = new TextEncoder().encode(E01);
    const cut = Math.floor(bytes.byteLength * 0.4);
    // 檔案大小是選取當下的(完整的 E01),stream 只交出前四成就結束(實測 Chromium:讀取途中檔案被別的程序截短時就是這樣)
    const short = {
      name: 'cut.jsonl',
      size: bytes.byteLength,
      stream: () =>
        new ReadableStream<Uint8Array>({
          start(c) {
            c.enqueue(bytes.subarray(0, cut));
            c.close();
          },
        }),
    } as unknown as File;
    await app.openFiles([short]);
    await app.updateComplete;
    expect(app.fileErrors).toEqual(['無法載入「cut.jsonl」:檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)']);
    expect(app.messageLevel).toBe('error');
    // 原本載入的紀錄與另存的那一份不動
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.recordName).toBe('good.jsonl');
    expect((await (await openReportStore()).get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('good.jsonl');
    // 對照:同樣的內容若就是整個檔案(大小相符;例如核心仍在寫入、最後一行還沒寫完的紀錄),照常載入
    await app.openFiles([new File([bytes.subarray(0, cut)], 'partial.jsonl')]);
    expect(app.recordName).toBe('partial.jsonl');
    expect(app.data?.header.scenarioId).toBe('E01_baseline');
    expect(app.messageLevel).toBe('info');
  });

  it('還原最近紀錄還沒讀完時使用者又開了別的紀錄:較早開始、較晚讀完的還原結果作廢,不蓋掉後來開的紀錄', async () => {
    const store = await openReportStore();
    // 讀得很慢的最近紀錄(E01;每段 60 ms 才回來)
    const e01 = new Blob([fixtureText('record-E01-core.jsonl')]);
    const slow = { size: e01.size, slice: (a: number, b: number) => ({ arrayBuffer: () => new Promise<ArrayBuffer>((resolve) => setTimeout(() => void e01.slice(a, b).arrayBuffer().then(resolve), 60)) }) };
    await store.set(LAST_RECORD_KEY, { name: 'slow-E01.jsonl', blob: slow });
    history.replaceState(null, '', '/report.html');
    const app = document.createElement('brm-report-app') as App;
    document.body.appendChild(app);
    // 等到還原開始讀取(本機儲存已開啟)但還沒讀完
    for (let i = 0; i < 100 && (app as unknown as { loadSeq: number }).loadSeq === 0; i++) await new Promise((r) => setTimeout(r, 1));
    expect(app.ready).toBe(false);
    expect(app.data).toBeNull();
    await app.openFiles([new File([E07], 'user-E07.jsonl')]);
    expect(app.recordName).toBe('user-E07.jsonl');
    for (let i = 0; i < 400 && !app.ready; i++) await new Promise((r) => setTimeout(r, 5));
    expect(app.ready).toBe(true);
    await app.updateComplete;
    // 還原(E01)讀完了,但畫面與最近紀錄仍是使用者開的 E07
    expect(app.recordName).toBe('user-E07.jsonl');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.message).toContain('已載入 user-E07.jsonl');
    expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('user-E07.jsonl');
  });

  it('重新整理後另存的那一份讀不到(無痕視窗存的是原檔案的參照,原檔案之後被改動):中文說明、清掉那一份(下次重新整理不再報錯);解析不了的內容照舊不動', async () => {
    const store = await openReportStore();
    // 讀取本身失敗的 Blob(實測 Chromium 無痕視窗:存了核心仍在寫入的紀錄,重新整理後讀取以 TypeError: network error 失敗)
    const broken: { size: number; slice: () => unknown; stream: () => unknown } = { size: 250_000, slice: () => broken, stream: () => ({ getReader: () => ({ read: () => Promise.reject(new TypeError('network error')), cancel: () => Promise.resolve() }) }) };
    await store.set(LAST_RECORD_KEY, { name: 'live.jsonl', blob: broken });
    const app = await mount();
    expect(app.data).toBeNull();
    expect(app.messageLevel).toBe('error');
    expect(app.message).toBe('無法還原先前載入的紀錄「live.jsonl」:另存於瀏覽器的那一份已經讀不到(原檔案之後被改動或移走時會這樣),請重新開啟檔案');
    expect(await store.get(LAST_RECORD_KEY)).toBeUndefined();
    document.body.innerHTML = '';
    const again = await mount();
    expect(again.message).toBe('');
    expect(again.data).toBeNull();
    // 之後照常開檔、另存
    await again.openFiles([new File([E07], 'ok.jsonl')]);
    expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('ok.jsonl');
    document.body.innerHTML = '';
    // 讀得到但不是紀錄(解析不了):顯示一般的錯誤,不清掉(與舊版文字內容相同的處理)
    await store.set(LAST_RECORD_KEY, { name: 'junk.bin', blob: new Blob(['not a record']) });
    const junk = await mount();
    expect(junk.message).toContain('無法載入紀錄「junk.bin」');
    expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('junk.bin');
  });

  it('超過上限的紀錄:照樣載入,但不另存於瀏覽器(並清掉先前記下的那一份),訊息說明重新整理後需重新開啟', async () => {
    const app = await mount();
    await app.openFiles([new File([E07], 'small.jsonl')]);
    const store = await openReportStore();
    expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('small.jsonl');
    // 大小超過上限的檔案替身:內容仍是 E07(超出實際內容的區段讀到空的)
    const real = new Blob([E07]);
    const chunk = 64 * 1024 * 1024;
    const huge = { name: 'huge.jsonl', size: LAST_RECORD_MAX_BYTES + 1, slice: (a: number, b: number) => real.slice(a, b) } as unknown as File;
    const { parseReplayBlob } = await import('../src/lib/record-stream.js');
    expect((await parseReplayBlob(huge, { chunkBytes: chunk })).data.states.length).toBeGreaterThan(0);
    await app.openFiles([huge]);
    expect(app.recordName).toBe('huge.jsonl');
    expect(app.messageLevel).toBe('info');
    expect(app.message).toContain('已載入 huge.jsonl');
    expect(app.message).toContain('檔案 512 MB,不另存於瀏覽器,重新整理後請重新開啟檔案');
    expect(await store.get(LAST_RECORD_KEY)).toBeUndefined();
  }, 60_000);

  describe('另存失敗(IndexedDB:請求成功、交易在提交時才中止)', () => {
    let factory: FakeIDBFactory;
    const E01 = fixtureText('record-E01-core.jsonl');
    /** 只有名為 name 的紀錄在提交時失敗(Chromium 存「選取之後內容又變動的檔案」時的錯誤) */
    const failFor = (name: string) => (writes: ReadonlyArray<{ op: string; key: unknown; value?: unknown }>) =>
      writes.some((w) => w.op === 'put' && w.key === LAST_RECORD_KEY && (w.value as { name?: unknown } | undefined)?.name === name) ? new DOMException('Failed to write blobs (InvalidBlob)', 'DataError') : null;

    beforeEach(() => {
      factory = new FakeIDBFactory();
      vi.stubGlobal('indexedDB', factory);
      resetReportStoreForTest();
    });

    it('核心仍在寫入的紀錄:照樣載入;先前記下的那一份清掉(重新整理不會還原成別的練習),狀態列說明未另存', async () => {
      const app = await mount();
      expect(app.persistent).toBe(true);
      await app.openFiles([new File([E07], 'earlier-E07.jsonl')]);
      const store = await openReportStore();
      expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('earlier-E07.jsonl');
      expect(app.message).not.toContain(NOT_REMEMBERED_NOTE);

      factory.commitFailure = failFor('live-E01.jsonl');
      await app.openFiles([new File([E01], 'live-E01.jsonl')]);
      await app.updateComplete;
      expect(app.recordName).toBe('live-E01.jsonl');
      expect(app.data?.header.scenarioId).toBe('E01_baseline');
      expect(app.messageLevel).toBe('info');
      expect(app.fileErrors).toEqual([]);
      expect(app.message).toMatch(/^已載入 live-E01\.jsonl:E01_baseline 狀態 \d+ 筆/);
      expect(app.message.endsWith(`;${NOT_REMEMBERED_NOTE}`)).toBe(true);
      expect(app.querySelector('.msg')!.textContent).toContain('未能另存於瀏覽器');
      expect(await store.get(LAST_RECORD_KEY)).toBeUndefined();

      // 重新整理:沒有紀錄可還原(不是還原成 earlier-E07.jsonl)
      document.body.innerHTML = '';
      const again = await mount();
      expect(again.data).toBeNull();
      expect(again.recordName).toBe('');

      // 之後另存恢復正常(練習結束、檔案不再變動):記得住,訊息不再帶說明
      factory.commitFailure = null;
      await again.openFiles([new File([E01], 'live-E01.jsonl')]);
      expect(again.message).not.toContain(NOT_REMEMBERED_NOTE);
      expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('live-E01.jsonl');
    });

    it('另存失敗的那一份之後又載入了別的紀錄:不清掉後來那一份,也不補說明', async () => {
      const app = await mount();
      const store = await openReportStore();
      factory.commitFailure = failFor('live.jsonl');
      const remember = (app as unknown as { rememberRecord(name: string, blob: Blob): Promise<void> }).rememberRecord.bind(app);
      app.loadRecordText(E07, 'later-E07.jsonl', false);
      // 兩次另存接連開始(前一次的交易還沒提交):前一次失敗、後一次成功
      await Promise.all([remember('live.jsonl', new Blob([E01])), remember('later-E07.jsonl', new Blob([E07]))]);
      expect((await store.get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('later-E07.jsonl');
      expect(app.message).toContain('已載入 later-E07.jsonl');
      expect(app.message).not.toContain(NOT_REMEMBERED_NOTE);
    });

    it('儲存空間不足(連清除也失敗):報告照樣載入、不丟例外,狀態列說明未另存', async () => {
      const app = await mount();
      factory.commitFailure = () => new DOMException('The quota has been exceeded.', 'QuotaExceededError');
      await app.openFiles([new File([E07], 'full-disk.jsonl')]);
      expect(app.recordName).toBe('full-disk.jsonl');
      expect(app.messageLevel).toBe('info');
      expect(app.message).toContain(NOT_REMEMBERED_NOTE);
      expect(app.report).not.toBeNull();
    });
  });
  describe('核心仍在寫入的紀錄(讀取失敗時重新取得檔案再讀)', () => {
    const E01 = fixtureText('record-E01-core.jsonl');
    const networkError = (): TypeError => new TypeError('network error');
    const NO_WAIT = { sleep: () => Promise.resolve(), random: () => 0.5 };
    /** 一讀就以 TypeError: network error 失敗的檔案(Chromium:檔案在選取後被改動) */
    const staleFile = (name: string, opened: { n: number } = { n: 0 }) =>
      ({
        name,
        size: 250_000,
        stream: () => {
          opened.n++;
          return { getReader: () => ({ read: () => Promise.reject(networkError()), cancel: () => Promise.resolve() }) };
        },
      }) as unknown as File;
    /** 可以重新取得檔案的來源:每次 open() 依序交出 blobs 的下一個(用完之後一直是最後一個) */
    function reopenable(name: string, blobs: readonly Blob[]): RecordSource & { opens: number } {
      const src = {
        name,
        opens: 0,
        reopenable: true,
        open(): Promise<Blob> {
          const b = blobs[Math.min(src.opens, blobs.length - 1)]!;
          src.opens++;
          return Promise.resolve(b);
        },
      };
      return src;
    }
    const sayLog = (app: App): string[] => {
      const messages: string[] = [];
      const self = app as unknown as { say(text: string, level?: string): void };
      const say = self.say.bind(app);
      self.say = (t, level) => {
        messages.push(t);
        say(t, level);
      };
      return messages;
    };

    it('第一次 read() 就以 TypeError: network error 失敗:重新取得檔案、重讀成功;報告與一次讀成功時相同,另存的是讀成功的那一份', async () => {
      const plain = await mount();
      await plain.openFiles([new File([E07], 'live.jsonl')]);
      const want = { data: plain.data, sheet: sheetHtml(plain), message: plain.message };
      document.body.innerHTML = '';
      resetReportStoreForTest();
      const app = await mount();
      const messages = sayLog(app);
      const fresh = new File([E07], 'live.jsonl');
      const opened = { n: 0 };
      const source = reopenable('live.jsonl', [staleFile('live.jsonl', opened), fresh]);
      await app.openSources([source], NO_WAIT);
      await app.updateComplete;
      expect(source.opens).toBe(2);
      expect(opened.n).toBe(1);
      expect(app.fileErrors).toEqual([]);
      expect(app.messageLevel).toBe('info');
      // 開啟就失敗的重讀不更新狀態列(什麼都還沒有顯示過;取得檔案與開啟之間也不可以有畫面更新):訊息只有最後的結果
      expect(messages).toEqual([want.message]);
      expect(app.data).toStrictEqual(want.data);
      expect(sheetHtml(app)).toBe(want.sheet);
      const last = await (await openReportStore()).get<{ name?: string; blob?: Blob }>(LAST_RECORD_KEY);
      expect(last?.name).toBe('live.jsonl');
      expect(last?.blob).toBe(fresh);
    });

    it('讀到一半失敗(8 MB 以上、進度已經顯示):從頭重讀,狀態列寫明是重新讀取、進度從頭算;狀態不重複、不缺漏', async () => {
      const app = await mount();
      await app.openFiles([new File([E07], 'e07.jsonl')]);
      const wantStates = app.data!.states;
      const messages = sayLog(app);
      // E07 分成兩半,中間夾 9 MB 的空白行:前半已經進了解析器之後才失敗
      const text = `${E07.trimEnd()}\n`;
      const cutAt = text.indexOf('\n', Math.floor(text.length / 2)) + 1;
      const pad = ' '.repeat(9 * 1024 * 1024 - 1) + '\n';
      const bytes = new TextEncoder().encode(text.slice(0, cutAt) + pad + text.slice(cutAt));
      const piece = 3 * 1024 * 1024;
      const failing = {
        name: 'big.jsonl',
        size: bytes.byteLength,
        stream: () => {
          let off = 0;
          return new ReadableStream<Uint8Array>({
            pull(c) {
              if (off >= 2 * piece) return c.error(networkError());
              c.enqueue(bytes.subarray(off, off + piece));
              off += piece;
            },
          });
        },
      } as unknown as File;
      const whole = {
        name: 'big.jsonl',
        size: bytes.byteLength,
        stream: () => {
          let off = 0;
          return new ReadableStream<Uint8Array>({
            pull(c) {
              if (off >= bytes.byteLength) return c.close();
              c.enqueue(bytes.subarray(off, off + piece));
              off += piece;
            },
          });
        },
      } as unknown as File;
      const source = reopenable('big.jsonl', [failing, whole]);
      await app.openSources([source], NO_WAIT);
      expect(source.opens).toBe(2);
      expect(app.fileErrors).toEqual([]);
      // 進度每讀 1 MB(分段的上限)回報一次:第一次讀了 6 MB(兩段各 3 MB)之後失敗
      const total = (bytes.byteLength / 1048576).toFixed(1);
      const MB = 1024 * 1024;
      const pct = (n: number) => Math.floor((Math.min(bytes.byteLength, n * MB) / bytes.byteLength) * 100);
      const again = '(檔案在讀取時被改動,第 1 次重新讀取)';
      const firstPass = [1, 2, 3, 4, 5, 6].map((n) => `讀取 big.jsonl… ${pct(n)}%(共 ${total} MB)`);
      const secondPass = Array.from({ length: Math.ceil(bytes.byteLength / MB) }, (_, i) => `重新讀取 big.jsonl… ${pct(i + 1)}%(共 ${total} MB)${again}`);
      expect(pct(6)).toBeGreaterThan(60);
      // 進度不會無聲地從六成跳回去:重讀的每一格百分比都寫明是重新讀取、從頭算到 100%(重讀的第一段讀到之前,原本的那一格留著)
      const progress = [...firstPass, ...secondPass];
      expect(secondPass[0]).toBe(`重新讀取 big.jsonl… ${pct(1)}%(共 ${total} MB)(檔案在讀取時被改動,第 1 次重新讀取)`);
      expect(messages.slice(0, progress.length)).toEqual(progress);
      expect(messages[progress.length]).toContain('已載入 big.jsonl');
      expect(app.recordName).toBe('big.jsonl');
      // 前半在第一次已經解析過:重讀是新的解析器,筆數與內容和一次讀成功時相同
      expect(app.data!.states.length).toBe(wantStates.length);
      expect(app.data!.states).toStrictEqual(wantStates);
      expect(app.message).toContain(`已載入 big.jsonl:E07_steering_failure 狀態 ${wantStates.length} 筆`);
    }, 30_000);

    it('每一次取得檔案的當下,狀態列都還沒有為這次載入改過字、也沒有排著的重繪(審查後修正:取得檔案與開啟之間不更新畫面)', async () => {
      // 瀏覽器在取得檔案的當下記下修改時間、開啟時核對:中間插進一次重繪,檔案被改動的機會就變大(重播檢視實測 4–6 倍)
      const app = await mount();
      await app.openFiles([new File([E07], 'good.jsonl')]);
      await app.updateComplete;
      const loaded = app.message;
      const messages = sayLog(app);
      const pending = () => (app as unknown as { isUpdatePending: boolean }).isUpdatePending;
      const atOpen: Array<{ message: string; pending: boolean }> = [];
      const fresh = new File([E01], 'live.jsonl');
      const blobs = [staleFile('live.jsonl'), staleFile('live.jsonl'), staleFile('live.jsonl'), fresh];
      let opens = 0;
      const source: RecordSource = {
        name: 'live.jsonl',
        reopenable: true,
        open: () => {
          atOpen.push({ message: app.message, pending: pending() });
          return Promise.resolve(blobs[opens++]!);
        },
      };
      // 等待(10、30 ms 那兩格)期間讓排著的更新跑完:改過字的話這裡就畫出來了
      await app.openSources([source], { random: () => 0.5, sleep: () => new Promise((r) => setTimeout(r, 2)) });
      expect(opens).toBe(4);
      const quiet = { message: loaded, pending: false };
      expect(atOpen).toEqual([quiet, quiet, quiet, quiet]);
      expect(messages).toHaveLength(1);
      expect(messages[0]).toContain('已載入 live.jsonl:E01_baseline');
      expect(app.data?.header.scenarioId).toBe('E01_baseline');
    });

    it('每次都失敗:讀到上限就結束(不無限重試),訊息是中文說明;原本載入的紀錄與另存的那一份不動', async () => {
      const app = await mount();
      await app.openFiles([new File([E07], 'good.jsonl')]);
      const opened = { n: 0 };
      const source = reopenable('live.jsonl', [staleFile('live.jsonl', opened)]);
      await app.openSources([source], NO_WAIT);
      await app.updateComplete;
      expect(source.opens).toBe(6);
      expect(opened.n).toBe(6);
      expect(app.fileErrors).toEqual(['無法載入「live.jsonl」:檔案持續被改動,重新讀取 5 次後仍讀不到(請待練習結束後再開啟,或稍後重新選取一次)']);
      expect(app.messageLevel).toBe('error');
      expect(app.querySelector('.msg.error')!.textContent).not.toMatch(/network error|TypeError/);
      expect(app.recordName).toBe('good.jsonl');
      expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
      expect((await (await openReportStore()).get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('good.jsonl');
      // 檔案不見了(重新取得時 NotFoundError):不再重試
      let opens = 0;
      const gone: RecordSource = { name: 'gone.jsonl', reopenable: true, open: () => (opens++ === 0 ? Promise.resolve(staleFile('gone.jsonl')) : Promise.reject(new DOMException('could not be found', 'NotFoundError'))) };
      await app.openSources([gone], NO_WAIT);
      expect(opens).toBe(2);
      expect(app.fileErrors).toEqual(['無法載入「gone.jsonl」:找不到檔案(已被刪除、改名或移走,或所在的磁碟已移除):請確認檔案還在之後重新選取一次']);
    });

    it('一般檔案輸入的 File(openFiles):讀取失敗不重讀(實測同一個 File 再讀幾次都一樣),TypeError: network error 顯示成中文說明;其餘檔案照常處理', async () => {
      const app = await mount();
      await app.openFiles([new File([E07], 'good.jsonl')]);
      const opened = { n: 0 };
      const roster = new File([JSON.stringify({ format: 'simosa-brm-roster', trainees: [{ id: 'T7', name: '丙' }], staff: [] })], 'roster.json');
      await app.openFiles([staleFile('live.jsonl', opened), roster]);
      await app.updateComplete;
      expect(opened.n).toBe(1);
      expect(app.fileErrors).toEqual(['無法載入「live.jsonl」:檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)']);
      expect(app.querySelector('.msg.error')!.textContent).not.toMatch(/network error|TypeError/);
      expect(app.recordName).toBe('good.jsonl');
      expect(app.ws.session.trainees.map((t) => t.id)).toEqual(['T7']);
    });

    it('重讀期間又開了別的紀錄:舊的那一次不再重讀、不報錯,不蓋掉後來那一份的畫面、訊息與另存', async () => {
      const app = await mount();
      let release: () => void = () => undefined;
      const waiting = new Promise<void>((r) => (release = r));
      const never = { n: 0 };
      const source = reopenable('slow-live.jsonl', [staleFile('slow-live.jsonl'), staleFile('slow-live.jsonl', never)]);
      let sleeping = false;
      const first = app.openSources([source], { delaysMs: [50], random: () => 0.5, sleep: () => ((sleeping = true), waiting) });
      for (let i = 0; i < 200 && !sleeping; i++) await new Promise((r) => setTimeout(r, 1));
      // 正在等待重讀(開啟就失敗的重讀不更新狀態列)
      expect(sleeping).toBe(true);
      expect(app.message).toBe('');
      await app.openFiles([new File([E01], 'user-E01.jsonl')]);
      expect(app.recordName).toBe('user-E01.jsonl');
      const loaded = app.message;
      release();
      await first;
      await app.updateComplete;
      expect(source.opens).toBe(1);
      expect(never.n).toBe(0);
      expect(app.recordName).toBe('user-E01.jsonl');
      expect(app.data?.header.scenarioId).toBe('E01_baseline');
      expect(app.message).toBe(loaded);
      expect(app.messageLevel).toBe('info');
      expect(app.fileErrors).toEqual([]);
      expect((await (await openReportStore()).get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('user-E01.jsonl');
      // 舊的那一次重讀到一半(讀得很慢)時新的已經載入:舊的讀完也不套用,進度訊息不再顯示
      const bytes = new TextEncoder().encode(E07);
      let unblock: () => void = () => undefined;
      const gate = new Promise<void>((r) => (unblock = r));
      const slowWhole = {
        name: 'slow-E07.jsonl',
        size: bytes.byteLength,
        stream: () => {
          let off = 0;
          return new ReadableStream<Uint8Array>({
            async pull(c) {
              if (off > 0) await gate;
              if (off >= bytes.byteLength) return c.close();
              c.enqueue(bytes.subarray(off, off + 50_000));
              off += 50_000;
            },
          });
        },
      } as unknown as File;
      const second = app.openSources([reopenable('slow-E07.jsonl', [staleFile('slow-E07.jsonl'), slowWhole])], NO_WAIT);
      await new Promise((r) => setTimeout(r, 20));
      await app.openFiles([new File([E01], 'user-E01-again.jsonl')]);
      unblock();
      await second;
      expect(app.recordName).toBe('user-E01-again.jsonl');
      expect(app.message).toContain('已載入 user-E01-again.jsonl');
      expect(app.fileErrors).toEqual([]);
    });

    it('「開啟檔案…」:瀏覽器有 showOpenFilePicker 時改開它(可複選;拿到檔案 handle,讀取失敗可以重讀);用不了時退回檔案輸入自己的對話框', async () => {
      let n = 0;
      const getFile = vi.fn(() => Promise.resolve(n++ === 0 ? staleFile('live.jsonl') : new File([E07], 'live.jsonl')));
      const rosterFile = new File([JSON.stringify({ format: 'simosa-brm-roster', trainees: [{ id: 'T8', name: '丁' }], staff: [] })], 'roster.json');
      const show = vi.fn((_options?: unknown) =>
        Promise.resolve([
          { kind: 'file', name: 'live.jsonl', getFile },
          { kind: 'file', name: 'roster.json', getFile: () => Promise.resolve(rosterFile) },
        ]),
      );
      vi.stubGlobal('showOpenFilePicker', show);
      const app = await mount();
      const input = app.querySelector<HTMLInputElement>('input.file-input')!;
      const click = new Event('click', { bubbles: true, cancelable: true });
      input.dispatchEvent(click);
      expect(click.defaultPrevented).toBe(true);
      expect(show).toHaveBeenCalledWith({ multiple: true, id: 'simosa-brm-records', types: [{ description: '練習紀錄與匯出檔', accept: { 'application/json': ['.jsonl', '.json', '.txt'] } }] });
      for (let i = 0; i < 400 && app.ws.session.trainees.length === 0; i++) await new Promise((r) => setTimeout(r, 5));
      expect(getFile).toHaveBeenCalledTimes(2);
      expect(app.recordName).toBe('live.jsonl');
      expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
      expect(app.ws.session.trainees.map((t) => t.id)).toEqual(['T8']);
      expect(app.fileErrors).toEqual([]);
      // 其他匯入用的檔案輸入(評估表、名冊…)不攔
      app.tab = 'session';
      await app.updateComplete;
      const others = [...app.querySelectorAll<HTMLInputElement>('input[type="file"]')].filter((i) => !i.classList.contains('file-input'));
      expect(others.length).toBeGreaterThan(0);
      for (const other of others) {
        const ev = new Event('click', { bubbles: true, cancelable: true });
        other.dispatchEvent(ev);
        expect(ev.defaultPrevented).toBe(false);
      }
      // 對話框一開就被取消(開不起來;自動化攔截):改開檔案輸入自己的對話框,之後不再攔
      const calls = show.mock.calls.length;
      show.mockImplementationOnce(() => Promise.reject(new DOMException('Intercepted by Page.setInterceptFileChooserDialog().', 'AbortError')));
      const main = app.querySelector<HTMLInputElement>('input.file-input')!;
      const fallbackClicks: boolean[] = [];
      main.click = () => {
        const ev = new Event('click', { bubbles: true, cancelable: true });
        main.dispatchEvent(ev);
        fallbackClicks.push(ev.defaultPrevented);
      };
      main.dispatchEvent(new Event('click', { bubbles: true, cancelable: true }));
      await new Promise((r) => setTimeout(r, 20));
      expect(fallbackClicks).toEqual([false]);
      expect(show.mock.calls.length).toBe(calls + 1);
      const later = new Event('click', { bubbles: true, cancelable: true });
      main.dispatchEvent(later);
      expect(later.defaultPrevented).toBe(false);
      expect(show.mock.calls.length).toBe(calls + 1);
    });

    it('在「開啟檔案…」上按兩下(對話框還開著又按一下):不停用 showOpenFilePicker、不另開檔案輸入自己的對話框;選到的檔案照常載入、讀取失敗照常重讀', async () => {
      // 實測(Chromium):對話框開著時再呼叫 showOpenFilePicker 會以「NotAllowedError: … File picker already active.」拒絕
      let n = 0;
      const getFile = vi.fn(() => Promise.resolve(n++ === 0 ? staleFile('live.jsonl') : new File([E07], 'live.jsonl')));
      let choose: (handles: unknown[]) => void = () => undefined;
      let active = false;
      const show = vi.fn((_options?: unknown) => {
        if (active) return Promise.reject(new DOMException("Failed to execute 'showOpenFilePicker' on 'Window': File picker already active.", 'NotAllowedError'));
        active = true;
        return new Promise<unknown[]>((resolve) => {
          choose = (handles) => {
            active = false;
            resolve(handles);
          };
        });
      });
      vi.stubGlobal('showOpenFilePicker', show);
      const app = await mount();
      const input = app.querySelector<HTMLInputElement>('input.file-input')!;
      const fallbackClicks: number[] = [];
      input.click = () => fallbackClicks.push(1);
      const clicks = [0, 1, 2].map(() => new Event('click', { bubbles: true, cancelable: true }));
      input.dispatchEvent(clicks[0]!);
      await new Promise((r) => setTimeout(r, 5));
      // 第二、三下:對話框還開著
      input.dispatchEvent(clicks[1]!);
      await new Promise((r) => setTimeout(r, 30));
      input.dispatchEvent(clicks[2]!);
      await new Promise((r) => setTimeout(r, 30));
      // 三下都攔下檔案輸入自己的對話框;沒有退回(input.click()),也沒有再向瀏覽器要第二個對話框
      expect(clicks.map((c) => c.defaultPrevented)).toEqual([true, true, true]);
      expect(fallbackClicks).toEqual([]);
      expect(show).toHaveBeenCalledTimes(1);
      // 使用者在(唯一的)對話框選了檔案:照常載入,第一次取得的檔案讀不到 → 重新取得再讀
      choose([{ kind: 'file', name: 'live.jsonl', getFile }]);
      for (let i = 0; i < 400 && app.recordName !== 'live.jsonl'; i++) await new Promise((r) => setTimeout(r, 5));
      expect(getFile).toHaveBeenCalledTimes(2);
      expect(app.recordName).toBe('live.jsonl');
      expect(app.fileErrors).toEqual([]);
      expect(fallbackClicks).toEqual([]);
      // 之後再按:仍然開 showOpenFilePicker(沒有被停用)
      const later = new Event('click', { bubbles: true, cancelable: true });
      input.dispatchEvent(later);
      expect(later.defaultPrevented).toBe(true);
      expect(show).toHaveBeenCalledTimes(2);
      choose([]);
      await new Promise((r) => setTimeout(r, 5));
      expect(fallbackClicks).toEqual([]);
    });

    it('把檔案拖進頁面 = 「開啟檔案…」(有 handle 時讀取失敗可以重讀);不是檔案的拖曳不攔', async () => {
      const app = await mount();
      let n = 0;
      const getFile = vi.fn(() => Promise.resolve(n++ === 0 ? staleFile('dropped.jsonl') : new File([E07], 'dropped.jsonl')));
      const file = new File([E07], 'dropped.jsonl');
      const dataTransfer = { types: ['Files'], dropEffect: 'none', files: [file], items: [{ kind: 'file', getAsFile: () => file, getAsFileSystemHandle: () => Promise.resolve({ kind: 'file', name: 'dropped.jsonl', getFile }) }] };
      const fire = (type: string, dt: unknown) => {
        const ev = new Event(type, { bubbles: true, cancelable: true });
        Object.defineProperty(ev, 'dataTransfer', { value: dt });
        app.dispatchEvent(ev);
        return ev;
      };
      expect(fire('dragover', dataTransfer).defaultPrevented).toBe(true);
      expect(fire('dragover', { types: ['text/plain'], files: [], items: [] }).defaultPrevented).toBe(false);
      expect(fire('drop', { types: ['text/plain'], files: [], items: [] }).defaultPrevented).toBe(false);
      expect(fire('drop', dataTransfer).defaultPrevented).toBe(true);
      for (let i = 0; i < 400 && app.recordName !== 'dropped.jsonl'; i++) await new Promise((r) => setTimeout(r, 5));
      expect(getFile).toHaveBeenCalledTimes(2);
      expect(app.recordName).toBe('dropped.jsonl');
      expect(app.messageLevel).toBe('info');
      expect(app.fileErrors).toEqual([]);
    });
  });
});
