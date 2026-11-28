// 檔案拖到頁面上不接受檔案的地方時,不讓瀏覽器把整個頁面換成那個檔案(第三十一輪;lib/file-drop-guard.ts;happy-dom):
//  - 沒有人接受的檔案拖曳:dragover 取消預設動作、dropEffect 是 none(放下去沒有反應);drop 也取消(保險);
//  - 接受檔案的元件先取消了預設動作(dropEffect = copy):防護不動它;
//  - 不是檔案的拖曳(文字、滑桿)不攔;拆掉之後不再處理;
//  - 每一頁的進入點(*.html 載入的模組)都裝了防護 —— 新增頁面時漏掉會在這裡失敗。
// @vitest-environment happy-dom
import { afterEach, describe, expect, it } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { dataTransferHasFiles, installFileDropGuard } from '../src/lib/file-drop-guard.js';
import { dataTransferHasFiles as fromRecordSource } from '../src/lib/record-source.js';

const WEB_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const fileDrag = (): { types: string[]; dropEffect: string } => ({ types: ['Files'], dropEffect: 'copy' });

function fire(target: EventTarget, type: string, dataTransfer: unknown): Event {
  const ev = new Event(type, { bubbles: true, cancelable: true, composed: true });
  Object.defineProperty(ev, 'dataTransfer', { value: dataTransfer });
  target.dispatchEvent(ev);
  return ev;
}

describe('檔案拖到不接受檔案的地方', () => {
  let uninstall: (() => void) | null = null;
  afterEach(() => {
    uninstall?.();
    uninstall = null;
    document.body.replaceChildren();
  });

  it('沒有人接受:dragover 取消預設動作並把 dropEffect 設成 none,drop 也取消(頁面不會被換成那個檔案)', () => {
    uninstall = installFileDropGuard();
    const somewhere = document.body.appendChild(document.createElement('div'));
    const over = fileDrag();
    expect(fire(somewhere, 'dragover', over).defaultPrevented).toBe(true);
    expect(over.dropEffect).toBe('none');
    const drop = fileDrag();
    expect(fire(somewhere, 'drop', drop).defaultPrevented).toBe(true);
    // drop 不改 dropEffect(那時已經沒有意義)
    expect(drop.dropEffect).toBe('copy');
  });

  it('接受檔案的元件先取消了預設動作:防護不動它的 dropEffect', () => {
    uninstall = installFileDropGuard();
    const zone = document.body.appendChild(document.createElement('div'));
    const inner = zone.appendChild(document.createElement('span'));
    zone.addEventListener('dragover', (ev) => {
      ev.preventDefault();
      (ev as Event & { dataTransfer: { dropEffect: string } }).dataTransfer.dropEffect = 'copy';
    });
    zone.addEventListener('drop', (ev) => ev.preventDefault());
    const over = { types: ['Files'], dropEffect: 'none' };
    expect(fire(inner, 'dragover', over).defaultPrevented).toBe(true);
    expect(over.dropEffect).toBe('copy');
    expect(fire(inner, 'drop', fileDrag()).defaultPrevented).toBe(true);
  });

  it('不是檔案的拖曳(選取的文字、沒有 dataTransfer 的事件)不攔', () => {
    uninstall = installFileDropGuard();
    const somewhere = document.body.appendChild(document.createElement('div'));
    const text = { types: ['text/plain'], dropEffect: 'move' };
    expect(fire(somewhere, 'dragover', text).defaultPrevented).toBe(false);
    expect(text.dropEffect).toBe('move');
    expect(fire(somewhere, 'drop', text).defaultPrevented).toBe(false);
    expect(fire(somewhere, 'dragover', null).defaultPrevented).toBe(false);
    expect(fire(somewhere, 'drop', undefined).defaultPrevented).toBe(false);
  });

  it('拆掉之後不再處理;重複安裝不會出錯', () => {
    const first = installFileDropGuard();
    const second = installFileDropGuard();
    const somewhere = document.body.appendChild(document.createElement('div'));
    expect(fire(somewhere, 'dragover', fileDrag()).defaultPrevented).toBe(true);
    first();
    second();
    const over = fileDrag();
    expect(fire(somewhere, 'dragover', over).defaultPrevented).toBe(false);
    expect(over.dropEffect).toBe('copy');
  });

  it('沒有 window 的環境(傳入 undefined)什麼都不做', () => {
    expect(() => installFileDropGuard(undefined)()).not.toThrow();
  });

  it('dataTransferHasFiles:與 lib/record-source.ts 匯出的是同一個判斷', () => {
    expect(fromRecordSource).toBe(dataTransferHasFiles);
    expect(dataTransferHasFiles({ types: ['Files'] } as unknown as DataTransfer)).toBe(true);
    expect(dataTransferHasFiles({ types: ['text/uri-list', 'Files'] } as unknown as DataTransfer)).toBe(true);
    expect(dataTransferHasFiles({ types: ['text/plain'] } as unknown as DataTransfer)).toBe(false);
    expect(dataTransferHasFiles({} as unknown as DataTransfer)).toBe(false);
    expect(dataTransferHasFiles(null)).toBe(false);
  });
});

describe('每一頁的進入點都裝了防護', () => {
  const pages = readdirSync(WEB_ROOT).filter((f) => f.endsWith('.html'));

  it('找得到全部的頁面(儀器頁、教官站、講評報告、雷達、ECDIS)', () => {
    expect(pages.sort()).toEqual(['ecdis.html', 'index.html', 'instructor.html', 'radar.html', 'report.html']);
  });

  it.each(pages)('%s 載入的模組呼叫 installFileDropGuard()', (page) => {
    const htmlText = readFileSync(path.join(WEB_ROOT, page), 'utf8');
    const entries = [...htmlText.matchAll(/<script[^>]*type="module"[^>]*src="([^"]+)"/g)].map((m) => m[1]!);
    expect(entries.length, `${page} 的 <script type="module">`).toBeGreaterThan(0);
    for (const src of entries) {
      const code = readFileSync(path.join(WEB_ROOT, src.replace(/^\//, '')), 'utf8');
      expect(code, src).toMatch(/^import \{ installFileDropGuard \} from '\.\/lib\/file-drop-guard\.js';$/m);
      expect(code, src).toMatch(/^installFileDropGuard\(\);$/m);
    }
  });
});
