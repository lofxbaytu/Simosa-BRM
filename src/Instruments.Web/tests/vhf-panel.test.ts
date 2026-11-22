// <brm-vhf> 元件(happy-dom):頻道旋鈕、PTT 鈕狀態(未授權麥克風時停用、發話/接收/佔用指示)、訊息列、文字送出、
// DSC 二次確認、雙守聽選單、空白鍵 PTT;以注入的 VhfClient(假 WebSocket/麥克風/錄音/播放)驅動。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { VhfClient, type PlayerLike, type RecorderLike } from '../src/lib/vhf-client.js';
import type { VhfPanel } from '../src/components/vhf-panel.js';
import { PTT_NO_MIC_LABEL, PTT_NO_MIC_NO_AUDIO_LABEL, isScrolledToBottom } from '../src/components/vhf-panel.js';

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

/** mic:'ok' 授權、'deny' 按 PTT 時被拒、'none' 沒有麥克風 API(happy-dom 的 navigator 沒有 mediaDevices → unavailable) */
function makeClient(mic: 'ok' | 'deny' | 'none' = 'ok', watch: string[] = [], playback: 'mse' | 'none' = 'mse'): { client: VhfClient; ws: () => FakeWebSocket } {
  const stream = { getTracks: () => [] } as unknown as MediaStream;
  const client = new VhfClient({
    url: 'ws://test:8767/',
    station: 'bridge',
    channel: '16',
    watch,
    webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket,
    ...(mic === 'none' ? {} : { getUserMedia: () => (mic === 'deny' ? Promise.reject(new Error('denied')) : Promise.resolve(stream)) }),
    recorderFactory: () => new FakeRecorder(),
    playerFactory: () => new FakePlayer(),
    detectPlayback: () => playback,
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

  it('空間不足時移到 title 的說明:DSC 列與 PTT 的 title 帶完整文字;麥克風說明(未授權)可隱藏、警示不可(第二十九輪 L3)', async () => {
    const { client, ws } = makeClient();
    const el = await mount({ client });
    await joined(el, ws());
    // DSC 說明整句在 .dsc 的 title(--brm-vhf-hint-display: none 時文字不顯示,但不截斷成半句)
    expect(q<HTMLElement>(el, '.dsc').title).toBe('簡化 DSC:文字警示送所有站(非真實 DSC 編碼)');
    expect(q<HTMLElement>(el, '.dsc .hint').textContent).toBe('簡化 DSC:文字警示送所有站(非真實 DSC 編碼)');
    // 麥克風尚未授權:說明列是 .hint.mic(非 .warn,可依 --brm-vhf-mic-hint-display 隱藏),PTT 的 title 帶同一句
    const mic = q<HTMLElement>(el, '.hint.mic');
    expect(mic.classList.contains('warn')).toBe(false);
    expect(q<HTMLButtonElement>(el, 'button.ptt').title).toContain('麥克風:按 PTT 時要求授權');
    // 二次確認中:DSC 列不帶 title(警示文字照常顯示)
    q<HTMLButtonElement>(el, 'button.distress').click();
    await settle(el);
    expect(q<HTMLElement>(el, '.dsc').title).toBe('');
    expect(q<HTMLElement>(el, '.dsc .hint.warn').textContent).toContain('s 內未確認自動取消');
    el.remove();
  });

  it('無麥克風權限的說明是警示(.warn),不隨空間不足隱藏', async () => {
    const { client, ws } = makeClient('deny');
    const el = await mount({ client });
    await joined(el, ws());
    q<HTMLButtonElement>(el, 'button.ptt').dispatchEvent(new Event('pointerdown', { bubbles: true, cancelable: true }));
    await settle(el);
    await settle(el);
    expect(q<HTMLElement>(el, '.hint.mic').classList.contains('warn')).toBe(true);
    el.remove();
  });

  it('無麥克風(被拒):PTT 停用、鈕上寫「無麥克風 · 只能用文字 / DSC」,完整說明與錯誤在 title;警示列標 on-ptt(壓縮時可隱藏)(第二十九輪 L4)', async () => {
    const { client, ws } = makeClient('deny');
    const el = await mount({ client });
    await joined(el, ws());
    const ptt = q<HTMLButtonElement>(el, 'button.ptt');
    expect(ptt.textContent?.trim()).toBe('PTT CH16');
    expect(ptt.classList.contains('no-mic')).toBe(false);
    ptt.dispatchEvent(new Event('pointerdown', { bubbles: true, cancelable: true }));
    await settle(el);
    await settle(el);
    expect(client.mic).toBe('denied');
    expect(ptt.disabled).toBe(true);
    expect(ptt.classList.contains('no-mic')).toBe(true);
    expect(ptt.textContent?.trim()).toBe(PTT_NO_MIC_LABEL);
    expect(ptt.title).toContain('無麥克風權限:只能以文字通訊');
    expect(ptt.title).toContain('(Error: denied)');
    // 警示列:完整文字照常產生(非壓縮時顯示);on-ptt 表示 PTT 鈕上已有短句,壓縮時(--brm-vhf-mic-hint-display: none)不另佔一列
    const hint = q<HTMLElement>(el, '.hint.mic');
    expect(hint.classList.contains('warn')).toBe(true);
    expect(hint.classList.contains('on-ptt')).toBe(true);
    expect(hint.textContent).toContain('無麥克風權限:只能以文字通訊');
    // 文字與 DSC 仍可用
    expect(q<HTMLButtonElement>(el, 'button.distress').disabled).toBe(false);
    expect(q<HTMLButtonElement>(el, 'button.urgency').disabled).toBe(false);
    el.remove();
  });

  it('沒有麥克風 API(unavailable):一開始就停用 PTT 並寫短句;同時無法播放音訊時寫「無麥克風 / 無音訊」', async () => {
    const { client, ws } = makeClient('none', [], 'none');
    const el = await mount({ client });
    expect(client.mic).toBe('unavailable');
    const ptt = q<HTMLButtonElement>(el, 'button.ptt');
    // 尚未連線:鈕上仍寫無麥克風,title 註明未連線
    expect(ptt.textContent?.trim()).toBe(PTT_NO_MIC_NO_AUDIO_LABEL);
    expect(ptt.title).toMatch(/^未連線;此環境沒有麥克風 API/);
    await joined(el, ws());
    expect(ptt.disabled).toBe(true);
    expect(ptt.textContent?.trim()).toBe(PTT_NO_MIC_NO_AUDIO_LABEL);
    expect(ptt.title).toContain('此環境沒有麥克風 API');
    expect(ptt.title).toContain('此環境無法播放音訊,只收文字');
    expect(q<HTMLElement>(el, '.hint.mic').className).toMatch(/warn.*on-ptt/);
    el.remove();
  });

  it('只有無法播放音訊(麥克風尚未授權):警示列不標 on-ptt(壓縮時照常顯示),PTT 可用', async () => {
    const { client, ws } = makeClient('ok', [], 'none');
    const el = await mount({ client });
    await joined(el, ws());
    const hint = q<HTMLElement>(el, '.hint.mic');
    expect(hint.classList.contains('warn')).toBe(true);
    expect(hint.classList.contains('on-ptt')).toBe(false);
    expect(hint.textContent).toContain('此環境無法播放音訊');
    expect(q<HTMLButtonElement>(el, 'button.ptt').disabled).toBe(false);
    expect(q<HTMLButtonElement>(el, 'button.ptt').textContent?.trim()).toBe('PTT CH16');
    el.remove();
  });

  it('訊息紀錄列貼底:收到新訊息時捲到最新一則(含第一次);使用者往上捲時不拉回,捲回底部後恢復(第二十九輪 L4)', async () => {
    const { client, ws } = makeClient();
    const el = await mount({ client });
    const log = q<HTMLElement>(el, '.log');
    // happy-dom 沒有版面:以每則 20 px、可見高度 35 px(壓縮時約兩則)模擬 scrollHeight / clientHeight
    const ITEM = 20;
    const VISIBLE = 35;
    Object.defineProperty(log, 'scrollHeight', { configurable: true, get: () => log.querySelectorAll('.item').length * ITEM });
    Object.defineProperty(log, 'clientHeight', { configurable: true, get: () => VISIBLE });
    const bottom = (): number => log.scrollHeight;
    // 第一次(連線訊息)就捲到底
    await joined(el, ws());
    expect(log.querySelectorAll('.item').length).toBe(1);
    expect(log.scrollTop).toBe(bottom());
    const rx = async (text: string): Promise<void> => {
      ws().json({ type: 'vhf', kind: 'text', id: 's2', station: 'VTS', channel: '16', text });
      await settle(el);
    };
    for (let i = 1; i <= 3; i++) await rx(`VTS 呼叫 第 ${i} 則`);
    expect(log.querySelectorAll('.item').length).toBe(4);
    expect(log.scrollTop).toBe(bottom());
    expect(isScrolledToBottom(log)).toBe(true);
    // 使用者往上捲(看舊訊息):新訊息不把畫面拉回底部
    log.scrollTop = 0;
    log.dispatchEvent(new Event('scroll'));
    await rx('VTS 呼叫 第 4 則');
    expect(log.scrollTop).toBe(0);
    // DSC 也一樣不拉回
    ws().json({ type: 'vhf', kind: 'dsc', id: 's9', station: 'tug', channel: '70', category: 'urgency', text: '' });
    await settle(el);
    expect(log.scrollTop).toBe(0);
    // 捲回底部(容差 4 px 內)後恢復貼底
    log.scrollTop = bottom() - VISIBLE - 3;
    log.dispatchEvent(new Event('scroll'));
    await rx('VTS 呼叫 第 5 則');
    expect(log.scrollTop).toBe(bottom());
    // 與紀錄無關的更新(音量)不捲動
    log.scrollTop = 0;
    log.dispatchEvent(new Event('scroll'));
    const vol = q<HTMLInputElement>(el, '.volume input');
    vol.value = '50';
    vol.dispatchEvent(new Event('input', { bubbles: true }));
    await settle(el);
    expect(log.scrollTop).toBe(0);
    el.remove();
  });

  it('isScrolledToBottom:scrollTop + clientHeight ≥ scrollHeight − 4 px', () => {
    expect(isScrolledToBottom({ scrollTop: 65, clientHeight: 35, scrollHeight: 100 })).toBe(true);
    expect(isScrolledToBottom({ scrollTop: 61, clientHeight: 35, scrollHeight: 100 })).toBe(true);
    expect(isScrolledToBottom({ scrollTop: 60, clientHeight: 35, scrollHeight: 100 })).toBe(false);
    expect(isScrolledToBottom({ scrollTop: 0, clientHeight: 35, scrollHeight: 20 })).toBe(true);
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
