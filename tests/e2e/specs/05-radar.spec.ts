// 雷達 PPI 頁(radar.html;Sensors.Radar.Host X 頻):連線後 PPI 畫布不是全黑(距離圈/掃描線/回波)。雷達服務未啟動時略過。
import { radarUrl } from '../support/env.ts';
import { expect, nonBlackRatio, test } from '../support/fixtures.ts';
import { PageErrors } from '../support/pages.ts';

test('雷達頁 PPI 畫布非全黑', async ({ page, env }) => {
  test.skip(env.radarPort === 0, '雷達服務(Sensors.Radar.Host)未啟動:global-setup 找不到 dll 或埠未開');
  const errors = new PageErrors(page);
  await page.goto(radarUrl(env, 'x'));
  await expect(page.locator('brm-radar-app .chip.ok').first()).toBeVisible({ timeout: 20_000 });
  const canvas = page.locator('radar-ppi canvas').first();
  await expect(canvas).toBeVisible();
  // 等幾圈天線(影像幀)再量
  await expect.poll(() => nonBlackRatio(canvas), { timeout: 20_000, message: 'PPI 畫布應有非黑像素' }).toBeGreaterThan(0.01);
  errors.expectNone();
});
