// 雷達頁狀態中樞(不依賴 DOM,可單元測試):訂閱資料來源(真實服務或示範)、保存最新 status/tracks/事件/自船歷史、
// 操作員顯示設定(PpiSettings)、選取目標、危險/失追確認;把影像幀轉給畫面;依顯示量程與偏心決定伺服器影像量程。

import type { SpokeFrame } from './frame.js';
import {
  DISPLAY_RANGES_NM,
  stepRange,
  type RadarAck,
  type RadarCommand,
  type RadarMessage,
  type RadarStatus,
  type RadarTracks,
} from './protocol.js';
import type { RadarConnStatus, RadarSource } from './radar-client.js';
import { pendingAlarms, pruneAcknowledged } from './arpa.js';
import {
  defaultSettings,
  effectiveMotion,
  extrapolateOwn,
  videoRangeFor,
  type OwnNow,
  type PpiSettings,
  type TmAnchor,
} from './ppi-model.js';
import type { Motion } from './geometry.js';

export interface FrameInfo {
  radarId: string;
  t: number;
  scan: number;
  headingDeg: number;
  cogDeg: number;
  headingValid: boolean;
  transmitting: boolean;
  degraded: boolean;
  rangeM: number;
  /** 本地接收時間(ms) */
  wall: number;
}

export interface LogEntry {
  t: number;
  kind: string;
  message: string;
  targetId?: number;
  error?: boolean;
}

const MAX_LOG = 60;
const OWN_HISTORY_S = 720;

type Listener = () => void;

export class RadarStore {
  source: RadarSource | null = null;
  conn: RadarConnStatus = 'disconnected';
  connDetail = '';
  status: RadarStatus | null = null;
  tracks: RadarTracks | null = null;
  frame: FrameInfo | null = null;
  log: LogEntry[] = [];
  settings: PpiSettings = defaultSettings();
  selectedId: number | null = null;
  readonly acknowledged = new Set<string>();
  tm: TmAnchor | null = null;
  ownHistory: { t: number; x: number; y: number }[] = [];
  lastError = '';
  /** 最近一次由本頁要求的伺服器影像量程(nm) */
  requestedVideoRange: number | null = null;

  private unsub: Array<() => void> = [];
  private listeners = new Set<Listener>();
  private frameListeners = new Set<(f: SpokeFrame) => void>();
  private pending = new Map<string, RadarCommand>();
  private notifyQueued = false;

  attach(src: RadarSource): void {
    this.detach();
    this.source = src;
    this.unsub.push(
      src.onConn((s, d) => {
        this.conn = s;
        this.connDetail = d ?? '';
        if (s !== 'connected' && s !== 'demo') this.frame = null;
        this.notify();
      }),
      src.onMessage((m) => this.handleMessage(m)),
      src.onFrame((f) => this.handleFrame(f)),
    );
  }

  detach(): void {
    for (const u of this.unsub) u();
    this.unsub = [];
    this.source = null;
  }

  /** UI 訂閱(合併同一微任務內的多次變更) */
  subscribe(l: Listener): () => void {
    this.listeners.add(l);
    return () => this.listeners.delete(l);
  }

  onFrame(l: (f: SpokeFrame) => void): () => void {
    this.frameListeners.add(l);
    return () => this.frameListeners.delete(l);
  }

  notify(): void {
    if (this.notifyQueued) return;
    this.notifyQueued = true;
    queueMicrotask(() => {
      this.notifyQueued = false;
      for (const l of this.listeners) l();
    });
  }

  // -------------------------------------------------------------------------------------------
  // 接收

  handleFrame(f: SpokeFrame): void {
    this.frame = {
      radarId: f.radarId,
      t: f.t,
      scan: f.scan,
      headingDeg: f.headingDeg,
      cogDeg: f.cogDeg,
      headingValid: f.headingValid,
      transmitting: f.transmitting,
      degraded: f.degraded,
      rangeM: f.rangeM,
      wall: typeof performance !== 'undefined' ? performance.now() : Date.now(),
    };
    for (const l of this.frameListeners) l(f);
  }

  handleMessage(m: RadarMessage): void {
    switch (m.type) {
      case 'status':
        this.applyStatus(m);
        break;
      case 'tracks':
        this.tracks = m;
        this.ownHistory.push({ t: m.t, x: m.ownShip.x, y: m.ownShip.y });
        if (this.ownHistory.length > 2 && this.ownHistory[0]!.t > m.t) this.ownHistory = [{ t: m.t, x: m.ownShip.x, y: m.ownShip.y }];
        while (this.ownHistory.length > 0 && this.ownHistory[0]!.t < m.t - OWN_HISTORY_S) this.ownHistory.shift();
        pruneAcknowledged(m.targets, this.acknowledged);
        if (this.selectedId !== null && !m.targets.some((t) => t.id === this.selectedId)) this.selectedId = null;
        break;
      case 'event':
        this.pushLog({ ...m.event });
        if (m.event.kind === 'radarFailure' || m.event.kind === 'headingLost') this.tm = null;
        break;
      case 'ack':
        this.handleAck(m);
        break;
    }
    this.notify();
  }

  private applyStatus(st: RadarStatus): void {
    const first = this.status === null;
    this.status = st;
    // 影像量程:不是本頁要求的值(第一次或其他客戶端改了)→ 顯示量程跟著伺服器
    if (first || this.requestedVideoRange === null || Math.abs(st.range_nm - this.requestedVideoRange) > 1e-9) {
      this.requestedVideoRange = st.range_nm;
      this.settings.displayRangeNm = this.displayRangeForVideo(st.range_nm);
    }
  }

  /** 伺服器影像量程 → 本頁顯示量程(偏心時取下一個較小量程,使影像涵蓋最遠圓邊) */
  private displayRangeForVideo(videoNm: number): number {
    const frac = this.videoOffcentreFraction();
    const candidates = [...DISPLAY_RANGES_NM].sort((a, b) => b - a);
    for (const d of candidates) if (videoRangeFor(d, frac) <= videoNm + 1e-9) return d;
    return DISPLAY_RANGES_NM[0]!;
  }

  private handleAck(a: RadarAck): void {
    const key = typeof a.reqId === 'string' ? a.reqId : undefined;
    const cmd = key ? this.pending.get(key) : undefined;
    if (key) this.pending.delete(key);
    if (!a.ok) {
      this.lastError = `${a.cmd}:${a.error ?? '失敗'}`;
      this.pushLog({ t: this.frame?.t ?? 0, kind: 'error', message: `指令 ${a.cmd} 失敗:${a.error ?? ''}`, error: true });
      return;
    }
    this.lastError = '';
    if (cmd?.cmd === 'acquire' && a.id !== undefined) this.selectedId = a.id;
  }

  pushLog(e: LogEntry): void {
    this.log.unshift(e);
    if (this.log.length > MAX_LOG) this.log.length = MAX_LOG;
  }

  // -------------------------------------------------------------------------------------------
  // 指令與設定

  send(cmd: RadarCommand): string | null {
    if (!this.source) return null;
    const id = this.source.send(cmd);
    if (id) this.pending.set(id, cmd);
    return id;
  }

  /** 部分更新顯示設定;影響影像量程時同步伺服器量程 */
  setSettings(patch: Partial<PpiSettings>): void {
    const prevOrient = this.settings.orientation;
    this.settings = { ...this.settings, ...patch };
    if (patch.orientation === 'course-up' && prevOrient !== 'course-up') this.settings.courseRef = this.ownNow().cog;
    if (patch.orientation !== undefined || patch.motion !== undefined) this.tm = null;
    if (patch.displayRangeNm !== undefined || patch.offcentre !== undefined || patch.motion !== undefined || patch.orientation !== undefined) {
      this.syncVideoRange();
    }
    this.notify();
  }

  /** 航向向上:重設參考航向為目前 COG */
  resetCourseUp(): void {
    this.settings = { ...this.settings, courseRef: this.ownNow().cog };
    this.tm = null;
    this.notify();
  }

  stepDisplayRange(dir: 1 | -1): void {
    this.setSettings({ displayRangeNm: stepRange(this.settings.displayRangeNm, dir) });
  }

  /** 偏心比例(決定影像量程):真運動以重設上限 0.7 計,相對運動為偏心長度 */
  videoOffcentreFraction(): number {
    if (this.motion() === 'true') return 0.7;
    return Math.hypot(this.settings.offcentre.x, this.settings.offcentre.y);
  }

  syncVideoRange(): void {
    const want = videoRangeFor(this.settings.displayRangeNm, this.videoOffcentreFraction(), this.status?.rangeScales);
    if (this.requestedVideoRange !== null && Math.abs(want - this.requestedVideoRange) < 1e-9 && this.status?.range_nm === want) return;
    this.requestedVideoRange = want;
    this.send({ cmd: 'range', value: want });
  }

  select(id: number | null): void {
    this.selectedId = id;
    this.notify();
  }

  /** 確認所有目前的危險/失追警示(停止閃爍) */
  acknowledgeAlarms(): void {
    for (const k of this.pendingAlarms()) this.acknowledged.add(k);
    this.notify();
  }

  pendingAlarms(): string[] {
    return this.tracks ? pendingAlarms(this.tracks.targets, this.acknowledged) : [];
  }

  // -------------------------------------------------------------------------------------------
  // 衍生值

  get headingValid(): boolean {
    return this.frame?.headingValid ?? this.status?.headingValid ?? true;
  }

  get transmitting(): boolean {
    if (this.status) return this.status.transmitting && (this.frame?.transmitting ?? true);
    return this.frame?.transmitting ?? false;
  }

  get simTime(): number {
    return this.frame?.t ?? this.tracks?.t ?? Number.NaN;
  }

  get headingDeg(): number {
    return this.frame?.headingDeg ?? this.tracks?.ownShip.heading ?? 0;
  }

  ownNow(): OwnNow {
    const tr = this.tracks;
    return extrapolateOwn(tr?.ownShip, tr?.t ?? 0, this.simTime, this.headingDeg);
  }

  motion(): Motion {
    return effectiveMotion(this.settings, this.ownNow(), this.headingValid);
  }
}
