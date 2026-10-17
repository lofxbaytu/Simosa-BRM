// 雷達頁面板元件的基底:持有 RadarStore 並在其變更時重繪。

import { LitElement } from 'lit';
import { property } from 'lit/decorators.js';
import type { RadarStore } from './radar-store.js';

export class StoreElement extends LitElement {
  @property({ attribute: false }) store!: RadarStore;
  private unsub: (() => void) | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    this.bind();
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.unsub?.();
    this.unsub = null;
  }

  override willUpdate(changed: Map<string, unknown>): void {
    if (changed.has('store')) this.bind();
  }

  private bind(): void {
    this.unsub?.();
    this.unsub = this.store ? this.store.subscribe(() => this.requestUpdate()) : null;
  }
}

/** 數值輸入框的值(無效時回傳 fallback) */
export function inputNumber(e: Event, fallback: number): number {
  const v = Number((e.target as HTMLInputElement).value);
  return Number.isFinite(v) ? v : fallback;
}
