// 情境選擇(規劃書第 9.1 節執行控制「載入/啟動」):列出建置時匯入的 data/scenarios/*.yaml,
// 顯示 id、標題、船、裝載、環境摘要、時間表/航線/規則/參數引用數、簡報附件與角色,以及簡報結構(規劃時間倒數、同儕評估提示、
// 講評引導問題;<brm-briefing-block>,R7);按「載入」發出 scenario-load 事件(由教官站送 loadScenario 指令)。
// 版面:面板高度由外層決定(教官站左欄捲動區內至少 250 px;簡化模式選擇頁整欄)。完整教官站:清單與詳細區平分剩餘高度
// (清單至少 80 px、詳細區至少 120 px;詳細區不超過自己的內容高度,放不下時在面板內捲動);簡化模式(simple 屬性):詳細區優先取自然高度、
// 清單吃剩餘高度。「載入」鈕固定在面板底部,不會被詳細區擠出面板(第二十九輪 L2);鈕內與清單的情境 id、詳細區的檔案路徑可換行
// (底線後優先斷行),長 id(E20_comprehensive_assessment_kaohsiung)不撐寬面板、不以 … 截斷(第二十九輪 L3)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { keyed } from 'lit/directives/keyed.js';
import { tileStyles } from '../shared.js';
import { instructorStyles } from './instructor-shared.js';
import { environmentSummary, extensionSummary, initialSummary, loadingLabel, type ScenarioSummary } from '../../lib/scenario.js';
import { PLAYED_BY_LABEL } from '../../lib/timeline.js';
import { ROLE_LABEL, type ScenarioLoadRejection } from '../../lib/sim-event.js';
import './briefing-block.js';

export type ScenarioLoadEvent = CustomEvent<ScenarioSummary>;

@customElement('brm-scenario-panel')
export class ScenarioPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      /* 尺寸容器(container: scenario):高度由外層決定(不隨內容),面板矮時清單與詳細區的下限改小(@container scenario,見下) */
      :host {
        display: flex;
        flex-direction: column;
        min-height: 0;
        container: scenario / size;
      }
      .panel {
        flex: 1 1 auto;
        min-height: 0;
      }
      /* 清單與詳細區平分剩餘高度:清單至少 80 px(約 2 個情境),詳細區至少 120 px、最多到自己的內容高度(多的給清單);
         拒絕說明可縮(自己捲動),「載入」鈕列不縮。兩個捲動區都只直向捲動:長 id 與路徑換行,不出現水平捲軸 */
      .list {
        flex: 1 1 0;
        min-height: 80px;
        overflow-x: hidden;
      }
      /* 簡化模式選擇頁(整欄高):詳細區優先取自然高度(講評引導問題的標題不被推到可見範圍外),清單吃剩餘高度、至少 96 px */
      :host([simple]) .list {
        min-height: 96px;
      }
      :host([simple]) .detail {
        flex: 0 1 auto;
      }
      .btn-row {
        flex: 0 0 auto;
      }
      /* 「載入 <id>」:按鈕不比所在的列寬,長 id 在底線後換行(<wbr>),仍放不下時任意處換行;完整文字都看得到 */
      .btn-row button.load {
        min-width: 0;
        max-width: 100%;
        overflow-wrap: anywhere;
      }
      /* 選擇器要比共用樣式的 .list .item(align-items: baseline)明確:否則各行不會撐滿項目寬度,
         過長的摘要行不是以 … 截斷而是把清單撐出水平捲動 */
      .list .item {
        flex-direction: column;
        align-items: stretch;
        gap: 1px;
        cursor: pointer;
      }
      /* 標題列:id 很長時標題換到下一行(標題的基準寬 8em);id 本身也可在底線後換行,不撐寬清單 */
      .item .head {
        display: flex;
        flex-wrap: wrap;
        gap: 0 6px;
        align-items: baseline;
      }
      .item .id {
        font-weight: 600;
        font-variant-numeric: tabular-nums;
        min-width: 0;
        overflow-wrap: anywhere;
      }
      .item .name {
        flex: 1 1 8em;
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
        flex: 1 1 0;
        min-height: 120px;
        max-height: max-content;
        overflow-y: auto;
        overflow-x: hidden;
        overflow-wrap: anywhere;
        scrollbar-width: thin;
        font-size: 11px;
        color: var(--ob-text-secondary);
        border-top: 1px solid var(--ob-border);
        padding-top: 4px;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      /* 詳細區是直向 flex 容器:子項不可被壓縮(否則文字互相疊上),超出的部分靠詳細區自己捲動 */
      .detail > * {
        flex: 0 0 auto;
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
      .rejected {
        flex: 0 1 auto;
        min-height: 44px;
        overflow-y: auto;
        scrollbar-width: thin;
        font-size: 11px;
        color: var(--ob-alarm);
        border: 1px solid var(--ob-alarm);
        border-radius: 3px;
        padding: 4px 6px;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .rejected > * {
        flex: 0 0 auto;
      }
      .rejected .title {
        font-weight: 600;
      }
      .rejected ul {
        margin: 0;
        padding-left: 16px;
      }
      .rejected .muted {
        color: var(--ob-text-muted);
      }
      /* 面板矮於 294 px(1280×720 與 1093×614 的完整教官站都是下限 260 px):清單至少 64 px、詳細區至少 96 px,
         兩行的「載入 <長 id>」鈕仍在面板內(放在最後:蓋過上面 .list / .detail 的下限)。
         294 px 以上用一般的 80 / 120 px:標題、「載入」鈕列(長 id 兩行)與間距約 94 px,294 = 94 + 80 + 120 剛好放得下
         (再低清單就不到 80 px;例:290 px 時只剩 76 px)。1366×768 的面板平時是 300 px,核心進入有階段的情境
         (教官站 header 多一個「階段 waiting」徽章、折成兩行 72 px)時是 297.6 px,兩者都套用 120 / 80 px
         (第二十九輪 L5:L4 的門檻是 300 px,階段徽章出現時詳細區退回 96 px;L4 之前寫成 max-height: 300px 含等於,
         1366×768 / 1536×864 都只有 96 px) */
      @container scenario (height < 294px) {
        .list {
          min-height: 64px;
        }
        .detail {
          min-height: 96px;
        }
      }
    `,
  ];

  @property({ attribute: false }) scenarios: ScenarioSummary[] = [];
  /** 目前核心載入中的情境 id(顯示用) */
  @property({ type: String }) activeId = '';
  @property({ type: Boolean }) compact = false;
  /** 簡化教官模式的選擇頁:詳細區優先取自然高度(完整教官站左欄則與清單平分) */
  @property({ type: Boolean, reflect: true }) simple = false;
  /** 核心拒絕上一次載入的原因(scenarioLoadRejected 事件 / Python ack ok=false);null = 無 */
  @property({ attribute: false }) rejection: ScenarioLoadRejection | null = null;
  /** 已送 loadScenario、等待核心結果:載入鈕停用並顯示載入中 */
  @property({ type: Boolean }) busy = false;

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
                  <span class="id">${breakableId(s.id)}</span>
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
              <div class="muted path">${breakableId(sel.path, /([_/])/)}${sel.assessment.parameterSet ? ` · 參數集 ${sel.assessment.parameterSet}` : ''}</div>
              ${sel.briefingDoc.roles.length > 0
                ? html`<div class="roles">角色:${sel.briefingDoc.roles.map((r) => `${ROLE_LABEL[r.role] ?? r.role}(${PLAYED_BY_LABEL[r.playedBy] ?? r.playedBy}${r.required ? ',必要' : ''})`).join('、')}${sel.briefingDoc.minParticipants !== undefined ? ` · 最少 ${sel.briefingDoc.minParticipants} 人` : ''}</div>`
                : nothing}
              ${sel.briefingDoc.attachments.length > 0
                ? html`<div class="attachments">附件:${sel.briefingDoc.attachments.map((a) => html`<a href=${`data/scenarios/${sel.id}/${a.file}`} target="_blank" rel="noopener" title=${a.file}>${a.title}</a>`)}</div>`
                : nothing}
              ${keyed(sel.id, html`<brm-briefing-block .briefing=${sel.briefingDoc} .scenarioId=${sel.id}></brm-briefing-block>`) /* 換情境即換元素:倒數不帶到下一個情境(同分鐘數的 E22a–d、E20 兩檔;審查第二十六輪) */}
              ${sel.issues.length > 0 ? html`<div class="issues">${sel.issues.join('; ')}</div>` : nothing}
            </div>`
          : nothing}
        ${this.rejection ? renderRejection(this.rejection) : nothing}
        <div class="btn-row">
          <button class="primary load" ?disabled=${!sel || this.busy} @click=${this.load}>${this.busy ? '載入中…' : html`載入${sel ? html` ${breakableId(sel.id)}` : nothing}`}</button>
        </div>
      </div>
    `;
  }
}

/**
 * 長識別字(情境 id、檔案路徑)的換行點:在每個分隔字元(預設底線)後插入 <wbr>,窄欄內優先在這些位置換行;
 * 文字內容(textContent)不變,測試與複製貼上仍得到原字串。
 */
export function breakableId(text: string, separator: RegExp = /(_)/) {
  const parts = text.split(separator);
  return parts.map((p, i) => (i % 2 === 1 && i < parts.length - 1 ? html`${p}<wbr />` : p));
}

/** 核心拒絕載入的說明區塊(情境面板與靠離泊預演面板共用):摘要訊息與問題清單(path:message),核心狀態不變。 */
export function renderRejection(r: ScenarioLoadRejection) {
  return html`<div class="rejected" role="alert">
    <div class="title">核心拒絕載入${r.scenarioId ? ` ${r.scenarioId}` : ''}(狀態不變,仍為原情境)</div>
    <div>${r.message}</div>
    ${r.issues.length > 0
      ? html`<ul>
          ${r.issues.map((i) => html`<li>${i.severity === 'warning' ? '[警告] ' : ''}${i.path ? `${i.path}:` : ''}${i.message}</li>`)}
        </ul>`
      : nothing}
    <div class="muted">修正後重新載入;${r.origin === 'ack' ? 'Python 參考伺服器 ack' : r.origin === 'file' ? '情境檔' : r.origin === 'inline' ? '內嵌情境' : '指令'} · t=${r.t.toFixed(0)} s</div>
  </div>`;
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
