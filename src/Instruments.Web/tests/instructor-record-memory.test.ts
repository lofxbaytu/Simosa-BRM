// 教官站練習紀錄的記憶體上限(第三十輪 W1–W3;happy-dom):
//  W1 restore 倒帶後,狀態抽樣截掉倒帶點(restore tick × dt)之後的舊分支,倒帶點記入紀錄 rewinds,紀錄讀回時 t 單調、倒帶次數不變;
//  W2「下載紀錄 JSON」的 Blob 與 JSON.stringify(紀錄)(修正前的下載內容)位元組相同;「講評報告」交接存 Blob;
//  W3 完整模式重設/載入情境時清學員操作紀錄(修正前只 restart、跨練習累積),只保留本練習情境的「規劃時間結束」。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import type { OperationEvent, OperationLogDeriver } from '../src/lib/operation-log.js';
import type { ScenarioSummary } from '../src/lib/scenario.js';
import type { SessionRecord, StateSampler } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import { HANDOFF_KEY, openReportStore, resetReportStoreForTest } from '../src/report/report-store.js';

type App = HTMLElement & { updateComplete: Promise<unknown> };
type Inner = {
  onState(state: unknown, issues: string[]): void;
  stopSource(): void;
  restartExercise(): void;
  sessionRecord(): SessionRecord;
  downloadRecord(): void;
  openReport(): Promise<void>;
  sampler: StateSampler;
  opLog: OperationLogDeriver;
  opEvents: readonly OperationEvent[];
  scenarios: ScenarioSummary[];
  activeScenario: ScenarioSummary | null;
  simplePhase: 'select' | 'briefing' | 'running' | 'ended';
};

async function mountQuiet(query = '/?demo=1'): Promise<[App, Inner]> {
  history.replaceState(null, '', query); // 示範模式:不開 WebSocket
  const app = document.createElement('brm-instructor-app') as App;
  document.body.appendChild(app);
  await app.updateComplete;
  const inner = app as unknown as Inner;
  inner.stopSource();
  inner.restartExercise();
  await app.updateComplete;
  return [app, inner];
}

const state = (t: number, rudderOrder = 0) => ({ ...defaultState('FSB1'), t, tick: Math.round(t / 0.02), pos: { lat: 0, lon: 0, x: t * 3, y: t }, rudderOrder, rudder: rudderOrder });
const command = (app: App, detail: unknown) => app.dispatchEvent(new CustomEvent('sim-command', { detail, bubbles: true, composed: true }));

describe('教官站紀錄(第三十輪 W1–W3)', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    vi.useRealTimers();
    vi.restoreAllMocks();
    resetReportStoreForTest();
  });

  it('W1:restore 倒帶後抽樣截掉倒帶點之後的舊分支;紀錄 rewinds 記倒帶點,讀回 t 單調、倒帶次數 1', async () => {
    const [app, inner] = await mountQuiet();
    for (let t = 0; t <= 200; t += 0.5) inner.onState(state(t), []);
    expect(inner.sampler.all()).toHaveLength(201);
    command(app, { type: 'restore', args: { tick: 5000 } });
    inner.onState(state(100.04), []); // 核心倒帶後第一筆廣播(tick 5002)
    for (let t = 100.5; t <= 150; t += 0.5) inner.onState(state(t), []);
    const ts = inner.sampler.all().map((s) => s.t);
    expect(ts.every((t, i) => i === 0 || t > ts[i - 1]!)).toBe(true);
    expect(ts.slice(99, 103)).toEqual([99, 100, 100.04, 101.5]); // 倒帶點(t = 100)以前保留,倒帶後第一筆一律抽樣
    expect(inner.sampler.all().length).toBe(101 + 1 + 49);
    const record = inner.sessionRecord();
    expect(record.rewinds).toEqual([{ t: 100, tick: 5000, from: 200, droppedStates: 100, droppedEvents: 0 }]);
    const r = parseReplay(JSON.stringify(record));
    expect(r.rewinds).toHaveLength(1);
    expect(r.rewinds[0]).toMatchObject({ t: 100, tick: 5000, from: 200, droppedStates: 100 });
    expect(r.states.map((s) => s.t)).toEqual(ts);
  });

  it('W1:簡化模式「結束練習」後(不再抽樣)核心 tick 倒退(核心重啟/他站重設),已結束練習的抽樣不截、不記倒帶(審查 WR1)', async () => {
    const [, inner] = await mountQuiet('/?mode=simple&demo=1');
    inner.simplePhase = 'running';
    for (let t = 0; t <= 600; t += 0.5) inner.onState(state(t), []);
    expect(inner.sampler.all()).toHaveLength(601);
    inner.simplePhase = 'ended';
    inner.onState(state(600.5), []);
    inner.onState(state(0.04), []); // 核心重啟後 WsClient 重連的第一筆(tick 2)
    for (let t = 0.5; t < 30; t += 0.5) inner.onState(state(t), []);
    // 修正前(HEAD)抽樣不受倒帶影響:601 筆;W1 第一版在這裡截成 1 筆並多記一筆 rewinds
    expect(inner.sampler.all()).toHaveLength(601);
    expect(inner.sampler.all().at(-1)!.t).toBe(600);
    const record = inner.sessionRecord();
    expect(record.states).toHaveLength(601);
    expect(record.rewinds).toEqual([]);
    // 「新練習」→ 選情境 → 「開始練習」都經 restartExercise 清抽樣,之後倒帶照常截斷
    inner.restartExercise();
    inner.simplePhase = 'running';
    for (let t = 0; t <= 20; t += 0.5) inner.onState(state(t), []);
    inner.onState(state(5.04), []);
    expect(inner.sampler.all().map((s) => s.t)).toEqual([0, 1, 2, 3, 4, 5, 5.04]);
    expect(inner.sampler.rewinds()).toEqual([{ t: 5.04, tick: 252, from: 20, droppedStates: 15, droppedEvents: 0 }]);
  });

  it('W2:下載紀錄 JSON 的 Blob 與 JSON.stringify(紀錄) 位元組相同;講評報告交接存 Blob(內容相同)', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(Date.UTC(2026, 10, 22, 8, 0, 0)));
    const [app, inner] = await mountQuiet();
    for (let t = 0; t <= 30; t += 0.25) inner.onState(state(t, t > 10 ? 15 : 0), []);
    app.dispatchEvent(new CustomEvent('brm-mark', { detail: { category: 'communication', note: '未複誦「右舵 15」' } }));
    let captured: Blob | null = null;
    vi.spyOn(URL, 'createObjectURL').mockImplementation((b: Blob | MediaSource) => {
      captured = b as Blob;
      return 'blob:test';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    inner.downloadRecord();
    expect(captured).not.toBeNull();
    const expected = new TextEncoder().encode(JSON.stringify(inner.sessionRecord()));
    const got = new Uint8Array(await captured!.arrayBuffer());
    expect(got.length).toBe(expected.length);
    expect(Buffer.compare(Buffer.from(got), Buffer.from(expected))).toBe(0);
    expect(captured!.type).toBe('application/json;charset=utf-8');

    const opened = vi.spyOn(window, 'open').mockImplementation(() => null);
    await inner.openReport();
    expect(opened).toHaveBeenCalledWith('./report.html?from=instructor', '_blank');
    const raw = await (await openReportStore()).get<{ blob?: Blob; text?: string }>(HANDOFF_KEY);
    expect(raw?.text).toBeUndefined();
    expect(raw?.blob).toBeDefined();
  });

  it('W3:完整模式重設/載入情境清學員操作紀錄(同步面板),只保留本練習情境的「規劃時間結束」;紀錄 operations 不混入前一個練習', async () => {
    const [app, inner] = await mountQuiet();
    const [a, b] = inner.scenarios;
    expect(a && b).toBeTruthy();
    /** 送 loadScenario,並以帶新情境 id 的狀態幀確認載入成功(核心換好情境後的第一筆廣播) */
    const load = async (sc: ScenarioSummary) => {
      app.dispatchEvent(new CustomEvent('scenario-load', { detail: sc }));
      inner.stopSource(); // 示範模式載入會重啟示範來源;改由測試餵狀態
      inner.onState({ ...state(0), scenario: { id: sc.id, phase: 'normal' } }, []);
      await app.updateComplete;
      expect(inner.activeScenario?.id).toBe(sc.id);
    };
    const planning = (scenarioId?: string) => app.dispatchEvent(new CustomEvent('planning-time-ended', { detail: { planningTimeMin: 5, ...(scenarioId ? { scenarioId } : {}) }, bubbles: true, composed: true }));
    const kinds = () => inner.opLog.all().map((e) => e.kind);
    await load(a!);
    planning(a!.id);
    for (let i = 1; i <= 40; i++) inner.onState(state(i * 2, i % 2 === 0 ? 10 : -10), []);
    expect(inner.opLog.all().filter((e) => e.kind === 'rudder').length).toBeGreaterThan(30);
    command(app, { type: 'reset' });
    await app.updateComplete;
    expect(kinds()).toEqual(['planningTime']); // 修正前:舵令仍在(完整模式只 restart)
    expect(inner.opEvents.map((e) => e.kind)).toEqual(['planningTime']);
    const panel = app.shadowRoot!.querySelector('brm-operation-log') as HTMLElement & { events: readonly OperationEvent[] };
    expect(panel.events.map((e) => e.kind)).toEqual(['planningTime']);
    // 在情境面板選了下一個情境 b 並跑完它的規劃時間,再載入 b:a 的規劃時間紀錄不帶到新練習,b 的保留
    for (let i = 0; i < 10; i++) inner.onState(state(i * 2, i % 2 === 0 ? 10 : -10), []);
    planning(b!.id);
    const seqB = inner.opLog.all().at(-1)!.seq;
    await load(b!);
    expect(inner.opLog.all().map((e) => [e.kind, e.seq])).toEqual([['planningTime', seqB]]);
    // 未帶 scenarioId 的事件歸目前情境
    planning();
    inner.onState(state(2, 20), []);
    command(app, { type: 'reset' });
    expect(kinds()).toEqual(['planningTime', 'planningTime']);
    expect(inner.sessionRecord().operations.map((e) => e.kind)).toEqual(['planningTime', 'planningTime']);
    await load(a!);
    expect(kinds()).toEqual([]);
  });

  it('W3:完整模式尚未載入任何情境時,由情境面板的簡報區塊跑完規劃時間,再載入該情境:「規劃時間結束」保留(審查 WR2,端對端)', async () => {
    const [app, inner] = await mountQuiet();
    expect(inner.activeScenario).toBeNull();
    // 取最後一個有規劃時間的情境(不是清單第一個:面板要先點選才換到它)
    const idx = inner.scenarios.map((s) => (s.briefingDoc.planningTimeMin ?? 0) > 0).lastIndexOf(true);
    expect(idx).toBeGreaterThan(0);
    const sc = inner.scenarios[idx]!;
    const panel = app.shadowRoot!.querySelector('brm-scenario-panel') as HTMLElement & { updateComplete: Promise<unknown> };
    (panel.shadowRoot!.querySelectorAll('.item')[idx] as HTMLElement).click();
    await panel.updateComplete;
    type Block = HTMLElement & { updateComplete: Promise<unknown>; now: () => number; tick(): void };
    const block = panel.shadowRoot!.querySelector('brm-briefing-block') as Block;
    await block.updateComplete;
    let clock = 1_000_000;
    block.now = () => clock;
    (block.shadowRoot!.querySelector('button.start') as HTMLButtonElement).click();
    clock += (sc.briefingDoc.planningTimeMin ?? 0) * 60_000;
    block.tick();
    expect(inner.opLog.all().map((e) => e.kind)).toEqual(['planningTime']);
    app.dispatchEvent(new CustomEvent('scenario-load', { detail: sc }));
    inner.stopSource();
    inner.onState({ ...state(0), scenario: { id: sc.id, phase: 'normal' } }, []);
    await app.updateComplete;
    expect(inner.activeScenario?.id).toBe(sc.id);
    // 面板沒把 .scenarioId 傳給簡報區塊時,這筆歸「尚未載入情境」(''),載入後就被清掉
    expect(inner.opLog.all().map((e) => e.kind)).toEqual(['planningTime']);
    expect(inner.sessionRecord().operations.map((e) => e.kind)).toEqual(['planningTime']);
  });
});
