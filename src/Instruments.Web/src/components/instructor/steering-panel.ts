// 操舵模式控制(規劃書第 7.2 節操舵模式選擇;設計文件 D32/D35):顯示 state.steering(模式、泵、NFU 推舵中),
// 按鈕 FU / NFU / 自動舵 / 應急操舵送 steeringMode 指令;NFU 的左/右推桿按住送 direction、放開送 stop;
// 泵選擇隨指令附上;自動舵可帶設定航向與 ROT 限制。核心只有 C# SimCore 支援(Python 參考伺服器照收無作用)。
// compact 屬性(簡化教官模式執行畫面):只顯示目前模式、FU / NFU / 應急操舵三鈕與 NFU 左右推桿,不顯示自動舵、泵與航向/ROT 表單。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import { steeringModeCommand, steeringLabel } from '../../lib/steering.js';
import { STEERING_MODE_LABEL } from '../../lib/sim-event.js';
import type { NfuDirection } from '../../types/command.js';
import { STEERING_MODES, STEERING_PUMPS, type OwnShipState, type SteeringModeValue, type SteeringPump } from '../../types/state.js';

@customElement('brm-steering-panel')
export class SteeringPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .status {
        font-size: 13px;
        font-weight: 600;
        font-variant-numeric: tabular-nums;
      }
      .status.none {
        color: var(--ob-text-muted);
        font-weight: 400;
      }
      .status.emergency {
        color: var(--ob-warning);
      }
      .modes {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 4px;
      }
      .modes button {
        min-height: 32px;
        font-weight: 600;
        font-size: 12px;
      }
      :host([compact]) .modes {
        grid-template-columns: repeat(3, 1fr);
      }
      :host([compact]) .modes button,
      :host([compact]) .nfu button {
        min-height: 44px;
        font-size: 15px;
      }
      :host([compact]) .status {
        font-size: 16px;
      }
      .nfu {
        display: grid;
        grid-template-columns: 1fr 1fr 1fr;
        gap: 4px;
      }
      .nfu button {
        min-height: 36px;
        font-weight: 700;
        user-select: none;
        touch-action: none;
      }
      .nfu button.pushing {
        background: var(--ob-accent);
        color: var(--ob-text-on-accent);
        border-color: var(--ob-accent-strong);
      }
      .pair {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 4px;
      }
    `,
  ];

  @property({ attribute: false }) state: OwnShipState | null = null;
  /** 精簡版(簡化教官模式):只有模式狀態、FU / NFU / 應急操舵三鈕與 NFU 推桿。 */
  @property({ type: Boolean, reflect: true }) compact = false;

  @state() private pump: SteeringPump | '' = '';
  @state() private heading: number | undefined;
  @state() private rotLimit: number | undefined;
  @state() private pushing: NfuDirection | null = null;

  private get mode(): SteeringModeValue | undefined {
    return this.state?.steering?.mode;
  }

  /** 送出時的泵:表單有選用表單的,否則不附(核心保留目前泵)。 */
  private pumpArg(): { pump?: SteeringPump } {
    return this.pump ? { pump: this.pump } : {};
  }

  private setMode(mode: SteeringModeValue): void {
    if (mode === 'nfu') dispatchCommand(this, steeringModeCommand('nfu', { ...this.pumpArg(), direction: 'stop' }));
    else if (mode === 'autopilot') {
      const args: { pump?: SteeringPump; heading?: number; rotLimit?: number } = { ...this.pumpArg() };
      if (this.heading !== undefined) args.heading = this.heading;
      if (this.rotLimit !== undefined) args.rotLimit = this.rotLimit;
      dispatchCommand(this, steeringModeCommand('autopilot', args));
    } else dispatchCommand(this, steeringModeCommand(mode, this.pumpArg()));
  }

  private setPump(ev: Event): void {
    const v = (ev.target as HTMLSelectElement).value;
    this.pump = (STEERING_PUMPS as readonly string[]).includes(v) ? (v as SteeringPump) : '';
    // 已在某模式時直接送同模式 + 新泵(核心只換泵、發 steeringModeChanged)
    const mode = this.mode;
    if (this.pump && mode && mode !== 'nfu') dispatchCommand(this, steeringModeCommand(mode, { pump: this.pump }));
    else if (this.pump && mode === 'nfu') dispatchCommand(this, steeringModeCommand('nfu', { pump: this.pump, direction: this.pushing ?? 'stop' }));
  }

  /** NFU 推桿:按住送 direction,放開(或指標離開)送 stop;放開時若已不在 nfu 不送。 */
  private nfuPush(direction: NfuDirection): void {
    this.pushing = direction;
    dispatchCommand(this, steeringModeCommand('nfu', { ...this.pumpArg(), direction }));
  }

  private nfuRelease(): void {
    if (this.pushing === null) return;
    this.pushing = null;
    dispatchCommand(this, steeringModeCommand('nfu', { ...this.pumpArg(), direction: 'stop' }));
  }

  override render() {
    const st = this.state?.steering;
    const mode = st?.mode;
    const emergencyBlocksAutopilot = mode === 'emergency';
    const modes = this.compact ? STEERING_MODES.filter((m) => m !== 'autopilot') : STEERING_MODES;
    return html`
      <div class="panel">
        <div class="panel-title"><span>操舵模式 STEERING</span><span class="tag unsupported">僅 C# 核心</span></div>
        <div class="status ${st ? st.mode : 'none'}" aria-live="polite">${steeringLabel(st)}</div>
        <div class="modes" role="group" aria-label="操舵模式">
          ${modes.map(
            (m) => html`<button class="mode-${m} ${mode === m ? 'active' : ''}" ?disabled=${m === 'autopilot' && emergencyBlocksAutopilot} title=${m === 'autopilot' && emergencyBlocksAutopilot ? '應急操舵中接不到自動舵,需先切回 FU' : ''} @click=${() => this.setMode(m)}>${STEERING_MODE_LABEL[m]}</button>`,
          )}
        </div>
        <div class="nfu" role="group" aria-label="NFU 推桿">
          <button
            class="nfu-port ${this.pushing === 'port' ? 'pushing' : ''}"
            @pointerdown=${(e: PointerEvent) => {
              e.preventDefault();
              this.nfuPush('port');
            }}
            @pointerup=${this.nfuRelease}
            @pointerleave=${this.nfuRelease}
            @pointercancel=${this.nfuRelease}
          >
            ◀ 左 PORT
          </button>
          <button class="nfu-stop" @click=${() => this.setMode('nfu')}>NFU 停</button>
          <button
            class="nfu-stbd ${this.pushing === 'starboard' ? 'pushing' : ''}"
            @pointerdown=${(e: PointerEvent) => {
              e.preventDefault();
              this.nfuPush('starboard');
            }}
            @pointerup=${this.nfuRelease}
            @pointerleave=${this.nfuRelease}
            @pointercancel=${this.nfuRelease}
          >
            右 STBD ▶
          </button>
        </div>
        ${this.compact
          ? nothing
          : html`<div class="form">
          <label>舵機泵</label>
          <select class="pump" .value=${this.pump} @change=${this.setPump}>
            <option value="">(核心目前${st ? ` ${st.pump}` : ''})</option>
            ${STEERING_PUMPS.map((p) => html`<option value=${p}>${p === 'both' ? '1 + 2' : `No.${p}`}</option>`)}
          </select>
          <label>自動舵航向° / ROT</label>
          <div class="pair">
            <input class="ap-heading" type="number" min="0" max="360" step="1" placeholder="保持" .value=${this.heading === undefined ? '' : String(this.heading)} @input=${(e: Event) => (this.heading = numberFromInput(e.target))} />
            <input class="ap-rot" type="number" min="1" max="120" step="1" placeholder="限制" .value=${this.rotLimit === undefined ? '' : String(this.rotLimit)} @input=${(e: Event) => (this.rotLimit = numberFromInput(e.target))} />
          </div>
        </div>`}
        ${emergencyBlocksAutopilot && !this.compact ? html`<div class="hint bad">應急操舵中:自動舵指令會被核心拒絕(commandRejected),需先切回 FU</div>` : nothing}
        ${mode === 'nfu' ? html`<div class="hint">NFU:按住左/右推桿推舵,放開即停;離開 NFU 時舵令同步到目前舵角</div>` : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-steering-panel': SteeringPanel;
  }
}
