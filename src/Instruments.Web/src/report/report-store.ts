// 報告頁的本機儲存(規劃書第 9.1 節成績資料庫的離線雛型):IndexedDB 資料庫 simosa-brm-report、物件存放區 kv
// (紀錄交接、最近紀錄、報告工作資料)與 admin(教官資格紀錄、證書效期雙軌追蹤;版本 2 新增,admin-records.ts)。
// 只在本機瀏覽器;不連網、不經核心。IndexedDB 不可用(私密視窗、測試環境)時退回記憶體,頁面仍可用(重新整理即遺失,
// 畫面會標示「未保存」,請以 JSON 匯出)。
// 教官站「講評報告」按鈕先把目前的練習紀錄寫入 HANDOFF_KEY,再開 report.html?from=instructor 讀取。

export const REPORT_DB_NAME = 'simosa-brm-report';
export const REPORT_DB_VERSION = 2;
export const KV_STORE = 'kv';
/** 教官與證書(admin-records.ts AdminRecords);鍵 ADMIN_KEY */
export const ADMIN_STORE = 'admin';
const STORES = [KV_STORE, ADMIN_STORE] as const;
export type ReportStoreName = (typeof STORES)[number];
export const ADMIN_KEY = 'admin:v1';

/** 教官站交給報告頁的練習紀錄(SessionRecord JSON 文字)。 */
export const HANDOFF_KEY = 'handoff:instructorRecord';
/** 最近載入的紀錄文字(重新整理後還原) */
export const LAST_RECORD_KEY = 'record:last';
/** 報告人工欄位、評估表、班次紀錄 */
export const WORKSPACE_KEY = 'workspace:v1';

/** 鍵值儲存;store 省略 = kv(ADMIN_STORE 給教官與證書紀錄)。 */
export interface KvStore {
  readonly persistent: boolean;
  get<T>(key: string, store?: ReportStoreName): Promise<T | undefined>;
  set(key: string, value: unknown, store?: ReportStoreName): Promise<void>;
  delete(key: string, store?: ReportStoreName): Promise<void>;
}

export class MemoryKvStore implements KvStore {
  readonly persistent = false;
  private readonly map = new Map<string, unknown>();
  async get<T>(key: string, store: ReportStoreName = KV_STORE): Promise<T | undefined> {
    return structuredClone(this.map.get(`${store}:${key}`)) as T | undefined;
  }
  async set(key: string, value: unknown, store: ReportStoreName = KV_STORE): Promise<void> {
    this.map.set(`${store}:${key}`, structuredClone(value));
  }
  async delete(key: string, store: ReportStoreName = KV_STORE): Promise<void> {
    this.map.delete(`${store}:${key}`);
  }
}

class IdbKvStore implements KvStore {
  readonly persistent = true;
  constructor(private readonly db: IDBDatabase) {}

  private tx<T>(store: ReportStoreName, mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    return new Promise((resolve, reject) => {
      const t = this.db.transaction(store, mode);
      const req = fn(t.objectStore(store));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error ?? new Error('IndexedDB 存取失敗'));
    });
  }

  async get<T>(key: string, store: ReportStoreName = KV_STORE): Promise<T | undefined> {
    return (await this.tx(store, 'readonly', (s) => s.get(key))) as T | undefined;
  }
  async set(key: string, value: unknown, store: ReportStoreName = KV_STORE): Promise<void> {
    await this.tx(store, 'readwrite', (s) => s.put(value, key));
  }
  async delete(key: string, store: ReportStoreName = KV_STORE): Promise<void> {
    await this.tx(store, 'readwrite', (s) => s.delete(key));
  }
}

/** 建立缺少的物件存放區(版本 1 → 2 新增 admin;測試用的假 IndexedDB 也走這裡)。 */
export function ensureStores(db: IDBDatabase): void {
  for (const s of STORES) if (!db.objectStoreNames.contains(s)) db.createObjectStore(s);
}

let shared: Promise<KvStore> | null = null;

/** 開啟本機儲存(同一頁共用一個連線);失敗時回傳記憶體儲存。 */
export function openReportStore(): Promise<KvStore> {
  if (shared) return shared;
  shared = new Promise<KvStore>((resolve) => {
    const idb = typeof indexedDB === 'undefined' ? undefined : indexedDB;
    if (!idb) return resolve(new MemoryKvStore());
    try {
      const req = idb.open(REPORT_DB_NAME, REPORT_DB_VERSION);
      req.onupgradeneeded = () => ensureStores(req.result);
      req.onsuccess = () => resolve(new IdbKvStore(req.result));
      req.onerror = () => resolve(new MemoryKvStore());
      req.onblocked = () => resolve(new MemoryKvStore());
    } catch {
      resolve(new MemoryKvStore());
    }
  });
  return shared;
}

/** 測試用:重設共用連線。 */
export function resetReportStoreForTest(): void {
  shared = null;
}

/** 教官站:把練習紀錄交給報告頁(寫入後才開新頁,避免新頁讀到舊資料)。 */
export async function saveHandoff(recordJson: string): Promise<boolean> {
  try {
    const store = await openReportStore();
    await store.set(HANDOFF_KEY, { savedUtc: new Date().toISOString(), text: recordJson });
    return store.persistent;
  } catch {
    return false;
  }
}

export async function takeHandoff(store: KvStore): Promise<{ savedUtc: string; text: string } | undefined> {
  const v = await store.get<{ savedUtc: string; text: string }>(HANDOFF_KEY);
  return v && typeof v.text === 'string' ? v : undefined;
}
