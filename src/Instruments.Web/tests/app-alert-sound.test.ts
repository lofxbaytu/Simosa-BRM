// @vitest-environment happy-dom
// 操船台(brm-app live 模式)與核心的連線狀態對警報聲響的影響:斷線/重連中或資料逾時時停止警報聲響(最後一筆狀態已不可信,
// 且靜音指令送不出去);收到新狀態後依 state.alerts 恢復。另測頁首「警報未確認 N」徽章顏色取需確認者的最高優先序。
// 以假 WebSocket 與假 AudioContext 驅動(happy-dom 無 Web Audio)。
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { defaultState } from '../src/lib/state-parser.js';

class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  onopen: (() => void) | null = null;
  onclose: ((ev: { reason: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }
  send(): void {}
  close(): void {
    this.readyState = 3;
  }
}

class FakeCtx {
  currentTime = 0;
  state = 'running';
  destination = {};
  resume() {
    return Promise.resolve();
  }
  close() {
    return Promise.resolve();
  }
  createOscillator() {
    return { type: '', frequency: { value: 0 }, connect: () => undefined, start: () => undefined, stop: () => undefined };
  }
  createGain() {
    return { gain: { value: 0, setValueAtTime: () => undefined, linearRampToValueAtTime: () => undefined }, connect: () => undefined };
  }
}

type App = HTMLElement & { updateComplete: Promise<unknown> };
type Inner = { sounder: { sounding: boolean; beepsScheduled: number; pattern: string | null }; status: string; stale: boolean };

const GYRO_ALARM = { id: 'gyroFailure', number: 1, priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', text: '電羅經失效', raisedAt_s: 480.02, escalated: false, count: 1, audible: true };

function frame(t: number, alerts: unknown[]) {
  return {
    ...defaultState('FSB1'),
    t,
    tick: Math.round(t / 0.02),
    sensors: { headingValid: false, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 30 },
    bnwas: { mode: 'off', active: false, stage: 0, dormant_s: 720 },
    alerts,
  };
}

beforeAll(() => {
  vi.stubGlobal('WebSocket', FakeWebSocket);
  vi.stubGlobal('AudioContext', FakeCtx);
});

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date', 'performance'] });
  try {
    localStorage.clear();
  } catch {
    /* 忽略 */
  }
  history.replaceState(null, '', '/');
});

afterEach(() => {
  document.body.innerHTML = '';
  vi.useRealTimers();
});

async function mountLive(): Promise<{ app: App; inner: Inner; ws: FakeWebSocket }> {
  await import('../src/app.js');
  const app = document.createElement('brm-app') as App;
  document.body.appendChild(app);
  await app.updateComplete;
  const ws = FakeWebSocket.instances.at(-1)!;
  ws.readyState = 1;
  ws.onopen?.();
  return { app, inner: app as unknown as Inner, ws };
}

describe('操船台:連線中斷/資料逾時時警報聲響', () => {
  it('斷線後停止聲響(不再排嗶聲);重連並收到新狀態後恢復', async () => {
    const { inner, ws } = await mountLive();
    ws.onmessage?.({ data: JSON.stringify(frame(480.1, [GYRO_ALARM])) });
    window.dispatchEvent(new Event('pointerdown')); // 使用者互動解鎖聲響
    expect(inner.sounder.sounding).toBe(true);
    expect(inner.status).toBe('connected');

    ws.readyState = 3;
    ws.onclose?.({ reason: '' });
    expect(inner.status).not.toBe('connected');
    expect(inner.sounder.sounding).toBe(false);
    const before = inner.sounder.beepsScheduled;
    vi.advanceTimersByTime(60_000);
    expect(inner.sounder.beepsScheduled - before).toBe(0);

    // WsClient 重連:新的 WebSocket 開啟並收到仍有 alarm 的狀態 → 恢復聲響
    const ws2 = FakeWebSocket.instances.at(-1)!;
    expect(ws2).not.toBe(ws);
    ws2.readyState = 1;
    ws2.onopen?.();
    ws2.onmessage?.({ data: JSON.stringify(frame(540.1, [GYRO_ALARM])) });
    expect(inner.sounder.sounding).toBe(true);
  });

  it('連線未斷但資料逾時:停止聲響;資料恢復後再響', async () => {
    const { inner, ws } = await mountLive();
    ws.onmessage?.({ data: JSON.stringify(frame(480.1, [GYRO_ALARM])) });
    window.dispatchEvent(new Event('pointerdown'));
    expect(inner.sounder.sounding).toBe(true);
    vi.advanceTimersByTime(5_000);
    expect(inner.stale).toBe(true);
    expect(inner.sounder.sounding).toBe(false);
    ws.onmessage?.({ data: JSON.stringify(frame(485.1, [GYRO_ALARM])) });
    expect(inner.sounder.sounding).toBe(true);
  });
});

describe('操船台頁首「警報未確認 N」徽章', () => {
  it('已確認的 alarm 不影響徽章顏色:需確認者只剩 warning → warning 色', async () => {
    const { app, ws } = await mountLive();
    const acked = { ...GYRO_ALARM, id: 'mainEngine', number: 9, source: 'engine', text: '主機故障', state: 'activeAcknowledged', audible: false };
    const warn = { ...GYRO_ALARM, id: 'echoSounderFailure', number: 3, priority: 'warning', source: 'echoSounder', text: '測深儀失效' };
    ws.onmessage?.({ data: JSON.stringify(frame(100, [acked, warn])) });
    await app.updateComplete;
    const badge = app.shadowRoot!.querySelector('.alert-badge')!;
    expect(badge.textContent).toContain('警報未確認 1');
    expect(badge.classList.contains('warning')).toBe(true);
  });
});
