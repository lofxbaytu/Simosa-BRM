// 報告頁的本機儲存(規劃書第 9.1 節成績資料庫的離線雛型):IndexedDB 資料庫 simosa-brm-report、物件存放區 kv。
// 只在本機瀏覽器;不連網、不經核心。IndexedDB 不可用(私密視窗、測試環境)時退回記憶體,頁面仍可用(重新整理即遺失,
// 畫面會標示「未保存」,請以 JSON 匯出)。
// 教官站「講評報告」按鈕先把目前的練習紀錄寫入 HANDOFF_KEY,再開 report.html?from=instructor 讀取。

export const REPORT_DB_NAME = 'simosa-brm-report';
const STORE = 'kv';

/** 教官站交給報告頁的練習紀錄(SessionRecord JSON 文字)。 */
export const HANDOFF_KEY = 'handoff:instructorRecord';
/** 最近載入的紀錄文字(重新整理後還原) */
export const LAST_RECORD_KEY = 'record:last';
/** 報告人工欄位、評估表、班次紀錄 */
export const WORKSPACE_KEY = 'workspace:v1';

export interface KvStore {
  readonly persistent: boolean;
  get<T>(key: string): Promise<T | undefined>;
  set(key: string, value: unknown): Promise<void>;
  delete(key: string): Promise<void>;
}

export class MemoryKvStore implements KvStore {
  readonly persistent = false;
  private readonly map = new Map<string, unknown>();
  async get<T>(key: string): Promise<T | undefined> {
    return structuredClone(this.map.get(key)) as T | undefined;
  }
  async set(key: string, value: unknown): Promise<void> {
    this.map.set(key, structuredClone(value));
  }
  async delete(key: string): Promise<void> {
    this.map.delete(key);
  }
}

class IdbKvStore implements KvStore {
  readonly persistent = true;
  constructor(private readonly db: IDBDatabase) {}

  private tx<T>(mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    return new Promise((resolve, reject) => {
      const t = this.db.transaction(STORE, mode);
      const req = fn(t.objectStore(STORE));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error ?? new Error('IndexedDB 存取失敗'));
    });
  }

  async get<T>(key: string): Promise<T | undefined> {
    return (await this.tx('readonly', (s) => s.get(key))) as T | undefined;
  }
  async set(key: string, value: unknown): Promise<void> {
    await this.tx('readwrite', (s) => s.put(value, key));
  }
  async delete(key: string): Promise<void> {
    await this.tx('readwrite', (s) => s.delete(key));
  }
}

let shared: Promise<KvStore> | null = null;

/** 開啟本機儲存(同一頁共用一個連線);失敗時回傳記憶體儲存。 */
export function openReportStore(): Promise<KvStore> {
  if (shared) return shared;
  shared = new Promise<KvStore>((resolve) => {
    const idb = typeof indexedDB === 'undefined' ? undefined : indexedDB;
    if (!idb) return resolve(new MemoryKvStore());
    try {
      const req = idb.open(REPORT_DB_NAME, 1);
      req.onupgradeneeded = () => {
        if (!req.result.objectStoreNames.contains(STORE)) req.result.createObjectStore(STORE);
      };
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
