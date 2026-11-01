// <brm-vhf>:內建 VHF 角色扮演面板(操船台與教官站共用;docs/design/vhf-roleplay.md 第 3 節):
// 頻道旋鈕(16/12/13/14/06/08/10/67/68/69/71/72/73/77)、音量、PTT(按住滑鼠/觸控/空白鍵)、接收指示燈與發話者站名、
// 文字訊息列與輸入(無麥克風時的備援;教官扮演 VTS/引水人/他船)、DSC 兩鍵(DISTRESS/URGENCY,需二次確認)。
// 教官站 dualWatch 可同時守聽 16 + 工作頻道。狀態機與傳輸在 lib/vhf.ts、lib/vhf-client.ts;本元件只負責畫面與輸入。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { VhfClient, type VhfClientOptions } from '../lib/vhf-client.js';
import { DSC_CATEGORY_LABEL, DEFAULT_VHF_URL, VHF_CHANNELS, VHF_STATE_LABEL, type VhfDscCategory, type VhfLogItem } from '../lib/vhf.js';

const CONNECTION_LABEL = { connected: '已連線', connecting: '連線中', disconnected: '未連線' } as const;
const MIC_LABEL = {
  unknown: '麥克風:按 PTT 時要求授權',
  requesting: '麥克風:等待授權…',
  granted: '麥克風:已授權',
  denied: '無麥克風權限:只能以文字通訊(瀏覽器網址列允許麥克風,或 Windows 隱私設定;安裝文件第 2.7 節)',
  unavailable: '此環境沒有麥克風 API:只能以文字通訊(需 https 或 localhost/127.0.0.1;安裝文件第 2.7 節)',
} as const;
/** DSC 二次確認的有效秒數 */
const DSC_ARM_MS = 6000;

function clock(ms: number): string {
  const d = new Date(ms);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
}

export function vhfLogText(item: VhfLogItem): string {
  switch (item.kind) {
    case 'call':
      return `${item.direction === 'tx' ? '本站發話' : `${item.station} 發話`} ${item.duration_s?.toFixed(1) ?? '?'} s`;
    case 'text':
      return `${item.direction === 'tx' ? '本站' : item.station}:${item.text ?? ''}`;
    case 'dsc':
      return `DSC ${DSC_CATEGORY_LABEL[item.category ?? 'routine']} — ${item.direction === 'tx' ? '本站' : item.station}${item.text ? `:${item.text}` : ''}`;
    case 'busy':
    case 'system':
    default:
      return item.text ?? '';
  }
}

@customElement('brm-vhf')
export class VhfPanel extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        display: block;
      }
      .tile {
        gap: 5px;
      }
      .status {
        display: flex;
        gap: 6px;
        align-items: center;
        flex-wrap: wrap;
        font-size: 11px;
      }
      .pill {
        border-radius: 10px;
        padding: 0 7px;
        line-height: 18px;
        border: 1px solid var(--ob-border-strong);
        color: var(--ob-text-secondary);
      }
      .pill.connected {
        border-color: var(--ob-ok);
        color: var(--ob-ok);
      }
      .pill.connecting {
        color: var(--ob-caution);
      }
      .pill.disconnected {
        color: var(--ob-alarm);
        border-color: var(--ob-alarm);
      }
      .lamp {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        font-weight: 600;
      }
      .lamp::before {
        content: '';
        width: 10px;
        height: 10px;
        border-radius: 50%;
        background: var(--ob-border-strong);
      }
      .lamp.receiving::before {
        background: var(--ob-ok);
        box-shadow: 0 0 6px var(--ob-ok);
      }
      .lamp.transmitting::before {
        background: var(--ob-alarm);
        box-shadow: 0 0 6px var(--ob-alarm);
      }
      .lamp.busy::before {
        background: var(--ob-warning);
      }
      .channels {
        display: flex;
        flex-wrap: wrap;
        gap: 3px;
        align-items: center;
      }
      .channels button {
        min-width: 32px;
        min-height: 22px;
        padding: 0 5px;
        font-size: 12px;
        font-variant-numeric: tabular-nums;
      }
      .channels button.occupied {
        border-color: var(--ob-caution);
      }
      .channels .label {
        margin-right: 2px;
      }
      .watch select {
        min-height: 22px;
        padding: 1px 4px;
        font-size: 12px;
      }
      .controls {
        display: grid;
        grid-template-columns: 1fr auto;
        gap: 6px;
        align-items: center;
      }
      .ptt {
        min-height: 44px;
        font-size: 15px;
        font-weight: 700;
        letter-spacing: 0.06em;
        touch-action: none;
        user-select: none;
      }
      .ptt.transmitting {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
        border-color: var(--ob-alarm);
      }
      .ptt.busy {
        background: var(--ob-warning);
        color: var(--ob-text-on-accent);
      }
      .volume {
        display: flex;
        flex-direction: column;
        align-items: center;
        font-size: 10px;
        color: var(--ob-text-secondary);
      }
      .volume input {
        width: 70px;
        min-height: 0;
        padding: 0;
      }
      .log {
        flex: 1 1 auto;
        min-height: 54px;
        max-height: 150px;
        overflow-y: auto;
        font-size: 11px;
        display: flex;
        flex-direction: column;
        gap: 1px;
      }
      :host([compact]) .log {
        max-height: 90px;
      }
      .log .item {
        display: flex;
        gap: 5px;
        align-items: baseline;
        padding: 1px 3px;
        border-left: 3px solid var(--ob-border);
        border-radius: 2px;
      }
      .log .item.tx {
        border-left-color: var(--ob-accent);
      }
      .log .item.rx {
        border-left-color: var(--ob-ok);
      }
      .log .item.warning .text {
        color: var(--ob-warning);
      }
      .log .item.alarm {
        background: color-mix(in srgb, var(--ob-alarm) 14%, transparent);
      }
      .log .item.alarm .text {
        color: var(--ob-alarm);
        font-weight: 600;
      }
      .log .time {
        color: var(--ob-text-muted);
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }
      .log .ch {
        color: var(--ob-text-muted);
        white-space: nowrap;
        min-width: 22px;
      }
      .log .text {
        overflow-wrap: anywhere;
      }
      .text-row {
        display: flex;
        gap: 4px;
      }
      .text-row input {
        flex: 1;
        min-width: 0;
      }
      .dsc {
        display: flex;
        gap: 4px;
        align-items: center;
        font-size: 11px;
      }
      .dsc button {
        min-height: 22px;
        padding: 0 7px;
        font-size: 11px;
        font-weight: 600;
      }
      .dsc button.distress {
        border-color: var(--ob-alarm);
        color: var(--ob-alarm);
      }
      .dsc button.urgency {
        border-color: var(--ob-warning);
        color: var(--ob-warning);
      }
      .dsc button.armed {
        background: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      .dsc button.armed.urgency {
        background: var(--ob-warning);
        color: var(--ob-text-on-accent);
      }
      .hint {
        font-size: 10px;
        color: var(--ob-text-muted);
      }
      .hint.warn {
        color: var(--ob-warning);
      }
    `,
  ];

  /** VHF 轉送伺服器網址(ws://<核心主機>:8767/) */
  @property() url = DEFAULT_VHF_URL;
  /** 站名(?station=;教官站 instructor) */
  @property() station = 'bridge';
  /** 初始頻道 */
  @property() channel = '16';
  /** 教官站:可另外守聽一個頻道(16 + 工作頻道) */
  @property({ type: Boolean }) dualWatch = false;
  @property({ type: Boolean, reflect: true }) compact = false;
  /** 不自動連線(測試或外部注入 client) */
  @property({ type: Boolean }) manual = false;
  /** 可注入的客戶端(測試用;未給時依 url/station 建立) */
  @property({ attribute: false }) client: VhfClient | null = null;
  /** 建立客戶端時的額外選項(測試注入假 WebSocket/錄音/播放) */
  @property({ attribute: false }) clientOptions: Partial<VhfClientOptions> = {};

  @state() private draft = '';
  @state() private armed: VhfDscCategory | null = null;
  @state() private volume = 1;
  @state() private tick = 0;

  private unsubscribe: (() => void) | null = null;
  private ownClient = false;
  private armTimer: ReturnType<typeof setTimeout> | null = null;
  private keyDown = false;

  override connectedCallback(): void {
    super.connectedCallback();
    if (!this.client && !this.manual) {
      this.client = new VhfClient({ ...this.clientOptions, url: this.url, station: this.station, channel: this.channel, watch: this.dualWatch ? ['16'].filter((c) => c !== this.channel) : [] });
      this.ownClient = true;
    }
    this.attach();
    window.addEventListener('keydown', this.onKeyDown);
    window.addEventListener('keyup', this.onKeyUp);
    window.addEventListener('blur', this.onWindowBlur);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    window.removeEventListener('keydown', this.onKeyDown);
    window.removeEventListener('keyup', this.onKeyUp);
    window.removeEventListener('blur', this.onWindowBlur);
    this.unsubscribe?.();
    this.unsubscribe = null;
    if (this.ownClient) {
      this.client?.stop();
      this.client = null;
      this.ownClient = false;
    }
    if (this.armTimer) clearTimeout(this.armTimer);
  }

  override willUpdate(changed: Map<PropertyKey, unknown>): void {
    if (changed.has('client') && this.client && !this.unsubscribe) this.attach();
  }

  private attach(): void {
    const c = this.client;
    if (!c) return;
    this.unsubscribe?.();
    this.unsubscribe = c.onChange(() => {
      this.tick++;
    });
    this.volume = c.volumeLevel;
    if (!this.manual) c.start();
  }

  // ---------------------------------------------------------------- PTT(滑鼠/觸控/空白鍵)
  private readonly onKeyDown = (ev: KeyboardEvent): void => {
    if (ev.code !== 'Space' || ev.repeat || this.keyDown) return;
    const target = ev.composedPath()[0];
    if (target instanceof HTMLElement && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return;
    ev.preventDefault();
    this.keyDown = true;
    void this.client?.pttDown();
  };

  private readonly onKeyUp = (ev: KeyboardEvent): void => {
    if (ev.code !== 'Space' || !this.keyDown) return;
    this.keyDown = false;
    void this.client?.pttUp();
  };

  private readonly onWindowBlur = (): void => {
    this.keyDown = false;
    void this.client?.pttUp();
  };

  private pttDown(ev: Event): void {
    ev.preventDefault();
    (ev.currentTarget as HTMLElement | null)?.focus?.();
    void this.client?.pttDown();
  }

  private pttUp(ev: Event): void {
    ev.preventDefault();
    void this.client?.pttUp();
  }

  // ---------------------------------------------------------------- 文字 / DSC
  private sendText(): void {
    const t = this.draft.trim();
    if (!t) return;
    this.client?.sendText(t);
    this.draft = '';
  }

  private onTextKey(ev: KeyboardEvent): void {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      this.sendText();
    }
    ev.stopPropagation();
  }

  private dsc(category: VhfDscCategory): void {
    if (this.armed === category) {
      this.client?.sendDsc(category, this.draft.trim());
      this.draft = '';
      this.disarm();
      return;
    }
    this.armed = category;
    if (this.armTimer) clearTimeout(this.armTimer);
    this.armTimer = setTimeout(() => this.disarm(), DSC_ARM_MS);
  }

  private disarm(): void {
    this.armed = null;
    if (this.armTimer) {
      clearTimeout(this.armTimer);
      this.armTimer = null;
    }
  }

  private setVolume(ev: Event): void {
    const v = Number((ev.target as HTMLInputElement).value) / 100;
    this.volume = v;
    this.client?.setVolume(v);
  }

  override render() {
    const c = this.client;
    const m = c?.machine;
    const state = m?.state ?? 'idle';
    const conn = c?.connection ?? 'disconnected';
    const log = (m?.log ?? []).slice(-60);
    const rx = m?.receivingFrom;
    const busy = m?.busyBy;
    const canPtt = !!c && c.machine.joined && c.mic !== 'unavailable' && c.mic !== 'denied';
    void this.tick;
    return html`
      <div class="tile">
        <div class="tile-title">
          <span>VHF <span class="zh">角色扮演頻道</span></span>
          <span class="status">
            <span class="pill ${conn}" title=${c?.url ?? ''}>${CONNECTION_LABEL[conn]}</span>
            <span class="lamp ${state}" aria-live="polite">${state === 'receiving' && rx ? `RX ${rx.station} CH${rx.channel}` : state === 'busy' && busy ? `佔用 ${busy.station}` : VHF_STATE_LABEL[state]}</span>
          </span>
        </div>
        <div class="channels" role="group" aria-label="頻道">
          <span class="label">CH</span>
          ${VHF_CHANNELS.map((ch) => {
            const occupant = m?.occupantOf(ch);
            return html`<button class="${m?.channel === ch ? 'active' : ''} ${occupant ? 'occupied' : ''}" title=${occupant ? `${occupant.station} 發話中` : `頻道 ${ch}`} @click=${() => c?.setChannel(ch)}>${ch}</button>`;
          })}
          ${this.dualWatch
            ? html`<label class="watch">守聽
                <select .value=${m?.watch[0] ?? ''} @change=${(e: Event) => c?.setWatch((e.target as HTMLSelectElement).value || null)}>
                  <option value="">—</option>
                  ${VHF_CHANNELS.filter((ch) => ch !== m?.channel).map((ch) => html`<option value=${ch} ?selected=${m?.watch[0] === ch}>${ch}</option>`)}
                </select></label>`
            : nothing}
        </div>
        <div class="controls">
          <button
            class="ptt ${state}"
            ?disabled=${!canPtt}
            title=${canPtt ? '按住說話(滑鼠/觸控/空白鍵)' : (c ? MIC_LABEL[c.mic] : '未連線')}
            @pointerdown=${this.pttDown}
            @pointerup=${this.pttUp}
            @pointercancel=${this.pttUp}
            @pointerleave=${(e: PointerEvent) => (state === 'transmitting' ? this.pttUp(e) : undefined)}
            @contextmenu=${(e: Event) => e.preventDefault()}
          >
            ${state === 'transmitting' ? `TX CH${m?.channel}` : state === 'busy' ? '頻道佔用' : `PTT CH${m?.channel ?? ''}`}
          </button>
          <label class="volume">音量 ${Math.round(this.volume * 100)}
            <input type="range" min="0" max="100" .value=${String(Math.round(this.volume * 100))} @input=${this.setVolume} />
          </label>
        </div>
        ${c && c.mic !== 'granted' ? html`<div class="hint ${c.mic === 'denied' || c.mic === 'unavailable' ? 'warn' : ''}">${MIC_LABEL[c.mic]}${c.micError ? `(${c.micError})` : ''}${c.playback === 'none' ? ';此環境無法播放音訊,只收文字' : ''}</div>` : nothing}
        <div class="log" aria-label="VHF 訊息">
          ${log.length === 0 ? html`<div class="hint">尚無通話;同頻道的語音會自動播放,文字訊息列於此</div>` : nothing}
          ${log.map((item) => html`<div class="item ${item.direction} ${item.severity}" data-kind=${item.kind}><span class="time">${clock(item.at)}</span><span class="ch">${item.kind === 'system' ? '' : `CH${item.channel}`}</span><span class="text">${vhfLogText(item)}</span></div>`)}
        </div>
        <div class="text-row">
          <input
            type="text"
            placeholder=${`文字訊息(CH${m?.channel ?? ''};無麥克風時的備援)`}
            .value=${this.draft}
            maxlength="500"
            @input=${(e: Event) => (this.draft = (e.target as HTMLInputElement).value)}
            @keydown=${this.onTextKey}
            @keyup=${(e: Event) => e.stopPropagation()}
          />
          <button class="send" ?disabled=${!m?.joined || !this.draft.trim()} @click=${this.sendText}>送出</button>
        </div>
        <div class="dsc">
          <span class="label">DSC</span>
          <button class="distress ${this.armed === 'distress' ? 'armed' : ''}" ?disabled=${!m?.joined} @click=${() => this.dsc('distress')}>${this.armed === 'distress' ? '再按一次送出 DISTRESS' : 'DISTRESS'}</button>
          <button class="urgency ${this.armed === 'urgency' ? 'armed' : ''}" ?disabled=${!m?.joined} @click=${() => this.dsc('urgency')}>${this.armed === 'urgency' ? '再按一次送出 URGENCY' : 'URGENCY'}</button>
          ${this.armed ? html`<button class="cancel" @click=${() => this.disarm()}>取消</button><span class="hint warn">文字欄內容會附在 DSC 警示;${DSC_ARM_MS / 1000} s 內未確認自動取消</span>` : html`<span class="hint">簡化 DSC:文字警示送所有站(非真實 DSC 編碼)</span>`}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-vhf': VhfPanel;
  }
}
