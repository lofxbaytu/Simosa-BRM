// 航跡圖點選編輯的幾何與草稿層(lib/plot-editing.ts、lib/scenario-editor-plot.ts):像素 ↔ ENU、把手命中、多邊形/折線插入位置、
// 方向鍵步距、ENU ↔ 經緯度與草稿座標寫法;草稿把手(航點/頂點/自船/目標船)、點擊新增、拖曳移動、刪除、內嵌圖疊圖與合成狀態。
import { describe, expect, it } from 'vitest';
import {
  coordinateReadout,
  draftPositionFromEnu,
  draftPositionToEnu,
  enuToLatLon,
  enuToPx,
  insertIndexOnPolygon,
  insertIndexOnPolyline,
  nearestHandle,
  nudgeStep_m,
  pointPositionMode,
  pxToEnu,
  segmentDistance,
  type PlotHandle,
  type PlotView,
} from '../src/lib/plot-editing.js';
import { latLonToEnu } from '../src/lib/timeline.js';
import { fromYaml, routeWaypoints, zonePolygon } from '../src/lib/scenario-editor.js';
import {
  addPlotPoint,
  editorPlotOverlay,
  editorPlotState,
  movePlotHandle,
  plotHandles,
  plotOrigin,
  plotTargetFromValue,
  plotTargetLabel,
  plotTargetValue,
  removePlotHandle,
} from '../src/lib/scenario-editor-plot.js';

const VIEW: PlotView = { cx: 1000, cy: -500, z: 0.5, w: 640, h: 480 };

describe('像素 ↔ ENU', () => {
  it('中心對應畫面中央,x 向右、y 向上;往返一致', () => {
    expect(enuToPx(VIEW, 1000, -500)).toEqual([320, 240]);
    expect(enuToPx(VIEW, 1100, -400)).toEqual([370, 190]);
    const p = pxToEnu(VIEW, 100, 400);
    expect(p).toEqual({ x: 1000 + (100 - 320) / 0.5, y: -500 - (400 - 240) / 0.5 });
    const [px, py] = enuToPx(VIEW, p.x, p.y);
    expect(px).toBeCloseTo(100, 9);
    expect(py).toBeCloseTo(400, 9);
  });
  it('把手命中:半徑內最近者;半徑外為 null', () => {
    const handles: PlotHandle[] = [
      { key: 'a', kind: 'waypoint', x: 1000, y: -500, label: 'A', removable: true },
      { key: 'b', kind: 'waypoint', x: 1010, y: -500, label: 'B', removable: true }, // 5 px 右
      { key: 'c', kind: 'own', x: 1200, y: -500, label: 'C', removable: false },
    ];
    expect(nearestHandle(handles, 320, 240, VIEW)?.key).toBe('a');
    expect(nearestHandle(handles, 324, 240, VIEW)?.key).toBe('b');
    expect(nearestHandle(handles, 340, 240, VIEW)).toBeNull();
    expect(nearestHandle(handles, 420, 240, VIEW)?.key).toBe('c');
    expect(nearestHandle([], 320, 240, VIEW)).toBeNull();
  });
});

describe('插入位置與步距', () => {
  it('線段距離與投影參數', () => {
    expect(segmentDistance({ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 5, y: 3 })).toEqual({ distance: 3, t: 0.5 });
    expect(segmentDistance({ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 15, y: 0 })).toEqual({ distance: 5, t: 1 });
    expect(segmentDistance({ x: 2, y: 2 }, { x: 2, y: 2 }, { x: 5, y: 6 })).toEqual({ distance: 5, t: 0 });
  });
  it('多邊形:插在最近邊的兩端之間(含閉合邊);頂點 < 3 接在末端', () => {
    const square = [
      { x: 0, y: 0 },
      { x: 100, y: 0 },
      { x: 100, y: 100 },
      { x: 0, y: 100 },
    ];
    expect(insertIndexOnPolygon(square, { x: 50, y: -5 })).toBe(1); // 底邊 (0,1)
    expect(insertIndexOnPolygon(square, { x: 105, y: 50 })).toBe(2); // 右邊 (1,2)
    expect(insertIndexOnPolygon(square, { x: -5, y: 50 })).toBe(4); // 閉合邊 (3,0) → 接在末端
    expect(insertIndexOnPolygon(square.slice(0, 2), { x: 50, y: 50 })).toBe(2);
  });
  it('折線:落在航段附近(容差內、投影在段內)插入該段,否則接在末端', () => {
    const line = [
      { x: 0, y: 0 },
      { x: 1000, y: 0 },
      { x: 1000, y: 1000 },
    ];
    expect(insertIndexOnPolyline(line, { x: 500, y: 20 }, 50)).toBe(1);
    expect(insertIndexOnPolyline(line, { x: 980, y: 500 }, 50)).toBe(2);
    expect(insertIndexOnPolyline(line, { x: 500, y: 200 }, 50)).toBe(3); // 太遠 → 末端
    expect(insertIndexOnPolyline(line, { x: 1500, y: 0 }, 50)).toBe(3); // 投影在段外 → 末端
    expect(insertIndexOnPolyline([], { x: 1, y: 1 }, 50)).toBe(0);
  });
  it('方向鍵步距約 10 px、取 1/2/5 階;Shift ×10', () => {
    expect(nudgeStep_m(1)).toBe(10);
    expect(nudgeStep_m(0.6)).toBe(20); // 16.7 m → 20
    expect(nudgeStep_m(0.02)).toBe(500);
    expect(nudgeStep_m(4)).toBe(5); // 2.5 → 5
    expect(nudgeStep_m(8)).toBe(2); // 1.25 → 2
    expect(nudgeStep_m(1, true)).toBe(100);
  });
});

describe('ENU ↔ 經緯度與草稿座標', () => {
  const origin = { lat: 23.78, lon: 120.1 };
  it('enuToLatLon 為 latLonToEnu 的反算(1 mm 內)', () => {
    const g = enuToLatLon(origin, 1234.5, -678.9);
    const back = latLonToEnu(origin, g.lat, g.lon);
    expect(back.x).toBeCloseTo(1234.5, 3);
    expect(back.y).toBeCloseTo(-678.9, 3);
  });
  it('草稿座標 → ENU:x/y 直接、經緯度需 origin、參數引用為 null', () => {
    expect(draftPositionToEnu({ x: 10, y: 20 }, null)).toEqual({ x: 10, y: 20 });
    expect(draftPositionToEnu({ lat: 23.79, lon: 120.1 }, origin)!.y).toBeCloseTo(1113.2, 6);
    expect(draftPositionToEnu({ lat: 23.79, lon: 120.1 }, null)).toBeNull();
    expect(draftPositionToEnu({ lat: '${params:a}', lon: 120 }, origin)).toBeNull();
    expect(draftPositionToEnu(undefined, origin)).toBeNull();
  });
  it('寫法:沿用既有點(lat/lon → 經緯度、x/y → ENU),新點依 fallback,經緯度無 origin 退回 ENU', () => {
    expect(pointPositionMode({ lat: 1, lon: 2 }, 'enu', origin)).toBe('latlon');
    expect(pointPositionMode({ x: 1, y: 2 }, 'latlon', origin)).toBe('enu');
    expect(pointPositionMode(undefined, 'latlon', origin)).toBe('latlon');
    expect(pointPositionMode({ lat: 1, lon: 2 }, 'latlon', null)).toBe('enu');
  });
  it('ENU → 草稿:經緯度 5 位小數、ENU 整數公尺;讀數文字含兩種座標', () => {
    const p = draftPositionFromEnu({ x: 1113.2, y: -1113.2 }, 'latlon', origin);
    expect(p.lat).toBeCloseTo(23.77, 5);
    expect(p.lon).toBeCloseTo(120.1 + 0.01 / Math.cos((23.78 * Math.PI) / 180), 5);
    expect(String(p.lat).split('.')[1]!.length).toBeLessThanOrEqual(5);
    expect(draftPositionFromEnu({ x: 12.6, y: -0.4 }, 'enu', origin)).toEqual({ x: 13, y: 0 });
    expect(draftPositionFromEnu({ x: 12.6, y: -0.4 }, 'latlon', null)).toEqual({ x: 13, y: 0 });
    expect(coordinateReadout({ x: 12.6, y: -3.4 }, null)).toBe('x 13 m  y -3 m');
    expect(coordinateReadout({ x: 0, y: 0 }, origin)).toBe('x 0 m  y 0 m · 23.78000°N 120.10000°E');
  });
});

const MINI = `id: MINI
name: 迷你
ship: { id: FSB1, loading: full }
origin: { lat: 23.78, lon: 120.1 }
initial: { position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8 }
environment:
  current: { set: 230, drift: 2.0 }
  waterDepth: 14
route:
  name: 進港
  waypoints:
    - { name: WP1, lat: 23.77, lon: 120.09 }
    - { name: WP2, lat: 23.78, lon: 120.12, checkpoint: true, speedLimit_kn: 8 }
zones:
  - id: anchorage_S
    name: 南側錨地
    type: noGo
    polygon: [{ lat: 23.785, lon: 120.125 }, { lat: 23.79, lon: 120.15 }, { lat: 23.78, lon: 120.152 }]
  - id: enuZone
    type: speedLimit
    speedLimit_kn: 6
    polygon: [{ x: 0, y: 0 }, { x: 1000, y: 0 }, { x: 1000, y: 1000 }, { x: 0, y: 1000 }]
targets:
  - id: ANCH1
    name: SEA PEARL
    initial: { position: { lat: 23.783, lon: 120.138 }, heading: 40 }
    behaviour: { mode: anchored }
`;

describe('草稿把手與圖上編輯', () => {
  const draft = fromYaml(MINI, 'MINI');
  const origin = plotOrigin(draft)!;
  it('原點取 raw.origin,否則經緯度初始位置;對象選單值往返與名稱', () => {
    expect(origin).toEqual({ lat: 23.78, lon: 120.1 });
    const noOrigin = fromYaml(MINI.replace('origin: { lat: 23.78, lon: 120.1 }\n', ''), 'X');
    expect(plotOrigin(noOrigin)).toEqual({ lat: 23.7696, lon: 120.0857 });
    expect(plotOrigin({ ...noOrigin, initial: { position: { x: 0, y: 0 } } })).toBeNull();
    for (const v of ['route', 'zone:1', 'own', 'target:0', 'field']) expect(plotTargetValue(plotTargetFromValue(v)!)).toBe(v);
    expect(plotTargetFromValue('nope')).toBeNull();
    expect(plotTargetLabel(draft, { kind: 'zone', index: 0 })).toBe('區域 anchorage_S 南側錨地');
    expect(plotTargetLabel(draft, { kind: 'target', index: 0 })).toBe('目標船 ANCH1 SEA PEARL');
    expect(plotTargetLabel(draft, { kind: 'route' })).toBe('航線(2 航點)');
  });
  it('把手:航點一律列出、只列編輯中區域的頂點、自船與目標船;ENU 頂點直接用 x/y', () => {
    const route = plotHandles(draft, { kind: 'route' }, origin);
    expect(route.map((h) => h.key)).toEqual(['wp:0', 'wp:1', 'own', 'target:0']);
    expect(route[0]).toMatchObject({ kind: 'waypoint', label: 'WP1', removable: true });
    expect(route[0]!.x).toBeCloseTo(latLonToEnu(origin, 23.77, 120.09).x, 9);
    expect(route[2]).toMatchObject({ kind: 'own', label: '自船', removable: false });
    expect(route[3]).toMatchObject({ kind: 'target', label: 'SEA PEARL', removable: false });
    const zone = plotHandles(draft, { kind: 'zone', index: 1 }, origin);
    expect(zone.filter((h) => h.kind === 'vertex').map((h) => [h.key, h.x, h.y])).toEqual([
      ['zone:1:0', 0, 0],
      ['zone:1:1', 1000, 0],
      ['zone:1:2', 1000, 1000],
      ['zone:1:3', 0, 1000],
    ]);
    // 無 origin 時經緯度的點不列,ENU 的仍列
    const noOrigin = plotHandles(draft, { kind: 'zone', index: 1 }, null);
    expect(noOrigin.map((h) => h.key)).toEqual(['zone:1:0', 'zone:1:1', 'zone:1:2', 'zone:1:3']);
  });
  it('點擊新增航點:遠離航線接在末端(經緯度寫法、名稱唯一),落在航段附近則插入該段', () => {
    const far = addPlotPoint(draft, { kind: 'route' }, { x: 5000, y: 5000 }, origin, 20);
    expect(far.key).toBe('wp:2');
    const wps = routeWaypoints(far.draft);
    expect(wps).toHaveLength(3);
    expect(wps[2]!['name']).toBe('WP3');
    expect(typeof wps[2]!['lat']).toBe('number');
    expect(wps[2]!['x']).toBeUndefined();
    const g = enuToLatLon(origin, 5000, 5000);
    expect(wps[2]!['lat']).toBeCloseTo(g.lat, 5);
    expect(wps[2]!['lon']).toBeCloseTo(g.lon, 5);
    const a = latLonToEnu(origin, 23.77, 120.09);
    const b = latLonToEnu(origin, 23.78, 120.12);
    const mid = { x: (a.x + b.x) / 2 + 5, y: (a.y + b.y) / 2 - 5 };
    const ins = addPlotPoint(draft, { kind: 'route' }, mid, origin, 50);
    expect(ins.key).toBe('wp:1');
    expect(routeWaypoints(ins.draft).map((w) => w['name'])).toEqual(['WP1', 'WP3', 'WP2']);
    expect(routeWaypoints(ins.draft)[2]).toEqual(routeWaypoints(draft)[1]); // 其他航點不動
    // 原草稿不變
    expect(routeWaypoints(draft)).toHaveLength(2);
  });
  it('點擊新增頂點:插在最近的邊,ENU 區域寫 x/y 整數', () => {
    const r = addPlotPoint(draft, { kind: 'zone', index: 1 }, { x: 500.4, y: -30 }, origin, 20);
    expect(r.key).toBe('zone:1:1');
    expect(zonePolygon(r.draft.zones[1]!)).toEqual([
      { x: 0, y: 0 },
      { x: 500, y: -30 },
      { x: 1000, y: 0 },
      { x: 1000, y: 1000 },
      { x: 0, y: 1000 },
    ]);
    expect(r.draft.zones[0]).toBe(draft.zones[0]);
    expect(addPlotPoint(draft, { kind: 'zone', index: 9 }, { x: 0, y: 0 }, origin, 20)).toEqual({ draft, key: null });
    expect(addPlotPoint(draft, { kind: 'field' }, { x: 0, y: 0 }, origin, 20)).toEqual({ draft, key: null });
  });
  it('點擊設定自船/目標船初始位置(沿用經緯度寫法;其他欄位保留)', () => {
    const own = addPlotPoint(draft, { kind: 'own' }, { x: 0, y: 1113.2 }, origin, 20);
    expect(own.key).toBe('own');
    expect(own.draft.initial.position.lat).toBeCloseTo(23.79, 5);
    expect(own.draft.initial.position.lon).toBeCloseTo(120.1, 5);
    expect(own.draft.initial.heading).toBe(52);
    const tg = addPlotPoint(draft, { kind: 'target', index: 0 }, { x: 100, y: 100 }, origin, 20);
    expect(tg.key).toBe('target:0');
    expect(typeof tg.draft.targets[0]!.position.lat).toBe('number');
    expect(tg.draft.targets[0]!.heading).toBe(40);
    expect(tg.draft.targets[0]!.raw['behaviour']).toEqual({ mode: 'anchored' });
    expect(addPlotPoint(draft, { kind: 'target', index: 5 }, { x: 0, y: 0 }, origin, 20).key).toBeNull();
  });
  it('拖曳移動:航點保留其他鍵、頂點、自船、目標船;未知鍵不變', () => {
    const moved = movePlotHandle(draft, 'wp:1', { x: 0, y: 0 }, origin);
    const wp = routeWaypoints(moved)[1]!;
    expect(wp['lat']).toBe(23.78);
    expect(wp['lon']).toBe(120.1);
    expect(wp).toMatchObject({ name: 'WP2', checkpoint: true, speedLimit_kn: 8 });
    const v = movePlotHandle(draft, 'zone:1:2', { x: 1200.6, y: 900.2 }, origin);
    expect(zonePolygon(v.zones[1]!)[2]).toEqual({ x: 1201, y: 900 });
    const own = movePlotHandle(draft, 'own', { x: 0, y: 0 }, origin);
    expect(own.initial.position).toEqual({ lat: 23.78, lon: 120.1 });
    const tg = movePlotHandle(draft, 'target:0', { x: 0, y: 0 }, origin);
    expect(tg.targets[0]!.position).toEqual({ lat: 23.78, lon: 120.1 });
    expect(movePlotHandle(draft, 'wp:7', { x: 0, y: 0 }, origin)).toBe(draft);
    expect(movePlotHandle(draft, 'zone:1:9', { x: 0, y: 0 }, origin)).toBe(draft);
    expect(movePlotHandle(draft, 'bogus', { x: 0, y: 0 }, origin)).toBe(draft);
  });
  it('刪除:航點與頂點可刪;自船/目標船/不存在者不變', () => {
    expect(routeWaypoints(removePlotHandle(draft, 'wp:0')).map((w) => w['name'])).toEqual(['WP2']);
    expect(zonePolygon(removePlotHandle(draft, 'zone:1:0').zones[1]!)).toHaveLength(3);
    expect(removePlotHandle(draft, 'own')).toBe(draft);
    expect(removePlotHandle(draft, 'target:0')).toBe(draft);
    expect(removePlotHandle(draft, 'wp:5')).toBe(draft);
    expect(removePlotHandle(draft, 'zone:3:0')).toBe(draft);
  });
  it('內嵌圖疊圖:航線/區域/示意幾何/流場網格;合成狀態取初始位置、航向、常數流', () => {
    const areas = { '/data/areas/t.json': { land: [{ name: '岸', points: [[23.79, 120.11], [23.79, 120.12], [23.8, 120.12]] }] } };
    const ov = editorPlotOverlay(draft, origin, areas);
    expect(ov.route?.waypoints.map((w) => w.name)).toEqual(['WP1', 'WP2']);
    expect(ov.zones.map((z) => z.id)).toEqual(['anchorage_S', 'enuZone']);
    expect(ov.sketch?.shapes).toHaveLength(1);
    expect(ov.currentField).toBeUndefined();
    const withField = {
      ...draft,
      currentModel: { field: { grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }, u_kn: [[1, 1], [1, 1]], v_kn: [[0, 0], [0, 0]] } },
    };
    expect(editorPlotOverlay(withField, origin, {}).currentField?.grid.nx).toBe(2);
    expect(editorPlotOverlay(withField, origin, {}).sketch).toBeUndefined();
    const s = editorPlotState(draft, origin);
    const p = latLonToEnu(origin, 23.7696, 120.0857);
    expect(s.pos.x).toBeCloseTo(p.x, 6);
    expect(s.pos.lat).toBeCloseTo(23.7696, 9);
    expect(s).toMatchObject({ shipId: 'FSB1', heading: 52, cog: 52, stw: 8, sog: 8, current: { set: 230, drift: 2 }, loading: 'full' });
    expect(editorPlotState({ ...draft, environment: { currentSet: '${params:x}' } }, origin).current).toEqual({ set: 0, drift: 0 });
  });
});
