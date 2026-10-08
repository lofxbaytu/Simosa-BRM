// 故障注入(規劃書第 9.1 節「故障即時注入與清除、現行故障清單」、第 7.2 節故障目錄):
// 目錄按鈕注入、現行故障(由狀態 faults 欄位)逐項清除或全部清除;可輸入自訂名稱。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles } from './instructor-shared.js';
import { clearFaultCommand, faultCatalogue, injectFaultCommand, type FaultDefinition } from '../../lib/instructor-command.js';

@customElement('brm-fault-panel')
export class FaultPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .catalogue {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 4px;
      }
      .catalogue button {
        text-align: left;
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 4px;
      }
      .catalogue button.active {
        background: var(--ob-warning);
        color: #1a1000;
        border-color: var(--ob-warning);
      }
      .catalogue .ex {
        font-size: 10px;
        opacity: 0.75;
      }
      .active-list .item {
        border-color: var(--ob-warning);
      }
      .custom {
        display: flex;
        gap: 4px;
      }
      .custom input {
        flex: 1;
        min-width: 0;
      }
    `,
  ];

  /** 核心回報的現行故障(state.faults) */
  @property({ attribute: false }) active: string[] = [];

  @state() private custom = '';
  private readonly catalogue = faultCatalogue();

  private inject(name: string): void {
    if (!name.trim()) return;
    dispatchCommand(this, injectFaultCommand(name));
  }

  private clear(name: string): void {
    dispatchCommand(this, clearFaultCommand(name));
  }

  /** 全部清除:逐項送 clearFault(不帶名稱的 clearFault 只有 C# 核心會清全部,Python 參考伺服器無作用)。 */
  private clearAll(): void {
    for (const name of this.active) dispatchCommand(this, clearFaultCommand(name));
  }

  private toggle(f: FaultDefinition): void {
    if (this.active.includes(f.name)) this.clear(f.name);
    else this.inject(f.name);
  }

  private labelOf(name: string): string {
    return this.catalogue.faults.find((f) => f.name === name)?.label ?? name;
  }

  override render() {
    const src = this.catalogue.source === 'simcore' ? '名稱來源:SimCore FaultNames.cs' : '名稱來源:內建預設(讀不到 SimCore FaultNames.cs)';
    return html`
      <div class="panel">
        <div class="panel-title"><span>故障 FAULTS</span><span class="zh">${this.active.length > 0 ? `現行 ${this.active.length}` : '無故障'}</span></div>
        <div class="catalogue">
          ${this.catalogue.faults.map(
            (f) => html`<button class="fault ${this.active.includes(f.name) ? 'active' : ''}" data-fault=${f.name} title=${f.name} @click=${() => this.toggle(f)}>
              <span>${f.label}${f.modelled ? '' : html`<span class="tag unsupported">僅記錄</span>`}</span>
              <span class="ex">${f.exercise ?? ''}</span>
            </button>`,
          )}
        </div>
        <div class="custom">
          <input placeholder="自訂故障名稱" .value=${this.custom} @input=${(e: Event) => (this.custom = (e.target as HTMLInputElement).value)} @keydown=${(e: KeyboardEvent) => e.key === 'Enter' && this.inject(this.custom)} />
          <button @click=${() => this.inject(this.custom)} ?disabled=${!this.custom.trim()}>注入</button>
        </div>
        ${this.active.length > 0
          ? html`<div class="list active-list" aria-label="現行故障">
              ${this.active.map(
                (name) => html`<div class="item"><span class="text alarm">${this.labelOf(name)}</span><span class="muted">${name}</span><button @click=${() => this.clear(name)}>清除</button></div>`,
              )}
              <div class="btn-row"><button class="clear-all" @click=${this.clearAll}>全部清除</button></div>
            </div>`
          : nothing}
        <div class="hint">${src};「僅記錄」= 核心只記錄與廣播,不改變行為</div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-fault-panel': FaultPanel;
  }
}
