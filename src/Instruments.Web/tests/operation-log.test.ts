// 學員操作紀錄推導:由狀態變化推出舵令/車鐘/側推/凍結/故障/擱淺事件,連續舵令合併。
import { describe, expect, it } from 'vitest';
import { OperationLogDeriver } from '../src/lib/operation-log.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { OwnShipState } from '../src/types/state.js';

function seq(): { at: (t: number, patch?: Partial<OwnShipState>) => OwnShipState } {
  let last = defaultState('FSB1');
  return {
    at(t, patch = {}) {
      last = { ...structuredClone(last), ...patch, t };
      return last;
    },
  };
}

describe('OperationLogDeriver', () => {
  it('第一筆為基準;舵令、車鐘、側推變更各成一筆並帶時間戳', () => {
    let clock = 0;
    const log = new OperationLogDeriver({ now: () => new Date(Date.UTC(2026, 0, 1, 0, 0, clock++)) });
    const s = seq();
    expect(log.push(s.at(0))).toEqual([]);
    const ev1 = log.push(s.at(1, { rudderOrder: 20 }));
    expect(ev1).toHaveLength(1);
    expect(ev1[0]).toMatchObject({ seq: 1, t: 1, kind: 'rudder', label: '舵令 S 20°', value: 20, wall: '2026-01-01T00:00:00.000Z' });
    const ev2 = log.push(s.at(5, { telegraph: 'FAH', rpmOrder: 130 }));
    expect(ev2[0]).toMatchObject({ kind: 'telegraph', label: '車鐘 STOP → FAH', value: 'FAH' });
    const ev3 = log.push(s.at(9, { thruster: { order: -0.5, actual: 0 } }));
    expect(ev3[0]).toMatchObject({ kind: 'thruster', label: '側推 P 50%', value: -0.5 });
    expect(log.length).toBe(3);
  });

  it('1 秒內連續舵令變更合併為一筆(保留最後值與最早時間)', () => {
    const log = new OperationLogDeriver();
    const s = seq();
    log.push(s.at(0));
    log.push(s.at(0.1, { rudderOrder: 5 }));
    log.push(s.at(0.4, { rudderOrder: 10 }));
    log.push(s.at(0.8, { rudderOrder: 15 }));
    expect(log.length).toBe(1);
    expect(log.all()[0]).toMatchObject({ t: 0.1, value: 15, label: '舵令 S 15°' });
    log.push(s.at(3, { rudderOrder: -10 }));
    expect(log.length).toBe(2);
    expect(log.all()[1]).toMatchObject({ t: 3, value: -10, label: '舵令 P 10°' });
  });

  it('舵令小於死區不記錄;轉速令直接變更記為 rpm', () => {
    const log = new OperationLogDeriver();
    const s = seq();
    log.push(s.at(0));
    expect(log.push(s.at(1, { rudderOrder: 0.3 }))).toEqual([]);
    const ev = log.push(s.at(2, { rpmOrder: 80 }));
    expect(ev[0]).toMatchObject({ kind: 'rpm', value: 80 });
  });

  it('凍結/恢復、故障注入/清除、擱淺與時間倒退', () => {
    const log = new OperationLogDeriver();
    const s = seq();
    log.push(s.at(0));
    expect(log.push(s.at(1, { flags: { frozen: true, aground: false, collision: false } }))[0]).toMatchObject({ kind: 'freeze', label: '凍結' });
    expect(log.push(s.at(2, { flags: { frozen: false, aground: false, collision: false } }))[0]).toMatchObject({ kind: 'resume' });
    expect(log.push(s.at(3, { faults: ['steeringGear'] }))[0]).toMatchObject({ kind: 'fault', value: 'steeringGear', label: '故障注入 steeringGear' });
    expect(log.push(s.at(4, { faults: [] }))[0]).toMatchObject({ kind: 'faultCleared', value: 'steeringGear' });
    expect(log.push(s.at(5, { flags: { frozen: false, aground: true, collision: false } }))[0]).toMatchObject({ kind: 'aground' });
    expect(log.push(s.at(6))).toEqual([]); // 擱淺持續中不重複
    const back = log.push(s.at(0.5));
    expect(back[0]).toMatchObject({ kind: 'timeReset' });
    expect(back[0]!.label).toContain('0:06 → 0:00');
  });

  it('restart 後第一筆重新作基準;clear 清空', () => {
    const log = new OperationLogDeriver();
    const s = seq();
    log.push(s.at(0));
    log.push(s.at(1, { rudderOrder: 10 }));
    log.restart();
    expect(log.push(s.at(0, { rudderOrder: -10 }))).toEqual([]);
    expect(log.length).toBe(1);
    log.clear();
    expect(log.length).toBe(0);
  });
});
