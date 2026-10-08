// 學員操作紀錄:教官站收不到學員站送出的指令(指令直接進核心),因此由連續的狀態推導
// 舵令、車鐘、轉速令、側推令的變更,以及凍結/恢復、故障、擱淺/碰撞、時間倒退等事件,並加上時間戳
// (模擬時間與本地時鐘)。規劃書第 9.1 節「警報與學員操作紀錄」、第 8.3 節操縱控制指標的資料來源。

import type { OwnShipState } from '../types/state.js';
import { formatRudder, formatThruster } from './format.js';

export type OperationKind =
  | 'rudder'
  | 'telegraph'
  | 'rpm'
  | 'thruster'
  | 'freeze'
  | 'resume'
  | 'fault'
  | 'faultCleared'
  | 'aground'
  | 'collision'
  | 'timeReset';

export interface OperationEvent {
  seq: number;
  /** 模擬時間(秒) */
  t: number;
  /** 本地時鐘 ISO 字串 */
  wall: string;
  kind: OperationKind;
  /** 顯示文字,例如「舵令 S 20°」 */
  label: string;
  /** 數值或文字(舵角、車鐘位置、故障名稱) */
  value?: number | string;
}

export interface OperationLogOptions {
  /** 連續舵令/側推變更在此秒數內視為同一次操作(舵輪拖曳會送出許多中間值) */
  coalesceWindow_s?: number;
  /** 舵令變化小於此角度不記錄 */
  rudderDeadband_deg?: number;
  /** 側推令變化小於此比例不記錄 */
  thrusterDeadband?: number;
  /** 取得本地時間(測試注入) */
  now?: () => Date;
}

/** 由狀態序列推導操作事件;push() 回傳本筆新增(或更新)的事件。 */
export class OperationLogDeriver {
  private prev: OwnShipState | null = null;
  private readonly events: OperationEvent[] = [];
  private seq = 0;
  private readonly window: number;
  private readonly rudderDeadband: number;
  private readonly thrusterDeadband: number;
  private readonly now: () => Date;

  constructor(options: OperationLogOptions = {}) {
    this.window = options.coalesceWindow_s ?? 1.0;
    this.rudderDeadband = options.rudderDeadband_deg ?? 0.5;
    this.thrusterDeadband = options.thrusterDeadband ?? 0.05;
    this.now = options.now ?? (() => new Date());
  }

  all(): readonly OperationEvent[] {
    return this.events;
  }

  get length(): number {
    return this.events.length;
  }

  clear(): void {
    this.events.length = 0;
    this.prev = null;
  }

  /** 重新開始(重設/換情境後),保留序號遞增。 */
  restart(): void {
    this.prev = null;
  }

  push(s: OwnShipState): OperationEvent[] {
    const out: OperationEvent[] = [];
    const p = this.prev;
    this.prev = s;
    if (!p) return out; // 第一筆只作基準

    const add = (kind: OperationKind, label: string, value?: number | string): OperationEvent => {
      const ev: OperationEvent = { seq: ++this.seq, t: s.t, wall: this.now().toISOString(), kind, label };
      if (value !== undefined) ev.value = value;
      this.events.push(ev);
      out.push(ev);
      return ev;
    };

    // 時間倒退:重設或還原快照
    if (s.t < p.t - 0.5) add('timeReset', `時間倒退 ${fmtT(p.t)} → ${fmtT(s.t)}(重設或還原)`);

    // 舵令(自動舵與人工都算;連續變更合併)
    if (Math.abs(s.rudderOrder - p.rudderOrder) >= this.rudderDeadband) {
      const last = this.events[this.events.length - 1];
      if (last && last.kind === 'rudder' && s.t - last.t <= this.window && s.t >= last.t) {
        last.value = s.rudderOrder;
        last.label = `舵令 ${formatRudder(s.rudderOrder)}`;
        out.push(last);
      } else {
        add('rudder', `舵令 ${formatRudder(s.rudderOrder)}`, s.rudderOrder);
      }
    }

    // 車鐘
    if (s.telegraph !== p.telegraph) add('telegraph', `車鐘 ${p.telegraph} → ${s.telegraph}`, s.telegraph);
    // 直接轉速令(車鐘不變而轉速令變)
    else if (Math.abs(s.rpmOrder - p.rpmOrder) >= 1) add('rpm', `轉速令 ${Math.round(s.rpmOrder)}`, Math.round(s.rpmOrder));

    // 側推
    const th = s.thruster?.order ?? 0;
    const pth = p.thruster?.order ?? 0;
    if (Math.abs(th - pth) >= this.thrusterDeadband) {
      const last = this.events[this.events.length - 1];
      if (last && last.kind === 'thruster' && s.t - last.t <= this.window && s.t >= last.t) {
        last.value = th;
        last.label = `側推 ${formatThruster(th)}`;
        out.push(last);
      } else add('thruster', `側推 ${formatThruster(th)}`, th);
    }

    // 凍結/恢復
    const fz = s.flags?.frozen ?? false;
    const pfz = p.flags?.frozen ?? false;
    if (fz !== pfz) add(fz ? 'freeze' : 'resume', fz ? '凍結' : '恢復');

    // 故障
    const faults = s.faults ?? [];
    const pfaults = p.faults ?? [];
    for (const f of faults) if (!pfaults.includes(f)) add('fault', `故障注入 ${f}`, f);
    for (const f of pfaults) if (!faults.includes(f)) add('faultCleared', `故障清除 ${f}`, f);

    // 擱淺/碰撞(上升緣)
    if ((s.flags?.aground ?? false) && !(p.flags?.aground ?? false)) add('aground', '擱淺');
    if ((s.flags?.collision ?? false) && !(p.flags?.collision ?? false)) add('collision', '碰撞');

    return out;
  }
}

function fmtT(t: number): string {
  const total = Math.max(0, Math.floor(t));
  const m = Math.floor(total / 60);
  const sec = total % 60;
  return `${m}:${sec.toString().padStart(2, '0')}`;
}
