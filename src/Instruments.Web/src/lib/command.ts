// 指令建構與序列化:輸出符合 src/Contracts/command.schema.json 的 JSON 字串。

import { COMMAND_TYPES, type AutopilotArgs, type CommandType, type SimCommand, type SteeringMode } from '../types/command.js';
import { TELEGRAPH_POSITIONS, type TelegraphPosition } from '../types/state.js';
import { clamp, normalizeHeading } from './angles.js';

/** 舵令:限幅在 ±maxAngle 並取一位小數。 */
export function rudderCommand(angleDeg: number, maxAngle: number, mode?: SteeringMode): SimCommand {
  const value = Math.round(clamp(angleDeg, -maxAngle, maxAngle) * 10) / 10;
  const cmd: SimCommand = { type: 'rudder', value: value === 0 ? 0 : value };
  if (mode) cmd.args = { mode };
  return cmd;
}

export function telegraphCommand(position: TelegraphPosition): SimCommand {
  if (!TELEGRAPH_POSITIONS.includes(position)) {
    throw new RangeError(`無效的車鐘位置:${String(position)}`);
  }
  return { type: 'telegraph', value: position };
}

/** 側推:限幅 −1 至 1,取兩位小數。 */
export function thrusterCommand(value: number): SimCommand {
  const v = Math.round(clamp(value, -1, 1) * 100) / 100;
  return { type: 'thruster', value: v === 0 ? 0 : v };
}

export function autopilotCommand(args: AutopilotArgs): SimCommand {
  const out: Record<string, unknown> = { enabled: Boolean(args.enabled) };
  if (args.heading !== undefined && Number.isFinite(args.heading)) {
    out['heading'] = Math.round(normalizeHeading(args.heading) * 10) / 10;
  }
  if (args.rotLimit !== undefined && Number.isFinite(args.rotLimit)) {
    out['rotLimit'] = Math.round(clamp(Math.abs(args.rotLimit), 1, 120));
  }
  return { type: 'autopilot', args: out };
}

export function simpleCommand(type: 'freeze' | 'resume' | 'reset' | 'snapshot'): SimCommand {
  return { type };
}

export function timeScaleCommand(scale: number): SimCommand {
  return { type: 'timeScale', value: clamp(scale, 0, 10) };
}

/** 檢查物件是否為合法指令(type 在列舉內,args 若有必須是物件)。 */
export function isValidCommand(cmd: unknown): cmd is SimCommand {
  if (typeof cmd !== 'object' || cmd === null) return false;
  const c = cmd as Record<string, unknown>;
  if (!(COMMAND_TYPES as readonly string[]).includes(c['type'] as string)) return false;
  if (c['args'] !== undefined && (typeof c['args'] !== 'object' || c['args'] === null || Array.isArray(c['args']))) {
    return false;
  }
  return true;
}

/** 序列化為單行 JSON;鍵固定順序 type、value、args,省略 undefined。 */
export function serializeCommand(cmd: SimCommand): string {
  if (!isValidCommand(cmd)) throw new TypeError(`無效的指令:${JSON.stringify(cmd)}`);
  const out: Record<string, unknown> = { type: cmd.type as CommandType };
  if (cmd.value !== undefined) out['value'] = cmd.value;
  if (cmd.args !== undefined) out['args'] = cmd.args;
  return JSON.stringify(out);
}
