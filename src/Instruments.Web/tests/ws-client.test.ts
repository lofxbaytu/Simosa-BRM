// WebSocket 客戶端:以假的 WebSocket 測試連線狀態、訊息解析、斷線重連(指數退避)、離線指令補送與停止。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WsClient } from '../src/net/ws-client.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { ConnectionStatus } from '../src/net/sim-source.js';

class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  readyState = FakeWebSocket.CONNECTING;
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
    this.readyState = FakeWebSocket.CLOSED;
  }
  // 測試輔助
  open(): void {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.();
  }
  message(data: unknown): void {
    this.onmessage?.({ data });
  }
  drop(reason = ''): void {
    this.readyState = FakeWebSocket.CLOSED;
    this.onclose?.({ reason });
  }
}

function makeClient() {
  const client = new WsClient({
    url: 'ws://test:1',
    reconnectMinMs: 1000,
    reconnectMaxMs: 5000,
    webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket,
  });
  const statuses: ConnectionStatus[] = [];
  client.onStatus((s) => statuses.push(s));
  return { client, statuses };
}

beforeEach(() => {
  vi.useFakeTimers();
  FakeWebSocket.instances = [];
  // ws-client 以全域 WebSocket.OPEN 判斷是否可送出
  vi.stubGlobal('WebSocket', FakeWebSocket);
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('WsClient', () => {
  it('連線成功後狀態為 connected,收到的狀態 JSON 會解析並廣播', () => {
    const { client, statuses } = makeClient();
    const received: number[] = [];
    client.onState(({ state, issues }) => {
      received.push(state.heading);
      expect(issues).toEqual([]);
    });
    client.start();
    expect(statuses.at(-1)).toBe('connecting');
    const ws = FakeWebSocket.instances[0]!;
    expect(ws.url).toBe('ws://test:1');
    ws.open();
    expect(client.status).toBe('connected');
    ws.message(JSON.stringify({ ...defaultState('FSB1'), heading: 123 }));
    ws.message('not json');
    ws.message(JSON.stringify({ type: 'ack' }));
    expect(received).toEqual([123]);
    client.stop();
  });

  it('缺欄位時沿用前一筆狀態', () => {
    const { client } = makeClient();
    const received: Array<{ sog: number; issues: string[] }> = [];
    client.onState(({ state, issues }) => received.push({ sog: state.sog, issues }));
    client.start();
    const ws = FakeWebSocket.instances[0]!;
    ws.open();
    ws.message(JSON.stringify({ ...defaultState('FSB1'), sog: 7.5 }));
    ws.message(JSON.stringify({ heading: 10, pos: { lat: 0, lon: 0, x: 0, y: 0 } }));
    expect(received[1]?.sog).toBe(7.5);
    expect(received[1]?.issues).toContain('sog');
    client.stop();
  });

  it('送出指令為 command.schema 的 JSON;未連線時暫存並於連上後補送(同類型只留最後一筆)', () => {
    const { client } = makeClient();
    client.start();
    client.send({ type: 'rudder', value: 10 });
    client.send({ type: 'rudder', value: -20 });
    client.send({ type: 'telegraph', value: 'HAH' });
    const ws = FakeWebSocket.instances[0]!;
    expect(ws.sent).toEqual([]);
    ws.open();
    expect(ws.sent).toEqual(['{"type":"rudder","value":-20}', '{"type":"telegraph","value":"HAH"}']);
    client.send({ type: 'freeze' });
    expect(ws.sent.at(-1)).toBe('{"type":"freeze"}');
    client.stop();
  });

  it('斷線後顯示未連線並以指數退避重連(1 s → 1.7 s → … ≤ 5 s),連上後退避重設', () => {
    const { client, statuses } = makeClient();
    client.start();
    const ws1 = FakeWebSocket.instances[0]!;
    ws1.open();
    ws1.drop('server gone');
    expect(client.status).toBe('disconnected');
    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(999);
    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeWebSocket.instances).toHaveLength(2);
    FakeWebSocket.instances[1]!.drop();
    vi.advanceTimersByTime(1700);
    expect(FakeWebSocket.instances).toHaveLength(3);
    FakeWebSocket.instances[2]!.drop();
    vi.advanceTimersByTime(2890);
    expect(FakeWebSocket.instances).toHaveLength(4);
    FakeWebSocket.instances[3]!.drop();
    vi.advanceTimersByTime(4913);
    expect(FakeWebSocket.instances).toHaveLength(5);
    FakeWebSocket.instances[4]!.drop();
    vi.advanceTimersByTime(5000); // 上限 5 s
    expect(FakeWebSocket.instances).toHaveLength(6);
    FakeWebSocket.instances[5]!.open();
    expect(client.status).toBe('connected');
    FakeWebSocket.instances[5]!.drop();
    vi.advanceTimersByTime(1000); // 退避已重設為 1 s
    expect(FakeWebSocket.instances).toHaveLength(7);
    expect(statuses).toContain('connecting');
    client.stop();
  });

  it('stop() 後不再重連', () => {
    const { client } = makeClient();
    client.start();
    FakeWebSocket.instances[0]!.open();
    client.stop();
    expect(client.status).toBe('disconnected');
    vi.advanceTimersByTime(60_000);
    expect(FakeWebSocket.instances).toHaveLength(1);
  });
});
