import { describe, expect, it } from 'vitest';
import { defaultState, parseState } from '../src/lib/state-parser.js';

const full = {
  t: 12.34,
  tick: 617,
  shipId: 'FSB2',
  pos: { lat: 23.8, lon: 120.1, x: 10.5, y: -20.25 },
  heading: 275.5,
  cog: 272.1,
  sog: 9.8,
  stw: 10.1,
  rot: -3.2,
  u: 5.2,
  v: -0.3,
  r: -0.00093,
  drift: 3.3,
  rudder: -4.5,
  rudderOrder: -5,
  rpm: 103,
  rpmOrder: 103,
  telegraph: 'FAH',
  thruster: { order: 0.2, actual: 0.1 },
  depthBelowKeel: 4.2,
  waterDepth: 12,
  squat: 0.3,
  wind: { trueSpeed: 20, trueDir: 40, relSpeed: 25.1, relDir: 110 },
  current: { set: 265, drift: 2.5 },
  loading: 'ballast',
  draft: { fore: 4.4, aft: 6.5 },
  engine: { state: 'running', startsRemaining: 11, load_pct: 48.5 },
  faults: ['gyro_drift'],
  flags: { frozen: false, aground: false, collision: false },
};

describe('parseState(完整訊息)', () => {
  it('所有欄位原樣解析,無問題清單', () => {
    const r = parseState(full);
    expect(r).not.toBeNull();
    expect(r!.issues).toEqual([]);
    const s = r!.state;
    expect(s.shipId).toBe('FSB2');
    expect(s.heading).toBeCloseTo(275.5);
    expect(s.telegraph).toBe('FAH');
    expect(s.thruster).toEqual({ order: 0.2, actual: 0.1 });
    expect(s.wind.relDir).toBe(110);
    expect(s.draft).toEqual({ fore: 4.4, aft: 6.5 });
    expect(s.engine).toEqual({ state: 'running', startsRemaining: 11, load_pct: 48.5 });
    expect(s.faults).toEqual(['gyro_drift']);
    expect(s.flags).toEqual({ frozen: false, aground: false, collision: false });
  });
  it('接受 JSON 字串與信封', () => {
    expect(parseState(JSON.stringify(full))!.state.tick).toBe(617);
    expect(parseState({ type: 'state', state: full })!.state.tick).toBe(617);
    expect(parseState({ data: full })!.state.sog).toBeCloseTo(9.8);
  });
  it('航向超範圍會正規化', () => {
    const s = parseState({ ...full, heading: 370, cog: -10 })!.state;
    expect(s.heading).toBe(10);
    expect(s.cog).toBe(350);
  });
});

describe('parseState(缺欄位防呆)', () => {
  it('缺必要欄位時以預設值補上並列入 issues', () => {
    const r = parseState({ heading: 90, pos: { x: 1, y: 2 } });
    expect(r).not.toBeNull();
    const { state, issues } = r!;
    expect(state.heading).toBe(90);
    expect(state.pos).toEqual({ lat: 0, lon: 0, x: 1, y: 2 });
    expect(state.sog).toBe(0);
    expect(state.telegraph).toBe('STOP');
    expect(state.wind).toEqual({ trueSpeed: 0, trueDir: 0 });
    expect(issues).toEqual(expect.arrayContaining(['pos.lat', 'pos.lon', 'sog', 'telegraph', 'wind', 'current', 'loading', 't', 'tick']));
    expect(issues).not.toContain('heading');
  });
  it('缺欄位時保留前一筆狀態的值', () => {
    const prev = { ...defaultState('FSB2'), sog: 7.7, telegraph: 'HAH' as const };
    const r = parseState({ heading: 1, pos: { lat: 0, lon: 0, x: 0, y: 0 } }, prev);
    expect(r!.state.sog).toBe(7.7);
    expect(r!.state.telegraph).toBe('HAH');
    expect(r!.state.shipId).toBe('FSB2');
  });
  it('型別錯誤的欄位視為缺少', () => {
    const r = parseState({ ...full, sog: 'fast', telegraph: 'WARP', shipId: 'XYZ', flags: { frozen: 'yes' } });
    expect(r!.state.sog).toBe(0);
    expect(r!.state.telegraph).toBe('STOP');
    expect(r!.state.shipId).toBe('FSB1');
    expect(r!.state.flags?.frozen).toBe(false);
    expect(r!.issues).toEqual(expect.arrayContaining(['sog', 'telegraph', 'shipId', 'flags.frozen']));
  });
  it('數字字串可接受', () => {
    expect(parseState({ ...full, sog: '3.5' })!.state.sog).toBe(3.5);
  });
  it('faults 非陣列時忽略', () => {
    const r = parseState({ ...full, faults: 'bad' });
    expect(r!.state.faults).toBeUndefined();
    expect(r!.issues).toContain('faults');
  });
  it('非狀態訊息回傳 null', () => {
    expect(parseState('not json')).toBeNull();
    expect(parseState(42)).toBeNull();
    expect(parseState(null)).toBeNull();
    expect(parseState({ type: 'ack' })).toBeNull();
    expect(parseState([1, 2])).toBeNull();
  });
});
