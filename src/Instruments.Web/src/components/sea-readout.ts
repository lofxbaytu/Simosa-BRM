// 浪與船間交互作用方塊(操船台環境區;docs/design/physics-waves.md 第 5 節、physics-ship-interaction.md 第 5 節):
//  - state.waves:Hs / Tp / 來向(艏向上的羅盤箭頭,由浪的來向指向中心,與風方塊相同慣例)與浪譜;
//  - state.shipInteraction:啟用時顯示主要目標與側向力 / 艏搖力矩(t、t·m),超過事件門檻(event)以警示色標示;
// 兩者皆無時 conning-bar 不放此方塊。練習中以 setEnvironment waves 關閉浪後 state.waves 省略 → 顯示「無浪」。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatBearing } from '../lib/format.js';
import { WAVE_SPECTRUM_LABELS, type ShipInteractionState, type WaveState } from '../types/state.js';

/** 側向力/力矩的顯示文字(帶正負號;右舷/艏向右為正)。 */
export function formatSignedTonnes(v: number | undefined, digits = 1): string {
  if (typeof v !== 'number' || !Number.isFinite(v)) return '--';
  const r = Number(v.toFixed(digits));
  const z = Object.is(r, -0) || r === 0 ? 0 : r;
  return `${z > 0 ? '+' : ''}${z.toFixed(digits)}`;
}

/** 浪的一行摘要(教官站環境面板標題、事件列也用):「Hs 2.0 m / Tp 7.0 s / 090°(PM)」。 */
export function formatWavesSummary(w: Pick<WaveState, 'significantHeight_m' | 'peakPeriod_s' | 'direction_deg'> & { spectrum?: string }): string {
  const spec = w.spectrum ? `(${WAVE_SPECTRUM_LABELS[w.spectrum as keyof typeof WAVE_SPECTRUM_LABELS] ?? w.spectrum})` : '';
  return `Hs ${w.significantHeight_m.toFixed(1)} m / Tp ${w.peakPeriod_s.toFixed(1)} s / ${formatBearing(w.direction_deg)}°${spec}`;
}

@customElement('brm-sea')
export class SeaReadout extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .body {
        flex: 1;
        display: flex;
        gap: 6px;
        align-items: center;
        min-height: 0;
      }
      .rose {
        width: 40px;
        height: 40px;
        flex: none;
      }
      .rows {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
      }
      .rows .value {
        font-size: clamp(13px, 0.95vw, 18px);
      }
      .ring {
        fill: none;
        stroke: var(--ob-scale-minor);
      }
      .arrow {
        stroke: var(--ob-vector-cog);
        stroke-width: 2.5;
        stroke-linecap: round;
        fill: var(--ob-vector-cog);
      }
      .bow {
        fill: var(--ob-scale);
      }
      .interaction {
        border-top: 1px solid var(--ob-border);
        margin-top: 2px;
        padding-top: 2px;
      }
      .interaction.event .value,
      .interaction.event .label {
        color: var(--ob-warning);
      }
      .interaction .value {
        font-size: clamp(12px, 0.85vw, 16px);
      }
    `,
  ];

  @property({ attribute: false }) waves: WaveState | undefined = undefined;
  @property({ attribute: false }) interaction: ShipInteractionState | undefined = undefined;
  @property({ type: Number }) heading = 0;

  override render() {
    const w = this.waves;
    const si = this.interaction;
    const rel = w ? w.direction_deg - this.heading : 0;
    const spec = w?.spectrum ? WAVE_SPECTRUM_LABELS[w.spectrum] : undefined;
    return html`
      <div class="tile">
        <div class="tile-title"><span>SEA</span><span class="zh">浪${spec ? html`<span class="spectrum" title="浪譜">(${spec})</span>` : nothing}</span></div>
        <div class="body">
          ${w
            ? html`<div class="rose">
                  <svg viewBox="0 0 50 50" aria-label="浪來向">
                    <circle class="ring" cx="25" cy="25" r="22" />
                    <polygon class="bow" points="25,1 22,7 28,7" />
                    <g transform="rotate(${rel} 25 25)">
                      <line class="arrow" x1="25" y1="6" x2="25" y2="30" />
                      <polygon class="arrow" points="25,36 20,26 30,26" />
                    </g>
                  </svg>
                </div>
                <div class="rows">
                  <div class="row"><span class="label">Hs / Tp</span><span class="value small waves-hs">${w.significantHeight_m.toFixed(1)}<span class="unit">m</span> ${w.peakPeriod_s.toFixed(1)}<span class="unit">s</span></span></div>
                  <div class="row"><span class="label">來向 / 相對</span><span class="value small waves-dir">${formatBearing(w.direction_deg)}° / ${formatBearing(rel)}°</span></div>
                </div>`
            : html`<div class="rows"><div class="label muted no-waves">無浪</div></div>`}
        </div>
        ${si
          ? html`<div class="interaction ${si.event ? 'event' : ''}" title="船間交互作用:側向力(右舷正)/ 艏搖力矩(艏向右正);超過門檻以警示色">
              <div class="row">
                <span class="label">交互作用${si.active && si.targetId ? ` ${si.targetId}` : ''}${si.event ? ' 超限' : ''}</span>
                <span class="value interaction-force">${si.active ? html`Y ${formatSignedTonnes(si.forceSway_t)}<span class="unit">t</span> N ${formatSignedTonnes(si.momentYaw_tm, 0)}<span class="unit">t·m</span>` : '無目標'}</span>
              </div>
            </div>`
          : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-sea': SeaReadout;
  }
}
