// 情境編輯器草稿模型(lib/scenario-editor.ts):每份 data/scenarios/*.yaml 解析 → 草稿 → YAML → parseScenario 摘要一致且物件深相等(未涵蓋欄位不遺失);
// 空白範本加 2 個故障與 1 艘目標船後的 YAML 結構;驗證問題;載入指令形狀;localStorage 草稿往返;YAML 引號規則。
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';
import { describe, expect, it } from 'vitest';
import { loadScenarioInlineCommand } from '../src/lib/instructor-command.js';
import { serializeCommand } from '../src/lib/command.js';
import { parseScenario } from '../src/lib/scenario.js';
import {
  DRAFT_STORAGE_KEY,
  addBand,
  addFaultItem,
  addHardFail,
  addTarget,
  blankDraft,
  clearDraft,
  dayNightLabel,
  describeTimeline,
  fromYaml,
  hasHardFail,
  loadDraft,
  newTarget,
  numberOrRefFromText,
  patchBand,
  patchTarget,
  removeTimelineItem,
  saveDraft,
  scenarioToYaml,
  setHardFail,
  toScenarioObject,
  toYaml,
  validateDraft,
  type DraftStorage,
  type ScenarioDraft,
} from '../src/lib/scenario-editor.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const scenarioDir = path.resolve(here, '../../../data/scenarios');
const files = readdirSync(scenarioDir)
  .filter((f) => /\.ya?ml$/i.test(f))
  .sort();

/** 摘要去掉 undefined 欄位後比較(toEqual 對 undefined 寬容,JSON 往返使兩邊一致)。 */
function plain<T>(v: T): T {
  return JSON.parse(JSON.stringify(v)) as T;
}

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

describe('草稿 round-trip(data/scenarios 全部情境)', () => {
  it('至少有 E01–E22、F01–F03 的情境檔', () => {
    expect(files.length).toBeGreaterThanOrEqual(30);
  });

  for (const file of files) {
    it(`${file}:parse → draft → toYaml → parseScenario 摘要一致、物件深相等`, () => {
      const text = readFileSync(path.join(scenarioDir, file), 'utf-8');
      const draft = fromYaml(text, file.replace(/\.ya?ml$/i, ''));
      const yaml = toYaml(draft);
      const before = parseScenario(text, `data/scenarios/${file}`);
      const after = parseScenario(yaml, `data/scenarios/${file}`);
      expect(plain(after)).toEqual(plain(before));
      expect(after.issues).toEqual(before.issues);
      // 未涵蓋欄位(route/zones/briefing 附件、sensors、tugs、berth …)原樣保留:解析後的物件深相等
      expect(parseYaml(yaml)).toEqual(parseYaml(text));
      // 草稿可序列化(localStorage)且序列化後輸出相同
      const again = JSON.parse(JSON.stringify(draft)) as ScenarioDraft;
      expect(toYaml(again)).toBe(yaml);
      expect(yaml.startsWith(`# ${before.name}\n# 由教官站情境編輯器產生`)).toBe(true);
    });
  }

  it('environment.tide(潮汐表/調和常數)原樣保留、不被環境表單欄位覆寫', () => {
    const withTide = files.filter((f) => {
      const doc = parseYaml(readFileSync(path.join(scenarioDir, f), 'utf-8')) as Record<string, unknown>;
      return typeof doc['environment'] === 'object' && doc['environment'] !== null && 'tide' in (doc['environment'] as Record<string, unknown>);
    });
    for (const f of withTide) {
      const text = readFileSync(path.join(scenarioDir, f), 'utf-8');
      const original = (parseYaml(text) as { environment: Record<string, unknown> }).environment['tide'];
      let d = fromYaml(text, f);
      d = { ...d, environment: { ...d.environment, windSpeed: 30, waterDepth: 9 } };
      const env = toScenarioObject(d)['environment'] as Record<string, unknown>;
      expect(env['tide']).toEqual(original);
      expect(env['waterDepth']).toBe(9);
    }
    // 沒有潮汐情境檔時也以合成情境驗證
    const d = fromYaml('id: T\nship: { id: FSB1 }\ninitial: { position: { lat: 23, lon: 120 }, heading: 0 }\nenvironment: { waterDepth: 10, tide: { table: [{ t: 0, height_m: 1.2 }] } }\n');
    const env = toScenarioObject({ ...d, environment: { ...d.environment, visibility_nm: 3 } })['environment'] as Record<string, unknown>;
    expect(env).toEqual({ waterDepth: 10, tide: { table: [{ t: 0, height_m: 1.2 }] }, visibility_nm: 3 });
  });

  it('E07:草稿讀出環境、目標船、時間表與評估的涵蓋欄位', () => {
    const d = fromYaml(readFileSync(path.join(scenarioDir, 'E07_steering_failure.yaml'), 'utf-8'), 'E07_steering_failure');
    expect(d.general.id).toBe('E07_steering_failure');
    expect(d.general.startTimeUtc).toBe('2026-07-07T06:30:00Z');
    expect(d.general.briefing).toContain('麥寮港進港');
    expect(d.ship).toEqual({ id: 'FSB1', loading: 'full' });
    expect(d.initial).toEqual({ position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8, telegraph: 'HAH', rudder: 0 });
    expect(d.environment).toEqual({ windSpeed: 18, windDir: 40, gustiness: 0.15, currentSet: 230, currentDrift: 2, waterDepth: 14, visibility_nm: 6 });
    expect(d.targets.map((t) => [t.id, t.mode, t.heading, t.speed])).toEqual([
      ['OUT1', 'waypoints', 262, 6],
      ['ANCH1', 'anchored', 40, undefined],
    ]);
    expect(d.targets[0]!.raw['trigger']).toEqual({ type: 'time', time: 240 });
    const rows = describeTimeline(d.timeline);
    expect(rows.map((r) => r.id)).toEqual(['pilotBoarded', 'vtsCall', 'steeringFail', 'ecrPrompt', 'emergencySteering', 'speedCoach', 'end']);
    expect(rows[2]).toMatchObject({ fault: true, faultNames: ['steeringGear'], issues: [] });
    expect(rows[2]!.condition).toContain('航段 ≥ 1');
    expect(rows[4]!.condition).toBe('手動(教官觸發)');
    expect(hasHardFail(d, 'grounding')).toBe(true);
    expect(d.assessment.hardFail).toHaveLength(4);
    expect(d.assessment.bands).toHaveLength(5);
    expect(d.assessment.passMark).toBe('${params:assessment.passMark}');
    expect(d.assessment.start).toBe(100);
    // 未涵蓋欄位在 raw
    expect(Array.isArray(d.raw['zones'])).toBe(true);
    expect((d.raw['assessment'] as Record<string, unknown>)['required']).toHaveLength(4);
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
  });
});

describe('空白範本組題:2 個故障 + 1 艘目標船', () => {
  function compose(): ScenarioDraft {
    let d = blankDraft();
    d = { ...d, general: { ...d.general, id: 'NEW_two_faults', name: '兩個故障', startTimeUtc: '2026-11-13T01:00:00Z' } };
    d = addTarget(d, { ...newTarget(d), id: 'X1', name: 'CROSSING', mode: 'colreg', heading: 290, speed: 9 });
    d = addFaultItem(d, { id: 'gyroFail', name: '電羅經失效', fault: 'gyroFailure', trigger: { kind: 'time', atLeast: 300 }, phase: 'degraded', message: '觀察航向監視' });
    d = addFaultItem(d, { id: 'meStop', fault: 'mainEngine', args: { reason: 'fuel' }, recordOnly: false, trigger: { kind: 'condition', when: { type: 'ownSpeed', lessThan: 5 } }, phase: 'emergency' });
    d = addFaultItem(d, { id: 'vhfOut', fault: 'vhfFailure', recordOnly: true, trigger: { kind: 'manual' } });
    d = setHardFail(d, 'collision', false);
    d = addHardFail(d, { id: 'cpaHard', rule: 'cpa', lessThan: 0.2 });
    d = addBand(d);
    d = patchBand(d, 0, 'id', 'xtd');
    d = patchBand(d, 0, 'direction', 'above');
    d = { ...d, assessment: { ...d.assessment, passMark: 75 } };
    return d;
  }

  it('toScenarioObject:targets / timeline / assessment 結構符合 schema 寫法', () => {
    const d = compose();
    const o = toScenarioObject(d);
    expect(o['id']).toBe('NEW_two_faults');
    expect(o['startTimeUtc']).toBe('2026-11-13T01:00:00Z');
    expect(o['briefing']).toBeUndefined();
    expect(o['targets']).toEqual([{ id: 'X1', name: 'CROSSING', shipType: 'cargo', loa: 100, beam: 16, initial: { position: { lat: 23.93, lon: 119.98 }, heading: 290, speed: 9 }, behaviour: { mode: 'colreg' } }]);
    expect(o['timeline']).toEqual([
      {
        id: 'gyroFail',
        name: '電羅經失效',
        phase: 'degraded',
        when: { type: 'time', atLeast: 300 },
        actions: [{ injectFault: 'gyroFailure' }, { message: { to: 'instructor', text: '觀察航向監視', severity: 'warning' } }],
      },
      { id: 'meStop', phase: 'emergency', when: { type: 'ownSpeed', lessThan: 5 }, actions: [{ injectFault: { name: 'mainEngine', args: { reason: 'fuel' } } }] },
      { id: 'vhfOut', manual: true, actions: [{ injectFault: { name: 'vhfFailure', recordOnly: true } }] },
    ]);
    expect(o['assessment']).toEqual({
      parameterSet: 'data/parameters/company-procedures.yaml',
      hardFail: [
        { id: 'grounding', rule: 'grounding' },
        { id: 'cpaHard', rule: 'cpa', lessThan: 0.2 },
      ],
      bands: [{ id: 'xtd', metric: 'track.maxXtd_m', warn: 50, fail: '2x', penalty: 10, failPenalty: 25 }],
      scoring: { start: 100, passMark: 75 },
    });
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.stats).toMatchObject({ timelineItems: 3, manualItems: 1, assessmentRules: 3, targets: 1 });
    expect(v.summary.timeline.map((t) => t.phase)).toEqual(['degraded', 'emergency', undefined]);
    // YAML 可再解析成同一物件,且參數引用/時間戳加引號
    expect(parseYaml(v.yaml)).toEqual(o);
    expect(v.yaml).toContain('startTimeUtc: "2026-11-13T01:00:00Z"');
    expect(v.yaml).toContain('    - injectFault: gyroFailure');
  });

  it('removeTimelineItem / patchTarget 不影響其他項目;raw 不被草稿編輯污染', () => {
    const d = compose();
    const d2 = removeTimelineItem(d, 1);
    expect(d2.timeline.map((t) => t['id'])).toEqual(['gyroFail', 'vhfOut']);
    expect(d.timeline).toHaveLength(3);
    const d3 = patchTarget(d2, 0, { speed: undefined, heading: 10 });
    expect(d3.targets[0]).not.toHaveProperty('speed');
    expect(toScenarioObject(d3)['targets']).toEqual([expect.objectContaining({ initial: { position: { lat: 23.93, lon: 119.98 }, heading: 10 } })]);
    expect((d3.raw['targets'] as unknown[]).length).toBe(0);
  });

  it('loadScenarioInlineCommand:args.scenario 為情境物件、args.id 正確、不帶 value/path', () => {
    const v = validateDraft(compose());
    const cmd = loadScenarioInlineCommand(v.summary.id, v.scenario);
    expect(cmd).toEqual({ type: 'loadScenario', args: { id: 'NEW_two_faults', scenario: v.scenario } });
    expect(typeof (cmd.args as { scenario: unknown }).scenario).toBe('object');
    const json = JSON.parse(serializeCommand(cmd)) as { args: { id: string; scenario: Record<string, unknown> } };
    expect(json.args.id).toBe('NEW_two_faults');
    expect(json.args.scenario['id']).toBe('NEW_two_faults');
    expect((json.args.scenario['timeline'] as unknown[]).length).toBe(3);
    expect('value' in cmd).toBe(false);
  });
});

describe('驗證問題', () => {
  it('缺 id、目標船 id 重複/位置不全、未知故障(警告)、zoneEntered 無區域、UKC ≤ 0', () => {
    let d = blankDraft();
    d = { ...d, general: { ...d.general, id: '' } };
    d = addTarget(d, { ...newTarget(d), id: 'A' });
    d = addTarget(d, { ...newTarget(d), id: 'A', position: { lat: 23 } });
    d = addFaultItem(d, { id: 'f1', fault: 'notAFault', trigger: { kind: 'time', atLeast: 10 } });
    d = addHardFail(d, { id: 'z', rule: 'zoneEntered', zoneId: 'nope' });
    d = { ...d, environment: { ...d.environment, waterDepth: 3 } };
    const v = validateDraft(d);
    expect(v.issues).toContain('缺 id');
    expect(v.issues).toContain('targets[1] id 重複:A');
    expect(v.issues.some((i) => i.startsWith('targets[1] A 初始位置'))).toBe(true);
    expect(v.issues).toContain('assessment.hardFail[2] zoneEntered 引用不存在的區域 nope');
    expect(v.issues.some((i) => i.includes('初始 UKC') && i.includes('≤ 0'))).toBe(true);
    expect(v.warnings.some((w) => w.includes('notAFault'))).toBe(true);
    expect(v.summary.id).toBe('draft');
  });

  it('id 含非法字元、x/y 無 origin、條件引用不存在的航點;合法草稿無問題', () => {
    let d = blankDraft();
    d = { ...d, general: { ...d.general, id: 'bad id/1' }, initial: { ...d.initial, position: { x: 0, y: 0 } } };
    d = addFaultItem(d, { id: 'f', fault: 'steeringGear', trigger: { kind: 'condition', when: { type: 'ownDistance', waypoint: 'WP9', lessThan: 1 } } });
    const v = validateDraft(d);
    expect(v.issues).toContain('id 只能用英數字、_、-、.(作為紀錄檔名)');
    expect(v.issues.some((i) => i.includes('origin'))).toBe(true);
    expect(v.issues).toContain('時間表 f:條件引用不存在的航點 WP9');
    const ok = validateDraft(blankDraft());
    expect(ok.issues).toEqual([]);
    expect(ok.warnings).toContain('時間表沒有項目(無故障、語音與結束條件;練習由教官手動結束)');
  });
});

describe('localStorage 草稿', () => {
  it('saveDraft / loadDraft 往返;壞資料或版本不合回 null;clearDraft 移除', () => {
    const storage = new MemoryStorage();
    expect(loadDraft(storage)).toBeNull();
    let d = blankDraft();
    d = addFaultItem(d, { id: 'f', fault: 'blackout', trigger: { kind: 'time', atLeast: 60 } });
    expect(saveDraft(d, storage)).toBe(true);
    const back = loadDraft(storage)!;
    expect(back).toEqual(JSON.parse(JSON.stringify(d)));
    expect(toYaml(back)).toBe(toYaml(d));
    storage.setItem(DRAFT_STORAGE_KEY, '{"version":2}');
    expect(loadDraft(storage)).toBeNull();
    storage.setItem(DRAFT_STORAGE_KEY, 'not json');
    expect(loadDraft(storage)).toBeNull();
    saveDraft(d, storage);
    clearDraft(storage);
    expect(storage.map.has(DRAFT_STORAGE_KEY)).toBe(false);
    expect(saveDraft(d, null)).toBe(false);
    expect(loadDraft(null)).toBeNull();
  });
});

describe('YAML 輸出與工具函式', () => {
  it('易誤判的字串值加雙引號(on/off、時間戳、數字樣、參數引用),鍵不加,數字與布林不加', () => {
    const y = scenarioToYaml({ bnwas: { mode: 'on' }, n: 85, s: '085', t: '2026-01-01T00:00:00Z', ref: '${params:a.b}', flag: true, fail: '2x', empty: '', text: 'line1\nline2\n' });
    expect(y).toContain('mode: "on"');
    expect(y).toContain('n: 85');
    expect(y).toContain('s: "085"');
    expect(y).toContain('t: "2026-01-01T00:00:00Z"');
    expect(y).toContain('ref: "${params:a.b}"');
    expect(y).toContain('flag: true');
    expect(y).toContain('fail: 2x');
    expect(y).toContain('empty: ""');
    expect(y).toContain('text: |');
  });

  it('numberOrRefFromText、dayNightLabel', () => {
    expect(numberOrRefFromText(' 12.5 ')).toBe(12.5);
    expect(numberOrRefFromText('')).toBeUndefined();
    expect(numberOrRefFromText('${params:x}')).toBe('${params:x}');
    expect(dayNightLabel('2026-11-13T01:00:00Z', 120)).toContain('白天');
    expect(dayNightLabel('2026-11-13T14:00:00Z', 120)).toContain('夜間');
    expect(dayNightLabel(undefined, 120)).toContain('未指定');
    expect(dayNightLabel('nope', 120)).toContain('無法解析');
  });
});
