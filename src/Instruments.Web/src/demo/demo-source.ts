// 示範模式來源:以 50 Hz 推進 DemoSim、25 Hz 廣播狀態,與 WebSocket 來源介面相同。

import type { SimCommand } from '../types/command.js';
import type { ConnectionStatus, SimSource, StateListener, StatusListener } from '../net/sim-source.js';
import { DEMO_DT, DemoSim, type DemoOptions } from './demo-sim.js';

const BROADCAST_HZ = 25;

export class DemoSource implements SimSource {
  private sim: DemoSim;
  private timer: ReturnType<typeof setInterval> | null = null;
  private lastNow = 0;
  private accumulator = 0;
  private stateListeners = new Set<StateListener>();
  private statusListeners = new Set<StatusListener>();
  private _status: ConnectionStatus = 'disconnected';

  constructor(options: DemoOptions = {}) {
    this.sim = new DemoSim(options);
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
    // 複製一份,避免元件持有的狀態被下一步覆寫
    const state = structuredClone(this.sim.current);
    for (const l of this.stateListeners) l({ state, issues: [] });
  }

  private setStatus(status: ConnectionStatus, detail?: string): void {
    this._status = status;
    for (const l of this.statusListeners) l(status, detail);
  }
}
