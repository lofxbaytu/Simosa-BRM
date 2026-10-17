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
