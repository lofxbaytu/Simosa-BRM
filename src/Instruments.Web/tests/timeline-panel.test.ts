// 時間表面板與事件流(happy-dom):狀態由事件更新、手動觸發送 triggerTimeline、按住強制送 force、
// acknowledge 鈕送出正確 args、必要事件倒數;事件流篩選、點擊發出 event-seek、語音播放鈕。
// @vitest-environment happy-dom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { SimCommand } from '../src/types/command.js';
import type { SimEvent } from '../src/types/event.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import { parseScenario } from '../src/lib/scenario.js';
import { HOLD_MS, requiredCountdowns, timelineStatuses, type TimelinePanel } from '../src/components/instructor/timeline-panel.js';
import type { EventStream } from '../src/components/instructor/event-stream.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}, attrs: Record<string, string> = {}): Promise<T & Updatable> {
  const el = document.createElement(tag) as T & Updatable;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function shadowText(el: HTMLElement): string {
  return el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
}

/** shadow 內某個元素的文字 */
function innerText(el: Element): string {
  return el.textContent?.replace(/\s+/g, ' ') ?? '';
}

function q<T extends Element>(el: HTMLElement, sel: string): T {
  const found = el.shadowRoot!.querySelector<T>(sel);
  if (!found) throw new Error(`找不到 ${sel}`);
  return found;
}

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = parseScenario(readFileSync(path.resolve(here, 'fixtures/E07_steering_failure.yaml'), 'utf-8'), 'data/scenarios/E07_steering_failure.yaml');

function ev(kind: string, t: number, extra: Partial<SimEvent> = {}): SimEvent {
  return { kind, tick: Math.round(t / 0.02), t, source: 'timeline', visible: false, seq: Math.round(t), ...extra };
}

let cmds: SimCommand[] = [];
const onCommand = (e: Event) => cmds.push((e as CustomEvent<SimCommand>).detail);

beforeAll(async () => {
  await import('../src/components/instructor/timeline-panel.js');
  await import('../src/components/instructor/event-stream.js');
});

beforeEach(() => {
  cmds = [];
  document.body.addEventListener(COMMAND_EVENT, onCommand);
});

afterEach(() => {
  document.body.removeEventListener(COMMAND_EVENT, onCommand);
  document.body.innerHTML = '';
  vi.useRealTimers();
});

describe('timelineStatuses / requiredCountdowns', () => {
  it('觸發次數、停用、待觸發;once 已觸發者不再待觸發,every 達 max 亦然', () => {
    const events = [ev('timelineTriggered', 60, { itemId: 'pilotBoarded', data: { fireCount: 1 } }), ev('timelineDisarmed', 70, { itemId: 'vtsCall' }), ev('timelineTriggered', 100, { itemId: 'speedCoach' }), ev('timelineTriggered', 160, { itemId: 'speedCoach' }), ev('timelineTriggered', 220, { itemId: 'speedCoach' }), ev('timelineManual', 230, { itemId: 'steeringFail', source: 'instructor', data: { ok: false } })];
    const st = timelineStatuses(E07.timeline, events);
    const by = Object.fromEntries(st.map((s) => [s.item.id, s]));
    expect(by['pilotBoarded']).toMatchObject({ fireCount: 1, lastFiredAt_s: 60, armed: true, pending: false });
    expect(by['vtsCall']).toMatchObject({ fireCount: 0, armed: false, pending: false });
    expect(by['speedCoach']).toMatchObject({ fireCount: 3, pending: false });
    expect(by['steeringFail']).toMatchObject({ fireCount: 0, pending: true, rejected: true });
    expect(by['emergencySteering']).toMatchObject({ pending: true });
  });

  it('必要事件倒數:after 事件後計時;met/missed 事件更新;參數引用的 within_s 只顯示計時中', () => {
    const rules = E07.assessment;
    const inactive = requiredCountdowns(rules, [], 100);
    expect(inactive.every((c) => c.state === 'inactive')).toBe(true);
    const injected = ev('faultInjected', 420, { itemId: 'steeringFail', data: { fault: 'steeringGear' } });
    const counting = requiredCountdowns(rules, [injected], 450);
    const byId = Object.fromEntries(counting.map((c) => [c.rule.id, c]));
    // within_s 為參數引用(未解析)→ 計時中但無剩餘秒數;vtsReport 的 after 未發生
    expect(byId['steeringFirstAction']).toMatchObject({ startedAt_s: 420, within_s: null, state: 'counting', remaining_s: null });
    expect(byId['vtsReport']!.state).toBe('inactive');
    const later = requiredCountdowns(rules, [injected, ev('requiredEventMet', 455, { itemId: 'steeringFirstAction', source: 'assessment', data: { dt_s: 35, by: 'telegraph' } }), ev('requiredEventMissed', 540, { itemId: 'notifyEcr', source: 'assessment' })], 600);
    const l = Object.fromEntries(later.map((c) => [c.rule.id, c]));
    expect(l['steeringFirstAction']).toMatchObject({ state: 'met', by: 'telegraph' });
    expect(l['notifyEcr']!.state).toBe('missed');
    // 數值 within_s(vtsReport 120 s):通過報告點後倒數與逾時
    const report = ev('reportPointPassed', 1000, { source: 'assessment', data: { waypoint: 'WP3' } });
    expect(requiredCountdowns(rules, [report], 1030).find((c) => c.rule.id === 'vtsReport')).toMatchObject({ state: 'counting', remaining_s: 90 });
    expect(requiredCountdowns(rules, [report], 1150).find((c) => c.rule.id === 'vtsReport')).toMatchObject({ state: 'overdue', remaining_s: 30 });
  });
});

describe('時間表面板', () => {
  it('列出項目、條件與動作;manual 項目「觸發」送 triggerTimeline(無 force)', async () => {
    const el = await mount<TimelinePanel>('brm-timeline-panel', { timeline: E07.timeline, events: [], assessment: E07.assessment, simTime: 10 });
    const text = shadowText(el);
    expect(text).toContain('已觸發 0 · 待觸發 7');
    expect(text).toContain('steeringFail');
    expect(text).toContain('條件:航段 ≥ 1 且 時間 ≥ 90 s');
    expect(text).toContain('動作:注入故障 steeringGear');
    q<HTMLButtonElement>(el, '.item[data-item="emergencySteering"] button.trigger').click();
    expect(cmds.at(-1)).toEqual({ type: 'triggerTimeline', args: { id: 'emergencySteering' } });
    expect(el.shadowRoot!.querySelector('.item[data-item="steeringFail"] button.trigger')).toBeNull();
  });

  it('非 manual 項目需按住「強制」才送 force: true;提早放開不送', async () => {
    vi.useFakeTimers();
    const el = await mount<TimelinePanel>('brm-timeline-panel', { timeline: E07.timeline, events: [], assessment: null });
    const force = q<HTMLButtonElement>(el, '.item[data-item="steeringFail"] button.force');
    force.dispatchEvent(new Event('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS / 2);
    force.dispatchEvent(new Event('pointerup'));
    vi.advanceTimersByTime(HOLD_MS);
    expect(cmds).toEqual([]);
    force.dispatchEvent(new Event('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS + 10);
    expect(cmds.at(-1)).toEqual({ type: 'triggerTimeline', args: { id: 'steeringFail', force: true } });
  });

  it('狀態由事件更新;state.scenario 的計數優先;核心拒絕標示', async () => {
    const el = await mount<TimelinePanel>('brm-timeline-panel', { timeline: E07.timeline, events: [], assessment: null });
    el.events = [ev('timelineTriggered', 60, { itemId: 'pilotBoarded' }), ev('timelineManual', 70, { itemId: 'vtsCall', source: 'instructor', data: { ok: false } })];
    el.scenarioStatus = { id: 'E07_steering_failure', phase: 'degraded', timelineFired: 1, timelinePending: 2 };
    await el.updateComplete;
    const text = shadowText(el);
    // 待觸發 = 由事件推導的 armed 且未達 once/max 項目數(7 項 − pilotBoarded);核心的 timelinePending 是 delay_s 待執行動作數,另顯示
    expect(text).toContain('已觸發 1 · 待觸發 6 · 延遲動作 2');
    expect(text).toContain('階段 degraded');
    expect(innerText(q<HTMLElement>(el, '.item[data-item="pilotBoarded"]'))).toContain('已觸發 1 次 @01:00');
    expect(innerText(q<HTMLElement>(el, '.item[data-item="vtsCall"]'))).toContain('核心拒絕');
    expect(q<HTMLElement>(el, '.item[data-item="pilotBoarded"]').classList.contains('fired')).toBe(true);
  });

  it('acknowledge 快速鈕送出 kind/role/note/itemId', async () => {
    const el = await mount<TimelinePanel>('brm-timeline-panel', { timeline: E07.timeline, events: [ev('timelineTriggered', 420, { itemId: 'steeringFail' })], assessment: null });
    q<HTMLButtonElement>(el, 'button[data-ack="notifyEcr"]').click();
    expect(cmds.at(-1)).toEqual({ type: 'acknowledge', args: { kind: 'notifyEcr', role: 'officer' } });
    const role = q<HTMLSelectElement>(el, 'select.role');
    role.value = 'master';
    role.dispatchEvent(new Event('change'));
    const item = q<HTMLSelectElement>(el, 'select.ack-item');
    item.value = 'steeringFail';
    item.dispatchEvent(new Event('change'));
    const note = q<HTMLInputElement>(el, 'input.note');
    note.value = '已電話通知';
    note.dispatchEvent(new Event('input'));
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button[data-ack="steeringMode"]').click();
    expect(cmds.at(-1)).toEqual({ type: 'acknowledge', args: { kind: 'steeringMode', itemId: 'steeringFail', role: 'master', note: '已電話通知' } });
    await el.updateComplete;
    expect(q<HTMLInputElement>(el, 'input.note').value).toBe('');
  });

  it('事件清空(重設/換情境)後對應項目選擇一併清掉,之後的確認不再帶過期的 itemId', async () => {
    const el = await mount<TimelinePanel>('brm-timeline-panel', { timeline: E07.timeline, events: [ev('timelineTriggered', 420, { itemId: 'steeringFail' })], assessment: null });
    const item = q<HTMLSelectElement>(el, 'select.ack-item');
    item.value = 'steeringFail';
    item.dispatchEvent(new Event('change'));
    await el.updateComplete;
    q<HTMLButtonElement>(el, 'button[data-ack="verbal"]').click();
    expect(cmds.at(-1)).toEqual({ type: 'acknowledge', args: { kind: 'verbal', itemId: 'steeringFail', role: 'officer' } });
    el.events = [];
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('select.ack-item')).toBeNull();
    q<HTMLButtonElement>(el, 'button[data-ack="notifyEcr"]').click();
    expect(cmds.at(-1)).toEqual({ type: 'acknowledge', args: { kind: 'notifyEcr', role: 'officer' } });
    // 之後再觸發同一故障項目:選單重新出現且未預選
    el.events = [ev('timelineTriggered', 900, { itemId: 'steeringFail' })];
    await el.updateComplete;
    expect(q<HTMLSelectElement>(el, 'select.ack-item').value).toBe('');
  });

  it('必要事件倒數顯示剩餘/逾時/達成;compact 模式不列項目;核心不支援時停用按鈕', async () => {
    const report = ev('reportPointPassed', 1000, { source: 'assessment', data: { waypoint: 'WP3' } });
    const el = await mount<TimelinePanel>('brm-timeline-panel', { timeline: E07.timeline, events: [report], assessment: E07.assessment, simTime: 1030, compact: true });
    expect(el.shadowRoot!.querySelector('.item[data-item]')).toBeNull();
    const cd = q<HTMLElement>(el, '.countdown[data-rule="vtsReport"]');
    expect(innerText(cd)).toContain('剩 90 s');
    expect(cd.classList.contains('counting')).toBe(true);
    el.simTime = 1200;
    await el.updateComplete;
    expect(innerText(q<HTMLElement>(el, '.countdown[data-rule="vtsReport"]'))).toContain('逾時 80 s');
    el.events = [report, ev('requiredEventMet', 1100, { itemId: 'vtsReport', source: 'assessment', data: { dt_s: 100, by: 'acknowledge:report' } })];
    await el.updateComplete;
    expect(innerText(q<HTMLElement>(el, '.countdown[data-rule="vtsReport"]'))).toContain('達成(acknowledge:report)');
    el.unsupported = true;
    await el.updateComplete;
    expect(q<HTMLButtonElement>(el, 'button[data-ack="verbal"]').disabled).toBe(true);
    expect(shadowText(el)).toContain('核心不支援,僅顯示');
  });
});

describe('事件流', () => {
  it('列出事件(最新在上)、來源篩選、點擊發出 event-seek、語音播放鈕發出 voice-play', async () => {
    const events = [ev('cpaAlarm', 120, { source: 'traffic', targetId: 'T1', cpa_nm: 0.4 }), ev('voiceScript', 300, { itemId: 'ecrCheck', data: { role: 'ecr', channel: 'phone', text: '機控室' } }), ev('hardFail', 500, { source: 'assessment', itemId: 'grounding', data: { rule: 'grounding' } })];
    const el = await mount<EventStream>('brm-event-stream', { events, voiceButtons: true });
    const items = Array.from(el.shadowRoot!.querySelectorAll<HTMLElement>('.list .item'));
    expect(items.map((i) => i.dataset['kind'])).toEqual(['hardFail', 'voiceScript', 'cpaAlarm']);
    expect(items[0]!.classList.contains('alarm')).toBe(true);
    expect(shadowText(el)).toContain('目標船 1');
    const seeks: SimEvent[] = [];
    el.addEventListener('event-seek', (e) => seeks.push((e as CustomEvent<SimEvent>).detail));
    const plays: SimEvent[] = [];
    el.addEventListener('voice-play', (e) => plays.push((e as CustomEvent<SimEvent>).detail));
    items[2]!.click();
    expect(seeks[0]?.kind).toBe('cpaAlarm');
    q<HTMLButtonElement>(el, 'button.play-voice').click();
    expect(plays[0]?.kind).toBe('voiceScript');
    expect(seeks).toHaveLength(1); // 播放鈕不觸發跳轉
    q<HTMLButtonElement>(el, 'button.src-traffic').click();
    await el.updateComplete;
    expect(Array.from(el.shadowRoot!.querySelectorAll<HTMLElement>('.list .item')).map((i) => i.dataset['kind'])).toEqual(['hardFail', 'voiceScript']);
  });
});
