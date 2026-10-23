// 教官站的參與者與休息時間確認(happy-dom;規劃書第 5.5 節):面板送出 sessionInfo 指令與事件、驗證提示、localStorage 草稿;
// 執行控制面板與標題列的未確認警示(不阻擋);簡化教官模式簡報頁的面板與開始鈕旁警示;教官站紀錄含 sessionInfo。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import type { SessionInfo } from '../src/lib/session-info.js';
import type { SessionInfoPanel } from '../src/components/instructor/session-info-panel.js';
import { loadSessionInfoDraft, SESSION_INFO_DRAFT_KEY, saveSessionInfoDraft } from '../src/components/instructor/session-info-panel.js';
import type { RunControlPanel } from '../src/components/instructor/run-control-panel.js';
import { isSessionRecord } from '../src/lib/session-record.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}, attrs: Record<string, string> = {}): Promise<T & Updatable> {
  const el = document.createElement(tag) as T & Updatable;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function q<T extends Element>(el: HTMLElement, sel: string): T {
  const found = el.shadowRoot!.querySelector<T>(sel);
  if (!found) throw new Error(`找不到 ${sel}`);
  return found;
}

function type(el: HTMLElement, sel: string, value: string): void {
  const input = q<HTMLInputElement>(el, sel);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

function select(el: HTMLElement, sel: string, value: string): void {
  const input = q<HTMLSelectElement>(el, sel);
  input.value = value;
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

beforeAll(async () => {
  await import('../src/components/instructor/session-info-panel.js');
  await import('../src/components/instructor/run-control-panel.js');
  await import('../src/instructor-app.js');
});

afterEach(() => {
  document.body.innerHTML = '';
  localStorage.removeItem(SESSION_INFO_DRAFT_KEY);
});

describe('參與者與休息時間確認面板', () => {
  it('填參與者與確認人後送出:sim-command 為 sessionInfo(確認時刻取 simUtc)、發 session-info-submitted、草稿存 localStorage', async () => {
    const cmds: SimCommand[] = [];
    document.body.addEventListener(COMMAND_EVENT, (ev) => cmds.push((ev as CustomEvent<SimCommand>).detail));
    const submitted: SessionInfo[] = [];
    document.body.addEventListener('session-info-submitted', (ev) => submitted.push((ev as CustomEvent<SessionInfo>).detail));
    const el = await mount<SessionInfoPanel>('brm-session-info-panel', { simUtc: '2026-10-17T02:00:00Z' });
    expect(el.shadowRoot!.querySelectorAll('tbody tr')).toHaveLength(1);
    type(el, 'tbody tr:nth-child(1) .p-name', '王大明');
    type(el, 'tbody tr:nth-child(1) .p-rank', '船長');
    type(el, 'tbody tr:nth-child(1) .p-cert', 'TW-12345');
    q<HTMLButtonElement>(el, '.add-participant').click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelectorAll('tbody tr')).toHaveLength(2);
    type(el, 'tbody tr:nth-child(2) .p-name', '李小華');
    select(el, 'tbody tr:nth-child(2) .p-role', 'oow');
    // 先不填確認人 → 提示、不送
    q<HTMLButtonElement>(el, 'button.submit').click();
    await el.updateComplete;
    expect(cmds).toHaveLength(0);
    expect(el.shadowRoot!.textContent).toContain('未填休息時間確認人');
    type(el, '.rest-by', '王大明');
    type(el, '.rest-note', '已對照休息時間紀錄');
    type(el, '.course-id', 'BRM-FSB1-2026-01');
    await el.updateComplete;
    expect(el.shadowRoot!.textContent).not.toContain('未填休息時間確認人');
    q<HTMLButtonElement>(el, 'button.submit').click();
    await el.updateComplete;
    expect(cmds).toHaveLength(1);
    expect(cmds[0]).toEqual({
      type: 'sessionInfo',
      args: {
        participants: [
          { name: '王大明', role: 'master', rank: '船長', certificateNo: 'TW-12345' },
          { name: '李小華', role: 'oow' },
        ],
        restHoursConfirmation: { confirmedBy: '王大明', confirmedAtUtc: '2026-10-17T02:00:00.000Z', compliant: true, note: '已對照休息時間紀錄' },
        course: { id: 'BRM-FSB1-2026-01' },
      },
    });
    expect(submitted[0]).toEqual(cmds[0]!.args);
    const draft = loadSessionInfoDraft();
    expect(draft.participants.map((p) => p.name)).toEqual(['王大明', '李小華']);
    expect(draft.restHours.confirmedBy).toBe('王大明');
    // 送出後面板標示已送出
    el.submitted = submitted[0]!;
    await el.updateComplete;
    expect(q(el, '.status').textContent).toContain('已送出:王大明');
    // 不符合 + 無 simUtc → 瀏覽器 UTC
    const el2 = await mount<SessionInfoPanel>('brm-session-info-panel');
    expect(el2.shadowRoot!.querySelectorAll('tbody tr')).toHaveLength(2); // 草稿還原
    const chk = q<HTMLInputElement>(el2, '.rest-compliant');
    chk.checked = false;
    chk.dispatchEvent(new Event('change', { bubbles: true }));
    q<HTMLButtonElement>(el2, 'button.submit').click();
    const last = cmds.at(-1)!.args as { restHoursConfirmation: { compliant: boolean; confirmedAtUtc: string } };
    expect(last.restHoursConfirmation.compliant).toBe(false);
    expect(Math.abs(Date.now() - new Date(last.restHoursConfirmation.confirmedAtUtc).getTime())).toBeLessThan(10_000);
  });

  it('草稿讀寫:損壞內容回空草稿;無 storage 不丟例外;清除後發 session-info-cleared', async () => {
    localStorage.setItem(SESSION_INFO_DRAFT_KEY, '{bad json');
    expect(loadSessionInfoDraft().participants).toEqual([]);
    expect(loadSessionInfoDraft(null).participants).toEqual([]);
    saveSessionInfoDraft({ participants: [{ name: 'A', role: 'oow' }], restHours: { confirmedBy: 'B', compliant: false, note: '' }, course: { id: '', title: '', instructor: '' } });
    expect(loadSessionInfoDraft()).toEqual({ participants: [{ name: 'A', role: 'oow', rank: '', certificateNo: '' }], restHours: { confirmedBy: 'B', compliant: false, note: '' }, course: { id: '', title: '', instructor: '' } });
    expect(() => saveSessionInfoDraft(loadSessionInfoDraft(), null)).not.toThrow();
    const el = await mount<SessionInfoPanel>('brm-session-info-panel');
    let cleared = 0;
    el.addEventListener('session-info-cleared', () => cleared++);
    q<HTMLButtonElement>(el, 'button.clear').click();
    await el.updateComplete;
    expect(cleared).toBe(1);
    expect(loadSessionInfoDraft().restHours.confirmedBy).toBe('');
  });
});

describe('未確認警示(不阻擋)', () => {
  it('執行控制面板:restHoursConfirmed=false 顯示警示、true/null 不顯示;凍結/恢復鈕不受影響', async () => {
    const el = await mount<RunControlPanel>('brm-run-control', { restHoursConfirmed: false });
    expect(q(el, '.rest-warning').textContent).toContain('不阻擋練習');
    expect(q<HTMLButtonElement>(el, 'button.freeze').disabled).toBe(false);
    el.restHoursConfirmed = true;
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.rest-warning')).toBeNull();
    el.restHoursConfirmed = null;
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.rest-warning')).toBeNull();
  });

  it('完整教官站(示範模式):標題列與執行控制有警示;面板送出後警示消失、指令進教官指令紀錄、下載紀錄含 sessionInfo', async () => {
    history.replaceState(null, '', '/?demo=1');
    const app = await mount<HTMLElement>('brm-instructor-app');
    expect(app.shadowRoot!.querySelector('header .rest-warning')!.textContent).toBe('休息時間未確認');
    const run = app.shadowRoot!.querySelector('brm-run-control') as RunControlPanel & Updatable;
    await run.updateComplete;
    expect(run.restHoursConfirmed).toBe(false);
    expect(run.shadowRoot!.querySelector('.rest-warning')).not.toBeNull();
    const panel = app.shadowRoot!.querySelector('.bottom brm-session-info-panel') as SessionInfoPanel & Updatable;
    expect(panel).not.toBeNull();
    await panel.updateComplete;
    type(panel, 'tbody tr:nth-child(1) .p-name', '王大明');
    type(panel, '.rest-by', '王大明');
    q<HTMLButtonElement>(panel, 'button.submit').click();
    await app.updateComplete;
    await run.updateComplete;
    expect(app.shadowRoot!.querySelector('header .rest-warning')).toBeNull();
    expect(run.restHoursConfirmed).toBe(true);
    expect(run.shadowRoot!.querySelector('.rest-warning')).toBeNull();
    const commandLog = app.shadowRoot!.querySelector('brm-command-log') as HTMLElement & { entries: ReadonlyArray<{ command: SimCommand }> };
    expect(commandLog.entries.at(-1)!.command.type).toBe('sessionInfo');
    const json = (app as unknown as { sessionRecordJson(): string }).sessionRecordJson();
    const record = JSON.parse(json) as { version: number; sessionInfo: SessionInfo | null };
    expect(isSessionRecord(record)).toBe(true);
    expect(record.version).toBe(3);
    expect(record.sessionInfo!.participants).toEqual([{ name: '王大明', role: 'master' }]);
    expect(record.sessionInfo!.restHoursConfirmation!.confirmedBy).toBe('王大明');
    // 清除後紀錄不再含 sessionInfo
    q<HTMLButtonElement>(panel, 'button.clear').click();
    await app.updateComplete;
    expect((JSON.parse((app as unknown as { sessionRecordJson(): string }).sessionRecordJson()) as { sessionInfo: unknown }).sessionInfo).toBeNull();
    expect(app.shadowRoot!.querySelector('header .rest-warning')).not.toBeNull();
  });

  it('簡化教官模式簡報頁:有精簡面板與開始鈕旁警示;未確認仍可開始;確認後警示改為已確認', async () => {
    history.replaceState(null, '', '/?demo=1&mode=simple');
    const app = await mount<HTMLElement>('brm-instructor-app');
    const scenarioPanel = app.shadowRoot!.querySelector('brm-scenario-panel') as Updatable;
    scenarioPanel.shadowRoot!.querySelector<HTMLButtonElement>('button.load')!.click();
    await app.updateComplete;
    const briefing = app.shadowRoot!.querySelector('.stage.briefing')!;
    const panel = briefing.querySelector('brm-session-info-panel') as SessionInfoPanel & Updatable;
    expect(panel).not.toBeNull();
    expect(panel.hasAttribute('compact')).toBe(true);
    expect(briefing.querySelector('.actions .rest-warning')!.textContent).toContain('尚未送出休息時間確認');
    expect(briefing.querySelector<HTMLButtonElement>('button.start')!.disabled).toBe(false);
    await panel.updateComplete;
    type(panel, 'tbody tr:nth-child(1) .p-name', '船長甲');
    type(panel, '.rest-by', '船長甲');
    q<HTMLButtonElement>(panel, 'button.submit').click();
    await app.updateComplete;
    const actions = app.shadowRoot!.querySelector('.stage.briefing .actions')!;
    expect(actions.querySelector('.rest-warning')).toBeNull();
    expect(actions.querySelector('.rest-ok')!.textContent).toContain('休息時間已由 船長甲 確認(符合)');
    actions.querySelector<HTMLButtonElement>('button.start')!.click();
    await app.updateComplete;
    expect(app.shadowRoot!.querySelector('.stage.running')).not.toBeNull();
    // 結束後的紀錄含 sessionInfo
    const record = JSON.parse((app as unknown as { sessionRecordJson(): string }).sessionRecordJson()) as { mode: string; sessionInfo: SessionInfo | null };
    expect(record.mode).toBe('simple');
    expect(record.sessionInfo!.restHoursConfirmation!.confirmedBy).toBe('船長甲');
  });
});
