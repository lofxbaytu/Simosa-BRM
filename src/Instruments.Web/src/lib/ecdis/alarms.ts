// 簡化 ECDIS 的頁面本地警報(docs/design/ecdis-lite.md 第 5 節;S-52/IEC 61174 警報清單的簡化):
// 條件由每筆狀態重新評估(evaluate 在 ecdis-store),本管理器維護清單與確認狀態:
//  - 新條件 → 未確認(有聲響,caution 除外);條件消失且已確認 → 移除;條件消失但未確認 → 保留為「已解除、未確認」直到確認(MSC.302 rectified-unacknowledged 概念);
//  - 已確認的條件再次成立(先消失再出現)→ 重新成為未確認並累計次數;
//  - 與核心 BAM(state.alerts)無關:不送 alertAcknowledge/acknowledge 指令、不影響核心評估與雜湊,只作顯示與聲響(聲響可關)。

import type { AlertItem } from '../../types/alerts.js';

export type EcdisAlarmKind = 'safetyContour' | 'xtd' | 'cpa' | 'zone' | 'gnss' | 'gyro' | 'log' | 'routeEnd';

export type EcdisAlarmPriority = 'alarm' | 'warning' | 'caution';

export const ECDIS_ALARM_KIND_LABEL: Readonly<Record<EcdisAlarmKind, string>> = {
  safetyContour: '安全等深線',
  xtd: '航跡偏差',
  cpa: 'CPA/TCPA',
  zone: '區域進入',
  gnss: 'GNSS',
  gyro: '電羅經',
  log: '計程儀',
  routeEnd: '航線終點',
};

export const ECDIS_ALARM_PRIORITY: Readonly<Record<EcdisAlarmKind, EcdisAlarmPriority>> = {
  safetyContour: 'alarm',
  xtd: 'warning',
  cpa: 'alarm',
  zone: 'alarm',
  gnss: 'warning',
  gyro: 'warning',
  log: 'caution',
  routeEnd: 'caution',
};

const PRIORITY_RANK: Readonly<Record<EcdisAlarmPriority, number>> = { alarm: 0, warning: 1, caution: 2 };

/** 評估出的警報條件(同一 id 在連續狀態中代表同一警報)。 */
export interface EcdisCondition {
  id: string;
  kind: EcdisAlarmKind;
  text: string;
  /** 省略時依 kind 的預設優先序 */
  priority?: EcdisAlarmPriority;
}

export interface EcdisAlarm {
  id: string;
  kind: EcdisAlarmKind;
  priority: EcdisAlarmPriority;
  text: string;
  /** 最近一次成立的模擬時間 (s) */
  raisedAt_s: number;
  /** 條件目前仍成立 */
  active: boolean;
  acknowledged: boolean;
  acknowledgedAt_s?: number;
  /** 成立次數 */
  count: number;
}

export class EcdisAlarmManager {
  private readonly alarms = new Map<string, EcdisAlarm>();

  /** 套用本筆狀態的條件;回傳清單是否改變。 */
  update(conditions: readonly EcdisCondition[], t_s: number): boolean {
    let changed = false;
    const seen = new Set<string>();
    for (const c of conditions) {
      seen.add(c.id);
      const priority = c.priority ?? ECDIS_ALARM_PRIORITY[c.kind];
      const existing = this.alarms.get(c.id);
      if (!existing) {
        this.alarms.set(c.id, { id: c.id, kind: c.kind, priority, text: c.text, raisedAt_s: t_s, active: true, acknowledged: false, count: 1 });
        changed = true;
        continue;
      }
      if (!existing.active) {
        // 已解除後再次成立:已確認者重新未確認;未確認者只回到 active
        existing.active = true;
        existing.raisedAt_s = t_s;
        existing.count++;
        if (existing.acknowledged) {
          existing.acknowledged = false;
          delete existing.acknowledgedAt_s;
        }
        changed = true;
      }
      if (existing.text !== c.text) {
        existing.text = c.text;
        changed = true;
      }
      if (existing.priority !== priority) {
        existing.priority = priority;
        changed = true;
      }
    }
    for (const [id, a] of this.alarms) {
      if (seen.has(id)) continue;
      if (a.acknowledged) {
        this.alarms.delete(id);
        changed = true;
      } else if (a.active) {
        a.active = false;
        changed = true;
      }
    }
    return changed;
  }

  /** 確認一筆:條件仍成立 → 留在清單(無聲);已解除 → 移除。回傳是否有此警報。 */
  acknowledge(id: string, t_s = 0): boolean {
    const a = this.alarms.get(id);
    if (!a) return false;
    if (!a.active) this.alarms.delete(id);
    else {
      a.acknowledged = true;
      a.acknowledgedAt_s = t_s;
    }
    return true;
  }

  acknowledgeAll(t_s = 0): void {
    for (const id of [...this.alarms.keys()]) this.acknowledge(id, t_s);
  }

  clear(): void {
    this.alarms.clear();
  }

  /** 清單:未確認在前,其次優先序(alarm → caution),再依成立時間新到舊。 */
  list(): EcdisAlarm[] {
    return [...this.alarms.values()].sort((a, b) => Number(a.acknowledged) - Number(b.acknowledged) || PRIORITY_RANK[a.priority] - PRIORITY_RANK[b.priority] || b.raisedAt_s - a.raisedAt_s);
  }

  get(id: string): EcdisAlarm | undefined {
    return this.alarms.get(id);
  }

  get size(): number {
    return this.alarms.size;
  }

  /** 未確認筆數(含已解除未確認)。 */
  get unacknowledged(): number {
    let n = 0;
    for (const a of this.alarms.values()) if (!a.acknowledged) n++;
    return n;
  }

  /** 目前應發聲的最高優先序(未確認且仍成立的 alarm/warning;caution 無聲;無 → null)。 */
  soundPriority(): 'alarm' | 'warning' | null {
    let best: 'alarm' | 'warning' | null = null;
    for (const a of this.alarms.values()) {
      if (a.acknowledged || !a.active || a.priority === 'caution') continue;
      if (a.priority === 'alarm') return 'alarm';
      best = 'warning';
    }
    return best;
  }

  /** 轉成 AlertItem 形式,供沿用操船台的 AlertSounder(聲響型式與核心警報一致)。 */
  toAlertItems(): AlertItem[] {
    return this.list().map((a, i) => ({
      id: `ecdis:${a.id}`,
      number: 900 + i,
      priority: a.priority,
      category: 'B' as const,
      source: 'ecdis',
      state: a.acknowledged ? ('activeAcknowledged' as const) : a.active ? ('activeUnacknowledged' as const) : ('rectifiedUnacknowledged' as const),
      text: a.text,
      raisedAt_s: a.raisedAt_s,
      escalated: false,
      count: a.count,
      audible: a.active && !a.acknowledged && a.priority !== 'caution',
    }));
  }
}
