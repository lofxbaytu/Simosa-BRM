// 繫纜與泊位面板(規劃書第 8.1 節 E13/E14、第 8.3 節第 4 類靠泊指標、第 9.1 節泊位/碰墊/繫船柱;
// docs/design/physics-tugs-anchors-mooring.md 第 4 節):
//  - 泊位:名稱、碼頭硬接觸警示、受壓碰墊清單(壓縮比例條依額定壓縮著色、反力 kN、過載);
//  - 各纜:狀態、張力條(相對 MBL:< 50 % 綠、50–80 % 黃、≥ 80 % 或斷裂紅)、張力/MBL、繫船柱、未受力纜長;
//    帶纜 makeFast(選繫船柱)、絞纜 heave(目標張力)、鬆纜 slack(放出長度,預設 2 m)、解纜 letGo,送 mooring 指令。
// 繫船柱清單取自泊位幾何(情境 berth.definition);沒有幾何時只能用纜的預設繫船柱。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import { MOORING_ACTION_LABEL, MOORING_STATUS_LABEL, fenderLoadLevel, lineLoadLevel, lineLoadRatio, mooringCommand, type BerthGeometry, type ScenarioMooringLineDef } from '../../lib/external-forces.js';
import type { BerthState, MooringAction, MooringLineState } from '../../types/external.js';

interface LineDraft {
  bollard?: string | undefined;
  tension?: number | undefined;
  slack?: number | undefined;
}

/** 纜繩可用的動作(依核心 BerthModel.Command 規則;回傳提示文字,可用時 null)。 */
export function mooringActionBlocked(l: MooringLineState, action: MooringAction): string | null {
  const loaded = l.status === 'fast' || l.status === 'heaving' || l.status === 'slacking';
  if (l.status === 'parted') return '纜繩已斷';
  if ((action === 'heave' || action === 'slack') && !loaded) return '纜繩未繫妥';
  if (action === 'letGo' && !loaded) return '未繫';
  return null;
}

@customElement('brm-mooring-panel')
export class MooringPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .quay-alarm {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
        font-weight: 700;
        border-radius: 4px;
        padding: 2px 6px;
        font-size: 12px;
      }
      .contacts,
      .lines {
        display: flex;
        flex-direction: column;
        gap: 3px;
        font-size: 12px;
      }
      .contact,
      .line-head {
        display: grid;
        grid-template-columns: 52px minmax(40px, 1fr) auto;
        gap: 6px;
        align-items: center;
        font-variant-numeric: tabular-nums;
      }
      .line {
        border-top: 1px solid var(--ob-border);
        padding-top: 3px;
        display: flex;
        flex-direction: column;
        gap: 3px;
      }
      .bar {
        position: relative;
        height: 8px;
        border-radius: 3px;
        background: var(--ob-bg-surface-raised);
        border: 1px solid var(--ob-border);
        overflow: hidden;
      }
      .bar > span {
        display: block;
        height: 100%;
        background: var(--ob-ok);
      }
      .bar.caution > span {
        background: var(--ob-caution);
      }
      .bar.alarm > span {
        background: var(--ob-alarm);
      }
      .ctl {
        display: grid;
        grid-template-columns: minmax(0, 1.2fr) auto minmax(0, 1fr) auto minmax(0, 1fr) auto auto;
        gap: 3px;
        align-items: center;
      }
      .ctl select,
      .ctl input {
        min-width: 0;
        width: 100%;
      }
      .ctl button {
        min-height: 24px;
        font-size: 11px;
        padding: 1px 4px;
      }
      .id {
        font-weight: 600;
      }
      .st {
        color: var(--ob-text-secondary);
      }
      .st.parted {
        color: var(--ob-alarm);
        font-weight: 700;
      }
    `,
  ];

  @property({ attribute: false }) berth: BerthState | null = null;
  @property({ attribute: false }) geometry: BerthGeometry | null = null;
  @property({ attribute: false }) lineDefs: readonly ScenarioMooringLineDef[] = [];

  @state() private drafts: Readonly<Record<string, LineDraft>> = {};

  private draft(id: string): LineDraft {
    return this.drafts[id] ?? {};
  }

  private setDraft(id: string, patch: LineDraft): void {
    this.drafts = { ...this.drafts, [id]: { ...this.draft(id), ...patch } };
  }

  private defaultBollard(l: MooringLineState): string {
    return this.draft(l.id).bollard ?? l.bollardId ?? this.lineDefs.find((d) => d.id === l.id)?.bollard ?? '';
  }

  private send(l: MooringLineState, action: MooringAction): void {
    const d = this.draft(l.id);
    const bollard = this.defaultBollard(l);
    const order: Parameters<typeof mooringCommand>[2] = {};
    if (action === 'makeFast' && bollard) order.bollard = bollard;
    if (action === 'heave' && d.tension !== undefined) order.tension_t = d.tension;
    if (action === 'slack') order.length_m = d.slack ?? 2;
    dispatchCommand(this, mooringCommand(l.id, action, order));
  }

  private renderContacts(b: BerthState) {
    if (b.contacts.length === 0) return html`<div class="hint">無碰墊受壓</div>`;
    return html`<div class="contacts">
      ${b.contacts.map((c) => {
        const rated = this.geometry?.fenders.find((f) => f.id === c.fenderId)?.ratedDeflection_pct ?? 72;
        const level = fenderLoadLevel(c, rated);
        const pct = c.deflection_pct ?? 0;
        return html`<div class="contact" data-fender=${c.fenderId}>
          <span class="id">${c.fenderId}</span>
          <div class="bar ${level}" title="壓縮 ${pct.toFixed(0)} %(額定 ${rated} %)"><span style="width:${Math.round(Math.min(1, pct / rated) * 100)}%"></span></div>
          <span>${pct.toFixed(0)} % · ${Math.round(c.reaction_kN)} kN${c.overload ? html` <b class="bad">過載</b>` : nothing}</span>
        </div>`;
      })}
    </div>`;
  }

  private renderLine(l: MooringLineState) {
    const d = this.draft(l.id);
    const level = lineLoadLevel(l);
    const ratio = lineLoadRatio(l);
    const bollards = this.geometry?.bollards ?? [];
    const def = this.lineDefs.find((x) => x.id === l.id);
    const btn = (action: MooringAction) => {
      const why = mooringActionBlocked(l, action);
      return html`<button class="act-${action}" ?disabled=${why !== null} title=${why ?? ''} @click=${() => this.send(l, action)}>${MOORING_ACTION_LABEL[action]}</button>`;
    };
    const current = this.defaultBollard(l);
    return html`<div class="line" data-line=${l.id}>
      <div class="line-head">
        <span class="id">${l.id}</span>
        <div class="bar ${level}" title="張力 / MBL ${Math.round(ratio * 100)} %"><span style="width:${Math.round(Math.min(1, ratio) * 100)}%"></span></div>
        <span><span class="st ${l.status}">${MOORING_STATUS_LABEL[l.status]}</span> ${l.tension_t.toFixed(1)}${l.mbl_t !== undefined ? ` / ${l.mbl_t.toFixed(0)}` : ''} t${l.bollardId ? ` · ${l.bollardId}` : ''}${l.length_m !== undefined ? ` · ${Math.round(l.length_m)} m` : ''}</span>
      </div>
      <div class="ctl">
        <select class="bollard" aria-label="${l.id} 繫船柱" .value=${current} @change=${(e: Event) => this.setDraft(l.id, { bollard: (e.target as HTMLSelectElement).value })}>
          ${bollards.length === 0 ? html`<option value=${current}>${current || '(預設)'}</option>` : nothing}
          ${bollards.map((b) => html`<option value=${b.id} ?selected=${b.id === current}>${b.id}${b.swl_t !== undefined ? ` ${b.swl_t} t` : ''}</option>`)}
        </select>
        ${btn('makeFast')}
        <input class="tension" type="number" min="0" step="1" placeholder=${def?.winchPull_t !== undefined ? `≤${def.winchPull_t} t` : 't'} aria-label="${l.id} 絞纜目標張力 (t)" .value=${d.tension === undefined ? '' : String(d.tension)} @input=${(e: Event) => this.setDraft(l.id, { tension: numberFromInput(e.target) })} />
        ${btn('heave')}
        <input class="slack-len" type="number" min="0.5" step="0.5" placeholder="2 m" aria-label="${l.id} 鬆纜長度 (m)" .value=${d.slack === undefined ? '' : String(d.slack)} @input=${(e: Event) => this.setDraft(l.id, { slack: numberFromInput(e.target) })} />
        ${btn('slack')}
        ${btn('letGo')}
      </div>
    </div>`;
  }

  override render() {
    const b = this.berth;
    return html`<div class="panel">
      <div class="panel-title"><span>繫纜 MOORING${b ? ` · ${b.name ?? b.id}` : ''}</span><span class="zh">張力條相對 MBL<span class="tag unsupported">僅 C# 核心</span></span></div>
      ${!b
        ? html`<div class="hint">情境沒有泊位</div>`
        : html`${b.quayContact ? html`<div class="quay-alarm">船體觸碰碼頭(硬接觸)</div>` : nothing}
            ${this.renderContacts(b)}
            <div class="lines">${b.lines.length === 0 ? html`<div class="hint">情境沒有定義纜繩</div>` : b.lines.map((l) => this.renderLine(l))}</div>
            ${!this.geometry ? html`<div class="hint">無泊位幾何(情境 berth.definition 未解析):帶纜用纜的預設繫船柱</div>` : nothing}`}
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-mooring-panel': MooringPanel;
  }
}
