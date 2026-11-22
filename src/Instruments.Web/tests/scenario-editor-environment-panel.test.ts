// 情境編輯器「環境進階」面板(happy-dom;components/instructor/scenario-editor-sections.ts renderEnvironmentExtras):
// 潮汐(模式、表點、分潮)、浪、流場模型(表、潮流、網格文字區塊)、船間交互作用、橫搖(ship.roll)、升沉/縱搖(ship.seakeeping;第二十九輪,R4 練習編輯)
// 各一組「新增/修改 → YAML 結構」與「驗證錯誤顯示」;升沉/縱搖另測「啟用但沒有浪」的提醒顯示與版本 3 草稿升級後的表單。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { parse as parseYaml } from 'yaml';
import type { ScenarioEditor } from '../src/components/instructor/scenario-editor.js';
import { DRAFT_STORAGE_KEY, SEAKEEPING_NO_WAVES_WARNING, fromYaml, type RawSection } from '../src/lib/scenario-editor.js';
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
    expect(q<HTMLInputElement>(el, 'input.sk-enabled').checked).toBe(false);
    expect(count(el, 'fieldset.seakeeping input[type="text"]')).toBe(0);
    expect(count(el, '.sk-no-waves')).toBe(0);
    // 升沉/縱搖區段緊接在橫搖區段之後(同一個環境進階面板)
    expect(q<HTMLElement>(el, 'fieldset.roll').nextElementSibling?.classList.contains('seakeeping')).toBe(true);
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
    // 草稿寫入 localStorage 並可還原(版本 4)
    const stored = JSON.parse(localStorage.getItem(DRAFT_STORAGE_KEY) ?? '{}') as RawSection;
    expect(stored['version']).toBe(4);
    expect(stored['roll']).toEqual({ enabled: false, naturalPeriod_s: 10.5, dampingRatio: 0.1, waveGain: 0.5 });
    expect(stored['seakeeping']).toBeNull();
  });
});

describe('環境進階面板:升沉/縱搖 ship.seakeeping', () => {
  const ship = (el: Editor) => (parseYaml(el.lastValidation.yaml) as RawSection)['ship'] as RawSection;
  const placeholder = (el: Editor, key: string) => q<HTMLInputElement>(el, `input.sk-${key}`).placeholder;
  const value = (el: Editor, key: string) => q<HTMLInputElement>(el, `input.sk-${key}`).value;
  /** 「驗證與輸出」面板的提醒列(⚠)。 */
  const outputWarnings = (el: Editor) => [...el.shadowRoot!.querySelectorAll('.panel.output .issues .warning')].map((n) => (n.textContent ?? '').trim());

  it('勾選啟用 → ship.seakeeping {enabled: true}、參數欄位(留白 = 預設;placeholder 顯示預設/估計)、說明與自然週期估計', async () => {
    const el = await mountEditor();
    await setChecked(el, 'input.sk-enabled', true);
    expect(ship(el)).toEqual({ id: 'FSB1', loading: 'full', seakeeping: { enabled: true } });
    expect(el.value.seakeeping).toEqual({ enabled: true });
    // 4 個選填參數皆留白,placeholder 為預設值或 particulars 估計
    for (const key of ['heaveDampingRatio', 'pitchDampingRatio', 'pitchGyradiusRatio', 'gmL_m']) expect(value(el, key), key).toBe('');
    expect(placeholder(el, 'heaveDampingRatio')).toBe('預設 0.35(0–1 之間,不含)');
    expect(placeholder(el, 'pitchDampingRatio')).toBe('預設 0.35(0–1 之間,不含)');
    expect(placeholder(el, 'pitchGyradiusRatio')).toBe('預設 0.25(> 0 且 ≤ 0.5)');
    expect(placeholder(el, 'gmL_m')).toBe('估計 119.0 m');
    // 自然週期估計(FSB1 滿載:T_h 6.42 s、T_p 4.99 s、GM_L 119.0 m;physics-waves.md 第 11.4 節)
    expect(text(el)).toContain('自然週期估計:升沉 ≈ 6.4 s、縱搖 ≈ 5.0 s(FSB1 L 99 m、B 18 m、T 5.701 m、C_b 0.738;GM_L 119.0 m 估計、k_yy/L 0.25');
    // 說明:線性、只輸出、不回饋 3 自由度;要有浪才有輸出;係數 ASSUMED
    const note = (q<HTMLElement>(el, '.sk-note').textContent ?? '').replace(/\s+/g, ' ');
    for (const phrase of ['線性、只輸出', '不回饋 3 自由度運動', '要有浪', 'ASSUMED', '留白 = 用核心預設']) expect(note, phrase).toContain(phrase);
    expect(text(el)).toContain('啟用(有浪時輸出 state.waveMotion、雜湊追加 SEAKEEP1)');
    // 改船與裝載 → 估計跟著變(FSB2 壓載:C_b 為估計)
    await setInput(el, 'select.ship', 'FSB2');
    await setInput(el, 'select.loading', 'ballast');
    expect(text(el)).toContain('自然週期估計:升沉 ≈ 6.3 s、縱搖 ≈ 4.9 s(FSB2 L 104 m、B 20 m、T 5.45 m、C_b 0.738 估計;GM_L 137.4 m 估計');
    expect(placeholder(el, 'gmL_m')).toBe('估計 137.4 m');
  });

  it('啟用但沒有浪 → 區段內與輸出面板顯示提醒、仍可載入;建立浪(Hs > 0)後消失、Hs 改 0 再出現;時間表 setEnvironment waves 也算有浪', async () => {
    const el = await mountEditor();
    expect(outputWarnings(el).some((w) => w.includes('ship.seakeeping'))).toBe(false);
    await setChecked(el, 'input.sk-enabled', true);
    // 提醒(不是錯誤):區段內一則、輸出面板一則,載入鈕可用
    expect((q<HTMLElement>(el, '.sk-no-waves').textContent ?? '').trim()).toBe(`⚠ ${SEAKEEPING_NO_WAVES_WARNING}`);
    expect(el.lastValidation.warnings).toContain(SEAKEEPING_NO_WAVES_WARNING);
    expect(outputWarnings(el)).toContain(`⚠ ${SEAKEEPING_NO_WAVES_WARNING}`);
    expect(el.lastValidation.issues).toEqual([]);
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(false);
    // 建立浪(預設 Hs 1 m)→ 提醒消失
    await setChecked(el, 'input.waves-enabled', true);
    expect(count(el, '.sk-no-waves')).toBe(0);
    expect(el.lastValidation.warnings).not.toContain(SEAKEEPING_NO_WAVES_WARNING);
    expect(outputWarnings(el).some((w) => w.includes('ship.seakeeping'))).toBe(false);
    // Hs = 0(= 關閉浪)→ 提醒再出現;改回 2.5 → 消失
    await setInput(el, 'input.wv-hs', '0');
    expect(count(el, '.sk-no-waves')).toBe(1);
    expect(el.lastValidation.warnings).toContain(SEAKEEPING_NO_WAVES_WARNING);
    await setInput(el, 'input.wv-hs', '2.5');
    expect(count(el, '.sk-no-waves')).toBe(0);
    // 取消浪 → 提醒;取消升沉/縱搖 → 提醒消失(未啟用不提醒)
    await setChecked(el, 'input.waves-enabled', false);
    expect(count(el, '.sk-no-waves')).toBe(1);
    await setChecked(el, 'input.sk-enabled', false);
    expect(count(el, '.sk-no-waves')).toBe(0);
    expect(el.lastValidation.warnings).not.toContain(SEAKEEPING_NO_WAVES_WARNING);
    expect(ship(el)).toEqual({ id: 'FSB1', loading: 'full' });
    // 沒有 environment.waves 但時間表以 setEnvironment 給浪 → 不提醒
    await setChecked(el, 'input.sk-enabled', true);
    expect(count(el, '.sk-no-waves')).toBe(1);
    await click(el, 'button.add-item'); // 新增一般項目(自動展開編輯器;範本已有 1 項 → 索引 1)
    const action = 'fieldset.tl-editor[data-index="1"] .row.action[data-action="0"]';
    await setInput(el, `${action} select.a-kind`, 'setEnvironment');
    expect(count(el, '.sk-no-waves')).toBe(1); // 預設的 setEnvironment 只改能見度,不算給浪
    await setInput(el, `${action} input.a-value`, '{ waves: { significantHeight_m: 0.8, peakPeriod_s: 10, direction_deg: 225 }, ramp_s: 600 }');
    expect(((parseYaml(el.lastValidation.yaml) as RawSection)['timeline'] as RawSection[])[1]?.['actions']).toEqual([{ setEnvironment: { waves: { significantHeight_m: 0.8, peakPeriod_s: 10, direction_deg: 225 }, ramp_s: 600 } }]);
    expect(count(el, '.sk-no-waves')).toBe(0);
    expect(el.lastValidation.warnings).not.toContain(SEAKEEPING_NO_WAVES_WARNING);
    expect(el.lastValidation.issues).toEqual([]);
    // 時間表改成關閉浪(waves: null)→ 提醒再出現
    await setInput(el, `${action} input.a-value`, '{ waves: null }');
    expect(count(el, '.sk-no-waves')).toBe(1);
  });

  it('參數輸入 → YAML;超出範圍顯示錯誤並停用載入;清空 = 刪鍵;關閉保留參數(enabled: false);草稿存版本 4 並可還原', async () => {
    const el = await mountEditor();
    await setChecked(el, 'input.waves-enabled', true);
    await setChecked(el, 'input.sk-enabled', true);
    // 阻尼比:1 不合法(開區間)
    await setInput(el, 'input.sk-heaveDampingRatio', '1');
    expect(el.lastValidation.issues).toEqual(['ship.seakeeping.heaveDampingRatio heaveDampingRatio 須在 0–1 之間(不含)']);
    expect(text(el)).toContain('✖ ship.seakeeping.heaveDampingRatio heaveDampingRatio 須在 0–1 之間(不含)');
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
    await setInput(el, 'input.sk-heaveDampingRatio', '0.3');
    expect(el.lastValidation.issues).toEqual([]);
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(false);
    await setInput(el, 'input.sk-pitchDampingRatio', '0');
    expect(el.lastValidation.issues).toEqual(['ship.seakeeping.pitchDampingRatio pitchDampingRatio 須在 0–1 之間(不含)']);
    await setInput(el, 'input.sk-pitchDampingRatio', '0.4');
    // k_yy/L:0.6 超出、0.5 可
    await setInput(el, 'input.sk-pitchGyradiusRatio', '0.6');
    expect(el.lastValidation.issues).toEqual(['ship.seakeeping.pitchGyradiusRatio pitchGyradiusRatio 須大於 0 且不超過 0.5']);
    await setInput(el, 'input.sk-pitchGyradiusRatio', '0.5');
    expect(el.lastValidation.issues).toEqual([]);
    await setInput(el, 'input.sk-pitchGyradiusRatio', '0.26');
    // GM_L:負值錯誤;150 → 估計改用情境值(不再標「估計」)、縱搖週期跟著變
    await setInput(el, 'input.sk-gmL_m', '-1');
    expect(el.lastValidation.issues).toEqual(['ship.seakeeping.gmL_m gmL_m 必須為正']);
    expect(text(el)).toContain('✖ ship.seakeeping.gmL_m gmL_m 必須為正');
    await setInput(el, 'input.sk-gmL_m', '150');
    expect(el.lastValidation.issues).toEqual([]);
    expect(text(el)).toContain('縱搖 ≈ 4.6 s(FSB1 L 99 m、B 18 m、T 5.701 m、C_b 0.738;GM_L 150.0 m、k_yy/L 0.26');
    expect(ship(el)).toEqual({ id: 'FSB1', loading: 'full', seakeeping: { enabled: true, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 150 } });
    expect(parseScenario(el.lastValidation.yaml, 'x').issues).toEqual([]);
    // 參數引用原樣寫出(與其他數值欄位相同)
    await setInput(el, 'input.sk-pitchDampingRatio', '${params:ship.pitchDamping}');
    expect(el.lastValidation.issues).toEqual([]);
    expect((ship(el)['seakeeping'] as RawSection)['pitchDampingRatio']).toBe('${params:ship.pitchDamping}');
    // 清空 = 刪鍵(用核心預設)
    await setInput(el, 'input.sk-pitchDampingRatio', '');
    await setInput(el, 'input.sk-gmL_m', '');
    expect(ship(el)['seakeeping']).toEqual({ enabled: true, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    expect(placeholder(el, 'gmL_m')).toBe('估計 119.0 m');
    // 關閉:有參數 → enabled: false 保留、欄位仍可編輯;再勾選 → 參數仍在
    await setChecked(el, 'input.sk-enabled', false);
    expect(ship(el)['seakeeping']).toEqual({ enabled: false, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    expect(text(el)).toContain('有區段但未啟用(enabled: false)');
    expect(value(el, 'heaveDampingRatio')).toBe('0.3');
    await setChecked(el, 'input.sk-enabled', true);
    expect(ship(el)['seakeeping']).toEqual({ enabled: true, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    // 草稿寫入 localStorage(版本 4)並在重新掛載後還原
    const stored = JSON.parse(localStorage.getItem(DRAFT_STORAGE_KEY) ?? '{}') as RawSection;
    expect(stored['version']).toBe(4);
    expect(stored['seakeeping']).toEqual({ enabled: true, heaveDampingRatio: 0.3, pitchGyradiusRatio: 0.26 });
    el.remove();
    const again = document.createElement('brm-scenario-editor') as Editor;
    Object.assign(again, { scenarios: [MINI], sources: SOURCES });
    document.body.appendChild(again);
    await again.updateComplete;
    expect(text(again)).toContain('已自本機草稿還原');
    expect(q<HTMLInputElement>(again, 'input.sk-enabled').checked).toBe(true);
    expect(value(again, 'heaveDampingRatio')).toBe('0.3');
    expect(value(again, 'pitchGyradiusRatio')).toBe('0.26');
    // 清到只剩 enabled 後關閉 → 整段移除、欄位收起
    await setInput(again, 'input.sk-heaveDampingRatio', '');
    await setInput(again, 'input.sk-pitchGyradiusRatio', '');
    await setChecked(again, 'input.sk-enabled', false);
    expect(ship(again)).toEqual({ id: 'FSB1', loading: 'full' });
    expect(count(again, 'fieldset.seakeeping input[type="text"]')).toBe(0);
    expect(text(again)).toContain('未啟用(無升沉/縱搖,與舊版相同)');
  });

  it('版本 3 草稿(localStorage 既有;範本有 ship.seakeeping 與浪)升級後表單顯示已啟用、內容不遺失', async () => {
    const SK_YAML = MINI_YAML.replace('ship: { id: FSB1, loading: full }', 'ship: { id: FSB1, loading: full, seakeeping: { enabled: true, gmL_m: 130 } }').replace(
      '  waterDepth: 14\n',
      '  waterDepth: 14\n  waves: { significantHeight_m: 0.5, peakPeriod_s: 10, direction_deg: 225, spectrum: jonswap }\n',
    );
    const expected = parseYaml(SK_YAML) as RawSection;
    expect((expected['ship'] as RawSection)['seakeeping']).toEqual({ enabled: true, gmL_m: 130 });
    // 版本 3 草稿:沒有 seakeeping 欄位(範本的 ship.seakeeping 只在 raw),名稱已被教官改過
    const { seakeeping: _sk, ...v3 } = fromYaml(SK_YAML, 'MINI');
    localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v3, version: 3, general: { ...v3.general, name: '舊草稿' } }));
    const el = document.createElement('brm-scenario-editor') as Editor;
    Object.assign(el, { scenarios: [MINI], sources: SOURCES });
    document.body.appendChild(el);
    await el.updateComplete;
    expect(text(el)).toContain('已自本機草稿還原');
    expect(el.value.version).toBe(4);
    expect(el.value.general.name).toBe('舊草稿');
    expect(el.value.seakeeping).toEqual({ enabled: true, gmL_m: 130 });
    expect(q<HTMLInputElement>(el, 'input.sk-enabled').checked).toBe(true);
    expect(value(el, 'gmL_m')).toBe('130');
    expect(count(el, '.sk-no-waves')).toBe(0);
    expect(el.lastValidation.issues).toEqual([]);
    expect(parseYaml(el.lastValidation.yaml)).toEqual({ ...expected, name: '舊草稿' });
    // 升級後可直接編輯
    await setInput(el, 'input.sk-gmL_m', '');
    await setChecked(el, 'input.sk-enabled', false);
    expect(ship(el)).toEqual({ id: 'FSB1', loading: 'full' });
  });
});
