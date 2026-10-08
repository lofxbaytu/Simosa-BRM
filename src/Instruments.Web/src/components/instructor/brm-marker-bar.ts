// BRM 快速標記鈕(規劃書第 9.1 節、第 8.2 節五大類):領導、狀況覺知、溝通、團隊合作、決策各一鈕,
// 按下發出 brm-mark 事件(含類別、備註、正向/待改進),由教官站加上時間戳存入;可匯出 JSON/CSV。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { live } from 'lit/directives/live.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import { BRM_CATEGORIES, brmCategory, type BrmCategory, type BrmMarker } from '../../lib/brm-markers.js';

export interface BrmMarkDetail {
  category: BrmCategory;
  note: string;
  polarity?: 'plus' | 'delta';
}

@customElement('brm-marker-bar')
export class BrmMarkerBar extends LitElement {
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
      .buttons {
        display: grid;
        grid-template-columns: repeat(5, 1fr);
        gap: 4px;
      }
      :host([large]) .buttons {
        grid-template-columns: repeat(5, 1fr);
      }
      .buttons button {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 2px;
        padding: 6px 2px;
        min-height: 44px;
        font-weight: 600;
        font-size: 13px;
      }
      :host([large]) .buttons button {
        min-height: 72px;
        font-size: 20px;
      }
      .buttons .key {
        font-size: 10px;
        color: var(--ob-text-muted);
        font-weight: 400;
      }
      .note {
        display: flex;
        gap: 4px;
      }
      .note input {
        flex: 1;
        min-width: 0;
      }
      :host([large]) .note input {
        font-size: 16px;
        min-height: 36px;
      }
      .polarity button {
        min-width: 44px;
      }
      .polarity button.plus.active {
        background: var(--ob-ok);
        border-color: var(--ob-ok);
        color: #fff;
      }
      .polarity button.delta.active {
        background: var(--ob-warning);
        border-color: var(--ob-warning);
        color: #1a1000;
      }
      .item .cat {
        font-weight: 600;
        white-space: nowrap;
      }
      .item .pol {
        font-size: 10px;
      }
      .item button {
        min-height: 20px;
        padding: 0 6px;
        font-size: 11px;
      }
      .list {
        max-height: 100%;
      }
    `,
  ];

  @property({ attribute: false }) markers: readonly BrmMarker[] = [];
  /** 大字版(簡化教官模式) */
  @property({ type: Boolean, reflect: true }) large = false;
  @property({ type: Boolean }) showList = true;

  @state() private note = '';
  @state() private polarity: 'plus' | 'delta' | undefined = undefined;

  private mark(category: BrmCategory): void {
    const detail: BrmMarkDetail = { category, note: this.note.trim() };
    if (this.polarity) detail.polarity = this.polarity;
    this.dispatchEvent(new CustomEvent<BrmMarkDetail>('brm-mark', { detail, bubbles: true, composed: true }));
    this.note = '';
  }

  private removeMarker(id: number): void {
    this.dispatchEvent(new CustomEvent<number>('brm-mark-remove', { detail: id, bubbles: true, composed: true }));
  }

  private exportAs(format: 'json' | 'csv'): void {
    this.dispatchEvent(new CustomEvent<'json' | 'csv'>('brm-export', { detail: format, bubbles: true, composed: true }));
  }

  private togglePolarity(p: 'plus' | 'delta'): void {
    this.polarity = this.polarity === p ? undefined : p;
  }

  override render() {
    return html`
      <div class="panel">
        <div class="panel-title"><span>BRM 標記 MARKERS</span><span class="zh">${this.markers.length} 筆 · 快速鍵 1–5</span></div>
        <div class="buttons" role="group" aria-label="BRM 快速標記">
          ${BRM_CATEGORIES.map(
            (c) => html`<button class="mark" data-category=${c.id} title=${c.elements} @click=${() => this.mark(c.id)}>
              <span>${c.label}</span><span class="key">${c.key} · ${c.en}</span>
            </button>`,
          )}
        </div>
        <div class="note">
          <span class="polarity btn-row">
            <button class="plus ${this.polarity === 'plus' ? 'active' : ''}" title="做得好(Plus)" @click=${() => this.togglePolarity('plus')}>＋</button>
            <button class="delta ${this.polarity === 'delta' ? 'active' : ''}" title="待改進(Delta)" @click=${() => this.togglePolarity('delta')}>Δ</button>
          </span>
          <input class="note-input" placeholder="一句備註(選填),再按類別鈕" .value=${live(this.note)} @input=${(e: Event) => (this.note = (e.target as HTMLInputElement).value)} />
        </div>
        ${this.showList
          ? html`<div class="list" aria-label="BRM 標記清單">
              ${this.markers.length === 0 ? html`<div class="hint">尚無標記</div>` : nothing}
              ${[...this.markers].reverse().map(
                (m) => html`<div class="item">
                  <span class="time">${shortTime(m.t)}</span>
                  <span class="cat">${brmCategory(m.category).label}</span>
                  ${m.polarity ? html`<span class="pol ${m.polarity === 'plus' ? 'ok' : 'bad'}">${m.polarity === 'plus' ? '＋' : 'Δ'}</span>` : nothing}
                  <span class="text">${m.note}</span>
                  <button title="刪除" @click=${() => this.removeMarker(m.id)}>×</button>
                </div>`,
              )}
            </div>`
          : nothing}
        <div class="btn-row">
          <button class="export-json" ?disabled=${this.markers.length === 0} @click=${() => this.exportAs('json')}>匯出 JSON</button>
          <button class="export-csv" ?disabled=${this.markers.length === 0} @click=${() => this.exportAs('csv')}>匯出 CSV</button>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-marker-bar': BrmMarkerBar;
  }
  interface HTMLElementEventMap {
    'brm-mark': CustomEvent<BrmMarkDetail>;
    'brm-mark-remove': CustomEvent<number>;
    'brm-export': CustomEvent<'json' | 'csv'>;
  }
}
