// 時間與位置:本地時鐘、模擬時間(自練習開始)、步數、緯經度。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatClock, formatLatitude, formatLongitude, formatSimTime } from '../lib/format.js';

@customElement('brm-clock')
export class ClockReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .rows {
        flex: 1;
        display: flex;
        flex-direction: column;
        justify-content: space-evenly;
        gap: 1px;
      }
      .pos {
        font-size: 12px;
      }
    `,
  ];

  @property({ type: Number }) simTime = 0;
  @property({ type: Number }) tick = 0;
  @property({ type: Number }) lat: number | undefined = undefined;
  @property({ type: Number }) lon: number | undefined = undefined;
  @property({ type: Boolean }) frozen = false;
  /** GNSS 無效(sensors.positionValid = false):位置為最後有效值 */
  @property({ type: Boolean }) posInvalid = false;

  @state() private now = new Date();
  private timer: ReturnType<typeof setInterval> | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    this.timer = setInterval(() => (this.now = new Date()), 1000);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  override render() {
    return html`
      <div class="tile">
        <div class="tile-title"><span>TIME / POS</span><span class="zh">時間與位置${this.posInvalid ? html` <span class="alarm" title="GNSS 無效:顯示最後有效位置">GNSS 無效</span>` : nothing}</span></div>
        <div class="rows">
          <div class="row"><span class="label">時鐘</span><span class="value small">${formatClock(this.now)}</span></div>
          <div class="row"><span class="label">模擬</span><span class="value small ${this.frozen ? 'caution' : ''}">${formatSimTime(this.simTime)}${this.frozen ? html` <span class="label caution">凍結</span>` : nothing}</span></div>
          <div class="row pos"><span class="label">LAT</span><span class="value small" style=${this.posInvalid ? 'opacity:0.45' : ''}>${formatLatitude(this.lat)}</span></div>
          <div class="row pos"><span class="label">LON</span><span class="value small" style=${this.posInvalid ? 'opacity:0.45' : ''}>${formatLongitude(this.lon)}</span></div>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-clock': ClockReadout;
  }
}
