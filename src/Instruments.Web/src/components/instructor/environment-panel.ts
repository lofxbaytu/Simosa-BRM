// 環境即時改變(規劃書第 9.1 節):真風速/向、陣風、流向/流速、水深、能見度、浪;分組套用,送 setEnvironment。
// 漸變秒數 ramp_s(設計文件第 3.5 節)附在每次套用上(0 = 立即;浪只對 Hs 漸變)。只有 C# 核心支援的欄位(陣風、能見度、漸變)標示但仍送出。
// 時變流場(state.currentSource = table | tidal;設計文件第 1.5.2 節):標題顯示目前流向/流速與來源,並提示套用流會改為常數、時變停止。
// 浪(docs/design/physics-waves.md「練習中變更」):標題顯示目前 Hs/Tp/來向(state.waves;無浪時「無浪」),Hs/Tp/來向/浪譜「套用」送 setEnvironment { waves },
// 「關閉」送 { waves: null }(ramp_s > 0 時 Hs 先漸變到 0)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import { CORE_SUPPORT_LABEL, ENVIRONMENT_FIELD_SUPPORT, setEnvironmentCommand, type EnvironmentChange } from '../../lib/instructor-command.js';
import { formatWavesSummary } from '../sea-readout.js';
import { CURRENT_SOURCE_LABELS, WAVE_SPECTRA, WAVE_SPECTRUM_LABELS, type OwnShipState, type WaveSpectrum } from '../../types/state.js';

interface EnvForm {
  windSpeed: number;
  windDir: number;
  gust: number;
  curSet: number;
  curDrift: number;
  depth: number;
  visibility: number;
  /** 漸變秒數(0 = 立即) */
  ramp: number;
  /** 浪:有義波高 (m)、譜峰週期 (s)、來向 (度)、浪譜 */
  waveHs: number;
  waveTp: number;
  waveDir: number;
  waveSpectrum: WaveSpectrum;
}

@customElement('brm-environment-panel')
export class EnvironmentPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .group {
        display: grid;
        grid-template-columns: auto 1fr 1fr auto;
        gap: 4px 6px;
        align-items: center;
        font-size: 12px;
      }
      .group label {
        color: var(--ob-text-secondary);
        white-space: nowrap;
      }
      .group input {
        width: 100%;
        min-width: 0;
      }
      .group button {
        min-width: 48px;
      }
      .group .one {
        grid-column: 2 / 4;
      }
      .group .three {
        grid-column: 2 / 4;
        display: grid;
        grid-template-columns: 1fr 1fr 1fr;
        gap: 4px;
      }
      .group .three input,
      .group .three select {
        min-width: 0;
        width: 100%;
      }
      .group .wave-actions {
        display: flex;
        gap: 4px;
      }
    `,
  ];

  @property({ attribute: false }) state: OwnShipState | null = null;

  @state() private form: EnvForm = { windSpeed: 0, windDir: 0, gust: 0, curSet: 0, curDrift: 0, depth: 20, visibility: 10, ramp: 0, waveHs: 1, waveTp: 7, waveDir: 0, waveSpectrum: 'pm' };
  private seeded = false;

  override updated(): void {
    // 第一次收到狀態時把目前環境帶入表單(之後由「帶入目前值」手動更新,避免打字時被覆蓋)
    if (!this.seeded && this.state) {
      this.seeded = true;
      this.fillFromState();
    }
  }

  private fillFromState(): void {
    const s = this.state;
    if (!s) return;
    this.form = {
      ...this.form,
      windSpeed: round1(s.wind.trueSpeed),
      windDir: Math.round(s.wind.trueDir),
      curSet: Math.round(s.current.set),
      curDrift: round1(s.current.drift),
      depth: s.waterDepth !== undefined ? round1(s.waterDepth) : this.form.depth,
      visibility: s.visibility_nm !== undefined ? round1(s.visibility_nm) : this.form.visibility,
      waveHs: s.waves ? round1(s.waves.significantHeight_m) : this.form.waveHs,
      waveTp: s.waves ? round1(s.waves.peakPeriod_s) : this.form.waveTp,
      waveDir: s.waves ? Math.round(s.waves.direction_deg) : this.form.waveDir,
      waveSpectrum: s.waves?.spectrum ?? this.form.waveSpectrum,
    };
  }

  private setSpectrum(ev: Event): void {
    const v = (ev.target as HTMLSelectElement).value;
    if ((WAVE_SPECTRA as readonly string[]).includes(v)) this.form = { ...this.form, waveSpectrum: v as WaveSpectrum };
  }

  private set<K extends keyof EnvForm>(key: K, ev: Event): void {
    const v = numberFromInput(ev.target);
    if (v !== undefined) this.form = { ...this.form, [key]: v };
  }

  private apply(change: EnvironmentChange): void {
    const cmd = setEnvironmentCommand(this.form.ramp > 0 ? { ...change, ramp_s: this.form.ramp } : change);
    if (cmd) dispatchCommand(this, cmd);
  }

  private applyWind = (): void => this.apply({ wind: { trueSpeed: this.form.windSpeed, trueDir: this.form.windDir, gustiness: this.form.gust } });
  private applyCurrent = (): void => this.apply({ current: { set: this.form.curSet, drift: this.form.curDrift } });
  private applyDepth = (): void => this.apply({ waterDepth: this.form.depth });
  private applyVisibility = (): void => this.apply({ visibility_nm: this.form.visibility });
  private applyWaves = (): void =>
    this.apply({ waves: { significantHeight_m: this.form.waveHs, peakPeriod_s: this.form.waveTp, direction_deg: this.form.waveDir, spectrum: this.form.waveSpectrum } });
  private clearWaves = (): void => this.apply({ waves: null });
  private applyAll = (): void =>
    this.apply({
      wind: { trueSpeed: this.form.windSpeed, trueDir: this.form.windDir, gustiness: this.form.gust },
      current: { set: this.form.curSet, drift: this.form.curDrift },
      waterDepth: this.form.depth,
      visibility_nm: this.form.visibility,
    });

  private tag(field: keyof typeof ENVIRONMENT_FIELD_SUPPORT) {
    const sup = ENVIRONMENT_FIELD_SUPPORT[field];
    const label = CORE_SUPPORT_LABEL[sup];
    return label ? html`<span class="tag unsupported">${label}</span>` : '';
  }

  override render() {
    const f = this.form;
    const s = this.state;
    return html`
      <div class="panel">
        <div class="panel-title">
          <span>環境 ENVIRONMENT</span>
          <span class="zh">${s ? `現在 風 ${round1(s.wind.trueSpeed)} kn/${Math.round(s.wind.trueDir)}° · 流 ${Math.round(s.current.set)}°/${round1(s.current.drift)} kn${s.currentSource ? `(${CURRENT_SOURCE_LABELS[s.currentSource]})` : ''} · 水深 ${s.waterDepth !== undefined ? round1(s.waterDepth) : '--'} m${s.tideHeight_m !== undefined ? `(潮高 ${s.tideHeight_m >= 0 ? '+' : ''}${round1(s.tideHeight_m)} m)` : ''}${s.visibility_nm !== undefined ? ` · 能見度 ${round1(s.visibility_nm)} nm` : ''} · 浪 ${s.waves ? formatWavesSummary(s.waves) : '無'}` : '等待狀態'}</span>
        </div>
        ${s?.currentSource
          ? html`<div class="hint current-source">時變流場(${CURRENT_SOURCE_LABELS[s.currentSource]})執行中:流向/流速由核心依練習時間更新;套用「流」會改為常數並停止時變,直到再載入情境。</div>`
          : nothing}
        <div class="group">
          <label>真風 kn / 來向°${this.tag('wind')}</label>
          <input class="wind-speed" type="number" min="0" max="100" step="1" .value=${String(f.windSpeed)} @input=${(e: Event) => this.set('windSpeed', e)} />
          <input class="wind-dir" type="number" min="0" max="360" step="5" .value=${String(f.windDir)} @input=${(e: Event) => this.set('windDir', e)} />
          <button class="apply-wind" @click=${this.applyWind}>套用</button>

          <label>陣風 0–1${this.tag('gustiness')}</label>
          <input class="gust one" type="number" min="0" max="1" step="0.05" .value=${String(f.gust)} @input=${(e: Event) => this.set('gust', e)} />
          <span class="hint">隨風套用</span>

          <label>流 去向° / kn${this.tag('current')}</label>
          <input class="cur-set" type="number" min="0" max="360" step="5" .value=${String(f.curSet)} @input=${(e: Event) => this.set('curSet', e)} />
          <input class="cur-drift" type="number" min="0" max="10" step="0.1" .value=${String(f.curDrift)} @input=${(e: Event) => this.set('curDrift', e)} />
          <button class="apply-current" @click=${this.applyCurrent}>套用</button>

          <label>水深 m${this.tag('waterDepth')}</label>
          <input class="depth one" type="number" min="1" max="999" step="0.5" .value=${String(f.depth)} @input=${(e: Event) => this.set('depth', e)} />
          <button class="apply-depth" @click=${this.applyDepth}>套用</button>

          <label>能見度 nm${this.tag('visibility')}</label>
          <input class="visibility one" type="number" min="0" max="30" step="0.1" .value=${String(f.visibility)} @input=${(e: Event) => this.set('visibility', e)} />
          <button class="apply-visibility" @click=${this.applyVisibility}>套用</button>

          <label title="有義波高 m / 譜峰週期 s / 來向°(氣象慣例);套用為建立或更新,關閉送 waves: null">浪 Hs m / Tp s / 來向°${this.tag('waves')}</label>
          <div class="three">
            <input class="wave-hs" type="number" min="0" max="15" step="0.1" .value=${String(f.waveHs)} @input=${(e: Event) => this.set('waveHs', e)} />
            <input class="wave-tp" type="number" min="1" max="25" step="0.5" .value=${String(f.waveTp)} @input=${(e: Event) => this.set('waveTp', e)} />
            <input class="wave-dir" type="number" min="0" max="360" step="5" .value=${String(f.waveDir)} @input=${(e: Event) => this.set('waveDir', e)} />
          </div>
          <div class="wave-actions">
            <button class="apply-waves" @click=${this.applyWaves}>套用</button>
            <button class="clear-waves" @click=${this.clearWaves} ?disabled=${!s?.waves} title="關閉浪(state.waves 省略;漸變秒數 > 0 時 Hs 先漸變到 0)">關閉</button>
          </div>

          <label>浪譜</label>
          <select class="wave-spectrum one" .value=${f.waveSpectrum} @change=${this.setSpectrum}>
            ${WAVE_SPECTRA.map((sp) => html`<option value=${sp} ?selected=${sp === f.waveSpectrum}>${WAVE_SPECTRUM_LABELS[sp]}</option>`)}
          </select>
          <span class="hint">${s?.waves ? `現在 ${formatWavesSummary(s.waves)}` : '現在無浪'}</span>

          <label>漸變 s${this.tag('ramp')}</label>
          <input class="ramp one" type="number" min="0" max="3600" step="10" .value=${String(f.ramp)} @input=${(e: Event) => this.set('ramp', e)} />
          <span class="hint">${f.ramp > 0 ? `套用時 ${f.ramp} s 線性漸變(浪只對 Hs)` : '0 = 立即'}</span>
        </div>
        <div class="btn-row">
          <button @click=${this.fillFromState} ?disabled=${!s}>帶入目前值</button>
          <button class="primary apply-all" @click=${this.applyAll}>全部套用</button>
        </div>
      </div>
    `;
  }
}

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-environment-panel': EnvironmentPanel;
  }
}
