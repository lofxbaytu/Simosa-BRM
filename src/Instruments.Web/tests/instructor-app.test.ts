// 教官站主元件(happy-dom):重播檢視常駐 — 載入紀錄後切回「即時」再切回「重播檢視」,紀錄與時間軸位置仍在;
// 即時模式下事件流點擊只在重播檢視已載有紀錄時跳轉,未載入時不會把即時版面換成空白的重播頁。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { defaultState } from '../src/lib/state-parser.js';
import type { ReplayView } from '../src/components/instructor/replay-view.js';
import type { SimEvent } from '../src/types/event.js';

type App = HTMLElement & { updateComplete: Promise<unknown> };

const line = (t: number) => JSON.stringify({ kind: 'state', state: { ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x: t, y: 0 } } });
const record = [
  JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: 0.02, scenario: { id: 'E01_baseline' } }),
  line(0),
  '{"kind":"event","tick":100,"event":{"kind":"frozen","tick":100,"t":2,"source":"instructor","seq":0}}',
  line(2),
  line(4),
  '{"kind":"footer","finalTick":200,"stateHash":"AB","lines":5,"inputs":0,"sha256":"CD"}',
].join('\n');

const seekEvent = (t: number): SimEvent => ({ kind: 'frozen', tick: Math.round(t / 0.02), t, source: 'instructor', seq: 0, visible: false, phase: 'normal' });

async function mountApp(): Promise<App> {
  history.replaceState(null, '', '/?demo=1'); // 示範模式:不開 WebSocket
  const el = document.createElement('brm-instructor-app') as App;
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function headerButton(el: App, label: string): HTMLButtonElement {
  const btn = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('header .seg[aria-label="檢視"] button')).find((b) => b.textContent?.trim() === label);
  if (!btn) throw new Error(`缺按鈕 ${label}`);
  return btn;
}

describe('教官站重播檢視常駐', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });

  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('切回即時再切回重播檢視:同一個元件、紀錄與時刻仍在', async () => {
    const app = await mountApp();
    const replay = app.shadowRoot!.querySelector('brm-replay-view') as (ReplayView & App) | null;
    expect(replay).not.toBeNull();
    expect(replay!.hasAttribute('hidden')).toBe(true);
    expect(app.shadowRoot!.querySelector('.full')!.hasAttribute('hidden')).toBe(false);
    replay!.loadText(record, 'r.jsonl');
    replay!.seekTo(2);
    expect(replay!.loaded).toBe(true);

    headerButton(app, '重播檢視').click();
    await app.updateComplete;
    expect(replay!.hasAttribute('hidden')).toBe(false);
    expect(app.shadowRoot!.querySelector('.full')!.hasAttribute('hidden')).toBe(true);

    headerButton(app, '即時').click();
    await app.updateComplete;
    expect(replay!.hasAttribute('hidden')).toBe(true);
    headerButton(app, '重播檢視').click();
    await app.updateComplete;
    const again = app.shadowRoot!.querySelector('brm-replay-view') as ReplayView & App;
    expect(again).toBe(replay);
    expect(again.loaded).toBe(true);
    expect(again.time).toBe(2);
  });

  it('即時模式的事件流點擊:未載入紀錄時不切換;已載入時切到重播檢視並跳至該時刻', async () => {
    const app = await mountApp();
    const replay = app.shadowRoot!.querySelector('brm-replay-view') as ReplayView & App;
    const stream = app.shadowRoot!.querySelector('brm-event-stream') ?? app;
    stream.dispatchEvent(new CustomEvent<SimEvent>('event-seek', { detail: seekEvent(2), bubbles: true, composed: true }));
    await app.updateComplete;
    expect(replay.hasAttribute('hidden')).toBe(true);
    expect(app.shadowRoot!.querySelector('.full')!.hasAttribute('hidden')).toBe(false);
    expect(replay.time).toBe(0);

    replay.loadText(record, 'r.jsonl');
    stream.dispatchEvent(new CustomEvent<SimEvent>('event-seek', { detail: seekEvent(4), bubbles: true, composed: true }));
    await app.updateComplete;
    await app.updateComplete;
    expect(replay.hasAttribute('hidden')).toBe(false);
    expect(replay.time).toBe(4);
  });
});

describe('教官站即時模式倒帶(restore)', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });

  afterEach(() => {
    document.body.innerHTML = '';
  });

  type Internals = { onSimEvent(ev: SimEvent): void; onState(state: unknown, issues: string[]): void; stopSource(): void; restartExercise(): void; events: readonly SimEvent[]; alarmTargets: ReadonlySet<string> };
  /** 掛載後停掉示範來源並清空(示範運動學啟動時即推送狀態與 DEMO-01 的 cpaAlarm),改由測試直接餵狀態/事件 */
  const mountQuiet = async (): Promise<[App, Internals]> => {
    const app = await mountApp();
    const inner = app as unknown as Internals;
    inner.stopSource();
    inner.restartExercise();
    await app.updateComplete;
    return [app, inner];
  };
  const state = (t: number) => ({ ...defaultState('FSB1'), t, tick: Math.round(t / 0.02) });
  const cpa = (t: number, targetId: string, kind: 'cpaAlarm' | 'cpaClear' = 'cpaAlarm'): SimEvent => ({ kind, tick: Math.round(t / 0.02), t, source: 'traffic', seq: 0, visible: false, targetId });

  it('restore 後第一筆 tick 倒退的狀態:丟棄 t > restore tick × dt 的事件並重算 CPA 警報;restore 指令本身不改事件流', async () => {
    const [app, inner] = await mountQuiet();
    const plot = () => app.shadowRoot!.querySelector('brm-track-plot') as HTMLElement & { alarmTargets: ReadonlySet<string> };
    inner.onState(state(200), []);
    inner.onSimEvent(cpa(100, 'T0'));
    inner.onSimEvent(cpa(100.02, 'T1')); // tick 5001:倒帶 tick 與第一筆新狀態(tick 5002)之間的舊分支事件
    inner.onSimEvent(cpa(300, 'T2'));
    inner.onState(state(400), []);
    await app.updateComplete;
    expect([...inner.alarmTargets].sort()).toEqual(['T0', 'T1', 'T2']);
    expect([...plot().alarmTargets].sort()).toEqual(['T0', 'T1', 'T2']);

    app.dispatchEvent(new CustomEvent('sim-command', { detail: { type: 'restore', args: { tick: 5000 } }, bubbles: true, composed: true }));
    await app.updateComplete;
    expect(inner.events).toHaveLength(3); // 示範來源不倒帶:等核心真的倒退才處理
    inner.onState(state(100.04), []); // 核心倒帶後第一筆廣播(tick 5002)
    await app.updateComplete;
    expect(inner.events.map((e) => e.t)).toEqual([100]);
    expect([...inner.alarmTargets]).toEqual(['T0']);
    expect([...plot().alarmTargets]).toEqual(['T0']);
    // 倒帶後的新事件照常累積;再次 tick 倒退但無 restore 指令(例如核心自行倒帶)→ 以該狀態的 t 為倒帶點
    inner.onSimEvent(cpa(150, 'T3'));
    inner.onState(state(160), []);
    inner.onState(state(120), []);
    expect(inner.events.map((e) => e.t)).toEqual([100]);
    expect([...inner.alarmTargets]).toEqual(['T0']);
  });

  it('事件流超過上限擠掉 cpaAlarm 後,警報集合仍保留該目標;reset 清空', async () => {
    const [app, inner] = await mountQuiet();
    inner.onState(state(1), []);
    inner.onSimEvent(cpa(1, 'T1'));
    for (let i = 0; i < 2000; i++) inner.onSimEvent({ kind: 'message', tick: 100 + i, t: 2 + i * 0.02, source: 'timeline', seq: i, visible: false });
    expect(inner.events.some((e) => e.kind === 'cpaAlarm')).toBe(false);
    expect([...inner.alarmTargets]).toEqual(['T1']);
    app.dispatchEvent(new CustomEvent('sim-command', { detail: { type: 'reset' }, bubbles: true, composed: true }));
    await app.updateComplete;
    expect(inner.alarmTargets.size).toBe(0);
    expect(inner.events).toHaveLength(0);
  });
});

describe('教官站操舵模式(示範模式)', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });

  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('面板顯示示範的 state.steering;按鈕送出的指令進教官指令紀錄、模式變更進學員操作紀錄;應急操舵下自動舵被 commandRejected 標為核心拒絕', async () => {
    const app = await mountApp();
    const panel = app.shadowRoot!.querySelector('brm-steering-panel') as (HTMLElement & { updateComplete: Promise<unknown>; state: unknown }) | null;
    expect(panel).not.toBeNull();
    await panel!.updateComplete;
    expect(panel!.shadowRoot!.textContent).toContain('FU 隨動 · 泵 both');

    const click = async (sel: string) => {
      panel!.shadowRoot!.querySelector<HTMLButtonElement>(sel)!.click();
      await app.updateComplete;
      await panel!.updateComplete;
    };
    await click('button.mode-nfu');
    expect(panel!.shadowRoot!.textContent).toContain('NFU 非隨動 · 泵 both · 推桿放開');
    const commandLog = app.shadowRoot!.querySelector('brm-command-log') as HTMLElement & { entries: ReadonlyArray<{ command: { type: string; value?: unknown }; ack?: { ok: boolean; detail?: string } }> };
    expect(commandLog.entries.at(-1)!.command).toEqual({ type: 'steeringMode', value: 'nfu', args: { direction: 'stop' } });
    const opLog = app.shadowRoot!.querySelector('brm-operation-log') as HTMLElement & { events: ReadonlyArray<{ kind: string; label: string }> };
    expect(opLog.events.filter((e) => e.kind === 'steeringMode').map((e) => e.label)).toEqual(['操舵模式 FU 隨動 → NFU 非隨動(泵 both)']);

    await click('button.mode-emergency');
    await click('button.mode-autopilot'); // 停用中:不送
    expect(commandLog.entries.at(-1)!.command.value).toBe('emergency');
    // 直接送 autopilot 指令(學員站的自動舵面板)→ 示範核心發 commandRejected → 該筆標為核心拒絕
    app.dispatchEvent(new CustomEvent('sim-command', { detail: { type: 'autopilot', args: { enabled: true, heading: 90 } }, bubbles: true, composed: true }));
    await app.updateComplete;
    const last = commandLog.entries.at(-1)!;
    expect(last.command.type).toBe('autopilot');
    expect(last.ack).toMatchObject({ ok: false, detail: '應急操舵中不可自動舵' });
    expect(opLog.events.filter((e) => e.kind === 'steeringMode')).toHaveLength(2); // nfu、emergency 各一筆,事件與狀態不重複
  });
});
