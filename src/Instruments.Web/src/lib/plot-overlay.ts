// 鳥瞰航跡圖的疊圖資料(設計文件第 6 節 W8):由情境摘要(教官站即時)或紀錄 header.scenario(重播檢視)
// 轉成本地 ENU 的航線(航點、走廊半寬)、區域多邊形與事件標記;座標換算用情境 origin(無 origin 時只有 x/y 點可畫)。

import type { SimEvent } from '../types/event.js';
import type { OwnShipState } from '../types/state.js';
import { geoPointToEnu, parseRoute, parseZones, type ScenarioRoute, type ScenarioZone } from './timeline.js';
import type { ScenarioSummary } from './scenario.js';
import { eventSeverity, type EventSeverity } from './sim-event.js';
import { stateIndexAt } from './record-replay.js';

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

/** 由教官站的情境摘要建立疊圖(即時模式)。 */
export function overlayFromScenario(s: ScenarioSummary | null): PlotOverlay {
  if (!s) return { route: null, zones: [], events: [] };
  const origin = s.origin ?? null;
  return { route: overlayRoute(s.route, origin), zones: overlayZones(s.zones, origin), events: [] };
}

/** 由紀錄 header.scenario(解析後的完整情境物件)建立疊圖(重播檢視)。 */
export function overlayFromHeaderScenario(raw: unknown): PlotOverlay {
  if (!isRecord(raw)) return { route: null, zones: [], events: [] };
  const origin = originOf(raw);
  return { route: overlayRoute(parseRoute(raw['route']), origin), zones: overlayZones(parseZones(raw['zones']), origin), events: [] };
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
