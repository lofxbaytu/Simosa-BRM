// 規劃時間倒數計時器(規劃書第 9.1 節簡報內容「規劃時間」;R7):簡報後給團隊的規劃時間,教官按「開始」倒數、可暫停/繼續/重設;
// 剩 ≤ 60 s 進入警示、到時為「結束」。純本地狀態(以呼叫者給的毫秒時鐘計算,便於測試注入),不送核心指令、不進紀錄雜湊;
// 到時由 <brm-briefing-block> 發 planning-time-ended 事件,教官站記入學員操作紀錄一筆。

export type PlanningTimerPhase = 'idle' | 'running' | 'paused' | 'ended';

/** 剩餘時間 ≤ 此秒數時變色提醒(規劃書第 9.1 節:剩 1 分鐘提示) */
export const PLANNING_WARNING_S = 60;

export interface PlanningTimerView {
  phase: PlanningTimerPhase;
  /** 剩餘秒數(≥ 0;idle 時為總長) */
  remaining_s: number;
  /** 總長(秒) */
  total_s: number;
  /** 進入最後 1 分鐘(running 或 paused 皆可) */
  warning: boolean;
}

export class PlanningTimer {
  readonly total_s: number;
  private startedAt_ms: number | null = null;
  private elapsedBefore_ms = 0;
  private ended = false;

  /** @param totalMin 規劃時間(分鐘;≤ 0 視為無計時器) */
  constructor(totalMin: number) {
    this.total_s = Number.isFinite(totalMin) && totalMin > 0 ? totalMin * 60 : 0;
  }

  /** 有可倒數的時間 */
  get enabled(): boolean {
    return this.total_s > 0;
  }

  /** 開始或繼續;已在倒數、已結束或無時間時不動作(回傳 false)。 */
  start(now_ms: number): boolean {
    if (!this.enabled || this.ended || this.startedAt_ms !== null) return false;
    this.startedAt_ms = now_ms;
    return true;
  }

  /** 暫停;未在倒數時不動作。 */
  pause(now_ms: number): boolean {
    if (this.startedAt_ms === null || this.ended) return false;
    this.elapsedBefore_ms += Math.max(0, now_ms - this.startedAt_ms);
    this.startedAt_ms = null;
    return true;
  }

  /** 重設為尚未開始(總長不變)。 */
  reset(): void {
    this.startedAt_ms = null;
    this.elapsedBefore_ms = 0;
    this.ended = false;
  }

  elapsed_ms(now_ms: number): number {
    const running = this.startedAt_ms === null ? 0 : Math.max(0, now_ms - this.startedAt_ms);
    return this.elapsedBefore_ms + running;
  }

  remaining_s(now_ms: number): number {
    return Math.max(0, this.total_s - this.elapsed_ms(now_ms) / 1000);
  }

  /**
   * 推進時鐘:倒數歸零時轉為 ended(停止計時)並回傳 true(呼叫者據此發事件、記紀錄);其餘回傳 false。
   * 只在 running 時會轉換,暫停中不會結束。
   */
  tick(now_ms: number): boolean {
    if (this.ended || this.startedAt_ms === null) return false;
    if (this.remaining_s(now_ms) > 0) return false;
    this.elapsedBefore_ms = this.total_s * 1000;
    this.startedAt_ms = null;
    this.ended = true;
    return true;
  }

  phase(): PlanningTimerPhase {
    if (this.ended) return 'ended';
    if (this.startedAt_ms !== null) return 'running';
    return this.elapsedBefore_ms > 0 ? 'paused' : 'idle';
  }

  view(now_ms: number): PlanningTimerView {
    const phase = this.phase();
    const remaining = phase === 'ended' ? 0 : this.remaining_s(now_ms);
    return { phase, remaining_s: remaining, total_s: this.total_s, warning: phase !== 'idle' && remaining <= PLANNING_WARNING_S };
  }
}

/** 剩餘秒數 → mm:ss(無條件進位到整秒,倒數顯示「還有多少秒」;≥ 1 小時顯示 h:mm:ss)。 */
export function formatCountdown(remaining_s: number): string {
  const total = Math.max(0, Math.ceil(remaining_s - 1e-9));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = h > 0 ? String(m).padStart(2, '0') : String(m);
  return `${h > 0 ? `${h}:` : ''}${mm}:${String(s).padStart(2, '0')}`;
}
