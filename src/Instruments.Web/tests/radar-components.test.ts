// 雷達頁元件煙霧測試(happy-dom):示範模式整頁可渲染、ARPA 表顯示假追蹤目標與排序、點選目標顯示資料、
// 控制面板送出符合契約的指令、EBL/VRM 讀數、PPI 點選工具(擷取、EBL、VRM、偏心)。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { DemoRadarSource } from '../src/radar/demo-radar.js';
import { RadarStore } from '../src/radar/radar-store.js';
import type { RadarCommand } from '../src/radar/protocol.js';
import { polarToScreen } from '../src/radar/geometry.js';

const flush = () => new Promise<void>((r) => setTimeout(r, 0));

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

function demoStore(): { store: RadarStore; src: DemoRadarSource; sent: RadarCommand[] } {
  const src = new DemoRadarSource({ autoTick: false });
  const sent: RadarCommand[] = [];
  const orig = src.send.bind(src);
  src.send = (c: RadarCommand) => {
    sent.push(c);
    return orig(c);
  };
  const store = new RadarStore();
  store.attach(src);
  src.start();
  src.tick(1.01);
  return { store, src, sent };
}

beforeAll(async () => {
  await import('../src/radar/radar-controls.js');
  await import('../src/radar/radar-arpa-panels.js');
  await import('../src/radar/radar-ppi.js');
});

describe('雷達面板', () => {
  it('ARPA 表:2 個假目標、危險目標標示、點選列選取、確認警示', async () => {
    const { store } = demoStore();
    await flush();
    const el = await mount<HTMLElement & { updateComplete: Promise<unknown>; sortKey: string }>('radar-arpa-table', { store });
    const rows = el.shadowRoot!.querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.className).toContain('danger');
    expect(text(el)).toContain('危險');
    (rows[1] as HTMLElement).click();
    expect(store.selectedId).toBe(2);
    const ackBtn = [...el.shadowRoot!.querySelectorAll('button')].find((b) => b.textContent?.includes('確認警示'))!;
    expect(ackBtn.className).toContain('alarm');
    ackBtn.click();
    expect(store.pendingAlarms()).toHaveLength(0);
    el.remove();
  });

  it('目標資料:顯示選取目標的 CPA/TCPA/BCR,取消按鈕送 cancel', async () => {
    const { store, sent } = demoStore();
    await flush();
    store.select(1);
    const el = await mount('radar-target-data', { store });
    const t = text(el);
    expect(t).toContain('TARGET 01');
    expect(t).toContain('CPA');
    expect(t).toMatch(/BCR/);
    [...el.shadowRoot!.querySelectorAll('button')].find((b) => b.textContent?.includes('取消此目標'))!.click();
    expect(sent.at(-1)).toEqual({ cmd: 'cancel', id: 1 });
    el.remove();
  });

  it('控制面板:量程 +/−、待機/發射、AUTO 調諧送出指令', async () => {
    const { store, sent } = demoStore();
    const el = await mount('radar-control-panel', { store });
    expect(text(el)).toContain('6 NM');
    (el.shadowRoot!.querySelector('button[aria-label="量程加大"]') as HTMLButtonElement).click();
    expect(sent.at(-1)).toEqual({ cmd: 'range', value: 12 });
    expect(store.settings.displayRangeNm).toBe(12);
    const btn = (label: string) => [...el.shadowRoot!.querySelectorAll('button')].find((b) => b.textContent?.includes(label))!;
    btn('STBY').click();
    expect(sent.at(-1)).toEqual({ cmd: 'standby' });
    btn('TX').click();
    expect(sent.at(-1)).toEqual({ cmd: 'transmit' });
    btn('AUTO').click();
    expect(sent.at(-1)).toEqual({ cmd: 'tune', value: 50 });
    const gain = el.shadowRoot!.querySelector('input[aria-label="增益"]') as HTMLInputElement;
    gain.value = '40';
    gain.dispatchEvent(new Event('change'));
    expect(sent.at(-1)).toEqual({ cmd: 'gain', value: 40 });
    el.remove();
  });

  it('EBL/VRM 面板:輸入方位與距離後顯示讀數', async () => {
    const { store } = demoStore();
    const el = await mount<HTMLElement & { updateComplete: Promise<unknown> }>('radar-tools-panel', { store });
    const ebl = el.shadowRoot!.querySelector('input[aria-label="EBL1 方位"]') as HTMLInputElement;
    ebl.value = '123.4';
    ebl.dispatchEvent(new Event('change'));
    const vrm = el.shadowRoot!.querySelector('input[aria-label="VRM1 距離"]') as HTMLInputElement;
    vrm.value = '1.5';
    vrm.dispatchEvent(new Event('change'));
    await flush();
    await el.updateComplete;
    expect(store.settings.ebl[0]).toMatchObject({ on: true, bearing: 123.4, ref: 'T' });
    expect(store.settings.vrm[0]).toEqual({ on: true, rangeNm: 1.5 });
    expect(text(el)).toContain('123.4° T');
    expect(text(el)).toContain('1.50 NM');
    el.remove();
  });

  it('試操船面板:啟用送 trialManoeuvre、預測 CPA 顯示', async () => {
    const { store, sent } = demoStore();
    await flush();
    const el = await mount<HTMLElement & { updateComplete: Promise<unknown> }>('radar-trial-panel', { store });
    [...el.shadowRoot!.querySelectorAll('button')].find((b) => b.textContent?.includes('啟用試操船'))!.click();
    expect(sent.at(-1)).toMatchObject({ cmd: 'trialManoeuvre', course: 44, speed: 10, delay: 1 });
    await flush();
    await flush();
    await el.updateComplete;
    expect(text(el)).toContain('預測 CPA');
    el.remove();
  });
});

describe('PPI 點選工具', () => {
  it('擷取工具送真方位/距離;EBL/VRM 工具設定讀數;偏心工具加大影像量程', async () => {
    const { store, sent } = demoStore();
    await flush();
    const ppi = (await mount('radar-ppi', { store })) as HTMLElement & {
      clickAt(x: number, y: number): void;
      computeView(): import('../src/radar/geometry.js').PpiView;
    };
    const v = ppi.computeView();
    const p = polarToScreen(v, 320, 2);
    store.setSettings({ tool: 'acquire' });
    ppi.clickAt(p.x, p.y);
    const acq = sent.at(-1) as { cmd: string; bearing: number; range: number };
    expect(acq.cmd).toBe('acquire');
    expect(acq.bearing).toBeCloseTo(320, 6);
    expect(acq.range).toBeCloseTo(2, 6);
    store.setSettings({ tool: 'ebl1' });
    const q = polarToScreen(v, 100, 3);
    ppi.clickAt(q.x, q.y);
    expect(store.settings.ebl[0]).toMatchObject({ on: true, bearing: 100 });
    store.setSettings({ tool: 'vrm2' });
    ppi.clickAt(q.x, q.y);
    expect(store.settings.vrm[1]).toEqual({ on: true, rangeNm: 3 });
    store.setSettings({ tool: 'offcentre' });
    ppi.clickAt(v.cx, v.cy + v.radiusPx * 0.5);
    expect(store.settings.offcentre.y).toBeCloseTo(0.5, 6);
    expect(store.settings.tool).toBe('select');
    expect(sent.at(-1)).toEqual({ cmd: 'range', value: 12 });
    ppi.remove();
  });
});

describe('整頁示範模式', () => {
  it('?demo=1 載入示範資料並渲染所有面板', async () => {
    history.replaceState(null, '', '/radar.html?demo=1&radar=s');
    await import('../src/radar/radar-app.js');
    const app = await mount<HTMLElement & { store: RadarStore; options: { demo: boolean; radar: string } }>('brm-radar-app');
    expect(app.options.demo).toBe(true);
    expect(app.options.radar).toBe('s');
    await flush();
    const t = text(app);
    expect(t).toContain('雷達 S 頻');
    expect(t).toContain('示範模式');
    for (const tag of ['radar-ppi', 'radar-control-panel', 'radar-display-panel', 'radar-tools-panel', 'radar-arpa-table', 'radar-target-data', 'radar-trial-panel', 'radar-event-log']) {
      expect(app.shadowRoot!.querySelector(tag)).not.toBeNull();
    }
    expect(app.store.status?.band).toBe('S');
    app.remove();
  });
});
