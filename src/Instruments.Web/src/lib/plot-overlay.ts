// 鳥瞰航跡圖的疊圖資料(設計文件第 6 節 W8):由情境摘要(教官站即時)或紀錄 header.scenario(重播檢視)
// 轉成本地 ENU 的航線(航點、走廊半寬)、區域多邊形與事件標記;座標換算用情境 origin(無 origin 時只有 x/y 點可畫)。
// 第二十一輪(docs/design/instructor-plot.md):加流場網格層(環境 current.field 解析後的 CurrentFieldSpec;箭頭由 track-plot 取樣繪製)
// 與示意幾何層(data/areas/*.json 的海岸線/防波堤/淺灘/錨地/速限區/岸壁;情境編輯器內嵌圖無 ENC 時的底圖)。

import type { SimEvent } from '../types/event.js';
import type { OwnShipState } from '../types/state.js';
import { geoPointToEnu, parseRoute, parseZones, type ScenarioRoute, type ScenarioZone } from './timeline.js';
import type { ScenarioSummary } from './scenario.js';
import { eventSeverity, type EventSeverity } from './sim-event.js';
import { stateIndexAt } from './record-replay.js';
import { berthGeometry, parseScenarioTugs, resolveScenarioBerth, type BerthGeometry, type ScenarioMooringLineDef, type ScenarioTugDef } from './external-forces.js';
import { currentFieldOfScenario, type CurrentFieldSpec } from './current-field-plot.js';
import { latLonToEnu } from './timeline.js';

export interface OverlayWaypoint {
  name: string;
  x: number;
  y: number;
  /** 到達此航點的航段走廊半寬(m);未知為 undefined(參數引用未解析) */
  xtdLimit_m?: number;
  checkpoint: boolean;
  report: boolean;
  arrivalRadius_m?: number;
}

export interface OverlayRoute {
  name: string;
  waypoints: OverlayWaypoint[];
}

export interface OverlayZone {
  id: string;
  name: string;
  type: string;
  polygon: Array<{ x: number; y: number }>;
}

export interface OverlayEventMark {
  x: number;
  y: number;
  t: number;
  severity: EventSeverity;
  kind: string;
}

export interface PlotOverlay {
  route: OverlayRoute | null;
  zones: OverlayZone[];
  events: OverlayEventMark[];
  /** 泊位幾何(碼頭線、碰墊、繫船柱;情境 berth;docs/design/physics-tugs-anchors-mooring.md 第 4 節) */
  berth?: BerthGeometry | null;
  /** 繫纜定義(導纜孔具名點、預設繫船柱;畫纜繩線段用) */
  mooringLines?: ScenarioMooringLineDef[];
  /** 拖船定義(連接點;畫拖纜/推頂點用) */
  tugDefs?: ScenarioTugDef[];
  /** 空間流場網格(情境 environment.current.field;track-plot 的流場層取樣畫箭頭;無/不合法則省略) */
  currentField?: CurrentFieldSpec | null;
  /** 示意幾何(data/areas 的海岸線等;情境編輯器內嵌圖底圖;即時/重播不帶) */
  sketch?: OverlaySketch | null;
}

/** 示意幾何形狀種類(對應 data/areas/*.json 的陣列鍵)。 */
export type SketchKind = 'land' | 'breakwater' | 'shoal' | 'anchorage' | 'speedLimit' | 'bank';

export interface OverlaySketchShape {
  kind: SketchKind;
  name: string;
  /** 多邊形(land/breakwater/shoal/anchorage/speedLimit)或折線(bank)的 ENU 點 */
  points: Array<{ x: number; y: number }>;
}

export interface OverlaySketch {
  shapes: OverlaySketchShape[];
  /** 使用到的區域檔名(顯示來源) */
  files: string[];
}

/** 疊圖是否有任何可畫的內容(航線、區域、事件、泊位、拖船、流場網格、示意幾何)。 */
export function overlayHasContent(o: PlotOverlay): boolean {
  return o.route !== null || o.zones.length > 0 || o.events.length > 0 || !!o.berth || (o.tugDefs?.length ?? 0) > 0 || !!o.currentField || (o.sketch?.shapes.length ?? 0) > 0;
}

const SKETCH_KEYS: ReadonlyArray<readonly [key: string, kind: SketchKind, member: 'polygon' | 'line' | 'points']> = [
  ['land', 'land', 'points'],
  ['breakwaters', 'breakwater', 'polygon'],
  ['shoals', 'shoal', 'polygon'],
  ['anchorages', 'anchorage', 'polygon'],
  ['speedLimitZones', 'speedLimit', 'polygon'],
  ['banks', 'bank', 'line'],
] as const;

function sketchPoints(raw: unknown, member: 'polygon' | 'line' | 'points', origin: { lat: number; lon: number }): Array<{ x: number; y: number }> {
  if (!isRecord(raw) || !Array.isArray(raw[member])) return [];
  const out: Array<{ x: number; y: number }> = [];
  for (const p of raw[member] as unknown[]) {
    if (Array.isArray(p) && typeof p[0] === 'number' && typeof p[1] === 'number') out.push(latLonToEnu(origin, p[0], p[1]));
    else if (isRecord(p)) {
      if (typeof p['x'] === 'number' && typeof p['y'] === 'number') out.push({ x: p['x'], y: p['y'] });
      else if (typeof p['lat'] === 'number' && typeof p['lon'] === 'number') out.push(latLonToEnu(origin, p['lat'], p['lon']));
    }
  }
  return out;
}

/**
 * 由建置時匯入的區域檔(鍵 = 路徑、值 = JSON)收集 origin 周圍 radius_m 內的示意幾何(任一頂點在範圍內即收錄;
 * land 的 points 為 [lat, lon] 對,其餘為 {lat, lon} 或 {x, y} 物件)。無 origin 或沒有形狀時回傳 null。
 */
export function sketchFromAreas(areaFiles: Readonly<Record<string, unknown>>, origin: { lat: number; lon: number } | null, radius_m = 30_000): OverlaySketch | null {
  if (!origin) return null;
  const shapes: OverlaySketchShape[] = [];
  const files: string[] = [];
  for (const key of Object.keys(areaFiles).sort()) {
    const doc = areaFiles[key];
    if (!isRecord(doc)) continue;
    let used = false;
    for (const [arrayKey, kind, member] of SKETCH_KEYS) {
      const list = doc[arrayKey];
      if (!Array.isArray(list)) continue;
      for (const item of list) {
        const points = sketchPoints(item, member, origin);
        if (points.length < 2 || !points.some((p) => Math.hypot(p.x, p.y) <= radius_m)) continue;
        shapes.push({ kind, name: isRecord(item) ? String(item['name'] ?? item['id'] ?? '') : '', points });
        used = true;
      }
    }
    if (used) files.push(key.replace(/\\/g, '/').split('/').pop() ?? key);
  }
  return shapes.length > 0 ? { shapes, files } : null;
}

function withExternal(base: PlotOverlay, berthRaw: unknown, tugsRaw: unknown, origin: { lat: number; lon: number } | null, resolved?: { definition: Record<string, unknown> | null; lines: ScenarioMooringLineDef[] } | null, tugDefs?: ScenarioTugDef[]): PlotOverlay {
  const berth = resolved !== undefined ? resolved : resolveScenarioBerth(berthRaw);
  const geo = berth?.definition ? berthGeometry(berth.definition, origin) : null;
  if (geo) base.berth = geo;
  if (berth && berth.lines.length > 0) base.mooringLines = berth.lines;
  const tugs = tugDefs ?? parseScenarioTugs(tugsRaw);
  if (tugs.length > 0) base.tugDefs = tugs;
  return base;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function originOf(raw: Record<string, unknown>): { lat: number; lon: number } | null {
  const o = raw['origin'];
  if (isRecord(o) && typeof o['lat'] === 'number' && typeof o['lon'] === 'number') return { lat: o['lat'], lon: o['lon'] };
  const init = raw['initial'];
  if (isRecord(init) && isRecord(init['position']) && typeof init['position']['lat'] === 'number' && typeof init['position']['lon'] === 'number') return { lat: init['position']['lat'], lon: init['position']['lon'] };
  return null;
}

export function overlayRoute(route: ScenarioRoute | null, origin: { lat: number; lon: number } | null): OverlayRoute | null {
  if (!route) return null;
  const waypoints: OverlayWaypoint[] = [];
  for (const w of route.waypoints) {
    const p = geoPointToEnu(w, origin);
    if (!p) continue;
    const wp: OverlayWaypoint = { name: w.name, x: p.x, y: p.y, checkpoint: w.checkpoint, report: w.report !== undefined };
    const limit = w.xtdLimit_m?.value ?? route.xtdLimit_m?.value;
    if (limit !== undefined) wp.xtdLimit_m = limit;
    if (w.arrivalRadius_m !== undefined) wp.arrivalRadius_m = w.arrivalRadius_m;
    waypoints.push(wp);
  }
  return waypoints.length >= 2 ? { name: route.name, waypoints } : null;
}

export function overlayZones(zones: readonly ScenarioZone[], origin: { lat: number; lon: number } | null): OverlayZone[] {
  const out: OverlayZone[] = [];
  for (const z of zones) {
    const polygon = z.polygon.map((p) => geoPointToEnu(p, origin)).filter((p): p is { x: number; y: number } => p !== null);
    if (polygon.length >= 3) out.push({ id: z.id, name: z.name, type: z.type, polygon });
  }
  return out;
}

/** 由教官站的情境摘要建立疊圖(即時模式);流場網格取摘要 currentField。 */
export function overlayFromScenario(s: ScenarioSummary | null): PlotOverlay {
  if (!s) return { route: null, zones: [], events: [] };
  const origin = s.origin ?? null;
  const base = withExternal({ route: overlayRoute(s.route, origin), zones: overlayZones(s.zones, origin), events: [] }, undefined, undefined, origin, s.berth ?? null, s.tugs ?? []);
  if (s.currentField) base.currentField = s.currentField;
  return base;
}

/** 由紀錄 header.scenario(解析後的完整情境物件)建立疊圖(重播檢視);流場網格由 environment.current.field 解析。 */
export function overlayFromHeaderScenario(raw: unknown): PlotOverlay {
  if (!isRecord(raw)) return { route: null, zones: [], events: [] };
  const origin = originOf(raw);
  // 紀錄 header 的情境在載入時已內嵌 berth.definition(重播不讀檔)
  const base = withExternal({ route: overlayRoute(parseRoute(raw['route']), origin), zones: overlayZones(parseZones(raw['zones']), origin), events: [] }, raw['berth'], raw['tugs'], origin);
  const field = currentFieldOfScenario(raw);
  if (field) base.currentField = field;
  return base;
}

/** 事件標記:以事件時刻的自船位置(狀態序列內插前一筆)為座標;只標 warning/alarm 與時間表觸發。 */
export function eventMarks(events: readonly SimEvent[], states: readonly OwnShipState[], tMax = Number.POSITIVE_INFINITY): OverlayEventMark[] {
  const out: OverlayEventMark[] = [];
  if (states.length === 0) return out;
  for (const e of events) {
    if (e.t > tMax) continue;
    const sev = eventSeverity(e);
    if (sev === 'info' && e.kind !== 'timelineTriggered' && e.kind !== 'checkpointPassed' && e.kind !== 'reportPointPassed') continue;
    const i = stateIndexAt(states, e.t);
    if (i < 0) continue;
    const s = states[i]!;
    out.push({ x: s.pos.x, y: s.pos.y, t: e.t, severity: sev, kind: e.kind });
  }
  return out;
}
