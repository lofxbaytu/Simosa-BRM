// 上排 conning 條:把各儀器方塊排成一列(1920 寬一列;窄於 1500 時折成兩列)。
// 核心有航線狀態(state.route)或能見度時加一格航線/能見度方塊(規劃書第 7.2 節 XTD/下一航點)。

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

@customElement('brm-conning-bar')
export class ConningBar extends LitElement {
  static override styles = css`
    :host {
      display: block;
      min-height: 0;
    }
    .bar {
      height: 100%;
      display: grid;
      /* 格序:HDG ROT SPEED RUDDER RPM BOW-THR UKC WIND CURRENT TIME/POS(ROUTE) */
      grid-template-columns: 1.5fr 1fr 1.1fr 1.5fr 1.4fr 1fr 1.2fr 1.5fr 1.6fr 1.5fr;
      gap: 6px;
    }
    .bar.with-route {
      /* 單列 11 格(教官站 1920 寬):風/流/時間格加寬,避免「真風 040° 12.0 kn」「流向 SET 200°」「23°48.004'N」被截斷 */
      grid-template-columns: 1.5fr 1fr 1.1fr 1.5fr 1.4fr 1fr 1.2fr 1.5fr 1.6fr 1.5fr 1.1fr;
    }
    @media (max-width: 1500px) {
      .bar,
      .bar.with-route {
        /* 兩列 × 6 格:第二列為 UKC / 風 / 流 / 時間 / 航線,第 2 欄(ROT / 風)略加寬 */
        grid-template-columns: 1.6fr 1.1fr 1.2fr 1.7fr 1.4fr 1.1fr;
        grid-template-rows: 1fr 1fr;
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
    // 感測器有效性(sensors-alerts.md 第 2.1 節):無效時各方塊標「無效」並淡化保持的最後值
    const sen = s.sensors;
    return html`
      <div class="bar ${withRoute ? 'with-route' : ''}">
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
          .squat=${s.squat}
          .draftFore=${s.draft?.fore}
          .draftAft=${s.draft?.aft}
          .aground=${s.flags?.aground ?? false}
          .valid=${!this.noData}
          .sensorInvalid=${sen ? !sen.depthValid : false}
        ></brm-depth>
        <brm-wind .trueDir=${s.wind.trueDir} .trueSpeed=${s.wind.trueSpeed} .relDir=${s.wind.relDir} .relSpeed=${s.wind.relSpeed} .heading=${s.heading}></brm-wind>
        <brm-current .set=${s.current.set} .drift=${s.current.drift} .heading=${s.heading}></brm-current>
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
