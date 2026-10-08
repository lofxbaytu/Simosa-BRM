// 自船覆寫(規劃書第 9.1 節):位置/航向/速度、裝載狀態、拖船數量。command.schema.json 無專用指令,
// 以 reset 附 args 送出(Python 參考伺服器讀 x/y/heading/speed;C# 核心目前忽略 args);未支援者標示。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import { CORE_SUPPORT_LABEL, OWNSHIP_FIELD_SUPPORT, ownShipOverrideCommand, type OwnShipOverride } from '../../lib/instructor-command.js';
import type { LoadingCondition, OwnShipState } from '../../types/state.js';

@customElement('brm-ownship-panel')
export class OwnShipPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .pair {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 4px;
      }
    `,
  ];

  @property({ attribute: false }) state: OwnShipState | null = null;

  @state() private form: Required<Omit<OwnShipOverride, 'lat' | 'lon'>> = { x: 0, y: 0, heading: 0, speed: 0, loading: 'ballast', tugs: 0 };
  @state() private confirm = false;
  private confirmTimer: ReturnType<typeof setTimeout> | null = null;
  private seeded = false;

  override updated(): void {
    if (!this.seeded && this.state) {
      this.seeded = true;
      this.fill();
    }
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.confirmTimer) clearTimeout(this.confirmTimer);
  }

  private fill(): void {
    const s = this.state;
    if (!s) return;
    this.form = { ...this.form, x: Math.round(s.pos.x), y: Math.round(s.pos.y), heading: Math.round(s.heading), speed: Math.round(s.stw * 10) / 10, loading: s.loading };
  }

  private setNum(key: 'x' | 'y' | 'heading' | 'speed' | 'tugs', ev: Event): void {
    const v = numberFromInput(ev.target);
    if (v !== undefined) this.form = { ...this.form, [key]: v };
  }

  private apply(): void {
    if (!this.confirm) {
      this.confirm = true;
      this.confirmTimer = setTimeout(() => (this.confirm = false), 4000);
      return;
    }
    if (this.confirmTimer) clearTimeout(this.confirmTimer);
    this.confirm = false;
    dispatchCommand(this, ownShipOverrideCommand({ ...this.form }));
    this.dispatchEvent(new CustomEvent('sim-reset', { bubbles: true, composed: true }));
  }

  private tag(field: keyof typeof OWNSHIP_FIELD_SUPPORT) {
    const label = CORE_SUPPORT_LABEL[OWNSHIP_FIELD_SUPPORT[field]];
    return label ? html`<span class="tag unsupported">${label}</span>` : nothing;
  }

  override render() {
    const f = this.form;
    return html`
      <div class="panel">
        <div class="panel-title"><span>自船覆寫 OWN SHIP</span><span class="zh">以 reset + args 送出</span></div>
        <div class="form">
          <label>位置 x / y (m)${this.tag('position')}</label>
          <div class="pair">
            <input class="pos-x" type="number" step="10" .value=${String(f.x)} @input=${(e: Event) => this.setNum('x', e)} />
            <input class="pos-y" type="number" step="10" .value=${String(f.y)} @input=${(e: Event) => this.setNum('y', e)} />
          </div>
          <label>航向°${this.tag('heading')}</label>
          <input class="heading" type="number" min="0" max="360" step="1" .value=${String(f.heading)} @input=${(e: Event) => this.setNum('heading', e)} />
          <label>速度 kn${this.tag('speed')}</label>
          <input class="speed" type="number" min="-10" max="20" step="0.1" .value=${String(f.speed)} @input=${(e: Event) => this.setNum('speed', e)} />
          <label>裝載${this.tag('loading')}</label>
          <select class="loading" .value=${f.loading} @change=${(e: Event) => (this.form = { ...this.form, loading: (e.target as HTMLSelectElement).value as LoadingCondition })}>
            <option value="full">滿載</option>
            <option value="ballast">壓載</option>
            <option value="intermediate">中間</option>
          </select>
          <label>拖船數${this.tag('tugs')}</label>
          <input class="tugs" type="number" min="0" max="4" step="1" .value=${String(f.tugs)} @input=${(e: Event) => this.setNum('tugs', e)} />
        </div>
        <div class="btn-row">
          <button @click=${this.fill} ?disabled=${!this.state}>帶入目前值</button>
          <button class="primary apply ${this.confirm ? 'danger' : ''}" @click=${this.apply}>${this.confirm ? '再按一次確認覆寫' : '套用覆寫'}</button>
        </div>
        <div class="hint">C# 核心的 reset 會回到情境初始狀態(忽略 args);Python 參考伺服器依 x/y/航向/速度重設</div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-ownship-panel': OwnShipPanel;
  }
}
