// 教官站/簡化教官模式自己的練習紀錄(JSON):核心的 JSON Lines 紀錄在核心主機上,船上帶領者
// 不一定拿得到,因此教官站把收到的狀態(抽樣)、推導的學員操作、BRM 標記、送出的指令與講評摘要
// 存成一個 JSON 供下載與重播檢視(規劃書第 5.5 節紀錄包的第一版雛型,尚未簽章)。
//
// 狀態抽樣(第三十輪 W1;一次訓練約 2 小時、規劃書第 14 章「記憶體不持續成長」):
//  - 每筆只存重播檢視、講評報告與即時疊圖實際讀取的欄位(sampleState 精簡投影,欄位清單見 SAMPLED_STATE_KEYS 與下方說明);
//    真值、感測器全量、自動舵、警報、BNWAS、操舵模式、情境執行狀態、utc、u/v/r、裝載、收錨中的錨與目標船的 AIS 報告等不存。
//  - 倒帶(restore;t 倒退)時截掉 t > 倒帶點的舊分支樣本(與 record-replay.pushState 相同),倒帶點另記在 rewinds[]。
//  - 安全上限:超過 MAX_STATE_SAMPLES(1 Hz 時即 2 小時,含兩端)筆時降採樣為一半(保留第一筆與最後一筆,之後的抽樣間隔加倍),
//    記憶體因此有上限;2 小時內(1 Hz)不會觸發。
// 匯出(第三十輪 W2):sessionRecordBlob 逐欄、逐筆 JSON.stringify 後以 BlobPart[] 組成 Blob,不產生整份紀錄的單一字串;
// 內容與 JSON.stringify(record) 位元組相同。

import type { SimCommand } from '../types/command.js';
import type { SimEvent } from '../types/event.js';
import type { SensorStatus } from '../types/alerts.js';
import type { AnchorState } from '../types/external.js';
import type { OwnShipState, Position } from '../types/state.js';
import type { TargetState } from '../types/target.js';
import type { AssessmentSummary } from './assessment.js';
import type { BrmMarker } from './brm-markers.js';
import type { DebriefSummary } from './debrief.js';
import type { OperationEvent } from './operation-log.js';
import type { ScenarioSummary } from './scenario.js';
import type { SessionInfo } from './session-info.js';
import type { RecordingSummary } from './screen-recording.js';

export const SESSION_RECORD_FORMAT = 'simosa-brm-instructor-record';
/** 目前寫出的紀錄版本(SessionRecord.version) */
export const SESSION_RECORD_VERSION = 5;

export interface CommandLogEntry {
  seq: number;
  /** 送出時的模擬時間 */
  t: number;
  wall: string;
  command: SimCommand;
  /** 核心回應(Python 參考伺服器會回 ack;C# 核心目前不回) */
  ack?: { ok: boolean; detail?: string; tick?: number };
}

/**
 * 抽樣保留的自船欄位:重播檢視的 conning 列(heading/rot/speed/rudder/engine/thruster/depth/wind/current/sea/inclinometer/clock/route 各方塊)、
 * 鳥瞰航跡圖(位置、航向/COG/SOG 向量、流場層、拖船/泊位/纜繩)、講評摘要累計(debrief.ts summarize)與事件標記位置(plot-overlay eventMarks)。
 */
export const SAMPLED_STATE_KEYS = [
  't',
  'tick',
  'shipId',
  'pos',
  'heading',
  'cog',
  'sog',
  'stw',
  'rot',
  'drift',
  'rudder',
  'rudderOrder',
  'rpm',
  'rpmOrder',
  'telegraph',
  'thruster',
  'depthBelowKeel',
  'waterDepth',
  'tideHeight_m',
  'squat',
  'wind',
  'current',
  'currentSource',
  'draft',
  'engine',
  'faults',
  'flags',
  'visibility_nm',
  'route',
  'tugs',
  'berth',
  'waves',
  'shipInteraction',
  'roll',
  'waveMotion',
] as const satisfies readonly (keyof OwnShipState)[];

/** 抽樣保留的目標船欄位:航跡圖的三角形/標籤/COG-SOG 向量(track-plot drawTargets)與 CPA/TCPA 小表(targets.ts targetTableRows)。 */
export const SAMPLED_TARGET_KEYS = ['id', 'name', 'heading', 'cog', 'sog', 'loa', 'aisOn', 'range_nm', 'bearing_deg', 'cpa_nm', 'tcpa_min'] as const satisfies readonly (keyof TargetState)[];

/** 抽樣保留的感測器有效性(conning 列各方塊的「無效」標示);磁羅經指示不存。 */
export const SAMPLED_SENSOR_KEYS = ['headingValid', 'positionValid', 'depthValid', 'stwValid'] as const satisfies readonly (keyof SensorStatus)[];

export type SampledTarget = Pick<TargetState, (typeof SAMPLED_TARGET_KEYS)[number]> & { pos: Pick<Position, 'x' | 'y'> };

/**
 * 一筆狀態抽樣(OwnShipState 的精簡投影;OwnShipState 可直接當作 SampledState 使用)。
 * anchors 只存已放出(有 position)的錨:航跡圖只畫有位置的錨,收錨中的錨每筆約 0.4 KB 且不變。
 */
export type SampledState = Pick<OwnShipState, (typeof SAMPLED_STATE_KEYS)[number]> & {
  targets: SampledTarget[];
  sensors?: Pick<SensorStatus, (typeof SAMPLED_SENSOR_KEYS)[number]>;
  anchors?: AnchorState[];
};

/** 由完整狀態取精簡投影(深複本,之後來源物件被改寫也不影響抽樣)。 */
export function sampleState(s: OwnShipState): SampledState {
  const out: Record<string, unknown> = {};
  for (const k of SAMPLED_STATE_KEYS) if (s[k] !== undefined) out[k] = s[k];
  out['targets'] = (s.targets ?? []).map((t) => {
    const o: Record<string, unknown> = {};
    for (const k of SAMPLED_TARGET_KEYS) if (t[k] !== undefined) o[k] = t[k];
    o['pos'] = { x: t.pos.x, y: t.pos.y };
    return o;
  });
  if (s.sensors) {
    const sen: Record<string, unknown> = {};
    for (const k of SAMPLED_SENSOR_KEYS) sen[k] = s.sensors[k];
    out['sensors'] = sen;
  }
  const deployed = (s.anchors ?? []).filter((a) => a.position !== undefined);
  if (deployed.length > 0) out['anchors'] = deployed;
  return structuredClone(out) as SampledState;
}

/** 倒帶點(SessionRecord.rewinds;版本 5):與 record-replay 的 ReplayRewind 同義 */
export interface SampledRewind {
  /** 倒帶點(模擬秒;restore 要求的 tick × dt,未知時為倒帶後第一筆狀態的 t) */
  t: number;
  tick: number;
  /** 倒帶前最後一筆抽樣的 t */
  from: number;
  /** 截掉的舊分支抽樣筆數 */
  droppedStates: number;
  /** 同時丟棄的舊分支事件數(教官站事件流 dropAfter) */
  droppedEvents: number;
}

export interface SessionRecord {
  format: typeof SESSION_RECORD_FORMAT;
  /**
   * 1:第一版;2:新增 events(核心事件)與 coreSummary(核心 summary);3:新增 sessionInfo(參與者與休息時間確認);4:新增 recordings(畫面錄影);
   * 5:states 改為精簡投影(SampledState)、倒帶的舊分支已截掉並新增 rewinds、超過上限時降採樣(sampleInterval_s 為降採樣後的間隔)
   */
  version: 1 | 2 | 3 | 4 | 5;
  /** 匯出時刻(每次下載或按「講評報告」都不同;不可作為練習識別) */
  createdUtc: string;
  /**
   * 練習開始時刻(教官站本機時鐘,ISO):載入情境/重設/簡化模式「開始練習」後收到第一筆狀態時記下,同一練習多次匯出不變。
   * 講評報告的紀錄鍵用它對應評估表與報告欄位;舊紀錄沒有此欄時退回 createdUtc。
   */
  startedUtc?: string;
  mode: 'full' | 'simple';
  source: string;
  scenario: ScenarioSummary | null;
  shipId: string;
  /** 狀態抽樣間隔(秒;版本 5 起為降採樣後的實際間隔) */
  sampleInterval_s: number;
  /** 狀態抽樣(版本 4 以前為完整 OwnShipState;版本 5 起為 SampledState) */
  states: SampledState[];
  operations: OperationEvent[];
  markers: BrmMarker[];
  commands: CommandLogEntry[];
  /** 本地推導(debrief.ts) */
  summary: DebriefSummary | null;
  /** 核心推送/查詢得到的事件(版本 2;舊紀錄無) */
  events?: SimEvent[];
  /** 核心 summary(版本 2;Python 伺服器/離線時為 null)。存原始物件(summary.raw),讀回時再解析 */
  coreSummary?: Record<string, unknown> | null;
  /** 參與者與休息時間確認(版本 3;教官站送出 sessionInfo 指令時的 args;未送出為 null) */
  sessionInfo?: SessionInfo | null;
  /** 本練習期間的畫面錄影(版本 4;lib/screen-recording.ts 上傳到核心的 <mediaId>.webm 摘要;未錄影為空陣列) */
  recordings?: RecordingSummary[];
  /** 倒帶點(版本 5;依發生順序;states 已截掉舊分支,讀回時直接列為倒帶標記) */
  rewinds?: SampledRewind[];
}

/** 抽樣筆數上限:1 Hz × 7200 s(2 小時)含 t = 0 與 t = 7200 兩端;超過即降採樣。 */
export const MAX_STATE_SAMPLES = 7201;

/**
 * 以固定間隔抽樣狀態(預設 1 Hz;2 小時約 7,200 筆)並存精簡投影(sampleState);講評累計器另由呼叫者以完整狀態餵。
 * 倒帶時截掉舊分支,超過 maxSamples 時降採樣(見檔頭說明)。
 */
export class StateSampler {
  private readonly states: SampledState[] = [];
  private readonly rewindList: SampledRewind[] = [];
  private lastT = Number.NEGATIVE_INFINITY;
  /** 倒帶後下一筆狀態一律抽樣(新分支的起點) */
  private forceNext = false;
  private step_s: number;
  private decimationCount = 0;

  constructor(
    readonly interval_s = 1,
    readonly maxSamples = MAX_STATE_SAMPLES,
  ) {
    this.step_s = interval_s;
  }

  push(s: OwnShipState): boolean {
    const last = this.states[this.states.length - 1];
    // 呼叫者未先 rewind()(例如核心重啟):t 倒退即視為倒帶到該狀態的 t
    if (last && s.t < last.t) this.rewind(s.t, s.tick);
    if (!this.forceNext && this.states.length > 0 && s.t - this.lastT < this.step_s) return false;
    this.forceNext = false;
    this.states.push(sampleState(s));
    this.lastT = s.t;
    if (this.states.length > this.maxSamples) this.decimate();
    return true;
  }

  /**
   * 倒帶(restore/核心重設):丟棄 t > t_rewind 的舊分支抽樣並記下倒帶點;下一筆狀態一律抽樣。回傳丟棄筆數。
   * 尚無抽樣時不記(新練習開始時核心 tick 歸零也會走到這裡)。
   */
  rewind(t: number, tick: number, droppedEvents = 0): number {
    const last = this.states[this.states.length - 1];
    if (!last) return 0;
    const from = last.t;
    let dropped = 0;
    while (this.states.length > 0 && this.states[this.states.length - 1]!.t > t) {
      this.states.pop();
      dropped++;
    }
    this.rewindList.push({ t, tick, from, droppedStates: dropped, droppedEvents });
    this.lastT = this.states[this.states.length - 1]?.t ?? Number.NEGATIVE_INFINITY;
    this.forceNext = true;
    return dropped;
  }

  /** 降採樣:保留偶數索引(含第一筆)與最後一筆,之後的抽樣間隔加倍。 */
  private decimate(): void {
    const n = this.states.length;
    let w = 0;
    for (let i = 0; i < n; i += 2) this.states[w++] = this.states[i]!;
    if ((n - 1) % 2 === 1) this.states[w++] = this.states[n - 1]!;
    this.states.length = w;
    this.step_s *= 2;
    this.decimationCount++;
  }

  all(): readonly SampledState[] {
    return this.states;
  }

  /** 倒帶點(依發生順序) */
  rewinds(): readonly SampledRewind[] {
    return this.rewindList;
  }

  /** 目前的抽樣間隔(秒;降採樣後為 interval_s × 2^次數) */
  get currentInterval_s(): number {
    return this.step_s;
  }

  /** 已降採樣的次數 */
  get decimations(): number {
    return this.decimationCount;
  }

  clear(): void {
    this.states.length = 0;
    this.rewindList.length = 0;
    this.lastT = Number.NEGATIVE_INFINITY;
    this.forceNext = false;
    this.step_s = this.interval_s;
    this.decimationCount = 0;
  }
}

export interface SessionRecordInput {
  mode: 'full' | 'simple';
  source: string;
  scenario: ScenarioSummary | null;
  shipId: string;
  sampleInterval_s: number;
  /** 抽樣(StateSampler.all();完整 OwnShipState 亦可,原樣寫入) */
  states: readonly SampledState[];
  operations: readonly OperationEvent[];
  markers: readonly BrmMarker[];
  commands: readonly CommandLogEntry[];
  summary: DebriefSummary | null;
  events?: readonly SimEvent[];
  coreSummary?: AssessmentSummary | null;
  /** 練習開始時刻(ISO;見 SessionRecord.startedUtc) */
  startedUtc?: string;
  /** 參與者與休息時間確認(sessionInfo 指令的 args;未送出時省略或 null) */
  sessionInfo?: SessionInfo | null;
  /** 畫面錄影摘要(未錄影時省略) */
  recordings?: readonly RecordingSummary[];
  /** 倒帶點(StateSampler.rewinds();省略 = 無) */
  rewinds?: readonly SampledRewind[];
}

export function buildSessionRecord(input: SessionRecordInput, now = new Date()): SessionRecord {
  return {
    format: SESSION_RECORD_FORMAT,
    version: SESSION_RECORD_VERSION,
    createdUtc: now.toISOString(),
    ...(input.startedUtc ? { startedUtc: input.startedUtc } : {}),
    mode: input.mode,
    source: input.source,
    scenario: input.scenario,
    shipId: input.shipId,
    sampleInterval_s: input.sampleInterval_s,
    states: [...input.states],
    operations: [...input.operations],
    markers: [...input.markers],
    commands: [...input.commands],
    summary: input.summary,
    events: [...(input.events ?? [])],
    coreSummary: input.coreSummary ? input.coreSummary.raw : null,
    sessionInfo: input.sessionInfo ? structuredClone(input.sessionInfo) : null,
    recordings: (input.recordings ?? []).map((r) => ({ ...r })),
    rewinds: (input.rewinds ?? []).map((r) => ({ ...r })),
  };
}

export function isSessionRecord(v: unknown): v is SessionRecord {
  return typeof v === 'object' && v !== null && (v as { format?: unknown }).format === SESSION_RECORD_FORMAT && Array.isArray((v as { states?: unknown }).states);
}

/**
 * 逐段序列化紀錄:頂層物件逐欄,陣列欄位(states、events、operations…)逐筆 JSON.stringify。
 * 全部片段串接後與 JSON.stringify(record) 完全相同(欄位順序、略過 undefined/函式欄位、陣列中的 undefined 寫成 null 皆同)。
 */
export function* sessionRecordChunks(record: SessionRecord): Generator<string> {
  const obj = record as unknown as Record<string, unknown>;
  let first = true;
  yield '{';
  for (const key of Object.keys(obj)) {
    const v = obj[key];
    if (v === undefined || typeof v === 'function' || typeof v === 'symbol') continue;
    const prefix = `${first ? '' : ','}${JSON.stringify(key)}:`;
    first = false;
    if (Array.isArray(v)) {
      yield `${prefix}[`;
      for (let i = 0; i < v.length; i++) yield `${i > 0 ? ',' : ''}${JSON.stringify(v[i]) ?? 'null'}`;
      yield ']';
    } else {
      yield prefix + JSON.stringify(v);
    }
  }
  yield '}';
}

/** 匯出 Blob 的 MIME(與原本 downloadText 的預設相同) */
export const SESSION_RECORD_MIME = 'application/json;charset=utf-8';

/**
 * 紀錄 → Blob(下載與講評報告交接共用):片段先累積到約 chunkChars 字元就組成一個子 Blob(資料交給瀏覽器的 Blob 儲存,
 * 頁面內的小字串隨即可回收),最後把子 Blob 組成一個 Blob。不產生整份紀錄的單一字串;位元組與 JSON.stringify(record) 的 UTF-8 相同。
 */
export function sessionRecordBlob(record: SessionRecord, chunkChars = 1 << 20): Blob {
  const parts: BlobPart[] = [];
  let buf: string[] = [];
  let n = 0;
  for (const c of sessionRecordChunks(record)) {
    buf.push(c);
    n += c.length;
    if (n >= chunkChars) {
      parts.push(new Blob(buf));
      buf = [];
      n = 0;
    }
  }
  if (buf.length > 0) parts.push(new Blob(buf));
  return new Blob(parts, { type: SESSION_RECORD_MIME });
}
