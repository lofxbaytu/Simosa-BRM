// 教官站情境編輯器的草稿模型(規劃書第 9.1 節「教官依範本組出新練習(環境、目標船、2 個故障、評估規則)≤30 分鐘」;R4;第 14 章教官第 2 列)。
// 流程:fromYaml(範本原文)→ ScenarioDraft(可序列化的純物件,存 localStorage)→ toScenarioObject() / toYaml() → parseScenario 自我驗證 →
// 「下載 YAML」或以 loadScenario args.scenario 內嵌送核心(與靠離泊預演相同的載入路徑;lib/berthing-rehearsal.ts)。
// 編輯器涵蓋:基本資料(id/name/description/簡報文字/seed/timeScale/startTimeUtc)、船與裝載、初始位置/航向/速度/車鐘/舵角、
// 環境(風、流、水深、能見度)、目標船(id/船名/船型/尺寸/初始位置/航向/速度/行為模式)、時間表(新增故障項目、新增一般項目、
// 逐項編輯 id/名稱/階段/觸發/動作與其參數、上下移、刪除)、評估(硬性失敗/容許帶/評分、必要事件、minCpa/encounter/ukc/manoeuvring)、
// 航線(航點表格、檢查點、走廊/速限/到達半徑)、區域(型別、屬性、多邊形頂點)、簡報(角色、附件、最少人數、時長)、
// sensors/alerts/bnwas、拖船/錨/泊位(file 參照與繫纜)/岸壁(file 參照與倍率;lines/definitions 原樣保留)。
// 其餘欄位(origin、environment.seabed/radar/tide(潮汐只保留不編輯)、目標船的 AIS/行為細節/trigger、assessment.parameterSet、
// 時間表項目的 repeat/cooldown_s/description、berth.definition、banks.lines/definitions …)原樣保留在 draft.raw 或各區段的原樣物件內,
// 輸出時覆寫涵蓋欄位、不遺失其餘資料(設計文件 docs/design/scenario-engine.md 第 6.1 節)。
// 新區段(route、zones、briefing、sensors、alerts、bnwas、assessment.required/settings、tugs、anchors、berth、banks)在草稿裡是情境物件的
// 原樣複本(Record),以 withPath 點路徑逐鍵修改:未改的鍵完全不動,round-trip 與既有情境檔深相等(tests/scenario-editor.test.ts)。
// 輸出 YAML 以 yaml Document 產生:會被 YAML 1.1 解析器誤判的字串(on/off/yes/no、時間戳、數字樣、參數引用)一律加雙引號,
// 與 data/scenarios 既有寫法一致(C# YamlJson 只把 true/false/null/數字當非字串;Python 參考實作以 PyYAML 讀檔)。YAML 註解不保留。

import { Document, Scalar, isCollection, parse as parseYaml, visit } from 'yaml';
import { SHIP_IDS, TELEGRAPH_POSITIONS, type LoadingCondition, type ShipId, type TelegraphPosition } from '../types/state.js';
import { faultCatalogue, type FaultDefinition } from './instructor-command.js';
import { AREA_FILES, parseScenario, type ScenarioSummary } from './scenario.js';
import { getShipConfig, maxDraft } from './ship-config.js';
import { TIMELINE_ACTION_KINDS, actionSummary, conditionSummary, isParamRef, parseTimelineItem, type TimelineActionKind, type TriggerCondition } from './timeline.js';

// ------------------------------------------------------------------ 型別

/** 數值欄位:數字,或原樣保留的字串(${params:key} 參數引用、"2x" 倍數;教官站不解析參數集)。 */
export type NumberOrRef = number | string;

/** 部分更新:值為 undefined 表示刪除該欄位(exactOptionalPropertyTypes 下 Partial 不接受明示的 undefined)。 */
export type Patch<T> = { [K in keyof T]?: T[K] | undefined };

/** 經緯度或本地 ENU(m);沿用情境座標語意,二擇一。 */
export interface DraftPosition {
  lat?: NumberOrRef;
  lon?: NumberOrRef;
  x?: NumberOrRef;
  y?: NumberOrRef;
}

export interface DraftGeneral {
  id: string;
  name: string;
  description: string;
  /** 簡報文字(briefing 字串或 briefing.text;附件與角色保留在 raw.briefing) */
  briefing: string;
  seed?: number;
  timeScale?: number;
  /** 練習開始 UTC 時刻(日/夜由此決定;未給時核心以實際時鐘代入) */
  startTimeUtc?: string;
}

export interface DraftShip {
  id: ShipId;
  /** 未給 = schema 預設 ballast(原樣不補) */
  loading?: LoadingCondition;
}

export interface DraftInitial {
  position: DraftPosition;
  heading?: NumberOrRef;
  speed?: NumberOrRef;
  telegraph?: TelegraphPosition;
  rudder?: NumberOrRef;
}

export interface DraftEnvironment {
  windSpeed?: NumberOrRef;
  windDir?: NumberOrRef;
  gustiness?: NumberOrRef;
  currentSet?: NumberOrRef;
  currentDrift?: NumberOrRef;
  waterDepth?: NumberOrRef;
  visibility_nm?: NumberOrRef;
}

export type TargetMode = 'hold' | 'waypoints' | 'scripted' | 'anchored' | 'follow' | 'colreg';
export const TARGET_MODES: readonly TargetMode[] = ['hold', 'waypoints', 'scripted', 'anchored', 'follow', 'colreg'] as const;
export const TARGET_MODE_LABEL: Readonly<Record<TargetMode, string>> = {
  hold: '保持航向航速',
  waypoints: '沿航點(raw 保留航點)',
  scripted: '腳本(raw 保留 script)',
  anchored: '錨泊迴盪',
  follow: '跟隨',
  colreg: 'COLREG 避讓',
};
export const TARGET_SHIP_TYPES: readonly string[] = ['cargo', 'tanker', 'fishing', 'pilot', 'tug', 'passenger', 'pleasure', 'sar', 'hsc', 'sailing', 'other'] as const;

export interface DraftTarget {
  id: string;
  name: string;
  shipType: string;
  loa?: NumberOrRef;
  beam?: NumberOrRef;
  position: DraftPosition;
  heading?: NumberOrRef;
  speed?: NumberOrRef;
  /** behaviour.mode;未給 = schema 預設 hold */
  mode?: TargetMode | (string & {});
  /** 原始目標船物件(mmsi、draft、AIS、行為細節、trigger …原樣保留;輸出時以上列欄位覆寫) */
  raw: Record<string, unknown>;
}

export interface DraftAssessment {
  /** 硬性失敗規則(原樣物件;{id, rule, lessThan?, zoneId?, kind?}) */
  hardFail: Record<string, unknown>[];
  /** 容許帶(原樣物件;{id, metric, direction?, warn?, fail?, penalty?, failPenalty?}) */
  bands: Record<string, unknown>[];
  /** 必要事件(原樣物件;{id, after{event, itemId?, targetId?, waypoint?}, expect, within_s, penalty?, latePenaltyPer_s?};patchRequired 點路徑編輯) */
  required: Record<string, unknown>[];
  /** 其餘評估設定(原樣鍵 minCpa_nm、encounter{…}、ukc{min{metres|fractionOfDraft}}、manoeuvring{…};patchAssessmentSetting 點路徑編輯;parameterSet 留在 raw) */
  settings: Record<string, unknown>;
  start?: NumberOrRef;
  passMark?: NumberOrRef;
}

/** 原樣物件區段(情境物件的複本;以 withPath 點路徑逐鍵修改)。 */
export type RawSection = Record<string, unknown>;

export interface ScenarioDraft {
  version: 2;
  /** 範本來源(情境 id;'' = 空白範本;顯示與 YAML 註解用) */
  template: string;
  general: DraftGeneral;
  ship: DraftShip;
  initial: DraftInitial;
  environment: DraftEnvironment;
  targets: DraftTarget[];
  /** 時間表項目(原樣物件;新增故障/一般項目、逐項編輯、上下移、刪除;repeat/cooldown_s/description 等未在表單的鍵保留) */
  timeline: RawSection[];
  assessment: DraftAssessment;
  /** 航線(route 物件原樣;name/xtdLimit_m/startLeg 與 waypoints[] 以 patchRoute / patchWaypoint 等編輯);null = 無航線 */
  route: RawSection | null;
  /** 區域(zone 物件原樣;id/name/type/polygon/waterDepth_m/ukcMin/speedLimit_kn) */
  zones: RawSection[];
  /** 簡報物件除 text 以外的鍵(roles、attachments、minParticipants、duration_s …;文字在 general.briefing;空物件 + 無文字 = 無簡報) */
  briefing: RawSection;
  /** 感測器常態參數(magneticCompassError_deg);null = 無區段 */
  sensors: RawSection | null;
  /** 警報門檻(escalate_s、silence_s、offHeading_deg、offHeadingDelay_s、headingMonitor_deg、shallowWater_m);null = 無區段 */
  alerts: RawSection | null;
  /** BNWAS(mode、dormant_s、stage2_s、stage3_s);null = 無區段 */
  bnwas: RawSection | null;
  /** 拖船(tug 物件原樣;id/name/bollardPull_t/attach/lineLength_m/modes/initial.*) */
  tugs: RawSection[];
  /** 錨初始狀態(port/starboard{length_m|length_shackles, bearing_deg}、holdingFactor、dragSpeed_mps);null = 無區段 */
  anchors: RawSection | null;
  /** 泊位(file/id/lines[]/departedDistance_m;definition 原樣保留);null = 無區段 */
  berth: RawSection | null;
  /** 岸壁(file/ids/scale;lines/definitions 原樣保留);null = 無區段 */
  banks: RawSection | null;
  /** 範本的完整情境物件(未涵蓋欄位由此保留;toScenarioObject 以草稿覆寫涵蓋欄位) */
  raw: Record<string, unknown>;
}

export const DRAFT_VERSION = 2;

// ------------------------------------------------------------------ 共用

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function clone<T>(v: T): T {
  return structuredClone(v);
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : '';
}

function finite(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

/** 數值欄位讀入:有限數字原樣、非空字串原樣(參數引用等),其他 undefined。 */
function numOrRef(v: unknown): NumberOrRef | undefined {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '') return v;
  return undefined;
}

/** 表單文字 → NumberOrRef:空白 = undefined;可解析為數字則為數字,否則原字串(參數引用、"2x")。 */
export function numberOrRefFromText(text: string): NumberOrRef | undefined {
  const s = text.trim();
  if (s === '') return undefined;
  const n = Number(s);
  return Number.isFinite(n) ? n : s;
}

/** NumberOrRef → 表單文字。 */
export function numberOrRefText(v: NumberOrRef | undefined): string {
  return v === undefined ? '' : String(v);
}

function setOrDelete(o: Record<string, unknown>, key: string, value: unknown): void {
  if (value === undefined || value === '') delete o[key];
  else o[key] = value;
}

function recordOf(v: unknown): Record<string, unknown> {
  return isRecord(v) ? { ...v } : {};
}

function readPosition(v: unknown): DraftPosition {
  const p: DraftPosition = {};
  if (!isRecord(v)) return p;
  const lat = numOrRef(v['lat']);
  const lon = numOrRef(v['lon']);
  const x = numOrRef(v['x']);
  const y = numOrRef(v['y']);
  if (lat !== undefined) p.lat = lat;
  if (lon !== undefined) p.lon = lon;
  if (x !== undefined) p.x = x;
  if (y !== undefined) p.y = y;
  return p;
}

function writePosition(p: DraftPosition, orig: unknown): Record<string, unknown> {
  const o = recordOf(orig);
  for (const k of ['lat', 'lon', 'x', 'y'] as const) {
    delete o[k];
    if (p[k] !== undefined) o[k] = p[k];
  }
  return o;
}

/** 位置是否完整(經緯度或 x/y 皆為數字)。 */
export function positionValid(p: DraftPosition): boolean {
  const n = (v: NumberOrRef | undefined) => typeof v === 'number';
  return (n(p.lat) && n(p.lon)) || (n(p.x) && n(p.y));
}

export type PositionMode = 'latlon' | 'enu';

export function positionMode(p: DraftPosition): PositionMode {
  return p.x !== undefined || p.y !== undefined ? (p.lat !== undefined && p.lon !== undefined ? 'latlon' : 'enu') : 'latlon';
}

// ------------------------------------------------------------------ 讀入

/** 由情境物件建立草稿(未涵蓋欄位保留在 raw)。 */
export function fromScenarioObject(obj: Record<string, unknown>, template = ''): ScenarioDraft {
  const briefing = obj['briefing'];
  const general: DraftGeneral = {
    id: str(obj['id']),
    name: str(obj['name']),
    description: str(obj['description']),
    briefing: typeof briefing === 'string' ? briefing : isRecord(briefing) ? str(briefing['text']) : '',
  };
  const seed = finite(obj['seed']);
  if (seed !== undefined) general.seed = Math.trunc(seed);
  const ts = finite(obj['timeScale']);
  if (ts !== undefined) general.timeScale = ts;
  const start = obj['startTimeUtc'];
  if (typeof start === 'string' && start.trim() !== '') general.startTimeUtc = start;
  else if (start instanceof Date) general.startTimeUtc = start.toISOString();

  const shipRaw = recordOf(obj['ship']);
  const ship: DraftShip = { id: (SHIP_IDS as readonly string[]).includes(str(shipRaw['id'])) ? (shipRaw['id'] as ShipId) : 'FSB1' };
  if (shipRaw['loading'] === 'full' || shipRaw['loading'] === 'ballast' || shipRaw['loading'] === 'intermediate') ship.loading = shipRaw['loading'];

  const initRaw = recordOf(obj['initial']);
  const initial: DraftInitial = { position: readPosition(initRaw['position']) };
  const heading = numOrRef(initRaw['heading']);
  if (heading !== undefined) initial.heading = heading;
  const speed = numOrRef(initRaw['speed']);
  if (speed !== undefined) initial.speed = speed;
  if ((TELEGRAPH_POSITIONS as readonly string[]).includes(str(initRaw['telegraph']))) initial.telegraph = initRaw['telegraph'] as TelegraphPosition;
  const rudder = numOrRef(initRaw['rudder']);
  if (rudder !== undefined) initial.rudder = rudder;

  const envRaw = recordOf(obj['environment']);
  const wind = recordOf(envRaw['wind']);
  const current = recordOf(envRaw['current']);
  const environment: DraftEnvironment = {};
  const envFields: Array<[keyof DraftEnvironment, unknown]> = [
    ['windSpeed', wind['trueSpeed']],
    ['windDir', wind['trueDir']],
    ['gustiness', wind['gustiness']],
    ['currentSet', current['set']],
    ['currentDrift', current['drift']],
    ['waterDepth', envRaw['waterDepth']],
    ['visibility_nm', envRaw['visibility_nm'] ?? envRaw['visibility']],
  ];
  for (const [k, v] of envFields) {
    const n = numOrRef(v);
    if (n !== undefined) environment[k] = n;
  }

  const targets: DraftTarget[] = (Array.isArray(obj['targets']) ? obj['targets'] : []).filter(isRecord).map(readTarget);
  const timeline: Record<string, unknown>[] = (Array.isArray(obj['timeline']) ? obj['timeline'] : []).filter(isRecord).map((t) => clone(t));

  const aRaw = recordOf(obj['assessment']);
  const settings: RawSection = {};
  for (const k of ASSESSMENT_SETTING_KEYS) if (aRaw[k] !== undefined) settings[k] = clone(aRaw[k]);
  const assessment: DraftAssessment = {
    hardFail: listOf(aRaw['hardFail']),
    bands: listOf(aRaw['bands']),
    required: listOf(aRaw['required']),
    settings,
  };
  const scoring = recordOf(aRaw['scoring']);
  const startScore = numOrRef(scoring['start']);
  if (startScore !== undefined) assessment.start = startScore;
  const pass = numOrRef(scoring['passMark']);
  if (pass !== undefined) assessment.passMark = pass;

  const briefingExtra: RawSection = isRecord(briefing) ? clone(briefing) : {};
  delete briefingExtra['text'];

  return {
    version: 2,
    template,
    general,
    ship,
    initial,
    environment,
    targets,
    timeline,
    assessment,
    route: sectionOf(obj['route']),
    zones: listOf(obj['zones']),
    briefing: briefingExtra,
    sensors: sectionOf(obj['sensors']),
    alerts: sectionOf(obj['alerts']),
    bnwas: sectionOf(obj['bnwas']),
    tugs: listOf(obj['tugs']),
    anchors: sectionOf(obj['anchors']),
    berth: sectionOf(obj['berth']),
    banks: sectionOf(obj['banks']),
    raw: clone(obj),
  };
}

/** 評估區段中以 settings 編輯的鍵(其餘如 parameterSet、scoring.appliesTo 留在 raw)。 */
export const ASSESSMENT_SETTING_KEYS: readonly string[] = ['minCpa_nm', 'encounter', 'ukc', 'manoeuvring'] as const;

function listOf(v: unknown): RawSection[] {
  return (Array.isArray(v) ? v : []).filter(isRecord).map((x) => clone(x));
}

function sectionOf(v: unknown): RawSection | null {
  return isRecord(v) ? clone(v) : null;
}

/** 非空物件才輸出(空物件或 null = 區段移除)。 */
function nonEmpty(v: RawSection | null | undefined): RawSection | undefined {
  return v && Object.keys(v).length > 0 ? clone(v) : undefined;
}

function readTarget(t: Record<string, unknown>): DraftTarget {
  const init = recordOf(t['initial']);
  const beh = recordOf(t['behaviour']);
  const out: DraftTarget = { id: str(t['id']), name: str(t['name']), shipType: str(t['shipType']), position: readPosition(init['position']), raw: clone(t) };
  const loa = numOrRef(t['loa']);
  if (loa !== undefined) out.loa = loa;
  const beam = numOrRef(t['beam']);
  if (beam !== undefined) out.beam = beam;
  const heading = numOrRef(init['heading']);
  if (heading !== undefined) out.heading = heading;
  const speed = numOrRef(init['speed']);
  if (speed !== undefined) out.speed = speed;
  if (typeof beh['mode'] === 'string' && beh['mode'].trim() !== '') out.mode = beh['mode'];
  return out;
}

/** 由 YAML 原文建立草稿;YAML 解析失敗或不是物件時丟 Error(由面板顯示)。 */
export function fromYaml(text: string, template = ''): ScenarioDraft {
  const raw = parseYaml(text);
  if (!isRecord(raw)) throw new Error('情境 YAML 不是物件');
  return fromScenarioObject(raw, template);
}

/** 空白範本:FSB1 壓載、台灣海峽示意位置、靜水、擱淺/碰撞硬性失敗、通過分數 70。 */
export function blankScenarioObject(): Record<string, unknown> {
  return {
    id: 'NEW_exercise',
    name: '新練習',
    description: '',
    seed: 1,
    timeScale: 1,
    ship: { id: 'FSB1', loading: 'ballast' },
    initial: { position: { lat: 23.9, lon: 119.98 }, heading: 20, speed: 8, telegraph: 'HAH', rudder: 0 },
    environment: { wind: { trueSpeed: 10, trueDir: 45, gustiness: 0.1 }, current: { set: 20, drift: 0.5 }, waterDepth: 40, visibility_nm: 10 },
    briefing: '',
    targets: [],
    timeline: [],
    assessment: {
      parameterSet: 'data/parameters/company-procedures.yaml',
      hardFail: [
        { id: 'grounding', rule: 'grounding' },
        { id: 'collision', rule: 'collision' },
      ],
      bands: [],
      scoring: { start: 100, passMark: 70 },
    },
  };
}

export function blankDraft(): ScenarioDraft {
  return fromScenarioObject(blankScenarioObject(), '');
}

// ------------------------------------------------------------------ 輸出

function subObject(orig: unknown, fields: Record<string, NumberOrRef | undefined>): Record<string, unknown> | undefined {
  const o = recordOf(orig);
  for (const [k, v] of Object.entries(fields)) setOrDelete(o, k, v);
  return Object.keys(o).length > 0 ? o : undefined;
}

function targetToObject(t: DraftTarget): Record<string, unknown> {
  const o = { ...t.raw };
  o['id'] = t.id;
  setOrDelete(o, 'name', t.name);
  setOrDelete(o, 'shipType', t.shipType);
  setOrDelete(o, 'loa', t.loa);
  setOrDelete(o, 'beam', t.beam);
  const init = recordOf(o['initial']);
  init['position'] = writePosition(t.position, init['position']);
  setOrDelete(init, 'heading', t.heading);
  setOrDelete(init, 'speed', t.speed);
  o['initial'] = init;
  const beh = recordOf(o['behaviour']);
  setOrDelete(beh, 'mode', t.mode);
  if (Object.keys(beh).length > 0) o['behaviour'] = beh;
  else delete o['behaviour'];
  return o;
}

/** 草稿 → 情境物件(與 YAML 結構相同;loadScenario args.scenario):raw 的複本,涵蓋欄位以草稿覆寫。 */
export function toScenarioObject(draft: ScenarioDraft): Record<string, unknown> {
  const out = clone(draft.raw);
  const g = draft.general;
  setOrDelete(out, 'id', g.id.trim());
  setOrDelete(out, 'name', g.name);
  setOrDelete(out, 'description', g.description);
  setOrDelete(out, 'seed', g.seed);
  setOrDelete(out, 'timeScale', g.timeScale);
  setOrDelete(out, 'startTimeUtc', g.startTimeUtc);
  // 簡報:範本為物件或草稿有角色/附件等鍵 → 物件(text 在前);否則字串(無文字 = 無簡報)
  if (isRecord(out['briefing']) || Object.keys(draft.briefing).length > 0) {
    const b: RawSection = { text: g.briefing, ...clone(draft.briefing) };
    setOrDelete(b, 'text', g.briefing);
    if (Object.keys(b).length === 0) delete out['briefing'];
    else out['briefing'] = b;
  } else setOrDelete(out, 'briefing', g.briefing);

  const ship = recordOf(out['ship']);
  ship['id'] = draft.ship.id;
  setOrDelete(ship, 'loading', draft.ship.loading);
  out['ship'] = ship;

  const init = recordOf(out['initial']);
  init['position'] = writePosition(draft.initial.position, init['position']);
  setOrDelete(init, 'heading', draft.initial.heading);
  setOrDelete(init, 'speed', draft.initial.speed);
  setOrDelete(init, 'telegraph', draft.initial.telegraph);
  setOrDelete(init, 'rudder', draft.initial.rudder);
  out['initial'] = init;

  const e = draft.environment;
  const env = recordOf(out['environment']);
  setOrDelete(env, 'wind', subObject(env['wind'], { trueSpeed: e.windSpeed, trueDir: e.windDir, gustiness: e.gustiness }));
  setOrDelete(env, 'current', subObject(env['current'], { set: e.currentSet, drift: e.currentDrift }));
  setOrDelete(env, 'waterDepth', e.waterDepth);
  delete env['visibility'];
  setOrDelete(env, 'visibility_nm', e.visibility_nm);
  out['environment'] = env;

  if (draft.targets.length > 0 || 'targets' in out) out['targets'] = draft.targets.map(targetToObject);
  if (draft.timeline.length > 0 || 'timeline' in out) out['timeline'] = clone(draft.timeline);

  const a = recordOf(out['assessment']);
  const as = draft.assessment;
  if (as.hardFail.length > 0 || 'hardFail' in a) a['hardFail'] = clone(as.hardFail);
  if (as.bands.length > 0 || 'bands' in a) a['bands'] = clone(as.bands);
  if (as.required.length > 0 || 'required' in a) a['required'] = clone(as.required);
  for (const k of ASSESSMENT_SETTING_KEYS) {
    const v = as.settings[k];
    setOrDelete(a, k, isRecord(v) ? nonEmpty(v) : v);
  }
  setOrDelete(a, 'scoring', subObject(a['scoring'], { start: as.start, passMark: as.passMark }));
  if (Object.keys(a).length > 0 || 'assessment' in out) out['assessment'] = a;

  setOrDelete(out, 'route', nonEmpty(draft.route));
  if (draft.zones.length > 0 || 'zones' in out) out['zones'] = clone(draft.zones);
  setOrDelete(out, 'sensors', nonEmpty(draft.sensors));
  setOrDelete(out, 'alerts', nonEmpty(draft.alerts));
  setOrDelete(out, 'bnwas', nonEmpty(draft.bnwas));
  if (draft.tugs.length > 0 || 'tugs' in out) out['tugs'] = clone(draft.tugs);
  setOrDelete(out, 'anchors', nonEmpty(draft.anchors));
  setOrDelete(out, 'berth', nonEmpty(draft.berth));
  setOrDelete(out, 'banks', nonEmpty(draft.banks));
  return out;
}

/** 會被 YAML 1.1 解析器(PyYAML)誤判型別、或與 data/scenarios 慣例一致應加引號的純量字串。 */
const QUOTE_RE =
  /^(?:[-+]?(?:[0-9][0-9_]*)?(?:\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|0[xob][0-9a-fA-F_]+|[-+]?\.(?:inf|Inf|INF|nan|NaN|NAN)|~|null|Null|NULL|y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|\d{4}-\d{1,2}-\d{1,2}.*|\$\{params:[^}]+\}|[0-9]+:[0-9:.]+)$/;

/** 情境物件 → YAML 文字(區塊式;不折行;多行字串用 | 區塊;易誤判的字串值加雙引號,鍵不加)。 */
export function scenarioToYaml(scenario: Record<string, unknown>): string {
  const doc = new Document(scenario);
  visit(doc, {
    Scalar(key, node) {
      if (key === 'key') return;
      if (typeof node.value === 'string' && (node.value === '' || QUOTE_RE.test(node.value))) node.type = Scalar.QUOTE_DOUBLE;
    },
  });
  return doc.toString({ lineWidth: 0 });
}

/** 草稿 → 可下載的 YAML(檔頭註解:名稱、來源範本)。 */
export function toYaml(draft: ScenarioDraft): string {
  const scenario = toScenarioObject(draft);
  const head = `# ${str(scenario['name']) || str(scenario['id'])}\n# 由教官站情境編輯器產生(規劃書第 9.1 節;src/Instruments.Web/src/lib/scenario-editor.ts)${draft.template ? `;範本 ${draft.template}` : ';空白範本'}\n`;
  return head + scenarioToYaml(scenario);
}

// ------------------------------------------------------------------ 目標船

export function newTarget(draft: ScenarioDraft): DraftTarget {
  const n = draft.targets.length + 1;
  let id = `T${n}`;
  for (let i = n; draft.targets.some((t) => t.id === id); i++) id = `T${i + 1}`;
  const own = draft.initial.position;
  const position: DraftPosition = typeof own.lat === 'number' && typeof own.lon === 'number' ? { lat: Math.round((own.lat + 0.03) * 1e4) / 1e4, lon: own.lon } : { x: 0, y: 3000 };
  return { id, name: '', shipType: 'cargo', loa: 100, beam: 16, position, heading: 180, speed: 8, mode: 'hold', raw: {} };
}

export function addTarget(draft: ScenarioDraft, target: DraftTarget = newTarget(draft)): ScenarioDraft {
  return { ...draft, targets: [...draft.targets, target] };
}

export function removeTarget(draft: ScenarioDraft, index: number): ScenarioDraft {
  return { ...draft, targets: draft.targets.filter((_, i) => i !== index) };
}

export function patchTarget(draft: ScenarioDraft, index: number, patch: Patch<Omit<DraftTarget, 'raw'>>): ScenarioDraft {
  return { ...draft, targets: draft.targets.map((t, i) => (i === index ? (stripUndefined({ ...t, ...patch }) as DraftTarget) : t)) };
}

/** 移除值為 undefined 的鍵(就地;patch 後的物件)。 */
export function stripUndefined<T extends object>(o: T): T {
  for (const k of Object.keys(o) as Array<keyof T>) if (o[k] === undefined) delete o[k];
  return o;
}

// ------------------------------------------------------------------ 時間表故障

export type FaultTrigger = { kind: 'time'; atLeast: number } | { kind: 'manual' } | { kind: 'condition'; when: Record<string, unknown> };

export interface FaultItemSpec {
  id: string;
  name?: string;
  /** 故障名稱(src/Contracts/fault-catalogue.json) */
  fault: string;
  args?: Record<string, unknown>;
  /** 情境作者明示只記錄(核心不改變行為,由語音/訊息腳本反應) */
  recordOnly?: boolean;
  trigger: FaultTrigger;
  /** 觸發時把情境階段設為此值(normal/degraded/emergency) */
  phase?: string;
  /** 給教官的訊息(選填;message 動作) */
  message?: string;
}

export const PHASES: readonly string[] = ['degraded', 'emergency', 'normal'] as const;

/** 故障項目的時間表物件(schema $defs.timelineItem;injectFault 字串或 {name, args, recordOnly})。 */
export function faultTimelineItem(spec: FaultItemSpec): Record<string, unknown> {
  const item: Record<string, unknown> = { id: spec.id.trim() };
  if (spec.name?.trim()) item['name'] = spec.name.trim();
  if (spec.phase?.trim()) item['phase'] = spec.phase.trim();
  if (spec.trigger.kind === 'manual') item['manual'] = true;
  else if (spec.trigger.kind === 'time') item['when'] = { type: 'time', atLeast: spec.trigger.atLeast };
  else item['when'] = clone(spec.trigger.when);
  const inject: Record<string, unknown> = { name: spec.fault.trim() };
  if (spec.args && Object.keys(spec.args).length > 0) inject['args'] = clone(spec.args);
  if (spec.recordOnly) inject['recordOnly'] = true;
  const actions: Record<string, unknown>[] = [{ injectFault: Object.keys(inject).length === 1 ? spec.fault.trim() : inject }];
  if (spec.message?.trim()) actions.push({ message: { to: 'instructor', text: spec.message.trim(), severity: 'warning' } });
  item['actions'] = actions;
  return item;
}

export function addFaultItem(draft: ScenarioDraft, spec: FaultItemSpec): ScenarioDraft {
  return { ...draft, timeline: [...draft.timeline, faultTimelineItem(spec)] };
}

export function removeTimelineItem(draft: ScenarioDraft, index: number): ScenarioDraft {
  return { ...draft, timeline: draft.timeline.filter((_, i) => i !== index) };
}

/** 解析表單輸入的 YAML/JSON 片段為物件(觸發條件、故障參數);空白回 null、不是物件時丟 Error。 */
export function parseObjectText(text: string, what: string): Record<string, unknown> | null {
  if (text.trim() === '') return null;
  const v = parseYaml(text);
  if (!isRecord(v)) throw new Error(`${what} 需為 YAML/JSON 物件`);
  return v;
}

export interface TimelineRow {
  index: number;
  id: string;
  name: string;
  /** 觸發條件摘要(手動 / 條件) */
  condition: string;
  actions: string;
  /** 含 injectFault 動作(編輯器新增的故障或範本既有的故障項目) */
  fault: boolean;
  faultNames: string[];
  issues: string[];
}

/** 時間表清單的顯示列(以 lib/timeline.ts 的解析與摘要)。 */
export function describeTimeline(items: readonly Record<string, unknown>[]): TimelineRow[] {
  return items.map((raw, index) => {
    const issues: string[] = [];
    const it = parseTimelineItem(raw, index, issues);
    if (!it) return { index, id: str(raw['id']) || `item${index}`, name: '', condition: '?', actions: '', fault: false, faultNames: [], issues };
    const faultNames = it.actions.filter((a) => a.kind === 'injectFault').map((a) => (typeof a.value === 'string' ? a.value : isRecord(a.value) ? str(a.value['name']) : ''));
    return {
      index,
      id: it.id,
      name: it.name,
      condition: it.manual ? '手動(教官觸發)' : conditionSummary(it.when),
      actions: it.actions.map(actionSummary).join('、'),
      fault: faultNames.length > 0,
      faultNames,
      issues,
    };
  });
}

/** 故障目錄(fault-catalogue.json;表單選單用)。 */
export function faultOptions(): FaultDefinition[] {
  return faultCatalogue().faults;
}

// ------------------------------------------------------------------ 評估

/** 不需參數的硬性失敗規則(開關)。 */
export const SIMPLE_HARD_FAILS: readonly string[] = ['grounding', 'collision'] as const;
export const HARD_FAIL_RULES: readonly string[] = ['grounding', 'collision', 'cpa', 'ukc', 'zoneEntered', 'event'] as const;

/** 容許帶指標建議(schema bandRule 說明與 summary 欄位)。 */
export const BAND_METRICS: readonly string[] = [
  'track.maxXtd_m',
  'track.timeOutsideCorridor_s',
  'ukc.min_m',
  'ukc.timeBelowLimit_s',
  'collisionAvoidance.minCpa_nm',
  'manoeuvring.timeOverSpeedLimit_s',
  'manoeuvring.maxRot_degPerMin',
  'manoeuvring.timeRotOverLimit_s',
  'manoeuvring.hardOverCount',
  'faultResponse.firstAction_s',
  'berthing.maxLateralSpeed_kn',
  'berthing.allFastPositionError_m',
  'anchoring.maxLetGoSog_kn',
] as const;

export function hasHardFail(draft: ScenarioDraft, rule: string): boolean {
  return draft.assessment.hardFail.some((h) => h['rule'] === rule);
}

/** 開關型硬性失敗:開 = 加 {id: rule, rule}(已有則不重複);關 = 移除該 rule 的所有規則。 */
export function setHardFail(draft: ScenarioDraft, rule: string, on: boolean): ScenarioDraft {
  const list = draft.assessment.hardFail;
  if (on) return hasHardFail(draft, rule) ? draft : { ...draft, assessment: { ...draft.assessment, hardFail: [...list, { id: rule, rule }] } };
  return { ...draft, assessment: { ...draft.assessment, hardFail: list.filter((h) => h['rule'] !== rule) } };
}

export function addHardFail(draft: ScenarioDraft, rule: Record<string, unknown>): ScenarioDraft {
  return { ...draft, assessment: { ...draft.assessment, hardFail: [...draft.assessment.hardFail, clone(rule)] } };
}

export function removeHardFail(draft: ScenarioDraft, index: number): ScenarioDraft {
  return { ...draft, assessment: { ...draft.assessment, hardFail: draft.assessment.hardFail.filter((_, i) => i !== index) } };
}

/** 硬性失敗規則一句話(HARD_FAIL_RULE_LABEL 在 assessment.ts;這裡附參數)。 */
export function hardFailText(h: Record<string, unknown>): string {
  const rule = str(h['rule']);
  const lt = h['lessThan'];
  switch (rule) {
    case 'cpa':
      return `CPA < ${str(lt)} nm`;
    case 'ukc':
      return isRecord(lt) ? (lt['metres'] !== undefined ? `UKC < ${str(lt['metres'])} m` : `UKC < 吃水 × ${str(lt['fractionOfDraft'])}`) : `UKC < ${str(lt)} m`;
    case 'zoneEntered':
      return `進入區域 ${str(h['zoneId'])}`;
    case 'event':
      return `事件 ${str(h['kind'] ?? (isRecord(h['event']) ? h['event']['kind'] : ''))}`;
    case 'grounding':
      return '擱淺';
    case 'collision':
      return '碰撞';
    default:
      return rule || '?';
  }
}

export function newBand(): Record<string, unknown> {
  return { id: '', metric: 'track.maxXtd_m', warn: 50, fail: '2x', penalty: 10, failPenalty: 25 };
}

export function addBand(draft: ScenarioDraft, band: Record<string, unknown> = newBand()): ScenarioDraft {
  return { ...draft, assessment: { ...draft.assessment, bands: [...draft.assessment.bands, clone(band)] } };
}

export function removeBand(draft: ScenarioDraft, index: number): ScenarioDraft {
  return { ...draft, assessment: { ...draft.assessment, bands: draft.assessment.bands.filter((_, i) => i !== index) } };
}

/** 改容許帶欄位(undefined / '' = 刪除該鍵;direction above 為預設,存 'above' 時刪鍵)。 */
export function patchBand(draft: ScenarioDraft, index: number, key: string, value: unknown): ScenarioDraft {
  return {
    ...draft,
    assessment: {
      ...draft.assessment,
      bands: draft.assessment.bands.map((b, i) => {
        if (i !== index) return b;
        const o = { ...b };
        setOrDelete(o, key, key === 'direction' && value === 'above' ? undefined : value);
        return o;
      }),
    },
  };
}

// ------------------------------------------------------------------ 點路徑與清單(原樣物件區段共用)

/** 讀巢狀鍵('after.event');不存在回 undefined。 */
export function getPath(o: RawSection | null | undefined, path: string): unknown {
  let cur: unknown = o;
  for (const k of path.split('.')) {
    if (!isRecord(cur)) return undefined;
    cur = cur[k];
  }
  return cur;
}

/** 以點路徑設定巢狀鍵(回傳複本;值 undefined / '' = 刪除該鍵,刪後空的父物件一併移除;中間鍵不存在時建立)。 */
export function withPath(o: RawSection | null | undefined, path: string, value: unknown): RawSection {
  const out = recordOf(o);
  const dot = path.indexOf('.');
  if (dot < 0) {
    setOrDelete(out, path, value);
    return out;
  }
  const head = path.slice(0, dot);
  const child = withPath(recordOf(out[head]), path.slice(dot + 1), value);
  if (Object.keys(child).length === 0) delete out[head];
  else out[head] = child;
  return out;
}

/** 清單項目上/下移(delta −1 / +1;越界不變)。 */
export function moveAt<T>(list: readonly T[], index: number, delta: number): T[] {
  const j = index + delta;
  const out = [...list];
  if (index < 0 || index >= list.length || j < 0 || j >= list.length) return out;
  const [item] = out.splice(index, 1);
  out.splice(j, 0, item as T);
  return out;
}

function patchAt(list: readonly RawSection[], index: number, path: string, value: unknown): RawSection[] {
  return list.map((x, i) => (i === index ? withPath(x, path, value) : x));
}

function removeAt<T>(list: readonly T[], index: number): T[] {
  return list.filter((_, i) => i !== index);
}

/** 清單內唯一 id:prefix + 流水號(從 n 起找第一個未用的)。 */
export function uniqueId(prefix: string, used: Iterable<string>, start = 1): string {
  const set = new Set(used);
  for (let i = start; ; i++) if (!set.has(`${prefix}${i}`)) return `${prefix}${i}`;
}

function round4(v: number): number {
  return Math.round(v * 1e4) / 1e4;
}

/** 表單文字 → 整數(空白或非數 undefined)。 */
export function intFromText(text: string): number | undefined {
  const n = Number(text.trim());
  return text.trim() === '' || !Number.isFinite(n) ? undefined : Math.trunc(n);
}

/** 任意值 → 單行流式 YAML(物件/陣列為 { } / [ ];字串、數字原樣;form 輸入框用;parseValueText 可讀回同值)。 */
export function flowYaml(value: unknown): string {
  if (value === undefined || value === null) return '';
  const doc = new Document(value);
  if (isCollection(doc.contents)) doc.contents.flow = true;
  return doc.toString({ lineWidth: 0 }).trim();
}

/** 表單輸入的 YAML/JSON(純量或物件)→ 值;空白回 undefined;解析失敗丟 Error。 */
export function parseValueText(text: string): unknown {
  if (text.trim() === '') return undefined;
  return parseYaml(text);
}

/** 原點是否可用(ENU x/y 座標的航點/區域/頂點需要 origin,或初始位置為經緯度時核心以它為原點)。 */
export function hasOrigin(draft: ScenarioDraft): boolean {
  const p = draft.initial.position;
  return isRecord(draft.raw['origin']) || (typeof p.lat === 'number' && typeof p.lon === 'number');
}

// ------------------------------------------------------------------ 航線 route

/** 航點數值欄位(作用於到達該航點的那一段;arrivalRadius_m 為到達半徑)。 */
export const WAYPOINT_FIELDS: ReadonlyArray<readonly [key: string, label: string, unit: string]> = [
  ['speedLimit_kn', '速限', 'kn'],
  ['xtdLimit_m', '走廊半寬', 'm'],
  ['arrivalRadius_m', '到達半徑', 'm'],
] as const;

export function routeWaypoints(draft: ScenarioDraft): RawSection[] {
  const w = draft.route?.['waypoints'];
  return Array.isArray(w) ? w.filter(isRecord) : [];
}

function setWaypoints(draft: ScenarioDraft, waypoints: RawSection[]): ScenarioDraft {
  return { ...draft, route: { ...recordOf(draft.route), waypoints } };
}

/** 新航點:名稱 WPn 唯一;位置接在最後一個航點北方 0.02°(ENU 則 +2000 m),第一個航點取初始位置。 */
export function newWaypoint(draft: ScenarioDraft): RawSection {
  const wps = routeWaypoints(draft);
  const name = uniqueId('WP', wps.map((w) => str(w['name'])), wps.length + 1);
  const last = wps[wps.length - 1];
  const p = last ? readPosition(last) : draft.initial.position;
  let position: DraftPosition;
  if (typeof p.lat === 'number' && typeof p.lon === 'number') position = last ? { lat: round4(p.lat + 0.02), lon: p.lon } : { lat: p.lat, lon: p.lon };
  else if (typeof p.x === 'number' && typeof p.y === 'number') position = last ? { x: p.x, y: p.y + 2000 } : { x: p.x, y: p.y };
  else position = { lat: 23.9, lon: 119.98 };
  return writePosition(position, { name });
}

/** 建立航線(name 空、2 個航點)或移除整個 route。 */
export function setRouteEnabled(draft: ScenarioDraft, on: boolean): ScenarioDraft {
  if (!on) return { ...draft, route: null };
  if (draft.route) return draft;
  return addWaypoint(addWaypoint({ ...draft, route: { name: '', waypoints: [] } }));
}

/** 改航線欄位(name、xtdLimit_m、startLeg;空值刪鍵)。 */
export function patchRoute(draft: ScenarioDraft, path: string, value: unknown): ScenarioDraft {
  return { ...draft, route: withPath(draft.route, path, value) };
}

export function addWaypoint(draft: ScenarioDraft, wp: RawSection = newWaypoint(draft)): ScenarioDraft {
  return setWaypoints(draft, [...routeWaypoints(draft), wp]);
}

export function removeWaypoint(draft: ScenarioDraft, index: number): ScenarioDraft {
  return setWaypoints(draft, removeAt(routeWaypoints(draft), index));
}

export function moveWaypoint(draft: ScenarioDraft, index: number, delta: number): ScenarioDraft {
  return setWaypoints(draft, moveAt(routeWaypoints(draft), index, delta));
}

/** 改航點欄位(name、speedLimit_kn、xtdLimit_m、arrivalRadius_m、checkpoint、report;空值/undefined 刪鍵)。 */
export function patchWaypoint(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return setWaypoints(draft, patchAt(routeWaypoints(draft), index, path, value));
}

/** 改航點位置(lat/lon 或 x/y;其餘鍵保留)。 */
export function patchWaypointPosition(draft: ScenarioDraft, index: number, position: DraftPosition): ScenarioDraft {
  return setWaypoints(
    draft,
    routeWaypoints(draft).map((w, i) => (i === index ? writePosition(position, w) : w)),
  );
}

// ------------------------------------------------------------------ 區域 zones

export const ZONE_TYPE_LABEL: Readonly<Record<string, string>> = {
  noGo: '禁航區',
  speedLimit: '速限區',
  ukcPolicy: 'UKC 政策 / 水深覆寫',
  reporting: '報告區',
};

export type UkcLimitKind = 'metres' | 'fractionOfDraft';
export const UKC_LIMIT_KINDS: readonly UkcLimitKind[] = ['metres', 'fractionOfDraft'] as const;

/** UKC 門檻物件 {metres} | {fractionOfDraft} → 表單(無 = kind ''、value undefined)。 */
export function ukcLimitForm(v: unknown): { kind: UkcLimitKind | ''; value: NumberOrRef | undefined } {
  if (!isRecord(v)) return { kind: '', value: undefined };
  for (const k of UKC_LIMIT_KINDS) {
    const n = numOrRef(v[k]);
    if (n !== undefined) return { kind: k, value: n };
  }
  return { kind: '', value: undefined };
}

/** 表單 → UKC 門檻物件(保留其他鍵;kind 空或無值 = undefined 刪鍵)。 */
export function ukcLimitValue(orig: unknown, kind: UkcLimitKind | '', value: NumberOrRef | undefined): RawSection | undefined {
  const o = recordOf(orig);
  for (const k of UKC_LIMIT_KINDS) delete o[k];
  if (kind && value !== undefined) o[kind] = value;
  return Object.keys(o).length > 0 ? o : undefined;
}

export function zonePolygon(zone: RawSection): RawSection[] {
  const p = zone['polygon'];
  return Array.isArray(p) ? p.filter(isRecord) : [];
}

/** 新區域:id zoneN 唯一、noGo、以初始位置為中心的四邊形(經緯度 ±0.01°;ENU ±500 m)。 */
export function newZone(draft: ScenarioDraft): RawSection {
  const id = uniqueId('zone', draft.zones.map((z) => str(z['id'])), draft.zones.length + 1);
  const c = draft.initial.position;
  const polygon: RawSection[] =
    typeof c.lat === 'number' && typeof c.lon === 'number'
      ? [
          { lat: round4(c.lat + 0.01), lon: round4(c.lon - 0.01) },
          { lat: round4(c.lat + 0.01), lon: round4(c.lon + 0.01) },
          { lat: round4(c.lat - 0.01), lon: round4(c.lon + 0.01) },
          { lat: round4(c.lat - 0.01), lon: round4(c.lon - 0.01) },
        ]
      : [
          { x: -500, y: -500 },
          { x: 500, y: -500 },
          { x: 500, y: 500 },
          { x: -500, y: 500 },
        ];
  return { id, name: '', type: 'noGo', polygon };
}

export function addZone(draft: ScenarioDraft, zone: RawSection = newZone(draft)): ScenarioDraft {
  return { ...draft, zones: [...draft.zones, zone] };
}

export function removeZone(draft: ScenarioDraft, index: number): ScenarioDraft {
  return { ...draft, zones: removeAt(draft.zones, index) };
}

/** 改區域欄位(id、name、type、waterDepth_m、speedLimit_kn、ukcMin;空值刪鍵)。 */
export function patchZone(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return { ...draft, zones: patchAt(draft.zones, index, path, value) };
}

export function setZoneUkcMin(draft: ScenarioDraft, index: number, kind: UkcLimitKind | '', value: NumberOrRef | undefined): ScenarioDraft {
  return patchZone(draft, index, 'ukcMin', ukcLimitValue(draft.zones[index]?.['ukcMin'], kind, value));
}

function setZonePolygon(draft: ScenarioDraft, index: number, polygon: RawSection[]): ScenarioDraft {
  return { ...draft, zones: draft.zones.map((z, i) => (i === index ? { ...z, polygon } : z)) };
}

/** 新增頂點:接在最後一點之後(複製最後一點稍作偏移,讓表格有可編輯的列)。 */
export function addZoneVertex(draft: ScenarioDraft, index: number): ScenarioDraft {
  const zone = draft.zones[index];
  if (!zone) return draft;
  const poly = zonePolygon(zone);
  const last = poly[poly.length - 1];
  const p = last ? readPosition(last) : draft.initial.position;
  const next: DraftPosition =
    typeof p.lat === 'number' && typeof p.lon === 'number' ? { lat: round4(p.lat + 0.005), lon: p.lon } : typeof p.x === 'number' && typeof p.y === 'number' ? { x: p.x, y: p.y + 500 } : { lat: 23.9, lon: 119.98 };
  return setZonePolygon(draft, index, [...poly, writePosition(next, {})]);
}

export function removeZoneVertex(draft: ScenarioDraft, index: number, vertex: number): ScenarioDraft {
  const zone = draft.zones[index];
  return zone ? setZonePolygon(draft, index, removeAt(zonePolygon(zone), vertex)) : draft;
}

export function patchZoneVertex(draft: ScenarioDraft, index: number, vertex: number, position: DraftPosition): ScenarioDraft {
  const zone = draft.zones[index];
  return zone ? setZonePolygon(draft, index, zonePolygon(zone).map((v, j) => (j === vertex ? writePosition(position, v) : v))) : draft;
}

// ------------------------------------------------------------------ 時間表項目編輯

export type TriggerKind = FaultTrigger['kind'];

export interface TimelineItemForm {
  id: string;
  name: string;
  phase: string;
  /** '' = 預設 instructor */
  visibility: string;
  /** '' = 預設啟用;'false' = 需 arm 啟用;'true' = 明示啟用 */
  armed: string;
  trigger: TriggerKind;
  /** 模擬時間觸發的秒數(trigger time) */
  atLeast: string;
  /** 自訂條件的流式 YAML(trigger condition) */
  condition: string;
  holdFor_s: string;
  delay_s: string;
  actions: Array<{ kind: string; value: string }>;
  /** 未在表單的鍵(repeat、cooldown_s、description …;原樣保留) */
  kept: string[];
}

export const TIMELINE_FORM_KEYS: readonly string[] = ['id', 'name', 'phase', 'visibility', 'armed', 'manual', 'when', 'holdFor_s', 'delay_s', 'actions'] as const;

/** 項目的觸發:manual → 手動;when 只有 {type: time, atLeast} → 時間;其他 → 自訂條件(無 when 時 when 為空物件)。 */
export function timelineItemTrigger(item: RawSection): FaultTrigger {
  if (item['manual'] === true) return { kind: 'manual' };
  const when = recordOf(item['when']);
  const keys = Object.keys(when);
  if (when['type'] === 'time' && typeof when['atLeast'] === 'number' && keys.length === 2) return { kind: 'time', atLeast: when['atLeast'] };
  return { kind: 'condition', when };
}

export function timelineActions(item: RawSection): RawSection[] {
  const a = item['actions'];
  return Array.isArray(a) ? a.filter(isRecord) : [];
}

/** 時間表項目 → 表單欄位(字串;動作值為流式 YAML)。 */
export function timelineItemForm(item: RawSection): TimelineItemForm {
  const trigger = timelineItemTrigger(item);
  const armed = item['armed'];
  return {
    id: str(item['id']),
    name: str(item['name']),
    phase: str(item['phase']),
    visibility: str(item['visibility']),
    armed: armed === false ? 'false' : armed === true ? 'true' : '',
    trigger: trigger.kind,
    atLeast: trigger.kind === 'time' ? String(trigger.atLeast) : '',
    condition: trigger.kind === 'condition' ? flowYaml(trigger.when) : '',
    holdFor_s: numberOrRefText(numOrRef(item['holdFor_s'])),
    delay_s: numberOrRefText(numOrRef(item['delay_s'])),
    actions: timelineActions(item).map((a) => {
      const kind = Object.keys(a)[0] ?? '';
      return { kind, value: flowYaml(a[kind]) };
    }),
    kept: Object.keys(item).filter((k) => !TIMELINE_FORM_KEYS.includes(k)),
  };
}

/** 改項目欄位(id、name、phase、visibility、holdFor_s、delay_s;armed 以 'true'/'false'/'' 表單值轉布林;空值刪鍵)。 */
export function patchTimelineItem(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  const v = path === 'armed' ? (value === 'true' ? true : value === 'false' ? false : undefined) : value;
  return { ...draft, timeline: patchAt(draft.timeline, index, path, v) };
}

/** 改項目觸發:manual → manual: true 並移除 when;time/condition → 設 when、移除 manual。 */
export function setTimelineTrigger(draft: ScenarioDraft, index: number, trigger: FaultTrigger): ScenarioDraft {
  return {
    ...draft,
    timeline: draft.timeline.map((it, i) => {
      if (i !== index) return it;
      const o = { ...it };
      if (trigger.kind === 'manual') {
        o['manual'] = true;
        delete o['when'];
      } else {
        delete o['manual'];
        o['when'] = trigger.kind === 'time' ? { type: 'time', atLeast: trigger.atLeast } : clone(trigger.when);
      }
      return o;
    }),
  };
}

export function moveTimelineItem(draft: ScenarioDraft, index: number, delta: number): ScenarioDraft {
  return { ...draft, timeline: moveAt(draft.timeline, index, delta) };
}

function setActions(draft: ScenarioDraft, index: number, actions: RawSection[]): ScenarioDraft {
  return { ...draft, timeline: draft.timeline.map((it, i) => (i === index ? { ...it, actions } : it)) };
}

/** 改動作(單鍵物件 {kind: value};換 kind 時 value 由表單另給)。 */
export function setTimelineAction(draft: ScenarioDraft, index: number, action: number, kind: string, value: unknown): ScenarioDraft {
  const item = draft.timeline[index];
  if (!item) return draft;
  return setActions(
    draft,
    index,
    timelineActions(item).map((a, j) => (j === action ? { [kind]: clone(value) } : a)),
  );
}

export function addTimelineAction(draft: ScenarioDraft, index: number, kind: string = 'message', value: unknown = newActionValue(kind, draft)): ScenarioDraft {
  const item = draft.timeline[index];
  return item ? setActions(draft, index, [...timelineActions(item), { [kind]: clone(value) }]) : draft;
}

export function removeTimelineAction(draft: ScenarioDraft, index: number, action: number): ScenarioDraft {
  const item = draft.timeline[index];
  return item ? setActions(draft, index, removeAt(timelineActions(item), action)) : draft;
}

export function moveTimelineAction(draft: ScenarioDraft, index: number, action: number, delta: number): ScenarioDraft {
  const item = draft.timeline[index];
  return item ? setActions(draft, index, moveAt(timelineActions(item), action, delta)) : draft;
}

/** 一般時間表項目(id itemN 唯一;模擬時間 60 s 觸發;一則給教官的訊息)。 */
export function newTimelineItem(draft: ScenarioDraft): RawSection {
  const id = uniqueId('item', draft.timeline.map((t) => str(t['id'])), draft.timeline.length + 1);
  return { id, when: { type: 'time', atLeast: 60 }, actions: [{ message: { to: 'instructor', text: '', severity: 'info' } }] };
}

export function addTimelineItem(draft: ScenarioDraft, item: RawSection = newTimelineItem(draft)): ScenarioDraft {
  return { ...draft, timeline: [...draft.timeline, item] };
}

/** 動作參數提示(schema $defs.timelineAction 說明;表單 placeholder / 提示列)。 */
export const ACTION_ARG_HINTS: Readonly<Record<TimelineActionKind, string>> = {
  injectFault: '故障名稱,或 { name, args?, recordOnly? }',
  clearFault: '故障名稱,或 all',
  setEnvironment: '{ wind: { trueSpeed, trueDir, gustiness }, current: { set, drift }, waterDepth, visibility_nm, ramp_s }',
  targetControl: '{ targetId, heading?, speed?, release?, position?, waypoints?, aisOn?, behaviour?, colreg?, activate?, remove? }',
  voiceScript: '{ role, channel: bridge|vhfPort|vhf16|phone, text?, file?, lang?, durationEstimate_s? }',
  message: '{ to: instructor|all, text, severity: info|warning|alarm }',
  phase: '階段名稱(normal | degraded | emergency)',
  freeze: '{ reason? }',
  autopilot: '{ enabled, heading?, rotLimit? }',
  ownShip: '{ rudder?, telegraph?, rpm?, thruster? }',
  timeScale: '倍率數值',
  arm: '時間表項目 id',
  disarm: '時間表項目 id',
  endExercise: '{ reason? }',
  tugControl: '{ id, mode: push|pull|standby|free|unavailable, direction_deg?, power_pct? }',
};

export const ACTION_KINDS: readonly string[] = TIMELINE_ACTION_KINDS;

/** 換成某動作種類時的預設值(可直接送核心的合法形狀)。 */
export function newActionValue(kind: string, draft?: ScenarioDraft): unknown {
  switch (kind as TimelineActionKind) {
    case 'injectFault':
      return 'steeringGear';
    case 'clearFault':
      return 'all';
    case 'setEnvironment':
      return { visibility_nm: 2, ramp_s: 300 };
    case 'targetControl':
      return { targetId: draft?.targets[0]?.id ?? '', speed: 0 };
    case 'voiceScript':
      return { role: 'narrator', channel: 'bridge', text: '', lang: 'zh-TW' };
    case 'message':
      return { to: 'instructor', text: '', severity: 'info' };
    case 'phase':
      return 'degraded';
    case 'freeze':
      return { reason: '' };
    case 'autopilot':
      return { enabled: false };
    case 'ownShip':
      return { telegraph: 'STOP' };
    case 'timeScale':
      return 1;
    case 'arm':
    case 'disarm':
      return draft?.timeline[0]?.['id'] ?? '';
    case 'endExercise':
      return { reason: '時間到' };
    case 'tugControl':
      return { id: str(draft?.tugs[0]?.['id']), mode: 'standby' };
    default:
      return {};
  }
}

// ------------------------------------------------------------------ 感測器 / 警報 / BNWAS

export type FlatSectionKey = 'sensors' | 'alerts' | 'bnwas';

export const SENSOR_FIELDS: ReadonlyArray<readonly [key: string, label: string, unit: string]> = [['magneticCompassError_deg', '磁羅經剩餘誤差', '°']] as const;
export const ALERT_FIELDS: ReadonlyArray<readonly [key: string, label: string, unit: string]> = [
  ['offHeading_deg', '偏航門檻', '°'],
  ['offHeadingDelay_s', '偏航持續', 's'],
  ['headingMonitor_deg', '航向監視差異', '°'],
  ['shallowWater_m', '淺水警報', 'm'],
  ['escalate_s', 'warning 升級 alarm', 's'],
  ['silence_s', '靜音後重響', 's'],
] as const;
export const BNWAS_FIELDS: ReadonlyArray<readonly [key: string, label: string, unit: string]> = [
  ['dormant_s', '休眠期 180–720', 's'],
  ['stage2_s', '第二階段', 's'],
  ['stage3_s', '第三階段', 's'],
] as const;
export const BNWAS_MODES: readonly string[] = ['off', 'auto', 'on'] as const;

/** 改 sensors / alerts / bnwas 的鍵(空值刪鍵;區段清空 = null = 不輸出)。 */
export function patchSection(draft: ScenarioDraft, section: FlatSectionKey, path: string, value: unknown): ScenarioDraft {
  const next = withPath(draft[section], path, value);
  return { ...draft, [section]: Object.keys(next).length > 0 ? next : null };
}

// ------------------------------------------------------------------ 簡報 briefing

export const PLAYED_BY_OPTIONS: readonly string[] = ['leader', 'voiceScript', 'instructor', 'trainee'] as const;
export const ROLE_SUGGESTIONS: readonly string[] = ['master', 'officer', 'oow', 'helmsman', 'lookout', 'pilot', 'ecr', 'chiefEngineer', 'vts', 'portControl', 'company', 'agent', 'tug', 'berth', 'mooring', 'crew', 'otherShip'] as const;

export function briefingRoles(draft: ScenarioDraft): RawSection[] {
  return Array.isArray(draft.briefing['roles']) ? draft.briefing['roles'].filter(isRecord) : [];
}

export function briefingAttachments(draft: ScenarioDraft): RawSection[] {
  return Array.isArray(draft.briefing['attachments']) ? draft.briefing['attachments'].filter(isRecord) : [];
}

/** 改簡報鍵(minParticipants、duration_s;空值刪鍵)。 */
export function patchBriefing(draft: ScenarioDraft, path: string, value: unknown): ScenarioDraft {
  return { ...draft, briefing: withPath(draft.briefing, path, value) };
}

function setBriefingList(draft: ScenarioDraft, key: 'roles' | 'attachments', list: RawSection[]): ScenarioDraft {
  const b = { ...draft.briefing };
  if (list.length > 0 || key in b) b[key] = list;
  return { ...draft, briefing: b };
}

export function addRole(draft: ScenarioDraft, role: RawSection = { role: 'master', playedBy: 'leader' }): ScenarioDraft {
  return setBriefingList(draft, 'roles', [...briefingRoles(draft), role]);
}

export function removeRole(draft: ScenarioDraft, index: number): ScenarioDraft {
  return setBriefingList(draft, 'roles', removeAt(briefingRoles(draft), index));
}

/** 改角色欄位(role、playedBy、required;required 為布林,false 刪鍵)。 */
export function patchRole(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return setBriefingList(draft, 'roles', patchAt(briefingRoles(draft), index, path, path === 'required' && value === false ? undefined : value));
}

export function addAttachment(draft: ScenarioDraft, attachment: RawSection = { title: '', file: '' }): ScenarioDraft {
  return setBriefingList(draft, 'attachments', [...briefingAttachments(draft), attachment]);
}

export function removeAttachment(draft: ScenarioDraft, index: number): ScenarioDraft {
  return setBriefingList(draft, 'attachments', removeAt(briefingAttachments(draft), index));
}

export function patchAttachment(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return setBriefingList(draft, 'attachments', patchAt(briefingAttachments(draft), index, path, value));
}

// ------------------------------------------------------------------ 評估:必要事件與設定

/** after.event 建議(核心事件種類;itemId 對 timelineTriggered/faultInjected 為時間表項目、alertRaised 為警報 id、reportPointPassed 用 waypoint、targetActivated/encounterStarted 用 targetId)。 */
export const AFTER_EVENT_SUGGESTIONS: readonly string[] = [
  'timelineTriggered',
  'faultInjected',
  'alertRaised',
  'reportPointPassed',
  'checkpointPassed',
  'targetActivated',
  'encounterStarted',
  'cpaAlarm',
  'zoneEntered',
  'anchorLetGo',
  'anchorDragging',
  'tugLineParted',
  'engineStartFailed',
] as const;

export const EXPECT_ACTIONS: readonly string[] = ['rudderOrder', 'telegraphChange', 'rpmOrder', 'thrusterOrder', 'steeringModeChange', 'autopilotOn', 'autopilotOff', 'acknowledge'] as const;
export const ACKNOWLEDGE_KINDS: readonly string[] = ['verbal', 'notifyEcr', 'callMaster', 'report', 'checklist', 'steeringMode', 'anchorReady', 'gyroCheck', 'prediction', 'driftCalculation', 'anchorDecision', 'notifyCompany', 'notifySar', 'lookoutReport', 'other'] as const;

/** expect 欄位的流式 YAML 建議(datalist)。 */
export function expectSuggestions(): string[] {
  return [
    ...EXPECT_ACTIONS,
    ...ACKNOWLEDGE_KINDS.map((k) => `{ acknowledge: { kind: ${k} } }`),
    '{ event: { kind: encounterFirstAction, targetId: X1 } }',
    '{ any: [ rudderOrder, telegraphChange, { acknowledge: { kind: verbal } } ] }',
  ];
}

export interface RequiredForm {
  id: string;
  event: string;
  itemId: string;
  targetId: string;
  waypoint: string;
  /** expect 的流式 YAML(字串動作名、{acknowledge}、{event}、{any}) */
  expect: string;
  within_s: string;
  penalty: string;
  latePenaltyPer_s: string;
  /** after 內未在表單的鍵(legIndex、zoneId) */
  keptAfter: string[];
}

export function requiredForm(r: RawSection): RequiredForm {
  const after = recordOf(r['after']);
  return {
    id: str(r['id']),
    event: str(after['event']),
    itemId: str(after['itemId']),
    targetId: str(after['targetId']),
    waypoint: str(after['waypoint']),
    expect: flowYaml(r['expect']),
    within_s: numberOrRefText(numOrRef(r['within_s'])),
    penalty: numberOrRefText(numOrRef(r['penalty'])),
    latePenaltyPer_s: numberOrRefText(numOrRef(r['latePenaltyPer_s'])),
    keptAfter: Object.keys(after).filter((k) => !['event', 'itemId', 'targetId', 'waypoint'].includes(k)),
  };
}

/** 新必要事件:計時起點為第一個時間表項目觸發,期望口頭確認,120 s、扣 10 分。 */
export function newRequired(draft: ScenarioDraft): RawSection {
  const id = uniqueId('required', draft.assessment.required.map((r) => str(r['id'])), draft.assessment.required.length + 1);
  const first = draft.timeline[0];
  const after: RawSection = { event: 'timelineTriggered' };
  if (first) after['itemId'] = str(first['id']);
  return { id, after, expect: { acknowledge: { kind: 'verbal' } }, within_s: 120, penalty: 10 };
}

function withRequired(draft: ScenarioDraft, required: RawSection[]): ScenarioDraft {
  return { ...draft, assessment: { ...draft.assessment, required } };
}

export function addRequired(draft: ScenarioDraft, rule: RawSection = newRequired(draft)): ScenarioDraft {
  return withRequired(draft, [...draft.assessment.required, rule]);
}

export function removeRequired(draft: ScenarioDraft, index: number): ScenarioDraft {
  return withRequired(draft, removeAt(draft.assessment.required, index));
}

export function moveRequired(draft: ScenarioDraft, index: number, delta: number): ScenarioDraft {
  return withRequired(draft, moveAt(draft.assessment.required, index, delta));
}

/** 改必要事件欄位(id、after.event、after.itemId、after.targetId、after.waypoint、expect(已解析的值)、within_s、penalty、latePenaltyPer_s)。 */
export function patchRequired(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return withRequired(draft, patchAt(draft.assessment.required, index, path, value));
}

/** 改評估設定(minCpa_nm、encounter.detectionRange_nm … 、manoeuvring.rotLimit_degPerMin …;空值刪鍵)。 */
export function patchAssessmentSetting(draft: ScenarioDraft, path: string, value: unknown): ScenarioDraft {
  return { ...draft, assessment: { ...draft.assessment, settings: withPath(draft.assessment.settings, path, value) } };
}

export function setAssessmentUkcMin(draft: ScenarioDraft, kind: UkcLimitKind | '', value: NumberOrRef | undefined): ScenarioDraft {
  return patchAssessmentSetting(draft, 'ukc.min', ukcLimitValue(getPath(draft.assessment.settings, 'ukc.min'), kind, value));
}

export const ENCOUNTER_FIELDS: ReadonlyArray<readonly [key: string, label: string, unit: string]> = [
  ['detectionRange_nm', '首次偵測距離', 'nm'],
  ['substantialCourseChange_deg', '明顯改向', '°'],
  ['substantialSpeedChange_kn', '明顯改速', 'kn'],
  ['cpaInterest_nm', '會遇 CPA 關注', 'nm'],
] as const;
export const MANOEUVRING_FIELDS: ReadonlyArray<readonly [key: string, label: string, unit: string]> = [
  ['rotLimit_degPerMin', 'ROT 上限', '°/min'],
  ['hardOver_deg', '滿舵角', '°'],
] as const;

// ------------------------------------------------------------------ 拖船 / 錨 / 泊位 / 岸壁

export const BODY_POINTS: readonly string[] = ['fwd', 'aft', 'bow', 'stern', 'fwdPort', 'fwdStbd', 'midPort', 'midStbd', 'aftPort', 'aftStbd'] as const;
export const TUG_MODES: readonly string[] = ['push', 'pull'] as const;
export const TUG_INITIAL_MODES: readonly string[] = ['standby', 'free', 'push', 'pull'] as const;
export const LINE_INITIAL_STATES: readonly string[] = ['stowed', 'fast'] as const;

/** 船體座標點是否合法(具名點或 {x, y} 數值)。 */
export function bodyPointValid(v: unknown): boolean {
  return (typeof v === 'string' && BODY_POINTS.includes(v)) || (isRecord(v) && typeof v['x'] === 'number' && typeof v['y'] === 'number');
}

/** 船體座標點表單文字:具名點原樣,{x, y} 為 "x,y"。 */
export function bodyPointText(v: unknown): string {
  if (typeof v === 'string') return v;
  if (isRecord(v)) return `${str(v['x'])},${str(v['y'])}`;
  return '';
}

/** 表單文字 → 船體座標點:具名點字串,或 "x,y" → {x, y};空白 undefined;格式錯誤丟 Error。 */
export function bodyPointFromText(text: string): unknown {
  const s = text.trim();
  if (s === '') return undefined;
  if (BODY_POINTS.includes(s)) return s;
  const m = /^(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)$/.exec(s);
  if (m) return { x: Number(m[1]), y: Number(m[2]) };
  throw new Error(`船體座標點需為 ${BODY_POINTS.join('|')} 或 "x,y"(m):${s}`);
}

export function newTug(draft: ScenarioDraft): RawSection {
  const id = uniqueId('TG', draft.tugs.map((t) => str(t['id'])), draft.tugs.length + 1);
  return { id, name: '', bollardPull_t: 40, attach: draft.tugs.length % 2 === 0 ? 'fwdStbd' : 'aftStbd', lineLength_m: 40, modes: ['push', 'pull'], initial: { mode: 'standby', direction_deg: 90, power_pct: 0 } };
}

export function addTug(draft: ScenarioDraft, tug: RawSection = newTug(draft)): ScenarioDraft {
  return { ...draft, tugs: [...draft.tugs, tug] };
}

export function removeTug(draft: ScenarioDraft, index: number): ScenarioDraft {
  return { ...draft, tugs: removeAt(draft.tugs, index) };
}

/** 改拖船欄位(id、name、bollardPull_t、attach、lineLength_m、modes(陣列)、initial.mode/direction_deg/power_pct;空值刪鍵)。 */
export function patchTug(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return { ...draft, tugs: patchAt(draft.tugs, index, path, value) };
}

export function tugModes(tug: RawSection): string[] {
  return Array.isArray(tug['modes']) ? tug['modes'].map(str) : [...TUG_MODES];
}

export const ANCHOR_SIDES: readonly string[] = ['port', 'starboard'] as const;

/** 改錨區段(port.length_m、port.length_shackles、port.bearing_deg、starboard.*、holdingFactor、dragSpeed_mps;清空 = null)。 */
export function patchAnchors(draft: ScenarioDraft, path: string, value: unknown): ScenarioDraft {
  const next = withPath(draft.anchors, path, value);
  return { ...draft, anchors: Object.keys(next).length > 0 ? next : null };
}

/** 建置時匯入的區域檔(data/areas/*.json)中含 berths[] / banks[] 的檔案路徑(相對專案根目錄)。 */
export function areaFileChoices(kind: 'berths' | 'banks', areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): string[] {
  return Object.entries(areaFiles)
    .filter(([, doc]) => isRecord(doc) && Array.isArray(doc[kind]) && doc[kind].length > 0)
    .map(([key]) => `data/areas/${key.replace(/\\/g, '/').split('/').pop() ?? key}`)
    .sort();
}

/** 區域檔內的泊位 / 岸壁 id(file 以檔名對應)。 */
export function areaIds(kind: 'berths' | 'banks', file: string | undefined, areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): string[] {
  if (!file) return [];
  const base = file.replace(/\\/g, '/').split('/').pop() ?? file;
  const key = Object.keys(areaFiles).find((k) => (k.replace(/\\/g, '/').split('/').pop() ?? k) === base);
  const doc = key ? areaFiles[key] : undefined;
  return isRecord(doc) && Array.isArray(doc[kind]) ? doc[kind].filter(isRecord).map((b) => str(b['id'])).filter(Boolean) : [];
}

export function berthLines(draft: ScenarioDraft): RawSection[] {
  const l = draft.berth?.['lines'];
  return Array.isArray(l) ? l.filter(isRecord) : [];
}

/** 建立泊位區段(第一個泊位檔與其第一個泊位;無泊位檔時只有 lines: [])或移除整段。 */
export function setBerthEnabled(draft: ScenarioDraft, on: boolean, areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): ScenarioDraft {
  if (!on) return { ...draft, berth: null };
  if (draft.berth) return draft;
  const file = areaFileChoices('berths', areaFiles)[0];
  const berth: RawSection = {};
  if (file) {
    berth['file'] = file;
    const id = areaIds('berths', file, areaFiles)[0];
    if (id) berth['id'] = id;
  }
  berth['lines'] = [];
  return { ...draft, berth };
}

/** 改泊位欄位(file、id、departedDistance_m;空值刪鍵)。 */
export function patchBerth(draft: ScenarioDraft, path: string, value: unknown): ScenarioDraft {
  return { ...draft, berth: withPath(draft.berth, path, value) };
}

function setBerthLines(draft: ScenarioDraft, lines: RawSection[]): ScenarioDraft {
  return { ...draft, berth: { ...recordOf(draft.berth), lines } };
}

export function newBerthLine(draft: ScenarioDraft): RawSection {
  const lines = berthLines(draft);
  const id = uniqueId('LINE', lines.map((l) => str(l['id'])), lines.length + 1);
  return { id, fairlead: lines.length % 2 === 0 ? 'fwdPort' : 'aftPort', bollard: '', mbl_t: 50, length_m: 150, initial: 'stowed' };
}

export function addBerthLine(draft: ScenarioDraft, line: RawSection = newBerthLine(draft)): ScenarioDraft {
  return setBerthLines(draft, [...berthLines(draft), line]);
}

export function removeBerthLine(draft: ScenarioDraft, index: number): ScenarioDraft {
  return setBerthLines(draft, removeAt(berthLines(draft), index));
}

/** 改繫纜欄位(id、fairlead、bollard、mbl_t、length_m、initial;空值刪鍵)。 */
export function patchBerthLine(draft: ScenarioDraft, index: number, path: string, value: unknown): ScenarioDraft {
  return setBerthLines(draft, patchAt(berthLines(draft), index, path, value));
}

/** 建立岸壁區段(第一個岸壁檔)或移除整段。 */
export function setBanksEnabled(draft: ScenarioDraft, on: boolean, areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): ScenarioDraft {
  if (!on) return { ...draft, banks: null };
  if (draft.banks) return draft;
  const file = areaFileChoices('banks', areaFiles)[0];
  return { ...draft, banks: file ? { file } : { lines: [] } };
}

/** 改岸壁欄位(file、scale;ids 給陣列,空陣列刪鍵;lines/definitions 不在表單、原樣保留)。 */
export function patchBanks(draft: ScenarioDraft, path: string, value: unknown): ScenarioDraft {
  const v = path === 'ids' && Array.isArray(value) && value.length === 0 ? undefined : value;
  return { ...draft, banks: withPath(draft.banks, path, v) };
}

/** 逗號分隔文字 → 字串陣列(去空白、去空項)。 */
export function idListFromText(text: string): string[] {
  return text
    .split(/[,、\s]+/)
    .map((s) => s.trim())
    .filter(Boolean);
}

export function bankLineCount(banks: RawSection | null): number {
  const lines = Array.isArray(banks?.['lines']) ? banks['lines'].length : 0;
  const defs = Array.isArray(banks?.['definitions']) ? banks['definitions'].length : 0;
  return lines + defs;
}

// ------------------------------------------------------------------ 驗證

export interface DraftValidation {
  scenario: Record<string, unknown>;
  yaml: string;
  summary: ScenarioSummary;
  /** 阻止載入的問題(parseScenario issues + 編輯器檢查) */
  issues: string[];
  /** 不阻止載入的提醒 */
  warnings: string[];
}

const ID_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]*$/;
const UKC_MINIMUM_M = 1.0;

function walkConditions(c: TriggerCondition | null, fn: (type: string, fields: Record<string, unknown>) => void): void {
  if (!c) return;
  if (c.op === 'leaf') fn(c.type, c.fields);
  else if (c.op === 'not') walkConditions(c.item, fn);
  else c.items.forEach((i) => walkConditions(i, fn));
}

/**
 * 以 toYaml 的輸出跑 parseScenario(與情境面板/核心相同的最小結構檢查),再加編輯器自己的檢查:
 * id 可作檔名、目標船 id 唯一且位置完整、故障名稱在目錄內(否則警告)、硬性失敗 zoneEntered 的區域存在、
 * 時間表條件引用的航點/目標/區域存在、初始 UKC(與靠離泊預演同判準:≤ 0 為錯誤、< 1.0 m 警告)。
 */
export function validateDraft(draft: ScenarioDraft, catalogue: readonly FaultDefinition[] = faultOptions()): DraftValidation {
  const scenario = toScenarioObject(draft);
  const yaml = toYaml(draft);
  const id = draft.general.id.trim();
  const summary = parseScenario(yaml, `${id || 'draft'}.yaml`);
  summary.path = '';
  const issues = [...summary.issues];
  const warnings: string[] = [];
  if (id && !ID_RE.test(id)) issues.push('id 只能用英數字、_、-、.(作為紀錄檔名)');
  if (!draft.general.name.trim()) warnings.push('未填名稱(情境清單與簡報以 id 顯示)');
  if (!positionValid(draft.initial.position)) issues.push('initial.position 需完整的 lat/lon 或 x/y 數值');
  if (positionMode(draft.initial.position) === 'enu' && !isRecord(draft.raw['origin'])) issues.push('初始位置用 x/y 時需有 origin(範本未提供;請改用經緯度)');

  const seen = new Set<string>();
  draft.targets.forEach((t, i) => {
    const tid = t.id.trim();
    if (!tid) issues.push(`targets[${i}] 缺 id`);
    else if (seen.has(tid)) issues.push(`targets[${i}] id 重複:${tid}`);
    seen.add(tid);
    if (!positionValid(t.position)) issues.push(`targets[${i}] ${tid || ''} 初始位置需完整的 lat/lon 或 x/y 數值`.replace('  ', ' '));
    if (t.mode !== undefined && !(TARGET_MODES as readonly string[]).includes(t.mode)) issues.push(`targets[${i}] 未知的行為模式 ${t.mode}`);
    const h = t.heading;
    if (typeof h === 'number' && (h < 0 || h > 360)) issues.push(`targets[${i}] 航向需在 0–360`);
  });

  const names = new Set(catalogue.map((f) => f.name));
  const waypoints = new Set((summary.route?.waypoints ?? []).map((w) => w.name));
  const zones = new Set(summary.zones.map((z) => z.id));
  const itemIds = new Set(draft.timeline.map((t) => str(t['id']).trim()).filter(Boolean));
  const tugIds = new Set(draft.tugs.map((t) => str(t['id']).trim()).filter(Boolean));
  const origin = hasOrigin(draft);
  summary.timeline.forEach((it) => {
    if (it.actions.length === 0) warnings.push(`時間表 ${it.id}:沒有動作`);
    for (const a of it.actions) {
      if (a.kind === 'injectFault' || a.kind === 'clearFault') {
        const name = typeof a.value === 'string' ? a.value : isRecord(a.value) ? str(a.value['name']) : '';
        if (a.kind === 'injectFault' && !name) issues.push(`時間表 ${it.id}:injectFault 缺故障名稱`);
        if (name && name !== 'all' && !names.has(name)) warnings.push(`時間表 ${it.id}:故障 ${name} 不在故障目錄(核心可能以只記錄處理或拒絕)`);
      }
      if (a.kind === 'targetControl' && isRecord(a.value)) {
        const tid = str(a.value['targetId'] ?? a.value['id']);
        if (tid && !seen.has(tid)) issues.push(`時間表 ${it.id}:targetControl 引用不存在的目標船 ${tid}`);
      }
      if (a.kind === 'arm' || a.kind === 'disarm') {
        const ref = str(a.value).trim();
        if (!itemIds.has(ref)) issues.push(`時間表 ${it.id}:${a.kind} 引用不存在的項目 ${ref || '(空)'}`);
      }
      if (a.kind === 'tugControl') {
        const tid = isRecord(a.value) ? str(a.value['id']).trim() : '';
        if (!tugIds.has(tid)) issues.push(`時間表 ${it.id}:tugControl 引用不存在的拖船 ${tid || '(空)'}`);
      }
      if (a.kind === 'message' && (!isRecord(a.value) || !str(a.value['text']).trim())) warnings.push(`時間表 ${it.id}:message 沒有 text`);
    }
    walkConditions(it.when, (type, f) => {
      if (type === 'ownDistance') {
        if (f['waypoint'] !== undefined && !waypoints.has(str(f['waypoint']))) issues.push(`時間表 ${it.id}:條件引用不存在的航點 ${str(f['waypoint'])}`);
        if (f['target'] !== undefined && !seen.has(str(f['target']))) issues.push(`時間表 ${it.id}:條件引用不存在的目標船 ${str(f['target'])}`);
      }
      if (type === 'zone' && f['id'] !== undefined && !zones.has(str(f['id']))) issues.push(`時間表 ${it.id}:條件引用不存在的區域 ${str(f['id'])}`);
      if (type === 'fault' && f['name'] !== undefined && !names.has(str(f['name']))) warnings.push(`時間表 ${it.id}:條件引用的故障 ${str(f['name'])} 不在故障目錄`);
    });
  });

  draft.assessment.hardFail.forEach((h, i) => {
    const rule = str(h['rule']);
    if (!(HARD_FAIL_RULES as readonly string[]).includes(rule)) issues.push(`assessment.hardFail[${i}] 未知的規則 ${rule || '(空)'}`);
    if (rule === 'zoneEntered' && !zones.has(str(h['zoneId']))) issues.push(`assessment.hardFail[${i}] zoneEntered 引用不存在的區域 ${str(h['zoneId']) || '(空)'}`);
    if ((rule === 'cpa' || rule === 'ukc') && h['lessThan'] === undefined) issues.push(`assessment.hardFail[${i}] ${rule} 需 lessThan`);
    if (rule === 'event' && !str(h['kind']) && !isRecord(h['event'])) issues.push(`assessment.hardFail[${i}] event 需 kind`);
  });
  draft.assessment.bands.forEach((b, i) => {
    if (!str(b['metric']).trim()) issues.push(`assessment.bands[${i}] 缺 metric`);
    const fail = b['fail'];
    if (typeof fail === 'string') {
      const f = fail.trim();
      if (!isParamRef(f as unknown) && !/^\d+(\.\d+)?x$/i.test(f) && !Number.isFinite(Number(f))) issues.push(`assessment.bands[${i}] fail 需為數值、參數引用或「2x」`);
    }
  });

  // 航線:航點名稱唯一、ENU 座標需原點、startLeg 範圍(位置不全與少於 2 點由 parseScenario 報)
  if (draft.route) {
    const wps = routeWaypoints(draft);
    const wpNames = new Set<string>();
    wps.forEach((w, i) => {
      const name = str(w['name']).trim();
      if (!name) issues.push(`route.waypoints[${i}] 缺 name`);
      else if (wpNames.has(name)) issues.push(`route.waypoints[${i}] name 重複:${name}`);
      wpNames.add(name);
      const p = readPosition(w);
      if (!positionValid(p)) issues.push(`route.waypoints[${i}] ${name || ''} 需完整的 lat/lon 或 x/y 數值`.replace('  ', ' '));
      else if (positionMode(p) === 'enu' && !origin) issues.push(`route.waypoints[${i}] 用 x/y 時情境需有 origin 或經緯度初始位置`);
    });
    const startLeg = draft.route['startLeg'];
    const maxLeg = Math.max(0, wps.length - 2);
    if (typeof startLeg === 'number' && (startLeg < 0 || startLeg > maxLeg || !Number.isInteger(startLeg))) issues.push(`route.startLeg 需為 0–${maxLeg} 的整數`);
  }

  // 區域:id 唯一、頂點完整(≥ 3 點與 type 列舉由 parseScenario 報)、型別應有的屬性
  const zoneIds = new Set<string>();
  draft.zones.forEach((z, i) => {
    const zid = str(z['id']).trim();
    if (zid && zoneIds.has(zid)) issues.push(`zones[${i}] id 重複:${zid}`);
    zoneIds.add(zid);
    const poly = zonePolygon(z);
    if (poly.length > 64) issues.push(`zones[${i}] 多邊形最多 64 點`);
    poly.forEach((p, j) => {
      const pos = readPosition(p);
      if (!positionValid(pos)) issues.push(`zones[${i}].polygon[${j}] 需完整的 lat/lon 或 x/y 數值`);
      else if (positionMode(pos) === 'enu' && !origin) issues.push(`zones[${i}].polygon[${j}] 用 x/y 時情境需有 origin 或經緯度初始位置`);
    });
    if (z['type'] === 'speedLimit' && z['speedLimit_kn'] === undefined) warnings.push(`zones[${i}] ${zid}:speedLimit 區未給 speedLimit_kn`);
    if (z['type'] === 'ukcPolicy' && z['ukcMin'] === undefined && z['waterDepth_m'] === undefined) warnings.push(`zones[${i}] ${zid}:ukcPolicy 區未給 ukcMin 或 waterDepth_m`);
  });

  // 必要事件:id 唯一、within_s、after 引用的時間表項目 / 目標船 / 航點存在(缺 after/event、expect 無效由 parseScenario 報)
  const reqIds = new Set<string>();
  draft.assessment.required.forEach((r, i) => {
    const rid = str(r['id']).trim();
    if (!rid) issues.push(`assessment.required[${i}] 缺 id`);
    else if (reqIds.has(rid)) issues.push(`assessment.required[${i}] id 重複:${rid}`);
    reqIds.add(rid);
    if (r['within_s'] === undefined) issues.push(`assessment.required[${i}] 缺 within_s`);
    const after = recordOf(r['after']);
    const event = str(after['event']);
    const itemId = after['itemId'];
    if (itemId !== undefined && (event === 'timelineTriggered' || event === 'faultInjected') && !itemIds.has(str(itemId))) issues.push(`assessment.required[${i}] after.itemId 引用不存在的時間表項目 ${str(itemId)}`);
    if (after['targetId'] !== undefined && !seen.has(str(after['targetId']))) issues.push(`assessment.required[${i}] after.targetId 引用不存在的目標船 ${str(after['targetId'])}`);
    if (after['waypoint'] !== undefined && !waypoints.has(str(after['waypoint']))) issues.push(`assessment.required[${i}] after.waypoint 引用不存在的航點 ${str(after['waypoint'])}`);
  });
  const minCpa = draft.assessment.settings['minCpa_nm'];
  if (typeof minCpa === 'number' && minCpa <= 0) issues.push('assessment.minCpa_nm 需 > 0');

  // 警報 / BNWAS:秒數與門檻不為負、mode 列舉、休眠期範圍
  for (const [section, fields] of [
    ['alerts', ALERT_FIELDS],
    ['bnwas', BNWAS_FIELDS],
  ] as const) {
    const o = draft[section];
    if (!o) continue;
    for (const [key] of fields) {
      const v = o[key];
      if (typeof v === 'number' && v < 0) issues.push(`${section}.${key} 需 ≥ 0`);
    }
  }
  if (draft.bnwas) {
    const mode = draft.bnwas['mode'];
    if (mode !== undefined && !BNWAS_MODES.includes(str(mode))) issues.push(`bnwas.mode 需為 ${BNWAS_MODES.join(' / ')}`);
    const dormant = draft.bnwas['dormant_s'];
    if (typeof dormant === 'number' && (dormant < 180 || dormant > 720)) warnings.push(`bnwas.dormant_s ${dormant} s 不在 MSC.128(75) 的 180–720 s`);
  }

  // 簡報:最少人數、角色 playedBy(缺 role / 附件缺 file 由 parseScenario 報)
  const minP = draft.briefing['minParticipants'];
  if (minP !== undefined && (typeof minP !== 'number' || !Number.isInteger(minP) || minP < 1)) issues.push('briefing.minParticipants 需為 ≥ 1 的整數');
  briefingRoles(draft).forEach((r, i) => {
    const pb = r['playedBy'];
    if (pb !== undefined && !PLAYED_BY_OPTIONS.includes(str(pb))) warnings.push(`briefing.roles[${i}] playedBy ${str(pb)} 不在 ${PLAYED_BY_OPTIONS.join('/')}`);
  });

  // 拖船:id 唯一、拖力、連接點、初始模式在可用模式內
  const tugSeen = new Set<string>();
  draft.tugs.forEach((t, i) => {
    const tid = str(t['id']).trim();
    if (!tid) issues.push(`tugs[${i}] 缺 id`);
    else if (tugSeen.has(tid)) issues.push(`tugs[${i}] id 重複:${tid}`);
    tugSeen.add(tid);
    const bp = t['bollardPull_t'];
    if (bp !== undefined && (typeof bp !== 'number' || bp <= 0)) issues.push(`tugs[${i}] bollardPull_t 需 > 0`);
    if (t['attach'] !== undefined && !bodyPointValid(t['attach'])) issues.push(`tugs[${i}] attach 需為 ${BODY_POINTS.join('|')} 或 {x, y}`);
    const modes = Array.isArray(t['modes']) ? t['modes'].map(str) : null;
    if (modes && modes.length === 0) issues.push(`tugs[${i}] modes 不可為空`);
    if (modes && modes.some((m) => !TUG_MODES.includes(m))) issues.push(`tugs[${i}] modes 只能是 push / pull`);
    const im = str(getPath(t, 'initial.mode'));
    if (im && !TUG_INITIAL_MODES.includes(im)) issues.push(`tugs[${i}] initial.mode 需為 ${TUG_INITIAL_MODES.join('/')}`);
    if (modes && (im === 'push' || im === 'pull') && !modes.includes(im)) issues.push(`tugs[${i}] initial.mode ${im} 不在 modes 內`);
    const pw = getPath(t, 'initial.power_pct');
    if (typeof pw === 'number' && (pw < 0 || pw > 100)) issues.push(`tugs[${i}] initial.power_pct 需在 0–100`);
  });

  // 錨:方位、鏈長、抓地力倍率
  if (draft.anchors) {
    for (const side of ANCHOR_SIDES) {
      const a = draft.anchors[side];
      if (!isRecord(a)) continue;
      const b = a['bearing_deg'];
      if (typeof b === 'number' && (b < 0 || b > 360)) issues.push(`anchors.${side}.bearing_deg 需在 0–360`);
      for (const k of ['length_m', 'length_shackles']) {
        const v = a[k];
        if (typeof v === 'number' && v < 0) issues.push(`anchors.${side}.${k} 需 ≥ 0`);
      }
    }
    for (const k of ['holdingFactor', 'dragSpeed_mps']) {
      const v = draft.anchors[k];
      if (typeof v === 'number' && v <= 0) issues.push(`anchors.${k} 需 > 0`);
    }
  }

  // 泊位:file 或 definition、泊位檔可解析、繫纜 id 唯一 / 導纜孔 / fast 需 bollard、bollard 在泊位定義內
  if (draft.berth) {
    const b = draft.berth;
    const file = str(b['file']).trim();
    if (!file && !isRecord(b['definition'])) issues.push('berth 需 file(泊位檔)或 definition');
    else if (file && !summary.berth?.definition) warnings.push(`berth:教官站建置時匯入的 data/areas 找不到 ${file}${b['id'] !== undefined ? ` 的泊位 ${str(b['id'])}` : ''}(核心載入時仍會解析)`);
    const dd = b['departedDistance_m'];
    if (dd !== undefined && (typeof dd !== 'number' || dd <= 0)) issues.push('berth.departedDistance_m 需 > 0');
    const defBollards = summary.berth?.definition?.['bollards'];
    const bollards = new Set((Array.isArray(defBollards) ? defBollards : []).filter(isRecord).map((x) => str(x['id'])));
    const lineIds = new Set<string>();
    berthLines(draft).forEach((l, i) => {
      const lid = str(l['id']).trim();
      if (!lid) issues.push(`berth.lines[${i}] 缺 id`);
      else if (lineIds.has(lid)) issues.push(`berth.lines[${i}] id 重複:${lid}`);
      lineIds.add(lid);
      if (!bodyPointValid(l['fairlead'])) issues.push(`berth.lines[${i}] ${lid} 需 fairlead(${BODY_POINTS.join('|')} 或 {x, y})`);
      const bollard = str(l['bollard']).trim();
      if (l['initial'] === 'fast' && !bollard) issues.push(`berth.lines[${i}] ${lid} initial fast 需指定 bollard`);
      if (l['initial'] !== undefined && !LINE_INITIAL_STATES.includes(str(l['initial']))) issues.push(`berth.lines[${i}] initial 需為 stowed / fast`);
      if (bollard && bollards.size > 0 && !bollards.has(bollard)) warnings.push(`berth.lines[${i}] ${lid}:泊位定義沒有繫船柱 ${bollard}`);
      for (const k of ['mbl_t', 'length_m']) {
        const v = l[k];
        if (typeof v === 'number' && v <= 0) issues.push(`berth.lines[${i}] ${k} 需 > 0`);
      }
    });
  }

  // 岸壁:file 或 lines、倍率
  if (draft.banks) {
    const file = str(draft.banks['file']).trim();
    if (!file && bankLineCount(draft.banks) === 0) issues.push('banks 需 file(岸壁檔)或 lines');
    else if (file && !areaFileChoices('banks').includes(file)) warnings.push(`banks:教官站建置時匯入的 data/areas 找不到 ${file}(核心載入時仍會解析)`);
    const scale = draft.banks['scale'];
    if (scale !== undefined && (typeof scale !== 'number' || scale < 0)) issues.push('banks.scale 需 ≥ 0');
  }

  const depth = draft.environment.waterDepth;
  if (typeof depth === 'number') {
    const draftMax = maxDraft(getShipConfig(draft.ship.id), draft.ship.loading ?? 'ballast');
    const ukc = Math.round((depth - draftMax) * 100) / 100;
    // 有 environment.tide 時 waterDepth 為海圖水深,有效水深另加潮高(第十七輪潮汐模型),靜態檢查只能提醒
    const tide = isRecord(recordOf(draft.raw['environment'])['tide']);
    if (ukc <= 0 && !tide) issues.push(`初始 UKC ${ukc} m ≤ 0(水深 ${depth} m − 最大吃水 ${draftMax} m;核心載入即擱淺、硬性失敗),請提高水深或改裝載狀態`);
    else if (ukc <= 0) warnings.push(`初始 UKC ${ukc} m ≤ 0 以海圖水深 ${depth} m 計(情境有 environment.tide,有效水深另加潮高;核心載入時判定)`);
    else if (ukc < UKC_MINIMUM_M) warnings.push(`初始 UKC ${ukc} m 低於公司 UKC 下限 ${UKC_MINIMUM_M} m(最大吃水 ${draftMax} m,未計 squat)`);
  }
  if (summary.stats.timelineItems === 0) warnings.push('時間表沒有項目(無故障、語音與結束條件;練習由教官手動結束)');
  return { scenario, yaml, summary, issues, warnings };
}

/** 開始時刻的日/夜標示(以經度推太陽時;顯示用,核心視景另算)。 */
export function dayNightLabel(startTimeUtc: string | undefined, lon: NumberOrRef | undefined): string {
  if (!startTimeUtc) return '未指定(核心以實際時鐘代入)';
  const d = new Date(startTimeUtc);
  if (Number.isNaN(d.getTime())) return '時刻格式無法解析(例如 2026-11-13T01:00:00Z)';
  const solar = (((d.getUTCHours() + d.getUTCMinutes() / 60 + (typeof lon === 'number' ? lon / 15 : 8)) % 24) + 24) % 24;
  const hh = `${Math.floor(solar).toString().padStart(2, '0')}:${Math.floor((solar % 1) * 60).toString().padStart(2, '0')}`;
  if (solar < 5 || solar >= 19) return `夜間(當地太陽時約 ${hh})`;
  if (solar < 7) return `黎明(當地太陽時約 ${hh})`;
  if (solar < 17) return `白天(當地太陽時約 ${hh})`;
  return `黃昏(當地太陽時約 ${hh})`;
}

// ------------------------------------------------------------------ localStorage 草稿

export const DRAFT_STORAGE_KEY = 'simosa-brm.instructor.scenario-draft';

export interface DraftStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

function defaultStorage(): DraftStorage | null {
  try {
    return typeof localStorage !== 'undefined' ? localStorage : null;
  } catch {
    return null;
  }
}

/** 存草稿(無網路;瀏覽器儲存空間不可用時回 false)。 */
export function saveDraft(draft: ScenarioDraft, storage: DraftStorage | null = defaultStorage()): boolean {
  if (!storage) return false;
  try {
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(draft));
    return true;
  } catch {
    return false;
  }
}

/** 讀回草稿;沒有、格式不符或版本不合回 null。 */
export function loadDraft(storage: DraftStorage | null = defaultStorage()): ScenarioDraft | null {
  if (!storage) return null;
  try {
    const text = storage.getItem(DRAFT_STORAGE_KEY);
    if (!text) return null;
    const v: unknown = JSON.parse(text);
    if (!isRecord(v) || !isRecord(v['general']) || !isRecord(v['raw']) || !Array.isArray(v['targets']) || !Array.isArray(v['timeline']) || !isRecord(v['assessment'])) return null;
    if (v['version'] === 1) return migrateDraftV1(v);
    if (v['version'] !== DRAFT_VERSION || !Array.isArray(v['zones']) || !isRecord(v['briefing']) || !Array.isArray(v['tugs'])) return null;
    return v as unknown as ScenarioDraft;
  } catch {
    return null;
  }
}

/**
 * 第十七輪(版本 1)草稿升級:當時無法編輯的區段(route、zones、簡報角色/附件、sensors/alerts/bnwas、必要事件、拖船/錨/泊位/岸壁)
 * 仍以範本 raw 為準,由 raw 重建;已可編輯的欄位沿用舊草稿。
 */
function migrateDraftV1(v: Record<string, unknown>): ScenarioDraft {
  const old = v as unknown as ScenarioDraft;
  const base = fromScenarioObject(old.raw, typeof v['template'] === 'string' ? v['template'] : '');
  const a = recordOf(v['assessment']);
  const assessment: DraftAssessment = { ...base.assessment, hardFail: listOf(a['hardFail']), bands: listOf(a['bands']) };
  const start = numOrRef(a['start']);
  if (start !== undefined) assessment.start = start;
  else delete assessment.start;
  const pass = numOrRef(a['passMark']);
  if (pass !== undefined) assessment.passMark = pass;
  else delete assessment.passMark;
  return { ...base, general: old.general, ship: old.ship, initial: old.initial, environment: old.environment, targets: old.targets, timeline: old.timeline, assessment };
}

export function clearDraft(storage: DraftStorage | null = defaultStorage()): void {
  try {
    storage?.removeItem(DRAFT_STORAGE_KEY);
  } catch {
    /* 儲存空間不可用 */
  }
}
