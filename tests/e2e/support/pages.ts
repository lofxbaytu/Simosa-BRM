// 頁面層的共用步驟:等待連線、收集頁面錯誤(pageerror / console error)、Shadow DOM 內取值。
// Playwright 的 CSS 定位器預設穿透 open shadow root(Lit 元件),故選擇器可直接寫元件內部的 class。
import { expect, type ConsoleMessage, type Page } from '@playwright/test';

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

/** 字串中的第一個數字(例如「S 12.3°」→ 12.3;「---」→ NaN) */
export function firstNumber(text: string | null): number {
  const m = (text ?? '').replace(/,/g, '').match(/-?\d+(\.\d+)?/);
  return m ? Number(m[0]) : Number.NaN;
}

/** 教官站情境面板的「載入」鈕(完整模式只有一個 brm-scenario-panel) */
export function scenarioLoadButton(page: Page) {
  return page.locator('brm-scenario-panel button.load');
}
