// 報告頁的本機儲存(規劃書第 9.1 節成績資料庫的離線雛型):IndexedDB 資料庫 simosa-brm-report、物件存放區 kv
// (紀錄交接、最近紀錄、報告工作資料)與 admin(教官資格紀錄、證書效期雙軌追蹤;版本 2 新增,admin-records.ts)。
// 只在本機瀏覽器;不連網、不經核心。IndexedDB 不可用(私密視窗、測試環境)時退回記憶體,頁面仍可用(重新整理即遺失,
// 畫面會標示「未保存」,請以 JSON 匯出)。
// 教官站「講評報告」按鈕先把目前的練習紀錄(Blob)寫入 HANDOFF_KEY,再開 report.html?from=instructor 讀取。
// 練習紀錄(交接與「最近載入的紀錄」)一律存 Blob:IndexedDB 把 Blob 的內容放在瀏覽器的檔案儲存,頁面不必持有整份文字,
// 讀回時由報告頁分段解析(lib/record-stream.ts)。舊版存的文字({ text })仍可讀,讀到後改存成 Blob。

export const REPORT_DB_NAME = 'simosa-brm-report';
export const REPORT_DB_VERSION = 2;
export const KV_STORE = 'kv';
/** 教官與證書(admin-records.ts AdminRecords);鍵 ADMIN_KEY */
export const ADMIN_STORE = 'admin';
const STORES = [KV_STORE, ADMIN_STORE] as const;
export type ReportStoreName = (typeof STORES)[number];
export const ADMIN_KEY = 'admin:v1';

/** 教官站交給報告頁的練習紀錄:{ savedUtc, blob }(SessionRecord JSON 的 Blob;第三十輪 W2 起)或舊版的 { savedUtc, text }。 */
export const HANDOFF_KEY = 'handoff:instructorRecord';
/** 最近載入的紀錄(重新整理後還原):{ name, blob };舊版為 { name, text }(整份文字) */
export const LAST_RECORD_KEY = 'record:last';
/**
 * 「最近載入的紀錄」另存一份的大小上限(位元組):超過的紀錄(約 1 小時以上的核心 JSON Lines 紀錄)不複製進瀏覽器儲存,
 * 重新整理後需重新開啟檔案。修正前超過約 512 MB(V8 字串長度上限)的紀錄本來就無法載入,因此沒有原本能還原、現在不能的情況。
 */
export const LAST_RECORD_MAX_BYTES = 512 * 1024 * 1024;
/** 報告人工欄位、評估表、班次紀錄 */
export const WORKSPACE_KEY = 'workspace:v1';

/** 鍵值儲存;store 省略 = kv(ADMIN_STORE 給教官與證書紀錄)。 */
export interface KvStore {
  readonly persistent: boolean;
  get<T>(key: string, store?: ReportStoreName): Promise<T | undefined>;
  set(key: string, value: unknown, store?: ReportStoreName): Promise<void>;
  delete(key: string, store?: ReportStoreName): Promise<void>;
}

/** Blob / File(或測試用的替身):有 size 與 slice()。 */
export function isBlobLike(v: unknown): v is Blob {
  return typeof v === 'object' && v !== null && typeof (v as { slice?: unknown }).slice === 'function' && typeof (v as { size?: unknown }).size === 'number';
}

/** 記憶體儲存用的複本:Blob 不可變,直接沿用同一個物件(非原生的 Blob 實作經 structuredClone 會失去方法);其餘深複製。 */
function cloneKeepingBlobs<T>(value: T): T {
  if (isBlobLike(value)) return value;
  if (typeof value === 'object' && value !== null && !Array.isArray(value) && Object.values(value).some(isBlobLike)) {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[k] = isBlobLike(v) ? v : structuredClone(v);
    return out as T;
  }
  return structuredClone(value);
}

export class MemoryKvStore implements KvStore {
  readonly persistent = false;
  private readonly map = new Map<string, unknown>();
  async get<T>(key: string, store: ReportStoreName = KV_STORE): Promise<T | undefined> {
    return cloneKeepingBlobs(this.map.get(`${store}:${key}`)) as T | undefined;
  }
  async set(key: string, value: unknown, store: ReportStoreName = KV_STORE): Promise<void> {
    this.map.set(`${store}:${key}`, cloneKeepingBlobs(value));
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

/**
 * 教官站:把練習紀錄交給報告頁(寫入後才開新頁,避免新頁讀到舊資料)。教官站傳 Blob(session-record.ts sessionRecordBlob):
 * IndexedDB 直接存 Blob,不必把整份紀錄組成字串再結構化複製(2 小時的紀錄可達數十 MB);也接受文字(測試、舊呼叫者)。
 */
export async function saveHandoff(record: Blob | string): Promise<boolean> {
  try {
    const store = await openReportStore();
    const savedUtc = new Date().toISOString();
    await store.set(HANDOFF_KEY, typeof record === 'string' ? { savedUtc, text: record } : { savedUtc, blob: record });
    return store.persistent;
  } catch {
    return false;
  }
}

/** 一份存放的練習紀錄:Blob(現行)或整份文字(舊版資料) */
export type StoredRecord = { blob: Blob } | { text: string };

function storedRecordOf(v: { blob?: unknown; text?: unknown }): StoredRecord | undefined {
  if (isBlobLike(v.blob)) return { blob: v.blob };
  return typeof v.text === 'string' ? { text: v.text } : undefined;
}

/** 報告頁:讀出交接的紀錄(Blob 不在這裡讀成文字,由報告頁分段解析;舊版交接為文字)。 */
export async function takeHandoff(store: KvStore): Promise<({ savedUtc: string } & StoredRecord) | undefined> {
  const v = await store.get<{ savedUtc?: unknown; text?: unknown; blob?: unknown }>(HANDOFF_KEY);
  if (!v || typeof v !== 'object') return undefined;
  const rec = storedRecordOf(v);
  return rec ? { savedUtc: typeof v.savedUtc === 'string' ? v.savedUtc : '', ...rec } : undefined;
}

/** 報告頁:最近載入的紀錄({ name, blob };舊版 { name, text })。 */
export async function readLastRecord(store: KvStore): Promise<({ name: string } & StoredRecord) | undefined> {
  const v = await store.get<{ name?: unknown; text?: unknown; blob?: unknown }>(LAST_RECORD_KEY);
  if (!v || typeof v !== 'object') return undefined;
  const rec = storedRecordOf(v);
  if (!rec || ('text' in rec && !rec.text)) return undefined;
  return { name: typeof v.name === 'string' ? v.name : '', ...rec };
}

/** 報告頁:記下最近載入的紀錄(存 Blob,不存文字)。 */
export async function saveLastRecord(store: KvStore, name: string, blob: Blob): Promise<void> {
  await store.set(LAST_RECORD_KEY, { name, blob });
}
