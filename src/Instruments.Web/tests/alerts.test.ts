// 警報管理(BAM)、BNWAS 與感測器真值/指示值:解析(含設計文件 docs/design/sensors-alerts.md 的 E08 第 600 s 範例)、
// 排序與狀態顯示、指令內容、BNWAS 倒數、由事件流推得的歷史、summary.alerts、聲響(假 AudioContext)、示範模式的警報與 BNWAS 循環。
import { describe, expect, it } from 'vitest';
import { parseState, defaultState } from '../src/lib/state-parser.js';
import {
  alertAcknowledgeCommand,
  alertCounts,
  alertHistory,
  alertSilenceCommand,
  alertVisual,
  bnwasCounts,
  bnwasDisplay,
  bnwasModeCommand,
  bnwasResetCommand,
  formatCountdown,
  parseAlertItems,
  sensorComparison,
  silenceRemaining,
  sortAlerts,
} from '../src/lib/alerts.js';
import { serializeCommand } from '../src/lib/command.js';
import { eventLabel, eventSeverity, parseSimEvent } from '../src/lib/sim-event.js';
import { parseAssessmentSummary } from '../src/lib/assessment.js';
import { AlertSounder, soundPriority, type AudioContextLike } from '../src/lib/alert-sound.js';
import { faultCatalogue, injectFaultCommand, parseFaultCatalogueJson } from '../src/lib/instructor-command.js';
import { DEMO_DT, DemoSim } from '../src/demo/demo-sim.js';
import { DEMO_BNWAS_PRE_ELAPSED_S } from '../src/demo/demo-alerts.js';
import type { AlertItem } from '../src/types/alerts.js';
import type { SimEvent } from '../src/types/event.js';

/** 設計文件/核心報告的 E08 第 600 s 狀態(頂層為指示值)。 */
const e08Frame = {
  ...defaultState('FSB1'),
  t: 600,
  tick: 30000,
  heading: 28.25,
  pos: { lat: 24.1, lon: 120.3, x: 1000, y: 5000 },
  cog: 35.92,
  sog: 11.04,
  stw: 10.28,
  depthBelowKeel: 29.23,
  truth: { heading: 37.19, pos: { lat: 24.1, lon: 120.3, x: 1000, y: 5000 }, cog: 35.92, sog: 11.04, stw: 10.28, depthBelowKeel: 29.23, headingError_deg: -8.94, positionError_m: 0 },
  sensors: { headingValid: false, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 38.19 },
  autopilot: { enabled: false, setHeading: 29.0, rotLimit_degPerMin: 15, available: false },
  alerts: [
    { id: 'bnwas', number: 14, priority: 'alarm', category: 'B', source: 'bnwas', state: 'activeUnacknowledged', text: 'BNWAS 值班警報', raisedAt_s: 540.02, escalated: true, count: 1, audible: true },
    { id: 'gyroFailure', number: 1, priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', text: '電羅經失效', raisedAt_s: 480.02, escalated: false, count: 1, audible: true },
  ],
  bnwas: { mode: 'on', active: true, stage: 2, remaining_s: 45.02, dormant_s: 540 },
  faults: ['gyroDrift', 'gyroFailure'],
};

function alert(over: Partial<AlertItem>): AlertItem {
  return { id: 'x', number: 1, priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', text: 'x', raisedAt_s: 0, escalated: false, count: 1, audible: true, ...over };
}

const ev = (kind: string, t: number, data: Record<string, unknown>, extra: Partial<SimEvent> = {}): SimEvent => ({ kind, t, tick: Math.round(t / 0.02), source: 'engine', visible: true, seq: 0, itemId: typeof data['id'] === 'string' ? (data['id'] as string) : 'bnwas', data, ...extra });

describe('state 的 truth / sensors / autopilot / alerts / bnwas 解析', () => {
  it('E08 第 600 s 範例:全部欄位解析、警報依優先序與時間排序、無問題', () => {
    const r = parseState(JSON.parse(JSON.stringify(e08Frame)))!;
    expect(r.issues).toEqual([]);
    const s = r.state;
    expect(s.heading).toBeCloseTo(28.25);
    expect(s.truth).toMatchObject({ heading: 37.19, headingError_deg: -8.94, positionError_m: 0, stw: 10.28 });
    expect(s.sensors).toEqual({ headingValid: false, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 38.19 });
    expect(s.autopilot).toEqual({ enabled: false, setHeading: 29, rotLimit_degPerMin: 15, available: false });
    expect(s.alerts!.map((a) => a.id)).toEqual(['gyroFailure', 'bnwas']);
    expect(s.alerts![1]).toMatchObject({ number: 14, escalated: true, audible: true, category: 'B' });
    expect(s.bnwas).toEqual({ mode: 'on', active: true, stage: 2, remaining_s: 45.02, dormant_s: 540 });
  });

  it('舊狀態(Python 參考伺服器)沒有這些欄位:undefined、不記問題;下一筆省略 alerts 即清空', () => {
    const r = parseState(JSON.parse(JSON.stringify(defaultState('FSB2'))))!;
    expect(r.issues).toEqual([]);
    expect(r.state.alerts).toBeUndefined();
    expect(r.state.bnwas).toBeUndefined();
    expect(r.state.truth).toBeUndefined();
    expect(r.state.sensors).toBeUndefined();
    const withAlerts = parseState(JSON.parse(JSON.stringify(e08Frame)))!.state;
    const next = parseState(JSON.parse(JSON.stringify({ ...defaultState('FSB1'), t: 601 })), withAlerts)!;
    expect(next.state.alerts).toBeUndefined();
  });

  it('壞資料:缺 id 的警報略過,列舉外的優先序/狀態以安全值補上並記入 issues;bnwas 缺 mode 不採用', () => {
    const issues: string[] = [];
    const list = parseAlertItems([{ priority: 'alarm' }, { id: 'gpsFailure', priority: 'loud', state: 'weird', raisedAt_s: 3 }, 'x'], issues);
    expect(list.map((a) => a.id)).toEqual(['gpsFailure']);
    expect(list[0]).toMatchObject({ priority: 'alarm', state: 'activeUnacknowledged', text: 'GNSS 失效', audible: true, count: 1 });
    expect(issues).toEqual(expect.arrayContaining(['alerts[0].id', 'alerts[1].priority', 'alerts[1].state', 'alerts[2]']));
    const r = parseState({ ...defaultState('FSB1'), bnwas: { stage: 1 }, alerts: 'nope', truth: { heading: 3 } })!;
    expect(r.state.bnwas).toBeUndefined();
    expect(r.state.truth).toBeUndefined();
    expect(r.issues).toEqual(expect.arrayContaining(['bnwas.mode', 'alerts', 'truth']));
  });
});

describe('警報排序與狀態顯示', () => {
  it('sortAlerts:緊急警報 → 警報 → 警告 → 注意,同優先序依成立時間、再依編號;不改動輸入', () => {
    const input = [
      alert({ id: 'c', priority: 'caution', raisedAt_s: 1 }),
      alert({ id: 'w', priority: 'warning', raisedAt_s: 2 }),
      alert({ id: 'a2', priority: 'alarm', raisedAt_s: 9, number: 2 }),
      alert({ id: 'a1', priority: 'alarm', raisedAt_s: 5, number: 9 }),
      alert({ id: 'e', priority: 'emergencyAlarm', raisedAt_s: 20 }),
      alert({ id: 'a0', priority: 'alarm', raisedAt_s: 9, number: 1 }),
    ];
    expect(sortAlerts(input).map((a) => a.id)).toEqual(['e', 'a1', 'a0', 'a2', 'w', 'c']);
    expect(input[0]!.id).toBe('c');
  });

  it('alertVisual:alarm 三角紅閃、warning 圓、caution 方且不閃不需確認;靜音、已解除未確認、已確認', () => {
    expect(alertVisual(alert({ priority: 'alarm' }))).toEqual({ tone: 'alarm', shape: 'triangle', flashing: true, silenced: false, rectified: false, needsAck: true });
    expect(alertVisual(alert({ priority: 'emergencyAlarm' })).tone).toBe('emergency');
    expect(alertVisual(alert({ priority: 'warning' }))).toMatchObject({ tone: 'warning', shape: 'circle', flashing: true });
    expect(alertVisual(alert({ priority: 'caution' }))).toMatchObject({ tone: 'caution', shape: 'square', flashing: false, needsAck: false });
    expect(alertVisual(alert({ state: 'activeSilenced' }))).toMatchObject({ flashing: false, silenced: true, needsAck: true });
    expect(alertVisual(alert({ state: 'rectifiedUnacknowledged' }))).toMatchObject({ flashing: false, rectified: true, needsAck: true });
    expect(alertVisual(alert({ state: 'activeAcknowledged' }))).toMatchObject({ flashing: false, needsAck: false });
  });

  it('alertCounts 與靜音倒數', () => {
    const list = [alert({ priority: 'warning', audible: true }), alert({ priority: 'alarm', state: 'activeAcknowledged', audible: false }), alert({ priority: 'caution', audible: false })];
    expect(alertCounts(list)).toEqual({ total: 3, unacknowledged: 1, audible: 1, highest: 'alarm', highestAudible: 'warning', highestUnacknowledged: 'warning' });
    expect(alertCounts(undefined)).toEqual({ total: 0, unacknowledged: 0, audible: 0, highest: null, highestAudible: null, highestUnacknowledged: null });
    expect(silenceRemaining(alert({ state: 'activeSilenced', silencedUntil_s: 585 }), 570.4)).toBeCloseTo(14.6);
    expect(silenceRemaining(alert({}), 1)).toBeUndefined();
  });
});

describe('警報與 BNWAS 指令(command.schema.json)', () => {
  it('alertAcknowledge / alertSilence:給 id 帶 args.id;省略或 "all" 不帶', () => {
    expect(alertAcknowledgeCommand('gyroFailure')).toEqual({ type: 'alertAcknowledge', args: { id: 'gyroFailure' } });
    expect(alertAcknowledgeCommand()).toEqual({ type: 'alertAcknowledge' });
    expect(alertAcknowledgeCommand('all')).toEqual({ type: 'alertAcknowledge' });
    expect(alertSilenceCommand()).toEqual({ type: 'alertSilence' });
    expect(alertSilenceCommand(' bnwas ')).toEqual({ type: 'alertSilence', args: { id: 'bnwas' } });
    expect(serializeCommand(alertAcknowledgeCommand('gyroFailure'))).toBe('{"type":"alertAcknowledge","args":{"id":"gyroFailure"}}');
  });
  it('bnwasMode:value 為模式,dormant_s 夾在 180–720 取整;bnwasReset', () => {
    expect(bnwasModeCommand('on', 540)).toEqual({ type: 'bnwasMode', value: 'on', args: { dormant_s: 540 } });
    expect(bnwasModeCommand('auto')).toEqual({ type: 'bnwasMode', value: 'auto' });
    expect(bnwasModeCommand('on', 60)).toEqual({ type: 'bnwasMode', value: 'on', args: { dormant_s: 180 } });
    expect(bnwasModeCommand('on', 999.6)).toEqual({ type: 'bnwasMode', value: 'on', args: { dormant_s: 720 } });
    expect(() => bnwasModeCommand('loud' as 'on')).toThrow(RangeError);
    expect(serializeCommand(bnwasResetCommand())).toBe('{"type":"bnwasReset"}');
    expect(serializeCommand(bnwasModeCommand('off'))).toBe('{"type":"bnwasMode","value":"off"}');
  });
  it('injectFault:無參數送 value 字串;有參數送 args {name, args}(略過非有限值)', () => {
    expect(injectFaultCommand('gyroDrift')).toEqual({ type: 'injectFault', value: 'gyroDrift' });
    expect(injectFaultCommand('gyroDrift', { rate_degPerMin: -1, bad: Number.NaN })).toEqual({ type: 'injectFault', args: { name: 'gyroDrift', args: { rate_degPerMin: -1 } } });
    expect(injectFaultCommand('gpsJump', { distance_m: undefined })).toEqual({ type: 'injectFault', value: 'gpsJump' });
  });
});

describe('BNWAS 顯示與倒數', () => {
  it('關 / 待命 / 休眠倒數 / 第 1 階段 caution / 第 2 階段 alarm 閃爍 / 第 3 階段無倒數', () => {
    expect(bnwasDisplay(undefined)).toMatchObject({ level: 'off', resettable: false, label: '核心未回報' });
    expect(bnwasDisplay({ mode: 'off', active: false, stage: 0, dormant_s: 720 })).toMatchObject({ level: 'off', label: 'BNWAS 關' });
    expect(bnwasDisplay({ mode: 'auto', active: false, stage: 0, dormant_s: 720 })).toMatchObject({ level: 'standby', resettable: false });
    expect(bnwasDisplay({ mode: 'on', active: true, stage: 0, remaining_s: 125.2, dormant_s: 540 })).toEqual({ label: '休眠', countdown: '第 1 階段 2:06', level: 'normal', flashing: false, resettable: true });
    expect(bnwasDisplay({ mode: 'on', active: true, stage: 1, remaining_s: 9.5, dormant_s: 540 })).toMatchObject({ countdown: '第 2 階段 0:10', level: 'caution', flashing: false });
    expect(bnwasDisplay({ mode: 'on', active: true, stage: 2, remaining_s: 45.02, dormant_s: 540 })).toMatchObject({ countdown: '第 3 階段 0:46', level: 'alarm', flashing: true });
    expect(bnwasDisplay({ mode: 'on', active: true, stage: 3, dormant_s: 540 })).toMatchObject({ countdown: '', level: 'alarm', flashing: true, label: '第 3 階段 · 呼叫後備值班官/船長' });
    expect(formatCountdown(0)).toBe('0:00');
    expect(formatCountdown(-3)).toBe('0:00');
    expect(formatCountdown(600)).toBe('10:00');
  });
});

describe('由事件流推得警報歷史與 BNWAS 次數;事件文字', () => {
  const events: SimEvent[] = [
    ev('alertRaised', 480.02, { id: 'gyroFailure', priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', count: 1, raisedAt_s: 480.02, repeat: false }, { message: '電羅經失效' }),
    ev('bnwasStage', 540.02, { stage: 1, from: 0, reason: 'dormantElapsed' }),
    ev('alertRaised', 540.02, { id: 'bnwas', priority: 'caution', category: 'B', source: 'bnwas', state: 'activeUnacknowledged', count: 1, raisedAt_s: 540.02, repeat: false }, { message: 'BNWAS 值班警報' }),
    ev('alertSilenced', 545, { id: 'gyroFailure', priority: 'alarm', state: 'activeSilenced', count: 1, raisedAt_s: 480.02, silencedUntil_s: 575 }),
    ev('bnwasStage', 555.02, { stage: 2, from: 1, reason: 'notReset' }),
    ev('alertEscalated', 555.02, { id: 'bnwas', priority: 'alarm', state: 'activeUnacknowledged', count: 1, raisedAt_s: 540.02, from: 'caution', to: 'alarm', reason: 'source' }),
    ev('alertAcknowledged', 560, { id: 'gyroFailure', priority: 'alarm', state: 'activeAcknowledged', count: 1, raisedAt_s: 480.02, ackTime_s: 79.98 }),
    ev('bnwasReset', 560, { fromStage: 2, by: 'alertAcknowledge' }, { source: 'instructor' }),
    ev('alertRectified', 560.02, { id: 'bnwas', priority: 'alarm', state: 'normal', count: 1, raisedAt_s: 540.02 }),
    ev('alertRectified', 700, { id: 'gyroFailure', priority: 'alarm', state: 'normal', count: 1, raisedAt_s: 480.02 }),
  ];

  it('alertHistory:每次成立一筆,含確認耗時、靜音、升級、解除', () => {
    const h = alertHistory(events);
    expect(h.map((x) => x.id)).toEqual(['gyroFailure', 'bnwas']);
    expect(h[0]).toMatchObject({ priority: 'alarm', acknowledgedAt_s: 560, ackTime_s: 79.98, silences: 1, rectifiedAt_s: 700, open: false, text: '電羅經失效' });
    // BNWAS 由 alertAcknowledge 的重設清除(fromStage 2):重設時刻即確認時刻(與核心 summary.alerts.records 一致)
    expect(h[1]).toMatchObject({ priority: 'alarm', escalated: true, open: false, rectifiedAt_s: 560.02, acknowledgedAt_s: 560 });
    expect(h[1]!.ackTime_s).toBeCloseTo(19.98, 6);
    // 休眠中的重設(fromStage 0)不影響紀錄;未經重設即解除(例如切 off)仍為未確認
    const off = alertHistory([
      ev('alertRaised', 10, { id: 'bnwas', priority: 'caution', raisedAt_s: 10, repeat: false }),
      ev('bnwasReset', 11, { fromStage: 0, by: 'bnwasReset' }),
      ev('alertRectified', 12, { id: 'bnwas', state: 'normal' }),
    ]);
    expect(off[0]!.ackTime_s).toBeUndefined();
  });

  it('再成立(repeat)併入同一筆;中途連線缺 alertRaised 時補一筆', () => {
    const h = alertHistory([
      ev('alertRaised', 10, { id: 'echoSounderFailure', priority: 'warning', raisedAt_s: 10, repeat: false }),
      ev('alertRectified', 20, { id: 'echoSounderFailure', state: 'rectifiedUnacknowledged' }),
      ev('alertRaised', 30, { id: 'echoSounderFailure', priority: 'warning', raisedAt_s: 30, repeat: true, count: 2 }),
      ev('alertAcknowledged', 33, { id: 'gpsFailure', priority: 'alarm', state: 'activeAcknowledged', raisedAt_s: 1 }),
    ]);
    expect(h).toHaveLength(2);
    expect(h[0]).toMatchObject({ id: 'echoSounderFailure', repeat: true, raisedAt_s: 30, open: true });
    expect(h[0]!.rectifiedAt_s).toBeUndefined();
    expect(h[1]).toMatchObject({ id: 'gpsFailure', ackTime_s: 32, text: 'GNSS 失效' });
  });

  it('bnwasCounts 與事件文字/嚴重度', () => {
    expect(bnwasCounts(events)).toEqual({ stage1: 1, stage2: 1, stage3: 0, resets: 1 });
    // 與核心 MetricsCollector 一致:只計階段 ≥ 1 的重設(休眠中按 BNWAS 重設鍵仍發事件,但不計次)
    const dormant = [
      ev('bnwasReset', 100, { fromStage: 0, by: 'bnwasReset' }, { source: 'instructor' }),
      ev('bnwasReset', 200, { fromStage: 0, by: 'bnwasReset' }, { source: 'instructor' }),
      ev('bnwasStage', 800, { stage: 1, from: 0, reason: 'dormantElapsed' }),
      ev('bnwasReset', 805, { fromStage: 1, by: 'rudder' }, { source: 'instructor' }),
    ];
    expect(bnwasCounts(dormant)).toEqual({ stage1: 1, stage2: 0, stage3: 0, resets: 1 });
    expect(eventLabel(events[0]!)).toBe('警報成立:電羅經失效');
    expect(eventSeverity(events[0]!)).toBe('alarm');
    expect(eventLabel(events[5]!)).toBe('警報升級:BNWAS 值班警報 注意 → 警報(來源升級)');
    expect(eventSeverity(events[4]!)).toBe('alarm');
    expect(eventSeverity(events[1]!)).toBe('warning');
    expect(eventLabel(events[6]!)).toBe('警報確認:電羅經失效 +80.0 s');
    expect(eventLabel(events[7]!)).toBe('BNWAS 重設(alertAcknowledge) 自第 2 階段');
    expect(eventLabel(events[4]!)).toBe('BNWAS 第 2 階段 · 駕駛台聲響(未重設)');
    const tripped = parseSimEvent({ kind: 'autopilotTripped', tick: 24000, t: 480, source: 'engine', data: { reason: 'headingInvalid', fault: 'gyroFailure' } })!;
    expect(eventLabel(tripped)).toBe('自動舵脫開(電羅經無效)');
    const rejected = parseSimEvent({ kind: 'commandRejected', tick: 1, t: 0.02, source: 'instructor', data: { command: 'autopilot', reason: 'autopilotUnavailable' } })!;
    expect(eventLabel(rejected)).toBe('指令被拒 autopilot(自動舵不可用(電羅經或自動舵失效))');
    const mode = parseSimEvent({ kind: 'bnwasModeChanged', tick: 1, t: 0.02, source: 'instructor', data: { from: 'off', to: 'on', fromStage: 0, dormant_s: 540 } })!;
    expect(eventLabel(mode)).toBe('BNWAS 模式 關 → 開(休眠 540 s)');
  });
});

describe('真值 vs 指示值', () => {
  it('E08:電羅經列因故障/無效標紅,位置一致;無 truth 時空陣列', () => {
    const s = parseState(JSON.parse(JSON.stringify(e08Frame)))!.state;
    const rows = sensorComparison(s);
    const byKey = Object.fromEntries(rows.map((r) => [r.key, r]));
    expect(byKey['heading']).toMatchObject({ indicated: '028.3°', truth: '037.2°', error: '-8.9°', faulty: true, invalid: true });
    expect(byKey['magnetic']).toMatchObject({ indicated: '038.2°', error: '+1.0°', faulty: false });
    expect(byKey['position']).toMatchObject({ error: '0 m', faulty: false });
    expect(byKey['stw']!.faulty).toBe(false);
    expect(sensorComparison(defaultState('FSB1'))).toEqual([]);
  });
  it('航向類欄位跨 0/360:359.96° 顯示 000.0°(不顯示 360.0°)', () => {
    const s = { ...defaultState('FSB1'), heading: 359.96, cog: 359.97, sensors: { headingValid: true, positionValid: true, depthValid: true, stwValid: true, magneticHeading: 359.99 }, truth: { heading: 0.02, pos: { lat: 0, lon: 0, x: 0, y: 0 }, cog: 359.951, sog: 0, stw: 0, depthBelowKeel: 0, headingError_deg: -0.06, positionError_m: 0 } };
    const byKey = Object.fromEntries(sensorComparison(s).map((r) => [r.key, r]));
    expect(byKey['heading']).toMatchObject({ indicated: '000.0°', truth: '000.0°', error: '-0.1°' });
    expect(byKey['magnetic']).toMatchObject({ indicated: '000.0°', truth: '000.0°' });
    expect(byKey['cog']).toMatchObject({ indicated: '000.0°', truth: '000.0°' });
  });
  it('GNSS 跳點:位置差距與故障標紅', () => {
    const s = { ...defaultState('FSB1'), pos: { lat: 0, lon: 0, x: 500, y: 0 }, faults: ['gpsJump'], truth: { heading: 0, pos: { lat: 0, lon: 0, x: 0, y: 0 }, cog: 0, sog: 0, stw: 0, depthBelowKeel: 0, headingError_deg: 0, positionError_m: 500 } };
    const pos = sensorComparison(s).find((r) => r.key === 'position')!;
    expect(pos).toMatchObject({ error: '500 m', faulty: true, invalid: false });
  });
});

describe('summary.alerts 與故障應變的警報確認', () => {
  it('解析設計文件第 5 節範例;舊 summary 無 alerts 為 null', () => {
    const sum = parseAssessmentSummary({
      version: 1,
      alerts: {
        raised: 2, acknowledged: 1, unacknowledged: 1, escalations: 1, silences: 0, meanAckTime_s: 4.98, maxAckTime_s: 4.98,
        records: [
          { id: 'gyroFailure', priority: 'alarm', raisedAt_s: 480.02, acknowledgedAt_s: 485.0, ackTime_s: 4.98, rectifiedAt_s: 560.02, escalated: false, silences: 0 },
          { id: 'bnwas', priority: 'alarm', raisedAt_s: 540.02, escalated: true, silences: 0 },
        ],
        bnwas: { stage1: 1, stage2: 1, stage3: 0, resets: 0 },
      },
      faultResponses: [{ fault: 'gyroFailure', injectedAt_s: 480, alertAckAt_s: 485, alertAckDt_s: 5, acknowledgements: [] }],
    })!;
    expect(sum.alerts).toMatchObject({ raised: 2, unacknowledged: 1, meanAckTime_s: 4.98, bnwas: { stage1: 1, stage2: 1, stage3: 0, resets: 0 } });
    expect(sum.alerts!.records[1]).toEqual({ id: 'bnwas', priority: 'alarm', raisedAt_s: 540.02, escalated: true, silences: 0 });
    expect(sum.faultResponses[0]).toMatchObject({ alertAckAt_s: 485, alertAckDt_s: 5 });
    expect(parseAssessmentSummary({ version: 1 })!.alerts).toBeNull();
  });
});

describe('警報聲響(假 AudioContext;不依賴實際音訊)', () => {
  function fakeCtx() {
    const started: Array<{ freq: number; at: number }> = [];
    const ctx: AudioContextLike = {
      currentTime: 0,
      state: 'running',
      destination: {},
      resume: () => Promise.resolve(),
      createOscillator() {
        const osc = { type: '', frequency: { value: 0 }, connect: () => undefined, start: (t?: number) => started.push({ freq: osc.frequency.value, at: t ?? 0 }), stop: () => undefined };
        return osc;
      },
      createGain: () => ({ gain: { value: 1, setValueAtTime: () => undefined, linearRampToValueAtTime: () => undefined }, connect: () => undefined }),
    };
    return { ctx, started };
  }

  it('soundPriority:只看 audible,caution 不發聲', () => {
    expect(soundPriority([alert({ priority: 'warning' }), alert({ priority: 'alarm', audible: false })])).toBe('warning');
    expect(soundPriority([alert({ priority: 'caution' })])).toBeNull();
    expect(soundPriority(undefined)).toBeNull();
  });

  it('解鎖前不建立 AudioContext;解鎖後 alarm 三短聲;關閉聲響即停;無聲響警報時停止', () => {
    const { ctx, started } = fakeCtx();
    let created = 0;
    const sounder = new AlertSounder({ storageKey: null, contextFactory: () => (created++, ctx) });
    sounder.update([alert({ priority: 'alarm' })]);
    expect(sounder.pattern).toBe('alarm');
    expect(sounder.sounding).toBe(false);
    expect(created).toBe(0);
    sounder.unlock();
    expect(created).toBe(1);
    expect(sounder.sounding).toBe(true);
    expect(started.map((s) => s.freq)).toEqual([880, 880, 880]);
    sounder.setMuted(true);
    expect(sounder.sounding).toBe(false);
    sounder.setMuted(false);
    expect(sounder.sounding).toBe(true);
    sounder.update([alert({ priority: 'alarm', state: 'activeSilenced', audible: false })]);
    expect(sounder.sounding).toBe(false);
    sounder.dispose();
  });

  it('沒有 Web Audio 的環境(contextFactory 回 null)不丟例外', () => {
    const sounder = new AlertSounder({ storageKey: null, contextFactory: () => null });
    sounder.update([alert({})]);
    expect(() => sounder.unlock()).not.toThrow();
    expect(sounder.sounding).toBe(false);
  });
});

describe('故障目錄同步 Contracts/fault-catalogue.json', () => {
  it('建置環境讀到目錄:感測器故障為 modelled,帶警報優先序與參數', () => {
    const { faults, source } = faultCatalogue();
    expect(source).toBe('contracts');
    const by = Object.fromEntries(faults.map((f) => [f.name, f]));
    expect(by['gyroDrift']).toMatchObject({ modelled: true, label: '電羅經漂移', args: { rate_degPerMin: 1 } });
    expect(by['gyroFailure']).toMatchObject({ modelled: true, alert: 'alarm' });
    expect(by['logFailure']!.args).toEqual({ bias_kn: null, factor: null });
    expect(by['radarFailure']!.modelled).toBe(false);
  });
  it('parseFaultCatalogueJson 對壞資料寬容', () => {
    expect(parseFaultCatalogueJson(null)).toEqual([]);
    expect(parseFaultCatalogueJson({ faults: [{ name: '' }, 3, { name: 'x', modelled: true }] })).toEqual([{ name: 'x', label: 'x', modelled: true }]);
  });
});

describe('示範模式的警報與 BNWAS 循環', () => {
  function run(sim: DemoSim, seconds: number): void {
    const n = Math.round(seconds / DEMO_DT);
    for (let i = 0; i < n; i++) sim.step();
  }
  const kinds = (sim: DemoSim) => sim.takeEvents().filter((e) => e.source !== 'traffic' && e.source !== 'timeline').map((e) => e.kind);

  it('預設關閉(既有測試的事件序列不變);DemoSource 預設開', () => {
    const sim = new DemoSim({ initialSpeed_kn: 8 });
    run(sim, 30);
    expect(sim.output().alerts).toBeUndefined();
    expect(sim.output().bnwas).toBeUndefined();
    expect(sim.output().truth).toBeDefined();
  });

  it('示範假警報(航向監視 warning)與 BNWAS 第 1/2 階段;確認、駕駛台操作重設 BNWAS;bnwas 警報直接解除', () => {
    const sim = new DemoSim({ initialSpeed_kn: 8, alerts: true });
    run(sim, 5);
    let out = sim.output();
    expect(out.alerts!.map((a) => a.id)).toEqual(['headingMonitor']);
    expect(out.alerts![0]).toMatchObject({ priority: 'warning', state: 'activeUnacknowledged', audible: true, number: 13 });
    expect(out.bnwas).toMatchObject({ mode: 'on', active: true, stage: 0, dormant_s: 180 });
    expect(out.bnwas!.remaining_s).toBeCloseTo(180 - DEMO_BNWAS_PRE_ELAPSED_S - 5, 0);
    run(sim, 21); // t = 26 s:第 1 階段 10 s、第 2 階段 25 s
    out = sim.output();
    expect(out.bnwas!.stage).toBe(2);
    expect(out.alerts!.map((a) => `${a.id}:${a.priority}`)).toEqual(['bnwas:alarm', 'headingMonitor:warning']);
    expect(kinds(sim)).toEqual(['alertRaised', 'bnwasStage', 'alertRaised', 'bnwasStage', 'alertEscalated']);
    sim.apply({ type: 'alertAcknowledge', args: { id: 'headingMonitor' } });
    out = sim.output();
    expect(out.alerts!.find((a) => a.id === 'headingMonitor')).toMatchObject({ state: 'activeAcknowledged', audible: false });
    expect(out.bnwas!.stage).toBe(0); // alertAcknowledge 是駕駛台操作 → 重設 BNWAS
    run(sim, 0.1);
    out = sim.output();
    expect(out.alerts!.map((a) => a.id)).toEqual(['headingMonitor']);
    expect(kinds(sim)).toEqual(['alertAcknowledged', 'bnwasReset', 'alertRectified']); // 與核心相同順序:先確認事件、再視為駕駛台操作重設 BNWAS
    sim.apply({ type: 'alertAcknowledge', value: 'nope' });
    expect(sim.takeEvents().find((e) => e.kind === 'commandRejected')!.data).toMatchObject({ reason: 'unknownAlert', command: 'alertAcknowledge' });
  });

  it('靜音 30 s 後重響;假警報時段結束 → 已解除未確認;確認後移除', () => {
    const sim = new DemoSim({ initialSpeed_kn: 8, alerts: true });
    run(sim, 6);
    sim.apply({ type: 'alertSilence' }); // 也重設 BNWAS(第 1 階段前,不發 bnwasReset 事件)
    expect(sim.output().alerts![0]).toMatchObject({ state: 'activeSilenced', audible: false, silencedUntil_s: 36 });
    run(sim, 30.1);
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.state).toBe('activeUnacknowledged');
    run(sim, 120); // t ≈ 156 s:假警報時段結束
    expect(sim.output().alerts!.find((a) => a.id === 'headingMonitor')!.state).toBe('rectifiedUnacknowledged');
    expect(kinds(sim)).toEqual(['alertRaised', 'alertSilenced', 'alertReannunciated', 'alertRectified']);
    sim.apply({ type: 'alertAcknowledge' });
    expect(sim.output().alerts).toBeUndefined();
    expect(sim.takeEvents().find((e) => e.kind === 'alertAcknowledged')!.data).toMatchObject({ state: 'normal' });
  });

  it('BNWAS 循環:第 1(10 s)→ 2(25 s)→ 3(115 s)階段,30 s 後示範自動重設,再休眠 180 s', () => {
    const sim = new DemoSim({ initialSpeed_kn: 8, alerts: true });
    run(sim, 146);
    const ev = sim.takeEvents().filter((e) => e.kind.startsWith('bnwas'));
    expect(ev.map((e) => `${e.kind}@${Math.round(e.t)}`)).toEqual(['bnwasStage@10', 'bnwasStage@25', 'bnwasStage@115', 'bnwasReset@145']);
    expect(ev[3]!.data).toMatchObject({ fromStage: 3, by: 'demoAutoReset' });
    expect(sim.output().bnwas!.stage).toBe(0);
    expect(sim.output().bnwas!.remaining_s).toBeCloseTo(179, 0);
  });

  it('BNWAS 未作動(off、或 auto 而自動舵未啟用)時 bnwasReset 不發事件、不改計時(與核心 Bnwas.Reset 一致)', () => {
    const sim = new DemoSim({ alerts: true });
    sim.apply({ type: 'bnwasMode', value: 'off' });
    sim.step();
    sim.takeEvents();
    sim.apply({ type: 'bnwasReset' });
    sim.apply({ type: 'rudder', value: 5 });
    expect(sim.takeEvents().filter((e) => e.kind === 'bnwasReset')).toHaveLength(0);
    sim.apply({ type: 'bnwasMode', value: 'auto' });
    sim.step();
    sim.takeEvents();
    sim.apply({ type: 'bnwasReset' });
    expect(sim.takeEvents().filter((e) => e.kind === 'bnwasReset')).toHaveLength(0);
    // 作動中(on)休眠期按重設鍵仍一律發事件
    sim.apply({ type: 'bnwasMode', value: 'on' });
    sim.step();
    sim.takeEvents();
    sim.apply({ type: 'bnwasReset' });
    expect(sim.takeEvents().filter((e) => e.kind === 'bnwasReset').map((e) => e.data)).toEqual([{ fromStage: 0, by: 'bnwasReset' }]);
  });

  it('gyroFailure:自動舵脫開(autopilotTripped + steeringModeChanged)、指示航向保持、不可再啟用;清除後恢復', () => {
    const sim = new DemoSim({ initialHeading: 30, initialSpeed_kn: 10, alerts: true });
    sim.apply({ type: 'autopilot', args: { enabled: true, heading: 30 } });
    run(sim, 2);
    sim.takeEvents();
    sim.apply({ type: 'injectFault', value: 'gyroFailure' });
    const evs = sim.takeEvents().map((e) => `${e.source}:${e.kind}`);
    expect(evs).toEqual(['instructor:faultInjected', 'instructor:phaseChanged', 'engine:autopilotTripped', 'engine:steeringModeChanged']);
    sim.apply({ type: 'rudder', value: 10 });
    run(sim, 20);
    const out = sim.output();
    expect(out.sensors!.headingValid).toBe(false);
    expect(out.autopilot).toMatchObject({ enabled: false, available: false });
    expect(Math.abs(out.truth!.headingError_deg)).toBeGreaterThan(2);
    expect(out.alerts!.some((a) => a.id === 'gyroFailure' && a.priority === 'alarm')).toBe(true);
    sim.takeEvents();
    sim.apply({ type: 'autopilot', args: { enabled: true } });
    expect(sim.takeEvents().find((e) => e.kind === 'commandRejected')!.data).toMatchObject({ reason: 'autopilotUnavailable' });
    sim.apply({ type: 'clearFault', value: 'gyroFailure' });
    run(sim, 0.1);
    expect(sim.output().sensors!.headingValid).toBe(true);
    expect(sim.output().alerts!.find((a) => a.id === 'gyroFailure')!.state).toBe('rectifiedUnacknowledged');
  });

  it('gyroDrift −1°/min:自動舵保持指示航向,真航向往右偏(E08);gpsJump 位置差 500 m', () => {
    const sim = new DemoSim({ initialHeading: 29, initialSpeed_kn: 11, current: { set: 0, drift: 0 }, alerts: true });
    sim.apply({ type: 'autopilot', args: { enabled: true, heading: 29 } });
    sim.apply({ type: 'injectFault', args: { name: 'gyroDrift', args: { rate_degPerMin: -1 } } });
    sim.apply({ type: 'injectFault', value: 'gpsJump' });
    run(sim, 240);
    const out = sim.output();
    expect(Math.abs(out.heading - 29)).toBeLessThan(1.5); // 示範自動舵追斜坡有約 1° 落後
    expect(out.truth!.heading).toBeGreaterThan(31.5);
    expect(out.truth!.headingError_deg).toBeCloseTo(-4, 0);
    expect(out.truth!.positionError_m).toBeCloseTo(500, 0);
    expect(out.sensors!.headingValid).toBe(true);
    expect(parseState(JSON.parse(JSON.stringify(out)))!.issues).toEqual([]);
  });

  it('bnwasMode:模式切換事件、休眠期夾限;off 時停止作動', () => {
    const sim = new DemoSim({ alerts: true });
    sim.apply({ type: 'bnwasMode', value: 'auto', args: { dormant_s: 60 } });
    expect(sim.takeEvents().find((e) => e.kind === 'bnwasModeChanged')!.data).toMatchObject({ from: 'on', to: 'auto', dormant_s: 180 });
    run(sim, 1);
    expect(sim.output().bnwas).toMatchObject({ mode: 'auto', active: false, stage: 0 });
    sim.apply({ type: 'bnwasMode', value: 'off' });
    run(sim, 1);
    expect(sim.output().bnwas).toEqual({ mode: 'off', active: false, stage: 0, dormant_s: 180 });
  });
});
