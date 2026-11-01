// 畫面錄影面板(docs/design/screen-recording.md 第 5 節;R6 影像擷取):開始/停止錄影、狀態(錄影中/上傳中/錯誤)、已上傳 MB、
// 離線緩衝與重試次數、錯誤訊息;設定「開始練習時自動開始」與「含系統音訊」(存 localStorage);列出本次工作階段已完成的錄影檔與長度。
// 實際錄影與上傳由 lib/screen-recording.ts 的控制器負責(教官站持有);本面板只發事件:
// recording-start / recording-stop / recording-settings-changed(detail: RecordingSettings)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, wallTime } from './instructor-shared.js';
import { formatMb, formatRecordingDuration, idleRecordingStatus, type RecordingSettings, type RecordingStatus, type RecordingSummary } from '../../lib/screen-recording.js';

const STATE_LABEL: Record<RecordingStatus['state'], string> = { idle: '未錄影', starting: '啟動中', recording: '錄影中', stopping: '停止中(上傳收尾)', error: '錯誤' };

@customElement('brm-recording-panel')
export class RecordingPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .status {
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        flex-wrap: wrap;
      }
      .dot {
        width: 10px;
        height: 10px;
        border-radius: 50%;
        background: var(--ob-text-muted);
        flex: none;
      }
      .dot.recording {
        background: var(--ob-alarm);
        animation: blink 1.2s infinite;
      }
      .dot.starting,
      .dot.stopping {
        background: var(--ob-caution);
      }
      .dot.error {
        background: var(--ob-alarm);
      }
      @keyframes blink {
        50% {
          opacity: 0.35;
        }
      }
      .stats {
        font-size: 11px;
        color: var(--ob-text-secondary);
        display: flex;
        flex-wrap: wrap;
        gap: 2px 10px;
      }
      .error {
        font-size: 11px;
        color: var(--ob-alarm);
      }
      .truncated {
        font-size: 11px;
        color: var(--ob-caution);
      }
      .settings {
        display: flex;
        flex-wrap: wrap;
        gap: 4px 10px;
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      .settings label {
        display: inline-flex;
        align-items: center;
        gap: 3px;
      }
      .list {
        font-size: 11px;
        max-height: 72px;
        overflow: auto;
      }
      .list .item {
        display: flex;
        gap: 6px;
        justify-content: space-between;
      }
      .list .file {
        font-family: ui-monospace, Consolas, monospace;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .unsupported {
        font-size: 11px;
        color: var(--ob-caution);
      }
      :host([compact]) .list,
      :host([compact]) .settings {
        display: none;
      }
    `,
  ];

  @property({ attribute: false }) status: RecordingStatus = idleRecordingStatus();
  @property({ attribute: false }) summaries: readonly RecordingSummary[] = [];
  @property({ attribute: false }) settings: RecordingSettings = { autoStart: false, withAudio: true };
  /** 瀏覽器支援 getDisplayMedia + MediaRecorder(false:顯示提示、停用按鈕) */
  @property({ type: Boolean }) supported = true;
  /** 錄影上傳端根網址(顯示用) */
  @property({ type: String }) baseUrl = '';
  @property({ type: Boolean, reflect: true }) compact = false;

  @state() private nowMs = Date.now();
  private tick: ReturnType<typeof setInterval> | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    this.tick = setInterval(() => (this.nowMs = Date.now()), 1000);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.tick) clearInterval(this.tick);
  }

  private start(): void {
    this.dispatchEvent(new CustomEvent('recording-start', { bubbles: true, composed: true }));
  }

  private stop(): void {
    this.dispatchEvent(new CustomEvent('recording-stop', { bubbles: true, composed: true }));
  }

  private setSetting(patch: Partial<RecordingSettings>): void {
    this.settings = { ...this.settings, ...patch };
    this.dispatchEvent(new CustomEvent<RecordingSettings>('recording-settings-changed', { detail: this.settings, bubbles: true, composed: true }));
  }

  private elapsed(): string {
    const s = this.status;
    if (!s.startedUtc || (s.state !== 'recording' && s.state !== 'stopping')) return '';
    const sec = Math.max(0, (this.nowMs - new Date(s.startedUtc).getTime()) / 1000);
    return formatRecordingDuration(sec);
  }

  override render() {
    const s = this.status;
    const busy = s.state === 'starting' || s.state === 'stopping';
    const recording = s.state === 'recording';
    const done = this.summaries.filter((x) => x.file);
    return html`
      <div class="panel">
        <div class="panel-title"><span>畫面錄影 RECORDING</span><span class="zh">${this.baseUrl ? `→ ${this.baseUrl}` : ''}</span></div>
        <div class="status">
          <span class="dot ${s.state}"></span>
          <span class="state-label">${STATE_LABEL[s.state]}</span>
          ${recording || s.state === 'stopping' ? html`<span class="elapsed">${this.elapsed()}</span>` : nothing}
          ${s.withAudio && (recording || busy) ? html`<span class="muted">含系統音訊</span>` : nothing}
        </div>
        <div class="btn-row">
          <button class="primary start" ?disabled=${!this.supported || recording || busy} @click=${this.start}>開始錄影</button>
          <button class="stop" ?disabled=${!recording} @click=${this.stop}>停止錄影</button>
        </div>
        ${this.supported ? nothing : html`<div class="unsupported">此瀏覽器不支援畫面擷取(需 Edge/Chrome 且以 http://127.0.0.1 或 https 開啟);請改用 OBS 等外部錄影。</div>`}
        ${recording || busy || s.uploadedBytes > 0
          ? html`<div class="stats">
              <span class="uploaded">已上傳 ${formatMb(s.uploadedBytes)}</span>
              <span>片段 ${s.chunks}</span>
              ${s.queuedBytes > 0 ? html`<span class="queued">緩衝 ${formatMb(s.queuedBytes)}</span>` : nothing}
              ${s.retries > 0 ? html`<span class="retries">重試 ${s.retries}</span>` : nothing}
            </div>`
          : nothing}
        ${s.truncated ? html`<div class="truncated">已達核心錄影上限(--media-max-mb),錄影提前結束;檔案只含上限內的片段</div>` : nothing}
        ${s.error ? html`<div class="error">${s.error}</div>` : nothing}
        <div class="settings">
          <label><input type="checkbox" class="auto-start" .checked=${this.settings.autoStart} @change=${(e: Event) => this.setSetting({ autoStart: (e.target as HTMLInputElement).checked })} />開始練習時自動開始(結束時停止)</label>
          <label><input type="checkbox" class="with-audio" .checked=${this.settings.withAudio} @change=${(e: Event) => this.setSetting({ withAudio: (e.target as HTMLInputElement).checked })} />含系統音訊</label>
        </div>
        ${done.length > 0
          ? html`<div class="list">
              ${done.map((x) => html`<div class="item" title=${x.mediaId}><span class="file">${x.file}</span><span>${wallTime(x.startedUtc)} · ${formatRecordingDuration(x.durationS)} · ${formatMb(x.uploadedBytes)}${x.truncated ? ' · 截斷' : ''}${x.error ? ' · 錯誤' : ''}</span></div>`)}
            </div>`
          : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-recording-panel': RecordingPanel;
  }
}
