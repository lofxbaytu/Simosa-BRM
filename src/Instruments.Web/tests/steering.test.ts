// 操舵模式:steeringMode 指令建構(依 command.schema.json 的 value/args)、由原始狀態框取出 state.steering(含信封)、
// 顯示文字、WsClient 在 parseState 之後補上 steering、事件文字化。
import { afterEach, describe, expect, it, vi } from 'vitest';
import { parseSteering, sameSteering, steeringLabel, steeringModeCommand } from '../src/lib/steering.js';
import { serializeCommand } from '../src/lib/command.js';
import { defaultState } from '../src/lib/state-parser.js';
import { eventLabel, parseSimEvent } from '../src/lib/sim-event.js';
import { WsClient } from '../src/net/ws-client.js';
import type { OwnShipState } from '../src/types/state.js';

describe('steeringModeCommand', () => {
  it('fu / emergency 只帶模式與泵;nfu 一定帶 direction(不明視同 stop);autopilot 帶 heading/rotLimit', () => {
    expect(steeringModeCommand('fu')).toEqual({ type: 'steeringMode', value: 'fu' });
    expect(steeringModeCommand('emergency', { pump: '2', direction: 'port', heading: 90 })).toEqual({ type: 'steeringMode', value: 'emergency', args: { pump: '2' } });
    expect(steeringModeCommand('nfu', { pump: 'both', direction: 'starboard' })).toEqual({ type: 'steeringMode', value: 'nfu', args: { pump: 'both', direction: 'starboard' } });
    expect(steeringModeCommand('nfu')).toEqual({ type: 'steeringMode', value: 'nfu', args: { direction: 'stop' } });
    expect(steeringModeCommand('nfu', { direction: 'left' as never })).toEqual({ type: 'steeringMode', value: 'nfu', args: { direction: 'stop' } });
    expect(steeringModeCommand('autopilot', { heading: 370.26, rotLimit: -15.6, direction: 'port' })).toEqual({ type: 'steeringMode', value: 'autopilot', args: { heading: 10.3, rotLimit: 16 } });
    expect(() => steeringModeCommand('track' as never)).toThrow(RangeError);
  });

  it('可序列化(type 在 COMMAND_TYPES 內)', () => {
    expect(serializeCommand(steeringModeCommand('nfu', { pump: '1', direction: 'port' }))).toBe('{"type":"steeringMode","value":"nfu","args":{"pump":"1","direction":"port"}}');
  });
});

describe('parseSteering / steeringLabel', () => {
  it('接受直接狀態、信封與 JSON 字串;mode/pump 不在列舉內或缺欄位回傳 undefined', () => {
    expect(parseSteering({ ...defaultState('FSB1'), steering: { mode: 'nfu', pump: 'both', nfuActive: true } })).toEqual({ mode: 'nfu', pump: 'both', nfuActive: true });
    expect(parseSteering({ type: 'state', state: { ...defaultState('FSB1'), steering: { mode: 'fu', pump: 1 } } })).toEqual({ mode: 'fu', pump: '1' });
    expect(parseSteering(JSON.stringify({ data: { heading: 0, pos: {}, steering: { mode: 'emergency', pump: '2' } } }))).toEqual({ mode: 'emergency', pump: '2' });
    expect(parseSteering(defaultState('FSB1'))).toBeUndefined();
    expect(parseSteering({ steering: { mode: 'track', pump: 'both' } })).toBeUndefined();
    expect(parseSteering({ steering: { mode: 'fu', pump: '3' } })).toBeUndefined();
    expect(parseSteering({ steering: { mode: 'fu' } })).toBeUndefined();
    expect(parseSteering('not json')).toBeUndefined();
    expect(parseSteering(null)).toBeUndefined();
  });

  it('顯示文字與比較', () => {
    expect(steeringLabel(undefined)).toBe('核心未回報');
    expect(steeringLabel({ mode: 'fu', pump: 'both' })).toBe('FU 隨動 · 泵 both');
    expect(steeringLabel({ mode: 'nfu', pump: '1', nfuActive: true })).toBe('NFU 非隨動 · 泵 1 · 推舵中');
    expect(steeringLabel({ mode: 'nfu', pump: '1' })).toBe('NFU 非隨動 · 泵 1 · 推桿放開');
    expect(sameSteering({ mode: 'nfu', pump: '1' }, { mode: 'nfu', pump: '1', nfuActive: false })).toBe(true);
    expect(sameSteering({ mode: 'nfu', pump: '1' }, { mode: 'nfu', pump: '1', nfuActive: true })).toBe(false);
    expect(sameSteering(undefined, { mode: 'fu', pump: 'both' })).toBe(false);
  });
});

describe('事件文字化', () => {
  it('steeringModeChanged / commandRejected 來源預設為 instructor 並有中文一句話', () => {
    const changed = parseSimEvent({ kind: 'steeringModeChanged', tick: 100, t: 2, data: { from: 'fu', to: 'nfu', pump: 'both' } })!;
    expect(changed.source).toBe('instructor');
    expect(eventLabel(changed)).toBe('操舵模式 FU 隨動 → NFU 非隨動(泵 both)');
    const rejected = parseSimEvent({ kind: 'commandRejected', tick: 100, t: 2, data: { command: 'autopilot', reason: 'emergencySteering' } })!;
    expect(eventLabel(rejected)).toBe('指令被拒 autopilot(應急操舵中不可自動舵)');
  });
});

// ---- WsClient:parseState 不認得 steering,由 ws-client 以原始框補上
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  onopen: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onerror: (() => void) | null = null;
  onclose: ((ev: { reason: string }) => void) | null = null;
  sent: string[] = [];
  constructor(readonly url: string) {
    FakeWebSocket.instances.push(this);
  }
  send(data: string): void {
    this.sent.push(data);
  }
  close(): void {
    this.readyState = 3;
    this.onclose?.({ reason: '' });
  }
  open(): void {
    this.readyState = 1;
    this.onopen?.();
  }
  message(data: string): void {
    this.onmessage?.({ data });
  }
}

afterEach(() => {
  vi.useRealTimers();
  FakeWebSocket.instances = [];
});

describe('WsClient state.steering', () => {
  it('狀態框含 steering 時附在 state 上;沒有(Python 參考伺服器)則為 undefined;非狀態框仍交給 onMessage', () => {
    vi.useFakeTimers();
    const client = new WsClient({ url: 'ws://test:1', webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket });
    const states: OwnShipState[] = [];
    const messages: Record<string, unknown>[] = [];
    client.onState(({ state }) => states.push(state));
    client.onMessage((m) => messages.push(m));
    client.start();
    const ws = FakeWebSocket.instances[0]!;
    ws.open();
    ws.message(JSON.stringify({ type: 'state', state: { ...defaultState('FSB1'), steering: { mode: 'nfu', pump: 'both', nfuActive: true } } }));
    ws.message(JSON.stringify({ ...defaultState('FSB1'), heading: 5 }));
    ws.message(JSON.stringify({ type: 'event', event: { kind: 'steeringModeChanged', tick: 1, t: 0.02, data: { from: 'fu', to: 'nfu', pump: 'both' } } }));
    ws.message('not json');
    expect(states.map((s) => s.steering)).toEqual([{ mode: 'nfu', pump: 'both', nfuActive: true }, undefined]);
    expect(messages).toHaveLength(1);
    expect(messages[0]!['type']).toBe('event');
    client.stop();
  });
});
