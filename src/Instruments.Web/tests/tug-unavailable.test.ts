// 拖船 unavailable 與時間表 tugControl 動作(E17 拖船縮減;docs/design/physics-tugs-anchors-mooring.md)。
import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseScenario } from '../src/lib/scenario.js';
import { EXTERNAL_REJECT_REASON_LABEL, TUG_STATUS_LABEL, parseTugs } from '../src/lib/external-forces.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const e17 = path.resolve(here, '../../../data/scenarios/E17_typhoon_departure_kaohsiung.yaml');

describe('拖船 unavailable', () => {
  it('E17 的時間表 tugControl 動作可解析、無 issue', () => {
    const sc = parseScenario(readFileSync(e17, 'utf8'), 'E17_typhoon_departure_kaohsiung.yaml');
    expect(sc.issues).toEqual([]);
    const kinds = sc.timeline.flatMap((it) => it.actions.map((a) => a.kind));
    expect(kinds).toContain('tugControl');
  });

  it('state 的 unavailable 拖船解析為 unavailable(不是 standby),並有中文標籤與拒絕原因', () => {
    const issues: string[] = [];
    const tugs = parseTugs([{ id: 'TG2', mode: 'unavailable', status: 'unavailable', force_t: 0, direction_deg: 0 }], issues);
    expect(issues).toEqual([]);
    expect(tugs[0]?.status).toBe('unavailable');
    expect(tugs[0]?.mode).toBe('unavailable');
    expect(TUG_STATUS_LABEL.unavailable).toBeTruthy();
    expect(EXTERNAL_REJECT_REASON_LABEL['tugUnavailable']).toBeTruthy();
  });
});

import { DemoSim } from '../src/demo/demo-sim.js';

describe('示範模式拖船 unavailable', () => {
  it('tugControl mode unavailable 讓拖船離開,之後的指令以 tugUnavailable 拒絕', () => {
    const sim = new DemoSim();
    const before = sim.output() as { tugs?: Array<{ id: string }> };
    const id = before.tugs?.[0]?.id;
    expect(id).toBeTruthy();
    sim.apply({ type: 'tugControl', args: { id, mode: 'unavailable' } });
    sim.step(0.5);
    const tug = (sim.output() as { tugs: Array<{ id: string; status: string; mode: string }> }).tugs.find((t) => t.id === id)!;
    expect(tug.status).toBe('unavailable');
    expect(tug.mode).toBe('unavailable');
    sim.takeEvents();
    sim.apply({ type: 'tugControl', args: { id, mode: 'push', power_pct: 50 } });
    const events = sim.takeEvents();
    expect(JSON.stringify(events)).toContain('tugUnavailable');
  });
});
