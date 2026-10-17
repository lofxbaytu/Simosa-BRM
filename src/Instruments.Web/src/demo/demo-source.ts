// 示範模式來源:以 50 Hz 推進 DemoSim、25 Hz 廣播狀態,與 WebSocket 來源介面相同;
// 另以 onMessage 推送 DemoSim 的假事件框 {"type":"event"} 與 getAssessment 的 {"type":"assessment"} 回覆。

import type { SimCommand } from '../types/command.js';
import type { ConnectionStatus, MessageListener, SimSource, StateListener, StatusListener } from '../net/sim-source.js';
import { DEMO_DT, DemoSim, type DemoOptions } from './demo-sim.js';

const BROADCAST_HZ = 25;

export class DemoSource implements SimSource {
  private sim: DemoSim;
  private timer: ReturnType<typeof setInterval> | null = null;
  private lastNow = 0;
  private accumulator = 0;
  private stateListeners = new Set<StateListener>();
  private statusListeners = new Set<StatusListener>();
  private messageListeners = new Set<MessageListener>();
  private _status: ConnectionStatus = 'disconnected';

  constructor(options: DemoOptions = {}) {
    // 示範來源預設開啟示範警報與 BNWAS(中央警報面板、教官站警報監看的離線預覽)
    this.sim = new DemoSim({ alerts: true, ...options });
  }

  get status(): ConnectionStatus {
    return this._status;
  }

  get simulator(): DemoSim {
    return this.sim;
  }

  start(): void {
    if (this.timer) return;
    this.lastNow = performance.now();
    this.accumulator = 0;
    this.timer = setInterval(() => this.tick(), 1000 / BROADCAST_HZ);
    this.setStatus('demo', '示範模式(內建運動學,非船模)');
    this.emit();
  }

  stop(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.setStatus('disconnected', '示範模式已停止');
  }

  send(cmd: SimCommand): void {
    this.sim.apply(cmd);
    this.emit();
    // 與 C# 主控程序相同:final 取 summary 的定稿旗標(結束練習後的 getAssessment 回覆仍為定稿)
    if (cmd.type === 'getAssessment' || cmd.type === 'endExercise') {
      const summary = this.sim.assessmentSummary();
      this.dispatch({ type: 'assessment', final: summary['final'] === true, summary });
    }
  }

  onMessage(listener: MessageListener): () => void {
    this.messageListeners.add(listener);
    return () => this.messageListeners.delete(listener);
  }

  private dispatch(frame: Record<string, unknown>): void {
    for (const l of this.messageListeners) l(frame);
  }

  onState(listener: StateListener): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  onStatus(listener: StatusListener): () => void {
    this.statusListeners.add(listener);
    listener(this._status);
    return () => this.statusListeners.delete(listener);
  }

  private tick(): void {
    const now = performance.now();
    // 分頁切到背景時 setInterval 會被節流,限制單次最多補 0.5 秒避免跳躍
    this.accumulator += Math.min(0.5, (now - this.lastNow) / 1000);
    this.lastNow = now;
    while (this.accumulator >= DEMO_DT) {
      this.sim.step(DEMO_DT);
      this.accumulator -= DEMO_DT;
    }
    this.emit();
  }

  private emit(): void {
    // output() 為複本(頂層為感測器指示值,附 truth/sensors/alerts/bnwas),避免元件持有的狀態被下一步覆寫
    const state = this.sim.output();
    for (const l of this.stateListeners) l({ state, issues: [] });
    for (const ev of this.sim.takeEvents()) this.dispatch({ type: 'event', event: ev });
  }

  private setStatus(status: ConnectionStatus, detail?: string): void {
    this._status = status;
    for (const l of this.statusListeners) l(status, detail);
  }
}
