// 情境檔(data/scenarios/*.yaml,schema:src/Contracts/scenario.schema.json)的解析與摘要。
// 建置時以 import.meta.glob 把所有情境 YAML 以文字匯入(船上離線也能列出),執行時以 yaml 套件解析;
// 教官站只需要清單與摘要,實際載入由核心依 loadScenario 指令讀同一份檔案(規劃書第 9.1 節)。

import { parse as parseYaml } from 'yaml';
import { SHIP_IDS, TELEGRAPH_POSITIONS, type LoadingCondition, type ShipId, type TelegraphPosition } from '../types/state.js';
import { normalizeHeading } from './angles.js';
import { parseAssessmentRules, type AssessmentRules } from './assessment.js';
import { collectParamRefs, isParamRef, parseBriefing, parseRoute, parseTimeline, parseZones, type ScenarioBriefing, type ScenarioRoute, type ScenarioZone, type TimelineItem } from './timeline.js';

const LOADINGS: readonly LoadingCondition[] = ['full', 'ballast', 'intermediate'];

export interface ScenarioEnvironment {
  wind?: { trueSpeed?: number; trueDir?: number; gustiness?: number };
  current?: { set?: number; drift?: number };
  waterDepth?: number;
  /** 能見度 (nm);無物理效果,核心保存/漸變/廣播(設計文件第 1.5 節) */
  visibility_nm?: number;
}

/** 情境擴充欄位的統計(情境面板摘要:「時間表 n 項、航線 n 航點、評估規則 n 條、參數引用 n 個」)。 */
export interface ScenarioStats {
  timelineItems: number;
  manualItems: number;
  routeWaypoints: number;
  zones: number;
  assessmentRules: number;
  paramRefs: number;
  targets: number;
}

export interface ScenarioInitial {
  position?: { lat?: number; lon?: number; x?: number; y?: number };
  heading?: number;
  speed?: number;
  telegraph?: TelegraphPosition;
  rudder?: number;
}

/** 情境摘要(教官站清單、簡化教官模式的簡報與講評報告使用)。 */
export interface ScenarioSummary {
  id: string;
  name: string;
  description: string;
  /** 練習簡報文字(briefing 字串或 briefing.text;簡化教官模式大字顯示;無則用摘要) */
  briefing: string;
  /** 簡報物件(附件、角色、最少人數;設計文件第 1.4 節) */
  briefingDoc: ScenarioBriefing;
  /** 本地 ENU 原點(航線/區域疊圖換算用) */
  origin?: { lat: number; lon: number };
  /** 相對於專案根目錄的路徑,例如 data/scenarios/E01_baseline.yaml(loadScenario 指令用) */
  path: string;
  shipId: ShipId;
  loading: LoadingCondition;
  seed: number;
  timeScale: number;
  startTimeUtc?: string;
  initial: ScenarioInitial;
  environment: ScenarioEnvironment;
  /** 時間表(設計文件第 1.1 節) */
  timeline: TimelineItem[];
  route: ScenarioRoute | null;
  zones: ScenarioZone[];
  /** 評估規則(含既有 minCpa_nm;數值可為參數引用) */
  assessment: AssessmentRules;
  /** 情境內所有 ${params:key} 引用(路徑 → 鍵) */
  paramRefs: Array<{ path: string; key: string }>;
  stats: ScenarioStats;
  /** 解析時發現的問題(缺必要欄位、型別錯誤),空陣列表示通過最小檢查 */
  issues: string[];
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function optNumber(v: unknown): number | undefined {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '' && !isParamRef(v) && Number.isFinite(Number(v))) return Number(v);
  return undefined;
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v);
}

/** 由檔案路徑或 glob 鍵取出檔名,組成相對於專案根目錄的 data/scenarios/<檔名>。 */
export function scenarioPathFromKey(key: string): string {
  const file = key.replace(/\\/g, '/').split('/').pop() ?? key;
  return `data/scenarios/${file}`;
}

/**
 * 解析一份情境 YAML 文字為摘要。不丟例外:格式錯誤時回傳 issues 並盡量補上預設值,
 * 讓教官站仍能列出(並標示有問題)。
 */
export function parseScenario(yamlText: string, path = ''): ScenarioSummary {
  const issues: string[] = [];
  let raw: unknown;
  try {
    raw = parseYaml(yamlText);
  } catch (err) {
    issues.push(`YAML 解析失敗:${err instanceof Error ? err.message : String(err)}`);
    raw = {};
  }
  const doc = isRecord(raw) ? raw : (issues.push('情境不是物件'), {});

  const id = str(doc['id']).trim();
  if (!id) issues.push('缺 id');

  const ship = isRecord(doc['ship']) ? doc['ship'] : (issues.push('缺 ship'), {});
  let shipId: ShipId = 'FSB1';
  if (typeof ship['id'] === 'string' && (SHIP_IDS as readonly string[]).includes(ship['id'])) shipId = ship['id'] as ShipId;
  else issues.push('ship.id 無效');
  let loading: LoadingCondition = 'ballast';
  if (ship['loading'] === undefined) {
    /* schema 預設 ballast */
  } else if (typeof ship['loading'] === 'string' && (LOADINGS as readonly string[]).includes(ship['loading'])) {
    loading = ship['loading'] as LoadingCondition;
  } else issues.push('ship.loading 無效');

  const initial: ScenarioInitial = {};
  const init = isRecord(doc['initial']) ? doc['initial'] : (issues.push('缺 initial'), {});
  if (isRecord(init['position'])) {
    const p = init['position'];
    const pos: NonNullable<ScenarioInitial['position']> = {};
    const lat = optNumber(p['lat']);
    const lon = optNumber(p['lon']);
    const x = optNumber(p['x']);
    const y = optNumber(p['y']);
    if (lat !== undefined) pos.lat = lat;
    if (lon !== undefined) pos.lon = lon;
    if (x !== undefined) pos.x = x;
    if (y !== undefined) pos.y = y;
    if ((lat === undefined || lon === undefined) && (x === undefined || y === undefined)) issues.push('initial.position 需 lat/lon 或 x/y');
    initial.position = pos;
  } else issues.push('缺 initial.position');
  const heading = optNumber(init['heading']);
  if (heading !== undefined) initial.heading = normalizeHeading(heading);
  else issues.push('缺 initial.heading');
  const speed = optNumber(init['speed']);
  if (speed !== undefined) initial.speed = speed;
  if (typeof init['telegraph'] === 'string') {
    if ((TELEGRAPH_POSITIONS as readonly string[]).includes(init['telegraph'])) initial.telegraph = init['telegraph'] as TelegraphPosition;
    else issues.push('initial.telegraph 無效');
  }
  const rudder = optNumber(init['rudder']);
  if (rudder !== undefined) initial.rudder = rudder;

  const environment: ScenarioEnvironment = {};
  const env = isRecord(doc['environment']) ? doc['environment'] : (issues.push('缺 environment'), {});
  if (isRecord(env['wind'])) {
    const w = env['wind'];
    const wind: NonNullable<ScenarioEnvironment['wind']> = {};
    const ts = optNumber(w['trueSpeed']);
    const td = optNumber(w['trueDir']);
    const g = optNumber(w['gustiness']);
    if (ts !== undefined) wind.trueSpeed = ts;
    if (td !== undefined) wind.trueDir = normalizeHeading(td);
    if (g !== undefined) wind.gustiness = g;
    environment.wind = wind;
  }
  if (isRecord(env['current'])) {
    const c = env['current'];
    const current: NonNullable<ScenarioEnvironment['current']> = {};
    const set = optNumber(c['set']);
    const drift = optNumber(c['drift']);
    if (set !== undefined) current.set = normalizeHeading(set);
    if (drift !== undefined) current.drift = drift;
    environment.current = current;
  }
  const depth = optNumber(env['waterDepth']);
  if (depth !== undefined && depth > 0) environment.waterDepth = depth;
  else if (!isParamRef(env['waterDepth'])) issues.push('缺 environment.waterDepth 或不為正數');
  const vis = optNumber(env['visibility_nm'] ?? env['visibility']);
  if (vis !== undefined) environment.visibility_nm = vis;

  const seed = optNumber(doc['seed']);
  const timeScale = optNumber(doc['timeScale']);
  const briefingDoc = parseBriefing(doc['briefing'], issues);
  const timeline = parseTimeline(doc['timeline'], issues);
  const route = parseRoute(doc['route'], issues);
  const zones = parseZones(doc['zones'], issues);
  const assessment = parseAssessmentRules(doc['assessment'], issues);
  const paramRefs = collectParamRefs(doc);
  const targets = Array.isArray(doc['targets']) ? doc['targets'].length : 0;
  const summary: ScenarioSummary = {
    id: id || (path ? scenarioPathFromKey(path).replace(/^data\/scenarios\//, '').replace(/\.ya?ml$/i, '') : '(無 id)'),
    name: str(doc['name']).trim() || id || '(未命名情境)',
    description: str(doc['description']).trim(),
    briefing: briefingDoc.text,
    briefingDoc,
    path: path ? scenarioPathFromKey(path) : '',
    shipId,
    loading,
    seed: seed !== undefined ? Math.trunc(seed) : 1,
    timeScale: timeScale !== undefined ? timeScale : 1,
    initial,
    environment,
    timeline,
    route,
    zones,
    assessment,
    paramRefs,
    stats: {
      timelineItems: timeline.length,
      manualItems: timeline.filter((i) => i.manual).length,
      routeWaypoints: route?.waypoints.length ?? 0,
      zones: zones.length,
      assessmentRules: assessment.ruleCount,
      paramRefs: paramRefs.length,
      targets,
    },
    issues,
  };
  if (isRecord(doc['origin'])) {
    const olat = optNumber(doc['origin']['lat']);
    const olon = optNumber(doc['origin']['lon']);
    if (olat !== undefined && olon !== undefined) summary.origin = { lat: olat, lon: olon };
  }
  if (!summary.origin && initial.position?.lat !== undefined && initial.position.lon !== undefined) summary.origin = { lat: initial.position.lat, lon: initial.position.lon };
  const start = doc['startTimeUtc'];
  if (typeof start === 'string' && start.trim() !== '') summary.startTimeUtc = start;
  else if (start instanceof Date) summary.startTimeUtc = start.toISOString();
  return summary;
}

const LOADING_LABEL: Record<LoadingCondition, string> = { full: '滿載', ballast: '壓載', intermediate: '中間' };

export function loadingLabel(loading: LoadingCondition): string {
  return LOADING_LABEL[loading];
}

/** 環境一句話摘要,例如「風 15 kn/045°(陣風 0.1)· 流 200°/0.5 kn · 水深 30 m」。 */
export function environmentSummary(env: ScenarioEnvironment): string {
  const parts: string[] = [];
  const w = env.wind;
  if (w && (w.trueSpeed !== undefined || w.trueDir !== undefined)) {
    let s = `風 ${w.trueSpeed ?? '?'} kn/${w.trueDir !== undefined ? Math.round(w.trueDir).toString().padStart(3, '0') + '°' : '?'}`;
    if (w.gustiness) s += `(陣風 ${w.gustiness})`;
    parts.push(s);
  } else parts.push('無風');
  const c = env.current;
  if (c && c.drift) parts.push(`流 ${c.set !== undefined ? Math.round(c.set).toString().padStart(3, '0') + '°' : '?'}/${c.drift} kn`);
  else parts.push('無流');
  parts.push(env.waterDepth !== undefined ? `水深 ${env.waterDepth} m` : '水深未定');
  if (env.visibility_nm !== undefined) parts.push(`能見度 ${env.visibility_nm} nm`);
  return parts.join(' · ');
}

/** 初始狀態一句話摘要,例如「HDG 000 · 7.8 kn · HAH」。 */
export function initialSummary(init: ScenarioInitial): string {
  const parts: string[] = [];
  if (init.heading !== undefined) parts.push(`HDG ${Math.round(init.heading).toString().padStart(3, '0')}`);
  if (init.speed !== undefined) parts.push(`${init.speed} kn`);
  if (init.telegraph) parts.push(init.telegraph);
  if (init.position) {
    const p = init.position;
    if (p.lat !== undefined && p.lon !== undefined) parts.push(`${p.lat.toFixed(3)}, ${p.lon.toFixed(3)}`);
    else if (p.x !== undefined && p.y !== undefined) parts.push(`x ${p.x} m, y ${p.y} m`);
  }
  return parts.join(' · ');
}

/** 情境擴充欄位的一句話摘要,例如「時間表 3 項(手動 1)· 航線 4 航點 · 區域 2 · 評估規則 9 條 · 參數引用 12 個」;全無時回傳空字串。 */
export function extensionSummary(s: ScenarioSummary): string {
  const st = s.stats;
  const parts: string[] = [];
  if (st.timelineItems > 0) parts.push(`時間表 ${st.timelineItems} 項${st.manualItems > 0 ? `(手動 ${st.manualItems})` : ''}`);
  if (st.routeWaypoints > 0) parts.push(`航線 ${st.routeWaypoints} 航點`);
  if (st.zones > 0) parts.push(`區域 ${st.zones}`);
  if (st.assessmentRules > 0) parts.push(`評估規則 ${st.assessmentRules} 條`);
  if (st.paramRefs > 0) parts.push(`參數引用 ${st.paramRefs} 個`);
  if (st.targets > 0) parts.push(`目標船 ${st.targets}`);
  return parts.join(' · ');
}

/** 簡化教官模式的簡報文字:優先用 briefing 欄位,無則由摘要組成。 */
export function briefingText(s: ScenarioSummary): string {
  if (s.briefing) return s.briefing;
  const lines = [
    s.description || s.name,
    `船:${s.shipId}(${loadingLabel(s.loading)})`,
    `初始:${initialSummary(s.initial)}`,
    `環境:${environmentSummary(s.environment)}`,
  ];
  return lines.join('\n');
}

/** 解析多份情境並依 id 排序(鍵為路徑或 glob 鍵,值為 YAML 文字)。 */
export function parseScenarioMap(files: Record<string, string>): ScenarioSummary[] {
  return Object.entries(files)
    .map(([key, text]) => parseScenario(text, key))
    .sort((a, b) => a.id.localeCompare(b.id));
}

/** 建置時匯入的全部情境(data/scenarios/*.yaml);沒有情境檔時為空陣列。 */
const SCENARIO_FILES = import.meta.glob('@data/scenarios/*.{yaml,yml}', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

export function loadScenarioList(): ScenarioSummary[] {
  return parseScenarioMap(SCENARIO_FILES);
}
