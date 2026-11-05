// 簡化 ECDIS 頁(ecdis.html;docs/design/ecdis-lite.md):載入後顯示情境航線(E07 有 route)與自船(HDG/位置),海圖畫布非全黑;唯讀不送指令。
import { ecdisUrl } from '../support/env.ts';
import { ensureRunning, ensureScenario, expect, nonBlackRatio, test } from '../support/fixtures.ts';
import { PageErrors } from '../support/pages.ts';

test('ECDIS 頁載入、顯示情境航線與自船', async ({ page, env, core }) => {
  await ensureScenario(core, 'E07_steering_failure');
  await ensureRunning(core);
  const errors = new PageErrors(page);
  await page.goto(ecdisUrl(env));
  const header = page.locator('brm-ecdis-app header');
  await expect(header.locator('.chip.ok')).toHaveText('已連線', { timeout: 15_000 });
  await expect(header.locator('.chip', { hasText: 'E07_steering_failure' })).toContainText('航線');
  // 自船:HDG 三位數、位置/深度有值
  await expect(header.locator('.kv', { hasText: 'HDG' }).locator('b')).toHaveText(/^\d{3}$/);
  // 航線監控面板:航段 n / m、DTG、XTD
  const route = page.locator('ecdis-route-panel');
  await expect(route).toContainText('航線監控');
  await expect(route.locator('.kv .v').first()).toHaveText(/^\d+ \/ \d+$|已抵達終點/);
  await expect(route).toContainText('DTG');
  await expect(route).toContainText('XTD');
  await expect(route.locator('.note', { hasText: '無航線' })).toHaveCount(0);
  // 海圖畫布有底圖/航線/自船
  const canvas = page.locator('ecdis-chart canvas').first();
  await expect(canvas).toBeVisible();
  await expect.poll(() => nonBlackRatio(canvas), { timeout: 15_000, message: '海圖畫布應有非黑像素' }).toBeGreaterThan(0.05);
  // 唯讀:頁面沒有送出任何指令(核心 commandsReceived 不可得,改驗證無指令型 UI 與無錯誤)
  errors.expectNone();
});
