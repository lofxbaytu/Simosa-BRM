// 情境編輯器環境進階區段(lib/scenario-editor.ts 第二十輪;設計文件 docs/design/scenario-engine.md 第 6.1 節;R3/R4):
// environment.tide(潮高表 / 調和分潮)、environment.waves、environment.current 的 table / tidal / field、environment.shipInteraction、ship.roll、
// ship.seakeeping(第二十九輪;R4 練習編輯、R2「浪」;浪一階運動 升沉/縱搖,docs/design/physics-waves.md 第 11 節)
// 的讀入 / 新增 / 修改 / 寫回結構、驗證規則(與核心 Scenario.StructuralIssues 相同訊息)、合成情境與情境庫(E06b/E18b/E17b)的 round-trip、
// 草稿版本 2 → 4、3 → 4 升級。
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';
import { describe, expect, it } from 'vitest';
import { parseScenario } from '../src/lib/scenario.js';
import {
  DRAFT_STORAGE_KEY,
  DRAFT_VERSION,
  SEAKEEPING_DEFAULTS,
  SEAKEEPING_FIELDS,
  SEAKEEPING_NO_WAVES_WARNING,
  addCurrentTablePoint,
  addTideConstituent,
  addTidePoint,
  blankDraft,
  currentField,
  currentFieldIssues,
  currentTablePoints,
  currentTimeModel,
  estimateGmL,
  estimateHeavePeriod,
  estimatePitchPeriod,
  estimateRollPeriod,
  fieldArrayText,
  fromYaml,
  loadDraft,
  parseFieldArrayText,
  patchCurrentModel,
  patchCurrentTablePoint,
  patchRoll,
  patchSeakeeping,
  patchShipInteraction,
  patchTide,
  patchTideConstituent,
  patchTidePoint,
  patchWaves,
  removeCurrentTablePoint,
  removeTideConstituent,
  removeTidePoint,
  rollPeriodEstimate,
  saveDraft,
  scenarioHasWaves,
  seakeepingEnabled,
  seakeepingHydrostatics,
  seakeepingPeriodEstimate,
  setCurrentFieldArray,
  setCurrentFieldEnabled,
  setCurrentTableTimeKey,
  setCurrentTimeModel,
  setRollEnabled,
  setSeakeepingEnabled,
  setShipInteractionEnabled,
  setTideMode,
  setTideTimeKey,
  setWavesEnabled,
  tablePointTimeKey,
  tideConstituents,
  tideMode,
  tidePoints,
  toScenarioObject,
  toYaml,
  utcText,
  validateDraft,
  validateEnvironmentExtras,
  type DraftStorage,
  type RawSection,
  type ScenarioDraft,
} from '../src/lib/scenario-editor.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const scenarioDir = path.resolve(here, '../../../data/scenarios');
const read = (f: string) => readFileSync(path.join(scenarioDir, f), 'utf-8');

class MemoryStorage implements DraftStorage {
  readonly map = new Map<string, string>();
  getItem(k: string): string | null {
    return this.map.get(k) ?? null;
  }
  setItem(k: string, v: string): void {
    this.map.set(k, v);
  }
  removeItem(k: string): void {
    this.map.delete(k);
  }
}

/** 含全部環境進階區段的合成情境(情境庫尚無 field / shipInteraction / roll 的檔案;seakeeping 另有 E17b)。 */
const FULL_YAML = `id: ENV
name: 環境進階
startTimeUtc: "2026-07-07T06:00:00Z"
ship:
  id: FSB1
  loading: full
  roll: { enabled: true, naturalPeriod_s: 10.5, dampingRatio: 0.08, windHeelGain: 1.0, turnHeelGain: 1.2, waveGain: 0.8 }
  seakeeping: { enabled: true, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 120 }
initial: { position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8 }
environment:
  wind: { trueSpeed: 18, trueDir: 40 }
  current:
    set: 85
    drift: 2.5
    tidal:
      flood: { speed_kn: 2.5, direction_deg: 85 }
      ebb: { speed_kn: 3.0, direction_deg: 265 }
      slackWindow_min: 20
    field:
      grid: { x0: -200, y0: -100, dx: 100, dy: 100, nx: 3, ny: 2 }
      u_kn: [[0, 0.5, 1], [0.2, 0.7, 1.2]]
      v_kn: [[1, 0.8, 0.6], [0.9, 0.7, 0.5]]
      outside: zero
      timeScale: tidal
  waterDepth: 14
  tide:
    harmonic:
      meanLevel_m: 0.3
      constituents:
        - { name: M2, amplitude_m: 1.2, period_s: 44714, phase_deg: 30 }
        - { name: S2, amplitude_m: 0.4, period_s: 43200 }
  waves: { significantHeight_m: 2.0, peakPeriod_s: 7.0, direction_deg: 90, spectrum: jonswap, gamma: 3.3 }
  shipInteraction: { enabled: true, coefficientScale: 1.0, eventThreshold_t: 3 }
  seabed: mud
assessment:
  hardFail: [{ id: grounding, rule: grounding }]
`;

describe('讀入與 round-trip', () => {
  it('合成情境:各區段讀入草稿、不改值寫回深相等、未涵蓋的 seabed 原樣保留', () => {
    const d = fromYaml(FULL_YAML, 'ENV');
    expect(d.version).toBe(4);
    expect(DRAFT_VERSION).toBe(4);
    expect(tideMode(d)).toBe('harmonic');
    expect(tideConstituents(d)).toHaveLength(2);
    expect(d.waves).toEqual({ significantHeight_m: 2.0, peakPeriod_s: 7.0, direction_deg: 90, spectrum: 'jonswap', gamma: 3.3 });
    expect(currentTimeModel(d)).toBe('tidal');
    expect(currentField(d)?.['outside']).toBe('zero');
    expect(d.environment).toEqual({ windSpeed: 18, windDir: 40, currentSet: 85, currentDrift: 2.5, waterDepth: 14 });
    expect(d.shipInteraction).toEqual({ enabled: true, coefficientScale: 1.0, eventThreshold_t: 3 });
    expect(d.roll).toEqual({ enabled: true, naturalPeriod_s: 10.5, dampingRatio: 0.08, windHeelGain: 1.0, turnHeelGain: 1.2, waveGain: 0.8 });
    expect(d.seakeeping).toEqual({ enabled: true, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 120 });
    expect(parseYaml(toYaml(d))).toEqual(parseYaml(FULL_YAML));
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.environment.current?.source).toBe('field+tidal');
    // 序列化後輸出相同
    expect(toYaml(JSON.parse(JSON.stringify(d)) as ScenarioDraft)).toBe(toYaml(d));
  });

  it('E06b(潮高表 + 潮流)與 E18b(潮高表)讀入與寫回', () => {
    const e06b = fromYaml(read('E06b_tidal_stream.yaml'), 'E06b');
    expect(tideMode(e06b)).toBe('table');
    expect(tidePoints(e06b)).toHaveLength(8);
    expect(tablePointTimeKey(tidePoints(e06b))).toBe('t');
    expect(currentTimeModel(e06b)).toBe('tidal');
    expect(currentField(e06b)).toBeNull();
    expect(parseYaml(toYaml(e06b))).toEqual(parseYaml(read('E06b_tidal_stream.yaml')));
    const e18b = fromYaml(read('E18b_tide_table.yaml'), 'E18b');
    expect(tidePoints(e18b).map((p) => p['height_m'])).toEqual([1.9, 1.85, 1.6, 1.5, 1.4, 1.2]);
    expect(currentTimeModel(e18b)).toBe('none');
    expect(e18b.waves).toBeNull();
    expect(e18b.roll).toBeNull();
    expect(e18b.seakeeping).toBeNull();
    expect(validateDraft(e18b).issues).toEqual([]);
  });

  it('E17b(ship.seakeeping + 港內湧浪):讀入草稿區段、不改值寫回深相等、有浪不提醒;關閉 = 移除整段、其餘內容不動', () => {
    const text = read('E17b_typhoon_seakeeping.yaml');
    const e17b = fromYaml(text, 'E17b');
    expect(e17b.seakeeping).toEqual({ enabled: true });
    expect(seakeepingEnabled(e17b)).toBe(true);
    expect(parseYaml(toYaml(e17b))).toEqual(parseYaml(text));
    const v = validateDraft(e17b);
    expect(v.issues).toEqual([]);
    expect(v.warnings.some((w) => w.includes('ship.seakeeping'))).toBe(false);
    // 草稿區段是複本:改草稿不動 raw;輸出以草稿覆寫 raw 的同名鍵
    const off = setSeakeepingEnabled(e17b, false);
    expect(off.seakeeping).toBeNull();
    expect((off.raw['ship'] as RawSection)['seakeeping']).toEqual({ enabled: true });
    const expected = parseYaml(text) as RawSection;
    const { seakeeping: _sk, ...shipWithout } = expected['ship'] as RawSection;
    expect(parseYaml(toYaml(off))).toEqual({ ...expected, ship: shipWithout });
    // 改參數:只有該鍵改變
    const tuned = patchSeakeeping(e17b, 'heaveDampingRatio', 0.3);
    expect(parseYaml(toYaml(tuned))).toEqual({ ...expected, ship: { ...(expected['ship'] as RawSection), seakeeping: { enabled: true, heaveDampingRatio: 0.3 } } });
  });

  it('移除區段 → 輸出不含該鍵(raw 的鍵被覆寫);常數 set/drift 與流場模型各自獨立', () => {
    let d = fromYaml(FULL_YAML, 'ENV');
    d = setTideMode(d, 'none');
    d = setWavesEnabled(d, false);
    d = setCurrentTimeModel(d, 'none');
    d = setCurrentFieldEnabled(d, false);
    d = setShipInteractionEnabled(d, false);
    d = setRollEnabled(d, false);
    d = setSeakeepingEnabled(d, false);
    const o = toScenarioObject(d);
    const env = o['environment'] as RawSection;
    expect(env['tide']).toBeUndefined();
    expect(env['waves']).toBeUndefined();
    expect(env['current']).toEqual({ set: 85, drift: 2.5 });
    expect(env['seabed']).toBe('mud');
    // 有其他參數的區段關閉時保留並寫 enabled: false
    expect(env['shipInteraction']).toEqual({ enabled: false, coefficientScale: 1.0, eventThreshold_t: 3 });
    expect((o['ship'] as RawSection)['roll']).toEqual({ enabled: false, naturalPeriod_s: 10.5, dampingRatio: 0.08, windHeelGain: 1.0, turnHeelGain: 1.2, waveGain: 0.8 });
    expect((o['ship'] as RawSection)['seakeeping']).toEqual({ enabled: false, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 120 });
    expect(validateDraft(d).issues).toEqual([]);
  });
});

describe('潮汐 tide', () => {
  it('模式切換:table 建 2 點、harmonic 建 M2;表點新增(+1 h)/修改/刪除;t ↔ UTC 切換需 startTimeUtc', () => {
    let d = blankDraft();
    expect(tideMode(d)).toBe('none');
    d = setTideMode(d, 'table');
    expect(tidePoints(d)).toEqual([
      { t: 0, height_m: 0 },
      { t: 3600, height_m: 0 },
    ]);
    d = addTidePoint(d);
    d = patchTidePoint(d, 2, 'height_m', 1.5);
    d = patchTidePoint(d, 0, 'height_m', -0.2);
    expect(tidePoints(d)[2]).toEqual({ t: 7200, height_m: 1.5 });
    const env = toScenarioObject(d)['environment'] as RawSection;
    expect(env['tide']).toEqual({ table: [{ t: 0, height_m: -0.2 }, { t: 3600, height_m: 0 }, { t: 7200, height_m: 1.5 }] });
    expect(validateDraft(d).issues).toEqual([]);
    // UTC 寫法
    expect(() => setTideTimeKey(d, 'utc')).toThrow(/startTimeUtc/);
    d = { ...d, general: { ...d.general, startTimeUtc: '2026-07-07T06:00:00Z' } };
    d = setTideTimeKey(d, 'utc');
    expect(tidePoints(d).map((p) => p['utc'])).toEqual(['2026-07-07T06:00:00Z', '2026-07-07T07:00:00Z', '2026-07-07T08:00:00Z']);
    expect(tidePoints(d).every((p) => p['t'] === undefined)).toBe(true);
    expect(tablePointTimeKey(tidePoints(d))).toBe('utc');
    d = addTidePoint(d);
    expect(tidePoints(d)[3]!['utc']).toBe('2026-07-07T09:00:00Z');
    expect(validateDraft(d).issues).toEqual([]);
    d = setTideTimeKey(d, 't');
    expect(tidePoints(d).map((p) => p['t'])).toEqual([0, 3600, 7200, 10800]);
    d = removeTidePoint(d, 3);
    expect(tidePoints(d)).toHaveLength(3);
    expect(utcText(Date.UTC(2026, 6, 7, 6, 30, 0))).toBe('2026-07-07T06:30:00Z');
    // 調和潮
    d = setTideMode(d, 'harmonic');
    expect(d.tide).toEqual({ harmonic: { meanLevel_m: 0, constituents: [{ name: 'M2', amplitude_m: 1, period_s: 44714, phase_deg: 0 }] } });
    d = addTideConstituent(d);
    expect(tideConstituents(d)[1]).toMatchObject({ name: 'S2', period_s: 43200 });
    d = patchTideConstituent(d, 1, 'amplitude_m', 0.3);
    d = patchTide(d, 'harmonic.meanLevel_m', 0.5);
    d = removeTideConstituent(d, 0);
    expect((toScenarioObject(d)['environment'] as RawSection)['tide']).toEqual({ harmonic: { meanLevel_m: 0.5, constituents: [{ name: 'S2', amplitude_m: 0.3, period_s: 43200, phase_deg: 0 }] } });
    expect(validateDraft(d).issues).toEqual([]);
    expect(setTideMode(d, 'none').tide).toBeNull();
  });

  it('驗證:表點少於 2、t 不嚴格遞增、t 與 utc 並存、utc 無法解析/缺 startTimeUtc、height_m 非數;harmonic 無分潮、週期非正、振幅負', () => {
    let d = setTideMode(blankDraft(), 'table');
    d = patchTidePoint(d, 1, 't', 0);
    expect(validateDraft(d).issues).toContain('environment.tide.table[1] 潮高表的 t 必須嚴格遞增');
    d = removeTidePoint(d, 1);
    expect(validateDraft(d).issues).toContain('environment.tide.table 潮高表至少需要 2 點');
    d = { ...d, tide: { table: [{ t: 0, utc: '2026-07-07T06:00:00Z', height_m: 1 }, { utc: 'bad', height_m: 'x' }, { height_m: 1 }, { utc: '2026-07-07T08:00:00Z', height_m: 1 }] } };
    const v = validateEnvironmentExtras(d);
    expect(v.issues).toEqual([
      'environment.tide.table[0] 表點的 t 與 utc 只能擇一',
      'environment.tide.table[1] utc 無法解析:bad',
      'environment.tide.table[1] height_m 必須為有限數值',
      'environment.tide.table[2] 表點需要 t 或 utc',
      'environment.tide.table[3] 表點以 utc 指定時情境必須有可解析的 startTimeUtc',
    ]);
    // 有 startTimeUtc 時 utc 表點可排序
    const d2 = { ...d, general: { ...d.general, startTimeUtc: '2026-07-07T06:00:00Z' }, tide: { table: [{ utc: '2026-07-07T08:00:00Z', height_m: 1 }, { utc: '2026-07-07T07:00:00Z', height_m: 1 }] } };
    expect(validateEnvironmentExtras(d2).issues).toEqual(['environment.tide.table[1] 潮高表的 t 必須嚴格遞增']);
    const both = { ...d, tide: { table: [{ t: 0, height_m: 1 }, { t: 1, height_m: 1 }], harmonic: { constituents: [] } } };
    expect(validateEnvironmentExtras(both).issues).toEqual(['environment.tide tide 需為 table 或 harmonic 擇一', 'environment.tide.harmonic.constituents 調和潮至少需要 1 個分潮']);
    const harm = { ...d, tide: { harmonic: { meanLevel_m: 'x', constituents: [{ amplitude_m: -1, period_s: 0, phase_deg: 'p' }] } } };
    expect(validateEnvironmentExtras(harm).issues).toEqual([
      'environment.tide.harmonic meanLevel_m 必須為有限數值',
      'environment.tide.harmonic.constituents[0] period_s 必須為正',
      'environment.tide.harmonic.constituents[0] amplitude_m 不可為負',
      'environment.tide.harmonic.constituents[0] phase_deg 必須為有限數值',
    ]);
    // 參數引用略過數值規則
    const ref = { ...d, tide: { table: [{ t: 0, height_m: '${params:tide.h0}' }, { t: 60, height_m: 1 }] } };
    expect(validateEnvironmentExtras(ref).issues).toEqual([]);
  });
});

describe('浪 waves', () => {
  it('建立(來向取風向)/修改/關閉 → YAML;驗證 Hs < 0、Tp ≤ 0、來向超出、浪譜未知、γ < 1、缺必填', () => {
    let d = blankDraft();
    expect(d.waves).toBeNull();
    d = setWavesEnabled(d, true);
    expect(d.waves).toEqual({ significantHeight_m: 1, peakPeriod_s: 7, direction_deg: 45 });
    d = patchWaves(d, 'significantHeight_m', 2.5);
    d = patchWaves(d, 'spectrum', 'jonswap');
    d = patchWaves(d, 'gamma', 2);
    expect((toScenarioObject(d)['environment'] as RawSection)['waves']).toEqual({ significantHeight_m: 2.5, peakPeriod_s: 7, direction_deg: 45, spectrum: 'jonswap', gamma: 2 });
    expect(validateDraft(d).issues).toEqual([]);
    d = patchWaves(d, 'spectrum', '');
    expect(d.waves?.['spectrum']).toBeUndefined();
    const bad: ScenarioDraft = { ...d, waves: { significantHeight_m: -1, peakPeriod_s: 0, direction_deg: 361, spectrum: 'bretschneider', gamma: 0.5 } };
    expect(validateEnvironmentExtras(bad).issues).toEqual([
      'environment.waves significantHeight_m 必須為 ≥ 0 的有限數值',
      'environment.waves peakPeriod_s 必須為正',
      'environment.waves direction_deg 須在 0–360(來向)',
      'environment.waves spectrum 須為 pm | jonswap:bretschneider',
      'environment.waves gamma 必須 ≥ 1',
    ]);
    const missing: ScenarioDraft = { ...d, waves: { gamma: 3.3 } };
    expect(validateEnvironmentExtras(missing).issues).toHaveLength(3);
    expect(setWavesEnabled(d, false).waves).toBeNull();
    expect((toScenarioObject(setWavesEnabled(d, false))['environment'] as RawSection)['waves']).toBeUndefined();
  });
});

describe('流場模型 current.table / tidal / field', () => {
  it('流場表:建立(取常數流)/新增/修改/刪除/UTC 切換 → YAML;潮流:建立(退潮反向)與修改;常數 set/drift 不受影響', () => {
    let d = blankDraft();
    expect(currentTimeModel(d)).toBe('none');
    d = setCurrentTimeModel(d, 'table');
    expect(currentTablePoints(d)).toEqual([
      { t: 0, speed_kn: 0.5, direction_deg: 20 },
      { t: 3600, speed_kn: 0.5, direction_deg: 20 },
    ]);
    d = addCurrentTablePoint(d);
    d = patchCurrentTablePoint(d, 2, 'speed_kn', 1.5);
    d = patchCurrentTablePoint(d, 2, 'direction_deg', 200);
    d = removeCurrentTablePoint(d, 1);
    const cur = (toScenarioObject(d)['environment'] as RawSection)['current'] as RawSection;
    expect(cur).toEqual({ set: 20, drift: 0.5, table: [{ t: 0, speed_kn: 0.5, direction_deg: 20 }, { t: 7200, speed_kn: 1.5, direction_deg: 200 }] });
    expect(validateDraft(d).issues).toEqual([]);
    expect(validateDraft(d).summary.environment.current?.source).toBe('table');
    d = { ...d, general: { ...d.general, startTimeUtc: '2026-07-07T06:00:00Z' } };
    d = setCurrentTableTimeKey(d, 'utc');
    expect(currentTablePoints(d).map((p) => p['utc'])).toEqual(['2026-07-07T06:00:00Z', '2026-07-07T08:00:00Z']);
    expect(validateDraft(d).issues).toEqual([]);
    // 潮流
    d = setCurrentTimeModel(d, 'tidal');
    expect(d.currentModel['table']).toBeUndefined();
    expect(d.currentModel['tidal']).toEqual({ flood: { speed_kn: 0.5, direction_deg: 20 }, ebb: { speed_kn: 0.5, direction_deg: 200 }, slackWindow_min: 20 });
    expect(validateDraft(d).issues).toEqual(['environment.current.tidal tidal 需要 environment.tide']);
    d = setTideMode(d, 'table');
    d = patchCurrentModel(d, 'tidal.ebb.speed_kn', 3);
    d = patchCurrentModel(d, 'tidal.slackWindow_min', undefined);
    expect(validateDraft(d).issues).toEqual([]);
    expect(((toScenarioObject(d)['environment'] as RawSection)['current'] as RawSection)['tidal']).toEqual({ flood: { speed_kn: 0.5, direction_deg: 20 }, ebb: { speed_kn: 3, direction_deg: 200 } });
    expect(setCurrentTimeModel(d, 'none').currentModel).toEqual({});
  });

  it('流場表 / 潮流驗證:少於 2 點、t 不遞增、速度負、方向非數、tidal 缺 flood/ebb、slackWindow 負、table 與 tidal 並存', () => {
    const d = blankDraft();
    const table = { ...d, currentModel: { table: [{ t: 10, speed_kn: -1, direction_deg: 'n' }, { t: 5, speed_kn: 1, direction_deg: 0 }] } };
    expect(validateEnvironmentExtras(table).issues).toEqual([
      'environment.current.table[0] speed_kn 不可為負',
      'environment.current.table[0] direction_deg 必須為有限數值',
      'environment.current.table[1] 流場表的 t 必須嚴格遞增',
    ]);
    const one = { ...d, currentModel: { table: [{ t: 0, speed_kn: 1, direction_deg: 0 }] } };
    expect(validateEnvironmentExtras(one).issues).toEqual(['environment.current.table 流場表至少需要 2 點']);
    const tidal = { ...setTideMode(d, 'table'), currentModel: { table: [{ t: 0, speed_kn: 1, direction_deg: 0 }, { t: 1, speed_kn: 1, direction_deg: 0 }], tidal: { flood: { speed_kn: -2, direction_deg: 85 }, slackWindow_min: -1 } } };
    expect(validateEnvironmentExtras(tidal).issues).toEqual([
      'environment.current current 的 table 與 tidal 只能擇一',
      'environment.current.tidal.flood speed_kn 不可為負',
      'environment.current.tidal.ebb tidal 需要 ebb',
      'environment.current.tidal.slackWindow_min slackWindow_min 不可為負',
    ]);
  });

  it('空間網格:建立 3×3 零場、陣列文字往返、尺寸/數值/列舉/timeScale 檢查', () => {
    let d = blankDraft();
    d = setCurrentFieldEnabled(d, true);
    expect(currentField(d)).toEqual({ grid: { x0: -1000, y0: -1000, dx: 1000, dy: 1000, nx: 3, ny: 3 }, u_kn: [[0, 0, 0], [0, 0, 0], [0, 0, 0]], v_kn: [[0, 0, 0], [0, 0, 0], [0, 0, 0]] });
    expect(validateDraft(d).issues).toEqual([]);
    expect(validateDraft(d).summary.environment.current?.source).toBe('field');
    expect(fieldArrayText([[0, 0.5, 1], [0.2, 0.7, 1.2]])).toBe('0 0.5 1\n0.2 0.7 1.2');
    expect(parseFieldArrayText(' 0, 0.5 1 \n\n0.2\t0.7 1.2\n')).toEqual([[0, 0.5, 1], [0.2, 0.7, 1.2]]);
    expect(parseFieldArrayText('')).toEqual([]);
    expect(() => parseFieldArrayText('0 x 1')).toThrow(/第 1 行的「x」不是數值/);
    // 2 列 × 3 行的 u_kn 對 3×3 網格 → 列數錯誤;改 nx/ny 後正確
    d = setCurrentFieldArray(d, 'u_kn', parseFieldArrayText('0 0.5 1\n0.2 0.7 1.2'));
    expect(validateDraft(d).issues).toEqual(['environment.current.field u_kn 須有 ny = 3 列(得到 2)']);
    d = patchCurrentModel(d, 'field.grid.ny', 2);
    expect(validateDraft(d).issues).toEqual(['environment.current.field v_kn 須有 ny = 2 列(得到 3)']);
    d = setCurrentFieldArray(d, 'v_kn', parseFieldArrayText('1 0.8 0.6\n0.9 0.7'));
    expect(validateDraft(d).issues).toEqual(['environment.current.field.v_kn[1] v_kn 每列須有 nx = 3 個值(得到 2)']);
    d = setCurrentFieldArray(d, 'v_kn', parseFieldArrayText('1 0.8 0.6\n0.9 0.7 0.5'));
    expect(validateDraft(d).issues).toEqual([]);
    // 列舉與 timeScale 對應模型
    d = patchCurrentModel(d, 'field.outside', 'zero');
    d = patchCurrentModel(d, 'field.timeScale', 'tidal');
    expect(validateDraft(d).issues).toEqual(['environment.current.field timeScale tidal 需要 current.tidal']);
    d = setTideMode(d, 'table');
    d = setCurrentTimeModel(d, 'tidal');
    expect(validateDraft(d).issues).toEqual([]);
    expect(validateDraft(d).summary.environment.current?.source).toBe('field+tidal');
    expect(currentFieldIssues({ grid: { x0: 'a', y0: 0, dx: 0, dy: -1, nx: 2.5, ny: 1 }, u_kn: 'x', outside: 'wrap', timeScale: 'daily' })).toEqual([
      'environment.current.field.grid x0/y0 必須為有限數值',
      'environment.current.field.grid dx 必須為正',
      'environment.current.field.grid dy 必須為正',
      'environment.current.field.grid nx 必須為 ≥ 2 的整數',
      'environment.current.field.grid ny 必須為 ≥ 2 的整數',
      'environment.current.field field 需要 u_kn',
      'environment.current.field field 需要 v_kn',
      'environment.current.field outside 須為 edge | zero:wrap',
      'environment.current.field timeScale 須為 none | tidal | table:daily',
    ]);
    expect(currentFieldIssues({ u_kn: [[0, 0], [0, 0]], v_kn: [[0, 'x'], [0, 0]] })).toEqual(['environment.current.field field 需要 grid']);
    expect(currentFieldIssues({ grid: { x0: 0, y0: 0, dx: 1, dy: 1, nx: 2, ny: 2 }, u_kn: [[0, 0], [0, 0]], v_kn: [[0, 'x'], [0, 0]] })).toEqual(['environment.current.field.v_kn[0][1] v_kn 的值必須為有限數值']);
    expect(setCurrentFieldEnabled(d, false).currentModel['field']).toBeUndefined();
  });
});

describe('船間交互作用與橫搖', () => {
  it('shipInteraction:啟用 → {enabled: true};加參數後關閉 → enabled: false;只有 enabled 時關閉 → 移除;驗證負值與 enabled 型別', () => {
    let d = blankDraft();
    d = setShipInteractionEnabled(d, true);
    expect(d.shipInteraction).toEqual({ enabled: true });
    expect(setShipInteractionEnabled(d, false).shipInteraction).toBeNull();
    d = patchShipInteraction(d, 'coefficientScale', 0.5);
    expect((toScenarioObject(d)['environment'] as RawSection)['shipInteraction']).toEqual({ enabled: true, coefficientScale: 0.5 });
    expect(setShipInteractionEnabled(d, false).shipInteraction).toEqual({ enabled: false, coefficientScale: 0.5 });
    expect(validateDraft(d).issues).toEqual([]);
    const bad = { ...d, shipInteraction: { enabled: 'yes', coefficientScale: -1, eventThreshold_t: 'x' } };
    expect(validateEnvironmentExtras(bad).issues).toEqual(['environment.shipInteraction enabled 須為布林值', 'environment.shipInteraction coefficientScale 必須為 ≥ 0 的有限數值', 'environment.shipInteraction eventThreshold_t 必須為 ≥ 0 的有限數值']);
    expect(patchShipInteraction(setShipInteractionEnabled(blankDraft(), true), 'enabled', undefined).shipInteraction).toBeNull();
  });

  it('ship.roll(橫搖契約):啟用/參數/關閉 → ship 區段;Weiss 估計 T = 0.8·B/√GM;驗證週期非正、阻尼比超出 (0, 1)、增益負、未給週期提醒', () => {
    let d = blankDraft();
    expect(d.roll).toBeNull();
    d = setRollEnabled(d, true);
    expect(d.roll).toEqual({ enabled: true });
    let v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.warnings.some((w) => w.startsWith('ship.roll 未給 naturalPeriod_s'))).toBe(true);
    // FSB1 壓載(空白範本):B 18 m、GM 4.15 m → 0.8·18/√4.15 ≈ 7.1 s;滿載 GM 1.96 → 10.3 s;無 GM 資料(FSB2)用預設 1.2 m
    expect(rollPeriodEstimate(d)).toEqual({ period_s: 7.1, breadth_m: 18, gm_m: 4.15, gmFromParticulars: true });
    expect(rollPeriodEstimate({ ...d, ship: { id: 'FSB1', loading: 'full' } })).toEqual({ period_s: 10.3, breadth_m: 18, gm_m: 1.96, gmFromParticulars: true });
    expect(rollPeriodEstimate({ ...d, ship: { id: 'FSB2', loading: 'full' } }).gmFromParticulars).toBe(false);
    expect(rollPeriodEstimate({ ...d, ship: { id: 'FSB2', loading: 'full' } }).gm_m).toBe(1.2);
    expect(estimateRollPeriod(18, 1.96)).toBeCloseTo(10.286, 3);
    expect(estimateRollPeriod(18, 0)).toBe(0);
    d = patchRoll(d, 'naturalPeriod_s', 10.5);
    d = patchRoll(d, 'dampingRatio', 0.1);
    d = patchRoll(d, 'waveGain', 0.5);
    const ship = toScenarioObject(d)['ship'] as RawSection;
    expect(ship).toEqual({ id: 'FSB1', loading: 'ballast', roll: { enabled: true, naturalPeriod_s: 10.5, dampingRatio: 0.1, waveGain: 0.5 } });
    v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.warnings.some((w) => w.includes('ship.roll'))).toBe(false);
    expect(parseScenario(toYaml(d), 'x').issues).toEqual([]);
    // 關閉:有其他參數 → enabled: false 保留參數;只有 enabled → 移除
    expect(setRollEnabled(d, false).roll).toEqual({ enabled: false, naturalPeriod_s: 10.5, dampingRatio: 0.1, waveGain: 0.5 });
    expect(setRollEnabled(setRollEnabled(blankDraft(), true), false).roll).toBeNull();
    expect((toScenarioObject(setRollEnabled(setRollEnabled(blankDraft(), true), false))['ship'] as RawSection)['roll']).toBeUndefined();
    const bad = { ...d, roll: { enabled: 1, naturalPeriod_s: 0, dampingRatio: -0.1, windHeelGain: 'x', turnHeelGain: -1, waveGain: 1 } };
    expect(validateEnvironmentExtras(bad).issues).toEqual([
      'ship.roll enabled 須為布林值',
      'ship.roll naturalPeriod_s 必須為正',
      'ship.roll dampingRatio 須在 0–1 之間(不含)',
      'ship.roll windHeelGain 必須為 ≥ 0 的有限數值',
      'ship.roll turnHeelGain 必須為 ≥ 0 的有限數值',
    ]);
    // 阻尼比與核心相同為開區間 (0, 1):0 與 1 皆為錯誤、0.99 通過;參數引用略過
    expect(validateEnvironmentExtras({ ...d, roll: { enabled: true, naturalPeriod_s: 9, dampingRatio: 1 } }).issues).toEqual(['ship.roll dampingRatio 須在 0–1 之間(不含)']);
    expect(validateEnvironmentExtras({ ...d, roll: { enabled: true, naturalPeriod_s: 9, dampingRatio: 0 } }).issues).toEqual(['ship.roll dampingRatio 須在 0–1 之間(不含)']);
    expect(validateEnvironmentExtras({ ...d, roll: { enabled: true, naturalPeriod_s: 9, dampingRatio: 0.99 } }).issues).toEqual([]);
    expect(validateEnvironmentExtras({ ...d, roll: { enabled: true, naturalPeriod_s: '${params:ship.rollPeriod_s}', dampingRatio: '${params:ship.rollDamping}' } }).issues).toEqual([]);
  });
});

describe('升沉/縱搖 ship.seakeeping(浪一階運動契約)', () => {
  const WAVES = { significantHeight_m: 2.5, peakPeriod_s: 8, direction_deg: 45 };
  const shipOf = (d: ScenarioDraft) => toScenarioObject(d)['ship'] as RawSection;
  const skWarned = (d: ScenarioDraft) => validateEnvironmentExtras(d).warnings.includes(SEAKEEPING_NO_WAVES_WARNING);
  /** 加一個時間表項目,動作為 setEnvironment(args)。 */
  const withSetEnvironment = (d: ScenarioDraft, args: unknown): ScenarioDraft => ({ ...d, timeline: [...d.timeline, { id: 'swell', when: { type: 'time', atLeast: 60 }, actions: [{ setEnvironment: args }] }] });

  it('啟用 / 改參數 / 清空參數 / 關閉 → ship 區段與 YAML;欄位以 schema 為準', () => {
    // schema ship.seakeeping 的欄位:enabled + 4 個選填參數(表單欄位與 schema 一致,不自創欄位)
    expect(SEAKEEPING_FIELDS.map((f) => f[0])).toEqual(['heaveDampingRatio', 'pitchDampingRatio', 'pitchGyradiusRatio', 'gmL_m']);
    let d = blankDraft();
    expect(d.seakeeping).toBeNull();
    expect(seakeepingEnabled(d)).toBe(false);
    expect(shipOf(d)).toEqual({ id: 'FSB1', loading: 'ballast' });
    // 啟用:只有 enabled: true(其餘用核心預設)
    d = setSeakeepingEnabled(d, true);
    expect(d.seakeeping).toEqual({ enabled: true });
    expect(seakeepingEnabled(d)).toBe(true);
    expect(shipOf(d)).toEqual({ id: 'FSB1', loading: 'ballast', seakeeping: { enabled: true } });
    expect(validateDraft(d).issues).toEqual([]);
    // 改參數
    d = patchSeakeeping(d, 'heaveDampingRatio', 0.3);
    d = patchSeakeeping(d, 'pitchDampingRatio', 0.4);
    d = patchSeakeeping(d, 'pitchGyradiusRatio', 0.26);
    d = patchSeakeeping(d, 'gmL_m', 150);
    expect(shipOf(d)).toEqual({ id: 'FSB1', loading: 'ballast', seakeeping: { enabled: true, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 150 } });
    expect(validateDraft(d).issues).toEqual([]);
    expect(parseScenario(toYaml(d), 'x').issues).toEqual([]);
    expect((parseYaml(toYaml(d)) as RawSection)['ship']).toEqual({ id: 'FSB1', loading: 'ballast', seakeeping: { enabled: true, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 150 } });
    // 清空單一參數(表單留白 = undefined)→ 刪鍵(用核心預設),其他鍵不動
    d = patchSeakeeping(d, 'gmL_m', undefined);
    d = patchSeakeeping(d, 'pitchDampingRatio', undefined);
    expect(d.seakeeping).toEqual({ enabled: true, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    // 關閉:有其他參數 → enabled: false 保留參數;再啟用 → 參數仍在
    const off = setSeakeepingEnabled(d, false);
    expect(off.seakeeping).toEqual({ enabled: false, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    expect(seakeepingEnabled(off)).toBe(false);
    expect(shipOf(off)['seakeeping']).toEqual({ enabled: false, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    expect(setSeakeepingEnabled(off, true).seakeeping).toEqual({ enabled: true, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    // 關閉:只有 enabled → 移除整段(輸出不含 seakeeping 鍵)
    const bare = setSeakeepingEnabled(setSeakeepingEnabled(blankDraft(), true), false);
    expect(bare.seakeeping).toBeNull();
    expect(shipOf(bare)).toEqual({ id: 'FSB1', loading: 'ballast' });
    expect(toYaml(bare)).not.toContain('seakeeping');
    // 清到空物件 → 區段移除;未啟用時先填參數 → 有區段但沒有 enabled(= 關閉),勾選後保留參數
    expect(patchSeakeeping(setSeakeepingEnabled(blankDraft(), true), 'enabled', undefined).seakeeping).toBeNull();
    const paramOnly = patchSeakeeping({ ...blankDraft(), seakeeping: { enabled: false, gmL_m: 150 } }, 'enabled', undefined);
    expect(paramOnly.seakeeping).toEqual({ gmL_m: 150 });
    expect(seakeepingEnabled(paramOnly)).toBe(false);
    expect(setSeakeepingEnabled(paramOnly, true).seakeeping).toEqual({ gmL_m: 150, enabled: true });
    // 未列在表單的鍵(日後 schema 新增)原樣保留
    const extra = fromYaml('id: X\nship: { id: FSB2, loading: full, seakeeping: { enabled: true, futureKey: 7 } }\ninitial: { position: { lat: 23, lon: 120 }, heading: 0 }\nenvironment: { waterDepth: 30 }\n', 'X');
    expect(shipOf(patchSeakeeping(extra, 'heaveDampingRatio', 0.2))['seakeeping']).toEqual({ enabled: true, futureKey: 7, heaveDampingRatio: 0.2 });
    // 草稿不改 raw:raw 只是範本複本,輸出以草稿區段覆寫
    expect((setSeakeepingEnabled(extra, false).raw['ship'] as RawSection)['seakeeping']).toEqual({ enabled: true, futureKey: 7 });
  });

  it('驗證邊界(與核心 Scenario.StructuralIssues 同規則同訊息;路徑 ship.seakeeping.<欄位>;enabled 與否都檢查)', () => {
    const base = { ...blankDraft(), waves: WAVES };
    const issuesOf = (sk: RawSection) => validateEnvironmentExtras({ ...base, seakeeping: sk }).issues;
    const damping = (key: string) => `ship.seakeeping.${key} ${key} 須在 0–1 之間(不含)`;
    const ratioMsg = 'ship.seakeeping.pitchGyradiusRatio pitchGyradiusRatio 須大於 0 且不超過 0.5';
    const gmMsg = 'ship.seakeeping.gmL_m gmL_m 必須為正';
    // 全部不合法(依欄位順序列出)
    expect(issuesOf({ enabled: 'yes', heaveDampingRatio: 0, pitchDampingRatio: 1, pitchGyradiusRatio: 0, gmL_m: 0 })).toEqual([
      'ship.seakeeping.enabled enabled 須為布林值',
      damping('heaveDampingRatio'),
      damping('pitchDampingRatio'),
      ratioMsg,
      gmMsg,
    ]);
    // 阻尼比:開區間 (0, 1)
    for (const key of ['heaveDampingRatio', 'pitchDampingRatio']) {
      for (const bad of [0, 1, -0.1, 1.5, 'x', true, null]) expect(issuesOf({ enabled: true, [key]: bad }), `${key}=${String(bad)}`).toEqual([damping(key)]);
      for (const ok of [0.01, 0.35, 0.99, '${params:ship.damping}']) expect(issuesOf({ enabled: true, [key]: ok }), `${key}=${String(ok)}`).toEqual([]);
    }
    // k_yy/L:(0, 0.5](0.5 含)
    for (const bad of [0, -0.25, 0.51, 1, 'x']) expect(issuesOf({ enabled: true, pitchGyradiusRatio: bad }), String(bad)).toEqual([ratioMsg]);
    for (const ok of [0.01, 0.25, 0.5, '${params:ship.kyy}']) expect(issuesOf({ enabled: true, pitchGyradiusRatio: ok }), String(ok)).toEqual([]);
    // GM_L:> 0
    for (const bad of [0, -1, 'x']) expect(issuesOf({ enabled: true, gmL_m: bad }), String(bad)).toEqual([gmMsg]);
    for (const ok of [0.1, 104.7, 500, '${params:ship.gmL}']) expect(issuesOf({ enabled: true, gmL_m: ok }), String(ok)).toEqual([]);
    // 未啟用(enabled: false 或未給 enabled)也檢查參數;全部留白沒有問題
    expect(issuesOf({ enabled: false, heaveDampingRatio: 2 })).toEqual([damping('heaveDampingRatio')]);
    expect(issuesOf({ gmL_m: -5 })).toEqual([gmMsg]);
    expect(issuesOf({ enabled: true })).toEqual([]);
    expect(issuesOf({ enabled: false })).toEqual([]);
    // validateDraft 併入同一批問題(阻止載入)
    expect(validateDraft({ ...base, seakeeping: { enabled: true, pitchGyradiusRatio: 0.6 } }).issues).toEqual([ratioMsg]);
    // 預設值在合法範圍內(表單 placeholder 顯示的預設)
    expect(issuesOf({ enabled: true, heaveDampingRatio: SEAKEEPING_DEFAULTS.heaveDampingRatio, pitchDampingRatio: SEAKEEPING_DEFAULTS.pitchDampingRatio, pitchGyradiusRatio: SEAKEEPING_DEFAULTS.pitchGyradiusRatio })).toEqual([]);
  });

  it('啟用但情境沒有浪 → 提醒(不是錯誤);environment.waves 的 Hs > 0 或時間表 setEnvironment waves 給浪即不提醒', () => {
    const on = setSeakeepingEnabled(blankDraft(), true);
    expect(scenarioHasWaves(on)).toBe(false);
    const v = validateDraft(on);
    expect(v.issues).toEqual([]);
    expect(v.warnings).toContain(SEAKEEPING_NO_WAVES_WARNING);
    expect(SEAKEEPING_NO_WAVES_WARNING.startsWith('ship.seakeeping 已啟用但情境沒有浪')).toBe(true);
    // 未啟用(無區段、enabled: false)不提醒
    expect(skWarned(blankDraft())).toBe(false);
    expect(skWarned({ ...on, seakeeping: { enabled: false, gmL_m: 150 } })).toBe(false);
    // environment.waves:Hs > 0 或參數引用 → 有浪;Hs = 0 = 關閉 → 提醒
    expect(skWarned({ ...on, waves: WAVES })).toBe(false);
    expect(skWarned({ ...on, waves: { ...WAVES, significantHeight_m: '${params:env.hs}' } })).toBe(false);
    expect(skWarned({ ...on, waves: { ...WAVES, significantHeight_m: 0 } })).toBe(true);
    // Hs 缺漏/負值已有錯誤訊息,不再重複提醒
    const negative = validateEnvironmentExtras({ ...on, waves: { ...WAVES, significantHeight_m: -1 } });
    expect(negative.issues).toEqual(['environment.waves significantHeight_m 必須為 ≥ 0 的有限數值']);
    expect(negative.warnings).not.toContain(SEAKEEPING_NO_WAVES_WARNING);
    // 時間表 setEnvironment waves:Hs > 0(或參數引用)才算給浪;null / Hs 0 / 只改來向的部分更新都不算
    expect(skWarned(withSetEnvironment(on, { waves: { significantHeight_m: 0.8, peakPeriod_s: 10, direction_deg: 225 }, ramp_s: 600 }))).toBe(false);
    expect(skWarned(withSetEnvironment(on, { waves: { significantHeight_m: '${params:env.hs}', peakPeriod_s: 10, direction_deg: 225 } }))).toBe(false);
    expect(skWarned(withSetEnvironment(on, { waves: null }))).toBe(true);
    expect(skWarned(withSetEnvironment(on, { waves: { significantHeight_m: 0 } }))).toBe(true);
    expect(skWarned(withSetEnvironment(on, { waves: { direction_deg: 180 } }))).toBe(true);
    expect(skWarned(withSetEnvironment(on, { wind: { trueSpeed: 30 } }))).toBe(true);
    // 起始 Hs = 0、由時間表加浪 → 不提醒
    expect(skWarned(withSetEnvironment({ ...on, waves: { ...WAVES, significantHeight_m: 0 } }, { waves: { significantHeight_m: 1.2 } }))).toBe(false);
    // E17b:拿掉 environment.waves 後時間表仍有 setEnvironment waves(Hs 0.8 / 1.2)→ 不提醒;再把時間表清空 → 提醒
    const e17b = fromYaml(read('E17b_typhoon_seakeeping.yaml'), 'E17b');
    const noStatic = setWavesEnabled(e17b, false);
    expect(noStatic.waves).toBeNull();
    expect(scenarioHasWaves(noStatic)).toBe(true);
    expect(skWarned(noStatic)).toBe(false);
    expect(skWarned({ ...noStatic, timeline: [] })).toBe(true);
    // 提醒不阻止載入:issues 為空
    expect(validateEnvironmentExtras({ ...noStatic, timeline: [] }).issues).toEqual([]);
  });

  it('自然週期估計與核心 SeakeepingModel 同式(physics-waves.md 第 11.4 節的 FSB 量級表);草稿的 gmL_m / k_yy/L 代入', () => {
    // 表列:船/裝載 → C_wp、∇、T_h、GM_L、T_p
    const table: Array<[ship: 'FSB1' | 'FSB2', loading: 'full' | 'ballast', cwp: number, volume: number, th: number, gmL: number, tp: number, estimated: boolean]> = [
      ['FSB1', 'full', 0.825, 7536, 6.42, 119.0, 4.99, false],
      ['FSB1', 'ballast', 0.8, 4364, 4.96, 180.7, 4.05, false],
      ['FSB2', 'full', 0.843, 11486, 7.26, 104.7, 5.58, false],
      ['FSB2', 'ballast', 0.826, 8369, 6.26, 137.4, 4.88, true], // FSB2 壓載 C_b、排水量無資料 → 估計
    ];
    for (const [ship, loading, cwp, volume, th, gmL, tp, estimated] of table) {
      const h = seakeepingHydrostatics(ship, loading);
      const label = `${ship} ${loading}`;
      expect((1 + 2 * h.cb) / 3, label).toBeCloseTo(cwp, 3);
      expect(Math.round(h.volume_m3), label).toBe(volume);
      expect(h.estimated, label).toBe(estimated);
      expect(estimateHeavePeriod(h), label).toBeCloseTo(th, 2);
      expect(estimateGmL(h), label).toBeCloseTo(gmL, 1);
      expect(estimatePitchPeriod(h.lpp_m, estimateGmL(h)), label).toBeCloseTo(tp, 2);
      const est = seakeepingPeriodEstimate({ ...blankDraft(), ship: { id: ship, loading } });
      // 表單顯示值:由未進位的估計值取 0.1(不是把表列的 0.01 值再進位一次)
      const round1 = (x: number) => Math.round(x * 10) / 10;
      expect([est.heavePeriod_s, est.pitchPeriod_s, est.gmL_m, est.gmLEstimate_m], label).toEqual([round1(estimateHeavePeriod(h)), round1(estimatePitchPeriod(h.lpp_m, estimateGmL(h))), gmL, gmL]);
      expect(Math.abs(est.heavePeriod_s - th), label).toBeLessThanOrEqual(0.05 + 1e-9);
      expect(Math.abs(est.pitchPeriod_s - tp), label).toBeLessThanOrEqual(0.05 + 1e-9);
      expect(est.gmLFromScenario, label).toBe(false);
      expect(est.pitchGyradiusRatio, label).toBe(0.25);
    }
    // 核心測試 NaturalPeriodsAndAssumedDefaultsFollowParticulars 的同一組式子(FSB2 滿載:L 104、C_b 0.765、T 7.2)
    const fsb2 = seakeepingHydrostatics('FSB2', 'full');
    expect(fsb2).toMatchObject({ lpp_m: 104, breadth_m: 20, draft_m: 7.2, cb: 0.765 });
    const cwp = (1 + 2 * 0.765) / 3;
    expect(estimateGmL(fsb2)).toBeCloseTo((3 * cwp * cwp * 104 * 104) / (40 * 0.765 * 7.2), 12);
    expect((2 * Math.PI) / estimatePitchPeriod(104, estimateGmL(fsb2))).toBeCloseTo(Math.sqrt((9.80665 * estimateGmL(fsb2)) / (1.2 * 26 * 26)), 12);
    expect((2 * Math.PI) / estimateHeavePeriod(fsb2)).toBeCloseTo(Math.sqrt((9.80665 * cwp * 104 * 20) / (2 * (11773.2 * 1000) / 1025)), 12);
    // FSB2 壓載:C_b = 0.765 − 0.11·(1 − 5.45/7.2);排水量 = C_b·L·B·T·ρ
    const fsb2b = seakeepingHydrostatics('FSB2', 'ballast');
    expect(fsb2b.cb).toBeCloseTo(0.765 - 0.11 * (1 - 5.45 / 7.2), 12);
    expect(fsb2b.volume_m3).toBeCloseTo(fsb2b.cb * 104 * 20 * 5.45, 6);
    // intermediate(particulars 無資料):full 與 ballast 各分量平均(核心 GetLoading);FSB2 的 C_b 與排水量只有 full 有值 → 取 full
    const fsb1i = seakeepingHydrostatics('FSB1', 'intermediate');
    expect(fsb1i).toMatchObject({ draft_m: (5.701 + 3.72) / 2, cb: (0.738 + 0.7) / 2, estimated: true });
    expect(fsb1i.volume_m3).toBeCloseTo((((7724.77 + 4472.8) / 2) * 1000) / 1025, 6);
    const fsb2i = seakeepingHydrostatics('FSB2', 'intermediate');
    expect(fsb2i).toMatchObject({ draft_m: (7.2 + 5.45) / 2, cb: 0.765, estimated: true });
    expect(fsb2i.volume_m3).toBeCloseTo((11773.2 * 1000) / 1025, 6);
    // 草稿的 gmL_m 與 pitchGyradiusRatio 代入縱搖週期(T_p ∝ k_yy/√GM_L);升沉週期不受影響;阻尼比不影響週期
    const d = { ...blankDraft(), ship: { id: 'FSB2' as const, loading: 'full' as const } };
    const given = seakeepingPeriodEstimate({ ...d, seakeeping: { enabled: true, gmL_m: 95, pitchGyradiusRatio: 0.22, heaveDampingRatio: 0.2, pitchDampingRatio: 0.5 } });
    expect(given).toMatchObject({ heavePeriod_s: 7.3, gmL_m: 95, gmLEstimate_m: 104.7, gmLFromScenario: true, pitchGyradiusRatio: 0.22 });
    expect(given.pitchPeriod_s).toBe(Math.round(estimatePitchPeriod(104, 95, 0.22) * 10) / 10);
    expect(estimatePitchPeriod(104, 95, 0.22)).toBeCloseTo(estimatePitchPeriod(104, estimateGmL(fsb2)) * (0.22 / 0.25) * Math.sqrt(estimateGmL(fsb2) / 95), 12);
    // 參數引用或不合法值 → 改用估計/預設(錯誤另由驗證報)
    for (const sk of [{ gmL_m: '${params:ship.gmL}', pitchGyradiusRatio: '${params:ship.kyy}' }, { gmL_m: -3, pitchGyradiusRatio: 0.8 }, { gmL_m: 0, pitchGyradiusRatio: 0 }]) {
      expect(seakeepingPeriodEstimate({ ...d, seakeeping: { enabled: true, ...sk } })).toMatchObject({ pitchPeriod_s: 5.6, gmL_m: 104.7, gmLFromScenario: false, pitchGyradiusRatio: 0.25 });
    }
    // 非正輸入回 0(不丟例外、不出現 NaN)
    expect(estimatePitchPeriod(104, 0)).toBe(0);
    expect(estimatePitchPeriod(0, 100)).toBe(0);
    expect(estimateHeavePeriod({ ...fsb2, volume_m3: 0 })).toBe(0);
    expect(estimateGmL({ ...fsb2, draft_m: 0 })).toBe(0);
  });
});

describe('草稿版本', () => {
  it('版本 2 草稿升級為 4:已編輯欄位沿用、環境進階區段(含 seakeeping)由 raw 重建;版本 4 原樣;版本 5 或缺區段回 null', () => {
    const v4 = fromYaml(FULL_YAML, 'ENV');
    const v2 = { ...v4, version: 2, general: { ...v4.general, name: '舊草稿' } } as unknown as RawSection;
    for (const k of ['tide', 'waves', 'currentModel', 'shipInteraction', 'roll', 'seakeeping']) delete v2[k];
    const storage = new MemoryStorage();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v2));
    const back = loadDraft(storage)!;
    expect(back.version).toBe(4);
    expect(back.general.name).toBe('舊草稿');
    expect(back.tide).toEqual(v4.tide);
    expect(back.waves).toEqual(v4.waves);
    expect(back.currentModel).toEqual(v4.currentModel);
    expect(back.shipInteraction).toEqual(v4.shipInteraction);
    expect(back.roll).toEqual(v4.roll);
    expect(back.seakeeping).toEqual(v4.seakeeping);
    expect(parseYaml(toYaml(back))).toEqual({ ...parseYaml(FULL_YAML), name: '舊草稿' });
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v4));
    expect(loadDraft(storage)).toEqual(JSON.parse(JSON.stringify(v4)));
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v4, version: 5 }));
    expect(loadDraft(storage)).toBeNull();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v4, currentModel: undefined }));
    expect(loadDraft(storage)).toBeNull();
    // 版本 4 但缺 seakeeping 欄位(格式不符)→ null
    const { seakeeping: _sk, ...noSeakeeping } = v4;
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(noSeakeeping));
    expect(loadDraft(storage)).toBeNull();
  });

  it('版本 3 草稿(第二十至二十八輪)升級為 4:seakeeping 由範本 raw 重建、其他欄位(含已編輯的環境進階區段)原樣不動', () => {
    // 版本 3 的草稿沒有 seakeeping 欄位;範本的 ship.seakeeping 只在 raw(當時輸出靠 raw 原樣帶出)
    const toV3 = (d: ScenarioDraft): RawSection => {
      const { seakeeping: _sk, ...rest } = d;
      return { ...rest, version: 3 };
    };
    const storage = new MemoryStorage();
    // (1) 範本有 seakeeping(E17b),使用者在版本 3 編輯過名稱、浪、橫搖、時間表以外的區段
    const text = read('E17b_typhoon_seakeeping.yaml');
    let edited = fromYaml(text, 'E17b');
    edited = { ...edited, general: { ...edited.general, name: '舊草稿 E17b' } };
    edited = patchWaves(edited, 'significantHeight_m', 0.7);
    edited = patchRoll(setRollEnabled(edited, true), 'dampingRatio', 0.1);
    const stored = toV3(edited);
    expect('seakeeping' in stored).toBe(false);
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(stored));
    const back = loadDraft(storage)!;
    expect(back.version).toBe(4);
    expect(back.seakeeping).toEqual({ enabled: true });
    // 其他欄位逐一與舊草稿相同(只多了 seakeeping、version 改 4)
    const { seakeeping: _a, version: _b, ...backRest } = back;
    const { version: _c, ...storedRest } = JSON.parse(JSON.stringify(stored)) as RawSection;
    expect(backRest).toEqual(storedRest);
    // 輸出內容不遺失:與版本 3 當時的輸出相同(seakeeping 仍在、編輯過的欄位沿用)
    const expected = parseYaml(text) as RawSection;
    const env = expected['environment'] as RawSection;
    expect(parseYaml(toYaml(back))).toEqual({
      ...expected,
      name: '舊草稿 E17b',
      ship: { ...(expected['ship'] as RawSection), roll: { enabled: true, dampingRatio: 0.1 } },
      environment: { ...env, waves: { ...(env['waves'] as RawSection), significantHeight_m: 0.7 } },
    });
    expect(validateDraft(back).issues).toEqual([]);
    // 升級後即可在表單編輯:關閉 → 輸出不含 seakeeping
    expect((toScenarioObject(setSeakeepingEnabled(back, false))['ship'] as RawSection)['seakeeping']).toBeUndefined();
    // 升級後再存 → 版本 4,往返相同
    expect(saveDraft(back, storage)).toBe(true);
    expect(loadDraft(storage)).toEqual(JSON.parse(JSON.stringify(back)));

    // (2) 範本的 seakeeping 帶參數且未啟用 → 原樣重建
    const full = fromYaml(FULL_YAML, 'ENV');
    const tuned = { ...full, raw: { ...full.raw, ship: { ...(full.raw['ship'] as RawSection), seakeeping: { enabled: false, gmL_m: 130 } } } };
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(toV3(tuned)));
    expect(loadDraft(storage)!.seakeeping).toEqual({ enabled: false, gmL_m: 130 });

    // (3) 範本沒有 seakeeping → null,輸出與原檔深相等
    const e18b = fromYaml(read('E18b_tide_table.yaml'), 'E18b');
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(toV3(e18b)));
    const plain = loadDraft(storage)!;
    expect(plain.version).toBe(4);
    expect(plain.seakeeping).toBeNull();
    expect(parseYaml(toYaml(plain))).toEqual(parseYaml(read('E18b_tide_table.yaml')));

    // (4) 版本 3 但缺環境進階區段(格式不符)→ null
    const { roll: _r, ...broken } = toV3(e18b);
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(broken));
    expect(loadDraft(storage)).toBeNull();
  });

  it('版本 1 草稿(第十七輪)升級為 4:範本的 ship.seakeeping 與 ship.mmsi(當時都只在 raw)不遺失', () => {
    const text = 'id: OLD\nship: { id: FSB2, loading: full, mmsi: 416000002, seakeeping: { enabled: true, gmL_m: 110 } }\ninitial: { position: { lat: 23, lon: 120 }, heading: 0 }\nenvironment: { waterDepth: 30, waves: { significantHeight_m: 1.5, peakPeriod_s: 8, direction_deg: 45 } }\nassessment: { hardFail: [{ id: grounding, rule: grounding }] }\n';
    const now = fromYaml(text, 'OLD');
    // 版本 1 草稿只有:general / ship(無 mmsi)/ initial / environment / targets / timeline / assessment{hardFail, bands, start, passMark} / raw
    const { mmsi: _m, ...oldShip } = now.ship;
    const v1 = { version: 1, template: 'OLD', general: { ...now.general, name: '舊草稿' }, ship: oldShip, initial: now.initial, environment: now.environment, targets: now.targets, timeline: now.timeline, assessment: { hardFail: now.assessment.hardFail, bands: now.assessment.bands }, raw: now.raw };
    const storage = new MemoryStorage();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v1));
    const back = loadDraft(storage)!;
    expect(back.version).toBe(4);
    expect(back.seakeeping).toEqual({ enabled: true, gmL_m: 110 });
    expect(back.ship).toEqual({ id: 'FSB2', loading: 'full', mmsi: 416000002 });
    expect(back.waves).toEqual({ significantHeight_m: 1.5, peakPeriod_s: 8, direction_deg: 45 });
    expect(parseYaml(toYaml(back))).toEqual({ ...(parseYaml(text) as RawSection), name: '舊草稿' });
  });

  it('data/scenarios 全部情境:版本 3 草稿升級後輸出與原檔深相等(既有草稿不遺失內容)', () => {
    const storage = new MemoryStorage();
    const files = readdirSync(scenarioDir).filter((f) => /\.ya?ml$/i.test(f));
    expect(files.length).toBeGreaterThan(20);
    expect(files).toContain('E17b_typhoon_seakeeping.yaml');
    for (const f of files) {
      const { seakeeping: _sk, ...v3 } = fromYaml(read(f), f);
      storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v3, version: 3 }));
      const back = loadDraft(storage);
      expect(back?.version, f).toBe(4);
      expect(parseYaml(toYaml(back!)), f).toEqual(parseYaml(read(f)));
    }
  });
});
