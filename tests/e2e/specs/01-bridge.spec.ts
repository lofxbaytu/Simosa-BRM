// 操船台(index.html):載入後 5 s 內收到核心狀態幀,HDG/SOG 顯示數值(不是 --- / --.-),連線藥丸為「已連線」,無頁面錯誤;
// 文書筆電的視窗大小(1280×720、1366×768、1536×864;失電後 header 折成兩行)下每個控制項都點得到、VHF 文字欄 / DSC 不必捲動(版面;判定在 support/layout.ts);
// 麥克風被拒時(1280×720、1366×768 失電後)PTT 鈕上寫「無麥克風」、DSC 仍不必捲動,VHF 訊息紀錄列貼底(看得到最新兩則);
// 1093×614(1366×768 在 125 % 縮放)的非關鍵判定(車鐘 / 艏側推鈕內文字、浮貼外力讀數不超出航跡圖;E09 失電後警報標題列一行、
// 2 筆警報完整可見、meta 不截斷);1280×720 的浮貼外力讀數放得下時不攔滑鼠(讀數中央的滾輪縮放、拖曳平移航跡圖)。
import type { Locator, Page } from '@playwright/test';
import { bridgeUrl, instructorUrl } from '../support/env.ts';
import { ensureRunning, ensureScenario, expect, scenarioPath, test } from '../support/fixtures.ts';
import { BRIDGE_LAYOUT, layoutFailures, probeLayout } from '../support/layout.ts';
import { PageErrors, waitConnected } from '../support/pages.ts';
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
