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
  | 'steeringMode'
  | 'alertAcknowledge'
  | 'alertSilence'
  | 'bnwasMode'
  | 'bnwasReset'
  | 'tugControl'
  | 'anchor'
  | 'mooring'
  | 'alertTransfer'
  | 'radarControl';

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
  'alertAcknowledge',
  'alertSilence',
  'bnwasMode',
  'bnwasReset',
  'tugControl',
  'anchor',
  'mooring',
  'alertTransfer',
  'radarControl',
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

/** alertAcknowledge / alertSilence:args.id(或 value 字串)為警報 id;省略或 "all" = 全部;alertAcknowledge 可帶 args.station(省略 = bridge;docs/design/sensors-alerts.md 第 3.3、3.6 節)。 */
export interface AlertCommand extends SimCommand {
  type: 'alertAcknowledge' | 'alertSilence';
  args?: { id?: string; station?: string } & Record<string, unknown>;
}

/** alertTransfer:責任轉移到 station(bridge|portWing|stbdWing|ecr;MSC.302(87) responsibility transferred)。 */
export interface AlertTransferCommand extends SimCommand {
  type: 'alertTransfer';
  args: { id: string; station: string };
}

/** bnwasMode:value 為 off | auto | on;args.dormant_s 夾在 180–720(設計文件第 4 節)。 */
export interface BnwasModeCommand extends SimCommand {
  type: 'bnwasMode';
  value: 'off' | 'auto' | 'on';
  args?: { dormant_s?: number; stage3_s?: number } & Record<string, unknown>;
}

/** tugControl:拖船令(docs/design/physics-tugs-anchors-mooring.md 第 2 節);mode/direction_deg/power_pct 省略 = 不變。
 *  direction_deg 為作用於自船的力方向,相對船艏順時針(0 向前、90 向右舷、180 向後、270 向左舷)。 */
export interface TugControlCommand extends SimCommand {
  type: 'tugControl';
  args: { id: string; mode?: 'push' | 'pull' | 'standby' | 'free'; direction_deg?: number; power_pct?: number } & Record<string, unknown>;
}

/** anchor:錨令(第 3 節);length_m 優先於 targetLength_shackles;letGo/heave 目標省略 = 鏈端/收妥。 */
export interface AnchorCommand extends SimCommand {
  type: 'anchor';
  args: { side: 'port' | 'starboard'; action: 'letGo' | 'veer' | 'heave' | 'brake' | 'release'; targetLength_shackles?: number; length_m?: number } & Record<string, unknown>;
}

/** mooring:繫纜令(第 4 節;情境 berth.lines[])。 */
export interface MooringCommand extends SimCommand {
  type: 'mooring';
  args: { lineId: string; action: 'makeFast' | 'heave' | 'slack' | 'letGo'; bollard?: string; tension_t?: number; length_m?: number } & Record<string, unknown>;
}
