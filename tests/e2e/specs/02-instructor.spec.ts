// 教官站完整模式(instructor.html):情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復;無效內嵌情境 → 核心拒絕(scenarioLoadRejected)
// 並在面板顯示;下舵令 20°(操船台舵輪預設鈕)→ 操船台舵角指示變化;凍結/恢復(執行控制面板)→ 狀態幟 frozen 與 header「已凍結」;
// 1366×768、1536×864 與 1920×1080 下每個控制項都點得到、載入與執行控制不必捲動;選了最長 id 的情境(E20_comprehensive_assessment_*)時「載入」鈕
// 不撐出左欄、清單與詳細區的高度依情境面板實際高度(核心固定在有階段的 E13);重播檢視載入紀錄後(講評摘要不水平捲動、補充欄不逐字換行)
// 與簡化模式「開始練習」後(1366×768)事件串流篩選鈕、PTT 等不被蓋住或裁切(版面;判定在 support/layout.ts);
// E14(開始即繫妥、吹攏風)碰墊的反覆接觸在事件串流合併成一列(次數與核心送出的事件數相符、文字不截斷),講評報告的時間軸顯示同一列;
// 頁首的發行版本、軟體更新包版次與降版提醒(simosa-web 的 /_status/release.json;降版時警示徽章與可展開的說明,版面判定通過;
// 沒有狀態檔時不顯示、也沒有 console error;駕駛台頁不顯示)。
import fs from 'node:fs';
import path from 'node:path';
import { ROOT, bridgeUrl, instructorUrl, webBase } from '../support/env.ts';
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

test('完整教官站 1280×720 與 1366×768 選了最長 id 的情境(核心在有階段的情境):「載入」鈕不撐出左欄(id 換行、不截斷),詳細區與清單有基本高度', async ({ page, env, core }) => {
  // 第二十九輪 L3:「載入 E20_comprehensive_assessment_kaohsiung」的 id 不可斷行,鈕寬 276 px > 左欄的鈕列 258 px,鈕右緣被右側面板蓋住、
  // 左欄出現水平捲軸(顯示捲軸時 _default 也一樣)。現在 id 在底線後換行(<wbr>),清單與詳細區只直向捲動。只選取、不載入(核心情境不變)。
  // 第二十九輪 L5:詳細區的高度取決於情境面板的高度,面板高度又取決於 header(核心在有階段的情境時多一個「階段 waiting」徽章,
  // 1366×768 的面板由 300 變成 297.6 px;L4 的門檻 300 px 讓詳細區退回 96 px)。原本的斷言只在前一個案例剛好載入沒有階段的情境時才通過;
  // 現在案例內固定核心情境(E13 跑到進入階段),斷言依面板實際高度:≥ 294 px 時清單 ≥ 80、詳細區 ≥ 120,否則 64 / 96。
  const errors = new PageErrors(page);
  type Phased = { scenario?: { phase?: string } };
  const inPhase = (s: unknown): boolean => {
    const phase = (s as Phased).scenario?.phase;
    return !!phase && phase !== 'normal';
  };
  await ensureScenario(core, 'E13_tug_berthing_mailiao');
  await ensureRunning(core);
  if (!inPhase(await core.nextState())) {
    // E13 在 t ≥ 30 s 進入「等待」階段;以 4 倍速跑到
    core.send({ type: 'timeScale', value: 4 });
    try {
      await core.waitState((s) => inPhase(s), 30_000, 'E13 進入有階段的狀態(scenario.phase 不是 normal)');
    } finally {
      core.send({ type: 'timeScale', value: 1 });
    }
  }
  for (const size of [{ width: 1280, height: 720 }, { width: 1366, height: 768 }]) {
    for (const id of ['E20_comprehensive_assessment_kaohsiung', 'E20_comprehensive_assessment_default']) {
      await test.step(`${size.width}×${size.height} 選 ${id}`, async () => {
        await page.setViewportSize(size);
        await page.goto(instructorUrl(env));
        await waitConnected(page);
        await expect(page.locator('header .phase'), 'header 的階段徽章').toBeVisible();
        const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: id });
        await expect(item).toHaveCount(1);
        await item.scrollIntoViewIfNeeded();
        await item.click();
        await expect(item).toHaveClass(/selected/);
        const load = scenarioLoadButton(page);
        await expect(load).toHaveText(`載入 ${id}`);
        const report = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
        expect(layoutFailures(report), `完整教官站 ${size.width}×${size.height} 選 ${id} 的版面問題`).toEqual([]);
        // 鈕與詳細區都在情境面板內、不水平溢出;清單與詳細區的高度依面板實際高度(scenario-panel.ts 的 @container scenario (height < 294px))
        const geo = await page.locator('brm-scenario-panel').evaluate((host) => {
          const root = host.shadowRoot!;
          const btn = root.querySelector('button.load')!.getBoundingClientRect();
          const row = root.querySelector('.btn-row')!.getBoundingClientRect();
          const list = root.querySelector('.list') as HTMLElement;
          const detail = root.querySelector('.detail') as HTMLElement;
          return {
            panelHeight: host.getBoundingClientRect().height,
            btnRight: btn.right,
            rowRight: row.right,
            listOverflowX: list.scrollWidth - list.clientWidth,
            detailOverflowX: detail.scrollWidth - detail.clientWidth,
            listHeight: list.getBoundingClientRect().height,
            detailHeight: detail.getBoundingClientRect().height,
          };
        });
        expect(geo.btnRight, '「載入」鈕右緣不超出鈕列').toBeLessThanOrEqual(geo.rowRight + 1);
        expect(geo.listOverflowX, '情境清單不水平溢出').toBeLessThanOrEqual(1);
        expect(geo.detailOverflowX, '詳細區不水平溢出').toBeLessThanOrEqual(1);
        const tall = geo.panelHeight >= 294;
        expect(geo.detailHeight, `詳細區高度(px;面板 ${geo.panelHeight.toFixed(1)} px)`).toBeGreaterThanOrEqual(tall ? 119 : 95);
        expect(geo.listHeight, `清單高度(px;面板 ${geo.panelHeight.toFixed(1)} px)`).toBeGreaterThanOrEqual(tall ? 79 : 63);
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
  // 第二十九輪 L5:補充欄(td.src;例:總覽「車鐘變更次數」的「轉速令 0 · 側推 0」、「最大 ROT」的「超限 00:00」)原本只剩約 32 px、
  // 一字一行;窄面板時移到名稱下方另起一列(與表格同寬),不逐字換行
  const summary = await page.locator('brm-replay-view brm-debrief-summary').evaluate((host) => {
    const root = host.shadowRoot!;
    const srcs = [...root.querySelectorAll('td.src')].filter((td) => (td.textContent ?? '').trim() !== '');
    return {
      overflowX: host.scrollWidth - host.clientWidth,
      thWidths: [...root.querySelectorAll('th')].map((th) => Math.round(th.getBoundingClientRect().width)),
      srcs: srcs.map((td) => {
        const cs = getComputedStyle(td);
        const lineHeight = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.25;
        const content = td.getBoundingClientRect().height - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom);
        return {
          text: (td.textContent ?? '').trim().slice(0, 24),
          width: Math.round(td.getBoundingClientRect().width),
          tableWidth: Math.round(td.closest('table')!.getBoundingClientRect().width),
          lines: Math.round(content / lineHeight),
        };
      }),
    };
  });
  expect(summary.overflowX, '講評摘要的水平溢出(px)').toBeLessThanOrEqual(1);
  expect(summary.thWidths.length, '講評摘要的列數').toBeGreaterThanOrEqual(10);
  expect(Math.min(...summary.thWidths), '指標名稱欄最窄的寬度(px)').toBeGreaterThanOrEqual(70);
  expect(summary.srcs.length, '有補充內容的列數').toBeGreaterThanOrEqual(3);
  for (const s of summary.srcs) {
    expect(s.width, `補充欄「${s.text}」的寬度(px;表格 ${s.tableWidth} px)`).toBeGreaterThanOrEqual(s.tableWidth - 2);
    expect(s.lines, `補充欄「${s.text}」的行數`).toBeLessThanOrEqual(2);
  }
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

test('E14 繫泊中碰墊的反覆接觸:事件串流合併成一列(次數 = 核心送出的事件數、文字不截斷、可篩選),講評報告的時間軸顯示同一列', async ({ page, env, core }) => {
  // 第三十輪 ENG-C9:核心的碰墊接觸沒有滯後,繫泊中每次擺盪都發一個 fenderContact(吹攏風 25 kn 約 16 筆/min),2 小時填滿事件串流的
  // 2000 列上限。教官站把同一個碰墊門檻(120 模擬秒)內的低速接觸合併成一列(lib/event-merge.ts)。E14 開始即繫妥(西風 18 kn 吹攏)、
  // 不操作:四個碰墊在 t = 0.04 s 各一筆,之後 S03 在 16.9、38.8、49.8 s 各再碰一次(固定種子,不操作時每次相同;實際次數以測試端
  // WebSocket 收到的事件框為準,不寫死)。
  const errors = new PageErrors(page);
  const id = 'E14_unberthing_no_tug';
  type Contact = { t: number; data: { fenderId: string; normalSpeed_mps: number; overload: boolean } };
  // 先確定核心在沒有泊位的情境(E07):之後測試端收到的 fenderContact 都是 E14 載入後的
  await ensureScenario(core, 'E07_steering_failure');
  await ensureRunning(core);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: id });
  await expect(item).toHaveCount(1);
  await item.scrollIntoViewIfNeeded();
  await item.click();
  const load = scenarioLoadButton(page);
  await expect(load).toHaveText(`載入 ${id}`);
  // 測試端自己數核心送出的 fenderContact(測試端另一條連到同一個核心的狀態 WebSocket);載入前先開始收,載入後第一筆在 t = 0.04 s
  const collected = core.collectEvents('fenderContact');
  const loaded = core.waitState((s) => s.scenario?.id === id && s.t < 5, 30_000, `${id} 載入後的狀態幀`);
  await load.click();
  await loaded;
  const stream = page.locator('.full brm-event-stream');
  try {
    // 以 4 倍速跑到 S03 有 4 筆接觸(第一次 + 3 次反覆接觸,約 t = 50 s),然後凍結:之後不再有新事件,頁面與測試端可以逐筆對照
    core.send({ type: 'timeScale', value: 4 });
    const received = (fender: string): number => (collected.events as unknown as Contact[]).filter((e) => e.data.fenderId === fender).length;
    // S03 第二次接觸(合併列建立:16.9 s)之後、之後的併入(38.8、49.8 s)之前送一筆教官指令(倍率 4 → 4:只產生 timeScaleChanged 事件)
    await expect.poll(() => received('S03'), { timeout: 60_000, message: 'S03 的 fenderContact 事件數' }).toBeGreaterThanOrEqual(2);
    const marked = core.waitEvent('timeScaleChanged', 10_000);
    core.send({ type: 'timeScale', value: 4 });
    const markerEvent = await marked;
    await expect.poll(() => received('S03'), { timeout: 60_000, message: 'S03 的 fenderContact 事件數' }).toBeGreaterThanOrEqual(4);
    expect((collected.events as unknown as Contact[]).filter((e) => e.data.fenderId === 'S03' && e.t > Number(markerEvent['t'])).length, '教官事件之後 S03 還有併入的接觸').toBeGreaterThanOrEqual(1);
    const frozen = core.waitEvent('frozen', 10_000);
    core.send({ type: 'freeze' });
    await frozen;
    const frozenState = await core.waitState((s) => s.flags?.frozen === true, 5_000, '凍結後的狀態幀');
    await page.waitForTimeout(500);
    collected.stop();
    const contacts = (collected.events as unknown as Contact[]).filter((e) => e.t <= frozenState.t + 0.02);
    const fenders = [...new Set(contacts.map((c) => c.data.fenderId))].sort();
    expect(fenders, '開始即繫妥:四個碰墊都有接觸').toEqual(['S03', 'S04', 'S05', 'S06']);
    // 前提(合併規則的條件):都未過載、法向速度不超過 0.15 m/s、同一個碰墊相鄰兩筆相隔不超過 120 s
    for (const f of fenders) {
      const list = contacts.filter((c) => c.data.fenderId === f);
      expect(list.every((c) => !c.data.overload && c.data.normalSpeed_mps <= 0.15), `${f} 的接觸都是低速、未過載`).toBe(true);
      expect(list.every((c, i) => i === 0 || c.t - list[i - 1]!.t <= 120), `${f} 相鄰兩筆接觸相隔不超過 120 s`).toBe(true);
    }
    // 每個碰墊:第一次接觸一列;之後的 n − 1 筆併成一列(只有一筆時就是一般的一列,兩筆以上帶次數)
    let mergedAway = 0;
    for (const f of fenders) {
      const n = contacts.filter((c) => c.data.fenderId === f).length;
      const rows = stream.locator(`.list .item[data-kind="fenderContact"][data-fender="${f}"]`);
      await expect(rows, `${f}:核心送出 ${n} 筆接觸`).toHaveCount(Math.min(n, 2));
      const merged = stream.locator(`.list .item.merged[data-fender="${f}"]`);
      if (n >= 3) {
        await expect(merged).toHaveCount(1);
        await expect(merged).toHaveAttribute('data-merged-count', String(n - 1));
        await expect(merged.locator('.text')).toContainText(`碰墊 ${f} 反覆接觸 ×${n - 1}(`);
        mergedAway += n - 2;
      } else await expect(merged).toHaveCount(0);
    }
    expect(mergedAway, '至少一個碰墊有合併列(S03)').toBeGreaterThanOrEqual(2);
    // 合併列:最新在上(S03 的合併列比它的第一次接觸早出現在清單裡)、文字完整(不以 … 截斷、沒有被裁掉)
    const s03 = stream.locator('.list .item.merged[data-fender="S03"]');
    const text = await s03.locator('.text').evaluate((el) => {
      const cs = getComputedStyle(el);
      const row = el.closest('.item')!.getBoundingClientRect();
      const box = el.getBoundingClientRect();
      const list = el.closest('.list')!.getBoundingClientRect();
      return { content: (el.textContent ?? '').trim(), overflowX: el.scrollWidth - el.clientWidth, overflowY: el.scrollHeight - el.clientHeight, textOverflow: cs.textOverflow, whiteSpace: cs.whiteSpace, inRow: box.bottom <= row.bottom + 1 && box.right <= row.right + 1, inList: row.right <= list.right + 1 };
    });
    expect(text.content).toMatch(/^碰墊 S03 反覆接觸 ×\d+\(\d\d:\d\d–\d\d:\d\d\)最大 v \d\.\d\d m\/s、能量 \d+ kN·m\(額定 \d+\)、壓縮 \d+ %$/);
    expect(text.textOverflow, '合併列文字不以 … 截斷').not.toBe('ellipsis');
    expect(text.whiteSpace, '合併列文字可換行').not.toBe('nowrap');
    expect(text.overflowX, '合併列文字的水平溢出(px)').toBeLessThanOrEqual(1);
    expect(text.overflowY, '合併列文字的垂直溢出(px)').toBeLessThanOrEqual(1);
    expect(text.inRow && text.inList, '合併列文字落在自己的列與清單內').toBe(true);
    const order = await stream.locator('.list .item[data-fender="S03"]').evaluateAll((els) => els.map((el) => el.classList.contains('merged')));
    expect(order, 'S03:合併列(16.9 s 建立)在第一次接觸(0.04 s)之上').toEqual([true, false]);
    // 合併列留在它建立時的位置(時間欄 = 第一次接觸的時刻),之後併入只在原地更新 —— 不跳到最上面(審查 BC-rBC-5:持續擺盪時每併入一次
    // 就移到最上面的話,清單可見的部分一直被進行中的合併列占著)。測試端在 S03 的合併列建立之後、最後一次併入之前送了一筆教官指令
    // (下面的 marker);它的事件必須排在合併列之上,凍結事件(最後發生)在最上面
    const firstRepeat = contacts.filter((c) => c.data.fenderId === 'S03')[1]!;
    const mm = String(Math.floor(firstRepeat.t / 60)).padStart(2, '0');
    const ss = String(Math.floor(firstRepeat.t % 60)).padStart(2, '0');
    await expect(s03.locator('.time'), '合併列的時間欄是第一次接觸的時刻').toHaveText(`${mm}:${ss}`);
    const kinds = await stream.locator('.list .item').evaluateAll((els) => els.map((el) => `${el.getAttribute('data-kind')}${el.classList.contains('merged') ? `:${el.getAttribute('data-fender')}` : ''}`));
    expect(kinds[0], '最上面是最後發生的事件(凍結)').toBe('frozen');
    const mergedAt = kinds.indexOf('fenderContact:S03');
    const markerAt = kinds.indexOf('timeScaleChanged');
    expect(markerAt, `事件串流的列:${kinds.join(' | ')}`).toBeGreaterThanOrEqual(0);
    expect(markerAt, `合併列建立之後才發生的教官事件排在合併列之上(列:${kinds.join(' | ')})`).toBeLessThan(mergedAt);
    // 標題:列數,另寫合併前的事件數(兩者之差 = 被合併掉的筆數)
    const title = ((await stream.locator('.panel-title .count').textContent()) ?? '').trim();
    const m = /^(\d+) 筆\(合併前 (\d+)\)$/.exec(title);
    expect(m, `事件串流標題「${title}」`).not.toBeNull();
    expect(Number(m![2]) - Number(m![1])).toBe(mergedAway);
    expect(await stream.locator('.list .item').count()).toBe(Number(m![1]));
    // 版面:有合併列時完整教官站的判定照樣通過(support/layout.ts)
    const report = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
    expect(layoutFailures(report), '完整教官站 1366×768(E14,事件串流有合併列)的版面問題').toEqual([]);
    expect(report.ellipsis.filter((e) => e.includes('brm-event-stream')), '事件串流沒有以 … 截斷的文字').toEqual([]);
    // 篩選:關掉「引擎」→ 碰墊的列(含合併列)都隱藏;再開 → 恢復
    const engine = stream.locator('.filters button.src-engine');
    await engine.click();
    await expect(stream.locator('.list .item[data-kind="fenderContact"]')).toHaveCount(0);
    await engine.click();
    await expect(s03).toHaveCount(1);
    // 講評報告(交接教官站紀錄):時間軸上 S03 同樣是一列合併列,次數相同
    const [popup] = await Promise.all([page.context().waitForEvent('page'), page.locator('header button.open-report').click()]);
    const popupErrors = new PageErrors(popup);
    await popup.waitForLoadState();
    const app = popup.locator('brm-report-app');
    await expect(app.locator('.msg')).toContainText('已載入 教官站紀錄', { timeout: 15_000 });
    await expect(app.locator('.msg')).toContainText(`(合併前 ${m![2]})`);
    const n03 = contacts.filter((c) => c.data.fenderId === 'S03').length;
    const tl = app.locator('.sheet[data-doc="report"] table.timeline tbody tr[data-kind="fenderContact"]');
    await expect(tl.filter({ hasText: '碰墊 S03' })).toHaveCount(2);
    await expect(tl.filter({ hasText: '碰墊 S03 反覆接觸' })).toHaveAttribute('data-count', String(n03 - 1));
    await expect(tl).toHaveCount(fenders.reduce((sum, f) => sum + Math.min(contacts.filter((c) => c.data.fenderId === f).length, 2), 0));
    popupErrors.expectNone();
    await popup.close();
    errors.expectNone();
  } finally {
    collected.stop();
    core.send({ type: 'timeScale', value: 1 });
    core.send({ type: 'resume' });
    await ensureRunning(core);
  }
});

test('重播檢視載入 E14 核心紀錄(逐筆的 fenderContact,1366×768):事件串流與時間軸顯示與教官站即時相同的合併列,版面判定通過', async ({ page, env }) => {
  // 核心的 JSON Lines 紀錄不變(仍逐筆);重播檢視讀入時以同一規則合併。夾具 record-E14(50 s):S03 在 0.04 s 第一次接觸,之後 16.9、38.8、49.8 s 三次反覆接觸。
  const errors = new PageErrors(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env, { mode: 'replay' }));
  const record = path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E14_unberthing_no_tug-core.jsonl');
  await page.locator('brm-replay-view input.file-input').setInputFiles(record);
  await expect(page.locator('brm-replay-view input.slider')).toBeVisible({ timeout: 15_000 });
  const stream = page.locator('brm-replay-view brm-event-stream');
  const s03 = stream.locator('.list .item[data-kind="fenderContact"][data-fender="S03"]');
  await expect(s03).toHaveCount(2);
  const merged = stream.locator('.list .item.merged[data-fender="S03"]');
  await expect(merged).toHaveAttribute('data-merged-count', '3');
  await expect(merged.locator('.text')).toHaveText('碰墊 S03 反覆接觸 ×3(00:16–00:49)最大 v 0.07 m/s、能量 7 kN·m(額定 282)、壓縮 9 %');
  await expect(merged.locator('.time')).toHaveText('00:16'); // 時間欄 = 第一次接觸(列留在它建立時的位置)
  await expect(page.locator('brm-replay-view .toolbar .info')).toContainText(/\d+ 筆事件\(合併前 \d+\)/);
  // 時間軸:合併列是一段橫條(第一次到最近一次接觸),寬度大於一般事件的直線標記;點它跳到第一次接觸
  const bar = page.locator('brm-replay-view .marks .mark.merged');
  await expect(bar).toHaveCount(1);
  const box = (await bar.boundingBox())!;
  const track = (await page.locator('brm-replay-view .marks').boundingBox())!;
  expect(box.width / track.width, '合併列橫條佔時間軸的比例(16.9 → 49.8 s,全長約 50 s)').toBeGreaterThan(0.5);
  // 橫條被同一段時間內其他事件的直線標記蓋住一部分:點橫條左端(第一次接觸的時刻)
  await bar.click({ position: { x: 2, y: 2 } });
  await expect(page.locator('brm-replay-view .timeline .t')).toContainText('00:16 /');
  const report = await page.evaluate(probeLayout, REPLAY_LAYOUT);
  expect(layoutFailures(report), '重播檢視 1366×768(E14,事件串流有合併列)的版面問題').toEqual([]);
  expect(report.ellipsis.filter((e) => e.includes('brm-event-stream')), '事件串流沒有以 … 截斷的文字').toEqual([]);
  errors.expectNone();
});

test('發行版本與降版提醒:頁首顯示發行版與軟體更新包版次(simosa-web 只送網頁需要的欄位);沒有狀態檔時不顯示、沒有 console error;降版時警示徽章、可展開說明,版面判定通過;駕駛台頁不顯示', async ({ page, env, core }) => {
  // 第三十輪:simosa-pkg status 的 releaseVersion / releaseDowngrade 原本只有下指令才看得到。啟動腳本把 status --json 存成狀態檔,
  // simosa-web(--status-file;global-setup 指到同樣形式的檔)以 /_status/release.json 提供,教官站頁首顯示。
  const errors = new PageErrors(page);
  const id = 'E13_tug_berthing_mailiao';
  await ensureRunning(core);
  await ensureScenario(core, id);
  try {
    // 1) 真實的 simosa-web:只有白名單欄位(狀態檔裡的裝置、金鑰、環境、警告都不送出)、no-store;/_status/ 之下其他路徑一律 404
    const res = await page.request.get(`${webBase(env)}/_status/release.json`);
    expect(res.status()).toBe(200);
    expect(res.headers()['cache-control']).toBe('no-store');
    expect(res.headers()['content-type']).toContain('application/json');
    const text = await res.text();
    expect(Object.keys(JSON.parse(text) as object).sort()).toEqual(['format', 'generatedUtc', 'releaseDowngrade', 'releaseVersion', 'softwareVersion']);
    expect(JSON.parse(text)).toMatchObject({ format: 'simosa-release-status/1', releaseVersion: env.releaseVersion, softwareVersion: env.softwareVersion, releaseDowngrade: null });
    expect((JSON.parse(text) as { generatedUtc: string }).generatedUtc).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
    expect(fs.readFileSync(env.statusFile, 'utf8'), '狀態檔本身帶有網頁用不到的欄位').toContain('E2E-SECRET-DEVICE');
    expect(text).not.toContain('SECRET');
    expect(text).not.toContain('E2E');
    for (const p of ['/_status/', '/_status/other.json', '/_STATUS/release.json', '/_status/release.json/x']) {
      expect((await page.request.get(`${webBase(env)}${p}`)).status(), p).toBe(404);
    }

    // 2) 沒有降版、套用過比發行版新的軟體更新包(global-setup 的狀態檔;船上靠更新包升級,這是常態):頁首小字寫實際執行的那一版
    //    「軟體更新 <版次>」,發行版在 title;不是按鈕(審查 BC-rBC-4:只寫發行版的話,靠更新包升級之後頁首一直停在安裝程式的版本)
    await page.setViewportSize({ width: 1366, height: 768 });
    await page.goto(instructorUrl(env));
    await waitConnected(page);
    const status = page.locator('header brm-release-status');
    await expect(status.locator('.version')).toHaveText(`軟體更新 ${env.softwareVersion}`);
    await expect(status.locator('.version')).toHaveAttribute('title', new RegExp(`實際執行的是軟體更新包 ${env.softwareVersion.replace(/\./g, '\\.')} 的程式\\(套用在發行版 ${env.releaseVersion.replace(/\./g, '\\.')} 之上`));
    await expect(status.locator('button')).toHaveCount(0);
    // 沒有套用過軟體更新包:寫「發行版 <版本>」
    await page.route('**/_status/release.json', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ format: 'simosa-release-status/1', generatedUtc: '2026-11-23T04:00:00Z', releaseVersion: env.releaseVersion, softwareVersion: null, releaseDowngrade: null }) }));
    await page.goto(instructorUrl(env));
    await waitConnected(page);
    await expect(status.locator('.version')).toHaveText(`發行版 ${env.releaseVersion}`);
    await page.unroute('**/_status/release.json');

    // 2b) 沒有狀態檔(開發模式、啟動時 status 逾時或失敗):simosa-web 回 200 與各欄 null(不是 404 —— 教官站每次載入都取這個網址,
    //     404 會讓瀏覽器每次記一筆 console error;審查 BC-rBC-2),頁首什麼都不顯示,完整與簡化模式都沒有 console error
    const moved = `${env.statusFile}.moved`;
    fs.renameSync(env.statusFile, moved);
    try {
      const none = await page.request.get(`${webBase(env)}/_status/release.json`);
      expect(none.status()).toBe(200);
      expect(none.headers()['cache-control']).toBe('no-store');
      expect(await none.json()).toEqual({ format: 'simosa-release-status/1', generatedUtc: null, releaseVersion: null, softwareVersion: null, releaseDowngrade: null });
      for (const mode of ['full', 'simple'] as const) {
        const statusResponses: number[] = [];
        const onResponse = (r: { url(): string; status(): number }): void => {
          if (r.url().includes('/_status/')) statusResponses.push(r.status());
        };
        page.on('response', onResponse);
        await page.goto(instructorUrl(env, mode === 'simple' ? { mode: 'simple' } : {}));
        await waitConnected(page);
        await expect.poll(() => statusResponses, { message: `${mode}:教官站載入時取一次發行版狀態` }).toEqual([200]);
        page.off('response', onResponse);
        await expect(page.locator('header brm-release-status')).toHaveCount(1);
        await expect(page.locator('header').first()).not.toContainText('發行版');
        await expect(page.locator('header').first()).not.toContainText('軟體更新');
        await expect(page.locator('header brm-release-status .version')).toHaveCount(0);
        errors.expectNone();
      }
    } finally {
      fs.renameSync(moved, env.statusFile);
    }

    // 3) 降版(攔下 /_status/release.json,回真實 simosa-pkg status 降版輸出縮成的內容):警示徽章 + 可展開的說明
    const downgrade = fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/release-status-downgrade.json'), 'utf8');
    await page.route('**/_status/release.json', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: downgrade }));
    const badge = status.locator('button.downgrade');
    const detail = status.locator('.detail');
    for (const size of [{ width: 1366, height: 768 }, { width: 1536, height: 864 }]) {
      await test.step(`完整教官站 ${size.width}×${size.height}(降版)`, async () => {
        await page.setViewportSize(size);
        await page.goto(instructorUrl(env));
        await waitConnected(page);
        // 與版面案例相同:以真實點擊選情境並載入(頁首多出情境 id,是頁首最長的狀態之一)
        const item = page.locator('brm-scenario-panel .list .item').filter({ hasText: id });
        await item.scrollIntoViewIfNeeded();
        await item.click();
        const loaded = core.waitState((s) => s.scenario?.id === id && s.t < 5, 30_000, `${id} 重新載入後的狀態幀`);
        await scenarioLoadButton(page).click();
        await loaded;
        await expect(page.locator('header .ship')).toContainText(id);
        for (const tag of ['brm-tug-panel', 'brm-anchor-panel', 'brm-mooring-panel']) await expect(page.locator(`.bottom ${tag}`)).toBeVisible();
        await expect(page.locator('brm-timeline-panel .list .item').first()).toBeVisible();
        await expect(badge).toHaveText('發行版 2026.11.1 · 降版提醒');
        await expect(badge).toHaveAttribute('aria-expanded', 'false');
        await expect(detail).toHaveCount(0); // 預設收合:不佔版面
        const collapsed = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
        expect(layoutFailures(collapsed), `降版徽章(收合)時完整教官站 ${size.width}×${size.height} 的版面問題`).toEqual([]);
        // 徽章整個在視窗內;真實點擊展開
        const b = (await badge.boundingBox())!;
        expect(b.x >= 0 && b.y >= 0 && b.x + b.width <= size.width && b.y + b.height <= size.height, `徽章外框 ${JSON.stringify(b)}`).toBe(true);
        await badge.click();
        await expect(badge).toHaveAttribute('aria-expanded', 'true');
        await expect(detail.locator('.lead')).toContainText('這台電腦目前執行的是較舊的發行版 2026.11.1;先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 比它新');
        await expect(detail.locator('.lead')).toContainText('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式');
        await expect(detail.locator('.advice')).toContainText('版本不低於 2026.12.2 的安裝程式重新安裝');
        await expect(detail.locator('.full')).toContainText('這則提醒會一直保留'); // status 的完整訊息
        await expect(detail.locator('.foot .source')).toContainText('127.0.0.1');
        // 展開的說明浮貼在頁首下方:整個在視窗內、不把版面推出視窗(整頁不捲動、面板不溢出),頁首高度不變
        const d = (await detail.boundingBox())!;
        expect(d.x >= 0 && d.y >= 0 && d.x + d.width <= size.width && d.y + d.height <= size.height, `說明外框 ${JSON.stringify(d)}`).toBe(true);
        const header = (await page.locator('header').first().boundingBox())!;
        expect(d.y, '說明在頁首下方').toBeGreaterThanOrEqual(header.y + header.height);
        const expanded = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
        expect([...expanded.page, ...expanded.overflow, ...expanded.offscreen, ...expanded.clipped, ...expanded.text], '展開說明時的版面問題(被說明蓋住的控制項不計)').toEqual([]);
        // 收合(真實點擊)後版面與展開前相同
        await detail.locator('button.collapse').click();
        await expect(detail).toHaveCount(0);
        expect(layoutFailures(await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), '收合後的版面問題').toEqual([]);
        // Esc 也能收合
        await badge.click();
        await expect(detail).toBeVisible();
        await page.keyboard.press('Escape');
        await expect(detail).toHaveCount(0);
      });
    }
    await test.step('簡化教官模式 1366×768(降版)', async () => {
      await page.setViewportSize({ width: 1366, height: 768 });
      await page.goto(instructorUrl(env, { mode: 'simple' }));
      await waitConnected(page);
      await expect(badge).toHaveText('發行版 2026.11.1 · 降版提醒');
      expect(layoutFailures(await page.evaluate(probeLayout, { keys: [], lists: [] })), '簡化模式選情境頁(降版徽章)的版面問題').toEqual([]);
      await badge.click();
      await expect(detail.locator('.lead')).toContainText('降版提醒');
      await detail.locator('button.collapse').click();
      await expect(detail).toHaveCount(0);
      // 點說明以外的地方也收合(審查 BC-rBC-6:展開的說明蓋住頁首下方的控制項,執行階段的 BRM 標記鈕就在它下面)
      await badge.click();
      await expect(detail).toBeVisible();
      const box = (await detail.boundingBox())!;
      await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2); // 點說明裡面:不收合
      await expect(detail).toBeVisible();
      await page.mouse.click(box.x + box.width / 2, Math.min(760, box.y + box.height + 40)); // 說明下方的版面
      await expect(detail).toHaveCount(0);
      await expect(badge).toHaveAttribute('aria-expanded', 'false');
    });

    // 4) 駕駛台頁(學員):沒有這個元件,也不取狀態
    let statusRequests = 0;
    page.on('request', (r) => {
      if (r.url().includes('/_status/')) statusRequests++;
    });
    await page.goto(bridgeUrl(env));
    await waitConnected(page);
    await expect(page.locator('brm-release-status')).toHaveCount(0);
    await expect(page.locator('header')).not.toContainText('發行版');
    expect(statusRequests, '駕駛台頁不應請求 /_status/').toBe(0);
    errors.expectNone();
  } finally {
    await ensureRunning(core);
  }
});
