// 簡化教官模式「靠離泊預演」表單(規劃書第 8.0 節、第 9.1 節 R13):輸入船與裝載、泊位(data/areas 清單,顯示碼頭線走向與水側)、
// 靠泊/離泊、靠泊舷、預定時刻 UTC、潮高與基底水深、潮流、預報風、能見度、拖船數、引水人在船、風變化(選填);
// 按「組題並載入」以 lib/berthing-rehearsal.ts 組出情境物件(parseScenario 自我驗證),發 rehearsal-compose 事件(教官站以 loadScenario
// args.scenario 送核心),可下載 YAML。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import {
  berthChoiceLabel,
  composeBerthingRehearsal,
  defaultRehearsalInput,
  listBerthChoices,
  normalizePlannedUtc,
  type AlongsideSide,
  type BerthChoice,
  type BerthingRehearsalInput,
  type BerthingRehearsalResult,
  type RehearsalOperation,
} from '../../lib/berthing-rehearsal.js';
import { downloadText } from '../../lib/download.js';
import { SHIP_IDS, type LoadingCondition, type ShipId } from '../../types/state.js';
import { loadingLabel } from '../../lib/scenario.js';
import type { ScenarioLoadRejection } from '../../lib/sim-event.js';
import { renderRejection } from './scenario-panel.js';

export type RehearsalComposeEvent = CustomEvent<BerthingRehearsalResult>;

const LOADINGS: readonly LoadingCondition[] = ['full', 'ballast', 'intermediate'];

@customElement('brm-berthing-rehearsal-panel')
export class BerthingRehearsalPanel extends LitElement {
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
        overflow-y: auto;
      }
      .form {
        grid-template-columns: auto 1fr auto 1fr;
      }
      .form .full {
        grid-column: 1 / -1;
      }
      .form .pair {
        display: flex;
        gap: 4px;
        align-items: center;
      }
      .form .pair input {
        width: 100%;
      }
      .form .pair span {
        color: var(--ob-text-muted);
        white-space: nowrap;
        font-size: 11px;
      }
      .berth-meta {
        font-size: 11px;
        color: var(--ob-text-secondary);
        white-space: pre-wrap;
      }
      .berth-meta .src {
        color: var(--ob-text-muted);
      }
      .preview {
        font-size: 11px;
        border-top: 1px solid var(--ob-border);
        padding-top: 4px;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .preview .brief {
        white-space: pre-wrap;
        color: var(--ob-text);
      }
      .preview .note {
        color: var(--ob-text-secondary);
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
      .preview .warning {
        color: var(--ob-warning);
      }
      .ok {
        color: var(--ob-ok);
        font-size: 11px;
      }
      fieldset {
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 4px 8px;
        margin: 0;
        grid-column: 1 / -1;
        display: grid;
        grid-template-columns: auto 1fr auto 1fr;
        gap: 4px 8px;
        font-size: 12px;
      }
      legend {
        color: var(--ob-text-muted);
        font-size: 11px;
        padding: 0 4px;
      }
    `,
  ];

  @property({ type: Boolean }) compact = false;
  /** 核心拒絕上一次「組題並載入」的原因(網頁自我驗證通過但核心的結構檢查/參數/泊位參照失敗時);null = 無 */
  @property({ attribute: false }) rejection: ScenarioLoadRejection | null = null;
  /** 已送 loadScenario、等待核心結果:載入鈕停用並顯示載入中 */
  @property({ type: Boolean }) busy = false;

  @state() private input: BerthingRehearsalInput = defaultRehearsalInput();
  @state() private windShiftOn = false;
  @state() private result: BerthingRehearsalResult | null = null;
  @state() private error = '';

  private readonly berths: BerthChoice[] = listBerthChoices();

  /** 目前輸入(測試用) */
  get value(): BerthingRehearsalInput {
    return this.input;
  }

  /** 最近一次組題結果(測試用;null = 尚未組題或失敗) */
  get lastResult(): BerthingRehearsalResult | null {
    return this.result;
  }

  private patch(p: Partial<BerthingRehearsalInput>): void {
    this.input = { ...this.input, ...p };
    this.result = null;
    this.error = '';
  }

  private selectBerth(id: string): void {
    const b = this.berths.find((x) => x.id === id);
    this.patch({ berthId: id, ...(b ? { baseDepth_m: b.defaultDepth_m } : {}) });
  }

  private num(ev: Event, apply: (n: number) => Partial<BerthingRehearsalInput>): void {
    const n = numberFromInput(ev.target);
    if (n !== undefined) this.patch(apply(n));
  }

  /** 組題(不載入):回傳結果或 null(錯誤顯示於面板)。 */
  compose(): BerthingRehearsalResult | null {
    try {
      const planned = normalizePlannedUtc(this.input.plannedUtc);
      if (!planned) throw new Error(`預定時刻無法解析:${this.input.plannedUtc}(例如 2026-11-13T01:00)`);
      const r = composeBerthingRehearsal({ ...this.input, plannedUtc: planned, windShift: this.windShiftOn ? this.input.windShift ?? null : null });
      if (r.summary.issues.length > 0) throw new Error(`自我驗證未通過:${r.summary.issues.join('; ')}`);
      this.result = r;
      this.error = '';
      return r;
    } catch (err) {
      this.result = null;
      this.error = err instanceof Error ? err.message : String(err);
      return null;
    }
  }

  private composeAndLoad(): void {
    const r = this.compose();
    if (!r) return;
    this.dispatchEvent(new CustomEvent<BerthingRehearsalResult>('rehearsal-compose', { detail: r, bubbles: true, composed: true }));
  }

  private download(): void {
    const r = this.result ?? this.compose();
    if (!r) return;
    downloadText(`${r.id}.yaml`, r.yaml, 'application/yaml');
  }

  override render() {
    const i = this.input;
    const berth = this.berths.find((b) => b.id === i.berthId);
    const ws = i.windShift ?? { after_s: 600, speed_kn: 25, dir_deg: 30, ramp_s: 120 };
    return html`
      <div class="panel">
        <div class="panel-title"><span>靠離泊預演 BERTHING REHEARSAL</span><span class="zh">輸入當次航次條件自動組題(規劃書第 9.1 節)</span></div>
        <div class="form">
          <label>作業</label>
          <select .value=${i.operation} @change=${(e: Event) => this.patch({ operation: (e.target as HTMLSelectElement).value as RehearsalOperation })}>
            <option value="berthing">靠泊(泊位外約 0.8 nm 接近)</option>
            <option value="unberthing">離泊(繫妥於泊位、4 纜 fast)</option>
          </select>
          <label>靠泊舷</label>
          <select .value=${i.side} @change=${(e: Event) => this.patch({ side: (e.target as HTMLSelectElement).value as AlongsideSide })}>
            <option value="port">左舷靠</option>
            <option value="starboard">右舷靠</option>
          </select>
          <label>船</label>
          <select .value=${i.shipId} @change=${(e: Event) => this.patch({ shipId: (e.target as HTMLSelectElement).value as ShipId })}>
            ${SHIP_IDS.map((s) => html`<option value=${s}>${s}</option>`)}
          </select>
          <label>裝載</label>
          <select .value=${i.loading} @change=${(e: Event) => this.patch({ loading: (e.target as HTMLSelectElement).value as LoadingCondition })}>
            ${LOADINGS.map((l) => html`<option value=${l}>${loadingLabel(l)}</option>`)}
          </select>
          <label>泊位</label>
          <select class="berth full-span" .value=${i.berthId} @change=${(e: Event) => this.selectBerth((e.target as HTMLSelectElement).value)}>
            ${this.berths.map((b) => html`<option value=${b.id}>${b.id} ${b.name}</option>`)}
          </select>
          <label>預定時刻 UTC</label>
          <input class="planned" type="text" placeholder="2026-11-13T01:00" .value=${i.plannedUtc} @change=${(e: Event) => this.patch({ plannedUtc: (e.target as HTMLInputElement).value })} />
          ${berth ? html`<div class="full berth-meta">${berthChoiceLabel(berth)}<br /><span class="src">${berth.file}${berth.source ? ` · ${berth.source}` : ''}</span></div>` : nothing}
          <label>潮高 (m)</label>
          <input class="tide" type="number" step="0.1" .value=${String(i.tideHeight_m)} @change=${(e: Event) => this.num(e, (n) => ({ tideHeight_m: n }))} />
          <label>基底水深 (m)</label>
          <input class="depth" type="number" step="0.1" min="0.1" .value=${String(i.baseDepth_m)} @change=${(e: Event) => this.num(e, (n) => ({ baseDepth_m: n }))} />
          <label>潮流 向/速</label>
          <div class="pair">
            <input class="cur-set" type="number" step="1" min="0" max="360" .value=${String(i.current.set_deg)} @change=${(e: Event) => this.num(e, (n) => ({ current: { ...i.current, set_deg: n } }))} /><span>°</span>
            <input class="cur-drift" type="number" step="0.1" min="0" .value=${String(i.current.drift_kn)} @change=${(e: Event) => this.num(e, (n) => ({ current: { ...i.current, drift_kn: n } }))} /><span>kn</span>
          </div>
          <label>預報風 速/向/陣風</label>
          <div class="pair">
            <input class="wind-speed" type="number" step="1" min="0" .value=${String(i.wind.speed_kn)} @change=${(e: Event) => this.num(e, (n) => ({ wind: { ...i.wind, speed_kn: n } }))} /><span>kn</span>
            <input class="wind-dir" type="number" step="1" min="0" max="360" .value=${String(i.wind.dir_deg)} @change=${(e: Event) => this.num(e, (n) => ({ wind: { ...i.wind, dir_deg: n } }))} /><span>°</span>
            <input class="wind-gust" type="number" step="0.1" min="0" max="1" .value=${String(i.wind.gustiness)} @change=${(e: Event) => this.num(e, (n) => ({ wind: { ...i.wind, gustiness: n } }))} />
          </div>
          <label>能見度 (nm)</label>
          <input class="vis" type="number" step="0.5" min="0.1" .value=${String(i.visibility_nm)} @change=${(e: Event) => this.num(e, (n) => ({ visibility_nm: n }))} />
          <label>拖船數</label>
          <select class="tugs" .value=${String(i.tugCount)} @change=${(e: Event) => this.patch({ tugCount: Number((e.target as HTMLSelectElement).value) as 0 | 1 | 2 })}>
            <option value="0">0(無拖船)</option>
            <option value="1">1(艉,40 t)</option>
            <option value="2">2(艏/艉,各 40 t)</option>
          </select>
          <label>拖船拖力 (t)</label>
          <input class="tug-pull" type="number" step="5" min="10" .value=${String(i.tugBollardPull_t ?? 40)} @change=${(e: Event) => this.num(e, (n) => ({ tugBollardPull_t: n }))} />
          <label>引水人在船</label>
          <label class="pair"><input class="pilot" type="checkbox" .checked=${i.pilotOnBoard} @change=${(e: Event) => this.patch({ pilotOnBoard: (e.target as HTMLInputElement).checked })} /><span>角色提示:MPX 必要事件、引水人語音由帶領者扮演</span></label>
          <fieldset>
            <legend><label class="pair"><input class="wind-shift" type="checkbox" .checked=${this.windShiftOn} @change=${(e: Event) => { this.windShiftOn = (e.target as HTMLInputElement).checked; this.result = null; }} /><span>風變化(選填)</span></label></legend>
            ${this.windShiftOn
              ? html`
                  <label>幾秒後</label>
                  <input class="ws-after" type="number" step="30" min="30" .value=${String(ws.after_s)} @change=${(e: Event) => this.num(e, (n) => ({ windShift: { ...ws, after_s: n } }))} />
                  <label>漸變 (s)</label>
                  <input class="ws-ramp" type="number" step="30" min="0" .value=${String(ws.ramp_s)} @change=${(e: Event) => this.num(e, (n) => ({ windShift: { ...ws, ramp_s: n } }))} />
                  <label>風速 (kn)</label>
                  <input class="ws-speed" type="number" step="1" min="0" .value=${String(ws.speed_kn)} @change=${(e: Event) => this.num(e, (n) => ({ windShift: { ...ws, speed_kn: n } }))} />
                  <label>風向 (°)</label>
                  <input class="ws-dir" type="number" step="1" min="0" max="360" .value=${String(ws.dir_deg)} @change=${(e: Event) => this.num(e, (n) => ({ windShift: { ...ws, dir_deg: n } }))} />
                `
              : nothing}
          </fieldset>
        </div>
        ${this.error ? html`<div class="error">${this.error}</div>` : nothing}
        ${this.rejection ? renderRejection(this.rejection) : nothing}
        ${this.result
          ? html`<div class="preview">
              <div class="ok">已組題 ${this.result.id} · 自我驗證通過 · 時間表 ${this.result.summary.stats.timelineItems} 項 · 評估規則 ${this.result.summary.stats.assessmentRules} 條 · 參數引用 ${this.result.summary.stats.paramRefs} 個</div>
              ${this.result.warnings.map((w) => html`<div class="warning">${w}</div>`)}
              ${this.result.notes.filter((n) => !this.result!.warnings.includes(n)).map((n) => html`<div class="note">${n}</div>`)}
              ${this.compact ? nothing : html`<div class="brief">${this.result.summary.briefing}</div>`}
            </div>`
          : nothing}
        <div class="btn-row">
          <button class="preview-btn" @click=${() => this.compose()}>預覽</button>
          <button class="download" @click=${this.download}>下載 YAML</button>
          <button class="primary load" ?disabled=${this.busy} @click=${this.composeAndLoad}>${this.busy ? '載入中…' : '組題並載入'}</button>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-berthing-rehearsal-panel': BerthingRehearsalPanel;
  }
  interface HTMLElementEventMap {
    'rehearsal-compose': RehearsalComposeEvent;
  }
}
