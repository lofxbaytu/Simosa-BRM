// 即時事件流(設計文件第 6 節 W2):核心推送的 SimEvent(目標船/時間表/評估/教官/引擎)依時間列出,
// 最新在上;可依來源篩選;嚴重度著色(hardFail/collision/grounding 紅);點擊發出 event-seek(重播檢視跳至該時刻)。
// 語音事件附「播放」鈕(發出 voice-play,由教官站的 VoiceScriptPlayer 播放)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import type { SimEvent, SimEventSource } from '../../types/event.js';
import { SIM_EVENT_SOURCES } from '../../types/event.js';
import { SOURCE_LABEL, eventLabel, eventSeverity } from '../../lib/sim-event.js';

export type EventSeekEvent = CustomEvent<SimEvent>;

@customElement('brm-event-stream')
export class EventStream extends LitElement {
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
      .filters {
        display: flex;
        gap: 3px;
        flex-wrap: wrap;
      }
      .filters button {
        min-height: 20px;
        padding: 0 6px;
        font-size: 11px;
      }
      .filters button.src-traffic.active {
        background: var(--ob-accent);
      }
      .item {
        cursor: pointer;
        border-left: 3px solid var(--ob-border);
      }
      .item.timeline {
        border-left-color: var(--ob-accent);
      }
      .item.assessment {
        border-left-color: var(--ob-ok);
      }
      .item.traffic {
        border-left-color: var(--ob-caution);
      }
      .item.instructor {
        border-left-color: var(--ob-text-muted);
      }
      .item.engine {
        border-left-color: var(--ob-border-strong);
      }
      .item.warning .text {
        color: var(--ob-warning);
      }
      .item.alarm .text {
        color: var(--ob-alarm);
        font-weight: 600;
      }
      .item.visible .text::after {
        content: ' 學員可見';
        font-size: 9px;
        color: var(--ob-text-muted);
        font-weight: 400;
      }
      .src {
        font-size: 10px;
        color: var(--ob-text-muted);
        white-space: nowrap;
        min-width: 34px;
      }
      .phase {
        font-size: 9px;
        color: var(--ob-text-muted);
      }
      .item button {
        min-height: 18px;
        padding: 0 5px;
        font-size: 10px;
      }
    `,
  ];

  @property({ attribute: false }) events: readonly SimEvent[] = [];
  @property({ type: Number }) max = 300;
  /** 顯示「播放」鈕(完整教官站;簡化模式自動播放不需要) */
  @property({ type: Boolean }) voiceButtons = false;
  /** 核心是否支援事件(Python 參考伺服器不推送) */
  @property({ type: Boolean }) unsupported = false;

  @state() private hiddenSources: Set<SimEventSource> = new Set();

  private toggle(src: SimEventSource): void {
    const next = new Set(this.hiddenSources);
    if (next.has(src)) next.delete(src);
    else next.add(src);
    this.hiddenSources = next;
  }

  private seek(e: SimEvent): void {
    this.dispatchEvent(new CustomEvent<SimEvent>('event-seek', { detail: e, bubbles: true, composed: true }));
  }

  private play(e: SimEvent, ev: Event): void {
    ev.stopPropagation();
    this.dispatchEvent(new CustomEvent<SimEvent>('voice-play', { detail: e, bubbles: true, composed: true }));
  }

  override render() {
    const shown = this.events.filter((e) => !this.hiddenSources.has(e.source)).slice(-this.max).reverse();
    const counts: Partial<Record<SimEventSource, number>> = {};
    for (const e of this.events) counts[e.source] = (counts[e.source] ?? 0) + 1;
    return html`
      <div class="panel">
        <div class="panel-title"><span>事件 EVENTS</span><span class="zh">${this.events.length} 筆${this.unsupported ? ' · 核心不推送事件' : ''}</span></div>
        <div class="filters" role="group" aria-label="事件來源篩選">
          ${SIM_EVENT_SOURCES.map(
            (src) => html`<button class="src-${src} ${this.hiddenSources.has(src) ? '' : 'active'}" title=${this.hiddenSources.has(src) ? '顯示' : '隱藏'} @click=${() => this.toggle(src)}>${SOURCE_LABEL[src]} ${counts[src] ?? 0}</button>`,
          )}
        </div>
        <div class="list" aria-label="事件流">
          ${shown.length === 0 ? html`<div class="hint">${this.events.length === 0 ? '尚無事件;核心的時間表、目標船、評估與教官指令事件會在此列出' : '已全部篩除'}</div>` : nothing}
          ${shown.map((e) => {
            const sev = eventSeverity(e);
            return html`<div class="item ${e.source} ${sev} ${e.visible ? 'visible' : ''}" data-kind=${e.kind} title=${`${e.kind} · seq ${e.seq} · tick ${e.tick}${e.phase ? ` · 階段 ${e.phase}` : ''}`} @click=${() => this.seek(e)}>
              <span class="time">${shortTime(e.t)}</span>
              <span class="src">${SOURCE_LABEL[e.source]}</span>
              <span class="text">${eventLabel(e)}</span>
              ${e.phase && e.phase !== 'normal' ? html`<span class="phase">${e.phase}</span>` : nothing}
              ${this.voiceButtons && e.kind === 'voiceScript' ? html`<button class="play-voice" @click=${(ev: Event) => this.play(e, ev)}>播放</button>` : nothing}
            </div>`;
          })}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-event-stream': EventStream;
  }
  interface HTMLElementEventMap {
    'event-seek': EventSeekEvent;
    'voice-play': EventSeekEvent;
  }
}
