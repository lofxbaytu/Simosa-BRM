// <brm-vhf> 元件(happy-dom):頻道旋鈕、PTT 鈕狀態(未授權麥克風時停用、發話/接收/佔用指示)、訊息列、文字送出、
// DSC 二次確認、雙守聽選單、空白鍵 PTT;以注入的 VhfClient(假 WebSocket/麥克風/錄音/播放)驅動。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { VhfClient, type PlayerLike, type RecorderLike } from '../src/lib/vhf-client.js';
import type { VhfPanel } from '../src/components/vhf-panel.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

class FakeWebSocket {
  static OPEN = 1;
  static CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  binaryType = 'blob';
  sent: Array<string | Uint8Array> = [];
  onopen: (() => void) | null = null;
  onclose: ((ev: { reason: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }
  send(data: string | Uint8Array): void {
    this.sent.push(data);
  }
  close(): void {
    this.readyState = FakeWebSocket.CLOSED;
  }
  open(): void {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.();
  }
  json(obj: unknown): void {
    this.onmessage?.({ data: JSON.stringify(obj) });
  }
  kinds(): string[] {
    return this.sent.filter((s): s is string => typeof s === 'string').map((s) => (JSON.parse(s) as { kind: string }).kind);
  }
  last(): Record<string, unknown> {
    const t = this.sent.filter((s): s is string => typeof s === 'string');
    return JSON.parse(t[t.length - 1]!) as Record<string, unknown>;
  }
}

class FakeRecorder implements RecorderLike {
  state: 'inactive' | 'recording' | 'paused' = 'inactive';
  ondataavailable: ((ev: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  start(): void {
    this.state = 'recording';
  }
  stop(): void {
    this.state = 'inactive';
    this.onstop?.();
  }
}

class FakePlayer implements PlayerLike {
  calls: string[] = [];
  start(): void {
    this.calls.push('start');
  }
  append(): void {}
  end(): void {
    this.calls.push('end');
  }
  abort(): void {}
  setVolume(): void {}
}

function makeClient(mic: 'ok' | 'deny' = 'ok', watch: string[] = []): { client: VhfClient; ws: () => FakeWebSocket } {
  const stream = { getTracks: () => [] } as unknown as MediaStream;
  const client = new VhfClient({
    url: 'ws://test:8767/',
    station: 'bridge',
    channel: '16',
    watch,
    webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket,
    getUserMedia: () => (mic === 'deny' ? Promise.reject(new Error('denied')) : Promise.resolve(stream)),
    recorderFactory: () => new FakeRecorder(),
    playerFactory: () => new FakePlayer(),
    detectPlayback: () => 'mse',
  });
  return { client, ws: () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1]! };
}

async function mount(props: Record<string, unknown>, attrs: Record<string, string> = {}): Promise<VhfPanel & Updatable> {
  const el = document.createElement('brm-vhf') as VhfPanel & Updatable;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

async function settle(el: Updatable): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  await el.updateComplete;
}

function text(el: HTMLElement): string {
  return el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
}

function q<T extends Element>(el: HTMLElement, sel: string): T {
  const n = el.shadowRoot?.querySelector<T>(sel);
  if (!n) throw new Error(`找不到 ${sel}`);
  return n;
}

async function joined(el: VhfPanel & Updatable, ws: FakeWebSocket, id = 's1'): Promise<void> {
  ws.open();
  ws.json({ type: 'vhf', kind: 'joined', id, station: 'bridge', channel: el.client!.machine.channel, watch: [...el.client!.machine.watch], stations: [{ id, station: 'bridge', channel: '16', watch: [], transmitting: null }] });
  await settle(el);
}

beforeAll(async () => {
  vi.stubGlobal('WebSocket', FakeWebSocket);
  await import('../src/components/vhf-panel.js');
});

afterEach(() => {
  document.body.innerHTML = '';
  FakeWebSocket.instances = [];
});

describe('<brm-vhf>', () => {
  it('渲染頻道旋鈕(14 個頻道)、未連線狀態、PTT 停用;連線並 joined 後顯示守聽與站名', async () => {
    const { client, ws } = makeClient();
    const el = await mount({ client });
    const buttons = [...el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.channels button')].map((b) => b.textContent?.trim());
    expect(buttons).toEqual(['16', '12', '13', '14', '06', '08', '10', '67', '68', '69', '71', '72', '73', '77']);
    expect(q<HTMLButtonElement>(el, '.channels button.active').textContent?.trim()).toBe('16');
    expect(text(el)).toContain('連線中'); // 注入的 client 由元件啟動連線
    expect(q<HTMLButtonElement>(el, 'button.ptt').disabled).toBe(true);
    expect(el.shadowRoot!.querySelector('.watch')).toBeNull(); // 非雙守聽
    await joined(el, ws());
    expect(text(el)).toContain('已連線');
    expect(text(el)).toContain('守聽');
    expect(text(el)).toContain('VHF 已連線:bridge,頻道 16');
    expect(q<HTMLButtonElement>(el, 'button.ptt').disabled).toBe(false);
    expect(q<HTMLButtonElement>(el, 'button.ptt').textContent?.trim()).toBe('PTT CH16');
    el.remove();
  });

  it('按頻道鈕送 channel 訊息並更新指示;roster 的佔用者標在頻道鈕上', async () => {
    const { client, ws } = makeClient();
    const el = await mount({ client });
    await joined(el, ws());
    const btn12 = [...el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.channels button')].find((b) => b.textContent?.trim() === '12')!;
    btn12.click();
    await settle(el);
    expect(ws().last()).toEqual({ type: 'vhf', kind: 'channel', channel: '12', watch: [] });
    expect(q<HTMLButtonElement>(el, '.channels button.active').textContent?.trim()).toBe('12');
    expect(q<HTMLButtonElement>(el, 'button.ptt').textContent?.trim()).toBe('PTT CH12');
    ws().json({ type: 'vhf', kind: 'roster', stations: [{ id: 's1', station: 'bridge', channel: '12', watch: [], transmitting: null }, { id: 's2', station: 'vts', channel: '16', watch: [], transmitting: '16' }] });
    await settle(el);
    const btn16 = [...el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.channels button')].find((b) => b.textContent?.trim() === '16')!;
    expect(btn16.classList.contains('occupied')).toBe(true);
    expect(btn16.title).toContain('vts');
    el.remove();
  });

  it('PTT 按下/放開(pointer 事件):送 pttStart/pttEnd、鈕顯示 TX;空白鍵也可(輸入框內不觸發)', async () => {
    const { client, ws } = makeClient();
    const el = await mount({ client });
    await joined(el, ws());
    const ptt = q<HTMLButtonElement>(el, 'button.ptt');
    ptt.dispatchEvent(new Event('pointerdown', { bubbles: true, cancelable: true }));
    await settle(el);
    await settle(el);
    expect(client.machine.state).toBe('transmitting');
    expect(ptt.classList.contains('transmitting')).toBe(true);
    expect(ptt.textContent?.trim()).toBe('TX CH16');
    expect(text(el)).toContain('發話中');
    ptt.dispatchEvent(new Event('pointerup', { bubbles: true, cancelable: true }));
    await settle(el);
    await settle(el);
    expect(client.machine.state).toBe('idle');
    expect(ws().kinds()).toEqual(['join', 'pttStart', 'pttEnd']);
    expect(text(el)).toContain('本站發話');
    // 空白鍵
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', bubbles: true }));
    await settle(el);
    await settle(el);
    expect(client.machine.state).toBe('transmitting');
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', bubbles: true, repeat: true }));
    window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space', bubbles: true }));
    await settle(el);
    await settle(el);
    expect(client.machine.state).toBe('idle');
    expect(ws().kinds()).toEqual(['join', 'pttStart', 'pttEnd', 'pttStart', 'pttEnd']);
    // 文字輸入框內按空白鍵不發話(由元件的 keydown 攔截:composedPath 第一個為 input)
    const input = q<HTMLInputElement>(el, '.text-row input');
    input.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', key: ' ', bubbles: true, composed: true }));
    await settle(el);
    expect(client.machine.state).toBe('idle');
    el.remove();
  });

  it('接收與佔用指示:他站發話時顯示 RX 站名;busy 顯示佔用者', async () => {
    const { client, ws } = makeClient();
    const el = await mount({ client });
    await joined(el, ws());
    ws().json({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'VTS', channel: '16' });
    await settle(el);
    expect(text(el)).toContain('RX VTS CH16');
    expect(q(el, '.lamp').classList.contains('receiving')).toBe(true);
    ws().json({ type: 'vhf', kind: 'pttEnd', id: 's2', station: 'VTS', channel: '16', duration_s: 3.4 });
    await settle(el);
    expect(text(el)).toContain('VTS 發話 3.4 s');
    expect(q(el, '.log .item.rx[data-kind="call"]')).toBeTruthy();
    ws().json({ type: 'vhf', kind: 'busy', channel: '16', by: 's3', byStation: 'pilot' });
    await settle(el);
    expect(text(el)).toContain('佔用 pilot');
    expect(q<HTMLButtonElement>(el, 'button.ptt').textContent?.trim()).toBe('頻道佔用');
    el.remove();
  });

  it('文字訊息:輸入 + Enter 送 text;收到的文字列於訊息列;無麥克風時 PTT 停用但文字可送', async () => {
    const { client, ws } = makeClient('deny');
    const el = await mount({ client });
    await joined(el, ws());
    const input = q<HTMLInputElement>(el, '.text-row input');
    input.value = 'Port Control, bridge, request pilot';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle(el);
    expect(q<HTMLButtonElement>(el, 'button.send').disabled).toBe(false);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle(el);
    expect(ws().last()).toEqual({ type: 'vhf', kind: 'text', channel: '16', text: 'Port Control, bridge, request pilot' });
    expect(input.value).toBe('');
    ws().json({ type: 'vhf', kind: 'text', id: 's1', station: 'bridge', channel: '16', text: 'Port Control, bridge, request pilot' });
    ws().json({ type: 'vhf', kind: 'text', id: 's2', station: 'VTS', channel: '16', text: 'Bridge, pilot boarding at 0930' });
    await settle(el);
    expect(text(el)).toContain('本站:Port Control, bridge, request pilot');
    expect(text(el)).toContain('VTS:Bridge, pilot boarding at 0930');
    // 麥克風被拒:按 PTT 後顯示提示、鈕停用
    q<HTMLButtonElement>(el, 'button.ptt').dispatchEvent(new Event('pointerdown', { bubbles: true, cancelable: true }));
    await settle(el);
    await settle(el);
    expect(client.mic).toBe('denied');
    expect(text(el)).toContain('無麥克風權限:只能以文字通訊');
    expect(q<HTMLButtonElement>(el, 'button.ptt').disabled).toBe(true);
    expect(client.machine.state).toBe('idle');
    el.remove();
  });

  it('DSC 二次確認:第一次按只進入待確認,再按一次才送;取消鈕與逾時取消;收到 DISTRESS 以 alarm 樣式列出', async () => {
    vi.useFakeTimers();
    try {
      const { client, ws } = makeClient();
      const el = await mount({ client });
      ws().open();
      ws().json({ type: 'vhf', kind: 'joined', id: 's1', station: 'bridge', channel: '16', watch: [], stations: [] });
      await el.updateComplete;
      const distress = q<HTMLButtonElement>(el, '.dsc button.distress');
      distress.click();
      await el.updateComplete;
      expect(distress.textContent?.trim()).toBe('再按一次送出 DISTRESS');
      expect(distress.classList.contains('armed')).toBe(true);
      expect(ws().kinds()).toEqual(['join']);
      q<HTMLButtonElement>(el, '.dsc button.cancel').click();
      await el.updateComplete;
      expect(distress.textContent?.trim()).toBe('DISTRESS');
      distress.click();
      await el.updateComplete;
      vi.advanceTimersByTime(6500);
      await el.updateComplete;
      expect(distress.classList.contains('armed')).toBe(false);
      const urgency = q<HTMLButtonElement>(el, '.dsc button.urgency');
      const input = q<HTMLInputElement>(el, '.text-row input');
      input.value = 'PAN PAN steering failure';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      urgency.click();
      await el.updateComplete;
      urgency.click();
      await el.updateComplete;
      expect(ws().last()).toEqual({ type: 'vhf', kind: 'dsc', category: 'urgency', text: 'PAN PAN steering failure' });
      expect(input.value).toBe('');
      ws().json({ type: 'vhf', kind: 'dsc', id: 's9', station: 'tug', channel: '70', category: 'distress', text: 'MAYDAY' });
      await el.updateComplete;
      const item = q(el, '.log .item[data-kind="dsc"]');
      expect(item.classList.contains('alarm')).toBe(true);
      expect(item.textContent).toContain('DSC DISTRESS 遇險 — tug:MAYDAY');
      el.remove();
    } finally {
      vi.useRealTimers();
    }
  });

  it('dualWatch:顯示守聽選單,選擇後送 channel 含 watch;守聽頻道的發話也接收', async () => {
    const { client, ws } = makeClient('ok', ['12']);
    const el = await mount({ client }, { dualWatch: '' });
    await joined(el, ws());
    const sel = q<HTMLSelectElement>(el, '.watch select');
    expect(sel.value).toBe('12');
    expect([...sel.options].map((o) => o.value)).toEqual(['', '12', '13', '14', '06', '08', '10', '67', '68', '69', '71', '72', '73', '77']);
    sel.value = '13';
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    await settle(el);
    expect(ws().last()).toEqual({ type: 'vhf', kind: 'channel', channel: '16', watch: ['13'] });
    ws().json({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'bridge-2', channel: '13' });
    await settle(el);
    expect(text(el)).toContain('RX bridge-2 CH13');
    el.remove();
  });

  it('未注入 client 時依 url/station 自行建立並連線;移除元件時停止', async () => {
    const el = await mount({ url: 'ws://host:8767/', station: 'ecr', clientOptions: { webSocketFactory: (url: string) => new FakeWebSocket(url) as unknown as WebSocket, detectPlayback: () => 'none' } });
    expect(el.client).not.toBeNull();
    expect(FakeWebSocket.instances.at(-1)?.url).toBe('ws://host:8767/');
    expect(el.client!.machine.station).toBe('ecr');
    const c = el.client!;
    el.remove();
    expect(c.connection).toBe('disconnected');
    expect(el.client).toBeNull();
  });
});
