// 測試用的最小記憶體版 IndexedDB(happy-dom 沒有 IndexedDB,船上/離線開發環境也不能裝 fake-indexeddb):
// 只實作 report-store.ts 走到的子集 — IDBFactory.open(onupgradeneeded / onsuccess / onerror / onblocked)、deleteDatabase、
// IDBDatabase.objectStoreNames / createObjectStore / transaction / close / onversionchange、
// IDBObjectStore.get / put / delete / getAll,以及版本升級(舊連線未關閉時 versionchange → blocked)。
// 事件回呼一律以 queueMicrotask 非同步觸發:呼叫端在 open() 回傳後才掛上 handler,與真 IndexedDB 相同。
// 不實作:keyPath / autoIncrement、索引、游標、鍵範圍、交易中止與回滾、事件冒泡;交易內各請求獨立且立即完成。
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

/** IDBRequest 的最小子集:result / error / readyState 與 onsuccess / onerror。 */
export class FakeIDBRequest<T> {
  result!: T;
  error: DOMException | null = null;
  readyState: 'pending' | 'done' = 'pending';
  onsuccess: ((ev: Event) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;

  /** @internal 非同步以 value 完成 */
  _succeed(value: T): this {
    later(() => {
      this.result = value;
      this.readyState = 'done';
      this.onsuccess?.(new Event('success'));
    });
    return this;
  }

  /** @internal 非同步以錯誤完成 */
  _fail(error: DOMException): this {
    later(() => {
      this.error = error;
      this.readyState = 'done';
      this.onerror?.(new Event('error'));
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

export class FakeIDBObjectStore {
  constructor(
    readonly name: string,
    private readonly data: StoreData,
    private readonly mode: IDBTransactionMode,
  ) {}

  get(key: IDBValidKey): FakeIDBRequest<unknown> {
    const req = new FakeIDBRequest<unknown>();
    try {
      return req._succeed(structuredClone(this.data.get(keyOf(key))));
    } catch (e) {
      return req._fail(e instanceof DOMException ? e : new DOMException(String(e), 'DataError'));
    }
  }

  getAll(): FakeIDBRequest<unknown[]> {
    const req = new FakeIDBRequest<unknown[]>();
    const values = [...this.data.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([, v]) => structuredClone(v));
    return req._succeed(values);
  }

  put(value: unknown, key?: IDBValidKey): FakeIDBRequest<IDBValidKey> {
    const req = new FakeIDBRequest<IDBValidKey>();
    if (this.mode === 'readonly') return req._fail(new DOMException('唯讀交易不能寫入', 'ReadOnlyError'));
    if (key === undefined) return req._fail(new DOMException('存放區無 keyPath,put 需要鍵', 'DataError'));
    try {
      this.data.set(keyOf(key), structuredClone(value));
    } catch (e) {
      return req._fail(e instanceof DOMException ? e : new DOMException(String(e), 'DataCloneError'));
    }
    return req._succeed(key);
  }

  delete(key: IDBValidKey): FakeIDBRequest<undefined> {
    const req = new FakeIDBRequest<undefined>();
    if (this.mode === 'readonly') return req._fail(new DOMException('唯讀交易不能刪除', 'ReadOnlyError'));
    this.data.delete(keyOf(key));
    return req._succeed(undefined);
  }
}

export class FakeIDBTransaction {
  constructor(
    private readonly db: FakeIDBDatabase,
    private readonly scope: readonly string[],
    readonly mode: IDBTransactionMode,
  ) {}

  objectStore(name: string): FakeIDBObjectStore {
    if (!this.scope.includes(name)) throw new DOMException(`存放區 ${name} 不在交易範圍內`, 'NotFoundError');
    return this.db._store(name, this.mode);
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
  _store(name: string, mode: IDBTransactionMode): FakeIDBObjectStore {
    const data = this.record.stores.get(name);
    if (!data) throw new DOMException(`存放區 ${name} 不存在`, 'NotFoundError');
    return new FakeIDBObjectStore(name, data, mode);
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
