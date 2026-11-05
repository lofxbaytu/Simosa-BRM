// 情境編輯器(教官站完整模式「情境編輯」分頁;規劃書第 9.1 節):套用範本 E05 → 環境進階勾選浪並改 Hs → 結構檢查通過 → YAML 預覽含 waves。
import { instructorUrl } from '../support/env.ts';
import { expect, test } from '../support/fixtures.ts';
import { PageErrors, waitConnected } from '../support/pages.ts';

test('情境編輯器:開啟範本、改 Hs、驗證通過、YAML 預覽含 waves', async ({ page, env }) => {
  const errors = new PageErrors(page);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  await page.locator('header button.view-editor').click();
  const editor = page.locator('brm-scenario-editor');
  await expect(editor).toBeVisible();
  await editor.locator('select.template').selectOption('E05_pilot_traffic');
  await editor.locator('button.apply-template').click();
  await expect(editor.locator('input.id')).toHaveValue(/^E05_pilot_traffic/);
  await expect(editor.locator('.hint', { hasText: '目前草稿' })).toContainText('範本 E05_pilot_traffic');

  const waves = editor.locator('input.waves-enabled');
  if (!(await waves.isChecked())) await waves.check();
  const hs = editor.locator('input.wv-hs');
  await expect(hs).toBeVisible();
  await hs.fill('2.5');
  await hs.press('Tab'); // change 事件(表單以 @change 寫回草稿)
  await expect(editor.locator('.issues .ok')).toContainText('結構檢查通過');
  await expect(editor.locator('.issues .issue')).toHaveCount(0);
  await expect(editor.locator('button.load')).toBeEnabled();

  await editor.locator('button.toggle-yaml').click();
  const yaml = editor.locator('pre.yaml');
  await expect(yaml).toBeVisible();
  await expect(yaml).toContainText('waves:');
  await expect(yaml).toContainText('significantHeight_m: 2.5');
  await expect(yaml).toContainText('peakPeriod_s:');
  // Hs 負值 → 驗證錯誤、載入鈕停用;改回後恢復
  await hs.fill('-1');
  await hs.press('Tab');
  await expect(editor.locator('.issues .issue').first()).toContainText('significantHeight_m');
  await expect(editor.locator('button.load')).toBeDisabled();
  await hs.fill('2.5');
  await hs.press('Tab');
  await expect(editor.locator('.issues .ok')).toContainText('結構檢查通過');
  errors.expectNone();
});
