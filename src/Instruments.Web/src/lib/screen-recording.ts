// 教官站畫面錄影(docs/design/screen-recording.md;需求 R6 影像擷取、R13 錄影與紀錄包同步/上限):
// 瀏覽器 getDisplayMedia(可含系統音訊)+ MediaRecorder 每 timeslice 吐一片 webm,片段依序即時 POST 到船上核心的
// MediaUploadServer(http://<核心主機>:8768/media/…),核心追加寫成練習紀錄旁的 <mediaId>.webm 與旁檔。
// 本檔不碰 DOM(依賴以 ScreenRecordingDeps 注入,vitest 以假 MediaRecorder / fetch 測試):
//  - 狀態機 idle → starting → recording → stopping → idle(失敗時 error,再 start 可重來)
//  - 上傳佇列:同一時間只傳一片、序號連續(X-Seq);網路錯誤/5xx 以指數退避重試(maxRetries 次),核心 409/404/410 為致命錯誤;
//    413(超過核心 --media-max-mb)→ 標 truncated、停止錄影並送 stop;離線緩衝(尚未上傳的片段)超過 maxBufferedBytes 時停止錄影並標錯誤
//    (不丟片段:丟片段會讓 webm 損壞;寧可提早結束)。
//  - stop:MediaRecorder.stop → 等最後一片與 onstop → 佇列送完 → POST stop(帶 MediaRecorder 量到的長度)→ 以核心回的旁檔做摘要。
// 完全離線;只用核心主機的本機/直連網段 HTTP。

export type RecordingState = 'idle' | 'starting' | 'recording' | 'stopping' | 'error';

/** 一段完成(或失敗)的錄影摘要(寫進教官站紀錄 recordings[];講評報告列出) */
export interface RecordingSummary {
  mediaId: string;
  /** 核心寫的檔名 <mediaId>.webm(未成功開始時無) */
  file?: string;
  station: string;
  startedUtc: string;
  stoppedUtc?: string;
  /** 長度(秒;MediaRecorder 量到的;核心旁檔 durationS) */
  durationS?: number;
  /** 已上傳並由核心確認的位元組數 */
  uploadedBytes: number;
  chunks: number;
  /** 超過核心上傳上限,檔案只含上限內的片段 */
  truncated: boolean;
  /** 失敗原因(上傳失敗、授權被拒、緩衝超限…);成功時無 */
  error?: string;
  /** 核心回報的對應紀錄檔名 */
  record?: string;
  /** 核心旁檔的 SHA-256(核對紀錄包用) */
  sha256?: string;
}

export interface RecordingStatus {
  state: RecordingState;
  mediaId: string | null;
  startedUtc: string | null;
  uploadedBytes: number;
  /** 尚未上傳的片段位元組(離線緩衝) */
  queuedBytes: number;
  chunks: number;
  retries: number;
  truncated: boolean;
  error: string | null;
  withAudio: boolean;
}

export interface RecordingSettings {
  /** 開始練習(載入情境成功 / 簡化模式「開始練習」)時自動開始錄影;練習結束時自動停止 */
  autoStart: boolean;
  /** getDisplayMedia 一併擷取系統音訊(瀏覽器分享對話框須勾「分享系統音訊」) */
  withAudio: boolean;
}

export function idleRecordingStatus(): RecordingStatus {
  return { state: 'idle', mediaId: null, startedUtc: null, uploadedBytes: 0, queuedBytes: 0, chunks: 0, retries: 0, truncated: false, error: null, withAudio: true };
}

export const RECORDING_SETTINGS_KEY = 'simosa-brm:instructor:recording';
export const DEFAULT_MEDIA_PORT = 8768;

export function defaultRecordingSettings(): RecordingSettings {
  return { autoStart: false, withAudio: true };
}

export function loadRecordingSettings(storage: Pick<Storage, 'getItem'> | null = typeof localStorage === 'undefined' ? null : localStorage): RecordingSettings {
  const s = defaultRecordingSettings();
  try {
    const raw = storage?.getItem(RECORDING_SETTINGS_KEY);
    if (!raw) return s;
    const obj = JSON.parse(raw) as Partial<RecordingSettings>;
    if (typeof obj.autoStart === 'boolean') s.autoStart = obj.autoStart;
    if (typeof obj.withAudio === 'boolean') s.withAudio = obj.withAudio;
  } catch {
    /* 損壞或無法存取:預設 */
  }
  return s;
}

export function saveRecordingSettings(s: RecordingSettings, storage: Pick<Storage, 'setItem'> | null = typeof localStorage === 'undefined' ? null : localStorage): void {
  try {
    storage?.setItem(RECORDING_SETTINGS_KEY, JSON.stringify(s));
  } catch {
    /* 配額或私密視窗:略過 */
  }
}

/**
 * 錄影上傳端根網址:?media= 明示優先;否則由狀態 WebSocket 網址推得(ws://host:8765 → http://host:8768;wss → https)。
 * 推不出時用 http://localhost:8768。回傳不含結尾斜線。
 */
export function mediaBaseUrl(wsUrl: string, override?: string | null, port = DEFAULT_MEDIA_PORT): string {
  if (override && override.trim()) return override.trim().replace(/\/+$/, '');
  try {
    const u = new URL(wsUrl);
    const scheme = u.protocol === 'wss:' ? 'https:' : 'http:';
    return `${scheme}//${u.hostname.includes(':') ? `[${u.hostname}]` : u.hostname}:${port}`;
  } catch {
    return `http://localhost:${port}`;
  }
}

// ------------------------------------------------------------ 可注入的瀏覽器依賴
export interface MediaTrackLike {
  kind: string;
  stop(): void;
  onended: (() => void) | null;
}

export interface MediaStreamLike {
  getTracks(): MediaTrackLike[];
}

export interface MediaRecorderLike {
  readonly state: 'inactive' | 'recording' | 'paused';
  readonly mimeType: string;
  ondataavailable: ((ev: { data: Blob }) => void) | null;
  onstop: (() => void) | null;
  onerror: ((ev: unknown) => void) | null;
  start(timesliceMs?: number): void;
  stop(): void;
}

export interface UploadResponse {
  ok: boolean;
  status: number;
  json(): Promise<unknown>;
}

export interface ScreenRecordingDeps {
  fetch: (url: string, init: { method: string; headers?: Record<string, string>; body?: Blob | string }) => Promise<UploadResponse>;
  getDisplayMedia: (constraints: { video: Record<string, unknown>; audio: boolean }) => Promise<MediaStreamLike>;
  createRecorder: (stream: MediaStreamLike, mimeType: string) => MediaRecorderLike;
  isTypeSupported: (mime: string) => boolean;
  now: () => Date;
  delay: (ms: number) => Promise<void>;
}

/** 瀏覽器實作;不支援 getDisplayMedia / MediaRecorder(舊瀏覽器、非安全來源、測試環境)時回傳 null。 */
export function browserDeps(): ScreenRecordingDeps | null {
  if (typeof navigator === 'undefined' || typeof MediaRecorder === 'undefined' || typeof fetch === 'undefined') return null;
  const md = navigator.mediaDevices as (MediaDevices & { getDisplayMedia?: (c: DisplayMediaStreamOptions) => Promise<MediaStream> }) | undefined;
  if (!md || typeof md.getDisplayMedia !== 'function') return null;
  return {
    fetch: (url, init) => fetch(url, init),
    getDisplayMedia: (c) => md.getDisplayMedia!(c as DisplayMediaStreamOptions) as unknown as Promise<MediaStreamLike>,
    createRecorder: (stream, mimeType) => new MediaRecorder(stream as unknown as MediaStream, mimeType ? { mimeType } : undefined) as unknown as MediaRecorderLike,
    isTypeSupported: (m) => MediaRecorder.isTypeSupported(m),
    now: () => new Date(),
    delay: (ms) => new Promise((r) => setTimeout(r, ms)),
  };
}

/** 測試可換掉瀏覽器依賴(instructor-app 以此建立控制器)。 */
export const recordingDepsProvider: { create: () => ScreenRecordingDeps | null } = { create: browserDeps };

export const PREFERRED_MIMES = ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm'];

export function pickMime(isTypeSupported: (m: string) => boolean): string {
  for (const m of PREFERRED_MIMES) {
    try {
      if (isTypeSupported(m)) return m;
    } catch {
      /* 某些瀏覽器對不合法字串丟例外 */
    }
  }
  return '';
}

// ------------------------------------------------------------ 控制器
export interface ScreenRecordingOptions {
  /** MediaRecorder 每片長度(ms;預設 2000) */
  timesliceMs: number;
  /** 離線緩衝上限(尚未上傳的片段總位元組;預設 64 MB);超過即停止錄影並標錯誤 */
  maxBufferedBytes: number;
  /** 單片重試次數上限(網路錯誤 / 5xx;預設 8,退避 0.5 s × 2^n 最長 10 s) */
  maxRetries: number;
  retryBaseMs: number;
  retryMaxMs: number;
  /** 等 MediaRecorder onstop 的上限(ms) */
  stopTimeoutMs: number;
}

export interface StartRecordingOptions {
  /** 練習識別(情境 id@開始時刻;只作關聯提示) */
  exerciseId: string;
  /** 核心正在寫的紀錄路徑(教官站通常不知道;核心會自己填) */
  recordPath?: string;
  station: string;
  withAudio?: boolean;
}

interface QueuedChunk {
  seq: number;
  blob: Blob;
  attempts: number;
}

const DEFAULTS: ScreenRecordingOptions = { timesliceMs: 2000, maxBufferedBytes: 64 * 1024 * 1024, maxRetries: 8, retryBaseMs: 500, retryMaxMs: 10_000, stopTimeoutMs: 5000 };

export class ScreenRecordingController {
  readonly options: ScreenRecordingOptions;
  readonly summaries: RecordingSummary[] = [];

  private state: RecordingState = 'idle';
  private mediaId: string | null = null;
  private startedUtc: string | null = null;
  private startedAtMs = 0;
  private station = '';
  private recordFile: string | undefined;
  private uploadedBytes = 0;
  private queuedBytes = 0;
  private chunks = 0;
  private retries = 0;
  private truncated = false;
  private error: string | null = null;
  private withAudio = true;
  private nextSeq = 0;
  private readonly queue: QueuedChunk[] = [];
  private pumping = false;
  private stream: MediaStreamLike | null = null;
  private recorder: MediaRecorderLike | null = null;
  private stopping: Promise<RecordingSummary | null> | null = null;
  private recorderStopped: (() => void) | null = null;
  private queueIdle: Array<() => void> = [];
  private fatal: string | null = null;
  private readonly listeners = new Set<(s: RecordingStatus) => void>();
  private disposed = false;

  constructor(
    readonly baseUrl: string,
    private readonly deps: ScreenRecordingDeps,
    options: Partial<ScreenRecordingOptions> = {},
  ) {
    this.options = { ...DEFAULTS, ...options };
  }

  get status(): RecordingStatus {
    return {
      state: this.state,
      mediaId: this.mediaId,
      startedUtc: this.startedUtc,
      uploadedBytes: this.uploadedBytes,
      queuedBytes: this.queuedBytes,
      chunks: this.chunks,
      retries: this.retries,
      truncated: this.truncated,
      error: this.error,
      withAudio: this.withAudio,
    };
  }

  get active(): boolean {
    return this.state === 'starting' || this.state === 'recording' || this.state === 'stopping';
  }

  onStatus(cb: (s: RecordingStatus) => void): () => void {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  }

  private emit(): void {
    const s = this.status;
    for (const l of this.listeners) l(s);
  }

  private setState(state: RecordingState): void {
    this.state = state;
    this.emit();
  }

  /** 開始錄影:取得畫面 → POST start → MediaRecorder.start(timeslice)。失敗時 state = error 並回傳 false(可再 start)。 */
  async start(opts: StartRecordingOptions): Promise<boolean> {
    if (this.disposed) return false;
    if (this.active) return false;
    this.resetSession();
    this.withAudio = opts.withAudio ?? true;
    this.station = opts.station;
    this.setState('starting');
    let stream: MediaStreamLike;
    try {
      stream = await this.deps.getDisplayMedia({ video: { frameRate: 15 }, audio: this.withAudio });
    } catch (e) {
      return this.failStart(describeError(e, '取得畫面失敗(使用者取消或瀏覽器拒絕分享畫面)'));
    }
    this.stream = stream;
    const mime = pickMime(this.deps.isTypeSupported);
    let recorder: MediaRecorderLike;
    try {
      recorder = this.deps.createRecorder(stream, mime);
    } catch (e) {
      this.releaseStream();
      return this.failStart(describeError(e, '建立 MediaRecorder 失敗'));
    }
    // POST start
    try {
      const body: Record<string, string> = { exerciseId: opts.exerciseId, station: opts.station, mime: recorder.mimeType || mime };
      if (opts.recordPath) body['recordPath'] = opts.recordPath;
      const res = await this.deps.fetch(`${this.baseUrl}/media/start`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const json = (await safeJson(res)) as Record<string, unknown>;
      if (!res.ok || typeof json['mediaId'] !== 'string') {
        this.releaseStream();
        return this.failStart(`核心拒絕開始錄影(HTTP ${res.status}${typeof json['error'] === 'string' ? `:${json['error']}` : ''})`);
      }
      this.mediaId = json['mediaId'];
      this.recordFile = typeof json['record'] === 'string' ? json['record'] : undefined;
    } catch (e) {
      this.releaseStream();
      return this.failStart(describeError(e, `無法連線錄影上傳端 ${this.baseUrl}(核心未啟動、--no-media 或防火牆)`));
    }
    if (this.disposed) {
      this.releaseStream();
      return false;
    }
    this.recorder = recorder;
    recorder.ondataavailable = (ev) => this.onData(ev.data);
    recorder.onstop = () => this.onRecorderStop();
    recorder.onerror = (ev) => this.onFatal(describeError(ev, 'MediaRecorder 錯誤'));
    for (const t of stream.getTracks()) {
      // 使用者按瀏覽器的「停止分享」:視同停止錄影
      t.onended = () => {
        if (this.state === 'recording') void this.stop('shareEnded');
      };
    }
    const now = this.deps.now();
    this.startedUtc = now.toISOString();
    this.startedAtMs = now.getTime();
    try {
      recorder.start(this.options.timesliceMs);
    } catch (e) {
      this.releaseStream();
      this.recorder = null;
      return this.failStart(describeError(e, 'MediaRecorder 無法開始'));
    }
    this.setState('recording');
    return true;
  }

  /** 停止錄影並送 stop;回傳摘要(未在錄影時 null)。重複呼叫回同一個 Promise。 */
  stop(reason = 'client'): Promise<RecordingSummary | null> {
    if (this.stopping) return this.stopping;
    if (this.state !== 'recording' && this.state !== 'starting') return Promise.resolve(null);
    this.stopping = this.doStop(reason).finally(() => {
      this.stopping = null;
    });
    return this.stopping;
  }

  private async doStop(reason: string): Promise<RecordingSummary | null> {
    this.setState('stopping');
    const recorder = this.recorder;
    if (recorder && recorder.state !== 'inactive') {
      const stopped = new Promise<void>((resolve) => (this.recorderStopped = resolve));
      try {
        recorder.stop();
      } catch {
        this.recorderStopped?.();
      }
      await Promise.race([stopped, this.deps.delay(this.options.stopTimeoutMs)]);
      this.recorderStopped = null;
    }
    this.releaseStream();
    // 佇列送完(或致命錯誤中止)
    await this.drain();
    const durationS = Math.max(0, (this.deps.now().getTime() - this.startedAtMs) / 1000);
    const summary: RecordingSummary = {
      mediaId: this.mediaId ?? '',
      station: this.station,
      startedUtc: this.startedUtc ?? this.deps.now().toISOString(),
      uploadedBytes: this.uploadedBytes,
      chunks: this.chunks,
      truncated: this.truncated,
    };
    if (this.recordFile) summary.record = this.recordFile;
    if (this.mediaId) {
      try {
        const res = await this.deps.fetch(`${this.baseUrl}/media/stop/${this.mediaId}`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ durationS, reason: this.fatal ? 'error' : reason }),
        });
        const json = (await safeJson(res)) as Record<string, unknown>;
        if (res.ok) {
          if (typeof json['file'] === 'string') summary.file = json['file'];
          if (typeof json['stoppedUtc'] === 'string') summary.stoppedUtc = json['stoppedUtc'];
          if (typeof json['durationS'] === 'number') summary.durationS = json['durationS'];
          if (typeof json['sizeBytes'] === 'number') summary.uploadedBytes = json['sizeBytes'];
          if (typeof json['chunks'] === 'number') summary.chunks = json['chunks'];
          if (typeof json['sha256'] === 'string') summary.sha256 = json['sha256'];
          if (json['truncated'] === true) summary.truncated = true;
          const rec = json['record'];
          const recFile = rec && typeof rec === 'object' ? (rec as Record<string, unknown>)['file'] : undefined;
          if (typeof recFile === 'string') summary.record = recFile;
        } else {
          this.fatal ??= `核心 stop 回應 HTTP ${res.status}${typeof json['error'] === 'string' ? `:${json['error']}` : ''}`;
        }
      } catch (e) {
        this.fatal ??= describeError(e, '送 stop 失敗(核心無法連線);錄影檔由核心在結束時定稿');
      }
    }
    summary.stoppedUtc ??= this.deps.now().toISOString();
    summary.durationS ??= durationS;
    if (this.fatal) summary.error = this.fatal;
    this.summaries.push(summary);
    this.error = this.fatal;
    this.recorder = null;
    this.setState(this.fatal ? 'error' : 'idle');
    return summary;
  }

  dispose(): void {
    this.disposed = true;
    if (this.state === 'recording' || this.state === 'starting') void this.stop('dispose');
    this.listeners.clear();
  }

  // ------------------------------------------------------------ 內部
  private resetSession(): void {
    this.mediaId = null;
    this.startedUtc = null;
    this.recordFile = undefined;
    this.uploadedBytes = 0;
    this.queuedBytes = 0;
    this.chunks = 0;
    this.retries = 0;
    this.truncated = false;
    this.error = null;
    this.fatal = null;
    this.nextSeq = 0;
    this.queue.length = 0;
  }

  private failStart(message: string): false {
    this.error = message;
    this.fatal = message;
    this.recorder = null;
    this.summaries.push({ mediaId: this.mediaId ?? '', station: this.station, startedUtc: this.deps.now().toISOString(), uploadedBytes: 0, chunks: 0, truncated: false, error: message });
    this.setState('error');
    return false;
  }

  private releaseStream(): void {
    const s = this.stream;
    this.stream = null;
    if (!s) return;
    for (const t of s.getTracks()) {
      t.onended = null;
      try {
        t.stop();
      } catch {
        /* 已停止 */
      }
    }
  }

  private onData(blob: Blob): void {
    if (!blob || blob.size === 0) return; // 空片段不佔序號
    if (this.fatal || this.truncated) return;
    this.queue.push({ seq: this.nextSeq++, blob, attempts: 0 });
    this.queuedBytes += blob.size;
    if (this.queuedBytes > this.options.maxBufferedBytes) {
      this.onFatal(`離線緩衝 ${(this.queuedBytes / 1048576).toFixed(0)} MB 超過上限 ${(this.options.maxBufferedBytes / 1048576).toFixed(0)} MB(核心長時間無法連線),停止錄影`);
      return;
    }
    this.emit();
    void this.pump();
  }

  private onRecorderStop(): void {
    this.recorderStopped?.();
  }

  /** 致命錯誤:停止 MediaRecorder、清空佇列、進入 stopping → error(仍送 stop 讓核心定稿已收到的部分)。 */
  private onFatal(message: string): void {
    if (this.fatal) return;
    this.fatal = message;
    this.error = message;
    this.queue.length = 0;
    this.queuedBytes = 0;
    this.emit();
    if (this.state === 'recording' || this.state === 'starting') void this.stop('error');
  }

  private drain(): Promise<void> {
    if (this.queue.length === 0 && !this.pumping) return Promise.resolve();
    return new Promise((resolve) => this.queueIdle.push(resolve));
  }

  private notifyIdle(): void {
    const waiters = this.queueIdle;
    this.queueIdle = [];
    for (const w of waiters) w();
  }

  private async pump(): Promise<void> {
    if (this.pumping) return;
    this.pumping = true;
    try {
      while (this.queue.length > 0 && !this.fatal) {
        const head = this.queue[0]!;
        const outcome = await this.upload(head);
        if (outcome === 'done') {
          this.queue.shift();
          this.queuedBytes -= head.blob.size;
          this.uploadedBytes += head.blob.size;
          this.chunks += 1;
          this.emit();
        } else if (outcome === 'retry') {
          head.attempts += 1;
          this.retries += 1;
          this.emit();
          if (head.attempts > this.options.maxRetries) {
            this.onFatal(`片段 ${head.seq} 上傳失敗 ${head.attempts} 次(核心無法連線),停止錄影`);
            break;
          }
          await this.deps.delay(Math.min(this.options.retryMaxMs, this.options.retryBaseMs * 2 ** (head.attempts - 1)));
        } else {
          break; // fatal / truncated 已處理
        }
      }
    } finally {
      this.pumping = false;
      if (this.queue.length === 0 || this.fatal) this.notifyIdle();
      else if (!this.fatal) void this.pump(); // 期間又有新片段
    }
  }

  private async upload(c: QueuedChunk): Promise<'done' | 'retry' | 'stop'> {
    if (!this.mediaId) {
      this.onFatal('沒有 mediaId');
      return 'stop';
    }
    let res: UploadResponse;
    try {
      res = await this.deps.fetch(`${this.baseUrl}/media/chunk/${this.mediaId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/octet-stream', 'X-Seq': String(c.seq) },
        body: c.blob,
      });
    } catch {
      return 'retry';
    }
    if (res.ok) return 'done';
    const json = (await safeJson(res)) as Record<string, unknown>;
    const detail = typeof json['error'] === 'string' ? `:${json['error']}` : '';
    if (res.status === 413) {
      // 超過核心上限:之後不再收;停止錄影並送 stop(核心旁檔標 truncated)
      this.truncated = true;
      this.queue.length = 0;
      this.queuedBytes = 0;
      this.error = `已達核心錄影上限(HTTP 413${detail}),錄影提前結束`;
      this.emit();
      if (this.state === 'recording') void this.stop('limit');
      return 'stop';
    }
    if (res.status >= 500) return 'retry';
    this.onFatal(`核心拒絕片段 ${c.seq}(HTTP ${res.status}${detail})`);
    return 'stop';
  }
}

async function safeJson(res: UploadResponse): Promise<unknown> {
  try {
    const v = await res.json();
    return v && typeof v === 'object' ? v : {};
  } catch {
    return {};
  }
}

function describeError(e: unknown, fallback: string): string {
  if (e && typeof e === 'object') {
    const name = (e as { name?: unknown }).name;
    const message = (e as { message?: unknown }).message;
    if (name === 'NotAllowedError') return '使用者取消或系統拒絕分享畫面(Windows:檢查「設定 → 隱私權 → 攝影機/螢幕擷取」與瀏覽器權限)';
    if (typeof message === 'string' && message) return `${fallback}:${message}`;
  }
  return fallback;
}

/** 位元組 → "12.3 MB"。 */
export function formatMb(bytes: number): string {
  return `${(bytes / 1048576).toFixed(bytes >= 100 * 1048576 ? 0 : 1)} MB`;
}

/** 秒 → "1 h 02 min" / "3 min 05 s"。 */
export function formatRecordingDuration(s: number | undefined): string {
  if (s === undefined || !Number.isFinite(s)) return '—';
  const total = Math.max(0, Math.floor(s));
  if (total >= 3600) return `${Math.floor(total / 3600)} h ${String(Math.floor((total % 3600) / 60)).padStart(2, '0')} min`;
  return `${Math.floor(total / 60)} min ${String(total % 60).padStart(2, '0')} s`;
}

/** 教官站紀錄 / 核心旁檔的 recordings 欄位解析(損壞元素略過)。 */
export function parseRecordings(v: unknown): RecordingSummary[] {
  if (!Array.isArray(v)) return [];
  const out: RecordingSummary[] = [];
  for (const raw of v) {
    if (!raw || typeof raw !== 'object') continue;
    const r = raw as Record<string, unknown>;
    if (typeof r['mediaId'] !== 'string' || typeof r['startedUtc'] !== 'string') continue;
    const s: RecordingSummary = {
      mediaId: r['mediaId'],
      station: typeof r['station'] === 'string' ? r['station'] : '',
      startedUtc: r['startedUtc'],
      uploadedBytes: typeof r['uploadedBytes'] === 'number' ? r['uploadedBytes'] : 0,
      chunks: typeof r['chunks'] === 'number' ? r['chunks'] : 0,
      truncated: r['truncated'] === true,
    };
    if (typeof r['file'] === 'string') s.file = r['file'];
    if (typeof r['stoppedUtc'] === 'string') s.stoppedUtc = r['stoppedUtc'];
    if (typeof r['durationS'] === 'number') s.durationS = r['durationS'];
    if (typeof r['error'] === 'string') s.error = r['error'];
    if (typeof r['record'] === 'string') s.record = r['record'];
    if (typeof r['sha256'] === 'string') s.sha256 = r['sha256'];
    out.push(s);
  }
  return out;
}
