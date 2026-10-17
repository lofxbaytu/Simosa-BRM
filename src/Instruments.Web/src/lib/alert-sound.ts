// 警報聲響(操船台):依 state.alerts[] 中「有聲響」(audible)者的最高優先序,以 Web Audio 產生短嗶聲。
//  - 緊急警報:連續快速嗶聲;alarm:三短聲、每 2 s 重複;warning:兩短聲、每 6 s 重複;caution 無聲(MSC.302 / IEC 62923 概念,
//    非型式認可的聲音特性);
//  - 核心的靜音(alertSilence)會使 audible 變 false,因此不需另行處理;本頁另有「聲響 開/關」設定(預設開,存在 localStorage);
//  - 瀏覽器要求使用者互動後才可發聲:AudioContext 只在 unlock()(第一次點擊/按鍵)時建立,之前只記錄應有的聲響型式;
//  - 測試(happy-dom 無 Web Audio)以 contextFactory 注入假物件,不依賴實際音訊。

import type { AlertItem, AlertPriority } from '../types/alerts.js';
import { PRIORITY_RANK } from './alerts.js';

/** 一聲嗶:相對週期起點的時間 (ms)、長度 (ms)、頻率 (Hz)。 */
export interface Beep {
  at_ms: number;
  dur_ms: number;
  freq: number;
}

export interface BeepPattern {
  beeps: readonly Beep[];
  /** 重複週期 (ms) */
  period_ms: number;
}

export const BEEP_PATTERNS: Readonly<Record<Exclude<AlertPriority, 'caution'>, BeepPattern>> = {
  emergencyAlarm: { beeps: [{ at_ms: 0, dur_ms: 180, freq: 1050 }, { at_ms: 250, dur_ms: 180, freq: 1050 }], period_ms: 500 },
  alarm: { beeps: [{ at_ms: 0, dur_ms: 150, freq: 880 }, { at_ms: 250, dur_ms: 150, freq: 880 }, { at_ms: 500, dur_ms: 150, freq: 880 }], period_ms: 2000 },
  warning: { beeps: [{ at_ms: 0, dur_ms: 120, freq: 660 }, { at_ms: 220, dur_ms: 120, freq: 660 }], period_ms: 6000 },
};

/** 應發出的聲響型式:有聲響警報中的最高優先序(caution 不發聲;無 → null)。 */
export function soundPriority(alerts: readonly AlertItem[] | undefined): Exclude<AlertPriority, 'caution'> | null {
  let best: AlertPriority | null = null;
  for (const a of alerts ?? []) {
    if (!a.audible || a.priority === 'caution') continue;
    if (best === null || PRIORITY_RANK[a.priority] < PRIORITY_RANK[best]) best = a.priority;
  }
  return best as Exclude<AlertPriority, 'caution'> | null;
}

/** AudioContext 用到的最小介面(便於測試注入)。 */
export interface AudioContextLike {
  readonly currentTime: number;
  readonly state: string;
  readonly destination: unknown;
  resume(): Promise<void>;
  createOscillator(): { type: string; frequency: { value: number }; connect(n: unknown): unknown; start(t?: number): void; stop(t?: number): void };
  createGain(): { gain: { value: number; setValueAtTime(v: number, t: number): unknown; linearRampToValueAtTime(v: number, t: number): unknown }; connect(n: unknown): unknown };
}

export interface AlertSounderOptions {
  contextFactory?: () => AudioContextLike | null;
  /** localStorage 鍵;null 表示不保存 */
  storageKey?: string | null;
  volume?: number;
}

export const ALERT_SOUND_KEY = 'simosa-brm.instruments.alertSound';

function defaultContext(): AudioContextLike | null {
  try {
    const Ctor = (globalThis as { AudioContext?: new () => AudioContextLike }).AudioContext;
    return Ctor ? new Ctor() : null;
  } catch {
    return null;
  }
}

export class AlertSounder {
  private ctx: AudioContextLike | null = null;
  private unlocked = false;
  private timer: ReturnType<typeof setInterval> | null = null;
  private current: Exclude<AlertPriority, 'caution'> | null = null;
  private _muted = false;
  private readonly factory: () => AudioContextLike | null;
  private readonly storageKey: string | null;
  private readonly volume: number;
  /** 已排程的嗶聲數(測試與除錯用) */
  beepsScheduled = 0;

  constructor(options: AlertSounderOptions = {}) {
    this.factory = options.contextFactory ?? defaultContext;
    this.storageKey = options.storageKey === undefined ? ALERT_SOUND_KEY : options.storageKey;
    this.volume = options.volume ?? 0.15;
    if (this.storageKey) {
      try {
        this._muted = localStorage.getItem(this.storageKey) === 'off';
      } catch {
        /* 私密視窗等情況:預設開 */
      }
    }
  }

  get muted(): boolean {
    return this._muted;
  }

  /** 目前應有的聲響型式(含靜音或未解鎖時) */
  get pattern(): Exclude<AlertPriority, 'caution'> | null {
    return this.current;
  }

  /** 目前是否真的在發聲(未靜音、已解鎖、有型式) */
  get sounding(): boolean {
    return this.timer !== null;
  }

  setMuted(muted: boolean): void {
    this._muted = muted;
    if (this.storageKey) {
      try {
        localStorage.setItem(this.storageKey, muted ? 'off' : 'on');
      } catch {
        /* 忽略 */
      }
    }
    this.restart();
  }

  /** 使用者互動後呼叫(點擊/按鍵):建立或恢復 AudioContext。 */
  unlock(): void {
    if (!this.ctx) this.ctx = this.factory();
    if (!this.ctx) return;
    this.unlocked = true;
    if (this.ctx.state === 'suspended') void this.ctx.resume().catch(() => undefined);
    this.restart();
  }

  /** 依目前警報清單更新聲響型式(每筆狀態呼叫;型式不變時不重排)。 */
  update(alerts: readonly AlertItem[] | undefined): void {
    const next = soundPriority(alerts);
    if (next === this.current) return;
    this.current = next;
    this.restart();
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  /** 結束並釋放 AudioContext。 */
  dispose(): void {
    this.stop();
    const ctx = this.ctx as unknown as { close?: () => Promise<void> } | null;
    this.ctx = null;
    this.unlocked = false;
    void ctx?.close?.().catch(() => undefined);
  }

  private restart(): void {
    this.stop();
    if (this._muted || !this.unlocked || !this.ctx || this.current === null) return;
    const pattern = BEEP_PATTERNS[this.current];
    this.playPattern(pattern);
    this.timer = setInterval(() => this.playPattern(pattern), pattern.period_ms);
  }

  private playPattern(p: BeepPattern): void {
    const ctx = this.ctx;
    if (!ctx) return;
    const t0 = ctx.currentTime + 0.02;
    for (const b of p.beeps) {
      try {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = 'square';
        osc.frequency.value = b.freq;
        const start = t0 + b.at_ms / 1000;
        const end = start + b.dur_ms / 1000;
        gain.gain.setValueAtTime(0, start);
        gain.gain.linearRampToValueAtTime(this.volume, start + 0.01);
        gain.gain.setValueAtTime(this.volume, end - 0.02);
        gain.gain.linearRampToValueAtTime(0, end);
        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.start(start);
        osc.stop(end + 0.01);
        this.beepsScheduled++;
      } catch {
        /* 音訊裝置不可用:靜默 */
      }
    }
  }
}
