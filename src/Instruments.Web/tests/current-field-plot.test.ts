// 流場疊圖幾何(lib/current-field-plot.ts):field 解析、雙線性內插(與設計文件 1.5.2.1 的範例一致)、格外 edge/zero、
// 可見範圍取樣與 40 × 40 上限、箭頭長度/顏色比例、由 state 推得的 timeScale 比例 k(t)、均勻流/兩端線性內插產生器;
// 以及 plot-overlay / scenario 摘要帶出 currentField、示意幾何 sketchFromAreas。
import { describe, expect, it } from 'vitest';
import {
  FIELD_MAX_SAMPLES_PER_AXIS,
  arrowLengthPx,
  currentFieldOfScenario,
  fieldBounds,
  fieldMaxSpeed,
  fieldScaleFromState,
  fieldVectorAt,
  linearFieldArrays,
  parseCurrentField,
  sampleFieldArrows,
  setDriftToVector,
  speedColor,
  uniformFieldArrays,
  vectorToSetDrift,
  type CurrentFieldSpec,
} from '../src/lib/current-field-plot.js';
import { overlayFromHeaderScenario, overlayFromScenario, overlayHasContent, sketchFromAreas } from '../src/lib/plot-overlay.js';
import { parseScenario } from '../src/lib/scenario.js';

/** 設計文件 1.5.2.1 的範例網格(u 依 x 每格 +0.5、依 y 每列 +0.2;v 全 1)。 */
const DOC_FIELD = {
  grid: { x0: -200, y0: -100, dx: 100, dy: 100, nx: 5, ny: 4 },
  u_kn: [
    [0.0, 0.5, 1.0, 1.5, 2.0],
    [0.2, 0.7, 1.2, 1.7, 2.2],
    [0.4, 0.9, 1.4, 1.9, 2.4],
    [0.6, 1.1, 1.6, 2.1, 2.6],
  ],
  v_kn: Array.from({ length: 4 }, () => [1, 1, 1, 1, 1]),
};

function field(extra: Record<string, unknown> = {}): CurrentFieldSpec {
  const f = parseCurrentField({ ...DOC_FIELD, ...extra });
  if (!f) throw new Error('範例網格應可解析');
  return f;
}

describe('parseCurrentField', () => {
  it('合法網格解析出 grid/u/v/outside/timeScale(預設 edge/none)', () => {
    const f = field();
    expect(f.grid).toEqual({ x0: -200, y0: -100, dx: 100, dy: 100, nx: 5, ny: 4 });
    expect(f.outside).toBe('edge');
    expect(f.timeScale).toBe('none');
    expect(field({ outside: 'ZERO', timeScale: 'Tidal' })).toMatchObject({ outside: 'zero', timeScale: 'tidal' });
  });
  it('結構不合(格數、格距、陣列尺寸、非數值)回傳 null', () => {
    expect(parseCurrentField(null)).toBeNull();
    expect(parseCurrentField({ ...DOC_FIELD, grid: { ...DOC_FIELD.grid, nx: 1 } })).toBeNull();
    expect(parseCurrentField({ ...DOC_FIELD, grid: { ...DOC_FIELD.grid, dx: 0 } })).toBeNull();
    expect(parseCurrentField({ ...DOC_FIELD, grid: { ...DOC_FIELD.grid, nx: 2.5 } })).toBeNull();
    expect(parseCurrentField({ ...DOC_FIELD, u_kn: DOC_FIELD.u_kn.slice(0, 3) })).toBeNull();
    expect(parseCurrentField({ ...DOC_FIELD, v_kn: DOC_FIELD.v_kn.map((r) => r.slice(0, 4)) })).toBeNull();
    expect(parseCurrentField({ ...DOC_FIELD, u_kn: DOC_FIELD.u_kn.map((r, j) => (j === 0 ? ['x', ...r.slice(1)] : r)) })).toBeNull();
    expect(currentFieldOfScenario({ environment: { current: { set: 1 } } })).toBeNull();
    expect(currentFieldOfScenario({ environment: { current: { field: DOC_FIELD } } })).not.toBeNull();
  });
});

describe('fieldVectorAt(雙線性內插)', () => {
  it('節點取原值、格內線性、格外 edge 夾邊緣、zero 取 0', () => {
    const f = field();
    expect(fieldVectorAt(f, -200, -100)).toEqual({ u_kn: 0, v_kn: 1 });
    expect(fieldVectorAt(f, 200, 200)).toEqual({ u_kn: 2.6, v_kn: 1 });
    // 格中心 (−150, −50):四角 0/0.5/0.2/0.7 的平均 0.35
    expect(fieldVectorAt(f, -150, -50).u_kn).toBeCloseTo(0.35, 9);
    // 沿 x 線性:(−100 + 25, −100) → 0.5 + 0.125
    expect(fieldVectorAt(f, -75, -100).u_kn).toBeCloseTo(0.625, 9);
    // edge:超出範圍夾到邊緣
    expect(fieldVectorAt(f, 1000, 1000)).toEqual({ u_kn: 2.6, v_kn: 1 });
    expect(fieldVectorAt(f, -1000, 50).u_kn).toBeCloseTo(0.3, 9);
    // zero
    const z = field({ outside: 'zero' });
    expect(fieldVectorAt(z, 1000, 0)).toEqual({ u_kn: 0, v_kn: 0 });
    expect(fieldVectorAt(z, 0, 0)).toEqual(fieldVectorAt(f, 0, 0));
  });
  it('向量 ↔ 流向/流速', () => {
    expect(vectorToSetDrift(1, 0)).toEqual({ set: 90, drift: 1 });
    expect(vectorToSetDrift(0, -2).set).toBe(180);
    expect(vectorToSetDrift(0, 0)).toEqual({ set: 0, drift: 0 });
    const v = setDriftToVector(45, Math.SQRT2);
    expect(v.u_kn).toBeCloseTo(1, 9);
    expect(v.v_kn).toBeCloseTo(1, 9);
  });
  it('最大流速與外框', () => {
    const f = field();
    expect(fieldMaxSpeed(f)).toBeCloseTo(Math.hypot(2.6, 1), 9);
    expect(fieldBounds(f.grid)).toEqual({ minX: -200, minY: -100, maxX: 200, maxY: 200 });
  });
});

describe('sampleFieldArrows(可見範圍取樣、≤ 40 × 40)', () => {
  it('小網格全取;視窗外的節點略過(外擴一格仍保留)', () => {
    const f = field();
    const all = sampleFieldArrows(f, null);
    expect(all.arrows).toHaveLength(20);
    expect(all).toMatchObject({ strideX: 1, strideY: 1, cell_m: 100 });
    expect(all.arrows[0]).toEqual({ i: 0, j: 0, x: -200, y: -100, u_kn: 0, v_kn: 1, speed_kn: 1 });
    // 視窗只含 x ∈ [−50, 50]、y ∈ [−50, 50]:外擴一格後 x ∈ [−150, 150] → i = 1..3,y ∈ [−150, 150] → j = 0..2
    const part = sampleFieldArrows(f, { cx: 0, cy: 0, halfWidth_m: 50, halfHeight_m: 50 });
    expect(part.arrows.map((a) => `${a.i},${a.j}`)).toEqual(['1,0', '2,0', '3,0', '1,1', '2,1', '3,1', '1,2', '2,2', '3,2']);
    expect(sampleFieldArrows(f, { cx: 10_000, cy: 10_000, halfWidth_m: 10, halfHeight_m: 10 }).arrows).toEqual([]);
  });
  it('大網格等距抽樣,兩軸各 ≤ 40 支', () => {
    const nx = 130;
    const ny = 95;
    const rows = () => Array.from({ length: ny }, () => Array.from({ length: nx }, () => 1));
    const big = parseCurrentField({ grid: { x0: 0, y0: 0, dx: 10, dy: 10, nx, ny }, u_kn: rows(), v_kn: rows() })!;
    const s = sampleFieldArrows(big, null);
    expect(s.strideX).toBe(4);
    expect(s.strideY).toBe(3);
    const xs = new Set(s.arrows.map((a) => a.i));
    const ys = new Set(s.arrows.map((a) => a.j));
    expect(xs.size).toBeLessThanOrEqual(FIELD_MAX_SAMPLES_PER_AXIS);
    expect(ys.size).toBeLessThanOrEqual(FIELD_MAX_SAMPLES_PER_AXIS);
    expect(s.arrows.length).toBe(xs.size * ys.size);
    expect(s.cell_m).toBe(30);
    expect([...xs].every((i) => i % 4 === 0)).toBe(true);
  });
});

describe('箭頭長度與顏色', () => {
  it('長度與流速成正比、格距 0.8 倍為滿長、上限 60 px、零流速為 0', () => {
    expect(arrowLengthPx(2, 2, 50)).toBeCloseTo(40, 9);
    expect(arrowLengthPx(1, 2, 50)).toBeCloseTo(20, 9);
    expect(arrowLengthPx(2, 2, 500)).toBe(60);
    expect(arrowLengthPx(0, 2, 50)).toBe(0);
    expect(arrowLengthPx(1, 0, 50)).toBe(0);
  });
  it('顏色色相由 200°(低速)遞減到 0°(高速);最大為 0 時取灰', () => {
    expect(speedColor(0, 2)).toBe('hsl(200 80% 55%)');
    expect(speedColor(1, 2)).toBe('hsl(100 80% 55%)');
    expect(speedColor(2, 2)).toBe('hsl(0 80% 55%)');
    expect(speedColor(5, 2)).toBe('hsl(0 80% 55%)');
    expect(speedColor(1, 0)).toBe('hsl(0 0% 60%)');
    expect(speedColor(1, 2, 0.5)).toBe('hsl(100 80% 55% / 0.5)');
  });
});

describe('fieldScaleFromState(timeScale 當前比例)', () => {
  it('無 state 視為預覽:作用中、k = 1;currentSource 非 field 時未作用', () => {
    const f = field({ timeScale: 'tidal' });
    expect(fieldScaleFromState(f, null)).toEqual({ active: true, k: 1, derived: false });
    expect(fieldScaleFromState(f, { pos: { x: 0, y: 0 }, current: { set: 0, drift: 1 } })).toEqual({ active: false, k: 1, derived: false });
    expect(fieldScaleFromState(f, { pos: { x: 0, y: 0 }, current: { set: 0, drift: 1 }, currentSource: 'tidal' }).active).toBe(false);
  });
  it('timeScale none 時 k = 1;tidal/table 時 k = |state.current| / |field(自船位置)| 夾到 [0, 1];場在自船位置近零時取預設 1', () => {
    const none = field();
    expect(fieldScaleFromState(none, { pos: { x: 0, y: 0 }, current: { set: 0, drift: 0.3 }, currentSource: 'field' })).toEqual({ active: true, k: 1, derived: false });
    const tidal = field({ timeScale: 'tidal' });
    // 自船在 (0, 0):u = 1.2、v = 1 → |field| = 1.5620…;state.current 0.781 → k ≈ 0.5
    const base = Math.hypot(1.2, 1);
    const half = fieldScaleFromState(tidal, { pos: { x: 0, y: 0 }, current: { set: 50, drift: base / 2 }, currentSource: 'field+tidal' });
    expect(half.active).toBe(true);
    expect(half.derived).toBe(true);
    expect(half.k).toBeCloseTo(0.5, 9);
    expect(fieldScaleFromState(tidal, { pos: { x: 0, y: 0 }, current: { set: 50, drift: base * 3 }, currentSource: 'field+tidal' }).k).toBe(1);
    expect(fieldScaleFromState(tidal, { pos: { x: 0, y: 0 }, current: { set: 50, drift: 0 }, currentSource: 'field+tidal' }).k).toBe(0);
    const zeroField = parseCurrentField({ grid: { x0: 0, y0: 0, dx: 10, dy: 10, nx: 2, ny: 2 }, u_kn: [[0, 0], [0, 0]], v_kn: [[0, 0], [0, 0]], timeScale: 'table' })!;
    expect(fieldScaleFromState(zeroField, { pos: { x: 5, y: 5 }, current: { set: 0, drift: 1 }, currentSource: 'field+table' })).toEqual({ active: true, k: 1, derived: false });
  });
});

describe('網格產生器', () => {
  it('均勻流:全部節點 (u, v) = drift·(sin set, cos set),ny 列 × nx 行', () => {
    const g = uniformFieldArrays(3, 2, 90, 2);
    expect(g.u_kn).toEqual([
      [2, 2, 2],
      [2, 2, 2],
    ]);
    expect(g.v_kn).toEqual([
      [0, 0, 0],
      [0, 0, 0],
    ]);
    const n = uniformFieldArrays(2, 2, 0, 1.5);
    expect(n.u_kn[0]![0]).toBe(0);
    expect(n.v_kn[1]![1]).toBe(1.5);
    expect(Object.is(uniformFieldArrays(2, 2, 180, 1).u_kn[0]![0], -0)).toBe(false);
  });
  it('兩端線性內插:沿 x 時第 0 行 = A、最後一行 = B、中間為向量平均;沿 y 時依列;方向跨 360° 以向量內插', () => {
    const x = linearFieldArrays(3, 2, 'x', { set: 90, drift: 1 }, { set: 90, drift: 3 });
    expect(x.u_kn).toEqual([
      [1, 2, 3],
      [1, 2, 3],
    ]);
    expect(x.v_kn).toEqual([
      [0, 0, 0],
      [0, 0, 0],
    ]);
    const y = linearFieldArrays(2, 3, 'y', { set: 0, drift: 2 }, { set: 180, drift: 2 });
    expect(y.v_kn).toEqual([
      [2, 2],
      [0, 0],
      [-2, -2],
    ]);
    // 350° → 10°:向量內插的中點指向正北(不繞 180°)
    const wrap = linearFieldArrays(3, 1, 'x', { set: 350, drift: 1 }, { set: 10, drift: 1 });
    expect(wrap.u_kn[0]![1]).toBe(0);
    expect(wrap.v_kn[0]![1]).toBeCloseTo(Math.cos((10 * Math.PI) / 180), 3);
  });
});

describe('疊圖與情境摘要帶出流場網格', () => {
  const YAML = `id: FIELD_TEST
name: 流場測試
ship: { id: FSB1, loading: full }
origin: { lat: 23.78, lon: 120.1 }
initial: { position: { lat: 23.78, lon: 120.1 }, heading: 0, speed: 5 }
environment:
  current:
    set: 85
    drift: 2.5
    tidal: { flood: { speed_kn: 2.5, direction_deg: 85 }, ebb: { speed_kn: 3, direction_deg: 265 } }
    field:
      grid: { x0: -200, y0: -100, dx: 100, dy: 100, nx: 5, ny: 4 }
      u_kn: [[0, 0.5, 1, 1.5, 2], [0.2, 0.7, 1.2, 1.7, 2.2], [0.4, 0.9, 1.4, 1.9, 2.4], [0.6, 1.1, 1.6, 2.1, 2.6]]
      v_kn: [[1, 1, 1, 1, 1], [1, 1, 1, 1, 1], [1, 1, 1, 1, 1], [1, 1, 1, 1, 1]]
      timeScale: tidal
  waterDepth: 14
`;
  it('parseScenario 摘要 currentField 可畫(environment.current 不變),overlayFromScenario / overlayFromHeaderScenario 帶 currentField,且 overlayHasContent 為真', () => {
    const s = parseScenario(YAML, 'data/scenarios/FIELD_TEST.yaml');
    expect(s.environment.current).toEqual({ set: 85, drift: 2.5, source: 'field+tidal' });
    expect(s.currentField?.grid).toEqual({ x0: -200, y0: -100, dx: 100, dy: 100, nx: 5, ny: 4 });
    expect(s.currentField?.timeScale).toBe('tidal');
    const live = overlayFromScenario(s);
    expect(live.currentField?.grid.nx).toBe(5);
    expect(overlayHasContent(live)).toBe(true);
    const replay = overlayFromHeaderScenario({ id: 'FIELD_TEST', origin: { lat: 23.78, lon: 120.1 }, environment: { current: { field: DOC_FIELD } } });
    expect(replay.currentField?.u_kn[3]![4]).toBe(2.6);
    expect(overlayFromHeaderScenario({ id: 'x', environment: { current: { set: 1 } } }).currentField).toBeUndefined();
    // 結構不合的 field 不帶(摘要仍標 source)
    const bad = parseScenario(YAML.replace('nx: 5', 'nx: 1'), 'data/scenarios/BAD.yaml');
    expect(bad.environment.current?.source).toBe('field+tidal');
    expect(bad.currentField).toBeUndefined();
    expect(overlayFromScenario(bad).currentField).toBeUndefined();
  });
  it('sketchFromAreas:收集原點周圍的海岸線/防波堤/淺灘/錨地/速限區/岸壁並換算 ENU;範圍外的檔案略過;無 origin 為 null', () => {
    const origin = { lat: 23.78, lon: 120.1 };
    const areas = {
      '/data/areas/near.json': {
        land: [{ name: '海岸(示意)', points: [[23.79, 120.11], [23.79, 120.12], [23.8, 120.12]] }],
        breakwaters: [{ id: 'bw', name: '北堤', polygon: [{ lat: 23.781, lon: 120.101 }, { lat: 23.782, lon: 120.102 }, { lat: 23.783, lon: 120.101 }] }],
        shoals: [{ id: 'sh', polygon: [{ x: 100, y: 100 }, { x: 200, y: 100 }, { x: 200, y: 200 }] }],
        anchorages: [{ id: 'A', polygon: [{ lat: 23.77, lon: 120.09 }, { lat: 23.77, lon: 120.095 }, { lat: 23.775, lon: 120.095 }] }],
        speedLimitZones: [{ id: 'S', polygon: [{ lat: 23.785, lon: 120.105 }, { lat: 23.785, lon: 120.106 }, { lat: 23.786, lon: 120.106 }] }],
        banks: [{ id: 'B', line: [{ lat: 23.78, lon: 120.1 }, { lat: 23.79, lon: 120.13 }] }],
        route: [{ name: 'WP1', lat: 23.78, lon: 120.1 }],
      },
      '/data/areas/far.json': { land: [{ name: '遠方', points: [[22.5, 120.3], [22.6, 120.3], [22.6, 120.4]] }] },
      '/data/areas/other.json': { berths: [{ id: 'b1', quay: [{ lat: 23.78, lon: 120.1 }] }] },
    };
    const sk = sketchFromAreas(areas, origin);
    expect(sk).not.toBeNull();
    expect(sk!.files).toEqual(['near.json']);
    expect(sk!.shapes.map((s) => s.kind)).toEqual(['land', 'breakwater', 'shoal', 'anchorage', 'speedLimit', 'bank']);
    expect(sk!.shapes[0]!.name).toBe('海岸(示意)');
    expect(sk!.shapes[0]!.points[0]!.y).toBeCloseTo(0.01 * 111_320, 3);
    expect(sk!.shapes[2]!.points).toEqual([{ x: 100, y: 100 }, { x: 200, y: 100 }, { x: 200, y: 200 }]);
    expect(sk!.shapes[5]!.points[0]).toEqual({ x: 0, y: 0 });
    expect(sketchFromAreas(areas, null)).toBeNull();
    expect(sketchFromAreas({ '/x.json': { land: [] } }, origin)).toBeNull();
    // 另一個原點:far.json 的海岸在範圍內;near.json 只剩 ENU 寫法的淺灘(x/y 不依原點,一律收錄)
    const far = sketchFromAreas(areas, { lat: 22.55, lon: 120.35 }, 10_000)!; // 角點距原點約 7.5 km
    expect(far.files).toEqual(['far.json', 'near.json']);
    expect(far.shapes.map((s) => s.kind)).toEqual(['land', 'shoal']);
  });
});
