// <brm-app>:整頁版面(上排 conning 條、中間鳥瞰航跡圖、下排操船台)、資料來源(WebSocket 或示範模式)、
// 主題切換(日/黃昏/夜)與指令路由。規劃書第 7 章第一版儀器;教官控制暫放同頁(第 9.1 節)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import type { OwnShipState, ShipId } from './types/state.js';
import type { SimCommand } from './types/command.js';
import type { ConnectionStatus, SimSource } from './net/sim-source.js';
import { DEFAULT_WS_URL, WsClient } from './net/ws-client.js';
import { DemoSource } from './demo/demo-source.js';
import { defaultState } from './lib/state-parser.js';
import { getShipConfig, isShipId, type ShipDisplayConfig } from './lib/ship-config.js';
import { TrackHistory } from './lib/track-history.js';
import { COMMAND_EVENT, tileStyles, type CommandEvent } from './components/shared.js';
import type { AutopilotChangedDetail, AutopilotPanel } from './components/autopilot-panel.js';
import './components/conning-bar.js';
import './components/track-plot.js';
import './components/helm-wheel.js';
import './components/telegraph-control.js';
import './components/thruster-lever.js';
import './components/autopilot-panel.js';
import './components/instructor-controls.js';

export type Theme = 'day' | 'dusk' | 'night';
export type SourceMode = 'live' | 'demo';

const THEMES: ReadonlyArray<{ id: Theme; label: string }> = [
  { id: 'day', label: '日' },
  { id: 'dusk', label: '黃昏' },
  { id: 'night', label: '夜' },
];
const THEME_KEY = 'simosa-brm.instruments.theme';
/** 連線中但超過此時間沒有狀態 → 顯示資料逾時 */
const STALE_MS = 2000;

const STATUS_LABEL: Record<ConnectionStatus, string> = {
  connected: '已連線',
  connecting: '連線中',
  disconnected: '未連線',
  demo: '示範模式',
};

function readTheme(): Theme {
  try {
    const v = localStorage.getItem(THEME_KEY);
    if (v === 'day' || v === 'dusk' || v === 'night') return v;
  } catch {
    /* 私密視窗等情況忽略 */
  }
  return 'dusk';
}

@customElement('brm-app')
export class BrmApp extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        display: grid;
        height: 100%;
        grid-template-rows: auto minmax(150px, 20vh) minmax(0, 1fr) minmax(210px, 27vh);
        gap: 6px;
        padding: 6px;
        background: var(--ob-bg-app);
        filter: brightness(var(--ob-dim));
      }
      header {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 4px 8px;
        background: var(--ob-bg-container);
        border: 1px solid var(--ob-border);
        border-radius: 6px;
        font-size: 12px;
        min-height: 34px;
        flex-wrap: wrap;
      }
      header .title {
        font-weight: 700;
        font-size: 13px;
      }
      header .ship {
        color: var(--ob-text-secondary);
      }
      header .spacer {
        flex: 1;
      }
      header .notice {
        color: var(--ob-text-muted);
        border: 1px solid var(--ob-border);
        border-radius: 4px;
        padding: 1px 6px;
      }
      .pill {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        border-radius: 12px;
        padding: 2px 10px;
        border: 1px solid var(--ob-border-strong);
        font-weight: 600;
      }
      .pill::before {
        content: '';
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: var(--ob-text-muted);
      }
      .pill.connected::before,
      .pill.demo::before {
        background: var(--ob-ok);
      }
      .pill.connecting::before {
        background: var(--ob-caution);
      }
      .pill.disconnected {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
        border-color: var(--ob-alarm);
      }
      .pill.disconnected::before {
        background: var(--ob-alarm-text);
      }
      .pill.stale {
        background: var(--ob-warning);
        color: #1a1000;
        border-color: var(--ob-warning);
      }
      .alarms {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
      }
      .alarms .alarm {
        font-weight: 700;
      }
      .controls {
        display: grid;
        grid-template-columns: 2.2fr 1.3fr 1.3fr 1.5fr 1fr;
        gap: 6px;
        min-height: 0;
      }
      .controls > * {
        background: var(--ob-bg-surface);
        border: 1px solid var(--ob-border);
        border-radius: 6px;
        padding: 6px 8px;
        min-height: 0;
        overflow: hidden;
      }
      .seg {
        display: inline-flex;
        border: 1px solid var(--ob-border-strong);
        border-radius: 4px;
        overflow: hidden;
      }
      .seg button {
        border: none;
        border-radius: 0;
        min-height: 24px;
        padding: 2px 8px;
      }
      .seg button + button {
        border-left: 1px solid var(--ob-border-strong);
      }
      select {
        min-height: 24px;
        padding: 1px 4px;
      }
      /* 1366×768 等較小螢幕:conning 條折成兩列,需要較高的列;下排操船台略縮 */
      @media (max-width: 1500px) {
        :host {
          grid-template-rows: auto minmax(220px, 32vh) minmax(0, 1fr) minmax(200px, 27vh);
          gap: 4px;
          padding: 4px;
        }
      }
    `,
  ];

  @state() private mode: SourceMode = 'live';
  @state() private status: ConnectionStatus = 'disconnected';
  @state() private statusDetail = '';
  @state() private shipState: OwnShipState = defaultState('FSB1');
  @state() private issues: string[] = [];
  @state() private theme: Theme = readTheme();
  @state() private stale = false;
  @state() private autopilot: AutopilotChangedDetail = { enabled: false, heading: 0, rotLimit: 15 };
  @state() private demoShip: ShipId = 'FSB1';
  @state() private wsUrl = DEFAULT_WS_URL;
  @state() private hasReceived = false;

  @query('brm-autopilot') private autopilotPanel?: AutopilotPanel;

  private source: SimSource | null = null;
  private unsubscribe: Array<() => void> = [];
  private readonly history = new TrackHistory(1, 3600);
  private lastStateAt = 0;
  private staleTimer: ReturnType<typeof setInterval> | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    const params = new URLSearchParams(location.search);
    const ws = params.get('ws');
    if (ws) this.wsUrl = ws;
    const theme = params.get('theme');
    if (theme === 'day' || theme === 'dusk' || theme === 'night') this.theme = theme;
    const ship = params.get('ship');
    if (isShipId(ship)) this.demoShip = ship;
    this.mode = params.get('demo') === '1' ? 'demo' : 'live';
    this.applyTheme();
    this.startSource();
    this.staleTimer = setInterval(() => {
      const isStale = this.status === 'connected' && this.hasReceived && performance.now() - this.lastStateAt > STALE_MS;
      if (isStale !== this.stale) this.stale = isStale;
    }, 500);
    this.addEventListener(COMMAND_EVENT, this.onCommand as EventListener);
    this.addEventListener('autopilot-changed', this.onAutopilotChanged as EventListener);
    this.addEventListener('sim-reset', this.onReset);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.stopSource();
    if (this.staleTimer) clearInterval(this.staleTimer);
    this.removeEventListener(COMMAND_EVENT, this.onCommand as EventListener);
    this.removeEventListener('autopilot-changed', this.onAutopilotChanged as EventListener);
    this.removeEventListener('sim-reset', this.onReset);
  }

  private get config(): ShipDisplayConfig {
    return getShipConfig(this.shipState.shipId);
  }

  // --- 資料來源 ---
  private startSource(): void {
    this.stopSource();
    this.history.clear();
    this.hasReceived = false;
    const source: SimSource =
      this.mode === 'demo'
        ? new DemoSource({ shipId: this.demoShip, initialHeading: 45, initialSpeed_kn: 8 })
        : new WsClient({ url: this.wsUrl });
    this.source = source;
    this.unsubscribe.push(
      source.onState(({ state, issues }) => {
        this.lastStateAt = performance.now();
        this.hasReceived = true;
        this.shipState = state;
        this.issues = issues;
        this.history.push(state.pos.x, state.pos.y, state.t);
      }),
      source.onStatus((status, detail) => {
        this.status = status;
        this.statusDetail = detail ?? '';
      }),
    );
    source.start();
  }

  private stopSource(): void {
    for (const u of this.unsubscribe) u();
    this.unsubscribe = [];
    this.source?.stop();
    this.source = null;
  }

  private setMode(mode: SourceMode): void {
    if (mode === this.mode) return;
    this.mode = mode;
    this.autopilotPanel?.disable();
    this.startSource();
  }

  private setDemoShip(ship: ShipId): void {
    this.demoShip = ship;
    if (this.mode === 'demo') {
      this.autopilotPanel?.disable();
      this.startSource();
    }
  }

  // --- 事件 ---
  private onCommand = (ev: CommandEvent): void => {
    const cmd: SimCommand = ev.detail;
    this.source?.send(cmd);
    if (cmd.type === 'reset') this.history.clear();
    // 手動舵令會解除自動舵(與核心行為一致)
    if (cmd.type === 'rudder' && this.autopilot.enabled) this.autopilotPanel?.disable();
  };

  private onAutopilotChanged = (ev: CustomEvent<AutopilotChangedDetail>): void => {
    this.autopilot = ev.detail;
  };

  private onReset = (): void => {
    this.history.clear();
    this.autopilotPanel?.disable();
  };

  // --- 主題 ---
  private setTheme(theme: Theme): void {
    this.theme = theme;
    this.applyTheme();
    try {
      localStorage.setItem(THEME_KEY, theme);
    } catch {
      /* 忽略 */
    }
  }

  private applyTheme(): void {
    document.documentElement.dataset['theme'] = this.theme;
  }

  private toggleFullscreen(): void {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void document.documentElement.requestFullscreen?.();
  }

  override render() {
    const s = this.shipState;
    const cfg = this.config;
    const flags = s.flags ?? {};
    const faults = s.faults ?? [];
    const pillClass = this.stale ? 'stale' : this.status;
    const pillText = this.stale ? '資料逾時' : STATUS_LABEL[this.status];
    return html`
      <header>
        <span class="title">Simosa BRM 駕駛台儀器</span>
        <span class="ship">${cfg.name}(${cfg.id})</span>
        <span class="pill ${pillClass}" title=${this.statusDetail}>${pillText}</span>
        ${this.mode === 'live' ? html`<span class="muted">${this.wsUrl}</span>` : nothing}
        <div class="alarms">
          ${flags.aground ? html`<span class="alarm">擱淺</span>` : nothing}
          ${flags.collision ? html`<span class="alarm">碰撞</span>` : nothing}
          ${faults.map((f) => html`<span class="alarm">故障:${f}</span>`)}
          ${this.issues.length > 0 ? html`<span class="warning" title=${this.issues.join(', ')}>資料可疑 ${this.issues.length}</span>` : nothing}
        </div>
        <span class="spacer"></span>
        ${this.mode === 'demo'
          ? html`<label class="label">船 <select .value=${this.demoShip} @change=${(e: Event) => this.setDemoShip((e.target as HTMLSelectElement).value as ShipId)}>
                <option value="FSB1">FSB1 中塑油品壹號</option>
                <option value="FSB2">FSB2 中塑油品貳號</option>
              </select></label>`
          : nothing}
        <span class="seg" role="group" aria-label="資料來源">
          <button class=${this.mode === 'live' ? 'active' : ''} @click=${() => this.setMode('live')}>即時連線</button>
          <button class=${this.mode === 'demo' ? 'active' : ''} @click=${() => this.setMode('demo')}>示範模式</button>
        </span>
        <span class="seg" role="group" aria-label="主題">
          ${THEMES.map((t) => html`<button class=${this.theme === t.id ? 'active' : ''} @click=${() => this.setTheme(t.id)}>${t.label}</button>`)}
        </span>
        <button title="全螢幕" @click=${this.toggleFullscreen}>全螢幕</button>
        <span class="notice">訓練模擬器,非航行設備</span>
      </header>

      <brm-conning-bar
        .state=${s}
        .config=${cfg}
        .issues=${this.issues}
        .noData=${!this.hasReceived}
        .setHeading=${this.autopilot.enabled ? this.autopilot.heading : undefined}
      ></brm-conning-bar>

      <brm-track-plot .state=${s} .config=${cfg} .history=${this.history} .noData=${!this.hasReceived}></brm-track-plot>

      <div class="controls">
        <brm-helm
          .order=${s.rudderOrder}
          .rudder=${s.rudder}
          .max=${cfg.rudderMax_deg}
          .normalMax=${cfg.rudderNormalMax_deg}
          .rate=${cfg.rudderRate_degPerS}
          .autopilot=${this.autopilot.enabled}
        ></brm-helm>
        <brm-telegraph .steps=${cfg.telegraph} .position=${s.telegraph}></brm-telegraph>
        <brm-thruster-lever .order=${s.thruster?.order ?? 0} .actual=${s.thruster?.actual ?? 0}></brm-thruster-lever>
        <brm-autopilot .heading=${s.heading}></brm-autopilot>
        <brm-instructor .frozen=${flags.frozen ?? false}></brm-instructor>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-app': BrmApp;
  }
}
