// 目標船顯示的輔助(規劃書第 5.2 節 Traffic、第 8.3 節避碰指標;設計文件第 6 節 W8 航跡圖):
//  - cpaAlarmTargets():由事件流推得「處於 CPA 警報中」的目標 id(cpaAlarm 進入、cpaClear 解除;重播時只看 t ≤ 目前時刻的事件);
//  - CpaAlarmTracker:即時模式逐事件維護的警報集合(不受事件流上限截斷影響;reset 清空、倒帶時依過濾後的事件重算);
//  - targetTableRows():航跡圖 CPA/TCPA 小表的列(依距離排序,警報者在前)。

import type { SimEvent } from '../types/event.js';
import type { TargetState } from '../types/target.js';

/** 處於 cpaAlarm 狀態的目標 id(事件依時間順序;tMax 之後的事件忽略,供重播)。 */
export function cpaAlarmTargets(events: readonly SimEvent[], tMax = Number.POSITIVE_INFINITY): Set<string> {
  const out = new Set<string>();
  for (const e of events) {
    if (e.t > tMax || !e.targetId) continue;
    if (e.kind === 'cpaAlarm') out.add(e.targetId);
    else if (e.kind === 'cpaClear') out.delete(e.targetId);
  }
  return out;
}

/**
 * 即時模式的 CPA 警報集合:逐事件套用(cpaAlarm 加入、cpaClear 移除),不由被截斷的事件流重算,
 * 因此事件流超過 EventStore 上限而擠掉 cpaAlarm 事件時,仍在警報中的目標不會失去標示。
 * 倒帶時以過濾後的事件流重算(rebuild),新練習時清空(clear)。
 */
export class CpaAlarmTracker {
  private readonly ids = new Set<string>();

  /** 套用一筆事件;回傳集合是否改變。 */
  apply(e: SimEvent): boolean {
    if (!e.targetId) return false;
    if (e.kind === 'cpaAlarm') {
      if (this.ids.has(e.targetId)) return false;
      this.ids.add(e.targetId);
      return true;
    }
    if (e.kind === 'cpaClear') return this.ids.delete(e.targetId);
    return false;
  }

  /** 倒帶後:以倒帶點之前的事件重算。 */
  rebuild(events: readonly SimEvent[], tMax = Number.POSITIVE_INFINITY): void {
    this.ids.clear();
    for (const id of cpaAlarmTargets(events, tMax)) this.ids.add(id);
  }

  clear(): void {
    this.ids.clear();
  }

  /** 目前警報中的目標 id(新的 Set 副本,供 Lit 屬性變更偵測)。 */
  snapshot(): ReadonlySet<string> {
    return new Set(this.ids);
  }

  get size(): number {
    return this.ids.size;
  }
}

export interface TargetTableRow {
  id: string;
  /** 船名(無則 id) */
  label: string;
  range_nm: number;
  bearing_deg: number;
  cpa_nm: number;
  tcpa_min: number;
  aisOn: boolean;
  alarm: boolean;
}

/** CPA/TCPA 小表:警報者在前,其餘依距離遞增;最多 max 列。 */
export function targetTableRows(targets: readonly TargetState[], alarms: ReadonlySet<string>, max = 8): TargetTableRow[] {
  return targets
    .map((t) => ({ id: t.id, label: t.name || t.id, range_nm: t.range_nm, bearing_deg: t.bearing_deg, cpa_nm: t.cpa_nm, tcpa_min: t.tcpa_min, aisOn: t.aisOn, alarm: alarms.has(t.id) }))
    .sort((a, b) => Number(b.alarm) - Number(a.alarm) || a.range_nm - b.range_nm)
    .slice(0, max);
}
