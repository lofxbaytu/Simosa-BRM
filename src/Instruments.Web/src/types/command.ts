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
  | 'restore'
  | 'targetControl'
  | 'triggerTimeline'
  | 'acknowledge'
  | 'endExercise'
  | 'getAssessment'
  | 'steeringMode';

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
  'targetControl',
  'triggerTimeline',
  'acknowledge',
  'endExercise',
  'getAssessment',
  'steeringMode',
] as const;

/** 舵令來源:FU 隨動(舵輪)或 NFU 非隨動(舵柄按鈕),僅作提示,核心可忽略。 */
export type SteeringMode = 'FU' | 'NFU';

/** NFU 推桿方向(steeringMode nfu 的 args.direction;不明方向核心視同 stop,D35)。 */
export type NfuDirection = 'port' | 'starboard' | 'stop';

export const NFU_DIRECTIONS: readonly NfuDirection[] = ['port', 'starboard', 'stop'] as const;

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

/** steeringMode:操舵模式選擇(規劃書第 7.2 節;設計文件 D32/D35):value 為模式,args 依模式(pump 皆可;direction 僅 nfu;heading/rotLimit 僅 autopilot)。 */
export interface SteeringModeCommand extends SimCommand {
  type: 'steeringMode';
  value: 'fu' | 'nfu' | 'autopilot' | 'emergency';
  args?: { pump?: '1' | '2' | 'both'; direction?: NfuDirection; heading?: number; rotLimit?: number } & Record<string, unknown>;
}

/** triggerTimeline:手動觸發時間表項目(manual 項目;其他需 force 跳過條件),設計文件第 5.2 節。 */
export interface TriggerTimelineCommand extends SimCommand {
  type: 'triggerTimeline';
  args: { id: string; force?: boolean } & Record<string, unknown>;
}

/** acknowledge:教官/帶領者記錄核心偵測不到的口頭辨識、通知等動作(餵給必要事件與故障應變)。 */
export interface AcknowledgeCommand extends SimCommand {
  type: 'acknowledge';
  args: { kind: string; itemId?: string; role?: string; note?: string } & Record<string, unknown>;
}
