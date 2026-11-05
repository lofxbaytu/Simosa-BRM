// 操船台(index.html):載入後 5 s 內收到核心狀態幀,HDG/SOG 顯示數值(不是 --- / --.-),連線藥丸為「已連線」,無頁面錯誤。
import { bridgeUrl } from '../support/env.ts';
import { expect, test } from '../support/fixtures.ts';
import { PageErrors, waitConnected } from '../support/pages.ts';

test('操船台載入並在 5 s 內顯示 HDG/SOG', async ({ page, env }) => {
  const errors = new PageErrors(page);
  await page.goto(bridgeUrl(env));
  // 5 s 自頁面載入完成起算(含 WebSocket 連線與第一幀)
  const hdg = page.locator('brm-heading .hdg .value');
  const sog = page.locator('brm-speed .row', { hasText: 'SOG' }).locator('.value');
  await expect(hdg).toHaveText(/^\d{3}$/, { timeout: 5000 });
  await expect(sog).toHaveText(/^-?\d+\.\d$/, { timeout: 5000 });
  await waitConnected(page);
  // 舵角與舵令也不是 --(核心狀態幀的 rudder / rudderOrder)
  await expect(page.locator('brm-rudder .readouts .value').first()).not.toHaveText(/--/);
  errors.expectNone();
});
