// <brm-app>:整頁版面(上排 conning 條、中間鳥瞰航跡圖、下排操船台、右欄 BNWAS / 警報 / VHF)、資料來源(WebSocket 或示範模式)、
// 主題切換(日/黃昏/夜)與指令路由。規劃書第 7 章第一版儀器;教官控制暫放同頁(第 9.1 節)。
// 版面(docs/design/web-layout.md):右欄跨航跡圖與操船台兩列,文書筆電(1366×768、1536×864)上警報面板與 VHF 都放得下;
// 視窗高度 ≤ 800 px 時 BNWAS 改到航跡圖下方,失電等 header 折成兩行時 VHF 的文字欄 / DSC 仍不必捲動;
// 放不下時右欄自己捲動,面板不互相擠壓。判定由端對端測試把關(tests/e2e/support/layout.ts)。
// 航跡圖左上角另有精簡錨/拖船讀數與接近泊位時的艏/艉橫向速度(<brm-external-readout>;state 有 tugs/anchors/berth 時才顯示),
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
import './components/vhf-panel.js';
import { defaultVhfUrl } from './lib/vhf.js';
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
        /* 兩欄五列:header 與 conning 條跨兩欄;左欄是航跡圖(中段)與操船台(下段),右欄上方是 BNWAS、其下(警報、VHF)跨中段與下段。
           右欄只佔中段時,1366×768 / 1536×864 的中段只剩約 225–300 px,警報面板被擠到高度 0、VHF 下半被操船台蓋住(第二十九輪 L2);
           跨兩列後右欄有約 435–540 px。寬螢幕也用同一種排法:1920 寬的瀏覽器視窗高度常不足 1080,只佔中段時第 2 筆警報就會被切到。
           conning 條與操船台的列高依視窗寬度(--brm-conning-row / --brm-controls-row,下面的 @media),高度 ≤ 800 px 時 BNWAS 改到航跡圖下方 */
        --brm-conning-row: minmax(150px, 20vh);
        --brm-controls-row: minmax(210px, 27vh);
        grid-template-columns: minmax(0, 1fr) 360px;
        grid-template-rows: auto var(--brm-conning-row) auto minmax(0, 1fr) var(--brm-controls-row);
        grid-template-areas:
          'header header'
          'conning conning'
          'plot bnwas'
          'plot side'
          'controls side';
        gap: 6px;
        padding: 6px;
        background: var(--ob-bg-app);
        filter: brightness(var(--ob-dim));
      }
      brm-bnwas {
        grid-area: bnwas;
        min-width: 0;
      }
      brm-conning-bar {
        grid-area: conning;
      }
      /* .middle 只是分組(航跡圖 + 右欄;DOM 與 Tab 順序維持 航跡圖 → 右欄 → 操船台):不產生方框,兩個子元素直接排進 :host 的格線 */
      .middle {
        display: contents;
      }
      /* 航跡圖左上角浮貼錨/拖船/靠泊讀數(不佔警報欄高度,中央警報面板維持原大小) */
      .plot-wrap {
        grid-area: plot;
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
      /* 右欄:中央警報面板、內建 VHF 角色扮演面板(docs/design/vhf-roleplay.md;?vhf=0 不顯示);BNWAS 在它上方的格子(或高度 ≤ 800 px 時在航跡圖下方)。
         VHF 取自然高度(不縮);警報面板吃剩餘高度,至少放得下標題列與 2 筆警報(含「轉移」選單,每筆約 59 px);
         兩者加起來超過欄高時整欄捲動(不互相擠壓、不疊到別的面板上)。
         右欄是尺寸容器(container: side):欄高不足時 VHF 只縮訊息紀錄列(@container side,見下),字級與觸控目標不縮 */
      .alert-col {
        grid-area: side;
        display: flex;
        flex-direction: column;
        gap: 6px;
        min-height: 0;
        min-width: 0;
        overflow-y: auto;
        scrollbar-width: thin;
        container: side / size;
      }
      .alert-col > * {
        flex: 0 0 auto;
      }
      /* 標題列 24 + 2 筆含「轉移」選單的警報(各 59)+ 間距與內距 ≈ 163 px */
      .alert-col > brm-alert-panel {
        flex: 1 1 0;
        min-height: 164px;
      }
      header {
        grid-area: header;
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
      /* 核心位址:寬度 ≤ 1500 px 時不另外顯示(位址仍在連線狀態徽章的 title),讓故障 / 警報徽章多的時候 header 少折一行 */
      @media (max-width: 1500px) {
        header .ws-url {
          display: none;
        }
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
      /* 操船台五個面板:操舵、車鐘、艏側推、自動舵、教官小面板。只佔左欄寬度(右欄跨到這一列);
         各面板是尺寸容器,窄時自己改排法(helm-wheel / thruster-lever / autopilot-panel / instructor-controls 的 @container) */
      .controls {
        grid-area: controls;
        display: grid;
        grid-template-columns: minmax(0, 2.2fr) minmax(0, 1.3fr) minmax(0, 1.3fr) minmax(0, 1.5fr) minmax(0, 1fr);
        gap: 6px;
        min-height: 0;
        min-width: 0;
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
      /* 寬度 ≤ 1900 px(含 1920×1080 筆電在 125% 縮放下的 1536 px):conning 條折成兩列(conning-bar.ts 同一門檻),需要較高的列;
         操船台只有約 1150 px 寬,教官小面板改窄欄(三個鈕直排) */
      @media (max-width: 1900px) {
        :host {
          --brm-conning-row: minmax(220px, 32vh);
        }
        .controls {
          grid-template-columns: minmax(0, 2fr) minmax(0, 1.15fr) minmax(0, 1.25fr) minmax(0, 1.45fr) minmax(0, 0.65fr);
        }
      }
      /* 1366×768 等較小螢幕:下排操船台略縮、間距縮小、右欄 344 px(操船台約 1010 px 寬) */
      @media (max-width: 1500px) {
        :host {
          --brm-controls-row: minmax(200px, 27vh);
          grid-template-columns: minmax(0, 1fr) 344px;
          gap: 4px;
          padding: 4px;
        }
        .alert-col {
          gap: 4px;
        }
        .controls {
          grid-template-columns: minmax(0, 2fr) minmax(0, 1.08fr) minmax(0, 1.1fr) minmax(0, 1.42fr) minmax(0, 0.64fr);
          gap: 4px;
        }
      }
      /* 高度 ≤ 800 px(1366×768、1280×720 的筆電):BNWAS 改到航跡圖下方(一列的橫條,約 40 px),右欄整欄給警報面板與 VHF。
         BNWAS 留在右欄上方時,失電等故障讓 header 折成兩行後右欄只剩約 340–370 px,2 筆警報(164)+ VHF(約 230–290)
         在字級與觸控目標不縮的前提下放不下,VHF 的文字欄 / 送出 / DISTRESS / URGENCY 被擠到視窗外(第二十九輪 L3 量測)。
         DOM 與 Tab 順序不變(航跡圖 → BNWAS → 警報 → VHF → 操船台) */
      @media (max-height: 800px) {
        :host {
          grid-template-rows: auto var(--brm-conning-row) minmax(0, 1fr) auto var(--brm-controls-row);
          grid-template-areas:
            'header header'
            'conning conning'
            'plot side'
            'bnwas side'
            'controls side';
        }
      }
      /* 右欄的實際高度不足(警報 164 + VHF 原尺寸約 292 = 460 px 放不下;例:1536×864 約 460 px、header 折成兩行時 430 px,
         1366×768 約 470 / 434 px,1280×720 約 438 / 402 px):VHF 的訊息紀錄列固定約兩行高(在列內捲動),
         DSC 說明與「麥克風:按 PTT 時要求授權」整句移到 title(不截斷成半句;無麥克風權限等警示照常顯示)。
         頻道鈕、PTT、文字欄、DSC 鈕的字級與大小不變 */
      @container side (max-height: 480px) {
        brm-vhf {
          --brm-vhf-log-min: 30px;
          --brm-vhf-log-max: 30px;
          --brm-vhf-hint-display: none;
          --brm-vhf-mic-hint-display: none;
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
  /** 內建 VHF 角色扮演頻道的轉送網址(?vhf=ws://…:8767/;未給時同核心主機埠 8767;?vhf=0 不顯示面板;示範模式無核心也不顯示) */
  @state() private vhfUrl: string | null = null;
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
    const vhf = params.get('vhf');
    this.vhfUrl = vhf === '0' || vhf === 'off' ? null : vhf || defaultVhfUrl(this.wsUrl);
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
        <span class="pill ${pillClass}" title=${this.mode === 'live' ? `核心 ${this.wsUrl}${this.statusDetail ? `(${this.statusDetail})` : ''}` : this.statusDetail}>${pillText}</span>
        ${this.mode === 'live' ? html`<span class="muted ws-url">${this.wsUrl}</span>` : nothing}
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
        <brm-bnwas .bnwas=${s.bnwas}></brm-bnwas>
        <div class="alert-col">
          <brm-alert-panel .alerts=${s.alerts ?? []} .simTime=${s.t} .soundOn=${this.soundOn} .unsupported=${!alertsSupported} .station=${this.station}></brm-alert-panel>
          ${this.vhfUrl && this.mode === 'live' ? html`<brm-vhf compact .url=${this.vhfUrl} .station=${this.station}></brm-vhf>` : nothing}
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
