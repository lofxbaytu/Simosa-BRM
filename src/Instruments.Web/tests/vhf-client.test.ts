// VHF 客戶端(lib/vhf-client.ts):以假 WebSocket / 假麥克風 / 假 MediaRecorder / 假播放器驗證 join 流程、PTT 擷取與二進位框送出、
// 放開 PTT 時先停錄再送 pttEnd、busy 停錄、接收時播放器 start/append/end、無麥克風時不發話但文字可用、斷線重連後重新 join。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { VhfClient, detectPlaybackMode, type PlayerLike, type RecorderLike } from '../src/lib/vhf-client.js';
import { decodeAudioFrame, encodeAudioFrame, type VhfPeer } from '../src/lib/vhf.js';

class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  readyState = FakeWebSocket.CONNECTING;
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
  message(data: unknown): void {
    this.onmessage?.({ data });
  }
  json(obj: unknown): void {
    this.message(JSON.stringify(obj));
  }
  drop(reason = ''): void {
    this.readyState = FakeWebSocket.CLOSED;
    this.onclose?.({ reason });
  }
  texts(): Array<Record<string, unknown>> {
    return this.sent.filter((s): s is string => typeof s === 'string').map((s) => JSON.parse(s) as Record<string, unknown>);
  }
  binaries(): Uint8Array[] {
    return this.sent.filter((s): s is Uint8Array => typeof s !== 'string');
  }
}

class FakeRecorder implements RecorderLike {
  static instances: FakeRecorder[] = [];
  state: 'inactive' | 'recording' | 'paused' = 'inactive';
  timeslice: number | undefined;
  ondataavailable: ((ev: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  constructor() {
    FakeRecorder.instances.push(this);
  }
  start(timeslice?: number): void {
    this.state = 'recording';
    this.timeslice = timeslice;
  }
  stop(): void {
    this.state = 'inactive';
    // 真實 MediaRecorder:stop 後先送最後一片 dataavailable,再 onstop
    this.chunk(new Uint8Array([9, 9]));
    this.onstop?.();
  }
  chunk(bytes: Uint8Array): void {
    this.ondataavailable?.({ data: new Blob([bytes as unknown as BlobPart]) });
  }
}

class FakePlayer implements PlayerLike {
  calls: string[] = [];
  chunks: number[][] = [];
  volume = 1;
  start(from: VhfPeer): void {
    this.calls.push(`start:${from.station}`);
  }
  append(chunk: Uint8Array): void {
    this.chunks.push(Array.from(chunk));
  }
  end(): void {
    this.calls.push('end');
  }
  abort(): void {
    this.calls.push('abort');
  }
  setVolume(v: number): void {
    this.volume = v;
  }
}

async function flush(): Promise<void> {
  // Blob.arrayBuffer() 在 Node 為真正的非同步工作:以未被假計時器取代的 setImmediate 讓出事件迴圈幾輪,並推進假計時器
  for (let i = 0; i < 6; i++) {
    await new Promise<void>((resolve) => setImmediate(resolve));
    vi.advanceTimersByTime(1);
  }
}

function makeClient(opts: { mic?: 'ok' | 'deny' | 'none'; watch?: string[] } = {}) {
  const player = new FakePlayer();
  const stream = { getTracks: () => [{ stop: () => undefined }] } as unknown as MediaStream;
  const client = new VhfClient({
    url: 'ws://test:8767/',
    station: 'bridge',
    channel: '16',
    watch: opts.watch ?? [],
    reconnectMinMs: 1000,
    reconnectMaxMs: 5000,
    webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket,
    ...(opts.mic === 'none' ? {} : { getUserMedia: () => (opts.mic === 'deny' ? Promise.reject(new DOMException('Permission denied', 'NotAllowedError')) : Promise.resolve(stream)) }),
    recorderFactory: () => new FakeRecorder(),
    playerFactory: () => player,
    detectPlayback: () => 'mse',
  });
  const changes: string[] = [];
  client.onChange(() => changes.push(`${client.connection}/${client.machine.state}/${client.mic}`));
  return { client, player, changes };
}

async function connectAndJoin(client: VhfClient, id = 's1'): Promise<FakeWebSocket> {
  client.start();
  const ws = FakeWebSocket.instances.at(-1)!;
  ws.open();
  await flush();
  // 伺服器回送 join 的頻道與守聽(與 C# VhfRelayServer 相同)
  ws.json({ type: 'vhf', kind: 'joined', id, station: 'bridge', channel: client.machine.channel, watch: [...client.machine.watch], stations: [] });
  await flush();
  return ws;
}

beforeEach(() => {
  // 保留真正的 setImmediate(flush 用來讓 Blob.arrayBuffer() 完成)
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
  FakeWebSocket.instances = [];
  FakeRecorder.instances = [];
  vi.stubGlobal('WebSocket', FakeWebSocket);
  vi.stubGlobal('navigator', { mediaDevices: undefined });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('VhfClient', () => {
  it('連線後送 join,joined 後 joined=true;麥克風未授權前 canTransmit=false', async () => {
    const { client, changes } = makeClient();
    client.start();
    const ws = FakeWebSocket.instances[0]!;
    expect(ws.url).toBe('ws://test:8767/');
    expect(ws.binaryType).toBe('arraybuffer');
    expect(client.connection).toBe('connecting');
    ws.open();
    await flush();
    expect(client.connection).toBe('connected');
    expect(ws.texts()).toEqual([{ type: 'vhf', kind: 'join', station: 'bridge', channel: '16', watch: [] }]);
    ws.json({ type: 'vhf', kind: 'joined', id: 's1', channel: '16', watch: [], stations: [] });
    await flush();
    expect(client.machine.joined).toBe(true);
    expect(client.canTransmit).toBe(false);
    expect(client.mic).toBe('unknown');
    expect(changes.length).toBeGreaterThan(0);
    client.stop();
    expect(client.connection).toBe('disconnected');
    expect(client.machine.joined).toBe(false);
  });

  it('PTT:第一次按下先取得麥克風 → pttStart + 錄音(100 ms 片段)→ 片段以本站 id 的二進位框送出 → 放開:先停錄(最後一片送出)再 pttEnd', async () => {
    const { client } = makeClient();
    const ws = await connectAndJoin(client);
    await client.pttDown();
    await flush();
    expect(client.mic).toBe('granted');
    expect(client.machine.state).toBe('transmitting');
    expect(ws.texts().at(-1)).toEqual({ type: 'vhf', kind: 'pttStart', channel: '16' });
    const rec = FakeRecorder.instances[0]!;
    expect(rec.state).toBe('recording');
    expect(rec.timeslice).toBe(100);
    rec.chunk(new Uint8Array([1, 2, 3]));
    await flush();
    const frames = ws.binaries();
    expect(frames).toHaveLength(1);
    const decoded = decodeAudioFrame(frames[0]!)!;
    expect(decoded.id).toBe('s1');
    expect(Array.from(decoded.payload)).toEqual([1, 2, 3]);
    expect(client.audioFramesSent).toBe(1);
    const up = client.pttUp();
    await flush();
    await up;
    expect(rec.state).toBe('inactive');
    // 最後一片(stop 時送出的 [9,9])在 pttEnd 之前
    const order = ws.sent.map((s) => (typeof s === 'string' ? (JSON.parse(s) as { kind: string }).kind : 'bin'));
    expect(order).toEqual(['join', 'pttStart', 'bin', 'bin', 'pttEnd']);
    expect(client.machine.state).toBe('idle');
    expect(client.machine.log.at(-1)?.kind).toBe('call');
    // 錄音器停止後遲到的片段不送
    rec.chunk(new Uint8Array([7]));
    await flush();
    expect(ws.binaries()).toHaveLength(2);
    // 第二次按下不再要求授權、建立新的錄音器
    await client.pttDown();
    expect(FakeRecorder.instances).toHaveLength(2);
    await client.pttUp();
    await flush();
  });

  it('授權對話框期間放開 PTT:授權完成後不開始發話(不送 pttStart、不錄音、狀態 idle)', async () => {
    // 第一次按 PTT 時瀏覽器跳出麥克風授權,使用者放開按鍵去點「允許」:不得卡在 transmitting(審查第二十一輪 major)
    let resolveMic: ((s: MediaStream) => void) | undefined;
    const stream = { getTracks: () => [{ stop: () => undefined }] } as unknown as MediaStream;
    const client = new VhfClient({
      url: 'ws://test:8767/',
      station: 'bridge',
      channel: '16',
      watch: [],
      reconnectMinMs: 1000,
      reconnectMaxMs: 5000,
      webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket,
      getUserMedia: () => new Promise<MediaStream>((res) => { resolveMic = res; }),
      recorderFactory: () => new FakeRecorder(),
      playerFactory: () => new FakePlayer(),
      detectPlayback: () => 'mse',
    });
    const ws = await connectAndJoin(client);
    const down = client.pttDown();
    await flush();
    await client.pttUp(); // 對話框仍開著就放開
    resolveMic!(stream);
    await down;
    await flush();
    expect(client.mic).toBe('granted');
    expect(client.machine.state).toBe('idle');
    expect(ws.texts().map((t) => t.kind)).toEqual(['join']);
    expect(FakeRecorder.instances).toHaveLength(0);
    // 之後正常按放仍可發話
    await client.pttDown();
    expect(client.machine.state).toBe('transmitting');
    await client.pttUp();
    await flush();
    expect(client.machine.state).toBe('idle');
  });

  it('busy:發話中收到佔用 → 停錄、狀態 busy、期滿回 idle;放開 PTT 不送 pttEnd', async () => {
    const { client } = makeClient();
    const ws = await connectAndJoin(client);
    await client.pttDown();
    await flush();
    ws.json({ type: 'vhf', kind: 'busy', channel: '16', by: 's2', byStation: 'vts' });
    await flush();
    expect(client.machine.state).toBe('busy');
    expect(FakeRecorder.instances[0]!.state).toBe('inactive');
    await client.pttUp();
    expect(ws.texts().map((t) => t['kind'])).toEqual(['join', 'pttStart']);
    vi.advanceTimersByTime(2000);
    expect(client.machine.state).toBe('idle');
  });

  it('接收:同頻道 pttStart → 播放器 start;音訊框只在接收該站時 append;pttEnd → end;他站 id 的框丟棄', async () => {
    const { client, player } = makeClient();
    const ws = await connectAndJoin(client);
    ws.message(encodeAudioFrame('s2', new Uint8Array([5])).buffer); // 尚未 pttStart:丟棄
    ws.json({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'vts', channel: '16' });
    await flush();
    expect(client.machine.state).toBe('receiving');
    expect(player.calls).toEqual(['start:vts']);
    ws.message(encodeAudioFrame('s2', new Uint8Array([1, 2])).buffer);
    ws.message(encodeAudioFrame('s3', new Uint8Array([3])).buffer);
    ws.message(new Uint8Array([0, 0]).buffer); // 壞框
    ws.message(encodeAudioFrame('s2', new Uint8Array([4])));
    expect(player.chunks).toEqual([[1, 2], [4]]);
    expect(client.audioFramesReceived).toBe(4);
    ws.json({ type: 'vhf', kind: 'pttEnd', id: 's2', station: 'vts', channel: '16', duration_s: 2.5 });
    await flush();
    expect(player.calls).toEqual(['start:vts', 'end']);
    expect(client.machine.state).toBe('idle');
    expect(client.machine.log.at(-1)).toMatchObject({ kind: 'call', direction: 'rx', station: 'vts', duration_s: 2.5 });
    client.setVolume(0.3);
    expect(player.volume).toBe(0.3);
    expect(client.volumeLevel).toBe(0.3);
  });

  it('無麥克風權限:按 PTT 不發話(mic denied、錯誤訊息),文字與 DSC 仍可送;沒有 getUserMedia 時 unavailable', async () => {
    const { client } = makeClient({ mic: 'deny' });
    const ws = await connectAndJoin(client);
    await client.pttDown();
    await flush();
    expect(client.mic).toBe('denied');
    expect(client.micError).toContain('NotAllowedError');
    expect(client.machine.state).toBe('idle');
    expect(ws.texts().map((t) => t['kind'])).toEqual(['join']);
    client.sendText('Bridge to VTS: no microphone, text only');
    client.sendDsc('urgency', 'PAN PAN');
    expect(ws.texts().slice(1)).toEqual([
      { type: 'vhf', kind: 'text', channel: '16', text: 'Bridge to VTS: no microphone, text only' },
      { type: 'vhf', kind: 'dsc', category: 'urgency', text: 'PAN PAN' },
    ]);
    const none = makeClient({ mic: 'none' });
    expect(none.client.mic).toBe('unavailable');
    expect(await none.client.requestMic()).toBe(false);
    expect(none.client.micError).toContain('麥克風');
  });

  it('斷線:狀態回 idle、播放中止、指數退避重連並重新 join;頻道/守聽變更送 channel', async () => {
    const { client, player } = makeClient({ watch: ['12'] });
    const ws = await connectAndJoin(client);
    expect(ws.texts()[0]).toEqual({ type: 'vhf', kind: 'join', station: 'bridge', channel: '16', watch: ['12'] });
    client.setChannel('13');
    client.setWatch('14');
    await flush();
    expect(ws.texts().slice(1)).toEqual([
      { type: 'vhf', kind: 'channel', channel: '13', watch: ['12'] },
      { type: 'vhf', kind: 'channel', channel: '13', watch: ['14'] },
    ]);
    ws.json({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'vts', channel: '14' });
    await flush();
    expect(client.machine.state).toBe('receiving');
    ws.drop('server gone');
    await flush();
    expect(client.connection).toBe('disconnected');
    expect(client.machine.state).toBe('idle');
    expect(client.machine.joined).toBe(false);
    expect(player.calls).toContain('abort');
    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1000);
    expect(FakeWebSocket.instances).toHaveLength(2);
    const ws2 = FakeWebSocket.instances[1]!;
    ws2.open();
    await flush();
    expect(ws2.texts()).toEqual([{ type: 'vhf', kind: 'join', station: 'bridge', channel: '13', watch: ['14'] }]);
    client.stop();
    vi.advanceTimersByTime(10000);
    expect(FakeWebSocket.instances).toHaveLength(2);
  });

  it('播放模式偵測:無 MediaSource/Audio 的環境為 none', () => {
    expect(detectPlaybackMode()).toBe('none');
    vi.stubGlobal('MediaSource', { isTypeSupported: (t: string) => t.includes('opus') });
    expect(detectPlaybackMode()).toBe('mse');
    vi.stubGlobal('MediaSource', { isTypeSupported: () => false });
    vi.stubGlobal('Audio', class {});
    vi.stubGlobal('Blob', class {});
    expect(detectPlaybackMode()).toBe('blob');
  });
});
