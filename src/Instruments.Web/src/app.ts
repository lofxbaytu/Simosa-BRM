// <brm-app>:整頁版面(上排 conning 條、中間鳥瞰航跡圖、下排操船台)、資料來源(WebSocket 或示範模式)、
// 主題切換(日/黃昏/夜)與指令路由。規劃書第 7 章第一版儀器;教官控制暫放同頁(第 9.1 節)。
// 右側警報欄另有精簡錨/拖船讀數與接近泊位時的艏/艉橫向速度(<brm-external-readout>;state 有 tugs/anchors/berth 時才顯示),
// 泊位幾何依 state.scenario.id 由建置時匯入的情境清單取得(示範模式用示範泊位),航跡圖同時畫碼頭線、碰墊、纜繩、拖船與錨鏈。

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
import type { SimEvent } from './types/event.js';
import { eventFromFrame, eventLabel } from './lib/sim-event.js';
import { AlertSounder } from './lib/alert-sound.js';
import { alertCounts, parseStation } from './lib/alerts.js';
import type { AlertStation } from './types/alerts.js';
import { ALERT_SOUND_TOGGLE_EVENT } from './components/alert-panel.js';
import './components/conning-bar.js';
import './components/track-plot.js';
import './components/helm-wheel.js';
import './components/telegraph-control.js';
import './components/thruster-lever.js';
import './components/autopilot-panel.js';
import './components/instructor-controls.js';
import './components/alert-panel.js';
import './components/bnwas-indicator.js';
import './components/external-readout.js';
import type { ScenarioSummary } from './lib/scenario.js';
import { berthGeometry, type BerthGeometry } from './lib/external-forces.js';
import type { PlotOverlay } from './lib/plot-overlay.js';

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
/** 學員可見的訊息/語音橫幅顯示秒數(設計文件第 6 節 W11;visible: true 的 message/voiceScript 事件) */
const BANNER_MS = 20000;

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
      /* 中間列:鳥瞰航跡圖 + 右側警報欄(BNWAS 指示、中央警報面板) */
      .middle {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 360px;
        gap: 6px;
        min-height: 0;
      }
      /* 航跡圖左上角浮貼錨/拖船/靠泊讀數(不佔警報欄高度,中央警報面板維持原大小) */
      .plot-wrap {
        position: relative;
        min-height: 0;
        min-width: 0;
      }
      .plot-wrap brm-external-readout {
        position: absolute;
        left: 8px;
        top: 24px;
        max-width: min(380px, 60%);
        pointer-events: none;
      }
      .alert-col {
        display: grid;
        grid-template-rows: auto minmax(0, 1fr);
        gap: 6px;
        min-height: 0;
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
      .banner {
        position: fixed;
        left: 50%;
        top: 48px;
        transform: translateX(-50%);
        z-index: 10;
        font-size: 18px;
        font-weight: 600;
        padding: 8px 20px;
        border-radius: 8px;
        background: var(--ob-accent-soft);
        border: 1px solid var(--ob-accent);
        color: var(--ob-text);
        box-shadow: var(--ob-shadow);
        max-width: 70vw;
        cursor: pointer;
      }
      .banner.warning {
        border-color: var(--ob-warning);
      }
      .banner.alarm {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
        border-color: var(--ob-alarm);
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
      header .alert-badge {
        font-weight: 700;
        border-radius: 4px;
        padding: 0 6px;
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      header .alert-badge.warning {
        background: var(--ob-warning);
        color: #1a1000;
      }
      /* 1366×768 等較小螢幕:conning 條折成兩列,需要較高的列;下排操船台略縮 */
      @media (max-width: 1500px) {
        :host {
          grid-template-rows: auto minmax(220px, 32vh) minmax(0, 1fr) minmax(200px, 27vh);
          gap: 4px;
          padding: 4px;
        }
        .middle {
          grid-template-columns: minmax(0, 1fr) 344px;
          gap: 4px;
        }
        .alert-col {
          gap: 4px;
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
  @state() private banner: { text: string; severity: string } | null = null;
  /** 本頁警報聲響設定(預設開;AlertSounder 存 localStorage) */
  @state() private soundOn = true;
  /** 本操船台所在的工作站(?station=bridge|portWing|stbdWing|ecr;警報面板的確認/轉移依此;sensors-alerts.md 第 3.6 節) */
  @state() private station: AlertStation = 'bridge';
  private readonly sounder = new AlertSounder();
  /** 上一筆核心狀態的 autopilot.enabled(偵測核心脫開自動舵:true → false) */
  private coreAutopilotEnabled: boolean | undefined;
  private bannerTimer: ReturnType<typeof setTimeout> | null = null;

  @query('brm-autopilot') private autopilotPanel?: AutopilotPanel;

  private source: SimSource | null = null;
  /** 建置時匯入的情境(依 state.scenario.id 取泊位/拖船定義;只在有外力欄位時才解析) */
  private scenarioCache: ScenarioSummary[] | null = null;
  private scenarioLoading = false;
  private overlayKey = '';
  private overlayCached: PlotOverlay | null = null;
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
    this.station = parseStation(params.get('station'));
    this.applyTheme();
    this.startSource();
    this.staleTimer = setInterval(() => {
      const isStale = this.status === 'connected' && this.hasReceived && performance.now() - this.lastStateAt > STALE_MS;
      if (isStale !== this.stale) this.stale = isStale;
      // 資料逾時:最後一筆狀態的警報已不可信(靜音/確認也無從生效)→ 停止聲響;收到新狀態時由 onState 恢復
      if (isStale) this.sounder.update(undefined);
    }, 500);
    this.addEventListener(COMMAND_EVENT, this.onCommand as EventListener);
    this.addEventListener('autopilot-changed', this.onAutopilotChanged as EventListener);
    this.addEventListener('sim-reset', this.onReset);
    this.addEventListener(ALERT_SOUND_TOGGLE_EVENT, this.onSoundToggle as EventListener);
    this.soundOn = !this.sounder.muted;
    // 瀏覽器要求使用者互動後才可發聲:第一次點擊/按鍵時建立 AudioContext
    window.addEventListener('pointerdown', this.unlockSound, true);
    window.addEventListener('keydown', this.unlockSound, true);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.stopSource();
    if (this.staleTimer) clearInterval(this.staleTimer);
    if (this.bannerTimer) clearTimeout(this.bannerTimer);
    this.removeEventListener(COMMAND_EVENT, this.onCommand as EventListener);
    this.removeEventListener('autopilot-changed', this.onAutopilotChanged as EventListener);
    this.removeEventListener('sim-reset', this.onReset);
    this.removeEventListener(ALERT_SOUND_TOGGLE_EVENT, this.onSoundToggle as EventListener);
    window.removeEventListener('pointerdown', this.unlockSound, true);
    window.removeEventListener('keydown', this.unlockSound, true);
    this.sounder.dispose();
  }

  private unlockSound = (): void => {
    this.sounder.unlock();
    window.removeEventListener('pointerdown', this.unlockSound, true);
    window.removeEventListener('keydown', this.unlockSound, true);
  };

  private onSoundToggle = (ev: CustomEvent<boolean>): void => {
    this.soundOn = ev.detail;
    this.sounder.setMuted(!ev.detail);
  };

  /** 航跡圖疊圖(泊位、纜繩、拖船連接點):依 state.scenario.id 的情境;示範模式用示範泊位與拖船。state 沒有外力欄位時為 null。 */
  private externalOverlay(): PlotOverlay | null {
    const s = this.shipState;
    if (s.tugs === undefined && s.berth === undefined && !(s.anchors ?? []).some((a) => a.position)) return null;
    const key = `${this.mode}:${s.scenario?.id ?? ''}:${s.berth?.id ?? ''}`;
    if (key === this.overlayKey) return this.overlayCached;
    this.overlayKey = key;
    let overlay: PlotOverlay | null = null;
    if (this.mode === 'demo' && this.source instanceof DemoSource) {
      const demo = this.source.simulator.demoBerth;
      const geo: BerthGeometry | null = demo ? berthGeometry(demo.definition, null) : null;
      overlay = { route: null, zones: [], events: [], ...(geo ? { berth: geo, mooringLines: demo!.lines } : {}), tugDefs: this.source.simulator.demoTugs.map((t) => ({ id: t.id, attach: t.attach })) };
    } else if (s.scenario?.id) {
      if (this.scenarioCache === null) {
        // 情境清單(含 YAML 解析器)只在需要泊位幾何時才動態載入,儀器頁一般情況不載
        this.overlayKey = '';
        if (!this.scenarioLoading) {
          this.scenarioLoading = true;
          void import('./lib/scenario.js').then((m) => {
            this.scenarioCache = m.loadScenarioList();
            this.requestUpdate();
          });
        }
        return null;
      }
      const sc = this.scenarioCache.find((x) => x.id === s.scenario!.id);
      const geo = sc?.berth?.definition ? berthGeometry(sc.berth.definition, sc.origin ?? null) : null;
      overlay = { route: null, zones: [], events: [], ...(geo ? { berth: geo } : {}), ...(sc?.berth && sc.berth.lines.length > 0 ? { mooringLines: sc.berth.lines } : {}), ...(sc?.tugs ? { tugDefs: sc.tugs } : {}) };
    }
    this.overlayCached = overlay && (overlay.berth || (overlay.tugDefs?.length ?? 0) > 0) ? overlay : null;
    return this.overlayCached;
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
        this.sounder.update(state.alerts);
        // 核心脫開自動舵(autopilotTripped:電羅經/自動舵失效)時同步本頁自動舵面板
        const coreAp = state.autopilot?.enabled;
        if (this.coreAutopilotEnabled === true && coreAp === false && this.autopilot.enabled) this.autopilotPanel?.disable();
        this.coreAutopilotEnabled = coreAp;
      }),
      source.onStatus((status, detail) => {
        this.status = status;
        this.statusDetail = detail ?? '';
        // 斷線/重連中:停止警報聲響(否則依最後一筆狀態一直響,且靜音指令只能暫存送不出去);重連後收到新狀態由 onState 恢復
        if (status !== 'connected') this.sounder.update(undefined);
      }),
    );
    if (source.onMessage) this.unsubscribe.push(source.onMessage((msg) => this.onMessage(msg)));
    source.start();
  }

  /** 核心事件框:只顯示 visible: true 的 message/voiceScript(學員站不顯示教官用事件)。 */
  private onMessage(msg: Record<string, unknown>): void {
    const ev = eventFromFrame(msg);
    if (!ev || !ev.visible) return;
    if (ev.kind !== 'message' && ev.kind !== 'voiceScript') return;
    this.showBanner(ev);
  }

  private showBanner(ev: SimEvent): void {
    const severity = typeof ev.data?.['severity'] === 'string' ? (ev.data['severity'] as string) : 'info';
    this.banner = { text: eventLabel(ev), severity };
    if (this.bannerTimer) clearTimeout(this.bannerTimer);
    this.bannerTimer = setTimeout(() => (this.banner = null), BANNER_MS);
  }

  private stopSource(): void {
    this.sounder.update(undefined);
    this.coreAutopilotEnabled = undefined;
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
    const counts = alertCounts(s.alerts);
    // 核心有感測器/BNWAS 欄位即視為支援警報(無警報時 alerts 省略)
    const alertsSupported = !this.hasReceived || s.sensors !== undefined || s.bnwas !== undefined || counts.total > 0;
    const extOverlay = this.externalOverlay();
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
          ${s.visibility_nm !== undefined && s.visibility_nm < 2 ? html`<span class="warning">能見度 ${s.visibility_nm.toFixed(1)} nm</span>` : nothing}
          ${this.issues.length > 0 ? html`<span class="warning" title=${this.issues.join(', ')}>資料可疑 ${this.issues.length}</span>` : nothing}
          ${counts.unacknowledged > 0 ? html`<span class="alert-badge ${counts.highestUnacknowledged === 'warning' || counts.highestUnacknowledged === 'caution' ? 'warning' : ''}" title="中央警報面板:需確認的警報">警報未確認 ${counts.unacknowledged}</span>` : nothing}
          ${s.bnwas && s.bnwas.stage >= 2 ? html`<span class="alert-badge">BNWAS 第 ${s.bnwas.stage} 階段</span>` : nothing}
        </div>
        <span class="spacer"></span>
        ${this.banner ? html`<div class="banner ${this.banner.severity}" role="status" @click=${() => (this.banner = null)}>${this.banner.text}</div>` : nothing}
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

      <div class="middle">
        <div class="plot-wrap">
          <brm-track-plot .state=${s} .config=${cfg} .history=${this.history} .noData=${!this.hasReceived} .overlay=${extOverlay}></brm-track-plot>
          <brm-external-readout floating .state=${this.hasReceived ? s : null} .config=${cfg} .geometry=${extOverlay?.berth ?? null}></brm-external-readout>
        </div>
        <div class="alert-col">
          <brm-bnwas .bnwas=${s.bnwas}></brm-bnwas>
          <brm-alert-panel .alerts=${s.alerts ?? []} .simTime=${s.t} .soundOn=${this.soundOn} .unsupported=${!alertsSupported} .station=${this.station}></brm-alert-panel>
        </div>
      </div>

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
        <brm-autopilot .heading=${s.heading} .available=${s.autopilot?.available ?? true}></brm-autopilot>
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
