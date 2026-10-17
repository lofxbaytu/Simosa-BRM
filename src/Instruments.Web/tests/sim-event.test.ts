// 核心事件(SimEvent)解析與文字化:交通/時間表/評估事件的 isSimEvent、預設來源、推送框、eventLabel、嚴重度、事件保存。
import { describe, expect, it } from 'vitest';
import { EventStore, defaultSourceFor, eventFromFrame, eventLabel, eventSeverity, isSimEvent, parseSimEvent, voiceScriptData } from '../src/lib/sim-event.js';
import type { SimEvent } from '../src/types/event.js';

describe('isSimEvent / parseSimEvent', () => {
  it('舊 TrafficEvent(無 source/seq/visible)解析後補預設:來源 traffic、seq -1、visible false', () => {
    const raw = { kind: 'cpaAlarm', tick: 6000, t: 120, targetId: 'T1', message: 'CPA 0.4 nm', range_nm: 2.1, cpa_nm: 0.4, tcpa_min: 8.5 };
    expect(isSimEvent(raw)).toBe(true);
    const e = parseSimEvent(raw)!;
    expect(e).toEqual({ kind: 'cpaAlarm', tick: 6000, t: 120, source: 'traffic', visible: false, seq: -1, targetId: 'T1', message: 'CPA 0.4 nm', range_nm: 2.1, cpa_nm: 0.4, tcpa_min: 8.5 });
  });

  it('時間表事件含 source/itemId/phase/visible/seq/data;缺 t 以 tick × dt 推得', () => {
    const e = parseSimEvent({ kind: 'faultInjected', tick: 21000, source: 'timeline', itemId: 'steeringFail', phase: 'degraded', visible: false, seq: 7, data: { fault: 'steeringGear', args: null } })!;
    expect(e.t).toBeCloseTo(420);
    expect(e.source).toBe('timeline');
    expect(e.itemId).toBe('steeringFail');
    expect(e.phase).toBe('degraded');
    expect(e.seq).toBe(7);
    expect(e.data).toEqual({ fault: 'steeringGear', args: null });
  });

  it('評估/教官事件的預設來源由 kind 推得;未知 kind 為 engine', () => {
    expect(defaultSourceFor('xtdExceeded')).toBe('assessment');
    expect(defaultSourceFor('acknowledge')).toBe('instructor');
    expect(defaultSourceFor('timelineTriggered')).toBe('timeline');
    expect(defaultSourceFor('grounding')).toBe('engine');
    expect(defaultSourceFor('somethingNew')).toBe('engine');
    expect(parseSimEvent({ kind: 'somethingNew', t: 3 })!.source).toBe('engine');
  });

  it('不是事件的物件回傳 null', () => {
    expect(isSimEvent(null)).toBe(false);
    expect(isSimEvent({ tick: 1 })).toBe(false);
    expect(isSimEvent({ kind: 'x' })).toBe(false);
    expect(parseSimEvent('{"kind":"x"}')).toBeNull();
  });

  it('eventFromFrame 只接受 {"type":"event","event":{…}}', () => {
    expect(eventFromFrame({ type: 'event', event: { kind: 'message', tick: 50, t: 1, source: 'timeline', data: { to: 'all', text: 'hi', severity: 'info' } } })?.kind).toBe('message');
    expect(eventFromFrame({ type: 'state', state: {} })).toBeNull();
    expect(eventFromFrame({ type: 'event', event: 'bad' })).toBeNull();
  });
});

function ev(kind: string, extra: Partial<SimEvent> = {}): SimEvent {
  return { kind, tick: 0, t: 0, source: 'engine', visible: false, seq: 0, ...extra };
}

describe('eventLabel / eventSeverity', () => {
  it('交通、時間表、評估、教官事件各有繁體中文一句話', () => {
    expect(eventLabel(ev('cpaAlarm', { targetId: 'T1', cpa_nm: 0.42, tcpa_min: 8.5 }))).toBe('CPA 警報 T1 CPA 0.42 nm TCPA 8.5 min');
    expect(eventLabel(ev('timelineTriggered', { itemId: 'steeringFail', data: { manual: true } }))).toBe('時間表觸發 steeringFail(手動)');
    expect(eventLabel(ev('faultInjected', { data: { fault: 'steeringGear' } }))).toBe('故障注入 steeringGear');
    expect(eventLabel(ev('environmentChange', { data: { wind: { trueSpeed: 20, trueDir: 10 }, ramp_s: 180 } }))).toBe('環境改變 風 20 kn/010(180 s 漸變)');
    expect(eventLabel(ev('voiceScript', { data: { role: 'ecr', channel: 'phone', text: '駕駛台,機控室。' } }))).toBe('語音 機控室(電話):駕駛台,機控室。');
    expect(eventLabel(ev('message', { data: { to: 'all', text: '注意', severity: 'warning' } }))).toBe('訊息(全體):注意');
    expect(eventLabel(ev('phaseChanged', { data: { from: 'normal', to: 'degraded' } }))).toBe('階段 normal → degraded');
    expect(eventLabel(ev('xtdExceeded', { data: { xtd_m: 74.2, limit_m: 50, legIndex: 1 } }))).toBe('XTD 超限 74 m(限 50 m)');
    expect(eventLabel(ev('zoneEntered', { data: { zoneId: 'shoal_N', type: 'noGo' } }))).toBe('進入區域 shoal_N(禁區)');
    expect(eventLabel(ev('requiredEventMet', { itemId: 'notifyEcr', data: { dt_s: 82, within_s: 120, by: 'acknowledge:notifyEcr' } }))).toBe('必要事件達成 notifyEcr 82 s(限 120 s,acknowledge:notifyEcr)');
    expect(eventLabel(ev('hardFail', { itemId: 'cpaHard', data: { rule: 'cpa', value: 0.15, limit: 0.2 } }))).toBe('硬性失敗 cpaHard cpa 0.15 / 0.20');
    expect(eventLabel(ev('acknowledge', { data: { kind: 'notifyEcr', role: 'master', note: '已通知' } }))).toBe('確認:通知機控室(船長) 已通知');
    expect(eventLabel(ev('timelineManual', { itemId: 'x', data: { ok: false } }))).toBe('手動觸發 x — 核心拒絕');
    expect(eventLabel(ev('exerciseEnded', { data: { reason: '到達泊位' } }))).toBe('練習結束:到達泊位');
    expect(eventLabel(ev('futureKind', { itemId: 'a', message: 'm' }))).toBe('futureKind a m');
  });

  it('嚴重度:hardFail/collision/grounding 為 alarm,cpaAlarm/faultInjected 為 warning,message 依 severity', () => {
    expect(eventSeverity(ev('hardFail'))).toBe('alarm');
    expect(eventSeverity(ev('collision'))).toBe('alarm');
    expect(eventSeverity(ev('grounding'))).toBe('alarm');
    expect(eventSeverity(ev('cpaAlarm'))).toBe('warning');
    expect(eventSeverity(ev('faultInjected'))).toBe('warning');
    expect(eventSeverity(ev('message', { data: { severity: 'alarm' } }))).toBe('alarm');
    expect(eventSeverity(ev('message'))).toBe('info');
    expect(eventSeverity(ev('timelineTriggered'))).toBe('info');
  });

  it('voiceScriptData 補預設角色/頻道', () => {
    expect(voiceScriptData(ev('voiceScript', { data: { text: 'x', file: 'a.ogg', lang: 'zh-TW', durationEstimate_s: 4 } }))).toEqual({ role: 'narrator', channel: 'bridge', text: 'x', file: 'a.ogg', lang: 'zh-TW', durationEstimate_s: 4 });
    expect(voiceScriptData(ev('message'))).toBeNull();
  });
});

describe('EventStore', () => {
  it('保留最近 N 筆;last/count 依 kind 與 itemId/targetId', () => {
    const store = new EventStore(3);
    store.push(ev('cpaAlarm', { t: 1, targetId: 'T1' }));
    store.push(ev('faultInjected', { t: 2, itemId: 'a' }));
    store.push(ev('faultInjected', { t: 3, itemId: 'b' }));
    store.push(ev('cpaClear', { t: 4, targetId: 'T1' }));
    expect(store.length).toBe(3);
    expect(store.all()[0]!.kind).toBe('faultInjected');
    expect(store.last('faultInjected')?.itemId).toBe('b');
    expect(store.last('faultInjected', { itemId: 'a' })?.t).toBe(2);
    expect(store.count('faultInjected')).toBe(2);
    expect(store.count('cpaAlarm')).toBe(0);
    store.clear();
    expect(store.length).toBe(0);
  });
});
