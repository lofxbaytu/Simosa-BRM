// 操舵模式(規劃書第 7.2 節操舵模式選擇;設計文件 docs/design/scenario-engine.md D32/D35):
//  - steeringModeCommand():建構 command.schema.json 的 steeringMode 指令(value 為模式;args.pump 皆可、direction 僅 nfu、
//    heading/rotLimit 僅 autopilot);
//  - parseSteering():由核心的原始狀態框(含信封)取出 state.steering(C# SimCore 產生,Python 參考伺服器不輸出);
//    state-parser.ts 不認得此欄位,由 ws-client 在解析後補上;
//  - steeringLabel():顯示文字。

import type { NfuDirection, SimCommand } from '../types/command.js';
import { STEERING_MODES, STEERING_PUMPS, type SteeringModeValue, type SteeringPump, type SteeringStatus } from '../types/state.js';
import { clamp, normalizeHeading } from './angles.js';
import { STEERING_MODE_LABEL } from './sim-event.js';

export interface SteeringModeArgs {
  pump?: SteeringPump;
  /** 僅 nfu:推桿方向;省略視同 stop(核心 D35) */
  direction?: NfuDirection;
  /** 僅 autopilot:設定航向(省略 = 保持艏向) */
  heading?: number;
  /** 僅 autopilot:ROT 限制(度/分) */
  rotLimit?: number;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/** steeringMode 指令:不適用於該模式的 args 不送(nfu 以外的 direction、autopilot 以外的 heading/rotLimit)。 */
export function steeringModeCommand(mode: SteeringModeValue, args: SteeringModeArgs = {}): SimCommand {
  if (!STEERING_MODES.includes(mode)) throw new RangeError(`無效的操舵模式:${String(mode)}`);
  const out: Record<string, unknown> = {};
  if (args.pump && STEERING_PUMPS.includes(args.pump)) out['pump'] = args.pump;
  if (mode === 'nfu') out['direction'] = args.direction === 'port' || args.direction === 'starboard' ? args.direction : 'stop';
  if (mode === 'autopilot') {
    if (typeof args.heading === 'number' && Number.isFinite(args.heading)) out['heading'] = Math.round(normalizeHeading(args.heading) * 10) / 10;
    if (typeof args.rotLimit === 'number' && Number.isFinite(args.rotLimit)) out['rotLimit'] = Math.round(clamp(Math.abs(args.rotLimit), 1, 120));
  }
  const cmd: SimCommand = { type: 'steeringMode', value: mode };
  if (Object.keys(out).length > 0) cmd.args = out;
  return cmd;
}

/**
 * 由原始狀態訊息取出 steering(接受與 parseState 相同的信封:直接狀態、{state}、{data}、{payload} 或 JSON 字串)。
 * 缺欄位或 mode/pump 不在列舉內回傳 undefined(不視為錯誤:Python 參考伺服器與舊紀錄沒有此欄位)。
 */
export function parseSteering(input: unknown): SteeringStatus | undefined {
  let raw: unknown = input;
  if (typeof raw === 'string') {
    try {
      raw = JSON.parse(raw);
    } catch {
      return undefined;
    }
  }
  if (!isRecord(raw)) return undefined;
  if (!('steering' in raw) && !('heading' in raw) && !('pos' in raw)) {
    const inner = raw['state'] ?? raw['data'] ?? raw['payload'];
    if (!isRecord(inner)) return undefined;
    raw = inner;
  }
  const st = (raw as Record<string, unknown>)['steering'];
  if (!isRecord(st)) return undefined;
  const mode = st['mode'];
  const pump = st['pump'];
  if (typeof mode !== 'string' || !(STEERING_MODES as readonly string[]).includes(mode)) return undefined;
  const pumpValue = typeof pump === 'number' ? String(pump) : pump;
  if (typeof pumpValue !== 'string' || !(STEERING_PUMPS as readonly string[]).includes(pumpValue)) return undefined;
  const out: SteeringStatus = { mode: mode as SteeringModeValue, pump: pumpValue as SteeringPump };
  if (typeof st['nfuActive'] === 'boolean') out.nfuActive = st['nfuActive'];
  return out;
}

/** 顯示文字,例如「NFU 非隨動 · 泵 both · 推舵中」。 */
export function steeringLabel(s: SteeringStatus | undefined): string {
  if (!s) return '核心未回報';
  const parts = [STEERING_MODE_LABEL[s.mode] ?? s.mode, `泵 ${s.pump}`];
  if (s.mode === 'nfu') parts.push(s.nfuActive ? '推舵中' : '推桿放開');
  return parts.join(' · ');
}

/** 兩筆 steering 是否相同(mode、pump、nfuActive;undefined 視同 false)。 */
export function sameSteering(a: SteeringStatus | undefined, b: SteeringStatus | undefined): boolean {
  if (!a || !b) return a === b;
  return a.mode === b.mode && a.pump === b.pump && (a.nfuActive ?? false) === (b.nfuActive ?? false);
}
