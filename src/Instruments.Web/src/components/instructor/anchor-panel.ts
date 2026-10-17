// 錨面板(規劃書第 3 章錨/鏈規格、第 8.1 節 E12 錨泊/走錨;docs/design/physics-tugs-anchors-mooring.md 第 3 節):
// 左/右錨各一卡:狀態、鏈長(節與公尺)/最大鏈長、錨鏈孔張力與水平張力、躺底長、抓地力(水平張力對抓地力的比例條)、剎車與鏈端;
// 目標鏈長(節)輸入與 拋錨 letGo / 鬆鏈 veer / 起錨 heave / 剎車 brake / 鬆剎車 release 五鈕,送 anchor 指令。
// 目標空白時:letGo、veer 送到鏈端,heave 收妥(核心語意);明顯會被拒的動作(收存時鬆鏈/起錨/剎車、鏈端時鬆鏈/鬆剎車)停用並附提示。
// compact 屬性(操船台以外的精簡顯示用):只顯示讀數與 拋錨/剎車/起錨 三鈕。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import { ANCHOR_ACTION_LABEL, ANCHOR_SIDE_LABEL, ANCHOR_STATUS_LABEL, SHACKLE_LENGTH_M, anchorCommand, anchorLevel, chainText } from '../../lib/external-forces.js';
import { ANCHOR_ACTIONS, type AnchorAction, type AnchorSide, type AnchorState } from '../../types/external.js';

/** 動作是否明顯會被核心拒絕(依 AnchorUnit.Command 的規則;回傳提示文字,可用時 null)。 */
export function anchorActionBlocked(a: AnchorState, action: AnchorAction): string | null {
  const stowed = a.status === 'stowed';
  switch (action) {
    case 'letGo':
      if (a.fullyOut) return '錨鏈已全部送出';
      // 核心 AnchorUnit.Status 只有著底(OnBottom)時才回報 holding/dragging/heaving,letGo 遇著底一律 anchorAlreadyOut;
      // 起錨中鏈已上下直立時躺底長 onBottom_m 為 0,仍屬著底,故 heaving 亦停用
      if (!stowed && (a.status === 'running' || a.status === 'holding' || a.status === 'dragging' || a.status === 'heaving' || a.onBottom_m > 0)) return '錨已拋出(著底或送出中)';
      return null;
    case 'veer':
      if (stowed) return '錨仍收存,需先拋錨';
      if (a.fullyOut) return '錨鏈已全部送出';
      return null;
    case 'heave':
    case 'brake':
      return stowed ? '錨仍收存' : null;
    case 'release':
      return a.fullyOut ? '錨鏈已全部送出' : null;
  }
}

const ACTION_HINT: Readonly<Record<AnchorAction, string>> = {
  letGo: '拋錨:剎車放開,2 m/s 送出到目標(空白 = 鏈端)後剎車',
  veer: '鬆鏈:0.5 m/s 送出到目標',
  heave: '起錨:依起錨速率收鏈(空白 = 收妥);超過絞盤拉力停轉',
  brake: '剎車:停在目前鏈長',
  release: '放鏈:鬆剎車自由送出(到鏈端或下剎車)',
};

@customElement('brm-anchor-panel')
export class AnchorPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .anchors {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
        gap: 6px;
      }
      .anchor {
        display: flex;
        flex-direction: column;
        gap: 4px;
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 4px 6px;
      }
      .anchor.dragging {
        border-color: var(--ob-alarm);
      }
      .name {
        display: flex;
        justify-content: space-between;
        font-weight: 600;
        font-size: 13px;
      }
      .status {
        font-size: 11px;
        padding: 0 5px;
        border-radius: 3px;
        border: 1px solid var(--ob-border-strong);
        color: var(--ob-text-secondary);
      }
      .status.holding {
        border-color: var(--ob-ok);
        color: var(--ob-ok);
      }
      .status.running,
      .status.heaving,
      .status.aweigh {
        border-color: var(--ob-caution);
        color: var(--ob-caution);
      }
      .status.dragging {
        background: var(--ob-alarm);
        border-color: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      .read {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 1px 6px;
        font-size: 12px;
        font-variant-numeric: tabular-nums;
      }
      .read .l {
        color: var(--ob-text-muted);
      }
      .bar {
        height: 6px;
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
      .actions {
        display: grid;
        grid-template-columns: repeat(5, 1fr);
        gap: 2px;
      }
      :host([compact]) .actions {
        grid-template-columns: repeat(3, 1fr);
      }
      .actions button {
        min-height: 26px;
        font-size: 11px;
        padding: 1px 2px;
      }
      .target {
        display: flex;
        gap: 4px;
        align-items: center;
        font-size: 12px;
      }
      .target input {
        width: 64px;
      }
    `,
  ];

  @property({ attribute: false }) anchors: readonly AnchorState[] = [];
  @property({ type: Boolean, reflect: true }) compact = false;

  /** 目標鏈長(節)草稿;空白 = 不給目標 */
  @state() private targets: Readonly<Partial<Record<AnchorSide, number>>> = {};

  private send(a: AnchorState, action: AnchorAction): void {
    const sh = this.targets[a.side];
    dispatchCommand(this, anchorCommand(a.side, action, sh !== undefined ? { shackles: sh } : {}));
  }

  private renderAnchor(a: AnchorState) {
    const h = a.horizontalTension_t ?? a.tension_t;
    const ratio = a.holding_t && a.holding_t > 0 ? Math.min(1.2, h / a.holding_t) : 0;
    const level = anchorLevel(a);
    const maxSh = a.maxLength_m !== undefined ? a.maxLength_m / SHACKLE_LENGTH_M : undefined;
    const actions: readonly AnchorAction[] = this.compact ? ['letGo', 'brake', 'heave'] : ANCHOR_ACTIONS;
    return html`<div class="anchor ${a.status}" data-side=${a.side}>
      <div class="name"><span>${ANCHOR_SIDE_LABEL[a.side]}</span><span class="status ${a.status}">${ANCHOR_STATUS_LABEL[a.status]}</span></div>
      <div class="read">
        <span class="l">鏈長</span><span class="length">${chainText(a)}${maxSh !== undefined ? ` / ${maxSh.toFixed(0)} 節` : ''}${a.fullyOut ? ' 鏈端' : ''}</span>
        <span class="l">張力</span><span class="tension">${a.tension_t.toFixed(1)} t${a.horizontalTension_t !== undefined ? `(水平 ${a.horizontalTension_t.toFixed(1)})` : ''}</span>
        ${this.compact ? nothing : html`<span class="l">躺底</span><span>${Math.round(a.onBottom_m)} m</span>`}
        <span class="l">抓地力</span><span class="holding">${a.holding_t !== undefined ? `${a.holding_t.toFixed(1)} t` : '—'}</span>
        ${this.compact ? nothing : html`<span class="l">剎車</span><span>${a.brake === undefined ? '—' : a.brake ? '剎住' : '放開'}</span>`}
      </div>
      ${a.holding_t !== undefined && a.status !== 'stowed' ? html`<div class="bar ${level}" title="水平張力 / 抓地力 ${Math.round(ratio * 100)} %"><span style="width:${Math.round(Math.min(1, ratio) * 100)}%"></span></div>` : nothing}
      ${this.compact
        ? nothing
        : html`<div class="target">
            <label>目標</label>
            <input class="target-sh" type="number" min="0" step="0.5" placeholder=${a.status === 'stowed' ? '鏈端' : '—'} .value=${this.targets[a.side] === undefined ? '' : String(this.targets[a.side])} @input=${(e: Event) => {
              const v = numberFromInput(e.target);
              const next = { ...this.targets };
              if (v === undefined || v < 0) delete next[a.side];
              else next[a.side] = v;
              this.targets = next;
            }} />
            <span class="hint" title="空白:拋錨/鬆鏈到鏈端、起錨到收妥;放鏈 = 鬆剎車自由送出">節</span>
          </div>`}
      <div class="actions" role="group" aria-label="${ANCHOR_SIDE_LABEL[a.side]} 動作">
        ${actions.map((act) => {
          const why = anchorActionBlocked(a, act);
          return html`<button class="act-${act} ${act === 'letGo' ? 'danger' : ''}" ?disabled=${why !== null} title=${why ?? ACTION_HINT[act]} @click=${() => this.send(a, act)}>${ANCHOR_ACTION_LABEL[act]}</button>`;
        })}
      </div>
    </div>`;
  }

  override render() {
    return html`<div class="panel">
      <div class="panel-title"><span>錨 ANCHORS</span><span class="zh">一節 ${SHACKLE_LENGTH_M} m<span class="tag unsupported">僅 C# 核心</span></span></div>
      ${this.anchors.length === 0 ? html`<div class="hint">核心未回報錨資料</div>` : html`<div class="anchors">${this.anchors.map((a) => this.renderAnchor(a))}</div>`}
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-anchor-panel': AnchorPanel;
  }
}
