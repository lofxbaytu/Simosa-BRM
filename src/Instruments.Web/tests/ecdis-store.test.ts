// 簡化 ECDIS 狀態容器(lib/ecdis/ecdis-store、chart-data):以 E04 情境 + 假狀態建立底圖(原點、航線、區域、水深區、示意幾何、CPA 門檻、安全等深線)、
// 情境不明時由狀態反推原點、AIS 目標(aisOn 過濾、AIS 報告位置、危險判定)、警報條件評估(GNSS/電羅經/XTD/CPA/禁區/安全等深線)、
// 航跡在 DR 時不累積、平移關閉跟隨、航向上與電羅經無效、EBL/VRM、倒帶清警報。不送任何指令。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { AREA_FILES, parseScenario, type ScenarioSummary } from '../src/lib/scenario.js';
import { buildChart, depthAreasFromAreas, zonesFromScenario } from '../src/lib/ecdis/chart-data.js';
import { EcdisStore, ecdisTargets, evaluateConditions } from '../src/lib/ecdis/ecdis-store.js';
import { defaultState } from '../src/lib/state-parser.js';
import { latLonToEnu } from '../src/lib/timeline.js';
import { AlertSounder } from '../src/lib/alert-sound.js';
import type { OwnShipState } from '../src/types/state.js';
import type { TargetState } from '../src/types/target.js';
import type { SimSource, StateListener, StatusListener } from '../src/net/sim-source.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const e04Path = path.resolve(here, '../../../data/scenarios/E04_pilot_boarding_kaohsiung.yaml');
const e07Path = path.resolve(here, '../../../data/scenarios/E07_steering_failure.yaml');
const e04: ScenarioSummary = parseScenario(readFileSync(e04Path, 'utf-8'), e04Path);
const e07: ScenarioSummary = parseScenario(readFileSync(e07Path, 'utf-8'), e07Path);
const ORIGIN = { lat: 22.53, lon: 120.24 };

function stateAt(lat: number, lon: number, extra: Partial<OwnShipState> = {}): OwnShipState {
  const s = defaultState('FSB2');
  const p = latLonToEnu(ORIGIN, lat, lon);
  s.pos = { lat, lon, x: p.x, y: p.y };
  s.loading = 'full';
  s.heading = 45;
  s.cog = 45;
  s.sog = 10;
  s.stw = 10;
  s.waterDepth = 40;
  s.scenario = { id: 'E04_pilot_boarding_kaohsiung', phase: 'normal' };
  return { ...s, ...extra };
}

function target(id: string, x: number, y: number, cpa: number, tcpa: number, aisOn = true, ais?: TargetState['ais']): TargetState {
  const t: TargetState = { id, name: id, mmsi: 1, shipType: 'cargo', loa: 100, beam: 16, pos: { lat: 0, lon: 0, x, y }, heading: 180, cog: 180, sog: 8, rot: 0, range_nm: 1, bearing_deg: 0, cpa_nm: cpa, tcpa_min: tcpa, aisOn, lights: 'powerDriven', behaviour: 'hold' };
  if (ais) t.ais = ais;
  return t;
}

class FakeSource implements SimSource {
  status = 'connected' as const;
  private states = new Set<StateListener>();
  private statuses = new Set<StatusListener>();
  sent = 0;
  start(): void {}
  stop(): void {}
  send(): void {
    this.sent++;
  }
  onState(l: StateListener): () => void {
    this.states.add(l);
    return () => this.states.delete(l);
  }
  onStatus(l: StatusListener): () => void {
    this.statuses.add(l);
    l('connected', 'fake');
    return () => this.statuses.delete(l);
  }
  push(state: OwnShipState): void {
    for (const l of this.states) l({ state, issues: [] });
  }
}

const quietSounder = () => new AlertSounder({ storageKey: null, contextFactory: () => null });

describe('ECDIS 底圖(chart-data)', () => {
  it('E04:情境原點、4 航點航線、3 區域、CPA 門檻為參數 → 預設 0.5、FSB2 滿載安全等深線 8.2 m、高雄示意幾何', () => {
    const c = buildChart(e04, stateAt(22.41174, 120.1127), AREA_FILES);
    expect(c.originSource).toBe('scenario');
    expect(c.origin).toEqual(ORIGIN);
    expect(c.route?.waypoints.map((w) => w.name)).toEqual(['START', 'K1', 'HOLD', 'CHARLIE']);
    expect(c.route?.waypoints[3]?.arrivalRadius_m).toBe(300);
    expect(c.zones.map((z) => z.id)).toEqual(['fairway', 'holdingArea', 'anchorage']);
    expect(c.minCpa_nm).toBe(0.5);
    expect(c.minCpaSource).toBe('default');
    expect(c.maxDraft_m).toBeCloseTo(7.2);
    expect(c.ukc).toMatchObject({ ukc_m: 1.0, source: 'default' });
    expect(c.contours.safety_m).toBeCloseTo(8.2);
    expect(c.depth.baseDepth_m).toBe(40);
    expect(c.baseDepthKnown).toBe(true);
    expect(c.sketch?.files).toContain('kaohsiung-approach.json');
    expect(c.sketch?.shapes.some((s) => s.kind === 'land')).toBe(true);
  });

  it('E07:ukcPolicy 區域的 waterDepth_m 成為水深區;UKC 比例為參數引用 → 公司最小值', () => {
    const c = buildChart(e07, null, AREA_FILES);
    expect(c.zones.find((z) => z.id === 'channel')?.waterDepth_m).toBe(12.5);
    expect(c.depth.areas.some((a) => a.id === 'zone:channel' && a.depth_m === 12.5)).toBe(true);
    expect(c.ukc.source).toBe('company');
    expect(c.zones.find((z) => z.id === 'anchorage_S')?.type).toBe('noGo');
    expect(zonesFromScenario(e07, null)).toEqual([]); // E07 航點為經緯度,無原點時無法換算
  });

  it('情境不明:由狀態反推原點,示意幾何仍可疊;無狀態無情境 → 空底圖', () => {
    const s = stateAt(22.41174, 120.1127, { scenario: { id: 'UNKNOWN', phase: 'normal' } });
    const c = buildChart(null, s, AREA_FILES);
    expect(c.originSource).toBe('state');
    expect(c.origin!.lat).toBeCloseTo(ORIGIN.lat, 6);
    expect(c.origin!.lon).toBeCloseTo(ORIGIN.lon, 6);
    expect(c.route).toBeNull();
    expect(c.sketch?.files).toContain('kaohsiung-approach.json');
    expect(c.scenarioId).toBe('UNKNOWN');
    const empty = buildChart(null, null, {});
    expect(empty.origin).toBeNull();
    expect(empty.baseDepthKnown).toBe(false);
    expect(empty.depth.baseDepth_m).toBe(20);
  });

  it('depthAreasFromAreas:安平淺灘與高雄港內航道的 depth_m', () => {
    const anping = depthAreasFromAreas(AREA_FILES, { lat: 22.99, lon: 120.13 });
    expect(anping.some((a) => a.kind === 'shoal' && a.depth_m === 5)).toBe(true);
    const khh = depthAreasFromAreas(AREA_FILES, { lat: 22.56, lon: 120.3 });
    expect(khh.some((a) => a.kind === 'channel' && a.depth_m === 12.5)).toBe(true);
    expect(depthAreasFromAreas(AREA_FILES, null)).toEqual([]);
  });
});

describe('AIS 目標與警報條件', () => {
  it('ecdisTargets:只取 aisOn,位置優先 AIS 報告經緯度,危險 = CPA < 門檻且 0 ≤ TCPA < 12', () => {
    const s = stateAt(22.5, 120.2);
    const aisPos = latLonToEnu(ORIGIN, 22.51, 120.21);
    s.targets = [
      target('A', 500, 500, 0.3, 5, true, { lat: 22.51, lon: 120.21, name: 'ALPHA', cog: 90, sog: 12 }),
      target('B', 1000, 0, 0.3, -1),
      target('C', 2000, 0, 1.2, 5),
      target('D', 100, 0, 0.1, 1, false),
    ];
    const ts = ecdisTargets(s, ORIGIN, 0.5, 12);
    expect(ts.map((t) => t.id)).toEqual(['A', 'B', 'C']);
    const a = ts[0]!;
    expect(a.label).toBe('ALPHA');
    expect(a.x).toBeCloseTo(aisPos.x);
    expect(a.y).toBeCloseTo(aisPos.y);
    expect(a.cog).toBe(90);
    expect(a.sog).toBe(12);
    expect(a.dangerous).toBe(true);
    expect(ts[1]!.dangerous).toBe(false); // TCPA 負:已通過
    expect(ts[2]!.dangerous).toBe(false);
  });

  it('evaluateConditions:GNSS/電羅經/計程儀、XTD、CPA、禁區、安全等深線', () => {
    const s = stateAt(22.5, 120.2, { sensors: { headingValid: false, positionValid: false, depthValid: true, stwValid: false, magneticHeading: 44 } });
    const chart = buildChart(e04, s, AREA_FILES);
    chart.zones.push({ id: 'ng', name: '禁區', type: 'noGo', polygon: [{ x: s.pos.x - 10, y: s.pos.y - 10 }, { x: s.pos.x + 10, y: s.pos.y - 10 }, { x: s.pos.x + 10, y: s.pos.y + 10 }, { x: s.pos.x - 10, y: s.pos.y + 10 }] });
    const targets = ecdisTargets({ ...s, targets: [target('T1', 0, 0, 0.2, 3)] }, chart.origin, chart.minCpa_nm, chart.tcpaLimit_min);
    const conds = evaluateConditions({
      state: s,
      chart,
      route: { legIndex: 0, legCount: 3, nextWaypoint: chart.route!.waypoints[1]!, nextIndex: 1, xtd_m: -1200, xtdLimit_m: 926, xtdExceeded: true, dtg_m: 1, dtgTotal_m: 1, ttg_s: 1, plannedCourse_deg: 45, bearingToWaypoint_deg: 45, nextCourse_deg: 45, turn_deg: 0, complete: false },
      targets,
      ahead: { hit: true, distance_m: 800, time_s: 155, depth_m: 6, area: { id: 'x', name: '淺灘', kind: 'shoal', depth_m: 6, polygon: [] } },
    });
    const ids = conds.map((c) => c.id);
    expect(ids).toEqual(['gnss', 'gyro', 'log', 'safetyContour', 'xtd', 'cpa:T1', 'zone:ng']);
    expect(conds.find((c) => c.id === 'xtd')!.text).toContain('1200 m(左)');
    expect(conds.find((c) => c.id === 'safetyContour')!.text).toContain('前方 800 m');
    expect(conds.find((c) => c.id === 'cpa:T1')!.text).toContain('0.5 nm / 12 分');
    // 全部正常 → 無條件
    const ok = stateAt(22.5, 120.2);
    expect(evaluateConditions({ state: ok, chart: buildChart(e04, ok, AREA_FILES), route: null, targets: [], ahead: { hit: false } })).toEqual([]);
  });
});

describe('EcdisStore', () => {
  it('收到狀態:依 scenario id 建底圖與航線監控;DR 時航跡不累積並有 GNSS 警報;不送指令', () => {
    const src = new FakeSource();
    const store = new EcdisStore({ scenarios: () => [e04, e07], areaFiles: AREA_FILES, sounder: quietSounder() });
    store.attach(src);
    let notified = 0;
    store.subscribe(() => notified++);
    src.push(stateAt(22.41174, 120.1127, { t: 0 }));
    expect(store.conn).toBe('connected');
    expect(store.chart.scenarioId).toBe('E04_pilot_boarding_kaohsiung');
    expect(store.chart.scenarioName).toContain('高雄');
    expect(store.routeStatus?.nextWaypoint.name).toBe('K1');
    expect(store.routeStatus?.plannedCourse_deg).toBeCloseTo(45, 0);
    expect(store.trail.length).toBe(1);
    expect(store.alarms.size).toBe(0);
    src.push(stateAt(22.42, 120.12, { t: 60, sensors: { headingValid: true, positionValid: false, depthValid: true, stwValid: true, magneticHeading: 45 } }));
    expect(store.positionValid).toBe(false);
    expect(store.trail.length).toBe(1); // DR:不加點
    expect(store.alarms.get('gnss')).toMatchObject({ active: true, acknowledged: false });
    expect(store.alarms.soundPriority()).toBe('warning');
    store.acknowledge('gnss');
    expect(store.alarms.get('gnss')?.acknowledged).toBe(true);
    expect(notified).toBeGreaterThan(2);
    expect(src.sent).toBe(0);
    store.dispose();
  });

  it('設定:航向上擷取參考航向、電羅經無效退回 0;平移關閉跟隨、置中恢復;EBL/VRM 兩組;量程;倒帶清警報', () => {
    const store = new EcdisStore({ scenarios: () => [e04], areaFiles: AREA_FILES, sounder: quietSounder() });
    store.onState(stateAt(22.41174, 120.1127, { t: 10, heading: 47 }));
    store.setSettings({ orientation: 'course-up' });
    expect(store.courseRef_deg).toBe(47);
    expect(store.rotationDeg).toBe(47);
    store.onState(stateAt(22.4118, 120.1128, { t: 11, heading: 50, sensors: { headingValid: false, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 50 } }));
    expect(store.rotationDeg).toBe(0);
    store.resetCourseUp();
    expect(store.courseRef_deg).toBe(50);
    store.pan(100, -50);
    expect(store.settings.follow).toBe(false);
    expect(store.centre).toEqual({ x: store.state!.pos.x + 100, y: store.state!.pos.y - 50 });
    store.recentre();
    expect(store.settings.follow).toBe(true);
    expect(store.centre).toEqual({ x: store.state!.pos.x, y: store.state!.pos.y });
    store.placeEblVrm(1, 370, 2.5);
    expect(store.ebl[1]).toEqual({ on: true, bearing_deg: 10 });
    expect(store.vrm[1]).toEqual({ on: true, range_nm: 2.5 });
    expect(store.ebl[0].on).toBe(false);
    store.setVrm(0, { range_nm: 100 });
    expect(store.vrm[0].range_nm).toBe(48);
    store.stepRange(1);
    expect(store.settings.rangeNm).toBe(6);
    store.setSettings({ rangeNm: 7 });
    expect(store.settings.rangeNm).toBe(6);
    store.setSettings({ trailInterval_s: 10 });
    expect(store.trail.length).toBe(0);
    // 倒帶:警報清空
    expect(store.alarms.get('gyro')).toBeDefined();
    store.onState(stateAt(22.41174, 120.1127, { t: 0 }));
    expect(store.alarms.size).toBe(0);
    store.setCursor({ x: store.state!.pos.x + 1852, y: store.state!.pos.y });
    expect(store.cursor?.bearing_deg).toBeCloseTo(90, 6);
    expect(store.cursor?.distance_nm).toBeCloseTo(1, 6);
    expect(store.cursor?.lon).toBeGreaterThan(120.1127);
    expect(store.cursor?.depth_m).toBe(40);
    store.setCursor(null);
    expect(store.cursor).toBeNull();
    store.dispose();
  });

  it('CPA 警報:危險目標進入 → alarm;選取目標在目標消失時清除', () => {
    const store = new EcdisStore({ scenarios: () => [e04], areaFiles: AREA_FILES, sounder: quietSounder() });
    const s = stateAt(22.5, 120.2, { t: 1 });
    s.targets = [target('T1', s.pos.x + 800, s.pos.y + 800, 0.2, 6)];
    store.onState(s);
    expect(store.targets).toHaveLength(1);
    expect(store.alarms.get('cpa:T1')?.priority).toBe('alarm');
    expect(store.alarms.soundPriority()).toBe('alarm');
    store.selectTarget('T1');
    expect(store.selectedTarget?.id).toBe('T1');
    store.onState({ ...s, t: 2, targets: [] });
    expect(store.selectedTargetId).toBeNull();
    expect(store.alarms.get('cpa:T1')).toMatchObject({ active: false, acknowledged: false });
    store.dispose();
  });
});
