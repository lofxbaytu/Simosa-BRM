// 練習紀錄的來源與「重新取得檔案再讀」(lib/record-source.ts;第三十一輪「核心仍在寫入的紀錄讀取偶爾失敗」)。
// 實測(真實 Chromium):檔案在選取後被改動時 Blob.stream() 的第一次 read() 以 TypeError: network error 失敗,同一個 File 再讀幾次都一樣,
// 要重新向瀏覽器取得檔案(檔案 handle 的 getFile()、拖放項目的 file())才讀得到。這裡以假的 Blob / stream 釘住每一種情形:
//  - 第一次 read() 就失敗 → 重新取得、重讀成功;讀到一半失敗 → 從頭重讀,結果與沒有失敗時逐項相同(狀態不重複、不缺漏);
//  - 每次都失敗 → 讀到上限就結束(不無限重試),說明是中文;超過時間上限不再重讀;
//  - 解析器 / onProgress 擲出的例外、不能重新取得的來源(一般的檔案輸入)、取得檔案失敗(檔案不在了)、已被後來的載入取代:不重讀;
//  - 截短(讀到的位元組比 size 少)仍然是錯誤,而且是讀取端的失敗;
//  - 讀取失敗的說明(recordReadErrorText):中文,認不出來的才附原始錯誤;解析器的例外照原樣;
//  - 檔案對話框(showOpenFilePicker)與拖放:拿到可以重新取得的來源;用不了時回傳 null(呼叫端退回一般的檔案輸入)。
//  - 審查後修正:對話框還開著時又被呼叫(在按鈕上按了兩下)不算用不了 —— 空陣列、不停用、不開第二個對話框。
import { describe, expect, it, vi } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import {
  blobRecordSource,
  dataTransferHasFiles,
  handleRecordSource,
  PICKER_INSTANT_ABORT_MS,
  readRecordSource,
  RECORD_READ_GIVE_UP_MS,
  RECORD_READ_MAX_ATTEMPTS,
  RECORD_READ_RETRY_DELAYS_MS,
  RecordFilePicker,
  RecordReadRetryError,
  recordSourcesFromDataTransfer,
  type RecordSource,
} from '../src/lib/record-source.js';
import { forEachBlobTextChunk, isRecordReadFailure, parseReplayBlob, recordReadErrorText, RECORD_CHANGED_TEXT, RECORD_GONE_TEXT, RECORD_UNREADABLE_TEXT } from '../src/lib/record-stream.js';
import { loadReplayBlob, loadReplayText } from '../src/lib/replay-states.js';
import { fixtureText } from './support/record-corpus.js';

const E07 = fixtureText('record-E07-core.jsonl');
const E07_BYTES = new TextEncoder().encode(E07);
/** Chromium 的 stream 讀不到檔案時的錯誤 */
const networkError = (): TypeError => new TypeError('network error');

interface FakeBlobOptions {
  /** 交出這麼多段之後,下一次 read() 以 failWith 失敗(0 = 第一次 read() 就失敗) */
  failAfter?: number;
  failWith?: unknown;
  /** 交出這麼多段之後就結束、不報錯(讀取途中檔案被截短) */
  endAfter?: number;
  piece?: number;
}

/** 假的檔案:只有 size 與 stream()(分段讀取只用這兩個);state 記下開了幾次、交出幾段、有沒有被取消。 */
function fakeBlob(bytes: Uint8Array, options: FakeBlobOptions = {}) {
  const piece = options.piece ?? 20_000;
  const state = { opened: 0, delivered: 0, cancelled: false };
  const blob = {
    size: bytes.byteLength,
    stream() {
      state.opened++;
      let off = 0;
      let given = 0;
      return new ReadableStream<Uint8Array>({
        pull(controller) {
          if (options.failAfter !== undefined && given >= options.failAfter) return controller.error(options.failWith);
          if (off >= bytes.byteLength || (options.endAfter !== undefined && given >= options.endAfter)) return controller.close();
          controller.enqueue(bytes.subarray(off, off + piece));
          off += piece;
          given++;
          state.delivered++;
        },
        cancel() {
          state.cancelled = true;
        },
      });
    },
  };
  return { blob: blob as unknown as Blob, state };
}

/** 可以重新取得檔案的來源:每次 open() 依序交出 blobs 的下一個(用完之後一直是最後一個)。 */
function scriptedSource(blobs: readonly Blob[], reopenable = true): RecordSource & { opens: number } {
  const src = {
    name: 'live.jsonl',
    opens: 0,
    reopenable,
    open(): Promise<Blob> {
      const b = blobs[Math.min(src.opens, blobs.length - 1)]!;
      src.opens++;
      return Promise.resolve(b);
    },
  };
  return src;
}

/** 不等待的重讀選項(記下每次等待的毫秒數) */
function instant(waits: number[] = []) {
  return { sleep: (ms: number) => (waits.push(ms), Promise.resolve()), random: () => 0.5 };
}

describe('讀取失敗時重新取得檔案再讀(readRecordSource)', () => {
  it('第一次 read() 就以 TypeError: network error 失敗(檔案在選取後被改動):重新取得檔案、重讀成功,結果與沒有失敗時逐項相同', async () => {
    const stale = fakeBlob(E07_BYTES, { failAfter: 0, failWith: networkError() });
    const fresh = fakeBlob(E07_BYTES);
    const source = scriptedSource([stale.blob, fresh.blob]);
    const retries: Array<[number, unknown]> = [];
    const waits: number[] = [];
    const res = await readRecordSource(source, (blob) => parseReplayBlob(blob, { chunkBytes: 8192 }), { ...instant(waits), onRetry: (n, err) => retries.push([n, err]) });
    expect(res.attempts).toBe(2);
    expect(res.blob).toBe(fresh.blob);
    expect(res.value.data).toStrictEqual(parseReplay(E07));
    expect(source.opens).toBe(2);
    expect(stale.state).toMatchObject({ opened: 1, delivered: 0 });
    expect(fresh.state.opened).toBe(1);
    expect(retries).toHaveLength(1);
    expect(retries[0]![0]).toBe(1);
    expect(String(retries[0]![1])).toBe('TypeError: network error');
    // 第一次重讀不等待(表的第一格是 0)
    expect(waits).toEqual([]);
  });

  it('讀到一半失敗:從頭重讀(每次都是新的解析器與狀態存放)—— 狀態不重複、不缺漏,與一次讀成功時相同', async () => {
    const want = parseReplay(E07);
    for (const failAfter of [1, 3, 10]) {
      // 第一份交出 failAfter 段之後失敗,第二份讀到一半被截短(交出一部分就結束),第三份完整
      const a = fakeBlob(E07_BYTES, { failAfter, failWith: networkError(), piece: 7000 });
      const b = fakeBlob(E07_BYTES, { endAfter: failAfter + 2, piece: 7000 });
      const c = fakeBlob(E07_BYTES, { piece: 7000 });
      const source = scriptedSource([a.blob, b.blob, c.blob]);
      const attempts: number[] = [];
      const res = await readRecordSource(
        source,
        (blob, attempt) => {
          attempts.push(attempt);
          return parseReplayBlob(blob, { chunkBytes: 4096 });
        },
        instant(),
      );
      expect(attempts, `failAfter ${failAfter}`).toEqual([1, 2, 3]);
      expect(a.state.delivered).toBe(failAfter);
      expect(res.value.data.states.length).toBe(want.states.length);
      expect(res.value.data).toStrictEqual(want);
      expect(res.blob).toBe(c.blob);
    }
    // 重播檢視的載入(降採樣的狀態存放 + 以全部狀態算的摘要 / 航跡):同樣與一次讀成功時相同
    const view = loadReplayText(E07, { maxStates: 64 });
    const half = fakeBlob(E07_BYTES, { failAfter: 12, failWith: networkError(), piece: 5000 });
    const res = await readRecordSource(scriptedSource([half.blob, fakeBlob(E07_BYTES).blob]), (blob) => loadReplayBlob(blob, { maxStates: 64, chunkBytes: 4096 }), instant());
    expect(res.attempts).toBe(2);
    expect(res.value).toStrictEqual(view);
    expect(res.value.stateStats.total).toBe(view.stateStats.total);
  });

  it('每次都失敗:讀到上限就結束(不無限重試),RecordReadRetryError 帶次數與最後一次的錯誤;等待照表(0.5–1.5 倍)', async () => {
    const blobs = Array.from({ length: 20 }, () => fakeBlob(E07_BYTES, { failAfter: 0, failWith: networkError() }));
    const source = scriptedSource(blobs.map((b) => b.blob));
    const waits: number[] = [];
    const err = await readRecordSource(source, (blob) => parseReplayBlob(blob), instant(waits)).then(
      () => null,
      (e: unknown) => e,
    );
    expect(err).toBeInstanceOf(RecordReadRetryError);
    expect((err as RecordReadRetryError).attempts).toBe(RECORD_READ_MAX_ATTEMPTS);
    expect(String((err as RecordReadRetryError).cause)).toBe('TypeError: network error');
    expect(isRecordReadFailure(err)).toBe(true);
    expect(source.opens).toBe(RECORD_READ_MAX_ATTEMPTS);
    expect(blobs.filter((b) => b.state.opened > 0)).toHaveLength(RECORD_READ_MAX_ATTEMPTS);
    // random() = 0.5 → 剛好是表上的毫秒數(0 不等待)
    expect(waits).toEqual(RECORD_READ_RETRY_DELAYS_MS.filter((ms) => ms > 0));
    expect(recordReadErrorText(err)).toBe(`檔案持續被改動,重新讀取 ${RECORD_READ_MAX_ATTEMPTS - 1} 次後仍讀不到(請待練習結束後再開啟,或稍後重新選取一次)`);
    // 等待的上下限:亂數 0 → 0.5 倍,接近 1 → 1.5 倍;總等待不到 1 秒
    for (const [r, factor] of [
      [0, 0.5],
      [0.999, 1.499],
    ] as const) {
      const w: number[] = [];
      await readRecordSource(scriptedSource(blobs.map((b) => b.blob)), (blob) => parseReplayBlob(blob), { sleep: (ms) => (w.push(ms), Promise.resolve()), random: () => r }).catch(() => undefined);
      expect(w.map((ms) => Math.round(ms * 1000) / 1000)).toEqual(RECORD_READ_RETRY_DELAYS_MS.filter((ms) => ms > 0).map((ms) => Math.round(ms * factor * 1000) / 1000));
      expect(w.reduce((a, b) => a + b, 0)).toBeLessThan(1000);
    }
    // 次數可調;上限 1 = 不重讀,就是原本的錯誤
    const once = scriptedSource(blobs.map((b) => b.blob));
    await expect(readRecordSource(once, (blob) => parseReplayBlob(blob), { ...instant(), maxAttempts: 1 })).rejects.toThrow('network error');
    expect(once.opens).toBe(1);
  });

  it('超過時間上限就不再開始新的一次重讀(每次讀很久的大檔)', async () => {
    let clock = 0;
    const blobs = Array.from({ length: 10 }, () => fakeBlob(E07_BYTES, { failAfter: 2, failWith: networkError() }));
    const source = scriptedSource(blobs.map((b) => b.blob));
    const err = await readRecordSource(
      source,
      async (blob) => {
        clock += 4000; // 每一次讀 4 秒才失敗
        return parseReplayBlob(blob);
      },
      { ...instant(), now: () => clock },
    ).then(
      () => null,
      (e: unknown) => e,
    );
    // 0 → 4 s 失敗(重讀)→ 8 s 失敗(重讀)→ 12 s 失敗:已超過 10 秒,不再重讀
    expect(RECORD_READ_GIVE_UP_MS).toBe(10_000);
    expect(source.opens).toBe(3);
    expect(err).toBeInstanceOf(RecordReadRetryError);
    expect((err as RecordReadRetryError).attempts).toBe(3);
    // 第一次就超過時間:沒有重讀過,就是原本的錯誤
    clock = 0;
    const slow = scriptedSource(blobs.map((b) => b.blob));
    const first = await readRecordSource(slow, async (blob) => ((clock += 11_000), parseReplayBlob(blob)), { ...instant(), now: () => clock }).then(
      () => null,
      (e: unknown) => e,
    );
    expect(slow.opens).toBe(1);
    expect(String(first)).toBe('TypeError: network error');
  });

  it('解析器 / onProgress 擲出的例外不重讀(不是讀取端的失敗):原樣往外丟,檔案只取一次', async () => {
    const boom = new Error('onProgress 擲出');
    const good = fakeBlob(E07_BYTES);
    const source = scriptedSource([good.blob, good.blob]);
    const onRetry = vi.fn();
    await expect(
      readRecordSource(
        source,
        (blob) =>
          parseReplayBlob(blob, {
            chunkBytes: 4096,
            onProgress: (read) => {
              if (read > 10_000) throw boom;
            },
          }),
        { ...instant(), onRetry },
      ),
    ).rejects.toBe(boom);
    expect(isRecordReadFailure(boom)).toBe(false);
    expect(source.opens).toBe(1);
    expect(good.state.opened).toBe(1);
    expect(good.state.cancelled).toBe(true);
    expect(onRetry).not.toHaveBeenCalled();
    // read 本身(不是 stream)擲出的例外也一樣
    const bug = new TypeError("Cannot read properties of undefined (reading 'x')");
    const again = scriptedSource([good.blob]);
    await expect(readRecordSource(again, () => Promise.reject(bug), instant())).rejects.toBe(bug);
    expect(again.opens).toBe(1);
    // 畫面上:解析器的例外照原樣顯示(不套讀取失敗的說明)
    expect(recordReadErrorText(bug)).toBe(String(bug));
    expect(recordReadErrorText(boom)).toBe('Error: onProgress 擲出');
  });

  it('截短(stream 交出的位元組比 size 少)仍然是錯誤,而且是讀取端的失敗:不能重新取得的來源以 NotReadableError 結束,可以的重讀', async () => {
    const cut = fakeBlob(E07_BYTES, { endAfter: 3 });
    const err = await readRecordSource(blobRecordSource(cut.blob, 'cut.jsonl'), (blob) => parseReplayBlob(blob), instant()).then(
      () => null,
      (e: unknown) => e,
    );
    expect((err as Error).name).toBe('NotReadableError');
    expect((err as Error).message).toBe(`檔案只讀到 60000 / ${E07_BYTES.byteLength} 位元組(讀取途中檔案被截短或改動)`);
    expect(isRecordReadFailure(err)).toBe(true);
    expect(recordReadErrorText(err)).toBe(RECORD_CHANGED_TEXT);
    expect(cut.state.opened).toBe(1);
    // 可以重新取得:重讀到新的那一份(截短後的檔案本身是完整的一份)
    const shorter = E07_BYTES.subarray(0, 40_000);
    const res = await readRecordSource(scriptedSource([fakeBlob(E07_BYTES, { endAfter: 2 }).blob, new Blob([shorter])]), (blob) => parseReplayBlob(blob), instant());
    expect(res.attempts).toBe(2);
    expect(res.value.data).toStrictEqual(parseReplay(new TextDecoder().decode(shorter)));
  });

  it('不能重新取得的來源(一般的檔案輸入給的 File):不重讀 —— 實測同一個 File 再讀幾次都一樣失敗;說明是中文', async () => {
    const stale = fakeBlob(E07_BYTES, { failAfter: 0, failWith: networkError() });
    const source = blobRecordSource(stale.blob, 'live.jsonl');
    expect(source.reopenable).toBe(false);
    const onRetry = vi.fn();
    const err = await readRecordSource(source, (blob) => parseReplayBlob(blob), { ...instant(), onRetry }).then(
      () => null,
      (e: unknown) => e,
    );
    expect(String(err)).toBe('TypeError: network error');
    expect(stale.state.opened).toBe(1);
    expect(onRetry).not.toHaveBeenCalled();
    expect(recordReadErrorText(err)).toBe(RECORD_CHANGED_TEXT);
    // 沒有失敗時:讀一次,結果相同
    const ok = await readRecordSource(blobRecordSource(new Blob([E07]), 'e07.jsonl'), (blob) => parseReplayBlob(blob));
    expect(ok.attempts).toBe(1);
    expect(ok.value.data).toStrictEqual(parseReplay(E07));
  });

  it('取得檔案失敗(檔案被刪除、改名、磁碟被移除:NotFoundError)不重試;重讀途中檔案不見了也到此為止', async () => {
    const gone = new DOMException('A requested file or directory could not be found at the time an operation was processed.', 'NotFoundError');
    const handle = { name: 'gone.jsonl', getFile: vi.fn(() => Promise.reject(gone)) };
    const read = vi.fn();
    const err = await readRecordSource(handleRecordSource(handle), read, instant()).then(
      () => null,
      (e: unknown) => e,
    );
    expect(err).toBe(gone);
    expect(isRecordReadFailure(err)).toBe(true);
    expect(handle.getFile).toHaveBeenCalledTimes(1);
    expect(read).not.toHaveBeenCalled();
    expect(recordReadErrorText(err)).toBe(RECORD_GONE_TEXT);
    // 第一次讀取失敗(被改動),重新取得時檔案已經不在
    const stale = fakeBlob(E07_BYTES, { failAfter: 0, failWith: networkError() });
    let calls = 0;
    const flaky = { name: 'moved.jsonl', getFile: () => (calls++ === 0 ? Promise.resolve(stale.blob as File) : Promise.reject(gone)) };
    await expect(readRecordSource(handleRecordSource(flaky), (blob) => parseReplayBlob(blob), instant())).rejects.toBe(gone);
    expect(calls).toBe(2);
  });

  it('已被後來的載入取代(isCurrent 為 false):不再重讀,也不再取得檔案', async () => {
    const blobs = Array.from({ length: 4 }, () => fakeBlob(E07_BYTES, { failAfter: 0, failWith: networkError() }));
    // 失敗的當下已經被取代
    let current = false;
    const a = scriptedSource(blobs.map((b) => b.blob));
    await expect(readRecordSource(a, (blob) => parseReplayBlob(blob), { ...instant(), isCurrent: () => current })).rejects.toThrow('network error');
    expect(a.opens).toBe(1);
    // 等待重讀的期間被取代
    current = true;
    const b = scriptedSource(blobs.map((x) => x.blob));
    const p = readRecordSource(b, (blob) => parseReplayBlob(blob), {
      delaysMs: [50],
      random: () => 0.5,
      sleep: async () => {
        current = false;
      },
      isCurrent: () => current,
    });
    await expect(p).rejects.toThrow('network error');
    expect(b.opens).toBe(1);
  });
});

describe('讀取端的失敗與說明(record-stream.ts)', () => {
  it('read() 被拒絕、位元組不足、slice 讀取被拒絕都記成讀取端的失敗;onChunk 擲出的例外不是', async () => {
    const ne = networkError();
    await expect(forEachBlobTextChunk(fakeBlob(E07_BYTES, { failAfter: 1, failWith: ne }).blob, () => undefined)).rejects.toBe(ne);
    expect(isRecordReadFailure(ne)).toBe(true);
    const sliceErr = new Error('slice 讀不到');
    const sliceOnly = { size: 100, slice: () => ({ arrayBuffer: () => Promise.reject(sliceErr) }) } as unknown as Blob;
    await expect(forEachBlobTextChunk(sliceOnly, () => undefined)).rejects.toBe(sliceErr);
    expect(isRecordReadFailure(sliceErr)).toBe(true);
    const parseErr = new Error('解析失敗');
    await expect(
      forEachBlobTextChunk(new Blob([E07]), () => {
        throw parseErr;
      }),
    ).rejects.toBe(parseErr);
    expect(isRecordReadFailure(parseErr)).toBe(false);
    // stream() 本身擲出(取不到 reader)也算;不是物件的錯誤值包成 Error
    const thrower = {
      size: 10,
      stream: () => {
        throw 'boom';
      },
    } as unknown as Blob;
    const wrapped = await forEachBlobTextChunk(thrower, () => undefined).then(
      () => null,
      (e: unknown) => e,
    );
    expect(wrapped).toBeInstanceOf(Error);
    expect(isRecordReadFailure(wrapped)).toBe(true);
    expect(isRecordReadFailure('boom')).toBe(false);
    expect(isRecordReadFailure(null)).toBe(false);
  });

  it('recordReadErrorText:教官看到的是中文(怎麼做),認不出來的讀取失敗才在後面附原始錯誤', async () => {
    // Chromium 的 stream:檔案在選取後被改動(或被刪除、改名、磁碟被移除 —— 同一個錯誤,分不出來)
    const ne = await forEachBlobTextChunk(fakeBlob(E07_BYTES, { failAfter: 0, failWith: networkError() }).blob, () => undefined).then(
      () => null,
      (e: unknown) => e,
    );
    expect(recordReadErrorText(ne)).toBe('檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)');
    expect(recordReadErrorText(ne)).not.toMatch(/network error|TypeError/);
    // FileReader 那一系
    expect(recordReadErrorText(new DOMException('The requested file could not be read', 'NotReadableError'))).toBe(RECORD_CHANGED_TEXT);
    expect(recordReadErrorText(new DOMException('could not be found', 'NotFoundError'))).toBe(RECORD_GONE_TEXT);
    expect(RECORD_GONE_TEXT).toContain('重新選取一次');
    // 認不出來的讀取失敗:中文說明 + 原始錯誤
    const odd = await forEachBlobTextChunk(fakeBlob(E07_BYTES, { failAfter: 0, failWith: new Error('EIO: i/o error') }).blob, () => undefined).then(
      () => null,
      (e: unknown) => e,
    );
    expect(recordReadErrorText(odd)).toBe(`${RECORD_UNREADABLE_TEXT};原始錯誤:Error: EIO: i/o error`);
    expect(RECORD_UNREADABLE_TEXT).toContain('請重新選取一次');
    // 不是讀取端的 TypeError(程式的例外)照原樣;訊息不是 network error 的讀取端 TypeError 當作認不出來
    expect(recordReadErrorText(new TypeError('network error'))).toBe('TypeError: network error');
    const other = await forEachBlobTextChunk(fakeBlob(E07_BYTES, { failAfter: 0, failWith: new TypeError('something else') }).blob, () => undefined).then(
      () => null,
      (e: unknown) => e,
    );
    expect(recordReadErrorText(other)).toBe(`${RECORD_UNREADABLE_TEXT};原始錯誤:TypeError: something else`);
    // 重讀到上限:最後一次不是「被改動」時附上最後一次的說明
    expect(recordReadErrorText(new RecordReadRetryError(4, odd))).toBe(`檔案持續被改動,重新讀取 3 次後仍讀不到(請待練習結束後再開啟,或稍後重新選取一次);最後一次:${RECORD_UNREADABLE_TEXT};原始錯誤:Error: EIO: i/o error`);
    expect(recordReadErrorText('字串錯誤')).toBe('字串錯誤');
  });
});

describe('檔案對話框(RecordFilePicker)與拖放', () => {
  const handleFor = (name: string, text: string) => ({ kind: 'file', name, getFile: vi.fn(() => Promise.resolve(new File([text], name))) });

  it('showOpenFilePicker 選到的檔案是可以重新取得的來源:每次 open() 都 getFile()(到要讀的時候才取)', async () => {
    const h1 = handleFor('a.jsonl', E07);
    const h2 = handleFor('b.json', '{}');
    const show = vi.fn((_options?: unknown) => Promise.resolve([h1, h2]));
    const picker = new RecordFilePicker({ showOpenFilePicker: show });
    expect(picker.available).toBe(true);
    const sources = await picker.pick({ multiple: true, description: '練習紀錄', extensions: ['.jsonl', '.json', '.txt'] });
    expect(show).toHaveBeenCalledWith({ multiple: true, id: 'simosa-brm-records', types: [{ description: '練習紀錄', accept: { 'application/json': ['.jsonl', '.json', '.txt'] } }] });
    expect(sources?.map((s) => [s.name, s.reopenable])).toEqual([
      ['a.jsonl', true],
      ['b.json', true],
    ]);
    // 選完還沒有取檔案;讀的時候才取,重讀再取一次
    expect(h1.getFile).not.toHaveBeenCalled();
    const res = await readRecordSource(sources![0]!, (blob) => parseReplayBlob(blob));
    expect(res.value.data).toStrictEqual(parseReplay(E07));
    expect(h1.getFile).toHaveBeenCalledTimes(1);
    await sources![0]!.open();
    expect(h1.getFile).toHaveBeenCalledTimes(2);
    expect(h2.getFile).not.toHaveBeenCalled();
    // 預設單選、不限副檔名
    await picker.pick();
    expect(show).toHaveBeenLastCalledWith({ multiple: false, id: 'simosa-brm-records' });
  });

  it('沒有這個 API、被拒絕、或對話框一開就被取消(開不起來):回傳 null 並且不再嘗試(呼叫端退回一般的檔案輸入);使用者取消是空陣列', async () => {
    expect(new RecordFilePicker({}).available).toBe(false);
    expect(await new RecordFilePicker({}).pick()).toBeNull();
    // 使用者開了對話框一段時間後按取消
    let clock = 0;
    const abort = () => new DOMException('The user aborted a request.', 'AbortError');
    const cancelShow = vi.fn(() => {
      clock += 2500;
      return Promise.reject(abort());
    });
    const cancelled = new RecordFilePicker({ showOpenFilePicker: cancelShow }, () => clock);
    expect(await cancelled.pick()).toEqual([]);
    expect(cancelled.available).toBe(true);
    // 一開就被取消(自動化攔截:Intercepted by Page.setInterceptFileChooserDialog();實測 0.3 ms)
    const instantShow = vi.fn(() => {
      clock += PICKER_INSTANT_ABORT_MS - 1;
      return Promise.reject(abort());
    });
    const intercepted = new RecordFilePicker({ showOpenFilePicker: instantShow }, () => clock);
    expect(await intercepted.pick()).toBeNull();
    expect(intercepted.available).toBe(false);
    expect(await intercepted.pick()).toBeNull();
    expect(instantShow).toHaveBeenCalledTimes(1);
    // 被瀏覽器或政策拒絕(SecurityError / NotAllowedError)、選項不被接受(TypeError)
    for (const err of [new DOMException('Must be handling a user gesture', 'SecurityError'), new DOMException('denied', 'NotAllowedError'), new TypeError('bad types')]) {
      const p = new RecordFilePicker({ showOpenFilePicker: () => ((clock += 5000), Promise.reject(err)) }, () => clock);
      expect(await p.pick()).toBeNull();
      expect(p.available).toBe(false);
    }
  });

  it('對話框還開著時又被呼叫(使用者在按鈕上按了兩下):這一下當作沒有選檔(空陣列)—— 不開第二個對話框、不停用;選檔由原本那一個對話框交回', async () => {
    // 實測(Chromium 153):對話框開著時再呼叫 showOpenFilePicker,約 12–16 ms 後以這個錯誤拒絕
    const alreadyActive = () => new DOMException("Failed to execute 'showOpenFilePicker' on 'Window': File picker already active.", 'NotAllowedError');
    const h = handleFor('a.jsonl', E07);
    let clock = 0;
    let settle: { resolve: (handles: unknown[]) => void; reject: (err: unknown) => void } | null = null;
    const show = vi.fn((_options?: unknown) => {
      if (settle) return Promise.reject(alreadyActive());
      return new Promise<unknown[]>((resolve, reject) => {
        settle = { resolve, reject };
      });
    });
    const finish = (how: 'resolve' | 'reject', value: unknown) => {
      const s = settle!;
      settle = null;
      if (how === 'resolve') s.resolve(value as unknown[]);
      else s.reject(value);
    };
    const picker = new RecordFilePicker({ showOpenFilePicker: show as never }, () => clock);
    const first = picker.pick({ multiple: true });
    // 第二、三下:對話框還開著 —— 馬上回傳空陣列,沒有再向瀏覽器要對話框
    expect(await picker.pick({ multiple: true })).toEqual([]);
    clock += 300;
    expect(await picker.pick()).toEqual([]);
    expect(show).toHaveBeenCalledTimes(1);
    expect(picker.available).toBe(true);
    // 使用者在原本的對話框選了檔案
    clock += 4000;
    finish('resolve', [h]);
    expect((await first)?.map((s) => [s.name, s.reopenable])).toEqual([['a.jsonl', true]]);
    expect(picker.available).toBe(true);
    // 對話框結束之後再按:照常開(成功、使用者取消之後都一樣)
    const second = picker.pick();
    expect(show).toHaveBeenCalledTimes(2);
    expect(await picker.pick()).toEqual([]);
    clock += 3000;
    finish('reject', new DOMException('The user aborted a request.', 'AbortError'));
    expect(await second).toEqual([]);
    const third = picker.pick();
    expect(show).toHaveBeenCalledTimes(3);
    finish('resolve', []);
    expect(await third).toEqual([]);
    expect(picker.available).toBe(true);
    // 對話框不是這個物件開的(頁面上別處開的):瀏覽器以「already active」拒絕 —— 同樣只是這一下沒有選檔,不停用
    const foreign = new RecordFilePicker({ showOpenFilePicker: () => ((clock += 14), Promise.reject(alreadyActive())) }, () => clock);
    expect(await foreign.pick()).toEqual([]);
    expect(foreign.available).toBe(true);
    // 其他的 NotAllowedError(被政策拒絕)仍然是用不了:退回一般的檔案輸入
    const denied = new RecordFilePicker({ showOpenFilePicker: () => ((clock += 5000), Promise.reject(new DOMException('The request is not allowed by the user agent or the platform in the current context.', 'NotAllowedError'))) }, () => clock);
    expect(await denied.pick()).toBeNull();
    expect(denied.available).toBe(false);
    // 對話框一開就被取消(開不起來)之後,下一下直接是 null(不會因為「還開著」而一直回傳空陣列)
    const instantAbort = new RecordFilePicker({ showOpenFilePicker: () => Promise.reject(new DOMException('aborted', 'AbortError')) }, () => clock);
    expect(await instantAbort.pick()).toBeNull();
    expect(await instantAbort.pick()).toBeNull();
  });

  it('拖放:能重新取得的優先(檔案 handle > 檔案項目 > 拖放當下的 File);資料夾與非檔案的項目略過;必須同步取出', async () => {
    const fileA = new File([E07], 'a.jsonl');
    const fileB = new File(['{}'], 'b.json');
    const fileC = new File(['x'], 'c.txt');
    const entryFile = vi.fn((ok: (f: File) => void) => ok(new File(['{}'], 'b.json')));
    const hA = handleFor('a.jsonl', E07);
    let live = true;
    const guard = <T>(v: T): T => {
      if (!live) throw new Error('事件結束後不可以再取用 DataTransfer');
      return v;
    };
    const items = [
      { kind: 'string', getAsFile: () => null },
      // 有 handle(安全來源的 Chromium)
      { kind: 'file', getAsFile: () => guard(fileA), webkitGetAsEntry: () => guard({ isFile: true, name: 'a.jsonl', file: vi.fn() }), getAsFileSystemHandle: () => guard(Promise.resolve(hA)) },
      // 只有檔案項目(不是安全來源的頁面)
      { kind: 'file', getAsFile: () => guard(fileB), webkitGetAsEntry: () => guard({ isFile: true, name: 'b.json', file: entryFile }) },
      // 資料夾
      { kind: 'file', getAsFile: () => null, webkitGetAsEntry: () => guard({ isFile: false, name: 'dir', file: vi.fn() }), getAsFileSystemHandle: () => guard(Promise.resolve({ kind: 'directory', name: 'dir' })) },
      // 什麼都沒有(舊瀏覽器):拖放當下的 File
      { kind: 'file', getAsFile: () => guard(fileC) },
      // handle 取不到(被拒絕):退回檔案項目
      { kind: 'file', getAsFile: () => guard(fileB), webkitGetAsEntry: () => guard({ isFile: true, name: 'd.json', file: entryFile }), getAsFileSystemHandle: () => guard(Promise.reject(new Error('denied'))) },
    ];
    const dt = { types: ['Files'], items, files: [fileA, fileB, fileC] } as unknown as DataTransfer;
    expect(dataTransferHasFiles(dt)).toBe(true);
    expect(dataTransferHasFiles({ types: ['text/plain'], items: [], files: [] } as unknown as DataTransfer)).toBe(false);
    expect(dataTransferHasFiles(null)).toBe(false);
    const pending = recordSourcesFromDataTransfer(dt);
    live = false; // drop 事件結束
    const sources = await pending;
    expect(sources.map((s) => [s.name, s.reopenable])).toEqual([
      ['a.jsonl', true],
      ['b.json', true],
      ['c.txt', false],
      ['d.json', true],
    ]);
    expect((await readRecordSource(sources[0]!, (blob) => parseReplayBlob(blob))).value.data).toStrictEqual(parseReplay(E07));
    expect(hA.getFile).toHaveBeenCalledTimes(1);
    expect(await (await sources[1]!.open()).text()).toBe('{}');
    expect(entryFile).toHaveBeenCalledTimes(1);
    expect(await sources[2]!.open()).toBe(fileC);
    // 檔案項目取檔案失敗:讀取端的失敗
    const gone = new DOMException('gone', 'NotFoundError');
    const failing = recordSourcesFromDataTransfer({ types: ['Files'], items: [{ kind: 'file', getAsFile: () => fileB, webkitGetAsEntry: () => ({ isFile: true, name: 'e.json', file: (_ok: unknown, fail: (e: unknown) => void) => fail(gone) }) }], files: [] } as unknown as DataTransfer);
    const err = await (await failing)[0]!.open().then(
      () => null,
      (e: unknown) => e,
    );
    expect(err).toBe(gone);
    expect(isRecordReadFailure(err)).toBe(true);
    // 沒有 items 的 DataTransfer(測試替身、舊環境):用 files
    const plain = await recordSourcesFromDataTransfer({ types: ['Files'], files: [fileC] } as unknown as DataTransfer);
    expect(plain.map((s) => [s.name, s.reopenable])).toEqual([['c.txt', false]]);
  });
});
