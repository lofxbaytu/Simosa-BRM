// 拖船/錨/繫纜面板與操船台讀數(happy-dom):送出的指令內容、面板狀態(停用規則、斷纜提示、張力著色)、
// 精簡版(簡化教官模式)、操船台艏/艉橫向速度、教官站只在 state 有欄位時顯示面板、拒絕原因中文顯示在教官指令紀錄。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import type { AnchorState, BerthState, TugState } from '../src/types/external.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import { defaultState } from '../src/lib/state-parser.js';
import { getShipConfig } from '../src/lib/ship-config.js';
import { berthGeometry } from '../src/lib/external-forces.js';
import { anchorActionBlocked } from '../src/components/instructor/anchor-panel.js';
import { mooringActionBlocked } from '../src/components/instructor/mooring-panel.js';
import { showBerthing } from '../src/components/external-readout.js';
import type { CommandLogEntry } from '../src/lib/session-record.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}, attrs: Record<string, string> = {}): Promise<T & Updatable> {
  const el = document.createElement(tag) as T & Updatable;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function captureCommands(): SimCommand[] {
  const cmds: SimCommand[] = [];
  document.body.addEventListener(COMMAND_EVENT, (ev) => cmds.push((ev as CustomEvent<SimCommand>).detail));
  return cmds;
}

function q<T extends Element>(el: HTMLElement, sel: string): T {
  const found = el.shadowRoot!.querySelector<T>(sel);
  if (!found) throw new Error(`找不到 ${sel}`);
  return found;
}

function text(el: HTMLElement): string {
  return el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
}

const TUGS: TugState[] = [
  { id: 'TG1', name: '麥寮1號', mode: 'standby', status: 'standby', force_t: 0, direction_deg: 90, directionOrder_deg: 90, power_pct: 0, efficiency: 1, bollardPull_t: 40, lineParted: false },
  { id: 'TG2', mode: 'pull', status: 'parted', force_t: 0, direction_deg: 180, power_pct: 80, efficiency: 0.5, bollardPull_t: 40, lineParted: true },
];

const ANCHORS: AnchorState[] = [
  { side: 'port', status: 'stowed', length_m: 0, length_shackles: 0, maxLength_m: 247.5, tension_t: 0, onBottom_m: 0, holding_t: 0, brake: true, fullyOut: false },
  { side: 'starboard', status: 'dragging', length_m: 110, length_shackles: 4, maxLength_m: 247.5, tension_t: 22, horizontalTension_t: 21.4, onBottom_m: 20, holding_t: 6.3, brake: true, fullyOut: false },
];

const BERTH: BerthState = {
  id: 'MLO-OIL-3',
  name: '麥寮油碼頭 3 號泊位(示意)',
  quayContact: true,
  contacts: [{ fenderId: 'F06', deflection_m: 0.6, deflection_pct: 60, reaction_kN: 980, overload: false }],
  lines: [
    { id: 'HEAD', status: 'fast', tension_t: 42, mbl_t: 50, bollardId: 'B6', length_m: 45 },
    { id: 'STERN', status: 'stowed', tension_t: 0, mbl_t: 50 },
    { id: 'SPRING', status: 'parted', tension_t: 0, mbl_t: 50 },
  ],
};

const GEO = berthGeometry(
  {
    id: 'MLO-OIL-3',
    quay: [
      { x: 0, y: 0 },
      { x: 300, y: 0 },
    ],
    waterSide: 'right',
    fenders: [{ id: 'F06', type: 'c', chainage_m: 120 }],
    fenderTypes: { c: { height_m: 1, width_m: 2.5, ratedDeflection_pct: 72 } },
    bollards: [
      { id: 'B2', chainage_m: 45, swl_t: 50 },
      { id: 'B6', chainage_m: 215, swl_t: 50 },
    ],
  },
  null,
);

beforeAll(async () => {
  await import('../src/components/instructor/tug-panel.js');
  await import('../src/components/instructor/anchor-panel.js');
  await import('../src/components/instructor/mooring-panel.js');
  await import('../src/components/external-readout.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('<brm-tug-panel>', () => {
  it('模式鈕:推/拉附上方向與出力草稿;待命/解纜只送模式;方向與出力單獨送', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-tug-panel', { tugs: TUGS });
    const card = q<HTMLElement>(el, '[data-tug="TG1"]');
    const dir = card.querySelector<HTMLInputElement>('input.dir')!;
    dir.value = '270';
    dir.dispatchEvent(new Event('input'));
    dir.dispatchEvent(new Event('change'));
    const power = card.querySelector<HTMLInputElement>('input.power')!;
    power.value = '50';
    power.dispatchEvent(new Event('input'));
    power.dispatchEvent(new Event('change'));
    await el.updateComplete;
    card.querySelector<HTMLButtonElement>('button.mode-push')!.click();
    card.querySelector<HTMLButtonElement>('button.mode-standby')!.click();
    expect(cmds).toEqual([
      { type: 'tugControl', args: { id: 'TG1', direction_deg: 270 } },
      { type: 'tugControl', args: { id: 'TG1', power_pct: 50 } },
      { type: 'tugControl', args: { id: 'TG1', mode: 'push', direction_deg: 270, power_pct: 50 } },
      { type: 'tugControl', args: { id: 'TG1', mode: 'standby' } },
    ]);
  });

  it('轉盤鍵盤 ±5° 後 Enter 送出;斷纜提示與斷纜故障鈕', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-tug-panel', { tugs: TUGS });
    const dial = q<SVGElement>(el, '[data-tug="TG1"] svg.dial');
    dial.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    dial.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    await el.updateComplete;
    expect(dial.getAttribute('aria-valuenow')).toBe('100');
    dial.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(cmds.at(-1)).toEqual({ type: 'tugControl', args: { id: 'TG1', direction_deg: 100 } });
    const tg2 = q<HTMLElement>(el, '[data-tug="TG2"]');
    expect(tg2.querySelector('.status')!.textContent).toBe('拖纜斷裂');
    expect(tg2.querySelector('.parted-warning')!.textContent).toContain('拖纜斷裂');
    expect(tg2.querySelector<HTMLButtonElement>('button.mode-pull')!.title).toContain('tugLineParted');
    const breakBtn = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('button.line-break')).find((b) => b.textContent!.includes('TG1'))!;
    breakBtn.click();
    expect(cmds.at(-1)).toEqual({ type: 'injectFault', args: { name: 'tugLineBreak', args: { id: 'TG1' } } });
    expect(text(el)).toContain('0.0 / 40 t');
  });

  it('情境 modes 只有 push 時拉鈕停用', async () => {
    const el = await mount('brm-tug-panel', { tugs: TUGS, defs: [{ id: 'TG1', modes: ['push'] }] });
    expect(q<HTMLButtonElement>(el, '[data-tug="TG1"] button.mode-pull').disabled).toBe(true);
    expect(q<HTMLButtonElement>(el, '[data-tug="TG1"] button.mode-push').disabled).toBe(false);
    expect(q<HTMLButtonElement>(el, '[data-tug="TG2"] button.mode-pull').disabled).toBe(false);
  });

  it('精簡版(簡化教官模式):方向與出力快速鍵,無轉盤', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-tug-panel', { tugs: TUGS }, { compact: '' });
    expect(el.shadowRoot!.querySelector('svg.dial')).toBeNull();
    q<HTMLButtonElement>(el, '[data-tug="TG1"] button.dir-270').click();
    q<HTMLButtonElement>(el, '[data-tug="TG1"] button.pw-75').click();
    q<HTMLButtonElement>(el, '[data-tug="TG1"] button.mode-pull').click();
    expect(cmds).toEqual([
      { type: 'tugControl', args: { id: 'TG1', direction_deg: 270 } },
      { type: 'tugControl', args: { id: 'TG1', power_pct: 75 } },
      { type: 'tugControl', args: { id: 'TG1', mode: 'pull', direction_deg: 270, power_pct: 75 } },
    ]);
  });
});

describe('<brm-anchor-panel>', () => {
  it('讀數、走錨著色與動作停用規則', async () => {
    const el = await mount('brm-anchor-panel', { anchors: ANCHORS });
    const stbd = q<HTMLElement>(el, '[data-side="starboard"]');
    expect(stbd.classList.contains('dragging')).toBe(true);
    expect(stbd.querySelector('.status')!.textContent).toBe('走錨');
    expect(stbd.querySelector('.length')!.textContent).toContain('4.0 節(110 m) / 9 節');
    expect(stbd.querySelector('.tension')!.textContent).toBe('22.0 t(水平 21.4)');
    expect(stbd.querySelector('.bar')!.classList.contains('alarm')).toBe(true);
    expect(stbd.querySelector<HTMLButtonElement>('.act-letGo')!.disabled).toBe(true);
    const port = q<HTMLElement>(el, '[data-side="port"]');
    expect(port.querySelector<HTMLButtonElement>('.act-letGo')!.disabled).toBe(false);
    expect(port.querySelector<HTMLButtonElement>('.act-veer')!.disabled).toBe(true);
    expect(port.querySelector<HTMLButtonElement>('.act-heave')!.title).toBe('錨仍收存');
    expect(anchorActionBlocked({ ...ANCHORS[1]!, fullyOut: true }, 'veer')).toBe('錨鏈已全部送出');
    expect(anchorActionBlocked({ ...ANCHORS[1]!, status: 'aweigh', onBottom_m: 0 }, 'letGo')).toBeNull();
  });

  it('目標鏈長(節)隨 letGo/heave 送出;空白不帶目標', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-anchor-panel', { anchors: ANCHORS });
    const port = q<HTMLElement>(el, '[data-side="port"]');
    port.querySelector<HTMLButtonElement>('.act-letGo')!.click();
    const input = port.querySelector<HTMLInputElement>('input.target-sh')!;
    input.value = '4';
    input.dispatchEvent(new Event('input'));
    await el.updateComplete;
    port.querySelector<HTMLButtonElement>('.act-letGo')!.click();
    q<HTMLButtonElement>(el, '[data-side="starboard"] .act-heave').click();
    q<HTMLButtonElement>(el, '[data-side="starboard"] .act-brake').click();
    expect(cmds).toEqual([
      { type: 'anchor', args: { side: 'port', action: 'letGo' } },
      { type: 'anchor', args: { side: 'port', action: 'letGo', targetLength_shackles: 4 } },
      { type: 'anchor', args: { side: 'starboard', action: 'heave' } },
      { type: 'anchor', args: { side: 'starboard', action: 'brake' } },
    ]);
  });
});

describe('<brm-mooring-panel>', () => {
  it('碼頭硬接觸、碰墊壓縮著色、纜繩張力相對 MBL 著色', async () => {
    const el = await mount('brm-mooring-panel', { berth: BERTH, geometry: GEO, lineDefs: [{ id: 'STERN', bollard: 'B2', winchPull_t: 10 }] });
    expect(text(el)).toContain('船體觸碰碼頭');
    expect(q<HTMLElement>(el, '[data-fender="F06"] .bar').classList.contains('caution')).toBe(true);
    expect(q<HTMLElement>(el, '[data-line="HEAD"] .bar').classList.contains('alarm')).toBe(true);
    expect(text(el)).toContain('42.0 / 50 t · B6 · 45 m');
    expect(q<HTMLButtonElement>(el, '[data-line="STERN"] .act-heave').disabled).toBe(true);
    expect(q<HTMLButtonElement>(el, '[data-line="SPRING"] .act-makeFast').disabled).toBe(true);
    expect(mooringActionBlocked(BERTH.lines[0]!, 'heave')).toBeNull();
    expect(q<HTMLSelectElement>(el, '[data-line="STERN"] select.bollard').value).toBe('B2');
  });

  it('帶纜(選繫船柱)、絞纜(目標張力)、鬆纜(預設 2 m)、解纜', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-mooring-panel', { berth: BERTH, geometry: GEO, lineDefs: [{ id: 'STERN', bollard: 'B2' }] });
    const stern = q<HTMLElement>(el, '[data-line="STERN"]');
    const sel = stern.querySelector<HTMLSelectElement>('select.bollard')!;
    sel.value = 'B6';
    sel.dispatchEvent(new Event('change'));
    stern.querySelector<HTMLButtonElement>('.act-makeFast')!.click();
    const head = q<HTMLElement>(el, '[data-line="HEAD"]');
    const t = head.querySelector<HTMLInputElement>('input.tension')!;
    t.value = '8';
    t.dispatchEvent(new Event('input'));
    head.querySelector<HTMLButtonElement>('.act-heave')!.click();
    head.querySelector<HTMLButtonElement>('.act-slack')!.click();
    head.querySelector<HTMLButtonElement>('.act-letGo')!.click();
    expect(cmds).toEqual([
      { type: 'mooring', args: { lineId: 'STERN', action: 'makeFast', bollard: 'B6' } },
      { type: 'mooring', args: { lineId: 'HEAD', action: 'heave', tension_t: 8 } },
      { type: 'mooring', args: { lineId: 'HEAD', action: 'slack', length_m: 2 } },
      { type: 'mooring', args: { lineId: 'HEAD', action: 'letGo' } },
    ]);
  });
});

describe('<brm-external-readout>(操船台)', () => {
  const cfg = getShipConfig('FSB1');

  it('舊狀態(無外力欄位)不顯示', async () => {
    const el = await mount('brm-external-readout', { state: defaultState('FSB1'), config: cfg });
    expect(el.shadowRoot!.querySelector('.box')).toBeNull();
  });

  it('錨鏈長/張力/狀態、拖船力與方向;兩錨收存時一行', async () => {
    const s = { ...defaultState('FSB1'), anchors: ANCHORS, tugs: TUGS };
    const el = await mount('brm-external-readout', { state: s, config: cfg });
    expect(q<HTMLElement>(el, '.anchor[data-side="starboard"]').textContent!.replace(/\s+/g, ' ')).toContain('走錨 4.0 節(110 m) 張力 22.0 t');
    expect(el.shadowRoot!.querySelector('.anchor[data-side="port"]')).toBeNull();
    expect(q<HTMLElement>(el, '.tug[data-tug="TG1"]').textContent!.replace(/\s+/g, ' ')).toContain('待命 0 t 090° 向右舷');
    expect(q<HTMLElement>(el, '.tug[data-tug="TG2"] .alarm-text').textContent).toBe('拖纜斷裂');
    const stowed = await mount('brm-external-readout', { state: { ...defaultState('FSB1'), anchors: [ANCHORS[0]!, { ...ANCHORS[0]!, side: 'starboard' }] }, config: cfg });
    expect(text(stowed)).toContain('左錨、右錨收存');
  });

  it('接近泊位時顯示艏/艉橫向速度(由 v、r、LPP 推算並註明)與碼頭接近速度', async () => {
    // 碼頭沿 x 軸、水域在 −y;船在 y = −60(距碼頭 60 m),航向 090,向左舷(北,朝碼頭)橫移 0.1 m/s
    const s = { ...defaultState('FSB1'), pos: { lat: 0, lon: 0, x: 150, y: -60 }, heading: 90, v: -0.1, r: 0, sog: 0.2, berth: { ...BERTH, quayContact: false } };
    expect(showBerthing(s, GEO, cfg.lpp_m)).toBe(true);
    const el = await mount('brm-external-readout', { state: s, config: cfg, geometry: GEO });
    expect(q<HTMLElement>(el, '.bow').textContent).toBe('左 0.10');
    expect(q<HTMLElement>(el, '.stern').textContent).toBe('左 0.10');
    expect(q<HTMLElement>(el, '.approach').textContent!.replace(/\s+/g, ' ')).toContain('60 m 艏接近 0.10 艉接近 0.10 m/s');
    expect(text(el)).toContain('由 v、r 與 LPP 推算');
    // 遠離泊位(> 500 m)不顯示;無幾何時以 SOG < 4 kn 判斷
    expect(showBerthing({ ...s, pos: { lat: 0, lon: 0, x: 150, y: -900 } }, GEO, cfg.lpp_m)).toBe(false);
    expect(showBerthing({ ...s, sog: 8 }, null, cfg.lpp_m)).toBe(false);
    expect(showBerthing({ ...s, sog: 2 }, null, cfg.lpp_m)).toBe(true);
  });
});

describe('教官站整合', () => {
  type App = HTMLElement & { updateComplete: Promise<unknown> };

  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });

  it('示範模式顯示拖船/錨/繫纜面板;state 沒有欄位時不顯示;拒絕原因以中文標在教官指令紀錄', async () => {
    history.replaceState(null, '', '/?demo=1');
    const app = document.createElement('brm-instructor-app') as App;
    document.body.appendChild(app);
    await app.updateComplete;
    await new Promise((r) => setTimeout(r, 80));
    await app.updateComplete;
    const root = app.shadowRoot!;
    expect(root.querySelector('brm-tug-panel')).not.toBeNull();
    expect(root.querySelector('brm-anchor-panel')).not.toBeNull();
    expect(root.querySelector('brm-mooring-panel')).not.toBeNull();
    // 未定義的拖船:示範來源同步回 commandRejected → 教官指令紀錄標為核心拒絕(中文原因)
    root.querySelector('brm-tug-panel')!.dispatchEvent(new CustomEvent<SimCommand>(COMMAND_EVENT, { detail: { type: 'tugControl', args: { id: 'TGX', mode: 'push' } }, bubbles: true, composed: true }));
    const log = (app as unknown as { commandLog: CommandLogEntry[] }).commandLog;
    const entry = log.find((e) => e.command.type === 'tugControl');
    expect(entry?.ack).toMatchObject({ ok: false, detail: '未定義的拖船' });
    // 停止示範來源,改餵沒有外力欄位的舊狀態
    const priv = app as unknown as { stopSource(): void; onState(s: unknown, i: string[]): void };
    priv.stopSource();
    priv.onState({ ...defaultState('FSB1'), tick: 999999 }, []);
    await app.updateComplete;
    expect(root.querySelector('brm-tug-panel')).toBeNull();
    expect(root.querySelector('brm-anchor-panel')).toBeNull();
    expect(root.querySelector('brm-mooring-panel')).toBeNull();
  });
});
