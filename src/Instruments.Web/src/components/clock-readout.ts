// 時間與位置:本地時鐘、模擬時間(自練習開始)、步數、緯經度。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatClock, formatLatitude, formatLongitude, formatSimTime } from '../lib/format.js';

@customElement('brm-clock')
export class ClockReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
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
      /* 經緯度(最長 12 個字元,例如 120°20.274'E):字級不超過方塊寬度的 10%,窄方塊不被裁切(conning 條內方塊是尺寸容器) */
      .pos .value.small {
        font-size: clamp(11px, min(1.1vw, 10cqi), 20px);
      }
      /* conning 條單列:時鐘與模擬時間(8 個字元)的字級不超過方塊寬度的 12.5%;經緯度(最長 12 個字元,例如 179°59.999'W)
         不超過 9.3%(13 格、方塊約 158 px 時約 14.7 px;原本 10%,CI 的字型下東經的「120°20.274'E」就比方塊寬 4 至 8 px)。
         再放不下時(西經多一個較寬的 W、比預期寬的字型)數值換到標籤的下一行(conningTileStyles 的 .row)。
         模擬時間列:時間留在標籤的同一行,「凍結」放不下時換到時間的下一行(靠右)—— 數值的基本寬度取最小內容寬(只算時間),
         再撐滿這一行,所以時間不會因為凍結而跳到下一行 */
      @container style(--brm-conning-single: 1) {
        .rows .value.small {
          font-size: clamp(14px, min(1.1vw, 12.5cqi), 20px);
        }
        .pos .value.small {
          font-size: clamp(11px, min(1.1vw, 9.3cqi), 20px);
        }
        .row.sim > .value {
          flex: 1 1 min-content;
          white-space: normal;
          text-align: right;
        }
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
          <div class="row sim"><span class="label">模擬</span><span class="value small ${this.frozen ? 'caution' : ''}"><span class="nb">${formatSimTime(this.simTime)}</span>${this.frozen ? html` <span class="label caution">凍結</span>` : nothing}</span></div>
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
