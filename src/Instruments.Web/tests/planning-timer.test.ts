// 規劃時間倒數計時器(lib/planning-timer.ts;規劃書第 9.1 節簡報「規劃時間」R7):以注入時鐘驗證開始/暫停/繼續/重設、
// 剩 1 分鐘警示、到時只轉換一次(tick 回傳 true 一次)、暫停中不會結束、mm:ss 格式。
import { describe, expect, it } from 'vitest';
import { PLANNING_WARNING_S, PlanningTimer, formatCountdown } from '../src/lib/planning-timer.js';

const MIN = 60_000;

describe('PlanningTimer', () => {
  it('idle → running → paused → running → ended;剩餘秒數與警示', () => {
    const t = new PlanningTimer(5);
    expect(t.enabled).toBe(true);
    expect(t.view(0)).toEqual({ phase: 'idle', remaining_s: 300, total_s: 300, warning: false });
    expect(t.pause(0)).toBe(false); // 未開始不能暫停
    expect(t.start(1000)).toBe(true);
    expect(t.start(2000)).toBe(false); // 已在倒數
    expect(t.remaining_s(1000 + 2 * MIN)).toBe(180);
    expect(t.pause(1000 + 2 * MIN)).toBe(true);
    expect(t.view(1000 + 10 * MIN)).toMatchObject({ phase: 'paused', remaining_s: 180, warning: false }); // 暫停中不走
    expect(t.tick(1000 + 10 * MIN)).toBe(false); // 暫停中不會結束
    expect(t.start(1000 + 10 * MIN)).toBe(true);
    const warnAt = 1000 + 10 * MIN + (180 - PLANNING_WARNING_S) * 1000;
    expect(t.view(warnAt)).toMatchObject({ phase: 'running', remaining_s: 60, warning: true });
    expect(t.tick(warnAt)).toBe(false);
    const endAt = 1000 + 10 * MIN + 180 * 1000;
    expect(t.tick(endAt)).toBe(true); // 剛好歸零 → 結束
    expect(t.tick(endAt + 5000)).toBe(false); // 只轉換一次
    expect(t.view(endAt + 5000)).toEqual({ phase: 'ended', remaining_s: 0, total_s: 300, warning: true });
    expect(t.start(endAt + 5000)).toBe(false); // 結束後需重設
    t.reset();
    expect(t.view(endAt + 6000)).toEqual({ phase: 'idle', remaining_s: 300, total_s: 300, warning: false });
    expect(t.start(endAt + 6000)).toBe(true);
  });

  it('小數分鐘與 0/負值:0.5 分鐘 = 30 s;≤ 0 或 NaN 視為無計時器', () => {
    expect(new PlanningTimer(0.5).total_s).toBe(30);
    for (const v of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      const t = new PlanningTimer(v);
      expect(t.enabled).toBe(false);
      expect(t.start(0)).toBe(false);
      expect(t.view(0).phase).toBe('idle');
    }
  });

  it('時鐘倒退不算負經過時間', () => {
    const t = new PlanningTimer(1);
    t.start(10_000);
    expect(t.remaining_s(5_000)).toBe(60);
  });
});

describe('formatCountdown', () => {
  it('mm:ss(無條件進位到整秒)與 h:mm:ss', () => {
    expect(formatCountdown(300)).toBe('5:00');
    expect(formatCountdown(299.2)).toBe('5:00');
    expect(formatCountdown(59.9)).toBe('1:00');
    expect(formatCountdown(59)).toBe('0:59');
    expect(formatCountdown(0)).toBe('0:00');
    expect(formatCountdown(-3)).toBe('0:00');
    expect(formatCountdown(3600)).toBe('1:00:00');
    expect(formatCountdown(7200 + 65)).toBe('2:01:05');
  });
});
