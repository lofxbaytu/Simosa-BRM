// 測試端的核心 WebSocket 客戶端(Node 22 內建 WebSocket):讀狀態幀/事件框、送 SimCommand(與教官站同一協定 src/Contracts/command.schema.json)。
// 用來在瀏覽器之外交叉驗證(狀態幀的情境 id、凍結旗標),以及送出網頁 UI 不會產生的指令(無效內嵌情境)。

export interface StateFrame {
  tick: number;
  t: number;
  heading: number;
  sog: number;
  rudder: number;
  rudderOrder: number;
  scenario?: { id?: string; ended?: boolean } | undefined;
  flags?: { frozen?: boolean } | undefined;
  [k: string]: unknown;
}

export class CoreClient {
  private readonly ws: WebSocket;
  private readonly frames: Array<Record<string, unknown>> = [];
  private readonly waiters: Array<{ pred: (f: Record<string, unknown>) => boolean; resolve: (f: Record<string, unknown>) => void }> = [];
  private _last: StateFrame | null = null;

  private constructor(ws: WebSocket) {
    this.ws = ws;
    ws.addEventListener('message', (ev) => {
      if (typeof ev.data !== 'string') return;
      let obj: unknown;
      try { obj = JSON.parse(ev.data); } catch { return; }
      if (typeof obj !== 'object' || obj === null) return;
      const f = obj as Record<string, unknown>;
      if (typeof f['tick'] === 'number' && typeof f['heading'] === 'number') this._last = f as unknown as StateFrame;
      this.frames.push(f);
      if (this.frames.length > 2000) this.frames.splice(0, this.frames.length - 1000);
      for (let i = this.waiters.length - 1; i >= 0; i--) {
        const w = this.waiters[i]!;
        if (w.pred(f)) {
          this.waiters.splice(i, 1);
          w.resolve(f);
        }
      }
    });
  }

  static connect(url: string, timeoutMs = 10_000): Promise<CoreClient> {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(url);
      const timer = setTimeout(() => reject(new Error(`WebSocket ${url} 連線逾時`)), timeoutMs);
      ws.addEventListener('open', () => {
        clearTimeout(timer);
        resolve(new CoreClient(ws));
      });
      ws.addEventListener('error', () => {
        clearTimeout(timer);
        reject(new Error(`WebSocket ${url} 連線失敗`));
      });
    });
  }

  get last(): StateFrame | null {
    return this._last;
  }

  send(cmd: { type: string; value?: unknown; args?: Record<string, unknown> }): void {
    this.ws.send(JSON.stringify(cmd));
  }

  /** 等待第一個符合條件的框(狀態幀或事件框) */
  waitFor(pred: (f: Record<string, unknown>) => boolean, timeoutMs = 10_000, what = '符合條件的框'): Promise<Record<string, unknown>> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        const i = this.waiters.findIndex((w) => w.resolve === done);
        if (i >= 0) this.waiters.splice(i, 1);
        reject(new Error(`${timeoutMs} ms 內未收到${what}`));
      }, timeoutMs);
      const done = (f: Record<string, unknown>) => {
        clearTimeout(timer);
        resolve(f);
      };
      this.waiters.push({ pred, resolve: done });
    });
  }

  /** 等待下一筆狀態幀 */
  nextState(timeoutMs = 5000): Promise<StateFrame> {
    return this.waitFor((f) => typeof f['tick'] === 'number' && typeof f['heading'] === 'number', timeoutMs, '狀態幀') as Promise<StateFrame>;
  }

  /** 等待狀態幀滿足條件 */
  waitState(pred: (s: StateFrame) => boolean, timeoutMs = 10_000, what = '狀態條件'): Promise<StateFrame> {
    return this.waitFor((f) => typeof f['tick'] === 'number' && typeof f['heading'] === 'number' && pred(f as unknown as StateFrame), timeoutMs, what) as Promise<StateFrame>;
  }

  /** 等待事件框 {type:'event', event:{kind}} */
  waitEvent(kind: string, timeoutMs = 10_000): Promise<Record<string, unknown>> {
    return this.waitFor((f) => f['type'] === 'event' && typeof f['event'] === 'object' && f['event'] !== null && (f['event'] as Record<string, unknown>)['kind'] === kind, timeoutMs, `事件 ${kind}`).then(
      (f) => f['event'] as Record<string, unknown>,
    );
  }

  close(): void {
    try { this.ws.close(); } catch { /* 已關 */ }
  }
}
