// 航線/能見度方塊(規劃書第 7.2 節 conning display 的 XTD/下一航點最低資訊;設計文件第 6 節 W11):
// 由 state.route 顯示航跡偏差 XTD(右正左負,顯示為 L/R + 走廊半寬條)、下一航點名稱/距離/方位、航段航向、速限;
// state.visibility_nm 顯示能見度。核心無航線(無 route 欄位)時顯示「無航線」。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import type { RouteStatus } from '../types/state.js';
import { formatHeading } from '../lib/format.js';

@customElement('brm-route')
export class RouteReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
    css`
      .xtd {
        display: flex;
        align-items: baseline;
        gap: 4px;
      }
      .xtd .side {
        font-size: 12px;
        font-weight: 600;
        color: var(--ob-text-secondary);
      }
      .bar {
        position: relative;
        height: 8px;
        border: 1px solid var(--ob-border-strong);
        border-radius: 4px;
        margin: 3px 0;
        background: var(--ob-bg-input);
      }
      .bar .center {
        position: absolute;
        left: 50%;
        top: -2px;
        bottom: -2px;
        width: 1px;
        background: var(--ob-scale);
      }
      /* 位置圓點:樣板給 --pos(0% 至 100%;偏移到限值以上時是 0% 或 100%),夾在條內(兩端各留半個圓點),不超出條的邊框。
         單列與兩列都是(原本圓點的中心在條的端點上,超出 3 px,被判定為水平裁切;例:重播檢視載入 E09 夾具、時間軸最後) */
      .bar .pos {
        position: absolute;
        top: -1px;
        left: clamp(3px, var(--pos, 50%), calc(100% - 3px));
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: var(--ob-accent);
        transform: translateX(-50%);
      }
      .bar.over .pos {
        background: var(--ob-warning);
      }
      .rows {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 1px 6px;
        font-size: 11px;
        margin-top: 2px;
      }
      .rows .v {
        text-align: right;
        font-variant-numeric: tabular-nums;
        font-weight: 600;
      }
      /* 「下一航點」一列的外框:平常不產生方塊(標籤與數值照舊是格線的兩格);conning 條單列時才成為跨兩欄的一列(見下方) */
      .rows .wp {
        display: contents;
      }
      .value.warn {
        color: var(--ob-warning);
      }
      /* conning 條單列:
         - 航線偏移的數值、左右、限值放得下時同一行,放不下時「左右 / 限值」換到下一行;方塊窄於 216 px(「1568 m R 偏右 限 1500 m」
           一行放不下)時數值固定獨占一行,不隨偏移的位數(12 / 386 / 1568 m)跳動。數值字級不超過方塊寬度的 21%。
           原本不換行:11 格(方塊約 141 px)偏移三位數時「左右 / 限值」被方塊邊界切掉 11 至 26 px(第三十一輪);
         - 偏移六位數以上(100 km 以上;不合理但格式上可能)字級縮到方塊寬度的 17.5%;
         - 下方各列的數值欄可以比內容窄:放不下一行時在不拆開的各段(樣板的 .nb)之間換行(原本數值欄被長名稱撐寬,整個格線比方塊寬);
         - 「下一航點」一列用整個方塊的寬度(樣板的 .wp 跨兩欄):標籤、航點名稱、距離放得下一行時同一行(數值靠右,與其他列對齊);
           放不下時名稱與距離換到標籤的下一行(靠右),名稱比方塊寬時才在字間換行,距離接在名稱後面。這一列的數值若只用右欄
           (13 格時約 66 px),「FW1 0.98 nm」就要兩行、比方塊寬的名稱(約 18 個字元以上)要四行,多出來的一行在視窗不夠高時
           把最後一列(能見度)擠到方塊下緣之外 */
      @container style(--brm-conning-single: 1) {
        .xtd {
          flex-wrap: wrap;
          gap: 0 4px;
        }
        .xtd .value.mid {
          font-size: clamp(18px, min(1.6vw, 21cqi), 28px);
        }
        .xtd .value.mid.long {
          font-size: clamp(18px, min(1.6vw, 17.5cqi), 28px);
        }
        .xtd .side {
          white-space: nowrap;
        }
        .rows {
          grid-template-columns: auto minmax(0, 1fr);
        }
        .rows .v {
          min-width: 0;
          overflow-wrap: anywhere;
        }
        .rows .wp {
          grid-column: 1 / -1;
          display: flex;
          flex-wrap: wrap;
          justify-content: space-between;
          align-items: baseline;
          gap: 0 6px;
          min-width: 0;
        }
        .rows .wp .v {
          margin-left: auto;
        }
      }
      @container style(--brm-conning-single: 1) and (max-width: 215px) {
        .xtd .value {
          flex-basis: 100%;
        }
      }
      /* conning 條兩列(方塊約 107 至 135 px 高;兩列時航線方塊至少約 215 px 寬,conning-bar.ts 的第二列比例)。原本標題、偏移、位置條加四列
         比方塊高 16 至 99 px(原本的欄寬只有 126 至 182 px,「偏右 / 偏左」被擠成直排、下方各列折成兩行),最後一至三列在方塊下緣之外。
         - 偏移的數值、左右、限值在同一行(限值靠右);放不下(偏移五位數以上)時限值換到下一行。數值的行高 = 字級,字級另以方塊高度設上限(20cqb);
         - 位置條上下的間距 3 → 2 px;
         - 下方各列的行高固定 13 px:「下一航點」一列用整個方塊寬度(名稱與距離放不下時換到標籤的下一行);「方位 / 航段」一列;
           「速限」與「能見度」排在同一列 —— 四列變三列。這一列是兩組「標籤 + 數值」:「速限」在左、數值緊接在標籤後面(靠左),
           「能見度」與它的數值靠右,多出來的寬度留在兩組之間(格線的欄是 標籤 / 數值(吃剩下的寬度)/ 標籤 / 數值)。
           速限的數值若照其他列靠右,會離自己的標籤 200 px 以上、卻貼著「能見度」(畫面上讀成「速限 ……… 10 kn 能見度 5.0 nm」,
           第三十一輪審查發現);只有其中一項時那一項佔整列,數值照其他列靠右 */
      @container style(--brm-conning-single: 0) {
        .xtd {
          flex-wrap: wrap;
          gap: 0 4px;
        }
        .xtd .value.mid {
          font-size: clamp(18px, min(1.6vw, 20cqb), 28px);
        }
        .xtd .side {
          white-space: nowrap;
          line-height: 13px;
        }
        .xtd .label {
          margin-left: auto;
          line-height: 13px;
        }
        .bar {
          margin: 2px 0;
        }
        .rows {
          grid-template-columns: auto minmax(0, 1fr) auto auto;
          gap: 0 6px;
          line-height: 13px;
        }
        .rows .label {
          line-height: 13px;
        }
        .rows .v {
          min-width: 0;
          overflow-wrap: anywhere;
        }
        .rows .wp {
          grid-column: 1 / -1;
          display: flex;
          flex-wrap: wrap;
          justify-content: space-between;
          align-items: baseline;
          gap: 0 6px;
          min-width: 0;
        }
        .rows .wp .v {
          margin-left: auto;
        }
        .rows .label.k-brg {
          grid-column: 1 / span 2;
        }
        .rows .v.k-brg {
          grid-column: 3 / span 2;
        }
        /* 速限的數值緊接在「速限」後面(間距 6 px);「能見度」離前面的數值至少 18 px(欄距 6 px + 這裡的 12 px) */
        .rows .v.k-lim {
          text-align: left;
        }
        .rows .label.k-vis {
          padding-left: 12px;
        }
        /* 只有「速限」或只有「能見度」時,數值佔到方塊的右緣(靠右,與其他列相同) */
        .rows:not(:has(.k-lim)) .label.k-vis {
          padding-left: 0;
        }
        .rows:not(:has(.k-lim)) .v.k-vis,
        .rows:not(:has(.k-vis)) .v.k-lim {
          grid-column: 2 / -1;
          text-align: right;
        }
      }
    `,
  ];

  @property({ attribute: false }) route: RouteStatus | undefined = undefined;
  @property({ type: Number }) visibility: number | undefined = undefined;

  override render() {
    const r = this.route;
    const xtd = r?.xtd_m;
    const limit = r?.xtdLimit_m;
    const over = xtd !== undefined && limit !== undefined && limit > 0 && Math.abs(xtd) > limit;
    const frac = xtd !== undefined && limit !== undefined && limit > 0 ? Math.max(-1, Math.min(1, xtd / limit)) : 0;
    const xtdText = xtd === undefined ? '' : Math.abs(xtd) < 0.5 ? '0' : String(Math.round(Math.abs(xtd)));
    return html`
      <div class="tile">
        <div class="tile-title"><span>ROUTE</span><span class="zh">${r?.complete ? '航線完成' : r?.nextWaypoint ? `→ ${r.nextWaypoint}` : '航線'}</span></div>
        ${r && xtd !== undefined
          ? html`<div class="xtd">
                <span class="value mid ${over ? 'warn' : ''} ${xtdText.length >= 6 ? 'long' : ''}">${xtdText}<span class="unit">m</span></span>
                <span class="side">${xtd > 0.5 ? 'R 偏右' : xtd < -0.5 ? 'L 偏左' : '在線上'}</span>
                ${limit !== undefined ? html`<span class="label">限 ${Math.round(limit)} m</span>` : nothing}
              </div>
              ${limit !== undefined && limit > 0
                ? html`<div class="bar ${over ? 'over' : ''}" aria-label="XTD"><span class="center"></span><span class="pos" style="--pos:${50 + frac * 50}%"></span></div>`
                : nothing}`
          : html`<div class="label muted">${r ? 'XTD --' : '無航線'}</div>`}
        <div class="rows">
          ${r?.nextWaypoint ? html`<span class="wp"><span class="label">下一航點</span><span class="v">${r.nextWaypoint}${r.distanceToWaypoint_nm !== undefined ? html` <span class="nb">${r.distanceToWaypoint_nm.toFixed(2)} nm</span>` : nothing}</span></span>` : nothing}
          ${r?.bearingToWaypoint_deg !== undefined ? html`<span class="label k-brg">方位 / 航段</span><span class="v k-brg"><span class="nb">${formatHeading(r.bearingToWaypoint_deg)}${r.legCourse_deg !== undefined ? ` / ${formatHeading(r.legCourse_deg)}` : ''}</span></span>` : nothing}
          ${r?.speedLimit_kn !== undefined ? html`<span class="label k-lim">速限</span><span class="v k-lim"><span class="nb">${r.speedLimit_kn} kn</span></span>` : nothing}
          ${this.visibility !== undefined ? html`<span class="label k-vis">能見度</span><span class="v k-vis ${this.visibility < 1 ? 'warning' : ''}"><span class="nb">${this.visibility.toFixed(1)} nm</span></span>` : nothing}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-route': RouteReadout;
  }
}
