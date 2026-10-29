// 教官站情境編輯器的草稿模型(規劃書第 9.1 節「教官依範本組出新練習(環境、目標船、2 個故障、評估規則)≤30 分鐘」;R4;第 14 章教官第 2 列)。
// 流程:fromYaml(範本原文)→ ScenarioDraft(可序列化的純物件,存 localStorage)→ toScenarioObject() / toYaml() → parseScenario 自我驗證 →
// 「下載 YAML」或以 loadScenario args.scenario 內嵌送核心(與靠離泊預演相同的載入路徑;lib/berthing-rehearsal.ts)。
// 編輯器只涵蓋:基本資料(id/name/description/簡報文字/seed/timeScale/startTimeUtc)、船與裝載、初始位置/航向/速度/車鐘/舵角、
// 環境(風、流、水深、能見度)、目標船(id/船名/船型/尺寸/初始位置/航向/速度/行為模式)、時間表故障項目(新增/刪除;其餘項目只列出與刪除)、
// 評估的硬性失敗/容許帶/評分。其餘欄位(origin、route、zones、briefing 附件與角色、sensors/alerts/bnwas、tugs/anchors/berth/banks、
// environment.seabed/radar/tide(潮汐只保留不編輯)、目標船的 AIS/行為細節/trigger、assessment.required/ukc/encounter/manoeuvring/parameterSet …)原樣保留在
// draft.raw,輸出時覆寫涵蓋欄位、不遺失其餘資料(設計文件 docs/design/scenario-engine.md 第 6.1 節)。
// 輸出 YAML 以 yaml Document 產生:會被 YAML 1.1 解析器誤判的字串(on/off/yes/no、時間戳、數字樣、參數引用)一律加雙引號,
// 與 data/scenarios 既有寫法一致(C# YamlJson 只把 true/false/null/數字當非字串;Python 參考實作以 PyYAML 讀檔)。YAML 註解不保留。

import { Document, Scalar, parse as parseYaml, visit } from 'yaml';
import { SHIP_IDS, TELEGRAPH_POSITIONS, type LoadingCondition, type ShipId, type TelegraphPosition } from '../types/state.js';
import { faultCatalogue, type FaultDefinition } from './instructor-command.js';
import { parseScenario, type ScenarioSummary } from './scenario.js';
import { getShipConfig, maxDraft } from './ship-config.js';
import { actionSummary, conditionSummary, isParamRef, parseTimelineItem, type TriggerCondition } from './timeline.js';

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
  start?: NumberOrRef;
  passMark?: NumberOrRef;
}

export interface ScenarioDraft {
  version: 1;
  /** 範本來源(情境 id;'' = 空白範本;顯示與 YAML 註解用) */
  template: string;
  general: DraftGeneral;
  ship: DraftShip;
  initial: DraftInitial;
  environment: DraftEnvironment;
  targets: DraftTarget[];
  /** 時間表項目(原樣物件;編輯器新增故障項目、刪除任一項目,其餘保留) */
  timeline: Record<string, unknown>[];
  assessment: DraftAssessment;
  /** 範本的完整情境物件(未涵蓋欄位由此保留;toScenarioObject 以草稿覆寫涵蓋欄位) */
  raw: Record<string, unknown>;
}

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
  const assessment: DraftAssessment = {
    hardFail: (Array.isArray(aRaw['hardFail']) ? aRaw['hardFail'] : []).filter(isRecord).map((h) => clone(h)),
    bands: (Array.isArray(aRaw['bands']) ? aRaw['bands'] : []).filter(isRecord).map((b) => clone(b)),
  };
  const scoring = recordOf(aRaw['scoring']);
  const startScore = numOrRef(scoring['start']);
  if (startScore !== undefined) assessment.start = startScore;
  const pass = numOrRef(scoring['passMark']);
  if (pass !== undefined) assessment.passMark = pass;

  return { version: 1, template, general, ship, initial, environment, targets, timeline, assessment, raw: clone(obj) };
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
  const b = out['briefing'];
  if (isRecord(b)) {
    setOrDelete(b, 'text', g.briefing);
    if (Object.keys(b).length === 0) delete out['briefing'];
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
  setOrDelete(a, 'scoring', subObject(a['scoring'], { start: as.start, passMark: as.passMark }));
  if (Object.keys(a).length > 0 || 'assessment' in out) out['assessment'] = a;
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
  summary.timeline.forEach((it) => {
    for (const a of it.actions) {
      if (a.kind === 'injectFault' || a.kind === 'clearFault') {
        const name = typeof a.value === 'string' ? a.value : isRecord(a.value) ? str(a.value['name']) : '';
        if (name && name !== 'all' && !names.has(name)) warnings.push(`時間表 ${it.id}:故障 ${name} 不在故障目錄(核心可能以只記錄處理或拒絕)`);
      }
      if (a.kind === 'targetControl' && isRecord(a.value)) {
        const tid = str(a.value['targetId'] ?? a.value['id']);
        if (tid && !seen.has(tid)) issues.push(`時間表 ${it.id}:targetControl 引用不存在的目標船 ${tid}`);
      }
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

  const depth = draft.environment.waterDepth;
  if (typeof depth === 'number') {
    const draftMax = maxDraft(getShipConfig(draft.ship.id), draft.ship.loading ?? 'ballast');
    const ukc = Math.round((depth - draftMax) * 100) / 100;
    if (ukc <= 0) issues.push(`初始 UKC ${ukc} m ≤ 0(水深 ${depth} m − 最大吃水 ${draftMax} m;核心載入即擱淺、硬性失敗),請提高水深或改裝載狀態`);
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
    if (!isRecord(v) || v['version'] !== 1 || !isRecord(v['general']) || !isRecord(v['raw']) || !Array.isArray(v['targets']) || !Array.isArray(v['timeline']) || !isRecord(v['assessment'])) return null;
    return v as unknown as ScenarioDraft;
  } catch {
    return null;
  }
}

export function clearDraft(storage: DraftStorage | null = defaultStorage()): void {
  try {
    storage?.removeItem(DRAFT_STORAGE_KEY);
  } catch {
    /* 儲存空間不可用 */
  }
}
