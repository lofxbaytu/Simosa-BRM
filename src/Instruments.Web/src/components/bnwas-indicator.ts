// <brm-bnwas>:BNWAS 駕駛台值班警報系統指示(MSC.128(75);docs/design/sensors-alerts.md 第 4 節)。
//  - 模式(關/自動/開)、作動與階段(0 休眠、1 駕駛台視覺、2 駕駛台聲響、3 呼叫後備值班官/船長)、距下一階段倒數與休眠期進度條;
//  - 「重設」送 bnwasReset(任何舵令/車令/側推/自動舵/操舵模式/警報確認或靜音也會在核心重設 BNWAS);
//  - 第 1 階段黃色;第 2 階段以上整格紅色閃爍並放大提示;
//  - 學員端不提供模式開關(實船受鑰匙/密碼保護,由教官站 bnwasMode 操作)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from './shared.js';
import type { BnwasStatus } from '../types/alerts.js';
import { BNWAS_MODE_LABEL, bnwasDisplay, bnwasResetCommand } from '../lib/alerts.js';

@customElement('brm-bnwas')
export class BnwasIndicator extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        display: block;
      }
      .tile {
        gap: 3px;
        transition: background 0.2s;
      }
      .top {
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .top .title {
        font-size: 12px;
        font-weight: 700;
        color: var(--ob-text-secondary);
      }
      .mode {
        font-size: 11px;
        border: 1px solid var(--ob-border-strong);
        border-radius: 3px;
        padding: 0 4px;
        color: var(--ob-text-secondary);
      }
      .top .spacer {
        flex: 1;
      }
      .reset {
        font-weight: 700;
        min-height: 28px;
        padding: 2px 12px;
      }
      .stage {
        font-size: 13px;
        font-weight: 700;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .countdown {
        font-variant-numeric: tabular-nums;
        font-size: 12px;
        color: var(--ob-text-secondary);
      }
      .bar {
        height: 5px;
        border-radius: 3px;
        background: var(--ob-bg-input);
        border: 1px solid var(--ob-border);
        overflow: hidden;
      }
      .bar > span {
        display: block;
        height: 100%;
        background: var(--ob-ok);
      }
      .level-off .stage,
      .level-standby .stage {
        color: var(--ob-text-muted);
        font-weight: 500;
      }
      .level-caution {
        border-color: var(--ob-caution);
        background: color-mix(in srgb, var(--ob-caution) 22%, var(--ob-bg-surface));
      }
      .level-caution .bar > span {
        background: var(--ob-caution);
      }
      .level-alarm {
        border-color: var(--ob-alarm);
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      .level-alarm .title,
      .level-alarm .countdown,
      .level-alarm .mode {
        color: var(--ob-alarm-text);
        border-color: var(--ob-alarm-text);
      }
      .level-alarm .stage {
        font-size: 15px;
      }
      .level-alarm .reset {
        background: var(--ob-bg-surface);
        color: var(--ob-text);
        border-color: var(--ob-alarm-text);
        font-size: 14px;
      }
      .flash {
        animation: brm-bnwas-flash 1s steps(1, end) infinite;
      }
      @keyframes brm-bnwas-flash {
        50% {
          background: color-mix(in srgb, var(--ob-alarm) 45%, var(--ob-bg-surface));
        }
      }
    `,
  ];

  /** state.bnwas(核心不支援時 undefined) */
  @property({ attribute: false }) bnwas: BnwasStatus | undefined = undefined;

  private reset(): void {
    dispatchCommand(this, bnwasResetCommand());
  }

  override render() {
    const b = this.bnwas;
    const d = bnwasDisplay(b);
    const progress = b && b.active && b.stage === 0 && b.remaining_s !== undefined && b.dormant_s > 0 ? Math.max(0, Math.min(1, b.remaining_s / b.dormant_s)) : null;
    return html`
      <div class="tile level-${d.level} ${d.flashing ? 'flash' : ''}" role="status" aria-label="BNWAS" data-stage=${b?.stage ?? -1}>
        <div class="top">
          <span class="title">BNWAS</span>
          ${b ? html`<span class="mode">模式 ${BNWAS_MODE_LABEL[b.mode]}${b.mode !== 'off' ? ` · 休眠 ${Math.round(b.dormant_s)} s` : ''}</span>` : nothing}
          <span class="spacer"></span>
          <button class="reset" title="BNWAS 重設(值班確認)" ?disabled=${!d.resettable} @click=${this.reset}>重設</button>
        </div>
        <div class="stage">${d.label}</div>
        ${d.countdown ? html`<div class="countdown">${d.countdown}</div>` : nothing}
        ${progress !== null ? html`<div class="bar" aria-hidden="true"><span style="width:${(progress * 100).toFixed(1)}%"></span></div>` : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-bnwas': BnwasIndicator;
  }
}
