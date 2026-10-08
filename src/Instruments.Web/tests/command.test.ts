import { describe, expect, it } from 'vitest';
import {
  autopilotCommand,
  isValidCommand,
  rudderCommand,
  serializeCommand,
  simpleCommand,
  telegraphCommand,
  thrusterCommand,
  timeScaleCommand,
} from '../src/lib/command.js';

describe('指令建構', () => {
  it('舵令限幅在船的最大舵角並取一位小數', () => {
    expect(rudderCommand(-20, 35)).toEqual({ type: 'rudder', value: -20 });
    expect(rudderCommand(50, 35)).toEqual({ type: 'rudder', value: 35 });
    expect(rudderCommand(-80, 70)).toEqual({ type: 'rudder', value: -70 });
    expect(rudderCommand(12.345, 35)).toEqual({ type: 'rudder', value: 12.3 });
    expect(rudderCommand(-0.01, 35).value).toBe(0);
    expect(rudderCommand(10, 35, 'NFU')).toEqual({ type: 'rudder', value: 10, args: { mode: 'NFU' } });
  });
  it('車鐘位置必須在列舉內', () => {
    expect(telegraphCommand('HAH')).toEqual({ type: 'telegraph', value: 'HAH' });
    expect(() => telegraphCommand('WARP' as never)).toThrow(RangeError);
  });
  it('側推限幅 −1 至 1', () => {
    expect(thrusterCommand(1.5)).toEqual({ type: 'thruster', value: 1 });
    expect(thrusterCommand(-0.456)).toEqual({ type: 'thruster', value: -0.46 });
    expect(thrusterCommand(0).value).toBe(0);
  });
  it('自動舵參數正規化', () => {
    expect(autopilotCommand({ enabled: true, heading: 275, rotLimit: 15 })).toEqual({
      type: 'autopilot',
      args: { enabled: true, heading: 275, rotLimit: 15 },
    });
    expect(autopilotCommand({ enabled: true, heading: 370, rotLimit: -20 }).args).toEqual({ enabled: true, heading: 10, rotLimit: 20 });
    expect(autopilotCommand({ enabled: false }).args).toEqual({ enabled: false });
  });
  it('教官簡單指令與時間倍率', () => {
    expect(simpleCommand('freeze')).toEqual({ type: 'freeze' });
    expect(timeScaleCommand(50)).toEqual({ type: 'timeScale', value: 10 });
  });
});

describe('serializeCommand(序列化)', () => {
  it('與 command.schema.json 的範例一致', () => {
    expect(serializeCommand({ type: 'rudder', value: -20 })).toBe('{"type":"rudder","value":-20}');
    expect(serializeCommand({ type: 'telegraph', value: 'HAH' })).toBe('{"type":"telegraph","value":"HAH"}');
    expect(serializeCommand({ type: 'autopilot', args: { enabled: true, heading: 275, rotLimit: 15 } })).toBe(
      '{"type":"autopilot","args":{"enabled":true,"heading":275,"rotLimit":15}}',
    );
    expect(serializeCommand({ type: 'freeze' })).toBe('{"type":"freeze"}');
  });
  it('省略 undefined 的 value/args', () => {
    expect(serializeCommand({ type: 'resume', value: undefined })).toBe('{"type":"resume"}');
  });
  it('往返解析後型別保留', () => {
    const json = serializeCommand(rudderCommand(-12.3, 35, 'FU'));
    expect(JSON.parse(json)).toEqual({ type: 'rudder', value: -12.3, args: { mode: 'FU' } });
  });
  it('無效指令拋錯', () => {
    expect(() => serializeCommand({ type: 'warp' as never })).toThrow(TypeError);
    expect(() => serializeCommand({ type: 'rudder', args: [] as never })).toThrow(TypeError);
  });
  it('isValidCommand', () => {
    expect(isValidCommand({ type: 'reset' })).toBe(true);
    expect(isValidCommand({ type: 'reset', args: null })).toBe(false);
    expect(isValidCommand(null)).toBe(false);
    expect(isValidCommand({})).toBe(false);
  });
});
