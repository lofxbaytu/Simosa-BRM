// 橫搖的儀表與教官站連動(state.schema.json roll;docs/design/visual-client.md 第 4 節「橫搖視角」;需求 R1/R3):
// state-parser 解析 state.roll(缺省不繼承、型別錯誤記入 issues)、傾斜儀 brm-inclinometer(±30° 刻度、指針、靜傾標記、P/S 數值、警示色)、
// conning 條只在 state 有 roll 時放傾斜儀、教官站自船面板顯示橫搖摘要。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { defaultState, parseRoll, parseState } from '../src/lib/state-parser.js';
import { formatHeel, INCLINOMETER_RANGE_DEG } from '../src/components/inclinometer.js';
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

const ROLL = { angle_deg: 3.25, rate_degps: -1.5, naturalPeriod_s: 11.2, staticHeel_deg: 1.0 };

beforeAll(async () => {
  await import('../src/components/inclinometer.js');
  await import('../src/components/conning-bar.js');
  await import('../src/components/instructor/ownship-panel.js');
});

describe('state-parser:roll', () => {
  const base = { ...defaultState('FSB1'), t: 1, tick: 50 };
  it('有欄位時解析四個欄位;缺省時不出現且不繼承前一幀', () => {
    const r = parseState({ ...base, roll: ROLL })!;
    expect(r.issues).toEqual([]);
    expect(r.state.roll).toEqual(ROLL);
    const next = parseState({ ...base }, r.state)!;
    expect(next.issues).toEqual([]);
    expect('roll' in next.state).toBe(false);
    expect(parseState({ ...base, roll: null })!.issues).toEqual([]);
  });
  it('型別錯誤記入 issues 但不影響其他欄位;非物件記 roll;自然週期非正記入 issues', () => {
    const r = parseState({ ...base, roll: { angle_deg: 'x', rate_degps: 1, naturalPeriod_s: 0, staticHeel_deg: 0.5 } })!;
    expect(r.issues).toEqual(['roll.naturalPeriod_s', 'roll.angle_deg']);
    expect(r.state.roll).toEqual({ angle_deg: 0, rate_degps: 1, naturalPeriod_s: 0, staticHeel_deg: 0.5 });
    expect(r.state.wind).toEqual(base.wind);
    const bad = parseState({ ...base, roll: 'tilted' })!;
    expect(bad.issues).toEqual(['roll']);
    expect(bad.state.roll).toBeUndefined();
    const issues: string[] = [];
    expect(parseRoll({ angle_deg: -2, rate_degps: 0, naturalPeriod_s: -5, staticHeel_deg: 0 }, issues)).toEqual({ angle_deg: -2, rate_degps: 0, naturalPeriod_s: 0, staticHeel_deg: 0 });
    expect(issues).toEqual(['roll.naturalPeriod_s']);
  });
});

describe('傾斜儀 brm-inclinometer', () => {
  it('P/S 文字:右傾 S、左傾 P、零不帶字母;量程 ±30°', () => {
    expect(formatHeel(3.25)).toBe('S 3.3°');
    expect(formatHeel(-1.5)).toBe('P 1.5°');
    expect(formatHeel(0)).toBe('0.0°');
    expect(formatHeel(-0.04)).toBe('0.0°');
    expect(formatHeel(undefined)).toBe('--');
    expect(INCLINOMETER_RANGE_DEG).toBe(30);
  });

  it('顯示橫搖(S)、靜傾、動態 = 角度 − 靜傾、週期與角速度;指針偏向 S 側、靜傾標記獨立', async () => {
    const el = await mount('brm-inclinometer', { roll: ROLL });
    const text = shadowText(el);
    expect(text).toContain('橫搖 S 3.3°');
    expect(text).toContain('靜傾 S 1.0°');
    expect(text).toContain('動態 S 2.3°');
    expect(text).toContain('11.2 s −1.5°/s');
    const pointer = el.shadowRoot!.querySelector<SVGLineElement>('line.pointer')!;
    expect(pointer).not.toBeNull();
    expect(Number(pointer.getAttribute('x2'))).toBeGreaterThan(70); // 樞軸在 x = 70,右傾 → 指針尖端偏右(S)
    expect(pointer.classList.contains('warn')).toBe(false);
    expect(el.shadowRoot!.querySelector('.static-marker')).not.toBeNull();
    // 左傾:指針偏左、文字 P;超過警示/警報門檻以對應 class
    const port = await mount('brm-inclinometer', { roll: { ...ROLL, angle_deg: -18, staticHeel_deg: -2 } });
    expect(shadowText(port)).toContain('橫搖 P 18.0°');
    expect(shadowText(port)).toContain('動態 P 16.0°');
    const p2 = port.shadowRoot!.querySelector<SVGLineElement>('line.pointer')!;
    expect(Number(p2.getAttribute('x2'))).toBeLessThan(70);
    expect(p2.classList.contains('warn')).toBe(true);
    expect(port.shadowRoot!.querySelector('.roll-angle.warn')).not.toBeNull();
    const heavy = await mount('brm-inclinometer', { roll: { ...ROLL, angle_deg: 34 } });
    expect(heavy.shadowRoot!.querySelector('line.pointer.alarm')).not.toBeNull();
    expect(shadowText(heavy)).toContain('橫搖 S 34.0°'); // 數值照實,指針夾在量程
    const tipX = Number(heavy.shadowRoot!.querySelector<SVGLineElement>('line.pointer')!.getAttribute('x2'));
    const limitX = 70 + 50 * Math.sin((30 * Math.PI) / 180);
    expect(Math.abs(tipX - limitX)).toBeLessThan(1e-6);
    // 無資料:-- 且無指針
    const none = await mount('brm-inclinometer', {});
    expect(shadowText(none)).toContain('橫搖 --');
    expect(none.shadowRoot!.querySelector('line.pointer')).toBeNull();
    expect(none.shadowRoot!.querySelector('.static-marker')).toBeNull();
  });
});

describe('conning 條與教官站自船面板', () => {
  it('conning 條只在 state 有 roll 時放傾斜儀並加 with-roll', async () => {
    const cfg = getShipConfig('FSB1');
    const withRoll = await mount('brm-conning-bar', { state: { ...defaultState('FSB1'), roll: ROLL }, config: cfg, issues: [] });
    const inc = withRoll.shadowRoot!.querySelector('brm-inclinometer') as (HTMLElement & Updatable) | null;
    expect(inc).not.toBeNull();
    await inc!.updateComplete;
    expect(shadowText(inc!)).toContain('橫搖 S 3.3°');
    expect(withRoll.shadowRoot!.querySelector('.bar.with-roll')).not.toBeNull();
    const plain = await mount('brm-conning-bar', { state: defaultState('FSB1'), config: cfg, issues: [] });
    expect(plain.shadowRoot!.querySelector('brm-inclinometer')).toBeNull();
    expect(plain.shadowRoot!.querySelector('.bar.with-roll')).toBeNull();
  });

  it('自船面板顯示橫搖摘要;無 roll 時不顯示', async () => {
    const el = await mount('brm-ownship-panel', { state: { ...defaultState('FSB1'), roll: ROLL } });
    await el.updateComplete;
    const text = shadowText(el);
    expect(el.shadowRoot!.querySelector('.roll-readout')).not.toBeNull();
    expect(text).toContain('橫搖 S 3.3° · 靜傾 S 1.0° · 動態 S 2.3° · 角速度 −1.5°/s · 自然週期 11.2 s');
    const none = await mount('brm-ownship-panel', { state: defaultState('FSB1') });
    expect(none.shadowRoot!.querySelector('.roll-readout')).toBeNull();
  });
});
