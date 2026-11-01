// 簡化 ECDIS 頁元件煙霧測試(happy-dom):示範模式整頁可渲染(不開 WebSocket、不送指令)、工具列切換定向/量程/圖層與 EBL/VRM、
// 航線面板無航線說明與有航線數值、AIS 目標表點選、警報面板確認、海圖畫布幾何與點選工具。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { EcdisStore } from '../src/lib/ecdis/ecdis-store.js';
import { AlertSounder } from '../src/lib/alert-sound.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { OwnShipState } from '../src/types/state.js';
import type { TargetState } from '../src/types/target.js';
import type { EcdisChart } from '../src/components/ecdis/chart-canvas.js';

const flush = () => new Promise<void>((r) => setTimeout(r, 0));

type El = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}): Promise<T> {
  const el = document.createElement(tag) as T & { updateComplete: Promise<unknown> };
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function text(el: Element): string {
  return (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');
}

function state(extra: Partial<OwnShipState> = {}): OwnShipState {
  const s = defaultState('FSB1');
  s.pos = { lat: 23.8, lon: 120.05, x: 0, y: 0 };
  s.heading = 90;
  s.cog = 92;
  s.sog = 8;
  s.stw = 7.5;
  s.waterDepth = 30;
  s.t = 100;
  return { ...s, ...extra };
}

function target(id: string, cpa: number, tcpa: number): TargetState {
  return { id, name: id, mmsi: 416000000, shipType: 'tanker', loa: 150, beam: 25, pos: { lat: 23.81, lon: 120.06, x: 900, y: 1100 }, heading: 200, cog: 200, sog: 9, rot: 0, range_nm: 0.77, bearing_deg: 39, cpa_nm: cpa, tcpa_min: tcpa, aisOn: true, lights: 'powerDriven', behaviour: 'hold', ais: { mmsi: 416000000, name: `M/V ${id}`, callSign: 'BZZZ', navStatus: 0, destination: 'MAILIAO', draught: 9.5 } };
}

function makeStore(): EcdisStore {
  return new EcdisStore({ scenarios: () => [], areaFiles: {}, sounder: new AlertSounder({ storageKey: null, contextFactory: () => null }) });
}

beforeAll(async () => {
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(0), 0) as unknown as number);
  vi.stubGlobal('cancelAnimationFrame', (id: number) => clearTimeout(id));
  await import('../src/components/ecdis/ecdis-app.js');
});

describe('ECDIS 面板', () => {
  it('工具列:定向/量程/距離圈/圖層/EBL/VRM 操作寫入 store;電羅經無效時 C UP 停用', async () => {
    const store = makeStore();
    store.onState(state());
    const el = await mount<El>('ecdis-toolbar', { store });
    const buttons = () => [...el.shadowRoot!.querySelectorAll('button')];
    buttons().find((b) => b.textContent?.trim() === 'C UP')!.click();
    expect(store.settings.orientation).toBe('course-up');
    expect(store.courseRef_deg).toBe(90);
    buttons().find((b) => b.textContent?.trim() === '+')!.click();
    expect(store.settings.rangeNm).toBe(6);
    const checks = [...el.shadowRoot!.querySelectorAll('input[type=checkbox]')] as HTMLInputElement[];
    const rings = checks.find((c) => c.parentElement?.textContent?.includes('距離圈'))!;
    rings.checked = false;
    rings.dispatchEvent(new Event('change'));
    expect(store.settings.rangeRings).toBe(false);
    const depth = checks.find((c) => c.parentElement?.textContent?.includes('水深著色'))!;
    depth.checked = false;
    depth.dispatchEvent(new Event('change'));
    expect(store.settings.layers.depth).toBe(false);
    buttons().find((b) => b.textContent?.trim() === 'EBL2')!.click();
    expect(store.settings.tool).toBe('ebl2');
    const ebl1 = checks.find((c) => c.parentElement?.textContent?.includes('EBL1'))!;
    ebl1.checked = true;
    ebl1.dispatchEvent(new Event('change'));
    expect(store.ebl[0].on).toBe(true);
    expect(text(el)).toContain('安全等深線');
    expect(text(el)).toContain('5.71 m'); // FSB1 滿載最大吃水(艉 5.711)
    store.onState(state({ sensors: { headingValid: false, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 90 } }));
    await el.updateComplete;
    expect((buttons().find((b) => b.textContent?.trim() === 'C UP') as HTMLButtonElement).disabled).toBe(true);
    el.remove();
  });

  it('航線面板:無情境航線說明;有航線時顯示航段/DTG/XTD 並可手動切換', async () => {
    const store = makeStore();
    store.onState(state());
    const el = await mount<El>('ecdis-route-panel', { store });
    expect(text(el)).toContain('無航線可監控');
    store.chart = { ...store.chart, route: { name: 'R', waypoints: [{ name: 'A', x: -1000, y: 0, checkpoint: false, report: false }, { name: 'B', x: 1000, y: 0, checkpoint: true, report: false, xtdLimit_m: 100 }, { name: 'C', x: 1000, y: 2000, checkpoint: false, report: false, xtdLimit_m: 100 }] } };
    store.routeMonitor = new (await import('../src/lib/ecdis/route-monitor.js')).RouteMonitor(store.chart.route!);
    store.onState(state({ t: 101, pos: { lat: 23.8, lon: 120.05, x: 0, y: -150 } }));
    await el.updateComplete;
    const t = text(el);
    expect(t).toContain('1 / 2');
    expect(t).toContain('B ✓');
    expect(t).toContain('150 m R'); // 東行航段,南側為右
    expect(t).toContain('090.0°');
    expect(t).toContain('左 90°'); // 下一轉向 090 → 000
    expect(store.alarms.get('xtd')).toBeDefined();
    const cBtn = [...el.shadowRoot!.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'C')!;
    cBtn.click();
    expect(store.routeStatus?.legIndex).toBe(1);
    el.remove();
  });

  it('目標表:危險目標標示、點選顯示 AIS 資料;警報面板確認', async () => {
    const store = makeStore();
    store.onState(state({ targets: [target('T1', 0.2, 5), target('T2', 1.5, 20)] }));
    const el = await mount<El>('ecdis-target-panel', { store });
    const rows = el.shadowRoot!.querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.className).toContain('danger');
    expect(text(el)).toContain('危險 1');
    (rows[0] as HTMLElement).click();
    await el.updateComplete;
    expect(store.selectedTargetId).toBe('T1');
    expect(text(el)).toContain('BZZZ');
    expect(text(el)).toContain('MAILIAO');
    expect(text(el)).toContain('吃水 9.5 m');
    const alarms = await mount<El>('ecdis-alarm-panel', { store });
    expect(text(alarms)).toContain('CPA/TCPA');
    expect(text(alarms)).toContain('未確認 1');
    const ack = [...alarms.shadowRoot!.querySelectorAll('button')].find((b) => b.textContent?.trim() === '確認')!;
    ack.click();
    await alarms.updateComplete;
    expect(store.alarms.get('cpa:T1')?.acknowledged).toBe(true);
    expect(text(alarms)).toContain('已確認');
    el.remove();
    alarms.remove();
  });

  it('海圖畫布:view 幾何與 EBL 工具點選設定 EBL/VRM;平移工具點空白取消選取', async () => {
    const store = makeStore();
    store.onState(state());
    const chart = await mount<EcdisChart>('ecdis-chart', { store, theme: 'day' });
    const v = chart.view(800, 600);
    expect(v).toMatchObject({ width: 800, height: 600, centreX: 0, centreY: 0, rangeNm: 3, rotationDeg: 0 });
    store.setSettings({ tool: 'ebl1' });
    // 以 800×600 的幾何點正上方 100 px(1 nm、000°)
    chart.view = () => v;
    chart.clickAt(400, 200);
    expect(store.ebl[0].on).toBe(true);
    expect(store.ebl[0].bearing_deg).toBeCloseTo(0, 6);
    expect(store.vrm[0].range_nm).toBeCloseTo(1, 6);
    store.setSettings({ tool: 'pan' });
    store.selectTarget('X');
    chart.clickAt(10, 10);
    expect(store.selectedTargetId).toBeNull();
    chart.remove();
  });

  it('整頁示範模式:可渲染、顯示示範模式與量程、不送指令', async () => {
    history.replaceState(null, '', '/?demo=1&range=6&theme=dusk');
    const app = await mount<El & { store: EcdisStore; options: { demo: boolean; rangeNm: number | null } }>('brm-ecdis-app');
    await flush();
    await app.updateComplete;
    expect(app.options.demo).toBe(true);
    expect(app.store.settings.rangeNm).toBe(6);
    expect(text(app)).toContain('示範模式');
    expect(text(app)).toContain('ECDIS');
    expect(app.shadowRoot!.querySelector('ecdis-chart')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('ecdis-route-panel')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('ecdis-alarm-panel')).not.toBeNull();
    expect(app.store.state).not.toBeNull();
    expect(document.documentElement.dataset['theme']).toBe('dusk');
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'n' }));
    expect(app.store.settings.orientation).toBe('north-up');
    app.remove();
    history.replaceState(null, '', '/');
  });
});
