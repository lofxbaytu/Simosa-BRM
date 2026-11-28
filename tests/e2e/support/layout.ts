// 版面可及性量測(第二十九輪 L2;規劃書第 7、9.1 節:船上以文書筆電為基準):駕駛台主畫面與完整教官站在常見筆電解析度下
// 「每個控制項都點得到」的判定。specs/01-bridge、02-instructor 與開發時的量測腳本(build/tmp/l3-layout/measure.mjs)共用這一份,
// 判定邏輯只有一處。本檔沒有執行期相依(只匯入型別),probeLayout 以 page.evaluate 送進頁面執行,函式內不可引用外部符號。
//
// 判定(對應 docs/design/web-layout.md 第 4 節):
//  1. 每個可見的 button / input / select / textarea:中心點的最上層元素(穿過 shadow DOM)必須是自己或自己的後代(與 Playwright
//     點擊前的攔截檢查相同);不是的話逐一 scrollIntoView 後再判定 — 只允許靠「使用者捲得動」的容器(overflow auto / scroll)捲到,
//     靠 overflow: hidden 的容器才捲得到者視為被裁切。
//  2. 關鍵控制(LayoutSpec.keys)不必捲動就整個看得到(中心點與四邊中點都點得到);關鍵清單(LayoutSpec.lists)的可見高度
//     足夠,且前幾列完整落在清單的可見範圍內。
//  3. 面板(brm-* 元件)內容不溢出(scrollHeight ≤ clientHeight,或自己會捲動)、面板之間的可見外框不相交、
//     不在視窗內的面板必須位於可捲動的容器內;浮貼的面板(position: absolute,例:航跡圖左上角的外力讀數)
//     可見外框不超出它的定位容器(第二十九輪 L4:pointer-events: none 的浮貼讀數溢出航跡圖、蓋住 BNWAS,點擊判定抓不到)。
//  4. conning 條各方塊內的元素沒有水平裁切(scrollWidth ≤ clientWidth + 1)。
//  5. 整頁沒有捲軸(html 的 scrollWidth / scrollHeight 不超過視窗)。
//  6. 按鈕內的文字沒有被自己的 overflow 切掉、也沒有溢出鈕外(button 的 scrollWidth ≤ clientWidth + 1;第二十九輪 L4:
//     1093×614 的車鐘鈕「EFAS 緊急全速倒車 −150」數值被 overflow: hidden 切掉、艏側推快速鈕的「100」溢出鈕外)。
//  7.(資訊,不列入不合格)以 text-overflow: ellipsis 截斷的文字(scrollWidth > clientWidth + 1):第二十九輪 L5 加入 —
//     1093×614 失電後警報 meta 的「00:00:03」被截成「00:00:…」,判定 1–6 都抓不到;各頁仍有刻意以 … 截斷的清單摘要,
//     所以只列出供量測比對,個別元件的「不截斷」由各自的端對端斷言把關(例:01 的 1093×614 警報 meta)。
//
// conning 條單列(視窗寬於 1900 px)的餘裕(第三十一輪;web-layout.md 第 2.5 節):船上與教官筆電是 Windows(Edge / WebView2、
// 微軟正黑體 / Segoe UI),字寬與 Linux 的字型不同,容器裡量不到。widenConningText 把 conning 條所有文字的字距加大(預設 0.03em),
// 代表較寬的字型;加大之後判定 4 仍須通過(specs/01-bridge、02-instructor 的 1920×1080 案例)。probeConning 量各方塊的寬度、
// 排成幾列、內容有沒有比方塊高(被方塊自己的 overflow: hidden 裁掉下緣)與各數值的字級;審查後另量「顯示的內容有沒有少」——
// 各圖形(svg)的寬高、沒有畫出來的文字(display: none 等)、互相重疊的文字(內容比方塊高時,置中的內容會往上擠到標題列,
// scrollHeight 看不出來)。overrideConning 把 conning 條顯示的狀態換成指定的值(極端數值、格數組合)。
//
// conning 條兩列(視窗 ≤ 1900 px;船上文書筆電 1366×768 與教官筆電 125 % 縮放的 1536×864 實際看到的排法)另量「文字有沒有被方塊邊界切掉」:
// 每段文字的墨水範圍超出方塊的裁切框(.tile 的 overflow: hidden)多少 —— 兩列的方塊只有約 107 至 135 px 高,內容比方塊高時
// 最後一列的下緣被切掉(判定 4 只看水平;scrollHeight 的容差是 4 px,而 2 至 3 px 的下緣已經切到字)。

/** 關鍵控制:host 是 brm-* 元件的選擇器,inner 是其 shadow root 內的選擇器;至少要有 min 個(預設 1),每一個都必須不捲動就整個點得到 */
export interface LayoutKeyControl {
  name: string;
  host: string;
  inner: string;
  min?: number;
}

/**
 * 關鍵清單:list 的可見高度至少 minHeightPx,且這段高度內沒有被別的面板蓋住;minRows > 0 時前 minRows 列(rows 選擇器;
 * 不足時有幾列算幾列)還要完整落在可見範圍內。列高固定的清單(警報)才設 minRows;事件串流的列會因文字換行變高,只看高度。
 */
export interface LayoutKeyList {
  name: string;
  host: string;
  list: string;
  rows: string;
  minRows: number;
  minHeightPx: number;
}

export interface LayoutSpec {
  keys: LayoutKeyControl[];
  lists: LayoutKeyList[];
}

export interface LayoutReport {
  viewport: { width: number; height: number };
  /** 控制項數:全部 / 不必捲動就點得到 / 捲動後點得到 */
  controls: { total: number; direct: number; scrolled: number };
  /** 捲動後點得到的控制項所在的捲動容器 → 個數(資訊) */
  scrollContainers: Record<string, number>;
  /** 被別的元素蓋住(捲動後仍點不到) */
  covered: string[];
  /** 被裁切或在視窗外(沒有使用者捲得動的容器可以捲到) */
  clipped: string[];
  /** 面板內容溢出自己的外框(overflow: visible) */
  overflow: string[];
  /** 面板的可見外框互相重疊 */
  overlap: string[];
  /** 面板(部分)在視窗外且不在可捲動容器內 */
  offscreen: string[];
  /** 關鍵控制 / 關鍵清單不符 */
  key: string[];
  /** conning 條方塊內水平裁切 */
  conning: string[];
  /** 按鈕內的文字被自己的 overflow 切掉或溢出鈕外(scrollWidth > clientWidth + 1) */
  text: string[];
  /** 整頁捲動 */
  page: string[];
  /** overflow: hidden / clip 的面板內容被裁掉的高度(資訊;控制項被裁掉者已列在 clipped) */
  hiddenClip: string[];
  /** 以 text-overflow: ellipsis 截斷的文字(資訊;判定 7) */
  ellipsis: string[];
}

/** 在頁面內執行(page.evaluate):量測目前視窗下的版面。函式自給自足,不引用模組內其他符號。 */
export function probeLayout(spec: LayoutSpec): LayoutReport {
  /** 面板內容高 / 寬超過外框多少 px 算溢出(字型的行高誤差在 4 px 內) */
  const OVERFLOW_TOLERANCE_PX = 4;
  /** 外框比較與面板重疊的容差 */
  const EDGE_TOLERANCE_PX = 1;
  /** conning 條方塊內元素 scrollWidth 超過 clientWidth 多少算裁切 */
  const CONNING_TOLERANCE_PX = 1;
  /** 按鈕內文字 scrollWidth 超過 clientWidth 多少算被切掉 / 溢出(次像素進位) */
  const TEXT_TOLERANCE_PX = 1;
  const vw = window.innerWidth;
  const vh = window.innerHeight;

  type Rect = { left: number; top: number; right: number; bottom: number };
  type Hit = { ok: true } | { ok: false; why: 'covered' | 'offscreen'; by: string };

  /** 合成樹的父節點(穿出 shadow root 到 host;slot 內容回到 slot) */
  const parentOf = (n: Node): Node | null => {
    if (n instanceof Element && n.assignedSlot) return n.assignedSlot;
    const p = n.parentNode;
    return p instanceof ShadowRoot ? p.host : p;
  };
  const isInside = (node: Node | null, ancestor: Element): boolean => {
    for (let n = node; n; n = parentOf(n)) if (n === ancestor) return true;
    return false;
  };
  /** 座標上的最上層元素(逐層進入 shadow root) */
  const deepAt = (x: number, y: number): Element | null => {
    let el = document.elementFromPoint(x, y);
    while (el?.shadowRoot) {
      const inner = el.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === el) break;
      el = inner;
    }
    return el;
  };
  /** 所屬面板(最近的 brm-* 元件) */
  const panelOf = (node: Node): string => {
    for (let n: Node | null = node; n; n = parentOf(n)) if (n instanceof Element && n.tagName.toLowerCase().startsWith('brm-')) return n.tagName.toLowerCase();
    return 'document';
  };
  const classesOf = (el: Element): string => {
    const c = Array.from(el.classList).slice(0, 2).join('.');
    return c ? `.${c}` : '';
  };
  const describeBox = (el: Element): string => `${panelOf(el)} ${el.tagName.toLowerCase()}${classesOf(el)}`;
  const describe = (el: Element): string => {
    const text = (el.textContent || (el as HTMLInputElement).value || el.getAttribute('aria-label') || el.getAttribute('placeholder') || '').replace(/\s+/g, ' ').trim().slice(0, 14);
    const b = el.getBoundingClientRect();
    return `${describeBox(el)}「${text}」@${Math.round(b.x + b.width / 2)},${Math.round(b.y + b.height / 2)}`;
  };
  const rendered = (el: Element): boolean => {
    const b = el.getBoundingClientRect();
    if (b.width < 2 || b.height < 2) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none';
  };
  const userScrollable = (v: string): boolean => v === 'auto' || v === 'scroll';

  const hitAt = (el: Element, x: number, y: number): Hit => {
    if (x < 0 || y < 0 || x >= vw || y >= vh) return { ok: false, why: 'offscreen', by: '視窗外' };
    const top = deepAt(x, y);
    if (!top) return { ok: false, why: 'offscreen', by: '視窗外' };
    if (isInside(top, el)) return { ok: true };
    // label 包住的控制項:點到同一個 label 的其他部分也會作用到控制項
    const label = el.closest('label');
    if (label && isInside(top, label)) return { ok: true };
    return { ok: false, why: 'covered', by: describeBox(top) };
  };
  const hitCenter = (el: Element): Hit => {
    const b = el.getBoundingClientRect();
    return hitAt(el, b.x + b.width / 2, b.y + b.height / 2);
  };
  /** 整個看得到:中心點與四邊中點(內縮 3 px)都點得到;用邊的中點而不是四角,圓形元件(舵輪)的四角本來就不在形狀內 */
  const hitFully = (el: Element): Hit => {
    const b = el.getBoundingClientRect();
    const dx = Math.min(3, b.width / 4);
    const dy = Math.min(3, b.height / 4);
    const cx = b.x + b.width / 2;
    const cy = b.y + b.height / 2;
    const points: Array<[number, number]> = [
      [cx, cy],
      [b.left + dx, cy],
      [b.right - dx, cy],
      [cx, b.top + dy],
      [cx, b.bottom - dy],
    ];
    for (const [x, y] of points) {
      const h = hitAt(el, x, y);
      if (!h.ok) return h;
    }
    return { ok: true };
  };
  /** 元素的可見外框:與視窗及所有會裁切 / 捲動的祖先(overflow 不是 visible)的內距框取交集 */
  const visibleRect = (el: Element): Rect => {
    const b = el.getBoundingClientRect();
    const r: Rect = { left: Math.max(0, b.left), top: Math.max(0, b.top), right: Math.min(vw, b.right), bottom: Math.min(vh, b.bottom) };
    for (let n = parentOf(el); n; n = parentOf(n)) {
      if (!(n instanceof Element)) continue;
      const cs = getComputedStyle(n);
      const a = n.getBoundingClientRect();
      if (cs.overflowX !== 'visible') {
        r.left = Math.max(r.left, a.left + n.clientLeft);
        r.right = Math.min(r.right, a.left + n.clientLeft + n.clientWidth);
      }
      if (cs.overflowY !== 'visible') {
        r.top = Math.max(r.top, a.top + n.clientTop);
        r.bottom = Math.min(r.bottom, a.top + n.clientTop + n.clientHeight);
      }
    }
    return r;
  };

  // ---- 走訪整頁(含所有 shadow root):控制項與元件
  const controls: Element[] = [];
  const hosts: Element[] = [];
  const all: Element[] = [];
  const walk = (root: Document | ShadowRoot): void => {
    for (const el of Array.from(root.querySelectorAll('*'))) {
      all.push(el);
      if (el.shadowRoot) {
        hosts.push(el);
        walk(el.shadowRoot);
      }
      if (el.matches('button, input, select, textarea')) controls.push(el);
    }
  };
  walk(document);
  const panels = hosts.filter((h) => h.tagName.toLowerCase().startsWith('brm-'));

  const report: LayoutReport = {
    viewport: { width: vw, height: vh },
    controls: { total: 0, direct: 0, scrolled: 0 },
    scrollContainers: {},
    covered: [],
    clipped: [],
    overflow: [],
    overlap: [],
    offscreen: [],
    key: [],
    conning: [],
    text: [],
    page: [],
    hiddenClip: [],
    ellipsis: [],
  };

  // ---- 1) 每個控制項:直接點得到,或捲動(只靠使用者捲得動的容器)後點得到
  for (const el of controls) {
    if (!rendered(el)) continue;
    report.controls.total++;
    // 6) 按鈕內的文字(overflow: hidden 時被切掉;visible 時溢出鈕外,疊到鄰居上)
    if (el instanceof HTMLButtonElement && el.clientWidth > 0 && el.scrollWidth > el.clientWidth + TEXT_TOLERANCE_PX) {
      report.text.push(`${describe(el)} 文字寬 ${el.scrollWidth} > 鈕內寬 ${el.clientWidth}(overflow ${getComputedStyle(el).overflowX})`);
    }
    if (hitCenter(el).ok) {
      report.controls.direct++;
      continue;
    }
    const label = describe(el);
    const chain: Element[] = [];
    for (let n = parentOf(el); n; n = parentOf(n)) if (n instanceof Element) chain.push(n);
    const before = chain.map((n) => [n.scrollTop, n.scrollLeft] as const);
    el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
    const moved: string[] = [];
    const forbidden: string[] = [];
    chain.forEach((n, i) => {
      const dy = n.scrollTop !== before[i]![0];
      const dx = n.scrollLeft !== before[i]![1];
      if (!dx && !dy) return;
      const cs = getComputedStyle(n);
      const allowed = (!dy || userScrollable(cs.overflowY)) && (!dx || userScrollable(cs.overflowX));
      (allowed ? moved : forbidden).push(describeBox(n));
    });
    const hit = hitCenter(el);
    chain.forEach((n, i) => {
      n.scrollTop = before[i]![0];
      n.scrollLeft = before[i]![1];
    });
    if (hit.ok && forbidden.length === 0) {
      report.controls.scrolled++;
      const key = moved.join(' › ') || '(未捲動)';
      report.scrollContainers[key] = (report.scrollContainers[key] ?? 0) + 1;
    } else if (hit.ok) {
      report.clipped.push(`${label} 被 ${forbidden.join('、')} 裁切(overflow 不是 auto / scroll,使用者捲不到)`);
    } else if (hit.why === 'covered') {
      report.covered.push(`${label} 被 ${hit.by} 蓋住${moved.length > 0 ? `(已捲動 ${moved.join(' › ')})` : ''}`);
    } else {
      report.clipped.push(`${label} 在視窗外,沒有可捲動的容器`);
    }
  }

  // ---- 2) 關鍵控制與關鍵清單
  const hostsOf = (selector: string): Element[] => panels.filter((h) => h.matches(selector) && rendered(h));
  for (const k of spec.keys) {
    const found: Element[] = [];
    for (const h of hostsOf(k.host)) for (const el of Array.from(h.shadowRoot!.querySelectorAll(k.inner))) if (rendered(el)) found.push(el);
    const min = k.min ?? 1;
    if (found.length < min) report.key.push(`${k.name}:找到 ${found.length} 個(${k.host} ${k.inner}),至少要 ${min} 個`);
    for (const el of found) {
      const h = hitFully(el);
      if (!h.ok) report.key.push(`${k.name}:${describe(el)} 不捲動時${h.why === 'covered' ? `被 ${h.by} 蓋住` : '在視窗外'}`);
    }
  }
  for (const k of spec.lists) {
    const lists: Element[] = [];
    for (const h of hostsOf(k.host)) for (const el of Array.from(h.shadowRoot!.querySelectorAll(k.list))) lists.push(el);
    if (lists.length === 0) report.key.push(`${k.name}:找不到清單(${k.host} ${k.list})`);
    for (const list of lists) {
      const r = visibleRect(list);
      const height = Math.max(0, r.bottom - r.top);
      if (height < k.minHeightPx) report.key.push(`${k.name}:清單可見高度 ${Math.round(height)} px < ${k.minHeightPx} px`);
      // 可見高度的最上與 minHeightPx 處都必須點得到清單本身(沒有被別的面板蓋住)
      const lb = list.getBoundingClientRect();
      const cx = lb.x + lb.width / 2;
      for (const y of [r.top + 4, r.top + k.minHeightPx - 4]) {
        if (y >= r.bottom) continue;
        const top = deepAt(cx, y);
        if (!top || !isInside(top, list)) report.key.push(`${k.name}:清單在 y ${Math.round(y)} 被 ${top ? describeBox(top) : '視窗外'} 蓋住`);
      }
      const rows = Array.from(list.querySelectorAll(k.rows)).slice(0, k.minRows);
      rows.forEach((row, i) => {
        const b = row.getBoundingClientRect();
        if (b.top < r.top - EDGE_TOLERANCE_PX || b.bottom > r.bottom + EDGE_TOLERANCE_PX) {
          report.key.push(`${k.name}:第 ${i + 1} 列(y ${Math.round(b.top)}–${Math.round(b.bottom)})超出清單可見範圍(y ${Math.round(r.top)}–${Math.round(r.bottom)})`);
        } else {
          const h = hitAt(row, b.x + b.width / 2, b.y + b.height / 2);
          if (!h.ok) report.key.push(`${k.name}:第 ${i + 1} 列被 ${h.by} 蓋住`);
        }
      });
    }
  }

  // ---- 3) 面板:內容不溢出、互不重疊、不在視窗內者位於可捲動容器內
  const shown: Array<{ el: Element; name: string; rect: Rect }> = [];
  for (const p of panels) {
    const b = p.getBoundingClientRect();
    const cs = getComputedStyle(p);
    if (b.width <= 0 || cs.display === 'none' || cs.display === 'inline' || cs.visibility === 'hidden') continue;
    const name = p.tagName.toLowerCase();
    const dh = p.scrollHeight - p.clientHeight;
    const dw = p.scrollWidth - p.clientWidth;
    if (dh > OVERFLOW_TOLERANCE_PX && cs.overflowY === 'visible') report.overflow.push(`${name} 內容高 ${p.scrollHeight} > 外框 ${p.clientHeight}`);
    if (dw > OVERFLOW_TOLERANCE_PX && cs.overflowX === 'visible') report.overflow.push(`${name} 內容寬 ${p.scrollWidth} > 外框 ${p.clientWidth}`);
    if (dh > OVERFLOW_TOLERANCE_PX && (cs.overflowY === 'hidden' || cs.overflowY === 'clip')) report.hiddenClip.push(`${name} 內容高 ${p.scrollHeight} > 外框 ${p.clientHeight}(overflow ${cs.overflowY})`);
    // 元件內的根容器(.tile / .panel)以 overflow: hidden 裁掉內容
    for (const inner of Array.from(p.shadowRoot!.querySelectorAll(':scope > .tile, :scope > .panel'))) {
      const ics = getComputedStyle(inner);
      const idh = inner.scrollHeight - inner.clientHeight;
      if (idh > OVERFLOW_TOLERANCE_PX && (ics.overflowY === 'hidden' || ics.overflowY === 'clip')) report.hiddenClip.push(`${name} ${inner.tagName.toLowerCase()}${classesOf(inner)} 內容高 ${inner.scrollHeight} > 外框 ${inner.clientHeight}(overflow ${ics.overflowY})`);
    }
    if (cs.position === 'absolute') {
      // 浮貼的面板(航跡圖左上角的讀數)本來就疊在定位容器內的面板上,不算重疊;但可見外框不可超出定位容器
      // (最近的 position 不是 static 的祖先,穿過 shadow DOM),否則會蓋住旁邊的面板(pointer-events: none 時點擊判定看不出來)
      let cb: Element | null = null;
      for (let n = parentOf(p); n && !cb; n = parentOf(n)) if (n instanceof Element && getComputedStyle(n).position !== 'static') cb = n;
      if (cb) {
        const a = cb.getBoundingClientRect();
        const r = visibleRect(p);
        const out = Math.max(a.left - r.left, a.top - r.top, r.right - a.right, r.bottom - a.bottom);
        if (r.right > r.left && r.bottom > r.top && out > EDGE_TOLERANCE_PX) {
          report.overflow.push(`${name}(浮貼)可見外框 x ${Math.round(r.left)}–${Math.round(r.right)}、y ${Math.round(r.top)}–${Math.round(r.bottom)} 超出定位容器 ${describeBox(cb)}(x ${Math.round(a.left)}–${Math.round(a.right)}、y ${Math.round(a.top)}–${Math.round(a.bottom)})`);
        }
      }
      continue;
    }
    if (cs.position === 'fixed') continue;
    // 部分在視窗外:必須有使用者捲得動的祖先
    if (b.top < -EDGE_TOLERANCE_PX || b.left < -EDGE_TOLERANCE_PX || b.bottom > vh + EDGE_TOLERANCE_PX || b.right > vw + EDGE_TOLERANCE_PX) {
      let scroller = false;
      for (let n = parentOf(p); n && !scroller; n = parentOf(n)) {
        if (!(n instanceof Element)) continue;
        const ncs = getComputedStyle(n);
        scroller = userScrollable(ncs.overflowY) || userScrollable(ncs.overflowX);
      }
      if (!scroller) report.offscreen.push(`${name} 外框 x ${Math.round(b.left)}–${Math.round(b.right)}、y ${Math.round(b.top)}–${Math.round(b.bottom)} 超出視窗 ${vw}×${vh},且不在可捲動的容器內`);
    }
    const rect = visibleRect(p);
    if (rect.right - rect.left > EDGE_TOLERANCE_PX && rect.bottom - rect.top > EDGE_TOLERANCE_PX) shown.push({ el: p, name, rect });
  }
  for (let i = 0; i < shown.length; i++) {
    for (let j = i + 1; j < shown.length; j++) {
      const a = shown[i]!;
      const b = shown[j]!;
      if (isInside(a.el, b.el) || isInside(b.el, a.el)) continue;
      const w = Math.min(a.rect.right, b.rect.right) - Math.max(a.rect.left, b.rect.left);
      const h = Math.min(a.rect.bottom, b.rect.bottom) - Math.max(a.rect.top, b.rect.top);
      if (w > EDGE_TOLERANCE_PX && h > EDGE_TOLERANCE_PX) report.overlap.push(`${a.name} 與 ${b.name} 重疊 ${Math.round(w)}×${Math.round(h)} px`);
    }
  }

  // ---- 4) conning 條:各方塊內的元素沒有水平裁切
  for (const bar of hostsOf('brm-conning-bar')) {
    for (const tile of Array.from(bar.shadowRoot!.querySelectorAll('.bar > *'))) {
      if (!tile.shadowRoot) continue;
      for (const el of Array.from(tile.shadowRoot.querySelectorAll('*'))) {
        if (!(el instanceof HTMLElement) || el.clientWidth <= 0) continue;
        if (el.scrollWidth > el.clientWidth + CONNING_TOLERANCE_PX) report.conning.push(`${tile.tagName.toLowerCase()} ${el.tagName.toLowerCase()}${classesOf(el)} scrollWidth ${el.scrollWidth} > clientWidth ${el.clientWidth}`);
      }
    }
  }

  // ---- 7) 以 … 截斷的文字(資訊)
  for (const el of all) {
    if (!(el instanceof HTMLElement) || el.clientWidth <= 0 || el.scrollWidth <= el.clientWidth + TEXT_TOLERANCE_PX) continue;
    if (getComputedStyle(el).textOverflow !== 'ellipsis' || !rendered(el)) continue;
    report.ellipsis.push(`${describeBox(el)}「${(el.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 24)}」 scrollWidth ${el.scrollWidth} > clientWidth ${el.clientWidth}`);
  }

  // ---- 5) 整頁不捲動
  const doc = document.documentElement;
  if (doc.scrollWidth > vw) report.page.push(`html scrollWidth ${doc.scrollWidth} > 視窗寬 ${vw}`);
  if (doc.scrollHeight > vh) report.page.push(`html scrollHeight ${doc.scrollHeight} > 視窗高 ${vh}`);
  return report;
}

/** 量測結果中不符合驗收的項目(空陣列 = 通過);hiddenClip、ellipsis 與 scrollContainers 只是資訊,不列入 */
export function layoutFailures(report: LayoutReport): string[] {
  return [
    ...report.covered.map((s) => `蓋住:${s}`),
    ...report.clipped.map((s) => `裁切:${s}`),
    ...report.key.map((s) => `關鍵控制:${s}`),
    ...report.overflow.map((s) => `溢出:${s}`),
    ...report.overlap.map((s) => `重疊:${s}`),
    ...report.offscreen.map((s) => `視窗外:${s}`),
    ...report.conning.map((s) => `conning 裁切:${s}`),
    ...report.text.map((s) => `鈕內文字:${s}`),
    ...report.page.map((s) => `整頁捲動:${s}`),
  ];
}

/** 駕駛台主畫面(index.html):不必捲動就要看得到並點得到的控制 */
export const BRIDGE_LAYOUT: LayoutSpec = {
  keys: [
    { name: '警報面板「靜音 30s」', host: 'brm-alert-panel', inner: '.head button.silence' },
    { name: '警報面板「全部確認」', host: 'brm-alert-panel', inner: '.head button.ack-all' },
    { name: '警報面板「聲響」', host: 'brm-alert-panel', inner: '.head button.sound' },
    { name: 'BNWAS 重設', host: 'brm-bnwas', inner: 'button.reset' },
    { name: 'VHF 頻道鈕', host: 'brm-vhf', inner: '.channels button', min: 14 },
    { name: 'VHF PTT', host: 'brm-vhf', inner: 'button.ptt' },
    // 第二十九輪 L3:失電等緊急情況下要用的文字通訊與 DSC(header 因故障徽章折成兩行時也必須不捲動就點得到)
    { name: 'VHF 文字欄', host: 'brm-vhf', inner: '.text-row input' },
    { name: 'VHF 送出', host: 'brm-vhf', inner: 'button.send' },
    { name: 'VHF DSC DISTRESS', host: 'brm-vhf', inner: 'button.distress' },
    { name: 'VHF DSC URGENCY', host: 'brm-vhf', inner: 'button.urgency' },
    { name: '舵輪', host: 'brm-helm', inner: '.wheel' },
    { name: '舵令預設鈕', host: 'brm-helm', inner: '.presets button', min: 9 },
    { name: 'NFU 舵柄', host: 'brm-helm', inner: '.nfu button', min: 2 },
    { name: '車鐘', host: 'brm-telegraph', inner: '.list button', min: 11 },
    { name: '艏側推桿', host: 'brm-thruster-lever', inner: '.track' },
    { name: '艏側推快速鈕', host: 'brm-thruster-lever', inner: '.quick button', min: 5 },
    { name: '自動舵開關', host: 'brm-autopilot', inner: 'button.toggle' },
    { name: '自動舵設定航向', host: 'brm-autopilot', inner: '.set button, .set input', min: 5 },
    { name: '自動舵 ROT 限制', host: 'brm-autopilot', inner: '.rot input' },
    { name: '自動舵採用現在航向', host: 'brm-autopilot', inner: '.cur button' },
    { name: '教官小面板(凍結 / 恢復 / 重設)', host: 'brm-instructor', inner: '.buttons button', min: 3 },
  ],
  lists: [
    // 警報面板:至少 2 筆警報的高度(一筆約 36 px、間距 3 px)
    { name: '警報清單', host: 'brm-alert-panel', list: '.list', rows: '.alert', minRows: 2, minHeightPx: 76 },
  ],
};

/** 完整教官站(instructor.html 的 full 模式):不必捲動就要看得到並點得到的控制 */
export const INSTRUCTOR_LAYOUT: LayoutSpec = {
  keys: [
    { name: '執行控制「凍結」', host: 'brm-run-control', inner: 'button.freeze' },
    { name: '執行控制「恢復」', host: 'brm-run-control', inner: 'button.resume' },
    { name: '執行控制「重設」', host: 'brm-run-control', inner: 'button.reset' },
    { name: '執行控制「結束練習」', host: 'brm-run-control', inner: 'button.end' },
    { name: '執行控制 時間倍率', host: 'brm-run-control', inner: '.seg button', min: 5 },
    { name: '情境面板「載入」', host: 'brm-scenario-panel', inner: 'button.load' },
  ],
  lists: [
    // 事件串流:至少 3 列的高度(單行的一列約 20 px、間距 2 px;文字長的事件會換行,不逐列檢查)
    { name: '事件串流', host: 'brm-event-stream', list: '.list', rows: '.item', minRows: 0, minHeightPx: 64 },
  ],
};

/** 簡化教官模式「開始練習」後(instructor.html?mode=simple 的執行階段):凍結 / 結束、BRM 標記鈕與 VHF PTT 不必捲動就點得到 */
export const SIMPLE_RUN_LAYOUT: LayoutSpec = {
  keys: [
    { name: '簡化模式「凍結 / 恢復」', host: 'brm-instructor-app', inner: '.stage.running .actions button.freeze' },
    { name: '簡化模式「結束練習」', host: 'brm-instructor-app', inner: '.stage.running .actions button.end' },
    { name: 'BRM 標記鈕', host: 'brm-marker-bar', inner: '.buttons button.mark', min: 5 },
    { name: 'VHF PTT', host: 'brm-vhf', inner: 'button.ptt' },
  ],
  lists: [],
};

/** 重播檢視載入紀錄後(instructor.html?mode=replay):時間軸與事件串流篩選鈕不必捲動就點得到,事件串流有足夠的可見高度 */
export const REPLAY_LAYOUT: LayoutSpec = {
  keys: [
    { name: '重播「播放 / 暫停」', host: 'brm-replay-view', inner: '.timeline button.play' },
    { name: '重播逐筆 / 播放鈕', host: 'brm-replay-view', inner: '.timeline .btn-row button', min: 3 },
    { name: '重播的檔案選擇', host: 'brm-replay-view', inner: '.toolbar input.file-input' },
    { name: '重播時間軸', host: 'brm-replay-view', inner: '.timeline input.slider' },
    { name: '事件串流篩選鈕', host: 'brm-event-stream', inner: '.filters button', min: 6 },
  ],
  lists: [{ name: '事件串流', host: 'brm-event-stream', list: '.list', rows: '.item', minRows: 0, minHeightPx: 64 }],
};

/**
 * 在頁面內執行(page.evaluate):把 conning 條各方塊內所有文字的字距加大 em(0 = 還原),代表比量測用的字型更寬的字型
 * (Windows 的字型在容器裡量不到)。標題列的英文縮寫原本就有 0.04em 的字距,加在它上面。回傳處理的方塊數。
 * 樣式注入各方塊的 shadow root;之後方塊重新渲染也仍然有效(Lit 只更新樣板裡的節點)。
 */
export function widenConningText(em: number): number {
  const bars: Element[] = [];
  const find = (root: Document | ShadowRoot): void => {
    for (const el of Array.from(root.querySelectorAll('*'))) {
      if (el.tagName === 'BRM-CONNING-BAR') bars.push(el);
      if (el.shadowRoot) find(el.shadowRoot);
    }
  };
  find(document);
  let count = 0;
  for (const bar of bars) {
    for (const tile of Array.from(bar.shadowRoot!.querySelectorAll('.bar > *'))) {
      if (!tile.shadowRoot) continue;
      tile.shadowRoot.querySelector('style[data-e2e-widen]')?.remove();
      if (em > 0) {
        const style = document.createElement('style');
        style.setAttribute('data-e2e-widen', '1');
        style.textContent = `* { letter-spacing: ${em}em !important; } .tile-title, .tile-title > :not(.zh) { letter-spacing: ${0.04 + em}em !important; }`;
        tile.shadowRoot.appendChild(style);
      }
      count++;
    }
  }
  return count;
}

/** conning 條一個方塊的量測 */
export interface ConningTileReport {
  /** 元件標籤(brm-heading …) */
  tag: string;
  width: number;
  height: number;
  /** 方塊內容比方塊高多少 px(> 0 時下緣被方塊的 overflow: hidden 裁掉) */
  overflowY: number;
  /** 各數值(.value)的 computed font-size:鍵是「父元素的類別>數值的類別」,同一個鍵只記第一個 */
  fonts: Record<string, number>;
  /** 方塊內各圖形(svg)的寬高(px);寬或高是 0 表示圖形沒有顯示(display: none) */
  graphics: Array<{ label: string; width: number; height: number }>;
  /** 沒有畫出來的文字(display: none、visibility: hidden、opacity 0;不含 SVG 內的文字):修正不可以把內容藏起來 */
  hiddenText: string[];
  /** 互相重疊的兩段文字(各自的墨水範圍相交超過 1.5 px;不含 SVG 內的文字與祖孫關係的元素) */
  overlaps: string[];
  /** 被方塊邊界切掉的文字:墨水範圍超出方塊的裁切框(.tile 的邊框內緣)1 px 以上的那一邊與 px(不含 SVG 內的文字);整段在框外時註明「看不到」 */
  clipped: string[];
}

export interface ConningReport {
  /** 排成幾列(各方塊上緣的相異值個數) */
  rows: number;
  width: number;
  height: number;
  tiles: ConningTileReport[];
}

/** 在頁面內執行(page.evaluate):量可見的那一條 conning 條(頁面裡隱藏的另一條不算)。找不到時 tiles 是空陣列。 */
export function probeConning(): ConningReport {
  let bar: Element | null = null;
  const find = (root: Document | ShadowRoot): void => {
    for (const el of Array.from(root.querySelectorAll('*'))) {
      if (bar) return;
      if (el.tagName === 'BRM-CONNING-BAR' && el.getBoundingClientRect().width > 0) bar = el;
      else if (el.shadowRoot) find(el.shadowRoot);
    }
  };
  find(document);
  const out: ConningReport = { rows: 0, width: 0, height: 0, tiles: [] };
  if (!bar) return out;
  const host = bar as Element;
  const hb = host.getBoundingClientRect();
  out.width = hb.width;
  out.height = hb.height;
  const classes = (el: Element | null): string => (el ? Array.from(el.classList).map((c) => `.${c}`).join('') : '');
  const canvas = document.createElement('canvas').getContext('2d')!;
  const tops = new Set<number>();
  for (const tile of Array.from(host.shadowRoot!.querySelectorAll('.bar > *'))) {
    const b = tile.getBoundingClientRect();
    tops.add(Math.round(b.top));
    const root = tile.shadowRoot;
    const inner = root?.querySelector('.tile');
    const fonts: Record<string, number> = {};
    for (const v of Array.from(root?.querySelectorAll('.value') ?? [])) {
      const key = `${classes(v.parentElement)}>${classes(v)}`;
      if (!(key in fonts)) fonts[key] = parseFloat(getComputedStyle(v).fontSize);
    }
    const graphics = Array.from(root?.querySelectorAll('svg') ?? []).map((svg) => {
      const r = svg.getBoundingClientRect();
      return { label: svg.getAttribute('aria-label') ?? classes(svg.parentElement), width: r.width, height: r.height };
    });
    // 文字:沒有畫出來的(沒有任何行框,或 visibility / opacity 讓它看不見)與互相重疊的。
    // 重疊以墨水範圍判定(行框含字型的上下留白,line-height: 1 的大字與相鄰的小字行框會相交但字沒有碰到):
    // 基線 = 行框中線對齊字型的升降部,墨水 = 基線 − 實際升部 … 基線 + 實際降部(canvas measureText)
    const hiddenText: string[] = [];
    const inks: Array<{ left: number; right: number; top: number; bottom: number; text: string; parent: Element }> = [];
    // 方塊的裁切框:.tile 是 overflow: hidden,內容超出它的邊框內緣(padding box)就看不到
    const clip = { left: -Infinity, right: Infinity, top: -Infinity, bottom: Infinity };
    if (inner) {
      const ib = inner.getBoundingClientRect();
      clip.left = ib.left + inner.clientLeft;
      clip.top = ib.top + inner.clientTop;
      clip.right = clip.left + inner.clientWidth;
      clip.bottom = clip.top + inner.clientHeight;
    }
    if (root) {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = (node.textContent ?? '').replace(/\s+/g, ' ').trim();
        const parent = node.parentElement;
        if (!text || !parent || parent.tagName === 'STYLE' || parent instanceof SVGElement) continue;
        const cs = getComputedStyle(parent);
        const range = document.createRange();
        range.selectNodeContents(node);
        const rects = Array.from(range.getClientRects()).filter((r) => r.width > 0.5);
        if (rects.length === 0 || cs.visibility === 'hidden' || parseFloat(cs.opacity) === 0) {
          hiddenText.push(text);
          continue;
        }
        canvas.font = `${cs.fontStyle} ${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
        const m = canvas.measureText(text);
        for (const r of rects) {
          const baseline = r.top + (r.height - (m.fontBoundingBoxAscent + m.fontBoundingBoxDescent)) / 2 + m.fontBoundingBoxAscent;
          inks.push({ left: r.left, right: r.right, top: baseline - m.actualBoundingBoxAscent, bottom: baseline + m.actualBoundingBoxDescent, text: text.slice(0, 24), parent });
        }
      }
    }
    const clipped: string[] = [];
    for (const ink of inks) {
      const sides: Array<[string, number, boolean]> = [
        ['左', clip.left - ink.left, ink.right <= clip.left],
        ['右', ink.right - clip.right, ink.left >= clip.right],
        ['上', clip.top - ink.top, ink.bottom <= clip.top],
        ['下', ink.bottom - clip.bottom, ink.top >= clip.bottom],
      ];
      for (const [side, px, gone] of sides) if (px > 1) clipped.push(`「${ink.text}」${side}緣被切 ${px.toFixed(1)} px${gone ? '(整段看不到)' : ''}`);
    }
    const overlaps: string[] = [];
    for (let i = 0; i < inks.length; i++) {
      for (let j = i + 1; j < inks.length; j++) {
        const p = inks[i]!;
        const q = inks[j]!;
        if (p.parent === q.parent || p.parent.contains(q.parent) || q.parent.contains(p.parent)) continue;
        const w = Math.min(p.right, q.right) - Math.max(p.left, q.left);
        const h = Math.min(p.bottom, q.bottom) - Math.max(p.top, q.top);
        if (w > 1.5 && h > 1.5) overlaps.push(`「${p.text}」×「${q.text}」 ${w.toFixed(1)}×${h.toFixed(1)} px`);
      }
    }
    out.tiles.push({ tag: tile.tagName.toLowerCase(), width: b.width, height: b.height, overflowY: inner ? inner.scrollHeight - inner.clientHeight : 0, fonts, graphics, hiddenText, overlaps, clipped });
  }
  out.rows = tops.size;
  return out;
}

/** overrideConning 的參數:patch 蓋在目前(核心或紀錄來的)狀態上,值為 null 的欄位刪除;config 蓋在目前的船舶顯示設定上 */
export interface ConningOverride {
  patch: Record<string, unknown>;
  config?: Record<string, unknown>;
  noData?: boolean;
  issues?: string[];
  setHeading?: number | null;
}

/**
 * 在頁面內執行(page.evaluate):把可見的那一條 conning 條顯示的狀態換成指定的值 —— 以實例屬性蓋過 Lit 的存取子,頁面之後每一幀的指定都被忽略,
 * 所以畫面停在給定的狀態(駕駛台、完整教官站、重播檢視都可用)。選填欄位(route / visibility_nm、waves / waveMotion / shipInteraction、roll)
 * 決定格數。回傳排版完成後的格數;找不到 conning 條時回傳 -1。
 */
export function overrideConning(arg: ConningOverride): Promise<number> {
  type Bar = HTMLElement & { state: Record<string, unknown>; config: Record<string, unknown>; requestUpdate(): void; updateComplete: Promise<unknown>; __held?: Record<string, unknown>; __base?: { state: Record<string, unknown>; config: Record<string, unknown> } };
  let found: Element | null = null;
  const find = (root: Document | ShadowRoot): void => {
    for (const el of Array.from(root.querySelectorAll('*'))) {
      if (found) return;
      if (el.tagName === 'BRM-CONNING-BAR' && el.getBoundingClientRect().width > 0) found = el;
      else if (el.shadowRoot) find(el.shadowRoot);
    }
  };
  find(document);
  if (!found) return Promise.resolve(-1);
  const bar = found as Bar;
  if (!bar.__held) {
    bar.__base = { state: bar.state, config: bar.config };
    const held: Record<string, unknown> = {};
    bar.__held = held;
    for (const key of ['state', 'config', 'noData', 'issues', 'setHeading']) Object.defineProperty(bar, key, { configurable: true, get: () => held[key], set: () => undefined });
  }
  const state: Record<string, unknown> = { ...bar.__base!.state };
  for (const [key, value] of Object.entries(arg.patch)) {
    if (value === null) delete state[key];
    else state[key] = value;
  }
  Object.assign(bar.__held, { state, config: { ...bar.__base!.config, ...(arg.config ?? {}) }, noData: arg.noData ?? false, issues: arg.issues ?? [], setHeading: arg.setHeading ?? undefined });
  bar.requestUpdate();
  const tiles = (): Array<Element & { updateComplete?: Promise<unknown> }> => Array.from(bar.shadowRoot!.querySelectorAll('.bar > *'));
  return bar.updateComplete
    .then(() => Promise.all(tiles().map((t) => t.updateComplete)))
    .then(() => new Promise<number>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve(tiles().length)))));
}
