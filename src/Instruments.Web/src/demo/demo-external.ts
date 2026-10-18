// 示範模式的拖船、錨與繫纜(離線測試教官站/操船台面板用;**不是**核心的物理模型,docs/design/physics-tugs-anchors-mooring.md):
//  - 拖船:預設兩艘(TG1 艏 fwd、TG2 艉 aft,繫柱拖力 40 t);tugControl 的狀態機簡化自核心(帶纜 20 s、推↔拉換位 15 s,示範縮短)、
//    出力一階上升(τ 30 s)、方向以 3 °/s 走最短弧、速度效能(推 3/5 kn、拉 4/7 kn);拒絕原因與核心同名;tugLineBreak 故障斷纜。
//    拖船力**不**影響示範運動學(只供面板與航跡圖顯示)。
//  - 錨:依 particulars 的節數/鏈重/錨鏈孔;letGo 以 2 m/s 送出到目標(預設鏈端)、veer 0.5 m/s、heave 依起錨速率、brake/release;
//    鏈長 > 水深 + 錨鏈孔高度時著底(錨位 = 當時錨鏈孔投影);張力以「水平距離超過鬆弛長度」的線性彈簧近似,超過抓地力即走錨(錨被拖向錨鏈孔)。
//  - 泊位:預設在自船初始位置右舷 150 m、平行初始航向的示意碼頭(碰墊每 25 m、繫船柱每 50 m);三條纜 HEAD/BREAST/STERN;
//    情境纜繩 initial: fast 在初始狀態即帶纜(initialise;與核心相同,不發事件);
//    makeFast 以預張力定未受力纜長、張力 T = MBL·(ε/0.18)^1.5、heave/slack 以 15 m/min 改纜長、T > MBL 斷纜。示範不模擬碰墊接觸。
// 事件 kind/data 與核心相同(tugCommand、tugLineParted、anchorLetGo、anchorCommand、anchorBroughtUp、anchorDragging、anchorHolding、
// anchorAweigh、anchorHoused、anchorChainFullyOut、mooringCommand、mooringLineParted、commandRejected)。

import { clamp, degToRad, normalizeHeading, normalizeRelative } from '../lib/angles.js';
import type { ShipDisplayConfig } from '../lib/ship-config.js';
import { bodyToEnu } from '../lib/ship-geometry.js';
import { berthGeometry, quayDistance, resolveBodyPoint, type BerthGeometry, type BodyPointSpec, type ScenarioMooringLineDef } from '../lib/external-forces.js';
import type { SimCommand } from '../types/command.js';
import type { SimEvent, SimEventSource } from '../types/event.js';
import type { OwnShipState } from '../types/state.js';
import type { AnchorSide, AnchorState, AnchorStatus, BerthState, MooringLineState, MooringStatus, TugMode, TugState, TugStatus } from '../types/external.js';

export type DemoExternalEmit = (kind: string, source: SimEventSource, extra?: Partial<SimEvent>) => void;

export interface DemoTugDef {
  id: string;
  name?: string;
  bollardPull_t: number;
  attach: BodyPointSpec;
  lineLength_m: number;
  modes?: Array<'push' | 'pull'>;
}

export interface DemoBerthOptions {
  definition: Record<string, unknown>;
  lines: ScenarioMooringLineDef[];
}

export const DEMO_TUGS: readonly DemoTugDef[] = [
  { id: 'TG1', name: '示範拖船一', bollardPull_t: 40, attach: 'fwd', lineLength_m: 40 },
  { id: 'TG2', name: '示範拖船二', bollardPull_t: 40, attach: 'aft', lineLength_m: 40 },
];

/** 示範縮短的帶纜/換位時間(核心預設 90 s / 60 s) */
export const DEMO_TUG_CONNECT_S = 20;
export const DEMO_TUG_MODE_CHANGE_S = 15;
const TUG_RISE_S = 30;
const TUG_TURN_DEG_PER_S = 3;
const LET_GO_MPS = 2;
const VEER_MPS = 0.5;
const DRAG_MPS = 0.5;
const SEA_WATER = 1025;
const STEEL = 7850;

/** 示範泊位(相對自船初始位置 (x0, y0) 與航向的本地 {x, y};碼頭線平行初始航向、在右舷 150 m) */
export function demoBerth(initialHeading: number, x0 = 0, y0 = 0): DemoBerthOptions {
  const psi = degToRad(initialHeading);
  const along = (s: number, off: number) => ({ x: x0 + Math.round(s * Math.sin(psi) + off * Math.cos(psi)), y: y0 + Math.round(s * Math.cos(psi) - off * Math.sin(psi)) });
  const fenders = Array.from({ length: 13 }, (_, i) => ({ id: `F${String(i + 1).padStart(2, '0')}`, type: 'cone-1000', chainage_m: 100 + i * 25 }));
  const bollards = Array.from({ length: 8 }, (_, i) => ({ id: `B${i + 1}`, chainage_m: 75 + i * 50, setback_m: 3, swl_t: 50 }));
  return {
    definition: {
      id: 'DEMO-BERTH',
      name: '示範泊位(非實際碼頭)',
      quay: [along(-100, 150), along(600, 150)],
      // 沿碼頭線(= 初始航向)看,水域(自船)在左側
      waterSide: 'left',
      fenderTypes: { 'cone-1000': { height_m: 1.0, width_m: 2.5, ratedReaction_kN: 1000, ratedDeflection_pct: 72, ratedEnergy_kNm: 551.4 } },
      fenders,
      bollards,
    },
    lines: [
      { id: 'HEAD', fairlead: 'fwdStbd', bollard: 'B7', mbl_t: 50, length_m: 150, winchPull_t: 10, pretension_t: 2 },
      { id: 'BREAST', fairlead: 'midStbd', bollard: 'B5', mbl_t: 50, length_m: 150, winchPull_t: 10, pretension_t: 2 },
      { id: 'STERN', fairlead: 'aftStbd', bollard: 'B3', mbl_t: 50, length_m: 150, winchPull_t: 10, pretension_t: 2 },
    ],
  };
}

interface TugUnit {
  def: DemoTugDef;
  mode: TugMode;
  status: TugStatus;
  /** 就位工作側(push/pull;未就位為 null) */
  side: 'push' | 'pull' | null;
  force: number;
  dir: number;
  dirOrder: number;
  power: number;
  timer: number;
  lineParted: boolean;
  efficiency: number;
}

interface AnchorUnit {
  side: AnchorSide;
  mode: 'stowed' | 'running' | 'veering' | 'heaving' | 'braked';
  length: number;
  target: number | null;
  onBottom: boolean;
  ax: number;
  ay: number;
  dragging: boolean;
  fullyOut: boolean;
  broughtUpPending: boolean;
  h: number;
  tension: number;
  /** 水平張力 (t) */
  hTension: number;
  lying: number;
  holding: number;
}

interface LineUnit {
  def: ScenarioMooringLineDef;
  status: MooringStatus;
  bollardId: string | null;
  rest: number;
  target: number;
  slackRemaining: number;
  tension: number;
}

function n(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

export class DemoExternal {
  private tugs: TugUnit[];
  private anchors: AnchorUnit[];
  private lines: LineUnit[];
  readonly berth: DemoBerthOptions | null;
  readonly geometry: BerthGeometry | null;
  private readonly shackle: number;
  /** 錨鏈水中單位重 (t/m) */
  private readonly wChain: number;
  private readonly anchorHolding_t: number;

  constructor(
    private readonly cfg: ShipDisplayConfig,
    private readonly emit: DemoExternalEmit,
    tugs: readonly DemoTugDef[],
    berth: DemoBerthOptions | null,
    origin: { lat: number; lon: number } | null = null,
  ) {
    this.tugs = tugs.map((def) => ({ def, mode: 'standby', status: 'standby', side: null, force: 0, dir: 90, dirOrder: 90, power: 0, timer: 0, lineParted: false, efficiency: 1 }));
    const a = cfg.anchors;
    this.shackle = a?.shackleLength_m ?? 27.5;
    this.wChain = ((a?.chainMass_kgPerM ?? 38.6) * (1 - SEA_WATER / STEEL)) / 1000;
    // 抓地力:錨重 × 抓著係數 7(砂,hhp)— 示範固定值
    this.anchorHolding_t = ((a?.anchorMass_kg ?? 2140) * 7) / 1000;
    this.anchors = (['port', 'starboard'] as const).map((side) => ({ side, mode: 'stowed', length: 0, target: null, onBottom: false, ax: 0, ay: 0, dragging: false, fullyOut: false, broughtUpPending: false, h: 0, tension: 0, hTension: 0, lying: 0, holding: 0 }));
    this.berth = berth;
    this.geometry = berth ? berthGeometry(berth.definition, origin) : null;
    this.lines = (berth?.lines ?? []).map((def) => ({ def, status: 'stowed', bollardId: def.bollard ?? null, rest: 0, target: 0, slackRemaining: 0, tension: 0 }));
  }

  private maxLength(side: AnchorSide): number {
    const a = this.cfg.anchors;
    return (side === 'port' ? (a?.shacklesPort ?? 9) : (a?.shacklesStbd ?? 9)) * this.shackle;
  }

  private hawse(side: AnchorSide, s: OwnShipState): { x: number; y: number } {
    const a = this.cfg.anchors;
    const p = { x: a?.hawseX_m ?? 0.47 * this.cfg.lpp_m, y: (side === 'port' ? -1 : 1) * (a?.hawseY_m ?? 0.15 * this.cfg.breadth_m) };
    return bodyToEnu(p, s.heading, s.pos);
  }

  private reject(command: string, reason: string, args: Record<string, unknown>): true {
    this.emit('commandRejected', 'instructor', { data: { command, reason, args: { ...args } } });
    return true;
  }

  /** 處理 tugControl / anchor / mooring 指令;其他指令回傳 false。 */
  apply(cmd: SimCommand, s: OwnShipState): boolean {
    const args = cmd.args ?? {};
    if (cmd.type === 'tugControl') return this.tugControl(args);
    if (cmd.type === 'anchor') return this.anchorCommand(args);
    if (cmd.type === 'mooring') return this.mooringCommand(args, s);
    return false;
  }

  private tugControl(args: Record<string, unknown>): true {
    const tug = this.tugs.find((t) => t.def.id === args['id']);
    if (!tug) return this.reject('tugControl', 'unknownTug', args);
    // 已離開的拖船不可再下令(核心 tugUnavailable);mode unavailable 讓拖船離開(不可恢復)
    if (tug.mode === 'unavailable') return this.reject('tugControl', 'tugUnavailable', args);
    const modeRaw = args['mode'];
    if (modeRaw === 'unavailable') {
      const was = tug.mode;
      tug.mode = 'unavailable';
      tug.status = 'unavailable';
      tug.side = null;
      tug.force = 0;
      tug.timer = 0;
      this.emit('tugCommand', 'instructor', { itemId: tug.def.id, data: { id: tug.def.id, mode: 'unavailable', from: was, status: 'unavailable', direction_deg: Math.round(tug.dirOrder * 10) / 10, power_pct: 0 } });
      return true;
    }
    const mode: Exclude<TugMode, 'unavailable'> | null = modeRaw === undefined ? (tug.mode as Exclude<TugMode, 'unavailable'>) : modeRaw === 'push' || modeRaw === 'pull' || modeRaw === 'standby' || modeRaw === 'free' ? modeRaw : null;
    if (!mode) return this.reject('tugControl', 'unknownTugMode', args);
    const power = n(args['power_pct']);
    if (power !== undefined && (power < 0 || power > 100)) return this.reject('tugControl', 'invalidPower', args);
    if ((mode === 'push' || mode === 'pull') && tug.def.modes && tug.def.modes.length > 0 && !tug.def.modes.includes(mode)) return this.reject('tugControl', 'tugModeUnavailable', args);
    if (mode === 'pull' && tug.lineParted) return this.reject('tugControl', 'tugLineParted', args);
    const from = tug.mode;
    const dir = n(args['direction_deg']);
    if (dir !== undefined) tug.dirOrder = normalizeHeading(dir);
    if (power !== undefined) tug.power = power / 100;
    if (modeRaw !== undefined && !(tug.status === 'parted' && mode === 'free')) {
      if (mode === 'free') {
        tug.status = 'free';
        tug.side = null;
        tug.force = 0;
        tug.timer = 0;
      } else if (mode === 'standby') {
        if (tug.status === 'free' || tug.status === 'parted') {
          tug.status = 'connecting';
          tug.timer = DEMO_TUG_CONNECT_S;
        } else if (tug.status !== 'connecting' && tug.status !== 'repositioning') tug.status = 'standby';
      } else if (tug.status === 'free' || tug.status === 'parted') {
        tug.status = 'connecting';
        tug.timer = DEMO_TUG_CONNECT_S;
        tug.side = mode;
      } else if (tug.side !== null && tug.side !== mode) {
        tug.status = 'repositioning';
        tug.timer = DEMO_TUG_MODE_CHANGE_S;
        tug.side = mode;
      } else if (tug.status !== 'connecting' && tug.status !== 'repositioning') {
        tug.status = 'working';
        tug.side = mode;
      } else tug.side = mode;
      tug.mode = mode;
    }
    this.emit('tugCommand', 'instructor', { itemId: tug.def.id, data: { id: tug.def.id, mode: tug.mode, from, status: tug.status, direction_deg: Math.round(tug.dirOrder * 10) / 10, power_pct: Math.round(tug.power * 1000) / 10 } });
    return true;
  }

  /** injectFault tugLineBreak(args.id 省略 = 第一艘拉中者,否則第一艘已連接者);回傳是否處理。 */
  partTugLine(id: string | undefined): boolean {
    const tug = id
      ? this.tugs.find((t) => t.def.id === id)
      : (this.tugs.find((t) => !t.lineParted && t.mode === 'pull' && t.status !== 'free') ?? this.tugs.find((t) => !t.lineParted && t.status !== 'free' && t.status !== 'parted'));
    if (!tug || tug.lineParted) return false;
    const before = tug.force;
    const mode = tug.mode;
    tug.lineParted = true;
    if (tug.mode !== 'push') {
      tug.force = 0;
      tug.status = 'parted';
      tug.mode = 'free';
      tug.side = null;
    }
    this.emit('tugLineParted', 'engine', { itemId: tug.def.id, visible: true, data: { id: tug.def.id, force_t: Math.round(before * 10) / 10, mode, reason: 'fault', stillPushing: tug.mode === 'push' } });
    return true;
  }

  restoreTugLines(): void {
    for (const t of this.tugs) {
      t.lineParted = false;
      if (t.status === 'parted') t.status = 'free';
    }
  }

  get hasTugs(): boolean {
    return this.tugs.length > 0;
  }

  private anchorCommand(args: Record<string, unknown>): true {
    const a = this.anchors.find((x) => x.side === args['side']);
    if (!a) return this.reject('anchor', 'unknownAnchor', args);
    const action = args['action'];
    const m = n(args['length_m']);
    const sh = n(args['targetLength_shackles']);
    const target = m ?? (sh !== undefined ? sh * this.shackle : undefined);
    if (target !== undefined && target < 0) return this.reject('anchor', 'invalidTarget', args);
    const max = this.maxLength(a.side);
    switch (action) {
      case 'letGo':
        if (a.mode !== 'stowed' && (a.onBottom || a.mode === 'running' || a.mode === 'veering')) return this.reject('anchor', 'anchorAlreadyOut', args);
        if (a.fullyOut) return this.reject('anchor', 'chainFullyOut', args);
        if (target !== undefined && target < a.length - 1e-9) return this.reject('anchor', 'invalidTarget', args);
        a.mode = 'running';
        a.target = target !== undefined ? Math.min(Math.max(target, a.length), max) : null;
        a.broughtUpPending = true;
        break;
      case 'veer':
        if (a.mode === 'stowed') return this.reject('anchor', 'anchorStowed', args);
        if (a.fullyOut) return this.reject('anchor', 'chainFullyOut', args);
        a.mode = 'veering';
        a.target = target !== undefined ? Math.min(Math.max(target, a.length), max) : null;
        break;
      case 'heave':
        if (a.mode === 'stowed') return this.reject('anchor', 'anchorStowed', args);
        if (target !== undefined && target >= a.length) return this.reject('anchor', 'invalidTarget', args);
        a.mode = 'heaving';
        a.target = target !== undefined ? Math.max(0, target) : null;
        a.fullyOut = false;
        break;
      case 'brake':
        if (a.mode === 'stowed') return this.reject('anchor', 'anchorStowed', args);
        a.mode = 'braked';
        a.target = null;
        break;
      case 'release':
        if (a.fullyOut) return this.reject('anchor', 'chainFullyOut', args);
        if (a.mode === 'stowed') a.broughtUpPending = true;
        a.mode = 'running';
        a.target = null;
        break;
      default:
        return this.reject('anchor', 'unknownAction', args);
    }
    const data = { side: a.side, action, targetLength_m: a.target, targetLength_shackles: a.target !== null ? a.target / this.shackle : null, length_m: Math.round(a.length * 10) / 10, status: this.anchorStatus(a) };
    this.emit(action === 'letGo' ? 'anchorLetGo' : 'anchorCommand', 'instructor', { itemId: a.side, data });
    return true;
  }

  private mooringCommand(args: Record<string, unknown>, s: OwnShipState): true {
    if (!this.berth) return this.reject('mooring', 'noBerth', args);
    const l = this.lines.find((x) => x.def.id === args['lineId']);
    if (!l) return this.reject('mooring', 'unknownMooringLine', args);
    const action = args['action'];
    const tension = n(args['tension_t']);
    const length = n(args['length_m']);
    const loaded = l.status === 'fast' || l.status === 'heaving' || l.status === 'slacking';
    switch (action) {
      case 'makeFast': {
        const reason = this.makeFast(l, typeof args['bollard'] === 'string' ? args['bollard'] : undefined, tension, s);
        if (reason) return this.reject('mooring', reason, args);
        break;
      }
      case 'heave':
        if (!loaded) return this.reject('mooring', 'mooringLineNotFast', args);
        l.target = Math.min(l.def.winchPull_t ?? 10, tension ?? l.def.winchPull_t ?? 10);
        l.status = 'heaving';
        break;
      case 'slack':
        if (!loaded) return this.reject('mooring', 'mooringLineNotFast', args);
        l.slackRemaining = Math.max(0.1, length ?? 2);
        l.status = 'slacking';
        break;
      case 'letGo':
        if (l.status === 'parted') return this.reject('mooring', 'mooringLineParted', args);
        l.status = 'letGo';
        l.tension = 0;
        break;
      default:
        return this.reject('mooring', 'unknownAction', args);
    }
    this.emit('mooringCommand', 'instructor', { itemId: l.def.id, data: { lineId: l.def.id, action, status: l.status, bollardId: l.bollardId, tension_t: tension ?? null, length_m: length ?? null, restLength_m: Math.round(l.rest * 10) / 10 } });
    return true;
  }

  /** 帶纜(預張力定未受力纜長);拒絕時回傳原因(與核心同名),成功 null。 */
  private makeFast(l: LineUnit, bollard: string | undefined, tension: number | undefined, s: OwnShipState): string | null {
    if (l.status === 'parted') return 'mooringLineParted';
    const id = bollard ?? l.def.bollard ?? null;
    if (!id || !this.geometry?.bollards.some((b) => b.id === id)) return 'unknownBollard';
    const prevBollard = l.bollardId;
    l.bollardId = id;
    const dist = this.lineDistance(l, s);
    if (dist === null || dist > (l.def.length_m ?? 150)) {
      l.bollardId = prevBollard;
      return 'bollardOutOfReach';
    }
    const mbl = l.def.mbl_t ?? 50;
    const pre = tension ?? l.def.pretension_t ?? 2;
    l.rest = dist / (1 + 0.18 * Math.pow(pre / mbl, 1 / 1.5));
    l.status = 'fast';
    l.slackRemaining = 0;
    return null;
  }

  /**
   * 情境初始狀態(自船初始狀態建立後呼叫一次;重設後亦同):纜繩 initial: fast 以預張力帶纜。
   * 與核心 ExternalForceModel 建構時對 initial = fast 的纜執行 makeFast 相同:不發事件,被拒(例如超出纜長)時維持 stowed。
   */
  initialise(s: OwnShipState): void {
    for (const l of this.lines) if (l.def.initial === 'fast') this.makeFast(l, undefined, undefined, s);
  }

  private lineDistance(l: LineUnit, s: OwnShipState): number | null {
    const b = this.geometry?.bollards.find((x) => x.id === l.bollardId);
    const fl = resolveBodyPoint(l.def.fairlead ?? 'midStbd', this.cfg.lpp_m, this.cfg.breadth_m);
    if (!b || !fl) return null;
    const p = bodyToEnu(fl, s.heading, s.pos);
    return Math.hypot(b.x - p.x, b.y - p.y);
  }

  private anchorStatus(a: AnchorUnit): AnchorStatus {
    if (a.mode === 'stowed') return 'stowed';
    if (!a.onBottom) return a.mode === 'running' || a.mode === 'veering' ? 'running' : 'aweigh';
    if (a.dragging) return 'dragging';
    if (a.mode === 'heaving') return 'heaving';
    if (a.mode === 'running' || a.mode === 'veering') return 'running';
    return 'holding';
  }

  /** 前進一步(在自船位置積分之後呼叫)。 */
  step(s: OwnShipState, dt: number): void {
    const stwKn = Math.abs(s.stw);
    for (const t of this.tugs) {
      if (t.timer > 0) {
        t.timer = Math.max(0, t.timer - dt);
        if (t.timer === 0) t.status = t.mode === 'push' || t.mode === 'pull' ? 'working' : 'standby';
      }
      const [full, half] = t.mode === 'push' ? [3, 5] : [4, 7];
      t.efficiency = stwKn <= full ? 1 : 1 / (1 + ((stwKn - full) / (half - full)) ** 2);
      const target = t.status === 'working' ? t.def.bollardPull_t * t.power * t.efficiency : 0;
      if (t.status === 'free' || (t.status === 'parted' && t.mode !== 'push')) t.force = 0;
      else t.force += (target - t.force) * (1 - Math.exp(-dt / TUG_RISE_S));
      const diff = normalizeRelative(t.dirOrder - t.dir);
      t.dir = normalizeHeading(t.dir + clamp(diff, -TUG_TURN_DEG_PER_S * dt, TUG_TURN_DEG_PER_S * dt));
    }
    for (const a of this.anchors) this.stepAnchor(a, s, dt);
    for (const l of this.lines) this.stepLine(l, s, dt);
  }

  private stepAnchor(a: AnchorUnit, s: OwnShipState, dt: number): void {
    if (a.mode === 'stowed') return;
    const max = this.maxLength(a.side);
    const hp = this.hawse(a.side, s);
    const hawseHeight = 7.5;
    const draft = s.draft?.fore ?? this.cfg.draftLoaded_m;
    a.h = Math.max(1, (s.waterDepth ?? 20) + hawseHeight - draft);
    const before = a.length;
    if (a.mode === 'running' || a.mode === 'veering') {
      const rate = a.mode === 'running' ? LET_GO_MPS : VEER_MPS;
      const stop = a.target ?? max;
      a.length = Math.min(stop, a.length + rate * dt);
      if (a.length >= max - 1e-9 && !a.fullyOut) {
        a.fullyOut = true;
        this.emit('anchorChainFullyOut', 'engine', { itemId: a.side, data: { side: a.side, length_m: max, shackles: max / this.shackle } });
      }
      if (a.length >= stop - 1e-9) a.mode = 'braked';
    } else if (a.mode === 'heaving') {
      const rate = (this.cfg.anchors?.heavingRate_mPerMin ?? 21.7) / 60;
      const stop = a.target ?? 0;
      a.length = Math.max(stop, a.length - rate * dt);
      if (a.length <= stop + 1e-9) {
        if (stop <= 0) {
          a.mode = 'stowed';
          a.length = 0;
          a.onBottom = false;
          a.tension = 0;
          a.hTension = 0;
          a.lying = 0;
          a.holding = 0;
          a.dragging = false;
          this.emit('anchorHoused', 'engine', { itemId: a.side, data: { side: a.side } });
          return;
        }
        a.mode = 'braked';
      }
    }
    // 著底/離底
    if (!a.onBottom && a.length > a.h && before <= a.h + 1e-9) {
      a.onBottom = true;
      a.ax = hp.x;
      a.ay = hp.y;
    } else if (a.onBottom && a.length <= a.h) {
      a.onBottom = false;
      a.dragging = false;
      this.emit('anchorAweigh', 'engine', { itemId: a.side, data: { side: a.side, length_m: Math.round(a.length * 10) / 10 } });
    }
    if (!a.onBottom) {
      a.tension = this.wChain * a.length;
      a.hTension = 0;
      a.lying = 0;
      a.holding = 0;
      return;
    }
    // 簡化張力:水平距離超過鬆弛長度 (L − h) 的部分以 2 t/m 線性彈簧
    const dx = a.ax - hp.x;
    const dy = a.ay - hp.y;
    const X = Math.hypot(dx, dy);
    const excess = X - (a.length - a.h);
    const H = Math.max(0, Math.min(150, excess * 2));
    a.tension = H + this.wChain * a.h;
    a.hTension = H;
    a.lying = Math.max(0, a.length - a.h - Math.max(0, excess));
    a.holding = this.anchorHolding_t + 0.75 * this.wChain * a.lying;
    if (!a.dragging && H > a.holding) {
      a.dragging = true;
      this.emit('anchorDragging', 'engine', { itemId: a.side, visible: true, data: { side: a.side, tension_t: round1(H), holding_t: round1(a.holding), length_m: round1(a.length) } });
    } else if (a.dragging && H <= 0.95 * a.holding) {
      a.dragging = false;
      this.emit('anchorHolding', 'engine', { itemId: a.side, data: { side: a.side, tension_t: round1(H), holding_t: round1(a.holding), length_m: round1(a.length) } });
    }
    if (a.dragging && X > 1e-6) {
      const v = DRAG_MPS * Math.min(1, (H - a.holding) / (0.25 * a.holding) + 0.2);
      a.ax -= (dx / X) * v * dt;
      a.ay -= (dy / X) * v * dt;
    }
    if (a.broughtUpPending && a.mode === 'braked' && !a.dragging && H > 0 && s.sog < 0.3) {
      a.broughtUpPending = false;
      this.emit('anchorBroughtUp', 'engine', { itemId: a.side, data: { side: a.side, length_m: round1(a.length), tension_t: round1(a.tension) } });
    }
  }

  private stepLine(l: LineUnit, s: OwnShipState, dt: number): void {
    if (!(l.status === 'fast' || l.status === 'heaving' || l.status === 'slacking')) {
      l.tension = 0;
      return;
    }
    const mbl = l.def.mbl_t ?? 50;
    const rate = 15 / 60;
    const D = this.lineDistance(l, s) ?? l.rest;
    if (l.status === 'heaving') l.rest = Math.max(1, l.rest - rate * dt);
    else if (l.status === 'slacking') {
      const d = Math.min(l.slackRemaining, rate * dt);
      l.rest += d;
      l.slackRemaining -= d;
      if (l.slackRemaining <= 1e-6) l.status = 'fast';
    }
    const eps = l.rest > 0 ? D / l.rest - 1 : 0;
    l.tension = eps > 0 ? Math.min(1.5 * mbl, mbl * Math.pow(eps / 0.18, 1.5)) : 0;
    if (l.status === 'heaving' && l.tension >= l.target) l.status = 'fast';
    if (l.tension > mbl) {
      const t = l.tension;
      l.status = 'parted';
      l.tension = 0;
      this.emit('mooringLineParted', 'engine', { itemId: l.def.id, visible: true, data: { lineId: l.def.id, tension_t: round1(t), mbl_t: mbl, bollardId: l.bollardId } });
    }
  }

  /** 寫入狀態欄位(state.schema.json tugs[] / anchors[] / berth)。 */
  write(s: OwnShipState, origin: { lat: number; lon: number }): void {
    const latRad = degToRad(origin.lat);
    const geo = (x: number, y: number) => ({ lat: origin.lat + y / 111_320, lon: origin.lon + x / (111_320 * Math.cos(latRad)), x: round1(x), y: round1(y) });
    if (this.tugs.length > 0) {
      s.tugs = this.tugs.map((t): TugState => {
        const attach = resolveBodyPoint(t.def.attach, this.cfg.lpp_m, this.cfg.breadth_m) ?? { x: 0, y: 0 };
        const a = bodyToEnu(attach, s.heading, s.pos);
        const dir = degToRad(s.heading + t.dir);
        const off = t.mode === 'pull' ? t.def.lineLength_m : -(this.cfg.breadth_m / 2 + 15);
        const out: TugState = {
          id: t.def.id,
          mode: t.mode,
          status: t.status,
          force_t: round1(t.force),
          direction_deg: round1(t.dir) % 360,
          directionOrder_deg: round1(t.dirOrder) % 360,
          power_pct: Math.round(t.power * 100),
          efficiency: Math.round(t.efficiency * 100) / 100,
          bollardPull_t: t.def.bollardPull_t,
          lineParted: t.lineParted,
          pos: geo(a.x + Math.sin(dir) * off, a.y + Math.cos(dir) * off),
        };
        if (t.def.name) out.name = t.def.name;
        return out;
      });
    }
    s.anchors = this.anchors.map((a): AnchorState => {
      const out: AnchorState = {
        side: a.side,
        status: this.anchorStatus(a),
        length_m: round1(a.length),
        length_shackles: Math.round((a.length / this.shackle) * 100) / 100,
        maxLength_m: this.maxLength(a.side),
        tension_t: round1(a.tension),
        horizontalTension_t: round1(a.hTension),
        onBottom_m: round1(a.lying),
        holding_t: round1(a.holding),
        brake: a.mode === 'braked' || a.mode === 'stowed',
        fullyOut: a.fullyOut,
      };
      if (a.onBottom) out.position = geo(a.ax, a.ay);
      return out;
    });
    if (this.berth) {
      const b: BerthState = {
        id: typeof this.berth.definition['id'] === 'string' ? this.berth.definition['id'] : 'DEMO-BERTH',
        quayContact: false,
        contacts: [],
        lines: this.lines.map((l): MooringLineState => {
          const out: MooringLineState = { id: l.def.id, status: l.status, tension_t: round1(l.tension), mbl_t: l.def.mbl_t ?? 50 };
          if (l.bollardId && l.status !== 'stowed') out.bollardId = l.bollardId;
          if (l.status === 'fast' || l.status === 'heaving' || l.status === 'slacking') out.length_m = round1(l.rest);
          return out;
        }),
      };
      if (typeof this.berth.definition['name'] === 'string') b.name = this.berth.definition['name'];
      // 船體與碼頭面距離 < 半寬時視為硬接觸(示範的極簡判斷)
      if (this.geometry) {
        const q = quayDistance(this.geometry, s.pos.x, s.pos.y);
        if (q && q.distance_m < this.cfg.breadth_m / 2) b.quayContact = true;
      }
      s.berth = b;
    }
  }
}

function round1(v: number): number {
  const r = Math.round(v * 10) / 10;
  return r === 0 ? 0 : r;
}
