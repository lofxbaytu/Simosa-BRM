// 完整教官模式「情境編輯」分頁(happy-dom):範本選擇與套用、表單編輯(環境、目標船、故障、評估)、驗證問題顯示與載入鈕停用、
// 「載入核心」→ 教官站以 loadScenario args.scenario 內嵌送核心(示範模式立即成功)、核心拒絕時顯示原因、localStorage 草稿還原。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import type { ScenarioEditor, ScenarioEditorResult } from '../src/components/instructor/scenario-editor.js';
import { DRAFT_STORAGE_KEY, type ScenarioDraft } from '../src/lib/scenario-editor.js';
import { parseScenario, type ScenarioSummary } from '../src/lib/scenario.js';
import type { ScenarioLoadRejection } from '../src/lib/sim-event.js';
import type { SimCommand } from '../src/types/command.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };
type Editor = ScenarioEditor & El;
type AppInner = El & { commandLog: ReadonlyArray<{ command: SimCommand; ack?: { ok: boolean; detail?: string } }>; activeScenario: ScenarioSummary | null; view: string };

const E07_YAML = `id: E07_mini
name: 迷你舵機故障
ship: { id: FSB1, loading: full }
origin: { lat: 23.78, lon: 120.1 }
initial: { position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8, telegraph: HAH }
environment:
  wind: { trueSpeed: 18, trueDir: 40, gustiness: 0.15 }
  current: { set: 230, drift: 2.0 }
  waterDepth: 14
  seabed: mud
briefing:
  text: 進港航道舵機故障
  roles:
    - { role: pilot, playedBy: leader, required: true }
zones:
  - id: anchorage_S
    name: 南側錨地
    type: noGo
    polygon: [{ lat: 23.785, lon: 120.125 }, { lat: 23.79, lon: 120.15 }, { lat: 23.78, lon: 120.152 }]
targets:
  - id: ANCH1
    name: SHENG LI
    initial: { position: { lat: 23.783, lon: 120.138 }, heading: 40 }
    behaviour: { mode: anchored, anchor: { swing: 20, period: 420 } }
timeline:
  - id: steeringFail
    name: 舵機故障
    phase: degraded
    when: { type: time, atLeast: 300 }
    actions:
      - injectFault: steeringGear
  - id: end
    when: { type: time, atLeast: 1800 }
    actions:
      - endExercise: { reason: 時間到 }
assessment:
  hardFail:
    - { id: grounding, rule: grounding }
    - { id: noGo, rule: zoneEntered, zoneId: anchorage_S }
  bands:
    - { id: xtdMax, metric: track.maxXtd_m, warn: 50, fail: 2x, penalty: 10, failPenalty: 25 }
  required:
    - { id: notifyEcr, after: { event: faultInjected, itemId: steeringFail }, expect: { acknowledge: { kind: notifyEcr } }, within_s: 120, penalty: 10 }
  scoring: { start: 100, passMark: 70 }
`;

const E07 = parseScenario(E07_YAML, 'data/scenarios/E07_mini.yaml');
const SOURCES = { [E07.path]: E07_YAML };

async function mountEditor(props: Record<string, unknown> = {}): Promise<Editor> {
  const el = document.createElement('brm-scenario-editor') as Editor;
  Object.assign(el, { scenarios: [E07], sources: SOURCES }, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

async function mountApp(query = '/?demo=1'): Promise<AppInner> {
  history.replaceState(null, '', query);
  const el = document.createElement('brm-instructor-app') as unknown as AppInner;
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function q<T extends HTMLElement>(el: HTMLElement, selector: string): T {
  const found = el.shadowRoot!.querySelector<T>(selector);
  if (!found) throw new Error(`缺元素 ${selector}`);
  return found;
}

function setInput(el: HTMLElement, selector: string, value: string): void {
  const input = q<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(el, selector);
  input.value = value;
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

function setChecked(el: HTMLElement, selector: string, on: boolean): void {
  const input = q<HTMLInputElement>(el, selector);
  input.checked = on;
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

function text(el: HTMLElement): string {
  return (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');
}

async function applyTemplate(el: Editor, id: string): Promise<void> {
  setInput(el, 'select.template', id);
  await el.updateComplete;
  q<HTMLButtonElement>(el, 'button.apply-template').click();
  await el.updateComplete;
}

beforeAll(async () => {
  await import('../src/components/instructor/scenario-editor.js');
  await import('../src/instructor-app.js');
});

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  document.body.innerHTML = '';
  localStorage.clear();
});

describe('情境編輯器:範本與表單', () => {
  it('預設空白範本通過驗證;套用既有情境後 id 加 _edit、涵蓋欄位帶入、未涵蓋欄位保留', async () => {
    const el = await mountEditor();
    expect(el.lastValidation.issues).toEqual([]);
    expect(text(el)).toContain('結構檢查通過');
    expect(text(el)).toContain('空白範本');
    await applyTemplate(el, 'E07_mini');
    const d = el.value;
    expect(d.template).toBe('E07_mini');
    expect(d.general.id).toBe('E07_mini_edit');
    expect(q<HTMLInputElement>(el, 'input.id').value).toBe('E07_mini_edit');
    expect(q<HTMLInputElement>(el, 'input.wind-speed').value).toBe('18');
    expect(q<HTMLInputElement>(el, 'input.depth').value).toBe('14');
    expect(q<HTMLTextAreaElement>(el, 'textarea.briefing').value).toBe('進港航道舵機故障');
    expect(el.shadowRoot!.querySelectorAll('.row.target')).toHaveLength(1);
    expect(el.shadowRoot!.querySelectorAll('.timeline .item')).toHaveLength(2);
    expect(text(el)).toContain('範本的 seabed 原樣保留');
    expect(text(el)).toContain('範本的 required 原樣保留');
    expect(text(el)).toContain('保留:behaviour');
    const out = el.lastValidation.scenario;
    expect(out['origin']).toEqual({ lat: 23.78, lon: 120.1 });
    expect((out['zones'] as unknown[]).length).toBe(1);
    expect((out['briefing'] as Record<string, unknown>)['roles']).toHaveLength(1);
    expect(el.lastValidation.issues).toEqual([]);
  });

  it('環境、初始狀態與簡報文字編輯反映在輸出;日/夜由開始時刻判定', async () => {
    const el = await mountEditor();
    await applyTemplate(el, 'E07_mini');
    setInput(el, 'input.wind-speed', '25');
    setInput(el, 'input.wind-dir', '${params:weather.windDir}');
    setInput(el, 'input.cur-drift', '');
    setInput(el, 'input.vis', '2');
    setInput(el, 'input.heading', '60');
    setInput(el, 'select.telegraph', 'DSAH');
    setInput(el, 'textarea.briefing', '改寫的簡報');
    setInput(el, 'input.start-utc', '2026-11-13T14:00:00Z');
    setInput(el, 'select.loading', 'ballast');
    await el.updateComplete;
    const out = el.lastValidation.scenario;
    expect(out['environment']).toEqual({ wind: { trueSpeed: 25, trueDir: '${params:weather.windDir}', gustiness: 0.15 }, current: { set: 230 }, waterDepth: 14, visibility_nm: 2, seabed: 'mud' });
    expect(out['initial']).toEqual({ position: { lat: 23.7696, lon: 120.0857 }, heading: 60, speed: 8, telegraph: 'DSAH' });
    expect((out['briefing'] as Record<string, unknown>)['text']).toBe('改寫的簡報');
    expect(out['ship']).toEqual({ id: 'FSB1', loading: 'ballast' });
    expect(text(el)).toContain('夜間');
    expect(el.lastValidation.yaml).toContain('trueDir: "${params:weather.windDir}"');
    expect(el.lastValidation.issues).toEqual([]);
    expect(el.lastValidation.summary.stats.paramRefs).toBe(1);
  });

  it('新增/編輯/刪除目標船;加入 2 個故障(時間觸發與手動、recordOnly);評估開關、硬性失敗、容許帶與通過分數', async () => {
    const el = await mountEditor();
    await applyTemplate(el, 'E07_mini');
    q<HTMLButtonElement>(el, 'button.add-target').click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelectorAll('.row.target')).toHaveLength(2);
    setInput(el, '.row.target[data-index="1"] input.t-id', 'X1');
    setInput(el, '.row.target[data-index="1"] input.t-name', 'CROSSING');
    setInput(el, '.row.target[data-index="1"] select.t-mode', 'colreg');
    setInput(el, '.row.target[data-index="1"] input.t-heading', '290');
    setInput(el, '.row.target[data-index="1"] input.t-speed', '9');
    setInput(el, '.row.target[data-index="1"] input.t-pos-lat', '23.8');
    await el.updateComplete;
    // 故障 1:時間觸發
    setInput(el, 'input.f-id', 'gyroFail');
    setInput(el, 'input.f-name', '電羅經失效');
    setInput(el, 'select.f-fault', 'gyroFailure');
    setInput(el, 'input.f-time', '600');
    setInput(el, 'select.f-phase', 'degraded');
    setInput(el, 'input.f-message', '觀察航向監視');
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.add-fault').click();
    await el.updateComplete;
    // 故障 2:手動、只記錄、帶參數
    setInput(el, 'input.f-id', 'vhfOut');
    setInput(el, 'select.f-fault', 'vhfFailure');
    setInput(el, 'select.f-trigger', 'manual');
    setInput(el, 'select.f-phase', '');
    setInput(el, 'input.f-args', '{ channel: 16 }');
    setChecked(el, 'input.f-record-only', true);
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.add-fault').click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelectorAll('.timeline .item')).toHaveLength(4);
    expect(q<HTMLInputElement>(el, 'input.f-id').value).toBe('fault1');
    // 刪除範本的 end 項目與原目標船
    q<HTMLButtonElement>(el, '.timeline .item[data-index="1"] button.tl-remove').click();
    await el.updateComplete;
    q<HTMLButtonElement>(el, '.row.target[data-index="0"] button.t-remove').click();
    await el.updateComplete;
    // 評估
    setChecked(el, 'input.hf-collision', true);
    setInput(el, 'select.hf-rule', 'cpa');
    setInput(el, 'input.hf-value', '0.3');
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.hf-add').click();
    await el.updateComplete;
    setInput(el, '.row.band[data-index="0"] input.b-warn', '${params:navigation.xtd.channel_m}');
    setInput(el, '.row.band[data-index="0"] input.b-fail', '3x');
    setInput(el, 'input.pass-mark', '75');
    await el.updateComplete;

    const out = el.lastValidation.scenario;
    expect(out['targets']).toEqual([{ id: 'X1', name: 'CROSSING', shipType: 'cargo', loa: 100, beam: 16, initial: { position: { lat: 23.8, lon: 120.0857 }, heading: 290, speed: 9 }, behaviour: { mode: 'colreg' } }]);
    expect((out['timeline'] as Record<string, unknown>[]).map((t) => t['id'])).toEqual(['steeringFail', 'gyroFail', 'vhfOut']);
    expect((out['timeline'] as Record<string, unknown>[])[1]).toEqual({
      id: 'gyroFail',
      name: '電羅經失效',
      phase: 'degraded',
      when: { type: 'time', atLeast: 600 },
      actions: [{ injectFault: 'gyroFailure' }, { message: { to: 'instructor', text: '觀察航向監視', severity: 'warning' } }],
    });
    expect((out['timeline'] as Record<string, unknown>[])[2]).toEqual({ id: 'vhfOut', manual: true, actions: [{ injectFault: { name: 'vhfFailure', args: { channel: 16 }, recordOnly: true } }] });
    expect(out['assessment']).toEqual({
      hardFail: [
        { id: 'grounding', rule: 'grounding' },
        { id: 'noGo', rule: 'zoneEntered', zoneId: 'anchorage_S' },
        { id: 'collision', rule: 'collision' },
        { id: 'cpaHard', rule: 'cpa', lessThan: 0.3 },
      ],
      bands: [{ id: 'xtdMax', metric: 'track.maxXtd_m', warn: '${params:navigation.xtd.channel_m}', fail: '3x', penalty: 10, failPenalty: 25 }],
      required: [{ id: 'notifyEcr', after: { event: 'faultInjected', itemId: 'steeringFail' }, expect: { acknowledge: { kind: 'notifyEcr' } }, within_s: 120, penalty: 10 }],
      scoring: { start: 100, passMark: 75 },
    });
    expect(el.lastValidation.issues).toEqual([]);
    expect(el.lastValidation.summary.stats).toMatchObject({ timelineItems: 3, manualItems: 1, assessmentRules: 6, targets: 1 });
    expect(text(el)).toContain('時間表 3 項');
  });
});

describe('情境編輯器:驗證問題與載入', () => {
  it('id 清空與目標船位置不全 → 問題清單、載入鈕停用;修正後回復;故障 id 重複與條件格式錯誤顯示錯誤', async () => {
    const el = await mountEditor();
    await applyTemplate(el, 'E07_mini');
    setInput(el, 'input.id', '');
    setInput(el, '.row.target[data-index="0"] input.t-pos-lat', '');
    await el.updateComplete;
    expect(el.lastValidation.issues).toContain('缺 id');
    expect(text(el)).toContain('✖ 缺 id');
    expect(text(el)).toContain('ANCH1 初始位置需完整');
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
    const events: ScenarioEditorResult[] = [];
    el.addEventListener('scenario-editor-load', (ev) => events.push((ev as CustomEvent<ScenarioEditorResult>).detail));
    setInput(el, 'input.id', 'E07_fixed');
    setInput(el, '.row.target[data-index="0"] input.t-pos-lat', '23.783');
    await el.updateComplete;
    expect(el.lastValidation.issues).toEqual([]);
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(false);
    // 故障表單錯誤不改草稿
    setInput(el, 'input.f-id', 'steeringFail');
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.add-fault').click();
    await el.updateComplete;
    expect(text(el)).toContain('時間表 id 重複:steeringFail');
    setInput(el, 'input.f-id', 'cond1');
    setInput(el, 'select.f-trigger', 'condition');
    await el.updateComplete;
    setInput(el, 'input.f-condition', '[1, 2]');
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.add-fault').click();
    await el.updateComplete;
    expect(text(el)).toContain('觸發條件 需為 YAML/JSON 物件');
    expect(el.value.timeline).toHaveLength(2);
    // 載入事件:args.scenario 物件、id 正確
    q<HTMLButtonElement>(el, 'button.load').click();
    expect(events).toHaveLength(1);
    expect(events[0]!.id).toBe('E07_fixed');
    expect(events[0]!.scenario['id']).toBe('E07_fixed');
    expect(events[0]!.summary.id).toBe('E07_fixed');
    expect(events[0]!.yaml).toContain('id: E07_fixed');
  });

  it('核心拒絕(rejection)顯示在輸出區;busy 時載入鈕停用', async () => {
    const rej: ScenarioLoadRejection = { scenarioId: 'E07_x', origin: 'inline', message: '內嵌情境載入失敗', issues: [{ path: 'zones[0]', message: '多邊形至少需要 3 點', severity: 'error' }], t: 3 };
    const el = await mountEditor({ rejection: rej, busy: true });
    expect(text(el)).toContain('核心拒絕載入 E07_x');
    expect(text(el)).toContain('zones[0]:多邊形至少需要 3 點');
    const btn = q<HTMLButtonElement>(el, 'button.load');
    expect(btn.disabled).toBe(true);
    expect(btn.textContent).toContain('載入中');
  });

  it('教官站完整模式:標題列「情境編輯」切換分頁;載入核心 → loadScenario args.scenario 內嵌(示範模式立即成功)並切到新情境', async () => {
    const app = await mountApp();
    const editor = app.shadowRoot!.querySelector('brm-scenario-editor') as Editor | null;
    expect(editor).not.toBeNull();
    expect(editor!.hasAttribute('hidden')).toBe(true);
    const btn = Array.from(app.shadowRoot!.querySelectorAll<HTMLButtonElement>('header .seg[aria-label="檢視"] button')).find((b) => b.textContent?.trim() === '情境編輯')!;
    btn.click();
    await app.updateComplete;
    expect(editor!.hasAttribute('hidden')).toBe(false);
    expect(app.shadowRoot!.querySelector('.full')!.hasAttribute('hidden')).toBe(true);
    expect(app.shadowRoot!.querySelector('brm-replay-view')!.hasAttribute('hidden')).toBe(true);
    // 編輯器範本清單來自教官站載入的情境(data/scenarios)
    const options = Array.from(editor!.shadowRoot!.querySelectorAll<HTMLOptionElement>('select.template option')).map((o) => o.value);
    expect(options[0]).toBe('');
    expect(options).toContain('E07_steering_failure');
    await applyTemplate(editor!, 'E01_baseline');
    setInput(editor!, 'input.id', 'E01_edited');
    await editor!.updateComplete;
    expect(editor!.lastValidation.issues).toEqual([]);
    q<HTMLButtonElement>(editor!, 'button.load').click();
    await app.updateComplete;
    const entry = [...app.commandLog].reverse().find((e) => e.command.type === 'loadScenario')!;
    expect(entry).toBeDefined();
    const args = entry.command.args as { id: string; scenario: Record<string, unknown>; path?: string };
    expect(args.id).toBe('E01_edited');
    expect(typeof args.scenario).toBe('object');
    expect(args.scenario['id']).toBe('E01_edited');
    expect(args.path).toBeUndefined();
    expect(entry.command.value).toBeUndefined();
    expect(entry.ack?.ok).toBe(true);
    expect(app.activeScenario?.id).toBe('E01_edited');
    expect(app.activeScenario?.path).toBe('');
    // 編輯器視圖中數字鍵不產生 BRM 標記(快速鍵只在即時版面)
    const before = (app as unknown as { markers: unknown[] }).markers.length;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '1' }));
    await app.updateComplete;
    expect((app as unknown as { markers: unknown[] }).markers.length).toBe(before);
  });
});

describe('情境編輯器:localStorage 草稿', () => {
  it('修改後自動存草稿;重新掛載還原(含範本名與故障);清除草稿回到空白範本', async () => {
    const el = await mountEditor();
    await applyTemplate(el, 'E07_mini');
    setInput(el, 'input.name', '草稿測試');
    setInput(el, 'input.f-id', 'f1');
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button.add-fault').click();
    await el.updateComplete;
    const stored = JSON.parse(localStorage.getItem(DRAFT_STORAGE_KEY)!) as ScenarioDraft;
    expect(stored.general.name).toBe('草稿測試');
    expect(stored.timeline).toHaveLength(3);
    el.remove();
    const again = await mountEditor();
    expect(again.value.general.name).toBe('草稿測試');
    expect(again.value.template).toBe('E07_mini');
    expect(q<HTMLInputElement>(again, 'input.name').value).toBe('草稿測試');
    expect(again.shadowRoot!.querySelectorAll('.timeline .item')).toHaveLength(3);
    expect(text(again)).toContain('已自本機草稿還原');
    expect(q<HTMLSelectElement>(again, 'select.template').value).toBe('E07_mini');
    q<HTMLButtonElement>(again, 'button.reset-draft').click();
    await again.updateComplete;
    expect(again.value.template).toBe('');
    expect(again.value.general.id).toBe('NEW_exercise');
    expect(JSON.parse(localStorage.getItem(DRAFT_STORAGE_KEY)!).general.id).toBe('NEW_exercise');
  });

  it('壞的草稿資料被忽略,回到空白範本', async () => {
    localStorage.setItem(DRAFT_STORAGE_KEY, '{"version":9}');
    const el = await mountEditor();
    expect(el.value.general.id).toBe('NEW_exercise');
    expect(text(el)).not.toContain('已自本機草稿還原');
  });
});
