// 情境編輯器「海圖點選編輯」面板與航跡圖的編輯模式/流場層(happy-dom):由航線/區域/船與初始/目標船的「在圖上編輯」鈕開啟、
// 內嵌 <brm-track-plot editable> 的點擊新增(航點/頂點/自船/目標船)、拖曳移動、右鍵刪除、方向鍵微調與表格同步;
// 環境進階的網格產生器(均勻流、兩端線性內插)與網格預覽;航跡圖工具列「流」開關。畫布無尺寸時以 640 × 480 換算。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import type { ScenarioEditor } from '../src/components/instructor/scenario-editor.js';
import type { TrackPlot } from '../src/components/track-plot.js';
import { routeWaypoints, zonePolygon, type RawSection } from '../src/lib/scenario-editor.js';
import { enuToLatLon } from '../src/lib/plot-editing.js';
import { parseScenario } from '../src/lib/scenario.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };
type Editor = ScenarioEditor & El;

const MINI_YAML = `id: MINI
name: 迷你範本
ship: { id: FSB1, loading: full }
origin: { lat: 23.78, lon: 120.1 }
initial: { position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8, telegraph: HAH }
environment:
  wind: { trueSpeed: 18, trueDir: 40, gustiness: 0.15 }
  current: { set: 230, drift: 2.0 }
  waterDepth: 14
route:
  name: 進港
  waypoints:
    - { name: WP1, lat: 23.77, lon: 120.09 }
    - { name: WP2, lat: 23.78, lon: 120.12, checkpoint: true, speedLimit_kn: 8 }
zones:
  - id: anchorage_S
    name: 南側錨地
    type: noGo
    polygon: [{ lat: 23.785, lon: 120.125 }, { lat: 23.79, lon: 120.15 }, { lat: 23.78, lon: 120.152 }]
targets:
  - id: ANCH1
    name: SEA PEARL
    initial: { position: { lat: 23.783, lon: 120.138 }, heading: 40 }
    behaviour: { mode: anchored }
timeline:
  - id: end
    when: { type: time, atLeast: 1800 }
    actions:
      - endExercise: { reason: 時間到 }
assessment:
  hardFail:
    - { id: grounding, rule: grounding }
  scoring: { start: 100, passMark: 70 }
`;

const MINI = parseScenario(MINI_YAML, 'data/scenarios/MINI.yaml');
const SOURCES = { [MINI.path]: MINI_YAML };
const ORIGIN = { lat: 23.78, lon: 120.1 };

function q<T extends HTMLElement>(el: HTMLElement, selector: string): T {
  const found = el.shadowRoot!.querySelector<T>(selector);
  if (!found) throw new Error(`缺元素 ${selector}`);
  return found;
}

function count(el: HTMLElement, selector: string): number {
  return el.shadowRoot!.querySelectorAll(selector).length;
}

async function setInput(el: El, selector: string, value: string): Promise<void> {
  const input = q<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(el, selector);
  input.value = value;
  input.dispatchEvent(new Event('change', { bubbles: true }));
  await el.updateComplete;
}

async function click(el: El, selector: string): Promise<void> {
  q<HTMLButtonElement>(el, selector).click();
  await el.updateComplete;
}

async function mountEditor(): Promise<Editor> {
  const el = document.createElement('brm-scenario-editor') as Editor;
  Object.assign(el, { scenarios: [MINI], sources: SOURCES });
  document.body.appendChild(el);
  await el.updateComplete;
  await setInput(el, 'select.template', 'MINI');
  await click(el, 'button.apply-template');
  expect(el.lastValidation.issues).toEqual([]);
  return el;
}

function plotOf(el: Editor): TrackPlot & El {
  return q<TrackPlot & El>(el, '.plot-editor brm-track-plot');
}

function canvasOf(plot: TrackPlot): HTMLCanvasElement {
  return plot.shadowRoot!.querySelector('canvas')!;
}

function pointer(type: string, px: number, py: number, init: PointerEventInit = {}): PointerEvent {
  return new PointerEvent(type, { clientX: px, clientY: py, button: 0, pointerId: 1, bubbles: true, composed: true, ...init });
}

/** 在畫布像素座標點一下(pointerdown + pointerup,不移動)。 */
async function clickPlot(el: Editor, px: number, py: number): Promise<void> {
  const plot = plotOf(el);
  const canvas = canvasOf(plot);
  canvas.dispatchEvent(pointer('pointerdown', px, py));
  canvas.dispatchEvent(pointer('pointerup', px, py));
  await plot.updateComplete;
  await el.updateComplete;
}

/** 由把手拖到目標像素。 */
async function dragHandle(el: Editor, key: string, toPx: number, toPy: number): Promise<void> {
  const plot = plotOf(el);
  const canvas = canvasOf(plot);
  const h = plot.handles.find((x) => x.key === key);
  if (!h) throw new Error(`缺把手 ${key}`);
  const [px, py] = plot.enuToPx(h.x, h.y);
  canvas.dispatchEvent(pointer('pointerdown', px, py));
  canvas.dispatchEvent(pointer('pointermove', (px + toPx) / 2, (py + toPy) / 2));
  canvas.dispatchEvent(pointer('pointermove', toPx, toPy));
  canvas.dispatchEvent(pointer('pointerup', toPx, toPy));
  await plot.updateComplete;
  await el.updateComplete;
}

function near(actual: number, expected: number, tol: number): void {
  expect(Math.abs(actual - expected)).toBeLessThanOrEqual(tol);
}

beforeAll(async () => {
  await import('../src/components/instructor/scenario-editor.js');
});

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  document.body.innerHTML = '';
  localStorage.clear();
});

describe('海圖點選編輯面板', () => {
  it('預設關閉;航線「在圖上編輯」開啟面板:對象為航線、把手為航點/自船/目標船、疊圖含航線與區域、提示含原點與示意幾何', async () => {
    const el = await mountEditor();
    expect(count(el, '.plot-editor')).toBe(0);
    expect(el.plotEditTarget).toBeNull();
    await click(el, 'button.route-plot');
    expect(el.plotEditTarget).toEqual({ kind: 'route' });
    expect(q<HTMLSelectElement>(el, 'select.plot-target').value).toBe('route');
    const plot = plotOf(el);
    await plot.updateComplete;
    expect(plot.editable).toBe(true);
    expect(plot.fieldPreview).toBe(true);
    expect(plot.handles.map((h) => h.key)).toEqual(['wp:0', 'wp:1', 'own', 'target:0']);
    expect(plot.overlay?.route?.waypoints).toHaveLength(2);
    expect(plot.overlay?.zones).toHaveLength(1);
    expect(plot.origin).toEqual(ORIGIN);
    expect(canvasOf(plot).getAttribute('tabindex')).toBe('0');
    const hint = q<HTMLElement>(el, '.plot-hint').textContent!.replace(/\s+/g, ' ');
    expect(hint).toContain('原點 23.7800°N 120.1000°E(origin)');
    expect(hint).toContain('新點以經緯度寫入');
    expect(q<HTMLElement>(el, 'button.route-plot').className).toContain('active');
    // 讀數列顯示操作提示
    expect(plot.shadowRoot!.querySelector('.readout')!.textContent).toContain('點擊空白處新增航點');
    await click(el, 'button.plot-close');
    expect(count(el, '.plot-editor')).toBe(0);
  });

  it('點擊空白處新增航點(經緯度與像素換算一致、表格同步 3 列);落在航段附近則插入中間;右鍵刪除', async () => {
    const el = await mountEditor();
    await click(el, 'button.route-plot');
    const plot = plotOf(el);
    await plot.updateComplete;
    const expected = plot.pxToEnu(600, 60);
    await clickPlot(el, 600, 60);
    const wps = routeWaypoints(el.value);
    expect(wps).toHaveLength(3);
    expect(count(el, '.row.wp')).toBe(3);
    const g = enuToLatLon(ORIGIN, expected.x, expected.y);
    near(wps[2]!['lat'] as number, g.lat, 1e-5);
    near(wps[2]!['lon'] as number, g.lon, 1e-5);
    expect(wps[2]!['name']).toBe('WP3');
    expect(q<HTMLInputElement>(el, '.row.wp[data-index="2"] input.wp-name').value).toBe('WP3');
    // 新點成為選取把手
    expect(plotOf(el).selectedKey).toBe('wp:2');
    // 航段中點附近 → 插入 WP1 與 WP2 之間
    const p = plotOf(el);
    const a = p.handles.find((h) => h.key === 'wp:0')!;
    const b = p.handles.find((h) => h.key === 'wp:1')!;
    const [mx, my] = p.enuToPx((a.x + b.x) / 2, (a.y + b.y) / 2);
    await clickPlot(el, mx + 3, my);
    expect(routeWaypoints(el.value).map((w) => w['name'])).toEqual(['WP1', 'WP4', 'WP2', 'WP3']);
    // 右鍵刪除 WP4
    const p2 = plotOf(el);
    const h4 = p2.handles.find((h) => h.key === 'wp:1')!;
    const [hx, hy] = p2.enuToPx(h4.x, h4.y);
    canvasOf(p2).dispatchEvent(new MouseEvent('contextmenu', { clientX: hx, clientY: hy, button: 2, bubbles: true, composed: true, cancelable: true }));
    await el.updateComplete;
    expect(routeWaypoints(el.value).map((w) => w['name'])).toEqual(['WP1', 'WP2', 'WP3']);
    expect(plotOf(el).selectedKey).toBeNull();
  });

  it('拖曳航點移動(其他鍵保留、表格同步);純點擊把手只選取不移動;方向鍵微調選取的把手,Delete 刪除', async () => {
    const el = await mountEditor();
    await click(el, 'button.route-plot');
    const plot = plotOf(el);
    await plot.updateComplete;
    const before = routeWaypoints(el.value)[1]!;
    await dragHandle(el, 'wp:1', 500, 100);
    const after = routeWaypoints(el.value)[1]!;
    const want = enuToLatLon(ORIGIN, plot.pxToEnu(500, 100).x, plot.pxToEnu(500, 100).y);
    near(after['lat'] as number, want.lat, 1e-5);
    near(after['lon'] as number, want.lon, 1e-5);
    expect(after['lat']).not.toBe(before['lat']);
    expect(after).toMatchObject({ name: 'WP2', checkpoint: true, speedLimit_kn: 8 });
    expect(q<HTMLInputElement>(el, '.row.wp[data-index="1"] input.wp-pos-lat').value).toBe(String(after['lat']));
    // 純點擊把手:選取、不移動、不新增
    const p = plotOf(el);
    const h0 = p.handles.find((h) => h.key === 'wp:0')!;
    const [hx, hy] = p.enuToPx(h0.x, h0.y);
    await clickPlot(el, hx + 2, hy - 1);
    expect(routeWaypoints(el.value)).toHaveLength(2);
    expect(routeWaypoints(el.value)[0]).toEqual({ name: 'WP1', lat: 23.77, lon: 120.09 });
    expect(plotOf(el).selectedKey).toBe('wp:0');
    expect(plotOf(el).shadowRoot!.querySelector('.readout')!.textContent).toContain('選取 WP1');
    // 方向鍵:→ 一步(zoom 0.6 → 20 m),Shift ↑ 200 m
    const canvas = canvasOf(plotOf(el));
    canvas.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, composed: true, cancelable: true }));
    await el.updateComplete;
    const h0b = plotOf(el).handles.find((h) => h.key === 'wp:0')!;
    near(h0b.x - h0.x, 20, 0.6);
    near(h0b.y - h0.y, 0, 0.6);
    canvasOf(plotOf(el)).dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', shiftKey: true, bubbles: true, composed: true, cancelable: true }));
    await el.updateComplete;
    const h0c = plotOf(el).handles.find((h) => h.key === 'wp:0')!;
    near(h0c.y - h0.y, 200, 0.6);
    // Delete 刪除選取的航點
    canvasOf(plotOf(el)).dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true, composed: true, cancelable: true }));
    await el.updateComplete;
    expect(routeWaypoints(el.value).map((w) => w['name'])).toEqual(['WP2']);
    // Esc 取消選取
    canvasOf(plotOf(el)).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, composed: true, cancelable: true }));
    await el.updateComplete;
    expect(plotOf(el).selectedKey).toBeNull();
  });

  it('區域「在圖上編輯」:列該區頂點把手、點擊插入最近邊的頂點、拖曳頂點、表格同步;自船/目標船「圖上」點擊設定位置並可拖曳', async () => {
    const el = await mountEditor();
    await click(el, '.row.zone[data-index="0"] button.z-plot');
    expect(el.plotEditTarget).toEqual({ kind: 'zone', index: 0 });
    let plot = plotOf(el);
    await plot.updateComplete;
    expect(plot.handles.filter((h) => h.kind === 'vertex')).toHaveLength(3);
    // 在第 0–1 邊的中點外側點一下 → 4 個頂點,插在索引 1
    const v0 = plot.handles.find((h) => h.key === 'zone:0:0')!;
    const v1 = plot.handles.find((h) => h.key === 'zone:0:1')!;
    const [mx, my] = plot.enuToPx((v0.x + v1.x) / 2, (v0.y + v1.y) / 2);
    await clickPlot(el, mx, my - 4);
    const poly = zonePolygon(el.value.zones[0]!);
    expect(poly).toHaveLength(4);
    expect(poly[0]).toEqual({ lat: 23.785, lon: 120.125 });
    expect(poly[2]).toEqual({ lat: 23.79, lon: 120.15 });
    expect(plotOf(el).selectedKey).toBe('zone:0:1');
    await click(el, '.row.zone[data-index="0"] button.z-vertices');
    expect(count(el, '.row.vertex')).toBe(4);
    // 拖曳頂點 #3(索引 2)
    await dragHandle(el, 'zone:0:2', 100, 400);
    plot = plotOf(el);
    const moved = zonePolygon(el.value.zones[0]!)[2]!;
    const want = enuToLatLon(ORIGIN, plot.pxToEnu(100, 400).x, plot.pxToEnu(100, 400).y);
    near(moved['lat'] as number, want.lat, 1e-5);
    near(moved['lon'] as number, want.lon, 1e-5);
    expect(q<HTMLInputElement>(el, '.row.vertex[data-vertex="2"] input.z-v-lat').value).toBe(String(moved['lat']));
    // 自船
    await click(el, 'button.own-plot');
    expect(el.plotEditTarget).toEqual({ kind: 'own' });
    plot = plotOf(el);
    await plot.updateComplete;
    const target = plot.pxToEnu(320, 100);
    await clickPlot(el, 320, 100);
    const ownWant = enuToLatLon(ORIGIN, target.x, target.y);
    near(el.value.initial.position.lat as number, ownWant.lat, 1e-5);
    near(el.value.initial.position.lon as number, ownWant.lon, 1e-5);
    expect(el.value.initial.heading).toBe(52);
    expect(q<HTMLInputElement>(el, 'input.own-lat').value).toBe(String(el.value.initial.position.lat));
    // 目標船
    await click(el, '.row.target[data-index="0"] button.t-plot');
    expect(el.plotEditTarget).toEqual({ kind: 'target', index: 0 });
    plot = plotOf(el);
    await plot.updateComplete;
    const tp = plot.pxToEnu(50, 50);
    await clickPlot(el, 50, 50);
    const tWant = enuToLatLon(ORIGIN, tp.x, tp.y);
    near(el.value.targets[0]!.position.lat as number, tWant.lat, 1e-5);
    near(el.value.targets[0]!.position.lon as number, tWant.lon, 1e-5);
    expect(el.value.targets[0]!.heading).toBe(40);
    expect(q<HTMLInputElement>(el, '.row.target[data-index="0"] input.t-pos-lat').value).toBe(String(el.value.targets[0]!.position.lat));
    // 右鍵自船/目標船不刪除
    const p2 = plotOf(el);
    const own = p2.handles.find((h) => h.key === 'own')!;
    const [ox, oy] = p2.enuToPx(own.x, own.y);
    canvasOf(p2).dispatchEvent(new MouseEvent('contextmenu', { clientX: ox, clientY: oy, button: 2, bubbles: true, composed: true, cancelable: true }));
    await el.updateComplete;
    expect(el.value.targets).toHaveLength(1);
    expect(el.value.initial.position.lat).toBeDefined();
    // 以選單切換對象
    await setInput(el, 'select.plot-target', 'route');
    expect(el.plotEditTarget).toEqual({ kind: 'route' });
    expect(el.lastValidation.issues).toEqual([]);
  });

  it('ENU 寫法的區域在無 origin 時仍可編輯;新點寫 ENU 整數', async () => {
    const yaml = MINI_YAML.replace('origin: { lat: 23.78, lon: 120.1 }\n', '')
      .replace('initial: { position: { lat: 23.7696, lon: 120.0857 }', 'initial: { position: { x: 0, y: 0 }')
      .replace('polygon: [{ lat: 23.785, lon: 120.125 }, { lat: 23.79, lon: 120.15 }, { lat: 23.78, lon: 120.152 }]', 'polygon: [{ x: 500, y: 500 }, { x: 1500, y: 500 }, { x: 1500, y: 1500 }]');
    const s = parseScenario(yaml, 'data/scenarios/ENU.yaml');
    const el = document.createElement('brm-scenario-editor') as Editor;
    Object.assign(el, { scenarios: [s], sources: { [s.path]: yaml } });
    document.body.appendChild(el);
    await el.updateComplete;
    await setInput(el, 'select.template', 'MINI');
    await click(el, 'button.apply-template');
    await click(el, '.row.zone[data-index="0"] button.z-plot');
    const plot = plotOf(el);
    await plot.updateComplete;
    expect(plot.origin).toBeNull();
    expect(q<HTMLElement>(el, '.plot-hint').textContent).toContain('尚無原點');
    expect(plot.handles.map((h) => h.key)).toEqual(['zone:0:0', 'zone:0:1', 'zone:0:2', 'own']); // 經緯度航點/目標船不列
    const p = plot.pxToEnu(600, 450);
    await clickPlot(el, 600, 450);
    const poly = zonePolygon(el.value.zones[0]!);
    expect(poly).toHaveLength(4);
    const added = poly.find((v) => v['x'] === Math.round(p.x) && v['y'] === Math.round(p.y));
    expect(added).toBeDefined();
  });
});

describe('網格產生器與流場層', () => {
  async function enableField(el: Editor): Promise<void> {
    const input = q<HTMLInputElement>(el, 'input.field-enabled');
    input.checked = true;
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await el.updateComplete;
  }
  function fieldOf(el: Editor): RawSection {
    return el.value.currentModel['field'] as RawSection;
  }

  it('由均勻流產生網格:以環境面板 230°/2.0 kn 填滿 3×3;流向/流速為參數引用時顯示錯誤不改草稿', async () => {
    const el = await mountEditor();
    await enableField(el);
    expect(fieldOf(el)['u_kn']).toEqual([
      [0, 0, 0],
      [0, 0, 0],
      [0, 0, 0],
    ]);
    await click(el, 'button.gen-uniform');
    const u = fieldOf(el)['u_kn'] as number[][];
    const v = fieldOf(el)['v_kn'] as number[][];
    expect(u).toHaveLength(3);
    expect(u.every((r) => r.length === 3 && r.every((x) => x === u[0]![0]))).toBe(true);
    expect(u[0]![0]).toBeCloseTo(2 * Math.sin((230 * Math.PI) / 180), 3);
    expect(v[2]![2]).toBeCloseTo(2 * Math.cos((230 * Math.PI) / 180), 3);
    expect(q<HTMLTextAreaElement>(el, 'textarea.fg-u').value.split('\n')).toHaveLength(3);
    expect(el.lastValidation.issues).toEqual([]);
    await setInput(el, 'input.cur-set', '${params:tide.set}');
    await click(el, 'button.gen-uniform');
    expect(q<HTMLElement>(el, '.error').textContent).toContain('流向/流速需為數值');
    expect(fieldOf(el)['u_kn']).toEqual(u);
  });

  it('由兩端流速線性內插:沿 x 由 A(090°/1 kn)到 B(090°/3 kn);沿 y 依列;非數值顯示錯誤', async () => {
    const el = await mountEditor();
    await enableField(el);
    await setInput(el, 'input.fg-nx', '3');
    await setInput(el, 'input.fg-ny', '2');
    await setInput(el, 'textarea.fg-u', '0 0 0\n0 0 0');
    await setInput(el, 'textarea.fg-v', '0 0 0\n0 0 0');
    const set = async (sel: string, v: string) => {
      const input = q<HTMLInputElement>(el, sel);
      input.value = v;
    };
    await set('input.gen-a-set', '90');
    await set('input.gen-a-drift', '1');
    await set('input.gen-b-set', '90');
    await set('input.gen-b-drift', '3');
    await click(el, 'button.gen-linear');
    expect(fieldOf(el)['u_kn']).toEqual([
      [1, 2, 3],
      [1, 2, 3],
    ]);
    expect(fieldOf(el)['v_kn']).toEqual([
      [0, 0, 0],
      [0, 0, 0],
    ]);
    expect(el.lastValidation.issues).toEqual([]);
    const axis = q<HTMLSelectElement>(el, 'select.gen-axis');
    axis.value = 'y';
    await set('input.gen-a-set', '0');
    await set('input.gen-a-drift', '2');
    await set('input.gen-b-set', '180');
    await set('input.gen-b-drift', '2');
    await click(el, 'button.gen-linear');
    expect(fieldOf(el)['v_kn']).toEqual([
      [2, 2, 2],
      [-2, -2, -2],
    ]);
    await set('input.gen-b-drift', 'abc');
    await click(el, 'button.gen-linear');
    expect(q<HTMLElement>(el, '.error').textContent).toContain('端點 B 流速需為數值');
  });

  it('網格預覽:開啟海圖面板、對象為流場網格、疊圖帶 currentField、圖例標示流場層;工具列「流」切換 show-current', async () => {
    const el = await mountEditor();
    await enableField(el);
    await click(el, 'button.gen-uniform');
    await click(el, 'button.field-preview');
    expect(el.plotEditTarget).toEqual({ kind: 'field' });
    const plot = plotOf(el);
    await plot.updateComplete;
    expect(plot.overlay?.currentField?.grid).toEqual({ x0: -1000, y0: -1000, dx: 1000, dy: 1000, nx: 3, ny: 3 });
    expect(plot.showCurrent).toBe(true);
    const legend = plot.shadowRoot!.querySelector('.legend')!.textContent!;
    expect(legend).toContain('箭頭 = 流場網格');
    expect(q<HTMLElement>(el, '.plot-hint').textContent).toContain('網格 3×3 已畫');
    const btn = plot.shadowRoot!.querySelector<HTMLButtonElement>('button.layer-current')!;
    expect(btn.className).toContain('active');
    btn.click();
    await plot.updateComplete;
    expect(plot.showCurrent).toBe(false);
    expect(plot.shadowRoot!.querySelector('.legend')!.textContent).not.toContain('箭頭 = 流場網格');
    expect(plot.shadowRoot!.querySelector<HTMLButtonElement>('button.layer-current')!.className).not.toContain('active');
    // 點擊圖面不新增任何東西(預覽對象)
    const wps = routeWaypoints(el.value).length;
    await clickPlot(el, 300, 300);
    expect(routeWaypoints(el.value)).toHaveLength(wps);
    // 網格尺寸錯誤時提示未畫
    await setInput(el, 'input.fg-nx', '5');
    expect(plotOf(el).overlay?.currentField).toBeUndefined();
    expect(q<HTMLElement>(el, '.plot-hint').textContent).toContain('網格有問題,未畫');
  });
});
