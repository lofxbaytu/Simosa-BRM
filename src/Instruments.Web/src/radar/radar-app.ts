// <brm-radar-app>:雷達 PPI 顯示頁(規劃書第 7.2 節雷達列、MSC.191(79)/IEC 62288 顯示概念)。
// 資料來源:雷達服務 WebSocket(Sensors.Radar,ws://<host>:8766/radar/x|s;?ws= 覆寫、?radar=x|s 選台)或示範模式(?demo=1)。
// 版面:左 PPI、右兩欄面板(雷達控制/顯示/EBL-VRM-PI;ARPA 表/目標資料/試操船與保護區/事件)。
// 快捷鍵:+/− 量程、A 擷取工具、Esc 回選取工具、Delete 取消選取目標。

import { LitElement, css, html, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import { RadarStore } from './radar-store.js';
import { RadarClient, parseRadarId, resolveRadarUrl, type RadarConnStatus, type RadarSource } from './radar-client.js';
import { DemoRadarSource } from './demo-radar.js';
import { formatBearing, formatRangeNm } from './geometry.js';
import { formatSimTime } from './radar-arpa-panels.js';
import { THEMES, applyTheme, readTheme, type Theme } from '../lib/theme.js';
import type { RadarId } from './protocol.js';
import type { RadarPpi } from './radar-ppi.js';
import './radar-ppi.js';
import './radar-controls.js';
import './radar-arpa-panels.js';

const CONN_LABEL: Record<RadarConnStatus, string> = {
  connected: '已連線',
  connecting: '連線中',
  disconnected: '未連線',
  demo: '示範模式',
};

export interface RadarPageOptions {
  radar: RadarId;
  demo: boolean;
  url: string;
  fps: number;
  perf: boolean;
}

/** 由網址參數決定頁面選項 */
export function pageOptions(search: string, host: string | undefined): RadarPageOptions {
  const p = new URLSearchParams(search);
  const radar = parseRadarId(p.get('radar'));
  const demo = p.get('demo') === '1' || p.get('demo') === 'true';
  const fpsRaw = Number(p.get('fps'));
  return {
    radar,
    demo,
    url: demo ? 'demo://radar' : resolveRadarUrl(p, host, radar),
    fps: Number.isFinite(fpsRaw) && fpsRaw >= 5 && fpsRaw <= 120 ? fpsRaw : 30,
    perf: p.get('perf') === '1',
  };
}

@customElement('brm-radar-app')
export class BrmRadarApp extends LitElement {
  static override styles = css`
    :host {
      display: grid;
      height: 100%;
      box-sizing: border-box;
      grid-template-rows: auto minmax(0, 1fr);
      gap: 6px;
      padding: 6px;
      background: var(--ob-bg-app);
      color: var(--ob-text);
      filter: brightness(var(--ob-dim));
      font-size: 12px;
    }
    header {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
      background: var(--ob-bg-surface);
      border: 1px solid var(--ob-border);
      border-radius: 6px;
      padding: 4px 8px;
      min-height: 34px;
    }
    header .title {
      font-weight: 700;
      font-size: 14px;
    }
    .chip {
      border: 1px solid var(--ob-border-strong);
      border-radius: 10px;
      padding: 1px 8px;
      white-space: nowrap;
    }
    .chip.ok {
      border-color: var(--ob-ok);
      color: var(--ob-ok);
    }
    .chip.warn {
      border-color: var(--ob-warning);
      color: var(--ob-warning);
    }
    .chip.alarm {
      background: var(--ob-alarm);
      border-color: var(--ob-alarm);
      color: var(--ob-alarm-text);
      font-weight: 700;
    }
    .chip.alarm.blink {
      animation: blink 1s steps(1) infinite;
    }
    @keyframes blink {
      50% {
        opacity: 0.25;
      }
    }
    .kv {
      white-space: nowrap;
      font-variant-numeric: tabular-nums;
    }
    .kv b {
      font-weight: 600;
    }
    .spacer {
      flex: 1 1 auto;
    }
    button {
      font: inherit;
      color: var(--ob-text);
      background: var(--ob-bg-surface-raised);
      border: 1px solid var(--ob-border-strong);
      border-radius: 4px;
      padding: 2px 8px;
      min-height: 24px;
      cursor: pointer;
    }
    button.active {
      background: var(--ob-accent);
      color: var(--ob-text-on-accent);
      border-color: var(--ob-accent-strong);
    }
    main {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(290px, 330px) minmax(300px, 350px);
      gap: 6px;
      min-height: 0;
    }
    radar-ppi {
      background: var(--ob-bg-surface);
      border: 1px solid var(--ob-border);
      border-radius: 6px;
      min-height: 0;
    }
    .col {
      min-height: 0;
      overflow: auto;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .note {
      color: var(--ob-text-muted);
      font-size: 11px;
      padding: 0 2px;
    }
    @media (max-width: 1100px) {
      main {
        grid-template-columns: minmax(0, 1fr) minmax(280px, 320px);
      }
      .col.second {
        grid-column: 1 / -1;
      }
    }
  `;

  readonly store = new RadarStore();
  readonly options: RadarPageOptions;
  private source: RadarSource;
  @state() private theme: Theme = readTheme();
  @state() private tick = 0;
  @state() private cursorText = '';
  @query('radar-ppi') ppi!: RadarPpi;
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsub: (() => void) | null = null;

  constructor() {
    super();
    this.options = pageOptions(
      typeof location !== 'undefined' ? location.search : '',
      typeof location !== 'undefined' ? location.hostname : undefined,
    );
    this.source = this.options.demo
      ? new DemoRadarSource({ radar: this.options.radar })
      : new RadarClient({ url: this.options.url, radar: this.options.radar });
    this.store.attach(this.source);
  }

  override connectedCallback(): void {
    super.connectedCallback();
    applyTheme(this.theme, false);
    this.unsub = this.store.subscribe(() => this.requestUpdate());
    this.source.start();
    this.timer = setInterval(() => (this.tick = (this.tick + 1) % 1e6), 500);
    window.addEventListener('keydown', this.onKey);
    if (this.options.perf) (window as unknown as Record<string, unknown>)['__simosaRadar'] = this;
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.source.stop();
    this.unsub?.();
    if (this.timer) clearInterval(this.timer);
    window.removeEventListener('keydown', this.onKey);
  }

  private onKey = (e: KeyboardEvent): void => {
    const t = e.composedPath()[0];
    if (t instanceof HTMLInputElement || t instanceof HTMLSelectElement || t instanceof HTMLTextAreaElement) return;
    const s = this.store;
    switch (e.key) {
      case '+':
      case '=':
        s.stepDisplayRange(1);
        break;
      case '-':
      case '_':
        s.stepDisplayRange(-1);
        break;
      case 'a':
      case 'A':
        s.setSettings({ tool: s.settings.tool === 'acquire' ? 'select' : 'acquire' });
        break;
      case 'Escape':
        s.setSettings({ tool: 'select' });
        break;
      case 'Delete':
        if (s.selectedId !== null) s.send({ cmd: 'cancel', id: s.selectedId });
        break;
      default:
        return;
    }
    e.preventDefault();
  };

  private setTheme(t: Theme): void {
    this.theme = t;
    applyTheme(t);
  }

  private switchRadar(r: RadarId): void {
    if (r === this.options.radar) return;
    const p = new URLSearchParams(location.search);
    p.set('radar', r);
    location.search = p.toString();
  }

  private onCursor = (e: Event): void => {
    const d = (e as CustomEvent<{ bearing: number; range: number } | null>).detail;
    this.cursorText = d ? `${formatBearing(d.bearing)} T ${formatRangeNm(d.range)} NM` : '';
  };

  override render() {
    const s = this.store;
    const st = s.status;
    const own = s.tracks?.ownShip;
    const conn = s.conn;
    const pending = s.pendingAlarms().length;
    const dangerous = (s.tracks?.targets ?? []).filter((t) => t.state === 'tracking' && t.dangerous).length;
    const lost = (s.tracks?.targets ?? []).filter((t) => t.state === 'lost').length;
    const band = st?.band ?? (this.options.radar === 's' ? 'S' : 'X');
    const fps = this.options.perf && this.ppi ? this.ppi.measuredFps : 0;
    return html`
      <header>
        <span class="title">雷達 ${band} 頻${st ? ` ${st.frequency_GHz} GHz` : ''}</span>
        <span>
          <button class=${this.options.radar === 'x' ? 'active' : ''} @click=${() => this.switchRadar('x')}>X</button>
          <button class=${this.options.radar === 's' ? 'active' : ''} @click=${() => this.switchRadar('s')}>S</button>
        </span>
        <span class="chip ${conn === 'connected' || conn === 'demo' ? 'ok' : 'warn'}" title=${s.connDetail || this.options.url}>
          ${CONN_LABEL[conn]}
        </span>
        ${st
          ? html`<span class="chip ${st.failed ? 'alarm' : st.transmitting ? 'ok' : 'warn'}">
              ${st.failed ? '雷達故障' : st.transmitting ? 'TX 發射中' : 'STBY 待機'}
            </span>`
          : nothing}
        ${st?.degraded ? html`<span class="chip warn">性能劣化</span>` : nothing}
        ${!s.headingValid ? html`<span class="chip warn">航向無效(ARPA 停止、只能艏向上)</span>` : nothing}
        ${dangerous > 0 ? html`<span class="chip alarm ${pending > 0 ? 'blink' : ''}">危險目標 ${dangerous}</span>` : nothing}
        ${lost > 0 ? html`<span class="chip warn">失追 ${lost}</span>` : nothing}
        <span class="kv">HDG <b>${formatBearing(s.headingDeg)}</b></span>
        ${own ? html`<span class="kv">COG <b>${formatBearing(own.cog)}</b> SOG <b>${own.sog.toFixed(1)}</b> kn</span>` : nothing}
        <span class="kv">T <b>${formatSimTime(s.simTime)}</b></span>
        ${this.cursorText ? html`<span class="kv">游標 <b>${this.cursorText}</b></span>` : nothing}
        ${this.options.perf
          ? html`<span class="kv">fps <b>${fps.toFixed(0)}</b> 繪製 <b>${(this.ppi?.renderer?.stats.avgDrawMs ?? 0).toFixed(1)}</b> ms</span>`
          : nothing}
        <span class="spacer"></span>
        ${s.lastError ? html`<span class="chip warn">${s.lastError}</span>` : nothing}
        <span>
          ${THEMES.map(
            (t) => html`<button class=${this.theme === t.id ? 'active' : ''} @click=${() => this.setTheme(t.id)}>${t.label}</button>`,
          )}
        </span>
      </header>
      <main>
        <radar-ppi .store=${s} .fps=${this.options.fps} .continuous=${this.options.perf} .theme=${this.theme} @ppi-cursor=${this.onCursor}></radar-ppi>
        <div class="col">
          <radar-control-panel .store=${s}></radar-control-panel>
          <radar-display-panel .store=${s}></radar-display-panel>
          <radar-tools-panel .store=${s}></radar-tools-panel>
        </div>
        <div class="col second">
          <radar-arpa-table .store=${s}></radar-arpa-table>
          <radar-target-data .store=${s}></radar-target-data>
          <radar-trial-panel .store=${s}></radar-trial-panel>
          <radar-event-log .store=${s}></radar-event-log>
          ${this.options.demo ? html`<div class="note">示範模式:本地合成影像與假追蹤目標,不可用於訓練或驗證。</div>` : nothing}
        </div>
      </main>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-radar-app': BrmRadarApp;
  }
}
