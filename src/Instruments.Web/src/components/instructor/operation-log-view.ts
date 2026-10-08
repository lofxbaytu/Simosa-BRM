// 學員操作紀錄串流(規劃書第 9.1 節監看):由狀態推導的舵令/車鐘/側推/凍結/故障事件,最新在上。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime, wallTime } from './instructor-shared.js';
import type { OperationEvent, OperationKind } from '../../lib/operation-log.js';

const KIND_CLASS: Partial<Record<OperationKind, string>> = {
  fault: 'bad',
  aground: 'bad',
  collision: 'bad',
  freeze: 'caution',
  timeReset: 'caution',
};

@customElement('brm-operation-log')
export class OperationLogView extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
        min-height: 0;
      }
      .panel {
        height: 100%;
      }
      .item .wall {
        color: var(--ob-text-muted);
        font-size: 10px;
      }
    `,
  ];

  @property({ attribute: false }) events: readonly OperationEvent[] = [];
  @property({ type: Number }) max = 300;

  override render() {
    const items = this.events.slice(-this.max).reverse();
    return html`
      <div class="panel">
        <div class="panel-title"><span>學員操作 OPERATIONS</span><span class="zh">${this.events.length} 筆(由狀態推導)</span></div>
        <div class="list" aria-label="學員操作紀錄">
          ${items.length === 0 ? html`<div class="hint">尚無操作;舵令、車鐘、側推變更會依狀態變化在此列出</div>` : ''}
          ${items.map(
            (e) => html`<div class="item">
              <span class="time">${shortTime(e.t)}</span>
              <span class="text ${KIND_CLASS[e.kind] ?? ''}">${e.label}</span>
              <span class="wall">${wallTime(e.wall)}</span>
            </div>`,
          )}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-operation-log': OperationLogView;
  }
}
