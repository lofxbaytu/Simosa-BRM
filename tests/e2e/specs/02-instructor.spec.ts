// 教官站完整模式(instructor.html):情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復;無效內嵌情境 → 核心拒絕(scenarioLoadRejected)
// 並在面板顯示;下舵令 20°(操船台舵輪預設鈕)→ 操船台舵角指示變化;凍結/恢復(執行控制面板)→ 狀態幟 frozen 與 header「已凍結」。
import { bridgeUrl, instructorUrl } from '../support/env.ts';
import { ensureRunning, ensureScenario, expect, test } from '../support/fixtures.ts';
import { PageErrors, scenarioLoadButton, waitConnected } from '../support/pages.ts';

/** 與 tests/SimCore.Tests/ScenarioLoadRejectionTests.cs 相同的兩個結構 error:多邊形只有 2 點、hardFail 引用不存在的區域 */
const INVALID_INLINE = {
  id: 'E2E_invalid',
  seed: 3,
  ship: { id: 'FSB1', loading: 'ballast' },
  origin: { lat: 23.8, lon: 120.05 },
  initial: { position: { x: 0, y: 0 }, heading: 0, speed: 8, telegraph: 'HAH' },
  environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 30 },
  zones: [{ id: 'bad', type: 'noGo', polygon: [{ x: 0, y: 0 }, { x: 100, y: 0 }] }],
  assessment: { hardFail: [{ id: 'noGo', rule: 'zoneEntered', zoneId: 'missing' }] },
};

test('情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復', async ({ page, env, core }) => {
  const errors = new PageErrors(page);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  const items = page.locator('brm-scenario-panel .list .item');
  await expect(items.first()).toBeVisible();
  expect(await items.count(), '建置時匯入的情境數(data/scenarios/*.yaml)').toBeGreaterThanOrEqual(20);
  const e07 = items.filter({ hasText: 'E07_steering_failure' });
  await expect(e07).toHaveCount(1);
  // 清單為捲動容器,項目可能被說明區遮住:捲到可見後直接派發 click(面板以 @click 選取)
  await e07.scrollIntoViewIfNeeded();
  await e07.dispatchEvent('click');
  await expect(e07).toHaveClass(/selected/);
  const load = scenarioLoadButton(page);
  await expect(load).toBeEnabled();
  await expect(load).toHaveText('載入 E07_steering_failure');
  const switched = core.waitState((s) => s.scenario?.id === 'E07_steering_failure', 30_000, 'E07 的狀態幀');
  // 1600×1000 視窗下左欄情境面板的內容超出格列、載入鈕被時間表面板蓋住(真實點擊會被攔截):直接派發 click 給按鈕(面板以 @click 處理)
  await load.dispatchEvent('click');
  const frame = await switched;
  expect(frame.scenario?.id).toBe('E07_steering_failure');
  // 載入成功:載入鈕回復(不停在「載入中…」)、清單標示已載入、header 顯示情境 id
  await expect(load).toBeEnabled({ timeout: 15_000 });
  await expect(load).toHaveText('載入 E07_steering_failure');
  await expect(e07.locator('.tag.ok')).toHaveText('已載入');
  await expect(page.locator('header .ship')).toContainText('E07_steering_failure');
  await expect(page.locator('brm-scenario-panel .rejected')).toHaveCount(0);
  errors.expectNone();
});

test('無效內嵌情境 → 教官站顯示核心拒絕,核心狀態不變', async ({ page, env, core }) => {
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  const before = (await core.nextState()).scenario?.id;
  const rejected = core.waitEvent('scenarioLoadRejected', 15_000);
  // 與靠離泊預演/情境編輯器相同的指令形狀(loadScenario args.scenario 內嵌物件);核心以帶外事件框回報所有客戶端
  core.send({ type: 'loadScenario', args: { id: 'E2E_invalid', scenario: INVALID_INLINE } });
  const ev = await rejected;
  expect(ev['itemId']).toBe('E2E_invalid');
  const data = ev['data'] as { origin?: string; issueCount?: number; issues?: unknown[] };
  expect(data.origin).toBe('inline');
  expect(data.issueCount).toBeGreaterThanOrEqual(2);
  const box = page.locator('brm-scenario-panel .rejected');
  await expect(box).toBeVisible();
  await expect(box.locator('.title')).toContainText('核心拒絕載入 E2E_invalid');
  await expect(box.locator('li')).toHaveCount(data.issues?.length ?? 2);
  await expect(box.locator('li').first()).toContainText('polygon');
  // 核心仍為原情境;載入鈕沒有停在「載入中…」
  const after = await core.nextState();
  expect(after.scenario?.id).toBe(before);
  await expect(scenarioLoadButton(page)).not.toHaveText(/載入中/);
  await expect(scenarioLoadButton(page)).toBeEnabled();
});

test('下舵令 20° → 操船台舵角指示變化', async ({ page, env, core }) => {
  await ensureRunning(core);
  await page.goto(bridgeUrl(env));
  await waitConnected(page);
  const wheel = page.locator('brm-helm');
  const actual = page.locator('brm-rudder .readouts span', { hasText: '舵角' }).locator('.value');
  const order = page.locator('brm-rudder .readouts span', { hasText: '舵令' }).locator('.value');
  // 先回正舵,等舵角回到 0 附近
  await wheel.locator('.presets button', { hasText: '正舵' }).click();
  await core.waitState((s) => s.rudderOrder === 0 && Math.abs(s.rudder) < 0.5, 30_000, '舵回正');
  await expect(actual).toHaveText('0°');
  const ordered = core.waitState((s) => s.rudderOrder === 20, 5000, '舵令 20° 的狀態幀');
  await wheel.locator('.presets button', { hasText: 'S 20°' }).click();
  await ordered;
  await expect(order).toHaveText('S 20°');
  // 舵以舵機速率轉到 20°(交船文件 28 s 硬舵到硬舵量級),指示隨狀態幀變化
  await expect(actual).toHaveText(/^S (1[0-9]|2[0-5])°$/, { timeout: 25_000 });
  await core.waitState((s) => s.rudder >= 19, 25_000, '舵角 ≥ 19°');
  await expect(actual).toHaveText('S 20°', { timeout: 10_000 });
  // 還原正舵,不影響後續案例
  await wheel.locator('.presets button', { hasText: '正舵' }).click();
  await core.waitState((s) => s.rudderOrder === 0, 5000, '舵令回 0');
});

test('凍結(教官站)→ 事件 frozen、tick 停止;恢復 → 事件 resumed、tick 前進', async ({ page, env, core }) => {
  await ensureScenario(core, 'E07_steering_failure');
  await ensureRunning(core);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  const panel = page.locator('brm-run-control');
  const stream = page.locator('brm-event-stream');
  await expect(panel.locator('.panel-title .zh')).toContainText('執行中');
  const frozenEvent = core.waitEvent('frozen', 10_000);
  await panel.locator('button.freeze').click();
  try {
    const ev = await frozenEvent;
    expect(ev['source']).toBe('instructor');
    // 凍結中核心不推進(SimulationEngine.Step 在 Frozen 時不 Advance);心跳幀的 tick 不變:1.5 s 內 tick 不前進
    const tickAtFreeze = core.last?.tick ?? 0;
    await page.waitForTimeout(1500);
    expect(core.last?.tick).toBe(tickAtFreeze);
    // 教官站事件流列出凍結事件(事件框仍會送達)
    await expect(stream.locator('.item[data-kind="frozen"]').first()).toBeVisible();
  } finally {
    // 恢復:以 WebSocket 送 resume(下一案例驗證面板的「恢復」鈕);不論斷言結果都恢復,避免影響後續案例
    const resumedEvent = core.waitEvent('resumed', 10_000);
    core.send({ type: 'resume' });
    await resumedEvent;
  }
  const after = await core.waitState((s) => s.flags?.frozen === false, 10_000, '恢復後的狀態幀');
  await core.waitState((s) => s.tick > after.tick + 10, 10_000, 'tick 前進');
  await expect(stream.locator('.item[data-kind="resumed"]').first()).toBeVisible();
  await expect(page.locator('header .alarms .caution', { hasText: '已凍結' })).toHaveCount(0);
  await expect(panel.locator('.panel-title .zh')).toContainText('執行中');
});

test('凍結後教官站顯示「已凍結」且「恢復」鈕可用(核心凍結時每 200 ms 廣播 frozen 心跳幀)', async ({ page, env, core }) => {
  // 第二十三輪修正:RealtimeRunner 凍結時以 SimulationEngine.BroadcastNow 每 200 ms 廣播目前狀態(flags.frozen = true、tick 不變),
  // 教官站 header 顯示「已凍結」、執行控制面板「恢復」鈕可用、不會轉成「資料逾時」。
  await ensureScenario(core, 'E07_steering_failure');
  await ensureRunning(core);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  const panel = page.locator('brm-run-control');
  const frozenEvent = core.waitEvent('frozen', 10_000);
  await panel.locator('button.freeze').click();
  try {
    await frozenEvent;
    await expect(page.locator('header .alarms .caution', { hasText: '已凍結' })).toBeVisible({ timeout: 3000 });
    await expect(panel.locator('button.resume')).toBeEnabled({ timeout: 3000 });
    const resumedEvent = core.waitEvent('resumed', 10_000);
    await panel.locator('button.resume').click();
    await resumedEvent;
  } finally {
    await ensureRunning(core);
  }
});
