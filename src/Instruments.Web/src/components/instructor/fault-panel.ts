// 故障注入(規劃書第 9.1 節「故障即時注入與清除、現行故障清單」、第 7.2 節故障目錄):
// 目錄按鈕注入、現行故障(由狀態 faults 欄位)逐項清除或全部清除;可輸入自訂名稱。
// 目錄取自 src/Contracts/fault-catalogue.json(核心改變行為的故障為按鈕;只記錄者收在「其他故障」選單);
// 有參數的故障(gyroDrift 漂移率、gpsJump 距離/方位…)可在「參數注入」列填值,送 injectFault {name, args}(sensors-alerts.md 第 2.2 節)。

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
      .custom input,
      .custom select {
        flex: 1;
        min-width: 0;
      }
      .args {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        align-items: center;
        font-size: 11px;
      }
      .args input {
        width: 64px;
      }
      .alert-tag {
        font-size: 10px;
        margin-left: 3px;
        padding: 0 3px;
        border-radius: 3px;
        border: 1px solid var(--ob-alarm);
        color: var(--ob-alarm);
      }
      .alert-tag.warning {
        border-color: var(--ob-warning);
        color: var(--ob-warning);
      }
    `,
  ];

  /** 核心回報的現行故障(state.faults) */
  @property({ attribute: false }) active: string[] = [];

  @state() private custom = '';
  @state() private other = '';
  @state() private argFault = '';
  @state() private argValues: Record<string, string> = {};
  private readonly catalogue = faultCatalogue();

  private inject(name: string, args?: Record<string, number | undefined>): void {
    if (!name.trim()) return;
    dispatchCommand(this, injectFaultCommand(name, args));
  }

  private injectWithArgs(): void {
    const def = this.catalogue.faults.find((f) => f.name === this.argFault);
    if (!def) return;
    const args: Record<string, number | undefined> = {};
    for (const key of Object.keys(def.args ?? {})) {
      const raw = (this.argValues[key] ?? '').trim();
      if (raw !== '' && Number.isFinite(Number(raw))) args[key] = Number(raw);
    }
    this.inject(def.name, args);
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
    const src =
      this.catalogue.source === 'contracts'
        ? '目錄:Contracts/fault-catalogue.json'
        : this.catalogue.source === 'simcore'
          ? '名稱來源:SimCore FaultNames.cs'
          : '名稱來源:內建預設(讀不到 SimCore FaultNames.cs)';
    // 目錄來自 fault-catalogue.json 時,只記錄的故障收進選單(避免面板過長);其他來源維持全部按鈕
    const grouped = this.catalogue.source === 'contracts';
    const buttons = grouped ? this.catalogue.faults.filter((f) => f.modelled) : this.catalogue.faults;
    const others = grouped ? this.catalogue.faults.filter((f) => !f.modelled) : [];
    const withArgs = this.catalogue.faults.filter((f) => f.args && Object.keys(f.args).length > 0);
    const argDef = withArgs.find((f) => f.name === this.argFault);
    return html`
      <div class="panel">
        <div class="panel-title"><span>故障 FAULTS</span><span class="zh">${this.active.length > 0 ? `現行 ${this.active.length}` : '無故障'}</span></div>
        <div class="catalogue">
          ${buttons.map(
            (f) => html`<button class="fault ${this.active.includes(f.name) ? 'active' : ''}" data-fault=${f.name} title=${f.effect ? `${f.name}:${f.effect}` : f.name} @click=${() => this.toggle(f)}>
              <span>${f.label}${f.modelled ? '' : html`<span class="tag unsupported">僅記錄</span>`}${f.alert ? html`<span class="alert-tag ${f.alert === 'warning' ? 'warning' : ''}" title="故障成立時的 BAM 警報">${f.alert === 'warning' ? '警告' : '警報'}</span>` : nothing}</span>
              <span class="ex">${f.exercise ?? ''}</span>
            </button>`,
          )}
        </div>
        ${others.length > 0
          ? html`<div class="custom">
              <select aria-label="其他故障(僅記錄)" .value=${this.other} @change=${(e: Event) => (this.other = (e.target as HTMLSelectElement).value)}>
                <option value="">其他故障(僅記錄)…</option>
                ${others.map((f) => html`<option value=${f.name}>${f.label}${f.exercise ? `(${f.exercise})` : ''}</option>`)}
              </select>
              <button class="inject-other" ?disabled=${!this.other} @click=${() => this.inject(this.other)}>注入</button>
            </div>`
          : nothing}
        ${withArgs.length > 0
          ? html`<div class="args">
              <select aria-label="參數注入" .value=${this.argFault} @change=${(e: Event) => {
                this.argFault = (e.target as HTMLSelectElement).value;
                this.argValues = {};
              }}>
                <option value="">參數注入…</option>
                ${withArgs.map((f) => html`<option value=${f.name}>${f.label}</option>`)}
              </select>
              ${argDef
                ? Object.entries(argDef.args ?? {}).map(
                    ([k, def]) => html`<label>${k} <input type="number" step="any" data-arg=${k} placeholder=${def === null ? '—' : String(def)} .value=${this.argValues[k] ?? ''} @input=${(e: Event) => (this.argValues = { ...this.argValues, [k]: (e.target as HTMLInputElement).value })} /></label>`,
                  )
                : nothing}
              <button class="inject-args" ?disabled=${!argDef} @click=${this.injectWithArgs}>注入</button>
            </div>`
          : nothing}
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
