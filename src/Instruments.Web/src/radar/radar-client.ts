// 雷達服務 WebSocket 客戶端:ws://<host>:8766/radar/x|s。同一連線收二進位影像幀(SRAD)與 JSON 文字框,送 JSON 控制指令。
// 斷線自動重連(指數退避 1 → 5 s);未連線時只暫存「設定類」指令(每類最後一筆,連上後補送),擷取/取消不補送(位置已過時)。

import { FrameError, parseSpokeFrame, type SpokeFrame } from './frame.js';
import {
  parseRadarMessage,
  serializeRadarCommand,
  type RadarCommand,
  type RadarCommandWithReq,
  type RadarId,
  type RadarMessage,
} from './protocol.js';

export type RadarConnStatus = 'connecting' | 'connected' | 'disconnected' | 'demo';

export type FrameListener = (f: SpokeFrame) => void;
export type MessageListener = (m: RadarMessage) => void;
export type ConnListener = (s: RadarConnStatus, detail?: string) => void;

/** 雷達資料來源(真實服務或示範模式)共同介面 */
export interface RadarSource {
  readonly url: string;
  readonly radar: RadarId;
  readonly status: RadarConnStatus;
  start(): void;
  stop(): void;
  /** 送出指令;回傳所帶 reqId(未送出時為 null) */
  send(cmd: RadarCommand): string | null;
  onFrame(l: FrameListener): () => void;
  onMessage(l: MessageListener): () => void;
  onConn(l: ConnListener): () => void;
}

export const DEFAULT_RADAR_PORT = 8766;

/**
 * 由頁面網址參數決定服務網址:?ws= 可給完整網址(含 /radar/x)或基底(ws://host:8766,會補 /radar/<radar>);
 * 未給時用 ws://<頁面主機或 localhost>:8766/radar/<radar>。
 */
export function resolveRadarUrl(params: URLSearchParams, pageHost: string | undefined, radar: RadarId): string {
  const ws = params.get('ws');
  if (ws && ws.trim()) {
    const u = ws.trim().replace(/\/+$/, '');
    if (/\/radar\/[xs]$/i.test(u)) return u.replace(/\/radar\/[xs]$/i, `/radar/${radar}`);
    return `${u}/radar/${radar}`;
  }
  const host = pageHost && pageHost.length > 0 ? pageHost : 'localhost';
  const h = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
  return `ws://${h}:${DEFAULT_RADAR_PORT}/radar/${radar}`;
}

export function parseRadarId(v: string | null | undefined): RadarId {
  return v === 's' || v === 'S' ? 's' : 'x';
}

/** 需要在重連後補送的設定類指令鍵值;其他(acquire、cancel、getTracks…)不補送 */
function replayKey(cmd: RadarCommand): string | null {
  switch (cmd.cmd) {
    case 'range':
    case 'gain':
    case 'sea':
    case 'rain':
    case 'tune':
    case 'guardZone':
    case 'trialManoeuvre':
    case 'cpaLimits':
      return cmd.cmd;
    case 'standby':
    case 'transmit':
      return 'power';
    default:
      return null;
  }
}

export interface RadarClientOptions {
  url: string;
  radar: RadarId;
  reconnectMinMs?: number;
  reconnectMaxMs?: number;
  webSocketFactory?: (url: string) => WebSocket;
}

export interface RadarClientStats {
  frames: number;
  bytes: number;
  messages: number;
  badFrames: number;
}

export class RadarClient implements RadarSource {
  readonly url: string;
  readonly radar: RadarId;
  readonly stats: RadarClientStats = { frames: 0, bytes: 0, messages: 0, badFrames: 0 };
  private socket: WebSocket | null = null;
  private _status: RadarConnStatus = 'disconnected';
  private frameL = new Set<FrameListener>();
  private msgL = new Set<MessageListener>();
  private connL = new Set<ConnListener>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private delay: number;
  private readonly minDelay: number;
  private readonly maxDelay: number;
  private stopped = true;
  private seq = 0;
  private pending = new Map<string, RadarCommand>();
  private readonly factory: (url: string) => WebSocket;

  constructor(o: RadarClientOptions) {
    this.url = o.url;
    this.radar = o.radar;
    this.minDelay = o.reconnectMinMs ?? 1000;
    this.maxDelay = o.reconnectMaxMs ?? 5000;
    this.delay = this.minDelay;
    this.factory = o.webSocketFactory ?? ((u) => new WebSocket(u));
  }

  get status(): RadarConnStatus {
    return this._status;
  }

  start(): void {
    this.stopped = false;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const s = this.socket;
    this.socket = null;
    if (s) {
      s.onopen = s.onclose = s.onerror = s.onmessage = null;
      try {
        s.close();
      } catch {
        /* 忽略 */
      }
    }
    this.setStatus('disconnected', '已停止');
  }

  send(cmd: RadarCommand): string | null {
    const s = this.socket;
    if (s && s.readyState === 1 /* OPEN */) {
      const reqId = `w${++this.seq}`;
      const withReq: RadarCommandWithReq = { ...cmd, reqId };
      s.send(serializeRadarCommand(withReq));
      return reqId;
    }
    const k = replayKey(cmd);
    if (k) this.pending.set(k, cmd);
    return null;
  }

  onFrame(l: FrameListener): () => void {
    this.frameL.add(l);
    return () => this.frameL.delete(l);
  }

  onMessage(l: MessageListener): () => void {
    this.msgL.add(l);
    return () => this.msgL.delete(l);
  }

  onConn(l: ConnListener): () => void {
    this.connL.add(l);
    l(this._status);
    return () => this.connL.delete(l);
  }

  /** 處理一個收到的 WebSocket 資料(測試可直接呼叫) */
  handleData(data: unknown): void {
    if (data instanceof ArrayBuffer || ArrayBuffer.isView(data)) {
      let f: SpokeFrame;
      try {
        f = parseSpokeFrame(data as ArrayBuffer);
      } catch (e) {
        if (e instanceof FrameError) {
          this.stats.badFrames++;
          return;
        }
        throw e;
      }
      this.stats.frames++;
      this.stats.bytes += (data as ArrayBuffer).byteLength;
      for (const l of this.frameL) l(f);
      return;
    }
    if (typeof data !== 'string') {
      // binaryType 未生效時可能收到 Blob
      if (typeof Blob !== 'undefined' && data instanceof Blob) {
        void data.arrayBuffer().then((b) => this.handleData(b));
      }
      return;
    }
    let obj: unknown;
    try {
      obj = JSON.parse(data);
    } catch {
      return;
    }
    const m = parseRadarMessage(obj);
    if (!m) return;
    this.stats.messages++;
    for (const l of this.msgL) l(m);
  }

  private connect(): void {
    if (this.stopped || this.socket) return;
    this.setStatus('connecting', `連線中 ${this.url}`);
    let ws: WebSocket;
    try {
      ws = this.factory(this.url);
      ws.binaryType = 'arraybuffer';
    } catch (err) {
      this.setStatus('disconnected', `無法建立連線:${String(err)}`);
      this.scheduleReconnect();
      return;
    }
    this.socket = ws;
    ws.onopen = () => {
      this.delay = this.minDelay;
      this.setStatus('connected', this.url);
      const cmds = [...this.pending.values()];
      this.pending.clear();
      for (const c of cmds) this.send(c);
    };
    ws.onmessage = (ev: MessageEvent) => this.handleData(ev.data);
    ws.onerror = () => {
      /* 之後必有 close */
    };
    ws.onclose = (ev: CloseEvent) => {
      if (this.socket === ws) this.socket = null;
      if (this.stopped) return;
      this.setStatus('disconnected', ev?.reason ? `連線中斷:${ev.reason}` : '連線中斷');
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.timer) return;
    const d = this.delay;
    this.delay = Math.min(this.maxDelay, Math.round(this.delay * 1.7));
    this.timer = setTimeout(() => {
      this.timer = null;
      this.connect();
    }, d);
  }

  private setStatus(s: RadarConnStatus, detail?: string): void {
    this._status = s;
    for (const l of this.connL) l(s, detail);
  }
}
