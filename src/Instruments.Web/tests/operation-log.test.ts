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

describe('OperationLogDeriver 操舵模式', () => {
  it('state.steering 的模式/泵變更各一筆;NFU 推桿推舵/放開亦記;首筆基準不記', () => {
    const log = new OperationLogDeriver();
    const s = seq();
    expect(log.push(s.at(0, { steering: { mode: 'fu', pump: 'both' } }))).toEqual([]);
    expect(log.push(s.at(1, { steering: { mode: 'fu', pump: 'both' } }))).toEqual([]);
    const toNfu = log.push(s.at(2, { steering: { mode: 'nfu', pump: 'both', nfuActive: false } }));
    expect(toNfu).toHaveLength(1);
    expect(toNfu[0]).toMatchObject({ kind: 'steeringMode', t: 2, label: '操舵模式 FU 隨動 → NFU 非隨動(泵 both)', value: 'nfu/both' });
    const push = log.push(s.at(3, { steering: { mode: 'nfu', pump: 'both', nfuActive: true }, rudderOrder: 35 }));
    expect(push.map((e) => e.kind)).toEqual(['rudder', 'steeringMode']);
    expect(push[1]!.label).toBe('NFU 推桿推舵');
    const release = log.push(s.at(4, { steering: { mode: 'nfu', pump: 'both', nfuActive: false }, rudderOrder: 12 }));
    expect(release.at(-1)!.label).toBe('NFU 推桿放開');
    const pump = log.push(s.at(5, { steering: { mode: 'nfu', pump: '1', nfuActive: false }, rudderOrder: 12 }));
    expect(pump[0]).toMatchObject({ kind: 'steeringMode', label: '舵機泵 both → 1', value: 'nfu/1' });
    const emergency = log.push(s.at(6, { steering: { mode: 'emergency', pump: '1' }, rudderOrder: 12 }));
    expect(emergency[0]!.label).toBe('操舵模式 NFU 非隨動 → 應急操舵(泵 1)');
    expect(log.all().filter((e) => e.kind === 'steeringMode')).toHaveLength(5);
  });

  it('recordSteering(核心 steeringModeChanged 事件)與同一時刻的狀態推導不重複;時間相隔超過合併窗則各記', () => {
    const log = new OperationLogDeriver({ coalesceWindow_s: 1 });
    const s = seq();
    log.push(s.at(0, { steering: { mode: 'fu', pump: 'both' } }));
    const ev = log.recordSteering({ mode: 'autopilot', pump: 'both' }, 10, { mode: 'fu', pump: 'both' });
    expect(ev).toMatchObject({ kind: 'steeringMode', t: 10, label: '操舵模式 FU 隨動 → 自動舵(泵 both)', value: 'autopilot/both' });
    // 事件之後的狀態框帶同一模式 → 不重複
    expect(log.push(s.at(10.04, { steering: { mode: 'autopilot', pump: 'both' } }))).toEqual([]);
    expect(log.recordSteering({ mode: 'autopilot', pump: 'both' }, 10.5)).toBeNull();
    // 狀態先到、事件後到也不重複
    log.push(s.at(20, { steering: { mode: 'fu', pump: 'both' } }));
    expect(log.recordSteering({ mode: 'fu', pump: 'both' }, 20, { mode: 'autopilot', pump: 'both' })).toBeNull();
    // 同模式再切(例如 fu → nfu → fu)超過窗口則記
    expect(log.recordSteering({ mode: 'fu', pump: 'both' }, 30, { mode: 'nfu', pump: 'both' })).not.toBeNull();
    expect(log.all().map((e) => e.value)).toEqual(['autopilot/both', 'fu/both', 'fu/both']);
  });
});
