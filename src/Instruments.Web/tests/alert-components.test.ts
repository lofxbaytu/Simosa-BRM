// 警報/BNWAS 元件(happy-dom):操船台中央警報面板與 BNWAS 指示、教官站警報監看與「真值 vs 指示值」、講評摘要的警報段、
// conning 條的感測器無效標示、兩頁在示範模式下的整合。聲響不在此測(happy-dom 無 Web Audio;見 alerts.test.ts 的假 AudioContext)。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import type { AlertItem, BnwasStatus } from '../src/types/alerts.js';
import type { SimEvent } from '../src/types/event.js';
import { defaultState } from '../src/lib/state-parser.js';
import { getShipConfig } from '../src/lib/ship-config.js';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
import { COMMAND_EVENT } from '../src/components/shared.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement = El>(tag: string, props: Record<string, unknown> = {}, attrs: Record<string, string> = {}): Promise<T & El> {
  const el = document.createElement(tag) as T & El;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

const text = (el: HTMLElement) => el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
const q = <T extends Element>(el: HTMLElement, sel: string) => el.shadowRoot!.querySelector(sel) as T;
const qa = <T extends Element>(el: HTMLElement, sel: string) => Array.from(el.shadowRoot!.querySelectorAll(sel)) as T[];

function captureCommands(): SimCommand[] {
  const cmds: SimCommand[] = [];
  document.body.addEventListener(COMMAND_EVENT, (ev) => cmds.push((ev as CustomEvent<SimCommand>).detail));
  return cmds;
}

function alert(over: Partial<AlertItem>): AlertItem {
  return { id: 'x', number: 1, priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', text: 'x', raisedAt_s: 0, escalated: false, count: 1, audible: true, ...over };
}

const ALERTS: AlertItem[] = [
  alert({ id: 'headingMonitor', number: 13, priority: 'warning', category: 'A', text: '航向監視', raisedAt_s: 4 }),
  alert({ id: 'bnwas', number: 14, priority: 'alarm', source: 'bnwas', text: 'BNWAS 值班警報', raisedAt_s: 540.02, escalated: true }),
  alert({ id: 'gyroFailure', number: 1, priority: 'alarm', text: '電羅經失效', raisedAt_s: 480.02, state: 'activeSilenced', silencedUntil_s: 585, audible: false }),
  alert({ id: 'mainEngine', number: 9, priority: 'alarm', source: 'engine', text: '主機故障', raisedAt_s: 100, state: 'activeAcknowledged', audible: false }),
];

beforeAll(async () => {
  await import('../src/components/alert-panel.js');
  await import('../src/components/bnwas-indicator.js');
  await import('../src/components/conning-bar.js');
  await import('../src/components/instructor/alert-monitor.js');
  await import('../src/components/instructor/sensor-truth-panel.js');
  await import('../src/components/instructor/debrief-summary.js');
  await import('../src/components/instructor/fault-panel.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('<brm-alert-panel> 中央警報面板', () => {
  it('依優先序與時間排序、狀態顯示(未確認閃爍、靜音倒數、已確認不閃)', async () => {
    const el = await mount('brm-alert-panel', { alerts: ALERTS, simTime: 570 });
    const rows = qa<HTMLElement>(el, '.alert');
    expect(rows.map((r) => r.dataset['alert'])).toEqual(['mainEngine', 'gyroFailure', 'bnwas', 'headingMonitor']);
    expect(rows.map((r) => r.dataset['state'])).toEqual(['activeAcknowledged', 'activeSilenced', 'activeUnacknowledged', 'activeUnacknowledged']);
    expect(rows[2]!.querySelector('svg.alert-icon.flash.tone-alarm polygon')).not.toBeNull(); // alarm 紅三角閃爍
    expect(rows[3]!.querySelector('svg.alert-icon.flash.tone-warning circle')).not.toBeNull(); // warning 圓形
    expect(rows[0]!.querySelector('svg.alert-icon.flash')).toBeNull(); // 已確認不閃
    expect(rows[1]!.textContent).toContain('靜音中 15 s');
    expect(rows[1]!.querySelector('path.badge')).not.toBeNull(); // 靜音圖示
    expect(rows[0]!.querySelector('button.ack')).toBeNull();
    expect(text(el)).toContain('未確認 3');
    expect(rows[2]!.textContent).toContain('已升級');
  });

  it('確認/全部確認/靜音送出指令;聲響開關只發本頁事件', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-alert-panel', { alerts: ALERTS, simTime: 570 });
    q<HTMLButtonElement>(el, 'button[data-ack="bnwas"]').click();
    q<HTMLButtonElement>(el, 'button.ack-all').click();
    q<HTMLButtonElement>(el, 'button.silence').click();
    expect(cmds).toEqual([{ type: 'alertAcknowledge', args: { id: 'bnwas' } }, { type: 'alertAcknowledge' }, { type: 'alertSilence' }]);
    let toggled: boolean | undefined;
    document.body.addEventListener('alert-sound-toggle', (e) => (toggled = (e as CustomEvent<boolean>).detail));
    q<HTMLButtonElement>(el, 'button.sound').click();
    expect(toggled).toBe(false);
    expect(cmds).toHaveLength(3);
  });

  it('「未確認 N」徽章顏色取需確認者的最高優先序(已確認的 alarm 不算)', async () => {
    const list = [
      alert({ id: 'mainEngine', number: 9, state: 'activeAcknowledged', audible: false }),
      alert({ id: 'echoSounderFailure', number: 3, priority: 'warning', audible: true }),
    ];
    const el = await mount('brm-alert-panel', { alerts: list });
    const badge = q<HTMLElement>(el, '.count');
    expect(badge.textContent).toContain('未確認 1');
    expect(badge.classList.contains('warning')).toBe(true);
    const red = await mount('brm-alert-panel', { alerts: [...list, alert({ id: 'gyroFailure', number: 1 })] });
    expect(q<HTMLElement>(red, '.count').classList.contains('warning')).toBe(false);
  });

  it('caution 不需確認、不閃;無警報「無警報」;核心不支援時提示', async () => {
    const el = await mount('brm-alert-panel', { alerts: [alert({ id: 'bnwas', priority: 'caution', audible: false })] });
    expect(q(el, '.alert svg rect.shape')).not.toBeNull();
    expect(q(el, '.alert svg.flash')).toBeNull();
    expect(q(el, 'button.ack')).toBeNull();
    expect(q<HTMLButtonElement>(el, 'button.silence').disabled).toBe(true);
    const empty = await mount('brm-alert-panel', { alerts: [] });
    expect(text(empty)).toContain('無警報');
    const unsup = await mount('brm-alert-panel', { alerts: [], unsupported: true });
    expect(text(unsup)).toContain('核心未回報警報');
  });
});

describe('<brm-bnwas> BNWAS 指示', () => {
  const st = (over: Partial<BnwasStatus>): BnwasStatus => ({ mode: 'on', active: true, stage: 0, remaining_s: 300, dormant_s: 540, ...over });
  it('休眠倒數與進度條;第 1 階段黃;第 2 階段紅閃;重設送 bnwasReset', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-bnwas', { bnwas: st({}) });
    expect(text(el)).toContain('第 1 階段 5:00');
    expect(q(el, '.bar > span')).not.toBeNull();
    expect(q(el, '.tile.level-normal')).not.toBeNull();
    (el as unknown as { bnwas: BnwasStatus }).bnwas = st({ stage: 1, remaining_s: 12 });
    await el.updateComplete;
    expect(q(el, '.tile.level-caution')).not.toBeNull();
    expect(q(el, '.tile.flash')).toBeNull();
    (el as unknown as { bnwas: BnwasStatus }).bnwas = st({ stage: 2, remaining_s: 45.02 });
    await el.updateComplete;
    expect(q(el, '.tile.level-alarm.flash')).not.toBeNull();
    expect(text(el)).toContain('第 2 階段 · 駕駛台聲響');
    expect(text(el)).toContain('第 3 階段 0:46');
    q<HTMLButtonElement>(el, 'button.reset').click();
    expect(cmds).toEqual([{ type: 'bnwasReset' }]);
  });
  it('關閉或核心未回報時重設停用', async () => {
    const off = await mount('brm-bnwas', { bnwas: { mode: 'off', active: false, stage: 0, dormant_s: 540 } satisfies BnwasStatus });
    expect(text(off)).toContain('BNWAS 關');
    expect(q<HTMLButtonElement>(off, 'button.reset').disabled).toBe(true);
    const none = await mount('brm-bnwas', {});
    expect(text(none)).toContain('核心未回報');
  });
});

describe('conning 條的感測器無效標示', () => {
  it('電羅經/測深儀/GNSS/計程儀無效時標「無效」', async () => {
    const state = { ...defaultState('FSB1'), heading: 28.25, sensors: { headingValid: false, positionValid: false, depthValid: false, stwValid: false, magneticHeading: 38 } };
    const el = await mount('brm-conning-bar', { state, config: getShipConfig('FSB1'), issues: [] });
    const sub = (tag: string) => el.shadowRoot!.querySelector(tag) as El;
    for (const tag of ['brm-heading', 'brm-speed', 'brm-depth', 'brm-clock']) await sub(tag).updateComplete;
    expect(text(sub('brm-heading'))).toContain('無效');
    expect(q(sub('brm-heading'), '.value.stale')).not.toBeNull();
    expect(text(sub('brm-speed'))).toContain('無效');
    expect(text(sub('brm-depth'))).toContain('無效');
    expect(text(sub('brm-clock'))).toContain('GNSS 無效');
    const ok = await mount('brm-conning-bar', { state: defaultState('FSB1'), config: getShipConfig('FSB1'), issues: [] });
    const h = ok.shadowRoot!.querySelector('brm-heading') as El;
    await h.updateComplete;
    expect(text(h)).not.toContain('無效');
  });
});

describe('教官站:警報監看、真值 vs 指示值、講評', () => {
  const events: SimEvent[] = [
    { kind: 'alertRaised', tick: 24001, t: 480.02, source: 'engine', itemId: 'gyroFailure', message: '電羅經失效', visible: true, seq: 23, data: { id: 'gyroFailure', priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', count: 1, raisedAt_s: 480.02, repeat: false } },
    { kind: 'bnwasStage', tick: 27001, t: 540.02, source: 'engine', itemId: 'bnwas', visible: true, seq: 24, data: { stage: 1, from: 0, reason: 'dormantElapsed' } },
    { kind: 'alertAcknowledged', tick: 24250, t: 485, source: 'engine', itemId: 'gyroFailure', visible: true, seq: 25, data: { id: 'gyroFailure', priority: 'alarm', state: 'activeAcknowledged', count: 1, raisedAt_s: 480.02, ackTime_s: 4.98 } },
  ];

  it('現行警報代為確認、全部確認;BNWAS 模式切換帶休眠期;歷史含確認耗時', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-alert-monitor', { alerts: ALERTS, bnwas: { mode: 'on', active: true, stage: 1, remaining_s: 10, dormant_s: 540 }, events, simTime: 570 });
    expect(qa<HTMLElement>(el, 'tr[data-alert]').map((r) => r.dataset['alert'])).toEqual(['mainEngine', 'gyroFailure', 'bnwas', 'headingMonitor']);
    q<HTMLButtonElement>(el, 'tr[data-alert="headingMonitor"] button.ack-for').click();
    q<HTMLButtonElement>(el, 'button.ack-all').click();
    const input = q<HTMLInputElement>(el, '.mode-row input');
    input.value = '300';
    input.dispatchEvent(new Event('input'));
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.bnwas-mode[data-mode="auto"]').click();
    q<HTMLButtonElement>(el, 'button.bnwas-reset').click();
    expect(cmds).toEqual([
      { type: 'alertAcknowledge', args: { id: 'headingMonitor' } },
      { type: 'alertAcknowledge' },
      { type: 'bnwasMode', value: 'auto', args: { dormant_s: 300 } },
      { type: 'bnwasReset' },
    ]);
    expect(q(el, 'button.bnwas-mode[data-mode="on"].active')).not.toBeNull();
    expect(text(el)).toContain('第 1 階段 1 次');
    expect(q<HTMLElement>(el, 'tr[data-history="gyroFailure"]').textContent).toContain('確認 +5.0 s');
  });

  it('compact:無模式列與歷史;核心不支援時按鈕停用', async () => {
    const el = await mount('brm-alert-monitor', { alerts: [], bnwas: undefined, events: [], unsupported: true }, { compact: '' });
    expect(q(el, '.mode-row')).toBeNull();
    expect(text(el)).toContain('核心不支援');
    expect(q<HTMLButtonElement>(el, 'button.bnwas-reset').disabled).toBe(true);
  });

  it('真值 vs 指示值:電羅經故障列標紅並標無效;無 truth 時提示', async () => {
    const state = {
      ...defaultState('FSB1'),
      heading: 28.25,
      faults: ['gyroFailure'],
      truth: { heading: 37.19, pos: { lat: 0, lon: 0, x: 0, y: 0 }, cog: 0, sog: 0, stw: 0, depthBelowKeel: 0, headingError_deg: -8.94, positionError_m: 0 },
      sensors: { headingValid: false, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 38.19 },
      autopilot: { enabled: false, setHeading: 29, rotLimit_degPerMin: 15, available: false },
    };
    const el = await mount('brm-sensor-truth', { state });
    const row = q<HTMLElement>(el, 'tr[data-row="heading"]');
    expect(row.classList.contains('faulty')).toBe(true);
    expect(row.textContent).toContain('無效');
    expect(row.textContent).toContain('-8.9°');
    expect(q<HTMLElement>(el, 'tr[data-row="position"]').classList.contains('faulty')).toBe(false);
    expect(text(el)).toContain('不可用');
    const none = await mount('brm-sensor-truth', { state: defaultState('FSB1') });
    expect(text(none)).toContain('核心未回報真值');
  });

  it('講評摘要顯示核心 summary 的警報確認時間與 BNWAS 階段次數', async () => {
    const coreSummary = parseAssessmentSummary({
      version: 1,
      final: true,
      alerts: { raised: 2, acknowledged: 1, unacknowledged: 1, escalations: 1, silences: 0, meanAckTime_s: 4.98, maxAckTime_s: 4.98, records: [{ id: 'gyroFailure', priority: 'alarm', raisedAt_s: 480.02, ackTime_s: 4.98, escalated: false, silences: 0 }, { id: 'bnwas', priority: 'alarm', raisedAt_s: 540.02, escalated: true, silences: 0 }], bnwas: { stage1: 1, stage2: 1, stage3: 0, resets: 0 } },
      faultResponses: [{ fault: 'gyroFailure', injectedAt_s: 480, alertAckDt_s: 5, acknowledgements: [] }],
    });
    const el = await mount('brm-debrief-summary', { coreSummary });
    const t = text(el);
    expect(t).toContain('警報與 BNWAS');
    expect(t).toContain('平均確認時間');
    expect(t).toContain('5.0 s'); // 4.98 → 5.0
    expect(t).toContain('1:1 · 2:1 · 3:0');
    expect(t).toContain('電羅經失效');
    expect(t).toContain('警報確認 +5.0 s');
  });

  it('故障面板:目錄來自 fault-catalogue.json,感測器故障為按鈕;參數注入送 args', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-fault-panel', { active: [] });
    expect(q(el, 'button[data-fault="gyroDrift"]')).not.toBeNull();
    expect(q(el, 'button[data-fault="radarFailure"]')).toBeNull(); // 只記錄者收進選單
    const sel = q<HTMLSelectElement>(el, '.args select');
    sel.value = 'gyroDrift';
    sel.dispatchEvent(new Event('change'));
    await el.updateComplete;
    const input = q<HTMLInputElement>(el, 'input[data-arg="rate_degPerMin"]');
    input.value = '-1';
    input.dispatchEvent(new Event('input'));
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.inject-args').click();
    q<HTMLButtonElement>(el, 'button[data-fault="gyroFailure"]').click();
    expect(cmds).toEqual([{ type: 'injectFault', args: { name: 'gyroDrift', args: { rate_degPerMin: -1 } } }, { type: 'injectFault', value: 'gyroFailure' }]);
  });
});

describe('整合(示範模式)', () => {
  beforeAll(async () => {
    await import('../src/app.js');
    await import('../src/instructor-app.js');
  });

  it('操船台:中央警報面板與 BNWAS 指示可見;示範狀態帶 alerts/bnwas', async () => {
    history.replaceState(null, '', '/?demo=1');
    const app = await mount('brm-app');
    const inner = app as unknown as { shipState: { bnwas?: BnwasStatus; truth?: unknown } };
    expect(app.shadowRoot!.querySelector('.middle brm-alert-panel')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('.middle brm-bnwas')).not.toBeNull();
    expect(inner.shipState.bnwas).toMatchObject({ mode: 'on', active: true });
    (app.shadowRoot!.querySelector('brm-alert-panel') as HTMLElement).dispatchEvent(new CustomEvent('alert-sound-toggle', { detail: false, bubbles: true, composed: true }));
    await app.updateComplete;
    expect((app.shadowRoot!.querySelector('brm-alert-panel') as unknown as { soundOn: boolean }).soundOn).toBe(false);
  });

  it('教官站:警報監看與真值面板可見;代為確認經示範來源產生 alertAcknowledged 事件', async () => {
    history.replaceState(null, '', '/instructor.html?demo=1');
    const app = await mount('brm-instructor-app');
    expect(app.shadowRoot!.querySelector('.bottom brm-alert-monitor')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('.bottom brm-sensor-truth')).not.toBeNull();
    const inner = app as unknown as { onCommand(ev: CustomEvent<SimCommand>): void; events: readonly SimEvent[] };
    inner.onCommand(new CustomEvent('sim-command', { detail: { type: 'bnwasMode', value: 'off' } }));
    expect(inner.events.some((e) => e.kind === 'bnwasModeChanged')).toBe(true);
  });
});
