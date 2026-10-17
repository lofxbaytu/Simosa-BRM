// 目標船(state.targets[]):state-parser 解析(完整項目含 AIS、舊狀態無 targets → 空陣列、壞項目略過並記 issues)、
// cpaAlarm 狀態由事件推得、CPA/TCPA 小表排序,以及航跡圖在 target-table 開啟時列出小表(happy-dom)。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { defaultState, parseState } from '../src/lib/state-parser.js';
import { cpaAlarmTargets, targetTableRows } from '../src/lib/targets.js';
import type { SimEvent } from '../src/types/event.js';
import type { TargetState } from '../src/types/target.js';
import type { TrackPlot } from '../src/components/track-plot.js';

const fullTarget = {
  id: 'OUT1',
  name: 'TAI PING 12',
  mmsi: 416400201,
  shipType: 'cargo',
  loa: 95,
  beam: 15,
  draft: 5.5,
  pos: { lat: 23.8, lon: 120.15, x: 5100.5, y: 2200.25 },
  heading: 250.4,
  cog: 248.9,
  sog: 6.1,
  stw: 6.0,
  rot: -1.5,
  range_nm: 2.94,
  bearing_deg: 75.1,
  relBearing_deg: 23.1,
  cpa_nm: 0.25,
  tcpa_min: 12.3,
  bcr_nm: -0.4,
  bct_min: 14.2,
  aisOn: true,
  lights: 'powerDriven',
  sound: 'oneShort',
  behaviour: 'waypoints',
  colreg: 'crossing/giveWay/avoiding',
  ais: { mmsi: 416400201, name: 'TAI PING 12', callSign: 'BXYZ', shipType: 70, navStatus: 0, lat: 23.8, lon: 120.15, cog: 248.9, sog: 6.1, heading: 250, rot: -1.5, dimToBow: 60, dimToStern: 35, dimToPort: 7, dimToStarboard: 8, draught: 5.5, destination: 'MAILIAO' },
};

function base(extra: Record<string, unknown> = {}): Record<string, unknown> {
  const { targets: _t, ...rest } = defaultState('FSB1');
  return { ...rest, t: 1, tick: 50, ...extra };
}

function ev(kind: string, t: number, targetId?: string): SimEvent {
  const e: SimEvent = { kind, tick: Math.round(t / 0.02), t, source: 'traffic', visible: false, seq: Math.round(t) };
  if (targetId) e.targetId = targetId;
  return e;
}

describe('parseState targets[]', () => {
  it('完整目標項目含 AIS 報告原樣解析,無問題清單', () => {
    const r = parseState(base({ targets: [fullTarget] }))!;
    expect(r.issues).toEqual([]);
    expect(r.state.targets).toHaveLength(1);
    const t = r.state.targets[0]!;
    expect(t).toMatchObject({ id: 'OUT1', name: 'TAI PING 12', mmsi: 416400201, shipType: 'cargo', loa: 95, beam: 15, draft: 5.5, heading: 250.4, cog: 248.9, sog: 6.1, stw: 6, rot: -1.5, range_nm: 2.94, bearing_deg: 75.1, relBearing_deg: 23.1, cpa_nm: 0.25, tcpa_min: 12.3, bcr_nm: -0.4, bct_min: 14.2, aisOn: true, lights: 'powerDriven', sound: 'oneShort', behaviour: 'waypoints', colreg: 'crossing/giveWay/avoiding' });
    expect(t.pos).toEqual({ lat: 23.8, lon: 120.15, x: 5100.5, y: 2200.25 });
    expect(t.ais).toMatchObject({ mmsi: 416400201, name: 'TAI PING 12', callSign: 'BXYZ', shipType: 70, navStatus: 0, destination: 'MAILIAO', dimToBow: 60, draught: 5.5 });
  });

  it('舊狀態(無 targets 欄位)或 targets 為 null → 空陣列且不記問題;AIS 關閉者無 ais', () => {
    const old = parseState(base())!;
    expect(old.state.targets).toEqual([]);
    expect(old.issues).toEqual([]);
    expect(parseState(base({ targets: null }))!.state.targets).toEqual([]);
    expect(defaultState().targets).toEqual([]);
    const { ais: _a, ...noAis } = fullTarget;
    const off = parseState(base({ targets: [{ ...noAis, aisOn: false }] }))!;
    expect(off.state.targets[0]).toMatchObject({ aisOn: false });
    expect(off.state.targets[0]!.ais).toBeUndefined();
    // 解析時不沿用前一筆的目標(每幀含所有已出現的目標)
    const prev = parseState(base({ targets: [fullTarget] }))!.state;
    expect(parseState(base({ t: 2 }), prev)!.state.targets).toEqual([]);
  });

  it('缺 id 或非物件的項目略過並記入 issues;缺數值欄位補 0;targets 非陣列記入 issues;航向正規化', () => {
    const r = parseState(base({ targets: [{ name: 'noid' }, 'junk', { id: 'T2', pos: { lat: 0, lon: 0, x: 1, y: 2 }, heading: 370, cog: -10, aisOn: true, cpa_nm: 0.9, tcpa_min: 5 }] }))!;
    expect(r.state.targets).toHaveLength(1);
    expect(r.state.targets[0]).toMatchObject({ id: 'T2', name: 'T2', heading: 10, cog: 350, sog: 0, range_nm: 0, cpa_nm: 0.9, tcpa_min: 5, shipType: 'other', behaviour: 'hold' });
    expect(r.issues).toContain('targets[0].id');
    expect(r.issues).toContain('targets[1]');
    expect(r.issues).toContain('targets[2].range_nm');
    expect(r.issues).toContain('targets[2].mmsi');
    expect(parseState(base({ targets: 'x' }))!.issues).toContain('targets');
  });
});

describe('cpaAlarmTargets / targetTableRows', () => {
  const events = [ev('targetActivated', 10, 'T1'), ev('cpaAlarm', 100, 'T1'), ev('cpaAlarm', 120, 'T2'), ev('cpaClear', 150, 'T1'), ev('collision', 160, 'T2')];
  it('cpaAlarm 進入、cpaClear 解除;重播時只看 t ≤ tMax 的事件', () => {
    expect([...cpaAlarmTargets(events)]).toEqual(['T2']);
    expect([...cpaAlarmTargets(events, 130)].sort()).toEqual(['T1', 'T2']);
    expect([...cpaAlarmTargets(events, 50)]).toEqual([]);
    expect([...cpaAlarmTargets([])]).toEqual([]);
  });
  it('小表:警報者在前,其餘依距離;無船名用 id;最多 max 列', () => {
    const mk = (id: string, range: number, name = ''): TargetState => ({ id, name, mmsi: 0, shipType: 'other', loa: 50, beam: 10, pos: { lat: 0, lon: 0, x: 0, y: 0 }, heading: 0, cog: 0, sog: 0, rot: 0, range_nm: range, bearing_deg: 90, cpa_nm: range / 2, tcpa_min: 1, aisOn: id !== 'C', lights: 'powerDriven', behaviour: 'hold' });
    const rows = targetTableRows([mk('A', 3, 'ALPHA'), mk('B', 1), mk('C', 2)], new Set(['C']));
    expect(rows.map((r) => r.id)).toEqual(['C', 'B', 'A']);
    expect(rows[0]).toMatchObject({ label: 'C', alarm: true, aisOn: false });
    expect(rows[2]).toMatchObject({ label: 'ALPHA', alarm: false });
    expect(targetTableRows([mk('A', 3), mk('B', 1), mk('C', 2)], new Set(), 2).map((r) => r.id)).toEqual(['B', 'C']);
  });
});

describe('航跡圖目標船小表', () => {
  beforeAll(async () => {
    await import('../src/components/track-plot.js');
    await import('../src/lib/ship-config.js');
  });
  it('target-table 開啟且有目標時列出 CPA/TCPA,警報列標 alarm;關閉時不列', async () => {
    const { getShipConfig } = await import('../src/lib/ship-config.js');
    const { TrackHistory } = await import('../src/lib/track-history.js');
    const state = parseState(base({ targets: [fullTarget, { ...fullTarget, id: 'ANCH1', name: 'SHENG LI', range_nm: 1.2, cpa_nm: 1.01, tcpa_min: -3, aisOn: false }] }))!.state;
    const el = document.createElement('brm-track-plot') as TrackPlot;
    el.state = state;
    el.config = getShipConfig('FSB1');
    el.history = new TrackHistory(1, 100);
    el.targets = state.targets;
    el.alarmTargets = cpaAlarmTargets([ev('cpaAlarm', 0.5, 'OUT1')]);
    el.setAttribute('target-table', '');
    document.body.appendChild(el);
    await el.updateComplete;
    const rows = Array.from(el.shadowRoot!.querySelectorAll<HTMLElement>('table.targets tbody tr'));
    expect(rows.map((r) => r.dataset['target'])).toEqual(['OUT1', 'ANCH1']);
    expect(rows[0]!.className).toContain('alarm');
    expect(rows[1]!.className).toContain('aisoff');
    const text = el.shadowRoot!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('TAI PING 12 2.94 075 0.25 12.3');
    expect(text).toContain('SHENG LI 1.20 075 1.01 -3.0');
    expect(text).toContain('三角 = 目標船');
    el.removeAttribute('target-table');
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('table.targets')).toBeNull();
    el.targets = [];
    await el.updateComplete;
    expect(el.shadowRoot!.textContent).not.toContain('三角 = 目標船');
  });
});
