// 教官站/簡化教官模式自己的練習紀錄(JSON):核心的 JSON Lines 紀錄在核心主機上,船上帶領者
// 不一定拿得到,因此教官站把收到的狀態(抽樣)、推導的學員操作、BRM 標記、送出的指令與講評摘要
// 存成一個 JSON 供下載與重播檢視(規劃書第 5.5 節紀錄包的第一版雛型,尚未簽章)。

import type { SimCommand } from '../types/command.js';
import type { OwnShipState } from '../types/state.js';
import type { BrmMarker } from './brm-markers.js';
import type { DebriefSummary } from './debrief.js';
import type { OperationEvent } from './operation-log.js';
import type { ScenarioSummary } from './scenario.js';

export const SESSION_RECORD_FORMAT = 'simosa-brm-instructor-record';

export interface CommandLogEntry {
  seq: number;
  /** 送出時的模擬時間 */
  t: number;
  wall: string;
  command: SimCommand;
  /** 核心回應(Python 參考伺服器會回 ack;C# 核心目前不回) */
  ack?: { ok: boolean; detail?: string; tick?: number };
}

export interface SessionRecord {
  format: typeof SESSION_RECORD_FORMAT;
  version: 1;
  createdUtc: string;
  mode: 'full' | 'simple';
  source: string;
  scenario: ScenarioSummary | null;
  shipId: string;
  /** 狀態抽樣間隔(秒) */
  sampleInterval_s: number;
  states: OwnShipState[];
  operations: OperationEvent[];
  markers: BrmMarker[];
  commands: CommandLogEntry[];
  summary: DebriefSummary | null;
}

/** 以固定間隔抽樣狀態(預設 1 Hz;2 小時約 7,200 筆),並保留事件所需的完整序列給講評累計器(由呼叫者另外餵)。 */
export class StateSampler {
  private readonly states: OwnShipState[] = [];
  private lastT = Number.NEGATIVE_INFINITY;

  constructor(readonly interval_s = 1) {}

  push(s: OwnShipState): boolean {
    if (s.t < this.lastT) this.lastT = Number.NEGATIVE_INFINITY; // 重設/還原後重新開始抽樣
    if (s.t - this.lastT < this.interval_s && this.states.length > 0) return false;
    this.states.push(structuredClone(s));
    this.lastT = s.t;
    return true;
  }

  all(): readonly OwnShipState[] {
    return this.states;
  }

  clear(): void {
    this.states.length = 0;
    this.lastT = Number.NEGATIVE_INFINITY;
  }
}

export interface SessionRecordInput {
  mode: 'full' | 'simple';
  source: string;
  scenario: ScenarioSummary | null;
  shipId: string;
  sampleInterval_s: number;
  states: readonly OwnShipState[];
  operations: readonly OperationEvent[];
  markers: readonly BrmMarker[];
  commands: readonly CommandLogEntry[];
  summary: DebriefSummary | null;
}

export function buildSessionRecord(input: SessionRecordInput, now = new Date()): SessionRecord {
  return {
    format: SESSION_RECORD_FORMAT,
    version: 1,
    createdUtc: now.toISOString(),
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
  };
}

export function isSessionRecord(v: unknown): v is SessionRecord {
  return typeof v === 'object' && v !== null && (v as { format?: unknown }).format === SESSION_RECORD_FORMAT && Array.isArray((v as { states?: unknown }).states);
}
