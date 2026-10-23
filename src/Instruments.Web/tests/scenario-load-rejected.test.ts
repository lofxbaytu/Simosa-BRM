// 情境載入被核心拒絕(交接紀錄待辦第 1 項):C# 核心對驗證錯誤的情境(檔案或 args.scenario 內嵌)發 scenarioLoadRejected
// 帶外事件、狀態不變;Python 參考伺服器以 ack ok=false 回報。教官站要顯示可讀的拒絕原因(含問題清單)、還原載入前的情境與
// 簡化模式階段、載入鈕回復,不得顯示為「已載入」。以假 WebSocket 餵事件框 / ack 框 / 狀態幀。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { defaultState } from '../src/lib/state-parser.js';
import { parseScenario, type ScenarioSummary } from '../src/lib/scenario.js';
import { eventFromFrame, eventLabel, scenarioLoadRejection, type ScenarioLoadRejection } from '../src/lib/sim-event.js';
import { SIM_EVENT_KINDS } from '../src/types/event.js';
import type { SimCommand } from '../src/types/command.js';

class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  sent: string[] = [];
  onopen: (() => void) | null = null;
  onclose: ((ev: { reason: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }
  send(data: string): void {
    this.sent.push(data);
  }
  close(): void {
    this.readyState = 3;
  }
  /** 模擬連線成功並送一框 */
  open(): void {
    this.readyState = 1;
    this.onopen?.();
  }
  push(frame: unknown): void {
    this.onmessage?.({ data: JSON.stringify(frame) });
  }
}

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };
type AppInner = Updatable & {
  commandLog: ReadonlyArray<{ command: SimCommand; ack?: { ok: boolean; detail?: string } }>;
  activeScenario: ScenarioSummary | null;
  simplePhase: string;
  loadPending: boolean;
  scenarioRejection: ScenarioLoadRejection | null;
};

const REJECT_FRAME = {
  type: 'event',
  event: {
    kind: 'scenarioLoadRejected',
    tick: 250,
    t: 5,
    source: 'instructor',
    itemId: 'T_inline',
    message: '情境 T_inline 載入被拒:zones[0].polygon:多邊形至少需要 3 點;assessment.hardFail[0]:zoneEntered 引用不存在的區域:missing',
    phase: 'normal',
    visible: false,
    seq: -1,
    data: {
      scenarioId: 'T_inline',
      origin: 'inline',
      path: null,
      issueCount: 2,
      issues: [
        { path: 'zones[0].polygon', message: '多邊形至少需要 3 點', severity: 'error' },
        { path: 'assessment.hardFail[0]', message: 'zoneEntered 引用不存在的區域:missing', severity: 'error' },
      ],
    },
  },
};

const E01: ScenarioSummary = parseScenario(
  `id: E01_baseline
name: 基線
ship: { id: FSB1, loading: ballast }
initial: { position: { lat: 23.8, lon: 120.05 }, heading: 0, speed: 7.8, telegraph: HAH }
environment: { wind: { trueSpeed: 15, trueDir: 45 }, current: { set: 200, drift: 0.5 }, waterDepth: 30 }
`,
  'data/scenarios/E01_baseline.yaml',
);

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}): Promise<T & Updatable> {
  const el = document.createElement(tag) as T & Updatable;
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function text(el: HTMLElement): string {
  return (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');
}

describe('scenarioLoadRejected 事件解析', () => {
  it('scenarioLoadRejection 取出 id、來源、訊息與問題清單;eventLabel 用核心訊息;kind 在 SIM_EVENT_KINDS 內', () => {
    const ev = eventFromFrame(REJECT_FRAME)!;
    expect(ev.kind).toBe('scenarioLoadRejected');
    expect(ev.source).toBe('instructor');
    expect((SIM_EVENT_KINDS as readonly string[]).includes('scenarioLoadRejected')).toBe(true);
    const r = scenarioLoadRejection(ev)!;
    expect(r.scenarioId).toBe('T_inline');
    expect(r.origin).toBe('inline');
    expect(r.t).toBe(5);
    expect(r.issues).toEqual([
      { path: 'zones[0].polygon', message: '多邊形至少需要 3 點', severity: 'error' },
      { path: 'assessment.hardFail[0]', message: 'zoneEntered 引用不存在的區域:missing', severity: 'error' },
    ]);
    expect(eventLabel(ev)).toContain('情境 T_inline 載入被拒');
    // 無 data 的精簡事件:仍有訊息、空清單;其他事件回 null
    const bare = eventFromFrame({ type: 'event', event: { kind: 'scenarioLoadRejected', tick: 1, t: 0.02, itemId: 'E07' } })!;
    expect(scenarioLoadRejection(bare)).toMatchObject({ scenarioId: 'E07', origin: 'command', issues: [] });
    expect(eventLabel(bare)).toBe('情境 E07 載入被拒');
    expect(scenarioLoadRejection(eventFromFrame({ type: 'event', event: { kind: 'frozen', tick: 1, t: 0.02 } })!)).toBeNull();
  });
});

describe('情境面板 / 靠離泊預演面板的拒絕顯示', () => {
  beforeAll(async () => {
    await import('../src/components/instructor/scenario-panel.js');
    await import('../src/components/instructor/berthing-rehearsal-panel.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('情境面板:rejection 顯示摘要與問題清單、無「已載入」標籤;busy 時載入鈕停用並顯示載入中,解除後回復', async () => {
    const rej = scenarioLoadRejection(eventFromFrame(REJECT_FRAME)!)!;
    const el = await mount<HTMLElement & { rejection: ScenarioLoadRejection | null; busy: boolean; activeId: string }>('brm-scenario-panel', { scenarios: [E01], rejection: rej, busy: true, activeId: '' });
    const t = text(el);
    expect(t).toContain('核心拒絕載入 T_inline(狀態不變,仍為原情境)');
    expect(t).toContain('zones[0].polygon:多邊形至少需要 3 點');
    expect(t).toContain('assessment.hardFail[0]:zoneEntered 引用不存在的區域:missing');
    expect(t).toContain('內嵌情境 · t=5 s');
    expect(el.shadowRoot!.querySelectorAll('.rejected li')).toHaveLength(2);
    expect(el.shadowRoot!.querySelector('.tag.ok')).toBeNull();
    const btn = el.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!;
    expect(btn.disabled).toBe(true);
    expect(btn.textContent).toContain('載入中');
    el.busy = false;
    el.rejection = null;
    await el.updateComplete;
    expect(btn.disabled).toBe(false);
    expect(btn.textContent).toContain('載入 E01_baseline');
    expect(el.shadowRoot!.querySelector('.rejected')).toBeNull();
  });

  it('靠離泊預演面板:rejection 顯示在表單下方,busy 時「組題並載入」停用', async () => {
    const rej: ScenarioLoadRejection = { scenarioId: 'berthing-rehearsal-1', origin: 'ack', message: '內嵌情境載入失敗:berth.file 找不到', issues: [], t: 12 };
    const el = await mount<HTMLElement & { rejection: ScenarioLoadRejection | null; busy: boolean }>('brm-berthing-rehearsal-panel', { rejection: rej, busy: true });
    const t = text(el);
    expect(t).toContain('核心拒絕載入 berthing-rehearsal-1');
    expect(t).toContain('內嵌情境載入失敗:berth.file 找不到');
    expect(t).toContain('Python 參考伺服器 ack');
    expect(el.shadowRoot!.querySelector('.rejected ul')).toBeNull();
    const btn = el.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!;
    expect(btn.disabled).toBe(true);
    expect(btn.textContent).toContain('載入中');
  });
});

describe('教官站(簡化模式、live 來源)收到核心拒絕', () => {
  beforeAll(async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket);
    await import('../src/instructor-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    FakeWebSocket.instances = [];
  });

  async function mountApp(): Promise<{ app: AppInner; ws: FakeWebSocket }> {
    history.replaceState(null, '', '/?mode=simple&ws=ws://test:1/');
    const app = document.createElement('brm-instructor-app') as unknown as AppInner;
    document.body.appendChild(app);
    await app.updateComplete;
    const ws = FakeWebSocket.instances.at(-1)!;
    ws.open();
    return { app, ws };
  }

  async function clickLoad(app: AppInner): Promise<string> {
    const panel = app.shadowRoot!.querySelector('brm-scenario-panel') as Updatable;
    await panel.updateComplete;
    panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await app.updateComplete;
    const entry = [...app.commandLog].reverse().find((e) => e.command.type === 'loadScenario')!;
    return (entry.command.args as { id: string }).id;
  }

  it('scenarioLoadRejected 事件:回到選擇練習、還原情境、載入鈕回復、指令標為核心拒絕並顯示問題清單', async () => {
    const { app, ws } = await mountApp();
    expect(app.simplePhase).toBe('select');
    const id = await clickLoad(app);
    expect(id).toBeTruthy();
    expect(app.simplePhase).toBe('briefing'); // 樂觀切換
    expect(app.activeScenario?.id).toBe(id);
    expect(app.loadPending).toBe(true);
    expect(ws.sent.some((s) => s.includes('"loadScenario"'))).toBe(true);

    ws.push({ ...REJECT_FRAME, event: { ...REJECT_FRAME.event, itemId: id, data: { ...REJECT_FRAME.event.data, scenarioId: id, origin: 'file' } } });
    await app.updateComplete;
    expect(app.simplePhase).toBe('select');
    expect(app.activeScenario).toBeNull(); // 載入前沒有情境 → 不得顯示為已載入
    expect(app.loadPending).toBe(false);
    expect(app.scenarioRejection?.scenarioId).toBe(id);
    expect(app.scenarioRejection?.issues).toHaveLength(2);
    const entry = [...app.commandLog].reverse().find((e) => e.command.type === 'loadScenario')!;
    expect(entry.ack).toMatchObject({ ok: false });
    expect(entry.ack?.detail).toContain('載入被拒');
    const panel = app.shadowRoot!.querySelector('brm-scenario-panel') as Updatable;
    await panel.updateComplete;
    const t = text(panel);
    expect(t).toContain(`核心拒絕載入 ${id}`);
    expect(t).toContain('zones[0].polygon:多邊形至少需要 3 點');
    expect(t).toContain('情境檔');
    expect(panel.shadowRoot!.querySelector('.tag.ok')).toBeNull();
    expect(panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.disabled).toBe(false);

    // 再次載入:拒絕訊息清除;狀態幀帶新情境 id → 視為成功,留在簡報
    const again = await clickLoad(app);
    expect(app.scenarioRejection).toBeNull();
    expect(app.loadPending).toBe(true);
    ws.push({ ...defaultState('FSB1'), t: 0, tick: 0, scenario: { id: again, phase: 'normal' } });
    await app.updateComplete;
    expect(app.loadPending).toBe(false);
    expect(app.simplePhase).toBe('briefing');
    expect(app.activeScenario?.id).toBe(again);
  });

  it('Python 參考伺服器 ack ok=false:同樣視為拒絕(detail 為原因、無問題清單);ack ok 則結束載入中', async () => {
    const { app, ws } = await mountApp();
    const id = await clickLoad(app);
    ws.push({ type: 'ack', command: 'loadScenario', ok: false, detail: '內嵌情境載入失敗:initial 必須為物件', tick: 10 });
    await app.updateComplete;
    expect(app.simplePhase).toBe('select');
    expect(app.activeScenario).toBeNull();
    expect(app.scenarioRejection).toMatchObject({ scenarioId: id, origin: 'ack', message: '內嵌情境載入失敗:initial 必須為物件', issues: [] });
    const panel = app.shadowRoot!.querySelector('brm-scenario-panel') as Updatable;
    await panel.updateComplete;
    expect(text(panel)).toContain('內嵌情境載入失敗:initial 必須為物件');

    await clickLoad(app);
    ws.push({ type: 'ack', command: 'loadScenario', ok: true, detail: '已載入情境', tick: 0 });
    await app.updateComplete;
    expect(app.loadPending).toBe(false);
    expect(app.simplePhase).toBe('briefing');
    expect(app.scenarioRejection).toBeNull();
  });
});
