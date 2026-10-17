// 情境選擇(規劃書第 9.1 節執行控制「載入/啟動」):列出建置時匯入的 data/scenarios/*.yaml,
// 顯示 id、標題、船、裝載、環境摘要、時間表/航線/規則/參數引用數、簡報附件與角色;按「載入」發出 scenario-load 事件
// (由教官站送 loadScenario 指令)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles } from './instructor-shared.js';
import { environmentSummary, extensionSummary, initialSummary, loadingLabel, type ScenarioSummary } from '../../lib/scenario.js';
import { PLAYED_BY_LABEL } from '../../lib/timeline.js';
import { ROLE_LABEL } from '../../lib/sim-event.js';

export type ScenarioLoadEvent = CustomEvent<ScenarioSummary>;

@customElement('brm-scenario-panel')
export class ScenarioPanel extends LitElement {
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
      .item {
        flex-direction: column;
        align-items: stretch;
        gap: 1px;
        cursor: pointer;
      }
      .item .head {
        display: flex;
        gap: 6px;
        align-items: baseline;
      }
      .item .id {
        font-weight: 600;
        font-variant-numeric: tabular-nums;
      }
      .item .name {
        flex: 1;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .item .meta {
        font-size: 11px;
        color: var(--ob-text-muted);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .detail {
        font-size: 11px;
        color: var(--ob-text-secondary);
        border-top: 1px solid var(--ob-border);
        padding-top: 4px;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .detail .desc {
        color: var(--ob-text);
        white-space: pre-wrap;
      }
      .issues {
        color: var(--ob-warning);
      }
      .meta.ext {
        color: var(--ob-accent);
      }
      .attachments a {
        color: var(--ob-accent);
        margin-right: 6px;
      }
    `,
  ];

  @property({ attribute: false }) scenarios: ScenarioSummary[] = [];
  /** 目前核心載入中的情境 id(顯示用) */
  @property({ type: String }) activeId = '';
  @property({ type: Boolean }) compact = false;

  @state() private selectedId = '';

  private get selected(): ScenarioSummary | undefined {
    return this.scenarios.find((s) => s.id === this.selectedId) ?? this.scenarios[0];
  }

  private select(s: ScenarioSummary): void {
    this.selectedId = s.id;
    this.dispatchEvent(new CustomEvent<ScenarioSummary>('scenario-select', { detail: s, bubbles: true, composed: true }));
  }

  private load(): void {
    const s = this.selected;
    if (!s) return;
    this.dispatchEvent(new CustomEvent<ScenarioSummary>('scenario-load', { detail: s, bubbles: true, composed: true }));
  }

  override render() {
    const sel = this.selected;
    return html`
      <div class="panel">
        <div class="panel-title"><span>情境 SCENARIO</span><span class="zh">${this.scenarios.length} 個(data/scenarios)</span></div>
        <div class="list" role="listbox" aria-label="情境清單">
          ${this.scenarios.length === 0 ? html`<div class="hint">沒有情境檔(data/scenarios/*.yaml)</div>` : nothing}
          ${this.scenarios.map(
            (s) => html`
              <div class="item ${sel?.id === s.id ? 'selected' : ''}" role="option" aria-selected=${sel?.id === s.id} @click=${() => this.select(s)}>
                <div class="head">
                  <span class="id">${s.id}</span>
                  <span class="name" title=${s.name}>${s.name}</span>
                  ${this.activeId === s.id ? html`<span class="tag ok">已載入</span>` : nothing}
                  ${s.issues.length > 0 ? html`<span class="tag unsupported" title=${s.issues.join('; ')}>有問題</span>` : nothing}
                </div>
                <div class="meta">${s.shipId} · ${loadingLabel(s.loading)} · ${environmentSummary(s.environment)}</div>
                ${extensionSummary(s) ? html`<div class="meta ext">${extensionSummary(s)}</div>` : nothing}
              </div>
            `,
          )}
        </div>
        ${sel && !this.compact
          ? html`<div class="detail">
              <div class="desc">${sel.description || '(無說明)'}</div>
              <div>初始:${initialSummary(sel.initial)}</div>
              <div>種子 ${sel.seed} · 倍率 ×${sel.timeScale}${sel.startTimeUtc ? ` · 開始 ${sel.startTimeUtc}` : ''}</div>
              <div class="muted">${sel.path}${sel.assessment.parameterSet ? ` · 參數集 ${sel.assessment.parameterSet}` : ''}</div>
              ${sel.briefingDoc.roles.length > 0
                ? html`<div class="roles">角色:${sel.briefingDoc.roles.map((r) => `${ROLE_LABEL[r.role] ?? r.role}(${PLAYED_BY_LABEL[r.playedBy] ?? r.playedBy}${r.required ? ',必要' : ''})`).join('、')}${sel.briefingDoc.minParticipants !== undefined ? ` · 最少 ${sel.briefingDoc.minParticipants} 人` : ''}</div>`
                : nothing}
              ${sel.briefingDoc.attachments.length > 0
                ? html`<div class="attachments">附件:${sel.briefingDoc.attachments.map((a) => html`<a href=${`data/scenarios/${sel.id}/${a.file}`} target="_blank" rel="noopener" title=${a.file}>${a.title}</a>`)}</div>`
                : nothing}
              ${sel.issues.length > 0 ? html`<div class="issues">${sel.issues.join('; ')}</div>` : nothing}
            </div>`
          : nothing}
        <div class="btn-row">
          <button class="primary load" ?disabled=${!sel} @click=${this.load}>載入${sel ? ` ${sel.id}` : ''}</button>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-scenario-panel': ScenarioPanel;
  }
  interface HTMLElementEventMap {
    'scenario-load': ScenarioLoadEvent;
    'scenario-select': ScenarioLoadEvent;
  }
}
