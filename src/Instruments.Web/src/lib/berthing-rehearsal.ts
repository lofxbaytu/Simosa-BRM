// 靠離泊預演自動組題(規劃書第 8.0 節「靠離泊預演」、第 9.1 節簡化教官模式「靠離泊預演模式可輸入當次航次的泊位、潮汐、預報風與拖船數自動組題」;R13)。
// 以 TypeScript 範本函式(而非 Handlebars 範本檔)產生情境物件:理由是船上離線、不增加執行時相依、位置/航向/繫船柱選擇需要幾何計算與型別檢查,
// 產生的物件可直接以 parseScenario 自我驗證、以 yaml.stringify 輸出下載、以 loadScenario args.scenario 送核心(C# ScenarioLoader.FromJsonElement、
// Python load_scenario_inline 走與 YAML 相同的載入路徑)。兩個範例輸出固定在 data/scenarios/templates/examples/(C# xunit 以 --validate 與 300 s 重播固定)。
//
// 幾何(與核心 LocalTangentPlane / ScenarioBodyPoint 相同的慣例):原點 = 泊位碼頭線起點 quay[0](里程 0),碼頭線走向 q(quay[0] → quay[1]),
// 沿碼頭單位向量 t、水側法向量 n(waterSide right = 走向右側)。靠哪一舷決定艏向:左舷靠 → 碼頭在左 → 走向右側為水域時艏向 = q,否則 q + 180;右舷靠相反。
// 靠泊:初始位置在泊位中心(碰墊列中點)沿艏向後退 0.75 nm、向水側 0.28 nm(合計約 0.8 nm),艏向與碼頭線平行,慢俥接近。
// 離泊:船舯在泊位中心、船體靠在碰墊上(碰墊高 + 半船寬 − 0.05 m 壓縮),4 纜 fast(艏纜/艏倒纜/艉倒纜/艉纜,繫船柱依艏艉位置自泊位定義挑選)。
// 初始 UKC:以與核心相同的最大吃水(ship-config maxDraft = max(艏、艉、平均);SimulationEngine/ScenarioValidator 用 ShipParticulars.MaxDraft_m)計算,
// ≤ 0 列入 summary.issues(核心 t=0 即 grounding → hardFail → exerciseEnded,面板不得載入),低於公司 UKC 下限時列入 warnings/notes。

import { stringify as stringifyYaml } from 'yaml';
import type { LoadingCondition, ShipId } from '../types/state.js';
import { normalizeHeading } from './angles.js';
import { wgs84ToEnu } from './external-forces.js';
import { AREA_FILES, loadingLabel, parseScenario, type ScenarioSummary } from './scenario.js';
import { getShipConfig, maxDraft } from './ship-config.js';

const M_PER_NM = 1852;
const DEG = Math.PI / 180;

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function finite(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

function r1(v: number): number {
  return Math.round(v * 10) / 10;
}

function r2(v: number): number {
  return Math.round(v * 100) / 100;
}

// ------------------------------------------------------------------ 泊位清單(data/areas)

export interface BerthBollardChoice {
  id: string;
  chainage_m: number;
  swl_t?: number;
}

/** 可選泊位(由建置時匯入的 data/areas/*.json berths[] 取得;碼頭線走向與水側供表單顯示)。 */
export interface BerthChoice {
  id: string;
  name: string;
  /** 情境 berth.file(相對專案根目錄) */
  file: string;
  /** 港口代碼(id 前綴:MLO 麥寮、KHH 高雄、…) */
  port: string;
  origin: { lat: number; lon: number };
  /** 碼頭線走向(quay[0] → quay[1],度) */
  quayBearing_deg: number;
  waterSide: 'left' | 'right';
  /** 碼頭線長度(m) */
  length_m: number;
  /** 碰墊列中點里程(泊位中心;無碰墊時取碼頭線中點) */
  centreChainage_m: number;
  fenderCount: number;
  /** 碰墊高(m;第一個碰墊型式;無資料 1.0) */
  fenderHeight_m: number;
  bollards: BerthBollardChoice[];
  /** 示意來源說明(泊位檔 source / description) */
  source: string;
  /** 預設基底水深(海圖基準,m;示意:泊位檔無此資料時依既有情境 E13/E14/E17/E20 的假設值) */
  defaultDepth_m: number;
}

/** 泊位預設基底水深(示意;data/areas 泊位檔沒有水深欄位,沿用既有情境假設值,表單可改)。 */
const DEFAULT_DEPTH_M: Readonly<Record<string, number>> = { 'MLO-OIL-3': 14, 'MLO-SOB-1': 11, 'KHH-SOB-1': 12, 'KHH-E20-OIL': 14 };

/**
 * 公司 UKC 下限 (m;參數集 company-procedures.yaml navigation.ukc.minimum_m = 1.0,SMS-NAV-012 3.3)。網頁離線、不載入參數集,
 * 此處只做組題時的提醒;實際評估由核心以參數集解析的 ukcMin 容許帶判定。
 */
const UKC_MINIMUM_M = 1.0;

/** 港口代碼 → 港區速限參數鍵(參數集 company-procedures.yaml;其他港口以固定 5 kn) */
const PORT_SPEED_PARAM: Readonly<Record<string, string>> = { MLO: 'port.mailiao.innerSpeedLimit_kn', KHH: 'port.kaohsiung.berthSpeedLimit_kn' };
const PORT_LABEL: Readonly<Record<string, string>> = { MLO: '麥寮', KHH: '高雄', TXG: '台中', KEL: '基隆', ANP: '安平' };

function fileKeyToPath(key: string): string {
  const file = key.replace(/\\/g, '/').split('/').pop() ?? key;
  return `data/areas/${file}`;
}

/** 列出全部可選泊位(依 id 排序)。 */
export function listBerthChoices(areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): BerthChoice[] {
  const out: BerthChoice[] = [];
  for (const [key, doc] of Object.entries(areaFiles)) {
    if (!isRecord(doc) || !Array.isArray(doc['berths'])) continue;
    const sharedTypes = isRecord(doc['fenderTypes']) ? doc['fenderTypes'] : {};
    for (const b of doc['berths']) {
      if (!isRecord(b) || typeof b['id'] !== 'string' || !Array.isArray(b['quay']) || b['quay'].length < 2) continue;
      const q0 = b['quay'][0];
      const q1 = b['quay'][1];
      if (!isRecord(q0) || !isRecord(q1)) continue;
      const lat0 = finite(q0['lat']);
      const lon0 = finite(q0['lon']);
      const lat1 = finite(q1['lat']);
      const lon1 = finite(q1['lon']);
      if (lat0 === undefined || lon0 === undefined || lat1 === undefined || lon1 === undefined) continue;
      const origin = { lat: lat0, lon: lon0 };
      const e = wgs84ToEnu(origin, lat1, lon1);
      const length = Math.hypot(e.x, e.y);
      if (length < 1) continue;
      const bearing = normalizeHeading(Math.atan2(e.x, e.y) / DEG);
      const types = { ...sharedTypes, ...(isRecord(b['fenderTypes']) ? b['fenderTypes'] : {}) };
      const fenders = Array.isArray(b['fenders']) ? b['fenders'].filter(isRecord) : [];
      const chainages = fenders.map((f) => finite(f['chainage_m'])).filter((c): c is number => c !== undefined);
      const centre = chainages.length > 0 ? (Math.min(...chainages) + Math.max(...chainages)) / 2 : length / 2;
      const firstType = fenders.length > 0 && typeof fenders[0]!['type'] === 'string' ? types[fenders[0]!['type'] as string] : undefined;
      const fenderHeight = isRecord(firstType) ? (finite(firstType['height_m']) ?? 1) : 1;
      const bollards: BerthBollardChoice[] = [];
      if (Array.isArray(b['bollards']))
        for (const bl of b['bollards']) {
          if (!isRecord(bl) || typeof bl['id'] !== 'string') continue;
          const ch = finite(bl['chainage_m']);
          if (ch === undefined) continue;
          const item: BerthBollardChoice = { id: bl['id'], chainage_m: ch };
          const swl = finite(bl['swl_t']);
          if (swl !== undefined) item.swl_t = swl;
          bollards.push(item);
        }
      bollards.sort((a, c) => a.chainage_m - c.chainage_m);
      const port = b['id'].split('-')[0] ?? '';
      out.push({
        id: b['id'],
        name: typeof b['name'] === 'string' ? b['name'] : b['id'],
        file: fileKeyToPath(key),
        port,
        origin,
        quayBearing_deg: r1(bearing),
        waterSide: b['waterSide'] === 'left' ? 'left' : 'right',
        length_m: r1(length),
        centreChainage_m: r1(centre),
        fenderCount: fenders.length,
        fenderHeight_m: fenderHeight,
        bollards,
        source: typeof b['source'] === 'string' ? b['source'] : typeof doc['source'] === 'string' ? doc['source'] : '',
        defaultDepth_m: DEFAULT_DEPTH_M[b['id']] ?? 12,
      });
    }
  }
  return out.sort((a, b) => a.id.localeCompare(b.id));
}

/** 泊位一句話:「麥寮油碼頭 3 號泊位(示意)· 碼頭線 085°、長 250 m、水側在走向右側(南)、碰墊 12、繫船柱 7」 */
export function berthChoiceLabel(b: BerthChoice): string {
  return `${b.name} · 碼頭線 ${Math.round(b.quayBearing_deg).toString().padStart(3, '0')}°、長 ${Math.round(b.length_m)} m、水側在走向${b.waterSide === 'right' ? '右' : '左'}側(${compass(b.quayBearing_deg + (b.waterSide === 'right' ? 90 : -90))})、碰墊 ${b.fenderCount}、繫船柱 ${b.bollards.length}`;
}

function compass(deg: number): string {
  const names = ['北', '東北', '東', '東南', '南', '西南', '西', '西北'];
  return names[Math.round(normalizeHeading(deg) / 45) % 8]!;
}

// ------------------------------------------------------------------ 輸入

export type RehearsalOperation = 'berthing' | 'unberthing';
export type AlongsideSide = 'port' | 'starboard';

export interface RehearsalWindShift {
  /** 幾秒後開始改變(自情境開始) */
  after_s: number;
  speed_kn: number;
  dir_deg: number;
  /** 漸變秒數 */
  ramp_s: number;
}

export interface BerthingRehearsalInput {
  operation: RehearsalOperation;
  shipId: ShipId;
  loading: LoadingCondition;
  berthId: string;
  /** 靠哪一舷 */
  side: AlongsideSide;
  /** 預定靠/離泊時刻(UTC,ISO 8601;秒可省略) */
  plannedUtc: string;
  /** 潮高(m,加到基底水深) */
  tideHeight_m: number;
  /** 基底水深(海圖基準,m) */
  baseDepth_m: number;
  current: { set_deg: number; drift_kn: number };
  wind: { speed_kn: number; dir_deg: number; gustiness: number };
  visibility_nm: number;
  /** 拖船數 0–2(馬力與連接點預設:各 40 t,船體水側艏/艉) */
  tugCount: 0 | 1 | 2;
  /** 拖船繫柱拖力(t;預設 40) */
  tugBollardPull_t?: number;
  /** 引水人在船(角色提示:簡報與 MPX 必要事件) */
  pilotOnBoard: boolean;
  /** 風變化(選填) */
  windShift?: RehearsalWindShift | null;
}

export interface BerthingRehearsalResult {
  /** 情境 id(紀錄用名稱;loadScenario args.id) */
  id: string;
  /** 情境物件(與 YAML 結構相同;loadScenario args.scenario) */
  scenario: Record<string, unknown>;
  /** YAML 文字(可下載) */
  yaml: string;
  /** parseScenario 自我驗證的摘要(issues 應為空;初始 UKC ≤ 0 亦列入 issues) */
  summary: ScenarioSummary;
  /** 組題說明(初始位置、艏向、繫船柱等;顯示用;含 warnings) */
  notes: string[];
  /** 警告(不阻止載入;目前:初始 UKC 低於公司下限) */
  warnings: string[];
  /** 組題所用最大吃水 (m;= 核心 ShipParticulars.MaxDraft_m) */
  draftMax_m: number;
  /** 初始 UKC (m) = 水深 − 最大吃水(未計 squat) */
  ukc_m: number;
}

/** 表單預設值(麥寮油碼頭 3 號泊位、No.1 滿載左舷靠、2 艘拖船)。 */
export function defaultRehearsalInput(now: Date = new Date()): BerthingRehearsalInput {
  const planned = new Date(now.getTime());
  planned.setUTCMinutes(0, 0, 0);
  planned.setUTCHours(planned.getUTCHours() + 2);
  return {
    operation: 'berthing',
    shipId: 'FSB1',
    loading: 'full',
    berthId: 'MLO-OIL-3',
    side: 'port',
    plannedUtc: planned.toISOString().slice(0, 16) + 'Z',
    tideHeight_m: 1.0,
    baseDepth_m: DEFAULT_DEPTH_M['MLO-OIL-3'] ?? 12,
    current: { set_deg: 250, drift_kn: 0.3 },
    wind: { speed_kn: 15, dir_deg: 20, gustiness: 0.2 },
    visibility_nm: 5,
    tugCount: 2,
    pilotOnBoard: true,
    windShift: null,
  };
}

/** 預定時刻正規化為 ISO 8601(秒補 00、Z);無法解析時回傳 null。 */
export function normalizePlannedUtc(text: string): string | null {
  const t = text.trim();
  if (!t) return null;
  const withZ = /[zZ]$|[+-]\d\d:?\d\d$/.test(t) ? t : `${t}Z`;
  const d = new Date(withZ);
  if (Number.isNaN(d.getTime())) return null;
  return d.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

// ------------------------------------------------------------------ 組題

interface Frame {
  t: { x: number; y: number };
  n: { x: number; y: number };
  heading_deg: number;
  /** +1:艏向與碼頭走向相同(里程沿艏向增加);−1:相反 */
  dir: 1 | -1;
}

function frameFor(b: BerthChoice, side: AlongsideSide): Frame {
  const q = b.quayBearing_deg * DEG;
  const t = { x: Math.sin(q), y: Math.cos(q) };
  const n = b.waterSide === 'right' ? { x: t.y, y: -t.x } : { x: -t.y, y: t.x };
  // 左舷靠:碼頭在左 → 水域在右 → 走向右側為水域時艏向 = 走向
  const along = (side === 'port') === (b.waterSide === 'right');
  return { t, n, heading_deg: normalizeHeading(along ? b.quayBearing_deg : b.quayBearing_deg + 180), dir: along ? 1 : -1 };
}

function point(b: BerthChoice, f: Frame, chainage: number, off: number): { x: number; y: number } {
  return { x: r2(chainage * f.t.x + off * f.n.x), y: r2(chainage * f.t.y + off * f.n.y) };
}

/** 依艏艉里程挑 4 條纜的繫船柱(艏纜向艏外、艉纜向艉外、倒纜在艏艉附近向內);不足 4 個不同繫船柱時回傳 null。 */
export function chooseBollards(bollards: readonly BerthBollardChoice[], bowChainage: number, sternChainage: number, dir: 1 | -1): { head: string; fwdSpring: string; aftSpring: string; stern: string } | null {
  if (bollards.length < 4) return null;
  const used = new Set<string>();
  const pick = (target: number, prefer: (c: number) => boolean): string | null => {
    const sorted = [...bollards].filter((b) => !used.has(b.id)).sort((a, c) => Math.abs(a.chainage_m - target) - Math.abs(c.chainage_m - target) || a.chainage_m - c.chainage_m);
    const chosen = sorted.find((b) => prefer(b.chainage_m)) ?? sorted[0];
    if (!chosen) return null;
    used.add(chosen.id);
    return chosen.id;
  };
  // 艏纜:艏前方 15–90 m 內最遠者;艉纜:艉後方;倒纜:距艏/艉 25–60 m 向船內
  const ahead = (c: number) => (c - bowChainage) * dir;
  const astern = (c: number) => (sternChainage - c) * dir;
  const head = pick(bowChainage + dir * 60, (c) => ahead(c) >= 10 && ahead(c) <= 90);
  const stern = pick(sternChainage - dir * 60, (c) => astern(c) >= 10 && astern(c) <= 90);
  const fwdSpring = pick(bowChainage - dir * 40, (c) => ahead(c) <= 0 && ahead(c) >= -70);
  const aftSpring = pick(sternChainage + dir * 40, (c) => astern(c) <= 0 && astern(c) >= -70);
  if (!head || !stern || !fwdSpring || !aftSpring) return null;
  return { head, fwdSpring, aftSpring, stern };
}

function utcLabel(iso: string): string {
  return iso.replace('T', ' ').replace(/:\d\d(?:\.\d+)?Z$/, ' UTC');
}

function formatIso(d: Date): string {
  return d.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/**
 * 組出靠離泊預演情境。回傳的 scenario 與 YAML 相同結構;summary 為 parseScenario 自我驗證結果(issues 空 = 通過最小檢查)。
 * 泊位不存在、預定時刻無法解析、繫船柱不足時丟出 Error。
 */
export function composeBerthingRehearsal(input: BerthingRehearsalInput, areaFiles: Readonly<Record<string, unknown>> = AREA_FILES): BerthingRehearsalResult {
  const berth = listBerthChoices(areaFiles).find((b) => b.id === input.berthId);
  if (!berth) throw new Error(`找不到泊位 ${input.berthId}(data/areas)`);
  const planned = normalizePlannedUtc(input.plannedUtc);
  if (!planned) throw new Error(`預定時刻無法解析:${input.plannedUtc}`);
  const ship = getShipConfig(input.shipId);
  const lpp = ship.lpp_m;
  const halfBeam = ship.breadth_m / 2;
  const f = frameFor(berth, input.side);
  const berthing = input.operation === 'berthing';
  const notes: string[] = [];
  const warnings: string[] = [];

  const waterDepth = r2(Math.max(0.1, input.baseDepth_m + input.tideHeight_m));
  // 最大吃水(與核心擱淺/UKC 判定相同;FSB1 壓載平均 3.72 m 但艉 5.41 m)
  const draft = r2(maxDraft(ship, input.loading));
  const ukc = r2(waterDepth - draft);
  const centre = berth.centreChainage_m;
  const bow = centre + f.dir * lpp / 2;
  const stern = centre - f.dir * lpp / 2;
  const alongsideOffset = r2(berth.fenderHeight_m + halfBeam - 0.05);
  const sideLabel = input.side === 'port' ? '左舷' : '右舷';
  const plannedDate = new Date(planned);
  const startUtc = berthing ? formatIso(new Date(plannedDate.getTime() - 20 * 60_000)) : planned;
  const stamp = planned.replace(/[-:]/g, '').slice(0, 13).replace('T', '-');
  const id = `BR_${berthing ? 'berthing' : 'unberthing'}_${berth.id}_${stamp}`;
  const seed = Number(planned.replace(/\D/g, '').slice(0, 12)) % 2_000_000_000;
  const speedParam = PORT_SPEED_PARAM[berth.port];
  const portName = PORT_LABEL[berth.port] ?? berth.port;

  // 初始位置
  let position: { x: number; y: number };
  let heading: number;
  let speed: number;
  let telegraph: string;
  if (berthing) {
    position = point(berth, f, centre - f.dir * 0.75 * M_PER_NM, alongsideOffset + 0.28 * M_PER_NM);
    heading = f.heading_deg;
    speed = 3;
    telegraph = 'DSAH';
    notes.push(`初始位置:泊位中心沿艏向後退 0.75 nm、離碼頭線 ${r1((alongsideOffset + 0.28 * M_PER_NM) / M_PER_NM)} nm(合計約 0.8 nm),艏向 ${Math.round(heading).toString().padStart(3, '0')}° 與碼頭線平行,微速前進 3 kn`);
  } else {
    position = point(berth, f, centre, alongsideOffset);
    heading = f.heading_deg;
    speed = 0;
    telegraph = 'STOP';
    notes.push(`初始位置:${sideLabel}靠在碰墊上(碰墊 ${berth.fenderHeight_m} m + 半船寬 ${halfBeam} m − 壓縮 0.05 m = 離碼頭線 ${alongsideOffset} m),船舯在泊位中心里程 ${centre} m,艏向 ${Math.round(heading).toString().padStart(3, '0')}°`);
  }

  // 繫纜與繫船柱
  const fairleadFwd = input.side === 'port' ? 'fwdPort' : 'fwdStbd';
  const fairleadAft = input.side === 'port' ? 'aftPort' : 'aftStbd';
  const bollards = chooseBollards(berth.bollards, bow, stern, f.dir);
  if (!bollards) throw new Error(`泊位 ${berth.id} 的繫船柱不足以配置 4 條纜`);
  const mbl = 50;
  const lines = [
    { id: 'HEAD', fairlead: fairleadFwd, bollard: bollards.head, mbl_t: mbl, length_m: 150, initial: berthing ? 'stowed' : 'fast' },
    { id: 'FWDSPRING', fairlead: fairleadFwd, bollard: bollards.fwdSpring, mbl_t: mbl, length_m: 150, initial: berthing ? 'stowed' : 'fast' },
    { id: 'AFTSPRING', fairlead: fairleadAft, bollard: bollards.aftSpring, mbl_t: mbl, length_m: 150, initial: berthing ? 'stowed' : 'fast' },
    { id: 'STERN', fairlead: fairleadAft, bollard: bollards.stern, mbl_t: mbl, length_m: 150, initial: berthing ? 'stowed' : 'fast' },
  ];
  notes.push(`繫纜:艏纜 → ${bollards.head}、艏倒纜 → ${bollards.fwdSpring}、艉倒纜 → ${bollards.aftSpring}、艉纜 → ${bollards.stern}(${berthing ? '收存,靠妥後帶纜' : '已繫妥'})`);

  // 拖船(船體水側:靠左舷 → 右舷艏/艉;推向碼頭 = 力方向朝靠泊舷)
  const tugSide = input.side === 'port' ? 'Stbd' : 'Port';
  const pushDir = input.side === 'port' ? 270 : 90;
  const pull = input.tugBollardPull_t && input.tugBollardPull_t > 0 ? input.tugBollardPull_t : 40;
  const tugs: Array<Record<string, unknown>> = [];
  const tugNames = [`${portName}1號`, `${portName}2號`];
  const attachPoints = input.tugCount === 1 ? [`aft${tugSide}`] : [`fwd${tugSide}`, `aft${tugSide}`];
  for (let i = 0; i < input.tugCount; i++)
    tugs.push({
      id: `TG${i + 1}`,
      name: tugNames[i],
      bollardPull_t: pull,
      attach: attachPoints[i],
      lineLength_m: 30,
      modes: ['push', 'pull'],
      modeChange_s: 30,
      initial: { mode: 'standby', direction_deg: pushDir, power_pct: 0 },
    });
  if (input.tugCount > 0) notes.push(`拖船 ${input.tugCount} 艘(各 ${pull} t):${attachPoints.map((a, i) => `TG${i + 1} ${a}`).join('、')},帶纜推頂待命`);
  else notes.push('無拖船');

  // 區域:泊位區(離泊結束判定)與港區速限
  const zoneLo = Math.min(0, centre - lpp) - 60;
  const zoneHi = Math.max(berth.length_m, centre + lpp) + 60;
  const berthArea = [point(berth, f, zoneLo, -10), point(berth, f, zoneHi, -10), point(berth, f, zoneHi, 110), point(berth, f, zoneLo, 110)];
  const speedArea = [point(berth, f, zoneLo - 700, -60), point(berth, f, zoneHi + 700, -60), point(berth, f, zoneHi + 700, 900), point(berth, f, zoneLo - 700, 900)];
  const zones: Array<Record<string, unknown>> = [
    { id: 'harbourSpeed', name: `${portName}港區速限區(示意)`, type: 'speedLimit', speedLimit_kn: speedParam ? `\${params:${speedParam}}` : 5, polygon: speedArea },
    { id: 'berthArea', name: '泊位區(示意;船舯離碼頭 110 m 內)', type: 'reporting', polygon: berthArea },
  ];

  // 時間表
  const plannedLabel = utcLabel(planned);
  const tideText = `潮窗:預定${berthing ? '靠泊' : '離泊'} ${plannedLabel},潮高 ${input.tideHeight_m} m(基底水深 ${input.baseDepth_m} m → 水深 ${waterDepth} m,最大吃水 ${draft} m,UKC 約 ${ukc} m);潮流 ${Math.round(input.current.set_deg).toString().padStart(3, '0')}°/${input.current.drift_kn} kn`;
  const briefingVoice = berthing
    ? `船長,我們${sideLabel}靠${berth.name.replace(/[(\uff08]示意[)\uff09]/g, '')}。${input.tugCount > 0 ? `${input.tugCount === 2 ? '兩艘拖船在' + (input.side === 'port' ? '右舷' : '左舷') + '艏艉帶纜推頂' : '一艘拖船在' + (input.side === 'port' ? '右舷' : '左舷') + '艉帶纜推頂'},接近時拉住控速。` : '沒有拖船,以俥舵與側推靠攏。'}風 ${Math.round(input.wind.dir_deg).toString().padStart(3, '0')}° ${input.wind.speed_kn} 節,靠攏時速度要慢。`
    : `船長,我們準備離泊。${input.tugCount > 0 ? `拖船在${input.side === 'port' ? '右舷' : '左舷'}帶纜,先開${f.dir === 1 ? '艏' : '艉'}再退出。` : '沒有拖船,以側推與俥舵先開艏或開艉。'}風 ${Math.round(input.wind.dir_deg).toString().padStart(3, '0')}° ${input.wind.speed_kn} 節。`;
  const timeline: Array<Record<string, unknown>> = [
    {
      id: 'pilotMpx',
      name: input.pilotOnBoard ? '引水人 MPX(船長/引水人資訊交換)' : '靠離泊計畫簡報',
      when: { type: 'time', atLeast: 5 },
      actions: [
        input.pilotOnBoard
          ? { voiceScript: { role: 'pilot', channel: 'bridge', text: briefingVoice, lang: 'zh-TW' } }
          : { message: { to: 'all', text: `${berthing ? '靠泊' : '離泊'}前:請完成操船計畫簡報(${berthing ? '接近航向與速度、拖船用法、繫纜順序' : '解纜順序、開艏/開艉、側推與俥舵'}、應變)。`, severity: 'info' } },
        { message: { to: 'instructor', text: `觀察:${input.pilotOnBoard ? 'MPX(' : '簡報('}${berthing ? '靠泊計畫、拖船、繫纜順序' : '離泊計畫、解纜順序、拖船'}、應變)${berthing ? '、橫向接近速度、碰墊接觸能量' : '、離泊時艉擺碰墊、再碰回速度'}`, severity: 'info' } },
      ],
    },
  ];
  if (input.tugCount > 0)
    timeline.push({
      id: 'tugsInPosition',
      name: '拖船就位',
      when: { type: 'time', atLeast: 60 },
      actions: [
        { voiceScript: { role: 'tug', channel: 'vhfPort', text: `${tugNames.slice(0, input.tugCount).join('、')}報告:已在${input.side === 'port' ? '右舷' : '左舷'}${input.tugCount === 2 ? '艏艉' : '艉'}帶纜就位,聽候指揮。`, lang: 'zh-TW' } },
        { message: { to: 'instructor', text: '拖船就位:觀察拖船令是否清楚、拖船是否複誦確認(必要事件 tugOrderConfirm)', severity: 'info' } },
      ],
    });
  timeline.push({ id: 'tideWindow', name: '潮窗提示', when: { type: 'time', atLeast: 120 }, actions: [{ message: { to: 'all', text: tideText, severity: 'info' } }] });
  const ws = input.windShift;
  if (ws && ws.after_s > 0)
    timeline.push({
      id: 'windShift',
      name: '預報風變化',
      phase: 'degraded',
      when: { type: 'time', atLeast: ws.after_s },
      actions: [
        { setEnvironment: { wind: { trueSpeed: ws.speed_kn, trueDir: normalizeHeading(ws.dir_deg), gustiness: input.wind.gustiness }, ramp_s: Math.max(0, ws.ramp_s) } },
        { message: { to: 'all', text: `風向轉 ${Math.round(normalizeHeading(ws.dir_deg)).toString().padStart(3, '0')}°、${ws.speed_kn} kn(${Math.max(0, ws.ramp_s)} s 內漸變)。`, severity: 'warning' } },
      ],
    });
  if (berthing) {
    timeline.push({ id: 'firstContact', name: '首次碰墊接觸', when: { type: 'event', kind: 'fenderContact' }, actions: [{ message: { to: 'instructor', text: '首次碰墊接觸:見 summary.berthing.contacts(接近速度、能量比)', severity: 'info' } }] });
    timeline.push({ id: 'allFast', name: '繫纜完成(教官)', manual: true, actions: [{ message: { to: 'all', text: '全部纜繩已繫妥,靠泊完成。', severity: 'info' } }, { endExercise: { reason: '靠泊完成' } }] });
  } else {
    timeline.push({ id: 'clearOfBerth', name: '離開泊位區', when: { type: 'zone', id: 'berthArea', inside: false }, actions: [{ message: { to: 'all', text: '已離開泊位區,離泊完成。', severity: 'info' } }, { endExercise: { reason: '離泊完成' } }] });
  }
  timeline.push({ id: 'groundingEnd', name: '擱淺', delay_s: 2, when: { type: 'event', kind: 'grounding' }, actions: [{ endExercise: { reason: '擱淺' } }] });

  // 評估(參數集 berthing.* / navigation.ukc.*;硬性失敗:擱淺、碼頭硬接觸、斷纜)
  const bands: Array<Record<string, unknown>> = [];
  if (berthing) {
    bands.push({ id: 'approachSpeed', metric: 'berthing.maxLateralSpeed_mps', warn: '${params:berthing.approachSpeed_mps}', fail: '${params:berthing.approachSpeedFail_mps}', penalty: 10, failPenalty: 25 });
    bands.push({ id: 'berthPosition', metric: 'berthing.allFastPositionError_m', warn: '${params:berthing.allFastPositionTolerance_m}', fail: '2x', penalty: 10, failPenalty: 20 });
  } else {
    bands.push({ id: 'reContactSpeed', metric: 'berthing.maxReContactSpeed_mps', warn: '${params:berthing.approachSpeed_mps}', fail: '${params:berthing.approachSpeedFail_mps}', penalty: 10, failPenalty: 25 });
  }
  bands.push({ id: 'contactEnergy', metric: 'berthing.maxEnergyRatio', warn: 0.8, fail: 1.0, penalty: 10, failPenalty: 25 });
  bands.push({ id: 'fenderOverload', metric: 'berthing.fenderOverloads', warn: 0, fail: 1, penalty: 10, failPenalty: 20 });
  bands.push({ id: 'lineTension', metric: 'berthing.maxLineTensionRatio', warn: 0.55, fail: 0.8, penalty: 5, failPenalty: 15 });
  bands.push({ id: 'ukcMin', metric: 'ukc.min_m', warn: '${params:navigation.ukc.minimum_m}', fail: 0.5, direction: 'below', penalty: 10, failPenalty: 25 });
  bands.push({ id: 'speed', metric: 'manoeuvring.timeOverSpeedLimit_s', warn: 0, fail: 60, penalty: 5, failPenalty: 10 });
  const required: Array<Record<string, unknown>> = [
    {
      id: 'mpx',
      after: { event: 'timelineTriggered', itemId: 'pilotMpx' },
      expect: { acknowledge: { kind: 'checklist' } },
      within_s: 300,
      penalty: 10,
    },
  ];
  if (input.tugCount > 0)
    required.push({
      id: 'tugOrderConfirm',
      after: { event: 'tugCommand' },
      expect: { any: [{ acknowledge: { kind: 'verbal' } }, { acknowledge: { kind: 'report' } }] },
      within_s: 60,
      penalty: 5,
    });

  const briefingText = [
    `${ship.name}(${input.shipId})${loadingLabel(input.loading)},${berthing ? `${sideLabel}靠` : `${sideLabel}靠泊中,準備離開`}${berth.name}(碼頭線 ${Math.round(berth.quayBearing_deg).toString().padStart(3, '0')}°、長 ${Math.round(berth.length_m)} m、碰墊 ${berth.fenderCount} 個、繫船柱 ${berth.bollards.length} 個;泊位為示意值)。`,
    `預定${berthing ? '靠泊' : '離泊'}時刻 ${plannedLabel};潮高 ${input.tideHeight_m} m、水深 ${waterDepth} m(最大吃水 ${draft} m,UKC 約 ${ukc} m);潮流 ${Math.round(input.current.set_deg).toString().padStart(3, '0')}°/${input.current.drift_kn} kn。`,
    `預報風 ${Math.round(input.wind.dir_deg).toString().padStart(3, '0')}° ${input.wind.speed_kn} kn(陣風 ${input.wind.gustiness})${ws && ws.after_s > 0 ? `,${Math.round(ws.after_s / 60)} 分鐘後轉 ${Math.round(normalizeHeading(ws.dir_deg)).toString().padStart(3, '0')}° ${ws.speed_kn} kn` : ''};能見度 ${input.visibility_nm} nm。`,
    input.tugCount > 0 ? `拖船 ${input.tugCount} 艘(${tugNames.slice(0, input.tugCount).join('、')},各 ${pull} t)在${input.side === 'port' ? '右舷' : '左舷'}${input.tugCount === 2 ? '艏艉' : '艉'}帶纜推頂(推 ↔ 拉換位約 30 s)。` : '無拖船可用:以俥舵與側推操船。',
    input.pilotOnBoard ? `引水人在船(由帶領者扮演):請完成 MPX(${berthing ? '靠泊計畫、拖船位置與用法、繫纜順序' : '離泊計畫、解纜順序、拖船用法'}、應變),並以檢查表確認。` : '無引水人:船長自行操船,請完成操船計畫簡報並以檢查表確認。',
    `${berthing ? `靠泊接近速度依公司程序(參數集 berthing.approachSpeed_mps);繫纜順序艏纜 → 艏倒纜 → 艉倒纜 → 艉纜(繫船柱 ${bollards.head}/${bollards.fwdSpring}/${bollards.aftSpring}/${bollards.stern});繫妥後由教官按「繫纜完成」結束。` : `解纜順序先單綁再解;船舯離碼頭超過 110 m(離開泊位區)即結束。`}`,
  ].join('\n');
  const roles: Array<Record<string, unknown>> = [];
  if (input.pilotOnBoard) roles.push({ role: 'pilot', playedBy: 'leader', required: true });
  roles.push({ role: 'master', playedBy: 'leader' }, { role: 'officer', required: true });
  if (input.tugCount > 0) roles.push({ role: 'tug', playedBy: 'voiceScript' });
  roles.push({ role: 'mooring', playedBy: 'leader' });

  const scenario: Record<string, unknown> = {
    id,
    name: `${berthing ? '靠泊預演' : '離泊預演'} ${berth.name.replace(/[(\uff08]示意[)\uff09]/g, '')} ${plannedLabel}`,
    description: `${berthing ? `${sideLabel}靠` : `自`}${berth.name.replace(/[(\uff08]示意[)\uff09]/g, '')}${berthing ? '' : '離泊'}(當次航次預演:潮高 ${input.tideHeight_m} m、風 ${Math.round(input.wind.dir_deg).toString().padStart(3, '0')}°/${input.wind.speed_kn} kn、拖船 ${input.tugCount} 艘${input.pilotOnBoard ? '、引水人在船' : ''});由簡化教官模式靠離泊預演表單自動組題。`,
    seed,
    startTimeUtc: startUtc,
    timeScale: 1,
    ship: { id: input.shipId, loading: input.loading },
    origin: { lat: berth.origin.lat, lon: berth.origin.lon },
    initial: { position, heading: r1(heading), speed, telegraph, rudder: 0 },
    environment: {
      wind: { trueSpeed: input.wind.speed_kn, trueDir: normalizeHeading(input.wind.dir_deg), gustiness: Math.min(1, Math.max(0, input.wind.gustiness)) },
      current: { set: normalizeHeading(input.current.set_deg), drift: Math.max(0, input.current.drift_kn) },
      waterDepth,
      visibility_nm: Math.max(0.05, input.visibility_nm),
    },
  };
  if (tugs.length > 0) scenario['tugs'] = tugs;
  const berthSection: Record<string, unknown> = { file: berth.file, id: berth.id, lines };
  if (!berthing) berthSection['departedDistance_m'] = 20;
  scenario['berth'] = berthSection;
  scenario['briefing'] = { text: briefingText, roles, minParticipants: 2, duration_s: 2400 };
  scenario['zones'] = zones;
  scenario['timeline'] = timeline;
  scenario['assessment'] = {
    parameterSet: 'data/parameters/company-procedures.yaml',
    hardFail: [
      { id: 'grounding', rule: 'grounding' },
      { id: 'quayContact', rule: 'event', kind: 'quayContact' },
      { id: 'mooringLineParted', rule: 'event', kind: 'mooringLineParted' },
    ],
    bands,
    required,
    scoring: { start: 100, passMark: '${params:assessment.passMark}' },
  };

  // 參數引用加引號(與 data/scenarios 既有寫法一致;流式 { } 內 } 會結束純量,區塊式雖可不加引號但統一加上)
  const body = stringifyYaml(scenario, { lineWidth: 0 }).replace(/: (\$\{params:[^}]+\})$/gm, ': "$1"');
  const yaml = `# ${scenario['name']}\n# 由簡化教官模式「靠離泊預演」自動組題(規劃書第 8.0 節、第 9.1 節;src/Instruments.Web/src/lib/berthing-rehearsal.ts);泊位與碰墊為示意值。\n` + body;
  const summary = parseScenario(yaml, `${id}.yaml`);
  summary.path = '';
  // 初始 UKC 檢查(與核心 ScenarioValidator「初始 UKC ≤ 0(初始即擱淺)」同判準;Host 只把驗證錯誤印在主控台,須在組題時擋下)
  if (ukc <= 0) summary.issues.push(`initial.position:初始 UKC ${ukc} m ≤ 0(水深 ${waterDepth} m − 最大吃水 ${draft} m;核心載入即擱淺、硬性失敗),請提高基底水深/潮高或改裝載狀態`);
  else if (ukc < UKC_MINIMUM_M) {
    warnings.push(`注意:初始 UKC ${ukc} m 低於公司 UKC 下限 ${UKC_MINIMUM_M} m(navigation.ukc.minimum_m;以最大吃水 ${draft} m 計,未計 squat),ukcMin 容許帶一開始即為警告`);
    notes.push(...warnings);
  }
  return { id, scenario, yaml, summary, notes, warnings, draftMax_m: draft, ukc_m: ukc };
}
