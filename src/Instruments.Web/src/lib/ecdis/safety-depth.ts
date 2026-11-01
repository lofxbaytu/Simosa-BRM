// 簡化 ECDIS 的水深模型與安全等深線(docs/design/ecdis-lite.md 第 3 節):
//  - 水深區:情境 zones 的 waterDepth_m(ukcPolicy;與核心 ZoneDepth 相同「多區重疊取最淺」)、data/areas 的 shoals.depth_m 與 channel.depth_m;
//    其餘為基底水深(情境 environment.waterDepth);有效水深 = 圖載水深 + 潮高(state.tideHeight_m);
//  - 安全等深線 = 自船最大吃水 + UKC 下限(情境 assessment.ukc.min 的公尺值或吃水比例;參數引用未解析時用公司 UKC 政策最小值 1.0 m,
//    即 data/parameters navigation.ukc.minimum_m 的預設);S-52 四階著色:淺於吃水(very shallow)、淺於安全等深線(shallow)、
//    淺於深水等深線(medium = 2 × 安全深度)、其餘 deep;
//  - 前方掃描:沿 COG 以 SOG 前進 6 分鐘的路徑逐點取有效水深,第一個淺於安全深度的點即「安全等深線接近」警報。
// 全部為純函數,不改核心狀態;結果只供顯示與頁面本地警報。

import type { AssessmentRules } from '../assessment.js';
import { M_PER_NM } from '../../radar/geometry.js';

export interface Pt {
  x: number;
  y: number;
}

/** 射線法點在多邊形內(邊界視為內)。 */
export function pointInPolygon(p: Pt, polygon: readonly Pt[]): boolean {
  const n = polygon.length;
  if (n < 3) return false;
  let inside = false;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    const a = polygon[i]!;
    const b = polygon[j]!;
    if (onSegment(p, a, b)) return true;
    const cross = a.y > p.y !== b.y > p.y && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x;
    if (cross) inside = !inside;
  }
  return inside;
}

function onSegment(p: Pt, a: Pt, b: Pt, eps = 1e-6): boolean {
  const cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
  if (Math.abs(cross) > eps * Math.max(1, Math.hypot(b.x - a.x, b.y - a.y))) return false;
  return p.x >= Math.min(a.x, b.x) - eps && p.x <= Math.max(a.x, b.x) + eps && p.y >= Math.min(a.y, b.y) - eps && p.y <= Math.max(a.y, b.y) + eps;
}

export type DepthAreaKind = 'zone' | 'shoal' | 'channel';

export interface DepthArea {
  id: string;
  name: string;
  kind: DepthAreaKind;
  /** 圖載水深 (m;基準面,不含潮高) */
  depth_m: number;
  polygon: Pt[];
}

export interface DepthModel {
  /** 基底水深 (m;情境 environment.waterDepth) */
  baseDepth_m: number;
  areas: DepthArea[];
}

/** 某點的圖載水深:包含該點的水深區取最淺,無則基底水深。 */
export function chartDepthAt(model: DepthModel, p: Pt): { depth_m: number; area: DepthArea | null } {
  let depth = model.baseDepth_m;
  let hit: DepthArea | null = null;
  for (const a of model.areas) {
    if (a.depth_m < depth && pointInPolygon(p, a.polygon)) {
      depth = a.depth_m;
      hit = a;
    }
  }
  return { depth_m: depth, area: hit };
}

/** 有效水深 = 圖載水深 + 潮高。 */
export function effectiveDepth(chartDepth_m: number, tide_m: number | undefined): number {
  return chartDepth_m + (tide_m ?? 0);
}

export type UkcSource = 'scenario' | 'company' | 'default';

export interface UkcMinimum {
  ukc_m: number;
  source: UkcSource;
  /** 說明(例如「情境 10% 吃水」、「公司 UKC 政策最小值 1.0 m(參數未解析)」) */
  note: string;
}

/** 公司 UKC 政策最小值預設(data/parameters/company-procedures.yaml navigation.ukc.minimum_m)。 */
export const COMPANY_UKC_MIN_M = 1.0;

/**
 * UKC 下限:情境 assessment.ukc.min 的 metres / fractionOfDraft 中可解析的數值取較大者;皆為參數引用或未給時用公司最小值。
 * 參數引用(${params:…})在網頁端不解析(與教官站相同)。
 */
export function ukcMinimum(rules: Pick<AssessmentRules, 'ukc'> | null | undefined, maxDraft_m: number, companyMin_m = COMPANY_UKC_MIN_M): UkcMinimum {
  const min = rules?.ukc?.min;
  const metres = min?.metres?.value;
  const fraction = min?.fractionOfDraft?.value;
  const candidates: Array<{ v: number; note: string }> = [];
  if (metres !== undefined && metres > 0) candidates.push({ v: metres, note: `情境 UKC ${metres} m` });
  if (fraction !== undefined && fraction > 0) candidates.push({ v: fraction * maxDraft_m, note: `情境 ${Math.round(fraction * 100)}% 吃水` });
  if (candidates.length > 0) {
    const best = candidates.reduce((a, b) => (b.v > a.v ? b : a));
    return { ukc_m: round2(best.v), source: 'scenario', note: best.note };
  }
  const unresolved = min?.metres?.param !== undefined || min?.fractionOfDraft?.param !== undefined;
  return { ukc_m: companyMin_m, source: unresolved ? 'company' : 'default', note: unresolved ? `公司 UKC 政策最小值 ${companyMin_m} m(情境參數未解析)` : `公司 UKC 政策最小值 ${companyMin_m} m` };
}

function round2(v: number): number {
  return Math.round(v * 100) / 100;
}

export interface SafetyContours {
  /** 淺水等深線 = 最大吃水 (m) */
  shallow_m: number;
  /** 安全等深線 = 最大吃水 + UKC 下限 (m) */
  safety_m: number;
  /** 深水等深線 = 2 × 安全等深線 (m) */
  deep_m: number;
}

export function safetyContours(maxDraft_m: number, ukc_m: number): SafetyContours {
  const safety = round2(maxDraft_m + ukc_m);
  return { shallow_m: round2(maxDraft_m), safety_m: safety, deep_m: round2(2 * safety) };
}

export type DepthClass = 'veryShallow' | 'shallow' | 'medium' | 'deep';

/** S-52 四階水深分類(以有效水深比較)。 */
export function classifyDepth(effectiveDepth_m: number, c: SafetyContours): DepthClass {
  if (effectiveDepth_m < c.shallow_m) return 'veryShallow';
  if (effectiveDepth_m < c.safety_m) return 'shallow';
  if (effectiveDepth_m < c.deep_m) return 'medium';
  return 'deep';
}

export interface AheadScan {
  hit: boolean;
  /** 到第一個淺於安全深度的點(m;自船已在淺區時 0) */
  distance_m?: number;
  /** 以 SOG 到達該點的秒數(SOG ≈ 0 且自船在淺區時 0) */
  time_s?: number;
  depth_m?: number;
  area?: DepthArea | null;
}

/**
 * 沿 COG 以 SOG 前進 minutes 分鐘的直線路徑逐點(step_m)取有效水深,第一個 < safety_m 的點即命中;
 * SOG < 0.1 kn 時只檢查自船位置。路徑長度上限 24 nm(避免極端 SOG 的大量取樣)。
 */
export function scanAhead(model: DepthModel, tide_m: number | undefined, pos: Pt, cogDeg: number, sogKn: number, safety_m: number, minutes = 6, step_m = 25): AheadScan {
  const speed_mps = Math.max(0, sogKn) * (M_PER_NM / 3600);
  const length = Math.min(24 * M_PER_NM, sogKn < 0.1 ? 0 : speed_mps * minutes * 60);
  const a = (cogDeg * Math.PI) / 180;
  const sx = Math.sin(a);
  const sy = Math.cos(a);
  const n = Math.max(0, Math.ceil(length / step_m));
  for (let i = 0; i <= n; i++) {
    const s = Math.min(length, i * step_m);
    const p = { x: pos.x + s * sx, y: pos.y + s * sy };
    const d = chartDepthAt(model, p);
    const eff = effectiveDepth(d.depth_m, tide_m);
    if (eff < safety_m) return { hit: true, distance_m: s, time_s: speed_mps > 0.01 ? s / speed_mps : 0, depth_m: round2(eff), area: d.area };
  }
  return { hit: false };
}
