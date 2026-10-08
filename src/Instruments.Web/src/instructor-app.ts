// <brm-instructor-app>:教官站(規劃書第 9.1 節)第一版,三種版面:
//  - full(預設):訪船船長直連時用。連線狀態與自船摘要(共用 conning 條)、情境選擇與載入、執行控制
//    (凍結/恢復/重設、時間倍率、快照/還原)、環境即時改變、故障注入、自船覆寫、鳥瞰航跡圖、
//    學員操作紀錄(由狀態推導)、教官指令與核心回應、BRM 快速標記(第 8.2 節五大類)。
//  - simple(?mode=simple):船上簡化教官模式(第 9.1 節 R13):選擇練習 → 簡報 → 開始 → 凍結/結束 →
//    自動講評摘要與紀錄下載。
//  - replay(?mode=replay):講評站雛型(第 9.2 節),載入 JSON Lines 紀錄檔離線重播,不需核心。
// 指令與狀態格式同儀器頁(src/Contracts);資料來源可為 WebSocket 核心或示範模式。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import type { OwnShipState, ShipId } from './types/state.js';
import type { SimCommand } from './types/command.js';
import type { ConnectionStatus, SimSource } from './net/sim-source.js';
import { DEFAULT_WS_URL, WsClient } from './net/ws-client.js';
import { DemoSource } from './demo/demo-source.js';
import type { DemoOptions } from './demo/demo-sim.js';
import { defaultState } from './lib/state-parser.js';
import { getShipConfig, isShipId, type ShipDisplayConfig } from './lib/ship-config.js';
import { TrackHistory } from './lib/track-history.js';
import { THEMES, applyTheme, isTheme, readTheme, type Theme } from './lib/theme.js';
import { COMMAND_EVENT, tileStyles, type CommandEvent } from './components/shared.js';
import { briefingText, loadScenarioList, loadingLabel, type ScenarioSummary } from './lib/scenario.js';
import { loadScenarioCommand } from './lib/instructor-command.js';
import { simpleCommand } from './lib/command.js';
import { OperationLogDeriver, type OperationEvent } from './lib/operation-log.js';
import { DebriefAccumulator, type DebriefSummary } from './lib/debrief.js';
import { BRM_CATEGORIES, BrmMarkerStore, type BrmMarker } from './lib/brm-markers.js';
import { StateSampler, buildSessionRecord, type CommandLogEntry } from './lib/session-record.js';
import { downloadText, fileStamp } from './lib/download.js';
import { formatHeading, formatKnots, formatMeters, formatRot, formatRudder, formatSimTime } from './lib/format.js';
import { instructorStyles } from './components/instructor/instructor-shared.js';
import type { BrmMarkDetail } from './components/instructor/brm-marker-bar.js';
import type { SnapshotEntry } from './components/instructor/run-control-panel.js';
import './components/conning-bar.js';
import './components/track-plot.js';
import './components/instructor/scenario-panel.js';
import './components/instructor/run-control-panel.js';
import './components/instructor/environment-panel.js';
import './components/instructor/fault-panel.js';
import './components/instructor/ownship-panel.js';
import './components/instructor/operation-log-view.js';
import './components/instructor/command-log-view.js';
import './components/instructor/brm-marker-bar.js';
import './components/instructor/debrief-summary.js';
import './components/instructor/replay-view.js';

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
        grid-template-rows: minmax(0, 1fr) auto;
      }
      .col.center {
        grid-template-rows: minmax(0, 1fr) auto;
      }
      .col.right {
        grid-template-rows: minmax(0, 1fr) minmax(0, 0.8fr) auto;
      }
      .center .plot {
        min-height: 0;
      }
      .center .bottom {
        display: grid;
        grid-template-columns: 1.2fr 1fr 0.9fr;
        gap: 6px;
        max-height: 36vh;
        overflow: auto;
      }
      .col.right brm-marker-bar {
        max-height: 34vh;
      }
      @media (max-width: 1500px) {
        .full {
          grid-template-rows: minmax(220px, 30vh) minmax(0, 1fr);
        }
        .body {
          grid-template-columns: 280px minmax(0, 1fr) 320px;
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
        grid-template-rows: minmax(0, 1fr) auto;
        gap: 10px;
        min-height: 0;
      }
      .simple .stage.running .actions {
        grid-column: 1 / -1;
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
  @state() private wsUrl = DEFAULT_WS_URL;
  @state() private scenarios: ScenarioSummary[] = [];
  @state() private activeScenario: ScenarioSummary | null = null;
  @state() private opEvents: readonly OperationEvent[] = [];
  @state() private commandLog: CommandLogEntry[] = [];
  @state() private markers: readonly BrmMarker[] = [];
  @state() private snapshots: SnapshotEntry[] = [];
  @state() private timeScale = 1;
  @state() private simplePhase: SimplePhase = 'select';
  @state() private debriefSummary: DebriefSummary | null = null;
  @state() private confirmEnd = false;

  private source: SimSource | null = null;
  private unsubscribe: Array<() => void> = [];
  private readonly history = new TrackHistory(1, 7200);
  private readonly opLog = new OperationLogDeriver();
  private readonly debrief = new DebriefAccumulator();
  private readonly markerStore = new BrmMarkerStore();
  private readonly sampler = new StateSampler(1);
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
    this.addEventListener('snapshot-taken', this.onSnapshotTaken as EventListener);
    this.addEventListener('time-scale-changed', this.onTimeScale as EventListener);
    this.addEventListener('brm-mark', this.onBrmMark as EventListener);
    this.addEventListener('brm-mark-remove', this.onBrmRemove as EventListener);
    this.addEventListener('brm-export', this.onBrmExport as EventListener);
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
    this.removeEventListener('snapshot-taken', this.onSnapshotTaken as EventListener);
    this.removeEventListener('time-scale-changed', this.onTimeScale as EventListener);
    this.removeEventListener('brm-mark', this.onBrmMark as EventListener);
    this.removeEventListener('brm-mark-remove', this.onBrmRemove as EventListener);
    this.removeEventListener('brm-export', this.onBrmExport as EventListener);
    window.removeEventListener('keydown', this.onKeyDown);
  }

  private get config(): ShipDisplayConfig {
    return getShipConfig(this.shipState.shipId);
  }

  // ------------------------------------------------------------ 資料來源
  private demoOptionsFor(s: ScenarioSummary | null): DemoOptions {
    const o: DemoOptions = { shipId: s?.shipId ?? this.demoShip, initialHeading: s?.initial.heading ?? 45, initialSpeed_kn: s?.initial.speed ?? 8 };
    if (s) {
      const p = s.initial.position;
      if (p?.lat !== undefined && p.lon !== undefined) {
        o.originLat = p.lat;
        o.originLon = p.lon;
      }
      if (s.environment.waterDepth !== undefined) o.waterDepth_m = s.environment.waterDepth;
      const w = s.environment.wind;
      if (w && w.trueSpeed !== undefined && w.trueDir !== undefined) o.wind = { trueSpeed: w.trueSpeed, trueDir: w.trueDir };
      const c = s.environment.current;
      if (c && c.set !== undefined && c.drift !== undefined) o.current = { set: c.set, drift: c.drift };
    }
    return o;
  }

  private startSource(scenario: ScenarioSummary | null = null): void {
    this.stopSource();
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
    this.hasReceived = true;
    this.shipState = state;
    this.issues = issues;
    this.history.push(state.pos.x, state.pos.y, state.t);
    const recording = this.pageMode !== 'simple' || this.simplePhase === 'running';
    if (recording) {
      const added = this.opLog.push(state);
      if (added.length > 0) this.opEvents = [...this.opLog.all()];
      this.debrief.push(state);
      this.sampler.push(state);
    } else {
      this.opLog.restart();
    }
  }

  private onMessage(msg: Record<string, unknown>): void {
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

  // ------------------------------------------------------------ 指令
  private send(cmd: SimCommand, ack?: CommandLogEntry['ack']): CommandLogEntry {
    this.source?.send(cmd);
    const entry: CommandLogEntry = { seq: ++this.commandSeq, t: this.shipState.t, wall: new Date().toISOString(), command: cmd };
    if (ack) entry.ack = ack;
    this.commandLog = [...this.commandLog, entry];
    return entry;
  }

  private onCommand = (ev: CommandEvent): void => {
    const cmd = ev.detail;
    this.send(cmd);
    if (cmd.type === 'reset') this.restartExercise();
  };

  private onReset = (): void => {
    this.restartExercise();
  };

  /** 新練習(載入情境、重設、覆寫):清航跡、操作紀錄、講評累計與抽樣;標記保留(教官可匯出或刪除)。 */
  private restartExercise(): void {
    this.history.clear();
    this.opLog.restart();
    this.debrief.reset();
    this.sampler.clear();
    this.debriefSummary = null;
  }

  private onScenarioLoad = (ev: CustomEvent<ScenarioSummary>): void => {
    const s = ev.detail;
    this.activeScenario = s;
    this.restartExercise();
    if (this.sourceMode === 'demo') {
      this.startSource(s);
      this.send(loadScenarioCommand(s.id, s.path), { ok: true, detail: '示範模式:以情境的船、初始狀態與環境重建示範運動學' });
    } else {
      this.send(loadScenarioCommand(s.id, s.path));
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
    });
    downloadText(`brm-record-${fileStamp()}-${this.activeScenario?.id ?? this.shipState.shipId}.json`, JSON.stringify(record));
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
    this.send(simpleCommand('freeze'));
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
    return html`
      ${this.renderHeader()}
      ${this.pageMode === 'simple' ? this.renderSimple() : this.view === 'replay' ? html`<brm-replay-view></brm-replay-view>` : this.renderFull()}
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
    return html`
      <header>
        <span class="title">${MODE_TITLE[this.pageMode]}</span>
        ${live ? html`<span class="ship">${cfg.name}(${cfg.id})${this.activeScenario ? ` · ${this.activeScenario.id}` : ''}</span>` : nothing}
        ${live ? html`<span class="pill ${pillClass}" title=${this.statusDetail}>${pillText}</span>` : nothing}
        ${live && this.sourceMode === 'live' ? html`<span class="muted">${this.wsUrl}</span>` : nothing}
        ${live
          ? html`<div class="alarms">
              ${flags.frozen ? html`<span class="caution">已凍結</span>` : nothing}
              ${flags.aground ? html`<span class="alarm">擱淺</span>` : nothing}
              ${flags.collision ? html`<span class="alarm">碰撞</span>` : nothing}
              ${faults.map((f) => html`<span class="alarm">故障:${f}</span>`)}
              ${this.issues.length > 0 ? html`<span class="warning" title=${this.issues.join(', ')}>資料可疑 ${this.issues.length}</span>` : nothing}
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
    return html`
      <div class="full">
        <brm-conning-bar .state=${s} .config=${cfg} .issues=${this.issues} .noData=${!this.hasReceived}></brm-conning-bar>
        <div class="body">
          <div class="col left">
            <brm-scenario-panel .scenarios=${this.scenarios} .activeId=${this.activeScenario?.id ?? ''}></brm-scenario-panel>
            <brm-run-control .frozen=${s.flags?.frozen ?? false} .tick=${s.tick} .simTime=${s.t} .snapshots=${this.snapshots} .timeScale=${this.timeScale}></brm-run-control>
          </div>
          <div class="col center">
            <brm-track-plot class="plot" .state=${s} .config=${cfg} .history=${this.history} .noData=${!this.hasReceived}></brm-track-plot>
            <div class="bottom">
              <brm-environment-panel .state=${this.hasReceived ? s : null}></brm-environment-panel>
              <brm-fault-panel .active=${s.faults ?? []}></brm-fault-panel>
              <brm-ownship-panel .state=${this.hasReceived ? s : null}></brm-ownship-panel>
            </div>
          </div>
          <div class="col right">
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
              <div class="sum-note">選擇情境後按「載入」進入簡報;練習中以 BRM 標記鈕記錄觀察,結束後顯示自動講評摘要並可下載紀錄。</div>
              <div class="sum-note">${this.hasReceived ? `核心:${cfg.name},模擬時間 ${formatSimTime(s.t)}` : '等待核心資料(未連線時可切換示範模式)'}</div>
            </div>
            <brm-scenario-panel .scenarios=${this.scenarios} .activeId=${sc?.id ?? ''}></brm-scenario-panel>
          </div>
        </div>`;
      case 'briefing':
        return html`<div class="simple">
          <div class="stage briefing">
            <h2>${sc?.id} ${sc?.name} <span class="sum-note">${sc ? `${sc.shipId} · ${loadingLabel(sc.loading)}` : ''}</span></h2>
            <div class="big briefing-text">${sc ? briefingText(sc) : ''}</div>
            <div class="actions">
              <button class="primary start" @click=${this.simpleStart}>開始練習</button>
              <button @click=${this.simpleNew}>返回</button>
              <span class="sum-note">核心已載入情境並凍結;按「開始練習」恢復執行</span>
            </div>
          </div>
        </div>`;
      case 'running':
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
            </div>
            <brm-track-plot .state=${s} .config=${cfg} .history=${this.history} .noData=${!this.hasReceived}></brm-track-plot>
            <div class="side">
              <brm-marker-bar large .markers=${this.markers}></brm-marker-bar>
              <brm-operation-log .events=${this.opEvents} .max=${50}></brm-operation-log>
            </div>
            <div class="actions">
              <button class="freeze ${frozen ? 'frozen' : ''}" @click=${this.simpleToggleFreeze}>${frozen ? '恢復' : '凍結'}</button>
              <button class="end ${this.confirmEnd ? 'danger' : ''}" @click=${this.simpleEnd}>${this.confirmEnd ? '再按一次確認結束' : '結束練習'}</button>
              <span class="sum-note">${sc?.id} ${sc?.name} · 標記 ${this.markers.length} 筆 · 快速鍵 1–5</span>
            </div>
          </div>
        </div>`;
      case 'ended':
        return html`<div class="simple">
          <div class="stage ended">
            <h2>練習結束 — ${sc?.id} ${sc?.name}</h2>
            <div class="panel">
              <div class="panel-title"><span>自動講評摘要 DEBRIEF</span></div>
              <brm-debrief-summary large .summary=${this.debriefSummary}></brm-debrief-summary>
            </div>
            <brm-marker-bar .markers=${this.markers}></brm-marker-bar>
            <div class="actions">
              <button class="primary download" @click=${this.downloadRecord}>下載紀錄 JSON</button>
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
