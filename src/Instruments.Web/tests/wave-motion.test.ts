// 浪一階運動(升沉/縱搖)的儀表與教官站連動(state.waveMotion;情境 ship.seakeeping.enabled 且有浪時才有;需求 R2/R3;
// docs/design/visual-client.md 第 4 節「橫搖、縱搖與升沉」的網頁端):
// state-parser 解析 state.waveMotion(缺省 → undefined 且不繼承前一幀、非數值防呆記入 issues)、操船台 SEA 方塊多一列「縱搖 ±x.x°」「升沉 ±x.xx m」
// (縱搖艏仰為正、升沉向上為正;無此欄位時方塊與原本相同)、conning 條把 waveMotion 傳給 SEA 方塊、
// 教官站自船面板在橫搖摘要同列顯示縱搖/升沉與速率。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { defaultState, parseState, parseWaveMotion } from '../src/lib/state-parser.js';
import { formatSignedMetres, formatWaveMotionSummary, type SeaReadout } from '../src/components/sea-readout.js';
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

function text(el: Element | null | undefined): string {
  return el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

const WAVES = { significantHeight_m: 3.5, peakPeriod_s: 9.0, direction_deg: 200, wavelength_m: 126.4, spectrum: 'jonswap' };
const MOTION = { heave_m: -0.347, pitch_deg: 1.24, heaveRate_mps: 0.118, pitchRate_dps: -0.304 };
const ROLL = { angle_deg: 3.25, rate_degps: -1.5, naturalPeriod_s: 11.2, staticHeel_deg: 1.0 };

beforeAll(async () => {
  await import('../src/components/sea-readout.js');
  await import('../src/components/conning-bar.js');
  await import('../src/components/instructor/ownship-panel.js');
});

describe('state-parser:waveMotion', () => {
  const base = { ...defaultState('FSB1'), t: 1, tick: 50 };

  it('有欄位時解析四個欄位;缺省時為 undefined 且不繼承前一幀;null 視同缺省', () => {
    const r = parseState({ ...base, waves: WAVES, waveMotion: MOTION })!;
    expect(r.issues).toEqual([]);
    expect(r.state.waveMotion).toEqual(MOTION);
    const next = parseState({ ...base, waves: WAVES }, r.state)!;
    expect(next.issues).toEqual([]);
    expect(next.state.waveMotion).toBeUndefined();
    expect('waveMotion' in next.state).toBe(false);
    const nul = parseState({ ...base, waveMotion: null })!;
    expect(nul.issues).toEqual([]);
    expect(nul.state.waveMotion).toBeUndefined();
    // 字串形式的 JSON 也一樣(WebSocket 文字框)
    const fromText = parseState(JSON.stringify({ ...base, waveMotion: MOTION }))!;
    expect(fromText.state.waveMotion).toEqual(MOTION);
  });

  it('沒有 waveMotion 的狀態其餘欄位與原本相同(既有情境不受影響)', () => {
    const plain = parseState({ ...base, roll: ROLL })!;
    expect(plain.issues).toEqual([]);
    expect(plain.state.roll).toEqual(ROLL);
    expect(Object.keys(plain.state)).not.toContain('waveMotion');
  });

  it('非數值防呆:缺少或非數值的欄位以 0 補上並記入 issues,不影響其他欄位;非物件記 waveMotion 且為 undefined', () => {
    const r = parseState({ ...base, roll: ROLL, waveMotion: { heave_m: 'high', pitch_deg: 1.5, heaveRate_mps: null, pitchRate_dps: true } })!;
    expect(r.issues).toEqual(['waveMotion.heave_m', 'waveMotion.heaveRate_mps', 'waveMotion.pitchRate_dps']);
    expect(r.state.waveMotion).toEqual({ heave_m: 0, pitch_deg: 1.5, heaveRate_mps: 0, pitchRate_dps: 0 });
    expect(r.state.roll).toEqual(ROLL);
    expect(r.state.wind).toEqual(base.wind);
    for (const bad of ['rough', 3, [1, 2], true]) {
      const b = parseState({ ...base, waveMotion: bad })!;
      expect(b.issues).toEqual(['waveMotion']);
      expect(b.state.waveMotion).toBeUndefined();
    }
    // 直接呼叫:NaN / Infinity 不是有限數值;缺欄位;數字字串照 num() 慣例接受
    const issues: string[] = [];
    expect(parseWaveMotion({ heave_m: Number.NaN, pitch_deg: Number.POSITIVE_INFINITY, heaveRate_mps: '0.25' }, issues)).toEqual({ heave_m: 0, pitch_deg: 0, heaveRate_mps: 0.25, pitchRate_dps: 0 });
    expect(issues).toEqual(['waveMotion.heave_m', 'waveMotion.pitch_deg', 'waveMotion.pitchRate_dps']);
  });
});

describe('SEA 方塊 brm-sea:縱搖 / 升沉', () => {
  it('帶號公尺與一行摘要:正值帶 +、負值用 Unicode 減號、四捨五入後為零不帶號;無值 --', () => {
    expect(formatSignedMetres(0.347)).toBe('+0.35');
    expect(formatSignedMetres(-0.3)).toBe('−0.30');
    expect(formatSignedMetres(0)).toBe('0.00');
    expect(formatSignedMetres(-0.004)).toBe('0.00');
    expect(formatSignedMetres(1.26, 1)).toBe('+1.3');
    expect(formatSignedMetres(undefined)).toBe('--');
    expect(formatSignedMetres(Number.NaN)).toBe('--');
    expect(formatWaveMotionSummary(MOTION)).toBe('縱搖 +1.2° / 升沉 −0.35 m');
    expect(formatWaveMotionSummary({ pitch_deg: -2.06, heave_m: 0.5 })).toBe('縱搖 −2.1° / 升沉 +0.50 m');
  });

  it('有 waveMotion 時在浪的數值下加一列縱搖/升沉(艏仰為正、向上為正);浪的既有兩行不變', async () => {
    const el = await mount<SeaReadout>('brm-sea', { waves: WAVES, motion: MOTION, heading: 20 });
    const rows = el.shadowRoot!.querySelectorAll('.rows > .row');
    expect(rows.length).toBe(3);
    expect(text(el.shadowRoot!.querySelector('.waves-hs'))).toBe('3.5m 9.0s');
    expect(text(el.shadowRoot!.querySelector('.waves-dir'))).toBe('200° / 180°');
    const row = el.shadowRoot!.querySelector('.rows > .row.motion')!;
    expect(row).toBe(rows[2]);
    const pairs = row.querySelectorAll('.pair');
    expect(pairs.length).toBe(2);
    expect(text(pairs[0]!.querySelector('.label'))).toBe('縱搖');
    expect(text(pairs[0]!.querySelector('.wave-pitch'))).toBe('+1.2°');
    expect(text(pairs[1]!.querySelector('.label'))).toBe('升沉');
    expect(text(pairs[1]!.querySelector('.wave-heave'))).toBe('−0.35m');
    expect(row.getAttribute('title')).toContain('縱搖艏仰為正、升沉向上為正');
    expect(row.getAttribute('title')).toContain('縱搖 +1.2° / 升沉 −0.35 m');
    // 更新:艏俯、上升
    el.motion = { heave_m: 0.5, pitch_deg: -2.06, heaveRate_mps: 0, pitchRate_dps: 0 };
    await el.updateComplete;
    expect(text(el.shadowRoot!.querySelector('.wave-pitch'))).toBe('−2.1°');
    expect(text(el.shadowRoot!.querySelector('.wave-heave'))).toBe('+0.50m');
  });

  it('無 waveMotion 時方塊與原本相同(兩行、無 .motion);關浪後 waveMotion 省略 → 只剩「無浪」', async () => {
    const el = await mount('brm-sea', { waves: WAVES, heading: 20 });
    expect(el.shadowRoot!.querySelectorAll('.rows > .row').length).toBe(2);
    expect(el.shadowRoot!.querySelector('.motion')).toBeNull();
    expect(shadowText(el)).not.toContain('縱搖');
    const calm = await mount('brm-sea', {});
    expect(calm.shadowRoot!.querySelector('.no-waves')).not.toBeNull();
    expect(calm.shadowRoot!.querySelector('.motion')).toBeNull();
    expect(calm.shadowRoot!.querySelector('.rose')).toBeNull();
    // 防呆:只有 waveMotion(契約上不會發生)仍顯示該行,不放羅盤
    const only = await mount('brm-sea', { motion: MOTION });
    expect(only.shadowRoot!.querySelector('.no-waves')).not.toBeNull();
    expect(text(only.shadowRoot!.querySelector('.wave-pitch'))).toBe('+1.2°');
    expect(text(only.shadowRoot!.querySelector('.wave-heave'))).toBe('−0.35m');
    expect(only.shadowRoot!.querySelector('.rose')).toBeNull();
  });
});

describe('conning 條與教官站自船面板', () => {
  const cfg = getShipConfig('FSB1');

  it('conning 條把 waveMotion 傳給 SEA 方塊;無 waveMotion 時 SEA 方塊沒有該行、版面 class 不變', async () => {
    const bar = await mount('brm-conning-bar', { state: { ...defaultState('FSB1'), heading: 20, waves: WAVES, waveMotion: MOTION }, config: cfg, issues: [] });
    const sea = bar.shadowRoot!.querySelector('brm-sea') as (HTMLElement & Updatable) | null;
    expect(sea).not.toBeNull();
    await sea!.updateComplete;
    expect(text(sea!.shadowRoot!.querySelector('.wave-pitch'))).toBe('+1.2°');
    expect(text(sea!.shadowRoot!.querySelector('.wave-heave'))).toBe('−0.35m');
    expect(bar.shadowRoot!.querySelector('.bar')!.className.split(/\s+/).filter(Boolean)).toEqual(['bar', 'with-sea']);

    const wavesOnly = await mount('brm-conning-bar', { state: { ...defaultState('FSB1'), waves: WAVES }, config: cfg, issues: [] });
    const sea2 = wavesOnly.shadowRoot!.querySelector('brm-sea') as HTMLElement & Updatable;
    await sea2.updateComplete;
    expect(sea2.shadowRoot!.querySelector('.motion')).toBeNull();
    expect(wavesOnly.shadowRoot!.querySelector('.bar')!.className.split(/\s+/).filter(Boolean)).toEqual(['bar', 'with-sea']);

    // 無浪、無交互作用、無 waveMotion:不放 SEA 方塊(原本的版面)
    const plain = await mount('brm-conning-bar', { state: defaultState('FSB1'), config: cfg, issues: [] });
    expect(plain.shadowRoot!.querySelector('brm-sea')).toBeNull();
    expect(plain.shadowRoot!.querySelector('.bar.with-sea')).toBeNull();

    // 防呆:只有 waveMotion 也放 SEA 方塊(資料不被吞掉)
    const only = await mount('brm-conning-bar', { state: { ...defaultState('FSB1'), waveMotion: MOTION }, config: cfg, issues: [] });
    expect(only.shadowRoot!.querySelector('brm-sea')).not.toBeNull();
    expect(only.shadowRoot!.querySelector('.bar.with-sea')).not.toBeNull();
  });

  it('自船面板:橫搖摘要同列接著縱搖/升沉與速率', async () => {
    const el = await mount('brm-ownship-panel', { state: { ...defaultState('FSB1'), roll: ROLL, waveMotion: MOTION } });
    await el.updateComplete;
    const line = el.shadowRoot!.querySelector('.attitude-readout')!;
    expect(line).not.toBeNull();
    expect(line.querySelector('.roll-readout')).not.toBeNull();
    expect(line.querySelector('.wave-motion-readout')).not.toBeNull();
    expect(text(line)).toBe('橫搖 S 3.3° · 靜傾 S 1.0° · 動態 S 2.3° · 角速度 −1.5°/s · 自然週期 11.2 s · 縱搖 +1.2°(−0.30°/s) · 升沉 −0.35 m(+0.12 m/s)');
    expect(line.querySelector('.wave-motion-readout')!.getAttribute('title')).toContain('縱搖艏仰為正、升沉向上為正');
  });

  it('自船面板:只有 waveMotion(未啟用橫搖)只列縱搖/升沉;只有橫搖時文字與原本相同;兩者皆無整列不顯示', async () => {
    const motionOnly = await mount('brm-ownship-panel', { state: { ...defaultState('FSB1'), waveMotion: { heave_m: 0.5, pitch_deg: -2.06, heaveRate_mps: -0.2, pitchRate_dps: 0.004 } } });
    await motionOnly.updateComplete;
    expect(motionOnly.shadowRoot!.querySelector('.roll-readout')).toBeNull();
    expect(text(motionOnly.shadowRoot!.querySelector('.attitude-readout'))).toBe('縱搖 −2.1°(0.00°/s) · 升沉 +0.50 m(−0.20 m/s)');

    const rollOnly = await mount('brm-ownship-panel', { state: { ...defaultState('FSB1'), roll: ROLL } });
    await rollOnly.updateComplete;
    expect(rollOnly.shadowRoot!.querySelector('.wave-motion-readout')).toBeNull();
    expect(text(rollOnly.shadowRoot!.querySelector('.attitude-readout'))).toBe('橫搖 S 3.3° · 靜傾 S 1.0° · 動態 S 2.3° · 角速度 −1.5°/s · 自然週期 11.2 s');

    const none = await mount('brm-ownship-panel', { state: defaultState('FSB1') });
    await none.updateComplete;
    expect(none.shadowRoot!.querySelector('.attitude-readout')).toBeNull();
    expect(none.shadowRoot!.querySelector('.roll-readout')).toBeNull();
    expect(none.shadowRoot!.querySelector('.wave-motion-readout')).toBeNull();
  });
});
