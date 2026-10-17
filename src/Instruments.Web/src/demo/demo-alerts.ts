// 示範模式的感測器故障層、警報管理與 BNWAS(離線預覽中央警報面板、BNWAS 指示與教官站「真值 vs 指示值」用;
// 簡化版,行為對齊 docs/design/sensors-alerts.md 的契約,但不是 C# SimCore 的實作,不可用於訓練或驗證):
//  - DemoSensors:gyroDrift/gyroFreeze/gyroFailure、gpsJump/gpsOffset/gpsFailure、echoSounderFailure、logFailure 的指示值;
//  - DemoAlerts:故障類警報 + 一則示範假警報(headingMonitor,4–150 s)+ bnwas 警報;MSC.302 狀態機(確認、靜音 30 s 重響、
//    warning 未確認 300 s 升級、已解除未確認);
//  - DemoBnwas:模式 on、休眠 180 s;示範開始時休眠期已過 170 s(約 10 s 後第 1 階段、25 s 第 2 階段),
//    第 3 階段 30 s 後由示範「自動重設」以形成循環;駕駛台操作與 bnwasReset 會重設。

import type { SimEvent, SimEventSource } from '../types/event.js';
import type { AlertItem, AlertState, BnwasMode, BnwasStage, BnwasStatus, SensorStatus, TruthState } from '../types/alerts.js';
import { BNWAS_MODES } from '../types/alerts.js';
import type { OwnShipState } from '../types/state.js';
import { ALERT_CATALOGUE, BNWAS_DORMANT_MAX_S, BNWAS_DORMANT_MIN_S, defaultAudible, sortAlerts, type AlertCatalogueEntry } from '../lib/alerts.js';
import { clamp, degToRad, headingDifference, normalizeHeading } from '../lib/angles.js';

export type DemoEmit = (kind: string, source: SimEventSource, extra?: Partial<SimEvent>) => void;

/** 會重設 BNWAS 的駕駛台操作(sensors-alerts.md 第 4 節) */
export const BNWAS_RESET_COMMANDS: ReadonlySet<string> = new Set(['rudder', 'telegraph', 'rpm', 'thruster', 'autopilot', 'steeringMode', 'alertAcknowledge', 'alertSilence']);

/** 示範假警報:航向監視(warning)於此時段成立 */
export const DEMO_FAKE_ALERT = { id: 'headingMonitor', from_s: 4, to_s: 150, text: '航向監視:電羅經與磁羅經差異(示範假警報)' } as const;
/** 示範 BNWAS:開始時休眠期已經過的秒數 */
export const DEMO_BNWAS_PRE_ELAPSED_S = 170;
/** 示範 BNWAS:第 3 階段後自動重設的秒數(形成循環) */
export const DEMO_BNWAS_AUTO_RESET_S = 30;
const ESCALATE_S = 300;
const SILENCE_S = 30;
const HEADING_MONITOR_DEG = 5;

const r2 = (v: number) => Math.round(v * 100) / 100;

// ------------------------------------------------------------------ 感測器

export interface SensorOverlay {
  heading: number;
  pos: { x: number; y: number };
  cog: number;
  sog: number;
  stw: number;
  depthBelowKeel: number;
  sensors: SensorStatus;
}

export class DemoSensors {
  private driftBase = 0;
  private driftRate = 0;
  private driftStart_t = 0;
  private frozenHeading: number | null = null;
  private heldHeading: number | null = null;
  private heldGnss: { x: number; y: number; cog: number; sog: number } | null = null;
  private heldDepth: number | null = null;
  private heldStw: number | null = null;
  private log: { bias: number; factor: number } | null = null;
  private gps: { dx: number; dy: number; ramp_s: number; start_t: number } | null = null;
  /** 磁羅經剩餘誤差(度) */
  magneticError_deg = 0;

  driftAt(t: number): number {
    return this.driftBase + (this.driftRate * (t - this.driftStart_t)) / 60;
  }

  /** 指示航向(gyroFailure 保持最後值、gyroFreeze 凍結、gyroDrift 累積漂移)。 */
  heading(s: OwnShipState): number {
    if (this.heldHeading !== null) return this.heldHeading;
    if (this.frozenHeading !== null) return this.frozenHeading;
    return normalizeHeading(s.heading + this.driftAt(s.t));
  }

  inject(name: string, args: Record<string, unknown>, s: OwnShipState): void {
    const n = (k: string, d: number) => (typeof args[k] === 'number' && Number.isFinite(args[k]) ? (args[k] as number) : d);
    const ind = this.overlay(s, []);
    switch (name) {
      case 'gyroDrift':
        // 重複注入:自目前誤差以新速率續漂
        this.driftBase = this.driftAt(s.t);
        this.driftStart_t = s.t;
        this.driftRate = n('rate_degPerMin', 1);
        break;
      case 'gyroFreeze':
        this.frozenHeading = ind.heading;
        break;
      case 'gyroFailure':
        this.heldHeading = ind.heading;
        break;
      case 'gpsJump':
      case 'gpsOffset': {
        const dist = n('distance_m', name === 'gpsJump' ? 500 : 200);
        const brg = degToRad(n('bearing_deg', name === 'gpsJump' ? 90 : 45));
        this.gps = { dx: Math.sin(brg) * dist, dy: Math.cos(brg) * dist, ramp_s: name === 'gpsJump' ? 0 : Math.max(0, n('ramp_s', 60)), start_t: s.t };
        break;
      }
      case 'gpsFailure':
        this.heldGnss = { x: ind.pos.x, y: ind.pos.y, cog: ind.cog, sog: ind.sog };
        break;
      case 'echoSounderFailure':
        this.heldDepth = ind.depthBelowKeel;
        break;
      case 'logFailure':
        if (typeof args['bias_kn'] === 'number' || typeof args['factor'] === 'number') this.log = { bias: n('bias_kn', 0), factor: n('factor', 1) };
        else this.heldStw = ind.stw;
        break;
      default:
        break;
    }
  }

  clear(name: string): void {
    switch (name) {
      case 'gyroDrift':
        this.driftBase = 0;
        this.driftRate = 0;
        break;
      case 'gyroFreeze':
        this.frozenHeading = null;
        break;
      case 'gyroFailure':
        this.heldHeading = null;
        break;
      case 'gpsJump':
      case 'gpsOffset':
        this.gps = null;
        break;
      case 'gpsFailure':
        this.heldGnss = null;
        break;
      case 'echoSounderFailure':
        this.heldDepth = null;
        break;
      case 'logFailure':
        this.log = null;
        this.heldStw = null;
        break;
      default:
        break;
    }
  }

  /** logFailure 是否為「無效」模式(無 bias/factor 參數;只有此模式發警報) */
  get logInvalid(): boolean {
    return this.heldStw !== null;
  }

  get headingValid(): boolean {
    return this.heldHeading === null;
  }

  overlay(s: OwnShipState, _faults: readonly string[]): SensorOverlay {
    let x = s.pos.x;
    let y = s.pos.y;
    let cog = s.cog;
    let sog = s.sog;
    if (this.gps) {
      const f = this.gps.ramp_s > 0 ? clamp((s.t - this.gps.start_t) / this.gps.ramp_s, 0, 1) : 1;
      x += this.gps.dx * f;
      y += this.gps.dy * f;
    }
    if (this.heldGnss) ({ x, y, cog, sog } = this.heldGnss);
    const stw = this.heldStw ?? (this.log ? s.stw * this.log.factor + this.log.bias : s.stw);
    return {
      heading: this.heading(s),
      pos: { x, y },
      cog,
      sog,
      stw,
      depthBelowKeel: this.heldDepth ?? s.depthBelowKeel,
      sensors: {
        headingValid: this.heldHeading === null,
        positionValid: this.heldGnss === null,
        depthValid: this.heldDepth === null,
        stwValid: this.heldStw === null,
        magneticHeading: r2(normalizeHeading(s.heading + this.magneticError_deg)),
      },
    };
  }

  /** 真值區塊(state.truth)。 */
  truth(s: OwnShipState, o: SensorOverlay): TruthState {
    return {
      heading: r2(s.heading),
      pos: { ...s.pos },
      cog: r2(s.cog),
      sog: r2(s.sog),
      stw: r2(s.stw),
      depthBelowKeel: r2(s.depthBelowKeel),
      headingError_deg: r2(headingDifference(s.heading, o.heading)),
      positionError_m: r2(Math.hypot(o.pos.x - s.pos.x, o.pos.y - s.pos.y)),
    };
  }
}

// ------------------------------------------------------------------ 警報

interface DemoAlert extends AlertItem {
  entry: AlertCatalogueEntry;
}

export class DemoAlerts {
  private readonly items = new Map<string, DemoAlert>();

  constructor(private readonly emit: DemoEmit) {}

  get list(): AlertItem[] {
    return sortAlerts([...this.items.values()].map((a) => this.view(a)));
  }

  has(id: string): boolean {
    return this.items.has(id);
  }

  private view(a: DemoAlert): AlertItem {
    const { entry: _e, ...rest } = a;
    return { ...rest, audible: defaultAudible(a.priority, a.state) };
  }

  private data(a: DemoAlert, extra: Record<string, unknown> = {}): Record<string, unknown> {
    const d: Record<string, unknown> = { id: a.id, priority: a.priority, category: a.category, source: a.source, state: a.state, count: a.count, raisedAt_s: a.raisedAt_s, ...extra };
    if (a.silencedUntil_s !== undefined && a.state === 'activeSilenced') d['silencedUntil_s'] = a.silencedUntil_s;
    return d;
  }

  private fire(kind: string, a: DemoAlert, extra: Record<string, unknown> = {}, stateOverride?: string): void {
    const data = this.data(a, extra);
    if (stateOverride) data['state'] = stateOverride;
    this.emit(kind, 'engine', { itemId: a.id, message: a.text, visible: true, data });
  }

  /**
   * 以各警報的條件更新狀態機(每步呼叫)。conditions:id → 是否成立;texts 可覆寫顯示文字(示範假警報)。
   * bnwasPriority:BNWAS 階段 1 為 caution、2 以上 alarm(null = 階段 0)。
   */
  update(t: number, conditions: ReadonlyMap<string, boolean>, bnwasPriority: 'caution' | 'alarm' | null, texts: ReadonlyMap<string, string> = new Map()): void {
    const t2 = r2(t);
    for (const entry of ALERT_CATALOGUE) {
      const cond = entry.id === 'bnwas' ? bnwasPriority !== null : conditions.get(entry.id) === true;
      const a = this.items.get(entry.id);
      if (cond) {
        if (!a) {
          const item: DemoAlert = { entry, id: entry.id, number: entry.number, priority: entry.id === 'bnwas' ? (bnwasPriority ?? 'caution') : entry.priority, category: entry.category, source: entry.source, state: 'activeUnacknowledged', text: texts.get(entry.id) ?? entry.text, raisedAt_s: t2, escalated: false, count: 1, audible: false };
          this.items.set(entry.id, item);
          this.fire('alertRaised', item, { repeat: false });
        } else if (a.state === 'rectifiedUnacknowledged') {
          a.state = 'activeUnacknowledged';
          a.count++;
          a.raisedAt_s = t2;
          this.fire('alertRaised', a, { repeat: true });
        }
      } else if (a) {
        if (a.id === 'bnwas' || a.state === 'activeAcknowledged') {
          this.items.delete(a.id);
          this.fire('alertRectified', a, {}, 'normal');
        } else if (a.state !== 'rectifiedUnacknowledged') {
          a.state = 'rectifiedUnacknowledged';
          delete a.silencedUntil_s;
          this.fire('alertRectified', a);
        }
      }
    }
    for (const a of this.items.values()) {
      if (a.state === 'activeSilenced' && a.silencedUntil_s !== undefined && t >= a.silencedUntil_s) {
        a.state = 'activeUnacknowledged';
        delete a.silencedUntil_s;
        this.fire('alertReannunciated', a);
      }
      if (a.id === 'bnwas' && bnwasPriority === 'alarm' && a.priority === 'caution') {
        a.priority = 'alarm';
        a.escalated = true;
        a.state = 'activeUnacknowledged';
        this.fire('alertEscalated', a, { from: 'caution', to: 'alarm', reason: 'source', after_s: r2(t - a.raisedAt_s) });
      } else if (a.priority === 'warning' && (a.state === 'activeUnacknowledged' || a.state === 'activeSilenced') && t - a.raisedAt_s >= ESCALATE_S) {
        a.priority = 'alarm';
        a.escalated = true;
        a.state = 'activeUnacknowledged';
        delete a.silencedUntil_s;
        this.fire('alertEscalated', a, { from: 'warning', to: 'alarm', reason: 'unacknowledged', after_s: r2(t - a.raisedAt_s) });
      }
    }
  }

  /** alertAcknowledge:回傳 false 表示未知 id。caution 不需確認(略過)。 */
  acknowledge(id: string | undefined, t: number): boolean {
    const targets = this.targets(id);
    if (targets === null) return false;
    for (const a of targets) {
      if (a.priority === 'caution') continue;
      if (a.state === 'rectifiedUnacknowledged') {
        this.items.delete(a.id);
        this.fire('alertAcknowledged', a, { ackTime_s: r2(t - a.raisedAt_s) }, 'normal');
      } else if (a.state === 'activeUnacknowledged' || a.state === 'activeSilenced') {
        a.state = 'activeAcknowledged';
        a.acknowledgedAt_s = r2(t);
        delete a.silencedUntil_s;
        this.fire('alertAcknowledged', a, { ackTime_s: r2(t - a.raisedAt_s) });
      }
    }
    return true;
  }

  /** alertSilence:未確認且有聲響者靜音 30 s;回傳 false 表示未知 id。 */
  silence(id: string | undefined, t: number): boolean {
    const targets = this.targets(id);
    if (targets === null) return false;
    for (const a of targets) {
      if (a.state !== 'activeUnacknowledged' || a.priority === 'caution') continue;
      a.state = 'activeSilenced' as AlertState;
      a.silencedUntil_s = r2(t + SILENCE_S);
      this.fire('alertSilenced', a, { silencedUntil_s: a.silencedUntil_s });
    }
    return true;
  }

  private targets(id: string | undefined): DemoAlert[] | null {
    if (!id || id === 'all') return [...this.items.values()];
    if (!ALERT_CATALOGUE.some((e) => e.id === id)) return null;
    const a = this.items.get(id);
    return a ? [a] : [];
  }

  clear(): void {
    this.items.clear();
  }
}

/** 示範的故障 → 警報條件(logFailure 只在無效模式;門檻類 headingMonitor 依電羅經/磁羅經差,另含示範假警報時段)。 */
export function demoAlertConditions(faults: readonly string[], t: number, sensors: DemoSensors, s: OwnShipState): { conditions: Map<string, boolean>; texts: Map<string, string> } {
  const conditions = new Map<string, boolean>();
  const texts = new Map<string, string>();
  for (const f of faults) if (f !== 'logFailure' && ALERT_CATALOGUE.some((e) => e.id === f)) conditions.set(f, true);
  if (faults.includes('logFailure') && sensors.logInvalid) conditions.set('logFailure', true);
  const fake = t >= DEMO_FAKE_ALERT.from_s && t < DEMO_FAKE_ALERT.to_s;
  const mismatch = sensors.headingValid && Math.abs(headingDifference(normalizeHeading(s.heading + sensors.magneticError_deg), sensors.heading(s))) > HEADING_MONITOR_DEG;
  if (fake || mismatch) conditions.set('headingMonitor', true);
  if (fake && !mismatch) texts.set('headingMonitor', DEMO_FAKE_ALERT.text);
  return { conditions, texts };
}

// ------------------------------------------------------------------ BNWAS

export class DemoBnwas {
  mode: BnwasMode = 'on';
  dormant_s = BNWAS_DORMANT_MIN_S;
  stage2_s = 15;
  stage3_s = 90;
  stage: BnwasStage = 0;
  private periodStart_t = -DEMO_BNWAS_PRE_ELAPSED_S;
  private stageStart_t = 0;
  private wasActive = true;

  constructor(private readonly emit: DemoEmit) {}

  active(autopilotEnabled: boolean): boolean {
    return this.mode === 'on' || (this.mode === 'auto' && autopilotEnabled);
  }

  step(t: number, autopilotEnabled: boolean): void {
    const active = this.active(autopilotEnabled);
    if (!active) {
      if (this.stage > 0) this.setStage(0, 'inactive', t);
      this.wasActive = false;
      return;
    }
    if (!this.wasActive) {
      this.periodStart_t = t;
      this.wasActive = true;
    }
    if (this.stage === 0 && t - this.periodStart_t >= this.dormant_s) this.setStage(1, 'dormantElapsed', t);
    else if (this.stage === 1 && t - this.stageStart_t >= this.stage2_s) this.setStage(2, 'notReset', t);
    else if (this.stage === 2 && t - this.stageStart_t >= this.stage3_s) this.setStage(3, 'notReset', t);
    else if (this.stage === 3 && t - this.stageStart_t >= DEMO_BNWAS_AUTO_RESET_S) this.reset(t, 'demoAutoReset', 'engine', autopilotEnabled);
  }

  private setStage(stage: BnwasStage, reason: string, t: number): void {
    const from = this.stage;
    this.stage = stage;
    this.stageStart_t = t;
    if (stage === 0) this.periodStart_t = t;
    this.emit('bnwasStage', 'engine', { itemId: 'bnwas', visible: true, data: { stage, from, reason } });
  }

  /**
   * 重設:未作動(off,或 auto 而自動舵未啟用)時不動作、不發事件(與核心 Bnwas.Reset 一致);
   * 作動中一般操作只在階段 ≥ 1 時發事件,bnwasReset 指令一律發。
   */
  reset(t: number, by: string, source: SimEventSource, autopilotEnabled: boolean): void {
    if (!this.active(autopilotEnabled)) return;
    const fromStage = this.stage;
    this.stage = 0;
    this.periodStart_t = t;
    this.stageStart_t = t;
    if (fromStage >= 1 || by === 'bnwasReset' || by === 'demoAutoReset') this.emit('bnwasReset', source, { itemId: 'bnwas', data: { fromStage, by } });
  }

  setMode(value: unknown, args: Record<string, unknown>, t: number, source: SimEventSource): void {
    const to = typeof value === 'string' && (BNWAS_MODES as readonly string[]).includes(value) ? (value as BnwasMode) : this.mode;
    const d = args['dormant_s'];
    if (typeof d === 'number' && Number.isFinite(d)) this.dormant_s = Math.round(clamp(d, BNWAS_DORMANT_MIN_S, BNWAS_DORMANT_MAX_S));
    const s3 = args['stage3_s'];
    if (typeof s3 === 'number' && Number.isFinite(s3) && s3 > 0) this.stage3_s = s3;
    const from = this.mode;
    const fromStage = this.stage;
    this.mode = to;
    this.stage = 0;
    this.periodStart_t = t;
    this.stageStart_t = t;
    this.emit('bnwasModeChanged', source, { itemId: 'bnwas', data: { from, to, fromStage, dormant_s: this.dormant_s, stage2_s: this.stage2_s, stage3_s: this.stage3_s } });
  }

  /** 對應 BNWAS 警報的優先序:階段 1 caution、2 以上 alarm、0 為 null */
  get alertPriority(): 'caution' | 'alarm' | null {
    return this.stage === 0 ? null : this.stage === 1 ? 'caution' : 'alarm';
  }

  status(t: number, autopilotEnabled: boolean): BnwasStatus {
    const active = this.active(autopilotEnabled);
    const out: BnwasStatus = { mode: this.mode, active, stage: this.stage, dormant_s: this.dormant_s };
    if (active) {
      if (this.stage === 0) out.remaining_s = r2(Math.max(0, this.dormant_s - (t - this.periodStart_t)));
      else if (this.stage === 1) out.remaining_s = r2(Math.max(0, this.stage2_s - (t - this.stageStart_t)));
      else if (this.stage === 2) out.remaining_s = r2(Math.max(0, this.stage3_s - (t - this.stageStart_t)));
    }
    return out;
  }
}

/** 指示位置換算緯經度(與 DemoSim.updateDerived 相同的平面近似)。 */
export function demoLatLon(originLat: number, originLon: number, x: number, y: number): { lat: number; lon: number } {
  const latRad = degToRad(originLat);
  return { lat: originLat + y / 111_320, lon: originLon + x / (111_320 * Math.cos(latRad)) };
}
