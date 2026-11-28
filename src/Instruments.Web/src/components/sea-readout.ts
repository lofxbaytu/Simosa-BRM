// 浪與船間交互作用方塊(操船台環境區;docs/design/physics-waves.md 第 5 節、physics-ship-interaction.md 第 5 節):
//  - state.waves:Hs / Tp / 來向(艏向上的羅盤箭頭,由浪的來向指向中心,與風方塊相同慣例)與浪譜;
//  - state.shipInteraction:啟用時顯示主要目標與側向力 / 艏搖力矩(t、t·m),超過事件門檻(event)以警示色標示;
//  - state.waveMotion(浪一階運動;情境 ship.seakeeping.enabled 且有浪時才有,需求 R2):在浪的數值下加一列「縱搖 ±x.x°」「升沉 ±x.xx m」
//    (縱搖艏仰為正、升沉向上為正;方塊夠寬時同一行、窄時折成兩行);無此欄位時方塊與原本完全相同;
// 三者皆無時 conning-bar 不放此方塊。練習中以 setEnvironment waves 關閉浪後 state.waves 省略 → 顯示「無浪」。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { conningTileStyles, tileStyles } from './shared.js';
import { formatBearing, formatSignedDeg } from '../lib/format.js';
import { WAVE_SPECTRUM_LABELS, type ShipInteractionState, type WaveMotionState, type WaveState } from '../types/state.js';

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

/** 帶正負號的公尺數(升沉:向上為正;不含單位):0.347 → "+0.35"、−0.3 → "−0.30"(Unicode 減號,與 formatSignedDeg 一致)、0 → "0.00";無值 "--"。 */
export function formatSignedMetres(v: number | undefined | null, digits = 2): string {
  if (typeof v !== 'number' || !Number.isFinite(v)) return '--';
  const r = Number(v.toFixed(digits));
  const sign = r < 0 ? '−' : r > 0 ? '+' : '';
  return sign + Math.abs(r).toFixed(digits);
}

/** 浪一階運動的一行摘要(SEA 方塊該列的提示文字):「縱搖 +1.2° / 升沉 −0.35 m」(縱搖艏仰為正、升沉向上為正)。 */
export function formatWaveMotionSummary(m: Pick<WaveMotionState, 'pitch_deg' | 'heave_m'>): string {
  return `縱搖 ${formatSignedDeg(m.pitch_deg)} / 升沉 ${formatSignedMetres(m.heave_m)} m`;
}

@customElement('brm-sea')
export class SeaReadout extends LitElement {
  static override styles = [
    tileStyles,
    conningTileStyles,
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
        font-size: clamp(11px, min(0.95vw, 8cqi), 18px);
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
      .interaction .value,
      .motion .value {
        font-size: clamp(11px, min(0.85vw, 7.5cqi), 16px);
      }
      /* 窄方塊(conning 條單列 12–13 格,1920 寬每格約 140–180 px):浪向玫瑰縮小並只佔第一列(Hs / Tp)的左側,
         其餘各列(來向 / 相對、縱搖 / 升沉)用整個方塊寬度;交互作用列的標籤與數值放不下時折成兩行 */
      @container (max-width: 215px) {
        .body {
          display: grid;
          grid-template-columns: auto minmax(0, 1fr);
          column-gap: 6px;
          row-gap: 2px;
          align-content: center;
          align-items: center;
        }
        .rows {
          display: contents;
        }
        .rose {
          width: 28px;
          height: 28px;
          grid-column: 1;
          grid-row: 1;
        }
        .rows > :first-child {
          grid-column: 2;
          grid-row: 1;
        }
        .rows > :not(:first-child) {
          grid-column: 1 / -1;
        }
        .interaction .row {
          flex-wrap: wrap;
          row-gap: 1px;
        }
      }
      /* 更窄(165 px 以下):不顯示浪向玫瑰,相對來向仍有數值。只有兩列(視窗 ≤ 1900 px)用這個門檻;
         conning 條單列一律顯示(本樣式末的樣式查詢區塊) */
      @container (max-width: 165px) {
        .rose {
          display: none;
        }
      }
      /* 縱搖/升沉:兩組「標籤 + 值」;方塊夠寬時同一行,不夠寬時(1920 單列版面每格約 165–190 px)自動折成兩行,不被方塊裁切 */
      .motion {
        flex-wrap: wrap;
        column-gap: 10px;
        row-gap: 2px;
      }
      .motion .pair {
        flex: 1 1 auto;
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 6px;
        white-space: nowrap;
      }
      /* conning 條單列:
         - 交互作用列的標籤與數值放不下一行時數值換到下一行(任何寬度;原本只有 215 px 以下的方塊才換行,
           2560 寬的單列(方塊約 220 至 250 px)長數值被方塊邊界裁切);數值的兩段(側向力 Y、艏搖力矩 N,樣板的 .nb)各自不拆開,
           整段比方塊寬時在兩段之間換行;
         - 交互作用列的標籤(「交互作用」、目標船代號、「超限」三段,樣板的 .nb)比方塊寬時在段與段之間換行,代號本身比方塊寬時才在字間換行
           (原本標籤不換行:13 格、代號 10 個字元以上時「超限」被方塊邊界切掉 6 至 15 px;情境的目標船代號沒有長度上限) */
      @container style(--brm-conning-single: 1) {
        .interaction .row {
          flex-wrap: wrap;
          row-gap: 1px;
        }
        .interaction .value {
          white-space: normal;
          text-align: right;
        }
        .interaction .label {
          white-space: normal;
          min-width: 0;
        }
        .interaction .label .nb {
          max-width: 100%;
          overflow-wrap: anywhere;
        }
      }
      /* conning 條單列的浪向玫瑰:一律顯示(蓋過上面 165 px 的門檻)。單列的格寬第三十一輪重排之後,12 格的浪方塊在 1920×1080 是
         164 至 166 px(原本約 179 px),沿用 165 px 的門檻會讓 12 格(情境庫的 E17b)少掉原本有的玫瑰圖。單列最窄的方塊
         (13 格、視窗 1901 px,150 px)也放得下:玫瑰圖旁的「Hs / Tp」一列放不下一行時數值換到標籤的下一行(conningTileStyles 的 .row);
         第三十一輪之前 13 格在視窗 1922 px 以下不顯示玫瑰圖,現在也顯示 */
      @container style(--brm-conning-single: 1) {
        .rose {
          display: block;
        }
      }
      /* conning 條兩列(方塊約 107 至 135 px 高;兩列時浪方塊至少約 200 px 寬,conning-bar.ts 的第二列比例):
         - 各列的行高固定 13 px;交互作用列:方塊窄於 290 px(「交互作用 ANCH1 超限」與「Y -123.4 t N -1234 t·m」一行放不下)時數值固定在
           標籤的下一行(靠右),不隨目標船代號的長度、有沒有「超限」與數值的位數在一行與兩行之間跳動;290 px 以上放得下時同一行
           (原本 215 px 以上不換行,1093 至 1152 寬時長數值被方塊右緣切掉)。側向力與艏搖力矩(樣板的 .nb)各自不拆開;
         - 方塊窄於 236 px:浪向玫瑰 28 px,佔前兩列(Hs / Tp、來向 / 相對)的左側,縱搖 / 升沉用整個方塊寬度(一行)。
           原本 215 px 以下玫瑰只佔第一列,加上交互作用的兩行之後比 107 px 高的方塊高;216 至 235 px 並排時縱搖 / 升沉折成兩行,也放不下;
         - 236 px 以上:玫瑰 40 px 在左,三列數值在右(原本的排法);
         - 數值的字級另以方塊高度設上限(13cqb / 12cqb:方塊 107 px 高時 13.9 / 12.8 px;只在視窗寬而矮時起作用 ——
           字級照視窗寬度到 18 px 時三列數值加交互作用(目標船代號很長時標籤再多一行)比 107 px 高的方塊高、往上擠到標題列;
           1366×768 與 1536×864 不受影響,1536×730 時小約 5 %) */
      @container style(--brm-conning-single: 0) {
        .rows {
          line-height: 13px;
        }
        .rows .value {
          font-size: clamp(11px, min(0.95vw, 8cqi, 13cqb), 18px);
        }
        .interaction .value,
        .motion .value {
          font-size: clamp(11px, min(0.85vw, 7.5cqi, 12cqb), 16px);
        }
        .rows .label,
        .interaction .label {
          line-height: 13px;
        }
        .motion {
          row-gap: 0;
        }
        .interaction {
          line-height: 13px;
        }
        .interaction .row {
          flex-wrap: wrap;
          row-gap: 0;
        }
        .interaction .value {
          margin-left: auto;
          white-space: normal;
          text-align: right;
        }
        /* 標籤(「交互作用」、目標船代號、「超限」三段,樣板的 .nb)比方塊寬時在段與段之間換行,代號本身比方塊寬時才在字間換行
           (情境的目標船代號沒有長度上限;情境庫最長 5 個字元,一行放得下) */
        .interaction .label {
          white-space: normal;
          min-width: 0;
        }
        .interaction .label .nb {
          max-width: 100%;
          overflow-wrap: anywhere;
        }
      }
      @container style(--brm-conning-single: 0) and (max-width: 289px) {
        .interaction .value {
          flex-basis: 100%;
        }
      }
      @container style(--brm-conning-single: 0) and (max-width: 235px) {
        .body {
          display: grid;
          grid-template-columns: auto minmax(0, 1fr);
          column-gap: 6px;
          row-gap: 2px;
          align-content: center;
          align-items: center;
        }
        .rows {
          display: contents;
        }
        .rose {
          width: 28px;
          height: 28px;
          grid-column: 1;
          grid-row: 1 / span 2;
        }
        .rows > :first-child {
          grid-column: 2;
          grid-row: 1;
        }
        .rows > :nth-child(2) {
          grid-column: 2;
          grid-row: 2;
        }
        .rows > :nth-child(n + 3) {
          grid-column: 1 / -1;
        }
      }
    `,
  ];

  @property({ attribute: false }) waves: WaveState | undefined = undefined;
  @property({ attribute: false }) interaction: ShipInteractionState | undefined = undefined;
  /** 浪一階運動(state.waveMotion;undefined = 情境未啟用或無浪 → 不顯示該行) */
  @property({ attribute: false }) motion: WaveMotionState | undefined = undefined;
  @property({ type: Number }) heading = 0;

  override render() {
    const w = this.waves;
    const si = this.interaction;
    const m = this.motion;
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
                  ${this.renderMotion(m)}
                </div>`
            : html`<div class="rows"><div class="label muted no-waves">無浪</div>${this.renderMotion(m)}</div>`}
        </div>
        ${si
          ? html`<div class="interaction ${si.event ? 'event' : ''}" title="船間交互作用:側向力(右舷正)/ 艏搖力矩(艏向右正);超過門檻以警示色">
              <div class="row">
                <span class="label"><span class="nb">交互作用</span>${si.active && si.targetId ? html` <span class="nb">${si.targetId}</span>` : nothing}${si.event ? html` <span class="nb">超限</span>` : nothing}</span>
                <span class="value interaction-force">${si.active ? html`<span class="nb">Y ${formatSignedTonnes(si.forceSway_t)}<span class="unit">t</span></span> <span class="nb">N ${formatSignedTonnes(si.momentYaw_tm, 0)}<span class="unit">t·m</span></span>` : '無目標'}</span>
              </div>
            </div>`
          : nothing}
      </div>
    `;
  }

  /** 浪一階運動一列(縱搖、升沉兩組;無 waveMotion 時不輸出任何節點,版面與原本相同)。 */
  private renderMotion(m: WaveMotionState | undefined) {
    if (!m) return nothing;
    return html`<div class="row motion" title="浪一階運動(state.waveMotion):縱搖艏仰為正、升沉向上為正;${formatWaveMotionSummary(m)}">
      <span class="pair"><span class="label">縱搖</span><span class="value small wave-pitch">${formatSignedDeg(m.pitch_deg)}</span></span>
      <span class="pair"><span class="label">升沉</span><span class="value small wave-heave">${formatSignedMetres(m.heave_m)}<span class="unit">m</span></span></span>
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-sea': SeaReadout;
  }
}
