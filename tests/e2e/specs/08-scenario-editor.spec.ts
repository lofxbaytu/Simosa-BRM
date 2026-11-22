// 情境編輯器(教官站完整模式「情境編輯」分頁;規劃書第 9.1 節):套用範本 E05 → 環境進階勾選浪並改 Hs → 結構檢查通過 → YAML 預覽含 waves。
// 第二案例:啟用 ship.seakeeping → 沒有浪的提醒 → 加浪 → 載入核心 → 狀態幀出現 waveMotion(編輯器到核心的整條路徑)。
import { instructorUrl } from '../support/env.ts';
import { ensureRunning, expect, test } from '../support/fixtures.ts';
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

test('情境編輯器:啟用升沉/縱搖 → 沒有浪時提醒 → 加浪 → 載入核心 → 狀態幀出現 waveMotion', async ({ page, env, core }) => {
  // 整條路徑(第二十九輪 ship.seakeeping 表單):編輯器組出的情境以內嵌 loadScenario 送核心,核心輸出 state.waveMotion(升沉/縱搖)
  const errors = new PageErrors(page);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  await page.locator('header button.view-editor').click();
  const editor = page.locator('brm-scenario-editor');
  await expect(editor).toBeVisible();
  await editor.locator('select.template').selectOption('E05_pilot_traffic');
  await editor.locator('button.apply-template').click();
  await expect(editor.locator('input.id')).toHaveValue(/^E05_pilot_traffic/);
  const id = await editor.locator('input.id').inputValue();

  // 啟用升沉/縱搖;E05 沒有浪 → 區段內提醒(不是錯誤,仍可載入)
  const sk = editor.locator('input.sk-enabled');
  await sk.scrollIntoViewIfNeeded();
  await sk.check();
  await expect(editor.locator('.sk-no-waves')).toContainText('沒有浪');
  await expect(editor.locator('button.load')).toBeEnabled();
  // 勾選框不被共用樣式拉成整列寬
  expect((await sk.boundingBox())!.width, '啟用勾選框的寬度(px)').toBeLessThan(40);

  // 加浪(Hs 2.5 m;Tp 與來向用表單預設)→ 提醒消失
  const waves = editor.locator('input.waves-enabled');
  await waves.scrollIntoViewIfNeeded();
  await waves.check();
  const hs = editor.locator('input.wv-hs');
  await hs.fill('2.5');
  await hs.press('Tab');
  await expect(editor.locator('.sk-no-waves')).toHaveCount(0);
  await expect(editor.locator('.issues .issue')).toHaveCount(0);

  // 載入核心:狀態幀的情境 id 變更,且帶 waveMotion(heave_m / pitch_deg 為有限數值)
  type WithMotion = { scenario?: { id?: string }; waveMotion?: { heave_m?: number; pitch_deg?: number } };
  const withMotion = core.waitState((s) => {
    const m = (s as WithMotion).waveMotion;
    return s.scenario?.id === id && !!m && Number.isFinite(m.heave_m) && Number.isFinite(m.pitch_deg);
  }, 30_000, `情境 ${id} 且帶 waveMotion 的狀態幀`);
  await editor.locator('button.load').dispatchEvent('click');
  await ensureRunning(core);
  const frame = (await withMotion) as WithMotion;
  expect(Math.abs(frame.waveMotion!.heave_m!), '升沉量級(m;Hs 2.5 m)').toBeLessThan(5);
  expect(Math.abs(frame.waveMotion!.pitch_deg!), '縱搖量級(度)').toBeLessThan(15);
  errors.expectNone();
});
