// 浪與船間交互作用的儀表/教官站連動(docs/design/physics-waves.md 第 5 節與「練習中變更」、physics-ship-interaction.md 第 5 節;需求 R1/R2/R3):
// state-parser 解析 waves / shipInteraction、setEnvironment waves 指令形狀(建立/更新/關閉/漸變)、操船台 SEA 方塊(Hs/Tp/來向、交互作用超限警示色)
// 與 conning 條只在有欄位時放方塊、教官站環境面板的浪設定/關閉與目前值、事件列的環境改變文字。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import type { OwnShipState } from '../src/types/state.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import { defaultState, parseState } from '../src/lib/state-parser.js';
import { clearWavesCommand, setEnvironmentCommand, setWavesCommand, waveArgs, ENVIRONMENT_FIELD_SUPPORT } from '../src/lib/instructor-command.js';
import { eventLabel, parseSimEvent } from '../src/lib/sim-event.js';
import { formatSignedTonnes, formatWavesSummary } from '../src/components/sea-readout.js';
import { getShipConfig } from '../src/lib/ship-config.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}): Promise<T & Updatable> {
  const el = document.createElement(tag) as T & Updatable;
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

function q<T extends Element>(el: HTMLElement, sel: string): T {
  const found = el.shadowRoot!.querySelector<T>(sel);
  if (!found) throw new Error(`找不到 ${sel}`);
  return found;
}

function setInput(el: HTMLElement, sel: string, value: string): void {
  const input = q<HTMLInputElement>(el, sel);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

const WAVES = { significantHeight_m: 2.0, peakPeriod_s: 7.0, direction_deg: 90, wavelength_m: 76.5, spectrum: 'pm', X_kN: -12.3, Y_kN: -145.6, N_kNm: 850.2 };
const INTERACTION = { active: true, targetId: 'OVT', forceSurge_t: 0.4, forceSway_t: -12.34, momentYaw_tm: 850.6, xi: 0.3, eta: 0.45, targets: 1, event: true };

beforeAll(async () => {
  await import('../src/components/sea-readout.js');
  await import('../src/components/conning-bar.js');
  await import('../src/components/instructor/environment-panel.js');
});

describe('state-parser:waves 與 shipInteraction', () => {
  const base = { ...defaultState('FSB1'), t: 1, tick: 50 };
  it('有欄位時解析(來向正規化、浪譜小寫),缺省時不出現且不繼承前一幀', () => {
    const r = parseState({ ...base, waves: { ...WAVES, direction_deg: 450, spectrum: 'JONSWAP' }, shipInteraction: INTERACTION })!;
    expect(r.issues).toEqual([]);
    expect(r.state.waves).toEqual({ ...WAVES, direction_deg: 90, spectrum: 'jonswap' });
    expect(r.state.shipInteraction).toEqual(INTERACTION);
    const next = parseState({ ...base }, r.state)!;
    expect(next.issues).toEqual([]);
    expect('waves' in next.state).toBe(false);
    expect('shipInteraction' in next.state).toBe(false);
    const minimal = parseState({ ...base, waves: { significantHeight_m: 0, peakPeriod_s: 7, direction_deg: 0, wavelength_m: 76.5 }, shipInteraction: { active: false, forceSway_t: 0, momentYaw_tm: 0 } })!;
    expect(minimal.issues).toEqual([]);
    expect(minimal.state.waves).toEqual({ significantHeight_m: 0, peakPeriod_s: 7, direction_deg: 0, wavelength_m: 76.5 });
    expect(minimal.state.shipInteraction).toEqual({ active: false, forceSway_t: 0, momentYaw_tm: 0 });
  });
  it('型別錯誤記入 issues 但不影響其他欄位', () => {
    const r = parseState({ ...base, waves: { significantHeight_m: 'big', peakPeriod_s: 7, direction_deg: 90, wavelength_m: 80, spectrum: 'bretschneider', Y_kN: 'x' }, shipInteraction: { active: 'yes', forceSway_t: 1, momentYaw_tm: 2, event: 1 } })!;
    expect(r.issues).toEqual(['waves.significantHeight_m', 'waves.spectrum', 'waves.Y_kN', 'shipInteraction.active', 'shipInteraction.event']);
    expect(r.state.waves).toEqual({ significantHeight_m: 0, peakPeriod_s: 7, direction_deg: 90, wavelength_m: 80 });
    expect(r.state.shipInteraction).toEqual({ active: false, forceSway_t: 1, momentYaw_tm: 2 });
    expect(r.state.wind).toEqual(base.wind);
    const bad = parseState({ ...base, waves: 'rough', shipInteraction: [1] })!;
    expect(bad.issues).toEqual(['waves', 'shipInteraction']);
    expect(bad.state.waves).toBeUndefined();
  });
});

describe('setEnvironment waves 指令形狀', () => {
  it('建立/更新:Hs 不為負、Tp 為正、來向正規化整數度、浪譜小寫、γ ≥ 1;不合規欄位略過', () => {
    expect(setEnvironmentCommand({ waves: { significantHeight_m: 2.456, peakPeriod_s: 7.44, direction_deg: 450, spectrum: 'JONSWAP ', gamma: 3.3 } })).toEqual({
      type: 'setEnvironment',
      args: { waves: { significantHeight_m: 2.46, peakPeriod_s: 7.4, direction_deg: 90, spectrum: 'jonswap', gamma: 3.3 } },
    });
    expect(waveArgs({ significantHeight_m: -1, peakPeriod_s: 0, spectrum: 'bretschneider', gamma: 0.5 })).toEqual({ significantHeight_m: 0 });
    expect(waveArgs({ peakPeriod_s: Number.NaN })).toBeNull();
    // 部分更新:只送有給的欄位
    expect(setWavesCommand({ direction_deg: 180 })).toEqual({ type: 'setEnvironment', args: { waves: { direction_deg: 180 } } });
    expect(setWavesCommand({ significantHeight_m: 3 }, 120)).toEqual({ type: 'setEnvironment', args: { waves: { significantHeight_m: 3 }, ramp_s: 120 } });
    expect(setWavesCommand({ spectrum: 'x' })).toBeNull();
    // 關閉:waves: null(漸變秒數附上時 Hs 先漸變到 0)
    expect(clearWavesCommand()).toEqual({ type: 'setEnvironment', args: { waves: null } });
    expect(clearWavesCommand(60)).toEqual({ type: 'setEnvironment', args: { waves: null, ramp_s: 60 } });
    expect(setEnvironmentCommand({ wind: { trueSpeed: 10 }, waves: null })).toEqual({ type: 'setEnvironment', args: { wind: { trueSpeed: 10 }, waves: null } });
    // 不變:不放 waves
    expect(setEnvironmentCommand({ waterDepth: 12 })).toEqual({ type: 'setEnvironment', args: { waterDepth: 12 } });
    expect(ENVIRONMENT_FIELD_SUPPORT.waves).toBe('both');
  });
});

describe('SEA 方塊與 conning 條', () => {
  it('顯示 Hs/Tp、來向與相對來向、浪譜;交互作用超過門檻以警示色', async () => {
    const el = await mount('brm-sea', { waves: WAVES, interaction: INTERACTION, heading: 30 });
    const text = shadowText(el);
    expect(text).toContain('浪(PM)');
    expect(text).toContain('2.0m 7.0s');
    expect(text).toContain('090° / 060°');
    expect(text).toContain('交互作用 OVT 超限');
    expect(text).toContain('Y -12.3t N +851t·m');
    expect(el.shadowRoot!.querySelector('.interaction.event')).not.toBeNull();
    // 未超限:無警示色;無目標:顯示無目標
    const calm = await mount('brm-sea', { waves: WAVES, interaction: { ...INTERACTION, event: false }, heading: 0 });
    expect(calm.shadowRoot!.querySelector('.interaction.event')).toBeNull();
    expect(shadowText(calm)).toContain('交互作用 OVT Y -12.3t');
    const idle = await mount('brm-sea', { interaction: { active: false, forceSway_t: 0, momentYaw_tm: 0 } });
    expect(shadowText(idle)).toContain('無浪');
    expect(shadowText(idle)).toContain('無目標');
    expect(idle.shadowRoot!.querySelector('.interaction')).not.toBeNull();
    // 只有浪:無交互作用列
    const only = await mount('brm-sea', { waves: WAVES });
    expect(only.shadowRoot!.querySelector('.interaction')).toBeNull();
    expect(formatSignedTonnes(0.04)).toBe('0.0');
    expect(formatSignedTonnes(undefined)).toBe('--');
    expect(formatWavesSummary(WAVES)).toBe('Hs 2.0 m / Tp 7.0 s / 090°(PM)');
  });

  it('conning 條只在 state 有 waves 或 shipInteraction 時放 SEA 方塊', async () => {
    const cfg = getShipConfig('FSB1');
    const withSea = await mount('brm-conning-bar', { state: { ...defaultState('FSB1'), heading: 45, waves: WAVES }, config: cfg, issues: [] });
    const sea = withSea.shadowRoot!.querySelector('brm-sea') as (HTMLElement & Updatable) | null;
    expect(sea).not.toBeNull();
    await sea!.updateComplete;
    expect(shadowText(sea!)).toContain('090° / 045°');
    expect(withSea.shadowRoot!.querySelector('.bar.with-sea')).not.toBeNull();
    const interactionOnly = await mount('brm-conning-bar', { state: { ...defaultState('FSB1'), shipInteraction: INTERACTION }, config: cfg, issues: [] });
    expect(interactionOnly.shadowRoot!.querySelector('brm-sea')).not.toBeNull();
    const plain = await mount('brm-conning-bar', { state: defaultState('FSB1'), config: cfg, issues: [] });
    expect(plain.shadowRoot!.querySelector('brm-sea')).toBeNull();
    expect(plain.shadowRoot!.querySelector('.bar.with-sea')).toBeNull();
  });
});

describe('教官站環境面板:浪', () => {
  it('標題與提示顯示目前浪、表單帶入目前值、套用送 setEnvironment waves、關閉送 waves: null、漸變附 ramp_s', async () => {
    const cmds = captureCommands();
    const st: OwnShipState = { ...defaultState('FSB1'), wind: { trueSpeed: 15, trueDir: 45 }, current: { set: 200, drift: 0.5 }, waterDepth: 30, waves: { ...WAVES, spectrum: 'jonswap' } };
    const el = await mount('brm-environment-panel', { state: st });
    await el.updateComplete;
    expect(shadowText(el)).toContain('浪 Hs 2.0 m / Tp 7.0 s / 090°(JONSWAP)');
    expect(q<HTMLInputElement>(el, 'input.wave-hs').value).toBe('2');
    expect(q<HTMLInputElement>(el, 'input.wave-tp').value).toBe('7');
    expect(q<HTMLInputElement>(el, 'input.wave-dir').value).toBe('90');
    expect(q<HTMLSelectElement>(el, 'select.wave-spectrum').value).toBe('jonswap');
    expect(q<HTMLButtonElement>(el, 'button.clear-waves').disabled).toBe(false);
    setInput(el, 'input.wave-hs', '3.5');
    setInput(el, 'input.wave-dir', '135');
    q<HTMLButtonElement>(el, 'button.apply-waves').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { waves: { significantHeight_m: 3.5, peakPeriod_s: 7, direction_deg: 135, spectrum: 'jonswap' } } });
    setInput(el, 'input.ramp', '120');
    q<HTMLButtonElement>(el, 'button.clear-waves').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { waves: null, ramp_s: 120 } });
    await el.updateComplete;
    expect(shadowText(el)).toContain('浪只對 Hs');
    // 無浪:標題「浪 無」、關閉鈕停用、表單保留預設可直接建立
    const none = await mount('brm-environment-panel', { state: { ...defaultState('FSB1'), waterDepth: 30 } });
    expect(shadowText(none)).toContain('浪 無');
    expect(shadowText(none)).toContain('現在無浪');
    expect(q<HTMLButtonElement>(none, 'button.clear-waves').disabled).toBe(true);
    q<HTMLButtonElement>(none, 'button.apply-waves').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { waves: { significantHeight_m: 1, peakPeriod_s: 7, direction_deg: 0, spectrum: 'pm' } } });
    // 其他欄位的套用不帶 waves
    q<HTMLButtonElement>(none, 'button.apply-depth').click();
    expect(cmds.at(-1)).toEqual({ type: 'setEnvironment', args: { waterDepth: 30 } });
  });
});

describe('事件列:環境改變含浪', () => {
  const ev = (data: Record<string, unknown>) => parseSimEvent({ kind: 'environmentChange', tick: 100, t: 2, source: 'instructor', seq: 1, data })!;
  it('設定/變更列出有給的欄位、null 或 Hs 0 為浪關閉', () => {
    expect(eventLabel(ev({ waves: { significantHeight_m: 2, peakPeriod_s: 7, direction_deg: 90, spectrum: 'jonswap' }, ramp_s: 60 }))).toBe('環境改變 浪 Hs 2 m/Tp 7 s/090/JONSWAP(60 s 漸變)');
    expect(eventLabel(ev({ waves: { direction_deg: 180 } }))).toBe('環境改變 浪 180');
    expect(eventLabel(ev({ waves: null }))).toBe('環境改變 浪關閉');
    expect(eventLabel(ev({ wind: { trueSpeed: 20, trueDir: 10 }, waves: { significantHeight_m: 0 } }))).toBe('環境改變 風 20 kn/010 · 浪關閉');
    expect(eventLabel(ev({ wind: { trueSpeed: 20, trueDir: 10 }, ramp_s: 180 }))).toBe('環境改變 風 20 kn/010(180 s 漸變)');
  });
});
