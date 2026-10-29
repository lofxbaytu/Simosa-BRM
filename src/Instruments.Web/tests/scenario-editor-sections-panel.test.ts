// 情境編輯器擴充區段的面板(happy-dom):航線、區域(頂點)、時間表逐項編輯、簡報角色/附件、感測器/警報/BNWAS、必要事件與評估設定、
// 拖船/錨/泊位/岸壁;每區段「新增一項 → 輸出 YAML 結構正確」與「驗證錯誤顯示」;含新區段的草稿還原。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import type { ScenarioEditor } from '../src/components/instructor/scenario-editor.js';
import { DRAFT_STORAGE_KEY, type RawSection, type ScenarioDraft } from '../src/lib/scenario-editor.js';
import { parseScenario } from '../src/lib/scenario.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };
type Editor = ScenarioEditor & El;

const MINI_YAML = `id: MINI
name: 迷你範本
ship: { id: FSB1, loading: full }
origin: { lat: 23.78, lon: 120.1 }
initial: { position: { lat: 23.7696, lon: 120.0857 }, heading: 52, speed: 8, telegraph: HAH }
environment:
  wind: { trueSpeed: 18, trueDir: 40, gustiness: 0.15 }
  current: { set: 230, drift: 2.0 }
  waterDepth: 14
briefing:
  text: 進港航道舵機故障
  roles:
    - { role: pilot, playedBy: leader, required: true }
  minParticipants: 2
alerts: { offHeading_deg: 10 }
bnwas: { mode: "on", dormant_s: 600 }
route:
  name: 進港
  waypoints:
    - { name: WP1, lat: 23.77, lon: 120.09 }
    - { name: WP2, lat: 23.78, lon: 120.12, checkpoint: true, speedLimit_kn: 8 }
zones:
  - id: anchorage_S
    name: 南側錨地
    type: noGo
    polygon: [{ lat: 23.785, lon: 120.125 }, { lat: 23.79, lon: 120.15 }, { lat: 23.78, lon: 120.152 }]
targets:
  - id: ANCH1
    initial: { position: { lat: 23.783, lon: 120.138 }, heading: 40 }
    behaviour: { mode: anchored }
tugs:
  - { id: TG1, bollardPull_t: 40, attach: fwdStbd, modes: [push, pull], initial: { mode: standby } }
timeline:
  - id: steeringFail
    name: 舵機故障
    phase: degraded
    when: { type: time, atLeast: 300 }
    actions:
      - injectFault: steeringGear
      - message: { to: instructor, text: 觀察, severity: warning }
  - id: end
    when: { type: time, atLeast: 1800 }
    actions:
      - endExercise: { reason: 時間到 }
assessment:
  hardFail:
    - { id: grounding, rule: grounding }
  required:
    - { id: notifyEcr, after: { event: faultInjected, itemId: steeringFail }, expect: { acknowledge: { kind: notifyEcr } }, within_s: 120, penalty: 10 }
  ukc: { min: { metres: 1.0 } }
  scoring: { start: 100, passMark: 70 }
`;

const MINI = parseScenario(MINI_YAML, 'data/scenarios/MINI.yaml');
const SOURCES = { [MINI.path]: MINI_YAML };

async function mountEditor(props: Record<string, unknown> = {}): Promise<Editor> {
  const el = document.createElement('brm-scenario-editor') as Editor;
  Object.assign(el, { scenarios: [MINI], sources: SOURCES }, props);
  document.body.appendChild(el);
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

async function mountWithTemplate(): Promise<Editor> {
  const el = await mountEditor();
  await setInput(el, 'select.template', 'MINI');
  await click(el, 'button.apply-template');
  expect(el.lastValidation.issues).toEqual([]);
  return el;
}

function scenario(el: Editor): RawSection {
  return el.lastValidation.scenario;
}

beforeAll(async () => {
  await import('../src/components/instructor/scenario-editor.js');
});

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  document.body.innerHTML = '';
  localStorage.clear();
});

describe('航線 ROUTE', () => {
  it('範本航點帶入;新增/編輯/上移/刪除航點與檢查點 → route 結構;空白範本勾選建立航線', async () => {
    const el = await mountWithTemplate();
    expect(count(el, '.row.wp')).toBe(2);
    expect(q<HTMLInputElement>(el, '.row.wp[data-index="1"] input.wp-checkpoint').checked).toBe(true);
    expect(q<HTMLInputElement>(el, 'input.route-name').value).toBe('進港');
    await click(el, 'button.add-waypoint');
    expect(count(el, '.row.wp')).toBe(3);
    await setInput(el, '.row.wp[data-index="2"] input.wp-name', 'PILOT');
    await setInput(el, '.row.wp[data-index="2"] input.wp-pos-lat', '23.8');
    await setInput(el, '.row.wp[data-index="2"] input.wp-speedLimit_kn', '${params:port.speed_kn}');
    await setInput(el, '.row.wp[data-index="2"] input.wp-arrivalRadius_m', '200');
    await setInput(el, '.row.wp[data-index="2"] input.wp-report', 'VTS 報告');
    await setChecked(el, '.row.wp[data-index="2"] input.wp-checkpoint', true);
    await click(el, '.row.wp[data-index="2"] button.wp-up');
    await click(el, '.row.wp[data-index="0"] button.wp-remove');
    await setInput(el, 'input.route-xtd', '300');
    await setInput(el, 'input.route-startleg', '0');
    expect(scenario(el)['route']).toEqual({
      name: '進港',
      xtdLimit_m: 300,
      startLeg: 0,
      waypoints: [
        { name: 'PILOT', lat: 23.8, lon: 120.12, speedLimit_kn: '${params:port.speed_kn}', arrivalRadius_m: 200, report: 'VTS 報告', checkpoint: true },
        { name: 'WP2', lat: 23.78, lon: 120.12, checkpoint: true, speedLimit_kn: 8 },
      ],
    });
    expect(el.lastValidation.issues).toEqual([]);
    expect(text(el)).toContain('航點 2');
    // 空白範本:勾選建立航線、取消勾選移除
    await click(el, 'button.reset-draft');
    expect(text(el)).toContain('無航線');
    await setChecked(el, 'input.route-enabled', true);
    expect(count(el, '.row.wp')).toBe(2);
    expect((scenario(el)['route'] as RawSection)['waypoints']).toEqual([
      { name: 'WP1', lat: 23.9, lon: 119.98 },
      { name: 'WP2', lat: 23.92, lon: 119.98 },
    ]);
    await setChecked(el, 'input.route-enabled', false);
    expect(scenario(el)['route']).toBeUndefined();
  });

  it('驗證錯誤顯示:航點名稱重複、位置清空 → 問題清單與載入鈕停用', async () => {
    const el = await mountWithTemplate();
    await setInput(el, '.row.wp[data-index="1"] input.wp-name', 'WP1');
    await setInput(el, '.row.wp[data-index="1"] input.wp-pos-lon', '');
    expect(text(el)).toContain('✖ route.waypoints[1] name 重複:WP1');
    expect(text(el)).toContain('route.waypoints[1] WP1 需完整');
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
  });
});

describe('區域 ZONES', () => {
  it('新增區域 → 型別/速限/UKC 下限/頂點編輯 → zones 結構', async () => {
    const el = await mountWithTemplate();
    expect(count(el, '.row.zone')).toBe(1);
    await click(el, 'button.add-zone');
    expect(count(el, '.row.zone')).toBe(2);
    await setInput(el, '.row.zone[data-index="1"] input.z-id', 'channel');
    await setInput(el, '.row.zone[data-index="1"] input.z-name', '航道');
    await setInput(el, '.row.zone[data-index="1"] select.z-type', 'ukcPolicy');
    await setInput(el, '.row.zone[data-index="1"] select.z-ukc-kind', 'fractionOfDraft');
    await setInput(el, '.row.zone[data-index="1"] input.z-ukc-value', '${params:navigation.ukc.channel_fraction}');
    await setInput(el, '.row.zone[data-index="1"] input.z-depth', '12');
    await click(el, '.row.zone[data-index="1"] button.z-vertices');
    expect(count(el, '.row.zone[data-index="1"] .row.vertex')).toBe(4);
    await setInput(el, '.row.zone[data-index="1"] .row.vertex[data-vertex="0"] input.z-v-lat', '23.8');
    await click(el, '.row.zone[data-index="1"] button.add-vertex');
    expect(count(el, '.row.zone[data-index="1"] .row.vertex')).toBe(5);
    await click(el, '.row.zone[data-index="1"] .row.vertex[data-vertex="4"] button.v-remove');
    const zones = scenario(el)['zones'] as RawSection[];
    expect(zones).toHaveLength(2);
    expect(zones[1]).toEqual({
      id: 'channel',
      name: '航道',
      type: 'ukcPolicy',
      polygon: [
        { lat: 23.8, lon: 120.0757 },
        { lat: 23.7796, lon: 120.0957 },
        { lat: 23.7596, lon: 120.0957 },
        { lat: 23.7596, lon: 120.0757 },
      ],
      ukcMin: { fractionOfDraft: '${params:navigation.ukc.channel_fraction}' },
      waterDepth_m: 12,
    });
    expect(el.lastValidation.issues).toEqual([]);
    expect(el.lastValidation.yaml).toContain('fractionOfDraft: "${params:navigation.ukc.channel_fraction}"');
    expect(text(el)).toContain('區域 2');
    await click(el, '.row.zone[data-index="0"] button.z-remove');
    expect((scenario(el)['zones'] as RawSection[]).map((z) => z['id'])).toEqual(['channel']);
  });

  it('驗證錯誤顯示:刪到少於 3 點、id 重複;zoneEntered 硬性失敗可引用新區域', async () => {
    const el = await mountWithTemplate();
    await click(el, '.row.zone[data-index="0"] button.z-vertices');
    await click(el, '.row.zone[data-index="0"] .row.vertex[data-vertex="2"] button.v-remove');
    expect(text(el)).toContain('✖ zones[0] 多邊形少於 3 點');
    await click(el, 'button.add-zone');
    await setInput(el, '.row.zone[data-index="1"] input.z-id', 'anchorage_S');
    expect(text(el)).toContain('✖ zones[1] id 重複:anchorage_S');
    await setInput(el, '.row.zone[data-index="1"] input.z-id', 'noGo2');
    await click(el, '.row.zone[data-index="0"] button.add-vertex');
    await setInput(el, 'select.hf-rule', 'zoneEntered');
    await setInput(el, 'input.hf-value', 'noGo2');
    await click(el, 'button.hf-add');
    expect(el.lastValidation.issues).toEqual([]);
    expect((scenario(el)['assessment'] as RawSection)['hardFail']).toContainEqual({ id: 'noGo_noGo2', rule: 'zoneEntered', zoneId: 'noGo2' });
  });
});

describe('時間表逐項編輯', () => {
  it('展開編輯:名稱/階段/可見/觸發(條件)/動作值與種類/新增動作/上下移;新增一般項目', async () => {
    const el = await mountWithTemplate();
    expect(count(el, 'fieldset.tl-editor')).toBe(0);
    await click(el, '.timeline .item[data-index="0"] button.tl-edit');
    expect(count(el, 'fieldset.tl-editor')).toBe(1);
    expect(q<HTMLInputElement>(el, 'fieldset.tl-editor input.tl-time').value).toBe('300');
    expect(count(el, 'fieldset.tl-editor .row.action')).toBe(2);
    expect(q<HTMLInputElement>(el, 'fieldset.tl-editor .row.action[data-action="1"] input.a-value').value).toBe('{ to: instructor, text: 觀察, severity: warning }');
    await setInput(el, 'fieldset.tl-editor input.tl-name', '舵機卡死');
    await setInput(el, 'fieldset.tl-editor select.tl-phase', 'emergency');
    await setInput(el, 'fieldset.tl-editor select.tl-visibility', 'all');
    await setInput(el, 'fieldset.tl-editor input.tl-hold', '5');
    await setInput(el, 'fieldset.tl-editor select.tl-trigger', 'condition');
    expect(q<HTMLInputElement>(el, 'fieldset.tl-editor input.tl-condition').value).toBe('{ type: ownSpeed, lessThan: 5 }');
    await setInput(el, 'fieldset.tl-editor input.tl-condition', '{ type: routeLeg, atLeast: 1 }');
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="1"] input.a-value', '{ to: all, text: 舵機故障, severity: alarm }');
    await click(el, 'fieldset.tl-editor button.add-action');
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="2"] select.a-kind', 'autopilot');
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="2"] input.a-value', '{ enabled: false }');
    await click(el, 'fieldset.tl-editor .row.action[data-action="2"] button.a-up');
    await click(el, 'fieldset.tl-editor button.tl-down');
    const timeline = scenario(el)['timeline'] as RawSection[];
    expect(timeline.map((t) => t['id'])).toEqual(['end', 'steeringFail']);
    expect(timeline[1]).toEqual({
      id: 'steeringFail',
      name: '舵機卡死',
      phase: 'emergency',
      visibility: 'all',
      holdFor_s: 5,
      when: { type: 'routeLeg', atLeast: 1 },
      actions: [{ injectFault: 'steeringGear' }, { autopilot: { enabled: false } }, { message: { to: 'all', text: '舵機故障', severity: 'alarm' } }],
    });
    expect(el.lastValidation.issues).toEqual([]);
    expect(text(el)).toContain('航段 ≥ 1');
    // 新增一般項目 → 自動展開編輯器
    await click(el, 'button.add-item');
    expect(count(el, '.timeline .item')).toBe(3);
    expect(count(el, 'fieldset.tl-editor[data-index="2"]')).toBe(1);
    await setInput(el, 'fieldset.tl-editor[data-index="2"] input.tl-id', 'fog');
    await setInput(el, 'fieldset.tl-editor[data-index="2"] .row.action[data-action="0"] select.a-kind', 'setEnvironment');
    expect((scenario(el)['timeline'] as RawSection[])[2]).toEqual({ id: 'fog', when: { type: 'time', atLeast: 60 }, actions: [{ setEnvironment: { visibility_nm: 2, ramp_s: 300 } }] });
  });

  it('驗證錯誤顯示:動作 YAML 格式錯誤不改草稿、空內容、arm 引用不存在的項目、id 重複', async () => {
    const el = await mountWithTemplate();
    await click(el, '.timeline .item[data-index="1"] button.tl-edit');
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="0"] input.a-value', '{ reason: [');
    expect(text(el)).toMatch(/Flow sequence|Unexpected|flow/i);
    expect((scenario(el)['timeline'] as RawSection[])[1]).toMatchObject({ actions: [{ endExercise: { reason: '時間到' } }] });
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="0"] input.a-value', '');
    expect(text(el)).toContain('動作 endExercise 需有內容');
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="0"] select.a-kind', 'arm');
    await setInput(el, 'fieldset.tl-editor .row.action[data-action="0"] input.a-value', 'ghost');
    expect(text(el)).toContain('✖ 時間表 end:arm 引用不存在的項目 ghost');
    await setInput(el, 'fieldset.tl-editor input.tl-id', 'steeringFail');
    expect(text(el)).toContain('✖ timeline id 重複:steeringFail');
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
  });
});

describe('簡報角色/附件、感測器/警報/BNWAS', () => {
  it('角色與附件新增/編輯/刪除、最少人數與時長 → briefing 物件;驗證最少人數', async () => {
    const el = await mountWithTemplate();
    expect(count(el, '.row.role')).toBe(1);
    expect(q<HTMLInputElement>(el, 'input.b-min').value).toBe('2');
    await click(el, 'button.add-role');
    await setInput(el, '.row.role[data-index="1"] input.r-role', 'vts');
    await setInput(el, '.row.role[data-index="1"] select.r-played', 'voiceScript');
    await setChecked(el, '.row.role[data-index="0"] input.r-required', false);
    await click(el, 'button.add-attachment');
    await setInput(el, '.row.attachment[data-index="0"] input.at-title', '航程計畫');
    await setInput(el, '.row.attachment[data-index="0"] input.at-file', 'docs/plan.pdf');
    await setInput(el, 'input.b-min', '3');
    await setInput(el, 'input.b-duration', '3600');
    expect(scenario(el)['briefing']).toEqual({
      text: '進港航道舵機故障',
      roles: [
        { role: 'pilot', playedBy: 'leader' },
        { role: 'vts', playedBy: 'voiceScript' },
      ],
      minParticipants: 3,
      attachments: [{ title: '航程計畫', file: 'docs/plan.pdf' }],
      duration_s: 3600,
    });
    expect(el.lastValidation.issues).toEqual([]);
    await click(el, '.row.role[data-index="0"] button.r-remove');
    await click(el, '.row.attachment[data-index="0"] button.at-remove');
    expect((scenario(el)['briefing'] as RawSection)['roles']).toEqual([{ role: 'vts', playedBy: 'voiceScript' }]);
    expect((scenario(el)['briefing'] as RawSection)['attachments']).toEqual([]);
    await setInput(el, 'input.b-min', '0');
    expect(text(el)).toContain('✖ briefing.minParticipants 需為 ≥ 1 的整數');
  });

  it('感測器/警報/BNWAS 欄位 → 區段輸出(on 加引號);負值與未知模式顯示錯誤;清空移除區段', async () => {
    const el = await mountWithTemplate();
    expect(q<HTMLInputElement>(el, 'input.al-offHeading_deg').value).toBe('10');
    expect(q<HTMLSelectElement>(el, 'select.bn-mode').value).toBe('on');
    await setInput(el, 'input.s-magneticCompassError_deg', '1.5');
    await setInput(el, 'input.al-shallowWater_m', '${params:navigation.ukc.alarm_m}');
    await setInput(el, 'input.bn-stage3_s', '90');
    expect(scenario(el)['sensors']).toEqual({ magneticCompassError_deg: 1.5 });
    expect(scenario(el)['alerts']).toEqual({ offHeading_deg: 10, shallowWater_m: '${params:navigation.ukc.alarm_m}' });
    expect(scenario(el)['bnwas']).toEqual({ mode: 'on', dormant_s: 600, stage3_s: 90 });
    expect(el.lastValidation.yaml).toContain('mode: "on"');
    expect(el.lastValidation.issues).toEqual([]);
    await setInput(el, 'input.al-escalate_s', '-1');
    await setInput(el, 'input.bn-dormant_s', '100');
    expect(text(el)).toContain('✖ alerts.escalate_s 需 ≥ 0');
    expect(text(el)).toContain('⚠ bnwas.dormant_s 100 s 不在');
    await setInput(el, 'input.al-escalate_s', '');
    await setInput(el, 'input.al-offHeading_deg', '');
    await setInput(el, 'input.al-shallowWater_m', '');
    expect(scenario(el)['alerts']).toBeUndefined();
    await setInput(el, 'select.bn-mode', 'auto');
    expect((scenario(el)['bnwas'] as RawSection)['mode']).toBe('auto');
  });
});

describe('評估:必要事件與設定', () => {
  it('範本必要事件帶入;新增/編輯/刪除 → required 結構;minCpa、會遇、UKC、操縱設定', async () => {
    const el = await mountWithTemplate();
    expect(count(el, '.row.required')).toBe(1);
    expect(q<HTMLInputElement>(el, '.row.required[data-index="0"] input.rq-expect').value).toBe('{ acknowledge: { kind: notifyEcr } }');
    expect(q<HTMLSelectElement>(el, 'select.ukc-kind').value).toBe('metres');
    expect(q<HTMLInputElement>(el, 'input.ukc-value').value).toBe('1');
    await click(el, 'button.add-required');
    expect(count(el, '.row.required')).toBe(2);
    await setInput(el, '.row.required[data-index="1"] input.rq-id', 'vtsReport');
    await setInput(el, '.row.required[data-index="1"] input.rq-event', 'reportPointPassed');
    await setInput(el, '.row.required[data-index="1"] input.rq-item', '');
    await setInput(el, '.row.required[data-index="1"] input.rq-waypoint', 'WP2');
    await setInput(el, '.row.required[data-index="1"] input.rq-expect', '{ any: [ rudderOrder, { acknowledge: { kind: report } } ] }');
    await setInput(el, '.row.required[data-index="1"] input.rq-within', '${params:navigation.vts.report_s}');
    await setInput(el, '.row.required[data-index="1"] input.rq-penalty', '5');
    await setInput(el, '.row.required[data-index="1"] input.rq-late', '0.05');
    await click(el, '.row.required[data-index="1"] button.rq-up');
    await setInput(el, 'input.as-minCpa', '0.8');
    await setInput(el, 'input.enc-detectionRange_nm', '5');
    await setInput(el, 'select.ukc-kind', 'fractionOfDraft');
    await setInput(el, 'input.ukc-value', '0.15');
    await setInput(el, 'input.man-rotLimit_degPerMin', '${params:manoeuvring.rotLimit_degPerMin}');
    const a = scenario(el)['assessment'] as RawSection;
    expect(a['required']).toEqual([
      { id: 'vtsReport', after: { event: 'reportPointPassed', waypoint: 'WP2' }, expect: { any: ['rudderOrder', { acknowledge: { kind: 'report' } }] }, within_s: '${params:navigation.vts.report_s}', penalty: 5, latePenaltyPer_s: 0.05 },
      { id: 'notifyEcr', after: { event: 'faultInjected', itemId: 'steeringFail' }, expect: { acknowledge: { kind: 'notifyEcr' } }, within_s: 120, penalty: 10 },
    ]);
    expect(a['minCpa_nm']).toBe(0.8);
    expect(a['encounter']).toEqual({ detectionRange_nm: 5 });
    expect(a['ukc']).toEqual({ min: { fractionOfDraft: 0.15 } });
    expect(a['manoeuvring']).toEqual({ rotLimit_degPerMin: '${params:manoeuvring.rotLimit_degPerMin}' });
    expect(el.lastValidation.issues).toEqual([]);
    expect(el.lastValidation.summary.stats.assessmentRules).toBe(3);
    await click(el, '.row.required[data-index="0"] button.rq-remove');
    expect((scenario(el)['assessment'] as RawSection)['required']).toHaveLength(1);
    await setInput(el, 'select.ukc-kind', '');
    expect((scenario(el)['assessment'] as RawSection)['ukc']).toBeUndefined();
  });

  it('驗證錯誤顯示:expect 空白/格式錯誤不改草稿、after.itemId 引用不存在的項目、id 重複、minCpa ≤ 0', async () => {
    const el = await mountWithTemplate();
    await setInput(el, '.row.required[data-index="0"] input.rq-expect', '');
    expect(text(el)).toContain('期望(expect)不可空白');
    await setInput(el, '.row.required[data-index="0"] input.rq-expect', '{ any: [');
    expect((scenario(el)['assessment'] as RawSection)['required']).toEqual([expect.objectContaining({ expect: { acknowledge: { kind: 'notifyEcr' } } })]);
    await setInput(el, '.row.required[data-index="0"] input.rq-item', 'ghost');
    expect(text(el)).toContain('✖ assessment.required[0] after.itemId 引用不存在的時間表項目 ghost');
    await click(el, 'button.add-required');
    await setInput(el, '.row.required[data-index="1"] input.rq-id', 'notifyEcr');
    expect(text(el)).toContain('✖ assessment.required[1] id 重複:notifyEcr');
    await setInput(el, 'input.as-minCpa', '0');
    expect(text(el)).toContain('✖ assessment.minCpa_nm 需 > 0');
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
  });
});

describe('拖船 / 錨 / 泊位 / 岸壁', () => {
  it('拖船新增/欄位/模式/刪除;錨;泊位與繫纜;岸壁 → 各區段結構', async () => {
    const el = await mountWithTemplate();
    expect(count(el, '.row.tug')).toBe(1);
    await click(el, 'button.add-tug');
    await setInput(el, '.row.tug[data-index="1"] input.tg-name', '麥寮2號');
    await setInput(el, '.row.tug[data-index="1"] input.tg-bp', '45');
    await setInput(el, '.row.tug[data-index="1"] input.tg-attach', '-40, 8');
    await setChecked(el, '.row.tug[data-index="1"] input.tg-push', false);
    await setInput(el, '.row.tug[data-index="1"] select.tg-mode', 'pull');
    await setInput(el, '.row.tug[data-index="1"] input.tg-power', '30');
    await setInput(el, '.row.tug[data-index="0"] input.tg-line', '30');
    expect(scenario(el)['tugs']).toEqual([
      { id: 'TG1', bollardPull_t: 40, attach: 'fwdStbd', modes: ['push', 'pull'], initial: { mode: 'standby' }, lineLength_m: 30 },
      { id: 'TG2', name: '麥寮2號', bollardPull_t: 45, attach: { x: -40, y: 8 }, lineLength_m: 40, modes: ['pull'], initial: { mode: 'pull', direction_deg: 90, power_pct: 30 } },
    ]);
    // 錨
    await setChecked(el, 'input.anchors-enabled', true);
    await setInput(el, 'input.an-port-length_shackles', '5');
    await setInput(el, 'input.an-port-bearing_deg', '45');
    await setInput(el, 'input.an-holdingFactor', '0.9');
    expect(scenario(el)['anchors']).toEqual({ holdingFactor: 0.9, port: { length_shackles: 5, bearing_deg: 45 } });
    // 泊位
    await setChecked(el, 'input.berth-enabled', true);
    await setInput(el, 'input.berth-file', 'data/areas/mailiao-berth.json');
    await setInput(el, 'input.berth-id', 'MLO-OIL-3');
    await click(el, 'button.add-line');
    await setInput(el, '.row.line[data-index="0"] input.ln-id', 'HEAD');
    await setInput(el, '.row.line[data-index="0"] input.ln-bollard', 'B7');
    await setInput(el, '.row.line[data-index="0"] select.ln-initial', 'fast');
    await click(el, 'button.add-line');
    await setInput(el, '.row.line[data-index="1"] input.ln-fairlead', 'aftPort');
    await setInput(el, '.row.line[data-index="1"] input.ln-mbl', '60');
    expect(scenario(el)['berth']).toEqual({
      file: 'data/areas/mailiao-berth.json',
      id: 'MLO-OIL-3',
      lines: [
        { id: 'HEAD', fairlead: 'fwdPort', bollard: 'B7', mbl_t: 50, length_m: 150, initial: 'fast' },
        { id: 'LINE2', fairlead: 'aftPort', bollard: '', mbl_t: 60, length_m: 150, initial: 'stowed' },
      ],
    });
    expect(el.lastValidation.summary.berth?.definition?.['id']).toBe('MLO-OIL-3');
    // 岸壁
    await setChecked(el, 'input.banks-enabled', true);
    await setInput(el, 'input.banks-file', 'data/areas/mailiao-channel-banks.json');
    await setInput(el, 'input.banks-ids', 'MLC-N, MLC-S');
    await setInput(el, 'input.banks-scale', '0.5');
    expect(scenario(el)['banks']).toEqual({ file: 'data/areas/mailiao-channel-banks.json', ids: ['MLC-N', 'MLC-S'], scale: 0.5 });
    expect(el.lastValidation.issues).toEqual([]);
    expect(text(el)).toContain('拖船 2 · 錨 有 · 泊位 有 · 岸壁 有');
    // 刪除與取消
    await click(el, '.row.tug[data-index="0"] button.tg-remove');
    await click(el, '.row.line[data-index="1"] button.ln-remove');
    await setChecked(el, 'input.anchors-enabled', false);
    await setChecked(el, 'input.banks-enabled', false);
    expect((scenario(el)['tugs'] as RawSection[]).map((t) => t['id'])).toEqual(['TG2']);
    expect((scenario(el)['berth'] as RawSection)['lines']).toHaveLength(1);
    expect(scenario(el)['anchors']).toBeUndefined();
    expect(scenario(el)['banks']).toBeUndefined();
    await setChecked(el, 'input.berth-enabled', false);
    expect(scenario(el)['berth']).toBeUndefined();
  });

  it('驗證錯誤顯示:連接點格式錯誤不改草稿、拖船 id 重複、initial.mode 不在 modes、fast 無 bollard、岸壁無檔', async () => {
    const el = await mountWithTemplate();
    await setInput(el, '.row.tug[data-index="0"] input.tg-attach', 'middle');
    expect(text(el)).toContain('船體座標點需為');
    expect((scenario(el)['tugs'] as RawSection[])[0]!['attach']).toBe('fwdStbd');
    await click(el, 'button.add-tug');
    await setInput(el, '.row.tug[data-index="1"] input.tg-id', 'TG1');
    await setChecked(el, '.row.tug[data-index="1"] input.tg-pull', false);
    await setInput(el, '.row.tug[data-index="1"] select.tg-mode', 'pull');
    expect(text(el)).toContain('✖ tugs[1] id 重複:TG1');
    expect(text(el)).toContain('✖ tugs[1] initial.mode pull 不在 modes 內');
    await setChecked(el, 'input.berth-enabled', true);
    await click(el, 'button.add-line');
    await setInput(el, '.row.line[data-index="0"] select.ln-initial', 'fast');
    expect(text(el)).toContain('✖ berth.lines[0] LINE1 initial fast 需指定 bollard');
    await setChecked(el, 'input.banks-enabled', true);
    await setInput(el, 'input.banks-file', '');
    expect(text(el)).toContain('✖ banks 需 file(岸壁檔)或 lines');
    expect(q<HTMLButtonElement>(el, 'button.load').disabled).toBe(true);
  });
});

describe('草稿還原(含新區段)', () => {
  it('修改新區段後重新掛載還原;第十七輪版本 1 草稿升級後新區段由範本重建', async () => {
    const el = await mountWithTemplate();
    await click(el, 'button.add-zone');
    await click(el, 'button.add-tug');
    await setInput(el, 'input.al-offHeading_deg', '15');
    const stored = JSON.parse(localStorage.getItem(DRAFT_STORAGE_KEY)!) as ScenarioDraft;
    expect(stored.version).toBe(2);
    expect(stored.zones).toHaveLength(2);
    expect(stored.tugs).toHaveLength(2);
    el.remove();
    const again = await mountEditor();
    expect(again.value.zones).toHaveLength(2);
    expect(count(again, '.row.zone')).toBe(2);
    expect(count(again, '.row.tug')).toBe(2);
    expect(q<HTMLInputElement>(again, 'input.al-offHeading_deg').value).toBe('15');
    expect(text(again)).toContain('已自本機草稿還原');
    // 版本 1 草稿
    const v1: Record<string, unknown> = { ...stored, version: 1, general: { ...stored.general, name: '舊草稿' } };
    for (const k of ['route', 'zones', 'briefing', 'sensors', 'alerts', 'bnwas', 'tugs', 'anchors', 'berth', 'banks']) delete v1[k];
    v1['assessment'] = { hardFail: stored.assessment.hardFail, bands: stored.assessment.bands, start: 100, passMark: 70 };
    again.remove();
    localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v1));
    const migrated = await mountEditor();
    expect(migrated.value.version).toBe(2);
    expect(migrated.value.general.name).toBe('舊草稿');
    expect(migrated.value.zones).toHaveLength(1);
    expect(migrated.value.tugs).toHaveLength(1);
    expect(migrated.value.assessment.required).toHaveLength(1);
    expect(count(migrated, '.row.required')).toBe(1);
    expect(migrated.lastValidation.issues).toEqual([]);
  });
});
