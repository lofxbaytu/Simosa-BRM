// 即時事件流(設計文件第 6 節 W2):核心推送的 SimEvent(目標船/時間表/評估/教官/引擎)依時間列出,
// 最新在上;可依來源篩選;嚴重度著色(hardFail/collision/grounding 紅);點擊發出 event-seek(重播檢視跳至該時刻)。
// 語音事件附「播放」鈕(發出 voice-play,由教官站的 VoiceScriptPlayer 播放)。
// 同一個碰墊連續的低速接觸在進來之前已合併成一列(lib/event-merge.ts;EventStore):列上寫次數、第一次到最近一次的時刻與最大值。
// **合併列留在它建立時的位置**(displayRows:依「第一次接觸」的時刻排,時間欄也是它),之後每併入一次只在原地更新次數、最近時刻與最大值,
// 不移到最上面 —— 持續擺盪時(25 kn 吹攏風:四個碰墊各一列進行中的合併列、每列約 84 px、每幾秒就有一列併入)若每併入一次就跳到最上面,
// 清單可見的 120 px 一直被它們占著,故障、警報、教官指令出現幾秒就被推出可見範圍(實測 1.6–10.7 s),與合併前一樣吵。
// 新的一段擺盪(隔超過門檻再碰回、上一列滿了)另起新列,出現在最上面。EventStore 內部仍依最近活動排序(2000 列上限先擠掉最久沒有活動的列、
// 倒帶依 t 截斷);講評報告的時間軸本來就把合併列排在第一次接觸的時刻。
// 標題與各來源鈕上的數字是列數;有合併列時標題另寫合併前的事件數。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { ifDefined } from 'lit/directives/if-defined.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import type { SimEvent, SimEventSource } from '../../types/event.js';
import { SIM_EVENT_SOURCES } from '../../types/event.js';
import { SOURCE_LABEL, eventLabel, eventSeverity } from '../../lib/sim-event.js';
import { FENDER_MERGE_GAP_S, fenderIdOf, rawEventCount } from '../../lib/event-merge.js';

export type EventSeekEvent = CustomEvent<SimEvent>;

/** 列在清單裡的位置依據的時刻(時間欄也顯示它):合併列是第一次接觸(這一列建立的時候),其餘是事件本身的時刻。 */
export function rowTime(e: SimEvent): number {
  return e.merged ? e.merged.firstT : e.t;
}

/**
 * 事件流要顯示的列(最新在上):events 是 EventStore 的清單(依最近活動遞增)。依 rowTime 排序(同一時刻維持原順序),取最新的 max 列;
 * 另外,仍在進行中的合併列(最近一次接觸距最新事件不超過合併門檻)即使已經排到 max 列之外也留著(排在最下面):
 * 它還在累計,往下捲要找得到。沒有合併列時與原本相同(清單本來就依 t 遞增)。
 */
export function displayRows(events: readonly SimEvent[], max: number): SimEvent[] {
  let rows: readonly SimEvent[] = events;
  if (events.some((e) => e.merged)) {
    // 穩定排序:合併列回到它第一次接觸的位置,其餘列的相對順序不變
    rows = events.map((e, i) => ({ e, i, t: rowTime(e) })).sort((a, b) => a.t - b.t || a.i - b.i).map((x) => x.e);
  }
  const cut = Math.max(0, rows.length - max);
  const shown = rows.slice(cut);
  if (cut > 0) {
    const latest = events[events.length - 1]!.t;
    const active = rows.slice(0, cut).filter((e) => e.merged && latest - e.t <= FENDER_MERGE_GAP_S);
    if (active.length > 0) return [...active, ...shown].reverse();
  }
  return shown.reverse();
}

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
        gap: 2px;
        flex-wrap: wrap;
      }
      /* 六個來源篩選鈕在 320 px 寬的欄內排成一行(左右內距 4 px);折成兩行會少掉一列事件的高度 */
      .filters button {
        min-height: 20px;
        padding: 0 4px;
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
      .item.vhf {
        border-left-color: var(--ob-ok);
        border-left-style: double;
      }
      .item.warning .text {
        color: var(--ob-warning);
      }
      .item.alarm .text {
        color: var(--ob-alarm);
        font-weight: 600;
      }
      /* 共用樣式的 .alarm(紅底)會套到事件列,使紅字落在紅底上;事件列改為淡紅底 */
      .list .item.alarm {
        background: color-mix(in srgb, var(--ob-alarm) 12%, transparent);
        color: var(--ob-text);
        padding: 2px 4px;
        border-radius: 3px;
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
    const shown = displayRows(this.hiddenSources.size === 0 ? this.events : this.events.filter((e) => !this.hiddenSources.has(e.source)), this.max);
    const counts: Partial<Record<SimEventSource, number>> = {};
    for (const e of this.events) counts[e.source] = (counts[e.source] ?? 0) + 1;
    // 列數(合併列算一列,與各來源鈕上的數字、2000 列上限一致);有合併列時另寫合併前的事件數
    const raw = rawEventCount(this.events);
    return html`
      <div class="panel">
        <div class="panel-title">
          <span>事件 EVENTS</span><span class="zh count">${this.events.length} 筆${raw > this.events.length ? `(合併前 ${raw})` : ''}${this.unsupported ? ' · 核心不推送事件' : ''}</span>
        </div>
        <div class="filters" role="group" aria-label="事件來源篩選">
          ${SIM_EVENT_SOURCES.map(
            (src) => html`<button class="src-${src} ${this.hiddenSources.has(src) ? '' : 'active'}" title=${this.hiddenSources.has(src) ? '顯示' : '隱藏'} @click=${() => this.toggle(src)}>${SOURCE_LABEL[src]} ${counts[src] ?? 0}</button>`,
          )}
        </div>
        <div class="list" aria-label="事件流">
          ${shown.length === 0 ? html`<div class="hint">${this.events.length === 0 ? '尚無事件;核心的時間表、目標船、評估與教官指令事件會在此列出' : '已全部篩除'}</div>` : nothing}
          ${shown.map((e) => {
            const sev = eventSeverity(e);
            const m = e.merged;
            // 合併列:時間欄是第一次接觸(列的位置依它);seq / tick / 階段是最近一次接觸的
            const title = `${e.kind}${m ? ` · 合併 ${m.count} 筆(${shortTime(m.firstT)}–${shortTime(m.lastT)};最近一次` : ''} · seq ${e.seq} · tick ${e.tick}${m ? ')' : ''}${e.phase ? ` · 階段 ${e.phase}` : ''}`;
            return html`<div
              class="item ${e.source} ${sev} ${e.visible ? 'visible' : ''} ${m ? 'merged' : ''}"
              data-kind=${e.kind}
              data-fender=${ifDefined(fenderIdOf(e) ?? undefined)}
              data-merged-count=${ifDefined(m?.count)}
              title=${title}
              @click=${() => this.seek(e)}
            >
              <span class="time">${shortTime(rowTime(e))}</span>
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
