// 雷達操作面板:<radar-control-panel>(量程、增益/STC/FTC、調諧、待機/發射)、
// <radar-display-panel>(定向、運動、偏心、向量、過去位置、軌跡、餘暉、距離圈、艏向線)、
// <radar-tools-panel>(EBL ×2、VRM ×2、平行索引線 ×2)。

import { html, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { StoreElement, inputNumber } from './store-element.js';
import { panelStyles } from './radar-styles.js';
import { formatRange, type RadarCommand } from './protocol.js';
import { TRAIL_CHOICES, VECTOR_CHOICES, type Afterglow, type CursorTool, type PiLine } from './ppi-model.js';
import { eblReadout, formatBearing, vrmReadout, type BearingRef, type Orientation } from './geometry.js';
import { trailLabel } from './ppi-renderer.js';
import { normalizeHeading } from '../lib/angles.js';

type SliderCmd = 'gain' | 'sea' | 'rain' | 'tune';

@customElement('radar-control-panel')
export class RadarControlPanel extends StoreElement {
  static override styles = panelStyles;
  /** 拖曳中的滑桿值(不被 status 覆寫) */
  @state() private drag: Partial<Record<SliderCmd, number>> = {};
  private throttle = new Map<SliderCmd, { t: number; timer: ReturnType<typeof setTimeout> | null }>();

  private sendSlider(cmd: SliderCmd, value: number, final: boolean): void {
    this.drag = { ...this.drag, [cmd]: value };
    const now = Date.now();
    const th = this.throttle.get(cmd) ?? { t: 0, timer: null };
    const fire = () => {
      th.t = Date.now();
      th.timer = null;
      const v = this.drag[cmd];
      if (v !== undefined) this.store.send({ cmd, value: v } as RadarCommand);
    };
    if (th.timer) clearTimeout(th.timer);
    if (final || now - th.t > 150) fire();
    else th.timer = setTimeout(fire, 150 - (now - th.t));
    this.throttle.set(cmd, th);
    if (final) {
      // 保留到下一個 status 之後,避免滑桿跳回舊值;期間又拖曳則不清除
      setTimeout(() => {
        if (this.drag[cmd] !== value) return;
        const d = { ...this.drag };
        delete d[cmd];
        this.drag = d;
      }, 400);
    }
  }

  private slider(cmd: SliderCmd, label: string, en: string) {
    const st = this.store.status;
    const v = this.drag[cmd] ?? (st ? st[cmd] : 0);
    const disabled = !st || (cmd === 'tune' && st.autoTune);
    return html`<div class="row">
      <label style="width:58px" title=${en}>${label}</label>
      <input
        type="range"
        min="0"
        max="100"
        step="1"
        .value=${String(Math.round(v))}
        ?disabled=${disabled}
        aria-label=${label}
        @input=${(e: Event) => this.sendSlider(cmd, inputNumber(e, v), false)}
        @change=${(e: Event) => this.sendSlider(cmd, inputNumber(e, v), true)}
      />
      <span class="value" style="width:28px;text-align:right">${Math.round(v)}</span>
      ${cmd === 'tune'
        ? html`<button
            class=${st?.autoTune ? 'active' : ''}
            @click=${() => this.store.send(st?.autoTune ? { cmd: 'tune', value: st.tune } : { cmd: 'tune', auto: true })}
          >
            AUTO
          </button>`
        : nothing}
    </div>`;
  }

  override render() {
    const s = this.store;
    const st = s.status;
    const disp = s.settings.displayRangeNm;
    const video = st?.range_nm;
    return html`<div class="panel">
      <h3>RADAR <span class="zh">雷達控制</span></h3>
      <div class="row">
        <label>量程</label>
        <button aria-label="量程減小" @click=${() => s.stepDisplayRange(-1)}>−</button>
        <span class="value" style="min-width:58px;text-align:center;font-size:15px">${formatRange(disp)} NM</span>
        <button aria-label="量程加大" @click=${() => s.stepDisplayRange(1)}>+</button>
        ${video !== undefined && Math.abs(video - disp) > 1e-9
          ? html`<span class="muted" title="偏心/真運動時影像量程加大">影像 ${formatRange(video)}</span>`
          : nothing}
        <span class="muted">${st ? `${st.pulse === 'short' ? 'SP' : st.pulse === 'long' ? 'LP' : 'MP'} ${st.pulseLength_m} m` : ''}</span>
      </div>
      ${this.slider('gain', '增益', 'GAIN')} ${this.slider('sea', '海雜波', 'SEA / STC')}
      ${this.slider('rain', '雨雜波', 'RAIN / FTC')} ${this.slider('tune', '調諧', 'TUNE')}
      <div class="row">
        <button
          class=${st?.standby ? 'active' : ''}
          ?disabled=${!st}
          @click=${() => s.send({ cmd: 'standby' })}
          title="待機會清除所有追蹤目標"
        >
          STBY 待機
        </button>
        <button class=${st && !st.standby ? 'active' : ''} ?disabled=${!st} @click=${() => s.send({ cmd: 'transmit' })}>
          TX 發射
        </button>
        ${st?.failed ? html`<span class="danger">故障</span>` : nothing}
        ${st?.degraded ? html`<span class="warning">性能劣化</span>` : nothing}
        ${st && !st.headingValid ? html`<span class="warning">航向無效</span>` : nothing}
        <span class="muted">${st ? `海況 ${st.seaState}` : ''}</span>
      </div>
    </div>`;
  }
}

@customElement('radar-display-panel')
export class RadarDisplayPanel extends StoreElement {
  static override styles = panelStyles;

  override render() {
    const s = this.store;
    const st = s.settings;
    const set = (p: Parameters<typeof s.setSettings>[0]) => s.setSettings(p);
    const orient = (o: Orientation, label: string) =>
      html`<button class=${st.orientation === o ? 'active' : ''} ?disabled=${!s.headingValid && o !== 'head-up'} @click=${() => set({ orientation: o })}>${label}</button>`;
    const tmPossible = st.orientation !== 'head-up' && s.headingValid;
    const centred = Math.hypot(st.offcentre.x, st.offcentre.y) < 0.01;
    return html`<div class="panel">
      <h3>DISPLAY <span class="zh">顯示</span></h3>
      <div class="row">
        <span class="seg">${orient('head-up', 'H UP')}${orient('north-up', 'N UP')}${orient('course-up', 'C UP')}</span>
        ${st.orientation === 'course-up'
          ? html`<button title="以目前 COG 重設航向向上參考" @click=${() => s.resetCourseUp()}>C UP 重設 ${formatBearing(st.courseRef)}</button>`
          : nothing}
      </div>
      <div class="row">
        <span class="seg">
          <button class=${s.motion() === 'relative' ? 'active' : ''} @click=${() => set({ motion: 'relative' })}>RM 相對</button>
          <button
            class=${s.motion() === 'true' ? 'active' : ''}
            ?disabled=${!tmPossible}
            title=${tmPossible ? '真運動' : '真運動需北向上或航向上且航向有效'}
            @click=${() => set({ motion: 'true' })}
          >
            TM 真
          </button>
        </span>
        <button class=${st.tool === 'offcentre' ? 'active' : ''} @click=${() => set({ tool: st.tool === 'offcentre' ? 'select' : 'offcentre' })}>
          偏心
        </button>
        <button ?disabled=${centred && s.motion() === 'relative'} @click=${() => {
          s.tm = null;
          set({ offcentre: { x: 0, y: 0 } });
        }}>置中</button>
      </div>
      <div class="row">
        <label>向量</label>
        <span class="seg">
          <button class=${st.vectorMode === 'true' ? 'active' : ''} @click=${() => set({ vectorMode: 'true' })}>T 真</button>
          <button class=${st.vectorMode === 'relative' ? 'active' : ''} @click=${() => set({ vectorMode: 'relative' })}>R 相對</button>
        </span>
        <select aria-label="向量時間" @change=${(e: Event) => set({ vectorMin: Number((e.target as HTMLSelectElement).value) })}>
          ${VECTOR_CHOICES.map((m) => html`<option value=${m} ?selected=${m === st.vectorMin}>${m} min</option>`)}
        </select>
        <button class=${st.pastPositions ? 'active' : ''} @click=${() => set({ pastPositions: !st.pastPositions })}>過去位置</button>
      </div>
      <div class="row">
        <label>軌跡</label>
        <select aria-label="回波軌跡" @change=${(e: Event) => set({ trailSec: Number((e.target as HTMLSelectElement).value) })}>
          ${TRAIL_CHOICES.map((v) => html`<option value=${v} ?selected=${v === st.trailSec}>${trailLabel(v)}</option>`)}
        </select>
        <label>餘暉</label>
        <select aria-label="掃描餘暉" @change=${(e: Event) => set({ afterglow: (e.target as HTMLSelectElement).value as Afterglow })}>
          ${(['off', 'short', 'long'] as const).map(
            (v) => html`<option value=${v} ?selected=${v === st.afterglow}>${v === 'off' ? '關' : v === 'short' ? '短' : '長'}</option>`,
          )}
        </select>
        <button class=${st.ringsOn ? 'active' : ''} @click=${() => set({ ringsOn: !st.ringsOn })}>距離圈</button>
        <button
          title="按住暫時隱藏艏向線"
          @pointerdown=${() => set({ headingLineOff: true })}
          @pointerup=${() => set({ headingLineOff: false })}
          @pointerleave=${() => st.headingLineOff && set({ headingLineOff: false })}
        >
          HL OFF
        </button>
      </div>
    </div>`;
  }
}

@customElement('radar-tools-panel')
export class RadarToolsPanel extends StoreElement {
  static override styles = panelStyles;

  private toolBtn(tool: CursorTool, label = '點選') {
    const s = this.store;
    return html`<button
      class=${s.settings.tool === tool ? 'active' : ''}
      title="在 PPI 上點選設定"
      @click=${() => s.setSettings({ tool: s.settings.tool === tool ? 'select' : tool })}
    >
      ${label}
    </button>`;
  }

  private refBtn(ref: BearingRef, onToggle: (r: BearingRef) => void) {
    return html`<button title="真方位 T / 相對艏向 R" @click=${() => onToggle(ref === 'T' ? 'R' : 'T')}>${ref}</button>`;
  }

  override render() {
    const s = this.store;
    const st = s.settings;
    // 畫面參考航向(航向無效時 0,與 PPI 畫線一致)
    const hdg = s.screenHeadingDeg;
    const setEbl = (i: 0 | 1, p: Partial<(typeof st.ebl)[0]>) => {
      const ebl = [...st.ebl] as typeof st.ebl;
      const cur = ebl[i];
      // 切換參考時保持同一條線(換算方位)
      if (p.ref && p.ref !== cur.ref) p.bearing = p.ref === 'T' ? normalizeHeading(cur.bearing + hdg) : normalizeHeading(cur.bearing - hdg);
      ebl[i] = { ...cur, ...p };
      s.setSettings({ ebl });
    };
    const setVrm = (i: 0 | 1, p: Partial<(typeof st.vrm)[0]>) => {
      const vrm = [...st.vrm] as typeof st.vrm;
      vrm[i] = { ...vrm[i], ...p };
      s.setSettings({ vrm });
    };
    const setPi = (i: 0 | 1, p: Partial<PiLine>) => {
      const pi = [...st.pi] as typeof st.pi;
      const cur = pi[i];
      if (p.ref && p.ref !== cur.ref) p.bearing = p.ref === 'T' ? normalizeHeading(cur.bearing + hdg) : normalizeHeading(cur.bearing - hdg);
      pi[i] = { ...cur, ...p };
      s.setSettings({ pi });
    };
    const rows = ([0, 1] as const).map((i) => {
      const e = st.ebl[i];
      const v = st.vrm[i];
      return html`
        <div class="row">
          <button class=${e.on ? 'active' : ''} @click=${() => setEbl(i, { on: !e.on })}>EBL${i + 1}</button>
          <input
            type="number"
            step="0.1"
            min="0"
            max="360"
            aria-label="EBL${i + 1} 方位"
            .value=${e.bearing.toFixed(1)}
            @change=${(ev: Event) => setEbl(i, { on: true, bearing: normalizeHeading(inputNumber(ev, e.bearing)) })}
          />
          ${this.refBtn(e.ref, (r) => setEbl(i, { ref: r }))} ${this.toolBtn(i === 0 ? 'ebl1' : 'ebl2')}
          <span class="value">${e.on ? eblReadout(e, hdg) : ''}</span>
        </div>
        <div class="row">
          <button class=${v.on ? 'active' : ''} @click=${() => setVrm(i, { on: !v.on })}>VRM${i + 1}</button>
          <input
            type="number"
            step="0.01"
            min="0"
            aria-label="VRM${i + 1} 距離"
            .value=${v.rangeNm.toFixed(2)}
            @change=${(ev: Event) => setVrm(i, { on: true, rangeNm: Math.max(0, inputNumber(ev, v.rangeNm)) })}
          />
          <span class="muted">NM</span> ${this.toolBtn(i === 0 ? 'vrm1' : 'vrm2')}
          <span class="value">${v.on ? vrmReadout(v) : ''}</span>
        </div>
      `;
    });
    const piRows = ([0, 1] as const).map((i) => {
      const p = st.pi[i];
      return html`<div class="row">
        <button class=${p.on ? 'active' : ''} @click=${() => setPi(i, { on: !p.on })}>PI${i + 1}</button>
        <input
          type="number"
          step="0.1"
          aria-label="PI${i + 1} 方位"
          .value=${p.bearing.toFixed(1)}
          @change=${(ev: Event) => setPi(i, { on: true, bearing: normalizeHeading(inputNumber(ev, p.bearing)) })}
        />
        ${this.refBtn(p.ref, (r) => setPi(i, { ref: r }))}
        <input
          type="number"
          step="0.05"
          aria-label="PI${i + 1} 垂距"
          title="垂直距離(nm;正 = 右側)"
          .value=${p.offsetNm.toFixed(2)}
          @change=${(ev: Event) => setPi(i, { on: true, offsetNm: inputNumber(ev, p.offsetNm) })}
        />
        <span class="muted">NM</span> ${this.toolBtn(i === 0 ? 'pi1' : 'pi2')}
      </div>`;
    });
    return html`<div class="panel">
      <h3>EBL / VRM / PI <span class="zh">方位線、距離圈、平行索引線</span></h3>
      ${rows} ${piRows}
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'radar-control-panel': RadarControlPanel;
    'radar-display-panel': RadarDisplayPanel;
    'radar-tools-panel': RadarToolsPanel;
  }
}
