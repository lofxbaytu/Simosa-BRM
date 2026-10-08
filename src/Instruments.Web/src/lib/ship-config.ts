// 儀器顯示所需的船舶參數,直接取自 data\ships\<ID>\particulars.json(與核心共用同一份資料,CLAUDE.md)。
// 這裡只做「顯示與示範模式」用的摘要:量程、舵角上限、舵機速率、車鐘對應轉速與速度。

import fsb1 from '@data/ships/FSB1/particulars.json';
import fsb2 from '@data/ships/FSB2/particulars.json';
import { SHIP_IDS, type ShipId, type TelegraphPosition } from '../types/state.js';

/** 車鐘位置對應的轉速與(滿載)參考速度;倒車速度無資料時以同轉速的前進速度打折估計。 */
export interface TelegraphStep {
  position: TelegraphPosition;
  /** 中文名稱 */
  label: string;
  rpm: number;
  /** 參考速度 (kn),倒車為負;僅供示範模式與顯示 */
  speedLoaded_kn: number;
  speedBallast_kn: number;
}

export interface ShipDisplayConfig {
  id: ShipId;
  name: string;
  loa_m: number;
  lpp_m: number;
  breadth_m: number;
  /** 舵角上限(FSB1 Schilling 舵 70°、FSB2 35°),舵角指示器量程 */
  rudderMax_deg: number;
  /** 一般操船的舵角上限(舵輪 FU 模式預設限制) */
  rudderNormalMax_deg: number;
  /** 舵機速率 (度/秒),由 35°→30° 另一舷(65°)的時間推得 */
  rudderRate_degPerS: number;
  /** 轉速量程(MCR,正負對稱) */
  rpmMax: number;
  /** 車鐘各段 */
  telegraph: TelegraphStep[];
  /** 艏側推:滿推時零速迴轉率 (度/分) 與達滿推力所需時間 (秒) */
  thrusterTurnRate_degPerMin: number;
  thrusterFullDelay_s: number;
  /** 側推在此速度以上無效 (kn) */
  thrusterIneffectiveAbove_kn: number;
  /** 滿載平均吃水 (m) */
  draftLoaded_m: number;
  draftBallast_m: number;
}

type TelegraphEntry = { rpm: number; speedLoaded_kn?: number; speedBallast_kn?: number };
type TelegraphTable = Record<string, TelegraphEntry | undefined>;

const TELEGRAPH_KEYS: ReadonlyArray<readonly [TelegraphPosition, string, string]> = [
  ['EFAS', 'emergencyFullAstern', '緊急全速倒車'],
  ['FAS', 'fullAstern', '全速倒車'],
  ['HAS', 'halfAstern', '半速倒車'],
  ['SAS', 'slowAstern', '慢速倒車'],
  ['DSAS', 'deadSlowAstern', '微速倒車'],
  ['STOP', 'stop', '停車'],
  ['DSAH', 'deadSlowAhead', '微速前進'],
  ['SAH', 'slowAhead', '慢速前進'],
  ['HAH', 'halfAhead', '半速前進'],
  ['FAH', 'fullAhead', '全速前進'],
  ['NAVF', 'fullSea', '海上全速'],
];

/** 倒車速度估計:同轉速的前進速度 × 0.6 取負(倒車功率約 61%,particulars.asternPowerFraction)。 */
const ASTERN_SPEED_FACTOR = 0.6;

function aheadSpeedAtRpm(table: TelegraphTable, rpm: number, key: 'speedLoaded_kn' | 'speedBallast_kn'): number {
  // 以前進各段的 (rpm, speed) 線性內插/外插
  const pts = TELEGRAPH_KEYS.map(([, k]) => table[k])
    .filter((e): e is TelegraphEntry => !!e && e.rpm > 0 && typeof e[key] === 'number')
    .map((e) => ({ rpm: e.rpm, spd: e[key] as number }))
    .sort((a, b) => a.rpm - b.rpm);
  if (pts.length === 0) return 0;
  const first = pts[0]!;
  if (rpm <= first.rpm) return (first.spd * rpm) / first.rpm;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1]!;
    const b = pts[i]!;
    if (rpm <= b.rpm) return a.spd + ((b.spd - a.spd) * (rpm - a.rpm)) / (b.rpm - a.rpm);
  }
  const last = pts[pts.length - 1]!;
  return (last.spd * rpm) / last.rpm;
}

function buildTelegraph(table: TelegraphTable): TelegraphStep[] {
  const fullAstern = table['fullAstern']?.rpm ?? -100;
  return TELEGRAPH_KEYS.map(([position, key, label]) => {
    if (position === 'STOP') return { position, label, rpm: 0, speedLoaded_kn: 0, speedBallast_kn: 0 };
    let entry = table[key];
    if (!entry && position === 'EFAS') entry = { rpm: fullAstern };
    const rpm = entry?.rpm ?? 0;
    const loaded =
      entry?.speedLoaded_kn ??
      (rpm < 0 ? -ASTERN_SPEED_FACTOR * aheadSpeedAtRpm(table, -rpm, 'speedLoaded_kn') : aheadSpeedAtRpm(table, rpm, 'speedLoaded_kn'));
    const ballast =
      entry?.speedBallast_kn ??
      (rpm < 0 ? -ASTERN_SPEED_FACTOR * aheadSpeedAtRpm(table, -rpm, 'speedBallast_kn') : aheadSpeedAtRpm(table, rpm, 'speedBallast_kn'));
    return { position, label, rpm, speedLoaded_kn: round1(loaded), speedBallast_kn: round1(ballast) };
  });
}

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

type Particulars = typeof fsb1 | typeof fsb2;

function hardOverTime(p: Particulars): number {
  const sg = p.rudder.steeringGear as Record<string, unknown>;
  const candidates = ['hardOverTime35_twoPumps_s', 'hardOverTime35_s', 'hardOverTime35_onePump_s'];
  for (const k of candidates) {
    const v = sg[k];
    if (typeof v === 'number' && v > 0) return v;
  }
  return 28; // SOLAS II-1/29 最低要求:35°→30° 另一舷 28 秒內
}

function fromParticulars(p: Particulars): ShipDisplayConfig {
  const rudderMax = p.rudder.maxAngle_deg;
  const normalMax = (p.rudder as { normalMaxAngle_deg?: number }).normalMaxAngle_deg ?? rudderMax;
  const bt = p.bowThruster as { turningRateAtZeroSpeed_degPerMin?: number; fullThrustDelay_s?: number; notEffectiveAbove_kn?: number };
  return {
    id: p.id as ShipId,
    name: p.name,
    loa_m: p.hull.lengthOverall_m,
    lpp_m: p.hull.lengthBetweenPerpendiculars_m,
    breadth_m: p.hull.breadth_m,
    rudderMax_deg: rudderMax,
    rudderNormalMax_deg: normalMax,
    rudderRate_degPerS: round1(65 / hardOverTime(p)),
    rpmMax: Math.ceil(p.engine.mcr_rpm / 10) * 10,
    telegraph: buildTelegraph(p.engine.telegraph as TelegraphTable),
    thrusterTurnRate_degPerMin: bt.turningRateAtZeroSpeed_degPerMin ?? 20,
    thrusterFullDelay_s: bt.fullThrustDelay_s ?? 30,
    thrusterIneffectiveAbove_kn: bt.notEffectiveAbove_kn ?? 5,
    draftLoaded_m: p.loadingConditions.full.draftMean_m,
    draftBallast_m: p.loadingConditions.ballast.draftMean_m,
  };
}

const CONFIGS: Record<ShipId, ShipDisplayConfig> = {
  FSB1: fromParticulars(fsb1),
  FSB2: fromParticulars(fsb2),
};

export function getShipConfig(id: ShipId): ShipDisplayConfig {
  return CONFIGS[id];
}

export function isShipId(v: unknown): v is ShipId {
  return typeof v === 'string' && (SHIP_IDS as readonly string[]).includes(v);
}

export function telegraphStep(cfg: ShipDisplayConfig, position: TelegraphPosition): TelegraphStep {
  const step = cfg.telegraph.find((s) => s.position === position);
  if (!step) throw new RangeError(`車鐘位置不存在:${position}`);
  return step;
}
