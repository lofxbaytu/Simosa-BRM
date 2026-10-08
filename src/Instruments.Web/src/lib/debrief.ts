// 自動講評摘要(規劃書第 8.3 節自動量測指標的第一版子集、第 9.1 節簡化教官模式):
// 由收到的狀態序列即時累計最小 UKC、最大舵角、最大 ROT、車鐘變更次數、航程距離、平均速度、
// 超過港區速限的時間等。閾值暫用常數,之後改由「公司程序參數集」提供(規劃書第 9.1 節、第 8.3 節)。

import type { OwnShipState, TelegraphPosition } from '../types/state.js';

/** 第一版閾值常數(待程序參數集取代;UKC 與儀器頁 depth-readout 的預設一致)。 */
export interface DebriefThresholds {
  /** 港區速限 (kn) */
  portSpeedLimit_kn: number;
  /** UKC 警報門檻 (m) */
  ukcAlarm_m: number;
  /** ROT 上限 (度/分) */
  rotLimit_degPerMin: number;
  /** 視為滿舵的舵角 (度) */
  hardOver_deg: number;
}

export const DEFAULT_THRESHOLDS: Readonly<DebriefThresholds> = {
  portSpeedLimit_kn: 8,
  ukcAlarm_m: 1.0,
  rotLimit_degPerMin: 30,
  hardOver_deg: 35,
};

export interface DebriefSummary {
  /** 狀態筆數 */
  samples: number;
  /** 練習模擬時間(秒),由第一筆到最後一筆 */
  duration_s: number;
  minUkc_m: number | null;
  minUkcAt_s: number | null;
  maxRudder_deg: number;
  maxRudderAt_s: number | null;
  maxRot_degPerMin: number;
  maxRotAt_s: number | null;
  telegraphChanges: number;
  /** 舵令變更次數(變化 ≥ 1°) */
  rudderOrders: number;
  /** 舵角達滿舵的次數 */
  hardOverCount: number;
  /** 航程(浬),由位置積分 */
  distance_nm: number;
  /** 平均對地速度 (kn) = 航程 / 時間 */
  avgSpeed_kn: number;
  maxSog_kn: number;
  /** 超過港區速限的時間(秒) */
  timeOverSpeedLimit_s: number;
  /** UKC 低於警報門檻的時間(秒) */
  timeUkcBelowAlarm_s: number;
  /** ROT 超過上限的時間(秒) */
  timeRotOverLimit_s: number;
  agroundEvents: number;
  collisionEvents: number;
  /** 練習中凍結的時間(秒,不計入航程與超限時間) */
  frozen_s: number;
  thresholds: DebriefThresholds;
}

const M_PER_NM = 1852;

/** 即時累計器:每收到一筆狀態呼叫 push();summary() 隨時可取。 */
export class DebriefAccumulator {
  private prev: OwnShipState | null = null;
  private first_t: number | null = null;
  private last_t = 0;
  private samples = 0;
  private minUkc: number | null = null;
  private minUkcAt: number | null = null;
  private maxRudder = 0;
  private maxRudderAt: number | null = null;
  private maxRot = 0;
  private maxRotAt: number | null = null;
  private telegraphChanges = 0;
  private rudderOrders = 0;
  private hardOverCount = 0;
  private hardOverActive = false;
  private distance_m = 0;
  private maxSog = 0;
  private overSpeed_s = 0;
  private ukcBelow_s = 0;
  private rotOver_s = 0;
  private aground = 0;
  private collision = 0;
  private frozen_s = 0;
  private lastTelegraph: TelegraphPosition | null = null;

  constructor(readonly thresholds: DebriefThresholds = DEFAULT_THRESHOLDS) {}

  reset(): void {
    this.prev = null;
    this.first_t = null;
    this.last_t = 0;
    this.samples = 0;
    this.minUkc = null;
    this.minUkcAt = null;
    this.maxRudder = 0;
    this.maxRudderAt = null;
    this.maxRot = 0;
    this.maxRotAt = null;
    this.telegraphChanges = 0;
    this.rudderOrders = 0;
    this.hardOverCount = 0;
    this.hardOverActive = false;
    this.distance_m = 0;
    this.maxSog = 0;
    this.overSpeed_s = 0;
    this.ukcBelow_s = 0;
    this.rotOver_s = 0;
    this.aground = 0;
    this.collision = 0;
    this.frozen_s = 0;
    this.lastTelegraph = null;
  }

  push(s: OwnShipState): void {
    const th = this.thresholds;
    this.samples++;
    if (this.first_t === null) this.first_t = s.t;
    const p = this.prev;
    // 時間倒退(重設/還原)時以本筆為新的基準,不累計負的時間
    const dt = p && s.t >= p.t ? s.t - p.t : 0;
    this.last_t = s.t;

    if (Number.isFinite(s.depthBelowKeel) && (this.minUkc === null || s.depthBelowKeel < this.minUkc)) {
      this.minUkc = s.depthBelowKeel;
      this.minUkcAt = s.t;
    }
    if (Math.abs(s.rudder) > this.maxRudder) {
      this.maxRudder = Math.abs(s.rudder);
      this.maxRudderAt = s.t;
    }
    if (Math.abs(s.rot) > this.maxRot) {
      this.maxRot = Math.abs(s.rot);
      this.maxRotAt = s.t;
    }
    if (s.sog > this.maxSog) this.maxSog = s.sog;

    const hard = Math.abs(s.rudder) >= th.hardOver_deg - 0.5;
    if (hard && !this.hardOverActive) this.hardOverCount++;
    this.hardOverActive = hard;

    if (this.lastTelegraph !== null && s.telegraph !== this.lastTelegraph) this.telegraphChanges++;
    this.lastTelegraph = s.telegraph;

    if (p) {
      if (Math.abs(s.rudderOrder - p.rudderOrder) >= 1) this.rudderOrders++;
      const frozen = s.flags?.frozen ?? false;
      if (frozen) this.frozen_s += dt;
      else if (dt > 0) {
        const dx = s.pos.x - p.pos.x;
        const dy = s.pos.y - p.pos.y;
        this.distance_m += Math.hypot(dx, dy);
        if (s.sog > th.portSpeedLimit_kn) this.overSpeed_s += dt;
        if (s.depthBelowKeel < th.ukcAlarm_m) this.ukcBelow_s += dt;
        if (Math.abs(s.rot) > th.rotLimit_degPerMin) this.rotOver_s += dt;
      }
      if ((s.flags?.aground ?? false) && !(p.flags?.aground ?? false)) this.aground++;
      if ((s.flags?.collision ?? false) && !(p.flags?.collision ?? false)) this.collision++;
    }
    this.prev = s;
  }

  summary(): DebriefSummary {
    const duration = this.first_t === null ? 0 : Math.max(0, this.last_t - this.first_t);
    const moving = Math.max(0, duration - this.frozen_s);
    const distance_nm = this.distance_m / M_PER_NM;
    return {
      samples: this.samples,
      duration_s: duration,
      minUkc_m: this.minUkc,
      minUkcAt_s: this.minUkcAt,
      maxRudder_deg: this.maxRudder,
      maxRudderAt_s: this.maxRudderAt,
      maxRot_degPerMin: this.maxRot,
      maxRotAt_s: this.maxRotAt,
      telegraphChanges: this.telegraphChanges,
      rudderOrders: this.rudderOrders,
      hardOverCount: this.hardOverCount,
      distance_nm,
      avgSpeed_kn: moving > 0 ? (distance_nm / moving) * 3600 : 0,
      maxSog_kn: this.maxSog,
      timeOverSpeedLimit_s: this.overSpeed_s,
      timeUkcBelowAlarm_s: this.ukcBelow_s,
      timeRotOverLimit_s: this.rotOver_s,
      agroundEvents: this.aground,
      collisionEvents: this.collision,
      frozen_s: this.frozen_s,
      thresholds: { ...this.thresholds },
    };
  }
}

/** 一次計算整段狀態序列(重播檢視用)。 */
export function summarize(states: readonly OwnShipState[], thresholds: DebriefThresholds = DEFAULT_THRESHOLDS): DebriefSummary {
  const acc = new DebriefAccumulator(thresholds);
  for (const s of states) acc.push(s);
  return acc.summary();
}
