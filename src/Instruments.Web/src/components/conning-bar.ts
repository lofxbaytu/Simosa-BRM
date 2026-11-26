// 上排 conning 條:把各儀器方塊排成一列(視窗寬於 1900 px 時一列 10 至 13 格;1900 px 以下折成兩列)。
// 核心有航線狀態(state.route)或能見度時加一格航線/能見度方塊(規劃書第 7.2 節 XTD/下一航點);
// 有浪(state.waves)或船間交互作用(state.shipInteraction)時再加一格 SEA 方塊(physics-waves.md 第 5 節);
// 有浪一階運動(state.waveMotion;情境 ship.seakeeping.enabled 且有浪,需求 R2)時 SEA 方塊多一列縱搖/升沉(契約上必與 state.waves 同時出現,
// 單獨出現時也放 SEA 方塊以免資料被吞掉);無 waveMotion 時版面與原本相同;
// 有橫搖(state.roll;情境 ship.roll.enabled)時再加一格傾斜儀 HEEL 方塊(inclinometer.ts),無 roll 時版面與原本相同。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import type { OwnShipState } from '../types/state.js';
import type { ShipDisplayConfig } from '../lib/ship-config.js';
import { telegraphStep } from '../lib/ship-config.js';
import './heading-display.js';
import './rot-indicator.js';
import './speed-readout.js';
import './rudder-indicator.js';
import './engine-readout.js';
import './thruster-indicator.js';
import './depth-readout.js';
import './wind-readout.js';
import './current-readout.js';
import './clock-readout.js';
import './route-readout.js';
import './sea-readout.js';
import './inclinometer.js';

@customElement('brm-conning-bar')
export class ConningBar extends LitElement {
  static override styles = css`
    :host {
      display: block;
      min-height: 0;
    }
    /* 單列(視窗寬於 1900 px):10 至 13 格排成一列。每格 =「基本寬度 + 依比例分到的多餘寬度」(flex: 比例 1 基本寬度)。
       格序:HDG ROT SPEED RUDDER RPM BOW-THR UKC WIND CURRENT (SEA) (HEEL) TIME/POS (ROUTE)。
       基本寬度是 13 格、視窗 1901 px(列寬 1889 − 12 個間距 = 1817 px)時各格放得下內容的寬度,13 格的總和就是 1817 px;
       格數較少時多出來的寬度依比例分配(航向帶、舵角刻度、風 / 流的玫瑰圖分得多,只有數字的 ROT / 艏側推分得少;
       10 格時各格的寬度與第三十一輪之前相近)。各方塊在這個寬度下的字級與排法寫在各元件的
       @container style(--brm-conning-single: 1) 區塊裡(shared.ts 的 conningTileStyles 有說明);量測與餘裕的作法見
       docs/design/web-layout.md 第 2.5 節。
       第三十一輪之前是每種格數組合各寫一份 grid-template-columns(八種);「有浪、有橫搖、沒有航線」與「有航線、有橫搖」共用一份,
       浪 / 傾斜儀 / 時間三格的寬度因此對錯了格(時間格只有 134 px),而且 13 格時主機、UKC、航線等格比內容窄 */
    .bar {
      height: 100%;
      display: flex;
      gap: 6px;
      /* 告訴各方塊「現在是單列」:方塊裡單列專用的規則以 @container style(--brm-conning-single: 1) 包住 */
      --brm-conning-single: 1;
    }
    /* 每個方塊是尺寸容器:方塊內的字級與排法可依「方塊自己的寬度」調整(@container / cqi) */
    .bar > * {
      container-type: inline-size;
    }
    brm-heading {
      flex: 2 1 148px;
    }
    brm-rot {
      flex: 0.7 1 117px;
    }
    brm-speed {
      flex: 1 1 120px;
    }
    brm-rudder {
      flex: 2 1 137px;
    }
    brm-engine {
      flex: 1.3 1 158px;
    }
    brm-thruster {
      flex: 0.7 1 123px;
    }
    brm-depth {
      flex: 0.8 1 155px;
    }
    brm-wind {
      flex: 2 1 139px;
    }
    brm-current {
      flex: 2 1 134px;
    }
    brm-sea {
      flex: 1.5 1 150px;
    }
    brm-inclinometer {
      flex: 1 1 132px;
    }
    brm-clock {
      flex: 1.5 1 158px;
    }
    brm-route {
      flex: 1.5 1 146px;
    }
    /* 寬度 ≤ 1900 px 折成兩列:單列時每格太窄 — 包含 1920×1080 筆電在 Windows 125% 縮放下的 1536 px。
       app.ts / instructor-app.ts / replay-view.ts 同一門檻加高該列。兩列的格線與各方塊的排法和第三十一輪之前相同
       (--brm-conning-single: 0,方塊裡單列專用的規則不套用) */
    @media (max-width: 1900px) {
      .bar {
        display: grid;
        --brm-conning-single: 0;
        /* 兩列 × 6 格:第二列為 UKC / 風 / 流 / (浪) / 時間 / 航線,第 2 欄(ROT / 風)略加寬 */
        grid-template-columns: 1.6fr 1.1fr 1.2fr 1.7fr 1.4fr 1.1fr;
        grid-template-rows: 1fr 1fr;
      }
      .bar.with-roll {
        /* 有傾斜儀時兩列 × 7 格 */
        grid-template-columns: 1.5fr 1.1fr 1.2fr 1.6fr 1.4fr 1.1fr 1.2fr;
      }
    }
  `;

  @property({ attribute: false }) state!: OwnShipState;
  @property({ attribute: false }) config!: ShipDisplayConfig;
  @property({ type: Number }) setHeading: number | undefined = undefined;
  @property({ attribute: false }) issues: string[] = [];
  /** 尚未收到任何狀態(即時模式未連線時) */
  @property({ type: Boolean }) noData = false;

  override render() {
    const s = this.state;
    const cfg = this.config;
    const step = telegraphStep(cfg, s.telegraph);
    const headingSuspect = this.noData || this.issues.includes('heading');
    const withRoute = s.route !== undefined || s.visibility_nm !== undefined;
    const withSea = s.waves !== undefined || s.shipInteraction !== undefined || s.waveMotion !== undefined;
    const withRoll = s.roll !== undefined;
    // 感測器有效性(sensors-alerts.md 第 2.1 節):無效時各方塊標「無效」並淡化保持的最後值
    const sen = s.sensors;
    return html`
      <div class="bar ${withRoute ? 'with-route' : ''} ${withSea ? 'with-sea' : ''} ${withRoll ? 'with-roll' : ''}">
        <brm-heading .heading=${s.heading} .setHeading=${this.setHeading} .cog=${s.cog} .suspect=${headingSuspect} .invalid=${sen ? !sen.headingValid : false}></brm-heading>
        <brm-rot .rot=${s.rot} .range=${30}></brm-rot>
        <brm-speed .stw=${s.stw} .sog=${s.sog} .cog=${s.cog} .drift=${s.drift} .stwInvalid=${sen ? !sen.stwValid : false} .gnssInvalid=${sen ? !sen.positionValid : false}></brm-speed>
        <brm-rudder .rudder=${s.rudder} .order=${s.rudderOrder} .max=${cfg.rudderMax_deg} .normalMax=${cfg.rudderNormalMax_deg}></brm-rudder>
        <brm-engine
          .rpm=${s.rpm}
          .rpmOrder=${s.rpmOrder}
          .rpmMax=${cfg.rpmMax}
          .telegraph=${s.telegraph}
          .telegraphLabel=${step.label}
          .engineState=${s.engine?.state}
          .loadPct=${s.engine?.load_pct}
        ></brm-engine>
        <brm-thruster .order=${s.thruster?.order ?? 0} .actual=${s.thruster?.actual ?? 0} .available=${!(s.faults ?? []).some((f) => f === 'thruster' || f === 'bowThruster')}></brm-thruster>
        <brm-depth
          .ukc=${s.depthBelowKeel}
          .waterDepth=${s.waterDepth}
          .tideHeight=${s.tideHeight_m}
          .squat=${s.squat}
          .draftFore=${s.draft?.fore}
          .draftAft=${s.draft?.aft}
          .aground=${s.flags?.aground ?? false}
          .valid=${!this.noData}
          .sensorInvalid=${sen ? !sen.depthValid : false}
        ></brm-depth>
        <brm-wind .trueDir=${s.wind.trueDir} .trueSpeed=${s.wind.trueSpeed} .relDir=${s.wind.relDir} .relSpeed=${s.wind.relSpeed} .heading=${s.heading}></brm-wind>
        <brm-current .set=${s.current.set} .drift=${s.current.drift} .heading=${s.heading} .source=${s.currentSource}></brm-current>
        ${withSea ? html`<brm-sea .waves=${s.waves} .interaction=${s.shipInteraction} .motion=${s.waveMotion} .heading=${s.heading}></brm-sea>` : ''}
        ${withRoll ? html`<brm-inclinometer .roll=${s.roll}></brm-inclinometer>` : ''}
        <brm-clock .simTime=${s.t} .tick=${s.tick} .lat=${s.pos.lat} .lon=${s.pos.lon} .frozen=${s.flags?.frozen ?? false} .posInvalid=${sen ? !sen.positionValid : false}></brm-clock>
        ${withRoute ? html`<brm-route .route=${s.route} .visibility=${s.visibility_nm}></brm-route>` : ''}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-conning-bar': ConningBar;
  }
}
