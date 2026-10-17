// 教官站元件煙霧測試(happy-dom):情境面板、執行控制、環境、故障、自船覆寫、BRM 標記鈕送出的事件與指令,
// 講評摘要(核心 summary 優先、本地推導後備)、重播檢視可載入 JSON Lines(含 event/summary 行)並依時間軸取狀態、
// conning 條的航線/能見度方塊。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
import type { DebriefSummaryView } from '../src/components/instructor/debrief-summary.js';
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

const here = path.dirname(fileURLToPath(import.meta.url));
const summaryFixture = readFileSync(path.resolve(here, 'fixtures/summary-E07.json'), 'utf-8');
const coreSummary = parseAssessmentSummary((JSON.parse(summaryFixture) as { summary: unknown }).summary)!;
const E07: ScenarioSummary = parseScenario(readFileSync(path.resolve(here, 'fixtures/E07_steering_failure.yaml'), 'utf-8'), 'data/scenarios/E07_steering_failure.yaml');

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
  await import('../src/components/conning-bar.js');
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

  it('擴充欄位摘要與簡報物件(角色、附件、最少人數、參數集)', async () => {
    const el = await mount('brm-scenario-panel', { scenarios: [E07] });
    const text = shadowText(el);
    expect(text).toContain('時間表 7 項(手動 1) · 航線 4 航點 · 區域 3 · 評估規則 13 條 · 參數引用');
    expect(text).toContain('角色:引水人(帶領者,必要)、機控室(語音腳本)、VTS(語音腳本) · 最少 3 人');
    expect(text).toContain('附件:');
    expect(text).toContain('參數集 data/parameters/company-procedures.yaml');
    expect(el.shadowRoot!.querySelector('.attachments a')?.getAttribute('href')).toBe('data/scenarios/E07_steering_failure/docs/passage-plan-mailiao.pdf');
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
    // 結束練習需按兩次,送 endExercise 並發出 exercise-ended
    const ended: Event[] = [];
    el.addEventListener('exercise-ended', (e) => ended.push(e));
    const n = cmds.length;
    q<HTMLButtonElement>(el, 'button.end').click();
    expect(cmds.length).toBe(n);
    q<HTMLButtonElement>(el, 'button.end').click();
    expect(cmds.at(-1)).toEqual({ type: 'endExercise', args: { reason: '教官結束練習' } });
    expect(ended).toHaveLength(1);
    el.ended = true;
    await el.updateComplete;
    expect(q<HTMLButtonElement>(el, 'button.end').disabled).toBe(true);
  });
});

describe('環境面板', () => {
  it('帶入目前狀態並分組送 setEnvironment;能見度與漸變標示僅 C# 核心', async () => {
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
    expect(shadowText(el)).toContain('僅 C# 核心');
    // 漸變秒數附在每次套用上(設計文件第 3.5 節 ramp_s)
    setInput(el, 'input.ramp', '180');
    q<HTMLButtonElement>(el, 'button.apply-depth').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { waterDepth: 12, ramp_s: 180 } });
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
    expect(text).toContain('本地推導');
  });

  it('有核心 summary 時優先顯示評分/硬性失敗/必要事件/容許帶/參數來源;本地推導可並列', async () => {
    const local = {
      samples: 10,
      duration_s: 3600,
      minUkc_m: 1.4,
      minUkcAt_s: 2210,
      maxRudder_deg: 20,
      maxRudderAt_s: 50,
      maxRot_degPerMin: 21,
      maxRotAt_s: 640,
      telegraphChanges: 4,
      rudderOrders: 36,
      hardOverCount: 0,
      distance_nm: 5.7,
      avgSpeed_kn: 5.8,
      maxSog_kn: 8.3,
      timeOverSpeedLimit_s: 0,
      timeUkcBelowAlarm_s: 0,
      timeRotOverLimit_s: 0,
      agroundEvents: 0,
      collisionEvents: 0,
      frozen_s: 45,
      thresholds: { portSpeedLimit_kn: 8, ukcAlarm_m: 1, rotLimit_degPerMin: 30, hardOver_deg: 35 },
    };
    const el = await mount<DebriefSummaryView>('brm-debrief-summary', { summary: local, coreSummary });
    const text = shadowText(el);
    expect(text).toContain('85 分 / 及格 70 通過 定稿 · E07_steering_failure · 參數集 simosa-company-procedures 2026.10.1');
    expect(text).toContain('必要事件');
    expect(text).toContain('steeringResponse 達成 +35 s / 限 60 s · telegraph');
    expect(text).toContain('SMS-EMG-004 rev.1 · emergency.steeringFailure.firstAction_s');
    expect(text).toContain('vtsReport 逾時');
    expect(text).toContain('xtdMax(track.maxXtd_m) 74.0 · 警告 警告 50.0 / 失敗 100.0 · 扣 10 SMS-NAV-012 rev.2 §4.1');
    expect(text).toContain('steeringGear(steeringFail) 首個動作 telegraph +35 s');
    expect(text).toContain('通知機控室(船長) +82 s');
    expect(text).toContain('會遇 T1 CPA 0.62 nm');
    expect(text).toContain('navigation.xtd.channel_m = 50 m — SMS-NAV-012 rev.2 §4.1');
    expect(text).toContain('自動評分僅輔助');
    expect(text).not.toContain('核心 5.70 nm');
    q<HTMLButtonElement>(el, 'button.toggle-local').click();
    await el.updateComplete;
    const both = shadowText(el);
    expect(both).toContain('5.70 nm');
    expect(both).toContain('核心 5.80 nm');
    // 硬性失敗
    const failed = parseAssessmentSummary({ ...coreSummary.raw, assessment: { score: 0, passMark: 70, passed: false, hardFailed: true, hardFails: [{ id: 'grounding', rule: 'grounding', t_s: 1200 }], bands: [], required: [], parameters: [] } })!;
    el.coreSummary = failed;
    await el.updateComplete;
    expect(shadowText(el)).toContain('硬性失敗');
    expect(shadowText(el)).toContain('grounding 擱淺 20:00');
  });
});

describe('講評摘要的必要事件倒數', () => {
  const cd = (id: string, state: 'counting' | 'overdue' | 'met' | 'inactive', remaining: number | null, within: number | null = 60) => ({ rule: E07.assessment.required.find((r) => r.id === id)!, startedAt_s: 420, within_s: within, deadline_t: within === null ? null : 420 + within, state, remaining_s: remaining });
  it('無核心 summary 時列「必要事件(即時倒數)」;核心 summary 的 pending 列改顯示剩餘/逾時秒數', async () => {
    const local = await mount<DebriefSummaryView>('brm-debrief-summary', { summary: null, countdowns: [cd('steeringFirstAction', 'counting', 30), cd('notifyEcr', 'overdue', 12, 120), cd('emergencySteering', 'counting', null, null), cd('vtsReport', 'inactive', null)] });
    const text = shadowText(local);
    expect(text).toContain('必要事件(即時倒數)');
    expect(text).toContain('steeringFirstAction 剩 30 s 自 07:00 / 限 60 s');
    expect(text).toContain('notifyEcr 逾時 12 s 自 07:00 / 限 120 s');
    expect(text).toContain('emergencySteering 計時中 自 07:00');
    expect(text).not.toContain('vtsReport');
    expect(text).toContain('尚無資料');
    const pending = { ...coreSummary, assessment: { ...coreSummary.assessment, required: coreSummary.assessment.required.map((r) => (r.id === 'steeringResponse' ? { id: r.id, state: 'pending' as const } : r)) } };
    const core = await mount<DebriefSummaryView>('brm-debrief-summary', { coreSummary: pending, countdowns: [cd('steeringFirstAction', 'counting', 30), { ...cd('steeringFirstAction', 'overdue', 7, 60), rule: { ...E07.assessment.required[0]!, id: 'steeringResponse' } }] });
    const rows = Array.from(core.shadowRoot!.querySelectorAll<HTMLElement>('table[aria-label="必要事件"] tr'));
    const row = rows.find((r) => r.textContent!.includes('steeringResponse'))!;
    expect(row.textContent!.replace(/\s+/g, ' ')).toContain('逾時 7 s / 限 60 s');
    expect(row.className).toBe('alarm');
    expect(shadowText(core)).not.toContain('必要事件(即時倒數)');
  });
});

describe('conning 條的航線/能見度方塊', () => {
  it('有 state.route 時顯示 XTD(右正 → R 偏右)、下一航點、速限與能見度;無則不顯示', async () => {
    const cfg = (await import('../src/lib/ship-config.js')).getShipConfig('FSB1');
    const withRoute = { ...defaultState('FSB1'), visibility_nm: 0.8, route: { legIndex: 1, nextWaypoint: 'WP3', xtd_m: 62, xtdLimit_m: 50, distanceToWaypoint_nm: 0.8, bearingToWaypoint_deg: 70, legCourse_deg: 70, speedLimit_kn: 8 } };
    const bar = await mount('brm-conning-bar', { state: withRoute, config: cfg, issues: [] });
    const route = bar.shadowRoot!.querySelector('brm-route') as (HTMLElement & { updateComplete: Promise<unknown> }) | null;
    expect(route).not.toBeNull();
    await route!.updateComplete;
    const text = shadowText(route!);
    expect(text).toContain('→ WP3');
    expect(text).toContain('62m R 偏右 限 50 m');
    expect(text).toContain('下一航點WP3 0.80 nm');
    expect(text).toContain('速限8 kn');
    expect(text).toContain('能見度0.8 nm');
    expect(route!.shadowRoot!.querySelector('.bar.over')).not.toBeNull();
    const plain = await mount('brm-conning-bar', { state: defaultState('FSB1'), config: cfg, issues: [] });
    expect(plain.shadowRoot!.querySelector('brm-route')).toBeNull();
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

  it('version 2 紀錄:事件標記、事件流、核心 summary 與評分、航線疊圖、參數集版次;點事件跳至該時刻', async () => {
    const el = await mount<ReplayView>('brm-replay-view');
    const line = (t: number) => JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x: t, y: 0 } } });
    const text = [
      JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02, scenario: { id: 'E07_steering_failure', origin: { lat: 23.78, lon: 120.1 }, route: { waypoints: [{ name: 'WP1', x: 0, y: 0 }, { name: 'WP2', x: 1000, y: 0, xtdLimit_m: 50 }] }, zones: [{ id: 'z', type: 'noGo', polygon: [{ x: 0, y: 100 }, { x: 100, y: 100 }, { x: 50, y: 200 }] }] }, parameterSet: { id: 'simosa-company-procedures', version: '2026.10.1', resolved: [{ path: 'a', key: 'k' }] } }),
      line(0),
      '{"kind":"event","tick":1500,"event":{"kind":"faultInjected","tick":1500,"t":30,"source":"timeline","itemId":"steeringFail","seq":1,"data":{"fault":"steeringGear"}}}',
      line(60),
      '{"kind":"event","tick":4500,"event":{"kind":"hardFail","tick":4500,"t":90,"source":"assessment","itemId":"grounding","seq":2,"data":{"rule":"grounding"}}}',
      line(120),
      summaryFixture.replace(/\s*\n\s*/g, ''),
      '{"kind":"footer","finalTick":6000,"stateHash":"AB","lines":8,"inputs":0,"sha256":"CD"}',
    ].join('\n');
    const data = el.loadText(text, 'e07.jsonl');
    expect(data.errors).toEqual([]);
    await el.updateComplete;
    const txt = shadowText(el);
    expect(txt).toContain('2 筆事件');
    expect(txt).toContain('參數集 simosa-company-procedures 2026.10.1(已解析 1 個引用)');
    expect(txt).toContain('評分 85(通過)');
    expect(txt).toContain('核心 summary(定稿)');
    const debrief = el.shadowRoot!.querySelector('brm-debrief-summary') as HTMLElement & { updateComplete: Promise<unknown> };
    await debrief.updateComplete;
    expect(shadowText(debrief)).toContain('85 分 / 及格 70');
    const marks = Array.from(el.shadowRoot!.querySelectorAll<HTMLElement>('.marks .mark'));
    expect(marks.map((m) => m.className)).toEqual(['mark phase', 'mark phase', 'mark timeline warning', 'mark assessment alarm']);
    expect(marks[3]!.style.left).toBe('75%');
    const plot = el.shadowRoot!.querySelector('brm-track-plot') as HTMLElement & { overlay: { route: { waypoints: unknown[] } | null; zones: unknown[]; events: unknown[] } };
    expect(plot.overlay.route?.waypoints).toHaveLength(2);
    expect(plot.overlay.zones).toHaveLength(1);
    expect(plot.overlay.events).toHaveLength(0); // t = 0 時尚無事件
    const stream = el.shadowRoot!.querySelector('brm-event-stream') as HTMLElement & { updateComplete: Promise<unknown> };
    await stream.updateComplete;
    (stream.shadowRoot!.querySelector('.item[data-kind="hardFail"]') as HTMLElement).click();
    await el.updateComplete;
    expect(el.time).toBe(90);
    expect(shadowText(el)).toContain('01:30 / 02:00');
    expect(plot.overlay.events).toHaveLength(2);
  });

  it('倒帶標記:時間軸畫 rewinds[],懸停顯示倒帶 tick 與丟棄筆數;目標船依時刻取該筆 state,cpaAlarm 由 t ≤ 目前時刻的事件推得', async () => {
    const el = await mount<ReplayView>('brm-replay-view');
    const target = (id: string, cpa: number) => ({ id, name: id, mmsi: 1, shipType: 'cargo', loa: 90, beam: 14, pos: { lat: 0, lon: 0, x: 500, y: 500 }, heading: 180, cog: 180, sog: 5, rot: 0, range_nm: 1, bearing_deg: 45, cpa_nm: cpa, tcpa_min: 6, aisOn: true, lights: 'powerDriven', behaviour: 'hold' });
    const line = (t: number, targets: unknown[]) => JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x: t, y: 0 }, targets } });
    const text = [
      JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02, scenario: { id: 'E01_baseline' } }),
      line(0, []),
      line(2, [target('T1', 0.9)]),
      '{"kind":"event","tick":150,"event":{"kind":"cpaAlarm","tick":150,"t":3,"targetId":"T1","cpa_nm":0.3,"tcpa_min":5}}',
      line(4, [target('T1', 0.3)]),
      line(6, [target('T1', 0.2)]),
      // 倒帶(restore 至 tick 100):t > 2 的舊分支狀態與事件丟棄
      line(2.02, [target('T1', 0.9)]),
      line(4, [target('T1', 0.8), target('T2', 2)]),
      '{"kind":"footer","finalTick":200}',
    ].join('\n');
    const data = el.loadText(text, 'rewind.jsonl');
    expect(data.rewinds).toEqual([{ t: 2.02, from: 6, droppedStates: 2, droppedEvents: 1 }]);
    await el.updateComplete;
    const rewindMarks = Array.from(el.shadowRoot!.querySelectorAll<HTMLElement>('.marks .mark.rewind'));
    expect(rewindMarks).toHaveLength(1);
    expect(rewindMarks[0]!.title).toBe('倒帶至 00:02(tick 101)← 自 00:06;丟棄 2 筆狀態、1 筆事件');
    expect(rewindMarks[0]!.style.left).toBe(`${(2.02 / 4) * 100}%`);
    const plot = el.shadowRoot!.querySelector('brm-track-plot') as HTMLElement & { targets: ReadonlyArray<{ id: string }>; alarmTargets: ReadonlySet<string>; targetTable: boolean };
    expect(plot.targets).toEqual([]);
    expect(plot.targetTable).toBe(true);
    el.seekTo(4);
    await el.updateComplete;
    expect(plot.targets.map((t) => t.id)).toEqual(['T1', 'T2']);
    // cpaAlarm 事件(t = 3)屬被丟棄的舊分支 → 不在警報中
    expect([...plot.alarmTargets]).toEqual([]);
    rewindMarks[0]!.click();
    await el.updateComplete;
    expect(el.time).toBe(2.02);
    expect(plot.targets.map((t) => t.id)).toEqual(['T1']);
  });

  it('cpaAlarm 狀態隨重播時刻變化;compact 模式不顯示小表', async () => {
    const el = await mount<ReplayView>('brm-replay-view', { compact: true });
    const target = { id: 'T1', name: 'T1', mmsi: 1, shipType: 'cargo', loa: 90, beam: 14, pos: { lat: 0, lon: 0, x: 500, y: 500 }, heading: 180, cog: 180, sog: 5, rot: 0, range_nm: 1, bearing_deg: 45, cpa_nm: 0.3, tcpa_min: 6, aisOn: true, lights: 'powerDriven', behaviour: 'hold' };
    const line = (t: number) => JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), targets: [target] } });
    el.loadText([JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02 }), line(0), '{"kind":"event","tick":100,"event":{"kind":"cpaAlarm","tick":100,"t":2,"targetId":"T1"}}', line(4), '{"kind":"event","tick":300,"event":{"kind":"cpaClear","tick":300,"t":6,"targetId":"T1"}}', line(8)].join('\n'), 'cpa.jsonl');
    await el.updateComplete;
    const plot = el.shadowRoot!.querySelector('brm-track-plot') as HTMLElement & { alarmTargets: ReadonlySet<string>; targetTable: boolean };
    expect(plot.targetTable).toBe(false);
    expect([...plot.alarmTargets]).toEqual([]);
    el.seekTo(4);
    await el.updateComplete;
    expect([...plot.alarmTargets]).toEqual(['T1']);
    el.seekTo(8);
    await el.updateComplete;
    expect([...plot.alarmTargets]).toEqual([]);
    expect(el.shadowRoot!.querySelectorAll('.marks .mark.rewind')).toHaveLength(0);
  });
});
