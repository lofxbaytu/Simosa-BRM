// ARPA 顯示邏輯(純函數):目標表排序、符號狀態(擷取中/追蹤中/危險閃爍/失追)、向量端點、
// CPA/TCPA 與試操船 CPA 計算(示範模式用;真實服務由 Sensors.Radar 以 SimCore Encounter.Compute 計算)。

import type { RadarTrack } from './protocol.js';

const RAD = Math.PI / 180;
const M_PER_NM = 1852;
const MPS_PER_KN = 1852 / 3600;

export type ArpaSortKey = 'cpa' | 'tcpa' | 'range' | 'id';

export const SORT_LABEL: Record<ArpaSortKey, string> = {
  cpa: 'CPA',
  tcpa: 'TCPA',
  range: '距離',
  id: '編號',
};

/** 狀態群組:危險(追蹤中)→ 追蹤中 → 擷取中 → 失追 */
function group(t: RadarTrack): number {
  if (t.state === 'tracking') return t.dangerous ? 0 : 1;
  return t.state === 'acquiring' ? 2 : 3;
}

function cmp(a: number, b: number): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * ARPA 目標表排序(不改原陣列):
 * - cpa:危險目標優先;接近中(TCPA ≥ 0)依 CPA 由小到大,已通過(TCPA < 0)排在後;無 CPA(擷取中/失追)依距離。
 * - tcpa:危險目標優先;TCPA ≥ 0 由小到大,已通過排在後;無 TCPA 依距離。
 * - range:距離由近到遠。 - id:編號。
 * 同值以編號排序(穩定、確定)。
 */
export function sortTracks(tracks: readonly RadarTrack[], key: ArpaSortKey): RadarTrack[] {
  const list = [...tracks];
  list.sort((a, b) => {
    if (key === 'range') return cmp(a.range_nm, b.range_nm) || cmp(a.id, b.id);
    if (key === 'id') return cmp(a.id, b.id);
    const g = cmp(group(a), group(b));
    if (g !== 0) return g;
    const hasA = a.cpa_nm !== undefined && a.tcpa_min !== undefined;
    const hasB = b.cpa_nm !== undefined && b.tcpa_min !== undefined;
    if (hasA !== hasB) return hasA ? -1 : 1;
    if (!hasA) return cmp(a.range_nm, b.range_nm) || cmp(a.id, b.id);
    const pastA = (a.tcpa_min ?? 0) < 0 ? 1 : 0;
    const pastB = (b.tcpa_min ?? 0) < 0 ? 1 : 0;
    if (pastA !== pastB) return pastA - pastB;
    if (key === 'cpa') return cmp(a.cpa_nm ?? 0, b.cpa_nm ?? 0) || cmp(a.tcpa_min ?? 0, b.tcpa_min ?? 0) || cmp(a.id, b.id);
    return cmp(a.tcpa_min ?? 0, b.tcpa_min ?? 0) || cmp(a.cpa_nm ?? 0, b.cpa_nm ?? 0) || cmp(a.id, b.id);
  });
  return list;
}

export type SymbolKind = 'acquiring' | 'tracking' | 'lost';

export interface SymbolStyle {
  kind: SymbolKind;
  /** 危險目標以警報色顯示 */
  danger: boolean;
  /** 未確認的危險/失追目標閃爍:此刻是否顯示 */
  visible: boolean;
  /** 是否畫向量(只在追蹤中且有向量時) */
  vector: boolean;
}

/** 符號樣式:危險與失追在確認(acknowledged)前以 blinkOn 閃爍。 */
export function symbolStyle(t: RadarTrack, acknowledged: ReadonlySet<string>, blinkOn: boolean): SymbolStyle {
  const kind: SymbolKind = t.state;
  const danger = t.state === 'tracking' && t.dangerous;
  const alarmKey = danger ? dangerKey(t.id) : t.state === 'lost' ? lostKey(t.id) : null;
  const flashing = alarmKey !== null && !acknowledged.has(alarmKey);
  return {
    kind,
    danger,
    visible: !flashing || blinkOn,
    vector: t.state === 'tracking' && t.course_deg !== undefined && t.speed_kn !== undefined,
  };
}

export function dangerKey(id: number): string {
  return `danger:${id}`;
}
export function lostKey(id: number): string {
  return `lost:${id}`;
}

/** 目前未確認的警示(危險目標、失追目標)鍵值 */
export function pendingAlarms(tracks: readonly RadarTrack[], acknowledged: ReadonlySet<string>): string[] {
  const out: string[] = [];
  for (const t of tracks) {
    if (t.state === 'tracking' && t.dangerous && !acknowledged.has(dangerKey(t.id))) out.push(dangerKey(t.id));
    if (t.state === 'lost' && !acknowledged.has(lostKey(t.id))) out.push(lostKey(t.id));
  }
  return out;
}

/** 移除已不適用的確認(目標不再危險或已刪除),讓再次變危險時重新閃爍。 */
export function pruneAcknowledged(tracks: readonly RadarTrack[], acknowledged: Set<string>): void {
  const live = new Set<string>();
  for (const t of tracks) {
    if (t.state === 'tracking' && t.dangerous) live.add(dangerKey(t.id));
    if (t.state === 'lost') live.add(lostKey(t.id));
  }
  for (const k of [...acknowledged]) if (!live.has(k)) acknowledged.delete(k);
}

export type VectorMode = 'true' | 'relative';

/** 向量端點相對目標位置的 ENU 位移(m)。vectorMin = 向量時間(分)。無向量時回傳 null。 */
export function vectorEnu(t: RadarTrack, mode: VectorMode, vectorMin: number): { dE: number; dN: number } | null {
  const c = mode === 'true' ? t.course_deg : t.relCourse_deg;
  const s = mode === 'true' ? t.speed_kn : t.relSpeed_kn;
  if (c === undefined || s === undefined) return null;
  const d = s * MPS_PER_KN * vectorMin * 60;
  return { dE: d * Math.sin(c * RAD), dN: d * Math.cos(c * RAD) };
}

// ---------------------------------------------------------------------------------------------
// CPA / TCPA(示範模式與試操船預覽用)

export interface CpaResult {
  cpaNm: number;
  /** 分;負值 = 已通過 */
  tcpaMin: number;
}

/** 相對位置 (dE, dN)(m;目標 − 自船)與相對速度 (vE, vN)(m/s)→ CPA/TCPA。 */
export function computeCpa(dE: number, dN: number, vE: number, vN: number): CpaResult {
  const v2 = vE * vE + vN * vN;
  if (v2 < 1e-9) return { cpaNm: Math.hypot(dE, dN) / M_PER_NM, tcpaMin: 0 };
  const tcpa = -(dE * vE + dN * vN) / v2;
  const x = dE + vE * tcpa;
  const y = dN + vN * tcpa;
  return { cpaNm: Math.hypot(x, y) / M_PER_NM, tcpaMin: tcpa / 60 };
}

/** 航向(度)與速度(kn)→ ENU 速度(m/s) */
export function velocityEnu(courseDeg: number, speedKn: number): { vE: number; vN: number } {
  const v = speedKn * MPS_PER_KN;
  return { vE: v * Math.sin(courseDeg * RAD), vN: v * Math.cos(courseDeg * RAD) };
}

/**
 * 試操船 CPA(MSC.192(79) 5.33):自船以目前速度向量航行 delayMin 分後瞬間改為 (trialCourse, trialSpeed);
 * 目標維持等速。兩段相對運動各取最近點,回傳整體最近點(TCPA 由現在起算,分)。
 */
export function trialCpa(
  dE: number,
  dN: number,
  target: { vE: number; vN: number },
  own: { vE: number; vN: number },
  trial: { vE: number; vN: number },
  delayMin: number,
): CpaResult {
  const D = Math.max(0, delayMin * 60);
  // 第一段:t ∈ [0, D]
  const r1E = target.vE - own.vE;
  const r1N = target.vN - own.vN;
  const v1 = r1E * r1E + r1N * r1N;
  let t1 = v1 < 1e-9 ? 0 : -(dE * r1E + dN * r1N) / v1;
  t1 = Math.min(D, Math.max(0, t1));
  const d1 = Math.hypot(dE + r1E * t1, dN + r1N * t1);
  // 第二段:t ≥ D
  const pE = dE + r1E * D;
  const pN = dN + r1N * D;
  const r2E = target.vE - trial.vE;
  const r2N = target.vN - trial.vN;
  const v2 = r2E * r2E + r2N * r2N;
  let t2 = v2 < 1e-9 ? 0 : -(pE * r2E + pN * r2N) / v2;
  t2 = Math.max(0, t2);
  const d2 = Math.hypot(pE + r2E * t2, pN + r2N * t2);
  return d2 < d1 - 1e-9
    ? { cpaNm: d2 / M_PER_NM, tcpaMin: (D + t2) / 60 }
    : { cpaNm: d1 / M_PER_NM, tcpaMin: t1 / 60 };
}

/** 是否為危險目標(CPA < 限值且 0 ≤ TCPA ≤ 限值) */
export function isDangerous(cpaNm: number, tcpaMin: number, cpaLimitNm: number, tcpaLimitMin: number): boolean {
  return cpaNm < cpaLimitNm && tcpaMin >= 0 && tcpaMin <= tcpaLimitMin;
}

/** TCPA 文字(分,一位小數;負值用 Unicode 減號) */
export function formatTcpa(min: number | undefined): string {
  if (min === undefined || !Number.isFinite(min)) return '--.-';
  const v = Number(min.toFixed(1));
  return v < 0 ? `−${Math.abs(v).toFixed(1)}` : v.toFixed(1);
}

/** 狀態文字 */
export function trackStateLabel(t: RadarTrack): string {
  if (t.state === 'acquiring') return '擷取中';
  if (t.state === 'lost') return '失追';
  return t.dangerous ? '危險' : '追蹤';
}
