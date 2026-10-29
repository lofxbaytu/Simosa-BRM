// 傾斜儀(橫搖指示;state.roll,情境 ship.roll.enabled 時才有;state.schema.json roll、docs/design/visual-client.md 第 4 節「橫搖視角」):
//  - 弧形刻度 ±30°(每 5° 刻度、每 10° 標數字),指針指向目前橫搖角 angle_deg(右傾正 → 指針偏向 S 側,即畫面上「船傾向哪一舷」);
//  - 靜傾 staticHeel_deg(迴轉離心 + 風傾的等效靜傾)以獨立的細標記顯示,動態橫搖 = angle_deg − staticHeel_deg 另列數值,靜態與動態分離判讀;
//  - 數值:橫搖角以 P/S 表示(右傾 S、左傾 P)、靜傾、動態、角速度(度/秒)與自然週期;|角度| ≥ warnAbove 以警示色、≥ alarmAbove 以警報色;
//  - 量程外的角度夾在 ±30° 指針極限,數值照實顯示。無 roll(roll 屬性 undefined)時顯示「--」(conning-bar 只在 state 有 roll 時放此方塊)。

import { LitElement, html, svg, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from './shared.js';
import { formatSignedDeg } from '../lib/format.js';
import { clamp } from '../lib/angles.js';
import type { RollState } from '../types/state.js';

/** 橫搖/傾斜角以左右表示:−3.25 → "P 3.3°"、+1.5 → "S 1.5°"、0 → "0.0°";無值 "--"。 */
export function formatHeel(deg: number | undefined | null, digits = 1): string {
  if (typeof deg !== 'number' || !Number.isFinite(deg)) return '--';
  const rounded = Number(deg.toFixed(digits));
  const abs = Math.abs(rounded).toFixed(digits) + '°';
  if (rounded > 0) return `S ${abs}`;
  if (rounded < 0) return `P ${abs}`;
  return abs;
}

const W = 140;
const H = 78;
/** 指針樞軸與弧半徑(viewBox 單位) */
const CX = W / 2;
const CY = 72;
const R = 60;
/** 量程(度) */
export const INCLINOMETER_RANGE_DEG = 30;

function tip(deg: number, radius: number): [number, number] {
  const a = (clamp(deg, -INCLINOMETER_RANGE_DEG, INCLINOMETER_RANGE_DEG) * Math.PI) / 180;
  return [CX + radius * Math.sin(a), CY - radius * Math.cos(a)];
}

@customElement('brm-inclinometer')
export class Inclinometer extends LitElement {
  static override styles = [
    tileStyles,
    css`
      .scale {
        flex: 1;
        min-height: 40px;
      }
      .arc {
        fill: none;
        stroke: var(--ob-scale);
        stroke-width: 1.5;
      }
      .tick {
        stroke: var(--ob-scale);
      }
      .tick.minor {
        stroke: var(--ob-scale-minor);
      }
      .pointer {
        stroke: var(--ob-pointer);
        stroke-width: 3;
        stroke-linecap: round;
      }
      .pointer.warn {
        stroke: var(--ob-warning);
      }
      .pointer.alarm {
        stroke: var(--ob-alarm);
      }
      .static-marker {
        fill: none;
        stroke: var(--ob-order);
        stroke-width: 2;
      }
      .pivot {
        fill: var(--ob-scale);
      }
      .readouts {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 1px 8px;
        font-size: 11px;
      }
      .readouts .value {
        font-size: clamp(12px, 0.9vw, 16px);
      }
      .readouts .value.main {
        font-size: clamp(14px, 1.1vw, 20px);
      }
      .value.warn {
        color: var(--ob-warning);
      }
      .value.alarm {
        color: var(--ob-alarm);
      }
    `,
  ];

  /** 橫搖狀態(state.roll;undefined = 無資料) */
  @property({ attribute: false }) roll: RollState | undefined = undefined;
  /** 警示門檻(度;|橫搖角| ≥ 此值以警示色) */
  @property({ type: Number }) warnAbove = 15;
  /** 警報門檻(度) */
  @property({ type: Number }) alarmAbove = 25;

  override render() {
    const r = this.roll;
    const angle = r?.angle_deg;
    const level = typeof angle !== 'number' ? '' : Math.abs(angle) >= this.alarmAbove ? 'alarm' : Math.abs(angle) >= this.warnAbove ? 'warn' : '';
    const ticks = [];
    for (let d = -INCLINOMETER_RANGE_DEG; d <= INCLINOMETER_RANGE_DEG; d += 5) {
      const major = d % 10 === 0;
      const [x1, y1] = tip(d, R);
      const [x2, y2] = tip(d, R - (major ? 8 : 4));
      ticks.push(svg`<line class="tick ${major ? '' : 'minor'}" x1=${x1} y1=${y1} x2=${x2} y2=${y2} />`);
      if (major) {
        const [tx, ty] = tip(d, R + 8);
        ticks.push(svg`<text x=${tx} y=${ty + 3} font-size="9" text-anchor="middle">${Math.abs(d)}</text>`);
      }
    }
    const [ax, ay] = tip(angle ?? 0, R - 10);
    const [sx, sy] = tip(r?.staticHeel_deg ?? 0, R + 1);
    const [arcL, arcR] = [tip(-INCLINOMETER_RANGE_DEG, R), tip(INCLINOMETER_RANGE_DEG, R)];
    const dynamic = r ? r.angle_deg - r.staticHeel_deg : undefined;
    return html`
      <div class="tile">
        <div class="tile-title"><span>HEEL</span><span class="zh">傾斜儀 ±${INCLINOMETER_RANGE_DEG}°</span></div>
        <div class="scale">
          <svg viewBox="0 0 ${W} ${H}" aria-label="傾斜儀">
            <text x="4" y="12" font-size="11" font-weight="600">P</text>
            <text x=${W - 12} y="12" font-size="11" font-weight="600">S</text>
            <path class="arc" d="M ${arcL[0]} ${arcL[1]} A ${R} ${R} 0 0 1 ${arcR[0]} ${arcR[1]}" />
            ${ticks}
            ${r ? svg`<polygon class="static-marker" points="${sx - 4},${sy - 7} ${sx + 4},${sy - 7} ${sx},${sy}" transform="rotate(${clamp(r.staticHeel_deg, -INCLINOMETER_RANGE_DEG, INCLINOMETER_RANGE_DEG)} ${sx} ${sy})" />` : nothing}
            ${r ? svg`<line class="pointer ${level}" x1=${CX} y1=${CY} x2=${ax} y2=${ay} />` : nothing}
            <circle class="pivot" cx=${CX} cy=${CY} r="3" />
          </svg>
        </div>
        <div class="readouts">
          <span><span class="label">橫搖</span> <span class="value main roll-angle ${level}">${formatHeel(angle)}</span></span>
          <span><span class="label">靜傾</span> <span class="value roll-static" style="color: var(--ob-order)">${formatHeel(r?.staticHeel_deg)}</span></span>
          <span><span class="label">動態</span> <span class="value roll-dynamic">${formatHeel(dynamic)}</span></span>
          <span><span class="label">T / 速率</span> <span class="value roll-period">${r && r.naturalPeriod_s > 0 ? `${r.naturalPeriod_s.toFixed(1)} s` : '--'} ${r ? formatSignedDeg(r.rate_degps) + '/s' : ''}</span></span>
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-inclinometer': Inclinometer;
  }
}
