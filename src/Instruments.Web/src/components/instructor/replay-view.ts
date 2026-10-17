// 重播檢視(講評站雛型,規劃書第 9.2 節「重播」與「航跡圖」):以檔案選擇器載入 SimCore 的 JSON Lines 紀錄
// 或教官站的 JSON 紀錄,時間軸拖曳檢視任一時刻的狀態(鳥瞰航跡圖 + conning 儀器摘要),列出輸入事件、核心事件與
// BRM 標記(若紀錄含),整段的講評摘要(核心 summary 優先,無則本地推導);不需核心連線。
// 設計文件第 6 節 W8:時間軸上畫事件標記(顏色依來源;hardFail/collision/grounding 紅)、航跡圖疊 header.scenario 的
// 航線走廊與區域、顯示 header.parameterSet 版次。時間軸另畫倒帶標記(ReplayData.rewinds[];懸停顯示倒帶 tick 與丟棄筆數),
// 航跡圖畫該時刻 state 的目標船(cpaAlarm 狀態由 t ≤ 目前時刻的事件推得)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import { parseReplay, stateIndexAt, trackPoints, type ReplayData, type ReplayRewind } from '../../lib/record-replay.js';
import { TrackHistory, type TrackPoint } from '../../lib/track-history.js';
import { getShipConfig } from '../../lib/ship-config.js';
import { summarize, type DebriefSummary } from '../../lib/debrief.js';
import { brmCategory } from '../../lib/brm-markers.js';
import { defaultState } from '../../lib/state-parser.js';
import { eventLabel, eventSeverity } from '../../lib/sim-event.js';
import { eventMarks, overlayFromHeaderScenario, type PlotOverlay } from '../../lib/plot-overlay.js';
import { cpaAlarmTargets } from '../../lib/targets.js';
import type { SimEvent } from '../../types/event.js';
import '../conning-bar.js';
import '../track-plot.js';
import './debrief-summary.js';
import './event-stream.js';

const PLAY_RATES = [0.25, 1, 4, 16];

@customElement('brm-replay-view')
export class ReplayView extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: grid;
        grid-template-rows: auto auto minmax(0, 1fr);
        gap: 6px;
        height: 100%;
        min-height: 0;
      }
      .toolbar {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        font-size: 12px;
      }
      .toolbar .file {
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .toolbar .info {
        color: var(--ob-text-secondary);
      }
      .timeline {
        display: grid;
        grid-template-columns: auto 1fr auto;
        gap: 8px;
        align-items: center;
      }
      .timeline .track {
        display: grid;
        grid-template-rows: 14px auto;
        gap: 2px;
      }
      .marks {
        position: relative;
        height: 14px;
        border-bottom: 1px solid var(--ob-border);
      }
      .marks .mark {
        position: absolute;
        top: 1px;
        width: 2px;
        height: 12px;
        transform: translateX(-1px);
        background: var(--ob-text-muted);
        cursor: pointer;
      }
      .marks .mark.timeline {
        background: var(--ob-accent);
      }
      .marks .mark.assessment {
        background: var(--ob-ok);
      }
      .marks .mark.traffic {
        background: var(--ob-caution);
      }
      .marks .mark.instructor {
        background: var(--ob-text-muted);
      }
      .marks .mark.warning {
        background: var(--ob-warning);
      }
      .marks .mark.alarm {
        background: var(--ob-alarm);
        width: 3px;
      }
      .marks .mark.phase {
        top: 0;
        height: 14px;
        width: 1px;
        background: var(--ob-border-strong);
      }
      .marks .mark.marker {
        top: 8px;
        height: 5px;
        background: var(--ob-text);
      }
      .marks .mark.rewind {
        top: 0;
        height: 14px;
        width: 0;
        background: none;
        border-left: 3px double var(--ob-warning);
        transform: translateX(-2px);
      }
      .timeline input[type='range'] {
        width: 100%;
        min-height: 20px;
      }
      .timeline .t {
        font-variant-numeric: tabular-nums;
        font-weight: 600;
        min-width: 70px;
        text-align: center;
      }
      .main {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 360px;
        grid-template-rows: minmax(150px, 22vh) minmax(0, 1fr);
        gap: 6px;
        min-height: 0;
      }
      .main brm-conning-bar {
        grid-column: 1 / -1;
      }
      .side {
        display: grid;
        grid-template-rows: minmax(0, 1fr) minmax(0, 0.8fr) minmax(0, 0.6fr) auto;
        gap: 6px;
        min-height: 0;
      }
      .side brm-event-stream {
        min-height: 0;
      }
      .param-set {
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      .side .panel {
        min-height: 0;
      }
      .side .summary {
        max-height: 40vh;
        overflow: auto;
      }
      .item.current {
        background: var(--ob-accent-soft);
      }
      .empty {
        display: grid;
        place-items: center;
        height: 100%;
        color: var(--ob-text-secondary);
        border: 1px dashed var(--ob-border-strong);
        border-radius: 6px;
        font-size: 14px;
        text-align: center;
        padding: 12px;
      }
      .errors {
        color: var(--ob-warning);
        font-size: 11px;
        max-height: 60px;
        overflow: auto;
      }
    `,
  ];

  @property({ type: Boolean }) compact = false;

  @state() private data: ReplayData | null = null;
  @state() private fileName = '';
  @state() private t = 0;
  @state() private playing = false;
  @state() private rate = 1;
  @state() private summary: DebriefSummary | null = null;
  @state() private loading = false;
  @state() private overlay: PlotOverlay | null = null;

  private points: TrackPoint[] = [];
  private history = new TrackHistory(1, 100000);
  private timer: ReturnType<typeof setInterval> | null = null;
  private lastTickWall = 0;

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.stopPlay();
  }

  /** 由文字載入紀錄(檔案選擇器與測試共用)。 */
  loadText(text: string, name = ''): ReplayData {
    const data = parseReplay(text);
    this.data = data;
    this.fileName = name;
    this.points = trackPoints(data.states, 1);
    this.summary = data.states.length > 0 ? summarize(data.states) : null;
    const base = overlayFromHeaderScenario(data.header.scenario);
    this.overlay = base.route || base.zones.length > 0 || data.events.length > 0 ? base : null;
    this.stopPlay();
    this.seek(data.tStart);
    return data;
  }

  /** 跳至指定時刻(教官站事件流點擊、測試)。 */
  seekTo(t: number): void {
    this.stopPlay();
    this.seek(t);
  }

  /** 目前時刻 */
  get time(): number {
    return this.t;
  }

  /** 已載入紀錄 */
  get loaded(): boolean {
    return this.data !== null;
  }

  private async onFile(ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.loading = true;
    try {
      const text = await file.text();
      this.loadText(text, file.name);
    } finally {
      this.loading = false;
    }
  }

  private seek(t: number): void {
    const d = this.data;
    if (!d) return;
    this.t = Math.min(d.tEnd, Math.max(d.tStart, t));
    this.history = new TrackHistory(1, 100000);
    for (const p of this.points) {
      if (p.t > this.t) break;
      this.history.push(p.x, p.y, p.t);
    }
    if (this.overlay) this.overlay = { ...this.overlay, events: eventMarks(d.events, d.states, this.t) };
  }

  private onEventSeek(ev: CustomEvent<SimEvent>): void {
    this.seekTo(ev.detail.t);
  }

  private renderMarks(d: ReplayData) {
    const span = d.tEnd - d.tStart;
    if (span <= 0) return nothing;
    const pct = (t: number) => `${((Math.min(d.tEnd, Math.max(d.tStart, t)) - d.tStart) / span) * 100}%`;
    const phases = d.summary?.phases ?? [];
    return html`<div class="marks" aria-label="事件標記">
      ${phases.slice(1).map((p) => html`<span class="mark phase" style="left:${pct(p.from_s)}" title=${`階段 ${p.phase} ${shortTime(p.from_s)}`}></span>`)}
      ${d.events.map((e) => html`<span class="mark ${e.source} ${eventSeverity(e)}" style="left:${pct(e.t)}" title=${`${shortTime(e.t)} ${eventLabel(e)}`} @click=${() => this.seekTo(e.t)}></span>`)}
      ${d.markers.map((m) => html`<span class="mark marker" style="left:${pct(m.t)}" title=${`${shortTime(m.t)} ${brmCategory(m.category).label} ${m.note}`} @click=${() => this.seekTo(m.t)}></span>`)}
      ${d.rewinds.map((r) => html`<span class="mark rewind" style="left:${pct(r.t)}" title=${rewindTitle(r, d.header.dt)} @click=${() => this.seekTo(r.t)}></span>`)}
    </div>`;
  }

  private onSlide(ev: Event): void {
    this.stopPlay();
    this.seek(Number((ev.target as HTMLInputElement).value));
  }

  private togglePlay(): void {
    if (this.playing) this.stopPlay();
    else this.startPlay();
  }

  private startPlay(): void {
    const d = this.data;
    if (!d || d.states.length === 0) return;
    if (this.t >= d.tEnd) this.seek(d.tStart);
    this.playing = true;
    this.lastTickWall = performance.now();
    this.timer = setInterval(() => {
      const now = performance.now();
      const dt = ((now - this.lastTickWall) / 1000) * this.rate;
      this.lastTickWall = now;
      const next = this.t + dt;
      if (next >= d.tEnd) {
        this.seek(d.tEnd);
        this.stopPlay();
      } else this.seek(next);
    }, 100);
  }

  private stopPlay(): void {
    this.playing = false;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private step(sign: 1 | -1): void {
    const d = this.data;
    if (!d || d.states.length === 0) return;
    this.stopPlay();
    const i = stateIndexAt(d.states, this.t);
    const j = Math.min(d.states.length - 1, Math.max(0, i + sign));
    this.seek(d.states[j]!.t);
  }

  override render() {
    const d = this.data;
    const idx = d ? stateIndexAt(d.states, this.t) : -1;
    const current = d && idx >= 0 ? d.states[idx]! : defaultState('FSB1');
    const cfg = getShipConfig(current.shipId);
    return html`
      <div class="toolbar">
        <label class="file">
          <span>紀錄檔</span>
          <input class="file-input" type="file" accept=".jsonl,.json,.txt,application/json" @change=${this.onFile} />
        </label>
        ${this.loading ? html`<span class="info">讀取中…</span>` : nothing}
        ${d
          ? html`<span class="info">
              ${this.fileName} · ${d.source === 'simcore-jsonl' ? 'SimCore JSON Lines' : '教官站紀錄'} · ${d.header.scenarioId ?? '(無情境)'}${d.header.scenarioName ? ` ${d.header.scenarioName}` : ''} ·
              ${d.header.shipId ?? '?'}${d.header.loading ? `/${d.header.loading}` : ''} · ${d.states.length} 筆狀態 · ${d.inputs.length} 筆輸入 · ${d.events.length} 筆事件 · ${d.markers.length} 筆標記
              ${d.header.dynamics ? ` · ${d.header.dynamics}` : ''}${d.footer?.stateHash ? ` · 雜湊 ${d.footer.stateHash.slice(0, 8)}…` : ''}
              ${d.header.parameterSet ? html`<span class="param-set"> · 參數集 ${d.header.parameterSet.id ?? ''} ${d.header.parameterSet.version ?? ''}${d.header.parameterSet.resolved.length > 0 ? `(已解析 ${d.header.parameterSet.resolved.length} 個引用)` : ''}</span>` : nothing}
              ${d.summary?.assessment.score !== undefined ? html`<span class="param-set"> · 評分 ${Math.round(d.summary.assessment.score)}${d.summary.assessment.passed !== undefined ? (d.summary.assessment.passed ? '(通過)' : '(未通過)') : ''}</span>` : nothing}
            </span>`
          : html`<span class="info">選擇 SimCore 的 .jsonl 紀錄(src/SimCore/README.md 第 4 節)或教官站下載的 .json 紀錄</span>`}
      </div>
      ${d && d.states.length > 0
        ? html`<div class="timeline">
            <span class="btn-row">
              <button @click=${() => this.step(-1)} title="上一筆">⏮</button>
              <button class="play" @click=${this.togglePlay}>${this.playing ? '暫停' : '播放'}</button>
              <button @click=${() => this.step(1)} title="下一筆">⏭</button>
              <select .value=${String(this.rate)} @change=${(e: Event) => (this.rate = Number((e.target as HTMLSelectElement).value))}>
                ${PLAY_RATES.map((r) => html`<option value=${r} ?selected=${r === this.rate}>×${r}</option>`)}
              </select>
            </span>
            <div class="track">
              ${this.renderMarks(d)}
              <input class="slider" type="range" min=${d.tStart} max=${d.tEnd} step="0.04" .value=${String(this.t)} @input=${this.onSlide} aria-label="時間軸" />
            </div>
            <span class="t">${shortTime(this.t)} / ${shortTime(d.tEnd)}</span>
          </div>`
        : html`<div class="hint">${d ? '紀錄沒有狀態資料' : ''}</div>`}
      ${d && d.states.length > 0
        ? html`<div class="main">
            <brm-conning-bar .state=${current} .config=${cfg} .issues=${[]} .noData=${false}></brm-conning-bar>
            <brm-track-plot .state=${current} .config=${cfg} .history=${this.history} .noData=${false} .overlay=${this.overlay} .targets=${current.targets} .alarmTargets=${cpaAlarmTargets(d.events, this.t)} ?target-table=${!this.compact}></brm-track-plot>
            <div class="side">
              <brm-event-stream .events=${d.events} @event-seek=${this.onEventSeek}></brm-event-stream>
              <div class="panel">
                <div class="panel-title"><span>輸入事件 INPUTS</span><span class="zh">${d.inputs.length} 筆 · 點選跳至</span></div>
                <div class="list" aria-label="輸入事件">
                  ${d.inputs.length === 0 ? html`<div class="hint">紀錄中沒有輸入事件</div>` : nothing}
                  ${d.inputs.map(
                    (i) => html`<div class="item ${isCurrent(i.t, this.t, d) ? 'current' : ''}" @click=${() => (this.stopPlay(), this.seek(i.t))}>
                      <span class="time">${shortTime(i.t)}</span>
                      <span class="text">${i.command.type}${i.command.value !== undefined ? ` ${typeof i.command.value === 'string' ? i.command.value : JSON.stringify(i.command.value)}` : ''}${i.command.args ? ` ${JSON.stringify(i.command.args)}` : ''}</span>
                      ${i.ack ? html`<span class=${i.ack.ok ? 'ok' : 'bad'} title=${i.ack.detail ?? ''}>${i.ack.ok ? 'OK' : '拒絕'}</span>` : nothing}
                    </div>`,
                  )}
                </div>
              </div>
              <div class="panel">
                <div class="panel-title"><span>BRM 標記 MARKERS</span><span class="zh">${d.markers.length} 筆</span></div>
                <div class="list" aria-label="BRM 標記">
                  ${d.markers.length === 0 ? html`<div class="hint">${d.source === 'simcore-jsonl' ? 'SimCore 紀錄不含 BRM 標記(標記在教官站紀錄內)' : '紀錄中沒有標記'}</div>` : nothing}
                  ${d.markers.map(
                    (m) => html`<div class="item" @click=${() => (this.stopPlay(), this.seek(m.t))}>
                      <span class="time">${shortTime(m.t)}</span>
                      <span class="text"><b>${brmCategory(m.category).label}</b>${m.polarity ? (m.polarity === 'plus' ? ' ＋' : ' Δ') : ''} ${m.note}</span>
                    </div>`,
                  )}
                  ${d.operations.length > 0 ? html`<div class="hint">另有 ${d.operations.length} 筆學員操作(教官站推導)</div>` : nothing}
                </div>
              </div>
              <div class="panel summary">
                <div class="panel-title"><span>講評摘要 DEBRIEF</span><span class="zh">${d.summary ? `核心 summary${d.summary.final ? '(定稿)' : ''}` : '本地推導(紀錄無核心 summary)'}</span></div>
                <brm-debrief-summary .summary=${this.summary} .coreSummary=${d.summary}></brm-debrief-summary>
                ${d.errors.length > 0 ? html`<div class="errors">略過 ${d.errors.length} 行:${d.errors.slice(0, 5).join(';')}${d.errors.length > 5 ? '…' : ''}</div>` : nothing}
                ${Object.keys(d.unknownKinds).length > 0 ? html`<div class="hint">未知的行種類:${Object.entries(d.unknownKinds).map(([k, n]) => `${k} ×${n}`).join('、')}</div>` : nothing}
              </div>
            </div>
          </div>`
        : html`<div class="empty">載入紀錄檔後,以時間軸拖曳檢視任一時刻的航跡與儀器</div>`}
    `;
  }
}

/** 倒帶標記的懸停文字:倒帶 tick(由 t / dt 推得)、來源時刻與丟棄的舊分支筆數。 */
export function rewindTitle(r: ReplayRewind, dt: number): string {
  const tick = Math.round(r.t / (dt > 0 ? dt : 0.02));
  return `倒帶至 ${shortTime(r.t)}(tick ${tick})← 自 ${shortTime(r.from)};丟棄 ${r.droppedStates} 筆狀態、${r.droppedEvents} 筆事件`;
}

/** 該輸入事件是否為目前時刻之前最後一筆(清單高亮) */
function isCurrent(t: number, now: number, d: ReplayData): boolean {
  let last: number | null = null;
  for (const i of d.inputs) if (i.t <= now) last = i.t;
  return last !== null && last === t;
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-replay-view': ReplayView;
  }
}
