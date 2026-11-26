// 教官站完整模式(instructor.html):情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復;無效內嵌情境 → 核心拒絕(scenarioLoadRejected)
// 並在面板顯示;下舵令 20°(操船台舵輪預設鈕)→ 操船台舵角指示變化;凍結/恢復(執行控制面板)→ 狀態幟 frozen 與 header「已凍結」;
// 1366×768、1536×864 與 1920×1080 下每個控制項都點得到、載入與執行控制不必捲動;選了最長 id 的情境(E20_comprehensive_assessment_*)時「載入」鈕
// 不撐出左欄、清單與詳細區的高度依情境面板實際高度(核心固定在有階段的 E13);重播檢視載入紀錄後(講評摘要不水平捲動、補充欄不逐字換行)
// 與簡化模式「開始練習」後(1366×768)事件串流篩選鈕、PTT 等不被蓋住或裁切(版面;判定在 support/layout.ts);
// E14(開始即繫妥、吹攏風)碰墊的反覆接觸在事件串流合併成一列(次數與核心送出的事件數相符、文字不截斷),講評報告的時間軸顯示同一列;
// 頁首的發行版本、軟體更新包版次與降版提醒(simosa-web 的 /_status/release.json;降版時警示徽章與可展開的說明,版面判定通過;
// 沒有狀態檔時不顯示、也沒有 console error;駕駛台頁不顯示);
// 教官筆電的教官站顯示船上筆電的版本(第三十一輪;頁面由「教官筆電」的 simosa-web 提供,跨來源向另一個當作「船上筆電」的 simosa-web 取
// /_status/release.json:版本相同、版本不同、船上降版、船上沒有發行版狀態、取不到;頁面與核心在同一台時不發跨來源請求;沒有 preflight、沒有 console error);
// 既有安裝的網址形式(沒有 shipWeb 參數,由 ?ws= 的主機推得 http://<主機>:8080;另開一個帶 --host-resolver-rules 的 Chromium)與
// 1093×614 展開船上降版的說明時執行控制不被蓋住、點得到(審查後修正)。
// 重播檢視讀大檔(第三十一輪):超過上限(7201 筆)的核心紀錄分段讀取、讀取中顯示百分比,資訊列寫明降採樣,時間軸 / 逐筆 / 事件跳轉可用,
// 講評摘要與航跡以全部狀態計算(完整教官站的「重播檢視」分頁與 ?mode=replay 都走同一個元件)。
// 審查後修正:降採樣時時間軸的方向鍵走逐筆;超過上限後倒帶回到上限以內的紀錄每一筆都在、沒有降採樣的說明。
import fs from 'node:fs';
import http from 'node:http';
import type { AddressInfo } from 'node:net';
import path from 'node:path';
import { chromium } from '@playwright/test';
import { OUT, ROOT, bridgeUrl, instructorUrl, shipWebBase, webBase, type E2eEnv } from '../support/env.ts';
import { freePort } from '../support/processes.ts';
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

test('警報列看得到字:講評摘要的逾時 / 失敗列與航跡圖目標船小表的 CPA 警報列沒有整列的紅底,列內每一格的字色都與底色不同', async ({ page, env }) => {
  // 第三十一輪主控程序驗收時發現(2026-10 起就有):共用樣式的 .alarm(徽章:紅底)也套到 <tr class="alarm">,
  // 而列內的數值由元件標成警報色 —— 紅底紅字,逾時 / 失敗列的數值與 CPA 警報中的目標船整列都看不到。
  // 夾具:E08(講評摘要有逾時的必要事件與未確認的警報)、E02(結尾有 3 艘目標船在 CPA 警報中)。
  const errors = new PageErrors(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env, { mode: 'replay' }));
  const alarmRows = (host: string) =>
    page.locator(`brm-replay-view ${host}`).evaluate((el) => {
      const transparent = (c: string) => c === 'transparent' || /^rgba\(\s*0,\s*0,\s*0,\s*0\s*\)$/.test(c);
      return [...el.shadowRoot!.querySelectorAll('tr.alarm')].map((tr) => {
        const rowBg = getComputedStyle(tr).backgroundColor;
        const cells = [...tr.children].map((td) => {
          const cs = getComputedStyle(td);
          return { text: (td.textContent ?? '').trim(), color: cs.color, bg: cs.backgroundColor };
        });
        return {
          text: (tr.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 40),
          rowBg,
          rowBgTransparent: transparent(rowBg),
          // 有字的格子裡,字色與它自己或整列的底色相同的(看不到字)
          invisible: cells.filter((c) => c.text !== '' && (c.color === rowBg || (!transparent(c.bg) && c.color === c.bg))).map((c) => c.text),
        };
      });
    });
  for (const [fixture, host, minRows] of [
    ['record-E08-core.jsonl', 'brm-debrief-summary', 3],
    ['record-E02_coastal_traffic_strait-core.jsonl', 'brm-track-plot', 3],
  ] as const) {
    await page.locator('brm-replay-view input.file-input').setInputFiles(path.join(ROOT, 'src/Instruments.Web/tests/fixtures', fixture));
    // 讀取中另有一項 .info.loading(「讀取中…」),與資訊列同時存在:只看資訊列
    await expect(page.locator('brm-replay-view .toolbar .info:not(.loading)')).toContainText(fixture, { timeout: 15_000 });
    // 跳到結尾:目標船小表的警報列依「到目前時刻為止的事件」決定
    await page.locator('brm-replay-view input.slider').press('End');
    await expect.poll(async () => (await alarmRows(host)).length, { message: `${fixture}:${host} 的警報列數` }).toBeGreaterThanOrEqual(minRows);
    const rows = await alarmRows(host);
    expect(rows.filter((r) => !r.rowBgTransparent).map((r) => `${r.text}:${r.rowBg}`), `${fixture}:${host} 的警報列不該有整列的底色`).toEqual([]);
    expect(rows.filter((r) => r.invisible.length > 0).map((r) => `${r.text}:${r.invisible.join(' | ')}`), `${fixture}:${host} 的警報列裡字色與底色相同的格子`).toEqual([]);
  }
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

/** 改寫「船上筆電」的發行版狀態檔(與 simosa-pkg status --json 相同形式;另帶網頁用不到、simosa-web 不得送出的欄位) */
function writeShipStatus(env: E2eEnv, st: { releaseVersion: string; softwareVersion?: string | null; releaseDowngrade?: unknown }): void {
  fs.writeFileSync(
    env.shipStatusFile,
    JSON.stringify({
      type: 'statusResult',
      shipId: 'FSB1',
      deviceId: 'E2E-SECRET-SHIP-DEVICE',
      components: st.softwareVersion ? { software: { version: st.softwareVersion, summary: 'E2E-SECRET-SHIP-SUMMARY' } } : {},
      warnings: ['E2E-SECRET-SHIP-WARNING'],
      releaseVersion: st.releaseVersion,
      ...(st.releaseDowngrade ? { releaseDowngrade: st.releaseDowngrade } : {}),
    }),
  );
}

test('教官筆電的教官站顯示船上筆電的版本:跨來源取船上的 /_status/release.json(沒有 preflight);版本相同「船上 <版本>」、版本不同與船上降版是警示徽章(展開先寫船上、再寫本機),版面判定通過;取不到時與只有本機時相同;頁面與核心在同一台時不發跨來源請求', async ({ page, env, core, context }) => {
  // 第三十一輪:教官筆電以 start-instructor-laptop 開的教官站,頁面是教官筆電自己的 simosa-web 提供的(只有核心的 WebSocket 指向船上),
  // 原本頁首只有教官筆電的版本。這裡的「教官筆電」是 global-setup 的 simosa-web(http://127.0.0.1:<埠>),「船上筆電」是第二個 simosa-web,
  // 以 ?shipWeb=http://localhost:<埠> 指過去 —— 127.0.0.1 與 localhost 在瀏覽器是不同來源,所以是真的跨來源請求(CORS 由真實的 simosa-web 回應)。
  const errors = new PageErrors(page);
  const id = 'E13_tug_berthing_mailiao';
  const ship = shipWebBase(env);
  const shipLog = path.join(env.logsDir, 'ship-web.log');
  // 「船上」simosa-web 的請求紀錄(ship-web.log)裡 /_status/ 的各行。simosa-web 先送出回應、後寫 log:收到回應不代表那一行已經落檔,
  // 所以「到目前為止有幾行」與「之後收到哪些請求」都以 expect.poll 等到位,不做一次性讀取(審查 e2e-ship-log-race:log 晚 300 ms 落檔時,
  // 步驟 0 自己的請求會被算進步驟 1 的「不應收到請求」)
  const shipStatusLog = (): string[] => fs.readFileSync(shipLog, 'utf8').split('\n').filter((l) => l.includes(' /_status/'));
  let logSeen = shipStatusLog().length; // 已經核對過的行數(本案例開始時沒有進行中的請求)
  const originalShipStatus = fs.readFileSync(env.shipStatusFile, 'utf8');
  const status = page.locator('header brm-release-status');
  const badge = status.locator('button.badge');
  const detail = status.locator('.detail');
  const statusRequests: string[] = [];
  page.on('request', (r) => {
    if (r.url().includes('/_status/')) statusRequests.push(`${r.method()} ${r.url()}`);
  });
  await ensureRunning(core);
  await ensureScenario(core, id);
  try {
    // 0) 真實的 simosa-web:只有本機迴路位址的頁面拿得到 CORS 標頭(完整的各種 Origin 在 tools/launch/tests/launch-tests.sh 的 test_web_status_cors)
    const allowed = await page.request.get(`${ship}/_status/release.json`, { headers: { Origin: webBase(env) } });
    expect(allowed.status()).toBe(200);
    expect(allowed.headers()['access-control-allow-origin']).toBe(webBase(env));
    expect(allowed.headers()['vary']).toBe('Origin');
    for (const origin of ['http://192.168.77.21:8080', 'https://127.0.0.1:8080', 'null', 'http://127.0.0.1.evil.example', `http://localhost.evil.example:${env.webPort}`]) {
      const denied = await page.request.get(`${ship}/_status/release.json`, { headers: { Origin: origin } });
      expect(denied.status(), origin).toBe(200);
      expect(denied.headers()['access-control-allow-origin'], origin).toBeUndefined();
    }
    const text = await allowed.text();
    expect(text).not.toContain('SECRET');
    // 上面 6 個請求的 log 都落檔之後,才開始看「之後」的請求
    await expect.poll(() => shipStatusLog().length, { message: '步驟 0 的 6 個請求都已寫進 ship-web.log' }).toBe(logSeen + 6);
    logSeen += 6;

    // 1) 頁面與核心在同一台電腦(ws 的主機 = 頁面的主機;其他案例都是這樣):只取同源的狀態,不發跨來源請求;頁首與原本相同
    await page.setViewportSize({ width: 1366, height: 768 });
    await page.goto(instructorUrl(env, { scenario: id }));
    await waitConnected(page);
    await expect(status.locator('.version')).toHaveText(`軟體更新 ${env.softwareVersion}`);
    await expect(status.locator('.version')).not.toHaveAttribute('title', /船上/);
    await page.waitForTimeout(500);
    expect(statusRequests, '同一台電腦:只有同源的一次請求').toEqual([`GET ${webBase(env)}/_status/release.json`]);
    expect(shipStatusLog().slice(logSeen), '「船上」的 simosa-web 不應收到請求').toEqual([]);

    // 2) 頁面由「教官筆電」提供、船上位址指向另一個伺服器,兩台同一版:頁首小字「船上 <版本>」(不是按鈕),本機的在 title;
    //    跨來源請求是簡單請求:瀏覽器沒有送 OPTIONS(preflight),伺服器看到的 Origin 是教官站頁面的來源
    statusRequests.length = 0;
    await page.goto(instructorUrl(env, { scenario: id, shipWeb: ship }));
    await waitConnected(page);
    const label = status.locator('.version.onboard');
    await expect(label).toHaveText(`船上 ${env.softwareVersion}`);
    const esc = (v: string): string => v.replace(/\./g, '\\.');
    await expect(label).toHaveAttribute('title', new RegExp(`船上筆電實際執行的是軟體更新包 ${esc(env.softwareVersion)} 的程式\\(套用在發行版 ${esc(env.releaseVersion)} 之上\\);這台電腦\\(本機\\)相同\\(軟體更新 ${esc(env.softwareVersion)}\\)`));
    await expect(label).toHaveAttribute('title', new RegExp(`船上筆電:${esc(ship)}.*本機:提供本頁面的電腦\\(127\\.0\\.0\\.1:${env.webPort},即這台電腦\\)`));
    await expect(status.locator('button')).toHaveCount(0);
    await expect(page.locator('header .ship')).toContainText(id);
    expect([...statusRequests].sort(), '一次同源、一次跨來源,都是 GET(沒有 OPTIONS)').toEqual([`GET ${webBase(env)}/_status/release.json`, `GET ${ship}/_status/release.json`].sort());
    // 步驟 1 之後到現在只有這一行(步驟 1 若有晚落檔的請求,這裡會多出來)
    await expect
      .poll(() => shipStatusLog().slice(logSeen).map((l) => l.replace(/^\S+ /, '')), { message: '「船上」的 simosa-web 收到的請求(沒有 OPTIONS;Origin 是教官站頁面的來源)' })
      .toEqual([`200 GET /_status/release.json HTTP/1.1(跨來源 ${webBase(env)})`]);
    expect(layoutFailures(await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), '完整教官站 1366×768(頁首「船上 <版本>」)的版面問題').toEqual([]);
    // 簡化模式也一樣
    await page.goto(instructorUrl(env, { mode: 'simple', shipWeb: ship }));
    await waitConnected(page);
    await expect(label).toHaveText(`船上 ${env.softwareVersion}`);
    expect(layoutFailures(await page.evaluate(probeLayout, { keys: [], lists: [] })), '簡化模式選情境頁(頁首「船上 <版本>」)的版面問題').toEqual([]);

    // 3) 兩台實際執行的版本不同(船上沒有套用軟體更新包,比本機舊):警示徽章「船上 <版本> · 版本不同」,展開說明哪一台較舊與處理
    writeShipStatus(env, { releaseVersion: env.releaseVersion });
    for (const size of [{ width: 1366, height: 768 }, { width: 1536, height: 864 }]) {
      await test.step(`版本不同:完整教官站 ${size.width}×${size.height}`, async () => {
        await page.setViewportSize(size);
        await page.goto(instructorUrl(env, { scenario: id, shipWeb: ship }));
        await waitConnected(page);
        await expect(badge).toHaveText(`船上 ${env.releaseVersion} · 版本不同`);
        await expect(badge).toHaveAttribute('aria-expanded', 'false');
        await expect(detail).toHaveCount(0); // 預設收合:不佔版面
        await expect(status.locator('.version')).toHaveCount(0);
        expect(layoutFailures(await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), `版本不同徽章(收合)時完整教官站 ${size.width}×${size.height} 的版面問題`).toEqual([]);
        const b = (await badge.boundingBox())!;
        expect(b.x >= 0 && b.y >= 0 && b.x + b.width <= size.width && b.y + b.height <= size.height, `徽章外框 ${JSON.stringify(b)}`).toBe(true);
        await badge.click();
        await expect(badge).toHaveAttribute('aria-expanded', 'true');
        await expect(detail.locator('.machine.onboard .line')).toHaveText(`船上筆電:實際執行的是發行版 ${env.releaseVersion}。`);
        await expect(detail.locator('.machine.local .line')).toContainText(`這台電腦(本機):實際執行的是軟體更新包 ${env.softwareVersion} 的程式`);
        await expect(detail.locator('.mismatch .lead')).toContainText(`版本不同:船上筆電執行的是發行版 ${env.releaseVersion},這台電腦(本機)執行的是軟體更新 ${env.softwareVersion} — 船上筆電較舊`);
        await expect(detail.locator('.mismatch .advice')).toContainText('更新較舊的那一台(船上筆電)');
        await expect(detail.locator('.mismatch .advice')).toContainText('docs/design/packaging-sync.md 第 6 節');
        await expect(detail.locator('.foot .source')).toContainText(`船上筆電:${ship}`);
        // 展開的說明浮貼在頁首下方:整個在視窗內、不把版面推出視窗,頁首高度不變
        const d = (await detail.boundingBox())!;
        expect(d.x >= 0 && d.y >= 0 && d.x + d.width <= size.width && d.y + d.height <= size.height, `說明外框 ${JSON.stringify(d)}`).toBe(true);
        const header = (await page.locator('header').first().boundingBox())!;
        expect(d.y, '說明在頁首下方').toBeGreaterThanOrEqual(header.y + header.height);
        const expanded = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
        expect([...expanded.page, ...expanded.overflow, ...expanded.offscreen, ...expanded.clipped, ...expanded.text], '展開說明時的版面問題(被說明蓋住的控制項不計)').toEqual([]);
        await detail.locator('button.collapse').click();
        await expect(detail).toHaveCount(0);
        expect(layoutFailures(await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), '收合後的版面問題').toEqual([]);
      });
    }

    // 4) 船上有降版提醒(真實 simosa-pkg status 的降版輸出):與本機降版同樣醒目的徽章、寫明是船上;展開先寫船上的,再寫本機的
    const downgrade = JSON.parse(fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/release-status-downgrade.json'), 'utf8')) as { releaseVersion: string; softwareVersion: string; releaseDowngrade: unknown };
    writeShipStatus(env, { releaseVersion: downgrade.releaseVersion, softwareVersion: downgrade.softwareVersion, releaseDowngrade: downgrade.releaseDowngrade });
    await test.step('船上降版:完整教官站 1366×768', async () => {
      await page.setViewportSize({ width: 1366, height: 768 });
      await page.goto(instructorUrl(env, { scenario: id, shipWeb: ship }));
      await waitConnected(page);
      await expect(badge).toHaveText('船上 2026.11.1 · 船上降版');
      await expect(badge).toHaveClass(/downgrade/);
      await expect(badge).toHaveAttribute('title', /船上筆電目前執行的是較舊的發行版 2026\.11\.1/);
      expect(layoutFailures(await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), '船上降版徽章(收合)時完整教官站 1366×768 的版面問題').toEqual([]);
      await badge.click();
      const blocks = await detail.evaluate((el) => [...el.children].map((c) => c.className));
      expect(blocks, '展開的說明:船上 → 本機 → 版本不同 → 資料來源').toEqual(['machine onboard down', 'machine local', 'mismatch', 'foot']);
      await expect(detail.locator('.machine.onboard .lead')).toContainText('船上筆電 · 降版提醒:船上筆電目前執行的是較舊的發行版 2026.11.1;先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 比它新');
      await expect(detail.locator('.machine.onboard .advice')).toContainText('版本不低於 2026.12.2 的安裝程式重新安裝');
      // 船上的 status 完整訊息預設收合(重點與處理在上面);點標題列才展開
      const full = detail.locator('.machine.onboard details.full');
      await expect(full.locator('summary')).toHaveText('完整訊息(船上筆電的 simosa-pkg status)');
      await expect(full.locator('.message')).toBeHidden();
      await full.locator('summary').click();
      await expect(full.locator('.message')).toBeVisible();
      await expect(full.locator('.message')).toContainText('這則提醒會一直保留');
      await full.locator('summary').click();
      await expect(full.locator('.message')).toBeHidden();
      await expect(detail.locator('.machine.local .line')).toContainText(`這台電腦(本機):實際執行的是軟體更新包 ${env.softwareVersion} 的程式`);
      await expect(detail.locator('.mismatch .lead')).toContainText('船上筆電較舊');
      // 較舊的船上筆電有降版提醒:「版本不同」的處理指向上面的降版提醒,不叫人「更新較舊的那一台」(與「刻意回到舊版時不必處理」矛盾)
      await expect(detail.locator('.mismatch .advice')).toContainText('較舊的是有降版提醒的那一台(船上筆電),依上面降版提醒的處理');
      await expect(detail.locator('.mismatch .advice')).not.toContainText('更新較舊的那一台');
      await expect(detail).not.toContainText('SECRET');
      const d = (await detail.boundingBox())!;
      expect(d.x >= 0 && d.y >= 0 && d.x + d.width <= 1366 && d.y + d.height <= 768, `說明外框 ${JSON.stringify(d)}`).toBe(true);
      const expanded = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
      expect([...expanded.page, ...expanded.overflow, ...expanded.offscreen, ...expanded.clipped, ...expanded.text], '展開說明時的版面問題(被說明蓋住的控制項不計)').toEqual([]);
      await page.keyboard.press('Escape');
      await expect(detail).toHaveCount(0);
    });
    await test.step('船上降版:簡化教官模式 1366×768', async () => {
      await page.goto(instructorUrl(env, { mode: 'simple', shipWeb: ship }));
      await waitConnected(page);
      await expect(badge).toHaveText('船上 2026.11.1 · 船上降版');
      expect(layoutFailures(await page.evaluate(probeLayout, { keys: [], lists: [] })), '簡化模式選情境頁(船上降版徽章)的版面問題').toEqual([]);
      await badge.click();
      await expect(detail.locator('.machine.onboard .lead')).toContainText('船上筆電 · 降版提醒');
      // 點說明以外的地方收合
      const box = (await detail.boundingBox())!;
      await page.mouse.click(box.x + box.width / 2, Math.min(760, box.y + box.height + 40));
      await expect(detail).toHaveCount(0);
    });
    // 4b) 船上連得到、但沒有發行版狀態(船上從儲存庫啟動的開發模式,或船上啟動時沒有取到 simosa-pkg status:狀態檔不存在 → 200、CORS 標頭、各欄 null):
    //     頁首與只有本機時相同;title 寫「船上筆電沒有發行版狀態」而不是「未取得 / 船上是較舊的版本」;不重試(取不到時第一次重試在 4 秒後)
    await test.step('船上沒有發行版狀態', async () => {
      fs.rmSync(env.shipStatusFile);
      statusRequests.length = 0;
      await page.goto(instructorUrl(env, { scenario: id, shipWeb: ship }));
      await waitConnected(page);
      await expect(status.locator('.version')).toHaveText(`軟體更新 ${env.softwareVersion}`);
      await expect(status.locator('.version')).toHaveAttribute('title', new RegExp(`船上筆電沒有發行版狀態\\(已向 ${esc(ship)} 取到回應`));
      await expect(status.locator('.version')).not.toHaveAttribute('title', /未取得|較舊的版本/);
      await expect(status.locator('.version.onboard, button')).toHaveCount(0);
      await page.waitForTimeout(4600);
      expect(statusRequests.filter((r) => r.includes(ship)), '對船上只有一次請求(沒有發行版狀態不重試)').toEqual([`GET ${ship}/_status/release.json`]);
      fs.writeFileSync(env.shipStatusFile, originalShipStatus);
    });
    // 到這裡為止(同一台、版本相同、版本不同、船上降版、船上沒有發行版狀態)主控台沒有任何錯誤
    errors.expectNone();

    // 5) 取不到船上的狀態:畫面與只有本機時相同、不顯示錯誤;title 註明「船上筆電的版本:未取得」。
    //    另開一頁收集主控台:跨來源請求失敗的紀錄是瀏覽器記的,網頁端消不掉 —— 只容許「對船上那個網址的請求失敗」這一種,其他錯誤照樣不行
    const closedPort = await freePort();
    // 舊版的船上網頁伺服:以測試程序內的 HTTP 伺服器重現(真的跨來源請求、真的沒有 CORS 標頭;page.route 的 fulfill 會自動補 CORS 標頭,做不出來)。
    //   第三十輪的 simosa-web 有 /_status/release.json 但不回 Access-Control-Allow-Origin(200);更舊的版本沒有這個網址(404)
    let oldStatus = 200;
    const oldShip = http.createServer((_req, res) => {
      res.writeHead(oldStatus, { 'Content-Type': oldStatus === 200 ? 'application/json; charset=utf-8' : 'text/plain; charset=utf-8', 'Cache-Control': 'no-store', Connection: 'close' });
      // 200 的本體是合格的發行版狀態(版本 2026.1.1):沒有 CORS 標頭時網頁讀不到,頁首不可以出現這個版本
      res.end(oldStatus === 200 ? JSON.stringify({ format: 'simosa-release-status/1', generatedUtc: null, releaseVersion: '2026.1.1', softwareVersion: null, releaseDowngrade: null }) : 'not found\n');
    });
    await new Promise<void>((resolve) => oldShip.listen(0, '127.0.0.1', resolve));
    const oldPort = (oldShip.address() as AddressInfo).port;
    const cases = [
      { name: '連不上(船上的網頁伺服沒有啟動)', shipWeb: `http://localhost:${closedPort}`, allow: /net::ERR_CONNECTION_REFUSED/, status: 0 },
      { name: '船上是第三十輪的版本(有 /_status/,沒有 CORS 標頭)', shipWeb: `http://localhost:${oldPort}`, allow: /blocked by CORS policy|net::ERR_FAILED/, status: 200 },
      { name: '船上是更舊的版本(沒有 /_status/:404)', shipWeb: `http://localhost:${oldPort}`, allow: /blocked by CORS policy|net::ERR_FAILED/, status: 404 },
    ];
    try {
      for (const c of cases) {
        await test.step(`取不到:${c.name}`, async () => {
          if (c.status) oldStatus = c.status;
          const p2 = await context.newPage();
          const target = `${c.shipWeb}/_status/release.json`;
          const seen: Array<{ text: string; url: string }> = [];
          const other: string[] = [];
          p2.on('pageerror', (e) => other.push(`pageerror: ${e.message}`));
          p2.on('console', (m) => {
            if (m.type() !== 'error') return;
            if ((m.location().url === target || m.text().includes(target)) && c.allow.test(m.text())) seen.push({ text: m.text(), url: m.location().url });
            else other.push(`console.error: ${m.text()}`);
          });
          await p2.setViewportSize({ width: 1366, height: 768 });
          await p2.goto(instructorUrl(env, { scenario: id, shipWeb: c.shipWeb }));
          await waitConnected(p2);
          const st = p2.locator('header brm-release-status');
          await expect.poll(() => seen.length, { message: '瀏覽器記下對船上那個網址的請求失敗' }).toBeGreaterThanOrEqual(1);
          await expect(st.locator('.version')).toHaveText(`軟體更新 ${env.softwareVersion}`);
          await expect(st.locator('.version.onboard')).toHaveCount(0);
          await expect(st.locator('.version')).toHaveAttribute('title', new RegExp(`船上筆電的版本:未取得\\(連上船上的核心後向 ${esc(c.shipWeb)} 取`));
          await expect(st.locator('button')).toHaveCount(0);
          await expect(p2.locator('header').first()).not.toContainText(/未取得|錯誤|失敗|2026\.1\.1/);
          expect(layoutFailures(await p2.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), `取不到船上的狀態時完整教官站 1366×768 的版面問題`).toEqual([]);
          expect(other, '除了「對船上那個網址的請求失敗」以外不應有其他錯誤').toEqual([]);
          // 一次失敗的跨來源請求瀏覽器記 1–2 筆(CORS 被擋時是兩筆);元件在連上後約 4、19、49、109、169 秒重試,這裡只會看到第一次(最多第二次)
          expect(seen.length, `主控台的紀錄:${JSON.stringify(seen)}`).toBeLessThanOrEqual(4);
          await p2.close();
        });
      }
    } finally {
      await new Promise((resolve) => oldShip.close(resolve));
    }
  } finally {
    fs.writeFileSync(env.shipStatusFile, originalShipStatus);
    await ensureRunning(core);
  }
});

test('既有安裝的教官站網址(沒有 shipWeb 參數):由 ?ws= 的主機推得 http://<主機>:8080 向船上取版本;1093×614 展開船上降版的說明時執行控制不被蓋住、點得到,「收合」不必捲動', async ({ env, core }) => {
  // 審查 e2e-no-derived-path:config/instructor-laptop.json 的 openPages 只有 ws 參數(ports.shipWeb 預設 null),實船走的是「由 ws 的主機推導」
  // 這條路,上一個案例每一步都帶明確的 ?shipWeb=。這裡另開一個 Chromium,以 --host-resolver-rules 把假的船上位址 192.168.77.11 對到本機:
  // ws://192.168.77.11:<核心埠> → 核心、http://192.168.77.11:8080 → 「船上」simosa-web;頁面仍由「教官筆電」的 simosa-web(http://127.0.0.1:<埠>)提供。
  // 審查 popover-covers-run-controls:船上降版的說明在 1093×614 高 320 px,蓋住執行控制的「凍結 / 恢復 / 重設 / 結束練習」而點不到。
  const id = 'E13_tug_berthing_mailiao';
  const fakeShip = '192.168.77.11';
  const shipLog = path.join(env.logsDir, 'ship-web.log');
  const shipStatusLog = (): string[] => fs.readFileSync(shipLog, 'utf8').split('\n').filter((l) => l.includes(' /_status/'));
  const originalShipStatus = fs.readFileSync(env.shipStatusFile, 'utf8');
  const use = test.info().project.use;
  const rules = [`MAP ${fakeShip}:${env.wsPort} 127.0.0.1:${env.wsPort}`, `MAP ${fakeShip}:8080 127.0.0.1:${env.shipWebPort}`].join(', ');
  const browser = await chromium.launch({
    ...(use.launchOptions ?? {}),
    ...(use.channel ? { channel: use.channel } : {}),
    headless: use.headless ?? true,
    // --no-proxy-server:環境變數設了代理的機器上,非迴路位址的 ws:// 會被送進代理(對應規則只改連線的位址,不改「要不要走代理」)
    args: [...(use.launchOptions?.args ?? []), '--no-proxy-server', `--host-resolver-rules=${rules}`],
  });
  await ensureRunning(core);
  await ensureScenario(core, id);
  try {
    const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, locale: 'zh-TW', timezoneId: 'Asia/Taipei', permissions: ['microphone'] });
    const page = await context.newPage();
    const errors = new PageErrors(page);
    const statusRequests: string[] = [];
    page.on('request', (r) => {
      if (r.url().includes('/_status/') || r.method() === 'OPTIONS') statusRequests.push(`${r.method()} ${r.url()}`);
    });
    // 與 start-instructor-laptop 開的網址相同的形式:ws 指向船上的 IP,沒有 shipWeb 參數
    const derivedUrl = (opts: Parameters<typeof instructorUrl>[1] = {}): string => {
      const u = new URL(instructorUrl(env, opts));
      u.searchParams.set('ws', `ws://${fakeShip}:${env.wsPort}`);
      expect(u.searchParams.has('shipWeb')).toBe(false);
      return u.toString();
    };
    const status = page.locator('header brm-release-status');
    const badge = status.locator('button.badge');
    const detail = status.locator('.detail');
    const esc = (v: string): string => v.replace(/\./g, '\\.');

    // 1) 兩台同一版:頁首「船上 <版本>」;請求送往 http://<ws 主機>:8080,簡單請求(沒有 OPTIONS),伺服器看到的 Origin 是教官站頁面的來源
    const logSeen = shipStatusLog().length;
    await page.goto(derivedUrl({ scenario: id }));
    await waitConnected(page);
    const label = status.locator('.version.onboard');
    await expect(label).toHaveText(`船上 ${env.softwareVersion}`);
    await expect(label).toHaveAttribute('title', new RegExp(`船上筆電:http://${esc(fakeShip)}:8080.*本機:提供本頁面的電腦\\(127\\.0\\.0\\.1:${env.webPort},即這台電腦\\)`));
    expect([...statusRequests].sort(), '一次同源、一次送往 http://<ws 主機>:8080,都是 GET(沒有 OPTIONS)').toEqual([`GET ${webBase(env)}/_status/release.json`, `GET http://${fakeShip}:8080/_status/release.json`].sort());
    await expect
      .poll(() => shipStatusLog().slice(logSeen).map((l) => l.replace(/^\S+ /, '')), { message: '「船上」的 simosa-web 收到的請求' })
      .toEqual([`200 GET /_status/release.json HTTP/1.1(跨來源 ${webBase(env)})`]);
    // 簡化模式相同
    await page.goto(derivedUrl({ mode: 'simple' }));
    await waitConnected(page);
    await expect(label).toHaveText(`船上 ${env.softwareVersion}`);
    errors.expectNone();

    // 2) 船上降版、1093×614(1366×768 的筆電以 125% 縮放;頁首下方到執行控制只有 264 px):展開的說明不蓋住執行控制
    const downgrade = JSON.parse(fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/release-status-downgrade.json'), 'utf8')) as { releaseVersion: string; softwareVersion: string; releaseDowngrade: unknown };
    writeShipStatus(env, { releaseVersion: downgrade.releaseVersion, softwareVersion: downgrade.softwareVersion, releaseDowngrade: downgrade.releaseDowngrade });
    await page.setViewportSize({ width: 1093, height: 614 });
    await page.goto(derivedUrl({ scenario: id }));
    await waitConnected(page);
    await expect(badge).toHaveText('船上 2026.11.1 · 船上降版');
    const run = page.locator('brm-run-control');
    const runKeys = (r: { key: string[] }): string[] => r.key.filter((k) => /執行控制|brm-release-status/.test(k));
    expect(runKeys(await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT)), '收合時執行控制點得到').toEqual([]);
    await badge.click();
    await expect(detail).toBeVisible();
    /** 說明、「收合」鈕、「凍結」鈕的外框:說明的底邊在「凍結」鈕之上;「收合」整個在說明的可見範圍內(不必在說明裡捲動) */
    const boxes = async (): Promise<{ d: { y: number; height: number }; c: { y: number; height: number }; f: { y: number } }> => ({
      d: (await detail.boundingBox())!,
      c: (await detail.locator('button.collapse').boundingBox())!,
      f: (await run.locator('button.freeze').boundingBox())!,
    });
    const check = async (what: string): Promise<void> => {
      const { d, c, f } = await boxes();
      expect(d.y + d.height, `${what}:說明的底邊(${d.y + d.height})在「凍結」鈕的上緣(${f.y})之上`).toBeLessThanOrEqual(f.y);
      expect(c.y >= d.y && c.y + c.height <= d.y + d.height + 0.5, `${what}:「收合」鈕在說明的可見範圍內(說明 ${JSON.stringify(d)}、收合 ${JSON.stringify(c)})`).toBe(true);
      // 不必捲動時,底列(來源與「收合」)不可往上蓋住上一段的最後一行(第三十一輪:貼底的寫法曾把底列往上推 8 px,蓋住最後一行的下半)
      const foot = await detail.evaluate((el) => {
        const last = el.lastElementChild as HTMLElement;
        const prev = last.previousElementSibling as HTMLElement;
        return { scrolls: el.scrollHeight > el.clientHeight + 1, gap: last.getBoundingClientRect().top - prev.getBoundingClientRect().bottom };
      });
      if (!foot.scrolls) expect(foot.gap, `${what}:底列與上一段之間的間隔(px;負值 = 蓋住上一段的最後一行)`).toBeGreaterThanOrEqual(0);
      const open = await page.evaluate(probeLayout, INSTRUCTOR_LAYOUT);
      expect(runKeys(open), `${what}:執行控制(凍結 / 恢復 / 重設 / 結束練習)沒有被說明蓋住`).toEqual([]);
      expect([...open.page, ...open.overflow, ...open.clipped], `${what}:說明不把版面推出視窗`).toEqual([]);
    };
    await check('展開(完整訊息收合)');
    // 把船上的完整訊息也展開:內容變高,說明在自己裡面捲動,仍不蓋住執行控制,「收合」貼在底邊
    await detail.locator('.machine.onboard details.full summary').click();
    await expect(detail.locator('.machine.onboard details.full .message')).toBeVisible();
    await check('展開(完整訊息也展開)');
    expect(await detail.evaluate((el) => el.scrollHeight > el.clientHeight), '完整訊息展開後內容比說明高(在說明裡捲動)').toBe(true);
    // 說明展開著,直接按「凍結」(真實點擊;被蓋住的話 Playwright 會回報被 <brm-release-status> 攔截):核心凍結,說明因為點在外面而收合
    const frozenEvent = core.waitEvent('frozen', 10_000);
    await run.locator('button.freeze').click();
    try {
      await frozenEvent;
      await expect(page.locator('header .alarms .caution', { hasText: '已凍結' })).toBeVisible({ timeout: 3000 });
      await expect(detail).toHaveCount(0);
      const resumedEvent = core.waitEvent('resumed', 10_000);
      await run.locator('button.resume').click();
      await resumedEvent;
    } finally {
      await ensureRunning(core);
    }
    errors.expectNone();
    await context.close();
  } finally {
    fs.writeFileSync(env.shipStatusFile, originalShipStatus);
    await browser.close();
    await ensureRunning(core);
  }
});

test('重播檢視讀超過上限的核心紀錄(當場合成 9,000 筆、約 27 MB;1366×768):讀取中顯示百分比,資訊列寫明降採樣,時間軸 / 逐筆 / 事件跳轉可用,版面判定通過', async ({ page, env }) => {
  // 第三十一輪:重播檢視原本 file.text() 整份讀成字串再整份解析、每筆保留完整狀態(2 小時的核心紀錄 1.35 GB 讀到 +1.2 GB 時分頁當掉)。
  // 現在分段讀取(lib/replay-states.ts);顯示用的狀態最多 7201 筆,超過時每 N 筆取 1 筆並在資訊列寫明,摘要 / 事件 / 航跡仍以全部狀態計算。
  // 合成:E13 夾具(拖船、泊位、時間表事件)的狀態行循環複製成 25 Hz × 9,000 筆(360 s),原本的事件 / summary / footer 行照 tick 的位置放回。
  test.setTimeout(120_000);
  const lines = fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E13_tug_berthing_mailiao-core.jsonl'), 'utf8').split('\n').filter((l) => l.trim());
  const header = lines.find((l) => l.startsWith('{"kind":"header"'))!;
  const states = lines.filter((l) => l.startsWith('{"kind":"state"')).map((l) => JSON.parse(l) as { kind: string; state: { t: number; tick: number; rudder: number; pos: { x: number; y: number } } });
  const events = lines.filter((l) => l.startsWith('{"kind":"event"')).map((l) => ({ line: l, tick: (JSON.parse(l) as { tick: number }).tick }));
  const tail = lines.filter((l) => l.startsWith('{"kind":"summary"') || l.startsWith('{"kind":"footer"'));
  expect(states.length).toBeGreaterThan(0);
  expect(events.length).toBeGreaterThan(3);
  const COUNT = 9000;
  const record = path.join(OUT, 'tmp', 'replay-over-limit.jsonl');
  fs.mkdirSync(path.dirname(record), { recursive: true });
  const fd = fs.openSync(record, 'w');
  let bytes = fs.writeSync(fd, `${header}\n`);
  let nextEvent = 0;
  let hardOver = 0;
  for (let k = 1; k <= COUNT; ) {
    const batch: string[] = [];
    for (let i = 0; i < 500 && k <= COUNT; i++, k++) {
      const tick = 2 * k;
      // 核心在同一個 tick 先寫事件行、再寫狀態行
      while (nextEvent < events.length && events[nextEvent]!.tick <= tick) batch.push(events[nextEvent++]!.line);
      const s = states[k % states.length]!;
      s.state.tick = tick;
      s.state.t = 0.04 * k;
      s.state.pos.x = 3 * 0.04 * k;
      s.state.pos.y = k % 2 ? 6 : 0;
      // 奇數筆滿舵(每 2 筆取 1 筆留下的是奇數筆之外的那些:序號 0 = k 1 …;摘要要算全部)
      s.state.rudder = k % 2 === 0 ? 35 : 0;
      if (k % 2 === 0) hardOver++;
      batch.push(JSON.stringify(s));
    }
    bytes += fs.writeSync(fd, `${batch.join('\n')}\n`);
  }
  bytes += fs.writeSync(fd, `${tail.join('\n')}\n`);
  fs.closeSync(fd);
  expect(nextEvent, '夾具的事件都在合成的時間範圍內').toBe(events.length);
  expect(bytes).toBeGreaterThan(8 * 1024 * 1024);

  const errors = new PageErrors(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env, { mode: 'replay' }));
  const view = page.locator('brm-replay-view');
  // 重播檢視自己的工具列(航跡圖裡也有一個 .toolbar,排在後面)
  const toolbar = view.locator('.toolbar').first();
  // 先載入一份小紀錄:讀大檔的期間原本的紀錄還在、頁面可以操作
  await view.locator('input.file-input').setInputFiles(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E14_unberthing_no_tug-core.jsonl'));
  await expect(view.locator('.toolbar .info').last()).toContainText('E14_unberthing_no_tug');
  // 頁面內:記下工具列出現過的讀取進度與當時資訊列顯示的紀錄(MutationObserver),以及各畫格(requestAnimationFrame)看到的進度 ——
  // 畫格看得到小於 100% 的進度,表示讀取中有讓出主執行緒、畫面有在更新(不是整段停住到讀完)
  await page.evaluate(() => {
    const w = window as unknown as { __progress: string[]; __during: string[]; __frames: string[]; __stop: () => void };
    w.__progress = [];
    w.__during = [];
    w.__frames = [];
    const root = document.querySelector('brm-instructor-app')!.shadowRoot!.querySelector('brm-replay-view')!.shadowRoot!;
    const loadingText = (): string => root.querySelector('.toolbar .loading')?.textContent ?? '';
    new MutationObserver(() => {
      const t = loadingText();
      if (t && w.__progress[w.__progress.length - 1] !== t) {
        w.__progress.push(t);
        w.__during.push(root.querySelector('.toolbar')?.textContent ?? '');
      }
    }).observe(root, { subtree: true, childList: true, characterData: true });
    let raf = 0;
    const frame = (): void => {
      const t = loadingText();
      if (t && w.__frames[w.__frames.length - 1] !== t) w.__frames.push(t);
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    w.__stop = () => cancelAnimationFrame(raf);
  });
  await view.locator('input.file-input').setInputFiles(record);
  const decimated = view.locator('.toolbar .decimated');
  await expect(decimated).toHaveText('狀態筆數超過 7,201:畫面每 2 筆取 1 筆(約每 0.08 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 9,000 筆計算', { timeout: 60_000 });
  await expect(toolbar).toContainText('replay-over-limit.jsonl · SimCore JSON Lines · E13_tug_berthing_mailiao');
  await expect(toolbar).toContainText('9000 筆狀態 · ');
  await expect(view.locator('.toolbar .loading')).toHaveCount(0);
  await expect(view.locator('.toolbar .error')).toHaveCount(0);
  const seen = await page.evaluate(() => {
    const w = window as unknown as { __progress: string[]; __during: string[]; __frames: string[]; __stop: () => void };
    w.__stop();
    return { progress: w.__progress, during: w.__during, frames: w.__frames };
  });
  const total = (bytes / 1048576).toFixed(bytes < 10 * 1048576 ? 1 : 0);
  const percents = seen.progress.map((t) => new RegExp(`^讀取 replay-over-limit\\.jsonl… (\\d+)%\\(共 ${total} MB\\)$`).exec(t)).filter((m): m is RegExpExecArray => m !== null).map((m) => Number(m[1]));
  expect(percents.length, `讀取中出現過的進度文字:${seen.progress.join(' | ')}`).toBeGreaterThanOrEqual(2);
  expect([...percents].sort((a, b) => a - b), '進度遞增').toEqual(percents);
  expect(percents.filter((p) => p < 100).length, '工具列出現過小於 100% 的進度').toBeGreaterThanOrEqual(1);
  const framePercents = seen.frames.map((t) => /… (\d+)%/.exec(t)).filter((m): m is RegExpExecArray => m !== null).map((m) => Number(m[1]));
  expect(framePercents.filter((p) => p < 100).length, `畫格看到的進度(讀取中有讓出主執行緒):${seen.frames.join(' | ')}`).toBeGreaterThanOrEqual(1);
  expect(seen.during.every((t) => t.includes('E14_unberthing_no_tug')), '讀取中原本載入的紀錄還在').toBe(true);

  // 時間軸:範圍是全部狀態的第一筆到最後一筆;拖到中間
  const time = view.locator('.timeline .t');
  await expect(time).toHaveText('00:00 / 06:00');
  const slider = view.locator('.timeline input.slider');
  await slider.fill('180');
  await expect(time).toHaveText('03:00 / 06:00');
  // 逐筆:鈕的說明寫了間隔;一次走一筆留下的狀態(0.08 秒)。由整數秒的時刻起逐筆 3 次、退 1 次
  const next = view.locator('.timeline .btn-row button[title="下一筆(約 0.08 秒)"]');
  const prev = view.locator('.timeline .btn-row button[title="上一筆(約 0.08 秒)"]');
  await expect(next).toHaveCount(1);
  await expect(prev).toHaveCount(1);
  const now = () => view.evaluate((el) => (el as unknown as { time: number }).time);
  const shownTick = () => view.evaluate((el) => (el.shadowRoot!.querySelector('brm-conning-bar') as unknown as { state: { tick: number } }).state.tick);
  // 留下的是序號 0、2、4 …(k = 1、3、5 …,t = 0.04、0.12、0.20 …)與最後一筆;t = 180 落在 k = 4500(沒留下),顯示的是 k = 4499
  expect(await shownTick()).toBe(2 * 4499);
  await next.click();
  expect(await now()).toBeCloseTo(0.04 * 4501, 6);
  expect(await shownTick()).toBe(2 * 4501);
  await next.click();
  await next.click();
  expect(await now()).toBeCloseTo(0.04 * 4505, 6);
  await prev.click();
  expect(await now()).toBeCloseTo(0.04 * 4503, 6);
  expect(await shownTick()).toBe(2 * 4503);
  // 時間軸的方向鍵(審查後修正 MEM-4):降採樣時走逐筆,每按一次換一筆留下的狀態。原本滑桿一格 0.04 秒、留下的狀態相隔 0.08 秒,
  // 第一次按下去(180.12 → 180.16,k = 4504 沒留下)畫面不變;2 小時的紀錄要按 32 次才換一筆
  await slider.focus();
  await page.keyboard.press('ArrowRight');
  expect(await shownTick()).toBe(2 * 4505);
  await page.keyboard.press('ArrowRight');
  expect(await shownTick()).toBe(2 * 4507);
  expect(await now()).toBeCloseTo(0.04 * 4507, 6);
  await page.keyboard.press('ArrowLeft');
  expect(await shownTick()).toBe(2 * 4505);
  await expect(time).toHaveText('03:00 / 06:00');
  // 滑桿的位置跟著時刻;PageDown 等其他按鍵照舊由瀏覽器處理(往回一大步)
  expect(Number(await slider.inputValue())).toBeCloseTo(0.04 * 4505, 2);
  await page.keyboard.press('PageDown');
  expect(await now()).toBeLessThan(0.04 * 4505 - 1);
  // 事件跳轉:點事件串流的列跳到事件的時刻(不是留下的狀態的時刻);時間軸上的事件標記也點得到
  const stream = view.locator('brm-event-stream');
  const row = stream.locator('.list .item').last();
  await row.scrollIntoViewIfNeeded();
  const rowTime = (await row.locator('.time').innerText()).trim();
  await row.click();
  await expect(time).toHaveText(`${rowTime} / 06:00`);
  expect(await now()).toBeLessThan(60);
  const marks = view.locator('.marks .mark:not(.phase)');
  expect(await marks.count()).toBeGreaterThan(3);
  await slider.fill('300');
  await expect(time).toHaveText('05:00 / 06:00');
  await marks.last().click({ position: { x: 1, y: 6 }, force: true });
  await expect(time).not.toHaveText('05:00 / 06:00');
  // 最後一筆一律保留:拖到底顯示 k = 9000
  await slider.fill('360');
  await expect(time).toHaveText('06:00 / 06:00');
  expect(await shownTick()).toBe(2 * COUNT);
  // 講評摘要與航跡以全部狀態計算:狀態 9,000 筆、滿舵次數 = 全部狀態中的次數(留下來的狀態都不是滿舵的那些);航跡每 1 秒一點
  const derived = await view.evaluate((el) => {
    const root = el.shadowRoot!;
    const summary = (root.querySelector('brm-debrief-summary') as unknown as { summary: { samples: number; hardOverCount: number } }).summary;
    const data = (el as unknown as { data: { states: Array<{ rudder: number }>; track: unknown[]; stateStats: { total: number; shown: number; stride: number } } }).data;
    return { samples: summary.samples, hardOver: summary.hardOverCount, track: data.track.length, stats: data.stateStats, shownHardOver: data.states.filter((s) => Math.abs(s.rudder) >= 34.5).length };
  });
  expect(derived.stats).toMatchObject({ total: COUNT, shown: 4501, stride: 2 });
  expect(derived.samples).toBe(COUNT);
  expect(derived.hardOver).toBe(hardOver);
  expect(derived.shownHardOver, '留下來顯示的狀態中滿舵的只有另外保留的最後一筆').toBe(1);
  expect(derived.track).toBeGreaterThanOrEqual(355);
  // 版面:資訊列多了降採樣的說明(折成多行)後,時間軸、播放 / 逐筆鈕、事件串流篩選鈕仍不必捲動就點得到
  const report = await page.evaluate(probeLayout, REPLAY_LAYOUT);
  expect(layoutFailures(report), '重播檢視 1366×768(超過上限的紀錄,資訊列有降採樣說明)的版面問題').toEqual([]);
  const box = (await decimated.boundingBox())!;
  expect(box.width, '降採樣的說明沒有被壓成一字一行').toBeGreaterThan(200);
  // 其他視窗(1366×768 在 125 % 縮放的 1093×614、1920×1080):說明多佔一行後版面判定仍通過
  for (const [width, height] of [[1093, 614], [1920, 1080]] as const) {
    await page.setViewportSize({ width, height });
    await expect(decimated).toBeVisible();
    expect(layoutFailures(await page.evaluate(probeLayout, REPLAY_LAYOUT)), `重播檢視 ${width}×${height}(超過上限的紀錄)的版面問題`).toEqual([]);
  }
  await page.setViewportSize({ width: 1366, height: 768 });
  // 播放照常
  const play = view.locator('.timeline button.play');
  await slider.fill('10');
  await play.click();
  await expect(play).toHaveText('暫停');
  await expect.poll(now).toBeGreaterThan(10.2);
  await play.click();
  await expect(play).toHaveText('播放');
  errors.expectNone();
  fs.rmSync(record, { force: true });
});

test('完整教官站的「重播檢視」分頁讀同一種紀錄(1366×768):與 ?mode=replay 相同的資訊列與時間軸;切回即時再切回來,紀錄與時刻還在', async ({ page, env, core }) => {
  // 重播檢視在完整教官站是常駐的元件(以 hidden 切換):檔案選擇走同一條分段讀取的路。核心照常連線。
  test.setTimeout(120_000);
  await ensureRunning(core);
  const lines = fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E01-core.jsonl'), 'utf8').split('\n').filter((l) => l.trim());
  const header = lines.find((l) => l.startsWith('{"kind":"header"'))!;
  const states = lines.filter((l) => l.startsWith('{"kind":"state"')).map((l) => JSON.parse(l) as { kind: string; state: { t: number; tick: number } });
  const record = path.join(OUT, 'tmp', 'replay-over-limit-full.jsonl');
  fs.mkdirSync(path.dirname(record), { recursive: true });
  const out: string[] = [header];
  for (let k = 0; k < 7300; k++) {
    const s = states[k % states.length]!;
    s.state.tick = 2 * k;
    s.state.t = 0.04 * k;
    out.push(JSON.stringify(s));
  }
  fs.writeFileSync(record, `${out.join('\n')}\n`);
  const errors = new PageErrors(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  await page.locator('button.view-replay').click();
  const view = page.locator('brm-replay-view');
  const toolbar = view.locator('.toolbar').first();
  await view.locator('input.file-input').setInputFiles(record);
  await expect(view.locator('.toolbar .decimated')).toHaveText('狀態筆數超過 7,201:畫面每 2 筆取 1 筆(約每 0.08 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 7,300 筆計算', { timeout: 60_000 });
  await expect(toolbar).toContainText('7300 筆狀態 · ');
  const time = view.locator('.timeline .t');
  await expect(time).toHaveText('00:00 / 04:51');
  await view.locator('.timeline input.slider').fill('120');
  await expect(time).toHaveText('02:00 / 04:51');
  const report = await page.evaluate(probeLayout, REPLAY_LAYOUT);
  expect(layoutFailures(report), '完整教官站的重播檢視分頁 1366×768(超過上限的紀錄)的版面問題').toEqual([]);
  // 切回即時、再切回重播:載入的紀錄與時間軸位置不變
  await page.locator('.seg[aria-label="檢視"] button', { hasText: '即時' }).click();
  await expect(view).toBeHidden();
  await page.locator('button.view-replay').click();
  await expect(view).toBeVisible();
  await expect(time).toHaveText('02:00 / 04:51');
  await expect(toolbar).toContainText('7300 筆狀態 · ');

  // 審查後修正(DATA-1 / MEM-1):超不超過上限看練習最終的歷史。同樣的 7,300 筆之後倒帶回 tick 2000(第 1,001 筆)再跑 2,000 筆:
  // 最終 3,001 筆 < 7,201 → 每一筆都在、沒有降採樣的說明、逐筆一次 0.04 秒(原本沿用倒帶前的間隔:1,501 筆、每 2 筆取 1 筆)
  const rewound = path.join(OUT, 'tmp', 'replay-rewound-under-limit.jsonl');
  const more: string[] = [...out, JSON.stringify({ kind: 'input', tick: 2 * 7299, command: { type: 'restore', args: { tick: 2000 } } })];
  for (let k = 1001; k <= 3000; k++) {
    const s = states[k % states.length]!;
    s.state.tick = 2 * k;
    s.state.t = 0.04 * k;
    more.push(JSON.stringify(s));
  }
  fs.writeFileSync(rewound, `${more.join('\n')}\n`);
  await view.locator('input.file-input').setInputFiles(rewound);
  await expect(toolbar).toContainText('replay-rewound-under-limit.jsonl · SimCore JSON Lines', { timeout: 60_000 });
  await expect(toolbar).toContainText('3001 筆狀態 · 1 筆輸入');
  await expect(view.locator('.toolbar .decimated')).toHaveCount(0);
  await expect(time).toHaveText('00:00 / 02:00');
  const rewindMark = view.locator('.marks .mark.rewind');
  await expect(rewindMark).toHaveCount(1);
  expect(await rewindMark.getAttribute('title')).toContain('丟棄 6299 筆狀態');
  const stats = await view.evaluate((el) => (el as unknown as { data: { states: unknown[]; stateStats: { total: number; shown: number; stride: number } } }).data.stateStats);
  expect(stats).toMatchObject({ total: 3001, shown: 3001, stride: 1 });
  const nextPlain = view.locator('.timeline .btn-row button[title="下一筆"]');
  await expect(nextPlain).toHaveCount(1);
  await nextPlain.click();
  await nextPlain.click();
  expect(await view.evaluate((el) => (el as unknown as { time: number }).time)).toBeCloseTo(0.08, 6);
  expect(await view.evaluate((el) => (el.shadowRoot!.querySelector('brm-conning-bar') as unknown as { state: { tick: number } }).state.tick)).toBe(4);
  errors.expectNone();
  fs.rmSync(record, { force: true });
  fs.rmSync(rewound, { force: true });
});
