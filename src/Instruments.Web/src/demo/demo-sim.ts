// 示範模式的簡單運動學(規劃書第 7 章第一版儀器獨立預覽用;不是第 6 章的 MMG 模型):
//  - 航向:一階 Nomoto 響應 T·ṙ + r = K·δ,K、T 隨瞬時船速換算(K = K'·U/L,T = T'·L/U);
//  - 速度:一階滯後趨向車鐘對應的參考速度,迴轉時依舵角打折;
//  - 舵機、主機轉速、側推各以速率限制或一階滯後模擬;
//  - 位置以本地 ENU 積分,加上均勻流;COG/SOG 由對地速度求得;
//  - 固定步長 50 Hz(與核心相同),輸出 25 Hz。
// 係數以 FSB1 滿載試俥的迴旋圈與 Z 形概略對齊(K' ≈ 1.1、T' ≈ 1.5),僅供畫面預覽。
// 情境引擎(設計文件 docs/design/scenario-engine.md)的離線預覽:產生幾個假事件(時間表訊息/語音、故障注入、確認、
// 手動觸發、結束練習)、state.scenario/route/visibility_nm 欄位與假的 summary,讓教官站不連核心也能測試面板。
// 操舵模式(D32/D35):state.steering 與 steeringMode 指令(fu/nfu/autopilot/emergency、泵、NFU 推桿;應急操舵下拒絕自動舵並發
// commandRejected;模式/泵變更發 steeringModeChanged);兩艘假目標船(直線等速:一艘交叉相遇、CPA 小於門檻而處於 cpaAlarm,
// 一艘錨泊)含 range/bearing/CPA/TCPA,讓航跡圖與 CPA/TCPA 小表離線可見。

import { clamp, degToRad, knotsToMps, mpsToKnots, normalizeHeading, radToDeg, headingDifference } from '../lib/angles.js';
import { getShipConfig, telegraphStep, type ShipDisplayConfig } from '../lib/ship-config.js';
import { defaultState } from '../lib/state-parser.js';
import type { SimCommand } from '../types/command.js';
import type { SimEvent, SimEventSource } from '../types/event.js';
import type { TargetState } from '../types/target.js';
import { STEERING_MODES, STEERING_PUMPS, TELEGRAPH_POSITIONS, type OwnShipState, type ShipId, type SteeringModeValue, type SteeringPump, type SteeringStatus, type TelegraphPosition } from '../types/state.js';

export const DEMO_DT = 0.02; // 50 Hz

/** 無因次 Nomoto 係數(示範用) */
const K_PRIME = 1.1;
const T_PRIME = 1.5;
/** 計算 K、T 時的最低速度 (m/s),避免零速時 T 無限大 */
const U_MIN_FOR_NOMOTO = 0.6;
/** 滿舵時的穩態速度比(FSB1 試俥:14.5 kn → 4.6–4.9 kn) */
const TURN_SPEED_LOSS = 0.67;
/** 橫向漂移係數:v = −c·r·L */
const SWAY_COEFF = 0.4;

export interface DemoOptions {
  shipId?: ShipId;
  /** 初始位置(本地 ENU 原點對應的緯經度;預設麥寮港外) */
  originLat?: number;
  originLon?: number;
  initialHeading?: number;
  initialSpeed_kn?: number;
  waterDepth_m?: number;
  wind?: { trueSpeed: number; trueDir: number };
  current?: { set: number; drift: number };
}

interface AutopilotState {
  enabled: boolean;
  heading: number;
  rotLimit: number;
}

/** 示範用假時間表:固定時刻發出訊息/語音事件(itemId 供時間表面板對照)。 */
const DEMO_TIMELINE: ReadonlyArray<{ id: string; at_s: number; make: (t: number) => Partial<SimEvent> & { kind: string } }> = [
  { id: 'demoBrief', at_s: 30, make: () => ({ kind: 'message', data: { to: 'instructor', text: '示範模式:時間表事件預覽(非核心)', severity: 'info' } }) },
  { id: 'demoEcr', at_s: 60, make: () => ({ kind: 'voiceScript', data: { role: 'ecr', channel: 'phone', text: '駕駛台,機控室。主機運轉正常。', lang: 'zh-TW' } }) },
  { id: 'demoVisible', at_s: 120, make: () => ({ kind: 'message', visible: true, data: { to: 'all', text: 'VTS:請注意前方出港船', severity: 'warning' } }) },
];

export const DEMO_PARAMETER_SET_VERSION = '2026.10.1';
const DEMO_ROUTE_WP_DISTANCE_M = 2 * 1852;
const NM_M = 1852;
/** 示範 CPA 警報門檻(nm;核心由情境 traffic.cpaAlarm_nm 給,示範固定) */
export const DEMO_CPA_ALARM_NM = 0.5;
/** 應急操舵的舵機速率比(設計文件第 10 節:無資料時取隨動速率一半) */
const EMERGENCY_RUDDER_RATE_FACTOR = 0.5;

/** 示範目標船(直線等速;位置相對自船初始位置與初始航向)。 */
interface DemoTarget {
  base: Omit<TargetState, 'pos' | 'range_nm' | 'bearing_deg' | 'relBearing_deg' | 'cpa_nm' | 'tcpa_min'>;
  x: number;
  y: number;
  /** 速度 (m/s) 與航向 (度) */
  speedMps: number;
  course: number;
  alarm: boolean;
}

export class DemoSim {
  readonly cfg: ShipDisplayConfig;
  private readonly opts: Required<DemoOptions>;
  private state: OwnShipState;
  private frozen = false;
  /** 凍結次數(核心 summary.general.freezes) */
  private freezes = 0;
  private autopilot: AutopilotState = { enabled: false, heading: 0, rotLimit: 15 };
  /** 側推造成的迴轉率 (rad/s),與舵效分開一階滯後 */
  private rThruster = 0;
  /** 尚未取走的假事件(DemoSource 以 {"type":"event"} 框推送) */
  private pendingEvents: SimEvent[] = [];
  private seq = 0;
  private firedTimeline = new Set<string>();
  private manualFired = new Set<string>();
  private phase = 'normal';
  private ended = false;
  private acks: Array<{ kind: string; t: number; role?: string }> = [];
  private faultInjections: Array<{ fault: string; t: number }> = [];
  private steering: SteeringStatus = { mode: 'fu', pump: 'both' };
  private targets: DemoTarget[] = [];
  /** 自船對地速度 ENU (m/s),供目標船 CPA 計算 */
  private ownVe = 0;
  private ownVn = 0;

  constructor(options: DemoOptions = {}) {
    const shipId = options.shipId ?? 'FSB1';
    this.cfg = getShipConfig(shipId);
    this.opts = {
      shipId,
      originLat: options.originLat ?? 23.8,
      originLon: options.originLon ?? 120.12,
      initialHeading: options.initialHeading ?? 0,
      initialSpeed_kn: options.initialSpeed_kn ?? 0,
      waterDepth_m: options.waterDepth_m ?? 20,
      wind: options.wind ?? { trueSpeed: 12, trueDir: 40 },
      current: options.current ?? { set: 200, drift: 0.8 },
    };
    this.state = this.initialState();
  }

  private initialState(): OwnShipState {
    const s = defaultState(this.opts.shipId);
    s.heading = normalizeHeading(this.opts.initialHeading);
    s.cog = s.heading;
    s.stw = this.opts.initialSpeed_kn;
    s.sog = s.stw;
    s.u = knotsToMps(this.opts.initialSpeed_kn);
    s.wind = { ...this.opts.wind };
    s.current = { ...this.opts.current };
    s.waterDepth = this.opts.waterDepth_m;
    s.draft = { fore: this.cfg.draftLoaded_m, aft: this.cfg.draftLoaded_m };
    s.depthBelowKeel = this.opts.waterDepth_m - this.cfg.draftLoaded_m;
    s.engine = { state: 'running', startsRemaining: 12, load_pct: 0 };
    // 初始車鐘依初速挑最接近的前進段
    const step = this.cfg.telegraph
      .filter((t) => t.rpm >= 0)
      .reduce((best, t) => (Math.abs(t.speedLoaded_kn - s.stw) < Math.abs(best.speedLoaded_kn - s.stw) ? t : best));
    s.telegraph = step.position;
    s.rpm = step.rpm;
    s.rpmOrder = step.rpm;
    s.visibility_nm = 10;
    s.steering = { ...this.steering };
    this.targets = this.makeTargets();
    this.updateDerived(s);
    return s;
  }

  // ------------------------------------------------------------ 假目標船(離線預覽用)

  /** 兩艘假目標船:交叉相遇船在右艏 1.5 nm、朝自船艏前 0.15 nm 處駛來(CPA < 門檻 → cpaAlarm);錨泊船在左正橫 1.2 nm。 */
  private makeTargets(): DemoTarget[] {
    const psi = this.opts.initialHeading;
    const place = (bearingRel: number, nm: number) => {
      const b = degToRad(normalizeHeading(psi + bearingRel));
      return { x: Math.sin(b) * nm * NM_M, y: Math.cos(b) * nm * NM_M };
    };
    const crossing = place(45, 1.5);
    const aim = place(0, 0.15);
    const course = normalizeHeading(radToDeg(Math.atan2(aim.x - crossing.x, aim.y - crossing.y)));
    const anchored = place(270, 1.2);
    return [
      {
        base: { id: 'DEMO-01', name: 'DEMO CROSSING', mmsi: 416000001, shipType: 'cargo', loa: 120, beam: 20, draft: 7, heading: course, cog: course, sog: 9, stw: 9, rot: 0, aisOn: true, lights: 'powerDriven', behaviour: 'hold', colreg: 'crossing/giveWay' },
        x: crossing.x,
        y: crossing.y,
        speedMps: knotsToMps(9),
        course,
        alarm: false,
      },
      {
        base: { id: 'DEMO-02', name: 'DEMO ANCHORED', mmsi: 416000002, shipType: 'tanker', loa: 90, beam: 16, draft: 6, heading: normalizeHeading(psi + 180), cog: 0, sog: 0, stw: 0, rot: 0, aisOn: false, lights: 'anchored', behaviour: 'anchored' },
        x: anchored.x,
        y: anchored.y,
        speedMps: 0,
        course: normalizeHeading(psi + 180),
        alarm: false,
      },
    ];
  }

  /** 目標船直線等速前進、更新相對量;CPA 進入/離開門檻時發 cpaAlarm / cpaClear(每秒評估,與核心的 1 s 評估步一致)。 */
  private stepTargets(s: OwnShipState, dt: number): void {
    for (const tg of this.targets) {
      const c = degToRad(tg.course);
      tg.x += Math.sin(c) * tg.speedMps * dt;
      tg.y += Math.cos(c) * tg.speedMps * dt;
    }
    this.updateTargets(s, s.tick % 50 === 0);
  }

  private updateTargets(s: OwnShipState, evaluate: boolean): void {
    const latRad = degToRad(this.opts.originLat);
    s.targets = this.targets.map((tg) => {
      const dx = tg.x - s.pos.x;
      const dy = tg.y - s.pos.y;
      const c = degToRad(tg.course);
      const rvx = Math.sin(c) * tg.speedMps - this.ownVe;
      const rvy = Math.cos(c) * tg.speedMps - this.ownVn;
      const v2 = rvx * rvx + rvy * rvy;
      const tcpaS = v2 > 1e-6 ? -(dx * rvx + dy * rvy) / v2 : 0;
      const cpaM = Math.hypot(dx + rvx * tcpaS, dy + rvy * tcpaS);
      const range = Math.hypot(dx, dy);
      const bearing = normalizeHeading(radToDeg(Math.atan2(dx, dy)));
      const t: TargetState = {
        ...tg.base,
        pos: { x: Math.round(tg.x * 10) / 10, y: Math.round(tg.y * 10) / 10, lat: this.opts.originLat + tg.y / 111_320, lon: this.opts.originLon + tg.x / (111_320 * Math.cos(latRad)) },
        range_nm: Math.round((range / NM_M) * 1000) / 1000,
        bearing_deg: Math.round(bearing * 10) / 10,
        relBearing_deg: Math.round(normalizeHeading(bearing - s.heading) * 10) / 10,
        cpa_nm: Math.round((cpaM / NM_M) * 1000) / 1000,
        tcpa_min: Math.round((tcpaS / 60) * 10) / 10,
      };
      if (tg.base.aisOn) t.ais = { mmsi: tg.base.mmsi, name: tg.base.name, shipType: 70, lat: t.pos.lat, lon: t.pos.lon, cog: tg.base.cog, sog: tg.base.sog, heading: tg.base.heading };
      if (evaluate) {
        const inAlarm = t.cpa_nm < DEMO_CPA_ALARM_NM && t.tcpa_min > 0 && tg.speedMps > 0;
        if (inAlarm && !tg.alarm) this.emit('cpaAlarm', 'traffic', { targetId: t.id, range_nm: t.range_nm, cpa_nm: t.cpa_nm, tcpa_min: t.tcpa_min, visible: true }, s);
        else if (!inAlarm && tg.alarm) this.emit('cpaClear', 'traffic', { targetId: t.id, range_nm: t.range_nm, cpa_nm: t.cpa_nm, tcpa_min: t.tcpa_min }, s);
        tg.alarm = inAlarm;
      }
      return t;
    });
  }

  // ------------------------------------------------------------ 操舵模式(離線預覽用)

  /** 切換模式/泵:有變更才發 steeringModeChanged {from, to, pump};nfuActive 由呼叫者設定。 */
  private setSteering(mode: SteeringModeValue, pump: SteeringPump | undefined, source: SimEventSource, nfuActive?: boolean): void {
    const from = this.steering;
    const next: SteeringStatus = { mode, pump: pump ?? from.pump };
    if (mode === 'nfu') next.nfuActive = nfuActive ?? false;
    const s = this.state;
    // 進出 nfu 時舵令同步到目前舵角(D35);離開 autopilot 時維持目前舵令
    if ((from.mode === 'nfu') !== (mode === 'nfu')) s.rudderOrder = s.rudder;
    const changed = from.mode !== next.mode || from.pump !== next.pump;
    this.steering = next;
    s.steering = { ...next };
    this.autopilot.enabled = mode === 'autopilot';
    if (changed) this.emit('steeringModeChanged', source, { data: { from: from.mode, to: next.mode, pump: next.pump } });
  }

  // ------------------------------------------------------------ 假事件與情境狀態(離線預覽用)

  private emit(kind: string, source: SimEventSource, extra: Partial<SimEvent> = {}, s: OwnShipState = this.state): SimEvent {
    const ev: SimEvent = { kind, tick: s.tick, t: Math.round(s.t * 100) / 100, source, visible: false, seq: this.seq++, phase: this.phase, ...extra };
    this.pendingEvents.push(ev);
    return ev;
  }

  /** 取走並清空待推送的假事件。 */
  takeEvents(): SimEvent[] {
    const out = this.pendingEvents;
    this.pendingEvents = [];
    return out;
  }

  /** 假的核心 summary(設計文件第 4.8 節欄位子集;數值由目前狀態概略推得,只供畫面測試)。 */
  assessmentSummary(): Record<string, unknown> {
    const s = this.state;
    const duration = Math.round(s.t);
    const required = this.faultInjections.map((f, i) => {
      const ack = this.acks.find((a) => a.kind === 'notifyEcr' && a.t >= f.t);
      const dt = ack ? Math.round(ack.t - f.t) : undefined;
      const met = dt !== undefined && dt <= 120;
      return { id: `notifyEcr${i === 0 ? '' : i}`, state: ack ? (met ? 'met' : 'late') : s.t - f.t > 120 ? 'missed' : 'pending', dt_s: dt, within_s: 120, by: ack ? 'acknowledge:notifyEcr' : undefined, penalty: ack && met ? 0 : 10, paramKey: 'emergency.steeringFailure.notifyEcr_s', source: 'SMS-EMG-004', revision: '1' };
    });
    const penalty = required.reduce((sum, r) => sum + (r.state === 'missed' || r.state === 'late' ? r.penalty : 0), 0) + (s.flags?.aground ? 100 : 0);
    const score = Math.max(0, 100 - penalty);
    return {
      version: 1,
      advisory: true,
      final: this.ended,
      scenarioId: 'demo',
      shipId: s.shipId,
      loading: s.loading,
      parameterSet: { id: 'simosa-company-procedures', version: DEMO_PARAMETER_SET_VERSION, sha256: 'demo' },
      phases: [{ phase: this.phase, from_s: 0, to_s: duration }],
      general: { duration_s: duration, freezes: this.freezes, distance_nm: Math.round((Math.hypot(s.pos.x, s.pos.y) / 1852) * 100) / 100, avgSog_kn: Math.round(s.sog * 10) / 10, maxSog_kn: Math.round(s.sog * 10) / 10 },
      collisionAvoidance: { minCpa_nm: 9.9, encounters: 0, collisions: 0, targets: [] },
      track: { meanAbsXtd_m: Math.round(Math.abs(s.route?.xtd_m ?? 0)), maxXtd_m: Math.round(Math.abs(s.route?.xtd_m ?? 0)), timeOutsideCorridor_s: 0, corridorExcursions: 0, waypoints: [] },
      ukc: { min_m: s.depthBelowKeel, minAt_s: duration, timeBelowLimit_s: 0, excursions: 0, groundings: s.flags?.aground ? 1 : 0, limit_m: 1.0, limitSource: 'navigation.ukc.minimum_m' },
      manoeuvring: { rudderDistribution_s: { '0-5': duration, '5-10': 0, '10-20': 0, '20-35': 0, '>35': 0 }, hardOverCount: 0, hardOverTime_s: 0, rudderOrders: 0, maxRot_degPerMin: Math.round(Math.abs(s.rot) * 10) / 10, timeRotOverLimit_s: 0, rotExcursions: 0, telegraphChanges: 0, telegraphOrders: [{ order: s.telegraph, from_s: 0, to_s: duration, duration_s: duration }], rpmOrders: 0, thrusterOrders: 0, thrusterTime_s: 0, timeOverSpeedLimit_s: 0, maxOverSpeed_kn: 0, autopilotTime_s: 0, autopilotSwitches: 0 },
      faultResponses: this.faultInjections.map((f) => ({ fault: f.fault, injectedAt_s: Math.round(f.t), source: 'instructor', acknowledgements: this.acks.filter((a) => a.t >= f.t).map((a) => ({ kind: a.kind, t_s: Math.round(a.t), dt_s: Math.round(a.t - f.t), role: a.role })) })),
      events: { total: this.seq, byKind: {} },
      assessment: {
        score,
        passMark: 70,
        passed: score >= 70 && !s.flags?.aground,
        hardFailed: s.flags?.aground ?? false,
        hardFails: s.flags?.aground ? [{ id: 'grounding', rule: 'grounding', t_s: duration }] : [],
        bands: [{ id: 'rotOver', metric: 'manoeuvring.timeRotOverLimit_s', value: 0, warn: 0, fail: 30, state: 'ok', penalty: 0, paramKey: 'manoeuvring.rotLimit_degPerMin', source: 'DWO-007', revision: '3', clause: '5.4' }],
        required,
        parameters: [{ key: 'emergency.steeringFailure.notifyEcr_s', value: 120, unit: 's', source: 'SMS-EMG-004', revision: '1', clause: '2.3' }],
      },
    };
  }

  /** 示範的假時間表項目 id 清單(教官站可用 triggerTimeline 觸發)。 */
  static get timelineIds(): string[] {
    return DEMO_TIMELINE.map((d) => d.id);
  }

  private stepTimeline(): void {
    const t = this.state.t;
    for (const item of DEMO_TIMELINE) {
      if (this.firedTimeline.has(item.id) || t < item.at_s) continue;
      this.firedTimeline.add(item.id);
      this.emit('timelineTriggered', 'timeline', { itemId: item.id, data: { fireCount: 1, manual: false, forced: false } });
      const e = item.make(t);
      this.emit(e.kind, 'timeline', { ...e, itemId: item.id });
    }
  }

  private updateScenarioStatus(s: OwnShipState): void {
    s.scenario = {
      id: 'demo',
      phase: this.phase,
      parameterSetVersion: DEMO_PARAMETER_SET_VERSION,
      timelineFired: this.firedTimeline.size + this.manualFired.size,
      timelinePending: 0, // 核心語意:delay_s 排程中待執行的動作數(示範時間表無延遲動作);待觸發項目數由教官站依事件推導
      hardFailed: s.flags?.aground ?? false,
      ended: this.ended,
    };
    // 假航線:自初始位置沿初始航向的直線,下一航點在 2 nm 處;XTD 右正
    const psi = degToRad(this.opts.initialHeading);
    const ux = Math.sin(psi);
    const uy = Math.cos(psi);
    const along = s.pos.x * ux + s.pos.y * uy;
    const xtd = s.pos.x * uy - s.pos.y * ux;
    const wpX = ux * DEMO_ROUTE_WP_DISTANCE_M;
    const wpY = uy * DEMO_ROUTE_WP_DISTANCE_M;
    const dist = Math.hypot(wpX - s.pos.x, wpY - s.pos.y);
    s.route = {
      legIndex: 0,
      nextWaypoint: 'WP2',
      xtd_m: Math.round(xtd * 10) / 10,
      xtdLimit_m: 50,
      distanceToWaypoint_nm: Math.round((dist / 1852) * 100) / 100,
      bearingToWaypoint_deg: normalizeHeading(radToDeg(Math.atan2(wpX - s.pos.x, wpY - s.pos.y))),
      legCourse_deg: normalizeHeading(this.opts.initialHeading),
      speedLimit_kn: 8,
      complete: along >= DEMO_ROUTE_WP_DISTANCE_M,
    };
  }

  get current(): OwnShipState {
    return this.state;
  }

  get isFrozen(): boolean {
    return this.frozen;
  }

  /** 接受與核心相同的指令。 */
  apply(cmd: SimCommand): void {
    const s = this.state;
    switch (cmd.type) {
      case 'rudder': {
        if (typeof cmd.value === 'number' && Number.isFinite(cmd.value)) {
          // 人工舵令把 autopilot/nfu 切回 fu(state.schema.json steering 說明)
          if (this.steering.mode === 'autopilot' || this.steering.mode === 'nfu') this.setSteering('fu', undefined, 'instructor');
          s.rudderOrder = clamp(cmd.value, -this.cfg.rudderMax_deg, this.cfg.rudderMax_deg);
          this.autopilot.enabled = false;
        }
        break;
      }
      case 'telegraph': {
        const pos = cmd.value as TelegraphPosition;
        if (TELEGRAPH_POSITIONS.includes(pos)) {
          s.telegraph = pos;
          s.rpmOrder = telegraphStep(this.cfg, pos).rpm;
        }
        break;
      }
      case 'rpm': {
        if (typeof cmd.value === 'number' && Number.isFinite(cmd.value)) {
          s.rpmOrder = clamp(cmd.value, -this.cfg.rpmMax, this.cfg.rpmMax);
        }
        break;
      }
      case 'thruster': {
        if (typeof cmd.value === 'number' && Number.isFinite(cmd.value)) {
          s.thruster = { order: clamp(cmd.value, -1, 1), actual: s.thruster?.actual ?? 0 };
        }
        break;
      }
      case 'autopilot': {
        const a = cmd.args ?? {};
        const enabled = a['enabled'] === true;
        if (enabled && this.steering.mode === 'emergency') {
          // 應急操舵接不到自動舵:整個指令拒絕、狀態不變(D35)
          this.emit('commandRejected', 'instructor', { data: { command: 'autopilot', reason: 'emergencySteering', args: { ...a } } });
          break;
        }
        const heading = typeof a['heading'] === 'number' ? normalizeHeading(a['heading']) : this.autopilot.heading;
        const rotLimit = typeof a['rotLimit'] === 'number' ? clamp(a['rotLimit'], 1, 120) : this.autopilot.rotLimit;
        this.autopilot = { enabled, heading, rotLimit };
        if (!enabled) s.rudderOrder = 0;
        if (enabled) this.setSteering('autopilot', undefined, 'instructor');
        else if (this.steering.mode === 'autopilot') this.setSteering('fu', undefined, 'instructor');
        break;
      }
      case 'steeringMode': {
        const mode = cmd.value;
        if (typeof mode !== 'string' || !(STEERING_MODES as readonly string[]).includes(mode)) break;
        const a = cmd.args ?? {};
        const pumpRaw = typeof a['pump'] === 'number' ? String(a['pump']) : a['pump'];
        const pump = typeof pumpRaw === 'string' && (STEERING_PUMPS as readonly string[]).includes(pumpRaw) ? (pumpRaw as SteeringPump) : undefined;
        if (mode === 'autopilot') {
          if (this.steering.mode === 'emergency') {
            this.emit('commandRejected', 'instructor', { data: { command: 'steeringMode', reason: 'emergencySteering', args: { ...a } } });
            break;
          }
          const heading = typeof a['heading'] === 'number' ? normalizeHeading(a['heading']) : s.heading;
          const rotLimit = typeof a['rotLimit'] === 'number' ? clamp(a['rotLimit'], 1, 120) : this.autopilot.rotLimit;
          this.autopilot = { enabled: true, heading, rotLimit };
          this.setSteering('autopilot', pump, 'instructor');
        } else if (mode === 'nfu') {
          // 推桿:port/starboard 推向滿舵、stop(或不明方向)舵停在目前舵角
          const dir = a['direction'];
          const pushing = dir === 'port' || dir === 'starboard';
          this.setSteering('nfu', pump, 'instructor', pushing);
          s.rudderOrder = dir === 'port' ? -this.cfg.rudderMax_deg : dir === 'starboard' ? this.cfg.rudderMax_deg : s.rudder;
        } else {
          this.setSteering(mode as SteeringModeValue, pump, 'instructor');
        }
        break;
      }
      case 'freeze':
        if (!this.frozen) {
          this.emit('frozen', 'instructor');
          this.freezes++;
        }
        this.frozen = true;
        break;
      case 'resume':
        if (this.frozen) this.emit('resumed', 'instructor');
        this.frozen = false;
        break;
      case 'reset':
        this.autopilot = { enabled: false, heading: 0, rotLimit: 15 };
        this.steering = { mode: 'fu', pump: 'both' };
        this.rThruster = 0;
        this.frozen = false;
        this.freezes = 0;
        this.pendingEvents = [];
        this.seq = 0;
        this.firedTimeline.clear();
        this.manualFired.clear();
        this.phase = 'normal';
        this.ended = false;
        this.acks = [];
        this.faultInjections = [];
        this.state = this.initialState();
        break;
      case 'setEnvironment': {
        const a = cmd.args ?? {};
        const w = a['wind'];
        if (typeof w === 'object' && w !== null) {
          const wr = w as Record<string, unknown>;
          if (typeof wr['trueSpeed'] === 'number') s.wind.trueSpeed = wr['trueSpeed'];
          if (typeof wr['trueDir'] === 'number') s.wind.trueDir = normalizeHeading(wr['trueDir']);
        }
        const c = a['current'];
        if (typeof c === 'object' && c !== null) {
          const cr = c as Record<string, unknown>;
          if (typeof cr['set'] === 'number') s.current.set = normalizeHeading(cr['set']);
          if (typeof cr['drift'] === 'number') s.current.drift = cr['drift'];
        }
        if (typeof a['waterDepth'] === 'number') s.waterDepth = a['waterDepth'];
        if (typeof a['visibility_nm'] === 'number') s.visibility_nm = a['visibility_nm'];
        this.emit('environmentChange', 'instructor', { data: { ...a } });
        break;
      }
      case 'injectFault': {
        // 示範模式只記錄故障名稱(供教官站清單預覽),不改變運動學;名稱可為 value 字串或 args.name
        const name = typeof cmd.value === 'string' ? cmd.value : typeof cmd.args?.['name'] === 'string' ? (cmd.args['name'] as string) : '';
        if (name.trim()) {
          const faults = s.faults ?? [];
          const already = faults.includes(name);
          if (!already) s.faults = [...faults, name];
          this.emit('faultInjected', 'instructor', { data: { fault: name, alreadyActive: already } });
          if (!already) {
            this.faultInjections.push({ fault: name, t: s.t });
            if (this.phase === 'normal') {
              this.emit('phaseChanged', 'instructor', { data: { from: this.phase, to: 'degraded' } });
              this.phase = 'degraded';
            }
          }
        }
        break;
      }
      case 'clearFault': {
        const before = s.faults ?? [];
        if (typeof cmd.value === 'string') s.faults = before.filter((f) => f !== cmd.value);
        else s.faults = [];
        for (const f of before) if (!(s.faults ?? []).includes(f)) this.emit('faultCleared', 'instructor', { data: { fault: f } });
        break;
      }
      case 'acknowledge': {
        const a = cmd.args ?? {};
        const kind = typeof a['kind'] === 'string' ? a['kind'] : 'other';
        const ack: { kind: string; t: number; role?: string } = { kind, t: s.t };
        if (typeof a['role'] === 'string') ack.role = a['role'];
        this.acks.push(ack);
        this.emit('acknowledge', 'instructor', { data: { ...a } });
        break;
      }
      case 'triggerTimeline': {
        const id = typeof cmd.args?.['id'] === 'string' ? (cmd.args['id'] as string) : '';
        const force = cmd.args?.['force'] === true;
        const known = DEMO_TIMELINE.find((d) => d.id === id);
        const ok = id !== '' && (force || !this.firedTimeline.has(id) || !known);
        this.emit('timelineManual', 'instructor', { itemId: id, data: { ok, force } });
        if (ok) {
          this.manualFired.add(id);
          this.emit('timelineTriggered', 'timeline', { itemId: id, data: { fireCount: 1, manual: true, forced: force } });
          if (known) {
            this.firedTimeline.add(id);
            this.emit(known.make(s.t).kind, 'timeline', { ...known.make(s.t), itemId: id });
          } else this.emit('message', 'timeline', { itemId: id, data: { to: 'instructor', text: `示範模式:手動觸發 ${id}(情境時間表由核心執行,示範只記事件)`, severity: 'info' } });
        }
        break;
      }
      case 'endExercise': {
        if (!this.ended) {
          this.ended = true;
          this.frozen = true;
          this.emit('exerciseEnded', 'instructor', { data: { reason: typeof cmd.args?.['reason'] === 'string' ? cmd.args['reason'] : '' } });
        }
        break;
      }
      case 'getAssessment':
        // 查詢由 DemoSource 直接回覆 assessment 框
        break;
      default:
        // 其他指令(情境載入、時間倍率、快照等)示範模式不支援,忽略;情境載入由教官站重建示範來源
        break;
    }
    this.updateDerived(this.state);
  }

  /** 前進一步(50 Hz)。凍結時只更新旗標不積分。 */
  step(dt = DEMO_DT): OwnShipState {
    const s = this.state;
    if (this.frozen) {
      s.flags = { ...(s.flags ?? {}), frozen: true };
      return s;
    }
    this.stepTimeline();
    const cfg = this.cfg;

    // 自動舵:依航向誤差求目標 ROT(受限制),再以 ROT 誤差決定舵令
    if (this.autopilot.enabled) {
      const err = headingDifference(s.heading, this.autopilot.heading); // 右轉正
      const desiredRot = clamp(err * 1.0, -this.autopilot.rotLimit, this.autopilot.rotLimit);
      const rudder = 0.6 * (desiredRot - s.rot) + 0.15 * err;
      s.rudderOrder = clamp(rudder, -cfg.rudderNormalMax_deg, cfg.rudderNormalMax_deg);
    }

    // 舵機:速率限制(應急操舵時速率減半)
    const rudderRate = cfg.rudderRate_degPerS * (this.steering.mode === 'emergency' ? EMERGENCY_RUDDER_RATE_FACTOR : 1);
    const rudderDelta = clamp(s.rudderOrder - s.rudder, -rudderRate * dt, rudderRate * dt);
    s.rudder += rudderDelta;

    // 主機轉速:一階滯後(τ 12 s),通過零時有換向延遲的粗略效果
    const tauRpm = 12;
    s.rpm += ((s.rpmOrder - s.rpm) * dt) / tauRpm;
    if (Math.abs(s.rpm) < 0.05 && s.rpmOrder === 0) s.rpm = 0;

    // 側推:達滿推力需 fullDelay 秒
    const th = s.thruster ?? { order: 0, actual: 0 };
    const thRate = dt / cfg.thrusterFullDelay_s;
    th.actual += clamp(th.order - th.actual, -thRate, thRate);
    s.thruster = th;

    // 參考速度:由目前轉速在車鐘表內插,倒車為負
    const targetSpeedKn = this.speedForRpm(s.rpm) * (1 - TURN_SPEED_LOSS * Math.min(1, Math.abs(s.rudder) / cfg.rudderNormalMax_deg));
    const targetU = knotsToMps(targetSpeedKn);
    const tauU = targetU * s.u < 0 ? 70 : Math.abs(targetU) > Math.abs(s.u) ? 90 : s.rpm === 0 ? 250 : 150;
    s.u += ((targetU - s.u) * dt) / tauU;

    // 航向:Nomoto 一階,K、T 依瞬時速度換算
    const U = Math.max(Math.abs(s.u), U_MIN_FOR_NOMOTO);
    const K = (K_PRIME * U) / cfg.lpp_m;
    const T = (T_PRIME * cfg.lpp_m) / U;
    const rudderSign = s.u >= 0 ? 1 : -0.5; // 倒退時舵效相反且較弱
    const rHydroTarget = K * degToRad(s.rudder) * rudderSign * Math.min(1, Math.abs(s.u) / U_MIN_FOR_NOMOTO);
    const rHydro = s.r - this.rThruster;
    const rHydroNew = rHydro + ((rHydroTarget - rHydro) * dt) / T;

    // 側推:零速 thrusterTurnRate 度/分,隨速度衰減到 notEffectiveAbove 為零
    const eff = clamp(1 - mpsToKnots(Math.abs(s.u)) / cfg.thrusterIneffectiveAbove_kn, 0, 1);
    const rThrTarget = degToRad(cfg.thrusterTurnRate_degPerMin / 60) * th.actual * eff;
    this.rThruster += ((rThrTarget - this.rThruster) * dt) / 20;

    s.r = rHydroNew + this.rThruster;
    s.v = -SWAY_COEFF * s.r * cfg.lpp_m;
    s.heading = normalizeHeading(s.heading + radToDeg(s.r) * dt);

    // 位置:船體速度轉 ENU 加均勻流
    const psi = degToRad(s.heading);
    const curSet = degToRad(s.current.set);
    const curMps = knotsToMps(s.current.drift);
    const ve = s.u * Math.sin(psi) + s.v * Math.cos(psi) + curMps * Math.sin(curSet);
    const vn = s.u * Math.cos(psi) - s.v * Math.sin(psi) + curMps * Math.cos(curSet);
    s.pos.x += ve * dt;
    s.pos.y += vn * dt;

    s.t += dt;
    s.tick += 1;
    this.updateDerived(s, ve, vn);
    this.stepTargets(s, dt);
    return s;
  }

  /** 由轉速查參考速度(kn):以車鐘表內插,倒車用倒車段。 */
  private speedForRpm(rpm: number): number {
    const steps = this.cfg.telegraph.filter((t) => (rpm >= 0 ? t.rpm >= 0 : t.rpm <= 0)).sort((a, b) => a.rpm - b.rpm);
    if (steps.length < 2) return 0;
    const first = steps[0]!;
    const last = steps[steps.length - 1]!;
    if (rpm <= first.rpm) return first.rpm === 0 ? 0 : (first.speedLoaded_kn * rpm) / first.rpm;
    if (rpm >= last.rpm) return last.rpm === 0 ? 0 : (last.speedLoaded_kn * rpm) / last.rpm;
    for (let i = 1; i < steps.length; i++) {
      const a = steps[i - 1]!;
      const b = steps[i]!;
      if (rpm <= b.rpm) {
        const f = b.rpm === a.rpm ? 0 : (rpm - a.rpm) / (b.rpm - a.rpm);
        return a.speedLoaded_kn + f * (b.speedLoaded_kn - a.speedLoaded_kn);
      }
    }
    return 0;
  }

  /** 更新導出量:STW/SOG/COG/ROT、相對風、緯經度、UKC、擱淺旗標。 */
  private updateDerived(s: OwnShipState, ve?: number, vn?: number): void {
    const psi = degToRad(s.heading);
    if (ve === undefined || vn === undefined) {
      const curSet = degToRad(s.current.set);
      const curMps = knotsToMps(s.current.drift);
      ve = s.u * Math.sin(psi) + s.v * Math.cos(psi) + curMps * Math.sin(curSet);
      vn = s.u * Math.cos(psi) - s.v * Math.sin(psi) + curMps * Math.cos(curSet);
    }
    this.ownVe = ve;
    this.ownVn = vn;
    s.stw = mpsToKnots(s.u);
    s.sog = mpsToKnots(Math.hypot(ve, vn));
    s.cog = s.sog > 0.05 ? normalizeHeading(radToDeg(Math.atan2(ve, vn))) : s.heading;
    s.rot = radToDeg(s.r) * 60;
    s.drift = s.u !== 0 || s.v !== 0 ? radToDeg(Math.atan2(-s.v, Math.abs(s.u) < 1e-6 ? 1e-6 : s.u)) : 0;

    // 相對風(相對艏向,來向)
    const wdir = degToRad(s.wind.trueDir);
    const wmps = knotsToMps(s.wind.trueSpeed);
    const windE = -wmps * Math.sin(wdir);
    const windN = -wmps * Math.cos(wdir);
    const relE = windE - ve;
    const relN = windN - vn;
    const relSpeed = Math.hypot(relE, relN);
    s.wind.relSpeed = Math.round(mpsToKnots(relSpeed) * 10) / 10;
    const relFrom = relSpeed > 1e-6 ? radToDeg(Math.atan2(-relE, -relN)) : s.wind.trueDir;
    s.wind.relDir = normalizeHeading(relFrom - s.heading);

    // 緯經度(小範圍平面近似)
    const latRad = degToRad(this.opts.originLat);
    s.pos.lat = this.opts.originLat + s.pos.y / 111_320;
    s.pos.lon = this.opts.originLon + s.pos.x / (111_320 * Math.cos(latRad));

    // Squat 與 UKC(squat 粗略 ∝ 速度平方,FSB1 表:14 kn 約 0.42 m)
    const draft = Math.max(s.draft?.fore ?? this.cfg.draftLoaded_m, s.draft?.aft ?? this.cfg.draftLoaded_m);
    const kn = Math.abs(s.stw);
    s.squat = Math.round(0.0021 * kn * kn * 100) / 100;
    const depth = s.waterDepth ?? this.opts.waterDepth_m;
    s.depthBelowKeel = Math.round((depth - draft - s.squat) * 100) / 100;
    s.engine = { state: 'running', startsRemaining: s.engine?.startsRemaining ?? 12, load_pct: Math.round(Math.abs(s.rpm / this.cfg.rpmMax) * 100) };
    const wasAground = s.flags?.aground ?? false;
    s.flags = { frozen: this.frozen, aground: s.depthBelowKeel <= 0, collision: false };
    if (s.flags.aground && !wasAground) this.emit('grounding', 'engine', {}, s);
    this.updateScenarioStatus(s);
    this.updateTargets(s, s.tick === 0 && s.targets.length === 0);
  }

  /** 供測試:目前操舵模式。 */
  get steeringState(): Readonly<SteeringStatus> {
    return this.steering;
  }

  /** 供測試:直接讀自動舵狀態。 */
  get autopilotState(): Readonly<AutopilotState> {
    return this.autopilot;
  }
}
