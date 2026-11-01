// VHF 角色扮演頻道的客戶端(docs/design/vhf-roleplay.md 第 3 節):把 VhfStateMachine 的效果接上
//  - WebSocket(ws://<核心主機>:8767/;斷線自動重連,重連後重新 join);
//  - MediaRecorder 擷取(audio/webm;codecs=opus,100 ms 片段 → 二進位框);無麥克風權限時只能文字(micStatus 顯示提示);
//  - 播放:MediaSource(SourceBuffer audio/webm;codecs=opus,逐片段 appendBuffer)→ 無法播放時退回「整段通話結束後以 Audio 播放 Blob」
//    → 都不支援時只收文字(playback = 'none')。
// 所有瀏覽器 API 以工廠注入(測試用假物件);不經核心、不進狀態雜湊。

import {
  VhfStateMachine,
  decodeAudioFrame,
  encodeAudioFrame,
  parseVhfMessage,
  serializeVhfMessage,
  type VhfDscCategory,
  type VhfEffect,
  type VhfMessage,
  type VhfPeer,
  type VhfStateMachineOptions,
} from './vhf.js';

export type VhfConnection = 'disconnected' | 'connecting' | 'connected';
export type MicStatus = 'unknown' | 'requesting' | 'granted' | 'denied' | 'unavailable';
export type PlaybackMode = 'mse' | 'blob' | 'none';

/** MediaRecorder 的最小介面(測試注入)。 */
export interface RecorderLike {
  start(timeslice?: number): void;
  stop(): void;
  ondataavailable: ((ev: { data: Blob }) => void) | null;
  onstop: (() => void) | null;
  readonly state: 'inactive' | 'recording' | 'paused';
}

/** 一段通話的播放器(每次接收建立一個)。 */
export interface PlayerLike {
  start(from: VhfPeer): void;
  append(chunk: Uint8Array): void;
  /** 發話結束:MSE 收尾(endOfStream)、Blob 模式開始播放 */
  end(): void;
  /** 中止(斷線、轉頻道) */
  abort(): void;
  setVolume(v: number): void;
}

export interface VhfClientOptions extends VhfStateMachineOptions {
  url: string;
  reconnectMinMs?: number;
  reconnectMaxMs?: number;
  webSocketFactory?: (url: string) => WebSocket;
  /** 取得麥克風(預設 navigator.mediaDevices.getUserMedia({audio:true})) */
  getUserMedia?: () => Promise<MediaStream>;
  recorderFactory?: (stream: MediaStream) => RecorderLike;
  playerFactory?: (mode: PlaybackMode) => PlayerLike;
  /** 播放模式偵測(預設依 MediaSource.isTypeSupported) */
  detectPlayback?: () => PlaybackMode;
}

export const VHF_MIME = 'audio/webm;codecs=opus';
export const VHF_TIMESLICE_MS = 100;

/** MediaSource 逐片段播放:每次通話一個 Audio + MediaSource;片段依序 appendBuffer(sequence 模式),結束時 endOfStream。 */
export class MsePlayer implements PlayerLike {
  private audio: HTMLAudioElement | null = null;
  private source: MediaSource | null = null;
  private buffer: SourceBuffer | null = null;
  private queue: Uint8Array[] = [];
  private ended = false;
  private volume = 1;

  start(): void {
    this.abort();
    this.ended = false;
    const ms = new MediaSource();
    const audio = new Audio();
    audio.volume = this.volume;
    audio.src = URL.createObjectURL(ms);
    ms.addEventListener('sourceopen', () => {
      if (this.source !== ms) return;
      try {
        const sb = ms.addSourceBuffer(VHF_MIME);
        sb.mode = 'sequence';
        sb.addEventListener('updateend', () => this.pump());
        this.buffer = sb;
        this.pump();
      } catch {
        this.abort();
      }
    });
    this.source = ms;
    this.audio = audio;
    void audio.play().catch(() => {
      /* 自動播放被擋:使用者第一次互動後才會有聲音 */
    });
  }

  append(chunk: Uint8Array): void {
    this.queue.push(chunk);
    this.pump();
  }

  end(): void {
    this.ended = true;
    this.pump();
  }

  abort(): void {
    this.queue = [];
    if (this.audio) {
      try {
        this.audio.pause();
        if (this.audio.src) URL.revokeObjectURL(this.audio.src);
        this.audio.src = '';
      } catch {
        /* 忽略 */
      }
    }
    this.audio = null;
    this.source = null;
    this.buffer = null;
  }

  setVolume(v: number): void {
    this.volume = Math.max(0, Math.min(1, v));
    if (this.audio) this.audio.volume = this.volume;
  }

  private pump(): void {
    const sb = this.buffer;
    const ms = this.source;
    if (!sb || !ms || ms.readyState !== 'open' || sb.updating) return;
    const next = this.queue.shift();
    if (next) {
      try {
        // SourceBuffer 需要 ArrayBuffer 型別的視圖:複製成獨立緩衝區(片段本來就是轉送框的子視圖)
        sb.appendBuffer(new Uint8Array(next).buffer as ArrayBuffer);
      } catch {
        this.abort();
      }
      return;
    }
    if (this.ended) {
      try {
        ms.endOfStream();
      } catch {
        /* 已結束 */
      }
    }
  }
}

/** 退回模式:整段通話收齊後以 Audio 播放 Blob(延遲 = 通話長度;適用無 MSE 的環境)。 */
export class BlobPlayer implements PlayerLike {
  private chunks: Uint8Array[] = [];
  private audio: HTMLAudioElement | null = null;
  private volume = 1;

  start(): void {
    this.abort();
    this.chunks = [];
  }

  append(chunk: Uint8Array): void {
    this.chunks.push(chunk);
  }

  end(): void {
    if (this.chunks.length === 0) return;
    const blob = new Blob(this.chunks as BlobPart[], { type: VHF_MIME });
    this.chunks = [];
    const audio = new Audio(URL.createObjectURL(blob));
    audio.volume = this.volume;
    audio.onended = () => URL.revokeObjectURL(audio.src);
    this.audio = audio;
    void audio.play().catch(() => {
      /* 自動播放被擋 */
    });
  }

  abort(): void {
    this.chunks = [];
    if (this.audio) {
      try {
        this.audio.pause();
      } catch {
        /* 忽略 */
      }
      this.audio = null;
    }
  }

  setVolume(v: number): void {
    this.volume = Math.max(0, Math.min(1, v));
    if (this.audio) this.audio.volume = this.volume;
  }
}

class NullPlayer implements PlayerLike {
  start(): void {}
  append(): void {}
  end(): void {}
  abort(): void {}
  setVolume(): void {}
}

/** 依瀏覽器能力選播放模式:MSE(Chromium/WebView2 支援 audio/webm;codecs=opus)> Blob > 無。 */
export function detectPlaybackMode(): PlaybackMode {
  try {
    if (typeof MediaSource !== 'undefined' && typeof MediaSource.isTypeSupported === 'function' && MediaSource.isTypeSupported(VHF_MIME)) return 'mse';
  } catch {
    /* 無 MSE */
  }
  return typeof Audio !== 'undefined' && typeof Blob !== 'undefined' ? 'blob' : 'none';
}

function defaultPlayer(mode: PlaybackMode): PlayerLike {
  return mode === 'mse' ? new MsePlayer() : mode === 'blob' ? new BlobPlayer() : new NullPlayer();
}

export class VhfClient {
  readonly machine: VhfStateMachine;
  readonly url: string;
  readonly playback: PlaybackMode;
  private socket: WebSocket | null = null;
  private _connection: VhfConnection = 'disconnected';
  private _mic: MicStatus = 'unknown';
  private _micError = '';
  private stream: MediaStream | null = null;
  private recorder: RecorderLike | null = null;
  /** 進行中的片段轉換(Blob.arrayBuffer → 送出);stopCapture 等它們完成後才送 pttEnd(最後一片不遺失) */
  private readonly inflight = new Set<Promise<void>>();
  private player: PlayerLike;
  private volume = 1;
  private stopped = true;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private tickTimer: ReturnType<typeof setInterval> | null = null;
  private reconnectDelay: number;
  private readonly minDelay: number;
  private readonly maxDelay: number;
  private readonly listeners = new Set<() => void>();
  private readonly factory: (url: string) => WebSocket;
  private readonly getUserMedia: (() => Promise<MediaStream>) | null;
  private readonly recorderFactory: (stream: MediaStream) => RecorderLike;
  private readonly playerFactory: (mode: PlaybackMode) => PlayerLike;
  /** 收到的音訊框數(診斷/測試) */
  audioFramesReceived = 0;
  /** 送出的音訊框數 */
  audioFramesSent = 0;

  constructor(opts: VhfClientOptions) {
    this.url = opts.url;
    this.machine = new VhfStateMachine(opts);
    this.minDelay = opts.reconnectMinMs ?? 1000;
    this.maxDelay = opts.reconnectMaxMs ?? 5000;
    this.reconnectDelay = this.minDelay;
    this.factory = opts.webSocketFactory ?? ((url) => new WebSocket(url));
    const gum = opts.getUserMedia;
    this.getUserMedia = gum ?? (typeof navigator !== 'undefined' && navigator.mediaDevices?.getUserMedia ? () => navigator.mediaDevices.getUserMedia({ audio: true }) : null);
    if (!this.getUserMedia) this._mic = 'unavailable';
    this.recorderFactory = opts.recorderFactory ?? ((stream) => new MediaRecorder(stream, { mimeType: VHF_MIME, audioBitsPerSecond: 24000 }) as unknown as RecorderLike);
    this.playback = (opts.detectPlayback ?? detectPlaybackMode)();
    this.playerFactory = opts.playerFactory ?? defaultPlayer;
    this.player = this.playerFactory(this.playback);
  }

  get connection(): VhfConnection {
    return this._connection;
  }
  get mic(): MicStatus {
    return this._mic;
  }
  get micError(): string {
    return this._micError;
  }
  /** 可發話:已加入且有麥克風(文字不受此限制) */
  get canTransmit(): boolean {
    return this.machine.joined && this._mic === 'granted';
  }

  /** 任何狀態變化(連線、狀態機、麥克風)通知(面板重繪)。 */
  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private notify(): void {
    for (const l of this.listeners) l();
  }

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;
    this.connect();
    this.tickTimer = setInterval(() => {
      if (this.machine.tick()) this.notify();
    }, 250);
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.tickTimer) {
      clearInterval(this.tickTimer);
      this.tickTimer = null;
    }
    this.run(this.machine.disconnected());
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
    for (const t of this.stream?.getTracks() ?? []) t.stop();
    this.stream = null;
    this.setConnection('disconnected');
  }

  setChannel(channel: string): void {
    this.run(this.machine.setChannel(channel));
  }

  setWatch(channel: string | null): void {
    this.run(this.machine.setWatch(channel));
  }

  setVolume(v: number): void {
    this.volume = v;
    this.player.setVolume(v);
    this.notify();
  }

  get volumeLevel(): number {
    return this.volume;
  }

  /** PTT 目前是否按住(授權對話框期間放開 → 授權完成後不再開始發話;審查第二十一輪 major)。 */
  private pttPressed = false;

  /** 按下 PTT:第一次先取得麥克風權限(非同步);無權限時不發話、只提示;等待授權期間放開則不發話。 */
  async pttDown(): Promise<void> {
    this.pttPressed = true;
    if (this.machine.state !== 'idle' || !this.machine.joined) return;
    if (this._mic !== 'granted') {
      const ok = await this.requestMic();
      if (!ok || !this.pttPressed || this.machine.state !== 'idle') return;
    }
    if (!this.pttPressed) return;
    await this.run(this.machine.pttDown());
  }

  async pttUp(): Promise<void> {
    this.pttPressed = false;
    await this.run(this.machine.pttUp());
  }

  sendText(text: string): void {
    this.run(this.machine.sendText(text));
  }

  sendDsc(category: VhfDscCategory, text = ''): void {
    this.run(this.machine.sendDsc(category, text));
  }

  /** 麥克風授權(可由面板預先呼叫);成功 true。 */
  async requestMic(): Promise<boolean> {
    if (this._mic === 'granted') return true;
    if (!this.getUserMedia) {
      this._mic = 'unavailable';
      this._micError = '此環境沒有麥克風 API(需 https 或 localhost/127.0.0.1;安裝文件第 2.7 節)';
      this.notify();
      return false;
    }
    this._mic = 'requesting';
    this.notify();
    try {
      this.stream = await this.getUserMedia();
      this._mic = 'granted';
      this._micError = '';
    } catch (err) {
      this._mic = 'denied';
      this._micError = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
    }
    this.notify();
    return this._mic === 'granted';
  }

  // ---------------------------------------------------------------- 效果
  private async run(effects: VhfEffect[]): Promise<void> {
    // 沒有效果也通知:roster/channel 確認等只改狀態機內部資料,面板仍需重繪
    for (const e of effects) {
      switch (e.type) {
        case 'send':
          this.send(e.message);
          break;
        case 'startCapture':
          this.startCapture();
          break;
        case 'stopCapture':
          await this.stopCapture();
          break;
        case 'startPlayback':
          this.player.start(e.from);
          break;
        case 'stopPlayback':
          this.player.end();
          break;
        case 'log':
          break;
      }
    }
    this.notify();
  }

  private send(m: VhfMessage): void {
    if (this.socket && this.socket.readyState === WebSocket.OPEN) this.socket.send(serializeVhfMessage(m));
  }

  private startCapture(): void {
    if (!this.stream) return;
    let rec: RecorderLike;
    try {
      rec = this.recorderFactory(this.stream);
    } catch (err) {
      this._mic = 'unavailable';
      this._micError = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
      void this.run(this.machine.pttUp());
      return;
    }
    rec.ondataavailable = (ev) => {
      if (this.recorder !== rec || !ev.data || ev.data.size === 0) return;
      const p = ev.data
        .arrayBuffer()
        .then((buf) => {
          // 最後一片在 stop 後才到:仍送出(伺服器在 pttEnd 之前仍轉送)
          if (this.recorder !== rec || !this.socket || this.socket.readyState !== WebSocket.OPEN || !this.machine.id) return;
          this.socket.send(encodeAudioFrame(this.machine.id, new Uint8Array(buf)));
          this.audioFramesSent++;
        })
        .catch(() => undefined)
        .finally(() => this.inflight.delete(p));
      this.inflight.add(p);
    };
    this.recorder = rec;
    rec.start(VHF_TIMESLICE_MS);
  }

  /** 停止擷取並等最後一片送出(onstop 後等進行中的片段轉換;onstop 最多等 600 ms),之後才送 pttEnd。 */
  private stopCapture(): Promise<void> {
    const rec = this.recorder;
    if (!rec) return Promise.resolve();
    return new Promise<void>((resolve) => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        void Promise.all([...this.inflight]).then(() => {
          if (this.recorder === rec) this.recorder = null;
          resolve();
        });
      };
      const timer = setTimeout(finish, 600);
      rec.onstop = finish;
      try {
        if (rec.state !== 'inactive') rec.stop();
        else finish();
      } catch {
        finish();
      }
    });
  }

  // ---------------------------------------------------------------- 連線
  private connect(): void {
    if (this.stopped || this.socket) return;
    this.setConnection('connecting');
    let ws: WebSocket;
    try {
      ws = this.factory(this.url);
    } catch {
      this.setConnection('disconnected');
      this.scheduleReconnect();
      return;
    }
    ws.binaryType = 'arraybuffer';
    this.socket = ws;
    ws.onopen = () => {
      this.reconnectDelay = this.minDelay;
      this.setConnection('connected');
      void this.run(this.machine.connected());
    };
    ws.onmessage = (ev: MessageEvent) => {
      const data: unknown = ev.data;
      if (typeof data === 'string') {
        const m = parseVhfMessage(data);
        if (m) void this.run(this.machine.handleMessage(m));
        return;
      }
      const buf = data instanceof ArrayBuffer ? data : data instanceof Uint8Array ? data : null;
      if (!buf) return;
      const frame = decodeAudioFrame(buf);
      if (!frame) return;
      this.audioFramesReceived++;
      if (this.machine.acceptsAudio(frame.id)) this.player.append(frame.payload);
    };
    ws.onerror = () => {
      /* onclose 會接著發生 */
    };
    ws.onclose = () => {
      if (this.socket === ws) this.socket = null;
      if (this.stopped) return;
      this.run(this.machine.disconnected());
      this.player.abort();
      this.setConnection('disconnected');
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

  private setConnection(c: VhfConnection): void {
    this._connection = c;
    this.notify();
  }
}
