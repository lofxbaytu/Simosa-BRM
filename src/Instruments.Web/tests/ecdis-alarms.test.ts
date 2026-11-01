// 頁面本地警報管理(lib/ecdis/alarms):新警報未確認、確認後仍成立則留在清單、條件消失且已確認即移除、
// 條件消失未確認保留為「已解除待確認」、確認已解除者移除、已確認再成立重新未確認並累計、排序、聲響優先序、AlertItem 轉換。
import { describe, expect, it } from 'vitest';
import { EcdisAlarmManager, type EcdisCondition } from '../src/lib/ecdis/alarms.js';

const xtd: EcdisCondition = { id: 'xtd', kind: 'xtd', text: '偏差' };
const cpa: EcdisCondition = { id: 'cpa:T1', kind: 'cpa', text: 'T1 CPA' };
const log: EcdisCondition = { id: 'log', kind: 'log', text: 'STW 無效' };

describe('ECDIS 警報', () => {
  it('新條件 → 未確認;確認後仍成立則留在清單且無聲', () => {
    const m = new EcdisAlarmManager();
    expect(m.update([xtd], 10)).toBe(true);
    expect(m.list()).toHaveLength(1);
    expect(m.unacknowledged).toBe(1);
    expect(m.soundPriority()).toBe('warning');
    expect(m.update([xtd], 11)).toBe(false);
    expect(m.acknowledge('xtd', 12)).toBe(true);
    expect(m.get('xtd')).toMatchObject({ acknowledged: true, acknowledgedAt_s: 12, active: true });
    expect(m.soundPriority()).toBeNull();
    expect(m.acknowledge('nope')).toBe(false);
  });

  it('條件消失:已確認即移除;未確認保留為已解除待確認,確認後移除', () => {
    const m = new EcdisAlarmManager();
    m.update([xtd, cpa], 1);
    m.acknowledge('xtd', 2);
    expect(m.update([], 3)).toBe(true);
    expect(m.get('xtd')).toBeUndefined();
    expect(m.get('cpa:T1')).toMatchObject({ active: false, acknowledged: false });
    expect(m.soundPriority()).toBeNull(); // 已解除者不發聲
    expect(m.unacknowledged).toBe(1);
    m.acknowledge('cpa:T1', 4);
    expect(m.size).toBe(0);
  });

  it('已確認再成立:重新未確認、次數累計、時間更新', () => {
    const m = new EcdisAlarmManager();
    m.update([cpa], 1);
    m.acknowledge('cpa:T1', 2);
    m.update([cpa], 3); // 仍成立,不變
    expect(m.get('cpa:T1')!.count).toBe(1);
    m.update([], 4); // 消失 → 已確認移除
    expect(m.get('cpa:T1')).toBeUndefined();
    // 未確認者消失再成立:回到 active,次數 +1
    m.update([xtd], 5);
    m.update([], 6);
    m.update([xtd], 7);
    expect(m.get('xtd')).toMatchObject({ active: true, acknowledged: false, count: 2, raisedAt_s: 7 });
  });

  it('排序:未確認在前 → alarm/warning/caution → 新到舊;全部確認;toAlertItems 的 audible', () => {
    const m = new EcdisAlarmManager();
    m.update([log], 1);
    m.update([log, xtd], 2);
    m.update([log, xtd, cpa], 3);
    m.acknowledge('cpa:T1', 4);
    expect(m.list().map((a) => a.id)).toEqual(['xtd', 'log', 'cpa:T1']);
    expect(m.soundPriority()).toBe('warning');
    const items = m.toAlertItems();
    expect(items.map((i) => i.audible)).toEqual([true, false, false]); // caution 無聲、已確認無聲
    expect(items[0]).toMatchObject({ id: 'ecdis:xtd', priority: 'warning', state: 'activeUnacknowledged', source: 'ecdis' });
    m.update([log, xtd, cpa], 5);
    expect(m.soundPriority()).toBe('warning');
    m.acknowledgeAll(6);
    expect(m.unacknowledged).toBe(0);
    m.update([cpa], 7);
    expect(m.list().map((a) => a.id)).toEqual(['cpa:T1']);
    m.update([], 8);
    m.update([cpa], 9); // 已確認且消失者已移除 → 重新出現為新警報
    expect(m.soundPriority()).toBe('alarm');
    expect(m.get('cpa:T1')).toMatchObject({ count: 1, raisedAt_s: 9, acknowledged: false });
    m.clear();
    expect(m.size).toBe(0);
  });
});
