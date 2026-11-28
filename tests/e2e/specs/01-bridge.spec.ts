// 操船台(index.html):載入後 5 s 內收到核心狀態幀,HDG/SOG 顯示數值(不是 --- / --.-),連線藥丸為「已連線」,無頁面錯誤;
// 文書筆電的視窗大小(1280×720、1366×768、1536×864;失電後 header 折成兩行)下每個控制項都點得到、VHF 文字欄 / DSC 不必捲動(版面;判定在 support/layout.ts);
// 麥克風被拒時(1280×720、1366×768 失電後)PTT 鈕上寫「無麥克風」、DSC 仍不必捲動,VHF 訊息紀錄列貼底(看得到最新兩則);
// 1093×614(1366×768 在 125 % 縮放)的非關鍵判定(車鐘 / 艏側推鈕內文字、浮貼外力讀數不超出航跡圖;E09 失電後警報標題列一行、
// 2 筆警報完整可見、meta 不截斷);1280×720 的浮貼外力讀數放得下時不攔滑鼠(讀數中央的滾輪縮放、拖曳平移航跡圖);
// 1920×1080(conning 條單列 10 至 13 格):真實核心的狀態與極端數值下讀數列各方塊不被水平裁切,字距加大 3 % 仍通過(第三十一輪);
// 審查後補:顯示的內容沒有少(浪向玫瑰、船速方塊的中文標籤)、橫搖角警報等級看得到字、UKC 六個字元與沒有空白的長字串不被裁切。
// 1366×768、1536×864 與 1093×614(conning 條兩列;船上文書筆電與教官筆電實際看到的排法):平常會出現的數值與狀態下
// 讀數列各方塊不被方塊邊界水平或垂直裁切(第三十一輪)。審查後補:航線方塊速限的數值緊接在自己的標籤後面、擱淺時 UKC 的大字在紅底裡上下置中。
import type { Locator, Page } from '@playwright/test';
import { bridgeUrl, instructorUrl } from '../support/env.ts';
import { ensureRunning, ensureScenario, expect, scenarioPath, test } from '../support/fixtures.ts';
import {
  CONNING_NONE, CONNING_ROLL, CONNING_ROUTE, CONNING_ROUTE_USUAL, CONNING_SEA, CONNING_TWO_ROW_ASTERN, CONNING_TWO_ROW_ROUTE, CONNING_TWO_ROW_SEA,
  CONNING_TWO_ROW_STATUSES, CONNING_UNDERWAY, CONNING_USUAL, CONNING_WIDEST,
} from '../support/conning-values.ts';
import { BRIDGE_LAYOUT, layoutFailures, overrideConning, probeLayout, type ConningReport } from '../support/layout.ts';
import { PageErrors, expectConningSingleRow, expectConningTwoRows, waitConnected } from '../support/pages.ts';
import type { CoreClient } from '../support/ws.ts';

/** E09 強制觸發全船失電,等 ≥ 2 筆警報與 ≥ 2 個故障(header 折成兩行、右欄最吃高度的情況) */
async function forceBlackout(core: CoreClient): Promise<void> {
  core.send({ type: 'triggerTimeline', args: { id: 'blackout', force: true } });
  await core.waitState((s) => Array.isArray(s['alerts']) && (s['alerts'] as unknown[]).length >= 2 && Array.isArray(s['faults']) && (s['faults'] as unknown[]).length >= 2, 20_000, '至少 2 筆警報與 2 個故障的狀態幀');
}

/** 還原:重新載入原情境(清掉強制注入的失電;不論斷言結果),不影響後續案例 */
async function restoreScenario(core: CoreClient, before: string | undefined, fallback: string): Promise<void> {
  const id = before ?? fallback;
  core.send({ type: 'loadScenario', value: scenarioPath(id), args: { id, path: scenarioPath(id) } });
  await core.waitState((st) => st.scenario?.id === id && st.t < 5, 30_000, `重新載入 ${id} 後的狀態幀(模擬時間歸零)`);
  await ensureRunning(core);
}

/** 操船台載入失電後的畫面:≥ 2 筆警報、header ≥ 2 個故障徽章、VHF 已連線 */
async function openBridgeAfterBlackout(page: Page, env: Parameters<typeof bridgeUrl>[0]): Promise<Locator> {
  await page.goto(bridgeUrl(env));
  await waitConnected(page);
  await expect.poll(() => page.locator('brm-alert-panel .alert').count(), { message: '警報面板列出的警報數' }).toBeGreaterThanOrEqual(2);
  await expect.poll(() => page.locator('header .alarms .alarm', { hasText: '故障' }).count(), { message: 'header 的故障徽章數' }).toBeGreaterThanOrEqual(2);
  const vhf = page.locator('brm-vhf');
  await expect(vhf.locator('.pill')).toHaveText('已連線');
  return vhf;
}

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
    await forceBlackout(core);
    for (const size of [{ width: 1280, height: 720 }, { width: 1366, height: 768 }, { width: 1536, height: 864 }]) {
      await test.step(`${size.width}×${size.height}`, async () => {
        await page.setViewportSize(size);
        await openBridgeAfterBlackout(page, env);
        await expect(page.locator('brm-vhf button.ptt')).toBeVisible();
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
    await restoreScenario(core, before, 'E09_blackout');
  }
});

test('操船台 1280×720 與 1366×768(失電後、麥克風被拒):PTT 寫「無麥克風」,DISTRESS / URGENCY / 文字欄 / 送出不必捲動;VHF 訊息紀錄列貼底、看得到最新兩則', async ({ page, env, core }) => {
  // 第二十九輪 L4:麥克風被拒(或沒有麥克風 API)時原本固定多一列警示(約 26 px),1280×720 失電後右欄超出約 25 px,
  // DISTRESS / URGENCY 落到視窗外 — 正是文字與 DSC 成為唯一通訊方式的情況(安裝文件第 2.7 節)。現在 PTT 鈕上直接寫短句、
  // 完整說明在 title,壓縮時不另佔一列。另:壓縮後的訊息紀錄列原本固定 30 px 且不捲到最新,只看得到最舊的「VHF 已連線」;
  // 現在約兩則高、收到新訊息時捲到最新一則。
  const errors = new PageErrors(page);
  // 瀏覽器 / Windows 拒絕麥克風(按 PTT 時 getUserMedia 以 NotAllowedError 失敗)
  await page.addInitScript(() => {
    const md = navigator.mediaDevices;
    if (md) md.getUserMedia = () => Promise.reject(new DOMException('Permission denied', 'NotAllowedError'));
  });
  const before = (await core.nextState()).scenario?.id;
  await ensureScenario(core, 'E09_blackout');
  // 另一站(教官站,同頻道 16)送文字與 DSC
  const instructor = await page.context().newPage();
  try {
    await forceBlackout(core);
    await instructor.goto(instructorUrl(env));
    await waitConnected(instructor);
    const vhfI = instructor.locator('brm-vhf');
    await expect(vhfI.locator('.status .pill')).toHaveText('已連線', { timeout: 15_000 });
    for (const size of [{ width: 1280, height: 720 }, { width: 1366, height: 768 }]) {
      await test.step(`${size.width}×${size.height}`, async () => {
        await page.setViewportSize(size);
        const vhf = await openBridgeAfterBlackout(page, env);
        // 按一次 PTT → 要求麥克風 → 被拒:鈕停用、鈕上寫短句,完整說明在 title;壓縮時警示不另佔一列
        const ptt = vhf.locator('button.ptt');
        await ptt.click();
        await expect(ptt).toContainText('無麥克風');
        await expect(ptt).toBeDisabled();
        await expect(ptt).toHaveAttribute('title', /無麥克風權限:只能以文字通訊/);
        await expect(vhf.locator('.hint.mic.warn')).toBeHidden();
        const report = await page.evaluate(probeLayout, BRIDGE_LAYOUT);
        expect(layoutFailures(report), `操船台 ${size.width}×${size.height}(麥克風被拒)的版面問題`).toEqual([]);
        // 他站送 3 則文字與 1 則 DSC URGENCY(無附文,單行):最新一則與前一則都完整落在紀錄列的可見範圍內(不必捲動)
        for (let i = 1; i <= 3; i++) {
          const text = `VTS ${size.width} 第 ${i} 則`;
          await vhfI.locator('.text-row input').fill(text);
          await vhfI.locator('.text-row button.send').click();
          await expect(vhf.locator('.log .item[data-kind="text"] .text', { hasText: text })).toHaveCount(1, { timeout: 5000 });
        }
        const dscBefore = await vhf.locator('.log .item[data-kind="dsc"]').count();
        await vhfI.locator('button.urgency').click();
        await vhfI.locator('button.urgency').click();
        await expect(vhf.locator('.log .item[data-kind="dsc"]')).toHaveCount(dscBefore + 1, { timeout: 5000 });
        await expect(vhf.locator('.log .item').last()).toContainText('URGENCY');
        await expect
          .poll(
            () =>
              vhf.locator('.log').evaluate((log) => {
                const lb = log.getBoundingClientRect();
                return [...log.querySelectorAll('.item')].slice(-2).map((it) => {
                  const b = it.getBoundingClientRect();
                  return b.top >= lb.top - 1 && b.bottom <= lb.bottom + 1;
                });
              }),
            { message: '紀錄列最後兩則(前一則、最新一則)是否完整可見' },
          )
          .toEqual([true, true]);
        // 真實點擊:文字欄與送出、DISTRESS(第一次只進入待確認)→ 取消、URGENCY(點擊前檢查)
        const text = vhf.locator('.text-row input');
        await text.click();
        await text.fill('版面測試');
        await vhf.locator('button.send').click({ trial: true });
        await text.fill('');
        const distress = vhf.locator('button.distress');
        await distress.click();
        await expect(distress).toContainText('再按一次送出 DISTRESS');
        await vhf.locator('.dsc button.cancel').click();
        await vhf.locator('button.urgency').click({ trial: true });
      });
    }
    errors.expectNone();
  } finally {
    await instructor.close();
    await restoreScenario(core, before, 'E09_blackout');
  }
});

/** 浮貼外力讀數的狀態:內容是否放不下([scrollable] 由元件以 ResizeObserver 設定)、是否接收滑鼠 */
async function readoutState(page: Page): Promise<{ scrollable: boolean; pointerEvents: string; scrollHeight: number; clientHeight: number; plotHeight: number }> {
  return page.locator('brm-external-readout').evaluate((el) => ({
    scrollable: el.hasAttribute('scrollable'),
    pointerEvents: getComputedStyle(el).pointerEvents,
    scrollHeight: el.scrollHeight,
    clientHeight: el.clientHeight,
    plotHeight: Math.round((el.parentElement ?? el).getBoundingClientRect().height),
  }));
}

/** 航跡圖的縮放倍率(元件內部狀態;只用來比較滾輪前後) */
async function plotZoom(page: Page): Promise<number> {
  return page.locator('brm-track-plot').evaluate((el) => (el as unknown as { zoom: number }).zoom);
}

test('操船台 1093×614(1366×768 在 125 % 縮放;E13 與 E09 失電後):鈕內文字不被切掉、浮貼外力讀數不超出航跡圖、警報標題列一行且 2 筆警報完整可見,非關鍵判定通過', async ({ page, env, core }) => {
  // 第二十九輪 L4:右欄跨到操船台那一列後,1093 寬時車鐘格只剩 125 px,「EFAS 緊急全速倒車 −150」等鈕內文字被 overflow: hidden 切掉、
  // 艏側推快速鈕的「100」溢出鈕外;航跡圖只剩約 100 px,拖船 / 靠泊讀數溢出到下方、蓋住 BNWAS。這個尺寸只要求非關鍵項通過
  // (關鍵控制可以要捲動;判定 1 仍檢查捲得到)。
  // 第二十九輪 L5:L4 為了車鐘 / 艏側推把右欄縮成 300 px,失電後警報面板的標題列折成兩行、警報清單 122 → 94 px(第 2 筆要捲動)、
  // 第 1 筆警報的 meta「… · 00:00:03」被 … 截斷。現在右欄 332 px、操船台五格的左右內距縮小,meta 放不下時換行(不截斷)。
  const errors = new PageErrors(page);
  const id = 'E13_tug_berthing_mailiao';
  const before = (await core.nextState()).scenario?.id;
  await ensureScenario(core, id);
  try {
    await page.setViewportSize({ width: 1093, height: 614 });
    await test.step('E13', async () => {
      await page.goto(bridgeUrl(env));
      await waitConnected(page);
      // E13 有拖船:航跡圖左上角的外力讀數會出現
      await expect(page.locator('brm-external-readout .box')).toBeVisible({ timeout: 10_000 });
      const report = await page.evaluate(probeLayout, BRIDGE_LAYOUT);
      expect(report.text, '鈕內文字被切掉或溢出').toEqual([]);
      expect(layoutFailures(report).filter((f) => !f.startsWith('關鍵控制:')), '操船台 1093×614 的非關鍵版面問題').toEqual([]);
      // 航跡圖約 100 px、讀數放不下:讀數自己捲動並接收滑鼠([scrollable])
      await expect.poll(async () => (await readoutState(page)).scrollable, { message: '讀數放不下時標示 [scrollable]' }).toBe(true);
      const ro = await readoutState(page);
      expect(ro.scrollHeight, '讀數內容高(px)').toBeGreaterThan(ro.clientHeight + 1);
      expect(ro.pointerEvents, '放不下時讀數接收滑鼠(捲得動)').toBe('auto');
      // 車鐘(緊急全速倒車)與艏側推快速鈕真實點擊前檢查
      await page.locator('brm-telegraph .list button', { hasText: 'EFAS' }).click({ trial: true });
      await page.locator('brm-thruster-lever .quick button', { hasText: 'P 100' }).click({ trial: true });
      // 自動舵面板(第二十九輪第五次審查):「ROT 限制 [15] °/min」一行約 143 px、面板內容寬 142 px — 「°/min」曾超出面板右緣;
      // 判定 6 只看鈕內文字,這裡直接量面板內各元素的右緣(面板出現捲軸時以捲軸內側為界)
      const apOut = await page.locator('brm-autopilot').evaluate((host) => {
        const panel = host.shadowRoot!.querySelector('.panel')!;
        const inner = panel.getBoundingClientRect().left + panel.clientLeft + panel.clientWidth;
        return [...panel.querySelectorAll('*')]
          .map((e) => ({ e, b: e.getBoundingClientRect() }))
          .filter(({ b }) => b.width > 0 && b.right > inner + 0.5)
          .map(({ e, b }) => `${e.tagName.toLowerCase()}「${(e.textContent ?? '').trim().slice(0, 12)}」 +${Math.round(b.right - inner)} px`);
      });
      expect(apOut, '自動舵面板內超出右緣的元素').toEqual([]);
    });
    await test.step('E09 失電後', async () => {
      await ensureScenario(core, 'E09_blackout');
      await forceBlackout(core);
      await openBridgeAfterBlackout(page, env);
      const report = await page.evaluate(probeLayout, BRIDGE_LAYOUT);
      expect(report.text, '鈕內文字被切掉或溢出').toEqual([]);
      expect(layoutFailures(report).filter((f) => !f.startsWith('關鍵控制:')), '操船台 1093×614 失電後的非關鍵版面問題').toEqual([]);
      const geo = await page.locator('brm-alert-panel').evaluate((host) => {
        const root = host.shadowRoot!;
        const head = [...root.querySelector('.head')!.children].map((c) => c.getBoundingClientRect());
        const list = root.querySelector('.list')!.getBoundingClientRect();
        const items = [...root.querySelectorAll('.alert')].map((a) => a.getBoundingClientRect());
        return {
          headSpan: Math.max(...head.map((b) => b.bottom)) - Math.min(...head.map((b) => b.top)),
          headItem: Math.max(...head.map((b) => b.height)),
          listHeight: list.height,
          full: items.filter((b) => b.top >= list.top - 1 && b.bottom <= list.bottom + 1).length,
          // meta / 警報文字:沒有被截斷(scrollWidth ≤ clientWidth + 1,且不用 text-overflow: ellipsis)
          cut: [...root.querySelectorAll('.alert .meta, .alert .text')]
            .filter((m) => m.scrollWidth > m.clientWidth + 1 || getComputedStyle(m).textOverflow === 'ellipsis')
            .map((m) => `${m.className}「${m.textContent}」 ${m.scrollWidth} > ${m.clientWidth}`),
        };
      });
      expect(geo.headSpan, '警報面板標題列一行(最高的項目 + 2 px 以內)').toBeLessThanOrEqual(geo.headItem + 2);
      expect(geo.full, `警報清單完整可見的筆數(清單 ${Math.round(geo.listHeight)} px)`).toBeGreaterThanOrEqual(2);
      expect(geo.cut, '警報 meta / 文字被截斷').toEqual([]);
    });
    errors.expectNone();
  } finally {
    await restoreScenario(core, before, id);
  }
});

test('操船台 1280×720(E13):浮貼外力讀數放得下時不攔滑鼠,讀數中央的滾輪縮放航跡圖、拖曳平移', async ({ page, env, core }) => {
  // 第二十九輪 L5:L4 讓讀數在航跡圖矮於 200 px 時接收滑鼠(讓使用者捲得動);1280×720 的航跡圖約 194 px、讀數其實放得下
  // (scrollHeight = clientHeight),讀數範圍內(約占航跡圖 1/4)的滾輪縮放與拖曳平移卻都失效。現在只在讀數真的放不下時才接收滑鼠。
  const errors = new PageErrors(page);
  const id = 'E13_tug_berthing_mailiao';
  const before = (await core.nextState()).scenario?.id;
  await ensureScenario(core, id);
  try {
    await page.setViewportSize({ width: 1280, height: 720 });
    await page.goto(bridgeUrl(env));
    await waitConnected(page);
    const box = page.locator('brm-external-readout .box');
    await expect(box).toBeVisible({ timeout: 10_000 });
    await page.waitForTimeout(500);
    const ro = await readoutState(page);
    expect(ro.scrollHeight, `讀數放得下(航跡圖 ${ro.plotHeight} px)`).toBeLessThanOrEqual(ro.clientHeight + 1);
    expect(ro.scrollable, '放得下時不標示 [scrollable]').toBe(false);
    expect(ro.pointerEvents, '放得下時讀數不攔滑鼠').toBe('none');
    // 讀數中央(讀數高的一半,最多 40 px 處):滾輪 → 航跡圖放大;拖曳 → 不再置中跟隨(平移)
    const b = (await page.locator('brm-external-readout').boundingBox())!;
    const x = b.x + b.width / 2;
    const y = b.y + Math.min(b.height / 2, 40);
    const zoom0 = await plotZoom(page);
    await page.mouse.move(x, y);
    await page.mouse.wheel(0, -300);
    await expect.poll(() => plotZoom(page), { message: '讀數中央滾輪後的航跡圖縮放倍率' }).toBeGreaterThan(zoom0);
    const recenter = page.locator('brm-track-plot .toolbar button', { hasText: '置中' });
    await expect(recenter).toHaveClass(/active/);
    await page.mouse.down();
    await page.mouse.move(x + 40, y + 10, { steps: 5 });
    await page.mouse.up();
    await expect(recenter, '讀數中央拖曳後航跡圖平移(不再置中跟隨)').not.toHaveClass(/active/);
    await recenter.click();
    errors.expectNone();
  } finally {
    if (before && before !== id) await restoreScenario(core, before, id);
  }
});

test('操船台 1920×1080(conning 條單列 10 至 13 格):真實核心的狀態與極端數值下,讀數列各方塊不被水平裁切,字距加大 3 % 仍通過', async ({ page, env, core }) => {
  // 第三十一輪:視窗寬於 1900 px 時 conning 條排成單列,13 格時每格只有約 117 至 160 px。原本各格的寬度比例與固定的字級(大字 56 px)
  // 讓主機轉速「-150」、UKC「19.2」、艏側推「P 100%」、航線偏移、凍結時的模擬時間、「GNSS 無效」的標題等比方塊寬,被方塊邊界切掉;
  // 「有浪、有橫搖、沒有航線」的組合還對錯了格(時間格只有 134 px)。現在每格 =「基本寬度 + 依比例分到的多餘寬度」,放不下的內容換行。
  // 判定在 support/pages.ts 的 expectConningSingleRow(版面判定通過、排成一列、內容不比方塊高、字距加大 3 % 後判定 4 仍通過)。
  const errors = new PageErrors(page);
  await ensureRunning(core);
  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.goto(bridgeUrl(env));
  await waitConnected(page);
  await expect(page.locator('brm-heading .hdg .value')).toHaveText(/^\d{3}$/);
  await expect(page.locator('brm-vhf .pill')).toHaveText('已連線');
  // 1) 真實核心目前的狀態(不論前面的案例留下哪一個情境)
  const live = await expectConningSingleRow(page, BRIDGE_LAYOUT, '操船台 1920×1080(核心目前的狀態)');
  expect(live.tiles.length, '格數').toBeGreaterThanOrEqual(10);

  // 2) 極端但合理的數值(support/conning-values.ts)。選填欄位決定格數:航線(或能見度)、浪 / 船間交互作用 / 浪一階運動、橫搖
  const route = CONNING_ROUTE;
  const sea = CONNING_SEA;
  const roll = CONNING_ROLL;
  const none = CONNING_NONE;
  const widest = CONNING_WIDEST;
  const usual = CONNING_USUAL;
  const usualRoute = CONNING_ROUTE_USUAL;
  const combos: Array<{ name: string; cells: number; extra: Record<string, unknown> }> = [
    { name: '10 格', cells: 10, extra: {} },
    { name: '11 格(航線)', cells: 11, extra: { route, visibility_nm: 0.2 } },
    { name: '12 格(航線、浪)', cells: 12, extra: { route, visibility_nm: 10, ...sea } },
    { name: '12 格(浪、橫搖;沒有航線)', cells: 12, extra: { ...sea, roll } },
    { name: '13 格(航線、浪、橫搖)', cells: 13, extra: { route, visibility_nm: 10, ...sea, roll } },
  ];
  for (const combo of combos) {
    await test.step(combo.name, async () => {
      // 最寬的數值
      expect(await page.evaluate(overrideConning, { patch: { ...none, ...widest, ...combo.extra }, config: { rudderMax_deg: 70 }, setHeading: 359 })).toBe(combo.cells);
      await expect(page.locator('brm-engine .top .value.big')).toHaveText('-150');
      await expectConningSingleRow(page, BRIDGE_LAYOUT, `操船台 1920×1080 ${combo.name}、最寬的數值`, combo.cells);
      // 顯示的內容沒有少(審查後補;expectConningSingleRow 也檢查各方塊的圖形與文字都有顯示):浪向玫瑰在單列一律顯示;
      // 船速方塊 125 px 以上(10 至 12 格)標籤的中文部分看得到(第三十一輪的第一版在 150 px 以下省略,11 至 12 格因此少了中文標籤)
      if ('waves' in combo.extra) expect((await page.locator('brm-sea .rose').boundingBox())?.width ?? 0, `${combo.name}:浪向玫瑰的寬度(px)`).toBeGreaterThanOrEqual(24);
      const speedWidth = (await page.locator('brm-speed').boundingBox())!.width;
      if (combo.cells <= 12) expect(speedWidth, `${combo.name}:船速方塊的寬度(px)`).toBeGreaterThan(125);
      if (speedWidth > 125) for (const zh of ['對水', '對地', '航跡向']) await expect(page.locator('brm-speed .label .zh', { hasText: zh }), `${combo.name}:船速方塊的「${zh}」`).toBeVisible();
      // 橫搖角超過警報門檻(25°):警報色的字、沒有徽章的紅底(原本共用樣式的徽章底色加上元件自己的警報字色,紅底紅字,數值看不到)
      if ('roll' in combo.extra) {
        const angle = page.locator('brm-inclinometer .roll-angle');
        await expect(angle).toHaveClass(/alarm/);
        await expect(angle).toHaveText('P 35.5°');
        const colors = await angle.evaluate((el) => ({ color: getComputedStyle(el).color, background: getComputedStyle(el).backgroundColor }));
        expect(colors.background, '橫搖角(警報等級)沒有底色').toBe('rgba(0, 0, 0, 0)');
        expect(colors.color, '橫搖角(警報等級)的字色與方塊的底色不同').not.toBe(await page.locator('brm-inclinometer .tile').evaluate((el) => getComputedStyle(el).backgroundColor));
      }
      // 平常的位數:主要數值的字級不比第三十一輪之前小超過 10 %(之前:航向 50.5(13 格)至 56、轉速 56、UKC 56、ROT 28、船速 28、
      // 舵角 / 艏側推 20、時間 20、航線偏移 28 px);不是靠一路縮字塞進方塊
      const extra = 'route' in combo.extra ? { ...combo.extra, route: usualRoute } : combo.extra;
      expect(await page.evaluate(overrideConning, { patch: { ...none, ...usual, ...extra }, config: { rudderMax_deg: 35 }, issues: ['heading'] })).toBe(combo.cells);
      await expect(page.locator('brm-depth .main .value.big')).toHaveText('19.2');
      const bar = await expectConningSingleRow(page, BRIDGE_LAYOUT, `操船台 1920×1080 ${combo.name}、平常的位數`, combo.cells);
      // 鍵是「父元素的類別>數值的類別」(support/layout.ts 的 probeConning);狀態類別(warning、caution、alarm…)在後面,以開頭比對
      const font = (tag: string, prefix: string): number => {
        const tile = bar.tiles.find((t) => t.tag === tag);
        expect(tile, tag).toBeDefined();
        const key = Object.keys(tile!.fonts).find((k) => k.startsWith(prefix));
        expect(key, `${tag} 的數值 ${prefix}(量到的:${Object.keys(tile!.fonts).join('、')})`).toBeDefined();
        return tile!.fonts[key!]!;
      };
      expect(font('brm-heading', '.hdg>.value.big'), '航向的字級').toBeGreaterThanOrEqual(50.4);
      expect(font('brm-rot', '.tile>.readout.value.mid'), 'ROT 的字級').toBeGreaterThanOrEqual(25.2);
      expect(font('brm-speed', '.row>.value.mid'), 'STW / SOG 的字級').toBeGreaterThanOrEqual(25.2);
      expect(font('brm-rudder', '>.value.small'), '舵角的字級').toBeGreaterThanOrEqual(18);
      expect(font('brm-engine', '.top>.value.big'), '主機轉速的字級').toBeGreaterThanOrEqual(50.4);
      expect(font('brm-thruster', '>.value.small'), '艏側推的字級').toBeGreaterThanOrEqual(18);
      expect(font('brm-depth', '.main'), 'UKC 的字級').toBeGreaterThanOrEqual(50.4);
      expect(font('brm-clock', '.row>.value.small'), '時鐘的字級').toBeGreaterThanOrEqual(18);
      if ('route' in combo.extra) expect(font('brm-route', '.xtd>.value.mid'), '航線偏移的字級').toBeGreaterThanOrEqual(25.2);
    });
  }
  // 3) 尚未收到資料(noData:UKC 顯示 --.-、航向標「可疑」)的 13 格
  expect(await page.evaluate(overrideConning, { patch: { ...none, ...usual, route, visibility_nm: 10, ...sea, roll }, config: { rudderMax_deg: 70 }, noData: true })).toBe(13);
  await expect(page.locator('brm-depth .main .value.big')).toHaveText('--.-');
  await expectConningSingleRow(page, BRIDGE_LAYOUT, '操船台 1920×1080 13 格、尚未收到資料', 13);

  // 4) 再多一位數與沒有空白的長字串(13 格;審查後補。情境的水深、目標船代號、航點名稱都沒有上限):
  //    UKC 1000 m 以上(六個字元)、航線偏移六位數、目標船代號 13 個字元並超限、航點名稱 22 個字元 ——
  //    原本「1234.5」左右各被切約 3 px、「交互作用 <代號> 超限」的「超限」與航線方塊標題的名稱被方塊邊界切掉 6 至 23 px
  const long = {
    depthBelowKeel: 1234.5,
    waterDepth: 1240.6,
    shipInteraction: { active: true, targetId: 'EVER_GIVEN_02', forceSway_t: -123.4, momentYaw_tm: -12345, event: true },
    route: { ...CONNING_UNDERWAY.route, nextWaypoint: 'KAOHSIUNG_2ND_ENTRANCE', xtd_m: -123456, xtdLimit_m: 18520, distanceToWaypoint_nm: 12.34 },
  };
  expect(await page.evaluate(overrideConning, { patch: { ...CONNING_UNDERWAY, ...long }, setHeading: 74 })).toBe(13);
  await expect(page.locator('brm-depth .main .value.big')).toHaveText('1234.5');
  await expect(page.locator('brm-route .tile-title .zh')).toHaveText('→ KAOHSIUNG_2ND_ENTRANCE');
  await expect(page.locator('brm-sea .interaction .label')).toHaveText('交互作用 EVER_GIVEN_02 超限');
  await expect(page.locator('brm-route .xtd .value')).toHaveText('123456m');
  const longBar = await expectConningSingleRow(page, BRIDGE_LAYOUT, '操船台 1920×1080 13 格、多一位數與長字串', 13);
  // UKC 六個字元:字級依字元數縮(方塊寬度的 130 % ÷ 6),不低於下限 28 px
  const ukcFont = Object.entries(longBar.tiles.find((t) => t.tag === 'brm-depth')!.fonts).find(([key]) => key.startsWith('.main'))![1];
  expect(ukcFont, 'UKC 1234.5 的字級(px)').toBeGreaterThanOrEqual(28);
  expect(ukcFont, 'UKC 1234.5 的字級(px)').toBeLessThan(41);
  errors.expectNone();
});

/** probeConning 量到的字級:鍵是「父元素的類別>數值的類別」,狀態類別(warning、caution、alarm…)在後面,以開頭比對 */
function conningFont(bar: ConningReport, tag: string, prefix: string): number {
  const tile = bar.tiles.find((t) => t.tag === tag);
  expect(tile, tag).toBeDefined();
  const key = Object.keys(tile!.fonts).find((k) => k.startsWith(prefix));
  expect(key, `${tag} 的數值 ${prefix}(量到的:${Object.keys(tile!.fonts).join('、')})`).toBeDefined();
  return tile!.fonts[key!]!;
}

test('操船台 1366×768、1536×864 與 1093×614(conning 條兩列 10 至 13 格):平常會出現的數值與狀態下,讀數列各方塊不被方塊邊界水平或垂直裁切,字距加大 3 % 仍通過', async ({ page, env, core }) => {
  // 第三十一輪:視窗 ≤ 1900 px 時 conning 條排成兩列,每個方塊只有約 107 至 135 px 高。原本兩列共用 6 欄(有傾斜儀時 7 欄)的格線,
  // 航向的「設定 / COG」、主機的車鐘位置與主機狀態、UKC 的「吃水 F/A」、航線下方的幾列在 1366×768 平常的數值下就被方塊下緣切掉;
  // 流方塊標題裡較長的流場來源標示、時間方塊「GNSS 無效」的標題被方塊右緣切掉;13 格時浪、傾斜儀、航線三格放不下。
  // 現在兩列各自分配寬度,各方塊依自己的寬度與高度排(行高固定、大字的字級以方塊高度設上限、圖形可以縮短)。
  // 判定在 support/pages.ts 的 expectConningTwoRows(版面判定通過、排成兩列、內容不比方塊高、沒有被方塊邊界切掉的文字、
  // 各方塊的圖形與文字都有顯示、文字不重疊、字距加大 3 % 後仍通過)。
  const errors = new PageErrors(page);
  await ensureScenario(core, 'E20_comprehensive_assessment_kaohsiung');
  await ensureRunning(core);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(bridgeUrl(env));
  await waitConnected(page);
  await expect(page.locator('brm-heading .hdg .value')).toHaveText(/^\d{3}$/);
  await expect(page.locator('brm-vhf .pill')).toHaveText('已連線');
  const sizes = [{ width: 1366, height: 768 }, { width: 1536, height: 864 }];
  // 1) 真實核心目前的狀態(E20:有航線,11 格)
  for (const size of sizes) {
    await page.setViewportSize(size);
    await expectConningTwoRows(page, BRIDGE_LAYOUT, `操船台 ${size.width}×${size.height}(核心目前的狀態)`, { cells: 11 });
  }

  // 2) 合成的數值(support/conning-values.ts 的 CONNING_TWO_ROW_*:平常會出現的位數與狀態)。選填欄位決定格數
  const none = CONNING_NONE;
  const route = CONNING_TWO_ROW_ROUTE;
  const sea = CONNING_TWO_ROW_SEA;
  const roll = CONNING_ROLL;
  const combos: Array<{ name: string; cells: number; extra: Record<string, unknown> }> = [
    { name: '10 格', cells: 10, extra: {} },
    { name: '11 格(航線)', cells: 11, extra: { route, visibility_nm: 0.2 } },
    { name: '12 格(航線、浪)', cells: 12, extra: { route, visibility_nm: 0.2, ...sea } },
    { name: '13 格(航線、浪、橫搖)', cells: 13, extra: { route, visibility_nm: 0.2, ...sea, roll } },
  ];
  for (const size of sizes) {
    await page.setViewportSize(size);
    for (const combo of combos) {
      await test.step(`${size.width}×${size.height} ${combo.name}`, async () => {
        const label = `操船台 ${size.width}×${size.height} ${combo.name}`;
        // 各種狀態同時出現:感測器無效、凍結、擱淺、主機故障並 EFAS、艏側推不可用、最長的流場來源標示、99:59:59、西經
        expect(await page.evaluate(overrideConning, { patch: { ...none, ...CONNING_TWO_ROW_STATUSES, ...combo.extra }, config: { rudderMax_deg: 35 }, setHeading: 288 })).toBe(combo.cells);
        await expect(page.locator('brm-clock .row.sim .value')).toHaveText('99:59:59 凍結');
        await expect(page.locator('brm-clock .tile-title .zh')).toHaveText('時間與位置 GNSS 無效');
        await expect(page.locator('brm-current .tile-title .zh')).toHaveText('流(流場網格·流場表)');
        await expect(page.locator('brm-depth .rows .row').last()).toHaveText('吃水 F/A5.7 / 6.1');
        await expect(page.locator('brm-engine .bottom')).toHaveText(/EFAS 緊急全速倒車\s*故障 100%/);
        const bar = await expectConningTwoRows(page, BRIDGE_LAYOUT, `${label}、各種狀態`, { cells: combo.cells });
        // 主要數值的字級不比修改前小超過 10 %(修改前:大字 3.2vw、迴轉率 / 船速 / 航線偏移 1.6vw、舵角 / 艏側推 / 時鐘 1.1vw)
        const vw = size.width / 100;
        expect(conningFont(bar, 'brm-heading', '.hdg>.value.big'), `${label}:航向的字級`).toBeGreaterThanOrEqual(0.9 * 3.2 * vw);
        expect(conningFont(bar, 'brm-rot', '.tile>.readout.value.mid'), `${label}:迴轉率的字級`).toBeGreaterThanOrEqual(0.9 * 1.6 * vw);
        expect(conningFont(bar, 'brm-speed', '.row>.value.mid'), `${label}:STW / SOG 的字級`).toBeGreaterThanOrEqual(0.9 * 1.6 * vw);
        expect(conningFont(bar, 'brm-rudder', '>.value.small'), `${label}:舵角的字級`).toBeGreaterThanOrEqual(0.9 * 1.1 * vw);
        expect(conningFont(bar, 'brm-engine', '.top>.value.big'), `${label}:主機轉速的字級`).toBeGreaterThanOrEqual(0.9 * 3.2 * vw);
        expect(conningFont(bar, 'brm-thruster', '>.value.small'), `${label}:艏側推的字級`).toBeGreaterThanOrEqual(0.9 * 1.1 * vw);
        expect(conningFont(bar, 'brm-depth', '.main'), `${label}:UKC 的字級`).toBeGreaterThanOrEqual(0.9 * 3.2 * vw);
        expect(conningFont(bar, 'brm-clock', '.row>.value.small'), `${label}:時鐘的字級`).toBeGreaterThanOrEqual(0.9 * 1.1 * vw);
        if ('route' in combo.extra) {
          expect(conningFont(bar, 'brm-route', '.xtd>.value.mid'), `${label}:航線偏移的字級`).toBeGreaterThanOrEqual(0.9 * 1.6 * vw);
          // 航線方塊:「L 偏左」沒有被擠成直排(一行),「速限」與「能見度」在同一列
          const routeBoxes = await page.locator('brm-route').evaluate((host) => {
            const root = host.shadowRoot!;
            const r = (sel: string) => root.querySelector(sel)!.getBoundingClientRect();
            // 文字本身的範圍(標籤與數值的格子比文字寬:數值的欄吃剩下的寬度、「能見度」的左邊有內距)
            const textOf = (sel: string) => {
              const range = document.createRange();
              range.selectNodeContents(root.querySelector(sel)!);
              const b = range.getBoundingClientRect();
              return { left: b.left, right: b.right };
            };
            return { side: r('.xtd .side'), limit: r('.rows .label.k-lim'), vis: r('.rows .label.k-vis'), limitText: textOf('.rows .label.k-lim'), limitValue: textOf('.rows .v.k-lim'), visText: textOf('.rows .label.k-vis') };
          });
          expect(routeBoxes.side.height, `${label}:「L 偏左」的高度(px;一行)`).toBeLessThan(20);
          expect(Math.abs(routeBoxes.limit.top - routeBoxes.vis.top), `${label}:「速限」與「能見度」在同一列`).toBeLessThan(2);
          // 審查後的修正:速限的數值緊接在「速限」後面,離「能見度」比離自己的標籤遠(原本數值靠右:離自己的標籤 200 px 以上、離「能見度」12 px,
          // 畫面上讀成「速限 ……… 12.5 kn 能見度 0.2 nm」)
          const own = routeBoxes.limitValue.left - routeBoxes.limitText.right;
          const next = routeBoxes.visText.left - routeBoxes.limitValue.right;
          expect(own, `${label}:速限的數值到「速限」的距離(px)`).toBeGreaterThanOrEqual(0);
          expect(own, `${label}:速限的數值到「速限」的距離(px)`).toBeLessThanOrEqual(8);
          expect(next, `${label}:「能見度」到速限的數值的距離(px;至少 18,而且比速限的數值到自己的標籤遠)`).toBeGreaterThanOrEqual(Math.max(17.5, own + 6));
        }
        // 審查後的修正:UKC 的大字在標題列與下方各列之間上下置中 —— 擱淺(警報)時紅底(.main)的上緣不貼著數字。原本大字排在 .main 的最上面,
        // 行高 0.82 的數字幾乎沒有上緣的留白(CI 的字型下比紅底高出 0.5 px),下面卻空著。多出來的高度有 8 px 以上時(10 格兩欄約 30 px)判定
        const ukc = await page.locator('brm-depth').evaluate((host) => {
          const root = host.shadowRoot!;
          const main = root.querySelector('.main')!;
          const m = main.getBoundingClientRect();
          const v = root.querySelector('.main .value.big')!.getBoundingClientRect();
          return { alarm: main.classList.contains('alarm'), above: v.top - m.top, below: m.bottom - v.bottom };
        });
        expect(ukc.alarm, `${label}:擱淺時 UKC 是警報的紅底`).toBe(true);
        if (combo.cells === 10) expect(ukc.above + ukc.below, `${label}:UKC 大字上下多出來的高度(px;10 格時兩欄)`).toBeGreaterThanOrEqual(8);
        if (ukc.above + ukc.below >= 8) {
          expect(ukc.above, `${label}:UKC 大字的行框上緣離紅底上緣(px)`).toBeGreaterThanOrEqual(2);
          expect(Math.abs(ukc.above - ukc.below), `${label}:UKC 大字上下的留白相差(px;「擱淺」的降部讓下面多最多 4 px)`).toBeLessThanOrEqual(4.5);
        }
        // 倒俥、滿舵 70°、緊急全速倒車 -150 並「啟動中 100%」、艏側推 P 100%、航向可疑
        expect(await page.evaluate(overrideConning, { patch: { ...none, ...CONNING_TWO_ROW_ASTERN, ...combo.extra }, config: { rudderMax_deg: 70 }, issues: ['heading'], setHeading: 359 })).toBe(combo.cells);
        await expect(page.locator('brm-engine .top .value.big')).toHaveText('-150');
        await expect(page.locator('brm-clock .row.sim .value')).toHaveText('23:59:59 凍結');
        await expectConningTwoRows(page, BRIDGE_LAYOUT, `${label}、倒俥滿舵`, { cells: combo.cells });
      });
    }
  }
  // 3) 1093×614(1366×768 在 125 % 縮放;方塊 107 px 高,13 格時最窄):關鍵控制本來就要捲動(第 6 節),這裡只判定 conning 條
  await page.setViewportSize({ width: 1093, height: 614 });
  for (const [name, values, config] of [['各種狀態', CONNING_TWO_ROW_STATUSES, { rudderMax_deg: 35 }], ['倒俥滿舵', CONNING_TWO_ROW_ASTERN, { rudderMax_deg: 70 }]] as const) {
    expect(await page.evaluate(overrideConning, { patch: { ...none, ...values, route, visibility_nm: 0.2, ...sea, roll }, config, setHeading: 288 })).toBe(13);
    await expectConningTwoRows(page, BRIDGE_LAYOUT, `操船台 1093×614 13 格、${name}`, { cells: 13, scope: 'conning' });
  }
  errors.expectNone();
});

