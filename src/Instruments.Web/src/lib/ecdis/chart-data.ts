// 簡化 ECDIS 的底圖資料(docs/design/ecdis-lite.md 第 2–3 節):沒有 ENC 時以 data/areas/*.json 的示意幾何(海岸線/防波堤/淺灘/錨地/速限區/岸壁,
// 沿用 lib/plot-overlay.sketchFromAreas)當底圖,加上情境 route(航點/走廊)、zones(禁區/速限/UKC 政策/報告區)與水深區
//(zones.waterDepth_m、areas shoals.depth_m / channel.depth_m);原點取情境 origin(與核心相同規則),情境不明時由狀態 lat/lon 與 x/y 反推。
// ENC 到位後只需替換本模組的底圖來源(sketch/depth),其餘顯示與監控不變。

import type { OwnShipState } from '../../types/state.js';
import type { ScenarioSummary } from '../scenario.js';
import { overlayRoute, sketchFromAreas, type OverlayRoute, type OverlaySketch } from '../plot-overlay.js';
import { geoPointToEnu, latLonToEnu } from '../timeline.js';
import { getShipConfig, isShipId, maxDraft } from '../ship-config.js';
import { inferOrigin } from './projection.js';
import { safetyContours, ukcMinimum, type DepthArea, type DepthModel, type Pt, type SafetyContours, type UkcMinimum } from './safety-depth.js';

/** 預設 CPA/TCPA 門檻(情境 assessment.minCpa_nm 未給或為參數引用時)。 */
export const DEFAULT_MIN_CPA_NM = 0.5;
export const DEFAULT_TCPA_LIMIT_MIN = 12;
/** 情境與狀態皆無水深時的基底水深(m;標為未知) */
const UNKNOWN_BASE_DEPTH_M = 20;

export interface EcdisZone {
  id: string;
  name: string;
  /** noGo | speedLimit | ukcPolicy | reporting | 自訂 */
  type: string;
  polygon: Pt[];
  waterDepth_m?: number;
  speedLimit_kn?: number;
}

export type OriginSource = 'scenario' | 'state' | 'none';

export interface EcdisChart {
  scenarioId: string | null;
  scenarioName: string;
  origin: { lat: number; lon: number } | null;
  originSource: OriginSource;
  sketch: OverlaySketch | null;
  zones: EcdisZone[];
  route: OverlayRoute | null;
  depth: DepthModel;
  /** 基底水深來自情境或狀態(false = 用預設 20 m,只供著色參考) */
  baseDepthKnown: boolean;
  minCpa_nm: number;
  minCpaSource: 'scenario' | 'default';
  tcpaLimit_min: number;
  /** 自船(狀態 shipId;無狀態時情境 shipId)最大吃水 (m) */
  maxDraft_m: number;
  ukc: UkcMinimum;
  contours: SafetyContours;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function polygonOf(raw: unknown, origin: { lat: number; lon: number }): Pt[] {
  if (!Array.isArray(raw)) return [];
  const out: Pt[] = [];
  for (const p of raw) {
    if (isRecord(p) && typeof p['lat'] === 'number' && typeof p['lon'] === 'number') out.push(latLonToEnu(origin, p['lat'], p['lon']));
    else if (isRecord(p) && typeof p['x'] === 'number' && typeof p['y'] === 'number') out.push({ x: p['x'], y: p['y'] });
  }
  return out;
}

/**
 * 由區域檔收集 origin 周圍 radius_m 內的水深區:shoals[].depth_m(淺灘)與 channel.depth_m(維護航道,比周圍深時仍記錄以便標示)。
 * 沒有 depth_m 的淺灘以 shoalDefault_m(chartDatum.shoalDepth_m 或 2 m)計。
 */
export function depthAreasFromAreas(areaFiles: Readonly<Record<string, unknown>>, origin: { lat: number; lon: number } | null, radius_m = 30_000): DepthArea[] {
  if (!origin) return [];
  const out: DepthArea[] = [];
  const near = (poly: Pt[]) => poly.length >= 3 && poly.some((p) => Math.hypot(p.x, p.y) <= radius_m);
  for (const key of Object.keys(areaFiles).sort()) {
    const doc = areaFiles[key];
    if (!isRecord(doc)) continue;
    const datum = isRecord(doc['chartDatum']) ? doc['chartDatum'] : {};
    const shoalDefault = typeof datum['shoalDepth_m'] === 'number' ? datum['shoalDepth_m'] : 2;
    const file = key.replace(/\\/g, '/').split('/').pop() ?? key;
    if (Array.isArray(doc['shoals'])) {
      doc['shoals'].forEach((s, i) => {
        if (!isRecord(s)) return;
        const polygon = polygonOf(s['polygon'], origin);
        if (!near(polygon)) return;
        const depth = typeof s['depth_m'] === 'number' ? s['depth_m'] : shoalDefault;
        out.push({ id: `${file}:shoal:${String(s['id'] ?? i)}`, name: String(s['name'] ?? s['id'] ?? '淺灘'), kind: 'shoal', depth_m: depth, polygon });
      });
    }
    const ch = doc['channel'];
    if (isRecord(ch) && typeof ch['depth_m'] === 'number') {
      const polygon = polygonOf(ch['polygon'], origin);
      if (near(polygon)) out.push({ id: `${file}:channel`, name: String(ch['name'] ?? '航道'), kind: 'channel', depth_m: ch['depth_m'], polygon });
    }
  }
  return out;
}

/** 情境 zones → ENU 多邊形(含 waterDepth_m / 可解析的 speedLimit_kn)。 */
export function zonesFromScenario(summary: ScenarioSummary | null, origin: { lat: number; lon: number } | null): EcdisZone[] {
  if (!summary) return [];
  const out: EcdisZone[] = [];
  for (const z of summary.zones) {
    const polygon = z.polygon.map((p) => geoPointToEnu(p, origin)).filter((p): p is Pt => p !== null);
    if (polygon.length < 3) continue;
    const zone: EcdisZone = { id: z.id, name: z.name, type: z.type, polygon };
    if (z.waterDepth_m !== undefined) zone.waterDepth_m = z.waterDepth_m;
    if (z.speedLimit_kn?.value !== undefined) zone.speedLimit_kn = z.speedLimit_kn.value;
    out.push(zone);
  }
  return out;
}

/**
 * 建立底圖:情境摘要(由 state.scenario.id 在建置時匯入的清單找到)+ 目前狀態 + 區域檔。
 * 沒有情境時仍可由狀態反推原點畫示意幾何;沒有狀態時用情境 shipId 的吃水。
 */
export function buildChart(summary: ScenarioSummary | null, state: OwnShipState | null, areaFiles: Readonly<Record<string, unknown>>): EcdisChart {
  let origin: { lat: number; lon: number } | null = summary?.origin ?? null;
  let originSource: OriginSource = origin ? 'scenario' : 'none';
  if (!origin && state) {
    origin = inferOrigin(state.pos);
    if (origin) originSource = 'state';
  }
  const shipId = state?.shipId ?? summary?.shipId ?? 'FSB1';
  const loading = state?.loading ?? summary?.loading ?? 'full';
  const draft = isShipId(shipId) ? maxDraft(getShipConfig(shipId), loading) : 7.2;
  const ukc = ukcMinimum(summary?.assessment ?? null, draft);
  const zones = zonesFromScenario(summary, origin);
  const tide = state?.tideHeight_m ?? 0;
  let base = summary?.environment.waterDepth;
  let baseKnown = base !== undefined;
  if (base === undefined && state?.waterDepth !== undefined && Number.isFinite(state.waterDepth) && state.waterDepth > 0) {
    base = Math.max(0.1, state.waterDepth - tide);
    baseKnown = true;
  }
  const areas: DepthArea[] = [];
  for (const z of zones) if (z.waterDepth_m !== undefined) areas.push({ id: `zone:${z.id}`, name: z.name, kind: 'zone', depth_m: z.waterDepth_m, polygon: z.polygon });
  areas.push(...depthAreasFromAreas(areaFiles, origin));
  const minCpa = summary?.assessment.minCpa_nm?.value;
  return {
    scenarioId: summary?.id ?? state?.scenario?.id ?? null,
    scenarioName: summary?.name ?? '',
    origin,
    originSource,
    sketch: sketchFromAreas(areaFiles, origin),
    zones,
    route: summary ? overlayRoute(summary.route, origin) : null,
    depth: { baseDepth_m: base ?? UNKNOWN_BASE_DEPTH_M, areas },
    baseDepthKnown: baseKnown,
    minCpa_nm: minCpa !== undefined && minCpa > 0 ? minCpa : DEFAULT_MIN_CPA_NM,
    minCpaSource: minCpa !== undefined && minCpa > 0 ? 'scenario' : 'default',
    tcpaLimit_min: DEFAULT_TCPA_LIMIT_MIN,
    maxDraft_m: draft,
    ukc,
    contours: safetyContours(draft, ukc.ukc_m),
  };
}

/** 空底圖(尚未收到狀態)。 */
export function emptyChart(): EcdisChart {
  return buildChart(null, null, {});
}
