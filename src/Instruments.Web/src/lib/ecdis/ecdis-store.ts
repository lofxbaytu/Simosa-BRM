// 簡化 ECDIS 頁的狀態容器(docs/design/ecdis-lite.md):訂閱狀態來源(WebSocket 或示範模式,唯讀、不送任何指令),
// 依 state.scenario.id 由建置時匯入的情境清單建立底圖(chart-data),維護顯示設定(北上/航向上、量程、平移、航跡間隔、圖層、EBL/VRM ×2)、
// 航線監控(route-monitor)、頁面本地警報(alarms;evaluateConditions 為純函數)、AIS 目標(state.targets 中 aisOn 者)與游標;
// 聲響沿用操船台 AlertSounder(可關,設定存 localStorage)。

import type { OwnShipState } from '../../types/state.js';
import type { TargetState } from '../../types/target.js';
import type { ConnectionStatus, SimSource } from '../../net/sim-source.js';
import { AlertSounder } from '../alert-sound.js';
import { normalizeHeading } from '../angles.js';
import { CpaAlarmTracker } from '../targets.js';
import { eventFromFrame } from '../sim-event.js';
import { TrackHistory } from '../track-history.js';
import { latLonToEnu } from '../timeline.js';
import type { ScenarioSummary } from '../scenario.js';
import { EcdisAlarmManager, type EcdisCondition } from './alarms.js';
import { buildChart, emptyChart, type EcdisChart } from './chart-data.js';
import { CHART_RANGES_NM, DEFAULT_RANGE_NM, VECTOR_MINUTES, bearingDistance, chartRotation, enuToLatLon, stepRange, type ChartEbl, type ChartOrientation, type ChartVrm } from './projection.js';
import { RouteMonitor, type RouteLegStatus } from './route-monitor.js';
import { chartDepthAt, effectiveDepth, pointInPolygon, scanAhead, type AheadScan } from './safety-depth.js';

export type ChartTool = 'pan' | 'ebl1' | 'ebl2';

/** 航跡取點間隔選項(秒;0 = 不畫) */
export const TRAIL_INTERVAL_CHOICES: readonly number[] = [0, 10, 30, 60, 180] as const;

export interface EcdisSettings {
  orientation: ChartOrientation;
  rangeNm: number;
  /** 畫面跟隨自船(平移後關閉;「置中」重開) */
  follow: boolean;
  trailInterval_s: number;
  vectorMinutes: number;
  rangeRings: boolean;
  layers: { depth: boolean; sketch: boolean; zones: boolean; route: boolean; targets: boolean; labels: boolean };
  tool: ChartTool;
}

export const DEFAULT_SETTINGS: EcdisSettings = {
  orientation: 'north-up',
  rangeNm: DEFAULT_RANGE_NM,
  follow: true,
  trailInterval_s: 30,
  vectorMinutes: VECTOR_MINUTES,
  rangeRings: true,
  layers: { depth: true, sketch: true, zones: true, route: true, targets: true, labels: true },
  tool: 'pan',
};

export const ECDIS_SOUND_KEY = 'simosa-brm.ecdis.alarmSound';

/** 顯示用的 AIS 目標(位置/航向/COG/SOG 優先取 AIS 報告值,無則真值;CPA/TCPA 為核心計算值)。 */
export interface EcdisTarget {
  id: string;
  label: string;
  mmsi: number;
  x: number;
  y: number;
  heading: number;
  cog: number;
  sog: number;
  range_nm: number;
  bearing_deg: number;
  cpa_nm: number;
  tcpa_min: number;
  /** CPA < 門檻且 0 ≤ TCPA < 門檻 */
  dangerous: boolean;
  shipType: string;
  loa: number;
  beam: number;
  raw: TargetState;
}

export interface CursorInfo {
  x: number;
  y: number;
  lat?: number;
  lon?: number;
  bearing_deg: number;
  distance_nm: number;
  depth_m?: number;
}

export function ecdisTargets(state: OwnShipState, origin: { lat: number; lon: number } | null, minCpa_nm: number, tcpaLimit_min: number): EcdisTarget[] {
  const out: EcdisTarget[] = [];
  for (const t of state.targets) {
    if (!t.aisOn) continue;
    const ais = t.ais;
    let x = t.pos.x;
    let y = t.pos.y;
    if (ais && origin && typeof ais.lat === 'number' && typeof ais.lon === 'number') {
      const p = latLonToEnu(origin, ais.lat, ais.lon);
      x = p.x;
      y = p.y;
    }
    const bd = bearingDistance(state.pos, { x, y });
    out.push({
      id: t.id,
      label: ais?.name || t.name || t.id,
      mmsi: ais?.mmsi ?? t.mmsi,
      x,
      y,
      heading: normalizeHeading(ais?.heading ?? t.heading),
      cog: normalizeHeading(ais?.cog ?? t.cog),
      sog: ais?.sog ?? t.sog,
      range_nm: bd.distance_nm,
      bearing_deg: bd.bearing_deg,
      cpa_nm: t.cpa_nm,
      tcpa_min: t.tcpa_min,
      dangerous: t.cpa_nm < minCpa_nm && t.tcpa_min >= 0 && t.tcpa_min < tcpaLimit_min,
      shipType: t.shipType,
      loa: t.loa,
      beam: t.beam,
      raw: t,
    });
  }
  return out.sort((a, b) => Number(b.dangerous) - Number(a.dangerous) || a.range_nm - b.range_nm);
}

export interface EvaluateInput {
  state: OwnShipState;
  chart: EcdisChart;
  route: RouteLegStatus | null;
  targets: readonly EcdisTarget[];
  ahead: AheadScan;
}

/** 由本筆狀態評估全部警報條件(純函數,供測試)。 */
export function evaluateConditions(input: EvaluateInput): EcdisCondition[] {
  const { state, chart, route, targets, ahead } = input;
  const out: EcdisCondition[] = [];
  const sen = state.sensors;
  if (sen && !sen.positionValid) out.push({ id: 'gnss', kind: 'gnss', text: 'GNSS 位置無效:位置凍結於最後有效值(DR)' });
  if (sen && !sen.headingValid) out.push({ id: 'gyro', kind: 'gyro', text: '電羅經航向無效:航向線隱藏、航向上不可用' });
  if (sen && !sen.stwValid) out.push({ id: 'log', kind: 'log', text: '計程儀 STW 無效' });
  if (ahead.hit) {
    const where = ahead.distance_m !== undefined && ahead.distance_m > 0 ? `前方 ${Math.round(ahead.distance_m)} m(約 ${Math.round((ahead.time_s ?? 0) / 60)} 分)` : '自船位置';
    const area = ahead.area ? `「${ahead.area.name}」` : '';
    out.push({ id: 'safetyContour', kind: 'safetyContour', text: `安全等深線 ${chart.contours.safety_m} m:${where}有效水深 ${ahead.depth_m} m${area}` });
  }
  if (route && !route.complete && route.xtdExceeded) {
    out.push({ id: 'xtd', kind: 'xtd', text: `航跡偏差 ${Math.round(Math.abs(route.xtd_m))} m(${route.xtd_m >= 0 ? '右' : '左'})超過走廊 ${route.xtdLimit_m} m` });
  }
  if (route?.complete) out.push({ id: 'routeEnd', kind: 'routeEnd', text: `已抵達航線終點 ${route.nextWaypoint.name}` });
  for (const t of targets) {
    if (!t.dangerous) continue;
    out.push({ id: `cpa:${t.id}`, kind: 'cpa', text: `${t.label} CPA ${t.cpa_nm.toFixed(2)} nm / TCPA ${t.tcpa_min.toFixed(1)} 分(門檻 ${chart.minCpa_nm} nm / ${chart.tcpaLimit_min} 分)` });
  }
  for (const z of chart.zones) {
    if (z.type !== 'noGo') continue;
    if (pointInPolygon(state.pos, z.polygon)) out.push({ id: `zone:${z.id}`, kind: 'zone', text: `進入禁區「${z.name}」` });
  }
  return out;
}

export type EcdisListener = () => void;

export interface EcdisStoreOptions {
  /** 情境清單提供者(預設由建置時匯入;測試可注入) */
  scenarios?: () => ScenarioSummary[];
  areaFiles?: Readonly<Record<string, unknown>>;
  sounder?: AlertSounder;
}

export class EcdisStore {
  state: OwnShipState | null = null;
  conn: ConnectionStatus = 'disconnected';
  connDetail = '';
  chart: EcdisChart = emptyChart();
  settings: EcdisSettings = { ...DEFAULT_SETTINGS, layers: { ...DEFAULT_SETTINGS.layers } };
  ebl: [ChartEbl, ChartEbl] = [
    { on: false, bearing_deg: 0 },
    { on: false, bearing_deg: 0 },
  ];
  vrm: [ChartVrm, ChartVrm] = [
    { on: false, range_nm: 1 },
    { on: false, range_nm: 2 },
  ];
  /** 平移偏移(ENU m;follow 時忽略) */
  panX = 0;
  panY = 0;
  /** 航向上的參考航向 */
  courseRef_deg = 0;
  trail: TrackHistory;
  routeMonitor: RouteMonitor | null = null;
  routeStatus: RouteLegStatus | null = null;
  readonly alarms = new EcdisAlarmManager();
  targets: EcdisTarget[] = [];
  ahead: AheadScan = { hit: false };
  selectedTargetId: string | null = null;
  cursor: CursorInfo | null = null;
  readonly sounder: AlertSounder;
  private readonly listeners = new Set<EcdisListener>();
  private readonly scenarioProvider: (() => ScenarioSummary[]) | null;
  private scenarioCache: ScenarioSummary[] | null = null;
  private readonly areaFiles: Readonly<Record<string, unknown>>;
  private chartKey = '';
  private unsubs: Array<() => void> = [];

  constructor(options: EcdisStoreOptions = {}) {
    this.scenarioProvider = options.scenarios ?? null;
    this.areaFiles = options.areaFiles ?? {};
    this.sounder = options.sounder ?? new AlertSounder({ storageKey: ECDIS_SOUND_KEY });
    this.trail = new TrackHistory(this.settings.trailInterval_s || 1, 2000);
  }

  subscribe(fn: EcdisListener): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private notify(): void {
    for (const l of this.listeners) l();
  }

  /**
   * CPA 危險判定來源:'core' = 核心 cpaAlarm/cpaClear 事件(與操船台/教官站一致,核心會排除跟隨自船的引水船/拖船);
   * 'local' = 本頁門檻(示範模式或來源不送事件時)。attach 時依來源是否支援 onMessage 自動選擇。
   */
  cpaSource: 'local' | 'core' = 'local';
  private readonly cpaTracker = new CpaAlarmTracker();

  /** 核心事件框(cpaAlarm/cpaClear 更新危險目標集合;其他事件忽略)。 */
  onMessage(frame: unknown): void {
    if (!frame || typeof frame !== 'object') return;
    const f = frame as Record<string, unknown>;
    if (f['type'] !== 'event') return;
    const ev = eventFromFrame(f);
    if (ev && (ev.kind === 'cpaAlarm' || ev.kind === 'cpaClear')) this.cpaTracker.apply(ev);
  }

  attach(source: SimSource): void {
    this.detach();
    if (source.onMessage) {
      this.cpaSource = 'core';
      this.unsubs.push(source.onMessage((m) => this.onMessage(m)));
    } else this.cpaSource = 'local';
    this.unsubs.push(source.onStatus((s, d) => {
      this.conn = s;
      this.connDetail = d ?? '';
      this.notify();
    }));
    this.unsubs.push(source.onState((ev) => this.onState(ev.state)));
  }

  detach(): void {
    for (const u of this.unsubs) u();
    this.unsubs = [];
  }

  dispose(): void {
    this.detach();
    this.sounder.dispose();
  }

  private scenarios(): ScenarioSummary[] {
    if (!this.scenarioCache) this.scenarioCache = this.scenarioProvider ? this.scenarioProvider() : [];
    return this.scenarioCache;
  }

  /** 收到一筆狀態:底圖(情境/船/裝載/基底水深變了才重建)、航跡、航線監控、前方掃描、目標、警報、聲響。 */
  onState(state: OwnShipState): void {
    const prev = this.state;
    if (prev && state.t < prev.t) {
      // 重設/倒帶:清航跡與警報,航線監控重來
      this.trail.clear();
      this.alarms.clear();
      this.cpaTracker.clear();
      this.routeMonitor = null;
    }
    this.state = state;
    // 底圖只在情境/船/裝載改變,或狀態首次帶有經緯度(可反推原點)時重建;不逐幀重建
    const hasGeo = Number.isFinite(state.pos.lat) && Number.isFinite(state.pos.lon) && (state.pos.lat !== 0 || state.pos.lon !== 0);
    const key = `${state.scenario?.id ?? ''}|${state.shipId}|${state.loading}|${hasGeo ? 'geo' : 'nogeo'}`;
    if (key !== this.chartKey) {
      const summary = state.scenario?.id ? this.scenarios().find((s) => s.id === state.scenario!.id) ?? null : null;
      this.chart = buildChart(summary, state, this.areaFiles);
      this.chartKey = key;
      this.routeMonitor = this.chart.route ? new RouteMonitor(this.chart.route, 0) : null;
    }
    const posValid = state.sensors?.positionValid ?? true;
    if (posValid && this.settings.trailInterval_s > 0) this.trail.push(state.pos.x, state.pos.y, state.t);
    this.routeStatus = this.routeMonitor ? this.routeMonitor.update(state.pos, state.sog) : null;
    this.ahead = scanAhead(this.chart.depth, state.tideHeight_m, state.pos, state.cog, state.sog, this.chart.contours.safety_m, this.settings.vectorMinutes);
    this.targets = ecdisTargets(state, this.chart.origin, this.chart.minCpa_nm, this.chart.tcpaLimit_min);
    // 即時模式:危險目標 = 核心 cpaAlarm 中者(本地門檻只用於數值顯示;審查第二十三輪 major:E05 引水船跟隨自船會被本地門檻誤判)
    if (this.cpaSource === 'core') this.targets = this.targets.map((t) => (t.dangerous === this.cpaTracker.has(t.id) ? t : { ...t, dangerous: this.cpaTracker.has(t.id) }));
    if (this.selectedTargetId && !this.targets.some((t) => t.id === this.selectedTargetId)) this.selectedTargetId = null;
    this.alarms.update(evaluateConditions({ state, chart: this.chart, route: this.routeStatus, targets: this.targets, ahead: this.ahead }), state.t);
    this.sounder.update(this.alarms.toAlertItems());
    this.notify();
  }

  // ------------------------------------------------------------ 衍生值

  get headingValid(): boolean {
    return this.state?.sensors?.headingValid ?? true;
  }

  get positionValid(): boolean {
    return this.state?.sensors?.positionValid ?? true;
  }

  get stwValid(): boolean {
    return this.state?.sensors?.stwValid ?? true;
  }

  /** 畫面向上的真方位。 */
  get rotationDeg(): number {
    return chartRotation(this.settings.orientation, this.courseRef_deg, this.headingValid);
  }

  /** 畫面中心 ENU(跟隨:自船;否則自船 + 平移)。 */
  get centre(): { x: number; y: number } {
    const p = this.state?.pos ?? { x: 0, y: 0 };
    return this.settings.follow ? { x: p.x, y: p.y } : { x: p.x + this.panX, y: p.y + this.panY };
  }

  /** 自船位置的有效水深(圖載 + 潮高)與分類資訊。 */
  get ownDepth(): { chart_m: number; effective_m: number } {
    const p = this.state?.pos ?? { x: 0, y: 0 };
    const d = chartDepthAt(this.chart.depth, p);
    return { chart_m: d.depth_m, effective_m: effectiveDepth(d.depth_m, this.state?.tideHeight_m) };
  }

  get selectedTarget(): EcdisTarget | null {
    return this.selectedTargetId ? this.targets.find((t) => t.id === this.selectedTargetId) ?? null : null;
  }

  // ------------------------------------------------------------ 操作

  setSettings(patch: Partial<EcdisSettings>): void {
    const next = { ...this.settings, ...patch };
    if (patch.layers) next.layers = { ...this.settings.layers, ...patch.layers };
    if (patch.orientation === 'course-up' && this.settings.orientation !== 'course-up') this.courseRef_deg = this.state?.heading ?? 0;
    if (patch.trailInterval_s !== undefined && patch.trailInterval_s !== this.settings.trailInterval_s) this.trail = new TrackHistory(patch.trailInterval_s || 1, 2000);
    if (patch.rangeNm !== undefined && !CHART_RANGES_NM.includes(patch.rangeNm)) next.rangeNm = this.settings.rangeNm;
    this.settings = next;
    this.notify();
  }

  setLayer(name: keyof EcdisSettings['layers'], on: boolean): void {
    this.setSettings({ layers: { ...this.settings.layers, [name]: on } });
  }

  stepRange(dir: 1 | -1): void {
    this.setSettings({ rangeNm: stepRange(this.settings.rangeNm, dir) });
  }

  /** 航向上:以目前航向為新的參考。 */
  resetCourseUp(): void {
    this.courseRef_deg = this.state?.heading ?? 0;
    this.notify();
  }

  /** 以 ENU 位移平移畫面(拖曳);自動關閉跟隨。 */
  pan(dx: number, dy: number): void {
    if (this.settings.follow) {
      this.panX = 0;
      this.panY = 0;
      this.settings = { ...this.settings, follow: false };
    }
    this.panX += dx;
    this.panY += dy;
    this.notify();
  }

  recentre(): void {
    this.panX = 0;
    this.panY = 0;
    this.setSettings({ follow: true });
  }

  setEbl(i: 0 | 1, patch: Partial<ChartEbl>): void {
    const next: [ChartEbl, ChartEbl] = [{ ...this.ebl[0] }, { ...this.ebl[1] }];
    next[i] = { ...next[i], ...patch, bearing_deg: normalizeHeading(patch.bearing_deg ?? next[i].bearing_deg) };
    this.ebl = next;
    this.notify();
  }

  setVrm(i: 0 | 1, patch: Partial<ChartVrm>): void {
    const next: [ChartVrm, ChartVrm] = [{ ...this.vrm[0] }, { ...this.vrm[1] }];
    next[i] = { ...next[i], ...patch, range_nm: Math.max(0.01, Math.min(48, patch.range_nm ?? next[i].range_nm)) };
    this.vrm = next;
    this.notify();
  }

  /** 點選海圖(EBL 工具):同時設定該組 EBL 方位與 VRM 距離並開啟。 */
  placeEblVrm(i: 0 | 1, bearing_deg: number, range_nm: number): void {
    this.setEbl(i, { on: true, bearing_deg });
    this.setVrm(i, { on: true, range_nm });
  }

  selectTarget(id: string | null): void {
    this.selectedTargetId = id;
    this.notify();
  }

  setCursor(p: { x: number; y: number } | null): void {
    if (!p || !this.state) {
      if (this.cursor !== null) {
        this.cursor = null;
        this.notify();
      }
      return;
    }
    const bd = bearingDistance(this.state.pos, p);
    const info: CursorInfo = { x: p.x, y: p.y, bearing_deg: bd.bearing_deg, distance_nm: bd.distance_nm };
    if (this.chart.origin) {
      const ll = enuToLatLon(this.chart.origin, p.x, p.y);
      info.lat = ll.lat;
      info.lon = ll.lon;
    }
    info.depth_m = effectiveDepth(chartDepthAt(this.chart.depth, p).depth_m, this.state.tideHeight_m);
    this.cursor = info;
    this.notify();
  }

  acknowledge(id: string): void {
    if (this.alarms.acknowledge(id, this.state?.t ?? 0)) {
      this.sounder.update(this.alarms.toAlertItems());
      this.notify();
    }
  }

  acknowledgeAll(): void {
    this.alarms.acknowledgeAll(this.state?.t ?? 0);
    this.sounder.update(this.alarms.toAlertItems());
    this.notify();
  }

  setSound(on: boolean): void {
    this.sounder.setMuted(!on);
    this.notify();
  }

  /** 手動指定航段(航線面板)。 */
  setLeg(i: number): void {
    if (!this.routeMonitor || !this.state) return;
    this.routeMonitor.setLeg(i);
    this.routeStatus = this.routeMonitor.update(this.state.pos, this.state.sog);
    this.notify();
  }
}
