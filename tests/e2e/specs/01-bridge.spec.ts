// 操船台(index.html):載入後 5 s 內收到核心狀態幀,HDG/SOG 顯示數值(不是 --- / --.-),連線藥丸為「已連線」,無頁面錯誤;
// 文書筆電的視窗大小(1280×720、1366×768、1536×864;失電後 header 折成兩行)下每個控制項都點得到、VHF 文字欄 / DSC 不必捲動(版面;判定在 support/layout.ts)。
import { bridgeUrl } from '../support/env.ts';
import { ensureRunning, ensureScenario, expect, scenarioPath, test } from '../support/fixtures.ts';
import { BRIDGE_LAYOUT, layoutFailures, probeLayout } from '../support/layout.ts';
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

test('操船台 1280×720、1366×768 與 1536×864(失電後):控制項不被蓋住,警報面板、VHF 的 PTT / 文字欄 / DSC 與操船台不必捲動就點得到', async ({ page, env, core }) => {
  // 第二十九輪 L2:右欄(BNWAS / 警報 / VHF)原本只佔中段,這兩個視窗下警報面板被擠到高度 0(「靜音」「全部確認」「聲響」被 VHF 蓋住)、
  // VHF 的文字輸入與 DSC 鈕被下排操船台蓋住。現在右欄跨中段與下段兩列;判定與開發時的量測腳本共用 support/layout.ts 的 probeLayout。
  // 第二十九輪 L3:失電後 header 因故障徽章折成兩行,右欄少約 35 px,VHF 的文字欄 / 送出 / DISTRESS / URGENCY 被擠到視窗外(當時的判定清單沒有列它們);
  // 現在這四項是關鍵控制,高度 ≤ 800 px 時 BNWAS 改到航跡圖下方、右欄高度不足時 VHF 只縮訊息紀錄列。
  // 1366×768 是船上文書筆電;1536×864 是 1920×1080 筆電在 Windows 125% 縮放下的視窗;1280×720 是更小的筆電。
  const errors = new PageErrors(page);
  const before = (await core.nextState()).scenario?.id;
  await ensureScenario(core, 'E09_blackout');
  try {
    // 強制觸發全船失電:電羅經、舵機、主機等警報成立(每筆警報有「確認」鈕與「轉移」選單,是右欄最吃高度的情況),header 有多個故障徽章
    core.send({ type: 'triggerTimeline', args: { id: 'blackout', force: true } });
    await core.waitState((s) => Array.isArray(s['alerts']) && (s['alerts'] as unknown[]).length >= 2 && Array.isArray(s['faults']) && (s['faults'] as unknown[]).length >= 2, 20_000, '至少 2 筆警報與 2 個故障的狀態幀');
    for (const size of [{ width: 1280, height: 720 }, { width: 1366, height: 768 }, { width: 1536, height: 864 }]) {
      await test.step(`${size.width}×${size.height}`, async () => {
        await page.setViewportSize(size);
        await page.goto(bridgeUrl(env));
        await waitConnected(page);
        await expect.poll(() => page.locator('brm-alert-panel .alert').count(), { message: '警報面板列出的警報數' }).toBeGreaterThanOrEqual(2);
        await expect.poll(() => page.locator('header .alarms .alarm', { hasText: '故障' }).count(), { message: 'header 的故障徽章數' }).toBeGreaterThanOrEqual(2);
        await expect(page.locator('brm-vhf button.ptt')).toBeVisible();
        await expect(page.locator('brm-vhf .pill')).toHaveText('已連線');
        const report = await page.evaluate(probeLayout, BRIDGE_LAYOUT);
        expect(report.controls.total, '量到的控制項數(button / input / select)').toBeGreaterThanOrEqual(70);
        expect(layoutFailures(report), `操船台 ${size.width}×${size.height} 的版面問題`).toEqual([]);
        // 真實點擊(被別的面板攔截時 Playwright 會失敗):警報面板的「聲響」開關、VHF 頻道鈕、文字欄與送出、DISTRESS(第一次按只進入待確認,再按「取消」)、舵令預設鈕
        const sound = page.locator('brm-alert-panel button.sound');
        await expect(sound).toHaveAttribute('aria-pressed', 'true');
        await sound.click();
        await expect(sound).toHaveAttribute('aria-pressed', 'false');
        await sound.click();
        await expect(sound).toHaveAttribute('aria-pressed', 'true');
        await page.locator('brm-vhf .channels button', { hasText: /^12$/ }).click();
        await expect(page.locator('brm-vhf button.ptt')).toContainText('CH12');
        const text = page.locator('brm-vhf .text-row input');
        await text.click();
        await text.fill('版面測試');
        await page.locator('brm-vhf button.send').click({ trial: true });
        await text.fill('');
        const distress = page.locator('brm-vhf button.distress');
        await distress.click();
        await expect(distress).toContainText('再按一次送出 DISTRESS');
        await page.locator('brm-vhf .dsc button.cancel').click();
        await expect(distress).toHaveText('DISTRESS');
        await page.locator('brm-vhf button.urgency').click({ trial: true });
        await page.locator('brm-helm .presets button', { hasText: '正舵' }).click({ trial: true });
        await page.locator('brm-instructor .buttons button.reset').click({ trial: true });
      });
    }
    errors.expectNone();
  } finally {
    // 還原:重新載入原情境(清掉強制注入的失電;不論斷言結果),不影響後續案例
    const id = before ?? 'E09_blackout';
    core.send({ type: 'loadScenario', value: scenarioPath(id), args: { id, path: scenarioPath(id) } });
    await core.waitState((st) => st.scenario?.id === id && st.t < 5, 30_000, `重新載入 ${id} 後的狀態幀(模擬時間歸零)`);
    await ensureRunning(core);
  }
});
