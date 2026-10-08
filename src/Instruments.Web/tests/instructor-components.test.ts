// 教官站元件煙霧測試(happy-dom):情境面板、執行控制、環境、故障、自船覆寫、BRM 標記鈕送出的事件與指令,
// 重播檢視可載入 JSON Lines 並依時間軸取狀態。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import { defaultState } from '../src/lib/state-parser.js';
import { parseScenario } from '../src/lib/scenario.js';
import type { ScenarioSummary } from '../src/lib/scenario.js';
import type { BrmMarkDetail } from '../src/components/instructor/brm-marker-bar.js';
import type { ReplayView } from '../src/components/instructor/replay-view.js';
import type { RunControlPanel, SnapshotEntry } from '../src/components/instructor/run-control-panel.js';
import type { FaultPanel } from '../src/components/instructor/fault-panel.js';
import type { BrmMarkerBar } from '../src/components/instructor/brm-marker-bar.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}, attrs: Record<string, string> = {}): Promise<T & Updatable> {
  const el = document.createElement(tag) as T & Updatable;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function shadowText(el: HTMLElement): string {
  return el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
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

function setInput(el: HTMLElement, sel: string, value: string): void {
  const input = q<HTMLInputElement>(el, sel);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

const E01: ScenarioSummary = parseScenario(
  `id: E01_baseline
name: 基線
ship: { id: FSB1, loading: ballast }
initial: { position: { lat: 23.8, lon: 120.05 }, heading: 0, speed: 7.8, telegraph: HAH }
environment: { wind: { trueSpeed: 15, trueDir: 45 }, current: { set: 200, drift: 0.5 }, waterDepth: 30 }
`,
  'data/scenarios/E01_baseline.yaml',
);

beforeAll(async () => {
  await import('../src/components/instructor/scenario-panel.js');
  await import('../src/components/instructor/run-control-panel.js');
  await import('../src/components/instructor/environment-panel.js');
  await import('../src/components/instructor/fault-panel.js');
  await import('../src/components/instructor/ownship-panel.js');
  await import('../src/components/instructor/brm-marker-bar.js');
  await import('../src/components/instructor/operation-log-view.js');
  await import('../src/components/instructor/command-log-view.js');
  await import('../src/components/instructor/debrief-summary.js');
  await import('../src/components/instructor/replay-view.js');
});

describe('情境面板', () => {
  it('列出情境摘要,按載入發出 scenario-load', async () => {
    const el = await mount('brm-scenario-panel', { scenarios: [E01] });
    const text = shadowText(el);
    expect(text).toContain('E01_baseline');
    expect(text).toContain('FSB1 · 壓載 · 風 15 kn/045°');
    const loads: ScenarioSummary[] = [];
    el.addEventListener('scenario-load', (ev) => loads.push((ev as CustomEvent<ScenarioSummary>).detail));
    q<HTMLButtonElement>(el, 'button.load').click();
    expect(loads[0]?.id).toBe('E01_baseline');
  });
});

describe('執行控制', () => {
  it('凍結/恢復、時間倍率、快照/還原指令', async () => {
    const cmds = captureCommands();
    const el = await mount<RunControlPanel>('brm-run-control', { frozen: false, tick: 12000, simTime: 240 });
    q<HTMLButtonElement>(el, 'button.freeze').click();
    expect(cmds.at(-1)).toEqual({ type: 'freeze' });
    const scales = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.seg button'));
    scales.find((b) => b.textContent?.trim() === '×5')!.click();
    expect(cmds.at(-1)).toEqual({ type: 'timeScale', value: 5 });
    const snaps: SnapshotEntry[] = [];
    el.addEventListener('snapshot-taken', (ev) => snaps.push((ev as CustomEvent<SnapshotEntry>).detail));
    q<HTMLButtonElement>(el, 'button.snapshot').click();
    expect(cmds.at(-1)).toEqual({ type: 'snapshot', value: 'snap-12000', args: { tick: 12000, t: 240 } });
    expect(snaps[0]).toMatchObject({ name: 'snap-12000', tick: 12000, t: 240 });
    el.snapshots = snaps;
    await el.updateComplete;
    const restore = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.snap-list button')).find((b) => b.textContent?.includes('還原'))!;
    restore.click();
    expect(cmds.at(-1)).toEqual({ type: 'restore', value: 'snap-12000', args: { tick: 12000 } });
    // 重設需按兩次
    const before = cmds.length;
    q<HTMLButtonElement>(el, 'button.reset').click();
    expect(cmds.length).toBe(before);
    q<HTMLButtonElement>(el, 'button.reset').click();
    expect(cmds.at(-1)).toEqual({ type: 'reset' });
  });
});

describe('環境面板', () => {
  it('帶入目前狀態並分組送 setEnvironment;能見度標示核心未實作', async () => {
    const cmds = captureCommands();
    const st = { ...defaultState('FSB1'), wind: { trueSpeed: 15, trueDir: 45 }, current: { set: 200, drift: 0.5 }, waterDepth: 30 };
    const el = await mount('brm-environment-panel', { state: st });
    await el.updateComplete;
    expect(q<HTMLInputElement>(el, 'input.wind-speed').value).toBe('15');
    setInput(el, 'input.wind-speed', '25');
    setInput(el, 'input.wind-dir', '90');
    q<HTMLButtonElement>(el, 'button.apply-wind').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { wind: { trueSpeed: 25, trueDir: 90, gustiness: 0 } } });
    setInput(el, 'input.depth', '12');
    q<HTMLButtonElement>(el, 'button.apply-depth').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { waterDepth: 12 } });
    setInput(el, 'input.visibility', '0.5');
    q<HTMLButtonElement>(el, 'button.apply-visibility').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { visibility_nm: 0.5 } });
    expect(shadowText(el)).toContain('核心未實作');
  });
});

describe('故障面板', () => {
  it('注入/清除故障並列出現行故障', async () => {
    const cmds = captureCommands();
    const el = await mount<FaultPanel>('brm-fault-panel', { active: [] });
    q<HTMLButtonElement>(el, 'button[data-fault="steeringGear"]').click();
    expect(cmds.at(-1)).toEqual({ type: 'injectFault', value: 'steeringGear' });
    el.active = ['steeringGear'];
    await el.updateComplete;
    expect(shadowText(el)).toContain('現行 1');
    q<HTMLButtonElement>(el, 'button[data-fault="steeringGear"]').click();
    expect(cmds.at(-1)).toEqual({ type: 'clearFault', value: 'steeringGear' });
    el.active = ['steeringGear', 'blackout'];
    await el.updateComplete;
    const before = cmds.length;
    q<HTMLButtonElement>(el, 'button.clear-all').click();
    expect(cmds.slice(before)).toEqual([
      { type: 'clearFault', value: 'steeringGear' },
      { type: 'clearFault', value: 'blackout' },
    ]);
    expect(shadowText(el)).toContain('全船失電');
  });
});

describe('自船覆寫', () => {
  it('以 reset 附 args 送出,需兩次確認', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-ownship-panel', { state: { ...defaultState('FSB2'), heading: 90, stw: 5 } });
    await el.updateComplete;
    setInput(el, 'input.pos-x', '150');
    setInput(el, 'input.tugs', '2');
    const before = cmds.length;
    q<HTMLButtonElement>(el, 'button.apply').click();
    expect(cmds.length).toBe(before);
    q<HTMLButtonElement>(el, 'button.apply').click();
    expect(cmds.at(-1)).toEqual({ type: 'reset', args: { x: 150, y: 0, heading: 90, speed: 5, loading: 'full', tugs: 2 } });
  });
});

describe('BRM 標記鈕', () => {
  it('五類按鈕發出 brm-mark(含備註與正向/待改進),匯出鈕發出 brm-export', async () => {
    const el = await mount<BrmMarkerBar>('brm-marker-bar', { markers: [] });
    const marks: BrmMarkDetail[] = [];
    el.addEventListener('brm-mark', (ev) => marks.push((ev as CustomEvent<BrmMarkDetail>).detail));
    const buttons = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('button.mark'));
    expect(buttons.map((b) => b.dataset['category'])).toEqual(['leadership', 'situationalAwareness', 'communication', 'teamwork', 'decisionMaking']);
    setInput(el, 'input.note-input', '舵令未複誦');
    q<HTMLButtonElement>(el, 'button.delta').click();
    buttons[2]!.click();
    expect(marks[0]).toEqual({ category: 'communication', note: '舵令未複誦', polarity: 'delta' });
    await el.updateComplete;
    expect(q<HTMLInputElement>(el, 'input.note-input').value).toBe('');
    el.markers = [{ id: 1, category: 'communication', t: 65, wall: new Date(0).toISOString(), note: '舵令未複誦', polarity: 'delta' }];
    await el.updateComplete;
    expect(shadowText(el)).toContain('01:05 溝通');
    const exports: string[] = [];
    el.addEventListener('brm-export', (ev) => exports.push((ev as CustomEvent<string>).detail));
    q<HTMLButtonElement>(el, 'button.export-csv').click();
    expect(exports).toEqual(['csv']);
  });
});

describe('紀錄清單與講評摘要', () => {
  it('操作紀錄、指令紀錄與講評表格可渲染', async () => {
    const ops = await mount('brm-operation-log', { events: [{ seq: 1, t: 61, wall: new Date(0).toISOString(), kind: 'rudder', label: '舵令 S 20°', value: 20 }] });
    expect(shadowText(ops)).toContain('01:01 舵令 S 20°');
    const cmdLog = await mount('brm-command-log', {
      entries: [{ seq: 1, t: 2, wall: new Date(0).toISOString(), command: { type: 'loadScenario', value: 'x.yaml' }, ack: { ok: false, detail: '情境載入尚未實作' } }],
    });
    expect(shadowText(cmdLog)).toContain('核心拒絕:情境載入尚未實作');
    const sum = await mount('brm-debrief-summary', {
      summary: {
        samples: 10,
        duration_s: 600,
        minUkc_m: 0.8,
        minUkcAt_s: 100,
        maxRudder_deg: 35,
        maxRudderAt_s: 50,
        maxRot_degPerMin: 12,
        maxRotAt_s: 55,
        telegraphChanges: 3,
        rudderOrders: 7,
        hardOverCount: 1,
        distance_nm: 1.5,
        avgSpeed_kn: 9,
        maxSog_kn: 10,
        timeOverSpeedLimit_s: 120,
        timeUkcBelowAlarm_s: 5,
        timeRotOverLimit_s: 0,
        agroundEvents: 0,
        collisionEvents: 0,
        frozen_s: 0,
        thresholds: { portSpeedLimit_kn: 8, ukcAlarm_m: 1, rotLimit_degPerMin: 30, hardOver_deg: 35 },
      },
    });
    const text = shadowText(sum);
    expect(text).toContain('0.80 m');
    expect(text).toContain('02:00');
    expect(sum.shadowRoot!.querySelectorAll('tr.alarm').length).toBe(2);
  });
});

describe('重播檢視', () => {
  it('載入 JSON Lines 後可依時間軸取狀態與輸入事件', async () => {
    const el = await mount<ReplayView>('brm-replay-view');
    const line = (t: number, heading: number) => JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), heading, pos: { lat: 0, lon: 0, x: t, y: 0 } } });
    const text = [
      JSON.stringify({ kind: 'header', version: 1, shipId: 'FSB1', loading: 'ballast', seed: 1, dt: 0.02, dynamics: 'placeholder-nomoto/1', scenario: { id: 'E01_baseline' } }),
      line(0, 0),
      '{"kind":"input","tick":50,"command":{"type":"rudder","value":10}}',
      line(2, 5),
      line(4, 10),
      '{"kind":"footer","finalTick":200,"stateHash":"AB","lines":5,"inputs":1,"sha256":"CD"}',
    ].join('\n');
    const data = el.loadText(text, 'test.jsonl');
    expect(data.errors).toEqual([]);
    await el.updateComplete;
    const txt = shadowText(el);
    expect(txt).toContain('SimCore JSON Lines');
    expect(txt).toContain('E01_baseline');
    expect(txt).toContain('3 筆狀態 · 1 筆輸入');
    expect(txt).toContain('rudder 10');
    const slider = q<HTMLInputElement>(el, 'input.slider');
    slider.value = '3';
    slider.dispatchEvent(new Event('input', { bubbles: true }));
    await el.updateComplete;
    expect(shadowText(el)).toContain('00:03 / 00:04');
    const bar = el.shadowRoot!.querySelector('brm-conning-bar') as HTMLElement & { state: { heading: number } };
    expect(bar.state.heading).toBe(5);
  });
});
