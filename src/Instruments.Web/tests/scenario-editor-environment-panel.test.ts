// 情境編輯器「環境進階」面板(happy-dom;components/instructor/scenario-editor-sections.ts renderEnvironmentExtras):
// 潮汐(模式、表點、分潮)、浪、流場模型(表、潮流、網格文字區塊)、船間交互作用、橫搖(ship.roll)各一組「新增/修改 → YAML 結構」與「驗證錯誤顯示」。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { parse as parseYaml } from 'yaml';
import type { ScenarioEditor } from '../src/components/instructor/scenario-editor.js';
import { DRAFT_STORAGE_KEY, type RawSection } from '../src/lib/scenario-editor.js';
import { parseScenario } from '../src/lib/scenario.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };
type Editor = ScenarioEditor & El;

const MINI_YAML = `id: MINI
name: 迷你範本
startTimeUtc: "2026-07-07T06:00:00Z"
ship: { id: FSB1, loading: full }
initial: { position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8, telegraph: HAH }
environment:
  wind: { trueSpeed: 18, trueDir: 40, gustiness: 0.15 }
  current: { set: 230, drift: 2.0 }
  waterDepth: 14
  seabed: mud
timeline:
  - id: end
    when: { type: time, atLeast: 1800 }
    actions:
      - endExercise: { reason: 時間到 }
assessment:
  hardFail:
    - { id: grounding, rule: grounding }
  scoring: { start: 100, passMark: 70 }
`;

const MINI = parseScenario(MINI_YAML, 'data/scenarios/MINI.yaml');
const SOURCES = { [MINI.path]: MINI_YAML };

async function mountEditor(): Promise<Editor> {
  const el = document.createElement('brm-scenario-editor') as Editor;
  Object.assign(el, { scenarios: [MINI], sources: SOURCES });
  document.body.appendChild(el);
  await el.updateComplete;
  const sel = q<HTMLSelectElement>(el, 'select.template');
  sel.value = 'MINI';
  sel.dispatchEvent(new Event('change', { bubbles: true }));
  await el.updateComplete;
  q<HTMLButtonElement>(el, 'button.apply-template').click();
  await el.updateComplete;
  return el;
}

function q<T extends HTMLElement>(el: HTMLElement, selector: string): T {
  const found = el.shadowRoot!.querySelector<T>(selector);
  if (!found) throw new Error(`缺元素 ${selector}`);
  return found;
}

function count(el: HTMLElement, selector: string): number {
  return el.shadowRoot!.querySelectorAll(selector).length;
}

async function setInput(el: El, selector: string, value: string): Promise<void> {
  const input = q<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(el, selector);
  input.value = value;
  input.dispatchEvent(new Event('change', { bubbles: true }));
  await el.updateComplete;
}

async function setChecked(el: El, selector: string, on: boolean): Promise<void> {
  const input = q<HTMLInputElement>(el, selector);
  input.checked = on;
  input.dispatchEvent(new Event('change', { bubbles: true }));
  await el.updateComplete;
}

async function click(el: El, selector: string): Promise<void> {
  q<HTMLButtonElement>(el, selector).click();
  await el.updateComplete;
}

function text(el: HTMLElement): string {
  return (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');
}

function env(el: Editor): RawSection {
  return (parseYaml(el.lastValidation.yaml) as RawSection)['environment'] as RawSection;
}

beforeAll(async () => {
  await import('../src/components/instructor/scenario-editor.js');
});

beforeEach(() => {
  localStorage.removeItem(DRAFT_STORAGE_KEY);
});

afterEach(() => {
  document.body.innerHTML = '';
  localStorage.removeItem(DRAFT_STORAGE_KEY);
});

describe('環境進階面板', () => {
  it('範本載入後面板存在、各區段關閉;環境面板不再把 tide/waves/shipInteraction 列為保留、seabed 仍保留', async () => {
    const el = await mountEditor();
    expect(el.value.general.id).toBe('MINI_edit');
    expect(count(el, '.panel.environment-extras')).toBe(1);
    expect(q<HTMLSelectElement>(el, 'select.tide-mode').value).toBe('none');
    expect(q<HTMLInputElement>(el, 'input.waves-enabled').checked).toBe(false);
    expect(q<HTMLSelectElement>(el, 'select.cur-model').value).toBe('none');
    expect(q<HTMLInputElement>(el, 'input.field-enabled').checked).toBe(false);
    expect(q<HTMLInputElement>(el, 'input.si-enabled').checked).toBe(false);
    expect(q<HTMLInputElement>(el, 'input.roll-enabled').checked).toBe(false);
    expect(text(el)).toContain('範本的 seabed 原樣保留');
    expect(el.lastValidation.issues).toEqual([]);
  });

  it('潮汐:潮高表 → 2 點、改 t 不遞增顯示驗證錯誤、改回後通過、新增表點、切換 UTC;調和潮分潮', async () => {
    const el = await mountEditor();
    await setInput(el, 'select.tide-mode', 'table');
    expect(count(el, '.tide-point')).toBe(2);
    await setInput(el, '.tide-point[data-index="1"] input.tp-t', '0');
    expect(el.lastValidation.issues).toContain('environment.tide.table[1] 潮高表的 t 必須嚴格遞增');
    expect(text(el)).toContain('✖ environment.tide.table[1] 潮高表的 t 必須嚴格遞增');
    await setInput(el, '.tide-point[data-index="1"] input.tp-t', '1800');
    await setInput(el, '.tide-point[data-index="1"] input.tp-h', '1.2');
    await click(el, 'button.add-tide-point');
    expect(count(el, '.tide-point')).toBe(3);
    expect(el.lastValidation.issues).toEqual([]);
    expect(env(el)['tide']).toEqual({ table: [{ t: 0, height_m: 0 }, { t: 1800, height_m: 1.2 }, { t: 5400, height_m: 1.2 }] });
    await click(el, 'button.tide-time-key');
    expect(count(el, 'input.tp-utc')).toBe(3);
    expect((env(el)['tide'] as RawSection)['table']).toEqual([
      { utc: '2026-07-07T06:00:00Z', height_m: 0 },
      { utc: '2026-07-07T06:30:00Z', height_m: 1.2 },
      { utc: '2026-07-07T07:30:00Z', height_m: 1.2 },
    ]);
    expect(el.lastValidation.issues).toEqual([]);
    await click(el, '.tide-point[data-index="2"] button.tp-remove');
    expect(count(el, '.tide-point')).toBe(2);
    // 調和潮
    await setInput(el, 'select.tide-mode', 'harmonic');
    expect(count(el, '.constituent')).toBe(1);
    await click(el, 'button.add-constituent');
    await setInput(el, '.constituent[data-index="1"] input.tc-amp', '-1');
    expect(el.lastValidation.issues).toEqual(['environment.tide.harmonic.constituents[1] amplitude_m 不可為負']);
    await setInput(el, '.constituent[data-index="1"] input.tc-amp', '0.4');
    await setInput(el, 'input.tide-mean', '0.3');
    expect(el.lastValidation.issues).toEqual([]);
    expect(env(el)['tide']).toEqual({ harmonic: { meanLevel_m: 0.3, constituents: [{ name: 'M2', amplitude_m: 1, period_s: 44714, phase_deg: 0 }, { name: 'S2', amplitude_m: 0.4, period_s: 43200, phase_deg: 0 }] } });
    await setInput(el, 'select.tide-mode', 'none');
    expect(env(el)['tide']).toBeUndefined();
  });

  it('浪:勾選建立(來向取風向 40)、Hs 負值顯示錯誤、浪譜與 γ → YAML、取消移除', async () => {
    const el = await mountEditor();
    await setChecked(el, 'input.waves-enabled', true);
    expect(env(el)['waves']).toEqual({ significantHeight_m: 1, peakPeriod_s: 7, direction_deg: 40 });
    await setInput(el, 'input.wv-hs', '-2');
    expect(el.lastValidation.issues).toEqual(['environment.waves significantHeight_m 必須為 ≥ 0 的有限數值']);
    expect(text(el)).toContain('✖ environment.waves significantHeight_m');
    await setInput(el, 'input.wv-hs', '2.5');
    await setInput(el, 'select.wv-spectrum', 'jonswap');
    await setInput(el, 'input.wv-gamma', '2.5');
    expect(el.lastValidation.issues).toEqual([]);
    expect(env(el)['waves']).toEqual({ significantHeight_m: 2.5, peakPeriod_s: 7, direction_deg: 40, spectrum: 'jonswap', gamma: 2.5 });
    await setChecked(el, 'input.waves-enabled', false);
    expect(env(el)['waves']).toBeUndefined();
  });

  it('流場模型:流場表(取常數流 230°/2 kn)→ 速度負值錯誤;潮流需潮汐;網格文字區塊尺寸錯誤與修正、timeScale', async () => {
    const el = await mountEditor();
    await setInput(el, 'select.cur-model', 'table');
    expect(count(el, '.current-point')).toBe(2);
    expect((env(el)['current'] as RawSection)['table']).toEqual([
      { t: 0, speed_kn: 2, direction_deg: 230 },
      { t: 3600, speed_kn: 2, direction_deg: 230 },
    ]);
    await setInput(el, '.current-point[data-index="1"] input.cp-speed', '-1');
    expect(el.lastValidation.issues).toEqual(['environment.current.table[1] speed_kn 不可為負']);
    await setInput(el, '.current-point[data-index="1"] input.cp-speed', '0.5');
    await click(el, 'button.add-current-point');
    expect(count(el, '.current-point')).toBe(3);
    expect(el.lastValidation.issues).toEqual([]);
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(false);
    // 潮流:需潮汐 → 錯誤;建立潮汐後通過;常數 set/drift 不變
    await setInput(el, 'select.cur-model', 'tidal');
    expect(el.lastValidation.issues).toEqual(['environment.current.tidal tidal 需要 environment.tide']);
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
    await setInput(el, 'select.tide-mode', 'table');
    await setInput(el, 'input.ctd-ebb-speed', '3');
    expect(el.lastValidation.issues).toEqual([]);
    const cur = env(el)['current'] as RawSection;
    expect(cur['set']).toBe(230);
    expect(cur['drift']).toBe(2);
    expect(cur['tidal']).toEqual({ flood: { speed_kn: 2, direction_deg: 230 }, ebb: { speed_kn: 3, direction_deg: 50 }, slackWindow_min: 20 });
    expect(cur['table']).toBeUndefined();
    // 空間網格
    await setChecked(el, 'input.field-enabled', true);
    expect(text(el)).toContain('尺寸正確:u_kn / v_kn 各 3 列 × 3 行');
    await setInput(el, 'textarea.fg-u', '0 0.5 1\n0.2 0.7 1.2');
    expect(el.lastValidation.issues).toEqual(['environment.current.field u_kn 須有 ny = 3 列(得到 2)']);
    expect(text(el)).toContain('✖ environment.current.field u_kn 須有 ny = 3 列(得到 2)');
    await setInput(el, 'input.fg-ny', '2');
    await setInput(el, 'textarea.fg-v', '1 0.8 0.6\n0.9 0.7 0.5');
    await setInput(el, 'select.fg-timescale', 'tidal');
    await setInput(el, 'select.fg-outside', 'zero');
    expect(el.lastValidation.issues).toEqual([]);
    expect((env(el)['current'] as RawSection)['field']).toEqual({ grid: { x0: -1000, y0: -1000, dx: 1000, dy: 1000, nx: 3, ny: 2 }, u_kn: [[0, 0.5, 1], [0.2, 0.7, 1.2]], v_kn: [[1, 0.8, 0.6], [0.9, 0.7, 0.5]], outside: 'zero', timeScale: 'tidal' });
    expect(el.lastValidation.summary.environment.current?.source).toBe('field+tidal');
    // 非數值文字 → 表單錯誤、草稿不變
    await setInput(el, 'textarea.fg-u', '0 x 1\n0 0 0');
    expect(text(el)).toContain('流場陣列第 1 行的「x」不是數值');
    expect((env(el)['current'] as RawSection)['field']).toMatchObject({ u_kn: [[0, 0.5, 1], [0.2, 0.7, 1.2]] });
    await setChecked(el, 'input.field-enabled', false);
    expect((env(el)['current'] as RawSection)['field']).toBeUndefined();
  });

  it('船間交互作用與橫搖:勾選 → enabled: true、參數負值錯誤、橫搖週期估計提示與 ship.roll 輸出', async () => {
    const el = await mountEditor();
    await setChecked(el, 'input.si-enabled', true);
    expect(env(el)['shipInteraction']).toEqual({ enabled: true });
    await setInput(el, 'input.si-coefficientScale', '-0.5');
    expect(el.lastValidation.issues).toEqual(['environment.shipInteraction coefficientScale 必須為 ≥ 0 的有限數值']);
    await setInput(el, 'input.si-coefficientScale', '0.5');
    await setInput(el, 'input.si-eventThreshold_t', '2');
    expect(el.lastValidation.issues).toEqual([]);
    expect(env(el)['shipInteraction']).toEqual({ enabled: true, coefficientScale: 0.5, eventThreshold_t: 2 });
    await setChecked(el, 'input.si-enabled', false);
    expect(env(el)['shipInteraction']).toEqual({ enabled: false, coefficientScale: 0.5, eventThreshold_t: 2 });
    // 橫搖(FSB1 滿載:B 18 m、GM 1.96 m → 估計 10.3 s)
    await setChecked(el, 'input.roll-enabled', true);
    expect(text(el)).toContain('估計 ≈ 10.3 s(FSB1 B 18 m、GM 1.96 m)');
    expect(el.lastValidation.warnings.some((w) => w.includes('ship.roll 未給 naturalPeriod_s'))).toBe(true);
    await setInput(el, 'input.roll-naturalPeriod_s', '0');
    expect(el.lastValidation.issues).toEqual(['ship.roll naturalPeriod_s 必須為正']);
    expect(text(el)).toContain('✖ ship.roll naturalPeriod_s 必須為正');
    await setInput(el, 'input.roll-naturalPeriod_s', '10.5');
    await setInput(el, 'input.roll-dampingRatio', '0.1');
    await setInput(el, 'input.roll-waveGain', '0.5');
    expect(el.lastValidation.issues).toEqual([]);
    expect((parseYaml(el.lastValidation.yaml) as RawSection)['ship']).toEqual({ id: 'FSB1', loading: 'full', roll: { enabled: true, naturalPeriod_s: 10.5, dampingRatio: 0.1, waveGain: 0.5 } });
    await setChecked(el, 'input.roll-enabled', false);
    expect(((parseYaml(el.lastValidation.yaml) as RawSection)['ship'] as RawSection)['roll']).toEqual({ enabled: false, naturalPeriod_s: 10.5, dampingRatio: 0.1, waveGain: 0.5 });
    // 草稿寫入 localStorage 並可還原(版本 3)
    const stored = JSON.parse(localStorage.getItem(DRAFT_STORAGE_KEY) ?? '{}') as RawSection;
    expect(stored['version']).toBe(3);
    expect(stored['roll']).toEqual({ enabled: false, naturalPeriod_s: 10.5, dampingRatio: 0.1, waveGain: 0.5 });
  });
});
