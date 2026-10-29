// 報告頁本機儲存的 IndexedDB 路徑(report-store.ts;第十六輪審查已知限制 4):以 tests/support/fake-indexeddb.ts 的記憶體假物件
// 跑真正的 openReportStore() —— 版本 1(只有 kv)升級到版本 2(新增 admin)時 kv 資料保留、admin 可 set/get 且與 kv 同名鍵互不干擾;
// 全新資料庫一次建兩個存放區;再次開啟(版本已是 2)不再升級;onblocked(舊連線未關閉)、onerror(現有版本更高)、open 擲例外、
// 無 indexedDB 時退回記憶體儲存(persistent=false,畫面據此標示「未保存」)。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeIDBDatabase, FakeIDBFactory, openFake, requestToPromise } from './support/fake-indexeddb.js';
import {
  ADMIN_KEY,
  ADMIN_STORE,
  HANDOFF_KEY,
  KV_STORE,
  LAST_RECORD_KEY,
  MemoryKvStore,
  REPORT_DB_NAME,
  REPORT_DB_VERSION,
  WORKSPACE_KEY,
  ensureStores,
  openReportStore,
  resetReportStoreForTest,
  saveHandoff,
  takeHandoff,
} from '../src/report/report-store.js';

const WORKSPACE_V1 = { version: 1, report: { title: '舊工作資料' }, markers: [{ t: 12, label: 'closed loop' }] };
const ADMIN_V1 = { version: 1, instructors: [{ name: '陳船長', expiryDate: '2026-12-15' }], trainees: [] };

let factory: FakeIDBFactory;

/** 先以版本 1 建立只有 kv 的資料庫並寫入工作資料(第十六輪之前的報告頁所留下的狀態)。 */
async function seedVersion1(): Promise<void> {
  const db = await openFake(factory, REPORT_DB_NAME, 1, (d) => d.createObjectStore(KV_STORE));
  await requestToPromise(db.transaction(KV_STORE, 'readwrite').objectStore(KV_STORE).put(WORKSPACE_V1, WORKSPACE_KEY));
  await requestToPromise(db.transaction(KV_STORE, 'readwrite').objectStore(KV_STORE).put({ text: '{"format":"x"}', savedUtc: 't' }, LAST_RECORD_KEY));
  db.close();
  expect(factory.versionOf(REPORT_DB_NAME)).toBe(1);
  expect(factory.storeNames(REPORT_DB_NAME)).toEqual([KV_STORE]);
  expect(factory.openConnections(REPORT_DB_NAME)).toBe(0);
}

beforeEach(() => {
  factory = new FakeIDBFactory();
  vi.stubGlobal('indexedDB', factory);
  resetReportStoreForTest();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetReportStoreForTest();
});

describe('report-store IndexedDB 版本 1 → 2 升級', () => {
  it('版本 1 的 kv 資料保留;新增 admin 存放區可 set/get,與 kv 同名鍵互不干擾', async () => {
    await seedVersion1();
    const upgradesBefore = factory.upgrades;

    const store = await openReportStore();
    expect(store.persistent).toBe(true);
    expect(store).not.toBeInstanceOf(MemoryKvStore);
    expect(factory.versionOf(REPORT_DB_NAME)).toBe(REPORT_DB_VERSION);
    expect(factory.upgrades).toBe(upgradesBefore + 1);
    expect(factory.storeNames(REPORT_DB_NAME)).toEqual([KV_STORE, ADMIN_STORE]);

    // 舊資料仍在(升級不清 kv)
    expect(await store.get(WORKSPACE_KEY)).toEqual(WORKSPACE_V1);
    expect(await store.get(LAST_RECORD_KEY)).toEqual({ text: '{"format":"x"}', savedUtc: 't' });
    expect(await store.get(ADMIN_KEY, ADMIN_STORE)).toBeUndefined();

    // admin 存放區
    await store.set(ADMIN_KEY, ADMIN_V1, ADMIN_STORE);
    expect(await store.get(ADMIN_KEY, ADMIN_STORE)).toEqual(ADMIN_V1);
    expect(await store.get(ADMIN_KEY)).toBeUndefined(); // kv 沒有同名鍵
    await store.set(ADMIN_KEY, { inKv: true });
    expect(await store.get(ADMIN_KEY)).toEqual({ inKv: true });
    expect(await store.get(ADMIN_KEY, ADMIN_STORE)).toEqual(ADMIN_V1);
    await store.delete(ADMIN_KEY, ADMIN_STORE);
    expect(await store.get(ADMIN_KEY, ADMIN_STORE)).toBeUndefined();
    expect(await store.get(ADMIN_KEY)).toEqual({ inKv: true });

    // 取回的是副本:改了不影響儲存的值
    const got = (await store.get<typeof WORKSPACE_V1>(WORKSPACE_KEY))!;
    got.report.title = '改掉';
    expect((await store.get<typeof WORKSPACE_V1>(WORKSPACE_KEY))!.report.title).toBe('舊工作資料');
  });

  it('同一頁共用一個連線;重設後再開(版本已是 2)不再升級、資料仍在', async () => {
    await seedVersion1();
    const a = await openReportStore();
    expect(await openReportStore()).toBe(a);
    const opens = factory.opens;
    await a.set(ADMIN_KEY, ADMIN_V1, ADMIN_STORE);

    resetReportStoreForTest();
    const b = await openReportStore();
    expect(b).not.toBe(a);
    expect(factory.opens).toBe(opens + 1);
    expect(factory.upgrades).toBe(2); // 版本 1 建立 + 1→2;第三次開啟不升級
    expect(factory.versionOf(REPORT_DB_NAME)).toBe(REPORT_DB_VERSION);
    expect(await b.get(WORKSPACE_KEY)).toEqual(WORKSPACE_V1);
    expect(await b.get(ADMIN_KEY, ADMIN_STORE)).toEqual(ADMIN_V1);
  });

  it('全新資料庫:一次建立 kv 與 admin;教官站交接(saveHandoff/takeHandoff)走 IndexedDB 並回報已保存', async () => {
    expect(factory.versionOf(REPORT_DB_NAME)).toBeUndefined();
    expect(await saveHandoff('{"format":"simosa-brm-instructor-record"}')).toBe(true);
    expect(factory.versionOf(REPORT_DB_NAME)).toBe(REPORT_DB_VERSION);
    expect(factory.storeNames(REPORT_DB_NAME)).toEqual([KV_STORE, ADMIN_STORE]);
    const store = await openReportStore();
    const handoff = await takeHandoff(store);
    expect(handoff?.text).toBe('{"format":"simosa-brm-instructor-record"}');
    expect(typeof handoff?.savedUtc).toBe('string');
    await store.delete(HANDOFF_KEY);
    expect(await takeHandoff(store)).toBeUndefined();
  });

  it('ensureStores 只建缺少的存放區(重複呼叫不擲例外)', async () => {
    const db = await openFake(factory, 'ensure-test', 1, (d) => {
      ensureStores(d as unknown as IDBDatabase);
      ensureStores(d as unknown as IDBDatabase);
    });
    expect([...db.objectStoreNames]).toEqual([KV_STORE, ADMIN_STORE]);
    expect(db.objectStoreNames.contains(ADMIN_STORE)).toBe(true);
    expect(db.objectStoreNames.length).toBe(2);
    db.close();
  });
});

describe('report-store 退回記憶體儲存', () => {
  it('onblocked(另一分頁仍開著版本 1 的連線):退回記憶體儲存並標示未保存;資料庫未升級', async () => {
    await seedVersion1();
    // 模擬另一個分頁:連線開著,收到 versionchange 也不關閉
    let versionChangeSeen: { oldVersion: number; newVersion: number | null } | null = null;
    const other: FakeIDBDatabase = await openFake(factory, REPORT_DB_NAME, 1);
    other.onversionchange = (ev) => {
      versionChangeSeen = { oldVersion: ev.oldVersion, newVersion: ev.newVersion };
    };

    const store = await openReportStore();
    expect(store).toBeInstanceOf(MemoryKvStore);
    expect(store.persistent).toBe(false);
    expect(factory.blocked).toBe(1);
    expect(versionChangeSeen).toEqual({ oldVersion: 1, newVersion: 2 });
    expect(factory.versionOf(REPORT_DB_NAME)).toBe(1); // 未升級
    expect(factory.storeNames(REPORT_DB_NAME)).toEqual([KV_STORE]);
    // 記憶體儲存仍可用(重新整理即遺失,由畫面標示)
    await store.set(ADMIN_KEY, ADMIN_V1, ADMIN_STORE);
    expect(await store.get(ADMIN_KEY, ADMIN_STORE)).toEqual(ADMIN_V1);
    expect(await store.get(WORKSPACE_KEY)).toBeUndefined();
    expect(await saveHandoff('{}')).toBe(false);
    other.close();

    // 舊連線關閉後重設再開:升級成功、舊資料仍在
    resetReportStoreForTest();
    const again = await openReportStore();
    expect(again.persistent).toBe(true);
    expect(factory.versionOf(REPORT_DB_NAME)).toBe(REPORT_DB_VERSION);
    expect(await again.get(WORKSPACE_KEY)).toEqual(WORKSPACE_V1);
  });

  it('onerror(瀏覽器內的資料庫版本高於程式版本):退回記憶體儲存', async () => {
    const db = await openFake(factory, REPORT_DB_NAME, REPORT_DB_VERSION + 1, (d) => ensureStores(d as unknown as IDBDatabase));
    db.close();
    const store = await openReportStore();
    expect(store).toBeInstanceOf(MemoryKvStore);
    expect(store.persistent).toBe(false);
    expect(factory.versionOf(REPORT_DB_NAME)).toBe(REPORT_DB_VERSION + 1);
  });

  it('open() 擲例外(例如私密視窗拒絕存取)或沒有 indexedDB:退回記憶體儲存', async () => {
    vi.stubGlobal('indexedDB', {
      open: () => {
        throw new DOMException('私密視窗', 'SecurityError');
      },
    });
    resetReportStoreForTest();
    expect(await openReportStore()).toBeInstanceOf(MemoryKvStore);

    vi.stubGlobal('indexedDB', undefined);
    resetReportStoreForTest();
    const store = await openReportStore();
    expect(store).toBeInstanceOf(MemoryKvStore);
    expect(store.persistent).toBe(false);
  });
});

describe('假 IndexedDB 本身', () => {
  it('getAll 依鍵排序回傳副本;唯讀交易不能寫;交易範圍外 / 不存在的存放區與升級外的 createObjectStore 擲例外', async () => {
    const db = await openFake(factory, 'self-test', 1, (d) => {
      d.createObjectStore('a');
      d.createObjectStore('b');
    });
    const w = db.transaction(['a'], 'readwrite').objectStore('a');
    await requestToPromise(w.put({ v: 2 }, 'k2'));
    await requestToPromise(w.put({ v: 1 }, 'k1'));
    await requestToPromise(w.put({ v: 3 }, 'k3'));
    await requestToPromise(w.delete('k3'));
    expect(await requestToPromise(db.transaction('a').objectStore('a').getAll())).toEqual([{ v: 1 }, { v: 2 }]);
    expect(await requestToPromise(db.transaction('a').objectStore('a').get('nope'))).toBeUndefined();
    await expect(requestToPromise(db.transaction('a', 'readonly').objectStore('a').put({ v: 9 }, 'k9'))).rejects.toMatchObject({ name: 'ReadOnlyError' });
    await expect(requestToPromise(db.transaction('a', 'readwrite').objectStore('a').put({ v: 9 }))).rejects.toMatchObject({ name: 'DataError' });
    expect(() => db.transaction('a').objectStore('b')).toThrow(/交易範圍/);
    expect(() => db.transaction('zzz')).toThrow(/不存在/);
    expect(() => db.createObjectStore('c')).toThrow(/onupgradeneeded/);
    db.close();
    expect(() => db.transaction('a')).toThrow(/已關閉/);
    expect(() => factory.open('x', 0)).toThrow(TypeError);
    // 事件在 open() 回傳後才觸發(handler 來得及掛上)
    const req = factory.open('late', 1);
    let fired = 0;
    req.onupgradeneeded = () => fired++;
    req.onsuccess = () => fired++;
    await new Promise((r) => setTimeout(r, 0));
    expect(fired).toBe(2);
    expect(req.readyState).toBe('done');
    // deleteDatabase
    await requestToPromise(factory.deleteDatabase('late'));
    expect(factory.versionOf('late')).toBeUndefined();
  });
});
