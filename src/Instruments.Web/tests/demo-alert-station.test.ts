// 示範模式的警報工作站規則(與核心一致;sensors-alerts.md 第 3.6 節、AlertStationTests):Category A 只能在負責站確認、已轉移責任者只能在接收站、
// 未知站拒絕;只有駕駛台三站且未被拒絕的 alertAcknowledge/alertSilence 重設 BNWAS(ecr 不重設);alertTransfer 依 transferTargets。
import { describe, expect, it } from 'vitest';
import { DEMO_DT, DemoSim } from '../src/demo/demo-sim.js';
import { BNWAS_RESET_COMMANDS } from '../src/demo/demo-alerts.js';
import type { SimEvent } from '../src/types/event.js';

function run(sim: DemoSim, seconds: number): void {
  const n = Math.round(seconds / DEMO_DT);
  for (let i = 0; i < n; i++) sim.step();
}

/** 示範:headingMonitor(Category A、warning,t = 4–150 s 假警報)於 t = 5 s 成立;BNWAS 第 1 階段 10 s、第 2 階段 25 s */
function simWithAlert(seconds = 26): DemoSim {
  const sim = new DemoSim({ initialSpeed_kn: 8, alerts: true });
  run(sim, seconds);
  sim.takeEvents();
  return sim;
}

const rejected = (events: SimEvent[]) => events.filter((e) => e.kind === 'commandRejected').map((e) => e.data);
const kinds = (events: SimEvent[]) => events.filter((e) => e.source !== 'traffic' && e.source !== 'timeline').map((e) => e.kind);

describe('示範模式警報工作站規則', () => {
  it('BNWAS_RESET_COMMANDS 不再含警報指令(改由站別規則決定)', () => {
    expect(BNWAS_RESET_COMMANDS.has('alertAcknowledge')).toBe(false);
    expect(BNWAS_RESET_COMMANDS.has('alertSilence')).toBe(false);
    expect(BNWAS_RESET_COMMANDS.has('rudder')).toBe(true);
  });

  it('Category A(headingMonitor)在 ecr 確認被拒 alertStationRequired、不重設 BNWAS;在 bridge 確認成功並重設(順序同核心:先確認後 bnwasReset)', () => {
    const sim = simWithAlert();
    expect(sim.output().bnwas!.stage).toBe(2);
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor', station: 'ecr' } });
    let ev = sim.takeEvents();
    expect(rejected(ev)).toEqual([{ command: 'alertAcknowledge', reason: 'alertStationRequired', id: 'headingMonitor', station: 'ecr' }]);
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.state).toBe('activeUnacknowledged');
    expect(sim.output().bnwas!.stage).toBe(2);
    // Category A 也不能在翼站確認(負責站 = bridge,與核心相同)
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor', station: 'stbdWing' } });
    expect(rejected(sim.takeEvents())).toEqual([{ command: 'alertAcknowledge', reason: 'alertStationRequired', id: 'headingMonitor', station: 'stbdWing' }]);
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor' } });
    ev = sim.takeEvents();
    expect(kinds(ev)).toEqual(['alertAcknowledged', 'bnwasReset']);
    expect(ev[0]!.data).not.toHaveProperty('ackStation');
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.state).toBe('activeAcknowledged');
    expect(sim.output().bnwas!.stage).toBe(0);
  });

  it('id 省略時 ecr 只確認可在 ecr 確認者(Category A 略過、不拒絕)、不重設 BNWAS;bnwas 警報(Category B)在 ecr 可確認', () => {
    const sim = simWithAlert();
    expect(sim.output().alerts!.map((a) => a.id)).toEqual(['bnwas', 'headingMonitor']);
    sim.apply({ type: 'alertAcknowledge', args: { station: 'ecr' } });
    const ev = sim.takeEvents();
    expect(rejected(ev)).toEqual([]);
    expect(kinds(ev)).toEqual(['alertAcknowledged']); // bnwas 警報被確認;headingMonitor 略過;無 bnwasReset
    expect(ev[0]!.itemId).toBe('bnwas');
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.state).toBe('activeUnacknowledged');
    expect(sim.output().bnwas!.stage).toBe(2); // ecr 不是駕駛台操作
  });

  it('未知站拒絕 unknownStation、不改狀態;已確認的 Category A 在 ecr 再確認不拒絕(無事可做,與核心相同)', () => {
    const sim = simWithAlert();
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor', station: 'engineRoom' } });
    expect(rejected(sim.takeEvents())).toEqual([{ command: 'alertAcknowledge', reason: 'unknownStation', id: 'headingMonitor', station: 'engineRoom' }]);
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor' } });
    sim.takeEvents();
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor', station: 'ecr' } });
    expect(rejected(sim.takeEvents())).toEqual([]);
  });

  it('alertSilence 不限制站別,但 ecr 靜音不重設 BNWAS、駕駛台靜音重設', () => {
    const sim = simWithAlert();
    sim.apply({ type: 'alertSilence', args: { id: 'headingMonitor', station: 'ecr' } });
    let ev = sim.takeEvents();
    expect(kinds(ev)).toEqual(['alertSilenced']);
    expect(sim.output().bnwas!.stage).toBe(2);
    run(sim, 31); // 重響
    sim.takeEvents();
    sim.apply({ type: 'alertSilence', args: { id: 'headingMonitor', station: 'portWing' } }); // 翼站靜音 = 駕駛台操作
    ev = sim.takeEvents();
    expect(kinds(ev)).toEqual(['alertSilenced', 'bnwasReset']);
    expect(sim.output().bnwas!.stage).toBe(0);
  });

  it('alertSilence 不驗證站別(與核心 SimulationEngine.AlertSilence 相同):未知站 engineRoom 靜音成功、不重設 BNWAS;id 省略亦同', () => {
    const sim = simWithAlert();
    sim.apply({ type: 'alertSilence', args: { id: 'headingMonitor', station: 'engineRoom' } });
    let ev = sim.takeEvents();
    expect(rejected(ev)).toEqual([]);
    expect(kinds(ev)).toEqual(['alertSilenced']);
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.state).toBe('activeSilenced');
    expect(sim.output().bnwas!.stage).toBe(2);
    run(sim, 31); // 重響
    sim.takeEvents();
    sim.apply({ type: 'alertSilence', args: { station: 'engineRoom' } });
    ev = sim.takeEvents();
    expect(rejected(ev)).toEqual([]);
    expect(kinds(ev).length).toBeGreaterThan(0);
    expect(kinds(ev).every((k) => k === 'alertSilenced')).toBe(true);
    expect(sim.output().bnwas!.stage).toBe(2);
  });

  it('未知 id + 未知站:alertAcknowledge/alertSilence/alertTransfer 皆以 unknownAlert 拒絕(先查 id 再查站,順序與核心 AlertManager 相同)', () => {
    const sim = simWithAlert();
    for (const type of ['alertAcknowledge', 'alertSilence', 'alertTransfer'] as const) {
      sim.apply({ type, args: { id: 'noSuchAlert', station: 'engineRoom' } });
      const r = rejected(sim.takeEvents());
      expect(r).toHaveLength(1);
      expect(r[0]).toMatchObject({ command: type, reason: 'unknownAlert', id: 'noSuchAlert' });
    }
    // 已知 id + 未知站:確認/轉移仍拒 unknownStation
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor', station: 'engineRoom' } });
    expect(rejected(sim.takeEvents())).toEqual([{ command: 'alertAcknowledge', reason: 'unknownStation', id: 'headingMonitor', station: 'engineRoom' }]);
    sim.apply({ type: 'alertTransfer', args: { id: 'headingMonitor', station: 'engineRoom' } });
    expect(rejected(sim.takeEvents())).toEqual([{ command: 'alertTransfer', reason: 'unknownStation', id: 'headingMonitor', station: 'engineRoom' }]);
  });

  it('alertTransfer:Category A 轉到 portWing 後只能在 portWing 確認(bridge 被拒);轉回 bridge 解除;gyro 來源不可轉 ecr;bnwas 不可轉移;未知警報拒絕', () => {
    const sim = simWithAlert();
    sim.apply({ type: 'alertTransfer', args: { id: 'headingMonitor', station: 'portWing' } });
    let ev = sim.takeEvents();
    expect(kinds(ev)).toEqual(['alertTransferred']);
    expect(ev[0]!.data).toMatchObject({ station: 'portWing', from: 'bridge', transferredTo: 'portWing' });
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')).toMatchObject({ station: 'bridge', transferredTo: 'portWing' });
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor' } }); // bridge
    expect(rejected(sim.takeEvents())).toEqual([{ command: 'alertAcknowledge', reason: 'alertStationRequired', id: 'headingMonitor', station: 'bridge' }]);
    sim.apply({ type: 'alertTransfer', args: { id: 'headingMonitor', station: 'ecr' } });
    expect(rejected(sim.takeEvents())).toEqual([{ command: 'alertTransfer', reason: 'alertNotTransferable', id: 'headingMonitor', station: 'ecr' }]);
    sim.apply({ type: 'alertTransfer', args: { id: 'bnwas', station: 'portWing' } });
    expect(rejected(sim.takeEvents())[0]).toMatchObject({ reason: 'alertNotTransferable', id: 'bnwas' });
    sim.apply({ type: 'alertTransfer', args: { id: 'gyroFailure', station: 'portWing' } });
    expect(rejected(sim.takeEvents())[0]).toMatchObject({ reason: 'unknownAlert', id: 'gyroFailure' });
    sim.apply({ type: 'alertTransfer', args: { id: 'headingMonitor' } });
    expect(rejected(sim.takeEvents())[0]).toMatchObject({ reason: 'unknownStation' });
    sim.apply({ type: 'alertTransfer', args: { id: 'headingMonitor', station: 'bridge' } });
    ev = sim.takeEvents();
    expect(ev[0]!.data).toMatchObject({ station: 'bridge', from: 'portWing', transferredTo: null });
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.transferredTo).toBeUndefined();
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor' } });
    expect(kinds(sim.takeEvents())).toEqual(['alertAcknowledged', 'bnwasReset']);
  });
});
