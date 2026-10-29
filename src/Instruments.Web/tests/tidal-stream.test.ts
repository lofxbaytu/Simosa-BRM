// 時變流場與潮高顯示(設計文件第 1.5.1/1.5.2 節;需求 R3):state-parser 解析 tideHeight_m / currentSource、
// 深度方塊顯示「潮高 +x.x m」(無潮汐時不顯示)、流方塊與教官站環境面板標示來源(流場表/潮流)、情境摘要標示時變流場、
// E06b 核心紀錄夾具(record-E06b_tidal_stream-core.jsonl)的 header/state 可解析且 current 隨時間變化。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { defaultState, parseState } from '../src/lib/state-parser.js';
import { parseScenario, environmentSummary } from '../src/lib/scenario.js';
import { parseReplay } from '../src/lib/record-replay.js';
import { getShipConfig } from '../src/lib/ship-config.js';
import { formatTide } from '../src/components/depth-readout.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const fx = (name: string): string => readFileSync(path.resolve(here, 'fixtures', name), 'utf-8');
const scenarioDir = path.resolve(here, '../../../data/scenarios');

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

beforeAll(async () => {
  await import('../src/components/conning-bar.js');
  await import('../src/components/instructor/environment-panel.js');
});

describe('state-parser:tideHeight_m 與 currentSource', () => {
  const base = { ...defaultState('FSB1'), t: 1, tick: 50 };
  it('有欄位時解析,缺省時不出現', () => {
    const r = parseState({ ...base, tideHeight_m: 1.85, currentSource: 'tidal', current: { set: 265, drift: 3 } })!;
    expect(r.issues).toEqual([]);
    expect(r.state.tideHeight_m).toBe(1.85);
    expect(r.state.currentSource).toBe('tidal');
    expect(r.state.current).toEqual({ set: 265, drift: 3 });
    const plain = parseState({ ...base })!;
    expect(plain.issues).toEqual([]);
    expect('tideHeight_m' in plain.state).toBe(false);
    expect('currentSource' in plain.state).toBe(false);
    expect(parseState({ ...base, currentSource: 'table' })!.state.currentSource).toBe('table');
  });
  it('型別錯誤記入 issues 但不影響其他欄位', () => {
    const r = parseState({ ...base, tideHeight_m: 'high', currentSource: 'random' })!;
    expect(r.issues).toEqual(['tideHeight_m', 'currentSource']);
    expect(r.state.tideHeight_m).toBeUndefined();
    expect(r.state.currentSource).toBeUndefined();
    expect(r.state.current).toEqual(base.current);
  });
});

describe('深度方塊潮高', () => {
  it('formatTide 帶正負號', () => {
    expect(formatTide(1.86)).toBe('+1.9 m');
    expect(formatTide(-0.26)).toBe('−0.3 m');
    expect(formatTide(0)).toBe('0.0 m');
    expect(formatTide(0.04)).toBe('0.0 m');
  });
  it('有潮高時顯示「潮高 +x.x m」,無潮高時不顯示該列', async () => {
    const el = await mount('brm-depth', { ukc: 3, waterDepth: 8.9, tideHeight: 1.9 });
    expect(shadowText(el)).toContain('潮高+1.9 m');
    expect(el.shadowRoot?.querySelector('.row.tide')).not.toBeNull();
    const none = await mount('brm-depth', { ukc: 3, waterDepth: 8.9 });
    expect(shadowText(none)).not.toContain('潮高');
    expect(none.shadowRoot?.querySelector('.row.tide')).toBeNull();
  });
  it('conning 列把 tideHeight_m 與 currentSource 傳給深度/流方塊', async () => {
    const s = { ...defaultState('FSB1'), tideHeight_m: -0.3, currentSource: 'tidal' as const, current: { set: 265, drift: 3 }, waterDepth: 13.7 };
    const bar = await mount('brm-conning-bar', { state: s, config: getShipConfig('FSB1'), issues: [], noData: false });
    const depth = bar.shadowRoot!.querySelector('brm-depth') as HTMLElement & { tideHeight?: number; updateComplete: Promise<unknown> };
    await depth.updateComplete;
    expect(depth.tideHeight).toBe(-0.3);
    expect(shadowText(depth)).toContain('潮高−0.3 m');
    const current = bar.shadowRoot!.querySelector('brm-current') as HTMLElement & { source?: string; updateComplete: Promise<unknown> };
    await current.updateComplete;
    expect(current.source).toBe('tidal');
    expect(shadowText(current)).toContain('流(潮流)');
    // 常數流:不標示來源
    const plain = await mount('brm-current', { set: 85, drift: 1.5, heading: 125 });
    expect(shadowText(plain)).not.toContain('(');
    const table = await mount('brm-current', { set: 85, drift: 1.5, heading: 125, source: 'table' });
    expect(shadowText(table)).toContain('流(流場表)');
  });
});

describe('教官站環境面板:時變流場來源', () => {
  it('標題顯示目前流向流速與來源、潮高,並提示套用流會停止時變', async () => {
    const st = { ...defaultState('FSB1'), current: { set: 265, drift: 3.04 }, currentSource: 'tidal' as const, waterDepth: 18.2, tideHeight_m: 4.22 };
    const el = await mount('brm-environment-panel', { state: st });
    const text = shadowText(el);
    expect(text).toContain('流 265°/3 kn(潮流)');
    expect(text).toContain('水深 18.2 m(潮高 +4.2 m)');
    expect(el.shadowRoot?.querySelector('.hint.current-source')?.textContent).toContain('停止時變');
    const plain = await mount('brm-environment-panel', { state: { ...defaultState('FSB1'), current: { set: 85, drift: 1.5 }, waterDepth: 14 } });
    expect(shadowText(plain)).toContain('流 85°/1.5 kn ·');
    expect(plain.shadowRoot?.querySelector('.hint.current-source')).toBeNull();
    const table = await mount('brm-environment-panel', { state: { ...st, currentSource: 'table' as const } });
    expect(shadowText(table)).toContain('(流場表)');
  });
});

describe('情境摘要與 E06b 夾具', () => {
  it('parseScenario 標示 current.table / tidal 來源,摘要帶「(潮流)」', () => {
    const text = readFileSync(path.resolve(scenarioDir, 'E06b_tidal_stream.yaml'), 'utf-8');
    const s = parseScenario(text, 'data/scenarios/E06b_tidal_stream.yaml');
    expect(s.issues).toEqual([]);
    expect(s.environment.current).toEqual({ set: 85, drift: 2.5, source: 'tidal' });
    expect(environmentSummary(s.environment)).toContain('流 085°/2.5 kn(潮流)');
    const flood = parseScenario(readFileSync(path.resolve(scenarioDir, 'E06_taichung_channel_flood.yaml'), 'utf-8'), 'data/scenarios/E06_taichung_channel_flood.yaml');
    expect(flood.environment.current).toEqual({ set: 85, drift: 1.5 });
    expect(environmentSummary(flood.environment)).toContain('流 085°/1.5 kn ·');
    const table = parseScenario(
      'id: T\nship: { id: FSB1 }\ninitial: { position: { lat: 23.8, lon: 120 }, heading: 0 }\nenvironment:\n  waterDepth: 20\n  current:\n    set: 10\n    drift: 0\n    table:\n      - { t: 0, speed_kn: 0, direction_deg: 10 }\n      - { t: 60, speed_kn: 1, direction_deg: 20 }\n',
      'data/scenarios/T.yaml',
    );
    expect(table.environment.current).toEqual({ set: 10, drift: 0, source: 'table' });
    expect(environmentSummary(table.environment)).toContain('流 010°/0 kn(流場表)');
  });
  it('E06b 核心紀錄夾具:header 情境含 tide 與 current.tidal,state 的 current/currentSource/tideHeight_m 隨時間變化', () => {
    const r = parseReplay(fx('record-E06b_tidal_stream-core.jsonl'));
    expect(r.source).toBe('simcore-jsonl');
    expect(r.header.scenarioId).toBe('E06b_tidal_stream');
    expect(r.states.length).toBeGreaterThanOrEqual(6);
    for (const st of r.states) {
      expect(st.currentSource).toBe('tidal');
      expect(st.tideHeight_m).toBeGreaterThan(3.9);
      expect(st.current.set).toBeCloseTo(85, 6);
    }
    const first = r.states[0]!;
    const last = r.states.at(-1)!;
    expect(last.tideHeight_m!).toBeGreaterThan(first.tideHeight_m!);   // 漲潮中潮高上升
    expect(last.waterDepth! - last.tideHeight_m!).toBeCloseTo(14, 6);   // 有效水深 = 基底 14 m + 潮高
    expect(first.current.drift).toBeCloseTo(2.5, 6);
  });
});
