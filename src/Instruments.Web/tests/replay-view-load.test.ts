// 重播檢視讀紀錄檔(第三十一輪「重播檢視讀大檔」;happy-dom):
//  - 檔案分段讀取(loadBlob):不呼叫 Blob.text() / slice(),整個檔案只開一次 stream();畫面與 loadText(整份文字)相同;
//  - 8 MB 以上的檔案讀取中顯示百分比,較小的只顯示「讀取中…」;
//  - 載入序號:大檔還沒讀完時又選了別的檔案(或 loadText),先前那一次在下一段中止、不蓋掉後開的紀錄,也不留下進度或錯誤訊息;
//  - 讀取失敗:工具列顯示中文說明(核心仍在寫入的紀錄 = NotReadableError),原本載入的紀錄不動;之後成功載入時清掉;
//  - 檔案選擇器:開對話框前清掉上一次的選擇(同一個檔案再選一次也會重讀);
//  - 超過上限的紀錄:資訊列之後寫明全部筆數、每幾筆取 1 筆與間隔(不得有無聲的降採樣),逐筆鈕的說明與步距跟著變,
//    時間軸上任一時刻顯示該時刻(含)之前最後一筆留下的狀態;講評摘要、航跡、事件跳轉仍以全部狀態 / 事件的時刻為準;
//  - 顯示用的精簡投影:conning 列與航跡圖(含畫布的繪圖指令、目標船小表)與拿完整狀態時相同(全部核心紀錄夾具的每一筆狀態)。
// 審查後修正:
//  - 倒帶前超過上限、最終不到上限的紀錄:沒有降採樣的說明,逐筆照舊(DATA-1 / MEM-1);
//  - 凍結中時刻相同的連續狀態:⏮ 退得過去(DATA-3);說明的秒數不是 0(DATA-2);
//  - 降採樣時時間軸的方向鍵走逐筆,沒有降採樣時不攔截(MEM-4);
//  - 讀取途中檔案被截短(stream 提早結束而不報錯):當作讀取失敗,原本的紀錄不動(MEM-2)。
// 核心仍在寫入的紀錄(第三十一輪;lib/record-source.ts):可以重新取得檔案的來源(檔案對話框的 handle、拖放)讀取失敗時從頭重讀 ——
//  第一次 read() 就失敗、讀到一半失敗都載得進來(與一次讀成功時相同),重讀的進度寫明是重新讀取;每次都失敗時讀到上限就結束、顯示中文說明;
//  重讀期間又選了別的檔案時舊的不蓋掉新的;一般檔案輸入的 File 不重讀(顯示中文說明,不是瀏覽器的英文錯誤)。
// 審查後修正(核心仍在寫入的紀錄):
//  - 可以重新取得檔案的來源:每一次取得檔案的當下,工具列都還沒有為這次載入改過字、也沒有排著的重繪(讀取說明要等讀到第一段才寫;
//    實測改一次字之後主執行緒要忙 5–25 ms,落在取得檔案與開啟之間時要重讀的比例是 4–6 倍);一般的檔案照舊馬上寫「讀取中…」;
//  - 在「紀錄檔」上按兩下(對話框還開著又按一下):不停用 showOpenFilePicker、不另開檔案輸入自己的對話框,選到的檔案照常載入。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { ReplayView } from '../src/components/instructor/replay-view.js';
import { decimationText, stepIntervalText } from '../src/components/instructor/replay-view.js';
import { summarize } from '../src/lib/debrief.js';
import { overlayFromHeaderScenario } from '../src/lib/plot-overlay.js';
import { parseReplay, trackPoints } from '../src/lib/record-replay.js';
import type { RecordSource } from '../src/lib/record-source.js';
import { replayDisplayState, type ReplayViewData } from '../src/lib/replay-states.js';
import { sampleState } from '../src/lib/session-record.js';
import { getShipConfig } from '../src/lib/ship-config.js';
import { defaultState } from '../src/lib/state-parser.js';
import { TrackHistory } from '../src/lib/track-history.js';
import type { OwnShipState } from '../src/types/state.js';
import { fixtureText } from './support/record-corpus.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };
type View = ReplayView & Updatable;

const E07 = fixtureText('record-E07-core.jsonl');
const E01 = fixtureText('record-E01-core.jsonl');
const enc = new TextEncoder();

async function mountView(props: Record<string, unknown> = {}): Promise<View> {
  const el = document.createElement('brm-replay-view') as View;
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

/** 元素樹連同各層 shadow DOM 的文字表示(不含 Lit 的範本註解與 <style>);兩個畫面逐字比對用。 */
function deepHtml(node: Node): string {
  if (node.nodeType === 3) return node.textContent ?? '';
  if (node.nodeType !== 1 && node.nodeType !== 11) return '';
  let inner = '';
  for (const child of Array.from(node.childNodes)) inner += deepHtml(child);
  if (node.nodeType === 11) return inner;
  const el = node as Element;
  if (el.tagName === 'STYLE') return '';
  const attrs = Array.from(el.attributes)
    .map((a) => ` ${a.name}="${a.value}"`)
    .sort()
    .join('');
  const shadow = el.shadowRoot ? `#shadow{${deepHtml(el.shadowRoot)}}` : '';
  return `<${el.tagName.toLowerCase()}${attrs}>${shadow}${inner}</${el.tagName.toLowerCase()}>`;
}

/** 等所有層的 Lit 元件更新完(巢狀元件各自排程) */
async function settle(el: Updatable): Promise<void> {
  for (let i = 0; i < 4; i++) {
    await el.updateComplete;
    const nested: Updatable[] = [];
    const walk = (root: ShadowRoot | null): void => {
      if (!root) return;
      for (const e of Array.from(root.querySelectorAll('*'))) {
        if ('updateComplete' in e) nested.push(e as Updatable);
        walk(e.shadowRoot);
      }
    };
    walk(el.shadowRoot);
    await Promise.all(nested.map((e) => e.updateComplete));
  }
}

function toolbarText(el: View): string {
  return (el.shadowRoot!.querySelector('.toolbar')!.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function dataOf(el: View): ReplayViewData | null {
  return (el as unknown as { data: ReplayViewData | null }).data;
}

/**
 * 由測試一段一段放行的假檔案(Blob.stream();每呼叫一次 next() 交出一段):看得到讀取中途的畫面。
 * failAfter:交出這麼多段之後讀取失敗(以 failWith 拒絕)。endAfter:交出這麼多段之後就結束(不報錯;讀取途中檔案被截短時 Chromium 的 stream 就是這樣)。
 */
function steppedBlob(bytes: Uint8Array, chunk: number, options: { failAfter?: number; failWith?: unknown; endAfter?: number } = {}) {
  const gates: Array<() => void> = [];
  const state = { opened: 0, delivered: 0, cancelled: false, closed: false };
  const blob = {
    size: bytes.byteLength,
    stream() {
      state.opened++;
      let off = 0;
      return new ReadableStream<Uint8Array>({
        async pull(controller) {
          await new Promise<void>((r) => gates.push(r));
          if (options.failAfter !== undefined && state.delivered >= options.failAfter) {
            controller.error(options.failWith);
            return;
          }
          if (off >= bytes.byteLength || (options.endAfter !== undefined && state.delivered >= options.endAfter)) {
            state.closed = true;
            controller.close();
            return;
          }
          controller.enqueue(bytes.subarray(off, off + chunk));
          off += chunk;
          state.delivered++;
        },
        cancel() {
          state.cancelled = true;
        },
      });
    },
    text(): never {
      throw new Error('不得把整份紀錄讀成字串');
    },
    slice(): never {
      throw new Error('不得以 slice 分次讀(核心仍在寫入的紀錄會失敗)');
    },
    arrayBuffer(): never {
      throw new Error('不得整份讀入');
    },
  };
  const tick = () => new Promise<void>((r) => setTimeout(r, 0));
  return {
    blob: blob as unknown as Blob,
    state,
    /** 放行 n 段(含結尾的那一次),並等解析與畫面更新跑完 */
    async next(n = 1): Promise<void> {
      for (let i = 0; i < n; i++) {
        for (let w = 0; w < 50 && gates.length === 0; w++) await tick();
        gates.shift()?.();
        for (let w = 0; w < 4; w++) await tick();
      }
    },
  };
}

beforeAll(async () => {
  await import('../src/components/instructor/replay-view.js');
  await import('../src/components/conning-bar.js');
  await import('../src/components/track-plot.js');
}, 60_000);

beforeEach(() => {
  // conning 列的時鐘方塊顯示電腦的時刻:固定時鐘(只換 Date,計時器照常),兩個畫面才可逐字比對(不會剛好跨過一秒)
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(Date.UTC(2026, 10, 25, 5, 0, 0)));
});

afterEach(() => {
  document.body.innerHTML = '';
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('重播檢視:檔案分段讀取', () => {
  it('loadBlob:不把整份紀錄讀成字串、整個檔案只開一次 stream();畫面與 loadText(整份文字)相同', async () => {
    const byText = await mountView();
    byText.loadText(E07, 'e07.jsonl');
    await settle(byText);
    const blob = new Blob([enc.encode(E07)]);
    const stream = vi.spyOn(blob, 'stream');
    const text = vi.spyOn(Blob.prototype, 'text');
    const slice = vi.spyOn(Blob.prototype, 'slice');
    const buffer = vi.spyOn(Blob.prototype, 'arrayBuffer');
    const byBlob = await mountView();
    const data = await byBlob.loadBlob(blob, 'e07.jsonl');
    await settle(byBlob);
    expect(data).not.toBeNull();
    expect(data).toStrictEqual(dataOf(byText));
    expect(stream).toHaveBeenCalledTimes(1);
    expect(text).not.toHaveBeenCalled();
    expect(slice).not.toHaveBeenCalled();
    expect(buffer).not.toHaveBeenCalled();
    expect(deepHtml(byBlob.shadowRoot!)).toBe(deepHtml(byText.shadowRoot!));
    expect(toolbarText(byBlob)).toContain('e07.jsonl · SimCore JSON Lines · E07_steering_failure');
    expect(toolbarText(byBlob)).toContain('30 筆狀態 · ');
    expect(byBlob.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(byBlob.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    // 同一個元件再載入別的紀錄:顯示的是新紀錄的那一筆狀態(時刻同樣在第一筆,不沿用上一份紀錄算好的)
    const bar = byBlob.shadowRoot!.querySelector('brm-conning-bar') as HTMLElement & { state: OwnShipState };
    const e07First = parseReplay(E07).states[0]!;
    const e01First = parseReplay(E01).states[0]!;
    expect(bar.state.pos).toEqual(e07First.pos);
    await byBlob.loadBlob(new Blob([enc.encode(E01)]), 'e01.jsonl');
    await byBlob.updateComplete;
    expect(e01First.pos).not.toEqual(e07First.pos);
    expect(bar.state.pos).toEqual(e01First.pos);
    expect(bar.state.heading).toBe(e01First.heading);
  }, 30_000);

  it('檔案選擇器:change 走分段讀取;開對話框前(click)清掉上一次的選擇,同一個檔案再選一次也會重讀', async () => {
    const el = await mountView();
    const input = el.shadowRoot!.querySelector<HTMLInputElement>('input.file-input')!;
    const file = new File([enc.encode(E01)], 'record-E01.jsonl');
    const text = vi.spyOn(Blob.prototype, 'text');
    Object.defineProperty(input, 'files', { configurable: true, value: [file] });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    for (let i = 0; i < 200 && !el.loaded; i++) await new Promise((r) => setTimeout(r, 5));
    await el.updateComplete;
    expect(toolbarText(el)).toContain('record-E01.jsonl · SimCore JSON Lines');
    expect(text).not.toHaveBeenCalled();
    const cleared: string[] = [];
    Object.defineProperty(input, 'value', { configurable: true, get: () => 'C:\\fakepath\\record-E01.jsonl', set: (v: string) => cleared.push(v) });
    input.dispatchEvent(new Event('click', { bubbles: true }));
    expect(cleared).toEqual(['']);
  });

  it('8 MB 以上的檔案:讀取中顯示百分比與總大小,原本載入的紀錄留到新的讀完才換掉', async () => {
    const el = await mountView();
    el.loadText(E01, 'old.jsonl');
    await el.updateComplete;
    // E07 之後接一行 9 MB 的空白(JSON Lines 的空行略過):3 MB 一段
    const pad = new Uint8Array(9 * 1024 * 1024).fill(0x20);
    const head = enc.encode(`${E07.trimEnd()}\n`);
    const bytes = new Uint8Array(head.byteLength + pad.byteLength);
    bytes.set(head, 0);
    bytes.set(pad, head.byteLength);
    const big = steppedBlob(bytes, 3 * 1024 * 1024);
    const done = el.loadBlob(big.blob, 'big.jsonl');
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .loading')!.textContent).toBe('讀取中…');
    const seen: string[] = [];
    for (let i = 0; i < 3; i++) {
      await big.next();
      await el.updateComplete;
      seen.push(el.shadowRoot!.querySelector('.toolbar .loading')?.textContent ?? '');
      // 讀取中:原本的紀錄還在、可以操作
      expect(toolbarText(el)).toContain('old.jsonl · SimCore JSON Lines');
    }
    const total = (bytes.byteLength / 1048576).toFixed(1);
    const pct = (n: number) => Math.floor(((n * 3 * 1024 * 1024) / bytes.byteLength) * 100);
    expect(seen).toEqual([1, 2, 3].map((n) => `讀取 big.jsonl… ${pct(n)}%(共 ${total} MB)`));
    expect(pct(1)).toBeGreaterThan(20);
    await big.next(3);
    expect(await done).not.toBeNull();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(toolbarText(el)).toContain('big.jsonl · SimCore JSON Lines · E07_steering_failure');
    expect(big.state.opened).toBe(1);
  }, 30_000);

  it('8 MB 以下的檔案:只顯示「讀取中…」', async () => {
    const el = await mountView();
    const small = steppedBlob(enc.encode(E07), 50_000);
    const done = el.loadBlob(small.blob, 'small.jsonl');
    await small.next(2);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .loading')!.textContent).toBe('讀取中…');
    expect(toolbarText(el)).not.toContain('%');
    await small.next(5);
    expect((await done)?.header.scenarioId).toBe('E07_steering_failure');
  });

  it('載入序號:大檔還沒讀完時又選了別的檔案 → 先前那一次在下一段中止(不再讀下去),不蓋掉後開的紀錄,不留下進度與錯誤', async () => {
    const el = await mountView();
    const slow = steppedBlob(enc.encode(E07), 20_000);
    const first = el.loadBlob(slow.blob, 'slow-E07.jsonl');
    await slow.next(2);
    expect(slow.state.delivered).toBe(2);
    // 後開的(小檔)先讀完
    const second = await el.loadBlob(new Blob([enc.encode(E01)]), 'user-E01.jsonl');
    expect(second?.header.scenarioId).toBe('E01_baseline');
    await el.updateComplete;
    expect(toolbarText(el)).toContain('user-E01.jsonl');
    // 先前那一次:下一段到達時中止讀取
    await slow.next(2);
    expect(await first).toBeNull();
    expect(slow.state.cancelled).toBe(true);
    expect(slow.state.closed).toBe(false);
    expect(slow.state.delivered).toBeLessThanOrEqual(4);
    await el.updateComplete;
    expect(dataOf(el)?.header.scenarioId).toBe('E01_baseline');
    expect(toolbarText(el)).toContain('user-E01.jsonl');
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
  }, 30_000);

  it('載入序號:讀取中途以 loadText 載入別的紀錄,或先前那一次讀取失敗 → 一樣作廢,後開的紀錄與畫面不動', async () => {
    const el = await mountView();
    const slow = steppedBlob(enc.encode(E07), 20_000);
    const first = el.loadBlob(slow.blob, 'slow-E07.jsonl');
    await slow.next(1);
    el.loadText(E01, 'text-E01.jsonl');
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    await slow.next(2);
    expect(await first).toBeNull();
    expect(slow.state.cancelled).toBe(true);
    expect(toolbarText(el)).toContain('text-E01.jsonl');
    // 資料都讀進來了、只差結尾時才被取代(之後不會再有進度回報):讀完也不套用
    const late = steppedBlob(enc.encode(E07), 1_000_000);
    const lateLoad = el.loadBlob(late.blob, 'late-E07.jsonl');
    await late.next(1);
    expect(late.state.delivered).toBe(1);
    el.loadText(E01, 'text-E01-again.jsonl');
    await late.next(1);
    expect(late.state.closed).toBe(true);
    expect(await lateLoad).toBeNull();
    await el.updateComplete;
    expect(dataOf(el)?.header.scenarioId).toBe('E01_baseline');
    expect(toolbarText(el)).toContain('text-E01-again.jsonl');
    // 先開的那一次後來讀取失敗:不顯示它的錯誤
    const failing = steppedBlob(enc.encode(E07), 20_000, { failAfter: 1, failWith: new DOMException('The requested file could not be read', 'NotReadableError') });
    const third = el.loadBlob(failing.blob, 'failing.jsonl');
    await failing.next(1);
    const fourth = await el.loadBlob(new Blob([enc.encode(E07)]), 'last-E07.jsonl');
    expect(fourth?.header.scenarioId).toBe('E07_steering_failure');
    await failing.next(2);
    expect(await third).toBeNull();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(toolbarText(el)).toContain('last-E07.jsonl');
  }, 30_000);

  it('讀取失敗:工具列顯示說明(核心仍在寫入的紀錄有中文說明),原本載入的紀錄不動;之後成功載入時清掉', async () => {
    const el = await mountView();
    el.loadText(E01, 'good.jsonl');
    await el.updateComplete;
    const before = dataOf(el);
    const live = steppedBlob(enc.encode(E07), 20_000, { failAfter: 2, failWith: new DOMException('The requested file could not be read', 'NotReadableError') });
    const p = el.loadBlob(live.blob, 'live.jsonl');
    await live.next(3);
    expect(await p).toBeNull();
    await el.updateComplete;
    const error = el.shadowRoot!.querySelector('.toolbar .error')!;
    expect(error.textContent).toBe('無法讀取「live.jsonl」:檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)');
    expect(error.getAttribute('role')).toBe('alert');
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(dataOf(el)).toBe(before);
    expect(toolbarText(el)).toContain('good.jsonl · SimCore JSON Lines');
    expect(el.shadowRoot!.querySelector('input.slider')).not.toBeNull();
    // 其他錯誤照原樣顯示
    const broken = steppedBlob(enc.encode(E07), 20_000, { failAfter: 0, failWith: new Error('讀取失敗') });
    const q = el.loadBlob(broken.blob, 'broken.jsonl');
    await broken.next(1);
    expect(await q).toBeNull();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')!.textContent).toBe('無法讀取「broken.jsonl」:無法讀取檔案(請重新選取一次;核心仍在寫入這份紀錄時也可以待練習結束後再開啟);原始錯誤:Error: 讀取失敗');
    // 讀下一份的期間不顯示先前的錯誤(顯示讀取中);成功後錯誤清掉、換成新的紀錄
    const again = steppedBlob(enc.encode(E07), 1_000_000);
    const r = el.loadBlob(again.blob, 'again.jsonl');
    await again.next(1);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(el.shadowRoot!.querySelector('.toolbar .loading')!.textContent).toBe('讀取中…');
    await again.next(1);
    expect((await r)?.header.scenarioId).toBe('E07_steering_failure');
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(toolbarText(el)).toContain('again.jsonl');
    // 失敗之後以 loadText 載入:錯誤同樣清掉
    const broken2 = steppedBlob(enc.encode(E07), 20_000, { failAfter: 0, failWith: new Error('讀取失敗') });
    const q2 = el.loadBlob(broken2.blob, 'broken2.jsonl');
    await broken2.next(1);
    expect(await q2).toBeNull();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).not.toBeNull();
    el.loadText(E01, 'text.jsonl');
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(toolbarText(el)).toContain('text.jsonl');
  }, 30_000);

  it('讀取途中檔案被截短(stream 交出一部分就結束、不報錯;審查 MEM-2):當作讀取失敗、顯示說明,原本的紀錄不動,不把半份紀錄當成整份', async () => {
    const el = await mountView();
    el.loadText(E01, 'good.jsonl');
    await el.updateComplete;
    const before = dataOf(el);
    const bytes = enc.encode(E07);
    // 檔案大小是選取當下的(完整的 E07),stream 交出 2 段(40,000 位元組)就結束
    const cut = steppedBlob(bytes, 20_000, { endAfter: 2 });
    expect(bytes.byteLength).toBeGreaterThan(60_000);
    const p = el.loadBlob(cut.blob, 'cut.jsonl');
    await cut.next(3);
    expect(await p).toBeNull();
    expect(cut.state.delivered).toBe(2);
    expect(cut.state.closed).toBe(true);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')!.textContent).toBe('無法讀取「cut.jsonl」:檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)');
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(dataOf(el)).toBe(before);
    expect(toolbarText(el)).toContain('good.jsonl · SimCore JSON Lines');
    // 對照:同樣的兩段若就是整個檔案(大小相符),照常載入(最後一行不完整時記在 errors,與核心仍在寫入的紀錄相同)
    const whole = steppedBlob(bytes.subarray(0, 40_000), 20_000);
    const q = el.loadBlob(whole.blob, 'whole.jsonl');
    await whole.next(3);
    const data = await q;
    expect(data).not.toBeNull();
    expect(data!.stateStats.total).toBeGreaterThan(0);
    expect(data!.stateStats.total).toBeLessThan(30);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(toolbarText(el)).toContain('whole.jsonl · SimCore JSON Lines');
  }, 30_000);

  it('無法辨識的檔案、空檔:與原本相同(換成「紀錄沒有狀態資料」,不丟例外)', async () => {
    const el = await mountView();
    el.loadText(E01, 'good.jsonl');
    const data = await el.loadBlob(new Blob(['this is not a record']), 'bad.txt');
    await el.updateComplete;
    expect(data?.errors).toEqual(['無法辨識的紀錄格式(需為 SimCore JSON Lines 或教官站 JSON 紀錄)']);
    expect(toolbarText(el)).toContain('bad.txt · SimCore JSON Lines · (無情境) · ? · 0 筆狀態 · 0 筆輸入 · 0 筆事件 · 0 筆標記');
    expect(el.shadowRoot!.querySelector('.hint')!.textContent).toBe('紀錄沒有狀態資料');
    expect(el.shadowRoot!.querySelector('input.slider')).toBeNull();
    expect((await el.loadBlob(new Blob([]), 'empty.jsonl'))?.states).toEqual([]);
  });
});

/** 可以重新取得檔案的來源(檔案 handle、拖放):每次 open() 依序交出 blobs 的下一個(用完之後一直是最後一個)。 */
function reopenableSource(name: string, blobs: readonly Blob[]): RecordSource & { opens: number } {
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

/** Chromium 的 stream 讀不到檔案時的錯誤(檔案在選取後被改動) */
const networkError = (): TypeError => new TypeError('network error');
/** 不等待的重讀(測試不必真的等 0–300 ms) */
const NO_WAIT = { sleep: () => Promise.resolve(), random: () => 0.5 };

describe('重播檢視:核心仍在寫入的紀錄(讀取失敗時重新取得檔案再讀)', () => {
  it('第一次 read() 就以 TypeError: network error 失敗:重新取得檔案、重讀成功 —— 畫面與一次讀成功時相同,沒有錯誤訊息', async () => {
    const byText = await mountView();
    byText.loadText(E07, 'live.jsonl');
    await settle(byText);
    const el = await mountView();
    const stale = steppedBlob(enc.encode(E07), 20_000, { failAfter: 0, failWith: networkError() });
    const fresh = steppedBlob(enc.encode(E07), 1_000_000);
    const source = reopenableSource('live.jsonl', [stale.blob, fresh.blob]);
    const p = el.loadSource(source, NO_WAIT);
    await el.updateComplete;
    // 可以重新取得的來源:讀到第一段之前工具列不更新(見下一個案例)
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    await stale.next(1); // 第一次 read() 失敗
    await el.updateComplete;
    // 開啟就失敗 → 重新取得檔案再讀:什麼都還沒有顯示過,不必說明;不是錯誤
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(source.opens).toBe(2);
    await fresh.next(1); // 重讀讀到第一段
    await el.updateComplete;
    // 重讀中:工具列寫明(不是錯誤)
    expect(el.shadowRoot!.querySelector('.toolbar .loading')!.textContent).toBe('重新讀取 live.jsonl…(檔案在讀取時被改動,第 1 次重新讀取)');
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    await fresh.next(1);
    const data = await p;
    expect(data).not.toBeNull();
    await settle(el);
    expect(source.opens).toBe(2);
    expect(stale.state).toMatchObject({ opened: 1, delivered: 0 });
    expect(fresh.state.opened).toBe(1);
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(deepHtml(el.shadowRoot!)).toBe(deepHtml(byText.shadowRoot!));
    expect(dataOf(el)).toStrictEqual(dataOf(byText));
  }, 30_000);

  it('讀到一半失敗(8 MB 以上、進度已經顯示):從頭重讀,進度寫明是重新讀取、從頭算起;載入的狀態不重複、不缺漏', async () => {
    const byText = await mountView();
    byText.loadText(E07, 'big.jsonl');
    await settle(byText);
    const el = await mountView();
    el.loadText(E01, 'old.jsonl');
    await el.updateComplete;
    // 同一份 E07 的內容分成兩半,中間夾 9 MB 的空白行:前半讀完(狀態已經進了解析器)之後才失敗
    const text = `${E07.trimEnd()}\n`;
    const cutAt = text.indexOf('\n', Math.floor(text.length / 2)) + 1;
    const head = enc.encode(text.slice(0, cutAt));
    const tail = enc.encode(text.slice(cutAt));
    const pad = new Uint8Array(9 * 1024 * 1024).fill(0x20);
    pad[pad.byteLength - 1] = 0x0a;
    const bytes = new Uint8Array(head.byteLength + pad.byteLength + tail.byteLength);
    bytes.set(head, 0);
    bytes.set(pad, head.byteLength);
    bytes.set(tail, head.byteLength + pad.byteLength);
    const piece = 3 * 1024 * 1024;
    const total = (bytes.byteLength / 1048576).toFixed(1);
    const pct = (n: number) => Math.floor((Math.min(bytes.byteLength, n * piece) / bytes.byteLength) * 100);
    const first = steppedBlob(bytes, piece, { failAfter: 2, failWith: networkError() });
    const second = steppedBlob(bytes, piece);
    const source = reopenableSource('big.jsonl', [first.blob, second.blob]);
    const p = el.loadSource(source, NO_WAIT);
    const loading = () => el.shadowRoot!.querySelector('.toolbar .loading')?.textContent ?? '';
    await first.next(2);
    await el.updateComplete;
    expect(loading()).toBe(`讀取 big.jsonl… ${pct(2)}%(共 ${total} MB)`);
    expect(pct(2)).toBeGreaterThan(50);
    await first.next(1); // 第三次 read() 失敗
    await el.updateComplete;
    // 重讀的第一段讀到之前,原本的百分比留著(取得檔案與開啟之間不改字);原本載入的紀錄不動
    expect(source.opens).toBe(2);
    expect(loading()).toBe(`讀取 big.jsonl… ${pct(2)}%(共 ${total} MB)`);
    expect(toolbarText(el)).toContain('old.jsonl · SimCore JSON Lines');
    // 進度不會無聲地跳回去:重讀的每一格百分比都寫明是重新讀取、從頭算起
    const seen: string[] = [];
    for (let i = 0; i < 4; i++) {
      await second.next(1);
      await el.updateComplete;
      seen.push(loading());
    }
    expect(seen).toEqual([1, 2, 3, 4].map((n) => `重新讀取 big.jsonl… ${pct(n)}%(共 ${total} MB)(檔案在讀取時被改動,第 1 次重新讀取)`));
    expect(pct(1)).toBeLessThan(pct(2));
    expect(pct(4)).toBe(100);
    await second.next(1);
    const data = await p;
    await settle(el);
    expect(first.state.delivered).toBe(2);
    expect(source.opens).toBe(2);
    // 前半在第一次已經解析過一遍:重讀用的是新的解析器與狀態存放,所以筆數與內容和一次讀成功時相同(不是 1.5 倍)
    expect(data!.stateStats.total).toBe(dataOf(byText)!.stateStats.total);
    expect(dataOf(el)).toStrictEqual(dataOf(byText));
    expect(loading()).toBe('');
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(toolbarText(el)).toContain('big.jsonl · SimCore JSON Lines · E07_steering_failure');
  }, 30_000);

  it('可以重新取得的來源:每一次取得檔案的當下,工具列都還沒有為這次載入改過字、也沒有排著的重繪(讀取說明要等讀到第一段才寫)', async () => {
    // 瀏覽器在 getFile() 的當下記下修改時間、開啟時核對;實測工具列改一次字,接下來主執行緒要忙 5–25 ms,
    // 落在兩者之間時要重讀的比例是 4–6 倍,重讀也常常再失敗(每次重讀前又改一次字)
    const el = await mountView();
    el.loadText(E01, 'old.jsonl');
    await settle(el);
    const loading = () => el.shadowRoot!.querySelector('.toolbar .loading')?.textContent ?? '';
    /** 元件內部的狀態(工具列的讀取說明、有沒有排著的重繪) */
    const inner = el as unknown as { loadingText: string; isUpdatePending: boolean };
    const pending = () => inner.isUpdatePending;
    const atOpen: Array<{ text: string; pending: boolean }> = [];
    const atStream: Array<{ text: string; pending: boolean }> = [];
    // 前三份一開啟就失敗,第四份讀得到;每次 open() 與 stream() 的當下記下工具列的狀態
    const stale = () => ({ size: 250_000, stream: () => (atStream.push({ text: inner.loadingText, pending: pending() }), { getReader: () => ({ read: () => Promise.reject(networkError()), cancel: () => Promise.resolve() }) }) }) as unknown as Blob;
    const good = steppedBlob(enc.encode(E07), 20_000);
    const goodStream = good.blob.stream.bind(good.blob);
    (good.blob as { stream: () => unknown }).stream = () => (atStream.push({ text: inner.loadingText, pending: pending() }), goodStream());
    const blobs = [stale(), stale(), stale(), good.blob];
    let opens = 0;
    const source: RecordSource = {
      name: 'live.jsonl',
      reopenable: true,
      open: () => {
        atOpen.push({ text: inner.loadingText, pending: pending() });
        return Promise.resolve(blobs[opens++]!);
      },
    };
    // 等待(10、30 ms 那兩格)期間讓排著的更新跑完:改過字的話這裡就畫出來了
    const p = el.loadSource(source, { random: () => 0.5, sleep: () => new Promise((r) => setTimeout(r, 2)) });
    await good.next(1);
    await el.updateComplete;
    expect(opens).toBe(4);
    const quiet = { text: '', pending: false };
    expect(atOpen).toEqual([quiet, quiet, quiet, quiet]);
    expect(atStream).toEqual([quiet, quiet, quiet, quiet]);
    // 第一段讀到之後才寫:重讀了三次
    expect(loading()).toBe('重新讀取 live.jsonl…(檔案在讀取時被改動,第 3 次重新讀取)');
    await good.next(20);
    expect((await p)?.header.scenarioId).toBe('E07_steering_failure');
    await el.updateComplete;
    expect(loading()).toBe('');
    // 沒有重讀的那一次:第一段讀到之後是「讀取中…」
    const once = steppedBlob(enc.encode(E07), 20_000);
    const texts: string[] = [];
    const q = el.loadSource({ name: 'once.jsonl', reopenable: true, open: () => (texts.push(inner.loadingText), Promise.resolve(once.blob)) }, NO_WAIT);
    await el.updateComplete;
    texts.push(loading());
    await once.next(1);
    await el.updateComplete;
    texts.push(loading());
    await once.next(20);
    await q;
    expect(texts).toEqual(['', '', '讀取中…']);
    // 一般的檔案(不能重新取得,取得與開啟之間的時間與它無關):照舊馬上寫「讀取中…」
    const plain = steppedBlob(enc.encode(E07), 20_000);
    const r = el.loadBlob(plain.blob, 'plain.jsonl');
    expect(inner.loadingText).toBe('讀取中…');
    await plain.next(20);
    await r;
  }, 30_000);

  it('每次都失敗:讀到上限就結束(不無限重試),工具列是中文說明(不是瀏覽器的英文錯誤),原本載入的紀錄不動', async () => {
    const el = await mountView();
    el.loadText(E01, 'good.jsonl');
    await el.updateComplete;
    const before = dataOf(el);
    const blobs = Array.from({ length: 12 }, () => {
      const b = { size: 250_000, stream: () => ({ getReader: () => ({ read: () => Promise.reject(networkError()), cancel: () => Promise.resolve() }) }) };
      return b as unknown as Blob;
    });
    const source = reopenableSource('live.jsonl', blobs);
    expect(await el.loadSource(source, NO_WAIT)).toBeNull();
    await el.updateComplete;
    expect(source.opens).toBe(6);
    const error = el.shadowRoot!.querySelector('.toolbar .error')!;
    expect(error.textContent).toBe('無法讀取「live.jsonl」:檔案持續被改動,重新讀取 5 次後仍讀不到(請待練習結束後再開啟,或稍後重新選取一次)');
    expect(error.textContent).not.toMatch(/network error|TypeError/);
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(dataOf(el)).toBe(before);
    expect(toolbarText(el)).toContain('good.jsonl · SimCore JSON Lines');
    // 次數可調
    const three = reopenableSource('live.jsonl', blobs);
    await el.loadSource(three, { ...NO_WAIT, maxAttempts: 3 });
    await el.updateComplete;
    expect(three.opens).toBe(3);
    expect(el.shadowRoot!.querySelector('.toolbar .error')!.textContent).toContain('重新讀取 2 次後仍讀不到');
    // 檔案不見了(重新取得時 NotFoundError):不再重試,說明是找不到檔案
    let opens = 0;
    const gone: RecordSource = { name: 'gone.jsonl', reopenable: true, open: () => (opens++ === 0 ? Promise.resolve(blobs[0]!) : Promise.reject(new DOMException('could not be found', 'NotFoundError'))) };
    await el.loadSource(gone, NO_WAIT);
    await el.updateComplete;
    expect(opens).toBe(2);
    expect(el.shadowRoot!.querySelector('.toolbar .error')!.textContent).toBe('無法讀取「gone.jsonl」:找不到檔案(已被刪除、改名或移走,或所在的磁碟已移除):請確認檔案還在之後重新選取一次');
    expect(dataOf(el)).toBe(before);
  });

  it('一般檔案輸入的 File(loadBlob):讀取失敗不重讀(實測同一個 File 再讀幾次都一樣),TypeError: network error 顯示成中文說明', async () => {
    const el = await mountView();
    el.loadText(E01, 'good.jsonl');
    await el.updateComplete;
    const before = dataOf(el);
    const stale = steppedBlob(enc.encode(E07), 20_000, { failAfter: 0, failWith: networkError() });
    const p = el.loadBlob(stale.blob, 'live.jsonl');
    await stale.next(1);
    expect(await p).toBeNull();
    await el.updateComplete;
    expect(stale.state.opened).toBe(1);
    expect(el.shadowRoot!.querySelector('.toolbar .error')!.textContent).toBe('無法讀取「live.jsonl」:檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)');
    expect(dataOf(el)).toBe(before);
  });

  it('重讀期間又選了別的檔案:舊的那一次不再重讀、不蓋掉新的紀錄,也不留下它的進度或錯誤', async () => {
    const el = await mountView();
    // (1) 等待重讀的期間被取代:不再取得檔案
    let release: () => void = () => undefined;
    const waiting = new Promise<void>((r) => (release = r));
    const stale = steppedBlob(enc.encode(E07), 20_000, { failAfter: 0, failWith: networkError() });
    const never = steppedBlob(enc.encode(E07), 20_000);
    const source = reopenableSource('slow-live.jsonl', [stale.blob, never.blob]);
    let sleeping = false;
    const first = el.loadSource(source, { delaysMs: [50], random: () => 0.5, sleep: () => ((sleeping = true), waiting) });
    await stale.next(1);
    await el.updateComplete;
    // 正在等待重讀(開啟就失敗的重讀不改字)
    expect(sleeping).toBe(true);
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    const second = await el.loadBlob(new Blob([enc.encode(E01)]), 'user-E01.jsonl');
    expect(second?.header.scenarioId).toBe('E01_baseline');
    release();
    expect(await first).toBeNull();
    await el.updateComplete;
    expect(source.opens).toBe(1);
    expect(never.state.opened).toBe(0);
    expect(dataOf(el)?.header.scenarioId).toBe('E01_baseline');
    expect(toolbarText(el)).toContain('user-E01.jsonl');
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    // (2) 重讀的那一次讀到一半時被取代:下一段中止,不套用
    const stale2 = steppedBlob(enc.encode(E07), 20_000, { failAfter: 1, failWith: networkError() });
    const retry2 = steppedBlob(enc.encode(E07), 20_000);
    const source2 = reopenableSource('slow-live-2.jsonl', [stale2.blob, retry2.blob]);
    const third = el.loadSource(source2, NO_WAIT);
    await stale2.next(2);
    await retry2.next(2);
    expect(retry2.state.delivered).toBe(2);
    el.loadText(E01, 'text-E01.jsonl');
    await retry2.next(2);
    expect(await third).toBeNull();
    expect(retry2.state.cancelled).toBe(true);
    expect(source2.opens).toBe(2);
    await el.updateComplete;
    expect(toolbarText(el)).toContain('text-E01.jsonl');
    expect(dataOf(el)?.header.scenarioId).toBe('E01_baseline');
    expect(el.shadowRoot!.querySelector('.toolbar .loading')).toBeNull();
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    // (3) 被取代之後才用完重讀次數:不顯示它的錯誤
    const dead = Array.from({ length: 8 }, () => steppedBlob(enc.encode(E07), 20_000, { failAfter: 0, failWith: networkError() }));
    const source3 = reopenableSource('dead.jsonl', dead.map((d) => d.blob));
    const fourth = el.loadSource(source3, NO_WAIT);
    await dead[0]!.next(1);
    el.loadText(E07, 'last-E07.jsonl');
    for (const d of dead.slice(1)) await d.next(1);
    expect(await fourth).toBeNull();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(toolbarText(el)).toContain('last-E07.jsonl');
    expect(source3.opens).toBeLessThanOrEqual(2);
  }, 30_000);

  it('「紀錄檔」:瀏覽器有 showOpenFilePicker 時改開它(拿到檔案 handle,讀取失敗可以重讀);用不了時退回檔案輸入自己的對話框', async () => {
    let n = 0;
    const getFile = vi.fn(() => Promise.resolve((n++ === 0 ? { name: 'live.jsonl', size: 250_000, stream: () => ({ getReader: () => ({ read: () => Promise.reject(networkError()), cancel: () => Promise.resolve() }) }) } : new File([enc.encode(E07)], 'live.jsonl')) as File));
    const show = vi.fn((_options?: unknown) => Promise.resolve([{ kind: 'file', name: 'live.jsonl', getFile }]));
    vi.stubGlobal('showOpenFilePicker', show);
    const el = await mountView();
    const input = el.shadowRoot!.querySelector<HTMLInputElement>('input.file-input')!;
    const click = new Event('click', { bubbles: true, cancelable: true });
    input.dispatchEvent(click);
    // 檔案輸入自己的對話框不開(preventDefault),改開 showOpenFilePicker
    expect(click.defaultPrevented).toBe(true);
    expect(show).toHaveBeenCalledWith({ multiple: false, id: 'simosa-brm-records', types: [{ description: '練習紀錄', accept: { 'application/json': ['.jsonl', '.json', '.txt'] } }] });
    for (let i = 0; i < 400 && !el.loaded; i++) await new Promise((r) => setTimeout(r, 5));
    await el.updateComplete;
    // 第一次取得的檔案讀不到(被改動)→ 重新 getFile() 再讀
    expect(getFile).toHaveBeenCalledTimes(2);
    expect(toolbarText(el)).toContain('live.jsonl · SimCore JSON Lines · E07_steering_failure');
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    // 使用者取消(AbortError,對話框開了一段時間):什麼都不做,下次仍用 showOpenFilePicker
    vi.useRealTimers();
    show.mockImplementationOnce(() => new Promise((_r, reject) => setTimeout(() => reject(new DOMException('The user aborted a request.', 'AbortError')), 130)));
    const before = dataOf(el);
    input.dispatchEvent(new Event('click', { bubbles: true, cancelable: true }));
    await new Promise((r) => setTimeout(r, 180));
    expect(dataOf(el)).toBe(before);
    // 對話框一開就被取消(開不起來;自動化攔截):改開檔案輸入自己的對話框,之後不再攔
    show.mockImplementationOnce(() => Promise.reject(new DOMException('Intercepted by Page.setInterceptFileChooserDialog().', 'AbortError')));
    const fallbackClicks: boolean[] = [];
    input.click = () => {
      const ev = new Event('click', { bubbles: true, cancelable: true });
      input.dispatchEvent(ev);
      fallbackClicks.push(ev.defaultPrevented);
    };
    const third = new Event('click', { bubbles: true, cancelable: true });
    input.dispatchEvent(third);
    expect(third.defaultPrevented).toBe(true);
    await new Promise((r) => setTimeout(r, 20));
    expect(fallbackClicks).toEqual([false]);
    const calls = show.mock.calls.length;
    const fourth = new Event('click', { bubbles: true, cancelable: true });
    input.dispatchEvent(fourth);
    expect(fourth.defaultPrevented).toBe(false);
    expect(show.mock.calls.length).toBe(calls);
  });

  it('在「紀錄檔」上按兩下(對話框還開著又按一下):不停用 showOpenFilePicker、不另開檔案輸入自己的對話框;選到的檔案照常載入、讀取失敗照常重讀', async () => {
    // 實測(Chromium):對話框開著時再呼叫 showOpenFilePicker 會以「NotAllowedError: … File picker already active.」拒絕
    let n = 0;
    const getFile = vi.fn(() => Promise.resolve((n++ === 0 ? { name: 'live.jsonl', size: 250_000, stream: () => ({ getReader: () => ({ read: () => Promise.reject(networkError()), cancel: () => Promise.resolve() }) }) } : new File([enc.encode(E07)], 'live.jsonl')) as File));
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
    const el = await mountView();
    const input = el.shadowRoot!.querySelector<HTMLInputElement>('input.file-input')!;
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
    for (let i = 0; i < 400 && !el.loaded; i++) await new Promise((r) => setTimeout(r, 5));
    await el.updateComplete;
    expect(getFile).toHaveBeenCalledTimes(2);
    expect(toolbarText(el)).toContain('live.jsonl · SimCore JSON Lines · E07_steering_failure');
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
    expect(fallbackClicks).toEqual([]);
    // 之後再按:仍然開 showOpenFilePicker(沒有被停用)
    const calls = show.mock.calls.length;
    const later = new Event('click', { bubbles: true, cancelable: true });
    input.dispatchEvent(later);
    expect(later.defaultPrevented).toBe(true);
    expect(show.mock.calls.length).toBe(calls + 1);
    choose([]);
    await new Promise((r) => setTimeout(r, 5));
    expect(fallbackClicks).toEqual([]);
  });

  it('把紀錄檔拖進重播檢視:與選檔案相同(有 handle 時讀取失敗可以重讀);不是檔案的拖曳不攔', async () => {
    const el = await mountView();
    let n = 0;
    const getFile = vi.fn(() => Promise.resolve((n++ === 0 ? { name: 'dropped.jsonl', size: 250_000, stream: () => ({ getReader: () => ({ read: () => Promise.reject(networkError()), cancel: () => Promise.resolve() }) }) } : new File([enc.encode(E07)], 'dropped.jsonl')) as File));
    const file = new File([enc.encode(E07)], 'dropped.jsonl');
    const dataTransfer = { types: ['Files'], dropEffect: 'none', files: [file], items: [{ kind: 'file', getAsFile: () => file, getAsFileSystemHandle: () => Promise.resolve({ kind: 'file', name: 'dropped.jsonl', getFile }) }] };
    const fire = (type: string, dt: unknown) => {
      const ev = new Event(type, { bubbles: true, cancelable: true });
      Object.defineProperty(ev, 'dataTransfer', { value: dt });
      el.dispatchEvent(ev);
      return ev;
    };
    expect(fire('dragover', dataTransfer).defaultPrevented).toBe(true);
    expect(dataTransfer.dropEffect).toBe('copy');
    expect(fire('dragover', { types: ['text/plain'], files: [], items: [] }).defaultPrevented).toBe(false);
    expect(fire('drop', { types: ['text/plain'], files: [], items: [] }).defaultPrevented).toBe(false);
    expect(fire('drop', dataTransfer).defaultPrevented).toBe(true);
    for (let i = 0; i < 400 && !el.loaded; i++) await new Promise((r) => setTimeout(r, 5));
    await el.updateComplete;
    expect(getFile).toHaveBeenCalledTimes(2);
    expect(toolbarText(el)).toContain('dropped.jsonl · SimCore JSON Lines · E07_steering_failure');
    expect(el.shadowRoot!.querySelector('.toolbar .error')).toBeNull();
  });
});

describe('重播檢視:超過上限的紀錄(降採樣)', () => {
  const state = (k: number): OwnShipState => ({ ...defaultState('FSB1'), tick: 2 * k, t: 2 * k * 0.02, heading: k % 360, rudder: k % 2 ? 35 : 0, pos: { lat: 23, lon: 120, x: 5 * k * 0.04, y: k % 2 ? 8 : 0 } });
  /** n 筆 25 Hz 的狀態;第 101 筆之前(tick 201)有一筆警告事件 */
  function record(n: number): string {
    const lines = [JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02, scenario: { id: 'E01_baseline' } })];
    for (let k = 0; k < n; k++) {
      if (k === 101) lines.push(JSON.stringify({ kind: 'event', tick: 201, event: { kind: 'hardFail', tick: 201, t: 4.02, source: 'assessment', itemId: 'grounding', seq: 1, data: { rule: 'grounding' } } }));
      lines.push(JSON.stringify({ kind: 'state', state: state(k) }));
    }
    return lines.join('\n');
  }

  it('降採樣的說明:沒有降採樣時沒有;降採樣時寫明上限、每幾筆取 1 筆、間隔、全部筆數與不受影響的項目', () => {
    const base = { total: 180000, shown: 5626, stride: 32, maxStates: 7201, interval_s: 1.28 };
    expect(decimationText({ ...base, total: 1250, shown: 1250, stride: 1, interval_s: 0.04 })).toBe('');
    expect(stepIntervalText({ ...base, stride: 1 })).toBe('');
    expect(decimationText(base)).toBe('狀態筆數超過 7,201:畫面每 32 筆取 1 筆(約每 1.28 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 180,000 筆計算');
    expect(stepIntervalText(base)).toBe('(約 1.28 秒)');
    // 間隔的寫法:浮點誤差不露出來;10 秒以上取整數
    expect(decimationText({ ...base, stride: 2, interval_s: 0.07999999999999918 })).toContain('約每 0.08 秒一筆');
    expect(decimationText({ ...base, stride: 4, interval_s: 4 })).toContain('約每 4 秒一筆');
    expect(stepIntervalText({ ...base, interval_s: 16.4 })).toBe('(約 16 秒)');
    // 間隔 0(留下的狀態時刻全部相同:整份紀錄都在凍結中)時不寫秒數,不會寫成「約每 0 秒一筆」「(約 0 秒)」(審查 DATA-2)
    expect(decimationText({ ...base, total: 36000, shown: 4501, stride: 8, interval_s: 0 })).toBe('狀態筆數超過 7,201:畫面每 8 筆取 1 筆(⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 36,000 筆計算');
    expect(stepIntervalText({ ...base, stride: 8, interval_s: 0 })).toBe('(每 8 筆)');
    // 最終筆數不到上限卻有降採樣(只會發生在時刻不依序的損壞紀錄):不寫「超過」
    expect(decimationText({ total: 6, shown: 2, stride: 8, maxStates: 8, interval_s: 0.24 })).toBe('紀錄裡狀態的時刻不是依序遞增(倒帶前超過 8 筆):畫面每 8 筆取 1 筆(約每 0.24 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 6 筆計算');
  });

  it('倒帶前超過上限、最終不到上限的紀錄(審查 DATA-1 / MEM-1):沒有降採樣的說明,逐筆與方向鍵照舊,每一筆都看得到', async () => {
    const el = await mountView({ maxStates: 64 });
    // 300 筆(超過上限 64)→ restore 回 tick 40(第 21 筆)→ 再 30 筆:最終 51 筆
    const lines = record(300).split('\n');
    lines.push(JSON.stringify({ kind: 'input', tick: 598, command: { type: 'restore', args: { tick: 40 } } }));
    for (let k = 21; k <= 50; k++) lines.push(JSON.stringify({ kind: 'state', state: state(k) }));
    const text = lines.join('\n');
    const full = parseReplay(text);
    expect(full.states).toHaveLength(51);
    const data = el.loadText(text, 'rewound.jsonl');
    await settle(el);
    expect(data.rewinds).toHaveLength(1);
    expect(data.stateStats).toMatchObject({ total: 51, shown: 51, stride: 1 });
    expect(data.states).toStrictEqual(full.states.map(sampleState));
    expect(el.shadowRoot!.querySelector('.toolbar .decimated')).toBeNull();
    expect(toolbarText(el)).toContain('· 51 筆狀態 · 1 筆輸入');
    expect(toolbarText(el)).not.toContain('超過');
    const buttons = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.timeline .btn-row button')).filter((b) => b.textContent === '⏮' || b.textContent === '⏭');
    expect(buttons.map((b) => b.title)).toEqual(['上一筆', '下一筆']);
    const bar = el.shadowRoot!.querySelector('brm-conning-bar') as HTMLElement & { state: OwnShipState };
    const seen: number[] = [];
    for (let i = 0; i < 4; i++) {
      buttons[1]!.click();
      await el.updateComplete;
      seen.push(bar.state.tick);
    }
    expect(seen).toEqual([2, 4, 6, 8]);
    // 沒有降採樣:時間軸的方向鍵不攔截(原生的一格 0.04 秒)
    const slider = el.shadowRoot!.querySelector<HTMLInputElement>('input.slider')!;
    const key = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    slider.dispatchEvent(key);
    expect(key.defaultPrevented).toBe(false);
    expect(el.time).toBeCloseTo(0.16, 9);
  });

  it('凍結中時刻相同的連續狀態(審查 DATA-3):⏮ 退得過去(原本退一筆後時刻不變,永遠停在同一段);⏭ 與沒有凍結的紀錄照舊', async () => {
    // 50 筆(t 0.04–2.00)+ 凍結 10 筆(t = 2.00,核心凍結時每 200 ms 記一筆 tick 不變的狀態)+ 50 筆(t 2.04–4.00)
    const lines = [JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02 })];
    const push = (k: number, heading: number) => lines.push(JSON.stringify({ kind: 'state', state: { ...state(k), heading } }));
    for (let k = 1; k <= 50; k++) push(k, k);
    for (let j = 0; j < 10; j++) push(50, 200 + j);
    for (let k = 51; k <= 100; k++) push(k, k);
    const text = lines.join('\n');
    for (const maxStates of [7201, 16]) {
      const el = await mountView({ maxStates });
      const data = el.loadText(text, 'frozen.jsonl');
      await el.updateComplete;
      expect(data.stateStats.total).toBe(110);
      expect(data.stateStats.stride).toBe(maxStates === 16 ? 8 : 1);
      const [prev, next] = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.timeline .btn-row button')).filter((b) => b.textContent === '⏮' || b.textContent === '⏭') as [HTMLButtonElement, HTMLButtonElement];
      const bar = el.shadowRoot!.querySelector('brm-conning-bar') as HTMLElement & { state: OwnShipState };
      el.seekTo(4);
      const times: number[] = [];
      for (let i = 0; i < 120 && el.time > data.tStart; i++) {
        prev.click();
        times.push(el.time);
      }
      // 一路退到第一筆,每按一次時刻都往前(不會停在 2.00)
      expect(el.time, `上限 ${maxStates}`).toBe(data.tStart);
      for (let i = 1; i < times.length; i++) expect(times[i]!, `上限 ${maxStates}:第 ${i + 1} 次 ⏮`).toBeLessThan(times[i - 1]!);
      if (maxStates === 7201) {
        // 沒有降採樣:每一個時刻都經過(100 個不同的時刻,由 4.00 退 99 次到 0.04),凍結的那一段顯示該時刻的最後一筆
        expect(times).toHaveLength(99);
        expect(times).toContain(2);
        el.seekTo(2.04);
        prev.click();
        await el.updateComplete;
        expect(el.time).toBe(2);
        expect(bar.state.heading).toBe(209);
        prev.click();
        await el.updateComplete;
        expect(el.time).toBeCloseTo(1.96, 9);
        expect(bar.state.tick).toBe(98);
        // ⏭ 照舊:由 1.96 到 2.00(顯示凍結段的最後一筆)再到 2.04
        next.click();
        await el.updateComplete;
        expect(el.time).toBe(2);
        expect(bar.state.heading).toBe(209);
        next.click();
        expect(el.time).toBeCloseTo(2.04, 9);
      }
      el.remove();
    }
  });

  it('降採樣時時間軸的方向鍵走逐筆(審查 MEM-4:滑桿一格 0.04 秒,留下的狀態相隔較遠,原生的方向鍵多數按下去畫面不變);其他按鍵不攔截', async () => {
    const el = await mountView({ maxStates: 64 });
    const data = el.loadText(record(1000), 'keys.jsonl');
    await settle(el);
    expect(data.stateStats).toMatchObject({ total: 1000, stride: 16 });
    const slider = el.shadowRoot!.querySelector<HTMLInputElement>('input.slider')!;
    const bar = el.shadowRoot!.querySelector('brm-conning-bar') as HTMLElement & { state: OwnShipState };
    const press = async (key: string, init: KeyboardEventInit = {}): Promise<boolean> => {
      const ev = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
      slider.dispatchEvent(ev);
      await el.updateComplete;
      return ev.defaultPrevented;
    };
    el.seekTo(10);
    await el.updateComplete;
    // t = 10 落在序號 250(tick 500)之後、下一筆留下的狀態(序號 256,tick 512)之前:顯示序號 240(tick 480)
    expect(bar.state.tick).toBe(480);
    const ticks: number[] = [];
    for (const key of ['ArrowRight', 'ArrowRight', 'ArrowUp', 'ArrowLeft', 'ArrowDown']) {
      expect(await press(key), key).toBe(true);
      ticks.push(bar.state.tick);
    }
    // 每按一次換一筆留下的狀態(16 筆 = 0.64 秒):往後 3 次、往前 2 次
    expect(ticks).toEqual([512, 544, 576, 544, 512]);
    expect(el.time).toBeCloseTo(512 * 0.02, 9);
    expect(slider.value).toBe(String(el.time));
    // 播放中按方向鍵:與 ⏮ ⏭ 相同,先停止播放
    (el.shadowRoot!.querySelector('.timeline button.play') as HTMLButtonElement).click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.timeline button.play')!.textContent).toBe('暫停');
    expect(await press('ArrowRight')).toBe(true);
    expect(el.shadowRoot!.querySelector('.timeline button.play')!.textContent).toBe('播放');
    expect(bar.state.tick).toBe(544);
    // Home / End / PageUp / PageDown 與帶修飾鍵的方向鍵不攔截(交給瀏覽器)
    const t = el.time;
    for (const key of ['Home', 'End', 'PageUp', 'PageDown', 'Tab', 'a']) expect(await press(key), key).toBe(false);
    for (const init of [{ altKey: true }, { ctrlKey: true }, { metaKey: true }]) expect(await press('ArrowLeft', init), JSON.stringify(init)).toBe(false);
    expect(el.time).toBe(t);
  });

  it('預設上限:7201 筆照舊(每筆都在);7300 筆起資訊列寫明降採樣,逐筆的步距與鈕的說明跟著變', async () => {
    const el = await mountView();
    el.loadText(record(7201), 'at-limit.jsonl');
    await el.updateComplete;
    expect(toolbarText(el)).toContain('· 7201 筆狀態 · 0 筆輸入');
    expect(el.shadowRoot!.querySelector('.toolbar .decimated')).toBeNull();
    const buttons = () => Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.timeline .btn-row button')).filter((b) => b.textContent === '⏮' || b.textContent === '⏭');
    expect(buttons().map((b) => b.title)).toEqual(['上一筆', '下一筆']);
    buttons()[1]!.click();
    expect(el.time).toBeCloseTo(0.04, 9);

    const text = record(7300);
    const full = parseReplay(text);
    const data = el.loadText(text, 'over-limit.jsonl');
    await settle(el);
    expect(data.states).toHaveLength(3651);
    expect(el.shadowRoot!.querySelector('.toolbar .decimated')!.textContent).toBe('狀態筆數超過 7,201:畫面每 2 筆取 1 筆(約每 0.08 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 7,300 筆計算');
    // 資訊列本身的寫法不變(全部狀態的筆數);說明接在資訊列之後
    expect(toolbarText(el)).toContain('· 7300 筆狀態 · 0 筆輸入 · 1 筆事件 · 0 筆標記');
    expect(el.shadowRoot!.querySelector('.toolbar .info + .decimated')).not.toBeNull();
    expect(buttons().map((b) => b.title)).toEqual(['上一筆(約 0.08 秒)', '下一筆(約 0.08 秒)']);
    // 逐筆:一次走一筆留下的狀態(0.08 秒)
    buttons()[1]!.click();
    expect(el.time).toBeCloseTo(0.08, 9);
    buttons()[1]!.click();
    expect(el.time).toBeCloseTo(0.16, 9);
    buttons()[0]!.click();
    expect(el.time).toBeCloseTo(0.08, 9);
    // 時間軸停在兩筆留下的狀態之間:顯示該時刻(含)之前最後一筆留下的狀態;由這裡逐筆到下一筆留下的狀態
    const bar = el.shadowRoot!.querySelector('brm-conning-bar') as HTMLElement & { state: OwnShipState };
    el.seekTo(0.13);
    await el.updateComplete;
    expect(el.time).toBe(0.13);
    expect(bar.state.tick).toBe(4);
    buttons()[1]!.click();
    expect(el.time).toBeCloseTo(0.16, 9);
    // 時間軸的範圍是全部狀態的第一筆到最後一筆(最後一筆序號 7299 是奇數,另外保留)
    const slider = el.shadowRoot!.querySelector<HTMLInputElement>('input.slider')!;
    expect(Number(slider.max)).toBe(full.tEnd);
    el.seekTo(full.tEnd);
    await el.updateComplete;
    expect(bar.state.tick).toBe(2 * 7299);
    // 講評摘要:全部狀態(奇數筆才有滿舵,留下來的偶數筆看不到)
    const summary = (el.shadowRoot!.querySelector('brm-debrief-summary') as HTMLElement & { summary: ReturnType<typeof summarize> }).summary;
    expect(summary).toStrictEqual(summarize(full.states));
    expect(summary.samples).toBe(7300);
    expect(summary.hardOverCount).toBe(3650);
    // 航跡:全部狀態每 1 秒一點
    const plot = el.shadowRoot!.querySelector('brm-track-plot') as HTMLElement & { history: TrackHistory; overlay: { events: Array<{ x: number; y: number; t: number }> } };
    expect(plot.history.all()).toEqual(trackPoints(full.states, 1));
    // 事件:跳到事件的時刻(4.02,不是留下的狀態的時刻);航跡圖上的位置取全部狀態中 t ≤ 4.02 的最後一筆(序號 100)
    const stream = el.shadowRoot!.querySelector('brm-event-stream') as Updatable;
    await stream.updateComplete;
    (stream.shadowRoot!.querySelector('.item[data-kind="hardFail"]') as HTMLElement).click();
    await el.updateComplete;
    expect(el.time).toBe(4.02);
    expect(bar.state.tick).toBe(200);
    expect(plot.overlay.events).toEqual([{ x: full.states[100]!.pos.x, y: full.states[100]!.pos.y, t: 4.02, severity: 'alarm', kind: 'hardFail' }]);
    // 時間軸上的事件標記也一樣
    el.seekTo(0);
    (el.shadowRoot!.querySelector('.marks .mark.assessment') as HTMLElement).click();
    expect(el.time).toBe(4.02);
  }, 60_000);

  it('maxStates 可調(之後載入的紀錄才套用):小上限下事件位置仍取全部狀態', async () => {
    const el = await mountView({ maxStates: 8 });
    const text = record(300);
    const full = parseReplay(text);
    const data = el.loadText(text, 'small-limit.jsonl');
    await el.updateComplete;
    expect(data.stateStats).toMatchObject({ total: 300, stride: 64, maxStates: 8 });
    expect(data.states.map((s) => s.tick)).toEqual([0, 128, 256, 384, 512, 598]);
    expect(el.shadowRoot!.querySelector('.toolbar .decimated')!.textContent).toBe('狀態筆數超過 8:畫面每 64 筆取 1 筆(約每 2.56 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 300 筆計算');
    el.seekTo(5);
    await el.updateComplete;
    const plot = el.shadowRoot!.querySelector('brm-track-plot') as HTMLElement & { overlay: { events: Array<{ x: number; y: number }> } };
    expect(plot.overlay.events.map((e) => [e.x, e.y])).toEqual([[full.states[100]!.pos.x, full.states[100]!.pos.y]]);
    // 由 Blob 讀也套用同一個上限
    const viaBlob = await el.loadBlob(new Blob([enc.encode(text)]), 'small-limit.jsonl');
    expect(viaBlob).toStrictEqual(data);
  });
});

describe('顯示用的精簡投影:畫面與拿完整狀態時相同', () => {
  const fixturesDir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures');
  const fixtures = readdirSync(fixturesDir).filter((f) => /^record-.*\.jsonl$/.test(f));

  /** 記錄畫布繪圖指令的 2D context(方法呼叫與屬性設定依序記下) */
  function recordingContext(log: string[]): CanvasRenderingContext2D {
    const fmt = (v: unknown): string => (typeof v === 'number' ? String(Math.round(v * 1000) / 1000) : typeof v === 'string' ? JSON.stringify(v) : Array.isArray(v) ? `[${v.map(fmt).join(',')}]` : String(v));
    return new Proxy({} as Record<string, unknown>, {
      get(target, prop) {
        if (typeof prop !== 'string') return undefined;
        if (prop in target) return target[prop];
        if (prop === 'measureText') return (text: string) => ({ width: text.length * 6 });
        return (...args: unknown[]) => {
          log.push(`${prop}(${args.map(fmt).join(',')})`);
          return undefined;
        };
      },
      set(target, prop, value) {
        if (typeof prop === 'string') {
          target[prop] = value;
          log.push(`${prop}=${fmt(value)}`);
        }
        return true;
      },
    }) as unknown as CanvasRenderingContext2D;
  }

  it('全部核心紀錄夾具的每一筆狀態:conning 列、航跡圖的畫布指令、目標船小表與提示', async () => {
    // happy-dom 的畫布沒有尺寸也沒有 2D context:給固定尺寸與記錄用的 context
    const logs = new WeakMap<object, string[]>();
    vi.spyOn(HTMLCanvasElement.prototype, 'clientWidth', 'get').mockReturnValue(800);
    vi.spyOn(HTMLCanvasElement.prototype, 'clientHeight', 'get').mockReturnValue(500);
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement) {
      let log = logs.get(this);
      if (!log) logs.set(this, (log = []));
      return recordingContext(log);
    } as never);
    const frame = () => new Promise<void>((r) => requestAnimationFrame(() => setTimeout(r, 0)));
    const make = async (tag: string, props: Record<string, unknown>) => {
      const el = document.createElement(tag) as Updatable & Record<string, unknown>;
      Object.assign(el, props);
      document.body.appendChild(el);
      await el.updateComplete;
      return el;
    };
    const first = defaultState('FSB1');
    const cfg0 = getShipConfig('FSB1');
    const barFull = await make('brm-conning-bar', { state: first, config: cfg0, issues: [], noData: false });
    const barShown = await make('brm-conning-bar', { state: first, config: cfg0, issues: [], noData: false });
    const plotFull = await make('brm-track-plot', { state: first, config: cfg0, history: new TrackHistory(1, 100000), noData: false, targetTable: true });
    const plotShown = await make('brm-track-plot', { state: first, config: cfg0, history: new TrackHistory(1, 100000), noData: false, targetTable: true });
    const drawLog = (plot: HTMLElement) => logs.get(plot.shadowRoot!.querySelector('canvas')!) ?? [];
    let compared = 0;
    let drawn = 0;
    const seen = { targets: 0, tugs: 0, berth: 0, anchors: 0, waves: 0, roll: 0, route: 0, sensors: 0 };
    for (const f of fixtures) {
      const full = parseReplay(fixtureText(f));
      const overlay = overlayFromHeaderScenario(full.header.scenario);
      const history = new TrackHistory(1, 100000);
      const states = [...full.states];
      if (f.includes('E12_anchoring')) {
        // 夾具的時間內還沒下錨:補兩筆下了錨的狀態(左錨抓住 / 走錨,右錨收著;收著的錨精簡投影不留,畫面也不畫)與感測器無效、AIS 關閉的目標
        const last = full.states[full.states.length - 1]!;
        const stowed = { side: 'starboard' as const, status: 'stowed' as const, length_m: 0, length_shackles: 0, maxLength_m: 247.5, tension_t: 0, horizontalTension_t: 0, onBottom_m: 0, holding_t: 0, brake: true, fullyOut: false };
        const out = { side: 'port' as const, length_m: 110, length_shackles: 4, maxLength_m: 247.5, tension_t: 12, horizontalTension_t: 10, onBottom_m: 60, holding_t: 40, brake: true, fullyOut: false, position: { lat: 22.6, lon: 120.25, x: last.pos.x + 90, y: last.pos.y + 40 } };
        states.push({ ...structuredClone(last), t: last.t + 10, tick: last.tick + 500, anchors: [{ ...out, status: 'holding' }, stowed], sensors: { headingValid: false, positionValid: false, depthValid: false, stwValid: false, magneticHeading: 12 } });
        states.push({ ...structuredClone(last), t: last.t + 20, tick: last.tick + 1000, anchors: [{ ...out, status: 'dragging', tension_t: 45 }, stowed], targets: last.targets.map((t) => ({ ...t, aisOn: false })) });
      }
      if (f.includes('E17b_typhoon_seakeeping')) {
        // 夾具沒有橫搖:補一筆有橫搖、船間交互作用、主機故障、艏側推故障、擱淺 / 碰撞 / 凍結旗標的狀態
        const last = full.states[full.states.length - 1]!;
        states.push({ ...structuredClone(last), t: last.t + 10, tick: last.tick + 500, roll: { angle_deg: 8.4, rate_degps: 1.5, naturalPeriod_s: 9.2, staticHeel_deg: 2 }, shipInteraction: { active: true, forceSway_t: 12, momentYaw_tm: 300, targetId: 'T1' }, engine: { state: 'failed', startsRemaining: 2, load_pct: 0 }, faults: ['bowThruster'], flags: { frozen: true, aground: true, collision: true } });
      }
      for (const s of states) {
        history.push(s.pos.x, s.pos.y, s.t);
        const shown = replayDisplayState(sampleState(s));
        const cfg = getShipConfig(s.shipId);
        Object.assign(barFull, { state: s, config: cfg });
        Object.assign(barShown, { state: shown, config: cfg });
        Object.assign(plotFull, { state: s, config: cfg, history, overlay, targets: s.targets });
        Object.assign(plotShown, { state: shown, config: cfg, history, overlay, targets: shown.targets });
        drawLog(plotFull).length = 0;
        drawLog(plotShown).length = 0;
        await settle(barFull);
        await settle(barShown);
        await settle(plotFull);
        await settle(plotShown);
        await frame();
        const label = `${f} t=${s.t}`;
        expect(deepHtml(barShown.shadowRoot!), `${label}:conning 列`).toBe(deepHtml(barFull.shadowRoot!));
        expect(deepHtml(plotShown.shadowRoot!), `${label}:航跡圖的 DOM(提示、圖例、目標船小表)`).toBe(deepHtml(plotFull.shadowRoot!));
        expect(drawLog(plotShown), `${label}:航跡圖的畫布指令`).toEqual(drawLog(plotFull));
        drawn += drawLog(plotFull).length;
        compared++;
        if (s.targets.length > 0) seen.targets++;
        if ((s.tugs?.length ?? 0) > 0) seen.tugs++;
        if ((s.berth?.lines.length ?? 0) > 0) seen.berth++;
        if ((s.anchors ?? []).some((a) => a.position !== undefined)) seen.anchors++;
        if (s.waves) seen.waves++;
        if (s.roll) seen.roll++;
        if (s.route) seen.route++;
        if (s.sensors) seen.sensors++;
      }
    }
    expect(compared).toBeGreaterThan(250);
    // 畫布確實有畫(不是兩邊都空的);夾具涵蓋目標船、拖船、纜繩、錨、浪、橫搖、航線、感測器有效性
    expect(drawn / compared).toBeGreaterThan(50);
    for (const [k, n] of Object.entries(seen)) expect(n, `夾具中有 ${k} 的狀態筆數`).toBeGreaterThan(0);
  }, 180_000);
});
