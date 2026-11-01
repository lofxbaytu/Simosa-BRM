// 航線監控(lib/ecdis/route-monitor):XTD 右正左負、沿航段投影、抵達切換(抵達半徑/越過航點垂線)、連跳多航點、終點 complete、
// 以 SOG 的 TTG、預定航向/下一轉向、走廊半寬、手動切換航段、TTG 格式。
import { describe, expect, it } from 'vitest';
import { RouteMonitor, alongTrack, arrivedAt, crossTrackDistance, formatTtg, legXtdLimit } from '../src/lib/ecdis/route-monitor.js';
import type { OverlayRoute } from '../src/lib/plot-overlay.js';

// 北行 2000 m,再東行 2000 m,再北行 1000 m
const route: OverlayRoute = {
  name: '測試航線',
  waypoints: [
    { name: 'WP1', x: 0, y: 0, checkpoint: false, report: false },
    { name: 'WP2', x: 0, y: 2000, checkpoint: true, report: false, xtdLimit_m: 100 },
    { name: 'WP3', x: 2000, y: 2000, checkpoint: false, report: true, xtdLimit_m: 150 },
    { name: 'END', x: 2000, y: 3000, checkpoint: false, report: false, xtdLimit_m: 150, arrivalRadius_m: 300 },
  ],
};

describe('航線監控', () => {
  it('XTD:北行航段右側(東)為正、左側為負;沿航段投影', () => {
    const a = { x: 0, y: 0 };
    const b = { x: 0, y: 2000 };
    expect(crossTrackDistance(a, b, { x: 50, y: 1000 })).toBeCloseTo(50);
    expect(crossTrackDistance(a, b, { x: -80, y: 500 })).toBeCloseTo(-80);
    // 東行航段:右側為南
    expect(crossTrackDistance({ x: 0, y: 0 }, { x: 1000, y: 0 }, { x: 500, y: -30 })).toBeCloseTo(30);
    expect(alongTrack(a, b, { x: 50, y: 1000 })).toBeCloseTo(1000);
    expect(alongTrack(a, b, { x: 0, y: -100 })).toBeCloseTo(-100);
  });

  it('arrivedAt:抵達半徑內或越過航點垂線', () => {
    const wp = route.waypoints[1]!;
    expect(arrivedAt(route.waypoints[0]!, wp, { x: 0, y: 1700 })).toBe(false);
    expect(arrivedAt(route.waypoints[0]!, wp, { x: 100, y: 1900 })).toBe(true); // 半徑 185 m
    expect(arrivedAt(route.waypoints[0]!, wp, { x: 400, y: 2050 })).toBe(true); // 越過垂線
    const end = route.waypoints[3]!;
    expect(arrivedAt(route.waypoints[2]!, end, { x: 2250, y: 2850 })).toBe(true); // 自訂半徑 300
  });

  it('數值:第一航段 10 kn,DTG/TTG/預定航向/下一轉向/走廊', () => {
    const m = new RouteMonitor(route);
    const s = m.update({ x: 40, y: 500 }, 10)!;
    expect(s.legIndex).toBe(0);
    expect(s.legCount).toBe(3);
    expect(s.nextWaypoint.name).toBe('WP2');
    expect(s.xtd_m).toBeCloseTo(40);
    expect(s.xtdLimit_m).toBe(100);
    expect(s.xtdExceeded).toBe(false);
    expect(s.dtg_m).toBeCloseTo(Math.hypot(40, 1500));
    expect(s.dtgTotal_m).toBeCloseTo(Math.hypot(40, 1500) + 2000 + 1000);
    expect(s.ttg_s).toBeCloseTo(Math.hypot(40, 1500) / (10 * 1852 / 3600), 6);
    expect(s.plannedCourse_deg).toBe(0);
    expect(s.nextCourse_deg).toBe(90);
    expect(s.turn_deg).toBe(90);
    expect(s.complete).toBe(false);
    expect(legXtdLimit(route, 0)).toBe(100);
    expect(legXtdLimit(route, 1)).toBe(150);
    // SOG 0 → TTG null;XTD 超限
    const slow = m.update({ x: -120, y: 600 }, 0)!;
    expect(slow.ttg_s).toBeNull();
    expect(slow.xtdExceeded).toBe(true);
    expect(slow.xtd_m).toBeCloseTo(-120);
  });

  it('抵達 WP2 自動切到第二航段;連續越過多個航點時連跳;抵達終點 complete', () => {
    const m = new RouteMonitor(route);
    m.update({ x: 0, y: 1000 }, 10);
    const s = m.update({ x: 30, y: 2010 }, 10)!;
    expect(s.legIndex).toBe(1);
    expect(s.nextWaypoint.name).toBe('WP3');
    expect(s.plannedCourse_deg).toBe(90);
    expect(s.xtd_m).toBeCloseTo(-10); // 東行航段,北側(左)為負
    // 直接跳到終點附近:連跳
    const end = m.update({ x: 2000, y: 2900 }, 5)!;
    expect(end.complete).toBe(true);
    expect(end.legIndex).toBe(2);
    expect(end.nextWaypoint.name).toBe('END');
    expect(end.turn_deg).toBeNull();
    // 手動切回第一航段
    m.setLeg(0);
    expect(m.complete).toBe(false);
    expect(m.update({ x: 0, y: 500 }, 10)!.legIndex).toBe(0);
  });

  it('航點少於 2 → null;formatTtg', () => {
    expect(new RouteMonitor({ name: 'x', waypoints: [route.waypoints[0]!] }).update({ x: 0, y: 0 }, 5)).toBeNull();
    expect(formatTtg(null)).toBe('--:--');
    expect(formatTtg(65)).toBe('01:05');
    expect(formatTtg(3725)).toBe('1:02:05');
  });
});
