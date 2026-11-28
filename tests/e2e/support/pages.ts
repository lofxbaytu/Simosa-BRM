// 頁面層的共用步驟:等待連線、收集頁面錯誤(pageerror / console error)、Shadow DOM 內取值。
// Playwright 的 CSS 定位器預設穿透 open shadow root(Lit 元件),故選擇器可直接寫元件內部的 class。
import fs from 'node:fs';
import { expect, type ConsoleMessage, type Locator, type Page } from '@playwright/test';
import { layoutFailures, probeConning, probeLayout, widenConningText, type ConningReport, type LayoutSpec } from './layout.ts';

/** 連線狀態藥丸(操船台/教官站 header .pill;STATUS_LABEL connected = 已連線) */
export async function waitConnected(page: Page, timeoutMs = 15_000): Promise<void> {
  await expect(page.locator('header .pill').first()).toHaveText('已連線', { timeout: timeoutMs });
}

/** 收集頁面錯誤:未捕捉例外與 console.error(排除已知無害的訊息);測試結束時以 expectNoErrors 斷言 */
export class PageErrors {
  readonly errors: string[] = [];
  constructor(page: Page, private readonly ignore: RegExp[] = []) {
    page.on('pageerror', (err) => this.push(`pageerror: ${err.message}`));
    page.on('console', (msg: ConsoleMessage) => {
      if (msg.type() === 'error') this.push(`console.error: ${msg.text()}`);
    });
  }
  private push(text: string): void {
    if (this.ignore.some((re) => re.test(text))) return;
    this.errors.push(text);
  }
  expectNone(): void {
    expect(this.errors, '頁面不應有未捕捉例外或 console.error').toEqual([]);
  }
}

/**
 * 把真實的檔案拖進頁面上的元素(CDP Input.dispatchDragEvent:瀏覽器端建立真正的拖放資料,頁面拿得到檔案 handle —— getAsFileSystemHandle;
 * 檔案對話框 showOpenFilePicker 給的是同一種物件,但原生對話框沒有辦法由 Playwright 操作)。
 * 紀錄檔的來源是 handle 時,讀取失敗可以重新取得檔案再讀(src/Instruments.Web/src/lib/record-source.ts);`setInputFiles` 走的是一般的檔案輸入,做不到。
 */
export async function dropFiles(page: Page, target: Locator, files: string[]): Promise<void> {
  const box = await target.boundingBox();
  if (!box) throw new Error('dropFiles:目標元素不在畫面上');
  const x = Math.round(box.x + box.width / 2);
  const y = Math.round(box.y + Math.min(box.height / 2, 300));
  const data = { items: [], files, dragOperationsMask: 1 };
  const cdp = await page.context().newCDPSession(page);
  try {
    for (const type of ['dragEnter', 'dragOver', 'drop'] as const) await cdp.send('Input.dispatchDragEvent', { type, x, y, data });
  } finally {
    await cdp.detach();
  }
}

/**
 * 「選取之後檔案被改動」的一般檔案輸入(不靠時機):攔下檔案輸入的 change 事件 → setInputFiles(瀏覽器建立 File)→ 先把這個 File 讀一段
 * → 對檔案附加一行 → 把 change 事件放行。頁面讀到的是「選取後被改動」的 File(Chromium 一定讀不到:TypeError: network error)。
 *
 * 先讀一段的原因:瀏覽器記下「選取當下的修改時間」不是在 setInputFiles 回來之前同步完成的 —— 少了這一步,附加偶爾搶在它前面
 * (實測這個做法 200 次裡 9 次),記下的就是附加之後的時間,頁面照常讀得到。讀成功一次代表已經記下(開啟時要拿它核對)。
 * 檔案輸入在 Shadow DOM 裡也可以(change 事件不穿出 shadow root,所以攔在檔案輸入所在的根)。
 */
export async function selectThenModify(input: Locator, file: string, appendText: string): Promise<void> {
  await input.evaluate((el) => {
    const root = el.getRootNode();
    const hold = (ev: Event): void => {
      if (ev.target !== el) return;
      ev.stopImmediatePropagation();
      root.removeEventListener('change', hold, true);
    };
    root.addEventListener('change', hold, true);
  });
  await input.setInputFiles(file);
  await input.evaluate(async (el) => {
    const reader = (el as HTMLInputElement).files![0]!.stream().getReader();
    await reader.read();
    await reader.cancel();
  });
  fs.appendFileSync(file, appendText);
  await input.evaluate((el) => el.dispatchEvent(new Event('change', { bubbles: true })));
}

/** 字串中的第一個數字(例如「S 12.3°」→ 12.3;「---」→ NaN) */
export function firstNumber(text: string | null): number {
  const m = (text ?? '').replace(/,/g, '').match(/-?\d+(\.\d+)?/);
  return m ? Number(m[0]) : Number.NaN;
}

/** 教官站情境面板的「載入」鈕(完整模式只有一個 brm-scenario-panel) */
export function scenarioLoadButton(page: Page) {
  return page.locator('brm-scenario-panel button.load');
}

/** conning 條內容比方塊高(下緣被方塊的 overflow: hidden 裁掉)的方塊;4 px 以內是字型行高的誤差 */
const tallTiles = (bar: ConningReport): string[] => bar.tiles.filter((t) => t.overflowY > 4).map((t) => `${t.tag} 內容比方塊高 ${t.overflowY} px`);

/** conning 條裡互相重疊的文字(內容比方塊高時,置中的內容往上擠到標題列;scrollHeight 看不出來) */
const overlappingText = (bar: ConningReport): string[] => bar.tiles.flatMap((t) => t.overlaps.map((o) => `${t.tag} ${o}`));

/**
 * conning 條裡沒有顯示的內容:寬或高不到 8 px 的圖形(svg)與沒有畫出來的文字。
 * 允許的例外只有一種,第三十一輪之前就有:船速方塊 125 px 以下(單列只有 13 格、視窗約 1980 px 以下)標籤只留英文縮寫
 * (STW / SOG / COG),中文部分「對水 / 對地 / 航跡向」不顯示(speed-readout.ts)。
 */
export function conningHiddenContent(bar: ConningReport): string[] {
  const out: string[] = [];
  for (const t of bar.tiles) {
    for (const g of t.graphics) if (g.width < 8 || g.height < 8) out.push(`${t.tag} 的圖形「${g.label}」沒有顯示(${g.width.toFixed(0)}×${g.height.toFixed(0)} px)`);
    for (const text of t.hiddenText) {
      if (t.tag === 'brm-speed' && t.width <= 125 && ['對水', '對地', '航跡向'].includes(text)) continue;
      out.push(`${t.tag} 的文字「${text}」沒有顯示(方塊寬 ${t.width.toFixed(1)} px)`);
    }
  }
  return out;
}

/** conning 條裡被方塊邊界切掉的文字(墨水範圍超出方塊的裁切框 1 px 以上;layout.ts 的 probeConning) */
const clippedText = (bar: ConningReport): string[] => bar.tiles.flatMap((t) => t.clipped.map((c) => `${t.tag} ${c}`));

/** expectConningSingleRow / expectConningTwoRows 的選項 */
export interface ConningExpectOptions {
  /** conning 條的格數(有給時斷言) */
  cells?: number;
  /**
   * 'page'(預設):整頁的版面判定(layout.ts 的判定 1–6)都要通過;
   * 'conning':整頁的判定只看 conning 條的水平裁切(判定 4)—— 1093×614 等關鍵控制本來就要捲動的視窗用
   */
  scope?: 'page' | 'conning';
}

/**
 * conning 條的共用判定(單列與兩列):
 *  1. 整頁的版面判定(layout.ts 的判定 1–6,含判定 4:各方塊內的元素沒有水平裁切)通過;
 *  2. conning 條排成 rows 列(cells 有給時格數相符),各方塊的內容不比方塊高、文字不互相重疊;
 *  3. 顯示的內容沒有少:各方塊的圖形都有顯示、沒有被藏起來的文字(conningHiddenContent;修正不可以靠 display: none 把內容藏起來);
 *  4. 兩列另判定:沒有被方塊邊界切掉的文字(每段文字的墨水範圍都在方塊的裁切框裡;兩列的方塊很矮,2 至 3 px 的下緣就切到字);
 *  5. 餘裕:把 conning 條所有文字的字距加大 3 %(代表比這台機器的字型更寬的字型;Windows 的字型在容器裡量不到)之後,
 *     判定 4 仍通過、內容仍不比方塊高、文字不互相重疊(兩列:也沒有被切掉的文字)—— 放不下的內容是換行或另有排法,不是被方塊邊界切掉。
 * 回傳字距還原後的量測(各方塊的寬度與數值的字級),供案例再斷言。
 */
async function expectConning(page: Page, spec: LayoutSpec, label: string, rows: 1 | 2, options: ConningExpectOptions): Promise<ConningReport> {
  const report = await page.evaluate(probeLayout, spec);
  if (options.scope === 'conning') expect(report.conning, `${label}:conning 條的水平裁切`).toEqual([]);
  else expect(layoutFailures(report), `${label} 的版面問題`).toEqual([]);
  const bar = await page.evaluate(probeConning);
  expect(bar.rows, `${label}:conning 條排成${rows === 1 ? '一' : '兩'}列`).toBe(rows);
  if (options.cells !== undefined) expect(bar.tiles.length, `${label}:conning 條的格數`).toBe(options.cells);
  expect(tallTiles(bar), `${label}:conning 條內容比方塊高的方塊`).toEqual([]);
  expect(overlappingText(bar), `${label}:conning 條互相重疊的文字`).toEqual([]);
  expect(conningHiddenContent(bar), `${label}:conning 條沒有顯示的內容`).toEqual([]);
  if (rows === 2) expect(clippedText(bar), `${label}:conning 條被方塊邊界切掉的文字`).toEqual([]);
  // 完整教官站另有一條隱藏的 conning 條(重播檢視分頁),所以是「至少」這麼多格
  expect(await page.evaluate(widenConningText, 0.03), '加大字距的方塊數').toBeGreaterThanOrEqual(bar.tiles.length);
  try {
    const wide = await page.evaluate(probeLayout, spec);
    expect(wide.conning, `${label}:字距加大 3 % 之後 conning 條的水平裁切`).toEqual([]);
    const wideBar = await page.evaluate(probeConning);
    expect(tallTiles(wideBar), `${label}:字距加大 3 % 之後 conning 條內容比方塊高的方塊`).toEqual([]);
    expect(overlappingText(wideBar), `${label}:字距加大 3 % 之後 conning 條互相重疊的文字`).toEqual([]);
    if (rows === 2) expect(clippedText(wideBar), `${label}:字距加大 3 % 之後 conning 條被方塊邊界切掉的文字`).toEqual([]);
  } finally {
    await page.evaluate(widenConningText, 0);
  }
  return bar;
}

/**
 * conning 條單列(視窗寬於 1900 px)的判定(第三十一輪;docs/design/web-layout.md 第 2.5 節):expectConning 的第 1、2、3、5 項。
 * 回傳字距還原後的量測(各方塊的寬度與數值的字級),供案例再斷言。
 */
export async function expectConningSingleRow(page: Page, spec: LayoutSpec, label: string, cells?: number): Promise<ConningReport> {
  return expectConning(page, spec, label, 1, cells === undefined ? {} : { cells });
}

/**
 * conning 條兩列(視窗 ≤ 1900 px;船上文書筆電 1366×768 與教官筆電 125 % 縮放的 1536×864 實際看到的排法)的判定
 * (第三十一輪;docs/design/web-layout.md 第 2.5 節「兩列」):expectConning 的五項都判定 —— 沒有水平裁切、內容不比方塊高、
 * 沒有被方塊邊界切掉的文字、各方塊的圖形與文字都有顯示、文字不重疊,字距加大 3 % 後仍通過。
 */
export async function expectConningTwoRows(page: Page, spec: LayoutSpec, label: string, options: ConningExpectOptions = {}): Promise<ConningReport> {
  return expectConning(page, spec, label, 2, options);
}
