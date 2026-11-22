// 教官站完整模式(instructor.html):情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復;無效內嵌情境 → 核心拒絕(scenarioLoadRejected)
// 並在面板顯示;下舵令 20°(操船台舵輪預設鈕)→ 操船台舵角指示變化;凍結/恢復(執行控制面板)→ 狀態幟 frozen 與 header「已凍結」;
// 1366×768、1536×864 與 1920×1080 下每個控制項都點得到、載入與執行控制不必捲動;選了最長 id 的情境(E20_comprehensive_assessment_*)時「載入」鈕
// 不撐出左欄;重播檢視載入紀錄後與簡化模式「開始練習」後(1366×768)事件串流篩選鈕、PTT 等不被蓋住或裁切(版面;判定在 support/layout.ts)。
import path from 'node:path';
import { ROOT, bridgeUrl, instructorUrl } from '../support/env.ts';
import { ensureRunning, ensureScenario, expect, test } from '../support/fixtures.ts';
import { INSTRUCTOR_LAYOUT, REPLAY_LAYOUT, SIMPLE_RUN_LAYOUT, layoutFailures, probeLayout } from '../support/layout.ts';
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
  // 清單為捲動容器:捲到可見後以真實點擊選取
  await e07.scrollIntoViewIfNeeded();
  await e07.click();
  await expect(e07).toHaveClass(/selected/);
  const load = scenarioLoadButton(page);
  await expect(load).toBeEnabled();
  await expect(load).toHaveText('載入 E07_steering_failure');
  const switched = core.waitState((s) => s.scenario?.id === 'E07_steering_failure', 30_000, 'E07 的狀態幀');
  // 真實點擊:載入鈕固定在情境面板底部、不必捲動就點得到(第二十九輪 L2 之前情境面板的內容超出格列,鈕被時間表面板蓋住,
  // 當時只能以 dispatchEvent 繞過;被別的面板攔截時這裡會失敗)
  await load.click();
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
    // 凍結中核心不推進(SimulationEngine.Step 在 Frozen 時不 Advance);心跳幀的 tick 不變:1.5 s 內 tick 不前進。
    // 基準取第一個 flags.frozen 的狀態幀:狀態幀 25 Hz(每 2 tick 一幀),凍結事件可能比凍結當下那個 tick 的幀早到,
    // 直接讀 core.last 會差 1 tick(第二十六輪在較快的機器上重現)
    const frozenState = await core.waitState((s) => s.flags?.frozen === true, 5_000, '凍結後的狀態幀');
    const tickAtFreeze = frozenState.tick;
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

test('完整教官站 1366×768、1536×864 與 1920×1080:控制項不被蓋住,載入、凍結 / 恢復 / 重設 / 結束、時間倍率不必捲動就點得到', async ({ page, env, core }) => {
  // 第二十九輪 L2:左欄四個面板原本以固定比例分列,情境面板的詳細區(簡報結構區塊)比格列高,「載入」「開始規劃時間」被時間表面板蓋住;
  // 1536×864(1920×1080 筆電在 Windows 125% 縮放)下執行控制的「結束練習」「時間倍率」「快照」被畫面錄影面板蓋住、事件串流只剩約 10 px 高。
  // 現在三欄各自捲動、執行控制固定在左欄最上方;判定與開發時的量測腳本共用 support/layout.ts 的 probeLayout。
  // 情境用 E13(拖船、錨、繫纜、泊位面板都有)。
  const errors = new PageErrors(page);
  const id = 'E13_tug_berthing_mailiao';
  await ensureRunning(core);
  try {
    for (const size of [{ width: 1366, height: 768 }, { width: 1536, height: 864 }, { width: 1920, height: 1080 }]) {
      await test.step(`${size.width}×${size.height}`, async () => {
        await page.setViewportSize(size);
        await page.goto(instructorUrl(env));
        await waitConnected(page);
        // 以真實點擊選情境並載入(教官站據此顯示時間表與確認鈕;第二個視窗時核心已是 E13,以模擬時間歸零辨識重新載入)
        const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: id });
        await expect(item).toHaveCount(1);
        await item.scrollIntoViewIfNeeded();
        await item.click();
        const load = scenarioLoadButton(page);
        await expect(load).toHaveText(`載入 ${id}`);
        const loaded = core.waitState((s) => s.scenario?.id === id && s.t < 5, 30_000, `${id} 重新載入後的狀態幀`);
        await load.click();
        await loaded;
        await expect(load).toBeEnabled({ timeout: 15_000 });
        await expect(page.locator('header .ship')).toContainText(id);
        for (const tag of ['brm-tug-panel', 'brm-anchor-panel', 'brm-mooring-panel']) await expect(page.locator(`.bottom ${tag}`)).toBeVisible();
        await expect(page.locator('brm-timeline-panel .list .item').first()).toBeVisible();
        const report = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
        expect(report.controls.total, '量到的控制項數(button / input / select)').toBeGreaterThanOrEqual(150);
        expect(layoutFailures(report), `完整教官站 ${size.width}×${size.height} 的版面問題`).toEqual([]);
        // 真實點擊(被別的面板攔截時 Playwright 會失敗):凍結 → 恢復、時間倍率 ×2 → ×1;結束練習只做點擊前檢查(不真的結束)
        const panel = page.locator('brm-run-control');
        const frozenEvent = core.waitEvent('frozen', 10_000);
        await panel.locator('button.freeze').click();
        await frozenEvent;
        await expect(panel.locator('button.resume')).toBeEnabled({ timeout: 3000 });
        const resumedEvent = core.waitEvent('resumed', 10_000);
        await panel.locator('button.resume').click();
        await resumedEvent;
        await panel.locator('.seg button', { hasText: '×2' }).click();
        await expect(panel.locator('.panel-title .zh')).toContainText('×2');
        await panel.locator('.seg button', { hasText: /^×1$/ }).click();
        await expect(panel.locator('.panel-title .zh')).toContainText('×1');
        await panel.locator('button.end').click({ trial: true });
        await panel.locator('button.reset').click({ trial: true });
        await load.click({ trial: true });
      });
    }
    errors.expectNone();
  } finally {
    // 不論斷言結果:核心恢復執行、時間倍率回 1(後面的案例各自以 ensureScenario 載入需要的情境)
    core.send({ type: 'timeScale', value: 1 });
    await ensureRunning(core);
  }
});

test('簡化教官模式簡報頁:規劃時間、同儕評估提示緊接在簡報文字之後(無多餘空白行),講評引導問題收合並標示練習後使用', async ({ page, env, core }) => {
  // 第二十九輪:情境庫每個情境都有規劃時間 / 同儕評估提示 / 講評引導問題。簡報頁的簡報文字容器用 white-space: pre-wrap,
  // 曾經連帶讓角色 / 附件與 <brm-briefing-block> 內樣板的換行被畫成空白行(清單與標題之間空出一大段,1366×768 上提示被擠到捲動範圍外)。
  const errors = new PageErrors(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env, { mode: 'simple' }));
  await waitConnected(page);
  const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: 'E09_blackout' });
  await expect(item).toHaveCount(1);
  await item.scrollIntoViewIfNeeded();
  await item.dispatchEvent('click');
  const loaded = core.waitState((s) => s.scenario?.id === 'E09_blackout', 30_000, 'E09 的狀態幀');
  await scenarioLoadButton(page).dispatchEvent('click');
  await loaded;
  try {
    const block = page.locator('.stage.briefing brm-briefing-block');
    await expect(block).toBeVisible({ timeout: 15_000 });
    await expect(block.locator('.timer .label')).toHaveText('規劃時間 10 分鐘');
    const prompts = block.locator('details.prompts');
    await expect(prompts.locator('li')).toHaveCount(3);
    await expect(prompts.locator('li').first()).toContainText('觀察');
    // 講評引導問題:收合、標示練習後使用(題目寫明故障與時刻,練習前不對學員展開)
    const questions = block.locator('details.questions');
    await expect(questions).not.toHaveAttribute('open', /.*/);
    await expect(questions.locator('summary .note')).toContainText('練習後使用');
    await expect(questions.locator('li')).toHaveCount(5);
    // 版面:標題與第一條提示之間、角色列與規劃時間之間都沒有多出的空白行
    const box = async (sel: ReturnType<typeof page.locator>) => (await sel.boundingBox())!;
    const summary = await box(prompts.locator('summary'));
    const firstLi = await box(prompts.locator('li').first());
    const lastLi = await box(prompts.locator('li').last());
    expect(firstLi.y - (summary.y + summary.height), '同儕評估提示標題與第一條之間的距離(px)').toBeLessThan(16);
    const qSummary = await box(questions.locator('summary'));
    expect(qSummary.y - (lastLi.y + lastLi.height), '最後一條提示與「講評引導問題」之間的距離(px)').toBeLessThan(24);
    const meta = await box(page.locator('.stage.briefing .brief-meta'));
    const timer = await box(block.locator('.timer'));
    expect(meta.height, '角色 / 附件兩行的高度(px)').toBeLessThan(70);
    expect(timer.y - (meta.y + meta.height), '附件列與規劃時間之間的距離(px)').toBeLessThan(24);
    // 1366×768(船上文書筆電)上三條提示不必捲動就在簡報框內
    const frame = await box(page.locator('.stage.briefing .briefing-text'));
    expect(lastLi.y + lastLi.height, '最後一條提示的底邊應在簡報框內').toBeLessThanOrEqual(frame.y + frame.height + 1);
    errors.expectNone();
  } finally {
    // 簡報期間核心凍結:還原成執行中,後面的測試各自以 ensureScenario 載入需要的情境
    await ensureRunning(core);
  }
});

test('完整教官站 1280×720 與 1366×768 選了最長 id 的情境:「載入」鈕不撐出左欄(id 換行、不截斷),詳細區有基本高度', async ({ page, env }) => {
  // 第二十九輪 L3:「載入 E20_comprehensive_assessment_kaohsiung」的 id 不可斷行,鈕寬 276 px > 左欄的鈕列 258 px,鈕右緣被右側面板蓋住、
  // 左欄出現水平捲軸(顯示捲軸時 _default 也一樣)。現在 id 在底線後換行(<wbr>),清單與詳細區只直向捲動。只選取、不載入(核心狀態不變)。
  const errors = new PageErrors(page);
  for (const size of [{ width: 1280, height: 720 }, { width: 1366, height: 768 }]) {
    for (const id of ['E20_comprehensive_assessment_kaohsiung', 'E20_comprehensive_assessment_default']) {
      await test.step(`${size.width}×${size.height} 選 ${id}`, async () => {
        await page.setViewportSize(size);
        await page.goto(instructorUrl(env));
        await waitConnected(page);
        const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: id });
        await expect(item).toHaveCount(1);
        await item.scrollIntoViewIfNeeded();
        await item.click();
        await expect(item).toHaveClass(/selected/);
        const load = scenarioLoadButton(page);
        await expect(load).toHaveText(`載入 ${id}`);
        const report = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
        expect(layoutFailures(report), `完整教官站 ${size.width}×${size.height} 選 ${id} 的版面問題`).toEqual([]);
        // 鈕與詳細區都在情境面板內、不水平溢出;詳細區至少 120 px(1366×768:面板 300 px),面板矮於 300 px 時(1280×720 為 260 px)96 px
        // (第二十九輪 L4:原本的容器查詢含等於 300,1366×768 / 1536×864 其實都只有 96–104 px)
        const geo = await page.locator('brm-scenario-panel').evaluate((host) => {
          const root = host.shadowRoot!;
          const btn = root.querySelector('button.load')!.getBoundingClientRect();
          const row = root.querySelector('.btn-row')!.getBoundingClientRect();
          const list = root.querySelector('.list') as HTMLElement;
          const detail = root.querySelector('.detail') as HTMLElement;
          return { btnRight: btn.right, rowRight: row.right, listOverflowX: list.scrollWidth - list.clientWidth, detailOverflowX: detail.scrollWidth - detail.clientWidth, detailHeight: detail.getBoundingClientRect().height };
        });
        expect(geo.btnRight, '「載入」鈕右緣不超出鈕列').toBeLessThanOrEqual(geo.rowRight + 1);
        expect(geo.listOverflowX, '情境清單不水平溢出').toBeLessThanOrEqual(1);
        expect(geo.detailOverflowX, '詳細區不水平溢出').toBeLessThanOrEqual(1);
        expect(geo.detailHeight, '詳細區高度(px)').toBeGreaterThanOrEqual(size.height >= 768 ? 119 : 95);
        await load.click({ trial: true });
      });
    }
  }
  errors.expectNone();
});

test('重播檢視載入紀錄後(1366×768):事件串流篩選鈕不被蓋住、事件串流有足夠高度,講評摘要在自己的面板內捲動', async ({ page, env }) => {
  // 第二十九輪 L3:講評摘要原本先取 40vh,事件串流只剩 38 px,六個篩選鈕被下一個面板蓋住(Playwright 點擊被 div.panel 攔截);
  // 講評摘要的長指標名稱把表格撐到約 1140 px 寬。現在右欄四個面板各有下限,講評摘要在面板內捲動、名稱換行。
  // 第二十九輪 L4:L3 讓名稱任意處換行後,名稱欄被擠到約 25 px(一字一行),表格仍有 1012 px 寬(時間欄不換行)、要水平捲動;
  // 現在名稱欄至少 6em、時間欄在「 · 」「 → 」之後換行,摘要不必水平捲動。
  const errors = new PageErrors(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env, { mode: 'replay' }));
  const record = path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E13_tug_berthing_mailiao-core.jsonl');
  await page.locator('brm-replay-view input.file-input').setInputFiles(record);
  await expect(page.locator('brm-replay-view input.slider')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('brm-replay-view brm-event-stream .list .item').first()).toBeVisible();
  const report = await page.evaluate(probeLayout, REPLAY_LAYOUT);
  expect(layoutFailures(report), '重播檢視 1366×768 的版面問題').toEqual([]);
  // 講評摘要:不水平捲動;指標名稱欄不被擠成一字一行(至少約 6em = 78 px)
  const summary = await page.locator('brm-replay-view brm-debrief-summary').evaluate((host) => ({
    overflowX: host.scrollWidth - host.clientWidth,
    thWidths: [...host.shadowRoot!.querySelectorAll('th')].map((th) => Math.round(th.getBoundingClientRect().width)),
  }));
  expect(summary.overflowX, '講評摘要的水平溢出(px)').toBeLessThanOrEqual(1);
  expect(summary.thWidths.length, '講評摘要的列數').toBeGreaterThanOrEqual(10);
  expect(Math.min(...summary.thWidths), '指標名稱欄最窄的寬度(px)').toBeGreaterThanOrEqual(70);
  // 真實點擊:篩選鈕(時間表)切換顯示 / 隱藏、播放 / 暫停
  const filter = page.locator('brm-replay-view brm-event-stream .filters button.src-timeline');
  await expect(filter).toHaveClass(/active/);
  await filter.click();
  await expect(filter).not.toHaveClass(/active/);
  await filter.click();
  await expect(filter).toHaveClass(/active/);
  const play = page.locator('brm-replay-view .timeline button.play');
  await play.click();
  await expect(play).toHaveText('暫停');
  await play.click();
  await expect(play).toHaveText('播放');
  errors.expectNone();
});

test('簡化教官模式「開始練習」後(1366×768):VHF 頻道鈕 / PTT / 音量不被裁切,PTT、凍結、結束、BRM 標記鈕不必捲動就點得到', async ({ page, env, core }) => {
  // 第二十九輪 L3:側欄原本是固定的格列,VHF 落在給學員操作紀錄的那一列(約 88 px),頻道 69–77、守聽選單、PTT、音量被 overflow: hidden 裁掉。
  // 現在側欄是直向 flex、各面板取自然高度,VHF 排在 BRM 標記之後。
  const errors = new PageErrors(page);
  const id = 'E20_comprehensive_assessment_kaohsiung';
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env, { mode: 'simple' }));
  await waitConnected(page);
  const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: id });
  await expect(item).toHaveCount(1);
  await item.scrollIntoViewIfNeeded();
  await item.click();
  const loaded = core.waitState((s) => s.scenario?.id === id, 30_000, `${id} 的狀態幀`);
  await scenarioLoadButton(page).click();
  await loaded;
  try {
    await page.locator('.stage.briefing .actions button.start').click({ timeout: 15_000 });
    await expect(page.locator('.stage.running')).toBeVisible();
    await expect(page.locator('.stage.running brm-vhf .pill')).toHaveText('已連線');
    const report = await page.evaluate(probeLayout, SIMPLE_RUN_LAYOUT);
    expect(layoutFailures(report), '簡化模式執行階段 1366×768 的版面問題').toEqual([]);
    // 真實點擊:VHF 頻道 77(第二列最後一個)、PTT(點擊前檢查)、音量
    await page.locator('.stage.running brm-vhf .channels button', { hasText: /^77$/ }).click();
    await expect(page.locator('.stage.running brm-vhf button.ptt')).toContainText('CH77');
    await page.locator('.stage.running brm-vhf .channels button', { hasText: /^16$/ }).click();
    await page.locator('.stage.running brm-vhf button.ptt').click({ trial: true });
    await page.locator('.stage.running brm-vhf .volume input').click({ trial: true });
    await page.locator('.stage.running .actions button.end').click({ trial: true });
    errors.expectNone();
  } finally {
    await ensureRunning(core);
  }
});
