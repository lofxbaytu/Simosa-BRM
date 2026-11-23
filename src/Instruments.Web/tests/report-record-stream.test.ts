// 講評報告頁的紀錄讀取(第三十輪「講評報告站的記憶體尖峰」;happy-dom):
//  - 「開啟檔案」選的檔案、教官站交接的 Blob、重新整理後還原的最近紀錄都分段讀取(lib/record-stream.ts),不呼叫 Blob.text() 把整份紀錄讀成字串;
//  - 2 小時規模的合成教官站紀錄(7201 筆 E02):報告物件與列印版 HTML 與「全文解析 + 完整狀態」(修正前的作法)完全相同;
//  - 最近載入的紀錄在本機儲存存 Blob(不存文字);舊版存的整份文字({ name, text })仍讀得進來並改存成 Blob;舊版的文字交接仍可讀;
//  - 評估表、BRM 標記、名冊、班次、教官與證書等小檔案照舊依 format 匯入;壞檔與讀取失敗顯示錯誤、不影響已載入的紀錄;
//  - 大檔案讀取中顯示進度;超過上限的紀錄不另存;
//  - 另存失敗(IndexedDB 的交易在提交時才中止:核心仍在寫入的紀錄、空間不足):照樣載入,清掉先前記下的那一份並在狀態列說明(第三十輪審查 A-rA-1)。
// @vitest-environment happy-dom
import { render } from 'lit';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
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
    expect(app.fileErrors[0]).toBe('無法載入「unreadable.jsonl」:Error: 讀取失敗');
    expect(app.fileErrors[1]).toBe('無法載入紀錄「bad.txt」:無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)');
    expect(app.fileErrors[2]).toBe('無法載入紀錄「truncated.json」:無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)');
    expect(app.querySelector('.msg.error')!.textContent).toContain('unreadable.jsonl');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.recordName).toBe('good.jsonl');
    expect(app.ws.session.trainees.map((t) => t.id)).toEqual(['T1']);
    expect((await (await openReportStore()).get<{ name?: string }>(LAST_RECORD_KEY))?.name).toBe('good.jsonl');
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
});
