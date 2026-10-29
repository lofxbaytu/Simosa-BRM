// 完整教官模式「情境編輯」分頁(規劃書第 9.1 節「教官依範本組出新練習(環境、目標船、2 個故障、評估規則)≤30 分鐘」;R4;第 14 章教官第 2 列):
// 選範本(任一既有情境的 YAML 原文或空白範本)→ 編輯基本資料、船與裝載、初始狀態、環境、目標船、時間表故障、評估規則 →
// 即時以 parseScenario 與編輯器檢查顯示問題清單 → 「下載 YAML」或「載入核心」(scenario-editor-load 事件;教官站以 loadScenario args.scenario
// 內嵌送核心,沿用 loadScenarioSummary 的樂觀切換與 scenarioLoadRejected 還原)。草稿每次修改存 localStorage(船上離線;重新整理後還原)。
// 草稿模型與輸出見 lib/scenario-editor.ts;未被表單涵蓋的欄位原樣保留。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles } from './instructor-shared.js';
import { renderRejection } from './scenario-panel.js';
import { downloadText } from '../../lib/download.js';
import { loadScenarioSources, loadingLabel, type ScenarioSummary } from '../../lib/scenario.js';
import { HARD_FAIL_RULE_LABEL } from '../../lib/assessment.js';
import type { ScenarioLoadRejection } from '../../lib/sim-event.js';
import { SHIP_IDS, TELEGRAPH_POSITIONS, type LoadingCondition, type ShipId, type TelegraphPosition } from '../../types/state.js';
import {
  BAND_METRICS,
  HARD_FAIL_RULES,
  PHASES,
  SIMPLE_HARD_FAILS,
  TARGET_MODES,
  TARGET_MODE_LABEL,
  TARGET_SHIP_TYPES,
  addBand,
  addFaultItem,
  addHardFail,
  addTarget,
  blankDraft,
  clearDraft,
  dayNightLabel,
  describeTimeline,
  faultOptions,
  fromYaml,
  hardFailText,
  hasHardFail,
  loadDraft,
  numberOrRefFromText,
  numberOrRefText,
  parseObjectText,
  patchBand,
  patchTarget,
  positionMode,
  removeBand,
  removeHardFail,
  removeTarget,
  removeTimelineItem,
  saveDraft,
  setHardFail,
  validateDraft,
  type DraftEnvironment,
  type DraftGeneral,
  type DraftInitial,
  type DraftPosition,
  type DraftStorage,
  type DraftTarget,
  type DraftValidation,
  type FaultItemSpec,
  type NumberOrRef,
  type Patch,
  type PositionMode,
  type ScenarioDraft,
  stripUndefined,
} from '../../lib/scenario-editor.js';

/** 「載入核心」事件內容(與靠離泊預演結果相同的用法:summary 供樂觀切換,scenario 為 loadScenario args.scenario)。 */
export interface ScenarioEditorResult {
  id: string;
  scenario: Record<string, unknown>;
  yaml: string;
  summary: ScenarioSummary;
}

export type ScenarioEditorLoadEvent = CustomEvent<ScenarioEditorResult>;

const LOADINGS: readonly LoadingCondition[] = ['full', 'ballast', 'intermediate'];

interface FaultForm {
  id: string;
  name: string;
  fault: string;
  trigger: 'time' | 'manual' | 'condition';
  atLeast: string;
  condition: string;
  args: string;
  recordOnly: boolean;
  phase: string;
  message: string;
}

interface HardFailForm {
  rule: string;
  value: string;
}

function textOf(ev: Event): string {
  return (ev.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;
}

function checked(ev: Event): boolean {
  return (ev.target as HTMLInputElement).checked;
}

@customElement('brm-scenario-editor')
export class ScenarioEditor extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
        min-height: 0;
        height: 100%;
        overflow: hidden;
      }
      :host([hidden]) {
        display: none !important;
      }
      .editor {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 6px;
        height: 100%;
        min-height: 0;
      }
      .col {
        display: flex;
        flex-direction: column;
        gap: 6px;
        min-height: 0;
        overflow-y: auto;
      }
      .panel {
        flex: 0 0 auto;
      }
      .form {
        grid-template-columns: auto 1fr auto 1fr;
      }
      .form .full {
        grid-column: 1 / -1;
      }
      .form textarea {
        width: 100%;
        min-height: 48px;
        resize: vertical;
        font: inherit;
        font-size: 12px;
      }
      .pair {
        display: flex;
        gap: 4px;
        align-items: center;
      }
      .pair input {
        width: 100%;
        min-width: 0;
      }
      .pair span {
        color: var(--ob-text-muted);
        white-space: nowrap;
        font-size: 11px;
      }
      .row {
        display: grid;
        gap: 4px 6px;
        align-items: center;
        font-size: 12px;
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 4px 6px;
      }
      .row.target {
        grid-template-columns: auto 1fr auto 1fr auto 1fr;
      }
      .row.band {
        grid-template-columns: auto 1fr auto 1fr auto 1fr;
      }
      .row label {
        color: var(--ob-text-secondary);
        white-space: nowrap;
      }
      .row input,
      .row select {
        width: 100%;
        min-width: 0;
      }
      .row .tail {
        grid-column: 1 / -1;
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 6px;
      }
      .item .text .sub {
        color: var(--ob-text-muted);
        font-size: 11px;
      }
      .item .text .bad {
        font-size: 11px;
      }
      button.small {
        min-height: 22px;
        padding: 1px 6px;
        font-size: 11px;
      }
      .issues {
        font-size: 12px;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .issues .issue {
        color: var(--ob-alarm);
      }
      .issues .warning {
        color: var(--ob-warning);
      }
      .issues .ok {
        color: var(--ob-ok);
      }
      .error {
        color: var(--ob-warning);
        font-size: 12px;
      }
      .rejected {
        font-size: 11px;
        color: var(--ob-alarm);
        border: 1px solid var(--ob-alarm);
        border-radius: 3px;
        padding: 4px 6px;
        display: flex;
        flex-direction: column;
        gap: 2px;
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
      pre.yaml {
        margin: 0;
        max-height: 320px;
        overflow: auto;
        font-size: 11px;
        background: var(--ob-bg-surface-raised);
        border: 1px solid var(--ob-border);
        border-radius: 3px;
        padding: 4px 6px;
        white-space: pre;
      }
      fieldset {
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 4px 8px;
        margin: 0;
        display: grid;
        grid-template-columns: auto 1fr auto 1fr;
        gap: 4px 8px;
        font-size: 12px;
        align-items: center;
      }
      fieldset .full {
        grid-column: 1 / -1;
      }
      legend {
        color: var(--ob-text-muted);
        font-size: 11px;
        padding: 0 4px;
      }
      .checks {
        display: flex;
        flex-wrap: wrap;
        gap: 4px 12px;
        font-size: 12px;
      }
      .checks label {
        display: inline-flex;
        gap: 4px;
        align-items: center;
      }
    `,
  ];

  /** 教官站已載入的情境清單(範本選單) */
  @property({ attribute: false }) scenarios: ScenarioSummary[] = [];
  /** 範本 YAML 原文(鍵 = ScenarioSummary.path);null = 建置時匯入的 data/scenarios */
  @property({ attribute: false }) sources: Record<string, string> | null = null;
  /** 核心拒絕上一次「載入核心」的原因;null = 無 */
  @property({ attribute: false }) rejection: ScenarioLoadRejection | null = null;
  /** 已送 loadScenario、等待核心結果 */
  @property({ type: Boolean }) busy = false;
  /** 草稿儲存(測試注入;undefined = 瀏覽器 localStorage) */
  @property({ attribute: false }) storage: DraftStorage | null | undefined = undefined;

  @state() private draft: ScenarioDraft = blankDraft();
  @state() private validation: DraftValidation = validateDraft(this.draft);
  @state() private templateChoice = '';
  @state() private restored = false;
  @state() private saved = true;
  @state() private error = '';
  @state() private showYaml = false;
  @state() private fault: FaultForm = { id: '', name: '', fault: 'steeringGear', trigger: 'time', atLeast: '300', condition: '', args: '', recordOnly: false, phase: 'degraded', message: '' };
  @state() private hardFailForm: HardFailForm = { rule: 'cpa', value: '0.3' };

  private readonly faults = faultOptions();

  /** 目前草稿(測試用) */
  get value(): ScenarioDraft {
    return this.draft;
  }

  /** 目前驗證結果(測試用) */
  get lastValidation(): DraftValidation {
    return this.validation;
  }

  override connectedCallback(): void {
    super.connectedCallback();
    const stored = loadDraft(this.storageArg());
    if (stored) {
      this.draft = stored;
      this.validation = validateDraft(stored);
      this.templateChoice = stored.template;
      this.restored = true;
    }
    this.fault = { ...this.fault, id: this.nextFaultId() };
  }

  private storageArg(): DraftStorage | null | undefined {
    return this.storage;
  }

  /** 選單的值以 data-value 給:Lit 依文件順序提交,.value 會在動態 <option> 渲染前提交而失效,故每次渲染後再同步。 */
  override updated(): void {
    for (const sel of this.renderRoot.querySelectorAll<HTMLSelectElement>('select[data-value]')) {
      const want = sel.dataset['value'] ?? '';
      if (sel.value !== want) sel.value = want;
    }
  }

  private patchFault(p: Partial<FaultForm>): void {
    this.fault = { ...this.fault, ...p };
  }

  private setDraft(d: ScenarioDraft): void {
    this.draft = d;
    this.validation = validateDraft(d);
    this.restored = false;
    this.error = '';
    this.saved = this.storage === undefined ? saveDraft(d) : saveDraft(d, this.storage);
  }

  private nextFaultId(): string {
    const ids = new Set(this.draft.timeline.map((t) => String(t['id'] ?? '')));
    for (let i = 1; ; i++) if (!ids.has(`fault${i}`)) return `fault${i}`;
  }

  // ------------------------------------------------------------ 範本
  private applyTemplate(): void {
    try {
      if (this.templateChoice === '') {
        this.setDraft(blankDraft());
      } else {
        const s = this.scenarios.find((x) => x.id === this.templateChoice);
        if (!s) throw new Error(`找不到情境 ${this.templateChoice}`);
        const text = (this.sources ?? loadScenarioSources())[s.path];
        if (text === undefined) throw new Error(`找不到 ${s.path} 的 YAML 原文`);
        const d = fromYaml(text, s.id);
        // 新練習預設以範本 id 加後綴,避免紀錄檔名與範本相同
        this.setDraft({ ...d, general: { ...d.general, id: `${d.general.id}_edit` } });
      }
      this.fault = { ...this.fault, id: this.nextFaultId() };
    } catch (err) {
      this.error = err instanceof Error ? err.message : String(err);
    }
  }

  private resetDraft(): void {
    if (this.storage === undefined) clearDraft();
    else clearDraft(this.storage);
    this.templateChoice = '';
    this.setDraft(blankDraft());
    this.fault = { ...this.fault, id: this.nextFaultId() };
  }

  // ------------------------------------------------------------ 欄位
  private patchGeneral(p: Patch<DraftGeneral>): void {
    this.setDraft({ ...this.draft, general: stripUndefined({ ...this.draft.general, ...p }) as DraftGeneral });
  }

  private patchInitial(p: Patch<DraftInitial>): void {
    this.setDraft({ ...this.draft, initial: stripUndefined({ ...this.draft.initial, ...p }) as DraftInitial });
  }

  private patchEnv(p: Patch<DraftEnvironment>): void {
    this.setDraft({ ...this.draft, environment: stripUndefined({ ...this.draft.environment, ...p }) as DraftEnvironment });
  }

  private setPosition(current: () => DraftPosition, key: keyof DraftPosition, ev: Event, apply: (p: DraftPosition) => void): void {
    const p = { ...current() };
    const v = numberOrRefFromText(textOf(ev));
    if (v === undefined) delete p[key];
    else p[key] = v;
    apply(p);
  }

  private switchPositionMode(current: () => DraftPosition, mode: PositionMode, apply: (p: DraftPosition) => void): void {
    if (positionMode(current()) === mode) return;
    apply(mode === 'latlon' ? { lat: 23.9, lon: 119.98 } : { x: 0, y: 0 });
  }

  private num(ev: Event): NumberOrRef | undefined {
    return numberOrRefFromText(textOf(ev));
  }

  // ------------------------------------------------------------ 故障
  private addFault(): void {
    const f = this.fault;
    try {
      const id = f.id.trim();
      if (!id) throw new Error('故障項目需有 id');
      if (this.draft.timeline.some((t) => t['id'] === id)) throw new Error(`時間表 id 重複:${id}`);
      let trigger: FaultItemSpec['trigger'];
      if (f.trigger === 'manual') trigger = { kind: 'manual' };
      else if (f.trigger === 'time') {
        const t = Number(f.atLeast);
        if (!Number.isFinite(t) || t < 0) throw new Error('觸發時間需為 ≥ 0 的秒數');
        trigger = { kind: 'time', atLeast: t };
      } else {
        const when = parseObjectText(f.condition, '觸發條件');
        if (!when) throw new Error('請輸入觸發條件(YAML/JSON 物件,例如 { type: ownSpeed, lessThan: 5 })');
        trigger = { kind: 'condition', when };
      }
      const spec: FaultItemSpec = { id, fault: f.fault, trigger, recordOnly: f.recordOnly };
      if (f.name.trim()) spec.name = f.name;
      if (f.phase) spec.phase = f.phase;
      if (f.message.trim()) spec.message = f.message;
      const args = parseObjectText(f.args, '故障參數');
      if (args) spec.args = args;
      this.setDraft(addFaultItem(this.draft, spec));
      this.fault = { ...f, id: this.nextFaultId(), name: '', condition: '', args: '', message: '' };
    } catch (err) {
      this.error = err instanceof Error ? err.message : String(err);
    }
  }

  // ------------------------------------------------------------ 評估
  private addHardFailRule(): void {
    const f = this.hardFailForm;
    const rule: Record<string, unknown> = { id: '', rule: f.rule };
    const v = f.value.trim();
    try {
      switch (f.rule) {
        case 'cpa':
          if (numberOrRefFromText(v) === undefined) throw new Error('CPA 門檻需為 nm 數值或參數引用');
          rule['id'] = 'cpaHard';
          rule['lessThan'] = numberOrRefFromText(v);
          break;
        case 'ukc':
          if (numberOrRefFromText(v) === undefined) throw new Error('UKC 門檻需為 m 數值或參數引用');
          rule['id'] = 'ukcHard';
          rule['lessThan'] = { metres: numberOrRefFromText(v) };
          break;
        case 'zoneEntered':
          if (!v) throw new Error('請輸入區域 id');
          rule['id'] = `noGo_${v}`;
          rule['zoneId'] = v;
          break;
        case 'event':
          if (!v) throw new Error('請輸入事件 kind(例如 quayContact、mooringLineParted)');
          rule['id'] = v;
          rule['kind'] = v;
          break;
        default:
          rule['id'] = f.rule;
      }
      let id = String(rule['id']);
      for (let i = 2; this.draft.assessment.hardFail.some((h) => h['id'] === id); i++) id = `${rule['id']}${i}`;
      rule['id'] = id;
      this.setDraft(addHardFail(this.draft, rule));
    } catch (err) {
      this.error = err instanceof Error ? err.message : String(err);
    }
  }

  // ------------------------------------------------------------ 輸出
  private download(): void {
    const v = this.validation;
    downloadText(`${this.draft.general.id.trim() || 'scenario'}.yaml`, v.yaml, 'application/yaml');
  }

  private load(): void {
    const v = this.validation;
    if (v.issues.length > 0) {
      this.error = `尚有問題,無法載入:${v.issues.join('; ')}`;
      return;
    }
    this.error = '';
    const detail: ScenarioEditorResult = { id: v.summary.id, scenario: v.scenario, yaml: v.yaml, summary: v.summary };
    this.dispatchEvent(new CustomEvent<ScenarioEditorResult>('scenario-editor-load', { detail, bubbles: true, composed: true }));
  }

  // ------------------------------------------------------------ 畫面
  override render() {
    const d = this.draft;
    const v = this.validation;
    return html`
      <div class="editor">
        <div class="col">
          ${this.renderTemplate()}
          ${this.renderGeneral()}
          ${this.renderShip()}
          ${this.renderEnvironment()}
        </div>
        <div class="col">
          ${this.renderTargets()}
          ${this.renderTimeline()}
        </div>
        <div class="col">
          ${this.renderAssessment()}
          <div class="panel output">
            <div class="panel-title"><span>驗證與輸出 VALIDATE</span><span class="zh">parseScenario + 編輯器檢查</span></div>
            <div class="issues">
              ${v.issues.length === 0
                ? html`<div class="ok">結構檢查通過 · ${d.ship.id} ${loadingLabel(d.ship.loading ?? 'ballast')} · 目標船 ${v.summary.stats.targets} · 時間表 ${v.summary.stats.timelineItems} 項 · 評估規則 ${v.summary.stats.assessmentRules} 條 · 參數引用 ${v.summary.stats.paramRefs} 個</div>`
                : v.issues.map((i) => html`<div class="issue">✖ ${i}</div>`)}
              ${v.warnings.map((w) => html`<div class="warning">⚠ ${w}</div>`)}
            </div>
            ${this.error ? html`<div class="error">${this.error}</div>` : nothing}
            ${this.rejection ? renderRejection(this.rejection) : nothing}
            <div class="btn-row">
              <button class="toggle-yaml" @click=${() => (this.showYaml = !this.showYaml)}>${this.showYaml ? '隱藏 YAML' : '預覽 YAML'}</button>
              <button class="download" @click=${this.download}>下載 YAML</button>
              <button class="primary load" ?disabled=${this.busy || v.issues.length > 0} @click=${this.load}>${this.busy ? '載入中…' : '載入核心'}</button>
            </div>
            ${this.showYaml ? html`<pre class="yaml">${v.yaml}</pre>` : nothing}
            <div class="hint">載入核心以 loadScenario args.scenario 內嵌送出(不寫檔);要加入情境清單請下載 YAML 放到 data/scenarios/ 並重新建置。</div>
          </div>
        </div>
      </div>
    `;
  }

  private renderTemplate() {
    const d = this.draft;
    return html`<div class="panel template">
      <div class="panel-title"><span>情境編輯 SCENARIO EDITOR</span><span class="zh">依範本組出新練習(規劃書第 9.1 節)</span></div>
      <div class="form">
        <label>範本</label>
        <select class="template full-span" data-value=${this.templateChoice} @change=${(e: Event) => (this.templateChoice = textOf(e))}>
          <option value="">空白範本</option>
          ${this.scenarios.map((s) => html`<option value=${s.id}>${s.id} ${s.name}</option>`)}
        </select>
        <button class="apply-template" @click=${this.applyTemplate}>套用範本</button>
        <button class="reset-draft" @click=${this.resetDraft}>清除草稿</button>
      </div>
      <div class="hint">
        目前草稿:${d.template ? `範本 ${d.template}` : '空白範本'}${this.restored ? ' · 已自本機草稿還原' : this.saved ? ' · 已自動儲存於本機' : ' · 本機儲存不可用(草稿不會保留)'}
      </div>
    </div>`;
  }

  private renderGeneral() {
    const g = this.draft.general;
    return html`<div class="panel general">
      <div class="panel-title"><span>基本資料</span><span class="zh">id 作紀錄檔名</span></div>
      <div class="form">
        <label>id</label>
        <input class="id" type="text" .value=${g.id} @change=${(e: Event) => this.patchGeneral({ id: textOf(e) })} />
        <label>名稱</label>
        <input class="name" type="text" .value=${g.name} @change=${(e: Event) => this.patchGeneral({ name: textOf(e) })} />
        <label class="full">說明</label>
        <textarea class="description full" .value=${g.description} @change=${(e: Event) => this.patchGeneral({ description: textOf(e) })}></textarea>
        <label class="full">簡報文字(附件與角色保留範本設定)</label>
        <textarea class="briefing full" .value=${g.briefing} @change=${(e: Event) => this.patchGeneral({ briefing: textOf(e) })}></textarea>
        <label>種子</label>
        <input class="seed" type="number" step="1" .value=${g.seed === undefined ? '' : String(g.seed)} @change=${(e: Event) => { const n = Number(textOf(e)); this.patchGeneral({ seed: textOf(e).trim() === '' || !Number.isFinite(n) ? undefined : Math.trunc(n) }); }} />
        <label>時間倍率</label>
        <input class="timescale" type="number" step="0.5" min="0.1" .value=${g.timeScale === undefined ? '' : String(g.timeScale)} @change=${(e: Event) => { const n = Number(textOf(e)); this.patchGeneral({ timeScale: textOf(e).trim() === '' || !Number.isFinite(n) ? undefined : n }); }} />
        <label>開始時刻 UTC(日/夜)</label>
        <input class="start-utc" type="text" placeholder="2026-11-13T01:00:00Z" .value=${g.startTimeUtc ?? ''} @change=${(e: Event) => this.patchGeneral({ startTimeUtc: textOf(e).trim() || undefined })} />
        <div class="full hint">${dayNightLabel(g.startTimeUtc, this.draft.initial.position.lon)}</div>
      </div>
    </div>`;
  }

  /** 位置欄位(自船與目標船共用);current 以函式取得,讓連續變更都讀到最新草稿而非渲染時的複本。 */
  private renderPosition(current: () => DraftPosition, cls: string, apply: (p: DraftPosition) => void) {
    const p = current();
    const mode = positionMode(p);
    return html`
      <select class="${cls}-mode" data-value=${mode} @change=${(e: Event) => this.switchPositionMode(current, textOf(e) as PositionMode, apply)}>
        <option value="latlon">經緯度</option>
        <option value="enu">ENU x/y (m)</option>
      </select>
      ${mode === 'latlon'
        ? html`<div class="pair">
            <input class="${cls}-lat" type="number" step="0.0001" .value=${numberOrRefText(p.lat)} @change=${(e: Event) => this.setPosition(current, 'lat', e, apply)} /><span>°N</span>
            <input class="${cls}-lon" type="number" step="0.0001" .value=${numberOrRefText(p.lon)} @change=${(e: Event) => this.setPosition(current, 'lon', e, apply)} /><span>°E</span>
          </div>`
        : html`<div class="pair">
            <input class="${cls}-x" type="number" step="1" .value=${numberOrRefText(p.x)} @change=${(e: Event) => this.setPosition(current, 'x', e, apply)} /><span>x</span>
            <input class="${cls}-y" type="number" step="1" .value=${numberOrRefText(p.y)} @change=${(e: Event) => this.setPosition(current, 'y', e, apply)} /><span>y</span>
          </div>`}
    `;
  }

  private renderShip() {
    const d = this.draft;
    const i = d.initial;
    return html`<div class="panel ship">
      <div class="panel-title"><span>船與初始狀態</span><span class="zh">航向 0–360、速度 kn、舵角右正</span></div>
      <div class="form">
        <label>船</label>
        <select class="ship" data-value=${d.ship.id} @change=${(e: Event) => this.setDraft({ ...this.draft, ship: { ...this.draft.ship, id: textOf(e) as ShipId } })}>
          ${SHIP_IDS.map((s) => html`<option value=${s}>${s}</option>`)}
        </select>
        <label>裝載</label>
        <select class="loading" data-value=${d.ship.loading ?? 'ballast'} @change=${(e: Event) => this.setDraft({ ...this.draft, ship: { ...this.draft.ship, loading: textOf(e) as LoadingCondition } })}>
          ${LOADINGS.map((l) => html`<option value=${l}>${loadingLabel(l)}</option>`)}
        </select>
        <label>初始位置</label>
        <div class="full-span pair">${this.renderPosition(() => this.draft.initial.position, 'own', (p) => this.patchInitial({ position: p }))}</div>
        <label>航向 (°)</label>
        <input class="heading" type="number" step="1" min="0" max="360" .value=${numberOrRefText(i.heading)} @change=${(e: Event) => this.patchInitial({ heading: this.num(e) })} />
        <label>速度 (kn)</label>
        <input class="speed" type="number" step="0.1" min="0" .value=${numberOrRefText(i.speed)} @change=${(e: Event) => this.patchInitial({ speed: this.num(e) })} />
        <label>車鐘</label>
        <select class="telegraph" data-value=${i.telegraph ?? ''} @change=${(e: Event) => this.patchInitial({ telegraph: (textOf(e) || undefined) as TelegraphPosition | undefined })}>
          <option value="">(依航速)</option>
          ${TELEGRAPH_POSITIONS.map((t) => html`<option value=${t}>${t}</option>`)}
        </select>
        <label>舵角 (°)</label>
        <input class="rudder" type="number" step="1" .value=${numberOrRefText(i.rudder)} @change=${(e: Event) => this.patchInitial({ rudder: this.num(e) })} />
      </div>
    </div>`;
  }

  private renderEnvironment() {
    const e = this.draft.environment;
    const raw = (this.draft.raw['environment'] ?? {}) as Record<string, unknown>;
    const kept = Object.keys(raw).filter((k) => !['wind', 'current', 'waterDepth', 'visibility_nm', 'visibility'].includes(k));
    return html`<div class="panel environment">
      <div class="panel-title"><span>環境</span><span class="zh">風向為來向、流向為去向;數值可用參數引用</span></div>
      <div class="form">
        <label>風 速/向/陣風</label>
        <div class="full-span pair">
          <input class="wind-speed" type="text" inputmode="decimal" .value=${numberOrRefText(e.windSpeed)} @change=${(ev: Event) => this.patchEnv({ windSpeed: this.num(ev) })} /><span>kn</span>
          <input class="wind-dir" type="text" inputmode="decimal" .value=${numberOrRefText(e.windDir)} @change=${(ev: Event) => this.patchEnv({ windDir: this.num(ev) })} /><span>°</span>
          <input class="wind-gust" type="text" inputmode="decimal" .value=${numberOrRefText(e.gustiness)} @change=${(ev: Event) => this.patchEnv({ gustiness: this.num(ev) })} /><span>陣風 0–1</span>
        </div>
        <label>流 向/速</label>
        <div class="full-span pair">
          <input class="cur-set" type="text" inputmode="decimal" .value=${numberOrRefText(e.currentSet)} @change=${(ev: Event) => this.patchEnv({ currentSet: this.num(ev) })} /><span>°</span>
          <input class="cur-drift" type="text" inputmode="decimal" .value=${numberOrRefText(e.currentDrift)} @change=${(ev: Event) => this.patchEnv({ currentDrift: this.num(ev) })} /><span>kn</span>
        </div>
        <label>水深 (m)</label>
        <input class="depth" type="text" inputmode="decimal" .value=${numberOrRefText(e.waterDepth)} @change=${(ev: Event) => this.patchEnv({ waterDepth: this.num(ev) })} />
        <label>能見度 (nm)</label>
        <input class="vis" type="text" inputmode="decimal" .value=${numberOrRefText(e.visibility_nm)} @change=${(ev: Event) => this.patchEnv({ visibility_nm: this.num(ev) })} />
        ${kept.length > 0 ? html`<div class="full hint">範本的 ${kept.join('、')} 原樣保留(不在此編輯)</div>` : nothing}
      </div>
    </div>`;
  }

  private renderTargets() {
    const d = this.draft;
    return html`<div class="panel targets">
      <div class="panel-title"><span>目標船 TARGETS</span><span class="zh">${d.targets.length} 艘;AIS/航點/腳本等細節保留範本設定</span></div>
      ${d.targets.map((t, i) => this.renderTarget(t, i))}
      <div class="btn-row"><button class="add-target" @click=${() => this.setDraft(addTarget(this.draft))}>新增目標船</button></div>
    </div>`;
  }

  private renderTarget(t: DraftTarget, i: number) {
    const patch = (p: Patch<Omit<DraftTarget, 'raw'>>) => this.setDraft(patchTarget(this.draft, i, p));
    const beh = t.raw['behaviour'];
    const extra = [
      ...Object.keys(t.raw).filter((k) => !['id', 'name', 'shipType', 'loa', 'beam', 'initial', 'behaviour'].includes(k)),
      ...(typeof beh === 'object' && beh !== null ? Object.keys(beh).filter((k) => k !== 'mode').map((k) => `behaviour.${k}`) : []),
    ];
    return html`<div class="row target" data-index=${i}>
      <label>id</label>
      <input class="t-id" type="text" .value=${t.id} @change=${(e: Event) => patch({ id: textOf(e) })} />
      <label>船名</label>
      <input class="t-name" type="text" .value=${t.name} @change=${(e: Event) => patch({ name: textOf(e) })} />
      <label>船型</label>
      <select class="t-type" data-value=${t.shipType || 'cargo'} @change=${(e: Event) => patch({ shipType: textOf(e) })}>
        ${TARGET_SHIP_TYPES.map((s) => html`<option value=${s}>${s}</option>`)}
      </select>
      <label>位置</label>
      <div class="pair" style="grid-column: 2 / 5">${this.renderPosition(() => this.draft.targets[i]?.position ?? {}, 't-pos', (p) => patch({ position: p }))}</div>
      <label>長/寬 (m)</label>
      <div class="pair">
        <input class="t-loa" type="number" step="1" .value=${numberOrRefText(t.loa)} @change=${(e: Event) => patch({ loa: this.num(e) })} />
        <input class="t-beam" type="number" step="1" .value=${numberOrRefText(t.beam)} @change=${(e: Event) => patch({ beam: this.num(e) })} />
      </div>
      <label>航向 (°)</label>
      <input class="t-heading" type="number" step="1" min="0" max="360" .value=${numberOrRefText(t.heading)} @change=${(e: Event) => patch({ heading: this.num(e) })} />
      <label>速度 (kn)</label>
      <input class="t-speed" type="number" step="0.1" min="0" .value=${numberOrRefText(t.speed)} @change=${(e: Event) => patch({ speed: this.num(e) })} />
      <label>行為</label>
      <select class="t-mode" data-value=${t.mode ?? 'hold'} @change=${(e: Event) => patch({ mode: textOf(e) })}>
        ${TARGET_MODES.map((m) => html`<option value=${m}>${m} ${TARGET_MODE_LABEL[m]}</option>`)}
        ${t.mode && !(TARGET_MODES as readonly string[]).includes(t.mode) ? html`<option value=${t.mode}>${t.mode}(未知)</option>` : nothing}
      </select>
      <div class="tail">
        <span class="hint">${extra.length > 0 ? `保留:${extra.join('、')}` : Object.keys(t.raw).length === 0 ? '新增的目標船' : '範本目標船'}</span>
        <button class="small danger t-remove" @click=${() => this.setDraft(removeTarget(this.draft, i))}>刪除</button>
      </div>
    </div>`;
  }

  private renderTimeline() {
    const rows = describeTimeline(this.draft.timeline);
    const f = this.fault;
    const def = this.faults.find((x) => x.name === f.fault);
    return html`<div class="panel timeline">
      <div class="panel-title"><span>時間表與故障 TIMELINE</span><span class="zh">${rows.length} 項(故障 ${rows.filter((r) => r.fault).length});語音/訊息等項目保留</span></div>
      <div class="list" role="list">
        ${rows.length === 0 ? html`<div class="hint">尚無時間表項目</div>` : nothing}
        ${rows.map(
          (r) => html`<div class="item" role="listitem" data-index=${r.index}>
            <div class="text">
              <div><b>${r.id}</b>${r.name && r.name !== r.id ? ` ${r.name}` : ''}${r.fault ? html` <span class="tag unsupported">故障 ${r.faultNames.join('、')}</span>` : nothing}</div>
              <div class="sub">${r.condition} → ${r.actions}</div>
              ${r.issues.length > 0 ? html`<div class="bad">${r.issues.join('; ')}</div>` : nothing}
            </div>
            <button class="small danger tl-remove" @click=${() => this.setDraft(removeTimelineItem(this.draft, r.index))}>刪除</button>
          </div>`,
        )}
      </div>
      <fieldset class="fault-form">
        <legend>新增故障(src/Contracts/fault-catalogue.json)</legend>
        <label>項目 id</label>
        <input class="f-id" type="text" .value=${f.id} @change=${(e: Event) => this.patchFault({ id: textOf(e) })} />
        <label>名稱</label>
        <input class="f-name" type="text" .value=${f.name} @change=${(e: Event) => this.patchFault({ name: textOf(e) })} />
        <label>故障</label>
        <select class="f-fault full-span" style="grid-column: 2 / 5" data-value=${f.fault} @change=${(e: Event) => this.patchFault({ fault: textOf(e) })}>
          ${this.faults.map((x) => html`<option value=${x.name}>${x.name} ${x.label}${x.modelled ? '' : '(只記錄)'}</option>`)}
        </select>
        ${def?.effect ? html`<div class="full hint">${def.effect}</div>` : nothing}
        <label>觸發</label>
        <select class="f-trigger" data-value=${f.trigger} @change=${(e: Event) => this.patchFault({ trigger: textOf(e) as FaultForm['trigger'] })}>
          <option value="time">模擬時間 t ≥</option>
          <option value="manual">教官手動觸發</option>
          <option value="condition">自訂條件</option>
        </select>
        ${f.trigger === 'time'
          ? html`<div class="pair"><input class="f-time" type="number" step="30" min="0" .value=${f.atLeast} @change=${(e: Event) => this.patchFault({ atLeast: textOf(e) })} /><span>s</span></div>`
          : f.trigger === 'condition'
            ? html`<input class="f-condition" type="text" placeholder="{ type: ownSpeed, lessThan: 5 }" .value=${f.condition} @change=${(e: Event) => this.patchFault({ condition: textOf(e) })} />`
            : html`<span class="hint">時間表面板「觸發」鈕</span>`}
        <label>階段</label>
        <select class="f-phase" data-value=${f.phase} @change=${(e: Event) => this.patchFault({ phase: textOf(e) })}>
          <option value="">(不變)</option>
          ${PHASES.map((p) => html`<option value=${p}>${p}</option>`)}
        </select>
        <label>參數</label>
        <input class="f-args" type="text" placeholder=${def?.args ? JSON.stringify(def.args) : '(無)'} .value=${f.args} @change=${(e: Event) => this.patchFault({ args: textOf(e) })} />
        <label>教官訊息</label>
        <input class="f-message" type="text" .value=${f.message} @change=${(e: Event) => this.patchFault({ message: textOf(e) })} />
        <label class="pair"><input class="f-record-only" type="checkbox" .checked=${f.recordOnly} @change=${(e: Event) => this.patchFault({ recordOnly: checked(e) })} /><span>只記錄(recordOnly)</span></label>
        <div class="full btn-row"><button class="add-fault" @click=${this.addFault}>加入故障項目</button></div>
      </fieldset>
    </div>`;
  }

  private renderAssessment() {
    const d = this.draft;
    const a = d.assessment;
    const hf = this.hardFailForm;
    const raw = (d.raw['assessment'] ?? {}) as Record<string, unknown>;
    const kept = Object.keys(raw).filter((k) => !['hardFail', 'bands', 'scoring'].includes(k));
    const needsValue = hf.rule === 'cpa' || hf.rule === 'ukc' || hf.rule === 'zoneEntered' || hf.rule === 'event';
    return html`<div class="panel assessment">
      <div class="panel-title"><span>評估規則 ASSESSMENT</span><span class="zh">硬性失敗、容許帶、通過分數</span></div>
      <div class="checks">
        ${SIMPLE_HARD_FAILS.map(
          (rule) => html`<label><input class="hf-${rule}" type="checkbox" .checked=${hasHardFail(d, rule)} @change=${(e: Event) => this.setDraft(setHardFail(this.draft, rule, checked(e)))} />${HARD_FAIL_RULE_LABEL[rule] ?? rule} 硬性失敗</label>`,
        )}
      </div>
      <div class="list" role="list">
        ${a.hardFail.map((h, i) =>
          SIMPLE_HARD_FAILS.includes(String(h['rule']))
            ? nothing
            : html`<div class="item" role="listitem"><span class="text">${String(h['id'])} · ${hardFailText(h)}</span><button class="small danger hf-remove" @click=${() => this.setDraft(removeHardFail(this.draft, i))}>刪除</button></div>`,
        )}
      </div>
      <div class="form">
        <label>新增硬性失敗</label>
        <select class="hf-rule" data-value=${hf.rule} @change=${(e: Event) => (this.hardFailForm = { ...this.hardFailForm, rule: textOf(e) })}>
          ${HARD_FAIL_RULES.filter((r) => !SIMPLE_HARD_FAILS.includes(r)).map((r) => html`<option value=${r}>${HARD_FAIL_RULE_LABEL[r] ?? r}</option>`)}
        </select>
        <input class="hf-value" type="text" ?disabled=${!needsValue} placeholder=${hf.rule === 'cpa' ? 'nm' : hf.rule === 'ukc' ? 'm' : hf.rule === 'zoneEntered' ? '區域 id' : '事件 kind'} .value=${hf.value} @change=${(e: Event) => (this.hardFailForm = { ...this.hardFailForm, value: textOf(e) })} />
        <button class="hf-add" @click=${this.addHardFailRule}>加入</button>
      </div>
      <div class="panel-title"><span>容許帶</span><span class="zh">${a.bands.length} 條;fail 可寫 2x</span></div>
      ${a.bands.map((b, i) => this.renderBand(b, i))}
      <div class="btn-row"><button class="add-band" @click=${() => this.setDraft(addBand(this.draft))}>新增容許帶</button></div>
      <datalist id="band-metrics">${BAND_METRICS.map((m) => html`<option value=${m}></option>`)}</datalist>
      <div class="form">
        <label>起始分</label>
        <input class="score-start" type="text" inputmode="decimal" .value=${numberOrRefText(a.start)} @change=${(e: Event) => this.setDraft({ ...this.draft, assessment: this.withScore(this.draft.assessment, 'start', this.num(e)) })} />
        <label>通過分數</label>
        <input class="pass-mark" type="text" inputmode="decimal" .value=${numberOrRefText(a.passMark)} @change=${(e: Event) => this.setDraft({ ...this.draft, assessment: this.withScore(this.draft.assessment, 'passMark', this.num(e)) })} />
        ${kept.length > 0 ? html`<div class="full hint">範本的 ${kept.join('、')} 原樣保留(必要事件等不在此編輯)</div>` : nothing}
      </div>
    </div>`;
  }

  private withScore(a: ScenarioDraft['assessment'], key: 'start' | 'passMark', v: NumberOrRef | undefined): ScenarioDraft['assessment'] {
    const out = { ...a };
    if (v === undefined) delete out[key];
    else out[key] = v;
    return out;
  }

  private renderBand(b: Record<string, unknown>, i: number) {
    const set = (key: string, ev: Event) => this.setDraft(patchBand(this.draft, i, key, key === 'id' || key === 'metric' || key === 'direction' ? textOf(ev) : numberOrRefFromText(textOf(ev))));
    const txt = (v: unknown) => (v === undefined || v === null ? '' : String(v));
    return html`<div class="row band" data-index=${i}>
      <label>id</label>
      <input class="b-id" type="text" .value=${txt(b['id'])} @change=${(e: Event) => set('id', e)} />
      <label>指標</label>
      <input class="b-metric" type="text" list="band-metrics" .value=${txt(b['metric'])} @change=${(e: Event) => set('metric', e)} />
      <label>方向</label>
      <select class="b-direction" data-value=${b['direction'] === 'below' ? 'below' : 'above'} @change=${(e: Event) => set('direction', e)}>
        <option value="above">above(越大越差)</option>
        <option value="below">below(越小越差)</option>
      </select>
      <label>警告</label>
      <input class="b-warn" type="text" inputmode="decimal" .value=${txt(b['warn'])} @change=${(e: Event) => set('warn', e)} />
      <label>失敗</label>
      <input class="b-fail" type="text" .value=${txt(b['fail'])} @change=${(e: Event) => set('fail', e)} />
      <label>扣分/失敗扣分</label>
      <div class="pair">
        <input class="b-penalty" type="text" inputmode="decimal" .value=${txt(b['penalty'])} @change=${(e: Event) => set('penalty', e)} />
        <input class="b-fail-penalty" type="text" inputmode="decimal" .value=${txt(b['failPenalty'])} @change=${(e: Event) => set('failPenalty', e)} />
      </div>
      <div class="tail"><span></span><button class="small danger b-remove" @click=${() => this.setDraft(removeBand(this.draft, i))}>刪除</button></div>
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-scenario-editor': ScenarioEditor;
  }
  interface HTMLElementEventMap {
    'scenario-editor-load': ScenarioEditorLoadEvent;
  }
}
