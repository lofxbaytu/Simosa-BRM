// 簡化教官模式「靠離泊預演」表單與流程(happy-dom):表單組題 → rehearsal-compose 事件 → 教官站以 loadScenario args.scenario 送核心並進入簡報;
// 預覽/錯誤顯示;loadScenarioInlineCommand 形狀。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { BerthingRehearsalPanel } from '../src/components/instructor/berthing-rehearsal-panel.js';
import type { BerthingRehearsalResult } from '../src/lib/berthing-rehearsal.js';
import { loadScenarioInlineCommand } from '../src/lib/instructor-command.js';
import { serializeCommand } from '../src/lib/command.js';
import type { CommandLogEntry } from '../src/lib/session-record.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };

async function mountPanel(): Promise<BerthingRehearsalPanel & El> {
  const el = document.createElement('brm-berthing-rehearsal-panel') as BerthingRehearsalPanel & El;
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

async function mountApp(query = '/?demo=1&mode=simple'): Promise<El> {
  history.replaceState(null, '', query);
  const el = document.createElement('brm-instructor-app') as El;
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function setInput(el: El, selector: string, value: string): void {
  const input = el.shadowRoot!.querySelector<HTMLInputElement | HTMLSelectElement>(selector);
  if (!input) throw new Error(`缺欄位 ${selector}`);
  input.value = value;
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

beforeAll(async () => {
  await import('../src/components/instructor/berthing-rehearsal-panel.js');
  await import('../src/instructor-app.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('loadScenarioInlineCommand', () => {
  it('只帶 args.id 與 args.scenario(無 value/path;兩核心以 args.scenario 為準)', () => {
    const cmd = loadScenarioInlineCommand('BR_x', { id: 'BR_x', ship: { id: 'FSB1' } });
    expect(cmd).toEqual({ type: 'loadScenario', args: { id: 'BR_x', scenario: { id: 'BR_x', ship: { id: 'FSB1' } } } });
    expect(serializeCommand(cmd)).toBe('{"type":"loadScenario","args":{"id":"BR_x","scenario":{"id":"BR_x","ship":{"id":"FSB1"}}}}');
  });
});

describe('靠離泊預演表單', () => {
  it('預設值組題成功並發 rehearsal-compose 事件,內容為自我驗證通過的情境', async () => {
    const panel = await mountPanel();
    const events: BerthingRehearsalResult[] = [];
    panel.addEventListener('rehearsal-compose', (ev) => events.push((ev as CustomEvent<BerthingRehearsalResult>).detail));
    expect(panel.shadowRoot!.querySelector('select.berth')).not.toBeNull();
    expect(panel.shadowRoot!.textContent).toContain('碼頭線 085°');
    panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await panel.updateComplete;
    expect(events).toHaveLength(1);
    const r = events[0]!;
    expect(r.summary.issues).toEqual([]);
    expect(r.id).toMatch(/^BR_berthing_MLO-OIL-3_/);
    expect(r.scenario['id']).toBe(r.id);
    expect(panel.lastResult).toBe(r);
    expect(panel.shadowRoot!.textContent).toContain('自我驗證通過');
  });

  it('改泊位帶入預設基底水深;改作業/拖船/引水人反映在組題;風變化勾選後加入 windShift', async () => {
    const panel = await mountPanel();
    setInput(panel, 'select.berth', 'MLO-SOB-1');
    await panel.updateComplete;
    expect(panel.value.berthId).toBe('MLO-SOB-1');
    expect(panel.value.baseDepth_m).toBe(11);
    expect(panel.shadowRoot!.textContent).toContain('碼頭線 000°');
    setInput(panel, 'select[class=""], .form > select:nth-of-type(1)', 'unberthing');
    setInput(panel, 'select.tugs', '0');
    setInput(panel, 'input.tide', '0.8');
    setInput(panel, 'input.planned', '2026-11-14T06:00');
    const pilot = panel.shadowRoot!.querySelector<HTMLInputElement>('input.pilot')!;
    pilot.checked = false;
    pilot.dispatchEvent(new Event('change', { bubbles: true }));
    const wsBox = panel.shadowRoot!.querySelector<HTMLInputElement>('input.wind-shift')!;
    wsBox.checked = true;
    wsBox.dispatchEvent(new Event('change', { bubbles: true }));
    await panel.updateComplete;
    setInput(panel, 'input.ws-after', '300');
    await panel.updateComplete;
    const r = panel.compose()!;
    expect(r).not.toBeNull();
    expect(r.id).toBe('BR_unberthing_MLO-SOB-1_20261114-0600');
    expect(r.summary.environment.waterDepth).toBeCloseTo(11.8, 6);
    expect(r.summary.tugs).toBeUndefined();
    expect(r.summary.timeline.some((i) => i.id === 'windShift')).toBe(true);
    expect(r.summary.briefingDoc.roles.some((x) => x.role === 'pilot')).toBe(false);
    expect(r.summary.issues).toEqual([]);
  });

  it('初始 UKC ≤ 0(FSB1 壓載、MLO-SOB-1、基底 4.3 m + 潮高 1.0 m)時顯示錯誤、不發事件;UKC 低於下限時顯示警告仍可載入', async () => {
    const panel = await mountPanel();
    const events: unknown[] = [];
    panel.addEventListener('rehearsal-compose', (ev) => events.push(ev));
    setInput(panel, 'select[class=""], .form > select:nth-of-type(1)', 'unberthing');
    setInput(panel, '.form > select:nth-of-type(4)', 'ballast');
    setInput(panel, 'select.berth', 'MLO-SOB-1');
    await panel.updateComplete;
    setInput(panel, 'input.depth', '4.3');
    setInput(panel, 'input.tide', '1.0');
    await panel.updateComplete;
    panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await panel.updateComplete;
    expect(events).toHaveLength(0);
    expect(panel.lastResult).toBeNull();
    expect(panel.shadowRoot!.querySelector('.error')!.textContent).toContain('初始 UKC -0.11 m ≤ 0');
    setInput(panel, 'input.depth', '5.5');
    setInput(panel, 'input.tide', '0.5');
    await panel.updateComplete;
    panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await panel.updateComplete;
    expect(events).toHaveLength(1);
    expect(panel.shadowRoot!.querySelector('.error')).toBeNull();
    expect(panel.shadowRoot!.querySelector('.warning')!.textContent).toContain('初始 UKC 0.59 m 低於公司 UKC 下限 1 m');
    expect(panel.shadowRoot!.textContent).toContain('UKC 約 0.59 m');
  });

  it('預定時刻無法解析時顯示錯誤、不發事件', async () => {
    const panel = await mountPanel();
    const events: unknown[] = [];
    panel.addEventListener('rehearsal-compose', (ev) => events.push(ev));
    setInput(panel, 'input.planned', 'not a time');
    await panel.updateComplete;
    panel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await panel.updateComplete;
    expect(events).toHaveLength(0);
    expect(panel.lastResult).toBeNull();
    expect(panel.shadowRoot!.querySelector('.error')!.textContent).toContain('預定時刻無法解析');
  });
});

describe('簡化教官模式流程', () => {
  it('選擇練習頁可切到「靠離泊預演」,組題並載入後進入簡報、送出 loadScenario args.scenario、可下載 YAML', async () => {
    const app = await mountApp();
    const tab = Array.from(app.shadowRoot!.querySelectorAll<HTMLButtonElement>('.select-tab button')).find((b) => b.textContent?.trim() === '靠離泊預演')!;
    expect(tab).toBeDefined();
    expect(app.shadowRoot!.querySelector('brm-berthing-rehearsal-panel')).toBeNull();
    tab.click();
    await app.updateComplete;
    const panel = app.shadowRoot!.querySelector('brm-berthing-rehearsal-panel') as (BerthingRehearsalPanel & El) | null;
    expect(panel).not.toBeNull();
    expect(app.shadowRoot!.querySelector('brm-scenario-panel')).toBeNull();
    await panel!.updateComplete;
    panel!.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await app.updateComplete;
    expect(app.shadowRoot!.querySelector('.stage.briefing')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('.stage.briefing')!.textContent).toContain('靠泊預演');
    expect(app.shadowRoot!.querySelector('.stage.briefing')!.textContent).toContain('引水人在船');
    expect(app.shadowRoot!.querySelector('button.download-yaml')).not.toBeNull();
    // 簡化教官模式不顯示指令紀錄面板:直接讀教官站的指令紀錄狀態
    const entries = (app as unknown as { commandLog: CommandLogEntry[] }).commandLog;
    const load = entries.find((e) => e.command.type === 'loadScenario')!;
    expect(load).toBeDefined();
    expect(load.command.value).toBeUndefined();
    expect(load.command.args?.['id']).toMatch(/^BR_berthing_MLO-OIL-3_/);
    expect((load.command.args?.['scenario'] as Record<string, unknown>)['ship']).toEqual({ id: 'FSB1', loading: 'full' });
    expect(load.ack?.ok).toBe(true);
    // 示範核心以情境重建:船為 FSB1、位置在泊位水側
    app.shadowRoot!.querySelector<HTMLButtonElement>('button.start')!.click();
    await app.updateComplete;
    expect(app.shadowRoot!.querySelector('.stage.running')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('.stage.running')!.textContent).toContain('BR_berthing_MLO-OIL-3');
    expect(app.shadowRoot!.querySelector('.stage.running brm-tug-panel')).not.toBeNull();
  });

  it('載入檔案情境時沒有「下載情境 YAML」鈕', async () => {
    const app = await mountApp();
    const scenarioPanel = app.shadowRoot!.querySelector('brm-scenario-panel') as El;
    scenarioPanel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await app.updateComplete;
    expect(app.shadowRoot!.querySelector('.stage.briefing')).not.toBeNull();
    expect(app.shadowRoot!.querySelector('button.download-yaml')).toBeNull();
  });
});
