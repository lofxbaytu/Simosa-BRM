// 練習紀錄的來源與「重新取得檔案再讀」(第三十一輪「核心仍在寫入的紀錄讀取偶爾失敗」;規劃書第 9.2 節;講評報告頁與重播檢視共用)。
//
// 實測(真實 Chromium 153;數字與量法見 README「紀錄讀取」):
//  - 瀏覽器在**選取檔案的當下**記下檔案的修改時間,之後每次開啟(Blob.stream() 的第一次 read()、Blob.text() …)都核對;不同就拒絕。
//    Blob.stream() 是 `TypeError: network error`、一個位元組都不交出;FileReader 那一系是 NotReadableError。
//  - 核心仍在寫入的紀錄(build/records 下正在變大的 .jsonl)因此**偶爾**讀不到:選取到開啟之間約 1 毫秒,剛好有一次寫入落在裡面。
//    失敗率 ≈ 這段時間 ÷ 寫入的間隔:每 20 ms 附加一行約 2–8%,核心實際的節奏(64 KB 的緩衝滿了才寫,約每 0.56 秒一次)400 次裡 1–2 次。
//  - **同一個 File 物件再讀幾次都一樣失敗**(立刻、隔 0.1 / 0.5 / 1 / 1.2 秒:500 多次裡 0 次成功)—— 它記著的是選取當下的修改時間,不會更新;
//    slice()、new Blob([file])、structuredClone、Response、物件網址、FileReader、Worker 也都一樣。一般的檔案輸入(<input type="file">)
//    沒有辦法不經使用者再選一次就拿到新的 File。
//  - 拿得到新 File 的來源:File System Access API 的 FileSystemFileHandle(showOpenFilePicker、拖放的 getAsFileSystemHandle;
//    每次 getFile() 都重新記下大小與修改時間)與拖放的 FileSystemFileEntry(webkitGetAsEntry;每次 file() 亦同)。
//    重新取得之後立刻讀,每一次的失敗率與第一次同一個量級(空白頁上每 20 ms 附加時 6–10%),失敗後再取一次多半就讀到。
//  - **取得檔案到開啟之間**(瀏覽器在 getFile() 的當下記下修改時間,開啟時核對)主執行緒忙多久,檔案被改動的機會就有多大。
//    頁面閒著時這段時間(getFile() 由呼叫到拿到檔案)約 0.4 毫秒(中位數;九成在 0.7–2 毫秒以內);畫面每更新一次(改一次狀態列 /
//    工具列的字),重播檢視接下來那個畫格主執行緒要忙 5–25 毫秒。
//    審查時量到(進行中的紀錄拖進頁面,每 20 ms 附加一行,各 300 次):先寫「讀取中…」再取檔案的重播檢視有 50–62 次要重讀
//    (講評報告頁 9–11 次),每次重讀前又改一次字,重讀也有四成再失敗 —— 每 5 ms 改一次修改時間時 600 次裡有 2 次用完 6 次讀取。
//    改成「取得檔案之前與各次重讀之間都不更新畫面」之後(同樣的量法):每 20 ms 附加,講評報告頁 9、完整教官站 12、?mode=replay 16 次要重讀,
//    都是重讀 1 次就讀到;每 5 ms 改修改時間(比核心實際的寫入密 112 倍),?mode=replay 2,000 次全部載入:379 次要重讀,
//    其中 372 次重讀 1 次、5 次 2 次、2 次 3 次。真實的核心正在寫的紀錄:講評報告頁 400 次裡 1 次要重讀,?mode=replay 400 次、
//    完整教官站 400 次(其中 200 次連著核心的即時畫面)都是 0 次。
//
// 所以:
//  - 「開啟檔案」在瀏覽器支援時改用 showOpenFilePicker(RecordFilePicker),拿到的是 handle;檔案也可以直接拖進頁面(recordSourcesFromDataTransfer)。
//    這兩種來源(RecordSource.reopenable)讀取失敗時自動重新取得檔案、從頭重讀(readRecordSource),教官選一次就載入。
//  - 不支援的環境(不是安全來源的頁面、非 Chromium 的瀏覽器、對話框開不起來)退回一般的檔案輸入:那種 File 重讀沒有用,所以不重讀,
//    直接顯示中文說明(請重新選取一次,或待練習結束後再開啟;record-stream.ts 的 recordReadErrorText)。
//  - 只有**讀取端的失敗**(record-stream.ts 的 isRecordReadFailure)才重讀;解析器或 onProgress 擲出的例外(內容或程式的事、使用者又選了別的檔案)
//    原樣往外丟。重新取得檔案本身失敗(檔案被刪除、改名、磁碟被移除:NotFoundError)就結束,不再重試。
//  - 每次重讀都是呼叫端的 read(新的 Blob) 從頭來過(新的解析器與狀態存放);上一次的結果沒有人留著,可以回收。
//  - 呼叫端(兩頁)在取得檔案之前、與各次重讀之間**不更新畫面**:讀取說明(讀取中、百分比、「重新讀取 …(第 N 次重新讀取)」)都等讀到
//    第一段(檔案已經開啟)才寫。開啟就失敗的那幾次什麼都還沒有顯示,不必說明;讀到一半才失敗時,原本的百分比留到重讀的第一段讀到為止。
//
// Windows 的 Edge / WebView2 沒有在開發環境實測(容器裡只有 Linux 的 Chromium)。

import { markRecordReadFailure, isRecordReadFailure } from './record-stream.js';

/** 一份練習紀錄的來源。 */
export interface RecordSource {
  /** 顯示用的檔名 */
  readonly name: string;
  /**
   * 取得檔案。reopenable 的來源每次呼叫都向瀏覽器重新取得(新的大小與修改時間;檔案已經不在時以 NotFoundError 拒絕,
   * 拒絕的錯誤已記成讀取端的失敗);其餘的每次都是選取當下的那一份。
   */
  open(): Promise<Blob>;
  /** 讀取失敗時可以重新取得檔案再讀(檔案 handle、拖放的檔案項目);false = 一般的檔案輸入、已經在記憶體裡的 Blob,重讀沒有用 */
  readonly reopenable: boolean;
}

/** 一般的檔案(檔案輸入選的 File、記憶體裡的 Blob):不能重新取得。 */
export function blobRecordSource(blob: Blob, name: string): RecordSource {
  return { name, open: () => Promise.resolve(blob), reopenable: false };
}

/** File System Access API 的檔案 handle(只用到這兩個成員)。 */
export interface RecordFileHandle {
  readonly name: string;
  getFile(): Promise<File>;
}

/**
 * 由檔案 handle 建立來源:每次 open() 都 getFile()(到要讀的時候才取,選取到開啟之間的時間最短;
 * 一次選多個檔案時,排在後面的也是輪到它才取,不會因為等前面的檔案讀完而過期)。
 */
export function handleRecordSource(handle: RecordFileHandle): RecordSource {
  const open = async (): Promise<Blob> => {
    try {
      return await handle.getFile();
    } catch (err) {
      throw markRecordReadFailure(err);
    }
  };
  return { name: handle.name, open, reopenable: true };
}

/** 拖放的檔案項目(FileSystemFileEntry;只用到這幾個成員)。 */
interface RecordFileEntry {
  readonly isFile: boolean;
  readonly name: string;
  file(ok: (file: File) => void, fail?: (err: unknown) => void): void;
}

function entryRecordSource(entry: RecordFileEntry): RecordSource {
  const open = (): Promise<Blob> => new Promise<Blob>((resolve, reject) => entry.file(resolve, (err) => reject(markRecordReadFailure(err))));
  return { name: entry.name, open, reopenable: true };
}

/** 拖放項目(DataTransferItem;getAsFileSystemHandle 只在安全來源的 Chromium 有) */
type DroppedItem = DataTransferItem & { getAsFileSystemHandle?: () => Promise<(RecordFileHandle & { kind?: string }) | null> };

// 拖放的內容有沒有檔案(dragover 時決定要不要接受):與整頁的防護(lib/file-drop-guard.ts;檔案拖到不接受的地方時不讓瀏覽器把頁面換掉)同一個判斷
export { dataTransferHasFiles } from './file-drop-guard.js';

/**
 * 拖放進來的檔案 → 來源(依拖放的順序;資料夾略過)。能重新取得的優先:檔案 handle(getAsFileSystemHandle)、其次檔案項目(webkitGetAsEntry),
 * 都沒有時就是拖放當下的那個 File(與檔案輸入的一樣不能重讀)。
 * **必須在 drop 事件的處理函式裡同步呼叫**(事件結束後 DataTransfer 的內容就不能取用;這裡先同步把各項目的 handle / 項目 / File 取出來,再等待)。
 */
export function recordSourcesFromDataTransfer(dt: DataTransfer): Promise<RecordSource[]> {
  const items = Array.from(dt.items ?? []) as DroppedItem[];
  if (items.length === 0) return Promise.resolve(Array.from(dt.files ?? []).map((f) => blobRecordSource(f, f.name)));
  const pending: Array<Promise<RecordSource | null>> = [];
  for (const item of items) {
    if (item.kind !== 'file') continue;
    const file = item.getAsFile();
    const entry = typeof item.webkitGetAsEntry === 'function' ? (item.webkitGetAsEntry() as unknown as RecordFileEntry | null) : null;
    if (entry && !entry.isFile) continue; // 資料夾
    const handle = typeof item.getAsFileSystemHandle === 'function' ? item.getAsFileSystemHandle().catch(() => null) : Promise.resolve(null);
    pending.push(
      handle.then((h) => {
        if (h && h.kind !== 'directory' && typeof h.getFile === 'function') return handleRecordSource(h);
        if (entry) return entryRecordSource(entry);
        return file ? blobRecordSource(file, file.name) : null;
      }),
    );
  }
  return Promise.all(pending).then((list) => list.filter((x): x is RecordSource => x !== null));
}

// ------------------------------------------------------------------ 重讀

/**
 * 總共最多讀幾次(含第一次)。頁面層量到的每次失敗率(檔頭「取得檔案到開啟之間」):核心實際的節奏約 400 次裡 0–1 次;每 20 ms 附加一行
 * (比核心密 28 倍)3–5%,重讀 1 次就讀到;每 5 ms 改一次修改時間(密 112 倍)第一次約 19%,重讀的那幾次約 2%(2,000 次裡最多重讀 3 次)。
 * 6 次在這些條件下都沒有用完過;用完時以 RecordReadRetryError 結束(不無限重試)。
 */
export const RECORD_READ_MAX_ATTEMPTS = 6;
/**
 * 第 1、2 … 次重讀之前等多久(毫秒;實際是 0.5–1.5 倍的亂數,不與固定節奏的寫入同步)。失敗是「取得檔案到開啟之間剛好有一次寫入」,
 * 立刻再取一次多半就讀到(第一格 0);越後面等越久,讓一陣密集的寫入過去。合計最多約 0.5 秒。
 */
export const RECORD_READ_RETRY_DELAYS_MS: readonly number[] = [0, 10, 30, 80, 200];
/**
 * 第一次開始讀之後超過這麼久(毫秒)就不再開始新的一次重讀:大檔讀到一半才失敗(檔案被截短)時每一次都要幾秒,
 * 教官等待的時間最壞是這個上限加最後一次讀的時間(2 小時的紀錄一次約 6–10 秒)。
 */
export const RECORD_READ_GIVE_UP_MS = 10_000;

/** 重讀到上限仍讀不到。cause:最後一次的錯誤;attempts:總共讀了幾次。 */
export class RecordReadRetryError extends Error {
  override readonly name = 'RecordReadRetryError';
  constructor(
    readonly attempts: number,
    override readonly cause: unknown,
  ) {
    super(`重新讀取 ${attempts - 1} 次後仍讀不到檔案:${String(cause)}`);
  }
}

export interface RecordReadRetryOptions {
  /** 總共最多讀幾次(含第一次;預設 RECORD_READ_MAX_ATTEMPTS) */
  maxAttempts?: number;
  /** 各次重讀之前的等待(預設 RECORD_READ_RETRY_DELAYS_MS;不夠長時沿用最後一個) */
  delaysMs?: readonly number[];
  /** 第一次開始讀之後超過這麼久就不再重讀(預設 RECORD_READ_GIVE_UP_MS) */
  giveUpAfterMs?: number;
  /** 還要不要這一次載入的結果(使用者又選了別的檔案時回傳 false):不再重讀,以當下的錯誤結束 */
  isCurrent?: () => boolean;
  /**
   * 決定重讀時呼叫(retry:第幾次重讀,由 1 起;err:剛才的錯誤;紀錄、測試用)。**不要在這裡更新畫面**:緊接著就要重新取得檔案,
   * 取得到開啟之間插進一次重繪,檔案被改動的機會就變大(見檔頭「取得檔案到開啟之間」);重讀的說明留到 read() 讀到第一段之後再寫。
   */
  onRetry?: (retry: number, err: unknown) => void;
  /** 時鐘、等待與亂數(預設 performance.now / setTimeout / Math.random;測試以假的替換) */
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
  random?: () => number;
}

export interface RecordReadResult<T> {
  value: T;
  /** 讀成功的那一份檔案(重讀過的話是最後取得的那一份;另存、再讀內容都用它) */
  blob: Blob;
  /** 總共讀了幾次(1 = 沒有重讀) */
  attempts: number;
}

const defaultNow = (): number => (typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now());
const defaultSleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * 讀一份紀錄;讀取端失敗而來源可以重新取得檔案時,重新取得、從頭重讀(次數與時間都有上限)。
 * read(blob, attempt):呼叫端的整個讀取與解析(attempt 由 1 起)。**每次呼叫都必須從頭來過**(新的解析器、新的狀態存放),不可以沿用上一次讀到一半的東西。
 *
 * 不重讀、把錯誤原樣往外丟的情形:不是讀取端的失敗(解析器 / onProgress 的例外)、來源不能重新取得(一般的檔案輸入:重讀沒有用)、
 * isCurrent() 為 false、取得檔案本身失敗(source.open() 被拒絕:檔案不在了)。重讀到上限或超過時間仍失敗 → RecordReadRetryError(cause 是最後一次的錯誤)。
 */
export async function readRecordSource<T>(source: RecordSource, read: (blob: Blob, attempt: number) => Promise<T>, options: RecordReadRetryOptions = {}): Promise<RecordReadResult<T>> {
  const maxAttempts = Math.max(1, options.maxAttempts ?? RECORD_READ_MAX_ATTEMPTS);
  const delays = options.delaysMs ?? RECORD_READ_RETRY_DELAYS_MS;
  const giveUpAfter = options.giveUpAfterMs ?? RECORD_READ_GIVE_UP_MS;
  const now = options.now ?? defaultNow;
  const sleep = options.sleep ?? defaultSleep;
  const random = options.random ?? Math.random;
  const started = now();
  for (let attempt = 1; ; attempt++) {
    // 取得檔案失敗(檔案不在了、權限被收回):到此為止,不重試
    const blob = await source.open();
    try {
      return { value: await read(blob, attempt), blob, attempts: attempt };
    } catch (err) {
      if (!isRecordReadFailure(err) || !source.reopenable) throw err;
      if (options.isCurrent && !options.isCurrent()) throw err;
      const delay = (delays.length > 0 ? delays[Math.min(attempt - 1, delays.length - 1)]! : 0) * (0.5 + random());
      if (attempt >= maxAttempts || now() - started + delay > giveUpAfter) {
        // 一次都沒有重讀過(第一次就超過時間、或上限是 1 次)時就是原本的錯誤
        if (attempt === 1) throw err;
        throw markRecordReadFailure(new RecordReadRetryError(attempt, err));
      }
      options.onRetry?.(attempt, err);
      if (delay > 0) await sleep(delay);
      if (options.isCurrent && !options.isCurrent()) throw err;
    }
  }
}

// ------------------------------------------------------------------ 開啟檔案的對話框

/** showOpenFilePicker 的選項(只列用到的) */
interface OpenFilePickerOptions {
  multiple?: boolean;
  id?: string;
  types?: Array<{ description?: string; accept: Record<string, string[]> }>;
}

type PickerWindow = { showOpenFilePicker?: (options?: OpenFilePickerOptions) => Promise<RecordFileHandle[]> };

/** 對話框一開就被取消得比這個時間(毫秒)還快 = 不是人按的取消,是對話框根本開不起來(自動化攔截、沒有視窗系統) */
export const PICKER_INSTANT_ABORT_MS = 100;
/** 已經有一個檔案對話框開著時,Chromium 拒絕 showOpenFilePicker 的訊息(NotAllowedError:「… File picker already active.」) */
const PICKER_ALREADY_ACTIVE = /already active/i;

/**
 * 以 File System Access API 的對話框(showOpenFilePicker)選紀錄檔:拿到的是檔案 handle,讀取失敗時可以重新取得檔案(見檔頭)。
 * 不支援或用不了的環境,呼叫端退回一般的檔案輸入:
 *   if (picker.available) { ev.preventDefault(); const sources = await picker.pick(…); if (sources === null) input.click(); … }
 * 用不了的情形只判斷一次(之後 available 一直是 false,直接走一般的檔案輸入)。
 *
 * 對話框還開著時又被呼叫(使用者在按鈕上按了兩下,第二下落在對話框出現之前或沒有被對話框擋住)**不算用不了**:這一下當作沒有選檔(空陣列),
 * 不開第二個對話框、也不停用。實測(Chromium 153,有畫面、沒有視窗管理員的 X 伺服器,兩下間隔 150 / 300 ms):對話框開著時再呼叫
 * showOpenFilePicker,約 12–16 ms 後被拒絕 —— 多數是「NotAllowedError: … File picker already active.」,也量到過
 * 「NotAllowedError: … User activation required.」,所以不靠訊息判斷,由這個物件自己記著對話框還沒有結束。原本把這種拒絕當成用不了,
 * 於是這個頁面到重新整理為止都退回一般的檔案輸入(讀取失敗不能重讀),而且當下又多開一個檔案輸入自己的對話框。
 * Windows 的 Edge / WebView2 上第二下會不會送到頁面沒有量(對話框通常會擋住它的視窗);送到的話走的也是這一條。
 */
export class RecordFilePicker {
  private disabled = false;
  /** 這個物件開的對話框還沒有結束 */
  private picking = false;

  /**
   * win:取 showOpenFilePicker 的物件(預設目前的 window;測試以假的替換);now:時鐘。
   */
  constructor(
    private readonly win: PickerWindow = globalThis as unknown as PickerWindow,
    private readonly now: () => number = defaultNow,
  ) {}

  /** 這個環境有 showOpenFilePicker 而且還沒有發現它用不了 */
  get available(): boolean {
    return !this.disabled && typeof this.win.showOpenFilePicker === 'function';
  }

  /**
   * 開對話框(必須在使用者手勢的處理函式裡呼叫)。回傳選到的來源;空陣列 = 這一下沒有選檔(使用者取消,或對話框本來就還開著);
   * null = 用不了(沒有這個 API、被瀏覽器或政策拒絕、對話框一開就被取消),呼叫端改用一般的檔案輸入。
   */
  async pick(options: { multiple?: boolean; description?: string; extensions?: readonly string[] } = {}): Promise<RecordSource[] | null> {
    const show = this.win.showOpenFilePicker;
    if (this.disabled || typeof show !== 'function') return null;
    // 上一次開的對話框還沒有結束:不再開一個(瀏覽器也不會讓它開),選檔由那一個對話框交回
    if (this.picking) return [];
    const started = this.now();
    let handles: RecordFileHandle[];
    this.picking = true;
    try {
      const pickerOptions: OpenFilePickerOptions = { multiple: options.multiple ?? false, id: 'simosa-brm-records' };
      if (options.extensions && options.extensions.length > 0) pickerOptions.types = [{ description: options.description ?? '練習紀錄', accept: { 'application/json': [...options.extensions] } }];
      handles = await show.call(this.win, pickerOptions);
    } catch (err) {
      const name = typeof err === 'object' && err !== null ? (err as { name?: unknown }).name : undefined;
      // 使用者按了取消(AbortError,而且對話框開了一段時間):沒有選檔案
      if (name === 'AbortError' && this.now() - started >= PICKER_INSTANT_ABORT_MS) return [];
      // 瀏覽器說已經有一個檔案對話框開著(不是這個物件開的,所以上面沒有擋到):同樣只是這一下沒有選檔
      if (name === 'NotAllowedError' && PICKER_ALREADY_ACTIVE.test(String((err as { message?: unknown }).message ?? ''))) return [];
      this.disabled = true;
      return null;
    } finally {
      this.picking = false;
    }
    return handles.map((h) => handleRecordSource(h));
  }
}
