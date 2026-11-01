// <brm-ecdis-app>:簡化 ECDIS 頁(ecdis.html;docs/design/ecdis-lite.md)。與儀器頁/教官站同一 WebSocket 狀態來源(ws://<主機>:8765,?ws= 覆寫)
// 或示範模式(?demo=1);唯讀,不送任何核心指令。版面:頂列(連線/情境/DR/航向/警報 chips、HDG/COG/SOG/STW、位置、水深、時間、游標、主題),
// 左海圖、右欄(顯示控制、航線監控、AIS 目標、警報)。快捷鍵:+/− 量程、N 北上、C 航向上、Home 置中、Esc 平移工具、A 全部確認。

import { LitElement, css, html, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import { EcdisStore } from '../../lib/ecdis/ecdis-store.js';
import { CHART_RANGES_NM } from '../../lib/ecdis/projection.js';
import { AREA_FILES, loadScenarioList } from '../../lib/scenario.js';
import { DEFAULT_WS_URL, WsClient } from '../../net/ws-client.js';
import { DemoSource } from '../../demo/demo-source.js';
import type { ConnectionStatus, SimSource } from '../../net/sim-source.js';
import { THEMES, applyTheme, isTheme, readTheme, type Theme } from '../../lib/theme.js';
import { isShipId } from '../../lib/ship-config.js';
import { formatHeading, formatKnots, formatLatitude, formatLongitude, formatSimTime } from '../../lib/format.js';
import { formatBearing, formatRangeNm } from '../../radar/geometry.js';
import type { ShipId } from '../../types/state.js';
import type { EcdisChart } from './chart-canvas.js';
import './chart-canvas.js';
import './toolbar.js';
import './route-panel.js';
import './alarm-panel.js';
import './target-panel.js';

const CONN_LABEL: Record<ConnectionStatus, string> = {
  connected: '已連線',
  connecting: '連線中',
  disconnected: '未連線',
  demo: '示範模式',
};

/** 連線中但超過此時間沒有狀態 → 顯示資料逾時 */
const STALE_MS = 2000;

export interface EcdisPageOptions {
  demo: boolean;
  url: string;
  theme: Theme | null;
  demoShip: ShipId;
  rangeNm: number | null;
}

/** 由網址參數決定頁面選項(?ws=、?demo=1、?theme=、?ship=、?range=)。 */
export function pageOptions(search: string, host: string | undefined): EcdisPageOptions {
  const p = new URLSearchParams(search);
  const demo = p.get('demo') === '1' || p.get('demo') === 'true';
  const ws = p.get('ws');
  const url = ws || (host && host !== 'localhost' && host !== '127.0.0.1' && host !== '' ? `ws://${host}:8765` : DEFAULT_WS_URL);
  const theme = p.get('theme');
  const ship = p.get('ship');
  const range = Number(p.get('range'));
  return {
    demo,
    url: demo ? 'demo://sim' : url,
    theme: isTheme(theme) ? theme : null,
    demoShip: isShipId(ship) ? ship : 'FSB2',
    rangeNm: CHART_RANGES_NM.includes(range) ? range : null,
  };
}

@customElement('brm-ecdis-app')
export class BrmEcdisApp extends LitElement {
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
      gap: 8px;
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
    .kv.dim {
      opacity: 0.5;
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
      grid-template-columns: minmax(0, 1fr) minmax(320px, 380px);
      gap: 6px;
      min-height: 0;
    }
    ecdis-chart {
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
    @media (max-width: 1000px) {
      main {
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: minmax(0, 1fr) minmax(0, 0.8fr);
      }
    }
  `;

  readonly store: EcdisStore;
  readonly options: EcdisPageOptions;
  private source: SimSource;
  @state() private theme: Theme = readTheme();
  @state() private tick = 0;
  @state() private stale = false;
  @query('ecdis-chart') chart!: EcdisChart;
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsub: (() => void) | null = null;
  private lastStateAt = 0;

  constructor() {
    super();
    this.options = pageOptions(typeof location !== 'undefined' ? location.search : '', typeof location !== 'undefined' ? location.hostname : undefined);
    if (this.options.theme) this.theme = this.options.theme;
    this.store = new EcdisStore({ scenarios: loadScenarioList, areaFiles: AREA_FILES });
    if (this.options.rangeNm) this.store.setSettings({ rangeNm: this.options.rangeNm });
    this.source = this.options.demo ? new DemoSource({ shipId: this.options.demoShip, initialHeading: 45, initialSpeed_kn: 8 }) : new WsClient({ url: this.options.url });
    this.store.attach(this.source);
  }

  override connectedCallback(): void {
    super.connectedCallback();
    applyTheme(this.theme, false);
    this.unsub = this.store.subscribe(() => {
      this.lastStateAt = performance.now();
      this.requestUpdate();
    });
    this.source.start();
    this.timer = setInterval(() => {
      this.tick = (this.tick + 1) % 1e6;
      const isStale = this.store.conn === 'connected' && this.store.state !== null && performance.now() - this.lastStateAt > STALE_MS;
      if (isStale !== this.stale) this.stale = isStale;
    }, 500);
    window.addEventListener('keydown', this.onKey);
    window.addEventListener('pointerdown', this.unlock, { once: true });
    window.addEventListener('keydown', this.unlock, { once: true });
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.source.stop();
    this.store.dispose();
    this.unsub?.();
    if (this.timer) clearInterval(this.timer);
    window.removeEventListener('keydown', this.onKey);
    window.removeEventListener('pointerdown', this.unlock);
    window.removeEventListener('keydown', this.unlock);
  }

  private unlock = (): void => {
    this.store.sounder.unlock();
  };

  private onKey = (e: KeyboardEvent): void => {
    const t = e.composedPath()[0];
    if (t instanceof HTMLInputElement || t instanceof HTMLSelectElement || t instanceof HTMLTextAreaElement) return;
    const s = this.store;
    switch (e.key) {
      case '+':
      case '=':
        s.stepRange(-1);
        break;
      case '-':
      case '_':
        s.stepRange(1);
        break;
      case 'n':
      case 'N':
        s.setSettings({ orientation: 'north-up' });
        break;
      case 'c':
      case 'C':
        if (s.headingValid) s.setSettings({ orientation: 'course-up' });
        break;
      case 'Home':
        s.recentre();
        break;
      case 'Escape':
        s.setSettings({ tool: 'pan' });
        s.selectTarget(null);
        break;
      case 'a':
      case 'A':
        s.acknowledgeAll();
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

  override render() {
    const s = this.store;
    const st = s.state;
    const conn = s.conn;
    const unack = s.alarms.unacknowledged;
    const dangerous = s.targets.filter((t) => t.dangerous).length;
    return html`
      <header>
        <span class="title">ECDIS(簡化)</span>
        <span class="chip ${conn === 'connected' || conn === 'demo' ? 'ok' : 'warn'}" title=${s.connDetail || this.options.url}>${CONN_LABEL[conn]}</span>
        ${this.stale ? html`<span class="chip warn">資料逾時</span>` : nothing}
        ${s.chart.scenarioId
          ? html`<span class="chip" title=${s.chart.scenarioName}>${s.chart.scenarioId}${s.chart.route ? ' · 航線' : ''}</span>`
          : st
            ? html`<span class="chip warn">無情境航線</span>`
            : nothing}
        ${st && s.chart.originSource === 'state' ? html`<span class="chip warn" title="情境不明:原點由狀態經緯度反推,示意幾何位置僅供參考">原點推估</span>` : nothing}
        ${st && !s.positionValid ? html`<span class="chip alarm blink">DR 位置凍結(GNSS 無效)</span>` : nothing}
        ${st && !s.headingValid ? html`<span class="chip warn">航向無效(北上)</span>` : nothing}
        ${st?.flags?.frozen ? html`<span class="chip warn">凍結</span>` : nothing}
        ${st?.flags?.aground ? html`<span class="chip alarm">擱淺</span>` : nothing}
        ${dangerous > 0 ? html`<span class="chip alarm">危險目標 ${dangerous}</span>` : nothing}
        ${unack > 0 ? html`<span class="chip alarm blink">警報 ${unack}</span>` : nothing}
        ${st
          ? html`<span class="kv">HDG <b>${s.headingValid ? formatHeading(st.heading) : '---'}</b></span>
              <span class="kv ${s.positionValid ? '' : 'dim'}">COG <b>${formatHeading(st.cog)}</b> SOG <b>${formatKnots(st.sog)}</b></span>
              <span class="kv ${s.stwValid ? '' : 'dim'}">STW <b>${formatKnots(st.stw)}</b></span>
              <span class="kv ${s.positionValid ? '' : 'dim'}" title="指示位置(GNSS)">${formatLatitude(st.pos.lat)} ${formatLongitude(st.pos.lon)}</span>
              <span class="kv" title="自船位置有效水深(圖載 + 潮高)/ 測深儀龍骨下">深 <b>${s.ownDepth.effective_m.toFixed(1)}</b> m · UKC <b>${st.depthBelowKeel.toFixed(1)}</b> m${st.tideHeight_m !== undefined ? ` · 潮 ${st.tideHeight_m >= 0 ? '+' : ''}${st.tideHeight_m.toFixed(2)}` : ''}</span>
              <span class="kv">T <b>${formatSimTime(st.t)}</b>${st.utc ? html` <span class="note">${st.utc.slice(11, 19)}Z</span>` : nothing}</span>`
          : nothing}
        ${s.cursor ? html`<span class="kv">游標 <b>${formatBearing(s.cursor.bearing_deg)} T ${formatRangeNm(s.cursor.distance_nm)} NM</b>${s.cursor.lat !== undefined ? ` ${formatLatitude(s.cursor.lat)} ${formatLongitude(s.cursor.lon)}` : ''}</span>` : nothing}
        <span class="spacer"></span>
        <span>${THEMES.map((t) => html`<button class=${this.theme === t.id ? 'active' : ''} @click=${() => this.setTheme(t.id)}>${t.label}</button>`)}</span>
      </header>
      <main>
        <ecdis-chart .store=${s} .theme=${this.theme}></ecdis-chart>
        <div class="col">
          <ecdis-toolbar .store=${s}></ecdis-toolbar>
          <ecdis-route-panel .store=${s}></ecdis-route-panel>
          <ecdis-target-panel .store=${s}></ecdis-target-panel>
          <ecdis-alarm-panel .store=${s}></ecdis-alarm-panel>
          <div class="note">非型式認可、非 S-57/S-63 ENC:底圖為 data/areas 示意幾何;唯讀顯示,不送核心指令。${this.options.demo ? ' 示範模式:內建運動學,非船模。' : ''}</div>
        </div>
      </main>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-ecdis-app': BrmEcdisApp;
  }
}
