// 安全等深線(lib/ecdis/safety-depth):點在多邊形內(含邊界)、多區重疊取最淺、UKC 下限來源(公尺/比例/參數未解析/未給)、
// S-52 四階分類、前方 6 分鐘掃描(命中距離/時間、SOG 0 時只看自船位置、潮高抬升後不命中)。
import { describe, expect, it } from 'vitest';
import { chartDepthAt, classifyDepth, effectiveDepth, pointInPolygon, safetyContours, scanAhead, ukcMinimum, type DepthModel } from '../src/lib/ecdis/safety-depth.js';

const square = (cx: number, cy: number, half: number) => [
  { x: cx - half, y: cy - half },
  { x: cx + half, y: cy - half },
  { x: cx + half, y: cy + half },
  { x: cx - half, y: cy + half },
];

describe('安全等深線', () => {
  it('pointInPolygon:內/外/邊界', () => {
    const poly = square(0, 0, 100);
    expect(pointInPolygon({ x: 0, y: 0 }, poly)).toBe(true);
    expect(pointInPolygon({ x: 150, y: 0 }, poly)).toBe(false);
    expect(pointInPolygon({ x: 100, y: 50 }, poly)).toBe(true);
    expect(pointInPolygon({ x: 0, y: 0 }, poly.slice(0, 2))).toBe(false);
  });

  it('chartDepthAt:多區重疊取最淺、無區用基底', () => {
    const model: DepthModel = {
      baseDepth_m: 30,
      areas: [
        { id: 'a', name: '航道', kind: 'channel', depth_m: 12.5, polygon: square(0, 0, 1000) },
        { id: 'b', name: '淺灘', kind: 'shoal', depth_m: 4, polygon: square(500, 0, 200) },
        { id: 'c', name: '深', kind: 'zone', depth_m: 40, polygon: square(-500, 0, 100) },
      ],
    };
    expect(chartDepthAt(model, { x: 0, y: 0 })).toMatchObject({ depth_m: 12.5, area: { id: 'a' } });
    expect(chartDepthAt(model, { x: 500, y: 0 })).toMatchObject({ depth_m: 4, area: { id: 'b' } });
    expect(chartDepthAt(model, { x: -500, y: 0 }).depth_m).toBe(12.5); // 比基底深的區不會加深周圍
    expect(chartDepthAt(model, { x: 5000, y: 0 })).toEqual({ depth_m: 30, area: null });
    expect(effectiveDepth(12.5, 0.8)).toBeCloseTo(13.3);
    expect(effectiveDepth(12.5, undefined)).toBe(12.5);
  });

  it('ukcMinimum:公尺值、吃水比例取較大者;參數未解析/未給用公司最小值 1.0 m', () => {
    expect(ukcMinimum({ ukc: { min: { metres: { value: 1.5 } } } }, 7.2)).toMatchObject({ ukc_m: 1.5, source: 'scenario' });
    expect(ukcMinimum({ ukc: { min: { fractionOfDraft: { value: 0.2 } } } }, 7.2)).toMatchObject({ ukc_m: 1.44, source: 'scenario' });
    expect(ukcMinimum({ ukc: { min: { metres: { value: 1.0 }, fractionOfDraft: { value: 0.2 } } } }, 7.2).ukc_m).toBe(1.44);
    const unresolved = ukcMinimum({ ukc: { min: { fractionOfDraft: { param: 'navigation.ukc.channel_fraction' } } } }, 7.2);
    expect(unresolved).toMatchObject({ ukc_m: 1.0, source: 'company' });
    expect(unresolved.note).toContain('參數未解析');
    expect(ukcMinimum(null, 7.2)).toMatchObject({ ukc_m: 1.0, source: 'default' });
    expect(ukcMinimum({}, 5.7, 1.2).ukc_m).toBe(1.2);
  });

  it('safetyContours 與 classifyDepth:FSB2 滿載 7.2 m + 1.0 m → 8.2 / 16.4', () => {
    const c = safetyContours(7.2, 1.0);
    expect(c).toEqual({ shallow_m: 7.2, safety_m: 8.2, deep_m: 16.4 });
    expect(classifyDepth(5, c)).toBe('veryShallow');
    expect(classifyDepth(7.2, c)).toBe('shallow');
    expect(classifyDepth(8.19, c)).toBe('shallow');
    expect(classifyDepth(8.2, c)).toBe('medium');
    expect(classifyDepth(16.4, c)).toBe('deep');
  });

  it('scanAhead:10 kn 向北、淺灘在 1000 m 前 → 命中約 1000 m / 194 s;SOG 0 不命中;潮高抬升後不命中', () => {
    const model: DepthModel = { baseDepth_m: 30, areas: [{ id: 's', name: '淺灘', kind: 'shoal', depth_m: 6, polygon: square(0, 1500, 500) }] };
    const hit = scanAhead(model, 0, { x: 0, y: 0 }, 0, 10, 8.2);
    expect(hit.hit).toBe(true);
    expect(hit.distance_m).toBeGreaterThanOrEqual(1000);
    expect(hit.distance_m).toBeLessThan(1030);
    expect(hit.time_s).toBeCloseTo(hit.distance_m! / (10 * 1852 / 3600), 3);
    expect(hit.depth_m).toBe(6);
    expect(hit.area?.name).toBe('淺灘');
    // 6 分鐘只走 1852 m:淺灘遠在 3000 m 外時不命中
    expect(scanAhead(model, 0, { x: 0, y: -2000 }, 0, 10, 8.2).hit).toBe(false);
    // 向南(背離)不命中
    expect(scanAhead(model, 0, { x: 0, y: 0 }, 180, 10, 8.2).hit).toBe(false);
    // SOG 0:只看自船位置
    expect(scanAhead(model, 0, { x: 0, y: 0 }, 0, 0, 8.2).hit).toBe(false);
    expect(scanAhead(model, 0, { x: 0, y: 1500 }, 0, 0, 8.2)).toMatchObject({ hit: true, distance_m: 0, time_s: 0 });
    // 潮高 +2.5 m → 有效 8.5 m ≥ 8.2 不命中
    expect(scanAhead(model, 2.5, { x: 0, y: 0 }, 0, 10, 8.2).hit).toBe(false);
  });
});
