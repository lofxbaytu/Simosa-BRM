// 元件煙霧測試(happy-dom):確認各 Lit 元件可渲染、屬性會反映到畫面、操船台會送出符合契約的指令事件。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import { defaultState } from '../src/lib/state-parser.js';
import { getShipConfig } from '../src/lib/ship-config.js';
import { COMMAND_EVENT } from '../src/components/shared.js';

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}): Promise<T> {
  const el = document.createElement(tag) as T & { updateComplete: Promise<unknown> };
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function shadowText(el: HTMLElement): string {
  return el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
}

function captureCommands(): SimCommand[] {
  const cmds: SimCommand[] = [];
  document.body.addEventListener(COMMAND_EVENT, (ev) => cmds.push((ev as CustomEvent<SimCommand>).detail));
  return cmds;
}

beforeAll(async () => {
  await import('../src/components/conning-bar.js');
  await import('../src/components/helm-wheel.js');
  await import('../src/components/telegraph-control.js');
  await import('../src/components/thruster-lever.js');
  await import('../src/components/autopilot-panel.js');
  await import('../src/components/instructor-controls.js');
});

describe('conning 顯示元件', () => {
  it('航向顯示三位數與設定航向', async () => {
    const el = await mount('brm-heading', { heading: 7.44, setHeading: 275, cog: 10 });
    const text = shadowText(el);
    expect(text).toContain('007');
    expect(text).toContain('007.4°');
    expect(text).toContain('設定 275');
    expect(text).toContain('COG 010');
    expect(el.shadowRoot?.querySelector('polygon.bug')).not.toBeNull();
  });
  it('舵角指示器顯示舵角與舵令,量程依船', async () => {
    const el = await mount('brm-rudder', { rudder: -12.4, order: -15, max: 70, normalMax: 35 });
    const text = shadowText(el);
    expect(text).toContain('舵角 ±70°');
    expect(text).toContain('P 12°');
    expect(text).toContain('P 15°');
  });
  it('UKC 低於門檻時標示警報', async () => {
    const el = await mount('brm-depth', { ukc: 0.6, waterDepth: 7, alarmBelow: 1 });
    expect(el.shadowRoot?.querySelector('.main.alarm')).not.toBeNull();
    const ok = await mount('brm-depth', { ukc: 5, waterDepth: 12 });
    expect(ok.shadowRoot?.querySelector('.main.alarm')).toBeNull();
  });
  it('主機讀數顯示倒車負值與車鐘', async () => {
    const el = await mount('brm-engine', { rpm: -80, rpmOrder: -80, rpmMax: 180, telegraph: 'SAS', telegraphLabel: '慢速倒車', engineState: 'running' });
    const text = shadowText(el);
    expect(text).toContain('-80');
    expect(text).toContain('SAS');
    expect(text).toContain('慢速倒車');
    expect(text).toContain('運轉中');
  });
  it('整個 conning 條可用完整狀態渲染', async () => {
    const state = { ...defaultState('FSB2'), heading: 123.4, stw: 9.9, rpm: 93, telegraph: 'HAH' as const };
    const el = await mount('brm-conning-bar', { state, config: getShipConfig('FSB2'), issues: [] });
    expect(el.shadowRoot?.querySelectorAll('brm-heading, brm-rot, brm-speed, brm-rudder, brm-engine, brm-thruster, brm-depth, brm-wind, brm-current, brm-clock').length).toBe(10);
    const engine = el.shadowRoot?.querySelector('brm-engine') as HTMLElement & { updateComplete: Promise<unknown> };
    await engine.updateComplete;
    expect(shadowText(engine)).toContain('半速前進');
  });
});

describe('操船台元件送出的指令', () => {
  it('車鐘按鈕送出 telegraph 指令', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-telegraph', { steps: getShipConfig('FSB1').telegraph, position: 'STOP' });
    const buttons = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('button'));
    expect(buttons).toHaveLength(11);
    expect(buttons[0]!.textContent).toContain('NAVF');
    expect(buttons[10]!.textContent).toContain('EFAS');
    const hah = buttons.find((b) => b.textContent?.includes('HAH'))!;
    hah.click();
    expect(cmds.at(-1)).toEqual({ type: 'telegraph', value: 'HAH' });
  });
  it('舵輪鍵盤與快速鍵送出 rudder 指令並限幅', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-helm', { order: 0, rudder: 0, max: 35, normalMax: 35, rate: 3 });
    const wheel = el.shadowRoot!.querySelector('.wheel') as HTMLElement;
    wheel.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', shiftKey: true, bubbles: true }));
    expect(cmds.at(-1)).toEqual({ type: 'rudder', value: 5, args: { mode: 'FU' } });
    const presets = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.presets button'));
    presets[0]!.click();
    expect(cmds.at(-1)).toEqual({ type: 'rudder', value: -35, args: { mode: 'FU' } });
    presets[4]!.click();
    expect(cmds.at(-1)).toEqual({ type: 'rudder', value: 0, args: { mode: 'FU' } });
  });
  it('自動舵開啟時舵輪鎖定', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-helm', { order: 0, rudder: 0, max: 35, normalMax: 35, autopilot: true });
    const before = cmds.length;
    const wheel = el.shadowRoot!.querySelector('.wheel') as HTMLElement;
    wheel.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    expect(cmds.length).toBe(before);
    expect(shadowText(el)).toContain('AUTO');
  });
  it('側推快速鍵送出 thruster 指令', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-thruster-lever', { order: 0, actual: 0 });
    const buttons = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.quick button'));
    buttons.find((b) => b.textContent?.includes('P 100'))!.click();
    expect(cmds.at(-1)).toEqual({ type: 'thruster', value: -1 });
    buttons.find((b) => b.textContent?.includes('停止'))!.click();
    expect(cmds.at(-1)).toEqual({ type: 'thruster', value: 0 });
  });
  it('自動舵面板開啟時以現在航向為設定值並送 autopilot 指令', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-autopilot', { heading: 274.6 });
    const toggle = el.shadowRoot!.querySelector('.toggle') as HTMLButtonElement;
    toggle.click();
    expect(cmds.at(-1)).toEqual({ type: 'autopilot', args: { enabled: true, heading: 275, rotLimit: 15 } });
    const plus10 = Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.set button')).find((b) => b.textContent?.trim() === '+10')!;
    plus10.click();
    expect(cmds.at(-1)).toEqual({ type: 'autopilot', args: { enabled: true, heading: 285, rotLimit: 15 } });
    toggle.click();
    expect(cmds.at(-1)).toEqual({ type: 'autopilot', args: { enabled: false, heading: 285, rotLimit: 15 } });
  });
  it('教官按鈕:凍結、恢復,重設需按兩次', async () => {
    const cmds = captureCommands();
    const el = await mount('brm-instructor', { frozen: false });
    const buttons = () => Array.from(el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.buttons button'));
    buttons()[0]!.click();
    expect(cmds.at(-1)).toEqual({ type: 'freeze' });
    const before = cmds.length;
    buttons()[2]!.click();
    expect(cmds.length).toBe(before);
    await (el as HTMLElement & { updateComplete: Promise<unknown> }).updateComplete;
    expect(buttons()[2]!.textContent).toContain('確認');
    buttons()[2]!.click();
    expect(cmds.at(-1)).toEqual({ type: 'reset' });
  });
});
