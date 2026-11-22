// <brm-external-readout>:操船台的精簡錨/拖船/靠泊讀數(規劃書第 7.2 節駕駛台顯示、第 8.3 節第 4 類靠泊指標;
// docs/design/physics-tugs-anchors-mooring.md 第 5 節):
//  - 錨:各錨狀態、鏈長(節/公尺)、錨鏈孔張力(走錨紅);兩錨都收存時只顯示一行;
//  - 拖船:名稱、狀態、實際出力 t 與力方向(相對船艏;斷纜紅);
//  - 靠泊:接近泊位時(船舯距碼頭面 < 500 m;無泊位幾何時以 state.berth 存在且 SOG < 4 kn 判斷)顯示艏/艉橫向速度,
//    有幾何時另顯示距碼頭與艏/艉朝碼頭的法向接近速度。state 沒有對應欄位,由 v、r 與 LPP 推算,畫面註明:
//    橫向速度為對水(船體座標);碼頭接近速度為對地(加 state.current 海流,與核心碰墊接觸速度、summary.berthing 同基準)。
// state 沒有 tugs/anchors/berth 時本元件不顯示任何內容(舊狀態、Python 參考伺服器)。
// 浮貼(floating)時由 <brm-app> 限制最大高度並讓本元件自己捲動;內容真的放不下(scrollHeight > clientHeight)時
// 本元件設 [scrollable] 屬性(ResizeObserver 觀察本元件與內容框),<brm-app> 依此才讓讀數接收滑鼠(捲得動);
// 放得下時不攔滑鼠,滾輪縮放與拖曳平移照常作用在下方的航跡圖(第二十九輪 L5;docs/design/web-layout.md 第 2.1 節)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import type { OwnShipState } from '../types/state.js';
import type { ShipDisplayConfig } from '../lib/ship-config.js';
import {
  ANCHOR_SIDE_LABEL,
  ANCHOR_STATUS_LABEL,
  BERTH_APPROACH_SHOW_M,
  BERTH_APPROACH_SHOW_SOG_KN,
  BERTHING_SPEED_CAUTION_MPS,
  TUG_STATUS_LABEL,
  chainText,
  lateralSpeeds,
  quayApproach,
  signedLateralText,
  tugDirectionText,
  tugName,
  type BerthGeometry,
} from '../lib/external-forces.js';

/** 操船台是否顯示靠泊讀數(由測試共用)。 */
export function showBerthing(s: OwnShipState, geometry: BerthGeometry | null, lpp_m: number): boolean {
  if (geometry) {
    const q = quayApproach(s, geometry, lpp_m);
    return q !== null && Math.abs(q.distance_m) < BERTH_APPROACH_SHOW_M;
  }
  return s.berth !== undefined && s.sog < BERTH_APPROACH_SHOW_SOG_KN;
}

/** 內容是否放不下(要捲動):scrollHeight 超過 clientHeight 1 px 以上(次像素進位不算) */
export function needsScroll(el: { scrollHeight: number; clientHeight: number }): boolean {
  return el.clientHeight > 0 && el.scrollHeight > el.clientHeight + 1;
}

@customElement('brm-external-readout')
export class ExternalReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        display: block;
      }
      :host([hidden]) {
        display: none;
      }
      :host([floating]) .box {
        background: color-mix(in srgb, var(--ob-bg-surface) 82%, transparent);
      }
      .box {
        display: flex;
        flex-direction: column;
        gap: 2px;
        background: var(--ob-bg-surface);
        border: 1px solid var(--ob-border);
        border-radius: 6px;
        padding: 4px 8px;
        font-size: 12px;
        font-variant-numeric: tabular-nums;
      }
      .row {
        display: flex;
        gap: 6px;
        align-items: baseline;
        flex-wrap: wrap;
      }
      .k {
        color: var(--ob-text-muted);
        min-width: 30px;
      }
      .v {
        font-weight: 600;
      }
      .big {
        font-size: 16px;
        font-weight: 700;
      }
      .alarm-text {
        color: var(--ob-alarm);
        font-weight: 700;
      }
      .caution-text {
        color: var(--ob-caution);
        font-weight: 700;
      }
      .note {
        font-size: 10px;
        color: var(--ob-text-muted);
      }
    `,
  ];

  @property({ attribute: false }) state: OwnShipState | null = null;
  @property({ attribute: false }) config: ShipDisplayConfig | null = null;
  /** 泊位幾何(情境 berth.definition;操船台依 state.scenario.id 由內建情境清單取得,無則 null) */
  @property({ attribute: false }) geometry: BerthGeometry | null = null;

  /** 觀察本元件(最大高度隨航跡圖變)與內容框(拖船 / 錨列數變) → 更新 [scrollable] */
  private resize: ResizeObserver | null = null;
  private observedBox: Element | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    if (typeof ResizeObserver === 'undefined') return;
    this.resize = new ResizeObserver(() => this.updateScrollable());
    this.resize.observe(this);
    // 重新接上 DOM 時內容框可能已存在(不一定會再 render)
    this.observedBox = this.shadowRoot?.querySelector('.box') ?? null;
    if (this.observedBox) this.resize.observe(this.observedBox);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.resize?.disconnect();
    this.resize = null;
    this.observedBox = null;
  }

  protected override updated(): void {
    // 內容框在沒有讀數時不存在(render 回 nothing),出現 / 重建時改觀察新的框;尺寸沒變時 ResizeObserver 不回呼,這裡不讀版面
    const box = this.shadowRoot?.querySelector('.box') ?? null;
    if (box === this.observedBox) return;
    if (this.observedBox) this.resize?.unobserve(this.observedBox);
    this.observedBox = box;
    if (box) this.resize?.observe(box);
    else this.toggleAttribute('scrollable', false);
  }

  /** 只在浮貼時標示(<brm-app> 的 CSS 依 [scrollable] 決定是否接收滑鼠);改屬性不改尺寸,不會讓 ResizeObserver 迴圈 */
  private updateScrollable(): void {
    const on = this.hasAttribute('floating') && needsScroll(this);
    if (on !== this.hasAttribute('scrollable')) this.toggleAttribute('scrollable', on);
  }

  private renderAnchors(s: OwnShipState) {
    const anchors = s.anchors ?? [];
    if (anchors.length === 0) return nothing;
    if (anchors.every((a) => a.status === 'stowed')) return html`<div class="row anchors"><span class="k">錨</span><span>${anchors.map((a) => ANCHOR_SIDE_LABEL[a.side]).join('、')}收存</span></div>`;
    return anchors
      .filter((a) => a.status !== 'stowed')
      .map(
        (a) => html`<div class="row anchor" data-side=${a.side}>
          <span class="k">${ANCHOR_SIDE_LABEL[a.side]}</span>
          <span class="v ${a.status === 'dragging' ? 'alarm-text' : ''}">${ANCHOR_STATUS_LABEL[a.status]}</span>
          <span class="v">${chainText(a)}</span>
          <span>張力 <span class="v">${a.tension_t.toFixed(1)} t</span></span>
        </div>`,
      );
  }

  private renderTugs(s: OwnShipState) {
    return (s.tugs ?? []).map((t) => {
      const parted = t.status === 'parted' || t.lineParted === true;
      return html`<div class="row tug" data-tug=${t.id}>
        <span class="k">${tugName(t)}</span>
        <span class="v ${parted ? 'alarm-text' : ''}">${parted ? '拖纜斷裂' : TUG_STATUS_LABEL[t.status]}</span>
        <span class="v">${t.force_t.toFixed(0)} t</span>
        <span>${Math.round(t.direction_deg).toString().padStart(3, '0')}° ${tugDirectionText(t.direction_deg)}</span>
      </div>`;
    });
  }

  private renderBerthing(s: OwnShipState, lpp: number) {
    if (!showBerthing(s, this.geometry, lpp)) return nothing;
    const lat = lateralSpeeds(s, lpp);
    const q = this.geometry ? quayApproach(s, this.geometry, lpp) : null;
    const cls = (v: number) => (v > BERTHING_SPEED_CAUTION_MPS ? 'caution-text' : '');
    // 避免顯示 −0.00
    const f2 = (v: number) => (Math.abs(v) < 0.005 ? '0.00' : v.toFixed(2));
    return html`<div class="row lateral">
        <span class="k">艏</span><span class="big bow">${signedLateralText(lat.bow_mps)}</span>
        <span class="k">艉</span><span class="big stern">${signedLateralText(lat.stern_mps)}</span><span>m/s 橫向</span>
      </div>
      ${q
        ? html`<div class="row approach">
            <span class="k">碼頭</span><span class="v">${Math.max(0, q.distance_m).toFixed(0)} m</span>
            <span>艏接近 <span class="v ${cls(q.bowApproach_mps)}">${f2(q.bowApproach_mps)}</span></span>
            <span>艉接近 <span class="v ${cls(q.sternApproach_mps)}">${f2(q.sternApproach_mps)}</span> m/s</span>
          </div>`
        : nothing}
      <div class="note">艏/艉速度由 v、r 與 LPP 推算(state 無對應欄位):橫向對水${q ? ';碼頭接近對地(含海流)' : ''}</div>
      ${s.berth?.quayContact ? html`<div class="row alarm-text">船體觸碰碼頭</div>` : nothing}`;
  }

  override render() {
    const s = this.state;
    if (!s || (s.tugs === undefined && s.anchors === undefined && s.berth === undefined)) return nothing;
    const lpp = this.config?.lpp_m ?? 100;
    return html`<div class="box" aria-label="錨、拖船與靠泊讀數">
      ${this.renderAnchors(s)} ${this.renderTugs(s)} ${this.renderBerthing(s, lpp)}
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-external-readout': ExternalReadout;
  }
}
