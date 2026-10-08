// 上排 conning 條:把各儀器方塊排成一列(1920 寬一列;窄於 1500 時折成兩列)。

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
      grid-template-columns: 1.7fr 1fr 1.1fr 1.8fr 1.5fr 1.1fr 1fr 1.2fr 1.1fr 1.3fr;
      gap: 6px;
    }
    @media (max-width: 1500px) {
      .bar {
        grid-template-columns: 1.7fr 1fr 1.1fr 1.8fr 1.5fr;
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
    return html`
      <div class="bar">
        <brm-heading .heading=${s.heading} .setHeading=${this.setHeading} .cog=${s.cog} .suspect=${headingSuspect}></brm-heading>
        <brm-rot .rot=${s.rot} .range=${30}></brm-rot>
        <brm-speed .stw=${s.stw} .sog=${s.sog} .cog=${s.cog} .drift=${s.drift}></brm-speed>
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
        <brm-thruster .order=${s.thruster?.order ?? 0} .actual=${s.thruster?.actual ?? 0} .available=${!(s.faults ?? []).includes('thruster')}></brm-thruster>
        <brm-depth
          .ukc=${s.depthBelowKeel}
          .waterDepth=${s.waterDepth}
          .squat=${s.squat}
          .draftFore=${s.draft?.fore}
          .draftAft=${s.draft?.aft}
          .aground=${s.flags?.aground ?? false}
          .valid=${!this.noData}
        ></brm-depth>
        <brm-wind .trueDir=${s.wind.trueDir} .trueSpeed=${s.wind.trueSpeed} .relDir=${s.wind.relDir} .relSpeed=${s.wind.relSpeed} .heading=${s.heading}></brm-wind>
        <brm-current .set=${s.current.set} .drift=${s.current.drift} .heading=${s.heading}></brm-current>
        <brm-clock .simTime=${s.t} .tick=${s.tick} .lat=${s.pos.lat} .lon=${s.pos.lon} .frozen=${s.flags?.frozen ?? false}></brm-clock>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-conning-bar': ConningBar;
  }
}
