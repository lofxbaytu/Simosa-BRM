// 狀態來源介面:WebSocket 連線(核心)與示範模式(內建運動學)共用同一介面。

import type { SimCommand } from '../types/command.js';
import type { OwnShipState } from '../types/state.js';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'demo';

export interface StateEvent {
  state: OwnShipState;
  /** 解析時補上預設值的欄位,供「資料可疑」標記 */
  issues: string[];
}

export type StateListener = (ev: StateEvent) => void;
export type StatusListener = (status: ConnectionStatus, detail?: string) => void;
/** 非狀態訊息(例如 Python 參考伺服器的 {"type":"ack",...}),已解析為 JSON 物件 */
export type MessageListener = (message: Record<string, unknown>) => void;

export interface SimSource {
  readonly status: ConnectionStatus;
  start(): void;
  stop(): void;
  send(cmd: SimCommand): void;
  onState(listener: StateListener): () => void;
  onStatus(listener: StatusListener): () => void;
  /** 訂閱非狀態訊息(指令回應等);來源不支援時可省略 */
  onMessage?(listener: MessageListener): () => void;
}
