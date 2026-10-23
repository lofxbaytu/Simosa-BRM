// <brm-instructor-app>:教官站(規劃書第 9.1 節)第一版,三種版面:
//  - full(預設):訪船船長直連時用。連線狀態與自船摘要(共用 conning 條)、情境選擇與載入、時間表面板
//    (手動觸發、確認鈕、必要事件倒數)、執行控制(凍結/恢復/重設/結束練習、時間倍率、快照/還原)、環境即時改變
//    (含漸變)、故障注入、自船覆寫、操舵模式(FU/NFU/自動舵/應急,送 steeringMode)、鳥瞰航跡圖(疊航線/區域)、核心事件流、學員操作紀錄(由狀態推導)、
//    教官指令與核心回應、BRM 快速標記(第 8.2 節五大類)、警報與 BNWAS 監看(代為確認、BNWAS 模式)、真值 vs 指示值比較
//    (docs/design/sensors-alerts.md)、拖船/錨/繫纜面板與航跡圖的泊位/拖船/錨鏈疊圖(docs/design/physics-tugs-anchors-mooring.md;
//    state.tugs / anchors / berth 有欄位才顯示;簡化模式另有精簡拖船令面板)。
//    核心推送的事件框 {"type":"event"} 與評估框 {"type":"assessment"}(設計文件 docs/design/scenario-engine.md 第 5.2 節)
//    經 onMessage 收下;語音腳本事件由 VoiceScriptPlayer 播放(簡化模式自動、完整模式按「播放」)。
//  - simple(?mode=simple):船上簡化教官模式(第 9.1 節 R13):選擇練習 → 簡報 → 開始(含精簡版操舵面板)→ 凍結/結束 →
//    自動講評摘要與紀錄下載。
//  - replay(?mode=replay):講評站雛型(第 9.2 節),載入 JSON Lines 紀錄檔離線重播,不需核心。
// 指令與狀態格式同儀器頁(src/Contracts);資料來源可為 WebSocket 核心或示範模式。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import type { OwnShipState, ShipId } from './types/state.js';
import type { SimCommand } from './types/command.js';
import type { ConnectionStatus, SimSource } from './net/sim-source.js';
import { DEFAULT_WS_URL, WsClient } from './net/ws-client.js';
import { DemoSource } from './demo/demo-source.js';
import type { DemoOptions } from './demo/demo-sim.js';
import { demoOptionsFromScenario } from './demo/demo-scenario.js';
import { defaultState } from './lib/state-parser.js';
import { getShipConfig, isShipId, type ShipDisplayConfig } from './lib/ship-config.js';
import { TrackHistory } from './lib/track-history.js';
import { THEMES, applyTheme, isTheme, readTheme, type Theme } from './lib/theme.js';
import { COMMAND_EVENT, tileStyles, type CommandEvent } from './components/shared.js';
import { briefingText, loadScenarioList, loadingLabel, type ScenarioSummary } from './lib/scenario.js';
import { endExerciseCommand, getAssessmentCommand, loadScenarioCommand, loadScenarioInlineCommand } from './lib/instructor-command.js';
import type { BerthingRehearsalResult } from './lib/berthing-rehearsal.js';
import { simpleCommand } from './lib/command.js';
import { OperationLogDeriver, type OperationEvent } from './lib/operation-log.js';
import { DebriefAccumulator, type DebriefSummary } from './lib/debrief.js';
import { BRM_CATEGORIES, BrmMarkerStore, type BrmMarker } from './lib/brm-markers.js';
import { StateSampler, buildSessionRecord, type CommandLogEntry } from './lib/session-record.js';
import { saveHandoff } from './report/report-store.js';
import { downloadText, fileStamp } from './lib/download.js';
import { formatHeading, formatKnots, formatMeters, formatRot, formatRudder, formatSimTime } from './lib/format.js';
import type { SimEvent } from './types/event.js';
import { EventStore, eventFromFrame, eventLabel, voiceScriptData } from './lib/sim-event.js';
import { assessmentFromFrame, type AssessmentSummary } from './lib/assessment.js';
import { VoiceScriptPlayer, voiceLine, voiceRecordLabel } from './lib/voice-script.js';
import { eventMarks, overlayFromScenario, overlayHasContent, type PlotOverlay } from './lib/plot-overlay.js';
import { berthGeometry, type ScenarioMooringLineDef, type ScenarioTugDef } from './lib/external-forces.js';
import { CpaAlarmTracker } from './lib/targets.js';
import { parseSteering } from './lib/steering.js';
import { PLAYED_BY_LABEL } from './lib/timeline.js';
import { REJECT_REASON_LABEL, ROLE_LABEL } from './lib/sim-event.js';
import { alertCounts } from './lib/alerts.js';
import { instructorStyles } from './components/instructor/instructor-shared.js';
import type { BrmMarkDetail } from './components/instructor/brm-marker-bar.js';
import type { SnapshotEntry } from './components/instructor/run-control-panel.js';
import type { ReplayView } from './components/instructor/replay-view.js';
import './components/conning-bar.js';
import './components/track-plot.js';
import './components/instructor/scenario-panel.js';
import './components/instructor/berthing-rehearsal-panel.js';
import './components/instructor/run-control-panel.js';
import './components/instructor/environment-panel.js';
import './components/instructor/fault-panel.js';
import './components/instructor/ownship-panel.js';
import './components/instructor/steering-panel.js';
import './components/instructor/operation-log-view.js';
import './components/instructor/command-log-view.js';
import './components/instructor/brm-marker-bar.js';
import './components/instructor/debrief-summary.js';
import './components/instructor/replay-view.js';
import './components/instructor/event-stream.js';
import './components/instructor/alert-monitor.js';
import './components/instructor/sensor-truth-panel.js';
import './components/instructor/tug-panel.js';
import './components/instructor/anchor-panel.js';
import './components/instructor/mooring-panel.js';
import { requiredCountdowns } from './components/instructor/timeline-panel.js';

export type PageMode = 'full' | 'simple' | 'replay';
export type SourceMode = 'live' | 'demo';
type SimplePhase = 'select' | 'briefing' | 'running' | 'ended';

const STALE_MS = 2000;
const STATUS_LABEL: Record<ConnectionStatus, string> = {
  connected: '已連線',
  connecting: '連線中',
  disconnected: '未連線',
  demo: '示範模式',
};
const MODE_TITLE: Record<PageMode, string> = { full: 'Simosa BRM 教官站', simple: 'Simosa BRM 簡化教官模式', replay: 'Simosa BRM 重播檢視' };

function readMode(v: string | null): PageMode {
  return v === 'simple' || v === 'replay' ? v : 'full';
}

@customElement('brm-instructor-app')
export class InstructorApp extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: grid;
        height: 100%;
        grid-template-rows: auto minmax(0, 1fr);
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

      /* ---- 教官站完整版面 ---- */
      .full[hidden],
      brm-replay-view[hidden] {
        display: none;
      }
      .full {
        display: grid;
        grid-template-rows: minmax(150px, 18vh) minmax(0, 1fr);
        gap: 6px;
        min-height: 0;
      }
      .body {
        display: grid;
        grid-template-columns: 320px minmax(0, 1fr) 360px;
        gap: 6px;
        min-height: 0;
      }
      .col {
        display: grid;
        gap: 6px;
        min-height: 0;
      }
      .col.left {
        grid-template-rows: minmax(0, 0.9fr) minmax(0, 1fr) auto;
      }
      .col.center {
        grid-template-rows: minmax(0, 1fr) auto;
      }
      .col.right {
        grid-template-rows: minmax(0, 1fr) minmax(0, 0.7fr) minmax(0, 0.5fr) auto;
      }
      header .warn-badge {
        color: var(--ob-warning);
        border: 1px solid var(--ob-warning);
        border-radius: 4px;
        padding: 1px 6px;
      }
      header .phase {
        font-weight: 600;
      }
      .voice-banner {
        grid-column: 1 / -1;
        font-size: 15px;
        padding: 4px 10px;
        border-radius: 6px;
        background: var(--ob-accent-soft);
        border: 1px solid var(--ob-accent);
      }
      .simple .brief-meta {
        font-size: 14px;
        color: var(--ob-text-secondary);
      }
      .center .plot {
        min-height: 0;
      }
      .center .bottom {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        /* 列高依面板內容(不平均分配),放不下時整列捲動,面板不會互相疊上 */
        grid-auto-rows: max-content;
        align-content: start;
        gap: 6px;
        max-height: 36vh;
        overflow: auto;
      }
      .col.right brm-marker-bar {
        max-height: 30vh;
      }
      @media (max-width: 1500px) {
        .full {
          grid-template-rows: minmax(220px, 30vh) minmax(0, 1fr);
        }
        .body {
          grid-template-columns: 280px minmax(0, 1fr) 320px;
        }
      }
      /* 1366×768 等寬度 ≤ 1440px 的筆電:中欄下方四個面板改兩列兩欄;左欄面板依內容高度排列、整欄捲動,避免面板擠壓與文字截斷 */
      @media (max-width: 1440px) {
        .center .bottom {
          grid-template-columns: 1fr 1fr;
          max-height: 42vh;
        }
        .col.left {
          /* auto 列在可捲動的格線容器內會被平均分配(Chromium),用 max-content 讓列高跟著內容 */
          grid-template-rows: max-content max-content max-content;
          align-content: start;
          overflow-y: auto;
        }
      }

      /* ---- 簡化教官模式 ---- */
      .simple {
        display: grid;
        grid-template-rows: minmax(0, 1fr);
        min-height: 0;
      }
      .simple .stage {
        display: grid;
        gap: 10px;
        min-height: 0;
        padding: 10px;
        background: var(--ob-bg-container);
        border: 1px solid var(--ob-border);
        border-radius: 8px;
      }
      .simple .stage.select {
        grid-template-columns: minmax(0, 1fr) minmax(280px, 420px);
      }
      .simple h2 {
        margin: 0;
        font-size: 22px;
        font-weight: 700;
      }
      .simple .big {
        font-size: 24px;
        line-height: 1.5;
        white-space: pre-wrap;
        overflow: auto;
        padding: 12px 16px;
        background: var(--ob-bg-surface);
        border: 1px solid var(--ob-border);
        border-radius: 8px;
        min-height: 0;
      }
      .simple .actions {
        display: flex;
        gap: 10px;
        flex-wrap: wrap;
      }
      .simple .actions button {
        font-size: 20px;
        font-weight: 600;
        min-height: 56px;
        padding: 8px 24px;
      }
      .simple .stage.briefing {
        grid-template-rows: auto minmax(0, 1fr) auto;
      }
      .simple .stage.running {
        grid-template-rows: auto minmax(0, 1fr) auto;
        grid-template-columns: minmax(0, 1fr) 380px;
      }
      .simple .stage.running .strip {
        grid-column: 1 / -1;
        display: flex;
        flex-wrap: wrap;
        gap: 6px 22px;
        align-items: baseline;
        font-size: 16px;
      }
      .simple .strip .v {
        font-size: 30px;
        font-weight: 700;
        font-variant-numeric: tabular-nums;
      }
      .simple .strip .l {
        color: var(--ob-text-secondary);
        font-size: 13px;
        margin-right: 4px;
      }
      .simple .stage.running .side {
        display: grid;
        /* 標記鈕、操舵面板、時間表依內容高度;操作紀錄吃剩餘高度,1366×768 放不下時側欄捲動 */
        grid-template-rows: max-content max-content max-content max-content minmax(90px, 1fr);
        gap: 10px;
        min-height: 0;
        overflow-y: auto;
      }
      .simple .stage.running .actions {
        grid-column: 1 / -1;
      }
      .simple .ctl-stack {
        display: grid;
        gap: 10px;
        grid-auto-rows: max-content;
      }
      .simple .stage.ended {
        grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
        grid-template-rows: auto minmax(0, 1fr) auto;
      }
      .simple .stage.ended h2,
      .simple .stage.ended .actions {
        grid-column: 1 / -1;
      }
      .simple .stage.ended .panel {
        min-height: 0;
        overflow: auto;
      }
      .simple .sum-note {
        font-size: 13px;
        color: var(--ob-text-secondary);
      }
      .simple .list .text {
        font-size: 15px;
      }
    `,
  ];

  @state() private pageMode: PageMode = 'full';
  @state() private view: 'live' | 'replay' = 'live';
  @state() private sourceMode: SourceMode = 'live';
  @state() private status: ConnectionStatus = 'disconnected';
  @state() private statusDetail = '';
  @state() private shipState: OwnShipState = defaultState('FSB1');
  @state() private issues: string[] = [];
  @state() private theme: Theme = readTheme();
  @state() private stale = false;
  @state() private hasReceived = false;
  @state() private demoShip: ShipId = 'FSB1';
  /** 練習世代:重設、載入情境、覆寫、切換來源時遞增(拖船面板據此清除未回報的方向/出力草稿) */
  @state() private exerciseEpoch = 0;
  @state() private wsUrl = DEFAULT_WS_URL;
  @state() private scenarios: ScenarioSummary[] = [];
  @state() private activeScenario: ScenarioSummary | null = null;
  @state() private opEvents: readonly OperationEvent[] = [];
  @state() private commandLog: CommandLogEntry[] = [];
  @state() private markers: readonly BrmMarker[] = [];
  @state() private snapshots: SnapshotEntry[] = [];
  @state() private timeScale = 1;
  @state() private simplePhase: SimplePhase = 'select';
  /** 簡化教官模式選擇階段的分頁:情境清單或靠離泊預演組題(規劃書第 9.1 節) */
  @state() private simpleSelectTab: 'list' | 'rehearsal' = 'list';
  /** 目前載入的靠離泊預演組題結果(以 loadScenario args.scenario 送核心;簡報頁可下載 YAML);載入檔案情境時為 null */
  @state() private rehearsal: BerthingRehearsalResult | null = null;
  @state() private debriefSummary: DebriefSummary | null = null;
  @state() private confirmEnd = false;
  @state() private events: readonly SimEvent[] = [];
  /** 處於 CPA 警報中的目標(CpaAlarmTracker 的快照;不由可能被截斷的事件流重算) */
  @state() private alarmTargets: ReadonlySet<string> = new Set();
  @state() private coreSummary: AssessmentSummary | null = null;
  @state() private lastVoice: string = '';
  @state() private voicePlaying = false;

  @query('brm-replay-view') private replayView?: ReplayView;

  private source: SimSource | null = null;
  private readonly eventStore = new EventStore(2000);
  private readonly alarms = new CpaAlarmTracker();
  /** 已送出 restore 指令、尚未收到倒帶後第一筆狀態時:要求回到的核心 tick(倒帶點;undefined 表示無待處理倒帶) */
  private pendingRewindTick: number | undefined;
  private readonly voice = new VoiceScriptPlayer();
  private assessmentTimer: ReturnType<typeof setTimeout> | null = null;
  private voiceUnsub: (() => void) | null = null;
  private unsubscribe: Array<() => void> = [];
  private readonly history = new TrackHistory(1, 7200);
  private readonly opLog = new OperationLogDeriver();
  private readonly debrief = new DebriefAccumulator();
  private readonly markerStore = new BrmMarkerStore();
  private readonly sampler = new StateSampler(1);
  /** 練習開始時刻(本機時鐘 ISO;新練習後第一筆抽樣時記下):紀錄的 startedUtc,講評報告以它識別同一練習(不隨匯出時間改變) */
  private exerciseStartedUtc: string | null = null;
  private lastStateAt = 0;
  private staleTimer: ReturnType<typeof setInterval> | null = null;
  private commandSeq = 0;
  private confirmEndTimer: ReturnType<typeof setTimeout> | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    const params = new URLSearchParams(location.search);
    this.pageMode = readMode(params.get('mode'));
    this.view = this.pageMode === 'replay' ? 'replay' : 'live';
    const ws = params.get('ws');
    if (ws) this.wsUrl = ws;
    const theme = params.get('theme');
    if (isTheme(theme)) this.theme = theme;
    const ship = params.get('ship');
    if (isShipId(ship)) this.demoShip = ship;
    this.sourceMode = params.get('demo') === '1' ? 'demo' : 'live';
    applyTheme(this.theme, false);
    this.scenarios = loadScenarioList();
    const preselect = params.get('scenario');
    if (preselect) this.activeScenario = this.scenarios.find((s) => s.id === preselect) ?? null;
    if (this.pageMode !== 'replay') this.startSource();
    this.staleTimer = setInterval(() => {
      const isStale = this.status === 'connected' && this.hasReceived && performance.now() - this.lastStateAt > STALE_MS;
      if (isStale !== this.stale) this.stale = isStale;
    }, 500);
    this.addEventListener(COMMAND_EVENT, this.onCommand as EventListener);
    this.addEventListener('sim-reset', this.onReset);
    this.addEventListener('scenario-load', this.onScenarioLoad as EventListener);
    this.addEventListener('rehearsal-compose', this.onRehearsalCompose as EventListener);
    this.addEventListener('snapshot-taken', this.onSnapshotTaken as EventListener);
    this.addEventListener('time-scale-changed', this.onTimeScale as EventListener);
    this.addEventListener('brm-mark', this.onBrmMark as EventListener);
    this.addEventListener('brm-mark-remove', this.onBrmRemove as EventListener);
    this.addEventListener('brm-export', this.onBrmExport as EventListener);
    this.addEventListener('event-seek', this.onEventSeek as EventListener);
    this.addEventListener('voice-play', this.onVoicePlay as EventListener);
    this.addEventListener('exercise-ended', this.onExerciseEnded);
    this.voiceUnsub = this.voice.onPlaying((p) => (this.voicePlaying = p));
    window.addEventListener('keydown', this.onKeyDown);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.stopSource();
    if (this.staleTimer) clearInterval(this.staleTimer);
    if (this.confirmEndTimer) clearTimeout(this.confirmEndTimer);
    this.removeEventListener(COMMAND_EVENT, this.onCommand as EventListener);
    this.removeEventListener('sim-reset', this.onReset);
    this.removeEventListener('scenario-load', this.onScenarioLoad as EventListener);
    this.removeEventListener('rehearsal-compose', this.onRehearsalCompose as EventListener);
    this.removeEventListener('snapshot-taken', this.onSnapshotTaken as EventListener);
    this.removeEventListener('time-scale-changed', this.onTimeScale as EventListener);
    this.removeEventListener('brm-mark', this.onBrmMark as EventListener);
    this.removeEventListener('brm-mark-remove', this.onBrmRemove as EventListener);
    this.removeEventListener('brm-export', this.onBrmExport as EventListener);
    this.removeEventListener('event-seek', this.onEventSeek as EventListener);
    this.removeEventListener('voice-play', this.onVoicePlay as EventListener);
    this.removeEventListener('exercise-ended', this.onExerciseEnded);
    if (this.assessmentTimer) clearTimeout(this.assessmentTimer);
    this.voiceUnsub?.();
    this.voice.stop();
    window.removeEventListener('keydown', this.onKeyDown);
  }

  private get config(): ShipDisplayConfig {
    return getShipConfig(this.shipState.shipId);
  }

  // ------------------------------------------------------------ 資料來源
  /** 示範模式選項(demo/demo-scenario.ts:座標系與航跡圖疊圖相同、情境拖船/泊位/纜繩初始帶纜)。 */
  private demoOptionsFor(s: ScenarioSummary | null): DemoOptions {
    return demoOptionsFromScenario(s, this.demoShip);
  }

  private startSource(scenario: ScenarioSummary | null = null): void {
    this.stopSource();
    this.exerciseEpoch += 1;
    this.history.clear();
    this.hasReceived = false;
    const source: SimSource = this.sourceMode === 'demo' ? new DemoSource(this.demoOptionsFor(scenario)) : new WsClient({ url: this.wsUrl });
    this.source = source;
    this.unsubscribe.push(
      source.onState(({ state, issues }) => this.onState(state, issues)),
      source.onStatus((status, detail) => {
        this.status = status;
        this.statusDetail = detail ?? '';
      }),
    );
    if (source.onMessage) this.unsubscribe.push(source.onMessage((msg) => this.onMessage(msg)));
    source.start();
  }

  private stopSource(): void {
    for (const u of this.unsubscribe) u();
    this.unsubscribe = [];
    this.source?.stop();
    this.source = null;
  }

  private setSourceMode(mode: SourceMode): void {
    if (mode === this.sourceMode) return;
    this.sourceMode = mode;
    this.startSource(this.activeScenario);
  }

  private setDemoShip(ship: ShipId): void {
    this.demoShip = ship;
    if (this.sourceMode === 'demo') this.startSource(null);
  }

  private onState(state: OwnShipState, issues: string[]): void {
    this.lastStateAt = performance.now();
    if (this.hasReceived && state.tick < this.shipState.tick) this.rewindEvents(state);
    this.hasReceived = true;
    this.shipState = state;
    this.issues = issues;
    this.history.push(state.pos.x, state.pos.y, state.t);
    const recording = this.pageMode !== 'simple' || this.simplePhase === 'running';
    if (recording) {
      const added = this.opLog.push(state);
      if (added.length > 0) this.opEvents = [...this.opLog.all()];
      this.debrief.push(state);
      if (this.sampler.push(state) && this.exerciseStartedUtc === null) this.exerciseStartedUtc = new Date().toISOString();
    } else {
      this.opLog.restart();
    }
  }

  private onMessage(msg: Record<string, unknown>): void {
    const ev = eventFromFrame(msg);
    if (ev) {
      this.onSimEvent(ev);
      return;
    }
    const summary = assessmentFromFrame(msg);
    if (summary) {
      this.coreSummary = summary;
      if (this.assessmentTimer) {
        clearTimeout(this.assessmentTimer);
        this.assessmentTimer = null;
      }
      return;
    }
    if (msg['type'] !== 'ack') return;
    const cmdType = msg['command'];
    const entry = this.commandLog.find((e) => !e.ack && e.command.type === cmdType);
    if (!entry) return;
    const ack: NonNullable<CommandLogEntry['ack']> = { ok: msg['ok'] === true };
    if (typeof msg['detail'] === 'string' && msg['detail']) ack.detail = msg['detail'];
    if (typeof msg['tick'] === 'number') ack.tick = msg['tick'];
    entry.ack = ack;
    this.commandLog = [...this.commandLog];
  }

  /**
   * 即時模式倒帶(restore;D31):狀態 tick 倒退時比照 record-replay.pushState 丟棄 t > 倒帶點的舊分支事件,
   * 倒帶點取 restore 指令要求的 tick(核心倒帶後第一筆廣播晚 BroadcastDivider 個 tick),無則取該狀態的 t;
   * 並重算 CPA 警報集合(C# Restore 由快照還原交通狀態、不重發 cpaAlarm/cpaClear)。
   */
  private rewindEvents(state: OwnShipState): void {
    const want = this.pendingRewindTick;
    this.pendingRewindTick = undefined;
    // 倒帶(含核心端重設造成的 tick 倒退):拖船面板草稿以還原後的 state 為準
    this.exerciseEpoch += 1;
    const dt = state.tick > 0 ? state.t / state.tick : 0.02;
    const tMax = want !== undefined && want <= state.tick ? want * dt : state.t;
    if (this.eventStore.dropAfter(tMax) > 0) this.events = [...this.eventStore.all()];
    this.alarms.rebuild(this.eventStore.all());
    this.alarmTargets = this.alarms.snapshot();
  }

  /** 核心事件:存入事件流;語音事件在簡化教官模式自動播放;練習結束時請求 summary;
   *  steeringModeChanged 記入學員操作紀錄(與 state.steering 的推導去重);commandRejected 標記對應教官指令為核心拒絕。 */
  private onSimEvent(ev: SimEvent): void {
    this.eventStore.push(ev);
    this.events = [...this.eventStore.all()];
    if (this.alarms.apply(ev)) this.alarmTargets = this.alarms.snapshot();
    if (ev.kind === 'steeringModeChanged') this.recordSteeringEvent(ev);
    else if (ev.kind === 'commandRejected') this.markRejected(ev);
    const v = voiceScriptData(ev);
    if (v) {
      this.lastVoice = voiceLine(v);
      if (this.pageMode === 'simple') void this.playVoice(ev, true);
    } else if (ev.kind === 'message' && (ev.visible || this.pageMode === 'simple')) {
      this.lastVoice = eventLabel(ev);
    }
    if (ev.kind === 'exerciseEnded') this.requestAssessment();
  }

  private recordSteeringEvent(ev: SimEvent): void {
    const to = parseSteering({ steering: { mode: ev.data?.['to'], pump: ev.data?.['pump'] ?? this.shipState.steering?.pump ?? 'both' } });
    if (!to) return;
    const from = parseSteering({ steering: { mode: ev.data?.['from'], pump: this.shipState.steering?.pump ?? to.pump } });
    if (this.opLog.recordSteering(to, ev.t, from)) this.opEvents = [...this.opLog.all()];
  }

  /** commandRejected(D35):把最近一筆同類型且尚無回應的教官指令標為核心拒絕(C# 不回 ack 框,以事件代替)。 */
  private markRejected(ev: SimEvent): void {
    const cmdType = ev.data?.['command'];
    if (typeof cmdType !== 'string') return;
    const entry = [...this.commandLog].reverse().find((e) => !e.ack && e.command.type === cmdType);
    if (!entry) return;
    const reason = ev.data?.['reason'];
    entry.ack = { ok: false, detail: typeof reason === 'string' ? REJECT_REASON_LABEL[reason] ?? reason : eventLabel(ev), tick: ev.tick };
    this.commandLog = [...this.commandLog];
  }

  private async playVoice(ev: SimEvent, auto: boolean): Promise<void> {
    const rec = await this.voice.play(ev, this.activeScenario?.id ?? this.shipState.scenario?.id ?? '', auto);
    if (!rec) return;
    this.opLog.addExternal('voiceScript', voiceRecordLabel(rec), rec.t, rec.text || rec.file);
    this.opEvents = [...this.opLog.all()];
  }

  /** 送 getAssessment;核心在 exerciseEnded 時也會主動推送,這裡作為後備(1 s 後仍無回覆才送)。 */
  private requestAssessment(immediate = false): void {
    if (this.assessmentTimer) clearTimeout(this.assessmentTimer);
    const send = () => {
      this.assessmentTimer = null;
      this.send(getAssessmentCommand());
    };
    if (immediate) send();
    else this.assessmentTimer = setTimeout(send, 1000);
  }

  private onExerciseEnded = (): void => {
    this.requestAssessment();
  };

  private onVoicePlay = (ev: CustomEvent<SimEvent>): void => {
    void this.playVoice(ev.detail, false);
  };

  /** 事件流點擊:重播檢視已載有紀錄時跳至該時刻(即時模式下切到重播檢視);未載入紀錄時不切換(空白的重播頁沒有可跳的時刻)。 */
  private onEventSeek = (ev: CustomEvent<SimEvent>): void => {
    if (this.pageMode === 'simple') return;
    if (!this.replayView?.loaded) return;
    void this.seekReplay(ev.detail.t);
  };

  private async seekReplay(t: number): Promise<void> {
    if (this.view !== 'replay') {
      this.view = 'replay';
      await this.updateComplete;
    }
    this.replayView?.seekTo(t);
  }

  /** 核心是否支援情境引擎(C# 核心廣播 state.scenario;Python 參考伺服器不會)。 */
  private get engineUnsupported(): boolean {
    return this.sourceMode === 'live' && this.hasReceived && this.shipState.scenario === undefined && this.events.length === 0;
  }

  /** 核心是否不回報警報/BNWAS(C# 核心有 sensors/bnwas 欄位;Python 參考伺服器沒有;無警報時 alerts 省略) */
  private get alertsUnsupported(): boolean {
    const s = this.shipState;
    return this.sourceMode === 'live' && this.hasReceived && s.sensors === undefined && s.bnwas === undefined && (s.alerts?.length ?? 0) === 0;
  }

  /** 參數集版次警示:情境檔引用參數但核心未回報版次、核心情境 id 與選擇不同、summary 與狀態版次不一致。 */
  private parameterSetWarning(): string {
    const sc = this.shipState.scenario;
    const active = this.activeScenario;
    if (!this.hasReceived || !active) return '';
    if (sc && sc.id && sc.id !== active.id) return `核心載入的情境為 ${sc.id},與所選 ${active.id} 不同`;
    if (active.stats.paramRefs > 0 && sc && !sc.parameterSetVersion) return `情境引用 ${active.stats.paramRefs} 個程序參數,但核心未回報參數集版次`;
    if (this.coreSummary?.parameterSet?.version && sc?.parameterSetVersion && this.coreSummary.parameterSet.version !== sc.parameterSetVersion) return `summary 的參數集版次 ${this.coreSummary.parameterSet.version} 與核心狀態 ${sc.parameterSetVersion} 不同`;
    return '';
  }

  private liveOverlay(): PlotOverlay | null {
    const base = { ...overlayFromScenario(this.activeScenario), events: eventMarks(this.events, this.sampler.all()) };
    // 示範模式且情境沒有泊位/拖船:用示範來源的示意泊位與內建拖船(連接點)
    if (this.sourceMode === 'demo' && this.source instanceof DemoSource) {
      const demo = this.source.simulator.demoBerth;
      if (!base.berth && demo) {
        const geo = berthGeometry(demo.definition, this.activeScenario?.origin ?? null);
        if (geo) base.berth = geo;
        base.mooringLines = demo.lines;
      }
      if (!base.tugDefs && (this.shipState.tugs?.length ?? 0) > 0) base.tugDefs = this.source.simulator.demoTugs.map((t): ScenarioTugDef => ({ id: t.id, attach: t.attach }));
    }
    return overlayHasContent(base) ? base : null;
  }

  /** 繫纜面板用的纜繩定義與泊位幾何(由疊圖取,與航跡圖一致)。 */
  private mooringContext(overlay: PlotOverlay | null): { lines: ScenarioMooringLineDef[]; geometry: PlotOverlay['berth'] } {
    return { lines: overlay?.mooringLines ?? this.activeScenario?.berth?.lines ?? [], geometry: overlay?.berth ?? null };
  }

  // ------------------------------------------------------------ 指令
  private send(cmd: SimCommand, ack?: CommandLogEntry['ack']): CommandLogEntry {
    const entry: CommandLogEntry = { seq: ++this.commandSeq, t: this.shipState.t, wall: new Date().toISOString(), command: cmd };
    if (ack) entry.ack = ack;
    // 先記入再送出:示範來源會同步推回事件(例如 commandRejected),需要找得到這筆指令
    this.commandLog = [...this.commandLog, entry];
    this.source?.send(cmd);
    return entry;
  }

  private onCommand = (ev: CommandEvent): void => {
    const cmd = ev.detail;
    this.send(cmd);
    if (cmd.type === 'reset') this.restartExercise();
    else if (cmd.type === 'restore') {
      const tick = cmd.args?.['tick'];
      this.pendingRewindTick = typeof tick === 'number' && Number.isFinite(tick) ? tick : undefined;
    }
  };

  private onReset = (): void => {
    this.restartExercise();
  };

  /** 新練習(載入情境、重設、覆寫):清航跡、操作紀錄、講評累計與抽樣;標記保留(教官可匯出或刪除)。 */
  private restartExercise(): void {
    this.exerciseEpoch += 1;
    this.history.clear();
    this.opLog.restart();
    this.debrief.reset();
    this.sampler.clear();
    this.exerciseStartedUtc = null;
    this.debriefSummary = null;
    this.eventStore.clear();
    this.events = [];
    this.alarms.clear();
    this.alarmTargets = new Set();
    this.pendingRewindTick = undefined;
    this.coreSummary = null;
    this.lastVoice = '';
    this.voice.stop();
  }

  private onScenarioLoad = (ev: CustomEvent<ScenarioSummary>): void => {
    this.rehearsal = null;
    this.loadScenarioSummary(ev.detail, loadScenarioCommand(ev.detail.id, ev.detail.path));
  };

  /** 靠離泊預演組題(簡化教官模式):以 loadScenario args.scenario 送內嵌情境物件,其餘流程與載入檔案情境相同。 */
  private onRehearsalCompose = (ev: CustomEvent<BerthingRehearsalResult>): void => {
    const r = ev.detail;
    this.rehearsal = r;
    this.loadScenarioSummary(r.summary, loadScenarioInlineCommand(r.id, r.scenario));
  };

  private downloadRehearsalYaml(): void {
    const r = this.rehearsal;
    if (r) downloadText(`${r.id}.yaml`, r.yaml, 'application/yaml');
  }

  private loadScenarioSummary(s: ScenarioSummary, cmd: SimCommand): void {
    this.activeScenario = s;
    this.restartExercise();
    if (this.sourceMode === 'demo') {
      this.startSource(s);
      this.send(cmd, { ok: true, detail: '示範模式:以情境的船、初始狀態與環境重建示範運動學' });
    } else {
      this.send(cmd);
    }
    if (this.pageMode === 'simple') {
      // 簡報期間先凍結,按「開始練習」才恢復
      this.send(simpleCommand('freeze'));
      this.opLog.clear();
      this.opEvents = [];
      this.markerStore.clear();
      this.markers = [];
      this.simplePhase = 'briefing';
    }
  };

  private onSnapshotTaken = (ev: CustomEvent<SnapshotEntry>): void => {
    this.snapshots = [...this.snapshots.slice(-19), ev.detail];
  };

  private onTimeScale = (ev: CustomEvent<number>): void => {
    this.timeScale = ev.detail;
  };

  // ------------------------------------------------------------ BRM 標記
  private addMarker(detail: BrmMarkDetail): void {
    const input: Parameters<BrmMarkerStore['add']>[0] = { category: detail.category, t: this.shipState.t, tick: this.shipState.tick, note: detail.note };
    if (detail.polarity) input.polarity = detail.polarity;
    if (this.activeScenario) input.scenarioId = this.activeScenario.id;
    this.markerStore.add(input);
    this.markers = [...this.markerStore.all()];
  }

  private onBrmMark = (ev: CustomEvent<BrmMarkDetail>): void => {
    this.addMarker(ev.detail);
  };

  private onBrmRemove = (ev: CustomEvent<number>): void => {
    this.markerStore.remove(ev.detail);
    this.markers = [...this.markerStore.all()];
  };

  private onBrmExport = (ev: CustomEvent<'json' | 'csv'>): void => {
    const stamp = fileStamp();
    const id = this.activeScenario?.id ?? this.shipState.shipId;
    if (ev.detail === 'json') {
      downloadText(`brm-markers-${stamp}-${id}.json`, this.markerStore.toJson({ scenarioId: this.activeScenario?.id ?? null, shipId: this.shipState.shipId, source: this.sourceLabel() }));
    } else {
      downloadText(`brm-markers-${stamp}-${id}.csv`, this.markerStore.toCsv(), 'text/csv');
    }
  };

  private onKeyDown = (ev: KeyboardEvent): void => {
    if (ev.ctrlKey || ev.altKey || ev.metaKey) return;
    const target = ev.composedPath()[0] as HTMLElement | undefined;
    const tag = target?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
    if (this.view === 'replay' || (this.pageMode === 'simple' && this.simplePhase !== 'running')) return;
    const cat = BRM_CATEGORIES.find((c) => c.key === ev.key);
    if (cat) {
      this.addMarker({ category: cat.id, note: '' });
      ev.preventDefault();
    }
  };

  // ------------------------------------------------------------ 紀錄下載
  private sourceLabel(): string {
    return this.sourceMode === 'demo' ? `demo:${this.demoShip}` : this.wsUrl;
  }

  private downloadRecord(): void {
    downloadText(`brm-record-${fileStamp()}-${this.activeScenario?.id ?? this.shipState.shipId}.json`, this.sessionRecordJson());
  }

  /** 講評報告頁(report.html):把目前的練習紀錄經 IndexedDB 交給新頁(重播檢視頁面沒有即時紀錄,只開新頁)。 */
  private async openReport(): Promise<void> {
    if (this.pageMode !== 'replay') await saveHandoff(this.sessionRecordJson());
    window.open(this.pageMode === 'replay' ? './report.html' : './report.html?from=instructor', '_blank');
  }

  private sessionRecordJson(): string {
    // 尚未收到狀態就匯出時也定下開始時刻,之後同一練習的匯出沿用
    this.exerciseStartedUtc ??= new Date().toISOString();
    const record = buildSessionRecord({
      mode: this.pageMode === 'simple' ? 'simple' : 'full',
      source: this.sourceLabel(),
      scenario: this.activeScenario,
      shipId: this.shipState.shipId,
      sampleInterval_s: this.sampler.interval_s,
      states: this.sampler.all(),
      operations: this.opLog.all(),
      markers: this.markerStore.all(),
      commands: this.commandLog,
      summary: this.debrief.summary(),
      events: this.eventStore.all(),
      coreSummary: this.coreSummary,
      startedUtc: this.exerciseStartedUtc,
    });
    return JSON.stringify(record);
  }

  // ------------------------------------------------------------ 簡化教官模式流程
  private simpleStart(): void {
    this.restartExercise();
    this.opLog.clear();
    this.opEvents = [];
    this.simplePhase = 'running';
    this.send(simpleCommand('resume'));
  }

  private simpleToggleFreeze(): void {
    this.send(simpleCommand(this.shipState.flags?.frozen ? 'resume' : 'freeze'));
  }

  private simpleEnd(): void {
    if (!this.confirmEnd) {
      this.confirmEnd = true;
      this.confirmEndTimer = setTimeout(() => (this.confirmEnd = false), 4000);
      return;
    }
    if (this.confirmEndTimer) clearTimeout(this.confirmEndTimer);
    this.confirmEnd = false;
    // endExercise 定稿核心 summary(C# 核心會推送 assessment 框);Python 參考伺服器不認得時仍以 freeze 停下
    this.send(endExerciseCommand('帶領者結束練習'));
    this.send(simpleCommand('freeze'));
    this.requestAssessment();
    this.debriefSummary = this.debrief.summary();
    this.simplePhase = 'ended';
  }

  private simpleNew(): void {
    this.simplePhase = 'select';
    this.debriefSummary = null;
  }

  // ------------------------------------------------------------ 主題與全螢幕
  private setTheme(theme: Theme): void {
    this.theme = theme;
    applyTheme(theme);
  }

  private toggleFullscreen(): void {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void document.documentElement.requestFullscreen?.();
  }

  // ------------------------------------------------------------ 畫面
  override render() {
    // 重播檢視常駐(以 hidden 切換,不換模板):載入的紀錄與時間軸位置不因切回「即時」而遺失
    return html`
      ${this.renderHeader()}
      ${this.pageMode === 'simple'
        ? this.renderSimple()
        : html`${this.renderFull()}<brm-replay-view ?hidden=${this.view !== 'replay'}></brm-replay-view>`}
    `;
  }

  private renderHeader() {
    const s = this.shipState;
    const cfg = this.config;
    const flags = s.flags ?? {};
    const faults = s.faults ?? [];
    const pillClass = this.stale ? 'stale' : this.status;
    const pillText = this.stale ? '資料逾時' : STATUS_LABEL[this.status];
    const live = this.pageMode !== 'replay';
    const alerts = alertCounts(s.alerts);
    return html`
      <header>
        <span class="title">${MODE_TITLE[this.pageMode]}</span>
        ${live ? html`<span class="ship">${cfg.name}(${cfg.id})${this.activeScenario ? ` · ${this.activeScenario.id}` : ''}${s.scenario?.parameterSetVersion ? html` · <span class="param-version" title="公司程序參數集版次(state.scenario.parameterSetVersion)">參數集 ${s.scenario.parameterSetVersion}</span>` : ''}</span>` : nothing}
        ${live ? html`<span class="pill ${pillClass}" title=${this.statusDetail}>${pillText}</span>` : nothing}
        ${live && this.sourceMode === 'live' ? html`<span class="muted">${this.wsUrl}</span>` : nothing}
        ${live
          ? html`<div class="alarms">
              ${flags.frozen ? html`<span class="caution">已凍結</span>` : nothing}
              ${flags.aground ? html`<span class="alarm">擱淺</span>` : nothing}
              ${flags.collision ? html`<span class="alarm">碰撞</span>` : nothing}
              ${faults.map((f) => html`<span class="alarm">故障:${f}</span>`)}
              ${s.scenario?.phase && s.scenario.phase !== 'normal' ? html`<span class="phase ${s.scenario.phase === 'emergency' ? 'alarm' : 'warning'}">階段 ${s.scenario.phase}</span>` : nothing}
              ${s.scenario?.hardFailed ? html`<span class="alarm">硬性失敗</span>` : nothing}
              ${s.scenario?.ended ? html`<span class="caution">練習已結束</span>` : nothing}
              ${this.parameterSetWarning() ? html`<span class="warn-badge param-warning" title=${this.parameterSetWarning()}>參數集不一致</span>` : nothing}
              ${this.issues.length > 0 ? html`<span class="warning" title=${this.issues.join(', ')}>資料可疑 ${this.issues.length}</span>` : nothing}
              ${alerts.unacknowledged > 0 ? html`<span class="${alerts.highestUnacknowledged === 'warning' || alerts.highestUnacknowledged === 'caution' ? 'warn-badge' : 'alarm'} alert-badge" title="駕駛台警報需確認">警報未確認 ${alerts.unacknowledged}</span>` : nothing}
              ${s.bnwas && s.bnwas.stage >= 1 ? html`<span class="${s.bnwas.stage >= 2 ? 'alarm' : 'caution'} bnwas-badge">BNWAS 第 ${s.bnwas.stage} 階段</span>` : nothing}
            </div>`
          : nothing}
        <span class="spacer"></span>
        ${this.pageMode === 'full'
          ? html`<span class="seg" role="group" aria-label="檢視">
              <button class=${this.view === 'live' ? 'active' : ''} @click=${() => (this.view = 'live')}>即時</button>
              <button class="view-replay ${this.view === 'replay' ? 'active' : ''}" @click=${() => (this.view = 'replay')}>重播檢視</button>
            </span>`
          : nothing}
        ${live && this.sourceMode === 'demo'
          ? html`<label class="label">船 <select .value=${this.demoShip} @change=${(e: Event) => this.setDemoShip((e.target as HTMLSelectElement).value as ShipId)}>
                <option value="FSB1">FSB1 中塑油品壹號</option>
                <option value="FSB2">FSB2 中塑油品貳號</option>
              </select></label>`
          : nothing}
        ${live
          ? html`<span class="seg" role="group" aria-label="資料來源">
              <button class=${this.sourceMode === 'live' ? 'active' : ''} @click=${() => this.setSourceMode('live')}>即時連線</button>
              <button class=${this.sourceMode === 'demo' ? 'active' : ''} @click=${() => this.setSourceMode('demo')}>示範模式</button>
            </span>`
          : nothing}
        ${this.pageMode === 'full' ? html`<button class="download-record" title="下載本站紀錄(狀態抽樣、操作、標記、指令、摘要)" @click=${this.downloadRecord}>下載紀錄 JSON</button>` : nothing}
        ${this.pageMode !== 'simple' ? html`<button class="open-report" title="講評報告、行為標記評估表、個人訓練紀錄(新分頁;可列印 / 另存 PDF)" @click=${this.openReport}>講評報告</button>` : nothing}
        <span class="seg" role="group" aria-label="主題">
          ${THEMES.map((t) => html`<button class=${this.theme === t.id ? 'active' : ''} @click=${() => this.setTheme(t.id)}>${t.label}</button>`)}
        </span>
        <button title="全螢幕" @click=${this.toggleFullscreen}>全螢幕</button>
        <span class="notice">訓練模擬器,非航行設備</span>
      </header>
    `;
  }

  private renderFull() {
    const s = this.shipState;
    const cfg = this.config;
    const overlay = this.liveOverlay();
    const moor = this.mooringContext(overlay);
    // 核心有錨資料時一律輸出 anchors:錨使用中、情境初始已錨泊或情境有泊位/拖船時錨面板排前面,否則放在最後(不擠下警報監看)
    const anchorsFirst = (s.anchors ?? []).some((a) => a.status !== 'stowed') || this.activeScenario?.anchorsInitial === true || s.berth !== undefined || (s.tugs?.length ?? 0) > 0;
    return html`
      <div class="full" ?hidden=${this.view !== 'live'}>
        <brm-conning-bar .state=${s} .config=${cfg} .issues=${this.issues} .noData=${!this.hasReceived}></brm-conning-bar>
        <div class="body">
          <div class="col left">
            <brm-scenario-panel .scenarios=${this.scenarios} .activeId=${this.activeScenario?.id ?? ''}></brm-scenario-panel>
            <brm-timeline-panel
              .timeline=${this.activeScenario?.timeline ?? []}
              .events=${this.events}
              .scenarioStatus=${s.scenario ?? null}
              .assessment=${this.activeScenario?.assessment ?? null}
              .simTime=${s.t}
              .unsupported=${this.engineUnsupported}
            ></brm-timeline-panel>
            <brm-run-control .frozen=${s.flags?.frozen ?? false} .ended=${s.scenario?.ended ?? false} .tick=${s.tick} .simTime=${s.t} .snapshots=${this.snapshots} .timeScale=${this.timeScale}></brm-run-control>
          </div>
          <div class="col center">
            <brm-track-plot class="plot" .state=${s} .config=${cfg} .history=${this.history} .noData=${!this.hasReceived} .overlay=${overlay} .targets=${s.targets} .alarmTargets=${this.alarmTargets} target-table></brm-track-plot>
            <div class="bottom">
              ${s.tugs ? html`<brm-tug-panel .tugs=${s.tugs} .defs=${overlay?.tugDefs ?? this.activeScenario?.tugs ?? []} .epoch=${this.exerciseEpoch}></brm-tug-panel>` : nothing}
              ${s.anchors && anchorsFirst ? html`<brm-anchor-panel .anchors=${s.anchors}></brm-anchor-panel>` : nothing}
              ${s.berth ? html`<brm-mooring-panel .berth=${s.berth} .geometry=${moor.geometry ?? null} .lineDefs=${moor.lines}></brm-mooring-panel>` : nothing}
              <brm-alert-monitor .alerts=${s.alerts ?? []} .bnwas=${s.bnwas} .events=${this.events} .simTime=${s.t} .unsupported=${this.alertsUnsupported}></brm-alert-monitor>
              <brm-sensor-truth .state=${this.hasReceived ? s : null}></brm-sensor-truth>
              <brm-environment-panel .state=${this.hasReceived ? s : null}></brm-environment-panel>
              <brm-fault-panel .active=${s.faults ?? []}></brm-fault-panel>
              <brm-ownship-panel .state=${this.hasReceived ? s : null}></brm-ownship-panel>
              <brm-steering-panel .state=${this.hasReceived ? s : null}></brm-steering-panel>
              ${s.anchors && !anchorsFirst ? html`<brm-anchor-panel .anchors=${s.anchors}></brm-anchor-panel>` : nothing}
            </div>
          </div>
          <div class="col right">
            <brm-event-stream .events=${this.events} voiceButtons .unsupported=${this.engineUnsupported}></brm-event-stream>
            <brm-operation-log .events=${this.opEvents}></brm-operation-log>
            <brm-command-log .entries=${this.commandLog}></brm-command-log>
            <brm-marker-bar .markers=${this.markers}></brm-marker-bar>
          </div>
        </div>
      </div>
    `;
  }

  private renderSimple() {
    const s = this.shipState;
    const cfg = this.config;
    const sc = this.activeScenario;
    const frozen = s.flags?.frozen ?? false;
    switch (this.simplePhase) {
      case 'select':
        return html`<div class="simple">
          <div class="stage select">
            <div class="panel">
              <h2>選擇練習</h2>
              <div class="sum-note">${this.simpleSelectTab === 'list' ? '選擇情境後按「載入」進入簡報' : '輸入當次航次的泊位、潮汐、預報風與拖船數,按「組題並載入」進入簡報'};練習中以 BRM 標記鈕記錄觀察,結束後顯示自動講評摘要並可下載紀錄。</div>
              <div class="sum-note">${this.hasReceived ? `核心:${cfg.name},模擬時間 ${formatSimTime(s.t)}` : '等待核心資料(未連線時可切換示範模式)'}</div>
              <div class="seg select-tab" role="tablist" aria-label="練習來源">
                <button role="tab" class=${this.simpleSelectTab === 'list' ? 'active' : ''} aria-selected=${this.simpleSelectTab === 'list'} @click=${() => (this.simpleSelectTab = 'list')}>情境清單</button>
                <button role="tab" class=${this.simpleSelectTab === 'rehearsal' ? 'active' : ''} aria-selected=${this.simpleSelectTab === 'rehearsal'} @click=${() => (this.simpleSelectTab = 'rehearsal')}>靠離泊預演</button>
              </div>
            </div>
            ${this.simpleSelectTab === 'list'
              ? html`<brm-scenario-panel .scenarios=${this.scenarios} .activeId=${sc?.id ?? ''}></brm-scenario-panel>`
              : html`<brm-berthing-rehearsal-panel></brm-berthing-rehearsal-panel>`}
          </div>
        </div>`;
      case 'briefing':
        return html`<div class="simple">
          <div class="stage briefing">
            <h2>${sc?.id} ${sc?.name} <span class="sum-note">${sc ? `${sc.shipId} · ${loadingLabel(sc.loading)}` : ''}${s.scenario?.parameterSetVersion ? ` · 參數集 ${s.scenario.parameterSetVersion}` : ''}</span></h2>
            <div class="big briefing-text">${sc ? briefingText(sc) : ''}${sc && (sc.briefingDoc.roles.length > 0 || sc.briefingDoc.attachments.length > 0)
                ? html`<div class="brief-meta">
                    ${sc.briefingDoc.roles.length > 0 ? html`<div>角色:${sc.briefingDoc.roles.map((r) => `${ROLE_LABEL[r.role] ?? r.role}(${PLAYED_BY_LABEL[r.playedBy] ?? r.playedBy}${r.required ? ',必要' : ''})`).join('、')}${sc.briefingDoc.minParticipants !== undefined ? ` · 最少 ${sc.briefingDoc.minParticipants} 人` : ''}</div>` : nothing}
                    ${sc.briefingDoc.attachments.length > 0 ? html`<div>附件:${sc.briefingDoc.attachments.map((a) => a.title).join('、')}</div>` : nothing}
                  </div>`
                : nothing}</div>
            <div class="actions">
              <button class="primary start" @click=${this.simpleStart}>開始練習</button>
              <button @click=${this.simpleNew}>返回</button>
              ${this.rehearsal ? html`<button class="download-yaml" @click=${this.downloadRehearsalYaml}>下載情境 YAML</button>` : nothing}
              <span class="sum-note">${this.rehearsal ? '靠離泊預演組題已以內嵌情境送核心並凍結' : '核心已載入情境並凍結'};按「開始練習」恢復執行</span>
            </div>
          </div>
        </div>`;
      case 'running': {
        const simpleOverlay = this.liveOverlay();
        return html`<div class="simple">
          <div class="stage running">
            <div class="strip">
              <span><span class="l">模擬時間</span><span class="v ${frozen ? 'caution' : ''}">${formatSimTime(s.t)}</span></span>
              <span><span class="l">HDG</span><span class="v">${formatHeading(s.heading)}</span></span>
              <span><span class="l">ROT</span><span class="v">${formatRot(s.rot)}</span></span>
              <span><span class="l">STW / SOG</span><span class="v">${formatKnots(s.stw)} / ${formatKnots(s.sog)}</span></span>
              <span><span class="l">舵</span><span class="v">${formatRudder(s.rudder)}</span></span>
              <span><span class="l">車鐘</span><span class="v">${s.telegraph}</span></span>
              <span><span class="l">UKC</span><span class="v ${s.depthBelowKeel <= 1 ? 'alarm' : ''}">${formatMeters(s.depthBelowKeel)} m</span></span>
              ${frozen ? html`<span class="v caution">已凍結</span>` : nothing}
              ${s.flags?.aground ? html`<span class="v alarm">擱淺</span>` : nothing}
              ${(s.faults ?? []).map((f) => html`<span class="alarm">故障:${f}</span>`)}
              ${s.scenario?.phase && s.scenario.phase !== 'normal' ? html`<span class="v warning">階段 ${s.scenario.phase}</span>` : nothing}
              ${alertCounts(s.alerts).unacknowledged > 0 ? html`<span class="v alarm">警報未確認 ${alertCounts(s.alerts).unacknowledged}</span>` : nothing}
              ${s.bnwas && s.bnwas.stage >= 1 ? html`<span class="v ${s.bnwas.stage >= 2 ? 'alarm' : 'caution'}">BNWAS ${s.bnwas.stage}</span>` : nothing}
            </div>
            ${this.lastVoice ? html`<div class="voice-banner">${this.voicePlaying ? '▶ ' : ''}${this.lastVoice}</div>` : nothing}
            <brm-track-plot .state=${s} .config=${cfg} .history=${this.history} .noData=${!this.hasReceived} .overlay=${simpleOverlay} .targets=${s.targets} .alarmTargets=${this.alarmTargets}></brm-track-plot>
            <div class="side">
              <brm-marker-bar large .markers=${this.markers}></brm-marker-bar>
              <div class="ctl-stack">
                ${s.tugs && s.tugs.length > 0 ? html`<brm-tug-panel compact .tugs=${s.tugs} .defs=${simpleOverlay?.tugDefs ?? sc?.tugs ?? []} .epoch=${this.exerciseEpoch}></brm-tug-panel>` : nothing}
                <brm-steering-panel compact .state=${this.hasReceived ? s : null}></brm-steering-panel>
              </div>
              <brm-alert-monitor compact .alerts=${s.alerts ?? []} .bnwas=${s.bnwas} .events=${this.events} .simTime=${s.t} .unsupported=${this.alertsUnsupported}></brm-alert-monitor>
              <brm-timeline-panel compact .timeline=${sc?.timeline ?? []} .events=${this.events} .scenarioStatus=${s.scenario ?? null} .assessment=${sc?.assessment ?? null} .simTime=${s.t} .unsupported=${this.engineUnsupported}></brm-timeline-panel>
              <brm-operation-log .events=${this.opEvents} .max=${50}></brm-operation-log>
            </div>
            <div class="actions">
              <button class="freeze ${frozen ? 'frozen' : ''}" @click=${this.simpleToggleFreeze}>${frozen ? '恢復' : '凍結'}</button>
              <button class="end ${this.confirmEnd ? 'danger' : ''}" @click=${this.simpleEnd}>${this.confirmEnd ? '再按一次確認結束' : '結束練習'}</button>
              <span class="sum-note">${sc?.id} ${sc?.name} · 標記 ${this.markers.length} 筆 · 快速鍵 1–5</span>
            </div>
          </div>
        </div>`;
      }
      case 'ended':
        return html`<div class="simple">
          <div class="stage ended">
            <h2>練習結束 — ${sc?.id} ${sc?.name}</h2>
            <div class="panel">
              <div class="panel-title"><span>自動講評摘要 DEBRIEF</span><span class="zh">${this.coreSummary ? `核心評分${this.coreSummary.final ? '(定稿)' : ''}${this.coreSummary.parameterSet?.version ? ` · 參數集 ${this.coreSummary.parameterSet.version}` : ''}` : '本地推導(核心未回覆 summary)'}</span></div>
              <brm-debrief-summary large .summary=${this.debriefSummary} .coreSummary=${this.coreSummary} .countdowns=${sc?.assessment ? requiredCountdowns(sc.assessment, this.events, s.t) : []}></brm-debrief-summary>
            </div>
            <brm-marker-bar .markers=${this.markers}></brm-marker-bar>
            <div class="actions">
              <button class="primary download" @click=${this.downloadRecord}>下載紀錄 JSON</button>
              <button class="open-report" @click=${this.openReport}>講評報告</button>
              <button @click=${this.simpleNew}>新練習</button>
              <span class="sum-note">紀錄含狀態抽樣(每 ${this.sampler.interval_s} s)、學員操作、BRM 標記、指令與摘要;可在教官站的重播檢視開啟</span>
            </div>
          </div>
        </div>`;
    }
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-instructor-app': InstructorApp;
  }
}
