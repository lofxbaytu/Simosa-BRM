// 各站送往模擬核心的指令型別,對應 src/Contracts/command.schema.json(SimCommand)。

import type { TelegraphPosition } from './state.js';

export type CommandType =
  | 'rudder'
  | 'telegraph'
  | 'rpm'
  | 'thruster'
  | 'autopilot'
  | 'freeze'
  | 'resume'
  | 'reset'
  | 'timeScale'
  | 'loadScenario'
  | 'setEnvironment'
  | 'injectFault'
  | 'clearFault'
  | 'snapshot'
  | 'restore';

export const COMMAND_TYPES: readonly CommandType[] = [
  'rudder',
  'telegraph',
  'rpm',
  'thruster',
  'autopilot',
  'freeze',
  'resume',
  'reset',
  'timeScale',
  'loadScenario',
  'setEnvironment',
  'injectFault',
  'clearFault',
  'snapshot',
  'restore',
] as const;

/** 舵令來源:FU 隨動(舵輪)或 NFU 非隨動(舵柄按鈕),僅作提示,核心可忽略。 */
export type SteeringMode = 'FU' | 'NFU';

export interface AutopilotArgs {
  enabled: boolean;
  /** 設定航向 (度,0–360) */
  heading?: number;
  /** ROT 限制 (度/分) */
  rotLimit?: number;
}

export interface SimCommand {
  type: CommandType;
  value?: unknown;
  args?: Record<string, unknown>;
}

export interface RudderCommand extends SimCommand {
  type: 'rudder';
  /** 舵令 (度),右正左負 */
  value: number;
  args?: { mode?: SteeringMode };
}

export interface TelegraphCommand extends SimCommand {
  type: 'telegraph';
  value: TelegraphPosition;
}

export interface ThrusterCommand extends SimCommand {
  type: 'thruster';
  /** −1 至 1,右推正 */
  value: number;
}

export interface AutopilotCommand extends SimCommand {
  type: 'autopilot';
  args: AutopilotArgs & Record<string, unknown>;
}
