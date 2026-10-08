// 教官站指令建構(規劃書第 9.1 節執行控制、環境即時變化、故障注入、自船覆寫),
// 輸出符合 src/Contracts/command.schema.json 的 SimCommand。
// 各欄位同時相容兩個核心:C# SimCore.Host(src/SimCore/README.md 第 3 節)與 Python 參考伺服器
// (src/Tools.Calibration/simosa_brm/server.py);核心尚未支援的欄位仍照送,由 UI 標示「核心未實作」。

import type { SimCommand } from '../types/command.js';
import type { LoadingCondition } from '../types/state.js';
import { clamp, normalizeHeading } from './angles.js';

// ------------------------------------------------------------------ 情境

/** loadScenario:C# 核心讀 value(路徑)或 args.path;args.id 供紀錄與 Python 伺服器(尚未實作)辨識。 */
export function loadScenarioCommand(id: string, path: string): SimCommand {
  return { type: 'loadScenario', value: path, args: { id, path } };
}

// ------------------------------------------------------------------ 快照/還原

/** 快照名稱:snap-<tick>,兩個核心都接受(C# 忽略 value、Python 以 value 為鍵)。 */
export function snapshotName(tick: number): string {
  return `snap-${Math.trunc(tick)}`;
}

/** snapshot:C# 只需 type;Python 以 value 為快照名稱。args 附模擬時間供教官站列表。 */
export function snapshotCommand(tick: number, t: number): SimCommand {
  return { type: 'snapshot', value: snapshotName(tick), args: { tick: Math.trunc(tick), t } };
}

/** restore:C# 讀 args.tick(回到記憶體中該 tick 之前最近的快照);Python 以 value 為名稱。 */
export function restoreCommand(tick: number): SimCommand {
  return { type: 'restore', value: snapshotName(tick), args: { tick: Math.trunc(tick) } };
}

// ------------------------------------------------------------------ 環境

export interface EnvironmentChange {
  wind?: { trueSpeed?: number; trueDir?: number; gustiness?: number };
  current?: { set?: number; drift?: number };
  waterDepth?: number;
  /** 能見度 (nm):兩個核心都尚未實作,照送供日後接上 */
  visibility_nm?: number;
}

/** 核心對 setEnvironment 各欄位的支援情形(供 UI 標示;依兩核心原始碼整理,2026-10)。 */
export type CoreSupport = 'both' | 'csharp' | 'python' | 'none';

export const ENVIRONMENT_FIELD_SUPPORT: Readonly<Record<'wind' | 'gustiness' | 'current' | 'waterDepth' | 'visibility', CoreSupport>> = {
  wind: 'both',
  gustiness: 'csharp',
  current: 'both',
  waterDepth: 'both',
  visibility: 'none',
};

export const CORE_SUPPORT_LABEL: Readonly<Record<CoreSupport, string>> = {
  both: '',
  csharp: '僅 C# 核心',
  python: '僅 Python 參考伺服器',
  none: '核心未實作',
};

function finite(v: number | undefined): v is number {
  return typeof v === 'number' && Number.isFinite(v);
}

/**
 * setEnvironment:只放有給的欄位(部分更新),角度正規化、速度與水深不為負。
 * 全部欄位都沒給時回傳 null(不送空指令)。
 */
export function setEnvironmentCommand(change: EnvironmentChange): SimCommand | null {
  const args: Record<string, unknown> = {};
  if (change.wind) {
    const w: Record<string, number> = {};
    if (finite(change.wind.trueSpeed)) w['trueSpeed'] = Math.max(0, round1(change.wind.trueSpeed));
    if (finite(change.wind.trueDir)) w['trueDir'] = round1(normalizeHeading(change.wind.trueDir));
    if (finite(change.wind.gustiness)) w['gustiness'] = Math.round(clamp(change.wind.gustiness, 0, 1) * 100) / 100;
    if (Object.keys(w).length > 0) args['wind'] = w;
  }
  if (change.current) {
    const c: Record<string, number> = {};
    if (finite(change.current.set)) c['set'] = round1(normalizeHeading(change.current.set));
    if (finite(change.current.drift)) c['drift'] = Math.max(0, round1(change.current.drift));
    if (Object.keys(c).length > 0) args['current'] = c;
  }
  if (finite(change.waterDepth) && change.waterDepth > 0) args['waterDepth'] = round1(change.waterDepth);
  if (finite(change.visibility_nm)) args['visibility_nm'] = Math.max(0, round1(change.visibility_nm));
  if (Object.keys(args).length === 0) return null;
  return { type: 'setEnvironment', args };
}

// ------------------------------------------------------------------ 故障

export interface FaultDefinition {
  /** 送給核心的名稱(value) */
  name: string;
  label: string;
  /** 對應規劃書第 8.1 節練習 */
  exercise?: string;
  /** 核心是否真的改變行為(SimCore FaultNames);否則只記錄與廣播 */
  modelled: boolean;
}

/** 內建故障目錄(規劃書第 7.2 節故障欄);前三項名稱來自 SimCore FaultNames.cs,其餘為教官站自定。 */
export const DEFAULT_FAULTS: readonly FaultDefinition[] = [
  { name: 'steeringGear', label: '舵機失效(卡舵)', exercise: 'E07', modelled: true },
  { name: 'mainEngine', label: '主機失效', exercise: 'E10', modelled: true },
  { name: 'bowThruster', label: '側推失效', exercise: 'F02', modelled: true },
  { name: 'gyroDrift', label: '電羅經漂移', exercise: 'E08', modelled: false },
  { name: 'gpsJump', label: 'GPS 跳點', exercise: 'E08', modelled: false },
  { name: 'radarFailure', label: '雷達失效', exercise: 'E03', modelled: false },
  { name: 'blackout', label: '全船失電', exercise: 'E09', modelled: false },
];

/**
 * 由 SimCore 的 FaultNames.cs 原始碼取出 `public const string X = "name";`(建置時以 ?raw 匯入)。
 * 回傳 名稱 → 摘要註解 的對應;讀不到或格式不符時回傳空物件。
 */
export function parseFaultNamesCs(source: string): Record<string, string> {
  const out: Record<string, string> = {};
  const re = /(?:\/\/\/\s*<summary>([^<]*)<\/summary>\s*)?public\s+const\s+string\s+\w+\s*=\s*"([A-Za-z0-9_]+)"\s*;/g;
  for (const m of source.matchAll(re)) {
    const name = m[2];
    if (name) out[name] = (m[1] ?? '').trim();
  }
  return out;
}

/**
 * 合併 SimCore 的名稱與內建目錄:SimCore 有的標為 modelled、保留其註解作標籤;
 * 內建目錄其餘項目保留(標為僅記錄)。
 */
export function mergeFaultCatalogue(fromCore: Record<string, string>, defaults: readonly FaultDefinition[] = DEFAULT_FAULTS): FaultDefinition[] {
  const coreNames = Object.keys(fromCore);
  const list: FaultDefinition[] = defaults.map((d) => {
    const inCore = coreNames.includes(d.name);
    return inCore ? { ...d, modelled: true } : { ...d, modelled: false };
  });
  for (const name of coreNames) {
    if (!list.some((d) => d.name === name)) {
      const def: FaultDefinition = { name, label: fromCore[name] || name, modelled: true };
      list.push(def);
    }
  }
  return list;
}

const FAULT_NAMES_CS = import.meta.glob('../../../SimCore/Engine/FaultNames.cs', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

/** 故障目錄與名稱來源(SimCore FaultNames.cs 讀得到時以其為準)。 */
export function faultCatalogue(): { faults: FaultDefinition[]; source: 'simcore' | 'builtin' } {
  const text = Object.values(FAULT_NAMES_CS)[0];
  const fromCore = text ? parseFaultNamesCs(text) : {};
  if (Object.keys(fromCore).length === 0) return { faults: [...DEFAULT_FAULTS], source: 'builtin' };
  return { faults: mergeFaultCatalogue(fromCore), source: 'simcore' };
}

export function injectFaultCommand(name: string): SimCommand {
  if (!name.trim()) throw new RangeError('故障名稱不可為空');
  return { type: 'injectFault', value: name.trim() };
}

/** clearFault:給名稱清除該項;不給名稱時 C# 核心清除全部(Python 參考伺服器則無作用)。 */
export function clearFaultCommand(name?: string): SimCommand {
  return name && name.trim() ? { type: 'clearFault', value: name.trim() } : { type: 'clearFault' };
}

// ------------------------------------------------------------------ 自船覆寫

export interface OwnShipOverride {
  x?: number;
  y?: number;
  lat?: number;
  lon?: number;
  heading?: number;
  /** 對水速度 (kn) */
  speed?: number;
  loading?: LoadingCondition;
  /** 拖船數量(核心未實作,照送) */
  tugs?: number;
}

/**
 * 自船覆寫:command.schema.json 沒有專用指令,以 reset 附 args 送出(Python 參考伺服器的 reset
 * 讀 args.x/y/heading/speed;C# 核心的 reset 目前忽略 args、回到情境初始狀態)。loading 與 tugs 兩核心皆未實作。
 */
export function ownShipOverrideCommand(o: OwnShipOverride): SimCommand {
  const args: Record<string, unknown> = {};
  if (finite(o.x)) args['x'] = round1(o.x);
  if (finite(o.y)) args['y'] = round1(o.y);
  if (finite(o.lat)) args['lat'] = o.lat;
  if (finite(o.lon)) args['lon'] = o.lon;
  if (finite(o.heading)) args['heading'] = round1(normalizeHeading(o.heading));
  if (finite(o.speed)) args['speed'] = round1(o.speed);
  if (o.loading) args['loading'] = o.loading;
  if (finite(o.tugs)) args['tugs'] = Math.max(0, Math.trunc(o.tugs));
  return { type: 'reset', args };
}

export const OWNSHIP_FIELD_SUPPORT: Readonly<Record<'position' | 'heading' | 'speed' | 'loading' | 'tugs', CoreSupport>> = {
  position: 'python',
  heading: 'python',
  speed: 'python',
  loading: 'none',
  tugs: 'none',
};

// ------------------------------------------------------------------ 時間倍率

/** 教官站提供的時間倍率選項(規劃書第 9.1 節:即時預設、×2 至 ×10 快轉、慢動作) */
export const TIME_SCALES: readonly number[] = [0.5, 1, 2, 5, 10];

function round1(v: number): number {
  const r = Math.round(v * 10) / 10;
  return r === 0 ? 0 : r;
}
