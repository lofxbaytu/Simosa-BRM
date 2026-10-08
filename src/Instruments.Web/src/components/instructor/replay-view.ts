// 重播檢視(講評站雛型,規劃書第 9.2 節「重播」與「航跡圖」):以檔案選擇器載入 SimCore 的 JSON Lines 紀錄
// 或教官站的 JSON 紀錄,時間軸拖曳檢視任一時刻的狀態(鳥瞰航跡圖 + conning 儀器摘要),列出輸入事件與
// BRM 標記(若紀錄含),整段的自動講評摘要;不需核心連線。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import { parseReplay, stateIndexAt, trackPoints, type ReplayData } from '../../lib/record-replay.js';
import { TrackHistory, type TrackPoint } from '../../lib/track-history.js';
import { getShipConfig } from '../../lib/ship-config.js';
import { summarize, type DebriefSummary } from '../../lib/debrief.js';
import { brmCategory } from '../../lib/brm-markers.js';
import { defaultState } from '../../lib/state-parser.js';
import '../conning-bar.js';
import '../track-plot.js';
import './debrief-summary.js';

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
        grid-template-rows: minmax(0, 1fr) minmax(0, 1fr) auto;
        gap: 6px;
        min-height: 0;
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
    this.stopPlay();
    this.seek(data.tStart);
    return data;
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
              ${d.header.shipId ?? '?'}${d.header.loading ? `/${d.header.loading}` : ''} · ${d.states.length} 筆狀態 · ${d.inputs.length} 筆輸入 · ${d.markers.length} 筆標記
              ${d.header.dynamics ? ` · ${d.header.dynamics}` : ''}${d.footer?.stateHash ? ` · 雜湊 ${d.footer.stateHash.slice(0, 8)}…` : ''}
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
            <input class="slider" type="range" min=${d.tStart} max=${d.tEnd} step="0.04" .value=${String(this.t)} @input=${this.onSlide} aria-label="時間軸" />
            <span class="t">${shortTime(this.t)} / ${shortTime(d.tEnd)}</span>
          </div>`
        : html`<div class="hint">${d ? '紀錄沒有狀態資料' : ''}</div>`}
      ${d && d.states.length > 0
        ? html`<div class="main">
            <brm-conning-bar .state=${current} .config=${cfg} .issues=${[]} .noData=${false}></brm-conning-bar>
            <brm-track-plot .state=${current} .config=${cfg} .history=${this.history} .noData=${false}></brm-track-plot>
            <div class="side">
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
                <div class="panel-title"><span>自動講評摘要 DEBRIEF</span></div>
                <brm-debrief-summary .summary=${this.summary}></brm-debrief-summary>
                ${d.errors.length > 0 ? html`<div class="errors">略過 ${d.errors.length} 行:${d.errors.slice(0, 5).join(';')}${d.errors.length > 5 ? '…' : ''}</div>` : nothing}
              </div>
            </div>
          </div>`
        : html`<div class="empty">載入紀錄檔後,以時間軸拖曳檢視任一時刻的航跡與儀器</div>`}
    `;
  }
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
