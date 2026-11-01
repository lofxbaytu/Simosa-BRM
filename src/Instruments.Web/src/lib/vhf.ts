// 內建 VHF 角色扮演頻道(需求追溯表 R1「VHF/DSC」、R6「角色扮演」;docs/design/vhf-roleplay.md、src/Contracts/vhf-protocol.md):
//  - 協定編解碼:文字框 {type:"vhf", kind:…} 與二進位音訊框 [uint32 LE id 長度][id][audio/webm;codecs=opus 片段];
//  - 狀態機 VhfStateMachine(純函式、可測):idle / transmitting / receiving / busy,頻道與雙守聽(教官站 16 + 工作頻道)、
//    同頻道過濾、PTT 佔用(後發者收到 busy)、文字與 DSC 訊息列;只回傳「效果」(送訊息、開始/停止擷取與播放、記錄),
//    由 vhf-client.ts 接上 WebSocket、MediaRecorder 與播放;
//  - 講評統計 vhfSummary():由紀錄 vhf 行(SimEvent 形狀,source "vhf")算通話次數、總秒數、文字/DSC 筆數。
// 不經核心、不進狀態雜湊;所有時間為牆鐘毫秒(now()),與模擬時間無關。

import type { SimEvent } from '../types/event.js';

/** 頻道旋鈕清單(規劃書第 7.2 節;與 C# VhfRelayServer.Channels 一致)。 */
export const VHF_CHANNELS: readonly string[] = ['16', '12', '13', '14', '06', '08', '10', '67', '68', '69', '71', '72', '73', '77'] as const;
/** DSC 簡化:固定標示 70(不做真實 DSC 編碼) */
export const DSC_CHANNEL = '70';
export const DEFAULT_VHF_PORT = 8767;
export const DEFAULT_VHF_URL = `ws://localhost:${DEFAULT_VHF_PORT}/`;

export type VhfDscCategory = 'distress' | 'urgency' | 'safety' | 'routine';
export const VHF_DSC_CATEGORIES: readonly VhfDscCategory[] = ['distress', 'urgency', 'safety', 'routine'] as const;
export const DSC_CATEGORY_LABEL: Readonly<Record<VhfDscCategory, string>> = { distress: 'DISTRESS 遇險', urgency: 'URGENCY 緊急', safety: 'SAFETY 安全', routine: 'ROUTINE 例行' };

export function isVhfChannel(c: unknown): c is string {
  return typeof c === 'string' && VHF_CHANNELS.includes(c);
}

export function isDscCategory(c: unknown): c is VhfDscCategory {
  return typeof c === 'string' && (VHF_DSC_CATEGORIES as readonly string[]).includes(c);
}

/** 由狀態 WebSocket 網址推得 VHF 轉送網址(同主機、埠 8767):ws://192.168.77.11:8765 → ws://192.168.77.11:8767/。 */
export function defaultVhfUrl(wsUrl: string | undefined | null): string {
  if (!wsUrl) return DEFAULT_VHF_URL;
  try {
    const u = new URL(wsUrl);
    if (u.protocol !== 'ws:' && u.protocol !== 'wss:') return DEFAULT_VHF_URL;
    u.port = String(DEFAULT_VHF_PORT);
    u.pathname = '/';
    u.search = '';
    return u.toString();
  } catch {
    return DEFAULT_VHF_URL;
  }
}

// ------------------------------------------------------------------ 協定

export interface VhfRosterEntry {
  id: string;
  station: string;
  channel: string;
  watch: string[];
  /** 發話中的頻道(null = 未發話) */
  transmitting: string | null;
}

/** 文字框(客戶端 ↔ 伺服器共用;kind 決定有哪些欄位,見 src/Contracts/vhf-protocol.md)。 */
export interface VhfMessage {
  type: 'vhf';
  kind: 'join' | 'joined' | 'roster' | 'channel' | 'pttStart' | 'pttEnd' | 'busy' | 'text' | 'dsc' | 'error' | (string & {});
  id?: string;
  station?: string;
  channel?: string;
  watch?: string[];
  text?: string;
  category?: VhfDscCategory;
  duration_s?: number;
  reason?: string;
  by?: string;
  byStation?: string;
  stations?: VhfRosterEntry[];
  channels?: string[];
  message?: string;
  utc?: string;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function str(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined;
}

function strList(v: unknown): string[] | undefined {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : undefined;
}

export function parseRosterEntry(raw: unknown): VhfRosterEntry | null {
  if (!isRecord(raw) || typeof raw['id'] !== 'string') return null;
  return {
    id: raw['id'],
    station: str(raw['station']) ?? raw['id'],
    channel: str(raw['channel']) ?? '16',
    watch: strList(raw['watch']) ?? [],
    transmitting: typeof raw['transmitting'] === 'string' ? raw['transmitting'] : null,
  };
}

/** 解析文字框;不是 {type:"vhf", kind} 物件回傳 null,未知欄位略過。 */
export function parseVhfMessage(raw: unknown): VhfMessage | null {
  let obj: unknown = raw;
  if (typeof raw === 'string') {
    try {
      obj = JSON.parse(raw);
    } catch {
      return null;
    }
  }
  if (!isRecord(obj) || obj['type'] !== 'vhf' || typeof obj['kind'] !== 'string' || !obj['kind']) return null;
  const m: VhfMessage = { type: 'vhf', kind: obj['kind'] };
  const id = str(obj['id']);
  if (id !== undefined) m.id = id;
  const station = str(obj['station']);
  if (station !== undefined) m.station = station;
  const channel = str(obj['channel']);
  if (channel !== undefined) m.channel = channel;
  const watch = strList(obj['watch']);
  if (watch !== undefined) m.watch = watch;
  const text = str(obj['text']);
  if (text !== undefined) m.text = text;
  if (isDscCategory(obj['category'])) m.category = obj['category'];
  if (typeof obj['duration_s'] === 'number' && Number.isFinite(obj['duration_s'])) m.duration_s = obj['duration_s'];
  const reason = str(obj['reason']);
  if (reason !== undefined) m.reason = reason;
  const by = str(obj['by']);
  if (by !== undefined) m.by = by;
  const byStation = str(obj['byStation']);
  if (byStation !== undefined) m.byStation = byStation;
  if (Array.isArray(obj['stations'])) m.stations = obj['stations'].map(parseRosterEntry).filter((e): e is VhfRosterEntry => e !== null);
  const channels = strList(obj['channels']);
  if (channels !== undefined) m.channels = channels;
  const message = str(obj['message']);
  if (message !== undefined) m.message = message;
  const utc = str(obj['utc']);
  if (utc !== undefined) m.utc = utc;
  return m;
}

export function serializeVhfMessage(m: VhfMessage): string {
  return JSON.stringify(m);
}

const utf8 = { enc: new TextEncoder(), dec: new TextDecoder() };

/** 二進位音訊框:[uint32 LE id 長度][id UTF-8][payload](與 C# VhfRelayServer.EncodeAudioFrame 相同)。 */
export function encodeAudioFrame(id: string, payload: Uint8Array): Uint8Array {
  const idBytes = utf8.enc.encode(id);
  const out = new Uint8Array(4 + idBytes.length + payload.length);
  new DataView(out.buffer).setUint32(0, idBytes.length, true);
  out.set(idBytes, 4);
  out.set(payload, 4 + idBytes.length);
  return out;
}

/** 解二進位音訊框;標頭不合法(長度 0 或 > 64、框太短)回傳 null。payload 為原緩衝區的視圖(不複製)。 */
export function decodeAudioFrame(buf: ArrayBuffer | Uint8Array): { id: string; payload: Uint8Array } | null {
  const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
  if (bytes.length < 4) return null;
  const n = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(0, true);
  if (n === 0 || n > 64 || bytes.length < 4 + n) return null;
  return { id: utf8.dec.decode(bytes.subarray(4, 4 + n)), payload: bytes.subarray(4 + n) };
}

// ------------------------------------------------------------------ 狀態機

export type VhfState = 'idle' | 'transmitting' | 'receiving' | 'busy';

export const VHF_STATE_LABEL: Readonly<Record<VhfState, string>> = { idle: '守聽', transmitting: '發話中', receiving: '接收中', busy: '頻道佔用' };

export type VhfLogSeverity = 'info' | 'warning' | 'alarm';

/** 面板訊息列的一筆(通話、文字、DSC、佔用、系統提示)。 */
export interface VhfLogItem {
  seq: number;
  /** 牆鐘毫秒(now()) */
  at: number;
  kind: 'call' | 'text' | 'dsc' | 'busy' | 'system';
  direction: 'tx' | 'rx' | 'sys';
  station: string;
  channel: string;
  text?: string;
  category?: VhfDscCategory;
  duration_s?: number;
  severity: VhfLogSeverity;
}

export interface VhfPeer {
  id: string;
  station: string;
  channel: string;
}

export type VhfEffect =
  | { type: 'send'; message: VhfMessage }
  | { type: 'startCapture' }
  | { type: 'stopCapture' }
  | { type: 'startPlayback'; from: VhfPeer }
  | { type: 'stopPlayback' }
  | { type: 'log'; item: VhfLogItem };

export interface VhfStateMachineOptions {
  station: string;
  channel?: string;
  /** 雙守聽的額外頻道(教官站;學員站不給) */
  watch?: readonly string[];
  now?: () => number;
  /** busy 顯示多久後回 idle(毫秒) */
  busyHold_ms?: number;
  /** 訊息列保留筆數 */
  maxLog?: number;
}

export class VhfStateMachine {
  readonly station: string;
  private readonly now: () => number;
  private readonly busyHold: number;
  private readonly maxLog: number;
  private _state: VhfState = 'idle';
  private _id = '';
  private _channel: string;
  private _watch: string[];
  private _joined = false;
  private _roster: VhfRosterEntry[] = [];
  private _receivingFrom: VhfPeer | null = null;
  private _busyBy: { id: string; station: string; channel: string } | null = null;
  private busyUntil = 0;
  private txStartedAt = 0;
  private txChannel = '';
  private readonly _log: VhfLogItem[] = [];
  private seq = 0;

  constructor(opts: VhfStateMachineOptions) {
    this.station = opts.station;
    this.now = opts.now ?? (() => Date.now());
    this.busyHold = opts.busyHold_ms ?? 1500;
    this.maxLog = opts.maxLog ?? 200;
    this._channel = isVhfChannel(opts.channel) ? opts.channel : '16';
    this._watch = (opts.watch ?? []).filter((c) => isVhfChannel(c) && c !== this._channel);
  }

  get state(): VhfState {
    return this._state;
  }
  /** 伺服器指派的連線 id(joined 前為空) */
  get id(): string {
    return this._id;
  }
  get channel(): string {
    return this._channel;
  }
  get watch(): readonly string[] {
    return this._watch;
  }
  get joined(): boolean {
    return this._joined;
  }
  get roster(): readonly VhfRosterEntry[] {
    return this._roster;
  }
  get receivingFrom(): VhfPeer | null {
    return this._receivingFrom;
  }
  get busyBy(): { id: string; station: string; channel: string } | null {
    return this._busyBy;
  }
  get log(): readonly VhfLogItem[] {
    return this._log;
  }

  /** 本站是否守聽該頻道(主頻道或雙守聽頻道)。 */
  listensTo(channel: string): boolean {
    return channel === this._channel || this._watch.includes(channel);
  }

  /** 頻道上其他正在發話的站(roster 的 transmitting;供指示燈) */
  occupantOf(channel: string): VhfRosterEntry | undefined {
    return this._roster.find((r) => r.transmitting === channel && r.id !== this._id);
  }

  private push(item: Omit<VhfLogItem, 'seq' | 'at'>): VhfEffect {
    const full: VhfLogItem = { seq: ++this.seq, at: this.now(), ...item };
    this._log.push(full);
    if (this._log.length > this.maxLog) this._log.splice(0, this._log.length - this.maxLog);
    return { type: 'log', item: full };
  }

  private system(text: string, severity: VhfLogSeverity = 'info'): VhfEffect {
    return this.push({ kind: 'system', direction: 'sys', station: '', channel: this._channel, text, severity });
  }

  private joinMessage(): VhfMessage {
    return { type: 'vhf', kind: 'join', station: this.station, channel: this._channel, watch: [...this._watch] };
  }

  /** WebSocket 連上:送 join。 */
  connected(): VhfEffect[] {
    return [{ type: 'send', message: this.joinMessage() }];
  }

  /** WebSocket 斷線:停止擷取/播放、回 idle、joined 清除(重連後再 join)。 */
  disconnected(): VhfEffect[] {
    const out: VhfEffect[] = [];
    if (this._state === 'transmitting') out.push({ type: 'stopCapture' });
    if (this._state === 'receiving') out.push({ type: 'stopPlayback' });
    this._state = 'idle';
    this._receivingFrom = null;
    this._busyBy = null;
    this._joined = false;
    this._roster = [];
    out.push(this.system('VHF 連線中斷', 'warning'));
    return out;
  }

  /** 轉頻道旋鈕:發話中先結束通話;不在清單的頻道忽略。 */
  setChannel(channel: string): VhfEffect[] {
    if (!isVhfChannel(channel) || channel === this._channel) return [];
    const out: VhfEffect[] = [];
    if (this._state === 'transmitting') out.push(...this.pttUp());
    if (this._state === 'receiving' && this._receivingFrom && this._receivingFrom.channel === this._channel && !this._watch.includes(this._receivingFrom.channel)) {
      // 轉離正在接收的頻道:停止播放
      out.push({ type: 'stopPlayback' });
      this._receivingFrom = null;
      this._state = 'idle';
    }
    this._channel = channel;
    this._watch = this._watch.filter((c) => c !== channel);
    if (this._joined) out.push({ type: 'send', message: { type: 'vhf', kind: 'channel', channel, watch: [...this._watch] } });
    return out;
  }

  /** 雙守聽:設定(或以 null 取消)額外守聽的頻道;與主頻道相同時取消。 */
  setWatch(channel: string | null): VhfEffect[] {
    const next = channel !== null && isVhfChannel(channel) && channel !== this._channel ? [channel] : [];
    if (next.length === this._watch.length && next.every((c, i) => c === this._watch[i])) return [];
    const out: VhfEffect[] = [];
    if (this._state === 'receiving' && this._receivingFrom && !next.includes(this._receivingFrom.channel) && this._receivingFrom.channel !== this._channel) {
      out.push({ type: 'stopPlayback' });
      this._receivingFrom = null;
      this._state = 'idle';
    }
    this._watch = next;
    if (this._joined) out.push({ type: 'send', message: { type: 'vhf', kind: 'channel', channel: this._channel, watch: [...this._watch] } });
    return out;
  }

  /** 按下 PTT:只在守聽(idle)且已加入時開始發話;接收中/佔用中不可發話(按下無效)。 */
  pttDown(): VhfEffect[] {
    if (this._state !== 'idle' || !this._joined) return [];
    this._state = 'transmitting';
    this.txStartedAt = this.now();
    this.txChannel = this._channel;
    return [{ type: 'send', message: { type: 'vhf', kind: 'pttStart', channel: this._channel } }, { type: 'startCapture' }];
  }

  /** 放開 PTT:停止擷取 → 送 pttEnd → 記錄本站通話。 */
  pttUp(): VhfEffect[] {
    if (this._state !== 'transmitting') return [];
    const duration = Math.max(0, (this.now() - this.txStartedAt) / 1000);
    this._state = 'idle';
    return [
      { type: 'stopCapture' },
      { type: 'send', message: { type: 'vhf', kind: 'pttEnd' } },
      this.push({ kind: 'call', direction: 'tx', station: this.station, channel: this.txChannel, duration_s: Math.round(duration * 10) / 10, severity: 'info' }),
    ];
  }

  /** 文字訊息(無麥克風時的備援;教官扮演 VTS/引水人/他船):空白不送。 */
  sendText(text: string): VhfEffect[] {
    const t = text.trim();
    if (!t || !this._joined) return [];
    return [{ type: 'send', message: { type: 'vhf', kind: 'text', channel: this._channel, text: t } }];
  }

  /** DSC 簡化(DISTRESS/URGENCY 文字警示;面板需二次確認後才呼叫)。 */
  sendDsc(category: VhfDscCategory, text = ''): VhfEffect[] {
    if (!this._joined) return [];
    const m: VhfMessage = { type: 'vhf', kind: 'dsc', category };
    const t = text.trim();
    if (t) m.text = t;
    return [{ type: 'send', message: m }];
  }

  /** 二進位音訊框是否該播放(接收中且來自目前的發話者)。 */
  acceptsAudio(id: string): boolean {
    return this._state === 'receiving' && this._receivingFrom !== null && this._receivingFrom.id === id;
  }

  /** 伺服器文字框。 */
  handleMessage(msg: VhfMessage): VhfEffect[] {
    switch (msg.kind) {
      case 'joined': {
        this._id = msg.id ?? '';
        this._joined = true;
        if (isVhfChannel(msg.channel)) this._channel = msg.channel;
        if (msg.watch) this._watch = msg.watch.filter((c) => isVhfChannel(c) && c !== this._channel);
        if (msg.stations) this._roster = msg.stations;
        return [this.system(`VHF 已連線:${this.station},頻道 ${this._channel}${this._watch.length ? ` + 守聽 ${this._watch.join('/')}` : ''}`)];
      }
      case 'roster':
        if (msg.stations) this._roster = msg.stations;
        return [];
      case 'channel':
        // 伺服器確認本站頻道(可能被修正)
        if (msg.id === this._id || msg.id === undefined) {
          if (isVhfChannel(msg.channel)) this._channel = msg.channel;
          if (msg.watch) this._watch = msg.watch.filter((c) => isVhfChannel(c) && c !== this._channel);
        }
        return [];
      case 'pttStart': {
        const channel = msg.channel ?? '';
        if (!msg.id || msg.id === this._id || !this.listensTo(channel)) return [];
        // 發話中聽不到他站(半雙工);已在接收另一站時不切換
        if (this._state === 'transmitting' || this._state === 'receiving') return [];
        this._state = 'receiving';
        this._busyBy = null;
        this._receivingFrom = { id: msg.id, station: msg.station ?? msg.id, channel };
        return [{ type: 'startPlayback', from: this._receivingFrom }];
      }
      case 'pttEnd': {
        if (!this._receivingFrom || msg.id !== this._receivingFrom.id) return [];
        const from = this._receivingFrom;
        this._receivingFrom = null;
        this._state = 'idle';
        return [
          { type: 'stopPlayback' },
          this.push({ kind: 'call', direction: 'rx', station: from.station, channel: from.channel, duration_s: msg.duration_s ?? 0, severity: 'info' }),
        ];
      }
      case 'busy': {
        const out: VhfEffect[] = [];
        if (this._state === 'transmitting') out.push({ type: 'stopCapture' });
        this._state = 'busy';
        this.busyUntil = this.now() + this.busyHold;
        this._busyBy = { id: msg.by ?? '', station: msg.byStation ?? msg.by ?? '?', channel: msg.channel ?? this._channel };
        out.push(this.push({ kind: 'busy', direction: 'sys', station: this._busyBy.station, channel: this._busyBy.channel, text: `頻道 ${this._busyBy.channel} 佔用中(${this._busyBy.station} 發話)`, severity: 'warning' }));
        return out;
      }
      case 'text': {
        if (!msg.text) return [];
        const mine = msg.id !== undefined && msg.id === this._id;
        return [this.push({ kind: 'text', direction: mine ? 'tx' : 'rx', station: msg.station ?? msg.id ?? '?', channel: msg.channel ?? this._channel, text: msg.text, severity: 'info' })];
      }
      case 'dsc': {
        const category = msg.category ?? 'routine';
        const mine = msg.id !== undefined && msg.id === this._id;
        const item: Omit<VhfLogItem, 'seq' | 'at'> = {
          kind: 'dsc',
          direction: mine ? 'tx' : 'rx',
          station: msg.station ?? msg.id ?? '?',
          channel: DSC_CHANNEL,
          category,
          severity: category === 'distress' ? 'alarm' : category === 'urgency' ? 'warning' : 'info',
        };
        if (msg.text) item.text = msg.text;
        return [this.push(item)];
      }
      case 'error':
        return [this.system(`VHF 伺服器拒絕:${msg.message ?? ''}`, 'warning')];
      default:
        return [];
    }
  }

  /** 定時呼叫:busy 顯示期滿回 idle。回傳是否有變化。 */
  tick(now = this.now()): boolean {
    if (this._state === 'busy' && now >= this.busyUntil) {
      this._state = 'idle';
      this._busyBy = null;
      return true;
    }
    return false;
  }
}

// ------------------------------------------------------------------ 講評統計

export const VHF_EVENT_KINDS: ReadonlySet<string> = new Set(['vhfCall', 'vhfText', 'vhfDsc']);

export interface VhfStationSummary {
  station: string;
  calls: number;
  duration_s: number;
  texts: number;
  dsc: number;
}

/** 紀錄 vhf 行的統計(講評報告「通訊」佐證;training record 匯出)。 */
export interface VhfSummary {
  calls: number;
  /** 通話總秒數 */
  duration_s: number;
  texts: number;
  dsc: number;
  /** DSC 中 distress 筆數 */
  distress: number;
  byStation: VhfStationSummary[];
}

function num(v: unknown): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : 0;
}

/** 由事件(source "vhf" 的 vhfCall/vhfText/vhfDsc;其他事件略過)算通話次數、總秒數、文字與 DSC 筆數、各站分列。 */
export function vhfSummary(events: readonly SimEvent[]): VhfSummary {
  const by = new Map<string, VhfStationSummary>();
  const sum: VhfSummary = { calls: 0, duration_s: 0, texts: 0, dsc: 0, distress: 0, byStation: [] };
  for (const e of events) {
    if (!VHF_EVENT_KINDS.has(e.kind)) continue;
    const station = typeof e.data?.['station'] === 'string' ? (e.data['station'] as string) : '?';
    let s = by.get(station);
    if (!s) {
      s = { station, calls: 0, duration_s: 0, texts: 0, dsc: 0 };
      by.set(station, s);
    }
    if (e.kind === 'vhfCall') {
      const d = num(e.data?.['duration_s']);
      sum.calls++;
      sum.duration_s += d;
      s.calls++;
      s.duration_s += d;
    } else if (e.kind === 'vhfText') {
      sum.texts++;
      s.texts++;
    } else {
      sum.dsc++;
      s.dsc++;
      if (e.data?.['category'] === 'distress') sum.distress++;
    }
  }
  sum.duration_s = Math.round(sum.duration_s * 10) / 10;
  sum.byStation = [...by.values()].map((s) => ({ ...s, duration_s: Math.round(s.duration_s * 10) / 10 })).sort((a, b) => a.station.localeCompare(b.station));
  return sum;
}

/** 有任何 VHF 活動才為 true(報告無活動時不列「通訊」段)。 */
export function hasVhfActivity(s: VhfSummary): boolean {
  return s.calls > 0 || s.texts > 0 || s.dsc > 0;
}
