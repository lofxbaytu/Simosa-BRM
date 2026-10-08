// WebSocket 客戶端:連 ws://localhost:8765(Python 參考模擬器 `simosa-brm serve` 或 C# SimCore 的 WebSocket 閘道),
// 收 state.schema.json 的 JSON,送 command.schema.json 的 JSON;斷線自動重連(指數退避 1 → 5 秒)。

import { parseState } from '../lib/state-parser.js';
import { serializeCommand } from '../lib/command.js';
import type { SimCommand } from '../types/command.js';
import type { OwnShipState } from '../types/state.js';
import type { ConnectionStatus, SimSource, StateListener, StatusListener } from './sim-source.js';

export const DEFAULT_WS_URL = 'ws://localhost:8765';

export interface WsClientOptions {
  url?: string;
  reconnectMinMs?: number;
  reconnectMaxMs?: number;
  /** 可注入的 WebSocket 建構子(測試用) */
  webSocketFactory?: (url: string) => WebSocket;
}

export class WsClient implements SimSource {
  readonly url: string;
  private socket: WebSocket | null = null;
  private _status: ConnectionStatus = 'disconnected';
  private stateListeners = new Set<StateListener>();
  private statusListeners = new Set<StatusListener>();
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectDelay: number;
  private readonly minDelay: number;
  private readonly maxDelay: number;
  private stopped = true;
  private lastState: OwnShipState | undefined;
  private readonly factory: (url: string) => WebSocket;
  /** 未連線時暫存的最後一筆各類指令(連上後補送,避免舵令遺失) */
  private pending = new Map<string, SimCommand>();

  constructor(options: WsClientOptions = {}) {
    this.url = options.url ?? DEFAULT_WS_URL;
    this.minDelay = options.reconnectMinMs ?? 1000;
    this.maxDelay = options.reconnectMaxMs ?? 5000;
    this.reconnectDelay = this.minDelay;
    this.factory = options.webSocketFactory ?? ((url) => new WebSocket(url));
  }

  get status(): ConnectionStatus {
    return this._status;
  }

  start(): void {
    this.stopped = false;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.socket) {
      const s = this.socket;
      this.socket = null;
      s.onopen = s.onclose = s.onerror = s.onmessage = null;
      try {
        s.close();
      } catch {
        /* 忽略 */
      }
    }
    this.setStatus('disconnected', '已停止');
  }

  send(cmd: SimCommand): void {
    const json = serializeCommand(cmd);
    if (this.socket && this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(json);
    } else {
      this.pending.set(cmd.type, cmd);
    }
  }

  onState(listener: StateListener): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  onStatus(listener: StatusListener): () => void {
    this.statusListeners.add(listener);
    listener(this._status);
    return () => this.statusListeners.delete(listener);
  }

  private connect(): void {
    if (this.stopped || this.socket) return;
    this.setStatus('connecting', `連線中 ${this.url}`);
    let ws: WebSocket;
    try {
      ws = this.factory(this.url);
    } catch (err) {
      this.setStatus('disconnected', `無法建立連線:${String(err)}`);
      this.scheduleReconnect();
      return;
    }
    this.socket = ws;
    ws.onopen = () => {
      this.reconnectDelay = this.minDelay;
      this.setStatus('connected', this.url);
      for (const cmd of this.pending.values()) ws.send(serializeCommand(cmd));
      this.pending.clear();
    };
    ws.onmessage = (ev: MessageEvent) => {
      const data: unknown = ev.data;
      if (typeof data !== 'string') return;
      const result = parseState(data, this.lastState);
      if (!result) return;
      this.lastState = result.state;
      for (const l of this.stateListeners) l(result);
    };
    ws.onerror = () => {
      // 錯誤後必定會收到 close,統一在 onclose 處理重連
    };
    ws.onclose = (ev: CloseEvent) => {
      if (this.socket === ws) this.socket = null;
      if (this.stopped) return;
      this.setStatus('disconnected', ev.reason ? `連線中斷:${ev.reason}` : '連線中斷');
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.reconnectTimer) return;
    const delay = this.reconnectDelay;
    this.reconnectDelay = Math.min(this.maxDelay, Math.round(this.reconnectDelay * 1.7));
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect();
    }, delay);
  }

  private setStatus(status: ConnectionStatus, detail?: string): void {
    this._status = status;
    for (const l of this.statusListeners) l(status, detail);
  }
}
