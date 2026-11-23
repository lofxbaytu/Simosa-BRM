// 碰墊反覆接觸的合併列在各畫面的顯示(第三十輪 ENG-C9;happy-dom):
//  - <brm-event-stream>:合併列一列(次數、時刻、最大值不截斷)、標題與來源篩選鈕上的數字是列數、標題另寫合併前的事件數、篩選與點擊;
//    合併列留在它建立時的位置(依第一次接觸的時刻排、時間欄也是它),之後併入只在原地更新,不移到最上面(第三十輪審查 BC-rBC-5);
//  - 教官站(完整模式與簡化模式):即時事件 → 合併 → 倒帶截斷 → 紀錄匯出(版本 6)→ 講評報告交接,報告頁時間軸顯示同一列;
//  - 重播檢視:核心 JSON Lines(逐筆)讀入後是同樣的列,時間軸上合併列畫成橫條;
//  - 講評報告頁:開啟核心紀錄時時間軸同樣合併;
//  - 載入情境(真實核心的框順序):新情境第一個廣播 tick 的事件比第一筆狀態幀早到,載入成功清空事件流後仍在(E14 開始即繫妥的第一次接觸)。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { EventStream } from '../src/components/instructor/event-stream.js';
import { displayRows, rowTime } from '../src/components/instructor/event-stream.js';
import type { ReplayView } from '../src/components/instructor/replay-view.js';
import { parseReplay } from '../src/lib/record-replay.js';
import type { SessionRecord } from '../src/lib/session-record.js';
import { EventStore } from '../src/lib/sim-event.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { ReportApp } from '../src/report/report-app.js';
import { resetReportStoreForTest } from '../src/report/report-store.js';
import type { SimEvent } from '../src/types/event.js';

type Updatable = { updateComplete: Promise<unknown> };
const DT = 0.02;
let seq = 0;

function contact(t: number, fenderId = 'S03', over: Partial<{ v: number; energy: number; pct: number; overload: boolean }> = {}): SimEvent {
  const overload = over.overload ?? false;
  return {
    kind: 'fenderContact',
    tick: Math.round(t / DT),
    t,
    source: 'engine',
    itemId: fenderId,
    message: `碰墊 ${fenderId} ${overload ? '過載' : '接觸'}`,
    phase: 'normal',
    visible: true,
    seq: seq++,
    data: { fenderId, normalSpeed_mps: over.v ?? 0.03, energy_kNm: over.energy ?? 2, maxReaction_kN: 100, maxDeflection_m: 0.04, deflection_pct: over.pct ?? 5, ratedEnergy_kNm: 282.3, ratedReaction_kN: 640, overload },
  };
}

const alert = (t: number): SimEvent => ({ kind: 'faultInjected', tick: Math.round(t / DT), t, source: 'timeline', itemId: 'engineStartFault', visible: false, seq: seq++, data: { fault: 'engineStartFailure' } });
const vhf = (t: number): SimEvent => ({ kind: 'vhfText', tick: Math.round(t / DT), t, source: 'vhf', itemId: '16', visible: true, seq: -1, data: { station: 'bridge', channel: '16', text: '測試' } });
const state = (t: number) => ({ ...defaultState('FSB1'), t, tick: Math.round(t / DT), pos: { lat: 0, lon: 0, x: t, y: 0 } });
const fenderOf = (e: SimEvent): string | undefined => (e.kind === 'fenderContact' ? (e.data?.['fenderId'] as string | undefined) : undefined);

/** 一個碰墊:第一次接觸 + n 次反覆接觸(每 gap 秒一次,自 start 起) */
function swings(fenderId: string, start: number, n: number, gap = 6): SimEvent[] {
  return Array.from({ length: n + 1 }, (_, i) => contact(start + i * gap, fenderId, i === 3 ? { v: 0.07, energy: 9, pct: 12 } : {}));
}

/** 依時間先後(核心送出的順序)放進 EventStore */
function storeInTimeOrder(events: readonly SimEvent[]): EventStore {
  const store = new EventStore();
  for (const e of [...events].sort((a, b) => a.t - b.t)) store.push(e);
  return store;
}

function rowsOf(root: ParentNode): Array<{ kind: string; fender: string | null; count: string | null; time: string; text: string; merged: boolean }> {
  return Array.from(root.querySelectorAll<HTMLElement>('.list .item')).map((el) => ({
    kind: el.dataset['kind'] ?? '',
    fender: el.getAttribute('data-fender'),
    count: el.getAttribute('data-merged-count'),
    time: el.querySelector('.time')!.textContent!.trim(),
    text: el.querySelector('.text')!.textContent!.trim(),
    merged: el.classList.contains('merged'),
  }));
}

describe('<brm-event-stream> 的合併列', () => {
  beforeAll(async () => {
    await import('../src/components/instructor/event-stream.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
  });

  async function mountStream(events: readonly SimEvent[]): Promise<EventStream & Updatable> {
    const el = document.createElement('brm-event-stream') as EventStream & Updatable;
    el.events = events;
    document.body.appendChild(el);
    await el.updateComplete;
    return el;
  }

  it('一個碰墊的 40 次接觸顯示兩列(第一次、合併列);合併列留在它建立時的位置(時間欄是第一次接觸),之後才發生的事件在它上面;文字寫次數、起訖時刻與最大值', async () => {
    const store = storeInTimeOrder([alert(1), ...swings('S03', 10, 39), vhf(120)]);
    // EventStore 內部:合併列依最近活動(t = 最近一次接觸 244 s)排在最後
    expect(store.all().map((e) => `${e.kind}@${e.t}`)).toEqual(['faultInjected@1', 'fenderContact@10', 'vhfText@120', 'fenderContact@244']);
    const el = await mountStream([...store.all()]);
    const rows = rowsOf(el.shadowRoot!);
    // 畫面(最新在上):VHF(120 s)在合併列(16 s 建立,之後一直併入到 244 s)之上 —— 合併列不因為併入而跳到最上面
    expect(rows.map((r) => `${r.kind}:${r.fender ?? ''}:${r.count ?? ''}`)).toEqual(['vhfText::', 'fenderContact:S03:39', 'fenderContact:S03:', 'faultInjected::']);
    expect(rows[1]).toMatchObject({ merged: true, time: '00:16', text: '碰墊 S03 反覆接觸 ×39(00:16–04:04)最大 v 0.07 m/s、能量 9 kN·m(額定 282)、壓縮 12 %' });
    expect(rows[2]).toMatchObject({ merged: false, time: '00:10', text: '碰墊 S03 接觸 v 0.03 m/s 能量 2 kN·m(額定 282)壓縮 5 %' });
    const item = el.shadowRoot!.querySelector<HTMLElement>('.item.merged')!;
    expect(item.title).toContain('合併 39 筆(00:16–04:04;最近一次');
    expect(item.classList.contains('info')).toBe(true);
    // 文字不截斷:沒有 text-overflow / nowrap 的內嵌樣式,內容就是完整的一句話
    expect(item.querySelector('.text')!.getAttribute('style')).toBeNull();
  });

  it('持續擺盪時新的事件留在最上面:四個碰墊的合併列每併入一次只在原地更新次數,不把故障、教官指令擠下去', async () => {
    // 25 kn 吹攏風的型態:四個碰墊輪流每幾秒碰一次(各自一列進行中的合併列)。修正前合併列每併入一次就移到最上面,
    // 最上面四列永遠是它們(每列約 84 px,清單可見 120 px),新的其他事件幾秒內就被推出可見範圍
    const store = new EventStore();
    const fenders = ['S03', 'S04', 'S05', 'S06'];
    for (const [k, f] of fenders.entries()) store.push(contact(1 + k, f));
    for (let i = 1; i <= 20; i++) for (const [k, f] of fenders.entries()) store.push(contact(1 + k + i * 8, f));
    store.push(alert(170)); // 擺盪中的故障事件
    const el = await mountStream([...store.all()]);
    const top = () => rowsOf(el.shadowRoot!).slice(0, 5).map((r) => `${r.kind}:${r.fender ?? ''}×${r.count ?? 1}@${r.time}`);
    expect(top()).toEqual(['faultInjected:×1@02:50', 'fenderContact:S06×20@00:12', 'fenderContact:S05×20@00:11', 'fenderContact:S04×20@00:10', 'fenderContact:S03×20@00:09']);
    // 之後 60 秒四個碰墊又各碰了 7 次:故障事件仍在最上面,合併列在原地累計
    for (let i = 21; i <= 27; i++) for (const [k, f] of fenders.entries()) store.push(contact(1 + k + i * 8, f));
    el.events = [...store.all()];
    await el.updateComplete;
    expect(top()).toEqual(['faultInjected:×1@02:50', 'fenderContact:S06×27@00:12', 'fenderContact:S05×27@00:11', 'fenderContact:S04×27@00:10', 'fenderContact:S03×27@00:09']);
    expect(rowsOf(el.shadowRoot!)[1]!.text).toContain('反覆接觸 ×27(00:12–03:40)');
    // 新的一段擺盪(隔超過 120 s 再碰回):另起新列,出現在最上面
    store.push(contact(400, 'S03'));
    store.push(contact(406, 'S03'));
    store.push(contact(412, 'S03'));
    el.events = [...store.all()];
    await el.updateComplete;
    expect(top().slice(0, 3)).toEqual(['fenderContact:S03×2@06:46', 'fenderContact:S03×1@06:40', 'faultInjected:×1@02:50']);
  });

  it('displayRows:沒有合併列時就是最新的 max 列(與原本相同);超過 max 時仍在進行中的合併列留在最下面,已經停了的不留', () => {
    const plain = [alert(1), vhf(2), alert(3), vhf(4)];
    expect(displayRows(plain, 300).map((e) => e.t)).toEqual([4, 3, 2, 1]);
    expect(displayRows(plain, 2).map((e) => e.t)).toEqual([4, 3]);
    expect(displayRows([], 300)).toEqual([]);
    // 兩個碰墊各一列合併列(10–20 s 建立);S03 持續到 1000 s、S04 在 100 s 就停了;之後 400 筆其他事件
    const store = new EventStore();
    store.push(contact(5, 'S03'));
    store.push(contact(6, 'S04'));
    for (let t = 10; t <= 100; t += 10) for (const f of ['S03', 'S04']) store.push(contact(t + (f === 'S04' ? 1 : 0), f));
    for (let i = 0; i < 400; i++) {
      store.push(vhf(110 + i * 2));
      if (i % 5 === 0) store.push(contact(110 + i * 2 + 1, 'S03'));
    }
    const rows = displayRows(store.all(), 300);
    expect(rows).toHaveLength(301);
    expect(rows.slice(0, 300).every((e) => e.kind === 'vhfText')).toBe(true);
    expect(rows[0]!.t).toBe(908);
    // 最下面:仍在進行中的 S03 合併列(第一次接觸 10 s,最近一次 901 s);S04 的合併列(最近一次 101 s)不在
    const last = rows[300]!;
    expect([fenderOf(last), last.merged?.count, rowTime(last), last.t]).toEqual(['S03', 90, 10, 901]);
    expect(rows.some((e) => fenderOf(e) === 'S04')).toBe(false);
    // 依列的時刻遞減(最新在上)
    expect(rows.map(rowTime)).toEqual([...rows.map(rowTime)].sort((a, b) => b - a));
  });

  it('標題與各來源鈕上的數字是列數;有合併列時標題另寫合併前的事件數;沒有合併列時不寫', async () => {
    const store = storeInTimeOrder([alert(1), ...swings('S03', 10, 39), ...swings('S04', 11, 20), vhf(130)]);
    const el = await mountStream([...store.all()]);
    expect(el.shadowRoot!.querySelector('.panel-title .count')!.textContent!.trim()).toBe('6 筆(合併前 63)');
    const buttons = Object.fromEntries(Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.filters button')).map((b) => [b.className.match(/src-(\w+)/)![1], b.textContent!.trim()]));
    expect(buttons).toEqual({ traffic: '目標船 0', timeline: '時間表 1', assessment: '評估 0', instructor: '教官 0', engine: '引擎 4', vhf: 'VHF 1' });
    const plain = await mountStream([alert(1), contact(2), vhf(3)]);
    expect(plain.shadowRoot!.querySelector('.panel-title .count')!.textContent!.trim()).toBe('3 筆');
  });

  it('篩選:關掉「引擎」時合併列與單筆接觸一起隱藏,其他來源照常;再點一次恢復;數字不變', async () => {
    const store = storeInTimeOrder([alert(1), ...swings('S03', 10, 39), vhf(120)]);
    const el = await mountStream([...store.all()]);
    const engine = el.shadowRoot!.querySelector<HTMLButtonElement>('.filters button.src-engine')!;
    engine.click();
    await el.updateComplete;
    expect(rowsOf(el.shadowRoot!).map((r) => r.kind)).toEqual(['vhfText', 'faultInjected']);
    expect(el.shadowRoot!.querySelector('.panel-title .count')!.textContent!.trim()).toBe('4 筆(合併前 42)');
    expect(engine.textContent!.trim()).toBe('引擎 2');
    expect(engine.classList.contains('active')).toBe(false);
    for (const src of ['timeline', 'vhf']) el.shadowRoot!.querySelector<HTMLButtonElement>(`.filters button.src-${src}`)!.click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.list .hint')!.textContent).toContain('已全部篩除');
    engine.click();
    await el.updateComplete;
    expect(rowsOf(el.shadowRoot!).map((r) => `${r.kind}:${r.count ?? ''}`)).toEqual(['fenderContact:39', 'fenderContact:']);
  });

  it('點合併列發出 event-seek,帶整列(t = 最近一次接觸、merged 有第一次的時刻)', async () => {
    expect(rowTime(contact(7))).toBe(7);
    const store = new EventStore();
    for (const e of swings('S03', 10, 5)) store.push(e);
    const el = await mountStream([...store.all()]);
    const got: SimEvent[] = [];
    el.addEventListener('event-seek', (ev) => got.push((ev as CustomEvent<SimEvent>).detail));
    el.shadowRoot!.querySelector<HTMLElement>('.item.merged')!.click();
    expect(got).toHaveLength(1);
    expect(got[0]!.t).toBe(40);
    expect(got[0]!.merged).toMatchObject({ count: 5, firstT: 16, lastT: 40 });
  });
});

type App = HTMLElement & Updatable;
type Inner = {
  onSimEvent(ev: SimEvent): void;
  onState(state: unknown, issues: string[]): void;
  stopSource(): void;
  restartExercise(): void;
  sessionRecord(): SessionRecord;
  openReport(): Promise<void>;
  events: readonly SimEvent[];
  simplePhase: 'select' | 'briefing' | 'running' | 'ended';
};

async function mountInstructor(query = '/?demo=1'): Promise<[App, Inner]> {
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

async function mountReport(query: string): Promise<ReportApp & Updatable> {
  history.replaceState(null, '', query);
  const el = document.createElement('brm-report-app') as ReportApp & Updatable;
  document.body.appendChild(el);
  for (let i = 0; i < 400 && !el.ready; i++) await new Promise((r) => setTimeout(r, 5));
  expect(el.ready).toBe(true);
  await el.updateComplete;
  return el;
}

function timelineRows(app: ReportApp): Array<{ kind: string; count: string | null; t: string; label: string }> {
  return Array.from(app.querySelectorAll<HTMLElement>('.sheet[data-doc="report"] table.timeline tbody tr')).map((tr) => ({
    kind: tr.dataset['kind'] ?? '',
    count: tr.getAttribute('data-count'),
    t: tr.children[0]!.textContent!.trim(),
    label: tr.children[2]!.textContent!.trim(),
  }));
}

describe('教官站:即時合併、倒帶、紀錄匯出與講評報告交接', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
    await import('../src/report/report-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    vi.restoreAllMocks();
    resetReportStoreForTest();
  });

  it('完整模式:2400 次擺盪接觸(4 個碰墊)只佔 8 列,先前的故障事件沒有被 2000 列的上限擠掉;事件流畫面只有 4 列合併列', async () => {
    const [app, inner] = await mountInstructor();
    inner.onState(state(0), []);
    inner.onSimEvent(alert(1));
    const fenders = ['S03', 'S04', 'S05', 'S06'];
    for (let i = 0; i <= 600; i++) for (const [k, f] of fenders.entries()) inner.onSimEvent(contact(2 + i * 12 + k, f));
    expect(inner.events).toHaveLength(9);
    expect(inner.events.some((e) => e.kind === 'faultInjected')).toBe(true);
    await app.updateComplete;
    const stream = app.shadowRoot!.querySelector('brm-event-stream') as EventStream & Updatable;
    await stream.updateComplete;
    const rows = rowsOf(stream.shadowRoot!);
    expect(rows.filter((r) => r.merged).map((r) => `${r.fender}×${r.count}`)).toEqual(['S06×600', 'S05×600', 'S04×600', 'S03×600']);
    expect(rows.filter((r) => r.kind === 'fenderContact' && !r.merged)).toHaveLength(4);
    expect(stream.shadowRoot!.querySelector('.panel-title .count')!.textContent!.trim()).toBe('9 筆(合併前 2405)');
  });

  it('倒帶(restore):合併列截到倒帶點、畫面跟著更新;紀錄的 rewinds 記丟棄的事件數;匯出的紀錄(版本 6)讀回同樣的列', async () => {
    const [app, inner] = await mountInstructor();
    inner.onState(state(0), []);
    inner.onSimEvent(alert(1));
    for (const e of swings('S03', 10, 30, 10)) inner.onSimEvent(e); // 10、20 … 310
    inner.onState(state(320), []);
    await app.updateComplete;
    const stream = app.shadowRoot!.querySelector('brm-event-stream') as EventStream & Updatable;
    await stream.updateComplete;
    expect(rowsOf(stream.shadowRoot!)[0]).toMatchObject({ count: '30', time: '00:20', text: expect.stringContaining('×30(00:20–05:10)') as unknown as string });

    app.dispatchEvent(new CustomEvent('sim-command', { detail: { type: 'restore', args: { tick: 5000 } }, bubbles: true, composed: true })); // t = 100
    inner.onState(state(100.04), []);
    await app.updateComplete;
    await stream.updateComplete;
    expect(inner.events.map((e) => [e.kind, e.t, e.merged?.count])).toEqual([
      ['faultInjected', 1, undefined],
      ['fenderContact', 10, undefined],
      ['fenderContact', 100, 9],
    ]);
    expect(rowsOf(stream.shadowRoot!)[0]).toMatchObject({ count: '9', time: '00:20', text: '碰墊 S03 反覆接觸 ×9(00:20–01:40)最大 v 0.07 m/s、能量 9 kN·m(額定 282)、壓縮 12 %' });
    // 新分支的接觸接著併入
    inner.onSimEvent(contact(105, 'S03'));
    inner.onState(state(110), []);
    const record = inner.sessionRecord();
    expect(record.version).toBe(6);
    expect(record.rewinds).toEqual([{ t: 100, tick: 5000, from: 320, droppedStates: expect.any(Number) as number, droppedEvents: 21 }]);
    expect(record.events!.map((e) => e.merged?.count ?? 1)).toEqual([1, 1, 10]);
    const back = parseReplay(JSON.stringify(record));
    expect(back.events).toEqual(inner.events);
    expect(back.rewinds).toHaveLength(1);
  });

  it('新練習(重設)清掉碰墊的合併狀態:下一筆接觸是新的第一次接觸,不併入上一個練習的列', async () => {
    const [app, inner] = await mountInstructor();
    inner.onState(state(0), []);
    for (const e of swings('S03', 10, 5)) inner.onSimEvent(e);
    expect(inner.events.map((e) => e.merged?.count ?? 1)).toEqual([1, 5]);
    app.dispatchEvent(new CustomEvent('sim-command', { detail: { type: 'reset' }, bubbles: true, composed: true }));
    expect(inner.events).toHaveLength(0);
    inner.onSimEvent(contact(41, 'S03'));
    inner.onSimEvent(contact(45, 'S03'));
    expect(inner.events.map((e) => [e.t, e.merged?.count ?? 1])).toEqual([
      [41, 1],
      [45, 1],
    ]);
  });

  it('簡化模式:同一套合併(畫面不列事件流);「講評報告」交接後報告頁的事件時間軸顯示同一列(排在第一次接觸的時刻)', async () => {
    const [, inner] = await mountInstructor('/?mode=simple&demo=1');
    inner.simplePhase = 'running';
    for (let t = 0; t <= 400; t += 1) inner.onState(state(t), []);
    inner.onSimEvent(alert(1));
    for (const e of swings('S03', 10, 30, 10)) inner.onSimEvent(e);
    inner.onSimEvent(contact(330, 'S03', { overload: true, energy: 300 }));
    expect(inner.events.map((e) => e.merged?.count ?? 1)).toEqual([1, 1, 30, 1]);
    const record = inner.sessionRecord();
    expect(record.mode).toBe('simple');
    expect(record.events!.find((e) => e.merged)!.merged).toEqual({ count: 30, firstT: 20, firstTick: 1000, lastT: 310, maxNormalSpeed_mps: 0.07, maxEnergy_kNm: 9, maxDeflection_pct: 12, maxReaction_kN: 100 });

    vi.spyOn(window, 'open').mockImplementation(() => null);
    await inner.openReport();
    document.body.innerHTML = '';
    const report = await mountReport('/report.html?from=instructor');
    expect(report.querySelector('.msg')!.textContent).toContain('事件 4 筆(合併前 33)');
    expect(timelineRows(report)).toEqual([
      { kind: 'faultInjected', count: null, t: '00:00:01', label: '故障注入 engineStartFailure' },
      { kind: 'fenderContact', count: null, t: '00:00:10', label: '碰墊 S03 接觸 v 0.03 m/s 能量 2 kN·m(額定 282)壓縮 5 %' },
      // 文字裡的起訖時刻與時刻欄同樣是 HH:MM:SS(事件流畫面上是 MM:SS;紙本上「00:20」容易被讀成時:分)
      { kind: 'fenderContact', count: '30', t: '00:00:20', label: '碰墊 S03 反覆接觸 ×30(00:00:20–00:05:10)最大 v 0.07 m/s、能量 9 kN·m(額定 282)、壓縮 12 %' },
      { kind: 'fenderContact', count: null, t: '00:05:30', label: '碰墊 S03 接觸 v 0.03 m/s 能量 300 kN·m(額定 282)壓縮 5 % — 過載' },
    ]);
  });
});

describe('重播檢視與講評報告頁讀核心 JSON Lines(逐筆)', () => {
  const events = [alert(1), ...swings('S03', 10, 30, 10), contact(330, 'S03', { overload: true, energy: 300 })];
  const text = [
    JSON.stringify({ kind: 'header', version: 2, shipId: 'FSB1', dt: DT, scenario: { id: 'E14_unberthing_no_tug' } }),
    JSON.stringify({ kind: 'state', state: state(0) }),
    ...events.map((e) => JSON.stringify({ kind: 'event', tick: e.tick, event: e })),
    JSON.stringify({ kind: 'state', state: state(200) }),
    JSON.stringify({ kind: 'state', state: state(400) }),
  ].join('\n');

  beforeAll(async () => {
    await import('../src/components/instructor/replay-view.js');
    await import('../src/report/report-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    resetReportStoreForTest();
  });

  it('重播檢視:事件流是合併後的列;時間軸上合併列畫成第一次到最近一次的橫條,點它跳到第一次接觸;資訊列寫合併前的事件數', async () => {
    const el = document.createElement('brm-replay-view') as ReplayView & Updatable;
    document.body.appendChild(el);
    await el.updateComplete;
    const data = el.loadText(text, 'e14.jsonl');
    expect(data.events.map((e) => e.merged?.count ?? 1)).toEqual([1, 1, 30, 1]);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.toolbar .info')!.textContent).toContain('4 筆事件(合併前 33)');
    const stream = el.shadowRoot!.querySelector('brm-event-stream') as EventStream & Updatable;
    await stream.updateComplete;
    expect(rowsOf(stream.shadowRoot!).map((r) => `${r.time} ${r.count ?? '-'}`)).toEqual(['05:30 -', '00:20 30', '00:10 -', '00:01 -']);
    const marks = Array.from(el.shadowRoot!.querySelectorAll<HTMLElement>('.marks .mark')).filter((m) => !m.classList.contains('phase'));
    expect(marks).toHaveLength(4);
    const bar = el.shadowRoot!.querySelector<HTMLElement>('.marks .mark.merged')!;
    expect(bar.getAttribute('style')).toBe('left:5%;width:max(2px, calc(77.5% - 5%))');
    expect(bar.title).toContain('反覆接觸 ×30(00:20–05:10)');
    bar.click();
    await el.updateComplete;
    expect(el.time).toBe(20);
    // 事件流裡點合併列:跳到它時間欄寫的時刻(第一次接觸,與橫條相同);點一般的列跳到事件的時刻
    stream.shadowRoot!.querySelector<HTMLElement>('.item[data-kind="faultInjected"]')!.click();
    await el.updateComplete;
    expect(el.time).toBe(1);
    stream.shadowRoot!.querySelector<HTMLElement>('.item.merged')!.click();
    await el.updateComplete;
    expect(el.time).toBe(20);
  });

  it('講評報告頁:開啟核心紀錄時事件時間軸同樣合併(列在第一次接觸的時刻,帶次數),過載照舊一列', async () => {
    const app = await mountReport('/report.html');
    app.loadRecordText(text, 'e14.jsonl');
    await app.updateComplete;
    expect(app.querySelector('.msg')!.textContent).toContain('事件 4 筆(合併前 33)');
    const rows = timelineRows(app);
    expect(rows.map((r) => `${r.t} ${r.kind} ${r.count ?? '-'}`)).toEqual(['00:00:01 faultInjected -', '00:00:10 fenderContact -', '00:00:20 fenderContact 30', '00:05:30 fenderContact -']);
    expect(rows[2]!.label).toBe('碰墊 S03 反覆接觸 ×30(00:00:20–00:05:10)最大 v 0.07 m/s、能量 9 kN·m(額定 282)、壓縮 12 %');
    expect(app.querySelector('.sheet[data-doc="report"] table.timeline tbody tr.sev-alarm')!.textContent).toContain('過載');
  });
});

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
  open(): void {
    this.readyState = 1;
    this.onopen?.();
  }
  push(frame: unknown): void {
    this.onmessage?.({ data: JSON.stringify(frame) });
  }
}

describe('教官站(live 來源)載入情境:新情境在第一筆狀態幀之前送到的事件', () => {
  type LiveApp = App & { events: readonly SimEvent[]; loadPending: boolean; commandLog: ReadonlyArray<{ command: { type: string; args?: Record<string, unknown> } }> };

  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    FakeWebSocket.instances = [];
    vi.unstubAllGlobals();
  });

  async function mountLive(): Promise<{ app: LiveApp; ws: FakeWebSocket }> {
    vi.stubGlobal('WebSocket', FakeWebSocket);
    history.replaceState(null, '', '/?ws=ws://test:1/&vhf=0');
    const app = document.createElement('brm-instructor-app') as unknown as LiveApp;
    document.body.appendChild(app);
    await app.updateComplete;
    const ws = FakeWebSocket.instances.at(-1)!;
    ws.open();
    return { app, ws };
  }

  async function clickLoad(app: LiveApp): Promise<string> {
    const panel = app.shadowRoot!.querySelector('brm-scenario-panel') as HTMLElement & Updatable;
    await panel.updateComplete;
    panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await app.updateComplete;
    const entry = [...app.commandLog].reverse().find((e) => e.command.type === 'loadScenario')!;
    return (entry.command.args as { id: string }).id;
  }

  const frame = (e: SimEvent) => ({ type: 'event', event: e });
  const stateFrame = (t: number, id: string) => ({ ...defaultState('FSB1'), t, tick: Math.round(t / DT), scenario: { id, phase: 'normal' } });

  it('核心的框順序(原情境的事件 → 新情境 tick 2 的事件 → 新情境第一筆狀態幀):清空後只留新情境的事件,之後的反覆接觸照常合併', async () => {
    const { app, ws } = await mountLive();
    for (let i = 1; i <= 5; i++) ws.push(stateFrame(i, 'E07_steering_failure'));
    ws.push(frame(alert(5.1)));
    expect(app.events).toHaveLength(1);
    const id = await clickLoad(app);
    expect(app.loadPending).toBe(true);
    ws.push(frame({ ...alert(5.12), itemId: 'old' })); // 原情境在載入生效前的最後一個事件(tick 256)
    for (const f of ['S03', 'S04', 'S05', 'S06']) ws.push(frame(contact(0.04, f)));
    ws.push(stateFrame(0.04, id));
    await app.updateComplete;
    expect(app.loadPending).toBe(false);
    expect(app.events.map((e) => `${e.kind}:${e.itemId}@${e.t}`)).toEqual(['fenderContact:S03@0.04', 'fenderContact:S04@0.04', 'fenderContact:S05@0.04', 'fenderContact:S06@0.04']);
    for (const t of [16.92, 38.76, 49.82]) ws.push(frame(contact(t, 'S03')));
    expect(app.events.map((e) => `${e.itemId}×${e.merged?.count ?? 1}`)).toEqual(['S03×1', 'S04×1', 'S05×1', 'S06×1', 'S03×3']);
  });

  it('原情境才剛開始(送出載入指令時 tick 不大於新情境第一筆狀態幀的 tick):分不出事件屬於哪一個情境,照舊全部清掉', async () => {
    const { app, ws } = await mountLive();
    ws.push(stateFrame(0.02, 'E07_steering_failure'));
    const id = await clickLoad(app);
    ws.push(frame(contact(0.04, 'S03')));
    ws.push(stateFrame(0.04, id));
    await app.updateComplete;
    expect(app.loadPending).toBe(false);
    expect(app.events).toHaveLength(0);
  });
});
