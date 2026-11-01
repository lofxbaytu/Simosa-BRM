// 情境編輯器「在圖上編輯」的草稿層(第二十一輪;docs/design/instructor-plot.md 第 4 節):把草稿的航點/區域頂點/自船初始位置/
// 目標船初始位置轉成航跡圖把手(PlotHandle),並把圖上的點擊(新增)、拖曳/方向鍵(移動)、右鍵/Delete(刪除)寫回草稿
//(沿用 lib/scenario-editor.ts 的修改函式與座標寫法:該點原本是經緯度就寫經緯度、是 ENU 就寫 ENU;新點依最後一點或初始位置的寫法)。
// 另提供內嵌航跡圖所需的疊圖(草稿 → 情境物件 → 航線/區域/泊位 + 流場網格 + data/areas 示意幾何)與合成的自船狀態(初始位置/航向/流)。
// 純函式、不碰 DOM;面板在 components/instructor/scenario-editor-plot.ts。

import { defaultState } from './state-parser.js';
import type { OwnShipState } from '../types/state.js';
import { AREA_FILES } from './scenario.js';
import { parseRoute, parseZones } from './timeline.js';
import { berthGeometry, resolveScenarioBerth } from './external-forces.js';
import { overlayRoute, overlayZones, sketchFromAreas, type PlotOverlay } from './plot-overlay.js';
import { parseCurrentField } from './current-field-plot.js';
import { draftPositionFromEnu, draftPositionToEnu, enuToLatLon, insertIndexOnPolygon, insertIndexOnPolyline, pointPositionMode, type PlotHandle, type Point } from './plot-editing.js';
import { patchTarget, positionMode, routeWaypoints, toScenarioObject, uniqueId, zonePolygon, type DraftPosition, type RawSection, type ScenarioDraft } from './scenario-editor.js';

/** 圖上編輯對象:航線(點擊新增航點)、區域(點擊新增頂點)、自船/目標船初始位置(點擊設定)、流場網格預覽(只看)。 */
export type PlotEditTarget = { kind: 'route' } | { kind: 'zone'; index: number } | { kind: 'own' } | { kind: 'target'; index: number } | { kind: 'field' };

/** 對象的選單值(route、zone:<i>、own、target:<i>、field)。 */
export function plotTargetValue(t: PlotEditTarget): string {
  return t.kind === 'zone' || t.kind === 'target' ? `${t.kind}:${t.index}` : t.kind;
}

export function plotTargetFromValue(v: string): PlotEditTarget | null {
  if (v === 'route' || v === 'own' || v === 'field') return { kind: v };
  const m = /^(zone|target):(\d+)$/.exec(v);
  if (!m) return null;
  return { kind: m[1] as 'zone' | 'target', index: Number(m[2]) };
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' ? String(v) : '';
}

/** 對象的顯示名稱。 */
export function plotTargetLabel(draft: ScenarioDraft, t: PlotEditTarget): string {
  switch (t.kind) {
    case 'route':
      return `航線${draft.route ? `(${routeWaypoints(draft).length} 航點)` : '(尚無航線)'}`;
    case 'zone': {
      const z = draft.zones[t.index];
      return z ? `區域 ${str(z['id']) || `#${t.index + 1}`}${str(z['name']) ? ` ${str(z['name'])}` : ''}` : `區域 #${t.index + 1}`;
    }
    case 'own':
      return '自船初始位置';
    case 'target': {
      const tg = draft.targets[t.index];
      return tg ? `目標船 ${tg.id}${tg.name ? ` ${tg.name}` : ''}` : `目標船 #${t.index + 1}`;
    }
    case 'field':
      return '流場網格預覽';
  }
}

/** 編輯對象的操作提示(讀數列在游標離開時顯示)。 */
export function plotTargetHint(t: PlotEditTarget): string {
  switch (t.kind) {
    case 'route':
      return '點擊空白處新增航點(落在航段附近則插入該段)· 拖曳航點移動 · 右鍵/Delete 刪除 · 方向鍵微調(Shift ×10)';
    case 'zone':
      return '點擊新增頂點(插入最近的邊)· 拖曳頂點移動 · 右鍵/Delete 刪除 · 方向鍵微調(Shift ×10)';
    case 'own':
      return '點擊設定自船初始位置 · 拖曳綠點移動 · 方向鍵微調(Shift ×10)';
    case 'target':
      return '點擊設定該目標船的初始位置 · 拖曳移動 · 方向鍵微調(Shift ×10)';
    case 'field':
      return '網格節點箭頭 = u_kn/v_kn(長/色 = 流速);工具列「流」可關閉;拖曳/滾輪移動與縮放';
  }
}

/** 內嵌圖的原點:raw.origin,否則經緯度的初始位置;皆無則 null(只能畫/編 ENU 座標)。 */
export function plotOrigin(draft: ScenarioDraft): { lat: number; lon: number } | null {
  const o = draft.raw['origin'];
  if (typeof o === 'object' && o !== null && !Array.isArray(o)) {
    const r = o as Record<string, unknown>;
    if (typeof r['lat'] === 'number' && typeof r['lon'] === 'number') return { lat: r['lat'], lon: r['lon'] };
  }
  const p = draft.initial.position;
  if (typeof p.lat === 'number' && typeof p.lon === 'number') return { lat: p.lat, lon: p.lon };
  return null;
}

function positionOf(o: RawSection | undefined): DraftPosition {
  const p: DraftPosition = {};
  if (!o) return p;
  for (const k of ['lat', 'lon', 'x', 'y'] as const) {
    const v = o[k];
    if (typeof v === 'number' || (typeof v === 'string' && v.trim() !== '')) p[k] = v;
  }
  return p;
}

/** 草稿 → 把手:航線航點(有航線時一律列出)、編輯中區域的頂點、自船、全部目標船;無法換算(無 origin 的經緯度、參數引用)者略過。 */
export function plotHandles(draft: ScenarioDraft, target: PlotEditTarget, origin: { lat: number; lon: number } | null): PlotHandle[] {
  const out: PlotHandle[] = [];
  routeWaypoints(draft).forEach((w, i) => {
    const p = draftPositionToEnu(positionOf(w), origin);
    if (p) out.push({ key: `wp:${i}`, kind: 'waypoint', x: p.x, y: p.y, label: str(w['name']) || `WP${i + 1}`, removable: true });
  });
  if (target.kind === 'zone') {
    const zone = draft.zones[target.index];
    if (zone)
      zonePolygon(zone).forEach((v, j) => {
        const p = draftPositionToEnu(positionOf(v), origin);
        if (p) out.push({ key: `zone:${target.index}:${j}`, kind: 'vertex', x: p.x, y: p.y, label: `#${j + 1}`, removable: true });
      });
  }
  const own = draftPositionToEnu(draft.initial.position, origin);
  if (own) out.push({ key: 'own', kind: 'own', x: own.x, y: own.y, label: '自船', removable: false });
  draft.targets.forEach((t, i) => {
    const p = draftPositionToEnu(t.position, origin);
    if (p) out.push({ key: `target:${i}`, kind: 'target', x: p.x, y: p.y, label: t.name || t.id || `目標 ${i + 1}`, removable: false });
  });
  return out;
}

function setRouteWaypoints(draft: ScenarioDraft, waypoints: RawSection[]): ScenarioDraft {
  return { ...draft, route: { ...(draft.route ?? {}), waypoints } };
}

function setZonePolygon(draft: ScenarioDraft, index: number, polygon: RawSection[]): ScenarioDraft {
  return { ...draft, zones: draft.zones.map((z, i) => (i === index ? { ...z, polygon } : z)) };
}

function writePosition(position: DraftPosition, orig: RawSection | undefined): RawSection {
  const o: RawSection = { ...(orig ?? {}) };
  for (const k of ['lat', 'lon', 'x', 'y'] as const) {
    delete o[k];
    if (position[k] !== undefined) o[k] = position[k];
  }
  return o;
}

/**
 * 圖上點擊新增:航線 → 新航點(落在航段 ±tolerance_m 內則插入該段,否則接在末端;名稱 WPn 唯一)、區域 → 插入最近邊的頂點、
 * 自船/目標船 → 設定初始位置。回傳新草稿與新點的把手鍵(field 或對象不存在時草稿不變、key null)。
 */
export function addPlotPoint(draft: ScenarioDraft, target: PlotEditTarget, p: Point, origin: { lat: number; lon: number } | null, tolerance_m: number): { draft: ScenarioDraft; key: string | null } {
  const fallback = positionMode(draft.initial.position);
  switch (target.kind) {
    case 'route': {
      const wps = routeWaypoints(draft);
      const pts = wps.map((w) => draftPositionToEnu(positionOf(w), origin));
      const mode = pointPositionMode(positionOf(wps[wps.length - 1]), fallback, origin);
      const name = uniqueId('WP', wps.map((w) => str(w['name'])), wps.length + 1);
      const wp = writePosition(draftPositionFromEnu(p, mode, origin), { name });
      // 只有全部航點都可換算時才嘗試插入航段中間;否則接在末端
      const index = pts.every((q): q is Point => q !== null) ? insertIndexOnPolyline(pts, p, tolerance_m) : wps.length;
      const next = [...wps.slice(0, index), wp, ...wps.slice(index)];
      return { draft: setRouteWaypoints(draft, next), key: `wp:${index}` };
    }
    case 'zone': {
      const zone = draft.zones[target.index];
      if (!zone) return { draft, key: null };
      const poly = zonePolygon(zone);
      const pts = poly.map((v) => draftPositionToEnu(positionOf(v), origin));
      const mode = pointPositionMode(positionOf(poly[poly.length - 1]), fallback, origin);
      const vertex = writePosition(draftPositionFromEnu(p, mode, origin), {});
      const index = pts.every((q): q is Point => q !== null) ? insertIndexOnPolygon(pts, p) : poly.length;
      const next = [...poly.slice(0, index), vertex, ...poly.slice(index)];
      return { draft: setZonePolygon(draft, target.index, next), key: `zone:${target.index}:${index}` };
    }
    case 'own': {
      const mode = pointPositionMode(draft.initial.position, fallback, origin);
      return { draft: { ...draft, initial: { ...draft.initial, position: draftPositionFromEnu(p, mode, origin) } }, key: 'own' };
    }
    case 'target': {
      const t = draft.targets[target.index];
      if (!t) return { draft, key: null };
      const mode = pointPositionMode(t.position, fallback, origin);
      return { draft: patchTarget(draft, target.index, { position: draftPositionFromEnu(p, mode, origin) }), key: `target:${target.index}` };
    }
    case 'field':
      return { draft, key: null };
  }
}

/** 拖曳/方向鍵移動把手:依鍵寫回對應的位置(沿用該點既有座標寫法);鍵不存在時草稿不變。 */
export function movePlotHandle(draft: ScenarioDraft, key: string, p: Point, origin: { lat: number; lon: number } | null): ScenarioDraft {
  const fallback = positionMode(draft.initial.position);
  if (key === 'own') {
    const mode = pointPositionMode(draft.initial.position, fallback, origin);
    return { ...draft, initial: { ...draft.initial, position: draftPositionFromEnu(p, mode, origin) } };
  }
  let m = /^wp:(\d+)$/.exec(key);
  if (m) {
    const i = Number(m[1]);
    const wps = routeWaypoints(draft);
    const w = wps[i];
    if (!w) return draft;
    const mode = pointPositionMode(positionOf(w), fallback, origin);
    return setRouteWaypoints(
      draft,
      wps.map((x, k) => (k === i ? writePosition(draftPositionFromEnu(p, mode, origin), x) : x)),
    );
  }
  m = /^zone:(\d+):(\d+)$/.exec(key);
  if (m) {
    const zi = Number(m[1]);
    const j = Number(m[2]);
    const zone = draft.zones[zi];
    if (!zone) return draft;
    const poly = zonePolygon(zone);
    const v = poly[j];
    if (!v) return draft;
    const mode = pointPositionMode(positionOf(v), fallback, origin);
    return setZonePolygon(
      draft,
      zi,
      poly.map((x, k) => (k === j ? writePosition(draftPositionFromEnu(p, mode, origin), x) : x)),
    );
  }
  m = /^target:(\d+)$/.exec(key);
  if (m) {
    const i = Number(m[1]);
    const t = draft.targets[i];
    if (!t) return draft;
    const mode = pointPositionMode(t.position, fallback, origin);
    return patchTarget(draft, i, { position: draftPositionFromEnu(p, mode, origin) });
  }
  return draft;
}

/** 右鍵/Delete 刪除把手:只有航點與區域頂點可刪;其他鍵草稿不變。 */
export function removePlotHandle(draft: ScenarioDraft, key: string): ScenarioDraft {
  let m = /^wp:(\d+)$/.exec(key);
  if (m) {
    const i = Number(m[1]);
    const wps = routeWaypoints(draft);
    return i < wps.length ? setRouteWaypoints(draft, wps.filter((_, k) => k !== i)) : draft;
  }
  m = /^zone:(\d+):(\d+)$/.exec(key);
  if (m) {
    const zi = Number(m[1]);
    const j = Number(m[2]);
    const zone = draft.zones[zi];
    if (!zone) return draft;
    const poly = zonePolygon(zone);
    return j < poly.length ? setZonePolygon(draft, zi, poly.filter((_, k) => k !== j)) : draft;
  }
  return draft;
}

/**
 * 內嵌圖的疊圖:草稿 → 情境物件 → 航線/區域(與教官站相同的換算)、泊位(file 以建置時匯入的 data/areas 解析)、
 * 流場網格(草稿 currentModel.field,可畫時)與 origin 周圍的示意幾何(無 ENC 的底圖)。
 */
export function editorPlotOverlay(draft: ScenarioDraft, origin: { lat: number; lon: number } | null, areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): PlotOverlay {
  const sc = toScenarioObject(draft);
  const ov: PlotOverlay = { route: overlayRoute(parseRoute(sc['route']), origin), zones: overlayZones(parseZones(sc['zones']), origin), events: [] };
  const berth = resolveScenarioBerth(sc['berth'], areaFiles);
  const geo = berth?.definition ? berthGeometry(berth.definition, origin) : null;
  if (geo) ov.berth = geo;
  if (berth && berth.lines.length > 0) ov.mooringLines = berth.lines;
  const field = parseCurrentField(draft.currentModel['field']);
  if (field) ov.currentField = field;
  const sketch = sketchFromAreas(areaFiles, origin);
  if (sketch) ov.sketch = sketch;
  return ov;
}

function num(v: unknown, fallback = 0): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

/** 內嵌圖的合成自船狀態:位置/航向/速度取草稿初始值、流取環境面板的常數流(參數引用視為 0);其餘為預設。 */
export function editorPlotState(draft: ScenarioDraft, origin: { lat: number; lon: number } | null): OwnShipState {
  const base = defaultState(draft.ship.id);
  const p = draftPositionToEnu(draft.initial.position, origin) ?? { x: 0, y: 0 };
  const g = origin ? enuToLatLon(origin, p.x, p.y) : { lat: 0, lon: 0 };
  const heading = num(draft.initial.heading);
  const speed = num(draft.initial.speed);
  return {
    ...base,
    pos: { lat: g.lat, lon: g.lon, x: p.x, y: p.y },
    heading,
    cog: heading,
    stw: speed,
    sog: speed,
    current: { set: num(draft.environment.currentSet), drift: num(draft.environment.currentDrift) },
    loading: draft.ship.loading ?? 'ballast',
  };
}
