// 情境編輯器環境進階區段(lib/scenario-editor.ts 第二十輪;設計文件 docs/design/scenario-engine.md 第 6.1 節;R3/R4):
// environment.tide(潮高表 / 調和分潮)、environment.waves、environment.current 的 table / tidal / field、environment.shipInteraction、ship.roll
// 的讀入 / 新增 / 修改 / 寫回結構、驗證規則(與核心 Scenario.StructuralIssues 相同訊息)、合成情境與情境庫(E06b/E18b)的 round-trip、草稿版本 2 → 3 升級。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';
import { describe, expect, it } from 'vitest';
import { parseScenario } from '../src/lib/scenario.js';
import {
  DRAFT_STORAGE_KEY,
  addCurrentTablePoint,
  addTideConstituent,
  addTidePoint,
  blankDraft,
  currentField,
  currentFieldIssues,
  currentTablePoints,
  currentTimeModel,
  estimateRollPeriod,
  fieldArrayText,
  fromYaml,
  loadDraft,
  parseFieldArrayText,
  patchCurrentModel,
  patchCurrentTablePoint,
  patchRoll,
  patchShipInteraction,
  patchTide,
  patchTideConstituent,
  patchTidePoint,
  patchWaves,
  removeCurrentTablePoint,
  removeTideConstituent,
  removeTidePoint,
  rollPeriodEstimate,
  setCurrentFieldArray,
  setCurrentFieldEnabled,
  setCurrentTableTimeKey,
  setCurrentTimeModel,
  setRollEnabled,
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

/** 含全部環境進階區段的合成情境(情境庫尚無 waves / field / shipInteraction / roll 的檔案)。 */
const FULL_YAML = `id: ENV
name: 環境進階
startTimeUtc: "2026-07-07T06:00:00Z"
ship: { id: FSB1, loading: full, roll: { enabled: true, naturalPeriod_s: 10.5, dampingRatio: 0.08, windHeelGain: 1.0, turnHeelGain: 1.2, waveGain: 0.8 } }
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
    expect(d.version).toBe(3);
    expect(tideMode(d)).toBe('harmonic');
    expect(tideConstituents(d)).toHaveLength(2);
    expect(d.waves).toEqual({ significantHeight_m: 2.0, peakPeriod_s: 7.0, direction_deg: 90, spectrum: 'jonswap', gamma: 3.3 });
    expect(currentTimeModel(d)).toBe('tidal');
    expect(currentField(d)?.['outside']).toBe('zero');
    expect(d.environment).toEqual({ windSpeed: 18, windDir: 40, currentSet: 85, currentDrift: 2.5, waterDepth: 14 });
    expect(d.shipInteraction).toEqual({ enabled: true, coefficientScale: 1.0, eventThreshold_t: 3 });
    expect(d.roll).toEqual({ enabled: true, naturalPeriod_s: 10.5, dampingRatio: 0.08, windHeelGain: 1.0, turnHeelGain: 1.2, waveGain: 0.8 });
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
    expect(validateDraft(e18b).issues).toEqual([]);
  });

  it('移除區段 → 輸出不含該鍵(raw 的鍵被覆寫);常數 set/drift 與流場模型各自獨立', () => {
    let d = fromYaml(FULL_YAML, 'ENV');
    d = setTideMode(d, 'none');
    d = setWavesEnabled(d, false);
    d = setCurrentTimeModel(d, 'none');
    d = setCurrentFieldEnabled(d, false);
    d = setShipInteractionEnabled(d, false);
    d = setRollEnabled(d, false);
    const o = toScenarioObject(d);
    const env = o['environment'] as RawSection;
    expect(env['tide']).toBeUndefined();
    expect(env['waves']).toBeUndefined();
    expect(env['current']).toEqual({ set: 85, drift: 2.5 });
    expect(env['seabed']).toBe('mud');
    // 有其他參數的區段關閉時保留並寫 enabled: false
    expect(env['shipInteraction']).toEqual({ enabled: false, coefficientScale: 1.0, eventThreshold_t: 3 });
    expect((o['ship'] as RawSection)['roll']).toEqual({ enabled: false, naturalPeriod_s: 10.5, dampingRatio: 0.08, windHeelGain: 1.0, turnHeelGain: 1.2, waveGain: 0.8 });
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

describe('草稿版本', () => {
  it('版本 2 草稿升級為 3:已編輯欄位沿用、環境進階區段由 raw 重建;版本 3 原樣;版本 4 或缺區段回 null', () => {
    const v3 = fromYaml(FULL_YAML, 'ENV');
    const v2 = { ...v3, version: 2, general: { ...v3.general, name: '舊草稿' } } as unknown as RawSection;
    for (const k of ['tide', 'waves', 'currentModel', 'shipInteraction', 'roll']) delete v2[k];
    const storage = new MemoryStorage();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v2));
    const back = loadDraft(storage)!;
    expect(back.version).toBe(3);
    expect(back.general.name).toBe('舊草稿');
    expect(back.tide).toEqual(v3.tide);
    expect(back.waves).toEqual(v3.waves);
    expect(back.currentModel).toEqual(v3.currentModel);
    expect(back.shipInteraction).toEqual(v3.shipInteraction);
    expect(back.roll).toEqual(v3.roll);
    expect(parseYaml(toYaml(back))).toEqual({ ...parseYaml(FULL_YAML), name: '舊草稿' });
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v3));
    expect(loadDraft(storage)).toEqual(JSON.parse(JSON.stringify(v3)));
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v3, version: 4 }));
    expect(loadDraft(storage)).toBeNull();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v3, currentModel: undefined }));
    expect(loadDraft(storage)).toBeNull();
  });
});
