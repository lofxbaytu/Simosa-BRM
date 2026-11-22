// 情境編輯器(教官站完整模式「情境編輯」分頁;規劃書第 9.1 節):套用範本 E05 → 環境進階勾選浪並改 Hs → 結構檢查通過 → YAML 預覽含 waves。
// 第二案例:啟用 ship.seakeeping → 沒有浪的提醒 → 加浪 → 載入核心 → 狀態幀出現 waveMotion(編輯器到核心的整條路徑)。
// 版面案例(第二十九輪 L1;每個範本一案):教官筆電常見的有效寬度 1100(一欄)/ 1366 / 1536 / 1920 / 2560 下,套用範本並把各區段展開後量測——
// 面板/欄不水平溢出、面板不重疊、控制項點得到、標籤不被擠成逐字換行、輸入框不被擠壓或裁切、頁面沒有水平捲軸(判定見 probeEditorLayout)。
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

// ------------------------------------------------------------------ 版面量測(第二十九輪 L1;R4 練習編輯、規劃書第 9.1 節教官站)
// 教官筆電常見的有效寬度是 1366(1366×768)與 1536(1920×1080 在 Windows 125 % 縮放);修正前編輯器固定三欄,每欄只有 447–504 px:
// 標籤被擠成逐字換行、初始位置與風/流的輸入框只剩約 30 px、感測器/時間表/評估面板的內容超出面板而被裁切。
// 修正後欄數依編輯器寬度切換(scenario-editor.ts 的 container query:≥ 1800 px 三欄、1150–1800 px 兩欄、更窄一欄),以下案例把關不退步。
//
// 下面兩個函式由 locator.evaluate 送進瀏覽器執行,必須自給自足(不可引用本檔其他識別字、不可用需要輔助函式的語法)。
// 「L1-LAYOUT-PROBE-BEGIN / END」之間的原文也可由獨立腳本讀取、以 TypeScript 轉譯後執行(不需核心,自己起靜態伺服;迭代版面時用,
// 第二十九輪放在 build/tmp/l1-layout/measure.cjs,不入版控),展開步驟與判定因此與本檔逐行相同。
// ==== L1-LAYOUT-PROBE-BEGIN ====
/** 互斥區段的展開組合:A = 調和潮 + 流場表 + 新增故障「模擬時間」;B = 潮高表(UTC 時刻)+ 潮流 + 新增故障「自訂條件」 */
type LayoutVariant = 'A' | 'B';

interface LayoutIssue {
  /** overflow 內容超出容器;label-squeezed / text-squeezed 逐字換行;control-narrow 輸入框太窄;control-outside 超出面板;
   *  control-clipped 被捲動/裁切容器切掉;value-clipped 數值顯示不完整;page-scroll 頁面水平捲軸 */
  kind: 'overflow' | 'label-squeezed' | 'text-squeezed' | 'control-narrow' | 'control-outside' | 'control-clipped' | 'value-clipped' | 'page-scroll' | 'panel-overlap' | 'control-covered';
  /** 面板 › 元素 */
  where: string;
  /** 量測值 */
  detail: string;
}

interface LayoutReport {
  issues: LayoutIssue[];
  stats: {
    viewport: [number, number];
    /** 編輯器(元件本身)的寬度 px */
    editorWidth: number;
    /** 欄數(面板左緣的相異位置數) */
    columns: number;
    /** 各欄面板的寬度 px(由左至右) */
    columnWidths: number[];
    panels: number;
    labels: number;
    controls: number;
    /** 標籤、輸入框、按鈕的最小字級 px */
    minFontPx: number;
    /** 輸入框(input / select / textarea;不含勾選框)的最小高度 px */
    minControlHeightPx: number;
    /** 按鈕的最小高度 px */
    minButtonHeightPx: number;
  };
}

/**
 * 套用範本後把可展開/可啟用的區段盡量打開(在頁面內直接操作 DOM,比逐一用定位器點擊快):勾選航線、錨、泊位、岸壁、浪、空間流場、
 * 船間交互作用、橫搖、升沉/縱搖;潮汐與流場模型依 variant 切換;每種清單列(目標船、容許帶、必要事件、拖船、繫纜、角色、附件、航點、區域、
 * 表點/分潮、時間表項目)至少一列;展開全部時間表項目與區域頂點;開啟海圖點選編輯面板與 YAML 預覽。回傳各步驟實際操作的次數。
 */
async function expandEditorForLayout(host: Element, variant: LayoutVariant): Promise<Record<string, number>> {
  const root = host.shadowRoot;
  if (!root) throw new Error('brm-scenario-editor 沒有 shadowRoot');
  const done: Record<string, number> = {};
  const note = (key: string): void => {
    done[key] = (done[key] ?? 0) + 1;
  };
  const settle = async (): Promise<void> => {
    await (host as Element & { updateComplete?: Promise<unknown> }).updateComplete;
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  };
  const all = <T extends Element>(selector: string): T[] => Array.from(root.querySelectorAll<T>(selector));
  const check = async (selector: string): Promise<void> => {
    for (let guard = 0; guard < 50; guard++) {
      const box = all<HTMLInputElement>(selector).find((b) => !b.checked && !b.disabled);
      if (!box) return;
      box.click();
      note(selector);
      await settle();
    }
  };
  const choose = async (selector: string, value: string): Promise<void> => {
    for (const select of all<HTMLSelectElement>(selector)) {
      if (select.value === value) continue;
      select.value = value;
      select.dispatchEvent(new Event('change', { bubbles: true }));
      note(`${selector}=${value}`);
      await settle();
    }
  };
  const clickFirst = async (selector: string): Promise<boolean> => {
    const button = all<HTMLButtonElement>(selector).find((b) => !b.disabled);
    if (!button) return false;
    button.click();
    note(selector);
    await settle();
    return true;
  };
  const ensureRow = async (rowSelector: string, addSelector: string): Promise<void> => {
    if (all(rowSelector).length === 0) await clickFirst(addSelector);
  };
  /** 逐一按下文字為 collapsedText 的鈕(每次重新查詢:按下後整個表單重新渲染) */
  const expandAll = async (selector: string, collapsedText: string): Promise<void> => {
    for (let guard = 0; guard < 500; guard++) {
      const button = all<HTMLButtonElement>(selector).find((b) => (b.textContent ?? '').trim() === collapsedText);
      if (!button) return;
      button.click();
      note(selector);
      await settle();
    }
  };

  for (const selector of ['input.route-enabled', 'input.anchors-enabled', 'input.berth-enabled', 'input.banks-enabled', 'input.waves-enabled', 'input.field-enabled', 'input.si-enabled', 'input.roll-enabled', 'input.sk-enabled']) {
    await check(selector);
  }
  if (variant === 'A') {
    await choose('select.tide-mode', 'harmonic');
    await ensureRow('.row.constituent', 'button.add-constituent');
    await choose('select.cur-model', 'table');
    await ensureRow('.row.current-point', 'button.add-current-point');
    await choose('select.f-trigger', 'time');
  } else {
    await choose('select.tide-mode', 'table');
    await ensureRow('.row.tide-point', 'button.add-tide-point');
    // 改用 UTC 時刻(時間欄變成文字輸入);範本沒有開始時刻時編輯器會顯示錯誤並維持 t(秒),不影響量測
    if (all('input.tp-utc').length === 0) await clickFirst('button.tide-time-key');
    await choose('select.cur-model', 'tidal');
    await choose('select.f-trigger', 'condition');
  }
  await ensureRow('.row.target', 'button.add-target');
  await ensureRow('.row.band', 'button.add-band');
  await ensureRow('.row.required', 'button.add-required');
  await ensureRow('.row.tug', 'button.add-tug');
  await ensureRow('.row.line', 'button.add-line');
  await ensureRow('.row.role', 'button.add-role');
  await ensureRow('.row.attachment', 'button.add-attachment');
  await ensureRow('.row.wp', 'button.add-waypoint');
  await ensureRow('.row.zone', 'button.add-zone');
  await ensureRow('.panel.timeline .list .item', 'button.add-item');
  await expandAll('button.tl-edit', '編輯');
  await expandAll('button.z-vertices', '編輯頂點');
  if (all('.panel.plot-editor').length === 0) await clickFirst('button.own-plot');
  await expandAll('button.toggle-yaml', '預覽 YAML');
  return done;
}

/** 量測編輯器目前的版面;判定門檻見函式開頭的常數。 */
function probeEditorLayout(host: Element): LayoutReport {
  const root = host.shadowRoot;
  if (!root) throw new Error('brm-scenario-editor 沒有 shadowRoot');
  /** 容器的 scrollWidth 超過 clientWidth 多少算溢出 */
  const OVERFLOW_TOLERANCE_PX = 2;
  /** 文字元素寬度小於此值且高度大於 SQUEEZED_HEIGHT_PX = 被擠成逐字換行 */
  const SQUEEZED_WIDTH_PX = 56;
  const SQUEEZED_HEIGHT_PX = 48;
  /** 輸入框最小寬度:數值(type=number 或 inputmode=decimal)與其他(文字、選單、id) */
  const NUMERIC_MIN_WIDTH_PX = 40;
  const TEXT_MIN_WIDTH_PX = 72;
  /** 外框比較的容差 */
  const EDGE_TOLERANCE_PX = 0.5;

  const issues: LayoutIssue[] = [];
  const px = (v: number): number => Math.round(v * 10) / 10;
  const visible = (el: Element): boolean => {
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
  };
  const describe = (el: Element): string => {
    const tag = el.tagName.toLowerCase();
    const classes = Array.from(el.classList).slice(0, 3).join('.');
    const index = el.getAttribute('data-index');
    const text = tag === 'label' || tag === 'legend' || tag === 'button' || el.classList.contains('hint') ? `「${(el.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 24)}」` : '';
    return `${tag}${classes ? `.${classes}` : ''}${index !== null ? `[${index}]` : ''}${text}`;
  };
  const where = (el: Element): string => {
    const panel = el.closest('.panel');
    const row = el.parentElement?.closest('.row, fieldset');
    const parts = [panel && panel !== el ? describe(panel) : '', row && row !== panel ? describe(row) : '', describe(el)];
    return parts.filter((p) => p !== '').join(' › ');
  };

  // 1) 面板、fieldset、欄、列與清單的內容不超出容器
  for (const el of Array.from(root.querySelectorAll('.editor, .editor > *, .col, .panel, fieldset, .form, .row, .list'))) {
    if (!visible(el)) continue;
    if (el.scrollWidth - el.clientWidth > OVERFLOW_TOLERANCE_PX) issues.push({ kind: 'overflow', where: where(el), detail: `scrollWidth ${el.scrollWidth} > clientWidth ${el.clientWidth}` });
  }

  // 2) 標籤(與說明、按鈕、legend)不被擠成逐字換行
  let labels = 0;
  let minFontPx = Infinity;
  let minButtonHeightPx = Infinity;
  for (const el of Array.from(root.querySelectorAll('label, legend, button, .hint'))) {
    if (!visible(el)) continue;
    const r = el.getBoundingClientRect();
    const isLabel = el.tagName === 'LABEL';
    if (isLabel) labels++;
    if (el.tagName === 'BUTTON') minButtonHeightPx = Math.min(minButtonHeightPx, r.height);
    if ((el.textContent ?? '').trim() !== '') minFontPx = Math.min(minFontPx, parseFloat(getComputedStyle(el).fontSize));
    if (r.width < SQUEEZED_WIDTH_PX && r.height > SQUEEZED_HEIGHT_PX) issues.push({ kind: isLabel ? 'label-squeezed' : 'text-squeezed', where: where(el), detail: `${px(r.width)} × ${px(r.height)} px` });
  }

  // 3) 輸入框:最小寬度、整個外框在所屬面板內、不被任何捲動/裁切容器切掉、數值顯示完整
  let controls = 0;
  let minControlHeightPx = Infinity;
  const hostRect = host.getBoundingClientRect();
  for (const el of Array.from(root.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('input, select, textarea'))) {
    const type = el instanceof HTMLInputElement ? el.type : '';
    if (type === 'checkbox' || type === 'radio' || type === 'hidden') continue;
    if (!visible(el)) continue;
    controls++;
    const r = el.getBoundingClientRect();
    minControlHeightPx = Math.min(minControlHeightPx, r.height);
    minFontPx = Math.min(minFontPx, parseFloat(getComputedStyle(el).fontSize));
    const numeric = el instanceof HTMLInputElement && (type === 'number' || el.inputMode === 'decimal' || el.inputMode === 'numeric');
    const minWidth = numeric ? NUMERIC_MIN_WIDTH_PX : TEXT_MIN_WIDTH_PX;
    if (r.width < minWidth - EDGE_TOLERANCE_PX) issues.push({ kind: 'control-narrow', where: where(el), detail: `寬 ${px(r.width)} px < ${minWidth} px(${numeric ? '數值' : '文字/選單'})` });
    const panel = el.closest('.panel');
    if (!panel) {
      issues.push({ kind: 'control-outside', where: where(el), detail: '不在任何面板內' });
    } else {
      const p = panel.getBoundingClientRect();
      if (r.left < p.left - EDGE_TOLERANCE_PX || r.right > p.right + EDGE_TOLERANCE_PX || r.top < p.top - EDGE_TOLERANCE_PX || r.bottom > p.bottom + EDGE_TOLERANCE_PX) {
        issues.push({ kind: 'control-outside', where: where(el), detail: `外框 x ${px(r.left)}–${px(r.right)}、y ${px(r.top)}–${px(r.bottom)} 超出面板 x ${px(p.left)}–${px(p.right)}、y ${px(p.top)}–${px(p.bottom)}` });
      }
    }
    // 往上找所有水平方向會裁切或捲動的祖先(overflow-x 不是 visible;display: contents 沒有外框,不裁切):輸入框的左右緣必須在其內距框內;元件本身(:host)也算
    let clippedBy = '';
    for (let a = el.parentElement; a && clippedBy === ''; a = a.parentElement) {
      const style = getComputedStyle(a);
      if (style.overflowX === 'visible' || style.display === 'contents') continue;
      const ar = a.getBoundingClientRect();
      const left = ar.left + a.clientLeft;
      const right = left + a.clientWidth;
      if (r.left < left - EDGE_TOLERANCE_PX || r.right > right + EDGE_TOLERANCE_PX) clippedBy = `${describe(a)} x ${px(left)}–${px(right)}`;
    }
    if (clippedBy === '' && (r.left < hostRect.left - EDGE_TOLERANCE_PX || r.right > hostRect.right + EDGE_TOLERANCE_PX)) clippedBy = `brm-scenario-editor x ${px(hostRect.left)}–${px(hostRect.right)}`;
    if (clippedBy !== '') issues.push({ kind: 'control-clipped', where: where(el), detail: `外框 x ${px(r.left)}–${px(r.right)} 被 ${clippedBy} 裁切` });
    // 純數值(不是參數引用)要整個看得到:輸入框內文字的捲動寬度不超過可見寬度
    if (numeric && /^-?\d+(\.\d+)?$/.test(el.value.trim()) && el.scrollWidth - el.clientWidth > 1) {
      issues.push({ kind: 'value-clipped', where: where(el), detail: `值 ${el.value} 需 ${el.scrollWidth} px,可見 ${el.clientWidth} px` });
    }
  }

  // 4) 面板之間的外框不相交(一欄時兩個隱含列曾被壓成容器高度的一半,乙丙組畫在甲組上面;第二十九輪審查)
  const visiblePanels = Array.from(root.querySelectorAll('.panel')).filter(visible);
  for (let i = 0; i < visiblePanels.length; i++) {
    const a = visiblePanels[i]!.getBoundingClientRect();
    for (let j = i + 1; j < visiblePanels.length; j++) {
      const pj = visiblePanels[j]!;
      if (visiblePanels[i]!.contains(pj) || pj.contains(visiblePanels[i]!)) continue;
      const b = pj.getBoundingClientRect();
      const w = Math.min(a.right, b.right) - Math.max(a.left, b.left);
      const h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (w > 1 && h > 1) issues.push({ kind: 'panel-overlap', where: `${describe(visiblePanels[i]!)} ↔ ${describe(pj)}`, detail: `重疊 ${px(w)} × ${px(h)} px` });
    }
  }

  // 5) 每個可見的控制項(含按鈕、勾選框)捲到可見範圍後,中心點的最上層元素(穿過 shadow DOM)是自己或自己的後代 —
  //    與 Playwright 點擊前的攔截檢查相同;海圖點選編輯的畫布元件(brm-track-plot)內部不算
  const deepAt = (x: number, y: number): Element | null => {
    let e = document.elementFromPoint(x, y);
    while (e && e.shadowRoot) {
      const inner = e.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === e) break;
      e = inner;
    }
    return e;
  };
  for (const el of Array.from(root.querySelectorAll<HTMLElement>('input, select, textarea, button'))) {
    if (el.closest('brm-track-plot') || (el instanceof HTMLInputElement && el.type === 'hidden') || !visible(el)) continue;
    el.scrollIntoView({ block: 'center', inline: 'nearest' });
    const r = el.getBoundingClientRect();
    const top = deepAt(r.left + r.width / 2, r.top + r.height / 2);
    if (!top || (top !== el && !el.contains(top))) {
      const blocker = top ? (top.closest('.panel') ? describe(top.closest('.panel')!) + ' › ' : '') + describe(top) : '(視窗外)';
      issues.push({ kind: 'control-covered', where: where(el), detail: `中心點被 ${blocker} 蓋住` });
    }
  }

  // 6) 頁面沒有水平捲軸;編輯器本身也在視窗內
  const doc = document.documentElement;
  if (doc.scrollWidth > doc.clientWidth) issues.push({ kind: 'page-scroll', where: 'html', detail: `scrollWidth ${doc.scrollWidth} > clientWidth ${doc.clientWidth}` });
  if (hostRect.left < -EDGE_TOLERANCE_PX || hostRect.right > doc.clientWidth + EDGE_TOLERANCE_PX) {
    issues.push({ kind: 'page-scroll', where: 'brm-scenario-editor', detail: `x ${px(hostRect.left)}–${px(hostRect.right)} 超出視窗寬 ${doc.clientWidth}` });
  }

  const panels = Array.from(root.querySelectorAll('.panel')).filter(visible);
  const byLeft = new Map<number, number>();
  for (const panel of panels) {
    const r = panel.getBoundingClientRect();
    const left = Math.round(r.left);
    byLeft.set(left, Math.min(byLeft.get(left) ?? Infinity, Math.round(r.width)));
  }
  const lefts = Array.from(byLeft.keys()).sort((a, b) => a - b);
  return {
    issues,
    stats: {
      viewport: [window.innerWidth, window.innerHeight],
      editorWidth: Math.round(hostRect.width),
      columns: lefts.length,
      columnWidths: lefts.map((left) => byLeft.get(left) ?? 0),
      panels: panels.length,
      labels,
      controls,
      minFontPx,
      minControlHeightPx: px(minControlHeightPx),
      minButtonHeightPx: px(minButtonHeightPx),
    },
  };
}
// ==== L1-LAYOUT-PROBE-END ====

/** 量測用的範本:環境進階各區段、拖船/錨/繫纜/泊位、評估規則多且時間表長、感測器故障 */
const LAYOUT_TEMPLATES = ['E17b_typhoon_seakeeping', 'E13_tug_berthing_mailiao', 'E20_comprehensive_assessment_kaohsiung', 'E08_gyro_failure'] as const;
/** [視窗寬 px, 預期欄數];視窗高 = 寬 × 9/16。1366 = 1366×768、1536 = 1920×1080 在 Windows 125 % 縮放、1920 = 1920×1080 */
const LAYOUT_WIDTHS: ReadonlyArray<readonly [width: number, columns: number]> = [
  [1100, 1], // 一欄:1366×768 在 125 % 縮放(1093 px)、視窗沒有最大化
  [1366, 2],
  [1536, 2],
  [1920, 3],
  [2560, 3],
];
/** 每欄面板的最小寬度 px(欄數切換的門檻保證不低於此值;scenario-editor.ts 樣式的註解) */
const LAYOUT_MIN_COLUMN_PX = 560;

for (const template of LAYOUT_TEMPLATES) {
  test(`情境編輯器版面:範本 ${template} 在 1100 / 1366 / 1536 / 1920 / 2560 寬不溢出、面板不重疊、控制項點得到、標籤不逐字換行、輸入框不被擠壓或裁切`, async ({ page, env }) => {
    const errors = new PageErrors(page);
    await page.goto(instructorUrl(env));
    await waitConnected(page);
    await page.locator('header button.view-editor').click();
    const editor = page.locator('brm-scenario-editor');
    await expect(editor).toBeVisible();
    await editor.locator('select.template').selectOption(template);
    await editor.locator('button.apply-template').click();
    await expect(editor.locator('input.id')).toHaveValue(new RegExp(`^${template}`));

    const problems: string[] = [];
    for (const variant of ['A', 'B'] as const) {
      await editor.evaluate(expandEditorForLayout, variant);
      for (const [width, columns] of LAYOUT_WIDTHS) {
        await page.setViewportSize({ width, height: Math.round((width * 9) / 16) });
        const report = await editor.evaluate(probeEditorLayout);
        const at = `${width} px / 組合 ${variant}`;
        for (const issue of report.issues) problems.push(`${at}:[${issue.kind}] ${issue.where}:${issue.detail}`);
        if (report.stats.columns !== columns) problems.push(`${at}:欄數 ${report.stats.columns},預期 ${columns}(各欄寬 ${report.stats.columnWidths.join(' / ')} px)`);
        if (Math.min(...report.stats.columnWidths) < LAYOUT_MIN_COLUMN_PX) problems.push(`${at}:欄寬 ${report.stats.columnWidths.join(' / ')} px,低於 ${LAYOUT_MIN_COLUMN_PX} px`);
        // 量測真的涵蓋了表單(防止選擇器失效後空量測通過)
        expect(report.stats.panels, `${at}:量到的面板數`).toBeGreaterThanOrEqual(13);
        expect(report.stats.labels, `${at}:量到的標籤數`).toBeGreaterThan(150);
        expect(report.stats.controls, `${at}:量到的輸入框數`).toBeGreaterThan(200);
      }
    }
    expect(problems, '版面問題(寬度 / 展開組合:[種類] 面板 › 元素:量測值)').toEqual([]);
    errors.expectNone();
  });
}
