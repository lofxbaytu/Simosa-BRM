// 測試用的最小記憶體版 IndexedDB(happy-dom 沒有 IndexedDB,船上/離線開發環境也不能裝 fake-indexeddb):
// 只實作 report-store.ts 走到的子集 — IDBFactory.open(onupgradeneeded / onsuccess / onerror / onblocked)、deleteDatabase、
// IDBDatabase.objectStoreNames / createObjectStore / transaction / close / onversionchange、
// IDBObjectStore.get / put / delete / getAll,以及版本升級(舊連線未關閉時 versionchange → blocked)。
// 事件回呼一律以 queueMicrotask 非同步觸發:呼叫端在 open() 回傳後才掛上 handler,與真 IndexedDB 相同。
// 交易:每個請求立即套用到資料;交易內的請求都完成後(下一個微任務仍沒有新請求時)提交並觸發 oncomplete;有請求失敗時改為中止(onabort)。
// 提交可以由測試指定失敗(FakeIDBFactory.commitFailure):這時各請求的 onsuccess 已經觸發,交易才中止 —— 寫入全部還原、
// 觸發 onabort(transaction.error 為指定的錯誤)。真的 IndexedDB 在提交時才寫入 Blob 的內容,內容仍在變動的檔案或空間不足
// 就是這樣失敗的(report-store.ts IdbKvStore.tx)。
// 不實作:keyPath / autoIncrement、索引、游標、鍵範圍、abort()、事件冒泡;交易結束後再用同一個存放區物件發請求不擲例外(照樣立即套用)。
// 鍵只支援字串與數字(report-store 的鍵皆為字串)。

type StoreData = Map<string, unknown>;
interface DbRecord {
  version: number;
  stores: Map<string, StoreData>;
}

function keyOf(key: IDBValidKey): string {
  if (typeof key === 'string') return `s:${key}`;
  if (typeof key === 'number') return `n:${key}`;
  throw new DOMException('假 IndexedDB 只支援字串與數字鍵', 'DataError');
}

function later(fn: () => void): void {
  queueMicrotask(fn);
}

function isBlobLike(v: unknown): boolean {
  return typeof v === 'object' && v !== null && typeof (v as { slice?: unknown }).slice === 'function' && typeof (v as { size?: unknown }).size === 'number';
}

/**
 * 存進去的複本:結構化複製;Blob / File(含物件第一層的欄位值)不可變,沿用同一個物件 —— happy-dom 的 Blob 不是原生物件,
 * 經 structuredClone 會失去方法(真的 IndexedDB 存的是 Blob 的內容,讀回來仍是 Blob)。
 */
function cloneForStore<T>(value: T): T {
  if (isBlobLike(value)) return value;
  if (typeof value === 'object' && value !== null && !Array.isArray(value) && Object.values(value).some(isBlobLike)) {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[k] = isBlobLike(v) ? v : structuredClone(v);
    return out as T;
  }
  return structuredClone(value);
}

/** IDBRequest 的最小子集:result / error / readyState 與 onsuccess / onerror。 */
export class FakeIDBRequest<T> {
  result!: T;
  error: DOMException | null = null;
  readyState: 'pending' | 'done' = 'pending';
  onsuccess: ((ev: Event) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;
  /** @internal 請求完成(回呼已觸發)後呼叫(失敗時帶錯誤):所屬的交易據此決定何時提交或中止 */
  _settled: ((error: DOMException | null) => void) | null = null;

  /** @internal 非同步以 value 完成 */
  _succeed(value: T): this {
    later(() => {
      this.result = value;
      this.readyState = 'done';
      try {
        this.onsuccess?.(new Event('success'));
      } finally {
        this._settled?.(null);
      }
    });
    return this;
  }

  /** @internal 非同步以錯誤完成 */
  _fail(error: DOMException): this {
    later(() => {
      this.error = error;
      this.readyState = 'done';
      try {
        this.onerror?.(new Event('error'));
      } finally {
        this._settled?.(error);
      }
    });
    return this;
  }
}

export class FakeIDBOpenRequest extends FakeIDBRequest<FakeIDBDatabase> {
  onupgradeneeded: ((ev: IDBVersionChangeEvent) => void) | null = null;
  onblocked: ((ev: IDBVersionChangeEvent) => void) | null = null;
}

/** DOMStringList 的最小子集(objectStoreNames)。 */
export class FakeDOMStringList implements Iterable<string> {
  constructor(private readonly names: () => string[]) {}
  get length(): number {
    return this.names().length;
  }
  contains(name: string): boolean {
    return this.names().includes(name);
  }
  item(index: number): string | null {
    return this.names()[index] ?? null;
  }
  [Symbol.iterator](): Iterator<string> {
    return this.names()[Symbol.iterator]();
  }
}

/** 交易裡的一筆寫入(commitFailure 據此決定提交是否失敗) */
export interface FakeWrite {
  store: string;
  op: 'put' | 'delete';
  key: IDBValidKey;
  /** put 的值(呼叫端傳入的原物件,未複製) */
  value?: unknown;
}

export class FakeIDBObjectStore {
  constructor(
    readonly name: string,
    private readonly data: StoreData,
    private readonly mode: IDBTransactionMode,
    /** @internal 所屬的交易(升級交易中建立的存放區沒有) */
    private readonly tx: FakeIDBTransaction | null = null,
  ) {}

  /** @internal 交易追蹤這個請求(完成後才可能提交) */
  private track<T>(req: FakeIDBRequest<T>): FakeIDBRequest<T> {
    this.tx?._track(req);
    return req;
  }

  /** @internal 套用一筆寫入,並讓交易記下它與寫入前後的值(交易中止時還原) */
  private write(op: 'put' | 'delete', key: IDBValidKey, apply: (k: string) => void, value?: unknown): void {
    const k = keyOf(key);
    const before = { had: this.data.has(k), old: this.data.get(k) };
    apply(k);
    this.tx?._logWrite(this.data, k, before, { store: this.name, op, key, ...(op === 'put' ? { value } : {}) });
  }

  get(key: IDBValidKey): FakeIDBRequest<unknown> {
    const req = this.track(new FakeIDBRequest<unknown>());
    try {
      return req._succeed(cloneForStore(this.data.get(keyOf(key))));
    } catch (e) {
      return req._fail(e instanceof DOMException ? e : new DOMException(String(e), 'DataError'));
    }
  }

  getAll(): FakeIDBRequest<unknown[]> {
    const req = this.track(new FakeIDBRequest<unknown[]>());
    const values = [...this.data.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([, v]) => cloneForStore(v));
    return req._succeed(values);
  }

  put(value: unknown, key?: IDBValidKey): FakeIDBRequest<IDBValidKey> {
    const req = this.track(new FakeIDBRequest<IDBValidKey>());
    if (this.mode === 'readonly') return req._fail(new DOMException('唯讀交易不能寫入', 'ReadOnlyError'));
    if (key === undefined) return req._fail(new DOMException('存放區無 keyPath,put 需要鍵', 'DataError'));
    try {
      const copy = cloneForStore(value);
      this.write('put', key, (k) => this.data.set(k, copy), value);
    } catch (e) {
      return req._fail(e instanceof DOMException ? e : new DOMException(String(e), 'DataCloneError'));
    }
    return req._succeed(key);
  }

  delete(key: IDBValidKey): FakeIDBRequest<undefined> {
    const req = this.track(new FakeIDBRequest<undefined>());
    if (this.mode === 'readonly') return req._fail(new DOMException('唯讀交易不能刪除', 'ReadOnlyError'));
    this.write('delete', key, (k) => this.data.delete(k));
    return req._succeed(undefined);
  }
}

export class FakeIDBTransaction {
  /** 交易中止的原因(提交失敗時由 FakeIDBFactory.commitFailure 給) */
  error: DOMException | null = null;
  oncomplete: ((ev: Event) => void) | null = null;
  onabort: ((ev: Event) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;
  /** 這個交易的寫入(依先後) */
  readonly writes: FakeWrite[] = [];
  private pending = 0;
  private finished = false;
  /** 第一個失敗的請求的錯誤(交易因此中止) */
  private failure: DOMException | null = null;
  /** 還原用:每筆寫入之前該鍵的原值,與寫入之後的值(別的交易之後又寫了同一個鍵時不還原 —— 真的 IndexedDB 交易依序執行,不會交錯) */
  private readonly undo: Array<{ data: StoreData; key: string; had: boolean; old: unknown; hasNow: boolean; now: unknown }> = [];

  constructor(
    private readonly db: FakeIDBDatabase,
    private readonly scope: readonly string[],
    readonly mode: IDBTransactionMode,
  ) {}

  objectStore(name: string): FakeIDBObjectStore {
    if (!this.scope.includes(name)) throw new DOMException(`存放區 ${name} 不在交易範圍內`, 'NotFoundError');
    return this.db._store(name, this.mode, this);
  }

  /** @internal */
  _track(req: { _settled: ((error: DOMException | null) => void) | null }): void {
    this.pending++;
    req._settled = (error) => {
      this.pending--;
      // 請求失敗(沒有 preventDefault 可用):交易中止
      if (error && !this.failure) this.failure = error;
      // 請求的回呼裡(或緊接其後的微任務)還可以再發請求;都沒有的話就提交
      later(() => this.maybeFinish());
    };
  }

  /** @internal 寫入之後呼叫:記下原值與寫入後的值 */
  _logWrite(data: StoreData, key: string, before: { had: boolean; old: unknown }, write: FakeWrite): void {
    if (this.finished) return;
    this.undo.push({ data, key, ...before, hasNow: data.has(key), now: data.get(key) });
    this.writes.push(write);
  }

  private maybeFinish(): void {
    if (this.finished || this.pending > 0) return;
    this.finished = true;
    const failure = this.failure ?? (this.writes.length > 0 ? this.db._commitFailure(this) : null);
    if (!failure) {
      this.oncomplete?.(new Event('complete'));
      return;
    }
    // 請求失敗或提交失敗:寫入全部還原(由後往前),交易中止
    for (let i = this.undo.length - 1; i >= 0; i--) {
      const u = this.undo[i]!;
      if (u.data.has(u.key) !== u.hasNow || u.data.get(u.key) !== u.now) continue;
      if (u.had) u.data.set(u.key, u.old);
      else u.data.delete(u.key);
    }
    this.error = failure;
    this.onabort?.(new Event('abort'));
  }
}

export class FakeIDBDatabase {
  readonly objectStoreNames: FakeDOMStringList;
  onversionchange: ((ev: IDBVersionChangeEvent) => void) | null = null;
  /** @internal 升級交易進行中(只有此時可 createObjectStore) */
  _upgrading = false;
  private closed = false;

  constructor(
    private readonly factory: FakeIDBFactory,
    readonly name: string,
    readonly version: number,
    private readonly record: DbRecord,
  ) {
    this.objectStoreNames = new FakeDOMStringList(() => [...this.record.stores.keys()]);
  }

  get isClosed(): boolean {
    return this.closed;
  }

  createObjectStore(name: string): FakeIDBObjectStore {
    if (!this._upgrading) throw new DOMException('只能在 versionchange 交易(onupgradeneeded)內建立存放區', 'InvalidStateError');
    if (this.record.stores.has(name)) throw new DOMException(`存放區 ${name} 已存在`, 'ConstraintError');
    const data: StoreData = new Map();
    this.record.stores.set(name, data);
    return new FakeIDBObjectStore(name, data, 'versionchange');
  }

  transaction(storeNames: string | string[], mode: IDBTransactionMode = 'readonly'): FakeIDBTransaction {
    if (this.closed) throw new DOMException('連線已關閉', 'InvalidStateError');
    const scope = typeof storeNames === 'string' ? [storeNames] : [...storeNames];
    for (const s of scope) if (!this.record.stores.has(s)) throw new DOMException(`存放區 ${s} 不存在`, 'NotFoundError');
    return new FakeIDBTransaction(this, scope, mode);
  }

  close(): void {
    this.closed = true;
    this.factory._closed(this);
  }

  /** @internal */
  _store(name: string, mode: IDBTransactionMode, tx: FakeIDBTransaction | null = null): FakeIDBObjectStore {
    const data = this.record.stores.get(name);
    if (!data) throw new DOMException(`存放區 ${name} 不存在`, 'NotFoundError');
    return new FakeIDBObjectStore(name, data, mode, tx);
  }

  /** @internal 交易提交時問 factory 要不要失敗 */
  _commitFailure(tx: FakeIDBTransaction): DOMException | null {
    return this.factory.commitFailure?.(tx.writes) ?? null;
  }
}

/** 記憶體版 IDBFactory;以 vi.stubGlobal('indexedDB', new FakeIDBFactory()) 裝上。 */
export class FakeIDBFactory {
  private readonly records = new Map<string, DbRecord>();
  private readonly connections = new Map<string, Set<FakeIDBDatabase>>();
  /** open() 次數 / 觸發 onupgradeneeded 次數 / 觸發 onblocked 次數(測試斷言用) */
  opens = 0;
  upgrades = 0;
  blocked = 0;
  /**
   * 測試用:交易提交時呼叫(只有有寫入的交易);回傳錯誤 = 提交失敗 —— 各請求的 onsuccess 已觸發,之後交易中止、寫入還原。
   * 例:`(writes) => writes.some((w) => w.key === 'record:last') ? new DOMException('Failed to write blobs (InvalidBlob)', 'DataError') : null`
   */
  commitFailure: ((writes: readonly FakeWrite[]) => DOMException | null) | null = null;

  /** 資料庫目前版本(不存在為 undefined) */
  versionOf(name: string): number | undefined {
    return this.records.get(name)?.version;
  }

  /** 資料庫的存放區名稱(建立順序) */
  storeNames(name: string): string[] {
    return [...(this.records.get(name)?.stores.keys() ?? [])];
  }

  /** 目前仍開著的連線數 */
  openConnections(name: string): number {
    return this.connections.get(name)?.size ?? 0;
  }

  open(name: string, version?: number): FakeIDBOpenRequest {
    this.opens++;
    const req = new FakeIDBOpenRequest();
    if (version !== undefined && (!Number.isInteger(version) || version < 1)) throw new TypeError(`版本須為正整數:${version}`);
    later(() => {
      const existing = this.records.get(name);
      const requested = version ?? existing?.version ?? 1;
      if (existing && requested < existing.version) {
        req._fail(new DOMException(`要求的版本 ${requested} 低於現有版本 ${existing.version}`, 'VersionError'));
        return;
      }
      if (!existing || requested > existing.version) {
        // 版本升級:先通知其他連線 versionchange;仍未關閉者 → blocked(真 IndexedDB 會一直等到它們關閉,這裡請求就此停住)
        const others = [...(this.connections.get(name) ?? [])];
        for (const c of others) c.onversionchange?.(this.versionEvent('versionchange', existing?.version ?? 0, requested));
        if (others.some((c) => !c.isClosed)) {
          this.blocked++;
          req.onblocked?.(this.versionEvent('blocked', existing?.version ?? 0, requested));
          return;
        }
        const record: DbRecord = existing ?? { version: 0, stores: new Map() };
        const oldVersion = record.version;
        const db = new FakeIDBDatabase(this, name, requested, record);
        db._upgrading = true;
        this.upgrades++;
        try {
          req.result = db;
          req.onupgradeneeded?.(this.versionEvent('upgradeneeded', oldVersion, requested));
        } catch (e) {
          db._upgrading = false;
          req._fail(new DOMException(`升級失敗:${String(e)}`, 'AbortError'));
          return;
        }
        db._upgrading = false;
        record.version = requested;
        this.records.set(name, record);
        this.track(name, db);
        req._succeed(db);
        return;
      }
      const db = new FakeIDBDatabase(this, name, existing.version, existing);
      this.track(name, db);
      req._succeed(db);
    });
    return req;
  }

  deleteDatabase(name: string): FakeIDBRequest<undefined> {
    const req = new FakeIDBRequest<undefined>();
    later(() => {
      this.records.delete(name);
      this.connections.delete(name);
      req._succeed(undefined);
    });
    return req;
  }

  /** @internal */
  _closed(db: FakeIDBDatabase): void {
    this.connections.get(db.name)?.delete(db);
  }

  private track(name: string, db: FakeIDBDatabase): void {
    let set = this.connections.get(name);
    if (!set) this.connections.set(name, (set = new Set()));
    set.add(db);
  }

  private versionEvent(type: string, oldVersion: number, newVersion: number): IDBVersionChangeEvent {
    const ev = new Event(type) as Event & { oldVersion: number; newVersion: number | null };
    Object.defineProperty(ev, 'oldVersion', { value: oldVersion });
    Object.defineProperty(ev, 'newVersion', { value: newVersion });
    return ev as unknown as IDBVersionChangeEvent;
  }
}

/** 以假 IndexedDB 直接開啟資料庫(測試前置:例如先建版本 1 只有 kv 的資料庫)。 */
export function openFake(
  factory: FakeIDBFactory,
  name: string,
  version: number,
  upgrade?: (db: FakeIDBDatabase, ev: IDBVersionChangeEvent) => void,
): Promise<FakeIDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = factory.open(name, version);
    req.onupgradeneeded = (ev) => upgrade?.(req.result, ev);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('open 失敗'));
    req.onblocked = () => reject(new Error('blocked'));
  });
}

/** 把 FakeIDBRequest 包成 Promise。 */
export function requestToPromise<T>(req: FakeIDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('request 失敗'));
  });
}
